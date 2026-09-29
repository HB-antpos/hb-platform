import assert from "node:assert/strict";
import test from "node:test";

import {
  PAYMENT_PERMISSION,
  PaymentCheckoutRuntime,
  PaymentCheckoutRuntimeError,
  type PaymentCartLease,
  type PaymentCartLeasePort,
  type PaymentCheckoutAttemptPort,
  type PaymentCheckoutDraft,
  type PaymentCheckoutDraftPort,
  type PaymentCheckoutMixedCoordinatorPort,
  type PaymentCheckoutPreparedAction,
  type PaymentPermissionCode,
} from "./payment-checkout-runtime";

import type { PaymentAttempt, PaymentProvider } from "@/core/contracts";
import type {
  LinklyPaymentTerminalSelectionBindingPort,
  LinklyPaymentTerminalSelectionExpectation,
} from "@/features/payments/linkly";
import type {
  PaymentProviderAvailability,
  PaymentProviderAvailabilityPort,
} from "@/features/payments/runtime/payment-provider-registry";

test("手动刷卡必须显式确认且一次收齐余额，拒绝时不调用支付服务", async () => {
  const harness = createHarness();
  harness.providers.unblock("manual-card");
  const runtime = harness.runtime();
  await assert.rejects(runtime.start({ ...startInput(), provider: "manual-card" }), /MANUAL_CARD_CONFIRMATION_REQUIRED/);
  assert.equal(harness.drafts.createCalls, 0);
  await assert.rejects(runtime.start({ ...startInput(), provider: "manual-card", manualConfirmed: true, amount: aud(500) }), /MANUAL_CARD_FULL_BALANCE_REQUIRED/);
  assert.equal(harness.mixed.onlineCalls, 0);
  await runtime.start({ ...startInput(), provider: "manual-card", manualConfirmed: true });
  assert.equal(harness.mixed.onlineCalls, 1);
  assert.equal(harness.mixed.lastOnlineInput?.manualConfirmed, true);
});

test("已持久化的手动确认可在开关关闭后恢复，Created 不允许取消", async () => {
  const harness = createHarness();
  harness.drafts.recovery = {
    draft: harness.drafts.current, attemptId: null,
    preparedAction: { actionId: "manual-durable", provider: "manual-card", operation: "purchase", amount: aud(1000), manualConfirmed: true },
  };
  await harness.runtime().resumeCurrent();
  assert.equal(harness.mixed.onlineCalls, 1);
  assert.equal(harness.mixed.lastOnlineInput?.actionId, "manual-durable");
  assert.equal(harness.mixed.lastOnlineInput?.manualConfirmed, true);
  const created = attempt({ provider: "manual-card", state: "Created" });
  harness.attempts.put(created);
  const recovery = harness.drafts.recovery;
  assert.ok(recovery);
  recovery.attemptId = created.attemptId;
  const snapshot = await harness.runtime().findRecoveryRequired();
  assert.equal(snapshot?.allowedActions.cancel, false);
  assert.equal(snapshot?.allowedActions.recover, true);
});

test("手动刷卡已确认且仅有 binding 时不能放弃，旧小数单仍可重放记账", async () => {
  const harness = createHarness();
  Object.assign(harness.lease.value.cart.lines[0]!, { quantity: "1.25" });
  Object.assign(harness.lease.value.pricingState.lines[0]!, { quantity: 1.25 });
  harness.drafts.recovery = {
    draft: harness.drafts.current,
    attemptId: null,
    preparedAction: {
      actionId: "manual-already-paid",
      provider: "manual-card",
      operation: "purchase",
      amount: aud(1_000),
      manualConfirmed: true,
    },
  };
  const runtime = harness.runtime();
  const found = await runtime.findRecoveryRequired();
  assert.equal(found?.allowedActions.cancel, false);
  assert.equal(found?.allowedActions.recover, true);
  await assert.rejects(
    () => runtime.abandonPrepared({ orderGuid: "order-1", actionId: "unsafe-abandon" }),
    /PAYMENT_DRAFT_ABANDON_FORBIDDEN/,
  );
  assert.equal(harness.drafts.abandonCalls, 0);
  assert.equal(harness.lease.releaseCalls, 0);

  await runtime.resumeCurrent();
  assert.equal(harness.mixed.onlineCalls, 1);
  assert.equal(harness.mixed.lastOnlineInput?.actionId, "manual-already-paid");
  assert.equal(harness.mixed.lastOnlineInput?.manualConfirmed, true);

  const created = attempt({ provider: "manual-card", state: "Created" });
  harness.attempts.put(created);
  const recovery = harness.drafts.recovery;
  assert.ok(recovery);
  recovery.attemptId = created.attemptId;
  await runtime.recover({ orderGuid: "order-1", attemptId: created.attemptId });
  assert.equal(harness.mixed.lastRecoveryInput?.attemptId, created.attemptId);
});

test("新手动刷卡仍拒绝小数单；无耐久确认的 Created 不获历史恢复例外", async () => {
  const harness = createHarness();
  harness.providers.unblock("manual-card");
  Object.assign(harness.lease.value.cart.lines[0]!, { quantity: "1.25" });
  Object.assign(harness.lease.value.pricingState.lines[0]!, { quantity: 1.25 });
  await assert.rejects(
    () => harness.runtime().start({ ...startInput(), provider: "manual-card", manualConfirmed: true }),
    /PAYMENT_QUANTITY_UNSUPPORTED/,
  );
  assert.equal(harness.drafts.createCalls, 0);
  assert.equal(harness.mixed.onlineCalls, 0);

  const created = attempt({ provider: "manual-card", state: "Created" });
  harness.attempts.put(created);
  await assert.rejects(
    () => harness.runtime().recover({ orderGuid: "order-1", attemptId: created.attemptId }),
    /PAYMENT_QUANTITY_UNSUPPORTED/,
  );
  assert.equal(harness.mixed.lastRecoveryInput, null);
});

test("含退货行的销售不能绕过界面开启手动刷卡", async () => {
  const harness = createHarness();
  harness.providers.unblock("manual-card");
  Object.assign(harness.lease.value.cart.lines[0]!, { kind: "return" });
  Object.assign(harness.lease.value.pricingState.lines[0]!, { kind: "return" });
  await assert.rejects(harness.runtime().start({ ...startInput(), provider: "manual-card", manualConfirmed: true }), /MANUAL_CARD_SALE_ONLY/);
  assert.equal(harness.mixed.onlineCalls, 0);
});


test("恢复的小数商品数量在现金与在线收款前被拒绝", async () => {
  for (const method of ["cash", "card"] as const) {
    const harness = createHarness();
    Object.assign(harness.lease.value.cart.lines[0]!, { quantity: "1.25" });
    Object.assign(harness.lease.value.pricingState.lines[0]!, { quantity: 1.25 });
    const runtime = harness.runtime();
    const action = method === "cash"
      ? () => runtime.startCash({
          checkoutIntentId: "checkout-intent-1",
          expectedCartRevision: 7,
          actionId: "cash-fractional-quantity",
          amount: aud(1_000),
        })
      : () => runtime.start(startInput());

    await assert.rejects(action, (error: unknown) => {
      assert.ok(error instanceof PaymentCheckoutRuntimeError);
      assert.equal(error.code, "PAYMENT_QUANTITY_UNSUPPORTED");
      return true;
    });
    assert.equal(harness.drafts.createCalls, 0);
    assert.equal(harness.mixed.cashCalls, 0);
    assert.equal(harness.mixed.onlineCalls, 0);
  }
});

test("历史已付款小数订单可完成恢复，Created 首次提交仍被阻止", async () => {
  const paid = createHarness(completedCardDraft("historical-fractional"));
  Object.assign(paid.lease.value.cart.lines[0]!, { quantity: "1.25" });
  Object.assign(paid.lease.value.pricingState.lines[0]!, { quantity: 1.25 });
  const approved = attempt({ state: "Approved" });
  paid.attempts.put(approved);
  paid.mixed.recoverOnlineAttempt = async () => mixed("completed", { remaining: aud(0) });
  assert.equal((await paid.runtime().recover({ orderGuid: "order-1", attemptId: approved.attemptId })).status, "completed");
  assert.equal(paid.lease.clearCalls, 1);

  const created = createHarness();
  Object.assign(created.lease.value.cart.lines[0]!, { quantity: "1.25" });
  Object.assign(created.lease.value.pricingState.lines[0]!, { quantity: 1.25 });
  const notSubmitted = attempt({ state: "Created" });
  created.attempts.put(notSubmitted);
  await assert.rejects(
    () => created.runtime().recover({ orderGuid: "order-1", attemptId: notSubmitted.attemptId }),
    (error: unknown) => error instanceof PaymentCheckoutRuntimeError && error.code === "PAYMENT_QUANTITY_UNSUPPORTED",
  );
  assert.equal(created.mixed.lastOnlineInput, null);
  created.drafts.recovery = cancellationRecovery(notSubmitted);
  assert.equal((await created.runtime().cancel({ orderGuid: "order-1", attemptId: notSubmitted.attemptId })).status, "cancelled");
  assert.equal(created.lease.releaseCalls, 1);
});

test("重复点击共享同一 action，provider/completion/clear 各执行一次且公开 JSON 无秘密", async () => {
  const harness = createHarness();
  const pending = deferred<void>();
  let calls = 0;
  harness.mixed.addOnlineTender = async () => {
    calls += 1;
    await pending.promise;
    const approved = attempt({ state: "Approved" });
    harness.attempts.put(approved);
    harness.drafts.current = draft({
      state: "PendingSync",
      remaining: aud(0),
      tenders: [
        {
          tenderGuid: "tender-card",
          method: "card",
          amount: aud(1_000),
          reversible: true,
        },
      ],
    });
    return mixed("completed", {
      attemptId: approved.attemptId,
      tenderGuid: "tender-card",
      remaining: aud(0),
    });
  };
  const runtime = harness.runtime();
  const input = startInput();

  const first = runtime.start(input);
  const duplicate = runtime.start(input);
  assert.strictEqual(first, duplicate);
  pending.resolve();
  const result = await first;

  assert.equal(calls, 1);
  assert.equal(harness.drafts.createCalls, 1);
  assert.equal(harness.lease.clearCalls, 1);
  assert.equal(result.status, "completed");
  const json = JSON.stringify(result);
  assert.equal(json.includes("checkout-secret"), false);
  assert.equal(json.includes("payment-secret"), false);
  assert.equal(json.includes("session-secret"), false);
  assert.equal(json.includes("voucher-secret"), false);
  assert.deepEqual(result.tenders, [
    {
      tenderGuid: "tender-card",
      method: "card",
      amount: aud(1_000),
      reversible: true,
    },
  ]);
});

