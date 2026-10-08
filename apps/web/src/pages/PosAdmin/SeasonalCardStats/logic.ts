// 节日贺卡「分店填报统计」页的纯函数：筛选与查询参数、金额与数量格式、状态筛选与合计、
// 价格分布条、单店矩阵与时间线文字、地址栏 ?store=、本地时间、错误分类。
// 刻意不依赖 React / antd / dayjs / i18n，方便在 Node 里直接做单测。

import type {
  SeasonalCardBatch,
  SeasonalCardBatchLine,
  SeasonalCardPriceOption,
  SeasonalCardStatsPriceQuantity,
  SeasonalCardStatsQuery,
  SeasonalCardStatsStoreRef,
  SeasonalCardStatsStoreRow,
  SeasonalCardStatsSupplierRef,
  SeasonalCardType,
} from '../../../types/seasonalCardStats'

export const EMPTY_VALUE = '—'
/** 负数用真正的减号（U+2212），与日结记录页一致。 */
export const MINUS_SIGN = '−'
/** 抽屉状态放在地址栏的查询参数名：?store=<分店编码>。 */
export const DETAIL_QUERY_PARAM = 'store'

export const CARD_TYPES: readonly SeasonalCardType[] = [1, 2, 3, 4, 5]
export const PRICE_OPTIONS: readonly SeasonalCardPriceOption[] = [1, 2, 3, 4]
/** 「其他」价格：单价不固定，表格只显示数量，金额计入合计。 */
export const OTHER_PRICE_OPTION: SeasonalCardPriceOption = 4
/** 价格类型的显示名：固定价格直接是 `$1/$2/$3`，「其他」由调用方传入已翻译的文案。 */
export function getPriceLabel(option: SeasonalCardPriceOption, otherLabel: string): string {
  if (option === 1) return '$1'
  if (option === 2) return '$2'
  if (option === 3) return '$3'
  return otherLabel
}

export type FillStatusFilter = 'all' | 'filled' | 'unfilled'
export const STATUS_FILTERS: readonly FillStatusFilter[] = ['all', 'filled', 'unfilled']

/** 年份下拉：明年 + 今年 + 往前 4 年（贺卡填报 2026 年才上线，再早没有数据也无妨）。 */
export const YEARS_BACK = 4

// ───────────────────────── 筛选与查询 ─────────────────────────

export interface SeasonalCardStatsFilters {
  seasonYear: number
  cardType: SeasonalCardType
  localSupplierCode?: string
  priceOption?: SeasonalCardPriceOption
  storeCodes: string[]
}

/** 默认：今年 + 圣诞节，其余不限。 */
export function createDefaultFilters(currentYear: number): SeasonalCardStatsFilters {
  return { seasonYear: currentYear, cardType: 1, localSupplierCode: undefined, priceOption: undefined, storeCodes: [] }
}

export function buildYearOptions(currentYear: number): number[] {
  const years: number[] = []
  for (let year = currentYear + 1; year >= currentYear - YEARS_BACK; year -= 1) years.push(year)
  return years
}

export function isCardType(value: unknown): value is SeasonalCardType {
  return typeof value === 'number' && (CARD_TYPES as readonly number[]).includes(value)
}

export function isPriceOption(value: unknown): value is SeasonalCardPriceOption {
  return typeof value === 'number' && (PRICE_OPTIONS as readonly number[]).includes(value)
}

export function buildSummaryQuery(filters: SeasonalCardStatsFilters): SeasonalCardStatsQuery {
  const storeCodes = Array.from(new Set(filters.storeCodes.map((code) => code.trim()).filter(Boolean)))
  const supplier = filters.localSupplierCode?.trim()
  return {
    seasonYear: filters.seasonYear,
    cardType: filters.cardType,
    localSupplierCode: supplier || undefined,
    priceOption: filters.priceOption,
    storeCodes: storeCodes.length ? storeCodes : undefined,
  }
}

