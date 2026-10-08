import {
  CASH_EXPENSE_CATEGORIES,
  CASH_REVIEW_STATUSES,
  type CashDailyRow,
  type CashExpenseCategory,
  type CashExpenseCategoryTotal,
  type CashExpenseListItem,
  type CashReviewStatus,
  type CashStoreOption,
} from '../../../types/storeCash'

/**
 * 现金管理页的纯逻辑：日期区间、地址栏参数、金额格式、合计、错误码映射。
 * 不依赖 React、i18n 与 DOM，直接用 Node 单测。
 *
 * 业务约定（与后端一致）：
 * - 日期一律是门店本地日期 yyyy-MM-dd，按 UTC 日历计算加减，不受浏览器时区影响；
 * - 区间含两端，最多 93 天（后端判断 to − from < 93）；
 * - 金额先换成整数「分」再累加，避免浮点误差；可空金额（日结未接入）是 null，显示「—」，绝不当 0。
 */

export const STORE_CASH_PATH = '/pos-admin/store-cash'
export const MAX_RANGE_DAYS = 93
/** 导出与支出合计一次最多取的行数；超出时提示缩小范围。 */
export const EXPORT_MAX_ROWS = 5000
export const EMPTY_CELL = '—'
/** 后端没下发 t2VisibleDays 时的兜底（StoreCashConstants.ManagerT2VisibleDays）。 */
export const DEFAULT_T2_VISIBLE_DAYS = 14

export const CASH_TABS = ['overview', 'daily', 'deposits', 'expenses'] as const
export type CashTab = (typeof CASH_TABS)[number]

// ---------------------------------------------------------------------------
// 日期
// ---------------------------------------------------------------------------

const isoDatePattern = /^(\d{4})-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$/

function toUtcDay(date: string): number | null {
  const match = isoDatePattern.exec(date)
  if (!match) return null
  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  const time = Date.UTC(year, month - 1, day)
  const check = new Date(time)
  // 排除 2026-02-30 这类格式合法但不存在的日期。
  if (check.getUTCFullYear() !== year || check.getUTCMonth() !== month - 1 || check.getUTCDate() !== day) return null
  return Math.floor(time / 86_400_000)
}

