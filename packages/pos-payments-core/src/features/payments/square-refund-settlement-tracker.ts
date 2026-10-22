/**
 * Square 退款结算跟踪（对齐 WPF #674 SquareRefundSettlementService）。
 * 退货单在 Square 返回 PENDING 时已当场完成；这里只用 GET 查询同一笔退款的结算结果，
 * 绝不重新发起退款。Square 官方：PENDING 多数几小时内完成、最长 14 天；
 * 只有商家余额与绑定银行账户都不足时才会 REJECTED/FAILED，此时需主管改用其他方式退给顾客。
 */

export type SquareRefundSettlementTrackerScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

export type SquareRefundSettlementCandidate = Readonly<{
  attemptId: string;
  providerEnvironment: string | null;
}>;

export type SquareRefundSettlementTrackerObservation =
  | Readonly<{ kind: "settled"; squareStatus: string }>
  | Readonly<{ kind: "rejected"; squareStatus: string }>
  | Readonly<{ kind: "still-settling"; squareStatus: string }>
  | Readonly<{ kind: "check-failed"; errorCode: string }>;

/** 结算记录的最小持久化面；SQLite 实现见 pos-db SqliteSquareRefundSettlementStore。 */
export interface SquareRefundSettlementTrackerStore {
  listSettling(scope: SquareRefundSettlementTrackerScope): Promise<readonly SquareRefundSettlementCandidate[]>;
  readRefundId(attemptId: string): Promise<string | null>;
  recordObservation(attemptId: string, observation: SquareRefundSettlementTrackerObservation): Promise<boolean>;
}

/** 只读查询同一笔 Square 退款，返回其状态（大写）；实现不得发起任何写请求。 */
export interface SquareRefundStatusPort {
  getRefundStatus(environment: string, refundId: string): Promise<string>;
}

export type SquareRefundSettlementCheckResult = Readonly<{
  checked: number;
  settled: number;
  rejected: number;
  stillSettling: number;
  errors: number;
}>;

/** 后台检查间隔：与 WPF 一致约 10 分钟一次；恢复中心也可手动立即检查。 */
export const SQUARE_REFUND_SETTLEMENT_CHECK_INTERVAL_MS = 10 * 60_000;

const EMPTY_RESULT: SquareRefundSettlementCheckResult = Object.freeze({
  checked: 0, settled: 0, rejected: 0, stillSettling: 0, errors: 0,
});

export class SquareRefundSettlementTracker {
  private inFlight: Promise<SquareRefundSettlementCheckResult> | null = null;

  public constructor(
    private readonly store: SquareRefundSettlementTrackerStore,
    private readonly square: SquareRefundStatusPort,
  ) {}

  /**
   * 检查本设备所有结算中的退款。后台定时与恢复中心可能同时触发：
   * 复用同一次进行中的检查，避免对同一记录并发查询与 CAS。
   */
  public checkSettling(scope: SquareRefundSettlementTrackerScope): Promise<SquareRefundSettlementCheckResult> {
    if (this.inFlight) return this.inFlight;
    const run = this.checkOnce(scope).finally(() => {
      if (this.inFlight === run) this.inFlight = null;
    });
    this.inFlight = run;
    return run;
  }

  private async checkOnce(scope: SquareRefundSettlementTrackerScope): Promise<SquareRefundSettlementCheckResult> {
    const candidates = await this.store.listSettling(scope);
    if (candidates.length === 0) return EMPTY_RESULT;
    let settled = 0;
    let rejected = 0;
    let stillSettling = 0;
    let errors = 0;
    for (const candidate of candidates) {
      const observation = await this.observe(candidate);
      // 单条记录的落库失败不能挡住其他记录的检查。
      await this.store.recordObservation(candidate.attemptId, observation).catch(() => false);
      if (observation.kind === "settled") settled += 1;
      else if (observation.kind === "rejected") rejected += 1;
      else if (observation.kind === "still-settling") stillSettling += 1;
      else errors += 1;
    }
    return Object.freeze({ checked: candidates.length, settled, rejected, stillSettling, errors });
  }

  private async observe(candidate: SquareRefundSettlementCandidate): Promise<SquareRefundSettlementTrackerObservation> {
    const environment = candidate.providerEnvironment?.trim();
    if (!environment) return { kind: "check-failed", errorCode: "SQUARE_REFUND_ENVIRONMENT_REQUIRED" };
    let refundId: string | null;
    try {
      refundId = await this.store.readRefundId(candidate.attemptId);
    } catch {
      return { kind: "check-failed", errorCode: "SQUARE_REFUND_EVIDENCE_UNREADABLE" };
    }
    if (!refundId) return { kind: "check-failed", errorCode: "SQUARE_REFUND_ID_MISSING" };
    let status: string;
    try {
      status = (await this.square.getRefundStatus(environment, refundId)).trim().toUpperCase();
    } catch (error) {
      return { kind: "check-failed", errorCode: checkErrorCode(error) };
    }
    return squareRefundSettlementObservation(status);
  }
}

/** Square 退款状态 → 结算结论。只有 COMPLETED 才算到账；REJECTED/FAILED/CANCELED 都没有退回顾客。 */
export function squareRefundSettlementObservation(status: string): SquareRefundSettlementTrackerObservation {
  if (status === "COMPLETED") return { kind: "settled", squareStatus: status };
  if (status === "REJECTED" || status === "FAILED" || status === "CANCELED") {
    return { kind: "rejected", squareStatus: status };
  }
  if (status === "PENDING") return { kind: "still-settling", squareStatus: status };
  return { kind: "check-failed", errorCode: status ? `SQUARE_REFUND_${status}` : "SQUARE_REFUND_STATUS_MISSING" };
}

function checkErrorCode(error: unknown): string {
  const code = error && typeof error === "object" && "code" in error && typeof error.code === "string"
    ? error.code.trim()
    : "";
  return /^[A-Za-z0-9_.:-]{1,80}$/u.test(code) ? code.toUpperCase() : "SQUARE_REFUND_CHECK_FAILED";
}
