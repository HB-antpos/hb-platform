// 供应商节日预订单「下单」页 · 去年历史数据的纯逻辑：
// 日期按「业务日期」处理（只取 yyyy-MM-dd，不做时区换算）、稀疏日销转稠密数组、峰值、
// 节日关键日期分组与标签分层、折扣区间转下标、进货批次按天汇总、售罄率颜色档、百分比/MM-DD 格式化、坐标刻度。
// 不依赖 React / antd，便于 node 直接跑单测。

import type {
  SupplierPreorderDailyPoint,
  SupplierPreorderDateRange,
  SupplierPreorderHistoryBatch,
  SupplierPreorderHistoryState,
  SupplierPreorderItemHistory,
  SupplierPreorderItemRef,
  SupplierPreorderKeyDate,
  SupplierPreorderKeyDateKey,
} from '../../../types/supplierPreorder'

const DAY_MS = 24 * 60 * 60 * 1000
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})/

// ---------- 业务日期 ----------

/** 日期字符串 → 自 1970-01-01 起的天数（按 UTC 日历算，不受浏览器时区影响）；无法解析返回 null */
export function toDayNumber(value?: string | null): number | null {
  if (!value) return null
  const match = DATE_PATTERN.exec(value.trim())
  if (!match) return null
  const ms = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  return Number.isNaN(ms) ? null : Math.round(ms / DAY_MS)
}

/** date 距 from 的天数（from 当天 = 0）；任一无法解析返回 null */
export function dayOffset(from: string | null | undefined, date: string | null | undefined): number | null {
  const start = toDayNumber(from)
  const target = toDayNumber(date)
  return start === null || target === null ? null : target - start
}

/** 窗口天数（含首尾）；缺日期或结束早于开始返回 null */
export function windowDayCount(from?: string | null, to?: string | null): number | null {
  const diff = dayOffset(from, to)
  return diff === null || diff < 0 ? null : diff + 1
}

/** 窗口起点加 offset 天后的 yyyy-MM-dd；起点无法解析返回空串 */
export function dateAtOffset(from: string | null | undefined, offset: number): string {
  const start = toDayNumber(from)
  if (start === null) return ''
  return new Date((start + offset) * DAY_MS).toISOString().slice(0, 10)
}

/** MM-DD；空值或无法解析返回 '—' */
export function formatMonthDay(value?: string | null): string {
  if (!value) return '—'
  const match = DATE_PATTERN.exec(value.trim())
  return match ? `${match[2]}-${match[3]}` : '—'
}

/** 「09-21 → 11-14」；两端都没有返回 '—'，只有一端时另一端显示 '—' */
export function formatDateSpan(first?: string | null, last?: string | null): string {
  if (!first && !last) return '—'
  return `${formatMonthDay(first)} → ${formatMonthDay(last)}`
}

/** 关键日期组的日期：单日「12-25」；同月「12-24~26」；跨月「11-30~12-01」 */
export function formatDayRange(first: string, last: string): string {
  const a = formatMonthDay(first)
  const b = formatMonthDay(last)
  if (a === b) return a
  if (a.slice(0, 2) === b.slice(0, 2)) return `${a}~${b.slice(3)}`
  return `${a}~${b}`
}

// ---------- 数字格式 ----------

/** 比例（0~1）转百分比整数文案「54%」；空值返回 '—' */
export function formatPercent(ratio: number | null | undefined): string {
  if (ratio === null || ratio === undefined || !Number.isFinite(ratio)) return '—'
  return `${Math.round(ratio * 100)}%`
}

export type SellThroughTone = 'good' | 'normal' | 'low' | 'none'

/** 售罄率颜色档：≥ 90% 绿色；< 60% 偏暖色（卖得慢）；其余正常；没有售罄率为 none */
export function sellThroughTone(ratio: number | null | undefined): SellThroughTone {
  if (ratio === null || ratio === undefined || !Number.isFinite(ratio)) return 'none'
  if (ratio >= 0.9) return 'good'
  if (ratio < 0.6) return 'low'
  return 'normal'
}

/** 坐标刻度：把最大值放大到 1/2/5×10ⁿ 的整倍数，刻度都是整数（销量、件数没有小数刻度） */
export function niceScale(maxValue: number, tickCount = 4): { max: number; ticks: number[] } {
  const safeMax = Number.isFinite(maxValue) && maxValue > 0 ? maxValue : 0
  if (safeMax === 0) return { max: 1, ticks: [0, 1] }
  const rough = safeMax / Math.max(1, tickCount)
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  const normalized = rough / magnitude
  const factor = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10
  const step = Math.max(1, factor * magnitude)
  const max = Math.ceil(safeMax / step) * step
  const ticks: number[] = []
  for (let value = 0; value <= max + step / 2; value += step) ticks.push(value)
  return { max, ticks }
}

