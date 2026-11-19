import assert from 'node:assert/strict'
import type { AttendanceHoursDayDto, AttendanceHoursEmployeeDto } from '../../../types/attendancePayroll'
import {
  buildManualSegments,
  collectMissingPunches,
  defaultManualRows,
  manualEntryCellKind,
  manualEntryDayState,
  manualEntryErrorText,
  manualPreviewHoursText,
  parseClock,
  validateManualRows,
} from './manualEntryLogic'

// ---------- 时间解析 ----------
assert.equal(parseClock('09:00'), 540)
assert.equal(parseClock('23:59'), 1439)
assert.equal(parseClock(' 07:05 '), 425)
assert.equal(parseClock('24:00'), null, '补录不跨午夜，24:00 不是合法的上下班时间')
assert.equal(parseClock('9:00'), null)
assert.equal(parseClock(''), null)

// ---------- 班段校验 ----------
assert.equal(validateManualRows([]), 'incomplete')
assert.equal(validateManualRows([{ start: '09:00', end: '' }]), 'incomplete')
assert.equal(validateManualRows([{ start: '09:00', end: '09:00' }]), 'range', '下班必须晚于上班')
assert.equal(validateManualRows([{ start: '10:00', end: '09:30' }]), 'range')
assert.equal(validateManualRows([{ start: '09:00', end: '15:00' }]), null)
assert.equal(
  validateManualRows([{ start: '13:00', end: '17:00' }, { start: '09:00', end: '12:00' }]),
  null,
  '班段不要求按输入顺序填，排序后不重叠即可',
)
assert.equal(
  validateManualRows([{ start: '09:00', end: '12:00' }, { start: '12:00', end: '15:00' }]),
  'overlap',
  '下一段上班要晚于上一段下班（相等也算重叠，与后端一致）',
)
assert.equal(validateManualRows([{ start: '09:00', end: '12:00' }, { start: '11:00', end: '15:00' }]), 'overlap')

// ---------- 请求拼装 ----------
assert.deepEqual(
  buildManualSegments('2026-11-17', [{ start: '13:00', end: '17:00' }, { start: '09:00', end: '12:00' }]),
  [
    { startLocal: '2026-11-17T09:00:00', endLocal: '2026-11-17T12:00:00' },
    { startLocal: '2026-11-17T13:00:00', endLocal: '2026-11-17T17:00:00' },
  ],
  '按上班时间排序，本地时间不带时区',
)

// ---------- 错误文案：去掉「CODE: 」前缀 ----------
assert.equal(manualEntryErrorText(new Error('PERIOD_LOCKED: 该分店这一期工资已提交锁定'), '失败'), '该分店这一期工资已提交锁定')
assert.equal(manualEntryErrorText(new Error('网络错误'), '失败'), '网络错误')
assert.equal(manualEntryErrorText('boom', '失败'), '失败')
assert.equal(manualEntryErrorText(new Error('  '), '失败'), '失败')

assert.equal(manualPreviewHoursText(5.5), '5.5 h')
assert.equal(manualPreviewHoursText(7.98), '7.98 h')

// ---------- 格子能不能补录 ----------
const day = (patch: Partial<AttendanceHoursDayDto> = {}): AttendanceHoursDayDto => ({
  date: '2026-11-17',
  paidHours: null,
  byTier: { weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0 },
  hasSchedule: false,
  pending: [],
  approvedMarks: [],
  otherStores: [],
  ...patch,
})
const options = { enabled: true, today: '2026-11-20' }

assert.equal(manualEntryCellKind(day(), options), 'addable', '没排班也没打卡的休息格：悬停可补录')
assert.equal(manualEntryCellKind(day({ hasSchedule: true, missingPunch: true, scheduleStart: '09:00', scheduleEnd: '15:30' }), options), 'missing')
assert.equal(manualEntryCellKind(day(), { ...options, enabled: false }), 'none', '没权限 / 本期已锁定：没有任何入口')
assert.equal(manualEntryCellKind(day({ date: '2026-11-21' }), options), 'none', '未来日期不能补')
assert.equal(manualEntryCellKind(day({ date: '2026-11-20' }), options), 'addable', '今天可以补已发生的时间')
assert.equal(manualEntryCellKind(day({ paidHours: 6, segments: [{ start: '09:00', end: '15:00' }] }), options), 'none', '已有打卡改走「修改打卡」')
assert.equal(manualEntryCellKind(day({ paidHours: 3 }), options), 'none', '已有工时不补')
assert.equal(
  manualEntryCellKind(day({ manualEntry: { reason: '忘记打卡', createdBy: 'sean' } }), options),
  'none',
  '已补录的格子不再重复补（要改先撤销）',
)
assert.equal(manualEntryCellKind(day({ leave: { type: 'PaidSick', hours: 7.6 } }), options), 'none', '带薪假那天不补')
assert.equal(manualEntryCellKind(day({ leave: { type: 'AnnualCashOut', hours: 8 } }), options), 'addable', '年假发放照常上班，可以补')

// ---------- 抽屉里日期按钮的状态 ----------
assert.equal(manualEntryDayState(undefined, '2026-11-17', '2026-11-20'), 'ok', '工时统计里没有这个人 = 一天都没有数据，可补')
assert.equal(manualEntryDayState(undefined, '2026-11-21', '2026-11-20'), 'future')
assert.equal(manualEntryDayState(day({ segments: [{ start: '09:00', end: '12:00' }] }), '2026-11-17', '2026-11-20'), 'hasPunches')
assert.equal(manualEntryDayState(day({ manualEntry: { reason: 'x' } }), '2026-11-17', '2026-11-20'), 'hasPunches')
assert.equal(manualEntryDayState(day({ leave: { type: 'PaidAnnual', hours: 8 } }), '2026-11-17', '2026-11-20'), 'leave')

// ---------- 默认班段：未打卡班预填排班时间，其它留空 ----------
assert.deepEqual(defaultManualRows(day({ missingPunch: true, scheduleStart: '09:00', scheduleEnd: '15:30' })), [{ start: '09:00', end: '15:30' }])
assert.deepEqual(defaultManualRows(day()), [{ start: '', end: '' }], '没排班不替人猜时间')
assert.deepEqual(defaultManualRows(), [{ start: '', end: '' }])

// ---------- 未打卡班汇总 ----------
const employee = (userGuid: string, userName: string, days: AttendanceHoursDayDto[]): AttendanceHoursEmployeeDto => ({
  userGuid,
  userName,
  days,
} as AttendanceHoursEmployeeDto)
const missing = collectMissingPunches([
  employee('u2', 'Kathryn', [day({ date: '2026-11-19', missingPunch: true, scheduleStart: '10:00', scheduleEnd: '16:00' })]),
  employee('u1', 'Javier', [
    day({ date: '2026-11-18', missingPunch: true, scheduleStart: '09:00', scheduleEnd: '15:30' }),
    day({ date: '2026-11-19', missingPunch: true }),
    day({ date: '2026-11-17' }),
  ]),
])
assert.deepEqual(
  missing.map((item) => `${item.date} ${item.userName}`),
  ['2026-11-18 Javier', '2026-11-19 Javier', '2026-11-19 Kathryn'],
  '按日期、再按姓名排序；只收未打卡的天',
)
assert.equal(missing[0].start, '09:00')
assert.deepEqual(collectMissingPunches([]), [])

console.log('manualEntryLogic tests passed')
