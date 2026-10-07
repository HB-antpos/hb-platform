import type { ApiResponse, PagedResult } from '../types/api'
import type {
  AttendanceApprovalDto,
  AttendanceAdjustmentDetailDto,
  AttendanceAvailabilityDto,
  AttendanceLocationSampleDto,
  AttendanceMealClaimDto,
  AttendancePagedResult,
  AttendancePunchDto,
  AttendancePunchAdjustmentDto,
  AttendancePunchAdjustmentPreviewDto,
  AttendanceQuery,
  AttendanceScheduleDto,
  AttendanceSettingsDto,
  AttendanceStoreHolidayDto,
  BatchUpsertAttendanceHolidayPayload,
  BatchUpsertAttendanceHolidayResult,
  ReviewAttendanceApprovalPayload,
  SaveAttendanceHolidayPayload,
  SaveAttendanceSchedulePayload,
  SaveAttendanceSettingsPayload,
  SaveAttendancePunchAdjustmentPayload,
  SyncAttendanceHolidayPayload,
  SyncAttendanceHolidayResult,
} from '../types/scheduleAttendance'
import request, { RequestError } from '../utils/request'

const API_BASE = '/api/react/v1/attendance'
const MY_PUNCH_ADJUSTMENTS_ENDPOINT = `${API_BASE}/my/punch-adjustments`

type UnknownRecord = Record<string, unknown>

function asRecord(value: unknown): UnknownRecord {
  return value && typeof value === 'object' ? value as UnknownRecord : {}
}

function readCompat<T>(source: UnknownRecord, camelKey: string, pascalKey: string): T | undefined {
  return (source[camelKey] ?? source[pascalKey]) as T | undefined
}

export function unwrapAttendanceApiData<T>(payload: unknown): T {
  const source = asRecord(payload)
  const successValues = [source.success, source.isSuccess, source.Success, source.IsSuccess]
  if (successValues.some((value) => value === false)) {
    const code = source.code ?? source.errorCode ?? source.Code ?? source.ErrorCode
    const message = source.message ?? source.Message ?? '请求失败'
    throw new RequestError(
      code ? `${String(code)}: ${String(message)}` : String(message),
      200,
      payload,
    )
  }
  if ('data' in source) return source.data as T
  if ('Data' in source) return source.Data as T
  return payload as T
}

function normalizeAttendanceSegment(value: unknown) {
  const source = asRecord(value)
  const clockIn = readCompat<unknown>(source, 'clockIn', 'ClockIn')
  const clockOut = readCompat<unknown>(source, 'clockOut', 'ClockOut')
  return {
    segmentIndex: readCompat<number>(source, 'segmentIndex', 'SegmentIndex') ?? 0,
    clockIn: clockIn && typeof clockIn === 'object' ? normalizeAttendancePunch(clockIn) : clockIn as string | undefined,
    clockOut: clockOut && typeof clockOut === 'object' ? normalizeAttendancePunch(clockOut) : clockOut as string | undefined,
    durationMinutes: readCompat<number>(source, 'durationMinutes', 'DurationMinutes'),
    status: readCompat<string>(source, 'status', 'Status'),
  }
}

