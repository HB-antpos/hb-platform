/**
 * iPad POS 的 BLE 扫码枪服务：把共享控制器（连接 / 拼帧 / 重连）接到本端扫码路由。
 *
 * 纯 TS，不直接依赖 React Native 与蓝牙库（由 create-ble-scanner-service.ts 注入），
 * 便于用 tsx 测试分流规则：
 * - kind === "barcode"：交给 HidScannerRouter.acceptBleText，按场景租约投递；
 *   没有页面在收（返回 false）时记一次「未接收」并通知 UI 轻提示，码直接丢弃，不缓存、不延迟投递。
 * - 设置码 / 设备回复：绝不进业务，只给「测试扫码」面板显示。
 */
import { type HidScannerRouter, isDeviceActivationScanValue } from "../hid-scanner";

import type {
  BleDiscoveredDevice,
  BleScannerController,
  BleScannerScan,
  BleScannerStatus,
} from "./ble-scanner-controller";
import type { BleScanFrame, BleScanKind } from "./ble-scanner-core";

export type BleScannerControllerPort = Pick<
  BleScannerController,
  | "start"
  | "resume"
  | "pause"
  | "dispose"
  | "getStatus"
  | "subscribeStatus"
  | "subscribeScans"
  | "startDiscovery"
  | "stopDiscovery"
  | "pair"
  | "forget"
  | "disconnect"
  | "reconnect"
  | "setAlias"
>;

export type BleScannerRouterPort = Pick<HidScannerRouter, "acceptBleText">;

/** 测试面板看到的每一个 BLE 码（含设置码、设备回复）。 */
export type BleScannerTestScan = Readonly<{
  frame: BleScanFrame;
  kind: BleScanKind;
  /** 是否被路由投递（测试弹窗打开时投给 scanner-test 场景）。 */
  delivered: boolean;
}>;

export type BleScannerUndeliveredNotice = Readonly<{ at: number }>;

export type BleScannerServiceLogEvent =
  | "ble-scan-undelivered"
  | "ble-start-failed";

export type BleScannerServiceOptions = Readonly<{
  controller: BleScannerControllerPort;
  router: BleScannerRouterPort;
  /** 搜索时排除的外设 ID（已保存的小票打印机），避免把打印机当扫码枪连走。 */
  loadExcludedPeripheralIds?: () => Promise<readonly string[]>;
  /** App 回到前台的订阅；回到前台时休眠 / 重连中的扫码枪立即补一轮唤醒扫描。 */
  subscribeForeground?: (listener: () => void) => () => void;
  /** App 进后台的订阅；进后台时停止唤醒扫描（省电）。 */
  subscribeBackground?: (listener: () => void) => () => void;
  /** 只记事件名，不记码值（码可能是员工码或其他敏感内容）。 */
  log?: (event: BleScannerServiceLogEvent) => void;
}>;

export class BleScannerService {
  private readonly controller: BleScannerControllerPort;
  private readonly router: BleScannerRouterPort;
  private readonly loadExcludedPeripheralIds: () => Promise<readonly string[]>;
  private readonly subscribeForeground?: BleScannerServiceOptions["subscribeForeground"];
  private readonly subscribeBackground?: BleScannerServiceOptions["subscribeBackground"];
  private readonly log?: BleScannerServiceOptions["log"];
  private readonly testListeners = new Set<(scan: BleScannerTestScan) => void>();
  private readonly undeliveredListeners = new Set<
    (notice: BleScannerUndeliveredNotice) => void
  >();
  private lastUndelivered: BleScannerUndeliveredNotice | null = null;
  private cleanups: (() => void)[] = [];
  private started = false;

  constructor(options: BleScannerServiceOptions) {
    this.controller = options.controller;
    this.router = options.router;
    this.loadExcludedPeripheralIds =
      options.loadExcludedPeripheralIds ?? (() => Promise.resolve([]));
    this.subscribeForeground = options.subscribeForeground;
    this.subscribeBackground = options.subscribeBackground;
    this.log = options.log;
  }

