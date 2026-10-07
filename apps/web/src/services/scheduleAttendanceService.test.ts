import {
  normalizeAttendanceApproval,
  normalizeAttendancePunch,
  normalizeAttendanceSchedule,
  normalizeAttendanceSettings,
  normalizeAttendancePunchAdjustmentPreview,
  unwrapAttendanceApiData,
  normalizeAttendancePagedResult,
} from './scheduleAttendanceService'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

const schedule = normalizeAttendanceSchedule({
  ScheduleGuid: 'schedule-1',
  StoreCode: 'S001',
  UserGuid: 'user-1',
  WorkDate: '2026-07-21',
  StartTime: '09:00',
  EndTime: '17:00',
  Status: 'Active',
  ScheduleState: 'Completed',
  SegmentLimit: 3,
  CompletedSegmentCount: 2,
  WorkedMinutes: 420,
  BreakMinutes: 45,
  HasOpenSegment: false,
  HasMissingClockOut: true,
  EarlyOvertimeMinutes: 15,
  LateOvertimeMinutes: 30,
  CandidateOvertimeMinutes: 45,
  ApprovedOvertimeMinutes: 15,
  OvertimeApprovalStatus: 'Pending',
  Segments: [{
    SegmentIndex: 1,
    ClockIn: {
      PunchGuid: 'punch-in-1',
      StoreCode: 'S001',
      UserGuid: 'user-1',
      WorkDate: '2026-07-21',
      PunchType: 'ClockIn',
      PunchTimeLocal: '2026-07-21T08:45:00+10:00',
      Status: 'Normal',
    },
    ClockOut: '2026-07-21T12:00:00+10:00',
    DurationMinutes: 195,
    Status: 'Completed',
  }],
})

assertEqual(schedule.scheduleGuid, 'schedule-1', '应兼容 PascalCase 排班字段')
assertEqual(schedule.candidateOvertimeMinutes, 45, '应兼容 PascalCase 候选加班字段')
assertEqual(schedule.segments?.[0]?.segmentIndex, 1, '应兼容 PascalCase 班段字段')
assertEqual(typeof schedule.segments?.[0]?.clockIn === 'object' ? schedule.segments[0].clockIn.punchTimeLocal : undefined, '2026-07-21T08:45:00+10:00', '班段应兼容嵌套 Punch DTO')
assertEqual(schedule.hasMissingClockOut, true, '应兼容 PascalCase 漏下班状态')

const punch = normalizeAttendancePunch({
  PunchGuid: 'punch-1',
  StoreCode: 'S001',
  UserGuid: 'user-1',
  WorkDate: '2026-07-21',
  PunchType: 'ClockIn',
  Status: 'Approved',
  SegmentIndex: 2,
  SegmentStatus: 'Completed',
  IsBreakBoundary: true,
  SupersedesPunchGuid: 'raw-punch-1',
  AdjustmentGuid: 'adjustment-1',
})

assertEqual(punch.segmentIndex, 2, '应兼容 PascalCase 打卡班段字段')
assertEqual(punch.supersedesPunchGuid, 'raw-punch-1', '应保留原始打卡审计关系')
assertEqual(punch.adjustmentGuid, 'adjustment-1', '应保留补卡调整审计关系')

const approval = normalizeAttendanceApproval({
  ApprovalGuid: 'approval-1',
  SourceType: 'PunchAdjustment',
  SourceGuid: 'adjustment-1',
  StoreCode: 'S001',
  ApplicantUserGuid: 'user-1',
  WorkDate: '2026-07-21',
  Title: 'Punch correction',
  Detail: 'ClockIn · 2026-07-21 08:45 · Network outage',
  ReviewStatus: 'Pending',
  CandidateOvertimeMinutes: 45,
  ApprovedOvertimeMinutes: 15,
  Adjustment: {
    OriginalPunchTimeLocal: '2026-07-21T08:52:00+10:00',
    RequestedPunchTimeLocal: '2026-07-21T08:45:00+10:00',
    EffectivePunchTimeLocal: '2026-07-21T08:52:00+10:00',
    Reason: '设备无网络',
    IsDirectAdjustment: false,
  },
})

assertEqual(approval.mealClaim, undefined, '非用餐审批不应伪造用餐声明')

// 用餐未休息加工时：审批带 MealClaim，排班带用餐扣除与计薪工时（PascalCase 兼容）。
const mealApproval = normalizeAttendanceApproval({
  ApprovalGuid: 'approval-meal',
  SourceType: 'MealBreak',
  SourceGuid: 'claim-1',
  StoreCode: 'S001',
  ApplicantUserGuid: 'user-1',
  ReviewStatus: 'Pending',
  MealClaim: {
    ClaimGuid: 'claim-1',
    ExpectedCount: 1,
    RecordedCount: 0,
    MissingCount: 1,
    NotTakenCount: 1,
    ClaimedMinutes: 30,
    Status: 'Pending',
    Reason: '太忙',
  },
})
assertEqual(mealApproval.sourceType, 'MealBreak', '应保留用餐审批类型')
assertEqual(mealApproval.mealClaim?.claimedMinutes, 30, '应解析申请加回分钟')
assertEqual(mealApproval.mealClaim?.reason, '太忙', '应保留员工填写的原因')
const mealSchedule = normalizeAttendanceSchedule({
  ScheduleGuid: 'schedule-meal',
  MealDeductionMinutes: 30,
  PendingMealAddBackMinutes: 30,
  ApprovedMealAddBackMinutes: 0,
  PaidMinutes: 490,
  EffectiveMealBreakCount: 1,
})
assertEqual(mealSchedule.mealDeductionMinutes, 30, '应解析用餐扣除分钟')
assertEqual(mealSchedule.pendingMealAddBackMinutes, 30, '应解析待审加回分钟')
assertEqual(mealSchedule.paidMinutes, 490, '应解析计薪工时')
assertEqual(mealSchedule.effectiveMealBreakCount, 1, '应解析有效用餐次数')

