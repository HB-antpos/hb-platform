// 日结记录页的纯函数：区间与分页、金额与差额格式化、门店时区时间、查询参数、明细分区、错误分类。
// 刻意不依赖 React / antd / dayjs，方便在 Node 里直接做单测。

import type {
  DailyCloseCashCount,
  DailyCloseClientKind,
  DailyCloseCounts,
  DailyCloseDifferenceKind,
  DailyCloseListItem,
  DailyCloseListQuery,
  DailyCloseStatusFilter,
  DailyCloseTender,
  DailyCloseTotals,
} from '../../../types/dailyClose'

export const DEFAULT_PAGE_SIZE = 20
export const PAGE_SIZE_OPTIONS = [20, 50, 100] as const
/** 营业日区间最长 93 个自然日（含首尾），与后端 DailyCloseQueryService.MaxRangeDays 同口径。 */
export const MAX_RANGE_DAYS = 93
/** 默认区间：最近 7 个自然日（含当天）。 */
export const DEFAULT_RANGE_DAYS = 7
/** 门店缺少时区时的回退口径（仓库其他页面同样回退到悉尼）。 */
export const FALLBACK_TIME_ZONE = 'Australia/Sydney'
/** 负数用真正的减号（U+2212），不用连字符，避免和区间分隔符混淆。 */
export const MINUS_SIGN = '−'
/** 抽屉状态放在地址栏的查询参数名。 */
export const DETAIL_QUERY_PARAM = 'id'
export const EMPTY_VALUE = '—'

export const STATUS_TABS: readonly DailyCloseStatusFilter[] = ['all', 'short', 'over', 'even', 'none']
export const CLIENT_KINDS: readonly DailyCloseClientKind[] = ['Wpf', 'Handheld', 'Ipad']

// ───────────────────────── 营业日区间 ─────────────────────────

const ISO_DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/

/** 严格校验 yyyy-MM-dd（含月份天数，如 2026-02-30 不合法）。 */
export function isIsoDate(value: unknown): value is string {
  if (typeof value !== 'string') return false
  const match = ISO_DATE_PATTERN.exec(value)
  if (!match) return false
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])]
  const date = new Date(Date.UTC(year, month - 1, day))
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day
}

function isoToDayNumber(iso: string): number {
  const [year, month, day] = iso.split('-').map(Number)
  return Math.round(Date.UTC(year, month - 1, day) / 86_400_000)
}

/** 纯日期加减（用 UTC 算，不受浏览器时区与夏令时影响）。 */
export function addDaysIso(iso: string, days: number): string {
  const [year, month, day] = iso.split('-').map(Number)
  return new Date(Date.UTC(year, month - 1, day + days)).toISOString().slice(0, 10)
}

/** to − from 的天数差（同一天为 0）。 */
export function diffDaysIso(from: string, to: string): number {
  return isoToDayNumber(to) - isoToDayNumber(from)
}

/** 默认区间：以「今天」（悉尼日历日）结尾的最近 7 天。 */
export function getDefaultBusinessDateRange(todayIso: string): [string, string] {
  return [addDaysIso(todayIso, -(DEFAULT_RANGE_DAYS - 1)), todayIso]
}

export type DatePresetKey = 'today' | 'yesterday' | 'last7' | 'last30' | 'thisMonth' | 'lastMonth'

export const DATE_PRESET_KEYS: readonly DatePresetKey[] = ['today', 'yesterday', 'last7', 'last30', 'thisMonth', 'lastMonth']

/** 区间快捷项；所有区间都不超过 93 天。 */
export function resolveDatePreset(key: DatePresetKey, todayIso: string): [string, string] {
  const [year, month] = todayIso.split('-').map(Number)
  switch (key) {
    case 'today':
      return [todayIso, todayIso]
    case 'yesterday': {
      const yesterday = addDaysIso(todayIso, -1)
      return [yesterday, yesterday]
    }
    case 'last7':
      return getDefaultBusinessDateRange(todayIso)
    case 'last30':
      return [addDaysIso(todayIso, -29), todayIso]
    case 'thisMonth':
      return [`${year}-${String(month).padStart(2, '0')}-01`, todayIso]
    case 'lastMonth': {
      const firstOfThisMonth = `${year}-${String(month).padStart(2, '0')}-01`
      const lastDayOfPrevious = addDaysIso(firstOfThisMonth, -1)
      return [`${lastDayOfPrevious.slice(0, 8)}01`, lastDayOfPrevious]
    }
  }
}

