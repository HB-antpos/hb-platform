import type {
  HolidayProduct,
  HolidayBenchmark,
  HolidayCategory,
  HolidayCategoryKey,
  HolidayDaily,
  HolidaySalesReport,
  HolidayStore,
  HolidayTotals,
} from '../../../services/holidaySalesReportService'

/** 大类显示名的消息键（页面级消息文件里）。写成字面量映射，静态契约测试才能核对到每个键。 */
export const CATEGORY_LABEL_KEY: Record<HolidayCategoryKey, string> = {
  Costume: 'category.Costume',
  Decoration: 'category.Decoration',
  MakeUp: 'category.MakeUp',
  Accessory: 'category.Accessory',
  Party: 'category.Party',
  MaskWig: 'category.MaskWig',
  Animated: 'category.Animated',
}

/** 大类固定配色（图例、条形、标签共用），选自同一套后台色板。 */
export const CATEGORY_COLOR: Record<HolidayCategoryKey, string> = {
  Costume: '#fa8c16',
  Decoration: '#722ed1',
  MakeUp: '#cf1322',
  Accessory: '#13c2c2',
  Party: '#faad14',
  MaskWig: '#2f54eb',
  Animated: '#52c41a',
}

/** 关键结论、对比指标、对比结论的消息键：同样写成字面量映射，避免动态拼接键。 */
export const INSIGHT_KEY = {
  lastWeek: 'insight.lastWeek',
  topCategory: 'insight.topCategory',
  lostCategory: 'insight.lostCategory',
  leftCategory: 'insight.leftCategory',
} as const

export const BENCHMARK_LABEL_KEY = {
  amount: 'benchmark.amount',
  sellThrough: 'benchmark.sellThrough',
  early: 'benchmark.early',
  lost: 'benchmark.lost',
  left: 'benchmark.left',
  after: 'benchmark.after',
} as const

export const VERDICT_KEY = {
  better: 'benchmark.better',
  worse: 'benchmark.worse',
  same: 'benchmark.same',
} as const

/** 榜单分批展开：先显示 10 个，之后每次多显示 10 个；后端每张榜最多返回 50 个（与 HolidaySalesReportBuilder.ListSize 一致）。 */
export const BOARD_INITIAL_COUNT = 10
export const BOARD_STEP = 10
export const BOARD_LIMIT = 50

/** 点「再显示」后的可见数量，不超过榜单总数。 */
export function nextVisibleCount(current: number, total: number): number {
  return Math.min(current + BOARD_STEP, total)
}

export type Tone = 'great' | 'ok' | 'warn' | 'bad' | 'none'

// ---------- 格式化 ----------

/** 金额：澳元整数，千分位；不显示小数（报表级别，分币没有意义）。 */
export function formatMoney(value: number): string {
  const rounded = Math.round(value)
  return `${rounded < 0 ? '-' : ''}$${Math.abs(rounded).toLocaleString('en-US')}`
}

/** 图上小字用的紧凑金额：≥$1,000 显示成 $1.2k。 */
export function formatMoneyCompact(value: number): string {
  return Math.abs(value) >= 1000 ? `$${(value / 1000).toFixed(1)}k` : formatMoney(value)
}

export function formatCount(value: number): string {
  return Math.round(value).toLocaleString('en-US')
}

/** 售罄率：null 显示「—」（没有进货，不是 0%）；超过 100% 说明卖了往年存货，显示「100%+」。 */
export function formatSellThrough(value: number | null): string {
  if (value === null) return '—'
  if (value > 1) return '100%+'
  return `${Math.round(value * 100)}%`
}

export function formatPercent(value: number): string {
  return `${Math.round(value * 100)}%`
}

/** 售罄率的颜色档：≥90% 绿（卖得好）、60~90 蓝、35~60 橙、<35 红；没有进货为灰。 */
export function sellThroughTone(value: number | null): Tone {
  if (value === null) return 'none'
  if (value >= 0.9) return 'great'
  if (value >= 0.6) return 'ok'
  if (value >= 0.35) return 'warn'
  return 'bad'
}

export const TONE_COLOR: Record<Tone, string> = {
  great: '#52c41a',
  ok: '#1677ff',
  warn: '#faad14',
  bad: '#ff4d4f',
  none: '#d9d9d9',
}

