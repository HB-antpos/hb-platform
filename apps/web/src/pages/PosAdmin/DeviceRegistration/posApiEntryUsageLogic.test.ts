import type { PosApiEntryUsageBucket, PosApiEntryUsageEntry } from '../../../types/posApiEntry'

import {
  addDays,
  buildStackedChart,
  buildUsagePresets,
  countDaysInclusive,
  formatBucketAxisLabel,
  formatBucketTitle,
  formatCount,
  formatErrorRate,
  formatShare,
  getDefaultUsageRange,
  isValidIsoDate,
  niceStep,
  startOfMonth,
  startOfWeek,
  validateUsageRange,
} from './posApiEntryUsageLogic'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const left = JSON.stringify(actual)
  const right = JSON.stringify(expected)
  if (left !== right) {
    throw new Error(`${message}: expected ${right}, got ${left}`)
  }
}

function bucket(bucketStart: string, counts: Partial<Record<PosApiEntryUsageEntry, number>>, partial = false): PosApiEntryUsageBucket {
  const entries = (['edge', 'aws', 'direct', 'unknown'] as const).map((entry) => ({
    entry,
    requests: counts[entry] ?? 0,
    status4xx: 0,
    status5xx: 0,
    share: 0,
  }))
  const total = entries.reduce((sum, item) => sum + item.requests, 0)
  return {
    bucketStart,
    bucketEnd: bucketStart,
    partial,
    total: { requests: total, status4xx: 0, status5xx: 0 },
    entries,
  }
}

// ───────── 日期运算：纯日历日，不受浏览器时区 / 夏令时影响 ─────────

assertEqual(isValidIsoDate('2026-10-24'), true, '合法日期')
assertEqual(isValidIsoDate('2026-02-30'), false, '2 月 30 日不合法（不能被滚动成 3 月 2 日）')
assertEqual(isValidIsoDate('2026-10-1'), false, '必须零填充')
assertEqual(isValidIsoDate('2026/10/24'), false, '分隔符必须是 -')
assertEqual(isValidIsoDate(''), false, '空串不合法')
assertEqual(isValidIsoDate('2028-02-29'), true, '闰年 2 月 29 日合法')
assertEqual(isValidIsoDate('2027-02-29'), false, '平年 2 月 29 日不合法')

assertEqual(addDays('2026-10-31', 1), '2026-11-01', '跨月')
assertEqual(addDays('2026-01-01', -1), '2025-12-31', '跨年')
assertEqual(addDays('2026-10-04', 1), '2026-10-05', '悉尼夏令时切换日（10-04）也恰好加一天')
assertEqual(addDays('bad', 3), 'bad', '非法日期原样返回，不抛异常')

assertEqual(countDaysInclusive('2026-10-01', '2026-10-01'), 1, '同一天算 1 天')
assertEqual(countDaysInclusive('2026-10-01', '2026-10-31'), 31, '含首尾')
assertEqual(countDaysInclusive('2026-10-02', '2026-10-01'), 0, '倒序不是合法跨度')
assertEqual(countDaysInclusive('x', '2026-10-01'), 0, '非法日期为 0')

// 周一起算：2026-10-24 是周六，所在周周一是 10-19；周日 10-25 仍属同一周；10-26 周一进入下一周
assertEqual(startOfWeek('2026-10-24'), '2026-10-19', '周六 → 当周周一')
assertEqual(startOfWeek('2026-10-25'), '2026-10-19', '周日属于上一个周一开始的周')
assertEqual(startOfWeek('2026-10-26'), '2026-10-26', '周一就是自己')
assertEqual(startOfWeek('2026-01-01'), '2025-12-29', '跨年的周')
assertEqual(startOfMonth('2026-10-24'), '2026-10-01', '月初')

// ───────── 默认范围 ─────────

