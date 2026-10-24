import type { BatchSalesBranch, BatchSalesCoverage, BatchSalesDaily, BatchSalesDetail, BatchSalesDiscountOverview, BatchSalesLockedScope, BatchSalesMetrics, BatchSalesProduct, BatchSalesPurchaseRow, BatchSalesScope } from '../../../types/batchProductSalesAnalysis'

const DATE_ONLY_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/

function nonZeroFinite(value: unknown): boolean {
  return typeof value === 'number' && Number.isFinite(value) && value !== 0
}

export interface BatchProductSalesAnalysisFilters {
  productCode?: string
  branchCode?: string
}

export interface BatchProductSalesContribution {
  product: BatchSalesProduct
  metrics: BatchSalesMetrics
}

export interface BatchProductSalesAnalysis {
  /** 当前商品/分店组合的汇总；只使用实际已返回的明细。 */
  metrics: BatchSalesMetrics
  daily: BatchSalesDaily[]
  /** 当前商品范围内的分店总量排行，始终保留全部分店以便切换。 */
  branches: BatchSalesBranch[]
  /** 当前分店内、当前商品范围的商品贡献。 */
  productContributions: BatchProductSalesContribution[]
}

/** 可靠单品详情与折扣分类是两次独立读取；后者绝不能覆盖前者的数量或金额。 */
export function mergeBatchProductSalesDetailClassifications(reliable: BatchSalesDetail, discounts: BatchSalesDiscountOverview): BatchSalesDetail {
  const merge = (reliableMetrics: BatchSalesMetrics, classified: BatchSalesMetrics): BatchSalesMetrics => ({ ...classified, quantity: reliableMetrics.quantity, salesAmount: reliableMetrics.salesAmount })
  if (!discounts.overview.metrics) return { ...reliable, discountStatisticStatus: discounts.discountStatisticStatus, discountUpdatedAt: discounts.discountUpdatedAt, warnings: [...new Set([...reliable.warnings, ...discounts.warnings])] }
  const days = new Map(discounts.overview.daily.map((day) => [day.date, day]))
  const branches = new Map(discounts.overview.branches.map((branch) => [branch.branchCode, branch]))
  return {
    ...reliable,
    metrics: merge(reliable.metrics, discounts.overview.metrics),
    daily: reliable.daily.map((day) => days.has(day.date) ? { ...days.get(day.date)!, metrics: merge(day.metrics, days.get(day.date)!.metrics) } : day),
    branches: reliable.branches.map((branch) => {
      const classified = branches.get(branch.branchCode)
      if (!classified) return branch
      const branchDays = new Map(classified.daily.map((day) => [day.date, day]))
      return { ...classified, metrics: merge(branch.metrics, classified.metrics), daily: branch.daily.map((day) => branchDays.has(day.date) ? { ...branchDays.get(day.date)!, metrics: merge(day.metrics, branchDays.get(day.date)!.metrics) } : day) }
    }),
    discountStatisticStatus: discounts.discountStatisticStatus,
    discountUpdatedAt: discounts.discountUpdatedAt,
    warnings: [...new Set([...reliable.warnings, ...discounts.warnings])],
  }
}

/** 详情 CSV 跟随当前商品视图；分店仅是页面钻取，不能缩小授权过的门店范围。 */
export function buildBatchProductSalesDetailExportScope(
  scope: BatchSalesScope,
  coverage: BatchSalesCoverage,
  productCodes: readonly string[],
  selectedProductCode?: string,
): BatchSalesLockedScope {
  return {
    ...scope,
    productCodes: selectedProductCode ? [selectedProductCode] : [...productCodes],
    coverageVersion: coverage.version,
    readyDates: [...coverage.readyDates],
  }
}

