import assert from "node:assert/strict";
import test from "node:test";

import {
  MixedPaymentCoordinator,
  MixedPaymentValidationError,
  type MixedApprovedPaymentCompletionPort,
  type MixedCashTenderPort,
  type MixedPaymentAttemptPort,
  type MixedPaymentOrderTruth,
  type MixedPaymentOrderTruthPort,
  type MixedTenderReversalPort,
} from "./mixed-payment-coordinator";

import type {
  ApprovedPaymentOrderCommitResult,
  AuditActorSnapshot,
  Money,
  OrderTender,
  PaymentAttempt,
  PaymentProvider,
} from "@/core/contracts";
import {
  PaymentAttemptDurabilityError,
  PaymentAttemptOfflineError,
  type PaymentAttemptExecutionResult,
  type StartPaymentAttemptInput,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";

const aud = (cents: number): Money => ({ currency: "AUD", cents });

test("手动刷卡确认标记透传到 PaymentAttemptService 输入并完成入账", async () => {
  const truth = new MemoryTruth(orderTruth(1_000));
  const attempts = new FakeAttempts();
  const completion = new FakeCompletion(truth);
  attempts.startResult = (input) => approvedExecution(input, "attempt-manual");
  const coordinator = createCoordinator({ truth, attempts, completion });

  const result = await coordinator.addOnlineTender({
    actionId: "action-manual",
    orderGuid: "order-1",
    provider: "manual-card",
    amount: aud(1_000),
    manualConfirmed: true,
  });

  assert.equal(attempts.startInputs[0]?.manualConfirmed, true);
  assert.equal(result.status, "completed");
  assert.deepEqual(truth.current.tenders.map((tender) => tender.amount.cents), [1_000]);
});

test("部分卡支付后可追加现金，并只按持久 tender truth 判定完成", async () => {
  const truth = new MemoryTruth(orderTruth(1_000));
  const attempts = new FakeAttempts();
  const completion = new FakeCompletion(truth);
  const cash = new FakeCashTender(truth);
  attempts.startResult = (input) => approvedExecution(input, "attempt-card");
  const coordinator = createCoordinator({ truth, attempts, completion, cash });

  const card = await coordinator.addOnlineTender({
    actionId: "action-card",
    orderGuid: "order-1",
    provider: "square",
    amount: aud(400),
  });
  const paidCash = await coordinator.addCashTender({
    actionId: "action-cash",
    orderGuid: "order-1",
    amount: aud(600),
  });

  assert.deepEqual(
    { status: card.status, remaining: card.remaining.cents },
    { status: "partial", remaining: 600 },
  );
  assert.deepEqual(
    { status: paidCash.status, remaining: paidCash.remaining.cents },
    { status: "completed", remaining: 0 },
  );
  assert.equal(truth.current.state, "PendingSync");
  assert.deepEqual(
    truth.current.tenders.map((tender) => [tender.method, tender.amount.cents]),
    [["card", 400], ["cash", 600]],
  );
});

test("两张卡使用两个独立 action，第二张只扣持久余额并完成同一订单", async () => {
  const truth = new MemoryTruth(orderTruth(1_000));
  const attempts = new FakeAttempts();
  const completion = new FakeCompletion(truth);
  attempts.startResult = (input, index) =>
    approvedExecution(input, `attempt-card-${index}`);
  const coordinator = createCoordinator({ truth, attempts, completion });

  const first = await coordinator.addOnlineTender({
    actionId: "action-card-1",
    orderGuid: "order-1",
    provider: "square",
    amount: aud(350),
  });
  const second = await coordinator.addOnlineTender({
    actionId: "action-card-2",
    orderGuid: "order-1",
    provider: "linkly-cloud",
    amount: aud(650),
  });

  assert.equal(first.status, "partial");
  assert.equal(first.remaining.cents, 650);
  assert.equal(second.status, "completed");
  assert.equal(second.remaining.cents, 0);
  assert.deepEqual(
    attempts.startInputs.map((input) => [
      input.actionId,
      input.provider,
      input.amount.cents,
    ]),
    [
      ["action-card-1", "square", 350],
      ["action-card-2", "linkly-cloud", 650],
    ],
  );
  assert.equal(completion.inputs[0]?.attempt.orderGuid, "order-1");
  assert.equal(completion.inputs[1]?.attempt.orderGuid, "order-1");
  assert.deepEqual(completion.actors, [actor(), actor()]);
});

test("同一 action 的并发重复点击共享一次 attempt 和一次 Approved completion", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  const completion = new FakeCompletion(truth);
  const deferred = createDeferred<PaymentAttemptExecutionResult>();
  attempts.startResult = () => deferred.promise;
  const coordinator = createCoordinator({ truth, attempts, completion });
  const input = {
    actionId: "action-double-click",
    orderGuid: "order-1",
    provider: "square" as const,
    amount: aud(500),
  };

  const first = coordinator.addOnlineTender(input);
  const second = coordinator.addOnlineTender(input);
  await waitUntil(() => attempts.startInputs.length === 1);
  deferred.resolve(approvedExecution({
    actionId: "action-double-click",
    orderGuid: "order-1",
    provider: "square",
    operation: "purchase",
    amount: aud(500),
  }, "attempt-single"));

  const [left, right] = await Promise.all([first, second]);
  assert.equal(left.status, "completed");
  assert.deepEqual(right, left);
  assert.equal(attempts.startInputs.length, 1);
  assert.equal(completion.inputs.length, 1);
});