function fromUtcDay(dayNumber: number): string {
  const date = new Date(dayNumber * 86_400_000)
  const year = String(date.getUTCFullYear()).padStart(4, '0')
  const month = String(date.getUTCMonth() + 1).padStart(2, '0')
  const day = String(date.getUTCDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function isIsoDate(value: unknown): value is string {
  return typeof value === 'string' && toUtcDay(value) !== null
}

export function addDays(date: string, days: number): string {
  const dayNumber = toUtcDay(date)
  if (dayNumber === null) throw new Error(`Invalid date: ${date}`)
  return fromUtcDay(dayNumber + days)
}

/** 区间天数（含两端）；日期非法或倒序时返回 0。 */
export function rangeDayCount(from: string, to: string): number {
  const start = toUtcDay(from)
  const end = toUtcDay(to)
  if (start === null || end === null || end < start) return 0
  return end - start + 1
}

export function firstOfMonth(date: string): string {
  return `${date.slice(0, 7)}-01`
}

/** 默认区间：本月 1 日 ~ 今天（today 为门店本地日期）。 */
export function defaultRange(today: string): { from: string; to: string } {
  return { from: firstOfMonth(today), to: today }
}

export type RangeProblem = 'invalid' | 'order' | 'tooLong'

/** 校验区间：日期合法、起不晚于止、含两端不超过 93 天。 */
export function checkRange(from: string | undefined, to: string | undefined): RangeProblem | null {
  if (!isIsoDate(from) || !isIsoDate(to)) return 'invalid'
  if (to < from) return 'order'
  if (rangeDayCount(from, to) > MAX_RANGE_DAYS) return 'tooLong'
  return null
}

/** 选择器里禁选的日期：晚于门店今天；已选一端时，另一端超出 93 天窗口的也禁选。 */
export function isDateDisabled(date: string, today: string, anchor?: string | null): boolean {
  if (!isIsoDate(date)) return true
  if (isIsoDate(today) && date > today) return true
  if (anchor && isIsoDate(anchor)) {
    const count = date < anchor ? rangeDayCount(date, anchor) : rangeDayCount(anchor, date)
    return count > MAX_RANGE_DAYS
  }
  return false
}

/** 页面的「今天」：取可见分店里最晚的门店本地日期（各店时区差不超过一天，与后端总览默认区间一致）。 */
export function referenceToday(stores: readonly Pick<CashStoreOption, 'storeToday'>[], fallback: string): string {
  let latest: string | null = null
  for (const store of stores) {
    if (isIsoDate(store.storeToday) && (latest === null || store.storeToday > latest)) latest = store.storeToday
  }
  return latest ?? fallback
}

const WEEKDAY_KEYS = [
  'storeCash.weekday.sun',
  'storeCash.weekday.mon',
  'storeCash.weekday.tue',
  'storeCash.weekday.wed',
  'storeCash.weekday.thu',
  'storeCash.weekday.fri',
  'storeCash.weekday.sat',
] as const

/** 星期文案键（业务日期按 UTC 日历取星期）。 */
export function weekdayKeyOf(date: string): string {
  const dayNumber = toUtcDay(date)
  if (dayNumber === null) return WEEKDAY_KEYS[0]
  // 1970-01-01 是周四。
  return WEEKDAY_KEYS[(dayNumber + 4) % 7]
}

// ---------------------------------------------------------------------------
// 地址栏参数：页签与筛选写进 query，刷新、分享可恢复
// ---------------------------------------------------------------------------

export interface CashPageQuery {
  tab: CashTab
  /** 区间：两端要么都有且合法，要么都没有（用默认区间）。所有页签共用。 */
  from?: string
  to?: string
  /** 总览的分店多选；空数组 = 全部可见分店。 */
  stores: string[]
  /** 按日明细 / 存款 / 支出共用的单店。 */
  store?: string
  category?: CashExpenseCategory
  review?: CashReviewStatus
  /** 存款、支出是否包含已作废记录。 */
  voided: boolean
  /** 收银系统筛选：缺省 = 只看启用收银系统的分店；off = 只看未启用；all = 全部。所有页签共用。 */
  register?: CashRegisterFilter
}

export const CASH_REGISTER_FILTERS = ['on', 'off', 'all'] as const
export type CashRegisterFilter = (typeof CASH_REGISTER_FILTERS)[number]

function isRegisterFilter(value: unknown): value is CashRegisterFilter {
  return typeof value === 'string' && (CASH_REGISTER_FILTERS as readonly string[]).includes(value)
}

/**
 * 按收银系统状态筛出页面可选的分店（保持原顺序）。
 * 只有一家可见分店时不筛：此时页面不显示分店与收银系统筛选，筛掉就再也选不回来。
 */
export function filterStoresByRegister(
  stores: readonly CashStoreOption[],
  register: CashRegisterFilter | undefined,
): CashStoreOption[] {
  const mode = register ?? 'on'
  if (mode === 'all' || stores.length <= 1) return [...stores]
  const wantEnabled = mode === 'on'
  return stores.filter((store) => store.cashRegisterEnabled === wantEnabled)
}

export const DEFAULT_CASH_QUERY: CashPageQuery = Object.freeze({ tab: 'overview', stores: [], voided: false }) as CashPageQuery

function isCashTab(value: string | null): value is CashTab {
  return value !== null && (CASH_TABS as readonly string[]).includes(value)
}

export function isExpenseCategory(value: unknown): value is CashExpenseCategory {
  return typeof value === 'string' && (CASH_EXPENSE_CATEGORIES as readonly string[]).includes(value)
}

export function isReviewStatus(value: unknown): value is CashReviewStatus {
  return typeof value === 'string' && (CASH_REVIEW_STATUSES as readonly string[]).includes(value)
}

function cleanCode(value: string | null): string | undefined {
  const code = value?.trim()
  return code ? code : undefined
}

/** 解析地址栏参数；非法值一律丢弃并回到默认值，不报错。 */
export function parseCashSearch(search: string): CashPageQuery {
  const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search)
  const tabParam = params.get('tab')
  const from = params.get('from') ?? undefined
  const to = params.get('to') ?? undefined
  // 区间要整体合法才保留，否则两端都丢弃（用默认区间），避免出现只剩一端的半个区间。
  const rangeOk = checkRange(from, to) === null
  const stores = [...new Set(params.getAll('stores').map(cleanCode).filter((code): code is string => Boolean(code)))]
  const category = params.get('category')
  const review = params.get('review')
  const voided = params.get('voided')
  const register = params.get('register')
  return {
    tab: isCashTab(tabParam) ? tabParam : 'overview',
    ...(rangeOk ? { from, to } : {}),
    stores,
    ...(cleanCode(params.get('store')) ? { store: cleanCode(params.get('store')) } : {}),
    ...(isExpenseCategory(category) ? { category } : {}),
    ...(isReviewStatus(review) ? { review } : {}),
    voided: voided === '1' || voided === 'true',
    // 缺省「只看启用」不写进对象，与默认值省略的其它参数一致。
    ...(isRegisterFilter(register) && register !== 'on' ? { register } : {}),
  }
}

/** 序列化成稳定顺序的 query 字符串（不含「?」）；默认值省略。parse(serialize(q)) 与 q 等价。 */
export function serializeCashQuery(query: CashPageQuery): string {
  const params = new URLSearchParams()
  params.set('tab', query.tab)
  if (checkRange(query.from, query.to) === null) {
    params.set('from', query.from as string)
    params.set('to', query.to as string)
  }
  for (const code of query.stores) params.append('stores', code)
  if (query.store) params.set('store', query.store)
  if (query.category) params.set('category', query.category)
  if (query.review) params.set('review', query.review)
  if (query.voided) params.set('voided', '1')
  if (query.register && query.register !== 'on') params.set('register', query.register)
  return params.toString()
}

export interface ResolvedCashFilters {
  /** 日期上限与默认区间的「今天」：总览为各店最晚的门店日期，单店页签为该店的门店日期。 */
  today: string
  from: string
  to: string
  /** 按收银系统筛选后可选的分店：分店下拉只列这些；为空时总览不取数、单店页签无分店。 */
  storeOptions: CashStoreOption[]
  /**
   * 总览实际查询的分店：只保留筛选后可选的；空数组 = 全部可见分店。
   * 收银系统筛选收窄了范围而又没选具体分店时，显式列出筛选后的全部分店（后端空数组表示全部）。
   */
  storeCodes: string[]
  /** 总览分店多选框显示的值：只是用户选中且仍在筛选范围内的分店，不含上面为收窄而补全的列表。 */
  selectedStoreCodes: string[]
  /** 单店页签实际查询的分店：地址里的分店不可见时回到第一家；没有可见分店时为 null。 */
  storeCode: string | null
}

/** 把地址栏参数与 context（可见分店、门店今天）合成实际查询条件。 */
export function resolveCashFilters(
  query: CashPageQuery,
  stores: readonly CashStoreOption[],
  fallbackToday: string,
): ResolvedCashFilters {
  const storeOptions = filterStoresByRegister(stores, query.register)
  const visible = new Set(storeOptions.map((store) => store.storeCode))
  const picked = query.stores.filter((code) => visible.has(code))
  const narrowed = storeOptions.length < stores.length
  const storeCodes = picked.length > 0 || !narrowed ? picked : storeOptions.map((store) => store.storeCode)
  const storeCode = query.store && visible.has(query.store) ? query.store : storeOptions[0]?.storeCode ?? null
  // 「今天」：总览取各店最晚的门店日期（与后端总览默认区间一致）；单店页签取该店自己的今天，
  // 避免珀斯等时区较晚的分店默认区间里出现还没到的日子。
  const ownStore = query.tab === 'overview' ? undefined : stores.find((store) => store.storeCode === storeCode)
  const today = ownStore && isIsoDate(ownStore.storeToday) ? ownStore.storeToday : referenceToday(stores, fallbackToday)
  const range = checkRange(query.from, query.to) === null
    ? { from: query.from as string, to: query.to as string }
    : defaultRange(today)
  return { today, ...range, storeOptions, storeCodes, selectedStoreCodes: picked, storeCode }
}

// ---------------------------------------------------------------------------
// 金额
// ---------------------------------------------------------------------------

/** 元 → 整数分（四舍五入，正负对称）；null / 非有限数返回 null。 */
export function toCents(value: number | null | undefined): number | null {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null
  // 先按 6 位小数取整再四舍五入，避免 1.005 * 100 = 100.4999… 这类浮点误差。
  const scaled = Number((Math.abs(value) * 100).toFixed(6))
  const cents = Math.round(scaled)
  return value < 0 && cents !== 0 ? -cents : cents
}

const audFormatter = new Intl.NumberFormat('en-AU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

/** 澳元显示：$1,234.50 / -$12.00；null 显示「—」。 */
export function formatAud(value: number | null | undefined): string {
  const cents = toCents(value)
  if (cents === null) return EMPTY_CELL
  return `${cents < 0 ? '-' : ''}$${audFormatter.format(Math.abs(cents) / 100)}`
}

/** 带正负号的差异显示：+$12.00 / -$5.50 / $0.00；null 显示「—」。 */
export function formatSignedAud(value: number | null | undefined): string {
  const cents = toCents(value)
  if (cents === null) return EMPTY_CELL
  return cents > 0 ? `+${formatAud(value)}` : formatAud(value)
}

/** 负数（短款、负余额）需要醒目显示。 */
export function isNegativeAmount(value: number | null | undefined): boolean {
  const cents = toCents(value)
  return cents !== null && cents < 0
}

export function formatCount(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString('en-US') : EMPTY_CELL
}

/** 表格排序：null 排在最后，其余按数值。 */
export function compareNullableNumber(left: number | null | undefined, right: number | null | undefined): number {
  const leftMissing = typeof left !== 'number' || !Number.isFinite(left)
  const rightMissing = typeof right !== 'number' || !Number.isFinite(right)
  if (leftMissing || rightMissing) return leftMissing === rightMissing ? 0 : leftMissing ? 1 : -1
  return (left as number) - (right as number)
}

// ---------------------------------------------------------------------------
// 时间：UTC → 门店本地时间
// ---------------------------------------------------------------------------

const DEFAULT_TIME_ZONE = 'Australia/Sydney'
const dateTimeFormatters = new Map<string, Intl.DateTimeFormat>()

function dateTimeFormatter(timeZone: string): Intl.DateTimeFormat {
  const cached = dateTimeFormatters.get(timeZone)
  if (cached) return cached
  let formatter: Intl.DateTimeFormat
  try {
    formatter = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hourCycle: 'h23',
    })
  } catch {
    // 时区标识非法（如后端给的是 Windows 时区名）时按悉尼时间显示。
    return dateTimeFormatter(DEFAULT_TIME_ZONE)
  }
  dateTimeFormatters.set(timeZone, formatter)
  return formatter
}

