import { Platform } from "react-native";
import type { ProductDetail } from "@/modules/product-maintenance/types";
import { isUnsupportedPrinterTransport, requiresSystemPairing } from "@/modules/printer/device-list";
import {
  buildCashRegisterUserBarcodeLabelCommand,
  buildEmployeeCashierBarcodeLabelCommand,
} from "@/modules/printer/cpcl-labels";
import {
  connectPrinter,
  disconnectPrinter,
  getPrinterStatus as getNativePrinterStatus,
  isBlePrintingSupported,
  printNativeBigDiscountLabel,
  printNativeClearanceLabel,
  printNativeDiscountLabel,
  printNativeProductLabel,
  printNativeWarehouseLocationLabel,
  printNativeWarehouseProductLabel,
  printRawCommand,
  scanPrinters,
} from "@/modules/printer/native";
import { buildReceiptPrinterTestCommand } from "@/modules/printer/receipt";
import { PrinterStorage } from "@/modules/printer/storage";
import { usePrinterStore, useReceiptPrinterStore } from "@/modules/printer/state";
import type {
  CashRegisterUserBarcodeLabelPrintPayload,
  EmployeeCashierBarcodeLabelPrintPayload,
  PrinterDevice,
  ProductLabelPrintPayload,
  SavedPrinter,
  WarehouseLocationLabelPrintPayload,
  WarehouseProductLabelPrintPayload,
} from "@/modules/printer/types";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";

const IOS_REVIEW_LABEL_PRINTER: SavedPrinter = {
  name: "App Review Label Printer",
  address: "IOS-REVIEW-LABEL",
};
const IOS_REVIEW_RECEIPT_PRINTER: SavedPrinter = {
  name: "App Review Receipt Printer",
  address: "IOS-REVIEW-RECEIPT",
};

interface ProductLabelOverrides {
  barcode?: string | null;
  retailPrice?: number | null;
  discountRate?: number | null;
  clearanceBarcode?: string | null;
  clearancePrice?: number | null;
}

let printerOperationTail: Promise<unknown> = Promise.resolve();
let labelConnectionInvalidated = false;
let autoReconnectIntent = 0;

function runPrinterOperation<T>(operation: () => Promise<T>): Promise<T> {
  // 两类打印机共用一个原生连接；检查、写入、切换设备必须作为完整操作串行执行。
  const result = printerOperationTail.then(operation);
  printerOperationTail = result.catch(() => undefined);
  return result;
}

function isPrinterConnectionError(error: unknown) {
  if (!error || typeof error !== "object") return false;
  const { code, message } = error as { code?: string; message?: string };
  return code === "PRINTER_CONNECTION_LOST" || code === "PRINT_TIMEOUT" ||
    /broken pipe|\bEPIPE\b|socket.*(?:closed|reset)|connection.*(?:lost|closed|reset)|printer.*disconnected|no bluetooth printer is connected/i.test(message ?? "");
}

async function runLabelPrint(print: () => Promise<boolean>) {
  return runPrinterOperation(async () => {
    await ensureConnectedPrinter({ preferHotWrite: true });
    try {
      return await print();
    } catch (error) {
      if (isPrinterConnectionError(error)) {
        // 兼容旧原生包：写失败后主动丢弃仍被标记为 connected 的 socket。
        // 数据可能已部分发送，只恢复连接状态，不重放本次标签。
        labelConnectionInvalidated = true;
        try {
          await disconnectPrinter();
        } catch {
          // 保留实际打印错误，不能用清理失败覆盖它。
        }
        const store = usePrinterStore.getState();
        store.setLastError(error instanceof Error ? error.message : String(error));
        store.setStatus(store.autoReconnectPaused ? "paused" : "disconnected");
      }
      throw error;
    }
  });
}

function toSavedPrinter(device: PrinterDevice | SavedPrinter): SavedPrinter {
  return {
    name: device.name ?? null,
    address: device.address,
    ...(device.transport ? { transport: device.transport } : {}),
  };
}