/** yyyy-MM-dd 是否周一（澳洲零售周以周一起算）。按 UTC 解析，不受浏览器时区影响。 */
export function isMonday(date: string): boolean {
  const [year, month, day] = date.split('-').map(Number)
  return new Date(Date.UTC(year, month - 1, day)).getUTCDay() === 1
}

/** yyyy-MM-dd → M/d。 */
export function formatMonthDay(date: string): string {
  const [, month, day] = date.split('-')
  return `${Number(month)}/${Number(day)}`
}

// ---------- 年份与阶段 ----------

/** 默认年份：当年 8 月 1 日之前节日季还没开始，看去年。today 为悉尼时区的 yyyy-MM-dd。 */
export function defaultYear(today: string): number {
  const year = Number(today.slice(0, 4))
  return Number(today.slice(5, 7)) < 8 ? year - 1 : year
}

/** 可选年份，新到旧；后端从 2024 起有统计。 */
export function yearOptions(today: string): number[] {
  const current = Number(today.slice(0, 4))
  const years: number[] = []
  for (let year = current; year >= 2024; year -= 1) years.push(year)
  return years
}

/** 数据止日不晚于万圣节前一天，视为节前进行中；否则已过节。 */
export function seasonPhase(report: Pick<HolidaySalesReport, 'holidayDate' | 'salesThrough'>): 'running' | 'ended' {
  return report.salesThrough < report.holidayDate ? 'running' : 'ended'
}

// ---------- 日销售曲线 ----------

export interface ChartWeek {
  /** 周一起算；首周可能不满 7 天，末周以数据止日为界。 */
  startDate: string
  endDate: string
  sum: number
  x0: number
  x1: number
}

export interface ChartGeometry {
  width: number
  height: number
  /** 绘图区顶部纵坐标（上方留给每周合计小字）。 */
  top: number
  line: string
  area: string
  yTicks: { y: number; value: number }[]
  xTicks: { x: number; label: string }[]
  /** 节日当天的横坐标与金额；曲线范围外为 null。 */
  holiday: { x: number; y: number; amount: number } | null
  /** 节前最后 7 天（含当天）的高亮条。 */
  weekBand: { x: number; width: number } | null
  /** 每周一的分割线（横坐标 + 日期）。 */
  weekLines: { x: number; date: string }[]
  /** 每周的起止与合计，用来在曲线上方标周合计、悬停时读本周合计。 */
  weeks: ChartWeek[]
  /** 每天的坐标，悬停时按横坐标反查。 */
  points: { x: number; y: number; date: string; amount: number }[]
}

/** 取不小于 value 的「好看」刻度上限：1 / 2 / 2.5 / 5 / 10 × 10^n。 */
export function niceCeil(value: number): number {
  if (value <= 0) return 1
  const exponent = Math.floor(Math.log10(value))
  const base = 10 ** exponent
  for (const step of [1, 2, 2.5, 5, 10]) {
    if (value <= step * base) return step * base
  }
  return 10 * base
}

