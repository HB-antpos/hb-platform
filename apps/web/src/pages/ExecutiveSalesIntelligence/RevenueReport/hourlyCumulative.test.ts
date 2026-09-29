import assert from 'node:assert/strict'
import {
  FULL_DAY_CUTOFF_HOUR,
  alignBranchesToCutoff,
  alignRangeBranchesToCutoff,
  alignRangeHourlyRows,
  alignRangeWeeklyToCutoff,
  alignWeeklyNodesToCutoff,
  buildCumulativeChartModel,
  buildHourlyDetailRows,
  buildHourlySeries,
  filterHourlyRowsByBranch,
  formatLocalClockTime,
  getCumulativeTotals,
  getCutoffOptions,
  getDisplayCutoffHour,
  groupHourlySeriesByBranch,
  isLowBase,
  parseHourKey,
  parseUtcTimestamp,
  referenceClockHour,
  resolveDefaultCutoff,
  resolveEffectiveCutoff,
  scopeWeeklyToBranch,
  sumBeforeHour,
  sydneyTodayKey,
} from './hourlyCumulative'
import type { RevenueBranch, RevenueHourly, RevenueWeeklyNode } from './types'

function hourly(hour: number, revenue: number, revenueLY: number, branchCode = 'S1', orderCount = 0, orderCountLY = 0): RevenueHourly {
  return {
    hour: `${String(hour).padStart(2, '0')}:00`, branchCode, branchName: branchCode,
    revenue, revenueLY, orderCount, orderCountLY, percentage: 0, isPeak: false,
  }
}

// 与移动端同一组数据：2026-09-21 15:30 这一轮统计（今天到 15:30，去年同 ISO 周同星期全天）。
const today: Record<number, number> = { 8: 224, 9: 5596, 10: 8902, 11: 9030, 12: 9283, 13: 7955, 14: 6836, 15: 3583 }
const lastYear: Record<number, number> = {
  8: 405, 9: 6824, 10: 8736, 11: 9143, 12: 9026, 13: 8417, 14: 7242, 15: 6990, 16: 6576, 17: 2721, 18: 92,
}
const allStoreRows = Array.from({ length: 24 }, (_, hour) => hour)
  .filter(hour => today[hour] !== undefined || lastYear[hour] !== undefined)
  .map(hour => hourly(hour, today[hour] ?? 0, lastYear[hour] ?? 0, 'S1', today[hour] ? 10 : 0, lastYear[hour] ? 8 : 0))

// 小时解析与逐小时对齐
assert.equal(parseHourKey('09:00'), 9)
assert.equal(parseHourKey('23:00'), 23)
assert.equal(parseHourKey('24:00'), null)
assert.equal(parseHourKey('bad'), null)
const series = buildHourlySeries(allStoreRows)
assert.equal(series.firstHour, 8)
assert.equal(series.endHour, 19)
assert.equal(sumBeforeHour(series.revenue, 15), 47_826)
assert.equal(sumBeforeHour(series.compareRevenue, 15), 49_793)
assert.equal(sumBeforeHour(series.revenue, 99), 51_409, '越界截止按整天处理')
assert.equal(sumBeforeHour(series.revenue, -3), 0)
assert.deepEqual(getCumulativeTotals(series, 10), { revenue: 5_820, compareRevenue: 7_229, orders: 20, compareOrders: 16 })

const duplicated = buildHourlySeries([hourly(9, 10, 5), hourly(9, 2, 1, 'S2'), { ...hourly(9, 99, 99), hour: '30:00' }])
assert.equal(duplicated.revenue[9], 12, '同一小时多家店必须累加')
assert.equal(duplicated.compareRevenue[9], 6)
assert.equal(duplicated.endHour, 10)
assert.equal(buildHourlySeries([]).firstHour, null)
assert.equal(buildHourlySeries([{ ...hourly(9, 10, 5), orderCount: undefined }]).orders[9], 0, '旧接口缺单数字段时按 0')

