import assert from "node:assert/strict";
import test from "node:test";

import { UpdateTransitionLeaseCoordinator } from "../../features/app-updates/update-transition-lease-coordinator";

import type {
  SettingsControlPort,
  SettingsLinklyHealthSnapshot,
  SettingsSnapshot,
} from "../../features/settings/settings-presenter";

import { createProductionSettingsRuntime } from "./production-settings-runtime";

const DEVICE_ACTIVATION_CODE = `HBDEV1-${"A".repeat(26)}-${"B".repeat(26)}`;
const SNAPSHOT: SettingsSnapshot = {
  apiBaseUrl: "https://pos.example.test/api",
  appUpdate: {
    channel: "preview",
    currentVersion: "1.0.0",
    availableVersion: null,
    updateRequired: false,
    restartAvailable: false,
  },
  catalog: {
    snapshotId: "catalog-1",
    itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  },
  device: {
    deviceCode: "IPAD-1",
    storeCode: "S1",
    storeName: "Store One",
    terminalName: "Front",
  },
  hardware: {
    printerStatus: "connected",
    scannerStatus: "ready",
    lastScannerValue: null,
  },
  linkly: {
    available: true,
    blockerCode: null,
    environment: "Production",
  },
  paymentProvider: "square",
  printer: {
    printEnabled: true,
    drawerEnabled: true,
    peripheralId: "XP-1",
    paper: "80mm",
    locale: "en",
    brandName: "",
    storeName: "",
    address: "",
    phone: "",
    abn: "",
    returnPolicy: "",
    voucherTerms: "",
    installmentTerms: "",
    profileStoreCode: "S1",
    profileVersion: 0,
    profileAckedVersion: 0,
  },
  square: {
    available: true,
    blockerCode: null,
    environment: "Sandbox",
    deviceId: "SQ-1",
    locationId: "LOC-1",
  },
};