export type RangeValidation = 'ok' | 'required' | 'reversed' | 'tooLong'

/** 与后端同口径的区间校验：必填、起始不晚于结束、最长 93 天（含首尾）。 */
export function validateBusinessDateRange(from: string | null | undefined, to: string | null | undefined): RangeValidation {
  if (!isIsoDate(from) || !isIsoDate(to)) return 'required'
  if (diffDaysIso(from, to) < 0) return 'reversed'
  if (diffDaysIso(from, to) + 1 > MAX_RANGE_DAYS) return 'tooLong'
  return 'ok'
}

/**
 * RangePicker 的日期禁用规则：不能选「今天」之后（悉尼日历日，营业日不会在未来）；
 * 已选定一端后，另一端与它相差不能超过 93 天。
 */
export function isBusinessDateSelectable(candidate: string, todayIso: string, anchor?: string | null): boolean {
  if (diffDaysIso(candidate, todayIso) < 0) return false
  if (anchor && isIsoDate(anchor) && Math.abs(diffDaysIso(anchor, candidate)) + 1 > MAX_RANGE_DAYS) return false
  return true
}

// ───────────────────────── 金额与差额 ─────────────────────────

function toCents(value: number): number {
  // 先乘 100 再四舍五入，避免 1.005 一类的浮点误差；负数对称处理。
  return Math.round(Math.abs(value) * 100)
}

