import { POS_API_ENTRY_USAGE_MAX_RANGE_DAYS } from '../../../services/posApiEntryService'
import type {
  PosApiEntryUsageBucket,
  PosApiEntryUsageCounts,
  PosApiEntryUsageEntry,
  PosApiEntryUsageGranularity,
} from '../../../types/posApiEntry'

/** 线路用量页的纯逻辑：日期运算、范围校验、预设区间、数字格式化、堆叠柱图的几何计算。全部是无副作用函数，便于单测。 */

export const USAGE_GRANULARITIES: readonly PosApiEntryUsageGranularity[] = ['day', 'week', 'month']

/** 堆叠顺序（自下而上）：三条线路在下，识别不出的 unknown 压在最上面。 */
export const USAGE_STACK_ORDER: readonly PosApiEntryUsageEntry[] = ['edge', 'aws', 'direct', 'unknown']

// ───────────────────────── 日期（全部按 yyyy-MM-dd 日历日运算，不碰浏览器本地时区） ─────────────────────────

const ISO_DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/
const MS_PER_DAY = 86_400_000

/** 解析成 UTC 零点的毫秒数：用 UTC 做日历运算，避开夏令时和浏览器时区造成的 ±1 天误差；非法日期返回 null。 */
function parseIsoDay(value: string): number | null {
  const match = ISO_DATE_PATTERN.exec(value)
  if (!match) {
    return null
  }
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])]
  const time = Date.UTC(year, month - 1, day)
  const date = new Date(time)
  // 2026-02-30 会被 Date 滚动成 3 月 2 日：往回比对一次年月日，滚动过的就是非法日期
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day ? time : null
}

