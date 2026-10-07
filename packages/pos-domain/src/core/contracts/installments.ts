export type InstallmentStatus =
  | "Active"
  | "PaidOff"
  | "PickedUp"
  | "Cancelled";

export type InstallmentCancellationKind = "RefundCancel" | "VoidCancel";

export type InstallmentSummary = Readonly<{
  installmentGuid: string;
  installmentNumber: string;
  storeCode: string;
  deviceCode: string;
  cashierName: string;
  customerName: string;
  customerPhone: string | null;
  createdAtIso: string;
  totalCents: number;
  downPaymentCents: number;
  paidCents: number;
  balanceCents: number;
  status: InstallmentStatus;
  updatedAtIso: string;
}>;

export type InstallmentSnapshot = InstallmentSummary &
  Readonly<{
    note: string | null;
    encryptedSensitiveRevision: number;
  }>;

/**
 * 分期本地缓存只用于离线浏览。所有 create/repayment/pickup/cancel/void 写操作
 * 必须通过在线 runtime，并复用耐久支付 attempt；仓储不得提供离线写业务状态的方法。
 */
export interface InstallmentSnapshotRepositoryPort {
  replaceForStore(
    storeCode: string,
    snapshots: readonly InstallmentSnapshot[],
  ): Promise<void>;
  listForStore(
    storeCode: string,
    limit: number,
    offset: number,
  ): Promise<readonly InstallmentSnapshot[]>;
  get(
    storeCode: string,
    installmentGuid: string,
  ): Promise<InstallmentSnapshot | null>;
}

export function canTransitionInstallment(
  from: InstallmentStatus,
  to: InstallmentStatus,
): boolean {
  return (
    (from === "Active" && (to === "PaidOff" || to === "Cancelled")) ||
    // 已付清未提货的单允许取消并全额退款；作废（不退款）仍只限进行中单。
    (from === "PaidOff" && (to === "PickedUp" || to === "Cancelled"))
  );
}

/**
 * 与服务端 InstallmentLifecycleRules.CanCancelWithRefund 同一口径：
 * 未付清的进行中单，或已付清（余额为 0）尚未提货的单可以取消并退款；
 * 已提货、已取消一律不可。作废（不退款）不走此规则，仍只限进行中且有余额。
 */
export function canCancelInstallmentWithRefund(
  status: InstallmentStatus,
  balanceCents: number,
): boolean {
  return (
    (status === "Active" && balanceCents > 0) ||
    (status === "PaidOff" && balanceCents === 0)
  );
}