/** 以固定并发处理任务，避免大批量货号同时打满明细接口。 */
export async function runBatchProductSalesPool<T>(items: readonly T[], concurrency: number, worker: (item: T) => Promise<void>): Promise<void> {
  if (!items.length) return
  let nextIndex = 0
  const runWorker = async () => {
    while (nextIndex < items.length) {
      const item = items[nextIndex++]
      await worker(item)
    }
  }
  const workerCount = Math.min(Math.max(1, Math.floor(concurrency)), items.length)
  await Promise.all(Array.from({ length: workerCount }, runWorker))
}

function emptyMetrics(): BatchSalesMetrics {
  return {
    quantity: 0,
    regularQuantity: 0,
    discountQuantity: 0,
    unknownQuantity: 0,
    returnQuantity: 0,
    salesAmount: 0,
    discountStatus: 'complete',
    originalPriceMin: null,
    originalPriceMax: null,
    discountPriceMin: null,
    discountPriceMax: null,
  }
}

function minimum(values: readonly (number | null)[]): number | null {
  const present = values.filter((value): value is number => value !== null)
  return present.length ? Math.min(...present) : null
}

function maximum(values: readonly (number | null)[]): number | null {
  const present = values.filter((value): value is number => value !== null)
  return present.length ? Math.max(...present) : null
}

/**
 * 聚合只针对已返回的服务端明细；调用方必须把失败商品单独展示，不能以零值替代。
 * 任何尚未完成或未知的折扣分类都向上冒泡，使视图继续显示破折号而非伪造分类值。
 */
export function aggregateBatchProductSalesMetrics(rows: readonly BatchSalesMetrics[]): BatchSalesMetrics {
  if (!rows.length) return emptyMetrics()
  const statuses = new Set(rows.map((row) => row.discountStatus))
  const discountStatus = statuses.has('pending') ? 'pending' : statuses.has('unknown') ? 'unknown' : statuses.has('partial') ? 'partial' : 'complete'
  return {
    quantity: rows.reduce((sum, row) => sum + row.quantity, 0),
    regularQuantity: rows.reduce((sum, row) => sum + row.regularQuantity, 0),
    discountQuantity: rows.reduce((sum, row) => sum + row.discountQuantity, 0),
    unknownQuantity: rows.reduce((sum, row) => sum + row.unknownQuantity, 0),
    returnQuantity: rows.reduce((sum, row) => sum + row.returnQuantity, 0),
    salesAmount: rows.reduce((sum, row) => sum + row.salesAmount, 0),
    discountStatus,
    originalPriceMin: minimum(rows.map((row) => row.originalPriceMin)),
    originalPriceMax: maximum(rows.map((row) => row.originalPriceMax)),
    discountPriceMin: minimum(rows.map((row) => row.discountPriceMin)),
    discountPriceMax: maximum(rows.map((row) => row.discountPriceMax)),
  }
}

export function aggregateBatchProductSalesDaily(rows: readonly BatchSalesDaily[]): BatchSalesDaily[] {
  const grouped = new Map<string, BatchSalesMetrics[]>()
  rows.forEach((row) => {
    const current = grouped.get(row.date)
    if (current) current.push(row.metrics)
    else grouped.set(row.date, [row.metrics])
  })
  return [...grouped.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([date, metrics]) => ({
      date,
      metrics: aggregateBatchProductSalesMetrics(metrics),
    }))
}

/**
 * 统一产出全部/单商品、全部/单分店的图表和排行模型。
 * `details` 仅传入成功返回的商品，缺失商品不会被创建成零销量行。
 */
