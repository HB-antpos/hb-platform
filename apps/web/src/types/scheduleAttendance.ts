import type { PagedResult } from './api'
import type {
  AttendanceApprovalLeaveDetailDto,
  AttendanceApprovalPublicHolidayDto,
} from './attendancePayroll'

export type AttendanceScheduleStatus = 'Draft' | 'Active' | 'Cancelled'
export type AttendanceAvailabilityStatus = 'Active' | 'Cancelled'
export type AttendancePunchType = 'ClockIn' | 'ClockOut'
export type AttendancePunchStatus =
  | 'Normal'
  | 'Late'
  | 'EarlyLeave'
  | 'NoSchedule'
  | 'Duplicate'
  | 'PendingApproval'
  | 'Approved'
  | 'Rejected'
export type AttendanceApprovalSourceType = 'Punch' | 'Leave' | 'PunchAdjustment' | 'Overtime' | 'MissingClockOut' | 'MealBreak' | 'PublicHolidayPay'
export type AttendanceReviewStatus = 'Pending' | 'Approved' | 'Rejected' | 'Cancelled'
export type AttendanceScheduleState = 'NotStarted' | 'InProgress' | 'Completed' | 'MissingClockOut' | string
export type AttendanceSegmentStatus = 'NotStarted' | 'Open' | 'Completed' | 'MissingClockOut' | string
export type AttendanceOvertimeApprovalStatus = 'NotRequired' | 'Pending' | 'Approved' | 'Rejected' | string
export type AttendancePunchAdjustmentStatus = 'Pending' | 'Applied' | 'Approved' | 'Rejected' | 'Cancelled' | string
export type AttendanceHolidayBusinessStatus = 'Open' | 'Closed' | 'Partial'
export type AttendanceLeaveType = 'AnnualLeave' | 'SickLeave' | 'PublicHoliday'

export interface AttendanceQuery {
  page?: number
  pageSize?: number
  storeCode?: string
  userGuid?: string
  weekStartDate?: string
  fromDate?: string
  toDate?: string
  storeTimeZone?: string
  status?: string
  keyword?: string
  offset?: number
}

export interface AttendanceScheduleDto {
  scheduleGuid: string
  storeCode: string
  storeName?: string
  userGuid: string
  userName?: string
  workDate: string
  startTime: string
  endTime: string
  status: AttendanceScheduleStatus
  remark?: string
  createdAt?: string
  updatedAt?: string
  scheduleState?: AttendanceScheduleState
  segmentLimit?: number
  completedSegmentCount?: number
  workedMinutes?: number
  breakMinutes?: number
  hasOpenSegment?: boolean
  hasMissingClockOut?: boolean
  earlyOvertimeMinutes?: number
  lateOvertimeMinutes?: number
  candidateOvertimeMinutes?: number
  approvedOvertimeMinutes?: number
  overtimeApprovalStatus?: AttendanceOvertimeApprovalStatus
  lateMinutes?: number
  earlyLeaveMinutes?: number
  crossStoreMissingClockOutStoreCode?: string
  segments?: AttendanceScheduleSegmentDto[]
  /** 店长手动指定的用餐次数（0–3）；缺省 / null＝按班次时长自动。 */
  mealBreakCount?: number | null
  /** 排班有效用餐次数（指定值优先，否则按时长默认）；旧后端不返回。 */
  effectiveMealBreakCount?: number
  /** 计薪时扣除的用餐分钟（是否休息过都按排班扣）。 */
  mealDeductionMinutes?: number
  /** 员工声明没休息且已批准、加回工时的分钟。 */
  approvedMealAddBackMinutes?: number
  /** 员工声明没休息、等待审核的分钟（未计入计薪工时）。 */
  pendingMealAddBackMinutes?: number
  /** 计薪工时 = 已闭合班段工时 − 用餐扣除 + 已批准加回；workedMinutes 含义不变。 */
  paidMinutes?: number
}

/** 下班时的用餐声明：员工声明没休息时生成 MealBreak 审批。 */
export interface AttendanceMealClaimDto {
  claimGuid: string
  expectedCount: number
  recordedCount: number
  missingCount: number
  notTakenCount: number
  claimedMinutes: number
  approvedMinutes?: number
  status: string
  reason?: string
}

export interface AttendanceScheduleSegmentDto {
  segmentIndex: number
  clockIn?: string | AttendancePunchDto
  clockOut?: string | AttendancePunchDto
  durationMinutes?: number
  status?: AttendanceSegmentStatus
}

export interface SaveAttendanceSchedulePayload {
  storeCode: string
  userGuid: string
  workDate: string
  startTime: string
  endTime: string
  status?: AttendanceScheduleStatus
  remark?: string
  /** 用餐次数（0–3）；新建不传＝自动，更新不传＝后端保持原值。 */
  mealBreakCount?: number
  /** 仅更新用：true＝恢复按时长自动（后端把次数写回 null）。 */
  resetMealBreakCount?: boolean
}

