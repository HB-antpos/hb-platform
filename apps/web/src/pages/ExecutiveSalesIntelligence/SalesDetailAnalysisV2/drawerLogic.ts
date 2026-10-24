import type { ProductPurchaseBatch, ProductPurchaseStore, ProductTrendDay } from './reportService'

/** 进货窗口 = 销售开始日前 N 天 ~ 销售结束日；后端 SalesDetailProductWindow.PurchaseLookbackDays 取同一个值。 */
export const PURCHASE_LOOKBACK_DAYS = 60
const DAY_MS = 86_400_000
const MAX_WINDOW_DAYS = 731 + PURCHASE_LOOKBACK_DAYS

function parse(date: string) {
  const [year, month, day] = date.slice(0, 10).split('-').map(Number)
  return Date.UTC(year, month - 1, day)
}
function pad(value: number) { return String(value).padStart(2, '0') }
function format(ms: number) {
  const date = new Date(ms)
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`
}
export function addDays(date: string, days: number) { return format(parse(date) + days * DAY_MS) }
/** b − a 的天数。 */
export function daysBetween(a: string, b: string) { return Math.round((parse(b) - parse(a)) / DAY_MS) }
/** 0 = 周日。 */
export function weekdayOf(date: string) { return new Date(parse(date)).getUTCDay() }
export function monthDay(date: string) { return date.slice(5, 10) }

export interface DrawerWindow { start: string; end: string }
export function drawerWindow(salesStart: string, salesEnd: string): DrawerWindow {
  return { start: addDays(salesStart, -PURCHASE_LOOKBACK_DAYS), end: salesEnd }
}

/** 闭区间逐日枚举；超过窗口上限时只取最后 MAX_WINDOW_DAYS 天，避免异常入参撑爆图表。 */
export function enumerateDays(start: string, end: string): string[] {
  const total = Math.max(0, daysBetween(start, end) + 1)
  const first = Math.max(0, total - MAX_WINDOW_DAYS)
  return Array.from({ length: total - first }, (_, index) => addDays(start, first + index))
}

export function normalizeBranchKey(code: string | undefined | null) { return (code ?? '').trim().toUpperCase() }

export interface TrendPoint { date: string; sales: number; warehouse: number; supplier: number }
export interface PurchaseDay { warehouse: number; supplier: number }

/** 逐日进货合计；传入 branchKey 时只统计该分店。 */
export function purchasesByDay(batches: ProductPurchaseBatch[], branchKey?: string | null) {
  const days = new Map<string, PurchaseDay>()
  for (const batch of batches) {
    if (branchKey && normalizeBranchKey(batch.branchCode) !== branchKey) continue
    const day = days.get(batch.date) ?? { warehouse: 0, supplier: 0 }
    if (batch.source === 'supplier') day.supplier += batch.quantity
    else day.warehouse += batch.quantity
    days.set(batch.date, day)
  }
  return days
}

/** 窗口内每一天一个点：销量缺口补 0，进货按来源分开。 */
export function buildDailyPoints(window: DrawerWindow, daily: ProductTrendDay[], batches: ProductPurchaseBatch[], branchKey?: string | null): TrendPoint[] {
  const sales = new Map(daily.map(item => [item.date, item.quantity]))
  const purchases = purchasesByDay(batches, branchKey)
  return enumerateDays(window.start, window.end).map(date => {
    const purchase = purchases.get(date)
    return { date, sales: sales.get(date) ?? 0, warehouse: purchase?.warehouse ?? 0, supplier: purchase?.supplier ?? 0 }
  })
}

export interface MonthPoint {
  key: string
  month: number
  sales: number
  warehouse: number
  supplier: number
  /** 窗口内属于该月的天数；不满整月时为 true，画斜纹柱并写「仅 N 日」。 */
  days: number
  partial: boolean
  hasSalesPeriod: boolean
}

function daysInMonth(year: number, month: number) { return new Date(Date.UTC(year, month, 0)).getUTCDate() }

/** 日点按自然月汇总；窗口头尾不满整月的月份标记 partial。 */
export function buildMonthlyPoints(points: TrendPoint[], salesStart: string, salesEnd: string): MonthPoint[] {
  const months: MonthPoint[] = []
  for (const point of points) {
    const key = point.date.slice(0, 7)
    let month = months[months.length - 1]
    if (!month || month.key !== key) {
      month = { key, month: Number(key.slice(5, 7)), sales: 0, warehouse: 0, supplier: 0, days: 0, partial: false, hasSalesPeriod: false }
      months.push(month)
    }
    month.sales += point.sales
    month.warehouse += point.warehouse
    month.supplier += point.supplier
    month.days += 1
    if (point.date >= salesStart && point.date <= salesEnd) month.hasSalesPeriod = true
  }
  for (const month of months) {
    month.partial = month.days < daysInMonth(Number(month.key.slice(0, 4)), month.month)
  }
  return months
}

const NICE_STEPS = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]
/** 刻度：取最小的「好看」步长，使区间数不超过 maxIntervals；max 不大于 0 时给一个固定小刻度。 */
export function niceScale(max: number, maxIntervals: number): { max: number; ticks: number[] } {
  if (!(max > 0)) return { max: maxIntervals, ticks: Array.from({ length: maxIntervals + 1 }, (_, index) => index) }
  for (let power = 0; power < 12; power += 1) {
    for (const base of NICE_STEPS) {
      const step = base * 10 ** power
      if (Math.ceil(max / step) <= maxIntervals) {
        const count = Math.ceil(max / step)
        return { max: step * count, ticks: Array.from({ length: count + 1 }, (_, index) => step * index) }
      }
    }
  }
  return { max, ticks: [0, max] }
}

export interface StorePurchase {
  key: string
  code: string
  quantity: number
  warehouse: number
  supplier: number
  batchCount: number
  /** 最近一次进货日。 */
  latestDate: string
  latestInSalesPeriod: boolean
  /** 日期从近到远，同日总仓在前。 */
  batches: ProductPurchaseBatch[]
}

export function summarizeStorePurchases(batches: ProductPurchaseBatch[], salesStart: string, salesEnd: string): Map<string, StorePurchase> {
  const stores = new Map<string, StorePurchase>()
  for (const batch of batches) {
    const key = normalizeBranchKey(batch.branchCode)
    let store = stores.get(key)
    if (!store) {
      store = { key, code: batch.branchCode.trim(), quantity: 0, warehouse: 0, supplier: 0, batchCount: 0, latestDate: '', latestInSalesPeriod: false, batches: [] }
      stores.set(key, store)
    }
    store.quantity += batch.quantity
    if (batch.source === 'supplier') store.supplier += batch.quantity
    else store.warehouse += batch.quantity
    store.batchCount += 1
    store.batches.push(batch)
  }
  for (const store of stores.values()) {
    store.batches.sort((a, b) => b.date.localeCompare(a.date) || (a.source === b.source ? 0 : a.source === 'warehouse' ? -1 : 1))
    store.latestDate = store.batches[0]?.date ?? ''
    store.latestInSalesPeriod = store.latestDate >= salesStart && store.latestDate <= salesEnd
  }
  return stores
}

/** 一批进货相对销售期的位置：销售期内，或销售开始日前 N 天。 */
export function batchOffset(date: string, salesStart: string, salesEnd: string): { inPeriod: boolean; daysBefore: number } {
  if (date >= salesStart && date <= salesEnd) return { inPeriod: true, daysBefore: 0 }
  return { inPeriod: false, daysBefore: Math.max(0, daysBetween(date, salesStart)) }
}

/** 表格里没有、但窗口内有进货的分店（销售期与同期都没有销量），按进货量从多到少追加。 */
export function purchaseOnlyStores(rowCodes: string[], stores: ProductPurchaseStore[], summaries: Map<string, StorePurchase>): ProductPurchaseStore[] {
  const present = new Set(rowCodes.map(normalizeBranchKey))
  const names = new Map(stores.map(store => [normalizeBranchKey(store.code), store.name]))
  return [...summaries.values()]
    .filter(summary => !present.has(summary.key) && summary.quantity !== 0)
    .sort((a, b) => b.quantity - a.quantity || a.key.localeCompare(b.key))
    .map(summary => ({ code: summary.code, name: names.get(summary.key) || summary.code }))
}

/** 悬停某一天：同一天的数据，加上更早的最近一次进货（当天没有进货时提示）。 */
export function previousPurchase(points: TrendPoint[], index: number): { date: string; quantity: number } | null {
  for (let i = index - 1; i >= 0; i -= 1) {
    const total = points[i].warehouse + points[i].supplier
    if (total > 0) return { date: points[i].date, quantity: total }
  }
  return null
}
