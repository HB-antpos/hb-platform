import assert from "node:assert/strict";
import test from "node:test";

import type {
  NoReceiptReturnItem,
  ReceiptReturnContext,
} from "@hb/pos-domain/features/returns/return-domain";
import {
  ReturnPresenter,
  type ReturnPresenterState,
} from "@hb/pos-domain/features/returns/return-presenter";
import {
  ReturnWorkflow,
  type ReturnExecutionCommand,
  type ReturnExecutionOutcome,
  type ReturnExecutionPort,
} from "@hb/pos-domain/features/returns/return-workflow";

import {
  UpdateTransitionLeaseCoordinator,
} from "@/features/app-updates/update-transition-lease-coordinator";

test("公开 Presenter JSON 不泄露原单、原明细、capacity、PaymentId、RFN、券码或恢复键", async () => {
  const execution = new PresenterExecution();
  execution.executeImpl = async () => ({
    status: "unknown",
    recoveryKey: "PaymentId=pay-secret;RFN=rfn-secret;voucher=vc-secret",
  });
  execution.recoverImpl = async () => ({
    status: "completed",
    returnOrderGuid: "return-order-secret-ABC999",
  });
  const presenter = createPresenter(execution);

  assert.equal(await presenter.loadReceipt("receipt-secret-654321"), true);
  const lineId = presenter.getState().lines[0]?.id;
  assert.ok(lineId);
  assert.equal(presenter.incrementLine(lineId), true);
  assert.equal(await presenter.confirm(), false);
  assert.equal(presenter.getState().phase, "unknown");
  assertSanitized(presenter.getState());

  assert.equal(await presenter.recoverUnknown(), true);
  assert.equal(presenter.getState().phase, "success");
  assert.equal(
    presenter.getState().result?.returnOrderSummary,
    "••••ABC999",
  );
  assertSanitized(presenter.getState());
});

test("退货页订单摘要：#单号末 8 位 + 售出时间 + 收银员，缺失时省略（对齐 WPF #701）", async () => {
  const soldAtIso = new Date(2026, 9, 9, 17, 30).toISOString();
  const withMeta = new ReturnPresenter(
    new ReturnWorkflow({
      lookup: {
        lookupReceipt: async () =>
          secretReceiptContext({ soldAtIso, cashierName: "Lucy" }),
        lookupNoReceiptProduct: async () => noReceiptItem(),
        createNoReceiptOpenItem: async () => noReceiptItem(),
      },
      connectivity: { isOnline: async () => true },
      supervisorAuthorization: {
        authorizeNoReceiptReturn: async () => ({ authorizationKey: "grant" }),
      },
      sessionGuard: new PresenterSessionGuard(),
      execution: new PresenterExecution(),
      createActionId: () => "action",
    }),
  );
  assert.equal(await withMeta.loadReceipt("x"), true);
  const state = withMeta.getState();
  assert.equal(state.orderSummary, "#T-654321");
  assert.equal(state.orderSoldAtIso, soldAtIso);
  assert.equal(state.orderCashierName, "Lucy");

  const plain = createPresenter(new PresenterExecution());
  assert.equal(await plain.loadReceipt("x"), true);
  assert.equal(plain.getState().orderSoldAtIso, null);
  assert.equal(plain.getState().orderCashierName, null);
  // 完整原单号不进入页面状态。
  assert.equal(JSON.stringify(plain.getState()).includes("original-order-secret"), false);
});

test("Presenter 防重复点击，等待期间不会发起第二次退款", async () => {
  const pending = deferred<ReturnExecutionOutcome>();
  const execution = new PresenterExecution();
  execution.executeImpl = async () => pending.promise;
  const presenter = createPresenter(execution);
  await presenter.loadReceipt("HB-1");
  const lineId = presenter.getState().lines[0]?.id;
  assert.ok(lineId);
  presenter.incrementLine(lineId);

  const first = presenter.confirm();
  const duplicate = presenter.confirm();
  assert.equal(await duplicate, false);
  await waitUntil(() => execution.executeCalls.length === 1);
  pending.resolve({
    status: "completed",
    returnOrderGuid: "return-order-1",
  });

  assert.equal(await first, true);
  assert.equal(execution.executeCalls.length, 1);
});

