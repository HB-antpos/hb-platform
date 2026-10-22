// 供应商节日预订单接口类型；与后端 BlazorApp.Shared/DTOs/SupplierPreorderDtos.cs 一一对应（camelCase）。

export type SupplierPreorderHolidayType = 'Christmas' | 'Halloween' | 'Easter' | 'Diaries' | 'Custom'
export type SupplierPreorderFormStatus = 'Draft' | 'Open' | 'Closed'
/** 分店订单状态：None 未开始 / Draft 草稿（含提交后又改过）/ Submitted 已提交 */
export type SupplierPreorderStoreOrderStatus = 'None' | 'Draft' | 'Submitted'
export type SupplierPreorderMatchMethod = 'Barcode' | 'ItemNumber' | 'PrevYearItemCode' | 'None'
export type SupplierPreorderPreviewStatus = 'Matched' | 'New' | 'Warning' | 'Invalid'
/** 填回原表时写入的数量单位：箱数 / 件数（Dats 原表各店列是件数） */
export type SupplierPreorderExportQtyUnit = 'Cartons' | 'Pieces'

// ---------- 导入 ----------

/** 列映射：值为 rows[].cells 的列下标（0 起）；null 表示没有这一列 */
export interface SupplierPreorderColumnMapping {
  itemCode: number | null
  name: number | null
  price: number | null
  cartonQty: number | null
  /** 进价备用列：主进价列为空时按行退回（Dats：PNP 为空用 BULK） */
  priceFallback: number | null
  /** 箱规备用列：主箱规列为空时按行退回（Dats：Inner 为空用 Outer） */
  cartonQtyFallback: number | null
  barcode: number | null
  unit: number | null
  cartonCbm: number | null
  image: number | null
  category: number | null
  prevYearItemCode: number | null
  notes: number | null
  seq: number | null
  /** 原表里填下单数量的列（填回原表时写合计） */
  orderQty: number | null
}

export interface SupplierPreorderParsedColumn {
  index: number
  letter?: string | null
  header: string
  sampleValues: string[]
}

export interface SupplierPreorderParsedRow {
  rowNo: number
  cells: (string | null)[]
}

export interface SupplierPreorderHeaderInfo {
  title?: string | null
  quoteNo?: string | null
  offerDate?: string | null
  /** 原表写的截止日期（只有日期） */
  deadlineDate?: string | null
  priceTerms?: string | null
  containerType?: string | null
  seasonYear?: number | null
  holidayType?: SupplierPreorderHolidayType | null
}

export interface SupplierPreorderParseResult {
  sourceFileGuid?: string | null
  fileName?: string | null
  sheetName: string
  headerRowNo: number
  headerSignature: string
  columns: SupplierPreorderParsedColumn[]
  suggestedMapping: SupplierPreorderColumnMapping
  suggestedExportQtyUnit: SupplierPreorderExportQtyUnit
  mappingFromProfile: boolean
  rows: SupplierPreorderParsedRow[]
  skippedRowCount: number
  skippedSamples: string[]
  headerInfo: SupplierPreorderHeaderInfo
  warnings: string[]
}

export interface SupplierPreorderPreviewItem {
  rowNo: number
  seq: number
  supplierItemCode: string
  barcode?: string | null
  name: string
  unit?: string | null
  cartonQty: number
  cartonCbm?: number | null
  price: number
  category?: string | null
  prevYearItemCode?: string | null
  imageUrl?: string | null
  notes?: string | null
  matchMethod: SupplierPreorderMatchMethod
  matchedProductCode?: string | null
  matchedItemNumber?: string | null
  matchedProductName?: string | null
  lastPurchasePrice?: number | null
  /** 与商品表进价相比的变动比例（0.059 = 上涨 5.9%） */
  priceChangeRate?: number | null
  status: SupplierPreorderPreviewStatus
  issues: string[]
}

export interface SupplierPreorderPreviewCounts {
  total: number
  matched: number
  new: number
  warning: number
  invalid: number
  priceChanged: number
}

export interface SupplierPreorderPreviewResult {
  items: SupplierPreorderPreviewItem[]
  counts: SupplierPreorderPreviewCounts
}

export interface SupplierPreorderFormHeaderFields {
  holidayType: SupplierPreorderHolidayType
  seasonYear: number
  title: string
  quoteNo?: string | null
  offerDate?: string | null
  /** UTC ISO 字符串；前端按悉尼时间选日期后换算 */
  deadlineAtUtc?: string | null
  containerType?: string | null
  containerCapacityCbm?: number | null
  priceTerms?: string | null
  historyFrom?: string | null
  historyTo?: string | null
  exportQtyUnit: SupplierPreorderExportQtyUnit
  notes?: string | null
}

