import { Linking, NativeEventEmitter, NativeModules, PermissionsAndroid, Platform } from "react-native";
import {
  buildBigDiscountLabelCommand,
  buildClearanceLabelCommand,
  buildDiscountLabelCommand,
  buildProductLabelCommand,
  buildWarehouseLocationLabelCommand,
  buildWarehouseProductLabelCommand,
} from "@/modules/printer/cpcl-labels";
import type {
  PrinterDevice,
  PrinterStatus,
  PrinterTransport,
  ProductLabelPrintPayload,
  WarehouseLocationLabelPrintPayload,
  WarehouseProductLabelPrintPayload,
} from "@/modules/printer/types";

type NativePrinterModule = {
  addListener?(eventName: string): void;
  removeListeners?(count: number): void;
  getStatus(): Promise<PrinterStatus>;
  scanPrinters(durationMs?: number): Promise<PrinterDevice[]>;
  connect(address: string): Promise<boolean>;
  /** 新安卓原生包才有：按传输类型选择 RFCOMM 或 BLE GATT，同时作为“支持 BLE 打印”的能力标记。 */
  connectWithTransport?(address: string, transport: PrinterTransport | null): Promise<boolean>;
  disconnect(): Promise<boolean>;
  print(command: string, encoding?: string): Promise<boolean>;
  printProductLabel(payload: ProductLabelPrintPayload, printType?: string | null): Promise<boolean>;
  printDiscountLabel(payload: ProductLabelPrintPayload, printType?: string | null): Promise<boolean>;
  printClearanceLabel(payload: ProductLabelPrintPayload): Promise<boolean>;
  printBigDiscountLabel(payload: ProductLabelPrintPayload, printType?: string | null): Promise<boolean>;
  printWarehouseProductLabel(payload: WarehouseProductLabelPrintPayload): Promise<boolean>;
  printWarehouseLocationLabel(payload: WarehouseLocationLabelPrintPayload): Promise<boolean>;
};

const nativeModule = NativeModules.HbPrinterModule as NativePrinterModule | undefined;
const unsupportedPrinterStatus: PrinterStatus = {
  supported: false,
  enabled: false,
  connected: false,
  address: null,
};

function getModule() {
  if (Platform.OS !== "android" && Platform.OS !== "ios") {
    throw new Error("Bluetooth printing is only supported on Android and iOS right now.");
  }

  if (!nativeModule) {
    throw new Error("The Bluetooth printer module is not available.");
  }

  return nativeModule;
}

function printIosCpclLabel(command: string) {
  // iOS 旧包没有原生位图标签能力时，仍可用 TS 生成 CPCL 作为兼容回退。
  return getModule().print(command, "GB18030");
}

function isIosUnsupportedLabelPrintError(error: unknown) {
  if (!error || typeof error !== "object") {
    return false;
  }

  const candidate = error as { code?: unknown; message?: unknown };
  return (
    candidate.code === "IOS_LABEL_PRINT_UNSUPPORTED" ||
    String(candidate.message ?? "").includes("IOS_LABEL_PRINT_UNSUPPORTED") ||
    String(candidate.message ?? "").includes("iOS label bitmap printing is not supported yet")
  );
}

async function requestAndroidBluetoothPermissions() {
  if (Platform.OS !== "android") {
    return true;
  }

  const permissions: string[] =
    Platform.Version >= 31
      ? [
          PermissionsAndroid.PERMISSIONS.BLUETOOTH_CONNECT,
          PermissionsAndroid.PERMISSIONS.BLUETOOTH_SCAN,
        ]
      : [
          "android.permission.BLUETOOTH",
          "android.permission.BLUETOOTH_ADMIN",
          PermissionsAndroid.PERMISSIONS.ACCESS_FINE_LOCATION,
        ];

  const result = (await PermissionsAndroid.requestMultiple(
    permissions as never[]
  )) as Record<string, string>;
  return permissions.every((permission) => result[permission] === PermissionsAndroid.RESULTS.GRANTED);
}

export async function ensureBluetoothPermissions() {
  const granted = await requestAndroidBluetoothPermissions();
  if (!granted) {
    throw new Error("Bluetooth permission was not granted.");
  }
}

export async function getPrinterStatus() {
  // iOS 真机通过原生模块检查 BLE 状态；无模块时仍保持“不支持”状态，避免页面崩溃。
  if ((Platform.OS !== "android" && Platform.OS !== "ios") || !nativeModule) {
    return unsupportedPrinterStatus;
  }

  return getModule().getStatus();
}

export function subscribePrinterStatusChanged(onChange: () => void): () => void {
  if (!nativeModule?.addListener || !nativeModule.removeListeners) {
    // 旧安装包没有事件接口，继续由前台轮询和打印失败恢复兜底。
    return () => undefined;
  }
  const emitter = new NativeEventEmitter(nativeModule as Required<NativePrinterModule>);
  const subscription = emitter.addListener("HbPrinterStatusChanged", onChange);
  return () => subscription.remove();
}

export async function scanPrinters(durationMs = 5000) {
  await ensureBluetoothPermissions();
  return getModule().scanPrinters(durationMs);
}

