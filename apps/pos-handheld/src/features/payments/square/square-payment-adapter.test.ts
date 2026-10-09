import assert from "node:assert/strict";
import test from "node:test";

import { SquarePaymentAdapter } from "./square-payment-adapter";

import type {
  HbposEnvelope,
  HbposTransport,
  HbposTransportRequest,
  HbposTransportResponse,
} from "@/core/api";
import type {
  PaymentAttempt,
  PaymentProviderReferences,
} from "@/core/contracts";

test("恢复只使用 attempt 冻结的 Sandbox 环境，不随当前 Production 配置漂移", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-sandbox",
      environment: "Sandbox",
      status: "CANCELED",
    }),
  ]);
  const adapter = createAdapter(transport, { environment: "Production" });

  assert.equal(adapter.providerEnvironment, "Production");
  const recovered = await adapter.recover(
    attempt({
      providerEnvironment: "Sandbox",
      state: "Pending",
      references: references({ checkoutId: "checkout-sandbox" }),
    }),
  );

  assert.equal(recovered.state, "Cancelled");
  assert.deepEqual(transport.calls, [
    {
      method: "GET",
      url: "/api/v1/square/checkouts/checkout-sandbox",
      params: { environment: "Sandbox" },
    },
  ]);
});

test("人工结论后 Square 查询缺少 checkout 不创建，已有 checkout 只 GET", async () => {
  const transport = new ScriptedTransport([ok({ checkoutId: "checkout-existing", environment: "Sandbox", status: "PENDING", paymentIds: [] })]);
  const adapter = createAdapter(transport);
  assert.equal((await adapter.queryExistingPayment(attempt({ state: "Unknown" }))).queryVerified, false);
  assert.equal(transport.calls.length, 0);
  assert.equal((await adapter.queryExistingPayment(attempt({ state: "Unknown", references: references({ checkoutId: "checkout-existing" }) }))).queryVerified, true);
  assert.equal(transport.calls.length, 1);
  assert.equal(transport.calls[0]?.method, "GET");
});

test("Square 只读查询失败或身份冲突不能作为人工收款的有效查询证据", async () => {
  for (const responses of [
    [new Error("GET timeout")],
    [{ status: 503, data: { success: false } }],
    [ok({ checkoutId: "other-checkout", environment: "Sandbox", status: "PENDING" })],
    [ok({ checkoutId: "checkout-1", environment: "Sandbox", status: "COMPLETED", paymentIds: ["payment-1"] }), new Error("payment timeout")],
  ]) {
    const result = await createAdapter(new ScriptedTransport(responses)).queryExistingPayment(
      attempt({ state: "Unknown", references: references({ checkoutId: "checkout-1" }) }),
    );
    assert.equal(result.state, "Unknown");
    assert.equal(result.queryVerified, false);
  }
});

test("恢复只使用 attempt 冻结的 Sandbox 环境，不随当前 Production 配置漂移", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-sandbox",
      environment: "Sandbox",
      status: "CANCELED",
    }),
  ]);
  const adapter = createAdapter(transport, { environment: "Production" });

  assert.equal(adapter.providerEnvironment, "Production");
  const recovered = await adapter.recover(
    attempt({
      providerEnvironment: "Sandbox",
      state: "Pending",
      references: references({ checkoutId: "checkout-sandbox" }),
    }),
  );

  assert.equal(recovered.state, "Cancelled");
  assert.deepEqual(transport.calls, [
    {
      method: "GET",
      url: "/api/v1/square/checkouts/checkout-sandbox",
      params: { environment: "Sandbox" },
    },
  ]);
});

test("Square 状态查询、取消、dismiss 与退款统一路由到 attempt 冻结环境", async () => {
  const transport = new ScriptedTransport([
    ok({ checkoutId: "checkout-1", environment: "Sandbox", status: "CANCELED" }),
    ok({ checkoutId: "checkout-1", environment: "Sandbox", status: "CANCELED" }),
    ok({ checkoutId: "checkout-1", environment: "Sandbox", status: "CANCELED" }),
    ok({
      refundId: "refund-1",
      paymentId: "payment-1",
      status: "PENDING",
      amountMoney: { amount: 1_250, currency: "AUD" },
    }),
  ]);
  const adapter = createAdapter(transport, { environment: "Production" });
  const purchase = attempt({
    providerEnvironment: "Sandbox",
    state: "Pending",
    references: references({ checkoutId: "checkout-1" }),
  });

  await adapter.getStatus(purchase);
  await adapter.cancel(purchase);
  await adapter.dismiss(purchase);
  await adapter.refund({
    ...purchase,
    operation: "refund",
    amount: { currency: "AUD", cents: -1_250 },
    references: references({ paymentId: "payment-1" }),
  });

  assert.deepEqual(
    transport.calls.map(
      (call) =>
        call.params?.environment ??
        (call.data as { environment?: string } | undefined)?.environment,
    ),
    ["Sandbox", "Sandbox", "Sandbox", "Sandbox"],
  );
});

