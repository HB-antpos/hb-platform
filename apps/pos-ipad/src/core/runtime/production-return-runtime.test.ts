import assert from "node:assert/strict";
import test from "node:test";

import { CurrentCashierSession } from "./current-cashier-session";
import {
  POS_RETURN_PERMISSIONS,
  PosReturnRuntimeError,
  createProductionReturnRuntime,
  type PosReturnAuthorizationPort,
  type ProductionReturnRuntimeDependencies,
} from "./production-return-runtime";

import {
  HbposApiError,
  type CashierSessionDto,
} from "@/core/api/hbpos-api";
import type { AuditEventDraft } from "@/core/contracts";
import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";
import type { LocalCatalogMatch } from "@/core/db/catalog-repository";
import type { SensitivePayloadEncryptor } from "@/core/db/sqlite-repositories";
import type { PaymentAcknowledgementResult, PaymentAcknowledgementRuntimePort } from "@hb/pos-payments-core/features/payments/payment-acknowledgement-service";
import {
  OperationAuthorizationService,
  type AuthorizedOperationContext,
  type OperationAuthorizationRequest,
  type OperationAuthorizationResult,
} from "@/features/operation-authorization";
import type {
  CompleteDurableReturnAction,
  DurableReturnAction,
  DurableReturnAllocation,
  PrepareDurableReturnAction,
  ReturnAllocationExternalOutcome,
  ReturnSupervisorResolutionRecord,
  ReturnExecutionLedgerPort,
  ReturnRecoveryScope,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";

const NOW_ISO = "2026-07-28T08:00:00.000Z";
const STORE_CODE = "STORE-1";
const DEVICE_CODE = "IPAD-1";
const ORDER_GUID = "11111111-1111-4111-8111-111111111111";
const ALL_RETURN_PERMISSIONS = Object.freeze(
  Object.values(POS_RETURN_PERMISSIONS),
);

test("公开服务只提供异步 presenter 工厂和脱敏恢复信号，View 当前收银员可直通", async () => {
  const harness = createHarness();

  assert.deepEqual(
    Object.keys(harness.runtime).sort(),
    ["createPresenter", "hasRecoveryRequired"],
  );
  const [presenter, recoveryRequired] = await Promise.all([
    harness.runtime.createPresenter(),
    harness.runtime.hasRecoveryRequired(),
  ]);

  assert.equal(presenter.getState().phase, "search");
  assert.equal(recoveryRequired, false);
  assert.equal(harness.recoveryScopes.length, 2);
  for (const scope of harness.recoveryScopes) {
    assert.deepEqual(scope, {
      storeCode: STORE_CODE,
      deviceCode: DEVICE_CODE,
      cashierId: "CASHIER-1",
      sessionEpoch: "1",
    });
  }
  assert.deepEqual(
    permissionRequests(harness, POS_RETURN_PERMISSIONS.view).length,
    1,
  );
  assert.equal(harness.supervisorBarcodes.length, 0);
});

test("Returns.View 支持同店同设备主管覆盖；拒绝后不创建 presenter", async () => {
  const allowed = createHarness({
    cashierPermissions: [],
    supervisorPermissions: [POS_RETURN_PERMISSIONS.view],
  });
  const creating = allowed.runtime.createPresenter();
  await flush();
  assert.equal(
    allowed.authorizationService.getState().kind,
    "awaiting-supervisor",
  );
  assert.deepEqual(
    await allowed.authorizationService.submitSupervisorBarcode(
      "VIEW-SUPERVISOR",
    ),
    { consumed: true, outcome: "authorized" },
  );
  assert.equal((await creating).getState().phase, "search");

  const denied = createHarness({
    cashierPermissions: [],
    supervisorPermissions: [],
  });
  const rejected = denied.runtime.createPresenter();
  await flush();
  assert.deepEqual(
    await denied.authorizationService.submitSupervisorBarcode(
      "DENIED-VIEW-BARCODE",
    ),
    {
      consumed: true,
      outcome: "denied",
      reason: "PERMISSION_DENIED",
    },
  );
  assert.equal(denied.authorizationService.cancel(), true);
  await assert.rejects(
    rejected,
    (error: unknown) =>
      error instanceof PosReturnRuntimeError &&
      error.code === "RETURN_VIEW_FORBIDDEN",
  );
  assert.equal(denied.ledger.prepared.length, 0);
});

test("presenter 捕获收银员 epoch；锁屏后旧实例不能查询、授权或执行", async () => {
  const harness = createHarness();
  const presenter = await harness.runtime.createPresenter();
  assert.equal(presenter.beginNoReceipt(), true);

  harness.currentCashier.clear();
  assert.equal(await presenter.addNoReceiptProduct("P1"), false);
  assert.equal(
    presenter.getState().errorCode,
    "RETURN_SESSION_EXPIRED",
  );
  assert.equal(
    permissionRequests(
      harness,
      POS_RETURN_PERMISSIONS.addNoReceiptItem,
    ).length,
    0,
  );
  assert.equal(harness.ledger.prepared.length, 0);
});

test("恢复列表查询前后 session 失效均失败关闭，不返回旧作用域 presenter", async () => {
  const before = createHarness();
  before.currentCashier.clear();
  await assert.rejects(
    before.runtime.createPresenter(),
    (error: unknown) =>
      error instanceof PosReturnRuntimeError &&
      error.code === "RETURN_SESSION_UNAVAILABLE",
  );
  assert.equal(before.recoveryScopes.length, 0);

  const after = createHarness();
  const listGate = deferred<void>();
  after.ledger.listBarrier = listGate.promise;
  const creating = after.runtime.createPresenter();
  await waitUntil(() => after.recoveryScopes.length === 1);
  after.currentCashier.clear();
  listGate.resolve(undefined);
  await assert.rejects(
    creating,
    (error: unknown) =>
      error instanceof PosReturnRuntimeError &&
      error.code === "RETURN_SESSION_UNAVAILABLE",
  );
});

test("小票退货当前收银员直通；完整非敏感行冻结入 ledger，履约失败不翻转 completed", async () => {
  const harness = createHarness({
    online: false,
    fulfilmentFailure: true,
  });
  const presenter = await harness.runtime.createPresenter();
  assert.equal(await presenter.loadReceipt(ORDER_GUID), true);
  const publicLine = presenter.getState().lines[0];
  assert.ok(publicLine);
  assert.equal(presenter.setLineQuantity(publicLine.id, 1), true);

  assert.equal(await presenter.confirm(), true);
  assert.equal(presenter.getState().phase, "success");
  assert.equal(harness.ledger.prepared.length, 1);
  assert.deepEqual(harness.ledger.prepared[0]?.lines, [
    {
      lineId: harness.ledger.prepared[0]?.lines[0]?.lineId,
      selectionKey: `local-receipt-line:${ORDER_GUID}:detail-1`,
      sourceKind: "receipt",
      returnSourceKey: `receipt:${ORDER_GUID}:detail-1`,
      originalOrderGuid: ORDER_GUID,
      originalOrderDetailGuid: "detail-1",
      productCode: "P1",
      itemNumber: "ITEM-1",
      lookupCode: "LOOKUP-1",
      displayName: "Receipt Product",
      quantity: 1,
      unitRefundCents: 250,
      signedAmountCents: -250,
      availableQuantity: 2,
      remainingAmountCents: 500,
      syncProvenance: {
        referenceCode: "RECEIPT-REF",
        priceSource: 2,
      },
    },
  ]);
  assert.deepEqual(
    harness.fulfilmentCalls.map((call) => call.kind),
    ["materialize", "drain"],
  );
  assert.equal(harness.ledger.actions()[0]?.status, "completed");

  const publicJson = JSON.stringify(presenter.getState());
  for (const seed of harness.capacitySeeds) {
    assert.equal(
      publicJson.includes(String(seed.capacityId)),
      false,
      "公开 presenter state 不得包含 capacityId",
    );
  }
  assert.equal(publicJson.includes("SUPERVISOR-TICKET"), false);
  assert.equal(publicJson.includes("BARCODE"), false);
});

test("小票确认按 AddReceiptLine → Confirm 两步主管授权，之后才允许 ledger/provider", async () => {
  const harness = createHarness({
    cashierPermissions: [POS_RETURN_PERMISSIONS.view],
    supervisorPermissions: [
      POS_RETURN_PERMISSIONS.addReceiptLine,
      POS_RETURN_PERMISSIONS.confirm,
    ],
    online: false,
  });
  const presenter = await selectedReceiptPresenter(harness);
  const confirming = presenter.confirm();
  await flush();

  assert.deepEqual(
    authorizationState(harness),
    {
      permissionCode: POS_RETURN_PERMISSIONS.addReceiptLine,
      action: "add-receipt-line",
    },
  );
  assert.equal(harness.ledger.prepared.length, 0);
  await harness.authorizationService.submitSupervisorBarcode(
    "RECEIPT-SUPERVISOR-ONE",
  );
  await flush();
  assert.deepEqual(
    authorizationState(harness),
    {
      permissionCode: POS_RETURN_PERMISSIONS.confirm,
      action: "confirm-return",
    },
  );
  assert.equal(harness.ledger.prepared.length, 0);
  await harness.authorizationService.submitSupervisorBarcode(
    "RECEIPT-SUPERVISOR-TWO",
  );

  assert.equal(await confirming, true);
  assert.equal(harness.ledger.prepared.length, 1);
  const requests = harness.authorizationRequests.filter(
    (request) =>
      request.permissionCode !== POS_RETURN_PERMISSIONS.view,
  );
  assert.deepEqual(
    requests.map((request) => request.permissionCode),
    [
      POS_RETURN_PERMISSIONS.addReceiptLine,
      POS_RETURN_PERMISSIONS.confirm,
    ],
  );
  assert.notEqual(requests[0]?.actionId, requests[1]?.actionId);
});

test("AddReceiptLine 或 Confirm 任一步拒绝都停在 selecting，不产生 Unknown/ledger/退款", async () => {
  const firstDenied = createHarness({
    cashierPermissions: [POS_RETURN_PERMISSIONS.view],
    supervisorPermissions: [POS_RETURN_PERMISSIONS.confirm],
    online: false,
  });
  const firstPresenter =
    await selectedReceiptPresenter(firstDenied);
  const firstConfirm = firstPresenter.confirm();
  await flush();
  assert.deepEqual(
    await firstDenied.authorizationService.submitSupervisorBarcode(
      "NO-ADD-RECEIPT",
    ),
    {
      consumed: true,
      outcome: "denied",
      reason: "PERMISSION_DENIED",
    },
  );
  firstDenied.authorizationService.cancel();
  assert.equal(await firstConfirm, false);
  assert.deepEqual(
    {
      phase: firstPresenter.getState().phase,
      errorCode: firstPresenter.getState().errorCode,
    },
    {
      phase: "selecting",
      errorCode: "RETURN_SUPERVISOR_REQUIRED",
    },
  );
  assert.equal(firstDenied.ledger.prepared.length, 0);
  assert.equal(firstDenied.onlineSubmits.length, 0);

  const secondDenied = createHarness({
    cashierPermissions: [POS_RETURN_PERMISSIONS.view],
    supervisorPermissions: [
      POS_RETURN_PERMISSIONS.addReceiptLine,
    ],
    online: false,
  });
  const secondPresenter =
    await selectedReceiptPresenter(secondDenied);
  const secondConfirm = secondPresenter.confirm();
  await flush();
  await secondDenied.authorizationService.submitSupervisorBarcode(
    "ALLOW-ADD-RECEIPT",
  );
  await flush();
  const confirmActionId =
    authorizationPendingActionId(secondDenied);
  assert.deepEqual(
    await secondDenied.authorizationService.submitSupervisorBarcode(
      "DENY-CONFIRM",
    ),
    {
      consumed: true,
      outcome: "denied",
      reason: "PERMISSION_DENIED",
    },
  );
  secondDenied.authorizationService.cancel();
  assert.equal(await secondConfirm, false);
  assert.equal(secondPresenter.getState().phase, "selecting");
  assert.equal(
    secondPresenter.getState().errorCode,
    "RETURN_SUPERVISOR_REQUIRED",
  );
  assert.equal(secondDenied.ledger.prepared.length, 0);

  // 相同 presenter 的重试沿用稳定 actionId，不能换 ID 绕过拒绝结论。
  assert.equal(await secondPresenter.confirm(), false);
  const confirmRequests = permissionRequests(
    secondDenied,
    POS_RETURN_PERMISSIONS.confirm,
  );
  assert.equal(confirmRequests.length, 2);
  assert.equal(confirmRequests[0]?.actionId, confirmActionId);
  assert.equal(confirmRequests[1]?.actionId, confirmActionId);
});

test("无票 AddNoReceiptItem 只授权一次并生成独立 opaque grant；两条 lookup material 均完整", async () => {
  const harness = createHarness();
  const presenter = await harness.runtime.createPresenter();
  assert.equal(presenter.beginNoReceipt(), true);
  assert.equal(await presenter.addNoReceiptProduct("P1"), true);
  assert.equal(presenter.selectMethod("card"), true);
  assert.equal(await presenter.addNoReceiptProduct("P2"), true);
  assert.equal(await presenter.confirm(), true);

  assert.equal(
    permissionRequests(
      harness,
      POS_RETURN_PERMISSIONS.addNoReceiptItem,
    ).length,
    1,
  );
  const prepared = harness.ledger.prepared[0];
  assert.ok(prepared?.supervisorGrantKey);
  assert.equal(
    prepared.supervisorGrantKey.includes("BARCODE"),
    false,
  );
  assert.equal(
    prepared.supervisorGrantKey.includes("SUPERVISOR-TICKET"),
    false,
  );
  assert.deepEqual(
    prepared.lines.map((line) => ({
      sourceKind: line.sourceKind,
      productCode: line.productCode,
      itemNumber: line.itemNumber,
      lookupCode: line.lookupCode,
      displayName: line.displayName,
      quantity: line.quantity,
      unitRefundCents: line.unitRefundCents,
      signedAmountCents: line.signedAmountCents,
      availableQuantity: line.availableQuantity,
      remainingAmountCents: line.remainingAmountCents,
      syncProvenance: line.syncProvenance,
    })),
    [
      {
        sourceKind: "no-receipt-product",
        productCode: "P1",
        itemNumber: "ITEM-P1",
        lookupCode: "P1",
        displayName: "Catalog P1",
        quantity: 1,
        unitRefundCents: 300,
        signedAmountCents: -300,
        availableQuantity: null,
        remainingAmountCents: null,
        syncProvenance: {
          referenceCode: "CATALOG-REF",
          priceSource: 3,
        },
      },
      {
        sourceKind: "no-receipt-product",
        productCode: "P2",
        itemNumber: "ITEM-P2",
        lookupCode: "P2",
        displayName: "Catalog P2",
        quantity: 1,
        unitRefundCents: 300,
        signedAmountCents: -300,
        availableQuantity: null,
        remainingAmountCents: null,
        syncProvenance: {
          referenceCode: "CATALOG-REF",
          priceSource: 3,
        },
      },
    ],
  );
  assert.equal(
    JSON.stringify(presenter.getState()).includes(
      prepared.supervisorGrantKey,
    ),
    false,
  );
});

test("在线退款的本地 action/allocation 完成后才确认其耐久支付 attempt", async () => {
  const harness = createHarness({
    acknowledgementImpl: async () => undefined,
  });
  const presenter = await harness.runtime.createPresenter();
  assert.equal(presenter.beginNoReceipt(), true);
  assert.equal(await presenter.addNoReceiptProduct("P1"), true);
  assert.equal(presenter.selectMethod("card"), true);

  assert.equal(await presenter.confirm(), true);

  const action = harness.ledger.actions()[0];
  const durableAttemptId = action?.allocations[0]?.durableAttemptId;
  assert.equal(action?.status, "completed");
  assert.ok(durableAttemptId);
  assert.deepEqual(
    action?.allocations.map((allocation) => ({
      status: allocation.status,
      executionKind: allocation.executionKind,
      externalAttemptKind: allocation.externalAttemptKind,
      durableAttemptId: allocation.durableAttemptId,
    })),
    [{
      status: "completed",
      executionKind: "online-refund",
      externalAttemptKind: "payment-provider",
      durableAttemptId,
    }],
  );
  assert.deepEqual(harness.acknowledgementCalls, [durableAttemptId]);
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 0);
});

