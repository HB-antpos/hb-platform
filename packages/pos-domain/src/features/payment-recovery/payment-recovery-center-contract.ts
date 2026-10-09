import type { AuditActorSnapshot } from "../../core/contracts/audit-actor";
import type { PaymentAttempt } from "../../core/contracts/payment";

/**
 * 支付恢复中心的耐久记录与命令合同。SQLite 实现在 pos-db，运行时适配与页面只依赖本合同，
 * iPad 与手持共用同一份。
 */
export type PaymentRecoveryCenterStatus =
  | "result-unknown"
  | "payment-failed"
  | "charged-order-incomplete"
  | "manual-paid"
  | "manual-unpaid"
  | "manual-uncertain"
  | "provider-recovered"
  | "review-required";

export type PaymentRecoveryCenterRecord = Readonly<{
  recordId: string;
  checkoutIntentId: string;
  orderGuid: string;
  attemptId: string;
  storeCode: string;
  deviceCode: string;
  /** 当前账本尚未持久化 provider terminal 名称时必须如实为空。 */
  terminalName: string | null;
  occurredAtIso: string;
  amountCents: number;
  provider: "square" | "linkly-cloud";
  attemptState: PaymentAttempt["state"];
  orderState: string;
  isParked: boolean;
  status: PaymentRecoveryCenterStatus;
  transactionReference: string | null;
  receiptReference: string | null;
  lines: readonly Readonly<{
    id: string;
    name: string;
    quantity: string;
    amountCents: number;
  }>[];
  events: readonly Readonly<{
    id: string;
    occurredAtIso: string;
    code: "PAYMENT_RECOVERY_PARKED" | "PAYMENT_RECOVERY_MANUAL_FINDING";
    source: "system" | "operator";
    params: Readonly<Record<string, string | number | null>>;
  }>[];
}>;

export type PaymentRecoveryCenterScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

export type ParkPaymentRecoveryInput = PaymentRecoveryCenterScope & Readonly<{
  orderGuid: string;
  attemptId: string;
  actionId: string;
  actor: AuditActorSnapshot;
}>;

export type ManualPaymentRecoveryFindingInput = PaymentRecoveryCenterScope & Readonly<{
  recordId: string;
  actionId: string;
  finding: "paid" | "unpaid" | "uncertain";
  verifiedAmountCents: number | null;
  evidenceReference: string;
  note: string;
  authorizationId: string;
  supervisorActor: AuditActorSnapshot;
  requestingActor: AuditActorSnapshot;
  /** paid 结论必须引用刚完成且绑定原 attempt 的只读 provider 对账。 */
  reconciliationId?: string;
}>;

export type PaymentRecoveryReconciliationInput = PaymentRecoveryCenterScope & Readonly<{
  recordId: string;
  reconciliationId: string;
}>;

export type ManualPaymentRecoveryFindingResult = Readonly<{
  record: PaymentRecoveryCenterRecord;
  actionId: string;
  authorizationId: string;
  replayed: boolean;
}>;

export type ManualPaidRecoveryCommitContext = Readonly<{
  record: PaymentRecoveryCenterRecord;
  actionId: string;
  authorizationId: string;
  supervisorActor: AuditActorSnapshot;
  tenderGuid: string | null;
}>;
