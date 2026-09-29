import { normalizePushToHqStoreOptions } from '../components/posHqPush/storeSelection'
import type { ApiResponse, PagedResult } from '../types/api'
import type {
  BatchUpdateProductStoreRecordsRequest,
  BatchUpdateProductStoreRecordsResult,
  BatchUpdatePosProductDto,
  BatchUpdateSupplierImagesJobRequest,
  BatchUpdateSupplierImagesJobResult,
  BatchUpdateSupplierImagesRequest,
  BatchUpdateSupplierImagesResult,
  CreateProductWithPricesDto,
  CreateProductWithPricesResultDto,
  HqProductFullSyncJobRequest,
  HqProductIncrementalSyncRequest,
  HqProductIncrementalSyncJobRequest,
  HqProductSyncJobResult,
  HqProductSyncJobStatus,
  HqProductSyncResult,
  PosProductDto,
  PosProductFilterParams,
  ProductStoreRecordDto,
  PushProductsToHqStoreOption,
  PushProductsToHqRequest,
  PushProductsToHqResult,
  SyncSelectedProductsFromHqRequest,
  SyncProductsToStoresRequest,
  SyncProductsToStoresJobResult,
  SyncProductsToStoresJobStatus,
  SyncProductsToStoresResult,
} from '../types/posProduct'
import { SUPPLIER_CATEGORY_SOURCES, type SupplierCategorySource } from '../types/localSupplierCategory'
import request, { RequestError, unwrapApiData, unwrapPagedResult } from '../utils/request'
import {
  HqProductSyncPollingTimeoutError,
  createProductHqSyncJobPoller,
  type HqProductSyncPollingOptions,
} from './productHqSyncPolling'

const API_BASE = '/api/react/v1/products'
const SYNC_API_BASE = '/api/react/v1/sync'

const TEXT_FILTER_TYPE_MAP = {
  equals: 0,
  startsWith: 2,
  endsWith: 3,
  contains: 4,
} as const

const NUMBER_FILTER_TYPE_MAP = {
  equals: 0,
  gte: 3,
  lte: 5,
  between: 6,
} as const

export { HqProductSyncPollingTimeoutError, createProductHqSyncJobPoller }
export type { HqProductSyncPollingOptions }

const activeHqProductSyncJobs = new Map<string, Promise<HqProductSyncResult>>()

export type PushProductsToHqJobStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed'

export interface PushProductsToHqJobRequest extends PushProductsToHqRequest {
  operationId: string
}

