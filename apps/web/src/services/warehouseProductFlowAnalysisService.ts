import type {
  WarehouseProductFlowBranch,
  WarehouseProductFlowBranchRequest,
  WarehouseProductFlowCandidate,
  WarehouseProductFlowCandidateRequest,
  WarehouseProductFlowCandidatesData,
  WarehouseProductFlowContainerRow,
  WarehouseProductFlowDaily,
  WarehouseProductFlowEnvelope,
  WarehouseProductFlowFilter,
  WarehouseProductFlowItemNumberMatch,
  WarehouseProductFlowMetrics,
  WarehouseProductFlowOptions,
  WarehouseProductFlowOrderRow,
  WarehouseProductFlowProduct,
  WarehouseProductFlowProductRequest,
  WarehouseProductFlowShipmentRow,
  WarehouseProductFlowSummaryData,
  WarehouseProductFlowSummaryRequest,
} from '../types/warehouseProductFlowAnalysis'
import request from '../utils/request'

const API_BASE = '/api/react/v1/dashboard/warehouse-product-flow-analysis'
type UnknownRecord = Record<string, unknown>

function asRecord(value: unknown): UnknownRecord | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as UnknownRecord : null
}

function pick(record: UnknownRecord, camel: string, pascal: string): unknown {
  return record[camel] ?? record[pascal]
}

function readString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value : undefined
}