test("零参数工厂冻结可信权限，并在每次端口调用前后复核 cashier lease", async () => {
  let epoch = 1;
  const events: string[] = [];
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push(`lease:${epoch}`);
        if (epoch !== 1) throw new Error("SESSION_REPLACED");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: ["Permissions.PosTerminal.Settings.View"],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => {
        events.push("load");
        return SNAPSHOT;
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();

  await presenter.load();
  assert.equal(presenter.getState().kind, "ready");
  assert.deepEqual(events, [
    "lease:1",
    "lease:1",
    "lease:1",
    "load",
    "lease:1",
  ]);

  epoch = 2;
  await presenter.load();
  assert.equal(presenter.getState().kind, "failed");
});

test("生产 settings wrapper 透传手动刷卡保存且不触发 reload", async () => {
  let saved: unknown = null;
  let reloadCalls = 0;
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => ({
        storeCode: "S1",
        deviceCode: "IPAD-1",
        permissionCodes: [
          "Permissions.PosTerminal.Settings.View",
          "Permissions.PosTerminal.Settings.PaymentTerminal",
        ],
      }),
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      savePaymentMethodSettings: async (settings, _signal, assertActive) => {
        assertActive?.();
        saved = settings;
        return { status: "completed", kind: "change-payment-settings" };
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  presenter.setUseManualCard(true);
  await presenter.saveManualCardSettings();
  assert.deepEqual(saved, { useManualCard: true });
  assert.equal(presenter.getState().statusCode, "payment-settings-saved");
  assert.equal(reloadCalls, 0);
});

test("生产 settings wrapper 在保存提交点前复核 session", async () => {
  let active = true;
  let assertActive: (() => void) | undefined;
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        if (!active) throw new Error("SESSION_REPLACED");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.PaymentTerminal",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      savePaymentMethodSettings: async (_settings, _signal, callback) => {
        assertActive = callback;
        await Promise.resolve();
        active = false;
        callback?.();
        return { status: "completed", kind: "change-payment-settings" };
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  presenter.setUseManualCard(true);
  await presenter.saveManualCardSettings();
  assert.equal(typeof assertActive, "function");
  assert.equal(presenter.getState().statusCode, "payment-settings-save-failed");
});

test("门店小票资料读取经可信 session 前后复核并原样进入 presenter 草稿", async () => {
  const events: string[] = [];
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.ReceiptPrinter",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      loadReceiptProfile: async () => {
        events.push("profile");
        return {
          storeCode: "S1",
          brandName: "Hot Bargain",
          storeName: "Store One",
          address: "1 Queen St",
          phone: "07 3000 0000",
          abn: "12 345 678 901",
          returnPolicy: "Refunds within 14 days.",
          voucherTerms: "",
          installmentTerms: "",
        };
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  events.length = 0;

  await presenter.loadReceiptProfile();

  assert.equal(presenter.getState().statusCode, "receipt-profile-loaded");
  assert.equal(presenter.getState().printer.brandName, "Hot Bargain");
  assert.deepEqual(events, ["lease", "profile", "lease"]);
});

test("可选 Square setup 端口原样转发 signal，并在每个异步调用前后复核 lease", async () => {
  const events: string[] = [];
  const receivedSignals: AbortSignal[] = [];
  const unavailable = async (): Promise<never> => {
    throw new Error("not implemented");
  };
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.PaymentTerminal",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      squareSetup: {
        getSquareTokenStatus: async (environment, signal) => {
          events.push(`token:${environment}`);
          receivedSignals.push(signal);
          return {
            environment,
            configured: true,
            enabled: true,
            updatedAt: null,
          };
        },
        listSquareLocations: async (environment, signal) => {
          events.push(`locations:${environment}`);
          receivedSignals.push(signal);
          return [];
        },
        listSquareDevices: unavailable,
        listSquareDeviceCodes: unavailable,
        createSquareDeviceCode: unavailable,
        getSquareDeviceCode: unavailable,
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  events.length = 0;

  await presenter.loadSquareLocations();

  assert.equal(events.filter((event) => event === "lease").length, 4);
  assert.equal(events.includes("token:Sandbox"), true);
  assert.equal(events.includes("locations:Sandbox"), true);
  assert.equal(receivedSignals.length, 2);
  assert.equal(receivedSignals[0], receivedSignals[1]);
  assert.equal(receivedSignals[0]?.aborted, false);
});

test("Square 配对码 POST 成功后 cashier lease 变化仍保留已创建结果且不重放", async () => {
  let sessionActive = true;
  let createCalls = 0;
  const unavailable = async (): Promise<never> => {
    throw new Error("not implemented");
  };
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        if (!sessionActive) throw new Error("SESSION_REPLACED");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.PaymentTerminal",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => ({
        ...SNAPSHOT,
        square: {
          ...SNAPSHOT.square,
          environment: "Production",
        },
      }),
      squareSetup: {
        getSquareTokenStatus: async (environment) => ({
          environment,
          configured: true,
          enabled: true,
          updatedAt: null,
        }),
        listSquareLocations: async () => [
          {
            id: "LOC-1",
            name: "Brisbane",
            status: "ACTIVE",
            currency: "AUD",
            country: "AU",
          },
        ],
        listSquareDevices: unavailable,
        listSquareDeviceCodes: async () => [],
        createSquareDeviceCode: async () => {
          createCalls += 1;
          sessionActive = false;
          return {
            id: "DC-1",
            code: "PAIR-1",
            status: "UNPAIRED",
            deviceId: null,
            locationId: "LOC-1",
            name: "Front register",
          };
        },
        getSquareDeviceCode: unavailable,
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  await presenter.loadSquareLocations();
  await presenter.loadSquareDeviceCodes();
  presenter.setSquareDeviceCodeNameDraft("Front register");

  await presenter.createSquareDeviceCode();

  assert.equal(createCalls, 1);
  assert.equal(
    presenter.getState().squareSetup.selectedDeviceCodeId,
    "DC-1",
  );
  assert.equal(presenter.getState().squareSetup.deviceCodes.items[0]?.id, "DC-1");
});

test("Square 配对码 create 调用前 cashier lease 已失效时底层保持零调用", async () => {
  let sessionActive = true;
  let createCalls = 0;
  const unavailable = async (): Promise<never> => {
    throw new Error("not implemented");
  };
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        if (!sessionActive) throw new Error("SESSION_REPLACED");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.PaymentTerminal",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => ({
        ...SNAPSHOT,
        square: {
          ...SNAPSHOT.square,
          environment: "Production",
        },
      }),
      squareSetup: {
        getSquareTokenStatus: async (environment) => ({
          environment,
          configured: true,
          enabled: true,
          updatedAt: null,
        }),
        listSquareLocations: async () => [
          {
            id: "LOC-1",
            name: "Brisbane",
            status: "ACTIVE",
            currency: "AUD",
            country: "AU",
          },
        ],
        listSquareDevices: unavailable,
        listSquareDeviceCodes: async () => [],
        createSquareDeviceCode: async () => {
          createCalls += 1;
          return {
            id: "DC-1",
            code: "PAIR-1",
            status: "UNPAIRED",
            deviceId: null,
            locationId: "LOC-1",
            name: "Front register",
          };
        },
        getSquareDeviceCode: unavailable,
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  await presenter.loadSquareLocations();
  await presenter.loadSquareDeviceCodes();
  presenter.setSquareDeviceCodeNameDraft("Front register");
  sessionActive = false;

  await presenter.createSquareDeviceCode();

  assert.equal(createCalls, 0);
});