export interface PushProductsToHqJobResult {
  jobId: string
  status: PushProductsToHqJobStatus
  operationId?: string
  result?: PushProductsToHqResult
  message?: string
  errorCode?: string
  errors?: string[]
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function readFirstString(source: Record<string, unknown>, aliases: string[]): string | undefined {
  for (const alias of aliases) {
    const value = source[alias]
    if (typeof value === 'string' && value) {
      return value
    }
  }
  return undefined
}

// PosProductDto 响应归一化：后端返回 productCategoryGUID/warehouseCategoryGUID 及国内供应商字段，
// 列表与详情复用同一 helper，避免列表/详情字段不一致。
export function normalizePosProductDto(raw: unknown): PosProductDto {
  if (!isRecord(raw)) {
    return raw as PosProductDto
  }

  const source: Record<string, unknown> = { ...raw }
  const categoryGuid = readFirstString(source, [
    'categoryGuid',
    'productCategoryGUID',
    'ProductCategoryGUID',
    'productCategoryGuid',
  ])
  const warehouseCategoryGuid = readFirstString(source, [
    'warehouseCategoryGuid',
    'warehouseCategoryGUID',
    'WarehouseCategoryGUID',
  ])
  const domesticSupplierCode = readFirstString(source, ['domesticSupplierCode', 'DomesticSupplierCode'])
  const domesticSupplierName = readFirstString(source, ['domesticSupplierName', 'DomesticSupplierName'])
  // 供应商分类四字段（新增）：后端 supplierCategoryGUID → supplierCategoryGuid，来源只接受小写白名单值。
  const supplierCategoryGuid = readFirstString(source, [
    'supplierCategoryGuid',
    'supplierCategoryGUID',
    'SupplierCategoryGUID',
  ])
  const supplierCategoryName = readFirstString(source, ['supplierCategoryName', 'SupplierCategoryName'])
  const supplierCategoryPath = readFirstString(source, ['supplierCategoryPath', 'SupplierCategoryPath'])
  const supplierCategorySource = normalizeSupplierCategorySource(
    readFirstString(source, ['supplierCategorySource', 'SupplierCategorySource']),
  )
  for (const alias of [
    'supplierCategoryGUID',
    'SupplierCategoryGUID',
    'supplierCategoryGuid',
    'SupplierCategoryName',
    'supplierCategoryName',
    'SupplierCategoryPath',
    'supplierCategoryPath',
    'SupplierCategorySource',
    'supplierCategorySource',
  ]) {
    delete source[alias]
  }

  delete source.productCategoryGUID
  delete source.ProductCategoryGUID
  delete source.productCategoryGuid
  delete source.warehouseCategoryGUID
  delete source.WarehouseCategoryGUID
  delete source.warehouseCategoryGuid
  delete source.DomesticSupplierCode
  delete source.DomesticSupplierName

  const normalized: Record<string, unknown> = { ...source }
  // 只写有值的归一化字段，避免响应中出现 undefined 键。
  if (categoryGuid !== undefined) normalized.categoryGuid = categoryGuid
  if (warehouseCategoryGuid !== undefined) normalized.warehouseCategoryGuid = warehouseCategoryGuid
  if (domesticSupplierCode !== undefined) normalized.domesticSupplierCode = domesticSupplierCode
  if (domesticSupplierName !== undefined) normalized.domesticSupplierName = domesticSupplierName
  if (supplierCategoryGuid !== undefined) normalized.supplierCategoryGuid = supplierCategoryGuid
  if (supplierCategoryName !== undefined) normalized.supplierCategoryName = supplierCategoryName
  if (supplierCategoryPath !== undefined) normalized.supplierCategoryPath = supplierCategoryPath
  if (supplierCategorySource !== undefined) normalized.supplierCategorySource = supplierCategorySource
  return normalized as unknown as PosProductDto
}

function normalizeSupplierCategorySource(value: string | undefined): SupplierCategorySource | undefined {
  const normalized = value?.trim().toLowerCase()
  return SUPPLIER_CATEGORY_SOURCES.find((source) => source === normalized)
}

function normalizePosProductList(raw: unknown[]): PosProductDto[] {
  return raw.map(normalizePosProductDto)
}

function readColumnFilterToken(value: string | undefined): Record<string, unknown> | undefined {
  if (!value) return undefined
  try {
    const parsed = JSON.parse(value)
    return isRecord(parsed) ? parsed : undefined
  } catch {
    return undefined
  }
}

function getColumnFilterValues(params: PosProductFilterParams, key: string): string[] {
  return params.columnFilters?.[key]?.filter((value) => typeof value === 'string' && value.trim()) ?? []
}

function applyTextColumnFilter(
  payload: Record<string, unknown>,
  params: PosProductFilterParams,
  key: string,
  valueField: string,
  typeField: string,
) {
  const token = readColumnFilterToken(getColumnFilterValues(params, key)[0])
  const value = typeof token?.value === 'string' ? token.value.trim() : ''
  const operator = typeof token?.operator === 'string' ? token.operator : 'contains'
  if (!value) return
  payload[valueField] = value
  payload[typeField] = TEXT_FILTER_TYPE_MAP[operator as keyof typeof TEXT_FILTER_TYPE_MAP] ?? TEXT_FILTER_TYPE_MAP.contains
}

function applyNumberColumnFilter(
  payload: Record<string, unknown>,
  params: PosProductFilterParams,
  key: string,
  minField: string,
  maxField: string,
  typeField?: string,
) {
  const token = readColumnFilterToken(getColumnFilterValues(params, key)[0])
  const operator = typeof token?.operator === 'string' ? token.operator : 'between'
  const value = token?.value !== undefined && token.value !== null && String(token.value).trim()
    ? Number(token.value)
    : undefined
  const min = token?.min !== undefined && token.min !== null && String(token.min).trim()
    ? Number(token.min)
    : undefined
  const max = token?.max !== undefined && token.max !== null && String(token.max).trim()
    ? Number(token.max)
    : undefined

  if (operator === 'equals' || operator === 'gte' || operator === 'lte') {
    if (value === undefined || Number.isNaN(value)) return
    if (typeField) {
      payload[minField] = value
      payload[typeField] = NUMBER_FILTER_TYPE_MAP[operator as keyof typeof NUMBER_FILTER_TYPE_MAP]
      return
    }
    // 无筛选类型字段的数量列只能通过 min/max 表达方向，避免 lte 被误传为最小值。
    if (operator === 'lte') {
      payload[maxField] = value
    } else {
      payload[minField] = value
      if (operator === 'equals') payload[maxField] = value
    }
    return
  }

  if (min !== undefined && !Number.isNaN(min)) payload[minField] = min
  if (max !== undefined && !Number.isNaN(max)) payload[maxField] = max
  if ((min !== undefined || max !== undefined) && typeField) payload[typeField] = NUMBER_FILTER_TYPE_MAP.between
}

function buildDateBoundsFromColumnFilter(params: PosProductFilterParams, key: string) {
  const token = readColumnFilterToken(getColumnFilterValues(params, key)[0])
  const operator = typeof token?.operator === 'string' ? token.operator : 'between'
  const value = typeof token?.value === 'string' ? token.value : ''
  const start = typeof token?.start === 'string' ? token.start : ''
  const end = typeof token?.end === 'string' ? token.end : ''

  const toStartOfDay = (date: string) => date ? `${date}T00:00:00` : undefined
  const toNextDayStart = (date: string) => {
    if (!date) return undefined
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date)
    if (!match) return undefined
    const parsed = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]) + 1))
    return `${parsed.toISOString().slice(0, 10)}T00:00:00`
  }

  if (operator === 'equals') {
    return { from: toStartOfDay(value), toExclusive: toNextDayStart(value) }
  }
  if (operator === 'gte') {
    return { from: toStartOfDay(value), toExclusive: undefined }
  }
  if (operator === 'lte') {
    return { from: undefined, toExclusive: toNextDayStart(value) }
  }
  return { from: toStartOfDay(start), toExclusive: toNextDayStart(end) }
}

function applyDateColumnFilter(
  payload: Record<string, unknown>,
  params: PosProductFilterParams,
  key: string,
  fromField: string,
  toExclusiveField: string,
) {
  const bounds = buildDateBoundsFromColumnFilter(params, key)
  if (bounds.from) payload[fromField] = bounds.from
  if (bounds.toExclusive) payload[toExclusiveField] = bounds.toExclusive
}

function getColumnFilterStrings(params: PosProductFilterParams, key: string): string[] | undefined {
  const values = getColumnFilterValues(params, key).map((value) => String(value).trim()).filter(Boolean)
  return values.length ? values : undefined
}

function assertApiSuccess<T>(response: ApiResponse<T>, fallbackMessage: string): void {
  if (response.success === false || response.isSuccess === false) {
    throw new RequestError(response.message || fallbackMessage, 200, response)
  }
}

