import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { normalizePayRate, normalizePayrollHours, normalizePayrollPay } from './attendancePayrollService'
import { normalizeAttendanceApproval, normalizeAttendancePunch, normalizeAttendanceSettings } from './scheduleAttendanceService'

// 联调口径：字段按后端 AttendancePayrollDtos.cs / AttendanceDtos.cs 的 camelCase 返回，DateTime 截成 YYYY-MM-DD。

// 工时统计：日期规整、提醒用 type、可空字段兜底
const hours = normalizePayrollHours({
  storeCode: '1013',
  periodStart: '2026-10-05T00:00:00',
  periodEnd: '2026-10-18T00:00:00',
  weeks: 2,
  weekNumber: 41,
  periodStatus: 'Open',
  pendingCount: 6,
  days: [{ date: '2026-10-05T00:00:00', isoWeekday: 1, isPublicHoliday: true, holidayName: "King's Birthday" }],
  employees: [{
    userGuid: 'u1',
    userName: 'Jack Nguyen',
    employmentType: null,
    birthday: '2009-10-16T00:00:00',
    days: [{
      date: '2026-10-05T00:00:00',
      paidHours: null,
      byTier: { weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0 },
      hasSchedule: false,
      publicHolidayPay: { state: 'Confirmed', hours: 7.6, count: 8 },
    }],
    totals: { weekday: 38.78, saturday: 8, sunday: 3.5, publicHoliday: 0, leave: 15.6, total: 65.88, sick: 8, annual: 0, cashOut: 0, publicHolidayPay: 7.6 },
  }],
  alerts: [{
    type: 'MultiStore',
    otherStoreItems: [{ userGuid: 'u2', userName: 'Liam Patel', date: '2026-10-07T00:00:00', storeCode: '1042', storeName: 'Sunnybank', hours: 5.5 }],
  }],
})
assert.equal(hours.periodStart, '2026-10-05')
assert.equal(hours.periodEnd, '2026-10-18')
assert.equal(hours.days[0].date, '2026-10-05')
assert.equal(hours.employees[0].days[0].date, '2026-10-05')
assert.equal(hours.employees[0].birthday, '2009-10-16')
assert.deepEqual(hours.employees[0].days[0].pending, [], '后端缺 pending 时兜底为空数组')
assert.equal(hours.alerts[0].type, 'MultiStore')
assert.equal(hours.alerts[0].otherStoreItems?.[0].date, '2026-10-07')

// 工资表：后端总会返回 annual / cashOut（默认 0），申请与核定都为 0 时去掉，界面不显示核定框
const pay = normalizePayrollPay({
  storeCode: '1013',
  periodStart: '2026-10-05T00:00:00',
  weeks: 2,
  periodStatus: 'Open',
  pendingCount: 0,
  rows: [
    {
      userGuid: 'u-emma',
      userName: 'Emma Walsh',
      payMode: 'Transfer',
      hours: { weekday: 19, saturday: 14.98, sunday: 0, publicHoliday: 0, leave: 20, total: 53.98 },
      leaveDetail: {
        sick: 0,
        publicHolidayPay: 0,
        annual: { leaveType: 'PaidAnnual', requestedHours: 24, approvedHours: 20, isFinanceApproved: true },
        cashOut: { leaveType: 'AnnualCashOut', requestedHours: 0, approvedHours: 0, isFinanceApproved: false },
      },
      transfer: { buckets: { sunday: 0, saturday: 14.98, leave: 20, publicHoliday: 0, weekday: 19 }, hours: 53.98, amount: 1702.96 },
      cash: { buckets: { sunday: 0, saturday: 0, leave: 0, publicHoliday: 0, weekday: 0 }, hours: 0, amount: 0 },
      allowance: 0,
      grossPay: 1702.96,
      warnings: [],
    },
    {
      userGuid: 'u-new',
      userName: 'New Staff',
      payMode: null,
      hours: { weekday: 4, saturday: 0, sunday: 0, publicHoliday: 0, leave: 0, total: 4 },
      transfer: { buckets: { sunday: 0, saturday: 0, leave: 0, publicHoliday: 0, weekday: 0 }, hours: 0, amount: 0 },
      cash: { buckets: { sunday: 0, saturday: 0, leave: 0, publicHoliday: 0, weekday: 0 }, hours: 0, amount: 0 },
      allowance: 0,
      grossPay: 0,
      warnings: ['NO_RATE'],
    },
  ],
  totalGrossPay: 1702.96,
})
assert.equal(pay.periodStart, '2026-10-05')
assert.equal(pay.rows[0].grossPay, 1702.96, '应发金额原样展示后端结果（验收样例 Emma $1,702.96）')
assert.deepEqual([pay.rows[0].leaveDetail.annual?.requestedHours, pay.rows[0].leaveDetail.annual?.approvedHours], [24, 20])
assert.equal(pay.rows[0].leaveDetail.cashOut, undefined, '申请与核定都为 0 的年假发放不显示')
assert.equal(pay.rows[1].payMode, null)
assert.deepEqual(pay.rows[1].leaveDetail, { sick: 0, publicHolidayPay: 0, annual: undefined, cashOut: undefined }, '缺 leaveDetail 时兜底')

// 时薪：日期截断、补贴兜底
const rate = normalizePayRate({ userGuid: 'u1', payMode: 'Mixed', effectiveFrom: '2026-10-05T00:00:00', birthday: null, cashAllowancePerPeriod: -20 })
assert.equal(rate.effectiveFrom, '2026-10-05')
assert.equal(rate.birthday, undefined)
assert.equal(rate.cashAllowancePerPeriod, -20)