test("危险动作只能在组合根独占区执行，并在动作完成后再次复核 lease", async () => {
  const events: string[] = [];
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.CatalogReset",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      executeDangerousAction: async () => {
        events.push("danger");
        return {
          status: "completed",
          kind: "reset-catalog",
          catalog: SNAPSHOT.catalog,
        };
      },
    }),
    runDangerousExclusive: async (operation) => {
      events.push("exclusive:start");
      const result = await operation();
      events.push("exclusive:end");
      return result;
    },
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  events.length = 0;

  assert.equal(presenter.requestCatalogReset(), true);
  await presenter.confirmDangerousAction();
  assert.deepEqual(events, [
    "lease",
    "exclusive:start",
    "danger",
    "lease",
    "exclusive:end",
  ]);
});

test("更换分店只读预检复核 lease 且不进入最终危险独占区", async () => {
  const events: string[] = [];
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.DeviceRegistration",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      previewDeviceActivationCode: async () => ({
        isAllowed: true,
        storeCode: "S2",
        storeName: "Store Two",
        deviceSystem: "Android",
        expiresAtUtc: "2026-08-28T00:00:00.000Z",
      }),
      preflightDeviceReregistration: async () => {
        events.push("preflight");
        return { status: "ready" };
      },
    }),
    runDangerousExclusive: async () => {
      events.push("exclusive:must-not-run");
      throw new Error("preflight must remain read-only");
    },
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  presenter.setDeviceActivationCode(DEVICE_ACTIVATION_CODE);
  await presenter.previewDeviceReregistration();
  events.length = 0;

  await presenter.requestDeviceReregistration();

  assert.deepEqual(events, ["lease", "preflight", "lease"]);
  assert.equal(presenter.getState().confirmation?.kind, "reregister-device");
});

