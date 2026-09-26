import type {
  LocalSupplierProductSalesAnalysisBranch,
  LocalSupplierProductSalesAnalysisBootstrap,
  LocalSupplierProductSalesAnalysisCandidate,
  LocalSupplierProductSalesAnalysisDaily,
  LocalSupplierProductSalesAnalysisFilter,
  LocalSupplierProductSalesAnalysisInvoiceDetail,
  LocalSupplierProductSalesAnalysisOptions,
  LocalSupplierProductSalesAnalysisPaged,
  LocalSupplierProductSalesAnalysisRequest,
  LocalSupplierProductSalesAnalysisSectionErrors,
  LocalSupplierProductSalesAnalysisSelection,
  LocalSupplierProductSalesAnalysisSummary,
} from '../../../types/localSupplierProductSalesAnalysis'

export interface LocalProductSalesAnalysisDateRange { startDate: string; endDate: string }

/** bootstrap 需要覆盖完整页面数据，给偶发但成功的慢查询留出有界等待时间。 */
export const PAGE_BOOTSTRAP_TIMEOUT_SECONDS = 15

/** 分段重试、候选分页与分店钻取继续使用较短的交互等待上限。 */
export const PAGE_SECTION_TIMEOUT_SECONDS = 8

export type LocalProductSalesAnalysisSectionKey = keyof LocalSupplierProductSalesAnalysisSectionErrors

/** 一次 bootstrap 响应原子提交后的页面数据快照。 */
export interface LocalProductSalesAnalysisBootstrapState {
  options: LocalSupplierProductSalesAnalysisOptions
  candidates: LocalSupplierProductSalesAnalysisPaged<LocalSupplierProductSalesAnalysisCandidate> | null
  effectiveSelection: LocalSupplierProductSalesAnalysisSelection
  currentProduct: LocalSupplierProductSalesAnalysisCandidate | null
  summary: LocalSupplierProductSalesAnalysisSummary | null
  invoiceDetails: LocalSupplierProductSalesAnalysisPaged<LocalSupplierProductSalesAnalysisInvoiceDetail> | null
  productDaily: LocalSupplierProductSalesAnalysisDaily[]
  branches: LocalSupplierProductSalesAnalysisBranch[]
  sectionErrors: LocalSupplierProductSalesAnalysisSectionErrors
  partial: boolean
}

export interface PageRequestTimeout {
  signal: AbortSignal
  clear: () => void
  abort: () => void
}

function brisbaneDateParts(now: Date) {
  const parts = new Intl.DateTimeFormat('en-AU', {
    timeZone: 'Australia/Brisbane', year: 'numeric', month: '2-digit', day: '2-digit',
  }).formatToParts(now)
  const value = (type: string) => parts.find((part) => part.type === type)?.value ?? ''
  return { year: value('year'), month: value('month'), day: value('day') }
}

function formatUtcDate(date: Date) {
  return date.toISOString().slice(0, 10)
}

// 先按 Brisbane 取自然日，再回推，避免运行环境时区影响默认“昨天”。
export function buildBrisbaneDefaultRange(days: number, now = new Date()): LocalProductSalesAnalysisDateRange {
  const { year, month, day } = brisbaneDateParts(now)
  const end = new Date(Date.UTC(Number(year), Number(month) - 1, Number(day) - 1))
  const start = new Date(end)
  start.setUTCDate(start.getUTCDate() - Math.max(1, days) + 1)
  return { startDate: formatUtcDate(start), endDate: formatUtcDate(end) }
}

function toUtcDay(value: string) {
  const date = new Date(`${value}T00:00:00.000Z`)
  return Number.isNaN(date.getTime()) ? null : date
}

export function getDateRangeError(startDate: string, endDate: string, brisbaneYesterday: string): string | undefined {
  const start = toUtcDay(startDate); const end = toUtcDay(endDate); const yesterday = toUtcDay(brisbaneYesterday)
  if (!start || !end || !yesterday || start > end) return '参数错误：开始日期不能晚于结束日期'
  if (end > yesterday) return '参数错误：日期范围截至 Brisbane 昨天'
  if ((end.getTime() - start.getTime()) / 86_400_000 > 365) return '参数错误：日期范围不能超过 366 天'
  return undefined
}

export function createIncludedSelection(productCodes: string[] = []): LocalSupplierProductSalesAnalysisSelection {
  return { mode: 'included', includedProductCodes: [...new Set(productCodes)], excludedProductCodes: [] }
}

