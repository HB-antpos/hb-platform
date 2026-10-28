import assert from 'node:assert/strict'

import type { AttendanceScheduleDto } from '../../../types/scheduleAttendance'

import { buildAvailabilityIndex, getDayAvailability } from './availabilityLogic'
import {
  buildCopyPayload,
  buildMoveUpdatePayload,
  buildPresetPayload,
  buildTransferCreatePayload,
  collectPunchScheduleGuids,
  getDropVerdict,
  getMealBadge,
  isScheduleLocked,
  rangesOverlap,
} from './scheduleDragLogic'
import type { DragSource } from './scheduleDragLogic'

const schedule = (extra: Partial<AttendanceScheduleDto> = {}): AttendanceScheduleDto => ({
  scheduleGuid: 's-1',
  storeCode: '1013',
  userGuid: 'u-sean',
  workDate: '2026-10-26T00:00:00',
  startTime: '09:00:00',
  endTime: '17:30:00',
  status: 'Draft',
  remark: '备注',
  ...extra,
})

// 时段重叠：首尾相接不算，脏数据（结束不晚于开始）不判
assert.equal(rangesOverlap('09:00', '13:00', '12:00', '17:00'), true)
assert.equal(rangesOverlap('09:00', '13:00', '13:00', '17:00'), false)
assert.equal(rangesOverlap('09:00', '13:00', '16:00', '17:30'), false)
assert.equal(rangesOverlap('09:00:00', '17:30:00', '16:00', '17:30'), true)
assert.equal(rangesOverlap('17:00', '09:00', '09:00', '17:00'), false)
assert.equal(rangesOverlap('', '09:00', '09:00', '17:00'), false)

const move = (extra: Partial<AttendanceScheduleDto> = {}): DragSource => {
  const item = schedule(extra)
  return { kind: 'move', schedule: item, start: '09:00', end: '17:30' }
}
const sourceCell = { userGuid: 'u-sean', dateKey: '2026-10-26' }

// 落点：空格可放；原格放回是 noop
assert.deepEqual(getDropVerdict(move(), { userGuid: 'u-staff', dateKey: '2026-10-28', schedules: [] }, sourceCell), { state: 'ok' })
assert.deepEqual(getDropVerdict(move(), { userGuid: 'u-sean', dateKey: '2026-10-26', schedules: [schedule()] }, sourceCell), { state: 'noop' })

// 与目标格已有班次重叠 → 不能放；已取消的班次不算
const existing = (startTime: string, endTime: string, status: 'Draft' | 'Active' | 'Cancelled' = 'Draft', guid = 'x') => ({ scheduleGuid: guid, startTime, endTime, status })
assert.deepEqual(
  getDropVerdict(move(), { userGuid: 'u-staff', dateKey: '2026-10-29', schedules: [existing('09:00', '17:00')] }, sourceCell),
  { state: 'bad', reason: 'overlap' },
)
assert.deepEqual(
  getDropVerdict(move(), { userGuid: 'u-staff', dateKey: '2026-10-29', schedules: [existing('09:00', '17:00', 'Cancelled')] }, sourceCell),
  { state: 'ok' },
)
// 同一天不重叠的班次可以并排（9–13 旁边放 16:00–17:30）
assert.deepEqual(
  getDropVerdict({ kind: 'preset', start: '16:00', end: '17:30' }, { userGuid: 'u-staff', dateKey: '2026-10-29', schedules: [existing('09:00', '13:00')] }),
  { state: 'ok' },
)
// 移动时同一天内挪动：忽略它自己（把 9–13 挪到同一天的下午不会和自己重叠）
assert.deepEqual(
  getDropVerdict(
    { kind: 'move', schedule: schedule({ startTime: '09:00', endTime: '13:00' }), start: '09:00', end: '13:00' },
    { userGuid: 'u-sean', dateKey: '2026-10-27', schedules: [existing('09:00', '13:00', 'Draft', 's-1')] },
    sourceCell,
  ),
  { state: 'ok' },
)
// 复制到原格：会与原班次自己重叠 → 不能放
assert.deepEqual(
  getDropVerdict({ ...move(), kind: 'copy' }, { userGuid: 'u-sean', dateKey: '2026-10-26', schedules: [existing('09:00', '17:30', 'Draft', 's-1')] }, sourceCell),
  { state: 'bad', reason: 'overlap' },
)

