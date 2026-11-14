// 扫码打包（分店 + 总仓共用）：与后端 Features/Packing/PackingDtos.cs 一一对应。

/** 箱状态：1 装箱中、2 已封箱、3 在路上、9 已作废。 */
export const PACK_CARTON_STATUS = { Packing: 1, Sealed: 2, InTransit: 3, Voided: 9 } as const
/** 托盘状态：1 组托中、2 已封托、3 在路上、9 已作废。 */
export const PACK_PALLET_STATUS = { Building: 1, Sealed: 2, InTransit: 3, Voided: 9 } as const
/** 去向：0 未指定、1 本地存放、2 其他分店、3 总仓（编码固定 WAREHOUSE）。 */
export const PACK_DESTINATION_TYPE = { Unassigned: 0, Local: 1, Store: 2, Warehouse: 3 } as const
/** 总仓的所属地点编码。 */
export const PACK_WAREHOUSE_CODE = 'WAREHOUSE'

export const PACK_ERROR_CODE = {
  NotAllowed: 'PACKING_NOT_ALLOWED',
  OwnerRequired: 'OWNER_REQUIRED',
  OwnerNotAllowed: 'OWNER_NOT_ALLOWED',
  CodeNotFound: 'CODE_NOT_FOUND',
  CodeAmbiguous: 'CODE_AMBIGUOUS',
  CartonNotSealed: 'CARTON_NOT_SEALED',
  CartonOnOtherPallet: 'CARTON_ON_OTHER_PALLET',
  CartonOnPallet: 'CARTON_ON_PALLET',
  CartonInTransit: 'CARTON_IN_TRANSIT',
  PalletInTransit: 'PALLET_IN_TRANSIT',
  DestinationMismatch: 'DESTINATION_MISMATCH',
  NotShippable: 'NOT_SHIPPABLE',
  NotInTransit: 'NOT_IN_TRANSIT',
  NotForYou: 'NOT_FOR_YOU',
  CountMismatch: 'COUNT_MISMATCH',
  OrderNotFound: 'ORDER_NOT_FOUND',
} as const

/** 去向请求体：destinationCode 是其他分店的编码（总仓不用传）；carrierGuid 为空 = 自己送 / 对方来取。 */
export interface PackDestinationRequest {
  destinationType: number
  destinationCode?: string | null
  carrierGuid?: string | null
}

export interface PackDestination {
  destinationType: number
  /** 收货地点编码（分店编码或 WAREHOUSE）；本地存放 / 未指定时为空。 */
  code?: string | null
  name?: string | null
  carrierGuid?: string | null
  carrierName?: string | null
}

export interface PackOwner {
  code: string
  name: string
  isWarehouse: boolean
}

export interface PackScope {
  owners: PackOwner[]
  isManager: boolean
  isAdmin: boolean
}

export interface PackCartonSummary {
  cartonGuid: string
  cartonNo: string
  status: number
  ownerCode: string
  ownerName: string
  originCode: string
  /** 标签上的 FROM。 */
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
  dispatchedByName?: string | null
  dispatchedAtUtc?: string | null
  receivedByName?: string | null
  receivedAtUtc?: string | null
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
  /** 关联订货单时该商品的配货数；没有关联订货单为空。 */
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
  storedCount: number
  outgoingCount: number
  incomingCount: number
}

export interface PackPalletSummary {
  palletGuid: string
  palletNo: string
  status: number
  ownerCode: string
  ownerName: string
  originCode: string
  originName: string
  destination: PackDestination
  consignmentNo?: string | null
  locationCode?: string | null
  weightKg?: number | null
  cartonCount: number
  /** 托盘上还没被签收的箱数。 */
  inTransitCartonCount: number
  skuCount: number
  pieceCount: number
  createdByName: string
  createdAtUtc: string
  sealedByName?: string | null
  sealedAtUtc?: string | null
  dispatchedByName?: string | null
  dispatchedAtUtc?: string | null
  receivedByName?: string | null
  receivedAtUtc?: string | null
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
  /** 签收整托数量对不上时带回托盘上实际还在路上的箱数。 */
  actualInTransitCartonCount?: number | null
}

export interface PackPalletList {
  items: PackPalletSummary[]
  buildingCount: number
  storedCount: number
}

export interface PackResolveResult {
  kind: 'carton' | 'pallet'
  guid: string
  no: string
  palletGuid?: string | null
}

export interface PackCarrier {
  carrierGuid: string
  name: string
  remark?: string | null
  isActive: boolean
}

export interface PackLocation {
  locationGuid: string
  ownerCode: string
  name: string
  /** 位置标签上的条码；总仓取货位条码。 */
  barcode: string
  isActive: boolean
}

export interface PackEvent {
  action: number
  detail?: string | null
  operatorName: string
  createdAtUtc: string
}

/** 待签收 / 最近签收列表里的一行：一个托盘，或一只散箱。 */
export interface PackIncomingItem {
  kind: 'pallet' | 'carton'
  guid: string
  no: string
  originCode: string
  originName: string
  carrierName?: string | null
  consignmentNo?: string | null
  cartonCount: number
  /** 托盘上已经签收的箱数；散箱为 0。 */
  receivedCartonCount: number
  pieceCount: number
  dispatchedAtUtc?: string | null
  dispatchedByName?: string | null
  receivedAtUtc?: string | null
  receivedByName?: string | null
}