test("退货 ACK pending 暂停 action；恢复只确认原退款，不重放 provider", async () => {
  let failOnce = true;
  const harness = createHarness({
    acknowledgementImpl: async () => {
      if (failOnce) {
        failOnce = false;
        throw new Error("Linkly acknowledgement unavailable");
      }
    },
  });
  const presenter = await harness.runtime.createPresenter();
  assert.equal(presenter.beginNoReceipt(), true);
  assert.equal(await presenter.addNoReceiptProduct("P1"), true);
  assert.equal(presenter.selectMethod("card"), true);

  assert.equal(await presenter.confirm(), false);

  assert.equal(presenter.getState().phase, "unknown");
  assert.equal(harness.ledger.actions()[0]?.status, "unknown");
  assert.equal(harness.acknowledgementCalls.length, 1);
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 0);

  const recovered = await harness.runtime.createPresenter();
  assert.equal(await recovered.recoverUnknown(), true);
  assert.equal(harness.ledger.actions()[0]?.status, "completed");
  assert.equal(harness.acknowledgementCalls.length, 2);
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 0);
});

test("退货 ACK 返回缺失结果时暂停 action，不继续提交退款", async () => {
  const harness = createHarness({
    acknowledgementImpl: async () => undefined,
    malformedAcknowledgementResult: true,
  });
  const presenter = await harness.runtime.createPresenter();
  assert.equal(presenter.beginNoReceipt(), true);
  assert.equal(await presenter.addNoReceiptProduct("P1"), true);
  assert.equal(presenter.selectMethod("card"), true);

  // ACK 结果不完整时必须停在可恢复状态，不能把下一笔金融动作当作已获确认。
  assert.equal(await presenter.confirm(), false);
  assert.equal(presenter.getState().phase, "unknown");
  assert.equal(harness.ledger.actions()[0]?.status, "unknown");
  assert.equal(harness.acknowledgementCalls.length, 1);
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 0);
});