// 员工上报：不能上班 / 超出可上班 → 只提醒（warn），不阻止
const index = buildAvailabilityIndex([
  { availabilityGuid: 'a1', storeCode: '1013', userGuid: 'u-staff', weekStartDate: '2026-10-26', availableDate: '2026-10-27', startTime: '00:00:00', endTime: '23:59:59', status: 'Active', isUnavailable: true },
  { availabilityGuid: 'a2', storeCode: '1013', userGuid: 'u-staff', weekStartDate: '2026-10-26', availableDate: '2026-10-30', startTime: '09:00:00', endTime: '13:00:00', status: 'Active' },
])
assert.deepEqual(
  getDropVerdict(move(), { userGuid: 'u-staff', dateKey: '2026-10-27', schedules: [], availability: getDayAvailability(index, 'u-staff', '2026-10-27') }, sourceCell),
  { state: 'warn', reason: 'unavailable' },
)
assert.deepEqual(
  getDropVerdict(move(), { userGuid: 'u-staff', dateKey: '2026-10-30', schedules: [], availability: getDayAvailability(index, 'u-staff', '2026-10-30') }, sourceCell),
  { state: 'warn', reason: 'outsideAvailable' },
)
// 重叠优先于提醒
assert.deepEqual(
  getDropVerdict(move(), { userGuid: 'u-staff', dateKey: '2026-10-27', schedules: [existing('10:00', '12:00')], availability: getDayAvailability(index, 'u-staff', '2026-10-27') }, sourceCell),
  { state: 'bad', reason: 'overlap' },
)

// 锁定：本周打卡里有这条班次 → 锁；能看到打卡时，没打卡的未来班次不锁
const punchGuids = collectPunchScheduleGuids([{ scheduleGuid: 's-1' }, { scheduleGuid: undefined }, { scheduleGuid: 's-1' }, { scheduleGuid: 's-2' }])
assert.deepEqual(Array.from(punchGuids).sort(), ['s-1', 's-2'])
const lock = (over: Partial<Parameters<typeof isScheduleLocked>[0]>) => isScheduleLocked({
  scheduleGuid: 's-9', workDateKey: '2026-10-29', todayKey: '2026-10-28', punchScheduleGuids: punchGuids, canViewPunches: true, ...over,
})
assert.equal(lock({ scheduleGuid: 's-1' }), true)
assert.equal(lock({}), false)
// 能看到打卡：即使是今天、没打卡的班次也不锁
assert.equal(lock({ workDateKey: '2026-10-28' }), false)
// 看不到打卡（无权限 / 拉取失败）：今天及以前保守地锁，未来不锁
assert.equal(lock({ canViewPunches: false, workDateKey: '2026-10-28' }), true)
assert.equal(lock({ canViewPunches: false, workDateKey: '2026-10-26' }), true)
assert.equal(lock({ canViewPunches: false, workDateKey: '2026-10-29' }), false)

// 各操作的保存载荷
{
  const manual = schedule({ status: 'Active', mealBreakCount: 0 })
  // 同员工换日期：更新载荷保持原状态、时间、备注；不带用餐字段（后端保持原值）
  assert.deepEqual(buildMoveUpdatePayload(manual, '2026-10-29'), {
    storeCode: '1013', userGuid: 'u-sean', workDate: '2026-10-29', startTime: '09:00', endTime: '17:30', remark: '备注', status: 'Active',
  })
  // 换员工：新建载荷保持原状态与备注，手动指定的用餐次数（含 0）一并带过去
  assert.deepEqual(buildTransferCreatePayload(manual, 'u-staff', '2026-10-28'), {
    storeCode: '1013', userGuid: 'u-staff', workDate: '2026-10-28', startTime: '09:00', endTime: '17:30', remark: '备注', status: 'Active', mealBreakCount: 0,
  })
  // 自动（没有手动次数）的班次转移时不带用餐字段
  assert.equal('mealBreakCount' in buildTransferCreatePayload(schedule(), 'u-staff', '2026-10-28'), false)
  // 复制：一律草稿，带备注和手动次数
  const copy = buildCopyPayload(manual, 'u-staff', '2026-10-28')
  assert.equal(copy.status, 'Draft')
  assert.equal(copy.mealBreakCount, 0)
  assert.equal(copy.remark, '备注')
  // 托盘添加：草稿，不带用餐字段（自动）
  assert.deepEqual(buildPresetPayload('1013', 'u-staff', '2026-10-28', '16:00', '17:30'), {
    storeCode: '1013', userGuid: 'u-staff', workDate: '2026-10-28', startTime: '16:00', endTime: '17:30', status: 'Draft',
  })
}

// 班次块上的用餐标记：只有手动指定（含 0）才有；自动 / null 没有
assert.deepEqual(getMealBadge(0), { count: 0 })
assert.deepEqual(getMealBadge(2), { count: 2 })
assert.equal(getMealBadge(undefined), undefined)
assert.equal(getMealBadge(null), undefined)

console.log('scheduleDragLogic tests passed')
