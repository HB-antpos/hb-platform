import assert from "node:assert/strict";

import {
  type BleAdapterState,
  type BleCharacteristicInfo,
  type BleDiscoveredDevice,
  BleScannerController,
  type BleScannerTransport,
} from "./ble-scanner-controller";
import { normalizeBleUuid, type SavedBleScanner } from "./ble-scanner-core";

/**
 * 休眠唤醒自动重连（2026-11-22 用户反馈：扫码枪休眠后按扳机，App 不会自己连回，
 * 必须去设置页点「重新连接」）。
 *
 * 假蓝牙层按真机特性建模：
 * - 扫码枪睡着时发出的挂起连接**永远不会完成**（iOS ble-plx 对同一设备重复 connect 时会把
 *   前一个请求连带取消，新请求再也等不到回调；Android autoConnect 也可能很慢）。
 *   只有扫码枪醒着、正在广播时发起的连接才会成功。
 * - cancelConnection 让该设备所有未完成的连接请求以「已取消」失败（与 ble-plx 一致）。
 * - 停止扫描是全局的（ble-plx 的 stopDeviceScan 不分是谁开的扫描）。
 */

const SCANNER_ID = "D2DA1459-BCE1-0B6C-B7A7-8DE157AAD5CF";
const WINDOW_MS = 3_000;
const INTERVAL_MS = 5_000;

const CHARACTERISTICS: readonly BleCharacteristicInfo[] = [
  { serviceUuid: "180F", uuid: "2A19", notifiable: true, readable: true },
  { serviceUuid: "FEEA", uuid: "2AA1", notifiable: true, readable: true },
];