export function normalizeAttendanceSchedule(value: unknown): AttendanceScheduleDto {
  const source = asRecord(value)
  const segments = readCompat<unknown[]>(source, 'segments', 'Segments')
  return {
    scheduleGuid: readCompat<string>(source, 'scheduleGuid', 'ScheduleGuid') ?? '',
    storeCode: readCompat<string>(source, 'storeCode', 'StoreCode') ?? '',
    storeName: readCompat<string>(source, 'storeName', 'StoreName'),
    userGuid: readCompat<string>(source, 'userGuid', 'UserGuid') ?? '',
    userName: readCompat<string>(source, 'userName', 'UserName')
      ?? readCompat<string>(source, 'employeeName', 'EmployeeName'),
    workDate: readCompat<string>(source, 'workDate', 'WorkDate') ?? '',
    startTime: readCompat<string>(source, 'startTime', 'StartTime') ?? '',
    endTime: readCompat<string>(source, 'endTime', 'EndTime') ?? '',
    status: readCompat<AttendanceScheduleDto['status']>(source, 'status', 'Status') ?? 'Draft',
    remark: readCompat<string>(source, 'remark', 'Remark'),
    createdAt: readCompat<string>(source, 'createdAt', 'CreatedAt'),
    updatedAt: readCompat<string>(source, 'updatedAt', 'UpdatedAt'),
    scheduleState: readCompat<string>(source, 'scheduleState', 'ScheduleState'),
    segmentLimit: readCompat<number>(source, 'segmentLimit', 'SegmentLimit'),
    completedSegmentCount: readCompat<number>(source, 'completedSegmentCount', 'CompletedSegmentCount'),
    workedMinutes: readCompat<number>(source, 'workedMinutes', 'WorkedMinutes'),
    breakMinutes: readCompat<number>(source, 'breakMinutes', 'BreakMinutes'),
    hasOpenSegment: readCompat<boolean>(source, 'hasOpenSegment', 'HasOpenSegment'),
    hasMissingClockOut: readCompat<boolean>(source, 'hasMissingClockOut', 'HasMissingClockOut'),
    earlyOvertimeMinutes: readCompat<number>(source, 'earlyOvertimeMinutes', 'EarlyOvertimeMinutes'),
    lateOvertimeMinutes: readCompat<number>(source, 'lateOvertimeMinutes', 'LateOvertimeMinutes'),
    candidateOvertimeMinutes: readCompat<number>(source, 'candidateOvertimeMinutes', 'CandidateOvertimeMinutes'),
    approvedOvertimeMinutes: readCompat<number>(source, 'approvedOvertimeMinutes', 'ApprovedOvertimeMinutes'),
    overtimeApprovalStatus: readCompat<string>(source, 'overtimeApprovalStatus', 'OvertimeApprovalStatus'),
    lateMinutes: readCompat<number>(source, 'lateMinutes', 'LateMinutes'),
    earlyLeaveMinutes: readCompat<number>(source, 'earlyLeaveMinutes', 'EarlyLeaveMinutes'),
    crossStoreMissingClockOutStoreCode: readCompat<string>(source, 'crossStoreMissingClockOutStoreCode', 'CrossStoreMissingClockOutStoreCode'),
    segments: Array.isArray(segments) ? segments.map(normalizeAttendanceSegment) : undefined,
    // 用餐扣除与计薪工时：旧后端不返回时都是 undefined，记录表不显示用餐列内容。
    effectiveMealBreakCount: readCompat<number>(source, 'effectiveMealBreakCount', 'EffectiveMealBreakCount'),
    mealDeductionMinutes: readCompat<number>(source, 'mealDeductionMinutes', 'MealDeductionMinutes'),
    approvedMealAddBackMinutes: readCompat<number>(source, 'approvedMealAddBackMinutes', 'ApprovedMealAddBackMinutes'),
    pendingMealAddBackMinutes: readCompat<number>(source, 'pendingMealAddBackMinutes', 'PendingMealAddBackMinutes'),
    paidMinutes: readCompat<number>(source, 'paidMinutes', 'PaidMinutes'),
  }
}

function normalizeAttendanceMealClaim(value: unknown): AttendanceMealClaimDto | undefined {
  const source = asRecord(value)
  const claimGuid = readCompat<string>(source, 'claimGuid', 'ClaimGuid')
  if (!claimGuid) return undefined
  return {
    claimGuid,
    expectedCount: readCompat<number>(source, 'expectedCount', 'ExpectedCount') ?? 0,
    recordedCount: readCompat<number>(source, 'recordedCount', 'RecordedCount') ?? 0,
    missingCount: readCompat<number>(source, 'missingCount', 'MissingCount') ?? 0,
    notTakenCount: readCompat<number>(source, 'notTakenCount', 'NotTakenCount') ?? 0,
    claimedMinutes: readCompat<number>(source, 'claimedMinutes', 'ClaimedMinutes') ?? 0,
    approvedMinutes: readCompat<number>(source, 'approvedMinutes', 'ApprovedMinutes'),
    status: readCompat<string>(source, 'status', 'Status') ?? 'None',
    reason: readCompat<string>(source, 'reason', 'Reason'),
  }
}