export function buildBatchProductSalesAnalysis(details: readonly BatchSalesDetail[], filters: BatchProductSalesAnalysisFilters = {}): BatchProductSalesAnalysis {
  const scopedProducts = filters.productCode ? details.filter((detail) => detail.product.productCode === filters.productCode) : [...details]
  const branchGroups = new Map<
    string,
    {
      branchName: string
      metrics: BatchSalesMetrics[]
      daily: BatchSalesDaily[]
    }
  >()
  scopedProducts.forEach((detail) =>
    detail.branches.forEach((branch) => {
      const current = branchGroups.get(branch.branchCode)
      if (current) {
        current.metrics.push(branch.metrics)
        current.daily.push(...branch.daily)
      } else {
        branchGroups.set(branch.branchCode, {
          branchName: branch.branchName,
          metrics: [branch.metrics],
          daily: [...branch.daily],
        })
      }
    }),
  )
  const branches = sortBatchProductSalesBranchesByQuantity(
    [...branchGroups.entries()].map(([branchCode, branch]) => ({
      branchCode,
      branchName: branch.branchName,
      metrics: aggregateBatchProductSalesMetrics(branch.metrics),
      daily: aggregateBatchProductSalesDaily(branch.daily),
    })),
  )
  const selectedBranches = filters.branchCode ? scopedProducts.map((detail) => detail.branches.find((branch) => branch.branchCode === filters.branchCode)).filter((branch): branch is BatchSalesBranch => !!branch) : []
  const productContributions = scopedProducts
    .map((detail) => {
      const branch = filters.branchCode ? detail.branches.find((item) => item.branchCode === filters.branchCode) : undefined
      return {
        product: detail.product,
        metrics: branch?.metrics ?? detail.metrics,
        hasSelectedBranch: !!branch,
      }
    })
    .filter((row) => !filters.branchCode || row.hasSelectedBranch)
    .map(({ product, metrics }) => ({ product, metrics }))
    .sort((left, right) => right.metrics.quantity - left.metrics.quantity)
  return {
    metrics: filters.branchCode ? aggregateBatchProductSalesMetrics(selectedBranches.map((branch) => branch.metrics)) : aggregateBatchProductSalesMetrics(scopedProducts.map((detail) => detail.metrics)),
    daily: filters.branchCode ? aggregateBatchProductSalesDaily(selectedBranches.flatMap((branch) => branch.daily)) : aggregateBatchProductSalesDaily(scopedProducts.flatMap((detail) => detail.daily)),
    branches,
    productContributions,
  }
}

export interface BatchProductSalesPurchaseQuantity {
  /** 总仓配货 + 供应商进货单。 */
  total: number
  warehouse: number
  supplier: number
}

/** 进货量按商品、分店、商品×分店预先汇总，页面切换商品或分店时只做查表。 */
export interface BatchProductSalesPurchaseIndex {
  total: BatchProductSalesPurchaseQuantity
  byProduct: Map<string, BatchProductSalesPurchaseQuantity>
  byBranch: Map<string, BatchProductSalesPurchaseQuantity>
  byProductBranch: Map<string, BatchProductSalesPurchaseQuantity>
  /** 每个商品有进货的分店，用于在单品分店排行里补上「有进货、无销量」的分店。 */
  branchesByProduct: Map<string, string[]>
}

const ZERO_PURCHASE: BatchProductSalesPurchaseQuantity = { total: 0, warehouse: 0, supplier: 0 }

function purchaseKey(productCode: string, branchCode: string) {
  return `${productCode}\u0001${branchCode}`
}

function addPurchase(map: Map<string, BatchProductSalesPurchaseQuantity>, key: string, warehouse: number, supplier: number) {
  const current = map.get(key) ?? ZERO_PURCHASE
  map.set(key, { total: current.total + warehouse + supplier, warehouse: current.warehouse + warehouse, supplier: current.supplier + supplier })
}

export function buildBatchProductSalesPurchaseIndex(rows: readonly BatchSalesPurchaseRow[]): BatchProductSalesPurchaseIndex {
  const byProduct = new Map<string, BatchProductSalesPurchaseQuantity>()
  const byBranch = new Map<string, BatchProductSalesPurchaseQuantity>()
  const byProductBranch = new Map<string, BatchProductSalesPurchaseQuantity>()
  const branchesByProduct = new Map<string, string[]>()
  let warehouse = 0
  let supplier = 0
  rows.forEach((row) => {
    addPurchase(byProduct, row.productCode, row.warehouseQuantity, row.supplierQuantity)
    addPurchase(byBranch, row.branchCode, row.warehouseQuantity, row.supplierQuantity)
    const key = purchaseKey(row.productCode, row.branchCode)
    if (!byProductBranch.has(key)) branchesByProduct.set(row.productCode, [...(branchesByProduct.get(row.productCode) ?? []), row.branchCode])
    addPurchase(byProductBranch, key, row.warehouseQuantity, row.supplierQuantity)
    warehouse += row.warehouseQuantity
    supplier += row.supplierQuantity
  })
  return { total: { total: warehouse + supplier, warehouse, supplier }, byProduct, byBranch, byProductBranch, branchesByProduct }
}

