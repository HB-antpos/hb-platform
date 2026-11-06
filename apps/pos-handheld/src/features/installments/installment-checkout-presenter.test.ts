import assert from "node:assert/strict";
import test from "node:test";

import {
  INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
  INSTALLMENTS_CANCEL_PERMISSION,
  INSTALLMENTS_CREATE_PERMISSION,
} from "@hb/pos-domain/features/installments/installment-authorization";
import { InstallmentCheckoutPresenter } from "./installment-checkout-presenter";
import type {
  InstallmentDetails,
} from "./installment-models";
import {
  InstallmentWorkflowError,
  type InstallmentWorkflowCreateInput,
  type InstallmentWorkflowPort,
  type InstallmentWorkflowRepaymentInput,
} from "./installment-presenter";

import { PAYMENT_PERMISSION } from "@/features/payments/runtime/payment-checkout-runtime";
import {
  installmentCreatePaymentEntry,
  installmentRepaymentPaymentEntry,
} from "@/features/payments/ui/unified-payment-entry";

const CHECKOUT_ID = "11111111-1111-4111-8111-111111111111";
const INSTALLMENT_ID = "22222222-2222-4222-8222-222222222222";

test("新分期只暂存一个现金 tender，并区分实收、入账和找零", async () => {
  const createInputs: InstallmentWorkflowCreateInput[] = [];
  const workflow = workflowStub({
    create: async (input) => {
      createInputs.push(input);
      return details({ balanceCents: 0, status: "PaidOff" });
    },
  });
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentCreatePaymentEntry({
      checkoutIntentId: CHECKOUT_ID,
      expectedCartRevision: 7,
    }),
    createDrafts: {
      getSnapshot: () => ({
        revision: 7,
        totalCents: 5_000,
        lines: [
          {
            lineKey: "line-1",
            displayName: "测试商品",
            quantity: "1",
            actualAmountCents: 5_000,
          },
        ],
      }),
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: createPermissions(),
    workflow,
    createTenderId: () => "tender-1",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.getState().attemptCreatedAtIso, null);
  presenter.openInstallmentCustomerEditor();
  presenter.setInstallmentCustomerDraftName("顾客甲");
  presenter.setInstallmentCustomerDraftPhone("0400000000");
  presenter.saveInstallmentCustomer();
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("60.00");
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(presenter.getState().tenders.length, 1);
  assert.deepEqual(presenter.getState().checkout.cash, {
    tenderedCents: 6_000,
    appliedCents: 5_000,
    changeCents: 1_000,
  });
  assert.equal(
    presenter.getState().checkout.fullInstallmentConfirmationRequired,
    true,
  );

  assert.equal(await presenter.confirm(), false);
  assert.equal(createInputs.length, 0);
  assert.equal(
    await presenter.confirm({
      acknowledgeFullInstallmentPayment: true,
    }),
    true,
  );
  assert.equal(createInputs[0]?.method, "cash");
  assert.equal(createInputs[0]?.downPaymentCents, 5_000);
  assert.equal(createInputs[0]?.cashTenderedCents, 6_000);
  assert.equal(createInputs[0]?.cardProvider, undefined);
  assert.equal(createInputs[0]?.customerName, "顾客甲");
  assert.equal(
    await presenter.confirm({
      acknowledgeFullInstallmentPayment: true,
    }),
    false,
  );
  assert.equal(createInputs.length, 1);
});

