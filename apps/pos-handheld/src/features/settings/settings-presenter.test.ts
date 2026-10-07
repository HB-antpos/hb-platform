import assert from "node:assert/strict";
import test from "node:test";

import { derivePendingWorkBlockers } from "@hb/pos-domain";

const deviceActivationCode =
  "HBDEV1-0123456789ABCDEFGHJKMNPQRS-STVWXYZ0123456789ABCDEFGHJ";

import {
  SETTINGS_APP_UPDATE_PERMISSION,
  SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
  SETTINGS_CATALOG_RESET_PERMISSION,
  SETTINGS_DEVICE_REGISTRATION_PERMISSION,
  SETTINGS_PAYMENT_TERMINAL_PERMISSION,
  SETTINGS_RECEIPT_PRINTER_PERMISSION,
  SETTINGS_VIEW_PERMISSION,
} from "./settings-authorization";
import {
  SettingsPresenter,
  type SettingsCashDrawerTestResult,
  type SettingsClearSavedPrinterResult,
  type SettingsControlPort,
  type SettingsDangerousActionResult,
  type SettingsDangerousConfirmation,
  type SettingsLinklyHealthSnapshot,
  type SettingsLinklyPairingPort,
  type SettingsLinklyPairResult,
  type SettingsLinklySetupControlPort,
  type SettingsLinklyTerminalSelectionSnapshot,
  type SettingsPaymentSettingsInput,
  type SettingsPendingDataSnapshot,
  type SettingsReceiptProfileDraft,
  type SettingsReceiptProfileSyncResult,
  type SettingsSnapshot,
} from "./settings-presenter";

import {
  DEFAULT_RECEIPT_PRINTER_SETTINGS,
  type ReceiptPrinterSettings,
} from "@/core/db/pos-settings-repository";
import type { CatalogRefreshState } from "@/features/catalog/catalog-refresh-coordinator";
import {
  SETTINGS_SQUARE_SANDBOX_CHECKOUT_DEVICES,
  type SettingsSquareDevice,
  type SettingsSquareDeviceCode,
  type SettingsSquareEnvironment,
  type SettingsSquareLocation,
  type SettingsSquareTokenStatus,
} from "@hb/pos-domain/features/settings/settings-square-setup";

const allPermissions = [
  SETTINGS_VIEW_PERMISSION,
  SETTINGS_PAYMENT_TERMINAL_PERMISSION,
  SETTINGS_RECEIPT_PRINTER_PERMISSION,
  SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
  SETTINGS_CATALOG_RESET_PERMISSION,
  SETTINGS_DEVICE_REGISTRATION_PERMISSION,
  SETTINGS_APP_UPDATE_PERMISSION,
] as const;

test("无 View 权限时 fail closed 且不读取任何运行时设置", async () => {
  const port = new FakeSettingsPort();
  const presenter = new SettingsPresenter({ permissions: [], port });

  await presenter.load();

  assert.equal(port.loadCalls, 0);
  assert.equal(presenter.getState().kind, "unauthorized");
  assert.equal(presenter.getState().statusCode, "permission-required");
});

test("无支付配置权限时不读取 Linkly health，也不执行设置动作", async () => {
  const port = new FakeSettingsPort();
  const setup = new FakeLinklySetupControlPort();
  const pairing = new FakeLinklyPairingPort();
  port.linklySetup = setup;
  port.linklyPairing = pairing;
  const presenter = new SettingsPresenter({
    permissions: [SETTINGS_VIEW_PERMISSION],
    port,
  });

  await presenter.load();
  await presenter.refreshLinklySetup();
  assert.equal(presenter.requestLinklyPair("123456"), false);
  await presenter.testPaymentProvider("linkly");

  assert.equal(presenter.getState().kind, "ready");
  assert.deepEqual(setup.readEnvironments, []);
  assert.deepEqual(pairing.pairCalls, []);
});

test("加载公开配置但不包含 Square/Linkly 密钥字段", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);

  await presenter.load();

  assert.equal(port.loadCalls, 1);
  assert.equal(presenter.getState().kind, "ready");
  assert.equal(
    presenter.getState().apiAddressDraft,
    "https://hotbargain.vip/pos-api",
  );
  assert.deepEqual(presenter.getState().squareDraft, {
    environment: "Production",
    deviceId: "sq-device-1",
    locationId: "sq-location-1",
  });
  assert.deepEqual(presenter.getState().linklyDraft, {
    environment: "Production",
  });
  assert.equal(presenter.getState().paymentProvider, "square");
  assert.equal(presenter.getState().paymentProviderDraft, "square");
  assert.equal(
    "accessToken" in (presenter.getState().squareDraft as object),
    false,
  );
  assert.equal("secret" in (presenter.getState().linklyDraft as object), false);
});

test("本机 Square 尚未绑定时仍可从 POS API 加载 token 与 locations", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  const current = snapshot();
  port.snapshotValue = {
    ...current,
    paymentProvider: null,
    square: {
      available: false,
      blockerCode: "square-not-configured",
      environment: "Production",
      deviceId: "",
      locationId: "",
    },
  };
  squareSetup.tokenStatus = {
    environment: "Production",
    configured: true,
    enabled: true,
    updatedAt: "2026-08-01T00:00:00.000Z",
  };
  squareSetup.locations = [
    {
      id: "location-production",
      name: "Brisbane",
      status: "ACTIVE",
      currency: "AUD",
      country: "AU",
    },
  ];
  const presenter = createPresenter(port);

  await presenter.load();
  await (
    presenter as unknown as { loadSquareLocations(): Promise<void> }
  ).loadSquareLocations();

  const setupState = (
    presenter.getState() as unknown as {
      squareSetup: {
        token: { kind: string; value: SettingsSquareTokenStatus | null };
        locations: { kind: string; items: readonly SettingsSquareLocation[] };
      };
    }
  ).squareSetup;
  assert.deepEqual(squareSetup.tokenCalls, ["Production"]);
  assert.deepEqual(squareSetup.locationCalls, ["Production"]);
  assert.equal(setupState.token.kind, "ready");
  assert.equal(setupState.token.value?.enabled, true);
  assert.equal(setupState.locations.kind, "ready");
  assert.deepEqual(setupState.locations.items, squareSetup.locations);

  presenter.setPaymentProvider("square");
  assert.equal(presenter.getState().paymentProviderDraft, "square");
  await presenter.savePaymentSettings();
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "payment-settings-invalid");
});

test("Square 远程选项重匹配已保存值，environment/location 变更级联清空", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  squareSetup.locations = [
    squareLocation("SQ-LOCATION-1", "Brisbane"),
    squareLocation("sq-location-2", "Gold Coast"),
  ];
  squareSetup.devices = [
    squareDevice("device:sq-device-1", "SQ-LOCATION-1", "Front"),
  ];
  squareSetup.deviceCodes = [
    squareDeviceCode(
      "device-code-1",
      "SQ-DEVICE-1",
      "SQ-LOCATION-1",
      "Front",
    ),
  ];
  const presenter = createPresenter(port);
  await presenter.load();

  await presenter.loadSquareLocations();
  assert.equal(
    presenter.getState().squareSetup.selectedLocationId,
    "SQ-LOCATION-1",
  );
  assert.equal(presenter.getState().squareDraft.locationId, "SQ-LOCATION-1");

  await presenter.loadSquareDevices();
  assert.deepEqual(
    squareSetup.deviceCalls.map(({ environment, locationId }) => ({
      environment,
      locationId,
    })),
    [{ environment: "Production", locationId: "SQ-LOCATION-1" }],
  );
  assert.equal(
    presenter.getState().squareSetup.selectedDeviceId,
    "sq-device-1",
  );
  assert.equal(presenter.getState().squareDraft.deviceId, "sq-device-1");

  await presenter.loadSquareDeviceCodes();
  assert.deepEqual(
    squareSetup.deviceCodeCalls.map(({ environment, locationId }) => ({
      environment,
      locationId,
    })),
    [{ environment: "Production", locationId: "SQ-LOCATION-1" }],
  );
  assert.equal(
    presenter.getState().squareSetup.selectedDeviceCodeId,
    "device-code-1",
  );

  presenter.setSquareLocationId("sq-location-2");
  assert.equal(presenter.getState().squareSetup.selectedDeviceId, "");
  assert.equal(presenter.getState().squareDraft.deviceId, "");
  assert.equal(presenter.getState().squareSetup.devices.kind, "idle");
  assert.equal(presenter.getState().squareSetup.deviceCodes.kind, "idle");

  presenter.setSquareEnvironment("Sandbox");
  assert.equal(presenter.getState().squareDraft.locationId, "");
  assert.equal(presenter.getState().squareSetup.selectedLocationId, "");
  assert.equal(presenter.getState().squareSetup.locations.kind, "idle");
  assert.equal(presenter.getState().squareSetup.deviceCodes.kind, "disabled");
});

test("Sandbox 单 location 自动加载设备、合并官方测试设备并重匹配保存值", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  const savedTestDevice = SETTINGS_SQUARE_SANDBOX_CHECKOUT_DEVICES[0];
  const current = snapshot();
  port.snapshotValue = {
    ...current,
    square: {
      ...current.square,
      environment: "Sandbox",
      locationId: "saved-sandbox-location",
      deviceId: `device:${savedTestDevice.id}`,
    },
  };
  squareSetup.tokenStatus = {
    environment: "Sandbox",
    configured: true,
    enabled: true,
    updatedAt: null,
  };
  squareSetup.locations = [
    squareLocation("sandbox-location", "Sandbox Store"),
  ];
  squareSetup.devices = [
    squareDevice(
      `device:${savedTestDevice.id}`,
      "sandbox-location",
      "Duplicate server test device",
    ),
  ];
  const presenter = createPresenter(port);
  await presenter.load();

  await presenter.loadSquareLocations();

  assert.deepEqual(
    squareSetup.deviceCalls.map(({ environment, locationId }) => ({
      environment,
      locationId,
    })),
    [{ environment: "Sandbox", locationId: "sandbox-location" }],
  );
  assert.equal(
    presenter.getState().squareSetup.selectedLocationId,
    "sandbox-location",
  );
  assert.equal(
    presenter.getState().squareSetup.selectedDeviceId,
    savedTestDevice.id,
  );
  assert.equal(
    presenter.getState().squareSetup.devices.items.length,
    SETTINGS_SQUARE_SANDBOX_CHECKOUT_DEVICES.length,
  );
  assert.equal(presenter.getState().squareSetup.deviceCodes.kind, "disabled");
  assert.equal(squareSetup.deviceCodeCalls.length, 0);
});

test("Sandbox 首次配置自动选中成功测试终端并允许进入保存确认", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  const current = snapshot();
  port.snapshotValue = {
    ...current,
    paymentProvider: null,
    square: {
      ...current.square,
      available: false,
      blockerCode: "SQUARE_CONFIGURATION_MISSING",
      deviceId: "",
      locationId: "",
    },
  };
  squareSetup.tokenStatus = {
    environment: "Sandbox",
    configured: true,
    enabled: true,
    updatedAt: null,
  };
  squareSetup.locations = [
    squareLocation("sandbox-location", "Sandbox Store"),
  ];
  squareSetup.devices = [];
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setSquareEnvironment("Sandbox");
  await presenter.loadSquareLocations();

  assert.equal(
    presenter.getState().squareSetup.selectedDeviceId,
    SETTINGS_SQUARE_SANDBOX_CHECKOUT_DEVICES[0].id,
  );
  presenter.setPaymentProvider("square");
  await presenter.savePaymentSettings();
  assert.equal(
    presenter.getState().confirmation?.kind,
    "change-payment-settings",
  );
});

test("Square 丢弃旧 environment 响应，destroy abort 后不转成失败态", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  const productionLocations = deferred<readonly SettingsSquareLocation[]>();
  const sandboxLocations = deferred<readonly SettingsSquareLocation[]>();
  squareSetup.tokenHandler = async (environment) => ({
    environment,
    configured: true,
    enabled: true,
    updatedAt: null,
  });
  squareSetup.locationHandler = (environment) =>
    environment === "Production"
      ? productionLocations.promise
      : sandboxLocations.promise;
  const presenter = createPresenter(port);
  await presenter.load();

  const productionLoad = presenter.loadSquareLocations();
  await Promise.resolve();
  presenter.setSquareEnvironment("Sandbox");
  const sandboxLoad = presenter.loadSquareLocations();
  sandboxLocations.resolve([
    squareLocation("sandbox-current", "Current Sandbox"),
  ]);
  await sandboxLoad;
  productionLocations.resolve([
    squareLocation("production-late", "Late Production"),
  ]);
  await productionLoad;

  assert.deepEqual(
    presenter.getState().squareSetup.locations.items.map(({ id }) => id),
    ["sandbox-current"],
  );
  assert.equal(
    presenter.getState().squareSetup.token.value?.environment,
    "Sandbox",
  );

  const abortPort = new FakeSettingsPort();
  const abortSetup = new FakeSquareSetupControlPort();
  abortPort.squareSetup = abortSetup;
  let abortObserved = false;
  abortSetup.locationHandler = (_environment, signal) =>
    new Promise((_resolve, reject) => {
      signal.addEventListener(
        "abort",
        () => {
          abortObserved = true;
          reject(Object.assign(new Error("aborted"), { name: "AbortError" }));
        },
        { once: true },
      );
    });
  const abortPresenter = createPresenter(abortPort);
  await abortPresenter.load();
  const abortedLoad = abortPresenter.loadSquareLocations();
  await Promise.resolve();
  abortPresenter.destroy();
  await abortedLoad;

  assert.equal(abortObserved, true);
  assert.equal(abortPresenter.getState().squareSetup.locations.kind, "loading");
});

test("Square 仅允许保存当前 location 已加载且未禁用的 device", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  squareSetup.locations = [
    squareLocation("sq-location-1", "Brisbane"),
  ];
  squareSetup.devices = [
    squareDevice("device-disabled", "sq-location-1", "Disabled", "DISABLED"),
    squareDevice("device-enabled", "sq-location-1", "Enabled", "ACTIVE"),
  ];
  const presenter = createPresenter(port);
  await presenter.load();
  await presenter.loadSquareLocations();
  await presenter.loadSquareDevices();

  presenter.setSquareDeviceId("device-disabled");
  await presenter.savePaymentSettings();
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "payment-settings-invalid");

  presenter.setSquareDeviceId("device-outside-list");
  await presenter.savePaymentSettings();
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "payment-settings-invalid");

  presenter.setSquareDeviceId("device-enabled");
  await presenter.savePaymentSettings();
  assert.equal(
    presenter.getState().confirmation?.kind,
    "change-payment-settings",
  );
});