export function normalizeAttendancePunch(value: unknown): AttendancePunchDto {
  const source = asRecord(value)
  return {
    punchGuid: readCompat<string>(source, 'punchGuid', 'PunchGuid') ?? '',
    scheduleGuid: readCompat<string>(source, 'scheduleGuid', 'ScheduleGuid'),
    storeCode: readCompat<string>(source, 'storeCode', 'StoreCode') ?? '',
    storeName: readCompat<string>(source, 'storeName', 'StoreName'),
    userGuid: readCompat<string>(source, 'userGuid', 'UserGuid') ?? '',
    userName: readCompat<string>(source, 'userName', 'UserName')
      ?? readCompat<string>(source, 'employeeName', 'EmployeeName'),
    workDate: readCompat<string>(source, 'workDate', 'WorkDate') ?? '',
    storeTimeZone: readCompat<string>(source, 'storeTimeZone', 'StoreTimeZone'),
    punchType: readCompat<AttendancePunchDto['punchType']>(source, 'punchType', 'PunchType') ?? 'ClockIn',
    punchTimeUtc: readCompat<string>(source, 'punchTimeUtc', 'PunchTimeUtc'),
    punchTimeLocal: readCompat<string>(source, 'punchTimeLocal', 'PunchTimeLocal'),
    status: readCompat<AttendancePunchDto['status']>(source, 'status', 'Status') ?? 'Normal',
    deviceId: readCompat<string>(source, 'deviceId', 'DeviceId'),
    source: readCompat<string>(source, 'source', 'Source'),
    remark: readCompat<string>(source, 'remark', 'Remark'),
    locationLatitude: readCompat<number>(source, 'locationLatitude', 'LocationLatitude'),
    locationLongitude: readCompat<number>(source, 'locationLongitude', 'LocationLongitude'),
    locationAccuracy: readCompat<number>(source, 'locationAccuracy', 'LocationAccuracy'),
    locationPermissionStatus: readCompat<string>(source, 'locationPermissionStatus', 'LocationPermissionStatus'),
    locationCapturedAtUtc: readCompat<string>(source, 'locationCapturedAtUtc', 'LocationCapturedAtUtc'),
    createdAt: readCompat<string>(source, 'createdAt', 'CreatedAt'),
    segmentIndex: readCompat<number>(source, 'segmentIndex', 'SegmentIndex'),
    segmentStatus: readCompat<string>(source, 'segmentStatus', 'SegmentStatus'),
    isBreakBoundary: readCompat<boolean>(source, 'isBreakBoundary', 'IsBreakBoundary'),
    supersedesPunchGuid: readCompat<string>(source, 'supersedesPunchGuid', 'SupersedesPunchGuid'),
    adjustmentGuid: readCompat<string>(source, 'adjustmentGuid', 'AdjustmentGuid'),
  }
}

function normalizeAttendanceAdjustment(value: unknown): AttendanceAdjustmentDetailDto {
  const source = asRecord(value)
  return {
    adjustmentGuid: readCompat<string>(source, 'adjustmentGuid', 'AdjustmentGuid'),
    originalPunchGuid: readCompat<string>(source, 'originalPunchGuid', 'OriginalPunchGuid'),
    punchType: readCompat<AttendanceAdjustmentDetailDto['punchType']>(source, 'punchType', 'PunchType'),
    originalPunchTimeLocal: readCompat<string>(source, 'originalPunchTimeLocal', 'OriginalPunchTimeLocal'),
    requestedPunchTimeLocal: readCompat<string>(source, 'requestedPunchTimeLocal', 'RequestedPunchTimeLocal'),
    effectivePunchTimeLocal: readCompat<string>(source, 'effectivePunchTimeLocal', 'EffectivePunchTimeLocal'),
    reason: readCompat<string>(source, 'reason', 'Reason'),
    status: readCompat<string>(source, 'status', 'Status'),
    isDirectAdjustment: readCompat<boolean>(source, 'isDirectAdjustment', 'IsDirectAdjustment')
      ?? readCompat<boolean>(source, 'isManagerSelfDirect', 'IsManagerSelfDirect'),
    requestedByUserGuid: readCompat<string>(source, 'requestedByUserGuid', 'RequestedByUserGuid'),
    reviewedByUserGuid: readCompat<string>(source, 'reviewedByUserGuid', 'ReviewedByUserGuid'),
    reviewedAt: readCompat<string>(source, 'reviewedAt', 'ReviewedAt'),
  }
}

