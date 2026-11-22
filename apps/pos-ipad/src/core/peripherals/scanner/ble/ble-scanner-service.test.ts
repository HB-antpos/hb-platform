import assert from "node:assert/strict";

import { HidScannerRouter } from "../hid-scanner";

import type {
  BleDiscoveredDevice,
  BleScannerScan,
  BleScannerStatus,
} from "./ble-scanner-controller";
import type { BleScanKind } from "./ble-scanner-core";
import {
  type BleScannerControllerPort,
  BleScannerService,
  type BleScannerServiceLogEvent,
  type BleScannerTestScan,
} from "./ble-scanner-service";

/* ---------------- 假控制器：只记录调用、手动推送扫码 ---------------- */

class FakeController implements BleScannerControllerPort {
  scanListeners = new Set<(scan: BleScannerScan) => void>();
  startCalls = 0;
  resumeCalls = 0;
  disposeCalls = 0;
  discoveryExclude: readonly string[] | null = null;
  discoveryStops = 0;

  async start(): Promise<void> {
    this.startCalls += 1;
  }
  pauseCalls = 0;
  pause(): void {
    this.pauseCalls += 1;
  }
  resume(): void {
    this.resumeCalls += 1;
  }
  dispose(): void {
    this.disposeCalls += 1;
  }
  getStatus(): BleScannerStatus {
    return {
      state: "ready",
      saved: null,
      batteryLevel: null,
      rssi: null,
      lastScanAt: null,
      lastScanValue: null,
      attention: false,
      lastErrorCode: null,
    };
  }
  subscribeStatus(): () => void {
    return () => undefined;
  }
  subscribeScans(listener: (scan: BleScannerScan) => void): () => void {
    this.scanListeners.add(listener);
    return () => this.scanListeners.delete(listener);
  }
  startDiscovery(
    _onDevices: (devices: readonly BleDiscoveredDevice[]) => void,
    options: Readonly<{ excludeIds?: readonly string[] }> = {},
  ): () => void {
    this.discoveryExclude = options.excludeIds ?? [];
    return () => {
      this.discoveryStops += 1;
    };
  }
  stopDiscovery(): void {}
  async pair(): Promise<void> {}
  async forget(): Promise<void> {}
  async disconnect(): Promise<void> {}
  async reconnect(): Promise<void> {}
  async setAlias(): Promise<void> {}

  emit(value: string, kind: BleScanKind, receivedAt: number): void {
    const scan: BleScannerScan = {
      kind,
      characteristicUuid: "00002AA1-0000-1000-8000-00805F9B34FB",
      frame: {
        value,
        rawHex: "",
        packetCount: 1,
        durationMs: 0,
        terminator: "CR",
        receivedAt,
      },
    };
    this.scanListeners.forEach((listener) => listener(scan));
  }
}

let now = 10_000;
const controller = new FakeController();
const router = new HidScannerRouter({ now: () => now });
const routed: string[] = [];
router.subscribeRouted((event) => routed.push(`${event.context}:${event.source}:${event.value}`));
let foregroundListener: (() => void) | null = null;
let backgroundListener: (() => void) | null = null;
const logs: BleScannerServiceLogEvent[] = [];
const service = new BleScannerService({
  controller,
  router,
  loadExcludedPeripheralIds: async () => ["PRINTER-1"],
  subscribeForeground: (listener) => {
    foregroundListener = listener;
    return () => {
      foregroundListener = null;
    };
  },
  subscribeBackground: (listener) => {
    backgroundListener = listener;
    return () => {
      backgroundListener = null;
    };
  },
  log: (event) => logs.push(event),
});
const testScans: BleScannerTestScan[] = [];
service.subscribeTestScans((scan) => testScans.push(scan));
const undelivered: number[] = [];
service.subscribeUndelivered((notice) => undelivered.push(notice.at));

service.start();
service.start();
assert.equal(controller.startCalls, 1, "start 可重复调用但只启动一次");
assert.equal(controller.scanListeners.size, 1);

// 1) 没有页面占用场景：条码不投递，记一次「未接收」。
controller.emit("9313559558795", "barcode", now);
assert.deepEqual(routed, []);
assert.deepEqual(undelivered, [now]);
assert.deepEqual(service.getLastUndelivered(), { at: now });
assert.deepEqual(logs, ["ble-scan-undelivered"]);
assert.equal(testScans.at(-1)?.delivered, false);

// 2) 销售页占用 product：条码进入商品场景。
const releaseSales = router.acquireContext("product");
now += 1_000;
controller.emit("9313559558795", "barcode", now);
assert.deepEqual(routed, ["product:ble:9313559558795"]);
assert.equal(testScans.at(-1)?.delivered, true);
assert.equal(undelivered.length, 1);

// 3) 设置码与设备回复只给测试面板，绝不进路由，也不算「未接收」。
now += 1_000;
controller.emit("%%SpecCodeAC", "setting-code", now);
controller.emit("HW:V1.00;SW:HD210_V2Nx_V1.8.7.0;Date:2025.12.29;", "device-reply", now);
assert.equal(routed.length, 1, "设置码 / 设备回复不得进业务页面");
assert.deepEqual(
  testScans.slice(-2).map((scan) => `${scan.kind}:${scan.delivered}`),
  ["setting-code:false", "device-reply:false"],
);
assert.equal(undelivered.length, 1);

// 4) 开通码被路由拦截，但不提示「页面不收」。
controller.emit("HBDEV1-SECRET", "barcode", now);
assert.equal(routed.length, 1);
assert.equal(undelivered.length, 1, "开通码被拦截不属于「页面不收」");

// 5) 测试独占：测试弹窗占用 scanner-test 时条码只进测试场景。
const releaseTest = router.acquireContext("scanner-test");
now += 1_000;
controller.emit("VD028", "barcode", now);
assert.equal(routed.at(-1), "scanner-test:ble:VD028");
releaseTest();
releaseSales();

// 6) 回到前台：重新挂连接请求。
assert.ok(foregroundListener);
(foregroundListener as () => void)();
assert.equal(controller.resumeCalls, 1);
// 进后台：停止唤醒扫描。
assert.ok(backgroundListener);
(backgroundListener as () => void)();
assert.equal(controller.pauseCalls, 1);

// 7) 搜索：排除已保存打印机；排除列表读出前就停止也不会开始扫描。
void (async () => {
const stopEarly = service.startDiscovery(() => undefined);
stopEarly();
await Promise.resolve();
await Promise.resolve();
assert.equal(controller.discoveryExclude, null, "读出排除列表前停止，不得再开始扫描");
const stop = service.startDiscovery(() => undefined);
await new Promise((resolve) => setTimeout(resolve, 0));
assert.deepEqual(controller.discoveryExclude, ["PRINTER-1"]);
stop();
assert.equal(controller.discoveryStops, 1);

// 8) 释放：退订扫码与前台监听，并释放控制器。
service.dispose();
assert.equal(controller.scanListeners.size, 0);
assert.equal(foregroundListener, null);
assert.equal(controller.disposeCalls, 1);

console.log("ble-scanner-service.test.ts: ok");
})().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