test("Production Device Code 创建后手动刷新；PAIRED 只重载并选中设备不自动保存", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  squareSetup.locations = [
    squareLocation("sq-location-1", "Brisbane"),
  ];
  squareSetup.devices = [
    squareDevice("sq-device-1", "sq-location-1", "Old device"),
  ];
  squareSetup.deviceCodes = [];
  squareSetup.createdDeviceCode = squareDeviceCode(
    "device-code-new",
    null,
    "sq-location-1",
    "Front register",
    "UNPAIRED",
  );
  const presenter = createPresenter(port);
  await presenter.load();
  await presenter.loadSquareLocations();
  await presenter.loadSquareDevices();
  await presenter.loadSquareDeviceCodes();

  assert.equal(
    presenter.getState().squareDeviceCodeNameDraft,
    "HBPOS Terminal",
  );
  presenter.setSquareDeviceCodeNameDraft(" Front register ");
  await presenter.createSquareDeviceCode();
  assert.deepEqual(
    squareSetup.createDeviceCodeCalls.map(
      ({ environment, locationId, name }) => ({
        environment,
        locationId,
        name,
      }),
    ),
    [
      {
        environment: "Production",
        locationId: "sq-location-1",
        name: "Front register",
      },
    ],
  );
  assert.equal(
    presenter.getState().squareSetup.selectedDeviceCodeId,
    "device-code-new",
  );

  squareSetup.devices = [
    squareDevice("sq-device-paired", "sq-location-1", "Paired device"),
  ];
  squareSetup.refreshedDeviceCode = squareDeviceCode(
    "device-code-new",
    "sq-device-paired",
    "sq-location-1",
    "Front register",
    "PAIRED",
  );
  await presenter.refreshSquareDeviceCode();

  assert.deepEqual(
    squareSetup.refreshDeviceCodeCalls.map(
      ({ environment, deviceCodeId }) => ({ environment, deviceCodeId }),
    ),
    [{ environment: "Production", deviceCodeId: "device-code-new" }],
  );
  assert.equal(
    presenter.getState().squareSetup.selectedDeviceId,
    "sq-device-paired",
  );
  assert.equal(presenter.getState().squareDraft.deviceId, "sq-device-paired");
  assert.equal(presenter.getState().confirmation, null);
  assert.deepEqual(port.savedPayments, []);

  presenter.setSquareEnvironment("Sandbox");
  await presenter.createSquareDeviceCode();
  await presenter.refreshSquareDeviceCode();
  assert.equal(squareSetup.createDeviceCodeCalls.length, 1);
  assert.equal(squareSetup.refreshDeviceCodeCalls.length, 1);
  assert.equal(presenter.getState().squareSetup.deviceCodes.kind, "disabled");
});

test("Device Code A 配对后的迟到设备响应不会覆盖已切换到 Code B 的 draft", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  squareSetup.locations = [
    squareLocation("sq-location-1", "Brisbane"),
  ];
  squareSetup.devices = [
    squareDevice("sq-device-current", "sq-location-1", "Current device"),
  ];
  squareSetup.deviceCodes = [
    squareDeviceCode(
      "device-code-a",
      "sq-device-current",
      "sq-location-1",
      "Code A",
      "UNPAIRED",
    ),
    squareDeviceCode(
      "device-code-b",
      null,
      "sq-location-1",
      "Code B",
      "UNPAIRED",
    ),
  ];
  squareSetup.refreshedDeviceCode = squareDeviceCode(
    "device-code-a",
    "sq-device-a",
    "sq-location-1",
    "Code A",
    "PAIRED",
  );
  const presenter = createPresenter(port);
  await presenter.load();
  await presenter.loadSquareLocations();
  await presenter.loadSquareDevices();
  await presenter.loadSquareDeviceCodes();
  presenter.setSquareDeviceCodeId("device-code-a");
  presenter.setSquareDeviceId("sq-device-current");

  const pairedDevices = deferred<readonly SettingsSquareDevice[]>();
  squareSetup.deviceHandler = async () => pairedDevices.promise;
  const refresh = presenter.refreshSquareDeviceCode();
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(squareSetup.deviceCalls.length, 2);

  presenter.setSquareDeviceCodeId("device-code-b");
  pairedDevices.resolve([
    squareDevice("sq-device-current", "sq-location-1", "Current device"),
    squareDevice("sq-device-a", "sq-location-1", "Paired A device"),
  ]);
  await refresh;

  assert.equal(
    presenter.getState().squareSetup.selectedDeviceCodeId,
    "device-code-b",
  );
  assert.deepEqual(
    presenter.getState().squareSetup.devices.items.map(({ id }) => id),
    ["sq-device-current", "sq-device-a"],
  );
  assert.equal(
    presenter.getState().squareSetup.selectedDeviceId,
    "sq-device-current",
  );
  assert.equal(presenter.getState().squareDraft.deviceId, "sq-device-current");
});

test("Device Code 创建 POST 在途时锁定作用域、合并重复调用并在完成后解锁", async () => {
  const port = new FakeSettingsPort();
  const squareSetup = new FakeSquareSetupControlPort();
  port.squareSetup = squareSetup;
  squareSetup.locations = [
    squareLocation("sq-location-1", "Brisbane"),
    squareLocation("sq-location-2", "Gold Coast"),
  ];
  const createdDeviceCode = deferred<SettingsSquareDeviceCode>();
  squareSetup.createDeviceCodeHandler = async () =>
    createdDeviceCode.promise;
  const presenter = createPresenter(port);
  await presenter.load();
  await presenter.loadSquareLocations();

  const firstCreate = presenter.createSquareDeviceCode();
  const duplicateCreate = presenter.createSquareDeviceCode();
  const callCountWhilePending = squareSetup.createDeviceCodeCalls.length;
  const busyWhilePending = presenter.getState().busy;
  presenter.setSquareEnvironment("Sandbox");
  presenter.setSquareLocationId("sq-location-2");
  presenter.setPaymentProvider("linkly");
  const stateWhilePending = presenter.getState();

  createdDeviceCode.resolve(
    squareDeviceCode(
      "device-code-created",
      null,
      "sq-location-1",
      "HBPOS Terminal",
      "UNPAIRED",
    ),
  );
  await Promise.all([firstCreate, duplicateCreate]);

  assert.equal(callCountWhilePending, 1);
  assert.equal(busyWhilePending, true);
  assert.equal(stateWhilePending.squareDraft.environment, "Production");
  assert.equal(stateWhilePending.squareDraft.locationId, "sq-location-1");
  assert.equal(stateWhilePending.paymentProviderDraft, "square");
  assert.equal(presenter.getState().busy, false);
  assert.equal(
    presenter.getState().squareSetup.selectedDeviceCodeId,
    "device-code-created",
  );

  presenter.setSquareLocationId("sq-location-2");
  presenter.setPaymentProvider("linkly");
  assert.equal(presenter.getState().squareDraft.locationId, "sq-location-2");
  assert.equal(presenter.getState().paymentProviderDraft, "linkly");
});

test("Device Code 列表或刷新在途时不插入创建请求", async () => {
  const listPort = new FakeSettingsPort();
  const listSetup = new FakeSquareSetupControlPort();
  listPort.squareSetup = listSetup;
  listSetup.locations = [squareLocation("sq-location-1", "Brisbane")];
  listSetup.createdDeviceCode = squareDeviceCode(
    "unexpected-create-from-list",
    null,
    "sq-location-1",
    "Unexpected",
    "UNPAIRED",
  );
  const listPresenter = createPresenter(listPort);
  await listPresenter.load();
  await listPresenter.loadSquareLocations();
  const listedCodes = deferred<readonly SettingsSquareDeviceCode[]>();
  listSetup.deviceCodeHandler = async () => listedCodes.promise;

  const listInFlight = listPresenter.loadSquareDeviceCodes();
  await Promise.resolve();
  await listPresenter.createSquareDeviceCode();
  listedCodes.resolve([]);
  await listInFlight;

  assert.equal(listSetup.createDeviceCodeCalls.length, 0);

  const refreshPort = new FakeSettingsPort();
  const refreshSetup = new FakeSquareSetupControlPort();
  refreshPort.squareSetup = refreshSetup;
  refreshSetup.locations = [squareLocation("sq-location-1", "Brisbane")];
  refreshSetup.deviceCodes = [
    squareDeviceCode(
      "device-code-refresh",
      "sq-device-1",
      "sq-location-1",
      "Refresh",
      "UNPAIRED",
    ),
  ];
  refreshSetup.createdDeviceCode = squareDeviceCode(
    "unexpected-create-from-refresh",
    null,
    "sq-location-1",
    "Unexpected",
    "UNPAIRED",
  );
  const refreshPresenter = createPresenter(refreshPort);
  await refreshPresenter.load();
  await refreshPresenter.loadSquareLocations();
  await refreshPresenter.loadSquareDeviceCodes();
  refreshPresenter.setSquareDeviceCodeId("device-code-refresh");
  const refreshedCode = deferred<SettingsSquareDeviceCode>();
  refreshSetup.refreshDeviceCodeHandler = async () => refreshedCode.promise;

  const refreshInFlight = refreshPresenter.refreshSquareDeviceCode();
  await Promise.resolve();
  await refreshPresenter.createSquareDeviceCode();
  refreshedCode.resolve(
    squareDeviceCode(
      "device-code-refresh",
      "sq-device-1",
      "sq-location-1",
      "Refresh",
      "UNPAIRED",
    ),
  );
  await refreshInFlight;

  assert.equal(refreshSetup.createDeviceCodeCalls.length, 0);
});

test("Square locations/devices 空列表或失败均不清除本机绑定", async () => {
  const emptyLocationPort = new FakeSettingsPort();
  const emptyLocationSetup = new FakeSquareSetupControlPort();
  emptyLocationPort.squareSetup = emptyLocationSetup;
  emptyLocationSetup.locations = [];
  const emptyLocationPresenter = createPresenter(emptyLocationPort);
  await emptyLocationPresenter.load();
  await emptyLocationPresenter.loadSquareLocations();

  assert.equal(
    emptyLocationPresenter.getState().square.locationId,
    "sq-location-1",
  );
  assert.equal(
    emptyLocationPresenter.getState().squareDraft.locationId,
    "sq-location-1",
  );
  assert.equal(
    emptyLocationPresenter.getState().squareSetup.locations.kind,
    "empty",
  );

  emptyLocationSetup.locationHandler = async () => {
    throw new Error("locations unavailable");
  };
  await emptyLocationPresenter.loadSquareLocations();
  assert.equal(
    emptyLocationPresenter.getState().squareDraft.locationId,
    "sq-location-1",
  );
  assert.equal(
    emptyLocationPresenter.getState().squareSetup.locations.kind,
    "failed",
  );

  const emptyDevicePort = new FakeSettingsPort();
  const emptyDeviceSetup = new FakeSquareSetupControlPort();
  emptyDevicePort.squareSetup = emptyDeviceSetup;
  emptyDeviceSetup.locations = [
    squareLocation("sq-location-1", "Brisbane"),
  ];
  emptyDeviceSetup.devices = [];
  const emptyDevicePresenter = createPresenter(emptyDevicePort);
  await emptyDevicePresenter.load();
  await emptyDevicePresenter.loadSquareLocations();
  await emptyDevicePresenter.loadSquareDevices();
  assert.equal(
    emptyDevicePresenter.getState().squareDraft.deviceId,
    "sq-device-1",
  );
  assert.equal(
    emptyDevicePresenter.getState().squareSetup.devices.kind,
    "empty",
  );

  emptyDeviceSetup.deviceHandler = async () => {
    throw new Error("devices unavailable");
  };
  await emptyDevicePresenter.loadSquareDevices();
  assert.equal(
    emptyDevicePresenter.getState().squareDraft.deviceId,
    "sq-device-1",
  );
  assert.equal(
    emptyDevicePresenter.getState().squareSetup.devices.kind,
    "failed",
  );
});

test("运行时快照即使夹带额外敏感字段也只按白名单进入 state", async () => {
  const port = new FakeSettingsPort();
  const clean = snapshot();
  port.snapshotValue = {
    ...clean,
    hardware: {
      ...clean.hardware,
      scannerCredential: "scanner-credential-should-not-enter-state",
    },
    linkly: {
      ...clean.linkly,
      secret: "linkly-secret-should-not-enter-state",
    },
    printer: {
      ...clean.printer,
      accessToken: "printer-token-should-not-enter-state",
    },
    square: {
      ...clean.square,
      accessToken: "square-token-should-not-enter-state",
      blockerCode: "SQUARE_ACCESS_TOKEN_ABC123",
    },
  } as unknown as SettingsSnapshot;
  const presenter = createPresenter(port);

  await presenter.load();

  const serialized = JSON.stringify(presenter.getState());
  assert.equal(
    presenter.getState().square.blockerCode,
    "invalid-provider-config",
  );
  for (const sensitive of [
    "authorization",
    "scannerCredential",
    "secret",
    "accessToken",
    "SQUARE_ACCESS_TOKEN_ABC123",
    "should-not-enter-state",
  ]) {
    assert.equal(serialized.includes(sensitive), false);
  }
});

test("Square/Linkly 与打印机保存按精确权限执行并使用稳定状态码", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setSquareEnvironment("Sandbox");
  presenter.setSquareLocationId(" sq-location-2 ");
  presenter.setSquareDeviceId(" sq-device-2 ");
  presenter.setLinklyEnvironment("Sandbox");
  presenter.setPaymentProvider("square");
  await presenter.testPaymentProvider("square");
  assert.deepEqual(port.paymentTests, [
    {
      provider: "square",
      input: {
        provider: "square",
        square: {
          environment: "Sandbox",
          locationId: "sq-location-2",
          deviceId: "sq-device-2",
        },
        linkly: null,
      },
    },
  ]);
  await presenter.savePaymentSettings();

  assert.equal(
    presenter.getState().confirmation?.kind,
    "change-payment-settings",
  );
  assert.deepEqual(port.savedPayments, []);
  await presenter.confirmDangerousAction();

  assert.deepEqual(port.savedPayments, [
    {
      provider: "square",
      square: {
        environment: "Sandbox",
        locationId: "sq-location-2",
        deviceId: "sq-device-2",
      },
      linkly: null,
    },
  ]);
  assert.equal(presenter.getState().statusCode, "payment-settings-saved");

  presenter.setPrinterEnabled(false);
  presenter.setPrinterPeripheralId(" printer-2 ");
  presenter.setPrinterPaper("58mm");
  presenter.setPrinterLocale("zh-CN");
  await presenter.savePrinterSettings();

  assert.deepEqual(port.savedPrinters, [
    {
      ...DEFAULT_RECEIPT_PRINTER_SETTINGS,
      printEnabled: false,
      peripheralId: "printer-2",
      paper: "58mm",
      locale: "zh-CN",
      profileStoreCode: "BNE-01",
    },
  ]);
  assert.equal(presenter.getState().statusCode, "printer-settings-saved");
});