// 选择前的统一校验：旧安卓包不支持 BLE；经典蓝牙未在系统中配对时不进入连接流程。
function assertPrinterSelectable(device: PrinterDevice) {
  if (isUnsupportedPrinterTransport(device, Platform.OS, isBlePrintingSupported())) {
    throw Object.assign(new Error("This app build does not support BLE printers. Install the updated app or select the classic Bluetooth device with the same name."), {
      code: "PRINTER_BLE_UNSUPPORTED",
    });
  }
  if (requiresSystemPairing(device, Platform.OS)) {
    throw Object.assign(new Error("Pair the classic Bluetooth printer in Android Bluetooth settings first, then connect it in the app."), {
      code: "PRINTER_PAIRING_REQUIRED",
    });
  }
}

function buildPayload(detail: ProductDetail, overrides?: ProductLabelOverrides) {
  return {
    productName: detail.productName,
    itemNumber: detail.itemNumber,
    grade: detail.grade,
    supplierName: detail.localSupplierName,
    barcode: overrides?.barcode ?? detail.barcode,
    retailPrice: overrides?.retailPrice ?? detail.storePrice?.retailPrice ?? null,
    discountRate: overrides?.discountRate ?? detail.storePrice?.discountRate ?? null,
    clearanceBarcode: overrides?.clearanceBarcode ?? detail.clearancePrice?.clearanceBarcode ?? null,
    clearancePrice: overrides?.clearancePrice ?? detail.clearancePrice?.clearancePrice ?? null,
  };
}

