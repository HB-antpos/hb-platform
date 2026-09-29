import assert from "node:assert/strict";
import test from "node:test";
import type { PaymentAttempt } from "@/core/contracts";
import { ManualCardPaymentAdapter } from "./manual-card-payment-adapter";

const attempt: PaymentAttempt = {
  attemptId: "attempt-1", idempotencyKey: "key-1", orderGuid: "order-1",
  provider: "manual-card", operation: "purchase", amount: { currency: "AUD", cents: 1234 },
  state: "Submitted", createdAtIso: "2026-09-15T00:00:00Z", updatedAtIso: "2026-09-15T00:00:00Z", lastErrorCode: null,
  references: { txnRef: "MANUAL:attempt-1", checkoutId: null, paymentId: null, sessionId: null, rfn: null, voucherReservationToken: null },
};

test("手动确认只重放绑定本attempt的确认事实，并输出Manual同步证据", async () => {
  const adapter = new ManualCardPaymentAdapter();
  const submitted = await adapter.submit(attempt);
  assert.equal(submitted.state, "Approved");
  assert.equal(submitted.protectedSyncEvidence?.processor, "Manual");
  assert.equal(submitted.protectedSyncEvidence?.txnRef, "MANUAL:attempt-1");
  assert.deepEqual(await adapter.recover({ ...attempt, state: "Unknown" }), submitted);
});

test("无确认、其他attempt引用及集成卡不能凭空批准；取消退款不可抹掉收款", async () => {
  const adapter = new ManualCardPaymentAdapter();
  for (const invalid of [
    { ...attempt, references: { ...attempt.references, txnRef: null } },
    { ...attempt, references: { ...attempt.references, txnRef: "MANUAL:another-attempt" } },
    { ...attempt, provider: "square" as const },
    { ...attempt, operation: "refund" as const },
    { ...attempt, amount: { currency: "AUD" as const, cents: 0 } },
  ]) assert.equal((await adapter.recover(invalid)).state, "Unknown");
  assert.equal((await adapter.cancel(attempt)).state, "Unknown");
  assert.equal((await adapter.refund(attempt)).state, "Unknown");
});