test("更新 transition 等待在途退货 action，并拒绝封门后的新异步 action", async () => {
  const transition = new UpdateTransitionLeaseCoordinator();
  transition.bindTransitionBarrier((operation) => operation());
  const pending = deferred<ReturnExecutionOutcome>();
  const transitionRelease = deferred<void>();
  const execution = new PresenterExecution();
  execution.executeImpl = async () => pending.promise;
  const first = new ReturnPresenter(createWorkflow(execution), {
    operationLease: transition,
  });
  const second = new ReturnPresenter(
    createWorkflow(new PresenterExecution()),
    { operationLease: transition },
  );
  await first.loadReceipt("HB-1");
  const lineId = first.getState().lines[0]?.id;
  assert.ok(lineId);
  first.incrementLine(lineId);

  const confirm = first.confirm();
  await waitUntil(() => execution.executeCalls.length === 1);
  let transitionStarted = false;
  const update = transition.runTransition(async () => {
    transitionStarted = true;
    await transitionRelease.promise;
  });
  await Promise.resolve();
  assert.equal(transitionStarted, false);
  assert.equal(await second.loadReceipt("HB-2"), false);

  pending.resolve({
    status: "completed",
    returnOrderGuid: "return-order-1",
  });
  assert.equal(await confirm, true);
  await Promise.resolve();
  assert.equal(transitionStarted, true);
  transitionRelease.resolve();
  await update;
  assert.equal(await second.loadReceipt("HB-2"), true);
});

test("旧会话 Presenter 的行操作失败为稳定码且不执行退款", async () => {
  const session = new PresenterSessionGuard();
  const execution = new PresenterExecution();
  const presenter = createPresenter(execution, session);
  await presenter.loadReceipt("HB-1");
  const lineId = presenter.getState().lines[0]?.id;
  assert.ok(lineId);
  session.rotate();

  assert.equal(presenter.incrementLine(lineId), false);
  assert.equal(presenter.getState().errorCode, "RETURN_SESSION_EXPIRED");
  assert.equal(await presenter.confirm(), false);
  assert.equal(execution.executeCalls.length, 0);
});

test("恢复态 Presenter 自动展示脱敏冻结行，禁止编辑和 reset，仅显式恢复原 action", async () => {
  const execution = new PresenterExecution();
  const workflow = createWorkflow(execution);
  workflow.hydrateRecovery({
    actionId: "secret-recovery-action",
    sourceKind: "receipt",
    totalRefundCents: 1_001,
    lines: [
      {
        sourceKind: "receipt",
        itemNumber: "1001",
        displayName: "Recovered Product",
        quantity: 3,
        unitRefundCents: 334,
        signedAmountCents: -1_001,
        syncProvenance: {
          referenceCode: "RECOVERY-REF",
          priceSource: 0,
        },
      },
    ],
  });
  const presenter = new ReturnPresenter(workflow);

  assert.deepEqual(
    {
      phase: presenter.getState().phase,
      selectedTotalCents: presenter.getState().selectedTotalCents,
      displayName: presenter.getState().lines[0]?.displayName,
      canConfirm: presenter.getState().canConfirm,
    },
    {
      phase: "unknown",
      selectedTotalCents: 1_001,
      displayName: "Recovered Product",
      canConfirm: false,
    },
  );
  assert.equal(
    JSON.stringify(presenter.getState()).includes(
      "secret-recovery-action",
    ),
    false,
  );
  const lineId = presenter.getState().lines[0]?.id;
  assert.ok(lineId);
  assert.equal(presenter.incrementLine(lineId), false);
  assert.equal(presenter.reset(), false);
  assert.equal(await presenter.confirm(), false);
  assert.equal(await presenter.recoverUnknown(), true);
  assert.deepEqual(execution.recoverCalls, [
    {
      actionId: "secret-recovery-action",
      recoveryKey: null,
    },
  ]);
  assert.equal(execution.executeCalls.length, 0);
});

function createPresenter(
  execution: PresenterExecution,
  sessionGuard = new PresenterSessionGuard(),
): ReturnPresenter {
  return new ReturnPresenter(createWorkflow(execution, sessionGuard));
}

function createWorkflow(
  execution: PresenterExecution,
  sessionGuard = new PresenterSessionGuard(),
): ReturnWorkflow {
  return new ReturnWorkflow({
    lookup: {
      lookupReceipt: async () => secretReceiptContext(),
      lookupNoReceiptProduct: async () => noReceiptItem(),
      createNoReceiptOpenItem: async () => ({
        ...noReceiptItem(),
        sourceKind: "no-receipt-open-item",
        lookupCode: "OPENITEM",
      }),
    },
    connectivity: { isOnline: async () => true },
    supervisorAuthorization: {
      authorizeNoReceiptReturn: async () => ({
        authorizationKey: "supervisor-grant-private",
      }),
    },
    sessionGuard,
    execution,
    createActionId: () => "action-private-but-not-presented",
  });
}

