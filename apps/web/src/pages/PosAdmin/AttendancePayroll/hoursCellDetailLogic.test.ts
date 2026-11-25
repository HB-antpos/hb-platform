import assert from 'node:assert/strict'

import type { AttendanceHoursDayDto, AttendanceHoursEmployeeDto, AttendanceHoursShiftDto } from '../../../types/attendancePayroll'
import {
  breakdownMismatch,
  buildBreakdown,
  buildCellDetail,
  clockToMinutes,
  dayTier,
  minutesLabel,
  punchSourceKind,
  shiftDeviation,
} from './hoursCellDetailLogic'

const NO_TIER = { weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0 }
const day = (patch: Partial<AttendanceHoursDayDto> = {}): AttendanceHoursDayDto => ({
  date: '2026-11-17',
  paidHours: null,
  byTier: { ...NO_TIER },
  hasSchedule: false,
  pending: [],
  approvedMarks: [],
  otherStores: [],
  ...patch,
})
const shift = (patch: Partial<AttendanceHoursShiftDto> = {}): AttendanceHoursShiftDto => ({
  scheduleStart: '09:00',
  scheduleEnd: '17:30',
  workedMinutes: 510,
  inScheduleMinutes: 510,
  mealDeductionMinutes: 30,
  mealAddBackMinutes: 0,
  overtimeMinutes: 0,
  paidMinutes: 480,
  segments: [{ start: '09:00', end: '17:30' }],
  ...patch,
})

// ---------- 时刻与来源 ----------
assert.equal(clockToMinutes('09:05'), 545)
assert.equal(clockToMinutes('24:00'), 1440, '系统午夜自动下班 = 24:00')
assert.equal(clockToMinutes('25:00'), null)
assert.equal(clockToMinutes('9:61'), null)
assert.equal(clockToMinutes(null), null)
assert.equal(punchSourceKind('PosQr'), 'pos')
assert.equal(punchSourceKind('ManualAdjustment'), 'adjusted')
assert.equal(punchSourceKind('ManualEntry'), 'manual')
assert.equal(punchSourceKind('SystemAutoClockOut'), 'autoOut')
assert.equal(punchSourceKind('Whatever'), 'other', '没见过的来源归「其他」，不丢')
assert.equal(punchSourceKind(undefined), null, '旧接口没有来源字段')

// ---------- 档位 ----------
assert.equal(dayTier(day({ byTier: { ...NO_TIER, saturday: 7.5 } })), 'saturday')
assert.equal(dayTier(day()), null, '没有计薪工时没有档位')

// ---------- 与排班的偏差 ----------
assert.deepEqual(
  shiftDeviation(shift({ segments: [{ start: '09:02', end: '17:31' }] })),
  { lateIn: 2, earlyIn: 0, earlyOut: 0, lateOut: 1 },
  '晚到 2 分、晚走 1 分',
)
assert.deepEqual(
  shiftDeviation(shift({ segments: [{ start: '08:55', end: '17:20' }] })),
  { lateIn: 0, earlyIn: 5, earlyOut: 10, lateOut: 0 },
  '早到 5 分、早走 10 分',
)
assert.deepEqual(
  shiftDeviation(shift({ segments: [{ start: '09:00', end: '13:00' }, { start: '16:00', end: '17:30' }] })),
  { lateIn: 0, earlyIn: 0, earlyOut: 0, lateOut: 0 },
  '多个班段：第一次上班对开始、最后一次下班对结束',
)
assert.deepEqual(
  shiftDeviation(shift({ scheduleStart: '22:00', scheduleEnd: '06:00', segments: [{ start: '21:55', end: '06:05' }] })),
  { lateIn: 0, earlyIn: 5, earlyOut: 0, lateOut: 5 },
  '跨午夜的夜班：06:05 下班按次日算，晚走 5 分',
)
assert.equal(shiftDeviation(shift({ segments: [] })), null, '没有打卡没有偏差')
assert.equal(shiftDeviation(shift({ segments: [{ start: '09:00', end: null }] })), null, '没闭合的班段不算偏差')
assert.equal(shiftDeviation(shift({ segments: [{ start: '09:00', end: '24:00', isAutoClockOut: true }] })), null, '系统自动下班的 24:00 不是真实下班时间')