// ---------- 日销与进货 ----------

/** 稠密数组长度：优先窗口天数，没有窗口时取稀疏数据里最大的下标 + 1 */
export function resolveSeriesLength(windowDays: number | null | undefined, daily: readonly SupplierPreorderDailyPoint[]): number {
  if (windowDays && windowDays > 0) return windowDays
  return daily.reduce((max, point) => Math.max(max, Math.floor(Number(point[0])) + 1), 0)
}

/** 稀疏日销 → 稠密数组（净销量、折扣销量）；越界或非法下标忽略，同一天多条累加 */
export function expandDaily(daily: readonly SupplierPreorderDailyPoint[], length: number): { sales: number[]; discount: number[] } {
  const size = Math.max(0, Math.floor(length))
  const sales = new Array<number>(size).fill(0)
  const discount = new Array<number>(size).fill(0)
  for (const point of daily) {
    const index = Math.floor(Number(point[0]))
    if (!Number.isFinite(index) || index < 0 || index >= size) continue
    sales[index] += Number(point[1]) || 0
    discount[index] += Number(point[2]) || 0
  }
  return { sales, discount }
}

/** 峰值（最早出现的最大值）；全部 ≤ 0 返回 null */
export function findPeak(values: readonly number[]): { index: number; value: number } | null {
  let best: { index: number; value: number } | null = null
  values.forEach((value, index) => {
    if (value > 0 && (!best || value > best.value)) best = { index, value }
  })
  return best
}

export interface IndexSpan {
  start: number
  end: number
}

/** 折扣日期区间 → 下标区间（含首尾），裁剪到窗口内；完全落在窗口外的丢弃 */
export function rangesToIndexSpans(ranges: readonly SupplierPreorderDateRange[], windowFrom: string | null | undefined, length: number): IndexSpan[] {
  const spans: IndexSpan[] = []
  for (const range of ranges) {
    const start = dayOffset(windowFrom, range.from)
    const end = dayOffset(windowFrom, range.to)
    if (start === null || end === null || end < start) continue
    if (end < 0 || start >= length) continue
    spans.push({ start: Math.max(0, start), end: Math.min(length - 1, end) })
  }
  return spans.sort((a, b) => a.start - b.start)
}

/** 进货批次按天汇总成两条稠密数组：warehouse 总仓配货 / supplier 供应商直送；窗口外的批次忽略 */
export function aggregateBatches(
  batches: readonly SupplierPreorderHistoryBatch[],
  windowFrom: string | null | undefined,
  length: number,
): { warehouse: number[]; supplier: number[] } {
  const warehouse = new Array<number>(Math.max(0, length)).fill(0)
  const supplier = new Array<number>(Math.max(0, length)).fill(0)
  for (const batch of batches) {
    const index = dayOffset(windowFrom, batch.date)
    if (index === null || index < 0 || index >= length) continue
    const target = batch.source === 'supplier' ? supplier : warehouse
    target[index] += Number(batch.quantity) || 0
  }
  return { warehouse, supplier }
}

/** 每月 1 日的下标（含窗口第一天），用于横轴「2025-10」刻度 */
export function monthStartTicks(windowFrom: string | null | undefined, length: number): { index: number; label: string }[] {
  const ticks: { index: number; label: string }[] = []
  for (let index = 0; index < length; index += 1) {
    const date = dateAtOffset(windowFrom, index)
    if (!date) break
    if (index === 0 || date.endsWith('-01')) ticks.push({ index, label: date.slice(0, 7) })
  }
  return ticks
}

// ---------- 节日关键日期 ----------

export interface KeyDateMark {
  index: number
  key: SupplierPreorderKeyDateKey
  date: string
}

/** 关键日期 → 窗口内下标，按日期排序，同一天只留一个 */
export function keyDateMarks(keyDates: readonly SupplierPreorderKeyDate[], windowFrom: string | null | undefined, length: number): KeyDateMark[] {
  const seen = new Set<string>()
  const marks: KeyDateMark[] = []
  for (const keyDate of keyDates) {
    const index = dayOffset(windowFrom, keyDate.date)
    if (index === null || index < 0 || index >= length) continue
    const id = `${index}|${keyDate.key}`
    if (seen.has(id)) continue
    seen.add(id)
    marks.push({ index, key: keyDate.key, date: keyDate.date.slice(0, 10) })
  }
  return marks.sort((a, b) => a.index - b.index)
}

export interface KeyDateGroup {
  start: number
  end: number
  keys: SupplierPreorderKeyDateKey[]
  firstDate: string
  lastDate: string
}

/**
 * 相邻关键日期合并成一个标签：相隔不超过 maxGapDays 天的算一组。
 * 例如平安夜 / 圣诞 / 节礼日（12-24、25、26）合并成「平安夜 · 圣诞 · 节礼日 12-24~26」，避免三个标签挤在一起。
 */