test("更换分店最终确认通过全局 transition 等待在途业务并拒绝新业务", async () => {
  let sessionActive = true;
  const events: string[] = [];
  const transition = new UpdateTransitionLeaseCoordinator();
  const operationRelease = deferred<void>();
  transition.bindTransitionBarrier((operation) => operation());
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        if (!sessionActive) throw new Error("SESSION_REPLACED");
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.DeviceRegistration",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      previewDeviceActivationCode: async () => ({
        isAllowed: true,
        storeCode: "S2",
        storeName: "Store Two",
        deviceSystem: "Android",
        expiresAtUtc: "2026-08-28T00:00:00.000Z",
      }),
      preflightDeviceReregistration: async () => ({ status: "ready" }),
      executeDangerousAction: async (action) => {
        assert.equal(action.kind, "reregister-device");
        events.push("reregister");
        // 成功重绑会同步废弃旧设备范围，不能在不可逆提交后再用旧 lease 改写为失败。
        sessionActive = false;
        return { status: "completed", kind: "reregister-device" };
      },
    }),
    runDangerousExclusive: async () => {
      throw new Error("device reregistration must not pre-acquire cart lease");
    },
    runDeviceRegistrationResetTransition: (operation) =>
      transition.runTransition(operation),
  } as Parameters<typeof createProductionSettingsRuntime>[0] & {
    runDeviceRegistrationResetTransition<T>(
      operation: () => Promise<T>,
    ): Promise<T>;
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  presenter.setDeviceActivationCode(DEVICE_ACTIVATION_CODE);
  await presenter.previewDeviceReregistration();
  await presenter.requestDeviceReregistration();
  events.length = 0;
  const existing = transition.runOperation(async () => {
    events.push("operation:started");
    await operationRelease.promise;
    events.push("operation:finished");
  });

  const reregister = presenter.confirmDangerousAction();

  assert.equal(transition.isTransitionActive(), true);
  await assert.rejects(transition.runOperation(async () => undefined));
  assert.deepEqual(events, ["operation:started", "lease"]);

  operationRelease.resolve();
  await existing;
  await reregister;

  assert.deepEqual(events, [
    "operation:started",
    "lease",
    "operation:finished",
    "reregister",
  ]);
  assert.equal(presenter.getState().statusCode, "device-reregister-started");
});

test("清除设备注册通过全局 transition 等待在途 operation 并拒绝新 operation", async () => {
  let sessionActive = true;
  const events: string[] = [];
  const transition = new UpdateTransitionLeaseCoordinator();
  const operationRelease = deferred<void>();
  transition.bindTransitionBarrier((operation) => operation());
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        if (!sessionActive) throw new Error("SESSION_REPLACED");
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.DeviceRegistration",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      executeDangerousAction: async (action, _signal, employeeBarcode) => {
        assert.equal(action.kind, "reset-device-registration");
        assert.equal(employeeBarcode, "EMPLOYEE-BARCODE");
        events.push("reset");
        sessionActive = false;
        return {
          status: "completed",
          kind: "reset-device-registration",
        };
      },
    }),
    runDangerousExclusive: async () => {
      throw new Error("device registration reset must not pre-acquire cart lease");
    },
    runDeviceRegistrationResetTransition: (operation) =>
      transition.runTransition(operation),
  } as Parameters<typeof createProductionSettingsRuntime>[0] & {
    runDeviceRegistrationResetTransition<T>(
      operation: () => Promise<T>,
    ): Promise<T>;
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  events.length = 0;
  const existing = transition.runOperation(async () => {
    events.push("operation:started");
    await operationRelease.promise;
    events.push("operation:finished");
  });

  assert.equal(presenter.requestDeviceRegistrationReset(), true);
  const reset = presenter.confirmDangerousAction("EMPLOYEE-BARCODE");

  assert.equal(transition.isTransitionActive(), true);
  await assert.rejects(transition.runOperation(async () => undefined));
  assert.deepEqual(events, ["operation:started", "lease"]);

  operationRelease.resolve();
  await existing;
  await reset;

  assert.deepEqual(events, [
    "operation:started",
    "lease",
    "operation:finished",
    "reset",
  ]);
});

test("App 重启跳过普通购物车独占，但动作完成后仍复核可信 cashier lease", async () => {
  const events: string[] = [];
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.AppUpdate",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      executeDangerousAction: async (action) => {
        assert.equal(action.kind, "restart-app");
        events.push("danger");
        return { status: "completed", kind: "restart-app" };
      },
    }),
    runDangerousExclusive: async () => {
      events.push("exclusive:must-not-run");
      throw new Error("restart must not acquire the ordinary cart lease");
    },
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  events.length = 0;

  assert.equal(presenter.requestAppRestart(), true);
  await presenter.confirmDangerousAction();
  assert.deepEqual(events, ["lease", "danger", "lease"]);
});