test("Square 旧 attempt 缺少冻结环境时所有金融及查询动作失败关闭且零 HTTP", async () => {
  const transport = new ScriptedTransport([]);
  const adapter = createAdapter(transport, { environment: "Production" });
  const legacy = attempt({
    providerEnvironment: null,
    state: "Pending",
    references: references({
      checkoutId: "checkout-legacy",
      paymentId: "payment-legacy",
    }),
  });

  for (const operation of [
    () => adapter.submit(legacy),
    () => adapter.recover(legacy),
    () => adapter.cancel(legacy),
    () => adapter.dismiss(legacy),
    () =>
      adapter.refund({
        ...legacy,
        operation: "refund",
        amount: { currency: "AUD", cents: -1_250 },
      }),
    () => adapter.getStatus(legacy),
  ]) {
    const result = await operation();
    assert.equal(result.state, "Unknown");
    assert.equal(result.responseCode, "SQUARE_ENVIRONMENT_REQUIRED");
  }
  assert.equal(transport.calls.length, 0);
});

test("无 checkout 的重放若当前配置与冻结环境不同则失败关闭", async () => {
  const transport = new ScriptedTransport([]);
  const adapter = createAdapter(transport, { environment: "Production" });

  const result = await adapter.recover(
    attempt({ providerEnvironment: "Sandbox", state: "Submitted" }),
  );

  assert.equal(result.state, "Unknown");
  assert.equal(result.responseCode, "SQUARE_ENVIRONMENT_CONFLICT");
  assert.equal(transport.calls.length, 0);
});

test("Square checkout 响应缺失或错配冻结环境时不得进入 Pending", async () => {
  for (const environment of [undefined, "Production"] as const) {
    const transport = new ScriptedTransport([
      ok({
        checkoutId: "checkout-environment-conflict",
        ...(environment ? { environment } : {}),
        status: "PENDING",
      }),
    ]);

    const result = await createAdapter(transport).getStatus(
      attempt({
        state: "Pending",
        references: references({ checkoutId: "checkout-environment-conflict" }),
      }),
    );

    assert.equal(result.state, "Unknown");
    assert.equal(result.responseCode, "SQUARE_ENVIRONMENT_CONFLICT");
  }
});

test("Square refund 响应缺失或错配冻结环境时不得进入 Pending", async () => {
  for (const environment of [undefined, "Production"] as const) {
    const transport = new ScriptedTransport([
      ok({
        refundId: "refund-environment-conflict",
        ...(environment ? { environment } : {}),
        status: "PENDING",
        paymentId: "payment-original",
        amountMoney: { amount: 500, currency: "AUD" },
      }),
    ]);

    const result = await createAdapter(transport).refund(
      attempt({
        operation: "refund",
        amount: { currency: "AUD", cents: -500 },
        references: references({ paymentId: "payment-original" }),
      }),
    );

    assert.equal(result.state, "Unknown");
    assert.equal(result.responseCode, "SQUARE_ENVIRONMENT_CONFLICT");
  }
});

test("checkout 完成后验证 payment 金额与币种，Approved 同时携带 CheckoutId 和 PaymentId", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-1",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-1"],
    }),
    ok({
      paymentId: "payment-1",
      status: "COMPLETED",
      approvedMoney: { amount: 1_250, currency: "AUD" },
      updatedAt: "2026-07-28T10:11:12+10:00",
      cardBrand: "VISA",
      maskedCardNumber: "****1111",
      authCode: "AUTH-1",
      receiptText: "DO NOT COPY RECEIPT",
      rawPayload: "{\"pan\":\"5555555555554444\"}",
      pan: "5555555555554444",
      token: "square-secret-token",
    }),
  ]);
  const adapter = createAdapter(transport);

  const outcome = await adapter.submit(attempt());

  assert.equal(outcome.state, "Approved");
  assert.equal(outcome.references.checkoutId, "checkout-1");
  assert.equal(outcome.references.paymentId, "payment-1");
  assert.deepEqual(outcome.protectedSyncEvidence, {
    version: 1,
    provider: "square",
    operation: "purchase",
    processor: "Square",
    txnRef: "payment-1",
    authCode: "AUTH-1",
    cardType: "VISA",
    cardBin: null,
    maskedCardNumber: "****1111",
    merchantId: null,
    responseCode: null,
    responseText: "COMPLETED",
    stan: null,
    bankDateTimeIso: "2026-07-28T00:11:12.000Z",
    amountCents: 1_250,
    refundReference: null,
  });
  assert.deepEqual(
    Object.keys(outcome).sort(),
    [
      "protectedSyncEvidence",
      "receiptText",
      "references",
      "responseCode",
      "state",
    ],
  );
  const publicOutcome = JSON.stringify(withoutProtectedEvidence(outcome));
  for (const protectedValue of [
    "AUTH-1",
    "VISA",
    "****1111",
    "2026-07-28T10:11:12+10:00",
    "DO NOT COPY RECEIPT",
    "5555555555554444",
    "square-secret-token",
  ]) {
    assert.equal(publicOutcome.includes(protectedValue), false);
    assert.equal(
      JSON.stringify(outcome.protectedSyncEvidence).includes(protectedValue),
      protectedValue === "AUTH-1" ||
        protectedValue === "VISA" ||
        protectedValue === "****1111",
    );
  }
  assert.deepEqual(transport.calls, [
    {
      method: "POST",
      url: "/api/v1/square/checkouts",
      data: {
        environment: "Sandbox",
        idempotencyKey: "idempotency-1",
        deviceId: "terminal-1",
        locationId: "location-1",
        amountMoney: { amount: 1_250, currency: "AUD" },
        referenceId: "order-1",
        note: "HB POS Handheld order-1",
      },
    },
    {
      method: "GET",
      url: "/api/v1/square/payments/payment-1",
      params: { environment: "Sandbox" },
    },
  ]);
  assert.equal(JSON.stringify(transport.calls).toLowerCase().includes("access"), false);
  assert.equal(JSON.stringify(transport.calls).toLowerCase().includes("token"), false);
});