test("Linkly 启动在 provider 调用期间按 OrderGuid 绑定 UI 已确认终端快照", async () => {
  const harness = createHarness();
  const binding = new RecordingLinklyPaymentSelection();
  harness.linklyPaymentSelection = binding;
  harness.attempts.put(attempt({
    attemptId: "attempt-selection-conflict",
    provider: "linkly-cloud",
    state: "Declined",
    lastErrorCode: "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  }));
  harness.mixed.addOnlineTender = async () => {
    assert.equal(binding.activeOrderGuid, "order-1");
    return mixed("declined", {
      attemptId: "attempt-selection-conflict",
      errorCode: "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
    });
  };
  const selection: LinklyPaymentTerminalSelectionExpectation = {
    environment: "Sandbox",
    mode: "Active",
    terminalId: "terminal-1",
    selectionRevision: 3,
  };

  const result = await harness.runtime().start({
    ...startInput(),
    provider: "linkly-cloud",
    linklyTerminalSelection: selection,
  });

  assert.deepEqual(binding.calls, [{ orderGuid: "order-1", selection }]);
  assert.equal(binding.activeOrderGuid, null);
  assert.equal(result.status, "declined");
  assert.equal(
    result.errorCode,
    "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  );
});

test("首次现金支付先耐久同一 draft，再原子写入现金 tender 并完成清车", async () => {
  const harness = createHarness();
  harness.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-first",
    remaining: aud(0),
  });
  harness.drafts.afterCash = draft({
    state: "PendingSync",
    remaining: aud(0),
    tenders: [
      {
        tenderGuid: "cash-first",
        method: "cash",
        amount: aud(1_000),
        reversible: false,
      },
    ],
  });
  const runtime = harness.runtime();
  const input = {
    checkoutIntentId: "checkout-intent-1",
    expectedCartRevision: 7,
    actionId: "cash-action-first",
    amount: aud(1_500),
  };

  const first = runtime.startCash(input);
  const duplicate = runtime.startCash(input);
  assert.strictEqual(first, duplicate);
  assert.equal((await first).status, "completed");
  assert.equal(harness.drafts.createCalls, 1);
  assert.equal(harness.mixed.cashCalls, 1);
  assert.equal(harness.mixed.lastCashInput?.amount.cents, 1_000);
  assert.equal(
    harness.mixed.lastCashInput?.tenderedAmount?.cents,
    1_500,
  );
  assert.equal(harness.mixed.lastCashInput?.change?.cents, 500);
  assert.equal(harness.lease.clearCalls, 1);
});

test("最终现金按五分规则传入账、规范化实收和找零，并允许 1 分零实收", async () => {
  const roundDown = createHarness(
    draft({ total: aud(1_002), remaining: aud(1_002) }),
  );
  roundDown.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-round-down",
    remaining: aud(0),
    cashSettlement: {
      tendered: aud(1_000),
      applied: aud(1_002),
      change: aud(0),
    },
  });
  roundDown.drafts.afterCash = draft({
    total: aud(1_002),
    state: "PendingSync",
    remaining: aud(0),
    tenders: [{
      tenderGuid: "cash-round-down",
      method: "cash",
      amount: aud(1_002),
      reversible: false,
    }],
  });
  const roundDownSnapshot = await roundDown.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-round-down-action",
    amount: aud(1_000),
  });
  assert.deepEqual(roundDown.mixed.lastCashInput, {
    actionId: "cash-round-down-action",
    orderGuid: "order-1",
    amount: aud(1_002),
    tenderedAmount: aud(1_000),
    change: aud(0),
  });
  assert.deepEqual(roundDownSnapshot.cashSettlement, {
    tendered: aud(1_000),
    applied: aud(1_002),
    change: aud(0),
  });

  const roundDownOneCentAbove = createHarness(
    draft({ total: aud(1_002), remaining: aud(1_002) }),
  );
  roundDownOneCentAbove.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-round-down-one-cent-above",
    remaining: aud(0),
  });
  roundDownOneCentAbove.drafts.afterCash = draft({
    total: aud(1_002),
    state: "PendingSync",
    remaining: aud(0),
    tenders: [{
      tenderGuid: "cash-round-down-one-cent-above",
      method: "cash",
      amount: aud(1_002),
      reversible: false,
    }],
  });
  await roundDownOneCentAbove.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-round-down-one-cent-above-action",
    amount: aud(1_001),
  });
  assert.deepEqual(roundDownOneCentAbove.mixed.lastCashInput, {
    actionId: "cash-round-down-one-cent-above-action",
    orderGuid: "order-1",
    amount: aud(1_002),
    tenderedAmount: aud(1_000),
    change: aud(0),
  });

  const oneCent = createHarness(
    draft({ total: aud(1), remaining: aud(1) }),
  );
  oneCent.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-one-cent",
    remaining: aud(0),
    cashSettlement: {
      tendered: aud(0),
      applied: aud(1),
      change: aud(0),
    },
  });
  oneCent.drafts.afterCash = draft({
    total: aud(1),
    state: "PendingSync",
    remaining: aud(0),
    tenders: [{
      tenderGuid: "cash-one-cent",
      method: "cash",
      amount: aud(1),
      reversible: false,
    }],
  });
  const oneCentSnapshot = await oneCent.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-one-cent-action",
    amount: aud(1),
  });
  assert.deepEqual(oneCent.mixed.lastCashInput, {
    actionId: "cash-one-cent-action",
    orderGuid: "order-1",
    amount: aud(1),
    tenderedAmount: aud(0),
    change: aud(0),
  });
  assert.deepEqual(oneCentSnapshot.cashSettlement, {
    tendered: aud(0),
    applied: aud(1),
    change: aud(0),
  });

  const twoCents = createHarness(
    draft({ total: aud(2), remaining: aud(2) }),
  );
  twoCents.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-two-cents",
    remaining: aud(0),
  });
  twoCents.drafts.afterCash = draft({
    total: aud(2),
    state: "PendingSync",
    remaining: aud(0),
    tenders: [{
      tenderGuid: "cash-two-cents",
      method: "cash",
      amount: aud(2),
      reversible: false,
    }],
  });
  await twoCents.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-two-cents-action",
    amount: aud(2),
  });
  assert.deepEqual(twoCents.mixed.lastCashInput, {
    actionId: "cash-two-cents-action",
    orderGuid: "order-1",
    amount: aud(2),
    tenderedAmount: aud(0),
    change: aud(0),
  });
});