test("支付配置切换由全局 transition 取得锁，不预取普通购物车独占", async () => {
  const events: string[] = [];
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.PaymentTerminal",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => ({
        ...SNAPSHOT,
        paymentProvider: "linkly",
      }),
      executeDangerousAction: async (action) => {
        assert.equal(action.kind, "change-payment-settings");
        events.push("transition");
        return { status: "completed", kind: "change-payment-settings" };
      },
    }),
    runDangerousExclusive: async () => {
      events.push("exclusive:must-not-run");
      throw new Error("payment transition must not pre-acquire cart lease");
    },
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  events.length = 0;

  presenter.setLinklyEnvironment("Sandbox");
  await presenter.savePaymentSettings();
  await presenter.confirmDangerousAction();

  assert.deepEqual(events, ["lease", "transition", "lease"]);
});

test("Linkly 配对走 transition bypass，health 读取走 lease，POST 后不做 lease 改写", async () => {
  const events: string[] = [];
  const health: SettingsLinklyHealthSnapshot = {
    environment: "Production",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    isReady: true,
    checks: [
      { code: "STORE_CREDENTIAL", isReady: true, message: "ready" },
      { code: "TERMINAL_SECRET", isReady: true, message: "ready" },
      { code: "TERMINAL_POS_ID", isReady: true, message: "ready" },
    ],
  };
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.PaymentTerminal",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      linklySetup: {
        readState: async () => {
          events.push("health");
          return health;
        },
      },
      executeDangerousAction: async (action) => {
        assert.equal(action.kind, "pair-linkly");
        events.push("pair");
        return { status: "completed", kind: "pair-linkly" };
      },
    }),
    runDangerousExclusive: async () => {
      throw new Error("pair-linkly must not pre-acquire ordinary cart lease");
    },
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  events.length = 0;

  assert.equal(
    presenter.requestLinklyPair("terminal-1", "123456"),
    true,
  );
  await presenter.confirmDangerousAction();

  assert.equal(presenter.getState().statusCode, "linkly-paired");
  assert.deepEqual(events, ["lease", "pair", "lease", "health", "lease"]);
});

test("可选钱箱与清除能力在不可撤销提交点前复核可信 session", async () => {
  const events: string[] = [];
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        events.push("lease");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.ReceiptPrinter",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      savePrinterSettings: async () => {
        events.push("save-printer");
      },
      testCashDrawer: async () => {
        events.push("test-cash-drawer");
        return { status: "completed", errorCode: null };
      },
      clearSavedPrinter: async () => {
        events.push("clear-saved-printer");
        return { status: "completed", errorCode: null };
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  events.length = 0;

  await presenter.testCashDrawer();
  assert.deepEqual(events, [
    "lease",
    "save-printer",
    "lease",
    "lease",
    "test-cash-drawer",
  ]);

  events.length = 0;
  await presenter.clearSavedPrinter();
  assert.deepEqual(events, [
    "lease",
    "clear-saved-printer",
  ]);
});

