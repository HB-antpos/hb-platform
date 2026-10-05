import assert from "node:assert/strict";
import test from "node:test";

import { paymentText } from "./payment-copy";
import {
  canSelectPaymentMethod,
  LINKLY_SAFE_OPERATOR_KEYS,
  PaymentPresenter,
  parseAudInput,
} from "./payment-presenter";

import type {
  Money,
  PaymentProvider,
} from "@/core/contracts";
import type {
  LinklyOperatorPublicResult,
  LinklyOperatorRuntimePort,
  LinklySafeOperatorKey,
} from "@/features/payments/runtime/linkly-operator-runtime";
import type {
  LinklyTerminalSelectionPort,
  LinklyTerminalSelectionSnapshot,
} from "@/features/payments/linkly";
import type {
  PaymentCheckoutPublicSnapshot,
  PaymentCheckoutRuntimePort,
} from "@/features/payments/runtime/payment-checkout-runtime";
import type {
  PaymentProviderAvailability,
} from "@/features/payments/runtime/payment-provider-registry";

test("冷启动 Unknown 仅保留恢复动作，重复恢复立即失败且首个恢复完成同一订单", async () => {
  const runtime = new FakePaymentRuntime();
  const pending = deferred<PaymentCheckoutPublicSnapshot>();
  runtime.recovery = snapshot({
    status: "unknown",
    provider: "square",
    attemptId: "attempt-local-1",
    errorCode: "PAYMENT_STATUS_UNKNOWN",
    allowedActions: actions({ recover: true }),
  });
  runtime.recoverImpl = async () => pending.promise;
  const presenter = createPresenter(runtime);

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.getState().phase, "unknown");
  assert.deepEqual(presenter.getState().allowedActions, {
    start: false,
    changeProvider: false,
    recover: true,
    cancel: false,
    addCash: false,
    removeTender: false,
  });

  const first = presenter.recover();
  const duplicate = presenter.recover();
  assert.notStrictEqual(first, duplicate);
  assert.equal(await duplicate, false);
  await tick();
  assert.equal(runtime.recoverCalls, 1);
  assert.equal(presenter.getState().phase, "submitting");
  assert.equal(presenter.getState().busy, true);
  assert.equal(presenter.getState().recoveryInFlight, false);

  pending.resolve(
    snapshot({
      status: "completed",
      remaining: aud(0),
      provider: "square",
      attemptId: "attempt-local-1",
      tenders: [
        {
          tenderGuid: "tender-local-1",
          method: "card",
          amount: aud(1_000),
          reversible: true,
        },
      ],
    }),
  );
  assert.equal(await first, true);
  assert.equal(presenter.getState().phase, "success");
  assert.equal(presenter.getState().orderGuid, "order-local-1");
});

test("后台恢复保持 Pending 页面稳定并公开独立恢复状态", async () => {
  const runtime = new FakePaymentRuntime();
  const pending = deferred<PaymentCheckoutPublicSnapshot>();
  runtime.recovery = snapshot({
    status: "pending",
    provider: "square",
    attemptId: "attempt-square-background",
    allowedActions: actions({ recover: true, cancel: true }),
  });
  runtime.recoverImpl = async () => pending.promise;
  const presenter = createPresenter(runtime);

  await presenter.initialize();
  const recovery = presenter.recover({ background: true });
  await tick();

  assert.equal(presenter.getState().phase, "pending");
  assert.equal(presenter.getState().busy, false);
  assert.equal(presenter.getState().recoveryInFlight, true);
  pending.resolve(runtime.recovery);
  assert.equal(await recovery, false);
  assert.equal(presenter.getState().recoveryInFlight, false);
});

test("Linkly Cloud 单终端在无选择时自动持久选择，多终端快速切换只发布最后目标", async () => {
  const runtime = new FakePaymentRuntime();
  const singlePort = new FakeLinklyTerminalSelectionPort(
    terminalSelection({ selectedTerminalId: null, selectionRevision: 0 }),
  );
  const single = createPresenter(runtime, undefined, aud(1_000), {
    environment: "Sandbox",
    port: singlePort,
  });

  await single.initialize();

  assert.deepEqual(singlePort.selectCalls, [
    { environment: "Sandbox", terminalId: "terminal-1", expectedRevision: 0 },
  ]);
  assert.equal(
    single.getState().linklyTerminals?.snapshot?.selectedTerminalId,
    "terminal-1",
  );
  assert.equal(canSelectPaymentMethod(single.getState(), "linkly-cloud"), true);

  const firstSwitch = deferred<LinklyTerminalSelectionSnapshot>();
  const multiPort = new FakeLinklyTerminalSelectionPort(
    terminalSelection({
      selectedTerminalId: "terminal-current",
      terminals: [
        terminal("terminal-current", "Current"),
        terminal("terminal-a", "A"),
        terminal("terminal-b", "B"),
      ],
    }),
  );
  multiPort.selectImpl = async (environment, terminalId, expectedRevision) => {
    if (terminalId === "terminal-a") return firstSwitch.promise;
    return terminalSelection({
      environment,
      selectedTerminalId: terminalId,
      selectionRevision: expectedRevision + 1,
      terminals: multiPort.snapshot.terminals,
    });
  };
  const multi = createPresenter(
    new FakePaymentRuntime(),
    undefined,
    aud(1_000),
    { environment: "Sandbox", port: multiPort },
  );
  await multi.initialize();
  const published: Array<string | null> = [];
  multi.subscribe(() => {
    published.push(
      multi.getState().linklyTerminals?.snapshot?.selectedTerminalId ?? null,
    );
  });

  const selectA = multi.selectLinklyTerminal("terminal-a");
  await tick();
  const selectB = multi.selectLinklyTerminal("terminal-b");
  firstSwitch.resolve(terminalSelection({
    selectedTerminalId: "terminal-a",
    selectionRevision: 2,
    terminals: multiPort.snapshot.terminals,
  }));

  assert.equal(await selectA, true);
  assert.equal(await selectB, true);
  assert.deepEqual(multiPort.selectCalls, [
    { environment: "Sandbox", terminalId: "terminal-a", expectedRevision: 1 },
    { environment: "Sandbox", terminalId: "terminal-b", expectedRevision: 2 },
  ]);
  assert.equal(
    multi.getState().linklyTerminals?.snapshot?.selectedTerminalId,
    "terminal-b",
  );
  assert.equal(published.includes("terminal-a"), false);
});

