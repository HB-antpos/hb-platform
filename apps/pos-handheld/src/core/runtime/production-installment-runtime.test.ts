import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";

import { CurrentCashierSession } from "./current-cashier-session";
import {
  ProductionInstallmentPaymentAdapter,
  type InstallmentApprovedPaymentMaterial,
  type InstallmentCashSettlement,
  type InstallmentProviderAttemptPlan,
  type InstallmentProviderAttemptRecord,
  type InstallmentProviderAttemptStorePort,
  type InstallmentVoucherIntentVaultPort,
} from "./production-installment-payment-adapter";
import {
  createProductionInstallmentRuntime,
  type InstallmentActionState,
  type InstallmentActionStorePort,
  type InstallmentApprovedRefund,
  type InstallmentPerformanceEvent,
  type InstallmentPaymentAction,
  type InstallmentMutationPaymentPort,
  type InstallmentReceiptReprintRuntimePort,
  type PersistedInstallmentAction,
  type PersistedInstallmentLifecycleAction,
  type ProductionInstallmentRuntimeDependencies,
} from "./production-installment-runtime";

import { HbposApiError } from "@/core/api/hbpos-api";
import type { InstallmentSnapshot, PaymentAttempt } from "@/core/contracts";
import type { CashierLoginResult } from "@/core/security/cashier-authentication";
import {
  INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
  INSTALLMENTS_CANCEL_PERMISSION,
  INSTALLMENTS_CONFIRM_PICKUP_PERMISSION,
  INSTALLMENTS_CREATE_PERMISSION,
  INSTALLMENTS_REPRINT_PERMISSION,
  INSTALLMENTS_VIEW_PERMISSION,
} from "@hb/pos-domain/features/installments/installment-authorization";
import type {
  InstallmentCreateCommand,
  InstallmentCancelCommand,
  InstallmentCancelClaim,
  InstallmentCancelClaimCommitCommand,
  InstallmentCancelClaimCreateCommand,
  InstallmentCancelClaimIdentity,
  InstallmentCancelClaimResolveCommand,
  InstallmentDetails,
  InstallmentPaymentCommand,
  InstallmentRepaymentCapabilities,
  InstallmentRepaymentClaim,
  InstallmentRepaymentClaimBeginProviderCommand,
  InstallmentRepaymentClaimPrepareProviderCommand,
  InstallmentRepaymentClaimCommitCommand,
  InstallmentRepaymentClaimCreateCommand,
  InstallmentRepaymentClaimIdentity,
  InstallmentRepaymentClaimResolveCommand,
  InstallmentsRemotePort,
} from "@/features/installments/installment-models";
import {
  InstallmentWorkflowError,
  type InstallmentPresenter,
  type InstallmentWorkflowPort,
} from "@/features/installments/installment-presenter";
import { PAYMENT_PERMISSION } from "@/features/payments/runtime/payment-checkout-runtime";

const STORE_CODE = "STORE-1";
const DEVICE_CODE = "IPAD-1";
const ALL_PERMISSIONS = Object.freeze([
  INSTALLMENTS_VIEW_PERMISSION,
  INSTALLMENTS_CREATE_PERMISSION,
  INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
  INSTALLMENTS_CANCEL_PERMISSION,
  INSTALLMENTS_CONFIRM_PICKUP_PERMISSION,
  INSTALLMENTS_REPRINT_PERMISSION,
  PAYMENT_PERMISSION.view,
  PAYMENT_PERMISSION.takeCash,
  PAYMENT_PERMISSION.takeCard,
  PAYMENT_PERMISSION.takeVoucher,
  PAYMENT_PERMISSION.confirm,
]);

test("公开服务提供管理、统一支付与恢复工厂，在线列表不读写旧门店快照", async () => {
  const harness = createHarness();

  assert.deepEqual(Object.keys(harness.runtime), [
    "createPresenter",
    "prepareCreateCheckout",
    "createCheckoutPresenter",
    "hasRecoveryRequired",
  ]);
  const presenter = harness.runtime.createPresenter();
  await presenter.load();

  assert.equal(harness.api.listCalls, 1);
  assert.equal(harness.cache.upsertCalls.length, 0);
  assert.equal(harness.cache.listCalls.length, 0);
  assert.equal(presenter.getState().orders[0]?.installmentGuid, "installment-1");
});

test("服务端 capability 快照仅放行同店跨机 Active 续付，同机续付默认保持可用", async () => {
  const sameDevice = createHarness();
  const sameDevicePresenter = sameDevice.runtime.createPresenter();
  await sameDevicePresenter.load();
  await sameDevicePresenter.select("installment-1");
  assert.equal(
    sameDevicePresenter.capabilities.selectedDetailsRepayable,
    true,
  );

  const disabled = createHarness();
  disabled.api.detailsResponse = details({ deviceCode: "IPAD-2" });
  const disabledPresenter = disabled.runtime.createPresenter();
  await disabledPresenter.load();
  await disabledPresenter.select("installment-1");
  assert.equal(
    disabledPresenter.capabilities.selectedDetailsRepayable,
    false,
  );

  const enabled = createHarness({ crossDeviceRepaymentEnabled: true });
  enabled.api.detailsResponse = details({ deviceCode: "IPAD-2" });
  const enabledPresenter = enabled.runtime.createPresenter();
  await enabledPresenter.load();
  await enabledPresenter.select("installment-1");
  assert.equal(
    enabledPresenter.capabilities.selectedDetailsRepayable,
    true,
  );
  assert.equal(enabledPresenter.capabilities.selectedDetailsWritable, false);

  const crossStore = createHarness({ crossDeviceRepaymentEnabled: true });
  crossStore.api.detailsResponse = details({
    storeCode: "STORE-OTHER",
    deviceCode: "IPAD-2",
  });
  const crossStorePresenter = crossStore.runtime.createPresenter();
  await crossStorePresenter.load();
  await crossStorePresenter.select("installment-1");
  assert.equal(
    crossStorePresenter.capabilities.selectedDetailsRepayable,
    false,
  );
});

test("非现金还款严格按 durable action、claim、provider、commit、本地完成排序", async () => {
  const cases = [
    { method: "voucher" as const },
    { method: "card" as const, cardProvider: "square" as const },
    { method: "card" as const, cardProvider: "linkly-cloud" as const },
  ];

  for (const entry of cases) {
    const harness = createHarness();
    const presenter = harness.runtime.createPresenter();
    const workflow = workflowOf(presenter);
    await workflow.addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: entry.method,
      voucherReference: entry.method === "voucher" ? "VOUCHER-1" : null,
      voucherReservationToken: null,
      ...(entry.cardProvider ? { cardProvider: entry.cardProvider } : {}),
    });

    assertOrdered(harness.events, [
      "action-create",
      "claim-create",
      "payments-prepare",
      "claim-begin",
      "payments-begin",
      "claim-commit",
      "action-complete",
    ]);
    assert.equal(harness.api.appendCalls, 0);
  }
});

test("addRepayment 现金在任何 action、plan 或 claim 产生前失败关闭", async () => {
  const paymentStore: { current: RuntimeCashAttemptStore | null } = {
    current: null,
  };
  const harness = createHarness({
    paymentsFactory: (actionStore, events) => {
      paymentStore.current = new RuntimeCashAttemptStore(actionStore, events);
      return new ProductionInstallmentPaymentAdapter({
        store: paymentStore.current,
        providers: { get: () => { throw new Error("cash must not select an online provider"); } },
        cardProviderSelection: { loadEnabledProviders: async () => [] },
        provenance: {
          resolveOrImport: async () => {
            throw new Error("cash repayment must not request refund provenance");
          },
          seedRefundAttempt: async () => {
            throw new Error("cash repayment must not seed a refund attempt");
          },
        },
        voucherMaterials: {
          prepare: async () => {
            throw new Error("cash repayment must not prepare voucher material");
          },
          resolveApproved: async () => {
            throw new Error("cash repayment must not resolve voucher material");
          },
        },
        createId: (() => {
          let next = 0;
          return () => `90000000-0000-4000-8000-${String(++next).padStart(12, "0")}`;
        })(),
        nowIso: () => "2026-08-04T01:00:00.000Z",
      });
    },
  });

  await assert.rejects(
    workflowOf(harness.runtime.createPresenter()).addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash",
      voucherReference: null,
      voucherReservationToken: null,
    }),
    (error) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "cash-confirmation-required",
  );

  assert.ok(paymentStore.current);
  assert.equal(paymentStore.current.planBindings, 0);
  assert.equal(paymentStore.current.cashApprovals, 0);
  assert.equal(harness.actionStore.getCurrent(), null);
  assert.equal(harness.api.createClaimCalls.length, 0);
  assert.equal(harness.api.prepareProviderCalls.length, 0);
  assert.equal(harness.api.beginClaimCalls.length, 0);
  assert.equal(harness.api.commitClaimCalls.length, 0);
  assert.deepEqual(harness.events, []);
});

test("executeRepaymentClaimAction 默认不允许遗漏调用隐式批准现金", async () => {
  const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
  const workflow = workflowOf(harness.runtime.createPresenter());
  await workflow.prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
  });
  const persisted = harness.actionStore.getCurrent();
  assert.ok(persisted);
  const getClaimCalls = harness.api.getClaimCalls.length;
  const prepareCalls = harness.payments.prepareCalls;

  await assert.rejects(
    executeRepaymentClaimActionForTest(workflow, persisted),
    (error) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "cash-confirmation-required",
  );

  assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");
  assert.equal(harness.api.getClaimCalls.length, getClaimCalls);
  assert.equal(harness.payments.prepareCalls, prepareCalls);
  assert.equal(harness.payments.confirmCalls, 0);
  assert.equal(harness.payments.authorizeCalls, 0);
  assert.equal(harness.payments.recoverCalls, 0);
  assert.equal(harness.api.commitClaimCalls.length, 0);
});

test("claim BUSY 在 provider plan 与授权前停止，且不接管远端 Pending", async () => {
  const harness = createHarness({
    claimCreateErrorCode: "INSTALLMENT_REPAYMENT_BUSY",
  });
  const presenter = harness.runtime.createPresenter();
  await presenter.load();
  await presenter.select("installment-1");
  fillVoucherRepayment(presenter);

  await presenter.addRepayment();

  assert.equal(presenter.getState().statusCode, "conflict");
  assert.equal(harness.payments.prepareCalls, 0);
  assert.equal(harness.payments.authorizeCalls, 0);
  assert.equal(harness.api.beginClaimCalls.length, 0);
  assert.equal(harness.api.resolveClaimCalls.length, 0);
  assert.equal(harness.api.appendCalls, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
  assert.deepEqual(harness.actionStore.finalizedCreatedReasons, ["ClaimBusy"]);
});

test("Card capability false 在 action ledger 前失败关闭", async () => {
  const harness = createHarness({ cardRepaymentSupported: false });

  await assert.rejects(
    workflowOf(harness.runtime.createPresenter()).addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "card",
      cardProvider: "square",
      voucherReference: null,
      voucherReservationToken: null,
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "conflict",
  );

  assert.equal(harness.actionStore.createdCandidates.length, 0);
  assert.equal(harness.api.createClaimCalls.length, 0);
  assert.equal(harness.payments.beginCalls, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("旧 capability 误放行 Card 时仅精确 unsupported 400 原子终结，随后券仍可继续", async () => {
  for (const method of ["voucher"] as const) {
    const harness = createHarness({
      cardRepaymentSupported: true,
      claimCreateErrorCode:
        "INSTALLMENT_REPAYMENT_PAYMENT_METHOD_UNSUPPORTED",
      claimCreateErrorStatus: 400,
    });
    const workflow = workflowOf(harness.runtime.createPresenter());

    await assert.rejects(
      workflow.addRepayment({
        installmentGuid: "installment-1",
        amountCents: 1_000,
        method: "card",
        cardProvider: "square",
        voucherReference: null,
        voucherReservationToken: null,
      }),
      (error) =>
        error instanceof Error &&
        "code" in error &&
        error.code === "conflict",
    );

    assert.deepEqual(harness.actionStore.finalizedCreatedReasons, [
      "PaymentMethodUnsupported",
    ]);
    assert.equal(harness.payments.prepareCalls, 0);
    assert.equal(harness.payments.beginCalls, 0);
    assert.equal(await harness.runtime.hasRecoveryRequired(), false);

    harness.api.claimCreateErrorCode = null;
    await workflow.addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method,
      voucherReference: method === "voucher" ? "VOUCHER-1" : null,
      voucherReservationToken: null,
    });
    assert.equal(harness.payments.beginCalls, 1);
  }
});

test("任意其他 400 不得被泛化为 Card unsupported 终结", async () => {
  const harness = createHarness({
    cardRepaymentSupported: true,
    claimCreateErrorCode: "INSTALLMENT_REPAYMENT_CLAIM_INVALID",
    claimCreateErrorStatus: 400,
  });

  await assert.rejects(
    workflowOf(harness.runtime.createPresenter()).addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "card",
      cardProvider: "square",
      voucherReference: null,
      voucherReservationToken: null,
    }),
  );

  assert.deepEqual(harness.actionStore.finalizedCreatedReasons, []);
  assert.equal(harness.payments.prepareCalls, 0);
  assert.equal(harness.payments.beginCalls, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), true);
});

test("claim MISMATCH 原子终结 Created 并返回 requires-review，不调用 provider/resolve/append", async () => {
  const harness = createHarness({
    claimCreateErrorCode: "INSTALLMENT_REPAYMENT_CLAIM_MISMATCH",
  });
  const workflow = workflowOf(harness.runtime.createPresenter());

  await assert.rejects(
    workflow.addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "card",
      cardProvider: "square",
      voucherReference: null,
      voucherReservationToken: null,
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "claim-review-required",
  );

  assert.deepEqual(harness.actionStore.finalizedCreatedReasons, [
    "ClaimMismatch",
  ]);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
  assert.equal(harness.payments.prepareCalls, 0);
  assert.equal(harness.payments.authorizeCalls, 0);
  assert.equal(harness.api.resolveClaimCalls.length, 0);
  assert.equal(harness.api.appendCalls, 0);
});

test("Created 首次 GET 返回 claim MISMATCH 时原子终结 review，绝不 create/provider", async () => {
  const harness = createHarness({
    claimGetErrorCode: "INSTALLMENT_REPAYMENT_CLAIM_MISMATCH",
  });
  const workflow = workflowOf(harness.runtime.createPresenter());

  await assert.rejects(
    workflow.addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "card",
      cardProvider: "square",
      voucherReference: null,
      voucherReservationToken: null,
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "claim-review-required",
  );

  assert.deepEqual(harness.actionStore.finalizedCreatedReasons, [
    "ClaimMismatch",
  ]);
  assert.equal(harness.api.createClaimCalls.length, 0);
  assert.equal(harness.payments.prepareCalls, 0);
  assert.equal(harness.payments.authorizeCalls, 0);
  assert.equal(harness.api.appendCalls, 0);
});

test("claim unsupported、capability 失败或 begin 失败一律关闭且绝不回退 legacy append", async () => {
  const scenarios = [
    createHarness({ repaymentClaimsSupported: false }),
    createHarness({ capabilityErrorStatus: 503 }),
    createHarness({
      claimBeginErrorCode: "INSTALLMENT_REPAYMENT_CLAIM_MISMATCH",
    }),
  ];

  for (const harness of scenarios) {
    const workflow = workflowOf(harness.runtime.createPresenter());
    await assert.rejects(
      workflow.addRepayment({
        installmentGuid: "installment-1",
        amountCents: 1_000,
        method: "card",
        cardProvider: "square",
        voucherReference: null,
        voucherReservationToken: null,
      }),
    );
    assert.equal(harness.payments.authorizeCalls, 0);
    assert.equal(harness.api.appendCalls, 0);
    assert.equal(harness.api.commitClaimCalls.length, 0);
  }
});

test("claims supported 且 required=false 的 rollout 阶段仍走新 claim 同机续付", async () => {
  const harness = createHarness({ repaymentClaimsRequired: false });
  const workflow = workflowOf(harness.runtime.createPresenter());

  await workflow.addRepayment({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "card",
    cardProvider: "square",
    voucherReference: null,
    voucherReservationToken: null,
  });

  assert.equal(harness.api.createClaimCalls.length, 1);
  assert.equal(harness.api.commitClaimCalls.length, 1);
  assert.equal(harness.api.appendCalls, 0);
});

test("claim create 网络/5xx 结果不确定时保留 Created，恢复先 GET 再用同 operation create", async () => {
  for (const failure of ["transport", "server"] as const) {
    const harness = createHarness({ claimCreateFailsOnce: failure });
    const workflow = workflowOf(harness.runtime.createPresenter());
    const input = {
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash" as const,
      voucherReference: null,
      voucherReservationToken: null,
    };

    await assert.rejects(workflow.prepareCashRepayment!(input));
    const operationGuid =
      harness.actionStore.getCurrent()?.action.actionId;
    assert.ok(operationGuid);
    assert.equal(harness.actionStore.getCurrent()?.state, "Created");
    assert.equal(harness.payments.prepareCalls, 1);
    assert.equal(harness.payments.authorizeCalls, 0);
    assert.equal(harness.api.appendCalls, 0);

    await assert.rejects(
      workflow.recoverBlocking(),
      (error) =>
        error instanceof InstallmentWorkflowError &&
        error.code === "cash-confirmation-required",
    );
    assert.equal(
      (await workflow.inspectPreparedCashRepayment!())?.amountCents,
      1_000,
    );
    assert.equal(harness.payments.authorizeCalls, 0);
    await workflow.confirmPreparedCashRepayment!();

    assert.equal(harness.api.createClaimCalls.length, 2);
    assert.equal(
      harness.api.createClaimCalls[0]?.operationGuid,
      operationGuid,
    );
    assert.equal(
      harness.api.createClaimCalls[1]?.operationGuid,
      operationGuid,
    );
    assert.equal(harness.payments.authorizeCalls, 0);
    assert.equal(harness.payments.recoverCalls, 0);
    assert.equal(await harness.runtime.hasRecoveryRequired(), false);
  }
});