test("Unknown 退货恢复完成后只确认原 durable attempt，不再次 submit", async () => {
  const harness = createHarness({
    acknowledgementImpl: async () => undefined,
    onlineSubmitOutcomes: [
      { status: "unknown", protectedRecoveryKey: "PROTECTED-RECOVERY" },
    ],
  });
  const first = await harness.runtime.createPresenter();
  assert.equal(first.beginNoReceipt(), true);
  assert.equal(await first.addNoReceiptProduct("P1"), true);
  assert.equal(first.selectMethod("card"), true);
  assert.equal(await first.confirm(), false);
  assert.equal(first.getState().phase, "unknown");

  harness.currentCashier.clear();
  activateCashier(harness.currentCashier, ALL_RETURN_PERMISSIONS);
  const recovered = await harness.runtime.createPresenter();
  assert.equal(await recovered.recoverUnknown(), true);

  const action = harness.ledger.actions()[0];
  assert.equal(action?.status, "completed");
  assert.deepEqual(harness.acknowledgementCalls, [
    action?.allocations[0]?.durableAttemptId,
  ]);
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 1);
});

test("同一 cashier 新 epoch 自动 hydrate 原 action；显式 Confirm 恢复且不再次 prepare/submit", async () => {
  const harness = createHarness({
    onlineSubmitOutcomes: [
      {
        status: "unknown",
        protectedRecoveryKey: "PROTECTED-RECOVERY",
      },
    ],
  });
  const presenter = await harness.runtime.createPresenter();
  presenter.beginNoReceipt();
  await presenter.addNoReceiptProduct("P1");

  assert.equal(await presenter.confirm(), false);
  assert.equal(presenter.getState().phase, "unknown");
  const actionId = harness.ledger.prepared[0]?.actionId;
  assert.ok(actionId);
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 0);
  assert.equal(
    JSON.stringify(presenter.getState()).includes(
      "PROTECTED-RECOVERY",
    ),
    false,
  );

  const originalReturnOrderGuid =
    harness.ledger.actions()[0]?.returnOrderGuid;
  harness.currentCashier.clear();
  activateCashier(
    harness.currentCashier,
    ALL_RETURN_PERMISSIONS,
    {
      cashierName: "Cashier One Renamed",
    },
  );
  assert.equal(await harness.runtime.hasRecoveryRequired(), true);
  const recoveredPresenter =
    await harness.runtime.createPresenter();
  assert.deepEqual(
    {
      phase: recoveredPresenter.getState().phase,
      displayName: recoveredPresenter.getState().lines[0]?.displayName,
      selectedTotalCents:
        recoveredPresenter.getState().selectedTotalCents,
      canConfirm: recoveredPresenter.getState().canConfirm,
    },
    {
      phase: "unknown",
      displayName: "Catalog P1",
      selectedTotalCents: 300,
      canConfirm: false,
    },
  );
  assert.equal(harness.ledger.prepared.length, 1);
  assert.equal(harness.onlinePrepares.length, 1);
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 0);

  const confirmBeforeRecovery = permissionRequests(
    harness,
    POS_RETURN_PERMISSIONS.confirm,
  );
  assert.equal(await recoveredPresenter.recoverUnknown(), true);
  assert.equal(recoveredPresenter.getState().phase, "success");
  assert.equal(harness.ledger.prepared.length, 1);
  assert.equal(harness.onlinePrepares.length, 1);
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 1);
  assert.equal(harness.onlineSubmits[0]?.actionId, actionId);
  assert.equal(harness.onlineRecovers[0]?.actionId, actionId);
  assert.equal(
    harness.ledger.actions()[0]?.returnOrderGuid,
    originalReturnOrderGuid,
  );
  const confirmAfterRecovery = permissionRequests(
    harness,
    POS_RETURN_PERMISSIONS.confirm,
  );
  assert.equal(
    confirmAfterRecovery.length,
    confirmBeforeRecovery.length + 1,
  );
  assert.equal(
    confirmAfterRecovery.at(-1)?.action,
    "recover-return",
  );
  assert.notEqual(
    confirmAfterRecovery.at(-1)?.actionId,
    confirmBeforeRecovery.at(-1)?.actionId,
  );
  assert.deepEqual(
    harness.fulfilmentCalls.map((call) => call.kind),
    ["materialize", "drain"],
  );
});

