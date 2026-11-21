import assert from "node:assert/strict";

import {
  type BleAdapterState,
  type BleCharacteristicInfo,
  BleScannerController,
  type BleScannerScan,
  type BleScannerStatus,
  type BleScannerTransport,
} from "./ble-scanner-controller";
import { normalizeBleUuid, type SavedBleScanner } from "./ble-scanner-core";

/* ---------------- 假蓝牙层：按背夹实测的服务表模拟 ---------------- */

const SCANNER_ID = "D2DA1459-BCE1-0B6C-B7A7-8DE157AAD5CF";

const YICHIP_CHARACTERISTICS: readonly BleCharacteristicInfo[] = [
  { serviceUuid: "1804", uuid: "2A07", notifiable: true, readable: true },
  { serviceUuid: "180F", uuid: "2A19", notifiable: true, readable: true },
  { serviceUuid: "180A", uuid: "2A29", notifiable: false, readable: true },
  { serviceUuid: "FEEA", uuid: "2AA1", notifiable: true, readable: true },
  { serviceUuid: "FEEA", uuid: "FEC9", notifiable: false, readable: true },
  { serviceUuid: "FEEA", uuid: "2AA2", notifiable: false, readable: false },
];

const UNKNOWN_CHARACTERISTICS: readonly BleCharacteristicInfo[] = [
  { serviceUuid: "180F", uuid: "2A19", notifiable: true, readable: true },
  { serviceUuid: "ABCD", uuid: "AB01", notifiable: true, readable: false },
  { serviceUuid: "ABCD", uuid: "AB02", notifiable: true, readable: false },
];

class FakeTransport implements BleScannerTransport {
  state: BleAdapterState = "on";
  stateListeners = new Set<(state: BleAdapterState) => void>();
  characteristics: readonly BleCharacteristicInfo[] = YICHIP_CHARACTERISTICS;
  pendingConnect: { id: string; resolve: () => void; reject: (error: Error) => void } | null = null;
  connectCalls = 0;
  cancelCalls: string[] = [];
  disconnectListeners = new Map<string, Set<(error: Error | null) => void>>();
  monitors = new Map<string, (bytes: Uint8Array) => void>();
  battery = 70;

  async getState() {
    return this.state;
  }
  onStateChange(listener: (state: BleAdapterState) => void) {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
  }
  setState(state: BleAdapterState) {
    this.state = state;
    this.stateListeners.forEach((listener) => listener(state));
  }
  startScan(onDevice: Parameters<BleScannerTransport["startScan"]>[0]) {
    onDevice({ id: "PRINTER-1", name: "Printer", rssi: -40, serviceUuids: [] });
    onDevice({ id: "PHONE", name: "iPhone sean", rssi: -50, serviceUuids: [] });
    onDevice({ id: SCANNER_ID, name: "BarCode Scanner BLE", rssi: -62, serviceUuids: ["FEEA"] });
    onDevice({ id: "ANON", name: null, rssi: -30, serviceUuids: [] });
    return () => undefined;
  }
  connect(id: string) {
    this.connectCalls += 1;
    // 挂起式连接：直到测试里调用 wake() 才完成。
    return new Promise<void>((resolve, reject) => {
      this.pendingConnect = { id, resolve, reject };
    });
  }
  async wake() {
    const pending = this.pendingConnect;
    this.pendingConnect = null;
    pending?.resolve();
    await flush();
  }
  async cancelConnection(id: string) {
    this.cancelCalls.push(id);
    this.monitors.clear();
  }
  onDisconnected(id: string, listener: (error: Error | null) => void) {
    const set = this.disconnectListeners.get(id) ?? new Set();
    set.add(listener);
    this.disconnectListeners.set(id, set);
    return () => set.delete(listener);
  }
  async sleep() {
    this.monitors.clear();
    this.disconnectListeners.get(SCANNER_ID)?.forEach((listener) =>
      listener(Object.assign(new Error("timed out"), { code: 6 })),
    );
    await flush();
  }
  async discover() {
    return this.characteristics;
  }
  monitor(id: string, serviceUuid: string, characteristicUuid: string, onValue: (bytes: Uint8Array) => void) {
    const key = normalizeBleUuid(characteristicUuid);
    this.monitors.set(key, onValue);
    return () => {
      if (this.monitors.get(key) === onValue) this.monitors.delete(key);
    };
  }
  emit(characteristicUuid: string, text: string) {
    const onValue = this.monitors.get(normalizeBleUuid(characteristicUuid));
    assert.ok(onValue, `没有订阅 ${characteristicUuid}`);
    onValue(Uint8Array.from(Array.from(text, (character) => character.charCodeAt(0))));
  }
  async read() {
    return Uint8Array.of(this.battery);
  }
  async readRssi() {
    return -62;
  }
}

