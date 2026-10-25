import assert from 'node:assert/strict'
import dayjs from 'dayjs'
import type { AttendanceApprovalDto } from '../../../types/scheduleAttendance'
import {
  buildTimelineModel,
  canReviewItem,
  canSelectForBatch,
  countByCategory,
  describeSubmittedTime,
  getReviewCategory,
  initialsOf,
  isBatchableCategory,
  pickNextApprovalGuid,
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
  (['PunchAdjustment', 'Leave', 'Punch', 'Overtime', 'MealBreak', 'MissingClockOut', 'PaidSick', 'PaidAnnual', 'AnnualCashOut', 'PublicHolidayPay'] as const)
    .filter(isBatchableCategory),
  ['PunchAdjustment', 'Leave', 'Punch'],
)

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

assert.equal(initialsOf('Jack Nguyen'), 'JN')
assert.equal(initialsOf(''), '?')

console.log('reviewLogic.test.ts: ok')
