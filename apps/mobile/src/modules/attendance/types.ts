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
}

export interface AttendancePunchMutationResult extends AttendancePunch {
  storeCode: string;
  workDate: string;
  punchType: "ClockIn" | "ClockOut";
  serverTimeUtc: string;
  storeTimeZone: string;
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
}

export interface AttendanceAvailabilityPayload {
  storeCode?: string;
  workDate: string;
  startTime: string;
  endTime: string;
  note?: string;
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
  sourceType: "Punch" | "Leave" | "PunchAdjustment" | "Overtime" | "MissingClockOut" | string;
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