/** 服务端只返回非零组合，查不到即真实 0；商品/分店未指定表示「全部」。 */
export function getBatchProductSalesPurchase(index: BatchProductSalesPurchaseIndex, filters: BatchProductSalesAnalysisFilters = {}): BatchProductSalesPurchaseQuantity {
  if (filters.productCode && filters.branchCode) return index.byProductBranch.get(purchaseKey(filters.productCode, filters.branchCode)) ?? ZERO_PURCHASE
  if (filters.productCode) return index.byProduct.get(filters.productCode) ?? ZERO_PURCHASE
  if (filters.branchCode) return index.byBranch.get(filters.branchCode) ?? ZERO_PURCHASE
  return index.total
}

/**
 * 单品分店排行只含有销量的分店；把当前商品范围内有进货、却没有销量的分店补在末尾（销量记 0），
 * 避免「进了货却没卖出去」的分店被藏起来。全部商品视图的总览已包含全部分店，通常不会新增。
 */
export function appendBatchProductSalesPurchaseOnlyBranches(
  branches: readonly BatchSalesBranch[],
  index: BatchProductSalesPurchaseIndex | undefined,
  productCode: string | undefined,
  branchNames: ReadonlyMap<string, string>,
): BatchSalesBranch[] {
  if (!index) return [...branches]
  const known = new Set(branches.map((branch) => branch.branchCode))
  const purchased = productCode ? index.branchesByProduct.get(productCode) ?? [] : [...index.byBranch.keys()]
  const extra = purchased
    .filter((branchCode) => !known.has(branchCode) && getBatchProductSalesPurchase(index, { productCode, branchCode }).total !== 0)
    .sort((left, right) => getBatchProductSalesPurchase(index, { productCode, branchCode: right }).total - getBatchProductSalesPurchase(index, { productCode, branchCode: left }).total)
    .map((branchCode) => ({ branchCode, branchName: branchNames.get(branchCode) ?? branchCode, metrics: emptyMetrics(), daily: [] }))
  return [...branches, ...extra]
}

/**
 * 只移除服务端补齐的全零日期。分类净量是有符号值，故退货和正负抵消也属于真实活动。
 * 同类销售和退货抵消后分类净量可为零，此时退货量或销售额仍能证明当天有真实业务。
 * 待统计尚未提供可靠分类时，以非零总销量兜底，避免把真实销售隐藏为补齐日。
 */
export function hasBatchProductSalesDailyActivity(day: BatchSalesDaily): boolean {
  const { metrics } = day
  return nonZeroFinite(metrics.regularQuantity) || nonZeroFinite(metrics.discountQuantity) || nonZeroFinite(metrics.unknownQuantity) || nonZeroFinite(metrics.returnQuantity) || nonZeroFinite(metrics.salesAmount) || (metrics.discountStatus === 'pending' && nonZeroFinite(metrics.quantity))
}

/** 保持服务端并列顺序，并避免原地排序影响原始分店数据。 */
export function sortBatchProductSalesBranchesByQuantity(branches: readonly BatchSalesBranch[]): BatchSalesBranch[] {
  return [...branches].sort((left, right) => right.metrics.quantity - left.metrics.quantity)
}

/** 待计算和终态不可用时不把缺少证据的正价/折扣数量显示成真实零；未知数量仍可如实展示。 */
export function getBatchProductSalesClassifiedQuantity(metrics: BatchSalesMetrics, field: 'regularQuantity' | 'discountQuantity' | 'unknownQuantity', classificationUnavailable = false): number | null {
  if (metrics.discountStatus === 'pending' || (metrics.discountStatus === 'unknown' && field !== 'unknownQuantity')) return null
  if (classificationUnavailable && field !== 'unknownQuantity') return null
  return metrics[field]
}

