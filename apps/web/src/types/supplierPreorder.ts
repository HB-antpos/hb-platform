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
  /** 导入校对页人工指定的参考商品（按原表行号）；productCode 为 null 表示「不要参考」 */
  refOverrides?: SupplierPreorderRefOverride[] | null
}

export interface SupplierPreorderRefOverride {
  rowNo: number
  productCode: string | null
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
  /** 去年历史快照的计算状态与窗口 */
  history: SupplierPreorderHistoryStatus
  /** 本品自己在窗口内有销量的款数 */
  ownHistoryCount: number
  refAutoCount: number
  refManualCount: number
  /** 本品没有销量、也没有参考商品的款数 */
  needRefCount: number
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
  match?: 'all' | 'matched' | 'new' | 'warning' | 'needRef' | 'ref'
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
  /** 本品自己在历史窗口内（全部分店）的销量；快照未算出时为 null */
  ownSalesQty?: number | null
  ref?: SupplierPreorderItemRef | null
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
  /** 去年历史快照状态、窗口与节日关键日期 */
  history: SupplierPreorderHistoryStatus
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

/** lastYearSold / lastYearDiscounted 只看本品自己的去年本店数据，不含参考商品 */
export type SupplierPreorderOrderingFilter = 'all' | 'mine' | 'others' | 'new' | 'matched' | 'lastYearSold' | 'lastYearDiscounted'
/** lastYearSalesDesc：去年本店销量从高到低（含参考商品） */
export type SupplierPreorderOrderingSort = 'seq' | 'priceAsc' | 'priceDesc' | 'othersDesc' | 'lastYearSalesDesc'

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
  /** 去年本店数据（本品或参考商品）；null = 无去年记录（或快照还没算出来） */
  history?: SupplierPreorderItemHistory | null
}

export interface SupplierPreorderSaveLineResult {
  itemGuid: string
  cartons: number
  summary: SupplierPreorderOrderingSummary
}

/** 分页大小：50 / 100 / 200 / 500，默认 50 */
export const SUPPLIER_PREORDER_PAGE_SIZES = [50, 100, 200, 500] as const

// ---------- 去年历史与参考商品（PR 2） ----------

export type SupplierPreorderKeyDateKey = 'Halloween' | 'BlackFriday' | 'ChristmasEve' | 'Christmas' | 'BoxingDay' | 'Easter'

export interface SupplierPreorderKeyDate {
  /** yyyy-MM-ddT00:00:00（按日期看，不做时区换算） */
  date: string
  key: SupplierPreorderKeyDateKey
}

export interface SupplierPreorderDateRange {
  from: string
  to: string
}

/** None 还没排队 / Queued 排队中 / Running 计算中 / Ready 已完成 / Failed 失败 / NoWindow 没有历史窗口 */
export type SupplierPreorderHistoryState = 'None' | 'Queued' | 'Running' | 'Ready' | 'Failed' | 'NoWindow'

export interface SupplierPreorderHistoryStatus {
  status: SupplierPreorderHistoryState
  computedAtUtc?: string | null
  requestedAtUtc?: string | null
  error?: string | null
  windowFrom?: string | null
  windowTo?: string | null
  keyDates: SupplierPreorderKeyDate[]
}

/** Auto 系统推荐 / Manual 人工指定 / Cleared 人工清除（productCode 为空） */
export type SupplierPreorderRefSource = 'Auto' | 'Manual' | 'Cleared'

export interface SupplierPreorderItemRef {
  source: SupplierPreorderRefSource
  productCode?: string | null
  itemNumber?: string | null
  barcode?: string | null
  name?: string | null
  purchasePrice?: number | null
  imageUrl?: string | null
  score?: number | null
  setBy?: string | null
  setAtUtc?: string | null
  /** 参考商品在历史窗口内（全部分店）的销量 */
  salesQty?: number | null
}

export type SupplierPreorderDiscountStatus = 'complete' | 'partial' | 'unknown' | 'none'

/** 稀疏日销：[距窗口起点天数, 当天净销量, 当天折扣销量] */
export type SupplierPreorderDailyPoint = [number, number, number]