test("在线 tender 必须是 AUD 正整数分币且不能超过持久余额", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });

  for (const amount of [
    aud(0),
    aud(-1),
    aud(501),
    aud(1.5),
    { currency: "USD", cents: 100 } as unknown as Money,
  ]) {
    await assert.rejects(
      async () => coordinator.addOnlineTender({
        actionId: `invalid-${amount.currency}-${amount.cents}`,
        orderGuid: "order-1",
        provider: "voucher",
        amount,
      }),
      MixedPaymentValidationError,
    );
  }
  assert.equal(attempts.startInputs.length, 0);
});

test("在线支付断网时不写 tender，并返回可恢复结果", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  attempts.startFailure = new PaymentAttemptOfflineError();
  const completion = new FakeCompletion(truth);
  const coordinator = createCoordinator({ truth, attempts, completion });

  const result = await coordinator.addOnlineTender({
    actionId: "action-offline",
    orderGuid: "order-1",
    provider: "square",
    amount: aud(500),
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.errorCode, "ONLINE_REQUIRED");
  assert.equal(result.remaining.cents, 500);
  assert.equal(completion.inputs.length, 0);
  assert.equal(truth.current.tenders.length, 0);
});

test("Unknown attempt 阻止切 provider、加现金和移除 tender，只有显式恢复可继续", async () => {
  const existingTender = tender("tender-existing", "card", 200);
  const truth = new MemoryTruth(orderTruth(1_000, {
    state: "Completing",
    tenders: [existingTender],
  }));
  const attempts = new FakeAttempts();
  attempts.blocking = paymentAttempt({
    attemptId: "attempt-unknown",
    provider: "square",
    amount: aud(300),
    state: "Unknown",
  });
  const cash = new FakeCashTender(truth);
  const reversal = new FakeReversal(truth);
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
    cash,
    reversal,
  });

  const switched = await coordinator.addOnlineTender({
    actionId: "action-switch",
    orderGuid: "order-1",
    provider: "linkly-cloud",
    amount: aud(300),
  });
  const addedCash = await coordinator.addCashTender({
    actionId: "action-cash-blocked",
    orderGuid: "order-1",
    amount: aud(300),
  });
  const removed = await coordinator.removeTender({
    actionId: "action-remove-blocked",
    orderGuid: "order-1",
    tenderGuid: existingTender.tenderGuid,
  });

  assert.equal(switched.status, "unknown");
  assert.equal(addedCash.status, "unknown");
  assert.equal(removed.status, "unknown");
  assert.equal(switched.attemptId, "attempt-unknown");
  assert.equal(attempts.startInputs.length, 0);
  assert.equal(cash.inputs.length, 0);
  assert.equal(reversal.inputs.length, 0);
});

test("Pending attempt 明确阻塞新 tender，不被自动取消或换 provider", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  attempts.blocking = paymentAttempt({
    attemptId: "attempt-pending",
    state: "Pending",
  });
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });

  const result = await coordinator.addOnlineTender({
    actionId: "action-after-pending",
    orderGuid: "order-1",
    provider: "voucher",
    amount: aud(500),
  });

  assert.equal(result.status, "pending");
  assert.equal(result.attemptId, "attempt-pending");
  assert.equal(attempts.startInputs.length, 0);
  assert.equal(attempts.cancelCalls, 0);
});

test("Submitted/Approved 映射 awaiting-terminal，终态恢复映射 declined/cancelled", async () => {
  for (const state of ["Submitted", "Approved"] as const) {
    const truth = new MemoryTruth(orderTruth(500));
    const attempts = new FakeAttempts();
    attempts.blocking = paymentAttempt({
      attemptId: `attempt-${state}`,
      state,
    });
    const coordinator = createCoordinator({
      truth,
      attempts,
      completion: new FakeCompletion(truth),
    });

    const result = await coordinator.addOnlineTender({
      actionId: `action-${state}`,
      orderGuid: "order-1",
      provider: "square",
      amount: aud(500),
    });
    assert.equal(result.status, "awaiting-terminal");
    assert.equal(result.attemptId, `attempt-${state}`);
  }

  for (const state of ["Declined", "Cancelled"] as const) {
    const truth = new MemoryTruth(orderTruth(500));
    const attempts = new FakeAttempts();
    const existing = paymentAttempt({
      attemptId: `attempt-${state}`,
      state,
    });
    attempts.attempts.set(existing.attemptId, existing);
    const coordinator = createCoordinator({
      truth,
      attempts,
      completion: new FakeCompletion(truth),
    });

    const result = await coordinator.recoverOnlineAttempt({
      orderGuid: "order-1",
      attemptId: existing.attemptId,
    });
    assert.equal(result.status, state === "Declined" ? "declined" : "cancelled");
    assert.equal(attempts.recoverInputs.length, 0);
  }
});

