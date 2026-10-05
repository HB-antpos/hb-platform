import assert from "node:assert/strict";
import test from "node:test";

import { CurrentCashierSession } from "./current-cashier-session";
import {
  createProductionPaymentRuntime,
  createProductionTenderReversalRouter,
  type TenderReversalVoidAuthorizer,
} from "./production-payment-runtime";

import type {
  Money,
  OrderTender,
  PaymentAttempt,
  PaymentAttemptRepositoryPort,
  PaymentProvider,
} from "@/core/contracts";
import type { PosDatabase } from "@/core/db/pos-database";
import type {
  PaymentDraftRecovery,
} from "@/core/db/sqlite-payment-draft-recovery-store";
import type {
  ManualPaymentRecoveryFindingInput,
  PaymentRecoveryCenterRecord,
} from "@/core/db/sqlite-payment-recovery-center-store";
import type {
  PosRepositoryBundle,
  SensitivePayloadEncryptor,
} from "@/core/db/sqlite-repositories";
import type {
  PaymentProviderRuntimeBootstrap,
  PaymentProviderRuntimeBootstrapWithVoucherRelease,
} from "@/core/runtime/payment-provider-runtime-bootstrap";
import type {
  MixedPaymentOrderTruth,
  MixedTenderReversalPort,
  VoucherTenderReversalRecord,
  VoucherTenderReversalReason,
} from "@/features/payments/mixed";
import type {
  PaymentCheckoutDraft,
} from "@/features/payments/runtime/payment-checkout-runtime";
import type {
  VoucherApprovedPurchaseReleasePort,
} from "@/features/payments/runtime/payment-provider-registry";
import { SquarePaymentAdapter } from "@/features/payments/square/square-payment-adapter";
import { PricingCart } from "@/features/sales/domain";
import { ActivePricingCartSession } from "@/features/sales/runtime";


const ALL_PAYMENT_PERMISSIONS = [
  "Permissions.PosTerminal.Payment.View",
  "Permissions.PosTerminal.Payment.TakeCash",
  "Permissions.PosTerminal.Payment.TakeCard",
  "Permissions.PosTerminal.Payment.TakeVoucher",
  "Permissions.PosTerminal.Payment.RemoveTender",
  "Permissions.PosTerminal.Payment.Confirm",
] as const;
const TEST_AUDIT_ACTOR = Object.freeze({
  cashierId: "cashier-1",
  cashierName: "Cashier",
  userGuid: null,
});

test("生产支付只公开 presenter/恢复布尔值，启动前和无可信收银员均 fail closed", async () => {
  let voucherBindCount = 0;
  const cashier = new CurrentCashierSession();
  const runtime = createProductionPaymentRuntime({
    database: database(),
    repositories: repositories(),
    encryptor,
    activeCart: new ActivePricingCartSession(
      new PricingCart(),
      () => new PricingCart(),
    ),
    currentCashier: cashier,
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: {
      now: () => new Date("2026-07-28T00:00:00.000Z"),
      nowIso: () => "2026-07-28T00:00:00.000Z",
    },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: bootstrap(() => {
      voucherBindCount += 1;
    }),
    async drainFulfilment() {},
  });

  const service = runtime.service;
  assert.equal(service.status, "available");
  assert.notEqual(runtime.returnRefund, null);
  if (service.status !== "available") return;
  assert.deepEqual(Object.keys(service).sort(), [
    "createPresenter",
    "hasRecoveryRequired",
    "recoveryCenter",
    "status",
  ]);
  assert.equal(
    JSON.stringify(service).includes("attempt"),
    false,
  );
  assert.equal(voucherBindCount, 1);

  assert.throws(
    () =>
      service.createPresenter({
        checkoutIntentId: "checkout-1",
        expectedCartRevision: 1,
        total: { currency: "AUD", cents: 500 },
      }),
    /PAYMENT_RUNTIME_NOT_INITIALIZED/,
  );

  await runtime.initializeRecovery();
  assert.throws(
    () => service.createPresenter(null),
    /CURRENT_CASHIER_REQUIRED/,
  );

  const authenticationEpoch = cashier.beginAuthentication();
  cashier.activate(
    authenticationEpoch,
    {
      source: "online",
      session: {
        cashierId: "cashier-1",
        cashierName: "Cashier",
        storeCode: "S1",
        deviceCode: "IPAD-1",
        permissionCodes: [...ALL_PAYMENT_PERMISSIONS],
      },
    },
    { storeCode: "S1", deviceCode: "IPAD-1" },
  );

  const presenter = service.createPresenter({
    checkoutIntentId: "checkout-1",
    expectedCartRevision: 1,
    total: { currency: "AUD", cents: 500 },
  });
  assert.equal(presenter.getState().total.cents, 500);
  assert.equal(
    JSON.stringify(presenter.getState()).includes("provider-reference"),
    false,
  );
  assert.equal(await service.hasRecoveryRequired(), false);
  presenter.destroy();

  assert.throws(
    () =>
      service.createPresenter({
        checkoutIntentId: "checkout-invalid",
        expectedCartRevision: -1,
        total: { currency: "AUD", cents: 500 },
      }),
    /PAYMENT_DRAFT_CONFLICT/,
  );
});

