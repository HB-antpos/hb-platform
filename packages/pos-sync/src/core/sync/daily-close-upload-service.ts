import {
  DAILY_CLOSE_UPLOAD_LEASE_SECONDS,
  isDailyCloseUploadGuid,
  type DailyCloseUploadLease,
  type DailyCloseUploadRepositoryPort,
  type DailyCloseUploadScope,
} from "@hb/pos-domain/core/contracts/daily-close-upload";

import {
  DailyCloseSyncMappingError,
  mapDailyCloseArchiveToSyncRequest,
  type DailyCloseSyncClientKind,
} from "./daily-close-sync-request";
import type { DailyCloseSyncPort } from "./hbpos-daily-close-sync-adapter";
import type {
  DailyCloseUploadDrainControl,
  DailyCloseUploadDrainPort,
  DailyCloseUploadDrainResult,
} from "./sync-coordinator";

/** 每轮批次条数（与 WPF 一致）；循环取空直到没有到期待传的日结。 */
const DEFAULT_BATCH_SIZE = 20;
/** 401/403 后的短冷却：保持 pending 但不能立刻再取到，否则调度器会据此 0ms 自旋。 */
export const DAILY_CLOSE_UNAUTHORIZED_COOLDOWN_SECONDS = 60;
const MAXIMUM_ERROR_MESSAGE_LENGTH = 512;

export type DailyCloseUploadServiceOptions = Readonly<{
  repository: DailyCloseUploadRepositoryPort;
  sync: DailyCloseSyncPort;
  /** 每轮重新读取当前设备授权范围：没有授权时不上传，也不消耗尝试次数。 */
  scope(): DailyCloseUploadScope | null;
  clientKind: DailyCloseSyncClientKind;
  appVersion?: string | null | undefined;
  now(): Date;
  batchSize?: number;
}>;

type ItemOutcome =
  | "none"
  | "uploaded"
  | "rejected"
  | "skipped"
  | "deferred"
  | "interrupted";

/**
 * 日结记录上传：把本机尚未同步的日结存档逐条上传到服务端（语义与 WPF DailyCloseUploadService 一致）。
 * 新保存的日结与升级后的全部历史日结都是 pending，由同一条路径补传；历史行含完整面额与支付方式，
 * 会覆盖服务端按审计事件回填的占位记录。由 PosSyncCoordinator 在每次 drain 末尾调用，
 * 因此共享它的单飞、更新切换互斥、关闭感知与定时唤醒。
 */
export class DailyCloseUploadService implements DailyCloseUploadDrainPort {
  private readonly batchSize: number;

  public constructor(private readonly options: DailyCloseUploadServiceOptions) {
    this.batchSize = Math.min(
      Math.max(Math.trunc(options.batchSize ?? DEFAULT_BATCH_SIZE), 1),
      100,
    );
  }

  public async drain(
    control: DailyCloseUploadDrainControl = {},
  ): Promise<DailyCloseUploadDrainResult> {
    const totals = {
      attempted: 0,
      uploaded: 0,
      rejected: 0,
      skipped: 0,
      deferred: 0,
    };
    const finish = (interrupted: boolean): DailyCloseUploadDrainResult =>
      Object.freeze({ ...totals, interrupted });

    const startedAt = this.options.now();
    await this.options.repository.recoverExpiredUploading({
      staleBeforeIso: new Date(
        startedAt.getTime() - DAILY_CLOSE_UPLOAD_LEASE_SECONDS * 1_000,
      ).toISOString(),
      nextAttemptAtIso: startedAt.toISOString(),
    });

    // 本次执行里已经处理过的记录：失败的记录会带退避时间离开到期队列，这里再兜底防止
    // 认领阶段异常（例如 SQLite 瞬时锁）让同一条记录反复出现在“取到期”结果里造成死循环。
    const handled = new Set<string>();
    while (!control.shouldStop?.()) {
      const scope = this.options.scope();
      if (
        !scope ||
        scope.storeCode.trim().length === 0 ||
        scope.deviceCode.trim().length === 0
      ) {
        break;
      }
      const due = await this.options.repository.listDue(
        scope,
        this.batchSize,
        this.options.now().toISOString(),
      );
      const batch = due.filter((closeId) => {
        if (handled.has(closeId)) return false;
        handled.add(closeId);
        return true;
      });
      // 取空（或剩下的都已处理过）即本轮结束。
      if (batch.length === 0) break;

      for (const closeId of batch) {
        if (control.shouldStop?.()) return finish(false);
        // 每条记录独立处理：一条出异常只计入“延后”，不影响其余记录。
        let outcome: ItemOutcome;
        try {
          outcome = await this.uploadOne(closeId, scope);
        } catch {
          totals.deferred += 1;
          continue;
        }
        if (outcome === "none") continue;
        if (outcome === "skipped") {
          totals.skipped += 1;
          continue;
        }
        totals.attempted += 1;
        if (outcome === "uploaded") totals.uploaded += 1;
        else if (outcome === "rejected") totals.rejected += 1;
        else totals.deferred += 1;
        if (outcome === "interrupted") {
          // 设备授权问题（401/403）：后面的记录必然同样失败，立即中断本批次。
          return finish(true);
        }
      }
    }
    return finish(false);
  }