test("Sandbox 金额超限的 Declined attempt 保留稳定错误码供支付页提示", async () => {
  const truth = new MemoryTruth(orderTruth(5_000));
  const attempts = new FakeAttempts();
  const declined = paymentAttempt({
    attemptId: "attempt-square-sandbox-limit",
    provider: "square",
    state: "Declined",
    amount: aud(2_501),
    lastErrorCode: "SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED",
  });
  attempts.attempts.set(declined.attemptId, declined);
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });

  const result = await coordinator.recoverOnlineAttempt({
    orderGuid: "order-1",
    attemptId: declined.attemptId,
  });

  assert.equal(result.status, "declined");
  assert.equal(result.errorCode, "SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED");
  assert.equal(attempts.recoverInputs.length, 0);
});

test("Linkly 终端选择冲突的 Declined attempt 保留稳定错误码供支付页刷新确认", async () => {
  const truth = new MemoryTruth(orderTruth(5_000));
  const attempts = new FakeAttempts();
  const declined = paymentAttempt({
    attemptId: "attempt-linkly-selection-conflict",
    provider: "linkly-cloud",
    state: "Declined",
    lastErrorCode: "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  });
  attempts.attempts.set(declined.attemptId, declined);
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });

  const result = await coordinator.recoverOnlineAttempt({
    orderGuid: "order-1",
    attemptId: declined.attemptId,
  });

  assert.equal(result.status, "declined");
  assert.equal(
    result.errorCode,
    "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  );
  assert.equal(attempts.recoverInputs.length, 0);
});

test("Approved completion 异常返回 recovery-required，不能宣称 partial 或 completed", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  attempts.startResult = (input) => approvedExecution(input, "attempt-approved");
  const completion = new FakeCompletion(truth);
  completion.failure = new Error("commit failed");
  const coordinator = createCoordinator({ truth, attempts, completion });

  const result = await coordinator.addOnlineTender({
    actionId: "action-commit-failure",
    orderGuid: "order-1",
    provider: "square",
    amount: aud(500),
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.attemptId, "attempt-approved");
  assert.equal(result.remaining.cents, 500);
  assert.equal(truth.current.tenders.length, 0);
});

test("崩溃恢复只恢复指定 OrderGuid/attempt，不创建新 attempt", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  const pending = paymentAttempt({
    attemptId: "attempt-before-crash",
    state: "Unknown",
    amount: aud(500),
  });
  attempts.attempts.set(pending.attemptId, pending);
  attempts.recoverResult = () => ({
    attempt: { ...pending, state: "Approved" },
    receiptText: null,
    responseCode: "APPROVED",
  });
  const completion = new FakeCompletion(truth);
  const coordinator = createCoordinator({ truth, attempts, completion });

  const result = await coordinator.recoverOnlineAttempt({
    orderGuid: "order-1",
    attemptId: "attempt-before-crash",
  });

  assert.equal(result.status, "completed");
  assert.equal(result.attemptId, "attempt-before-crash");
  assert.deepEqual(attempts.recoverInputs, ["attempt-before-crash"]);
  assert.equal(attempts.startInputs.length, 0);
  assert.equal(completion.inputs[0]?.attempt.orderGuid, "order-1");
  assert.equal(JSON.stringify(result).includes("voucherReservationToken"), false);
  assert.equal(JSON.stringify(result).includes("paymentId"), false);
});

test("recoverOnlineAttempt 原样向支付 attempt 服务传递 deadline 控制", async () => {
  const pending = paymentAttempt({
    attemptId: "attempt-mixed-abortable-recovery",
    state: "Pending",
    references: {
      checkoutId: "checkout-mixed-abortable-recovery",
      paymentId: null,
      sessionId: null,
      txnRef: null,
      rfn: null,
      voucherReservationToken: null,
    },
  });
  const attempts = new SignalAwareFakeAttempts();
  attempts.attempts.set(pending.attemptId, pending);
  attempts.recoverResult = () => ({
    attempt: { ...pending, state: "Unknown" },
    receiptText: null,
    responseCode: "SQUARE_ABORTED",
  });
  const truth = new MemoryTruth(orderTruth(500));
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });
  const controller = new AbortController();
  const deadlineAtMs = Date.parse(pending.createdAtIso) + 90_000;

  const result = await coordinator.recoverOnlineAttempt({
    orderGuid: pending.orderGuid,
    attemptId: pending.attemptId,
    signal: controller.signal,
    deadlineAtMs,
  });

  assert.equal(result.status, "unknown");
  assert.strictEqual(attempts.recoveryControls[0]?.signal, controller.signal);
  assert.equal(attempts.recoveryControls[0]?.deadlineAtMs, deadlineAtMs);
});