function withOperationId<T extends { operationId?: string }>(data: T | undefined, prefix: string): T & { operationId: string } {
  return {
    ...data,
    // 缺省时使用稳定前缀，避免时间戳破坏后端幂等。
    operationId: data?.operationId || prefix,
  } as T & { operationId: string }
}

function pickDefinedHqProductSyncFields(raw: Record<string, unknown>): Partial<HqProductSyncResult> {
  const fields: Partial<HqProductSyncResult> = {}
  if (raw.productsAdded !== undefined || raw.addedCount !== undefined) {
    fields.productsAdded = Number(raw.productsAdded ?? raw.addedCount ?? 0)
  }
  if (raw.productsUpdated !== undefined || raw.updatedCount !== undefined) {
    fields.productsUpdated = Number(raw.productsUpdated ?? raw.updatedCount ?? 0)
  }
  if (raw.productsDeleted !== undefined || raw.productsSoftDeleted !== undefined || raw.deletedCount !== undefined) {
    fields.productsDeleted = Number(raw.productsDeleted ?? raw.productsSoftDeleted ?? raw.deletedCount ?? 0)
  }
  if (raw.productSetCodesCreated !== undefined || raw.productSetCodesAdded !== undefined) {
    fields.productSetCodesCreated = Number(raw.productSetCodesCreated ?? raw.productSetCodesAdded ?? 0)
  }
  if (raw.productSetCodesDeleted !== undefined || raw.productSetCodesSoftDeleted !== undefined) {
    fields.productSetCodesDeleted = Number(raw.productSetCodesDeleted ?? raw.productSetCodesSoftDeleted ?? 0)
  }

  const fieldNames: Array<keyof HqProductSyncResult> = [
    'addedCount',
    'updatedCount',
    'deletedCount',
    'totalCount',
    'errorCount',
    'totalHqProducts',
    'totalLocalProducts',
    'productsAdded',
    'productsUpdated',
    'productsDeleted',
    'productsSoftDeleted',
    'storeRetailPricesCreated',
    'storeRetailPricesDeleted',
    'productSetCodesCreated',
    'productSetCodesAdded',
    'productSetCodesUpdated',
    'productSetCodesDeleted',
    'productSetCodesSoftDeleted',
    'storeMultiCodesCreated',
    'storeMultiCodesDeleted',
    'durationMs',
  ]

  fieldNames.forEach((fieldName) => {
    if (raw[fieldName] !== undefined) {
      ;(fields as Record<string, unknown>)[fieldName] = raw[fieldName]
    }
  })

  return fields
}

function normalizePushProductsToHqResult(raw: PushProductsToHqResult): PushProductsToHqResult {
  // 后端统计字段可能逐步演进，前端统一兜底，保证结果弹窗始终可读。
  const payload = raw as PushProductsToHqResult & Record<string, unknown>
  const relationCount =
    Number(payload.productsAdded ?? 0) +
    Number(payload.productsUpdated ?? 0) +
    Number(payload.warehouseInventoriesCreated ?? 0) +
    Number(payload.warehouseInventoriesUpdated ?? 0) +
    Number(payload.storeRetailPricesCreated ?? 0) +
    Number(payload.storeRetailPricesUpdated ?? 0) +
    Number(payload.productSetCodesCreated ?? payload.productSetCodesAdded ?? 0) +
    Number(payload.productSetCodesUpdated ?? 0) +
    Number(payload.storeMultiCodesCreated ?? 0) +
    Number(payload.storeMultiCodesUpdated ?? 0)
  const successCount = Number(payload.successCount ?? payload.pushedCount ?? payload.productsAdded ?? 0) +
    Number(payload.productsUpdated !== undefined && payload.successCount === undefined && payload.pushedCount === undefined ? payload.productsUpdated : 0)
  const failedCount = Number(payload.failedCount ?? payload.errorCount ?? 0)
  const totalCount = payload.totalCount === undefined ? successCount + failedCount : Number(payload.totalCount)

  return {
    ...raw,
    successCount,
    failedCount,
    totalCount,
    affectedRowCount: Number(payload.affectedRowCount ?? relationCount),
    errors: Array.isArray(raw.errors) ? raw.errors : [],
  }
}

function normalizePushProductsToHqJobStatus(status: unknown, success: unknown, payload: unknown): PushProductsToHqJobStatus {
  if (typeof status === 'string') {
    switch (status.trim().toLowerCase()) {
      case 'queued':
      case 'pending':
        return 'Queued'
      case 'running':
        return 'Running'
      case 'succeeded':
      case 'success':
      case 'completed':
        return 'Succeeded'
      case 'failed':
      case 'failure':
      case 'error':
        return 'Failed'
      default:
        // job 状态不能静默兜底，否则页面会误把未知终态当成仍在执行。
        throw new RequestError(`未知发送 HQ 任务状态: ${status}`, 200, payload)
    }
  }

  if (success === true) {
    return 'Succeeded'
  }

  if (success === false) {
    return 'Failed'
  }

  return 'Running'
}

