import {
  createAttendanceLocationRequest,
  getAttendanceLocationRequests,
  normalizeAttendanceLocationRequest,
  normalizeAttendanceEmployee,
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


// 可排班员工（attendance/employees）：店长不在员工管理列表里，只有这个接口能给出他们的昵称 / 名 / 姓
const manager = normalizeAttendanceEmployee({
  UserGuid: 'manager-1',
  Username: 'terry',
  FullName: 'Zhan Chen',
  GivenName: 'Zhan',
  MiddleName: '',
  FamilyName: 'Chen',
  Nickname: 'Terry',
  IsStoreManager: true,
  ManagesStore: true,
  Age: 31,
})
assertEqual(manager.nickname, 'Terry', '应兼容 PascalCase 昵称字段')
assertEqual(manager.givenName, 'Zhan', '应兼容 PascalCase 名字段')
assertEqual(manager.familyName, 'Chen', '应兼容 PascalCase 姓字段')
assertEqual(manager.middleName, '', '空串原样保留，由取名逻辑判空')
assertEqual(manager.isStoreManager, true, '店长标记要保留')
const camelEmployee = normalizeAttendanceEmployee({ userGuid: 'staff-1', username: 's1', fullName: 'Cody Johnson', nickname: 'Cody', givenName: 'Cody', familyName: 'Johnson', age: 19 })
assertEqual(camelEmployee.nickname, 'Cody', '应兼容 camelCase 昵称字段')
assertEqual(camelEmployee.age, 19, '年龄照旧映射')
// 旧后端不返回姓名拆分字段：缺省统一为 null，不能是 undefined 或报错
const legacyEmployee = normalizeAttendanceEmployee({ userGuid: 'old-1', username: 'old', fullName: 'Old Staff' })
assertEqual(legacyEmployee.nickname, null, '旧后端缺昵称字段时为 null')
assertEqual(legacyEmployee.givenName, null, '旧后端缺名字段时为 null')
assertEqual(legacyEmployee.age, null, '没有年龄时为 null')

// ───────── 位置请求（服务端要求员工上报位置）─────────
const requestWire = {
  requestGuid: 'req-1',
  storeCode: 'S001',
  storeName: 'Orion',
  targetUserGuid: 'user-1',
  targetUserName: 'Cody',
  requestedByUserGuid: 'manager-1',
  requestedByName: 'Sean',
  status: 'Responded',
  statusReason: null,
  createdAtUtc: '2026-11-19T01:00:00',
  expiresAtUtc: '2026-11-19T01:15:00',
  respondedAtUtc: '2026-11-19T01:05:00',
  sampleGuid: 'sample-1',
  locationLatitude: -27.47,
  locationLongitude: 153.02,
  locationAccuracy: 12.5,
  locationCapturedAtUtc: '2026-11-19T01:04:59',
}
const normalizedRequest = normalizeAttendanceLocationRequest(requestWire)
assertEqual(normalizedRequest.requestGuid, 'req-1', '位置请求 GUID')
assertEqual(normalizedRequest.requestedByName, 'Sean', '请求人姓名')
assertEqual(normalizedRequest.statusReason, undefined, 'null 的取消原因收成 undefined')
assertEqual(normalizedRequest.locationLatitude, -27.47, '纬度原样保留')
assertEqual(normalizedRequest.locationAccuracy, 12.5, '精度原样保留')
assertEqual(normalizedRequest.locationCapturedAtUtc, '2026-11-19T01:04:59', '采集时间原样保留（无后缀 UTC 由展示层补 Z）')

const pascalRequest = normalizeAttendanceLocationRequest({
  RequestGuid: 'req-2',
  StoreCode: 'S002',
  TargetUserGuid: 'user-2',
  RequestedByUserGuid: 'manager-1',
  Status: 'Cancelled',
  StatusReason: 'ClockedOut',
  CreatedAtUtc: '2026-11-19T02:00:00',
  ExpiresAtUtc: '2026-11-19T02:15:00',
})
assertEqual(pascalRequest.status, 'Cancelled', '应兼容 PascalCase 状态')
assertEqual(pascalRequest.statusReason, 'ClockedOut', '应兼容 PascalCase 取消原因')
assertEqual(pascalRequest.locationLatitude, undefined, '未响应的请求没有坐标')

const dirtyRequest = normalizeAttendanceLocationRequest({
  requestGuid: 'req-3',
  locationLatitude: '-27.47',
  locationLongitude: Number.NaN,
  locationAccuracy: null,
})
assertEqual(dirtyRequest.locationLatitude, undefined, '字符串坐标不当成数字')
assertEqual(dirtyRequest.locationLongitude, undefined, 'NaN 坐标丢弃')
assertEqual(dirtyRequest.locationAccuracy, undefined, 'null 精度收成 undefined')
assertEqual(dirtyRequest.status, '', '缺失状态落成空串，由展示层走兜底')

{
  const originalFetch = globalThis.fetch
  const captured: Array<{ url: URL; init?: RequestInit }> = []
  let nextBody: unknown = {}
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    // 失败请求会异步上报中心日志，这些请求不计入断言。
    if (!url.pathname.startsWith('/api/react/v1/attendance')) {
      return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
    }
    captured.push({ url, init })
    return new Response(JSON.stringify(nextBody), { status: 200, headers: { 'content-type': 'application/json' } })
  }) as typeof fetch

  try {
    nextBody = { success: true, data: requestWire }
    const created = await createAttendanceLocationRequest({ storeCode: 'S001', targetUserGuid: 'user-1' })
    const createCall = captured[captured.length - 1]
    assertEqual(createCall.url.pathname, '/api/react/v1/attendance/location-requests', '创建请求路径')
    assertEqual(createCall.init?.method, 'POST', '创建请求是 POST')
    assertEqual(createCall.init?.body, JSON.stringify({ storeCode: 'S001', targetUserGuid: 'user-1' }), '请求体只带分店与目标员工')
    assertEqual(created.requestGuid, 'req-1', '创建结果应归一化')

    // 业务失败是 HTTP 200 + success=false：必须抛错，并带着错误码让页面映射友好文案
    nextBody = { success: false, errorCode: 'LOCATION_REQUEST_PENDING', message: '已有未响应的请求' }
    let businessError: unknown
    try {
      await createAttendanceLocationRequest({ storeCode: 'S001', targetUserGuid: 'user-1' })
    } catch (error) {
      businessError = error
    }
    assertEqual(businessError instanceof Error, true, '业务失败必须抛错，不能误报成功')
    assertEqual(
      (businessError as { payload?: { errorCode?: string } }).payload?.errorCode,
      'LOCATION_REQUEST_PENDING',
      '错误码要随异常带出',
    )

    nextBody = { success: true, data: [requestWire, { ...requestWire, requestGuid: 'req-9' }] }
    const listed = await getAttendanceLocationRequests({ storeCode: 'S001', targetUserGuid: 'user-1', limit: 20 })
    const listCall = captured[captured.length - 1]
    assertEqual(listCall.url.pathname, '/api/react/v1/attendance/location-requests', '列表路径')
    assertEqual(listCall.init?.method ?? 'GET', 'GET', '列表是 GET')
    assertEqual(listCall.url.search, '?storeCode=S001&targetUserGuid=user-1&limit=20', '列表查询参数')
    assertEqual(listed.length, 2, '列表逐条归一化')
    assertEqual(listed[1].requestGuid, 'req-9', '列表保持服务端顺序')

    nextBody = { success: true, data: null }
    assertEqual((await getAttendanceLocationRequests({ storeCode: 'S001' })).length, 0, '空数据兜底为空数组')
  } finally {
    globalThis.fetch = originalFetch
  }
}

console.log('scheduleAttendanceService.test.ts: ok')
