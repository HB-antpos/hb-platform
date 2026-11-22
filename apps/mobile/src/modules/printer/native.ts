import { Linking, NativeEventEmitter, NativeModules, PermissionsAndroid, Platform } from "react-native";
import {
  buildBigDiscountLabelCommand,
  buildClearanceLabelCommand,
  buildDiscountLabelCommand,
  buildProductLabelCommand,
  buildWarehouseLocationLabelCommand,
  buildWarehouseProductLabelCommand,
} from "@/modules/printer/cpcl-labels";
import {
  chunkItems,
  encodeBase64,
  normalizeLabelRenderInfo,
  normalizeTextBitmap,
  normalizeTextMetrics,
  type LabelRenderInfo,
  type LabelRenderTextItem,
  type LabelTextBitmap,
  type LabelTextItem,
  type LabelTextMetrics,
} from "@/modules/printer/label-render";
import type {
  NativeLinkEvent,
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
  /** 新安卓原生包才有：取走原生层缓冲的蓝牙链路事件；旧包没有这个方法。 */
  drainLinkDiagnostics?(): Promise<unknown>;
  disconnect(): Promise<boolean>;
  /** 新 iOS 原生包才有：下发 BLE 写入策略；旧包没有这个方法时原生层维持旧的逐包有应答写入。 */
  configureWrite?(options: IosBleWriteOptions): Promise<boolean>;
  print(command: string, encoding?: string): Promise<boolean>;
  printProductLabel(payload: ProductLabelPrintPayload, printType?: string | null): Promise<boolean>;
  printDiscountLabel(payload: ProductLabelPrintPayload, printType?: string | null): Promise<boolean>;
  printClearanceLabel(payload: ProductLabelPrintPayload): Promise<boolean>;
  printBigDiscountLabel(payload: ProductLabelPrintPayload, printType?: string | null): Promise<boolean>;
  printWarehouseProductLabel(payload: WarehouseProductLabelPrintPayload): Promise<boolean>;
  printWarehouseLocationLabel(payload: WarehouseLocationLabelPrintPayload): Promise<boolean>;
  /** 新原生包才有（JS 排版能力标记）：内置字体、批量上限等。 */
  getLabelRenderInfo?(): Promise<unknown>;
  /** 新原生包才有：批量量文字尺寸。 */
  measureTexts?(items: LabelTextItem[]): Promise<unknown>;
  /** 新原生包才有：批量把文字画成 1bit 位图。 */
  renderTexts?(items: LabelRenderTextItem[]): Promise<unknown>;
  /** 新原生包才有：下发原始字节（base64），与 print 共用写入路径。 */
  writeBytes?(base64: string): Promise<boolean>;
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

export type IosBleWriteOptions = {
  /** fast：无应答写入连发 + 定期有应答栅栏；reliable：旧的全部有应答长写（慢但最保守）。 */
  mode: "fast" | "reliable";
  /** fast 模式下每隔多少字节插一包有应答写入，原生层夹在 256–16384。 */
  ackIntervalBytes: number;
};

/**
 * iOS 标签打印 BLE 写入策略。默认值与原生层一致；若某型号打印机在 fast 下丢数据（缺字、半张、乱码），
 * 改成 { mode: "reliable", ... } 或调小 ackIntervalBytes 后发 OTA 即可回退，无需重新上架原生包。
 */
export const IOS_BLE_WRITE_OPTIONS: IosBleWriteOptions = { mode: "fast", ackIntervalBytes: 2048 };

/** 下发写入策略只是调速，失败或旧包没有方法都不能挡住连接。 */
async function applyIosBleWriteOptions(module: NativePrinterModule) {
  if (Platform.OS !== "ios" || typeof module.configureWrite !== "function") {
    return;
  }
  try {
    await module.configureWrite(IOS_BLE_WRITE_OPTIONS);
  } catch {
    // 原生层保持当前策略继续连接。
  }
}

export async function connectPrinter(address: string, transport?: PrinterTransport | null) {
  await ensureBluetoothPermissions();
  const module = getModule();
  // 每次连接（含 App 启动自动连接）前下发一次，热连接直写路径不再重复调用。
  await applyIosBleWriteOptions(module);
  if (Platform.OS === "android" && typeof module.connectWithTransport === "function") {
    return module.connectWithTransport(address, transport ?? null);
  }
  return module.connect(address);
}

const NATIVE_DIAGNOSTICS_TIMEOUT_MS = 1500;

function isNativeLinkEvent(value: unknown): value is NativeLinkEvent {
  if (!value || typeof value !== "object") return false;
  const { ev, atMs } = value as { ev?: unknown; atMs?: unknown };
  return typeof ev === "string" && typeof atMs === "number" && Number.isFinite(atMs);
}

/**
 * 取走安卓原生层的蓝牙链路事件，供链路诊断并入。只有新安卓原生包才有；旧包、iOS、取不到、超时一律返回空数组，
 * 诊断只是旁路，绝不能让连接或打印等它或因它失败。
 */
export async function drainNativeLinkDiagnostics(
  timeoutMs = NATIVE_DIAGNOSTICS_TIMEOUT_MS,
): Promise<NativeLinkEvent[]> {
  if (Platform.OS !== "android" || typeof nativeModule?.drainLinkDiagnostics !== "function") {
    return [];
  }
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    const timeout = new Promise<unknown>((resolve) => {
      timer = setTimeout(() => resolve([]), timeoutMs);
    });
    const events = await Promise.race([nativeModule.drainLinkDiagnostics(), timeout]);
    return Array.isArray(events) ? events.filter(isNativeLinkEvent) : [];
  } catch {
    return [];
  } finally {
    if (timer) clearTimeout(timer);
  }
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

let labelRenderInfoPromise: Promise<LabelRenderInfo | null> | null = null;

/**
 * JS 排版能力：新原生包返回内置字体与批量上限，旧包 / 取不到 / 版本不够返回 null（调用方继续走旧的原生排版）。
 * 原生能力在一次安装内不会变，成功结果缓存；失败不缓存，下次再试。
 */
export function getLabelRenderInfo(): Promise<LabelRenderInfo | null> {
  if ((Platform.OS !== "android" && Platform.OS !== "ios") || typeof nativeModule?.getLabelRenderInfo !== "function") {
    return Promise.resolve(null);
  }
  if (!labelRenderInfoPromise) {
    const module = nativeModule;
    labelRenderInfoPromise = Promise.resolve()
      .then(() => module.getLabelRenderInfo?.())
      .then(normalizeLabelRenderInfo)
      .then((info) => {
        if (!info) labelRenderInfoPromise = null;
        return info;
      })
      .catch(() => {
        labelRenderInfoPromise = null;
        return null;
      });
  }
  return labelRenderInfoPromise;
}

async function requireLabelRender() {
  const info = await getLabelRenderInfo();
  const module = getModule();
  if (!info || typeof module.measureTexts !== "function" || typeof module.renderTexts !== "function") {
    throw new Error("This app version does not support JS label layout yet.");
  }
  return { info, module };
}

/** 批量量文字尺寸；超过原生单批上限时自动分批，结果与入参一一对应。 */
export async function measureLabelTexts(items: readonly LabelTextItem[]): Promise<LabelTextMetrics[]> {
  if (items.length === 0) return [];
  const { info, module } = await requireLabelRender();
  const results: LabelTextMetrics[] = [];
  for (const batch of chunkItems(items, info.maxBatch)) {
    const raw = await module.measureTexts!(batch);
    if (!Array.isArray(raw) || raw.length !== batch.length) {
      throw new Error("Printer measure result does not match the request.");
    }
    results.push(...raw.map(normalizeTextMetrics));
  }
  return results;
}

/** 批量把文字画成 1bit 位图；超过原生单批上限时自动分批，结果与入参一一对应。 */
export async function renderLabelTexts(items: readonly LabelRenderTextItem[]): Promise<LabelTextBitmap[]> {
  if (items.length === 0) return [];
  const { info, module } = await requireLabelRender();
  const results: LabelTextBitmap[] = [];
  for (const batch of chunkItems(items, info.maxBatch)) {
    const raw = await module.renderTexts!(batch);
    if (!Array.isArray(raw) || raw.length !== batch.length) {
      throw new Error("Printer render result does not match the request.");
    }
    results.push(...raw.map(normalizeTextBitmap));
  }
  return results;
}

/** 下发原始字节（如二进制 CG 位图）；旧包没有 writeBytes 时明确报错，不偷偷改成文本发送。 */
export async function printRawBytes(bytes: Uint8Array) {
  await ensureBluetoothPermissions();
  const module = getModule();
  if (typeof module.writeBytes !== "function") {
    throw new Error("This app version does not support raw byte printing yet.");
  }
  return module.writeBytes(encodeBase64(bytes));
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