// 审批：请假 / 带薪公假结构化字段（DateTime、TimeSpan 规整）
const approval = normalizeAttendanceApproval({
  approvalGuid: 'a1',
  sourceType: 'Leave',
  sourceGuid: 'l1',
  storeCode: '1013',
  applicantUserGuid: 'u1',
  employeeName: 'Jack Nguyen',
  reviewStatus: 'Pending',
  isSelfReview: false,
  leave: {
    leaveType: 'PaidSick',
    startDate: '2026-10-21T00:00:00',
    endDate: '2026-10-21T00:00:00',
    dayCount: 1,
    paidHours: 8,
    reason: '发烧',
    attachmentUrls: ['https://cos.example.invalid/a.jpg'],
    schedules: [{ scheduleGuid: 's1', workDate: '2026-10-21T00:00:00', startTime: '09:00:00', endTime: '17:30:00', paidHours: 8, hasPunch: false }],
    scheduleConflicts: [],
  },
})
assert.equal(approval.applicantName, 'Jack Nguyen')
assert.equal(approval.leave?.startDate, '2026-10-21')
assert.deepEqual(approval.leave?.schedules?.[0], { scheduleGuid: 's1', workDate: '2026-10-21', startTime: '09:00', endTime: '17:30', paidHours: 8, hasPunch: false })
const holiday = normalizeAttendanceApproval({
  approvalGuid: 'a2',
  sourceType: 'PublicHolidayPay',
  publicHolidayPay: { holidayDate: '2026-10-05T00:00:00', holidayName: "King's Birthday", ordinaryCount: 7, lookbackWeeks: 10, suggestedHours: 6, status: 'Pending' },
})
assert.equal(holiday.publicHolidayPay?.holidayDate, '2026-10-05')
assert.equal(holiday.publicHolidayPay?.suggestedHours, 6)

// 系统午夜自动下班卡标记（卡 A）
assert.equal(normalizeAttendancePunch({ punchGuid: 'p1', isSystemAutoClockOut: true }).isSystemAutoClockOut, true)

// 考勤设置新参数（卡 A 的段数 / 午夜自动下班 + 卡 B 的工资参数）；双周起始日截成日期
const settings = normalizeAttendanceSettings({
  lateGraceMinutes: 5,
  defaultPeriodWeeks: 2,
  fortnightAnchorDate: '2026-10-05T00:00:00',
  employeeSegmentLimit: 4,
  managerSegmentLimit: 6,
  autoClockOutAtMidnight: true,
})
assert.equal(settings.defaultPeriodWeeks, 2)
assert.equal(settings.fortnightAnchorDate, '2026-10-05')
assert.equal(settings.managerSegmentLimit, 6)

// 前端字段名必须在后端 DTO 里存在（PascalCase），防止联调后一边改名另一边没跟上
const backendPayroll = readFileSync(join(process.cwd(), '../../services/backend/BlazorApp.Shared/DTOs/AttendancePayrollDtos.cs'), 'utf8')
for (const field of [
  'PaidHours', 'ByTier', 'HasSchedule', 'IsUnavailable', 'PublicHolidayPay', 'ApprovedMarks', 'OtherStores', 'IsBirthday',
  'OtherStoreItems', 'OrdinaryCount', 'LookbackWeeks', 'MinCount', 'IsMinor', 'PeriodStatus', 'PendingCount',
  'LeaveDetail', 'RequestedHours', 'ApprovedHours', 'FixedTransferHoursPerWeek', 'Allowance', 'GrossPay', 'Warnings',
  'TransferWeekdayRate', 'TransferSaturdayRate', 'TransferSundayRate', 'TransferPublicHolidayRate', 'CashRate',
  'CashAllowancePerPeriod', 'MinimumRate', 'EffectiveFrom', 'BirthdayReviewHint',
]) {
  assert.ok(new RegExp(`public [^\\n]* ${field} \\{`).test(backendPayroll), `后端 AttendancePayrollDtos 缺字段 ${field}`)
}
const backendAttendance = readFileSync(join(process.cwd(), '../../services/backend/BlazorApp.Shared/DTOs/AttendanceDtos.cs'), 'utf8')
for (const field of ['IsSelfReview', 'ApprovedPaidHours', 'ScheduleConflicts', 'AttachmentUrls', 'SuggestedHours', 'DeletedAt', 'DeletedBy', 'FortnightAnchorDate']) {
  assert.ok(new RegExp(`public [^\\n]* ${field} \\{`).test(backendAttendance), `后端 AttendanceDtos 缺字段 ${field}`)
}
const controller = readFileSync(join(process.cwd(), '../../services/backend/BlazorApp.Api/Controllers/React/ReactAttendanceController.cs'), 'utf8')
for (const route of [
  'payroll/hours', 'payroll/pay', 'payroll/rates', 'payroll/rates/{userGuid}/history', 'payroll/periods/notes',
  'payroll/periods/leave-approvals', 'payroll/periods/submit', 'payroll/periods/unlock', 'schedules/deleted',
  'schedules/{scheduleGuid}/restore', 'managed/punch-adjustments/preview', 'managed/punch-adjustments',
]) {
  assert.ok(controller.includes(`"${route}"`), `后端缺路由 ${route}`)
}

console.log('attendancePayrollService.test: ok')