function toNullableString(value: unknown): string | null {
  if (typeof value !== "string") {
    return null;
  }
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

function toNullableNumber(value: unknown): number | null {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function normalizeWarehouseProductLabelPayload(
  payload: WarehouseProductLabelPrintPayload
): WarehouseProductLabelPrintPayload {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  return {
    productCode: String(data.productCode ?? data.ProductCode ?? ""),
    productName: String(data.productName ?? data.ProductName ?? ""),
    itemNumber: toNullableString(data.itemNumber ?? data.ItemNumber),
    barcode: toNullableString(data.barcode ?? data.Barcode),
    supplierName: toNullableString(data.supplierName ?? data.SupplierName),
    middlePackageQuantity: toNullableNumber(data.middlePackageQuantity ?? data.MiddlePackageQuantity),
    purchasePrice: toNullableNumber(data.purchasePrice ?? data.PurchasePrice),
    retailPrice: toNullableNumber(data.retailPrice ?? data.RetailPrice),
    domesticPrice: toNullableNumber(data.domesticPrice ?? data.DomesticPrice),
    oemPrice: toNullableNumber(data.oEMPrice ?? data.OEMPrice ?? data.oemPrice ?? data.OemPrice),
    importPrice: toNullableNumber(data.importPrice ?? data.ImportPrice),
    locationCode: toNullableString(data.locationCode ?? data.LocationCode),
    locationBarcode: toNullableString(data.locationBarcode ?? data.LocationBarcode),
  };
}

function normalizeWarehouseLocationLabelPayload(
  payload: WarehouseLocationLabelPrintPayload
): WarehouseLocationLabelPrintPayload {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const middlePackageQuantity = toNullableNumber(data.middlePackageQuantity ?? data.MiddlePackageQuantity);
  return {
    locationGuid: String(data.locationGuid ?? data.LocationGuid ?? ""),
    locationCode: toNullableString(data.locationCode ?? data.LocationCode),
    locationBarcode: toNullableString(data.locationBarcode ?? data.LocationBarcode),
    itemNumber: toNullableString(data.itemNumber ?? data.ItemNumber),
    productName: toNullableString(data.productName ?? data.ProductName),
    middlePackageQuantity: middlePackageQuantity && middlePackageQuantity > 0 ? middlePackageQuantity : 1,
    productCount: toNullableNumber(data.productCount ?? data.ProductCount) ?? 0,
  };
}

async function ensureConnectedPrinter(options?: { status?: "connecting" | "reconnecting"; force?: boolean; preferHotWrite?: boolean }) {
  if (isIosReviewSessionActive()) {
    // 审核模式只展示打印成功结果，不能读取蓝牙状态或连接真实设备。
    return;
  }
  const store = usePrinterStore.getState();
  if (store.autoReconnectPaused) {
    store.setStatus("paused");
    throw new Error("Printer auto-connect is paused. Reconnect it in Settings first.");
  }

  if (options?.preferHotWrite && !options.force && !labelConnectionInvalidated && store.hydrated &&
      store.status === "connected" && store.savedPrinter?.address) {
    // 热连接直接交给原生写入检查 socket，扫码打印只需一次原生调用。
    // 若状态事件漏报断线，写入会失败并清理旧会话；不能自动重印。
    return;
  }

  // 已完成 hydration 时复用内存中的打印机地址，避免每张标签读取一次存储。
  // 冷连接和重连仍读取原生状态，以识别当前 socket 是否属于小票打印机。
  const statusPromise = getNativePrinterStatus();
  const savedPrinterPromise = store.hydrated ? Promise.resolve(store.savedPrinter) : PrinterStorage.getPrinter();
  const [status, savedPrinter] = await Promise.all([statusPromise, savedPrinterPromise]);

  if (!savedPrinter?.address) {
    store.setStatus("disconnected");
    throw new Error("No label printer has been selected yet.");
  }

  store.setSavedPrinter(savedPrinter);
  if (!options?.force && !labelConnectionInvalidated && store.hydrated &&
      store.status === "connected" && status.connected && status.address === savedPrinter.address &&
      store.savedPrinter?.address === savedPrinter.address) {
    store.setStatus("connected");
    store.setLastError(null);
    return;
  }

  if (status.connected && status.address !== savedPrinter.address) {
    // 小票测试会临时占用原生 socket；标签打印前必须确保当前连接回到标签打印机。
    await disconnectPrinter();
  }

  store.setStatus(options?.status ?? "connecting");
  store.setLastError(null);
  try {
    const connected = await connectPrinter(savedPrinter.address, savedPrinter.transport);
    if (!connected) {
      throw new Error("Unable to connect to the saved label printer.");
    }
    // 用户可在连接等待中暂停；不能让完成回调恢复已取消的自动连接。
    if (usePrinterStore.getState().autoReconnectPaused) {
      await disconnectPrinter();
      store.setStatus("paused");
      throw new Error("Printer auto-connect is paused. Reconnect it in Settings first.");
    }
    labelConnectionInvalidated = false;
    store.setStatus("connected");
  } catch (error) {
    store.setStatus(usePrinterStore.getState().autoReconnectPaused ? "paused" : "error");
    store.setLastError(error instanceof Error ? error.message : String(error));
    throw error;
  }
}

export async function scanPrinterDevices() {
  if (isIosReviewSessionActive()) {
    // 固定演示设备让审核员可完整体验扫描、选择和测试流程。
    return [
      { ...IOS_REVIEW_LABEL_PRINTER, bonded: true, connected: false },
      { ...IOS_REVIEW_RECEIPT_PRINTER, bonded: true, connected: false },
    ];
  }
  return scanPrinters();
}

export async function getPrinterStatus() {
  if (isIosReviewSessionActive()) {
    return {
      supported: true,
      enabled: true,
      connected: true,
      address: IOS_REVIEW_LABEL_PRINTER.address,
    };
  }
  return getNativePrinterStatus();
}

export async function selectPrinter(device: PrinterDevice) {
  // 不可连接的设备必须在恢复自动重连或修改保存/连接状态前拒绝。
  assertPrinterSelectable(device);
  if (isIosReviewSessionActive()) {
    const store = usePrinterStore.getState();
    store.setSavedPrinter(toSavedPrinter(device));
    store.setLastError(null);
    store.setStatus("connected");
    return true;
  }
  resumePrinterAutoReconnect();
  const selectionIntent = autoReconnectIntent;
  return runPrinterOperation(async () => {
    const store = usePrinterStore.getState();
    const previousPrinter = store.hydrated ? store.savedPrinter : await PrinterStorage.getPrinter();
    const selectedPrinter = toSavedPrinter(device);
    store.setStatus("connecting");
    store.setLastError(null);

    try {
      if (autoReconnectIntent !== selectionIntent || usePrinterStore.getState().autoReconnectPaused) {
        throw new Error("Printer connection was cancelled.");
      }

      const currentStatus = await getNativePrinterStatus();
      if (currentStatus.connected && currentStatus.address !== selectedPrinter.address) {
        await disconnectPrinter();
      }
      const connected = currentStatus.connected && currentStatus.address === selectedPrinter.address
        ? true
        : await connectPrinter(selectedPrinter.address, selectedPrinter.transport);
      if (!connected) {
        throw new Error("Unable to connect to the selected label printer.");
      }
      if (autoReconnectIntent !== selectionIntent || usePrinterStore.getState().autoReconnectPaused) {
        await disconnectPrinter();
        throw new Error("Printer connection was cancelled.");
      }

      // 连接成功后才保存，避免连不上的地址进入自动重连。
      await PrinterStorage.setPrinter(selectedPrinter);
      store.setSavedPrinter(selectedPrinter);
      labelConnectionInvalidated = false;
      store.setLastError(null);
      store.setStatus("connected");
      return true;
    } catch (error) {
      store.setSavedPrinter(previousPrinter);
      store.setLastError(error instanceof Error ? error.message : String(error));
      if (usePrinterStore.getState().autoReconnectPaused) {
        store.setStatus("paused");
        throw error;
      }
      try {
        const currentStatus = await getNativePrinterStatus();
        store.setStatus(
          currentStatus.connected && currentStatus.address === previousPrinter?.address
            ? "connected"
            : "error"
        );
      } catch {
        store.setStatus("error");
      }
      throw error;
    }
  });
}

export async function getSavedPrinter() {
  if (isIosReviewSessionActive()) {
    return IOS_REVIEW_LABEL_PRINTER;
  }
  const store = usePrinterStore.getState();
  return store.hydrated ? store.savedPrinter : PrinterStorage.getPrinter();
}

export async function getSavedReceiptPrinter() {
  if (isIosReviewSessionActive()) {
    return IOS_REVIEW_RECEIPT_PRINTER;
  }
  return PrinterStorage.getReceiptPrinter();
}

export async function clearSavedPrinter() {
  if (isIosReviewSessionActive()) {
    const store = usePrinterStore.getState();
    store.setSavedPrinter(null);
    store.setLastError(null);
    store.setStatus("idle");
    return;
  }
  stopPrinterAutoReconnect();
  return runPrinterOperation(async () => {
    await disconnectPrinter();
    await PrinterStorage.clearPrinter();
    const store = usePrinterStore.getState();
    store.setSavedPrinter(null);
    labelConnectionInvalidated = false;
    store.setAutoReconnectPaused(false);
    store.setLastError(null);
    store.setStatus("idle");
  });
}

export async function hydrateSavedPrinter() {
  if (isIosReviewSessionActive()) {
    const store = usePrinterStore.getState();
    store.setSavedPrinter(IOS_REVIEW_LABEL_PRINTER);
    store.setStatus("connected");
    store.setHydrated(true);
    return IOS_REVIEW_LABEL_PRINTER;
  }
  const savedPrinter = await PrinterStorage.getPrinter();
  const store = usePrinterStore.getState();
  store.setSavedPrinter(savedPrinter);
  store.setHydrated(true);
  return savedPrinter;
}

export async function hydrateSavedReceiptPrinter() {
  if (isIosReviewSessionActive()) {
    const store = useReceiptPrinterStore.getState();
    store.setSavedPrinter(IOS_REVIEW_RECEIPT_PRINTER);
    store.setStatus("idle");
    store.setHydrated(true);
    return IOS_REVIEW_RECEIPT_PRINTER;
  }
  const savedPrinter = await PrinterStorage.getReceiptPrinter();
  const store = useReceiptPrinterStore.getState();
  store.setSavedPrinter(savedPrinter);
  store.setHydrated(true);
  return savedPrinter;
}

export async function connectSavedPrinter(options?: { status?: "connecting" | "reconnecting"; force?: boolean }) {
  if (isIosReviewSessionActive()) {
    const store = usePrinterStore.getState();
    store.setSavedPrinter(IOS_REVIEW_LABEL_PRINTER);
    store.setLastError(null);
    store.setStatus("connected");
    return true;
  }
  return runPrinterOperation(async () => {
    // 入队后再次读取暂停状态，避免旧的自动重连任务覆盖用户手动断开。
    if (usePrinterStore.getState().autoReconnectPaused) return false;
    await ensureConnectedPrinter(options);
    return true;
  });
}

export async function startPrinterAutoConnect() {
  resumePrinterAutoReconnect();
  return connectSavedPrinter({ status: "connecting", force: true });
}

export async function disconnectCurrentPrinter(options?: { pauseAutoReconnect?: boolean }) {
  if (isIosReviewSessionActive()) {
    const pause = options?.pauseAutoReconnect ?? false;
    const store = usePrinterStore.getState();
    store.setLastError(null);
    store.setStatus(pause ? "paused" : "disconnected");
    return true;
  }
  const pause = options?.pauseAutoReconnect ?? false;
  // 先暂停，再等待正在写入的操作结束，排队中的重连也能看到最新意图。
  autoReconnectIntent += 1;
  usePrinterStore.getState().setAutoReconnectPaused(pause);
  return runPrinterOperation(async () => {
    await disconnectPrinter();
    const store = usePrinterStore.getState();
    store.setLastError(null);
    store.setStatus(store.autoReconnectPaused ? "paused" : "disconnected");
    return true;
  });
}

export function resumePrinterAutoReconnect() {
  autoReconnectIntent += 1;
  const store = usePrinterStore.getState();
  store.setAutoReconnectPaused(false);
  if (store.savedPrinter) {
    store.setStatus("disconnected");
  }
}

export function stopPrinterAutoReconnect() {
  autoReconnectIntent += 1;
  usePrinterStore.getState().setAutoReconnectPaused(true);
}

export async function syncPrinterStatus() {
  if (isIosReviewSessionActive()) {
    const status = {
      supported: true,
      enabled: true,
      connected: true,
      address: IOS_REVIEW_LABEL_PRINTER.address,
    };
    const store = usePrinterStore.getState();
    store.setSavedPrinter(IOS_REVIEW_LABEL_PRINTER);
    store.setLastError(null);
    store.setStatus("connected");
    return status;
  }
  const reportedStatus = await getPrinterStatus();
  // 清理失败时仍不能把已确认不可写的旧连接重新显示为“已连接”。
  const nativeStatus = labelConnectionInvalidated
    ? { ...reportedStatus, connected: false }
    : reportedStatus;
  const store = usePrinterStore.getState();

  if (!store.savedPrinter) {
    store.setStatus(nativeStatus.connected ? "connected" : "idle");
    return nativeStatus;
  }

  if (nativeStatus.connected && nativeStatus.address === store.savedPrinter.address) {
    store.setStatus("connected");
    store.setLastError(null);
  } else if (store.autoReconnectPaused) {
    store.setStatus("paused");
  } else if (store.status !== "connecting" && store.status !== "reconnecting") {
    store.setStatus("disconnected");
  }

  return nativeStatus;
}

export async function testPrinterConnection() {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runLabelPrint(() => printRawCommand("! 0 200 200 160 1\r\nPAGE-WIDTH 570\r\nTEXT 7 0 20 30 HB LABEL PRINTER\r\nTEXT 4 0 20 78 Connection OK\r\nTEXT 4 0 20 118 TEST\r\nPRINT\r\n"));
}

export async function selectReceiptPrinter(device: PrinterDevice) {
  // 必须在读取状态、断开连接或写入存储前拒绝不可连接的设备。
  assertPrinterSelectable(device);
  if (isIosReviewSessionActive()) {
    const store = useReceiptPrinterStore.getState();
    store.setSavedPrinter(toSavedPrinter(device));
    store.setLastError(null);
    store.setStatus("idle");
    return true;
  }
  const nextPrinter = toSavedPrinter(device);

  // 选择只写入小票机配置，不连接原生 socket；BLE 设备在测试打印时按保存的类型直连。
  await PrinterStorage.setReceiptPrinter(nextPrinter);
  const store = useReceiptPrinterStore.getState();
  store.setSavedPrinter(nextPrinter);
  store.setAutoReconnectPaused(false);
  store.setLastError(null);
  store.setStatus("idle");
  return true;
}

export async function clearSavedReceiptPrinter() {
  if (isIosReviewSessionActive()) {
    const store = useReceiptPrinterStore.getState();
    store.setSavedPrinter(null);
    store.setLastError(null);
    store.setStatus("idle");
    return;
  }
  return runPrinterOperation(async () => {
    const savedPrinter = await PrinterStorage.getReceiptPrinter();
    const nativeStatus = await getPrinterStatus();
    if (savedPrinter?.address && nativeStatus.connected && nativeStatus.address === savedPrinter.address) {
      await disconnectPrinter();
    }

    await PrinterStorage.clearReceiptPrinter();
    const store = useReceiptPrinterStore.getState();
    store.setSavedPrinter(null);
    store.setAutoReconnectPaused(false);
    store.setLastError(null);
    store.setStatus("idle");
  });
}

export async function testReceiptPrinterConnection() {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runPrinterOperation(async () => {
    const savedPrinter = await PrinterStorage.getReceiptPrinter();
    const receiptStore = useReceiptPrinterStore.getState();
    if (!savedPrinter?.address) {
      receiptStore.setStatus("disconnected");
      throw new Error("No receipt printer has been selected yet.");
    }

    receiptStore.setSavedPrinter(savedPrinter);
    receiptStore.setStatus("connecting");
    receiptStore.setLastError(null);

    const labelStoreBeforeTest = usePrinterStore.getState();
    const previousLabelAutoReconnectPaused = labelStoreBeforeTest.autoReconnectPaused;
    const intentBeforeTest = autoReconnectIntent;
    labelStoreBeforeTest.setAutoReconnectPaused(true);
    if (labelStoreBeforeTest.savedPrinter) {
      labelStoreBeforeTest.setStatus("paused");
    }

    let testSucceeded = false;
    let testError: unknown = null;
    let cleanupError: unknown = null;

    try {
      const connected = await connectPrinter(savedPrinter.address, savedPrinter.transport);
      if (!connected) {
        receiptStore.setStatus("error");
        throw new Error("Unable to connect to the saved receipt printer.");
      }

      receiptStore.setStatus("connected");
      await printRawCommand(buildReceiptPrinterTestCommand());
      testSucceeded = true;
    } catch (error) {
      testError = error;
      receiptStore.setStatus("error");
      receiptStore.setLastError(error instanceof Error ? error.message : String(error));
    } finally {
      try {
        await disconnectPrinter();
      } catch (error) {
        cleanupError = error;
      }

      const labelStoreAfterTest = usePrinterStore.getState();
      const paused = autoReconnectIntent === intentBeforeTest
        ? previousLabelAutoReconnectPaused
        : labelStoreAfterTest.autoReconnectPaused;
      labelStoreAfterTest.setAutoReconnectPaused(paused);
      // 原生蓝牙模块只有一个 socket，小票测试结束后恢复标签打印机原自动重连策略。
      labelStoreAfterTest.setStatus(
        labelStoreAfterTest.savedPrinter
          ? paused
            ? "paused"
            : "disconnected"
          : "idle"
      );
    }

    if (cleanupError) {
      const cleanupMessage = cleanupError instanceof Error ? cleanupError.message : String(cleanupError);
      receiptStore.setStatus("error");
      if (testError) {
        const testMessage = testError instanceof Error ? testError.message : String(testError);
        receiptStore.setLastError(`${testMessage}; disconnect failed: ${cleanupMessage}`);
      } else {
        receiptStore.setLastError(cleanupMessage);
      }
      if (testSucceeded) {
        throw new Error(`Receipt test print was sent, but disconnect failed: ${cleanupMessage}`);
      }
    }

    if (testError) {
      throw testError;
    }

    receiptStore.setStatus("disconnected");
    return true;
  });
}

export async function printProductLabel(detail: ProductDetail, overrides?: ProductLabelOverrides, printType?: string | null) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runLabelPrint(() => printNativeProductLabel(buildPayload(detail, overrides), printType));
}

export async function printProductLabelPayload(payload: ProductLabelPrintPayload, printType?: string | null) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runLabelPrint(() => printNativeProductLabel(payload, printType));
}

