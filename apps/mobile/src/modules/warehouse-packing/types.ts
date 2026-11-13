// 与后端 WarehousePackingDtos.cs 对应；后端统一 WhenWritingNull，空字段在归一化时补成 null。

export const CARTON_STATUS = { packing: 1, sealed: 2, voided: 9 } as const;
export const PALLET_STATUS = { building: 1, sealed: 2, voided: 9 } as const;
/** 去向：0 未指定、1 分店、2 运输公司（此时 storeCode 是最终收货分店，可空）。 */
export const DESTINATION_TYPE = { unassigned: 0, store: 1, carrier: 2 } as const;

export type CartonFilter = "all" | "packing" | "sealed" | "loose";
export type PalletFilter = "all" | "building" | "sealed";
/** 装箱计数方式：每扫 +1、按中包、扫后输数量。 */
export type ScanCountMode = "each" | "pack" | "ask";

export interface PackDestination {
  destinationType: number;
  storeCode: string | null;
  storeName: string | null;
  carrierGuid: string | null;
  carrierName: string | null;
}

export interface PackDestinationRequest {
  destinationType: number;
  destinationStoreCode: string | null;
  carrierGuid: string | null;
}

export interface PackCartonSummary {
  cartonGuid: string;
  cartonNo: string;
  status: number;
  originName: string;
  destination: PackDestination;
  skuCount: number;
  pieceCount: number;
  palletGuid: string | null;
  palletNo: string | null;
  locationCode: string | null;
  weightKg: number | null;
  orderGuid: string | null;
  orderNo: string | null;
  createdByName: string;
  createdAtUtc: string;
  sealedByName: string | null;
  sealedAtUtc: string | null;
  updatedAtUtc: string;
}

export interface PackCartonItem {
  productCode: string;
  itemNumber: string | null;
  productName: string | null;
  barcode: string | null;
  productImage: string | null;
  quantity: number;
  middlePackageQuantity: number | null;
  packingQuantity: number | null;
  /** 关联订货单时的配货数；商品不在订货单上为 null。 */
  orderQuantity: number | null;
  packedInOtherCartons: number;
  updatedAtUtc: string;
}

export interface PackProductCandidate {
  productCode: string;
  itemNumber: string | null;
  productName: string | null;
  barcode: string | null;
  productImage: string | null;
}

export interface PackCartonDetail extends PackCartonSummary {
  remark: string | null;
  items: PackCartonItem[];
  changedProductCode: string | null;
}

export interface PackCartonList {
  items: PackCartonSummary[];
  packingCount: number;
  sealedCount: number;
  looseSealedCount: number;
}

export interface PackPalletSummary {
  palletGuid: string;
  palletNo: string;
  status: number;
  originName: string;
  destination: PackDestination;
  consignmentNo: string | null;
  locationCode: string | null;
  weightKg: number | null;
  cartonCount: number;
  skuCount: number;
  pieceCount: number;
  createdByName: string;
  createdAtUtc: string;
  sealedByName: string | null;
  sealedAtUtc: string | null;
  updatedAtUtc: string;
}

export interface PackPalletDetail extends PackPalletSummary {
  remark: string | null;
  cartons: PackCartonSummary[];
}

export interface PackPalletList {
  items: PackPalletSummary[];
  buildingCount: number;
  sealedCount: number;
}

export interface PackResolveResult {
  kind: "carton" | "pallet";
  guid: string;
  no: string;
}

export interface PackCarrier {
  carrierGuid: string;
  name: string;
  remark: string | null;
  isActive: boolean;
}

export interface PackOrderOption {
  orderGuid: string;
  orderNo: string | null;
  storeCode: string | null;
  storeName: string | null;
  flowStatus: number;
  orderDate: string | null;
}

export interface PackLocateRow {
  cartonGuid: string;
  cartonNo: string;
  cartonStatus: number;
  palletGuid: string | null;
  palletNo: string | null;
  locationCode: string | null;
  originName: string;
  destination: PackDestination;
  quantity: number;
  orderNo: string | null;
  updatedAtUtc: string;
}

export interface PackLocateResult {
  product: PackProductCandidate;
  middlePackageQuantity: number | null;
  packingQuantity: number | null;
  stockQuantity: number | null;
  packedQuantity: number;
  cartonCount: number;
  palletCount: number;
  rows: PackLocateRow[];
}

/** 去向弹层的取值；箱和托盘共用，托盘才有运单号与备注，新建箱才选订货单。 */
export interface DestinationFormValue {
  destinationType: number;
  storeCode: string | null;
  storeName: string | null;
  carrierGuid: string | null;
  carrierName: string | null;
  orderGuid: string | null;
  orderNo: string | null;
  consignmentNo: string;
  remark: string;
}