test("PENDING checkout 只返回 Pending；recover 按 CheckoutId 查询并验证 PaymentId", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-1",
      environment: "Production",
      status: "PENDING",
    }),
    ok({
      checkoutId: "checkout-1",
      environment: "Production",
      status: "COMPLETED",
      payment: {
        paymentId: "payment-1",
        status: "COMPLETED",
        approvedMoney: { amount: 1_250, currency: "AUD" },
      },
    }),
    ok({
      paymentId: "payment-1",
      status: "COMPLETED",
      totalMoney: { amount: 1_250, currency: "AUD" },
    }),
  ]);
  const adapter = createAdapter(transport, { environment: "Production" });

  const pending = await adapter.submit(
    attempt({ providerEnvironment: "Production" }),
  );
  assert.equal(pending.state, "Pending");
  assert.equal(pending.references.checkoutId, "checkout-1");
  assert.equal("protectedSyncEvidence" in pending, false);

  const recovered = await adapter.recover(
    attempt({
      state: "Pending",
      providerEnvironment: "Production",
      references: pending.references,
    }),
  );
  assert.equal(recovered.state, "Approved");
  assert.equal(recovered.references.checkoutId, "checkout-1");
  assert.equal(recovered.references.paymentId, "payment-1");
  assert.deepEqual(recovered.protectedSyncEvidence, {
    version: 1,
    provider: "square",
    operation: "purchase",
    processor: "Square",
    txnRef: "payment-1",
    authCode: null,
    cardType: null,
    cardBin: null,
    maskedCardNumber: null,
    merchantId: null,
    responseCode: null,
    responseText: "COMPLETED",
    stan: null,
    bankDateTimeIso: null,
    amountCents: 1_250,
    refundReference: null,
  });
  assert.equal(transport.calls[1]?.url, "/api/v1/square/checkouts/checkout-1");
  assert.equal(transport.calls[2]?.url, "/api/v1/square/payments/payment-1");
});

test("Sandbox 成功测试终端拒绝超过 25.00 的付款且不会创建 checkout", async () => {
  const transport = new ScriptedTransport([]);
  const adapter = createAdapter(transport, {
    environment: "Sandbox",
    deviceId: "device:9fa747a2-25ff-48ee-b078-04381f7c828f",
  });

  const result = await adapter.submit(
    attempt({ amount: { currency: "AUD", cents: 2_501 } }),
  );

  assert.equal(result.state, "Declined");
  assert.equal(
    result.responseCode,
    "SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED",
  );
  assert.equal(transport.calls.length, 0);
});

test("已有 CheckoutId 的恢复只依赖 environment，不被后来缺失的终端设置阻断", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-existing",
      environment: "Sandbox",
      status: "CANCELED",
    }),
  ]);
  const adapter = createAdapter(transport, { deviceId: "", locationId: "" });

  const recovered = await adapter.recover(
    attempt({
      state: "Pending",
      references: references({ checkoutId: "checkout-existing" }),
    }),
  );

  assert.equal(recovered.state, "Cancelled");
  assert.deepEqual(transport.calls[0], {
    method: "GET",
    url: "/api/v1/square/checkouts/checkout-existing",
    params: { environment: "Sandbox" },
  });
});

