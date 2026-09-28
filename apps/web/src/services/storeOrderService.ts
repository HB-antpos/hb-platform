import type { ApiResponse } from '../types/api'
import type { SupplyPlan } from '../types/supplyNotice'
import type {
  AddStoreOrderLinePayload,
  BatchAddStoreOrderLinePayload,
  PasteReplaceStoreOrderLinesPayload,
  RefreshStoreOrderImportPricesPayload,
  RefreshStoreOrderImportPricesResult,
  BatchUpdateStoreOrderLinePayload,
  BatchUpdateStoreOrderProductStatusPayload,
  CopyStoreOrderPayload,
  CopyStoreOrderResult,
  CreateStoreOrderPayload,
  RemoveStoreOrderLinePayload,
  StoreOrderHqSyncPayload,
  StoreOrderImportPriceVarianceDetailItem,
  StoreOrderImportPriceVarianceDetailQuery,
  StoreOrderImportPriceVarianceDetailResult,
  StoreOrderImportPriceVarianceDomesticPriceUpdatePayload,
  StoreOrderImportPriceVarianceDomesticPriceUpdateResult,
  StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdatePayload,
  StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResult,
  StoreOrderImportPriceVarianceWarehouseImportPriceUpdatePayload,
  StoreOrderImportPriceVarianceWarehouseImportPriceUpdateResult,
  StoreOrderImportPriceVarianceItem,
  StoreOrderImportPriceVarianceQuery,
  StoreOrderImportPriceVarianceResult,
  StoreOrderImportPriceVarianceSummary,
  StoreOrderImportPriceVarianceSupplierSummary,
  SyncMissingStoreOrdersPayload,
  SyncMissingStoreOrdersResult,
  StoreOrderSyncJobResult,
  StoreOrderSyncJobStatus,
  StoreOrderInvoiceEmailJobResult,
  StoreOrderInvoiceEmailSentInfo,
  StoreOrderPasteReplaceJobResult,
  StoreOrderBatchMapStoreCodePayload,
  StoreOrderBatchMapStoreCodeResult,
  StoreOrderBatchStatusUpdatePayload,
  StoreOrderBranchOption,
  StoreOrderDetail,
  StoreOrderDetailQuery,
  StoreOrderBatchLookupItem,
  StoreOrderBatchLookupPayload,
  StoreOrderCart,
  SubmitStoreOrderKeptLine,
  SubmitStoreOrderResult,
  StoreOrderListItem,
  StoreOrderListQuery,
  StoreOrderListResult,
  StoreOrderScanLookupResult,
  StoreOrderDynamicData,
  StoreOrderDynamicDataRequest,
  StoreOrderPasteTargetField,
  StoreOrderProductItem,
  StoreOrderProductListResult,
  StoreOrderProductQuery,
  StoreOrderProductActivityHistoryItem,
  StoreOrderProductActivityHistoryQuery,
  StoreOrderProductActivityHistoryResult,
  StoreOrderProductActivityRecordType,
  StoreOrderProductSalesSummaryItem,
  StoreOrderProductSalesSummaryQuery,
  StoreOrderStatusUpdatePayload,
  UnmatchedStoreOrderGroup,
  SendStoreOrderInvoiceEmailPayload,
  TranslateStoreOrderInvoiceEmailTextPayload,
  TranslateStoreOrderInvoiceEmailTextResult,
  UpdateStoreOrderStoreContactPayload,
  UpdateStoreOrderHeaderPayload,
  UpdateStoreOrderOutboundDatePayload,
  UpdateStoreOrderLinePayload,
  UpdateStoreOrderProductStatusPayload,
} from '../types/storeOrder'
import request from '../utils/request'

const API_BASE = '/api/react/v1/store-order'

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function unwrapEnvelope<T>(payload: unknown): T {
  let current = payload

  for (let depth = 0; depth < 3; depth += 1) {
    if (!isRecord(current) || !('data' in current)) {
      break
    }

    const keys = Object.keys(current)
    const looksLikeEnvelope =
      keys.includes('data') &&
      (keys.includes('success') ||
        keys.includes('isSuccess') ||
        keys.includes('message') ||
        keys.includes('errorCode') ||
        keys.includes('code'))

    if (!looksLikeEnvelope) {
      break
    }

    current = current.data
  }

  return current as T
}

function normalizePagedList<T>(payload: unknown): StoreOrderListResult {
  const result = unwrapEnvelope<{
    items?: T[]
    total?: number
    page?: number
    pageSize?: number
    pageNumber?: number
  }>(payload)

  return {
    items: (result?.items ?? []) as StoreOrderListItem[],
    total: result?.total ?? 0,
    page: result?.page ?? result?.pageNumber ?? 1,
    pageSize: result?.pageSize ?? 10,
  }
}

function normalizeProductPagedList(payload: unknown): StoreOrderProductListResult {
  const result = unwrapEnvelope<{
    items?: StoreOrderProductItem[]
    total?: number
    page?: number
    pageNumber?: number
    pageSize?: number
  }>(payload)

  return {
    items: result?.items ?? [],
    total: result?.total ?? 0,
    page: result?.page ?? result?.pageNumber ?? 1,
    pageSize: result?.pageSize ?? 24,
  }
}