test("新分期公开 WPF 最低金额和顾客必填错误，不调用 workflow", async () => {
  const belowTotal = new InstallmentCheckoutPresenter({
    entry: installmentCreatePaymentEntry({
      checkoutIntentId: CHECKOUT_ID,
      expectedCartRevision: 7,
    }),
    createDrafts: {
      getSnapshot: () => ({
        revision: 7,
        totalCents: 4_999,
        lines: [
          {
            lineKey: "line-below-total",
            displayName: "低于最低金额",
            quantity: "1",
            actualAmountCents: 4_999,
          },
        ],
      }),
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: createPermissions(),
    workflow: workflowStub(),
    createTenderId: () => "tender-below-total",
  });

  assert.equal(await belowTotal.initialize(), false);
  assert.equal(
    belowTotal.getState().fieldIssue,
    "installment-total-below-minimum",
  );

  const createInputs: InstallmentWorkflowCreateInput[] = [];
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentCreatePaymentEntry({
      checkoutIntentId: CHECKOUT_ID,
      expectedCartRevision: 7,
    }),
    createDrafts: {
      getSnapshot: () => ({
        revision: 7,
        totalCents: 5_000,
        lines: [
          {
            lineKey: "line-minimum",
            displayName: "最低金额",
            quantity: "1",
            actualAmountCents: 5_000,
          },
        ],
      }),
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: createPermissions(),
    workflow: workflowStub({
      create: async (input) => {
        createInputs.push(input);
        return details({});
      },
    }),
    createTenderId: () => "tender-minimum",
  });

  assert.equal(await presenter.initialize(), true);
  presenter.openInstallmentCustomerEditor();
  presenter.saveInstallmentCustomer();
  assert.equal(
    presenter.getState().fieldIssue,
    "installment-customer-required",
  );
  presenter.setAmountText("19.99");
  assert.equal(await presenter.submitSelected(), false);
  assert.equal(
    presenter.getState().fieldIssue,
    "installment-down-payment-below-minimum",
  );

  presenter.setAmountText("20.00");
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(await presenter.confirm(), false);
  assert.equal(
    presenter.getState().fieldIssue,
    "installment-customer-required",
  );
  assert.equal(createInputs.length, 0);
});

test("还款顾客只读，银行卡拒绝超付并冻结所选 Linkly provider", async () => {
  const repaymentInputs: InstallmentWorkflowRepaymentInput[] = [];
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions(),
    workflow: workflowStub({
      addRepayment: async (input) => {
        repaymentInputs.push(input);
        return details({ balanceCents: 2_000 });
      },
    }),
    createTenderId: () => "tender-2",
  });

  assert.equal(await presenter.initialize(), true);
  const customer = presenter.getState().checkout.installmentCustomer;
  assert.equal(customer?.editable, false);
  assert.equal(customer?.name, "顾客");
  assert.equal(presenter.selectMethod("linkly-cloud"), true);
  presenter.setAmountText("31.00");
  assert.equal(await presenter.submitSelected(), false);
  assert.equal(
    presenter.getState().fieldIssue,
    "amount-exceeds-remaining",
  );
  presenter.setAmountText("10.00");
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(
    presenter.getState().checkout.fullInstallmentConfirmationRequired,
    false,
  );
  assert.equal(await presenter.confirm(), true);
  assert.equal(repaymentInputs[0]?.method, "card");
  assert.equal(repaymentInputs[0]?.cardProvider, "linkly-cloud");
  assert.equal(repaymentInputs[0]?.cashTenderedCents, undefined);
});

test("续付遇到占用 / 服务端未放开通道：支付页给出可区分的「本次未收款」提示，其余失败仍是通用提示", async () => {
  const cases: ReadonlyArray<readonly [InstallmentWorkflowError, string]> = [
    [new InstallmentWorkflowError("claim-busy", "busy"), "INSTALLMENT_REPAYMENT_BUSY"],
    [
      new InstallmentWorkflowError("payment-method-unsupported", "unsupported"),
      "INSTALLMENT_REPAYMENT_METHOD_UNSUPPORTED",
    ],
    [new InstallmentWorkflowError("online-required", "offline"), "ONLINE_REQUIRED"],
    [new InstallmentWorkflowError("conflict", "changed"), "PAYMENT_CHECKOUT_FAILED"],
    [new InstallmentWorkflowError("claim-review-required", "review"), "PAYMENT_CHECKOUT_FAILED"],
  ];
  for (const [error, expectedCode] of cases) {
    const presenter = new InstallmentCheckoutPresenter({
      entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
      createDrafts: {
        getSnapshot: () => null,
        subscribe: () => () => undefined,
      },
      initialOnline: true,
      permissions: repaymentPermissions(),
      workflow: workflowStub({
        addRepayment: async () => {
          throw error;
        },
        // 占用 / 通道未放开发生在任何 provider 调用之前、本地 action 已终结：没有需要恢复的本机事实。
        hasRecoveryRequired: async () => false,
      }),
      createTenderId: () => `tender-${error.code}`,
    });

    assert.equal(await presenter.initialize(), true);
    assert.equal(presenter.selectMethod("linkly-cloud"), true);
    presenter.setAmountText("10.00");
    assert.equal(await presenter.submitSelected(), true);
    assert.equal(await presenter.confirm(), false, error.code);
    assert.equal(presenter.getState().runtimeErrorCode, expectedCode, error.code);
    // 没有本机恢复事实：回到可重新选择付款方式的状态，不伪造恢复入口。
    assert.equal(presenter.getState().phase, "ready", error.code);
    assert.equal(presenter.getState().allowedActions.recover, false, error.code);
  }
});