test("Linkly 未知结果恢复期间禁止切换终端", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.recovery = snapshot({
    status: "unknown",
    provider: "linkly-cloud",
    attemptId: "attempt-linkly-unknown",
    allowedActions: actions({ recover: true }),
  });
  const terminals = new FakeLinklyTerminalSelectionPort(
    terminalSelection({
      terminals: [terminal("terminal-1", "Front"), terminal("terminal-2", "Back")],
    }),
  );
  const presenter = createPresenter(runtime, undefined, aud(1_000), {
    environment: "Sandbox",
    port: terminals,
  });

  await presenter.initialize();

  assert.equal(await presenter.selectLinklyTerminal("terminal-2"), false);
  assert.equal(terminals.selectCalls.length, 0);
});

test("Linkly 忙碌终端可预选，但支付仍保持关闭", async () => {
  const terminals = new FakeLinklyTerminalSelectionPort(
    terminalSelection({
      terminals: [
        terminal("terminal-1", "Front"),
        { ...terminal("terminal-2", "Back"), isBusy: true },
      ],
    }),
  );
  const presenter = createPresenter(
    new FakePaymentRuntime(),
    undefined,
    aud(1_000),
    { environment: "Sandbox", port: terminals },
  );
  await presenter.initialize();

  assert.equal(await presenter.selectLinklyTerminal("terminal-2"), true);
  assert.deepEqual(terminals.selectCalls, [
    { environment: "Sandbox", terminalId: "terminal-2", expectedRevision: 1 },
  ]);
  assert.equal(
    presenter.getState().linklyTerminals?.snapshot?.selectedTerminalId,
    "terminal-2",
  );
  assert.equal(canSelectPaymentMethod(presenter.getState(), "linkly-cloud"), false);
});

test("Linkly selection conflict 只刷新权威目录并要求确认，不自动重发付款", async () => {
  const runtime = new FakePaymentRuntime();
  const terminals = new FakeLinklyTerminalSelectionPort(
    terminalSelection({
      selectedTerminalId: "terminal-a",
      selectionRevision: 3,
      terminals: [terminal("terminal-a", "A"), terminal("terminal-b", "B")],
    }),
  );
  runtime.startImpl = async () => {
    terminals.snapshot = terminalSelection({
      selectedTerminalId: "terminal-b",
      selectionRevision: 4,
      terminals: [terminal("terminal-a", "A"), terminal("terminal-b", "B")],
    });
    return snapshot({
      status: "declined",
      provider: "linkly-cloud",
      attemptId: "attempt-selection-conflict",
      errorCode: "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
      allowedActions: actions({ start: true, changeProvider: true, addCash: true }),
    });
  };
  const presenter = createPresenter(runtime, undefined, aud(1_000), {
    environment: "Sandbox",
    port: terminals,
  });
  await presenter.initialize();
  assert.equal(presenter.selectMethod("linkly-cloud"), true);

  const submitted = await presenter.submitSelected();

  assert.equal(submitted, false);
  assert.equal(runtime.startCalls.length, 1);
  assert.deepEqual(runtime.startCalls[0], {
    checkoutIntentId: "checkout-local-1",
    expectedCartRevision: 7,
    actionId: "action-1",
    provider: "linkly-cloud",
    amount: aud(1_000),
    linklyTerminalSelection: {
      environment: "Sandbox",
      mode: "Active",
      terminalId: "terminal-a",
      selectionRevision: 3,
    },
  });
  assert.equal(terminals.readCalls, 2);
  assert.equal(
    presenter.getState().linklyTerminals?.snapshot?.selectedTerminalId,
    "terminal-b",
  );
  assert.equal(
    presenter.getState().runtimeErrorCode,
    "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  );
  assert.equal(
    paymentText("zh", "error.LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT"),
    "刷卡机选择已变更。请确认当前刷卡机后再重新发起付款。",
  );
});

test("ACK pending 文案明确结果已保存且不会再次扣款或退款", () => {
  assert.match(
    paymentText("zh", "error.LINKLY_ACKNOWLEDGEMENT_PENDING"),
    /交易结果已保存.*不会再次扣款或退款/u,
  );
  assert.match(
    paymentText("en", "error.LINKLY_ACKNOWLEDGEMENT_PENDING"),
    /result is saved.*no charge or refund will be repeated/u,
  );
});

test("后台恢复在途时手动恢复立即失败，不加入后台 signal 的 promise", async () => {
  const runtime = new FakePaymentRuntime();
  const pending = deferred<PaymentCheckoutPublicSnapshot>();
  const controller = new AbortController();
  const deadlineAtMs = Date.parse("2026-08-09T00:00:00.000Z") + 90_000;
  runtime.recovery = snapshot({
    status: "pending",
    provider: "square",
    attemptId: "attempt-square-background-first",
    allowedActions: actions({ recover: true, cancel: true }),
  });
  runtime.recoverImpl = async () => pending.promise;
  const presenter = createPresenter(runtime);
  await presenter.initialize();

  const background = presenter.recover({
    background: true,
    signal: controller.signal,
    deadlineAtMs,
  });
  await tick();
  const manual = presenter.recover();

  assert.notStrictEqual(manual, background);
  assert.equal(await manual, false);
  assert.equal(runtime.recoverCalls, 1);
  assert.strictEqual(runtime.recoverInputs[0]?.signal, controller.signal);

  pending.resolve(runtime.recovery);
  assert.equal(await background, false);
});

test("不同后台 signal 的恢复不互相加入同一个 promise", async () => {
  const runtime = new FakePaymentRuntime();
  const pending = deferred<PaymentCheckoutPublicSnapshot>();
  const firstController = new AbortController();
  const secondController = new AbortController();
  runtime.recovery = snapshot({
    status: "pending",
    provider: "square",
    attemptId: "attempt-square-different-signals",
    allowedActions: actions({ recover: true, cancel: true }),
  });
  runtime.recoverImpl = async () => pending.promise;
  const presenter = createPresenter(runtime);
  await presenter.initialize();

  const first = presenter.recover({
    background: true,
    signal: firstController.signal,
    deadlineAtMs: 1,
  });
  await tick();
  const second = presenter.recover({
    background: true,
    signal: secondController.signal,
    deadlineAtMs: 2,
  });

  assert.notStrictEqual(first, second);
  assert.equal(await second, false);
  assert.equal(runtime.recoverCalls, 1);
  assert.strictEqual(runtime.recoverInputs[0]?.signal, firstController.signal);

  pending.resolve(runtime.recovery);
  assert.equal(await first, false);
});

test("后台恢复传递本次 signal/deadline，且已有手动 action 时立即 false 不冒领旧 promise", async () => {
  const runtime = new FakePaymentRuntime();
  const pending = deferred<PaymentCheckoutPublicSnapshot>();
  const createdAtIso = "2026-08-09T00:00:00.000Z";
  runtime.recovery = snapshot({
    status: "pending",
    provider: "square",
    attemptId: "attempt-square-owned-recovery",
    attemptCreatedAtIso: createdAtIso,
    allowedActions: actions({ recover: true, cancel: true }),
  });
  runtime.recoverImpl = async () => pending.promise;
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  assert.equal(presenter.getState().attemptCreatedAtIso, createdAtIso);

  const manual = presenter.recover();
  await tick();
  const controller = new AbortController();
  const background = presenter.recover({
    background: true,
    signal: controller.signal,
    deadlineAtMs: Date.parse(createdAtIso) + 90_000,
  });

  assert.notStrictEqual(background, manual);
  assert.equal(await background, false);
  assert.equal(runtime.recoverCalls, 1);
  assert.deepEqual(runtime.recoverInputs, [
    {
      orderGuid: "order-local-1",
      attemptId: "attempt-square-owned-recovery",
    },
  ]);

  pending.resolve(runtime.recovery);
  assert.equal(await manual, false);
});

test("空闲后台恢复把结构化 deadline 控制原样交给 runtime", async () => {
  const runtime = new FakePaymentRuntime();
  const createdAtIso = "2026-08-09T00:10:00.000Z";
  runtime.recovery = snapshot({
    status: "pending",
    provider: "square",
    attemptId: "attempt-square-forward-control",
    attemptCreatedAtIso: createdAtIso,
    allowedActions: actions({ recover: true, cancel: true }),
  });
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  const controller = new AbortController();
  const deadlineAtMs = Date.parse(createdAtIso) + 90_000;

  await presenter.recover({
    background: true,
    signal: controller.signal,
    deadlineAtMs,
  });

  assert.equal(runtime.recoverCalls, 1);
  assert.strictEqual(runtime.recoverInputs[0]?.signal, controller.signal);
  assert.equal(runtime.recoverInputs[0]?.deadlineAtMs, deadlineAtMs);
});

test("Linkly 受控恢复 abort 后不补发不可取消的 operator.read；ACK 成功走 runtime ACK-only recovery", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.recovery = snapshot({
    status: "pending",
    provider: "linkly-cloud",
    attemptId: "attempt-linkly-control",
    allowedActions: actions({ recover: true }),
  });
  runtime.recoverImpl = async () => runtime.recovery!;
  const linkly = new FakeLinklyOperator();
  const presenter = createPresenter(runtime, linkly);
  await presenter.initialize();
  const readsAfterInitialize = linkly.readInputs.length;
  const controller = new AbortController();
  controller.abort();
  await presenter.recover({
    background: true,
    signal: controller.signal,
    deadlineAtMs: Date.now() + 180_000,
  });
  assert.equal(linkly.readInputs.length, readsAfterInitialize);

  runtime.recovery = snapshot({
    status: "completed",
    provider: "linkly-cloud",
    attemptId: "attempt-linkly-control",
    errorCode: null,
    allowedActions: actions(),
  });
  assert.equal(await presenter.acknowledgeLinkly(), true);
  assert.equal(runtime.recoverCalls, 2);
  assert.equal(linkly.readInputs.length, readsAfterInitialize);
});