function normalizeStoreOrderProductActivityHistoryResult(
  payload: unknown,
  query: StoreOrderProductActivityHistoryQuery,
): StoreOrderProductActivityHistoryResult {
  const result = unwrapEnvelope<{
    items?: Array<Record<string, unknown>>
    total?: number | string
    pageNumber?: number | string
    pageSize?: number | string
    lastArrivalDate?: string | null
    endDate?: string | null
    latestOrderQuantity?: number | string
    latestAllocQuantity?: number | string
    totalSalesQuantity?: number | string
  }>(payload)

  const items = Array.isArray(result?.items)
    ? result.items
      .filter((item): item is Record<string, unknown> => Boolean(item && typeof item === 'object'))
        .map((item): StoreOrderProductActivityHistoryItem => {
          const recordType: StoreOrderProductActivityRecordType =
            item.recordType === 'salesSubtotal'
              ? 'salesSubtotal'
              : item.recordType === 'sales'
                ? 'sales'
                : 'order'
          const rawFlowStatus =
            typeof item.flowStatus === 'number'
              ? item.flowStatus
              : typeof item.flowStatus === 'string' && item.flowStatus.trim()
                ? Number(item.flowStatus)
                : undefined

          return {
            recordType,
            recordDate: typeof item.recordDate === 'string' ? item.recordDate : undefined,
            periodStartDate:
              typeof item.periodStartDate === 'string' ? item.periodStartDate : undefined,
            periodEndDate:
              typeof item.periodEndDate === 'string' ? item.periodEndDate : undefined,
            orderGUID: typeof item.orderGUID === 'string' ? item.orderGUID : undefined,
            orderNo: typeof item.orderNo === 'string' && item.orderNo.trim() ? item.orderNo : undefined,
            orderDate: typeof item.orderDate === 'string' ? item.orderDate : undefined,
            outboundDate: typeof item.outboundDate === 'string' ? item.outboundDate : undefined,
            flowStatus:
              typeof rawFlowStatus === 'number' && Number.isFinite(rawFlowStatus)
                ? rawFlowStatus
                : undefined,
            quantity: readOptionalFiniteNumber(item.quantity),
            allocQuantity: readOptionalFiniteNumber(item.allocQuantity),
            salesQuantity: readOptionalFiniteNumber(item.salesQuantity),
            averagePrice:
              item.averagePrice === null
                ? null
                : typeof item.averagePrice === 'number'
                  ? item.averagePrice
                  : undefined,
          }
        })
    : []

  return {
    items,
    total: readFiniteNumber(result?.total, 0),
    pageNumber: readFiniteNumber(result?.pageNumber, query.pageNumber || 1),
    pageSize: readFiniteNumber(result?.pageSize, query.pageSize || 30),
    lastArrivalDate: typeof result?.lastArrivalDate === 'string' ? result.lastArrivalDate : null,
    endDate: typeof result?.endDate === 'string' ? result.endDate : null,
    latestOrderQuantity: readFiniteNumber(result?.latestOrderQuantity, 0),
    latestAllocQuantity: readFiniteNumber(result?.latestAllocQuantity, 0),
    totalSalesQuantity: readFiniteNumber(result?.totalSalesQuantity, 0),
  }
}

function readFiniteNumber(value: unknown, fallback = 0) {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value
  }

  if (typeof value === 'string' && value.trim()) {
    const parsed = Number(value)
    return Number.isFinite(parsed) ? parsed : fallback
  }

  return fallback
}

function readOptionalFiniteNumber(value: unknown): number | undefined {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value
  }

  if (typeof value === 'string' && value.trim()) {
    const parsed = Number(value)
    return Number.isFinite(parsed) ? parsed : undefined
  }

  return undefined
}

function normalizeImportPriceVarianceSummary(payload: unknown): StoreOrderImportPriceVarianceSummary {
  const summary = isRecord(payload) ? payload : {}

  return {
    totalRows: readFiniteNumber(summary.totalRows),
    originalImportAmountTotal: readFiniteNumber(summary.originalImportAmountTotal),
    baselineImportAmountTotal: readFiniteNumber(summary.baselineImportAmountTotal),
    varianceAmountTotal: readFiniteNumber(summary.varianceAmountTotal),
  }
}

function readOptionalText(value: unknown) {
  return typeof value === 'string' && value.trim() ? value : undefined
}

function normalizeImportPriceVarianceSupplierSummary(
  payload: unknown,
): StoreOrderImportPriceVarianceSupplierSummary {
  const summary = isRecord(payload) ? payload : {}

  return {
    supplierCode: readOptionalText(summary.supplierCode),
    supplierName: readOptionalText(summary.supplierName),
    productCount: readFiniteNumber(summary.productCount),
    detailCount: readFiniteNumber(summary.detailCount),
    originalImportAmountTotal: readFiniteNumber(summary.originalImportAmountTotal),
    baselineImportAmountTotal: readFiniteNumber(summary.baselineImportAmountTotal),
    increaseVarianceAmountTotal: readFiniteNumber(summary.increaseVarianceAmountTotal),
    decreaseVarianceAmountTotal: readFiniteNumber(summary.decreaseVarianceAmountTotal),
    varianceAmountTotal: readFiniteNumber(summary.varianceAmountTotal),
  }
}