/** 供应商或价格筛选生效时，数量与金额只是部分口径（填报状态不受影响）。 */
export function hasQuantityFilter(filters: SeasonalCardStatsFilters): boolean {
  return Boolean(filters.localSupplierCode?.trim()) || filters.priceOption !== undefined
}

// ───────────────────────── 金额与数量 ─────────────────────────

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function groupThousands(integerText: string): string {
  return integerText.replace(/\B(?=(\d{3})+(?!\d))/g, ',')
}

/** 金额：`$1,234.50`；负数用「−」；空值给占位符。先换算到分再四舍五入，避免浮点误差。 */
export function formatMoney(value: number | null | undefined, placeholder = EMPTY_VALUE): string {
  if (!isFiniteNumber(value)) return placeholder
  const cents = Math.round(Math.abs(value) * 100)
  const dollars = groupThousands(String(Math.floor(cents / 100)))
  const remainder = String(cents % 100).padStart(2, '0')
  return `${value < 0 && cents !== 0 ? MINUS_SIGN : ''}$${dollars}.${remainder}`
}

/** 数量：千分位整数（剩余张数都是整数），空值给占位符。 */
export function formatCount(value: number | null | undefined, placeholder = EMPTY_VALUE): string {
  if (!isFiniteNumber(value)) return placeholder
  const rounded = Math.round(value)
  return `${rounded < 0 ? MINUS_SIGN : ''}${groupThousands(String(Math.abs(rounded)))}`
}

/** 按分求和，避免 0.1 + 0.2 一类的残差。 */
export function sumMoney(values: readonly number[]): number {
  return values.reduce((sum, value) => sum + Math.round((isFiniteNumber(value) ? value : 0) * 100), 0) / 100
}

/** 某价格类型的数量（行里没有该项按 0）。 */
export function getPriceQuantity(prices: readonly SeasonalCardStatsPriceQuantity[], option: SeasonalCardPriceOption): number {
  return prices.find((item) => item.priceOption === option)?.quantity ?? 0
}

// ───────────────────────── 状态筛选与合计 ─────────────────────────

export function isStatusFilter(value: unknown): value is FillStatusFilter {
  return typeof value === 'string' && (STATUS_FILTERS as readonly string[]).includes(value)
}

export function filterRowsByStatus(rows: readonly SeasonalCardStatsStoreRow[], status: FillStatusFilter): SeasonalCardStatsStoreRow[] {
  if (status === 'filled') return rows.filter((row) => row.isFilled)
  if (status === 'unfilled') return rows.filter((row) => !row.isFilled)
  return [...rows]
}

export function countByStatus(rows: readonly SeasonalCardStatsStoreRow[]): Record<FillStatusFilter, number> {
  const filled = rows.filter((row) => row.isFilled).length
  return { all: rows.length, filled, unfilled: rows.length - filled }
}

export interface FooterTotals {
  filledCount: number
  quantities: Record<SeasonalCardPriceOption, number>
  totalQuantity: number
  totalAmount: number
}

/** 表格合计行：只累加当前显示的行（未填报行本来就是 0）。 */
export function buildFooterTotals(rows: readonly SeasonalCardStatsStoreRow[]): FooterTotals {
  const quantities = { 1: 0, 2: 0, 3: 0, 4: 0 } as Record<SeasonalCardPriceOption, number>
  for (const row of rows) {
    for (const option of PRICE_OPTIONS) quantities[option] += getPriceQuantity(row.prices, option)
  }
  return {
    filledCount: rows.filter((row) => row.isFilled).length,
    quantities,
    totalQuantity: rows.reduce((sum, row) => sum + row.totalQuantity, 0),
    totalAmount: sumMoney(rows.map((row) => row.totalAmount)),
  }
}

/** 平均每店剩余张数 = 合计数量 ÷ 已填报分店数（四舍五入到整数）；没有已填报分店时为 null。 */
export function averagePerFilledStore(totalQuantity: number, filledStoreCount: number): number | null {
  if (!isFiniteNumber(totalQuantity) || !isFiniteNumber(filledStoreCount) || filledStoreCount <= 0) return null
  return Math.round(totalQuantity / filledStoreCount)
}