function readNumber(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function readNullableNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function normalizeDate(value: unknown): string | undefined {
  const date = readString(value)
  return date && /^\d{4}-\d{2}-\d{2}/.test(date) ? date.slice(0, 10) : undefined
}

function normalizeMetrics(raw: unknown): WarehouseProductFlowMetrics {
  const record = asRecord(raw) ?? {}
  return {
    inboundQuantity: readNumber(pick(record, 'inboundQuantity', 'InboundQuantity')),
    orderedQuantity: readNumber(pick(record, 'orderedQuantity', 'OrderedQuantity')),
    shippedQuantity: readNumber(pick(record, 'shippedQuantity', 'ShippedQuantity')),
    netSalesQuantity: readNumber(pick(record, 'netSalesQuantity', 'NetSalesQuantity')),
    netSalesAmount: readNumber(pick(record, 'netSalesAmount', 'NetSalesAmount')),
    averageUnitPrice: readNullableNumber(pick(record, 'averageUnitPrice', 'AverageUnitPrice')),
  }
}

function normalizeCandidate(raw: unknown): WarehouseProductFlowCandidate | null {
  const record = asRecord(raw)
  const productCode = record && readString(pick(record, 'productCode', 'ProductCode'))
  if (!record || !productCode) return null
  return {
    productCode,
    itemNumber: readString(pick(record, 'itemNumber', 'ItemNumber')),
    productName: readString(pick(record, 'productName', 'ProductName')),
    englishName: readString(pick(record, 'englishName', 'EnglishName')),
    barcode: readString(pick(record, 'barcode', 'Barcode')),
    imageUrl: readString(pick(record, 'imageUrl', 'ImageUrl')),
    supplierCode: readString(pick(record, 'supplierCode', 'SupplierCode')),
    supplierName: readString(pick(record, 'supplierName', 'SupplierName')),
    categoryName: readString(pick(record, 'categoryName', 'CategoryName')),
  }
}

function normalizeProduct(raw: unknown): WarehouseProductFlowProduct | null {
  const candidate = normalizeCandidate(raw)
  if (!candidate) return null
  const record = asRecord(raw) ?? {}
  return { ...candidate, metrics: normalizeMetrics(pick(record, 'metrics', 'Metrics') ?? record) }
}

function normalizeItemNumberMatch(raw: unknown): WarehouseProductFlowItemNumberMatch | undefined {
  const record = asRecord(raw)
  if (!record) return undefined
  const unmatched = pick(record, 'unmatchedItemNumbers', 'UnmatchedItemNumbers')
  return {
    requestedCount: readNumber(pick(record, 'requestedCount', 'RequestedCount')),
    matchedCount: readNumber(pick(record, 'matchedCount', 'MatchedCount')),
    unmatchedItemNumbers: Array.isArray(unmatched) ? unmatched.filter((value): value is string => typeof value === 'string' && !!value.trim()) : [],
  }
}

function normalizePagedProducts(raw: unknown): WarehouseProductFlowCandidatesData {
  const record = asRecord(raw) ?? {}
  const items = pick(record, 'items', 'Items')
  const itemNumberMatch = normalizeItemNumberMatch(pick(record, 'itemNumberMatch', 'ItemNumberMatch'))
  return {
    items: Array.isArray(items) ? items.map(normalizeCandidate).filter((item): item is WarehouseProductFlowCandidate => !!item) : [],
    total: readNumber(pick(record, 'total', 'Total')),
    pageNumber: readNumber(pick(record, 'pageNumber', 'PageNumber'), 1),
    pageSize: readNumber(pick(record, 'pageSize', 'PageSize'), 20),
    ...(itemNumberMatch ? { itemNumberMatch } : {}),
  }
}

function normalizeSummary(raw: unknown): WarehouseProductFlowSummaryData {
  const record = asRecord(raw) ?? {}
  const items = pick(record, 'items', 'Items')
  return {
    items: Array.isArray(items) ? items.map(normalizeProduct).filter((item): item is WarehouseProductFlowProduct => !!item) : [],
    total: readNumber(pick(record, 'total', 'Total')),
    pageNumber: readNumber(pick(record, 'pageNumber', 'PageNumber'), 1),
    pageSize: readNumber(pick(record, 'pageSize', 'PageSize'), 20),
    totals: normalizeMetrics(pick(record, 'totals', 'Totals')),
    currentProduct: normalizeProduct(pick(record, 'currentProduct', 'CurrentProduct')),
  }
}

function normalizeDaily(raw: unknown): WarehouseProductFlowDaily | null {
  const record = asRecord(raw)
  const date = record && normalizeDate(pick(record, 'date', 'Date'))
  if (!record || !date) return null
  const metrics = normalizeMetrics(record)
  return { date, inboundQuantity: metrics.inboundQuantity, orderedQuantity: metrics.orderedQuantity, shippedQuantity: metrics.shippedQuantity, netSalesQuantity: metrics.netSalesQuantity, netSalesAmount: metrics.netSalesAmount, averageUnitPrice: metrics.averageUnitPrice }
}

function normalizeBranch(raw: unknown): WarehouseProductFlowBranch | null {
  const record = asRecord(raw)
  const branchCode = record && readString(pick(record, 'branchCode', 'BranchCode'))
  if (!record || !branchCode) return null
  return {
    branchCode,
    branchName: readString(pick(record, 'branchName', 'BranchName')),
    orderedQuantity: readNumber(pick(record, 'orderedQuantity', 'OrderedQuantity')),
    shippedQuantity: readNumber(pick(record, 'shippedQuantity', 'ShippedQuantity')),
    netSalesQuantity: readNumber(pick(record, 'netSalesQuantity', 'NetSalesQuantity')),
    netSalesAmount: readNumber(pick(record, 'netSalesAmount', 'NetSalesAmount')),
    sellThroughRate: readNullableNumber(pick(record, 'sellThroughRate', 'SellThroughRate')),
    averageUnitPrice: readNullableNumber(pick(record, 'averageUnitPrice', 'AverageUnitPrice')),
  }
}

function normalizeContainer(raw: unknown): WarehouseProductFlowContainerRow | null {
  const record = asRecord(raw)
  const containerNumber = record && readString(pick(record, 'containerNumber', 'ContainerNumber'))
  if (!record || !containerNumber) return null
  return {
    containerNumber,
    arrivalDate: normalizeDate(pick(record, 'arrivalDate', 'ArrivalDate')),
    inboundQuantity: readNumber(pick(record, 'inboundQuantity', 'InboundQuantity')),
    inboundUnitPrice: readNullableNumber(pick(record, 'inboundUnitPrice', 'InboundUnitPrice')),
    supplierName: readString(pick(record, 'supplierName', 'SupplierName')),
  }
}

function normalizeOrder(raw: unknown): WarehouseProductFlowOrderRow | null {
  const record = asRecord(raw); const orderNumber = record && readString(pick(record, 'orderNumber', 'OrderNumber'))
  return !record || !orderNumber ? null : { orderNumber, branchName: readString(pick(record, 'branchName', 'BranchName')), orderDate: normalizeDate(pick(record, 'orderDate', 'OrderDate')), orderedQuantity: readNumber(pick(record, 'orderedQuantity', 'OrderedQuantity')) }
}

function normalizeShipment(raw: unknown): WarehouseProductFlowShipmentRow | null {
  const record = asRecord(raw); if (!record) return null
  const shipmentNumber = readString(pick(record, 'shipmentNumber', 'ShipmentNumber'))
  const orderNumber = readString(pick(record, 'orderNumber', 'OrderNumber'))
  if (!shipmentNumber && !orderNumber) return null
  const posEnabled = pick(record, 'posEnabled', 'PosEnabled')
  return {
    shipmentNumber,
    orderNumber,
    branchCode: readString(pick(record, 'branchCode', 'BranchCode')),
    branchName: readString(pick(record, 'branchName', 'BranchName')),
    posEnabled: typeof posEnabled === 'boolean' ? posEnabled : null,
    shipmentDate: normalizeDate(pick(record, 'shipmentDate', 'ShipmentDate')),
    shippedQuantity: readNumber(pick(record, 'shippedQuantity', 'ShippedQuantity')),
    // 兼容旧响应时保留未知值，避免把未提供的销量显示为零。
    netSalesQuantity: readNullableNumber(pick(record, 'netSalesQuantity', 'NetSalesQuantity')),
  }
}

function normalizeOptions(raw: unknown): WarehouseProductFlowOptions {
  const record = asRecord(raw) ?? {}; const values = pick(record, 'domesticSuppliers', 'DomesticSuppliers')
  return { domesticSuppliers: Array.isArray(values) ? values.flatMap((value) => { const item = asRecord(value); const code = item && readString(pick(item, 'code', 'Code')); return code ? [{ code, name: readString(pick(item!, 'name', 'Name')) }] : [] }) : [] }
}

function unwrap<T>(raw: unknown, normalize: (value: unknown) => T): WarehouseProductFlowEnvelope<T> {
  const response = asRecord(raw)
  if (!response) throw new Error('响应格式非法')
  const success = pick(response, 'success', 'Success') ?? pick(response, 'isSuccess', 'IsSuccess')
  if (success === false) throw new Error(readString(pick(response, 'message', 'Message')) ?? '请求失败')
  return { data: normalize(pick(response, 'data', 'Data') ?? response) }
}

function serializeFilter(filter: WarehouseProductFlowFilter) {
  return {
    keyword: filter.keyword,
    warehouseCategoryGuids: filter.warehouseCategoryGuids,
    supplierCodes: filter.supplierCodes,
    documentKeyword: filter.documentKeyword,
    // 批量货号可达 2000 个，放在 POST 请求体里，不进 URL。
    itemNumbers: filter.itemNumbers?.length ? filter.itemNumbers : undefined,
    itemNumberMatchMode: filter.itemNumbers?.length ? filter.itemNumberMatchMode ?? 'itemNumber' : undefined,
  }
}

function serializeRequest<T extends { filter: WarehouseProductFlowFilter }>(payload: T) {
  return { ...payload, filter: serializeFilter(payload.filter) }
}

async function post<T>(path: string, body: unknown, normalize: (value: unknown) => T, signal?: AbortSignal) {
  const response = await request.post<unknown>(`${API_BASE}${path}`, body, { signal })
  return unwrap(response, normalize)
}

export function queryWarehouseProductFlowSummary(requestBody: WarehouseProductFlowSummaryRequest, signal?: AbortSignal) {
  return post('/summary', serializeRequest(requestBody), normalizeSummary, signal)
}

export function getWarehouseProductFlowOptions(_filter?: WarehouseProductFlowFilter, signal?: AbortSignal, forceRefresh = false) {
  // options 与商品主档筛选无关；保留入参兼容旧调用，但不把筛选透传到 GET。
  const query = forceRefresh ? '?forceRefresh=true' : ''
  return request(`${API_BASE}/options${query}`, { method: 'GET', signal }).then((raw) => unwrap(raw, normalizeOptions))
}

export function queryWarehouseProductFlowCandidates(requestBody: WarehouseProductFlowCandidateRequest, signal?: AbortSignal) {
  return post('/candidates', { ...requestBody, filter: serializeFilter(requestBody.filter) }, normalizePagedProducts, signal)
}

export function queryWarehouseProductFlowContainers(requestBody: WarehouseProductFlowProductRequest, signal?: AbortSignal) {
  return post('/containers', serializeRequest(requestBody), (raw) => Array.isArray(raw) ? raw.map(normalizeContainer).filter((item): item is WarehouseProductFlowContainerRow => !!item) : [], signal)
}

export function queryWarehouseProductFlowOrders(requestBody: WarehouseProductFlowProductRequest, signal?: AbortSignal) {
  return post('/orders', serializeRequest(requestBody), (raw) => Array.isArray(raw) ? raw.map(normalizeOrder).filter((item): item is WarehouseProductFlowOrderRow => !!item) : [], signal)
}

export function queryWarehouseProductFlowShipments(requestBody: WarehouseProductFlowProductRequest, signal?: AbortSignal) {
  return post('/shipments', serializeRequest(requestBody), (raw) => Array.isArray(raw) ? raw.map(normalizeShipment).filter((item): item is WarehouseProductFlowShipmentRow => !!item) : [], signal)
}

export function queryWarehouseProductFlowDaily(requestBody: WarehouseProductFlowProductRequest, signal?: AbortSignal) {
  return post('/product-daily', serializeRequest(requestBody), (raw) => Array.isArray(raw) ? raw.map(normalizeDaily).filter((item): item is WarehouseProductFlowDaily => !!item) : [], signal)
}

export function queryWarehouseProductFlowOrderShipmentDaily(requestBody: WarehouseProductFlowProductRequest, signal?: AbortSignal) {
  return post('/order-shipment-daily', serializeRequest(requestBody), (raw) => Array.isArray(raw) ? raw.map(normalizeDaily).filter((item): item is WarehouseProductFlowDaily => !!item) : [], signal)
}

export function queryWarehouseProductFlowSalesDaily(requestBody: WarehouseProductFlowProductRequest, signal?: AbortSignal) {
  return post('/sales-daily', serializeRequest(requestBody), (raw) => Array.isArray(raw) ? raw.map(normalizeDaily).filter((item): item is WarehouseProductFlowDaily => !!item) : [], signal)
}

export function queryWarehouseProductFlowBranches(requestBody: WarehouseProductFlowBranchRequest, signal?: AbortSignal) {
  return post('/branches', serializeRequest(requestBody), (raw) => Array.isArray(raw) ? raw.map(normalizeBranch).filter((item): item is WarehouseProductFlowBranch => !!item) : [], signal)
}

export function queryWarehouseProductFlowBranchDaily(requestBody: Required<Pick<WarehouseProductFlowBranchRequest, 'branchCode'>> & WarehouseProductFlowProductRequest, signal?: AbortSignal) {
  return post('/branch-daily', serializeRequest(requestBody), (raw) => Array.isArray(raw) ? raw.map(normalizeDaily).filter((item): item is WarehouseProductFlowDaily => !!item) : [], signal)
}