function normalizeStoreOrderImportPriceVarianceResult(
  payload: unknown,
  query: StoreOrderImportPriceVarianceQuery,
): StoreOrderImportPriceVarianceResult {
  const result = unwrapEnvelope<{
    items?: StoreOrderImportPriceVarianceItem[]
    total?: number | string
    page?: number | string
    pageNumber?: number | string
    pageSize?: number | string
    summary?: unknown
    supplierSummaries?: unknown
  }>(payload)

  return {
    items: Array.isArray(result?.items) ? result.items : [],
    total: readFiniteNumber(result?.total),
    page: readFiniteNumber(result?.page ?? result?.pageNumber, query.pageNumber || 1),
    pageSize: readFiniteNumber(result?.pageSize, query.pageSize || 20),
    summary: normalizeImportPriceVarianceSummary(result?.summary),
    supplierSummaries: Array.isArray(result?.supplierSummaries)
      ? result.supplierSummaries.map(normalizeImportPriceVarianceSupplierSummary)
      : [],
  }
}

function normalizeStoreOrderImportPriceVarianceDetailResult(
  payload: unknown,
  query: StoreOrderImportPriceVarianceDetailQuery,
): StoreOrderImportPriceVarianceDetailResult {
  const result = unwrapEnvelope<{
    items?: StoreOrderImportPriceVarianceDetailItem[]
    total?: number | string
    page?: number | string
    pageNumber?: number | string
    pageSize?: number | string
    summary?: unknown
  }>(payload)

  return {
    items: Array.isArray(result?.items) ? result.items : [],
    total: readFiniteNumber(result?.total),
    page: readFiniteNumber(result?.page ?? result?.pageNumber, query.pageNumber || 1),
    pageSize: readFiniteNumber(result?.pageSize, query.pageSize || 20),
    summary: normalizeImportPriceVarianceSummary(result?.summary),
  }
}

function normalizeStoreOrderImportPriceVarianceDomesticPriceUpdateResult(
  payload: unknown,
): StoreOrderImportPriceVarianceDomesticPriceUpdateResult {
  const result = unwrapEnvelope<{
    productCode?: unknown
    domesticPrice?: unknown
  }>(payload)

  return {
    productCode: typeof result?.productCode === 'string' ? result.productCode : '',
    domesticPrice: readFiniteNumber(result?.domesticPrice),
  }
}

function normalizeStoreOrderImportPriceVarianceWarehouseImportPriceUpdateResult(
  payload: unknown,
): StoreOrderImportPriceVarianceWarehouseImportPriceUpdateResult {
  const result = unwrapEnvelope<{
    productCode?: unknown
    warehouseImportPrice?: unknown
  }>(payload)

  return {
    productCode: typeof result?.productCode === 'string' ? result.productCode : '',
    warehouseImportPrice: readFiniteNumber(result?.warehouseImportPrice),
  }
}

function normalizeStoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResult(
  payload: unknown,
): StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResult {
  const result = unwrapEnvelope<{
    updatedCount?: unknown
    warehouseImportPrice?: unknown
    productCodes?: unknown
  }>(payload)

  return {
    updatedCount: readFiniteNumber(result?.updatedCount),
    warehouseImportPrice: readFiniteNumber(result?.warehouseImportPrice),
    productCodes: Array.isArray(result?.productCodes)
      ? result.productCodes.filter((code): code is string => typeof code === 'string')
      : [],
  }
}

type StoreOrderAmountLine = {
  allocatedImportAmount?: number
  allocQuantity?: number
  importPrice?: number
}

function normalizeAllocatedImportAmount<T extends StoreOrderAmountLine>(item: T): T & { allocatedImportAmount: number } {
  // 兼容旧接口：旧响应没有 allocatedImportAmount 时，用发货数量和进口价计算发票金额。
  return {
    ...item,
    allocatedImportAmount: item.allocatedImportAmount ?? Number(item.allocQuantity ?? 0) * Number(item.importPrice ?? 0),
  }
}

function normalizeCart(payload: unknown, options?: { isSummaryOnly?: boolean }): StoreOrderCart | null {
  const result = normalizeResult<Partial<StoreOrderCart> | null>(payload)
  if (!result) {
    return null
  }
  const invoiceEmailSentInfo = normalizeStoreOrderInvoiceEmailSentInfo(result.invoiceEmailSentInfo)
  const items = Array.isArray(result.items) ? result.items : []

  return {
    orderGUID: result.orderGUID ?? '',
    orderNo: result.orderNo,
    storeCode: result.storeCode,
    storeName: result.storeName,
    totalAmount: result.totalAmount ?? 0,
    totalQuantity: result.totalQuantity ?? 0,
    totalSKU: result.totalSKU ?? 0,
    totalImportAmount: result.totalImportAmount ?? 0,
    totalAllocatedImportAmount: result.totalAllocatedImportAmount,
    totalVolume: result.totalVolume ?? 0,
    remarks: result.remarks,
    shippingFee: result.shippingFee,
    orderDate: result.orderDate,
    outboundDate: result.outboundDate,
    storeAddress: result.storeAddress,
    storeContactEmail: result.storeContactEmail,
    flowStatus: result.flowStatus,
    invoiceEmailSentInfo,
    isSummaryOnly: Boolean(options?.isSummaryOnly),
    items,
  }
}

