import assert from 'node:assert/strict'
import { groupHourlySeriesByBranch, resolveDefaultCutoff, sumBeforeHour } from './hourlyCumulative'
import {
  FALLBACK_UTC_OFFSET_MINUTES,
  buildStoreTimeZoneMap,
  formatClockTimeAtOffset,
  getBranchOffsetMinutes,
  getBranchShiftHours,
  getCutoffClockAnchorUtc,
  getTimeZoneOffsetMinutes,
  resolveCutoffClock,
  shiftHourlyRowsToClock,
} from './storeCutoffClock'
import type { RevenueHourly } from './types'

function row(branchCode: string, hour: number, revenue: number, revenueLY = 0): RevenueHourly {
  return {
    hour: `${String(hour).padStart(2, '0')}:00`,
    revenue,
    revenueLY,
    percentage: 0,
    isPeak: false,
    branchCode,
    branchName: branchCode,
    orderCount: 0,
    orderCountLY: 0,
  }
}

// —— 时区偏移：悉尼 2026-10-04 进入夏令时，布里斯班全年 UTC+10 ——
const beforeDst = getCutoffClockAnchorUtc('2026-09-29')
const afterDst = getCutoffClockAnchorUtc('2026-10-06')
assert.equal(getTimeZoneOffsetMinutes('Australia/Sydney', beforeDst), 600)
assert.equal(getTimeZoneOffsetMinutes('Australia/Sydney', afterDst), 660, '夏令时期间悉尼是 UTC+11')
assert.equal(getTimeZoneOffsetMinutes('Australia/Brisbane', afterDst), 600, '布里斯班没有夏令时')
assert.equal(getTimeZoneOffsetMinutes('Australia/Adelaide', afterDst), 630)
assert.equal(getTimeZoneOffsetMinutes('Not/AZone', afterDst), null, '无效时区不能抛错')
assert.equal(getTimeZoneOffsetMinutes('Australia/Sydney', Number.NaN), null)

// —— 门店时区表：后端字典整理成按大写分店代码查找的表 ——
const timeZones = buildStoreTimeZoneMap({
  ' 1013 ': 'Australia/Brisbane',
  '1012': 'Australia/Sydney',
  '1028': '   ',
  '1014': null,
})
// 整数样式的键在对象里会被排到前面，所以按键查找而不是比较遍历顺序。
assert.equal(timeZones.size, 2, '空白与 null 时区的门店不进表')
assert.equal(timeZones.get('1013'), 'Australia/Brisbane', '分店代码去空格')
assert.equal(timeZones.get('1012'), 'Australia/Sydney')
assert.equal(buildStoreTimeZoneMap(undefined).size, 0, '旧后端没有该字段')

// —— 夏令时期间的全部分店：参考悉尼，布里斯班店与无时区门店平移一小时 ——
const allStores = ['1012', '1013', '1014']
const dstClock = resolveCutoffClock({
  branchCodes: allStores,
  referenceBranchCodes: allStores,
  timeZoneByBranch: timeZones,
  atUtc: afterDst,
})
assert.equal(dstClock.referenceOffsetMinutes, 660)
assert.equal(getBranchShiftHours(dstClock, '1012'), 0)
assert.equal(getBranchShiftHours(dstClock, '1013'), 1)
assert.equal(getBranchShiftHours(dstClock, '1014'), 1, '时区未知按 UTC+10 回退，只会少算不会多算')
assert.equal(getBranchShiftHours(dstClock, ' 1013 '), 1, '分店代码大小写与空格不影响查找')
assert.equal(getBranchOffsetMinutes(dstClock, 'UNKNOWN'), FALLBACK_UTC_OFFSET_MINUTES)
assert.equal(getBranchOffsetMinutes(dstClock, undefined), FALLBACK_UTC_OFFSET_MINUTES)

// 悉尼 10:30（UTC 前一天 23:30）完成统计：悉尼店 9–10 点已完整，布里斯班店本地才 09:30。
const completedAt = '2026-10-05T23:30:42Z'
assert.deepEqual(
  resolveDefaultCutoff({
    selectedDate: '2026-10-06',
    todayKey: '2026-10-06',
    statisticsCompletedAtUtc: completedAt,
    utcOffsetMinutes: dstClock.referenceOffsetMinutes,
  }),
  { cutoffHour: 10, live: true, liveHourFraction: 0.5 },
  '夏令时按悉尼时间截止到 10:00',
)
assert.equal(
  resolveDefaultCutoff({ selectedDate: '2026-10-06', todayKey: '2026-10-06', statisticsCompletedAtUtc: completedAt })?.cutoffHour,
  9,
  '固定 UTC+10 会让悉尼店少算一小时（Web 与移动端截止整点不一致的原因）',
)
assert.equal(formatClockTimeAtOffset(completedAt, dstClock.referenceOffsetMinutes), '10:30')
assert.equal(formatClockTimeAtOffset(completedAt, 600), '09:30')
assert.equal(formatClockTimeAtOffset(null, 600), null)