// ---------- 单元格气泡模型 ----------
const normal = buildCellDetail(day({
  paidHours: 8,
  byTier: { ...NO_TIER, weekday: 8 },
  shifts: [shift({ segments: [{ start: '09:02', end: '17:31', inSource: 'PosQr', outSource: 'ManualAdjustment', deviceCode: 'POS_1003_1308' }] })],
}))
assert.equal(normal.hasContent, true)
assert.equal(normal.tier, 'weekday')
assert.equal(normal.shifts.length, 1)
assert.equal(normal.shifts[0].scheduleText, '09:00–17:30')
assert.equal(normal.shifts[0].missing, false)
assert.equal(normal.shifts[0].segments[0].minutes, 509)
assert.equal(normal.shifts[0].segments[0].inSource, 'pos')
assert.equal(normal.shifts[0].segments[0].outSource, 'adjusted')
assert.equal(normal.shifts[0].segments[0].deviceCode, 'POS_1003_1308')
assert.equal(normal.shifts[0].mealDeductionMinutes, 30)

const missing = buildCellDetail(day({ hasSchedule: true, shifts: [shift({ segments: [], workedMinutes: 0, paidMinutes: 0 })] }))
assert.equal(missing.hasContent, true)
assert.equal(missing.shifts[0].missing, true, '有排班没有打卡段 = 未打卡')

assert.equal(buildCellDetail(day()).hasContent, false, '休息格没有任何内容不弹气泡')
assert.equal(buildCellDetail(day({ leave: { type: 'PaidAnnual', hours: 8 } })).hasContent, true, '请假格要弹')
assert.equal(buildCellDetail(day({ otherStores: [{ storeCode: 'SB', hours: 4 }] })).hasContent, true, '只有其他分店工时也弹')

// 旧版接口没有 shifts、但有打卡段：合成一个没有排班窗口的班，至少能列出上下班时间
const legacy = buildCellDetail(day({ paidHours: 5.5, segments: [{ start: '09:00', end: '15:00' }] }))
assert.equal(legacy.hasContent, true)
assert.equal(legacy.shifts[0].scheduleText, '')
assert.equal(legacy.shifts[0].segments[0].end, '15:00')

assert.equal(minutesLabel(30, { hour: '小时', minute: '分钟' }), '30 分钟')
assert.equal(minutesLabel(65, { hour: '小时', minute: '分钟' }), '1 小时 5 分钟')
assert.equal(minutesLabel(120, { hour: '小时', minute: '分钟' }), '2 小时')
assert.equal(minutesLabel(0, { hour: '小时', minute: '分钟' }), '')

// ---------- 工资表逐日明细 ----------
const employee = {
  userGuid: 'u1',
  userName: 'Alesi',
  days: [
    day({ date: '2026-11-16', paidHours: 8, byTier: { ...NO_TIER, weekday: 8 }, segments: [{ start: '09:00', end: '17:30' }] }),
    day({ date: '2026-11-17', paidHours: 8, byTier: { ...NO_TIER, weekday: 8 }, segments: [{ start: '09:00', end: '13:00' }, { start: '14:00', end: '18:00' }] }),
    day({ date: '2026-11-19' }),
    day({ date: '2026-11-21', paidHours: 7.5, byTier: { ...NO_TIER, saturday: 7.5 }, segments: [{ start: '09:00', end: '16:30' }] }),
    day({ date: '2026-11-20', leave: { type: 'PaidSick', hours: 7.5 } }),
    day({ date: '2026-11-22', publicHolidayPay: { state: 'Confirmed', hours: 7.6, count: 3 } }),
    day({ date: '2026-11-18', publicHolidayPay: { state: 'Pending', hours: null, count: 2 } }),
  ],
} as unknown as AttendanceHoursEmployeeDto

const weekday = buildBreakdown(employee, 'weekday')
assert.deepEqual(weekday.items.map((item) => [item.date, item.hours]), [['2026-11-16', 8], ['2026-11-17', 8]])
assert.deepEqual(weekday.items[1].ranges, ['09:00–13:00', '14:00–18:00'], '多班段列出全部时间段')
assert.equal(weekday.total, 16)
assert.equal(buildBreakdown(employee, 'saturday').total, 7.5)
assert.equal(buildBreakdown(employee, 'sunday').items.length, 0)

const leave = buildBreakdown(employee, 'leave')
assert.deepEqual(leave.items.map((item) => [item.date, item.leaveType, item.hours]), [
  ['2026-11-20', 'PaidSick', 7.5],
  ['2026-11-22', 'PublicHolidayPay', 7.6],
], '带薪假：请假申请工时 + 已确认的带薪公假；待确认的公假不算')
assert.equal(leave.total, 15.1)

const total = buildBreakdown(employee, 'total')
assert.deepEqual(total.items.map((item) => item.date), ['2026-11-16', '2026-11-17', '2026-11-20', '2026-11-21', '2026-11-22'], '合计按日期排序')
assert.equal(total.total, 38.6)

assert.equal(breakdownMismatch(weekday, 16), false)
assert.equal(breakdownMismatch(leave, 16), true, '年假核定与申请不同等情况：对不上就提示以表中数字为准')

console.log('hoursCellDetailLogic tests passed')
