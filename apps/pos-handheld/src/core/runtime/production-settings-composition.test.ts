import assert from "node:assert/strict";
import test from "node:test";

import { CurrentCashierSession } from "./current-cashier-session";
import {
  createProductionSettingsComposition,
  type ProductionSettingsCompositionInput,
} from "./production-settings-composition";

import type { ReceiptPrinterSettings } from "@/core/db/pos-settings-repository";
import { PricingCart } from "@/features/sales/domain";
import { ActivePricingCartSession } from "@/features/sales/runtime";

const NOW = "2026-07-28T00:00:00.000Z";

test("生产设置组合只从可信组合根生成快照，并复用同一购物车独占门闩", async () => {
  const events: string[] = [];
  const cashier = activeCashier();
  const activeCart = new ActivePricingCartSession(
    new PricingCart(),
    () => new PricingCart(),
  );
  const input = dependencies({
    currentCashier: cashier,
    activeCart,
    readDevicePresentation: async () => ({
      deviceCode: "IPAD-1",
      storeCode: "S1",
      storeName: "Store One",
      terminalName: "Front",
    }),
    pendingData: {
      read: async () => ({
        hasFulfilmentInFlight: false,
        hasSyncOrAuditInFlight: false,
        paymentConfigurationSensitiveOrderCount: 0,
        pendingDurableWriteCount: 0,
        pendingReturnCount: 0,
        pendingSaleCount: 0,
        unresolvedPaymentCount: 0,
      }),
    },
    apiConfiguration: {
      probe: async () => true,
      save: async () => {
        events.push("api:save");
      },
    },
    runtimeReload: {
      reload: async () => {
        events.push("reload");
      },
    },
  });
  const runtime = createProductionSettingsComposition(input);
  const presenter = runtime.createPresenter();

  await presenter.load();
  assert.equal(presenter.getState().kind, "ready");
  assert.equal(presenter.getState().device.deviceCode, "IPAD-1");
  assert.equal(presenter.getState().catalog.itemCount, 2);

  presenter.setApiAddressDraft("https://next.example.test");
  assert.equal(presenter.requestApiAddressChange(), true);
  await presenter.confirmDangerousAction();
  assert.deepEqual(events, ["api:save", "reload"]);
});