export interface PackIncoming {
  pending: PackIncomingItem[]
  recent: PackIncomingItem[]
}

/** 扫码签收：扫托盘标签或箱标签后展示要核对的内容。 */
export interface PackReceiveLookup {
  kind: 'pallet' | 'carton'
  pallet?: PackPalletDetail | null
  carton?: PackCartonDetail | null
  scannedCartonGuid?: string | null
  scannedCartonNo?: string | null
  /** 通过箱标签找到托盘（托盘标签丢了）。 */
  viaCartonLabel: boolean
  inTransitCartonCount: number
  canReceive: boolean
  /** 不能签收的原因：NOT_IN_TRANSIT / NOT_FOR_YOU。 */
  blockReason?: string | null
}

export type PackLocateGroup = 'stored' | 'transit' | 'elsewhere' | 'other'

export interface PackLocateRow {
  cartonGuid: string
  cartonNo: string
  cartonStatus: number
  palletGuid?: string | null
  palletNo?: string | null
  locationCode?: string | null
  ownerCode: string
  ownerName: string
  originName: string
  destination: PackDestination
  quantity: number
  orderNo?: string | null
  updatedAtUtc: string
  /** stored 本地存放 / transit 在路上 / elsewhere 对方已签收 / other 其他分店（仅查全部分店时）。 */
  group: PackLocateGroup | string
}

export interface PackLocateResult {
  product: PackProductCandidate
  middlePackageQuantity?: number | null
  packingQuantity?: number | null
  /** 仅总仓有意义：现有仓库库存，仅作对照。 */
  stockQuantity?: number | null
  packedQuantity: number
  cartonCount: number
  palletCount: number
  rows: PackLocateRow[]
}

export type PackCartonFilter = 'all' | 'packing' | 'stored' | 'outgoing'
export type PackPalletFilter = 'all' | 'building' | 'stored' | 'outgoing'

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

export interface PackLocationSaveRequest {
  /** 新建时必填：属于哪家分店。 */
  ownerCode?: string | null
  name: string
  isActive: boolean
}

export interface PackReceivePalletRequest {
  /** 整托签收时必填：实到箱数。 */
  expectedCartonCount?: number | null
  /** 只签收这几只箱（逐箱核对）。 */
  cartonGuids?: string[] | null
  locationCode?: string | null
  /** 托盘标签丢了、扫箱标签找到托盘时传扫到的箱号。 */
  viaCartonNo?: string | null
}

// ---- 二期：仓库订单详情「装箱情况」与分店「按订单签收」 ----
// 后端 JSON 里 null 字段不输出：可空字段一律按「缺失」处理。

/** 订单装箱进度里的一个商品：订货 / 已拣 / 已装件数（对应 WarehousePickingPackProgressLineDto）。 */
export interface WarehousePackProgressLine {
  productCode: string
  productName?: string | null
  itemNumber?: string | null
  orderedPieces: number
  pickedPieces: number
  packedPieces: number
}

/** 订单名下的一只箱（作废的不返回）。 */
export interface WarehousePackProgressCarton {
  cartonGuid: string
  cartonNo: string
  /** 1 装箱中 / 2 已封箱 / 3 在途。 */
  status: number
  skuCount: number
  pieceCount: number
  palletNo?: string | null
  createdByName: string
  /** Web 账号认不出拣货人，恒为 false；界面不使用。 */
  isMine: boolean
}

export interface WarehousePackProgress {
  orderGuid: string
  orderNo?: string | null
  storeCode?: string | null
  storeName?: string | null
  orderDate?: string | null
  flowStatus: number
  /** 拣货会话状态：1 拣货中 / 2 已提交；还没开始拣货时缺失。 */
  sessionStatus?: number | null
  lines: WarehousePackProgressLine[]
  cartons: WarehousePackProgressCarton[]
  /** 装箱中的箱数（含空箱）。 */
  openCartonCount: number
  emptyCartonCount: number
  sealedCartonCount: number
  /** 每个商品拣货数 = 装箱数，且没有非空的装箱中的箱。订单没有任何箱时拣了货也是 false。 */
  reconciled: boolean
}

/** 分店按订单签收：该订单发给本店的一只箱（在路上，或已签收归本店）。 */
export interface PackOrderReceiveCarton {
  cartonGuid: string
  cartonNo: string
  status: number
  palletNo?: string | null
  skuCount: number
  pieceCount: number
}

export interface PackOrderReceiveProduct {
  productCode: string
  productName?: string | null
  itemNumber?: string | null
  orderedPieces: number
  /** 配货数；订单所有行都没有配货数时缺失。 */
  allocPieces?: number | null
  /** 已发出件数：该订单在途加已签收的箱内件数合计。 */
  shippedPieces: number
}

export interface PackOrderReceive {
  orderGuid: string
  orderNo?: string | null
  storeCode: string
  cartons: PackOrderReceiveCarton[]
  inTransitCount: number
  receivedCount: number
  products: PackOrderReceiveProduct[]
  /** 仅 409 COUNT_MISMATCH 时带回：系统记录里该订单发给本店、还在路上的箱数。 */
  actualInTransitCartonCount?: number | null
}
