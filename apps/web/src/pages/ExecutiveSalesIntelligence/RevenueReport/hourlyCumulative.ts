import { getHierarchyBranchCode } from './logic'
import type { RevenueBranch, RevenueHourly, RevenueWeeklyNode } from './types'

/**
 * 单日营业额按整点累计、对齐去年同一时刻比较。
 * 算法与移动端 apps/mobile/src/modules/reports/hourly-cumulative.ts 保持一致，两端截止整点与数字相同。
 */

/** 历史日期默认比较整天；截止整点取 24 表示不截断。 */
export const FULL_DAY_CUTOFF_HOUR = 24

/** 去年同期累计不足去年全天的 5% 时，百分比会被极小分母放大，改显示金额差。 */
export const LOW_BASE_SHARE = 0.05

/**
 * 截止整点按固定 UTC+10 计算。
 * 门店只分布在悉尼与布里斯班，小时桶是各店本地墙钟；取最西且无夏令时的 UTC+10，
 * 夏令时期间悉尼店的同一整点也已结束，任何门店都不会把半截小时拿去和去年整小时比较。
 */
export const CUTOFF_REFERENCE_UTC_OFFSET_MINUTES = 10 * 60

const HOURS_PER_DAY = 24

export interface HourlySeries {
  revenue: number[]
  compareRevenue: number[]
  orders: number[]
  compareOrders: number[]
  /** 两期任一有数据的最早小时；没有数据时为 null。 */
  firstHour: number | null
  /** 两期任一有数据的最晚小时 + 1，即坐标轴终点。 */
  endHour: number | null
}

export interface CumulativeTotals {
  revenue: number
  compareRevenue: number
  orders: number
  compareOrders: number
}

export interface CutoffResolution {
  /** 只比较 Hour < cutoffHour 的完整小时。 */
  cutoffHour: number
  /** 所选日期仍在营业中：截止之后还有进行中的小时。 */
  live: boolean
  /** 最近一次统计时刻在当前小时内的进度（0–1），用于画进行中的尾段。 */
  liveHourFraction: number
}

export type HourlyDetailStatus = 'complete' | 'live' | 'upcoming'

export interface HourlyDetailRow {
  hour: number
  /** 累计口径下这一行的「截至」整点，即 hour + 1。 */
  boundaryHour: number
  revenue: number
  compareRevenue: number
  orders: number
  compareOrders: number
  status: HourlyDetailStatus
  /** 这一行正好是当前截止整点，用于高亮。 */
  isCutoffRow: boolean
}

export interface CumulativePoint {
  hour: number
  value: number
}

export interface GapPoint {
  hour: number
  current: number
  compare: number
}

export interface GapRegion {
  /** 当期领先为 ahead，落后为 behind。 */
  tone: 'ahead' | 'behind'
  points: GapPoint[]
}

export interface CumulativeChartModel {
  startHour: number
  endHour: number
  currentPoints: CumulativePoint[]
  comparePoints: CumulativePoint[]
  gapRegions: GapRegion[]
  liveTail: CumulativePoint | null
  maxValue: number
}