test("冷启动礼券撤销 Unknown 使用脱敏持久恢复入口，完成后清除旧恢复标记", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.recovery = snapshot({
    status: "unknown",
    errorCode: "TENDER_REVERSAL_UNKNOWN",
    allowedActions: actions({ recover: true }),
    tenderReversalRecovery: {
      tenderGuid: "tender-voucher-restart-1",
      status: "unknown",
    },
  });
  runtime.retryTenderReversalImpl = async () =>
    snapshot({
      status: "partial",
      total: aud(1_000),
      remaining: aud(600),
      tenders: [
        {
          tenderGuid: "cash-after-reversal",
          method: "cash",
          amount: aud(400),
          reversible: true,
        },
      ],
      allowedActions: actions({
        start: true,
        changeProvider: true,
        addCash: true,
        removeTender: true,
      }),
    });
  const presenter = createPresenter(runtime);

  assert.equal(await presenter.initialize(), true);
  assert.deepEqual(presenter.getState().tenderReversalRecovery, {
    tenderGuid: "tender-voucher-restart-1",
    status: "unknown",
  });
  assert.equal(JSON.stringify(presenter.getState()).includes("actionId"), false);
  assert.equal(JSON.stringify(presenter.getState()).includes("token"), false);

  assert.equal(await presenter.recover(), true);
  assert.deepEqual(runtime.retryTenderReversalCalls, [
    {
      orderGuid: "order-local-1",
      tenderGuid: "tender-voucher-restart-1",
    },
  ]);
  assert.equal(runtime.recoverCalls, 0);
  assert.equal(presenter.getState().tenderReversalRecovery, null);
});

test("Blocked 礼券撤销即使快照错误开放恢复也零调用并保持脱敏", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.recovery = snapshot({
    status: "recovery-required",
    errorCode: "TENDER_REVERSAL_BLOCKED",
    allowedActions: actions({ recover: true }),
    tenderReversalRecovery: {
      tenderGuid: "tender-voucher-blocked-1",
      status: "blocked",
    },
  });
  const presenter = createPresenter(runtime);

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.getState().allowedActions.recover, false);
  assert.equal(await presenter.recover(), false);
  assert.equal(runtime.retryTenderReversalCalls.length, 0);
  assert.equal(runtime.recoverCalls, 0);
  assert.equal(JSON.stringify(presenter.getState()).includes("actionId"), false);
  assert.equal(JSON.stringify(presenter.getState()).includes("token"), false);
});

