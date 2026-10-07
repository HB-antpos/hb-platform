export type AttendancePunchType = "ClockIn" | "ClockOut";

export type AttendanceVerificationDisplayStatus =
  | "available"
  | "permissionDenied"
  | "unavailable"
  | "unknown";

export type AttendanceLocationPermissionStatus =
  | "granted"
  | "denied"
  | "unavailable"
  | "unknown";

export type AttendanceNetworkVerificationStatus =
  | "online"
  | "offline"
  | "unknown";

export type AttendanceVerificationReason =
  | "captured"
  | "dependencyMissing"
  | "permissionDenied"
  | "networkUnreachable"
  | "timeout"
  | "unknown";

export type AttendancePunchStatus =
  | "Normal"
  | "Late"
  | "EarlyLeave"
  | "NoSchedule"
  | "Duplicate"
  | "PendingApproval"
  | "Approved"
  | "Rejected"
  | string;

export type AttendanceAvailabilityStatus = "Submitted" | "Cancelled" | string;

export type AttendanceLeaveType = "AnnualLeave" | "SickLeave" | "PublicHoliday" | string;

export type AttendanceApprovalStatus = "Pending" | "Approved" | "Rejected" | string;

export type AttendanceScheduleStatus = "Draft" | "Active" | "Cancelled" | string;

export type AttendanceHolidayBusinessStatus = "Open" | "Closed" | "Partial" | string;
export type AttendanceHolidayJurisdiction = "NSW" | "QLD";

export interface MinorEmploymentComplianceFinding {
  ruleId: string;
  severity: "warning" | "review" | "incomplete" | string;
  message: string;
  workDate?: string;
  actualMinutes?: number;
  limitMinutes?: number;
  ruleCategory?: string;
  sourceUrl?: string;
}

export interface MinorEmploymentComplianceEvaluation {
  findings: MinorEmploymentComplianceFinding[];
  hasFindings: boolean;
}

export interface AttendanceSchedule {
  scheduleGuid: string;
  storeCode: string;
  storeName?: string;
  userGuid: string;
  employeeName?: string;
  workDate: string;
  startTime: string;
  endTime: string;
  status: AttendanceScheduleStatus;
  remark?: string;
  /** 店长指定的用餐次数（每次 30 分钟，0 = 不扣用餐）；为空表示按班次时长默认。 */
  mealBreakCount?: number | null;
  /** 后端算好的有效用餐次数（指定值优先，否则按时长默认）；旧后端不返回。 */
  effectiveMealBreakCount?: number;
  /** 计薪时扣除的用餐分钟（是否休息过都按排班扣）。 */
  mealDeductionMinutes?: number;
  /** 声明没休息且店长已批准、加回工时的分钟。 */
  approvedMealAddBackMinutes?: number;
  /** 声明没休息、等待店长审核的分钟（只展示，未计入计薪工时）。 */
  pendingMealAddBackMinutes?: number;
  /** 计薪工时 = 已闭合班段工时 − 用餐扣除 + 已批准加回。 */
  paidMinutes?: number;
  /** 当班用餐状态：仅「我的今日」且排班有用餐时返回。 */
  meal?: AttendanceMealState;
  minorCompliance?: MinorEmploymentComplianceEvaluation;
  isMine: boolean;
  holidayName?: string;
  holidayBusinessStatus?: string;
  scheduleState?: string;
  segmentLimit?: number;
  completedSegmentCount?: number;
  workedMinutes?: number;
  breakMinutes?: number;
  hasOpenSegment?: boolean;
  hasMissingClockOut?: boolean;
  earlyOvertimeMinutes?: number;
  lateOvertimeMinutes?: number;
  candidateOvertimeMinutes?: number;
  approvedOvertimeMinutes?: number;
  overtimeApprovalStatus?: string;
  /** 当天已批准请假的类型；有值表示员工请假，不计工时、不算缺卡。 */
  leaveType?: string;
  leaveGuid?: string;
  segments?: AttendancePunchSegment[];
}

