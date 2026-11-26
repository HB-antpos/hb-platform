import assert from 'node:assert/strict'
import dayjs from 'dayjs'
import type { AttendanceApprovalDto, AttendanceScheduleDto } from '../../../types/scheduleAttendance'
import {
  buildDayPreviewFromRecord,
  buildTimelineModel,
  canRejectCategory,
  canReviewItem,
  canSelectForBatch,
  countByCategory,
  describeSubmittedTime,
  findAutoClockOutPunchGuid,
  paidHoursAfterOvertime,
  getReviewCategory,
  initialsOf,
  isBatchableCategory,
  isSystemGenerated,
  pickNextApprovalGuid,
  summarizePending,
  tierOfWorkDate,
} from './reviewLogic'

const base: AttendanceApprovalDto = {
  approvalGuid: 'a',
  sourceType: 'PunchAdjustment',
  sourceGuid: 's',
  storeCode: 'CH',
  applicantUserGuid: 'u-staff',
  title: '',
  reviewStatus: 'Pending',
}
const item = (patch: Partial<AttendanceApprovalDto>): AttendanceApprovalDto => ({ ...base, ...patch })
const MANAGER = 'U-MANAGER'

// 分类：请假按假种细分，新增的病假 / 年假 / 年假发放单独成类
assert.equal(getReviewCategory(item({ sourceType: 'Leave', leave: { leaveType: 'SickLeave', startDate: '', endDate: '', attachmentUrls: [] } })), 'Leave')
assert.equal(getReviewCategory(item({ sourceType: 'Leave', leave: { leaveType: 'PaidSick', startDate: '', endDate: '', attachmentUrls: [] } })), 'PaidSick')
assert.equal(getReviewCategory(item({ sourceType: 'Leave', leave: { leaveType: 'AnnualCashOut', startDate: '', endDate: '', attachmentUrls: [] } })), 'AnnualCashOut')
assert.equal(getReviewCategory(item({ sourceType: 'PublicHolidayPay' })), 'PublicHolidayPay')

// 批量只开放给补卡、请假、打卡异常
assert.deepEqual(
  (['PunchAdjustment', 'Leave', 'Punch', 'Overtime', 'MealBreak', 'MissingClockOut', 'NoShow', 'PaidSick', 'PaidAnnual', 'AnnualCashOut', 'PublicHolidayPay'] as const)
    .filter(isBatchableCategory),
  ['PunchAdjustment', 'Leave', 'Punch'],
)

// 缺勤（整班未打卡）：独立分类、系统生成、只能确认不能驳回、不能批量
const noShow = item({ sourceType: 'NoShow', scheduleStartTime: '09:00', scheduleEndTime: '17:00' })
assert.equal(getReviewCategory(noShow), 'NoShow')
assert.equal(isSystemGenerated(noShow), true)
assert.equal(canRejectCategory('NoShow'), false)
assert.equal(canRejectCategory('MissingClockOut'), true)
assert.equal(canSelectForBatch(noShow, MANAGER), false)

// 本人申请：不能勾选批量；没有 ReviewSelf 不能审，有则可逐条审（GUID 大小写不敏感）
const own = item({ applicantUserGuid: 'u-manager' })
assert.equal(canSelectForBatch(own, MANAGER), false)
assert.equal(canReviewItem(own, MANAGER, false), false)
assert.equal(canReviewItem(own, MANAGER, true), true)
assert.equal(canSelectForBatch(item({}), MANAGER), true)
assert.equal(canSelectForBatch(item({ sourceType: 'Overtime' }), MANAGER), false)
assert.equal(canReviewItem(item({ reviewStatus: 'Approved' }), MANAGER, true), false)

assert.deepEqual(
  [...countByCategory([item({}), item({ approvalGuid: 'b' }), item({ approvalGuid: 'c', sourceType: 'Overtime' })]).entries()],
  [['PunchAdjustment', 2], ['Overtime', 1]],
)

// 批准后选下一条：同位置的后一条，末尾则前一条
const list = ['a', 'b', 'c'].map((guid) => item({ approvalGuid: guid }))
assert.equal(pickNextApprovalGuid(list, 'a'), 'b')
assert.equal(pickNextApprovalGuid(list, 'c'), 'b')
assert.equal(pickNextApprovalGuid([list[0]], 'a'), undefined)

// 时薪档：周六 / 周日按日期，公假以后端预览为准
assert.equal(tierOfWorkDate('2026-10-17'), 'saturday')
assert.equal(tierOfWorkDate('2026-10-18'), 'sunday')
assert.equal(tierOfWorkDate('2026-10-15'), 'weekday')
assert.equal(tierOfWorkDate('2026-10-05', 'publicHoliday'), 'publicHoliday')

