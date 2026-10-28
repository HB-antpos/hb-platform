import assert from 'node:assert/strict'

import type { AttendanceAvailabilityDto } from '../../../types/scheduleAttendance'
import {
  buildAvailabilityIndex,
  getDayAvailability,
  getScheduleAvailabilityConflict,
  mergeSegments,
  timeToMinutes,
} from './availabilityLogic'

const item = (
  userGuid: string,
  availableDate: string,
  startTime: string,
  endTime: string,
  extra: Partial<AttendanceAvailabilityDto> = {},
): AttendanceAvailabilityDto => ({
  availabilityGuid: `${userGuid}-${availableDate}-${startTime}-${endTime}`,
  storeCode: '1001',
  userGuid,
  weekStartDate: '2026-10-26',
  availableDate,
  startTime,
  endTime,
  status: 'Active',
  ...extra,
})

// 时间换算：HH:mm / HH:mm:ss 都支持，23:59:59 仍然 ≥ 23:59
assert.equal(timeToMinutes('09:30'), 570)
assert.equal(timeToMinutes('09:30:00'), 570)
assert.ok((timeToMinutes('23:59:59') ?? 0) >= 23 * 60 + 59)
assert.equal(timeToMinutes(''), undefined)
assert.equal(timeToMinutes(undefined), undefined)

// 归并：按员工 + 日期分组，日期取前 10 位，isUnavailable 区分两类，已取消的忽略
const index = buildAvailabilityIndex([
  item('u1', '2026-10-27T00:00:00', '09:00:00', '17:00:00'),
  item('u1', '2026-10-27', '18:00', '22:00', { isUnavailable: true }),
  item('u1', '2026-10-28', '00:00:00', '23:59:59', { isUnavailable: true, remark: '带薪年假' }),
  item('u1', '2026-10-29', '09:00', '12:00', { status: 'Cancelled' }),
  item('u2', '2026-10-27', '10:00', '14:00'),
  item('u1', '2026-10-30', '17:00', '09:00'), // 结束 ≤ 开始，脏数据直接丢弃
])
const u1Tue = getDayAvailability(index, 'u1', '2026-10-27')
assert.deepEqual(u1Tue?.available.map((s) => [s.startLabel, s.endLabel]), [['09:00', '17:00']])
assert.deepEqual(u1Tue?.unavailable.map((s) => [s.startLabel, s.endLabel]), [['18:00', '22:00']])
const u1Wed = getDayAvailability(index, 'u1', '2026-10-28')
assert.equal(u1Wed?.unavailable[0].allDay, true)
assert.equal(u1Wed?.unavailable[0].remark, '带薪年假')
assert.equal(getDayAvailability(index, 'u1', '2026-10-29'), undefined)
assert.equal(getDayAvailability(index, 'u1', '2026-10-30'), undefined)
assert.equal(getDayAvailability(index, 'u3', '2026-10-27'), undefined)
assert.equal(getDayAvailability(index, 'u2', '2026-10-27')?.available.length, 1)

// 合并：重叠或首尾相接合成一段，含有全天则标记 allDay
assert.deepEqual(
  mergeSegments([
    { startMin: 600, endMin: 720, startLabel: '10:00', endLabel: '12:00', allDay: false },
    { startMin: 540, endMin: 630, startLabel: '09:00', endLabel: '10:30', allDay: false },
    { startMin: 720, endMin: 780, startLabel: '12:00', endLabel: '13:00', allDay: false },
    { startMin: 900, endMin: 960, startLabel: '15:00', endLabel: '16:00', allDay: false },
  ]).map((s) => [s.startLabel, s.endLabel]),
  [['09:00', '13:00'], ['15:00', '16:00']],
)
const dedup = buildAvailabilityIndex([
  item('u1', '2026-10-27', '09:00', '12:00'),
  item('u1', '2026-10-27', '11:00', '15:00'),
])
assert.deepEqual(getDayAvailability(dedup, 'u1', '2026-10-27')?.available.map((s) => [s.startLabel, s.endLabel]), [['09:00', '15:00']])

// 冲突：与不能上班重叠 → unavailable；超出可上班 → outsideAvailable；完全覆盖 → 无冲突
const day = u1Tue
assert.equal(getScheduleAvailabilityConflict({ startTime: '09:00:00', endTime: '17:00:00', status: 'Draft' }, day), undefined)
assert.equal(getScheduleAvailabilityConflict({ startTime: '10:00', endTime: '18:30', status: 'Active' }, day), 'unavailable')
assert.equal(getScheduleAvailabilityConflict({ startTime: '08:00', endTime: '12:00', status: 'Active' }, day), 'outsideAvailable')
// 恰好首尾相接（班次 17:00-18:00 与不能上班 18:00-22:00）不算重叠，但已超出可上班 09-17
assert.equal(getScheduleAvailabilityConflict({ startTime: '17:00', endTime: '18:00', status: 'Active' }, day), 'outsideAvailable')
// 全天不能上班：任何班次都冲突
assert.equal(getScheduleAvailabilityConflict({ startTime: '09:00', endTime: '17:00', status: 'Active' }, u1Wed), 'unavailable')
// 没上报 / 只上报不能上班且不重叠：不判断
assert.equal(getScheduleAvailabilityConflict({ startTime: '09:00', endTime: '17:00', status: 'Active' }, undefined), undefined)
const onlyUnavailable = getDayAvailability(
  buildAvailabilityIndex([item('u9', '2026-10-27', '18:00', '22:00', { isUnavailable: true })]),
  'u9',
  '2026-10-27',
)
assert.equal(getScheduleAvailabilityConflict({ startTime: '09:00', endTime: '17:00', status: 'Active' }, onlyUnavailable), undefined)
// 已取消的班次、跨午夜班次不判断
assert.equal(getScheduleAvailabilityConflict({ startTime: '10:00', endTime: '19:00', status: 'Cancelled' }, day), undefined)
assert.equal(getScheduleAvailabilityConflict({ startTime: '20:00', endTime: '02:00', status: 'Active' }, day), undefined)

console.log('availabilityLogic tests passed')