function normalizePushProductsToHqJobResult(payload: unknown, fallbackJobId = ''): PushProductsToHqJobResult {
  const result = unwrapApiData(payload) as Record<string, unknown> | null
  const raw = isRecord(result) ? result : {}
  const nestedResult = isRecord(raw.result)
    ? normalizePushProductsToHqResult(raw.result as unknown as PushProductsToHqResult)
    : undefined
  const success = typeof raw.success === 'boolean' ? raw.success : nestedResult?.failedCount === 0

  return {
    jobId: typeof raw.jobId === 'string' ? raw.jobId : fallbackJobId,
    status: normalizePushProductsToHqJobStatus(raw.status, success, raw),
    operationId: typeof raw.operationId === 'string' ? raw.operationId : undefined,
    result: nestedResult,
    message: typeof raw.message === 'string' ? raw.message : nestedResult?.message,
    errorCode: readFirstString(raw, ['errorCode', 'ErrorCode', 'code', 'Code']),
    errors: Array.isArray(raw.errors)
      ? raw.errors.filter((item): item is string => typeof item === 'string')
      : [],
  }
}

export async function getProducts(params: PosProductFilterParams, options: { signal?: AbortSignal } = {}) {
  const sortOrderMap: Record<string, string> = { ascend: 'asc', descend: 'desc' }
  // 顶部 categoryGuid/warehouseCategoryGuid 优先于同列头过滤，分别发送 productCategoryGUIDs/warehouseCategoryGUIDs。
  const categoryGuids = params.categoryGuid
    ? [params.categoryGuid]
    : getColumnFilterStrings(params, 'categoryGuid')
  const warehouseCategoryGuids = params.warehouseCategoryGuid
    ? [params.warehouseCategoryGuid]
    : getColumnFilterStrings(params, 'warehouseCategoryGuid')
  const payload: Record<string, unknown> = {
    pageNumber: params.pageIndex,
    pageSize: params.pageSize,
    search: params.keyword || undefined,
    localSupplierCode: params.supplierCode || undefined,
    productCategoryGUIDs: categoryGuids,
    // 契约保留单值 warehouseCategoryGUID，顶部筛选同时发送数组与单值，兼容新旧后端。
    warehouseCategoryGUID: params.warehouseCategoryGuid || undefined,
    warehouseCategoryGUIDs: warehouseCategoryGuids,
    isActive: params.isActive,
    storeRecordCountMin: params.storeRecordCountMin,
    storeRecordCountMax: params.storeRecordCountMax,
    sortBy: params.sortBy || undefined,
    sortOrder: params.sortOrder ? sortOrderMap[params.sortOrder] || params.sortOrder : undefined,
  }

  if (!params.supplierCode) {
    payload.localSupplierCodes = getColumnFilterStrings(params, 'localSupplierCode')
  }
  // 供应商分类筛选：服务端展开子树并按分类所属树（供应商分类/仓库分类）分流；未设置时不写键，旧请求体保持不变。
  if (params.supplierCategoryGuid) {
    payload.supplierCategoryGUIDs = [params.supplierCategoryGuid]
  }
  if (params.supplierCategoryUnassignedOnly) {
    payload.supplierCategoryUnassignedOnly = true
  }
  // 国内供应商列头按契约发送 domesticSupplierCodes，不能映射到本地供应商字段。
  payload.domesticSupplierCodes = getColumnFilterStrings(params, 'domesticSupplierCode')
  if (params.isActive === undefined) {
    payload.isActiveValues = getColumnFilterStrings(params, 'isActive')?.map((value) => value === 'true')
  }
  if (params.isSet !== undefined) {
    payload.productType = params.isSet ? 1 : 0
  } else {
    payload.productTypeValues = getColumnFilterStrings(params, 'productType')?.map((value) => Number(value)).filter((value) => !Number.isNaN(value))
  }
  payload.isAutoPricingValues = getColumnFilterStrings(params, 'isAutoPricing')?.map((value) => value === 'true')

  applyTextColumnFilter(payload, params, 'itemNumber', 'itemNumber', 'itemNumberFilterType')
  applyTextColumnFilter(payload, params, 'barcode', 'barcode', 'barcodeFilterType')
  applyTextColumnFilter(payload, params, 'productName', 'productName', 'productNameFilterType')
  applyTextColumnFilter(payload, params, 'productCode', 'productCode', 'productCodeFilterType')
  applyNumberColumnFilter(payload, params, 'purchasePrice', 'purchasePriceMin', 'purchasePriceMax', 'purchasePriceFilterType')
  applyNumberColumnFilter(payload, params, 'retailPrice', 'retailPriceMin', 'retailPriceMax', 'retailPriceFilterType')
  if (params.storeRecordCountMin === undefined && params.storeRecordCountMax === undefined) {
    applyNumberColumnFilter(payload, params, 'storeRecordCount', 'storeRecordCountMin', 'storeRecordCountMax')
  }
  applyDateColumnFilter(payload, params, 'createdAt', 'createdAtFrom', 'createdAtToExclusive')
  applyDateColumnFilter(payload, params, 'updatedAt', 'updatedAtFrom', 'updatedAtToExclusive')

  const response = await request.post<ApiResponse<PagedResult<PosProductDto>> | PagedResult<PosProductDto> | PosProductDto[]>(
    `${API_BASE}/list`,
    payload,
    // 页面快速切换筛选时用于取消已过期的列表请求。
    { signal: options.signal },
  )

  if (Array.isArray(response)) {
    return { items: normalizePosProductList(response), total: response.length }
  }

  if (isRecord(response) && Array.isArray(response.data)) {
    return {
      items: normalizePosProductList(response.data),
      total: Number(response.total ?? response.totalCount ?? response.data.length),
    }
  }

  const unwrapped = unwrapApiData(response)
  if (Array.isArray(unwrapped)) {
    return { items: normalizePosProductList(unwrapped), total: unwrapped.length }
  }

  const paged = unwrapPagedResult(unwrapped as PagedResult<PosProductDto>)
  return { ...paged, items: normalizePosProductList(paged.items) }
}

export { getProducts as getPosProducts }