test("不同 cashier 看不到也不能恢复旧 action，创建 presenter 不触发 provider", async () => {
  const harness = createHarness({
    onlineSubmitOutcomes: [
      {
        status: "unknown",
        protectedRecoveryKey: "PROTECTED-RECOVERY",
      },
    ],
  });
  const presenter = await harness.runtime.createPresenter();
  presenter.beginNoReceipt();
  await presenter.addNoReceiptProduct("P1");
  assert.equal(await presenter.confirm(), false);

  harness.currentCashier.clear();
  activateCashier(
    harness.currentCashier,
    ALL_RETURN_PERMISSIONS,
    { cashierId: "CASHIER-2", cashierName: "Cashier Two" },
  );
  assert.equal(await harness.runtime.hasRecoveryRequired(), false);
  const otherCashierPresenter =
    await harness.runtime.createPresenter();
  assert.equal(otherCashierPresenter.getState().phase, "search");
  assert.equal(harness.onlineSubmits.length, 1);
  assert.equal(harness.onlineRecovers.length, 0);
});

test("恢复 Confirm 权限拒绝保持 Unknown，绝不调用 provider recover", async () => {
  const harness = createHarness({
    supervisorPermissions: [],
    onlineSubmitOutcomes: [
      {
        status: "unknown",
        protectedRecoveryKey: "PROTECTED-RECOVERY",
      },
    ],
  });
  const presenter = await harness.runtime.createPresenter();
  presenter.beginNoReceipt();
  await presenter.addNoReceiptProduct("P1");
  assert.equal(await presenter.confirm(), false);

  harness.currentCashier.clear();
  activateCashier(
    harness.currentCashier,
    [POS_RETURN_PERMISSIONS.view],
  );
  const recoveredPresenter =
    await harness.runtime.createPresenter();
  assert.equal(recoveredPresenter.getState().phase, "unknown");
  assert.equal(harness.onlineRecovers.length, 0);

  const recovering = recoveredPresenter.recoverUnknown();
  await flush();
  assert.deepEqual(
    authorizationState(harness),
    {
      permissionCode: POS_RETURN_PERMISSIONS.confirm,
      action: "recover-return",
    },
  );
  assert.deepEqual(
    await harness.authorizationService.submitSupervisorBarcode(
      "DENY-RETURN-RECOVERY",
    ),
    {
      consumed: true,
      outcome: "denied",
      reason: "PERMISSION_DENIED",
    },
  );
  harness.authorizationService.cancel();
  assert.equal(await recovering, false);
  assert.equal(recoveredPresenter.getState().phase, "unknown");
  assert.equal(harness.onlineRecovers.length, 0);
  assert.equal(harness.ledger.prepared.length, 1);
});