test("Blocked 礼券撤销提供明确的中英文稳定文案", () => {
  assert.equal(
    paymentText("en", "error.TENDER_REVERSAL_RECOVERY_REQUIRED"),
    "A saved gift card reversal must be recovered before taking another tender.",
  );
  assert.equal(
    paymentText("zh", "error.TENDER_REVERSAL_RECOVERY_REQUIRED"),
    "必须先恢复已保存的礼品卡撤销，才能继续收款。",
  );
  assert.equal(
    paymentText("en", "error.TENDER_REVERSAL_BLOCKED"),
    "Gift card reversal cannot be retried. A supervisor can void this gift card tender; the gift card is never redeemed for this order.",
  );
  assert.equal(
    paymentText("zh", "error.TENDER_REVERSAL_BLOCKED"),
    "礼品卡撤销已阻断且不能重试。可由主管授权作废该礼品卡付款，本单不会核销该礼品卡。",
  );
  assert.equal(
    paymentText("en", "error.TENDER_REVERSAL_VOID_NOT_AUTHORIZED"),
    "Supervisor approval was not completed. The gift card tender is still blocked.",
  );
  assert.equal(
    paymentText("zh", "error.TENDER_REVERSAL_VOID_NOT_AUTHORIZED"),
    "主管授权未完成，礼品卡付款仍处于阻断状态。",
  );
  assert.equal(
    paymentText("en", "action.voidBlockedTenderReversal"),
    "Supervisor: void gift card tender",
  );
  assert.equal(
    paymentText("zh", "action.voidBlockedTenderReversal"),
    "主管授权作废礼品卡付款",
  );
  assert.equal(
    paymentText("en", "error.SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED"),
    "Square Sandbox test payments must not exceed AUD 25.00.",
  );
  assert.equal(
    paymentText("zh", "error.SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED"),
    "Square 沙盒测试付款不得超过 25.00 澳元。",
  );
});

test("礼券码不进入公开状态，重复提交只调用一次并在异步边界后清除", async () => {
  const runtime = new FakePaymentRuntime();
  const pending = deferred<PaymentCheckoutPublicSnapshot>();
  runtime.startImpl = async () => pending.promise;
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  assert.equal(presenter.selectMethod("voucher"), true);
  presenter.setAmountText("4.50");
  presenter.setVoucherCode(" VOUCHER-SECRET-123 ");

  assert.equal(presenter.getState().voucherCaptured, true);
  assert.equal(
    JSON.stringify(presenter.getState()).includes("VOUCHER-SECRET-123"),
    false,
  );

  const first = presenter.submitSelected();
  const duplicate = presenter.submitSelected();
  assert.strictEqual(first, duplicate);
  await tick();
  assert.equal(runtime.startCalls.length, 1);
  assert.deepEqual(runtime.startCalls[0], {
    checkoutIntentId: "checkout-local-1",
    expectedCartRevision: 7,
    actionId: "action-1",
    provider: "voucher",
    amount: aud(450),
    voucherCode: "VOUCHER-SECRET-123",
  });

  pending.resolve(
    snapshot({
      status: "partial",
      total: aud(1_000),
      remaining: aud(550),
      provider: "voucher",
      attemptId: "attempt-voucher-1",
      tenders: [
        {
          tenderGuid: "tender-voucher-1",
          method: "voucher",
          amount: aud(450),
          reversible: true,
        },
      ],
      allowedActions: actions({
        start: true,
        changeProvider: true,
        addCash: true,
        removeTender: true,
      }),
    }),
  );
  assert.equal(await first, true);
  assert.equal(presenter.getState().voucherCaptured, false);
  assert.equal(presenter.getState().sensitiveInputRevision, 1);
  assert.equal(
    JSON.stringify(presenter.getState()).includes("VOUCHER-SECRET-123"),
    false,
  );
});

test("纯 DraftPrepared 使用 abandonPrepared，已有 attempt 才调用 provider cancel", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.recovery = snapshot({
    status: "draft-prepared",
    allowedActions: actions({
      start: true,
      changeProvider: true,
      cancel: true,
      addCash: true,
    }),
  });
  runtime.abandonImpl = async () =>
    snapshot({
      status: "cancelled",
      allowedActions: actions(),
    });
  const presenter = createPresenter(runtime);
  await presenter.initialize();

  assert.equal(await presenter.cancel(), true);
  assert.equal(runtime.abandonCalls.length, 1);
  assert.equal(runtime.cancelCalls, 0);
  assert.equal(presenter.getState().phase, "cancelled");
});

test("确定离线且未创建 attempt 时保留安全改现金入口", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.startImpl = async () =>
    snapshot({
      status: "recovery-required",
      errorCode: "ONLINE_REQUIRED",
      allowedActions: actions({
        start: true,
        changeProvider: true,
        addCash: true,
      }),
    });
  runtime.addCashImpl = async () =>
    snapshot({
      status: "completed",
      remaining: aud(0),
      tenders: [
        {
          tenderGuid: "cash-offline-safe",
          method: "cash",
          amount: aud(1_000),
          reversible: false,
        },
      ],
    });
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  // 默认支付方式为现金；本用例聚焦 provider 离线回退，显式切到卡支付再提交。
  assert.equal(presenter.selectMethod("square"), true);
  assert.equal(await presenter.submitSelected(), false);
  assert.equal(presenter.getState().phase, "offline-cash");
  assert.equal(presenter.selectMethod("cash"), true);
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(runtime.addCashCalls, 1);
  assert.equal(presenter.getState().phase, "success");
});

test("现金展示只使用持久化 settlement，不从输入金额重新猜找零", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.startCashImpl = async () =>
    snapshot({
      status: "completed",
      remaining: aud(0),
      tenders: [
        {
          tenderGuid: "cash-first-entry",
          method: "cash",
          amount: aud(1_000),
          reversible: false,
        },
      ],
      cashSettlement: {
        tendered: aud(1_000),
        applied: aud(1_002),
        change: aud(0),
      },
    });
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  // 默认支付方式即现金：无需显式选择，直接提交应走 startCash 路径。
  assert.equal(presenter.getState().selectedMethod, "cash");
  presenter.setAmountText("20.00");

  assert.equal(await presenter.submitSelected(), true);
  assert.deepEqual(runtime.startCashCalls, [
    {
      checkoutIntentId: "checkout-local-1",
      expectedCartRevision: 7,
      actionId: "action-1",
      amount: aud(2_000),
    },
  ]);
  assert.deepEqual(presenter.getState().checkout.cash, {
    tenderedCents: 1_000,
    appliedCents: 1_002,
    changeCents: 0,
  });
});