export function normalizeAttendanceApproval(value: unknown): AttendanceApprovalDto {
  const source = asRecord(value)
  const adjustment = readCompat<unknown>(source, 'adjustment', 'Adjustment')
    ?? readCompat<unknown>(source, 'adjustmentDetail', 'AdjustmentDetail')
  return {
    approvalGuid: readCompat<string>(source, 'approvalGuid', 'ApprovalGuid') ?? '',
    sourceType: readCompat<AttendanceApprovalDto['sourceType']>(source, 'sourceType', 'SourceType') ?? 'Punch',
    sourceGuid: readCompat<string>(source, 'sourceGuid', 'SourceGuid') ?? '',
    storeCode: readCompat<string>(source, 'storeCode', 'StoreCode') ?? '',
    storeName: readCompat<string>(source, 'storeName', 'StoreName'),
    applicantUserGuid: readCompat<string>(source, 'applicantUserGuid', 'ApplicantUserGuid') ?? '',
    applicantName: readCompat<string>(source, 'applicantName', 'ApplicantName')
      ?? readCompat<string>(source, 'employeeName', 'EmployeeName'),
    reviewerUserGuid: readCompat<string>(source, 'reviewerUserGuid', 'ReviewerUserGuid'),
    reviewerName: readCompat<string>(source, 'reviewerName', 'ReviewerName'),
    workDate: readCompat<string>(source, 'workDate', 'WorkDate'),
    title: readCompat<string>(source, 'title', 'Title')
      ?? readCompat<string>(source, 'sourceType', 'SourceType')
      ?? 'Punch',
    detail: readCompat<string>(source, 'detail', 'Detail'),
    reviewStatus: readCompat<AttendanceApprovalDto['reviewStatus']>(source, 'reviewStatus', 'ReviewStatus') ?? 'Pending',
    reviewRemark: readCompat<string>(source, 'reviewRemark', 'ReviewRemark'),
    reviewedAt: readCompat<string>(source, 'reviewedAt', 'ReviewedAt'),
    createdAt: readCompat<string>(source, 'createdAt', 'CreatedAt'),
    candidateOvertimeMinutes: readCompat<number>(source, 'candidateOvertimeMinutes', 'CandidateOvertimeMinutes'),
    approvedOvertimeMinutes: readCompat<number>(source, 'approvedOvertimeMinutes', 'ApprovedOvertimeMinutes'),
    adjustment: adjustment ? normalizeAttendanceAdjustment(adjustment) : undefined,
    mealClaim: normalizeAttendanceMealClaim(readCompat<unknown>(source, 'mealClaim', 'MealClaim')),
  }
}

function normalizeAttendanceWorkSession(value: unknown) {
  const source = asRecord(value)
  const segments = readCompat<unknown[]>(source, 'segments', 'Segments')
  return {
    scheduleState: readCompat<string>(source, 'scheduleState', 'ScheduleState'),
    segmentLimit: readCompat<number>(source, 'segmentLimit', 'SegmentLimit'),
    completedSegmentCount: readCompat<number>(source, 'completedSegmentCount', 'CompletedSegmentCount'),
    workedMinutes: readCompat<number>(source, 'workedMinutes', 'WorkedMinutes') ?? 0,
    breakMinutes: readCompat<number>(source, 'breakMinutes', 'BreakMinutes') ?? 0,
    hasOpenSegment: readCompat<boolean>(source, 'hasOpenSegment', 'HasOpenSegment'),
    hasMissingClockOut: readCompat<boolean>(source, 'hasMissingClockOut', 'HasMissingClockOut'),
    earlyOvertimeMinutes: readCompat<number>(source, 'earlyOvertimeMinutes', 'EarlyOvertimeMinutes'),
    lateOvertimeMinutes: readCompat<number>(source, 'lateOvertimeMinutes', 'LateOvertimeMinutes'),
    candidateOvertimeMinutes: readCompat<number>(source, 'candidateOvertimeMinutes', 'CandidateOvertimeMinutes') ?? 0,
    segments: Array.isArray(segments) ? segments.map(normalizeAttendanceSegment) : undefined,
  }
}