export async function getProductById(productCode: string): Promise<PosProductDto> {
  const response = await request.get<ApiResponse<PosProductDto>>(`${API_BASE}/${productCode}`)
  return normalizePosProductDto(unwrapApiData(response))
}

export async function getProductByBarcode(barcode: string): Promise<PosProductDto> {
  const response = await request.get<ApiResponse<PosProductDto>>(`${API_BASE}/by-barcode/${barcode}`)
  return normalizePosProductDto(unwrapApiData(response))
}

export async function getProductStoreRecords(productCode: string): Promise<ProductStoreRecordDto[]> {
  const response = await request.get<ApiResponse<ProductStoreRecordDto[]>>(
    `${API_BASE}/${encodeURIComponent(productCode)}/store-records`,
  )
  return unwrapApiData(response) ?? []
}

function toProductMutationPayload(data: Partial<PosProductDto>) {
  const { categoryGuid, warehouseCategoryGuid, ...payload } = data
  return {
    ...payload,
    productCategoryGUID: categoryGuid,
    warehouseCategoryGUID: warehouseCategoryGuid,
  }
}

export async function createProduct(data: Partial<PosProductDto> & Record<string, unknown>) {
  const response = await request.post<ApiResponse<PosProductDto>>(`${API_BASE}`, toProductMutationPayload(data))
  return normalizePosProductDto(unwrapApiData(response))
}

export async function createProductWithPrices(data: CreateProductWithPricesDto): Promise<CreateProductWithPricesResultDto> {
  const response = await request.post<ApiResponse<CreateProductWithPricesResultDto>>(`${API_BASE}/create-with-prices`, data)
  assertApiSuccess(response, '创建商品失败')
  const result = unwrapApiData(response)
  // 内嵌 product 同样返回 PosProductDto，复用同一 helper 避免与列表字段不一致。
  return {
    ...result,
    product: result.product ? normalizePosProductDto(result.product) : result.product,
  }
}

export async function updateProduct(productCode: string, data: Partial<PosProductDto>) {
  const response = await request.put<ApiResponse<PosProductDto>>(`${API_BASE}/${productCode}`, toProductMutationPayload(data))
  return normalizePosProductDto(unwrapApiData(response))
}

export async function batchUpdateProducts(items: BatchUpdatePosProductDto[]) {
  const response = await request.post<ApiResponse<{ successCount: number; failedCount: number; errors: string[] }>>(
    `${API_BASE}/batch-update`,
    { items },
  )
  return unwrapApiData(response)
}

function normalizeSupplierImageBatchUpdateResult(raw: unknown): BatchUpdateSupplierImagesResult | undefined {
  if (!isRecord(raw)) {
    return undefined
  }

  return {
    totalCount: Number(raw.totalCount ?? 0),
    hbwebUpdatedCount: Number(raw.hbwebUpdatedCount ?? 0),
    hqUpdatedCount: Number(raw.hqUpdatedCount ?? 0),
    hbwebSkippedExistingImageCount: Number(raw.hbwebSkippedExistingImageCount ?? 0),
    hqSkippedExistingImageCount: Number(raw.hqSkippedExistingImageCount ?? 0),
    skippedCount: Number(raw.skippedCount ?? 0),
    hqFailedCount: Number(raw.hqFailedCount ?? 0),
    errors: Array.isArray(raw.errors) ? raw.errors.filter((item): item is string => typeof item === 'string') : [],
    message: typeof raw.message === 'string' ? raw.message : undefined,
  }
}

function normalizeSyncProductsToStoresResult(raw: unknown): SyncProductsToStoresResult | undefined {
  if (!isRecord(raw)) {
    return undefined
  }

  const errors = Array.isArray(raw.errors) ? raw.errors.filter((item): item is string => typeof item === 'string') : []
  const createdCount = Number(raw.createdCount ?? raw.successCount ?? 0)
  const updatedCount = Number(raw.updatedCount ?? 0)
  const failedCount = Number(raw.failedCount ?? raw.errorCount ?? errors.length)

  return {
    createdCount,
    updatedCount,
    failedCount,
    // 兼容仍返回 successCount 的旧 payload，便于页面按真实统计展示结果。
    successCount: Number(raw.successCount ?? createdCount + updatedCount),
    errors,
    message: typeof raw.message === 'string' ? raw.message : undefined,
  }
}

function normalizeSyncProductsToStoresJobStatus(status: unknown, success: unknown): SyncProductsToStoresJobStatus {
  if (typeof status === 'string') {
    switch (status.trim().toLowerCase()) {
      case 'queued':
      case 'pending':
        return 'Queued'
      case 'running':
      case 'processing':
      case 'inprogress':
      case 'in-progress':
        return 'Running'
      case 'succeeded':
      case 'success':
      case 'completed':
        return 'Succeeded'
      case 'failed':
      case 'failure':
      case 'error':
        return 'Failed'
      default:
        // 同步到分店 job 允许保留后端新增状态，页面会继续按非终态处理。
        return status.trim()
    }
  }

  if (success === true) {
    return 'Succeeded'
  }

  if (success === false) {
    return 'Failed'
  }

  return 'Running'
}