// 小基数
const lastYearFullDay = sumBeforeHour(series.compareRevenue, FULL_DAY_CUTOFF_HOUR)
assert.equal(isLowBase(405, lastYearFullDay), true)
assert.equal(isLowBase(7_229, lastYearFullDay), false)
assert.equal(isLowBase(0, 0), false, '去年全天为 0 时交给「新增」口径处理，不算小基数')

// UTC 时间解析与本地时钟
assert.equal(parseUtcTimestamp('2026-09-21T05:30:03Z'), Date.UTC(2026, 8, 21, 5, 30, 3))
assert.equal(parseUtcTimestamp('2026-09-21T05:30:03'), Date.UTC(2026, 8, 21, 5, 30, 3))
assert.equal(parseUtcTimestamp('2026-09-21T15:30:03+10:00'), Date.UTC(2026, 8, 21, 5, 30, 3))
assert.equal(parseUtcTimestamp('not a date'), null)
assert.equal(parseUtcTimestamp(null), null)
assert.match(formatLocalClockTime('2026-09-21T05:31:00Z') ?? '', /^\d{2}:\d{2}$/)
assert.equal(formatLocalClockTime('bad'), null)
assert.equal(sydneyTodayKey(new Date('2026-09-23T14:30:00Z')), '2026-09-24', '悉尼 0:30 已是次日')
assert.equal(sydneyTodayKey(new Date('2026-09-24T06:00:00Z')), '2026-09-24')

// 默认截止整点
assert.deepEqual(
  resolveDefaultCutoff({ selectedDate: '2026-09-21', todayKey: '2026-09-21', statisticsCompletedAtUtc: '2026-09-21T05:30:03Z' }),
  { cutoffHour: 15, live: true, liveHourFraction: 0.5 },
  '15:30 统计完成时 15 点之前的小时都已完整',
)
assert.deepEqual(
  resolveDefaultCutoff({ selectedDate: '2026-09-20', todayKey: '2026-09-21', statisticsCompletedAtUtc: null }),
  { cutoffHour: FULL_DAY_CUTOFF_HOUR, live: false, liveHourFraction: 0 },
  '历史日期比较整天，不依赖统计时间',
)
assert.deepEqual(
  resolveDefaultCutoff({ selectedDate: '2026-09-21', todayKey: '2026-09-21', statisticsCompletedAtUtc: '2026-09-20T10:00:00Z' }),
  { cutoffHour: 0, live: true, liveHourFraction: 0 },
  '今天尚未统计时没有完整小时',
)
assert.equal(
  resolveDefaultCutoff({ selectedDate: '2026-09-21', todayKey: '2026-09-21', statisticsCompletedAtUtc: undefined }),
  null,
  '旧后端没有统计时间字段时不猜整点',
)
assert.equal(
  resolveDefaultCutoff({ selectedDate: '2026-10-05', todayKey: '2026-10-05', statisticsCompletedAtUtc: '2026-10-05T04:30:00Z' })?.cutoffHour,
  14,
  '夏令时悉尼 15:30 = 布里斯班 14:30，截止取 14 点',
)

// 可点选的截止整点
assert.deepEqual(getCutoffOptions(series, 15), [9, 10, 11, 12, 13, 14, 15])
assert.deepEqual(getCutoffOptions(series, FULL_DAY_CUTOFF_HOUR), [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19])
assert.deepEqual(getCutoffOptions(series, 8), [], '开门前没有可比较的整点')
assert.equal(resolveEffectiveCutoff(null, 15), 15)
assert.equal(resolveEffectiveCutoff(12, 15), 12)
assert.equal(resolveEffectiveCutoff(18, 15), 15, '不能选到尚未完整的小时')
assert.equal(getDisplayCutoffHour(series, FULL_DAY_CUTOFF_HOUR), 19, '整天截止显示为最后营业整点')

