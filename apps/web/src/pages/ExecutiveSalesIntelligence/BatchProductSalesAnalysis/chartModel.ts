import type { BatchSalesDaily } from '../../../types/batchProductSalesAnalysis'

export type DiscountChartKind = 'regular' | 'discount' | 'unknown' | 'total'

export interface DiscountChartSegment {
  kind: DiscountChartKind
  value: number
  x: number
  y: number
  width: number
  height: number
}

export interface DiscountChartPoint {
  unavailable: boolean
  pending: boolean
  discountPending: boolean
  date: string
  quantity: number
  salesAmount: number
  regularQuantity: number
  discountQuantity: number
  unknownQuantity: number
  originalPriceMin: number | null
  originalPriceMax: number | null
  discountPriceMin: number | null
  discountPriceMax: number | null
  x: number
  /** 整个日期槽均可交互，缺失日期没有柱形时仍能显示提示。 */
  hitX: number
  hitWidth: number
  segments: DiscountChartSegment[]
}

/** 缺失统计日占据日期轴，但没有指标，避免把未知数据伪装为零。 */
export interface DiscountChartDaily {
  date: string
  metrics: BatchSalesDaily['metrics'] | null
}

export interface DiscountChartModel {
  width: number
  height: number
  zeroY: number
  plotLeft: number
  plotRight: number
  plotTop: number
  plotBottom: number
  /** 每个日期槽的宽度；数值标注按它判断疏密。 */
  slotWidth: number
  minValue: number
  maxValue: number
  points: DiscountChartPoint[]
  ticks: Array<{ value: number; y: number }>
  xTicks: Array<{ date: string; x: number }>
  weekDividers: Array<{ date: string; x: number }>
}

const SERIES: Array<{ kind: DiscountChartKind; key: 'regularQuantity' | 'discountQuantity' | 'unknownQuantity' }> = [
  { kind: 'regular', key: 'regularQuantity' },
  { kind: 'discount', key: 'discountQuantity' },
  { kind: 'unknown', key: 'unknownQuantity' },
]