/** 填报进度百分比（0–100 的整数），应填报分店为 0 时为 0。 */
export function fillPercent(filled: number, total: number): number {
  if (!isFiniteNumber(filled) || !isFiniteNumber(total) || total <= 0) return 0
  return Math.max(0, Math.min(100, Math.round((filled / total) * 100)))
}

export interface PriceBar {
  priceOption: SeasonalCardPriceOption
  quantity: number
  amount: number
  /** 相对最大一档的宽度百分比（0–100）；全为 0 时都是 0。 */
  widthPercent: number
}

export function buildPriceBars(priceTotals: readonly SeasonalCardStatsPriceQuantity[]): PriceBar[] {
  const max = Math.max(0, ...PRICE_OPTIONS.map((option) => getPriceQuantity(priceTotals, option)))
  return PRICE_OPTIONS.map((option) => {
    const item = priceTotals.find((entry) => entry.priceOption === option)
    const quantity = item?.quantity ?? 0
    return {
      priceOption: option,
      quantity,
      amount: item?.amount ?? 0,
      widthPercent: max > 0 ? Math.round((quantity / max) * 100) : 0,
    }
  })
}

/** 供应商列：名称去重后用分隔符连接；没有时给占位符。 */
export function formatSupplierList(suppliers: readonly SeasonalCardStatsSupplierRef[], separator: string): string {
  const names = Array.from(new Set(suppliers.map((supplier) => supplier.supplierName || supplier.localSupplierCode || '').filter(Boolean)))
  return names.length ? names.join(separator) : EMPTY_VALUE
}

/** 「复制名单」：每行一个「编码 名称」。 */
export function buildUnfilledCopyText(stores: readonly SeasonalCardStatsStoreRef[]): string {
  return stores.map((store) => [store.storeCode, store.storeName].filter(Boolean).join(' ')).join('\n')
}

/** 分店选项：把每次汇总返回的分店并入已知名单（无分店清单权限的账号也能选分店），按编码排序。 */
export function mergeStoreUniverse(
  current: readonly SeasonalCardStatsStoreRef[],
  incoming: readonly SeasonalCardStatsStoreRef[],
): SeasonalCardStatsStoreRef[] {
  const byCode = new Map(current.map((store) => [store.storeCode, store]))
  let changed = false
  for (const store of incoming) {
    if (!store.storeCode) continue
    const known = byCode.get(store.storeCode)
    if (!known || (store.storeName && known.storeName !== store.storeName)) {
      byCode.set(store.storeCode, { storeCode: store.storeCode, storeName: store.storeName || known?.storeName || '' })
      changed = true
    }
  }
  if (!changed) return current as SeasonalCardStatsStoreRef[]
  return [...byCode.values()].sort((left, right) => left.storeCode.localeCompare(right.storeCode, 'en', { numeric: true }))
}

// ───────────────────────── 单店明细 ─────────────────────────

export interface MatrixCell {
  quantity: number
  /** 「其他」价格的实际单价（数量大于 0 且单价大于 0 才有）；固定价格为 null。 */
  unitPrice: number | null
}

export interface MatrixRow {
  key: string
  supplierName: string
  cells: Record<SeasonalCardPriceOption, MatrixCell>
  totalQuantity: number
  totalAmount: number
}

function emptyCells(): Record<SeasonalCardPriceOption, MatrixCell> {
  return {
    1: { quantity: 0, unitPrice: null },
    2: { quantity: 0, unitPrice: null },
    3: { quantity: 0, unitPrice: null },
    4: { quantity: 0, unitPrice: null },
  }
}