function finiteOrZero(value: number | null | undefined) {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

function createEmptyHours() {
  return Array.from({ length: HOURS_PER_DAY }, () => 0)
}

function clampHour(hour: number) {
  return Math.max(0, Math.min(HOURS_PER_DAY, Math.trunc(hour)))
}

function pad(value: number) {
  return String(value).padStart(2, '0')
}

export function normalizeBranchKey(value: string | null | undefined) {
  return value?.trim().toLocaleUpperCase('en-AU') ?? ''
}

export function formatHourLabel(hour: number) {
  return `${pad(clampHour(hour))}:00`
}

/** 接口的小时是 "HH:00" 字符串；解析失败返回 null。 */
export function parseHourKey(value: string | number | null | undefined) {
  const hour = typeof value === 'number' ? value : Number.parseInt(String(value ?? '').trim(), 10)
  return Number.isInteger(hour) && hour >= 0 && hour < HOURS_PER_DAY ? hour : null
}

/** 把接口返回的逐小时行对齐成 24 个槽位；同一小时出现多行（多家店）时累加。 */
export function buildHourlySeries(rows: readonly RevenueHourly[]): HourlySeries {
  const series: HourlySeries = {
    revenue: createEmptyHours(),
    compareRevenue: createEmptyHours(),
    orders: createEmptyHours(),
    compareOrders: createEmptyHours(),
    firstHour: null,
    endHour: null,
  }
  for (const row of rows) {
    const hour = parseHourKey(row.hour)
    if (hour === null) continue
    series.revenue[hour] += finiteOrZero(row.revenue)
    series.compareRevenue[hour] += finiteOrZero(row.revenueLY)
    series.orders[hour] += finiteOrZero(row.orderCount)
    series.compareOrders[hour] += finiteOrZero(row.orderCountLY)
  }
  for (let hour = 0; hour < HOURS_PER_DAY; hour += 1) {
    const hasData = series.revenue[hour] !== 0
      || series.compareRevenue[hour] !== 0
      || series.orders[hour] !== 0
      || series.compareOrders[hour] !== 0
    if (!hasData) continue
    if (series.firstHour === null) series.firstHour = hour
    series.endHour = hour + 1
  }
  return series
}

/** 多店小时行按分店拆成各自的 24 小时序列（键为大写分店代码）。 */
export function groupHourlySeriesByBranch(rows: readonly RevenueHourly[]) {
  const rowsByBranch = new Map<string, RevenueHourly[]>()
  for (const row of rows) {
    const key = normalizeBranchKey(row.branchCode)
    if (!key) continue
    const branchRows = rowsByBranch.get(key)
    if (branchRows) branchRows.push(row)
    else rowsByBranch.set(key, [row])
  }
  const seriesByBranch = new Map<string, HourlySeries>()
  rowsByBranch.forEach((branchRows, key) => seriesByBranch.set(key, buildHourlySeries(branchRows)))
  return seriesByBranch
}

/** 整页快照总是带回全部授权门店的小时行，选中门店时在前端筛选，不再重新请求。 */
export function filterHourlyRowsByBranch(rows: readonly RevenueHourly[], branchCode: string | null) {
  if (!branchCode) return [...rows]
  const key = normalizeBranchKey(branchCode)
  return rows.filter(row => normalizeBranchKey(row.branchCode) === key)
}

/** 累计 Hour < cutoffHour 的值。 */
export function sumBeforeHour(values: readonly number[], cutoffHour: number) {
  const end = clampHour(cutoffHour)
  let total = 0
  for (let hour = 0; hour < end; hour += 1) total += values[hour] ?? 0
  return total
}

export function getCumulativeTotals(series: HourlySeries, cutoffHour: number): CumulativeTotals {
  return {
    revenue: sumBeforeHour(series.revenue, cutoffHour),
    compareRevenue: sumBeforeHour(series.compareRevenue, cutoffHour),
    orders: sumBeforeHour(series.orders, cutoffHour),
    compareOrders: sumBeforeHour(series.compareOrders, cutoffHour),
  }
}

export function isLowBase(compareCumulative: number, compareFullDay: number) {
  return compareFullDay > 0 && compareCumulative < compareFullDay * LOW_BASE_SHARE
}

/** 后端时间字段按 UTC 解释；缺少时区标记的字符串补 Z，避免被当成浏览器本地时间。 */
export function parseUtcTimestamp(value: string | null | undefined) {
  if (!value) return null
  const trimmed = value.trim()
  const hasZone = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(trimmed)
  const timestamp = Date.parse(hasZone ? trimmed : `${trimmed}Z`)
  return Number.isFinite(timestamp) ? timestamp : null
}

/** 统计完成时刻的本地「时:分」。 */
export function formatLocalClockTime(value: string | null | undefined) {
  const timestamp = parseUtcTimestamp(value)
  if (timestamp === null) return null
  const date = new Date(timestamp)
  return `${pad(date.getHours())}:${pad(date.getMinutes())}`
}

const sydneyDateFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: 'Australia/Sydney',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
})

/** 业务日按悉尼时区取，与后端 SalesStatisticsBusinessDate 一致，不受浏览器时区影响。 */
export function sydneyTodayKey(now: Date = new Date()) {
  return sydneyDateFormatter.format(now)
}

/**
 * 默认截止整点：历史日期比整天；当天取最近一次统计完成时刻所在整点（之前的小时都已完整）。
 * 统计时间未知时返回 null，调用方回退到不截断的旧口径，而不是猜一个整点。
 */