test("现金续付先准备再确认，重复点击不重复调用任一阶段", async () => {
  let prepareCalls = 0;
  let confirmCalls = 0;
  let addRepaymentCalls = 0;
  let monotonicMilliseconds = 100;
  const performanceEvents: unknown[] = [];
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions(),
    workflow: workflowStub({
      addRepayment: async () => {
        addRepaymentCalls += 1;
        return details({});
      },
      prepareCashRepayment: async () => {
        prepareCalls += 1;
        return {
          installmentGuid: INSTALLMENT_ID,
          operationHash: "sha256:operation-1",
          amountCents: 1_000,
          path: "prepare-provider-v1",
        };
      },
      confirmPreparedCashRepayment: async () => {
        confirmCalls += 1;
        monotonicMilliseconds = 145;
        return details({ balanceCents: 2_000 });
      },
    }),
    createTenderId: () => "cash-repayment-tender",
    monotonicNowMilliseconds: () => monotonicMilliseconds,
    performanceRecorder: {
      record(event) {
        performanceEvents.push(event);
      },
    },
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("10.00");
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(prepareCalls, 1);
  assert.equal(addRepaymentCalls, 0);
  assert.equal(presenter.getState().phase, "cash-collection-ready");
  assert.equal(presenter.getState().checkout.cash.appliedCents, 1_000);

  const confirmations = await Promise.all([
    presenter.confirm(),
    presenter.confirm(),
  ]);
  assert.deepEqual(confirmations, [true, false]);
  assert.equal(confirmCalls, 1);
  assert.equal(presenter.getState().phase, "success");
  assert.deepEqual(performanceEvents, []);
  presenter.recordSuccessRendered();
  presenter.recordSuccessRendered();
  assert.deepEqual(performanceEvents, [{
    name: "presenter-success",
    elapsedMs: 45,
    operationHash: "sha256:operation-1",
    path: "prepare-provider-v1",
    outcome: "success",
  }]);
});

test("现金续付准备失败且无耐久 action 时恢复 ready 并允许重试", async () => {
  for (const error of [
    new InstallmentWorkflowError("online-required", "offline"),
    new InstallmentWorkflowError("conflict", "capability unavailable"),
  ]) {
    let recoveryChecks = 0;
    const presenter = new InstallmentCheckoutPresenter({
      entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
      createDrafts: {
        getSnapshot: () => null,
        subscribe: () => () => undefined,
      },
      initialOnline: true,
      permissions: repaymentPermissions(),
      workflow: workflowStub({
        prepareCashRepayment: async () => {
          throw error;
        },
        hasRecoveryRequired: async () => {
          recoveryChecks += 1;
          return false;
        },
      }),
      createTenderId: () => "cash-prepare-retry-tender",
    });

    assert.equal(await presenter.initialize(), true);
    assert.equal(presenter.selectMethod("cash"), true);
    presenter.setAmountText("10.00");
    assert.equal(await presenter.submitSelected(), false);
    assert.equal(recoveryChecks, 1);
    assert.equal(presenter.getState().phase, "ready");
    assert.equal(presenter.getState().allowedActions.start, true);
    assert.equal(presenter.getState().allowedActions.addCash, true);
    assert.equal(presenter.getState().allowedActions.recover, false);
    assert.equal(presenter.getState().checkout.canConfirm, false);
    assert.equal(presenter.getState().checkout.cashRepaymentStatus, "idle");
    assert.equal(
      presenter.getState().runtimeErrorCode,
      error.code === "online-required"
        ? "ONLINE_REQUIRED"
        : "PAYMENT_CHECKOUT_FAILED",
    );
  }
});

test("现金续付准备失败且耐久事实为真或无法核实时强制恢复", async () => {
  for (const hasRecoveryRequired of [
    async () => true,
    async (): Promise<boolean> => {
      throw new Error("ledger unavailable");
    },
  ]) {
    const presenter = new InstallmentCheckoutPresenter({
      entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
      createDrafts: {
        getSnapshot: () => null,
        subscribe: () => () => undefined,
      },
      initialOnline: true,
      permissions: repaymentPermissions(),
      workflow: workflowStub({
        prepareCashRepayment: async () => {
          throw new InstallmentWorkflowError("conflict", "prepare failed");
        },
        hasRecoveryRequired,
      }),
      createTenderId: () => "cash-prepare-recovery-tender",
    });

    assert.equal(await presenter.initialize(), true);
    assert.equal(presenter.selectMethod("cash"), true);
    presenter.setAmountText("10.00");
    assert.equal(await presenter.submitSelected(), false);
    assert.equal(presenter.getState().phase, "recovery-required");
    assert.equal(presenter.getState().allowedActions.recover, true);
  }
});

