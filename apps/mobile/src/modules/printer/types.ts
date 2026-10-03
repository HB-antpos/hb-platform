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
}