function normalizeStoreOrderDetail(payload: unknown): StoreOrderDetail | null {
  const result = normalizeResult<Partial<StoreOrderDetail> | null>(payload)
  if (!result) {
    return null
  }

  const items = Array.isArray(result.items) ? result.items.map(normalizeAllocatedImportAmount) : []
  const invoiceEmailSentInfo = normalizeStoreOrderInvoiceEmailSentInfo(result.invoiceEmailSentInfo)

  return {
    ...result,
    orderGUID: result.orderGUID ?? '',
    totalAmount: result.totalAmount ?? 0,
    totalQuantity: result.totalQuantity ?? 0,
    totalImportAmount: result.totalImportAmount ?? 0,
    totalAllocatedImportAmount:
      result.totalAllocatedImportAmount ?? items.reduce((sum, item) => sum + item.allocatedImportAmount, 0),
    totalVolume: result.totalVolume ?? 0,
    itemsTotal: result.itemsTotal ?? items.length,
    invoiceEmailSentInfo,
    items,
  } as StoreOrderDetail
}

function normalizeStoreOrderInvoiceEmailSentInfo(value: unknown): StoreOrderInvoiceEmailSentInfo | undefined {
  if (!isRecord(value)) {
    return undefined
  }

  return {
    hasSent: Boolean(value.hasSent),
    sentAt: typeof value.sentAt === 'string' ? value.sentAt : undefined,
    toEmail: typeof value.toEmail === 'string' ? value.toEmail : undefined,
    jobId: typeof value.jobId === 'string' ? value.jobId : undefined,
  }
}

function normalizeResult<T>(payload: unknown): T {
  return unwrapEnvelope<T>(payload)
}

function normalizeStoreOrderSyncPayload(payload?: SyncMissingStoreOrdersPayload) {
  const storeCodes = Array.from(
    new Set(
      (payload?.storeCodes?.length ? payload.storeCodes : payload?.storeCode ? [payload.storeCode] : [])
        .map((item) => item.trim())
        .filter(Boolean),
    ),
  )

  return storeCodes.length ? { storeCodes } : {}
}

function buildStoreOrderDetailQueryParams(query: StoreOrderDetailQuery) {
  const { columnFilters, ...params } = query

  return {
    ...params,
    // 明细列头筛选走 GET；这里主动展平成一层 query，避免全局 request 把对象转成 [object Object]。
    ...(columnFilters ?? {}),
  }
}

function normalizeStoreOrderHqIncrementalSyncPayload(payload?: StoreOrderHqSyncPayload) {
  const basePayload = normalizeStoreOrderSyncPayload(payload)
  return {
    ...basePayload,
    ...(payload?.startDate ? { startDate: payload.startDate } : {}),
    ...(payload?.endDate ? { endDate: payload.endDate } : {}),
    ...(payload?.conflictStrategy ? { conflictStrategy: payload.conflictStrategy } : {}),
  }
}

function normalizeStoreOrderSyncJobStatus(status: unknown): StoreOrderSyncJobStatus {
  if (typeof status !== 'string') {
    return 'Running'
  }

  switch (status.trim().toLowerCase()) {
    case 'queued':
    case 'pending':
      return 'Queued'
    case 'running':
      return 'Running'
    case 'succeeded':
      return 'Succeeded'
    case 'failed':
      return 'Failed'
    default:
      return 'Running'
  }
}

function normalizeStoreOrderSyncJobResult(
  payload: unknown,
  fallbackJobId = '',
): StoreOrderSyncJobResult {
  const rawPayload = isRecord(payload) ? payload : null
  const rawResult = normalizeResult<Record<string, unknown> | null>(payload)
  const result = isRecord(rawResult) ? rawResult : {}
  const nestedResult = isRecord(result.result) ? result.result : {}

  const readNumber = (...values: unknown[]) =>
    values.find((value): value is number => typeof value === 'number')
  const message =
    typeof result.message === 'string'
      ? result.message
      : typeof nestedResult.message === 'string'
        ? nestedResult.message
        : rawPayload && typeof rawPayload.message === 'string'
          ? rawPayload.message
          : undefined
  const success =
    typeof result.success === 'boolean'
      ? result.success
      : typeof nestedResult.success === 'boolean'
        ? nestedResult.success
        : rawPayload && typeof rawPayload.success === 'boolean'
          ? rawPayload.success
          : undefined
  const resolvedStatus =
    typeof result.status === 'string'
      ? normalizeStoreOrderSyncJobStatus(result.status)
      : success === false
        ? 'Failed'
        : 'Running'

  return {
    jobId: typeof result.jobId === 'string' ? result.jobId : fallbackJobId,
    status: resolvedStatus,
    mode:
      result.mode === 'Full' || result.mode === 'Incremental'
        ? result.mode
        : nestedResult.mode === 'Full' || nestedResult.mode === 'Incremental'
          ? nestedResult.mode
          : undefined,
    conflictStrategy:
      result.conflictStrategy === 'LatestWins' || result.conflictStrategy === 'HqWins'
        ? result.conflictStrategy
        : nestedResult.conflictStrategy === 'LatestWins' || nestedResult.conflictStrategy === 'HqWins'
          ? nestedResult.conflictStrategy
          : undefined,
    message,
    success,
    storeCodes: Array.isArray(result.storeCodes)
      ? result.storeCodes.filter((item): item is string => typeof item === 'string')
      : undefined,
    startDate: typeof result.startDate === 'string' ? result.startDate : undefined,
    endDate: typeof result.endDate === 'string' ? result.endDate : undefined,
    ordersSynced: readNumber(result.ordersSynced, nestedResult.ordersSynced),
    detailsSynced: readNumber(result.detailsSynced, nestedResult.detailsSynced),
    ordersUpdated: readNumber(result.ordersUpdated, nestedResult.ordersUpdated),
    detailsUpdated: readNumber(result.detailsUpdated, nestedResult.detailsUpdated),
    ordersSoftDeleted: readNumber(result.ordersSoftDeleted, nestedResult.ordersSoftDeleted),
    detailsSoftDeleted: readNumber(result.detailsSoftDeleted, nestedResult.detailsSoftDeleted),
    skippedOrdersBecauseLocalNewer: readNumber(
      result.skippedOrdersBecauseLocalNewer,
      nestedResult.skippedOrdersBecauseLocalNewer,
    ),
    skippedDetailsBecauseLocalNewer: readNumber(
      result.skippedDetailsBecauseLocalNewer,
      nestedResult.skippedDetailsBecauseLocalNewer,
    ),
    hqOrderCount: readNumber(result.hqOrderCount, nestedResult.hqOrderCount),
    hqDetailCount: readNumber(result.hqDetailCount, nestedResult.hqDetailCount),
    shadowRowCount: readNumber(result.shadowRowCount, nestedResult.shadowRowCount),
    durationMs: readNumber(result.durationMs, nestedResult.durationMs),
    errors: Array.isArray(nestedResult.errors)
      ? nestedResult.errors.filter((item): item is string => typeof item === 'string')
      : Array.isArray(result.errors)
        ? result.errors.filter((item): item is string => typeof item === 'string')
        : undefined,
  }
}