const SAVED: SavedBleScanner = Object.freeze({
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

type PendingConnect = {
  id: string;
  resolve: () => void;
  reject: (error: Error) => void;
};

type ActiveScan = { onDevice: (device: BleDiscoveredDevice) => void };

class SleepyTransport implements BleScannerTransport {
  awake = true;
  connectCalls = 0;
  cancelCalls = 0;
  scanStarts = 0;
  /** 同一时刻未完成的连接请求数的最大值：必须始终 ≤ 1。 */
  maxConcurrentConnects = 0;
  readonly pending: PendingConnect[] = [];
  readonly scans = new Set<ActiveScan>();
  readonly disconnectListeners = new Set<(error: Error | null) => void>();
  connected = false;

  async getState(): Promise<BleAdapterState> {
    return "on";
  }
  onStateChange() {
    return () => undefined;
  }
  startScan(onDevice: (device: BleDiscoveredDevice) => void) {
    this.scanStarts += 1;
    const scan: ActiveScan = { onDevice };
    this.scans.add(scan);
    // 扫描开始时如果扫码枪正在广播，马上能看到它。
    if (this.awake && !this.connected) queueMicrotask(() => this.advertiseTo(scan));
    // ble-plx 的停止扫描是全局的：谁调用都会停掉当前唯一的扫描。
    return () => this.scans.clear();
  }
  connect(id: string) {
    this.connectCalls += 1;
    return new Promise<void>((resolve, reject) => {
      const entry: PendingConnect = { id, resolve, reject };
      this.pending.push(entry);
      this.maxConcurrentConnects = Math.max(this.maxConcurrentConnects, this.pending.length);
      if (this.awake) {
        // 扫码枪醒着：连接很快完成。
        queueMicrotask(() => this.complete(entry));
      }
      // 睡着时发出的请求永远挂着（真机上的问题所在），直到被 cancelConnection 取消。
    });
  }
  async cancelConnection(id: string) {
    this.cancelCalls += 1;
    for (const entry of this.pending.splice(0)) {
      if (entry.id === id) entry.reject(Object.assign(new Error("Operation was cancelled"), { code: 2 }));
    }
  }
  onDisconnected(_id: string, listener: (error: Error | null) => void) {
    this.disconnectListeners.add(listener);
    return () => this.disconnectListeners.delete(listener);
  }
  async discover() {
    return CHARACTERISTICS;
  }
  monitor() {
    return () => undefined;
  }
  async read() {
    return Uint8Array.of(80);
  }
  async readRssi() {
    return -60;
  }

  /** 扫码枪休眠：断开链路，停止广播。 */
  async sleep() {
    this.awake = false;
    this.connected = false;
    [...this.disconnectListeners].forEach((listener) =>
      listener(Object.assign(new Error("timed out"), { code: 6 })),
    );
    await flush();
  }
  /** 按扳机唤醒：开始广播。挂起中的旧连接请求不会因此完成（模拟真机问题）。 */
  async pressTrigger() {
    this.awake = true;
    this.scans.forEach((scan) => this.advertiseTo(scan));
    await flush();
  }

  private advertiseTo(scan: ActiveScan) {
    if (!this.scans.has(scan) || !this.awake || this.connected) return;
    scan.onDevice({ id: "OTHER", name: "iPhone", rssi: -40, serviceUuids: [] });
    scan.onDevice({ id: SCANNER_ID, name: "BarCode Scanner BLE", rssi: -60, serviceUuids: ["FEEA"] });
  }
  private complete(entry: PendingConnect) {
    const index = this.pending.indexOf(entry);
    if (index < 0) return;
    this.pending.splice(index, 1);
    this.connected = true;
    entry.resolve();
  }
}

/** 手动推进的计时器：测试里精确控制扫描窗口与间隔。 */
class ManualClock {
  now = 1_000;
  private nextId = 1;
  private readonly timers = new Map<number, { at: number; callback: () => void }>();
  setTimer = (callback: () => void, ms: number): unknown => {
    const id = this.nextId++;
    this.timers.set(id, { at: this.now + ms, callback });
    return id;
  };
  clearTimer = (handle: unknown): void => {
    this.timers.delete(handle as number);
  };
  async advance(ms: number) {
    const target = this.now + ms;
    for (;;) {
      let nextId: number | null = null;
      let nextAt = Infinity;
      for (const [id, timer] of this.timers) {
        if (timer.at <= target && timer.at < nextAt) {
          nextAt = timer.at;
          nextId = id;
        }
      }
      if (nextId === null) break;
      const timer = this.timers.get(nextId)!;
      this.timers.delete(nextId);
      this.now = timer.at;
      timer.callback();
      await flush();
    }
    this.now = target;
    await flush();
  }
}

async function flush() {
  for (let index = 0; index < 20; index += 1) await Promise.resolve();
}

async function setup() {
  const clock = new ManualClock();
  const transport = new SleepyTransport();
  const storage = {
    value: SAVED as SavedBleScanner | null,
    async load() {
      return this.value;
    },
    async save(value: SavedBleScanner | null) {
      this.value = value;
    },
  };
  const controller = new BleScannerController({
    transport,
    storage,
    now: () => clock.now,
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    setRepeatingTimer: () => 0,
    clearRepeatingTimer: () => undefined,
    wakeScanWindowMs: WINDOW_MS,
    wakeScanIntervalMs: INTERVAL_MS,
  });
  await controller.start();
  await flush();
  assert.equal(controller.getStatus().state, "ready", "启动后连上已保存的扫码枪");
  // 很久没扫码后扫码枪休眠断开。
  clock.now += 307_000;
  await transport.sleep();
  assert.equal(controller.getStatus().state, "sleeping");
  return { clock, transport, controller };
}

const cases: [string, () => Promise<void>][] = [
  [
    "1. 休眠断开后，周期性短扫描；扫到广播就先取消旧请求再连，连上后停止扫描。",
    async () => {
      const { clock, transport, controller } = await setup();
      await clock.advance(INTERVAL_MS);
      assert.equal(transport.scans.size, 1, "休眠期间在前台会周期性短扫描");
      await clock.advance(WINDOW_MS);
      assert.equal(transport.scans.size, 0, "扫描窗口结束就停");
      assert.equal(controller.getStatus().state, "sleeping");
  
      await transport.pressTrigger();
      assert.equal(controller.getStatus().state, "sleeping", "没在扫描时按扳机，等下一轮扫描");
      const cancelsBefore = transport.cancelCalls;
      await clock.advance(INTERVAL_MS);
      await flush();
      assert.equal(controller.getStatus().state, "ready", "扫到广播后自动连回，不用去设置页");
      assert.ok(transport.cancelCalls > cancelsBefore, "重连前先取消挂着的旧请求");
      assert.equal(transport.maxConcurrentConnects, 1, "同一设备同时只有一个连接请求");
      assert.equal(transport.scans.size, 0, "连上后停止扫描");
      const startsAfterReady = transport.scanStarts;
      await clock.advance(60_000);
      assert.equal(transport.scanStarts, startsAfterReady, "已连接时不再扫描");
  
      // 再睡一次、扫描中按扳机：同一扫描窗口内就能连回。
      clock.now += 307_000;
      await transport.sleep();
      await clock.advance(INTERVAL_MS);
      assert.equal(transport.scans.size, 1);
      await transport.pressTrigger();
      assert.equal(controller.getStatus().state, "ready", "扫描中按扳机，立即连回");
      assert.equal(transport.maxConcurrentConnects, 1);
      controller.dispose();
    },
  ],
  [
    "2. 回前台不叠加重复连接请求，只是立即补一轮扫描。",
    async () => {
    const { transport, controller } = await setup();
      const connectsBefore = transport.connectCalls;
      controller.resume();
      await flush();
      assert.equal(transport.connectCalls, connectsBefore, "resume 不再对同一设备重复发连接请求");
      assert.equal(transport.scans.size, 1, "回前台立即扫一轮");
      await transport.pressTrigger();
      assert.equal(controller.getStatus().state, "ready");
      assert.equal(transport.maxConcurrentConnects, 1);
      controller.dispose();
    },
  ],
  [
    "3. 进后台停止扫描；回前台恢复。",
    async () => {
      const { clock, transport, controller } = await setup();
      await clock.advance(INTERVAL_MS);
      assert.equal(transport.scans.size, 1);
      controller.pause();
      assert.equal(transport.scans.size, 0, "进后台立即停止扫描");
      const startsInBackground = transport.scanStarts;
      await clock.advance(120_000);
      assert.equal(transport.scanStarts, startsInBackground, "后台期间不再扫描");
      controller.resume();
      await flush();
      assert.equal(transport.scans.size, 1, "回前台恢复扫描");
      await transport.pressTrigger();
      assert.equal(controller.getStatus().state, "ready");
      controller.dispose();
    },
  ],
  [
    "4. 与设置页「搜索扫码枪」互斥：搜索期间不做唤醒扫描、不会误停搜索；搜索结束后恢复。",
    async () => {
      const { clock, transport, controller } = await setup();
      await clock.advance(INTERVAL_MS);
      assert.equal(transport.scans.size, 1);
      let found: readonly BleDiscoveredDevice[] = [];
      const stopDiscovery = controller.startDiscovery((devices) => (found = devices));
      assert.equal(transport.scans.size, 1, "唤醒扫描让位给搜索，只剩搜索这一个扫描");
      await clock.advance(60_000);
      assert.equal(transport.scans.size, 1, "唤醒扫描的计时器不会把搜索停掉，也不会另开扫描");
      const connectsBefore = transport.connectCalls;
      await transport.pressTrigger();
      assert.ok(found.some((device) => device.id === SCANNER_ID), "搜索照常列出设备");
      assert.equal(transport.connectCalls, connectsBefore, "搜索期间看到广播不触发自动重连");
      stopDiscovery();
      assert.equal(transport.scans.size, 0);
      await clock.advance(INTERVAL_MS);
      await flush();
      assert.equal(controller.getStatus().state, "ready", "搜索结束后恢复唤醒扫描并连回");
      assert.equal(transport.maxConcurrentConnects, 1);
      controller.dispose();
    },
  ],
  [
    "5. 手动断开后不做唤醒扫描（用户明确不要自动重连）。",
    async () => {
      const { clock, transport, controller } = await setup();
      await controller.disconnect();
      assert.equal(controller.getStatus().state, "disconnected");
      const starts = transport.scanStarts;
      await clock.advance(60_000);
      assert.equal(transport.scanStarts, starts, "手动断开后不扫描");
      controller.dispose();
    },
  ],
  [
    "6. dispose 后不再扫描。",
    async () => {
      const { clock, transport, controller } = await setup();
      controller.dispose();
      await clock.advance(60_000);
      assert.equal(transport.scans.size, 0);
    },
  ],
  [
    "7. 手动「重新连接」（设置页 / 扫码页图标）先取消挂着的旧请求，同一设备只有一个连接请求。",
    async () => {
      const { transport, controller } = await setup();
      await transport.pressTrigger();
      await controller.reconnect();
      await flush();
      assert.equal(controller.getStatus().state, "ready");
      assert.equal(transport.maxConcurrentConnects, 1, "重新连接不叠加请求");
      controller.dispose();
    },
  ],
  [
    "8. 扫码枪还睡着就点「重新连接」：直连 15 秒没结果就取消，回到休眠并继续唤醒扫描，不会卡在连接中。",
    async () => {
      const { clock, transport, controller } = await setup();
      await controller.reconnect();
      await flush();
      assert.equal(controller.getStatus().state, "connecting");
      await clock.advance(15_000);
      await flush();
      assert.equal(controller.getStatus().state, "sleeping", "直连超时后回到休眠");
      assert.equal(transport.maxConcurrentConnects, 1);
      await clock.advance(INTERVAL_MS);
      assert.equal(transport.scans.size, 1, "继续唤醒扫描");
      await transport.pressTrigger();
      assert.equal(controller.getStatus().state, "ready");
      assert.equal(transport.maxConcurrentConnects, 1);
      controller.dispose();
    },
  ],
];

async function main(): Promise<void> {
  const failures: string[] = [];
  for (const [title, run] of cases) {
    try {
      await run();
    } catch (error) {
      failures.push(`${title}\n    ${error instanceof Error ? error.message.split("\n")[0] : String(error)}`);
    }
  }
  if (failures.length > 0) {
    throw new Error(`${failures.length}/${cases.length} 个用例失败：\n  ${failures.join("\n  ")}`);
  }
  console.log("ble-scanner-controller-wake: ok");
}

main().catch((error: unknown) => {
  console.error(error);
  process.exit(1);
});