test("手动载入门店资料成功时只替换六字段并保留硬件，失败/控制字符/店号不一致均不改草稿", async () => {
  const port = new FakeSettingsPort();
  port.snapshotValue = {
    ...snapshot(),
    printer: {
      ...DEFAULT_RECEIPT_PRINTER_SETTINGS,
      printEnabled: true,
      drawerEnabled: true,
      peripheralId: "XP-N160I",
      paper: "58mm",
      locale: "zh-CN",
    },
  };
  const presenter = createPresenter(port);
  await presenter.load();

  port.receiptProfileValue = {
    storeCode: "BNE-01",
    brandName: "Hot Bargain",
    storeName: "Brisbane",
    address: "1 Queen St",
    phone: "07 1234 5678",
    abn: "12 345 678 901",
    returnPolicy: "Refunds within 14 days.",
  };
  await presenter.loadReceiptProfile();

  const loaded = presenter.getState().printer;
  assert.deepEqual(
    {
      brandName: loaded.brandName,
      storeName: loaded.storeName,
      address: loaded.address,
      phone: loaded.phone,
      abn: loaded.abn,
      returnPolicy: loaded.returnPolicy,
      profileStoreCode: loaded.profileStoreCode,
    },
    {
      brandName: "Hot Bargain",
      storeName: "Brisbane",
      address: "1 Queen St",
      phone: "07 1234 5678",
      abn: "12 345 678 901",
      returnPolicy: "Refunds within 14 days.",
      profileStoreCode: "BNE-01",
    },
  );
  assert.equal(loaded.printEnabled, true);
  assert.equal(loaded.drawerEnabled, true);
  assert.equal(loaded.peripheralId, "XP-N160I");
  assert.equal(loaded.paper, "58mm");
  assert.equal(loaded.locale, "zh-CN");
  assert.equal(presenter.getState().statusCode, "receipt-profile-loaded");

  port.receiptProfileValue = {
    storeCode: "BNE-01",
    brandName: "",
    storeName: "",
    address: "",
    phone: "",
    abn: "",
    returnPolicy: "",
  };
  await presenter.loadReceiptProfile();
  const empty = presenter.getState().printer;
  assert.equal(empty.brandName, "");
  assert.equal(empty.storeName, "");
  assert.equal(empty.address, "");
  assert.equal(empty.phone, "");
  assert.equal(empty.abn, "");
  assert.equal(empty.returnPolicy, "");
  assert.equal(empty.profileStoreCode, "BNE-01");
  assert.equal(empty.peripheralId, "XP-N160I");

  const failedBefore = presenter.getState().printer;
  port.failReceiptProfile = true;
  await presenter.loadReceiptProfile();
  assert.equal(presenter.getState().statusCode, "receipt-profile-load-failed");
  assert.deepEqual(presenter.getState().printer, failedBefore);

  port.failReceiptProfile = false;
  port.receiptProfileValue = {
    storeCode: "BNE-01",
    brandName: "Hot Bargain",
    storeName: "Brisbane",
    address: "1 Queen St",
    phone: "07 1234 5678",
    abn: "12 345 678 901",
    returnPolicy: "Unsafe\u001b@",
  };
  await presenter.loadReceiptProfile();
  assert.equal(presenter.getState().statusCode, "receipt-profile-load-failed");
  assert.deepEqual(presenter.getState().printer, failedBefore);

  port.receiptProfileValue = {
    storeCode: "OTHER-01",
    brandName: "Hot Bargain",
    storeName: "Brisbane",
    address: "1 Queen St",
    phone: "07 1234 5678",
    abn: "12 345 678 901",
    returnPolicy: "Refunds within 14 days.",
  };
  await presenter.loadReceiptProfile();
  assert.equal(presenter.getState().statusCode, "receipt-profile-load-failed");
  assert.deepEqual(presenter.getState().printer, failedBefore);

  port.receiptProfileValue = null;
  await presenter.loadReceiptProfile();
  assert.equal(presenter.getState().statusCode, "receipt-profile-load-failed");
  assert.deepEqual(presenter.getState().printer, failedBefore);
});

test("载入门店资料只在 Save 后落本机", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  port.receiptProfileValue = {
    storeCode: "BNE-01",
    brandName: "Hot Bargain",
    storeName: "Brisbane",
    address: "1 Queen St",
    phone: "07 1234 5678",
    abn: "12 345 678 901",
    returnPolicy: "Refunds within 14 days.",
  };
  await presenter.loadReceiptProfile();
  assert.equal(port.savedPrinters.length, 0);

  await presenter.savePrinterSettings();
  assert.equal(port.savedPrinters.length, 1);
  assert.equal(port.savedPrinters[0]?.brandName, "Hot Bargain");
  assert.equal(port.savedPrinters[0]?.returnPolicy, "Refunds within 14 days.");
  assert.equal(port.savedPrinters[0]?.profileStoreCode, "BNE-01");
  assert.equal(presenter.getState().statusCode, "printer-settings-saved");
});

test("测试打印先保存当前分店 draft，保存失败时不触发硬件", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setPrinterPeripheralId(" printer-current ");
  presenter.setReceiptBrandName("Current Brand");

  await presenter.testPrinter();

  assert.equal(port.savedPrinters.length, 1);
  assert.equal(port.savedPrinters[0]?.peripheralId, "printer-current");
  assert.equal(port.savedPrinters[0]?.brandName, "Current Brand");
  assert.equal(port.savedPrinters[0]?.profileStoreCode, "BNE-01");
  assert.equal(port.printerTestCalls, 1);
  assert.equal(presenter.getState().statusCode, "printer-test-passed");

  const failedPort = new FakeSettingsPort();
  failedPort.failPrinterSave = true;
  const failedPresenter = createPresenter(failedPort);
  await failedPresenter.load();

  await failedPresenter.testPrinter();

  assert.equal(failedPort.printerTestCalls, 0);
  assert.equal(failedPresenter.getState().statusCode, "printer-test-failed");
});

test("钱箱测试先保存当前 draft，再单次执行受控动作并区分三种结果", async () => {
  for (const [outcome, expectedStatus] of [
    ["completed", "cash-drawer-test-passed"],
    ["unknown", "cash-drawer-test-unknown"],
    ["failed", "cash-drawer-test-failed"],
  ] as const) {
    const port = new FakeSettingsPort();
    port.cashDrawerTestResult = { status: outcome, errorCode: null };
    const presenter = createPresenter(port);
    await presenter.load();
    presenter.setPrinterPeripheralId(" printer-current ");
    presenter.setDrawerEnabled(true);

    await presenter.testCashDrawer();

    assert.deepEqual(port.printerEvents, [
      "save:printer-current:true",
      "test-cash-drawer",
    ]);
    assert.equal(port.cashDrawerTestCalls, 1);
    assert.equal(port.savedPrinters[0]?.profileStoreCode, "BNE-01");
    assert.equal(presenter.getState().statusCode, expectedStatus);
  }
});

test("钱箱测试保存失败时不执行硬件动作，缺少能力时也明确失败", async () => {
  const saveFailurePort = new FakeSettingsPort();
  saveFailurePort.failPrinterSave = true;
  const saveFailurePresenter = createPresenter(saveFailurePort);
  await saveFailurePresenter.load();
  saveFailurePresenter.setPrinterPeripheralId("printer-1");
  saveFailurePresenter.setDrawerEnabled(true);

  await saveFailurePresenter.testCashDrawer();

  assert.equal(saveFailurePort.cashDrawerTestCalls, 0);
  assert.equal(
    saveFailurePresenter.getState().statusCode,
    "cash-drawer-test-failed",
  );

  const unavailablePort = new FakeSettingsPort();
  unavailablePort.testCashDrawer = undefined;
  const unavailablePresenter = createPresenter(unavailablePort);
  await unavailablePresenter.load();

  await unavailablePresenter.testCashDrawer();

  assert.deepEqual(unavailablePort.savedPrinters, []);
  assert.equal(
    unavailablePresenter.getState().statusCode,
    "cash-drawer-test-failed",
  );
});

test("清除打印机区分完整成功、已清除但断开失败和持久化失败", async () => {
  const completedPort = new FakeSettingsPort();
  const completedPresenter = createPresenter(completedPort);
  await completedPresenter.load();
  completedPresenter.setPrinterPeripheralId("printer-1");

  await completedPresenter.clearSavedPrinter();

  assert.equal(completedPort.clearSavedPrinterCalls, 1);
  assert.equal(completedPresenter.getState().printer.peripheralId, null);
  assert.equal(
    completedPresenter.getState().hardware.printerStatus,
    "connected",
  );
  assert.equal(completedPresenter.getState().statusCode, "printer-cleared");

  const disconnectFailurePort = new FakeSettingsPort();
  disconnectFailurePort.clearSavedPrinterResult = {
    status: "cleared-disconnect-failed",
    errorCode: "PRINTER_DISCONNECT_FAILED",
  };
  const disconnectFailurePresenter = createPresenter(disconnectFailurePort);
  await disconnectFailurePresenter.load();
  disconnectFailurePresenter.setPrinterPeripheralId("printer-2");

  await disconnectFailurePresenter.clearSavedPrinter();

  assert.equal(
    disconnectFailurePresenter.getState().printer.peripheralId,
    null,
  );
  assert.equal(
    disconnectFailurePresenter.getState().hardware.printerStatus,
    "connected",
  );
  assert.equal(
    disconnectFailurePresenter.getState().statusCode,
    "printer-cleared-disconnect-failed",
  );

  const saveFailurePort = new FakeSettingsPort();
  saveFailurePort.failClearSavedPrinter = true;
  const saveFailurePresenter = createPresenter(saveFailurePort);
  await saveFailurePresenter.load();
  saveFailurePresenter.setPrinterPeripheralId("printer-kept");

  await saveFailurePresenter.clearSavedPrinter();

  assert.equal(
    saveFailurePresenter.getState().printer.peripheralId,
    "printer-kept",
  );
  assert.equal(
    saveFailurePresenter.getState().statusCode,
    "printer-clear-failed",
  );
});

test("扫描打印机时 preferred 优先，其余按名称和 ID 稳定排序", async () => {
  const port = new FakeSettingsPort();
  port.printerDevices = [
    {
      id: "printer-z",
      name: "Beta",
      transport: "bluetooth-le",
      preferred: false,
    },
    {
      id: "printer-b",
      name: "Alpha",
      transport: "bluetooth-le",
      preferred: false,
    },
    {
      id: "printer-preferred",
      name: " Printer001 ",
      transport: "bluetooth-le",
      preferred: true,
    },
    {
      id: "printer-a",
      name: "Alpha",
      transport: "bluetooth-le",
      preferred: false,
    },
  ];
  const presenter = createPresenter(port);
  await presenter.load();

  await presenter.scanPrinters();

  assert.deepEqual(presenter.getState().printerDevices, [
    {
      id: "printer-preferred",
      name: "Printer001",
      transport: "bluetooth-le",
      preferred: true,
      paired: null,
    },
    {
      id: "printer-a",
      name: "Alpha",
      transport: "bluetooth-le",
      preferred: false,
      paired: null,
    },
    {
      id: "printer-b",
      name: "Alpha",
      transport: "bluetooth-le",
      preferred: false,
      paired: null,
    },
    {
      id: "printer-z",
      name: "Beta",
      transport: "bluetooth-le",
      preferred: false,
      paired: null,
    },
  ]);
});

for (const [nativeCode, expectedStatus] of [
  [
    "PRINTER_BLUETOOTH_PERMISSION_REQUIRED",
    "printer-bluetooth-permission-required",
  ],
  [
    "PRINTER_BLUETOOTH_AUTHORIZATION_PENDING",
    "printer-bluetooth-authorization-pending",
  ],
  ["PRINTER_BLUETOOTH_RESTRICTED", "printer-bluetooth-restricted"],
  ["PRINTER_BLUETOOTH_POWERED_OFF", "printer-bluetooth-powered-off"],
  ["PRINTER_BLUETOOTH_LOCATION_OFF", "printer-bluetooth-location-off"],
] as const) {
  test(`蓝牙扫描错误 ${nativeCode} 保留为专属界面状态`, async () => {
    const port = new FakeSettingsPort();
    port.printerScanError = Object.assign(new Error(nativeCode), {
      code: nativeCode,
    });
    const presenter = createPresenter(port);
    await presenter.load();

    await presenter.scanPrinters();

    assert.equal(presenter.getState().statusCode, expectedStatus);
  });
}

test("连接打印机成功后保存当前 draft 与 UUID", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setPrinterEnabled(false);
  presenter.setPrinterPaper("58mm");
  presenter.setPrinterLocale("zh-CN");

  await presenter.connectPrinter(" printer-2 ");

  assert.deepEqual(port.connectedPrinterIds, ["printer-2"]);
  assert.deepEqual(port.savedPrinters, [
    {
      ...DEFAULT_RECEIPT_PRINTER_SETTINGS,
      peripheralId: "printer-2",
      paper: "58mm",
      locale: "zh-CN",
      profileStoreCode: "BNE-01",
    },
  ]);
  assert.equal(presenter.getState().printer.peripheralId, "printer-2");
  assert.equal(presenter.getState().hardware.printerStatus, "connected");
  assert.equal(presenter.getState().statusCode, "printer-connected");
});