/** iOS 只有 BLE；安卓需新原生包才能走 BLE GATT，OTA 推到旧安装包时仍按不支持处理。 */
export function isBlePrintingSupported() {
  if (Platform.OS === "ios") {
    return true;
  }
  return Platform.OS === "android" && typeof nativeModule?.connectWithTransport === "function";
}

export async function connectPrinter(address: string, transport?: PrinterTransport | null) {
  await ensureBluetoothPermissions();
  const module = getModule();
  if (Platform.OS === "android" && typeof module.connectWithTransport === "function") {
    return module.connectWithTransport(address, transport ?? null);
  }
  return module.connect(address);
}

/** 经典蓝牙必须先在系统中配对；打不开蓝牙设置页时退回本应用设置页。 */
export async function openBluetoothSettings() {
  if (Platform.OS !== "android") {
    return Linking.openSettings();
  }
  try {
    await Linking.sendIntent("android.settings.BLUETOOTH_SETTINGS");
  } catch {
    await Linking.openSettings();
  }
}

export async function disconnectPrinter() {
  return getModule().disconnect();
}

export async function printRawCommand(command: string) {
  await ensureBluetoothPermissions();
  return getModule().print(command, "GB18030");
}

export async function printNativeProductLabel(payload: ProductLabelPrintPayload, printType?: string | null) {
  await ensureBluetoothPermissions();
  if (Platform.OS === "ios") {
    try {
      // 新 iOS 包走 Swift 位图文本渲染，布局和 Android 普通商品标签保持一致。
      return await getModule().printProductLabel(payload, printType ?? null);
    } catch (error) {
      if (!isIosUnsupportedLabelPrintError(error)) {
        throw error;
      }
      return printIosCpclLabel(buildProductLabelCommand(payload, printType));
    }
  }
  return getModule().printProductLabel(payload, printType ?? null);
}

export async function printNativeDiscountLabel(payload: ProductLabelPrintPayload, printType?: string | null) {
  await ensureBluetoothPermissions();
  if (Platform.OS === "ios") {
    const module = getModule();
    if (typeof module.printDiscountLabel !== "function") {
      return printIosCpclLabel(buildDiscountLabelCommand(payload, printType));
    }

    try {
      // 新 iOS 包走 Swift 位图渲染，旧包才回退到 JS CPCL。
      return await module.printDiscountLabel(payload, printType ?? null);
    } catch (error) {
      if (!isIosUnsupportedLabelPrintError(error)) {
        throw error;
      }
      return printIosCpclLabel(buildDiscountLabelCommand(payload, printType));
    }
  }
  return getModule().printDiscountLabel(payload, printType ?? null);
}

export async function printNativeClearanceLabel(payload: ProductLabelPrintPayload) {
  await ensureBluetoothPermissions();
  if (Platform.OS === "ios") {
    return printIosCpclLabel(buildClearanceLabelCommand(payload));
  }
  return getModule().printClearanceLabel(payload);
}

export async function printNativeBigDiscountLabel(
  payload: ProductLabelPrintPayload,
  printType?: string | null
) {
  await ensureBluetoothPermissions();
  if (Platform.OS === "ios") {
    const module = getModule();
    if (typeof module.printBigDiscountLabel !== "function") {
      return printIosCpclLabel(buildBigDiscountLabelCommand(payload, printType));
    }

    try {
      // 大折扣标签需要原生位图布局才能和 Android 纸面一致。
      return await module.printBigDiscountLabel(payload, printType ?? null);
    } catch (error) {
      if (!isIosUnsupportedLabelPrintError(error)) {
        throw error;
      }
      return printIosCpclLabel(buildBigDiscountLabelCommand(payload, printType));
    }
  }
  return getModule().printBigDiscountLabel(payload, printType ?? null);
}

export async function printNativeWarehouseProductLabel(payload: WarehouseProductLabelPrintPayload) {
  await ensureBluetoothPermissions();
  if (Platform.OS === "ios") {
    const module = getModule();
    if (typeof module.printWarehouseProductLabel !== "function") {
      return printIosCpclLabel(buildWarehouseProductLabelCommand(payload));
    }

    try {
      // 仓库商品标签优先使用 Swift 复刻 Android 位图布局。
      return await module.printWarehouseProductLabel(payload);
    } catch (error) {
      if (!isIosUnsupportedLabelPrintError(error)) {
        throw error;
      }
      return printIosCpclLabel(buildWarehouseProductLabelCommand(payload));
    }
  }
  return getModule().printWarehouseProductLabel(payload);
}

export async function printNativeWarehouseLocationLabel(payload: WarehouseLocationLabelPrintPayload) {
  await ensureBluetoothPermissions();
  if (Platform.OS === "ios") {
    const module = getModule();
    if (typeof module.printWarehouseLocationLabel !== "function") {
      return printIosCpclLabel(buildWarehouseLocationLabelCommand(payload));
    }

    try {
      // 仓库货位标签优先使用 Swift 复刻 Android 位图布局。
      return await module.printWarehouseLocationLabel(payload);
    } catch (error) {
      if (!isIosUnsupportedLabelPrintError(error)) {
        throw error;
      }
      return printIosCpclLabel(buildWarehouseLocationLabelCommand(payload));
    }
  }
  return getModule().printWarehouseLocationLabel(payload);
}