function formatCentsAsDollars(cents: number): string {
  const dollars = Math.floor(cents / 100)
  const remainder = String(cents % 100).padStart(2, '0')
  return `$${dollars.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ',')}.${remainder}`
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

/** AUD 金额：`$1,842.30`；负数用「−」；空值给占位符。四舍五入到分后为 0 的不带符号。 */
export function formatMoney(value: number | null | undefined, placeholder = EMPTY_VALUE): string {
  if (!isFiniteNumber(value)) return placeholder
  const cents = toCents(value)
  return `${value < 0 && cents !== 0 ? MINUS_SIGN : ''}${formatCentsAsDollars(cents)}`
}

/** 差额：带正负号（`+$2.00` / `−$3.50`），已平为 `$0.00`，空值给占位符。 */
export function formatSignedMoney(value: number | null | undefined, placeholder = EMPTY_VALUE): string {
  if (!isFiniteNumber(value)) return placeholder
  const cents = toCents(value)
  if (cents === 0) return formatCentsAsDollars(0)
  return `${value < 0 ? MINUS_SIGN : '+'}${formatCentsAsDollars(cents)}`
}

/** 整数/小数计数（订单数、件数）：千分位，空值给占位符。 */
export function formatCount(value: number | null | undefined, placeholder = EMPTY_VALUE): string {
  if (!isFiniteNumber(value)) return placeholder
  const rounded = Math.round(value * 100) / 100
  const [integerPart, decimalPart] = String(Math.abs(rounded)).split('.')
  const grouped = integerPart.replace(/\B(?=(\d{3})+(?!\d))/g, ',')
  return `${rounded < 0 ? MINUS_SIGN : ''}${grouped}${decimalPart ? `.${decimalPart}` : ''}`
}

/** 按差额符号归类（与后端一致：NULL=none，<0 short，>0 over，=0 even）；按分比较，避免 0.004 之类的残差。 */
export function classifyDifference(difference: number | null | undefined): DailyCloseDifferenceKind {
  if (!isFiniteNumber(difference)) return 'none'
  if (toCents(difference) === 0) return 'even'
  return difference < 0 ? 'short' : 'over'
}

export function isDifferenceKind(value: unknown): value is DailyCloseDifferenceKind {
  return value === 'short' || value === 'over' || value === 'even' || value === 'none'
}

/** 状态图标：与文字、正负号一起出现，色弱也能分清。 */
export const DIFFERENCE_GLYPHS: Record<DailyCloseDifferenceKind, string> = {
  short: '▼',
  over: '▲',
  even: '✓',
  none: '–',
}

// ───────────────────────── 分页与查询参数 ─────────────────────────

export interface DailyCloseFilters {
  businessDateFrom: string
  businessDateTo: string
  storeCodes: string[]
  deviceCode: string
  clientKind?: DailyCloseClientKind
  keyword: string
}

export function createDefaultFilters(todayIso: string): DailyCloseFilters {
  const [businessDateFrom, businessDateTo] = getDefaultBusinessDateRange(todayIso)
  return { businessDateFrom, businessDateTo, storeCodes: [], deviceCode: '', clientKind: undefined, keyword: '' }
}

export function isStatusFilter(value: unknown): value is DailyCloseStatusFilter {
  return typeof value === 'string' && (STATUS_TABS as readonly string[]).includes(value)
}

/** 页签 → 查询参数：`all` 不传，由后端按默认处理。 */
export function statusToQueryParam(status: DailyCloseStatusFilter): DailyCloseStatusFilter | undefined {
  return status === 'all' ? undefined : status
}

function trimToUndefined(value: string | undefined): string | undefined {
  const trimmed = value?.trim()
  return trimmed ? trimmed : undefined
}

export function buildListQuery(
  filters: DailyCloseFilters,
  status: DailyCloseStatusFilter,
  page: number,
  pageSize: number,
): DailyCloseListQuery {
  const storeCodes = Array.from(new Set(filters.storeCodes.map((code) => code.trim()).filter(Boolean)))
  return {
    businessDateFrom: filters.businessDateFrom,
    businessDateTo: filters.businessDateTo,
    storeCodes: storeCodes.length ? storeCodes.join(',') : undefined,
    deviceCode: trimToUndefined(filters.deviceCode),
    clientKind: filters.clientKind,
    keyword: trimToUndefined(filters.keyword),
    status: statusToQueryParam(status),
    page,
    pageSize,
  }
}

/** 当前页的「第 a–b 条」；无数据时为 0–0。 */
export function getPageRange(page: number, pageSize: number, total: number): { from: number; to: number } {
  if (total <= 0) return { from: 0, to: 0 }
  return { from: (page - 1) * pageSize + 1, to: Math.min(page * pageSize, total) }
}

/** 页签右侧合计只在有意义时显示：无记录、或「无金额」页签（这些记录本来就不计入合计）都不显示。 */
export function shouldShowTotals(status: DailyCloseStatusFilter, counts: DailyCloseCounts | null): boolean {
  if (!counts) return false
  if (status === 'none') return false
  return counts[status] > 0
}

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

// ───────────────────────── 地址栏中的抽屉状态 ─────────────────────────

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** 地址栏 `?id=` → 合法日结 GUID（统一小写）；不合法一律当作没有。 */
export function parseDetailIdParam(value: string | null | undefined): string | null {
  const trimmed = value?.trim()
  return trimmed && GUID_PATTERN.test(trimmed) ? trimmed.toLowerCase() : null
}

/** 在保留其他查询参数的前提下写入/移除 `id`。 */
export function withDetailIdParam(current: URLSearchParams, id: string | null): URLSearchParams {
  const next = new URLSearchParams(current)
  if (id) next.set(DETAIL_QUERY_PARAM, id)
  else next.delete(DETAIL_QUERY_PARAM)
  return next
}

/** 日结编号简写：GUID 前 8 位大写（`#A3F29C01`），用于抽屉标题与行内核对。 */
export function shortGuid(guid: string): string {
  return guid.replace(/-/g, '').slice(0, 8).toUpperCase()
}

// ───────────────────────── 时间（门店本地时区） ─────────────────────────

const validTimeZoneCache = new Map<string, boolean>()
const dateTimeFormatterCache = new Map<string, Intl.DateTimeFormat>()

function isValidTimeZone(timeZone: string): boolean {
  const cached = validTimeZoneCache.get(timeZone)
  if (cached !== undefined) return cached
  let valid = true
  try {
    new Intl.DateTimeFormat('en-CA', { timeZone })
  } catch {
    valid = false
  }
  validTimeZoneCache.set(timeZone, valid)
  return valid
}

/** 门店时区缺失或无法识别时回退到悉尼。 */
export function resolveTimeZone(timeZoneId: string | null | undefined): string {
  const trimmed = timeZoneId?.trim()
  return trimmed && isValidTimeZone(trimmed) ? trimmed : FALLBACK_TIME_ZONE
}

function getDateTimeFormatter(timeZone: string): Intl.DateTimeFormat {
  let formatter = dateTimeFormatterCache.get(timeZone)
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    })
    dateTimeFormatterCache.set(timeZone, formatter)
  }
  return formatter
}

