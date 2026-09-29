export interface BestSellerBranchSale {
  branchCode: string
  branchName?: string
  quantity: number
  salesAmount: number
  totalCost?: number
  grossProfit?: number
  grossMarginRate?: number
  costSource?: string
}

export interface BestSellerProduct {
  productCode: string
  itemNumber?: string
  barcode?: string
  productImage?: string
  productName?: string
  quantity: number
  salesAmount: number
  totalCost?: number
  grossProfit?: number
  grossMarginRate?: number
  costSource?: string
  rank: number
  // 是否上架，前端用它控制状态展示和加购按钮禁用态。
  isActive?: boolean
  // 最小起订量，用于热销商品快捷加购默认数量。
  minOrderQuantity?: number
  // 销售过该商品的分店数，优先使用后端聚合值而不是前端现算长度。
  branchSalesCount?: number
  // 分店销量明细，用于 Stores Sold 弹层展示。
  branchSales?: BestSellerBranchSale[]
  // 商品统计状态，用于提示数据是否完整。
  statisticStatus?: string
}

export interface BestSellerResponse {
  products: BestSellerProduct[]
  total: number
  pageIndex: number
  pageSize: number
  totalPages: number
  statisticStatus?: string
  statisticMessage?: string
}

export type CompareMode = 'ByWeek' | 'ByDate'

export interface DateRange {
  startDate: string
  endDate: string
  compareStartDate?: string
  compareEndDate?: string
  compareMode?: CompareMode
}

export interface SupplierSalesRank {
  startDate: string
  endDate: string
  supplierCode: string
  supplierName: string
  totalAmount: number
  totalQuantity: number
  storeCount: number
  compareTotalAmount?: number
  totalAmountGrowth?: number
}

export interface ChinaSupplierSalesRank {
  startDate: string
  endDate: string
  supplierCode: string
  supplierName: string
  totalAmount: number
  totalQuantity: number
  storeCount: number
  compareTotalAmount?: number
  totalAmountGrowth?: number
}

export interface SalesProductDetailWithDiscount {
  productCode: string
  itemNumber?: string
  productImage?: string
  productName?: string
  quantity: number
  discountedQuantity: number
  salesAmount: number
  averageUnitPrice: number
  averageOriginalPrice?: number
  orderCount: number
  quantityLY: number
  discountedQuantityLY: number
  salesAmountLY: number
  averageUnitPriceLY: number
  averageOriginalPriceLY?: number
  orderCountLY: number
}

export interface PagedSalesProductDetailWithDiscount {
  data: SalesProductDetailWithDiscount[]
  total: number
  pageIndex: number
  pageSize: number
}

export interface BranchSalesAggregate {
  branchCode: string
  branchName: string
  totalRevenue: number
  totalRevenueLY: number
  totalQuantity: number
  totalQuantityLY: number
  orderCount: number
  orderCountLY: number
  hbRevenue: number
  hbRevenueLY: number
}

export interface CompactSalesBoardStore {
  branchCode: string
  branchName: string
  totalAmount: number
  totalQuantity: number
  domesticSupplierAmount: number
  /** 该分店在区间内的总营业额（营业额日报同一口径，含全部供应商的商品），分店栏占比的分母；无统计时为 0 */
  branchTotalAmount: number
  australianSupplierCode: string
  australianSupplierName: string
  /** 当前筛选下该分店有销售的商品款数 */
  productCount: number
}

export interface CompactSalesBoardChinaSupplier {
  supplierCode: string
  supplierName: string
  totalAmount: number
  totalQuantity: number
  /** 当前筛选下该供应商有销售的商品款数 */
  productCount: number
}

export interface CompactSalesBoardProduct {
  productCode: string
  itemNumber?: string
  productImage?: string
  productName?: string
  chinaSupplierCode?: string
  chinaSupplierName?: string
  totalQuantity: number
  unitPrice: number
  totalAmount: number
}

export interface PagedCompactSalesBoardProduct {
  data: CompactSalesBoardProduct[]
  total: number
  pageIndex: number
  pageSize: number
  /** 商品栏在分店、供应商约束下（关键词过滤前）的营业额合计，商品占比的分母 */
  scopeAmount: number
}

/** KPI 汇总：total* 同时受三个维度选中项约束，overall* 只受授权分店范围约束。 */
export interface CompactSalesBoardSummary {
  totalAmount: number
  totalQuantity: number
  productCount: number
  storeCount: number
  supplierCount: number
  overallAmount: number
  overallQuantity: number
}

export interface CompactSalesBoard {
  stores: CompactSalesBoardStore[]
  chinaSuppliers: CompactSalesBoardChinaSupplier[]
  productDetails: PagedCompactSalesBoardProduct
  summary: CompactSalesBoardSummary
  statisticStatus?: string
  statisticMessage?: string
  statisticUpdatedAt?: string
  /** 服务端是否复用了已缓存的「门店×商品」聚合 */
  fromCache: boolean
}

export type CompactSalesBoardSortField = 'amount' | 'quantity' | 'unitPrice' | 'itemNumber'
export type CompactSalesBoardSortOrder = 'asc' | 'desc'

/**
 * branchCodes 是授权分店范围（undefined 表示全部），selected* 是页面联动选中项；
 * 每栏只受其他栏的选中项约束，由服务端统一计算。
 */
export interface CompactSalesBoardRequest {
  dateRange: DateRange
  branchCodes?: string[]
  selectedBranchCode?: string | null
  selectedChinaSupplierCode?: string | null
  selectedProductCode?: string | null
  keyword?: string
  sortField?: CompactSalesBoardSortField
  sortOrder?: CompactSalesBoardSortOrder
  pageIndex?: number
  pageSize?: number
  forceRefresh?: boolean
}

export interface WeeklyHierarchyData {
  key: string
  level: 'week' | 'branch' | 'date'
  hierarchy: string
  revenue: number
  revenueLY: number
  orders: number
  ordersLY: number
  aov: number
  aovLY: number
  yoyChange?: number
  children?: WeeklyHierarchyData[]
}

export interface ExecutiveBranchPerformance {
  rank: number
  branchCode: string
  branchName: string
  revenue: number
  revenueLY: number
  orderCount: number
  orderCountLY: number
  aov: number
  aovLY: number
}

export interface ExecutiveHourlyTraffic {
  hour: string
  revenue: number
  revenueLY: number
  percentage: number
  isPeak: boolean
  branchCode?: string
  branchName?: string
}