export function isSelected(selection: LocalSupplierProductSalesAnalysisSelection, productCode: string) {
  return selection.mode === 'allFiltered'
    ? !selection.excludedProductCodes.includes(productCode)
    : selection.includedProductCodes.includes(productCode)
}

export function canSetCurrentProduct(selection: LocalSupplierProductSalesAnalysisSelection, productCode: string) {
  return isSelected(selection, productCode)
}

export function createLatestRequestGuard() {
  let version = 0
  return {
    next: () => ++version,
    isCurrent: (token: number) => token === version,
    invalidate: () => { version += 1 },
  }
}

export function applyCandidateSelection(
  selection: LocalSupplierProductSalesAnalysisSelection,
  productCode: string,
  checked: boolean,
): LocalSupplierProductSalesAnalysisSelection {
  if (selection.mode === 'allFiltered') {
    const excluded = new Set(selection.excludedProductCodes)
    if (checked) excluded.delete(productCode)
    else excluded.add(productCode)
    return { ...selection, excludedProductCodes: [...excluded] }
  }
  const included = new Set(selection.includedProductCodes)
  if (checked) included.add(productCode)
  else included.delete(productCode)
  return { ...selection, includedProductCodes: [...included] }
}

/** 树搜索后只替换当前可见叶节点，隐藏分支中的已选分类必须继续保留。 */
export function mergeVisibleSupplierCategorySelection(
  previous: string[],
  checkedVisible: string[],
  visibleLeafGuids: ReadonlySet<string>,
): string[] {
  return [...new Set([
    ...previous.filter((guid) => !visibleLeafGuids.has(guid)),
    ...checkedVisible.filter((guid) => visibleLeafGuids.has(guid)),
  ])]
}

/** 供应商分类接口可能返回嵌套 roots；清理已选值时只保留仍存在的叶分类。 */
interface SupplierCategoryGuidNode { guid: string; children?: SupplierCategoryGuidNode[] }
export function collectSupplierCategoryLeafGuids(
  options: SupplierCategoryGuidNode[],
): string[] {
  return options.flatMap((item) => item.children?.length ? collectSupplierCategoryLeafGuids(item.children) : [item.guid])
}

export function getCurrentProductAfterCancellation<T extends Pick<LocalSupplierProductSalesAnalysisCandidate, 'productCode'>>(
  currentProduct: T | null,
  summaryItems: T[],
  currentWasCancelled: boolean,
): T | null {
  if (!currentWasCancelled) return currentProduct
  return summaryItems[0] ?? null
}

export interface LocalProductSalesAnalysisBootstrapRequestContext {
  filter: LocalSupplierProductSalesAnalysisFilter
  selection?: LocalSupplierProductSalesAnalysisSelection
  currentProductCode?: string
  autoSelectFirst: boolean
  forceRefresh: boolean
  candidatePageNumber: number
  candidatePageSize: number
  summaryPageNumber: number
  summaryPageSize: number
}

/** 挂载/查询/重置（autoSelectFirst）与刷新（携带原选择/当前商品、forceRefresh）共用同一请求构造。 */
export function buildLocalProductSalesAnalysisBootstrapRequest(context: LocalProductSalesAnalysisBootstrapRequestContext): LocalSupplierProductSalesAnalysisRequest {
  return {
    filter: context.filter,
    ...(context.selection ? { selection: context.selection } : {}),
    currentProductCode: context.currentProductCode,
    autoSelectFirst: context.autoSelectFirst,
    forceRefresh: context.forceRefresh,
    candidatePageNumber: context.candidatePageNumber,
    candidatePageSize: context.candidatePageSize,
    summaryPageNumber: context.summaryPageNumber,
    summaryPageSize: context.summaryPageSize,
  }
}

/** 页面请求安全超时：默认用于分段请求；bootstrap 必须显式传入自己的等待上限。 */
export function createPageRequestTimeout(seconds = PAGE_SECTION_TIMEOUT_SECONDS): PageRequestTimeout {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), Math.max(1, Math.round(seconds * 1000)))
  return {
    signal: controller.signal,
    clear: () => clearTimeout(timer),
    abort: () => { clearTimeout(timer); controller.abort() },
  }
}

export function createEmptyLocalProductSalesAnalysisState(): LocalProductSalesAnalysisBootstrapState {
  return {
    options: { warehouseCategories: [], suppliers: [] },
    candidates: null,
    effectiveSelection: createIncludedSelection(),
    currentProduct: null,
    summary: null,
    invoiceDetails: null,
    productDaily: [],
    branches: [],
    sectionErrors: {},
    partial: false,
  }
}

