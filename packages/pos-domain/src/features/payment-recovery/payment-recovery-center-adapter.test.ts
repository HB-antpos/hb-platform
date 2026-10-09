import assert from "node:assert/strict";
import test from "node:test";

import { PaymentRecoveryCenterAdapter, type PaymentRecoveryOperations } from "./payment-recovery-center-adapter";

import type { PaymentRecoveryRecord } from "./payment-recovery-types";
const record: PaymentRecoveryRecord = {
  id: "attempt-1", orderGuid: "order-1", occurredAtIso: "2026-09-11T00:00:00.000Z",
  amountCents: 99, status: "result-unknown", terminalName: "Lane 1", transactionReference: "ref-1",
  receiptReference: null, lines: [], events: [],
};
function ports(overrides: Partial<PaymentRecoveryOperations> = {}): PaymentRecoveryOperations {
  return { list: async () => [record], recoverOriginalPayment: async () => {},
    submitManualVerification: async () => {}, parkCurrent: async () => {}, ...overrides };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => { resolve = r; });
  return { promise, resolve };
}
test("恢复原交易后重读记录，不创建新支付", async () => {
  let resolved = false;
  const adapter = new PaymentRecoveryCenterAdapter(ports({
    list: async () => [{ ...record, status: resolved ? "provider-recovered" : "result-unknown" }],
    recoverOriginalPayment: async (id) => { assert.equal(id, record.id); resolved = true; },
  }));
  await adapter.refresh(); await adapter.recoverOriginalPayment(record.id);
  assert.equal(adapter.getState().records[0]?.status, "provider-recovered");
  assert.equal(adapter.getState().filter, "resolved");
  assert.equal(adapter.getState().selectedRecordId, record.id);
  adapter.destroy();
});
test("下一单等待耐久转存成功，失败拒绝导航承诺", async () => {
  const adapter = new PaymentRecoveryCenterAdapter(ports({ parkCurrent: async () => { throw new Error("disk full"); } }));
  await assert.rejects(adapter.startNextSale(), /disk full/);
  assert.equal(adapter.getState().errorCode, "RECOVERY_ACTION_FAILED");
  assert.equal(adapter.getState().action, "idle");
});
test("并发操作不重复提交，卸载后不发布结果", async () => {
  const pending = deferred<void>(); let calls = 0;
  const adapter = new PaymentRecoveryCenterAdapter(ports({ recoverOriginalPayment: async () => { calls++; await pending.promise; } }));
  let notifications = 0; adapter.subscribe(() => { notifications++; });
  const first = adapter.recoverOriginalPayment(record.id);
  await assert.rejects(adapter.recoverOriginalPayment(record.id), /RECOVERY_BUSY/);
  adapter.destroy(); const before = notifications; pending.resolve(); await first;
  assert.equal(calls, 1); assert.equal(notifications, before);
});
test("较早的列表结果不能覆盖较新的结果", async () => {
  const old = deferred<readonly PaymentRecoveryRecord[]>(); let reads = 0;
  const adapter = new PaymentRecoveryCenterAdapter(ports({ list: () => ++reads === 1 ? old.promise : Promise.resolve([{ ...record, status: "manual-paid" }]) }));
  const first = adapter.refresh(); await adapter.refresh(); old.resolve([record]); await first;
  assert.equal(adapter.getState().records[0]?.status, "manual-paid");
});

test("当前销售占用时显示本地化可操作错误，不泄漏底层消息", async () => {
  const adapter = new PaymentRecoveryCenterAdapter(ports({
    recoverOriginalPayment: async () => { throw Object.assign(new Error("internal cart data"), { code: "ACTIVE_PRICING_CART_BUSY" }); },
  }));
  await assert.rejects(adapter.recoverOriginalPayment(record.id));
  assert.equal(adapter.getState().errorCode, "RECOVERY_CURRENT_SALE_BUSY");
});

test("M34：投影保留终端实扣金额，金额不符与反冲正确认的底层错误映射为稳定页面错误码", async () => {
  const { projectPaymentRecoveryRecord } = await import("./payment-recovery-center-adapter");
  const projected = projectPaymentRecoveryRecord({
    recordId: "r1", checkoutIntentId: "c1", orderGuid: "o1", attemptId: "a1", storeCode: "S1", deviceCode: "D1",
    terminalName: null, occurredAtIso: "2026-09-11T00:00:00.000Z", amountCents: 1_000, provider: "linkly-cloud",
    attemptState: "Unknown", orderState: "Draft", isParked: true, status: "result-unknown",
    transactionReference: "TXN-1", receiptReference: null, terminalAmountMismatchCents: 1_500, lines: [], events: [],
  });
  assert.equal(projected.terminalAmountMismatchCents, 1_500);
  for (const code of ["PAYMENT_RECOVERY_APPROVED_AMOUNT_MISMATCH", "PAYMENT_RECOVERY_REVERSAL_ACK_REQUIRED"]) {
    const adapter = new PaymentRecoveryCenterAdapter(ports({
      submitManualVerification: async () => { throw new Error(code); },
    }));
    await assert.rejects(adapter.submitManualVerification({
      recordId: "attempt-1", finding: "unpaid", verifiedAmountCents: null, evidenceReference: "e", note: "n",
    }));
    assert.equal(adapter.getState().errorCode, "RECOVERY_APPROVED_AMOUNT_MISMATCH");
  }
});