test("RecallActive 与同一支付草稿并存时启动保留 binding 并锁住恢复购物车", async () => {
  const cart = pricedCart();
  const binding = {
    kind: "recalled",
    scope: { storeCode: "S1", deviceCode: "IPAD-1" },
    holdId: "hold-payment-recovery",
    recallAttemptId: "attempt-payment-recovery",
  } as const;
  const activeCart = new ActivePricingCartSession(
    new PricingCart(),
    () => new PricingCart(),
  );
  activeCart.blockForRecallRecovery(binding);
  const baseDatabase = database();
  const baseDrafts = baseDatabase.paymentDraftRecovery({
    createOrderGuid: () => "unused-order",
    createOrderLineGuid: () => "unused-line",
    createAuditEventId: () => "unused-audit",
  });
  const runtime = createProductionPaymentRuntime({
    database: {
      ...baseDatabase,
      paymentDraftRecovery: () => ({
        ...baseDrafts,
        async findBlockingRecovery() {
          return { ...draftRecovery(cart), recallBinding: binding };
        },
      }),
    } as unknown as PosDatabase,
    repositories: repositories(),
    encryptor,
    activeCart,
    currentCashier: new CurrentCashierSession(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: {
      now: () => new Date("2026-07-28T00:00:00.000Z"),
      nowIso: () => "2026-07-28T00:00:00.000Z",
    },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: bootstrap(() => undefined),
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  assert.deepEqual(activeCart.read().recallBinding, binding);
  assert.deepEqual(activeCart.read().cart, cart.snapshot());
  assert.equal(activeCart.read().terminalRecoveryRequired, false);
  assert.throws(
    () => activeCart.increaseLine("line-1"),
    { code: "ACTIVE_PRICING_CART_BUSY" },
  );
});

test("生产支付 facade 基于当前 cashier 的 TakeCash 权限透传现金可用性", async () => {
  const runtime = createProductionPaymentRuntime({
    database: database(),
    repositories: repositories(),
    encryptor,
    activeCart: new ActivePricingCartSession(
      new PricingCart(),
      () => new PricingCart(),
    ),
    currentCashier: activeCashier(
      ALL_PAYMENT_PERMISSIONS.filter(
        (code) => code !== "Permissions.PosTerminal.Payment.TakeCash",
      ),
    ),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: {
      now: () => new Date("2026-07-28T00:00:00.000Z"),
      nowIso: () => "2026-07-28T00:00:00.000Z",
    },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: bootstrap(() => undefined),
    async drainFulfilment() {},
  });
  await runtime.initializeRecovery();
  if (runtime.service.status !== "available") return;

  const presenter = runtime.service.createPresenter({
    checkoutIntentId: "checkout-no-cash",
    expectedCartRevision: 1,
    total: { currency: "AUD", cents: 500 },
  });
  assert.notEqual(presenter.getState().selectedMethod, "cash");
  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.getState().allowedActions.addCash, false);
});

test("无 Linkly provider 时 return recovery 仍封锁现金金融入口且不触发底层操作", async () => {
  let cashRepositoryWrites = 0;
  const runtime = createProductionPaymentRuntime({
    database: database(),
    repositories: repositories(null, () => {
      cashRepositoryWrites += 1;
    }),
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: {
      now: () => new Date("2026-09-09T00:00:00.000Z"),
      nowIso: () => "2026-09-09T00:00:00.000Z",
    },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: bootstrap(() => undefined),
    hasReturnRecoveryRequired: async () => true,
    async drainFulfilment() {},
  });
  await runtime.initializeRecovery();
  if (runtime.service.status !== "available") assert.fail("payment runtime should be available");
  const presenter = runtime.service.createPresenter({
    checkoutIntentId: "return-blocked-checkout",
    expectedCartRevision: 0,
    total: aud(500),
  });
  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("5.00");
  assert.equal(await presenter.submitSelected(), false);
  assert.equal(presenter.getState().runtimeErrorCode, "RETURN_RECOVERY_REQUIRED");
  assert.equal(cashRepositoryWrites, 0);
  presenter.destroy();
});

test("无 Linkly provider 时 return recovery 也封锁 Square，provider submit/refund/recover 均为零", async () => {
  const calls = { submit: 0, refund: 0, recover: 0 };
  const provider = {
    provider: "square" as const,
    async submit(source: PaymentAttempt) {
      calls.submit += 1;
      return { state: "Unknown" as const, references: source.references, receiptText: null, responseCode: null };
    },
    async refund(source: PaymentAttempt) {
      calls.refund += 1;
      return { state: "Unknown" as const, references: source.references, receiptText: null, responseCode: null };
    },
    async recover(source: PaymentAttempt) {
      calls.recover += 1;
      return { state: "Unknown" as const, references: source.references, receiptText: null, responseCode: null };
    },
    async cancel(source: PaymentAttempt) {
      return { state: "Unknown" as const, references: source.references, receiptText: null, responseCode: null };
    },
  };
  const base = bootstrap(() => undefined);
  const configuredBootstrap = {
    ...base,
    providers: {
      ...base.providers,
      get() { return provider; },
      getAvailability(providerName: PaymentProvider) {
        return { provider: providerName, available: providerName === "square", blocker: null };
      },
      listAvailability() {
        return [
          { provider: "square" as const, available: true, blocker: null },
          { provider: "linkly-cloud" as const, available: false, blocker: "LINKLY_CONFIGURATION_MISSING" as const },
          { provider: "voucher" as const, available: false, blocker: "VOUCHER_CONFIGURATION_DISABLED" as const },
        ];
      },
      listAvailableProviders() { return ["square" as const]; },
    },
  } as unknown as PaymentProviderRuntimeBootstrap;
  const runtime = createProductionPaymentRuntime({
    database: database(),
    repositories: repositories(),
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: { now: () => new Date("2026-09-09T00:00:00.000Z"), nowIso: () => "2026-09-09T00:00:00.000Z" },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: configuredBootstrap,
    hasReturnRecoveryRequired: async () => true,
    async drainFulfilment() {},
  });
  await runtime.initializeRecovery();
  if (runtime.service.status !== "available") assert.fail("payment runtime should be available");
  const presenter = runtime.service.createPresenter({ checkoutIntentId: "square-return-blocked", expectedCartRevision: 0, total: aud(500) });
  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("square"), true);
  presenter.setAmountText("5.00");
  assert.equal(await presenter.submitSelected(), false);
  assert.equal(presenter.getState().runtimeErrorCode, "RETURN_RECOVERY_REQUIRED");
  assert.deepEqual(calls, { submit: 0, refund: 0, recover: 0 });
  presenter.destroy();
});

test("生产组合冷启动只确认已持久的 Linkly 最终 attempt；成功 marker 后不重放金融动作", async () => {
  for (const state of ["Approved", "Declined", "Cancelled"] as const) {
    const harness = productionAcknowledgementHarness({ state });
    await harness.runtime.initializeRecovery();
    if (harness.runtime.service.status !== "available") {
      assert.fail("payment runtime should be available");
    }

    const first = harness.runtime.service.createPresenter(null);
    assert.equal(await first.initialize(), true);
    await first.recover();
    first.destroy();

    assert.deepEqual(harness.counts(), {
      acknowledged: 1,
      financial: 0,
      reconcile: 0,
    });
    assert.ok(harness.current().providerAcknowledgedAtIso);

    // 模拟页面重建：marker 已写时只读原终态，绝不能退回普通支付恢复。
    const reopened = harness.runtime.service.createPresenter(null);
    assert.equal(await reopened.initialize(), true);
    reopened.destroy();
    assert.deepEqual(harness.counts(), {
      acknowledged: 1,
      financial: 0,
      reconcile: 0,
    });
  }
});

test("生产组合 legacy ACK 只接受原 session/环境，并保留 provider this 绑定", async () => {
  const matched = productionAcknowledgementHarness({
    state: "Declined",
    legacy: true,
    listedSessionId: "legacy-session-1",
    listedEnvironment: "Sandbox",
    reconciledEnvironment: "Sandbox",
  });
  await matched.runtime.initializeRecovery();
  if (matched.runtime.service.status !== "available") {
    assert.fail("payment runtime should be available");
  }
  const recovered = matched.runtime.service.createPresenter(null);
  assert.equal(await recovered.initialize(), true);
  await recovered.recover();
  recovered.destroy();
  assert.equal(matched.current().providerEnvironment, "Sandbox");
  assert.deepEqual(matched.counts(), {
    acknowledged: 1,
    financial: 0,
    reconcile: 1,
  });
  assert.equal(matched.reconcileThisBound(), true);

  for (const mismatch of [
    {
      listedSessionId: "other-terminal-session",
      listedEnvironment: "Sandbox",
      reconciledEnvironment: "Sandbox",
    },
    {
      listedSessionId: "legacy-session-1",
      listedEnvironment: "Production",
      reconciledEnvironment: "Sandbox",
    },
  ]) {
    const blocked = productionAcknowledgementHarness({
      state: "Declined",
      legacy: true,
      ...mismatch,
    });
    await blocked.runtime.initializeRecovery();
    if (blocked.runtime.service.status !== "available") {
      assert.fail("payment runtime should be available");
    }
    const presenter = blocked.runtime.service.createPresenter(null);
    assert.equal(await presenter.initialize(), true);
    presenter.destroy();
    assert.deepEqual(blocked.counts(), {
      acknowledged: 0,
      financial: 0,
      reconcile: mismatch.listedSessionId === "legacy-session-1" ? 1 : 0,
    });
    assert.equal(blocked.current().providerEnvironment, undefined);
  }
});

test("legacy discovery 只在 probe/初始化执行一次，普通 presenter read 不重复查询 provider", async () => {
  const harness = productionAcknowledgementHarness({
    state: "Declined",
    legacy: true,
    listedSessionId: "legacy-session-1",
    listedEnvironment: "Sandbox",
    reconciledEnvironment: "Sandbox",
  });
  await harness.runtime.initializeRecovery();
  assert.equal(harness.legacyListCount(), 1);
  if (harness.runtime.service.status !== "available") assert.fail("payment runtime should be available");
  const presenter = harness.runtime.service.createPresenter(null);
  assert.equal(await presenter.initialize(), true);
  presenter.destroy();
  assert.equal(harness.legacyListCount(), 1);
  assert.equal(await harness.runtime.recoveryProbe.hasRecoveryRequired(), true);
  assert.equal(harness.legacyListCount(), 1);
});

test("新库没有旧 Linkly 候选时，冷启动不查询后端 active/resumable", async () => {
  const harness = productionAcknowledgementHarness({ state: "Declined" });
  await harness.runtime.initializeRecovery();
  assert.equal(harness.legacyListCount(), 0);
  await harness.runtime.recoveryProbe.hasRecoveryRequired();
  assert.equal(harness.legacyListCount(), 0);
});

test("普通支付展示行只投影可信活动购物车，忽略路由伪造明细", async () => {
  const cart = pricedCart();
  const activeCart = new ActivePricingCartSession(
    cart,
    () => new PricingCart(),
  );
  const snapshot = activeCart.getSnapshot();
  const runtime = createProductionPaymentRuntime({
    database: database(),
    repositories: repositories(),
    encryptor,
    activeCart,
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: {
      now: () => new Date("2026-07-28T00:00:00.000Z"),
      nowIso: () => "2026-07-28T00:00:00.000Z",
    },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: bootstrap(() => undefined),
    async drainFulfilment() {},
  });
  await runtime.initializeRecovery();
  const service = runtime.service;
  assert.equal(service.status, "available");
  if (service.status !== "available") return;

  const presenter = service.createPresenter({
    checkoutIntentId: "checkout-1",
    expectedCartRevision: snapshot.revision,
    total: snapshot.actualAmount,
    lines: [{
      lineKey: "forged-line",
      displayName: "Forged item",
      quantity: "99",
      actualAmountCents: 1,
    }],
  });
  assert.deepEqual(presenter.getState().checkout.lines, [{
    lineKey: "line-1",
    displayName: "Tea",
    quantity: "1",
    actualAmountCents: 1_000,
  }]);
  presenter.destroy();

  const stalePresenter = service.createPresenter({
    checkoutIntentId: "checkout-stale",
    expectedCartRevision: snapshot.revision + 1,
    total: snapshot.actualAmount,
    lines: [{
      lineKey: "forged-line",
      displayName: "Forged item",
      quantity: "99",
      actualAmountCents: 1,
    }],
  });
  assert.deepEqual(stalePresenter.getState().checkout.lines, []);
  stalePresenter.destroy();
});

test("生产 reversal router 只把现金和礼券交给各自实现，银行卡始终失败关闭且零 provider 调用", async () => {
  const truth = mixedTruth();
  const cashCalls: string[] = [];
  const voucherProviderCalls: string[] = [];
  const cash: MixedTenderReversalPort = {
    async reverseTender(command) {
      cashCalls.push(command.tenderGuid);
      return reversedMutation(truth, command.actionId, command.tenderGuid);
    },
  };
  const voucher: MixedTenderReversalPort = {
    async reverseTender(command) {
      voucherProviderCalls.push(command.tenderGuid);
      return reversedMutation(truth, command.actionId, command.tenderGuid);
    },
  };
  const router = createProductionTenderReversalRouter({
    orderTruth: {
      async getPaymentTruth(orderGuid) {
        return orderGuid === truth.orderGuid ? truth : null;
      },
    },
    cash,
    voucher,
  });

  await router.reverseTender({
    actionId: "reverse-cash",
    orderGuid: "order-1",
    tenderGuid: "cash-1",
    actor: TEST_AUDIT_ACTOR,
  });
  await router.reverseTender({
    actionId: "reverse-voucher",
    orderGuid: "order-1",
    tenderGuid: "voucher-1",
    actor: TEST_AUDIT_ACTOR,
  });
  await assert.rejects(
    router.reverseTender({
      actionId: "reverse-card",
      orderGuid: "order-1",
      tenderGuid: "card-1",
      actor: TEST_AUDIT_ACTOR,
    }),
    /TENDER_REVERSAL_UNAVAILABLE/,
  );

  assert.deepEqual(cashCalls, ["cash-1"]);
  assert.deepEqual(voucherProviderCalls, ["voucher-1"]);
});

test("公开 draft 只有现金和具备 release capability 的礼券可撤，绝不暴露 action/token/provider ref", async () => {
  for (const available of [false, true]) {
    let releaseCalls = 0;
    const cashier = activeCashier();
    const runtime = createProductionPaymentRuntime({
      database: database(checkoutDraft()),
      repositories: repositories(),
      encryptor,
      activeCart: new ActivePricingCartSession(
        new PricingCart(),
        () => new PricingCart(),
      ),
      currentCashier: cashier,
      terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
      clock: {
        now: () => new Date("2026-07-28T00:00:00.000Z"),
        nowIso: () => "2026-07-28T00:00:00.000Z",
      },
      createId: idFactory(),
      connectivity: { async isOnline() { return true; } },
      bootstrap: voucherReleaseBootstrap(available, async () => {
        releaseCalls += 1;
        throw new Error("projection must not call provider");
      }),
      async drainFulfilment() {},
    });
    await runtime.initializeRecovery();
    const service = runtime.service;
    assert.equal(service.status, "available");
    if (service.status !== "available") continue;

    const presenter = service.createPresenter(null);
    assert.equal(await presenter.load("order-1"), true);
    assert.deepEqual(
      presenter.getState().tenders.map((tender) => [
        tender.method,
        tender.reversible,
      ]),
      [
        ["cash", true],
        ["card", false],
        ["voucher", available],
      ],
    );
    const serialized = JSON.stringify(presenter.getState());
    assert.equal(serialized.includes("actionId"), false);
    assert.equal(serialized.includes("reservation-secret"), false);
    assert.equal(serialized.includes("provider-reference"), false);
    assert.equal(releaseCalls, 0);
    presenter.destroy();
  }
});

test("生产 draft port 的 durable close 是唯一提交屏障，提交后不再读取草稿或复核会话", async () => {
  for (const branch of [
    { name: "DraftPrepared", cancellableAfterReversal: false },
    { name: "fully-reversed", cancellableAfterReversal: true },
  ]) {
    const cart = pricedCart();
    const cashier = activeCashier();
    const draft: PaymentCheckoutDraft = {
      checkoutIntentId: "checkout-1",
      orderGuid: "order-1",
      cartRevision: cart.snapshot().revision,
      state: branch.cancellableAfterReversal ? "Completing" : "DraftPrepared",
      total: aud(1_000),
      remaining: aud(1_000),
      cancellableAfterReversal: branch.cancellableAfterReversal,
      tenders: [],
    };
    const durable = durableCloseDatabase({
      draft,
      recovery: draftRecovery(cart),
      onCommitted: () => cashier.clear(),
    });
    const activeCart = new ActivePricingCartSession(
      new PricingCart(),
      () => new PricingCart(),
    );
    const runtime = createProductionPaymentRuntime({
      database: durable.database,
      repositories: repositories(),
      encryptor,
      activeCart,
      currentCashier: cashier,
      terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
      clock: {
        now: () => new Date("2026-07-28T00:00:00.000Z"),
        nowIso: () => "2026-07-28T00:00:00.000Z",
      },
      createId: idFactory(),
      connectivity: { async isOnline() { return true; } },
      bootstrap: bootstrap(() => undefined),
      async drainFulfilment() {},
    });
    await runtime.initializeRecovery();
    assert.equal(runtime.service.status, "available");
    if (runtime.service.status !== "available") continue;
    const presenter = runtime.service.createPresenter(null);
    assert.equal(await presenter.initialize(), true, branch.name);
    assert.equal(presenter.getState().allowedActions.cancel, true);

    assert.equal(await presenter.cancel(), true, branch.name);
    assert.equal(durable.readAfterCommit, 0, branch.name);
    assert.equal(
      branch.cancellableAfterReversal
        ? durable.closeFullyReversedCalls
        : durable.abandonPreparedCalls,
      1,
      branch.name,
    );
    activeCart.increaseLine("line-1");
    assert.equal(activeCart.read().cart.lines[0]?.quantity, "2");
    presenter.destroy();
  }
});

test("礼券 reversal Unknown 后即时关闭换 provider、加现金和再次移除，provider/cash 均零新增调用", async () => {
  let releaseCalls = 0;
  let providerCalls = 0;
  let cashCalls = 0;
  const attempt = approvedVoucherAttempt();
  const cart = pricedCart();
  const truth = voucherOnlyTruth();
  const draft = voucherCheckoutDraft(cart.snapshot().revision);
  const recovery = draftRecovery(cart);
  const record = voucherReversalRecord(truth);
  const cashier = activeCashier();
  const runtime = createProductionPaymentRuntime({
    database: reversalDatabase({
      draft,
      recovery,
      record,
      onCash: () => {
        cashCalls += 1;
      },
    }),
    repositories: repositories(attempt),
    encryptor,
    activeCart: new ActivePricingCartSession(
      new PricingCart(),
      () => new PricingCart(),
    ),
    currentCashier: cashier,
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: {
      now: () => new Date("2026-07-28T00:00:00.000Z"),
      nowIso: () => "2026-07-28T00:00:00.000Z",
    },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: voucherReleaseBootstrap(
      true,
      async (source) => {
        releaseCalls += 1;
        return {
          state: "Unknown",
          references: source.references,
          receiptText: null,
          responseCode: "VOUCHER_RELEASE_RESULT_UNRESOLVED",
        };
      },
      () => {
        providerCalls += 1;
      },
    ),
    async drainFulfilment() {},
  });
  await runtime.initializeRecovery();
  const service = runtime.service;
  assert.equal(service.status, "available");
  if (service.status !== "available") return;
  const presenter = service.createPresenter({
    checkoutIntentId: "checkout-1",
    expectedCartRevision: draft.cartRevision,
    total: draft.total,
  });
  assert.equal(await presenter.load("order-1"), true);
  assert.equal(
    presenter.getState().tenders[0]?.reversible,
    true,
  );

  assert.equal(await presenter.removeTender("voucher-1"), false);
  assert.equal(presenter.getState().phase, "unknown");
  assert.deepEqual(presenter.getState().allowedActions, {
    start: false,
    changeProvider: false,
    recover: true,
    cancel: false,
    addCash: false,
    removeTender: false,
  });
  assert.equal(presenter.selectMethod("cash"), false);
  presenter.setAmountText("5.00");
  assert.equal(await presenter.submitSelected(), false);

  assert.equal(releaseCalls, 1);
  assert.equal(providerCalls, 0);
  assert.equal(cashCalls, 0);
  presenter.destroy();
});

test("重启后从持久 Unknown 撤券恢复原 action，恢复前所有新支付入口保持关闭", async () => {
  let releaseCalls = 0;
  let providerCalls = 0;
  let cashCalls = 0;
  const attempt = approvedVoucherAttempt();
  const cart = pricedCart();
  const draft = voucherCheckoutDraft(cart.snapshot().revision);
  const recovery = draftRecovery(cart);
  const persisted = {
    ...voucherReversalRecord(voucherOnlyTruth()),
    state: "Unknown" as const,
    attemptCount: 1,
    lastErrorCode: "VOUCHER_RELEASE_RESULT_UNRESOLVED",
  };
  const durableDatabase = reversalDatabase({
    draft,
    recovery,
    record: persisted,
    recoveryThrowsAfterFirst: true,
    onCash: () => {
      cashCalls += 1;
    },
  });
  const runtime = createProductionPaymentRuntime({
    database: durableDatabase,
    repositories: repositories(attempt),
    encryptor,
    activeCart: new ActivePricingCartSession(
      new PricingCart(),
      () => new PricingCart(),
    ),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: {
      now: () => new Date("2026-07-28T00:00:00.000Z"),
      nowIso: () => "2026-07-28T00:00:00.000Z",
    },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: voucherReleaseBootstrap(
      true,
      async (source) => {
        releaseCalls += 1;
        return {
          state: "Cancelled",
          references: source.references,
          receiptText: null,
          responseCode: "VOUCHER_RELEASED",
        };
      },
      () => {
        providerCalls += 1;
      },
    ),
    async drainFulfilment() {},
  });
  await runtime.initializeRecovery();
  const service = runtime.service;
  assert.equal(service.status, "available");
  if (service.status !== "available") return;

  const presenter = service.createPresenter(null);
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
  assert.equal(
    JSON.stringify(presenter.getState()).includes(persisted.actionId),
    false,
  );
  assert.equal(presenter.selectMethod("cash"), false);
  presenter.setAmountText("5.00");
  assert.equal(await presenter.submitSelected(), false);
  assert.equal(cashCalls, 0);
  assert.equal(providerCalls, 0);
  assert.equal(releaseCalls, 0);

  assert.equal(await presenter.recover(), true);
  assert.equal(releaseCalls, 1);
  assert.equal(providerCalls, 0);
  assert.equal(cashCalls, 0);
  assert.equal(presenter.getState().phase, "partial");
  assert.equal(presenter.getState().remaining.cents, 1_000);
  presenter.destroy();
});

test("Prepared/Submitted/Unknown/Blocked 重启投影与 provider capability 一致且始终脱敏锁单", async (t) => {
  for (const fixture of [
    {
      state: "Prepared" as const,
      attemptCount: 0,
      lastErrorCode: null,
      releaseAvailable: true,
      phase: "recovery-required",
      recover: true,
      errorCode: "TENDER_REVERSAL_RECOVERY_REQUIRED",
    },
    {
      state: "Submitted" as const,
      attemptCount: 1,
      lastErrorCode: null,
      releaseAvailable: true,
      phase: "recovery-required",
      recover: true,
      errorCode: "TENDER_REVERSAL_RECOVERY_REQUIRED",
    },
    {
      state: "Unknown" as const,
      attemptCount: 1,
      lastErrorCode: "VOUCHER_RELEASE_RESULT_UNRESOLVED",
      releaseAvailable: false,
      phase: "unknown",
      recover: false,
      errorCode: "TENDER_REVERSAL_UNAVAILABLE",
    },
    {
      state: "Blocked" as const,
      attemptCount: 1,
      lastErrorCode: "VOUCHER_RELEASE_REJECTED",
      releaseAvailable: true,
      phase: "recovery-required",
      recover: false,
      errorCode: "TENDER_REVERSAL_BLOCKED",
    },
  ]) {
    await t.test(fixture.state, async () => {
      let releaseCalls = 0;
      let providerCalls = 0;
      let cashCalls = 0;
      const cart = pricedCart();
      const draft = voucherCheckoutDraft(cart.snapshot().revision);
      const persisted = {
        ...voucherReversalRecord(voucherOnlyTruth()),
        state: fixture.state,
        attemptCount: fixture.attemptCount,
        lastErrorCode: fixture.lastErrorCode,
      };
      const runtime = createProductionPaymentRuntime({
        database: reversalDatabase({
          draft,
          recovery: draftRecovery(cart),
          record: persisted,
          persisted: true,
          onCash: () => {
            cashCalls += 1;
          },
        }),
        repositories: repositories(approvedVoucherAttempt()),
        encryptor,
        activeCart: new ActivePricingCartSession(
          new PricingCart(),
          () => new PricingCart(),
        ),
        currentCashier: activeCashier(),
        terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
        clock: {
          now: () => new Date("2026-07-28T00:00:00.000Z"),
          nowIso: () => "2026-07-28T00:00:00.000Z",
        },
        createId: idFactory(),
        connectivity: { async isOnline() { return true; } },
        bootstrap: voucherReleaseBootstrap(
          fixture.releaseAvailable,
          async (source) => {
            releaseCalls += 1;
            return {
              state: "Unknown",
              references: source.references,
              receiptText: null,
              responseCode: "TEST_MUST_NOT_RELEASE",
            };
          },
          () => {
            providerCalls += 1;
          },
        ),
        async drainFulfilment() {},
      });
      await runtime.initializeRecovery();
      const service = runtime.service;
      assert.equal(service.status, "available");
      if (service.status !== "available") return;
      const presenter = service.createPresenter(null);

      assert.equal(await presenter.initialize(), true);
      assert.equal(presenter.getState().phase, fixture.phase);
      assert.equal(
        presenter.getState().runtimeErrorCode,
        fixture.errorCode,
      );
      assert.deepEqual(presenter.getState().allowedActions, {
        start: false,
        changeProvider: false,
        recover: fixture.recover,
        cancel: false,
        addCash: false,
        removeTender: false,
      });
      const encoded = JSON.stringify(presenter.getState());
      assert.equal(encoded.includes(persisted.actionId), false);
      assert.equal(encoded.includes("reservation-secret"), false);
      assert.equal(encoded.includes("voucherCode"), false);
      assert.equal(presenter.selectMethod("cash"), false);
      presenter.setAmountText("5.00");
      assert.equal(await presenter.submitSelected(), false);
      if (!fixture.recover) {
        assert.equal(await presenter.recover(), false);
      }
      assert.equal(releaseCalls, 0);
      assert.equal(providerCalls, 0);
      assert.equal(cashCalls, 0);
      presenter.destroy();
    });
  }
});

test("恢复中心离线列表只读本地账本，不等待联网或 provider", async () => {
  let connectivityCalls = 0;
  let providerCalls = 0;
  const record = recoveryCenterRecord({ status: "result-unknown", attemptState: "Unknown" });
  const base = database();
  const runtime = createProductionPaymentRuntime({
    database: {
      ...base,
      paymentRecoveryCenter: () => ({
        async list() { return [record]; },
        async findCurrentCandidate() { return null; },
        async getExact() { return record; },
      }),
    } as unknown as PosDatabase,
    repositories: repositories(),
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { connectivityCalls += 1; return false; } },
    bootstrap: bootstrap(() => undefined, () => { providerCalls += 1; }),
    async drainFulfilment() {},
  });

  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  assert.deepEqual(await center.list(), [record]);
  assert.equal(connectivityCalls, 0);
  assert.equal(providerCalls, 0);
});

