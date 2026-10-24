import type { ApiResponse } from '../types/api'
import type {
  BatchProductSalesApi,
  BatchSalesBranch,
  BatchSalesBranchOverview,
  BatchSalesBranchOverviewRequest,
  BatchSalesCoverage,
  BatchSalesDaily,
  BatchSalesDetail,
  BatchSalesDetailRequest,
  BatchSalesMatch,
  BatchSalesMetrics,
  BatchSalesOptions,
  BatchSalesOverview,
  BatchSalesOverviewBranch,
  BatchSalesDiscountOverview,
  BatchSalesLockedScope,
  BatchSalesProduct,
  BatchSalesProductSummary,
  BatchSalesPurchaseRequest,
  BatchSalesPurchaseResult,
  BatchSalesQuery,
  BatchSalesQueryResult,
  BatchSalesScope,
} from '../types/batchProductSalesAnalysis'
import request, { unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/dashboard/batch-product-sales-analysis'
const DATE_ONLY_PATTERN = /^(\d{4})-(\d{2})-(\d{2})/
type UnknownRecord = Record<string, unknown>

function asRecord(value: unknown, label: string): UnknownRecord {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label}格式非法`)
  }
  return value as UnknownRecord
}

function pick(record: UnknownRecord, camelKey: string, pascalKey: string): unknown {
  return record[camelKey] !== undefined ? record[camelKey] : record[pascalKey]
}

function requiredString(value: unknown, label: string): string {
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error(`缺少或非法${label}`)
  }
  return value
}

function optionalString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value : undefined
}

function requiredNumber(value: unknown, label: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new Error(`缺少或非法${label}`)
  }
  return value
}

function requiredNullableNumber(value: unknown, label: string): number | null {
  // Program.cs 的 WhenWritingNull 会省略 null 价格字段；缺失与显式 null 都表示服务端没有可用价格范围。
  if (value === null || value === undefined) {
    return null
  }
  return requiredNumber(value, label)
}

function requiredStringArray(value: unknown, label: string): string[] {
  if (!Array.isArray(value)) {
    throw new Error(`缺少或非法${label}`)
  }
  return value.map((entry, index) => requiredString(entry, `${label}[${index}]`))
}

function requiredArray(value: unknown, label: string): unknown[] {
  if (!Array.isArray(value)) {
    throw new Error(`缺少或非法${label}`)
  }
  return value
}

function normalizeDate(value: unknown, label: string): string {
  const raw = requiredString(value, label)
  const match = DATE_ONLY_PATTERN.exec(raw)
  if (!match) {
    throw new Error(`缺少或非法${label}`)
  }
  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  const parsed = new Date(Date.UTC(year, month - 1, day))
  if (
    parsed.getUTCFullYear() !== year
    || parsed.getUTCMonth() !== month - 1
    || parsed.getUTCDate() !== day
  ) {
    throw new Error(`缺少或非法${label}`)
  }
  return `${match[1]}-${match[2]}-${match[3]}`
}

function normalizeEnum<T extends string>(value: unknown, label: string, values: readonly T[]): T {
  const raw = requiredString(value, label).trim().toLowerCase()
  const match = values.find((candidate) => candidate.toLowerCase() === raw)
  if (!match) {
    throw new Error(`缺少或非法${label}`)
  }
  return match
}

function validateScope(scope: BatchSalesScope): void {
  const start = normalizeDate(scope.startDate, '开始日期')
  const end = normalizeDate(scope.endDate, '结束日期')
  if (start > end) {
    throw new Error('开始日期不能晚于结束日期')
  }
  requiredStringArray(scope.storeCodes, '门店编码')
}

function normalizeProduct(raw: unknown): BatchSalesProduct {
  const record = asRecord(raw, '商品')
  return {
    productCode: requiredString(pick(record, 'productCode', 'ProductCode'), '商品编码'),
    itemNumber: requiredString(pick(record, 'itemNumber', 'ItemNumber'), '货号'),
    productName: requiredString(pick(record, 'productName', 'ProductName'), '商品名称'),
    englishName: optionalString(pick(record, 'englishName', 'EnglishName')),
    barcode: optionalString(pick(record, 'barcode', 'Barcode')),
    imageUrl: optionalString(pick(record, 'imageUrl', 'ImageUrl')),
  }
}

function assertMetricsConservation(metrics: BatchSalesMetrics): void {
  // 三类数量已经扣除对应退货；returnQuantity 仅用于展示，不能再次计入守恒式。
  const classifiedQuantity = metrics.regularQuantity
    + metrics.discountQuantity
    + metrics.unknownQuantity
  const scale = Math.max(1, Math.abs(metrics.quantity), Math.abs(classifiedQuantity))
  if (Math.abs(metrics.quantity - classifiedQuantity) > scale * 1e-9) {
    throw new Error('销量数量与分类数量不一致')
  }
}

function normalizeMetrics(raw: unknown): BatchSalesMetrics {
  const record = asRecord(raw, '销量指标')
  const metrics: BatchSalesMetrics = {
    quantity: requiredNumber(pick(record, 'quantity', 'Quantity'), '销量'),
    regularQuantity: requiredNumber(pick(record, 'regularQuantity', 'RegularQuantity'), '正价销量'),
    discountQuantity: requiredNumber(pick(record, 'discountQuantity', 'DiscountQuantity'), '折扣销量'),
    unknownQuantity: requiredNumber(pick(record, 'unknownQuantity', 'UnknownQuantity'), '未知销量'),
    returnQuantity: requiredNumber(pick(record, 'returnQuantity', 'ReturnQuantity'), '退货数量'),
    salesAmount: requiredNumber(pick(record, 'salesAmount', 'SalesAmount'), '销售额'),
    discountStatus: normalizeEnum(
      pick(record, 'discountStatus', 'DiscountStatus'),
      '折扣状态',
      ['complete', 'partial', 'unknown', 'pending'],
    ),
    originalPriceMin: requiredNullableNumber(pick(record, 'originalPriceMin', 'OriginalPriceMin'), '原价最小值'),
    originalPriceMax: requiredNullableNumber(pick(record, 'originalPriceMax', 'OriginalPriceMax'), '原价最大值'),
    discountPriceMin: requiredNullableNumber(pick(record, 'discountPriceMin', 'DiscountPriceMin'), '折扣价最小值'),
    discountPriceMax: requiredNullableNumber(pick(record, 'discountPriceMax', 'DiscountPriceMax'), '折扣价最大值'),
  }
  if (
    metrics.originalPriceMin !== null
    && metrics.originalPriceMax !== null
    && metrics.originalPriceMin > metrics.originalPriceMax
  ) {
    throw new Error('原价区间非法')
  }
  if (
    metrics.discountPriceMin !== null
    && metrics.discountPriceMax !== null
    && metrics.discountPriceMin > metrics.discountPriceMax
  ) {
    throw new Error('折扣价区间非法')
  }
  assertMetricsConservation(metrics)
  return metrics
}

function normalizeMatch(raw: unknown): BatchSalesMatch {
  const record = asRecord(raw, '匹配结果')
  return {
    itemNumber: requiredString(pick(record, 'itemNumber', 'ItemNumber'), '匹配货号'),
    status: normalizeEnum(
      pick(record, 'status', 'Status'),
      '匹配状态',
      ['matched', 'ambiguous', 'notFound'],
    ),
    productCodes: requiredStringArray(pick(record, 'productCodes', 'ProductCodes'), '匹配商品编码'),
  }
}

function normalizeProductSummary(raw: unknown): BatchSalesProductSummary {
  const record = asRecord(raw, '商品汇总')
  return {
    ...normalizeProduct(record),
    quantity: requiredNullableNumber(pick(record, 'quantity', 'Quantity'), '商品汇总销量'),
    salesAmount: requiredNullableNumber(pick(record, 'salesAmount', 'SalesAmount'), '商品汇总销售额'),
  }
}

function normalizeCoverage(raw: unknown): BatchSalesCoverage {
  const record = asRecord(raw, '日期覆盖范围')
  const readyDates = requiredStringArray(pick(record, 'readyDates', 'ReadyDates'), '已完成日期').map((value, index) => normalizeDate(value, `已完成日期[${index}]`))
  const pendingDates = requiredArray(pick(record, 'pendingDates', 'PendingDates'), '待完成日期').map((entry, index) => {
    const pending = asRecord(entry, `待完成日期[${index}]`)
    return {
      date: normalizeDate(pick(pending, 'date', 'Date'), `待完成日期[${index}].date`),
      reason: requiredString(pick(pending, 'reason', 'Reason'), `待完成日期[${index}].reason`),
    }
  })
  const status = normalizeEnum(pick(record, 'status', 'Status'), '日期覆盖状态', ['complete', 'partial', 'pending'])
  const version = requiredString(pick(record, 'version', 'Version'), '日期覆盖版本')
  const known = new Set<string>()
  for (const date of [...readyDates, ...pendingDates.map((item) => item.date)]) {
    if (known.has(date)) throw new Error('日期覆盖范围包含重复日期')
    known.add(date)
  }
  if ((status === 'complete') !== (pendingDates.length === 0) || (status === 'pending') !== (readyDates.length === 0)) {
    throw new Error('日期覆盖状态与日期集合不一致')
  }
  return { status, readyDates, pendingDates, version }
}

function normalizeDaily(raw: unknown): BatchSalesDaily {
  const record = asRecord(raw, '每日销量')
  return {
    date: normalizeDate(pick(record, 'date', 'Date'), '每日日期'),
    metrics: normalizeMetrics(pick(record, 'metrics', 'Metrics')),
  }
}

function normalizeBranch(raw: unknown): BatchSalesBranch {
  const record = asRecord(raw, '分店销量')
  return {
    branchCode: requiredString(pick(record, 'branchCode', 'BranchCode'), '分店编码'),
    branchName: requiredString(pick(record, 'branchName', 'BranchName'), '分店名称'),
    metrics: normalizeMetrics(pick(record, 'metrics', 'Metrics')),
    daily: requiredArray(pick(record, 'daily', 'Daily'), '分店每日销量').map(normalizeDaily),
  }
}

function normalizeOverviewBranch(raw: unknown): BatchSalesOverviewBranch {
  const record = asRecord(raw, '总览分店销量')
  return { ...normalizeBranch(record), contributingProductCount: requiredNumber(pick(record, 'contributingProductCount', 'ContributingProductCount'), '贡献商品数') }
}

function normalizeOverview(raw: unknown): BatchSalesOverview {
  const record = asRecord(raw, '总览')
  const rawMetrics = pick(record, 'metrics', 'Metrics')
  return {
    metrics: rawMetrics === null || rawMetrics === undefined ? null : normalizeMetrics(rawMetrics),
    daily: requiredArray(pick(record, 'daily', 'Daily'), '总览每日销量').map(normalizeDaily),
    branches: requiredArray(pick(record, 'branches', 'Branches'), '总览分店销量').map(normalizeOverviewBranch),
  }
}

function normalizeOptions(raw: unknown): BatchSalesOptions {
  const record = asRecord(raw, '选项响应')
  return {
    stores: requiredArray(pick(record, 'stores', 'Stores'), '门店选项').map((entry) => {
      const store = asRecord(entry, '门店选项')
      return {
        code: requiredString(pick(store, 'code', 'Code'), '门店编码'),
        name: requiredString(pick(store, 'name', 'Name'), '门店名称'),
      }
    }),
    maxItemNumbers: requiredNumber(pick(record, 'maxItemNumbers', 'MaxItemNumbers'), '最大货号数'),
    maxDays: requiredNumber(pick(record, 'maxDays', 'MaxDays'), '最大天数'),
  }
}

function normalizeResponseScope(record: UnknownRecord): BatchSalesScope {
  const scope = {
    startDate: normalizeDate(pick(record, 'startDate', 'StartDate'), '开始日期'),
    endDate: normalizeDate(pick(record, 'endDate', 'EndDate'), '结束日期'),
    storeCodes: requiredStringArray(pick(record, 'storeCodes', 'StoreCodes'), '门店编码'),
  }
  validateScope(scope)
  return scope
}

function normalizeQueryResult(raw: unknown): BatchSalesQueryResult {
  const record = asRecord(raw, '查询响应')
  return {
    // 明细和导出沿用服务端实际有效范围；页面单独保留尚未提交的筛选草稿。
    ...normalizeResponseScope(record),
    matches: requiredArray(pick(record, 'matches', 'Matches'), '匹配结果').map(normalizeMatch),
    products: requiredArray(pick(record, 'products', 'Products'), '商品汇总').map(normalizeProductSummary),
    warnings: requiredStringArray(pick(record, 'warnings', 'Warnings'), '警告'),
    statisticStatus: optionalString(pick(record, 'statisticStatus', 'StatisticStatus')),
    statisticUpdatedAt: optionalString(pick(record, 'statisticUpdatedAt', 'StatisticUpdatedAt')),
    discountStatisticStatus: optionalString(pick(record, 'discountStatisticStatus', 'DiscountStatisticStatus')),
    discountUpdatedAt: optionalString(pick(record, 'discountUpdatedAt', 'DiscountUpdatedAt')),
    coverage: normalizeCoverage(pick(record, 'coverage', 'Coverage')),
    overview: normalizeOverview(pick(record, 'overview', 'Overview')),
  }
}

function normalizeLockedScope(input: BatchSalesLockedScope): void {
  validateScope(input)
  requiredStringArray(input.productCodes, '商品编码')
  requiredString(input.coverageVersion, '日期覆盖版本')
  input.readyDates.forEach((date, index) => normalizeDate(date, `已完成日期[${index}]`))
}

function normalizeBranchOverview(raw: unknown): BatchSalesBranchOverview {
  const record = asRecord(raw, '分店总览响应')
  return { ...normalizeResponseScope(record), productCodes: requiredStringArray(pick(record, 'productCodes', 'ProductCodes'), '商品编码'), coverage: normalizeCoverage(pick(record, 'coverage', 'Coverage')), branch: normalizeBranch(pick(record, 'branch', 'Branch')), products: requiredArray(pick(record, 'products', 'Products'), '商品贡献').map((entry) => {
    const product = asRecord(entry, '商品贡献')
    return { ...normalizeProduct(product), metrics: normalizeMetrics(pick(product, 'metrics', 'Metrics')) }
  }) }
}

function normalizeDiscountOverview(raw: unknown): BatchSalesDiscountOverview {
  const record = asRecord(raw, '折扣总览响应')
  const products = pick(record, 'products', 'Products')
  const branch = pick(record, 'branch', 'Branch')
  return { ...normalizeResponseScope(record), productCodes: requiredStringArray(pick(record, 'productCodes', 'ProductCodes'), '商品编码'), coverage: normalizeCoverage(pick(record, 'coverage', 'Coverage')), overview: normalizeOverview(pick(record, 'overview', 'Overview')), branch: branch === null || branch === undefined ? undefined : normalizeBranch(branch), products: products === null || products === undefined ? undefined : requiredArray(products, '商品贡献').map((entry) => { const product = asRecord(entry, '商品贡献'); return { ...normalizeProduct(product), metrics: normalizeMetrics(pick(product, 'metrics', 'Metrics')) } }), discountStatisticStatus: optionalString(pick(record, 'discountStatisticStatus', 'DiscountStatisticStatus')), discountUpdatedAt: optionalString(pick(record, 'discountUpdatedAt', 'DiscountUpdatedAt')), warnings: requiredStringArray(pick(record, 'warnings', 'Warnings'), '警告') }
}

function normalizePurchaseResult(raw: unknown): BatchSalesPurchaseResult {
  const record = asRecord(raw, '进货量响应')
  return {
    ...normalizeResponseScope(record),
    productCodes: requiredStringArray(pick(record, 'productCodes', 'ProductCodes'), '商品编码'),
    rows: requiredArray(pick(record, 'rows', 'Rows'), '进货量').map((entry, index) => {
      const row = asRecord(entry, `进货量[${index}]`)
      return {
        productCode: requiredString(pick(row, 'productCode', 'ProductCode'), `进货量[${index}].商品编码`),
        branchCode: requiredString(pick(row, 'branchCode', 'BranchCode'), `进货量[${index}].分店编码`),
        warehouseQuantity: requiredNumber(pick(row, 'warehouseQuantity', 'WarehouseQuantity'), `进货量[${index}].总仓配货`),
        supplierQuantity: requiredNumber(pick(row, 'supplierQuantity', 'SupplierQuantity'), `进货量[${index}].供应商进货`),
      }
    }),
  }
}

function normalizeDetail(raw: unknown): BatchSalesDetail {
  const record = asRecord(raw, '详情响应')
  return {
    ...normalizeResponseScope(record),
    productCodes: requiredStringArray(pick(record, 'productCodes', 'ProductCodes'), '商品编码'),
    statisticStatus: optionalString(pick(record, 'statisticStatus', 'StatisticStatus')),
    statisticUpdatedAt: optionalString(pick(record, 'statisticUpdatedAt', 'StatisticUpdatedAt')),
    discountStatisticStatus: optionalString(pick(record, 'discountStatisticStatus', 'DiscountStatisticStatus')),
    discountUpdatedAt: optionalString(pick(record, 'discountUpdatedAt', 'DiscountUpdatedAt')),
    product: normalizeProduct(pick(record, 'product', 'Product')),
    metrics: normalizeMetrics(pick(record, 'metrics', 'Metrics')),
    daily: requiredArray(pick(record, 'daily', 'Daily'), '每日销量').map(normalizeDaily),
    branches: requiredArray(pick(record, 'branches', 'Branches'), '分店销量').map(normalizeBranch),
    warnings: requiredStringArray(pick(record, 'warnings', 'Warnings'), '警告'),
    coverage: normalizeCoverage(pick(record, 'coverage', 'Coverage')),
  }
}

function unwrapPayload(raw: unknown): unknown {
  const record = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw as UnknownRecord : undefined
  // request 的标准解包器只识别 camelCase；这里先把最外层 PascalCase 信封映射成同一形状，随后只解包一次。
  const envelope = record && ('Success' in record || 'IsSuccess' in record || 'Data' in record)
    ? {
      success: record.Success,
      isSuccess: record.IsSuccess,
      message: record.Message,
      code: record.Code,
      errorCode: record.ErrorCode,
      data: record.Data,
    }
    : raw
  const data = unwrapApiData<unknown>(envelope as ApiResponse<unknown> | unknown)
  if (data === undefined || data === null) {
    throw new Error('响应缺少数据')
  }
  return data
}

async function getOptions(signal?: AbortSignal): Promise<BatchSalesOptions> {
  const response = await request.get<ApiResponse<unknown> | unknown>(`${API_BASE}/options`, { signal })
  return normalizeOptions(unwrapPayload(response))
}

async function query(input: BatchSalesQuery, signal?: AbortSignal): Promise<BatchSalesQueryResult> {
  validateScope(input)
  requiredStringArray(input.itemNumbers, '货号')
  const response = await request.post<ApiResponse<unknown> | unknown>(`${API_BASE}/query`, input, { signal })
  return normalizeQueryResult(unwrapPayload(response))
}

async function getDetail(input: BatchSalesDetailRequest, signal?: AbortSignal): Promise<BatchSalesDetail> {
  validateScope(input)
  requiredString(input.productCode, '商品编码')
  if (input.includeDiscounts !== undefined && typeof input.includeDiscounts !== 'boolean') throw new Error('是否包含折扣分类必须为布尔值')
  if ((input.coverageVersion === undefined) !== (input.readyDates === undefined)) throw new Error('日期覆盖版本与已完成日期必须同时提供')
  if (input.coverageVersion !== undefined) {
    requiredString(input.coverageVersion, '日期覆盖版本')
    input.readyDates?.forEach((date, index) => normalizeDate(date, `已完成日期[${index}]`))
  }
  const response = await request.post<ApiResponse<unknown> | unknown>(`${API_BASE}/detail`, input, { signal })
  return normalizeDetail(unwrapPayload(response))
}

async function getBranchOverview(input: BatchSalesBranchOverviewRequest, signal?: AbortSignal): Promise<BatchSalesBranchOverview> {
  normalizeLockedScope(input)
  requiredString(input.branchCode, '分店编码')
  const response = await request.post<ApiResponse<unknown> | unknown>(`${API_BASE}/overview/branch`, input, { signal })
  return normalizeBranchOverview(unwrapPayload(response))
}

async function getDiscounts(input: BatchSalesLockedScope & { branchCode?: string }, signal?: AbortSignal): Promise<BatchSalesDiscountOverview> {
  normalizeLockedScope(input)
  const response = await request.post<ApiResponse<unknown> | unknown>(`${API_BASE}/overview/discounts`, input, { signal })
  return normalizeDiscountOverview(unwrapPayload(response))
}

async function exportDetail(input: BatchSalesLockedScope, signal?: AbortSignal): Promise<string> {
  normalizeLockedScope(input)
  return request.post<string>(`${API_BASE}/export/detail`, input, { signal })
}

async function getPurchases(input: BatchSalesPurchaseRequest, signal?: AbortSignal): Promise<BatchSalesPurchaseResult> {
  validateScope(input)
  if (!requiredStringArray(input.productCodes, '商品编码').length) throw new Error('缺少商品编码')
  const response = await request.post<ApiResponse<unknown> | unknown>(`${API_BASE}/purchases`, input, { signal })
  return normalizePurchaseResult(unwrapPayload(response))
}

export const batchProductSalesApi: BatchProductSalesApi = { getOptions, query, getDetail, getBranchOverview, getDiscounts, exportDetail, getPurchases }