test("recoverWithControl 为 checkout 创建及 payment 查询逐次重算 signal/timeout", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-abortable-create",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-abortable-create"],
    }),
    ok({
      paymentId: "payment-abortable-create",
      status: "COMPLETED",
      approvedMoney: { amount: 1_250, currency: "AUD" },
    }),
  ]);
  const controller = new AbortController();
  const originalNow = Date.now;
  const startedAtMs = Date.parse("2026-08-09T00:00:00.000Z");
  const deadlineAtMs = startedAtMs + 20_000;
  const nowValues = [startedAtMs, startedAtMs + 7_000];
  Date.now = () => nowValues.shift() ?? startedAtMs + 7_000;

  try {
    const recovered = await createAdapter(transport).recoverWithControl(
      attempt({ state: "Unknown" }),
      { signal: controller.signal, deadlineAtMs },
    );

    assert.equal(recovered.state, "Approved");
    assert.equal(transport.calls.length, 2);
    assert.equal(transport.calls[0]?.timeoutMs, 15_000);
    assert.equal(transport.calls[1]?.timeoutMs, 13_000);
    for (const request of transport.calls) {
      assert.equal(request.signal, controller.signal);
    }
  } finally {
    Date.now = originalNow;
  }
});

test("recoverWithControl 将 deadline 传给 checkout 状态和 refund，已取消或到期时零 transport", async () => {
  const checkoutTransport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-abortable-status",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-abortable-status"],
    }),
    ok({
      paymentId: "payment-abortable-status",
      status: "COMPLETED",
      approvedMoney: { amount: 1_250, currency: "AUD" },
    }),
  ]);
  const controller = new AbortController();
  const deadlineAtMs = Date.now() + 10_000;
  const checkoutRecovered = await createAdapter(checkoutTransport).recoverWithControl(
    attempt({
      state: "Pending",
      references: references({ checkoutId: "checkout-abortable-status" }),
    }),
    { signal: controller.signal, deadlineAtMs },
  );
  assert.equal(checkoutRecovered.state, "Approved");
  for (const request of checkoutTransport.calls) {
    assert.equal(request.signal, controller.signal);
    assert.ok((request.timeoutMs ?? 0) > 0);
    assert.ok((request.timeoutMs ?? 0) <= 10_000);
  }

  const refundTransport = new ScriptedTransport([
    ok({
      refundId: "refund-abortable",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentId: "payment-original",
      amountMoney: { amount: 500, currency: "AUD" },
    }),
  ]);
  const refundRecovered = await createAdapter(refundTransport).recoverWithControl(
    attempt({
      operation: "refund",
      amount: { currency: "AUD", cents: -500 },
      state: "Unknown",
      references: references({ paymentId: "payment-original" }),
    }),
    { signal: controller.signal, deadlineAtMs },
  );
  assert.equal(refundRecovered.state, "Approved");
  assert.equal(refundTransport.calls[0]?.signal, controller.signal);
  assert.ok((refundTransport.calls[0]?.timeoutMs ?? 0) > 0);

  const aborted = new AbortController();
  aborted.abort();
  const cancelledTransport = new ScriptedTransport([]);
  const cancelled = await createAdapter(cancelledTransport).recoverWithControl(
    attempt({
      state: "Pending",
      references: references({ checkoutId: "checkout-cancelled" }),
    }),
    { signal: aborted.signal, deadlineAtMs },
  );
  assert.equal(cancelled.state, "Unknown");
  assert.equal(cancelledTransport.calls.length, 0);

  const expiredTransport = new ScriptedTransport([]);
  const expired = await createAdapter(expiredTransport).recoverWithControl(
    attempt({
      state: "Pending",
      references: references({ checkoutId: "checkout-expired" }),
    }),
    { signal: new AbortController().signal, deadlineAtMs: Date.now() },
  );
  assert.equal(expired.state, "Unknown");
  assert.equal(expiredTransport.calls.length, 0);
});

test("payment 已返回 Approved 时即使 JS 越过 deadline 仍保留批准结果", async () => {
  const originalNow = Date.now;
  const startedAtMs = Date.parse("2026-08-09T01:00:00.000Z");
  const deadlineAtMs = startedAtMs + 5_000;
  let now = startedAtMs;
  Date.now = () => now;
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-approved-race",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-approved-race"],
    }),
    () => {
      now = deadlineAtMs;
      return ok({
        paymentId: "payment-approved-race",
        status: "COMPLETED",
        approvedMoney: { amount: 1_250, currency: "AUD" },
      });
    },
  ]);

  try {
    const recovered = await createAdapter(transport).recoverWithControl(
      attempt({ state: "Unknown" }),
      { signal: new AbortController().signal, deadlineAtMs },
    );
    assert.equal(recovered.state, "Approved");
    assert.equal(transport.calls.length, 2);
  } finally {
    Date.now = originalNow;
  }
});