test("deadline abort 与 Approved 竞争时仍原子完成一次且不重复 tender", async () => {
  const pending = paymentAttempt({
    attemptId: "attempt-approved-at-deadline",
    state: "Pending",
  });
  const attempts = new SignalAwareFakeAttempts();
  attempts.attempts.set(pending.attemptId, pending);
  const gate = createDeferred<PaymentAttemptExecutionResult>();
  attempts.recoverResult = () => gate.promise;
  const truth = new MemoryTruth(orderTruth(500));
  const completion = new FakeCompletion(truth);
  const coordinator = createCoordinator({ truth, attempts, completion });
  const controller = new AbortController();
  const input = {
    orderGuid: pending.orderGuid,
    attemptId: pending.attemptId,
    signal: controller.signal,
    deadlineAtMs: Date.parse(pending.createdAtIso) + 90_000,
  };

  const first = coordinator.recoverOnlineAttempt(input);
  await waitUntil(() => attempts.recoveryControls.length === 1);
  const sameOwner = coordinator.recoverOnlineAttempt(input);
  controller.abort();
  gate.resolve({
    attempt: { ...pending, state: "Approved" },
    receiptText: null,
    responseCode: "APPROVED",
  });

  const [left, right] = await Promise.all([first, sameOwner]);
  assert.equal(left.status, "completed");
  assert.deepEqual(right, left);
  assert.equal(attempts.recoveryControls.length, 1);
  assert.equal(completion.inputs.length, 1);
  assert.equal(truth.current.tenders.length, 1);
});

test("跨 coordinator 的不同 signal 或 manual 不 join，只有同一 signal 可复用", async () => {
  const pending = paymentAttempt({
    attemptId: "attempt-mixed-owned-flight",
    state: "Pending",
  });
  const attempts = new SignalAwareFakeAttempts();
  attempts.attempts.set(pending.attemptId, pending);
  const gate = createDeferred<PaymentAttemptExecutionResult>();
  attempts.recoverResult = () => gate.promise;
  const truth = new MemoryTruth(orderTruth(500));
  const firstCoordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });
  const secondCoordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });
  const firstController = new AbortController();
  const secondController = new AbortController();
  const deadlineAtMs = Date.parse(pending.createdAtIso) + 90_000;
  const input = {
    orderGuid: pending.orderGuid,
    attemptId: pending.attemptId,
    deadlineAtMs,
  };

  const first = firstCoordinator.recoverOnlineAttempt({
    ...input,
    signal: firstController.signal,
  });
  await waitUntil(() => attempts.recoverInputs.length === 1);
  const sameOwner = secondCoordinator.recoverOnlineAttempt({
    ...input,
    signal: firstController.signal,
  });
  const differentOwner = secondCoordinator.recoverOnlineAttempt({
    ...input,
    signal: secondController.signal,
  });
  const manual = secondCoordinator.recoverOnlineAttempt({
    orderGuid: input.orderGuid,
    attemptId: input.attemptId,
  });

  gate.resolve({
    attempt: { ...pending, state: "Unknown" },
    receiptText: null,
    responseCode: "SQUARE_PENDING",
  });
  const [, , differentResult, manualResult] = await Promise.all([
    first,
    sameOwner,
    differentOwner,
    manual,
  ]);
  assert.strictEqual(sameOwner, first);
  assert.notStrictEqual(differentOwner, first);
  assert.notStrictEqual(manual, first);
  assert.equal(differentResult.errorCode, "PAYMENT_ACTION_IN_FLIGHT");
  assert.equal(manualResult.errorCode, "PAYMENT_ACTION_IN_FLIGHT");
  assert.equal(attempts.recoverInputs.length, 1);
});

test("Approved 冷恢复完成订单只使用 action 原员工，不使用当前登录员工", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  const approved = paymentAttempt({
    attemptId: "attempt-alice-crash",
    state: "Approved",
    amount: aud(500),
  });
  attempts.attempts.set(approved.attemptId, approved);
  attempts.actionActor = actor();
  attempts.recoverResult = () => ({
    attempt: approved,
    receiptText: null,
    responseCode: "APPROVED",
  });
  const completion = new FakeCompletion(truth);
  const bob = {
    cashierId: "cashier-2",
    cashierName: "Bob",
    userGuid: "user-guid-2",
  } as const;
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion,
    actor: bob,
  });

  const result = await coordinator.recoverOnlineAttempt({
    orderGuid: "order-1",
    attemptId: approved.attemptId,
  });

  assert.equal(result.status, "completed");
  assert.deepEqual(completion.actors, [actor()]);
  assert.notDeepEqual(completion.actors, [bob]);
});

test("持久余额为零时直接返回 completed，绝不再次请求扣款", async () => {
  const truth = new MemoryTruth(orderTruth(500, {
    state: "PendingSync",
    tenders: [tender("tender-paid", "card", 500)],
  }));
  const attempts = new FakeAttempts();
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });

  const result = await coordinator.addOnlineTender({
    actionId: "action-zero-balance",
    orderGuid: "order-1",
    provider: "square",
    amount: aud(1),
  });

  assert.equal(result.status, "completed");
  assert.equal(result.remaining.cents, 0);
  assert.equal(attempts.startInputs.length, 0);
});

