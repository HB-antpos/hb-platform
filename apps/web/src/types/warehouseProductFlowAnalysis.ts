export type WarehouseProductFlowSelectionMode = 'allFiltered' | 'included'

/** 批量货号匹配方式：仅货号，或货号 / 条码 / 商品编码任一。 */
export type WarehouseProductFlowItemNumberMatchMode = 'itemNumber' | 'any'

export interface WarehouseProductFlowFilter {
  keyword?: string
  warehouseCategoryGuids: string[]
  supplierCodes: string[]
  documentKeyword?: string
  /** 从 Excel 粘贴的批量货号，按粘贴顺序；后端精确匹配。 */
  itemNumbers?: string[]
  itemNumberMatchMode?: WarehouseProductFlowItemNumberMatchMode
}

export interface WarehouseProductFlowItemNumberMatch {
  requestedCount: number
  matchedCount: number
  unmatchedItemNumbers: string[]
}

export interface WarehouseProductFlowPeriod {
  startDate: string
  endDate: string
}

export interface WarehouseProductFlowPeriods {
  containerPeriod: WarehouseProductFlowPeriod
  orderShipmentPeriod: WarehouseProductFlowPeriod
  salesPeriod: WarehouseProductFlowPeriod
}

export interface WarehouseProductFlowSelection {
  mode: WarehouseProductFlowSelectionMode
  includedProductCodes: string[]
  excludedProductCodes: string[]
}

export interface WarehouseProductFlowMetrics {
  inboundQuantity: number
  orderedQuantity?: number
  shippedQuantity: number
  netSalesQuantity: number
  netSalesAmount: number
  averageUnitPrice: number | null
}

export interface WarehouseProductFlowCandidate {
  productCode: string
  itemNumber?: string
  productName?: string
  englishName?: string
  barcode?: string
  imageUrl?: string
  supplierCode?: string
  supplierName?: string
  categoryName?: string
}

export interface WarehouseProductFlowProduct extends WarehouseProductFlowCandidate {
  metrics: WarehouseProductFlowMetrics
}

export interface WarehouseProductFlowSummaryData {
  totals: WarehouseProductFlowMetrics
  currentProduct: WarehouseProductFlowProduct | null
  items: WarehouseProductFlowProduct[]
  total: number
  pageNumber: number
  pageSize: number
}

export interface WarehouseProductFlowCandidatesData {
  items: WarehouseProductFlowCandidate[]
  total: number
  pageNumber: number
  pageSize: number
  /** 只在筛选带批量货号时返回。 */
  itemNumberMatch?: WarehouseProductFlowItemNumberMatch
}

export interface WarehouseProductFlowSupplierOption {
  code: string
  name?: string
}

export interface WarehouseProductFlowOptions {
  domesticSuppliers: WarehouseProductFlowSupplierOption[]
}

export interface WarehouseProductFlowContainerRow {
  containerNumber: string
  arrivalDate?: string
  inboundQuantity: number
  inboundUnitPrice: number | null
  supplierName?: string
}

export interface WarehouseProductFlowOrderRow {
  orderNumber: string
  branchName?: string
  orderDate?: string
  orderedQuantity: number
}

export interface WarehouseProductFlowShipmentRow {
  shipmentNumber?: string
  orderNumber?: string
  branchCode?: string
  branchName?: string
  posEnabled: boolean | null
  shipmentDate?: string
  shippedQuantity: number
  /** 对应分店在 salesPeriod 内的净销量，不按单张发货单分摊。 */
  netSalesQuantity: number | null
}

export interface WarehouseProductFlowDaily {
  date: string
  inboundQuantity: number
  /** 仓库流转接口始终返回；本地商品共享图表数据可省略。 */
  orderedQuantity?: number
  shippedQuantity: number
  netSalesQuantity: number
  netSalesAmount: number
  averageUnitPrice: number | null
}

export interface WarehouseProductFlowBranch {
  branchCode: string
  branchName?: string
  orderedQuantity: number
  shippedQuantity: number
  netSalesQuantity: number
  netSalesAmount: number
  sellThroughRate: number | null
  averageUnitPrice: number | null
}

export interface WarehouseProductFlowSummaryRequest {
  filter: WarehouseProductFlowFilter
  periods: WarehouseProductFlowPeriods
  selection: WarehouseProductFlowSelection
  currentProductCode?: string
  pageNumber: number
  pageSize: number
  sortBy?: string
  sortDirection?: 'asc' | 'desc'
  forceRefresh?: boolean
}

export interface WarehouseProductFlowCandidateRequest {
  filter: WarehouseProductFlowFilter
  pageNumber: number
  pageSize: number
  sortBy?: string
  sortDirection?: 'asc' | 'desc'
  forceRefresh?: boolean
}

export interface WarehouseProductFlowProductRequest {
  filter: WarehouseProductFlowFilter
  periods: WarehouseProductFlowPeriods
  currentProductCode: string
  forceRefresh?: boolean
}

export interface WarehouseProductFlowDetailRequest extends WarehouseProductFlowProductRequest {
  detailType?: never
}

export interface WarehouseProductFlowBranchRequest extends WarehouseProductFlowProductRequest {
  branchCode?: string
}

export interface WarehouseProductFlowEnvelope<T> {
  data: T
}