/** 供应商 × 价格矩阵（当前生效批次）：一行一个供应商。 */
export function buildSupplierMatrix(batches: readonly SeasonalCardBatch[], unassignedLabel: string): MatrixRow[] {
  return batches.map((batch, index) => {
    const cells = emptyCells()
    for (const line of batch.lines) {
      const cell = cells[line.priceOption]
      cell.quantity += line.remainingQuantity
      if (line.priceOption === OTHER_PRICE_OPTION && line.remainingQuantity > 0 && line.unitPrice > 0) {
        cell.unitPrice = line.unitPrice
      }
    }
    return {
      key: batch.localSupplierCode ?? batch.batchGuid ?? `row-${index}`,
      supplierName: batch.supplierName || batch.localSupplierCode || unassignedLabel,
      cells,
      totalQuantity: batch.totalQuantity,
      totalAmount: batch.totalAmount,
    }
  })
}

export interface BatchLineLabels {
  /** 「其他」价格的名称（已翻译） */
  other: string
  /** 「其他」价格的单价后缀（已翻译），如中文 `（$4.50）`、英文 ` ($4.50)` */
  unitPrice: (price: string) => string
}

/** 时间线里一批的摘要：`$1 × 120 · $2 × 80 · $3 × 36 · 其他 × 12（$4.50）`。 */
export function formatBatchLines(lines: readonly SeasonalCardBatchLine[], labels: BatchLineLabels): string {
  if (!lines.length) return EMPTY_VALUE
  return lines
    .map((line) => {
      if (line.priceOption === OTHER_PRICE_OPTION) {
        const price = line.remainingQuantity > 0 && line.unitPrice > 0 ? labels.unitPrice(formatMoney(line.unitPrice)) : ''
        return `${labels.other} × ${formatCount(line.remainingQuantity)}${price}`
      }
      return `${getPriceLabel(line.priceOption, labels.other)} × ${formatCount(line.remainingQuantity)}`
    })
    .join(' · ')
}

export interface YearComparison {
  currentWidth: number
  previousWidth: number
  /** 今年 − 去年（去年没填为 null） */
  delta: number | null
}

/** 与去年同节日对比的两根条：较大者为 100%。 */
export function buildYearComparison(current: number, previous: number | null): YearComparison {
  const max = Math.max(current, previous ?? 0)
  const width = (value: number) => (max > 0 ? Math.round((value / max) * 100) : 0)
  return {
    currentWidth: width(current),
    previousWidth: previous === null ? 0 : width(previous),
    delta: previous === null ? null : current - previous,
  }
}

/** 带正负号的数量差：`+12` / `−8` / `0`。 */
export function formatSignedCount(value: number | null | undefined): string {
  if (!isFiniteNumber(value)) return EMPTY_VALUE
  if (Math.round(value) === 0) return '0'
  return `${value > 0 ? '+' : ''}${formatCount(value)}`
}

// ───────────────────────── 地址栏中的抽屉状态 ─────────────────────────

const STORE_CODE_PATTERN = /^[A-Za-z0-9_-]{1,32}$/

/** 地址栏 `?store=` → 分店编码；不合法一律当作没有。 */
export function parseStoreParam(value: string | null | undefined): string | null {
  const trimmed = value?.trim()
  return trimmed && STORE_CODE_PATTERN.test(trimmed) ? trimmed : null
}

/** 在保留其他查询参数的前提下写入/移除 `store`。 */
export function withStoreParam(current: URLSearchParams, storeCode: string | null): URLSearchParams {
  const next = new URLSearchParams(current)
  if (storeCode) next.set(DETAIL_QUERY_PARAM, storeCode)
  else next.delete(DETAIL_QUERY_PARAM)
  return next
}

// ───────────────────────── 时间（浏览器本地时区） ─────────────────────────

/** 后端 DateTime 从库里读出时不带时区后缀：一律按 UTC 解释。 */
export function parseUtcTimestamp(value: string | null | undefined): Date | null {
  if (!value) return null
  const normalized = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value) ? value : `${value}Z`
  const date = new Date(normalized)
  return Number.isNaN(date.getTime()) ? null : date
}