function normalizeSyncProductsToStoresJobResult(
  payload: unknown,
  fallbackJobId = '',
): SyncProductsToStoresJobResult {
  const result = unwrapApiData(payload) as Record<string, unknown> | null
  const raw = isRecord(result) ? result : {}
  const nestedResult = normalizeSyncProductsToStoresResult(raw.result)
  const topLevelResult = normalizeSyncProductsToStoresResult(raw)
  const mergedErrors = Array.isArray(raw.errors)
    ? raw.errors.filter((item): item is string => typeof item === 'string')
    : nestedResult?.errors ?? topLevelResult?.errors ?? []
  const normalizedResult = nestedResult ?? topLevelResult ?? {
    createdCount: 0,
    updatedCount: 0,
    failedCount: mergedErrors.length ? mergedErrors.length : 0,
    successCount: 0,
    errors: mergedErrors,
    message: typeof raw.message === 'string' ? raw.message : undefined,
  }

  if (!normalizedResult.errors.length && mergedErrors.length) {
    normalizedResult.errors = mergedErrors
  }

  const success = typeof raw.success === 'boolean' ? raw.success : raw.isSuccess

  return {
    jobId: typeof raw.jobId === 'string' ? raw.jobId : fallbackJobId,
    status: normalizeSyncProductsToStoresJobStatus(raw.status, success),
    operationId: typeof raw.operationId === 'string' ? raw.operationId : undefined,
    result: normalizedResult,
    message:
      typeof raw.message === 'string'
        ? raw.message
        : normalizedResult.message,
    isDuplicateRequest: typeof raw.isDuplicateRequest === 'boolean' ? raw.isDuplicateRequest : undefined,
    errors: mergedErrors,
  }
}

function normalizeSupplierImageBatchUpdateJobResult(
  payload: unknown,
  fallbackJobId = '',
): BatchUpdateSupplierImagesJobResult {
  const result = unwrapApiData(payload) as Record<string, unknown> | null
  const raw = isRecord(result) ? result : {}
  const nestedResult = normalizeSupplierImageBatchUpdateResult(raw.result)
  const success = typeof raw.success === 'boolean' ? raw.success : undefined

  return {
    jobId: typeof raw.jobId === 'string' ? raw.jobId : fallbackJobId,
    operationId: typeof raw.operationId === 'string' ? raw.operationId : undefined,
    status: normalizeHqProductSyncJobStatus(raw.status, success, raw),
    request: isRecord(raw.request) ? raw.request : undefined,
    result: nestedResult,
    message: typeof raw.message === 'string' ? raw.message : undefined,
    errorMessage: typeof raw.errorMessage === 'string' ? raw.errorMessage : undefined,
    errors: Array.isArray(raw.errors)
      ? raw.errors.filter((item): item is string => typeof item === 'string')
      : nestedResult?.errors ?? [],
    createdAt: typeof raw.createdAt === 'string' ? raw.createdAt : undefined,
    startedAt: typeof raw.startedAt === 'string' ? raw.startedAt : undefined,
    completedAt: typeof raw.completedAt === 'string' ? raw.completedAt : undefined,
  }
}

export function buildSupplierImageBatchUpdateOperationId(data: BatchUpdateSupplierImagesRequest) {
  const targets = [
    data.updateHbweb ? 'hbweb' : '',
    data.updateHq ? 'hq' : '',
    data.saveSupplierImageBaseUrl ? 'save-url' : '',
  ].filter(Boolean).join('+') || 'none'
  const productScope = data.productCodes?.length ? `selected:${data.productCodes.join(',')}` : 'supplier-all'

  return `supplier-image:${data.localSupplierCode}:${targets}:${productScope}:${data.urlTemplate}`
}

export async function createSupplierImageBatchUpdateJob(
  data: BatchUpdateSupplierImagesJobRequest,
): Promise<BatchUpdateSupplierImagesJobResult> {
  const response = await request.post<ApiResponse<unknown>>(
    `${API_BASE}/batch-update-supplier-images/job`,
    withOperationId(data, buildSupplierImageBatchUpdateOperationId(data)),
  )
  assertApiSuccess(response, '创建供应商图片批量修改任务失败')
  return normalizeSupplierImageBatchUpdateJobResult(response)
}

export async function getSupplierImageBatchUpdateJob(jobId: string): Promise<BatchUpdateSupplierImagesJobResult> {
  const response = await request.get<ApiResponse<unknown>>(
    `${API_BASE}/batch-update-supplier-images/job/${encodeURIComponent(jobId)}`,
  )
  assertApiSuccess(response, '查询供应商图片批量修改任务失败')
  return normalizeSupplierImageBatchUpdateJobResult(response, jobId)
}

export async function syncProductsToStores(syncRequest: SyncProductsToStoresRequest): Promise<SyncProductsToStoresResult> {
  const response = await request.post<ApiResponse<SyncProductsToStoresResult>>(
    `${API_BASE}/sync-to-stores`,
    syncRequest,
  )
  return unwrapApiData(response)
}

export async function startSyncProductsToStoresJob(
  syncRequest: SyncProductsToStoresRequest,
): Promise<SyncProductsToStoresJobResult> {
  const response = await request.post<ApiResponse<unknown>>(
    `${API_BASE}/sync-to-stores/jobs`,
    syncRequest,
  )
  assertApiSuccess(response, '创建同步到分店任务失败')
  return normalizeSyncProductsToStoresJobResult(response)
}

export async function getSyncProductsToStoresJob(jobId: string): Promise<SyncProductsToStoresJobResult> {
  const response = await request.get<ApiResponse<unknown>>(
    `${API_BASE}/sync-to-stores/jobs/${encodeURIComponent(jobId)}`,
  )
  assertApiSuccess(response, '查询同步到分店任务失败')
  return normalizeSyncProductsToStoresJobResult(response, jobId)
}

export async function batchUpdateProductStoreRecords(
  productCode: string,
  data: BatchUpdateProductStoreRecordsRequest,
): Promise<BatchUpdateProductStoreRecordsResult> {
  const response = await request.post<ApiResponse<BatchUpdateProductStoreRecordsResult>>(
    // 商品编码可能包含空格或斜杠，这里必须编码后再拼路径，避免路由误拆。
    `${API_BASE}/${encodeURIComponent(productCode)}/store-records/batch-update`,
    data,
  )
  return unwrapApiData(response)
}

