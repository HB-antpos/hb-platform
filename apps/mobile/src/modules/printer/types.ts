import type { PersonalCodeSymbology } from "./personal-code-symbology";

export type PrinterBarcodeKind = "EAN13" | "CODE128";

export type PrinterTransport = "classic" | "ble" | "dual" | "unknown";

export interface SavedPrinter {
  name?: string | null;
  address: string;
  /** 选择时记录的蓝牙类型；安卓重连时据此选择 RFCOMM 或 BLE GATT，旧保存记录没有此字段。 */
  transport?: PrinterTransport;
}

export interface PrinterDevice {
  name?: string | null;
  address: string;
  bonded: boolean;
  connected: boolean;
  transport?: PrinterTransport;
  deviceClass?: number | null;
}

export interface PrinterStatus {
  supported: boolean;
  enabled: boolean;
  connected: boolean;
  address?: string | null;
}

/**
 * 安卓原生层缓冲的蓝牙链路事件（ACL / 配对 / 蓝牙开关广播、每次连接尝试的现场与耗时、BLE GATT 状态码）。
 * ev 是事件类型，atMs 是发生时刻（epoch 毫秒）；其余字段都是标量，缺失值为 null。
 */
export type NativeLinkEvent = {
  ev: string;
  atMs: number;
} & Record<string, string | number | boolean | null>;

export interface PreparedBarcode {
  value: string;
  kind: PrinterBarcodeKind;
}

export interface ProductLabelPrintPayload {
  productName: string;
  itemNumber?: string | null;
  grade?: string | null;
  supplierName?: string | null;
  barcode?: string | null;
  retailPrice?: number | null;
  discountRate?: number | null;
  clearanceBarcode?: string | null;
  clearancePrice?: number | null;
}

export interface WarehouseProductLabelPrintPayload {
  productCode: string;
  productName: string;
  itemNumber?: string | null;
  barcode?: string | null;
  supplierName?: string | null;
  middlePackageQuantity?: number | null;
  purchasePrice?: number | null;
  retailPrice?: number | null;
  domesticPrice?: number | null;
  oemPrice?: number | null;
  importPrice?: number | null;
  locationCode?: string | null;
  locationBarcode?: string | null;
}

export interface WarehouseLocationLabelPrintPayload {
  locationGuid: string;
  locationCode?: string | null;
  locationBarcode?: string | null;
  itemNumber?: string | null;
  productName?: string | null;
  middlePackageQuantity?: number | null;
  productCount: number;
}

export interface CashRegisterUserBarcodeLabelPrintPayload {
  operatorName: string;
  storeName?: string | null;
  barcode: string;
}

export interface EmployeeCashierBarcodeLabelPrintPayload {
  employeeName: string;
  username: string;
  barcode: string;
  /** 缺省为二维码；仅 "barcode" 时打印 EAN13 一维码。 */
  symbology?: PersonalCodeSymbology;
}

/**
 * 打包箱标签（70×26 mm 英文版）；时间由调用方按本机时间格式化成 "13 NOV 14:32"。
 * destinationType 与后端一致：0 未指定、1 本地存放、2 其他分店、3 总仓。
 */
export interface PackCartonLabelPrintPayload {
  cartonNo: string;
  /** 标签上的 FROM：取后端返回的发出地（分店名 / 总仓名）。 */
  originName: string;
  destinationType: number;
  /** 收货方名称（其他分店 / 总仓）；本地存放和未指定不用。 */
  destinationName?: string | null;
  /** 运输公司；为空且要发出时打 DIRECT。 */
  carrierName?: string | null;
  skuCount: number;
  pieceCount: number;
  orderNo?: string | null;
  printedAt: string;
  operatorName?: string | null;
}

/** 打包托盘标签；同一托盘打多张时带 sheetIndex / sheetTotal（1/2）。 */
export interface PackPalletLabelPrintPayload {
  palletNo: string;
  originName: string;
  destinationType: number;
  destinationName?: string | null;
  carrierName?: string | null;
  cartonCount: number;
  pieceCount: number;
  consignmentNo?: string | null;
  orderNo?: string | null;
  printedAt: string;
  operatorName?: string | null;
  sheetIndex?: number;
  sheetTotal?: number;
}
