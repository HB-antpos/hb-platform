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

// 证据输入取消、备注选填：适配器补占位值，账本的非空 CHECK 约束和同步契约保持不变。
test("人工核实不填证据与备注时补占位值；写了备注则规范化后原样传下去", async () => {
  const sent: { evidenceReference?: string | undefined; note: string }[] = [];
  const adapter = new PaymentRecoveryCenterAdapter(ports({
    submitManualVerification: async (input) => { sent.push({ evidenceReference: input.evidenceReference, note: input.note }); },
  }));
  await adapter.submitManualVerification({ recordId: "attempt-1", finding: "unpaid", verifiedAmountCents: null, note: "" });
  await adapter.submitManualVerification({
    recordId: "attempt-1", finding: "unpaid", verifiedAmountCents: null, note: " 刷卡机离线\n刷卡机卡住了 ",
  });
  await adapter.submitManualVerification({
    recordId: "attempt-1", finding: "unpaid", verifiedAmountCents: null, evidenceReference: " R-42 ", note: "n",
  });
  assert.deepEqual(sent, [
    { evidenceReference: "-", note: "Supervisor confirmed (no note entered)." },
    { evidenceReference: "-", note: "刷卡机离线 刷卡机卡住了" },
    { evidenceReference: "R-42", note: "n" },
  ]);
});

test("Square 退款结算：刷新只带回结算中/被拒的记录；检查与结案后重读；运行时缺能力时报不可用", async () => {
  const settlement = (attemptId: string, status: "settling" | "settled" | "rejected" | "refunded-otherwise") => ({
    attemptId, returnOrderGuid: `return-${attemptId}`, amountCents: 500, status,
    squareStatus: null, lastCheckedAtIso: null, registeredAtIso: "2026-10-22T00:00:00.000Z",
  });
  let rows = [settlement("s1", "settling"), settlement("s2", "rejected"), settlement("s3", "settled"), settlement("s4", "refunded-otherwise")];
  const acknowledged: { attemptId: string; note: string }[] = [];
  let checks = 0;
  const adapter = new PaymentRecoveryCenterAdapter(ports({
    listRefundSettlements: async () => rows,
    checkRefundSettlements: async () => { checks += 1; rows = rows.map((row) => row.attemptId === "s1" ? settlement("s1", "settled") : row); },
    acknowledgeRefundSettlementFailure: async (input) => { acknowledged.push(input); },
  }));
  await adapter.refresh();
  assert.deepEqual(adapter.getState().refundSettlements.map((row) => row.attemptId), ["s1", "s2"]);

  await adapter.checkRefundSettlements();
  assert.equal(checks, 1);
  assert.deepEqual(adapter.getState().refundSettlements.map((row) => row.attemptId), ["s2"]);
  assert.equal(adapter.getState().action, "idle");

  await adapter.acknowledgeRefundSettlementFailure({ attemptId: "s2", note: "Cash" });
  assert.deepEqual(acknowledged, [{ attemptId: "s2", note: "Cash" }]);

  // 结算列表读取失败不影响异常支付列表。
  const failing = new PaymentRecoveryCenterAdapter(ports({ listRefundSettlements: async () => { throw new Error("db"); } }));
  await failing.refresh();
  assert.equal(failing.getState().records.length, 1);
  assert.deepEqual(failing.getState().refundSettlements, []);
  assert.equal(failing.getState().errorCode, null);

  const legacy = new PaymentRecoveryCenterAdapter(ports());
  await assert.rejects(legacy.checkRefundSettlements(), /PAYMENT_RECOVERY_PROVIDER_UNAVAILABLE/);
  await assert.rejects(legacy.acknowledgeRefundSettlementFailure({ attemptId: "s2", note: "Cash" }), /PAYMENT_RECOVERY_PROVIDER_UNAVAILABLE/);
});