test("现金默认金额按五分应收刷新，手动输入的部分现金保持精确", async () => {
  const roundDownRuntime = new FakePaymentRuntime();
  roundDownRuntime.startCashImpl = async () =>
    snapshot({
      status: "completed",
      total: aud(1_002),
      remaining: aud(0),
      tenders: [{
        tenderGuid: "cash-round-down-default",
        method: "cash",
        amount: aud(1_002),
        reversible: false,
      }],
      cashSettlement: {
        tendered: aud(1_000),
        applied: aud(1_002),
        change: aud(0),
      },
    });
  const roundDown = createPresenter(roundDownRuntime, undefined, aud(1_002));
  assert.equal(roundDown.getState().selectedMethod, "cash");
  assert.equal(roundDown.getState().amountText, "10.00");
  await roundDown.initialize();
  assert.equal(await roundDown.submitSelected(), true);
  assert.deepEqual(roundDownRuntime.startCashCalls, [{
    checkoutIntentId: "checkout-local-1",
    expectedCartRevision: 7,
    actionId: "action-1",
    amount: aud(1_000),
  }]);

  const runtime = new FakePaymentRuntime();
  runtime.recovery = snapshot({
    status: "partial",
    total: aud(1_003),
    remaining: aud(1_003),
    allowedActions: actions({
      start: true,
      changeProvider: true,
      addCash: true,
    }),
  });
  const presenter = createPresenter(runtime, undefined, aud(1_003));
  assert.equal(presenter.getState().amountText, "10.05");
  await presenter.initialize();
  assert.equal(presenter.getState().selectedMethod, "cash");
  assert.equal(presenter.getState().amountText, "10.05");

  assert.equal(presenter.selectMethod("square"), true);
  presenter.setAmountText("1.00");
  assert.equal(presenter.selectMethod("cash"), true);
  assert.equal(presenter.getState().amountText, "10.05");

  presenter.setAmountText("10.02");
  assert.equal(await presenter.submitSelected(), true);
  assert.deepEqual(runtime.addCashInputs, [
    {
      actionId: "action-1",
      orderGuid: "order-local-1",
      amount: aud(1_002),
    },
  ]);
});

test("现金剩余 1/2 分时默认保持正数并可直接提交", async () => {
  for (const remainingCents of [1, 2]) {
    const runtime = new FakePaymentRuntime();
    runtime.startCashImpl = async () =>
      snapshot({
        status: "completed",
        total: aud(remainingCents),
        remaining: aud(0),
        tenders: [{
          tenderGuid: `cash-${remainingCents}-cent-default`,
          method: "cash",
          amount: aud(remainingCents),
          reversible: false,
        }],
        cashSettlement: {
          tendered: aud(0),
          applied: aud(remainingCents),
          change: aud(0),
        },
      });
    const presenter = createPresenter(
      runtime,
      undefined,
      aud(remainingCents),
    );

    assert.equal(
      presenter.getState().amountText,
      remainingCents === 1 ? "0.01" : "0.02",
    );
    await presenter.initialize();
    assert.equal(await presenter.submitSelected(), true);
    assert.deepEqual(runtime.startCashCalls, [{
      checkoutIntentId: "checkout-local-1",
      expectedCartRevision: 7,
      actionId: "action-1",
      amount: aud(remainingCents),
    }]);
  }
});

test("现金全额输入低于五分应收时在 presenter 拒绝且不调用 runtime", async () => {
  const runtime = new FakePaymentRuntime();
  const presenter = createPresenter(runtime, undefined, aud(1_003));
  presenter.setAmountText("10.03");

  assert.equal(await presenter.submitSelected(), false);
  assert.equal(presenter.getState().fieldIssue, "cash-final-below-due");
  assert.equal(runtime.startCashCalls.length, 0);
  assert.equal(runtime.addCashCalls, 0);
  assert.equal(
    paymentText("en", "field.cash-final-below-due"),
    "Cash tendered must meet the rounded cash due.",
  );
  assert.equal(
    paymentText("zh", "field.cash-final-below-due"),
    "现金实收必须达到按五分取整后的应收金额。",
  );
});

test("运行时未公开 TakeCash 能力时现金不默认选中、不可选且不标记可用", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.canTakeCash = () => false;
  const presenter = createPresenter(runtime);

  assert.equal(presenter.getState().selectedMethod, "square");
  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.getState().allowedActions.addCash, false);
  assert.equal(presenter.selectMethod("cash"), false);
});

test("cashAvailable 缺失时现金 fail closed，不能选择或提交", async () => {
  const runtime = new FakePaymentRuntime();
  Object.defineProperty(runtime, "canTakeCash", {
    value: undefined,
    configurable: true,
  });
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  const { cashAvailable: _cashAvailable, ...missingCapability } =
    presenter.getState();
  const staleAllowedActions = {
    ...missingCapability,
    allowedActions: {
      ...missingCapability.allowedActions,
      addCash: true,
    },
  };

  assert.equal(canSelectPaymentMethod(staleAllowedActions, "cash"), false);
  assert.equal(presenter.selectMethod("cash"), false);
  assert.equal(runtime.startCashCalls.length, 0);
  assert.equal(runtime.addCashCalls, 0);
});

test("未配置 startCash 时默认回退到第一个可用 provider", async () => {
  const runtime = new FakePaymentRuntime();
  // 遮蔽原型上的 startCash 方法，模拟未配置现金结账的运行时。
  Object.defineProperty(runtime, "startCash", {
    value: undefined,
    writable: true,
  });
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  // resolveInitialMethod 现金不可用时回退 firstAvailableProvider（square）。
  assert.equal(presenter.getState().selectedMethod, "square");
  // 初始 addCash 权限与默认值共用同一判定，同步锁住无现金配置。
  assert.equal(presenter.getState().allowedActions.addCash, false);
});