// 提交时间
const now = dayjs('2026-10-20T10:00:00')
assert.deepEqual(describeSubmittedTime('2026-10-20T09:59:30', now), { kind: 'justNow' })
assert.deepEqual(describeSubmittedTime('2026-10-20T09:20:00', now), { kind: 'minutesAgo', value: 40 })
assert.deepEqual(describeSubmittedTime('2026-10-20T00:00:00', now), { kind: 'hoursAgo', value: 10 })
assert.deepEqual(describeSubmittedTime('2026-10-19T23:30:00', now), { kind: 'hoursAgo', value: 10 })
assert.deepEqual(describeSubmittedTime('2026-10-19T18:05:00', now), { kind: 'yesterday', time: '18:05' })
assert.deepEqual(describeSubmittedTime('2026-10-19T08:05:00', now), { kind: 'yesterday', time: '08:05' })
assert.deepEqual(describeSubmittedTime('2026-10-16T18:20:00', now), { kind: 'date', date: '10-16', time: '18:20' })

// 时间线：批准后替换的段画成绿色虚线，不再画原来的自动下班段
const model = buildTimelineModel({
  scheduleStart: '09:00',
  scheduleEnd: '17:30',
  segments: [{ start: '08:57', end: '12:30' }, { start: '13:02', end: '24:00', isSystemAutoClockOut: true }],
  proposedSegments: [{ start: '13:02', end: '17:30' }],
})!
assert.deepEqual(model.ticks, [8, 10, 12, 14, 16, 18, 20])
assert.deepEqual(model.actual.map((bar) => bar.kind), ['actual', 'proposed'])
assert.ok(model.schedule && model.schedule.left > 0 && model.schedule.width > 0)
// 只有自动下班段时，画到轴末尾并标灰
const autoOnly = buildTimelineModel({ scheduleStart: '16:00', scheduleEnd: '21:30', segments: [{ start: '16:02', end: '24:00', isSystemAutoClockOut: true }] })!
assert.equal(autoOnly.actual[0].kind, 'auto')
assert.ok(autoOnly.actual[0].left + autoOnly.actual[0].width <= 100.0001)

// 由当天考勤记录推算（后端审批不带当天预览）：Jack 10-18 第 2 段被系统 00:00 自动下班，补卡改成 17:30
const punch = (punchGuid: string, punchTimeLocal: string, isSystemAutoClockOut = false) => ({
  punchGuid, punchTimeLocal, isSystemAutoClockOut, storeCode: 'CH', userGuid: 'u-jack', workDate: '2026-10-18', punchType: 'ClockIn' as const, status: 'Normal' as const,
})
const jackRecord: AttendanceScheduleDto = {
  scheduleGuid: 'sch-jack-1018',
  storeCode: 'CH',
  userGuid: 'u-jack',
  workDate: '2026-10-18',
  startTime: '09:00:00',
  endTime: '17:30:00',
  status: 'Active',
  segmentLimit: 4,
  workedMinutes: 213,
  paidMinutes: 210,
  segments: [
    { segmentIndex: 0, clockIn: punch('in-1', '2026-10-18T08:57:00'), clockOut: punch('out-1', '2026-10-18T12:30:00'), durationMinutes: 213 },
    { segmentIndex: 1, clockIn: punch('in-2', '2026-10-18T13:02:00'), clockOut: punch('auto-2', '2026-10-19T00:00:00', true), durationMinutes: 0 },
  ],
}
const jackPreview = buildDayPreviewFromRecord(jackRecord, item({
  sourceType: 'PunchAdjustment',
  workDate: '2026-10-18',
  adjustment: { punchType: 'ClockOut', originalPunchGuid: 'auto-2', requestedPunchTimeLocal: '2026-10-18T17:30:00' },
}))
assert.equal(jackPreview.scheduleStart, '09:00')
assert.deepEqual(jackPreview.segments, [{ start: '08:57', end: '12:30', isSystemAutoClockOut: false }], '被改的自动下班段换成批准后的段')
assert.deepEqual(jackPreview.proposedSegments, [{ start: '13:02', end: '17:30' }])
assert.deepEqual(jackPreview.replaced, { segmentNumber: 2, originalTime: '00:00', isSystemAutoClockOut: true })
// 实际工时：3:33 + 4:28 = 8:01 → 8.02（与设计稿一致）；计薪显示当前值
assert.equal(jackPreview.workedHoursAfter, 8.02)
assert.equal(jackPreview.workedHours, 3.55)
assert.equal(jackPreview.paidHours, 3.5)
assert.equal(jackPreview.tier, 'sunday')
assert.equal(findAutoClockOutPunchGuid(jackRecord), 'auto-2')
// 漏下班：店长填 21:30，只改最后一段的结束
const missing = buildDayPreviewFromRecord(jackRecord, item({ sourceType: 'MissingClockOut', workDate: '2026-10-18' }), '17:30')
assert.deepEqual(missing.proposedSegments, [{ start: '13:02', end: '17:30' }])
assert.equal(missing.workedHoursAfter, 8.02)
// 改上班卡：新段从申请时间到原下班
const clockInPreview = buildDayPreviewFromRecord(jackRecord, item({
  sourceType: 'PunchAdjustment',
  workDate: '2026-10-18',
  adjustment: { punchType: 'ClockIn', originalPunchGuid: 'in-1', requestedPunchTimeLocal: '2026-10-18T08:55:00' },
}))
assert.deepEqual(clockInPreview.proposedSegments, [{ start: '08:55', end: '12:30' }])
assert.equal(clockInPreview.replaced?.originalTime, '08:57')

