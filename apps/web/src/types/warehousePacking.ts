// 仓库扫码打包：与后端 Features/WarehousePacking/WarehousePackingDtos.cs 一一对应。

/** 箱状态：1 装箱中、2 已封箱、9 已作废。 */
export const PACK_CARTON_STATUS = { Packing: 1, Sealed: 2, Voided: 9 } as const
/** 托盘状态：1 组托中、2 已封托、9 已作废。 */
export const PACK_PALLET_STATUS = { Building: 1, Sealed: 2, Voided: 9 } as const
/** 去向：0 未指定、1 分店、2 运输公司（此时分店是最终收货分店，可空）。 */
export const PACK_DESTINATION_TYPE = { Unassigned: 0, Store: 1, Carrier: 2 } as const

export const PACK_ERROR_CODE = {
  NotAllowed: 'PACKING_NOT_ALLOWED',
  CodeNotFound: 'CODE_NOT_FOUND',
  CodeAmbiguous: 'CODE_AMBIGUOUS',
  CartonNotSealed: 'CARTON_NOT_SEALED',
  CartonOnOtherPallet: 'CARTON_ON_OTHER_PALLET',
  DestinationMismatch: 'DESTINATION_MISMATCH',
} as const

export interface PackDestinationRequest {
  destinationType: number
  destinationStoreCode?: string | null
  carrierGuid?: string | null
}

export interface PackDestination {
  destinationType: number
  storeCode?: string | null
  storeName?: string | null
  carrierGuid?: string | null
  carrierName?: string | null
}

export interface PackCartonSummary {
  cartonGuid: string
  cartonNo: string
  status: number
  originName: string
  destination: PackDestination
  skuCount: number
  pieceCount: number
  palletGuid?: string | null
  palletNo?: string | null
  locationCode?: string | null
  weightKg?: number | null
  orderGuid?: string | null
  orderNo?: string | null
  createdByName: string
  createdAtUtc: string
  sealedByName?: string | null
  sealedAtUtc?: string | null
  updatedAtUtc: string
}

export interface PackCartonItem {
  productCode: string
  itemNumber?: string | null
  productName?: string | null
  barcode?: string | null
  productImage?: string | null
  quantity: number
  middlePackageQuantity?: number | null
  packingQuantity?: number | null
  /** 关联订货单时该商品的配货数；商品不在订货单上为空。 */
  orderQuantity?: number | null
  /** 同一订货单的其他箱里已装的数量。 */
  packedInOtherCartons: number
  updatedAtUtc: string
}

export interface PackProductCandidate {
  productCode: string
  itemNumber?: string | null
  productName?: string | null
  barcode?: string | null
  productImage?: string | null
}

export interface PackCartonDetail extends PackCartonSummary {
  remark?: string | null
  /** 最近改动的在前。 */
  items: PackCartonItem[]
  changedProductCode?: string | null
  candidates?: PackProductCandidate[] | null
}

export interface PackCartonList {
  items: PackCartonSummary[]
  packingCount: number
  sealedCount: number
  looseSealedCount: number
}

export interface PackPalletSummary {
  palletGuid: string
  palletNo: string
  status: number
  originName: string
  destination: PackDestination
  consignmentNo?: string | null
  locationCode?: string | null
  weightKg?: number | null
  cartonCount: number
  skuCount: number
  pieceCount: number
  createdByName: string
  createdAtUtc: string
  sealedByName?: string | null
  sealedAtUtc?: string | null
  updatedAtUtc: string
}

export interface PackPalletProduct {
  productCode: string
  itemNumber?: string | null
  productName?: string | null
  barcode?: string | null
  quantity: number
  cartonCount: number
}

export interface PackPalletDetail extends PackPalletSummary {
  remark?: string | null
  cartons: PackCartonSummary[]
  products: PackPalletProduct[]
  /** 加入托盘被拦下时带回的那只箱。 */
  blockedCarton?: PackCartonSummary | null
}

export interface PackPalletList {
  items: PackPalletSummary[]
  buildingCount: number
  sealedCount: number
}

export interface PackResolveResult {
  kind: 'carton' | 'pallet'
  guid: string
  no: string
}

export interface PackCarrier {
  carrierGuid: string
  name: string
  remark?: string | null
  isActive: boolean
}

export interface PackOrderOption {
  orderGuid: string
  orderNo?: string | null
  storeCode?: string | null
  storeName?: string | null
  flowStatus: number
  orderDate?: string | null
}

export interface PackEvent {
  action: number
  detail?: string | null
  operatorName: string
  createdAtUtc: string
}

export interface PackLocateRow {
  cartonGuid: string
  cartonNo: string
  cartonStatus: number
  palletGuid?: string | null
  palletNo?: string | null
  locationCode?: string | null
  originName: string
  destination: PackDestination
  quantity: number
  orderNo?: string | null
  updatedAtUtc: string
}

export interface PackLocateResult {
  product: PackProductCandidate
  middlePackageQuantity?: number | null
  packingQuantity?: number | null
  /** 现有仓库库存，仅作对照。 */
  stockQuantity?: number | null
  packedQuantity: number
  cartonCount: number
  palletCount: number
  rows: PackLocateRow[]
}

export type PackCartonFilter = 'all' | 'packing' | 'sealed' | 'loose'
export type PackPalletFilter = 'all' | 'building' | 'sealed'

export interface PackCartonHeaderRequest extends PackDestinationRequest {
  orderGuid?: string | null
  weightKg?: number | null
  remark?: string | null
}

export interface PackPalletHeaderRequest extends PackDestinationRequest {
  consignmentNo?: string | null
  weightKg?: number | null
  remark?: string | null
}

export interface PackCarrierSaveRequest {
  name: string
  remark?: string | null
  isActive: boolean
}