/**
 * 下单列表一行商品的去年本店数据。source = Own 本品 / Ref 借用参考商品（整格紫色、标「参考 XXX」）。
 * hasStoreData = false：这个商品去年在别的分店有记录、本店没有。
 */
export interface SupplierPreorderItemHistory {
  source: 'Own' | 'Ref'
  productCode: string
  ref?: SupplierPreorderItemRef | null
  hasStoreData: boolean
  purchaseFirst?: string | null
  purchaseLast?: string | null
  purchaseBatches: number
  purchaseQty: number
  salesFirst?: string | null
  salesLast?: string | null
  salesQty: number
  sellThrough?: number | null
  discountStatus: SupplierPreorderDiscountStatus
  hasDiscount: boolean
  discountShare?: number | null
  discountRanges: SupplierPreorderDateRange[]
  daily: SupplierPreorderDailyPoint[]
}

export interface SupplierPreorderHistoryBatch {
  date: string
  /** warehouse 总仓配货 / supplier 供应商直送 */
  source: 'warehouse' | 'supplier'
  quantity: number
  documentNo?: string | null
}

export interface SupplierPreorderStoreHistory {
  storeCode: string
  storeName: string
  isCurrent: boolean
  hasData: boolean
  purchaseFirst?: string | null
  purchaseLast?: string | null
  purchaseBatches: number
  purchaseQty: number
  warehouseQty: number
  supplierQty: number
  salesFirst?: string | null
  salesLast?: string | null
  salesQty: number
  sellThrough?: number | null
  discountStatus: SupplierPreorderDiscountStatus
  hasDiscount: boolean
  discountShare?: number | null
  retailPrice?: number | null
  discountPriceMin?: number | null
  discountRanges: SupplierPreorderDateRange[]
  /** 只有本店（展开大图）有 */
  daily: SupplierPreorderDailyPoint[]
  /** 只有本店（展开大图）有 */
  batches: SupplierPreorderHistoryBatch[]
  /** 今年这家店已订箱数（只算开启分店） */
  thisYearCartons?: number | null
}

/** 展开行：本店大图 + 小结 + 各分店去年对比（不能看其他分店时 stores 为 null） */
export interface SupplierPreorderItemHistoryDetail {
  itemGuid: string
  source: 'Own' | 'Ref' | 'None'
  productCode?: string | null
  ref?: SupplierPreorderItemRef | null
  windowFrom?: string | null
  windowTo?: string | null
  keyDates: SupplierPreorderKeyDate[]
  store?: SupplierPreorderStoreHistory | null
  /** 去年本店销量 ÷ 箱规（只作参考） */
  suggestedCartons?: number | null
  stores?: SupplierPreorderStoreHistory[] | null
}

export interface SupplierPreorderRefCandidate {
  productCode: string
  itemNumber?: string | null
  barcode?: string | null
  name: string
  purchasePrice?: number | null
  imageUrl?: string | null
  localSupplierCode?: string | null
  salesQty: number
  purchaseQty: number
  score: number
  nameScore: number
  priceScore: number
  /** 是否过了两个硬门槛（名称 ≥ 0.5、进价差 < 30%） */
  eligible: boolean
  /** 窗口内没有销量也没有进货：选它也看不到数据 */
  hasHistory: boolean
}

export interface SupplierPreorderRefCandidates {
  windowFrom?: string | null
  windowTo?: string | null
  /** 同供应商、窗口内有记录、过了门槛的前 3 个（没有关键词时） */
  recommended: SupplierPreorderRefCandidate[]
  /** 按货号 / 条码 / 名称搜索的结果（不限供应商） */
  searchResults: SupplierPreorderRefCandidate[]
}

/** Manual 指定（productCode 必填）/ Clear 清除且不再自动带上 / Auto 交回系统推荐 */
export interface SupplierPreorderSetRefPayload {
  mode: 'Manual' | 'Clear' | 'Auto'
  productCode?: string | null
}

export interface SupplierPreorderImportRefCandidatesPayload {
  localSupplierCode: string
  holidayType: SupplierPreorderHolidayType
  seasonYear: number
  historyFrom?: string | null
  historyTo?: string | null
  name: string
  price: number
  keyword?: string | null
  excludeProductCodes?: string[] | null
}
