import type {
  MonthlyStoreDailySales,
  MonthlyStoreDailySalesStore,
} from '../../../services/monthlyDailySalesService'

/**
 * 月度日销售下载的纯逻辑：月份边界、按「分」累加、缺数 / 无支付方式统计、文件名与工作表名、CSV。
 * 不依赖 React、i18n 与 DOM，便于直接用 Node 单测。
 *
 * 金额一律先转成整数「分」再累加，最后才换回元，避免浮点误差让合计与明细对不上。
 * 空值语义（绝不能当 0）：
 * - revenue 为 null  → 该日统计尚未发布（缺数），不计入任何合计；
 * - revenue 有值但刷卡 / 现金 / 其他不全 → 该日没有可靠的支付方式拆分，营业额照常计入，
 *   刷卡 / 现金 / 其他三列的合计不含这些日子；
 * - 休业日是 0，正常计入。
 */

export const MIN_MONTH = '2025-01'

export type DownloadFormat = 'xlsx' | 'csv'

/** 每天的数据状态：ok 完整；noSplit 有营业额但无可靠的支付方式拆分；missing 缺数。 */
export type DayStatus = 'ok' | 'noSplit' | 'missing'

// ---------------------------------------------------------------------------
// 月份
// ---------------------------------------------------------------------------

const monthPattern = /^(\d{4})-(0[1-9]|1[0-2])$/

export function parseMonth(month: string): { year: number; month: number } | null {
  const match = monthPattern.exec(month)
  return match ? { year: Number(match[1]), month: Number(match[2]) } : null
}

export function formatMonth(year: number, month: number): string {
  return `${String(year).padStart(4, '0')}-${String(month).padStart(2, '0')}`
}

/** 该月天数（按 UTC 日历计算，不受浏览器时区影响）；月份非法时返回 0。 */
export function daysInMonth(month: string): number {
  const parsed = parseMonth(month)
  return parsed ? new Date(Date.UTC(parsed.year, parsed.month, 0)).getUTCDate() : 0
}

/** 当月（todayKey 为悉尼时区的 yyyy-MM-dd）。 */
export function currentMonthOf(todayKey: string): string {
  return todayKey.slice(0, 7)
}

/** 把月份夹在 [MIN_MONTH, 当月] 之间；格式非法时回到当月。 */
export function clampMonth(month: string, todayKey: string): string {
  const latest = currentMonthOf(todayKey)
  if (!parseMonth(month)) return latest
  if (month < MIN_MONTH) return MIN_MONTH
  if (month > latest) return latest
  return month
}

/** 默认月份：悉尼时区的上一个整月；不早于最早可选月份。 */
export function defaultMonth(todayKey: string): string {
  const parsed = parseMonth(currentMonthOf(todayKey))
  if (!parsed) return MIN_MONTH
  const previous = parsed.month === 1 ? formatMonth(parsed.year - 1, 12) : formatMonth(parsed.year, parsed.month - 1)
  return previous < MIN_MONTH ? MIN_MONTH : previous
}

/** 步进后的月份；超出 [MIN_MONTH, 当月] 时返回 null。 */
export function shiftMonth(month: string, delta: number, todayKey: string): string | null {
  const parsed = parseMonth(month)
  if (!parsed) return null
  const index = parsed.year * 12 + (parsed.month - 1) + delta
  const next = formatMonth(Math.floor(index / 12), (index % 12) + 1)
  return next < MIN_MONTH || next > currentMonthOf(todayKey) ? null : next
}

/** 日期（yyyy-MM-dd）的星期，0 = 周日；按 UTC 计算，业务日期不带时区。 */
export function weekdayOf(date: string): number {
  const [year, month, day] = date.split('-').map(Number)
  return new Date(Date.UTC(year, month - 1, day)).getUTCDay()
}

export function isWeekend(weekday: number): boolean {
  return weekday === 0 || weekday === 6
}

// ---------------------------------------------------------------------------
// 金额：整数「分」
// ---------------------------------------------------------------------------

/** 元 → 分（四舍五入到整数分）；null / 非有限数返回 null。 */
export function toCents(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? Math.round(value * 100) : null
}