export function groupKeyDateMarks(marks: readonly KeyDateMark[], maxGapDays = 1): KeyDateGroup[] {
  const groups: KeyDateGroup[] = []
  for (const mark of marks) {
    const last = groups[groups.length - 1]
    if (last && mark.index - last.end <= maxGapDays) {
      last.end = mark.index
      last.lastDate = mark.date
      if (!last.keys.includes(mark.key)) last.keys.push(mark.key)
      continue
    }
    groups.push({ start: mark.index, end: mark.index, keys: [mark.key], firstDate: mark.date, lastDate: mark.date })
  }
  return groups
}

/** 粗估文字宽度（px）：中日韩字符按一个字号宽，其余按 0.6 个字号宽 */
export function estimateTextWidth(text: string, fontSize: number): number {
  let width = 0
  for (const char of text) width += /[⺀-鿿＀-￯]/.test(char) ? fontSize : fontSize * 0.6
  return Math.ceil(width)
}

export interface LabelSlot {
  left: number
  row: number
}

/**
 * 标签分层：每个标签以 center 为中心、限制在 [minX, maxX] 内，按左边界从左到右放进第一层放得下的行；
 * 与该行上一个标签的间距小于 gap 就换到下一行。返回顺序与输入一致。
 */
export function layoutLabelRows(
  items: readonly { center: number; width: number }[],
  minX: number,
  maxX: number,
  gap = 6,
): { slots: LabelSlot[]; rowCount: number } {
  const placed = items.map((item, order) => {
    const width = Math.min(item.width, Math.max(0, maxX - minX))
    const left = Math.min(Math.max(item.center - width / 2, minX), maxX - width)
    return { order, left, right: left + width }
  })
  const rowRights: number[] = []
  const slots: LabelSlot[] = new Array(items.length)
  for (const item of [...placed].sort((a, b) => a.left - b.left || a.order - b.order)) {
    let row = rowRights.findIndex((right) => right + gap <= item.left)
    if (row < 0) {
      row = rowRights.length
      rowRights.push(item.right)
    } else {
      rowRights[row] = item.right
    }
    slots[item.order] = { left: item.left, row }
  }
  return { slots, rowCount: rowRights.length }
}

// ---------- 曲线路径 ----------

/** 第 index 个点的横坐标：首尾贴边，只有一个点时居中 */
export function seriesX(index: number, length: number, left: number, width: number): number {
  if (length <= 1) return left + width / 2
  return left + (index * width) / (length - 1)
}

/** 迷你曲线的折线与面积路径（坐标保留 1 位小数）；没有点返回空串 */
export function sparklinePaths(
  values: readonly number[],
  width: number,
  height: number,
  options: { top?: number; maxValue?: number } = {},
): { line: string; area: string } {
  if (!values.length) return { line: '', area: '' }
  const top = options.top ?? 2
  const max = options.maxValue ?? Math.max(0, ...values)
  const plotHeight = height - top
  const y = (value: number) => (max > 0 ? top + plotHeight - (Math.max(0, value) / max) * plotHeight : height)
  const coords = values.map((value, index) => `${seriesX(index, values.length, 0, width).toFixed(1)},${y(value).toFixed(1)}`)
  const line = `M${coords.join(' L')}`
  const firstX = seriesX(0, values.length, 0, width).toFixed(1)
  const lastX = seriesX(values.length - 1, values.length, 0, width).toFixed(1)
  return { line, area: `${line} L${lastX},${height} L${firstX},${height} Z` }
}

// ---------- 表格格子状态 ----------

/**
 * 去年数据格子的显示状态：
 * - data：有数据（本品或参考商品）
 * - noStore：别的分店去年有记录，本店没有
 * - pending：快照还没算好（未排队 / 排队中 / 计算中）
 * - failed：快照计算失败
 * - none：无去年记录（含没有历史窗口）
 */
export type HistoryCellState = 'data' | 'noStore' | 'pending' | 'failed' | 'none'

export function historyCellState(history: SupplierPreorderItemHistory | null | undefined, status: SupplierPreorderHistoryState | null | undefined): HistoryCellState {
  if (history) return history.hasStoreData ? 'data' : 'noStore'
  if (status === 'None' || status === 'Queued' || status === 'Running') return 'pending'
  if (status === 'Failed') return 'failed'
  return 'none'
}

/** 参考商品在界面上显示的编号：优先货号，没有时用商品编码 */
export function refDisplayCode(ref: SupplierPreorderItemRef | null | undefined, fallbackProductCode?: string | null): string {
  return ref?.itemNumber?.trim() || ref?.productCode?.trim() || fallbackProductCode?.trim() || ''
}