test("健康现金续付使用 prepare-provider 两请求路径，确认前不收现金且只 commit 一次", async () => {
  const harness = createHarness({
    capturePerformance: true,
    repaymentClaimPrepareProviderV1: true,
    useAtomicFinalizer: true,
  });
  const workflow = workflowOf(harness.runtime.createPresenter());
  const prepared = await workflow.prepareCashRepayment?.({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
    cashTenderedCents: 1_000,
  });

  assert.equal(prepared?.amountCents, 1_000);
  assert.equal(harness.api.prepareProviderCalls.length, 1);
  assert.equal(harness.api.createClaimCalls.length, 0);
  assert.equal(harness.api.beginClaimCalls.length, 0);
  assert.equal(harness.payments.confirmCalls, 0);
  assert.equal(harness.payments.authorizeCalls, 0);
  assert.equal(harness.api.commitClaimCalls.length, 0);
  assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");

  const completed = await workflow.confirmPreparedCashRepayment?.();
  assert.equal(completed?.installmentGuid, "installment-1");
  assert.equal(harness.payments.confirmCalls, 1);
  assert.equal(harness.payments.authorizeCalls, 0);
  assert.equal(harness.payments.recoverCalls, 0);
  assert.equal(harness.api.commitClaimCalls.length, 1);
  assert.equal(harness.api.commitClaimCalls[0]?.operationGuid,
    harness.api.prepareProviderCalls[0]?.operationGuid);
  assert.equal(harness.api.claim?.paymentGuid,
    harness.api.prepareProviderCalls[0]?.paymentGuid);
  assert.equal(harness.api.claim?.providerAttemptId,
    harness.api.prepareProviderCalls[0]?.providerAttemptId);
  assert.equal(harness.actionStore.finalizerCalls.length, 1);
  assert.equal(harness.cache.upsertCalls.length, 0);
  assert.equal(harness.events.includes("action-complete-with-snapshot"), true);
  assert.deepEqual(
    harness.performanceEvents.map((event) => event.name),
    ["prepare", "cash-durable", "commit", "local-finalize"],
  );
  assert.deepEqual(
    harness.performanceEvents.map((event) => event.path),
    [
      "prepare-provider-v1",
      "prepare-provider-v1",
      "prepare-provider-v1",
      "prepare-provider-v1",
    ],
  );
  assert.equal(
    harness.performanceEvents.every(
      (event) =>
        event.elapsedMs === 5 &&
        event.operationHash.startsWith("sha256:") &&
        !event.operationHash.includes(
          harness.api.prepareProviderCalls[0]?.operationGuid ?? "missing",
        ),
    ),
    true,
  );
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("原设备主管明确未收现后安全释放 ProviderPending/Unknown，且不批准、不 commit、不生成新身份", async () => {
  for (const remoteStatus of ["ProviderPending", "Unknown"] as const) {
    const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
    const originalWorkflow = workflowOf(harness.runtime.createPresenter());
    if (remoteStatus === "ProviderPending") {
      harness.actionStore.failNextTransitionTo = "Unknown";
    }
    const prepare = originalWorkflow.prepareCashRepayment!({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash",
      voucherReference: null,
      voucherReservationToken: null,
      cashTenderedCents: 1_000,
    });
    if (remoteStatus === "ProviderPending") {
      await assert.rejects(prepare);
      assert.equal(harness.actionStore.getCurrent()?.state, "ProviderPending");
    } else {
      await prepare;
      assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");
    }
    const originalAttemptId = harness.api.claim?.providerAttemptId;
    const createdIdCount = harness.createdIdCount();
    harness.api.claim = Object.freeze({
      ...harness.api.claim!,
      status: remoteStatus,
    });

    harness.currentCashier.clear();
    activateCashier(
      harness.currentCashier,
      [INSTALLMENTS_CANCEL_PERMISSION],
      "SUPERVISOR-1",
    );
    const supervisorWorkflow = workflowOf(
      harness.rebuildRuntime().createPresenter(),
    );
    await supervisorWorkflow.cancelPreparedCashRepayment!();

    assert.deepEqual(harness.api.resolveClaimCalls, [
      {
        installmentGuid: "installment-1",
        operationGuid:
          harness.api.prepareProviderCalls[0]?.operationGuid,
        outcome: "Released",
        cashNotCollectedConfirmed: true,
        providerAttemptId: originalAttemptId,
      },
    ]);
    assert.equal(harness.actionStore.getCurrent(), null);
    assert.equal(harness.api.claim?.status, "Released");
    assert.equal(harness.payments.confirmCalls, 0);
    assert.equal(harness.payments.authorizeCalls, 0);
    assert.equal(harness.payments.recoverCalls, 0);
    assert.equal(harness.api.commitClaimCalls.length, 0);
    assert.equal(harness.api.prepareProviderCalls.length, 1);
    assert.equal(harness.actionStore.createdCandidates.length, 1);
    assert.equal(harness.createdIdCount(), createdIdCount);
  }
});

test("只读取消检查返回原设备 ProviderPending/Unknown 的既有现金 preparation", async () => {
  for (const remoteStatus of ["ProviderPending", "Unknown"] as const) {
    const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
    const originalWorkflow = workflowOf(harness.runtime.createPresenter());
    if (remoteStatus === "ProviderPending") {
      harness.actionStore.failNextTransitionTo = "Unknown";
      await assert.rejects(
        originalWorkflow.prepareCashRepayment!({
          installmentGuid: "installment-1",
          amountCents: 1_000,
          method: "cash",
          voucherReference: null,
          voucherReservationToken: null,
        }),
      );
    } else {
      await originalWorkflow.prepareCashRepayment!({
        installmentGuid: "installment-1",
        amountCents: 1_000,
        method: "cash",
        voucherReference: null,
        voucherReservationToken: null,
      });
    }
    harness.api.claim = Object.freeze({
      ...harness.api.claim!,
      status: remoteStatus,
    });
    harness.currentCashier.clear();
    activateCashier(
      harness.currentCashier,
      [INSTALLMENTS_CANCEL_PERMISSION],
      "SUPERVISOR-1",
    );
    const workflow = workflowOf(harness.rebuildRuntime().createPresenter());
    const transitionCalls = harness.actionStore.transitionCalls.length;
    const createdIdCount = harness.createdIdCount();
    const resolveCalls = harness.api.resolveClaimCalls.length;

    const preparation =
      await workflow.inspectCancellablePreparedCashRepayment!();

    assert.deepEqual(preparation, {
      installmentGuid: "installment-1",
      amountCents: 1_000,
      operationHash:
        harness.actionStore.getCurrent()!.intentFingerprint.slice(0, 23),
      path: "recovery",
    });
    assert.equal(harness.actionStore.transitionCalls.length, transitionCalls);
    assert.equal(harness.createdIdCount(), createdIdCount);
    assert.equal(harness.api.resolveClaimCalls.length, resolveCalls);
    assert.equal(harness.api.commitClaimCalls.length, 0);
    assert.equal(harness.payments.confirmCalls, 0);
    assert.equal(harness.payments.authorizeCalls, 0);
    assert.equal(harness.payments.recoverCalls, 0);
  }
});

test("只读取消检查精确校验远程现金事实，且不开放 Committed/Declined", async () => {
  for (const scenario of ["amount-mismatch", "Committed", "Declined"] as const) {
    const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
    const workflow = workflowOf(harness.runtime.createPresenter());
    await workflow.prepareCashRepayment!({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash",
      voucherReference: null,
      voucherReservationToken: null,
    });
    harness.api.claim = Object.freeze({
      ...harness.api.claim!,
      ...(scenario === "amount-mismatch"
        ? { amountCents: 1_001 }
        : { status: scenario }),
    });
    const transitionCalls = harness.actionStore.transitionCalls.length;

    if (scenario === "amount-mismatch") {
      await assert.rejects(
        workflow.inspectCancellablePreparedCashRepayment!(),
      );
    } else {
      assert.equal(
        await workflow.inspectCancellablePreparedCashRepayment!(),
        null,
      );
    }

    assert.equal(harness.actionStore.transitionCalls.length, transitionCalls);
    assert.equal(harness.api.resolveClaimCalls.length, 0);
    assert.equal(harness.api.commitClaimCalls.length, 0);
    assert.equal(harness.payments.confirmCalls, 0);
  }
});

test("只读取消检查要求当前主管 Cancel 权限，且不读取远程 claim", async () => {
  const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
  await workflowOf(harness.runtime.createPresenter()).prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
  });
  harness.currentCashier.clear();
  activateCashier(
    harness.currentCashier,
    [INSTALLMENTS_VIEW_PERMISSION],
    "SUPERVISOR-1",
  );

  await assert.rejects(
    workflowOf(
      harness.rebuildRuntime().createPresenter(),
    ).inspectCancellablePreparedCashRepayment!(),
    (error) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "authorization-declined",
  );

  assert.equal(harness.api.getClaimCalls.length, 0);
  assert.equal(harness.api.resolveClaimCalls.length, 0);
});

test("只读取消检查对 Approved settlement 与缺失 plan 失败关闭且不读取 binding", async () => {
  for (const settlementState of ["Approved", "Missing"] as const) {
    const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
    const workflow = workflowOf(harness.runtime.createPresenter());
    await workflow.prepareCashRepayment!({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash",
      voucherReference: null,
      voucherReservationToken: null,
    });
    const prepareCalls = harness.payments.prepareCalls;
    const createdIdCount = harness.createdIdCount();
    harness.payments.cashSettlementState = settlementState;

    assert.equal(
      await workflow.inspectCancellablePreparedCashRepayment!(),
      null,
    );

    assert.equal(harness.payments.prepareCalls, prepareCalls);
    assert.equal(harness.createdIdCount(), createdIdCount);
    assert.equal(harness.api.getClaimCalls.length, 0);
    assert.equal(harness.api.resolveClaimCalls.length, 0);
  }
});

test("只读取消检查对跨设备或非现金 blocking action 不开放", async () => {
  const crossDevice = createHarness({ repaymentClaimPrepareProviderV1: true });
  await workflowOf(crossDevice.runtime.createPresenter()).prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
  });
  await assert.rejects(
    workflowOf(
      crossDevice.rebuildRuntimeForDevice("IPAD-2").createPresenter(),
    ).inspectCancellablePreparedCashRepayment!(),
  );
  assert.equal(crossDevice.api.getClaimCalls.length, 0);

  const nonCash = createHarness({ paymentOutcome: "unknown" });
  await assert.rejects(
    workflowOf(nonCash.runtime.createPresenter()).addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "card",
      cardProvider: "square",
      voucherReference: null,
      voucherReservationToken: null,
    }),
  );
  const getClaimCalls = nonCash.api.getClaimCalls.length;
  assert.equal(
    await workflowOf(
      nonCash.rebuildRuntime().createPresenter(),
    ).inspectCancellablePreparedCashRepayment!(),
    null,
  );
  assert.equal(nonCash.api.getClaimCalls.length, getClaimCalls);
});

test("现金取消缺少主管权限时保留 blocking action 且不触碰远程 claim", async () => {
  const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
  await workflowOf(harness.runtime.createPresenter()).prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
  });
  harness.currentCashier.clear();
  activateCashier(harness.currentCashier, [INSTALLMENTS_VIEW_PERMISSION], "CASHIER-2");

  await assert.rejects(
    workflowOf(
      harness.rebuildRuntime().createPresenter(),
    ).cancelPreparedCashRepayment!(),
    (error) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "authorization-declined",
  );

  assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");
  assert.equal(harness.api.getClaimCalls.length, 0);
  assert.equal(harness.api.resolveClaimCalls.length, 0);
});

test("现金取消对 Approved settlement 或缺失 plan 均失败关闭", async () => {
  for (const settlementState of ["Approved", "Missing"] as const) {
    const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
    const workflow = workflowOf(harness.runtime.createPresenter());
    await workflow.prepareCashRepayment!({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash",
      voucherReference: null,
      voucherReservationToken: null,
    });
    const prepareCalls = harness.payments.prepareCalls;
    harness.payments.cashSettlementState = settlementState;

    await assert.rejects(workflow.cancelPreparedCashRepayment!());

    assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");
    assert.equal(harness.api.resolveClaimCalls.length, 0);
    assert.equal(harness.api.commitClaimCalls.length, 0);
    assert.equal(harness.payments.confirmCalls, 0);
    assert.equal(harness.payments.authorizeCalls, 0);
    assert.equal(harness.payments.prepareCalls, prepareCalls);
  }
});

test("现金取消对 attempt、operation 或设备不一致均拒绝且不释放本地 action", async () => {
  const attemptMismatch = createHarness({
    repaymentClaimPrepareProviderV1: true,
  });
  await workflowOf(
    attemptMismatch.runtime.createPresenter(),
  ).prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
  });
  attemptMismatch.api.claim = Object.freeze({
    ...attemptMismatch.api.claim!,
    providerAttemptId: "attempt:mismatch",
  });
  await assert.rejects(
    workflowOf(
      attemptMismatch.rebuildRuntime().createPresenter(),
    ).cancelPreparedCashRepayment!(),
  );
  assert.equal(attemptMismatch.api.resolveClaimCalls.length, 0);
  assert.equal(attemptMismatch.actionStore.getCurrent()?.state, "Unknown");

  const operationMismatch = createHarness({
    repaymentClaimPrepareProviderV1: true,
  });
  await workflowOf(
    operationMismatch.runtime.createPresenter(),
  ).prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
  });
  operationMismatch.api.claim = Object.freeze({
    ...operationMismatch.api.claim!,
    operationGuid: "f0000000-0000-4000-8000-000000000001",
  });
  await assert.rejects(
    workflowOf(
      operationMismatch.rebuildRuntime().createPresenter(),
    ).cancelPreparedCashRepayment!(),
  );
  assert.equal(operationMismatch.api.resolveClaimCalls.length, 0);
  assert.equal(operationMismatch.actionStore.getCurrent()?.state, "Unknown");

  const crossDevice = createHarness({ repaymentClaimPrepareProviderV1: true });
  await workflowOf(crossDevice.runtime.createPresenter()).prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
  });
  await assert.rejects(
    workflowOf(
      crossDevice.rebuildRuntimeForDevice("IPAD-2").createPresenter(),
    ).cancelPreparedCashRepayment!(),
  );
  assert.equal(crossDevice.api.getClaimCalls.length, 0);
  assert.equal(crossDevice.api.resolveClaimCalls.length, 0);
  assert.equal(crossDevice.actionStore.getCurrent()?.state, "Unknown");
});

test("现金 release 回包丢失时保留 action，重试 GET 已 Released 后只完成本地释放", async () => {
  const harness = createHarness({
    repaymentClaimPrepareProviderV1: true,
    claimResolveTransportFailsOnce: true,
  });
  const workflow = workflowOf(harness.runtime.createPresenter());
  await workflow.prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
  });

  await assert.rejects(workflow.cancelPreparedCashRepayment!());
  assert.equal(harness.api.claim?.status, "Released");
  assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");
  assert.equal(harness.api.resolveClaimCalls.length, 1);

  const retryWorkflow = workflowOf(
    harness.rebuildRuntime().createPresenter(),
  );
  const preparation =
    await retryWorkflow.inspectCancellablePreparedCashRepayment!();
  assert.equal(preparation?.installmentGuid, "installment-1");
  assert.equal(preparation?.amountCents, 1_000);
  assert.equal(preparation?.path, "recovery");
  assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");
  assert.equal(harness.api.resolveClaimCalls.length, 1);

  await retryWorkflow.cancelPreparedCashRepayment!();
  assert.equal(harness.actionStore.getCurrent(), null);
  assert.equal(harness.api.resolveClaimCalls.length, 1);
  assert.equal(harness.api.commitClaimCalls.length, 0);
  assert.equal(harness.payments.confirmCalls, 0);
});

test("现金取消仅接受远程 ProviderPending、Unknown 或已 Released", async () => {
  for (const status of ["Committed", "Declined", "Prepared"] as const) {
    const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
    const workflow = workflowOf(harness.runtime.createPresenter());
    await workflow.prepareCashRepayment!({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash",
      voucherReference: null,
      voucherReservationToken: null,
    });
    harness.api.claim = Object.freeze({
      ...harness.api.claim!,
      status,
      ...(status === "Prepared"
        ? { provider: null, providerAttemptId: null }
        : {}),
    });

    await assert.rejects(workflow.cancelPreparedCashRepayment!());

    assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");
    assert.equal(harness.api.resolveClaimCalls.length, 0);
    assert.equal(harness.api.commitClaimCalls.length, 0);
  }
});

test("现金确认入口展示前建立耐久 fence，点击后状态写失败也不得重新开放收现", async () => {
  const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
  const workflow = workflowOf(harness.runtime.createPresenter());
  await workflow.prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
    cashTenderedCents: 1_000,
  });

  assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");
  harness.actionStore.failNextTransitionTo = "Approved";
  await assert.rejects(workflow.confirmPreparedCashRepayment!());
  assert.equal(harness.payments.cashSettlementState, "Approved");
  assert.equal(harness.payments.confirmCalls, 1);
  assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");

  const recoveredWorkflow = workflowOf(
    harness.rebuildRuntime().createPresenter(),
  );
  assert.equal(await recoveredWorkflow.inspectPreparedCashRepayment!(), null);
  const recovered = await recoveredWorkflow.recoverBlocking();
  assert.equal(recovered.installmentGuid, "installment-1");
  assert.equal(harness.payments.confirmCalls, 1);
  assert.equal(harness.api.commitClaimCalls.length, 1);
});

test("两步现金续付在跨设备 capability 关闭时不写 durable action", async () => {
  const harness = createHarness({
    crossDeviceRepaymentEnabled: false,
    repaymentClaimPrepareProviderV1: true,
  });
  harness.api.detailsResponse = details({ deviceCode: "IPAD-2" });

  await assert.rejects(
    workflowOf(harness.runtime.createPresenter()).prepareCashRepayment!({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash",
      voucherReference: null,
      voucherReservationToken: null,
      cashTenderedCents: 1_000,
    }),
    (error) =>
      error instanceof InstallmentWorkflowError && error.code === "conflict",
  );

  assert.equal(harness.actionStore.getCurrent(), null);
  assert.equal(harness.payments.prepareCalls, 0);
  assert.equal(harness.api.prepareProviderCalls.length, 0);
  assert.equal(harness.api.createClaimCalls.length, 0);
});

test("现金确认入口已展示后重启只允许主管核对，Approved 才恢复同 operation commit", async () => {
  const prepared = createHarness({
    capturePerformance: true,
    repaymentClaimPrepareProviderV1: true,
  });
  const preparedWorkflow = workflowOf(prepared.runtime.createPresenter());
  await preparedWorkflow.prepareCashRepayment?.({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
    cashTenderedCents: 1_000,
  });
  const preparedRecoveryRuntime = prepared.rebuildRuntime();
  await assert.rejects(
    workflowOf(preparedRecoveryRuntime.createPresenter()).recoverBlocking(),
    (error) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "payment-recovery-required",
  );
  assert.equal(prepared.payments.confirmCalls, 0);
  assert.equal(prepared.api.commitClaimCalls.length, 0);

  prepared.performanceEvents.length = 0;
  prepared.payments.cashSettlementState = "Approved";
  const recovered = await workflowOf(prepared.rebuildRuntime().createPresenter()).recoverBlocking();
  assert.equal(recovered.installmentGuid, "installment-1");
  assert.equal(prepared.payments.confirmCalls, 0);
  assert.equal(prepared.payments.recoverCalls, 1);
  assert.equal(prepared.api.commitClaimCalls.length, 1);
  assert.deepEqual(
    prepared.performanceEvents.map((event) => event.path),
    ["recovery", "recovery"],
  );
});