export interface AttendancePunch {
  punchGuid: string;
  scheduleGuid?: string;
  storeCode?: string;
  storeName?: string;
  workDate: string;
  punchType: AttendancePunchType | string;
  punchTimeUtc?: string;
  punchTimeLocal?: string;
  status: AttendancePunchStatus;
  statusReason?: string;
  locationLatitude?: number;
  locationLongitude?: number;
  locationAccuracy?: number;
  locationPermissionStatus?: AttendanceLocationPermissionStatus | string;
  locationCapturedAtUtc?: string;
  userGuid?: string;
  employeeName?: string;
  posDeviceCode?: string;
  serverTimeUtc?: string;
  effectivePunchTime?: string;
  segmentIndex?: number;
  segmentStatus?: string;
  isBreakBoundary?: boolean;
  supersedesPunchGuid?: string;
  adjustmentGuid?: string;
  earlyArrivalMinutes?: number;
  lateMinutes?: number;
  earlyLeaveMinutes?: number;
  lateDepartureMinutes?: number;
  minorCompliance?: MinorEmploymentComplianceEvaluation;
  /** 打卡响应里的用餐声明：最后一次下班带了声明才有；声明没休息时 status 为 Pending。 */
  mealClaim?: AttendanceMealClaim;
}

export interface AttendancePunchMutationResult extends AttendancePunch {
  storeCode: string;
  workDate: string;
  punchType: "ClockIn" | "ClockOut";
  serverTimeUtc: string;
  storeTimeZone: string;
}

export interface AttendanceMealBreakRecord {
  breakGuid: string;
  startUtc: string;
  endUtc?: string;
}

/** 某个排班此刻的用餐状态，由后端按排班与实际工时计算。 */
export interface AttendanceMealState {
  scheduleGuid: string;
  storeCode: string;
  effectiveMealBreakCount: number;
  /** 已有的休息：满 10 分钟的休息记录 + 班段间满 30 分钟的空档 + 此前已声明的次数。 */
  handledCount: number;
  requiredCount: number;
  /** 此刻下班是否为最后一次下班；只有最后一次下班才检查用餐。 */
  clockOutWouldBeFinal: boolean;
  /** 此刻下班缺几次休息；大于 0 时扫码前先问员工。 */
  missingCountIfClockOutNow: number;
  hasOpenBreak: boolean;
  openBreakStartedAtUtc?: string;
  /** 连续工作满 4 小时的提醒时间；为空表示无需提醒。 */
  nextReminderAtUtc?: string;
  serverTimeUtc?: string;
  breaks: AttendanceMealBreakRecord[];
}

/** 下班时对「缺休息」的声明：notTakenCount 为没休息的次数，0 表示都休息了。 */
export interface AttendanceMealDeclaration {
  notTakenCount: number;
  reason?: string;
}

export type AttendanceMealClaimStatus = "None" | "Pending" | "Approved" | "Rejected" | "Cancelled" | string;

export interface AttendanceMealClaim {
  claimGuid: string;
  scheduleGuid?: string;
  workDate?: string;
  expectedCount: number;
  recordedCount: number;
  missingCount: number;
  notTakenCount: number;
  claimedMinutes: number;
  approvedMinutes?: number;
  status: AttendanceMealClaimStatus;
  reason?: string;
}

export interface AttendancePunchSegment {
  segmentIndex: number;
  segmentNumber: number;
  clockIn?: AttendancePunch;
  clockOut?: AttendancePunch;
  durationMinutes?: number;
  workedMinutes?: number;
  status?: string;
  adjustmentStatus?: string;
}

export interface AttendanceScheduleSession extends AttendanceSchedule {
  segments: AttendancePunchSegment[];
  overtimeRawMinutes?: number;
  overtimeCandidateMinutes?: number;
  adjustmentStatus?: string;
}

export interface AttendanceStorePunchState {
  storeCode: string;
  storeName?: string;
  state?: string;
  hasOpenSegment?: boolean;
  hasMissingClockOut?: boolean;
  scheduleGuid?: string;
  relatedReminder?: string;
  scheduleSessions: AttendanceScheduleSession[];
}

export interface AttendancePunchAdjustmentPayload {
  storeCode: string;
  scheduleGuid?: string;
  originalPunchGuid?: string;
  punchType: AttendancePunchType;
  requestedPunchTimeLocal: string;
  requestedPunchTimeUtc?: string;
  reason: string;
  previewRevision?: string;
}

/** 店长代员工补录/修改打卡：在本人补卡字段基础上指定员工。 */
export interface AttendanceManagedPunchAdjustmentPayload extends AttendancePunchAdjustmentPayload {
  userGuid: string;
}

export interface AttendancePunchAdjustment {
  adjustmentGuid: string;
  storeCode: string;
  scheduleGuid?: string;
  originalPunchGuid?: string;
  originalPunchTimeLocal?: string;
  punchType: AttendancePunchType;
  requestedPunchTimeLocal: string;
  requestedPunchTimeUtc?: string;
  effectivePunchTimeLocal?: string;
  reason: string;
  status: AttendanceApprovalStatus;
  isDirectAdjustment?: boolean;
  submittedAt?: string;
  reviewedAt?: string;
  reviewedByName?: string;
  reviewRemark?: string;
}