test("最终现金以 cashDue 判定，未达 cashDue 的现金保留原始实收", async () => {
  const mixedRoundDown = createHarness(
    draft({
      total: aud(1_003),
      remaining: aud(1_002),
      state: "Completing",
      tenders: [{
        tenderGuid: "card-one-cent",
        method: "card",
        amount: aud(1),
        reversible: true,
      }],
    }),
  );
  mixedRoundDown.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-mixed-round-down",
    remaining: aud(0),
  });
  mixedRoundDown.drafts.afterCash = draft({
    total: aud(1_003),
    state: "PendingSync",
    remaining: aud(0),
    tenders: [
      {
        tenderGuid: "card-one-cent",
        method: "card",
        amount: aud(1),
        reversible: true,
      },
      {
        tenderGuid: "cash-mixed-round-down",
        method: "cash",
        amount: aud(1_002),
        reversible: false,
      },
    ],
  });
  await mixedRoundDown.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-mixed-round-down-action",
    amount: aud(1_002),
  });
  assert.deepEqual(mixedRoundDown.mixed.lastCashInput, {
    actionId: "cash-mixed-round-down-action",
    orderGuid: "order-1",
    amount: aud(1_002),
    tenderedAmount: aud(1_000),
    change: aud(0),
  });

  const mixedRoundUp = createHarness(
    draft({
      total: aud(1_004),
      remaining: aud(1_003),
      state: "Completing",
      tenders: [{
        tenderGuid: "card-one-cent-up",
        method: "card",
        amount: aud(1),
        reversible: true,
      }],
    }),
  );
  mixedRoundUp.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-mixed-round-up",
    remaining: aud(0),
  });
  mixedRoundUp.drafts.afterCash = draft({
    total: aud(1_004),
    state: "PendingSync",
    remaining: aud(0),
    tenders: [
      {
        tenderGuid: "card-one-cent-up",
        method: "card",
        amount: aud(1),
        reversible: true,
      },
      {
        tenderGuid: "cash-mixed-round-up",
        method: "cash",
        amount: aud(1_003),
        reversible: false,
      },
    ],
  });
  await mixedRoundUp.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-mixed-round-up-action",
    amount: aud(1_005),
  });
  assert.deepEqual(mixedRoundUp.mixed.lastCashInput, {
    actionId: "cash-mixed-round-up-action",
    orderGuid: "order-1",
    amount: aud(1_003),
    tenderedAmount: aud(1_005),
    change: aud(0),
  });

  const roundUp = createHarness(
    draft({ total: aud(1_003), remaining: aud(1_003) }),
  );
  roundUp.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-round-up",
    remaining: aud(0),
  });
  roundUp.drafts.afterCash = draft({
    total: aud(1_003),
    state: "PendingSync",
    remaining: aud(0),
    tenders: [{
      tenderGuid: "cash-round-up",
      method: "cash",
      amount: aud(1_003),
      reversible: false,
    }],
  });
  await roundUp.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-round-up-action",
    amount: aud(1_005),
  });
  assert.deepEqual(roundUp.mixed.lastCashInput, {
    actionId: "cash-round-up-action",
    orderGuid: "order-1",
    amount: aud(1_003),
    tenderedAmount: aud(1_005),
    change: aud(0),
  });

  const partial = createHarness(
    draft({
      total: aud(1_003),
      remaining: aud(1_003),
      state: "Completing",
    }),
  );
  partial.mixed.cashResult = mixed("partial", {
    tenderGuid: "cash-partial",
    remaining: aud(1),
  });
  partial.drafts.afterCash = draft({
    total: aud(1_003),
    state: "Completing",
    remaining: aud(1),
    tenders: [
      {
        tenderGuid: "cash-partial",
        method: "cash",
        amount: aud(1_002),
        reversible: true,
      },
    ],
  });
  const partialSnapshot = await partial.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-partial-action",
    amount: aud(1_002),
  });
  assert.deepEqual(partial.mixed.lastCashInput, {
    actionId: "cash-partial-action",
    orderGuid: "order-1",
    amount: aud(1_002),
    tenderedAmount: aud(1_002),
    change: aud(0),
  });
  assert.deepEqual(partialSnapshot.remaining, aud(1));

  const belowCashDue = createHarness(
    draft({ total: aud(1_002), remaining: aud(1_002), state: "Completing" }),
  );
  belowCashDue.mixed.cashResult = mixed("partial", {
    tenderGuid: "cash-below-due",
    remaining: aud(3),
  });
  belowCashDue.drafts.afterCash = draft({
    total: aud(1_002),
    state: "Completing",
    remaining: aud(3),
    tenders: [{
      tenderGuid: "cash-below-due",
      method: "cash",
      amount: aud(999),
      reversible: true,
    }],
  });
  const belowCashDueSnapshot = await belowCashDue.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-below-due-action",
    amount: aud(999),
  });
  assert.deepEqual(belowCashDue.mixed.lastCashInput, {
    actionId: "cash-below-due-action",
    orderGuid: "order-1",
    amount: aud(999),
    tenderedAmount: aud(999),
    change: aud(0),
  });
  assert.deepEqual(belowCashDueSnapshot.remaining, aud(3));

  const reachesCashDue = createHarness(
    draft({ total: aud(500), remaining: aud(500), state: "Completing" }),
  );
  reachesCashDue.mixed.cashResult = mixed("completed", {
    tenderGuid: "cash-reaches-due",
    remaining: aud(0),
  });
  reachesCashDue.drafts.afterCash = draft({
    total: aud(500),
    state: "PendingSync",
    remaining: aud(0),
    tenders: [{
      tenderGuid: "cash-reaches-due",
      method: "cash",
      amount: aud(500),
      reversible: false,
    }],
  });
  await reachesCashDue.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-reaches-due-action",
    amount: aud(500),
  });
  assert.deepEqual(reachesCashDue.mixed.lastCashInput, {
    actionId: "cash-reaches-due-action",
    orderGuid: "order-1",
    amount: aud(500),
    tenderedAmount: aud(500),
    change: aud(0),
  });
});

test("现金覆盖 remaining 但不足五分应收时在 mixed 持久化前拒绝", async () => {
  const addCash = createHarness(
    draft({ total: aud(1_003), remaining: aud(1_003) }),
  );
  await assert.rejects(
    () =>
      addCash.runtime().addCash({
        orderGuid: "order-1",
        actionId: "cash-insufficient-add-action",
        amount: aud(1_003),
      }),
    (error: unknown) => {
      assert.ok(error instanceof PaymentCheckoutRuntimeError);
      assert.equal(error.code, "MIXED_CASH_COMMIT_FAILED");
      return true;
    },
  );
  assert.equal(addCash.mixed.cashCalls, 0);

  const startCash = createHarness(
    draft({ total: aud(1_003), remaining: aud(1_003) }),
  );
  await assert.rejects(
    () =>
      startCash.runtime().startCash({
        checkoutIntentId: "checkout-intent-1",
        expectedCartRevision: 7,
        actionId: "cash-insufficient-start-action",
        amount: aud(1_003),
      }),
    (error: unknown) => {
      assert.ok(error instanceof PaymentCheckoutRuntimeError);
      assert.equal(error.code, "MIXED_CASH_COMMIT_FAILED");
      return true;
    },
  );
  assert.equal(startCash.mixed.cashCalls, 0);
});

test("配置缺失在 draft/lease/provider 边界前 fail closed，保留稳定 blocker", async () => {
  const harness = createHarness();
  harness.providers.block("square", "SQUARE_CONFIGURATION_MISSING");
  const runtime = harness.runtime();

  await assert.rejects(
    () => runtime.start(startInput()),
    (error: unknown) => {
      assert.ok(error instanceof PaymentCheckoutRuntimeError);
      assert.equal(error.code, "SQUARE_CONFIGURATION_MISSING");
      return true;
    },
  );
  assert.equal(harness.lease.acquireCalls, 0);
  assert.equal(harness.drafts.createCalls, 0);
  assert.equal(harness.mixed.onlineCalls, 0);
});

test("已知离线不创建伪 attempt，可安全改现金；create 响应丢失 Unknown 则冻结全部新 tender", async () => {
  const offline = createHarness();
  offline.mixed.onlineResult = mixed("recovery-required", {
    errorCode: "ONLINE_REQUIRED",
  });
  const offlineResult = await offline.runtime().start(startInput());
  assert.equal(offlineResult.attemptId, null);
  assert.equal(offlineResult.errorCode, "ONLINE_REQUIRED");
  assert.equal(offlineResult.allowedActions.addCash, true);
  assert.equal(offline.lease.clearCalls, 0);

  const ambiguous = createHarness();
  const unknown = attempt({ state: "Unknown" });
  ambiguous.attempts.put(unknown);
  ambiguous.mixed.onlineResult = mixed("unknown", {
    attemptId: unknown.attemptId,
    errorCode: "PAYMENT_STATUS_UNKNOWN",
  });
  const unknownResult = await ambiguous.runtime().start(startInput());
  assert.equal(unknownResult.status, "unknown");
  assert.deepEqual(unknownResult.allowedActions, {
    start: false,
    changeProvider: false,
    recover: true,
    cancel: false,
    addCash: false,
    removeTender: false,
  });
  assert.equal(ambiguous.lease.clearCalls, 0);
  assert.equal(ambiguous.lease.releaseCalls, 0);
});

