import type { ApiResponse } from '../types/api'
import type {
  BestSellerBranchSale,
  BestSellerProduct,
  BestSellerResponse,
  BranchSalesAggregate,
  ChinaSupplierSalesRank,
  CompactSalesBoard,
  CompactSalesBoardChinaSupplier,
  CompactSalesBoardProduct,
  CompactSalesBoardRequest,
  CompactSalesBoardStore,
  CompactSalesBoardSummary,
  DateRange,
  ExecutiveBranchPerformance,
  ExecutiveHourlyTraffic,
  PagedSalesProductDetailWithDiscount,
  SupplierSalesRank,
  WeeklyHierarchyData,
} from '../types/salesDashboard'
import request from '../utils/request'

export type {
  BestSellerBranchSale,
  BestSellerProduct,
  BranchSalesAggregate,
  ChinaSupplierSalesRank,
  CompactSalesBoard,
  CompactSalesBoardChinaSupplier,
  CompactSalesBoardProduct,
  CompactSalesBoardRequest,
  CompactSalesBoardSortField,
  CompactSalesBoardSortOrder,
  CompactSalesBoardStore,
  CompactSalesBoardSummary,
  DateRange,
  ExecutiveBranchPerformance,
  ExecutiveHourlyTraffic,
  PagedSalesProductDetailWithDiscount,
  SupplierSalesRank,
  WeeklyHierarchyData,
} from '../types/salesDashboard'