/** 后端 DateTime 带 Z；万一缺少时区后缀，一律按 UTC 解释。 */
export function parseUtcTimestamp(value: string | null | undefined): Date | null {
  if (!value) return null
  const normalized = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value) ? value : `${value}Z`
  const date = new Date(normalized)
  return Number.isNaN(date.getTime()) ? null : date
}

export type TimeFormat = 'short' | 'minutes' | 'seconds'

/**
 * 按门店本地时区显示 UTC 时间。
 * short：`MM-DD HH:mm`（列表）；minutes：`YYYY-MM-DD HH:mm`；seconds：`YYYY-MM-DD HH:mm:ss`（抽屉）。
 */
export function formatInStoreTime(
  value: string | null | undefined,
  timeZoneId: string | null | undefined,
  format: TimeFormat = 'seconds',
): string {
  const date = parseUtcTimestamp(value)
  if (!date) return EMPTY_VALUE
  const parts: Record<string, string> = {}
  for (const part of getDateTimeFormatter(resolveTimeZone(timeZoneId)).formatToParts(date)) {
    if (part.type !== 'literal') parts[part.type] = part.value
  }
  const day = `${parts.month}-${parts.day}`
  const clock = `${parts.hour}:${parts.minute}`
  if (format === 'short') return `${day} ${clock}`
  if (format === 'minutes') return `${parts.year}-${day} ${clock}`
  return `${parts.year}-${day} ${clock}:${parts.second}`
}

/** 营业日的星期（`周二` / `Tue`），按日期本身计算，不受浏览器时区影响。 */
export function formatWeekday(businessDate: string, locale: string): string {
  if (!isIsoDate(businessDate)) return ''
  const [year, month, day] = businessDate.split('-').map(Number)
  return new Intl.DateTimeFormat(locale, { weekday: 'short', timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, day)))
}

// ───────────────────────── 行与明细的标记判定 ─────────────────────────

/** 同日多次保存才标「第 N 次」；只保存过一次的不加标记。 */
export function getSaveSequenceBadge(item: Pick<DailyCloseListItem, 'saveSequence' | 'saveCountInDay'>): number | null {
  return item.saveCountInDay > 1 && item.saveSequence >= 1 ? item.saveSequence : null
}

/** 补录：来自操作日志回填，或明细不是完整上传。 */
export function isBackfilled(item: Pick<DailyCloseListItem, 'dataSource' | 'detailLevel'>): boolean {
  return item.dataSource === 'AuditBackfill' || item.detailLevel !== 'Full'
}

export type DetailNotice = 'cashOnly' | 'traceOnly' | null

/** 抽屉顶部蓝色提示条的类型：Full 没有；CashOnly 只补出现金三项；TraceOnly 只有保存记录。 */
export function getDetailNotice(item: Pick<DailyCloseListItem, 'detailLevel'>): DetailNotice {
  if (item.detailLevel === 'CashOnly') return 'cashOnly'
  if (item.detailLevel === 'TraceOnly') return 'traceOnly'
  return null
}

export function hasCashFigures(item: Pick<DailyCloseListItem, 'expectedCashAmount' | 'countedCashAmount' | 'cashDifference'>): boolean {
  return isFiniteNumber(item.cashDifference) && isFiniteNumber(item.expectedCashAmount) && isFiniteNumber(item.countedCashAmount)
}

// ───────────────────────── 明细：支付方式与盘点 ─────────────────────────

const TENDER_ORDER = ['Cash', 'Card', 'Voucher']

export interface TenderSummary {
  rows: DailyCloseTender[]
  total: { salesAmount: number; refundAmount: number; netAmount: number }
}

/** 支付方式按 现金 / 刷卡 / 代金券 固定顺序，合计行按行累加（按分求和，避免浮点误差）。 */
export function buildTenderSummary(tenders: DailyCloseTender[]): TenderSummary {
  const rank = (method: string) => {
    const index = TENDER_ORDER.indexOf(method)
    return index === -1 ? TENDER_ORDER.length : index
  }
  const rows = [...tenders].sort((left, right) => rank(left.method) - rank(right.method))
  const sumCents = (pick: (row: DailyCloseTender) => number) =>
    rows.reduce((sum, row) => sum + Math.round(pick(row) * 100), 0) / 100
  return {
    rows,
    total: {
      salesAmount: sumCents((row) => row.salesAmount),
      refundAmount: sumCents((row) => row.refundAmount),
      netAmount: sumCents((row) => row.netAmount),
    },
  }
}