export function normalizeAttendancePunchAdjustmentPreview(value: unknown): AttendancePunchAdjustmentPreviewDto {
  const source = asRecord(value)
  const existingSession = readCompat<unknown>(source, 'existingSession', 'ExistingSession')
  const proposedSession = readCompat<unknown>(source, 'proposedSession', 'ProposedSession')
  return {
    isValid: readCompat<boolean>(source, 'isValid', 'IsValid') ?? false,
    validationErrorCode: readCompat<string>(source, 'validationErrorCode', 'ValidationErrorCode'),
    validationMessage: readCompat<string>(source, 'validationMessage', 'ValidationMessage'),
    existingSession: existingSession ? normalizeAttendanceWorkSession(existingSession) : undefined,
    proposedSession: proposedSession ? normalizeAttendanceWorkSession(proposedSession) : undefined,
    workedMinutesDelta: readCompat<number>(source, 'workedMinutesDelta', 'WorkedMinutesDelta') ?? 0,
    candidateOvertimeMinutesDelta: readCompat<number>(source, 'candidateOvertimeMinutesDelta', 'CandidateOvertimeMinutesDelta') ?? 0,
    wouldAutoApprove: readCompat<boolean>(source, 'wouldAutoApprove', 'WouldAutoApprove') ?? false,
    previewRevision: readCompat<string>(source, 'previewRevision', 'PreviewRevision'),
  }
}

export function normalizeAttendancePunchAdjustment(value: unknown): AttendancePunchAdjustmentDto {
  const source = asRecord(value)
  const detail = normalizeAttendanceAdjustment(source)
  const exceptionChanges = readCompat<unknown[]>(source, 'exceptionChanges', 'ExceptionChanges')
  return {
    ...detail,
    adjustmentGuid: detail.adjustmentGuid ?? '',
    storeCode: readCompat<string>(source, 'storeCode', 'StoreCode') ?? '',
    userGuid: readCompat<string>(source, 'userGuid', 'UserGuid'),
    scheduleGuid: readCompat<string>(source, 'scheduleGuid', 'ScheduleGuid'),
    punchType: detail.punchType ?? 'ClockIn',
    requestedPunchTimeLocal: detail.requestedPunchTimeLocal ?? '',
    reason: detail.reason ?? '',
    status: detail.status ?? 'Pending',
    createdAt: readCompat<string>(source, 'createdAt', 'CreatedAt'),
    requestedPunchTimeUtc: readCompat<string>(source, 'requestedPunchTimeUtc', 'RequestedPunchTimeUtc'),
    appliedPunchGuid: readCompat<string>(source, 'appliedPunchGuid', 'AppliedPunchGuid'),
    isManagerSelfDirect: readCompat<boolean>(source, 'isManagerSelfDirect', 'IsManagerSelfDirect'),
    beforeWorkedMinutes: readCompat<number>(source, 'beforeWorkedMinutes', 'BeforeWorkedMinutes'),
    afterWorkedMinutes: readCompat<number>(source, 'afterWorkedMinutes', 'AfterWorkedMinutes'),
    beforeCandidateOvertimeMinutes: readCompat<number>(source, 'beforeCandidateOvertimeMinutes', 'BeforeCandidateOvertimeMinutes'),
    afterCandidateOvertimeMinutes: readCompat<number>(source, 'afterCandidateOvertimeMinutes', 'AfterCandidateOvertimeMinutes'),
    exceptionChanges: Array.isArray(exceptionChanges) ? exceptionChanges.map(String) : undefined,
  }
}

