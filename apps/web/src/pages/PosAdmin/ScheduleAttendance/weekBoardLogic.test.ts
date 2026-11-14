import assert from 'node:assert/strict'

import type { DayAvailability } from './availabilityLogic'
import {
  buildStaffLegend,
  compareDayShifts,
  findAvailableUnscheduled,
  groupSchedulesByDay,
  matchStaffKeyword,
  sortDayShifts,
  summarizeDayList,
} from './weekBoardLogic'
import type { BoardSchedule } from './weekBoardLogic'

const names: Record<string, string> = { a: 'Chaise', b: 'Charlie', c: 'Ruby', d: 'Zoe', e: 'alice' }
const nameOf = (userGuid: string) => names[userGuid] ?? userGuid

let seq = 0
const shift = (userGuid: string, start: string, end: string, extra: Partial<BoardSchedule> = {}): BoardSchedule => {
  seq += 1
  return { scheduleGuid: `s${seq}`, userGuid, workDate: '2026-11-23', startTime: start, endTime: end, status: 'Draft', ...extra }
}

// 排序：开始时间优先，其次员工名（忽略大小写），再次结束时间
const sample = [
  shift('d', '15:00', '17:30'),
  shift('b', '15:00', '17:30'),
  shift('a', '15:00', '17:30'),
  shift('c', '15:00', '17:30'),
  shift('b', '09:00', '17:30'),
  shift('e', '15:00', '17:30'),
  shift('a', '07:00', '11:00'),
]
const sorted = sortDayShifts(sample, nameOf)
assert.deepEqual(
  sorted.map((item) => `${item.startTime}-${nameOf(item.userGuid)}`),
  ['07:00-Chaise', '09:00-Charlie', '15:00-alice', '15:00-Chaise', '15:00-Charlie', '15:00-Ruby', '15:00-Zoe'],
)
// 不修改入参
assert.equal(sample[0].userGuid, 'd')

// 同人同开始时间：结束早的在前；完全相同按班次 ID 兜底，结果稳定
const early = shift('a', '15:00', '16:00')
const late = shift('a', '15:00', '17:30')
assert.ok(compareDayShifts(early, late, nameOf) < 0)
assert.ok(compareDayShifts(late, early, nameOf) > 0)
const twinA = shift('a', '15:00', '17:30')
const twinB = shift('a', '15:00', '17:30')
assert.equal(Math.sign(compareDayShifts(twinA, twinB, nameOf)), Math.sign(twinA.scheduleGuid.localeCompare(twinB.scheduleGuid)))
// 接口时间带秒也能比
assert.ok(compareDayShifts(shift('a', '09:00:00', '10:00:00'), shift('a', '10:00:00', '11:00:00'), nameOf) < 0)

// 按天分组：带时间的日期取前 10 位；周外日期丢弃；空的天也有一项
const grouped = groupSchedulesByDay(
  [
    shift('a', '09:00', '12:00', { workDate: '2026-11-23T00:00:00' }),
    shift('b', '08:00', '12:00', { workDate: '2026-11-23' }),
    shift('c', '10:00', '12:00', { workDate: '2026-11-25' }),
    shift('d', '10:00', '12:00', { workDate: '2026-12-31' }),
  ],
  ['2026-11-23', '2026-11-24', '2026-11-25'],
  nameOf,
)
assert.deepEqual(Array.from(grouped.keys()), ['2026-11-23', '2026-11-24', '2026-11-25'])
assert.deepEqual(grouped.get('2026-11-23')?.map((item) => item.userGuid), ['b', 'a'])
assert.deepEqual(grouped.get('2026-11-24'), [])
assert.equal(grouped.get('2026-11-25')?.length, 1)

// 每天汇总：同一人多班只算 1 人；已取消不算
assert.deepEqual(
  summarizeDayList([
    shift('a', '09:00', '13:00'),
    shift('a', '14:00', '17:30'),
    shift('b', '09:00', '17:30'),
    shift('c', '09:00', '17:00', { status: 'Cancelled' }),
  ]),
  { people: 2, hours: 16, shifts: 3 },
)
assert.deepEqual(summarizeDayList([]), { people: 0, hours: 0, shifts: 0 })

// 图例：本周出现过班次的人（含只有已取消班次的），按姓名排；合计不含已取消
const legend = buildStaffLegend(
  [
    shift('d', '09:00', '13:00'),
    shift('a', '09:00', '13:00'),
    shift('a', '14:00', '17:30'),
    shift('c', '09:00', '17:00', { status: 'Cancelled' }),
  ],
  nameOf,
)
assert.deepEqual(legend.map((item) => item.name), ['Chaise', 'Ruby', 'Zoe'])
assert.deepEqual(
  legend.map((item) => [item.shifts, item.hours]),
  [[2, 7.5], [0, 0], [1, 4]],
)

// 可上班未排：当天上报了可上班、且当天没有未取消班次的人
const avail = (hasAvailable: boolean): DayAvailability => ({
  available: hasAvailable ? [{ startMin: 720, endMin: 1050, startLabel: '12:00', endLabel: '17:30', allDay: false }] : [],
  unavailable: [],
})
const availabilityOf = (userGuid: string): DayAvailability | undefined => (
  userGuid === 'a' || userGuid === 'b' || userGuid === 'c' ? avail(true) : userGuid === 'd' ? avail(false) : undefined
)
assert.deepEqual(
  findAvailableUnscheduled(
    '2026-11-23',
    ['a', 'b', 'c', 'd', 'e'],
    [shift('a', '09:00', '12:00'), shift('b', '09:00', '12:00', { status: 'Cancelled' })],
    availabilityOf,
  ),
  ['b', 'c'],
)

// 搜索高亮：忽略大小写；空关键字不高亮
const users = [{ userGuid: 'a', name: 'Chaise Holland' }, { userGuid: 'b', name: 'Charlie' }, { userGuid: 'c' }]
assert.deepEqual(Array.from(matchStaffKeyword(users, 'CHA')), ['a', 'b'])
assert.deepEqual(Array.from(matchStaffKeyword(users, 'hol')), ['a'])
assert.equal(matchStaffKeyword(users, '  ').size, 0)
assert.equal(matchStaffKeyword(users, undefined).size, 0)

console.log('weekBoardLogic tests passed')