  public async nextReadyAtIso(): Promise<string | null> {
    const scope = this.options.scope();
    if (!scope) return null;
    return this.options.repository.nextReadyAtIso(scope);
  }

  private async uploadOne(
    closeId: string,
    scope: DailyCloseUploadScope,
  ): Promise<ItemOutcome> {
    const repository = this.options.repository;
    if (!isDailyCloseUploadGuid(closeId)) {
      // 服务端 dailyCloseGuid 是 Guid：非 GUID 的遗留 closeId 永远无法上传，不发请求。
      await repository.markSkipped(
        closeId,
        "DAILY_CLOSE_GUID_INVALID",
        "Daily close id is not a GUID and cannot be uploaded.",
      );
      return "skipped";
    }
    const lease = await repository.tryClaim(
      closeId,
      scope,
      this.options.now().toISOString(),
    );
    // 没抢到（别处已认领/尚未到期/范围不符）：不是失败，也不算一次尝试。
    if (!lease) return "none";

    try {
      const archive = await repository.readArchive(closeId);
      if (!archive) {
        await this.markPending(
          lease,
          "ARCHIVE_NOT_FOUND",
          "The daily close archive could not be read.",
        );
        return "deferred";
      }
      let request;
      try {
        request = mapDailyCloseArchiveToSyncRequest(archive, {
          clientKind: this.options.clientKind,
          appVersion: this.options.appVersion,
        });
      } catch (error) {
        if (error instanceof DailyCloseSyncMappingError) {
          // 本地数据本身无法映射：重试不会改变结果，永久拒绝且不发请求。
          await repository.markRejected(
            closeId,
            error.code,
            trimMessage(error.message),
          );
          return "rejected";
        }
        throw error;
      }

      const result = await this.options.sync.sync(request);
      switch (result.kind) {
        case "synced":
          // Accepted / AlreadySynced / ReplacedPlaceholder 都表示服务端已持有这条日结。
          await repository.markSucceeded(
            closeId,
            this.options.now().toISOString(),
          );
          return "uploaded";
        case "rejected":
          await repository.markRejected(
            closeId,
            result.code,
            trimMessage(result.message),
          );
          return "rejected";
        case "unauthorized":
          // 设备授权问题不是这条记录的错：保持 pending、撤销本次尝试计数，并中断整个批次。
          await repository.releaseWithoutAttempt(
            closeId,
            new Date(
              this.options.now().getTime() +
                DAILY_CLOSE_UNAUTHORIZED_COOLDOWN_SECONDS * 1_000,
            ).toISOString(),
            result.code,
            trimMessage(result.message),
          );
          return "interrupted";
        case "retry":
          await this.markPending(lease, result.code, result.message);
          return "deferred";
      }
    } catch {
      // 上传异常必须保持在后台重试，不得影响收银流程。
      await this.markPending(
        lease,
        "UPLOAD_EXCEPTION",
        "Daily close upload failed unexpectedly.",
      );
      return "deferred";
    }
  }

  private markPending(
    lease: DailyCloseUploadLease,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    return this.options.repository.markPending(
      lease.closeId,
      new Date(
        this.options.now().getTime() +
          dailyCloseUploadRetryDelaySeconds(lease.attemptCount) * 1_000,
      ).toISOString(),
      errorCode,
      trimMessage(errorMessage),
    );
  }
}

/** 退避 5s→10s→20s→…→300s 封顶（与 WPF / Linkly 结算上传同一公式），按累计尝试次数增长。 */
export function dailyCloseUploadRetryDelaySeconds(attemptCount: number): number {
  return Math.min(
    300,
    5 * 2 ** Math.min(Math.max(attemptCount - 1, 0), 6),
  );
}

function trimMessage(message: string): string {
  const text = message.trim();
  if (text.length === 0) return "Daily close sync failed.";
  return text.length <= MAXIMUM_ERROR_MESSAGE_LENGTH
    ? text
    : text.slice(0, MAXIMUM_ERROR_MESSAGE_LENGTH);
}