export async function printDiscountLabel(detail: ProductDetail, printType?: string | null) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runLabelPrint(() => printNativeDiscountLabel(buildPayload(detail), printType));
}

export async function printDiscountLabelPayload(payload: ProductLabelPrintPayload, printType?: string | null) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  // 价签更新的折扣标签也必须共用连接队列和断线恢复逻辑。
  return runLabelPrint(() => printNativeDiscountLabel(payload, printType));
}

export async function printClearanceLabel(detail: ProductDetail) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runLabelPrint(() => printNativeClearanceLabel(buildPayload(detail)));
}

export async function printBigDiscountLabel(detail: ProductDetail, printType?: string | null) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runLabelPrint(() => printNativeBigDiscountLabel(buildPayload(detail), printType));
}

export async function printWarehouseProductLabel(payload: WarehouseProductLabelPrintPayload) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runLabelPrint(() => printNativeWarehouseProductLabel(normalizeWarehouseProductLabelPayload(payload)));
}

export async function printWarehouseLocationLabel(payload: WarehouseLocationLabelPrintPayload) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  return runLabelPrint(() => printNativeWarehouseLocationLabel(normalizeWarehouseLocationLabelPayload(payload)));
}

export async function printEmployeeCashierBarcodeLabel(
  payload: EmployeeCashierBarcodeLabelPrintPayload
) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  const status = await getPrinterStatus();
  if (status.supported && !status.enabled) {
    throw new Error("Bluetooth is disabled.");
  }
  // 员工条码复用标签打印机、蓝牙权限、GB18030 编码和现有单连接链路。
  return runLabelPrint(() => printRawCommand(buildEmployeeCashierBarcodeLabelCommand(payload)));
}

export async function printCashRegisterUserBarcodeLabel(
  payload: CashRegisterUserBarcodeLabelPrintPayload
) {
  if (isIosReviewSessionActive()) {
    return true;
  }
  const status = await getPrinterStatus();
  if (status.supported && !status.enabled) {
    throw new Error("Bluetooth is disabled.");
  }
  // 老收银系统员工条码与个人码共用标签打印机链路；CPCL 在 JS 生成，走已有原生 print，无需重建原生包。
  return runLabelPrint(() => printRawCommand(buildCashRegisterUserBarcodeLabelCommand(payload)));
}