assertDeepEqual(
  getDefaultUsageRange('day', '2026-10-24'),
  { startDate: '2026-09-25', endDate: '2026-10-24' },
  '按日默认最近 30 天（含今天）',
)
assertDeepEqual(
  getDefaultUsageRange('week', '2026-10-24'),
  { startDate: '2026-08-03', endDate: '2026-10-24' },
  '按周默认 12 个周，起点对齐周一',
)
assertEqual(new Date(`${getDefaultUsageRange('week', '2026-10-24').startDate}T00:00:00Z`).getUTCDay(), 1, '周起点是周一')
assertDeepEqual(
  getDefaultUsageRange('month', '2026-10-24'),
  { startDate: '2025-11-01', endDate: '2026-10-24' },
  '按月默认 12 个月，起点对齐月初',
)
for (const granularity of ['day', 'week', 'month'] as const) {
  const range = getDefaultUsageRange(granularity, '2026-10-24')
  assertEqual(validateUsageRange(range.startDate, range.endDate), null, `${granularity} 默认范围必须通过校验`)
}

// ───────── 校验 ─────────

assertEqual(validateUsageRange('2026-10-01', '2026-10-31'), null, '正常范围')
assertEqual(validateUsageRange('2026-10-31', '2026-10-01'), 'reversed', '开始晚于结束')
assertEqual(validateUsageRange('2026-13-01', '2026-10-01'), 'invalid', '非法日期')
assertEqual(validateUsageRange('2025-09-20', '2026-10-24'), null, '正好 400 天允许')
assertEqual(validateUsageRange('2025-09-19', '2026-10-24'), 'tooLarge', '401 天拒绝')

// ───────── 预设 ─────────

const presets = buildUsagePresets('2026-10-24')
assertDeepEqual(presets.map((p) => p.key), ['last7', 'last30', 'last90', 'thisMonth', 'lastMonth', 'last12Months'], '预设顺序')
const byKey = Object.fromEntries(presets.map((p) => [p.key, p]))
assertDeepEqual([byKey.last7.startDate, byKey.last7.endDate], ['2026-10-18', '2026-10-24'], '近 7 天含今天')
assertDeepEqual([byKey.thisMonth.startDate, byKey.thisMonth.endDate], ['2026-10-01', '2026-10-24'], '本月到今天')
assertDeepEqual([byKey.lastMonth.startDate, byKey.lastMonth.endDate], ['2026-09-01', '2026-09-30'], '上月整月')
assertDeepEqual([byKey.lastMonth.startDate, byKey.lastMonth.endDate], ['2026-09-01', '2026-09-30'], '上月整月')
assertDeepEqual(
  buildUsagePresets('2026-01-15').find((p) => p.key === 'lastMonth'),
  { key: 'lastMonth', startDate: '2025-12-01', endDate: '2025-12-31' },
  '1 月的「上月」跨年',
)
for (const preset of presets) {
  assertEqual(validateUsageRange(preset.startDate, preset.endDate), null, `预设 ${preset.key} 必须通过校验`)
}

// ───────── 格式化 ─────────

assertEqual(formatCount(1234567), '1,234,567', '千分位')
assertEqual(formatCount(Number.NaN), '0', 'NaN 按 0')
assertEqual(formatShare(0), '0%', '0')
assertEqual(formatShare(0.0004), '<0.1%', '极小但非 0 不显示成 0%')
assertEqual(formatShare(0.5), '50%', '整数百分比去掉 .0')
assertEqual(formatShare(0.6234), '62.3%', '一位小数')
assertEqual(formatShare(1), '100%', '100%')
assertEqual(formatShare(0.99999), '100%', '四舍五入到 100% 不显示 100.0%')
assertEqual(formatErrorRate({ requests: 0, status4xx: 0, status5xx: 0 }), '—', '没有请求时不算比率')
assertEqual(formatErrorRate({ requests: 200, status4xx: 10, status5xx: 1 }), '0.5%', '5xx 占比')

assertEqual(formatBucketAxisLabel('2026-10-24', 'day'), '10-24', '日轴标签')
assertEqual(formatBucketAxisLabel('2026-10-19', 'week'), '10-19', '周轴标签用周一')
assertEqual(formatBucketAxisLabel('2026-10-01', 'month'), '2026-10', '月轴标签')
const weekBucket = { ...bucket('2026-10-19', {}), bucketEnd: '2026-10-25' }
assertEqual(formatBucketTitle(weekBucket, 'week'), '2026-10-19 ~ 2026-10-25', '周标题含起止')
assertEqual(formatBucketTitle(weekBucket, 'day'), '2026-10-19', '日标题')
assertEqual(formatBucketTitle({ ...weekBucket, bucketStart: '2026-10-01' }, 'month'), '2026-10', '月标题')