test("移除最后一笔现金后清空实收展示，并开放继续支付或取消", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.startCashImpl = async () =>
    snapshot({
      status: "partial",
      remaining: aud(600),
      tenders: [
        {
          tenderGuid: "cash-removable",
          method: "cash",
          amount: aud(400),
          reversible: true,
        },
      ],
      cashSettlement: {
        tendered: aud(400),
        applied: aud(400),
        change: aud(0),
      },
      allowedActions: actions({
        start: true,
        changeProvider: true,
        addCash: true,
        removeTender: true,
      }),
    });
  runtime.removeImpl = async () =>
    snapshot({
      status: "draft-prepared",
      remaining: aud(1_000),
      tenders: [],
      allowedActions: actions({
        start: true,
        changeProvider: true,
        cancel: true,
        addCash: true,
      }),
    });
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("4.00");
  assert.equal(await presenter.submitSelected(), true);
  assert.deepEqual(presenter.getState().checkout.cash, {
    tenderedCents: 400,
    appliedCents: 400,
    changeCents: 0,
  });

  assert.equal(await presenter.removeTender("cash-removable"), true);

  assert.deepEqual(presenter.getState().checkout.cash, {
    tenderedCents: 0,
    appliedCents: 0,
    changeCents: 0,
  });
  assert.equal(presenter.getState().allowedActions.cancel, true);
  assert.equal(presenter.getState().allowedActions.addCash, true);
});

test("Linkly UI 仅发送 attemptId 与枚举安全键，完成后走同一 attempt 恢复", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.recovery = snapshot({
    status: "awaiting-terminal",
    provider: "linkly-cloud",
    attemptId: "attempt-linkly-1",
    errorCode: "PAYMENT_TERMINAL_AWAITED",
    allowedActions: actions({ recover: true, cancel: true }),
  });
  runtime.recoverImpl = async () =>
    snapshot({
      status: "partial",
      total: aud(1_000),
      remaining: aud(400),
      provider: "linkly-cloud",
      attemptId: "attempt-linkly-1",
      tenders: [
        {
          tenderGuid: "tender-card-1",
          method: "card",
          amount: aud(600),
          reversible: true,
        },
      ],
      allowedActions: actions({
        start: true,
        changeProvider: true,
        addCash: true,
        removeTender: true,
      }),
    });
  const linkly = new FakeLinklyOperator();
  const presenter = createPresenter(runtime, linkly);
  await presenter.initialize();

  assert.deepEqual(
    presenter.getState().linkly.allowedKeys,
    ["yes"],
  );
  assert.equal(await presenter.sendLinklyKey("yes"), true);
  assert.deepEqual(linkly.sendCalls, [
    { attemptId: "attempt-linkly-1", key: "yes" },
  ]);
  assert.equal(runtime.recoverCalls, 1);
  assert.equal(presenter.getState().phase, "partial");
  assert.equal(
    JSON.stringify(presenter.getState()).includes("session"),
    false,
  );
});

test("销毁后异步结果不回流；未知异常只映射稳定失败码且不泄露 message", async () => {
  const recovery = deferred<PaymentCheckoutPublicSnapshot | null>();
  const runtime = new FakePaymentRuntime();
  runtime.findRecoveryImpl = async () => recovery.promise;
  const presenter = createPresenter(runtime);
  let emissions = 0;
  presenter.subscribe(() => {
    emissions += 1;
  });
  const loading = presenter.initialize();
  await tick();
  const emissionsBeforeDestroy = emissions;
  presenter.destroy();
  recovery.resolve(snapshot({ status: "unknown" }));
  assert.equal(await loading, false);
  assert.equal(emissions, emissionsBeforeDestroy);

  const failingRuntime = new FakePaymentRuntime();
  failingRuntime.startImpl = async () => {
    throw new Error("provider-token-secret");
  };
  const failing = createPresenter(failingRuntime);
  await failing.initialize();
  // 默认支付方式为现金；本用例聚焦 provider 启动失败映射，显式切到卡支付。
  assert.equal(failing.selectMethod("square"), true);
  assert.equal(await failing.submitSelected(), false);
  assert.equal(
    failing.getState().runtimeErrorCode,
    "PAYMENT_CHECKOUT_FAILED",
  );
  assert.equal(
    JSON.stringify(failing.getState()).includes("provider-token-secret"),
    false,
  );
});

test("手动刷卡要求人工确认、固定全部余额且重复提交只调用一次 runtime", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.listProviderAvailability = () => [availability("manual-card")];
  const pending = deferred<PaymentCheckoutPublicSnapshot>();
  runtime.startImpl = async () => pending.promise;
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  assert.equal(presenter.selectMethod("manual-card"), true);
  assert.equal(await presenter.submitSelected(), false);
  assert.equal(presenter.getState().fieldIssue, "manual-card-confirmation-required");
  assert.equal(runtime.startCalls.length, 0);
  presenter.setManualCardConfirmed(true);
  presenter.setAmountText("1.00");
  assert.equal(presenter.getState().amountText, "10.00");
  assert.equal(presenter.getState().manualCardConfirmed, false);
  presenter.setManualCardConfirmed(true);
  const first = presenter.submitSelected();
  const duplicate = presenter.submitSelected();
  await tick();
  assert.equal(runtime.startCalls.length, 1);
  assert.deepEqual(runtime.startCalls[0], {
    checkoutIntentId: "checkout-local-1",
    expectedCartRevision: 7,
    actionId: "action-1",
    provider: "manual-card",
    amount: aud(1_000),
    manualConfirmed: true,
  });
  pending.resolve(snapshot({ status: "completed", remaining: aud(0), provider: "manual-card" }));
  assert.equal(await first, true);
  await duplicate;
  assert.equal(presenter.getState().manualCardConfirmed, false);
});

test("手动刷卡切换、刷新余额和销毁均清确认，恢复草稿显式传递确认", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.listProviderAvailability = () => [availability("manual-card")];
  runtime.recovery = snapshot({ remaining: aud(650) });
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  presenter.selectMethod("manual-card");
  presenter.setManualCardConfirmed(true);
  presenter.selectMethod("cash");
  presenter.selectMethod("manual-card");
  assert.equal(presenter.getState().manualCardConfirmed, false);
  presenter.setManualCardConfirmed(true);
  runtime.read = async () => snapshot({ remaining: aud(500) });
  await presenter.load("order-local-1");
  assert.equal(presenter.getState().manualCardConfirmed, false);
  presenter.selectMethod("manual-card");
  assert.equal(presenter.getState().amountText, "5.00");
  presenter.setManualCardConfirmed(true);
  await presenter.submitSelected();
  assert.deepEqual(runtime.resumeCalls[0], {
    actionId: "action-1", provider: "manual-card", amount: aud(500), manualConfirmed: true,
  });
  assert.equal(runtime.startCalls.length, 0);
  presenter.destroy();
  assert.equal(presenter.getState().manualCardConfirmed, false);
});

