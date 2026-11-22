/**
 * 移动端 BLE 扫码枪服务：全 App 单例控制器 + 存储 + 把码交给 ble-scan-hub。
 *
 * - 存储：AsyncStorage `hbweb_ble_scanner`（与打印机 `hbweb_label_printer` 同一套 AppAsyncStorage 封装），
 *   读出后一律用 parseSavedBleScanner 校验，不合法当没保存。
 * - 启动：App 启动时只有「已保存过扫码枪」才真正启动控制器（创建 BleManager 会让 iOS 弹蓝牙授权，
 *   不能对从没用过扫码枪的人弹）；第一次使用由设置页「搜索」按钮触发。
 * - iOS 审核态：不扫描、不连接、不分发。旧原生包（没有 ble-plx）：全部空操作，设置页整行隐藏。
 * - 分发：业务码 → 测试拦截器优先，否则投给最后一个启用的 useHidBarcodeScanner；没人接就轻提示一次。
 *   设置码 / 设备回复只给测试面板，绝不进业务页面。
 */
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { usePrinterStore, useReceiptPrinterStore } from "@/modules/printer/state";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { deliverBleScan } from "./ble-scan-hub";
import {
  BleScannerController,
  type BleDiscoveredDevice,
  type BleScannerScan,
  type BleScannerStatus,
  type BleScannerStorage,
} from "./ble-scanner-controller";
import { parseSavedBleScanner, type SavedBleScanner } from "./ble-scanner-core";
import { createBlePlxTransport, isBleScannerNativeAvailable } from "./ble-plx-transport";
import { INITIAL_BLE_SCANNER_STATUS, useBleScannerStore } from "./ble-scanner-state";

export const BLE_SCANNER_STORAGE_KEY = "hbweb_ble_scanner";
/** 「当前页面不接收扫码」提示的最短间隔，避免连扫时反复弹。 */
const NO_RECEIVER_NOTICE_INTERVAL_MS = 3_000;

const storage: BleScannerStorage = {
  async load() {
    return parseSavedBleScanner(await AppAsyncStorage.getString(BLE_SCANNER_STORAGE_KEY));
  },
  async save(value: SavedBleScanner | null) {
    if (!value) {
      await AppAsyncStorage.removeItem(BLE_SCANNER_STORAGE_KEY);
      return;
    }
    await AppAsyncStorage.setString(BLE_SCANNER_STORAGE_KEY, JSON.stringify(value));
  },
};

let controller: BleScannerController | null = null;
let startPromise: Promise<boolean> | null = null;
let serviceGeneration = 0;
let detachListeners: (() => void) | null = null;
let lastNoReceiverNoticeAt = 0;
const scanListeners = new Set<(scan: BleScannerScan) => void>();

/** 旧原生包（OTA 推到没有 ble-plx 的包）返回 false：设置页隐藏「蓝牙扫码枪」整行。 */
export function isBleScannerFeatureAvailable(): boolean {
  return isBleScannerNativeAvailable() && !isIosReviewSessionActive();
}

function getController(): BleScannerController {
  if (!controller) {
    controller = new BleScannerController({ transport: createBlePlxTransport(), storage });
  }
  return controller;
}

function routeScan(scan: BleScannerScan): void {
  // 测试面板要看到全部（含设置码、设备回复、分包细节），先发给它的订阅。
  scanListeners.forEach((listener) => {
    try {
      listener(scan);
    } catch {
      // 测试面板出错不影响业务分发。
    }
  });
  // 设置码与设备回复（如版本信息）只在测试页显示，绝不进业务页面。
  if (scan.kind !== "barcode") return;
  if (isIosReviewSessionActive()) return;
  const handled = deliverBleScan(scan.frame.value);
  if (handled) return;
  const now = Date.now();
  if (now - lastNoReceiverNoticeAt < NO_RECEIVER_NOTICE_INTERVAL_MS) return;
  lastNoReceiverNoticeAt = now;
  // 没人接就丢：不缓存、不延迟投递，只轻提示一次。
  useBleScannerStore.getState().showNotice("no-receiver");
}

/**
 * 启动控制器（可重复调用）。onlyIfSaved=true 用于 App 启动：没有保存过扫码枪就不碰蓝牙。
 * 返回控制器是否已启动。
 */
