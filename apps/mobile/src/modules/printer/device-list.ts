import type { PrinterDevice, PrinterTransport } from "@/modules/printer/types";

export interface PrinterTransportFilters {
  showClassic: boolean;
  showBle: boolean;
}

// 经典蓝牙与 BLE 都可连接，默认同时列出，由每行的提示区分“需系统配对”和“可直接连接”。
export const DEFAULT_PRINTER_TRANSPORT_FILTERS: Readonly<PrinterTransportFilters> = {
  showClassic: true,
  showBle: true,
};

export function getPrinterTransport(device: PrinterDevice): PrinterTransport {
  switch (device.transport) {
    case "classic":
    case "ble":
    case "dual":
      return device.transport;
    default:
      // 旧原生包没有类型字段，不能根据名称或地址猜测传输能力。
      return "unknown";
  }
}

/** 列表展示用的通道：iOS 只能走 BLE，扫描结果不带类型字段。 */
export function getDisplayPrinterTransport(device: PrinterDevice, platform: string): PrinterTransport {
  return platform === "ios" ? "ble" : getPrinterTransport(device);
}

/** 只有旧安卓原生包（无 BLE GATT 通道）才拒绝 BLE-only 设备。 */
export function isUnsupportedPrinterTransport(device: PrinterDevice, platform: string, bleSupported: boolean) {
  return platform === "android" && !bleSupported && getPrinterTransport(device) === "ble";
}

/** 安卓经典/双模/未知类型设备走 RFCOMM，必须先在系统蓝牙设置中配对；BLE 直接连接。 */
export function requiresSystemPairing(device: PrinterDevice, platform: string) {
  return platform === "android" && !device.bonded && getPrinterTransport(device) !== "ble";
}

export function getPrinterDeviceIcon(device: PrinterDevice): "printer" | "bluetooth" {
  // 影像主类别也包含相机/扫描仪，只有 Printer 能力位才显示打印机；仍不代表协议兼容性。
  return typeof device.deviceClass === "number"
    && (device.deviceClass & 0x1f00) === 0x0600
    && (device.deviceClass & 0x0080) !== 0
    ? "printer"
    : "bluetooth";
}

export function filterPrinterDevices(
  devices: PrinterDevice[],
  options: PrinterTransportFilters & { xpOnly?: boolean; platform: string }
) {
  return orderPrinterDevices(devices.filter((device) => {
    if (options.xpOnly && !device.name?.trim().toUpperCase().startsWith("XP")) return false;
    if (options.platform !== "android") return true;
    const transport = getPrinterTransport(device);
    if (transport === "ble") return options.showBle;
    if (transport === "dual") return options.showClassic || options.showBle;
    return options.showClassic;
  }));
}

export function orderPrinterDevices(devices: PrinterDevice[]) {
  return devices
    .map((device, index) => ({ device, index }))
    .sort(
      (left, right) =>
        Number(right.device.bonded) - Number(left.device.bonded) || left.index - right.index
    )
    .map(({ device }) => device);
}