function formatIsoDay(time: number): string {
  const date = new Date(time)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`
}

export function isValidIsoDate(value: string): boolean {
  return parseIsoDay(value) !== null
}

export function addDays(value: string, days: number): string {
  const time = parseIsoDay(value)
  return time === null ? value : formatIsoDay(time + days * MS_PER_DAY)
}

/** 含首尾的天数；任一端非法返回 0。 */
export function countDaysInclusive(start: string, end: string): number {
  const from = parseIsoDay(start)
  const to = parseIsoDay(end)
  return from === null || to === null ? 0 : Math.round((to - from) / MS_PER_DAY) + 1
}

/** 周一起算的周起始日（与后端一致）。 */
export function startOfWeek(value: string): string {
  const time = parseIsoDay(value)
  if (time === null) {
    return value
  }
  const dayOfWeek = new Date(time).getUTCDay() // 0=周日
  return formatIsoDay(time - ((dayOfWeek + 6) % 7) * MS_PER_DAY)
}

export function startOfMonth(value: string): string {
  return isValidIsoDate(value) ? `${value.slice(0, 7)}-01` : value
}

function addMonths(value: string, months: number): string {
  const time = parseIsoDay(value)
  if (time === null) {
    return value
  }
  const date = new Date(time)
  return formatIsoDay(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + months, 1))
}

// ───────────────────────── 范围 ─────────────────────────

export interface UsageRange {
  startDate: string
  endDate: string
}

/**
 * 各粒度的默认范围（结束日都是悉尼「今天」）：
 * 日＝最近 30 天；周＝最近 12 个周（起点对齐到周一，首周是完整周）；月＝最近 12 个月（起点对齐到月初）。
 */
export function getDefaultUsageRange(granularity: PosApiEntryUsageGranularity, today: string): UsageRange {
  if (granularity === 'week') {
    return { startDate: addDays(startOfWeek(today), -11 * 7), endDate: today }
  }
  if (granularity === 'month') {
    return { startDate: addMonths(startOfMonth(today), -11), endDate: today }
  }
  return { startDate: addDays(today, -29), endDate: today }
}

export type UsageRangeProblem = 'invalid' | 'reversed' | 'tooLarge'

/** 校验起止日期：格式、先后、跨度（含首尾不超过 400 天，与后端一致）；通过返回 null。 */
export function validateUsageRange(startDate: string, endDate: string): UsageRangeProblem | null {
  if (!isValidIsoDate(startDate) || !isValidIsoDate(endDate)) {
    return 'invalid'
  }
  if (startDate > endDate) {
    return 'reversed'
  }
  return countDaysInclusive(startDate, endDate) > POS_API_ENTRY_USAGE_MAX_RANGE_DAYS ? 'tooLarge' : null
}

export type UsagePresetKey = 'last7' | 'last30' | 'last90' | 'thisMonth' | 'lastMonth' | 'last12Months'

export interface UsagePreset extends UsageRange {
  key: UsagePresetKey
}

/** 快捷区间，结束日不超过悉尼今天。last12Months 取最近 365 天，仍在 400 天上限内。 */
export function buildUsagePresets(today: string): UsagePreset[] {
  const thisMonthStart = startOfMonth(today)
  const lastMonthStart = addMonths(thisMonthStart, -1)
  return [
    { key: 'last7', startDate: addDays(today, -6), endDate: today },
    { key: 'last30', startDate: addDays(today, -29), endDate: today },
    { key: 'last90', startDate: addDays(today, -89), endDate: today },
    { key: 'thisMonth', startDate: thisMonthStart, endDate: today },
    { key: 'lastMonth', startDate: lastMonthStart, endDate: addDays(thisMonthStart, -1) },
    { key: 'last12Months', startDate: addDays(today, -364), endDate: today },
  ]
}

// ───────────────────────── 格式化 ─────────────────────────

const countFormatter = new Intl.NumberFormat('en-US')

export function formatCount(value: number): string {
  return countFormatter.format(Number.isFinite(value) ? value : 0)
}

/** 占比：0 显示「0%」；非 0 但不足 0.1% 显示「<0.1%」，避免一条真实存在的线路被四舍五入成 0。 */
export function formatShare(share: number): string {
  if (!Number.isFinite(share) || share <= 0) {
    return '0%'
  }
  if (share < 0.001) {
    return '<0.1%'
  }
  const percent = share * 100
  return `${percent >= 99.95 ? '100' : percent.toFixed(1).replace(/\.0$/, '')}%`
}

/** 5xx 占请求数的比例，沿用占比格式；没有请求时为「—」。 */
export function formatErrorRate(counts: PosApiEntryUsageCounts): string {
  return counts.requests > 0 ? formatShare(counts.status5xx / counts.requests) : '—'
}

/** 图表横轴短标签：日/周＝MM-DD，月＝YYYY-MM。 */
export function formatBucketAxisLabel(bucketStart: string, granularity: PosApiEntryUsageGranularity): string {
  return granularity === 'month' ? bucketStart.slice(0, 7) : bucketStart.slice(5)
}

/** 表格 / 提示里的完整桶名：日＝完整日期，周＝「起 ~ 止」，月＝YYYY-MM。 */
export function formatBucketTitle(bucket: PosApiEntryUsageBucket, granularity: PosApiEntryUsageGranularity): string {
  if (granularity === 'day') {
    return bucket.bucketStart
  }
  if (granularity === 'month') {
    return bucket.bucketStart.slice(0, 7)
  }
  return `${bucket.bucketStart} ~ ${bucket.bucketEnd}`
}

// ───────────────────────── 堆叠柱图几何 ─────────────────────────

export interface ChartSegment {
  entry: PosApiEntryUsageEntry
  requests: number
  y: number
  height: number
}

export interface ChartBar {
  bucketStart: string
  total: number
  x: number
  width: number
  segments: ChartSegment[]
  /** 是否在横轴上标注日期（桶很多时隔几个标一个，避免文字叠在一起） */
  showLabel: boolean
}

export interface ChartTick {
  value: number
  y: number
}

export interface StackedChartModel {
  bars: ChartBar[]
  ticks: ChartTick[]
  /** 纵轴上限（刻度间隔 × 4） */
  max: number
  plot: { left: number; top: number; width: number; height: number }
}

export interface StackedChartLayout {
  width: number
  height: number
  padLeft?: number
  padRight?: number
  padTop?: number
  padBottom?: number
}

/** 向上取整到 1 / 2 / 5 × 10^k（至少为 1）：用作纵轴刻度间隔，保证刻度都是整数且好读。 */
export function niceStep(value: number): number {
  if (!Number.isFinite(value) || value <= 1) {
    return 1
  }
  const magnitude = 10 ** Math.floor(Math.log10(value))
  const normalized = value / magnitude
  const step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10
  return step * magnitude
}

/**
 * 把时间桶换算成堆叠柱的矩形。纵轴从 0 起分 4 格，刻度间隔取 1 / 2 / 5 × 10^k（所以刻度都是整数），上限＝间隔 × 4；
 * 每根柱子按 USAGE_STACK_ORDER 自下而上叠，高度为 0 的线路不产生矩形；
 * 横轴标签最多约 12 个（桶多时隔几根标一个，从第一根起），避免文字叠在一起。
 */
export function buildStackedChart(buckets: PosApiEntryUsageBucket[], layout: StackedChartLayout): StackedChartModel {
  const padLeft = layout.padLeft ?? 48
  const padRight = layout.padRight ?? 12
  const padTop = layout.padTop ?? 12
  const padBottom = layout.padBottom ?? 28
  const plot = {
    left: padLeft,
    top: padTop,
    width: Math.max(1, layout.width - padLeft - padRight),
    height: Math.max(1, layout.height - padTop - padBottom),
  }
  const maxRequests = Math.max(0, ...buckets.map((bucket) => bucket.total.requests))
  const tickStep = niceStep(maxRequests / 4)
  const max = tickStep * 4
  const step = buckets.length > 0 ? plot.width / buckets.length : plot.width
  const barWidth = Math.max(1, step * 0.72)
  const labelEvery = Math.max(1, Math.ceil(buckets.length / 12))

  const bars = buckets.map<ChartBar>((bucket, index) => {
    let stacked = 0
    const segments: ChartSegment[] = []
    for (const entry of USAGE_STACK_ORDER) {
      const requests = bucket.entries.find((item) => item.entry === entry)?.requests ?? 0
      if (requests <= 0) {
        continue
      }
      const height = (requests / max) * plot.height
      stacked += requests
      // y 是矩形上边缘：柱顶位置减去本段高度
      segments.push({ entry, requests, y: plot.top + plot.height - (stacked / max) * plot.height, height })
    }
    return {
      bucketStart: bucket.bucketStart,
      total: bucket.total.requests,
      x: plot.left + index * step + (step - barWidth) / 2,
      width: barWidth,
      segments,
      showLabel: index % labelEvery === 0,
    }
  })

  const ticks = [0, 1, 2, 3, 4].map<ChartTick>((i) => {
    const value = tickStep * i
    return { value, y: plot.top + plot.height - (value / max) * plot.height }
  })
  return { bars, ticks, max, plot }
}