export async function startBleScannerService(options: Readonly<{ onlyIfSaved?: boolean }> = {}): Promise<boolean> {
  if (isIosReviewSessionActive()) return false;
  if (!isBleScannerNativeAvailable()) return false;
  if (startPromise) return startPromise;
  const generation = ++serviceGeneration;
  const pending = (async () => {
    if (options.onlyIfSaved) {
      const saved = await storage.load().catch(() => null);
      if (!saved) return false;
    }
    if (generation !== serviceGeneration || isIosReviewSessionActive()) return false;
    const instance = getController();
    if (!detachListeners) {
      const offStatus = instance.subscribeStatus((status) => {
        useBleScannerStore.getState().setStatus(status);
      });
      const offScans = instance.subscribeScans(routeScan);
      detachListeners = () => {
        offStatus();
        offScans();
      };
    }
    await instance.start();
    useBleScannerStore.getState().setStatus(instance.getStatus());
    // 之前被 stop 断开过（例如审核态切换）时，start 本身是空操作，这里补一次重连。
    instance.resume();
    return true;
  })();
  startPromise = pending;
  const started = await pending.catch(() => false);
  // 只在本次启动没成功、且期间没有新的启动 / 停止时清掉缓存，允许下次再试。
  if (!started && startPromise === pending) startPromise = null;
  return started;
}

/** App 回到前台：休眠 / 重连中的扫码枪立即补一轮唤醒扫描（不叠加连接请求）。 */
export function resumeBleScannerService(): void {
  if (isIosReviewSessionActive()) return;
  if (!startPromise) return;
  controller?.resume();
}

/** App 进后台：停止唤醒扫描（省电；iOS 没有声明蓝牙后台模式，后台本来也扫不了）。 */
export function pauseBleScannerService(): void {
  controller?.pause();
}

/** 副作用关闭（如进入 iOS 审核态）：停止搜索、断开、解除订阅；保存的配置保留。 */
export function stopBleScannerService(): void {
  serviceGeneration += 1;
  startPromise = null;
  detachListeners?.();
  detachListeners = null;
  const instance = controller;
  if (!instance) return;
  instance.stopDiscovery();
  void instance
    .disconnect()
    .catch(() => undefined)
    .finally(() => {
      // 断开排队期间又重新启动了就不要 dispose，交给新的启动流程。
      if (!startPromise) instance.dispose();
    });
  useBleScannerStore.getState().setStatus(INITIAL_BLE_SCANNER_STATUS);
}

/** 搜索附近设备；排除已保存的蓝牙打印机，避免误把打印机连成扫码枪。返回停止函数。 */
export async function startBleScannerDiscovery(
  onDevices: (devices: readonly BleDiscoveredDevice[]) => void,
): Promise<(() => void) | null> {
  if (isIosReviewSessionActive()) return null;
  if (!(await startBleScannerService())) return null;
  const excludeIds = [
    usePrinterStore.getState().savedPrinter?.address,
    useReceiptPrinterStore.getState().savedPrinter?.address,
  ].filter((id): id is string => Boolean(id));
  return getController().startDiscovery(onDevices, { excludeIds });
}

export function stopBleScannerDiscovery(): void {
  controller?.stopDiscovery();
}

export async function pairBleScanner(device: BleDiscoveredDevice): Promise<void> {
  if (isIosReviewSessionActive()) return;
  if (!(await startBleScannerService())) return;
  await getController().pair(device);
}

export async function reconnectBleScanner(): Promise<void> {
  if (isIosReviewSessionActive()) return;
  if (!(await startBleScannerService())) return;
  await getController().reconnect();
}

export async function disconnectBleScanner(): Promise<void> {
  await controller?.disconnect();
}

export async function forgetBleScanner(): Promise<void> {
  if (controller) {
    await controller.forget();
    return;
  }
  // 控制器没启动过（例如蓝牙从没打开）也要能清掉存储里的旧配置。
  await storage.save(null);
  useBleScannerStore.getState().setStatus(INITIAL_BLE_SCANNER_STATUS);
}

export async function setBleScannerAlias(alias: string | null): Promise<void> {
  await controller?.setAlias(alias);
}

export async function setBleScannerAutoReconnect(autoReconnect: boolean): Promise<void> {
  await controller?.setAutoReconnect(autoReconnect);
}

/** 测试面板订阅每一个完整码（含设置码、设备回复与分包细节）。 */
export function subscribeBleScannerScans(listener: (scan: BleScannerScan) => void): () => void {
  scanListeners.add(listener);
  return () => {
    scanListeners.delete(listener);
  };
}

export function getBleScannerStatus(): BleScannerStatus {
  return controller?.getStatus() ?? useBleScannerStore.getState().status;
}