test("无 provider bootstrap 时人工已收款缺失 tender 可按原 action 重放完成", async () => {
  let committed = false;
  let commitCalls = 0;
  const base = database();
  const pending = recoveryCenterRecord({
    status: "charged-order-incomplete",
    attemptState: "Unknown",
    orderState: "Draft",
  });
  const completed = { ...pending, status: "manual-paid" as const, orderState: "PendingSync" };
  const runtime = createProductionPaymentRuntime({
    database: {
      ...base,
      paymentRecoveryCenter: () => ({
        async list() { return [committed ? completed : pending]; },
        async findCurrentCandidate() { return null; },
        async getExact() { return committed ? completed : pending; },
        async getManualPaidCommitContext() {
          return {
            record: pending,
            actionId: "manual-action-1",
            authorizationId: "supervisor-authorization-1",
            supervisorActor: { cashierId: "supervisor-1", cashierName: "Supervisor", userGuid: null },
            tenderGuid: null,
          };
        },
      }),
      manualPaymentOrderCommitter: () => ({
        async completeManualPaymentOrder(command: Readonly<{ actionId: string; attemptId: string }>) {
          commitCalls += 1;
          assert.equal(command.actionId, "manual-action-1");
          assert.equal(command.attemptId, pending.attemptId);
          committed = true;
          return { replayed: false };
        },
      }),
    } as unknown as PosDatabase,
    repositories: repositories(),
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { throw new Error("must stay local"); } },
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  assert.equal(await center.recoverOriginalPayment(pending.recordId), "completed");
  assert.equal(commitCalls, 1);
  assert.equal(committed, true);
});

for (const queryState of ["Approved", "Unknown"] as const) {
  test(`人工已收款先只读对账原 attempt：${queryState} 时${queryState === "Approved" ? "拒绝人工入账" : "持久绑定后提交"}`, async () => {
    const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
    const attempt = { ...recoveryAttempt("Unknown"), providerEnvironment: "Sandbox" };
    const repositoryBundle = repositories(attempt);
    const record = recoveryCenterRecord({ status: "result-unknown", attemptState: "Unknown" });
    let reconciliationCalls = 0;
    let findingCalls = 0;
    let commitCalls = 0;
    const base = database();
    const runtime = createProductionPaymentRuntime({
      database: {
        ...base,
        paymentRecoveryCenter: () => ({
          async list() { return [record]; },
          async findCurrentCandidate() { return null; },
          async getExact() { return record; },
          async recordProviderReconciliation() {
            reconciliationCalls += 1;
            const current = await repositoryBundle.payments.get(attempt.attemptId);
            if (current?.state !== "Unknown") throw new Error("PAYMENT_RECOVERY_RECONCILIATION_NOT_UNRESOLVED");
            return "reconciliation-1";
          },
          async recordManualFinding(input: ManualPaymentRecoveryFindingInput) {
            findingCalls += 1;
            assert.equal(input.reconciliationId, "reconciliation-1");
            return { record, actionId: "manual-action-1", authorizationId: input.authorizationId, replayed: false };
          },
        }),
        manualPaymentOrderCommitter: () => ({
          async completeManualPaymentOrder() { commitCalls += 1; return { replayed: false }; },
        }),
      } as unknown as PosDatabase,
      repositories: repositoryBundle,
      encryptor,
      activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
      currentCashier: activeCashier(),
      terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
      clock: testClock(),
      createId: idFactory(),
      connectivity: { async isOnline() { return true; } },
      bootstrap: queryOnlyBootstrap(calls, () => undefined, queryState),
      authorizeRecovery: async (_request, run) => run({
        authorizationId: "authorization-1",
        authorizingActor: {
          cashierId: "supervisor-2",
          cashierName: "Supervisor",
          userGuid: "supervisor-user-2",
        },
      }),
      async drainFulfilment() {},
    });

    const center = runtime.service.recoveryCenter;
    assert.ok(center);
    const action = center.submitManualVerification({
      recordId: record.recordId,
      finding: "paid",
      verifiedAmountCents: record.amountCents,
      evidenceReference: "terminal-history",
      note: "Read-only provider check completed",
    });
    if (queryState === "Approved") {
      await assert.rejects(action, /RECONCILIATION_NOT_UNRESOLVED/);
      assert.deepEqual({ reconciliationCalls, findingCalls, commitCalls }, { reconciliationCalls: 0, findingCalls: 0, commitCalls: 0 });
    } else {
      await action;
      assert.deepEqual({ reconciliationCalls, findingCalls, commitCalls }, { reconciliationCalls: 1, findingCalls: 1, commitCalls: 1 });
    }
    assert.equal(calls.query, 1);
    assert.deepEqual({ submit: calls.submit, refund: calls.refund, recover: calls.recover, cancel: calls.cancel },
      { submit: 0, refund: 0, recover: 0, cancel: 0 });
  });
}

for (const scenario of ["Created", "stale-record", "query-unavailable", "query-throws", "square-transport-error", "missing-checkout", "bootstrap-unavailable"] as const) {
  test(`人工已收款 ${scenario} 不得伪造对账或继续入账`, async () => {
    const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
    const source = {
      ...recoveryAttempt(scenario === "Created" || scenario === "stale-record" ? "Created" : "Unknown"),
      providerEnvironment: "Sandbox",
    };
    const attempt = scenario === "missing-checkout"
      ? { ...source, references: { ...source.references, checkoutId: null } }
      : source;
    const repositoryBundle = repositories(attempt);
    // 页面记录可能过时，必须以耐久 attempt 的状态复核。
    const record = recoveryCenterRecord({ status: "result-unknown", attemptState: scenario === "Created" ? "Created" : "Unknown" });
    let reconciliationCalls = 0;
    let findingCalls = 0;
    let commitCalls = 0;
    const providerBootstrap = queryOnlyBootstrap(calls, () => {
      if (scenario === "query-throws") throw new Error("query transport failed");
    }, "Unknown");
    if (scenario === "query-unavailable") {
      const provider = providerBootstrap.providers.get("square");
      const { queryExistingPayment: _query, ...withoutQuery } = provider as typeof provider & { queryExistingPayment?: unknown };
      providerBootstrap.providers.get = () => withoutQuery;
    }
    if (scenario === "square-transport-error") {
      const square = new SquarePaymentAdapter({
        async request(request) {
          calls.query += 1;
          assert.equal(request.method, "GET");
          throw new Error("network timeout");
        },
      }, async () => ({ environment: "Sandbox", deviceId: "device-1", locationId: "location-1" }), "Sandbox");
      providerBootstrap.providers.get = () => square;
    }
    const runtime = createProductionPaymentRuntime({
      database: {
        ...database(),
        paymentRecoveryCenter: () => ({
          async getExact() { return record; },
          async recordProviderReconciliation() { reconciliationCalls += 1; return "reconciliation-1"; },
          async recordManualFinding(input: ManualPaymentRecoveryFindingInput) {
            findingCalls += 1;
            return { record, actionId: "manual-action-1", authorizationId: input.authorizationId, replayed: false };
          },
        }),
        manualPaymentOrderCommitter: () => ({
          async completeManualPaymentOrder() { commitCalls += 1; return { replayed: false }; },
        }),
      } as unknown as PosDatabase,
      repositories: repositoryBundle, encryptor,
      activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
      currentCashier: activeCashier(),
      terminal: { storeCode: "S1", deviceCode: "IPAD-1" }, clock: testClock(),
      createId: idFactory(), connectivity: { async isOnline() { return true; } },
      ...(scenario === "bootstrap-unavailable" ? {} : { bootstrap: providerBootstrap }),
      authorizeRecovery: async (_request, run) => run({
        authorizationId: "authorization-1",
        authorizingActor: { cashierId: "supervisor-2", cashierName: "Supervisor", userGuid: "supervisor-user-2" },
      }),
      async drainFulfilment() {},
    });
    const center = runtime.service.recoveryCenter;
    assert.ok(center);
    await assert.rejects(center.submitManualVerification({
      recordId: record.recordId, finding: "paid", verifiedAmountCents: record.amountCents,
      evidenceReference: "terminal-history", note: "Checked terminal history",
    }), /PAYMENT_RECOVERY_/);
    assert.deepEqual({ reconciliationCalls, findingCalls, commitCalls }, { reconciliationCalls: 0, findingCalls: 0, commitCalls: 0 });
    assert.deepEqual(calls, { query: scenario === "query-throws" || scenario === "square-transport-error" ? 1 : 0, submit: 0, refund: 0, recover: 0, cancel: 0 });
    assert.equal((await repositoryBundle.payments.get(attempt.attemptId))?.state, attempt.state);
  });
}

for (const attemptState of ["Submitted", "Unknown"] as const) {
  test(`人工未收款 ${attemptState} 仅查询原交易，迟到 Approved 写回且不重新提交`, async () => {
    const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
    const attempt = recoveryAttempt(attemptState);
    const repositoryBundle = repositories(attempt);
    const record = recoveryCenterRecord({ status: "manual-unpaid", attemptState });
    const runtime = createProductionPaymentRuntime({
      database: recoveryCenterDatabase(record, repositoryBundle),
      repositories: repositoryBundle,
      encryptor,
      activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
      currentCashier: activeCashier(),
      terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
      clock: testClock(),
      createId: idFactory(),
      connectivity: { async isOnline() { return true; } },
      bootstrap: queryOnlyBootstrap(calls),
      async drainFulfilment() {},
    });

    await runtime.initializeRecovery();
    const center = runtime.service.recoveryCenter;
    assert.ok(center);
    assert.equal(await center.recoverOriginalPayment(record.recordId), "completed");
    assert.equal(calls.query, 1);
    assert.deepEqual(
      { submit: calls.submit, refund: calls.refund, recover: calls.recover, cancel: calls.cancel },
      { submit: 0, refund: 0, recover: 0, cancel: 0 },
    );
    assert.equal((await repositoryBundle.payments.get(attempt.attemptId))?.state, "Approved");
  });
}

test("人工未收款 Created 不调用任何 provider 方法，只恢复原购物车", async () => {
  const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
  const attempt = recoveryAttempt("Created");
  const repositoryBundle = repositories(attempt);
  const record = recoveryCenterRecord({ status: "manual-unpaid", attemptState: "Created" });
  const runtime = createProductionPaymentRuntime({
    database: recoveryCenterDatabase(record, repositoryBundle),
    repositories: repositoryBundle,
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: queryOnlyBootstrap(calls),
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  assert.equal(await center.recoverOriginalPayment(record.recordId), undefined);
  assert.deepEqual(calls, { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 });
  assert.equal((await repositoryBundle.payments.get(attempt.attemptId))?.state, "Created");
});

test("provider 已配置但离线时人工未收款仍可恢复原单改收现金", async () => {
  const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
  let resumeCalls = 0;
  const attempt = recoveryAttempt("Unknown");
  const repositoryBundle = repositories(attempt);
  const record = recoveryCenterRecord({ status: "manual-unpaid", attemptState: "Unknown" });
  const runtime = createProductionPaymentRuntime({
    database: recoveryCenterDatabase(record, repositoryBundle, () => { resumeCalls += 1; }),
    repositories: repositoryBundle,
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { return false; } },
    bootstrap: queryOnlyBootstrap(calls),
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  assert.equal(await center.recoverOriginalPayment(record.recordId), undefined);
  assert.equal(resumeCalls, 1);
  assert.deepEqual(calls, { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 });
  assert.equal((await repositoryBundle.payments.get(attempt.attemptId))?.state, "Unknown");
});

test("人工仍未知 Created 保持 parked 且不提交或恢复金融请求", async () => {
  const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
  let resumeCalls = 0;
  const attempt = recoveryAttempt("Created");
  const repositoryBundle = repositories(attempt);
  const record = recoveryCenterRecord({ status: "manual-uncertain", attemptState: "Created" });
  const runtime = createProductionPaymentRuntime({
    database: recoveryCenterDatabase(record, repositoryBundle, () => { resumeCalls += 1; }),
    repositories: repositoryBundle,
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: queryOnlyBootstrap(calls),
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  assert.equal(await center.recoverOriginalPayment(record.recordId), "completed");
  assert.equal(resumeCalls, 0);
  assert.deepEqual(calls, { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 });
  assert.equal((await repositoryBundle.payments.get(attempt.attemptId))?.state, "Created");
});

test("已完成 Linkly 订单 ACK 未耐久时恢复中心保持待处理错误", async () => {
  let acknowledgementCalls = 0;
  const attempt: PaymentAttempt = {
    ...recoveryAttempt("Approved"),
    provider: "linkly-cloud",
    providerEnvironment: "Sandbox",
    references: {
      ...recoveryAttempt("Approved").references,
      sessionId: "linkly-recovery-session",
    },
  };
  const baseRepositories = repositories(attempt);
  const repositoryBundle = {
    ...baseRepositories,
    payments: {
      ...baseRepositories.payments,
      async canProviderAcknowledged() { return true; },
      async markProviderAcknowledged() { return false; },
    },
  } as PosRepositoryBundle;
  const record = {
    ...recoveryCenterRecord({
      status: "provider-recovered",
      attemptState: "Approved",
      orderState: "PendingSync",
    }),
    provider: "linkly-cloud" as const,
  };
  const baseBootstrap = bootstrap(() => undefined);
  const provider = {
    provider: "linkly-cloud" as const,
    async acknowledge() { acknowledgementCalls += 1; },
    async submit(source: PaymentAttempt) { return unknownProviderResult(source); },
    async recover(source: PaymentAttempt) { return unknownProviderResult(source); },
    async cancel(source: PaymentAttempt) { return unknownProviderResult(source); },
    async refund(source: PaymentAttempt) { return unknownProviderResult(source); },
  };
  const runtime = createProductionPaymentRuntime({
    database: recoveryCenterDatabase(record, repositoryBundle),
    repositories: repositoryBundle,
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: {
      ...baseBootstrap,
      providers: {
        ...baseBootstrap.providers,
        get() { return provider; },
      } as unknown as PaymentProviderRuntimeBootstrap["providers"],
      linklyTerminals: { environment: "Sandbox", port: {} as never },
    },
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  await assert.rejects(
    center.recoverOriginalPayment(record.recordId),
    /LINKLY_ACKNOWLEDGEMENT_PENDING/,
  );
  assert.equal(acknowledgementCalls, 1);
});

test("人工已收款且订单已完成时 Created 也不调用 provider", async () => {
  const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
  const attempt = recoveryAttempt("Created");
  const repositoryBundle = repositories(attempt);
  const record = recoveryCenterRecord({
    status: "manual-paid",
    attemptState: "Created",
    orderState: "PendingSync",
  });
  const runtime = createProductionPaymentRuntime({
    database: recoveryCenterDatabase(record, repositoryBundle),
    repositories: repositoryBundle,
    encryptor,
    activeCart: new ActivePricingCartSession(new PricingCart(), () => new PricingCart()),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: queryOnlyBootstrap(calls),
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  assert.equal(await center.recoverOriginalPayment(record.recordId), "completed");
  assert.deepEqual(calls, { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 });
  assert.equal((await repositoryBundle.payments.get(attempt.attemptId))?.state, "Created");
});

test("仅查询返回 Approved 但账本 CAS 失败时不恢复购物车或解除 parked", async () => {
  const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
  let resumeCalls = 0;
  const attempt = recoveryAttempt("Unknown");
  const baseRepositories = repositories(attempt);
  const repositoryBundle = {
    ...baseRepositories,
    payments: {
      ...baseRepositories.payments,
      async compareAndUpdate() { throw new Error("simulated ledger CAS failure"); },
    },
  } as PosRepositoryBundle;
  const record = recoveryCenterRecord({ status: "manual-unpaid", attemptState: "Unknown" });
  const activeCart = new ActivePricingCartSession(new PricingCart(), () => new PricingCart());
  const runtime = createProductionPaymentRuntime({
    database: recoveryCenterDatabase(record, repositoryBundle, () => { resumeCalls += 1; }),
    repositories: repositoryBundle,
    encryptor,
    activeCart,
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: queryOnlyBootstrap(calls),
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  await assert.rejects(center.recoverOriginalPayment(record.recordId));
  assert.equal(calls.query, 1);
  assert.deepEqual(
    { submit: calls.submit, refund: calls.refund, recover: calls.recover, cancel: calls.cancel },
    { submit: 0, refund: 0, recover: 0, cancel: 0 },
  );
  assert.equal(resumeCalls, 0);
  assert.deepEqual(activeCart.getSnapshot().lines, []);
});

test("查询期间换班时只允许 Approved 落账，不恢复购物车或解除 parked", async () => {
  const calls = { query: 0, submit: 0, refund: 0, recover: 0, cancel: 0 };
  let resumeCalls = 0;
  const cashier = activeCashier();
  const attempt = recoveryAttempt("Submitted");
  const repositoryBundle = repositories(attempt);
  const record = recoveryCenterRecord({ status: "manual-unpaid", attemptState: "Submitted" });
  const activeCart = new ActivePricingCartSession(new PricingCart(), () => new PricingCart());
  const runtime = createProductionPaymentRuntime({
    database: recoveryCenterDatabase(record, repositoryBundle, () => { resumeCalls += 1; }),
    repositories: repositoryBundle,
    encryptor,
    activeCart,
    currentCashier: cashier,
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: testClock(),
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: queryOnlyBootstrap(calls, () => {
      cashier.clear();
      const epoch = cashier.beginAuthentication();
      cashier.activate(epoch, {
        source: "online",
        session: {
          cashierId: "cashier-2",
          cashierName: "Replacement Cashier",
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [...ALL_PAYMENT_PERMISSIONS],
        },
      }, { storeCode: "S1", deviceCode: "IPAD-1" });
    }),
    async drainFulfilment() {},
  });

  await runtime.initializeRecovery();
  const center = runtime.service.recoveryCenter;
  assert.ok(center);
  await assert.rejects(center.recoverOriginalPayment(record.recordId), /CURRENT_CASHIER_REQUIRED/);
  assert.equal(calls.query, 1);
  assert.deepEqual(
    { submit: calls.submit, refund: calls.refund, recover: calls.recover, cancel: calls.cancel },
    { submit: 0, refund: 0, recover: 0, cancel: 0 },
  );
  assert.equal((await repositoryBundle.payments.get(attempt.attemptId))?.state, "Approved");
  assert.equal(resumeCalls, 0);
  assert.deepEqual(activeCart.getSnapshot().lines, []);
});

test("Blocked 撤券：主管授权作废后不再阻塞并恢复可操作；授权取消保持阻断；未提供授权时不开放入口", async (t) => {
  const createRuntime = (options: Readonly<{
    authorize?: TenderReversalVoidAuthorizer;
    onVoid?(command: unknown): void;
  }>) => {
    const cart = pricedCart();
    const persisted = {
      ...voucherReversalRecord(voucherOnlyTruth()),
      state: "Blocked" as const,
      attemptCount: 1,
      lastErrorCode: "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
    };
    return createProductionPaymentRuntime({
      database: reversalDatabase({
        draft: voucherCheckoutDraft(cart.snapshot().revision),
        recovery: draftRecovery(cart),
        record: persisted,
        persisted: true,
        onCash: () => {
          throw new Error("cash must not be called");
        },
        ...(options.onVoid ? { onVoid: options.onVoid } : {}),
      }),
      repositories: repositories(approvedVoucherAttempt()),
      encryptor,
      activeCart: new ActivePricingCartSession(
        new PricingCart(),
        () => new PricingCart(),
      ),
      currentCashier: activeCashier(),
      terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
      clock: {
        now: () => new Date("2026-07-28T00:00:00.000Z"),
        nowIso: () => "2026-07-28T00:00:00.000Z",
      },
      createId: idFactory(),
      connectivity: { async isOnline() { return true; } },
      bootstrap: voucherReleaseBootstrap(
        true,
        async () => {
          throw new Error("void must never call provider release");
        },
        () => {
          throw new Error("void must never call provider");
        },
      ),
      ...(options.authorize
        ? { authorizeTenderReversalVoid: options.authorize }
        : {}),
      async drainFulfilment() {},
    });
  };

  await t.test("主管授权成功", async () => {
    const voidCommands: unknown[] = [];
    const authorizations: unknown[] = [];
    const runtime = createRuntime({
      authorize: async (request, run) => {
        authorizations.push(request);
        return run({
          authorizingActor: {
            cashierId: "supervisor-1",
            cashierName: "Supervisor",
            userGuid: "supervisor-guid-1",
          },
          permissionCode: request.permissionCode,
        });
      },
      onVoid: (command) => voidCommands.push(command),
    });
    await runtime.initializeRecovery();
    const service = runtime.service;
    assert.equal(service.status, "available");
    if (service.status !== "available") return;
    assert.equal(await runtime.recoveryProbe.hasRecoveryRequired(), true);

    const presenter = service.createPresenter(null);
    assert.equal(await presenter.initialize(), true);
    // 修复前：只有 Blocked 提示，所有动作均为 false，且没有任何出口。
    assert.equal(presenter.getState().runtimeErrorCode, "TENDER_REVERSAL_BLOCKED");
    assert.deepEqual(presenter.getState().tenderReversalRecovery, {
      tenderGuid: "voucher-1",
      status: "blocked",
      voidAvailable: true,
    });
    assert.equal(presenter.getState().allowedActions.recover, false);

    assert.equal(await presenter.voidBlockedTenderReversal(), true);
    assert.equal(authorizations.length, 1);
    assert.deepEqual(authorizations[0], {
      action: "payment-tender-void-unreleased",
      screen: "payment",
      orderGuid: "order-1",
      tenderGuid: "voucher-1",
      permissionCode: "Permissions.PosTerminal.Payment.RemoveTender",
    });
    assert.equal(voidCommands.length, 1);
    assert.deepEqual(voidCommands[0], {
      scope: { storeCode: "S1", deviceCode: "IPAD-1" },
      actionId: "id-2",
      orderGuid: "order-1",
      sourceTenderGuid: "voucher-1",
      requestingActor: TEST_AUDIT_ACTOR,
      authorizingActor: {
        cashierId: "supervisor-1",
        cashierName: "Supervisor",
        userGuid: "supervisor-guid-1",
      },
      permissionCode: "Permissions.PosTerminal.Payment.RemoveTender",
    });
    const state = presenter.getState();
    assert.equal(state.tenderReversalRecovery, null);
    assert.equal(state.runtimeErrorCode, null);
    assert.equal(state.remaining.cents, 1_000);
    assert.equal(state.tenders.length, 0);
    // 订单回到普通未完成支付：可继续收款，或在无 tender 时取消回到销售页。
    assert.equal(
      state.allowedActions.start || state.allowedActions.addCash,
      true,
    );
    assert.equal(state.allowedActions.cancel, true);
    // 撤券账本不再贡献阻断；剩余的 true 只来自普通活动支付草稿。
    assert.equal(await runtime.recoveryProbe.hasRecoveryRequired(), true);
    // 幂等：已无阻断时再次调用不会再授权或再次落账。
    assert.equal(await presenter.voidBlockedTenderReversal(), false);
    assert.equal(authorizations.length, 1);
    presenter.destroy();
  });

  await t.test("主管授权取消", async () => {
    let voidCalls = 0;
    const runtime = createRuntime({
      authorize: async () => {
        throw new Error("TENDER_REVERSAL_VOID_AUTHORIZATION_CANCELLED");
      },
      onVoid: () => {
        voidCalls += 1;
      },
    });
    await runtime.initializeRecovery();
    const service = runtime.service;
    if (service.status !== "available") return assert.fail();
    const presenter = service.createPresenter(null);
    assert.equal(await presenter.initialize(), true);
    assert.equal(await presenter.voidBlockedTenderReversal(), false);
    assert.equal(voidCalls, 0);
    assert.equal(
      presenter.getState().runtimeErrorCode,
      "TENDER_REVERSAL_VOID_NOT_AUTHORIZED",
    );
    assert.equal(presenter.getState().tenderReversalRecovery?.status, "blocked");
    assert.equal(presenter.getState().busy, false);
    presenter.destroy();
  });

  await t.test("组合根未提供主管授权", async () => {
    const runtime = createRuntime({});
    await runtime.initializeRecovery();
    const service = runtime.service;
    if (service.status !== "available") return assert.fail();
    const presenter = service.createPresenter(null);
    assert.equal(await presenter.initialize(), true);
    assert.deepEqual(presenter.getState().tenderReversalRecovery, {
      tenderGuid: "voucher-1",
      status: "blocked",
    });
    assert.equal(await presenter.voidBlockedTenderReversal(), false);
    presenter.destroy();
  });
});

function bootstrap(
  onBind: () => void,
  onProviderGet: () => void = () => undefined,
): PaymentProviderRuntimeBootstrap {
  return {
    providers: {
      get() {
        onProviderGet();
        throw new Error("provider execution is outside this composition test");
      },
      getAvailability(provider: PaymentProvider) {
        return {
          provider,
          available: false,
          blocker:
            provider === "square"
              ? "SQUARE_CONFIGURATION_MISSING"
              : provider === "linkly-cloud"
                ? "LINKLY_CONFIGURATION_MISSING"
                : "VOUCHER_CONFIGURATION_DISABLED",
        };
      },
      listAvailability() {
        return [
          {
            provider: "square",
            available: false,
            blocker: "SQUARE_CONFIGURATION_MISSING",
          },
          {
            provider: "linkly-cloud",
            available: false,
            blocker: "LINKLY_CONFIGURATION_MISSING",
          },
          {
            provider: "voucher",
            available: false,
            blocker: "VOUCHER_CONFIGURATION_DISABLED",
          },
        ];
      },
      listAvailableProviders() {
        return [];
      },
    } as unknown as PaymentProviderRuntimeBootstrap["providers"],
    configurationAvailability:
      {} as PaymentProviderRuntimeBootstrap["configurationAvailability"],
    bindVoucherContextProvider() {
      onBind();
    },
    createLinklyOperator() {
      return null;
    },
  };
}

type QueryOnlyProviderCalls = {
  query: number;
  submit: number;
  refund: number;
  recover: number;
  cancel: number;
};

function queryOnlyBootstrap(
  calls: QueryOnlyProviderCalls,
  onQuery: () => void = () => undefined,
  queryState: "Approved" | "Unknown" = "Approved",
): PaymentProviderRuntimeBootstrap {
  const base = bootstrap(() => undefined);
  const provider = {
    provider: "square" as const,
    async queryExistingPayment(source: PaymentAttempt) {
      calls.query += 1;
      onQuery();
      return {
        queryVerified: true,
        state: queryState,
        references: source.references,
        receiptText: null,
        responseCode: "APPROVED",
        ...(queryState === "Approved" ? { protectedSyncEvidence: {
          version: 1 as const,
          provider: "square" as const,
          operation: source.operation,
          processor: "Square" as const,
          txnRef: source.references.txnRef,
          authCode: null,
          cardType: null,
          cardBin: null,
          maskedCardNumber: null,
          merchantId: null,
          responseCode: "APPROVED",
          responseText: "Approved",
          stan: null,
          bankDateTimeIso: null,
          amountCents: Math.abs(source.amount.cents),
          refundReference: null,
        } } : {}),
      };
    },
    async submit(source: PaymentAttempt) {
      calls.submit += 1;
      return unknownProviderResult(source);
    },
    async refund(source: PaymentAttempt) {
      calls.refund += 1;
      return unknownProviderResult(source);
    },
    async recover(source: PaymentAttempt) {
      calls.recover += 1;
      return unknownProviderResult(source);
    },
    async cancel(source: PaymentAttempt) {
      calls.cancel += 1;
      return unknownProviderResult(source);
    },
  };
  return {
    ...base,
    providers: {
      ...base.providers,
      get(providerName: PaymentProvider) {
        if (providerName !== "square") throw new Error(`unexpected provider ${providerName}`);
        return provider;
      },
      getAvailability(providerName: PaymentProvider) {
        return providerName === "square"
          ? { provider: providerName, available: true, blocker: null }
          : { provider: providerName, available: false, blocker: "PAYMENT_PROVIDER_UNKNOWN" };
      },
      listAvailability() {
        return [
          { provider: "square" as const, available: true as const, blocker: null },
          { provider: "linkly-cloud" as const, available: false as const, blocker: "PAYMENT_PROVIDER_UNKNOWN" as const },
          { provider: "voucher" as const, available: false as const, blocker: "PAYMENT_PROVIDER_UNKNOWN" as const },
        ];
      },
      listAvailableProviders() { return ["square"] as const; },
    } as unknown as PaymentProviderRuntimeBootstrap["providers"],
  };
}

function unknownProviderResult(source: PaymentAttempt) {
  return {
    state: "Unknown" as const,
    references: source.references,
    receiptText: null,
    responseCode: "TEST_FINANCIAL_METHOD_MUST_NOT_RUN",
  };
}

function recoveryCenterDatabase(
  baseRecord: PaymentRecoveryCenterRecord,
  repositoryBundle: PosRepositoryBundle,
  onResume: () => void = () => undefined,
): PosDatabase {
  const cart = pricedCart();
  const base = database();
  return {
    ...base,
    paymentDraftRecovery: () => ({
      async assertPersisted() {},
      async findBlockingRecovery() { return null; },
      async readRecoveryCart(orderGuid: string) {
        if (orderGuid !== baseRecord.orderGuid) return null;
        return {
          checkoutIntentId: baseRecord.checkoutIntentId,
          cart: cart.snapshot(),
          pricingState: cart.stateSnapshot(),
          recallBinding: null,
        };
      },
    }),
    paymentRecoveryCenter: () => ({
      async list() { return [await currentRecord()]; },
      async findCurrentCandidate() { return null; },
      async getExact(_scope: unknown, recordId: string) {
        return recordId === baseRecord.recordId ? currentRecord() : null;
      },
      async getManualPaidCommitContext() { return null; },
      async resumeExact() { onResume(); return currentRecord(); },
    }),
  } as unknown as PosDatabase;

  async function currentRecord(): Promise<PaymentRecoveryCenterRecord> {
    const attempt = await repositoryBundle.payments.get(baseRecord.attemptId);
    assert.ok(attempt);
    return {
      ...baseRecord,
      attemptState: attempt.state,
      status: baseRecord.status === "manual-unpaid" && attempt.state === "Approved"
        ? "review-required"
        : baseRecord.status,
    };
  }
}

function recoveryCenterRecord(input: Readonly<{
  status: PaymentRecoveryCenterRecord["status"];
  attemptState: PaymentAttempt["state"];
  orderState?: string;
}>): PaymentRecoveryCenterRecord {
  return {
    recordId: "recovery-record-1",
    checkoutIntentId: "checkout-1",
    orderGuid: "order-1",
    attemptId: "attempt-1",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    terminalName: "Lane 1",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    amountCents: 1_000,
    provider: "square",
    attemptState: input.attemptState,
    orderState: input.orderState ?? "Draft",
    isParked: true,
    status: input.status,
    transactionReference: null,
    receiptReference: null,
    lines: [{ id: "line-1", name: "Tea", quantity: "1", amountCents: 1_000 }],
    events: [],
  };
}

function recoveryAttempt(state: PaymentAttempt["state"]): PaymentAttempt {
  return {
    attemptId: "attempt-1",
    idempotencyKey: "attempt-idempotency-1",
    orderGuid: "order-1",
    provider: "square",
    operation: "purchase",
    amount: aud(1_000),
    state,
    references: {
      checkoutId: "checkout-1",
      paymentId: "payment-1",
      sessionId: null,
      txnRef: null,
      rfn: null,
      voucherReservationToken: null,
    },
    createdAtIso: "2026-07-28T00:00:00.000Z",
    updatedAtIso: "2026-07-28T00:01:00.000Z",
    lastErrorCode: null,
  };
}

function testClock() {
  return {
    now: () => new Date("2026-07-28T00:02:00.000Z"),
    nowIso: () => "2026-07-28T00:02:00.000Z",
  };
}

function voucherReleaseBootstrap(
  available: boolean,
  release: Extract<
    VoucherApprovedPurchaseReleasePort,
    { status: "available" }
  >["release"],
  onProviderGet: () => void = () => undefined,
): PaymentProviderRuntimeBootstrapWithVoucherRelease {
  const base = bootstrap(() => undefined);
  return {
    ...base,
    providers: {
      ...base.providers,
      get(provider: PaymentProvider) {
        onProviderGet();
        return {
          provider,
          async submit(source: PaymentAttempt) {
            return {
              state: "Unknown" as const,
              references: source.references,
              receiptText: null,
              responseCode: "TEST_PROVIDER_CALLED",
            };
          },
          async recover(source: PaymentAttempt) {
            return this.submit(source);
          },
          async cancel(source: PaymentAttempt) {
            return this.submit(source);
          },
          async refund(source: PaymentAttempt) {
            return this.submit(source);
          },
        };
      },
      getAvailability(provider: PaymentProvider) {
        return provider === "linkly-cloud"
          ? {
              provider,
              available: false,
              blocker: "LINKLY_CONFIGURATION_MISSING",
            }
          : { provider, available: true, blocker: null };
      },
      listAvailability() {
        return [
          { provider: "square", available: true, blocker: null },
          {
            provider: "linkly-cloud",
            available: false,
            blocker: "LINKLY_CONFIGURATION_MISSING",
          },
          { provider: "voucher", available: true, blocker: null },
        ];
      },
      listAvailableProviders() {
        return ["square", "voucher"];
      },
    } as unknown as PaymentProviderRuntimeBootstrap["providers"],
    voucherApprovedPurchaseRelease: available
      ? {
          status: "available",
          release,
        }
      : {
          status: "unavailable",
          reason: "VOUCHER_CONFIGURATION_DISABLED",
        },
  };
}

function database(draft: PaymentCheckoutDraft | null = null): PosDatabase {
  const draftStore = {
    async assertPersisted() {},
    async findBlockingRecovery() {
      return null;
    },
    async readDraft() {
      return draft;
    },
  };
  return {
    paymentDraftRecovery: () => draftStore,
    paymentRecoveryCenter: () => ({
      async list() {
        return [];
      },
      async findCurrentCandidate() {
        return null;
      },
      async getExact() {
        return null;
      },
    }),
    manualPaymentOrderCommitter: () => ({
      async completeManualPaymentOrder() {
        throw new Error("not used");
      },
    }),
    paymentActionBindings: () => ({}),
    voucherPreparationStore: () => ({
      async prepare() {
        return "protected-context";
      },
      async bindToAttempt() {
        return null;
      },
    }),
    settings: () => ({
      async getReceiptPrinterSettings() {
        return {
          printEnabled: false,
          drawerEnabled: false,
          peripheralId: null,
          paper: "80mm",
          locale: "en",
          brandName: "Hot Bargain",
          storeName: "Store",
          address: "",
          phone: "",
          abn: "",
        };
      },
    }),
    paymentOrderCommitter: () => ({}),
    mixedPaymentOrderTruth: () => ({
      async getPaymentTruth() {
        return null;
      },
    }),
    mixedPaymentTenders: () => ({
      async appendCashTenderAtomically() {
        throw new Error("not used");
      },
      async reverseTender() {
        throw new Error("not used");
      },
    }),
    voucherTenderReversals: () => ({
      async findBlocking() {
        return null;
      },
      async prepareOrLoad() {
        throw new Error("not used");
      },
      async markSubmitted() {
        throw new Error("not used");
      },
      async markUnknown() {
        throw new Error("not used");
      },
      async markBlocked() {
        throw new Error("not used");
      },
      async commitReleased() {
        throw new Error("not used");
      },
    }),
    returnCapacityVault: () => ({
      async get() {
        return null;
      },
      async resolveProtectedContext() {
        return null;
      },
    }),
  } as unknown as PosDatabase;
}

function durableCloseDatabase(input: Readonly<{
  draft: PaymentCheckoutDraft;
  recovery: PaymentDraftRecovery;
  onCommitted(): void;
}>): Readonly<{
  database: PosDatabase;
  readonly abandonPreparedCalls: number;
  readonly closeFullyReversedCalls: number;
  readonly readAfterCommit: number;
}> {
  let committed = false;
  let abandonPreparedCalls = 0;
  let closeFullyReversedCalls = 0;
  let readAfterCommit = 0;
  const close = () => {
    committed = true;
    input.onCommitted();
    return { replayed: false };
  };
  const draftStore = {
    async assertPersisted() {},
    async findBlockingRecovery() {
      return input.recovery;
    },
    async readDraft() {
      if (committed) {
        readAfterCommit += 1;
        throw new Error("draft must not be read after durable close");
      }
      return input.draft;
    },
    async abandonPreparedDraft() {
      abandonPreparedCalls += 1;
      return close();
    },
    async closeFullyReversedDraft() {
      closeFullyReversedCalls += 1;
      return close();
    },
  };
  return {
    database: {
      ...database(input.draft),
      paymentDraftRecovery: () => draftStore,
    } as unknown as PosDatabase,
    get abandonPreparedCalls() {
      return abandonPreparedCalls;
    },
    get closeFullyReversedCalls() {
      return closeFullyReversedCalls;
    },
    get readAfterCommit() {
      return readAfterCommit;
    },
  };
}

function repositories(
  initialAttempt: PaymentAttempt | null = null,
  onWrite: () => void = () => undefined,
): PosRepositoryBundle {
  const attempts = new Map<string, PaymentAttempt>();
  if (initialAttempt) {
    attempts.set(initialAttempt.attemptId, initialAttempt);
  }
  return {
    orders: {
      async nextLocalSequence() {
        return 1;
      },
      async getByGuid() {
        return null;
      },
      async listLocal() {
        return [];
      },
    },
    payments: {
      async insertIfUnblocked(attempt: PaymentAttempt) {
        onWrite();
        attempts.set(attempt.attemptId, attempt);
        return null;
      },
      async compareAndUpdate(
        _expected: PaymentAttempt,
        next: PaymentAttempt,
      ) {
        onWrite();
        attempts.set(next.attemptId, next);
        return true;
      },
      async get(attemptId: string) {
        return attempts.get(attemptId) ?? null;
      },
      async findBlocking(orderGuid: string) {
        return (
          [...attempts.values()].find(
            (attempt) =>
              attempt.orderGuid === orderGuid &&
              attempt.state !== "Approved" &&
              attempt.state !== "Declined" &&
              attempt.state !== "Cancelled",
          ) ?? null
        );
      },
    } satisfies PaymentAttemptRepositoryPort,
  } as unknown as PosRepositoryBundle;
}

function productionAcknowledgementHarness(input: Readonly<{
  state: Extract<PaymentAttempt["state"], "Approved" | "Declined" | "Cancelled">;
  legacy?: boolean;
  listedSessionId?: string;
  listedEnvironment?: string;
  reconciledEnvironment?: string;
}>) {
  let attempt: PaymentAttempt = {
    attemptId: "linkly-final-attempt",
    idempotencyKey: "linkly-final-idempotency",
    orderGuid: "linkly-final-order",
    provider: "linkly-cloud",
    ...(input.legacy ? {} : { providerEnvironment: "Sandbox" }),
    operation: "purchase",
    amount: aud(1_000),
    state: input.state,
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: "legacy-session-1",
      txnRef: null,
      rfn: null,
      voucherReservationToken: null,
    },
    createdAtIso: "2026-09-09T00:00:00.000Z",
    updatedAtIso: "2026-09-09T00:00:01.000Z",
    lastErrorCode: null,
  };
  let acknowledged = 0;
  let financial = 0;
  let reconcile = 0;
  let legacyList = 0;
  let reconcileThisBound = false;
  const provider: {
    environment: string;
    acknowledge(): Promise<void>;
    listUnacknowledgedSessions(): Promise<readonly Readonly<{
      sessionId: string;
      environment: string;
    }>[]>;
    reconcileLegacy(this: { environment: string }): Promise<Readonly<{
      environment: string;
      clientAcknowledgedAt: null;
    }>>;
    submit(source: PaymentAttempt): Promise<unknown>;
    recover(source: PaymentAttempt): Promise<unknown>;
    cancel(source: PaymentAttempt): Promise<unknown>;
    refund(source: PaymentAttempt): Promise<unknown>;
  } = {
    environment: input.reconciledEnvironment ?? "Sandbox",
    async acknowledge() {
      acknowledged += 1;
    },
    async listUnacknowledgedSessions() {
      legacyList += 1;
      return input.legacy
          ? [{
            sessionId: input.listedSessionId ?? "legacy-session-1",
            environment: input.listedEnvironment ?? "Sandbox",
            idempotencyKey: attempt.idempotencyKey,
          }]
        : [];
    },
    async reconcileLegacy(this: { environment: string }) {
      reconcile += 1;
      reconcileThisBound = this === provider;
      return {
        environment: this.environment,
        clientAcknowledgedAt: null,
      };
    },
    async submit(source: PaymentAttempt) {
      financial += 1;
      return { state: "Unknown" as const, references: source.references, receiptText: null, responseCode: null };
    },
    async recover(source: PaymentAttempt) {
      financial += 1;
      return { state: "Unknown" as const, references: source.references, receiptText: null, responseCode: null };
    },
    async cancel(source: PaymentAttempt) {
      financial += 1;
      return { state: "Unknown" as const, references: source.references, receiptText: null, responseCode: null };
    },
    async refund(source: PaymentAttempt) {
      financial += 1;
      return { state: "Unknown" as const, references: source.references, receiptText: null, responseCode: null };
    },
  };
  const draftStore = {
    async assertPersisted() {},
    async findBlockingRecovery() { return null; },
    async hasLegacyLinklyRecovery() { return input.legacy === true; },
    async readDraft() { return null; },
    async findPendingLinklyAcknowledgement() {
      return attempt.providerEnvironment && !attempt.providerAcknowledgedAtIso
        ? attempt.attemptId
        : null;
    },
    async findLegacyLinklyAttemptForSession(
      _scope: unknown,
      sessionId: string,
    ) {
      return input.legacy && sessionId === attempt.references.sessionId
        ? attempt.attemptId
        : null;
    },
  };
  const baseDatabase = database();
  const paymentLedger = {
    async insertIfUnblocked() { return null; },
    async compareAndUpdate() { return true; },
    async get(attemptId: string) {
      return attemptId === attempt.attemptId ? attempt : null;
    },
    async findBlocking() { return null; },
    async canProviderAcknowledged() { return true; },
    async markProviderAcknowledged(
      expected: PaymentAttempt,
      providerAcknowledgedAtIso: string,
    ) {
      if (expected.attemptId !== attempt.attemptId ||
          attempt.providerAcknowledgedAtIso ||
          !attempt.providerEnvironment) return false;
      attempt = { ...attempt, providerAcknowledgedAtIso };
      return true;
    },
    async verifyProviderEnvironment(
      expected: PaymentAttempt,
      providerEnvironment: string,
    ) {
      if (expected.attemptId !== attempt.attemptId ||
          attempt.providerEnvironment !== undefined ||
          providerEnvironment !== provider.environment) return false;
      attempt = { ...attempt, providerEnvironment };
      return true;
    },
  };
  const runtime = createProductionPaymentRuntime({
    database: {
      ...baseDatabase,
      paymentDraftRecovery: () => draftStore,
    } as unknown as PosDatabase,
    repositories: {
      orders: {
        async nextLocalSequence() { return 1; },
        async getByGuid(orderGuid: string) {
          return orderGuid === attempt.orderGuid
            ? {
                orderGuid,
                storeCode: "S1",
                deviceCode: "IPAD-1",
                state: "Synced",
                actualAmount: aud(1_000),
              }
            : null;
        },
        async listLocal() { return []; },
      },
      payments: paymentLedger,
    } as unknown as PosRepositoryBundle,
    encryptor,
    activeCart: new ActivePricingCartSession(
      new PricingCart(),
      () => new PricingCart(),
    ),
    currentCashier: activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    clock: {
      now: () => new Date("2026-09-09T00:00:00.000Z"),
      nowIso: () => "2026-09-09T00:00:02.000Z",
    },
    createId: idFactory(),
    connectivity: { async isOnline() { return true; } },
    bootstrap: {
      providers: {
        get() { return provider; },
        getAvailability(providerName: PaymentProvider) {
          return { provider: providerName, available: providerName === "linkly-cloud", blocker: null };
        },
        listAvailability() { return []; },
        listAvailableProviders() { return ["linkly-cloud"]; },
      } as unknown as PaymentProviderRuntimeBootstrap["providers"],
      configurationAvailability: {} as PaymentProviderRuntimeBootstrap["configurationAvailability"],
      linklyTerminals: { environment: "Sandbox", port: {} as never },
      bindVoucherContextProvider() {},
      createLinklyOperator() { return null; },
    },
    async drainFulfilment() {},
  });
  return {
    runtime,
    current: () => attempt,
    reconcileThisBound: () => reconcileThisBound,
    counts: () => ({ acknowledged, financial, reconcile }),
    legacyListCount: () => legacyList,
  };
}

const encryptor: SensitivePayloadEncryptor = {
  async encrypt(value) {
    return new TextEncoder().encode(value);
  },
  async decrypt(value) {
    return new TextDecoder().decode(value);
  },
};

function idFactory(): () => string {
  let value = 0;
  return () => `id-${++value}`;
}

function activeCashier(
  permissionCodes: readonly string[] = ALL_PAYMENT_PERMISSIONS,
): CurrentCashierSession {
  const cashier = new CurrentCashierSession();
  const epoch = cashier.beginAuthentication();
  cashier.activate(
    epoch,
    {
      source: "online",
      session: {
        cashierId: "cashier-1",
        cashierName: "Cashier",
        storeCode: "S1",
        deviceCode: "IPAD-1",
        permissionCodes: [...permissionCodes],
      },
    },
    { storeCode: "S1", deviceCode: "IPAD-1" },
  );
  return cashier;
}

function checkoutDraft(): PaymentCheckoutDraft {
  return {
    checkoutIntentId: "checkout-1",
    orderGuid: "order-1",
    cartRevision: 0,
    state: "Completing",
    total: aud(1_000),
    remaining: aud(400),
    cancellableAfterReversal: false,
    tenders: [
      checkoutTender("cash-1", "cash", 100),
      checkoutTender("card-1", "card", 200),
      checkoutTender("voucher-1", "voucher", 300),
    ],
  };
}

function checkoutTender(
  tenderGuid: string,
  method: OrderTender["method"],
  cents: number,
) {
  return {
    tenderGuid,
    method,
    amount: aud(cents),
    reversible: true,
  };
}

function mixedTruth(): MixedPaymentOrderTruth {
  return {
    orderGuid: "order-1",
    state: "Completing",
    actualAmount: aud(1_000),
    tenders: [
      orderTender("cash-1", "cash", 100),
      orderTender("card-1", "card", 200),
      orderTender("voucher-1", "voucher", 300),
    ],
    reversalLinks: [],
  };
}

function orderTender(
  tenderGuid: string,
  method: OrderTender["method"],
  cents: number,
): OrderTender {
  return {
    tenderGuid,
    method,
    amount: aud(cents),
    reference:
      method === "card" ? "provider-reference-secret" : null,
    reservationToken:
      method === "voucher" ? "reservation-secret" : null,
  };
}

function reversedMutation(
  truth: MixedPaymentOrderTruth,
  actionId: string,
  sourceTenderGuid: string,
) {
  const source = truth.tenders.find(
    (tender) => tender.tenderGuid === sourceTenderGuid,
  );
  assert.ok(source);
  const reversalTenderGuid = `reversal-${actionId}`;
  return {
    state: "reversed" as const,
    replayed: false,
    reversalTenderGuid,
    truth: {
      ...truth,
      tenders: [
        ...truth.tenders,
        {
          ...source,
          tenderGuid: reversalTenderGuid,
          amount: aud(-source.amount.cents),
          reference: null,
          reservationToken: null,
        },
      ],
      reversalLinks: [
        ...truth.reversalLinks,
        {
          actionId,
          sourceTenderGuid,
          reversalTenderGuid,
        },
      ],
    },
  };
}

function aud(cents: number): Money {
  return { currency: "AUD", cents };
}

function approvedVoucherAttempt(): PaymentAttempt {
  return {
    attemptId: "voucher-attempt-1",
    idempotencyKey: "voucher-idempotency-1",
    orderGuid: "order-1",
    provider: "voucher",
    operation: "purchase",
    amount: aud(500),
    state: "Approved",
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: null,
      txnRef: null,
      rfn: null,
      voucherReservationToken: "reservation-secret",
    },
    createdAtIso: "2026-07-28T00:00:00.000Z",
    updatedAtIso: "2026-07-28T00:01:00.000Z",
    lastErrorCode: null,
  };
}

function pricedCart(): PricingCart {
  const cart = new PricingCart();
  cart.addItem({
    lineId: "line-1",
    productCode: "P1",
    itemNumber: "1001",
    lookupCode: "930000000001",
    displayName: "Tea",
    unitPrice: aud(1_000),
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  return cart;
}

function voucherCheckoutDraft(
  cartRevision: number,
): PaymentCheckoutDraft {
  return {
    checkoutIntentId: "checkout-1",
    orderGuid: "order-1",
    cartRevision,
    state: "Completing",
    total: aud(1_000),
    remaining: aud(500),
    cancellableAfterReversal: false,
    tenders: [checkoutTender("voucher-1", "voucher", 500)],
  };
}

function voucherOnlyTruth(): MixedPaymentOrderTruth {
  return {
    orderGuid: "order-1",
    state: "Completing",
    actualAmount: aud(1_000),
    tenders: [orderTender("voucher-1", "voucher", 500)],
    reversalLinks: [],
  };
}

function draftRecovery(cart: PricingCart): PaymentDraftRecovery {
  return {
    kind: "DraftPrepared",
    attemptId: null,
    draftId: "checkout-1",
    orderGuid: "order-1",
    originalOrderGuid: null,
    localSequence: 1,
    soldAtIso: "2026-07-28T00:00:00.000Z",
    identity: {
      storeCode: "S1",
      deviceCode: "IPAD-1",
      cashierId: "cashier-1",
      cashierName: "Cashier",
    },
    cart: cart.snapshot(),
    pricingState: cart.stateSnapshot(),
    recallBinding: null,
    boundAction: null,
  };
}

function voucherReversalRecord(
  truth: MixedPaymentOrderTruth,
): VoucherTenderReversalRecord {
  return {
    actionId: "id-2",
    orderGuid: "order-1",
    sourceTenderGuid: "voucher-1",
    sourceAttemptId: "voucher-attempt-1",
    amount: aud(500),
    reason: "SALE",
    state: "Prepared",
    attemptCount: 0,
    lastErrorCode: null,
    reversalTenderGuid: null,
    actor: TEST_AUDIT_ACTOR,
    truth,
  };
}

function reversalDatabase(input: Readonly<{
  draft: PaymentCheckoutDraft;
  recovery: PaymentDraftRecovery;
  record: VoucherTenderReversalRecord;
  persisted?: boolean;
  recoveryThrowsAfterFirst?: boolean;
  onCash(): void;
  onVoid?(command: unknown): void;
}>): PosDatabase {
  let current = input.record;
  // 主管作废后：Blocked 行保持不变，但已处置、不再阻塞，礼券被负 tender 抵消。
  let voided = false;
  let created =
    input.persisted ?? input.record.state !== "Prepared";
  let recoveryReads = 0;
  return {
    ...database(input.draft),
    paymentDraftRecovery: () => ({
      async assertPersisted() {},
      async findPendingLinklyAcknowledgement() {
        return null;
      },
      async findBlockingRecovery() {
        recoveryReads += 1;
        if (
          input.recoveryThrowsAfterFirst &&
          recoveryReads > 1
        ) {
          throw new Error(
            "ordinary payment recovery is intentionally ambiguous",
          );
        }
        return input.recovery;
      },
      async readDraft() {
        if (voided) {
          return {
            ...input.draft,
            remaining: input.draft.total,
            cancellableAfterReversal: true,
            tenders: [],
          };
        }
        return current.state === "Reversed"
          ? {
              ...input.draft,
              remaining: input.draft.total,
              tenders: [],
            }
          : input.draft;
      },
    }),
    paymentActionBindings: () => ({
      async bindOrGet(binding: unknown) {
        return binding;
      },
    }),
    mixedPaymentOrderTruth: () => ({
      async getPaymentTruth(orderGuid: string) {
        return orderGuid === current.orderGuid
          ? current.truth
          : null;
      },
    }),
    mixedPaymentTenders: () => ({
      async appendCashTenderAtomically() {
        input.onCash();
        throw new Error("cash must remain blocked");
      },
      async reverseTender() {
        throw new Error("cash reversal is not used");
      },
    }),
    voucherTenderReversals: () => ({
      async findBlocking(scope: Readonly<{
        storeCode: string;
        deviceCode: string;
      }>) {
        if (
          scope.storeCode !== "S1" ||
          scope.deviceCode !== "IPAD-1" ||
          !created ||
          current.state === "Reversed" ||
          voided
        ) {
          return null;
        }
        return current;
      },
      async voidBlockedUnreleased(command: Readonly<{
        actionId: string;
        orderGuid: string;
        sourceTenderGuid: string;
      }>) {
        input.onVoid?.(command);
        if (
          current.state !== "Blocked" ||
          command.actionId !== current.actionId ||
          command.orderGuid !== current.orderGuid ||
          command.sourceTenderGuid !== current.sourceTenderGuid
        ) {
          throw new Error("only the persisted blocked action can be voided");
        }
        if (voided) {
          return {
            actionId: current.actionId,
            orderGuid: current.orderGuid,
            sourceTenderGuid: current.sourceTenderGuid,
            reversalTenderGuid: `reversal-${current.actionId}`,
            replayed: true,
            truth: current.truth,
          };
        }
        const reversed = reversedMutation(
          current.truth,
          current.actionId,
          current.sourceTenderGuid,
        );
        const replayed = false;
        voided = true;
        current = { ...current, truth: reversed.truth };
        return {
          actionId: current.actionId,
          orderGuid: current.orderGuid,
          sourceTenderGuid: current.sourceTenderGuid,
          reversalTenderGuid: reversed.reversalTenderGuid,
          replayed,
          truth: reversed.truth,
        };
      },
      async prepareOrLoad(command: Readonly<{
        actionId: string;
        orderGuid: string;
        sourceTenderGuid: string;
        reason: VoucherTenderReversalReason;
      }>) {
        created = true;
        if (
          command.actionId !== current.actionId ||
          command.orderGuid !== current.orderGuid ||
          command.sourceTenderGuid !== current.sourceTenderGuid ||
          command.reason !== current.reason
        ) {
          throw new Error("persisted reversal action changed");
        }
        return current;
      },
      async markSubmitted(record: VoucherTenderReversalRecord) {
        current = {
          ...record,
          state: "Submitted",
          attemptCount: record.attemptCount + 1,
        };
        return current;
      },
      async markUnknown(
        record: VoucherTenderReversalRecord,
        errorCode: string,
      ) {
        current = {
          ...record,
          state: "Unknown",
          lastErrorCode: errorCode,
        };
        return current;
      },
      async markBlocked(
        record: VoucherTenderReversalRecord,
        errorCode: string,
      ) {
        current = {
          ...record,
          state: "Blocked",
          lastErrorCode: errorCode,
        };
        return current;
      },
      async commitReleased(record: VoucherTenderReversalRecord) {
        const reversed = reversedMutation(
          record.truth,
          record.actionId,
          record.sourceTenderGuid,
        );
        current = {
          ...record,
          state: "Reversed",
          lastErrorCode: null,
          reversalTenderGuid: reversed.reversalTenderGuid,
          truth: reversed.truth,
        };
        return current;
      },
    }),
  } as unknown as PosDatabase;
}
