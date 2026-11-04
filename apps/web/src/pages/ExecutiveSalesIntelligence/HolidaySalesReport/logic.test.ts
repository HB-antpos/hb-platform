import assert from 'node:assert/strict'
import type { HolidayCategory, HolidayDaily, HolidayProduct, HolidaySalesReport, HolidayStore } from '../../../services/holidaySalesReportService'
import {
  BOARD_INITIAL_COUNT,
  BOARD_LIMIT,
  BOARD_STEP,
  DRAWER_PAGE_SIZE,
  benchmarkMetrics,
  buildChart,
  buildInsights,
  categoryPointDiff,
  compareToAverage,
  defaultYear,
  filterAndSortProducts,
  formatMoneyCompact,
  formatWeekday,
  heaviestWeekSum,
  isMonday,
  matchesProductFilter,
  matchesProductQuery,
  nextDrawerCount,
  nextVisibleCount,
  productFilterCounts,
  sortProducts,
  weekOf,
  formatMetric,
  formatMoney,
  formatMonthDay,
  formatSellThrough,
  hasNoPurchase,
  nearestPoint,
  niceCeil,
  resolveStore,
  seasonPhase,
  sellThroughTone,
  stepStore,
  storeOptions,
  supplierShares,
  yearOptions,
} from './logic'

// 金额与百分比
assert.equal(formatMoney(283372.7478), '$283,373')
assert.equal(formatMoney(-12.4), '-$12')
assert.equal(formatMoney(0), '$0')
assert.equal(formatSellThrough(null), '—', '没有进货是「—」而不是 0%')
assert.equal(formatSellThrough(0), '0%')
assert.equal(formatSellThrough(0.827), '83%')
assert.equal(formatSellThrough(1.2), '100%+')
assert.equal(formatMonthDay('2026-10-05'), '10/5')

// 售罄率颜色档
assert.equal(sellThroughTone(null), 'none')
assert.equal(sellThroughTone(0.9), 'great')
assert.equal(sellThroughTone(0.89), 'ok')
assert.equal(sellThroughTone(0.6), 'ok')
assert.equal(sellThroughTone(0.59), 'warn')
assert.equal(sellThroughTone(0.35), 'warn')
assert.equal(sellThroughTone(0.34), 'bad')

// 年份：8 月前看去年；可选年份新到旧、到 2024
assert.equal(defaultYear('2026-07-31'), 2025)
assert.equal(defaultYear('2026-08-01'), 2026)
assert.equal(defaultYear('2027-01-15'), 2026)
assert.deepEqual(yearOptions('2026-11-04'), [2026, 2025, 2024])
assert.deepEqual(yearOptions('2024-10-01'), [2024])

// 阶段
assert.equal(seasonPhase({ holidayDate: '2026-10-31', salesThrough: '2026-10-20' }), 'running')
assert.equal(seasonPhase({ holidayDate: '2026-10-31', salesThrough: '2026-11-03' }), 'ended')

// 图表刻度
assert.equal(niceCeil(0), 1)
assert.equal(niceCeil(47227), 50000)
assert.equal(niceCeil(10001), 20000)
assert.equal(niceCeil(2300), 2500)
assert.equal(niceCeil(9), 10)

// 曲线几何：节日当天与「节前最后 7 天」高亮条
const days: HolidayDaily[] = []
for (let day = 1; day <= 40; day += 1) {
  const date = day <= 31 ? `2026-10-${String(day).padStart(2, '0')}` : `2026-11-${String(day - 31).padStart(2, '0')}`
  days.push({ date, amount: date === '2026-10-31' ? 40000 : 100 })
}
const chart = buildChart(days, '2026-10-31')
assert.equal(chart.points.length, 40)
assert.ok(chart.holiday && chart.holiday.amount === 40000)
assert.ok(chart.weekBand && chart.weekBand.width > 0)
const lastBand = chart.points[24] // 10/25 ~ 10/31 是最后 7 天
assert.equal(Math.round(chart.weekBand!.x), Math.round(lastBand.x))
assert.deepEqual(chart.xTicks.map((tick) => tick.label), ['10/5', '10/12', '10/19', '10/26', '11/2', '11/9'], '横轴刻度 = 每周一')
assert.deepEqual(chart.weekLines.map((weekLine) => weekLine.date), ['2026-10-05', '2026-10-12', '2026-10-19', '2026-10-26', '2026-11-02', '2026-11-09'], '周分割线画在每个周一')
assert.equal(chart.yTicks.length, 5)
assert.equal(chart.yTicks[4].value, 50000)
assert.equal(nearestPoint(chart, chart.points[10].x + 0.3)?.date, '2026-10-11')
assert.equal(nearestPoint(buildChart([], '2026-10-31'), 100), null)
assert.equal(buildChart(days, '2027-10-31').holiday, null, '节日不在曲线范围内时没有节日标记')
assert.equal(buildChart([], '2026-10-31').line, '', '没有数据不画线')
assert.deepEqual(buildChart([], '2026-10-31').weeks, [], '没有数据没有周')