assert.equal(initialsOf('Jack Nguyen'), 'JN')
assert.equal(initialsOf(''), '?')

console.log('reviewLogic.test.ts: ok')

// ───── 待审概览：侧栏各店角标 + 范围外提示 ─────
{
  const pending = [
    { storeCode: '1002', workDate: '2026-11-17' },
    { storeCode: '1002', workDate: '2026-11-22T00:00:00' },
    { storeCode: '1013', workDate: '2026-11-16' },
    { storeCode: '1002', workDate: '2026-11-10' },
    { storeCode: '1013', workDate: '2026-11-03' },
    { storeCode: '1013', workDate: undefined },
  ]
  const all = summarizePending(pending, '2026-11-16', '2026-11-22')
  assert.deepEqual(all.byStore, { '1002': 2, '1013': 2 }, '首尾日都算在内；没有工作日的算在范围内')
  assert.equal(all.outside, 2)
  assert.equal(all.outsideFrom, '2026-11-03')
  assert.equal(all.outsideTo, '2026-11-10')
  const one = summarizePending(pending, '2026-11-16', '2026-11-22', '1002')
  assert.deepEqual(one.byStore, { '1002': 2, '1013': 2 }, '角标始终是各店的数，不受当前选店影响')
  assert.equal(one.outside, 1, '范围外只数当前选的店')
  assert.equal(one.outsideFrom, '2026-11-10')
  assert.deepEqual(summarizePending([], '2026-11-16', '2026-11-22'), { byStore: {}, outside: 0, outsideFrom: undefined, outsideTo: undefined })
}

// 加班审核的计薪工时（工资口径）：Breanna 11-24 排班 09:00–17:30、打卡约 07:45–17:5x，候选加班 75 分钟。
// 「现在」只计排班内 510 − 用餐 30 = 480（8.00 h），不再把未批的早到 / 晚走算进去；
// 批准后按输入实时推算，超过候选分钟按候选封顶。
{
  const record: AttendanceScheduleDto = {
    ...jackRecord,
    workedMinutes: 583,
    paidMinutes: 553,
    payrollPaidMinutes: 480,
    candidateOvertimeMinutes: 75,
    approvedOvertimeMinutes: 0,
  }
  const preview = buildDayPreviewFromRecord(record, item({ sourceType: 'Overtime', workDate: '2026-11-24' }))
  assert.equal(preview.paidHours, 8, '计薪工时（现在）用工资口径，不含未批加班')
  assert.equal(paidHoursAfterOvertime(preview, 0), 8, '批 0 分钟＝只计排班内')
  assert.equal(paidHoursAfterOvertime(preview, 75), 9.25)
  assert.equal(paidHoursAfterOvertime(preview, 30), 8.5)
  assert.equal(paidHoursAfterOvertime(preview, 200), 9.25, '超过候选按候选封顶')
  assert.equal(paidHoursAfterOvertime(preview, null), 8)
  // 已有另一条已批 30 分钟：工资口径已含这 30，本次再批 30 → 共 60
  assert.equal(paidHoursAfterOvertime({ payrollPaidMinutes: 510, approvedOvertimeMinutes: 30, candidateOvertimeMinutes: 75 }, 30), 9)
  // 旧后端没有工资口径字段：退回旧 paidMinutes，且不做批准后推算
  const legacy = buildDayPreviewFromRecord({ ...record, payrollPaidMinutes: undefined }, item({ sourceType: 'Overtime', workDate: '2026-11-24' }))
  assert.equal(legacy.paidHours, 9.22)
  assert.equal(paidHoursAfterOvertime(legacy, 75), undefined)
}