test("点击已收现金先耐久批准，前置网络失败后只能恢复同一 operation", async () => {
  const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
  const workflow = workflowOf(harness.runtime.createPresenter());
  await workflow.prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
    cashTenderedCents: 1_000,
  });

  harness.online.value = false;
  await assert.rejects(
    workflow.confirmPreparedCashRepayment!(),
    (error) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "online-required",
  );
  assert.equal(harness.payments.cashSettlementState, "Approved");
  assert.equal(harness.payments.confirmCalls, 1);
  assert.equal(harness.api.commitClaimCalls.length, 0);

  harness.online.value = true;
  const recoveredWorkflow = workflowOf(
    harness.rebuildRuntime().createPresenter(),
  );
  assert.equal(await recoveredWorkflow.inspectPreparedCashRepayment!(), null);
  const recovered = await recoveredWorkflow.recoverBlocking();
  assert.equal(recovered.installmentGuid, "installment-1");
  assert.equal(harness.payments.confirmCalls, 1);
  assert.equal(harness.payments.recoverCalls, 1);
  assert.equal(harness.api.commitClaimCalls.length, 1);
});

test("Prepared 恢复 inspection 不创建缺失的本地 settlement plan", async () => {
  const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
  const workflow = workflowOf(harness.runtime.createPresenter());
  await workflow.prepareCashRepayment!({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
    cashTenderedCents: 1_000,
  });
  const prepareCalls = harness.payments.prepareCalls;
  harness.payments.cashSettlementState = "Missing";

  assert.equal(
    await workflowOf(
      harness.rebuildRuntime().createPresenter(),
    ).inspectPreparedCashRepayment!(),
    null,
  );
  assert.equal(harness.payments.prepareCalls, prepareCalls);
  assert.equal(harness.api.prepareProviderCalls.length, 1);
});

test("Prepared 现金只允许原收银员确认，但原收银员权限变化不打断锁定 operation", async () => {
  const harness = createHarness({ repaymentClaimPrepareProviderV1: true });
  harness.actionStore.failNextTransitionTo = "Unknown";
  await assert.rejects(
    workflowOf(harness.runtime.createPresenter()).prepareCashRepayment!({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "cash",
      voucherReference: null,
      voucherReservationToken: null,
      cashTenderedCents: 1_000,
    }),
  );
  assert.equal(harness.actionStore.getCurrent()?.state, "ProviderPending");

  harness.currentCashier.clear();
  activateCashier(harness.currentCashier, ALL_PERMISSIONS, "CASHIER-2");
  const otherCashier = workflowOf(harness.rebuildRuntime().createPresenter());
  await assert.rejects(
    otherCashier.inspectPreparedCashRepayment!(),
    (error) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "authorization-declined",
  );
  await assert.rejects(
    otherCashier.confirmPreparedCashRepayment!(),
    (error) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "authorization-declined",
  );
  assert.equal(harness.payments.confirmCalls, 0);

  harness.currentCashier.clear();
  activateCashier(harness.currentCashier, [], "CASHIER-1");
  const originalCashier = workflowOf(harness.rebuildRuntime().createPresenter());
  assert.equal(
    (await originalCashier.inspectPreparedCashRepayment!())?.amountCents,
    1_000,
  );
  const completed = await originalCashier.confirmPreparedCashRepayment!();
  assert.equal(completed.installmentGuid, "installment-1");
  assert.equal(harness.payments.confirmCalls, 1);
});

test("卡续付保持既有 snapshot 与 action 分离完成路径", async () => {
  const harness = createHarness({ useAtomicFinalizer: true });
  await workflowOf(harness.runtime.createPresenter()).addRepayment({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "card",
    cardProvider: "square",
    voucherReference: null,
    voucherReservationToken: null,
  });

  assert.equal(harness.actionStore.finalizerCalls.length, 0);
  assert.equal(harness.cache.upsertCalls.length, 1);
  assert.equal(harness.events.includes("action-complete"), true);
});

test("生产现金续付 finalizer 失败保持 BackendPending recovery", async () => {
  const failed = createHarness({
    useAtomicFinalizer: true,
    finalizerFailsOnce: true,
    repaymentClaimPrepareProviderV1: true,
  });
  const workflow = workflowOf(failed.runtime.createPresenter());
  await workflow.prepareCashRepayment?.({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "cash",
    voucherReference: null,
    voucherReservationToken: null,
    cashTenderedCents: 1_000,
  });
  await assert.rejects(
    workflow.confirmPreparedCashRepayment!(),
  );
  assert.equal(failed.actionStore.finalizerCalls.length, 1);
  assert.equal(failed.cache.upsertCalls.length, 0);
  assert.equal(failed.actionStore.getCurrent()?.state, "BackendPending");
  assert.equal(await failed.runtime.hasRecoveryRequired(), true);
});

test("Unknown 使用同一 claim binding 重开，只 recover 原 attempt 不发起新授权", async () => {
  const harness = createHarness({ paymentOutcomes: ["unknown", "approved"] });
  const presenter = harness.runtime.createPresenter();
  await presenter.load();
  await presenter.select("installment-1");
  fillVoucherRepayment(presenter);

  await presenter.addRepayment();
  const firstBinding = harness.api.beginClaimCalls[0];
  assert.equal(presenter.getState().statusCode, "payment-recovery-required");
  assert.equal(harness.api.claim?.status, "Unknown");

  await presenter.recoverBlocking();

  assert.deepEqual(harness.api.beginClaimCalls[1], firstBinding);
  assert.equal(harness.payments.authorizeCalls, 1);
  assert.equal(harness.payments.recoverCalls, 1);
  assert.equal(harness.payments.prepareBindings[0]?.providerAttemptId,
    harness.payments.prepareBindings[1]?.providerAttemptId);
  assert.equal(harness.api.claim?.status, "Committed");
});

test("provider 抛异常先双边 Unknown，恢复只 recover 同 attempt 不重新授权", async () => {
  const harness = createHarness({ paymentOutcomes: ["throw", "approved"] });
  const workflow = workflowOf(harness.runtime.createPresenter());
  const input = {
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "card" as const,
    cardProvider: "square" as const,
    voucherReference: null,
    voucherReservationToken: null,
  };

  await assert.rejects(workflow.addRepayment(input));
  const attemptId = harness.payments.prepareBindings[0]?.providerAttemptId;
  assert.equal(harness.api.claim?.status, "Unknown");
  assert.equal(harness.actionStore.getCurrent()?.state, "Unknown");

  await workflow.recoverBlocking();

  assert.equal(harness.payments.authorizeCalls, 1);
  assert.equal(harness.payments.recoverCalls, 1);
  assert.equal(
    harness.payments.prepareBindings[1]?.providerAttemptId,
    attemptId,
  );
});