test("支付落库 durability 异常必须把 attemptId 交给恢复界面", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  attempts.startFailure = new PaymentAttemptDurabilityError(
    "attempt-durability",
    "order-1",
    "Submitted",
    "Approved",
    new Error("commit outcome unknown"),
  );
  const coordinator = createCoordinator({
    truth,
    attempts,
    completion: new FakeCompletion(truth),
  });

  const result = await coordinator.addOnlineTender({
    actionId: "action-durability",
    orderGuid: "order-1",
    provider: "square",
    amount: aud(500),
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.attemptId, "attempt-durability");
  assert.equal(result.errorCode, "PAYMENT_START_FAILED");
});

test("Created attempt 崩溃后由显式 recover 恢复，不能新建 payment action", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  const created = paymentAttempt({
    attemptId: "attempt-created",
    state: "Created",
  });
  attempts.attempts.set(created.attemptId, created);
  attempts.recoverResult = () => ({
    attempt: { ...created, state: "Approved" },
    receiptText: null,
    responseCode: "APPROVED",
  });
  const completion = new FakeCompletion(truth);
  const coordinator = createCoordinator({ truth, attempts, completion });

  const result = await coordinator.recoverOnlineAttempt({
    orderGuid: "order-1",
    attemptId: created.attemptId,
  });

  assert.equal(result.status, "completed");
  assert.deepEqual(attempts.recoverInputs, [created.attemptId]);
  assert.equal(attempts.startInputs.length, 0);
  assert.equal(completion.inputs[0]?.attempt.attemptId, created.attemptId);
});

test("恢复前后任一不可变支付身份变化都拒绝 completion", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const attempts = new FakeAttempts();
  const pending = paymentAttempt({
    attemptId: "attempt-identity",
    idempotencyKey: "key-original",
    state: "Pending",
  });
  attempts.attempts.set(pending.attemptId, pending);
  attempts.recoverResult = () => ({
    attempt: {
      ...pending,
      idempotencyKey: "key-tampered",
      state: "Approved",
    },
    receiptText: null,
    responseCode: "APPROVED",
  });
  const completion = new FakeCompletion(truth);
  const coordinator = createCoordinator({ truth, attempts, completion });

  const result = await coordinator.recoverOnlineAttempt({
    orderGuid: "order-1",
    attemptId: pending.attemptId,
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.attemptId, pending.attemptId);
  assert.equal(result.errorCode, "PAYMENT_RECOVERY_MISMATCH");
  assert.equal(completion.inputs.length, 0);
  assert.equal(truth.current.tenders.length, 0);
});

test("已落库部分 tender 后显式 recover 原 Approved attempt 可幂等完成，不受当前余额限制", async () => {
  const approved = paymentAttempt({
    attemptId: "attempt-approved-partial",
    amount: aud(400),
    state: "Approved",
  });
  const existingTender = tender(
    `tender-${approved.attemptId}`,
    "card",
    approved.amount.cents,
  );
  const truth = new MemoryTruth(
    orderTruth(500, {
      state: "Completing",
      tenders: [existingTender],
    }),
  );
  const attempts = new FakeAttempts();
  attempts.attempts.set(approved.attemptId, approved);
  const completion = new FakeCompletion(truth);
  const coordinator = createCoordinator({ truth, attempts, completion });

  const result = await coordinator.recoverOnlineAttempt({
    orderGuid: "order-1",
    attemptId: approved.attemptId,
  });

  assert.equal(result.status, "partial");
  assert.equal(result.remaining.cents, 100);
  assert.equal(result.attemptId, approved.attemptId);
  assert.equal(attempts.recoverInputs.length, 1);
  assert.equal(truth.current.tenders.length, 1);
});

test("跨新 coordinator 重放同一部分支付 action 复用 attempt 且不重复 tender", async () => {
  const truth = new MemoryTruth(orderTruth(1_000));
  const attempts = new FakeAttempts();
  const completion = new FakeCompletion(truth);
  const durableExecution = approvedExecution(
    {
      actionId: "action-restart-replay",
      orderGuid: "order-1",
      provider: "square",
      operation: "purchase",
      amount: aud(400),
    },
    "attempt-restart-replay",
  );
  attempts.startResult = () => durableExecution;
  const input = {
    actionId: "action-restart-replay",
    orderGuid: "order-1",
    provider: "square" as const,
    amount: aud(400),
  };

  const first = await createCoordinator({
    truth,
    attempts,
    completion,
  }).addOnlineTender(input);
  const replayed = await createCoordinator({
    truth,
    attempts,
    completion,
  }).addOnlineTender(input);

  assert.equal(first.status, "partial");
  assert.equal(replayed.status, "partial");
  assert.equal(first.attemptId, "attempt-restart-replay");
  assert.equal(replayed.attemptId, first.attemptId);
  assert.equal(truth.current.tenders.length, 1);
  assert.equal(completion.inputs.length, 2);
});