function finite(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

function tickValues(min: number, max: number) {
  const span = Math.max(1, max - min)
  const rawStep = span / 4
  const power = 10 ** Math.floor(Math.log10(rawStep))
  const normalized = rawStep / power
  const step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * power
  const start = Math.floor(min / step) * step
  const end = Math.ceil(max / step) * step
  const values: number[] = []
  for (let value = start; value <= end + step / 10; value += step) values.push(Number(value.toFixed(8)))
  return values
}

function xTickIndices(length: number) {
  if (length <= 6) return Array.from({ length }, (_, index) => index)
  const count = Math.min(7, length)
  return Array.from({ length: count }, (_, index) => Math.round((index * (length - 1)) / (count - 1)))
}

function naturalWeekKey(date: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return null
  const parsed = new Date(`${date}T00:00:00Z`)
  if (Number.isNaN(parsed.valueOf()) || parsed.toISOString().slice(0, 10) !== date) return null
  const weekday = parsed.getUTCDay() || 7
  parsed.setUTCDate(parsed.getUTCDate() - weekday + 1)
  return parsed.toISOString().slice(0, 10)
}

/** 生成有符号堆叠柱。各分类各自在零线两侧累计，因此退货不会被正销量抵消。 */
export function buildDiscountDailyChartModel(data: DiscountChartDaily[], width = 720, height = 248, classificationUnavailable = false): DiscountChartModel {
  const plotLeft = 42
  const plotRight = width - 12
  const plotTop = 18
  const plotBottom = height - 32
  const normalized = data.map((item) => {
    const unavailable = item.metrics === null
    const metrics = item.metrics
    const pending = metrics !== null && (metrics.discountStatus === 'pending' || classificationUnavailable)
    const regularQuantity = finite(metrics?.regularQuantity)
    const discountQuantity = finite(metrics?.discountQuantity)
    const unknownQuantity = finite(metrics?.unknownQuantity)
    return {
      pending,
      discountPending: metrics?.discountStatus === 'pending',
      date: item.date,
      unavailable,
      quantity: finite(metrics?.quantity),
      salesAmount: finite(metrics?.salesAmount),
      regularQuantity,
      discountQuantity,
      unknownQuantity,
      originalPriceMin: metrics?.originalPriceMin ?? null,
      originalPriceMax: metrics?.originalPriceMax ?? null,
      discountPriceMin: metrics?.discountPriceMin ?? null,
      discountPriceMax: metrics?.discountPriceMax ?? null,
      positive: metrics === null ? 0 : (pending ? [finite(metrics.quantity)] : [regularQuantity, discountQuantity, unknownQuantity]).filter((value) => value > 0).reduce((sum, value) => sum + value, 0),
      negative: metrics === null ? 0 : (pending ? [finite(metrics.quantity)] : [regularQuantity, discountQuantity, unknownQuantity]).filter((value) => value < 0).reduce((sum, value) => sum + value, 0),
    }
  })
  const minValue = Math.min(0, ...normalized.map((point) => point.negative))
  const maxValue = Math.max(0, ...normalized.map((point) => point.positive), 1)
  const values = tickValues(minValue, maxValue)
  const axisMin = values[0] ?? minValue
  const axisMax = values[values.length - 1] ?? maxValue
  const scale = (plotBottom - plotTop) / Math.max(1, axisMax - axisMin)
  const y = (value: number) => plotBottom - (value - axisMin) * scale
  const zeroY = y(0)
  const slotWidth = (plotRight - plotLeft) / Math.max(1, normalized.length)
  const barWidth = Math.max(0.5, Math.min(28, slotWidth * 0.62))

  const points = normalized.map((point, index) => {
    const x = plotLeft + slotWidth * index + (slotWidth - barWidth) / 2
    let positiveBase = 0
    let negativeBase = 0
    const series: Array<{ kind: DiscountChartKind; key: 'quantity' | 'regularQuantity' | 'discountQuantity' | 'unknownQuantity' }> = point.unavailable ? [] : point.pending ? [{ kind: 'total', key: 'quantity' }] : SERIES
    const segments = series.map(({ kind, key }) => {
      const value = point[key]
      if (!value) return { kind, value, x, y: zeroY, width: barWidth, height: 0 }
      if (value > 0) {
        const next = positiveBase + value
        const segment = { kind, value, x, y: y(next), width: barWidth, height: Math.abs(y(next) - y(positiveBase)) }
        positiveBase = next
        return segment
      }
      const next = negativeBase + value
      const segment = { kind, value, x, y: y(negativeBase), width: barWidth, height: Math.abs(y(next) - y(negativeBase)) }
      negativeBase = next
      return segment
    })
    return { ...point, x: x + barWidth / 2, hitX: plotLeft + slotWidth * index, hitWidth: slotWidth, segments }
  })

  return {
    width, height, zeroY, plotLeft, plotRight, plotTop, plotBottom,
    slotWidth,
    minValue: axisMin,
    maxValue: axisMax,
    points,
    ticks: values.map((value) => ({ value, y: y(value) })),
    xTicks: xTickIndices(points.length).map((index) => ({ date: points[index].date, x: points[index].x })),
    // 以严格 UTC 日期归属的周一比较相邻点；缺日跨周时仍在当前柱槽左侧分隔，首日不重复左边界。
    weekDividers: points.flatMap((point, index) => {
      if (index === 0) return []
      const previousWeek = naturalWeekKey(points[index - 1].date)
      const currentWeek = naturalWeekKey(point.date)
      return previousWeek && currentWeek && previousWeek !== currentWeek
        ? [{ date: point.date, x: plotLeft + slotWidth * index }]
        : []
    }),
  }
}

/** 图上的一块矩形区域（SVG 坐标），用于标签避让与提示框定位。 */
export interface ChartBox { x1: number; x2: number; y1: number; y2: number }

export interface DiscountChartValueLabel {
  index: number
  /** 完整千分位数字；两侧邻柱之间放不下且 |值| ≥ 1000 时改用 1.2k 缩写，提示框仍显示完整数。 */
  text: string
  x: number
  /** 文字基线 y。 */
  y: number
  /** 当前图中 |净销量| 最大的日期，标签加粗。 */
  peak: boolean
  box: ChartBox
}

const labelNumberFormatter = new Intl.NumberFormat('en-AU')
const boxesOverlap = (a: ChartBox, b: ChartBox) => a.x1 < b.x2 && b.x1 < a.x2 && a.y1 < b.y2 && b.y1 < a.y2

/** 按 10px 系统字体（含加粗）偏宽估算标签宽度，宁可少标也不重叠；不依赖 DOM，便于单测。 */
export function estimateValueLabelWidth(text: string) {
  let width = 0
  for (const char of text) width += /\d/.test(char) ? 6 : char === ',' || char === '.' ? 2.9 : char === 'k' ? 5.6 : 3.8
  return width
}

function compactQuantity(value: number) {
  const abs = Math.abs(value)
  if (abs < 1000) return labelNumberFormatter.format(value)
  return `${(value / 1000).toFixed(abs >= 10000 ? 0 : 1).replace(/\.0$/, '')}k`
}

/**
 * 柱顶数值标注（贪心避让）：
 * 1. 只标已统计且净销量非 0 的日期；按 |值| 从大到小、同值取较新日期依次尝试放置；
 * 2. 与已放标签、其他日期的柱子、或调用方传入的避让区（悬浮提示框）相交就跳过——永不重叠、不压邻柱；
 * 3. 按柱槽疏密分档：≥24px 全标；9–24px 不标低于峰值 5% 的；<9px（约 80 天以上）只标 ≥ 峰值 20% 的；
 * 4. 正值标在柱顶上方，负值（退货）标在柱底下方；下方空间会碰到 x 轴日期文字时就不标。
 */
export function buildValueLabels(model: DiscountChartModel, options: { avoid?: ChartBox | null; measure?: (text: string) => number } = {}): DiscountChartValueLabel[] {
  const measure = options.measure ?? estimateValueLabelWidth
  const floorRatio = model.slotWidth >= 24 ? 0 : model.slotWidth >= 9 ? 0.05 : 0.2
  const barBoxes = model.points.map((point) => {
    const visible = point.segments.filter((segment) => segment.height > 0)
    if (!visible.length) return null
    return {
      x1: visible[0].x,
      x2: visible[0].x + visible[0].width,
      y1: Math.min(...visible.map((segment) => segment.y)),
      y2: Math.max(...visible.map((segment) => segment.y + segment.height)),
    }
  })
  const maxAbs = Math.max(0, ...model.points.map((point) => (point.unavailable ? 0 : Math.abs(point.quantity))))
  if (maxAbs === 0) return []
  const candidates = model.points
    .map((point, index) => ({ point, index }))
    .filter(({ point, index }) => !point.unavailable && point.quantity !== 0 && barBoxes[index] && Math.abs(point.quantity) >= maxAbs * floorRatio)
    .sort((left, right) => Math.abs(right.point.quantity) - Math.abs(left.point.quantity) || right.index - left.index)
  const placed: DiscountChartValueLabel[] = []
  for (const { point, index } of candidates) {
    const bar = barBoxes[index]!
    const full = labelNumberFormatter.format(point.quantity)
    // 当前柱两侧邻柱内缘之间的可用宽度；完整数字放得下才用完整写法。
    const room = 2 * model.slotWidth - (bar.x2 - bar.x1) - 4
    const text = measure(full) <= room ? full : compactQuantity(point.quantity)
    const width = measure(text)
    const positive = point.quantity > 0
    const y = positive ? Math.max(9, bar.y1 - 4) : bar.y2 + 11
    // 退货柱底常贴近坐标轴最小刻度：允许标签略低于绘图区，但不能碰到下方 x 轴日期文字（其顶部约在 plotBottom + 9）。
    if (!positive && y > model.plotBottom + 7) continue
    const x = Math.min(Math.max(point.x, model.plotLeft + width / 2), model.plotRight - width / 2)
    const box = { x1: x - width / 2 - 1, x2: x + width / 2 + 1, y1: y - 8, y2: y + 1 }
    if (options.avoid && boxesOverlap(options.avoid, box)) continue
    if (placed.some((label) => boxesOverlap(label.box, box))) continue
    if (barBoxes.some((other, otherIndex) => other && otherIndex !== index && boxesOverlap(other, box))) continue
    placed.push({ index, text, x, y, peak: Math.abs(point.quantity) === maxAbs, box })
  }
  return placed
}

/**
 * 悬浮提示框定位：先放在日期槽右侧，超出图宽就翻到左侧，永远不压回当前列；
 * 两侧都放不下（极窄图）才贴边。提示框本身不接收鼠标（pointer-events: none），即使重叠也不会触发 mouseleave 闪烁。
 */
export function placeChartTooltip(model: DiscountChartModel, index: number, size: { width: number; height: number }, gap = 6): ChartBox & { side: 'right' | 'left' | 'edge' } {
  const point = model.points[index]
  const right = point.hitX + point.hitWidth + gap
  const left = point.hitX - gap - size.width
  // 贴着绘图区顶端放，让最高的完整提示框也在 x 轴日期胶囊之上结束，不遮挡悬停日期。
  const y1 = Math.max(2, model.plotTop - 16)
  let x1: number
  let side: 'right' | 'left' | 'edge'
  if (right + size.width <= model.width - 2) { x1 = right; side = 'right' }
  else if (left >= 2) { x1 = left; side = 'left' }
  else { x1 = Math.max(2, Math.min(right, model.width - size.width - 2)); side = 'edge' }
  return { x1, x2: x1 + size.width, y1, y2: y1 + size.height, side }
}