/** 澳元纸币 5 档、硬币 6 档（单位：分）。 */
export const NOTE_DENOMINATIONS_CENTS = [10000, 5000, 2000, 1000, 500] as const
export const COIN_DENOMINATIONS_CENTS = [200, 100, 50, 20, 10, 5] as const
/** 与后端 DailyCloseQueryService 一致：面额不小于 5 元按纸币汇总。 */
const NOTE_MIN_DENOMINATION_CENTS = 500

export interface CashCountRow {
  denominationCents: number
  quantity: number
  subtotalAmount: number
}

export interface CashCountSection {
  rows: CashCountRow[]
  subtotal: number
}

export interface CashCountSections {
  notes: CashCountSection
  coins: CashCountSection
}

function buildCashSection(
  counts: DailyCloseCashCount[],
  standardDenominations: readonly number[],
  serverSubtotal: number | null | undefined,
): CashCountSection {
  const byDenomination = new Map<number, CashCountRow>()
  for (const denomination of standardDenominations) {
    // 标准面额先补 0 行：0 张灰显但保留，核对时不会漏看。
    byDenomination.set(denomination, { denominationCents: denomination, quantity: 0, subtotalAmount: 0 })
  }
  for (const count of counts) {
    byDenomination.set(count.denominationCents, {
      denominationCents: count.denominationCents,
      quantity: count.quantity,
      subtotalAmount: count.subtotalAmount,
    })
  }
  const rows = [...byDenomination.values()].sort((left, right) => right.denominationCents - left.denominationCents)
  const computed = rows.reduce((sum, row) => sum + Math.round(row.subtotalAmount * 100), 0) / 100
  return { rows, subtotal: isFiniteNumber(serverSubtotal) ? serverSubtotal : computed }
}

/**
 * 盘点明细分纸币/硬币两栏。后端没给面额数据（空数组）时返回 null，由界面显示「暂无数据」占位，
 * 不能用全 0 的表去冒充「盘点过且为 0」。
 */
export function buildCashCountSections(
  counts: DailyCloseCashCount[],
  noteSubtotal: number | null | undefined,
  coinSubtotal: number | null | undefined,
): CashCountSections | null {
  if (!counts.length) return null
  const isNote = (count: DailyCloseCashCount) =>
    count.kind === 'Note' || (count.kind !== 'Coin' && count.denominationCents >= NOTE_MIN_DENOMINATION_CENTS)
  return {
    notes: buildCashSection(counts.filter(isNote), NOTE_DENOMINATIONS_CENTS, noteSubtotal),
    coins: buildCashSection(counts.filter((count) => !isNote(count)), COIN_DENOMINATIONS_CENTS, coinSubtotal),
  }
}

/** 面额显示：≥ $1 用 `$100`，其余用 `50c`。 */
export function formatDenomination(cents: number): string {
  return cents >= 100 ? `$${cents / 100}` : `${cents}c`
}

// ───────────────────────── 合计展示 ─────────────────────────

export interface TotalsView {
  expected: string
  counted: string
  difference: string
  differenceKind: DailyCloseDifferenceKind
}

export function buildTotalsView(totals: DailyCloseTotals): TotalsView {
  return {
    expected: formatMoney(totals.expectedCash),
    counted: formatMoney(totals.countedCash),
    difference: formatSignedMoney(totals.difference),
    differenceKind: classifyDifference(totals.difference),
  }
}

// ───────────────────────── 错误分类 ─────────────────────────

export type DailyCloseErrorKind = 'forbidden' | 'notFound' | 'invalidQuery' | 'failed'

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

/**
 * 把请求异常归成几类，界面按类给出明确文案：
 * 403 无权限；404 不存在或无分店权限（后端不区分，以免泄露存在性）；400 INVALID_QUERY；其余为通用失败（可重试）。
 */
export function classifyDailyCloseError(error: unknown): DailyCloseErrorKind {
  const { status, payload } = (typeof error === 'object' && error !== null ? error : {}) as ErrorLike
  if (status === 403) return 'forbidden'
  if (status === 404) return 'notFound'
  if (status === 400 && readErrorCode(payload) === 'INVALID_QUERY') return 'invalidQuery'
  return 'failed'
}