test("连接失败与已连接但保存失败使用不同状态且保留真实硬件状态", async () => {
  const connectFailurePort = new FakeSettingsPort();
  connectFailurePort.failPrinterConnect = true;
  connectFailurePort.snapshotValue = {
    ...snapshot(),
    hardware: {
      ...snapshot().hardware,
      printerStatus: "disconnected",
    },
  };
  const connectFailurePresenter = createPresenter(connectFailurePort);
  await connectFailurePresenter.load();

  await connectFailurePresenter.connectPrinter("printer-connect-fails");

  assert.deepEqual(connectFailurePort.connectedPrinterIds, [
    "printer-connect-fails",
  ]);
  assert.deepEqual(connectFailurePort.savedPrinters, []);
  assert.equal(
    connectFailurePresenter.getState().printer.peripheralId,
    null,
  );
  assert.equal(
    connectFailurePresenter.getState().hardware.printerStatus,
    "disconnected",
  );
  assert.equal(
    connectFailurePresenter.getState().statusCode,
    "printer-connect-failed",
  );

  // 经典蓝牙未配对：原生在 RFCOMM 前拒绝，状态单独提示去系统配对，且不保存设置。
  const pairingPort = new FakeSettingsPort();
  pairingPort.printerConnectErrorCode = "PRINTER_SPP_PAIRING_REQUIRED";
  const pairingPresenter = createPresenter(pairingPort);
  await pairingPresenter.load();
  await pairingPresenter.connectPrinter("spp:00:11:22:33:44:55");
  assert.equal(
    pairingPresenter.getState().statusCode,
    "printer-pairing-required",
  );
  assert.deepEqual(pairingPort.savedPrinters, []);

  const saveFailurePort = new FakeSettingsPort();
  saveFailurePort.failPrinterSave = true;
  saveFailurePort.snapshotValue = {
    ...snapshot(),
    hardware: {
      ...snapshot().hardware,
      printerStatus: "disconnected",
    },
  };
  const saveFailurePresenter = createPresenter(saveFailurePort);
  await saveFailurePresenter.load();

  await saveFailurePresenter.connectPrinter("printer-save-fails");

  assert.deepEqual(saveFailurePort.connectedPrinterIds, [
    "printer-save-fails",
  ]);
  assert.equal(saveFailurePort.savedPrinters.length, 1);
  assert.equal(
    saveFailurePort.savedPrinters[0]?.peripheralId,
    "printer-save-fails",
  );
  assert.equal(
    saveFailurePresenter.getState().printer.peripheralId,
    "printer-save-fails",
  );
  assert.equal(
    saveFailurePresenter.getState().hardware.printerStatus,
    "connected",
  );
  assert.equal(
    saveFailurePresenter.getState().statusCode,
    "printer-connected-save-failed",
  );
});

test("Square 不可用时仍可单独保存 Linkly 环境", async () => {
  const port = new FakeSettingsPort();
  const current = snapshot();
  port.snapshotValue = {
    ...current,
    square: {
      available: false,
      blockerCode: "square-not-configured",
      environment: "Production",
      deviceId: "",
      locationId: "",
    },
    paymentProvider: null,
  };
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setPaymentProvider("linkly");
  presenter.setLinklyEnvironment("Sandbox");
  await presenter.savePaymentSettings();

  assert.equal(
    presenter.getState().confirmation?.kind,
    "change-payment-settings",
  );
  await presenter.confirmDangerousAction();

  assert.deepEqual(port.savedPayments, [
    {
      provider: "linkly",
      square: null,
      linkly: { environment: "Sandbox" },
    },
  ]);
  assert.equal(presenter.getState().statusCode, "payment-settings-saved");
});

test("Linkly 缺少公开环境时仍可首次选择、测试并保存", async () => {
  const port = new FakeSettingsPort();
  const current = snapshot();
  port.snapshotValue = {
    ...current,
    linkly: {
      available: false,
      blockerCode: "LINKLY_CONFIGURATION_MISSING",
      environment: "Production",
    },
  };
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setPaymentProvider("linkly");
  presenter.setLinklyEnvironment("Sandbox");
  await presenter.testPaymentProvider("linkly");
  await presenter.savePaymentSettings();

  const expected = {
    provider: "linkly" as const,
    square: null,
    linkly: { environment: "Sandbox" as const },
  };
  assert.equal(presenter.getState().paymentProviderDraft, "linkly");
  assert.deepEqual(port.paymentTests, [
    { provider: "linkly", input: expected },
  ]);
  assert.equal(
    presenter.getState().confirmation?.kind,
    "change-payment-settings",
  );

  await presenter.confirmDangerousAction();

  assert.deepEqual(port.savedPayments, [expected]);
  assert.deepEqual(presenter.getState().linkly, {
    available: true,
    blockerCode: null,
    environment: "Sandbox",
  });
  assert.equal(presenter.getState().statusCode, "payment-settings-saved");
});

test("手动刷卡默认关闭，切换后独立保存且不触发重载", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  assert.equal(presenter.getState().paymentMethods.useManualCard, false);
  presenter.setUseManualCard(true);
  await presenter.saveManualCardSettings();

  assert.deepEqual(port.savedPaymentMethods, [{ useManualCard: true }]);
  assert.equal(port.dangerousActionCalls, 0);
  assert.equal(presenter.getState().statusCode, "payment-settings-saved");

  port.failPaymentMethodSave = true;
  presenter.setUseManualCard(false);
  await presenter.saveManualCardSettings();
  assert.equal(
    presenter.getState().paymentMethods.useManualCard,
    true,
  );
  assert.equal(presenter.getState().statusCode, "payment-settings-save-failed");
});

test("Linkly 配置无效或读取失败时仍禁止选择", async () => {
  for (const blockerCode of [
    "LINKLY_CONFIGURATION_INVALID",
    "LINKLY_CONFIGURATION_LOAD_FAILED",
  ]) {
    const port = new FakeSettingsPort();
    const current = snapshot();
    port.snapshotValue = {
      ...current,
      linkly: {
        available: false,
        blockerCode,
        environment: "Production",
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();

    presenter.setPaymentProvider("linkly");

    assert.equal(presenter.getState().paymentProviderDraft, "square");
    assert.equal(presenter.getState().statusCode, "payment-settings-invalid");
  }
});

test("Square 与 Linkly 同时可用但未显式选择时支付保持 fail closed", async () => {
  const port = new FakeSettingsPort();
  port.snapshotValue = { ...snapshot(), paymentProvider: null };
  const presenter = createPresenter(port);
  await presenter.load();

  assert.equal(presenter.getState().paymentProvider, null);
  assert.equal(presenter.getState().paymentProviderDraft, null);

  await presenter.savePaymentSettings();
  await presenter.testPaymentProvider("square");

  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "payment-settings-invalid");
  assert.deepEqual(port.paymentTests, []);
  assert.deepEqual(port.savedPayments, []);
});

test("显式切换到 Linkly 时只保存 Linkly，不能依赖提供方顺序", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setPaymentProvider("linkly");
  presenter.setLinklyEnvironment("Sandbox");
  await presenter.testPaymentProvider("linkly");
  await presenter.savePaymentSettings();
  await presenter.confirmDangerousAction();

  const expected = {
    provider: "linkly" as const,
    square: null,
    linkly: { environment: "Sandbox" as const },
  };
  assert.deepEqual(port.paymentTests, [
    { provider: "linkly", input: expected },
  ]);
  assert.deepEqual(port.savedPayments, [expected]);
  assert.equal(presenter.getState().paymentProvider, "linkly");
  assert.equal(presenter.getState().paymentProviderDraft, "linkly");
});

test("Linkly 页面加载与环境切换刷新 health，并丢弃迟到环境响应", async () => {
  const port = new FakeSettingsPort();
  const setup = new FakeLinklySetupControlPort();
  port.linklySetup = setup;
  const sandboxHealth = deferred<SettingsLinklyHealthSnapshot>();
  setup.readHandlers = {
    Production: async () => linklyHealth("Production", true),
    Sandbox: () => sandboxHealth.promise,
  };
  const presenter = createPresenter(port);

  await presenter.load();
  assert.equal(
    presenter.getState().linklySetup?.health.value?.environment,
    "Production",
  );

  presenter.setLinklyEnvironment("Sandbox");
  await Promise.resolve();
  presenter.setLinklyEnvironment("Production");
  sandboxHealth.resolve(linklyHealth("Sandbox", false));
  await new Promise<void>((resolve) => setImmediate(resolve));
  await new Promise<void>((resolve) => setImmediate(resolve));

  assert.deepEqual(setup.readEnvironments, [
    "Production",
    "Sandbox",
    "Production",
  ]);
  assert.equal(presenter.getState().linklyDraft.environment, "Production");
  assert.equal(
    presenter.getState().linklySetup?.health.value?.environment,
    "Production",
  );
  assert.equal(
    presenter.getState().linklySetup?.health.value?.isReady,
    true,
  );
});

test("Linkly Active 设置先读取权威终端快照，再按同一快照读取 health", async () => {
  const port = new FakeSettingsPort();
  const setup = new FakeLinklySetupControlPort();
  port.linklySetup = setup;
  setup.terminals = linklyTerminals("Production", "terminal-2", 9);
  const presenter = createPresenter(port);

  await presenter.load();
  await presenter.testPaymentProvider("linkly");

  assert.deepEqual(setup.readSequence, ["terminals", "health"]);
  assert.deepEqual(setup.healthSelections, [setup.terminals]);
  assert.deepEqual(port.paymentTerminalSelections, [setup.terminals]);
  assert.equal(presenter.getState().linklySetup?.health.kind, "ready");
  assert.equal(presenter.getState().linklySetup?.terminals.kind, "ready");
});

test("Linkly 多终端读取、持久切换并按选中终端配对", async () => {
  const port = new FakeSettingsPort();
  const setup = new FakeLinklySetupControlPort();
  const pairing = new FakeLinklyPairingPort();
  port.linklySetup = setup;
  port.linklyPairing = pairing;
  setup.terminals = linklyTerminals("Production", "terminal-1", 2);
  const presenter = createPresenter(port);

  await presenter.load();

  assert.equal(
    presenter.getState().linklySetup?.terminals.value?.terminals.length,
    2,
  );
  await presenter.selectLinklyTerminal("terminal-2");
  assert.deepEqual(setup.selectCalls, [
    {
      environment: "Production",
      terminalId: "terminal-2",
      expectedRevision: 2,
    },
  ]);
  assert.equal(
    presenter.getState().linklySetup?.terminals.value?.selectedTerminalId,
    "terminal-2",
  );
  assert.equal(presenter.requestLinklyPair("654321"), true);
  await presenter.confirmDangerousAction();
  assert.deepEqual(pairing.pairCalls, [
    {
      environment: "Production",
      terminalId: "terminal-2",
      pairCode: "654321",
    },
  ]);
});

test("Linkly 忙碌终端仍可持久预选", async () => {
  const port = new FakeSettingsPort();
  const setup = new FakeLinklySetupControlPort();
  port.linklySetup = setup;
  setup.terminals = Object.freeze({
    ...linklyTerminals("Production", "terminal-1", 2),
    terminals: Object.freeze([
      ...linklyTerminals("Production", "terminal-1", 2).terminals.slice(0, 1),
      Object.freeze({
        ...linklyTerminals("Production", "terminal-1", 2).terminals[1]!,
        isBusy: true,
      }),
    ]),
  });
  const presenter = createPresenter(port);

  await presenter.load();
  await presenter.selectLinklyTerminal("terminal-2");

  assert.deepEqual(setup.selectCalls, [
    {
      environment: "Production",
      terminalId: "terminal-2",
      expectedRevision: 2,
    },
  ]);
  assert.equal(
    presenter.getState().linklySetup?.terminals.value?.selectedTerminalId,
    "terminal-2",
  );
});

test("Linkly Legacy/Draft readiness 只使用旧 health，不受终端目录状态污染", async () => {
  for (const mode of ["Legacy", "Draft"] as const) {
    const port = new FakeSettingsPort();
    const setup = new FakeLinklySetupControlPort();
    port.linklySetup = setup;
    setup.health = linklyHealth("Production", true);
    setup.terminals = Object.freeze({
      ...linklyTerminals("Production", mode === "Legacy" ? null : "terminal-1", 2),
      mode,
      terminals:
        mode === "Legacy"
          ? Object.freeze([])
          : Object.freeze([
              Object.freeze({
                ...linklyTerminals("Production", "terminal-1", 2).terminals[0]!,
                pairingState: "NeedsRepair" as const,
                isBusy: true,
                isReady: false,
              }),
            ]),
    });
    const presenter = createPresenter(port);

    await presenter.load();
    await presenter.testPaymentProvider("linkly");
    presenter.setPaymentProvider("linkly");
    await presenter.savePaymentSettings();

    assert.equal(presenter.getState().linklySetup?.logonTest.status, "passed");
    assert.equal(presenter.getState().paymentProviderDraft, "linkly");
    assert.equal(
      presenter.getState().confirmation?.kind,
      "change-payment-settings",
    );
    assert.equal(port.paymentTests.length, 1);
  }
});

test("Linkly 切换结果不明时重读权威选择；仍失败则清空旧确认并 fail closed", async () => {
  const recoveredPort = new FakeSettingsPort();
  const recoveredSetup = new FakeLinklySetupControlPort();
  recoveredPort.linklySetup = recoveredSetup;
  const recoveredPresenter = createPresenter(recoveredPort);
  await recoveredPresenter.load();
  recoveredSetup.selectTerminal = async (
    environment,
    terminalId,
    expectedRevision,
  ) => {
    recoveredSetup.selectCalls.push({ environment, terminalId, expectedRevision });
    recoveredSetup.terminals = Object.freeze({
      ...recoveredSetup.terminals,
      selectedTerminalId: terminalId,
      selectionRevision: expectedRevision + 1,
    });
    throw new Error("PUT committed but response GET failed");
  };

  await recoveredPresenter.selectLinklyTerminal("terminal-2");

  assert.equal(
    recoveredPresenter.getState().linklySetup?.terminals.value?.selectedTerminalId,
    "terminal-2",
  );
  assert.equal(
    recoveredPresenter.getState().linklySetup?.terminals.kind,
    "ready",
  );

  const failedPort = new FakeSettingsPort();
  const failedSetup = new FakeLinklySetupControlPort();
  failedPort.linklySetup = failedSetup;
  const failedPresenter = createPresenter(failedPort);
  await failedPresenter.load();
  failedSetup.selectTerminal = async () => {
    throw new Error("selection result unknown");
  };
  failedSetup.readTerminals = async () => {
    throw new Error("authoritative refresh unavailable");
  };

  await failedPresenter.selectLinklyTerminal("terminal-2");

  assert.equal(failedPresenter.getState().linklySetup?.terminals.kind, "failed");
  assert.equal(failedPresenter.getState().linklySetup?.terminals.value, null);
  assert.equal(
    failedPresenter.getState().statusCode,
    "linkly-terminal-switch-failed",
  );
});

test("Linkly 首次配对只需门店凭据；刷新 ready 后 logon 才可保存，未变更保存保持 no-op", async () => {
  const port = new FakeSettingsPort();
  const setup = new FakeLinklySetupControlPort();
  const pairing = new FakeLinklyPairingPort();
  port.linklySetup = setup;
  port.linklyPairing = pairing;
  setup.health = linklyHealth("Production", false, true);
  port.snapshotValue = {
    ...snapshot(),
    paymentProvider: null,
    linkly: {
      available: false,
      blockerCode: "LINKLY_CONFIGURATION_MISSING",
      environment: "Production",
    },
  };
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setPaymentProvider("linkly");
  assert.equal(presenter.getState().paymentProviderDraft, null);
  assert.equal(presenter.getState().statusCode, "linkly-setup-required");

  await presenter.savePaymentSettings();
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "payment-settings-invalid");

  assert.equal(presenter.requestLinklyPair("123456"), true);
  setup.health = linklyHealth("Production", true);
  await presenter.confirmDangerousAction();
  assert.equal(presenter.getState().statusCode, "linkly-paired");

  await presenter.testPaymentProvider("linkly");
  assert.equal(presenter.getState().linklySetup?.logonTest.status, "passed");
  presenter.setPaymentProvider("linkly");
  assert.equal(presenter.getState().paymentProviderDraft, "linkly");
  await presenter.savePaymentSettings();
  assert.equal(
    presenter.getState().confirmation?.kind,
    "change-payment-settings",
  );
  await presenter.confirmDangerousAction();
  assert.equal(presenter.getState().statusCode, "payment-settings-saved");

  await presenter.savePaymentSettings();
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "payment-settings-saved");
  assert.equal(port.savedPayments.length, 1);
});