test("恢复前后清除 session 均失败关闭，provider 完成事实不触发第二次恢复", async () => {
  const before = createHarness({
    onlineSubmitOutcomes: [
      { status: "unknown", protectedRecoveryKey: null },
    ],
  });
  await unknownNoReceiptPresenter(before);
  before.currentCashier.clear();
  activateCashier(before.currentCashier, ALL_RETURN_PERMISSIONS);
  const beforeRecoveryPresenter =
    await before.runtime.createPresenter();
  before.currentCashier.clear();
  assert.equal(
    await beforeRecoveryPresenter.recoverUnknown(),
    false,
  );
  assert.equal(before.onlineRecovers.length, 0);

  const recoveryGate = deferred<ReturnAllocationExternalOutcome>();
  const after = createHarness({
    onlineSubmitOutcomes: [
      { status: "unknown", protectedRecoveryKey: null },
    ],
    onlineRecoverImpl: async () => recoveryGate.promise,
  });
  await unknownNoReceiptPresenter(after);
  after.currentCashier.clear();
  activateCashier(after.currentCashier, ALL_RETURN_PERMISSIONS);
  const afterRecoveryPresenter =
    await after.runtime.createPresenter();
  const recovering = afterRecoveryPresenter.recoverUnknown();
  await waitUntil(() => after.onlineRecovers.length === 1);
  after.currentCashier.clear();
  recoveryGate.resolve({ status: "completed" });

  assert.equal(await recovering, false);
  assert.equal(after.onlineRecovers.length, 1);
  assert.equal(after.onlineSubmits.length, 1);
  assert.equal(after.ledger.actions()[0]?.status, "completed");
  assert.equal(
    await afterRecoveryPresenter.recoverUnknown(),
    false,
  );
  assert.equal(after.onlineRecovers.length, 1);
});

test("H9：退款未知主管结案强制另一名主管授权，结案落点收到冻结身份并补发 ACK", async () => {
  let drained = 0;
  const resolved: Record<string, unknown>[] = [];
  const harness = createHarness({
    onlineSubmitOutcomes: [{ status: "unknown", protectedRecoveryKey: null }],
    onlineRecoverImpl: async () => ({ status: "unknown", protectedRecoveryKey: null }),
    supervisorResolve: async (record) => {
      resolved.push({ ...record });
      return "declined";
    },
    supervisorDrain: async () => { drained += 1; },
  });
  const presenter = await unknownNoReceiptPresenter(harness);
  assert.equal(presenter.getState().supervisorResolutionAvailable, true);
  const recoversBefore = harness.onlineRecovers.length;

  const resolving = presenter.resolveBySupervisor({
    finding: "not-refunded",
    evidenceReference: "terminal receipt 8841",
    note: "Checked terminal and portal",
  });
  await flush();
  // 当前收银员拥有 Confirm 权限也必须转交主管。
  assert.equal(harness.authorizationService.getState().kind, "awaiting-supervisor");
  assert.deepEqual(authorizationState(harness), {
    permissionCode: POS_RETURN_PERMISSIONS.confirm,
    action: "resolve-unknown-refund",
  });
  assert.equal(harness.authorizationRequests.at(-1)?.forceSupervisor, true);
  assert.deepEqual(
    await harness.authorizationService.submitSupervisorBarcode("RETURN-RESOLVE-SUPERVISOR"),
    { consumed: true, outcome: "authorized" },
  );
  assert.equal(await resolving, true);
  assert.equal(presenter.getState().errorCode, "RETURN_SUPERVISOR_NOT_REFUNDED");

  // provider 真实结果优先：结案前先按原 attempt 恢复一次。
  assert.equal(harness.onlineRecovers.length, recoversBefore + 1);
  assert.equal(resolved.length, 1);
  assert.equal(resolved[0]?.finding, "not-refunded");
  assert.equal(resolved[0]?.evidenceReference, "terminal receipt 8841");
  assert.equal((resolved[0]?.supervisorActor as { cashierId: string }).cashierId, "SUPERVISOR-1");
  assert.equal((resolved[0]?.requestingActor as { cashierId: string }).cashierId, "CASHIER-1");
  assert.deepEqual(resolved[0]?.scope, { storeCode: STORE_CODE, deviceCode: DEVICE_CODE });
  assert.equal(
    resolved[0]?.authorizationId,
    harness.authorizationRequests.at(-1)?.actionId,
  );
  assert.equal(drained, 1, "结论耐久后补发后端会话 ACK");
  assert.equal(harness.onlineSubmits.length, 1, "绝不重新提交退款");
});

test("H9：主管拒绝/取消时不落结论、不补发 ACK，Unknown 锁定不变", async () => {
  let drained = 0;
  let resolveCalls = 0;
  const harness = createHarness({
    supervisorPermissions: [],
    onlineSubmitOutcomes: [{ status: "unknown", protectedRecoveryKey: null }],
    onlineRecoverImpl: async () => ({ status: "unknown", protectedRecoveryKey: null }),
    supervisorResolve: async () => { resolveCalls += 1; return "declined"; },
    supervisorDrain: async () => { drained += 1; },
  });
  const presenter = await unknownNoReceiptPresenter(harness);
  const resolving = presenter.resolveBySupervisor({
    finding: "not-refunded",
    evidenceReference: "receipt",
    note: "note",
  });
  await flush();
  assert.deepEqual(
    await harness.authorizationService.submitSupervisorBarcode("NO-PERMISSION"),
    { consumed: true, outcome: "denied", reason: "PERMISSION_DENIED" },
  );
  harness.authorizationService.cancel();
  assert.equal(await resolving, false);
  assert.equal(presenter.getState().phase, "unknown");
  assert.equal(presenter.getState().errorCode, "RETURN_SUPERVISOR_REQUIRED");
  assert.equal(resolveCalls, 0);
  assert.equal(drained, 0);
});

test("H9：未配置结案落点的组合根不展示结案入口，resolveUnknown 失败关闭", async () => {
  const harness = createHarness({
    onlineSubmitOutcomes: [{ status: "unknown", protectedRecoveryKey: null }],
  });
  const presenter = await unknownNoReceiptPresenter(harness);
  assert.equal(presenter.getState().supervisorResolutionAvailable, false);
  assert.equal(await presenter.resolveBySupervisor({ finding: "not-refunded", evidenceReference: "r", note: "n" }), false);
  assert.equal(presenter.getState().phase, "unknown");
  assert.equal(harness.authorizationRequests.some((request) => request.action === "resolve-unknown-refund"), false);
});