const rows = [
  row('1012', 9, 100, 90),
  row('1012', 10, 40, 80),
  row('1013', 8, 50, 45),
  row('1013', 9, 30, 70),
]
const shifted = shiftHourlyRowsToClock(rows, dstClock)
const series = groupHourlySeriesByBranch(shifted)
assert.equal(sumBeforeHour(series.get('1012')!.revenue, 10), 100, '悉尼店截至本地 10:00')
assert.equal(sumBeforeHour(series.get('1012')!.compareRevenue, 10), 90)
assert.equal(sumBeforeHour(series.get('1013')!.revenue, 10), 50, '布里斯班店截至本地 09:00，进行中的 9 点不算')
assert.equal(sumBeforeHour(series.get('1013')!.compareRevenue, 10), 45, '去年同期按同一本地时刻对齐')
assert.equal(rows[2]!.hour, '08:00', '平移不能改写原始行')
assert.equal(shifted[2]!.hour, '09:00', '平移后的小时仍是 HH:00 字符串')

// —— 选中布里斯班单店：参考就是布里斯班本地时间，悉尼店反向平移 ——
const brisbaneClock = resolveCutoffClock({
  branchCodes: allStores,
  referenceBranchCodes: ['1013'],
  timeZoneByBranch: timeZones,
  atUtc: afterDst,
})
assert.equal(brisbaneClock.referenceOffsetMinutes, 600)
assert.equal(getBranchShiftHours(brisbaneClock, '1013'), 0)
assert.equal(getBranchShiftHours(brisbaneClock, '1012'), -1)
assert.equal(
  resolveDefaultCutoff({
    selectedDate: '2026-10-06',
    todayKey: '2026-10-06',
    statisticsCompletedAtUtc: completedAt,
    utcOffsetMinutes: brisbaneClock.referenceOffsetMinutes,
  })?.cutoffHour,
  9,
  '布里斯班单店按本地 09:30 截止到 09:00',
)
const brisbaneSeries = groupHourlySeriesByBranch(shiftHourlyRowsToClock(rows, brisbaneClock))
assert.equal(sumBeforeHour(brisbaneSeries.get('1012')!.revenue, 9), 100, '悉尼店在布里斯班时钟 09:00 即本地 10:00')

// —— 夏令时之外全部门店都是 UTC+10：结果与原来的固定偏移一致，且不复制数组 ——
const standardClock = resolveCutoffClock({
  branchCodes: allStores,
  referenceBranchCodes: allStores,
  timeZoneByBranch: timeZones,
  atUtc: beforeDst,
})
assert.equal(standardClock.referenceOffsetMinutes, 600)
assert.equal(shiftHourlyRowsToClock(rows, standardClock), rows, '无需平移时保持同一引用')

// 旧后端不返回时区：退回固定 UTC+10，行为与改动前相同。
const noTimeZoneClock = resolveCutoffClock({
  branchCodes: allStores,
  referenceBranchCodes: allStores,
  timeZoneByBranch: new Map(),
  atUtc: afterDst,
})
assert.equal(noTimeZoneClock.referenceOffsetMinutes, FALLBACK_UTC_OFFSET_MINUTES)
assert.equal(shiftHourlyRowsToClock(rows, noTimeZoneClock), rows)
assert.equal(
  resolveCutoffClock({ branchCodes: [], referenceBranchCodes: [], timeZoneByBranch: timeZones, atUtc: afterDst })
    .referenceOffsetMinutes,
  FALLBACK_UTC_OFFSET_MINUTES,
)

// 半小时时区向上取整平移：只会少算。
const adelaideClock = resolveCutoffClock({
  branchCodes: ['1012', '9001'],
  referenceBranchCodes: ['1012', '9001'],
  timeZoneByBranch: new Map([['1012', 'Australia/Sydney'], ['9001', 'Australia/Adelaide']]),
  atUtc: afterDst,
})
assert.equal(getBranchShiftHours(adelaideClock, '9001'), 1)

// 超出 0–23 的小时并入首尾，整天合计不变；无法解析的小时原样保留。
const edgeRows = shiftHourlyRowsToClock([row('1013', 23, 5), row('1012', 0, 7)], dstClock)
assert.deepEqual(edgeRows.map(item => item.hour), ['23:00', '00:00'])
const edgeBrisbane = shiftHourlyRowsToClock([row('1012', 0, 7)], brisbaneClock)
assert.deepEqual(edgeBrisbane.map(item => item.hour), ['00:00'])
const badHour = { ...row('1013', 9, 1), hour: 'oops' }
assert.equal(shiftHourlyRowsToClock([badHour], dstClock)[0], badHour)

console.log('营业额门店时区截止时钟：通过')