export interface AttendanceAvailabilityDto {
  availabilityGuid: string
  storeCode: string
  storeName?: string
  userGuid: string
  userName?: string
  weekStartDate: string
  availableDate: string
  startTime: string
  endTime: string
  status: AttendanceAvailabilityStatus
  remark?: string
  createdAt?: string
  /** true＝员工标记的不能上班时段，false/缺省＝可上班 */
  isUnavailable?: boolean
}

export interface AttendancePunchDto {
  punchGuid: string
  scheduleGuid?: string
  storeCode: string
  storeName?: string
  userGuid: string
  userName?: string
  workDate: string
  storeTimeZone?: string
  punchType: AttendancePunchType
  punchTimeUtc?: string
  punchTimeLocal?: string
  status: AttendancePunchStatus
  deviceId?: string
  source?: string
  remark?: string
  locationLatitude?: number
  locationLongitude?: number
  locationAccuracy?: number
  locationPermissionStatus?: string
  locationCapturedAtUtc?: string
  createdAt?: string
  segmentIndex?: number
  segmentStatus?: AttendanceSegmentStatus
  isBreakBoundary?: boolean
  supersedesPunchGuid?: string
  /** 门店时区 00:00 系统补的自动下班卡（该时段不计薪）。 */
  isSystemAutoClockOut?: boolean
  adjustmentGuid?: string
}

export interface AttendanceLocationSampleDto {
  sampleGuid: string
  userGuid: string
  storeCode?: string
  hardwareId?: string
  systemDeviceNumber?: string
  deviceSystem?: string
  eventType: string
  locationLatitude: number
  locationLongitude: number
  locationAccuracy?: number
  locationPermissionStatus?: string
  locationCapturedAtUtc: string
  createdAt?: string
}

export interface AttendanceApprovalDto {
  approvalGuid: string
  sourceType: AttendanceApprovalSourceType
  sourceGuid: string
  storeCode: string
  storeName?: string
  applicantUserGuid: string
  applicantName?: string
  reviewerUserGuid?: string
  reviewerName?: string
  workDate?: string
  title: string
  detail?: string
  reviewStatus: AttendanceReviewStatus
  reviewRemark?: string
  reviewedAt?: string
  createdAt?: string
  candidateOvertimeMinutes?: number
  approvedOvertimeMinutes?: number
  adjustment?: AttendanceAdjustmentDetailDto
  /** sourceType 为 MealBreak 时的用餐声明。 */
  mealClaim?: AttendanceMealClaimDto
  /** 审核人与申请人是同一人（持 Attendance.Approval.ReviewSelf 审本人申请），审批记录可按 isSelfReview 筛选。 */
  isSelfReview?: boolean
  /** 请假（含带薪病假、年假、年假发放）的类型、区间、带薪工时与医嘱。 */
  leave?: AttendanceApprovalLeaveDetailDto
  /** sourceType 为 PublicHolidayPay 时的公假与规则命中次数。 */
  publicHolidayPay?: AttendanceApprovalPublicHolidayDto
}

export interface ReviewAttendanceApprovalPayload {
  reviewRemark?: string
  approvedOvertimeMinutes?: number
  /** 带薪公假批准时由店长确认的工时（null 用建议工时）。 */
  approvedPaidHours?: number
}

export interface AttendanceAdjustmentDetailDto {
  adjustmentGuid?: string
  originalPunchGuid?: string
  punchType?: AttendancePunchType
  originalPunchTimeLocal?: string
  requestedPunchTimeLocal?: string
  effectivePunchTimeLocal?: string
  reason?: string
  status?: AttendancePunchAdjustmentStatus
  isDirectAdjustment?: boolean
  requestedByUserGuid?: string
  reviewedByUserGuid?: string
  reviewedAt?: string
}

export interface AttendancePunchAdjustmentDto extends AttendanceAdjustmentDetailDto {
  adjustmentGuid: string
  storeCode: string
  userGuid?: string
  scheduleGuid?: string
  punchType: AttendancePunchType
  requestedPunchTimeLocal: string
  reason: string
  status: AttendancePunchAdjustmentStatus
  createdAt?: string
  requestedPunchTimeUtc?: string
  appliedPunchGuid?: string
  isManagerSelfDirect?: boolean
  beforeWorkedMinutes?: number
  afterWorkedMinutes?: number
  beforeCandidateOvertimeMinutes?: number
  afterCandidateOvertimeMinutes?: number
  exceptionChanges?: string[]
}

export interface AttendancePunchAdjustmentPreviewDto {
  isValid: boolean
  validationErrorCode?: string
  validationMessage?: string
  existingSession?: AttendanceWorkSessionDto
  proposedSession?: AttendanceWorkSessionDto
  workedMinutesDelta: number
  candidateOvertimeMinutesDelta: number
  wouldAutoApprove: boolean
  previewRevision?: string
}