export function buildChart(daily: readonly HolidayDaily[], holidayDate: string, width = 820, height = 250): ChartGeometry {
  const left = 52
  const right = 12
  const top = 34
  const bottom = 26
  const count = daily.length
  const max = daily.reduce((current, row) => Math.max(current, row.amount), 0)
  const ceiling = niceCeil(max)
  const x = (index: number) => (count <= 1 ? left : left + ((width - left - right) * index) / (count - 1))
  const y = (value: number) => top + (height - top - bottom) * (1 - value / ceiling)

  const points = daily.map((row, index) => ({ x: x(index), y: y(row.amount), date: row.date, amount: row.amount }))
  const line = points.map((point) => `${point.x.toFixed(1)},${point.y.toFixed(1)}`).join(' ')
  const baseline = y(0)
  const area = count === 0 ? '' : `${left},${baseline} ${line} ${x(count - 1).toFixed(1)},${baseline}`

  const holidayIndex = daily.findIndex((row) => row.date === holidayDate)
  const holiday = holidayIndex < 0 ? null : { x: points[holidayIndex].x, y: points[holidayIndex].y, amount: daily[holidayIndex].amount }
  const weekStart = holidayIndex < 0 ? -1 : Math.max(holidayIndex - 6, 0)
  const weekBand = holidayIndex < 0 ? null : { x: points[weekStart].x, width: points[holidayIndex].x - points[weekStart].x }

  const yTicks = [0, 1, 2, 3, 4].map((step) => ({ y: y((ceiling * step) / 4), value: (ceiling * step) / 4 }))

  // 周分割线画在每个周一的位置；横轴刻度与它对齐，标出每周从几月几号开始
  const weekLines = points.filter((point) => isMonday(point.date)).map((point) => ({ x: point.x, date: point.date }))
  const xTicks = weekLines.map((weekLine) => ({ x: weekLine.x, label: formatMonthDay(weekLine.date) }))

  // 按周分组：遇到周一开新周；数据第一天不是周一时，第一周从曲线左端开始
  const groups: { start: number; end: number; sum: number }[] = []
  daily.forEach((row, index) => {
    const last = groups[groups.length - 1]
    if (!last || isMonday(row.date)) groups.push({ start: index, end: index, sum: row.amount })
    else {
      last.end = index
      last.sum += row.amount
    }
  })
  const weeks: ChartWeek[] = groups.map((group, index) => ({
    startDate: daily[group.start].date,
    endDate: daily[group.end].date,
    sum: group.sum,
    x0: index === 0 ? left : x(group.start),
    x1: index === groups.length - 1 ? x(count - 1) : x(groups[index + 1].start),
  }))

  return { width, height, top, line, area, yTicks, xTicks, holiday, weekBand, weekLines, weeks, points }
}

/** 悬停横坐标 → 最近的一天（落在曲线之外返回 null）。 */
export function nearestPoint(chart: ChartGeometry, svgX: number) {
  if (chart.points.length === 0) return null
  let best = chart.points[0]
  for (const point of chart.points) {
    if (Math.abs(point.x - svgX) < Math.abs(best.x - svgX)) best = point
  }
  return best
}

/** 某天所在的周（悬停读数用）。 */
export function weekOf(chart: ChartGeometry, date: string): ChartWeek | null {
  return chart.weeks.find((week) => date >= week.startDate && date <= week.endDate) ?? null
}

/** 各周合计里最大的一个；用来判断哪些周「够大」值得加粗（相对值，单店和合计视图都适用）。 */
export function heaviestWeekSum(weeks: readonly ChartWeek[]): number {
  return weeks.reduce((current, week) => Math.max(current, week.sum), 0)
}

/** 周一至周日的星期简称，按界面语言；日期按 UTC 解析，不受浏览器时区影响。 */
export function formatWeekday(date: string, language: string): string {
  const [year, month, day] = date.split('-').map(Number)
  return new Intl.DateTimeFormat(language.startsWith('zh') ? 'zh-CN' : 'en-AU', { weekday: 'short', timeZone: 'UTC' }).format(
    new Date(Date.UTC(year, month - 1, day)),
  )
}

// ---------- 钻取抽屉里的商品筛选 / 排序 ----------

export type ProductFilter = 'all' | 'early' | 'slow' | 'over'
export type ProductSort = 'amount' | 'sellThrough' | 'leftover' | 'lost'

/** 与榜单同一口径：提前卖完 = 节前卖断；滞销 = 进货 ≥12 件且售罄率 <35%；库存多 = 还有剩余。 */
export function matchesProductFilter(product: HolidayProduct, filter: ProductFilter): boolean {
  if (filter === 'early') return product.soldOutStoreCount > 0
  if (filter === 'slow') return product.purchaseQuantity >= SLOW_MIN_PURCHASE && (product.sellThrough ?? 1) < SLOW_SELL_THROUGH_BELOW
  if (filter === 'over') return product.leftoverQuantity > 0
  return true
}

export const SLOW_MIN_PURCHASE = 12
export const SLOW_SELL_THROUGH_BELOW = 0.35

export function productFilterCounts(products: readonly HolidayProduct[]): Record<ProductFilter, number> {
  return {
    all: products.length,
    early: products.filter((product) => matchesProductFilter(product, 'early')).length,
    slow: products.filter((product) => matchesProductFilter(product, 'slow')).length,
    over: products.filter((product) => matchesProductFilter(product, 'over')).length,
  }
}

