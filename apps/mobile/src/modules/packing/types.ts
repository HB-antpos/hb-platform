// 与后端 Features/Packing/PackingDtos.cs 对应；后端统一 WhenWritingNull，空字段在归一化时补成 null。

export const CARTON_STATUS = { packing: 1, sealed: 2, inTransit: 3, voided: 9 } as const;
export const PALLET_STATUS = { building: 1, sealed: 2, inTransit: 3, voided: 9 } as const;
/**
 * 去向：0 未指定、1 本地存放、2 其他分店（code 是分店编码）、3 总仓（编码固定 WAREHOUSE）。
 * 运输公司不是去向，是「怎么送」：carrierGuid 为空 = 自己送 / 对方来取。
 */
export const DESTINATION_TYPE = { unassigned: 0, local: 1, store: 2, warehouse: 3 } as const;
/** 总仓作为所属地点 / 去向的固定编码。 */
export const WAREHOUSE_OWNER_CODE = "WAREHOUSE";

export type CartonFilter = "all" | "packing" | "stored" | "outgoing";
export type PalletFilter = "all" | "building" | "stored" | "outgoing";
/** 装箱计数方式：每扫 +1、按中包、扫后输数量。 */
export type ScanCountMode = "each" | "pack" | "ask";

/** 账号能操作的一个所属地点：一家分店，或总仓。 */
export interface PackOwner {
  code: string;
  name: string;
  isWarehouse: boolean;
}

export interface PackScope {
  owners: PackOwner[];
  isManager: boolean;
  isAdmin: boolean;
}

export interface PackDestination {
  destinationType: number;
  /** 收货地点编码（分店编码或总仓编码）；本地存放 / 未指定时为 null。 */
  code: string | null;
  name: string | null;
  carrierGuid: string | null;
  carrierName: string | null;
}

/** 请求体里的去向（与后端 PackingDestinationRequestDto 对应）。 */
export interface PackDestinationRequest {
  destinationType: number;
  destinationCode: string | null;
  carrierGuid: string | null;
}

export interface PackCartonSummary {
  cartonGuid: string;
  cartonNo: string;
  status: number;
  ownerCode: string;
  ownerName: string;
  originCode: string;
  /** 标签上的 FROM。 */
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
  dispatchedByName: string | null;
  dispatchedAtUtc: string | null;
  receivedByName: string | null;
  receivedAtUtc: string | null;
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
  /** 装箱中的箱数（本地点）。 */
  packingCount: number;
  /** 本地存放：已封箱、所属地点是本地点、没有在路上的箱数。 */
  storedCount: number;
  /** 已发出：本地点发出、还在路上或对方已签收的箱数。 */
  outgoingCount: number;
  /** 待签收：发给本地点、还没签收的托盘和散箱批数。 */
  incomingCount: number;
}

export interface PackPalletSummary {
  palletGuid: string;
  palletNo: string;
  status: number;
  ownerCode: string;
  ownerName: string;
  originCode: string;
  originName: string;
  destination: PackDestination;
  consignmentNo: string | null;
  locationCode: string | null;
  weightKg: number | null;
  cartonCount: number;
  /** 托盘上还没被签收的箱数。 */
  inTransitCartonCount: number;
  skuCount: number;
  pieceCount: number;
  createdByName: string;
  createdAtUtc: string;
  sealedByName: string | null;
  sealedAtUtc: string | null;
  dispatchedByName: string | null;
  dispatchedAtUtc: string | null;
  receivedByName: string | null;
  receivedAtUtc: string | null;
  updatedAtUtc: string;
}

export interface PackPalletDetail extends PackPalletSummary {
  remark: string | null;
  cartons: PackCartonSummary[];
  /** 签收整托数量对不上（COUNT_MISMATCH）时带回托盘上实际还在路上的箱数。 */
  actualInTransitCartonCount: number | null;
}

export interface PackPalletList {
  items: PackPalletSummary[];
  buildingCount: number;
  storedCount: number;
}

export interface PackResolveResult {
  kind: "carton" | "pallet";
  guid: string;
  no: string;
  /** 扫到箱号时：箱所在托盘。 */
  palletGuid: string | null;
}

export interface PackCarrier {
  carrierGuid: string;
  name: string;
  remark: string | null;
  isActive: boolean;
}

/** 分店自己维护的存放位置；总仓取现有货位（barcode 是货位条码）。 */
export interface PackLocation {
  locationGuid: string;
  ownerCode: string;
  name: string;
  barcode: string;
  isActive: boolean;
}

export interface PackIncomingItem {
  kind: "pallet" | "carton";
  guid: string;
  no: string;
  originCode: string;
  originName: string;
  carrierName: string | null;
  consignmentNo: string | null;
  cartonCount: number;
  /** 托盘上已经签收的箱数；散箱为 0。 */
  receivedCartonCount: number;
  pieceCount: number;
  dispatchedAtUtc: string | null;
  dispatchedByName: string | null;
  receivedAtUtc: string | null;
  receivedByName: string | null;
}

export interface PackIncoming {
  pending: PackIncomingItem[];
  recent: PackIncomingItem[];
}

export type ReceiveBlockReason = "NOT_IN_TRANSIT" | "NOT_FOR_YOU";

/** 扫码签收：扫托盘标签或箱标签后，界面据此展示要核对的内容。 */
export interface PackReceiveLookup {
  kind: "pallet" | "carton";
  pallet: PackPalletDetail | null;
  carton: PackCartonDetail | null;
  /** 通过箱标签找到托盘（托盘标签丢了）时，扫到的那只箱。 */
  scannedCartonGuid: string | null;
  scannedCartonNo: string | null;
  viaCartonLabel: boolean;
  inTransitCartonCount: number;
  canReceive: boolean;
  blockReason: ReceiveBlockReason | null;
}

export type LocateGroupKey = "stored" | "transit" | "elsewhere" | "other";

export interface PackLocateRow {
  cartonGuid: string;
  cartonNo: string;
  cartonStatus: number;
  palletGuid: string | null;
  palletNo: string | null;
  locationCode: string | null;
  ownerCode: string;
  ownerName: string;
  originName: string;
  destination: PackDestination;
  quantity: number;
  orderNo: string | null;
  updatedAtUtc: string;
  /** stored 本地存放 / transit 在路上 / elsewhere 对方已签收 / other 其他分店（仅 allStores）。 */
  group: LocateGroupKey;
}

export interface PackLocateResult {
  product: PackProductCandidate;
  middlePackageQuantity: number | null;
  packingQuantity: number | null;
  /** 仅总仓有意义：现有仓库库存，用来对照；打包不改它。 */
  stockQuantity: number | null;
  packedQuantity: number;
  cartonCount: number;
  palletCount: number;
  rows: PackLocateRow[];
}

/** 去向弹层的取值；箱和托盘共用，托盘才有运单号与备注。 */
export interface DestinationFormValue {
  destinationType: number;
  code: string | null;
  name: string | null;
  carrierGuid: string | null;
  carrierName: string | null;
  consignmentNo: string;
  remark: string;
}
