import type { ApiResponse } from '../types/api'
import request from '../utils/request'

/**
 * 月度日销售下载：各分店某个月每天的营业额 / 刷卡 / 现金 / 其他。
 *
 * 空值语义（不能当 0）：
 * - revenue 为 null：该日统计尚未发布（缺数）。
 * - revenue 有值但 card / cash / other 为 null：该日没有可靠的支付方式拆分
 *   （营业额含旧系统来源，或统计尚未追上 POSM 迟到上传，两边对不上）。
 * - 休业日是 0，不是 null。
 */
export interface MonthlyStoreDay {
  /** yyyy-MM-dd */
  date: string
  revenue: number | null
  card: number | null
  cash: number | null
  /** 其他 = 营业额 − 刷卡 − 现金（代金券等），服务端已算好，前端不重算。 */
  other: number | null
}

export interface MonthlyStoreDailySalesStore {
  branchCode: string
  branchName: string
  /** 只含已计入的日子（1 日 ~ countedThroughDate），按日期升序。 */
  days: MonthlyStoreDay[]
}

export interface MonthlyStoreDailySales {
  /** yyyy-MM */
  month: string
  daysInMonth: number
  /** 已计入的最后一天；当月进行中时是昨天，整月还没有任何已出数日子时为 null。 */
  countedThroughDate: string | null
  countedDays: number
  stores: MonthlyStoreDailySalesStore[]
}

export interface MonthlyStoreDailySalesQuery {
  /** yyyy-MM */
  month: string
  /** 授权分店子集；管理员 / 全局范围不传（undefined）。 */
  branchCodes?: string[]
  forceRefresh?: boolean
}

const monthPattern = /^(\d{4})-(0[1-9]|1[0-2])$/
const datePattern = /^(\d{4})-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$/

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function readString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

/** 只认有限数值；字符串、NaN、对象一律按 null，绝不悄悄当 0。 */
function readNullableNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function readInteger(value: unknown): number | null {
  return typeof value === 'number' && Number.isInteger(value) ? value : null
}

/** 该月天数；month 非法时返回 0。按 UTC 计算，不受浏览器时区影响。 */
function daysInMonthOf(month: string): number {
  const match = monthPattern.exec(month)
  if (!match) return 0
  return new Date(Date.UTC(Number(match[1]), Number(match[2]), 0)).getUTCDate()
}

/** 日期必须是真实存在的、属于该月的日历日（2026-09-31 这种格式合法但不存在的日期要排除）。 */
function isDateInMonth(date: string | undefined, month: string): date is string {
  if (!date || !datePattern.test(date) || !date.startsWith(`${month}-`)) return false
  return Number(date.slice(8, 10)) <= daysInMonthOf(month)
}

function normalizeDay(raw: unknown, month: string): MonthlyStoreDay | null {
  if (!isRecord(raw)) return null
  const date = readString(raw.date ?? raw.Date)
  if (!isDateInMonth(date, month)) return null
  // 逐字段独立读取：缺字段、非数值都按 null；是否构成可靠拆分由页面逻辑统一判断。
  return {
    date,
    revenue: readNullableNumber(raw.revenue ?? raw.Revenue),
    card: readNullableNumber(raw.card ?? raw.Card),
    cash: readNullableNumber(raw.cash ?? raw.Cash),
    other: readNullableNumber(raw.other ?? raw.Other),
  }
}

function normalizeStore(raw: unknown, month: string): MonthlyStoreDailySalesStore | null {
  if (!isRecord(raw)) return null
  const branchCode = readString(raw.branchCode ?? raw.BranchCode)
  if (!branchCode) return null
  const rawDays = raw.days ?? raw.Days
  const byDate = new Map<string, MonthlyStoreDay>()
  if (Array.isArray(rawDays)) {
    for (const item of rawDays) {
      const day = normalizeDay(item, month)
      // 同一天出现多条时保留第一条，避免重复计入合计。
      if (day && !byDate.has(day.date)) byDate.set(day.date, day)
    }
  }
  return {
    branchCode,
    branchName: readString(raw.branchName ?? raw.BranchName) ?? branchCode,
    days: [...byDate.values()].sort((left, right) => left.date.localeCompare(right.date)),
  }
}

/** 信封 { success, data } 或裸对象都接受；success 为 false 时抛错而不是当成空数据。 */
function unwrapPayload(payload: unknown): unknown {
  let current: unknown = payload
  for (let depth = 0; depth < 3; depth += 1) {
    if (!isRecord(current)) break
    if (current.success === false || current.isSuccess === false) {
      throw new Error(readString(current.message) ?? 'Monthly daily sales request failed')
    }
    if (!('data' in current) && !('Data' in current)) break
    current = current.data ?? current.Data
  }
  return current
}

export function emptyMonthlyStoreDailySales(month: string): MonthlyStoreDailySales {
  return { month, daysInMonth: daysInMonthOf(month), countedThroughDate: null, countedDays: 0, stores: [] }
}

/** 把接口返回规范化成页面可直接使用的结构；fallbackMonth 是请求的月份，响应缺 month 时使用。 */
export function normalizeMonthlyStoreDailySales(payload: unknown, fallbackMonth: string): MonthlyStoreDailySales {
  const result = unwrapPayload(payload)
  const record = isRecord(result) ? result : {}
  const responseMonth = readString(record.month ?? record.Month)
  const month = responseMonth && monthPattern.test(responseMonth) ? responseMonth : fallbackMonth
  const daysInMonth = daysInMonthOf(month)

  // 已计入截止日必须落在本月内，否则当作没有已出数日子。
  const rawThrough = readString(record.countedThroughDate ?? record.CountedThroughDate)
  const countedThroughDate = isDateInMonth(rawThrough, month) ? rawThrough : null
  const throughDay = countedThroughDate ? Number(countedThroughDate.slice(8, 10)) : 0
  const rawCounted = readInteger(record.countedDays ?? record.CountedDays)
  const countedDays = rawCounted === null
    ? throughDay
    : Math.min(Math.max(rawCounted, 0), daysInMonth)

  const rawStores = record.stores ?? record.Stores
  const stores: MonthlyStoreDailySalesStore[] = []
  const seenCodes = new Set<string>()
  if (Array.isArray(rawStores)) {
    for (const item of rawStores) {
      const store = normalizeStore(item, month)
      if (store && !seenCodes.has(store.branchCode)) {
        seenCodes.add(store.branchCode)
        stores.push(store)
      }
    }
  }
  return { month, daysInMonth, countedThroughDate, countedDays, stores }
}

export async function getMonthlyStoreDailySales(
  query: MonthlyStoreDailySalesQuery,
  signal?: AbortSignal,
): Promise<MonthlyStoreDailySales> {
  // 授权范围为空数组时不能发请求：request 会丢弃空数组参数，服务端会把它当成「不限分店」。
  if (query.branchCodes && query.branchCodes.length === 0) {
    return emptyMonthlyStoreDailySales(query.month)
  }
  const response = await request<ApiResponse<MonthlyStoreDailySales> | MonthlyStoreDailySales>(
    '/api/react/v1/dashboard/monthly-store-daily-sales',
    {
      method: 'GET',
      signal,
      params: {
        month: query.month,
        branchCodes: query.branchCodes,
        forceRefresh: query.forceRefresh ?? false,
      },
    },
  )
  return normalizeMonthlyStoreDailySales(response, query.month)
}
