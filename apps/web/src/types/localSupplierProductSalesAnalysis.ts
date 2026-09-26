export interface LocalSupplierProductSalesAnalysisFilter {
  startDate: string
  endDate: string
  keyword?: string
  categoryGuid?: string
  supplierCode?: string
  warehouseCategoryGuids?: string[]
  supplierCodes?: string[]
  supplierCategoryGuids?: string[]
  documentKeyword?: string
}

export type LocalSupplierProductSalesAnalysisSelectionMode = 'allFiltered' | 'included'

export interface LocalSupplierProductSalesAnalysisSelection {
  mode: LocalSupplierProductSalesAnalysisSelectionMode
  includedProductCodes: string[]
  excludedProductCodes: string[]
}

export interface LocalSupplierProductSalesAnalysisRequest {
  filter: LocalSupplierProductSalesAnalysisFilter
  /** 可选：跨分页商品选择；bootstrap 首屏/查询省略时由服务端 autoSelectFirst 计算。 */
  selection?: LocalSupplierProductSalesAnalysisSelection
  currentProductCode?: string
  branchCode?: string
  /** bootstrap 专用：为 true 时服务端在无选择情况下显式选中首项候选。 */
  autoSelectFirst?: boolean
  /** bootstrap 专用：候选分页与汇总分页分别传参。 */
  candidatePageNumber?: number
  candidatePageSize?: number
  summaryPageNumber?: number
  summaryPageSize?: number
  pageNumber?: number
  pageSize?: number
  forceRefresh?: boolean
}

export interface LocalSupplierProductSalesAnalysisCategoryOption { guid: string; name?: string }
export interface LocalSupplierProductSalesAnalysisSupplierOption { code: string; name?: string }
export interface LocalSupplierProductSalesAnalysisSupplierCategoryOption {
  supplierCode: string
  guid: string
  parentGuid?: string
  name: string
  isSelectable: boolean
  children: LocalSupplierProductSalesAnalysisSupplierCategoryOption[]
}
export interface LocalSupplierProductSalesAnalysisOptions {
  warehouseCategories: LocalSupplierProductSalesAnalysisCategoryOption[]
  suppliers: LocalSupplierProductSalesAnalysisSupplierOption[]
}

export interface LocalSupplierProductSalesAnalysisSupplierRef { code: string; name?: string }

export interface LocalSupplierProductSalesAnalysisCandidate {
  productCode: string
  itemNumber?: string
  barcode?: string
  productName?: string
  imageUrl?: string
  warehouseCategoryGuid?: string
  warehouseCategoryName?: string
}

export interface LocalSupplierProductSalesAnalysisTotals {
  purchaseQuantity: number
  purchaseAmount: number
  netSalesQuantity: number
  netSalesAmount: number
  sellThroughRate: number | null
}

export interface LocalSupplierProductSalesAnalysisSummaryRow extends LocalSupplierProductSalesAnalysisCandidate, LocalSupplierProductSalesAnalysisTotals {
  suppliers: LocalSupplierProductSalesAnalysisSupplierRef[]
}

export interface LocalSupplierProductSalesAnalysisPaged<T> {
  items: T[]
  total: number
  pageNumber: number
  pageSize: number
}

export interface LocalSupplierProductSalesAnalysisSummary extends LocalSupplierProductSalesAnalysisPaged<LocalSupplierProductSalesAnalysisSummaryRow> {
  totals: LocalSupplierProductSalesAnalysisTotals
}

export interface LocalSupplierProductSalesAnalysisDaily {
  date: string
  purchaseQuantity: number
  purchaseAmount: number
  netSalesQuantity: number
  netSalesAmount: number
  averageUnitPrice: number | null
}

export interface LocalSupplierProductSalesAnalysisInvoiceDetail {
  detailGuid: string
  invoiceGuid?: string
  invoiceNo?: string
  storeCode?: string
  storeName?: string
  supplierCode?: string
  supplierName?: string
  purchaseDate?: string
  productCode?: string
  productName?: string
  quantity: number
  purchasePrice: number | null
  amount: number
}

export interface LocalSupplierProductSalesAnalysisBranch {
  branchCode: string
  branchName?: string
  netSalesQuantity: number
  netSalesAmount: number
  averageUnitPrice: number | null
}

/** 分段错误键：options/summary/invoiceDetails/productDaily/branches。 */
export interface LocalSupplierProductSalesAnalysisSectionErrors {
  options?: string
  summary?: string
  invoiceDetails?: string
  productDaily?: string
  branches?: string
}

/**
 * 统一 bootstrap 响应：一次请求带回页面全部数据分段。
 * 候选或关键错误时整体失败（success=false）；分段失败时 success=true 且 partial=true + sectionErrors。
 */
export interface LocalSupplierProductSalesAnalysisBootstrap {
  options: LocalSupplierProductSalesAnalysisOptions
  candidates: LocalSupplierProductSalesAnalysisPaged<LocalSupplierProductSalesAnalysisCandidate>
  effectiveSelection?: LocalSupplierProductSalesAnalysisSelection
  currentProduct?: LocalSupplierProductSalesAnalysisCandidate | null
  summary?: LocalSupplierProductSalesAnalysisSummary | null
  invoiceDetails?: LocalSupplierProductSalesAnalysisPaged<LocalSupplierProductSalesAnalysisInvoiceDetail> | null
  productDaily?: LocalSupplierProductSalesAnalysisDaily[] | null
  branches?: LocalSupplierProductSalesAnalysisBranch[] | null
  partial?: boolean
  sectionErrors?: LocalSupplierProductSalesAnalysisSectionErrors
}

export interface LocalSupplierProductSalesAnalysisEnvelope<T> { data: T }