function normalizeStoreOrderInvoiceEmailJobResult(
  payload: unknown,
  fallbackJobId = '',
): StoreOrderInvoiceEmailJobResult {
  const rawPayload = isRecord(payload) ? payload : null
  const result = normalizeResult<Record<string, unknown> | null>(payload)
  const job = isRecord(result) ? result : {}
  const message =
    typeof job.message === 'string'
      ? job.message
      : rawPayload && typeof rawPayload.message === 'string'
        ? rawPayload.message
        : undefined

  return {
    jobId: typeof job.jobId === 'string' ? job.jobId : fallbackJobId,
    status: normalizeStoreOrderSyncJobStatus(job.status),
    message,
    orderGUID: typeof job.orderGUID === 'string' ? job.orderGUID : undefined,
    toEmail: typeof job.toEmail === 'string' ? job.toEmail : undefined,
    createdAt: typeof job.createdAt === 'string' ? job.createdAt : undefined,
    completedAt: typeof job.completedAt === 'string' ? job.completedAt : undefined,
  }
}

function normalizeStoreOrderPasteReplaceJobResult(
  payload: unknown,
  fallbackJobId = '',
): StoreOrderPasteReplaceJobResult {
  const rawPayload = isRecord(payload) ? payload : null
  const result = normalizeResult<Record<string, unknown> | null>(payload)
  const job = isRecord(result) ? result : {}
  const success = rawPayload && typeof rawPayload.success === 'boolean' ? rawPayload.success : undefined
  const message =
    typeof job.message === 'string'
      ? job.message
      : rawPayload && typeof rawPayload.message === 'string'
        ? rawPayload.message
        : undefined
  const status = typeof job.status === 'string'
    ? normalizeStoreOrderSyncJobStatus(job.status)
    : success === false
      ? 'Failed'
      : 'Running'

  return {
    jobId: typeof job.jobId === 'string' ? job.jobId : fallbackJobId,
    status,
    message,
    orderGUID: typeof job.orderGUID === 'string' ? job.orderGUID : undefined,
    targetField: job.targetField === 'allocQuantity' || job.targetField === 'quantity' ? job.targetField : undefined,
    totalCount: typeof job.totalCount === 'number' ? job.totalCount : undefined,
    importedCount: typeof job.importedCount === 'number' ? job.importedCount : undefined,
    skippedCount: typeof job.skippedCount === 'number' ? job.skippedCount : undefined,
    createdAt: typeof job.createdAt === 'string' ? job.createdAt : undefined,
    completedAt: typeof job.completedAt === 'string' ? job.completedAt : undefined,
  }
}

export async function getStoreOrderList(query: StoreOrderListQuery) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/list`, {
    method: 'POST',
    data: query,
  })

  return normalizePagedList<StoreOrderListItem>(response)
}

export async function getStoreOrderImportPriceVariance(query: StoreOrderImportPriceVarianceQuery) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/import-price-variance`, {
    method: 'POST',
    data: query,
  })

  return normalizeStoreOrderImportPriceVarianceResult(response, query)
}

export async function getStoreOrderImportPriceVarianceDetails(query: StoreOrderImportPriceVarianceDetailQuery) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/import-price-variance/details`, {
    method: 'POST',
    data: query,
  })

  return normalizeStoreOrderImportPriceVarianceDetailResult(response, query)
}

export async function updateStoreOrderImportPriceVarianceDomesticPrice(
  payload: StoreOrderImportPriceVarianceDomesticPriceUpdatePayload,
) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/import-price-variance/domestic-price`, {
    method: 'POST',
    data: payload,
  })

  return normalizeStoreOrderImportPriceVarianceDomesticPriceUpdateResult(response)
}