// 周：周一起算；首周（10/1 周四起）不满 7 天；末周以数据止日为界
assert.equal(chart.weeks.length, 7)
assert.deepEqual(chart.weeks.map((week) => week.startDate), ['2026-10-01', '2026-10-05', '2026-10-12', '2026-10-19', '2026-10-26', '2026-11-02', '2026-11-09'])
assert.equal(chart.weeks[0].endDate, '2026-10-04')
assert.equal(chart.weeks[0].sum, 400, '首周只有 4 天')
assert.equal(chart.weeks[0].x0, 52, '首周从曲线左端开始')
assert.equal(chart.weeks[4].sum, 40600, '10/26–11/1 含节日当天')
assert.equal(chart.weeks[6].endDate, '2026-11-09')
assert.equal(chart.weeks[6].x1, chart.points[39].x, '末周到数据止日')
assert.equal(chart.weeks.reduce((sum, week) => sum + week.sum, 0), days.reduce((sum, row) => sum + row.amount, 0), '各周合计之和 = 总额')
assert.equal(heaviestWeekSum(chart.weeks), 40600)
assert.equal(heaviestWeekSum([]), 0)
assert.equal(weekOf(chart, '2026-10-31')?.startDate, '2026-10-26')
assert.equal(weekOf(chart, '2026-10-31')?.endDate, '2026-11-01')
assert.equal(weekOf(chart, '2026-09-01'), null, '不在曲线范围内')
assert.equal(chart.top, 34, '上方留给周合计小字')

// 周一判断按 UTC 解析
assert.equal(isMonday('2026-10-26'), true)
assert.equal(isMonday('2026-10-31'), false)
assert.equal(isMonday('2026-11-01'), false)
assert.match(formatWeekday('2026-10-31', 'zh'), /六/)
assert.match(formatWeekday('2026-10-31', 'en'), /Sat/)
assert.equal(formatMoneyCompact(141104), '$141.1k')
assert.equal(formatMoneyCompact(999), '$999')

// 榜单分批展开：先 10 个，每次 +10，最多 50；不超过总数
assert.equal(BOARD_INITIAL_COUNT, 10)
assert.equal(BOARD_STEP, 10)
assert.equal(BOARD_LIMIT, 50)
assert.equal(nextVisibleCount(10, 50), 20)
assert.equal(nextVisibleCount(40, 45), 45)
assert.equal(nextVisibleCount(10, 7), 7)
assert.equal(DRAWER_PAGE_SIZE, 20)
assert.equal(nextDrawerCount(20, 474), 40)
assert.equal(nextDrawerCount(460, 474), 474)