const dollarFormatter = new Intl.NumberFormat('en-AU', { maximumFractionDigits: 0 })
const amountFormatter = new Intl.NumberFormat('en-AU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

export const EMPTY_CELL = '—'

/** 整数美元显示，如 $456,756；null 显示「—」。 */
export function formatDollars(cents: number | null): string {
  if (cents === null) return EMPTY_CELL
  // 先取绝对值再四舍五入，正负对称（-1.5 → -$2），且不出现「-$0」。
  const dollars = Math.round(Math.abs(cents) / 100)
  return `${cents < 0 && dollars > 0 ? '-' : ''}$${dollarFormatter.format(dollars)}`
}

/** 两位小数、千分位，如 12,040.28；null 显示「—」。 */
export function formatAmount(cents: number | null): string {
  return cents === null ? EMPTY_CELL : amountFormatter.format(cents / 100)
}

/** 占比文字，如 70.1%；分母为 0 或不可用时显示「—」。 */
export function formatPercent(part: number, whole: number): string {
  return whole > 0 ? `${((part / whole) * 100).toFixed(1)}%` : EMPTY_CELL
}

// ---------------------------------------------------------------------------
// 每日行与合计
// ---------------------------------------------------------------------------

/** 一天的数据（金额为整数分）；缺数日全部为 null，无拆分日只有 revenue 有值。 */
export interface DayRow {
  date: string
  day: number
  weekday: number
  status: DayStatus
  revenue: number | null
  card: number | null
  cash: number | null
  other: number | null
}

export interface Totals {
  /** 营业额合计：含有营业额的所有日子（ok + noSplit） */
  revenue: number
  card: number
  cash: number
  other: number
  /** 有可靠拆分的日子的营业额，作为刷卡 / 现金 / 其他占比的分母 */
  splitRevenue: number
  /** 计入营业额的天数，为 0 表示一天都没有，合计应显示「—」而不是 0 */
  revenueDays: number
  /** 计入刷卡 / 现金 / 其他的天数 */
  splitDays: number
}

export const ZERO_TOTALS: Totals = Object.freeze({ revenue: 0, card: 0, cash: 0, other: 0, splitRevenue: 0, revenueDays: 0, splitDays: 0 })

export function addTotals(left: Totals, right: Totals): Totals {
  return {
    revenue: left.revenue + right.revenue,
    card: left.card + right.card,
    cash: left.cash + right.cash,
    other: left.other + right.other,
    splitRevenue: left.splitRevenue + right.splitRevenue,
    revenueDays: left.revenueDays + right.revenueDays,
    splitDays: left.splitDays + right.splitDays,
  }
}

export interface DisplayTotals {
  revenue: number | null
  card: number | null
  cash: number | null
  other: number | null
}

/** 没有任何贡献日的合计项为 null（显示「—」/ 文件里留空），不写成 0。 */
export function displayTotals(totals: Totals): DisplayTotals {
  return {
    revenue: totals.revenueDays > 0 ? totals.revenue : null,
    card: totals.splitDays > 0 ? totals.card : null,
    cash: totals.splitDays > 0 ? totals.cash : null,
    other: totals.splitDays > 0 ? totals.other : null,
  }
}

/** 已计入的最后一天是几号；没有已出数日子时为 0。 */
export function countedThroughDay(data: Pick<MonthlyStoreDailySales, 'countedThroughDate'>): number {
  return data.countedThroughDate ? Number(data.countedThroughDate.slice(8, 10)) : 0
}

/** 展开成 1 日 ~ throughDay 的逐日行：接口没返回的日子按缺数处理，不会凭空补 0。 */
export function buildDayRows(store: MonthlyStoreDailySalesStore, month: string, throughDay: number): DayRow[] {
  const byDate = new Map(store.days.map(day => [day.date, day]))
  const rows: DayRow[] = []
  for (let day = 1; day <= throughDay; day += 1) {
    const date = `${month}-${String(day).padStart(2, '0')}`
    const source = byDate.get(date)
    const revenue = toCents(source?.revenue)
    const card = toCents(source?.card)
    const cash = toCents(source?.cash)
    const other = toCents(source?.other)
    const weekday = weekdayOf(date)
    if (revenue === null) {
      rows.push({ date, day, weekday, status: 'missing', revenue: null, card: null, cash: null, other: null })
    } else if (card === null || cash === null || other === null) {
      // 拆分不全就整体视为不可靠：三列都不显示、不计入合计，避免出现只有一半的支付构成。
      rows.push({ date, day, weekday, status: 'noSplit', revenue, card: null, cash: null, other: null })
    } else {
      rows.push({ date, day, weekday, status: 'ok', revenue, card, cash, other })
    }
  }
  return rows
}

export function totalsOfRows(rows: readonly DayRow[]): Totals {
  let totals: Totals = ZERO_TOTALS
  for (const row of rows) {
    if (row.revenue === null) continue
    totals = {
      ...totals,
      revenue: totals.revenue + row.revenue,
      revenueDays: totals.revenueDays + 1,
    }
    if (row.status === 'ok' && row.card !== null && row.cash !== null && row.other !== null) {
      totals = {
        ...totals,
        card: totals.card + row.card,
        cash: totals.cash + row.cash,
        other: totals.other + row.other,
        splitRevenue: totals.splitRevenue + row.revenue,
        splitDays: totals.splitDays + 1,
      }
    }
  }
  return totals
}

export interface StoreSummary {
  branchCode: string
  branchName: string
  rows: DayRow[]
  totals: Totals
  /** 缺数的日期（yyyy-MM-dd） */
  missingDates: string[]
  /** 有营业额但没有支付方式拆分的日期 */
  noSplitDates: string[]
}

export function summarizeStore(store: MonthlyStoreDailySalesStore, month: string, throughDay: number): StoreSummary {
  const rows = buildDayRows(store, month, throughDay)
  return {
    branchCode: store.branchCode,
    branchName: store.branchName || store.branchCode,
    rows,
    totals: totalsOfRows(rows),
    missingDates: rows.filter(row => row.status === 'missing').map(row => row.date),
    noSplitDates: rows.filter(row => row.status === 'noSplit').map(row => row.date),
  }
}

/** 编码按数字感知排序（2 在 10 之前），其余按字符串，保证文件顺序稳定。 */
export function compareBranchCode(left: string, right: string): number {
  return left.localeCompare(right, 'en', { numeric: true, sensitivity: 'base' }) || (left < right ? -1 : left > right ? 1 : 0)
}

/** 左栏顺序：按营业额降序，营业额相同按编码升序。 */
export function summarizeStores(data: MonthlyStoreDailySales): StoreSummary[] {
  const throughDay = countedThroughDay(data)
  return data.stores
    .map(store => summarizeStore(store, data.month, throughDay))
    .sort((left, right) => right.totals.revenue - left.totals.revenue || compareBranchCode(left.branchCode, right.branchCode))
}

export interface Coverage {
  month: string
  daysInMonth: number
  /** 服务端已计入的天数 */
  countedDays: number
  /** 已计入的最后一天（几号），没有则为 0 */
  throughDay: number
  throughDate: string | null
  /** 本月还没结束（还有未出数的日子） */
  isPartial: boolean
}

export function describeCoverage(data: MonthlyStoreDailySales): Coverage {
  const throughDay = countedThroughDay(data)
  return {
    month: data.month,
    daysInMonth: data.daysInMonth,
    countedDays: data.countedDays,
    throughDay,
    throughDate: data.countedThroughDate,
    isPartial: data.countedDays < data.daysInMonth || throughDay < data.daysInMonth,
  }
}

export interface SelectionSummary {
  storeCount: number
  totals: Totals
  /** 有缺数日的分店及总店日数 */
  missingStores: StoreSummary[]
  missingStoreDays: number
  /** 有无拆分日的分店及总店日数 */
  noSplitStores: StoreSummary[]
  noSplitStoreDays: number
  /** CSV 长表行数：每店每个已计入的日子一行（缺数日也保留一行，金额留空） */
  csvRowCount: number
}

export function summarizeSelection(stores: readonly StoreSummary[]): SelectionSummary {
  let totals: Totals = ZERO_TOTALS
  let missingStoreDays = 0
  let noSplitStoreDays = 0
  let csvRowCount = 0
  const missingStores: StoreSummary[] = []
  const noSplitStores: StoreSummary[] = []
  for (const store of stores) {
    totals = addTotals(totals, store.totals)
    csvRowCount += store.rows.length
    if (store.missingDates.length > 0) {
      missingStores.push(store)
      missingStoreDays += store.missingDates.length
    }
    if (store.noSplitDates.length > 0) {
      noSplitStores.push(store)
      noSplitStoreDays += store.noSplitDates.length
    }
  }
  return { storeCount: stores.length, totals, missingStores, missingStoreDays, noSplitStores, noSplitStoreDays, csvRowCount }
}

/** 支付构成条的三段宽度（0–100）；没有可靠拆分的日子时返回 null。 */
export function compositionOf(totals: Totals): { card: number; cash: number; other: number } | null {
  if (totals.splitRevenue <= 0 || totals.splitDays === 0) return null
  const card = Math.min(100, Math.max(0, (totals.card / totals.splitRevenue) * 100))
  const cash = Math.min(100 - card, Math.max(0, (totals.cash / totals.splitRevenue) * 100))
  return { card, cash, other: Math.max(0, 100 - card - cash) }
}

// ---------------------------------------------------------------------------
// 分店选择与搜索
// ---------------------------------------------------------------------------

/** 左栏搜索：分店名或编码包含关键词即可，不区分大小写。 */
export function matchesStoreSearch(store: Pick<StoreSummary, 'branchCode' | 'branchName'>, query: string): boolean {
  const keyword = query.trim().toLowerCase()
  if (!keyword) return true
  return `${store.branchName} ${store.branchCode}`.toLowerCase().includes(keyword)
}

/** 勾选状态按「被排除的编码」保存：默认全选，换月后新出现的分店也默认勾选。 */
export function toggleExcluded(excluded: ReadonlySet<string>, branchCode: string): Set<string> {
  const next = new Set(excluded)
  if (next.has(branchCode)) next.delete(branchCode)
  else next.add(branchCode)
  return next
}

/** 全选 / 清空只作用于当前可见（搜索过滤后）的分店。 */
export function setCodesSelected(excluded: ReadonlySet<string>, branchCodes: readonly string[], selected: boolean): Set<string> {
  const next = new Set(excluded)
  for (const code of branchCodes) {
    if (selected) next.delete(code)
    else next.add(code)
  }
  return next
}

/** 已勾选分店，沿用传入顺序。 */
export function pickSelected<T extends { branchCode: string }>(stores: readonly T[], excluded: ReadonlySet<string>): T[] {
  return stores.filter(store => !excluded.has(store.branchCode))
}

/** 当前预览的分店：优先用点选的，点选的已不在数据里时回到第一家。 */
export function resolveFocus<T extends { branchCode: string }>(stores: readonly T[], focusCode: string | null): T | undefined {
  return stores.find(store => store.branchCode === focusCode) ?? stores[0]
}

/** 导出中断后「重试」依据的选择指纹：月份、格式或勾选变了，旧的中断状态就作废。 */
export function selectionFingerprint(month: string, format: DownloadFormat, branchCodes: readonly string[]): string {
  return JSON.stringify([month, format, [...branchCodes].sort(compareBranchCode)])
}

/** 最多列出前几个日期，其余用省略号，避免备注与提示条过长。 */
export function listDates(dates: readonly string[], limit = 6): { shown: string[]; hidden: number } {
  return { shown: dates.slice(0, limit), hidden: Math.max(0, dates.length - limit) }
}

// ---------------------------------------------------------------------------
// 文件名与工作表名
// ---------------------------------------------------------------------------

export function buildFileName(prefix: string, month: string, format: DownloadFormat): string {
  return `${prefix}_${month}.${format}`
}

export const MAX_SHEET_NAME_LENGTH = 31

// Excel 工作表名不能含 [ ] : * ? / \，不能以单引号开头或结尾，不能为空，长度不超过 31（按 UTF-16 单元计）。
const invalidSheetChars = /[[\]:*?/\\\u0000-\u001f]/g

export function sanitizeSheetName(raw: string): string {
  return raw.replace(invalidSheetChars, '').replace(/\s+/g, ' ').trim().replace(/^'+|'+$/g, '').trim()
}

/** 按 Excel 的 31 个 UTF-16 单元截断，且不在代理对（emoji 等）中间切开。 */
export function truncateSheetName(name: string, maxLength = MAX_SHEET_NAME_LENGTH): string {
  let result = ''
  for (const char of name) {
    if (result.length + char.length > maxLength) break
    result += char
  }
  return result.trim().replace(/^'+|'+$/g, '').trim()
}

/**
 * 为每家分店生成唯一的工作表名（Excel 比较时不区分大小写）：
 * 先取清洗后的分店名；与已有名称或保留名（如「汇总」、Excel 保留的 History）重名时追加「-分店编码」，
 * 仍重名再追加序号。按编码顺序处理，结果稳定，中断重试时前后一致。
 */
export function buildSheetNames(
  stores: readonly { branchCode: string; branchName: string }[],
  reserved: readonly string[],
): Map<string, string> {
  const used = new Set([...reserved, 'History'].map(name => name.toLowerCase()))
  const result = new Map<string, string>()
  const ordered = [...stores].sort((left, right) => compareBranchCode(left.branchCode, right.branchCode))
  for (const store of ordered) {
    const code = sanitizeSheetName(store.branchCode)
    const base = truncateSheetName(sanitizeSheetName(store.branchName) || code || 'Sheet')
    let name = base
    if (used.has(name.toLowerCase())) {
      const suffix = `-${code || 'store'}`
      name = `${truncateSheetName(base, MAX_SHEET_NAME_LENGTH - suffix.length)}${suffix}`
    }
    for (let counter = 2; used.has(name.toLowerCase()); counter += 1) {
      const suffix = `-${code || 'store'}-${counter}`
      name = `${truncateSheetName(base, MAX_SHEET_NAME_LENGTH - suffix.length)}${suffix}`
    }
    used.add(name.toLowerCase())
    result.set(store.branchCode, name)
  }
  return result
}

// ---------------------------------------------------------------------------
// CSV 长表
// ---------------------------------------------------------------------------

export const CSV_BOM = '\uFEFF'

/** 整数分 → 不带千分位的两位小数文本（用于 CSV）；null 留空。 */
export function formatCsvAmount(cents: number | null): string {
  if (cents === null) return ''
  const abs = Math.abs(cents)
  return `${cents < 0 ? '-' : ''}${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, '0')}`
}

/** 字段含逗号、引号或换行时加引号并把引号翻倍。 */
export function escapeCsvField(value: string): string {
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value
}

/** 文本字段（分店名、编码）以 = + - @ 或制表符开头时前置单引号，防止用 Excel 打开 CSV 时被当成公式执行。 */
export function guardCsvText(value: string): string {
  return /^[=+\-@\t\r]/.test(value) ? `'${value}` : value
}

/**
 * CSV 长表：一行一店一天；UTF-8 带 BOM，Excel 直接打开不乱码。
 * 先按分店编码、再按日期排序；缺数日与无拆分日的金额留空，不写 0。
 */
export function buildCsv(stores: readonly StoreSummary[], header: readonly string[]): string {
  const lines: string[] = [header.map(escapeCsvField).join(',')]
  for (const store of [...stores].sort((left, right) => compareBranchCode(left.branchCode, right.branchCode))) {
    const code = escapeCsvField(guardCsvText(store.branchCode))
    const name = escapeCsvField(guardCsvText(store.branchName))
    for (const row of [...store.rows].sort((left, right) => left.date.localeCompare(right.date))) {
      lines.push([
        row.date, code, name,
        formatCsvAmount(row.revenue), formatCsvAmount(row.card), formatCsvAmount(row.cash), formatCsvAmount(row.other),
      ].join(','))
    }
  }
  return `${CSV_BOM}${lines.join('\r\n')}\r\n`
}

// ---------------------------------------------------------------------------
// 文案参数（月份 / 日期的本地化占位符，由页面交给 i18n）
// ---------------------------------------------------------------------------

// 英文月份名写成常量：Intl 的缩写在不同 ICU 版本 / 地区间不一致（如 Sep 与 Sept）。
const LONG_MONTH_NAMES = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
const SHORT_MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

export interface MonthTextParams {
  year: number
  month: number
  /** 英文月份全称，如 September；中文文案不使用 */
  monthName: string
}

/** 月份文案参数：中文用 year / month，英文用 monthName / year。 */
export function monthTextParams(month: string): MonthTextParams {
  const parsed = parseMonth(month) ?? { year: 0, month: 1 }
  return { ...parsed, monthName: LONG_MONTH_NAMES[parsed.month - 1] }
}

export interface DayTextParams {
  month: number
  day: number
  /** 英文月份缩写，如 Sep */
  monthName: string
}

/** 日期文案参数（yyyy-MM-dd）：中文用 month / day，英文用 day / monthName。 */
export function dayTextParams(date: string): DayTextParams {
  const [, month, day] = date.split('-').map(Number)
  return { month, day, monthName: SHORT_MONTH_NAMES[month - 1] ?? '' }
}