// ───────── 纵轴刻度 ─────────

assertEqual(niceStep(0), 1, '0 按 1')
assertEqual(niceStep(0.3), 1, '小于 1 按 1')
assertEqual(niceStep(1), 1, '1')
assertEqual(niceStep(1.2), 2, '1.2 → 2')
assertEqual(niceStep(3), 5, '3 → 5')
assertEqual(niceStep(6), 10, '6 → 10')
assertEqual(niceStep(230), 500, '230 → 500')
assertEqual(niceStep(12_000), 20_000, '12000 → 20000')

// ───────── 堆叠柱图几何 ─────────

const layout = { width: 400, height: 200, padLeft: 40, padRight: 0, padTop: 10, padBottom: 20 }
// 绘图区：左 40、上 10、宽 360、高 170
const empty = buildStackedChart([], layout)
assertEqual(empty.bars.length, 0, '没有数据没有柱子')
assertEqual(empty.max, 4, '没有数据时纵轴上限 4（刻度间隔 1）')
assertDeepEqual(empty.ticks.map((tick) => tick.value), [0, 1, 2, 3, 4], '刻度都是整数')

const chart = buildStackedChart(
  [
    bucket('2026-10-19', { edge: 60, aws: 30, direct: 10 }),
    bucket('2026-10-20', { edge: 0, aws: 0, direct: 0, unknown: 5 }),
    bucket('2026-10-21', {}),
  ],
  layout,
)
// 最大值 100 → 刻度间隔 niceStep(25)=50 → 上限 200
assertEqual(chart.max, 200, '纵轴上限 = 刻度间隔 × 4')
assertDeepEqual(chart.ticks.map((tick) => tick.value), [0, 50, 100, 150, 200], '刻度')
assertEqual(chart.ticks[0].y, 180, '0 刻度在绘图区底部')
assertEqual(chart.ticks[4].y, 10, '上限刻度在绘图区顶部')

const [first, second, third] = chart.bars
assertDeepEqual(first.segments.map((s) => s.entry), ['edge', 'aws', 'direct'], '自下而上按 edge、aws、direct 叠')
assertEqual(first.segments[0].height, 51, 'edge 60/200 × 170')
assertEqual(first.segments[0].y, 180 - 51, 'edge 在最底部：顶边 = 底 - 自身高度')
assertEqual(first.segments[1].y, 180 - (90 / 200) * 170, 'aws 叠在 edge 之上')
assertEqual(first.segments[2].y, 180 - (100 / 200) * 170, 'direct 叠到总高')
assertEqual(first.total, 100, '总数')
assertDeepEqual(second.segments.map((s) => s.entry), ['unknown'], '0 的线路不产生矩形，unknown 在最上层')
assertEqual(third.segments.length, 0, '空桶没有矩形')
assertEqual(third.total, 0, '空桶总数 0')
// 三根柱均分 360 宽
assertEqual(first.x > 40 && first.x + first.width <= 40 + 120, true, '第一根柱在第一格内')
assertEqual(second.x > first.x + first.width, true, '柱子从左到右排列且不重叠')

// 标签稀疏：桶多时隔几根标一个，且从第一根开始
const many = Array.from({ length: 30 }, (_value, index) => bucket(addDays('2026-09-25', index), { edge: index }))
const labeled = buildStackedChart(many, layout).bars.filter((bar) => bar.showLabel)
assertEqual(labeled[0].bucketStart, '2026-09-25', '第一根一定标')
assertEqual(labeled.length <= 15, true, '标签数量受控')
assertEqual(buildStackedChart(many.slice(0, 5), layout).bars.every((bar) => bar.showLabel), true, '桶少时每根都标')

console.log('posApiEntryUsageLogic tests passed')