/** 切换商品/筛选只清空下游分段，保留候选与选项。 */
export function clearLocalProductSalesAnalysisDetailSections(state: LocalProductSalesAnalysisBootstrapState): LocalProductSalesAnalysisBootstrapState {
  return { ...state, summary: null, invoiceDetails: null, productDaily: [], branches: [], sectionErrors: {} }
}

/** 原子提交契约：一次调用替换全部数据分段；未提供的分段保留旧值。 */
export function applyLocalProductSalesAnalysisBootstrapResult(
  result: LocalSupplierProductSalesAnalysisBootstrap,
  previous: LocalProductSalesAnalysisBootstrapState,
): LocalProductSalesAnalysisBootstrapState {
  return {
    options: result.options,
    candidates: result.candidates,
    effectiveSelection: result.effectiveSelection ?? previous.effectiveSelection,
    currentProduct: result.currentProduct === undefined ? previous.currentProduct : result.currentProduct,
    summary: result.summary === undefined ? previous.summary : result.summary,
    invoiceDetails: result.invoiceDetails === undefined ? previous.invoiceDetails : result.invoiceDetails,
    productDaily: result.productDaily === undefined ? previous.productDaily : result.productDaily ?? [],
    branches: result.branches === undefined ? previous.branches : result.branches ?? [],
    sectionErrors: result.sectionErrors ?? {},
    partial: result.partial === true,
  }
}

function withoutSectionError(errors: LocalSupplierProductSalesAnalysisSectionErrors, key: LocalProductSalesAnalysisSectionKey): LocalSupplierProductSalesAnalysisSectionErrors {
  const next = { ...errors }
  delete next[key]
  return next
}

/** 分段重试成功后只替换目标分段并清除对应错误，不影响其它分段。 */
export function applyLocalProductSalesAnalysisSectionResult(
  state: LocalProductSalesAnalysisBootstrapState,
  key: LocalProductSalesAnalysisSectionKey,
  data: unknown,
): LocalProductSalesAnalysisBootstrapState {
  const next: LocalProductSalesAnalysisBootstrapState = { ...state }
  switch (key) {
    case 'options':
      next.options = data as LocalSupplierProductSalesAnalysisOptions
      break
    case 'summary':
      next.summary = data as LocalSupplierProductSalesAnalysisSummary | null
      break
    case 'invoiceDetails':
      next.invoiceDetails = data as LocalSupplierProductSalesAnalysisPaged<LocalSupplierProductSalesAnalysisInvoiceDetail> | null
      break
    case 'productDaily':
      next.productDaily = data as LocalSupplierProductSalesAnalysisDaily[]
      break
    case 'branches':
      next.branches = data as LocalSupplierProductSalesAnalysisBranch[]
      break
  }
  return { ...next, sectionErrors: withoutSectionError(next.sectionErrors, key) }
}

export function setLocalProductSalesAnalysisSectionError(
  state: LocalProductSalesAnalysisBootstrapState,
  key: LocalProductSalesAnalysisSectionKey,
  message: string,
): LocalProductSalesAnalysisBootstrapState {
  return { ...state, sectionErrors: { ...state.sectionErrors, [key]: message } }
}

export function clearLocalProductSalesAnalysisSectionError(
  state: LocalProductSalesAnalysisBootstrapState,
  key: LocalProductSalesAnalysisSectionKey,
): LocalProductSalesAnalysisBootstrapState {
  return { ...state, sectionErrors: withoutSectionError(state.sectionErrors, key) }
}

export function formatAud(value: number | null | undefined) {
  return value === null || value === undefined ? '—' : new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' }).format(value)
}

export function toFlowTrendData(data: Array<{ date: string; purchaseQuantity?: number; netSalesQuantity: number; netSalesAmount: number; averageUnitPrice: number | null }>) {
  return data.map((item) => ({
    date: item.date,
    inboundQuantity: item.purchaseQuantity ?? 0,
    shippedQuantity: 0,
    netSalesQuantity: item.netSalesQuantity,
    netSalesAmount: item.netSalesAmount,
    averageUnitPrice: item.averageUnitPrice,
  }))
}

/** 除数不为正时返回 null，供均价、日均等派生指标展示为 “—”。 */
export function safeDivide(numerator: number | null | undefined, denominator: number | null | undefined): number | null {
  if (numerator === null || numerator === undefined || denominator === null || denominator === undefined) return null
  return denominator > 0 ? numerator / denominator : null
}

