import type { PaymentRecoveryCenterRecord } from "./payment-recovery-center-contract";

import type {
  ManualPaymentVerificationInput,
  PaymentRecoveryCenterService,
  PaymentRecoveryCenterState,
  PaymentRecoveryFilter,
  PaymentRecoveryRecord,
} from "./payment-recovery-types";

/** 页面只持有业务接口；数据库、主管身份与支付凭据始终留在生产运行时。 */
export interface PaymentRecoveryOperations {
  list(): Promise<readonly PaymentRecoveryRecord[]>;
  recoverOriginalPayment(recordId: string): Promise<void | "completed">;
  submitManualVerification(input: ManualPaymentVerificationInput): Promise<void>;
  parkCurrent(): Promise<void>;
}

export class PaymentRecoveryCenterAdapter implements PaymentRecoveryCenterService {
  private state: PaymentRecoveryCenterState = {
    filter: "pending", keyword: "", records: [], selectedRecordId: null,
    loading: true, refreshing: false, action: "idle", errorCode: null,
  };
  private readonly listeners = new Set<() => void>();
  private disposed = false;
  private generation = 0;

  public constructor(private readonly operations: PaymentRecoveryOperations) {}
  public getState = (): PaymentRecoveryCenterState => this.state;
  public subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };
  public destroy() { this.disposed = true; this.generation += 1; this.listeners.clear(); }
  public setFilter(filter: PaymentRecoveryFilter) { this.publish({ filter }); }
  public setKeyword(keyword: string) { this.publish({ keyword }); }
  public selectRecord(selectedRecordId: string) {
    if (this.state.records.some((record) => record.id === selectedRecordId)) this.publish({ selectedRecordId });
  }
  public async refresh(): Promise<void> {
    if (this.disposed) return;
    const generation = ++this.generation;
    this.publish({ refreshing: true, errorCode: null });
    try {
      const records = await this.operations.list();
      if (this.disposed || generation !== this.generation) return;
      this.publish({ records, selectedRecordId: records.some((r) => r.id === this.state.selectedRecordId)
        ? this.state.selectedRecordId : records[0]?.id ?? null });
    } catch {
      if (generation === this.generation) this.publish({ errorCode: "RECOVERY_LOAD_FAILED" });
    } finally {
      if (generation === this.generation) this.publish({ loading: false, refreshing: false });
    }
  }
  public async recoverOriginalPayment(recordId: string) {
    await this.run("recovering", async () => { await this.operations.recoverOriginalPayment(recordId); });
    this.showProcessedRecord(recordId);
  }
  public async submitManualVerification(input: ManualPaymentVerificationInput) {
    await this.run("manual-verifying", () => this.operations.submitManualVerification(input));
    this.showProcessedRecord(input.recordId);
  }
  public async startNextSale(): Promise<void> {
    await this.run("recovering", () => this.operations.parkCurrent());
  }
  private showProcessedRecord(recordId: string) {
    const record = this.state.records.find((item) => item.id === recordId);
    if (record && ["manual-paid", "manual-unpaid", "provider-recovered"].includes(record.status)) {
      this.publish({ filter: "resolved", selectedRecordId: recordId });
    }
  }
  private async run(action: "recovering" | "manual-verifying", operation: () => Promise<void>) {
    if (this.disposed || this.state.action !== "idle") throw new Error("RECOVERY_BUSY");
    // 先失效旧列表请求，防止操作完成后被操作前的记录覆盖。
    this.generation += 1;
    this.publish({ action, errorCode: null });
    try {
      await operation();
      await this.refresh();
    } catch (error) {
      this.publish({ errorCode: recoveryActionErrorCode(error) });
      throw error;
    } finally { this.publish({ action: "idle" }); }
  }
  private publish(update: Partial<PaymentRecoveryCenterState>) {
    if (this.disposed) return;
    this.state = { ...this.state, ...update };
    this.listeners.forEach((listener) => listener());
  }
}


/** 将耐久记录投影为本地化事件码；不向页面提供门店权限或主管票据。 */
export function projectPaymentRecoveryRecord(record: PaymentRecoveryCenterRecord): PaymentRecoveryRecord {
  return {
    id: record.recordId, orderGuid: record.orderGuid, occurredAtIso: record.occurredAtIso,
    amountCents: record.amountCents,
    status: record.status === "manual-uncertain" ? "result-unknown" : record.status,
    terminalName: record.terminalName,
    transactionReference: record.transactionReference,
    receiptReference: record.receiptReference,
    lines: record.lines,
    events: record.events.map((event) => ({
      id: event.id, occurredAtIso: event.occurredAtIso, source: event.source,
      params: Object.fromEntries(Object.entries(event.params).filter((entry): entry is [string, string | number] => entry[1] !== null)),
      code: event.code === "PAYMENT_RECOVERY_MANUAL_FINDING"
        ? event.params.finding === "paid" ? "operator-paid"
          : event.params.finding === "unpaid" ? "operator-unpaid" : "operator-uncertain"
        : "attempt-created",
    })),
  };
}

function recoveryActionErrorCode(error: unknown): string {
  const code = error && typeof error === "object" && "code" in error && typeof error.code === "string"
    ? error.code : error instanceof Error ? error.message : "";
  if (code === "ACTIVE_PRICING_CART_BUSY") return "RECOVERY_CURRENT_SALE_BUSY";
  if (code === "PAYMENT_RECOVERY_PROVIDER_UNAVAILABLE") return "RECOVERY_PROVIDER_UNAVAILABLE";
  if (code === "LINKLY_ACKNOWLEDGEMENT_PENDING") return "RECOVERY_TERMINAL_CONFIRMATION_PENDING";
  if (code === "PAYMENT_RECOVERY_AUTHORIZATION_DENIED") return "RECOVERY_AUTHORIZATION_DENIED";
  if (code === "PAYMENT_RECOVERY_AUTHORIZATION_REVOKED" || code === "CURRENT_CASHIER_REQUIRED") return "RECOVERY_SESSION_CHANGED";
  return "RECOVERY_ACTION_FAILED";
}