class PresenterExecution implements ReturnExecutionPort {
  public readonly executeCalls: ReturnExecutionCommand[] = [];
  public readonly recoverCalls: {
    actionId: string;
    recoveryKey: string | null;
  }[] = [];
  public executeImpl: (
    command: ReturnExecutionCommand,
  ) => Promise<ReturnExecutionOutcome> = async () => ({
    status: "completed",
    returnOrderGuid: "return-order-default",
  });
  public recoverImpl: (input: Readonly<{
    actionId: string;
    recoveryKey: string | null;
  }>) => Promise<ReturnExecutionOutcome> = async () => ({
    status: "completed",
    returnOrderGuid: "return-order-default",
  });

  public execute(
    command: ReturnExecutionCommand,
  ): Promise<ReturnExecutionOutcome> {
    this.executeCalls.push(command);
    return this.executeImpl(command);
  }

  public recover(input: Readonly<{
    actionId: string;
    recoveryKey: string | null;
  }>): Promise<ReturnExecutionOutcome> {
    this.recoverCalls.push(input);
    return this.recoverImpl(input);
  }
}

class PresenterSessionGuard {
  private epoch = 1;

  public captureLease(): string {
    return String(this.epoch);
  }

  public assertActive(lease: string): void {
    if (lease !== String(this.epoch)) throw new Error("stale");
  }

  public rotate(): void {
    this.epoch += 1;
  }
}

function secretReceiptContext(
  extra: Partial<ReceiptReturnContext> = {},
): ReceiptReturnContext {
  return {
    ...extra,
    originalOrderGuid: "original-order-secret-123456",
    receiptLabel: "receipt-secret-654321",
    loadedFrom: "remote",
    returnRecordsMayBeStale: false,
    lines: [
      {
        selectionKey: "detail-key-secret-PaymentId",
        originalOrderGuid: "original-order-secret-123456",
        originalOrderDetailGuid: "original-detail-secret-987654",
        returnSourceKey: "voucher-code-secret-VC111",
        productCode: "P-1",
        itemNumber: "1001",
        lookupCode: "1001",
        displayName: "Product",
        availableQuantity: 1,
        unitRefundCents: 1_000,
        remainingAmountCents: 1_000,
        syncProvenance: {
          referenceCode: "RECEIPT-REF",
          priceSource: 0,
        },
      },
    ],
    tenderCapacities: [
      {
        capacityId: "RFN-secret-capacity",
        originalOrderGuid: "original-order-secret-123456",
        method: "cash",
        remainingCents: 1_000,
        offlineCashProof: {
          evidenceId: "cash-proof-secret",
          capacityId: "RFN-secret-capacity",
          originalOrderGuid: "original-order-secret-123456",
          remainingCents: 1_000,
        },
      },
    ],
  };
}

function noReceiptItem(): NoReceiptReturnItem {
  return {
    sourceKind: "no-receipt-product",
    selectionKey: "no-receipt-selection",
    returnSourceKey: "no-receipt-source",
    productCode: "P-2",
    itemNumber: "2002",
    lookupCode: "9320001",
    displayName: "No receipt product",
    unitRefundCents: 500,
    syncProvenance: {
      referenceCode: "CATALOG-REF",
      priceSource: 1,
    },
  };
}

function assertSanitized(state: ReturnPresenterState): void {
  const json = JSON.stringify(state);
  for (const secret of [
    "original-order-secret-123456",
    "original-detail-secret-987654",
    "detail-key-secret-PaymentId",
    "voucher-code-secret-VC111",
    "RFN-secret-capacity",
    "cash-proof-secret",
    "pay-secret",
    "rfn-secret",
    "vc-secret",
    "return-order-secret-ABC999",
    "action-private-but-not-presented",
    "originalOrderGuid",
    "originalOrderDetailGuid",
    "returnSourceKey",
    "capacityId",
    "recoveryKey",
  ]) {
    assert.equal(json.includes(secret), false, `leaked ${secret}`);
  }
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

async function waitUntil(predicate: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  throw new Error("condition not reached");
}
