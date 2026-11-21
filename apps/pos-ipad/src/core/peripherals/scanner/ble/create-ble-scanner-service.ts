import { AppState } from "react-native";

import type { HidScannerRouter } from "../hid-scanner";

import { createBlePlxTransport, isBleScannerNativeAvailable } from "./ble-plx-transport";
import { BleScannerController, type BleScannerStorage } from "./ble-scanner-controller";
import { BleScannerService, type BleScannerServiceOptions } from "./ble-scanner-service";

/**
 * 组装 iPad 的 BLE 扫码枪服务。
 * 旧原生包（新原生包之前推的 OTA）里没有 ble-plx 原生模块：返回 null，
 * 设置页据此不显示「蓝牙扫码枪」卡片，其余扫码（键盘模式、相机）照常。
 */
export function createBleScannerService(
  input: Readonly<{
    router: HidScannerRouter;
    storage: BleScannerStorage;
    loadExcludedPeripheralIds?: BleScannerServiceOptions["loadExcludedPeripheralIds"];
    log?: BleScannerServiceOptions["log"];
  }>,
): BleScannerService | null {
  if (!isBleScannerNativeAvailable()) return null;
  const controller = new BleScannerController({
    transport: createBlePlxTransport(),
    storage: input.storage,
  });
  return new BleScannerService({
    controller,
    router: input.router,
    ...(input.loadExcludedPeripheralIds
      ? { loadExcludedPeripheralIds: input.loadExcludedPeripheralIds }
      : {}),
    ...(input.log ? { log: input.log } : {}),
    // 回到前台重新挂一次连接请求（扫码枪休眠期间 App 可能被系统挂起过）。
    subscribeForeground: (listener) => {
      const subscription = AppState.addEventListener("change", (state) => {
        if (state === "active") listener();
      });
      return () => subscription.remove();
    },
  });
}