type HarnessOptions = Readonly<{
  cashierPermissions?: readonly string[];
  supervisorPermissions?: readonly string[];
  online?: boolean;
  fulfilmentFailure?: boolean;
  onlineSubmitOutcomes?: readonly ReturnAllocationExternalOutcome[];
  onlineRecoverImpl?(
    input: Record<string, unknown>,
  ): Promise<ReturnAllocationExternalOutcome>;
  acknowledgementImpl?(attemptId: string): Promise<void>;
  acknowledgementPending?: boolean;
  malformedAcknowledgementResult?: boolean;
  supervisorResolve?(record: ReturnSupervisorResolutionRecord): Promise<"declined" | "waiting">;
  supervisorDrain?(): Promise<unknown>;
}>;

type Harness = ReturnType<typeof createHarness>;

function createHarness(options: HarnessOptions = {}) {
  const cashierPermissions =
    options.cashierPermissions ?? ALL_RETURN_PERMISSIONS;
  const supervisorPermissions =
    options.supervisorPermissions ?? ALL_RETURN_PERMISSIONS;
  const currentCashier = new CurrentCashierSession();
  activateCashier(currentCashier, cashierPermissions);

  const authorizationRequests: OperationAuthorizationRequest[] = [];
  const supervisorBarcodes: string[] = [];
  const audits: AuditEventDraft[] = [];
  const authorizationService = new OperationAuthorizationService({
    cashierAuthentication: {
      async login(input) {
        supervisorBarcodes.push(input.userBarcode);
        return {
          source: "online",
          session: supervisor(supervisorPermissions),
        };
      },
    },
    audit: {
      async append(events) {
        audits.push(...events);
      },
    },
    createId: sequence("authorization-audit"),
    nowIso: () => NOW_ISO,
  });
  const authorization: PosReturnAuthorizationPort = {
    activateRequestingCashier(identity) {
      authorizationService.activateRequestingCashier(identity);
    },
    authorizeAndRun<T>(
      request: OperationAuthorizationRequest,
      operation: (
        context: AuthorizedOperationContext,
      ) => T | Promise<T>,
    ): Promise<OperationAuthorizationResult<T>> {
      authorizationRequests.push(request);
      return authorizationService.authorizeAndRun(
        request,
        operation,
      );
    },
  };

  const recoveryScopes: ReturnRecoveryScope[] = [];
  const ledger = new MemoryReturnLedger(recoveryScopes);
  const capacitySeeds: Record<string, unknown>[] = [];
  const fulfilmentCalls: Readonly<{
    kind: "materialize" | "drain";
    actionId?: string;
  }>[] = [];
  const onlineSubmits: Record<string, unknown>[] = [];
  const onlineRecovers: Record<string, unknown>[] = [];
  const onlinePrepares: Record<string, unknown>[] = [];
  const acknowledgementCalls: string[] = [];
  const acknowledgements: PaymentAcknowledgementRuntimePort | undefined =
    options.acknowledgementImpl
      ? {
          async acknowledge(attemptId) {
            acknowledgementCalls.push(attemptId);
            await options.acknowledgementImpl?.(attemptId);
            if (options.malformedAcknowledgementResult) return undefined as unknown as PaymentAcknowledgementResult;
            return { attempt: {} as never, acknowledged: false, pending: options.acknowledgementPending ?? false, errorCode: null };
          },
        }
      : undefined;
  const submitOutcomes = [
    ...(options.onlineSubmitOutcomes ?? []),
  ];
  const createId = sequence("return-runtime");
  const database = {
    catalogSnapshots: () => ({
      async findExact(query: string) {
        return catalogMatch(query);
      },
      async searchByName(query: string) {
        const match = catalogMatch(query);
        return match ? [match] : [];
      },
    }),
    returnCapacityVault: () => ({
      async seedOrLoad(seed: Record<string, unknown>) {
        capacitySeeds.push(seed);
        return seed;
      },
    }),
    returnExecutionLedger: () => ledger,
  } as unknown as ProductionReturnRuntimeDependencies["database"];
  const repositories = {
    orders: {
      async getByGuid(guid: string) {
        return guid === ORDER_GUID ? receiptOrder() : null;
      },
      async listLocal() {
        return [receiptOrder()];
      },
    },
  } as unknown as ProductionReturnRuntimeDependencies["repositories"];
  const encryptor: SensitivePayloadEncryptor = {
    async encrypt(value) {
      return new TextEncoder().encode(value);
    },
    async decrypt(value) {
      return new TextDecoder().decode(value);
    },
  };

  const runtime = createProductionReturnRuntime({
    database,
    repositories,
    encryptor,
    currentCashier,
    terminal: {
      storeCode: STORE_CODE,
      deviceCode: DEVICE_CODE,
    },
    authorization,
    historyApi: {
      async search() {
        throw transportFailure();
      },
      async getReturnContext() {
        throw transportFailure();
      },
    },
    connectivity: {
      async isOnline() {
        return options.online ?? true;
      },
    },
    cashRefund: {
      async submit() {
        return { status: "completed" };
      },
      async recover() {
        return { status: "completed" };
      },
    },
    onlineRefund: {
      async prepareAttempt(input) {
        onlinePrepares.push({ ...input });
        return {
          attemptKind:
            input.method === "card" ||
            input.method === "voucher"
              ? "payment-provider"
              : "hbpos-api",
          externalActionId: input.externalAttemptId,
          durableAttemptId: `attempt-${input.externalAttemptId}`,
        };
      },
      async submit(input) {
        onlineSubmits.push({ ...input });
        return (
          submitOutcomes.shift() ?? { status: "completed" }
        );
      },
      async recover(input) {
        onlineRecovers.push({ ...input });
        return options.onlineRecoverImpl
          ? options.onlineRecoverImpl({ ...input })
          : { status: "completed" };
      },
    },
    fulfilment: {
      async materializeAction(actionId) {
        fulfilmentCalls.push({
          kind: "materialize",
          actionId,
        });
        if (options.fulfilmentFailure) {
          throw new Error("printer unavailable");
        }
        return { actionId, status: "materialized" };
      },
      async drainPending() {
        fulfilmentCalls.push({ kind: "drain" });
        if (options.fulfilmentFailure) {
          throw new Error("printer unavailable");
        }
        return {
          materialized: 0,
          failed: 0,
          materializedActionIds: [],
          failedActionIds: [],
        };
      },
    },
    acknowledgements,
    ...(options.supervisorResolve
      ? { supervisorResolution: { resolve: options.supervisorResolve } }
      : {}),
    ...(options.supervisorDrain
      ? { supervisorAcknowledgements: { drain: options.supervisorDrain } }
      : {}),
    sha256Hex: async (material) =>
      `digest-${material.length}`,
    createId,
    nowIso: () => NOW_ISO,
  });

  return {
    runtime,
    currentCashier,
    authorizationService,
    authorizationRequests,
    supervisorBarcodes,
    audits,
    ledger,
    capacitySeeds,
    fulfilmentCalls,
    onlinePrepares,
    onlineSubmits,
    onlineRecovers,
    acknowledgementCalls,
    recoveryScopes,
  };
}

