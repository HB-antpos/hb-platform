export type PaymentRecoveryFilter = "pending" | "failed" | "resolved";

export type PaymentRecoveryStatus =
  | "result-unknown"
  | "payment-failed"
  | "charged-order-incomplete"
  | "manual-paid"
  | "manual-unpaid"
  | "provider-recovered"
  | "review-required";

export type PaymentRecoveryLine = Readonly<{
  id: string;
  name: string;
  quantity: string;
  amountCents: number;
}>;

export type PaymentRecoveryEventCode =
  | "attempt-created"
  | "provider-query-started"
  | "provider-paid"
  | "provider-failed"
  | "operator-paid"
  | "operator-unpaid"
  | "operator-uncertain"
  | "provider-manual-conflict"
  | "order-completed";

export type PaymentRecoveryEvent = Readonly<{
  id: string;
  occurredAtIso: string;
  code: PaymentRecoveryEventCode;
  params?: Readonly<Record<string, string | number>>;
  source: "provider" | "operator" | "system";
}>;

export type PaymentRecoveryRecord = Readonly<{
  id: string;
  orderGuid: string;
  occurredAtIso: string;
  amountCents: number;
  status: PaymentRecoveryStatus;
  terminalName: string | null;
  transactionReference: string | null;
  receiptReference: string | null;
  /** 终端批准但实扣金额与订单不符时的实扣分值（M34）；否则为 null。 */
  terminalAmountMismatchCents?: number | null;
  lines: readonly PaymentRecoveryLine[];
  events: readonly PaymentRecoveryEvent[];
}>;

export type ManualPaymentFinding = "paid" | "unpaid" | "uncertain";

export type ManualPaymentVerificationInput = Readonly<{
  recordId: string;
  finding: ManualPaymentFinding;
  verifiedAmountCents: number | null;
  /** 已取消证据输入；仅为兼容旧调用方保留，缺省时由适配器补占位值。 */
  evidenceReference?: string;
  /** 选填；缺省时由适配器补默认说明。 */
  note: string;
  /** 终端已批准不同金额时，主管明确知晓终端已扣款且需总部冲正（仅 unpaid 结论使用）。 */
  terminalChargeAcknowledged?: boolean;
}>;

/** Square 已受理（PENDING）后在后台跟踪结算的退款；退货单本身已完成。 */
export type SquareRefundSettlementStatus = "settling" | "settled" | "rejected" | "refunded-otherwise";

export type SquareRefundSettlementView = Readonly<{
  attemptId: string;
  returnOrderGuid: string;
  amountCents: number;
  status: SquareRefundSettlementStatus;
  squareStatus: string | null;
  lastCheckedAtIso: string | null;
  registeredAtIso: string;
}>;

export type PaymentRecoveryCenterState = Readonly<{
  filter: PaymentRecoveryFilter;
  keyword: string;
  records: readonly PaymentRecoveryRecord[];
  selectedRecordId: string | null;
  /** 结算中或被拒、尚需关注的 Square 退款（已到账/已结案的不再展示）。 */
  refundSettlements: readonly SquareRefundSettlementView[];
  loading: boolean;
  refreshing: boolean;
  action: "idle" | "recovering" | "manual-verifying" | "checking-refunds" | "resolving-refund";
  errorCode: string | null;
}>;

export interface PaymentRecoveryCenterService {
  getState(): PaymentRecoveryCenterState;
  subscribe(listener: () => void): () => void;
  setFilter(filter: PaymentRecoveryFilter): void;
  setKeyword(keyword: string): void;
  selectRecord(recordId: string): void;
  refresh(): Promise<void>;
  recoverOriginalPayment(recordId: string): Promise<void>;
  submitManualVerification(input: ManualPaymentVerificationInput): Promise<void>;
  /** 立即只读检查结算中的 Square 退款。 */
  checkRefundSettlements(): Promise<void>;
  /** 被拒的 Square 退款：主管授权确认已改用其他方式退给顾客。 */
  acknowledgeRefundSettlementFailure(input: Readonly<{ attemptId: string; note: string }>): Promise<void>;
}