export function normalizeAttendanceSettings(value: unknown): AttendanceSettingsDto {
  const source = asRecord(value)
  return {
    lateGraceMinutes: readCompat<number>(source, 'lateGraceMinutes', 'LateGraceMinutes') ?? 0,
    earlyLeaveGraceMinutes: readCompat<number>(source, 'earlyLeaveGraceMinutes', 'EarlyLeaveGraceMinutes') ?? 0,
    allowNoSchedulePunch: readCompat<boolean>(source, 'allowNoSchedulePunch', 'AllowNoSchedulePunch') ?? false,
    requireApprovalForLate: readCompat<boolean>(source, 'requireApprovalForLate', 'RequireApprovalForLate') ?? false,
    requireApprovalForEarlyLeave: readCompat<boolean>(source, 'requireApprovalForEarlyLeave', 'RequireApprovalForEarlyLeave') ?? false,
    requireApprovalForNoSchedule: readCompat<boolean>(source, 'requireApprovalForNoSchedule', 'RequireApprovalForNoSchedule') ?? false,
    updatedAt: readCompat<string>(source, 'updatedAt', 'UpdatedAt'),
    updatedBy: readCompat<string>(source, 'updatedBy', 'UpdatedBy'),
    overtimeMinimumMinutes: readCompat<number>(source, 'overtimeMinimumMinutes', 'OvertimeMinimumMinutes'),
    requireOvertimeApproval: readCompat<boolean>(source, 'requireOvertimeApproval', 'RequireOvertimeApproval'),
    allowManagerDirectOwnAdjustment: readCompat<boolean>(source, 'allowManagerDirectOwnAdjustment', 'AllowManagerDirectOwnAdjustment'),
  }
}

export function normalizeAttendancePagedResult<T>(
  payload: ApiResponse<PagedResult<T>> | PagedResult<T> | T[] | unknown,
  normalizeItem?: (value: unknown) => T,
  requestedPage?: Pick<AttendanceQuery, 'page' | 'pageSize'>,
): AttendancePagedResult<T> {
  const data = unwrapAttendanceApiData<unknown>(payload)
  if (Array.isArray(data)) {
    const page = Math.max(1, requestedPage?.page ?? 1)
    const pageSize = Math.max(1, requestedPage?.pageSize ?? (data.length || 10))
    const offset = (page - 1) * pageSize
    return {
      items: (normalizeItem ? data.map(normalizeItem) : data as T[]).slice(offset, offset + pageSize),
      total: data.length,
      page,
      pageSize,
      totalPages: Math.ceil(data.length / pageSize),
    }
  }
  const source = asRecord(data)
  const items = readCompat<unknown[]>(source, 'items', 'Items') ?? []
  return {
    items: normalizeItem ? items.map(normalizeItem) : items as T[],
    total: readCompat<number>(source, 'total', 'Total')
      ?? readCompat<number>(source, 'totalCount', 'TotalCount')
      ?? items.length,
    page: readCompat<number>(source, 'page', 'Page')
      ?? readCompat<number>(source, 'pageIndex', 'PageIndex')
      ?? 1,
    pageSize: readCompat<number>(source, 'pageSize', 'PageSize') ?? (items.length || 10),
    totalPages: readCompat<number>(source, 'totalPages', 'TotalPages'),
  }
}

export async function getAttendanceScheduleWeek(params: AttendanceQuery): Promise<AttendanceScheduleDto[]> {
  const response = await request.get<ApiResponse<AttendanceScheduleDto[]> | AttendanceScheduleDto[]>(
    `${API_BASE}/schedules/week`,
    { params: params as Record<string, unknown> },
  )
  const data = unwrapAttendanceApiData<unknown[]>(response) ?? []
  return data.map(normalizeAttendanceSchedule)
}

export async function getAttendanceRecords(params: AttendanceQuery): Promise<AttendancePagedResult<AttendanceScheduleDto>> {
  const response = await request.get<ApiResponse<PagedResult<AttendanceScheduleDto>> | PagedResult<AttendanceScheduleDto> | AttendanceScheduleDto[]>(
    `${API_BASE}/records`,
    { params: params as Record<string, unknown> },
  )
  return normalizeAttendancePagedResult(response, normalizeAttendanceSchedule, params)
}

export async function createAttendanceSchedule(payload: SaveAttendanceSchedulePayload): Promise<AttendanceScheduleDto> {
  const response = await request.post<ApiResponse<AttendanceScheduleDto>>(`${API_BASE}/schedules`, payload)
  return normalizeAttendanceSchedule(unwrapAttendanceApiData(response))
}