export function resolveDefaultCutoff({
  selectedDate,
  todayKey,
  statisticsCompletedAtUtc,
  utcOffsetMinutes = CUTOFF_REFERENCE_UTC_OFFSET_MINUTES,
}: {
  selectedDate: string
  todayKey: string
  statisticsCompletedAtUtc: string | null | undefined
  utcOffsetMinutes?: number
}): CutoffResolution | null {
  if (selectedDate < todayKey) return { cutoffHour: FULL_DAY_CUTOFF_HOUR, live: false, liveHourFraction: 0 }
  if (selectedDate > todayKey) return null

  const completedAt = parseUtcTimestamp(statisticsCompletedAtUtc)
  if (completedAt === null) return null
  const reference = new Date(completedAt + utcOffsetMinutes * 60_000)
  const referenceDate = `${reference.getUTCFullYear()}-${pad(reference.getUTCMonth() + 1)}-${pad(reference.getUTCDate())}`
  // 今天还没有跑过统计：没有任何完整小时。
  if (referenceDate < selectedDate) return { cutoffHour: 0, live: true, liveHourFraction: 0 }
  if (referenceDate > selectedDate) return { cutoffHour: FULL_DAY_CUTOFF_HOUR, live: false, liveHourFraction: 0 }
  return { cutoffHour: reference.getUTCHours(), live: true, liveHourFraction: reference.getUTCMinutes() / 60 }
}

/** 可点选的截止整点：从最早营业小时之后一直到默认截止（或最后营业小时）。 */
export function getCutoffOptions(series: HourlySeries, maxCutoffHour: number) {
  if (series.firstHour === null || series.endHour === null) return []
  const end = Math.min(clampHour(maxCutoffHour), series.endHour)
  const options: number[] = []
  for (let hour = series.firstHour + 1; hour <= end; hour += 1) options.push(hour)
  return options
}

/** 用户点选的整点不能晚于默认截止；超出或未选时回到默认。 */
export function resolveEffectiveCutoff(selectedCutoffHour: number | null, defaultCutoffHour: number) {
  if (selectedCutoffHour === null) return defaultCutoffHour
  return selectedCutoffHour <= defaultCutoffHour ? selectedCutoffHour : defaultCutoffHour
}

/** 截止整点落在营业时段之后（例如历史日期取 24）时，显示为最后营业整点。 */
export function getDisplayCutoffHour(series: HourlySeries, cutoffHour: number) {
  if (series.endHour === null) return cutoffHour
  return Math.min(cutoffHour, series.endHour)
}

/**
 * 时段表的行：累计口径把本期与对比期都从开门累加到该小时结束；
 * 当天进行中的小时不算增长率，尚未到达的小时只保留去年值，避免出现 $0 / −100%。
 */
export function buildHourlyDetailRows(
  series: HourlySeries,
  { cutoffHour, live, cumulative, highlightCutoffHour = cutoffHour }: {
    /** 默认截止（最近完整整点）：决定进行中 / 未到的状态。 */
    cutoffHour: number
    live: boolean
    cumulative: boolean
    /** 用户点选的截止整点：只决定高亮哪一行。 */
    highlightCutoffHour?: number
  },
): HourlyDetailRow[] {
  if (series.firstHour === null || series.endHour === null) return []
  const rows: HourlyDetailRow[] = []
  for (let hour = series.firstHour; hour < series.endHour; hour += 1) {
    const status: HourlyDetailStatus = !live || hour < cutoffHour ? 'complete' : hour === cutoffHour ? 'live' : 'upcoming'
    const end = hour + 1
    rows.push({
      hour,
      boundaryHour: end,
      revenue: cumulative ? sumBeforeHour(series.revenue, end) : series.revenue[hour],
      compareRevenue: cumulative ? sumBeforeHour(series.compareRevenue, end) : series.compareRevenue[hour],
      orders: cumulative ? sumBeforeHour(series.orders, end) : series.orders[hour],
      compareOrders: cumulative ? sumBeforeHour(series.compareOrders, end) : series.compareOrders[hour],
      status,
      // 逐小时口径下 11:00 这一行是 11–12 点，容易被误读为截至 11 点，故只在累计口径高亮。
      isCutoffRow: cumulative && end === highlightCutoffHour,
    })
  }
  return rows
}