class MemoryReturnLedger implements ReturnExecutionLedgerPort {
  public readonly prepared: PrepareDurableReturnAction[] = [];
  public listBarrier: Promise<void> | null = null;
  private readonly byAction = new Map<string, DurableReturnAction>();

  public constructor(
    private readonly recoveryScopes: ReturnRecoveryScope[],
  ) {}

  public actions(): readonly DurableReturnAction[] {
    return [...this.byAction.values()];
  }

  public async prepareOrLoad(
    draft: PrepareDurableReturnAction,
  ): Promise<DurableReturnAction> {
    const existing = this.byAction.get(draft.actionId);
    if (existing) return existing;
    this.prepared.push(draft);
    const action: DurableReturnAction = {
      ...draft,
      lines: draft.lines.map((line) => ({ ...line })),
      allocations: draft.allocations.map((allocation) => ({
        ...allocation,
      })),
      status: "processing",
      completedAtIso: null,
    };
    this.byAction.set(action.actionId, action);
    return action;
  }

  public async load(
    actionId: string,
  ): Promise<DurableReturnAction | null> {
    return this.byAction.get(actionId) ?? null;
  }

  public async listRecoverable(
    scope: ReturnRecoveryScope,
  ) {
    this.recoveryScopes.push({ ...scope });
    if (this.listBarrier) await this.listBarrier;
    const matching = [...this.byAction.values()].filter(
      (action) =>
        (action.status === "processing" ||
          action.status === "unknown") &&
        action.identity.storeCode === scope.storeCode &&
        action.identity.deviceCode === scope.deviceCode &&
        action.identity.cashierId === scope.cashierId,
    );
    if (matching.length > 1) {
      throw new Error("multiple active return actions");
    }
    return matching.map((action) => ({
      actionId: action.actionId,
      returnOrderGuid: action.returnOrderGuid,
      sourceKind: action.plan.sourceKind,
      totalRefundCents: action.plan.totalRefundCents,
      status: action.status as "processing" | "unknown",
      lines: action.lines.map((line) => ({
        sourceKind: line.sourceKind,
        itemNumber: line.itemNumber,
        displayName: line.displayName,
        quantity: line.quantity,
        unitRefundCents: line.unitRefundCents,
        signedAmountCents: line.signedAmountCents,
        syncProvenance: line.syncProvenance,
      })),
    }));
  }

  public async markAllocationSubmitted(input: Readonly<{
    actionId: string;
    allocationId: string;
  }>): Promise<boolean> {
    return this.updateAllocation(
      input.actionId,
      input.allocationId,
      (allocation) =>
        allocation.status === "created"
          ? { ...allocation, status: "submitted" }
          : null,
    );
  }

  public async bindAllocationAttempt(input: Readonly<{
    actionId: string;
    allocationId: string;
    attemptKind: "payment-provider" | "hbpos-api";
    externalActionId: string;
    durableAttemptId: string;
  }>): Promise<boolean> {
    return this.updateAllocation(
      input.actionId,
      input.allocationId,
      (allocation) => ({
        ...allocation,
        externalAttemptKind: input.attemptKind,
        externalActionId: input.externalActionId,
        durableAttemptId: input.durableAttemptId,
      }),
    );
  }

  public async recordAllocationOutcome(input: Readonly<{
    actionId: string;
    allocationId: string;
    expectedStatuses: readonly ("submitted" | "unknown")[];
    status: "completed" | "declined" | "unknown";
    protectedRecoveryKey: string | null;
  }>): Promise<boolean> {
    return this.updateAllocation(
      input.actionId,
      input.allocationId,
      (allocation) =>
        input.expectedStatuses.includes(
          allocation.status as "submitted" | "unknown",
        )
          ? {
              ...allocation,
              status: input.status,
              protectedRecoveryKey:
                input.protectedRecoveryKey,
            }
          : null,
    );
  }

  public async markActionUnknown(input: Readonly<{
    actionId: string;
  }>): Promise<void> {
    this.updateAction(input.actionId, (action) => ({
      ...action,
      status: "unknown",
    }));
  }

  public async resumeUnknownAction(input: Readonly<{
    actionId: string;
  }>): Promise<boolean> {
    const action = this.byAction.get(input.actionId);
    if (!action || action.status !== "unknown") return false;
    this.byAction.set(input.actionId, {
      ...action,
      status: "processing",
    });
    return true;
  }

  public async markActionDeclined(input: Readonly<{
    actionId: string;
  }>): Promise<void> {
    this.updateAction(input.actionId, (action) => ({
      ...action,
      status: "declined",
    }));
  }

  public async completeAtomically(
    input: CompleteDurableReturnAction,
  ): Promise<DurableReturnAction> {
    const action = this.requireAction(input.actionId);
    const completed: DurableReturnAction = {
      ...action,
      status: "completed",
      completedAtIso: input.completedAtIso,
    };
    this.byAction.set(input.actionId, completed);
    return completed;
  }

  private updateAllocation(
    actionId: string,
    allocationId: string,
    update: (
      allocation: DurableReturnAllocation,
    ) => DurableReturnAllocation | null,
  ): boolean {
    const action = this.byAction.get(actionId);
    if (!action) return false;
    let changed = false;
    const allocations = action.allocations.map((allocation) => {
      if (allocation.allocationId !== allocationId) {
        return allocation;
      }
      const next = update(allocation);
      if (!next) return allocation;
      changed = true;
      return next;
    });
    if (changed) {
      this.byAction.set(actionId, {
        ...action,
        allocations,
      });
    }
    return changed;
  }