// 钻取抽屉里的商品筛选 / 搜索 / 排序
const product = (overrides: Partial<HolidayProduct>): HolidayProduct => ({
  productCode: 'P', itemNumber: 'XH0001', productName: 'Hanging Ghost', imageUrl: null, supplierCode: '257', categoryKey: 'Decoration',
  amount: 100, quantity: 10, purchaseQuantity: 20, sellThrough: 0.5, storeCount: 3, soldOutStoreCount: 0, daysEarly: null, soldOutDate: null,
  estimatedLostAmount: 0, leftoverQuantity: 0, leftoverCost: 0, ...overrides,
})
const sample = [
  product({ productCode: 'A', productName: 'Alpha Cape', itemNumber: 'XH1', amount: 500, sellThrough: 0.9, soldOutStoreCount: 2, daysEarly: 3, estimatedLostAmount: 40, purchaseQuantity: 30, leftoverQuantity: 2, leftoverCost: 6 }),
  product({ productCode: 'B', productName: 'Beta Mask', itemNumber: 'XH2', amount: 300, sellThrough: 0.2, purchaseQuantity: 40, leftoverQuantity: 32, leftoverCost: 90 }),
  product({ productCode: 'C', productName: 'Gamma Wig', itemNumber: 'HW3', amount: 50, sellThrough: null, purchaseQuantity: 0 }),
  product({ productCode: 'D', productName: 'Delta Hat', itemNumber: 'XH4', amount: 400, sellThrough: 0.2, purchaseQuantity: 11, leftoverQuantity: 9, leftoverCost: 20 }),
]
assert.deepEqual(productFilterCounts(sample), { all: 4, early: 1, slow: 1, over: 3 }, '滞销要进货 ≥12 件，没进货的（null）不算滞销')
assert.equal(matchesProductFilter(sample[2], 'slow'), false)
assert.equal(matchesProductFilter(sample[3], 'slow'), false, '进货 11 件不够 12 件')
assert.equal(matchesProductQuery(sample[0], '  alpha '), true, '忽略大小写与首尾空格')
assert.equal(matchesProductQuery(sample[0], 'xh1'), true, '货号也能搜')
assert.equal(matchesProductQuery(sample[0], 'zzz'), false)
assert.equal(matchesProductQuery(sample[0], ''), true)
assert.deepEqual(sortProducts(sample, 'amount').map((p) => p.productCode), ['A', 'D', 'B', 'C'])
assert.deepEqual(sortProducts(sample, 'sellThrough').map((p) => p.productCode), ['B', 'D', 'A', 'C'], '售罄率低在前，没进货的排最后；同率按剩余进价')
assert.deepEqual(sortProducts(sample, 'leftover').map((p) => p.productCode), ['B', 'D', 'A', 'C'])
assert.deepEqual(sortProducts(sample, 'lost').map((p) => p.productCode)[0], 'A')
assert.deepEqual(sample.map((p) => p.productCode), ['A', 'B', 'C', 'D'], '排序不能改原数组')
assert.deepEqual(filterAndSortProducts(sample, 'over', 'a', 'leftover').map((p) => p.productCode), ['B', 'D', 'A'], '筛选 + 搜索 + 排序')

// 关键结论
const category = (key: HolidayCategory['key'], amount: number, lost: number, left: number): HolidayCategory => ({
  key, amount, quantity: 0, skuCount: 0, purchaseQuantity: 0, sellThrough: 0.5, estimatedLostAmount: lost, leftoverQuantity: 0, leftoverCost: left,
})
const baseReport = {
  holidayDate: '2026-10-31',
  totals: { amount: 1000, lastWeekAmount: 530, holidayDayAmount: 170, afterHolidayAmount: 8 },
  categories: [category('Costume', 400, 10, 90), category('Decoration', 300, 80, 20)],
} as unknown as HolidaySalesReport
const insights = buildInsights(baseReport, (key) => `<${key}>`)
assert.deepEqual(insights.map((item) => item.id), ['lastWeek', 'topCategory', 'lostCategory', 'leftCategory'])
assert.equal(insights[0].params.lastWeek, '53%')
assert.equal(insights[0].params.holiday, '17%')
assert.equal(insights[0].params.holidayDate, '10/31')
assert.equal(insights[1].params.name, '<Costume>')
assert.equal(insights[1].params.second, '<Decoration>')
assert.equal(insights[2].params.name, '<Decoration>', '少卖最多的大类')
assert.equal(insights[3].params.name, '<Costume>', '剩余库存最多的大类')
const noLoss = buildInsights(
  { ...baseReport, categories: [category('Costume', 400, 0, 0)] } as HolidaySalesReport,
  (key) => key,
)
assert.deepEqual(noLoss.map((item) => item.id), ['lastWeek', 'topCategory'], '没有卖断 / 剩余就不出对应结论')
// 没有销售时不能用 0 做分母：不出「节前占比 / 销售最高」，但剩余库存的结论仍然成立
assert.deepEqual(
  buildInsights({ ...baseReport, totals: { ...baseReport.totals, amount: 0 } } as HolidaySalesReport, (key) => key).map((item) => item.id),
  ['lostCategory', 'leftCategory'],
)

