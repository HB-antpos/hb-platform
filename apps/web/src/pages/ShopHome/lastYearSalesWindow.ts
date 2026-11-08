import type { StoreOrderDynamicData } from '../../types/storeOrder'
import { formatShopCardDate } from './shopCardDate'

// 去年同期窗口天数：工具栏全局切换，默认 30 天。
export type LastYearWindowDays = 30 | 60

export const LAST_YEAR_WINDOW_DAYS_OPTIONS: readonly LastYearWindowDays[] = [30, 60]
export const DEFAULT_LAST_YEAR_WINDOW_DAYS: LastYearWindowDays = 30
export const LAST_YEAR_WINDOW_DAYS_STORAGE_KEY = 'shop-last-year-window-days'

/** 本机记住的选择只认 30 / 60，其它值（被改坏、旧版本）一律回到默认 30 天。 */
export function parseLastYearWindowDays(value: unknown): LastYearWindowDays {
  const numericValue = Number(value)
  return numericValue === 60 ? 60 : DEFAULT_LAST_YEAR_WINDOW_DAYS
}

export function readStoredLastYearWindowDays(): LastYearWindowDays {
  try {
    return parseLastYearWindowDays(window.localStorage.getItem(LAST_YEAR_WINDOW_DAYS_STORAGE_KEY))
  } catch {
    // 无痕模式或禁用存储时读不到，按默认 30 天显示。
    return DEFAULT_LAST_YEAR_WINDOW_DAYS
  }
}

export function storeLastYearWindowDays(days: LastYearWindowDays) {
  try {
    window.localStorage.setItem(LAST_YEAR_WINDOW_DAYS_STORAGE_KEY, String(days))
  } catch {
    // 存不下只影响下次打开的默认值，不影响本次切换。
  }
}

/** 取所选窗口的销量；后端没返回（分店未启用 POS、去年此时没数据、旧后端）时为 null，卡片据此隐藏。 */
export function pickLastYearSalesQuantity(
  data: Pick<StoreOrderDynamicData, 'lastYearSalesQuantity30Days' | 'lastYearSalesQuantity60Days'> | undefined,
  days: LastYearWindowDays,
): number | null {
  const value = days === 60 ? data?.lastYearSalesQuantity60Days : data?.lastYearSalesQuantity30Days
  return typeof value === 'number' ? value : null
}

/**
 * 去年同期日期范围文字，终点 = 起点 + 天数 − 1（含首尾）。同一年只在终点写年份（「9 Nov – 8 Dec 2025」），
 * 跨年两端都写（「9 Nov 2025 – 7 Jan 2026」），卡片窄也能一行放下。
 * 只按 YYYY-MM-DD 做 UTC 纯日期加法，不经过本地时区，避免跨夏令时差一天。
 */
export function formatLastYearWindowRange(startDate: string | null | undefined, days: LastYearWindowDays): string | null {
  const dateOnly = startDate?.trim().match(/^(\d{4})-(\d{2})-(\d{2})/)
  if (!dateOnly) {
    return null
  }

  const startUtc = Date.UTC(Number(dateOnly[1]), Number(dateOnly[2]) - 1, Number(dateOnly[3]))
  if (!Number.isFinite(startUtc)) {
    return null
  }

  const endIso = new Date(startUtc + (days - 1) * 24 * 60 * 60 * 1000).toISOString()
  const formattedStart = formatShopCardDate(startDate)
  const formattedEnd = formatShopCardDate(endIso)
  if (!formattedStart || !formattedEnd) {
    return null
  }

  const sameYear = endIso.startsWith(dateOnly[1])
  const startLabel = sameYear ? formattedStart.replace(/ \d{4}$/, '') : formattedStart
  return `${startLabel} – ${formattedEnd}`
}

/** 当前页任一商品带回了去年同期窗口，说明本店是 POS 启用分店且去年此时有数据，才显示工具栏开关。 */
export function hasLastYearSalesWindow(dynamicDataMap: Record<string, StoreOrderDynamicData>): boolean {
  return Object.values(dynamicDataMap).some((item) => Boolean(item?.lastYearWindowStartDate))
}