  /** App 启动时调用一次：订阅扫码分流、挂起已保存扫码枪的连接。可重复调用。 */
  start(): void {
    if (this.started) return;
    this.started = true;
    this.cleanups.push(this.controller.subscribeScans((scan) => this.handleScan(scan)));
    if (this.subscribeForeground) {
      this.cleanups.push(this.subscribeForeground(() => this.controller.resume()));
    }
    if (this.subscribeBackground) {
      this.cleanups.push(this.subscribeBackground(() => this.controller.pause()));
    }
    void this.controller.start().catch(() => {
      // 蓝牙初始化失败不影响收银主流程；状态停在未配对 / 不可用。
      this.log?.("ble-start-failed");
    });
  }

  dispose(): void {
    this.started = false;
    for (const cleanup of this.cleanups.splice(0)) cleanup();
    this.controller.dispose();
    this.testListeners.clear();
    this.undeliveredListeners.clear();
  }

  getStatus(): BleScannerStatus {
    return this.controller.getStatus();
  }

  subscribeStatus(listener: (status: BleScannerStatus) => void): () => void {
    return this.controller.subscribeStatus(listener);
  }

  /** 测试面板订阅：收到的每一个 BLE 码（带拼帧细节），关闭面板即退订。 */
  subscribeTestScans(listener: (scan: BleScannerTestScan) => void): () => void {
    this.testListeners.add(listener);
    return () => this.testListeners.delete(listener);
  }

  /** 「当前页面不接收扫码」的轻提示订阅。 */
  subscribeUndelivered(
    listener: (notice: BleScannerUndeliveredNotice) => void,
  ): () => void {
    this.undeliveredListeners.add(listener);
    return () => this.undeliveredListeners.delete(listener);
  }

  getLastUndelivered(): BleScannerUndeliveredNotice | null {
    return this.lastUndelivered;
  }

  /**
   * 搜索附近设备。先异步读出要排除的打印机 ID 再开始扫描；
   * 返回的停止函数在排除列表读出前调用也有效。
   */
  startDiscovery(
    onDevices: (devices: readonly BleDiscoveredDevice[]) => void,
  ): () => void {
    let stopped = false;
    let stop: (() => void) | null = null;
    void this.loadExcludedPeripheralIds()
      .catch((): readonly string[] => [])
      .then((excludeIds) => {
        if (stopped) return;
        stop = this.controller.startDiscovery(onDevices, { excludeIds });
      });
    return () => {
      stopped = true;
      stop?.();
    };
  }

  stopDiscovery(): void {
    this.controller.stopDiscovery();
  }

  pair(device: BleDiscoveredDevice): Promise<void> {
    return this.controller.pair(device);
  }

  forget(): Promise<void> {
    return this.controller.forget();
  }

  disconnect(): Promise<void> {
    return this.controller.disconnect();
  }

  reconnect(): Promise<void> {
    return this.controller.reconnect();
  }

  setAlias(alias: string | null): Promise<void> {
    return this.controller.setAlias(alias);
  }

  private handleScan(scan: BleScannerScan): void {
    let delivered = false;
    if (scan.kind === "barcode") {
      delivered = this.router.acceptBleText(scan.frame.value, scan.frame.receivedAt);
      // 开通码被路由拦截时同样返回 false，但它不是「页面不收」，不提示。
      if (!delivered && !isDeviceActivationScanValue(scan.frame.value)) {
        // 没有页面在收（付款中、设置页普通表单等）：丢弃并轻提示一次，不缓存。
        const notice = Object.freeze({ at: scan.frame.receivedAt });
        this.lastUndelivered = notice;
        this.log?.("ble-scan-undelivered");
        this.undeliveredListeners.forEach((listener) => listener(notice));
      }
    }
    // 设置码 / 设备回复不进路由，只在测试面板显示。
    const testScan: BleScannerTestScan = Object.freeze({
      frame: scan.frame,
      kind: scan.kind,
      delivered,
    });
    this.testListeners.forEach((listener) => listener(testScan));
  }
}