/** 累计曲线的数据模型（与屏幕坐标无关，便于单测）。 */
export function buildCumulativeChartModel(
  series: HourlySeries,
  { cutoffHour, live, liveHourFraction }: CutoffResolution,
): CumulativeChartModel | null {
  if (series.firstHour === null || series.endHour === null) return null
  const startHour = series.firstHour
  const endHour = Math.max(series.endHour, live ? Math.min(cutoffHour + 1, HOURS_PER_DAY) : series.endHour)
  const lastCurrentHour = Math.max(startHour, Math.min(cutoffHour, endHour))

  const comparePoints: CumulativePoint[] = []
  for (let hour = startHour; hour <= endHour; hour += 1) {
    comparePoints.push({ hour, value: sumBeforeHour(series.compareRevenue, hour) })
  }
  const currentPoints: CumulativePoint[] = []
  for (let hour = startHour; hour <= lastCurrentHour; hour += 1) {
    currentPoints.push({ hour, value: sumBeforeHour(series.revenue, hour) })
  }

  const liveTotal = sumBeforeHour(series.revenue, HOURS_PER_DAY)
  const cutoffTotal = currentPoints[currentPoints.length - 1]?.value ?? 0
  // 进行中小时已有入账时才画虚线尾段，位置按统计时刻在该小时内的进度。
  const liveTail = live && liveTotal > cutoffTotal
    ? { hour: lastCurrentHour + Math.max(0.1, Math.min(1, liveHourFraction)), value: liveTotal }
    : null

  const gapRegions: GapRegion[] = []
  let region: GapRegion | null = null
  for (let index = 0; index < currentPoints.length; index += 1) {
    const point: GapPoint = { hour: currentPoints[index].hour, current: currentPoints[index].value, compare: comparePoints[index].value }
    const difference = point.current - point.compare
    const tone: GapRegion['tone'] = difference >= 0 ? 'ahead' : 'behind'
    if (!region) {
      region = { tone, points: [point] }
      continue
    }
    const previous: GapPoint = region.points[region.points.length - 1]
    const previousDifference = previous.current - previous.compare
    if (previousDifference * difference < 0) {
      // 两条累计线在这一小时内交叉：按线性插值切开，前后分别着色。
      const ratio = previousDifference / (previousDifference - difference)
      const value = previous.current + ratio * (point.current - previous.current)
      const crossing: GapPoint = { hour: previous.hour + ratio * (point.hour - previous.hour), current: value, compare: value }
      region.points.push(crossing)
      gapRegions.push(region)
      region = { tone, points: [crossing, point] }
      continue
    }
    if (previousDifference === 0 && difference !== 0) region.tone = tone
    region.points.push(point)
  }
  if (region && region.points.length > 1) gapRegions.push(region)

  const maxValue = Math.max(
    liveTotal,
    ...comparePoints.map(point => point.value),
    ...currentPoints.map(point => point.value),
  )
  return { startHour, endHour, currentPoints, comparePoints, gapRegions, liveTail, maxValue }
}

/**
 * 分店排行对齐到截止整点：本期与同期都只累计 Hour < cutoffHour，再按对齐后的营业额重排。
 * 小时数据里没有出现的分店（两期都没有销售）按 0 处理，与日统计表的口径一致。
 */
export function alignBranchesToCutoff(
  branches: readonly RevenueBranch[],
  seriesByBranch: ReadonlyMap<string, HourlySeries>,
  cutoffHour: number,
): RevenueBranch[] {
  return branches
    .map((branch, index) => {
      const series = seriesByBranch.get(normalizeBranchKey(branch.branchCode))
      const totals = series ? getCumulativeTotals(series, cutoffHour) : { revenue: 0, compareRevenue: 0, orders: 0, compareOrders: 0 }
      const aligned: RevenueBranch = {
        ...branch,
        revenue: totals.revenue,
        revenueLY: totals.compareRevenue,
        orderCount: totals.orders,
        orderCountLY: totals.compareOrders,
        // 累计客单价必须用累计营业额 / 累计单数，不能平均逐小时客单价。
        aov: totals.orders > 0 ? totals.revenue / totals.orders : 0,
        aovLY: totals.compareOrders > 0 ? totals.compareRevenue / totals.compareOrders : 0,
      }
      return { aligned, index }
    })
    .sort((left, right) => right.aligned.revenue - left.aligned.revenue || left.index - right.index)
    .map(({ aligned }, index) => ({ ...aligned, rank: index + 1 }))
}