test("90 秒后的手动恢复不附加 signal/timeout，并复用同一 attempt 幂等键", async () => {
  const source = attempt({
    state: "Unknown",
    idempotencyKey: "manual-after-deadline-key",
  });
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-manual-after-deadline",
      environment: "Sandbox",
      status: "PENDING",
    }),
  ]);

  await createAdapter(transport).recover(source);

  assert.equal(transport.calls.length, 1);
  assert.equal(transport.calls[0]?.signal, undefined);
  assert.equal(transport.calls[0]?.timeoutMs, undefined);
  assert.equal(
    (transport.calls[0]?.data as { idempotencyKey: string }).idempotencyKey,
    source.idempotencyKey,
  );
});

test("checkout 完成但 payment FAILED 映射 Declined，且仍保留两个 provider ID", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-declined",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-declined"],
    }),
    ok({
      paymentId: "payment-declined",
      status: "FAILED",
      approvedMoney: { amount: 1_250, currency: "AUD" },
    }),
  ]);

  const result = await createAdapter(transport).submit(attempt());

  assert.equal(result.state, "Declined");
  assert.equal(result.responseCode, "SQUARE_PAYMENT_FAILED");
  assert.equal(result.references.checkoutId, "checkout-declined");
  assert.equal(result.references.paymentId, "payment-declined");
  assert.equal("protectedSyncEvidence" in result, false);
});

test("取消和 dismiss 仅使用已知 CheckoutId，且不会重建 checkout", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-1",
      environment: "Sandbox",
      status: "CANCELED",
      cancelReason: "SELLER_CANCELED",
    }),
    ok({
      checkoutId: "checkout-2",
      environment: "Sandbox",
      status: "CANCELED",
    }),
  ]);
  const adapter = createAdapter(transport);

  const cancelled = await adapter.cancel(
    attempt({
      state: "Pending",
      references: references({ checkoutId: "checkout-1" }),
    }),
  );
  const dismissed = await adapter.dismiss(
    attempt({
      state: "Pending",
      references: references({ checkoutId: "checkout-2" }),
    }),
  );

  assert.equal(cancelled.state, "Cancelled");
  assert.equal(dismissed.state, "Cancelled");
  assert.equal("protectedSyncEvidence" in cancelled, false);
  assert.equal("protectedSyncEvidence" in dismissed, false);
  assert.deepEqual(transport.calls, [
    {
      method: "POST",
      url: "/api/v1/square/checkouts/checkout-1/cancel",
      data: { environment: "Sandbox" },
    },
    {
      method: "POST",
      url: "/api/v1/square/checkouts/checkout-2/dismiss",
      data: { environment: "Sandbox" },
    },
  ]);
});

test("create 超时或传输失败只返回 Unknown，本次调用绝不自动重发", async () => {
  const transport = new ScriptedTransport([
    new Error("timeout"),
  ]);
  const adapter = createAdapter(transport);

  const result = await adapter.submit(attempt());

  assert.equal(result.state, "Unknown");
  assert.equal(result.responseCode, "SQUARE_TRANSPORT_ERROR");
  assert.equal("protectedSyncEvidence" in result, false);
  assert.equal(transport.calls.length, 1);
  assert.equal(transport.calls[0]?.url, "/api/v1/square/checkouts");
});

test("create 响应丢失后的 recover 只能以同一 attempt.idempotencyKey 重放 create", async () => {
  const transport = new ScriptedTransport([
    new Error("response lost"),
    ok({
      checkoutId: "checkout-replayed",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-replayed"],
    }),
    ok({
      paymentId: "payment-replayed",
      status: "COMPLETED",
      approvedMoney: { amount: 1_250, currency: "AUD" },
    }),
  ]);
  const adapter = createAdapter(transport);
  const original = attempt({ idempotencyKey: "durable-key-1" });

  const unknown = await adapter.submit(original);
  const recovered = await adapter.recover({
    ...original,
    state: "Unknown",
    references: unknown.references,
  });

  assert.equal(unknown.state, "Unknown");
  assert.equal(recovered.state, "Approved");
  assert.equal("protectedSyncEvidence" in unknown, false);
  assert.equal(
    recovered.protectedSyncEvidence?.txnRef,
    "payment-replayed",
  );
  const createCalls = transport.calls.filter(
    (request) => request.method === "POST" && request.url === "/api/v1/square/checkouts",
  );
  assert.equal(createCalls.length, 2);
  assert.equal(
    (createCalls[0]?.data as { idempotencyKey: string }).idempotencyKey,
    "durable-key-1",
  );
  assert.equal(
    (createCalls[1]?.data as { idempotencyKey: string }).idempotencyKey,
    "durable-key-1",
  );
});