// 时段表：累计口径与状态
const cumulativeRows = buildHourlyDetailRows(series, { cutoffHour: 15, live: true, cumulative: true })
assert.deepEqual(cumulativeRows.map(row => row.status), [
  'complete', 'complete', 'complete', 'complete', 'complete', 'complete', 'complete', 'live', 'upcoming', 'upcoming', 'upcoming',
])
const cutoffRow = cumulativeRows.find(row => row.isCutoffRow)
assert.equal(cutoffRow?.hour, 14)
assert.equal(cutoffRow?.boundaryHour, 15)
assert.equal(cutoffRow?.revenue, 47_826)
assert.equal(cutoffRow?.compareRevenue, 49_793)
assert.equal(cutoffRow?.orders, 70)
assert.equal(cumulativeRows[cumulativeRows.length - 1]!.compareRevenue, lastYearFullDay)
const pickedRows = buildHourlyDetailRows(series, { cutoffHour: 15, live: true, cumulative: true, highlightCutoffHour: 12 })
assert.equal(pickedRows.find(row => row.isCutoffRow)?.hour, 11, '高亮跟随用户点选的整点')
assert.equal(pickedRows.find(row => row.hour === 15)?.status, 'live', '状态仍按最近完整整点判断')
const perHourRows = buildHourlyDetailRows(series, { cutoffHour: 15, live: true, cumulative: false })
assert.equal(perHourRows[6]!.revenue, 6_836)
assert.equal(perHourRows.some(row => row.isCutoffRow), false, '逐小时口径不高亮截止行')
assert.ok(buildHourlyDetailRows(series, { cutoffHour: FULL_DAY_CUTOFF_HOUR, live: false, cumulative: true })
  .every(row => row.status === 'complete'), '历史日期全部为完整小时')

// 累计曲线模型
const chart = buildCumulativeChartModel(series, { cutoffHour: 15, live: true, liveHourFraction: 0.5 })
assert.ok(chart)
assert.equal(chart.startHour, 8)
assert.equal(chart.endHour, 19)
assert.equal(chart.comparePoints.length, 12)
assert.equal(chart.currentPoints.length, 8)
assert.deepEqual(chart.liveTail, { hour: 15.5, value: 51_409 })
assert.deepEqual(chart.gapRegions.map(region => region.tone), ['behind'])
assert.equal(chart.maxValue, 66_172)
const crossing = buildCumulativeChartModel(
  buildHourlySeries([hourly(9, 100, 300), hourly(10, 500, 100), hourly(11, 0, 50)]),
  { cutoffHour: 12, live: false, liveHourFraction: 0 },
)
assert.ok(crossing)
assert.deepEqual(crossing.gapRegions.map(region => region.tone), ['behind', 'ahead'], '交叉后必须分段着色')
const crossingPoint = crossing.gapRegions[0]!.points[crossing.gapRegions[0]!.points.length - 1]!
assert.equal(crossingPoint.current, crossingPoint.compare, '分段点落在两线交点上')
assert.equal(crossing.liveTail, null)

// 分店排行对齐：大小写不同的分店代码也要匹配；小时数据里没有的分店按 0
const branchRows = [
  hourly(9, 1_000, 1_500, '1013', 1, 1), hourly(10, 2_000, 1_000, '1013', 1, 1),
  hourly(15, 500, 900, '1013', 1, 1), hourly(16, 0, 800, '1013', 0, 1),
  hourly(9, 500, 300, 'HB17', 1, 1), hourly(10, 900, 400, 'HB17', 1, 1),
]
const byBranch = groupHourlySeriesByBranch(branchRows)
assert.equal(byBranch.size, 2)
assert.equal(byBranch.get('1013')?.endHour, 17)
function branch(branchCode: string, revenue: number, revenueLY: number, rank: number): RevenueBranch {
  return { rank, branchCode, branchName: branchCode, revenue, revenueLY, orderCount: 9, orderCountLY: 9, aov: 1, aovLY: 1 }
}
const aligned = alignBranchesToCutoff(
  [branch('1013', 3_500, 4_200, 1), branch('1001', 0, 1_822, 2), branch('hb17', 1_400, 700, 3)],
  byBranch,
  15,
)
assert.deepEqual(aligned.map(row => row.branchCode), ['1013', 'hb17', '1001'])
assert.deepEqual(aligned.map(row => row.rank), [1, 2, 3], '对齐后重排并重新编号')
assert.equal(aligned[0]!.revenue, 3_000, '进行中的 15 点不计入')
assert.equal(aligned[0]!.revenueLY, 2_500, '去年只取到 15 点，不再拿全天比')
assert.equal(aligned[0]!.aov, 1_500, '对齐后客单价用累计营业额除以累计单数')
assert.equal(aligned[1]!.revenue, 1_400, '分店代码大小写不同也能匹配小时序列')
assert.equal(aligned[2]!.revenue, 0)
assert.equal(aligned[2]!.revenueLY, 0, '小时数据里没有的分店两期都按 0')