/** 货号或品名包含关键词（忽略大小写与首尾空格）；关键词为空则都匹配。 */
export function matchesProductQuery(product: HolidayProduct, query: string): boolean {
  const keyword = query.trim().toLowerCase()
  return keyword === '' || product.productName.toLowerCase().includes(keyword) || product.itemNumber.toLowerCase().includes(keyword)
}

/** 排序：销售额 / 少卖 / 剩余进价 从大到小；售罄率从低到高，没有进货（null）的排最后。不改原数组。 */
export function sortProducts(products: readonly HolidayProduct[], sort: ProductSort): HolidayProduct[] {
  const copy = [...products]
  if (sort === 'sellThrough') {
    return copy.sort((a, b) => {
      if (a.sellThrough === null && b.sellThrough === null) return b.amount - a.amount
      if (a.sellThrough === null) return 1
      if (b.sellThrough === null) return -1
      return a.sellThrough - b.sellThrough || b.leftoverCost - a.leftoverCost
    })
  }
  const key = sort === 'leftover' ? 'leftoverCost' : sort === 'lost' ? 'estimatedLostAmount' : 'amount'
  return copy.sort((a, b) => b[key] - a[key] || b.amount - a.amount)
}

/** 钻取抽屉里商品清单：先显示 20 个，每点一次再多 20 个。 */
export const DRAWER_PAGE_SIZE = 20

export function nextDrawerCount(current: number, total: number): number {
  return Math.min(current + DRAWER_PAGE_SIZE, total)
}

/** 先按筛选和关键词过滤，再排序（不改原数组）。 */
export function filterAndSortProducts(products: readonly HolidayProduct[], filter: ProductFilter, query: string, sort: ProductSort): HolidayProduct[] {
  return sortProducts(
    products.filter((product) => matchesProductFilter(product, filter) && matchesProductQuery(product, query)),
    sort,
  )
}

// ---------- 关键结论 ----------

export interface Insight {
  id: 'lastWeek' | 'topCategory' | 'lostCategory' | 'leftCategory'
  color: string
  params: Record<string, string | number>
}

/** 自动结论：每条只用报告里已有的数字，没有数据支撑的不出（例如没有卖断就没有「卖断最严重」）。 */
export function buildInsights(report: HolidaySalesReport, categoryName: (key: HolidayCategoryKey) => string): Insight[] {
  const { totals, categories } = report
  const insights: Insight[] = []
  if (totals.amount > 0) {
    insights.push({
      id: 'lastWeek',
      color: '#1677ff',
      params: {
        lastWeek: formatPercent(totals.lastWeekAmount / totals.amount),
        holiday: formatPercent(totals.holidayDayAmount / totals.amount),
        holidayDate: formatMonthDay(report.holidayDate),
        after: formatMoney(totals.afterHolidayAmount),
      },
    })
  }
  const byAmount = [...categories].sort((a, b) => b.amount - a.amount)
  if (byAmount.length > 0 && totals.amount > 0 && byAmount[0].amount > 0) {
    insights.push({
      id: 'topCategory',
      color: '#fa8c16',
      params: {
        name: categoryName(byAmount[0].key),
        amount: formatMoney(byAmount[0].amount),
        share: formatPercent(byAmount[0].amount / totals.amount),
        second: byAmount[1] ? categoryName(byAmount[1].key) : '—',
      },
    })
  }
  const lost = [...categories].sort((a, b) => b.estimatedLostAmount - a.estimatedLostAmount)[0]
  if (lost && lost.estimatedLostAmount > 0) {
    insights.push({
      id: 'lostCategory',
      color: '#cf1322',
      params: { name: categoryName(lost.key), amount: formatMoney(lost.estimatedLostAmount) },
    })
  }
  const left = [...categories].sort((a, b) => b.leftoverCost - a.leftoverCost)[0]
  if (left && left.leftoverCost > 0) {
    insights.push({
      id: 'leftCategory',
      color: '#d46b08',
      params: { name: categoryName(left.key), cost: formatMoney(left.leftoverCost) },
    })
  }
  return insights
}