export async function pushProductsToHq(data: PushProductsToHqRequest): Promise<PushProductsToHqResult> {
  const response = await request.post<ApiResponse<PushProductsToHqResult>>(
    `${API_BASE}/push-to-hq`,
    data,
  )
  assertApiSuccess(response, '发送商品到 HQ 失败')
  return normalizePushProductsToHqResult(unwrapApiData(response))
}

// 发送到 HQ 弹窗每次打开都读取最新 HQ 分店选项，接口按现有 ApiResponse 包裹返回数组。
export async function getPushToHqStoreOptions(): Promise<PushProductsToHqStoreOption[]> {
  const response = await request.get<ApiResponse<PushProductsToHqStoreOption[]>>(
    `${API_BASE}/push-to-hq/store-options`,
  )
  return normalizePushToHqStoreOptions(unwrapApiData(response))
}

export function buildPushProductsToHqOperationId(
  containerGuid: string,
  productCodes: string[],
  itemCount: number,
  updateFields: PushProductsToHqRequest['updateFields'] = [],
) {
  const stableCodes = productCodes.map((item) => item.trim()).filter(Boolean).sort().join(',')
  const stableFields = updateFields.map((item) => item.trim()).filter(Boolean).sort().join(',')
  return `container-push-hq:${containerGuid || 'unknown'}:${stableCodes || 'items'}:${itemCount}:${stableFields || 'all'}`
}

export async function createPushProductsToHqJob(data: PushProductsToHqJobRequest): Promise<PushProductsToHqJobResult> {
  const response = await request.post<ApiResponse<unknown>>(
    `${API_BASE}/push-to-hq/jobs`,
    data,
  )
  assertApiSuccess(response, '创建发送商品到 HQ 任务失败')
  return normalizePushProductsToHqJobResult(response)
}

export async function getPushProductsToHqJob(jobId: string): Promise<PushProductsToHqJobResult> {
  const response = await request.get<ApiResponse<unknown>>(
    `${API_BASE}/push-to-hq/jobs/${encodeURIComponent(jobId)}`,
  )
  assertApiSuccess(response, '查询发送商品到 HQ 任务失败')
  return normalizePushProductsToHqJobResult(response, jobId)
}

function normalizeHqProductSyncResult(raw: HqProductSyncResult): HqProductSyncResult {
  const productsDeleted = raw.productsDeleted ?? raw.productsSoftDeleted ?? raw.deletedCount ?? 0
  const productSetCodesDeleted = raw.productSetCodesDeleted ?? raw.productSetCodesSoftDeleted ?? 0
  const productSetCodesCreated = raw.productSetCodesCreated ?? raw.productSetCodesAdded ?? 0

  return {
    ...raw,
    productsAdded: raw.productsAdded ?? raw.addedCount ?? 0,
    productsUpdated: raw.productsUpdated ?? raw.updatedCount ?? 0,
    productsDeleted,
    productSetCodesCreated,
    productSetCodesDeleted,
    errors: raw.errors ?? [],
    durationMs: raw.durationMs ?? 0,
  }
}

function normalizeHqProductSyncJobStatus(
  status: unknown,
  success: unknown,
  payload: unknown,
): HqProductSyncJobStatus {
  if (typeof status === 'string') {
    switch (status.trim().toLowerCase()) {
      case 'queued':
      case 'pending':
        return 'Queued'
      case 'running':
        return 'Running'
      case 'succeeded':
      case 'success':
      case 'completed':
        return 'Succeeded'
      case 'failed':
      case 'failure':
      case 'error':
        return 'Failed'
      default:
        // 未知状态必须显式暴露，避免前端误判为仍在执行中。
        throw new RequestError(`未知同步任务状态: ${status}`, 200, payload)
    }
  }

  if (success === true) {
    return 'Succeeded'
  }

  if (success === false) {
    return 'Failed'
  }

  return 'Running'
}

function normalizeHqProductSyncJobResult(
  payload: unknown,
  fallbackJobId = '',
): HqProductSyncJobResult {
  const result = unwrapApiData(payload) as Record<string, unknown> | null
  const raw = isRecord(result) ? result : {}
  const nestedResult = isRecord(raw.result) ? raw.result : {}
  const success = typeof raw.success === 'boolean' ? raw.success : nestedResult.success
  const normalizedRawResult = pickDefinedHqProductSyncFields(raw)
  const normalizedNestedResult = isRecord(raw.result)
    ? normalizeHqProductSyncResult(nestedResult as HqProductSyncResult)
    : undefined

  return {
    ...normalizedRawResult,
    jobId: typeof raw.jobId === 'string' ? raw.jobId : fallbackJobId,
    status: normalizeHqProductSyncJobStatus(raw.status, success, raw),
    mode:
      raw.mode === 'Full' || raw.mode === 'Incremental'
        ? raw.mode
        : nestedResult.mode === 'Full' || nestedResult.mode === 'Incremental'
          ? nestedResult.mode
          : undefined,
    operationId: typeof raw.operationId === 'string' ? raw.operationId : undefined,
    success: typeof success === 'boolean' ? success : undefined,
    startDate: typeof raw.startDate === 'string' ? raw.startDate : undefined,
    result: normalizedNestedResult,
    message:
      typeof raw.message === 'string'
        ? raw.message
        : typeof nestedResult.message === 'string'
          ? nestedResult.message
          : undefined,
    errors: Array.isArray(raw.errors)
      ? raw.errors.filter((item): item is string => typeof item === 'string')
      : [],
  }
}