class MemoryStorage {
  value: SavedBleScanner | null = null;
  saves = 0;
  async load() {
    return this.value;
  }
  async save(value: SavedBleScanner | null) {
    this.saves += 1;
    this.value = value;
  }
}

async function flush() {
  for (let index = 0; index < 10; index += 1) await Promise.resolve();
}

function createController(transport: FakeTransport, storage: MemoryStorage, clock: { now: number }) {
  const statuses: BleScannerStatus[] = [];
  const scans: BleScannerScan[] = [];
  const controller = new BleScannerController({
    transport,
    storage,
    now: () => clock.now,
    // 拼帧计时器在测试里不需要（每个码都带 CR）。
    setTimer: () => 0,
    clearTimer: () => undefined,
    setRepeatingTimer: () => 0,
    clearRepeatingTimer: () => undefined,
  });
  controller.subscribeStatus((status) => statuses.push(status));
  controller.subscribeScans((scan) => scans.push(scan));
  return { controller, statuses, scans };
}

// tsx 以 CJS 运行本仓库测试，不支持顶层 await，全部用例放进 main。
async function main(): Promise<void> {
// 1. 搜索：排除已保存的打印机、无名设备；扫码枪排第一。
{
  const transport = new FakeTransport();
  const { controller } = createController(transport, new MemoryStorage(), { now: 0 });
  let devices: readonly { id: string }[] = [];
  controller.startDiscovery((next) => (devices = next), { excludeIds: ["PRINTER-1"] });
  assert.deepEqual(
    devices.map((device) => device.id),
    [SCANNER_ID, "PHONE"],
  );
}

// 2. 配对已知型号 → 直连 FEEA/2AA1 → ready；电量读到 70%；扫码带 CR CR 只出一个码。
{
  const clock = { now: 1_000 };
  const transport = new FakeTransport();
  const storage = new MemoryStorage();
  const { controller, scans } = createController(transport, storage, clock);
  await controller.start();
  assert.equal(controller.getStatus().state, "not-paired");

  const pairing = controller.pair({
    id: SCANNER_ID,
    name: "BarCode Scanner BLE",
    rssi: -62,
    serviceUuids: ["FEEA"],
  });
  await flush();
  assert.equal(controller.getStatus().state, "connecting");
  assert.equal(storage.value?.profileId, "yichip-feea", "已知型号配对时立即保存");
  await transport.wake();
  await pairing;
  await flush();
  const ready = controller.getStatus();
  assert.equal(ready.state, "ready");
  assert.equal(ready.batteryLevel, 70);
  assert.equal(ready.rssi, -62);
  assert.ok(!transport.monitors.has(normalizeBleUuid("2A07")), "已知型号只订阅数据通道");

  transport.emit("2AA1", "9528503822012\r\r");
  assert.equal(scans.length, 1);
  assert.equal(scans[0]?.kind, "barcode");
  assert.equal(scans[0]?.frame.value, "9528503822012");
  assert.equal(controller.getStatus().lastScanValue, "9528503822012");

  // 设置码 / 设备回复照样交给订阅方，由各端决定只进测试页。
  transport.emit("2AA1", "HW:V1.00;SW:HD210_V2Nx_V1.8.7.0;\n\r");
  assert.equal(scans.at(-1)?.kind, "device-reply");
  assert.equal(controller.getStatus().lastScanValue, "9528503822012", "设备回复不覆盖最近一次条码");

  // 3. 长时间没扫码后断开 → sleeping（不算异常）→ 挂起连接 → 唤醒自动连回。
  clock.now += 307_000;
  const connectsBefore = transport.connectCalls;
  await transport.sleep();
  assert.equal(controller.getStatus().state, "sleeping");
  assert.equal(controller.getStatus().attention, false, "休眠不计入需处理");
  assert.equal(transport.connectCalls, connectsBefore + 1, "断开后立即挂起新的连接请求");
  await transport.wake();
  await flush();
  assert.equal(controller.getStatus().state, "ready");
  transport.emit("2AA1", "9528503822014\r\r");
  assert.equal(scans.at(-1)?.frame.value, "9528503822014", "重连后数据正常");

  // 4. 刚扫完就断开 → reconnecting；持续超过阈值才标需处理。
  clock.now += 5_000;
  await transport.sleep();
  assert.equal(controller.getStatus().state, "reconnecting");
  assert.equal(controller.getStatus().attention, false);
  clock.now += 61_000;
  transport.setState("on"); // 触发一次状态重算不应改变 reconnecting
  await flush();

  // 5. 蓝牙关闭 → bluetooth-off；打开后自动重新挂起连接。
  transport.setState("off");
  assert.equal(controller.getStatus().state, "bluetooth-off");
  transport.setState("on");
  await flush();
  await transport.wake();
  await flush();
  assert.equal(controller.getStatus().state, "ready");

  // 6. 起备注名、手动断开不再自动重连、忘记后清存储。
  await controller.setAlias("1 号背夹");
  assert.equal(storage.value?.alias, "1 号背夹");
  await controller.disconnect();
  assert.equal(controller.getStatus().state, "disconnected");
  assert.ok(transport.cancelCalls.includes(SCANNER_ID));
  await controller.forget();
  assert.equal(storage.value, null);
  assert.equal(controller.getStatus().state, "not-paired");
  controller.dispose();
}

// 7. 未知型号：订阅全部 notify 候选（跳过电池），第一个出码的特征学会为数据通道并保存。
{
  const clock = { now: 0 };
  const transport = new FakeTransport();
  transport.characteristics = UNKNOWN_CHARACTERISTICS;
  const storage = new MemoryStorage();
  const { controller, scans } = createController(transport, storage, clock);
  await controller.start();
  const pairing = controller.pair({ id: "X1", name: "Generic", rssi: -50, serviceUuids: [] });
  await flush();
  assert.equal(storage.value, null, "学会通道之前不落盘");
  await transport.wake();
  await pairing;
  await flush();
  assert.equal(controller.getStatus().state, "learning");
  assert.ok(!transport.monitors.has(normalizeBleUuid("2A19")), "电池通道不当候选");
  assert.ok(transport.monitors.has(normalizeBleUuid("AB01")));
  assert.ok(transport.monitors.has(normalizeBleUuid("AB02")));

  transport.emit("AB02", "12345678\r");
  await flush();
  assert.equal(controller.getStatus().state, "ready");
  assert.equal((await storage.load())?.notifyUuid, normalizeBleUuid("AB02"));
  assert.equal(scans.length, 1);

  transport.emit("AB01", "NOISE\r");
  await flush();
  assert.equal(scans.length, 1, "学会之后其他候选的数据忽略");
  controller.dispose();
}

// 8. 启动时有保存的扫码枪：直接挂起连接，不需要先搜索。
{
  const clock = { now: 0 };
  const transport = new FakeTransport();
  const storage = new MemoryStorage();
  storage.value = Object.freeze({
    version: 1,
    peripheralId: SCANNER_ID,
    name: "BarCode Scanner BLE",
    alias: null,
    profileId: "yichip-feea",
    serviceUuid: normalizeBleUuid("FEEA"),
    notifyUuid: normalizeBleUuid("2AA1"),
    autoReconnect: true,
    savedAt: "2026-11-21T00:00:00.000Z",
  });
  const { controller } = createController(transport, storage, clock);
  await controller.start();
  await flush();
  assert.equal(transport.connectCalls, 1);
  assert.equal(controller.getStatus().state, "connecting");
  await transport.wake();
  await flush();
  assert.equal(controller.getStatus().state, "ready");
  controller.dispose();
}

console.log("ble-scanner-controller: ok");
}

main().catch((error: unknown) => {
  console.error(error);
  process.exit(1);
});