test("Linkly 配对是危险操作；成功清码刷新，unknown 只刷新且不重试", async () => {
  const port = new FakeSettingsPort();
  const setup = new FakeLinklySetupControlPort();
  const pairing = new FakeLinklyPairingPort();
  port.linklySetup = setup;
  port.linklyPairing = pairing;
  setup.health = linklyHealth("Production", false, true);
  const presenter = createPresenter(port);
  await presenter.load();

  assert.equal(presenter.requestLinklyPair("123456"), true);
  assert.deepEqual(pairing.pairCalls, []);
  assert.equal(presenter.getState().confirmation?.kind, "pair-linkly");
  setup.health = linklyHealth("Production", true);
  await presenter.confirmDangerousAction();
  assert.deepEqual(pairing.pairCalls, [
    {
      environment: "Production",
      terminalId: "terminal-1",
      pairCode: "123456",
    },
  ]);
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "linkly-paired");
  assert.equal(presenter.getState().linklySetup?.pairCodeResetToken, 1);
  assert.equal(presenter.getState().linklySetup?.logonTest.status, "idle");

  await presenter.testPaymentProvider("linkly");
  assert.equal(presenter.getState().linklySetup?.logonTest.status, "passed");

  pairing.pairResult = { status: "unknown" };
  assert.equal(presenter.requestLinklyPair("654321"), true);
  await presenter.confirmDangerousAction();
  assert.equal(presenter.getState().statusCode, "linkly-pair-unknown");
  assert.equal(presenter.getState().linklySetup?.pairCodeResetToken, 2);
  assert.deepEqual(pairing.pairCalls, [
    {
      environment: "Production",
      terminalId: "terminal-1",
      pairCode: "123456",
    },
    {
      environment: "Production",
      terminalId: "terminal-1",
      pairCode: "654321",
    },
  ]);
  assert.equal(
    setup.readEnvironments.filter((environment) => environment === "Production")
      .length,
    3,
  );
});

test("Linkly 配对结果 unknown 后刷新失败时保留真实刷新失败状态", async () => {
  const port = new FakeSettingsPort();
  const setup = new FakeLinklySetupControlPort();
  const pairing = new FakeLinklyPairingPort();
  port.linklySetup = setup;
  port.linklyPairing = pairing;
  setup.health = linklyHealth("Production", false, true);
  const presenter = createPresenter(port);
  await presenter.load();

  pairing.pairResult = { status: "unknown" };
  assert.equal(presenter.requestLinklyPair("123456"), true);
  setup.readHandlers.Production = async () => {
    throw new Error("health unavailable");
  };
  await presenter.confirmDangerousAction();

  assert.equal(presenter.getState().linklySetup?.health.kind, "failed");
  assert.equal(presenter.getState().statusCode, "linkly-health-load-failed");
  assert.equal(presenter.getState().linklySetup?.pairCodeResetToken, 1);
  assert.deepEqual(pairing.pairCalls, [
    {
      environment: "Production",
      terminalId: "terminal-1",
      pairCode: "123456",
    },
  ]);
});

test("快照选择不可用提供方时清空活动选择，且不可再次选中", async () => {
  const port = new FakeSettingsPort();
  const current = snapshot();
  port.snapshotValue = {
    ...current,
    paymentProvider: "square",
    square: {
      ...current.square,
      blockerCode: "square-not-configured",
    },
  };
  const presenter = createPresenter(port);
  await presenter.load();

  assert.equal(presenter.getState().paymentProvider, null);
  assert.equal(presenter.getState().paymentProviderDraft, null);

  presenter.setPaymentProvider("square");
  await presenter.savePaymentSettings();

  assert.equal(presenter.getState().paymentProviderDraft, null);
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "payment-settings-invalid");
});

test("API 切换、目录重置、设备重注册与应用重启必须先确认", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setApiAddressDraft("https://staging.example.com/pos-api/");
  assert.equal(presenter.requestApiAddressChange(), true);
  assert.equal(port.apiAddressChanges.length, 0);
  assert.equal(presenter.getState().confirmation?.kind, "change-api-address");
  await presenter.confirmDangerousAction();
  assert.deepEqual(port.apiAddressChanges, [
    "https://staging.example.com/pos-api",
  ]);

  assert.equal(presenter.requestCatalogReset(), true);
  assert.equal(port.catalogResetCalls, 0);
  await presenter.confirmDangerousAction();
  assert.equal(port.catalogResetCalls, 1);

  presenter.setDeviceActivationCode(` \t${deviceActivationCode.toLowerCase()}\n`);
  presenter.setTerminalName(" iPad Front ");
  await presenter.previewDeviceReregistration();
  assert.deepEqual(presenter.getState().deviceActivationPreview, {
    activationCode: deviceActivationCode,
    storeCode: "BNE-02",
    storeName: "Sunnybank",
    deviceSystem: "Android",
    expiresAtUtc: "2026-08-28T00:00:00.000Z",
  });
  await presenter.requestDeviceReregistration();
  assert.equal(presenter.getState().confirmation?.kind, "reregister-device");
  assert.equal(port.reregistrations.length, 0);
  await presenter.confirmDangerousAction();
  assert.deepEqual(port.reregistrations, [
    { activationCode: deviceActivationCode, terminalName: "iPad Front" },
  ]);

  assert.equal(presenter.requestAppRestart(), true);
  assert.equal(port.restartCalls, 0);
  await presenter.confirmDangerousAction();
  assert.equal(port.restartCalls, 1);
  assert.equal(port.dangerousActionCalls, 4);
});

test("更换分店预检保存八类脱敏阻断并允许只读重新检查", async () => {
  const port = new FakeSettingsPort();
  port.pending = {
    hasActiveCart: true,
    hasFulfilmentInFlight: true,
    hasSyncOrAuditInFlight: true,
    paymentConfigurationSensitiveOrderCount: 4,
    pendingDurableWriteCount: 5,
    pendingReturnCount: 2,
    pendingSaleCount: 3,
    unresolvedPaymentCount: 1,
  };
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.previewDeviceReregistration();

  await presenter.requestDeviceReregistration();

  assert.equal(port.preflightCalls, 1);
  assert.equal(port.dangerousActionCalls, 0);
  assert.equal(presenter.getState().confirmation, null);
  assert.deepEqual(presenter.getState().deviceReregistrationPreflight, {
    kind: "blocked",
    blockers: derivePendingWorkBlockers(port.pending),
  });
  assert.equal(presenter.getState().statusCode, "pending-local-data");

  port.pending = safePending();
  await presenter.requestDeviceReregistration();

  assert.equal(port.preflightCalls, 2);
  assert.equal(port.dangerousActionCalls, 0);
  assert.equal(presenter.getState().confirmation?.kind, "reregister-device");
  assert.deepEqual(presenter.getState().deviceReregistrationPreflight, {
    kind: "ready",
  });
});

test("换店提交 side-channel 显示必须重启并保持 busy，销毁后退订", async () => {
  const port = new FakeSettingsPort();
  port.deviceReregistrationHold = new Promise<SettingsDangerousActionResult>(
    () => undefined,
  );
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.previewDeviceReregistration();
  await presenter.requestDeviceReregistration();

  void presenter.confirmDangerousAction();
  await Promise.resolve();
  assert.equal(port.dangerousActionCalls, 1);
  assert.equal(presenter.getState().busy, true);
  assert.equal(port.deviceReregistrationCommittedListenerCount, 1);

  port.publishDeviceReregistrationCommitted();
  assert.equal(
    presenter.getState().statusCode,
    "device-reregister-restart-required",
  );
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().busy, true);

  port.publishDeviceReregistrationCommitted();
  void presenter.confirmDangerousAction();
  assert.equal(port.dangerousActionCalls, 1);
  assert.equal(port.deviceReregistrationCommittedListenerCount, 1);

  presenter.destroy();
  assert.equal(port.deviceReregistrationCommittedListenerCount, 0);
});

test("其他动作执行中重复申请换店不会伪造 checking 或启动第二次预检", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.previewDeviceReregistration();
  port.holdScannerUntilAbort = true;

  const scannerInFlight = presenter.testScanner();
  await Promise.resolve();
  const reregisterRequest = presenter.requestDeviceReregistration();

  assert.deepEqual(presenter.getState().deviceReregistrationPreflight, {
    kind: "idle",
  });
  assert.equal(port.preflightCalls, 0);

  presenter.destroy();
  await Promise.all([scannerInFlight, reregisterRequest]);
});

test("修改开通码会清除旧阻断详情且不会保留已翻译文案", async () => {
  const port = new FakeSettingsPort();
  port.pending = safePending({ pendingSaleCount: 2 });
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.previewDeviceReregistration();
  await presenter.requestDeviceReregistration();
  assert.equal(
    presenter.getState().deviceReregistrationPreflight.kind,
    "blocked",
  );

  presenter.setDeviceActivationCode(`${deviceActivationCode}X`);

  assert.deepEqual(presenter.getState().deviceReregistrationPreflight, {
    kind: "idle",
  });
  assert.equal(presenter.getState().deviceActivationPreview, null);
  assert.equal(presenter.getState().statusCode, null);
  assert.equal(
    JSON.stringify(presenter.getState()).includes("待同步销售"),
    false,
  );
});

test("最终确认出现新业务时用最新 blockers 返回详情且不执行换店", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.previewDeviceReregistration();
  await presenter.requestDeviceReregistration();
  assert.equal(presenter.getState().confirmation?.kind, "reregister-device");

  port.pending = safePending({ pendingReturnCount: 2 });
  await presenter.confirmDangerousAction();

  assert.deepEqual(port.reregistrations, []);
  assert.deepEqual(presenter.getState().deviceReregistrationPreflight, {
    kind: "blocked",
    blockers: [
      { kind: "count", code: "pending-returns", count: 2 },
    ],
  });
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "pending-local-data");
});

test("更换分店预检失败映射 safety-check-failed 且不会打开确认", async () => {
  const port = new FakeSettingsPort();
  port.failSafety = true;
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.previewDeviceReregistration();

  await presenter.requestDeviceReregistration();

  assert.deepEqual(presenter.getState().deviceReregistrationPreflight, {
    kind: "failed",
  });
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "safety-check-failed");
});

test("清除设备注册必须先展示影响说明，并只在最终确认时传递瞬时员工条码", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  assert.equal(presenter.requestDeviceRegistrationReset(), true);
  assert.deepEqual(presenter.getState().confirmation, {
    kind: "reset-device-registration",
  });
  assert.deepEqual(port.deviceResetBarcodes, []);

  await presenter.confirmDangerousAction(" 9900000000001 ");

  assert.deepEqual(port.deviceResetBarcodes, ["9900000000001"]);
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(
    presenter.getState().statusCode,
    "device-registration-reset-completed",
  );
});

test("任何待同步、未决支付、活动购物车或耐久写入都会阻断危险操作并保留本地数据", async () => {
  const pendingCases: SettingsPendingDataSnapshot[] = [
    safePending({ paymentConfigurationSensitiveOrderCount: 1 }),
    safePending({ pendingSaleCount: 1 }),
    safePending({ pendingReturnCount: 1 }),
    safePending({ unresolvedPaymentCount: 1 }),
    safePending({ pendingDurableWriteCount: 1 }),
    safePending({ hasActiveCart: true }),
    safePending({ hasSyncOrAuditInFlight: true }),
    safePending({ hasFulfilmentInFlight: true }),
  ];

  for (const pending of pendingCases) {
    const port = new FakeSettingsPort();
    port.pending = pending;
    const presenter = createPresenter(port);
    await presenter.load();
    assert.equal(presenter.requestCatalogReset(), true);

    await presenter.confirmDangerousAction();

    assert.equal(port.catalogResetCalls, 0);
    assert.equal(port.dangerousActionCalls, 1);
    assert.equal(presenter.getState().statusCode, "pending-local-data");
    assert.equal(presenter.getState().confirmation, null);
    assert.deepEqual(port.pending, pending);
  }
});

test("未决支付会阻断支付配置切换且不会持久化新环境", async () => {
  const port = new FakeSettingsPort();
  port.pending = safePending({ unresolvedPaymentCount: 1 });
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setSquareEnvironment("Sandbox");
  presenter.setSquareLocationId("sq-location-1");
  presenter.setSquareDeviceId("sq-device-1");
  await presenter.savePaymentSettings();
  assert.equal(
    presenter.getState().confirmation?.kind,
    "change-payment-settings",
  );

  await presenter.confirmDangerousAction();

  assert.deepEqual(port.savedPayments, []);
  assert.equal(presenter.getState().statusCode, "pending-local-data");
  assert.equal(presenter.getState().square.environment, "Production");
});

test("安全检查失败时 fail closed，异常详情不会进入 UI 状态", async () => {
  const port = new FakeSettingsPort();
  port.failSafety = true;
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.previewDeviceReregistration();
  await presenter.requestDeviceReregistration();

  await presenter.confirmDangerousAction();

  assert.deepEqual(port.reregistrations, []);
  assert.equal(port.preflightCalls, 1);
  assert.equal(port.dangerousActionCalls, 0);
  assert.equal(presenter.getState().statusCode, "safety-check-failed");
  assert.equal(
    JSON.stringify(presenter.getState()).includes("Bearer secret"),
    false,
  );
});

test("确认期间锁定底层设置与其他确认，取消后才恢复编辑", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();
  const originalApiAddress = presenter.getState().apiAddressDraft;

  assert.equal(presenter.requestCatalogReset(), true);
  assert.equal(presenter.selectPane("payments"), false);
  presenter.setApiAddressDraft("https://should-not-apply.example.com");
  assert.equal(presenter.getState().apiAddressDraft, originalApiAddress);
  assert.equal(presenter.requestAppRestart(), false);
  assert.equal(presenter.getState().confirmation?.kind, "reset-catalog");

  presenter.cancelConfirmation();
  assert.equal(presenter.selectPane("payments"), true);
  presenter.setApiAddressDraft("https://allowed.example.com");
  assert.equal(
    presenter.getState().apiAddressDraft,
    "https://allowed.example.com",
  );
});