function buildHqProductSyncResult(job: HqProductSyncJobResult): HqProductSyncResult {
  if (job.result) {
    return normalizeHqProductSyncResult(job.result)
  }

  return normalizeHqProductSyncResult(job)
}

function resolveHqProductSyncJobResult(job: HqProductSyncJobResult): HqProductSyncResult {
  if (job.status === 'Succeeded') {
    return buildHqProductSyncResult(job)
  }

  if (job.status === 'Failed') {
    throw new RequestError(job.message || '商品同步任务执行失败', 200, job)
  }

  return buildHqProductSyncResult(job)
}

function normalizeUnknownStatusError(error: unknown): never {
  if (error instanceof RequestError && error.message.startsWith('未知同步任务状态')) {
    const status = error.message.split(':').slice(1).join(':').trim()
    throw new RequestError(`未知商品同步任务状态：${status}`, error.status, error.payload)
  }

  throw error
}

export function buildProductHqSyncOperationId(mode: 'full' | 'incremental', startDate?: string) {
  return `product-hq-sync:${mode}:${startDate || 'all'}`
}

export async function syncProductsFromHqFull(
  options?: HqProductSyncPollingOptions,
): Promise<HqProductSyncResult> {
  const operationId = buildProductHqSyncOperationId('full')
  const activeJob = activeHqProductSyncJobs.get(operationId)
  if (activeJob) {
    return activeJob
  }

  const jobPromise = (async () => {
    try {
      const job = await createProductHqSyncFullJob({ operationId })
      if (job.status === 'Queued' || job.status === 'Running') {
        const poller = createProductHqSyncJobPoller({
          jobId: job.jobId,
          getJob: getProductHqSyncJob,
          ...options,
        })
        return resolveHqProductSyncJobResult(await poller.promise)
      }
      return resolveHqProductSyncJobResult(job)
    } catch (error) {
      normalizeUnknownStatusError(error)
    } finally {
      activeHqProductSyncJobs.delete(operationId)
    }
  })()

  activeHqProductSyncJobs.set(operationId, jobPromise)
  return jobPromise
}

export async function syncProductsFromHqIncremental(
  data: HqProductIncrementalSyncRequest = {},
  options?: HqProductSyncPollingOptions,
): Promise<HqProductSyncResult> {
  const operationId = buildProductHqSyncOperationId('incremental', data.startDate)
  const activeJob = activeHqProductSyncJobs.get(operationId)
  if (activeJob) {
    return activeJob
  }

  const jobPromise = (async () => {
    try {
      const job = await createProductHqSyncIncrementalJob({ ...data, operationId })
      if (job.status === 'Queued' || job.status === 'Running') {
        const poller = createProductHqSyncJobPoller({
          jobId: job.jobId,
          getJob: getProductHqSyncJob,
          ...options,
        })
        return resolveHqProductSyncJobResult(await poller.promise)
      }
      return resolveHqProductSyncJobResult(job)
    } catch (error) {
      normalizeUnknownStatusError(error)
    } finally {
      activeHqProductSyncJobs.delete(operationId)
    }
  })()

  activeHqProductSyncJobs.set(operationId, jobPromise)
  return jobPromise
}

export async function syncProductsFromHq(): Promise<HqProductSyncResult> {
  const response = await request.post<ApiResponse<HqProductSyncResult>>(`${API_BASE}/sync-from-hq`)
  assertApiSuccess(response, 'HQ 商品同步失败')
  return normalizeHqProductSyncResult(unwrapApiData(response))
}

export async function syncSelectedProductsFromHq(
  data: SyncSelectedProductsFromHqRequest,
): Promise<HqProductSyncResult> {
  const response = await request.post<ApiResponse<HqProductSyncResult>>(
    `${API_BASE}/sync-selected-from-hq`,
    data,
  )
  assertApiSuccess(response, '选中商品 HQ 同步失败')
  return normalizeHqProductSyncResult(unwrapApiData(response))
}

export async function createProductHqSyncFullJob(
  data: HqProductFullSyncJobRequest,
): Promise<HqProductSyncJobResult> {
  const response = await request.post<ApiResponse<unknown>>(
    `${SYNC_API_BASE}/products/jobs`,
    withOperationId(data, buildProductHqSyncOperationId('full')),
  )
  assertApiSuccess(response, '创建商品 HQ 全量同步任务失败')
  return normalizeHqProductSyncJobResult(response)
}

export async function createProductHqSyncIncrementalJob(
  data: HqProductIncrementalSyncJobRequest,
): Promise<HqProductSyncJobResult> {
  const response = await request.post<ApiResponse<unknown>>(
    `${SYNC_API_BASE}/products-incremental/jobs`,
    withOperationId(data, buildProductHqSyncOperationId('incremental', data.startDate)),
  )
  assertApiSuccess(response, '创建商品 HQ 增量同步任务失败')
  return normalizeHqProductSyncJobResult(response)
}

export async function getProductHqSyncJob(jobId: string): Promise<HqProductSyncJobResult> {
  const response = await request.get<ApiResponse<unknown>>(`${SYNC_API_BASE}/products/jobs/${encodeURIComponent(jobId)}`)
  assertApiSuccess(response, '查询商品 HQ 同步任务失败')
  return normalizeHqProductSyncJobResult(response, jobId)
}

export const createProductFullHqSyncJob = createProductHqSyncFullJob
export const createProductIncrementalHqSyncJob = createProductHqSyncIncrementalJob
export const createHqProductFullSyncJob = createProductHqSyncFullJob
export const createHqProductIncrementalSyncJob = createProductHqSyncIncrementalJob
export const getHqProductSyncJob = getProductHqSyncJob