export interface AttendanceWorkSessionDto {
  scheduleState?: AttendanceScheduleState
  segmentLimit?: number
  completedSegmentCount?: number
  workedMinutes: number
  breakMinutes: number
  hasOpenSegment?: boolean
  hasMissingClockOut?: boolean
  earlyOvertimeMinutes?: number
  lateOvertimeMinutes?: number
  candidateOvertimeMinutes: number
  segments?: AttendanceScheduleSegmentDto[]
}

export interface SaveAttendancePunchAdjustmentPayload {
  storeCode: string
  scheduleGuid?: string
  originalPunchGuid?: string
  punchType: AttendancePunchType
  requestedPunchTimeLocal: string
  reason: string
  previewRevision?: string
}

export interface AttendanceStoreHolidayDto {
  holidayGuid: string
  storeCode: string
  storeName?: string
  holidayDate: string
  holidayName: string
  businessStatus: AttendanceHolidayBusinessStatus
  openTime?: string
  closeTime?: string
  isPaidHoliday?: boolean
  remark?: string
  createdAt?: string
  updatedAt?: string
}

export interface SaveAttendanceHolidayPayload {
  storeCode: string
  holidayDate: string
  holidayName: string
  businessStatus: AttendanceHolidayBusinessStatus
  openTime?: string
  closeTime?: string
  isPaidHoliday?: boolean
  remark?: string
}

export interface BatchUpsertAttendanceHolidayPayload {
  storeCodes: string[]
  holidayDate: string
  holidayName: string
  businessStatus: AttendanceHolidayBusinessStatus
  openTime?: string
  closeTime?: string
  isPaidHoliday?: boolean
  remark?: string
}

export interface BatchUpsertAttendanceHolidayResult {
  createdCount: number
  updatedCount: number
  items: AttendanceStoreHolidayDto[]
}

export interface SyncAttendanceHolidayPayload {
  storeCode: string
  daysAhead?: number
}

export interface SyncAttendanceHolidayResult {
  storeCode?: string
  jurisdiction?: 'NSW' | 'QLD'
  fromDate: string
  toDate: string
  syncedCount: number
  createdCount: number
  updatedCount: number
  skippedCount: number
  holidays: AttendanceStoreHolidayDto[]
  skippedStores?: string[]
  syncedAt?: string
}

export interface AttendanceSettingsDto {
  lateGraceMinutes: number
  earlyLeaveGraceMinutes: number
  allowNoSchedulePunch: boolean
  requireApprovalForLate: boolean
  requireApprovalForEarlyLeave: boolean
  requireApprovalForNoSchedule: boolean
  updatedAt?: string
  updatedBy?: string
  overtimeMinimumMinutes?: number
  requireOvertimeApproval?: boolean
  allowManagerDirectOwnAdjustment?: boolean
  // ===== 规格第 1 节新增（卡 B）；旧后端不返回 =====
  /** 工时统计默认周期：1 或 2 周。 */
  defaultPeriodWeeks?: 1 | 2
  /** 双周起始日（与发薪周期对齐），YYYY-MM-DD。 */
  fortnightAnchorDate?: string
  publicHolidayLookbackWeeks?: number
  publicHolidayMinCount?: number
  sickLeaveSubmitDays?: number
  annualLeaveLeadWeeks?: number
  annualLeaveMaxWeeks?: number
  annualCashOutLeadDays?: number
  annualCashOutMaxWeeks?: number
  birthdayRemindDays?: number
  employeeSegmentLimit?: number
  managerSegmentLimit?: number
  autoClockOutAtMidnight?: boolean
}

export type SaveAttendanceSettingsPayload = Pick<
  AttendanceSettingsDto,
  | 'lateGraceMinutes'
  | 'earlyLeaveGraceMinutes'
  | 'allowNoSchedulePunch'
  | 'requireApprovalForLate'
  | 'requireApprovalForEarlyLeave'
  | 'requireApprovalForNoSchedule'
> & Partial<Pick<
  AttendanceSettingsDto,
  | 'overtimeMinimumMinutes'
  | 'requireOvertimeApproval'
  | 'allowManagerDirectOwnAdjustment'
  | 'defaultPeriodWeeks'
  | 'fortnightAnchorDate'
  | 'publicHolidayLookbackWeeks'
  | 'publicHolidayMinCount'
  | 'sickLeaveSubmitDays'
  | 'annualLeaveLeadWeeks'
  | 'annualLeaveMaxWeeks'
  | 'annualCashOutLeadDays'
  | 'annualCashOutMaxWeeks'
  | 'birthdayRemindDays'
  | 'employeeSegmentLimit'
  | 'managerSegmentLimit'
  | 'autoClockOutAtMidnight'
>>

export type AttendancePagedResult<T> = PagedResult<T>
