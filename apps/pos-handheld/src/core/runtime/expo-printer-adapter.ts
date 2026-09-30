import { requireNativeModule } from "expo";
import { PermissionsAndroid, Platform, type Permission } from "react-native";

import { requestAndroidBluetoothScanPermissions } from "../peripherals/printer/android-bluetooth-permissions";

import {
  createLazyHbPrinterAdapter,
  type RuntimePrinterAdapter,
} from "./lazy-printer-adapter";

/**
 * Expo Modules 在首次使用硬件前才解析，避免启动阶段创建蓝牙模块。缺少 Development
 * Build 原生模块时，底层 bridge 会将加载异常转换为明确的不可用结果。
 * 扫描前先请求 Android 运行时权限，避免店员只能去系统设置里手动查找。
 */
export function createLazyExpoPrinterAdapter(): RuntimePrinterAdapter {
  return createLazyHbPrinterAdapter(requireNativeModule, () =>
    requestAndroidBluetoothScanPermissions(
      { os: Platform.OS, version: Platform.Version },
      (permissions) =>
        PermissionsAndroid.requestMultiple(permissions as Permission[]),
    ),
  );
}