function readNumber(value: unknown, fallback = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function readOptionalNumber(value: unknown) {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function readString(value: unknown) {
  return typeof value === 'string' && value.trim() ? value : undefined
}

function normalizeBestSellerBranchSale(raw: unknown): BestSellerBranchSale | null {
  if (!raw || typeof raw !== 'object') {
    return null
  }

  const record = raw as Record<string, unknown>
  const branchCode = readString(record.branchCode ?? record.BranchCode)

  if (!branchCode) {
    return null
  }

  return {
    branchCode,
    branchName: readString(record.branchName ?? record.BranchName),
    quantity: readNumber(record.quantity ?? record.Quantity),
    salesAmount: readNumber(record.salesAmount ?? record.SalesAmount),
    totalCost: readOptionalNumber(record.totalCost ?? record.TotalCost),
    grossProfit: readOptionalNumber(record.grossProfit ?? record.GrossProfit),
    grossMarginRate: readOptionalNumber(record.grossMarginRate ?? record.GrossMarginRate),
    costSource: readString(record.costSource ?? record.CostSource),
  }
}

function normalizeBestSellerProduct(raw: unknown): BestSellerProduct | null {
  if (!raw || typeof raw !== 'object') {
    return null
  }

  const record = raw as Record<string, unknown>
  const productCode = readString(record.productCode ?? record.ProductCode)

  if (!productCode) {
    return null
  }

  const branchSales = Array.isArray(record.branchSales ?? record.BranchSales)
    ? ((record.branchSales ?? record.BranchSales) as unknown[])
        .map(normalizeBestSellerBranchSale)
        .filter((item): item is BestSellerBranchSale => item !== null)
    : undefined

  return {
    productCode,
    itemNumber: readString(record.itemNumber ?? record.ItemNumber),
    barcode: readString(record.barcode ?? record.Barcode),
    productImage: readString(record.productImage ?? record.ProductImage),
    productName: readString(record.productName ?? record.ProductName),
    quantity: readNumber(record.quantity ?? record.Quantity),
    salesAmount: readNumber(record.salesAmount ?? record.SalesAmount),
    totalCost: readOptionalNumber(record.totalCost ?? record.TotalCost),
    grossProfit: readOptionalNumber(record.grossProfit ?? record.GrossProfit),
    grossMarginRate: readOptionalNumber(record.grossMarginRate ?? record.GrossMarginRate),
    costSource: readString(record.costSource ?? record.CostSource),
    rank: readNumber(record.rank ?? record.Rank),
    isActive: typeof (record.isActive ?? record.IsActive) === 'boolean'
      ? (record.isActive ?? record.IsActive) as boolean
      : undefined,
    minOrderQuantity: readOptionalNumber(record.minOrderQuantity ?? record.MinOrderQuantity),
    branchSalesCount: readOptionalNumber(record.branchSalesCount ?? record.BranchSalesCount),
    branchSales,
    statisticStatus: readString(record.statisticStatus ?? record.StatisticStatus),
  }
}

function unwrapBestSellerResponse(payload: ApiResponse<BestSellerResponse> | BestSellerResponse): BestSellerResponse {
  let current: unknown = payload

  for (let depth = 0; depth < 3; depth += 1) {
    if (!current || typeof current !== 'object' || !('data' in current)) {
      break
    }

    const record = current as {
      data?: unknown
      products?: unknown
      total?: unknown
      pageIndex?: unknown
      success?: boolean
      isSuccess?: boolean
      message?: string
    }
    const looksLikeResult =
      Array.isArray(record.products) || 'total' in record || 'pageIndex' in record

    if (looksLikeResult) {
      break
    }

    current = record.data
  }

  const result = (current ?? {}) as Partial<BestSellerResponse>
  const products = Array.isArray(result.products ?? (result as Record<string, unknown>).Products)
    ? ((result.products ?? (result as Record<string, unknown>).Products) as unknown[])
        .map(normalizeBestSellerProduct)
        .filter((item): item is BestSellerProduct => item !== null)
    : []

  return {
    products,
    total: readNumber(result.total ?? (result as Record<string, unknown>).Total),
    pageIndex: readNumber(result.pageIndex ?? (result as Record<string, unknown>).PageIndex, 1),
    pageSize: readNumber(result.pageSize ?? (result as Record<string, unknown>).PageSize),
    totalPages: readNumber(result.totalPages ?? (result as Record<string, unknown>).TotalPages),
    statisticStatus: readString(result.statisticStatus ?? (result as Record<string, unknown>).StatisticStatus),
    statisticMessage: readString(result.statisticMessage ?? (result as Record<string, unknown>).StatisticMessage),
  }
}

export async function getBestSellers(
  startDate: string,
  endDate: string,
  branchCodes?: string[],
  pageIndex = 1,
  pageSize = 8,
  signal?: AbortSignal,
): Promise<BestSellerResponse> {
  const response = await request<ApiResponse<BestSellerResponse> | BestSellerResponse>(
    '/api/react/v1/dashboard/best-sellers',
    {
      method: 'GET',
      signal,
      params: {
        startDate,
        endDate,
        branchCodes,
        pageIndex,
        pageSize,
      },
    },
  )

  return unwrapBestSellerResponse(response)
}

function unwrapApiResponse<T>(payload: ApiResponse<T> | T): ApiResponse<T> {
  if (payload && typeof payload === 'object' && ('success' in payload || 'isSuccess' in payload || 'data' in payload)) {
    return payload as ApiResponse<T>
  }
  return { success: true, data: payload as T }
}

function unwrapDataPayload<T>(payload: ApiResponse<T> | T): T {
  let current: unknown = payload

  for (let depth = 0; depth < 3; depth += 1) {
    if (!current || typeof current !== 'object' || !('data' in current)) {
      break
    }
    current = (current as { data?: unknown }).data
  }

  return current as T
}

function normalizeCompactStore(raw: unknown): CompactSalesBoardStore | null {
  if (!raw || typeof raw !== 'object') return null
  const record = raw as Record<string, unknown>
  const branchCode = readString(record.branchCode ?? record.BranchCode)
  if (!branchCode) return null

  return {
    branchCode,
    branchName: readString(record.branchName ?? record.BranchName) ?? branchCode,
    totalAmount: readNumber(record.totalAmount ?? record.TotalAmount),
    totalQuantity: readNumber(record.totalQuantity ?? record.TotalQuantity),
    domesticSupplierAmount: readNumber(record.domesticSupplierAmount ?? record.DomesticSupplierAmount),
    branchTotalAmount: readNumber(record.branchTotalAmount ?? record.BranchTotalAmount),
    australianSupplierCode: readString(record.australianSupplierCode ?? record.AustralianSupplierCode) ?? '200',
    australianSupplierName: readString(record.australianSupplierName ?? record.AustralianSupplierName) ?? '200-hotbargain',
    productCount: readNumber(record.productCount ?? record.ProductCount),
  }
}

function normalizeCompactChinaSupplier(raw: unknown): CompactSalesBoardChinaSupplier | null {
  if (!raw || typeof raw !== 'object') return null
  const record = raw as Record<string, unknown>
  const supplierCode = readString(record.supplierCode ?? record.SupplierCode)
  if (!supplierCode) return null

  return {
    supplierCode,
    supplierName: readString(record.supplierName ?? record.SupplierName) ?? supplierCode,
    totalAmount: readNumber(record.totalAmount ?? record.TotalAmount),
    totalQuantity: readNumber(record.totalQuantity ?? record.TotalQuantity),
    productCount: readNumber(record.productCount ?? record.ProductCount),
  }
}

function normalizeCompactSummary(raw: unknown): CompactSalesBoardSummary {
  const record = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>
  return {
    totalAmount: readNumber(record.totalAmount ?? record.TotalAmount),
    totalQuantity: readNumber(record.totalQuantity ?? record.TotalQuantity),
    productCount: readNumber(record.productCount ?? record.ProductCount),
    storeCount: readNumber(record.storeCount ?? record.StoreCount),
    supplierCount: readNumber(record.supplierCount ?? record.SupplierCount),
    overallAmount: readNumber(record.overallAmount ?? record.OverallAmount),
    overallQuantity: readNumber(record.overallQuantity ?? record.OverallQuantity),
  }
}

function normalizeCompactProduct(raw: unknown): CompactSalesBoardProduct | null {
  if (!raw || typeof raw !== 'object') return null
  const record = raw as Record<string, unknown>
  const productCode = readString(record.productCode ?? record.ProductCode)
  if (!productCode) return null

  return {
    productCode,
    itemNumber: readString(record.itemNumber ?? record.ItemNumber),
    productImage: readString(record.productImage ?? record.ProductImage),
    productName: readString(record.productName ?? record.ProductName),
    chinaSupplierCode: readString(record.chinaSupplierCode ?? record.ChinaSupplierCode),
    chinaSupplierName: readString(record.chinaSupplierName ?? record.ChinaSupplierName),
    totalQuantity: readNumber(record.totalQuantity ?? record.TotalQuantity),
    unitPrice: readNumber(record.unitPrice ?? record.UnitPrice),
    totalAmount: readNumber(record.totalAmount ?? record.TotalAmount),
  }
}

function normalizeCompactSalesBoard(payload: ApiResponse<CompactSalesBoard> | CompactSalesBoard): CompactSalesBoard {
  const result = (unwrapDataPayload(payload) ?? {}) as unknown as Record<string, unknown>
  const productDetails = (result.productDetails ?? result.ProductDetails ?? {}) as Record<string, unknown>
  const productData = Array.isArray(productDetails.data ?? productDetails.Data)
    ? ((productDetails.data ?? productDetails.Data) as unknown[])
        .map(normalizeCompactProduct)
        .filter((item): item is CompactSalesBoardProduct => item !== null)
    : []

  return {
    stores: Array.isArray(result.stores ?? result.Stores)
      ? ((result.stores ?? result.Stores) as unknown[]).map(normalizeCompactStore).filter((item): item is CompactSalesBoardStore => item !== null)
      : [],
    chinaSuppliers: Array.isArray(result.chinaSuppliers ?? result.ChinaSuppliers)
      ? ((result.chinaSuppliers ?? result.ChinaSuppliers) as unknown[]).map(normalizeCompactChinaSupplier).filter((item): item is CompactSalesBoardChinaSupplier => item !== null)
      : [],
    productDetails: {
      data: productData,
      total: readNumber(productDetails.total ?? productDetails.Total),
      pageIndex: readNumber(productDetails.pageIndex ?? productDetails.PageIndex, 1),
      pageSize: readNumber(productDetails.pageSize ?? productDetails.PageSize),
      scopeAmount: readNumber(productDetails.scopeAmount ?? productDetails.ScopeAmount),
    },
    summary: normalizeCompactSummary(result.summary ?? result.Summary),
    statisticStatus: readString(result.statisticStatus ?? result.StatisticStatus),
    statisticMessage: readString(result.statisticMessage ?? result.StatisticMessage),
    statisticUpdatedAt: readString(result.statisticUpdatedAt ?? result.StatisticUpdatedAt),
    fromCache: (result.fromCache ?? result.FromCache) === true,
  }
}

export async function getSupplierSalesRank(
  dateRange: DateRange,
  topN = 20,
  branchCodes?: string[],
): Promise<ApiResponse<SupplierSalesRank[]>> {
  const response = await request<ApiResponse<SupplierSalesRank[]> | SupplierSalesRank[]>(
    '/api/react/v1/dashboard/supplier-sales-rank',
    {
      method: 'GET',
      params: {
        ...dateRange,
        topN,
        branchCodes,
      },
    },
  )

  return unwrapApiResponse(response)
}

export async function getChinaSupplierSalesRank(
  dateRange: DateRange,
  topN = 20,
  branchCodes?: string[],
): Promise<ApiResponse<ChinaSupplierSalesRank[]>> {
  const response = await request<ApiResponse<ChinaSupplierSalesRank[]> | ChinaSupplierSalesRank[]>(
    '/api/react/v1/dashboard/china-supplier-sales-rank',
    {
      method: 'GET',
      params: {
        ...dateRange,
        topN,
        branchCodes,
      },
    },
  )

  return unwrapApiResponse(response)
}

export async function getEnhancedSalesProductDetails(
  dateRange: DateRange,
  branchCodes?: string[],
  localSupplierCodes?: string[],
  chinaSupplierCodes?: string[],
  pageIndex = 1,
  pageSize = 100,
): Promise<ApiResponse<PagedSalesProductDetailWithDiscount>> {
  const response = await request<
    ApiResponse<PagedSalesProductDetailWithDiscount> | PagedSalesProductDetailWithDiscount
  >('/api/react/v1/dashboard/enhanced-sales-product-details', {
    method: 'GET',
    params: {
      ...dateRange,
      branchCodes,
      localSupplierCodes,
      chinaSupplierCodes,
      pageIndex,
      pageSize,
    },
  })

  return unwrapApiResponse(response)
}

export async function getBranchSalesAggregate(
  dateRange: DateRange,
  compareDateRange?: DateRange,
  branchCodes?: string[],
  supplierCodes?: string[],
): Promise<ApiResponse<BranchSalesAggregate[]>> {
  const response = await request<ApiResponse<BranchSalesAggregate[]> | BranchSalesAggregate[]>(
    '/api/react/v1/dashboard/branch-sales-aggregate',
    {
      method: 'GET',
      params: {
        startDate: dateRange.startDate,
        endDate: dateRange.endDate,
        compareStartDate: compareDateRange?.startDate,
        compareEndDate: compareDateRange?.endDate,
        branchCodes,
        supplierCodes,
      },
    },
  )

  return unwrapApiResponse(response)
}

export async function getCompactSalesBoard(
  query: CompactSalesBoardRequest,
  signal?: AbortSignal,
): Promise<CompactSalesBoard> {
  const response = await request<ApiResponse<CompactSalesBoard> | CompactSalesBoard>(
    '/api/react/v1/dashboard/compact-sales-board',
    {
      method: 'GET',
      signal,
      params: {
        startDate: query.dateRange.startDate,
        endDate: query.dateRange.endDate,
        branchCodes: query.branchCodes,
        selectedBranchCode: query.selectedBranchCode,
        selectedChinaSupplierCode: query.selectedChinaSupplierCode,
        selectedProductCode: query.selectedProductCode,
        keyword: query.keyword?.trim() || undefined,
        sortField: query.sortField,
        sortOrder: query.sortOrder,
        pageIndex: query.pageIndex ?? 1,
        pageSize: query.pageSize ?? 80,
        forceRefresh: query.forceRefresh ?? false,
      },
    },
  )

  return normalizeCompactSalesBoard(response)
}

export async function getWeeklyPerformanceHierarchy(
  dateRange: DateRange,
  branchCodes?: string[],
): Promise<ApiResponse<WeeklyHierarchyData[]>> {
  const response = await request<ApiResponse<WeeklyHierarchyData[]> | WeeklyHierarchyData[]>(
    '/api/react/v1/dashboard/weekly-performance-hierarchy',
    {
      method: 'GET',
      params: {
        ...dateRange,
        branchCodes,
      },
    },
  )

  return unwrapApiResponse(response)
}

export async function getExecutiveBranchPerformance(
  dateRange: DateRange,
  topN = 100,
  branchCodes?: string[],
): Promise<ApiResponse<ExecutiveBranchPerformance[]>> {
  const response = await request<ApiResponse<ExecutiveBranchPerformance[]> | ExecutiveBranchPerformance[]>(
    '/api/react/v1/dashboard/executive-branch-performance',
    {
      method: 'GET',
      params: {
        ...dateRange,
        topN,
        branchCodes,
      },
    },
  )

  return unwrapApiResponse(response)
}

export async function getExecutiveHourlyTraffic(
  dateRange: DateRange,
  branchCodes?: string[],
): Promise<ApiResponse<ExecutiveHourlyTraffic[]>> {
  const response = await request<ApiResponse<ExecutiveHourlyTraffic[]> | ExecutiveHourlyTraffic[]>(
    '/api/react/v1/dashboard/executive-hourly-traffic',
    {
      method: 'GET',
      params: {
        ...dateRange,
        branchCodes,
      },
    },
  )

  return unwrapApiResponse(response)
}