export async function updateAttendanceSchedule(
  scheduleGuid: string,
  payload: SaveAttendanceSchedulePayload,
): Promise<AttendanceScheduleDto> {
  const response = await request.put<ApiResponse<AttendanceScheduleDto>>(`${API_BASE}/schedules/${scheduleGuid}`, payload)
  return normalizeAttendanceSchedule(unwrapAttendanceApiData(response))
}

export async function deleteAttendanceSchedule(scheduleGuid: string): Promise<void> {
  await request.delete<ApiResponse<boolean> | boolean>(`${API_BASE}/schedules/${scheduleGuid}`)
}

export async function publishAttendanceScheduleWeek(payload: { storeCode: string; weekStartDate: string }): Promise<void> {
  await request.post<ApiResponse<boolean> | boolean>(`${API_BASE}/schedules/publish-week`, payload)
}

export async function getAttendanceAvailability(params: AttendanceQuery): Promise<AttendancePagedResult<AttendanceAvailabilityDto>> {
  const response = await request.get<ApiResponse<PagedResult<AttendanceAvailabilityDto>> | PagedResult<AttendanceAvailabilityDto> | AttendanceAvailabilityDto[]>(
    `${API_BASE}/availability`,
    { params: params as Record<string, unknown> },
  )
  return normalizeAttendancePagedResult(response)
}

export async function getAttendancePunches(params: AttendanceQuery): Promise<AttendancePagedResult<AttendancePunchDto>> {
  const response = await request.get<ApiResponse<PagedResult<AttendancePunchDto>> | PagedResult<AttendancePunchDto> | AttendancePunchDto[]>(
    `${API_BASE}/punches`,
    { params: params as Record<string, unknown> },
  )
  return normalizeAttendancePagedResult(response, normalizeAttendancePunch)
}

export async function getAttendanceLocationSamples(params: AttendanceQuery): Promise<AttendanceLocationSampleDto[]> {
  const response = await request.get<ApiResponse<AttendanceLocationSampleDto[]> | AttendanceLocationSampleDto[]>(
    `${API_BASE}/location-samples`,
    { params: params as Record<string, unknown> },
  )
  return unwrapAttendanceApiData<AttendanceLocationSampleDto[]>(response) ?? []
}

export async function getAttendanceApprovals(params: AttendanceQuery): Promise<AttendancePagedResult<AttendanceApprovalDto>> {
  const response = await request.get<ApiResponse<PagedResult<AttendanceApprovalDto>> | PagedResult<AttendanceApprovalDto> | AttendanceApprovalDto[]>(
    `${API_BASE}/approvals`,
    { params: params as Record<string, unknown> },
  )
  return normalizeAttendancePagedResult(response, normalizeAttendanceApproval)
}

export async function getPendingAttendanceApprovals(params: AttendanceQuery): Promise<AttendancePagedResult<AttendanceApprovalDto>> {
  const response = await request.get<ApiResponse<PagedResult<AttendanceApprovalDto>> | PagedResult<AttendanceApprovalDto> | AttendanceApprovalDto[]>(
    `${API_BASE}/approvals/pending`,
    { params: params as Record<string, unknown> },
  )
  return normalizeAttendancePagedResult(response, normalizeAttendanceApproval)
}

export async function approveAttendanceApproval(
  approvalGuid: string,
  payload: ReviewAttendanceApprovalPayload,
): Promise<AttendanceApprovalDto> {
  const response = await request.post<ApiResponse<AttendanceApprovalDto>>(`${API_BASE}/approvals/${approvalGuid}/approve`, payload)
  return normalizeAttendanceApproval(unwrapAttendanceApiData(response))
}

export async function rejectAttendanceApproval(
  approvalGuid: string,
  payload: ReviewAttendanceApprovalPayload,
): Promise<AttendanceApprovalDto> {
  const response = await request.post<ApiResponse<AttendanceApprovalDto>>(`${API_BASE}/approvals/${approvalGuid}/reject`, payload)
  return normalizeAttendanceApproval(unwrapAttendanceApiData(response))
}

