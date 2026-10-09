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
  lines: readonly PaymentRecoveryLine[];
  events: readonly PaymentRecoveryEvent[];
}>;

export type ManualPaymentFinding = "paid" | "unpaid" | "uncertain";

export type ManualPaymentVerificationInput = Readonly<{
  recordId: string;
  finding: ManualPaymentFinding;
  verifiedAmountCents: number | null;
  evidenceReference: string;
  note: string;
}>;

export type PaymentRecoveryCenterState = Readonly<{
  filter: PaymentRecoveryFilter;
  keyword: string;
  records: readonly PaymentRecoveryRecord[];
  selectedRecordId: string | null;
  loading: boolean;
  refreshing: boolean;
  action: "idle" | "recovering" | "manual-verifying";
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
}