function alignedMetrics(totals: CumulativeTotals) {
  return {
    revenue: totals.revenue,
    revenueLY: totals.compareRevenue,
    orders: totals.orders,
    ordersLY: totals.compareOrders,
    aov: totals.orders > 0 ? totals.revenue / totals.orders : 0,
    aovLY: totals.compareOrders > 0 ? totals.compareRevenue / totals.compareOrders : 0,
    // 页面同比都按 revenue / revenueLY 现算；后端预算的 yoyChange 是整天口径，对齐后清空以免误用。
    yoyChange: null,
  }
}

/**
 * 单日周层级对齐到截止整点：分店与日期节点换成该店累计到截止整点的值，周节点为其下分店之和。
 * 只在查询范围是单日时调用：此时每个节点都只含这一天。
 */
export function alignWeeklyNodesToCutoff(
  nodes: readonly RevenueWeeklyNode[],
  seriesByBranch: ReadonlyMap<string, HourlySeries>,
  cutoffHour: number,
): RevenueWeeklyNode[] {
  const totalsFor = (node: RevenueWeeklyNode): CumulativeTotals => {
    const series = seriesByBranch.get(normalizeBranchKey(getHierarchyBranchCode(node)))
    return series ? getCumulativeTotals(series, cutoffHour) : { revenue: 0, compareRevenue: 0, orders: 0, compareOrders: 0 }
  }
  const alignNode = (node: RevenueWeeklyNode): RevenueWeeklyNode => {
    if (node.level !== 'week') {
      const children = node.children?.map(alignNode) ?? node.children
      return { ...node, ...alignedMetrics(totalsFor(node)), children }
    }
    const children = node.children?.map(alignNode) ?? []
    const totals = children.reduce<CumulativeTotals>((sum, child) => ({
      revenue: sum.revenue + child.revenue,
      compareRevenue: sum.compareRevenue + child.revenueLY,
      orders: sum.orders + child.orders,
      compareOrders: sum.compareOrders + child.ordersLY,
    }), { revenue: 0, compareRevenue: 0, orders: 0, compareOrders: 0 })
    return { ...node, ...alignedMetrics(totals), children }
  }
  return nodes.map(alignNode)
}

const ZERO_TOTALS: CumulativeTotals = { revenue: 0, compareRevenue: 0, orders: 0, compareOrders: 0 }

/** 小时金额相减会留下浮点尾差，统一取到分，并且不出现负数。 */
function nonNegativeCents(value: number) {
  return Math.max(0, Math.round(value * 100) / 100)
}

function sumChildTotals(children: readonly RevenueWeeklyNode[]): CumulativeTotals {
  return children.reduce<CumulativeTotals>((sum, child) => ({
    revenue: sum.revenue + child.revenue,
    compareRevenue: sum.compareRevenue + child.revenueLY,
    orders: sum.orders + child.orders,
    compareOrders: sum.compareOrders + child.ordersLY,
  }), { ...ZERO_TOTALS })
}

/**
 * 多日区间含今天：分店值 = 区间合计 − 今天全天日统计 + 今天截至截止整点的小时累计；
 * 同期同样把对应日换成截至同一整点。其余日期仍是全天日统计，与原口径一致。
 */
export function alignRangeBranchesToCutoff(
  branches: readonly RevenueBranch[],
  lastDayBranches: readonly RevenueBranch[],
  lastDaySeriesByBranch: ReadonlyMap<string, HourlySeries>,
  cutoffHour: number,
): RevenueBranch[] {
  const dayByBranch = new Map(lastDayBranches.map(branch => [normalizeBranchKey(branch.branchCode), branch]))
  return branches
    .map((branch, index) => {
      const key = normalizeBranchKey(branch.branchCode)
      const day = dayByBranch.get(key)
      const series = lastDaySeriesByBranch.get(key)
      const totals = series ? getCumulativeTotals(series, cutoffHour) : ZERO_TOTALS
      const revenue = nonNegativeCents(branch.revenue - (day?.revenue ?? 0) + totals.revenue)
      const revenueLY = nonNegativeCents(branch.revenueLY - (day?.revenueLY ?? 0) + totals.compareRevenue)
      const orderCount = Math.max(0, branch.orderCount - (day?.orderCount ?? 0) + totals.orders)
      const orderCountLY = Math.max(0, branch.orderCountLY - (day?.orderCountLY ?? 0) + totals.compareOrders)
      const aligned: RevenueBranch = {
        ...branch,
        revenue,
        revenueLY,
        orderCount,
        orderCountLY,
        aov: orderCount > 0 ? revenue / orderCount : 0,
        aovLY: orderCountLY > 0 ? revenueLY / orderCountLY : 0,
      }
      return { aligned, index }
    })
    .sort((left, right) => right.aligned.revenue - left.aligned.revenue || left.index - right.index)
    .map(({ aligned }, index) => ({ ...aligned, rank: index + 1 }))
}