test("claim commit 回包丢失后先 GET 已提交事实，不重复 provider 或本地付款", async () => {
  const harness = createHarness({ claimCommitTransportFailsOnce: true });
  const presenter = harness.runtime.createPresenter();
  await presenter.load();
  await presenter.select("installment-1");
  fillVoucherRepayment(presenter);

  await presenter.addRepayment();

  assert.equal(presenter.getState().statusCode, "repayment-complete");
  assert.equal(harness.payments.charges, 1);
  assert.equal(harness.api.commitClaimCalls.length, 1);
  assert.equal(harness.api.getClaimCalls.length, 2);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("同店跨机还款仅由实时 capability 放行并走 claim，cancel 仍要求原终端", async () => {
  const harness = createHarness({ crossDeviceRepaymentEnabled: true });
  harness.api.detailsResponse = details({ deviceCode: "IPAD-2" });
  const workflow = workflowOf(harness.runtime.createPresenter());

  const repaid = await workflow.addRepayment({
    installmentGuid: "installment-1",
    amountCents: 1_000,
    method: "card",
    cardProvider: "square",
    voucherReference: null,
    voucherReservationToken: null,
  });
  assert.equal(repaid.payments.length, 1);
  assert.equal(harness.api.createClaimCalls.length, 1);
  assert.equal(harness.api.appendCalls, 0);

  await assert.rejects(
    workflow.cancelWithRefund({
      installmentGuid: "installment-1",
      reason: null,
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "conflict",
  );
  assert.equal(harness.api.cancelCalls.length, 0);
});

test("同店跨机高风险动作的直接调用全部失败关闭且零副作用", async () => {
  const reprintCalls: string[] = [];
  const receiptReprint: InstallmentReceiptReprintRuntimePort = {
    canReprint: () => true,
    async execute(installmentGuid) {
      reprintCalls.push(installmentGuid);
      return { state: "Printed", errorCode: null };
    },
  };
  const harness = createHarness({
    crossDeviceRepaymentEnabled: true,
    receiptReprint,
  });
  harness.api.detailsResponse = details({ deviceCode: "IPAD-2" });
  const presenter = harness.runtime.createPresenter();
  const workflow = workflowOf(presenter);

  for (const mutation of [
    () =>
      workflow.cancelWithRefund({
        installmentGuid: "installment-1",
        reason: null,
      }),
    () =>
      workflow.void({
        installmentGuid: "installment-1",
        reason: "duplicate",
      }),
    () =>
      workflow.confirmPickup({
        installmentGuid: "installment-1",
        note: null,
      }),
  ]) {
    await assert.rejects(
      mutation(),
      (error) =>
        error instanceof Error &&
        "code" in error &&
        error.code === "conflict",
    );
  }

  await presenter.load();
  await presenter.select("installment-1");
  await presenter.reprintSelected();

  assert.equal(harness.api.cancelCalls.length, 0);
  assert.equal(harness.api.voidCalls.length, 0);
  assert.equal(harness.api.pickupCalls.length, 0);
  assert.deepEqual(reprintCalls, []);
  assert.equal(harness.actionStore.createdCandidates.length, 0);
  assert.equal(harness.payments.authorizeCalls, 0);
});

test("同店跨机取消退款、作废和提货的 runtime 直接调用分别要求独立 capability", async () => {
  const cancel = createHarness();
  cancel.api.detailsResponse = details({
    deviceCode: "IPAD-2",
    payments: [
      {
        paymentGuid: "20000000-0000-4000-8000-000000000001",
        method: "cash",
        amountCents: 2_000,
        status: "Recorded",
        recordedAtIso: "2026-07-28T08:00:00.000Z",
        cashierId: "CASHIER-1",
        deviceCode: "IPAD-1",
        cardType: null,
        maskedCardNumber: null,
      },
    ],
  });
  cancel.api.capabilities = Object.freeze({
    ...cancel.api.capabilities,
    crossDeviceCancelRefundEnabled: true,
  }) as InstallmentRepaymentCapabilities;
  await workflowOf(cancel.runtime.createPresenter()).cancelWithRefund({
    installmentGuid: "installment-1",
    reason: null,
  });
  assert.equal(cancel.api.cancelCalls.length, 1);

  const voided = createHarness();
  voided.api.detailsResponse = details({ deviceCode: "IPAD-2" });
  voided.api.capabilities = Object.freeze({
    ...voided.api.capabilities,
    crossDeviceVoidEnabled: true,
  }) as InstallmentRepaymentCapabilities;
  await workflowOf(voided.runtime.createPresenter()).void({
    installmentGuid: "installment-1",
    reason: "duplicate",
  });
  assert.equal(voided.api.voidCalls.length, 1);
  assert.match(
    Reflect.get(voided.api.voidCalls[0]!, "operationGuid"),
    /^[0-9a-f-]{36}$/i,
  );
  assert.equal(
    Reflect.get(voided.api.voidCalls[0]!, "idempotencyKey"),
    Reflect.get(voided.api.voidCalls[0]!, "operationGuid"),
  );

  const pickedUp = createHarness();
  pickedUp.api.detailsResponse = details({
    deviceCode: "IPAD-2",
    status: "PaidOff",
  });
  pickedUp.api.capabilities = Object.freeze({
    ...pickedUp.api.capabilities,
    crossDevicePickupEnabled: true,
  }) as InstallmentRepaymentCapabilities;
  await workflowOf(pickedUp.runtime.createPresenter()).confirmPickup({
    installmentGuid: "installment-1",
    note: null,
  });
  assert.equal(pickedUp.api.pickupCalls.length, 1);
  assert.match(
    Reflect.get(pickedUp.api.pickupCalls[0]!, "operationGuid"),
    /^[0-9a-f-]{36}$/i,
  );
  assert.equal(
    Reflect.get(pickedUp.api.pickupCalls[0]!, "idempotencyKey"),
    Reflect.get(pickedUp.api.pickupCalls[0]!, "operationGuid"),
  );
});

test("分期作废回包丢失并重启后复用完整冻结命令", async () => {
  const voided = createHarness();
  voided.api.voidTransportFailsOnce = true;
  await assert.rejects(
    workflowOf(voided.runtime.createPresenter()).void({
      installmentGuid: "installment-1",
      reason: "duplicate",
    }),
  );
  assert.equal(await voided.runtime.hasRecoveryRequired(), true);
  const frozen = voided.actionStore.lifecycleCandidates[0];
  assert.ok(frozen?.kind === "void");
  assert.equal(frozen.originalDeviceCode, DEVICE_CODE);
  await workflowOf(voided.rebuildRuntime().createPresenter()).recoverBlocking();
  assert.equal(voided.api.voidCalls.length, 2);
  assert.deepEqual(voided.api.voidCalls[1], voided.api.voidCalls[0]);
  assert.equal(await voided.rebuildRuntime().hasRecoveryRequired(), false);
});

test("分期作废服务端已提交但本地快照缓存失败时进入恢复且可重放", async () => {
  const voided = createHarness({ cacheUpsertFailsOnce: true });
  await assert.rejects(
    workflowOf(voided.runtime.createPresenter()).void({
      installmentGuid: "installment-1",
      reason: "duplicate",
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "payment-recovery-required",
  );
  assert.equal(voided.api.voidCalls.length, 1);
  assert.equal(await voided.runtime.hasRecoveryRequired(), true);
  const frozen = voided.actionStore.lifecycleCandidates[0];
  assert.ok(frozen?.kind === "void");
  await workflowOf(voided.rebuildRuntime().createPresenter()).recoverBlocking();
  assert.equal(voided.api.voidCalls.length, 2);
  assert.deepEqual(voided.api.voidCalls[1], voided.api.voidCalls[0]);
  assert.equal(await voided.rebuildRuntime().hasRecoveryRequired(), false);
});

test("分期作废服务端已提交但 lifecycle 完成写失败时进入恢复且可重放", async () => {
  const voided = createHarness({ lifecycleCompleteFailsOnce: true });
  await assert.rejects(
    workflowOf(voided.runtime.createPresenter()).void({
      installmentGuid: "installment-1",
      reason: "duplicate",
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "payment-recovery-required",
  );
  assert.equal(voided.api.voidCalls.length, 1);
  assert.equal(await voided.runtime.hasRecoveryRequired(), true);
  await workflowOf(voided.rebuildRuntime().createPresenter()).recoverBlocking();
  assert.equal(voided.api.voidCalls.length, 2);
  assert.deepEqual(voided.api.voidCalls[1], voided.api.voidCalls[0]);
  assert.equal(await voided.rebuildRuntime().hasRecoveryRequired(), false);
});

test("分期提货服务端已提交但本地快照缓存失败时进入恢复且可重放", async () => {
  const pickedUp = createHarness({ cacheUpsertFailsOnce: true });
  pickedUp.api.detailsResponse = details({ status: "PaidOff" });
  await assert.rejects(
    workflowOf(pickedUp.runtime.createPresenter()).confirmPickup({
      installmentGuid: "installment-1",
      note: null,
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "payment-recovery-required",
  );
  assert.equal(pickedUp.api.pickupCalls.length, 1);
  assert.equal(await pickedUp.runtime.hasRecoveryRequired(), true);
  await workflowOf(
    pickedUp.rebuildRuntime().createPresenter(),
  ).recoverBlocking();
  assert.equal(pickedUp.api.pickupCalls.length, 2);
  assert.deepEqual(pickedUp.api.pickupCalls[1], pickedUp.api.pickupCalls[0]);
  assert.equal(await pickedUp.rebuildRuntime().hasRecoveryRequired(), false);
});

test("分期作废响应显示名变更时仍按稳定操作事实结算", async () => {
  const voided = createHarness();
  voided.api.lifecycleCashierNameOverride = "Cashier One (Renamed)";
  await workflowOf(voided.runtime.createPresenter()).void({
    installmentGuid: "installment-1",
    reason: "duplicate",
  });
  assert.equal(voided.api.voidCalls.length, 1);
  assert.equal(await voided.runtime.hasRecoveryRequired(), false);
});

test("分期作废响应原因与冻结事实不一致时进入恢复", async () => {
  const voided = createHarness();
  voided.api.lifecycleVoidReasonOverride = "different reason";
  await assert.rejects(
    workflowOf(voided.runtime.createPresenter()).void({
      installmentGuid: "installment-1",
      reason: "duplicate",
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "payment-recovery-required",
  );
  assert.equal(await voided.runtime.hasRecoveryRequired(), true);
});

test("分期提货响应显示名变更时仍按稳定操作事实结算", async () => {
  const pickedUp = createHarness();
  pickedUp.api.detailsResponse = details({ status: "PaidOff" });
  pickedUp.api.lifecycleCashierNameOverride = "Cashier One (Renamed)";
  await workflowOf(pickedUp.runtime.createPresenter()).confirmPickup({
    installmentGuid: "installment-1",
    note: null,
  });
  assert.equal(pickedUp.api.pickupCalls.length, 1);
  assert.equal(await pickedUp.runtime.hasRecoveryRequired(), false);
});

test("分期提货备注在冻结前 trim/空白转 null，回包丢失重启后原样复用", async () => {
  for (const [inputNote, expectedNote] of [
    ["  Customer collected at dock  ", "Customer collected at dock"],
    ["   ", null],
  ] as const) {
    const pickedUp = createHarness();
    pickedUp.api.detailsResponse = details({
      deviceCode: "IPAD-2",
      status: "PaidOff",
    });
    pickedUp.api.capabilities = Object.freeze({
      ...pickedUp.api.capabilities,
      crossDevicePickupEnabled: true,
    }) as InstallmentRepaymentCapabilities;
    pickedUp.api.pickupTransportFailsOnce = true;
    await assert.rejects(
      workflowOf(pickedUp.runtime.createPresenter()).confirmPickup({
        installmentGuid: "installment-1",
        note: inputNote,
      }),
    );
    const frozen = pickedUp.actionStore.lifecycleCandidates[0];
    assert.ok(frozen?.kind === "pickup");
    assert.equal(frozen.originalDeviceCode, "IPAD-2");
    assert.equal(frozen.deviceCode, DEVICE_CODE);
    assert.equal(frozen.command.cashierId, "CASHIER-1");
    assert.equal(Reflect.get(frozen.command, "note"), expectedNote);
    await workflowOf(
      pickedUp.rebuildRuntime().createPresenter(),
    ).recoverBlocking();
    assert.equal(pickedUp.api.pickupCalls.length, 2);
    assert.deepEqual(pickedUp.api.pickupCalls[1], pickedUp.api.pickupCalls[0]);
    assert.equal(await pickedUp.rebuildRuntime().hasRecoveryRequired(), false);
  }
});

test("原终端直接调用取消、作废、提货与重打仍可执行", async () => {
  const cancel = createHarness();
  await workflowOf(cancel.runtime.createPresenter()).cancelWithRefund({
    installmentGuid: "installment-1",
    reason: null,
  });
  assert.equal(cancel.api.cancelCalls.length, 1);

  const voided = createHarness();
  await workflowOf(voided.runtime.createPresenter()).void({
    installmentGuid: "installment-1",
    reason: "duplicate",
  });
  assert.equal(voided.api.voidCalls.length, 1);

  const pickedUp = createHarness();
  pickedUp.api.detailsResponse = details({ status: "PaidOff" });
  await workflowOf(pickedUp.runtime.createPresenter()).confirmPickup({
    installmentGuid: "installment-1",
    note: null,
  });
  assert.equal(pickedUp.api.pickupCalls.length, 1);

  const reprintCalls: string[] = [];
  const reprint = createHarness({
    receiptReprint: {
      canReprint: () => true,
      async execute(installmentGuid) {
        reprintCalls.push(installmentGuid);
        return { state: "Printed", errorCode: null };
      },
    },
  });
  const presenter = reprint.runtime.createPresenter();
  await presenter.load();
  await presenter.select("installment-1");
  await presenter.reprintSelected();
  assert.deepEqual(reprintCalls, ["installment-1"]);
});

test("provider Declined 先 resolve claim 再释放本地 action", async () => {
  const harness = createHarness({ paymentOutcome: "declined" });
  const workflow = workflowOf(harness.runtime.createPresenter());

  await assert.rejects(
    workflow.addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "card",
      cardProvider: "square",
      voucherReference: null,
      voucherReservationToken: null,
    }),
    (error) =>
      error instanceof Error &&
      "code" in error &&
      error.code === "authorization-declined",
  );

  assert.equal(harness.api.claim?.status, "Declined");
  assert.equal(harness.api.resolveClaimCalls[0]?.outcome, "Declined");
  assertOrdered(harness.events, ["claim-resolve:Declined", "action-decline"]);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("Prepared claim 在 provider plan 无法固定时可 Released，且 provider 零调用", async () => {
  const harness = createHarness({ paymentPrepareFails: true });
  const workflow = workflowOf(harness.runtime.createPresenter());

  await assert.rejects(
    workflow.addRepayment({
      installmentGuid: "installment-1",
      amountCents: 1_000,
      method: "card",
      cardProvider: "square",
      voucherReference: null,
      voucherReservationToken: null,
    }),
  );

  assert.equal(harness.api.claim?.status, "Released");
  assert.equal(harness.api.resolveClaimCalls[0]?.outcome, "Released");
  assert.equal(harness.payments.authorizeCalls, 0);
  assert.equal(harness.api.appendCalls, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("分期重打使用同一可信 lease、精确 History.Reprint 权限与 fulfilment 结果", async () => {
  const calls: string[] = [];
  const receiptReprint: InstallmentReceiptReprintRuntimePort = {
    canReprint: () => true,
    async execute(installmentGuid, authorization, assertActive) {
      assertActive();
      calls.push(installmentGuid);
      assert.equal(
        authorization.permissionCode,
        INSTALLMENTS_REPRINT_PERMISSION,
      );
      assert.equal(authorization.requestingCashierId, "CASHIER-1");
      return { state: "Printed", errorCode: null };
    },
  };
  const harness = createHarness({ receiptReprint });
  const presenter = harness.runtime.createPresenter();
  await presenter.select("installment-1");

  assert.equal(presenter.capabilities.reprint, true);
  await presenter.reprintSelected();

  assert.deepEqual(calls, ["installment-1"]);
  assert.deepEqual(presenter.getState().reprint, {
    kind: "succeeded",
    installmentGuid: "installment-1",
  });
});

test("分期重打缺权限或 cashier lease 失效时 fail closed", async () => {
  const receiptReprint: InstallmentReceiptReprintRuntimePort = {
    canReprint: () => true,
    async execute() {
      throw new Error("不应调用 fulfilment");
    },
  };
  const missingPermission = createHarness({
    permissions: ALL_PERMISSIONS.filter(
      (permission) => permission !== INSTALLMENTS_REPRINT_PERMISSION,
    ),
    receiptReprint,
  });
  const denied = missingPermission.runtime.createPresenter();
  await denied.select("installment-1");
  assert.equal(denied.capabilities.reprint, false);

  const expired = createHarness({ receiptReprint });
  const presenter = expired.runtime.createPresenter();
  await presenter.select("installment-1");
  expired.currentCashier.clear();
  assert.equal(presenter.capabilities.reprint, false);
  await presenter.reprintSelected();
  assert.deepEqual(presenter.getState().reprint, { kind: "unavailable" });
});

test("统一支付确认前掉线且无耐久 action 时保留可重试状态", async () => {
  const harness = createHarness();
  const entry = harness.runtime.prepareCreateCheckout();
  const presenter = harness.runtime.createCheckoutPresenter(entry);

  assert.equal(await presenter.initialize(), true);
  presenter.openInstallmentCustomerEditor();
  presenter.setInstallmentCustomerDraftName("Customer");
  presenter.setInstallmentCustomerDraftPhone("0400000000");
  presenter.saveInstallmentCustomer();
  assert.equal(await presenter.submitSelected(), true);

  harness.online.value = false;
  assert.equal(await presenter.confirm(), false);
  assert.equal(presenter.getState().phase, "ready");
  assert.equal(presenter.getState().runtimeErrorCode, "ONLINE_REQUIRED");
  assert.equal(presenter.getState().allowedActions.recover, false);
  assert.equal(harness.actionStore.createdCandidates.length, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("transport failure 与离线都返回 online-required，且不读取旧 SQLCipher 快照", async () => {
  const harness = createHarness({
    cached: [snapshot({ installmentGuid: "cached-store-1" })],
  });
  harness.api.listError = new HbposApiError("network", { kind: "transport" });
  const presenter = harness.runtime.createPresenter();
  await presenter.load();
  assert.equal(harness.cache.upsertCalls.length, 0);
  assert.equal(presenter.getState().statusCode, "online-required");
  assert.deepEqual(presenter.getState().orders, []);

  harness.online.value = false;
  presenter.setOnline(false);
  await presenter.load();
  assert.deepEqual(harness.cache.listCalls, []);
  assert.deepEqual(presenter.getState().orders, []);
  assert.equal(presenter.getState().statusCode, "online-required");
});

test("设备范围只使用可信 terminal code，日期按注入时钟和门店时区转为 UTC", async () => {
  const harness = createHarness();
  const presenter = harness.runtime.createPresenter();

  await presenter.setDeviceScope("device");
  await presenter.setDateFilter({
    preset: "last7",
    fromDate: null,
    toDate: null,
  });

  assert.deepEqual(
    harness.api.listInputs.at(-1),
    {
      createdFromIso: "2026-07-21T14:00:00.000Z",
      createdToIso: "2026-07-28T13:59:59.999Z",
      deviceCode: DEVICE_CODE,
      keyword: null,
      skip: 0,
      status: null,
      take: 51,
    },
  );
  assert.equal(harness.cache.upsertCalls.length, 0);
  assert.equal(harness.cache.listCalls.length, 0);
});

test("远端 5xx、权限和未知错误映射为不同安全状态码", async () => {
  const harness = createHarness();
  const presenter = harness.runtime.createPresenter();

  harness.api.listError = new HbposApiError(
    "https://secret.example/internal unavailable",
    { kind: "http", status: 503 },
  );
  await presenter.load();
  assert.equal(presenter.getState().statusCode, "service-unavailable");

  harness.api.listError = new HbposApiError("secret permission payload", {
    kind: "http",
    status: 403,
  });
  await presenter.load();
  assert.equal(presenter.getState().statusCode, "authorization-declined");

  harness.api.listError = new Error("https://secret.example/raw-url");
  await presenter.load();
  assert.equal(presenter.getState().statusCode, "history-failed");
  assert.equal(
    JSON.stringify(presenter.getState()).includes("secret.example"),
    false,
  );
});

test("详情设备范围拒绝不会伪装成收银员无权限或清空已加载列表", async () => {
  const harness = createHarness();
  const presenter = harness.runtime.createPresenter();
  await presenter.load();
  harness.api.detailsError = new HbposApiError("device scope denied", {
    kind: "http",
    status: 403,
    code: "DEVICE_SCOPE_FORBIDDEN",
  });

  await presenter.select("installment-1");

  assert.equal(presenter.getState().kind, "ready");
  assert.equal(presenter.getState().statusCode, "details-failed");
  assert.equal(presenter.getState().selectedGuid, "installment-1");
  assert.equal(presenter.getState().orders.length, 1);
});

test("单次 mutation 更新快照时不会把第 101 条以后的本机历史截断", async () => {
  const cached = Array.from({ length: 150 }, (_, index) =>
    snapshot({ installmentGuid: `cached-${String(index).padStart(3, "0")}` }),
  );
  const harness = createHarness({ cached });
  const presenter = harness.runtime.createPresenter();
  fillCreate(presenter);

  await presenter.create();

  assert.equal(
    harness.cache
      .all()
      .some((item) => item.installmentGuid === "cached-149"),
    true,
  );
  assert.equal(harness.cache.all().length, 151);
});

test("create 在后端失败后以同一 action 恢复，绝不重复扣款且不清车", async () => {
  const harness = createHarness({ createFailsOnce: true });
  const presenter = harness.runtime.createPresenter();
  fillCreate(presenter);

  await presenter.create();
  assert.equal(harness.payments.charges, 1);
  assert.equal(harness.cart.clearCalls, 0);
  assert.equal(harness.api.createCalls.length, 1);

  await presenter.recoverBlocking();
  assert.equal(harness.payments.charges, 1);
  assert.equal(harness.api.createCalls.length, 2);
  assert.equal(
    harness.api.createCalls[0]?.installmentGuid,
    harness.api.createCalls[1]?.installmentGuid,
  );
  assert.equal(
    harness.api.createCalls[0]?.downPayment.paymentGuid,
    harness.api.createCalls[1]?.downPayment.paymentGuid,
  );
  assert.deepEqual(
    harness.api.createCalls[0]?.lines.map((line) => line.installmentLineGuid),
    harness.api.createCalls[1]?.lines.map((line) => line.installmentLineGuid),
  );
  assert.equal(harness.cart.clearCalls, 1);
});

test("Unknown 支付结果只恢复同一 provider attempt，不创建新 action", async () => {
  const harness = createHarness({ paymentOutcome: "unknown" });
  const presenter = harness.runtime.createPresenter();
  fillCreate(presenter);

  await presenter.create();
  assert.equal(presenter.getState().statusCode, "payment-recovery-required");
  await presenter.recoverBlocking();
  assert.equal(harness.payments.beginCalls, 2);
  assert.equal(
    harness.payments.actions[0]?.actionId,
    harness.payments.actions[1]?.actionId,
  );
  assert.equal(harness.api.createCalls.length, 0);
});

test("Approved 后 backend 失败并重建 runtime，必须恢复同一 action 且只扣款一次", async () => {
  const harness = createHarness({ createFailsOnce: true });
  const first = harness.runtime.createPresenter();
  fillCreate(first);
  await first.create();
  first.destroy();

  const recovered = harness.rebuildRuntime().createPresenter();
  await recovered.load();

  assert.equal(harness.payments.charges, 1);
  assert.equal(harness.api.createCalls.length, 2);
  assert.equal(
    harness.payments.actions[0]?.actionId,
    harness.payments.actions[1]?.actionId,
  );
  assert.equal(
    harness.api.createCalls[0]?.installmentGuid,
    harness.api.createCalls[1]?.installmentGuid,
  );
});

test("Unknown 后重建 runtime 只能恢复旧 attempt，不能创建新的 provider action", async () => {
  const harness = createHarness({
    paymentOutcomes: ["unknown", "approved"],
  });
  const first = harness.runtime.createPresenter();
  fillCreate(first);
  await first.create();
  first.destroy();

  const recovered = harness.rebuildRuntime().createPresenter();
  await recovered.load();

  assert.equal(harness.payments.beginCalls, 2);
  assert.equal(harness.payments.charges, 1);
  assert.equal(
    harness.payments.actions[0]?.actionId,
    harness.payments.actions[1]?.actionId,
  );
  assert.equal(harness.api.createCalls.length, 1);
});

test("券 create/repayment 仅提交券码并在 action insert 前按稳定 actionId 写 vault", async () => {
  const createHarnessInstance = createHarness();
  const createPresenter = createHarnessInstance.runtime.createPresenter();
  fillVoucherCreate(createPresenter);

  await createPresenter.create();

  const createCandidate =
    createHarnessInstance.actionStore.createdCandidates[0];
  const createStage = createHarnessInstance.voucherIntents.calls[0];
  assert.ok(createCandidate);
  assert.ok(createStage);
  assert.equal(createStage.actionId, createCandidate.action.actionId);
  assert.equal(createStage.paymentGuid, createCandidate.action.paymentGuid);
  assert.equal(createStage.installmentGuid, createCandidate.action.installmentGuid);
  assert.equal(createStage.storeCode, STORE_CODE);
  assert.equal(createStage.deviceCode, DEVICE_CODE);
  assert.equal(createStage.cashierId, "CASHIER-1");
  assert.equal(createStage.amountCents, 2_000);
  assert.equal(createStage.voucherReference, "VOUCHER-SECRET-CREATE");
  assert.equal(createStage.voucherReservationToken, null);
  assert.ok(
    createHarnessInstance.events.indexOf("voucher-stage") <
      createHarnessInstance.events.indexOf("action-create"),
  );
  assert.ok(
    createHarnessInstance.events.indexOf("action-create") <
      createHarnessInstance.events.indexOf(
        "action-transition:ProviderPending",
      ),
  );
  assert.match(
    createCandidate.intentFingerprint,
    /^sha256:[0-9a-f]{64}$/,
  );
  const createPersistedJson = JSON.stringify(createCandidate);
  assert.equal(createPersistedJson.includes("VOUCHER-SECRET-CREATE"), false);
  const createVoucherMaterial = createHarnessInstance.hashMaterials[0];
  const createFingerprintMaterial =
    createHarnessInstance.hashMaterials[1];
  assert.ok(createVoucherMaterial);
  assert.ok(createFingerprintMaterial);
  const createVoucherDigest = `sha256:${createHash("sha256")
    .update(createVoucherMaterial, "utf8")
    .digest("hex")}`;
  assert.equal(
    createFingerprintMaterial.includes(createVoucherDigest),
    true,
  );
  assert.equal(
    createFingerprintMaterial.includes("VOUCHER-SECRET-CREATE"),
    false,
  );
  assert.equal(createVoucherMaterial.includes('"reservationToken":null'), true);

  const repaymentHarness = createHarness();
  const repaymentPresenter = repaymentHarness.runtime.createPresenter();
  await repaymentPresenter.load();
  await repaymentPresenter.select("installment-1");
  fillVoucherRepayment(repaymentPresenter);

  await repaymentPresenter.addRepayment();

  const repaymentCandidate =
    repaymentHarness.actionStore.createdCandidates[0];
  const repaymentStage = repaymentHarness.voucherIntents.calls[0];
  assert.ok(repaymentCandidate);
  assert.ok(repaymentStage);
  assert.equal(repaymentStage.actionId, repaymentCandidate.action.actionId);
  assert.equal(repaymentStage.paymentGuid, repaymentCandidate.action.paymentGuid);
  assert.equal(repaymentStage.installmentGuid, "installment-1");
  assert.equal(repaymentStage.amountCents, 1_000);
  assert.equal(repaymentStage.voucherReference, "VOUCHER-SECRET-REPAY");
  assert.equal(repaymentStage.voucherReservationToken, null);
  assert.match(
    repaymentCandidate.intentFingerprint,
    /^sha256:[0-9a-f]{64}$/,
  );
  assert.equal(
    JSON.stringify(repaymentCandidate).includes("VOUCHER-SECRET-REPAY"),
    false,
  );
});

test("券 intent 支持 WPF 等价的 code-only 输入，reservationToken 通常为 null", async () => {
  const harness = createHarness();
  const presenter = harness.runtime.createPresenter();

  await workflowOf(presenter).create({
    draftRevision: 1,
    customerName: "Customer",
    customerPhone: "0400000000",
    note: null,
    downPaymentCents: 2_000,
    method: "voucher",
    voucherReference: "VOUCHER-CODE-ONLY",
    voucherReservationToken: null,
  });

  assert.equal(harness.voucherIntents.calls.length, 1);
  assert.equal(
    harness.voucherIntents.calls[0]?.voucherReservationToken,
    null,
  );
  assert.equal(
    harness.hashMaterials[0]?.includes('"reservationToken":null'),
    true,
  );
  assert.equal(
    JSON.stringify(harness.actionStore.createdCandidates[0]).includes(
      "VOUCHER-CODE-ONLY",
    ),
    false,
  );
});

test("券 vault stage 失败时不插入 action、不进入 ProviderPending 且不把错误 secret 暴露为状态", async () => {
  const harness = createHarness();
  harness.voucherIntents.failure = new Error(
    "vault failed for VOUCHER-SECRET-FAIL",
  );
  const presenter = harness.runtime.createPresenter();
  fillVoucherCreate(presenter, "VOUCHER-SECRET-FAIL");

  await presenter.create();

  assert.equal(harness.voucherIntents.calls.length, 1);
  assert.equal(harness.actionStore.createdCandidates.length, 0);
  assert.equal(harness.actionStore.transitionCalls.length, 0);
  assert.equal(harness.payments.beginCalls, 0);
  assert.equal(presenter.getState().statusCode, "action-failed");
});

test("券 Unknown/崩溃恢复沿用同一 actionId 且不再次 stage", async () => {
  const harness = createHarness({
    paymentOutcomes: ["unknown", "approved"],
  });
  const first = harness.runtime.createPresenter();
  fillVoucherCreate(first);
  await first.create();
  first.destroy();

  const recovered = harness.rebuildRuntime().createPresenter();
  await recovered.load();

  assert.equal(harness.voucherIntents.calls.length, 1);
  assert.equal(harness.payments.actions.length, 2);
  assert.equal(
    harness.payments.actions[0]?.actionId,
    harness.payments.actions[1]?.actionId,
  );
  assert.equal(
    harness.voucherIntents.calls[0]?.actionId,
    harness.payments.actions[0]?.actionId,
  );
});

test("terminal scope 竞争时 losing 券 intent 保持原 actionId，不重绑 winning action", async () => {
  const harness = createHarness();
  harness.actionStore.raceOnNextCreate = true;
  const presenter = harness.runtime.createPresenter();
  fillVoucherCreate(presenter);

  await presenter.create();

  const losingActionId = harness.voucherIntents.calls[0]?.actionId;
  const winningActionId = harness.actionStore.raceWinner?.action.actionId;
  assert.ok(losingActionId);
  assert.ok(winningActionId);
  assert.notEqual(losingActionId, winningActionId);
  assert.equal(harness.voucherIntents.calls.length, 1);
  assert.equal(harness.payments.actions[0]?.actionId, winningActionId);
  assert.equal(
    harness.voucherIntents.calls.some(
      (call) => call.actionId === winningActionId,
    ),
    false,
  );
});

test("现金和卡 action 不写 voucher vault，fingerprint 仍为不可逆摘要", async () => {
  const cash = createHarness();
  const cashPresenter = cash.runtime.createPresenter();
  fillCreate(cashPresenter);
  await cashPresenter.create();

  assert.equal(cash.voucherIntents.calls.length, 0);
  assert.match(
    cash.actionStore.createdCandidates[0]?.intentFingerprint ?? "",
    /^sha256:[0-9a-f]{64}$/,
  );

  const card = createHarness();
  const cardPresenter = card.runtime.createPresenter();
  fillCreate(cardPresenter);
  cardPresenter.setCreatePaymentMethod("card");
  await cardPresenter.create();

  assert.equal(card.voucherIntents.calls.length, 0);
  assert.match(
    card.actionStore.createdCandidates[0]?.intentFingerprint ?? "",
    /^sha256:[0-9a-f]{64}$/,
  );
});

test("Approved/backend pending 后即使重建 runtime 并修改 note，也只重放冻结旧 command", async () => {
  const harness = createHarness({ createFailsOnce: true });
  const first = harness.runtime.createPresenter();
  fillCreate(first);
  first.setCreateNote("Original note");
  await first.create();
  first.destroy();

  const recovered = harness.rebuildRuntime().createPresenter();
  fillCreate(recovered);
  recovered.setCreateNote("Changed note");
  await recovered.create();

  assert.equal(harness.payments.charges, 1);
  assert.equal(harness.api.createCalls.length, 2);
  assert.equal(harness.api.createCalls[0]?.note, "Original note");
  assert.equal(harness.api.createCalls[1]?.note, "Original note");
});

test("mutation 回包必须关联冻结 action、terminal、approved payment/refund 与目标状态", async () => {
  const mismatchedCreate = createHarness({
    createResult: details({
      installmentGuid: "90000000-0000-4000-8000-000000000001",
    }),
  });
  const createPresenter = mismatchedCreate.runtime.createPresenter();
  fillCreate(createPresenter);
  await createPresenter.create();
  assert.equal(
    createPresenter.getState().statusCode,
    "payment-recovery-required",
  );
  assert.equal(mismatchedCreate.cart.clearCalls, 0);

  const missingRepayment = createHarness({
    appendResultMode: "missing-payment",
  });
  const repaymentPresenter = missingRepayment.runtime.createPresenter();
  await repaymentPresenter.load();
  await repaymentPresenter.select("installment-1");
  fillVoucherRepayment(repaymentPresenter);
  await repaymentPresenter.addRepayment();
  assert.equal(
    repaymentPresenter.getState().statusCode,
    "payment-recovery-required",
  );

  const invalidCancel = createHarness({
    cancelResultMode: "active-without-refund",
  });
  const cancelPresenter = invalidCancel.runtime.createPresenter();
  await cancelPresenter.select("installment-1");
  await cancelPresenter.cancelWithRefund();
  assert.equal(
    cancelPresenter.getState().statusCode,
    "payment-recovery-required",
  );
});

test("cancel/refund 只把 durable action 交给支付 port，不从脱敏详情伪造原卡引用", async () => {
  const harness = createHarness();
  const presenter = harness.runtime.createPresenter();
  await presenter.select("installment-1");
  await presenter.cancelWithRefund();

  const action = harness.payments.actions[0];
  assert.deepEqual(
    Object.keys(action ?? {}).sort(),
    [
      "actionId",
      "amountCents",
      "idempotencyKey",
      "installmentGuid",
      "kind",
      "method",
      "paymentGuid",
    ],
  );
  assert.equal(action?.kind, "cancel-refund");
  assertOrdered(harness.events, [
    "action-create",
    "cancel-claim-create",
    "cancel-claim-begin",
    "payments-begin",
    "cancel-claim-commit",
    "action-complete",
  ]);
  assert.equal(harness.api.cancelCalls.length, 1);
  assert.equal(
    harness.api.cancelCalls[0]?.idempotencyKey,
    action?.idempotencyKey,
  );
  assert.equal(harness.api.cancelCalls[0]?.refunds.length, 1);
  assert.equal(
    harness.api.cancelCalls[0]?.refunds[0]?.originalPaymentGuid,
    "20000000-0000-4000-8000-000000000001",
  );
});

test("取消退款完成后才打印退款券；打印异常不影响取消结果", async () => {
  for (const failPrint of [false, true]) {
    const prints: string[] = [];
    let harnessEvents: string[] = [];
    const harness = createHarness({
      refundVoucherPrinter: {
        async printAfterCancel(installmentGuid, orderLabel) {
          // 打印发生在本地 action 完成之后，取消事实已落定。
          assert.ok(harnessEvents.includes("action-complete"));
          prints.push(`${installmentGuid}|${orderLabel}`);
          if (failPrint) throw new Error("printer offline");
        },
      },
    });
    harnessEvents = harness.events;
    const presenter = harness.runtime.createPresenter();
    await presenter.select("installment-1");
    await presenter.cancelWithRefund();

    assert.equal(prints.length, 1, `failPrint=${failPrint}`);
    assert.match(prints[0]!, /^installment-1\|/u);
    assert.equal(harness.api.cancelCalls.length, 1);
    assert.ok(harness.events.includes("action-complete"));
    assert.equal(presenter.getState().statusCode, "cancel-complete", `failPrint=${failPrint}`);
  }
});

test("取消 claim 指纹与后端/WPF 共享固定 golden vector，并忽略输入付款顺序", async () => {
  const installmentGuid = "11111111-1111-4111-8111-111111111111";
  const sourceGuids = [
    "40000000-0000-4000-8000-000000000001",
    "20000000-0000-4000-8000-000000000001",
    "30000000-0000-4000-8000-000000000001",
  ];
  const sourceAmounts = [725, 2_000, 1_050];
  const harness = createHarness({
    paymentsFactory: (actionStore, events) =>
      new FixedRefundPayments(actionStore, events, (actionId) =>
        sourceGuids.map((sourceGuid, index) => ({
          refund: {
            paymentGuid: `10000000-0000-4000-8000-00000000000${index + 1}`,
            method: "voucher" as const,
            amountCents: sourceAmounts[index]!,
            reference: `VOUCHER_REFUND:RV-${index + 1}`,
            cardTransactions: [],
            idempotencyKey: `${actionId}:refund:${sourceGuid}`,
          },
          originalTenderEvidenceId: `evidence-${index + 1}`,
          refundAttemptId: `refund-attempt-${index + 1}`,
          sourceAttemptId: `source-attempt-${index + 1}`,
          sourcePaymentGuid: sourceGuid,
        })),
      ),
  });
  harness.api.detailsResponse = details({
    installmentGuid,
    payments: [
      {
        ...recordedPayment({
          paymentGuid: "40000000-0000-4000-8000-000000000001",
          method: "voucher",
          amountCents: 725,
          reference: null,
          reservationToken: null,
          cardTransactions: [],
          idempotencyKey: "voucher-payment",
        }),
      },
      {
        ...recordedPayment({
          paymentGuid: "20000000-0000-4000-8000-000000000001",
          method: "cash",
          amountCents: 2_000,
          reference: null,
          reservationToken: null,
          cardTransactions: [],
          idempotencyKey: "cash-payment",
        }),
      },
      {
        ...recordedPayment({
          paymentGuid: "30000000-0000-4000-8000-000000000001",
          method: "card",
          amountCents: 1_050,
          reference: null,
          reservationToken: null,
          cardTransactions: [],
          idempotencyKey: "card-payment",
        }),
      },
    ],
  });

  // 含刷卡原付款只能退代金券；指纹仍按原付款方式计算，黄金向量保持不变。
  await workflowOf(harness.runtime.createPresenter()).cancelWithRefund({
    installmentGuid,
    reason: null,
    refundMode: "voucher",
  });

  const command = harness.actionStore.createdCandidates[0]?.command;
  assert.ok(command && command.kind === "cancel-refund");
  assert.equal(command.refundMode, "voucher");
  assert.equal(
    command.refundPlanFingerprint,
    "sha256:e71e70a0dde391c395f87e43cbeb12056488ad6fbbd76622ba77761cf2b816e4",
  );
  assert.equal(harness.api.cancelClaimCreateCalls[0]?.refundMode, "voucher");
  assert.equal(
    harness.api.cancelClaimCreateCalls[0]?.refundPlanFingerprint,
    command.refundPlanFingerprint,
  );
  assert.equal(
    harness.hashMaterials[0],
    '{"installmentGuid":"11111111-1111-4111-8111-111111111111","payments":[["20000000-0000-4000-8000-000000000001","cash",2000],["30000000-0000-4000-8000-000000000001","card",1050],["40000000-0000-4000-8000-000000000001","voucher",725]]}',
  );
});

test("原路退遇到刷卡原付款时在建 action/claim 前失败关闭并提示改选退代金券", async () => {
  const harness = createHarness();
  harness.api.detailsResponse = details({
    payments: [
      recordedPayment({
        paymentGuid: "30000000-0000-4000-8000-000000000001",
        method: "card",
        amountCents: 2_000,
        reference: null,
        reservationToken: null,
        cardTransactions: [],
        idempotencyKey: "card-payment",
      }),
    ],
  });

  await assert.rejects(
    workflowOf(harness.runtime.createPresenter()).cancelWithRefund({
      installmentGuid: "installment-1",
      reason: null,
      refundMode: "original-route",
    }),
    (error: unknown) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "refund-method-unsupported",
  );
  assert.equal(harness.actionStore.createdCandidates.length, 0);
  assert.equal(harness.api.cancelClaimCreateCalls.length, 0);
  assert.equal(harness.payments.beginCalls, 0);
});

test("服务端 INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED 映射为可读错误并释放本地 Created", async () => {
  const harness = createHarness({
    cancelClaimCreateErrorCode: "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED",
  });
  const presenter = harness.runtime.createPresenter();
  await presenter.select("installment-1");
  await presenter.cancelWithRefund();

  assert.equal(
    presenter.getState().statusCode,
    "cancel-refund-method-unsupported",
  );
  assert.equal(harness.payments.beginCalls, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("旧服务端丢弃 refundMode 时不 begin-refund、不签发退款券，释放 claim 并失败关闭", async () => {
  const harness = createHarness();
  harness.api.cancelClaimRefundModeOverride = "original-route";

  await assert.rejects(
    workflowOf(harness.runtime.createPresenter()).cancelWithRefund({
      installmentGuid: "installment-1",
      reason: null,
      refundMode: "voucher",
    }),
    (error: unknown) =>
      error instanceof InstallmentWorkflowError &&
      error.code === "refund-mode-unsupported",
  );
  assert.equal(harness.api.cancelClaimCreateCalls[0]?.refundMode, "voucher");
  assert.equal(harness.events.includes("cancel-claim-begin"), false);
  assert.equal(harness.events.includes("payments-begin"), false);
  assert.ok(harness.events.includes("cancel-claim-resolve:Released"));
  assert.equal(harness.payments.beginCalls, 0);
  assert.equal(harness.api.cancelCalls.length, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("退代金券模式：现金、刷卡、代金券原付款全部以退款券退回，commit 带 VOUCHER_REFUND 前缀", async () => {
  const sources = [
    { guid: "20000000-0000-4000-8000-000000000001", method: "cash" as const, amountCents: 2_000 },
    { guid: "30000000-0000-4000-8000-000000000001", method: "card" as const, amountCents: 1_050 },
    { guid: "40000000-0000-4000-8000-000000000001", method: "voucher" as const, amountCents: 725 },
  ];
  const harness = createHarness({
    paymentsFactory: (actionStore, events) =>
      new FixedRefundPayments(actionStore, events, (actionId) =>
        sources.map((source, index) => ({
          refund: {
            paymentGuid: `10000000-0000-4000-8000-00000000000${index + 1}`,
            method: "voucher" as const,
            amountCents: source.amountCents,
            reference: `VOUCHER_REFUND:RV-${index + 1}`,
            cardTransactions: [],
            idempotencyKey: `${actionId}:refund:${source.guid}`,
          },
          originalTenderEvidenceId: `evidence-${index + 1}`,
          refundAttemptId: `refund-attempt-${index + 1}`,
          sourceAttemptId: `source-attempt-${index + 1}`,
          sourcePaymentGuid: source.guid,
        })),
      ),
  });
  harness.api.detailsResponse = details({
    payments: sources.map((source) =>
      recordedPayment({
        paymentGuid: source.guid,
        method: source.method,
        amountCents: source.amountCents,
        reference: null,
        reservationToken: null,
        cardTransactions: [],
        idempotencyKey: `${source.method}-payment`,
      }),
    ),
  });

  const result = await workflowOf(harness.runtime.createPresenter()).cancelWithRefund({
    installmentGuid: "installment-1",
    reason: null,
    refundMode: "voucher",
  });

  assert.equal(result.status, "Cancelled");
  const committed = harness.api.cancelCalls[0]?.refunds ?? [];
  assert.equal(committed.length, 3);
  assert.deepEqual(
    committed.map((refund) => [refund.method, refund.amountCents, refund.originalPaymentGuid, refund.reference]),
    [
      ["voucher", 2_000, sources[0]!.guid, "VOUCHER_REFUND:RV-1"],
      ["voucher", 1_050, sources[1]!.guid, "VOUCHER_REFUND:RV-2"],
      ["voucher", 725, sources[2]!.guid, "VOUCHER_REFUND:RV-3"],
    ],
  );
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("退代金券模式下支付端返回非券退款或券退款缺前缀时不提交 commit", async () => {
  for (const invalid of [
    { method: "cash" as const, reference: null },
    { method: "voucher" as const, reference: "RV-1" },
  ]) {
    const harness = createHarness({
      paymentsFactory: (actionStore, events) =>
        new FixedRefundPayments(actionStore, events, (actionId) => [{
          refund: {
            paymentGuid: "10000000-0000-4000-8000-000000000001",
            method: invalid.method,
            amountCents: 2_000,
            reference: invalid.reference,
            cardTransactions: [],
            idempotencyKey: `${actionId}:refund:20000000-0000-4000-8000-000000000001`,
          },
          originalTenderEvidenceId: "evidence-1",
          refundAttemptId: "refund-attempt-1",
          sourceAttemptId: "source-attempt-1",
          sourcePaymentGuid: "20000000-0000-4000-8000-000000000001",
        }]),
    });

    await assert.rejects(
      workflowOf(harness.runtime.createPresenter()).cancelWithRefund({
        installmentGuid: "installment-1",
        reason: null,
        refundMode: "voucher",
      }),
      (error: unknown) =>
        error instanceof InstallmentWorkflowError &&
        error.code === "payment-recovery-required",
    );
    assert.equal(harness.events.includes("cancel-claim-commit"), false);
    assert.equal(harness.api.cancelCalls.length, 0);
  }
});

test("cancel claim busy 在任何退款 provider 前原子释放本地 Created", async () => {
  const harness = createHarness({ cancelClaimCreateErrorCode: "INSTALLMENT_MUTATION_BUSY" });
  const presenter = harness.runtime.createPresenter();
  await presenter.select("installment-1");
  await presenter.cancelWithRefund();
  assert.equal(presenter.getState().statusCode, "conflict");
  assert.equal(harness.payments.beginCalls, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
  assert.equal(harness.api.cancelCalls.length, 0);
});

test("cancel claim commit 回包丢失时 GET 同一 claim，不重复退款 provider", async () => {
  const harness = createHarness({ cancelClaimCommitTransportFailsOnce: true });
  const presenter = harness.runtime.createPresenter();
  await presenter.select("installment-1");
  await presenter.cancelWithRefund();
  assert.equal(presenter.getState().statusCode, "cancel-complete");
  assert.equal(harness.payments.beginCalls, 1);
  assert.equal(harness.events.filter((event) => event === "cancel-claim-commit").length, 1);
  assert.equal(harness.events.filter((event) => event === "payments-begin").length, 1);
  assert.ok(harness.events.includes("cancel-claim-get"));
});

test("跨设备退款取消 commit 回包丢失时接受原单设备并恢复同一退款流水", async () => {
  const harness = createHarness({ cancelClaimCommitTransportFailsOnce: true });
  harness.api.detailsResponse = details({
    deviceCode: "IPAD-2",
    payments: [
      {
        paymentGuid: "20000000-0000-4000-8000-000000000001",
        method: "cash",
        amountCents: 2_000,
        status: "Recorded",
        recordedAtIso: "2026-07-28T08:00:00.000Z",
        cashierId: "CASHIER-1",
        deviceCode: DEVICE_CODE,
        cardType: null,
        maskedCardNumber: null,
      },
    ],
  });
  harness.api.capabilities = Object.freeze({
    ...harness.api.capabilities,
    crossDeviceCancelRefundEnabled: true,
  }) as InstallmentRepaymentCapabilities;

  const result = await workflowOf(
    harness.runtime.createPresenter(),
  ).cancelWithRefund({
    installmentGuid: "installment-1",
    reason: null,
  });

  assert.equal(result.status, "Cancelled");
  assert.equal(result.deviceCode, "IPAD-2");
  assert.equal(harness.payments.beginCalls, 1);
  assert.equal(harness.api.cancelCalls.length, 1);
  assert.ok(harness.events.includes("cancel-claim-get"));
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
});

test("cancel ProviderPending/Unknown 恢复时中央 claim 404 不得创建新 claim 或重退", async () => {
  for (const state of ["ProviderPending", "Unknown"] as const) {
    const harness = createHarness();
    const actionId = state === "ProviderPending"
      ? "60000000-0000-4000-8000-000000000001"
      : "60000000-0000-4000-8000-000000000002";
    await harness.actionStore.createIfNone({
      action: { actionId, idempotencyKey: actionId, kind: "cancel-refund", installmentGuid: "installment-1", paymentGuid: null, method: null, amountCents: null },
      command: {
        kind: "cancel-refund",
        installmentGuid: "installment-1",
        deviceCode: DEVICE_CODE,
        cashierId: "CASHIER-1",
        cashierName: "Cashier One",
        cancelledAtIso: "2026-08-04T01:00:00.000Z",
        reason: null,
        idempotencyKey: actionId,
        refundPlanFingerprint: `sha256:${"a".repeat(64)}`,
      },
      deviceCode: DEVICE_CODE,
      storeCode: STORE_CODE,
      intentFingerprint: `sha256:${"b".repeat(64)}`,
      state,
    });
    const beforeProvider = harness.payments.beginCalls;
    await assert.rejects(
      workflowOf(harness.runtime.createPresenter()).recoverBlocking(),
      /Cancel claim is missing/,
    );
    assert.equal(harness.payments.beginCalls, beforeProvider);
    assert.equal(harness.events.filter((event) => event === "cancel-claim-create").length, 0);
    assert.equal(harness.events.filter((event) => event === "cancel-claim-begin").length, 0);
  }
});

test("cancel claim Committed 回包的门店或取消状态不匹配时不得本地 complete", async () => {
  const harness = createHarness();
  harness.api.cancelClaimCommitDetailsOverride = details({
    storeCode: "STORE-OTHER",
    status: "Cancelled",
    cancellationInfo: {
      kind: "RefundCancel",
      cancelledAtIso: "2026-08-04T01:00:00.000Z",
      cancelledBy: "Cashier One",
      reason: null,
    },
  });
  const presenter = harness.runtime.createPresenter();
  await presenter.select("installment-1");
  await presenter.cancelWithRefund();
  assert.equal(presenter.getState().statusCode, "payment-recovery-required");
  assert.equal(await harness.runtime.hasRecoveryRequired(), true);
  assert.ok(!harness.events.includes("action-complete"));
});

test("部分退款成功后随后的拒绝必须双边 Unknown，并仅恢复同一 action", async () => {
  const harness = createHarness({
    paymentOutcomes: ["declined", "approved"],
    cancelAllRefundsDeclined: false,
  });
  const presenter = harness.runtime.createPresenter();
  await presenter.select("installment-1");
  await presenter.cancelWithRefund();
  assert.equal(presenter.getState().statusCode, "payment-recovery-required");
  assert.equal(harness.api.cancelClaim?.status, "Unknown");
  const originalActionId = harness.payments.actions[0]?.actionId;
  await workflowOf(harness.runtime.createPresenter()).recoverBlocking();
  assert.equal(harness.payments.actions[1]?.actionId, originalActionId);
  assert.equal(harness.payments.authorizeCalls, 1);
  assert.equal(harness.payments.recoverCalls, 1);
});

test("首次还款在 provider 前重新读取详情并精确拒绝 GUID、门店或设备 scope 不匹配", async () => {
  const mismatches: readonly Partial<InstallmentDetails>[] = [
    { installmentGuid: "installment-other" },
    { storeCode: "STORE-OTHER" },
    { deviceCode: "DEVICE-OTHER" },
  ];

  for (const mismatch of mismatches) {
    const harness = createHarness();
    const presenter = harness.runtime.createPresenter();
    await presenter.load();
    await presenter.select("installment-1");
    harness.api.detailsResponse = details(mismatch);
    fillVoucherRepayment(presenter);

    await presenter.addRepayment();

    assert.equal(harness.api.detailsCalls.length, 2);
    assert.equal(harness.payments.beginCalls, 0);
    assert.equal(harness.actionStore.createdCandidates.length, 0);
    assert.equal(await harness.runtime.hasRecoveryRequired(), false);
    assert.equal(presenter.getState().statusCode, "conflict");
    assert.equal(
      harness.actionStore.transitionCalls.some(
        (call) => call.nextState === "ProviderPending",
      ),
      false,
    );
  }
});

test("首次还款或退款的详情复核读取失败时 fail closed，provider 零调用", async () => {
  for (const operation of ["repayment", "refund"] as const) {
    for (const failure of ["transport", "missing"] as const) {
      const harness = createHarness();
      const presenter = harness.runtime.createPresenter();
      await presenter.load();
      await presenter.select("installment-1");
      if (failure === "transport") {
        harness.api.detailsError = new HbposApiError("scope lookup failed", {
          kind: "transport",
        });
      } else {
        harness.api.detailsResponse = null;
      }

      if (operation === "repayment") {
        fillVoucherRepayment(presenter);
        await presenter.addRepayment();
      } else {
        await presenter.cancelWithRefund();
      }

      assert.equal(harness.api.detailsCalls.length, 2);
      assert.equal(harness.payments.beginCalls, 0);
      assert.equal(harness.api.cancelCalls.length, 0);
      assert.equal(harness.actionStore.createdCandidates.length, 0);
      assert.equal(await harness.runtime.hasRecoveryRequired(), false);
      assert.equal(
        presenter.getState().statusCode,
        failure === "transport" ? "online-required" : "conflict",
      );
    }
  }
});

test("券还款 scope 预检失败时不生成候选券材料或 durable action", async () => {
  const harness = createHarness();
  const presenter = harness.runtime.createPresenter();
  await presenter.load();
  await presenter.select("installment-1");
  harness.api.detailsResponse = details({ deviceCode: "DEVICE-OTHER" });
  fillVoucherRepayment(presenter);

  await presenter.addRepayment();

  assert.equal(harness.voucherIntents.calls.length, 0);
  assert.equal(harness.actionStore.createdCandidates.length, 0);
  assert.equal(harness.payments.beginCalls, 0);
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
  assert.equal(presenter.getState().statusCode, "conflict");
});

test("首次退款在 provider 前拒绝跨设备分期，但同设备正常流不受影响", async () => {
  const denied = createHarness();
  const deniedPresenter = denied.runtime.createPresenter();
  await deniedPresenter.select("installment-1");
  denied.api.detailsResponse = details({ deviceCode: "DEVICE-OTHER" });

  await deniedPresenter.cancelWithRefund();

  assert.equal(denied.payments.beginCalls, 0);
  assert.equal(denied.api.cancelCalls.length, 0);
  assert.equal(denied.actionStore.createdCandidates.length, 0);
  assert.equal(await denied.runtime.hasRecoveryRequired(), false);
  assert.equal(deniedPresenter.getState().statusCode, "conflict");

  const allowed = createHarness();
  const allowedPresenter = allowed.runtime.createPresenter();
  await allowedPresenter.select("installment-1");
  await allowedPresenter.cancelWithRefund();

  assert.equal(allowed.api.detailsCalls.length, 2);
  assert.equal(allowed.payments.beginCalls, 1);
  assert.equal(allowed.api.cancelCalls.length, 1);
});

test("cancel claim 返回异常详情后恢复只读取同一 claim，不再重退 provider", async () => {
  const harness = createHarness({
    cancelResultMode: "active-without-refund",
  });
  const presenter = harness.runtime.createPresenter();
  await presenter.load();
  await presenter.select("installment-1");

  await presenter.cancelWithRefund();
  assert.equal(presenter.getState().statusCode, "payment-recovery-required");
  assert.equal(harness.payments.beginCalls, 1);
  assert.equal(harness.api.detailsCalls.length, 2);

  harness.api.detailsError = new Error("details endpoint unavailable");
  await presenter.recoverBlocking();

  assert.equal(harness.payments.beginCalls, 1);
  assert.equal(harness.api.detailsCalls.length, 2);
  assert.equal(harness.api.cancelCalls.length, 1);
});

test("create 在独占 lease 内复核 revision；过期草稿不会触发支付", async () => {
  const harness = createHarness();
  const presenter = harness.runtime.createPresenter();
  fillCreate(presenter);
  harness.cart.bumpRevisionWithoutNotification();

  await presenter.create();
  assert.equal(harness.payments.beginCalls, 0);
  assert.equal(harness.api.createCalls.length, 0);
  assert.equal(presenter.getState().statusCode, "conflict");
});

test("写操作在执行时复核 online、权限与 cashier lease", async () => {
  const offline = createHarness();
  const offlinePresenter = offline.runtime.createPresenter();
  fillCreate(offlinePresenter);
  offline.online.value = false;
  await offlinePresenter.create();
  assert.equal(offline.payments.beginCalls, 0);
  assert.equal(offlinePresenter.getState().statusCode, "online-required");

  const expired = createHarness();
  const expiredPresenter = expired.runtime.createPresenter();
  fillCreate(expiredPresenter);
  expired.currentCashier.clear();
  await expiredPresenter.create();
  assert.equal(expired.payments.beginCalls, 0);
  assert.equal(expired.api.createCalls.length, 0);

  const denied = createHarness({ permissions: [INSTALLMENTS_VIEW_PERMISSION] });
  const deniedPresenter = denied.runtime.createPresenter();
  deniedPresenter.showCreate();
  assert.equal(deniedPresenter.getState().statusCode, "permission-required");
});

class ScriptedPayments implements InstallmentMutationPaymentPort {
  public beginCalls = 0;
  public authorizeCalls = 0;
  public recoverCalls = 0;
  public confirmCalls = 0;
  public prepareCalls = 0;
  public charges = 0;
  public cashSettlementState: "Prepared" | "Approved" | "Missing" = "Prepared";
  public readonly actions: InstallmentPaymentAction[] = [];
  public readonly prepareBindings: {
    provider: string;
    providerAttemptId: string;
  }[] = [];
  private readonly chargedActionIds = new Set<string>();

  private readonly outcomes: ("approved" | "declined" | "throw" | "unknown")[];

  public constructor(
    outcomes: readonly ("approved" | "declined" | "throw" | "unknown")[],
    private readonly actionStore: MemoryInstallmentActionStore,
    private readonly events: string[],
    private readonly prepareFails = false,
    private readonly cancelAllRefundsDeclined = true,
  ) {
    this.outcomes = [...outcomes];
  }

  public async prepareRepaymentClaim(persistedActionId: string) {
    this.prepareCalls += 1;
    this.events.push("payments-prepare");
    if (this.prepareFails) throw new Error("provider plan unavailable");
    const persisted = this.actionStore.get(persistedActionId);
    if (!persisted || persisted.action.kind !== "repayment") {
      throw new Error("repayment action is required");
    }
    const provider =
      persisted.action.method === "cash"
        ? "cash"
        : persisted.action.method === "voucher"
          ? "voucher"
          : persisted.command.kind === "repayment"
            ? (persisted.command.cardProvider ?? "square")
            : "square";
    const binding = {
      provider,
      providerAttemptId: `attempt:${persisted.action.actionId}`,
    };
    this.prepareBindings.push(binding);
    return binding;
  }

  public beginOrRecover(persistedActionId: string) {
    this.authorizeCalls += 1;
    if (this.actionStore.get(persistedActionId)?.action.method === "cash") {
      this.cashSettlementState = "Approved";
    }
    return this.resolve(persistedActionId);
  }

  public recoverBlocking(persistedActionId: string) {
    this.recoverCalls += 1;
    return this.resolve(persistedActionId);
  }

  public async inspectCashSettlement(_persistedActionId: string) {
    if (this.cashSettlementState === "Missing") {
      throw new Error("cash settlement plan is missing");
    }
    return this.cashSettlementState;
  }

  public async confirmCashRepayment(persistedActionId: string) {
    this.confirmCalls += 1;
    this.events.push("payments-confirm-cash");
    this.cashSettlementState = "Approved";
    return this.resolve(persistedActionId);
  }

  private async resolve(persistedActionId: string) {
    this.beginCalls += 1;
    this.events.push("payments-begin");
    const persisted = this.actionStore.get(persistedActionId);
    if (!persisted) throw new Error("persisted action is required");
    const action = persisted.action;
    this.actions.push(action);
    const outcome =
      this.outcomes.length > 1
        ? this.outcomes.shift()
        : this.outcomes[0] ?? "approved";
    if (outcome === "unknown") return { kind: "unknown" as const };
    if (outcome === "throw") throw new Error("provider transport failed");
    if (outcome === "declined") {
      return action.kind === "cancel-refund"
        ? { kind: "declined" as const, allRefundsDeclined: this.cancelAllRefundsDeclined }
        : { kind: "declined" as const };
    }
    if (!this.chargedActionIds.has(action.actionId)) {
      this.chargedActionIds.add(action.actionId);
      this.charges += 1;
    }
    if (action.kind === "cancel-refund") {
      const refund: InstallmentApprovedRefund = {
        refund: {
          paymentGuid: "10000000-0000-4000-8000-000000000001",
          method: "cash",
          amountCents: 2_000,
          reference: null,
          cardTransactions: [],
          idempotencyKey: `${action.actionId}:refund:20000000-0000-4000-8000-000000000001`,
        },
        originalTenderEvidenceId: "tender-evidence-1",
        refundAttemptId: "refund-attempt-1",
        sourceAttemptId: "source-attempt-1",
        sourcePaymentGuid: "20000000-0000-4000-8000-000000000001",
      };
      return {
        kind: "approved" as const,
        refunds: [refund],
      };
    }
    const payment: InstallmentPaymentCommand = {
      paymentGuid: action.paymentGuid ?? "invalid-payment-guid",
      method: action.method ?? "cash",
      amountCents: action.amountCents ?? 0,
      reference: null,
      reservationToken: null,
      cardTransactions: [],
      idempotencyKey: action.idempotencyKey,
    };
    return { kind: "approved" as const, payment };
  }
}

/** 取消退款专用的固定支付端：按 action 生成既定退款结果，用于退代金券模式断言。 */
class FixedRefundPayments implements InstallmentMutationPaymentPort {
  public beginCalls = 0;

  public constructor(
    private readonly actionStore: MemoryInstallmentActionStore,
    private readonly events: string[],
    private readonly refundsFor: (actionId: string) => InstallmentApprovedRefund[],
  ) {}

  public async prepareRepaymentClaim(): Promise<never> {
    throw new Error("repayment is not supported by this fake");
  }

  public async inspectCashSettlement(): Promise<never> {
    throw new Error("cash settlement is not supported by this fake");
  }

  public async confirmCashRepayment(): Promise<never> {
    throw new Error("cash repayment is not supported by this fake");
  }

  public beginOrRecover(persistedActionId: string) {
    return this.resolve(persistedActionId);
  }

  public recoverBlocking(persistedActionId: string) {
    return this.resolve(persistedActionId);
  }

  private async resolve(persistedActionId: string) {
    this.beginCalls += 1;
    this.events.push("payments-begin");
    const persisted = this.actionStore.get(persistedActionId);
    if (persisted?.action.kind !== "cancel-refund") {
      throw new Error("cancel action is required");
    }
    return {
      kind: "approved" as const,
      refunds: this.refundsFor(persisted.action.actionId),
    };
  }
}

class MemorySnapshotCache {
  public readonly listCalls: { storeCode: string; limit: number; offset: number }[] = [];
  public readonly upsertCalls: { storeCode: string; snapshots: readonly InstallmentSnapshot[] }[] = [];
  public failNextUpsert = false;

  public constructor(private snapshots: readonly InstallmentSnapshot[]) {}

  public async listForStore(storeCode: string, limit: number, offset: number) {
    this.listCalls.push({ storeCode, limit, offset });
    return this.snapshots.slice(offset, offset + limit);
  }

  public async upsertForStore(storeCode: string, snapshots: readonly InstallmentSnapshot[]) {
    this.upsertCalls.push({ storeCode, snapshots });
    if (this.failNextUpsert) {
      this.failNextUpsert = false;
      throw new Error("snapshot cache write failed");
    }
    const incoming = new Map(
      snapshots.map((snapshot) => [snapshot.installmentGuid, snapshot]),
    );
    this.snapshots = [
      ...snapshots,
      ...this.snapshots.filter(
        (snapshot) => !incoming.has(snapshot.installmentGuid),
      ),
    ];
  }

  public all(): readonly InstallmentSnapshot[] {
    return this.snapshots;
  }
}

class RuntimeCashAttemptStore implements InstallmentProviderAttemptStorePort {
  private plan: InstallmentProviderAttemptPlan | null = null;
  public planBindings = 0;
  public cashApprovals = 0;

  public constructor(
    private readonly actionStore: MemoryInstallmentActionStore,
    private readonly events: string[],
  ) {}

  public async loadAction(actionId: string): Promise<PersistedInstallmentAction | null> {
    return this.actionStore.get(actionId);
  }

  public async loadPlan(actionId: string): Promise<InstallmentProviderAttemptPlan | null> {
    return this.plan?.actionId === actionId ? this.plan : null;
  }

  public async bindPlanOrGet(
    candidate: InstallmentProviderAttemptPlan,
  ): Promise<InstallmentProviderAttemptPlan> {
    if (this.plan) return this.plan;
    this.planBindings += 1;
    this.events.push("real-plan-bind");
    this.plan = candidate;
    return candidate;
  }

  public async compareAndUpdateAttempt(_input: Readonly<{
    expected: InstallmentProviderAttemptRecord;
    nextAttempt: PaymentAttempt;
    approvedMaterial?: InstallmentApprovedPaymentMaterial;
  }>): Promise<boolean> {
    throw new Error("cash repayment must not update an online provider attempt");
  }

  public async loadApprovedMaterial(
    _attemptId: string,
  ): Promise<InstallmentApprovedPaymentMaterial | null> {
    return null;
  }

  public async approveCashSettlements(
    actionId: string,
  ): Promise<readonly InstallmentCashSettlement[]> {
    if (!this.plan || this.plan.actionId !== actionId) {
      throw new Error("cash plan is missing");
    }
    this.cashApprovals += 1;
    this.events.push("real-cash-approve");
    this.plan = Object.freeze({
      ...this.plan,
      cashSettlements: Object.freeze(
        this.plan.cashSettlements.map((settlement) =>
          settlement.state === "Approved"
            ? settlement
            : Object.freeze({ ...settlement, state: "Approved" as const }),
        ),
      ),
    });
    return this.plan.cashSettlements;
  }
}

class MemoryInstallmentActionStore implements InstallmentActionStorePort {
  private current: PersistedInstallmentAction | null = null;
  private lifecycleCurrent: PersistedInstallmentLifecycleAction | null = null;
  public failNextLifecycleComplete = false;
  public failNextTransitionTo: InstallmentActionState | null = null;
  public readonly createdCandidates: PersistedInstallmentAction[] = [];
  public readonly lifecycleCandidates: PersistedInstallmentLifecycleAction[] = [];
  public readonly transitionCalls: Parameters<
    InstallmentActionStorePort["transition"]
  >[0][] = [];
  public readonly finalizedCreatedReasons: (
    | "ClaimBusy"
    | "ClaimMismatch"
    | "ClaimReleased"
    | "PaymentMethodUnsupported"
  )[] = [];
  public raceOnNextCreate = false;
  public raceWinner: PersistedInstallmentAction | null = null;
  public failNextRepaymentFinalizer = false;
  public readonly finalizerCalls: Parameters<
    NonNullable<InstallmentActionStorePort["completeCommittedRepaymentWithSnapshot"]>
  >[0][] = [];

  public constructor(private readonly events: string[]) {}

  public getCurrent(): PersistedInstallmentAction | null {
    return this.current;
  }

  public async loadBlocking(): Promise<PersistedInstallmentAction | null> {
    return this.current;
  }

  public async createIfNone(candidate: PersistedInstallmentAction) {
    this.events.push("action-create");
    if (this.lifecycleCurrent) {
      throw new Error("lifecycle action blocks payment action");
    }
    if (this.current) {
      return { created: false, action: this.current };
    }
    if (this.raceOnNextCreate) {
      this.raceOnNextCreate = false;
      const winnerActionId = "f0000000-0000-4000-8000-000000000001";
      const winnerPaymentGuid =
        candidate.action.paymentGuid === null
          ? null
          : "f0000000-0000-4000-8000-000000000002";
      this.raceWinner = Object.freeze({
        ...candidate,
        action: Object.freeze({
          ...candidate.action,
          actionId: winnerActionId,
          idempotencyKey: winnerActionId,
          paymentGuid: winnerPaymentGuid,
        }),
        intentFingerprint: `sha256:${"f".repeat(64)}`,
      });
      this.current = this.raceWinner;
      return { created: false, action: this.raceWinner };
    }
    this.createdCandidates.push(candidate);
    this.current = candidate;
    return { created: true, action: candidate };
  }

  public async loadLifecycleBlocking() {
    return this.lifecycleCurrent;
  }

  public async createLifecycleIfNone(
    candidate: PersistedInstallmentLifecycleAction,
  ) {
    this.events.push("lifecycle-create");
    if (this.current) {
      throw new Error("payment action blocks lifecycle action");
    }
    if (this.lifecycleCurrent) {
      return { created: false, action: this.lifecycleCurrent };
    }
    this.lifecycleCandidates.push(candidate);
    this.lifecycleCurrent = candidate;
    return { created: true, action: candidate };
  }

  public async completeLifecycle(
    input: Parameters<InstallmentActionStorePort["completeLifecycle"]>[0],
  ) {
    if (this.lifecycleCurrent?.operationGuid !== input.operationGuid) {
      throw new Error("lifecycle action not found");
    }
    if (this.failNextLifecycleComplete) {
      this.failNextLifecycleComplete = false;
      throw new Error("lifecycle complete failed");
    }
    this.events.push("lifecycle-complete");
    this.lifecycleCurrent = null;
  }

  public async finalizeCreatedFailure(
    input: Parameters<
      NonNullable<InstallmentActionStorePort["finalizeCreatedFailure"]>
    >[0],
  ) {
    const current = this.requireCurrent(input.actionId);
    if (current.state !== "Created") {
      throw new Error("created failure state conflict");
    }
    this.events.push(`action-finalize:${input.reason}`);
    this.finalizedCreatedReasons.push(input.reason);
    this.current = null;
  }

  public async transition(input: Parameters<InstallmentActionStorePort["transition"]>[0]) {
    this.transitionCalls.push(input);
    this.events.push(`action-transition:${input.nextState}`);
    const current = this.requireCurrent(input.actionId);
    if (current.state !== input.expectedState) {
      throw new Error(
        `state conflict: expected ${input.expectedState}, got ${current.state}`,
      );
    }
    if (this.failNextTransitionTo === input.nextState) {
      this.failNextTransitionTo = null;
      throw new Error("installment action transition failed");
    }
    this.current = Object.freeze({
      ...current,
      state: input.nextState,
    });
    return this.current;
  }

  public async decline(input: Parameters<InstallmentActionStorePort["decline"]>[0]) {
    const current = this.requireCurrent(input.actionId);
    if (current.state !== input.expectedState) {
      throw new Error("decline state conflict");
    }
    this.events.push("action-decline");
    this.current = null;
  }

  public async complete(input: Parameters<InstallmentActionStorePort["complete"]>[0]) {
    const current = this.requireCurrent(input.actionId);
    if (current.state !== input.expectedState) {
      throw new Error("complete state conflict");
    }
    this.events.push("action-complete");
    this.current = null;
  }

  public async completeCommittedRepaymentWithSnapshot(
    input: Parameters<
      NonNullable<InstallmentActionStorePort["completeCommittedRepaymentWithSnapshot"]>
    >[0],
    snapshotRepository: Parameters<
      NonNullable<InstallmentActionStorePort["completeCommittedRepaymentWithSnapshot"]>
    >[1],
  ) {
    this.finalizerCalls.push(input);
    if (this.failNextRepaymentFinalizer) {
      this.failNextRepaymentFinalizer = false;
      throw new Error("atomic repayment finalizer failed");
    }
    const current = this.requireCurrent(input.actionId);
    if (current.state !== input.expectedState) {
      throw new Error("atomic repayment finalizer state conflict");
    }
    // 只证明 runtime 把生产 snapshot repository 交给原子 finalizer；不模拟事务内部写入。
    void snapshotRepository;
    this.events.push("action-complete-with-snapshot");
    this.current = null;
  }

  public get(actionId: string): PersistedInstallmentAction | null {
    return this.current?.action.actionId === actionId ? this.current : null;
  }

  private requireCurrent(actionId: string): PersistedInstallmentAction {
    if (!this.current || this.current.action.actionId !== actionId) {
      throw new Error("persisted action not found");
    }
    return this.current;
  }
}

type VoucherIntentStageInput = Parameters<
  InstallmentVoucherIntentVaultPort["stage"]
>[0];

class RecordingVoucherIntentVault
  implements InstallmentVoucherIntentVaultPort
{
  public readonly calls: VoucherIntentStageInput[] = [];
  public failure: Error | null = null;

  public constructor(private readonly events: string[]) {}

  public async stage(input: VoucherIntentStageInput): Promise<void> {
    this.events.push("voucher-stage");
    this.calls.push(Object.freeze({ ...input }));
    if (this.failure) throw this.failure;
  }
}

class FakeCart {
  public clearCalls = 0;
  private readonly listeners = new Set<() => void>();
  private revision = 1;

  public read = () => this.snapshot();

  public subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public async runExclusive<T>(operation: (lease: { read(): unknown; clearAfterCommittedOrder(orderGuid: string): unknown }) => T | Promise<T>): Promise<T> {
    return operation({
      read: () => this.snapshot(),
      clearAfterCommittedOrder: () => {
        this.clearCalls += 1;
        this.revision += 1;
        for (const listener of this.listeners) listener();
      },
    });
  }

  public bumpRevisionWithoutNotification(): void {
    this.revision += 1;
  }

  private snapshot() {
    const cart = {
      revision: this.revision,
      mode: "sale",
      lines: [
        {
          lineId: "line-1",
          productCode: "P1",
          itemNumber: "ITEM-1",
          lookupCode: "P1",
          displayName: "Product 1",
          quantity: "1",
          unitPrice: { currency: "AUD", cents: 5_000 },
          discount: { currency: "AUD", cents: 0 },
          actualAmount: { currency: "AUD", cents: 5_000 },
          priceSource: "catalog",
          kind: "sale",
          returnSourceKey: null,
          originalOrderGuid: null,
          originalOrderDetailGuid: null,
        },
      ],
      subtotal: { currency: "AUD", cents: 5_000 },
      discount: { currency: "AUD", cents: 0 },
      actualAmount: { currency: "AUD", cents: 5_000 },
    };
    return { cart, pricingState: { revision: this.revision } } as unknown;
  }
}

class ScriptedApi {
  public listCalls = 0;
  public readonly listInputs: unknown[] = [];
  public listError: Error | null = null;
  public detailsError: Error | null = null;
  public detailsResponse: InstallmentDetails | null = details({
    payments: [
      {
        paymentGuid: "20000000-0000-4000-8000-000000000001",
        method: "cash",
        amountCents: 2_000,
        status: "Recorded",
        recordedAtIso: "2026-07-28T08:00:00.000Z",
        cashierId: "CASHIER-1",
        deviceCode: DEVICE_CODE,
        cardType: null,
        maskedCardNumber: null,
      },
    ],
  });
  public readonly detailsCalls: string[] = [];
  public readonly createCalls: InstallmentCreateCommand[] = [];
  public readonly cancelCalls: InstallmentCancelCommand[] = [];
  public readonly voidCalls: { installmentGuid: string }[] = [];
  public readonly pickupCalls: { installmentGuid: string }[] = [];
  public appendCalls = 0;
  public readonly createClaimCalls: InstallmentRepaymentClaimCreateCommand[] = [];
  public readonly beginClaimCalls: InstallmentRepaymentClaimBeginProviderCommand[] = [];
  public readonly prepareProviderCalls: InstallmentRepaymentClaimPrepareProviderCommand[] = [];
  public readonly getClaimCalls: InstallmentRepaymentClaimIdentity[] = [];
  public readonly resolveClaimCalls: InstallmentRepaymentClaimResolveCommand[] = [];
  public readonly commitClaimCalls: InstallmentRepaymentClaimCommitCommand[] = [];
  public claim: InstallmentRepaymentClaim | null = null;
  public cancelClaim: InstallmentCancelClaim | null = null;
  public cancelClaimCommitDetailsOverride: InstallmentDetails | null = null;
  /** 模拟旧服务端丢弃 refundMode：回包退款方式固定为该值。 */
  public cancelClaimRefundModeOverride: InstallmentCancelClaim["refundMode"] | null = null;
  public readonly cancelClaimCreateCalls: InstallmentCancelClaimCreateCommand[] = [];
  public voidTransportFailsOnce = false;
  public pickupTransportFailsOnce = false;
  public lifecycleCashierNameOverride: string | undefined;
  public lifecycleVoidReasonOverride: string | undefined;
  public capabilities: InstallmentRepaymentCapabilities;
  public claimCreateErrorCode: string | null;
  public claimCreateErrorStatus: number;
  private claimCommitTransportFailsOnce: boolean;
  private claimResolveTransportFailsOnce: boolean;
  private capabilityErrorStatus: number | null;
  private claimBeginErrorCode: string | null;
  private claimCreateFailsOnce: "server" | "transport" | null;
  private claimGetErrorCode: string | null;
  private claimCreateCommand: InstallmentRepaymentClaimCreateCommand | null = null;
  private cancelClaimCreateErrorCode: string | null = null;
  private cancelClaimCommitTransportFailsOnce = false;

  public constructor(
    private createFailsOnce: boolean,
    private readonly createResult: InstallmentDetails | null,
    private readonly appendResultMode: "approved-payment" | "missing-payment",
    private readonly cancelResultMode:
      | "cancelled-with-refund"
      | "active-without-refund",
    private readonly events: string[],
    options: Readonly<{
      crossDeviceRepaymentEnabled: boolean;
      repaymentClaimsSupported: boolean;
      repaymentClaimsRequired: boolean;
      repaymentClaimPrepareProviderV1?: boolean;
      capabilityErrorStatus: number | null;
      claimCreateErrorCode: string | null;
      claimCreateErrorStatus: number;
      cardRepaymentSupported: boolean;
      claimBeginErrorCode: string | null;
      claimCreateFailsOnce: "server" | "transport" | null;
      claimGetErrorCode: string | null;
      claimCommitTransportFailsOnce: boolean;
      claimResolveTransportFailsOnce: boolean;
      cancelClaimCreateErrorCode?: string | null;
      cancelClaimCommitTransportFailsOnce?: boolean;
    }>,
  ) {
    this.capabilities = {
      repaymentClaimsSupported: options.repaymentClaimsSupported,
      repaymentClaimsRequired: options.repaymentClaimsRequired,
      repaymentClaimPrepareProviderV1:
        options.repaymentClaimPrepareProviderV1 ?? false,
      cardRepaymentSupported: options.cardRepaymentSupported,
      crossDeviceRepaymentEnabled: options.crossDeviceRepaymentEnabled,
      crossDeviceCancelRefundEnabled: false,
      crossDeviceVoidEnabled: false,
      crossDevicePickupEnabled: false,
      preparedClaimTtlSeconds: 300,
      cancelClaimsSupported: true,
      cancelClaimsRequired: false,
      cancelPreparedClaimTtlSeconds: 120,
    };
    this.claimCreateErrorCode = options.claimCreateErrorCode;
    this.claimCreateErrorStatus = options.claimCreateErrorStatus;
    this.claimBeginErrorCode = options.claimBeginErrorCode;
    this.claimCreateFailsOnce = options.claimCreateFailsOnce;
    this.claimGetErrorCode = options.claimGetErrorCode;
    this.capabilityErrorStatus = options.capabilityErrorStatus;
    this.claimCommitTransportFailsOnce =
      options.claimCommitTransportFailsOnce;
    this.claimResolveTransportFailsOnce =
      options.claimResolveTransportFailsOnce;
    this.cancelClaimCreateErrorCode = options.cancelClaimCreateErrorCode ?? null;
    this.cancelClaimCommitTransportFailsOnce =
      options.cancelClaimCommitTransportFailsOnce ?? false;
  }

  public async getCapabilities() {
    if (this.capabilityErrorStatus !== null) {
      throw new HbposApiError("capabilities unavailable", {
        kind: "http",
        status: this.capabilityErrorStatus,
      });
    }
    return this.capabilities;
  }

  public async createRepaymentClaim(
    command: InstallmentRepaymentClaimCreateCommand,
  ) {
    this.events.push("claim-create");
    this.createClaimCalls.push(command);
    if (this.claimCreateFailsOnce) {
      const failure = this.claimCreateFailsOnce;
      this.claimCreateFailsOnce = null;
      throw new HbposApiError("claim create result unknown", {
        kind: failure === "transport" ? "transport" : "http",
        ...(failure === "server" ? { status: 503 } : {}),
      });
    }
    if (this.claimCreateErrorCode) {
      throw new HbposApiError("claim unavailable", {
        kind: "http",
        status: this.claimCreateErrorStatus,
        code: this.claimCreateErrorCode,
      });
    }
    if (this.claim) return Object.freeze({ ...this.claim, alreadyExists: true });
    this.claimCreateCommand = command;
    this.claim = claimFrom(command, "Prepared");
    return this.claim;
  }

  public async beginRepaymentClaimProvider(
    command: InstallmentRepaymentClaimBeginProviderCommand,
  ) {
    this.events.push("claim-begin");
    this.beginClaimCalls.push(command);
    if (this.claimBeginErrorCode) {
      throw new HbposApiError("claim begin unavailable", {
        kind: "http",
        status: 409,
        code: this.claimBeginErrorCode,
      });
    }
    const current = this.requireClaim(command);
    if (
      current.provider !== null &&
      (current.provider !== command.provider ||
        current.providerAttemptId !== command.providerAttemptId)
    ) {
      throw new HbposApiError("claim mismatch", {
        kind: "http",
        status: 409,
        code: "MISMATCH",
      });
    }
    if (current.status !== "Prepared" && current.status !== "Unknown" &&
      current.status !== "ProviderPending") {
      throw new HbposApiError("claim cannot begin", {
        kind: "http",
        status: 409,
        code: "MISMATCH",
      });
    }
    this.claim = Object.freeze({
      ...current,
      provider: command.provider,
      providerAttemptId: command.providerAttemptId,
      status: "ProviderPending",
      updatedAtIso: "2026-08-04T01:01:00.000Z",
    });
    return this.claim;
  }

  public async prepareRepaymentClaimProvider(
    command: InstallmentRepaymentClaimPrepareProviderCommand,
  ) {
    this.events.push("claim-prepare-provider");
    this.prepareProviderCalls.push(command);
    if (!this.claim) {
      const created = claimFrom(
        {
          installmentGuid: command.installmentGuid,
          operationGuid: command.operationGuid,
          paymentGuid: command.paymentGuid,
          amountCents: command.amountCents,
          method: command.method,
          idempotencyKey: command.idempotencyKey,
        },
        "ProviderPending",
      );
      this.claim = Object.freeze({
        ...created,
        provider: command.provider,
        providerAttemptId: command.providerAttemptId,
      });
      this.claimCreateCommand = {
        installmentGuid: command.installmentGuid,
        operationGuid: command.operationGuid,
        paymentGuid: command.paymentGuid,
        amountCents: command.amountCents,
        method: command.method,
        idempotencyKey: command.idempotencyKey,
      };
      return this.claim;
    }
    const current = this.requireClaim(command);
    this.claim = Object.freeze({
      ...current,
      provider: command.provider,
      providerAttemptId: command.providerAttemptId,
      status: "ProviderPending",
    });
    return this.claim;
  }

  public async getRepaymentClaim(identity: InstallmentRepaymentClaimIdentity) {
    this.events.push("claim-get");
    this.getClaimCalls.push(identity);
    if (this.claimGetErrorCode) {
      throw new HbposApiError("claim get mismatch", {
        kind: "http",
        status: 409,
        code: this.claimGetErrorCode,
      });
    }
    return this.requireClaim(identity);
  }

  public async resolveRepaymentClaim(
    command: InstallmentRepaymentClaimResolveCommand,
  ) {
    this.events.push(`claim-resolve:${command.outcome}`);
    this.resolveClaimCalls.push(command);
    const current = this.requireClaim(command);
    this.claim = Object.freeze({ ...current, status: command.outcome });
    if (this.claimResolveTransportFailsOnce) {
      this.claimResolveTransportFailsOnce = false;
      throw new HbposApiError("claim resolve response lost", {
        kind: "transport",
      });
    }
    return this.claim;
  }

  public async commitRepaymentClaim(
    command: InstallmentRepaymentClaimCommitCommand,
  ) {
    this.events.push("claim-commit");
    this.commitClaimCalls.push(command);
    const current = this.requireClaim(command);
    const create = this.claimCreateCommand;
    if (!create) throw new Error("claim create command missing");
    const payment: InstallmentPaymentCommand = {
      paymentGuid: create.paymentGuid,
      method: create.method,
      amountCents: create.amountCents,
      reference: command.reference,
      reservationToken: command.reservationToken,
      cardTransactions: command.cardTransactions,
      idempotencyKey: create.idempotencyKey,
    };
    const committedDetails = details({
      installmentGuid: create.installmentGuid,
      paidCents: 3_000,
      balanceCents: 7_000,
      payments:
        this.appendResultMode === "approved-payment"
          ? [recordedPayment(payment)]
          : [],
    });
    this.claim = Object.freeze({
      ...current,
      status: "Committed",
      commit: Object.freeze({
        details: committedDetails,
        alreadyRecorded: false,
      }),
    });
    if (this.claimCommitTransportFailsOnce) {
      this.claimCommitTransportFailsOnce = false;
      throw new HbposApiError("commit response lost", { kind: "transport" });
    }
    return this.claim;
  }

  public async createCancelClaim(command: InstallmentCancelClaimCreateCommand) {
    this.events.push("cancel-claim-create");
    this.cancelClaimCreateCalls.push(command);
    if (this.cancelClaimCreateErrorCode) {
      throw new HbposApiError("cancel claim busy", { kind: "http", status: 409, code: this.cancelClaimCreateErrorCode });
    }
    if (this.cancelClaim) return Object.freeze({ ...this.cancelClaim, alreadyExists: true });
    this.cancelClaim = cancelClaimFrom(
      this.cancelClaimRefundModeOverride
        ? { ...command, refundMode: this.cancelClaimRefundModeOverride }
        : command,
      "Prepared",
    );
    return this.cancelClaim;
  }

  public async beginCancelClaimRefund(identity: InstallmentCancelClaimIdentity) {
    this.events.push("cancel-claim-begin");
    const current = this.requireCancelClaim(identity);
    this.cancelClaim = Object.freeze({ ...current, status: "RefundPending" });
    return this.cancelClaim;
  }

  public async getCancelClaim(identity: InstallmentCancelClaimIdentity) {
    this.events.push("cancel-claim-get");
    return this.requireCancelClaim(identity);
  }

  public async resolveCancelClaim(command: InstallmentCancelClaimResolveCommand) {
    this.events.push(`cancel-claim-resolve:${command.outcome}`);
    const current = this.requireCancelClaim(command);
    this.cancelClaim = Object.freeze({ ...current, status: command.outcome });
    return this.cancelClaim;
  }

  public async commitCancelClaim(command: InstallmentCancelClaimCommitCommand) {
    this.events.push("cancel-claim-commit");
    const current = this.requireCancelClaim(command);
    const committedDetails = this.cancelClaimCommitDetailsOverride ?? await this.cancelWithRefund({
      installmentGuid: command.installmentGuid,
      deviceCode: DEVICE_CODE,
      cashierId: "CASHIER-1",
      cashierName: "Cashier One",
      cancelledAtIso: "2026-08-04T01:00:00.000Z",
      refunds: command.refunds,
      reason: null,
      idempotencyKey: current.idempotencyKey,
    });
    this.cancelClaim = Object.freeze({
      ...current,
      status: "Committed",
      commit: Object.freeze({ details: committedDetails, alreadyCancelled: false }),
    });
    if (this.cancelClaimCommitTransportFailsOnce) {
      this.cancelClaimCommitTransportFailsOnce = false;
      throw new HbposApiError("cancel claim commit response lost", { kind: "transport" });
    }
    return this.cancelClaim;
  }

  private requireCancelClaim(identity: InstallmentCancelClaimIdentity) {
    if (!this.cancelClaim || this.cancelClaim.installmentGuid !== identity.installmentGuid || this.cancelClaim.operationGuid !== identity.operationGuid) {
      throw new HbposApiError("cancel claim missing", { kind: "http", status: 404, code: "CLAIM_NOT_FOUND" });
    }
    return this.cancelClaim;
  }

  private requireClaim(identity: InstallmentRepaymentClaimIdentity) {
    if (
      !this.claim ||
      this.claim.installmentGuid !== identity.installmentGuid ||
      this.claim.operationGuid !== identity.operationGuid
    ) {
      throw new HbposApiError("claim missing", {
        kind: "http",
        status: 404,
        code: "CLAIM_NOT_FOUND",
      });
    }
    return this.claim;
  }

  public async list(input: unknown) {
    this.listCalls += 1;
    this.listInputs.push(input);
    if (this.listError) throw this.listError;
    return [summary()];
  }

  public async getDetails(installmentGuid: string) {
    this.detailsCalls.push(installmentGuid);
    if (this.detailsError) throw this.detailsError;
    return this.detailsResponse;
  }

  public async create(command: InstallmentCreateCommand) {
    this.createCalls.push(command);
    if (this.createFailsOnce) {
      this.createFailsOnce = false;
      throw new Error("backend write failed after approved payment");
    }
    return (
      this.createResult ??
      details({
        installmentGuid: command.installmentGuid,
        deviceCode: command.deviceCode,
        cashierId: command.cashierId,
        cashierName: command.cashierName,
        customerName: command.customerName,
        customerPhone: command.customerPhone,
        totalCents: command.totalCents,
        downPaymentCents: command.downPaymentCents,
        paidCents: command.downPayment.amountCents,
        balanceCents:
          command.totalCents - command.downPayment.amountCents,
        status:
          command.totalCents === command.downPayment.amountCents
            ? "PaidOff"
            : "Active",
        lines: command.lines,
        payments: [
          recordedPayment(command.downPayment),
        ],
      })
    );
  }

  public async appendPayment(command: {
    installmentGuid: string;
    payment: InstallmentPaymentCommand;
  }) {
    this.appendCalls += 1;
    return details({
      installmentGuid: command.installmentGuid,
      payments:
        this.appendResultMode === "approved-payment"
          ? [recordedPayment(command.payment)]
          : [],
    });
  }
  public async cancelWithRefund(command: InstallmentCancelCommand) {
    this.cancelCalls.push(command);
    if (this.cancelResultMode === "active-without-refund") {
      return details({ installmentGuid: command.installmentGuid });
    }
    return details({
      installmentGuid: command.installmentGuid,
      // 顶层设备属于原分期单；退款流水设备属于本次实际执行终端。
      deviceCode: this.detailsResponse?.deviceCode ?? DEVICE_CODE,
      status: "Cancelled",
      balanceCents: 0,
      cancellationInfo: {
        kind: "RefundCancel",
        cancelledAtIso: command.cancelledAtIso,
        cancelledBy: command.cashierName,
        reason: command.reason,
      },
      payments: command.refunds.map((refund) => ({
          paymentGuid: refund.paymentGuid,
          method: refund.method,
          amountCents: -refund.amountCents,
          status: "Recorded",
          recordedAtIso: "2026-07-28T08:00:00.000Z",
          cashierId: command.cashierId,
          deviceCode: command.deviceCode,
          cardType: null,
          maskedCardNumber: null,
        })),
    });
  }
  public async void(command: { installmentGuid: string }) {
    this.voidCalls.push(command);
    const result = details({
      installmentGuid: command.installmentGuid,
      deviceCode: this.detailsResponse?.deviceCode ?? DEVICE_CODE,
      status: "Cancelled",
      cancellationInfo: {
        kind: "VoidCancel",
        cancelledAtIso: Reflect.get(command, "voidedAtIso"),
        cancelledBy:
          this.lifecycleCashierNameOverride ??
          Reflect.get(command, "cashierName"),
        reason:
          this.lifecycleVoidReasonOverride ?? Reflect.get(command, "reason"),
      },
    });
    if (this.voidTransportFailsOnce) {
      this.voidTransportFailsOnce = false;
      throw new HbposApiError("void response lost", { kind: "transport" });
    }
    return result;
  }
  public async confirmPickup(command: { installmentGuid: string }) {
    this.pickupCalls.push(command);
    const result = details({
      installmentGuid: command.installmentGuid,
      deviceCode: this.detailsResponse?.deviceCode ?? DEVICE_CODE,
      status: "PickedUp",
      pickupInfo: {
        pickedUpAtIso: Reflect.get(command, "confirmedAtIso"),
        pickedUpBy:
          this.lifecycleCashierNameOverride ??
          Reflect.get(command, "cashierName"),
        note: Reflect.get(command, "note"),
      },
    });
    if (this.pickupTransportFailsOnce) {
      this.pickupTransportFailsOnce = false;
      throw new HbposApiError("pickup response lost", { kind: "transport" });
    }
    return result;
  }
}

function createHarness(options: Readonly<{
  appendResultMode?: "approved-payment" | "missing-payment";
  cached?: readonly InstallmentSnapshot[];
  cancelResultMode?: "cancelled-with-refund" | "active-without-refund";
  crossDeviceRepaymentEnabled?: boolean;
  claimCreateErrorCode?: string;
  claimCreateErrorStatus?: number;
  cardRepaymentSupported?: boolean;
  claimCreateFailsOnce?: "server" | "transport";
  claimGetErrorCode?: string;
  claimBeginErrorCode?: string;
  claimCommitTransportFailsOnce?: boolean;
  claimResolveTransportFailsOnce?: boolean;
  cancelClaimCreateErrorCode?: string;
  cancelClaimCommitTransportFailsOnce?: boolean;
  capabilityErrorStatus?: number;
  repaymentClaimsSupported?: boolean;
  repaymentClaimsRequired?: boolean;
  repaymentClaimPrepareProviderV1?: boolean;
  createFailsOnce?: boolean;
  createResult?: InstallmentDetails;
  paymentOutcome?: "approved" | "declined" | "throw" | "unknown";
  paymentOutcomes?: readonly ("approved" | "declined" | "throw" | "unknown")[];
  paymentPrepareFails?: boolean;
  paymentsFactory?: (
    actionStore: MemoryInstallmentActionStore,
    events: string[],
  ) => InstallmentMutationPaymentPort;
  cancelAllRefundsDeclined?: boolean;
  permissions?: readonly string[];
  receiptReprint?: InstallmentReceiptReprintRuntimePort;
  refundVoucherPrinter?: Readonly<{
    printAfterCancel(installmentGuid: string, orderLabel: string): Promise<unknown>;
  }>;
  cacheUpsertFailsOnce?: boolean;
  lifecycleCompleteFailsOnce?: boolean;
  useAtomicFinalizer?: boolean;
  finalizerFailsOnce?: boolean;
  capturePerformance?: boolean;
}> = {}) {
  const currentCashier = new CurrentCashierSession();
  activateCashier(currentCashier, options.permissions ?? ALL_PERMISSIONS);
  const cart = new FakeCart();
  const online = { value: true };
  const events: string[] = [];
  const api = new ScriptedApi(
    options.createFailsOnce ?? false,
    options.createResult ?? null,
    options.appendResultMode ?? "approved-payment",
    options.cancelResultMode ?? "cancelled-with-refund",
    events,
    {
      crossDeviceRepaymentEnabled:
        options.crossDeviceRepaymentEnabled ?? false,
      repaymentClaimsSupported:
        options.repaymentClaimsSupported ?? true,
      repaymentClaimsRequired:
        options.repaymentClaimsRequired ?? true,
      repaymentClaimPrepareProviderV1:
        options.repaymentClaimPrepareProviderV1 ?? false,
      capabilityErrorStatus: options.capabilityErrorStatus ?? null,
      claimCreateErrorCode: options.claimCreateErrorCode ?? null,
      claimCreateErrorStatus: options.claimCreateErrorStatus ?? 409,
      cardRepaymentSupported: options.cardRepaymentSupported ?? true,
      claimCreateFailsOnce: options.claimCreateFailsOnce ?? null,
      claimGetErrorCode: options.claimGetErrorCode ?? null,
      claimBeginErrorCode: options.claimBeginErrorCode ?? null,
      claimCommitTransportFailsOnce:
        options.claimCommitTransportFailsOnce ?? false,
      claimResolveTransportFailsOnce:
        options.claimResolveTransportFailsOnce ?? false,
      cancelClaimCreateErrorCode: options.cancelClaimCreateErrorCode ?? null,
      cancelClaimCommitTransportFailsOnce:
        options.cancelClaimCommitTransportFailsOnce ?? false,
    },
  );
  const cache = new MemorySnapshotCache(options.cached ?? []);
  const hashMaterials: string[] = [];
  const performanceEvents: InstallmentPerformanceEvent[] = [];
  let monotonicMilliseconds = 0;
  const actionStore = new MemoryInstallmentActionStore(events);
  if (options.cacheUpsertFailsOnce) {
    cache.failNextUpsert = true;
  }
  if (options.lifecycleCompleteFailsOnce) {
    actionStore.failNextLifecycleComplete = true;
  }
  if (options.finalizerFailsOnce) {
    actionStore.failNextRepaymentFinalizer = true;
  }
  const voucherIntents = new RecordingVoucherIntentVault(events);
  const scriptedPayments = new ScriptedPayments(
    options.paymentOutcomes ?? [options.paymentOutcome ?? "approved"],
    actionStore,
    events,
    options.paymentPrepareFails ?? false,
    options.cancelAllRefundsDeclined ?? true,
  );
  const payments = options.paymentsFactory?.(actionStore, events) ?? scriptedPayments;
  let id = 0;
  const dependencies: ProductionInstallmentRuntimeDependencies = {
    currentCashier,
    terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
    activeCart: cart as unknown as ProductionInstallmentRuntimeDependencies["activeCart"],
    connectivity: { isOnline: async () => online.value },
    api: api as unknown as InstallmentsRemotePort,
    snapshotCache: cache,
    ...(options.useAtomicFinalizer
      ? { snapshotRepository: cache as never }
      : {}),
    actionStore,
    payments,
    ...(options.receiptReprint
      ? { receiptReprint: options.receiptReprint }
      : {}),
    ...(options.refundVoucherPrinter
      ? { refundVoucherPrinter: options.refundVoucherPrinter }
      : {}),
    voucherIntents,
    sha256Hex: async (material: string) => {
      hashMaterials.push(material);
      return createHash("sha256").update(material, "utf8").digest("hex");
    },
    createId: () => `00000000-0000-4000-8000-${String(++id).padStart(12, "0")}`,
    businessTimeZone: "Australia/Brisbane",
    now: () => new Date("2026-07-28T08:00:00.000Z"),
    nowIso: () => "2026-07-28T08:00:00.000Z",
    ...(options.capturePerformance
      ? {
          monotonicNowMilliseconds: () => {
            monotonicMilliseconds += 5;
            return monotonicMilliseconds;
          },
          performanceRecorder: {
            record(event: InstallmentPerformanceEvent) {
              performanceEvents.push(event);
            },
          },
        }
      : {}),
  };
  return {
    currentCashier,
    cart,
    online,
    api,
    cache,
    payments: scriptedPayments,
    actionStore,
    events,
    hashMaterials,
    performanceEvents,
    voucherIntents,
    createdIdCount: () => id,
    runtime: createProductionInstallmentRuntime(dependencies),
    rebuildRuntime: () => createProductionInstallmentRuntime(dependencies),
    rebuildRuntimeForDevice: (deviceCode: string) => {
      currentCashier.clear();
      activateCashier(
        currentCashier,
        ALL_PERMISSIONS,
        "SUPERVISOR-DEVICE",
        deviceCode,
      );
      return createProductionInstallmentRuntime({
        ...dependencies,
        terminal: { storeCode: STORE_CODE, deviceCode },
      });
    },
  };
}

function activateCashier(
  session: CurrentCashierSession,
  permissions: readonly string[],
  cashierId = "CASHIER-1",
  deviceCode = DEVICE_CODE,
): void {
  const epoch = session.beginAuthentication();
  session.activate(epoch, {
    source: "online",
    session: {
      cashierId,
      userGuid: null,
      cashierName: "Cashier One",
      storeCode: STORE_CODE,
      deviceCode,
      permissionCodes: [...permissions],
    },
  } satisfies CashierLoginResult, { storeCode: STORE_CODE, deviceCode });
}

function fillCreate(presenter: InstallmentPresenter): void {
  presenter.showCreate();
  presenter.setCustomerName("Customer");
  presenter.setCustomerPhone("0400000000");
  presenter.setCreateDownPayment("20.00");
}

function fillVoucherCreate(
  presenter: InstallmentPresenter,
  voucherReference = "VOUCHER-SECRET-CREATE",
): void {
  fillCreate(presenter);
  presenter.setCreatePaymentMethod("voucher");
  presenter.setCreateVoucherReference(voucherReference);
}

function fillVoucherRepayment(presenter: InstallmentPresenter): void {
  presenter.setRepaymentAmount("10.00");
  presenter.setRepaymentMethod("voucher");
  presenter.setRepaymentVoucherReference("VOUCHER-SECRET-REPAY");
}

function workflowOf(presenter: InstallmentPresenter): InstallmentWorkflowPort {
  return (
    presenter as unknown as Readonly<{
      workflow: InstallmentWorkflowPort;
    }>
  ).workflow;
}

function executeRepaymentClaimActionForTest(
  workflow: InstallmentWorkflowPort,
  persisted: PersistedInstallmentAction,
): Promise<InstallmentDetails> {
  const execute = Reflect.get(workflow, "executeRepaymentClaimAction");
  if (typeof execute !== "function") {
    throw new Error("executeRepaymentClaimAction is unavailable");
  }
  return Reflect.apply(execute, workflow, [persisted]) as Promise<InstallmentDetails>;
}

function claimFrom(
  command: InstallmentRepaymentClaimCreateCommand,
  status: InstallmentRepaymentClaim["status"],
): InstallmentRepaymentClaim {
  return Object.freeze({
    ...command,
    status,
    provider: null,
    providerAttemptId: null,
    createdAtIso: "2026-08-04T01:00:00.000Z",
    updatedAtIso: "2026-08-04T01:00:00.000Z",
    expiresAtIso: "2026-08-04T01:05:00.000Z",
    commit: null,
    alreadyExists: false,
  });
}

function cancelClaimFrom(
  command: InstallmentCancelClaimCreateCommand,
  status: InstallmentCancelClaim["status"],
): InstallmentCancelClaim {
  return Object.freeze({
    ...command,
    status,
    createdAtIso: "2026-08-04T01:00:00.000Z",
    updatedAtIso: "2026-08-04T01:00:00.000Z",
    expiresAtIso: "2026-08-04T01:05:00.000Z",
    commit: null,
    alreadyExists: false,
  });
}

function assertOrdered(actual: readonly string[], expected: readonly string[]) {
  let cursor = -1;
  for (const event of expected) {
    const next = actual.indexOf(event, cursor + 1);
    assert.notEqual(next, -1, `缺少顺序事件 ${event}: ${actual.join(", ")}`);
    cursor = next;
  }
}

function summary(overrides: Partial<InstallmentSnapshot> = {}) {
  return {
    installmentGuid: "installment-1",
    installmentNumber: "IP-001",
    storeCode: STORE_CODE,
    deviceCode: DEVICE_CODE,
    cashierName: "Cashier One",
    customerName: "Customer",
    customerPhone: "0400000000",
    createdAtIso: "2026-07-28T08:00:00.000Z",
    totalCents: 5_000,
    downPaymentCents: 2_000,
    paidCents: 2_000,
    balanceCents: 3_000,
    status: "Active",
    updatedAtIso: "2026-07-28T08:00:00.000Z",
    ...overrides,
  } as const;
}

function snapshot(overrides: Partial<InstallmentSnapshot> = {}): InstallmentSnapshot {
  return {
    ...summary(overrides),
    note: null,
    encryptedSensitiveRevision: 1,
  };
}

function details(overrides: Partial<InstallmentDetails> = {}): InstallmentDetails {
  return {
    ...summary(overrides),
    cashierId: "CASHIER-1",
    minimumDownPaymentCents: 2_000,
    lines: [],
    payments: [],
    pickupInfo: null,
    cancellationInfo: null,
    note: null,
    ...overrides,
  };
}

function recordedPayment(command: InstallmentPaymentCommand) {
  return {
    paymentGuid: command.paymentGuid,
    method: command.method,
    amountCents: command.amountCents,
    status: "Recorded" as const,
    recordedAtIso: "2026-07-28T08:00:00.000Z",
    cashierId: "CASHIER-1",
    deviceCode: DEVICE_CODE,
    cardType: null,
    maskedCardNumber: null,
  };
}