test("COMPLETED checkout 缺少 PaymentId 或 payment 金额不一致时保持 Unknown", async () => {
  const missingPayment = new ScriptedTransport([
    ok({
      checkoutId: "checkout-1",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: [],
    }),
  ]);
  const mismatch = new ScriptedTransport([
    ok({
      checkoutId: "checkout-2",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-2"],
    }),
    ok({
      paymentId: "payment-2",
      status: "COMPLETED",
      approvedMoney: { amount: 1_251, currency: "AUD" },
    }),
  ]);

  const missing = await createAdapter(missingPayment).submit(attempt());
  const wrongAmount = await createAdapter(mismatch).submit(attempt());

  assert.equal(missing.state, "Unknown");
  assert.equal(missing.references.checkoutId, "checkout-1");
  assert.equal(wrongAmount.state, "Unknown");
  assert.equal(wrongAmount.references.paymentId, "payment-2");
  assert.equal("protectedSyncEvidence" in missing, false);
  assert.equal("protectedSyncEvidence" in wrongAmount, false);
});

test("COMPLETED payment 的身份不匹配或完整 PAN 无法生成证据时 fail closed", async () => {
  const identityMismatch = new ScriptedTransport([
    ok({
      checkoutId: "checkout-identity",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-expected"],
    }),
    ok({
      paymentId: "payment-other",
      status: "COMPLETED",
      approvedMoney: { amount: 1_250, currency: "AUD" },
    }),
  ]);
  const unsafePan = new ScriptedTransport([
    ok({
      checkoutId: "checkout-pan",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-pan"],
    }),
    ok({
      paymentId: "payment-pan",
      status: "COMPLETED",
      approvedMoney: { amount: 1_250, currency: "AUD" },
      maskedCardNumber: "4111111111111111",
    }),
  ]);

  const mismatched = await createAdapter(identityMismatch).submit(attempt());
  const unsafe = await createAdapter(unsafePan).submit(attempt());

  assert.equal(mismatched.state, "Unknown");
  assert.equal(mismatched.responseCode, "SQUARE_REFERENCE_CONFLICT");
  assert.equal("protectedSyncEvidence" in mismatched, false);
  assert.equal(unsafe.state, "Unknown");
  assert.equal(unsafe.responseCode, "SQUARE_SYNC_EVIDENCE_INVALID");
  assert.equal("protectedSyncEvidence" in unsafe, false);
  assert.equal(
    JSON.stringify(withoutProtectedEvidence(unsafe)).includes("4111111111111111"),
    false,
  );
});

test("首次完成响应出现多个不同 PaymentId 时保持 Unknown，不猜测交易归属", async () => {
  const transport = new ScriptedTransport([
    ok({
      checkoutId: "checkout-ambiguous",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentIds: ["payment-1", "payment-2"],
    }),
  ]);

  const result = await createAdapter(transport).submit(attempt());

  assert.equal(result.state, "Unknown");
  assert.equal(result.responseCode, "SQUARE_REFERENCE_CONFLICT");
  assert.equal(result.references.checkoutId, "checkout-ambiguous");
  assert.equal(result.references.paymentId, null);
  assert.equal(transport.calls.length, 1);
});

test("refund 使用原 PaymentId 与 attempt 幂等键；PENDING 可用同一请求恢复到 COMPLETED", async () => {
  const transport = new ScriptedTransport([
    ok({
      refundId: "refund-1",
      environment: "Sandbox",
      status: "PENDING",
      paymentId: "payment-original",
      amountMoney: { amount: 500, currency: "AUD" },
    }),
    ok({
      refundId: "refund-1",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentId: "payment-original",
      amountMoney: { amount: 500, currency: "AUD" },
      updatedAt: "2026-07-28T08:30:00+10:00",
    }),
  ]);
  const adapter = createAdapter(transport);
  const refundAttempt = attempt({
    operation: "refund",
    amount: { currency: "AUD", cents: -500 },
    references: references({ paymentId: "payment-original" }),
  });

  const pending = await adapter.refund(refundAttempt);
  const completed = await adapter.recover({
    ...refundAttempt,
    state: "Pending",
    references: pending.references,
  });

  assert.equal(pending.state, "Pending");
  assert.equal("protectedSyncEvidence" in pending, false);
  assert.equal(
    JSON.stringify(withoutProtectedEvidence(pending)).includes("refund-1"),
    false,
  );
  assert.equal(completed.state, "Approved");
  assert.equal(completed.references.paymentId, "payment-original");
  assert.deepEqual(completed.protectedSyncEvidence, {
    version: 1,
    provider: "square",
    operation: "refund",
    processor: "Square",
    txnRef: "payment-original",
    authCode: null,
    cardType: null,
    cardBin: null,
    maskedCardNumber: null,
    merchantId: null,
    responseCode: null,
    responseText: "COMPLETED",
    stan: null,
    bankDateTimeIso: "2026-07-27T22:30:00.000Z",
    amountCents: 500,
    refundReference: "refund-1",
  });
  assert.equal(
    JSON.stringify(withoutProtectedEvidence(completed)).includes("refund-1"),
    false,
  );
  assert.equal(transport.calls.length, 2);
  for (const call of transport.calls) {
    assert.equal(call.url, "/api/v1/square/refunds");
    assert.deepEqual(call.data, {
      environment: "Sandbox",
      idempotencyKey: "idempotency-1",
      paymentId: "payment-original",
      amountMoney: { amount: 500, currency: "AUD" },
    });
  }
});