test("destroy 会 abort 等待中的扫码并让端口释放硬件监听", async () => {
  const port = new FakeSettingsPort();
  port.holdScannerUntilAbort = true;
  const presenter = createPresenter(port);
  await presenter.load();

  const testInFlight = presenter.testScanner();
  await Promise.resolve();
  presenter.destroy();
  await testInFlight;

  assert.equal(port.scannerAbortObserved, true);
});

test("API 地址拒绝凭据、query 与 fragment，且不会打开确认框", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  for (const invalid of [
    "not-a-url",
    "http://example.com/pos-api",
    "https://user:pass@example.com/pos-api",
    "https://example.com/pos-api?token=secret",
    "https://example.com/pos-api#secret",
  ]) {
    presenter.setApiAddressDraft(invalid);
    assert.equal(presenter.requestApiAddressChange(), false);
    assert.equal(presenter.getState().statusCode, "invalid-api-address");
    assert.equal(presenter.getState().confirmation, null);
  }

  presenter.setApiAddressDraft("http://192.168.31.246:5003/pos-api/");
  assert.equal(presenter.requestApiAddressChange(), true);
  assert.deepEqual(presenter.getState().confirmation, {
    kind: "change-api-address",
    apiBaseUrl: "http://192.168.31.246:5003/pos-api",
  });
});

test("本地后端允许使用受信任的局域网 IP 与 5003 端口", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setApiAddressDraft("http://192.168.31.246:5003");

  assert.equal(presenter.requestApiAddressChange(), true);
  assert.deepEqual(presenter.getState().confirmation, {
    kind: "change-api-address",
    apiBaseUrl: "http://192.168.31.246:5003",
  });
});

test("旧本地后端 5159 端口在客户端迁移期间仍可使用", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setApiAddressDraft("http://192.168.31.246:5159");

  assert.equal(presenter.requestApiAddressChange(), true);
  assert.deepEqual(presenter.getState().confirmation, {
    kind: "change-api-address",
    apiBaseUrl: "http://192.168.31.246:5159",
  });
});

test("测试候选 API 只检查规范地址并显示结果，不切换当前地址", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setApiAddressDraft("http://192.168.31.246:5003/");
  await presenter.testApiAddress();

  assert.deepEqual(port.apiAddressTests, ["http://192.168.31.246:5003"]);
  assert.deepEqual(port.apiAddressChanges, []);
  assert.equal(
    presenter.getState().apiBaseUrl,
    "https://hotbargain.vip/pos-api",
  );
  assert.equal(
    presenter.getState().apiAddressDraft,
    "http://192.168.31.246:5003",
  );
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "api-health-check-passed");

  port.failApiHealth = true;
  await presenter.testApiAddress();
  assert.equal(
    presenter.getState().apiAddressDraft,
    "http://192.168.31.246:5003",
  );
  assert.equal(presenter.getState().statusCode, "api-health-check-failed");
});

test("候选 API 健康检查失败时保留旧地址", async () => {
  const port = new FakeSettingsPort();
  port.failApiHealth = true;
  const presenter = createPresenter(port);
  await presenter.load();

  presenter.setApiAddressDraft("https://offline.example.com/pos-api");
  assert.equal(presenter.requestApiAddressChange(), true);
  await presenter.confirmDangerousAction();

  assert.deepEqual(port.apiAddressChanges, []);
  assert.equal(
    presenter.getState().apiAddressDraft,
    "https://hotbargain.vip/pos-api",
  );
  assert.equal(
    presenter.getState().apiBaseUrl,
    "https://hotbargain.vip/pos-api",
  );
  assert.equal(presenter.getState().statusCode, "api-health-check-failed");
});

test("目录下载与硬件测试使用单航班并返回安全结果", async () => {
  let releaseCatalog!: () => void;
  const port = new FakeSettingsPort();
  port.catalogHold = new Promise<void>((resolve) => {
    releaseCatalog = resolve;
  });
  const presenter = createPresenter(port);
  await presenter.load();

  const firstDownload = presenter.downloadCatalog();
  const secondDownload = presenter.downloadCatalog();
  assert.equal(firstDownload, secondDownload);
  assert.equal(port.catalogDownloadCalls, 1);
  releaseCatalog();
  await firstDownload;
  assert.equal(presenter.getState().catalog.snapshotId, "catalog-new");

  await presenter.testPrinter();
  await presenter.testScanner();

  assert.equal(port.printerTestCalls, 1);
  assert.equal(port.scannerTestCalls, 1);
  assert.equal(
    presenter.getState().hardware.lastScannerValue,
    "••••0001 · 12 chars",
  );
  assert.equal(
    JSON.stringify(presenter.getState()).includes("930000000001"),
    false,
  );
});

test("设置呈现器立即恢复共享目录进度，销毁只退订且不取消刷新", async () => {
  let releaseCatalog!: () => void;
  const port = new FakeSettingsPort();
  port.catalogHold = new Promise<void>((resolve) => {
    releaseCatalog = resolve;
  });
  port.publishCatalogRefresh({
    kind: "running",
    storeCode: "BNE-01",
    progress: catalogProgress({
      currentStep: "products",
      elapsedMilliseconds: 76_000,
      overallPercent: 35,
      steps: [
        { step: "prepare", percent: 100 },
        {
          step: "products",
          percent: 25,
          completedItemCount: 500,
          totalItemCount: 2_000,
          completedPageCount: 1,
          totalPageCount: 4,
        },
        { step: "promotions", percent: 0 },
        { step: "activate", percent: 0 },
      ],
    }),
  });
  const presenter = createPresenter(port);

  const recoveredRefresh = presenter.getState().catalogRefresh;
  assert.equal(recoveredRefresh.kind, "running");
  assert.equal(
    recoveredRefresh.kind === "running"
      ? recoveredRefresh.progress.elapsedMilliseconds
      : null,
    76_000,
  );
  await presenter.load();

  const download = presenter.downloadCatalog();
  await Promise.resolve();
  presenter.destroy();

  assert.equal(port.catalogRefreshListenerCount, 0);
  assert.equal(port.catalogDownloadSignal?.aborted, false);
  releaseCatalog();
  await download;
});

test("共享目录刷新状态持续同步；成功更新摘要，失败只暴露稳定安全码", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();

  port.publishCatalogRefresh({
    kind: "success",
    storeCode: "BNE-01",
    summary: {
      snapshotId: "catalog-background",
      catalogVersion: "v-background",
      itemCount: 81,
      activatedAt: "2026-07-29T01:00:00.000Z",
    },
    progress: catalogProgress({
      currentStep: "activate",
      elapsedMilliseconds: 91_000,
      overallPercent: 100,
      steps: [
        { step: "prepare", percent: 100 },
        { step: "products", percent: 100 },
        { step: "promotions", percent: 100 },
        { step: "activate", percent: 100 },
      ],
    }),
  });

  assert.equal(presenter.getState().catalog.snapshotId, "catalog-background");
  assert.equal(presenter.getState().catalog.itemCount, 81);
  assert.equal(presenter.getState().catalogRefresh.kind, "success");

  port.publishCatalogRefresh({
    kind: "failed",
    storeCode: "BNE-01",
    errorCode: "catalog-refresh-network-failed",
    progress: catalogProgress({
      elapsedMilliseconds: 94_000,
    }),
  });
  assert.equal(presenter.getState().catalogRefresh.kind, "failed");
  assert.equal(
    JSON.stringify(presenter.getState()).includes("Bearer secret"),
    false,
  );
});

test("目录刷新中阻断所有会重绑运行时的危险操作，但不锁定页签", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();
  port.publishCatalogRefresh({
    kind: "running",
    storeCode: "BNE-01",
    progress: catalogProgress(),
  });

  presenter.setApiAddressDraft("https://next.example.test/pos-api");
  assert.equal(presenter.requestApiAddressChange(), false);
  assert.equal(presenter.requestCatalogReset(), false);
  await presenter.savePaymentSettings();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.requestDeviceReregistration();
  assert.equal(presenter.requestAppRestart(), false);
  assert.equal(presenter.getState().statusCode, "safety-check-failed");
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.selectPane("payments"), true);
  assert.equal(port.dangerousActionCalls, 0);
});

test("确认后目录刷新才开始时，执行前再次 fail closed", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();
  presenter.setApiAddressDraft("https://next.example.test/pos-api");
  assert.equal(presenter.requestApiAddressChange(), true);

  port.publishCatalogRefresh({
    kind: "running",
    storeCode: "BNE-01",
    progress: catalogProgress(),
  });
  await presenter.confirmDangerousAction();

  assert.equal(port.dangerousActionCalls, 0);
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "safety-check-failed");
});

test("缺少细分权限时写操作 fail closed", async () => {
  const port = new FakeSettingsPort();
  const presenter = new SettingsPresenter({
    permissions: [SETTINGS_VIEW_PERMISSION],
    port,
  });
  await presenter.load();

  presenter.setSquareEnvironment("Sandbox");
  await presenter.savePaymentSettings();
  presenter.setPrinterEnabled(false);
  await presenter.savePrinterSettings();
  presenter.requestCatalogReset();
  presenter.setDeviceActivationCode(deviceActivationCode);
  await presenter.requestDeviceReregistration();

  assert.deepEqual(port.savedPayments, []);
  assert.deepEqual(port.savedPrinters, []);
  assert.equal(presenter.getState().confirmation, null);
  assert.equal(presenter.getState().statusCode, "permission-required");
});

function createPresenter(port: FakeSettingsPort): SettingsPresenter {
  return new SettingsPresenter({ permissions: allPermissions, port });
}

function safePending(
  patch: Partial<SettingsPendingDataSnapshot> = {},
): SettingsPendingDataSnapshot {
  return {
    hasActiveCart: false,
    hasFulfilmentInFlight: false,
    hasSyncOrAuditInFlight: false,
    paymentConfigurationSensitiveOrderCount: 0,
    pendingDurableWriteCount: 0,
    pendingReturnCount: 0,
    pendingSaleCount: 0,
    unresolvedPaymentCount: 0,
    ...patch,
  };
}

const MANAGED_PRINTER: ReceiptPrinterSettings = {
  ...DEFAULT_RECEIPT_PRINTER_SETTINGS,
  printEnabled: true,
  drawerEnabled: true,
  peripheralId: "XP-N160I",
  paper: "80mm",
  locale: "en",
  brandName: "Hot Bargain",
  storeName: "Bankstown",
  address: "1 Main St",
  phone: "02 1234 5678",
  abn: "12 345 678 901",
  returnPolicy: "14 days.",
  profileStoreCode: "BNE-01",
  profileVersion: 3,
  profileAckedVersion: 3,
};

async function managedPresenter(port = new FakeSettingsPort()) {
  port.snapshotValue = { ...snapshot(), printer: MANAGED_PRINTER };
  const presenter = createPresenter(port);
  await presenter.load();
  return { port, presenter };
}

test("已应用总部下发资料（profileVersion>0）：六项资料只读，硬件设置照常可改", async () => {
  const { presenter } = await managedPresenter();
  const before = presenter.getState().printer;

  presenter.setReceiptBrandName("Edited brand");
  presenter.setReceiptStoreName("Edited store");
  presenter.setReceiptAddress("Edited address");
  presenter.setReceiptPhone("0000");
  presenter.setReceiptAbn("00 000 000 000");
  presenter.setReceiptReturnPolicy("Edited policy");
  assert.deepEqual(presenter.getState().printer, before, "六项资料不能被改动");

  presenter.setPrinterPaper("58mm");
  presenter.setPrinterLocale("zh-CN");
  presenter.setDrawerEnabled(false);
  assert.equal(presenter.getState().printer.paper, "58mm");
  assert.equal(presenter.getState().printer.locale, "zh-CN");
  assert.equal(presenter.getState().printer.drawerEnabled, false);
  assert.equal(presenter.getState().printer.profileVersion, 3);
});

test("未下发（profileVersion=0）时六项资料仍可手工编辑，行为与旧版一致", async () => {
  const port = new FakeSettingsPort();
  const presenter = createPresenter(port);
  await presenter.load();
  assert.equal(presenter.getState().printer.profileVersion, 0);

  presenter.setReceiptBrandName("Manual brand");
  presenter.setReceiptStoreName("Manual store");
  presenter.setReceiptAddress("Manual address");
  presenter.setReceiptPhone("0400");
  presenter.setReceiptAbn("11 111 111 111");
  presenter.setReceiptReturnPolicy("Manual policy");
  const printer = presenter.getState().printer;
  assert.equal(printer.brandName, "Manual brand");
  assert.equal(printer.storeName, "Manual store");
  assert.equal(printer.address, "Manual address");
  assert.equal(printer.phone, "0400");
  assert.equal(printer.abn, "11 111 111 111");
  assert.equal(printer.returnPolicy, "Manual policy");
});

test("立即同步成功：只替换六项资料与下发版本，保留尚未保存的硬件草稿，状态为已更新", async () => {
  const { port, presenter } = await managedPresenter();
  presenter.setPrinterPaper("58mm");
  port.syncResult = {
    status: "updated",
    version: 4,
    printer: {
      ...MANAGED_PRINTER,
      paper: "80mm",
      brandName: "New brand",
      storeName: "New store",
      address: "2 New St\nSydney",
      phone: "02 9999 0000",
      abn: "99 999 999 999",
      returnPolicy: "7 days.",
      profileVersion: 4,
      profileAckedVersion: 4,
    },
  };

  await presenter.syncReceiptProfile();

  const printer = presenter.getState().printer;
  assert.equal(port.syncCalls, 1);
  assert.equal(presenter.getState().statusCode, "receipt-profile-synced");
  assert.equal(printer.brandName, "New brand");
  assert.equal(printer.storeName, "New store");
  assert.equal(printer.address, "2 New St\nSydney");
  assert.equal(printer.phone, "02 9999 0000");
  assert.equal(printer.abn, "99 999 999 999");
  assert.equal(printer.returnPolicy, "7 days.");
  assert.equal(printer.profileVersion, 4);
  assert.equal(printer.profileAckedVersion, 4);
  // 用户尚未保存的硬件草稿不能被同步结果覆盖
  assert.equal(printer.paper, "58mm");
  // 同步本身不写本机设置（写入由同步控制器原子完成）
  assert.equal(port.savedPrinters.length, 0);
});

test("立即同步：已是最新、总部还没下发过，各自给出对应状态，且不改草稿", async () => {
  const { port, presenter } = await managedPresenter();
  const before = presenter.getState().printer;

  port.syncResult = { status: "up-to-date", version: 3, printer: MANAGED_PRINTER };
  await presenter.syncReceiptProfile();
  assert.equal(presenter.getState().statusCode, "receipt-profile-up-to-date");
  assert.deepEqual(presenter.getState().printer, before);

  port.syncResult = { status: "not-published" };
  await presenter.syncReceiptProfile();
  assert.equal(presenter.getState().statusCode, "receipt-profile-not-published");
  assert.deepEqual(presenter.getState().printer, before);
});