export interface AttendanceAdjustmentPreview {
  isValid: boolean;
  validationErrorCode?: string;
  validationMessage?: string;
  existingSession?: AttendanceScheduleSession;
  proposedSession?: AttendanceScheduleSession;
  workedMinutesDelta: number;
  candidateOvertimeMinutesDelta: number;
  wouldAutoApprove: boolean;
  previewRevision?: string;
}

export interface AttendancePunchVerificationPayload {
  locationLatitude?: number;
  locationLongitude?: number;
  locationAccuracy?: number;
  locationPermissionStatus?: AttendanceLocationPermissionStatus | string;
  locationCapturedAtUtc?: string;
  networkVerificationStatus?: AttendanceNetworkVerificationStatus | string;
}

export interface AttendancePunchPayload {
  qrToken: string;
  punchAuthorizationToken?: string;
  locationLatitude?: number;
  locationLongitude?: number;
  locationAccuracy?: number;
  locationCapturedAtUtc?: string;
  /** 扫码前员工对缺休息的回答；旧后端忽略此字段。 */
  mealDeclaration?: AttendanceMealDeclaration;
}

export interface AttendanceQrResolveResult {
  storeCode: string;
  deviceCode: string;
  expiresAtUtc: string;
  punchAuthorizationToken?: string;
  punchAuthorizationExpiresAtUtc?: string;
  storeName?: string;
}

export interface AttendanceLocationSamplePayload {
  storeCode: string;
  hardwareId?: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  eventType?: string;
  locationLatitude: number;
  locationLongitude: number;
  locationAccuracy?: number;
  locationPermissionStatus?: AttendanceLocationPermissionStatus | string;
  locationCapturedAtUtc?: string;
}

export interface AttendanceVerificationFieldState {
  status: AttendanceVerificationDisplayStatus;
  reason: AttendanceVerificationReason;
}

export interface AttendanceLocationVerificationState
  extends AttendanceVerificationFieldState {
  permissionStatus: AttendanceLocationPermissionStatus;
  latitude?: number;
  longitude?: number;
  accuracy?: number;
}

export interface AttendanceNetworkVerificationState
  extends AttendanceVerificationFieldState {
  verificationStatus: AttendanceNetworkVerificationStatus;
}

export interface AttendancePunchVerificationState {
  checkedAt?: string;
  location: AttendanceLocationVerificationState;
  network: AttendanceNetworkVerificationState;
  payload: AttendancePunchVerificationPayload;
}

export interface AttendanceToday {
  workDate: string;
  minorCompliance?: MinorEmploymentComplianceEvaluation;
  storeTimeZone?: string;
  holidayName?: string;
  holidayBusinessStatus?: string;
  holidays?: AttendanceStoreHoliday[];
  schedules: AttendanceSchedule[];
  punches: AttendancePunch[];
  nextPunchType: AttendancePunchType;
  canClockIn: boolean;
  canClockOut: boolean;
  storePunchStates: AttendanceStorePunchState[];
  scheduleSessions: AttendanceScheduleSession[];
  relatedStoreReminders: string[];
  canRequestAdjustment?: boolean;
}

export interface AttendanceWeekDay {
  workDate: string;
  dayOfWeek: number;
  holidayName?: string;
  holidayBusinessStatus?: string;
  schedules: AttendanceSchedule[];
}

export interface AttendanceWeek {
  weekStart: string;
  weekEnd: string;
  days: AttendanceWeekDay[];
}

export interface AttendanceAvailability {
  availabilityGuid: string;
  userGuid?: string;
  storeCode?: string;
  storeName?: string;
  workDate: string;
  startTime: string;
  endTime: string;
  note?: string;
  status: AttendanceAvailabilityStatus;
  /** true＝不能上班（员工标记的不可排班时段），false/缺省＝可上班。 */
  isUnavailable?: boolean;
}

export interface AttendanceAvailabilityPayload {
  storeCode?: string;
  workDate: string;
  startTime: string;
  endTime: string;
  note?: string;
  /** true＝不能上班；缺省按可上班提交。 */
  isUnavailable?: boolean;
}

export interface AttendanceAvailabilityBatchPayload
  extends Omit<AttendanceAvailabilityPayload, "workDate"> {
  workDates: string[];
}