test("生产组合把可选 Square setup 能力转给 Presenter 且不暴露 token", async () => {
  const events: string[] = [];
  const receivedSignals: AbortSignal[] = [];
  const unavailable = async (): Promise<never> => {
    throw new Error("not implemented");
  };
  const runtime = createProductionSettingsComposition(
    dependencies({
      squareSetup: {
        getSquareTokenStatus: async (environment, signal) => {
          events.push(`token:${environment}`);
          receivedSignals.push(signal);
          return {
            environment,
            configured: true,
            enabled: true,
            updatedAt: "2026-08-01T00:00:00Z",
          };
        },
        listSquareLocations: async (environment, signal) => {
          events.push(`locations:${environment}`);
          receivedSignals.push(signal);
          return [
            {
              id: "LOC-1",
              name: "Brisbane",
              status: "ACTIVE",
              currency: "AUD",
              country: "AU",
            },
          ];
        },
        listSquareDevices: unavailable,
        listSquareDeviceCodes: unavailable,
        createSquareDeviceCode: unavailable,
        getSquareDeviceCode: unavailable,
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.loadSquareLocations();

  assert.deepEqual(events, ["token:Sandbox", "locations:Sandbox"]);
  assert.equal(receivedSignals.length, 2);
  assert.equal(receivedSignals[0], receivedSignals[1]);
  assert.deepEqual(presenter.getState().squareSetup.token.value, {
    environment: "Sandbox",
    configured: true,
    enabled: true,
    updatedAt: "2026-08-01T00:00:00Z",
  });
  assert.deepEqual(presenter.getState().squareSetup.locations.items, [
    {
      id: "LOC-1",
      name: "Brisbane",
      status: "ACTIVE",
      currency: "AUD",
      country: "AU",
    },
  ]);
});

test("生产组合隔离 Linkly health 读取与配对写入，并只经危险动作提交一次", async () => {
  const events: string[] = [];
  let paired = false;
  let selectedTerminalId = "terminal-1";
  let selectionRevision = 1;
  const base = dependencies();
  const runtime = createProductionSettingsComposition({
    ...base,
    paymentConfiguration: {
      ...base.paymentConfiguration,
      current: null,
      availability: {
        ...base.paymentConfiguration.availability,
        linkly: {
          available: false,
          blockerCode: "LINKLY_CONFIGURATION_MISSING",
        },
      },
      test: async (_provider, _configuration, _signal, terminals) => {
        assert.equal(terminals?.selectedTerminalId, "terminal-2");
        assert.equal(terminals?.selectionRevision, 2);
        events.push("logon:Production:terminal-2:2");
      },
    },
    linklySetup: {
      readState: async (environment, signal, terminals) => {
        assert.equal(signal.aborted, false);
        assert.equal(terminals?.selectedTerminalId, selectedTerminalId);
        assert.equal(terminals?.selectionRevision, selectionRevision);
        events.push(`health:${environment}:${paired ? "paired" : "unpaired"}`);
        return {
          environment,
          storeCode: "S1",
          deviceCode: "IPAD-1",
          isReady: paired,
          checks: [
            { code: "STORE_CREDENTIAL", isReady: true, message: "ready" },
            { code: "TERMINAL_SECRET", isReady: paired, message: null },
            { code: "TERMINAL_POS_ID", isReady: paired, message: null },
          ],
        };
      },
      readTerminals: async (environment, signal) => {
        assert.equal(signal.aborted, false);
        events.push(`terminals:${environment}:${selectedTerminalId}`);
        return {
          environment,
          mode: "Active",
          selectedTerminalId,
          selectionRevision,
          terminals: [
            {
              terminalId: "terminal-1",
              laneNo: 1,
              displayName: "Front",
              pairingState: "Ready",
              isBusy: false,
              isReady: true,
              lastHealthStatus: "Ready",
              lastHealthAt: null,
            },
            {
              terminalId: "terminal-2",
              laneNo: 2,
              displayName: "Returns",
              pairingState: "Ready",
              isBusy: false,
              isReady: true,
              lastHealthStatus: "Ready",
              lastHealthAt: null,
            },
          ],
        };
      },
      selectTerminal: async (
        environment,
        terminalId,
        expectedRevision,
        signal,
      ) => {
        assert.equal(signal.aborted, false);
        events.push(
          `select:${environment}:${terminalId}:${expectedRevision}`,
        );
        selectedTerminalId = terminalId;
        selectionRevision = expectedRevision + 1;
        return {
          environment,
          mode: "Active",
          selectedTerminalId,
          selectionRevision,
          terminals: [
            {
              terminalId: "terminal-1",
              laneNo: 1,
              displayName: "Front",
              pairingState: "Ready",
              isBusy: false,
              isReady: true,
              lastHealthStatus: "Ready",
              lastHealthAt: null,
            },
            {
              terminalId: "terminal-2",
              laneNo: 2,
              displayName: "Returns",
              pairingState: "Ready",
              isBusy: false,
              isReady: true,
              lastHealthStatus: "Ready",
              lastHealthAt: null,
            },
          ],
        };
      },
      pair: async (environment, terminalId, pairCode, signal) => {
        assert.equal(signal.aborted, false);
        events.push(`pair:${environment}:${terminalId}:${pairCode}`);
        paired = true;
        return { status: "completed" };
      },
    },
  });
  const presenter = runtime.createPresenter();

  await presenter.load();
  assert.equal(presenter.getState().linklySetup?.health.value?.isReady, false);
  assert.equal(
    presenter.getState().linklySetup?.terminals.value?.terminals.length,
    2,
  );
  await presenter.selectLinklyTerminal("terminal-2");
  assert.equal(presenter.requestLinklyPair("123456"), true);
  await presenter.confirmDangerousAction();
  await presenter.testPaymentProvider("linkly");

  assert.deepEqual(events, [
    "terminals:Production:terminal-1",
    "health:Production:unpaired",
    "select:Production:terminal-2:1",
    "pair:Production:terminal-2:123456",
    "terminals:Production:terminal-2",
    "health:Production:paired",
    "logon:Production:terminal-2:2",
  ]);
  assert.equal(presenter.getState().statusCode, "payment-test-passed");
  assert.equal(presenter.getState().linklySetup?.health.value?.isReady, true);
});

test("生产 Linkly 终端切换遇到未决支付时保持零 PUT", async () => {
  let selectCalls = 0;
  const base = dependencies();
  const runtime = createProductionSettingsComposition({
    ...base,
    pendingData: {
      read: async () => ({
        hasFulfilmentInFlight: false,
        hasSyncOrAuditInFlight: false,
        paymentConfigurationSensitiveOrderCount: 0,
        pendingDurableWriteCount: 0,
        pendingReturnCount: 0,
        pendingSaleCount: 0,
        unresolvedPaymentCount: 1,
      }),
    },
    linklySetup: {
      readState: async (environment) => ({
        environment,
        storeCode: "S1",
        deviceCode: "IPAD-1",
        isReady: true,
        checks: [],
      }),
      readTerminals: async (environment) => ({
        environment,
        mode: "Active",
        selectedTerminalId: "terminal-1",
        selectionRevision: 1,
        terminals: [
          {
            terminalId: "terminal-1",
            laneNo: 1,
            displayName: "Front",
            pairingState: "Ready",
            isBusy: false,
            isReady: true,
            lastHealthStatus: "Ready",
            lastHealthAt: null,
          },
          {
            terminalId: "terminal-2",
            laneNo: 2,
            displayName: "Returns",
            pairingState: "Ready",
            isBusy: false,
            isReady: true,
            lastHealthStatus: "Ready",
            lastHealthAt: null,
          },
        ],
      }),
      selectTerminal: async (environment) => {
        selectCalls += 1;
        return {
          environment,
          mode: "Active",
          selectedTerminalId: "terminal-2",
          selectionRevision: 2,
          terminals: [],
        };
      },
      pair: async () => ({ status: "completed" }),
    },
  });
  const presenter = runtime.createPresenter();

  await presenter.load();
  assert.equal(
    presenter.getState().linklySetup?.terminals.value?.terminals.length,
    2,
  );
  await presenter.selectLinklyTerminal("terminal-2");

  assert.equal(selectCalls, 0);
  assert.equal(presenter.getState().statusCode, "linkly-terminal-switch-failed");
});

test("生产组合每次创建 Square 配对码只生成一个幂等键并调用底层 API 一次", async () => {
  let createIdCalls = 0;
  let createCalls = 0;
  let capturedInput: unknown = null;
  const capturedSignals: AbortSignal[] = [];
  const base = dependencies();
  const unavailable = async (): Promise<never> => {
    throw new Error("not implemented");
  };
  const runtime = createProductionSettingsComposition({
    ...base,
    createId: () => {
      createIdCalls += 1;
      return "settings-square-key-1";
    },
    paymentConfiguration: {
      ...base.paymentConfiguration,
      current: {
        provider: "square",
        square: {
          environment: "Production",
          deviceId: "SQ-1",
          locationId: "LOC-1",
        },
        linkly: null,
      },
    },
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
      listSquareDevices: async () => [],
      listSquareDeviceCodes: async () => [],
      createSquareDeviceCode: async (input, signal) => {
        createCalls += 1;
        capturedInput = input;
        capturedSignals.push(signal);
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
  });
  const presenter = runtime.createPresenter();
  await presenter.load();
  await presenter.loadSquareLocations();
  await presenter.loadSquareDeviceCodes();
  presenter.setSquareDeviceCodeNameDraft(" Front register ");

  await presenter.createSquareDeviceCode();

  assert.equal(createIdCalls, 1);
  assert.equal(createCalls, 1);
  assert.deepEqual(capturedInput, {
    environment: "Production",
    idempotencyKey: "settings-square-key-1",
    locationId: "LOC-1",
    name: "Front register",
  });
  assert.equal(capturedSignals[0]?.aborted, false);
  assert.equal(JSON.stringify(capturedInput).includes("token"), false);
});

test("设置快照读取失败时按阶段上报异常，页面仍显示 load-failed", async () => {
  const reported: Array<{ stage: string; error: unknown }> = [];
  const printerError = new Error("printer settings unreadable");
  const runtime = createProductionSettingsComposition(
    dependencies({
      receiptSettings: {
        get: async () => {
          throw printerError;
        },
        save: async () => undefined,
      },
      reportSnapshotFailure: (stage, error) => {
        reported.push({ stage, error });
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  assert.equal(presenter.getState().kind, "failed");
  assert.equal(presenter.getState().statusCode, "load-failed");
  assert.deepEqual(reported, [{ stage: "receipt-settings", error: printerError }]);
});

test("设备范围不一致时上报 device-scope，上报钩子抛错不改变失败语义", async () => {
  const stages: string[] = [];
  const runtime = createProductionSettingsComposition(
    dependencies({
      readDevicePresentation: async () => ({
        deviceCode: "OTHER-DEVICE",
        storeCode: "S1",
        storeName: "Store One",
        terminalName: "Front",
      }),
      reportSnapshotFailure: (stage) => {
        stages.push(stage);
        throw new Error("log sink unavailable");
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  assert.equal(presenter.getState().statusCode, "load-failed");
  assert.deepEqual(stages, ["device-scope"]);
});

test("更新快照校验失败时上报 app-update 阶段，页面仍显示 load-failed", async () => {
  const reported: Array<{ stage: string; error: unknown }> = [];
  const updateError = new Error("Settings public metadata is invalid.");
  const runtime = createProductionSettingsComposition(
    dependencies({
      appUpdate: {
        check: async () => {
          throw updateError;
        },
        snapshot: () => {
          throw updateError;
        },
        restart: async () => true,
      },
      reportSnapshotFailure: (stage, error) => {
        reported.push({ stage, error });
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  assert.equal(presenter.getState().statusCode, "load-failed");
  assert.deepEqual(reported, [{ stage: "app-update", error: updateError }]);
});

test("数据库、退货或支付恢复任一未清零时，危险设置动作保持阻断", async () => {
  let saved = false;
  const runtime = createProductionSettingsComposition(
    dependencies({
      currentCashier: activeCashier(),
      pendingData: {
        read: async () => ({
          hasFulfilmentInFlight: false,
          hasSyncOrAuditInFlight: false,
          paymentConfigurationSensitiveOrderCount: 0,
          pendingDurableWriteCount: 1,
          pendingReturnCount: 0,
          pendingSaleCount: 0,
          unresolvedPaymentCount: 0,
        }),
      },
      apiConfiguration: {
        probe: async () => true,
        save: async () => {
          saved = true;
        },
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();
  presenter.setApiAddressDraft("https://next.example.test");
  presenter.requestApiAddressChange();
  await presenter.confirmDangerousAction();

  assert.equal(saved, false);
  assert.equal(presenter.getState().statusCode, "pending-local-data");
});

test("打印测试只使用已配置外设和无敏感字段的只读测试数据", async () => {
  const events: unknown[] = [];
  const runtime = createProductionSettingsComposition(
    dependencies({
      currentCashier: activeCashier(),
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async (id) => {
          events.push(["connect", id]);
        },
        disconnect: async () => undefined,
        print: async (id, bytes) => {
          events.push(["print", id, Array.from(bytes)]);
          return { status: "printed", errorCode: null };
        },
        subscribe: () => () => undefined,
        open: async () => ({
          status: "completed",
          errorCode: null,
        }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.testPrinter();

  assert.equal(presenter.getState().statusCode, "printer-test-passed");
  assert.deepEqual(events[0], ["connect", "XP-1"]);
  assert.equal((events.at(-1) as unknown[])[0], "print");
});

test("打印机扫描仅把 trim 后精确匹配 printer001 的设备标记为 preferred", async () => {
  const runtime = createProductionSettingsComposition(
    dependencies({
      printer: {
        getStatus: async () => "ready",
        scan: async () => [
          { id: "backup", name: "printer001 backup", rssi: null },
          { id: "preferred", name: " pRiNtEr001 ", rssi: null },
          { id: "similar", name: "printer001-x", rssi: null },
        ],
        connect: async () => undefined,
        disconnect: async () => undefined,
        print: async () => ({ status: "printed", errorCode: null }),
        subscribe: () => () => undefined,
        open: async () => ({ status: "completed", errorCode: null }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.scanPrinters();

  assert.deepEqual(presenter.getState().printerDevices, [
    {
      id: "preferred",
      name: "pRiNtEr001",
      transport: "bluetooth-le",
      preferred: true,
    },
    {
      id: "backup",
      name: "printer001 backup",
      transport: "bluetooth-le",
      preferred: false,
    },
    {
      id: "similar",
      name: "printer001-x",
      transport: "bluetooth-le",
      preferred: false,
    },
  ]);
});

test("组合层把当前门店小票资料载入能力接入设置呈现器", async () => {
  const runtime = createProductionSettingsComposition(
    dependencies({
      receiptProfile: {
        load: async () => ({
          storeCode: "S1",
          brandName: "Hot Bargain",
          storeName: "Store One",
          address: "1 Queen St",
          phone: "07 3000 0000",
          abn: "12 345 678 901",
          returnPolicy: "Refunds within 14 days.",
        }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.loadReceiptProfile();

  assert.equal(presenter.getState().statusCode, "receipt-profile-loaded");
  assert.equal(presenter.getState().printer.profileStoreCode, "S1");
  assert.equal(presenter.getState().printer.returnPolicy, "Refunds within 14 days.");
});

test("设置页测试打印只要求已保存 peripheralId，不受自动打印开关限制", async () => {
  const events: string[] = [];
  const runtime = createProductionSettingsComposition(
    dependencies({
      receiptSettings: {
        get: async () => ({
          printEnabled: false,
          drawerEnabled: false,
          peripheralId: "printer-saved",
          paper: "80mm",
          locale: "en",
          brandName: "",
          storeName: "Store One",
          address: "",
          phone: "",
          abn: "",
          returnPolicy: "",
          profileStoreCode: "S1",
        }),
        save: async () => undefined,
      },
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async (id) => {
          events.push(`connect:${id}`);
        },
        disconnect: async () => undefined,
        print: async () => {
          events.push("print");
          return { status: "printed", errorCode: null };
        },
        subscribe: () => () => undefined,
        open: async () => ({ status: "completed", errorCode: null }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.testPrinter();

  assert.deepEqual(events, ["connect:printer-saved", "print"]);
  assert.equal(presenter.getState().statusCode, "printer-test-passed");
});

test("测试打印用正式销售票据构造 TEST/NOT A SALE 样例且包含政策与机读编码", async () => {
  const prints: Uint8Array[] = [];
  const runtime = createProductionSettingsComposition(
    dependencies({
      receiptSettings: {
        get: async () => ({
          printEnabled: true,
          drawerEnabled: true,
          peripheralId: "printer-saved",
          paper: "80mm",
          locale: "en",
          brandName: "Hot Bargain",
          storeName: "Brisbane",
          address: "1 Queen St",
          phone: "0712345678",
          abn: "12 345 678 901",
          returnPolicy: "Refunds within 14 days.",
          profileStoreCode: "S1",
        }),
        save: async () => undefined,
      },
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async () => undefined,
        disconnect: async () => undefined,
        print: async (_id, bytes) => {
          prints.push(bytes);
          return { status: "printed", errorCode: null };
        },
        subscribe: () => () => undefined,
        open: async () => ({ status: "completed", errorCode: null }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.testPrinter();

  assert.equal(presenter.getState().statusCode, "printer-test-passed");
  assert.equal(prints.length, 1);
  const bytes = prints[0]!;
  const text = new TextDecoder().decode(bytes);
  assert.match(text, /===== TEST =====/);
  assert.match(text, /\*\*\* NOT A SALE \*\*\*/);
  assert.doesNotMatch(text, /Paid/);
  assert.match(text, /Printer test item/);
  assert.match(text, /Payment:/);
  assert.match(text, /Refunds within 14 days\./);
  const raw = Array.from(bytes);
  assert.ok(raw.includes(0x1b));
  assert.ok(raw.includes(0x1d));
});

test("设置页打印结果 unknown 时明确提示且不自动重试", async () => {
  let printCalls = 0;
  const runtime = createProductionSettingsComposition(
    dependencies({
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async () => undefined,
        disconnect: async () => undefined,
        print: async () => {
          printCalls += 1;
          return {
            status: "ambiguous",
            errorCode: "PRINTER_OUTCOME_UNKNOWN",
          };
        },
        subscribe: () => () => undefined,
        open: async () => ({ status: "completed", errorCode: null }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.testPrinter();

  assert.equal(printCalls, 1);
  assert.equal(presenter.getState().statusCode, "printer-test-unknown");
});

test("设置页钱箱测试先保存 draft，再只调用受控动作且不直连原生 open", async () => {
  const events: string[] = [];
  let settings: ReceiptPrinterSettings = {
    printEnabled: true,
    drawerEnabled: false,
    peripheralId: "printer-saved" as string | null,
    paper: "80mm" as const,
    locale: "en" as const,
    brandName: "",
    storeName: "Store One",
    address: "",
    phone: "",
    abn: "",
    returnPolicy: "",
    profileStoreCode: "S1",
  };
  const runtime = createProductionSettingsComposition(
    dependencies({
      receiptSettings: {
        get: async () => settings,
        save: async (input) => {
          events.push(`save:${input.peripheralId}:${input.drawerEnabled}`);
          settings = input;
        },
      },
      cashDrawerTest: {
        execute: async () => {
          events.push(`authorized:${settings.peripheralId}:${settings.drawerEnabled}`);
          return { state: "Completed", errorCode: null };
        },
      },
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async () => undefined,
        disconnect: async () => undefined,
        print: async () => ({ status: "printed", errorCode: null }),
        subscribe: () => () => undefined,
        open: async () => {
          events.push("raw-open");
          return { status: "completed", errorCode: null };
        },
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();
  presenter.setPrinterPeripheralId("printer-current");
  presenter.setDrawerEnabled(true);

  await presenter.testCashDrawer();

  assert.deepEqual(events, [
    "save:printer-current:true",
    "authorized:printer-current:true",
  ]);
  assert.equal(presenter.getState().statusCode, "cash-drawer-test-passed");
});

test("受控钱箱测试将未知与恢复冲突保留为 unknown，且绝不自动重放", async () => {
  for (const state of ["Unknown", "Ambiguous", "recovery-required"] as const) {
    let calls = 0;
    const runtime = createProductionSettingsComposition(
      dependencies({
        cashDrawerTest: {
          execute: async () => {
            calls += 1;
            return { state, errorCode: "DRAWER_OUTCOME_UNKNOWN" };
          },
        },
      }),
    );
    const presenter = runtime.createPresenter();
    await presenter.load();

    await presenter.testCashDrawer();

    assert.equal(calls, 1);
    assert.equal(presenter.getState().statusCode, "cash-drawer-test-unknown");
  }
});

test("受控钱箱测试将拒绝、未配置和硬件失败稳定映射为 failed", async () => {
  for (const state of [
    "denied",
    "not-found",
    "not-retryable",
    "Failed",
  ] as const) {
    const runtime = createProductionSettingsComposition(
      dependencies({
        cashDrawerTest: {
          execute: async () => ({ state, errorCode: "DRAWER_TEST_FAILED" }),
        },
      }),
    );
    const presenter = runtime.createPresenter();
    await presenter.load();

    await presenter.testCashDrawer();

    assert.equal(presenter.getState().statusCode, "cash-drawer-test-failed");
  }
});

test("清除打印机只持久化 null，不绕过 fulfilment hardware tail 主动断开", async () => {
  const events: string[] = [];
  let settings: ReceiptPrinterSettings = {
    printEnabled: true,
    drawerEnabled: true,
    peripheralId: "printer-saved" as string | null,
    paper: "80mm" as const,
    locale: "en" as const,
    brandName: "",
    storeName: "Store One",
    address: "",
    phone: "",
    abn: "",
    returnPolicy: "",
    profileStoreCode: "S1",
  };
  const runtime = createProductionSettingsComposition(
    dependencies({
      receiptSettings: {
        get: async () => settings,
        save: async (input) => {
          events.push(`save:${input.peripheralId ?? "none"}`);
          settings = input;
        },
      },
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async () => undefined,
        disconnect: async () => {
          events.push("disconnect");
        },
        print: async () => ({ status: "printed", errorCode: null }),
        subscribe: () => () => undefined,
        open: async () => ({ status: "completed", errorCode: null }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.clearSavedPrinter();

  assert.deepEqual(events, ["save:none"]);
  assert.equal(settings.peripheralId, null);
  assert.equal(presenter.getState().printer.peripheralId, null);
  assert.equal(presenter.getState().statusCode, "printer-cleared");
});

test("清除打印机持久化失败时不应断开或清空 UI draft", async () => {
  let disconnectCalls = 0;
  const runtime = createProductionSettingsComposition(
    dependencies({
      receiptSettings: {
        get: async () => ({
          printEnabled: true,
          drawerEnabled: true,
          peripheralId: "printer-kept",
          paper: "80mm",
          locale: "en",
          brandName: "",
          storeName: "Store One",
          address: "",
          phone: "",
          abn: "",
          returnPolicy: "",
          profileStoreCode: "S1",
        }),
        save: async () => {
          throw new Error("save failed");
        },
      },
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async () => undefined,
        disconnect: async () => {
          disconnectCalls += 1;
        },
        print: async () => ({ status: "printed", errorCode: null }),
        subscribe: () => () => undefined,
        open: async () => ({ status: "completed", errorCode: null }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  await presenter.clearSavedPrinter();

  assert.equal(disconnectCalls, 0);
  assert.equal(presenter.getState().printer.peripheralId, "printer-kept");
  assert.equal(presenter.getState().statusCode, "printer-clear-failed");
});

test("清除打印机在读取后保存前复核 lease，失效时不保存也不断开", async () => {
  const cashier = activeCashier();
  let releaseRead!: () => void;
  let enterRead!: () => void;
  let blockRead = false;
  let saveCalls = 0;
  let disconnectCalls = 0;
  const readEntered = new Promise<void>((resolve) => {
    enterRead = resolve;
  });
  const readReleased = new Promise<void>((resolve) => {
    releaseRead = resolve;
  });
  const runtime = createProductionSettingsComposition(
    dependencies({
      currentCashier: cashier,
      receiptSettings: {
        get: async () => {
          if (blockRead) {
            enterRead();
            await readReleased;
          }
          return {
            printEnabled: true,
            drawerEnabled: true,
            peripheralId: "printer-kept",
            paper: "80mm",
            locale: "en",
            brandName: "",
            storeName: "Store One",
            address: "",
            phone: "",
            abn: "",
            returnPolicy: "",
            profileStoreCode: "S1",
          };
        },
        save: async () => {
          saveCalls += 1;
        },
      },
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async () => undefined,
        disconnect: async () => {
          disconnectCalls += 1;
        },
        print: async () => ({ status: "printed", errorCode: null }),
        subscribe: () => () => undefined,
        open: async () => ({ status: "completed", errorCode: null }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();
  blockRead = true;

  const clearing = presenter.clearSavedPrinter();
  await readEntered;
  cashier.clear();
  releaseRead();
  await clearing;

  assert.equal(saveCalls, 0);
  assert.equal(disconnectCalls, 0);
  assert.equal(presenter.getState().printer.peripheralId, "printer-kept");
  assert.equal(presenter.getState().statusCode, "printer-clear-failed");
});

test("清除打印机保存生效后 session 变化仍返回完成且不主动断开", async () => {
  const cashier = activeCashier();
  let releaseSave!: () => void;
  let enterSave!: () => void;
  let savedPeripheralId: string | null = "printer-saved";
  let disconnectCalls = 0;
  const saveEntered = new Promise<void>((resolve) => {
    enterSave = resolve;
  });
  const saveReleased = new Promise<void>((resolve) => {
    releaseSave = resolve;
  });
  const runtime = createProductionSettingsComposition(
    dependencies({
      currentCashier: cashier,
      receiptSettings: {
        get: async () => ({
          printEnabled: true,
          drawerEnabled: true,
          peripheralId: savedPeripheralId,
          paper: "80mm",
          locale: "en",
          brandName: "",
          storeName: "Store One",
          address: "",
          phone: "",
          abn: "",
          returnPolicy: "",
          profileStoreCode: "S1",
        }),
        save: async (settings) => {
          savedPeripheralId = settings.peripheralId;
          enterSave();
          await saveReleased;
        },
      },
      printer: {
        getStatus: async () => "ready",
        scan: async () => [],
        connect: async () => undefined,
        disconnect: async () => {
          disconnectCalls += 1;
        },
        print: async () => ({ status: "printed", errorCode: null }),
        subscribe: () => () => undefined,
        open: async () => ({ status: "completed", errorCode: null }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  const clearing = presenter.clearSavedPrinter();
  await saveEntered;
  assert.equal(savedPeripheralId, null);
  cashier.clear();
  releaseSave();
  await clearing;

  assert.equal(disconnectCalls, 0);
  assert.equal(presenter.getState().printer.peripheralId, null);
  assert.equal(presenter.getState().statusCode, "printer-cleared");
});

test("销毁 settings presenter 只退订页面，不中止应用级目录下载", async () => {
  let receivedSignal: AbortSignal | null = null;
  let releaseDownload!: () => void;
  let downloadEntered!: () => void;
  const entered = new Promise<void>((resolve) => { downloadEntered = resolve; });
  const release = new Promise<void>((resolve) => { releaseDownload = resolve; });
  const runtime = createProductionSettingsComposition(
    dependencies({
      catalog: {
        getActiveMetadata: async () => ({
          snapshotId: "catalog-1",
          catalogVersion: "catalog-v1",
          itemCount: 2,
          activatedAt: NOW,
        }),
        getRefreshState: () => ({ kind: "idle" }),
        subscribeRefresh: () => () => undefined,
        runExclusive: (operation) => operation(),
        download: async (signal) => {
          receivedSignal = signal;
          downloadEntered();
          await release;
          return { snapshotId: "catalog-2", itemCount: 3, activatedAt: NOW };
        },
        reset: async () => ({
          snapshotId: "catalog-3",
          itemCount: 4,
          activatedAt: NOW,
        }),
      },
    }),
  );
  const presenter = runtime.createPresenter();
  await presenter.load();

  const download = presenter.downloadCatalog();
  await entered;
  presenter.destroy();
  assert.equal((receivedSignal as unknown as AbortSignal).aborted, false);
  releaseDownload();
  await download;
});

function activeCashier(): CurrentCashierSession {
  const cashier = new CurrentCashierSession();
  const epoch = cashier.beginAuthentication();
  cashier.activate(
    epoch,
    {
      source: "online",
      session: {
        cashierId: "C1",
        cashierName: "Alice",
        userGuid: "U1",
        storeCode: "S1",
        deviceCode: "IPAD-1",
        permissionCodes: [
          "Permissions.PosTerminal.Settings.View",
          "Permissions.PosTerminal.Settings.AppUpdate",
          "Permissions.PosTerminal.Settings.CatalogDownload",
          "Permissions.PosTerminal.Settings.CatalogReset",
          "Permissions.PosTerminal.Settings.PaymentTerminal",
          "Permissions.PosTerminal.Settings.ReceiptPrinter",
          "Permissions.PosTerminal.Settings.DeviceRegistration",
        ],
      },
    },
    { storeCode: "S1", deviceCode: "IPAD-1" },
  );
  return cashier;
}

function dependencies(
  overrides: Partial<ProductionSettingsCompositionInput> = {},
): ProductionSettingsCompositionInput {
  const { apiConfiguration, device, ...otherOverrides } = overrides;
  const activeCart =
    otherOverrides.activeCart ??
    new ActivePricingCartSession(
      new PricingCart(),
      () => new PricingCart(),
    );
  return {
    currentCashier: otherOverrides.currentCashier ?? activeCashier(),
    terminal: { storeCode: "S1", deviceCode: "IPAD-1" },
    activeCart,
    apiBaseUrl: "https://pos.example.test",
    appVersion: "1.0.0",
    updateChannel: "preview",
    createId: () => "settings-operation-1",
    readDevicePresentation: async () => ({
      deviceCode: "IPAD-1",
      storeCode: "S1",
      storeName: "Store One",
      terminalName: "Front",
    }),
    catalog: {
      getActiveMetadata: async () => ({
        snapshotId: "catalog-1",
        catalogVersion: "catalog-v1",
        itemCount: 2,
        activatedAt: NOW,
      }),
      getRefreshState: () => ({ kind: "idle" }),
      subscribeRefresh: () => () => undefined,
      runExclusive: (operation) => operation(),
      download: async () => ({
        snapshotId: "catalog-2",
        itemCount: 3,
        activatedAt: NOW,
      }),
      reset: async () => ({
        snapshotId: "catalog-3",
        itemCount: 4,
        activatedAt: NOW,
      }),
    },
    receiptSettings: {
      get: async () => ({
        printEnabled: true,
        drawerEnabled: true,
        peripheralId: "XP-1",
        paper: "80mm",
        locale: "en",
        brandName: "",
        storeName: "Store One",
        address: "",
        phone: "",
        abn: "",
        returnPolicy: "",
        profileStoreCode: "S1",
      }),
      save: async () => undefined,
    },
    receiptProfile: {
      load: async () => null,
    },
    paymentConfiguration: {
      current: {
        provider: "square",
        square: {
          environment: "Sandbox",
          deviceId: "SQ-1",
          locationId: "LOC-1",
        },
        linkly: null,
      },
      test: async () => undefined,
      save: async () => undefined,
      availability: {
        square: { available: true, blockerCode: null },
        linkly: { available: true, blockerCode: null },
      },
    },
    paymentConfigurationTransition: {
      run: (operation) => operation(),
    },
    pendingData: {
      read: async () => ({
        hasFulfilmentInFlight: false,
        hasSyncOrAuditInFlight: false,
        paymentConfigurationSensitiveOrderCount: 0,
        pendingDurableWriteCount: 0,
        pendingReturnCount: 0,
        pendingSaleCount: 0,
        unresolvedPaymentCount: 0,
      }),
    },
    apiConfiguration: {
      probe: async () => true,
      save: async () => undefined,
      runSwitchGuarded: async (operation) => ({
        blocked: false as const,
        value: await operation(),
      }),
      ...apiConfiguration,
    },
    runtimeReload: {
      reload: async () => undefined,
    },
    device: {
      reregister: async () => undefined,
      resetRegistration: async () => "completed",
      hasRegistrationRecoveryRisk: async () => false,
      ...device,
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
    cashDrawerTest: {
      execute: async () => ({ state: "Completed", errorCode: null }),
    },
    scanner: {
      test: async () => ({ source: "hid", value: "SKU-1" }),
      status: "ready",
    },
    appUpdate: {
      check: async () => ({
        channel: "preview",
        currentVersion: "1.0.0",
        availableVersion: null,
        updateRequired: false,
        restartAvailable: true,
      }),
      snapshot: () => ({
        channel: "preview",
        currentVersion: "1.0.0",
        availableVersion: null,
        updateRequired: false,
        restartAvailable: true,
      }),
      restart: async () => true,
    },
    ...otherOverrides,
  };
}
