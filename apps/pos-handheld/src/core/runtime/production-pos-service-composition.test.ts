import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";

import {
  UPDATE_TRANSITION_IN_PROGRESS,
  UpdateTransitionLeaseCoordinator,
} from "../../features/app-updates/update-transition-lease-coordinator";
import {
  calculateCatalogPageChecksum,
  type CatalogPageDigest,
  type CatalogLookupItem,
} from "../../features/catalog/hbpos-catalog-remote";
import { CATALOG_DOWNLOAD_PERMISSION } from "@hb/pos-domain/features/catalog/maintenance/catalog-maintenance-authorization";
import type { CashCheckoutResult } from "../../features/checkout/cash";
import {
  HOLD_ORDER_PERMISSION,
  RECALL_LIST_PERMISSION,
  RECALL_RESTORE_PERMISSION,
} from "../../features/held-orders/held-orders-domain";
import {
  INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
  INSTALLMENTS_VIEW_PERMISSION,
} from "@hb/pos-domain/features/installments/installment-authorization";
import {
  type LocalHistoryDetails,
  type LocalHistoryPage,
  type LocalHistoryQuery,
} from "../../features/local-history/local-history-domain";
import {
  LOCAL_HISTORY_REPRINT_PERMISSION,
  LOCAL_HISTORY_VIEW_PERMISSION,
} from "../../features/local-history/local-history-presenter";
import { PAYMENT_PERMISSION } from "../../features/payments/runtime/payment-checkout-runtime";
import { installmentRepaymentPaymentEntry } from "../../features/payments/ui/unified-payment-entry";
import {
  REMOTE_HISTORY_REPRINT_PERMISSION,
  REMOTE_HISTORY_VIEW_PERMISSION,
} from "@hb/pos-domain/features/remote-history/remote-history-presenter";
import { PricingCart } from "../../features/sales/domain";
import { SALES_PERMISSIONS } from "@hb/pos-domain/features/sales/runtime/sales-operation-security";
import {
  SETTINGS_APP_UPDATE_PERMISSION,
  SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
  SETTINGS_CATALOG_RESET_PERMISSION,
  SETTINGS_DEVICE_REGISTRATION_PERMISSION,
  SETTINGS_PAYMENT_TERMINAL_PERMISSION,
  SETTINGS_RECEIPT_PRINTER_PERMISSION,
  SETTINGS_VIEW_PERMISSION,
} from "../../features/settings/settings-authorization";
import type {
  LocalSyncHistoryOrder,
  LocalSyncHistorySupportContext,
} from "../../features/sync-history";
import {
  SYNC_HISTORY_EXPORT_PERMISSION,
  SYNC_HISTORY_MANUAL_SYNC_PERMISSION,
  SYNC_HISTORY_VIEW_PERMISSION,
} from "@hb/pos-sync/features/sync-history/sync-history-authorization";
import type {
  HbposTransport,
  HbposTransportRequest,
} from "../api/hbpos-api";
import { HbposApiError } from "../api/hbpos-api";
import type {
  DailyCloseArchiveCommit,
  DailyCloseRepositoryPort,
  DailyCloseScope,
  DailyCloseSummary,
  HeldOrderRecordRepositoryPort,
  SpecialProductItem,
  TerminalCartFence,
} from "../contracts";
import type {
  AuditEventDraft,
  DurableCashOrderCommit,
  LocalOrder,
} from "@hb/pos-domain/core/contracts/order";
import type {
  ActiveCatalogMetadata,
  ActiveCatalogPromotions,
  LocalCatalogMatch,
} from "../db/catalog-repository";
import { PosDatabase } from "../db/pos-database";
import type { ReceiptPrinterSettings } from "../db/pos-settings-repository";
import type { SqliteInstallmentSnapshotRepository } from "../db/sqlite-installment-snapshot-repository";
import type { SensitivePayloadEncryptor } from "../db/sqlite-repositories";
import {
  DeviceSessionCoordinator,
  type DeviceSessionApi,
} from "../security/device-session";
import {
  DeviceLockStore,
  DeviceCredentialStore,
  InMemorySecureStore,
  InstallationIdentityStore,
  PendingDeviceActivationCodeStore,
  PendingDeviceRegistrationStore,
} from "../security/secure-storage";

import { reregisterSettingsDevice } from "./expo-settings-configuration";
import type { PaymentProviderRuntimeBootstrap } from "./payment-provider-runtime-bootstrap";
import type {
  InstallmentProviderAttemptPlan,
  InstallmentProviderAttemptStorePort,
} from "./production-installment-payment-adapter";
import type {
  InstallmentActionStorePort,
  InstallmentPerformanceEvent,
  PersistedInstallmentAction,
} from "./production-installment-runtime";
import {
  createPostCommitWorkDrain,
  createPostCommitFulfilmentCashCheckout,
  createProductionPosRuntimeServices,
  type ProductionSettingsRuntimeConfiguration,
} from "./production-pos-service-composition";

const ids = [
  "00000000-0000-4000-8000-000000000001",
  "00000000-0000-4000-8000-000000000002",
  "00000000-0000-4000-8000-000000000003",
  "00000000-0000-4000-8000-000000000004",
  "00000000-0000-4000-8000-000000000005",
  "00000000-0000-4000-8000-000000000006",
  "00000000-0000-4000-8000-000000000007",
  "00000000-0000-4000-8000-000000000008",
] as const;

const nodeCatalogPageDigest: CatalogPageDigest = async (payload) =>
  createHash("sha256").update(payload, "utf8").digest("hex");

function deferred<T>(): Readonly<{
  promise: Promise<T>;
  resolve(value: T | PromiseLike<T>): void;
}> {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((accept) => {
    resolve = accept;
  });
  return { promise, resolve };
}

class RecordingUpdateTransitionLeaseCoordinator extends UpdateTransitionLeaseCoordinator {
  public operationCalls = 0;

  public override runOperation<T>(
    operation: () => T | Promise<T>,
  ): Promise<T> {
    this.operationCalls += 1;
    return super.runOperation(operation);
  }
}

test("生产组合只暴露 route 所需业务面，并以 DurableCashCheckoutService 提交现金订单", async () => {
  let nextId = 0;
  const durableCommits: DurableCashOrderCommit[] = [];
  let hardwareCalls = 0;
  let frozenSettingsReads = 0;
  let cashierAuthorizationClears = 0;
  let cashierLockNotifications = 0;
  let preparedReprint: Readonly<{
    orderGuid: string;
    printerId: string;
    receiptBytes: Uint8Array;
  }> | null = null;
  const services = createProductionPosRuntimeServices({
    database: databaseFor(durableCommits, {
      lastOrder: lastCashOrder(),
      onFrozenSettingsRead: () => { frozenSettingsReads += 1; },
      onReprintPrepared: (input) => { preparedReprint = input; },
    }),
    transport: {} as HbposTransport,
    encryptor: encryptor(),
    syncSecurity: { async lockDevice() {} },
    auditMetadata: {
      storeCode: "S001",
      deviceCode: "IPAD-1",
      appVersion: "0.1.0-test",
      instanceId: ids[0],
    },
    supportAppId: "com.hbweb.posipad",
    clock: {
      now: () => new Date("2026-07-28T00:00:00.000Z"),
      nowIso: () => "2026-07-28T00:00:00.000Z",
    },
    createId: () => ids[nextId++] ?? "00000000-0000-4000-8000-000000000099",
    random: () => 0.5,
    sha256Hex: async (material) => `sha256:${material}`,
    catalogPageDigest: nodeCatalogPageDigest,
    createPrinter: () => ({
      async connect() {
        hardwareCalls += 1;
      },
      async print() {
        hardwareCalls += 1;
        return { status: "printed", errorCode: null } as const;
      },
      async open() {
        hardwareCalls += 1;
        return { status: "completed", errorCode: null } as const;
      },
    }),
    connectivity: { async isOnline() { return true; } },
    cashierAuthentication: {
      async login(request) {
        const canOpenDrawer = request.userBarcode === "cashier-drawer";
        return {
          source: "online",
          session: {
            authorizationToken: "cashier-session-secret",
            authorizationExpiresAtUtc: "2026-07-29T00:00:00.000Z",
            emergencyGrantId: "must-not-leak",
            cashierId: canOpenDrawer ? "cashier-2" : "cashier-1",
            userGuid: "user-1",
            cashierName: canOpenDrawer ? "Next Cashier" : "Cashier",
            storeCode: request.storeCode,
            deviceCode: request.deviceCode,
            permissionCodes: canOpenDrawer
              ? [
                  "Permissions.PosTerminal.CashDrawer.Open",
                  SALES_PERMISSIONS.view,
                  SALES_PERMISSIONS.addItem,
                ]
              : [
                  "Permissions.PosTerminal.Receipt.PrintLast",
                  SALES_PERMISSIONS.view,
                  SALES_PERMISSIONS.addItem,
                  SALES_PERMISSIONS.changePrice,
                ],
          },
        };
      },
    },
    cashierSessionSecurity: {
      async getDeviceIdentity() {
        return { storeCode: "S001", deviceCode: "IPAD-1" };
      },
      async clearAuthorization() {
        cashierAuthorizationClears += 1;
      },
      invalidateAuthorizationForDeviceScope() {
        cashierAuthorizationClears += 1;
      },
      subscribeSessionInvalidation() {
        return () => undefined;
      },
    },
    newTransactionGate: {
      getGate: () => ({
        state: "enabled",
        canStartNewTransaction: true,
        canContinueRecovery: true,
      }),
    },
    operationAuthorization: {
      cashierAuthentication: {
        async login() {
          throw new Error("supervisor scan is not used in this test");
        },
      },
    },
    cashierLock: {
      onLocked() {
        cashierLockNotifications += 1;
      },
    },
  });

  assert.equal(hardwareCalls, 0, "组合根启动不得连接或操作硬件");
  await services.initialize();
  assert.equal("database" in services, false);
  assert.equal("encryptor" in services, false);
  assert.equal("transport" in services, false);
  assert.equal(services.payments.status, "unavailable");
  if (services.payments.status === "unavailable") {
    assert.deepEqual(services.payments.blockers, [
      "SQUARE_TERMINAL_CONFIGURATION_MISSING",
      "LINKLY_ENVIRONMENT_MISSING",
      "VOUCHER_PROTECTED_TOKEN_STORE_MISSING",
      "APPROVED_PAYMENT_COMPLETION_PLANNER_MISSING",
    ]);
  }
  assert.equal(services.capabilities.cashCheckout.status, "available");
  assert.equal(services.capabilities.offlineReturns.status, "available");
  assert.equal(services.capabilities.returns.status, "available");
  assert.equal(services.capabilities.cashDrawer.status, "available");
  assert.equal(services.capabilities.receiptReprint.status, "available");
  assert.equal(
    services.capabilities.supervisorAuthorization.status,
    "available",
  );
  assert.equal(services.operationAuthorization.status, "available");
  assert.equal(
    services.operationAuthorization.status === "available" &&
      "service" in services.operationAuthorization,
    false,
    "route 不能取得原始授权服务或会话激活入口",
  );
  assert.equal("cashierAuthentication" in services, false);
  assert.equal(services.returns.status, "available");
  assert.equal(
    services.returns.status === "available" &&
      ("database" in services.returns ||
        "ledger" in services.returns ||
        "authorization" in services.returns),
    false,
    "route 不能取得退货数据库、账本或原始授权服务",
  );
  assert.throws(
    () => services.remoteHistory.createPresenter({ online: true }),
    /CURRENT_CASHIER_REQUIRED/,
    "远程历史不得接受 route 伪造的门店、终端或权限身份",
  );
  const cashierSummary = await services.cashierSession.signIn(
    "cashier-no-drawer",
  );
  assert.deepEqual(cashierSummary, {
    cashierId: "cashier-1",
    userGuid: "user-1",
    cashierName: "Cashier",
    storeCode: "S001",
    deviceCode: "IPAD-1",
    permissions: [
      "Permissions.PosTerminal.Receipt.PrintLast",
      SALES_PERMISSIONS.addItem,
      SALES_PERMISSIONS.changePrice,
      SALES_PERMISSIONS.view,
    ],
    source: "online",
  });
  assert.doesNotMatch(
    JSON.stringify(cashierSummary),
    /session|token|expires|emergencyGrant/i,
  );
  const remoteHistoryWithoutTrustedPermission =
    services.remoteHistory.createPresenter({ online: false });
  assert.equal(
    remoteHistoryWithoutTrustedPermission.getState().kind,
    "unauthorized",
  );
  assert.equal(
    remoteHistoryWithoutTrustedPermission.getState().filters.deviceCode,
    null,
  );
  remoteHistoryWithoutTrustedPermission.destroy();
  const syncHistoryPresenter = services.syncHistory.createPresenter(
    cashierSummary.permissions,
  );
  assert.equal(syncHistoryPresenter.getState().kind, "empty");
  assert.equal(syncHistoryPresenter.getState().access.canView, false);
  await assert.rejects(
    () => syncHistoryPresenter.createSupportExport(),
    /permission-required/,
  );
  syncHistoryPresenter.destroy();
  assert.equal(services.fulfilment.reprint.status, "available");
  const settingsReadsBeforeReprint = frozenSettingsReads;
  const reprintResult = await services.fulfilment.reprint.execute();
  const capturedReprint = preparedReprint as Readonly<{
    orderGuid: string;
    printerId: string;
    receiptBytes: Uint8Array;
  }> | null;
  assert.deepEqual(reprintResult, { state: "Printed", errorCode: null });
  assert.equal(
    frozenSettingsReads,
    settingsReadsBeforeReprint + 1,
    "重打单次准备只能读取一次持久设置",
  );
  assert.equal(capturedReprint?.orderGuid, "receipt-order-1");
  assert.equal(capturedReprint?.printerId, "printer-1");
  assert.match(
    new TextDecoder().decode(capturedReprint?.receiptBytes),
    /\*\*\* REPRINT \*\*\*/,
  );

  const presenter = services.sales.createPresenter();
  assert.deepEqual(presenter.getState().capabilities, {
    catalog: true,
    cartEditing: true,
    cashCheckout: true,
    hold: true,
    lock: true,
  });
  presenter.setQuery("930000000001");
  assert.equal(await presenter.addLookupCode(), true);
  assert.equal(presenter.getState().cart.lines.length, 1);

  assert.equal(await presenter.openCash(), true);
  presenter.setExactCash();
  assert.equal(await presenter.submitCash(), true);
  assert.equal(durableCommits.length, 1);
  assert.equal(
    presenter.getState().success?.drawerDisposition,
    "permission-denied",
  );
  assert.equal(presenter.startNewSale(), true);
  presenter.setQuery("930000000001");
  assert.equal(await presenter.addLookupCode(), true);
  const clearsBeforeLock = cashierAuthorizationClears;
  assert.equal(await presenter.lockTerminal(), true);
  assert.equal(cashierAuthorizationClears, clearsBeforeLock + 1);
  assert.equal(cashierLockNotifications, 1);
  assert.deepEqual(
    services.operationAuthorization.status === "available"
      ? services.operationAuthorization.getState()
      : null,
    { kind: "idle" },
  );
  assert.throws(
    () => services.sales.createPresenter(),
    /CURRENT_CASHIER_REQUIRED/,
  );
  assert.equal(await presenter.openCash(), false);
  presenter.setExactCash();
  assert.equal(await presenter.submitCash(), false);
  assert.equal(durableCommits.length, 1, "旧 presenter 不得在锁屏后继续收款");
  presenter.destroy();

  await services.cashierSession.signIn("cashier-drawer");
  const unlockedPresenter = services.sales.createPresenter();
  assert.equal(
    unlockedPresenter.getState().cart.lines.length,
    1,
    "锁屏后重新认证必须保留当前购物车",
  );
  assert.equal(await unlockedPresenter.openCash(), true);
  unlockedPresenter.setExactCash();
  assert.equal(await unlockedPresenter.submitCash(), true);
  assert.equal(durableCommits.length, 2);
  assert.equal(unlockedPresenter.getState().success?.drawerDisposition, "queued");
  assert.equal(
    durableCommits[0]?.terminalContext.kind,
    "none",
    "普通销售必须由共享 terminal session 注入 none 上下文",
  );
  assert.equal("checkout" in services, false, "route 不得绕过共享购物车直接提交");
  unlockedPresenter.destroy();
});

test("销售履约 facade 缺少主管服务时按当前收银员权限 fail-closed", async () => {
  let drawerOpens = 0;
  const services = createTestComposition(databaseFor([]), {
    onDrawerOpen() {
      drawerOpens += 1;
    },
  });
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  assert.deepEqual(
    await services.fulfilment.openCashDrawer.execute(),
    {
      state: "denied",
      errorCode: "PERMISSION_DENIED",
    },
  );
  assert.equal(drawerOpens, 0);
});