test("refund 缺少原 PaymentId、响应交易换绑或 REJECTED 时不会误报成功", async () => {
  const noRequest = new ScriptedTransport([]);
  const missing = await createAdapter(noRequest).refund(
    attempt({
      operation: "refund",
      amount: { currency: "AUD", cents: -500 },
    }),
  );
  assert.equal(missing.state, "Unknown");
  assert.equal(noRequest.calls.length, 0);

  const rejectedTransport = new ScriptedTransport([
    ok({
      refundId: "refund-rejected",
      environment: "Sandbox",
      status: "REJECTED",
      paymentId: "payment-original",
      amountMoney: { amount: 500, currency: "AUD" },
    }),
  ]);
  const rejected = await createAdapter(rejectedTransport).refund(
    attempt({
      operation: "refund",
      amount: { currency: "AUD", cents: -500 },
      references: references({ paymentId: "payment-original" }),
    }),
  );
  assert.equal(rejected.state, "Declined");
  assert.equal(rejected.references.paymentId, "payment-original");
  assert.equal("protectedSyncEvidence" in rejected, false);
});

test("COMPLETED refund 必须绑定 PaymentId、退款引用和精确金额，否则保持 Unknown", async () => {
  const scenarios = [
    {
      response: {
        refundId: "refund-missing-payment",
        environment: "Sandbox",
        status: "COMPLETED",
        amountMoney: { amount: 500, currency: "AUD" },
      },
      code: "SQUARE_REFUND_REFERENCE_CONFLICT",
    },
    {
      response: {
        refundId: "refund-missing-money",
        environment: "Sandbox",
        status: "COMPLETED",
        paymentId: "payment-original",
      },
      code: "SQUARE_REFUND_VERIFICATION_FAILED",
    },
    {
      response: {
        refundId: "refund-other-payment",
        environment: "Sandbox",
        status: "COMPLETED",
        paymentId: "payment-other",
        amountMoney: { amount: 500, currency: "AUD" },
      },
      code: "SQUARE_REFUND_REFERENCE_CONFLICT",
    },
    {
      response: {
        refundId: "refund-unsafe\u0000reference",
        environment: "Sandbox",
        status: "COMPLETED",
        paymentId: "payment-original",
        amountMoney: { amount: 500, currency: "AUD" },
      },
      code: "SQUARE_SYNC_EVIDENCE_INVALID",
    },
  ] as const;

  for (const scenario of scenarios) {
    const transport = new ScriptedTransport([ok(scenario.response)]);
    const result = await createAdapter(transport).refund(
      attempt({
        operation: "refund",
        amount: { currency: "AUD", cents: -500 },
        references: references({ paymentId: "payment-original" }),
      }),
    );

    assert.equal(result.state, "Unknown");
    assert.equal(result.responseCode, scenario.code);
    assert.equal("protectedSyncEvidence" in result, false);
  }
});

test("refund 响应丢失后仅以同一幂等 attempt 恢复，并只在 Approved 携带证据", async () => {
  const transport = new ScriptedTransport([
    new Error("response lost"),
    ok({
      refundId: "refund-replayed",
      environment: "Sandbox",
      status: "COMPLETED",
      paymentId: "payment-original",
      amountMoney: { amount: 500, currency: "AUD" },
    }),
  ]);
  const adapter = createAdapter(transport);
  const original = attempt({
    idempotencyKey: "refund-durable-key",
    operation: "refund",
    amount: { currency: "AUD", cents: -500 },
    references: references({ paymentId: "payment-original" }),
  });

  const unknown = await adapter.refund(original);
  const recovered = await adapter.recover({
    ...original,
    state: "Unknown",
    references: unknown.references,
  });

  assert.equal(unknown.state, "Unknown");
  assert.equal("protectedSyncEvidence" in unknown, false);
  assert.equal(recovered.state, "Approved");
  assert.equal(
    recovered.protectedSyncEvidence?.refundReference,
    "refund-replayed",
  );
  assert.equal(
    recovered.protectedSyncEvidence?.txnRef,
    "payment-original",
  );
  for (const call of transport.calls) {
    assert.equal(
      (call.data as { idempotencyKey: string }).idempotencyKey,
      "refund-durable-key",
    );
  }
});

test("refund 零、正数和 MIN_SAFE 金额均在 Square 请求前 fail closed", async () => {
  for (const cents of [0, 500, Number.MIN_SAFE_INTEGER]) {
    const transport = new ScriptedTransport([]);
    const result = await createAdapter(transport).refund(
      attempt({
        operation: "refund",
        amount: { currency: "AUD", cents },
        references: references({ paymentId: "payment-original" }),
      }),
    );

    assert.equal(result.state, "Unknown");
    assert.equal(result.responseCode, "SQUARE_AMOUNT_INVALID");
    assert.equal(transport.calls.length, 0);
  }
});