export interface SupplierPreorderCreateFormPayload extends SupplierPreorderFormHeaderFields {
  localSupplierCode: string
  sourceFileGuid?: string | null
  sheetName?: string | null
  headerRowNo: number
  headerSignature?: string | null
  mapping: SupplierPreorderColumnMapping
  rows: SupplierPreorderParsedRow[]
}

// ---------- 订单表（管理） ----------

export interface SupplierPreorderFormSummary {
  formGuid: string
  localSupplierCode: string
  supplierName: string
  holidayType: SupplierPreorderHolidayType
  seasonYear: number
  title: string
  quoteNo?: string | null
  status: SupplierPreorderFormStatus
  deadlineAtUtc?: string | null
  itemCount: number
  enabledStoreCount: number
  submittedStoreCount: number
  draftStoreCount: number
  totalCartons: number
  totalAmount: number
  totalCbm: number
  hasSourceFile: boolean
  exportQtyUnit: SupplierPreorderExportQtyUnit
  createdAtUtc: string
  createdBy?: string | null
}

export interface SupplierPreorderFormDetail {
  summary: SupplierPreorderFormSummary
  offerDate?: string | null
  containerType?: string | null
  containerCapacityCbm?: number | null
  priceTerms?: string | null
  historyFrom?: string | null
  historyTo?: string | null
  notes?: string | null
  matchedCount: number
  newCount: number
  warningCount: number
  categories: string[]
}

export interface SupplierPreorderFormQuery {
  page?: number
  pageSize?: number
  keyword?: string
  status?: SupplierPreorderFormStatus | ''
}

export type SupplierPreorderUpdateFormPayload = SupplierPreorderFormHeaderFields

export interface SupplierPreorderFormItemQuery {
  page?: number
  pageSize?: number
  keyword?: string
  match?: 'all' | 'matched' | 'new' | 'warning'
}

export interface SupplierPreorderFormItem {
  itemGuid: string
  seq: number
  supplierItemCode: string
  barcode?: string | null
  name: string
  unit?: string | null
  cartonQty: number
  cartonCbm?: number | null
  price: number
  category?: string | null
  /** 优先我方商品图（COS），没有时为原表图片地址 */
  imageUrl?: string | null
  imageSourceUrl?: string | null
  matchMethod: SupplierPreorderMatchMethod
  matchedProductCode?: string | null
  matchNote?: string | null
  totalCartons: number
}

export interface SupplierPreorderStoreRef {
  storeGuid: string
  storeCode: string
  storeName: string
}

export interface SupplierPreorderFormStoreStatus extends SupplierPreorderStoreRef {
  enabled: boolean
  orderStatus: SupplierPreorderStoreOrderStatus
  submittedAtUtc?: string | null
  submittedBy?: string | null
  lastEditedAtUtc?: string | null
  lastEditedBy?: string | null
  itemCount: number
  cartons: number
  pieces: number
  amount: number
  cbm: number
  /** 下单人员授权里能替这家店下单的人 */
  orderers: string[]
}

export interface SupplierPreorderCopyStoresResult {
  sourceFormGuid?: string | null
  sourceTitle?: string | null
  stores: SupplierPreorderFormStoreStatus[]
}

export interface SupplierPreorderMatrixQuery {
  page?: number
  pageSize?: number
  keyword?: string
  onlyOrdered?: boolean
}

export interface SupplierPreorderMatrixRow {
  itemGuid: string
  seq: number
  supplierItemCode: string
  name: string
  imageUrl?: string | null
  price: number
  cartonQty: number
  cartonCbm?: number | null
  /** 键为 storeGuid，值为箱数 */
  storeCartons: Record<string, number>
  totalCartons: number
  totalPieces: number
  totalAmount: number
  totalCbm: number
}

export interface SupplierPreorderMatrixTotals {
  storeCartons: Record<string, number>
  storePieces: Record<string, number>
  storeAmount: Record<string, number>
  storeCbm: Record<string, number>
  orderedItemCount: number
  totalCartons: number
  totalPieces: number
  totalAmount: number
  totalCbm: number
}

export interface SupplierPreorderMatrix {
  stores: SupplierPreorderStoreRef[]
  rows: { items: SupplierPreorderMatrixRow[]; total: number; page: number; pageSize: number }
  totals: SupplierPreorderMatrixTotals
}

export interface SupplierPreorderSupplierOption {
  code: string
  name: string
}

