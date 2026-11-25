/** 批量货号查询独立契约；所有数量均来自同一日期和门店范围。 */
export interface BatchSalesScope {
  startDate: string
  endDate: string
  storeCodes: string[]
}

export interface BatchSalesMetrics {
  /** 净销量；等于正价、折扣和未知净数量之和。 */
  quantity: number
  regularQuantity: number
  discountQuantity: number
  unknownQuantity: number
  /** 正数退货量，仅作辅助信息，已在上述净数量扣除。 */
  returnQuantity: number
  salesAmount: number
  discountStatus: 'complete' | 'partial' | 'unknown' | 'pending'
  originalPriceMin: number | null
  originalPriceMax: number | null
  discountPriceMin: number | null
  discountPriceMax: number | null
}

export interface BatchSalesProduct {
  productCode: string
  itemNumber: string
  productName: string
  englishName?: string
  barcode?: string
  imageUrl?: string
}

export interface BatchSalesProductSummary extends BatchSalesProduct {
  /** 所有日期尚未发布时没有可靠的销量，不能用 0 代替。 */
  quantity: number | null
  salesAmount: number | null
}

export interface BatchSalesCoverage {
  status: 'complete' | 'partial' | 'pending'
  readyDates: string[]
  pendingDates: Array<{ date: string; reason: string }>
  /** 服务端为当前可读日期集合生成的不可变快照版本。 */
  version: string
}

export interface BatchSalesMatch {
  itemNumber: string
  status: 'matched' | 'ambiguous' | 'notFound'
  productCodes: string[]
}

export interface BatchSalesQuery extends BatchSalesScope {
  itemNumbers: string[]
}

export interface BatchSalesQueryResult extends BatchSalesScope {
  matches: BatchSalesMatch[]
  products: BatchSalesProductSummary[]
  warnings: string[]
  statisticStatus?: string
  statisticUpdatedAt?: string
  /** 折扣分类后台状态与销量统计状态分开，不能互相覆盖。 */
  discountStatisticStatus?: string
  discountUpdatedAt?: string
  coverage: BatchSalesCoverage
  overview: BatchSalesOverview
}

export interface BatchSalesDaily {
  date: string
  metrics: BatchSalesMetrics
}

export interface BatchSalesBranch {
  branchCode: string
  branchName: string
  metrics: BatchSalesMetrics
  daily: BatchSalesDaily[]
}

export interface BatchSalesOverviewBranch extends BatchSalesBranch {
  contributingProductCount: number
}

export interface BatchSalesOverview {
  /** 全 pending 时没有可靠聚合量。 */
  metrics: BatchSalesMetrics | null
  daily: BatchSalesDaily[]
  branches: BatchSalesOverviewBranch[]
}

export interface BatchSalesLockedScope extends BatchSalesScope {
  productCodes: string[]
  coverageVersion: string
  readyDates: string[]
}

export interface BatchSalesBranchOverviewRequest extends BatchSalesLockedScope {
  branchCode: string
}

export interface BatchSalesBranchOverview extends BatchSalesScope {
  productCodes: string[]
  coverage: BatchSalesCoverage
  branch: BatchSalesBranch
  products: Array<BatchSalesProduct & { metrics: BatchSalesMetrics }>
}

export interface BatchSalesDiscountOverview extends BatchSalesScope {
  productCodes: string[]
  coverage: BatchSalesCoverage
  overview: BatchSalesOverview
  branch?: BatchSalesBranch
  products?: Array<BatchSalesProduct & { metrics: BatchSalesMetrics }>
  discountStatisticStatus?: string
  discountUpdatedAt?: string
  warnings: string[]
}

export interface BatchSalesDetailRequest extends BatchSalesScope {
  productCode: string
  /** 单品钻取先读取可靠销量；折扣分类由独立锁定请求异步补齐。 */
  includeDiscounts?: boolean
  coverageVersion?: string
  readyDates?: string[]
}

export interface BatchSalesDetail extends BatchSalesScope {
  productCodes: string[]
  statisticStatus?: string
  statisticUpdatedAt?: string
  discountStatisticStatus?: string
  discountUpdatedAt?: string
  product: BatchSalesProduct
  metrics: BatchSalesMetrics
  daily: BatchSalesDaily[]
  branches: BatchSalesBranch[]
  warnings: string[]
  coverage: BatchSalesCoverage
}

/** 进货量查询：startDate/endDate 是进货日期区间，与销量日期区间相互独立。 */
export interface BatchSalesPurchaseRequest extends BatchSalesScope {
  productCodes: string[]
}

/** 商品×分店进货量；总仓配货与供应商进货单分开返回，页面按合计展示。 */
export interface BatchSalesPurchaseRow {
  productCode: string
  branchCode: string
  warehouseQuantity: number
  supplierQuantity: number
}

export interface BatchSalesPurchaseResult extends BatchSalesScope {
  productCodes: string[]
  /** 只含非零组合；未出现的商品或分店即进货量为 0。 */
  rows: BatchSalesPurchaseRow[]
}

/** 进货逐笔明细：点开某个进货量时按需请求，日期与门店口径与进货量完全一致。branchCode 为空表示全部分店。 */
export interface BatchSalesPurchaseDetailRequest extends BatchSalesScope {
  productCodes: string[]
  branchCode?: string
}

/** Warehouse = 仓库分店订单发货（配货数量）；Supplier = 分店供应商进货单。 */
export type BatchSalesPurchaseSource = 'Warehouse' | 'Supplier'

/** 每张单据×商品一行。purchaseDate 为计入进货量的日期：仓库单按出库日期，未出库（isShipped = false）按订单日期。 */
export interface BatchSalesPurchaseDetailRow {
  source: BatchSalesPurchaseSource
  documentNo: string
  purchaseDate: string
  isShipped: boolean
  productCode: string
  branchCode: string
  quantity: number
}

export interface BatchSalesPurchaseDetailResult extends BatchSalesScope {
  productCodes: string[]
  branchCode?: string
  /** 按日期倒序，最多 limit 行；truncated 表示还有更早的记录未返回。 */
  rows: BatchSalesPurchaseDetailRow[]
  limit: number
  truncated: boolean
}

export interface BatchSalesOptions {
  stores: { code: string; name: string }[]
  maxItemNumbers: number
  maxDays: number
}

export interface BatchProductSalesApi {
  getOptions(signal?: AbortSignal): Promise<BatchSalesOptions>
  query(input: BatchSalesQuery, signal?: AbortSignal): Promise<BatchSalesQueryResult>
  getDetail(input: BatchSalesDetailRequest, signal?: AbortSignal): Promise<BatchSalesDetail>
  getBranchOverview(input: BatchSalesBranchOverviewRequest, signal?: AbortSignal): Promise<BatchSalesBranchOverview>
  getDiscounts(input: BatchSalesLockedScope & { branchCode?: string }, signal?: AbortSignal): Promise<BatchSalesDiscountOverview>
  exportDetail(input: BatchSalesLockedScope, signal?: AbortSignal): Promise<string>
  getPurchases(input: BatchSalesPurchaseRequest, signal?: AbortSignal): Promise<BatchSalesPurchaseResult>
  getPurchaseDetails(input: BatchSalesPurchaseDetailRequest, signal?: AbortSignal): Promise<BatchSalesPurchaseDetailResult>
}