// 选店筛选与周层级
assert.deepEqual(filterHourlyRowsByBranch(branchRows, 'hb17').map(row => row.hour), ['09:00', '10:00'])
assert.equal(filterHourlyRowsByBranch(branchRows, null).length, branchRows.length)
function node(key: string, level: RevenueWeeklyNode['level'], hierarchy: string, revenue: number, children: RevenueWeeklyNode[] = []): RevenueWeeklyNode {
  return { key, level, hierarchy, revenue, revenueLY: revenue, orders: 1, ordersLY: 1, aov: revenue, aovLY: revenue, yoyChange: 0, children }
}
const weekly = [node('w2026-39', 'week', '2026-W39', 9_999, [
  node('w2026-39-1013', 'branch', 'Orion', 7_000, [node('w2026-39-1013-20260924', 'date', '2026-09-24', 7_000)]),
  node('w2026-39-HB17', 'branch', 'Waratah', 2_999, [node('w2026-39-HB17-20260924', 'date', '2026-09-24', 2_999)]),
])]
const alignedWeekly = alignWeeklyNodesToCutoff(weekly, byBranch, 15)
assert.equal(alignedWeekly[0]!.revenue, 4_400, '周节点为对齐后分店之和')
assert.equal(alignedWeekly[0]!.revenueLY, 3_200)
assert.equal(alignedWeekly[0]!.children?.[0]?.revenue, 3_000)
assert.equal(alignedWeekly[0]!.children?.[0]?.children?.[0]?.revenue, 3_000, '日期节点按所属分店对齐')
assert.equal(alignedWeekly[0]!.yoyChange, null, '整天口径的 yoyChange 不能带进对齐结果')
const scoped = scopeWeeklyToBranch(weekly, 'hb17')
assert.equal(scoped.length, 1)
assert.equal(scoped[0]!.revenue, 2_999, '选店后周节点只算该店')
assert.deepEqual(scoped[0]!.children?.map(child => child.key), ['w2026-39-HB17'])
assert.deepEqual(scopeWeeklyToBranch(weekly, '9999'), [], '该店当周无销售时整周不显示')
assert.deepEqual(scopeWeeklyToBranch(weekly, null), weekly, '未选店保持原样')