assertEqual(approval.candidateOvertimeMinutes, 45, '审批应兼容候选加班')
assertEqual(approval.adjustment?.originalPunchTimeLocal, '2026-07-21T08:52:00+10:00', '审批应保留原始打卡时间')
assertEqual(approval.adjustment?.requestedPunchTimeLocal, '2026-07-21T08:45:00+10:00', '审批应兼容补卡明细')
assertEqual(approval.adjustment?.effectivePunchTimeLocal, '2026-07-21T08:52:00+10:00', '审批应保留实际生效时间')
assertEqual(approval.workDate, '2026-07-21', '审批应兼容工作日期')
assertEqual(approval.title, 'Punch correction', '审批应兼容标题')
assertEqual(approval.detail, 'ClockIn · 2026-07-21 08:45 · Network outage', '审批应兼容明细')

const legacySettings = normalizeAttendanceSettings({
  lateGraceMinutes: 5,
  earlyLeaveGraceMinutes: 5,
  allowNoSchedulePunch: true,
  requireApprovalForLate: true,
  requireApprovalForEarlyLeave: true,
  requireApprovalForNoSchedule: true,
})
assertEqual(legacySettings.lateGraceMinutes, 5, '旧设置字段应保持兼容')
assertEqual(legacySettings.overtimeMinimumMinutes, undefined, '旧响应缺少新阈值时不得伪造值')

const settings = normalizeAttendanceSettings({
  LateGraceMinutes: 5,
  EarlyLeaveGraceMinutes: 5,
  AllowNoSchedulePunch: true,
  RequireApprovalForLate: true,
  RequireApprovalForEarlyLeave: true,
  RequireApprovalForNoSchedule: true,
  OvertimeMinimumMinutes: 15,
  RequireOvertimeApproval: true,
  AllowManagerDirectOwnAdjustment: true,
})
assertEqual(settings.overtimeMinimumMinutes, 15, '新设置应兼容 PascalCase 阈值')
assertEqual(settings.requireOvertimeApproval, true, '新设置应兼容 PascalCase 审批开关')

const preview = normalizeAttendancePunchAdjustmentPreview({
  IsValid: true,
  ExistingSession: { WorkedMinutes: 420, BreakMinutes: 45, CandidateOvertimeMinutes: 15 },
  ProposedSession: { WorkedMinutes: 450, BreakMinutes: 45, CandidateOvertimeMinutes: 45 },
  WorkedMinutesDelta: 30,
  CandidateOvertimeMinutesDelta: 30,
  WouldAutoApprove: true,
  PreviewRevision: 'preview-revision-1',
})
assertEqual(preview.proposedSession?.workedMinutes, 450, '补卡预览应兼容 PascalCase 工时结果')
assertEqual(preview.candidateOvertimeMinutesDelta, 30, '补卡预览应兼容加班变化')
assertEqual(preview.wouldAutoApprove, true, '补卡预览应显示是否直接生效')
assertEqual(preview.previewRevision, 'preview-revision-1', '补卡预览应兼容 PascalCase revision')

let pascalFailure: unknown
try {
  unwrapAttendanceApiData({
    IsSuccess: false,
    Code: 'ATTENDANCE_PREVIEW_REJECTED',
    Message: '补卡时间不合法',
    Data: { IsValid: true },
  })
} catch (error) {
  pascalFailure = error
}
assertEqual(pascalFailure instanceof Error, true, 'PascalCase HTTP 200 失败 envelope 必须抛错')
assertEqual(
  pascalFailure instanceof Error ? pascalFailure.message : '',
  'ATTENDANCE_PREVIEW_REJECTED: 补卡时间不合法',
  'PascalCase 失败 envelope 应保留 code 与 message，不能继续读取 Data',
)

const legacySecondPage = normalizeAttendancePagedResult(
  [{ ScheduleGuid: 's1' }, { ScheduleGuid: 's2' }, { ScheduleGuid: 's3' }],
  normalizeAttendanceSchedule,
  { page: 2, pageSize: 2 },
)
assertEqual(legacySecondPage.items.length, 1, '旧数组响应应按请求页真实切片，不能伪装成第一页')
assertEqual(legacySecondPage.items[0]?.scheduleGuid, 's3', '旧数组响应第二页应返回正确项目')
assertEqual(legacySecondPage.total, 3, '旧数组响应应保留真实总数')
assertEqual(legacySecondPage.page, 2, '旧数组响应应保留请求页码')
assertEqual(legacySecondPage.pageSize, 2, '旧数组响应应保留请求 pageSize')
assertEqual(legacySecondPage.totalPages, 2, '旧数组响应应计算真实总页数')

const serverPage = normalizeAttendancePagedResult(
  { Data: { Items: [{ ScheduleGuid: 's2' }], Total: 3, Page: 2, PageSize: 1, TotalPages: 3 } },
  normalizeAttendanceSchedule,
  { page: 9, pageSize: 99 },
)
assertEqual(serverPage.page, 2, '分页对象应以服务端 page 为权威')
assertEqual(serverPage.pageSize, 1, '分页对象应以服务端 pageSize 为权威')
assertEqual(serverPage.total, 3, '分页对象应以服务端 total 为权威')
assertEqual(serverPage.totalPages, 3, '分页对象应保留服务端 totalPages')

console.log('scheduleAttendanceService.test.ts: ok')
