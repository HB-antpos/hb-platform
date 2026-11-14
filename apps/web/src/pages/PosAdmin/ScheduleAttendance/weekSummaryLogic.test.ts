import assert from 'node:assert/strict'

import { formatHours, getScheduleHours, summarizeSchedules } from './weekSummaryLogic'
import type { SummarySchedule } from './weekSummaryLogic'

const shift = (startTime: string, endTime: string, extra: Partial<SummarySchedule> = {}): SummarySchedule => ({
  status: 'Active',
  startTime,
  endTime,
  ...extra,
})

// 单个班次：09:00-17:30 共 8.5 小时，自动用餐 1 次（扣 0.5）→ 计薪 8 小时
assert.deepEqual(getScheduleHours(shift('09:00', '17:30')), { hours: 8.5, paidHours: 8 })
// 接口可能返回带秒的时间
assert.deepEqual(getScheduleHours(shift('09:00:00', '13:00:00')), { hours: 4, paidHours: 4 })
// 手动指定 0 次用餐＝不扣
assert.deepEqual(getScheduleHours(shift('09:00', '17:30', { mealBreakCount: 0 })), { hours: 8.5, paidHours: 8.5 })
// 结束不晚于开始：时长按 0 计，不抛错
assert.deepEqual(getScheduleHours(shift('22:00', '02:00')), { hours: 0, paidHours: 0 })

// 合计：取消的班次不计入班次数和时长
assert.deepEqual(
  summarizeSchedules([shift('09:00', '13:00'), shift('14:00', '17:30'), shift('09:00', '17:00', { status: 'Cancelled' })]),
  { shifts: 2, hours: 7.5, paidHours: 7.5 },
)
assert.deepEqual(summarizeSchedules([]), { shifts: 0, hours: 0, paidHours: 0 })
// 跨天班次计入班次数但不加时长
assert.deepEqual(summarizeSchedules([shift('22:00', '02:00')]), { shifts: 1, hours: 0, paidHours: 0 })

// 工时文案
assert.equal(formatHours(8.5), '8.5h')
assert.equal(formatHours(8), '8h')
assert.equal(formatHours(0), '0h')
assert.equal(formatHours(0.25), '0.25h')
assert.equal(formatHours(42.499999), '42.5h')

console.log('weekSummaryLogic tests passed')