for (const operation of ["submit", "recover"] as const) {
  test(`${operation} 取得交易标识后 payment 查询失败仍保留标识，后续仅查询原交易`, async () => {
    const checkout = { checkoutId: "checkout-recovered", environment: "Sandbox", status: "COMPLETED", paymentIds: ["payment-recovered"] };
    const transport = new ScriptedTransport([
      ok(checkout),
      new Error("payment response timeout"),
      ok(checkout),
      ok({ paymentId: "payment-recovered", status: "COMPLETED", approvedMoney: { amount: 1_250, currency: "AUD" } }),
    ]);
    const adapter = createAdapter(transport);
    const original = attempt({ state: "Unknown" });
    const failed = await adapter[operation](original);

    assert.equal(failed.state, "Unknown");
    assert.equal(failed.responseCode, "SQUARE_TRANSPORT_ERROR");
    assert.equal(failed.references.checkoutId, "checkout-recovered");
    assert.equal(failed.references.paymentId, "payment-recovered");
    assert.equal(original.references.checkoutId, null);
    assert.equal(failed.protectedSyncEvidence, undefined);

    const recovered = await adapter.getStatus({ ...original, references: failed.references });
    assert.equal(recovered.state, "Approved");
    assert.deepEqual(transport.calls.map(({ method, url }) => ({ method, url })), [
      { method: "POST", url: "/api/v1/square/checkouts" },
      { method: "GET", url: "/api/v1/square/payments/payment-recovered" },
      { method: "GET", url: "/api/v1/square/checkouts/checkout-recovered" },
      { method: "GET", url: "/api/v1/square/payments/payment-recovered" },
    ]);
  });
}

class ScriptedTransport implements HbposTransport {
  public readonly calls: HbposTransportRequest[] = [];

  public constructor(
    private readonly steps: (
      | HbposTransportResponse<unknown>
      | Error
      | (() => HbposTransportResponse<unknown>)
    )[],
  ) {}

  public async request<T>(
    request: HbposTransportRequest,
  ): Promise<HbposTransportResponse<T>> {
    this.calls.push(request);
    const step = this.steps.shift();
    if (!step) throw new Error("Unexpected transport request.");
    if (step instanceof Error) throw step;
    if (typeof step === "function") return step() as HbposTransportResponse<T>;
    return step as HbposTransportResponse<T>;
  }
}

function createAdapter(
  transport: HbposTransport,
  overrides: Partial<{
    environment: string;
    deviceId: string;
    locationId: string;
  }> = {},
) {
  return new SquarePaymentAdapter(
    transport,
    async () => ({
      environment: overrides.environment ?? "Sandbox",
      deviceId: overrides.deviceId ?? "device:terminal-1",
      locationId: overrides.locationId ?? "location-1",
    }),
    overrides.environment ?? "Sandbox",
  );
}

function ok<T>(data: T): HbposTransportResponse<HbposEnvelope<T>> {
  return {
    status: 200,
    data: { success: true, data },
  };
}

function attempt(overrides: Partial<PaymentAttempt> = {}): PaymentAttempt {
  return {
    attemptId: overrides.attemptId ?? "attempt-1",
    idempotencyKey: overrides.idempotencyKey ?? "idempotency-1",
    orderGuid: overrides.orderGuid ?? "order-1",
    provider: overrides.provider ?? "square",
    operation: overrides.operation ?? "purchase",
    amount: overrides.amount ?? { currency: "AUD", cents: 1_250 },
    state: overrides.state ?? "Submitted",
    references: overrides.references ?? references(),
    createdAtIso: overrides.createdAtIso ?? "2026-07-28T00:00:00.000Z",
    updatedAtIso: overrides.updatedAtIso ?? "2026-07-28T00:00:00.001Z",
    lastErrorCode: overrides.lastErrorCode ?? null,
    providerEnvironment:
      overrides.providerEnvironment === undefined
        ? "Sandbox"
        : overrides.providerEnvironment,
  };
}

function references(
  overrides: Partial<PaymentProviderReferences> = {},
): PaymentProviderReferences {
  return {
    checkoutId: overrides.checkoutId ?? null,
    paymentId: overrides.paymentId ?? null,
    sessionId: overrides.sessionId ?? null,
    txnRef: overrides.txnRef ?? null,
    rfn: overrides.rfn ?? null,
    voucherReservationToken: overrides.voucherReservationToken ?? null,
  };
}

function withoutProtectedEvidence(
  result: Readonly<Record<string, unknown>>,
): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(result).filter(([key]) => key !== "protectedSyncEvidence"),
  );
}