export async function getAttendanceHolidays(params: AttendanceQuery): Promise<AttendancePagedResult<AttendanceStoreHolidayDto>> {
  const response = await request.get<ApiResponse<PagedResult<AttendanceStoreHolidayDto>> | PagedResult<AttendanceStoreHolidayDto> | AttendanceStoreHolidayDto[]>(
    `${API_BASE}/holidays`,
    { params: params as Record<string, unknown> },
  )
  return normalizeAttendancePagedResult(response)
}

export async function createAttendanceHoliday(payload: SaveAttendanceHolidayPayload): Promise<AttendanceStoreHolidayDto> {
  const response = await request.post<ApiResponse<AttendanceStoreHolidayDto>>(`${API_BASE}/holidays`, payload)
  return unwrapAttendanceApiData<AttendanceStoreHolidayDto>(response)
}

export async function batchUpsertAttendanceHolidays(
  payload: BatchUpsertAttendanceHolidayPayload,
): Promise<BatchUpsertAttendanceHolidayResult> {
  const response = await request.post<ApiResponse<BatchUpsertAttendanceHolidayResult>>(`${API_BASE}/holidays/batch-upsert`, payload)
  return unwrapAttendanceApiData<BatchUpsertAttendanceHolidayResult>(response)
}

export async function syncAttendanceHolidays(
  payload: SyncAttendanceHolidayPayload,
): Promise<SyncAttendanceHolidayResult> {
  const response = await request.post<ApiResponse<SyncAttendanceHolidayResult>>(`${API_BASE}/holidays/sync`, payload)
  return unwrapAttendanceApiData<SyncAttendanceHolidayResult>(response)
}

export async function updateAttendanceHoliday(
  holidayGuid: string,
  payload: SaveAttendanceHolidayPayload,
): Promise<AttendanceStoreHolidayDto> {
  const response = await request.put<ApiResponse<AttendanceStoreHolidayDto>>(`${API_BASE}/holidays/${holidayGuid}`, payload)
  return unwrapAttendanceApiData<AttendanceStoreHolidayDto>(response)
}

export async function deleteAttendanceHoliday(holidayGuid: string): Promise<void> {
  await request.delete<ApiResponse<boolean> | boolean>(`${API_BASE}/holidays/${holidayGuid}`)
}

export async function getAttendanceSettings(): Promise<AttendanceSettingsDto> {
  const response = await request.get<ApiResponse<AttendanceSettingsDto>>(`${API_BASE}/settings`)
  return normalizeAttendanceSettings(unwrapAttendanceApiData(response))
}

export async function updateAttendanceSettings(payload: SaveAttendanceSettingsPayload): Promise<AttendanceSettingsDto> {
  const response = await request.put<ApiResponse<AttendanceSettingsDto>>(`${API_BASE}/settings`, payload)
  return normalizeAttendanceSettings(unwrapAttendanceApiData(response))
}

export async function getMyAttendancePunchAdjustments(
  params: AttendanceQuery = {},
): Promise<AttendancePagedResult<AttendancePunchAdjustmentDto>> {
  const response = await request.get<unknown>(MY_PUNCH_ADJUSTMENTS_ENDPOINT, {
    params: params as Record<string, unknown>,
  })
  return normalizeAttendancePagedResult(response, normalizeAttendancePunchAdjustment)
}

export async function createMyAttendancePunchAdjustment(
  payload: SaveAttendancePunchAdjustmentPayload,
): Promise<AttendancePunchAdjustmentDto> {
  const response = await request.post<unknown>(MY_PUNCH_ADJUSTMENTS_ENDPOINT, payload)
  return normalizeAttendancePunchAdjustment(unwrapAttendanceApiData(response))
}

export async function previewMyAttendancePunchAdjustment(
  payload: SaveAttendancePunchAdjustmentPayload,
): Promise<AttendancePunchAdjustmentPreviewDto> {
  const response = await request.post<unknown>(`${MY_PUNCH_ADJUSTMENTS_ENDPOINT}/preview`, payload)
  return normalizeAttendancePunchAdjustmentPreview(unwrapAttendanceApiData(response))
}