test("缺少原子现金实现时 capability=unavailable，不伪造本地 tender", async () => {
  const truth = new MemoryTruth(orderTruth(500));
  const coordinator = createCoordinator({
    truth,
    attempts: new FakeAttempts(),
    completion: new FakeCompletion(truth),
  });

  assert.equal(coordinator.getCapabilities().mixedCashTender, "unavailable");
  const result = await coordinator.addCashTender({
    actionId: "action-no-cash-port",
    orderGuid: "order-1",
    amount: aud(200),
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.capability, "unavailable");
  assert.equal(result.errorCode, "MIXED_CASH_UNAVAILABLE");
  assert.equal(truth.current.tenders.length, 0);
});

test("移除 tender 只能通过追加 reversal truth，不能从本地数组删除原 tender", async () => {
  const original = tender("tender-original", "card", 400);
  const truth = new MemoryTruth(orderTruth(1_000, {
    state: "Completing",
    tenders: [original],
  }));
  const reversal = new FakeReversal(truth);
  const coordinator = createCoordinator({
    truth,
    attempts: new FakeAttempts(),
    completion: new FakeCompletion(truth),
    reversal,
  });

  const result = await coordinator.removeTender({
    actionId: "action-reverse",
    orderGuid: "order-1",
    tenderGuid: original.tenderGuid,
  });

  assert.equal(result.status, "partial");
  assert.equal(result.remaining.cents, 1_000);
  assert.equal(truth.current.tenders.some((item) => item.tenderGuid === "tender-original"), true);
  assert.deepEqual(
    truth.current.tenders.map((item) => item.amount.cents),
    [400, -400],
  );
});

test("只有 Completing 订单可 reversal，完成、同步、拒绝状态均在 feature 预检拒绝", async () => {
  for (const state of [
    "Draft",
    "CompletedLocal",
    "PendingSync",
    "Syncing",
    "Synced",
    "Blocked403",
    "Rejected",
  ] as const) {
    const original = tender(`tender-${state}`, "cash", 100);
    const truth = new MemoryTruth(
      orderTruth(500, {
        state,
        tenders: [original],
      }),
    );
    const reversal = new FakeReversal(truth);
    const coordinator = createCoordinator({
      truth,
      attempts: new FakeAttempts(),
      completion: new FakeCompletion(truth),
      reversal,
    });

    await assert.rejects(
      () =>
        coordinator.removeTender({
          actionId: `reverse-${state}`,
          orderGuid: "order-1",
          tenderGuid: original.tenderGuid,
        }),
      (error: unknown) => {
        assert.ok(error instanceof MixedPaymentValidationError);
        assert.equal(error.code, "ORDER_NOT_COMPLETING");
        return true;
      },
    );
    assert.equal(reversal.inputs.length, 0);
  }
});

test("同一 source 的第二个 reversal action 在 feature 层拒绝，同 action 重放幂等返回", async () => {
  const original = tender("tender-once", "card", 400);
  const truth = new MemoryTruth(
    orderTruth(1_000, {
      state: "Completing",
      tenders: [original],
    }),
  );
  const reversal = new FakeReversal(truth);
  const coordinator = createCoordinator({
    truth,
    attempts: new FakeAttempts(),
    completion: new FakeCompletion(truth),
    reversal,
  });

  const first = await coordinator.removeTender({
    actionId: "reverse-once",
    orderGuid: "order-1",
    tenderGuid: original.tenderGuid,
  });
  const replayed = await createCoordinator({
    truth,
    attempts: new FakeAttempts(),
    completion: new FakeCompletion(truth),
    reversal,
  }).removeTender({
    actionId: "reverse-once",
    orderGuid: "order-1",
    tenderGuid: original.tenderGuid,
  });

  assert.equal(first.tenderGuid, "reversal-reverse-once");
  assert.equal(replayed.tenderGuid, first.tenderGuid);
  assert.equal(reversal.inputs.length, 1);

  await assert.rejects(
    () =>
      coordinator.removeTender({
        actionId: "reverse-twice",
        orderGuid: "order-1",
        tenderGuid: original.tenderGuid,
      }),
    (error: unknown) => {
      assert.ok(error instanceof MixedPaymentValidationError);
      assert.equal(error.code, "TENDER_ALREADY_REVERSED");
      return true;
    },
  );
  assert.equal(reversal.inputs.length, 1);
});

function createCoordinator(input: Readonly<{
  truth: MixedPaymentOrderTruthPort;
  attempts: MixedPaymentAttemptPort;
  completion: MixedApprovedPaymentCompletionPort;
  actor?: AuditActorSnapshot;
  cash?: MixedCashTenderPort;
  reversal?: MixedTenderReversalPort;
}>): MixedPaymentCoordinator {
  return new MixedPaymentCoordinator({
    actor: input.actor ?? actor(),
    orderTruth: input.truth,
    paymentAttempts: input.attempts,
    approvedCompletion: input.completion,
    ...(input.cash ? { cashTender: input.cash } : {}),
    ...(input.reversal ? { tenderReversal: input.reversal } : {}),
  });
}

class MemoryTruth implements MixedPaymentOrderTruthPort {
  public constructor(public current: MixedPaymentOrderTruth) {}

  public async getPaymentTruth(orderGuid: string): Promise<MixedPaymentOrderTruth | null> {
    return this.current.orderGuid === orderGuid ? this.current : null;
  }
}

class FakeAttempts implements MixedPaymentAttemptPort {
  public readonly startInputs: StartPaymentAttemptInput[] = [];
  public readonly recoverInputs: string[] = [];
  public readonly attempts = new Map<string, PaymentAttempt>();
  public readonly actors = new Map<string, AuditActorSnapshot>();
  public blocking: PaymentAttempt | null = null;
  public startFailure: Error | null = null;
  public cancelCalls = 0;
  public actionActor: AuditActorSnapshot = actor();
  public startResult:
    | ((
        input: StartPaymentAttemptInput,
        index: number,
      ) => PaymentAttemptExecutionResult | Promise<PaymentAttemptExecutionResult>)
    | null = null;
  public recoverResult:
    | ((attemptId: string) => PaymentAttemptExecutionResult | Promise<PaymentAttemptExecutionResult>)
    | null = null;

  public async startAttempt(
    input: StartPaymentAttemptInput,
  ): Promise<PaymentAttemptExecutionResult> {
    this.startInputs.push(input);
    if (this.startFailure) throw this.startFailure;
    const value = this.startResult?.(input, this.startInputs.length);
    if (!value) throw new Error("Missing fake start result.");
    const execution = await value;
    this.attempts.set(execution.attempt.attemptId, execution.attempt);
    this.actors.set(execution.attempt.attemptId, input.actor);
    return execution;
  }

  public async recoverAttempt(attemptId: string): Promise<PaymentAttemptExecutionResult> {
    this.recoverInputs.push(attemptId);
    const value = this.recoverResult?.(attemptId);
    if (value) {
      const execution = await value;
      this.attempts.set(attemptId, execution.attempt);
      return execution;
    }
    const attempt = this.attempts.get(attemptId);
    if (!attempt) throw new Error("Missing fake attempt.");
    return { attempt, receiptText: null, responseCode: null };
  }

  public async getAttempt(attemptId: string): Promise<PaymentAttempt | null> {
    return this.attempts.get(attemptId) ?? null;
  }

  public async getBlockingAttempt(orderGuid: string): Promise<PaymentAttempt | null> {
    return this.blocking?.orderGuid === orderGuid ? this.blocking : null;
  }

  public async getActionActor(
    attemptId: string,
    orderGuid: string,
  ): Promise<AuditActorSnapshot> {
    const attempt = this.attempts.get(attemptId);
    if (attempt && attempt.orderGuid !== orderGuid) {
      throw new Error("Fake action actor order mismatch.");
    }
    return this.actors.get(attemptId) ?? this.actionActor;
  }
}

class SignalAwareFakeAttempts extends FakeAttempts {
  public readonly recoveryControls: {
    signal: AbortSignal;
    deadlineAtMs: number;
  }[] = [];

  public override async recoverAttempt(
    attemptId: string,
    control?: { signal: AbortSignal; deadlineAtMs: number },
  ): Promise<PaymentAttemptExecutionResult> {
    if (control) this.recoveryControls.push(control);
    return super.recoverAttempt(attemptId);
  }
}

class FakeCompletion implements MixedApprovedPaymentCompletionPort {
  public readonly inputs: PaymentAttemptExecutionResult[] = [];
  public readonly actors: (AuditActorSnapshot | undefined)[] = [];
  public failure: Error | null = null;

  public constructor(private readonly truth: MemoryTruth) {}

  public async complete(
    execution: PaymentAttemptExecutionResult,
    frozenActor?: AuditActorSnapshot,
  ): Promise<ApprovedPaymentOrderCommitResult> {
    this.inputs.push(execution);
    this.actors.push(frozenActor);
    if (this.failure) throw this.failure;
    const { attempt } = execution;
    const tenderGuid = `tender-${attempt.attemptId}`;
    const method = attempt.provider === "voucher" ? "voucher" : "card";
    const existing = this.truth.current.tenders.find(
      (candidate) => candidate.tenderGuid === tenderGuid,
    );
    if (existing) {
      const paid = this.truth.current.tenders.reduce(
        (sum, item) => sum + item.amount.cents,
        0,
      );
      return {
        replayed: true,
        orderGuid: attempt.orderGuid,
        tenderGuid,
        completed: paid === this.truth.current.actualAmount.cents,
        signedTenderAmountCents: existing.amount.cents,
      };
    }
    const nextTenders = [
      ...this.truth.current.tenders,
      tender(tenderGuid, method, attempt.amount.cents),
    ];
    const paid = nextTenders.reduce((sum, item) => sum + item.amount.cents, 0);
    const completed = paid === this.truth.current.actualAmount.cents;
    this.truth.current = {
      ...this.truth.current,
      state: completed ? "PendingSync" : "Completing",
      tenders: nextTenders,
    };
    return {
      replayed: false,
      orderGuid: attempt.orderGuid,
      tenderGuid,
      completed,
      signedTenderAmountCents: attempt.amount.cents,
    };
  }
}

function actor(): AuditActorSnapshot {
  return Object.freeze({
    cashierId: "cashier-1",
    cashierName: "Alice",
    userGuid: "user-guid-1",
  });
}

class FakeCashTender implements MixedCashTenderPort {
  public readonly inputs: Parameters<MixedCashTenderPort["appendCashTenderAtomically"]>[0][] = [];

  public constructor(private readonly truth: MemoryTruth) {}

  public async appendCashTenderAtomically(
    input: Parameters<MixedCashTenderPort["appendCashTenderAtomically"]>[0],
  ): Promise<Awaited<ReturnType<MixedCashTenderPort["appendCashTenderAtomically"]>>> {
    this.inputs.push(input);
    const tenderGuid = `cash-${input.actionId}`;
    const tenders = [
      ...this.truth.current.tenders,
      tender(tenderGuid, "cash", input.amount.cents),
    ];
    const paid = tenders.reduce((sum, item) => sum + item.amount.cents, 0);
    this.truth.current = {
      ...this.truth.current,
      state: paid === this.truth.current.actualAmount.cents ? "PendingSync" : "Completing",
      tenders,
    };
    return { replayed: false, tenderGuid, truth: this.truth.current };
  }
}

class FakeReversal implements MixedTenderReversalPort {
  public readonly inputs: Parameters<MixedTenderReversalPort["reverseTender"]>[0][] = [];

  public constructor(private readonly truth: MemoryTruth) {}

  public async reverseTender(
    input: Parameters<MixedTenderReversalPort["reverseTender"]>[0],
  ): Promise<Awaited<ReturnType<MixedTenderReversalPort["reverseTender"]>>> {
    this.inputs.push(input);
    const source = this.truth.current.tenders.find(
      (item) => item.tenderGuid === input.tenderGuid,
    );
    if (!source) throw new Error("Missing source tender.");
    const reversalTenderGuid = `reversal-${input.actionId}`;
    this.truth.current = {
      ...this.truth.current,
      state: "Completing",
      tenders: [
        ...this.truth.current.tenders,
        {
          ...source,
          tenderGuid: reversalTenderGuid,
          amount: aud(-source.amount.cents),
          reference: null,
          reservationToken: null,
        },
      ],
      reversalLinks: [
        ...this.truth.current.reversalLinks,
        {
          actionId: input.actionId,
          sourceTenderGuid: source.tenderGuid,
          reversalTenderGuid,
        },
      ],
    };
    return {
      state: "reversed",
      replayed: false,
      reversalTenderGuid,
      truth: this.truth.current,
    };
  }
}

function orderTruth(
  actualAmountCents: number,
  overrides: Partial<MixedPaymentOrderTruth> = {},
): MixedPaymentOrderTruth {
  return {
    orderGuid: "order-1",
    state: "Draft",
    actualAmount: aud(actualAmountCents),
    tenders: [],
    reversalLinks: [],
    ...overrides,
  };
}

function tender(
  tenderGuid: string,
  method: OrderTender["method"],
  cents: number,
): OrderTender {
  return {
    tenderGuid,
    method,
    amount: aud(cents),
    reference: null,
    reservationToken: null,
  };
}

function approvedExecution(
  input: Pick<
    StartPaymentAttemptInput,
    "actionId" | "orderGuid" | "provider" | "operation" | "amount"
  >,
  attemptId: string,
): PaymentAttemptExecutionResult {
  return {
    attempt: paymentAttempt({
      attemptId,
      orderGuid: input.orderGuid,
      provider: input.provider,
      operation: input.operation,
      amount: input.amount,
      state: "Approved",
    }),
    receiptText: null,
    responseCode: "APPROVED",
  };
}

function paymentAttempt(
  overrides: Partial<PaymentAttempt> = {},
): PaymentAttempt {
  const provider: PaymentProvider = overrides.provider ?? "square";
  return {
    attemptId: "attempt-1",
    idempotencyKey: "idempotency-1",
    orderGuid: "order-1",
    provider,
    operation: "purchase",
    amount: aud(500),
    state: "Submitted",
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: null,
      txnRef: null,
      rfn: null,
      voucherReservationToken: null,
    },
    createdAtIso: "2026-07-28T00:00:00.000Z",
    updatedAtIso: "2026-07-28T00:00:01.000Z",
    lastErrorCode: null,
    receiptText: null,
    responseCode: null,
    ...overrides,
  };
}

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

async function waitUntil(predicate: () => boolean): Promise<void> {
  for (let index = 0; index < 100; index += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
  throw new Error("Condition was not reached.");
}