/** 后台回填会逐日发布，部分可用和可恢复的失配状态也要继续读取最新快照。 */
export function shouldRefreshBatchProductSalesDiscountStatistics(status?: string): boolean {
  // 后端排队状态曾返回 Pending；OutOfSync/Unavailable 可能正由后台重试，保持既有轮询节奏以恢复展示。
  return ['queued', 'running', 'pending', 'backfilling', 'refreshing', 'partial', 'outofsync', 'unavailable'].includes(normalizeDiscountState(status))
}

/** Fresh 以外的状态均需要提示。 */
export function hasBatchProductSalesDiscountStatisticsNotice(status?: string): boolean {
  return !!status && status.toLowerCase() !== 'fresh'
}

/** 服务端状态大小写和分隔符不稳定，翻译 key 必须收敛为既有的 PascalCase 枚举。 */
export function getBatchProductSalesDiscountStateKey(status?: string): 'Fresh' | 'Queued' | 'Running' | 'Backfilling' | 'Refreshing' | 'Partial' | 'Unavailable' | 'Failed' | 'OutOfSync' | 'Superseded' | 'Pending' {
  const normalized = normalizeDiscountState(status)
  switch (normalized) {
    case 'fresh':
      return 'Fresh'
    case 'queued':
      return 'Queued'
    case 'running':
      return 'Running'
    case 'backfilling':
      return 'Backfilling'
    case 'refreshing':
      return 'Refreshing'
    case 'partial':
      return 'Partial'
    case 'failed':
      return 'Failed'
    case 'outofsync':
      return 'OutOfSync'
    case 'superseded':
      return 'Superseded'
    case 'pending':
      return 'Pending'
    default:
      return 'Unavailable'
  }
}

function normalizeDiscountState(status?: string): string {
  return status?.trim().replace(/[ _-]/g, '').toLowerCase() ?? ''
}

function parseDateOnly(value: string): Date | null {
  const match = DATE_ONLY_PATTERN.exec(value.trim())
  if (!match) {
    return null
  }
  const parsed = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])))
  if (parsed.getUTCFullYear() !== Number(match[1]) || parsed.getUTCMonth() !== Number(match[2]) - 1 || parsed.getUTCDate() !== Number(match[3])) {
    return null
  }
  return parsed
}

/**
 * 使用页面传入的业务日期字符串校验，避免浏览器本地时区在 UTC 零点附近改变结果。
 */
export function getBatchProductSalesDateRangeError(startDate: string, endDate: string, todayDate: string): string | undefined {
  const start = parseDateOnly(startDate)
  const end = parseDateOnly(endDate)
  const today = parseDateOnly(todayDate)
  if (!start || !end || !today) {
    return '日期格式无效'
  }
  if (start.getTime() > end.getTime()) {
    return '开始日期不能晚于结束日期'
  }
  if (end.getTime() > today.getTime()) {
    return '结束日期不能晚于今天'
  }
  if ((end.getTime() - start.getTime()) / 86_400_000 > 365) {
    return '日期范围不能超过 366 天'
  }
  return undefined
}

/** CSV 单元格统一转义，并把可能被表格程序视为公式的前缀改为纯文本。 */
export function escapeCsvCell(value: unknown): string {
  if (value === null || value === undefined) {
    return ''
  }
  if (typeof value === 'number') {
    // 数值（包括退货负数）必须保留为可计算 CSV 数字，不能当作公式文本加单引号。
    return Number.isFinite(value) ? String(value) : ''
  }

  let text = String(value)
  if (typeof value === 'string' && /^\s*[=+\-@]/.test(text)) {
    text = `'${text}`
  }
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

export function formatCsvRow(values: readonly unknown[]): string {
  return values.map(escapeCsvCell).join(',')
}