  private updateAction(
    actionId: string,
    update: (
      action: DurableReturnAction,
    ) => DurableReturnAction,
  ): void {
    this.byAction.set(actionId, update(this.requireAction(actionId)));
  }

  private requireAction(actionId: string): DurableReturnAction {
    const action = this.byAction.get(actionId);
    if (!action) throw new Error("missing action");
    return action;
  }
}

async function selectedReceiptPresenter(harness: Harness) {
  const presenter = await harness.runtime.createPresenter();
  assert.equal(await presenter.loadReceipt(ORDER_GUID), true);
  const line = presenter.getState().lines[0];
  assert.ok(line);
  assert.equal(presenter.setLineQuantity(line.id, 1), true);
  return presenter;
}

async function unknownNoReceiptPresenter(harness: Harness) {
  const presenter = await harness.runtime.createPresenter();
  assert.equal(presenter.beginNoReceipt(), true);
  assert.equal(await presenter.addNoReceiptProduct("P1"), true);
  assert.equal(await presenter.confirm(), false);
  assert.equal(presenter.getState().phase, "unknown");
  return presenter;
}

function activateCashier(
  session: CurrentCashierSession,
  permissionCodes: readonly string[],
  identity: Readonly<{
    cashierId?: string;
    cashierName?: string;
  }> = {},
): void {
  const epoch = session.beginAuthentication();
  session.activate(
    epoch,
    {
      source: "online",
      session: {
        cashierId: identity.cashierId ?? "CASHIER-1",
        userGuid: "cashier-user",
        cashierName: identity.cashierName ?? "Cashier One",
        storeCode: STORE_CODE,
        deviceCode: DEVICE_CODE,
        permissionCodes: [...permissionCodes],
        isEmergencyOverride: false,
        authorizationToken: null,
        authorizationExpiresAtUtc: null,
      },
    },
    {
      storeCode: STORE_CODE,
      deviceCode: DEVICE_CODE,
    },
  );
}

function supervisor(
  permissionCodes: readonly string[],
): CashierSessionDto {
  return {
    cashierId: "SUPERVISOR-1",
    userGuid: "supervisor-user",
    cashierName: "Supervisor",
    storeCode: STORE_CODE,
    deviceCode: DEVICE_CODE,
    permissionCodes: [...permissionCodes],
    isEmergencyOverride: false,
    authorizationToken: "SUPERVISOR-TICKET",
    authorizationExpiresAtUtc:
      "2026-07-28T09:00:00.000Z",
  };
}

function receiptOrder(): LocalOrder {
  return {
    orderGuid: ORDER_GUID,
    localSequence: 7,
    storeCode: STORE_CODE,
    deviceCode: DEVICE_CODE,
    cashierId: "ORIGINAL-CASHIER",
    cashierName: "Original Cashier",
    soldAtIso: NOW_ISO,
    state: "Synced",
    total: money(500),
    discount: money(0),
    actualAmount: money(500),
    originalOrderGuid: null,
    lines: [
      {
        lineId: "detail-1",
        productCode: "P1",
        itemNumber: "ITEM-1",
        lookupCode: "LOOKUP-1",
        displayName: "Receipt Product",
        quantity: "2",
        unitPrice: money(250),
        discount: money(0),
        actualAmount: money(500),
        priceSource: "catalog",
        syncProvenance: {
          referenceCode: "RECEIPT-REF",
          priceSource: 2,
        },
        kind: "sale",
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
      },
    ],
    tenders: [
      {
        tenderGuid: "cash-tender",
        method: "cash",
        amount: money(500),
        reference: null,
        reservationToken: null,
      },
    ],
  };
}

function catalogMatch(queryInput: string): LocalCatalogMatch | null {
  const query = queryInput.trim().toUpperCase();
  if (query === "OPENITEM") {
    return {
      ...catalogBase(),
      productCode: "OPEN-PRODUCT",
      itemNumber: "OPENITEM",
      lookupCode: "OPENITEM",
      lookupCodeNormalized: "OPENITEM",
      displayName: "Open Item",
    };
  }
  if (query !== "P1" && query !== "P2") return null;
  return {
    ...catalogBase(),
    productCode: query,
    itemNumber: `ITEM-${query}`,
    lookupCode: query,
    lookupCodeNormalized: query,
    displayName: `Catalog ${query}`,
  };
}

function catalogBase(): LocalCatalogMatch {
  return {
    storeCode: STORE_CODE,
    productCode: "P1",
    referenceCode: "CATALOG-REF",
    itemNumber: "ITEM-P1",
    displayName: "Catalog P1",
    barcode: null,
    lookupCode: "P1",
    lookupCodeNormalized: "P1",
    retailPriceCents: 300,
    priceSource: 3,
    priceSourceLabel: "catalog",
    quantityFactor: 1,
    taxRateBasisPoints: null,
    updatedAtIso: null,
    rowVersion: null,
    productImage: null,
    discountRate: null,
    isSpecialProduct: false,
  };
}

function permissionRequests(
  harness: Harness,
  permissionCode: string,
): readonly OperationAuthorizationRequest[] {
  return harness.authorizationRequests.filter(
    (request) => request.permissionCode === permissionCode,
  );
}

function authorizationState(harness: Harness): Readonly<{
  permissionCode: string;
  action: string;
}> | null {
  const state = harness.authorizationService.getState();
  return state.kind === "awaiting-supervisor"
    ? {
        permissionCode: state.permissionCode,
        action: state.action,
      }
    : null;
}

function authorizationPendingActionId(harness: Harness): string {
  const state = harness.authorizationService.getState();
  assert.equal(state.kind, "awaiting-supervisor");
  return state.actionId;
}

function transportFailure(): HbposApiError {
  return new HbposApiError("offline", { kind: "transport" });
}

function sequence(prefix: string): () => string {
  let value = 0;
  return () => `${prefix}-${++value}`;
}

function money(cents: number) {
  return { currency: "AUD" as const, cents };
}

async function flush(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
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
  for (let attempt = 0; attempt < 30; attempt += 1) {
    if (predicate()) return;
    await flush();
  }
  throw new Error("condition not reached");
}
