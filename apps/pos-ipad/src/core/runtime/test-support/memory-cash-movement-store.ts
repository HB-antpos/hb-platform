import {
  CashMovementRepositoryError,
  formatCashMovementDisplayNumber,
  summarizeCashMovements,
  type CashMovementCommit,
  type CashMovementRepositoryPort,
  type CashMovementSummary,
  type LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";
import type {
  CashMovementClaimResult,
  CashMovementUploadRepositoryPort,
  CashMovementUploadScope,
} from "@hb/pos-domain/core/contracts/cash-movement-upload";
import type { DailyCloseScope } from "@hb/pos-domain/core/contracts/daily-close";
import type { AuditEventDraft } from "@hb/pos-domain/core/contracts/order";

/**
 * 仅测试使用的内存替身：同时实现现金进出仓储端口与上传端口，语义对齐 SQLite 实现的关键点
 * （流水号 = 记录数 + 1、冲正标记原记录、相同 guid 重放、上传状态机最小子集）。
 * 真实 SQL 约束（触发器、CHECK）由共享层的 SQLite 集成测试覆盖，这里不重复。
 */
export class MemoryCashMovementStore
  implements CashMovementRepositoryPort, CashMovementUploadRepositoryPort
{
  public rows: LocalCashMovement[] = [];
  public readonly audits: AuditEventDraft[] = [];
  /** 设置后 append 抛出该错误（模拟落库失败）。 */
  public appendError: Error | null = null;
  public latestDailyCloseSavedAt: string | null = null;
  public appendCalls = 0;

  public async append(
    commit: CashMovementCommit,
  ): Promise<Readonly<{ replayed: boolean; movement: LocalCashMovement }>> {
    this.appendCalls += 1;
    if (this.appendError) throw this.appendError;
    const existing = this.rows.find(
      (row) => row.movementGuid === commit.movement.movementGuid,
    );
    if (existing) return { replayed: true, movement: existing };
    if (commit.movement.voidsMovementGuid !== null) {
      const index = this.rows.findIndex(
        (row) => row.movementGuid === commit.movement.voidsMovementGuid,
      );
      if (index < 0) throw new CashMovementRepositoryError("VOID_TARGET_NOT_FOUND");
      if (this.rows[index]!.voidedByMovementGuid !== null) {
        throw new CashMovementRepositoryError("VOID_NOT_ALLOWED", "ALREADY_VOIDED");
      }
      this.rows[index] = {
        ...this.rows[index]!,
        voidedByMovementGuid: commit.movement.movementGuid,
      };
    }
    const movement: LocalCashMovement = {
      ...commit.movement,
      displayNumber: formatCashMovementDisplayNumber(this.rows.length + 1),
      voidedByMovementGuid: null,
      auditEventId: commit.audit.eventId,
      uploadState: "pending",
      uploadAttempts: 0,
      uploadErrorCode: null,
      uploadedAtIso: null,
      createdAtIso: commit.movement.occurredAtIso,
    };
    this.rows.push(movement);
    this.audits.push(commit.audit);
    return { replayed: false, movement };
  }

  public async getByGuid(movementGuid: string): Promise<LocalCashMovement | null> {
    return this.rows.find((row) => row.movementGuid === movementGuid) ?? null;
  }

  public async listToday(scope: DailyCloseScope): Promise<readonly LocalCashMovement[]> {
    return this.inScope(scope).sort((left, right) =>
      left.occurredAtIso.localeCompare(right.occurredAtIso),
    );
  }

  public async summarizePeriod(scope: DailyCloseScope): Promise<CashMovementSummary> {
    return summarizeCashMovements(this.inScope(scope));
  }

  public async latestDailyCloseSavedAtIso(): Promise<string | null> {
    return this.latestDailyCloseSavedAt;
  }

  /** 测试里直接改某条记录的上传状态。 */
  public patchRow(movementGuid: string, patch: Partial<LocalCashMovement>): void {
    const index = this.rows.findIndex((row) => row.movementGuid === movementGuid);
    if (index < 0) throw new Error(`unknown movement ${movementGuid}`);
    this.rows[index] = { ...this.rows[index]!, ...patch };
  }

  // ───────────── 上传端口（最小子集）─────────────

  /** 每次 claimDueBatch 的范围，用来断言上传范围固定为可信终端。 */
  public readonly claimScopes: CashMovementUploadScope[] = [];

  public async recoverExpiredUploading(): Promise<number> {
    return 0;
  }

  public async claimDueBatch(
    scope: CashMovementUploadScope,
    limit: number,
  ): Promise<CashMovementClaimResult> {
    this.claimScopes.push(scope);
    const claimed: { movement: LocalCashMovement; attemptCount: number }[] = [];
    for (const row of [...this.rows]) {
      if (claimed.length >= limit) break;
      if (
        row.uploadState !== "pending" ||
        row.storeCode !== scope.storeCode ||
        row.deviceCode !== scope.deviceCode
      ) {
        continue;
      }
      this.patchRow(row.movementGuid, {
        uploadState: "uploading",
        uploadAttempts: row.uploadAttempts + 1,
      });
      claimed.push({
        movement: this.rows.find((entry) => entry.movementGuid === row.movementGuid)!,
        attemptCount: row.uploadAttempts + 1,
      });
    }
    return { claimed, rejectedLocally: 0 };
  }

  public async markSynced(movementGuids: readonly string[], uploadedAtIso: string): Promise<void> {
    for (const guid of movementGuids) {
      this.patchRow(guid, { uploadState: "synced", uploadedAtIso });
    }
  }

  public async markRetry(movementGuid: string, _next: string, errorCode: string): Promise<void> {
    this.patchRow(movementGuid, { uploadState: "pending", uploadErrorCode: errorCode });
  }

  public async releaseWithoutAttempt(movementGuids: readonly string[]): Promise<void> {
    for (const guid of movementGuids) this.patchRow(guid, { uploadState: "pending" });
  }

  public async markRejected(movementGuid: string, errorCode: string): Promise<void> {
    this.patchRow(movementGuid, { uploadState: "rejected", uploadErrorCode: errorCode });
  }

  public async nextReadyAtIso(): Promise<string | null> {
    return null;
  }

  private inScope(scope: DailyCloseScope): LocalCashMovement[] {
    const from = Date.parse(scope.periodFromIso);
    const to = Date.parse(scope.periodToIso);
    return this.rows.filter((row) => {
      const at = Date.parse(row.occurredAtIso);
      return (
        row.storeCode === scope.storeCode &&
        row.deviceCode === scope.deviceCode &&
        at >= from &&
        at < to
      );
    });
  }
}