test("立即同步失败：按原因给出离线/设备认证未通过/服务器未支持/资料无效/通用失败，草稿保持不变", async () => {
  const { port, presenter } = await managedPresenter();
  const before = presenter.getState().printer;
  const expectations = [
    ["offline", "receipt-profile-sync-offline"],
    ["unauthorized", "receipt-profile-sync-forbidden"],
    ["unsupported", "receipt-profile-sync-unsupported"],
    ["invalid", "receipt-profile-sync-invalid"],
    ["failed", "receipt-profile-sync-failed"],
  ] as const;
  for (const [reason, statusCode] of expectations) {
    port.syncResult = { status: "failed", reason };
    await presenter.syncReceiptProfile();
    assert.equal(presenter.getState().statusCode, statusCode, reason);
    assert.deepEqual(presenter.getState().printer, before);
  }

  port.failSync = true;
  await presenter.syncReceiptProfile();
  assert.equal(presenter.getState().statusCode, "receipt-profile-sync-failed");
  assert.deepEqual(presenter.getState().printer, before);

  // 同步结果里的资料不合规（控制字符）：整体丢弃，草稿保持不变
  port.failSync = false;
  port.syncResult = {
    status: "updated",
    version: 5,
    printer: { ...MANAGED_PRINTER, returnPolicy: "Unsafe\u001b@", profileVersion: 5 },
  };
  await presenter.syncReceiptProfile();
  assert.equal(presenter.getState().statusCode, "receipt-profile-sync-invalid");
  assert.deepEqual(presenter.getState().printer, before);
});

test("运行时没有提供同步能力时按失败处理，设置页本身缺少「设置小票打印机」权限时不发同步，均不会误报已是最新", async () => {
  const { port, presenter } = await managedPresenter();
  port.omitSync = true;
  const withoutSync = createPresenter(port);
  await withoutSync.load();
  await withoutSync.syncReceiptProfile();
  assert.equal(withoutSync.getState().statusCode, "receipt-profile-sync-failed");

  const noPermission = new SettingsPresenter({
    permissions: [SETTINGS_VIEW_PERMISSION],
    port,
  });
  await noPermission.load();
  await noPermission.syncReceiptProfile();
  assert.equal(noPermission.getState().statusCode, "permission-required");
  assert.equal(port.syncCalls, 0);
  void presenter;
});

test("已下发后旧的「载入门店资料」不再覆盖只读资料，一律改走同步；未下发时仍走旧载入", async () => {
  const { port, presenter } = await managedPresenter();
  port.syncResult = { status: "up-to-date", version: 3, printer: MANAGED_PRINTER };
  port.receiptProfileValue = {
    storeCode: "BNE-01",
    brandName: "Legacy brand",
    storeName: "Legacy store",
    address: "",
    phone: "",
    abn: "",
    returnPolicy: "",
  };

  await presenter.loadReceiptProfile();

  assert.equal(port.receiptProfileCalls, 0, "不得再走旧载入接口");
  assert.equal(port.syncCalls, 1);
  assert.equal(presenter.getState().printer.brandName, "Hot Bargain");

  const legacyPort = new FakeSettingsPort();
  legacyPort.receiptProfileValue = port.receiptProfileValue;
  const legacy = createPresenter(legacyPort);
  await legacy.load();
  await legacy.loadReceiptProfile();
  assert.equal(legacyPort.receiptProfileCalls, 1);
  assert.equal(legacyPort.syncCalls, 0);
  assert.equal(legacy.getState().statusCode, "receipt-profile-loaded");
});

test("已下发时保存硬件设置：六项资料与版本原样随草稿提交（只读字段不被当作用户改动）", async () => {
  const { port, presenter } = await managedPresenter();
  presenter.setPrinterPaper("58mm");

  await presenter.savePrinterSettings();

  assert.equal(port.savedPrinters.length, 1);
  const saved = port.savedPrinters[0];
  assert.equal(saved?.paper, "58mm");
  assert.equal(saved?.brandName, "Hot Bargain");
  assert.equal(saved?.storeName, "Bankstown");
  assert.equal(saved?.address, "1 Main St");
  assert.equal(saved?.profileVersion, 3);
  assert.equal(saved?.profileAckedVersion, 3);
  assert.equal(presenter.getState().statusCode, "printer-settings-saved");
});