test("钱箱动作返回完成或未知后 session 变化仍保留不可撤销终态", async () => {
  for (const expected of [
    {
      result: { status: "completed", errorCode: null } as const,
      statusCode: "cash-drawer-test-passed",
    },
    {
      result: {
        status: "unknown",
        errorCode: "DRAWER_OUTCOME_UNKNOWN",
      } as const,
      statusCode: "cash-drawer-test-unknown",
    },
  ] as const) {
    let sessionActive = true;
    let enterAction!: () => void;
    let finishAction!: () => void;
    const actionEntered = new Promise<void>((resolve) => {
      enterAction = resolve;
    });
    const actionFinished = new Promise<void>((resolve) => {
      finishAction = resolve;
    });
    const runtime = createProductionSettingsRuntime({
      createSessionLease: () => ({
        get: () => {
          if (!sessionActive) throw new Error("SESSION_REPLACED");
          return {
            storeCode: "S1",
            deviceCode: "IPAD-1",
            permissionCodes: [
              "Permissions.PosTerminal.Settings.View",
              "Permissions.PosTerminal.Settings.ReceiptPrinter",
            ],
          };
        },
      }),
      control: fakeControl({
        loadSnapshot: async () => SNAPSHOT,
        savePrinterSettings: async () => undefined,
        testCashDrawer: async () => {
          enterAction();
          await actionFinished;
          return expected.result;
        },
      }),
      runDangerousExclusive: (operation) => operation(),
    });
    const presenter = runtime.createPresenter();
    await presenter.load();

    const action = presenter.testCashDrawer();
    await actionEntered;
    sessionActive = false;
    finishAction();
    await action;

    assert.equal(presenter.getState().statusCode, expected.statusCode);
  }
});

test("session 在调用前失效时禁止钱箱与清除打印机动作", async () => {
  let sessionActive = true;
  let saveCalls = 0;
  let drawerCalls = 0;
  let clearCalls = 0;
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        if (!sessionActive) throw new Error("SESSION_REPLACED");
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.ReceiptPrinter",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      savePrinterSettings: async () => {
        saveCalls += 1;
      },
      testCashDrawer: async () => {
        drawerCalls += 1;
        return { status: "completed", errorCode: null };
      },
      clearSavedPrinter: async () => {
        clearCalls += 1;
        return { status: "completed", errorCode: null };
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  sessionActive = false;

  await presenter.testCashDrawer();
  await presenter.clearSavedPrinter();

  assert.equal(saveCalls, 0);
  assert.equal(drawerCalls, 0);
  assert.equal(clearCalls, 0);
});

test("清除打印机把保存前 session 复核回调转发给组合层且不做后置复核", async () => {
  let sessionChecks = 0;
  const runtime = createProductionSettingsRuntime({
    createSessionLease: () => ({
      get: () => {
        sessionChecks += 1;
        return {
          storeCode: "S1",
          deviceCode: "IPAD-1",
          permissionCodes: [
            "Permissions.PosTerminal.Settings.View",
            "Permissions.PosTerminal.Settings.ReceiptPrinter",
          ],
        };
      },
    }),
    control: fakeControl({
      loadSnapshot: async () => SNAPSHOT,
      clearSavedPrinter: async (_signal, assertActive?: () => void) => {
        assert.equal(typeof assertActive, "function");
        assertActive?.();
        return { status: "completed", errorCode: null };
      },
    }),
    runDangerousExclusive: (operation) => operation(),
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  sessionChecks = 0;

  await presenter.clearSavedPrinter();

  assert.equal(presenter.getState().statusCode, "printer-cleared");
  assert.equal(sessionChecks, 2);
});

function fakeControl(
  overrides: Partial<SettingsControlPort>,
): SettingsControlPort {
  const unavailable = async (): Promise<never> => {
    throw new Error("not implemented");
  };
  return {
    getCatalogRefreshState: () => ({ kind: "idle" }),
    subscribeCatalogRefresh: () => () => undefined,
    subscribeDeviceReregistrationCommitted: () => () => undefined,
    loadSnapshot: unavailable,
    savePaymentMethodSettings: unavailable,
    downloadCatalog: unavailable,
    testApiAddress: unavailable,
    preflightDeviceReregistration: unavailable,
    testPaymentProvider: unavailable,
    savePrinterSettings: unavailable,
    loadReceiptProfile: unavailable,
    scanPrinters: unavailable,
    connectPrinter: unavailable,
    testPrinter: unavailable,
    testScanner: unavailable,
    checkForAppUpdate: unavailable,
    executeDangerousAction: unavailable,
    ...overrides,
  };
}

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