/** UTC 时间按门店时区显示为 yyyy-MM-dd HH:mm；空值或非法值显示「—」。 */
export function formatStoreDateTime(utc: string | null | undefined, timeZone?: string | null): string {
  if (!utc) return EMPTY_CELL
  const date = new Date(utc)
  if (Number.isNaN(date.getTime())) return EMPTY_CELL
  const parts = Object.fromEntries(
    dateTimeFormatter(timeZone?.trim() || DEFAULT_TIME_ZONE).formatToParts(date).map((part) => [part.type, part.value]),
  )
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`
}

/** 分店编码 → 时区；找不到时用悉尼。 */
export function timeZoneOf(stores: readonly CashStoreOption[], storeCode: string | null | undefined): string {
  return stores.find((store) => store.storeCode === storeCode)?.timeZoneId || DEFAULT_TIME_ZONE
}

// ---------------------------------------------------------------------------
// 文案键（完整字面量，便于契约测试静态核对）
// ---------------------------------------------------------------------------

export const CATEGORY_LABEL_KEYS: Readonly<Record<CashExpenseCategory, string>> = {
  Salary: 'storeCash.category.Salary',
  Purchase: 'storeCash.category.Purchase',
  T2: 'storeCash.category.T2',
  Other: 'storeCash.category.Other',
}

export const REVIEW_LABEL_KEYS: Readonly<Record<CashReviewStatus, string>> = {
  None: 'storeCash.review.None',
  Reviewed: 'storeCash.review.Reviewed',
  Flagged: 'storeCash.review.Flagged',
}

/** 类别文案键；未知类别码原样显示（返回 null 由调用方兜底）。 */
export function categoryLabelKey(category: string): string | null {
  return isExpenseCategory(category) ? CATEGORY_LABEL_KEYS[category] : null
}

export function reviewLabelKey(status: string): string {
  return isReviewStatus(status) ? REVIEW_LABEL_KEYS[status] : REVIEW_LABEL_KEYS.None
}

/** 核对状态 → 胶囊颜色：存疑红、已核绿、未核对灰。 */
export function reviewTone(status: string): 'red' | 'green' | 'gray' {
  if (status === 'Flagged') return 'red'
  if (status === 'Reviewed') return 'green'
  return 'gray'
}

export function isVoided(record: { status: string }): boolean {
  return record.status === 'Voided'
}

export function recordStatusKey(record: { status: string }): string {
  return isVoided(record) ? 'storeCash.status.Voided' : 'storeCash.status.Active'
}

export function selectionModeKey(mode: string): string {
  return mode === 'Manual' ? 'storeCash.selection.Manual' : 'storeCash.selection.Default'
}

// ---------------------------------------------------------------------------
// 合计
// ---------------------------------------------------------------------------

/** 从类别合计数组取某一类；缺这一类时返回 null（显示「—」，不臆造 0）。 */
export function categoryAmount(
  totals: readonly CashExpenseCategoryTotal[],
  category: CashExpenseCategory,
): number | null {
  const match = totals.find((item) => item.category === category)
  return match && typeof match.amount === 'number' ? match.amount : null
}

export interface ExpenseSummary {
  /** 各类合计（元）；只含有效记录。 */
  byCategory: Record<CashExpenseCategory, number>
  total: number
  /** 计入合计的有效记录数。 */
  count: number
  /** 列表里已作废、不计入合计的记录数（打开「含作废」时才会有）。 */
  voidedCount: number
  flaggedCount: number
}

/** 支出合计条：当前筛选下的全部记录，按分累加；已作废记录不计入金额。 */
export function summarizeExpenses(items: readonly CashExpenseListItem[]): ExpenseSummary {
  const cents: Record<CashExpenseCategory, number> = { Salary: 0, Purchase: 0, T2: 0, Other: 0 }
  let totalCents = 0
  let count = 0
  let voidedCount = 0
  let flaggedCount = 0
  for (const item of items) {
    if (isVoided(item)) {
      voidedCount += 1
      continue
    }
    const amount = toCents(item.amount)
    if (amount === null) continue
    count += 1
    totalCents += amount
    if (isExpenseCategory(item.category)) cents[item.category] += amount
    if (item.reviewStatus === 'Flagged') flaggedCount += 1
  }
  return {
    byCategory: {
      Salary: cents.Salary / 100,
      Purchase: cents.Purchase / 100,
      T2: cents.T2 / 100,
      Other: cents.Other / 100,
    },
    total: totalCents / 100,
    count,
    voidedCount,
    flaggedCount,
  }
}

export interface DailySummary {
  /** 区间日结现金合计；日结未接入时为 null。 */
  inflow: number | null
  closeDays: number
  /** 有日结、但还没被存款覆盖的营业日数。 */
  uncoveredDays: number
  expenseTotal: number
}

export function summarizeDaily(rows: readonly CashDailyRow[], dailyCloseConnected: boolean): DailySummary {
  let inflowCents = 0
  let expenseCents = 0
  let closeDays = 0
  let uncoveredDays = 0
  for (const row of rows) {
    inflowCents += toCents(row.inflowCash) ?? 0
    expenseCents += toCents(row.expenseTotal) ?? 0
    if (row.hasClose) {
      closeDays += 1
      if (!row.covered) uncoveredDays += 1
    }
  }
  return {
    inflow: dailyCloseConnected ? inflowCents / 100 : null,
    closeDays: dailyCloseConnected ? closeDays : 0,
    uncoveredDays: dailyCloseConnected ? uncoveredDays : 0,
    expenseTotal: expenseCents / 100,
  }
}

/** 逐日行按营业日升序（账本顺序）。 */
export function sortDailyRows(rows: readonly CashDailyRow[]): CashDailyRow[] {
  return [...rows].sort((left, right) => left.businessDate.localeCompare(right.businessDate))
}

/** 日结未接入时逐日的日结现金不可算，显示「—」而不是 0。 */
export function dailyInflow(row: Pick<CashDailyRow, 'inflowCash'>, dailyCloseConnected: boolean): number | null {
  return dailyCloseConnected ? row.inflowCash : null
}

// ---------------------------------------------------------------------------
// 作废原因 / 核对说明
// ---------------------------------------------------------------------------

export const MIN_REASON_LENGTH = 2

/** 原因 / 说明至少 2 个字（按字符计，中文一个字算一个）。 */
export function isReasonValid(text: string | null | undefined): boolean {
  return [...(text ?? '').trim()].length >= MIN_REASON_LENGTH
}

/** 核对标记里只有「存疑」必须写说明。 */
export function reviewNoteRequired(status: CashReviewStatus): boolean {
  return status === 'Flagged'
}

// ---------------------------------------------------------------------------
// 错误码 → 文案
// ---------------------------------------------------------------------------

const ERROR_CODE_KEYS: Readonly<Record<string, string>> = {
  CASH_INVALID_REQUEST: 'storeCash.errors.invalidRequest',
  CASH_DATE_OUT_OF_RANGE: 'storeCash.errors.dateOutOfRange',
  CASH_STORE_NOT_FOUND: 'storeCash.errors.storeNotFound',
  CASH_STORE_FORBIDDEN: 'storeCash.errors.storeForbidden',
  CASH_RECORD_NOT_FOUND: 'storeCash.errors.recordNotFound',
  CASH_VOID_NOT_ALLOWED: 'storeCash.errors.voidNotAllowed',
  CASH_CLOSE_SOURCE_UNAVAILABLE: 'storeCash.errors.closeSourceUnavailable',
  CASH_CONFLICT: 'storeCash.errors.conflict',
  CASH_INTERNAL_ERROR: 'storeCash.errors.server',
}

export type ResolvedCashError =
  | { kind: 'abort' }
  | { kind: 'error'; key: string; code: string | null; status: number | null; serverMessage: string | null }

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function statusKey(status: number | null): string {
  if (status === null) return 'storeCash.errors.network'
  if (status === 400) return 'storeCash.errors.invalidRequest'
  if (status === 401 || status === 403) return 'storeCash.errors.storeForbidden'
  if (status === 404) return 'storeCash.errors.recordNotFound'
  if (status === 409) return 'storeCash.errors.conflict'
  if (status >= 500) return 'storeCash.errors.server'
  return 'storeCash.errors.unknown'
}

/**
 * 把请求异常归类：取消的请求不提示；有错误码按错误码，没有按 HTTP 状态；
 * 服务端 message 是可直接展示的中文，一并带回由界面按语言决定用哪个。
 */
export function resolveCashError(error: unknown): ResolvedCashError {
  if (isRecord(error) && error.name === 'AbortError') return { kind: 'abort' }
  if (isRecord(error) && typeof error.status === 'number') {
    const payload = isRecord(error.payload) ? error.payload : {}
    const rawCode = payload.errorCode ?? payload.code
    const code = typeof rawCode === 'string' && rawCode ? rawCode : null
    const message = typeof payload.message === 'string' && payload.message.trim() ? payload.message.trim() : null
    const status = error.status as number
    const key = (code && ERROR_CODE_KEYS[code]) || statusKey(status)
    return { kind: 'error', key, code, status, serverMessage: message }
  }
  // fetch 本身失败（断网、跨域、DNS）是 TypeError，没有响应。
  if (error instanceof TypeError) {
    return { kind: 'error', key: 'storeCash.errors.network', code: null, status: null, serverMessage: null }
  }
  return { kind: 'error', key: 'storeCash.errors.unknown', code: null, status: null, serverMessage: null }
}

/** 中文界面优先用服务端的具体中文 message；英文界面用错误码对应的英文文案。 */
export function chooseCashErrorText(
  resolved: Extract<ResolvedCashError, { kind: 'error' }>,
  language: string,
  translate: (key: string) => string,
): string {
  if (language.toLowerCase().startsWith('zh') && resolved.serverMessage) return resolved.serverMessage
  return translate(resolved.key)
}

// ---------------------------------------------------------------------------
// 导出文件名
// ---------------------------------------------------------------------------

/** 文件名里不能出现的字符替换成下划线。 */
function safeFilePart(value: string): string {
  return value.replace(/[\\/:*?"<>|\s]+/g, '_')
}

/** 现金总览_2026-10-01_2026-10-08.csv、现金存款_S001_2026-10-01_2026-10-08.csv */
export function buildCashExportFileName(prefix: string, from: string, to: string, storeCode?: string | null): string {
  const store = storeCode ? `${safeFilePart(storeCode)}_` : ''
  return `${safeFilePart(prefix)}_${store}${from}_${to}.csv`
}
