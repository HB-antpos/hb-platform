import assert from 'node:assert/strict'
import type { HolidayCategory, HolidayDaily, HolidaySalesReport, HolidayStore } from '../../../services/holidaySalesReportService'
import {
  benchmarkMetrics,
  buildChart,
  buildInsights,
  categoryPointDiff,
  compareToAverage,
  defaultYear,
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
assert.deepEqual(chart.xTicks.map((tick) => tick.label), ['10/1', '10/15', '11/1'])
assert.equal(chart.yTicks.length, 5)
assert.equal(chart.yTicks[4].value, 50000)
assert.equal(nearestPoint(chart, chart.points[10].x + 0.3)?.date, '2026-10-11')
assert.equal(nearestPoint(buildChart([], '2026-10-31'), 100), null)
assert.equal(buildChart(days, '2027-10-31').holiday, null, '节日不在曲线范围内时没有节日标记')
assert.equal(buildChart([], '2026-10-31').line, '', '没有数据不画线')

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