test("快照里的脏下发版本号（负数/小数/非数字）按 0 规整，缺失也按 0", async () => {
  for (const dirty of [-1, 1.5, Number.NaN, undefined, "3"]) {
    const port = new FakeSettingsPort();
    port.snapshotValue = {
      ...snapshot(),
      printer: {
        ...MANAGED_PRINTER,
        profileVersion: dirty as unknown as number,
        profileAckedVersion: dirty as unknown as number,
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    assert.equal(presenter.getState().printer.profileVersion, 0);
    assert.equal(presenter.getState().printer.profileAckedVersion, 0);
  }
});

class FakeSettingsPort implements SettingsControlPort {
  public squareSetup: FakeSquareSetupControlPort | undefined;
  public linklySetup: FakeLinklySetupControlPort | undefined;
  public linklyPairing: FakeLinklyPairingPort | undefined;
  public loadCalls = 0;
  public safetyCalls = 0;
  public preflightCalls = 0;
  public dangerousActionCalls = 0;
  public catalogDownloadCalls = 0;
  public catalogResetCalls = 0;
  public printerTestCalls = 0;
  public cashDrawerTestCalls = 0;
  public clearSavedPrinterCalls = 0;
  public scannerTestCalls = 0;
  public restartCalls = 0;
  public catalogHold: Promise<void> | null = null;
  public catalogDownloadSignal: AbortSignal | null = null;
  public catalogRefreshListenerCount = 0;
  public deviceReregistrationCommittedListenerCount = 0;
  private catalogRefreshState: CatalogRefreshState = { kind: "idle" };
  private readonly catalogRefreshListeners = new Set<() => void>();
  private readonly deviceReregistrationCommittedListeners = new Set<
    () => void
  >();
  public failSafety = false;
  public failApiHealth = false;
  public pending = safePending();
  public snapshotValue: SettingsSnapshot | null = null;
  public holdScannerUntilAbort = false;
  public scannerAbortObserved = false;
  public failPrinterConnect = false;
  public printerConnectErrorCode: string | null = null;
  public failPrinterSave = false;
  public failClearSavedPrinter = false;
  public failReceiptProfile = false;
  public receiptProfileValue: SettingsReceiptProfileDraft | null = null;
  public receiptProfileCalls = 0;
  public failSync = false;
  public omitSync = false;
  public syncCalls = 0;
  public syncResult: SettingsReceiptProfileSyncResult = {
    status: "not-published",
  };
  public printerScanError: unknown = null;
  public cashDrawerTestResult: SettingsCashDrawerTestResult = {
    status: "completed",
    errorCode: null,
  };
  public clearSavedPrinterResult: SettingsClearSavedPrinterResult = {
    status: "completed",
    errorCode: null,
  };
  public readonly apiAddressChanges: string[] = [];
  public readonly apiAddressTests: string[] = [];
  public readonly connectedPrinterIds: string[] = [];
  public readonly savedPayments: SettingsPaymentSettingsInput[] = [];
  public readonly savedPaymentMethods: { useManualCard: boolean }[] = [];
  public failPaymentMethodSave = false;
  public readonly paymentTests: Readonly<{
    provider: "square" | "linkly";
    input: SettingsPaymentSettingsInput;
  }>[] = [];
  public readonly paymentTerminalSelections: (
    | SettingsLinklyTerminalSelectionSnapshot
    | null
    | undefined
  )[] = [];
  public readonly savedPrinters: ReceiptPrinterSettings[] = [];
  public readonly printerEvents: string[] = [];
  public printerDevices: readonly Readonly<{
    id: string;
    name: string;
    transport: string;
    preferred: boolean;
  }>[] = [];
  public readonly reregistrations: {
    activationCode: string;
    terminalName?: string;
  }[] = [];
  public readonly activationPreviewRequests: string[] = [];
  public activationPreviewResponse: Awaited<
    ReturnType<NonNullable<SettingsControlPort["previewDeviceActivationCode"]>>
  > | null = null;
  public readonly deviceResetBarcodes: string[] = [];
  public deviceReregistrationHold: Promise<SettingsDangerousActionResult> | null = null;

  public async loadSnapshot(): Promise<SettingsSnapshot> {
    this.loadCalls += 1;
    return this.snapshotValue ?? snapshot();
  }

  public async savePaymentMethodSettings(settings: { useManualCard: boolean }): Promise<SettingsDangerousActionResult> {
    if (this.failPaymentMethodSave) throw new Error("save failed");
    this.savedPaymentMethods.push(settings);
    return { status: "completed", kind: "change-payment-settings" };
  }

  public getCatalogRefreshState() {
    return this.catalogRefreshState;
  }

  public subscribeCatalogRefresh(listener: () => void): () => void {
    this.catalogRefreshListeners.add(listener);
    this.catalogRefreshListenerCount = this.catalogRefreshListeners.size;
    return () => {
      this.catalogRefreshListeners.delete(listener);
      this.catalogRefreshListenerCount = this.catalogRefreshListeners.size;
    };
  }

  public subscribeDeviceReregistrationCommitted(
    listener: () => void,
  ): () => void {
    this.deviceReregistrationCommittedListeners.add(listener);
    this.deviceReregistrationCommittedListenerCount =
      this.deviceReregistrationCommittedListeners.size;
    return () => {
      this.deviceReregistrationCommittedListeners.delete(listener);
      this.deviceReregistrationCommittedListenerCount =
        this.deviceReregistrationCommittedListeners.size;
    };
  }

  public publishDeviceReregistrationCommitted(): void {
    for (const listener of this.deviceReregistrationCommittedListeners) {
      listener();
    }
  }

  public publishCatalogRefresh(
    state: CatalogRefreshState,
  ): void {
    this.catalogRefreshState = state;
    for (const listener of this.catalogRefreshListeners) listener();
  }

  public async testApiAddress(apiBaseUrl: string): Promise<boolean> {
    this.apiAddressTests.push(apiBaseUrl);
    return !this.failApiHealth;
  }

  public async previewDeviceActivationCode(activationCode: string) {
    this.activationPreviewRequests.push(activationCode);
    return this.activationPreviewResponse ?? {
      isAllowed: true,
      storeCode: "BNE-02",
      storeName: "Sunnybank",
      deviceSystem: "Android",
      expiresAtUtc: "2026-08-28T00:00:00.000Z",
    };
  }

  public async preflightDeviceReregistration() {
    this.preflightCalls += 1;
    if (this.failSafety) {
      return {
        status: "blocked" as const,
        reason: "safety-check-failed" as const,
      };
    }
    const blockers = derivePendingWorkBlockers(this.pending);
    return blockers.length > 0
      ? {
          status: "blocked" as const,
          reason: "pending-local-data" as const,
          blockers,
        }
      : { status: "ready" as const };
  }

  public async executeDangerousAction(
    action: SettingsDangerousConfirmation,
    _signal?: AbortSignal,
    employeeBarcode?: string,
  ): Promise<SettingsDangerousActionResult> {
    this.dangerousActionCalls += 1;
    if (this.failSafety) {
      return {
        status: "blocked" as const,
        reason: "safety-check-failed" as const,
      };
    }
    if (
      this.pending.hasActiveCart ||
      this.pending.hasFulfilmentInFlight ||
      this.pending.hasSyncOrAuditInFlight ||
      this.pending.paymentConfigurationSensitiveOrderCount > 0 ||
      this.pending.pendingDurableWriteCount > 0 ||
      this.pending.pendingReturnCount > 0 ||
      this.pending.pendingSaleCount > 0 ||
      this.pending.unresolvedPaymentCount > 0
    ) {
      return {
        status: "blocked" as const,
        reason: "pending-local-data" as const,
        blockers: derivePendingWorkBlockers(this.pending),
      };
    }
    if (action.kind === "change-api-address" && this.failApiHealth) {
      return {
        status: "blocked" as const,
        reason: "candidate-unreachable" as const,
      };
    }
    if (action.kind === "change-api-address") {
      this.apiAddressChanges.push(action.apiBaseUrl);
      return { status: "completed" as const, kind: action.kind };
    }
    if (action.kind === "pair-linkly") {
      const result = await this.linklyPairing?.pair(
        action.environment,
        action.terminalId,
        action.pairCode,
      );
      return result?.status === "unknown"
        ? { status: "unknown" as const, kind: action.kind }
        : { status: "completed" as const, kind: action.kind };
    }
    if (action.kind === "reset-catalog") {
      this.catalogResetCalls += 1;
      return {
        status: "completed" as const,
        kind: action.kind,
        catalog: {
          snapshotId: null,
          itemCount: 0,
          activatedAt: null,
        },
      };
    }
    if (action.kind === "reregister-device") {
      this.reregistrations.push({
        activationCode: action.activationCode,
        ...(action.terminalName ? { terminalName: action.terminalName } : {}),
      });
      if (this.deviceReregistrationHold) {
        return this.deviceReregistrationHold;
      }
      return { status: "completed" as const, kind: action.kind };
    }
    if (action.kind === "reset-device-registration") {
      this.deviceResetBarcodes.push(employeeBarcode?.trim() ?? "");
      return { status: "completed" as const, kind: action.kind };
    }
    if (action.kind === "change-payment-settings") {
      this.savedPayments.push(action.input);
      return { status: "completed" as const, kind: action.kind };
    }
    this.restartCalls += 1;
    return { status: "completed" as const, kind: action.kind };
  }

  public async downloadCatalog(signal: AbortSignal) {
    this.catalogDownloadCalls += 1;
    this.catalogDownloadSignal = signal;
    await this.catalogHold;
    return {
      snapshotId: "catalog-new",
      itemCount: 77,
      activatedAt: "2026-07-28T02:00:00.000Z",
    };
  }

  public async testPaymentProvider(
    provider: "square" | "linkly",
    input: SettingsPaymentSettingsInput,
    _signal?: AbortSignal,
    terminals?: SettingsLinklyTerminalSelectionSnapshot | null,
  ): Promise<void> {
    this.paymentTests.push({ provider, input });
    this.paymentTerminalSelections.push(terminals);
  }

  public async savePrinterSettings(
    input: ReceiptPrinterSettings,
  ): Promise<void> {
    this.savedPrinters.push(input);
    this.printerEvents.push(
      `save:${input.peripheralId ?? "none"}:${input.drawerEnabled}`,
    );
    if (this.failPrinterSave) {
      throw new Error("printer settings save failed");
    }
  }

  public async scanPrinters() {
    if (this.printerScanError) throw this.printerScanError;
    return this.printerDevices;
  }

  public async connectPrinter(peripheralId: string): Promise<void> {
    this.connectedPrinterIds.push(peripheralId);
    if (this.printerConnectErrorCode) {
      throw Object.assign(new Error("printer connect failed"), {
        code: this.printerConnectErrorCode,
      });
    }
    if (this.failPrinterConnect) {
      throw new Error("printer connect failed");
    }
  }

  public async testPrinter(): Promise<void> {
    this.printerTestCalls += 1;
  }

  public async loadReceiptProfile(): Promise<SettingsReceiptProfileDraft | null> {
    this.receiptProfileCalls += 1;
    if (this.failReceiptProfile) throw new Error("receipt profile load failed");
    return this.receiptProfileValue;
  }

  // omitSync 模拟运行时没有提供「立即同步」能力（可选端口缺省）。
  public get syncReceiptProfile(): SettingsControlPort["syncReceiptProfile"] {
    if (this.omitSync) return undefined;
    return async () => {
      this.syncCalls += 1;
      if (this.failSync) throw new Error("receipt profile sync failed");
      return this.syncResult;
    };
  }

  public testCashDrawer: SettingsControlPort["testCashDrawer"] = async () => {
    this.cashDrawerTestCalls += 1;
    this.printerEvents.push("test-cash-drawer");
    return this.cashDrawerTestResult;
  };

  public clearSavedPrinter: SettingsControlPort["clearSavedPrinter"] =
    async () => {
      this.clearSavedPrinterCalls += 1;
      if (this.failClearSavedPrinter) {
        throw new Error("printer settings clear failed");
      }
      return this.clearSavedPrinterResult;
    };

  public async testScanner(signal?: AbortSignal) {
    this.scannerTestCalls += 1;
    if (this.holdScannerUntilAbort) {
      await new Promise<void>((_resolve, reject) => {
        signal?.addEventListener(
          "abort",
          () => {
            this.scannerAbortObserved = true;
            reject(new Error("scanner test aborted"));
          },
          { once: true },
        );
      });
    }
    return { source: "hid" as const, value: "930000000001" };
  }

  public async checkForAppUpdate() {
    return {
      channel: "production",
      currentVersion: "1.0.0",
      availableVersion: "1.1.0",
      updateRequired: false,
      restartAvailable: true,
    };
  }
}

class FakeLinklySetupControlPort implements SettingsLinklySetupControlPort {
  public health = linklyHealth("Production", true);
  public terminals = linklyTerminals("Production", "terminal-1", 2);
  public readHandlers: Partial<
    Record<
      "Sandbox" | "Production",
      () => Promise<SettingsLinklyHealthSnapshot>
    >
  > = {};
  public readonly readEnvironments: ("Sandbox" | "Production")[] = [];
  public readonly readSequence: ("terminals" | "health")[] = [];
  public readonly healthSelections: (
    | SettingsLinklyTerminalSelectionSnapshot
    | null
    | undefined
  )[] = [];
  public readonly selectCalls: Readonly<{
    environment: "Sandbox" | "Production";
    terminalId: string;
    expectedRevision: number;
  }>[] = [];

  public async readState(
    environment: "Sandbox" | "Production",
    _signal?: AbortSignal,
    terminals?: SettingsLinklyTerminalSelectionSnapshot | null,
  ): Promise<SettingsLinklyHealthSnapshot> {
    this.readSequence.push("health");
    this.healthSelections.push(terminals);
    this.readEnvironments.push(environment);
    const handler = this.readHandlers[environment];
    return handler ? handler() : this.health;
  }

  public async readTerminals(
    environment: "Sandbox" | "Production",
  ): Promise<SettingsLinklyTerminalSelectionSnapshot> {
    this.readSequence.push("terminals");
    if (this.terminals.environment === environment) return this.terminals;
    return linklyTerminals(environment, "terminal-1", 2);
  }

  public async selectTerminal(
    environment: "Sandbox" | "Production",
    terminalId: string,
    expectedRevision: number,
  ): Promise<SettingsLinklyTerminalSelectionSnapshot> {
    this.selectCalls.push({ environment, terminalId, expectedRevision });
    this.terminals = Object.freeze({
      ...this.terminals,
      environment,
      selectedTerminalId: terminalId,
      selectionRevision: expectedRevision + 1,
    });
    return this.terminals;
  }
}

class FakeLinklyPairingPort implements SettingsLinklyPairingPort {
  public readonly pairCalls: {
    environment: "Sandbox" | "Production";
    terminalId: string;
    pairCode: string;
  }[] = [];
  public pairResult: SettingsLinklyPairResult = { status: "completed" };

  public async pair(
    environment: "Sandbox" | "Production",
    terminalId: string,
    pairCode: string,
  ): Promise<SettingsLinklyPairResult> {
    this.pairCalls.push({ environment, terminalId, pairCode });
    return this.pairResult;
  }
}

function linklyTerminals(
  environment: "Sandbox" | "Production",
  selectedTerminalId: string | null,
  selectionRevision: number,
): SettingsLinklyTerminalSelectionSnapshot {
  return Object.freeze({
    environment,
    mode: "Active",
    selectedTerminalId,
    selectionRevision,
    terminals: Object.freeze([
      Object.freeze({
        terminalId: "terminal-1",
        laneNo: 1,
        displayName: "Front counter",
        pairingState: "Ready" as const,
        isBusy: false,
        isReady: true,
        lastHealthStatus: "ready",
        lastHealthAt: null,
      }),
      Object.freeze({
        terminalId: "terminal-2",
        laneNo: 2,
        displayName: "Returns",
        pairingState: "Ready" as const,
        isBusy: false,
        isReady: true,
        lastHealthStatus: "ready",
        lastHealthAt: null,
      }),
    ]),
  });
}

function linklyHealth(
  environment: "Sandbox" | "Production",
  isReady: boolean,
  storeCredentialReady = isReady,
): SettingsLinklyHealthSnapshot {
  return {
    environment,
    storeCode: "STORE-01",
    deviceCode: "IPAD-01",
    isReady,
    checks: [
      {
        code: "STORE_CREDENTIAL",
        isReady: storeCredentialReady,
        message: storeCredentialReady ? "ready" : "missing",
      },
      {
        code: "TERMINAL_SECRET",
        isReady,
        message: isReady ? "ready" : "missing",
      },
      {
        code: "TERMINAL_POS_ID",
        isReady,
        message: isReady ? "ready" : "missing",
      },
    ],
  };
}

class FakeSquareSetupControlPort {
  public tokenStatus: SettingsSquareTokenStatus = {
    environment: "Production",
    configured: true,
    enabled: true,
    updatedAt: null,
  };
  public locations: readonly SettingsSquareLocation[] = [];
  public devices: readonly SettingsSquareDevice[] = [];
  public deviceCodes: readonly SettingsSquareDeviceCode[] = [];
  public readonly tokenCalls: SettingsSquareEnvironment[] = [];
  public readonly locationCalls: SettingsSquareEnvironment[] = [];
  public readonly deviceCalls: Readonly<{
    environment: SettingsSquareEnvironment;
    locationId: string;
    signal: AbortSignal;
  }>[] = [];
  public readonly deviceCodeCalls: Readonly<{
    environment: SettingsSquareEnvironment;
    locationId: string;
    signal: AbortSignal;
  }>[] = [];
  public readonly createDeviceCodeCalls: Readonly<{
    environment: SettingsSquareEnvironment;
    locationId: string;
    name: string;
    signal: AbortSignal;
  }>[] = [];
  public readonly refreshDeviceCodeCalls: Readonly<{
    environment: SettingsSquareEnvironment;
    deviceCodeId: string;
    signal: AbortSignal;
  }>[] = [];
  public tokenHandler:
    | ((
        environment: SettingsSquareEnvironment,
        signal: AbortSignal,
      ) => Promise<SettingsSquareTokenStatus>)
    | null = null;
  public locationHandler:
    | ((
        environment: SettingsSquareEnvironment,
        signal: AbortSignal,
      ) => Promise<readonly SettingsSquareLocation[]>)
    | null = null;
  public deviceHandler:
    | ((
        environment: SettingsSquareEnvironment,
        locationId: string,
        signal: AbortSignal,
      ) => Promise<readonly SettingsSquareDevice[]>)
    | null = null;
  public deviceCodeHandler:
    | ((
        environment: SettingsSquareEnvironment,
        locationId: string,
        signal: AbortSignal,
      ) => Promise<readonly SettingsSquareDeviceCode[]>)
    | null = null;
  public createDeviceCodeHandler:
    | ((
        environment: SettingsSquareEnvironment,
        locationId: string,
        name: string,
        signal: AbortSignal,
      ) => Promise<SettingsSquareDeviceCode>)
    | null = null;
  public refreshDeviceCodeHandler:
    | ((
        environment: SettingsSquareEnvironment,
        deviceCodeId: string,
        signal: AbortSignal,
      ) => Promise<SettingsSquareDeviceCode>)
    | null = null;
  public createdDeviceCode: SettingsSquareDeviceCode | null = null;
  public refreshedDeviceCode: SettingsSquareDeviceCode | null = null;

  public async getSquareTokenStatus(
    environment: SettingsSquareEnvironment,
    signal: AbortSignal,
  ): Promise<SettingsSquareTokenStatus> {
    this.tokenCalls.push(environment);
    if (this.tokenHandler) return this.tokenHandler(environment, signal);
    return this.tokenStatus;
  }

  public async listSquareLocations(
    environment: SettingsSquareEnvironment,
    signal: AbortSignal,
  ): Promise<readonly SettingsSquareLocation[]> {
    this.locationCalls.push(environment);
    if (this.locationHandler) {
      return this.locationHandler(environment, signal);
    }
    return this.locations;
  }

  public async listSquareDevices(
    environment: SettingsSquareEnvironment,
    locationId: string,
    signal: AbortSignal,
  ): Promise<readonly SettingsSquareDevice[]> {
    this.deviceCalls.push({ environment, locationId, signal });
    if (this.deviceHandler) {
      return this.deviceHandler(environment, locationId, signal);
    }
    return this.devices;
  }

  public async listSquareDeviceCodes(
    environment: SettingsSquareEnvironment,
    locationId: string,
    signal: AbortSignal,
  ): Promise<readonly SettingsSquareDeviceCode[]> {
    this.deviceCodeCalls.push({ environment, locationId, signal });
    if (this.deviceCodeHandler) {
      return this.deviceCodeHandler(environment, locationId, signal);
    }
    return this.deviceCodes;
  }

  public async createSquareDeviceCode(
    environment: SettingsSquareEnvironment,
    locationId: string,
    name: string,
    signal: AbortSignal,
  ): Promise<SettingsSquareDeviceCode> {
    this.createDeviceCodeCalls.push({
      environment,
      locationId,
      name,
      signal,
    });
    if (this.createDeviceCodeHandler) {
      return this.createDeviceCodeHandler(
        environment,
        locationId,
        name,
        signal,
      );
    }
    if (!this.createdDeviceCode) {
      throw new Error("unexpected create Square device code");
    }
    return this.createdDeviceCode;
  }

  public async getSquareDeviceCode(
    environment: SettingsSquareEnvironment,
    deviceCodeId: string,
    signal: AbortSignal,
  ): Promise<SettingsSquareDeviceCode> {
    this.refreshDeviceCodeCalls.push({
      environment,
      deviceCodeId,
      signal,
    });
    if (this.refreshDeviceCodeHandler) {
      return this.refreshDeviceCodeHandler(
        environment,
        deviceCodeId,
        signal,
      );
    }
    if (!this.refreshedDeviceCode) {
      throw new Error("unexpected refresh Square device code");
    }
    return this.refreshedDeviceCode;
  }
}

function catalogProgress(
  patch: Partial<
    Extract<
      ReturnType<SettingsControlPort["getCatalogRefreshState"]>,
      { kind: "running" }
    >["progress"]
  > = {},
) {
  return {
    currentStep: "prepare" as const,
    overallPercent: 0,
    elapsedMilliseconds: 0,
    steps: [
      { step: "prepare" as const, percent: 0 },
      { step: "products" as const, percent: 0 },
      { step: "promotions" as const, percent: 0 },
      { step: "activate" as const, percent: 0 },
    ],
    ...patch,
  };
}

function snapshot(): SettingsSnapshot {
  return {
    apiBaseUrl: "https://hotbargain.vip/pos-api",
    appUpdate: {
      channel: "production",
      currentVersion: "1.0.0",
      availableVersion: null,
      updateRequired: false,
      restartAvailable: false,
    },
    catalog: {
      snapshotId: "catalog-old",
      itemCount: 42,
      activatedAt: "2026-07-27T01:00:00.000Z",
    },
    device: {
      deviceCode: "POS-01",
      storeCode: "BNE-01",
      storeName: "Brisbane",
      terminalName: "Front",
    },
    hardware: {
      printerStatus: "connected",
      scannerStatus: "ready",
      lastScannerValue: null,
    },
    paymentProvider: "square",
    linkly: {
      available: true,
      blockerCode: null,
      environment: "Production",
    },
    printer: DEFAULT_RECEIPT_PRINTER_SETTINGS,
    square: {
      available: true,
      blockerCode: null,
      environment: "Production",
      deviceId: "sq-device-1",
      locationId: "sq-location-1",
    },
  };
}

function squareLocation(
  id: string,
  name: string,
): SettingsSquareLocation {
  return { id, name, status: "ACTIVE", currency: "AUD", country: "AU" };
}

function squareDevice(
  id: string,
  locationId: string,
  name: string,
  status = "ACTIVE",
): SettingsSquareDevice {
  return {
    id,
    code: null,
    name,
    status,
    locationId,
    sandboxTest: false,
  };
}

function squareDeviceCode(
  id: string,
  deviceId: string | null,
  locationId: string,
  name: string,
  status = "PAIRED",
): SettingsSquareDeviceCode {
  return {
    id,
    code: "ABCD-EFGH",
    status,
    deviceId,
    locationId,
    name,
  };
}

function deferred<T>(): Readonly<{
  promise: Promise<T>;
  resolve(value: T): void;
  reject(error: unknown): void;
}> {
  let resolvePromise!: (value: T) => void;
  let rejectPromise!: (error: unknown) => void;
  const promise = new Promise<T>((resolve, reject) => {
    resolvePromise = resolve;
    rejectPromise = reject;
  });
  return {
    promise,
    resolve: resolvePromise,
    reject: rejectPromise,
  };
}