// ---------- 下单人员授权 ----------

export interface SupplierPreorderOrderer {
  userGuid: string
  userName: string
  fullName?: string | null
  isActive: boolean
  primaryStoreName?: string | null
  canViewOtherStores: boolean
  stores: SupplierPreorderStoreRef[]
  /** 是否有「预订单下单」入口权限；没有时看不到菜单 */
  hasOrderEntryPermission: boolean
  updatedAtUtc: string
  updatedBy?: string | null
}

export interface SupplierPreorderOrderersOverview {
  orderers: SupplierPreorderOrderer[]
  storesWithoutOrderer: SupplierPreorderStoreRef[]
}

export interface SupplierPreorderSaveOrdererPayload {
  storeGuids: string[]
  canViewOtherStores: boolean
}

export interface SupplierPreorderUserCandidate {
  userGuid: string
  userName: string
  fullName?: string | null
  primaryStoreName?: string | null
  alreadyOrderer: boolean
}

export interface SupplierPreorderAuthLog {
  logGuid: string
  scope: 'Orderer' | 'FormStore'
  targetUserName?: string | null
  formGuid?: string | null
  action: string
  detail?: string | null
  operatorName?: string | null
  createdAtUtc: string
}

export interface SupplierPreorderAuthLogQuery {
  page?: number
  pageSize?: number
  userGuid?: string
  formGuid?: string
}

// ---------- 下单 ----------

export interface SupplierPreorderMyStore extends SupplierPreorderStoreRef {
  canEdit: boolean
  /** 不能编辑时的原因（中文） */
  readOnlyReason?: string | null
  orderStatus: SupplierPreorderStoreOrderStatus
  submittedAtUtc?: string | null
  lastEditedAtUtc?: string | null
  lastEditedBy?: string | null
  cartons: number
  amount: number
}

export interface SupplierPreorderOrderingForm {
  formGuid: string
  title: string
  supplierName: string
  holidayType: SupplierPreorderHolidayType
  seasonYear: number
  status: SupplierPreorderFormStatus
  quoteNo?: string | null
  offerDate?: string | null
  deadlineAtUtc?: string | null
  containerType?: string | null
  containerCapacityCbm?: number | null
  priceTerms?: string | null
  itemCount: number
  /** 对普通下单人是否已锁定 */
  isLocked: boolean
  myStores: SupplierPreorderMyStore[]
}

export interface SupplierPreorderOrderingContext {
  form: SupplierPreorderOrderingForm
  isManager: boolean
  canViewOtherStores: boolean
  categories: string[]
  serverNowUtc: string
}

export interface SupplierPreorderStoreCbm {
  storeName: string
  cbm: number
}

export interface SupplierPreorderOrderingSummary {
  storeGuid: string
  orderStatus: SupplierPreorderStoreOrderStatus
  itemCount: number
  cartons: number
  pieces: number
  amount: number
  cbm: number
  lastEditedAtUtc?: string | null
  lastEditedBy?: string | null
  submittedAtUtc?: string | null
  allStoresCbm: number
  /** 只有可查看其他分店的人才有，否则为 null */
  storeCbms?: SupplierPreorderStoreCbm[] | null
}

export type SupplierPreorderOrderingFilter = 'all' | 'mine' | 'others' | 'new' | 'matched'
export type SupplierPreorderOrderingSort = 'seq' | 'priceAsc' | 'priceDesc' | 'othersDesc'

export interface SupplierPreorderOrderingItemQuery {
  storeGuid: string
  page?: number
  pageSize?: number
  keyword?: string
  category?: string
  filter?: SupplierPreorderOrderingFilter
  sort?: SupplierPreorderOrderingSort
}

export interface SupplierPreorderOtherStoreCartons {
  storeName: string
  cartons: number
}

export interface SupplierPreorderOrderingItem {
  itemGuid: string
  seq: number
  supplierItemCode: string
  barcode?: string | null
  name: string
  unit?: string | null
  cartonQty: number
  cartonCbm?: number | null
  price: number
  category?: string | null
  imageUrl?: string | null
  imageSourceUrl?: string | null
  isNew: boolean
  myCartons: number
  /** 不能查看其他分店时为 null */
  otherStores?: SupplierPreorderOtherStoreCartons[] | null
}

export interface SupplierPreorderSaveLineResult {
  itemGuid: string
  cartons: number
  summary: SupplierPreorderOrderingSummary
}

/** 分页大小：50 / 100 / 200 / 500，默认 50 */
export const SUPPLIER_PREORDER_PAGE_SIZES = [50, 100, 200, 500] as const