// ---------- 供应商构成 ----------

/** 两家供应商的销售额占比（0~1）；总额为 0 时都是 0，避免除零。 */
export function supplierShares(report: HolidaySalesReport): { code: string; name: string; amount: number; share: number }[] {
  const total = report.suppliers.reduce((sum, supplier) => sum + supplier.amount, 0)
  return report.suppliers.map((supplier) => ({
    code: supplier.supplierCode,
    name: supplier.supplierName,
    amount: supplier.amount,
    share: total > 0 ? supplier.amount / total : 0,
  }))
}

// ---------- 单店对比 ----------

export type Comparison = 'better' | 'worse' | 'same'

/** 数值越高越好（销售额、售罄率）或越低越好（卖断、少卖、剩余库存）。 */
export function compareToAverage(value: number, average: number, higherIsBetter: boolean): Comparison {
  if (Math.abs(value - average) < 1e-9) return 'same'
  return value > average === higherIsBetter ? 'better' : 'worse'
}

/** 本店大类售罄率与全店的差（百分点，整数）；任一缺失为 null。 */
export function categoryPointDiff(category: HolidayCategory, benchmark: HolidayBenchmark | null): number | null {
  const all = benchmark?.categorySellThrough[category.key]
  if (all === undefined || all === null || category.sellThrough === null) return null
  return Math.round((category.sellThrough - all) * 100)
}

export interface BenchmarkMetric {
  id: 'amount' | 'sellThrough' | 'early' | 'lost' | 'left' | 'after'
  kind: 'money' | 'percent' | 'count'
  value: number | null
  average: number | null
  higherIsBetter: boolean
}

export function benchmarkMetrics(totals: HolidayTotals, benchmark: HolidayBenchmark): BenchmarkMetric[] {
  return [
    { id: 'amount', kind: 'money', value: totals.amount, average: benchmark.averageAmount, higherIsBetter: true },
    { id: 'sellThrough', kind: 'percent', value: totals.sellThrough, average: benchmark.sellThrough, higherIsBetter: true },
    { id: 'early', kind: 'count', value: totals.earlySoldOutCount, average: benchmark.averageEarlySoldOutCount, higherIsBetter: false },
    { id: 'lost', kind: 'money', value: totals.estimatedLostAmount, average: benchmark.averageLostAmount, higherIsBetter: false },
    { id: 'left', kind: 'money', value: totals.leftoverCost, average: benchmark.averageLeftoverCost, higherIsBetter: false },
    { id: 'after', kind: 'money', value: totals.afterHolidayAmount, average: benchmark.averageAfterHolidayAmount, higherIsBetter: true },
  ]
}

export function formatMetric(kind: BenchmarkMetric['kind'], value: number | null): string {
  if (value === null) return '—'
  if (kind === 'money') return formatMoney(value)
  if (kind === 'percent') return formatSellThrough(value)
  return Number.isInteger(value) ? formatCount(value) : value.toFixed(1)
}

// ---------- 分店 ----------

/** 单店下拉的候选：有销售的店按销售额降序。 */
export function storeOptions(allReport: HolidaySalesReport | undefined): HolidayStore[] {
  return allReport ? [...allReport.stores].sort((a, b) => b.amount - a.amount) : []
}

/** 进入「分店独立看」时的默认店：已选且仍在列表里的沿用，否则销售额最高的一家。 */
export function resolveStore(current: string | null, options: readonly HolidayStore[]): string | null {
  if (current && options.some((store) => store.branchCode === current)) return current
  return options[0]?.branchCode ?? null
}

/** 上一家 / 下一家（循环）。 */
export function stepStore(current: string | null, options: readonly HolidayStore[], step: 1 | -1): string | null {
  if (options.length === 0) return null
  const index = options.findIndex((store) => store.branchCode === current)
  if (index < 0) return options[0].branchCode
  return options[(index + step + options.length) % options.length].branchCode
}

/** 一家店有销售但没有今年进货单（卖去年存货或调拨），售罄率不适用。 */
export function hasNoPurchase(store: HolidayStore): boolean {
  return store.purchaseQuantity <= 0
}
