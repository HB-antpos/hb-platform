import assert from "node:assert/strict";
import Module from "node:module";
import { beforeEach, test } from "node:test";
import { installPrinterLinkRecorder, type PrinterLinkLogItem } from "./link-diagnostics";
import type { NativeLinkEvent, PrinterStatus, SavedPrinter } from "./types";

function mockModule(name: string, exports: object) {
  const filename = require.resolve(name);
  const module = new Module(filename);
  module.filename = filename;
  module.loaded = true;
  module.exports = exports;
  require.cache[filename] = module;
}

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => { resolve = done; });
  return { promise, resolve };
}

async function run() {
  let saved: SavedPrinter | null;
  let receiptSaved: SavedPrinter | null;
  let nativeStatus: PrinterStatus;
  let events: string[];
  let writeError: Error | null;
  let connectError: Error | null;
  let connectTransports: (string | null | undefined)[];
  let bleSupported: boolean;
  let disconnectError: Error | null;
  let statusReads: number;
  let storageReads: number;
  let printGate: ReturnType<typeof deferred> | null;
  let connectGate: ReturnType<typeof deferred> | null;
  let nativeLinkEvents: NativeLinkEvent[] = [];
  let drainError: Error | null = null;
  let drainCalls = 0;
  let reviewMode = false;
  const platform = { OS: "android" };
  const receipt = { name: "Receipt", address: "receipt" };

  const print = async () => {
    events.push(`print:${nativeStatus.address}`);
    await printGate?.promise;
    if (writeError) throw writeError;
    return true;
  };

  // 仅替换蓝牙与存储边界，实际执行共享 API 和 Zustand 状态转换。
  mockModule("react-native", { Platform: platform });
  mockModule("./native", {
    getPrinterStatus: async () => { statusReads += 1; return { ...nativeStatus }; },
    connectPrinter: async (address: string, transport?: string | null) => {
      events.push(`connect:${address}`);
      connectTransports.push(transport);
      await connectGate?.promise;
      if (connectError) throw connectError;
      nativeStatus = { ...nativeStatus, connected: true, address };
      return true;
    },
    // 新安卓原生包才有：取走原生层缓冲的蓝牙事件（取走即清空）；旧包返回空。
    drainNativeLinkDiagnostics: async () => {
      drainCalls += 1;
      if (drainError) throw drainError;
      const events = nativeLinkEvents;
      nativeLinkEvents = [];
      return events;
    },
    // 新安卓原生包有 BLE GATT 通道；iOS 恒为 BLE。
    isBlePrintingSupported: () => platform.OS === "ios" || bleSupported,
    disconnectPrinter: async () => {
      events.push("disconnect");
      if (disconnectError) throw disconnectError;
      nativeStatus = { ...nativeStatus, connected: false, address: null };
      return true;
    },
    printNativeProductLabel: print,
    printNativeDiscountLabel: print,
    printNativeClearanceLabel: print,
    printNativeBigDiscountLabel: print,
    printNativeWarehouseProductLabel: print,
    printNativeWarehouseLocationLabel: print,
    printRawCommand: print,
  });
  mockModule("./storage", {
    PrinterStorage: {
      getPrinter: async () => { storageReads += 1; return saved; },
      setPrinter: async (printer: SavedPrinter) => { saved = printer; },
      clearPrinter: async () => { saved = null; },
      getReceiptPrinter: async () => receiptSaved,
      setReceiptPrinter: async (printer: SavedPrinter) => { receiptSaved = printer; },
    },
  });
  mockModule("../ios-review/session", { isIosReviewSessionActive: () => reviewMode });
  const api = await import("./api");
  const { usePrinterStore, useReceiptPrinterStore } = await import("./state");
  const payload = { productName: "Test label", barcode: "1234567890128" };

  beforeEach(() => {
    saved = { name: "XP label", address: "label" };
    receiptSaved = receipt;
    nativeStatus = { supported: true, enabled: true, connected: true, address: "label" };
    events = [];
    writeError = null;
    connectError = null;
    connectTransports = [];
    bleSupported = true;
    disconnectError = null;
    statusReads = 0;
    storageReads = 0;
    printGate = null;
    connectGate = null;
    nativeLinkEvents = [];
    drainError = null;
    drainCalls = 0;
    reviewMode = false;
    platform.OS = "android";
    usePrinterStore.setState({ savedPrinter: saved, status: "connected", autoReconnectPaused: false, lastError: null, hydrated: true });
    useReceiptPrinterStore.setState({ savedPrinter: receipt, status: "idle", autoReconnectPaused: false, lastError: null, hydrated: true });
  });

  test("旧安卓原生包不支持 BLE：在任何断连、状态或存储变更前拒绝", async () => {
    bleSupported = false;
    usePrinterStore.setState({ autoReconnectPaused: true, status: "paused" });
    const before = usePrinterStore.getState();
    for (const bonded of [true, false]) {
      await assert.rejects(api.selectPrinter({ name: "XP BLE", address: "ble", bonded, connected: false, transport: "ble" }), { code: "PRINTER_BLE_UNSUPPORTED" });
    }
    assert.deepEqual(events, []);
    assert.equal(statusReads, 0);
    assert.equal(storageReads, 0);
    assert.equal(usePrinterStore.getState(), before);
    assert.equal(saved?.address, "label");
    assert.equal(nativeStatus.address, "label");
  });

  test("安卓 BLE 打印机无需配对直接连接，并保存传输类型供重连使用", async () => {
    await api.selectPrinter({ name: "XP BLE", address: "ble", bonded: false, connected: false, transport: "ble" });
    assert.deepEqual(events, ["disconnect", "connect:ble"]);
    assert.deepEqual(connectTransports, ["ble"]);
    assert.deepEqual(saved, { name: "XP BLE", address: "ble", transport: "ble" });

    // 重启后自动重连沿用保存的类型，不依赖系统是否还缓存该地址是 BLE。
    nativeStatus = { ...nativeStatus, connected: false, address: null };
    await api.connectSavedPrinter();
    assert.deepEqual(connectTransports, ["ble", "ble"]);
  });

  test("iOS BLE 选择继续沿用现有连接与保存路径", async () => {
    platform.OS = "ios";
    await api.selectPrinter({ name: "XP BLE", address: "ble", bonded: true, connected: false, transport: "ble" });
    assert.deepEqual(events, ["disconnect", "connect:ble"]);
    assert.equal(saved?.address, "ble");
  });

  test("旧安卓原生包的 BLE 小票打印机选择在任何断连、状态或存储变更前拒绝", async () => {
    bleSupported = false;
    useReceiptPrinterStore.setState({ status: "connected" });
    const before = useReceiptPrinterStore.getState();
    await assert.rejects(
      api.selectReceiptPrinter({ name: "XP BLE receipt", address: "ble-receipt", bonded: false, connected: false, transport: "ble" }),
      { code: "PRINTER_BLE_UNSUPPORTED" }
    );
    assert.deepEqual(events, []);
    assert.equal(statusReads, 0);
    assert.equal(storageReads, 0);
    assert.deepEqual(useReceiptPrinterStore.getState(), before);
    assert.equal(receiptSaved?.address, "receipt");
  });

  test("未配对经典小票打印机要求先去系统配对，不保存也不连接", async () => {
    const before = useReceiptPrinterStore.getState();
    await assert.rejects(
      api.selectReceiptPrinter({ name: "New receipt", address: "unpaired-receipt", bonded: false, connected: false, transport: "classic" }),
      { code: "PRINTER_PAIRING_REQUIRED" }
    );
    assert.deepEqual(events, []);
    assert.equal(receiptSaved?.address, "receipt");
    assert.deepEqual(useReceiptPrinterStore.getState(), before);
  });

  test("已配对小票打印机选择只保存，不连接或断开现有标签 socket", async () => {
    await api.selectReceiptPrinter({ name: "Bonded receipt", address: "bonded-receipt", bonded: true, connected: false, transport: "classic" });
    assert.deepEqual(events, []);
    assert.equal(receiptSaved?.address, "bonded-receipt");
    assert.equal(nativeStatus.address, "label");
    assert.equal(useReceiptPrinterStore.getState().status, "idle");
  });

  test("BLE 小票打印机无需配对直接保存，测试打印按 BLE 连接", async () => {
    await api.selectReceiptPrinter({ name: "BLE receipt", address: "ble-receipt", bonded: false, connected: false, transport: "ble" });
    assert.deepEqual(events, []);
    assert.deepEqual(receiptSaved, { name: "BLE receipt", address: "ble-receipt", transport: "ble" });

    await api.testReceiptPrinterConnection();
    assert.deepEqual(connectTransports, ["ble"]);
  });

  test("iOS 小票打印机选择保持仅保存行为", async () => {
    platform.OS = "ios";
    await api.selectReceiptPrinter({ name: "iOS receipt", address: "ios-receipt", bonded: false, connected: false, transport: "ble" });
    assert.deepEqual(events, []);
    assert.equal(receiptSaved?.address, "ios-receipt");
    assert.equal(nativeStatus.address, "label");
  });

  test("broken pipe 清除假连接，保留原始失败且不自动重印，下一次打印恢复", async () => {
    writeError = new Error("write failed: EPIPE (Broken pipe)");
    const original = writeError;
    await assert.rejects(api.printProductLabelPayload(payload), (error) => error === original);
    assert.deepEqual(events, ["print:label", "disconnect"]);
    assert.equal(usePrinterStore.getState().status, "disconnected");
    assert.equal(usePrinterStore.getState().lastError, original.message);
    writeError = null;
    await api.printProductLabelPayload(payload);
    assert.deepEqual(events, ["print:label", "disconnect", "connect:label", "print:label"]);
    assert.equal(usePrinterStore.getState().status, "connected");
  });

  test("已 hydration 的热连接打印只调用原生写入，不读取状态或存储", async () => {
    assert.equal((await api.getSavedPrinter())?.address, "label");
    await api.printProductLabelPayload(payload);
    assert.equal(statusReads, 0);
    assert.equal(storageReads, 0);
    assert.deepEqual(events, ["print:label"]);
  });

  test("热连接状态过期时保留失败且不自动重印，下一次扫码才重连", async () => {
    nativeStatus.connected = false;
    writeError = new Error("No Bluetooth printer is connected.");
    await assert.rejects(api.printProductLabelPayload(payload), /No Bluetooth printer/);
    assert.deepEqual(events, ["print:label", "disconnect"]);
    assert.equal(usePrinterStore.getState().status, "disconnected");
    writeError = null;
    await api.printProductLabelPayload(payload);
    assert.deepEqual(events, ["print:label", "disconnect", "connect:label", "print:label"]);
  });

  test("切换标签打印机时连接成功后保存，并向新设备打印", async () => {
    await api.selectPrinter({ name: "Other", address: "other", bonded: true, connected: false });
    assert.equal((await api.getSavedPrinter())?.address, "other");
    await api.printProductLabelPayload(payload);
    assert.deepEqual(events, ["disconnect", "connect:other", "print:other"]);
    assert.equal(storageReads, 0);
  });

  test("未配对经典蓝牙标签打印机要求先去系统配对，不断开当前连接也不覆盖保存", async () => {
    for (const transport of ["classic", "dual", undefined] as const) {
      await assert.rejects(
        api.selectPrinter({ name: "New printer", address: "unpaired", bonded: false, connected: false, transport }),
        { code: "PRINTER_PAIRING_REQUIRED" }
      );
    }
    assert.deepEqual(events, []);
    assert.equal(statusReads, 0);
    assert.equal(saved?.address, "label");
    assert.equal(usePrinterStore.getState().savedPrinter?.address, "label");
    assert.equal(usePrinterStore.getState().status, "connected");
  });

  test("已在系统配对的经典蓝牙连接成功后才保存", async () => {
    connectGate = deferred();
    const selecting = api.selectPrinter({ name: "Paired printer", address: "paired", bonded: true, connected: false, transport: "classic" });
    await new Promise<void>((resolve) => setImmediate(resolve));

    assert.equal(saved?.address, "label");
    assert.deepEqual(events, ["disconnect", "connect:paired"]);
    assert.deepEqual(connectTransports, ["classic"]);

    connectGate.resolve();
    await selecting;
    assert.equal(saved?.address, "paired");
    assert.equal(usePrinterStore.getState().status, "connected");
  });

  test("后台重连只连接已保存设备，不主动唤起系统配对", async () => {
    nativeStatus = { ...nativeStatus, connected: false, address: null };
    connectError = Object.assign(new Error("Pairing is required."), {
      code: "PRINTER_PAIRING_REQUIRED",
    });

    await assert.rejects(api.connectSavedPrinter(), /Pairing is required/);
    assert.deepEqual(events, ["connect:label"]);
    assert.equal(usePrinterStore.getState().status, "error");
  });

  test("手动选择连接等待中被暂停时不保存新设备", async () => {
    connectGate = deferred();
    const selecting = api.selectPrinter({ name: "Other", address: "other", bonded: true, connected: false });
    await new Promise<void>((resolve) => setImmediate(resolve));
    const pausing = api.disconnectCurrentPrinter({ pauseAutoReconnect: true });
    connectGate.resolve();

    await assert.rejects(selecting, /cancelled/);
    await pausing;
    assert.equal(saved?.address, "label");
    assert.equal(usePrinterStore.getState().savedPrinter?.address, "label");
    assert.equal(usePrinterStore.getState().status, "paused");
  });

  test("旧 iOS 原生包写入超时后也丢弃会话，重试前重新连接且不自动重印", async () => {
    writeError = Object.assign(new Error("Bluetooth printer write timed out."), { code: "PRINT_TIMEOUT" });
    const original = writeError;
    await assert.rejects(api.printProductLabelPayload(payload), (error) => error === original);
    assert.deepEqual(events, ["print:label", "disconnect"]);
    assert.equal(usePrinterStore.getState().status, "disconnected");
    writeError = null;
    await api.printProductLabelPayload(payload);
    assert.deepEqual(events, ["print:label", "disconnect", "connect:label", "print:label"]);
  });

  test("标签内容错误不误判为断线或重连", async () => {
    writeError = new Error("Barcode content is invalid");
    await assert.rejects(api.printProductLabelPayload(payload), /Barcode content/);
    assert.deepEqual(events, ["print:label"]);
    assert.equal(usePrinterStore.getState().status, "connected");
  });

  test("价签更新的折扣标签断线后也清除旧连接，下一次打印重连", async () => {
    writeError = new Error("write failed: EPIPE (Broken pipe)");
    await assert.rejects(api.printDiscountLabelPayload(payload), /Broken pipe/);
    assert.deepEqual(events, ["print:label", "disconnect"]);
    assert.equal(usePrinterStore.getState().status, "disconnected");
    writeError = null;
    await api.printDiscountLabelPayload(payload);
    assert.deepEqual(events, ["print:label", "disconnect", "connect:label", "print:label"]);
    assert.equal(usePrinterStore.getState().status, "connected");
  });

  test("清理失败也不能恢复假连接状态，自动连接仍会建立新 socket", async () => {
    writeError = new Error("Broken pipe");
    disconnectError = new Error("Disconnect failed");
    await assert.rejects(api.printProductLabelPayload(payload), /Broken pipe/);
    assert.equal((await api.syncPrinterStatus()).connected, false);
    assert.equal(usePrinterStore.getState().status, "disconnected");
    disconnectError = null;
    writeError = null;
    await api.connectSavedPrinter();
    assert.equal((await api.syncPrinterStatus()).connected, true);
    assert.deepEqual(events, ["print:label", "disconnect", "connect:label"]);
  });

  test("自动重连与打印同时发生时只连接一次", async () => {
    nativeStatus.connected = false;
    connectGate = deferred();
    const reconnect = api.connectSavedPrinter({ status: "reconnecting" });
    const printing = api.printProductLabelPayload(payload);
    await new Promise<void>((resolve) => setImmediate(resolve));
    const eventsBeforeConnected = [...events];
    connectGate.resolve();
    await Promise.all([reconnect, printing]);
    assert.deepEqual(eventsBeforeConnected, ["connect:label"]);
    assert.deepEqual(events, ["connect:label", "print:label"]);
  });

  test("小票测试等待标签写入完成，之后标签打印重新连接正确设备", async () => {
    printGate = deferred();
    const printing = api.printProductLabelPayload(payload);
    await new Promise<void>((resolve) => setImmediate(resolve));
    const receiptTest = api.testReceiptPrinterConnection();
    await new Promise<void>((resolve) => setImmediate(resolve));
    const eventsDuringPrint = [...events];
    printGate.resolve();
    await Promise.all([printing, receiptTest]);
    assert.deepEqual(eventsDuringPrint, ["print:label"]);
    assert.deepEqual(events, ["print:label", "connect:receipt", "print:receipt", "disconnect"]);
    assert.equal(usePrinterStore.getState().autoReconnectPaused, false);
    await api.printProductLabelPayload(payload);
    assert.deepEqual(events.slice(-2), ["connect:label", "print:label"]);
  });

  test("排队中的自动重连不会覆盖用户暂停", async () => {
    printGate = deferred();
    const printing = api.printProductLabelPayload(payload);
    await new Promise<void>((resolve) => setImmediate(resolve));
    const reconnect = api.connectSavedPrinter({ status: "reconnecting" });
    const disconnect = api.disconnectCurrentPrinter({ pauseAutoReconnect: true });
    printGate.resolve();
    await Promise.all([printing, reconnect, disconnect]);
    assert.equal(events.filter((event) => event.startsWith("connect:")).length, 0);
    assert.equal(usePrinterStore.getState().autoReconnectPaused, true);
    assert.equal(usePrinterStore.getState().status, "paused");
  });

  test("小票测试中手动暂停，测试结束不能恢复之前的自动重连意图", async () => {
    printGate = deferred();
    const receiptTest = api.testReceiptPrinterConnection();
    await new Promise<void>((resolve) => setImmediate(resolve));
    const disconnect = api.disconnectCurrentPrinter({ pauseAutoReconnect: true });
    printGate.resolve();
    await Promise.all([receiptTest, disconnect]);
    assert.equal(usePrinterStore.getState().autoReconnectPaused, true);
    assert.equal(usePrinterStore.getState().status, "paused");
  });

  test("重连失败释放操作队列并更新错误，恢复后可重连", async () => {
    nativeStatus.connected = false;
    connectError = new Error("Connection timed out");
    await assert.rejects(api.connectSavedPrinter(), /Connection timed out/);
    assert.equal(usePrinterStore.getState().status, "error");
    assert.equal(usePrinterStore.getState().lastError, connectError.message);
    connectError = null;
    await api.connectSavedPrinter();
    assert.equal(usePrinterStore.getState().status, "connected");
  });

  test("审核模式保持模拟打印，不读写真实蓝牙", async () => {
    reviewMode = true;
    await api.printProductLabelPayload(payload);
    await api.connectSavedPrinter();
    await api.testReceiptPrinterConnection();
    assert.deepEqual(events, []);
  });

  // 蓝牙链路诊断：用真实的 api.ts + 真实记录器，只替换原生边界，验证打点位置与内容。
  function installRecorder(emitted: PrinterLinkLogItem[]) {
    let clock = 1_000_000;
    let ids = 0;
    installPrinterLinkRecorder({
      now: () => (clock += 1_000),
      newId: () => `00000000-0000-4000-8000-${String((ids += 1)).padStart(12, "0")}`,
      emit: (item) => emitted.push(item),
      context: () => ({ environment: "test", appVersion: "1.0.10+63" }),
    });
  }

  type LoggedEvent = { kind: string; trigger?: string; bluetoothEnabled?: boolean; source?: string; code?: string };

  test("蓝牙链路诊断：自动重连连续失败与恢复各生成一条日志，带触发来源、错误码与蓝牙状态", async () => {
    const emitted: PrinterLinkLogItem[] = [];
    installRecorder(emitted);
    try {
      nativeStatus = { ...nativeStatus, connected: false, address: null };
      connectError = Object.assign(new Error("read failed, socket might closed or timeout, read ret: -1"), { code: "CONNECT_ERROR" });
      for (let attempt = 0; attempt < 3; attempt += 1) {
        await assert.rejects(api.connectSavedPrinter({ status: "reconnecting" }), /read failed/);
      }
      assert.equal(emitted.length, 1, "第 3 次失败才上报一次，不是每次失败都上报");
      const failing = emitted[0];
      assert.equal(failing.category, "printer.link");
      assert.equal(failing.level, "Warning");
      assert.equal(failing.properties.phase, "failing");
      assert.equal(failing.properties.failures, 3);
      assert.equal(failing.properties.address, "label");
      assert.deepEqual(
        (failing.properties.errors as { code: string; count: number }[]).map((error) => [error.code, error.count]),
        [["CONNECT_ERROR", 3]],
      );
      const starts = (failing.properties.events as LoggedEvent[]).filter((event) => event.kind === "connect.start");
      assert.equal(starts.length, 3);
      assert.ok(starts.every((event) => event.trigger === "auto" && event.bluetoothEnabled === true));

      connectError = null;
      await api.connectSavedPrinter({ status: "reconnecting" });
      assert.equal(emitted.length, 2);
      assert.equal(emitted[1].properties.phase, "recovered");
      assert.equal(emitted[1].properties.failures, 3);
    } finally {
      installPrinterLinkRecorder(null);
    }
  });

  test("蓝牙链路诊断：打印时才发现断线记为 link.lost，随后手动重连失败的触发来源是 print/manual", async () => {
    const emitted: PrinterLinkLogItem[] = [];
    installRecorder(emitted);
    try {
      writeError = new Error("write failed: EPIPE (Broken pipe)");
      await assert.rejects(api.printProductLabelPayload(payload), /Broken pipe/);
      writeError = null;
      connectError = Object.assign(new Error("Bluetooth is turned off."), { code: "BLUETOOTH_DISABLED" });
      await assert.rejects(api.printProductLabelPayload(payload), /turned off/);
      await assert.rejects(api.connectSavedPrinter(), /turned off/);
      await assert.rejects(api.connectSavedPrinter(), /turned off/);
      assert.equal(emitted.length, 1);
      const events = emitted[0].properties.events as LoggedEvent[];
      assert.ok(events.some((event) => event.kind === "link.lost" && event.source === "print"), "必须带上断线来源");
      assert.deepEqual(
        events.filter((event) => event.kind === "connect.fail").map((event) => [event.trigger, event.code]),
        [["print", "BLUETOOTH_DISABLED"], ["manual", "BLUETOOTH_DISABLED"], ["manual", "BLUETOOTH_DISABLED"]],
      );
    } finally {
      installPrinterLinkRecorder(null);
    }
  });

  test("蓝牙链路诊断：未安装记录器时连接与打印行为不变", async () => {
    installPrinterLinkRecorder(null);
    nativeStatus = { ...nativeStatus, connected: false, address: null };
    await api.connectSavedPrinter({ status: "reconnecting" });
    assert.deepEqual(events, ["connect:label"]);
    assert.equal(usePrinterStore.getState().status, "connected");
  });
  test("蓝牙链路诊断：原生层事件在连接失败时并入快照，按原始时间排在失败之前", async () => {
    const emitted: PrinterLinkLogItem[] = [];
    installRecorder(emitted);
    try {
      nativeStatus = { ...nativeStatus, connected: false, address: null };
      connectError = Object.assign(new Error("read failed, socket might closed or timeout, read ret: -1"), { code: "CONNECT_ERROR" });
      for (let attempt = 0; attempt < 3; attempt += 1) {
        // 每次失败前原生层都缓冲了这次尝试的现场；第一次还带着更早发生的 ACL 断开。
        nativeLinkEvents = [
          ...(attempt === 0 ? [{ ev: "acl.disconnected", atMs: 500_000, address: "label", tracked: true, socketConnected: true }] : []),
          { ev: "connect.error", atMs: 1_000_000 + attempt, address: "label", elapsedMs: 5_450, error: "IOException: read failed", acl: null },
        ] as NativeLinkEvent[];
        await assert.rejects(api.connectSavedPrinter({ status: "reconnecting" }), /read failed/);
      }
      assert.equal(emitted.length, 1);
      const events = emitted[0].properties.events as (LoggedEvent & { t: number; ev?: string; elapsedMs?: number })[];
      const times = events.map((event) => event.t);
      assert.deepEqual(times, [...times].sort((a, b) => a - b), "必须按真实发生时间升序");
      const diag = events.filter((event) => event.kind === "native.diag");
      assert.ok(diag.length >= 3, "三次失败的原生事件都应并入");
      assert.equal(diag[0].ev, "acl.disconnected", "更早发生的 ACL 断开排在最前");
      assert.ok(diag.some((event) => event.ev === "connect.error" && event.elapsedMs === 5_450));
      assert.equal(emitted[0].properties.failures, 3, "原生事件不计入失败次数");
      assert.ok(drainCalls >= 3, "每次连接结束都要取走原生缓冲");
    } finally {
      installPrinterLinkRecorder(null);
    }
  });

  test("蓝牙链路诊断：取原生事件失败不影响连接结果与原始错误", async () => {
    const emitted: PrinterLinkLogItem[] = [];
    installRecorder(emitted);
    try {
      nativeStatus = { ...nativeStatus, connected: false, address: null };
      drainError = new Error("native bridge exploded");
      connectError = Object.assign(new Error("Connection timed out"), { code: "CONNECT_ERROR" });
      for (let attempt = 0; attempt < 3; attempt += 1) {
        await assert.rejects(api.connectSavedPrinter({ status: "reconnecting" }), (error) => error === connectError);
      }
      assert.equal(emitted.length, 1, "原生事件取不到，日志照常上报");
      assert.equal(
        (emitted[0].properties.events as LoggedEvent[]).filter((event) => event.kind === "native.diag").length,
        0,
      );

      drainError = null;
      connectError = null;
      await api.connectSavedPrinter({ status: "reconnecting" });
      assert.equal(usePrinterStore.getState().status, "connected", "诊断异常不能让连接失败");
    } finally {
      installPrinterLinkRecorder(null);
    }
  });

  test("蓝牙链路诊断：连接成功时也取走原生事件，恢复快照带上完整经过", async () => {
    const emitted: PrinterLinkLogItem[] = [];
    installRecorder(emitted);
    try {
      nativeStatus = { ...nativeStatus, connected: false, address: null };
      connectError = Object.assign(new Error("read failed, socket might closed or timeout, read ret: -1"), { code: "CONNECT_ERROR" });
      for (let attempt = 0; attempt < 3; attempt += 1) {
        await assert.rejects(api.connectSavedPrinter({ status: "reconnecting" }), /read failed/);
      }
      connectError = null;
      nativeLinkEvents = [{ ev: "acl.connected", atMs: 1_500_000, address: "label", tracked: false, socketConnected: null }] as NativeLinkEvent[];
      await api.connectSavedPrinter({ status: "reconnecting" });
      assert.equal(emitted.length, 2);
      assert.equal(emitted[1].properties.phase, "recovered");
      const events = emitted[1].properties.events as (LoggedEvent & { ev?: string })[];
      assert.ok(events.some((event) => event.kind === "native.diag" && event.ev === "acl.connected"), "恢复前的 ACL 连上要在快照里");
    } finally {
      installPrinterLinkRecorder(null);
    }
  });

}

void run();