export async function updateStoreOrderImportPriceVarianceWarehouseImportPrice(
  payload: StoreOrderImportPriceVarianceWarehouseImportPriceUpdatePayload,
) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/import-price-variance/warehouse-import-price`,
    {
      method: 'POST',
      data: payload,
    },
  )

  return normalizeStoreOrderImportPriceVarianceWarehouseImportPriceUpdateResult(response)
}

export async function batchUpdateStoreOrderImportPriceVarianceWarehouseImportPrice(
  payload: StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdatePayload,
) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/import-price-variance/warehouse-import-price/batch`,
    {
      method: 'POST',
      data: payload,
    },
  )

  return normalizeStoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResult(response)
}

export async function getUsedStoreOrderBranches() {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/used-branches`, {
    method: 'GET',
  })

  return normalizeResult<StoreOrderBranchOption[]>(response)
}

export async function getUnmatchedStoreOrderGroups() {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/unmatched-store-groups`, {
    method: 'GET',
  })

  const result = normalizeResult<UnmatchedStoreOrderGroup[] | null>(response)
  return Array.isArray(result) ? result : []
}

export async function batchMapStoreOrderStoreCode(payload: StoreOrderBatchMapStoreCodePayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/batch-map-store-code`, {
    method: 'POST',
    data: payload,
  })

  return normalizeResult<StoreOrderBatchMapStoreCodeResult>(response)
}

export async function getStoreOrderDetail(
  orderGuid: string,
  query?: StoreOrderDetailQuery,
  signal?: AbortSignal,
) {
  const response = await request<ApiResponse<unknown> | unknown>(
    query ? `${API_BASE}/detail/${orderGuid}` : `${API_BASE}/detail/${orderGuid}/full`,
    {
      method: 'GET',
      params: query ? buildStoreOrderDetailQueryParams(query) : undefined,
      signal,
    },
  )

  return normalizeStoreOrderDetail(response)
}

export async function getStoreOrderDetailFull(orderGuid: string, signal?: AbortSignal) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/detail/${orderGuid}/full`, {
    method: 'GET',
    signal,
  })

  return normalizeStoreOrderDetail(response)
}

export async function getStoreOrderDetailProductCodes(orderGuid: string, signal?: AbortSignal) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/detail/${orderGuid}/product-codes`,
    {
      method: 'GET',
      signal,
    },
  )

  const result = normalizeResult<unknown>(response)
  return Array.isArray(result) ? result.filter((item): item is string => typeof item === 'string') : []
}

export async function getStoreOrderProducts(query: StoreOrderProductQuery, signal?: AbortSignal) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/products`, {
    method: 'POST',
    data: query,
    signal,
  })

  return normalizeProductPagedList(response)
}

export async function batchLookupStoreOrderProducts(payload: StoreOrderBatchLookupPayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/products/batch-lookup`, {
    method: 'POST',
    data: payload,
  })

  return normalizeResult<StoreOrderBatchLookupItem[]>(response)
}

export async function lookupStoreOrderProductsByBarcode(barcode: string, signal?: AbortSignal) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/products/scan-lookup`, {
    method: 'POST',
    data: { barcode },
    signal,
  })

  const result = normalizeResult<Partial<StoreOrderScanLookupResult> | null>(response)

  return {
    barcode: result?.barcode ?? barcode,
    items: Array.isArray(result?.items) ? (result?.items as StoreOrderProductItem[]) : [],
  } satisfies StoreOrderScanLookupResult
}

export async function getStoreOrderProductsDynamicData(payload: StoreOrderDynamicDataRequest) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/dynamic-data`, {
    method: 'POST',
    data: payload,
  })

  const result = normalizeResult<StoreOrderDynamicData[] | null>(response)
  return Array.isArray(result) ? result : []
}

export async function getStoreOrderProductSalesSummary(query: StoreOrderProductSalesSummaryQuery) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/sales-since-last-arrival/summary`,
    {
      method: 'POST',
      data: query,
    },
  )
  const result = normalizeResult<unknown>(response)

  if (!Array.isArray(result)) {
    return []
  }

  return result.reduce<StoreOrderProductSalesSummaryItem[]>((items, item) => {
    if (!isRecord(item) || typeof item.productCode !== 'string') {
      return items
    }

    const salesQuantity = item.salesQuantitySinceLastArrival
    items.push({
      productCode: item.productCode,
      // 0 与负数是有效业务值；只有缺失或非法值归一为隐藏所用的 null。
      salesQuantitySinceLastArrival:
        typeof salesQuantity === 'number' || salesQuantity === null ? salesQuantity : null,
    })
    return items
  }, [])
}

export async function getStoreOrderProductActivityHistory(
  query: StoreOrderProductActivityHistoryQuery,
  signal?: AbortSignal,
) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/product-activity-history`,
    {
      method: 'POST',
      data: query,
      signal,
    },
  )

  return normalizeStoreOrderProductActivityHistoryResult(response, query)
}

export async function getActiveStoreOrderCart(storeCode: string) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/cart/${storeCode}`, {
    method: 'GET',
  })

  return normalizeCart(response)
}

export async function getActiveStoreOrderCartSummary(storeCode: string) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/cart/${storeCode}/summary`, {
    method: 'GET',
  })

  // 登录和切换分店只需要摘要；明细按打开抽屉等真实需要再拉全量。
  return normalizeCart(response, { isSummaryOnly: true })
}

export async function addStoreOrderCartItem(payload: {
  storeCode: string
  productCode: string
  quantity: number
}) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/cart/add`, {
    method: 'POST',
    data: payload,
  })

  return normalizeCart(response)
}