test("关闭自动打印后，最后小票与支付成功精确订单仍可经正式授权手动重打", async () => {
  const order = lastCashOrder();
  const preparedOrderGuids: string[] = [];
  const printedJobIds: string[] = [];
  const services = createTestComposition(
    databaseFor([], {
      lastOrder: order,
      onReprintPrepared(input) {
        preparedOrderGuids.push(input.orderGuid);
      },
    }),
    {
      cashierPermissions: [
        "Permissions.PosTerminal.Receipt.PrintLast",
      ],
      onPrint(jobId) {
        printedJobIds.push(jobId);
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  const currentSettings = await services.receiptSettings.get();
  await services.receiptSettings.save({
    ...currentSettings,
    printEnabled: false,
  });

  const lastResult = await services.fulfilment.reprint.execute();
  const currentResult = await services.fulfilment.reprintCurrentReceipt(
    order.orderGuid,
  );

  assert.deepEqual(lastResult, { state: "Printed", errorCode: null });
  assert.deepEqual(currentResult, { state: "Printed", errorCode: null });
  assert.deepEqual(preparedOrderGuids, [order.orderGuid, order.orderGuid]);
  assert.equal(printedJobIds.length, 2);
});

test("支付成功重打在本地普通订单缺失时回退可信分期详情", async () => {
  const installmentGuid = "10000000-0000-4000-8000-000000000001";
  const ordinaryOrder = lastCashOrder();
  const installmentRequests: HbposTransportRequest[] = [];
  const printedBytes: Uint8Array[] = [];
  const database = databaseFor([], { lastOrder: ordinaryOrder });
  Object.assign(database, {
    installmentSnapshots: () => ({
      async upsertForStore() {},
      async listForStore() {
        return [];
      },
    }),
    installmentActions: () => ({
      async loadBlocking() {
        return null;
      },
    }),
    installmentPaymentPersistence: () => ({
      providerAttempts: {},
      voucherIntents: {},
      voucherProtectedTokens: {},
      voucherContextForAttempt: async () => {
        throw new Error("not called by this composition test");
      },
      voucherMaterials: {},
      refundProvenance: {},
    }),
  });
  const configuredAvailability = {
    getAvailability(provider: "square" | "linkly-cloud" | "voucher") {
      return {
        provider,
        available: false,
        blocker: "PAYMENT_PROVIDER_UNKNOWN" as const,
      };
    },
    listAvailability() {
      return [];
    },
  };
  const bootstrap = {
    providers: {
      ...configuredAvailability,
      get() {
        throw new Error("provider is outside this composition test");
      },
      listAvailableProviders() {
        return [];
      },
      getVoucherApprovedPurchaseReleasePort() {
        return {
          status: "unavailable" as const,
          reason: "PAYMENT_PROVIDER_UNKNOWN" as const,
        };
      },
    },
    configurationAvailability: configuredAvailability,
    bindVoucherContextProvider() {},
    createLinklyOperator() {
      return null;
    },
  } as PaymentProviderRuntimeBootstrap;
  const services = createTestComposition(database, {
    cashierPermissions: ["Permissions.PosTerminal.Receipt.PrintLast"],
    installmentBootstrap: bootstrap,
    transport: {
      async request<T>(request: HbposTransportRequest) {
        installmentRequests.push(request);
        return {
          status: 200,
          data: {
            success: true,
            data: {
              installmentGuid,
              installmentNumber: "INS-100",
              storeCode: "S001",
              deviceCode: "IPAD-1",
              cashierId: "cashier-1",
              cashierName: "Cashier",
              customerName: "Customer One",
              customerPhone: "0400000000",
              createdAt: "2026-08-01T01:00:00Z",
              updatedAt: "2026-08-04T00:00:00Z",
              totalAmount: 10,
              minimumDownPayment: 2,
              downPaymentAmount: 4,
              paidAmount: 6,
              balanceAmount: 4,
              status: 1,
              lines: [{
                installmentLineGuid:
                  "30000000-0000-4000-8000-000000000001",
                productCode: "P-1",
                referenceCode: null,
                displayName: "Spring water",
                lookupCode: "930000000001",
                quantity: 1,
                unitPrice: 10,
                discountAmount: 0,
                actualAmount: 10,
                itemNumber: "SKU-1",
              }],
              payments: [{
                paymentGuid: "20000000-0000-4000-8000-000000000001",
                method: 1,
                amount: 6,
                reference: null,
                status: 1,
                recordedAt: "2026-08-04T00:00:00Z",
                cashierId: "cashier-1",
                deviceCode: "IPAD-1",
                idempotencyKey: "test-idempotency-key",
                cardTransactions: [],
              }],
              pickupInfo: null,
              cancellationInfo: null,
              note: null,
            },
          } as T,
        };
      },
    },
    onPrint(_jobId, bytes) {
      printedBytes.push(bytes);
    },
  });
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  const result = await services.fulfilment.reprintCurrentReceipt(
    installmentGuid,
  );

  assert.deepEqual(result, { state: "Printed", errorCode: null });
  assert.deepEqual(installmentRequests.map((request) => request.url), [
    `/api/v1/installments/${installmentGuid}`,
  ]);
  assert.equal(printedBytes.length, 1);
  assert.match(new TextDecoder().decode(printedBytes[0]), /INS-100/u);

  assert.deepEqual(
    await services.fulfilment.reprintCurrentReceipt(ordinaryOrder.orderGuid),
    { state: "Printed", errorCode: null },
  );
  assert.equal(installmentRequests.length, 1);
  assert.equal(printedBytes.length, 2);
});

test("支付成功精确订单重打复用在途动作，并只接受 PrintLast 主管授权", async () => {
  const order = lastCashOrder();
  const printedJobIds: string[] = [];
  const services = createTestComposition(
    databaseFor([], { lastOrder: order }),
    {
      supervisorPermissions: [
        "Permissions.PosTerminal.Receipt.PrintLast",
      ],
      onPrint(jobId) {
        printedJobIds.push(jobId);
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  if (services.operationAuthorization.status !== "available") {
    throw new Error("operation authorization must be available");
  }

  const first = services.fulfilment.reprintCurrentReceipt(order.orderGuid);
  const duplicate = services.fulfilment.reprintCurrentReceipt(order.orderGuid);
  assert.strictEqual(duplicate, first);
  const authorization = services.operationAuthorization.getState();
  assert.equal(authorization.kind, "awaiting-supervisor");
  if (authorization.kind !== "awaiting-supervisor") {
    throw new Error("current receipt authorization must be pending");
  }
  assert.equal(
    authorization.permissionCode,
    "Permissions.PosTerminal.Receipt.PrintLast",
  );
  assert.equal(authorization.action, "reprint-current-receipt");

  assert.equal(
    (
      await services.operationAuthorization.submitSupervisorBarcode(
        "supervisor",
      )
    ).outcome,
    "authorized",
  );
  assert.deepEqual(await first, { state: "Printed", errorCode: null });
  assert.deepEqual(await duplicate, { state: "Printed", errorCode: null });
  assert.equal(printedJobIds.length, 1);
});

test("销售履约 facade 复用并发动作的 Promise 和 actionId，并通过主管授权后只触发一次硬件", async () => {
  let drawerOpens = 0;
  let printCalls = 0;
  let settingsReads = 0;
  const services = createTestComposition(
    databaseFor([], {
      lastOrder: lastCashOrder(),
      onFrozenSettingsRead() {
        settingsReads += 1;
      },
    }),
    {
      supervisorPermissions: [
        "Permissions.PosTerminal.CashDrawer.Open",
        "Permissions.PosTerminal.Receipt.PrintLast",
      ],
      onDrawerOpen() {
        drawerOpens += 1;
      },
      onPrint() {
        printCalls += 1;
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  if (services.operationAuthorization.status !== "available") {
    throw new Error("operation authorization must be available");
  }

  const readsBeforeDrawer = settingsReads;
  const drawerFirst = services.fulfilment.openCashDrawer.execute();
  const drawerDuplicate = services.fulfilment.openCashDrawer.execute();
  assert.strictEqual(
    drawerDuplicate,
    drawerFirst,
    "并发开箱必须复用同一个在途 Promise",
  );
  const drawerAuthorization =
    services.operationAuthorization.getState();
  assert.equal(drawerAuthorization.kind, "awaiting-supervisor");
  if (drawerAuthorization.kind !== "awaiting-supervisor") {
    throw new Error("drawer authorization must be pending");
  }
  assert.equal(
    drawerAuthorization.permissionCode,
    "Permissions.PosTerminal.CashDrawer.Open",
  );
  assert.equal(drawerAuthorization.action, "open-cash-drawer");
  assert.equal(
    (
      await services.operationAuthorization.submitSupervisorBarcode(
        "supervisor",
      )
    ).outcome,
    "authorized",
  );
  assert.deepEqual(await drawerFirst, {
    state: "Completed",
    errorCode: null,
  });
  assert.deepEqual(await drawerDuplicate, {
    state: "Completed",
    errorCode: null,
  });
  assert.equal(drawerOpens, 1);
  assert.equal(
    settingsReads,
    readsBeforeDrawer + 1,
    "单次手动开箱只能读取一次持久设置",
  );

  const readsBeforeReprint = settingsReads;
  const reprintFirst = services.fulfilment.reprint.execute();
  const reprintDuplicate = services.fulfilment.reprint.execute();
  assert.strictEqual(
    reprintDuplicate,
    reprintFirst,
    "并发重打必须复用同一个在途 Promise",
  );
  const reprintAuthorization =
    services.operationAuthorization.getState();
  assert.equal(reprintAuthorization.kind, "awaiting-supervisor");
  if (reprintAuthorization.kind !== "awaiting-supervisor") {
    throw new Error("reprint authorization must be pending");
  }
  assert.equal(
    reprintAuthorization.permissionCode,
    "Permissions.PosTerminal.Receipt.PrintLast",
  );
  assert.equal(reprintAuthorization.action, "reprint-last-receipt");
  assert.equal(
    (
      await services.operationAuthorization.submitSupervisorBarcode(
        "supervisor",
      )
    ).outcome,
    "authorized",
  );
  assert.deepEqual(await reprintFirst, {
    state: "Printed",
    errorCode: null,
  });
  assert.deepEqual(await reprintDuplicate, {
    state: "Printed",
    errorCode: null,
  });
  assert.equal(printCalls, 1);
  assert.equal(
    settingsReads,
    readsBeforeReprint + 1,
    "单次最后小票重打只能读取一次持久设置",
  );
});

test("销售履约把 cashier lease 守卫传入硬件队列，失效后排队开箱不读取设置、不落任务也不发脉冲", async () => {
  let invalidate!: () => void;
  let releasePrint!: () => void;
  const printReleased = new Promise<void>((resolve) => {
    releasePrint = resolve;
  });
  let settingsReads = 0;
  let printCalls = 0;
  let drawerOpens = 0;
  const services = createTestComposition(
    databaseFor([], {
      lastOrder: lastCashOrder(),
      onFrozenSettingsRead() {
        settingsReads += 1;
      },
    }),
    {
      cashierPermissions: [
        "Permissions.PosTerminal.Receipt.PrintLast",
        "Permissions.PosTerminal.CashDrawer.Open",
      ],
      captureInvalidation(listener) {
        invalidate = listener;
      },
      onPrint() {
        printCalls += 1;
      },
      waitForPrint: () => printReleased,
      onDrawerOpen() {
        drawerOpens += 1;
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  const readsBeforeReprint = settingsReads;

  const reprint = services.fulfilment.reprint.execute();
  while (printCalls === 0) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  const drawer = services.fulfilment.openCashDrawer.execute();
  invalidate();
  releasePrint();

  const settled = await Promise.allSettled([reprint, drawer]);
  assert.deepEqual(
    settled.map((result) => result.status),
    ["fulfilled", "rejected"],
  );
  assert.deepEqual(
    settled[0]?.status === "fulfilled"
      ? settled[0].value
      : null,
    { state: "Printed", errorCode: null },
  );
  assert.equal(printCalls, 1);
  assert.equal(drawerOpens, 0);
  assert.equal(
    settingsReads,
    readsBeforeReprint + 1,
    "排队开箱在取得 hardwareTail 后先复核 lease，不能读取设置或创建 Requested 事件",
  );
});

test("履约硬件与终态已成功后 lease 才失效，facade 保留真实结果且并发调用不创建第二动作", async () => {
  let invalidate!: () => void;
  let reprintJobs = 0;
  let drawerEvents = 0;
  let printCalls = 0;
  let drawerOpens = 0;
  const terminalOrder: ("reprint" | "drawer")[] = [];
  const services = createTestComposition(
    databaseFor([], {
      lastOrder: lastCashOrder(),
      onReprintPrepared() {
        reprintJobs += 1;
      },
      onManualDrawerCreated() {
        drawerEvents += 1;
      },
      onFulfilmentTerminalPersisted(kind) {
        terminalOrder.push(kind);
        invalidate();
      },
    }),
    {
      cashierPermissions: [
        "Permissions.PosTerminal.Receipt.PrintLast",
        "Permissions.PosTerminal.CashDrawer.Open",
      ],
      captureInvalidation(listener) {
        invalidate = listener;
      },
      onPrint() {
        printCalls += 1;
      },
      onDrawerOpen() {
        drawerOpens += 1;
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  const reprint = services.fulfilment.reprint.execute();
  const duplicateReprint = services.fulfilment.reprint.execute();
  assert.strictEqual(duplicateReprint, reprint);
  assert.deepEqual(await reprint, {
    state: "Printed",
    errorCode: null,
  });
  assert.deepEqual(await duplicateReprint, {
    state: "Printed",
    errorCode: null,
  });
  assert.equal(reprintJobs, 1);
  assert.equal(printCalls, 1);

  await services.cashierSession.signIn("cashier");
  const drawer = services.fulfilment.openCashDrawer.execute();
  const duplicateDrawer =
    services.fulfilment.openCashDrawer.execute();
  assert.strictEqual(duplicateDrawer, drawer);
  assert.deepEqual(await drawer, {
    state: "Completed",
    errorCode: null,
  });
  assert.deepEqual(await duplicateDrawer, {
    state: "Completed",
    errorCode: null,
  });
  assert.equal(drawerEvents, 1);
  assert.equal(drawerOpens, 1);
  assert.deepEqual(terminalOrder, ["reprint", "drawer"]);
});

test("生产销售本地目录未命中且在线时使用可信门店做远程精确回退", async () => {
  const requests: HbposTransportRequest[] = [];
  const lookupCode = "930000000999";
  let markRemoteLookupStarted!: () => void;
  const remoteLookupStarted = new Promise<void>((resolve) => {
    markRemoteLookupStarted = resolve;
  });
  let releaseRemoteLookup!: () => void;
  const remoteLookupReleased = new Promise<void>((resolve) => {
    releaseRemoteLookup = resolve;
  });
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      requests.push(request);
      markRemoteLookupStarted();
      await remoteLookupReleased;
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S001",
            lookupCode,
            lookupCodeNormalized: lookupCode,
            found: true,
            item: {
              storeCode: "S001",
              productCode: "P-REMOTE",
              referenceCode: "REF-REMOTE",
              displayName: "Remote tea",
              lookupCode,
              lookupCodeNormalized: lookupCode,
              itemNumber: "I-REMOTE",
              barcode: lookupCode,
              retailPrice: 2.5,
              priceSource: 1,
              priceSourceLabel: "Store retail",
              quantityFactor: 1,
              updatedAt: "2026-07-28T00:00:00.000Z",
              rowVersion: "remote-row-1",
              productImage: null,
              discountRate: null,
              isSpecialProduct: false,
            },
          },
        } as T,
      };
    },
  };
  const services = createTestComposition(databaseFor([]), {
    transport,
  });
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  const presenter = services.sales.createPresenter();

  presenter.setQuery(lookupCode);
  const addCompletion = presenter.addLookupCode();
  const firstCompletedPhase = await Promise.race([
    addCompletion.then(() => "local-cart" as const),
    remoteLookupStarted.then(() => "remote-lookup" as const),
  ]);
  if (firstCompletedPhase === "remote-lookup") {
    releaseRemoteLookup();
  }
  assert.equal(firstCompletedPhase, "local-cart");
  assert.equal(presenter.getState().query, "");
  assert.equal(presenter.getState().pendingLookupCount, 1);
  await remoteLookupStarted;

  assert.deepEqual(
    requests.map((request) => ({
      url: request.url,
      params: request.params,
    })),
    [
      {
        url: "/api/v1/catalog/sellable-items/lookup",
        params: { storeCode: "S001", lookupCode },
      },
    ],
  );
  const catalogWorkSettled = new Promise<void>((resolve) => {
    let unsubscribe: () => void = () => undefined;
    const finishIfSettled = () => {
      const state = presenter.getState();
      if (
        state.pendingLookupCount === 0 &&
        state.cart.lines.length > 0
      ) {
        unsubscribe();
        resolve();
      }
    };
    unsubscribe = presenter.subscribe(finishIfSettled);
    finishIfSettled();
  });
  releaseRemoteLookup();
  await catalogWorkSettled;
  const checkoutCart = presenter.getState().cart;
  assert.equal(
    checkoutCart.lines[0]?.productCode,
    "P-REMOTE",
  );
  assert.equal(
    checkoutCart.lines[0]?.unitPrice.cents,
    250,
  );
  assert.equal(
    (await services.catalog.findExact(lookupCode))?.productCode,
    "P-REMOTE",
  );
  presenter.destroy();
});

test("收银员登录从当前门店 active 目录装载完整促销快照并重算共享购物车", async () => {
  const loadedStores: string[] = [];
  const services = createTestComposition(
    databaseFor([], {
      activeCatalogPromotions: {
        snapshotId: "catalog-active-1",
        storeCode: "S001",
        promotions: [
          {
            promotionId: "PROMO-2-FOR-1.50",
            definitionJson: JSON.stringify({
              promotionId: "PROMO-2-FOR-1.50",
              name: "2 for 1.50",
              effectiveStart: "2026-07-27T00:00:00.000Z",
              effectiveEnd: "2026-07-29T00:00:00.000Z",
              isExclusive: false,
              priority: 10,
              applyQuantity: 2,
              fixedPrice: 1.5,
              maxApplicationsPerOrder: null,
              products: [{ productCode: "P-1", unitWeight: 1 }],
            }),
          },
        ],
      },
      onActivePromotionsLoad(storeCode) {
        loadedStores.push(storeCode);
      },
    }),
  );

  await services.initialize();
  await services.cashierSession.signIn("cashier");
  const presenter = services.sales.createPresenter();
  presenter.setQuery("930000000001");
  assert.equal(await presenter.addLookupCode(), true);
  presenter.setQuery("930000000001");
  assert.equal(await presenter.addLookupCode(), true);

  assert.deepEqual(loadedStores, ["S001"]);
  assert.equal(presenter.getState().cart.actualAmount.cents, 150);
  assert.equal(presenter.getState().cart.discount.cents, 50);
  presenter.destroy();
});

test("终端启动门禁并发只执行一次，崩溃遗留 HoldClear 必须确认后才开放销售", async () => {
  let confirmations = 0;
  const scope = { storeCode: "S001", deviceCode: "IPAD-1" } as const;
  const fence: TerminalCartFence = {
    scope,
    kind: "HoldClear",
    holdId: "hold-clear-1",
    recallAttemptId: null,
    boundOrderGuid: null,
    createdAtIso: "2026-07-28T00:00:00.000Z",
  };
  const services = createTestComposition(
    databaseFor([], {
      heldOrderRecords: {
        async getTerminalFence() {
          return fence;
        },
        async confirmHoldCartCleared(input) {
          confirmations += 1;
          assert.deepEqual(input, { scope, holdId: fence.holdId });
          return true;
        },
      },
    }),
  );

  assert.throws(
    () => services.sales.createPresenter(),
    /not initialized/i,
  );
  await Promise.all([services.initialize(), services.initialize()]);
  assert.equal(confirmations, 1);
  await services.cashierSession.signIn("cashier");
  const presenter = services.sales.createPresenter();
  assert.equal(presenter.getState().cart.lines.length, 0);
  presenter.destroy();
});

test("分期生产服务使用独立支付账本和第二套 provider 上下文，blocking action 同时阻止安全重启", async () => {
  let boundVoucherContext = false;
  let hasBlockingAction = true;
  const installmentRequests: HbposTransportRequest[] = [];
  const database = databaseFor([]);
  Object.assign(database, {
    installmentSnapshots: () => ({
      async upsertForStore() {},
      async listForStore() {
        return [];
      },
    }),
    installmentActions: () => ({
      async loadBlocking() {
        return hasBlockingAction ? { actionId: "blocking-installment" } : null;
      },
      async loadLifecycleBlocking() {
        return null;
      },
    }),
    installmentPaymentPersistence: () => ({
      providerAttempts: {},
      voucherIntents: {},
      voucherProtectedTokens: {},
      voucherContextForAttempt: async () => {
        throw new Error("not called by this composition test");
      },
      voucherMaterials: {},
      refundProvenance: {},
    }),
  });
  const configuredAvailability = {
    getAvailability(provider: "square" | "linkly-cloud" | "voucher") {
      return {
        provider,
        available: false,
        blocker: "PAYMENT_PROVIDER_UNKNOWN" as const,
      };
    },
    listAvailability() {
      return [];
    },
  };
  const bootstrap = {
    providers: {
      ...configuredAvailability,
      get() {
        throw new Error("provider is outside this composition test");
      },
      listAvailableProviders() {
        return [];
      },
      getVoucherApprovedPurchaseReleasePort() {
        return {
          status: "unavailable" as const,
          reason: "PAYMENT_PROVIDER_UNKNOWN" as const,
        };
      },
    },
    configurationAvailability: configuredAvailability,
    bindVoucherContextProvider() {
      boundVoucherContext = true;
    },
    createLinklyOperator() {
      return null;
    },
  } as PaymentProviderRuntimeBootstrap;
  const services = createTestComposition(database, {
    cashierPermissions: [INSTALLMENTS_VIEW_PERMISSION],
    installmentBootstrap: bootstrap,
    transport: {
      async request<T>(request: HbposTransportRequest) {
        installmentRequests.push(request);
        return {
          status: 200,
          data: { success: true, data: { orders: [] } } as T,
        };
      },
    },
  });

  await services.initialize();
  await services.cashierSession.signIn("cashier");

  assert.equal(boundVoucherContext, true);
  assert.equal("createPresenter" in services.installments, true);
  assert.equal(
    (await services.appUpdateSafety.getSnapshot()).hasUnresolvedPayment,
    true,
  );

  hasBlockingAction = false;
  if (!("createPresenter" in services.installments)) {
    assert.fail("installment runtime should be available");
  }
  const presenter = services.installments.createPresenter();
  await presenter.setDateFilter({
    preset: "today",
    fromDate: null,
    toDate: null,
  });
  assert.deepEqual(installmentRequests.at(-1)?.params, {
    storeCode: "S001",
    createdFrom: "2026-07-27T14:00:00.000Z",
    createdTo: "2026-07-28T13:59:59.999Z",
    skip: 0,
    take: 51,
  });
  presenter.destroy();
});

test("分期生产组合以服务端 scoped Linkly DTO 冻结旧 pending 与 final attempt 的环境", async () => {
  for (const kind of ["pending", "final"] as const) {
    const uid = kind === "pending"
      ? "00112233445566778899aabbccddeeff"
      : "ffeeddccbbaa99887766554433221100";
    const sessionId = `legacy-${kind}-session`;
    const calls = { list: 0, find: 0, reconcile: 0, freeze: 0, financial: 0, acknowledgement: 0 };
    let frozen = false;
    const attempt = { attemptId: `legacy-${kind}`, provider: "linkly-cloud" };
    const provider = {
      provider: "linkly-cloud" as const,
      async submit(): Promise<never> {
        calls.financial += 1;
        return assert.fail("legacy discovery must not submit a financial operation");
      },
      async recover(): Promise<never> {
        calls.financial += 1;
        return assert.fail("legacy discovery must not recover a financial operation");
      },
      async cancel(): Promise<never> {
        calls.financial += 1;
        return assert.fail("legacy discovery must not cancel a financial operation");
      },
      async refund(): Promise<never> {
        calls.financial += 1;
        return assert.fail("legacy discovery must not refund a financial operation");
      },
      async acknowledge() {
        calls.acknowledgement += 1;
        return { acknowledged: true };
      },
      async listUnacknowledgedSessions() {
        calls.list += 1;
        return [{ sessionId, environment: "production", idempotencyKey: uid }];
      },
      async reconcileLegacy() {
        // 回归真实 provider 实例绑定；脱离实例调用会在此失去 this。
        assert.equal(this, provider);
        calls.reconcile += 1;
        return { environment: "production" };
      },
      async recoverWithControl() {
        calls.financial += 1;
        return assert.fail("legacy discovery must not use controlled financial recovery");
      },
    };
    const database = databaseFor([]);
    Object.assign(database, {
      installmentSnapshots: () => ({ async upsertForStore() {}, async listForStore() { return []; } }),
      installmentActions: () => ({
        async loadBlocking() {
          return kind === "pending" && frozen ? { actionId: "pending-recovery" } : null;
        },
        async loadLifecycleBlocking() { return null; },
        async loadProviderAcknowledgementPending() {
          return kind === "final" && frozen ? { actionId: "final-ack" } : null;
        },
      }),
      installmentPaymentPersistence: () => ({
        providerAttempts: {
          async findLegacyLinklyAttemptForSession(scope: unknown, foundSession: string, foundUid: string) {
            calls.find += 1;
            assert.deepEqual(scope, { storeCode: "S001", deviceCode: "IPAD-1", appVersion: "0.1.0-test", instanceId: "test-instance" });
            return foundSession === sessionId && foundUid === uid ? attempt : null;
          },
          async verifyProviderEnvironment(foundAttempt: unknown, environment: string) {
            assert.equal(foundAttempt, attempt);
            assert.equal(environment, "production");
            calls.freeze += 1;
            frozen = true;
            return attempt;
          },
        },
        voucherIntents: {}, voucherProtectedTokens: {},
        voucherContextForAttempt: async () => { throw new Error("not called"); },
        voucherMaterials: {}, refundProvenance: {},
      }),
    });
    const availability = {
      getAvailability(providerName: "square" | "linkly-cloud" | "voucher") {
        return { provider: providerName, available: providerName === "linkly-cloud", blocker: null };
      },
      listAvailability() { return []; },
    };
    const bootstrap = {
      providers: {
        ...availability,
        get(providerName: string) { if (providerName === "linkly-cloud") return provider; throw new Error("unexpected provider"); },
        listAvailableProviders() { return ["linkly-cloud"]; },
        getVoucherApprovedPurchaseReleasePort() { return { status: "unavailable", reason: "PAYMENT_PROVIDER_UNKNOWN" as const }; },
      },
      configurationAvailability: availability,
      bindVoucherContextProvider() {},
      createLinklyOperator() { return null; },
    } as PaymentProviderRuntimeBootstrap;
    const services = createTestComposition(database, { cashierPermissions: [INSTALLMENTS_VIEW_PERMISSION], installmentBootstrap: bootstrap });
    await services.initialize();
    await services.cashierSession.signIn("cashier");
    assert.equal((await services.appUpdateSafety.getSnapshot()).hasUnresolvedPayment, true, kind);
    assert.deepEqual(calls, { list: 1, find: 1, reconcile: 1, freeze: 1, financial: 0, acknowledgement: 0 }, kind);
  }
});

test("分期生产组合注入现金原子 finalizer 并在渲染前上报四阶段指标", async () => {
  const installmentGuid = "10000000-0000-4000-8000-000000000001";
  let persistedAction: PersistedInstallmentAction | null = null;
  let providerPlan: InstallmentProviderAttemptPlan | null = null;
  let snapshotUpsertCalls = 0;
  let atomicFinalizerCalls = 0;
  let preparedOperationGuid = "";
  let preparedPaymentGuid = "";
  let preparedAttemptId = "";
  const performanceEvents: InstallmentPerformanceEvent[] = [];
  const installmentUrls: string[] = [];
  const durableSteps: string[] = [];

  const snapshotRepository = {
    async upsertForStore() {
      snapshotUpsertCalls += 1;
    },
    async listForStore() {
      return [];
    },
  } as unknown as SqliteInstallmentSnapshotRepository;
  const actionStore: InstallmentActionStorePort = {
    async loadBlocking() {
      durableSteps.push("load-blocking");
      return persistedAction;
    },
    async createIfNone(candidate) {
      durableSteps.push("create-action");
      if (persistedAction) {
        return { created: false, action: persistedAction };
      }
      persistedAction = candidate;
      return { created: true, action: candidate };
    },
    async loadLifecycleBlocking() {
      durableSteps.push("load-lifecycle-blocking");
      return null;
    },
    async createLifecycleIfNone() {
      throw new Error("lifecycle action is outside this test");
    },
    async completeLifecycle() {
      throw new Error("lifecycle action is outside this test");
    },
    async finalizeCreatedFailure() {
      persistedAction = null;
    },
    async transition(input) {
      const current = persistedAction;
      if (
        !current ||
        current.action.actionId !== input.actionId ||
        current.state !== input.expectedState
      ) {
        throw new Error("unexpected installment action transition");
      }
      persistedAction = Object.freeze({
        ...current,
        state: input.nextState,
      });
      return persistedAction;
    },
    async decline() {
      persistedAction = null;
    },
    async complete() {
      throw new Error("cash repayment must use the atomic finalizer");
    },
    async completeCommittedRepaymentWithSnapshot(input, repository) {
      assert.strictEqual(repository, snapshotRepository);
      assert.equal(persistedAction?.state, input.expectedState);
      assert.equal(
        persistedAction?.action.installmentGuid,
        input.snapshot.installmentGuid,
      );
      atomicFinalizerCalls += 1;
      persistedAction = null;
    },
  };
  const providerAttempts: InstallmentProviderAttemptStorePort = {
    async loadAction(actionId) {
      return persistedAction?.action.actionId === actionId
        ? persistedAction
        : null;
    },
    async loadPlan(actionId) {
      return providerPlan?.actionId === actionId ? providerPlan : null;
    },
    async bindPlanOrGet(candidate) {
      providerPlan ??= candidate;
      return providerPlan;
    },
    async compareAndUpdateAttempt() {
      throw new Error("cash repayment has no card attempt");
    },
    async loadApprovedMaterial() {
      return null;
    },
    async approveCashSettlements(actionId) {
      if (!providerPlan || providerPlan.actionId !== actionId) {
        throw new Error("cash settlement plan was not prepared");
      }
      providerPlan = Object.freeze({
        ...providerPlan,
        cashSettlements: Object.freeze(
          providerPlan.cashSettlements.map((settlement) =>
            Object.freeze({ ...settlement, state: "Approved" as const }),
          ),
        ),
      });
      return providerPlan.cashSettlements;
    },
  };
  const database = databaseFor([]);
  Object.assign(database, {
    installmentSnapshots: () => snapshotRepository,
    installmentActions: () => actionStore,
    installmentPaymentPersistence: () => ({
      providerAttempts,
      voucherIntents: { async stage() {} },
      voucherProtectedTokens: {},
      voucherContextForAttempt: async () => {
        throw new Error("voucher context is outside this test");
      },
      voucherMaterials: {
        async prepare() {
          throw new Error("voucher material is outside this test");
        },
        async resolveApproved() {
          throw new Error("voucher material is outside this test");
        },
      },
      refundProvenance: {},
    }),
  });
  const configuredAvailability = {
    getAvailability(provider: "square" | "linkly-cloud" | "voucher") {
      return {
        provider,
        available: false,
        blocker: "PAYMENT_PROVIDER_UNKNOWN" as const,
      };
    },
    listAvailability() {
      return [];
    },
  };
  const bootstrap = {
    providers: {
      ...configuredAvailability,
      get() {
        throw new Error("cash repayment has no remote provider");
      },
      listAvailableProviders() {
        return [];
      },
      getVoucherApprovedPurchaseReleasePort() {
        return {
          status: "unavailable" as const,
          reason: "PAYMENT_PROVIDER_UNKNOWN" as const,
        };
      },
    },
    configurationAvailability: configuredAvailability,
    bindVoucherContextProvider() {},
    createLinklyOperator() {
      return null;
    },
  } as PaymentProviderRuntimeBootstrap;
  const initialDetails = installmentDetailsPayload({
    installmentGuid,
    paidAmount: 20,
    balanceAmount: 80,
    payments: [],
  });
  const services = createTestComposition(database, {
    cashierPermissions: [
      INSTALLMENTS_VIEW_PERMISSION,
      INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
      PAYMENT_PERMISSION.view,
      PAYMENT_PERMISSION.confirm,
      PAYMENT_PERMISSION.takeCash,
    ],
    installmentBootstrap: bootstrap,
    installmentPerformanceRecorder: {
      record(event) {
        performanceEvents.push(event);
      },
    },
    createId: uuidSequence(),
    sha256Hex: async (material) =>
      createHash("sha256").update(material, "utf8").digest("hex"),
    transport: {
      async request<T>(request: HbposTransportRequest) {
        installmentUrls.push(request.url);
        if (request.url === "/api/v1/installments/capabilities") {
          return {
            status: 200,
            data: {
              success: true,
              data: {
                repaymentClaimsSupported: true,
                repaymentClaimsRequired: true,
                repaymentClaimPrepareProviderV1: true,
                cardRepaymentSupported: false,
                crossDeviceRepaymentEnabled: false,
                crossDeviceCancelRefundEnabled: false,
                crossDeviceVoidEnabled: false,
                crossDevicePickupEnabled: false,
                preparedClaimTtlSeconds: 300,
                cancelClaimsSupported: true,
                cancelClaimsRequired: true,
                cancelPreparedClaimTtlSeconds: 300,
              },
            } as T,
          };
        }
        if (request.url === `/api/v1/installments/${installmentGuid}`) {
          return {
            status: 200,
            data: { success: true, data: initialDetails } as T,
          };
        }
        if (request.url.endsWith("/prepare-provider")) {
          const payload = request.data as Readonly<Record<string, unknown>>;
          preparedOperationGuid =
            request.url.split("/repayment-claims/")[1]?.split("/")[0] ?? "";
          preparedPaymentGuid = String(payload.paymentGuid ?? "");
          preparedAttemptId = String(payload.providerAttemptId ?? "");
          return {
            status: 200,
            data: {
              success: true,
              data: repaymentClaimPayload({
                installmentGuid,
                operationGuid: preparedOperationGuid,
                paymentGuid: preparedPaymentGuid,
                providerAttemptId: preparedAttemptId,
                status: 2,
                commit: null,
              }),
            } as T,
          };
        }
        if (request.url.endsWith("/commit")) {
          const committedDetails = installmentDetailsPayload({
            installmentGuid,
            paidAmount: 30,
            balanceAmount: 70,
            payments: [installmentPaymentPayload(preparedPaymentGuid)],
          });
          return {
            status: 200,
            data: {
              success: true,
              data: repaymentClaimPayload({
                installmentGuid,
                operationGuid: preparedOperationGuid,
                paymentGuid: preparedPaymentGuid,
                providerAttemptId: preparedAttemptId,
                status: 3,
                commit: {
                  details: committedDetails,
                  alreadyRecorded: false,
                },
              }),
            } as T,
          };
        }
        throw new Error(`Unexpected installment URL: ${request.url}`);
      },
    },
  });

  await services.initialize();
  await services.cashierSession.signIn("cashier");
  if (!("createCheckoutPresenter" in services.installments)) {
    assert.fail("installment runtime should be available");
  }
  const presenter = services.installments.createCheckoutPresenter(
    installmentRepaymentPaymentEntry(installmentGuid),
  );
  assert.equal(await presenter.initialize(), true);
  assert.equal(presenter.selectMethod("cash"), true);
  presenter.setAmountText("10.00");
  assert.equal(
    await presenter.submitSelected(),
    true,
    JSON.stringify({
      state: presenter.getState(),
      installmentUrls,
      durableSteps,
    }),
  );
  assert.equal(await presenter.confirm(), true);

  assert.equal(atomicFinalizerCalls, 1);
  assert.equal(snapshotUpsertCalls, 0);
  assert.deepEqual(
    performanceEvents.map((event) => event.name),
    [
      "prepare",
      "cash-durable",
      "commit",
      "local-finalize",
    ],
  );
  assert.equal(
    performanceEvents.some((event) => event.name === "presenter-success"),
    false,
  );
  assert.equal(
    performanceEvents.every((event) =>
      event.operationHash.startsWith("sha256:")),
    true,
  );
  presenter.destroy();
});

test("RecallActive 启动只建立隐藏门禁，登录后仍需双权限 recover 才恢复并成交", async () => {
  const durableCommits: DurableCashOrderCommit[] = [];
  let recallLoads = 0;
  const pricingCart = new PricingCart();
  pricingCart.addItem({
    lineId: "recalled-line-1",
    productCode: "P-1",
    itemNumber: "I-1",
    lookupCode: "930000000001",
    displayName: "Milk",
    unitPrice: { currency: "AUD", cents: 100 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  const scope = { storeCode: "S001", deviceCode: "IPAD-1" } as const;
  const binding = {
    kind: "recalled" as const,
    scope,
    holdId: "hold-recalled-1",
    recallAttemptId: "recall-attempt-1",
  };
  const fence: TerminalCartFence = {
    scope,
    kind: "RecallActive",
    holdId: binding.holdId,
    recallAttemptId: binding.recallAttemptId,
    boundOrderGuid: null,
    createdAtIso: "2026-07-28T00:00:00.000Z",
  };
  const heldAtIso = "2026-07-27T23:00:00.000Z";
  const services = createTestComposition(
    databaseFor(durableCommits, {
      heldOrderRecords: {
        async getTerminalFence() {
          return fence;
        },
        async loadRecallForFence(input) {
          recallLoads += 1;
          assert.deepEqual(input, binding);
          return {
            hold: {
              holdId: binding.holdId,
              localSequence: 7,
              scope,
              heldBy: { cashierId: "cashier-1", cashierName: "Cashier" },
              status: "Recalling",
              itemCount: 1,
              subtotalCents: 100,
              discountCents: 0,
              actualAmountCents: 100,
              heldAtIso,
              recallingAtIso: "2026-07-28T00:00:00.000Z",
            },
            recallAttemptId: binding.recallAttemptId,
            payload: {
              version: 1,
              pricingState: pricingCart.stateSnapshot(),
            },
          };
        },
      },
    }),
    {
      cashierPermissions: [
        RECALL_LIST_PERMISSION,
        RECALL_RESTORE_PERMISSION,
      ],
      supervisorPermissions: [],
    },
  );
  await services.initialize();
  assert.equal(recallLoads, 0, "启动阶段不得读取或暴露上一位收银员的挂单内容");
  await services.cashierSession.signIn("cashier");

  const presenter = services.sales.createPresenter();
  assert.equal(presenter.getState().cart.lines.length, 0);
  presenter.setQuery("930000000001");
  assert.equal(
    await presenter.addLookupCode(),
    false,
    "隐藏恢复门禁解除前普通编辑必须 fail-closed",
  );

  const heldPresenter = services.heldOrders.createPresenter();
  assert.deepEqual(await heldPresenter.recover(binding.holdId), {
    ok: true,
    code: "recovered",
    holdId: binding.holdId,
  });
  assert.equal(recallLoads, 1);
  assert.equal(presenter.getState().cart.lines[0]?.lineId, "recalled-line-1");
  assert.equal(await presenter.openCash(), true);
  presenter.setExactCash();
  assert.equal(await presenter.submitCash(), true);

  assert.equal(durableCommits.length, 1);
  assert.deepEqual(durableCommits[0]?.terminalContext, binding);
  assert.deepEqual(
    durableCommits[0]?.recalledHoldCompletion?.binding,
    binding,
  );
  assert.equal(presenter.getState().cart.lines.length, 0);
  heldPresenter.destroy();
  presenter.destroy();
});

test("销售挂单使用全局主管授权且只执行一次，会话失效取消待授权动作", async () => {
  let fence: TerminalCartFence | null = null;
  let holdCalls = 0;
  const invalidationCapture: { listener?: () => void } = {};
  const scope = { storeCode: "S001", deviceCode: "IPAD-1" } as const;
  const services = createTestComposition(
    databaseFor([], {
      heldOrderRecords: {
        async getTerminalFence() {
          return fence;
        },
        async hold(command) {
          holdCalls += 1;
          fence = {
            scope,
            kind: "HoldClear",
            holdId: command.holdId,
            recallAttemptId: null,
            boundOrderGuid: null,
            createdAtIso: command.heldAtIso,
          };
          return {
            holdId: command.holdId,
            localSequence: holdCalls,
            scope,
            heldBy: command.heldBy,
            status: "Pending",
            itemCount: command.payload.pricingState.lines.length,
            subtotalCents: 100,
            discountCents: 0,
            actualAmountCents: 100,
            heldAtIso: command.heldAtIso,
            recallingAtIso: null,
          };
        },
        async confirmHoldCartCleared(input) {
          if (
            fence?.kind !== "HoldClear" ||
            fence.holdId !== input.holdId ||
            input.scope.storeCode !== scope.storeCode ||
            input.scope.deviceCode !== scope.deviceCode
          ) {
            return false;
          }
          fence = null;
          return true;
        },
      },
    }),
    {
      supervisorPermissions: [HOLD_ORDER_PERMISSION],
      captureInvalidation(listener) {
        invalidationCapture.listener = listener;
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  const presenter = services.sales.createPresenter();
  presenter.setQuery("930000000001");
  assert.equal(await presenter.addLookupCode(), true);

  const pending = presenter.holdCart();
  await Promise.resolve();
  assert.equal(services.operationAuthorization.status, "available");
  if (services.operationAuthorization.status !== "available") {
    throw new Error("operation authorization must be available");
  }
  const authorizationState = services.operationAuthorization.getState();
  assert.equal(authorizationState.kind, "awaiting-supervisor");
  if (authorizationState.kind !== "awaiting-supervisor") {
    throw new Error("supervisor authorization must be pending");
  }
  assert.ok(authorizationState.actionId);
  assert.equal(authorizationState.permissionCode, HOLD_ORDER_PERMISSION);
  assert.equal(authorizationState.screen, "held-orders");
  assert.equal(authorizationState.action, "hold");
  assert.equal(authorizationState.verifying, false);
  const firstScan =
    services.operationAuthorization.submitSupervisorBarcode("supervisor");
  const duplicateScan =
    services.operationAuthorization.submitSupervisorBarcode("supervisor");
  assert.equal((await duplicateScan).outcome, "duplicate-ignored");
  assert.equal((await firstScan).outcome, "authorized");
  assert.equal(await pending, true);
  assert.equal(holdCalls, 1);
  assert.equal(presenter.getState().cart.lines.length, 0);

  presenter.setQuery("930000000001");
  assert.equal(await presenter.addLookupCode(), true);
  const cancelled = presenter.holdCart();
  await Promise.resolve();
  const invalidateSession = invalidationCapture.listener;
  if (typeof invalidateSession !== "function") {
    throw new Error("session invalidation listener was not captured");
  }
  invalidateSession();
  assert.equal(await cancelled, false);
  assert.equal(holdCalls, 1);
  assert.equal(presenter.getState().cart.lines.length, 1);
  presenter.destroy();
});

test("同步历史 presenter 只使用可信收银员 lease，会话失效后拒绝读取、导出和补传", async () => {
  let restoreCalls = 0;
  const invalidationCapture: { listener?: () => void } = {};
  const order = pendingSyncHistoryOrder();
  const services = createTestComposition(
    databaseFor([], {
      syncHistoryOrders: [order],
      onSyncHistoryRestore() {
        restoreCalls += 1;
      },
    }),
    {
      cashierPermissions: [
        SYNC_HISTORY_EXPORT_PERMISSION,
        SYNC_HISTORY_MANUAL_SYNC_PERMISSION,
        SYNC_HISTORY_VIEW_PERMISSION,
      ],
      captureInvalidation(listener) {
        invalidationCapture.listener = listener;
      },
    },
  );
  await services.initialize();
  assert.throws(
    () => services.syncHistory.createPresenter([]),
    /CURRENT_CASHIER_REQUIRED/,
  );

  const cashier = await services.cashierSession.signIn("cashier");
  const presenter = services.syncHistory.createPresenter(
    cashier.permissions,
  );
  await presenter.refresh();
  assert.equal(presenter.getState().rows.length, 1);
  presenter.setSelected(order.orderGuid, true);

  const invalidate = invalidationCapture.listener;
  if (!invalidate) throw new Error("session invalidation listener was not captured");
  invalidate();

  assert.deepEqual(await presenter.requestRetransmitSelected(), {
    kind: "failed",
    requestedCount: 0,
    skippedCount: 0,
    reauthenticationRequiredCount: 0,
    supervisorRequiredCount: 0,
    errorCode: "retransmit-failed",
  });
  assert.equal(restoreCalls, 0, "失效后的旧 presenter 不得恢复 outbox");
  await assert.rejects(
    () => presenter.createSupportExport(),
    /CURRENT_CASHIER_REQUIRED/,
  );
  await presenter.refresh();
  assert.equal(presenter.getState().kind, "failed");
  presenter.destroy();
});

test("同店跨终端非现金远程历史重打会重新读取后端详情并进入耐久打印状态机", async () => {
  const orderGuid = "10000000-0000-4000-8000-000000000001";
  const soldAtIso = "2026-07-28T01:02:03.000Z";
  const preparedOrderGuids: string[] = [];
  const printedJobIds: string[] = [];
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      if (request.url === "/api/v1/orders/history") {
        return {
          status: 200,
          data: {
            success: true,
            data: {
              orders: [
                {
                  orderGuid,
                  storeCode: "S001",
                  deviceCode: "IPAD-2",
                  cashierName: "Cashier",
                  soldAt: soldAtIso,
                  totalAmount: 1,
                  discountAmount: 0,
                  actualAmount: 1,
                  lineCount: 1,
                  paymentSummary: "Card",
                  statusLabel: "Synced",
                },
              ],
            },
          } as T,
        };
      }
      return {
        status: 200,
        data: {
          success: true,
          data: {
            orderGuid,
            storeCode: "S001",
            deviceCode: "IPAD-2",
            cashierName: "Cashier",
            soldAt: soldAtIso,
            totalAmount: 1,
            discountAmount: 0,
            actualAmount: 1,
            lines: [
              {
                orderLineGuid:
                  "20000000-0000-4000-8000-000000000001",
                productCode: "P-1",
                displayName: "Milk",
                quantity: 1,
                unitPrice: 1,
                discountAmount: 0,
                actualAmount: 1,
                kind: 1,
              },
            ],
            payments: [
              {
                paymentGuid:
                  "30000000-0000-4000-8000-000000000001",
                method: 2,
                amount: 1,
                reference: null,
                cardTransactions: [],
              },
            ],
          },
        } as T,
      };
    },
  };
  const services = createTestComposition(
    databaseFor([], {
      onReprintPrepared(input) {
        preparedOrderGuids.push(input.orderGuid);
      },
    }),
    {
      cashierPermissions: [
        REMOTE_HISTORY_REPRINT_PERMISSION,
        REMOTE_HISTORY_VIEW_PERMISSION,
      ],
      onPrint(jobId) {
        printedJobIds.push(jobId);
      },
      transport,
    },
  );

  await services.initialize();
  await services.cashierSession.signIn("cashier");
  const presenter = services.remoteHistory.createPresenter({
    online: true,
  });
  await presenter.refresh();
  assert.equal(presenter.capabilities.reprint, true);

  await presenter.reprintSelected();

  assert.deepEqual(preparedOrderGuids, [orderGuid]);
  assert.deepEqual(printedJobIds, ["reprint-job-1"]);
  assert.deepEqual(presenter.getState().reprint, {
    kind: "succeeded",
    orderGuid,
  });
  presenter.destroy();
});

test("本机历史组合只按可信终端读取，并从选中订单账本进入本机来源重打", async () => {
  const order = lastCashOrder();
  const scopes: {
    storeCode: string;
    deviceCode: string;
  }[] = [];
  const queries: LocalHistoryQuery[] = [];
  const preparedOrderGuids: string[] = [];
  const printedJobIds: string[] = [];
  const page: LocalHistoryPage = {
    orders: [
      {
        orderGuid: order.orderGuid,
        localSequence: order.localSequence,
        soldAtIso: order.soldAtIso,
        cashierName: order.cashierName,
        state: "PendingSync",
        totalCents: order.total.cents,
        discountCents: order.discount.cents,
        actualAmountCents: order.actualAmount.cents,
        lineCount: order.lines.length,
        paymentSummary: "Cash",
      },
    ],
    nextCursor: null,
  };
  const details: LocalHistoryDetails = {
    ...page.orders[0]!,
    lines: order.lines.map((line) => ({
      lineId: line.lineId,
      productCode: line.productCode,
      itemNumber: line.itemNumber,
      lookupCode: line.lookupCode,
      displayName: line.displayName,
      quantity: line.quantity,
      unitPriceCents: line.unitPrice.cents,
      discountCents: line.discount.cents,
      actualAmountCents: line.actualAmount.cents,
      kind: line.kind,
    })),
    tenders: order.tenders.map((tender) => ({
      method: tender.method,
      amountCents: tender.amount.cents,
    })),
  };
  const database = databaseFor([], {
    lastOrder: order,
    onReprintPrepared(input) {
      preparedOrderGuids.push(input.orderGuid);
    },
  });
  Object.assign(database as object, {
    localHistory(scope: { storeCode: string; deviceCode: string }) {
      scopes.push(scope);
      return {
        async list(query: LocalHistoryQuery) {
          queries.push(query);
          return page;
        },
        async getDetails(orderGuid: string) {
          return orderGuid === order.orderGuid ? details : null;
        },
      };
    },
  });
  const services = createTestComposition(database, {
    cashierPermissions: [
      LOCAL_HISTORY_VIEW_PERMISSION,
      LOCAL_HISTORY_REPRINT_PERMISSION,
    ],
    settings: settingsRuntimeConfiguration(),
    onPrint(jobId) {
      printedJobIds.push(jobId);
    },
  });

  await services.initialize();
  await services.cashierSession.signIn("cashier");
  const receiptSettings = await services.receiptSettings.get();
  await services.receiptSettings.save({
    ...receiptSettings,
    printEnabled: false,
    peripheralId: null,
  });
  const presenter = services.localHistory.createPresenter();
  await presenter.refresh();
  await presenter.selectOrder(order.orderGuid);

  const preview = presenter.getState().receiptPreview;
  assert.equal(preview.kind, "ready");
  if (preview.kind !== "ready") {
    throw new Error("local history receipt preview was not ready");
  }
  assert.ok(preview.document.lines.some(
    (line) => line.kind === "text" && line.text === order.orderGuid,
  ));
  assert.equal(preview.document.lines.some(
    (line) => line.kind === "text" && /Order:|#\d+/.test(line.text),
  ), false);
  assert.ok(preview.document.lines.some(
    (line) => line.kind === "text" && line.text === "Brisbane",
  ));
  assert.ok(preview.document.lines.some(
    (line) => line.kind === "text" && line.text === "Store: Brisbane (S001)",
  ));
  assert.ok(preview.document.lines.some(
    (line) => line.kind === "qr" && line.value === order.orderGuid,
  ));

  // 预览不需要外设；真实重打仍须重新配置并冻结有效 printerId。
  await services.receiptSettings.save({
    ...receiptSettings,
    printEnabled: false,
    peripheralId: "printer-1",
  });
  await presenter.reprintSelected();

  assert.deepEqual(scopes, [
    { storeCode: "S001", deviceCode: "IPAD-1" },
  ]);
  assert.equal(queries.length, 1);
  assert.equal(queries[0]?.limit, 50);
  assert.deepEqual(preparedOrderGuids, [order.orderGuid]);
  assert.deepEqual(printedJobIds, ["reprint-job-1"]);
  assert.deepEqual(presenter.getState().reprint, {
    kind: "succeeded",
    orderGuid: order.orderGuid,
  });
  presenter.destroy();
});

test("本机历史异步读取期间收银会话失效时，不发布旧 scope 数据", async () => {
  const pendingPage = deferred<LocalHistoryPage>();
  const invalidationCapture: { listener?: () => void } = {};
  const database = databaseFor([]);
  Object.assign(database as object, {
    localHistory() {
      return {
        list() {
          return pendingPage.promise;
        },
        async getDetails() {
          throw new Error("details must not be read");
        },
      };
    },
  });
  const services = createTestComposition(database, {
    cashierPermissions: [LOCAL_HISTORY_VIEW_PERMISSION],
    captureInvalidation(listener) {
      invalidationCapture.listener = listener;
    },
  });

  await services.initialize();
  await services.cashierSession.signIn("cashier");
  const presenter = services.localHistory.createPresenter();
  const refresh = presenter.refresh();
  await Promise.resolve();
  const invalidate = invalidationCapture.listener;
  if (!invalidate) {
    throw new Error("session invalidation listener was not captured");
  }
  invalidate();
  pendingPage.resolve({ orders: [], nextCursor: null });
  await refresh;

  assert.equal(presenter.getState().kind, "failed");
  assert.deepEqual(presenter.getState().rows, []);
  presenter.destroy();
});

test("现金提交耐久化成功后立即触发履约，履约失败不改变成功结果", async () => {
  const expected: CashCheckoutResult = {
    completed: true,
    canClearCart: true,
    orderGuid: "cash-order-1",
    cashDueCents: 100,
    changeCents: 0,
    postCommit: {
      requestDrawer: false,
      drawerDisposition: "not-required",
      printPolicy: "never",
    },
  };
  const trace: string[] = [];
  const postCommitWork = createPostCommitWorkDrain(
    async () => {
      trace.push("drain-started");
      throw new Error("printer is temporarily unavailable");
    },
    async () => {
      trace.push("sync-wake-started");
      throw new Error("network is temporarily unavailable");
    },
  );
  const checkout = createPostCommitFulfilmentCashCheckout(
    {
      async complete() {
        trace.push("committed");
        return expected;
      },
    },
    postCommitWork,
  );

  const result = await checkout.complete({} as never);

  assert.equal(result, expected);
  assert.deepEqual(trace, [
    "committed",
    "sync-wake-started",
    "drain-started",
  ]);
});

test("生产退货服务按可信收银员作用域启用，缺少主管授权时明确不可用", async () => {
  const available = createTestComposition(databaseFor([]), {
    cashierPermissions: ["Permissions.PosTerminal.Returns.View"],
    supervisorPermissions: [],
  });
  await available.initialize();
  await available.cashierSession.signIn("cashier");
  assert.equal(available.returns.status, "available");
  if (available.returns.status !== "available") {
    throw new Error("returns runtime should be available");
  }
  assert.equal(await available.returns.hasRecoveryRequired(), false);
  const presenter = await available.returns.createPresenter();
  assert.equal(presenter.getState().mode, "receipt");

  const unavailable = createTestComposition(databaseFor([]));
  await unavailable.initialize();
  await unavailable.cashierSession.signIn("cashier");
  assert.deepEqual(unavailable.returns, {
    status: "unavailable",
    reason: "SUPERVISOR_AUTHENTICATION_MISSING",
  });
});

test("生产更新安全快照把可信门店作用域内的退货恢复标记为阻断", async () => {
  const services = createTestComposition(
    databaseFor([], { returnRecoveryRequired: true }),
    {
      cashierPermissions: ["Permissions.PosTerminal.Returns.View"],
      supervisorPermissions: [],
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  const snapshot = await services.appUpdateSafety.getSnapshot();
  assert.equal(snapshot.hasRecoveryRequired, true);
});

test("支付配置切换仍阻断需要恢复的退货，不把普通待同步退货误作恢复", async () => {
  let saveCalls = 0;
  let reloadCalls = 0;
  const baseSettings = settingsRuntimeConfiguration();
  const services = createTestComposition(
    databaseFor([], { returnRecoveryRequired: true }),
    {
      appUpdateTransition: new UpdateTransitionLeaseCoordinator(),
      cashierPermissions: [
        "Permissions.PosTerminal.Returns.View",
        SETTINGS_VIEW_PERMISSION,
        SETTINGS_PAYMENT_TERMINAL_PERMISSION,
      ],
      supervisorPermissions: [],
      settings: {
        ...baseSettings,
        paymentConfiguration: {
          current: {
            provider: "linkly",
            square: null,
            linkly: { environment: "Production" },
          },
          availability: {
            square: { available: false, blockerCode: "not-configured" },
            linkly: { available: true, blockerCode: null },
          },
          test: async () => undefined,
          save: async () => {
            saveCalls += 1;
          },
        },
        runtimeReload: {
          reload: async () => {
            reloadCalls += 1;
          },
        },
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();

  presenter.setLinklyEnvironment("Sandbox");
  await presenter.savePaymentSettings();
  await presenter.confirmDangerousAction();

  assert.equal(presenter.getState().statusCode, "pending-local-data");
  assert.equal(saveCalls, 0);
  assert.equal(reloadCalls, 0);
  presenter.destroy();
});

test("换店门禁独立读取未启用功能的耐久恢复状态并在读取失败时 fail closed", async (context) => {
  const cases = [
    {
      name: "支付 bootstrap 缺失但支付草稿待恢复",
      database: { paymentDraftRecoveryRequired: true },
      unavailableService: "payments",
      expectedStatus: "pending-local-data",
    },
    {
      name: "支付 bootstrap 缺失但礼券 tender reversal 待恢复",
      database: { voucherTenderReversalRecoveryRequired: true },
      unavailableService: "payments",
      expectedStatus: "pending-local-data",
    },
    {
      name: "主管授权缺失但退货执行账本待恢复",
      database: { returnRecoveryRequired: true },
      unavailableService: "returns",
      expectedStatus: "pending-local-data",
    },
    {
      name: "分期配置缺失但分期 action 待恢复",
      database: { installmentRecoveryRequired: true },
      unavailableService: "installments",
      expectedStatus: "pending-local-data",
    },
    {
      name: "恢复探针读取失败",
      database: { recoveryProbeFailure: "payment" },
      unavailableService: "payments",
      expectedStatus: "safety-check-failed",
    },
    {
      name: "退货执行账本存在多条非法恢复记录",
      database: { returnRecoveryCount: 2 },
      unavailableService: "returns",
      expectedStatus: "safety-check-failed",
    },
  ] as const;

  for (const scenario of cases) {
    await context.test(scenario.name, async (scenarioContext) => {
      let reregisterCalls = 0;
      let reloadCalls = 0;
      const baseSettings = settingsRuntimeConfiguration();
      const services = createTestComposition(
        databaseFor([], scenario.database),
        {
          appUpdateTransition: new UpdateTransitionLeaseCoordinator(),
          cashierPermissions: [
            SETTINGS_VIEW_PERMISSION,
            SETTINGS_DEVICE_REGISTRATION_PERMISSION,
          ],
          settings: {
            ...baseSettings,
            device: {
              ...baseSettings.device,
              previewActivationCode: async () => ({
                isAllowed: true,
                storeCode: "S002",
                storeName: "Target store",
                deviceSystem: "Android",
                expiresAtUtc: "2026-08-28T00:00:00.000Z",
              }),
              reregister: async () => {
                reregisterCalls += 1;
              },
            },
            runtimeReload: {
              reload: async () => {
                reloadCalls += 1;
              },
            },
          },
        },
      );
      scenarioContext.after(() => services.shutdownBackgroundWork());
      const unavailableService: unknown =
        services[scenario.unavailableService];
      assert.equal(
        typeof unavailableService === "object" &&
          unavailableService !== null &&
          "status" in unavailableService
          ? unavailableService.status
          : null,
        "unavailable",
      );
      await services.initialize();
      await services.cashierSession.signIn("cashier");
      assert.equal("createPresenter" in services.settings, true);
      if (!("createPresenter" in services.settings)) return;
      const presenter = services.settings.createPresenter();
      await presenter.load();
      presenter.setDeviceActivationCode(
        "HBDEV1-0123456789ABCDEFGHJKMNPQRS-STVWXYZ0123456789ABCDEFGHJ",
      );
      await presenter.previewDeviceReregistration();
      await presenter.requestDeviceReregistration();

      assert.equal(presenter.getState().statusCode, scenario.expectedStatus);
      assert.equal(reregisterCalls, 0);
      assert.equal(reloadCalls, 0);
      presenter.destroy();
    });
  }
});

test("生产组合绑定全局 transition：自身 cart lease 不误报 durable write，封门期间拒绝新同步", async () => {
  const transition = new UpdateTransitionLeaseCoordinator();
  const services = createTestComposition(databaseFor([]), {
    appUpdateTransition: transition,
  });
  await services.initialize();

  const release = deferred<void>();
  const action = transition.runTransition(async () => {
    const snapshot = await services.appUpdateSafety.getSnapshot();
    assert.equal(snapshot.hasPendingDurableWrite, false);
    await assert.rejects(
      services.sync.requestDrain(),
      (error: unknown) =>
        error instanceof Error &&
        (error as Error & { code?: string }).code ===
          UPDATE_TRANSITION_IN_PROGRESS,
    );
    await release.promise;
  });
  await Promise.resolve();
  assert.equal(transition.isTransitionActive(), true);
  release.resolve();
  await action;
  assert.equal(transition.isTransitionActive(), false);
  assert.equal(
    await transition.runOperation(async () => "released"),
    "released",
  );
});

test("生产组合只暴露订单同步计数窄读与 drain-settled 订阅", async () => {
  const services = createTestComposition(
    databaseFor([], { pendingOrderSyncCount: 7 }),
  );

  assert.equal(await services.sync.readPendingOrderSyncCount(), 7);
  const unsubscribe = services.sync.subscribeDrainSettled(() => undefined);
  assert.equal(typeof unsubscribe, "function");
  unsubscribe();
  unsubscribe();
});

test("支付配置保存等待既有 operation，封门拒绝新 operation，释放后才保存并重载", async () => {
  const transition = new UpdateTransitionLeaseCoordinator();
  const existingRelease = deferred<void>();
  const existing = transition.runOperation(() => existingRelease.promise);
  let saveCalls = 0;
  let reloadCalls = 0;
  const baseSettings = settingsRuntimeConfiguration();
  const services = createTestComposition(databaseFor([]), {
    appUpdateTransition: transition,
    cashierPermissions: [
      SETTINGS_VIEW_PERMISSION,
      SETTINGS_PAYMENT_TERMINAL_PERMISSION,
    ],
    settings: {
      ...baseSettings,
      paymentConfiguration: {
        current: {
          provider: "linkly",
          square: null,
          linkly: { environment: "Production" },
        },
        availability: {
          square: { available: false, blockerCode: "not-configured" },
          linkly: { available: true, blockerCode: null },
        },
        test: async () => undefined,
        save: async () => {
          saveCalls += 1;
        },
      },
      runtimeReload: {
        reload: async () => {
          reloadCalls += 1;
        },
      },
    },
  });
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();
  presenter.setLinklyEnvironment("Sandbox");
  await presenter.savePaymentSettings();

  const confirmation = presenter.confirmDangerousAction();
  await Promise.resolve();
  assert.equal(transition.isTransitionActive(), true);
  assert.equal(saveCalls, 0);
  assert.equal(reloadCalls, 0);
  await assert.rejects(
    transition.runOperation(async () => undefined),
    (error: unknown) =>
      error instanceof Error &&
      (error as Error & { code?: string }).code ===
        UPDATE_TRANSITION_IN_PROGRESS,
  );

  existingRelease.resolve();
  await existing;
  await confirmation;

  assert.equal(saveCalls, 1);
  assert.equal(reloadCalls, 1);
  assert.equal(transition.isTransitionActive(), false);
  presenter.destroy();
});

test("目录刷新登记覆盖完整生命周期，transition 等完成后才在双独占锁内重读安全快照", async () => {
  const transition = new UpdateTransitionLeaseCoordinator();
  const catalogEntered = deferred<void>();
  const catalogRelease = deferred<void>();
  const transitionEntered = deferred<void>();
  const transitionRelease = deferred<void>();
  const services = createTestComposition(databaseFor([]), {
    appUpdateTransition: transition,
    cashierPermissions: [
      SETTINGS_VIEW_PERMISSION,
      SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
    ],
    settings: settingsRuntimeConfiguration(),
    transport: await catalogDownloadTransport({
      beforePromotions: async () => {
        catalogEntered.resolve();
        await catalogRelease.promise;
      },
    }),
  });
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();

  const download = presenter.downloadCatalog();
  await catalogEntered.promise;
  assert.equal(
    (await services.appUpdateSafety.getSnapshot())
      .hasCatalogRefreshInFlight,
    true,
  );

  const update = transition.runTransition(async () => {
    const snapshot = await services.appUpdateSafety.getSnapshot();
    assert.equal(snapshot.hasCatalogRefreshInFlight, false);
    assert.equal(snapshot.hasPendingDurableWrite, false);
    transitionEntered.resolve();
    await transitionRelease.promise;
  });
  await Promise.resolve();
  assert.equal(transition.isTransitionActive(), true);
  let entered = false;
  void transitionEntered.promise.then(() => {
    entered = true;
  });
  await Promise.resolve();
  assert.equal(entered, false);

  catalogRelease.resolve();
  await download;
  await transitionEntered.promise;
  assert.equal(entered, true);

  transitionRelease.resolve();
  await update;
  assert.equal(transition.isTransitionActive(), false);
  presenter.destroy();
});

test("四个目录顶层入口各登记一次，Settings 重启直接进入 transition 不发生嵌套自锁", async () => {
  const transition = new RecordingUpdateTransitionLeaseCoordinator();
  const settingsConfiguration = settingsRuntimeConfiguration();
  const services = createTestComposition(databaseFor([]), {
    appUpdateTransition: transition,
    cashierPermissions: [
      CATALOG_DOWNLOAD_PERMISSION,
      SETTINGS_VIEW_PERMISSION,
      SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
      SETTINGS_CATALOG_RESET_PERMISSION,
      SETTINGS_DEVICE_REGISTRATION_PERMISSION,
      SETTINGS_APP_UPDATE_PERMISSION,
    ],
    settings: {
      ...settingsConfiguration,
      appUpdate: {
        ...settingsConfiguration.appUpdate,
        restart: async () => {
          await transition.runTransition(async () => undefined);
          return true;
        },
      },
    },
    transport: await catalogDownloadTransport(),
  });
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();
  const baseline = transition.operationCalls;

  await services.catalog.downloadAndActivate({ storeCode: "S001" });
  assert.equal(transition.operationCalls, baseline + 1);

  await presenter.downloadCatalog();
  assert.equal(transition.operationCalls, baseline + 2);

  assert.equal(presenter.requestCatalogReset(), true);
  await presenter.confirmDangerousAction();
  assert.equal(transition.operationCalls, baseline + 3);

  presenter.setApiAddressDraft("https://next.example.test/pos");
  assert.equal(presenter.requestApiAddressChange(), true);
  await presenter.confirmDangerousAction();
  assert.equal(transition.operationCalls, baseline + 4);

  assert.equal(presenter.requestAppRestart(), true);
  await presenter.confirmDangerousAction();
  assert.equal(transition.operationCalls, baseline + 4);
  assert.equal(transition.isTransitionActive(), false);
  presenter.destroy();
});

test("Settings 钱箱测试复用受权限、lease 与审计保护的正式开箱动作", async () => {
  const savedPeripheralIds: (string | null)[] = [];
  let drawerOpens = 0;
  const services = createTestComposition(
    databaseFor([], {
      onReceiptSettingsSave(settings) {
        savedPeripheralIds.push(settings.peripheralId);
      },
    }),
    {
      cashierPermissions: [
        SETTINGS_VIEW_PERMISSION,
        SETTINGS_RECEIPT_PRINTER_PERMISSION,
        "Permissions.PosTerminal.CashDrawer.Open",
      ],
      settings: settingsRuntimeConfiguration(),
      onDrawerOpen() {
        drawerOpens += 1;
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();
  presenter.setDrawerEnabled(true);

  await presenter.testCashDrawer();

  assert.deepEqual(savedPeripheralIds, ["printer-1"]);
  assert.equal(drawerOpens, 1);
  assert.equal(presenter.getState().statusCode, "cash-drawer-test-passed");
  presenter.destroy();
});

test("Settings 从可信当前门店端点载入小票资料，确认保存前不写本机", async () => {
  const requests: HbposTransportRequest[] = [];
  const saved: ReceiptPrinterSettings[] = [];
  const services = createTestComposition(
    databaseFor([], {
      onReceiptSettingsSave(settings) {
        saved.push(settings);
      },
    }),
    {
      cashierPermissions: [
        SETTINGS_VIEW_PERMISSION,
        SETTINGS_RECEIPT_PRINTER_PERMISSION,
      ],
      settings: settingsRuntimeConfiguration(),
      transport: {
        async request<T>(request: HbposTransportRequest) {
          requests.push(request);
          if (request.url !== "/api/v1/stores/current/receipt-profile") {
            throw new Error(`Unexpected URL: ${request.url}`);
          }
          return {
            status: 200,
            data: {
              success: true,
              data: {
                storeCode: "S001",
                storeName: "Brisbane Central",
                brandName: "Hot Bargain",
                address: "1 Queen St\nBrisbane QLD 4000",
                phone: "07 3000 0000",
                abn: "12 345 678 901",
                returnPolicy: "Refunds within 14 days.",
              },
            } as T,
          };
        },
      },
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();

  await presenter.loadReceiptProfile();

  assert.deepEqual(
    requests.map(({ method, url }) => ({ method, url })),
    [{ method: "GET", url: "/api/v1/stores/current/receipt-profile" }],
  );
  assert.equal(saved.length, 0);
  assert.deepEqual(
    {
      brandName: presenter.getState().printer.brandName,
      storeName: presenter.getState().printer.storeName,
      address: presenter.getState().printer.address,
      phone: presenter.getState().printer.phone,
      abn: presenter.getState().printer.abn,
      returnPolicy: presenter.getState().printer.returnPolicy,
      profileStoreCode: presenter.getState().printer.profileStoreCode,
    },
    {
      brandName: "Hot Bargain",
      storeName: "Brisbane Central",
      address: "1 Queen St\nBrisbane QLD 4000",
      phone: "07 3000 0000",
      abn: "12 345 678 901",
      returnPolicy: "Refunds within 14 days.",
      profileStoreCode: "S001",
    },
  );
  assert.equal(presenter.getState().statusCode, "receipt-profile-loaded");

  await presenter.savePrinterSettings();
  assert.equal(saved.length, 1);
  assert.equal(saved[0]?.profileStoreCode, "S001");
  presenter.destroy();
});

test("Settings 钱箱测试缺少 CashDrawer.Open 权限时失败且不触发硬件", async () => {
  let drawerOpens = 0;
  const services = createTestComposition(databaseFor([]), {
    cashierPermissions: [
      SETTINGS_VIEW_PERMISSION,
      SETTINGS_RECEIPT_PRINTER_PERMISSION,
    ],
    settings: settingsRuntimeConfiguration(),
    onDrawerOpen() {
      drawerOpens += 1;
    },
  });
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();

  await presenter.testCashDrawer();

  assert.equal(drawerOpens, 0);
  assert.equal(presenter.getState().statusCode, "cash-drawer-test-failed");
  presenter.destroy();
});

test("生产退货组合必须取得二次加密退款券材料端口，不能保留不可用占位渲染器", async () => {
  let protectedMaterialFacadeCalls = 0;
  const services = createTestComposition(
    databaseFor([], {
      onRefundVoucherPrintMaterialCreated() {
        protectedMaterialFacadeCalls += 1;
      },
    }),
    {
      cashierPermissions: ["Permissions.PosTerminal.Returns.View"],
      supervisorPermissions: [],
    },
  );

  await services.initialize();
  assert.equal(services.returns.status, "available");
  assert.equal(protectedMaterialFacadeCalls, 1);
});

test("特殊商品只从可信收银员作用域加载，并将服务端价格来源原值带入共享购物车", async () => {
  const item: SpecialProductItem = {
    storeCode: "S001",
    productCode: "SPECIAL-1",
    referenceCode: "REF-SPECIAL-1",
    itemNumber: "ITEM-SPECIAL-1",
    displayName: "Special item",
    barcode: "930000000099",
    lookupCode: "930000000099",
    retailPriceCents: 275,
    priceSource: 3,
    quantityFactor: 1,
    productImage: null,
    discountRate: null,
    sortOrder: 0,
  };
  const services = createTestComposition(
    databaseFor([], { specialProductItems: [item] }),
    {
      cashierPermissions: [
        "Permissions.PosTerminal.SpecialProducts.View",
        "Permissions.PosTerminal.SpecialProducts.AddToCart",
      ],
    },
  );
  await services.initialize();
  assert.throws(
    () => services.specialProducts.createPresenter(),
    /CURRENT_CASHIER_REQUIRED/,
  );
  await services.cashierSession.signIn("cashier");

  const specialProducts = services.specialProducts.createPresenter();
  await specialProducts.load();
  assert.equal(specialProducts.getState().items.length, 1);
  await specialProducts.addToCart("SPECIAL-1");

  const cart = services.sales.createPresenter().getState().cart;
  assert.deepEqual(cart.lines[0]?.syncProvenance, {
    referenceCode: "REF-SPECIAL-1",
    priceSource: 3,
  });
  assert.equal(cart.lines[0]?.unitPrice.cents, 275);
  specialProducts.destroy();
});

test("生产更新门禁阻止空车开始普通或特殊商品交易，但不截断已有购物车", async () => {
  let canStartNewTransaction = false;
  const specialItem: SpecialProductItem = {
    storeCode: "S001",
    productCode: "SPECIAL-GATE",
    referenceCode: null,
    itemNumber: "ITEM-GATE",
    displayName: "Gated item",
    barcode: "930000000088",
    lookupCode: "930000000088",
    retailPriceCents: 300,
    priceSource: 0,
    quantityFactor: 1,
    productImage: null,
    discountRate: null,
    sortOrder: 0,
  };
  const services = createTestComposition(
    databaseFor([], { specialProductItems: [specialItem] }),
    {
      canStartNewTransaction: () => canStartNewTransaction,
      cashierPermissions: [
        "Permissions.PosTerminal.SpecialProducts.View",
        "Permissions.PosTerminal.SpecialProducts.AddToCart",
      ],
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  const sales = services.sales.createPresenter();
  assert.deepEqual(await services.appUpdateSafety.getSnapshot(), {
    hasActiveCart: false,
    hasUnresolvedPayment: false,
    hasPendingDurableWrite: false,
    hasRecoveryRequired: false,
    hasCatalogRefreshInFlight: false,
    hasSyncOrAuditInFlight: false,
    hasFulfilmentInFlight: false,
  });
  sales.setQuery("930000000001");
  assert.equal(await sales.addLookupCode(), false);
  assert.equal(sales.getState().errorCode, "new-transactions-disabled");

  const specials = services.specialProducts.createPresenter();
  await specials.load();
  await specials.addToCart("SPECIAL-GATE");
  assert.equal(specials.getState().statusCode, "add-to-cart-failed");
  assert.equal(sales.getState().cart.lines.length, 0);

  canStartNewTransaction = true;
  await specials.addToCart("SPECIAL-GATE");
  assert.equal(specials.getState().statusCode, "added-to-cart");
  assert.equal(sales.getState().cart.lines.length, 1);
  assert.deepEqual(await services.appUpdateSafety.getSnapshot(), {
    hasActiveCart: true,
    hasUnresolvedPayment: false,
    hasPendingDurableWrite: false,
    hasRecoveryRequired: false,
    hasCatalogRefreshInFlight: false,
    hasSyncOrAuditInFlight: false,
    hasFulfilmentInFlight: false,
  });

  canStartNewTransaction = false;
  await specials.addToCart("SPECIAL-GATE");
  assert.equal(specials.getState().statusCode, "added-to-cart");
  assert.equal(sales.getState().cart.lines[0]?.quantity, "2");

  specials.destroy();
  sales.destroy();
});

test("本地目录摘要只向具备下载权限的可信同店收银员公开", async () => {
  const summary: ActiveCatalogMetadata = {
    snapshotId: "catalog-active-1",
    catalogVersion: "catalog-v3",
    itemCount: 42,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const services = createTestComposition(
    databaseFor([], { activeCatalogMetadata: summary }),
    { cashierPermissions: [CATALOG_DOWNLOAD_PERMISSION] },
  );
  await services.initialize();

  await assert.rejects(
    () => services.catalog.getCurrentCatalog({ storeCode: "S001" }),
    /CURRENT_CASHIER_REQUIRED/,
  );

  await services.cashierSession.signIn("cashier");
  assert.deepEqual(
    await services.catalog.getCurrentCatalog({ storeCode: "S001" }),
    summary,
  );
  await assert.rejects(
    () => services.catalog.getCurrentCatalog({ storeCode: "OTHER" }),
    /store/i,
  );

  const withoutPermission = createTestComposition(
    databaseFor([], { activeCatalogMetadata: summary }),
  );
  await withoutPermission.initialize();
  await withoutPermission.cashierSession.signIn("cashier");
  await assert.rejects(
    () => withoutPermission.catalog.getCurrentCatalog({ storeCode: "S001" }),
    /permission/i,
  );
});

test("下载期间会话失效会在激活前拒绝，既不覆盖 active 也不报告成功", async () => {
  let invalidate: (() => void) | undefined;
  let activations = 0;
  const services = createTestComposition(
    databaseFor([], {
      onCatalogActivate() { activations += 1; },
    }),
    {
      cashierPermissions: [CATALOG_DOWNLOAD_PERMISSION],
      captureInvalidation(listener) { invalidate = listener; },
      transport: await catalogDownloadTransport(),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  await assert.rejects(
    () => services.catalog.downloadAndActivate({
      storeCode: "S001",
      onProgress(event) {
        if (event.step === "products" && event.percent === 100) {
          invalidate?.();
        }
      },
    }),
    /CURRENT_CASHIER_REQUIRED/,
  );
  assert.equal(activations, 0);
});

test("激活后没有同快照促销状态时返回告警，并仍返回新 active 目录摘要", async () => {
  const services = createTestComposition(
    databaseFor([]),
    {
      cashierPermissions: [CATALOG_DOWNLOAD_PERMISSION],
      transport: await catalogDownloadTransport(),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  const outcome = await services.catalog.downloadAndActivate({
    storeCode: "S001",
  });

  assert.deepEqual(outcome, {
    kind: "activated-with-warning",
    summary: {
      snapshotId: "test-id-1",
      catalogVersion: "catalog-v3",
      itemCount: 1,
      activatedAt: "2026-07-28T00:00:00.000Z",
    },
    warningCode: "catalog-runtime-reload-failed",
  });
});

test("激活后促销快照与目录不一致时返回告警", async () => {
  const services = createTestComposition(
    databaseFor([], {
      activeCatalogPromotions: {
        snapshotId: "another-active-snapshot",
        storeCode: "S001",
        promotions: [],
      },
    }),
    {
      cashierPermissions: [CATALOG_DOWNLOAD_PERMISSION],
      transport: await catalogDownloadTransport(),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  const outcome = await services.catalog.downloadAndActivate({
    storeCode: "S001",
  });

  assert.equal(outcome.kind, "activated-with-warning");
  assert.equal(outcome.warningCode, "catalog-runtime-reload-failed");
});

test("运行时目录下载在旧后端 sync-plan 404 时回退首包固定版本 full", async () => {
  const services = createTestComposition(
    databaseFor([]),
    {
      cashierPermissions: [CATALOG_DOWNLOAD_PERMISSION],
      transport: await catalogDownloadTransport({ syncPlanStatus: 404 }),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  const outcome = await services.catalog.downloadAndActivate({
    storeCode: "S001",
  });

  assert.equal(outcome.kind, "activated-with-warning");
  assert.equal(outcome.summary.catalogVersion, "catalog-v3");
});

test("目录下载成功后刷新码冲突候选，扫码精确查询返回目录胜出项与其它商品", async () => {
  const conflict = (
    overrides: Partial<CatalogLookupItem>,
  ): CatalogLookupItem => ({
    storeCode: "S001",
    productCode: "P-1",
    referenceCode: null,
    displayName: "Milk",
    lookupCode: "930000000001",
    lookupCodeNormalized: "930000000001",
    itemNumber: "I-1",
    barcode: "930000000001",
    retailPrice: 1,
    priceSource: 2,
    priceSourceLabel: "set",
    quantityFactor: 1,
    updatedAt: "2026-07-28T00:00:00.000Z",
    rowVersion: "row-conflict",
    productImage: null,
    discountRate: null,
    isSpecialProduct: false,
    ...overrides,
  });
  let conflictRequests = 0;
  const services = createTestComposition(
    databaseFor([]),
    {
      cashierPermissions: [CATALOG_DOWNLOAD_PERMISSION],
      transport: await catalogDownloadTransport({
        codeConflicts: [
          conflict({}),
          conflict({
            productCode: "P-FLOWER",
            displayName: "flower",
            itemNumber: "FLW-1",
            retailPrice: 2.99,
            priceSource: 0,
            priceSourceLabel: "product",
          }),
        ],
        onCodeConflictsRequest: () => {
          conflictRequests += 1;
        },
      }),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.deepEqual(
    (await services.catalog.findExactCandidates?.("930000000001"))?.map(
      (candidate) => candidate.productCode,
    ),
    ["P-1"],
  );

  await services.catalog.downloadAndActivate({ storeCode: "S001" });

  assert.equal(conflictRequests, 1);
  assert.deepEqual(
    (await services.catalog.findExactCandidates?.(" 930000000001 "))?.map(
      (candidate) => [candidate.productCode, candidate.retailPriceCents],
    ),
    [
      ["P-1", 100],
      ["P-FLOWER", 299],
    ],
  );
});

test("旧服务端没有码冲突接口时目录下载结果不变", async () => {
  let conflictRequests = 0;
  const services = createTestComposition(
    databaseFor([]),
    {
      cashierPermissions: [CATALOG_DOWNLOAD_PERMISSION],
      transport: await catalogDownloadTransport({
        codeConflicts: "legacy-404",
        onCodeConflictsRequest: () => {
          conflictRequests += 1;
        },
      }),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");

  const outcome = await services.catalog.downloadAndActivate({
    storeCode: "S001",
  });

  assert.equal(conflictRequests, 1);
  assert.equal(outcome.summary.catalogVersion, "catalog-v3");
  assert.equal(outcome.summary.itemCount, 1);
  assert.deepEqual(
    (await services.catalog.findExactCandidates?.("930000000001"))?.map(
      (candidate) => candidate.productCode,
    ),
    ["P-1"],
  );
});

test("settings 与维护页共享目录重载告警，已切换目录保持可用", async () => {
  const cases: readonly Readonly<{
    name: string;
    database: NonNullable<Parameters<typeof databaseFor>[1]>;
  }>[] = [
    { name: "missing", database: {} },
    {
      name: "fallback",
      database: {
        onActivePromotionsLoad() {
          throw new Error("promotion reload failed");
        },
      },
    },
    {
      name: "mismatch",
      database: {
        activeCatalogPromotions: {
          snapshotId: "other-snapshot",
          storeCode: "S001",
          promotions: [],
        },
      },
    },
  ];

  for (const scenario of cases) {
    let activations = 0;
    const services = createTestComposition(
      databaseFor([], {
        ...scenario.database,
        onCatalogActivate() { activations += 1; },
      }),
      {
        cashierPermissions: [
          SETTINGS_VIEW_PERMISSION,
          SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
        ],
        settings: settingsRuntimeConfiguration(),
        transport: await catalogDownloadTransport(),
      },
    );
    await services.initialize();
    await services.cashierSession.signIn("cashier");
    assert.equal("createPresenter" in services.settings, true, scenario.name);
    if (!("createPresenter" in services.settings)) continue;
    const presenter = services.settings.createPresenter();
    await presenter.load();
    await presenter.downloadCatalog();

    assert.equal(activations, 1, scenario.name);
    assert.equal(
      presenter.getState().statusCode,
      "catalog-downloaded",
      scenario.name,
    );
    assert.equal(
      presenter.getState().catalogRefresh.kind,
      "warning",
      scenario.name,
    );
    assert.equal(
      services.catalogRefresh.getState().kind,
      "warning",
      scenario.name,
    );
  }
});

test("销毁 settings 页面后目录继续刷新，重新进入仍可读取完成状态", async () => {
  let activateCount = 0;
  let discardCount = 0;
  let promotionsEntered!: () => void;
  let releasePromotions!: () => void;
  const entered = new Promise<void>((resolve) => { promotionsEntered = resolve; });
  const release = new Promise<void>((resolve) => { releasePromotions = resolve; });
  const services = createTestComposition(
    databaseFor([], {
      onCatalogActivate() { activateCount += 1; },
      onCatalogDiscard() { discardCount += 1; },
    }),
    {
      cashierPermissions: [
        SETTINGS_VIEW_PERMISSION,
        SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
      ],
      settings: settingsRuntimeConfiguration(),
      transport: await catalogDownloadTransport({
        beforePromotions: async () => {
          promotionsEntered();
          await release;
        },
      }),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();

  const download = presenter.downloadCatalog();
  await entered;
  presenter.destroy();
  assert.equal(services.catalogRefresh.getState().kind, "running");
  releasePromotions();
  await download;

  assert.equal(activateCount, 1);
  assert.equal(discardCount, 0);
  assert.equal(services.catalogRefresh.getState().kind, "warning");

  const reopened = services.settings.createPresenter();
  assert.equal(reopened.getState().catalogRefresh.kind, "warning");
  await reopened.load();
  assert.equal(reopened.getState().catalog.snapshotId, "test-id-1");
  reopened.destroy();
});

test("设置中确认重置后改由共享后台任务执行，离页不会中止且返回仍见结果", async () => {
  let activateCount = 0;
  let discardCount = 0;
  let promotionsEntered!: () => void;
  let releasePromotions!: () => void;
  const entered = new Promise<void>((resolve) => {
    promotionsEntered = resolve;
  });
  const release = new Promise<void>((resolve) => {
    releasePromotions = resolve;
  });
  const services = createTestComposition(
    databaseFor([], {
      onCatalogActivate() {
        activateCount += 1;
      },
      onCatalogDiscard() {
        discardCount += 1;
      },
    }),
    {
      cashierPermissions: [
        SETTINGS_VIEW_PERMISSION,
        SETTINGS_CATALOG_RESET_PERMISSION,
      ],
      settings: settingsRuntimeConfiguration(),
      transport: await catalogDownloadTransport({
        beforePromotions: async () => {
          promotionsEntered();
          await release;
        },
      }),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();

  assert.equal(presenter.requestCatalogReset(), true);
  const reset = presenter.confirmDangerousAction();
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(services.catalogRefresh.getState().kind, "running");
  await entered;
  presenter.destroy();

  releasePromotions();
  await reset;

  assert.equal(activateCount, 1);
  assert.equal(discardCount, 0);
  assert.equal(services.catalogRefresh.getState().kind, "warning");
  const reopened = services.settings.createPresenter();
  assert.equal(reopened.getState().catalogRefresh.kind, "warning");
  await reopened.load();
  assert.equal(reopened.getState().catalog.snapshotId, "test-id-1");
  reopened.destroy();
});

test("settings 与目录维护入口加入同一后台任务且只激活一次", async () => {
  let activateCount = 0;
  let promotionsEntered!: () => void;
  let releasePromotions!: () => void;
  const entered = new Promise<void>((resolve) => {
    promotionsEntered = resolve;
  });
  const release = new Promise<void>((resolve) => {
    releasePromotions = resolve;
  });
  const services = createTestComposition(
    databaseFor([], {
      onCatalogActivate() { activateCount += 1; },
    }),
    {
      cashierPermissions: [
        SETTINGS_VIEW_PERMISSION,
        SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
      ],
      settings: settingsRuntimeConfiguration(),
      transport: await catalogDownloadTransport({
        beforePromotions: async () => {
          promotionsEntered();
          await release;
        },
      }),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const settingsPresenter = services.settings.createPresenter();
  await settingsPresenter.load();

  const settingsDownload = settingsPresenter.downloadCatalog();
  await entered;
  const maintenanceJoin = services.catalogRefresh.start({
    storeCode: "S001",
    execute: ({ signal, onProgress }) =>
      services.catalog.downloadAndActivate({
        storeCode: "S001",
        signal,
        onProgress,
      }),
  });
  assert.equal(services.catalogRefresh.getState().kind, "running");

  releasePromotions();
  await Promise.all([settingsDownload, maintenanceJoin]);

  assert.equal(activateCount, 1);
  assert.equal(services.catalogRefresh.getState().kind, "warning");
  settingsPresenter.destroy();
});

test("runtime 关闭会取消并等待后台目录清理后再结束", async () => {
  let activateCount = 0;
  let discardCount = 0;
  let promotionsEntered!: () => void;
  let releasePromotions!: () => void;
  const entered = new Promise<void>((resolve) => {
    promotionsEntered = resolve;
  });
  const release = new Promise<void>((resolve) => {
    releasePromotions = resolve;
  });
  const services = createTestComposition(
    databaseFor([], {
      onCatalogActivate() { activateCount += 1; },
      onCatalogDiscard() { discardCount += 1; },
    }),
    {
      cashierPermissions: [
        SETTINGS_VIEW_PERMISSION,
        SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
      ],
      settings: settingsRuntimeConfiguration(),
      transport: await catalogDownloadTransport({
        beforePromotions: async () => {
          promotionsEntered();
          await release;
        },
      }),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();

  const download = presenter.downloadCatalog();
  await entered;
  let shutdownFinished = false;
  const shutdown = services.shutdownBackgroundWork().then(() => {
    shutdownFinished = true;
  });
  await Promise.resolve();
  assert.equal(shutdownFinished, false);

  releasePromotions();
  await Promise.all([download, shutdown]);

  assert.equal(activateCount, 0);
  assert.equal(discardCount, 1);
  assert.equal(services.catalogRefresh.getState().kind, "idle");
  presenter.destroy();
});

test("settings 目录下载绑定原 cashier lease，换班到同店身份也不能激活", async () => {
  let activateCount = 0;
  let discardCount = 0;
  let promotionsEntered!: () => void;
  let releasePromotions!: () => void;
  const entered = new Promise<void>((resolve) => { promotionsEntered = resolve; });
  const release = new Promise<void>((resolve) => { releasePromotions = resolve; });
  const services = createTestComposition(
    databaseFor([], {
      onCatalogActivate() { activateCount += 1; },
      onCatalogDiscard() { discardCount += 1; },
    }),
    {
      cashierPermissions: [
        SETTINGS_VIEW_PERMISSION,
        SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
      ],
      settings: settingsRuntimeConfiguration(),
      transport: await catalogDownloadTransport({
        beforePromotions: async () => {
          promotionsEntered();
          await release;
        },
      }),
    },
  );
  await services.initialize();
  await services.cashierSession.signIn("cashier-1");
  assert.equal("createPresenter" in services.settings, true);
  if (!("createPresenter" in services.settings)) return;
  const presenter = services.settings.createPresenter();
  await presenter.load();

  const download = presenter.downloadCatalog();
  await entered;
  await services.cashierSession.signIn("cashier-2");
  releasePromotions();
  await download;

  assert.equal(activateCount, 0);
  assert.equal(discardCount, 1);
});

test("日结只使用可信收银员作用域，先耐久归档再通过同一打印适配器输出 ESC/POS", async () => {
  const printed: Readonly<{
    jobId: string;
    bytes: Uint8Array;
  }>[] = [];
  const dailyClose = new MemoryDailyCloseRepository();
  const services = createTestComposition(
    databaseFor([], { dailyClose }),
    {
      cashierPermissions: [
        "Permissions.PosTerminal.DailyClose.View",
        "Permissions.PosTerminal.DailyClose.Save",
        "Permissions.PosTerminal.DailyClose.Reprint",
      ],
      onPrint(jobId, bytes) {
        printed.push({ jobId, bytes });
      },
    },
  );
  await services.initialize();

  assert.throws(
    () => services.dailyClose.createPresenter(),
    /CURRENT_CASHIER_REQUIRED/,
  );
  const receiptSettings = await services.receiptSettings.get();
  await services.receiptSettings.save({
    ...receiptSettings,
    returnPolicy: "Refunds within 14 days with proof of purchase.",
  });
  await services.cashierSession.signIn("cashier");
  const presenter = services.dailyClose.createPresenter();
  await presenter.load();
  assert.equal(presenter.getState().summary?.storeCode, "S001");
  assert.equal(presenter.setCount(100, 10), true);
  await presenter.saveAndPrint();

  assert.equal(dailyClose.saved.length, 1);
  assert.equal(dailyClose.saved[0]?.archive.storeCode, "S001");
  assert.equal(dailyClose.saved[0]?.archive.deviceCode, "IPAD-1");
  assert.equal(dailyClose.saved[0]?.archive.savedCashierId, "cashier-1");
  assert.equal(presenter.getState().statusCode, "saved-printed");
  assert.equal(printed.length, 1);
  assert.match(printed[0]?.jobId ?? "", /^daily-close:/);
  assert.deepEqual(
    [...(printed[0]?.bytes.slice(-3) ?? [])],
    [0x1d, 0x56, 0x00],
  );
  assert.match(
    new TextDecoder().decode(printed[0]?.bytes),
    /Refunds and returns[\s\S]*Refunds within 14 days/,
  );
  presenter.destroy();
});

test("换店凭据提交并 reload 后废弃旧 cashier/cart，提交前失败不误杀", async (context) => {
  const activationCode =
    "HBDEV1-0123456789ABCDEFGHJKMNPQRS-STVWXYZ0123456789ABCDEFGHJ";
  const cases = [
    { name: "正常 reload", afterCommit: "success" },
    { name: "重新注册失败", afterCommit: "reregister-failed" },
  ] as const;

  for (const scenario of cases) {
    await context.test(scenario.name, async (scenarioContext) => {
      let reregisterCalls = 0;
      let reloadCalls = 0;
      const secureStore = new InMemorySecureStore();
      const installation = new InstallationIdentityStore(
        secureStore,
        () => "INSTALL-001",
      );
      const credentials = new DeviceCredentialStore(secureStore);
      await credentials.save({
        deviceCode: "IPAD-1",
        storeCode: "S001",
        hardwareId: "INSTALL-001",
        authorizationCode: "old-device-secret",
      });
      const api: DeviceSessionApi = {
        async register() {
          throw new Error("not used");
        },
        async verify() {
          throw new Error("not used");
        },
        async reregister() {
          throw new Error("not used");
        },
        async rebindActivationCode() {
          reregisterCalls += 1;
          if (scenario.afterCommit === "reregister-failed") {
            throw new Error("reregister failed before credentials save");
          }
          return {
            deviceCode: "IPAD-2",
            storeCode: "S002",
            deviceStatus: 1,
            isAllowed: true,
            reasonCode: "ACTIVATED",
            authorizationCode: "new-device-secret",
          };
        },
      };
      const coordinator = new DeviceSessionCoordinator(
        api,
        installation,
        credentials,
      );
      const settings = settingsRuntimeConfiguration();
      const services = createTestComposition(databaseFor([]), {
        appUpdateTransition: new UpdateTransitionLeaseCoordinator(),
        cashierPermissions: [
          SETTINGS_VIEW_PERMISSION,
          SETTINGS_DEVICE_REGISTRATION_PERMISSION,
        ],
        settings: {
          ...settings,
          device: {
            previewActivationCode: async () => ({
              isAllowed: true,
              storeCode: "S002",
              storeName: "Target store",
              deviceSystem: "Android",
              expiresAtUtc: "2026-08-28T00:00:00.000Z",
            }),
            reregister: async (request, signal, onCredentialsCommitted) => {
              await reregisterSettingsDevice(
                request,
                signal,
                (nextRequest, markCommitted) =>
                  coordinator.rebindActivationCode(nextRequest, markCommitted),
                onCredentialsCommitted,
              );
            },
            resetRegistration: async () => "completed",
          },
          runtimeReload: {
            reload: async () => {
              reloadCalls += 1;
            },
          },
        },
      });
      scenarioContext.after(() => services.shutdownBackgroundWork());
      await services.initialize();
      await services.cashierSession.signIn("cashier");
      assert.equal("createPresenter" in services.settings, true);
      if (!("createPresenter" in services.settings)) return;
      const presenter = services.settings.createPresenter();
      await presenter.load();
      presenter.setDeviceActivationCode(activationCode);
      await presenter.previewDeviceReregistration();
      await presenter.requestDeviceReregistration();
      assert.equal(
        presenter.getState().confirmation?.kind,
        "reregister-device",
      );
      await presenter.confirmDangerousAction();

      if (scenario.afterCommit === "reregister-failed") {
        assert.equal(
          presenter.getState().statusCode,
          "device-reregister-failed",
        );
        assert.equal(reregisterCalls, 1);
        assert.equal(reloadCalls, 0);
        assert.doesNotThrow(() => services.dailyClose.createPresenter());
        return;
      }

      assert.equal(
        presenter.getState().statusCode,
        "device-reregister-started",
      );
      await presenter.confirmDangerousAction();
      assert.equal(reregisterCalls, 1);
      assert.equal(reloadCalls, 1);

      assert.equal(
        await coordinator.getRequestHeaders().then((headers) =>
          headers?.["X-HBPOS-Store-Code"],
        ),
        "S002",
      );
      assert.throws(
        () => services.dailyClose.createPresenter(),
        /CURRENT_CASHIER_REQUIRED/,
      );
    });
  }
});

test("换店已提交但 reload 失败时持续封锁旧 runtime 写入", async () => {
  const activationCode =
    "HBDEV1-0123456789ABCDEFGHJKMNPQRS-STVWXYZ0123456789ABCDEFGHJ";
  const transition = new UpdateTransitionLeaseCoordinator();
  const reloadAttempted = deferred<void>();
  let reregisterCalls = 0;
  let reloadCalls = 0;
  const secureStore = new InMemorySecureStore();
  const installation = new InstallationIdentityStore(
    secureStore,
    () => "INSTALL-001",
  );
  const credentials = new DeviceCredentialStore(secureStore);
  const lockStore = new DeviceLockStore(secureStore);
  const pendingRegistration = new PendingDeviceRegistrationStore(secureStore);
  const pendingActivation = new PendingDeviceActivationCodeStore(secureStore);
  pendingRegistration.clear = async () => {
    throw new Error("post-save pending registration clear failed");
  };
  await credentials.save({
    deviceCode: "IPAD-1",
    storeCode: "S001",
    hardwareId: "INSTALL-001",
    authorizationCode: "old-device-secret",
  });
  const coordinator = new DeviceSessionCoordinator(
    {
      async register() { throw new Error("not used"); },
      async verify() { throw new Error("not used"); },
      async reregister() { throw new Error("not used"); },
      async rebindActivationCode() {
        reregisterCalls += 1;
        return {
          deviceCode: "IPAD-2",
          storeCode: "S002",
          deviceStatus: 1,
          isAllowed: true,
          reasonCode: "ACTIVATED",
          authorizationCode: "new-device-secret",
        };
      },
    },
    installation,
    credentials,
    lockStore,
    pendingRegistration,
    undefined,
    pendingActivation,
  );
  const settings = settingsRuntimeConfiguration();
  const services = createTestComposition(databaseFor([]), {
    appUpdateTransition: transition,
    cashierPermissions: [
      SETTINGS_VIEW_PERMISSION,
      SETTINGS_DEVICE_REGISTRATION_PERMISSION,
    ],
    settings: {
      ...settings,
      device: {
        previewActivationCode: async () => ({
          isAllowed: true,
          storeCode: "S002",
          storeName: "Target store",
          deviceSystem: "Android",
          expiresAtUtc: "2026-08-28T00:00:00.000Z",
        }),
        reregister: async (request, signal, onCredentialsCommitted) => {
          await reregisterSettingsDevice(
            request,
            signal,
            (nextRequest, markCommitted) =>
              coordinator.rebindActivationCode(nextRequest, markCommitted),
            onCredentialsCommitted,
          );
        },
        resetRegistration: async () => "completed",
      },
      runtimeReload: {
        reload: async () => {
          reloadCalls += 1;
          reloadAttempted.resolve();
          throw new Error("runtime reload failed");
        },
      },
    },
  });

  try {
    await services.initialize();
    await services.cashierSession.signIn("cashier");
    const sales = services.sales.createPresenter();
    sales.setQuery("930000000001");
    assert.equal("createPresenter" in services.settings, true);
    if (!("createPresenter" in services.settings)) return;
    const presenter = services.settings.createPresenter();
    await presenter.load();
    presenter.setDeviceActivationCode(activationCode);
    await presenter.previewDeviceReregistration();
    await presenter.requestDeviceReregistration();

    let confirmationSettled = false;
    const confirmation = presenter.confirmDangerousAction();
    void confirmation.then(
      () => { confirmationSettled = true; },
      () => { confirmationSettled = true; },
    );
    await reloadAttempted.promise;
    await new Promise<void>((resolve) => { setImmediate(resolve); });

    assert.equal(confirmationSettled, false);
    assert.equal(transition.isTransitionActive(), true);
    assert.equal(presenter.getState().busy, true);
    assert.equal(
      presenter.getState().statusCode,
      "device-reregister-restart-required",
    );
    await presenter.confirmDangerousAction();
    assert.equal(reregisterCalls, 1);
    assert.equal(reloadCalls, 1);
    assert.equal(
      await coordinator.getRequestHeaders().then((headers) =>
        headers?.["X-HBPOS-Store-Code"],
      ),
      "S002",
    );
    assert.equal(await sales.addLookupCode(), false);
    assert.throws(
      () => services.dailyClose.createPresenter(),
      /CURRENT_CASHIER_REQUIRED/,
    );
    await assert.rejects(
      services.sync.requestDrain(),
      (error: unknown) =>
        error instanceof Error &&
        (error as Error & { code?: string }).code ===
          UPDATE_TRANSITION_IN_PROGRESS,
    );
    sales.destroy();
    presenter.destroy();
  } finally {
    // terminal fence 故意让 catalog exclusive 永不完成；真实进程由 reload/restart
    // 丢弃整个 runtime，测试只触发同步退订和 timer 清理，不能等待该 fence。
    void services.shutdownBackgroundWork();
  }
});

test("初始化失败收尾后解除 device scope listener，不让重试 runtime 重复失效", async () => {
  let oldClearCalls = 0;
  let retryClearCalls = 0;
  const failed = createTestComposition(
    databaseFor([], {
      heldOrderRecords: {
        async getTerminalFence() {
          throw new Error("terminal fence initialization failed");
        },
      },
    }),
    { onClearAuthorization: () => { oldClearCalls += 1; } },
  );
  await assert.rejects(
    () => failed.initialize(),
    /terminal fence initialization failed/,
  );
  await failed.cashierSession.signIn("old-cashier");
  oldClearCalls = 0;

  const retry = createTestComposition(databaseFor([]), {
    onClearAuthorization: () => { retryClearCalls += 1; },
  });
  await retry.initialize();
  await retry.cashierSession.signIn("retry-cashier");

  const secureStore = new InMemorySecureStore();
  const installation = new InstallationIdentityStore(
    secureStore,
    () => "INSTALL-001",
  );
  const credentials = new DeviceCredentialStore(secureStore);
  await credentials.save({
    deviceCode: "IPAD-1",
    storeCode: "S001",
    hardwareId: "INSTALL-001",
    authorizationCode: "old-device-secret",
  });
  const coordinator = new DeviceSessionCoordinator(
    {
      async register() { throw new Error("not used"); },
      async verify() { throw new Error("not used"); },
      async reregister() {
        return {
          deviceCode: "IPAD-2",
          storeCode: "S002",
          deviceStatus: 1,
          isAllowed: true,
          authorizationCode: "new-device-secret",
        };
      },
    },
    installation,
    credentials,
  );

  await coordinator.reregister({ targetStoreCode: "S002" });
  assert.equal(oldClearCalls, 0, "失败 runtime 的旧闭包必须已退订");
  assert.equal(
    retryClearCalls,
    2,
    "仅重试 runtime 响应一次 scope 撤销及一次旧会话清理",
  );
  await failed.shutdownBackgroundWork();
  await failed.shutdownBackgroundWork();
  await retry.shutdownBackgroundWork();
});

test("device scope 外部撤销回调异常时仍同步废弃旧 cashier 与购物车", async () => {
  const secureStore = new InMemorySecureStore();
  const installation = new InstallationIdentityStore(
    secureStore,
    () => "INSTALL-001",
  );
  const credentials = new DeviceCredentialStore(secureStore);
  await credentials.save({
    deviceCode: "IPAD-1",
    storeCode: "S001",
    hardwareId: "INSTALL-001",
    authorizationCode: "device-token-1",
  });
  const coordinator = new DeviceSessionCoordinator(
    {
      async register() { throw new Error("not used"); },
      async verify() { throw new Error("not used"); },
      async reregister() {
        return {
          deviceCode: "IPAD-2",
          storeCode: "S002",
          deviceStatus: 1,
          isAllowed: true,
          authorizationCode: "device-token-2",
        };
      },
    },
    installation,
    credentials,
  );
  const services = createTestComposition(databaseFor([]), {
    throwOnScopeInvalidation: true,
  });
  try {
    await services.initialize();
    await services.cashierSession.signIn("cashier");
    const sales = services.sales.createPresenter();
    sales.setQuery("930000000001");
    assert.equal(await sales.addLookupCode(), true);
    assert.equal(sales.getState().cart.lines.length, 1);

    await coordinator.reregister({ targetStoreCode: "S002" });

    // scope invalidation 保留只读购物车快照供 UI 收尾，但旧 presenter 不得再写入。
    assert.equal(await sales.addLookupCode(), false);
    assert.throws(
      () => services.dailyClose.createPresenter(),
      /CURRENT_CASHIER_REQUIRED/,
    );
    sales.destroy();
  } finally {
    await services.shutdownBackgroundWork();
  }
});

test("组合根同步构造失败时不遗留 device scope listener", async () => {
  let staleRuntimeInvalidations = 0;
  const database = databaseFor([]);
  Object.assign(database, {
    catalogSnapshots() {
      throw new Error("catalog composition failed");
    },
  });
  assert.throws(
    () =>
      createTestComposition(database, {
        onClearAuthorization: () => { staleRuntimeInvalidations += 1; },
      }),
    /catalog composition failed/,
  );

  const secureStore = new InMemorySecureStore();
  const installation = new InstallationIdentityStore(
    secureStore,
    () => "INSTALL-001",
  );
  const credentials = new DeviceCredentialStore(secureStore);
  await credentials.save({
    deviceCode: "IPAD-1",
    storeCode: "S001",
    hardwareId: "INSTALL-001",
    authorizationCode: "old-device-secret",
  });
  const coordinator = new DeviceSessionCoordinator(
    {
      async register() { throw new Error("not used"); },
      async verify() { throw new Error("not used"); },
      async reregister() {
        return {
          deviceCode: "IPAD-2",
          storeCode: "S002",
          deviceStatus: 1,
          isAllowed: true,
          authorizationCode: "new-device-secret",
        };
      },
    },
    installation,
    credentials,
  );

  await coordinator.reregister({ targetStoreCode: "S002" });
  assert.equal(staleRuntimeInvalidations, 0);
});

test("收银员登录审计使用统一的手持 POS 产品身份", async () => {
  const auditEvents: AuditEventDraft[] = [];
  const services = createTestComposition(
    databaseFor([], { auditEvents }),
  );

  await services.cashierSession.signIn("cashier");

  assert.deepEqual(
    auditEvents.map((event) => ({
      eventType: event.eventType,
      source: event.payload.source,
    })),
    [{ eventType: "CASHIER_LOGIN", source: "pos-handheld" }],
  );
});

function createTestComposition(
  database: PosDatabase,
  options: Readonly<{
    canStartNewTransaction?: () => boolean;
    cashierPermissions?: readonly string[];
    supervisorPermissions?: readonly string[];
    captureInvalidation?(listener: () => void): void;
    onClearAuthorization?(): void;
    throwOnScopeInvalidation?: boolean;
    transport?: HbposTransport;
    onPrint?(jobId: string, bytes: Uint8Array): void;
    waitForPrint?(): Promise<void>;
    onDrawerOpen?(actionId: string): void;
    installmentBootstrap?: PaymentProviderRuntimeBootstrap;
    installmentPerformanceRecorder?: Readonly<{
      record(event: InstallmentPerformanceEvent): void | Promise<void>;
    }>;
    createId?: () => string;
    sha256Hex?: (material: string) => Promise<string>;
    settings?: ProductionSettingsRuntimeConfiguration;
    appUpdateTransition?: UpdateTransitionLeaseCoordinator;
  }> = {},
) {
  let nextId = 0;
  const supervisorPermissions = options.supervisorPermissions;
  return createProductionPosRuntimeServices({
    database,
    transport: options.transport ?? ({} as HbposTransport),
    encryptor: encryptor(),
    syncSecurity: { async lockDevice() {} },
    auditMetadata: {
      storeCode: "S001",
      deviceCode: "IPAD-1",
      appVersion: "0.1.0-test",
      instanceId: "test-instance",
    },
    supportAppId: "com.hbweb.posipad",
    clock: {
      now: () => new Date("2026-07-28T00:00:00.000Z"),
      nowIso: () => "2026-07-28T00:00:00.000Z",
    },
    createId: options.createId ?? (() => `test-id-${++nextId}`),
    random: () => 0.5,
    sha256Hex:
      options.sha256Hex ?? (async (material) => `sha256:${material}`),
    ...(options.installmentPerformanceRecorder
      ? {
          installmentPerformanceRecorder:
            options.installmentPerformanceRecorder,
        }
      : {}),
    catalogPageDigest: nodeCatalogPageDigest,
    createPrinter: () => ({
      async connect() {},
      async print(jobId, bytes) {
        options.onPrint?.(jobId, bytes);
        await options.waitForPrint?.();
        return { status: "printed", errorCode: null } as const;
      },
      async open(actionId) {
        options.onDrawerOpen?.(actionId);
        return { status: "completed", errorCode: null } as const;
      },
    }),
    connectivity: { async isOnline() { return true; } },
    cashierAuthentication: {
      async login(request) {
        return {
          source: "online",
          session: {
            authorizationToken: "cashier-session-secret",
            authorizationExpiresAtUtc: "2026-07-29T00:00:00.000Z",
            cashierId: "cashier-1",
            userGuid: "user-1",
            cashierName: "Cashier",
            storeCode: request.storeCode,
            deviceCode: request.deviceCode,
            permissionCodes: [
              SALES_PERMISSIONS.view,
              SALES_PERMISSIONS.addItem,
              ...(options.cashierPermissions ?? []),
            ],
          },
        };
      },
    },
    cashierSessionSecurity: {
      async getDeviceIdentity() {
        return { storeCode: "S001", deviceCode: "IPAD-1" };
      },
    async clearAuthorization() {
      options.onClearAuthorization?.();
    },
    invalidateAuthorizationForDeviceScope() {
      options.onClearAuthorization?.();
      if (options.throwOnScopeInvalidation) {
        throw new Error("scope invalidation callback failed");
      }
    },
    subscribeSessionInvalidation(listener) {
        options.captureInvalidation?.(listener);
        return () => undefined;
      },
    },
    newTransactionGate: {
      getGate: () => ({
        state:
          (options.canStartNewTransaction?.() ?? true)
            ? "enabled"
            : "disabled",
        canStartNewTransaction:
          options.canStartNewTransaction?.() ?? true,
        canContinueRecovery: true,
      }),
    },
    ...(options.appUpdateTransition
      ? { appUpdateTransition: options.appUpdateTransition }
      : {}),
    ...(supervisorPermissions
      ? {
          operationAuthorization: {
            cashierAuthentication: {
              async login(request: Readonly<{
                storeCode: string;
                deviceCode: string;
                userBarcode: string;
              }>) {
                return {
                  source: "online" as const,
                  session: {
                    authorizationToken: "supervisor-session-secret",
                    authorizationExpiresAtUtc:
                      "2026-07-29T00:00:00.000Z",
                    cashierId: "supervisor-1",
                    userGuid: "supervisor-user-1",
                    cashierName: "Supervisor",
                    storeCode: request.storeCode,
                    deviceCode: request.deviceCode,
                    permissionCodes: [...supervisorPermissions],
                  },
                };
              },
            },
          },
        }
      : {}),
    ...(options.installmentBootstrap
      ? {
          installments: {
            bootstrap: options.installmentBootstrap,
          },
        }
      : {}),
    ...(options.settings ? { settings: options.settings } : {}),
  });
}

function settingsRuntimeConfiguration(): ProductionSettingsRuntimeConfiguration {
  return {
    apiBaseUrl: "https://pos.example.test",
    appVersion: "0.1.0-test",
    updateChannel: "preview",
    readDevicePresentation: async () => ({
      deviceCode: "IPAD-1",
      storeCode: "S001",
      storeName: "Test Store",
      terminalName: "Test Terminal",
    }),
    paymentConfiguration: {
      current: null,
      availability: {
        square: { available: false, blockerCode: "not-configured" },
        linkly: { available: false, blockerCode: "not-configured" },
      },
      test: async () => undefined,
      save: async () => undefined,
    },
    apiConfiguration: {
      probe: async () => true,
      save: async () => undefined,
      runSwitchGuarded: async (operation) => ({
        blocked: false as const,
        value: await operation(),
      }),
    },
    runtimeReload: { reload: async () => undefined },
    device: {
      reregister: async () => undefined,
      resetRegistration: async () => "completed",
      hasRegistrationRecoveryRisk: async () => false,
    },
    printer: {
      getStatus: async () => "ready",
      scan: async () => [],
      connect: async () => undefined,
      disconnect: async () => undefined,
      print: async () => ({ status: "printed", errorCode: null }),
      subscribe: () => () => undefined,
      open: async () => ({ status: "completed", errorCode: null }),
    },
    scanner: {
      status: "ready",
      test: async () => ({ source: "hid", value: "SKU-1" }),
    },
    appUpdate: {
      snapshot: () => ({
        channel: "preview",
        currentVersion: "0.1.0-test",
        availableVersion: null,
        updateRequired: false,
        restartAvailable: true,
      }),
      check: async () => ({
        channel: "preview",
        currentVersion: "0.1.0-test",
        availableVersion: null,
        updateRequired: false,
        restartAvailable: true,
      }),
      restart: async () => true,
    },
  };
}

async function catalogDownloadTransport(
  options: Readonly<{
    beforePromotions?(): void | Promise<void>;
    syncPlanStatus?: 404 | 501;
    codeConflicts?: readonly CatalogLookupItem[] | "legacy-404";
    onCodeConflictsRequest?(): void;
  }> = {},
): Promise<HbposTransport> {
  const item: CatalogLookupItem = {
    storeCode: "S001",
    productCode: "P-001",
    referenceCode: null,
    displayName: "Milk",
    lookupCode: "930000000001",
    lookupCodeNormalized: "930000000001",
    itemNumber: "I-001",
    barcode: null,
    retailPrice: 12.34,
    priceSource: 0,
    priceSourceLabel: "product",
    quantityFactor: 1,
    updatedAt: "2026-07-28T00:00:00.000Z",
    rowVersion: "row-1",
    productImage: null,
    discountRate: null,
    isSpecialProduct: false,
  };
  const pageChecksum = await calculateCatalogPageChecksum(
    [item],
    nodeCatalogPageDigest,
    2,
  );
  return {
    async request<T>(request: HbposTransportRequest) {
      if (request.url === "/api/v1/catalog/sync-plan") {
        if (options.syncPlanStatus) {
          throw new HbposApiError("legacy catalog backend", {
            kind: "http",
            status: options.syncPlanStatus,
          });
        }
        return {
          status: 200,
          data: {
            success: true,
            data: {
              storeCode: "S001",
              generatedAt: "2026-07-28T00:00:00.000Z",
              mode: "full",
              baseCatalogVersion:
                request.params?.baseCatalogVersion ?? null,
              targetCatalogVersion: "catalog-v3",
              targetTotal: 1,
            },
          } as T,
        };
      }
      if (request.url === "/api/v1/catalog/sellable-items/page") {
        return {
          status: 200,
          data: {
            success: true,
            data: {
              storeCode: "S001",
              generatedAt: "2026-07-28T00:00:00.000Z",
              cursor: null,
              items: [item],
              deletedLookups: [],
              nextCursor: null,
              hasMore: false,
              totalCount: 1,
              catalogVersion: "catalog-v3",
              pageChecksum,
            },
          } as T,
        };
      }
      if (request.url === "/api/v1/catalog/promotions") {
        await options.beforePromotions?.();
        return {
          status: 200,
          data: {
            success: true,
            data: {
              storeCode: "S001",
              generatedAt: "2026-07-28T00:00:00.000Z",
              promotions: [],
            },
          } as T,
        };
      }
      if (request.url === "/api/v1/catalog/sellable-items/code-conflicts") {
        options.onCodeConflictsRequest?.();
        if (options.codeConflicts === "legacy-404") {
          throw new HbposApiError("legacy catalog backend", {
            kind: "http",
            status: 404,
          });
        }
        return {
          status: 200,
          data: {
            success: true,
            data: {
              storeCode: "S001",
              generatedAt: "2026-07-28T00:00:00.000Z",
              available: true,
              items: options.codeConflicts ?? [],
            },
          } as T,
        };
      }
      throw new Error(`Unexpected catalog URL: ${request.url}`);
    },
  };
}

function unavailableHeldOrderRecords(): HeldOrderRecordRepositoryPort {
  return {
    async hold() {
      throw new Error("held order hold is not configured");
    },
    async listPending() {
      return [];
    },
    async claimRecall() {
      return null;
    },
    async getTerminalFence() {
      return null;
    },
    async loadRecallForFence() {
      return null;
    },
    async confirmHoldCartCleared() {
      return false;
    },
    async releaseRecallAfterCartCleared() {
      return false;
    },
    async listRecoverable() {
      return [];
    },
  };
}

function databaseFor(
  durableCommits: DurableCashOrderCommit[],
  options: Readonly<{
    lastOrder?: LocalOrder;
    onFrozenSettingsRead?(): void;
    onReceiptSettingsSave?(settings: ReceiptPrinterSettings): void;
    onReprintPrepared?(input: Readonly<{
      orderGuid: string;
      printerId: string;
      receiptBytes: Uint8Array;
    }>): void;
    onManualDrawerCreated?(): void;
    onFulfilmentTerminalPersisted?(
      kind: "reprint" | "drawer",
    ): void;
    heldOrderRecords?: Partial<HeldOrderRecordRepositoryPort>;
    syncHistoryOrders?: readonly LocalSyncHistoryOrder[];
    specialProductItems?: readonly SpecialProductItem[];
    dailyClose?: DailyCloseRepositoryPort;
    onSyncHistoryRestore?(): void;
    onRefundVoucherPrintMaterialCreated?(): void;
    returnRecoveryRequired?: boolean;
    returnRecoveryCount?: number;
    paymentDraftRecoveryRequired?: boolean;
    voucherTenderReversalRecoveryRequired?: boolean;
    installmentRecoveryRequired?: boolean;
    recoveryProbeFailure?: "payment" | "return" | "installment";
    activeCatalogPromotions?: ActiveCatalogPromotions | null;
    activeCatalogMetadata?: ActiveCatalogMetadata | null;
    onCatalogActivate?(): void;
    onCatalogDiscard?(): void;
    onActivePromotionsLoad?(storeCode: string): void;
    auditEvents?: AuditEventDraft[];
    pendingOrderSyncCount?: number;
  }> = {},
): PosDatabase {
  let activeCatalogMetadata = options.activeCatalogMetadata ?? null;
  const lookupOverlays = new Map<string, LocalCatalogMatch | null>();
  const codeConflicts = new Map<string, readonly LocalCatalogMatch[]>();
  let stagingCatalog: Readonly<{
    snapshotId: string;
    catalogVersion: string;
    itemCount: number;
  }> | null = null;
  const findBaseCatalogMatch = (
    lookupCode: string,
  ): LocalCatalogMatch | null =>
    lookupCode === "930000000001"
      ? {
          storeCode: "S001",
          productCode: "P-1",
          referenceCode: null,
          itemNumber: "I-1",
          displayName: "Milk",
          barcode: "930000000001",
          lookupCode: "930000000001",
          lookupCodeNormalized: "930000000001",
          retailPriceCents: 100,
          priceSource: 0,
          priceSourceLabel: "Retail",
          quantityFactor: 1,
          taxRateBasisPoints: 1_000,
          updatedAtIso: null,
          rowVersion: "1",
          productImage: null,
          discountRate: null,
          isSpecialProduct: false,
        }
      : null;
  let receiptPrinterSettings: ReceiptPrinterSettings = {
    printEnabled: true,
    drawerEnabled: true,
    peripheralId: "printer-1",
    paper: "80mm",
    locale: "en",
    brandName: "Hot Bargain",
    storeName: "Brisbane",
    address: "1 Queen St",
    phone: "0712345678",
    abn: "12 345 678 901",
    returnPolicy: "",
    profileStoreCode: "S001",
  };
  const settings = {
    async getReceiptPrinterSettings() {
      options.onFrozenSettingsRead?.();
      return receiptPrinterSettings;
    },
    async saveReceiptPrinterSettings(input: ReceiptPrinterSettings) {
      receiptPrinterSettings = input;
      options.onReceiptSettingsSave?.(input);
      return input;
    },
  };
  const repositories = {
    orders: {
      async nextLocalSequence() { return 1; },
      async getByGuid(orderGuid: string) {
        return options.lastOrder?.orderGuid === orderGuid
          ? options.lastOrder
          : null;
      },
      async listLocal() {
        return options.lastOrder ? [options.lastOrder] : [] as LocalOrder[];
      },
    },
    payments: {},
    outbox: {},
    audit: options.auditEvents
      ? {
          async append(events: readonly AuditEventDraft[]) {
            options.auditEvents?.push(...events);
          },
        }
      : {},
    heldOrderRecords: {
      ...unavailableHeldOrderRecords(),
      ...options.heldOrderRecords,
    },
  };

  return {
    readPendingOrderSyncCount: async () =>
      options.pendingOrderSyncCount ?? 0,
    repositories: () => repositories,
    orderSyncMaterial: () => ({
      async resolve(order: LocalOrder) {
        return order;
      },
    }),
    catalogSnapshots: () => ({
      async getActiveMetadata() {
        return activeCatalogMetadata;
      },
      async beginStaging(input: Readonly<{
        snapshotId: string;
        catalogVersion: string;
      }>) {
        stagingCatalog = {
          snapshotId: input.snapshotId,
          catalogVersion: input.catalogVersion,
          itemCount: 0,
        };
      },
      async appendPage(snapshotId: string, items: readonly unknown[]) {
        if (!stagingCatalog || stagingCatalog.snapshotId !== snapshotId) {
          throw new Error("unexpected catalog staging page");
        }
        stagingCatalog = {
          ...stagingCatalog,
          itemCount: stagingCatalog.itemCount + items.length,
        };
      },
      async replacePromotions() {},
      async activate(
        snapshotId: string,
        expectedItemCount: number,
        activatedAt: string,
      ) {
        if (
          !stagingCatalog ||
          stagingCatalog.snapshotId !== snapshotId ||
          stagingCatalog.itemCount !== expectedItemCount
        ) {
          throw new Error("unexpected catalog activation");
        }
        activeCatalogMetadata = {
          snapshotId,
          catalogVersion: stagingCatalog.catalogVersion,
          itemCount: expectedItemCount,
          activatedAt,
        };
        options.onCatalogActivate?.();
        stagingCatalog = null;
      },
      async discardStaging(snapshotId: string) {
        if (stagingCatalog?.snapshotId === snapshotId) stagingCatalog = null;
        options.onCatalogDiscard?.();
      },
      async loadActivePromotions(storeCode: string) {
        options.onActivePromotionsLoad?.(storeCode);
        return options.activeCatalogPromotions ?? null;
      },
      async findExact(lookupCode: string) {
        return findBaseCatalogMatch(lookupCode);
      },
      async searchByName() {
        return [];
      },
    }),
    catalogLookupOverlay: () => ({
      async getActiveSnapshotId() {
        return activeCatalogMetadata?.snapshotId ?? null;
      },
      async upsert(input: Readonly<{
        baseSnapshotId: string | null;
        item: LocalCatalogMatch;
      }>) {
        if (
          input.baseSnapshotId !==
          (activeCatalogMetadata?.snapshotId ?? null)
        ) {
          return "stale-generation" as const;
        }
        lookupOverlays.set(
          `${input.item.storeCode}\0${input.item.lookupCodeNormalized}`,
          input.item,
        );
        return "applied" as const;
      },
      async tombstone(input: Readonly<{
        baseSnapshotId: string | null;
        storeCode: string;
        lookupCodeNormalized: string;
      }>) {
        if (
          input.baseSnapshotId !==
          (activeCatalogMetadata?.snapshotId ?? null)
        ) {
          return "stale-generation" as const;
        }
        lookupOverlays.set(
          `${input.storeCode}\0${input.lookupCodeNormalized}`,
          null,
        );
        return "applied" as const;
      },
      async findExact(storeCode: string, lookupCode: string) {
        const normalized = lookupCode.trim().toUpperCase();
        const key = `${storeCode}\0${normalized}`;
        return lookupOverlays.has(key)
          ? lookupOverlays.get(key) ?? null
          : findBaseCatalogMatch(normalized);
      },
      async searchByName(
        storeCode: string,
        query: string,
        limit: number,
        offset = 0,
      ) {
        const normalizedQuery = query.trim().toLowerCase();
        return [...lookupOverlays.values()]
          .filter(
            (item): item is LocalCatalogMatch =>
              item !== null &&
              item.storeCode === storeCode &&
              item.displayName.toLowerCase().includes(normalizedQuery),
          )
          .slice(offset, offset + limit);
      },
      async cleanupOldGenerations() {
        return 0;
      },
    }),
    catalogCodeConflicts: () => ({
      async replaceStoreConflicts(
        storeCode: string,
        items: readonly LocalCatalogMatch[],
      ) {
        for (const key of [...codeConflicts.keys()]) {
          if (key.startsWith(`${storeCode}\0`)) codeConflicts.delete(key);
        }
        for (const conflict of items) {
          const key = `${storeCode}\0${conflict.lookupCodeNormalized}`;
          codeConflicts.set(key, [...(codeConflicts.get(key) ?? []), conflict]);
        }
        return {
          storeCode,
          itemCount: items.length,
          lookupCodeCount: new Set(
            items.map((conflict) => conflict.lookupCodeNormalized),
          ).size,
        };
      },
      async findCandidates(storeCode: string, lookupCode: string) {
        return (
          codeConflicts.get(`${storeCode}\0${lookupCode.trim().toUpperCase()}`) ??
          []
        );
      },
    }),
    specialProducts: () => ({
      async list(storeCode: string, limit: number, offset: number) {
        return (options.specialProductItems ?? [])
          .filter((item) => item.storeCode === storeCode)
          .slice(offset, offset + limit);
      },
      async searchCandidates() {
        return [];
      },
      async replaceDownloaded() {},
      async applyMark() {},
      async saveOrder() {},
    }),
    dailyCloses: () =>
      options.dailyClose ?? new MemoryDailyCloseRepository(),
    settings: () => settings,
    settingsSafety: () => ({
      async read() {
        return {
          paymentConfigurationSensitiveOrderCount: 0,
          pendingDurableWriteCount: 0,
          pendingReturnCount: 0,
          pendingSaleCount: 0,
          unresolvedPaymentCount: 0,
        };
      },
    }),
    offlineReturnCapacity: () => ({
      async hasCapacity() {
        return false;
      },
    }),
    fulfilmentStore: () => reprintStore(options),
    receiptCompletionSettlements: () => ({
      async getByOrderGuid() {
        return options.lastOrder ? { cashChangeCents: 0 } : null;
      },
    }),
    cashOrderCommitter: () => ({
      async completeDurableCashOrder(input: DurableCashOrderCommit) {
        durableCommits.push(input);
        return {
          replayed: false,
          orderGuid: input.command.order.orderGuid,
          cashDueCents: input.intent.cashDueCents,
          changeCents: input.intent.changeCents,
        };
      },
    }),
    paymentDraftRecovery: () => ({
      async findBlockingRecovery() {
        if (options.recoveryProbeFailure === "payment") {
          throw new Error("payment recovery storage unavailable");
        }
        return options.paymentDraftRecoveryRequired
          ? ({ draftId: "payment-draft-recovery" } as never)
          : null;
      },
      // 正常组合 fake 明确表示没有 Linkly ACK/legacy 记录；专项故障用例仍由上面的抛错覆盖。
      async findPendingLinklyAcknowledgement() { return null; },
      async hasLegacyLinklyRecovery() { return false; },
      async findLegacyLinklyAttemptForSession() { return null; },
    }),
    voucherTenderReversals: () => ({
      async findBlocking() {
        return options.voucherTenderReversalRecoveryRequired
          ? ({ actionId: "voucher-reversal-recovery" } as never)
          : null;
      },
    }),
    installmentActions: () => ({
      async loadBlocking() {
        if (options.recoveryProbeFailure === "installment") {
          throw new Error("installment recovery storage unavailable");
        }
        return options.installmentRecoveryRequired
          ? ({ actionId: "installment-recovery" } as never)
          : null;
      },
    }),
    paymentOrderCommitter: () => ({}),
    returnCapacityVault: () => ({
      async protect() {
        throw new Error("return capacity protect is not used");
      },
    }),
    returnExecutionLedger: () => ({
      async listRecoverable() {
        if (options.recoveryProbeFailure === "return") {
          throw new Error("return recovery storage unavailable");
        }
        return Array.from(
          {
            length:
              options.returnRecoveryCount ??
              (options.returnRecoveryRequired ? 1 : 0),
          },
          () => ({} as never),
        );
      },
    }),
    returnFulfilmentPlans: () => ({
      async get() {
        return null;
      },
      async listPending() {
        return [];
      },
      async materialize() {
        throw new Error("return fulfilment materialize is not used");
      },
    }),
    refundVoucherPrintMaterial: () => {
      options.onRefundVoucherPrintMaterialCreated?.();
      return {
        async resolveApprovedRefundVoucher() {
          return null;
        },
      };
    },
    localSyncHistory: (
      supportContext: LocalSyncHistorySupportContext,
    ) => ({
      async listLocalSyncHistory() {
        const orders = options.syncHistoryOrders ?? [];
        return {
          orders,
          nextBeforeLocalSequence: null,
          pendingCount: orders.filter(
            (order) => order.outbox?.state === "pending",
          ).length,
        };
      },
      async restoreExistingOrderOutboxToPending(orderGuids: readonly string[]) {
        options.onSyncHistoryRestore?.();
        const available = new Set(
          (options.syncHistoryOrders ?? []).map((order) => order.orderGuid),
        );
        return {
          restoredOrderGuids: orderGuids.filter((orderGuid) =>
            available.has(orderGuid),
          ),
          skippedOrderGuids: orderGuids.filter(
            (orderGuid) => !available.has(orderGuid),
          ),
        };
      },
      async getSupportContext() {
        return supportContext;
      },
    }),
  } as unknown as PosDatabase;
}

class MemoryDailyCloseRepository implements DailyCloseRepositoryPort {
  public readonly saved: DailyCloseArchiveCommit[] = [];
  private readonly archives: DailyCloseArchiveCommit["archive"][] = [];

  public async summarize(scope: DailyCloseScope): Promise<DailyCloseSummary> {
    return {
      ...scope,
      orderCount: 1,
      returnQuantity: "0",
      tenders: [
        {
          method: "cash",
          salesCents: 1_000,
          refundCents: 0,
          netCents: 1_000,
        },
        {
          method: "card",
          salesCents: 0,
          refundCents: 0,
          netCents: 0,
        },
        {
          method: "voucher",
          salesCents: 0,
          refundCents: 0,
          netCents: 0,
        },
      ],
      expectedCashCents: 1_000,
    };
  }

  public async saveArchive(input: DailyCloseArchiveCommit) {
    this.saved.push(input);
    this.archives.unshift(input.archive);
    return { replayed: false, archive: input.archive };
  }

  public async getArchive(closeId: string) {
    return (
      this.archives.find((archive) => archive.closeId === closeId) ??
      null
    );
  }

  public async listArchives(
    scope: Readonly<{
      storeCode: string;
      deviceCode: string;
      businessDate?: string | null;
      limit: number;
    }>,
  ) {
    return this.archives
      .filter(
        (archive) =>
          archive.storeCode === scope.storeCode &&
          archive.deviceCode === scope.deviceCode &&
          (!scope.businessDate ||
            archive.businessDate === scope.businessDate),
      )
      .slice(0, scope.limit);
  }
}

function reprintStore(
  options: Parameters<typeof databaseFor>[1],
) {
  let reprint: Readonly<{
    jobId: string;
    orderGuid: string;
    printerId: string;
    isReprint: true;
    bytes: Uint8Array;
    state: "Queued" | "Sending" | "Printed" | "Failed" | "Ambiguous";
    retryCount: number;
  }> | null = null;
  let drawer: Readonly<{
    eventId: string;
    orderGuid: null;
    printerId: string;
    state: "Requested" | "Completed" | "Failed" | "Unknown";
    reason: "MANUAL";
    retryCount: number;
  }> | null = null;
  return {
    async listQueuedPrintJobs() {
      return [];
    },
    async listRequiredDrawerEvents() {
      return [];
    },
    async beginManualDrawerOpen(input: Readonly<{
      eventId: string;
      printerId: string;
      reason: "MANUAL";
    }>) {
      if (drawer?.eventId === input.eventId) {
        return { kind: "existing" as const, event: drawer };
      }
      drawer = {
        ...input,
        orderGuid: null,
        state: "Requested",
        retryCount: 0,
      };
      options?.onManualDrawerCreated?.();
      return { kind: "created" as const, event: drawer };
    },
    async finishDrawerEvent(
      eventId: string,
      _expected: "Requested",
      state: "Completed" | "Failed" | "Unknown",
    ) {
      if (!drawer || drawer.eventId !== eventId) return false;
      drawer = { ...drawer, state };
      options?.onFulfilmentTerminalPersisted?.("drawer");
      return true;
    },
    async createLastReceiptReprint(input: Readonly<{
      orderGuid: string;
      printerId: string;
      receiptBytes: Uint8Array;
    }>) {
      options?.onReprintPrepared?.(input);
      reprint = {
        jobId: "reprint-job-1",
        orderGuid: input.orderGuid,
        printerId: input.printerId,
        isReprint: true,
        bytes: input.receiptBytes,
        state: "Queued",
        retryCount: 0,
      };
      return reprint;
    },
    async claimQueuedPrintJob(jobId: string) {
      if (reprint?.jobId !== jobId || reprint.state !== "Queued") {
        return null;
      }
      reprint = { ...reprint, state: "Sending" };
      return reprint;
    },
    async finishPrintJob(
      jobId: string,
      _expected: "Sending",
      state: "Printed" | "Failed" | "Ambiguous",
    ) {
      if (!reprint || reprint.jobId !== jobId) return false;
      reprint = { ...reprint, state };
      options?.onFulfilmentTerminalPersisted?.("reprint");
      return true;
    },
  };
}

function uuidSequence(): () => string {
  let sequence = 0;
  return () =>
    `90000000-0000-4000-8000-${String(++sequence).padStart(12, "0")}`;
}

function installmentDetailsPayload(input: Readonly<{
  installmentGuid: string;
  paidAmount: number;
  balanceAmount: number;
  payments: readonly ReturnType<typeof installmentPaymentPayload>[];
}>) {
  return {
    installmentGuid: input.installmentGuid,
    installmentNumber: "INS-CASH-100",
    storeCode: "S001",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Cashier",
    customerName: "Cash Customer",
    customerPhone: "0400000000",
    createdAt: "2026-07-27T01:02:03Z",
    updatedAt: "2026-07-28T00:00:00Z",
    totalAmount: 100,
    minimumDownPayment: 20,
    downPaymentAmount: 20,
    paidAmount: input.paidAmount,
    balanceAmount: input.balanceAmount,
    status: 1,
    lines: [],
    payments: input.payments,
    pickupInfo: null,
    cancellationInfo: null,
    note: null,
  };
}

function installmentPaymentPayload(paymentGuid: string) {
  return {
    paymentGuid,
    method: 1,
    amount: 10,
    reference: null,
    status: 1,
    recordedAt: "2026-07-28T00:00:00Z",
    cashierId: "cashier-1",
    deviceCode: "IPAD-1",
    idempotencyKey: paymentGuid,
    cardTransactions: [],
  };
}

function repaymentClaimPayload(input: Readonly<{
  installmentGuid: string;
  operationGuid: string;
  paymentGuid: string;
  providerAttemptId: string;
  status: 2 | 3;
  commit: unknown;
}>) {
  return {
    installmentGuid: input.installmentGuid,
    operationGuid: input.operationGuid,
    paymentGuid: input.paymentGuid,
    amount: 10,
    method: 1,
    idempotencyKey: input.operationGuid,
    status: input.status,
    provider: "cash",
    providerAttemptId: input.providerAttemptId,
    createdAtUtc: "2026-07-28T00:00:00Z",
    updatedAtUtc: "2026-07-28T00:00:00Z",
    expiresAtUtc: null,
    commit: input.commit,
    alreadyExists: false,
  };
}

function lastCashOrder(): LocalOrder {
  return {
    orderGuid: "receipt-order-1",
    localSequence: 7,
    storeCode: "S001",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Cashier",
    soldAtIso: "2026-07-28T00:00:00.000Z",
    state: "PendingSync",
    total: { currency: "AUD", cents: 100 },
    discount: { currency: "AUD", cents: 0 },
    actualAmount: { currency: "AUD", cents: 100 },
    lines: [{
      lineId: "receipt-line-1",
      productCode: "P-1",
      itemNumber: "I-1",
      lookupCode: "930000000001",
      displayName: "Milk",
      quantity: "1",
      unitPrice: { currency: "AUD", cents: 100 },
      discount: { currency: "AUD", cents: 0 },
      actualAmount: { currency: "AUD", cents: 100 },
      priceSource: "catalog",
      kind: "sale",
      returnSourceKey: null,
      originalOrderGuid: null,
      originalOrderDetailGuid: null,
    }],
    tenders: [{
      tenderGuid: "receipt-tender-1",
      method: "cash",
      amount: { currency: "AUD", cents: 100 },
      reference: null,
      reservationToken: null,
    }],
    originalOrderGuid: null,
  };
}

function pendingSyncHistoryOrder(): LocalSyncHistoryOrder {
  return {
    orderGuid: "pending-sync-order-1",
    localSequence: 8,
    storeCode: "S001",
    deviceCode: "IPAD-1",
    soldAtIso: "2026-07-28T00:00:00.000Z",
    state: "PendingSync",
    totalCents: 100,
    discountCents: 0,
    actualAmountCents: 100,
    tenders: [{ method: "cash", amountCents: 100 }],
    outbox: {
      state: "pending",
      attemptCount: 1,
      lastErrorCode: "NETWORK",
      nextAttemptAtIso: "2026-07-28T00:01:00.000Z",
    },
  };
}

function encryptor(): SensitivePayloadEncryptor {
  return {
    async encrypt(plaintext: string) {
      return new TextEncoder().encode(plaintext);
    },
    async decrypt(ciphertext: Uint8Array) {
      return new TextDecoder().decode(ciphertext);
    },
  } as SensitivePayloadEncryptor;
}