export interface AttendanceStoreHoliday {
  holidayGuid: string;
  storeCode: string;
  storeName?: string;
  holidayDate: string;
  holidayName: string;
  businessStatus: AttendanceHolidayBusinessStatus;
  openTime?: string;
  closeTime?: string;
  isPaidHoliday: boolean;
  remark?: string;
}

export interface AttendanceStoreHolidayPayload {
  storeCode: string;
  holidayDate: string;
  holidayName: string;
  businessStatus: AttendanceHolidayBusinessStatus;
  openTime?: string;
  closeTime?: string;
  isPaidHoliday: boolean;
  remark?: string;
}

export interface AttendanceHolidayQueryParams {
  storeCode?: string;
  fromDate?: string;
  toDate?: string;
}

export interface AttendanceHolidaySyncPayload {
  storeCode?: string;
  postcode?: string;
  jurisdiction?: AttendanceHolidayJurisdiction;
  stateCode?: AttendanceHolidayJurisdiction;
  fromDate?: string;
  toDate?: string;
  daysAhead?: number;
}

export interface AttendanceHolidaySyncResult {
  storeCode?: string;
  jurisdiction?: AttendanceHolidayJurisdiction;
  fromDate: string;
  toDate: string;
  syncedCount: number;
  createdCount: number;
  updatedCount: number;
  skippedCount: number;
  holidays: AttendanceStoreHoliday[];
  skippedStores?: string[];
  syncedAt?: string;
}

export interface AttendanceLeaveRequest {
  leaveGuid: string;
  storeCode?: string;
  storeName?: string;
  leaveType: AttendanceLeaveType;
  startDate: string;
  endDate: string;
  startTime?: string;
  endTime?: string;
  reason?: string;
  attachmentUrl?: string;
  status: AttendanceApprovalStatus;
  submittedAt?: string;
  reviewedAt?: string;
  reviewedByName?: string;
  reviewRemark?: string;
}

export interface AttendanceLeaveRequestPayload {
  userGuid?: string;
  storeCode?: string;
  leaveType: AttendanceLeaveType;
  startDate: string;
  endDate: string;
  startTime?: string;
  endTime?: string;
  reason?: string;
  attachmentUrl?: string;
}

export interface AttendanceDirectUploadRequest {
  fileName: string;
  contentType: string;
  fileSize: number;
  objectKey?: string | null;
}

export interface AttendanceDirectUploadSignature {
  url: string;
  objectKey: string;
  headers: Record<string, string>;
}

export interface AttendanceLeaveAttachmentUploadResult {
  objectKey: string;
  downloadUrl: string;
}

export interface AttendanceApproval {
  approvalGuid: string;
  sourceGuid: string;
  sourceType: "Punch" | "Leave" | "PunchAdjustment" | "Overtime" | "MissingClockOut" | "MealBreak" | string;
  employeeName?: string;
  storeCode?: string;
  storeName?: string;
  workDate?: string;
  title: string;
  detail?: string;
  status: AttendanceApprovalStatus;
  submittedAt?: string;
  candidateOvertimeMinutes?: number;
  approvedOvertimeMinutes?: number;
  adjustment?: AttendancePunchAdjustment;
  /** sourceType 为 MealBreak 时的用餐声明。 */
  mealClaim?: AttendanceMealClaim;
}

export interface AttendanceApprovalPayload {
  approvalGuid: string;
  remark?: string;
  approvedOvertimeMinutes?: number;
}

export interface AttendanceScheduleWeekParams {
  storeCode?: string;
  weekStartDate?: string;
}

export interface AttendanceSchedulePayload {
  storeCode: string;
  userGuid: string;
  workDate: string;
  startTime: string;
  endTime: string;
  status?: AttendanceScheduleStatus;
  remark?: string;
  /** 为空按时长默认用餐次数。 */
  mealBreakCount?: number | null;
}

export interface AttendanceScheduleUpdatePayload {
  workDate: string;
  startTime: string;
  endTime: string;
  status?: AttendanceScheduleStatus;
  remark?: string;
  /** 有值即覆盖用餐次数；为空且 resetMealBreakCount 为 true 时恢复按时长默认。 */
  mealBreakCount?: number | null;
  /** 恢复按时长默认（后端置空）；不传则保持原有设置，兼容旧客户端。 */
  resetMealBreakCount?: boolean;
}

export interface AttendancePublishWeekPayload {
  storeCode: string;
  weekStartDate: string;
}