// 供应商占比
const shares = supplierShares({ suppliers: [{ supplierCode: '257', supplierName: 'KFC', amount: 300, quantity: 0, purchaseQuantity: 0 }, { supplierCode: '243', supplierName: 'Brazco', amount: 100, quantity: 0, purchaseQuantity: 0 }] } as HolidaySalesReport)
assert.equal(shares[0].share, 0.75)
assert.equal(supplierShares({ suppliers: [{ supplierCode: '257', supplierName: 'KFC', amount: 0, quantity: 0, purchaseQuantity: 0 }] } as HolidaySalesReport)[0].share, 0, '总额为 0 不能除零')

// 对比
assert.equal(compareToAverage(10, 5, true), 'better')
assert.equal(compareToAverage(10, 5, false), 'worse')
assert.equal(compareToAverage(3, 5, false), 'better')
assert.equal(compareToAverage(5, 5, true), 'same')
const benchmark = { storeCount: 25, rank: 1, averageAmount: 11386, sellThrough: 0.82, averageEarlySoldOutCount: 24, averageLostAmount: 475, averageLeftoverCost: 1685, averageAfterHolidayAmount: 33, categorySellThrough: { Costume: 0.5, Party: null } }
assert.equal(categoryPointDiff({ ...category('Costume', 1, 0, 0), sellThrough: 0.77 }, benchmark), 27)
assert.equal(categoryPointDiff({ ...category('Costume', 1, 0, 0), sellThrough: 0.4 }, benchmark), -10)
assert.equal(categoryPointDiff({ ...category('Party', 1, 0, 0), sellThrough: 0.4 }, benchmark), null)
assert.equal(categoryPointDiff({ ...category('Costume', 1, 0, 0), sellThrough: null }, benchmark), null)
assert.equal(categoryPointDiff({ ...category('Costume', 1, 0, 0), sellThrough: 0.4 }, null), null)
const metrics = benchmarkMetrics({ amount: 58844, sellThrough: 0.93, earlySoldOutCount: 98, estimatedLostAmount: 3573, leftoverCost: 5410, afterHolidayAmount: 103 } as never, benchmark)
assert.deepEqual(metrics.map((metric) => metric.id), ['amount', 'sellThrough', 'early', 'lost', 'left', 'after'])
assert.equal(formatMetric('count', 24.5), '24.5')
assert.equal(formatMetric('count', 98), '98')
assert.equal(formatMetric('percent', null), '—')
assert.equal(formatMetric('money', 3573.4), '$3,573')

// 分店：候选按销售额降序；默认选最高；步进循环
const store = (branchCode: string, amount: number, purchaseQuantity = 10): HolidayStore => ({
  branchCode, branchName: branchCode, amount, quantity: 0, purchaseQuantity, sellThrough: null, earlySoldOutCount: 0, estimatedLostAmount: 0, leftoverCost: 0, afterHolidayAmount: 0,
})
const options = storeOptions({ stores: [store('1012', 5), store('1013', 9), store('1016', 7)] } as HolidaySalesReport)
assert.deepEqual(options.map((item) => item.branchCode), ['1013', '1016', '1012'])
assert.deepEqual(storeOptions(undefined), [])
assert.equal(resolveStore(null, options), '1013')
assert.equal(resolveStore('1012', options), '1012')
assert.equal(resolveStore('9999', options), '1013', '已选店不在列表里时回到销售额最高的店')
assert.equal(resolveStore(null, []), null)
assert.equal(stepStore('1012', options, 1), '1013', '最后一家再往后回到第一家')
assert.equal(stepStore('1013', options, -1), '1012', '第一家再往前回到最后一家')
assert.equal(stepStore(null, options, 1), '1013')
assert.equal(stepStore('1013', [], 1), null)
assert.equal(hasNoPurchase(store('1004', 1, 0)), true)
assert.equal(hasNoPurchase(store('1013', 1, 5)), false)

console.log('holidaySalesReport logic.test: ok')