test("手动刷卡不能绕过普通销售限制或 unknown 恢复锁", async () => {
  const runtime = new FakePaymentRuntime();
  runtime.listProviderAvailability = () => [availability("manual-card")];
  const presenter = createPresenter(runtime);
  await presenter.initialize();
  const state = presenter.getState();
  assert.equal(canSelectPaymentMethod({ ...state, checkout: { ...state.checkout, flow: "installment-create" } }, "manual-card"), false);
  runtime.read = async () => snapshot({ status: "unknown", allowedActions: actions({ recover: true }) });
  await presenter.load("order-local-1");
  assert.equal(presenter.selectMethod("manual-card"), false);
  presenter.setManualCardConfirmed(true);
  assert.equal(presenter.getState().manualCardConfirmed, false);
  assert.equal(await presenter.submitSelected(), false);
  assert.equal(runtime.startCalls.length + runtime.resumeCalls.length, 0);
});

test("手动刷卡与礼品卡具有稳定中英文提示", () => {
  assert.equal(paymentText("en", "method.voucher"), "Gift card");
  assert.equal(paymentText("zh", "method.voucher"), "礼品卡");
  for (const code of ["MANUAL_CARD_CONFIRMATION_REQUIRED", "MANUAL_CARD_FULL_BALANCE_REQUIRED", "MANUAL_CARD_SALE_ONLY", "MANUAL_CARD_CONFIGURATION_DISABLED", "MANUAL_CARD_CONFIGURATION_LOAD_FAILED"] as const) {
    for (const locale of ["en", "zh"] as const) {
      assert.notEqual(paymentText(locale, `error.${code}`), `error.${code}`);
    }
  }
});

test("AUD 输入仅接受正数与最多两位小数", () => {
  assert.deepEqual(parseAudInput("4"), aud(400));
  assert.deepEqual(parseAudInput("4.5"), aud(450));
  assert.deepEqual(parseAudInput("4.50"), aud(450));
  assert.equal(parseAudInput("0"), null);
  assert.equal(parseAudInput("-1"), null);
  assert.equal(parseAudInput("1.234"), null);
  assert.equal(parseAudInput("1,000"), null);
});

class FakePaymentRuntime implements PaymentCheckoutRuntimePort {
  public recovery: PaymentCheckoutPublicSnapshot | null = null;
  public readonly startCalls: unknown[] = [];
  public readonly resumeCalls: unknown[] = [];
  public readonly startCashCalls: unknown[] = [];
  public recoverCalls = 0;
  public readonly recoverInputs: Parameters<PaymentCheckoutRuntimePort["recover"]>[0][] = [];
  public readonly retryTenderReversalCalls: unknown[] = [];
  public addCashCalls = 0;
  public readonly addCashInputs: Parameters<PaymentCheckoutRuntimePort["addCash"]>[0][] = [];
  public cancelCalls = 0;
  public readonly abandonCalls: unknown[] = [];
  public findRecoveryImpl: () => Promise<PaymentCheckoutPublicSnapshot | null> =
    async () => this.recovery;
  public startImpl: (
    input: Parameters<PaymentCheckoutRuntimePort["start"]>[0],
  ) => Promise<PaymentCheckoutPublicSnapshot> = async () =>
    snapshot({ status: "pending" });
  public startCashImpl: (
    input: Parameters<
      NonNullable<PaymentCheckoutRuntimePort["startCash"]>
    >[0],
  ) => Promise<PaymentCheckoutPublicSnapshot> = async () =>
    snapshot({ status: "pending" });
  public recoverImpl: () => Promise<PaymentCheckoutPublicSnapshot> =
    async () => snapshot({ status: "pending" });
  public retryTenderReversalImpl: () => Promise<PaymentCheckoutPublicSnapshot> =
    async () => snapshot({ status: "partial" });
  public abandonImpl: () => Promise<PaymentCheckoutPublicSnapshot> =
    async () => snapshot({ status: "cancelled" });
  public addCashImpl: () => Promise<PaymentCheckoutPublicSnapshot> =
    async () => snapshot({ status: "partial" });
  public removeImpl: () => Promise<PaymentCheckoutPublicSnapshot> =
    async () => snapshot({ status: "partial" });

  public listProviderAvailability(): readonly PaymentProviderAvailability[] {
    return [
      availability("square"),
      availability("linkly-cloud"),
      availability("voucher"),
    ];
  }

  public canTakeCash(): boolean {
    return true;
  }

  public async read(): Promise<PaymentCheckoutPublicSnapshot> {
    return snapshot();
  }

  public findRecoveryRequired(): Promise<PaymentCheckoutPublicSnapshot | null> {
    return this.findRecoveryImpl();
  }

  public async resumeCurrent(input: Parameters<PaymentCheckoutRuntimePort["resumeCurrent"]>[0]): Promise<PaymentCheckoutPublicSnapshot | null> {
    this.resumeCalls.push(input);
    return snapshot({ status: "pending" });
  }

  public start(
    input: Parameters<PaymentCheckoutRuntimePort["start"]>[0],
  ): Promise<PaymentCheckoutPublicSnapshot> {
    this.startCalls.push(input);
    return this.startImpl(input);
  }

  public startCash(
    input: Parameters<
      NonNullable<PaymentCheckoutRuntimePort["startCash"]>
    >[0],
  ): Promise<PaymentCheckoutPublicSnapshot> {
    this.startCashCalls.push(input);
    return this.startCashImpl(input);
  }

  public recover(
    input: Parameters<PaymentCheckoutRuntimePort["recover"]>[0],
  ): Promise<PaymentCheckoutPublicSnapshot> {
    this.recoverCalls += 1;
    this.recoverInputs.push(input);
    return this.recoverImpl();
  }

  public retryTenderReversal(input: {
    orderGuid: string;
    tenderGuid: string;
  }): Promise<PaymentCheckoutPublicSnapshot> {
    this.retryTenderReversalCalls.push(input);
    return this.retryTenderReversalImpl();
  }

  public async cancel(): Promise<PaymentCheckoutPublicSnapshot> {
    this.cancelCalls += 1;
    return snapshot({ status: "cancelled" });
  }