/** 日期范围含首尾两天；只按 yyyy-MM-dd 文本计算，避免浏览器时区偏移。 */
export function countInclusiveDays(startDate: string, endDate: string): number {
  const start = Date.parse(`${startDate}T00:00:00Z`)
  const end = Date.parse(`${endDate}T00:00:00Z`)
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) return 0
  return Math.round((end - start) / 86_400_000) + 1
}

export type SellThroughLevel = 'none' | 'restock' | 'healthy' | 'slow' | 'stale'

/**
 * 售进比状态分级。入参为百分数（316.7 即 316.7%）。
 * 阈值为业务约定：>150 补货预警、60–150 健康、30–60 偏慢、<30 滞销；无进货（null）不评级。
 */
export function getSellThroughLevel(rate: number | null | undefined): SellThroughLevel {
  if (rate === null || rate === undefined || !Number.isFinite(rate)) return 'none'
  if (rate > 150) return 'restock'
  if (rate >= 60) return 'healthy'
  if (rate >= 30) return 'slow'
  return 'stale'
}

export interface BranchPriceTier { price: number; branchCount: number }

/** 按分店均价（四舍五入到分）归档，用于提示同一商品在各分店的定价是否一致。 */
export function buildBranchPriceTiers(branches: readonly Pick<LocalSupplierProductSalesAnalysisBranch, 'averageUnitPrice'>[]): BranchPriceTier[] {
  const tiers = new Map<number, number>()
  for (const branch of branches) {
    if (branch.averageUnitPrice === null || branch.averageUnitPrice === undefined) continue
    const price = Math.round(branch.averageUnitPrice * 100) / 100
    tiers.set(price, (tiers.get(price) ?? 0) + 1)
  }
  return [...tiers.entries()].sort((a, b) => a[0] - b[0]).map(([price, branchCount]) => ({ price, branchCount }))
}

export type TrendChartMode = 'daily' | 'cumulative'

export interface TrendChartModel {
  purchase: number[]
  sales: number[]
  /** 数量轴刻度；负向只保留不超过最小值的整步，少量退货不会把下界拉到整十。 */
  ticks: number[]
  domainMin: number
  domainMax: number
  /** 均价面板值域；期间无销售时为 null。 */
  priceDomain: [number, number] | null
  xTickIndices: number[]
}

function niceStep(range: number, count: number) {
  const raw = Math.max(range / count, 1)
  const power = 10 ** Math.floor(Math.log10(raw))
  const factor = [1, 2, 5, 10].find((item) => item * power >= raw) ?? 10
  return factor * power
}

/**
 * 进销趋势图模型：数量与均价分属上下两个面板、各自独立纵轴，不做双轴叠加。
 * cumulative 模式把进货与净销逐日累加，直接呈现售进比的追赶关系。
 */
export function buildTrendChartModel(data: readonly LocalSupplierProductSalesAnalysisDaily[], mode: TrendChartMode, tickCount = 4): TrendChartModel {
  let purchaseTotal = 0
  let salesTotal = 0
  const purchase = data.map((item) => mode === 'cumulative' ? (purchaseTotal += item.purchaseQuantity) : item.purchaseQuantity)
  const sales = data.map((item) => mode === 'cumulative' ? (salesTotal += item.netSalesQuantity) : item.netSalesQuantity)
  const minValue = Math.min(0, ...sales, ...purchase)
  const maxValue = Math.max(1, ...sales, ...purchase)
  const step = niceStep(maxValue - minValue, tickCount)
  const lowestTick = -Math.floor(-minValue / step) * step
  const highestTick = Math.ceil(maxValue / step) * step
  const ticks: number[] = []
  for (let value = lowestTick; value <= highestTick + 1e-9; value += step) ticks.push(Math.round(value))
  const prices = data.map((item) => item.averageUnitPrice).filter((value): value is number => value !== null && value !== undefined)
  let priceDomain: [number, number] | null = null
  if (prices.length) {
    const low = Math.min(...prices)
    const high = Math.max(...prices)
    // 全期同价时给出上下留白，避免折线贴边。
    priceDomain = high - low < 0.1 ? [low - 0.25, high + 0.25] : [low, high]
  }
  const labelEvery = data.length <= 7 ? 1 : data.length <= 31 ? 5 : Math.ceil(data.length / 6)
  const xTickIndices = data.map((_, index) => index).filter((index) => index % labelEvery === 0)
  return { purchase, sales, ticks, domainMin: Math.min(lowestTick, minValue), domainMax: highestTick, priceDomain, xTickIndices }
}