test("现金续付第二步先显示专属确认中状态并锁定按钮", async () => {
  let resolveConfirmation!: (value: InstallmentDetails) => void;
  let confirmCalls = 0;
  const confirmation = new Promise<InstallmentDetails>((resolve) => {
    resolveConfirmation = resolve;
  });
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions(),
    workflow: workflowStub({
      prepareCashRepayment: async () => ({
        installmentGuid: INSTALLMENT_ID,
        operationHash: "sha256:operation-confirming",
        amountCents: 1_000,
      }),
      confirmPreparedCashRepayment: async () => {
        confirmCalls += 1;
        return confirmation;
      },
    }),
    createTenderId: () => "cash-confirming-tender",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("10.00");
  await presenter.submitSelected();

  const confirming = presenter.confirm();
  await Promise.resolve();
  assert.equal(confirmCalls, 1);
  assert.equal(presenter.getState().phase, "cash-confirming");
  assert.equal(presenter.getState().busy, true);
  assert.equal(presenter.getState().checkout.cashRepaymentStatus, "confirming");

  resolveConfirmation(details({ balanceCents: 2_000 }));
  assert.equal(await confirming, true);
});

test("Prepared 现金续付重启后重建 tender 并可从恢复页确认", async () => {
  let confirmCalls = 0;
  const presenter = new InstallmentCheckoutPresenter({
    entry: null,
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions(),
    workflow: workflowStub({
      recoverBlocking: async () => {
        throw new InstallmentWorkflowError(
          "cash-confirmation-required",
          "check drawer",
        );
      },
      inspectPreparedCashRepayment: async () => ({
        installmentGuid: INSTALLMENT_ID,
        operationHash: "sha256:recovered-operation",
        amountCents: 1_000,
        path: "recovery",
      }),
      confirmPreparedCashRepayment: async () => {
        confirmCalls += 1;
        return details({ balanceCents: 2_000 });
      },
    }),
    createTenderId: () => "recovered-cash-tender",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(await presenter.recover(), false);
  assert.equal(presenter.getState().checkout.flow, "installment-repayment");
  assert.equal(presenter.getState().checkout.cashRepaymentStatus, "ready");
  assert.equal(presenter.getState().tenders[0]?.tenderGuid, "recovered-cash-tender");
  assert.equal(presenter.getState().tenders[0]?.amount.cents, 1_000);
  assert.equal(await presenter.confirm(), true);
  assert.equal(confirmCalls, 1);
  assert.equal(presenter.getState().phase, "success");
});

test("现金已进入确认后失败只允许恢复提交，不再次显示收款确认", async () => {
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions(),
    workflow: workflowStub({
      prepareCashRepayment: async () => ({
        installmentGuid: INSTALLMENT_ID,
        operationHash: "sha256:cash-collected",
        amountCents: 1_000,
        path: "prepare-provider-v1",
      }),
      confirmPreparedCashRepayment: async () => {
        throw new InstallmentWorkflowError(
          "payment-recovery-required",
          "commit timed out",
        );
      },
      hasRecoveryRequired: async () => true,
    }),
    createTenderId: () => "cash-collected-tender",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("10.00");
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(await presenter.confirm(), false);
  assert.equal(presenter.getState().phase, "recovery-required");
  assert.equal(presenter.getState().checkout.canConfirm, false);
  assert.equal(presenter.getState().checkout.cashRepaymentStatus, "idle");
  assert.equal(presenter.getState().allowedActions.recover, true);
});

test("现金续付初次 prepare 仅在 Cancel 权限与 runtime 方法同时存在时开放取消", async () => {
  for (const canCancel of [false, true]) {
    for (const hasRuntimeMethod of [false, true]) {
      const presenter = new InstallmentCheckoutPresenter({
        entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
        createDrafts: {
          getSnapshot: () => null,
          subscribe: () => () => undefined,
        },
        initialOnline: true,
        permissions: repaymentPermissions({ canCancel }),
        workflow: workflowStub({
          prepareCashRepayment: async () => ({
            installmentGuid: INSTALLMENT_ID,
            operationHash: `sha256:cash-cancel-gate-${String(canCancel)}-${String(hasRuntimeMethod)}`,
            amountCents: 1_000,
          }),
          ...(hasRuntimeMethod
            ? { cancelPreparedCashRepayment: async () => undefined }
            : {}),
        }),
        createTenderId: () =>
          `cash-cancel-gate-${String(canCancel)}-${String(hasRuntimeMethod)}-tender`,
      });

      assert.equal(await presenter.initialize(), true);
      assert.equal(presenter.selectMethod("cash"), true);
      presenter.setAmountText("10.00");
      assert.equal(await presenter.submitSelected(), true);
      assert.equal(
        presenter.getState().allowedActions.cancel,
        canCancel && hasRuntimeMethod,
      );
    }
  }
});

test("不可确认的 Unknown 与 ProviderPending Prepared 只为主管重建取消 fence", async () => {
  const scenarios = [
    {
      name: "Unknown+Prepared",
      recoveryError: new InstallmentWorkflowError(
        "payment-recovery-required",
        "unknown prepared claim",
      ),
      confirmationInspectCalls: 0,
    },
    {
      name: "ProviderPending+Prepared",
      recoveryError: new InstallmentWorkflowError(
        "cash-confirmation-required",
        "original cashier required",
      ),
      confirmationInspectCalls: 1,
    },
  ] as const;

  for (const scenario of scenarios) {
    let inspectPreparedCalls = 0;
    let inspectCancellableCalls = 0;
    let confirmCalls = 0;
    const workflow = workflowStub({
      recoverBlocking: async () => {
        throw scenario.recoveryError;
      },
      inspectPreparedCashRepayment: async () => {
        inspectPreparedCalls += 1;
        return null;
      },
      inspectCancellablePreparedCashRepayment: async () => {
        inspectCancellableCalls += 1;
        return {
          installmentGuid: INSTALLMENT_ID,
          operationHash: `sha256:${scenario.name}`,
          amountCents: 1_000,
          path: "recovery" as const,
        };
      },
      confirmPreparedCashRepayment: async () => {
        confirmCalls += 1;
        return details({ balanceCents: 2_000 });
      },
      cancelPreparedCashRepayment: async () => undefined,
    });
    const presenter = new InstallmentCheckoutPresenter({
      entry: null,
      createDrafts: {
        getSnapshot: () => null,
        subscribe: () => () => undefined,
      },
      initialOnline: true,
      permissions: repaymentPermissions({ canCancel: true }),
      workflow,
      createTenderId: () => `${scenario.name}-cancel-tender`,
    });

    assert.equal(await presenter.initialize(), true);
    assert.equal(await presenter.recover(), false);
    assert.equal(inspectPreparedCalls, scenario.confirmationInspectCalls);
    assert.equal(inspectCancellableCalls, 1);
    assert.equal(presenter.getState().phase, "recovery-required");
    assert.equal(presenter.getState().runtimeErrorCode, "PAYMENT_RECOVERY_FAILED");
    assert.equal(presenter.getState().checkout.canConfirm, false);
    assert.equal(presenter.getState().checkout.cashRepaymentStatus, "idle");
    assert.equal(presenter.getState().tenders.length, 1);
    assert.equal(presenter.getState().tenders[0]?.method, "cash");
    assert.equal(presenter.getState().tenders[0]?.reversible, false);
    assert.deepEqual(
      Object.entries(presenter.getState().allowedActions)
        .filter(([, enabled]) => enabled)
        .map(([action]) => action),
      ["cancel"],
    );
    assert.equal(await presenter.confirm(), false);
    assert.equal(confirmCalls, 0);
  }
});

test("无 Cancel 权限时不探测可取消 Prepared 且保持恢复阻断", async () => {
  let inspectCancellableCalls = 0;
  const workflow = workflowStub({
    recoverBlocking: async () => {
      throw new InstallmentWorkflowError(
        "payment-recovery-required",
        "unknown prepared claim",
      );
    },
    inspectCancellablePreparedCashRepayment: async () => {
      inspectCancellableCalls += 1;
      return {
        installmentGuid: INSTALLMENT_ID,
        operationHash: "sha256:unauthorized-cancellable-prepared",
        amountCents: 1_000,
        path: "recovery" as const,
      };
    },
    cancelPreparedCashRepayment: async () => undefined,
  });
  const presenter = new InstallmentCheckoutPresenter({
    entry: null,
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions(),
    workflow,
    createTenderId: () => "unauthorized-cancellable-prepared-tender",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(await presenter.recover(), false);
  assert.equal(inspectCancellableCalls, 0);
  assert.equal(presenter.getState().phase, "recovery-required");
  assert.equal(presenter.getState().allowedActions.cancel, false);
  assert.equal(presenter.getState().tenders.length, 0);
});

test("尚未收现取消续付为单飞操作，成功后清空 fence 并回到可重试状态", async () => {
  let cancelCalls = 0;
  let resolveCancellation!: () => void;
  const cancellation = new Promise<void>((resolve) => {
    resolveCancellation = resolve;
  });
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions({ canCancel: true }),
    workflow: workflowStub({
      prepareCashRepayment: async () => ({
        installmentGuid: INSTALLMENT_ID,
        operationHash: "sha256:cash-cancel-success",
        amountCents: 1_000,
        path: "prepare-provider-v1",
      }),
      cancelPreparedCashRepayment: async () => {
        cancelCalls += 1;
        await cancellation;
      },
    }),
    createTenderId: () => "cash-cancel-success-tender",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("10.00");
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(presenter.getState().allowedActions.cancel, true);

  const firstCancellation = presenter.cancel();
  const duplicateCancellation = presenter.cancel();
  await Promise.resolve();
  assert.equal(cancelCalls, 1);
  assert.equal(presenter.getState().busy, true);

  resolveCancellation();
  assert.deepEqual(
    await Promise.all([firstCancellation, duplicateCancellation]),
    [true, false],
  );
  assert.equal(presenter.getState().phase, "ready");
  assert.equal(presenter.getState().runtimeErrorCode, null);
  assert.equal(presenter.getState().tenders.length, 0);
  assert.equal(presenter.getState().remaining.cents, 3_000);
  assert.equal(presenter.getState().checkout.canConfirm, false);
  assert.equal(presenter.getState().checkout.cashRepaymentStatus, "idle");
  assert.deepEqual(presenter.getState().checkout.cash, {
    tenderedCents: 0,
    appliedCents: 0,
    changeCents: 0,
  });
  assert.equal(presenter.getState().allowedActions.start, true);
  assert.equal(presenter.getState().allowedActions.addCash, true);
  assert.equal(presenter.getState().allowedActions.cancel, false);
  assert.equal(presenter.getState().allowedActions.recover, false);
});

test("恢复出的 Prepared 现金取消成功后不显示支付成功并允许安全返回", async () => {
  let cancelCalls = 0;
  const presenter = new InstallmentCheckoutPresenter({
    entry: null,
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions({ canCancel: true }),
    workflow: workflowStub({
      recoverBlocking: async () => {
        throw new InstallmentWorkflowError(
          "cash-confirmation-required",
          "check drawer",
        );
      },
      inspectPreparedCashRepayment: async () => ({
        installmentGuid: INSTALLMENT_ID,
        operationHash: "sha256:recovered-cash-cancel",
        amountCents: 1_000,
        path: "recovery",
      }),
      cancelPreparedCashRepayment: async () => {
        cancelCalls += 1;
      },
    }),
    createTenderId: () => "recovered-cash-cancel-tender",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(await presenter.recover(), false);
  assert.equal(presenter.getState().allowedActions.cancel, true);
  assert.equal(await presenter.cancel(), true);
  assert.equal(cancelCalls, 1);
  assert.equal(presenter.getState().phase, "cancelled");
  assert.notEqual(presenter.getState().phase, "success");
  assert.equal(presenter.getState().tenders.length, 0);
  assert.equal(presenter.getState().allowedActions.recover, false);
  assert.equal(presenter.getState().allowedActions.cancel, false);
});

test("尚未收现取消超时只开放同一 cancel 重放，禁止 recover 重新开放确认", async () => {
  let cancelCalls = 0;
  let recoverCalls = 0;
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions({ canCancel: true }),
    workflow: workflowStub({
      recoverBlocking: async () => {
        recoverCalls += 1;
        return details({ balanceCents: 2_000 });
      },
      prepareCashRepayment: async () => ({
        installmentGuid: INSTALLMENT_ID,
        operationHash: "sha256:cash-cancel-timeout",
        amountCents: 1_000,
      }),
      cancelPreparedCashRepayment: async () => {
        cancelCalls += 1;
        if (cancelCalls === 1) throw new Error("timeout");
      },
    }),
    createTenderId: () => "cash-cancel-timeout-tender",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("10.00");
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(await presenter.cancel(), false);
  assert.equal(presenter.getState().phase, "recovery-required");
  assert.equal(
    presenter.getState().runtimeErrorCode,
    "INSTALLMENT_CASH_CANCELLATION_FAILED",
  );
  assert.equal(presenter.getState().tenders.length, 1);
  assert.equal(presenter.getState().tenders[0]?.method, "cash");
  assert.equal(presenter.getState().tenders[0]?.reversible, false);
  assert.equal(presenter.getState().checkout.canConfirm, false);
  assert.equal(presenter.getState().checkout.cashRepaymentStatus, "idle");
  assert.equal(presenter.getState().allowedActions.start, false);
  assert.equal(presenter.getState().allowedActions.addCash, false);
  assert.equal(presenter.getState().allowedActions.cancel, true);
  assert.equal(presenter.getState().allowedActions.recover, false);
  assert.deepEqual(
    Object.entries(presenter.getState().allowedActions)
      .filter(([, enabled]) => enabled)
      .map(([action]) => action),
    ["cancel"],
  );

  assert.equal(await presenter.recover(), false);
  assert.equal(recoverCalls, 0);
  assert.equal(presenter.getState().checkout.canConfirm, false);
  assert.equal(presenter.getState().checkout.cashRepaymentStatus, "idle");

  assert.equal(await presenter.cancel(), true);
  assert.equal(cancelCalls, 2);
  assert.equal(presenter.getState().phase, "ready");
  assert.equal(presenter.getState().tenders.length, 0);
});

test("缺少取消 Prepared 现金方法时失败关闭且不改变原 fence", async () => {
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentRepaymentPaymentEntry(INSTALLMENT_ID),
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: repaymentPermissions({ canCancel: true }),
    workflow: workflowStub({
      prepareCashRepayment: async () => ({
        installmentGuid: INSTALLMENT_ID,
        operationHash: "sha256:cash-cancel-unavailable",
        amountCents: 1_000,
      }),
    }),
    createTenderId: () => "cash-cancel-unavailable-tender",
  });

  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("10.00");
  assert.equal(await presenter.submitSelected(), true);
  const before = presenter.getState();
  assert.equal(before.allowedActions.cancel, false);
  assert.equal(
    await presenter.removeTender("cash-cancel-unavailable-tender"),
    false,
  );
  assert.equal(presenter.getState(), before);
  assert.equal(await presenter.cancel(), false);
  assert.equal(presenter.getState(), before);
  assert.equal(presenter.getState().checkout.cashRepaymentStatus, "ready");
  assert.equal(presenter.getState().tenders[0]?.reversible, false);
});

test("确认掉线按本地耐久 action 事实区分安全重试与强制恢复", async () => {
  for (const recoveryRequired of [false, true]) {
    const workflow = Object.assign(
      workflowStub({
        create: async () => {
          throw new InstallmentWorkflowError(
            "online-required",
            "offline",
          );
        },
      }),
      {
        hasRecoveryRequired: async () => recoveryRequired,
      },
    );
    const presenter = new InstallmentCheckoutPresenter({
      entry: installmentCreatePaymentEntry({
        checkoutIntentId: CHECKOUT_ID,
        expectedCartRevision: 7,
      }),
      createDrafts: {
        getSnapshot: () => ({
          revision: 7,
          totalCents: 5_000,
          lines: [
            {
              lineKey: `line-offline-${String(recoveryRequired)}`,
              displayName: "离线测试商品",
              quantity: "1",
              actualAmountCents: 5_000,
            },
          ],
        }),
        subscribe: () => () => undefined,
      },
      initialOnline: true,
      permissions: createPermissions(),
      workflow,
      createTenderId: () => `tender-offline-${String(recoveryRequired)}`,
    });

    assert.equal(await presenter.initialize(), true);
    presenter.openInstallmentCustomerEditor();
    presenter.setInstallmentCustomerDraftName("顾客丙");
    presenter.setInstallmentCustomerDraftPhone("0400000001");
    presenter.saveInstallmentCustomer();
    assert.equal(await presenter.submitSelected(), true);
    assert.equal(await presenter.confirm(), false);
    assert.equal(
      presenter.getState().runtimeErrorCode,
      "ONLINE_REQUIRED",
    );
    assert.equal(
      presenter.getState().phase,
      recoveryRequired ? "recovery-required" : "ready",
    );
    assert.equal(
      presenter.getState().allowedActions.recover,
      recoveryRequired,
    );
    assert.equal(presenter.getState().checkout.canConfirm, true);
  }
});

test("确认发生业务冲突但未落耐久 action 时不伪造恢复入口", async () => {
  const workflow = Object.assign(
    workflowStub({
      create: async () => {
        throw new InstallmentWorkflowError("conflict", "cart changed");
      },
    }),
    {
      hasRecoveryRequired: async () => false,
    },
  );
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentCreatePaymentEntry({
      checkoutIntentId: CHECKOUT_ID,
      expectedCartRevision: 7,
    }),
    createDrafts: {
      getSnapshot: () => ({
        revision: 7,
        totalCents: 5_000,
        lines: [
          {
            lineKey: "line-conflict",
            displayName: "冲突测试商品",
            quantity: "1",
            actualAmountCents: 5_000,
          },
        ],
      }),
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: createPermissions(),
    workflow,
    createTenderId: () => "tender-conflict",
  });

  assert.equal(await presenter.initialize(), true);
  presenter.openInstallmentCustomerEditor();
  presenter.setInstallmentCustomerDraftName("顾客丁");
  presenter.setInstallmentCustomerDraftPhone("0400000002");
  presenter.saveInstallmentCustomer();
  assert.equal(await presenter.submitSelected(), true);
  assert.equal(await presenter.confirm(), false);
  assert.equal(presenter.getState().phase, "ready");
  assert.equal(presenter.getState().allowedActions.recover, false);
  assert.equal(
    presenter.getState().runtimeErrorCode,
    "PAYMENT_CHECKOUT_FAILED",
  );
});

function createPermissions(): readonly string[] {
  return [
    INSTALLMENTS_CREATE_PERMISSION,
    PAYMENT_PERMISSION.view,
    PAYMENT_PERMISSION.confirm,
    PAYMENT_PERMISSION.takeCash,
    PAYMENT_PERMISSION.takeCard,
    PAYMENT_PERMISSION.takeVoucher,
  ];
}

function repaymentPermissions(
  options: Readonly<{ canCancel?: boolean }> = {},
): readonly string[] {
  return [
    INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
    ...(options.canCancel ? [INSTALLMENTS_CANCEL_PERMISSION] : []),
    PAYMENT_PERMISSION.view,
    PAYMENT_PERMISSION.confirm,
    PAYMENT_PERMISSION.takeCash,
    PAYMENT_PERMISSION.takeCard,
    PAYMENT_PERMISSION.takeVoucher,
  ];
}

function workflowStub(
  overrides: Partial<InstallmentWorkflowPort> = {},
): InstallmentWorkflowPort {
  return {
    listPaymentProviderAvailability: async () => [
      { provider: "square", available: true, blocker: null },
      { provider: "linkly-cloud", available: true, blocker: null },
      { provider: "voucher", available: true, blocker: null },
    ],
    list: async () => [],
    getDetails: async () => details({}),
    recoverBlocking: async () => details({}),
    create: async () => details({}),
    addRepayment: async () => details({}),
    cancelWithRefund: async () => details({}),
    void: async () => details({}),
    confirmPickup: async () => details({}),
    ...overrides,
  };
}

function details(
  overrides: Partial<InstallmentDetails>,
): InstallmentDetails {
  return {
    installmentGuid: INSTALLMENT_ID,
    installmentNumber: "IP-1001",
    storeCode: "HB001",
    deviceCode: "IPAD-1",
    cashierName: "Cashier",
    cashierId: "cashier-1",
    customerName: "顾客",
    customerPhone: "0400111222",
    createdAtIso: "2026-07-29T00:00:00.000Z",
    updatedAtIso: "2026-07-29T00:00:00.000Z",
    totalCents: 5_000,
    downPaymentCents: 2_000,
    paidCents: 2_000,
    balanceCents: 3_000,
    status: "Active",
    minimumDownPaymentCents: 2_000,
    lines: [
      {
        installmentLineGuid: "33333333-3333-4333-8333-333333333333",
        productCode: "P1",
        referenceCode: null,
        displayName: "测试商品",
        lookupCode: "P1",
        quantity: "1",
        unitPriceCents: 5_000,
        discountCents: 0,
        actualAmountCents: 5_000,
        itemNumber: null,
      },
    ],
    payments: [],
    pickupInfo: null,
    cancellationInfo: null,
    note: null,
    ...overrides,
  };
}