const formatterCache = new Map<string, Intl.DateTimeFormat>()

function getFormatter(timeZone: string | undefined): Intl.DateTimeFormat {
  const cacheKey = timeZone ?? '__local__'
  let formatter = formatterCache.get(cacheKey)
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    })
    formatterCache.set(cacheKey, formatter)
  }
  return formatter
}

export type TimeFormat = 'short' | 'full'

/**
 * UTC 时间按浏览器本地时区显示（timeZone 只给单测固定时区用）。
 * short：`MM-DD HH:mm`（表格、时间线）；full：`YYYY-MM-DD HH:mm`（悬停、导出）。
 */
export function formatLocalTime(value: string | null | undefined, format: TimeFormat = 'short', timeZone?: string): string {
  const date = parseUtcTimestamp(value)
  if (!date) return EMPTY_VALUE
  const parts: Record<string, string> = {}
  for (const part of getFormatter(timeZone).formatToParts(date)) {
    if (part.type !== 'literal') parts[part.type] = part.value
  }
  const clock = `${parts.hour}:${parts.minute}`
  if (format === 'short') return `${parts.month}-${parts.day} ${clock}`
  return `${parts.year}-${parts.month}-${parts.day} ${clock}`
}

// ───────────────────────── 导出文件名 ─────────────────────────

/** 文件名：`前缀_2026_圣诞节.xlsx`，去掉文件系统不允许的字符。 */
export function buildExportFileName(prefix: string, seasonYear: number, holiday: string): string {
  const safe = (text: string) => text.replace(/[\\/:*?"<>|\s]+/g, '-').replace(/^-+|-+$/g, '')
  return `${[safe(prefix), String(seasonYear), safe(holiday)].filter(Boolean).join('_')}.xlsx`
}

// ───────────────────────── 请求守卫与错误分类 ─────────────────────────

/** 最新请求守卫：每次发起都会取消上一次，只有最后一次请求允许写入页面状态。 */
export function createAbortableRequestGuard() {
  let latestRequestId = 0
  let controller: AbortController | null = null
  return {
    begin(): { requestId: number; signal: AbortSignal } {
      controller?.abort()
      controller = new AbortController()
      latestRequestId += 1
      return { requestId: latestRequestId, signal: controller.signal }
    },
    isLatest(requestId: number): boolean {
      return requestId === latestRequestId
    },
    abort(): void {
      controller?.abort()
      controller = null
      latestRequestId += 1
    },
  }
}

export type StatsErrorKind = 'forbidden' | 'notFound' | 'invalidQuery' | 'failed'

interface ErrorLike {
  status?: unknown
  payload?: unknown
  name?: unknown
}

export function isAbortError(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as ErrorLike).name === 'AbortError'
}

function readErrorCode(payload: unknown): string | undefined {
  if (typeof payload !== 'object' || payload === null) return undefined
  const record = payload as { code?: unknown; errorCode?: unknown }
  const code = record.errorCode ?? record.code
  return typeof code === 'string' ? code : undefined
}

const INVALID_QUERY_CODES = new Set(['INVALID_SEASON_YEAR', 'INVALID_CARD_TYPE', 'STORE_CODE_REQUIRED'])

/**
 * 请求异常归类：403 无权限；分店不存在（HTTP 404 或业务码 STORE_NOT_FOUND）；
 * 年份 / 节日不合法（业务码，HTTP 200 + success=false）；其余为通用失败（可重试）。
 */
export function classifyStatsError(error: unknown): StatsErrorKind {
  const { status, payload } = (typeof error === 'object' && error !== null ? error : {}) as ErrorLike
  if (status === 403) return 'forbidden'
  const code = readErrorCode(payload)
  if (status === 404 || code === 'STORE_NOT_FOUND') return 'notFound'
  if (code && INVALID_QUERY_CODES.has(code)) return 'invalidQuery'
  return 'failed'
}