/**
 * 多日区间的时段合计：截止整点及之后的小时，两期都去掉最后一天（今天 / 同期对应日），
 * 这样每个小时比较的都是相同天数的完整小时；截止之前的小时两期都含最后一天。
 */
export function alignRangeHourlyRows(
  rangeRows: readonly RevenueHourly[],
  lastDayRows: readonly RevenueHourly[],
  cutoffHour: number,
): RevenueHourly[] {
  const dayRows = new Map<string, RevenueHourly>()
  for (const row of lastDayRows) {
    const hour = parseHourKey(row.hour)
    if (hour !== null) dayRows.set(`${normalizeBranchKey(row.branchCode)}|${hour}`, row)
  }
  return rangeRows.map(row => {
    const hour = parseHourKey(row.hour)
    if (hour === null || hour < cutoffHour) return row
    const day = dayRows.get(`${normalizeBranchKey(row.branchCode)}|${hour}`)
    if (!day) return row
    return {
      ...row,
      revenue: nonNegativeCents(finiteOrZero(row.revenue) - finiteOrZero(day.revenue)),
      revenueLY: nonNegativeCents(finiteOrZero(row.revenueLY) - finiteOrZero(day.revenueLY)),
      orderCount: Math.max(0, finiteOrZero(row.orderCount) - finiteOrZero(day.orderCount)),
      orderCountLY: Math.max(0, finiteOrZero(row.orderCountLY) - finiteOrZero(day.orderCountLY)),
    }
  })
}

/**
 * 多日区间的周层级：只把最后一天的日期节点换成该店截至整点的累计，再向上重算分店与周节点。
 * 不含最后一天的周原样返回。
 */
export function alignRangeWeeklyToCutoff(
  nodes: readonly RevenueWeeklyNode[],
  lastDate: string,
  lastDaySeriesByBranch: ReadonlyMap<string, HourlySeries>,
  cutoffHour: number,
): RevenueWeeklyNode[] {
  return nodes.map(week => {
    let changed = false
    const children = (week.children ?? []).map(branch => {
      const dayIndex = branch.children?.findIndex(child => child.level === 'date' && child.hierarchy === lastDate) ?? -1
      if (!branch.children || dayIndex < 0) return branch
      const day = branch.children[dayIndex]!
      const series = lastDaySeriesByBranch.get(normalizeBranchKey(getHierarchyBranchCode(day)))
      const nextChildren = [...branch.children]
      nextChildren[dayIndex] = { ...day, ...alignedMetrics(series ? getCumulativeTotals(series, cutoffHour) : ZERO_TOTALS) }
      changed = true
      return { ...branch, ...alignedMetrics(sumChildTotals(nextChildren)), children: nextChildren }
    })
    return changed ? { ...week, ...alignedMetrics(sumChildTotals(children)), children } : week
  })
}

/** 固定 UTC+10 的当前整点（与截止整点同一参考时区），用于拿不到统计时间时推断哪些小时还没到。 */
export function referenceClockHour(now: number = Date.now(), utcOffsetMinutes = CUTOFF_REFERENCE_UTC_OFFSET_MINUTES) {
  return new Date(now + utcOffsetMinutes * 60_000).getUTCHours()
}

/**
 * 周层级收窄到单个分店：周节点换成该店分店节点的值，子节点只保留该店。
 * 与后端 focusBranchCodes=[分店] 的结果一致；该店当周无销售时整周不显示。
 */
export function scopeWeeklyToBranch(nodes: readonly RevenueWeeklyNode[], branchCode: string | null): RevenueWeeklyNode[] {
  if (!branchCode) return [...nodes]
  const key = normalizeBranchKey(branchCode)
  return nodes.flatMap(week => {
    const branch = week.children?.find(child => normalizeBranchKey(getHierarchyBranchCode(child)) === key)
    if (!branch) return []
    return [{
      ...week,
      revenue: branch.revenue,
      revenueLY: branch.revenueLY,
      orders: branch.orders,
      ordersLY: branch.ordersLY,
      aov: branch.aov,
      aovLY: branch.aovLY,
      yoyChange: branch.yoyChange,
      children: [branch],
    }]
  })
}