export async function updateStoreOrderCartItem(payload: {
  storeCode: string
  productCode: string
  quantity: number
}) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/cart/update`, {
    method: 'POST',
    data: payload,
  })

  return normalizeCart(response)
}

export async function removeStoreOrderCartItem(payload: {
  storeCode: string
  detailGUID: string
}) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/cart/remove`, {
    method: 'POST',
    data: payload,
  })

  return normalizeCart(response)
}

export async function clearActiveStoreOrderCart(storeCode: string) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/cart/clear`, {
    method: 'POST',
    data: { storeCode },
  })

  return normalizeCart(response)
}

const SUPPLY_PLANS = new Set<string>(['WillRestock', 'Undecided', 'Seasonal', 'Discontinued'])

function normalizeSupplyPlan(value: unknown): SupplyPlan | null {
  return typeof value === 'string' && SUPPLY_PLANS.has(value) ? (value as SupplyPlan) : null
}

/**
 * 提交结果归一化：新后端在 data 里返回保留行；旧后端只返回 true 或空，一律视为整车已提交。
 */
function normalizeSubmitStoreOrderResult(raw: unknown): SubmitStoreOrderResult {
  if (!isRecord(raw) || !Array.isArray(raw.keptLines)) {
    return { submitted: true, submittedLineCount: 0, keptLines: [] }
  }

  const keptLines = raw.keptLines
    .filter(isRecord)
    .map<SubmitStoreOrderKeptLine>((line) => ({
      detailGUID: typeof line.detailGUID === 'string' ? line.detailGUID : undefined,
      productCode: String(line.productCode ?? ''),
      itemNumber: typeof line.itemNumber === 'string' ? line.itemNumber : null,
      productName: typeof line.productName === 'string' ? line.productName : null,
      quantity: Number(line.quantity ?? 0) || 0,
      supplyPlan: normalizeSupplyPlan(line.supplyPlan),
    }))
    .filter((line) => line.productCode)

  return {
    submitted: true,
    orderGUID: typeof raw.orderGUID === 'string' ? raw.orderGUID : null,
    orderNo: typeof raw.orderNo === 'string' ? raw.orderNo : null,
    submittedLineCount: Number(raw.submittedLineCount ?? 0) || 0,
    keptLines,
    keptCartOrderGUID: typeof raw.keptCartOrderGUID === 'string' ? raw.keptCartOrderGUID : null,
  }
}

export async function submitActiveStoreOrder(payload: {
  storeCode: string
  remarks?: string
}): Promise<SubmitStoreOrderResult> {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/submit`, {
    method: 'POST',
    data: payload,
  })

  return normalizeSubmitStoreOrderResult(normalizeResult<unknown>(response))
}

export async function createStoreOrder(payload: CreateStoreOrderPayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/create`, {
    method: 'POST',
    data: payload,
  })

  return normalizeResult<string>(response)
}

export async function copyStoreOrder(payload: CopyStoreOrderPayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/copy`, {
    method: 'POST',
    data: payload,
  })

  return normalizeResult<CopyStoreOrderResult | string>(response)
}

export async function updateStoreOrderStatus(payload: StoreOrderStatusUpdatePayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/status`, {
    method: 'POST',
    data: payload,
  })
}

export async function batchUpdateStoreOrderStatus(payload: StoreOrderBatchStatusUpdatePayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/batch-status`, {
    method: 'POST',
    data: payload,
  })

  return normalizeResult<number | boolean | null>(response)
}

export async function deleteStoreOrder(orderGuid: string) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/${orderGuid}`, {
    method: 'DELETE',
  })
}

export async function addStoreOrderLine(payload: AddStoreOrderLinePayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/line/add`, {
    method: 'POST',
    data: payload,
  })
}

export async function batchAddStoreOrderLines(payload: BatchAddStoreOrderLinePayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/line/batch-add`, {
    method: 'POST',
    data: payload,
  })
}

export async function pasteReplaceStoreOrderLines(payload: PasteReplaceStoreOrderLinesPayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/line/paste-replace`, {
    method: 'POST',
    data: payload,
  })
}

export async function createStoreOrderPasteReplaceJob(payload: PasteReplaceStoreOrderLinesPayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/line/paste-replace/jobs`, {
    method: 'POST',
    data: payload,
  })

  return normalizeStoreOrderPasteReplaceJobResult(response)
}

export async function getStoreOrderPasteReplaceJob(jobId: string) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/line/paste-replace/jobs/${encodeURIComponent(jobId)}`,
    {
      method: 'GET',
    },
  )

  return normalizeStoreOrderPasteReplaceJobResult(response, jobId)
}

export async function updateStoreOrderLine(payload: UpdateStoreOrderLinePayload) {
  const { allocQuantity, ...restPayload } = payload
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/line/update`, {
    method: 'POST',
    // 后端当前接口仍使用 quantity 字段表达发货数，前端类型保持 allocQuantity 语义。
    data: {
      ...restPayload,
      quantity: allocQuantity,
    },
  })
}

export async function removeStoreOrderLine(payload: RemoveStoreOrderLinePayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/line/remove`, {
    method: 'POST',
    data: payload,
  })
}