// 多日区间含今天：今天换成截至整点的累计，其余日期仍按全天
const todayHourly = [
  hourly(9, 200, 300, '1013', 2, 3), hourly(10, 400, 350, '1013', 4, 3),
  hourly(15, 100, 250, '1013', 1, 2), hourly(16, 0, 300, '1013', 0, 3),
]
const lastDaySeries = groupHourlySeriesByBranch(todayHourly)
const rangeBranches = alignRangeBranchesToCutoff(
  [
    { rank: 1, branchCode: '1013', branchName: 'Orion', revenue: 1_700, revenueLY: 2_100, orderCount: 17, orderCountLY: 21, aov: 100, aovLY: 100 },
    { rank: 2, branchCode: 'HB17', branchName: 'Waratah', revenue: 1_650, revenueLY: 1_000, orderCount: 10, orderCountLY: 10, aov: 165, aovLY: 100 },
  ],
  [
    { rank: 1, branchCode: '1013', branchName: 'Orion', revenue: 700, revenueLY: 1_200, orderCount: 7, orderCountLY: 12, aov: 100, aovLY: 100 },
    { rank: 2, branchCode: 'hb17', branchName: 'Waratah', revenue: 50, revenueLY: 0, orderCount: 1, orderCountLY: 0, aov: 50, aovLY: 0 },
  ],
  lastDaySeries,
  15,
)
const orion = rangeBranches.find(row => row.branchCode === '1013')!
assert.equal(orion.revenue, 1_600, '区间合计 − 今天全天 + 今天截至 15 点')
assert.equal(orion.revenueLY, 1_550, '同期把对应日也换成截至 15 点')
assert.equal(orion.orderCount, 16)
assert.equal(orion.orderCountLY, 15)
assert.equal(orion.aov, 100)
const waratah = rangeBranches.find(row => row.branchCode === 'HB17')!
assert.equal(waratah.revenue, 1_600, '今天没有小时数据的分店只去掉今天全天')
assert.deepEqual(rangeBranches.map(row => row.rank), [1, 2])

const rangeHourly = alignRangeHourlyRows(
  [hourly(9, 500, 700, '1013', 5, 7), hourly(15, 300, 500, '1013', 3, 5), hourly(16, 150, 450, '1013', 2, 5)],
  todayHourly,
  15,
)
assert.deepEqual(rangeHourly.map(row => [row.hour, row.revenue, row.revenueLY]), [
  ['09:00', 500, 700],
  ['15:00', 200, 250],
  ['16:00', 150, 150],
], '截止及之后的小时两期都去掉最后一天')
assert.equal(rangeHourly[2]!.orderCountLY, 2)
assert.equal(alignRangeHourlyRows([hourly(15, 0.1 + 0.2, 0, '1013')], [hourly(15, 0.3, 0, '1013')], 15)[0]!.revenue, 0,
  '浮点尾差取到分且不为负')

const otherWeek = node('w2026-38', 'week', '2026-W38', 500, [node('w2026-38-1013', 'branch', 'Orion', 500, [node('w2026-38-1013-20260918', 'date', '2026-09-18', 500)])])
const rangeWeekly = alignRangeWeeklyToCutoff([
  {
    ...node('w2026-39', 'week', '2026-W39', 1_700), revenueLY: 2_100, children: [{
      ...node('w2026-39-1013', 'branch', 'Orion', 1_700), revenueLY: 2_100, children: [
        { ...node('w2026-39-1013-20260924', 'date', '2026-09-24', 700), revenueLY: 1_200 },
        { ...node('w2026-39-1013-20260923', 'date', '2026-09-23', 1_000), revenueLY: 900 },
      ],
    }],
  },
  otherWeek,
], '2026-09-24', lastDaySeries, 15)
assert.equal(rangeWeekly[0]!.revenue, 1_600, '周节点随今天重新汇总')
assert.equal(rangeWeekly[0]!.revenueLY, 1_550)
assert.equal(rangeWeekly[0]!.children?.[0]?.revenue, 1_600)
assert.equal(rangeWeekly[0]!.children?.[0]?.children?.[0]?.revenue, 600, '今天的日期节点换成截至 15 点')
assert.equal(rangeWeekly[0]!.children?.[0]?.children?.[1]?.revenue, 1_000, '其余日期不变')
assert.equal(rangeWeekly[1], otherWeek, '不含今天的周原样返回')

// 回退态的参考整点按固定 UTC+10
assert.equal(referenceClockHour(Date.UTC(2026, 8, 24, 6, 12)), 16)
assert.equal(referenceClockHour(Date.UTC(2026, 8, 24, 14, 30)), 0, 'UTC+10 已过零点')

console.log('营业额分时累计对齐：通过')
