import {
  CASH_MOVEMENT_UPLOAD_MAXIMUM_BATCH_SIZE,
  type CashMovementUploadRepositoryPort,
  type CashMovementUploadScope,
  type ClaimedCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement-upload";

import {
  CashMovementSyncMappingError,
  buildCashMovementSyncRequest,
  fitCashMovementBatch,
  mapCashMovementToSyncItem,
  type CashMovementSyncClientKind,
  type CashMovementSyncItem,
} from "./cash-movement-sync-request";
import {
  DAILY_CLOSE_UNAUTHORIZED_COOLDOWN_SECONDS,
  dailyCloseUploadRetryDelaySeconds,
} from "./daily-close-upload-service";
import type { CashMovementSyncPort } from "./hbpos-cash-movement-sync-adapter";
import type {
  CashMovementUploadDrainPort,
  DailyCloseUploadDrainControl,
  DailyCloseUploadDrainResult,
} from "./sync-coordinator";

/** 每轮认领的条数上限（也是单个请求的条数上限，服务端 MaximumBatchSize = 100）。 */
const DEFAULT_BATCH_SIZE = CASH_MOVEMENT_UPLOAD_MAXIMUM_BATCH_SIZE;
/** 防御性上限：一次 drain 最多发这么多个请求，避免仓储异常时无限循环。 */
const MAXIMUM_REQUESTS_PER_DRAIN = 1_000;
const MAXIMUM_ERROR_MESSAGE_LENGTH = 512;

/** 401/403 后的短冷却：与日结共用 60 秒，避免调度器 0ms 自旋。 */
export const CASH_MOVEMENT_UNAUTHORIZED_COOLDOWN_SECONDS =
  DAILY_CLOSE_UNAUTHORIZED_COOLDOWN_SECONDS;

/** 与日结上传同一形状的 drain 端口 / 结果（协调器按同一接口驱动；skipped 恒为 0）。 */
export type CashMovementUploadDrainResult = DailyCloseUploadDrainResult;
export type CashMovementUploadDrainControl = DailyCloseUploadDrainControl;

export type CashMovementUploadServiceOptions = Readonly<{
  repository: CashMovementUploadRepositoryPort;
  sync: CashMovementSyncPort;
  /** 每轮重新读取当前设备授权范围：没有授权时不上传，也不消耗尝试次数。 */
  scope(): CashMovementUploadScope | null;
  clientKind: CashMovementSyncClientKind;
  appVersion?: string | null | undefined;
  now(): Date;
  batchSize?: number;
  /** 仅测试用：覆盖单请求字节上限（默认 256KB）。 */
  maximumRequestBytes?: number;
}>;

/**
 * 现金进出上传：把本机尚未同步的现金进出逐批上传到服务端（语义对齐日结上传 / WPF）。
 * 关键约束：
 * - 按 occurred_at 升序上传（冲正必须在原记录之后到达服务端）；
 * - 一个请求 <= 100 条且 <= 256KB，超出部分退还租约（不消耗尝试次数）留给下一轮；
 * - Accepted / AlreadySynced 都算成功；单条 Rejected 按错误码分重试类与永久类；
 * - 401/403：释放租约、不消耗尝试次数，并中断本批；整批 400：本批全部 rejected；
 * - 与开关无关：关闭 cashMovement.enabled 后已有记录照常上传。
 * 由 PosSyncCoordinator 在每次 drain 里、日结上传之前调用，共享它的单飞、更新切换互斥、关闭感知与定时唤醒。
 */
export class CashMovementUploadService implements CashMovementUploadDrainPort {
  private readonly batchSize: number;

  public constructor(private readonly options: CashMovementUploadServiceOptions) {
    this.batchSize = Math.min(
      Math.max(Math.trunc(options.batchSize ?? DEFAULT_BATCH_SIZE), 1),
      CASH_MOVEMENT_UPLOAD_MAXIMUM_BATCH_SIZE,
    );
  }

  public async drain(
    control: CashMovementUploadDrainControl = {},
  ): Promise<CashMovementUploadDrainResult> {
    const totals = { attempted: 0, uploaded: 0, rejected: 0, skipped: 0, deferred: 0 };
    const finish = (interrupted: boolean): CashMovementUploadDrainResult =>
      Object.freeze({ ...totals, interrupted });
    const repository = this.options.repository;

    const startedAt = this.options.now();
    await repository.recoverExpiredUploading({
      nowIso: startedAt.toISOString(),
      nextAttemptAtIso: startedAt.toISOString(),
    });

    for (let round = 0; round < MAXIMUM_REQUESTS_PER_DRAIN; round += 1) {
      if (control.shouldStop?.()) return finish(false);
      const scope = this.options.scope();
      if (
        !scope ||
        scope.storeCode.trim().length === 0 ||
        scope.deviceCode.trim().length === 0
      ) {
        return finish(false);
      }
      const claim = await repository.claimDueBatch(
        scope,
        this.batchSize,
        this.options.now().toISOString(),
      );
      totals.rejected += claim.rejectedLocally;
      if (claim.claimed.length === 0) {
        // 冲正因原记录被拒而本地 rejected 后，后面可能还有可传的记录：再取一轮；取空才结束。
        if (claim.rejectedLocally > 0) continue;
        return finish(false);
      }
      let outcome: BatchOutcome;
      try {
        outcome = await this.uploadBatch(claim.claimed, scope, totals);
      } catch {
        // 上传异常必须保持在后台重试，不得影响收银流程：整批带退避回 pending，并结束本轮。
        await this.bestEffortRetry(claim.claimed, "UPLOAD_EXCEPTION", "Cash movement upload failed unexpectedly.");
        totals.attempted += claim.claimed.length;
        totals.deferred += claim.claimed.length;
        return finish(false);
      }
      if (outcome === "interrupted") return finish(true);
      // 整批退避（网络 / 5xx / 429 …）：后面的记录同样会失败，本轮到此为止，等定时唤醒。
      if (outcome === "stop") return finish(false);
    }
    return finish(false);
  }

  public async nextReadyAtIso(): Promise<string | null> {
    const scope = this.options.scope();
    if (!scope) return null;
    return this.options.repository.nextReadyAtIso(scope);
  }

  private async uploadBatch(
    claimed: readonly ClaimedCashMovement[],
    scope: CashMovementUploadScope,
    totals: { attempted: number; uploaded: number; rejected: number; skipped: number; deferred: number },
  ): Promise<BatchOutcome> {
    const repository = this.options.repository;
    // 映射：本地数据本身无法映射的记录永久拒绝且不发请求。
    const sendable: { claimed: ClaimedCashMovement; item: CashMovementSyncItem }[] = [];
    for (const entry of claimed) {
      try {
        sendable.push({ claimed: entry, item: mapCashMovementToSyncItem(entry.movement) });
      } catch (error) {
        if (!(error instanceof CashMovementSyncMappingError)) throw error;
        await repository.markRejected(
          entry.movement.movementGuid,
          error.code,
          trimMessage(error.message),
        );
        totals.attempted += 1;
        totals.rejected += 1;
      }
    }
    if (sendable.length === 0) return "continue";

    // 一个请求 <= 100 条且 <= 256KB：取最长的前缀，多出来的退还租约（不算一次尝试），下一轮再认领。
    const requestOptions = {
      storeCode: scope.storeCode,
      deviceCode: scope.deviceCode,
      clientKind: this.options.clientKind,
      appVersion: this.options.appVersion,
    } as const;
    const fitCount = fitCashMovementBatch(
      sendable.map((entry) => entry.item),
      requestOptions,
      this.options.maximumRequestBytes === undefined
        ? {}
        : { maximumBytes: this.options.maximumRequestBytes },
    );
    const overflow = sendable.slice(fitCount);
    if (overflow.length > 0) {
      await repository.releaseWithoutAttempt(
        overflow.map((entry) => entry.claimed.movement.movementGuid),
        this.options.now().toISOString(),
        "BATCH_SPLIT",
        "The upload was split into smaller requests.",
      );
    }
    const batch = sendable.slice(0, fitCount);
    const request = buildCashMovementSyncRequest(
      batch.map((entry) => entry.item),
      requestOptions,
    );

    const result = await this.options.sync.sync(request);
    totals.attempted += batch.length;
    switch (result.kind) {
      case "unauthorized": {
        // 设备授权问题不是这些记录的错：释放租约、撤销尝试计数，并中断整个批次。
        await repository.releaseWithoutAttempt(
          batch.map((entry) => entry.claimed.movement.movementGuid),
          new Date(
            this.options.now().getTime() +
              CASH_MOVEMENT_UNAUTHORIZED_COOLDOWN_SECONDS * 1_000,
          ).toISOString(),
          result.code,
          trimMessage(result.message),
        );
        totals.deferred += batch.length;
        return "interrupted";
      }
      case "rejected": {
        // 整批请求被判定无效：本批全部永久拒绝。
        for (const entry of batch) {
          await repository.markRejected(
            entry.claimed.movement.movementGuid,
            result.code,
            trimMessage(result.message),
          );
        }
        totals.rejected += batch.length;
        return "continue";
      }
      case "retry": {
        for (const entry of batch) {
          await this.markRetry(entry.claimed, result.code, result.message);
        }
        totals.deferred += batch.length;
        return "stop";
      }
      case "results": {
        const byGuid = new Map(
          result.items.map((item) => [item.movementGuid, item] as const),
        );
        const syncedGuids: string[] = [];
        for (const entry of batch) {
          const guid = entry.claimed.movement.movementGuid;
          const item = byGuid.get(guid.toLowerCase());
          if (item?.outcome === "synced") {
            syncedGuids.push(guid);
          } else if (item?.outcome === "rejected") {
            await repository.markRejected(
              guid,
              item.code ?? "REJECTED",
              trimMessage(item.message ?? "The server rejected the cash movement."),
            );
            totals.rejected += 1;
          } else if (item) {
            await this.markRetry(
              entry.claimed,
              item.code ?? "SYNC_RETRY",
              item.message ?? "The server asked to retry the cash movement.",
            );
            totals.deferred += 1;
          } else {
            // 响应里缺这条记录：不可信，保守重试，不能当成功也不能永久拒绝。
            await this.markRetry(
              entry.claimed,
              "SYNC_RESULT_MISSING",
              "The server response did not include this cash movement.",
            );
            totals.deferred += 1;
          }
        }
        if (syncedGuids.length > 0) {
          await repository.markSynced(syncedGuids, this.options.now().toISOString());
          totals.uploaded += syncedGuids.length;
        }
        return "continue";
      }
    }
  }

  private markRetry(
    entry: ClaimedCashMovement,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    return this.options.repository.markRetry(
      entry.movement.movementGuid,
      new Date(
        this.options.now().getTime() +
          dailyCloseUploadRetryDelaySeconds(entry.attemptCount) * 1_000,
      ).toISOString(),
      errorCode,
      trimMessage(errorMessage),
    );
  }

  private async bestEffortRetry(
    claimed: readonly ClaimedCashMovement[],
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    for (const entry of claimed) {
      try {
        await this.markRetry(entry, errorCode, errorMessage);
      } catch {
        // 仓储本身故障时放弃；租约到期后会被 recoverExpiredUploading 回收。
      }
    }
  }
}

type BatchOutcome = "continue" | "stop" | "interrupted";

function trimMessage(message: string): string {
  const text = message.trim();
  if (text.length === 0) return "Cash movement sync failed.";
  return text.length <= MAXIMUM_ERROR_MESSAGE_LENGTH
    ? text
    : text.slice(0, MAXIMUM_ERROR_MESSAGE_LENGTH);
}