export async function batchUpdateStoreOrderLines(payload: BatchUpdateStoreOrderLinePayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/line/batch-update`, {
    method: 'POST',
    data: payload,
  })
}

export async function refreshStoreOrderImportPrices(payload: RefreshStoreOrderImportPricesPayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/line/refresh-import-prices`, {
    method: 'POST',
    data: payload,
  })

  const result = normalizeResult<Partial<RefreshStoreOrderImportPricesResult> | null>(response)
  return {
    updatedCount: result?.updatedCount ?? 0,
    unchangedCount: result?.unchangedCount ?? 0,
    skippedCount: result?.skippedCount ?? 0,
    missingWarehousePriceCount: result?.missingWarehousePriceCount ?? 0,
  } satisfies RefreshStoreOrderImportPricesResult
}

export async function updateStoreOrderProductStatus(payload: UpdateStoreOrderProductStatusPayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/product/status`, {
    method: 'POST',
    data: payload,
  })
}

export async function batchUpdateStoreOrderProductStatus(payload: BatchUpdateStoreOrderProductStatusPayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/product/batch-status`, {
    method: 'POST',
    data: payload,
  })
}

export async function updateStoreOrderHeader(payload: UpdateStoreOrderHeaderPayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/header/update`, {
    method: 'POST',
    data: {
      ...payload,
      orderGuid: payload.orderGUID,
    },
  })
}

export async function updateStoreOrderOutboundDate(payload: UpdateStoreOrderOutboundDatePayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/outbound-date`, {
    method: 'POST',
    data: {
      ...payload,
      orderGuid: payload.orderGUID,
    },
  })
}

export async function updateStoreOrderStoreContact(payload: UpdateStoreOrderStoreContactPayload) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/store-contact/update`, {
    method: 'POST',
    data: payload,
  })
}

export async function sendStoreOrderInvoiceEmail(payload: SendStoreOrderInvoiceEmailPayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/invoice/email`, {
    method: 'POST',
    data: payload,
  })

  return normalizeStoreOrderInvoiceEmailJobResult(response)
}

export async function translateStoreOrderInvoiceEmailText(
  payload: TranslateStoreOrderInvoiceEmailTextPayload,
): Promise<TranslateStoreOrderInvoiceEmailTextResult> {
  const response = await request<ApiResponse<TranslateStoreOrderInvoiceEmailTextResult> | unknown>(
    `${API_BASE}/invoice/email/translate-text`,
    {
      method: 'POST',
      data: payload,
    },
  )

  const result = normalizeResult<Record<string, unknown> | null>(response)
  return {
    subject: typeof result?.subject === 'string' ? result.subject : undefined,
    body: typeof result?.body === 'string' ? result.body : undefined,
  }
}

export async function getStoreOrderInvoiceEmailJob(jobId: string) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/invoice/email/jobs/${encodeURIComponent(jobId)}`,
    {
      method: 'GET',
    },
  )

  return normalizeStoreOrderInvoiceEmailJobResult(response, jobId)
}

export async function completeStoreOrder(orderGuid: string) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/complete/${orderGuid}`, {
    method: 'POST',
  })
}

export async function startPickingStoreOrder(orderGuid: string) {
  await request<ApiResponse<unknown> | unknown>(`${API_BASE}/start-picking/${orderGuid}`, {
    method: 'POST',
  })
}

export async function syncMissingStoreOrders(payload?: SyncMissingStoreOrdersPayload) {
  const controller = new AbortController()
  const timeoutId = setTimeout(() => controller.abort(), 10 * 60 * 1000)

  try {
    const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/sync-missing-orders`, {
      method: 'POST',
      data: normalizeStoreOrderSyncPayload(payload),
      signal: controller.signal,
    })

    return normalizeResult<SyncMissingStoreOrdersResult>(response)
  } finally {
    clearTimeout(timeoutId)
  }
}

export async function createStoreOrderSyncJob(payload?: SyncMissingStoreOrdersPayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/sync-missing-orders/jobs`, {
    method: 'POST',
    data: normalizeStoreOrderSyncPayload(payload),
  })

  return normalizeStoreOrderSyncJobResult(response)
}

export async function createStoreOrderFullHqSyncJob() {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/hq-sync/full/jobs`, {
    method: 'POST',
    data: {},
  })

  return normalizeStoreOrderSyncJobResult(response)
}

export async function createStoreOrderIncrementalHqSyncJob(payload?: StoreOrderHqSyncPayload) {
  const response = await request<ApiResponse<unknown> | unknown>(`${API_BASE}/hq-sync/incremental/jobs`, {
    method: 'POST',
    data: normalizeStoreOrderHqIncrementalSyncPayload(payload),
  })

  return normalizeStoreOrderSyncJobResult(response)
}

export async function getStoreOrderSyncJob(jobId: string) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/sync-missing-orders/jobs/${encodeURIComponent(jobId)}`,
    {
      method: 'GET',
    },
  )

  return normalizeStoreOrderSyncJobResult(response, jobId)
}

export async function getStoreOrderHqSyncJob(jobId: string) {
  const response = await request<ApiResponse<unknown> | unknown>(
    `${API_BASE}/hq-sync/jobs/${encodeURIComponent(jobId)}`,
    {
      method: 'GET',
    },
  )

  return normalizeStoreOrderSyncJobResult(response, jobId)
}

export type { StoreOrderPasteTargetField }