  public abandonPrepared(
    input: Parameters<PaymentCheckoutRuntimePort["abandonPrepared"]>[0],
  ): Promise<PaymentCheckoutPublicSnapshot> {
    this.abandonCalls.push(input);
    return this.abandonImpl();
  }

  public addCash(
    input: Parameters<PaymentCheckoutRuntimePort["addCash"]>[0],
  ): Promise<PaymentCheckoutPublicSnapshot> {
    this.addCashCalls += 1;
    this.addCashInputs.push(input);
    return this.addCashImpl();
  }

  public async removeTender(): Promise<PaymentCheckoutPublicSnapshot> {
    return this.removeImpl();
  }
}

class FakeLinklyOperator implements LinklyOperatorRuntimePort {
  public readonly readInputs: {
    attemptId: string;
    signal?: AbortSignal;
    deadlineAtMs?: number;
  }[] = [];
  public readonly sendCalls: {
    attemptId: string;
    key: LinklySafeOperatorKey;
  }[] = [];

  public async sendKey(input: {
    attemptId: string;
    key: LinklySafeOperatorKey;
  }): Promise<LinklyOperatorPublicResult> {
    this.sendCalls.push(input);
    return {
      attemptId: input.attemptId,
      status: "completed",
      errorCode: null,
      allowedKeys: [],
    };
  }

  public async read(input: { attemptId: string; signal?: AbortSignal; deadlineAtMs?: number }): Promise<LinklyOperatorPublicResult> {
    this.readInputs.push(input);
    return {
      attemptId: input.attemptId,
      status: "in-progress",
      errorCode: null,
      allowedKeys: ["yes"],
      interaction: {
        displayText: "PRESENT CARD",
        displayLines: [],
        inputType: null,
        graphicCode: null,
        recoveryAction: null,
      },
    };
  }

  public async markReceiptPrinted(
    attemptId: string,
  ): Promise<LinklyOperatorPublicResult> {
    return {
      attemptId,
      status: "completed",
      errorCode: null,
      allowedKeys: [],
    };
  }

  public async acknowledge(
    attemptId: string,
  ): Promise<LinklyOperatorPublicResult> {
    return {
      attemptId,
      status: "completed",
      errorCode: null,
      allowedKeys: [],
    };
  }
}

function createPresenter(
  runtime: FakePaymentRuntime,
  linklyOperator?: LinklyOperatorRuntimePort,
  entryTotal: Money = aud(1_000),
  linklyTerminals?: Readonly<{
    environment: string;
    port: LinklyTerminalSelectionPort;
  }>,
): PaymentPresenter {
  let action = 0;
  return new PaymentPresenter({
    runtime,
    ...(linklyOperator ? { linklyOperator } : {}),
    ...(linklyTerminals ? { linklyTerminals } : {}),
    entry: {
      checkoutIntentId: "checkout-local-1",
      expectedCartRevision: 7,
      total: entryTotal,
    },
    createActionId: () => `action-${++action}`,
  });
}

function terminal(terminalId: string, displayName: string) {
  return {
    terminalId,
    laneNo: 1,
    displayName,
    pairingState: "Ready" as const,
    isBusy: false,
    isReady: true,
    lastHealthStatus: "Ready",
    lastHealthAt: null,
  };
}

function terminalSelection(
  override: Partial<LinklyTerminalSelectionSnapshot> = {},
): LinklyTerminalSelectionSnapshot {
  return {
    environment: "Sandbox",
    mode: "Active",
    selectedTerminalId: "terminal-1",
    selectionRevision: 1,
    terminals: [terminal("terminal-1", "Front")],
    ...override,
  };
}

class FakeLinklyTerminalSelectionPort implements LinklyTerminalSelectionPort {
  public readCalls = 0;
  public readonly selectCalls: Array<{
    environment: string;
    terminalId: string;
    expectedRevision: number;
  }> = [];
  public selectImpl: (
    environment: string,
    terminalId: string,
    expectedRevision: number,
  ) => Promise<LinklyTerminalSelectionSnapshot> = async (
    environment,
    terminalId,
    expectedRevision,
  ) => terminalSelection({
    environment,
    selectedTerminalId: terminalId,
    selectionRevision: expectedRevision + 1,
    terminals: this.snapshot.terminals,
  });

  public constructor(public snapshot: LinklyTerminalSelectionSnapshot) {}

  public readTerminals(): Promise<LinklyTerminalSelectionSnapshot> {
    this.readCalls += 1;
    return Promise.resolve(this.snapshot);
  }

  public async selectTerminal(
    environment: string,
    terminalId: string,
    expectedRevision: number,
  ): Promise<LinklyTerminalSelectionSnapshot> {
    this.selectCalls.push({ environment, terminalId, expectedRevision });
    this.snapshot = await this.selectImpl(
      environment,
      terminalId,
      expectedRevision,
    );
    return this.snapshot;
  }
}

function snapshot(
  override: Partial<PaymentCheckoutPublicSnapshot> & {
    tenderReversalRecovery?: Readonly<{
      tenderGuid: string;
      status: "pending" | "unknown" | "blocked";
    }>;
  } = {},
): PaymentCheckoutPublicSnapshot {
  return {
    orderGuid: "order-local-1",
    total: aud(1_000),
    remaining: aud(1_000),
    tenders: [],
    attemptId: null,
    attemptCreatedAtIso: null,
    provider: null,
    status: "draft-prepared",
    errorCode: null,
    allowedActions: actions({
      start: true,
      changeProvider: true,
      cancel: true,
      addCash: true,
    }),
    ...override,
  };
}

function actions(
  override: Partial<PaymentCheckoutPublicSnapshot["allowedActions"]> = {},
): PaymentCheckoutPublicSnapshot["allowedActions"] {
  return {
    start: false,
    changeProvider: false,
    recover: false,
    cancel: false,
    addCash: false,
    removeTender: false,
    ...override,
  };
}

function availability(
  provider: PaymentProvider,
): PaymentProviderAvailability {
  return {
    provider,
    available: true,
    blocker: null,
  };
}

function aud(cents: number): Money {
  return { currency: "AUD", cents };
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve(value: T): void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}

async function tick(): Promise<void> {
  await new Promise<void>((resolve) => setImmediate(resolve));
}