test("Approved 后 completion 崩溃保持同一 OrderGuid/attempt/cart lease 等待恢复", async () => {
  const harness = createHarness();
  const approved = attempt({ state: "Approved" });
  harness.attempts.put(approved);
  harness.mixed.onlineResult = mixed("recovery-required", {
    attemptId: approved.attemptId,
    errorCode: "APPROVED_COMPLETION_FAILED",
  });

  const result = await harness.runtime().start(startInput());

  assert.equal(result.orderGuid, "order-1");
  assert.equal(result.attemptId, "attempt-1");
  assert.equal(result.errorCode, "APPROVED_COMPLETION_FAILED");
  assert.equal(result.allowedActions.recover, true);
  assert.equal(harness.lease.clearCalls, 0);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("Cancelled 先以原 action 耐久 close，严格成功后才释放 lease；Unknown/Approved 不 close", async () => {
  const safe = createHarness();
  const submitted = attempt({ state: "Submitted" });
  safe.attempts.put(submitted);
  safe.drafts.recovery = cancellationRecovery(submitted);

  const cancelled = await safe.runtime().cancel({
    orderGuid: submitted.orderGuid,
    attemptId: submitted.attemptId,
  });

  assert.equal(cancelled.status, "cancelled");
  assert.equal(safe.attempts.cancelCalls, 1);
  assert.equal(safe.drafts.closeCalls, 1);
  assert.deepEqual(safe.drafts.closeInputs, [
    { orderGuid: "order-1", actionId: "card-action-1" },
  ]);
  assert.equal(safe.lease.releaseCalls, 1);
  assert.equal(safe.lease.clearCalls, 0);
  assert.deepEqual(safe.events, [
    "attempt:cancel",
    "draft:close",
    "lease:release",
  ]);

  const ambiguous = createHarness();
  const unknown = attempt({ state: "Unknown" });
  ambiguous.attempts.put(unknown);

  const blocked = await ambiguous.runtime().cancel({
    orderGuid: unknown.orderGuid,
    attemptId: unknown.attemptId,
  });

  assert.equal(blocked.status, "unknown");
  assert.equal(blocked.allowedActions.cancel, false);
  assert.equal(ambiguous.attempts.cancelCalls, 0);
  assert.equal(ambiguous.drafts.closeCalls, 0);
  assert.equal(ambiguous.lease.releaseCalls, 0);
  assert.equal(ambiguous.lease.clearCalls, 0);

  const approvedHarness = createHarness();
  const approved = attempt({ state: "Approved" });
  approvedHarness.attempts.put(approved);
  approvedHarness.drafts.recovery = cancellationRecovery(approved);

  const approvedResult = await approvedHarness.runtime().cancel({
    orderGuid: approved.orderGuid,
    attemptId: approved.attemptId,
  });

  assert.equal(approvedResult.status, "recovery-required");
  assert.equal(approvedHarness.attempts.cancelCalls, 0);
  assert.equal(approvedHarness.drafts.closeCalls, 0);
  assert.equal(approvedHarness.lease.releaseCalls, 0);
});

test("Square 终端先取消后自动耐久结束支付并允许返回收银", async () => {
  const harness = createHarness();
  const pending = attempt({ state: "Pending" });
  const cancelledAttempt = { ...pending, state: "Cancelled" as const };
  harness.attempts.put(pending);
  harness.mixed.recoverOnlineAttempt = async () => {
    harness.attempts.put(cancelledAttempt);
    harness.drafts.recovery = cancellationRecovery(cancelledAttempt);
    return mixed("cancelled", { attemptId: cancelledAttempt.attemptId });
  };
  const runtime = harness.runtime();

  const terminalCancelled = await runtime.recover({
    orderGuid: pending.orderGuid,
    attemptId: pending.attemptId,
  });

  assert.equal(terminalCancelled.status, "cancelled");
  assert.equal(harness.attempts.cancelCalls, 0);
  assert.deepEqual(harness.drafts.closeInputs, [
    { orderGuid: "order-1", actionId: "card-action-1" },
  ]);
  assert.equal(harness.lease.releaseCalls, 1);
  assert.equal(terminalCancelled.attemptId, null);
  assert.deepEqual(terminalCancelled.allowedActions, {
    start: false,
    changeProvider: false,
    recover: false,
    cancel: false,
    addCash: false,
    removeTender: false,
  });
});

test("冷启动发现 Square 已取消 action 时复用原 action 做本地收尾", async () => {
  const harness = createHarness();
  const cancelledAttempt = attempt({ state: "Cancelled" });
  harness.attempts.put(cancelledAttempt);
  harness.drafts.recovery = {
    ...cancellationRecovery(cancelledAttempt),
    attemptId: null,
  };
  harness.mixed.onlineResult = mixed("cancelled", {
    attemptId: cancelledAttempt.attemptId,
  });

  const result = await harness.runtime().resumeCurrent();

  assert.equal(harness.attempts.cancelCalls, 0);
  assert.deepEqual(harness.mixed.lastOnlineInput, {
    actionId: "card-action-1",
    orderGuid: "order-1",
    provider: "square",
    amount: aud(1_000),
  });
  assert.deepEqual(harness.drafts.closeInputs, [
    { orderGuid: "order-1", actionId: "card-action-1" },
  ]);
  assert.equal(harness.lease.releaseCalls, 1);
  assert.equal(result?.status, "cancelled");
  assert.equal(result?.attemptId, null);
});

test("Square 终端取消但 immutable action 缺失时保持锁定且不释放 lease", async () => {
  const harness = createHarness();
  const pending = attempt({ state: "Pending" });
  const cancelledAttempt = { ...pending, state: "Cancelled" as const };
  harness.attempts.put(pending);
  harness.mixed.recoverOnlineAttempt = async () => {
    harness.attempts.put(cancelledAttempt);
    harness.drafts.recovery = {
      draft: harness.drafts.current,
      attemptId: null,
      preparedAction: null,
    };
    return mixed("cancelled", { attemptId: cancelledAttempt.attemptId });
  };

  const result = await harness.runtime().recover({
    orderGuid: pending.orderGuid,
    attemptId: pending.attemptId,
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.errorCode, "PAYMENT_CANCEL_FAILED");
  assert.equal(result.attemptId, cancelledAttempt.attemptId);
  assert.equal(result.allowedActions.cancel, true);
  assert.equal(harness.attempts.cancelCalls, 0);
  assert.equal(harness.drafts.closeCalls, 0);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("Square 终端取消后的本地 close 失败时继续锁定恢复且不释放 lease", async () => {
  const harness = createHarness();
  const pending = attempt({ state: "Pending" });
  const cancelledAttempt = { ...pending, state: "Cancelled" as const };
  harness.attempts.put(pending);
  harness.drafts.closeError = new Error("sqlite close failed");
  harness.mixed.recoverOnlineAttempt = async () => {
    harness.attempts.put(cancelledAttempt);
    harness.drafts.recovery = cancellationRecovery(cancelledAttempt);
    return mixed("cancelled", { attemptId: cancelledAttempt.attemptId });
  };
  const runtime = harness.runtime();

  const result = await runtime.recover({
    orderGuid: pending.orderGuid,
    attemptId: pending.attemptId,
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.errorCode, "PAYMENT_CANCEL_FAILED");
  assert.equal(result.attemptId, cancelledAttempt.attemptId);
  assert.deepEqual(result.allowedActions, {
    start: false,
    changeProvider: false,
    recover: false,
    cancel: true,
    addCash: false,
    removeTender: false,
  });
  assert.equal(harness.drafts.closeCalls, 1);
  assert.equal(harness.lease.releaseCalls, 0);

  harness.drafts.closeError = null;
  const retried = await runtime.cancel({
    orderGuid: cancelledAttempt.orderGuid,
    attemptId: cancelledAttempt.attemptId,
  });

  assert.equal(harness.attempts.cancelCalls, 0);
  assert.equal(harness.drafts.closeCalls, 2);
  assert.equal(harness.lease.releaseCalls, 1);
  assert.equal(retried.status, "cancelled");
  assert.equal(retried.attemptId, null);
});

test("Cancelled close 幂等重放仍验证投影后释放，且沿用同一 immutable actionId", async () => {
  const harness = createHarness();
  const cancelledAttempt = attempt({ state: "Cancelled" });
  harness.attempts.put(cancelledAttempt);
  harness.drafts.recovery = cancellationRecovery(cancelledAttempt);
  harness.drafts.closeReplayed = true;

  const result = await harness.runtime().cancel({
    orderGuid: cancelledAttempt.orderGuid,
    attemptId: cancelledAttempt.attemptId,
  });

  assert.equal(result.status, "cancelled");
  assert.equal(harness.drafts.closeCalls, 1);
  assert.equal(harness.drafts.closeInputs[0]?.actionId, "card-action-1");
  assert.equal(harness.lease.releaseCalls, 1);
});

test("Cancelled close 抛错时不 release，返回冻结的 recovery-required/PAYMENT_CANCEL_FAILED", async () => {
  const harness = createHarness();
  const submitted = attempt({ state: "Submitted" });
  harness.attempts.put(submitted);
  harness.drafts.recovery = cancellationRecovery(submitted);
  harness.drafts.closeError = new Error("sqlite close failed");

  const result = await harness.runtime().cancel({
    orderGuid: submitted.orderGuid,
    attemptId: submitted.attemptId,
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.errorCode, "PAYMENT_CANCEL_FAILED");
  assert.deepEqual(result.allowedActions, {
    start: false,
    changeProvider: false,
    recover: false,
    cancel: true,
    addCash: false,
    removeTender: false,
  });
  assert.equal(harness.drafts.closeCalls, 1);
  assert.equal(harness.lease.releaseCalls, 0);
  assert.deepEqual(harness.events, ["attempt:cancel", "draft:close"]);
});

test("缺失或不匹配的 immutable payment action 时不调用 cancel/provider/close", async () => {
  const harness = createHarness();
  const submitted = attempt({ state: "Submitted" });
  harness.attempts.put(submitted);
  harness.drafts.recovery = {
    ...cancellationRecovery(submitted),
    preparedAction: {
      ...cancellationRecovery(submitted).preparedAction,
      amount: aud(999),
    },
  };

  const result = await harness.runtime().cancel({
    orderGuid: submitted.orderGuid,
    attemptId: submitted.attemptId,
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.errorCode, "PAYMENT_CANCEL_FAILED");
  assert.equal(harness.attempts.cancelCalls, 0);
  assert.equal(harness.drafts.closeCalls, 0);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("close 返回不同 order/intent/revision 或活动 tender 投影时均 fail closed", async () => {
  const invalidDrafts: PaymentCheckoutDraft[] = [
    draft({ orderGuid: "other-order" }),
    draft({ checkoutIntentId: "other-intent" }),
    draft({ cartRevision: 8 }),
    draft({
      remaining: aud(500),
      tenders: [
        {
          tenderGuid: "unexpected-tender",
          method: "cash",
          amount: aud(500),
          reversible: true,
        },
      ],
    }),
  ];

  for (const invalid of invalidDrafts) {
    const harness = createHarness();
    const submitted = attempt({ state: "Submitted" });
    harness.attempts.put(submitted);
    harness.drafts.recovery = cancellationRecovery(submitted);
    harness.drafts.closeDraft = invalid;

    const result = await harness.runtime().cancel({
      orderGuid: submitted.orderGuid,
      attemptId: submitted.attemptId,
    });

    assert.equal(result.status, "recovery-required");
    assert.equal(result.errorCode, "PAYMENT_CANCEL_FAILED");
    assert.equal(harness.lease.releaseCalls, 0);
  }
});

test("Approved 已耐久结算为 partial 后解除 attempt 阻塞，可追加其他 method 但不能再加活动 card", async () => {
  const harness = createHarness();
  harness.mixed.addOnlineTender = async () => {
    const approved = attempt({ state: "Approved", amount: aud(500) });
    harness.attempts.put(approved);
    harness.drafts.current = draft({
      state: "Completing",
      remaining: aud(500),
      tenders: [
        {
          tenderGuid: "card-partial",
          method: "card",
          amount: aud(500),
          reversible: true,
        },
      ],
    });
    return mixed("partial", {
      attemptId: approved.attemptId,
      tenderGuid: "card-partial",
      remaining: aud(500),
    });
  };
  const runtime = harness.runtime();

  const result = await runtime.start({
    ...startInput(),
    amount: aud(500),
  });

  assert.equal(result.status, "partial");
  assert.equal(result.allowedActions.start, true);
  assert.equal(result.allowedActions.changeProvider, true);
  assert.equal(result.allowedActions.addCash, true);
  assert.equal(result.allowedActions.removeTender, true);
  assert.equal(result.allowedActions.recover, false);
  assert.equal(result.allowedActions.cancel, false);
});

test("冷启动可发现 DraftPrepared(null attempt)，保留 promotion/asOf/手工折扣并安全继续同一订单", async () => {
  const harness = createHarness();
  harness.drafts.recovery = {
    draft: harness.drafts.current,
    attemptId: null,
    preparedAction: null,
  };
  harness.mixed.onlineResult = mixed("recovery-required", {
    errorCode: "ONLINE_REQUIRED",
  });
  const runtime = harness.runtime();

  const found = await runtime.findRecoveryRequired();
  assert.equal(found?.status, "draft-prepared");
  assert.equal(found?.attemptId, null);
  assert.equal(found?.attemptCreatedAtIso, null);
  const resumed = await runtime.resumeCurrent({
    actionId: "resume-action",
    provider: "square",
    amount: aud(1_000),
  });

  assert.equal(resumed?.orderGuid, "order-1");
  assert.equal(harness.drafts.createCalls, 0);
  assert.equal(harness.lease.value.pricingState.asOfIso, "2026-07-28T00:00:00.000Z");
  assert.equal(harness.lease.value.pricingState.promotions[0]?.id, "promo-1");
  assert.deepEqual(
    harness.lease.value.pricingState.lines[0]?.discountState,
    { kind: "manual-percent", basisPoints: 1_000 },
  );
});

test("binding 已落库但 attempt 未插入时自动复用原 action；Voucher 不再索取券码", async () => {
  const harness = createHarness();
  harness.drafts.recovery = {
    draft: harness.drafts.current,
    attemptId: null,
    preparedAction: {
      actionId: "bound-voucher-action",
      provider: "voucher",
      operation: "purchase",
      amount: aud(1_000),
    },
  };
  let prepareCalls = 0;
  harness.voucherPreparation = {
    async preparePurchase() {
      prepareCalls += 1;
      throw new Error("prepared voucher context must be reused");
    },
  };
  harness.mixed.onlineResult = mixed("recovery-required", {
    errorCode: "ONLINE_REQUIRED",
  });
  const runtime = harness.runtime();

  const found = await runtime.findRecoveryRequired();
  assert.equal(found?.attemptId, null);
  assert.equal(found?.provider, "voucher");
  assert.equal(found?.status, "recovery-required");
  assert.equal(
    found?.errorCode,
    "PAYMENT_PREPARED_ACTION_RECOVERY_REQUIRED",
  );
  assert.equal(found?.allowedActions.start, false);
  assert.equal(found?.allowedActions.recover, true);

  const resumed = await runtime.resumeCurrent();
  assert.equal(resumed?.orderGuid, "order-1");
  assert.equal(prepareCalls, 0);
  assert.deepEqual(harness.mixed.lastOnlineInput, {
    actionId: "bound-voucher-action",
    orderGuid: "order-1",
    provider: "voucher",
    amount: aud(1_000),
  });
});

test("零 tender 的 DraftPrepared 可显式 CAS 放弃并安全释放 lease，包括仅有 binding 的旧单", async () => {
  const pure = createHarness();
  pure.drafts.recovery = {
    draft: pure.drafts.current,
    attemptId: null,
    preparedAction: null,
  };
  const runtime = pure.runtime();
  const found = await runtime.findRecoveryRequired();
  assert.equal(found?.allowedActions.cancel, true);

  const abandoned = await runtime.abandonPrepared({
    orderGuid: "order-1",
    actionId: "abandon-action-1",
  });
  assert.equal(abandoned.status, "cancelled");
  assert.deepEqual(abandoned.allowedActions, {
    start: false,
    changeProvider: false,
    recover: false,
    cancel: false,
    addCash: false,
    removeTender: false,
  });
  assert.equal(pure.drafts.abandonCalls, 1);
  assert.equal(pure.lease.releaseCalls, 1);

  const bound = createHarness();
  bound.drafts.recovery = {
    draft: bound.drafts.current,
    attemptId: null,
    preparedAction: {
      actionId: "bound-action",
      provider: "square",
      operation: "purchase",
      amount: aud(1_000),
    },
  };
  assert.equal((await bound.runtime().findRecoveryRequired())?.allowedActions.cancel, true);
  const boundAbandoned = await bound.runtime().abandonPrepared({
    orderGuid: "order-1", actionId: "bound-abandon",
  });
  assert.equal(boundAbandoned.status, "cancelled");
  assert.equal(bound.drafts.abandonCalls, 1);
  assert.equal(bound.lease.releaseCalls, 1);
});

test("耐久关闭提交后收银员会话失效仍立即且仅释放一次购物车 lease", async () => {
  for (const draftToClose of [
    draft(),
    draft({
      state: "Completing",
      cancellableAfterReversal: true,
    }),
  ]) {
    const harness = createHarness(draftToClose);
    harness.drafts.recovery = {
      draft: draftToClose,
      attemptId: null,
      preparedAction: null,
    };
    harness.drafts.onAbandonCommitted = () => {
      harness.session.active = false;
    };

    const cancelled = await harness.runtime().abandonPrepared({
      orderGuid: draftToClose.orderGuid,
      actionId: `close-${draftToClose.cancellableAfterReversal ? "reversed" : "prepared"}`,
    });

    assert.equal(cancelled.status, "cancelled");
    assert.equal(cancelled.orderGuid, draftToClose.orderGuid);
    assert.equal(harness.drafts.abandonCalls, 1);
    assert.equal(harness.lease.releaseCalls, 1);
  }
});

test("耐久关闭在提交前被 store 拒绝时不得释放购物车 lease", async () => {
  const harness = createHarness();
  harness.drafts.recovery = {
    draft: harness.drafts.current,
    attemptId: null,
    preparedAction: null,
  };
  harness.drafts.abandonError = new Error("sqlite compare-and-swap rejected");

  await assert.rejects(
    () => harness.runtime().abandonPrepared({
      orderGuid: "order-1",
      actionId: "abandon-rejected",
    }),
    /sqlite compare-and-swap rejected/,
  );
  assert.equal(harness.drafts.abandonCalls, 1);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("Unknown 冷恢复只复用同一 attempt；恢复 Approved 完成后才清购物车", async () => {
  const harness = createHarness();
  const unknown = attempt({
    state: "Unknown",
    createdAtIso: "2026-08-09T00:00:00.000Z",
  });
  harness.attempts.put(unknown);
  harness.drafts.recovery = {
    draft: harness.drafts.current,
    attemptId: unknown.attemptId,
    preparedAction: null,
  };
  harness.mixed.recoverOnlineAttempt = async (input) => {
    assert.deepEqual(input, {
      orderGuid: "order-1",
      attemptId: "attempt-1",
    });
    const approved = { ...unknown, state: "Approved" as const };
    harness.attempts.put(approved);
    harness.drafts.current = draft({
      state: "PendingSync",
      remaining: aud(0),
      tenders: [
        {
          tenderGuid: "tender-card",
          method: "card",
          amount: aud(1_000),
          reversible: true,
        },
      ],
    });
    return mixed("completed", {
      attemptId: approved.attemptId,
      tenderGuid: "tender-card",
      remaining: aud(0),
    });
  };

  const result = await harness.runtime().resumeCurrent();

  assert.equal(result?.orderGuid, "order-1");
  assert.equal(result?.status, "completed");
  assert.equal(result?.attemptCreatedAtIso, unknown.createdAtIso);
  assert.equal(harness.lease.clearCalls, 1);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("Square 恢复把本次 absolute deadline 控制原样传给 mixed coordinator", async () => {
  const harness = createHarness();
  const pending = attempt({ state: "Pending" });
  harness.attempts.put(pending);
  const controller = new AbortController();
  const deadlineAtMs = Date.parse(pending.createdAtIso) + 90_000;

  await harness.runtime().recover({
    orderGuid: pending.orderGuid,
    attemptId: pending.attemptId,
    signal: controller.signal,
    deadlineAtMs,
  });

  assert.strictEqual(harness.mixed.lastRecoveryInput?.signal, controller.signal);
  assert.equal(harness.mixed.lastRecoveryInput?.deadlineAtMs, deadlineAtMs);
});

test("不可逆支付已耐久完成后即使旧可信会话失效仍按原 lease 清车并返回成功", async () => {
  const harness = createHarness();
  harness.mixed.addOnlineTender = async () => {
    const approved = attempt({ state: "Approved" });
    harness.attempts.put(approved);
    harness.drafts.current = draft({
      state: "PendingSync",
      remaining: aud(0),
      tenders: [{
        tenderGuid: "card-completed",
        method: "card",
        amount: aud(1_000),
        reversible: true,
      }],
    });
    harness.session.active = false;
    return mixed("completed", {
      attemptId: approved.attemptId,
      tenderGuid: "card-completed",
      remaining: aud(0),
    });
  };

  const result = await harness.runtime().start(startInput());

  assert.equal(result.status, "completed");
  assert.equal(harness.lease.clearCalls, 1);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("现金已耐久完成后旧可信会话失效仍按原 lease 清车并返回成功", async () => {
  const harness = createHarness();
  harness.drafts.onRead = () => harness.session.assertActive();
  harness.mixed.addCashTender = async () => {
    harness.drafts.current = draft({
      state: "PendingSync",
      remaining: aud(0),
      tenders: [{
        tenderGuid: "cash-completed",
        method: "cash",
        amount: aud(1_000),
        reversible: false,
      }],
    });
    harness.session.active = false;
    return mixed("completed", {
      tenderGuid: "cash-completed",
      remaining: aud(0),
      cashSettlement: {
        tendered: aud(1_000),
        applied: aud(1_000),
        change: aud(0),
      },
    });
  };

  const result = await harness.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-completed-action",
    amount: aud(1_000),
  });

  assert.equal(result.status, "completed");
  assert.equal(harness.lease.clearCalls, 1);
  assert.equal(harness.lease.releaseCalls, 0);
  // addCash 在提交前为取得当前 draft 已普通读取一次；完成后不能再走受会话保护的 read。
  assert.equal(harness.drafts.readCalls, 1);
  assert.equal(harness.drafts.postCommitReadCalls, 1);
});

test("完成态名称但冻结余额非零时不得按幂等 replay 跳过本次 action 证明", async () => {
  const before = draft({ state: "PendingSync", remaining: aud(1_000) });
  const harness = createHarness(before);
  harness.mixed.addOnlineTender = async () => {
    harness.drafts.current = completedCardDraft("card-invalid-replay");
    return mixed("completed", { remaining: aud(0) });
  };

  const result = await harness.runtime().start(startInput());

  assert.equal(result.status, "recovery-required");
  assert.equal(result.errorCode, "APPROVED_TRUTH_MISMATCH");
  assert.equal(harness.lease.clearCalls, 0);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("本次现金完成必须按冻结 draft 的五分规则重算全部结算字段", async () => {
  const harness = createHarness();
  harness.mixed.addCashTender = async () => {
    harness.drafts.current = draft({
      state: "PendingSync",
      remaining: aud(0),
      tenders: [{
        tenderGuid: "cash-forged-settlement",
        method: "cash",
        amount: aud(1_000),
        reversible: false,
      }],
    });
    return mixed("completed", {
      tenderGuid: "cash-forged-settlement",
      remaining: aud(0),
      cashSettlement: {
        tendered: aud(0),
        applied: aud(1_000),
        change: aud(0),
      },
    });
  };

  const result = await harness.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-forged-settlement-action",
    amount: aud(1_000),
  });

  assert.equal(result.status, "recovery-required");
  assert.equal(result.errorCode, "APPROVED_TRUTH_MISMATCH");
  assert.equal(harness.lease.clearCalls, 0);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("非 completed 的 pending/unknown 仍拒绝跨 provider attempt", async () => {
  for (const state of ["Pending", "Unknown"] as const) {
    const harness = createHarness();
    const crossProvider = attempt({ state, provider: "linkly-cloud" });
    harness.attempts.put(crossProvider);
    harness.mixed.onlineResult = mixed(
      state === "Pending" ? "pending" : "unknown",
      { attemptId: crossProvider.attemptId },
    );

    await assert.rejects(
      () => harness.runtime().start(startInput()),
      (error: unknown) => {
        assert.ok(error instanceof PaymentCheckoutRuntimeError);
        assert.equal(error.code, "PAYMENT_ATTEMPT_IDENTITY_MISMATCH");
        return true;
      },
    );
    assert.equal(harness.lease.clearCalls, 0);
    assert.equal(harness.lease.releaseCalls, 0);
  }
});

test("本次完成缺少持久完成态、attempt 或 tender 时必须保留原 lease", async () => {
  const cases = [
    {
      name: "冻结 draft 仍是 Completing",
      configure: (harness: ReturnType<typeof createHarness>) => {
        harness.mixed.addCashTender = async () => {
          harness.drafts.current = draft({
            state: "Completing",
            remaining: aud(0),
            tenders: [{
              tenderGuid: "cash-completing",
              method: "cash",
              amount: aud(1_000),
              reversible: false,
            }],
          });
          return mixed("completed", {
            tenderGuid: "cash-completing",
            remaining: aud(0),
            cashSettlement: {
              tendered: aud(1_000),
              applied: aud(1_000),
              change: aud(0),
            },
          });
        };
        return harness.runtime().addCash({
          orderGuid: "order-1",
          actionId: "cash-completing-action",
          amount: aud(1_000),
        });
      },
    },
    {
      name: "在线完成缺少 attempt",
      configure: (harness: ReturnType<typeof createHarness>) => {
        harness.mixed.addOnlineTender = async () => {
          harness.drafts.current = completedCardDraft("card-without-attempt");
          return mixed("completed", {
            tenderGuid: "card-without-attempt",
            remaining: aud(0),
          });
        };
        return harness.runtime().start(startInput());
      },
    },
    {
      name: "在线完成缺少 tender",
      configure: (harness: ReturnType<typeof createHarness>) => {
        const approved = attempt({ state: "Approved" });
        harness.attempts.put(approved);
        harness.mixed.addOnlineTender = async () => {
          harness.drafts.current = completedCardDraft("card-without-result-tender");
          return mixed("completed", {
            attemptId: approved.attemptId,
            remaining: aud(0),
          });
        };
        return harness.runtime().start(startInput());
      },
    },
    {
      name: "现金完成缺少结算事实",
      configure: (harness: ReturnType<typeof createHarness>) => {
        harness.mixed.addCashTender = async () => {
          harness.drafts.current = draft({
            state: "PendingSync",
            remaining: aud(0),
            tenders: [{
              tenderGuid: "cash-without-settlement",
              method: "cash",
              amount: aud(1_000),
              reversible: false,
            }],
          });
          return mixed("completed", {
            tenderGuid: "cash-without-settlement",
            remaining: aud(0),
          });
        };
        return harness.runtime().addCash({
          orderGuid: "order-1",
          actionId: "cash-without-settlement-action",
          amount: aud(1_000),
        });
      },
    },
  ] as const;

  for (const current of cases) {
    const harness = createHarness();
    const result = await current.configure(harness);
    assert.equal(result.status, "recovery-required", current.name);
    assert.equal(result.errorCode, "APPROVED_TRUTH_MISMATCH", current.name);
    assert.equal(harness.lease.clearCalls, 0, current.name);
    assert.equal(harness.lease.releaseCalls, 0, current.name);
  }
});

test("本次完成的 post-commit draft 必须与原 checkout、revision 和 total 绑定", async () => {
  const cases = [
    completedCardDraft("card-revision-mismatch", { cartRevision: 8 }),
    draft({
      state: "PendingSync",
      total: aud(900),
      remaining: aud(0),
      tenders: [{
        tenderGuid: "card-total-mismatch",
        method: "card",
        amount: aud(900),
        reversible: true,
      }],
    }),
  ];

  for (const after of cases) {
    const harness = createHarness();
    const approved = attempt({ state: "Approved", amount: after.tenders[0]!.amount });
    harness.attempts.put(approved);
    harness.mixed.addOnlineTender = async () => {
      harness.drafts.current = after;
      return mixed("completed", {
        attemptId: approved.attemptId,
        tenderGuid: after.tenders[0]!.tenderGuid,
        remaining: aud(0),
      });
    };

    const result = await harness.runtime().start(startInput());

    assert.equal(result.status, "recovery-required");
    assert.equal(result.errorCode, "APPROVED_TRUTH_MISMATCH");
    assert.equal(harness.lease.clearCalls, 0);
    assert.equal(harness.lease.releaseCalls, 0);
  }
});

test("本次在线完成的 provider/operation/amount 必须与持久 Approved attempt 相符", async () => {
  const cases = [
    attempt({ state: "Approved", provider: "linkly-cloud" }),
    attempt({ state: "Approved", operation: "refund" }),
    attempt({ state: "Approved", amount: aud(500) }),
  ];

  for (const approved of cases) {
    const harness = createHarness();
    harness.attempts.put(approved);
    harness.mixed.addOnlineTender = async () => {
      harness.drafts.current = completedCardDraft("card-attempt-mismatch");
      return mixed("completed", {
        attemptId: approved.attemptId,
        tenderGuid: "card-attempt-mismatch",
        remaining: aud(0),
      });
    };

    const result = await harness.runtime().start(startInput());

    assert.equal(result.status, "recovery-required");
    assert.equal(result.errorCode, "APPROVED_TRUTH_MISMATCH");
    assert.equal(harness.lease.clearCalls, 0);
    assert.equal(harness.lease.releaseCalls, 0);
  }
});

test("已完成订单的 coordinator 幂等 replay 可省略 attempt/tender 但仍只清原 lease", async () => {
  const before = completedCardDraft("already-completed-card");
  const harness = createHarness(before);
  const approved = attempt({ state: "Approved" });
  harness.attempts.put(approved);
  harness.mixed.recoverOnlineAttempt = async () =>
    mixed("completed", { remaining: aud(0) });

  const result = await harness.runtime().recover({
    orderGuid: before.orderGuid,
    attemptId: approved.attemptId,
  });

  assert.equal(result.status, "completed");
  assert.equal(harness.lease.clearCalls, 1);
  assert.equal(harness.lease.releaseCalls, 0);
});

test("Voucher 上下文必在 provider 前耐久准备，且六项精确权限都经 guard", async () => {
  const permissions = new Set<PaymentPermissionCode>();
  const voucher = createHarness();
  voucher.permissions.onAssert = (code) => permissions.add(code);
  voucher.providers.unblock("voucher");
  voucher.events.length = 0;
  voucher.voucherPreparation = {
    async preparePurchase() {
      voucher.events.push("voucher-prepared");
      return { prepared: true };
    },
  };
  voucher.mixed.addOnlineTender = async () => {
    voucher.events.push("provider");
    return mixed("declined", { attemptId: null });
  };
  await voucher.runtime().start({
    ...startInput(),
    provider: "voucher",
    voucherCode: "SECRET-VOUCHER-CODE",
  });
  assert.ok(
    voucher.events.indexOf("voucher-prepared") <
      voucher.events.indexOf("provider"),
  );

  const cash = createHarness();
  cash.permissions.onAssert = (code) => permissions.add(code);
  cash.mixed.cashResult = mixed("partial", {
    tenderGuid: "cash-tender",
    remaining: aud(500),
  });
  cash.drafts.afterCash = draft({
    state: "Completing",
    remaining: aud(500),
    tenders: [
      {
        tenderGuid: "cash-tender",
        method: "cash",
        amount: aud(500),
        reversible: true,
      },
    ],
  });
  await cash.runtime().addCash({
    orderGuid: "order-1",
    actionId: "cash-action",
    amount: aud(500),
  });

  const remove = createHarness(
    draft({
      state: "Completing",
      remaining: aud(500),
      tenders: [
        {
          tenderGuid: "cash-tender",
          method: "cash",
          amount: aud(500),
          reversible: true,
        },
      ],
    }),
  );
  remove.permissions.onAssert = (code) => permissions.add(code);
  remove.mixed.removeResult = mixed("partial", {
    tenderGuid: "cash-reversal",
    remaining: aud(1_000),
  });
  remove.drafts.afterRemove = draft();
  await remove.runtime().removeTender({
    orderGuid: "order-1",
    actionId: "remove-action",
    tenderGuid: "cash-tender",
  });

  const card = createHarness();
  card.permissions.onAssert = (code) => permissions.add(code);
  card.mixed.onlineResult = mixed("recovery-required", {
    errorCode: "ONLINE_REQUIRED",
  });
  await card.runtime().start(startInput());

  assert.deepEqual(
    new Set(permissions),
    new Set(Object.values(PAYMENT_PERMISSION)),
  );
});

test("每种活动 tender 最多一个；reversal 后 draft 移除活动行才允许重加", async () => {
  const activeCard = createHarness(
    draft({
      state: "Completing",
      remaining: aud(500),
      tenders: [
        {
          tenderGuid: "card-existing",
          method: "card",
          amount: aud(500),
          reversible: true,
        },
      ],
    }),
  );
  await assert.rejects(
    () =>
      activeCard.runtime().start({
        ...startInput(),
        amount: aud(500),
      }),
    (error: unknown) => {
      assert.ok(error instanceof PaymentCheckoutRuntimeError);
      assert.equal(error.code, "PAYMENT_TENDER_METHOD_ALREADY_ACTIVE");
      return true;
    },
  );
  assert.equal(activeCard.mixed.onlineCalls, 0);

  const activeCash = createHarness(
    draft({
      state: "Completing",
      remaining: aud(500),
      tenders: [
        {
          tenderGuid: "cash-existing",
          method: "cash",
          amount: aud(500),
          reversible: true,
        },
      ],
    }),
  );
  await assert.rejects(
    () =>
      activeCash.runtime().addCash({
        orderGuid: "order-1",
        actionId: "cash-duplicate",
        amount: aud(500),
      }),
    (error: unknown) => {
      assert.ok(error instanceof PaymentCheckoutRuntimeError);
      assert.equal(error.code, "PAYMENT_TENDER_METHOD_ALREADY_ACTIVE");
      return true;
    },
  );
  assert.equal(activeCash.mixed.cashCalls, 0);
});

test("最后一笔现金已追加 reversal 后可继续支付，也可耐久取消并释放购物车", async () => {
  const harness = createHarness(
    draft({
      state: "Completing",
      remaining: aud(600),
      tenders: [
        {
          tenderGuid: "cash-to-remove",
          method: "cash",
          amount: aud(400),
          reversible: true,
        },
      ],
    }),
  );
  const fullyReversed = draft({
    state: "Completing",
    remaining: aud(1_000),
    cancellableAfterReversal: true,
    tenders: [],
  });
  harness.mixed.removeResult = mixed("partial", {
    tenderGuid: "cash-reversal",
    remaining: aud(1_000),
  });
  harness.drafts.afterRemove = fullyReversed;

  const removed = await harness.runtime().removeTender({
    orderGuid: "order-1",
    actionId: "remove-last-cash",
    tenderGuid: "cash-to-remove",
  });

  assert.equal(removed.status, "draft-prepared");
  assert.equal(removed.allowedActions.addCash, true);
  assert.equal(removed.allowedActions.start, true);
  assert.equal(removed.allowedActions.cancel, true);
  assert.deepEqual(removed.tenders, []);
  harness.drafts.recovery = {
    draft: fullyReversed,
    attemptId: null,
    preparedAction: null,
  };

  const cancelled = await harness.runtime().abandonPrepared({
    orderGuid: "order-1",
    actionId: "cancel-fully-reversed",
  });

  assert.equal(cancelled.status, "cancelled");
  assert.equal(harness.drafts.abandonCalls, 1);
  assert.equal(harness.lease.releaseCalls, 1);
});

function createHarness(
  initialDraft = draft(),
  leaseTotalCents = initialDraft.total.cents,
) {
  const events: string[] = [];
  const session = new SessionGuard();
  const permissions = new PermissionRecorder();
  const drafts = new MemoryDrafts(initialDraft, events);
  const lease = new MemoryLease(events, leaseTotalCents);
  const attempts = new MemoryAttempts(events);
  const providers = new ProviderAvailability();
  const mixed = new MemoryMixed(drafts);
  let voucherPreparation:
    | {
        preparePurchase(input: {
          actionId: string;
          orderGuid: string;
          voucherCode: string;
        }): Promise<{ prepared: true }>;
      }
    | undefined;
  let linklyPaymentSelection:
    | LinklyPaymentTerminalSelectionBindingPort
    | undefined;
  return {
    events,
    session,
    permissions,
    drafts,
    lease,
    attempts,
    providers,
    mixed,
    get voucherPreparation() {
      return voucherPreparation;
    },
    set voucherPreparation(value) {
      voucherPreparation = value;
    },
    get linklyPaymentSelection() {
      return linklyPaymentSelection;
    },
    set linklyPaymentSelection(value) {
      linklyPaymentSelection = value;
    },
    runtime() {
      const options = {
        mixed,
        attempts,
        drafts,
        cartLease: lease,
        providers,
        trustedSession: session,
        permissions,
      };
      return new PaymentCheckoutRuntime(
        {
          ...options,
          ...(voucherPreparation ? { voucherPreparation } : {}),
          ...(linklyPaymentSelection ? { linklyPaymentSelection } : {}),
        },
      );
    },
  };
}

class RecordingLinklyPaymentSelection
  implements LinklyPaymentTerminalSelectionBindingPort
{
  public activeOrderGuid: string | null = null;
  public readonly calls: Array<Readonly<{
    orderGuid: string;
    selection: LinklyPaymentTerminalSelectionExpectation;
  }>> = [];

  public async runWithSelection<T>(
    orderGuid: string,
    selection: LinklyPaymentTerminalSelectionExpectation,
    operation: () => Promise<T>,
  ): Promise<T> {
    this.calls.push({ orderGuid, selection });
    this.activeOrderGuid = orderGuid;
    try {
      return await operation();
    } finally {
      this.activeOrderGuid = null;
    }
  }
}

class SessionGuard {
  public active = true;

  public assertActive(): void {
    if (!this.active) throw new Error("CURRENT_CASHIER_REQUIRED");
  }
}

class PermissionRecorder {
  public onAssert: (code: PaymentPermissionCode) => void = () => {};

  public assert(code: PaymentPermissionCode): void {
    this.onAssert(code);
  }
}

class MemoryLease implements PaymentCartLeasePort {
  public readonly value: PaymentCartLease;
  public acquireCalls = 0;
  public readCalls = 0;
  public clearCalls = 0;
  public releaseCalls = 0;

  public constructor(
    private readonly events: string[],
    totalCents = 1_000,
  ) {
    const base = lease();
    const total = aud(totalCents);
    this.value = {
      ...base,
      total,
      cart: { ...base.cart, actualAmount: total },
    };
  }

  public async acquireExact(input: {
    checkoutIntentId: string;
    expectedRevision: number;
  }): Promise<PaymentCartLease> {
    this.acquireCalls += 1;
    assert.equal(input.checkoutIntentId, this.value.checkoutIntentId);
    assert.equal(input.expectedRevision, this.value.revision);
    return this.value;
  }

  public async readExact(value: PaymentCartLease): Promise<PaymentCartLease> {
    this.readCalls += 1;
    assert.strictEqual(value, this.value);
    return this.value;
  }

  public async clearAfterCompleted(
    value: PaymentCartLease,
    orderGuid: string,
  ): Promise<void> {
    assert.strictEqual(value, this.value);
    assert.equal(orderGuid, "order-1");
    this.clearCalls += 1;
  }

  public async releaseAfterSafeCancel(
    value: PaymentCartLease,
    orderGuid: string,
  ): Promise<void> {
    assert.strictEqual(value, this.value);
    assert.equal(orderGuid, "order-1");
    this.releaseCalls += 1;
    this.events.push("lease:release");
  }
}

class MemoryDrafts implements PaymentCheckoutDraftPort {
  public createCalls = 0;
  public readCalls = 0;
  public postCommitReadCalls = 0;
  public onRead: () => void = () => {};
  public abandonCalls = 0;
  public closeCalls = 0;
  public closeReplayed = false;
  public closeError: unknown = null;
  public abandonError: unknown = null;
  public onAbandonCommitted: (() => void) | null = null;
  public closeDraft: PaymentCheckoutDraft | null = null;
  public readonly closeInputs: Readonly<{
    orderGuid: string;
    actionId: string;
  }>[] = [];
  public recovery: {
    draft: PaymentCheckoutDraft;
    attemptId: string | null;
    preparedAction: PaymentCheckoutPreparedAction | null;
  } | null = null;
  public afterCash: PaymentCheckoutDraft | null = null;
  public afterRemove: PaymentCheckoutDraft | null = null;

  public constructor(
    public current: PaymentCheckoutDraft,
    private readonly events: string[],
  ) {}

  public async createOrReuse(input: {
    checkoutIntentId: string;
    lease: PaymentCartLease;
  }): Promise<PaymentCheckoutDraft> {
    this.createCalls += 1;
    assert.equal(input.checkoutIntentId, this.current.checkoutIntentId);
    assert.equal(input.lease.pricingState.promotions[0]?.id, "promo-1");
    assert.deepEqual(
      input.lease.pricingState.lines[0]?.discountState,
      { kind: "manual-percent", basisPoints: 1_000 },
    );
    return this.current;
  }

  public async read(orderGuid: string): Promise<PaymentCheckoutDraft | null> {
    assert.equal(orderGuid, this.current.orderGuid);
    this.readCalls += 1;
    this.onRead();
    return this.current;
  }

  public async readAfterDurableCompletion(
    orderGuid: string,
  ): Promise<PaymentCheckoutDraft | null> {
    assert.equal(orderGuid, this.current.orderGuid);
    this.postCommitReadCalls += 1;
    return this.current;
  }

  public async findBlockingRecovery() {
    return this.recovery;
  }

  public async abandonPrepared(input: {
    orderGuid: string;
    actionId: string;
  }) {
    assert.equal(input.orderGuid, this.current.orderGuid);
    assert.ok(input.actionId.trim());
    this.abandonCalls += 1;
    if (this.abandonError) throw this.abandonError;
    this.onAbandonCommitted?.();
    return {
      replayed: false,
    };
  }

  public async closeCancelled(input: {
    orderGuid: string;
    actionId: string;
  }) {
    this.closeCalls += 1;
    this.closeInputs.push(input);
    this.events.push("draft:close");
    if (this.closeError) throw this.closeError;
    return {
      draft: this.closeDraft ?? this.current,
      replayed: this.closeReplayed,
    };
  }
}

class MemoryAttempts implements PaymentCheckoutAttemptPort {
  private readonly attempts = new Map<string, PaymentAttempt>();
  public cancelCalls = 0;

  public constructor(private readonly events: string[]) {}

  public put(value: PaymentAttempt): void {
    this.attempts.set(value.attemptId, value);
  }

  public async getAttempt(attemptId: string): Promise<PaymentAttempt | null> {
    return this.attempts.get(attemptId) ?? null;
  }

  public async getBlockingAttempt(
    orderGuid: string,
  ): Promise<PaymentAttempt | null> {
    return (
      [...this.attempts.values()].find(
        (value) =>
          value.orderGuid === orderGuid &&
          ["Created", "Submitted", "Pending", "Unknown", "Approved"].includes(
            value.state,
          ),
      ) ?? null
    );
  }

  public async cancelAttempt(attemptId: string) {
    this.cancelCalls += 1;
    this.events.push("attempt:cancel");
    const current = this.attempts.get(attemptId);
    if (!current) throw new Error("attempt missing");
    const cancelled = { ...current, state: "Cancelled" as const };
    this.put(cancelled);
    return {
      attempt: cancelled,
      receiptText: null,
      responseCode: "CANCELLED",
    };
  }
}

class ProviderAvailability implements PaymentProviderAvailabilityPort {
  private readonly entries = new Map<
    PaymentProvider,
    PaymentProviderAvailability
  >([
    [
      "square",
      { provider: "square", available: true, blocker: null },
    ],
    [
      "linkly-cloud",
      { provider: "linkly-cloud", available: true, blocker: null },
    ],
    [
      "voucher",
      { provider: "voucher", available: true, blocker: null },
    ],
  ]);

  public block(
    provider: PaymentProvider,
    blocker: NonNullable<PaymentProviderAvailability["blocker"]>,
  ): void {
    this.entries.set(provider, { provider, available: false, blocker });
  }

  public unblock(provider: PaymentProvider): void {
    this.entries.set(provider, { provider, available: true, blocker: null });
  }

  public getAvailability(provider: PaymentProvider): PaymentProviderAvailability {
    return (
      this.entries.get(provider) ?? {
        provider,
        available: false,
        blocker: "PAYMENT_PROVIDER_UNKNOWN",
      }
    );
  }

  public listAvailability(): readonly PaymentProviderAvailability[] {
    return [...this.entries.values()];
  }
}

class MemoryMixed implements PaymentCheckoutMixedCoordinatorPort {
  public onlineCalls = 0;
  public cashCalls = 0;
  public removeCalls = 0;
  public lastOnlineInput: {
    actionId: string;
    orderGuid: string;
    provider: PaymentProvider;
    amount: ReturnType<typeof aud>;
    manualConfirmed?: boolean;
  } | null = null;
  public lastCashInput: {
    actionId: string;
    orderGuid: string;
    amount: ReturnType<typeof aud>;
    tenderedAmount?: ReturnType<typeof aud>;
    change?: ReturnType<typeof aud>;
  } | null = null;
  public lastRecoveryInput: {
    orderGuid: string;
    attemptId: string;
    signal?: AbortSignal;
    deadlineAtMs?: number;
  } | null = null;
  public onlineResult = mixed("recovery-required", {
    errorCode: "ONLINE_REQUIRED",
  });
  public cashResult = mixed("partial", { remaining: aud(500) });
  public removeResult = mixed("partial");

  public constructor(private readonly drafts: MemoryDrafts) {}

  public async addOnlineTender(input: {
    actionId: string;
    orderGuid: string;
    provider: PaymentProvider;
    amount: ReturnType<typeof aud>;
  }): Promise<ReturnType<typeof mixed>> {
    this.onlineCalls += 1;
    this.lastOnlineInput = input;
    return this.onlineResult;
  }

  public async recoverOnlineAttempt(input: {
    orderGuid: string;
    attemptId: string;
    signal?: AbortSignal;
    deadlineAtMs?: number;
  }): Promise<ReturnType<typeof mixed>> {
    this.lastRecoveryInput = input;
    return this.onlineResult;
  }

  public async addCashTender(input: {
    actionId: string;
    orderGuid: string;
    amount: ReturnType<typeof aud>;
    tenderedAmount?: ReturnType<typeof aud>;
    change?: ReturnType<typeof aud>;
  }): Promise<ReturnType<typeof mixed>> {
    this.cashCalls += 1;
    this.lastCashInput = input;
    if (this.drafts.afterCash) {
      this.drafts.current = this.drafts.afterCash;
      this.drafts.afterCash = null;
    }
    if (
      this.cashResult.status === "completed" &&
      this.cashResult.cashSettlement === undefined
    ) {
      return {
        ...this.cashResult,
        cashSettlement: {
          tendered: input.tenderedAmount ?? input.amount,
          applied: input.amount,
          change: input.change ?? aud(0),
        },
      };
    }
    return this.cashResult;
  }

  public async removeTender(_input: {
    actionId: string;
    orderGuid: string;
    tenderGuid: string;
  }): Promise<ReturnType<typeof mixed>> {
    this.removeCalls += 1;
    if (this.drafts.afterRemove) {
      this.drafts.current = this.drafts.afterRemove;
      this.drafts.afterRemove = null;
    }
    return this.removeResult;
  }
}

function startInput() {
  return {
    checkoutIntentId: "checkout-intent-1",
    expectedCartRevision: 7,
    actionId: "card-action-1",
    provider: "square" as const,
    amount: aud(1_000),
  };
}

function lease(): PaymentCartLease {
  const pricingState = {
    revision: 7,
    mode: "sale" as const,
    asOfIso: "2026-07-28T00:00:00.000Z",
    promotions: [
      {
        id: "promo-1",
        name: "Promotion",
        effectiveStartIso: "2026-07-01T00:00:00.000Z",
        effectiveEndIso: "2026-07-31T23:59:59.000Z",
        isExclusive: false,
        priority: 1,
        applyQuantity: 2,
        fixedPrice: aud(900),
        maxApplicationsPerOrder: 1,
        products: [{ productCode: "P1", unitWeight: 1 }],
      },
    ],
    lines: [
      {
        lineId: "line-1",
        productCode: "P1",
        itemNumber: "I1",
        lookupCode: "930000000001",
        displayName: "Product 1",
        quantity: 1,
        unitPriceCents: 1_100,
        basePriceSource: "catalog" as const,
        kind: "sale" as const,
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
        discountState: {
          kind: "manual-percent" as const,
          basisPoints: 1_000,
        },
      },
    ],
  };
  const cart = {
    revision: 7,
    mode: "sale" as const,
    lines: [
      {
        lineId: "line-1",
        productCode: "P1",
        itemNumber: "I1",
        lookupCode: "930000000001",
        displayName: "Product 1",
        quantity: "1",
        unitPrice: aud(1_100),
        discount: aud(100),
        actualAmount: aud(1_000),
        priceSource: "catalog" as const,
        kind: "sale" as const,
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
      },
    ],
    subtotal: aud(1_100),
    discount: aud(100),
    actualAmount: aud(1_000),
  };
  return {
    leaseId: "lease-1",
    checkoutIntentId: "checkout-intent-1",
    revision: 7,
    total: aud(1_000),
    cart,
    pricingState,
  };
}

function draft(
  overrides: Partial<PaymentCheckoutDraft> = {},
): PaymentCheckoutDraft {
  return {
    checkoutIntentId: "checkout-intent-1",
    orderGuid: "order-1",
    cartRevision: 7,
    state: "DraftPrepared",
    total: aud(1_000),
    remaining: aud(1_000),
    cancellableAfterReversal: false,
    tenders: [],
    ...overrides,
  };
}

function completedCardDraft(
  tenderGuid: string,
  overrides: Partial<PaymentCheckoutDraft> = {},
): PaymentCheckoutDraft {
  return draft({
    state: "PendingSync",
    remaining: aud(0),
    tenders: [{
      tenderGuid,
      method: "card",
      amount: aud(1_000),
      reversible: true,
    }],
    ...overrides,
  });
}

function attempt(overrides: Partial<PaymentAttempt> = {}): PaymentAttempt {
  return {
    attemptId: "attempt-1",
    idempotencyKey: "idempotency-1",
    orderGuid: "order-1",
    provider: "square",
    operation: "purchase",
    amount: aud(1_000),
    state: "Submitted",
    references: {
      checkoutId: "checkout-secret",
      paymentId: "payment-secret",
      sessionId: "session-secret",
      txnRef: "txn-secret",
      rfn: "rfn-secret",
      voucherReservationToken: "voucher-secret",
    },
    createdAtIso: "2026-07-28T00:00:00.000Z",
    updatedAtIso: "2026-07-28T00:01:00.000Z",
    lastErrorCode: null,
    ...overrides,
  };
}

function cancellationRecovery(value: PaymentAttempt) {
  return {
    draft: draft(),
    attemptId: value.attemptId,
    preparedAction: {
      actionId: "card-action-1",
      provider: value.provider,
      operation: "purchase" as const,
      amount: value.amount,
    },
  };
}

function mixed(
  status:
    | "awaiting-terminal"
    | "pending"
    | "unknown"
    | "partial"
    | "completed"
    | "declined"
    | "cancelled"
    | "recovery-required",
  overrides: Partial<{
    remaining: ReturnType<typeof aud>;
    attemptId: string | null;
    tenderGuid: string | null;
    errorCode: string | null;
    cashSettlement: {
      tendered: ReturnType<typeof aud>;
      applied: ReturnType<typeof aud>;
      change: ReturnType<typeof aud>;
    } | undefined;
  }> = {},
) {
  return {
    status,
    orderGuid: "order-1",
    remaining: aud(1_000),
    attemptId: null,
    tenderGuid: null,
    capability: "available" as const,
    errorCode: null,
    cashSettlement: undefined,
    ...overrides,
  };
}

function aud(cents: number) {
  return { currency: "AUD", cents } as const;
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}
