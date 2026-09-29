import assert from "node:assert/strict";
import test from "node:test";

import {
  ProductionReturnRefundAdapter,
  type ReturnCapacityVaultReadPort,
} from "./production-return-refund-adapter";

import type { PaymentAttempt } from "@/core/contracts";
import { paymentProviderAmountCents } from "@hb/pos-payments-core/features/payments/payment-amount";
import type {
  PaymentAttemptExecutionResult,
  PaymentAttemptService,
  PaymentProviderRegistryPort,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";
import type { DurableVoucherPreparationService } from "@/features/payments/runtime/voucher-preparation";


const returnOrderGuid = "return-order-1";
const originalOrderGuid = "original-order-1";

test("Square prepare 只创建 Created attempt，不触碰 provider，并用 capacityId 注入受信任 seed", async () => {
  const payments = new FakePayments(attempt("Created", "square"));
  const adapter = createAdapter({ payments, context: squareContext() });

  const prepared = await adapter.prepareAttempt(input());

  assert.deepEqual(prepared, {
    attemptKind: "payment-provider", externalActionId: "external-1", durableAttemptId: "attempt-1",
  });
  assert.deepEqual(payments.prepareInputs, [{
    actionId: "external-1", orderGuid: returnOrderGuid, provider: "square", operation: "refund",
    amount: { currency: "AUD", cents: -500 }, actor: auditActor(),
    refundCapacityId: "capacity-1",
  }]);
  assert.equal(payments.startInputs.length, 0);
  assert.deepEqual(await adapter.trustedRefundReferenceSeed(seedInput("square")), {
    provider: "square", paymentId: "payment-1",
  });
});

test("Linkly seed 只交付 RFN；provider/context 冲突、无小票卡和非负退款均拒绝", async () => {
  const adapter = createAdapter({ context: linklyContext() });
  assert.deepEqual(await adapter.trustedRefundReferenceSeed(seedInput("linkly-cloud")), {
    provider: "linkly-cloud", rfn: "RFN-1",
  });
  await assert.rejects(() => adapter.prepareAttempt({ ...input(), originalOrderGuid: null }));
  await assert.rejects(() => adapter.prepareAttempt({ ...input(), signedAmountCents: 0 }));
  await assert.rejects(() => adapter.trustedRefundReferenceSeed(seedInput("square")));
  await assert.rejects(() => adapter.trustedRefundReferenceSeed({
    ...seedInput("linkly-cloud"),
    operation: "purchase",
  } as never));
});

test("手工刷卡额度（manual-card context）原卡退款在创建 attempt 前即拒绝", async () => {
  const payments = new FakePayments(attempt("Created", "square"));
  const adapter = createAdapter({
    payments,
    context: { version: 1, provider: "manual-card" },
  });
  await assert.rejects(
    () => adapter.prepareAttempt(input()),
    (error: unknown) => (error as { code?: unknown }).code === "REFUND_CONTEXT_INVALID",
  );
  assert.equal(payments.prepareInputs.length, 0);
});

test("Voucher 先耐久 prepareRefund，再以空 provider reference 创建退款 attempt", async () => {
  const payments = new FakePayments(attempt("Created", "voucher"));
  const voucher = new FakeVoucherPreparation();
  const adapter = createAdapter({ payments, voucher, method: "voucher", context: voucherContext() });

  await adapter.prepareAttempt({ ...input(), method: "voucher" });

  assert.deepEqual(voucher.inputs, [{
    actionId: "external-1", orderGuid: returnOrderGuid, refundReason: "RETURN_REFUND",
  }]);
  assert.equal(payments.prepareInputs[0]?.provider, "voucher");
  assert.equal(payments.prepareInputs[0]?.amount.cents, -500);
  assert.equal(JSON.stringify(payments.prepareInputs[0]).includes("voucher-original-secret"), false);
});

test("submit/recover 只使用已绑定 attempt，并如实映射 Approved、Cancelled 与 Unknown", async () => {
  const payments = new FakePayments(attempt("Created", "square"));
  payments.startResult = result(attempt("Approved", "square"));
  const adapter = createAdapter({ payments, context: squareContext() });
  const bound = boundInput();

  assert.deepEqual(await adapter.submit(bound), { status: "completed" });
  assert.equal(payments.startInputs.length, 1);
  assert.equal(payments.startInputs[0]?.amount.cents, -500);
  assert.equal(
    paymentProviderAmountCents(
      "refund",
      payments.startInputs[0]?.amount ?? { currency: "AUD", cents: 0 },
    ),
    500,
  );

  payments.current = attempt("Unknown", "square");
  payments.recoverResult = result(attempt("Unknown", "square"));
  assert.deepEqual(await adapter.recover({ ...bound, protectedRecoveryKey: null }), {
    status: "unknown", protectedRecoveryKey: null,
  });
  assert.deepEqual(payments.recoverIds, ["attempt-1"]);

  payments.current = attempt("Cancelled", "square");
  payments.startResult = result(attempt("Cancelled", "square"));
  assert.deepEqual(await adapter.submit(bound), { status: "declined" });
});

test("已有 attempt 必须匹配绑定真相；不会因 provider 或 amount 漂移而切换退款", async () => {
  const payments = new FakePayments(attempt("Approved", "linkly-cloud"));
  const adapter = createAdapter({ payments, context: squareContext() });
  await assert.rejects(() => adapter.submit(boundInput()));
  assert.equal(payments.startInputs.length, 0);
});

class FakePayments implements Pick<PaymentAttemptService, "prepareAttempt" | "startAttempt" | "recoverAttempt" | "getAttempt"> {
  public prepareInputs: Parameters<PaymentAttemptService["prepareAttempt"]>[0][] = [];
  public startInputs: Parameters<PaymentAttemptService["startAttempt"]>[0][] = [];
  public recoverIds: string[] = [];
  public startResult: PaymentAttemptExecutionResult;
  public recoverResult: PaymentAttemptExecutionResult;
  public constructor(public current: PaymentAttempt) {
    this.startResult = result(current);
    this.recoverResult = result(current);
  }
  public async prepareAttempt(value: Parameters<PaymentAttemptService["prepareAttempt"]>[0]): Promise<PaymentAttemptExecutionResult> { this.prepareInputs.push(value); return result(this.current); }
  public async startAttempt(value: Parameters<PaymentAttemptService["startAttempt"]>[0]): Promise<PaymentAttemptExecutionResult> { this.startInputs.push(value); return this.startResult; }
  public async recoverAttempt(attemptId: string): Promise<PaymentAttemptExecutionResult> { this.recoverIds.push(attemptId); return this.recoverResult; }
  public async getAttempt(): Promise<PaymentAttempt | null> { return this.current; }
}

class FakeVoucherPreparation implements Pick<DurableVoucherPreparationService, "prepareRefund"> {
  public inputs: unknown[] = [];
  public async prepareRefund(input: unknown): Promise<{ prepared: true }> { this.inputs.push(input); return { prepared: true }; }
}

function createAdapter(inputOverrides: Partial<{
  payments: FakePayments;
  voucher: FakeVoucherPreparation;
  method: "card" | "voucher";
  context: Readonly<Record<string, unknown>>;
}> = {}): ProductionReturnRefundAdapter {
  const method = inputOverrides.method ?? "card";
  const context = inputOverrides.context ?? squareContext();
  return new ProductionReturnRefundAdapter({
    paymentAttempts: inputOverrides.payments ?? new FakePayments(attempt("Created", method === "voucher" ? "voucher" : "square")),
    capacityVault: new FakeVault(method, context),
    providers: providers(),
    voucherPreparation: inputOverrides.voucher ?? new FakeVoucherPreparation(),
  });
}

class FakeVault implements ReturnCapacityVaultReadPort {
  public constructor(private readonly method: "card" | "voucher", private readonly context: Readonly<Record<string, unknown>>) {}
  public async get() { return { capacityId: "capacity-1", originalOrderGuid, method: this.method, originalAmountCents: 500, remainingAmountCents: 500, observedAtIso: "2026-07-28T00:00:00.000Z" } as const; }
  public async resolveProtectedContext() { return this.context; }
}

function providers(): PaymentProviderRegistryPort {
  return { get(provider) { return { provider, submit: async () => providerResult("Approved"), recover: async () => providerResult("Approved"), cancel: async () => providerResult("Cancelled"), refund: async () => providerResult("Approved") }; } };
}

function input() { return { actionId: "return-action-1", allocationId: "allocation-1", externalAttemptId: "external-1", returnOrderGuid, actor: auditActor(), method: "card" as const, signedAmountCents: -500, capacityId: "capacity-1", originalOrderGuid }; }
function boundInput() { return { ...input(), attemptKind: "payment-provider" as const, externalActionId: "external-1", durableAttemptId: "attempt-1" }; }
function seedInput(provider: "square" | "linkly-cloud") { return { identity: { attemptId: "attempt-1", idempotencyKey: "idempotency-1", orderGuid: returnOrderGuid, createdAtIso: "2026-07-28T00:00:00.000Z" }, provider, operation: "refund" as const, action: { orderGuid: returnOrderGuid, actionId: "external-1", requestSignature: "signature", attemptId: "attempt-1", idempotencyKey: "idempotency-1", createdAtIso: "2026-07-28T00:00:00.000Z", actor: auditActor() }, capacity: { capacityId: "capacity-1", actionId: "external-1", orderGuid: returnOrderGuid, provider, operation: "refund" as const, amount: { currency: "AUD" as const, cents: -500 } } }; }
function auditActor() { return { cashierId: "cashier-alice", cashierName: "Alice", userGuid: "user-alice" } as const; }
function attempt(state: PaymentAttempt["state"], provider: PaymentAttempt["provider"]): PaymentAttempt { return { attemptId: "attempt-1", idempotencyKey: "idempotency-1", orderGuid: returnOrderGuid, provider, operation: "refund", amount: { currency: "AUD", cents: -500 }, state, references: { checkoutId: null, paymentId: null, sessionId: null, txnRef: null, rfn: null, voucherReservationToken: null }, createdAtIso: "2026-07-28T00:00:00.000Z", updatedAtIso: "2026-07-28T00:00:00.000Z", lastErrorCode: null }; }
function result(value: PaymentAttempt): PaymentAttemptExecutionResult { return { attempt: value, receiptText: null, responseCode: null }; }
function providerResult(state: "Approved" | "Cancelled") { return { state, references: { checkoutId: null, paymentId: null, sessionId: null, txnRef: null, rfn: null, voucherReservationToken: null }, receiptText: null, responseCode: null }; }
function squareContext() { return { version: 1, provider: "square", paymentId: "payment-1" }; }
function linklyContext() { return { version: 1, provider: "linkly-cloud", rfn: "RFN-1", originalReference: "ANZCLOUD:original-1" }; }
function voucherContext() { return { version: 1, provider: "voucher" }; }
