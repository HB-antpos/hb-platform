import type {
  AttendanceApprovalSourceType,
  AttendancePunchType,
  AttendancePunchAdjustmentPreviewDto,
  AttendanceScheduleDto,
  AttendanceScheduleSegmentDto,
  SaveAttendancePunchAdjustmentPayload,
} from '../../../types/scheduleAttendance'

const knownAttendanceApprovalSourceTypes = new Set<AttendanceApprovalSourceType>([
  'Punch',
  'Leave',
  'PunchAdjustment',
  'Overtime',
  'MissingClockOut',
  'MealBreak',
])

export function getAttendanceWeekdayIndex(value?: string): number | undefined {
  const match = value?.match(/^(\d{4})-(\d{2})-(\d{2})/)
  if (!match) return undefined

  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  // 工作日期是门店日历日期；按 UTC 校验并计算星期，避免浏览器时区造成日期偏移。
  const date = new Date(Date.UTC(year, month - 1, day))
  if (
    date.getUTCFullYear() !== year
    || date.getUTCMonth() !== month - 1
    || date.getUTCDate() !== day
  ) return undefined

  return (date.getUTCDay() + 6) % 7
}

export function insertAttendanceWeekday(displayText: string, weekdayLabel?: string): string {
  if (!weekdayLabel || displayText === '--') return displayText

  const match = displayText.match(/^(\d{4}-\d{2}-\d{2})(.*)$/)
  if (!match) return displayText

  return `${match[1]} ${weekdayLabel}${match[2]}`
}

export function insertAttendanceWeekdaysInText(
  text: string,
  resolveWeekdayLabel: (dateText: string) => string | undefined,
): string {
  return text.replace(/\b\d{4}-\d{2}-\d{2}\b/g, (dateText) => (
    insertAttendanceWeekday(dateText, resolveWeekdayLabel(dateText))
  ))
}

export function isKnownAttendanceApprovalSourceType(value: string): value is AttendanceApprovalSourceType {
  return knownAttendanceApprovalSourceTypes.has(value as AttendanceApprovalSourceType)
}

export function getSupplementalAttendanceApprovalDetail(input: {
  sourceType: string
  detail?: string
  displayedTitle: string
}): string | undefined {
  if (input.sourceType !== 'Punch' && input.sourceType !== 'Leave') return undefined
  const detail = input.detail?.trim()
  if (!detail || detail === input.displayedTitle.trim()) return undefined
  return detail
}

export interface AttendanceRecordSummary {
  firstClockIn?: string
  finalClockOut?: string
  workedMinutes: number
  breakMinutes: number
  lateMinutes: number
  earlyLeaveMinutes: number
  earlyOvertimeMinutes: number
  lateOvertimeMinutes: number
  candidateOvertimeMinutes: number
  approvedOvertimeMinutes: number
}

export type OvertimeApprovalValidationError = 'outOfRange' | 'invalidIncrement' | 'remarkRequired'

export interface OvertimeApprovalInput {
  candidateMinutes: number
  approvedMinutes: number
  action: 'approve' | 'reject'
  remark?: string
}

export interface LocalPunchAdjustmentPreview {
  punchType: AttendancePunchType
  originalPunchTimeLocal?: string
  requestedPunchTimeLocal: string
  beforeWorkedMinutes: number
  afterWorkedMinutes: number
  workedMinutesDelta: number
  beforeCandidateOvertimeMinutes: number
  afterCandidateOvertimeMinutes: number
  overtimeMinutesDelta: number
  exceptions: string[]
}

export interface PunchAdjustmentOption {
  value: string
  segmentIndex: number
  punchTimeLocal?: string
}

export type PunchAdjustmentMode = 'create' | 'replace'

export interface MatchingPunchAdjustmentPreviewInput {
  requestId: number
  latestRequestId: number
  previewPayloadSnapshot: string
  currentPayloadSnapshot: string
}

export interface OwnAttendanceAdjustmentAccessInput {
  isAdmin: boolean
  isStoreManager: boolean
  isOwnSchedule: boolean
  isManagedStore: boolean
}

function minuteOfDay(value?: string): number | undefined {
  if (!value) return undefined
  const timePart = value.includes('T') ? value.split('T')[1] : value
  const match = timePart?.match(/^(\d{1,2}):(\d{2})/)
  if (!match) return undefined
  return Number(match[1]) * 60 + Number(match[2])
}

function durationBetween(start?: string, end?: string): number {
  const startMinute = minuteOfDay(start)
  const endMinute = minuteOfDay(end)
  if (startMinute === undefined || endMinute === undefined) return 0
  return Math.max(0, endMinute - startMinute)
}

function orderedSegments(segments?: AttendanceScheduleSegmentDto[]) {
  return [...(segments ?? [])].sort((left, right) => left.segmentIndex - right.segmentIndex)
}

export function resolveSegmentPunchTime(value?: string | { punchTimeLocal?: string; punchTimeUtc?: string }): string | undefined {
  if (typeof value === 'string') return value
  return value?.punchTimeLocal ?? value?.punchTimeUtc
}

export function getPunchAdjustmentOptions(
  schedule: AttendanceScheduleDto,
  punchType: AttendancePunchType,
): PunchAdjustmentOption[] {
  return orderedSegments(schedule.segments).flatMap((segment) => {
    const punch = punchType === 'ClockIn' ? segment.clockIn : segment.clockOut
    if (!punch || typeof punch === 'string' || !punch.punchGuid) return []
    return [{
      value: punch.punchGuid,
      segmentIndex: segment.segmentIndex,
      punchTimeLocal: punch.punchTimeLocal ?? punch.punchTimeUtc,
    }]
  })
}

export function deriveOriginalPunchGuid(
  schedule: AttendanceScheduleDto,
  punchType: AttendancePunchType,
): string | undefined {
  const options = getPunchAdjustmentOptions(schedule, punchType)
  return punchType === 'ClockIn' ? options[0]?.value : options[options.length - 1]?.value
}

export function getDefaultPunchAdjustmentMode(
  schedule: AttendanceScheduleDto,
  punchType: AttendancePunchType,
): PunchAdjustmentMode {
  if (punchType === 'ClockOut' && schedule.hasMissingClockOut) return 'create'
  return getPunchAdjustmentOptions(schedule, punchType).length ? 'replace' : 'create'
}

export function resolvePunchAdjustmentOriginalGuid(
  mode: PunchAdjustmentMode,
  selectedOriginalPunchGuid?: string,
): string | undefined {
  return mode === 'replace' ? selectedOriginalPunchGuid : undefined
}

export function getPunchAdjustmentPayloadSnapshot(payload: SaveAttendancePunchAdjustmentPayload): string {
  return JSON.stringify({
    storeCode: payload.storeCode,
    scheduleGuid: payload.scheduleGuid ?? null,
    originalPunchGuid: payload.originalPunchGuid ?? null,
    punchType: payload.punchType,
    requestedPunchTimeLocal: payload.requestedPunchTimeLocal,
    reason: payload.reason,
  })
}

export function isLatestMatchingPunchAdjustmentPreview(input: MatchingPunchAdjustmentPreviewInput): boolean {
  return input.requestId === input.latestRequestId
    && input.previewPayloadSnapshot === input.currentPayloadSnapshot
}

export function canAdjustOwnAttendanceRecord(input: OwnAttendanceAdjustmentAccessInput): boolean {
  if (!input.isOwnSchedule) return false
  return input.isAdmin || (input.isStoreManager && input.isManagedStore)
}

export function buildAttendanceRecordSummary(schedule: AttendanceScheduleDto): AttendanceRecordSummary {
  const segments = orderedSegments(schedule.segments)
  const firstClockIn = resolveSegmentPunchTime(segments.find((segment) => segment.clockIn)?.clockIn)
  const finalClockOut = resolveSegmentPunchTime([...segments].reverse().find((segment) => segment.clockOut)?.clockOut)
  const calculatedWorkedMinutes = segments.reduce(
    (total, segment) => total + (segment.durationMinutes ?? durationBetween(
      resolveSegmentPunchTime(segment.clockIn),
      resolveSegmentPunchTime(segment.clockOut),
    )),
    0,
  )
  const calculatedBreakMinutes = segments.slice(1).reduce((total, segment, index) => (
    total + durationBetween(
      resolveSegmentPunchTime(segments[index]?.clockOut),
      resolveSegmentPunchTime(segment.clockIn),
    )
  ), 0)
  const scheduledStart = minuteOfDay(schedule.startTime)
  const scheduledEnd = minuteOfDay(schedule.endTime)
  const firstClockInMinute = minuteOfDay(firstClockIn)
  const finalClockOutMinute = minuteOfDay(finalClockOut)

  return {
    firstClockIn,
    finalClockOut,
    workedMinutes: schedule.workedMinutes ?? calculatedWorkedMinutes,
    breakMinutes: schedule.breakMinutes ?? calculatedBreakMinutes,
    lateMinutes: schedule.lateMinutes ?? (
      scheduledStart !== undefined && firstClockInMinute !== undefined
        ? Math.max(0, firstClockInMinute - scheduledStart)
        : 0
    ),
    earlyLeaveMinutes: schedule.earlyLeaveMinutes ?? (
      scheduledEnd !== undefined && finalClockOutMinute !== undefined
        ? Math.max(0, scheduledEnd - finalClockOutMinute)
        : 0
    ),
    earlyOvertimeMinutes: schedule.earlyOvertimeMinutes ?? (
      scheduledStart !== undefined && firstClockInMinute !== undefined
        ? Math.max(0, scheduledStart - firstClockInMinute)
        : 0
    ),
    lateOvertimeMinutes: schedule.lateOvertimeMinutes ?? (
      scheduledEnd !== undefined && finalClockOutMinute !== undefined
        ? Math.max(0, finalClockOutMinute - scheduledEnd)
        : 0
    ),
    candidateOvertimeMinutes: schedule.candidateOvertimeMinutes ?? 0,
    approvedOvertimeMinutes: schedule.approvedOvertimeMinutes ?? 0,
  }
}

export function validateOvertimeApproval(input: OvertimeApprovalInput): OvertimeApprovalValidationError | null {
  if (input.approvedMinutes < 0 || input.approvedMinutes > input.candidateMinutes) {
    return 'outOfRange'
  }
  if (input.approvedMinutes % 15 !== 0) {
    return 'invalidIncrement'
  }
  if ((input.action === 'reject' || input.approvedMinutes < input.candidateMinutes) && !input.remark?.trim()) {
    return 'remarkRequired'
  }
  return null
}

/** 用餐未休息加工时：批准按申请分钟全额加回，拒绝必须填写审核备注（与后端 REVIEW_REMARK_REQUIRED 一致）。 */
export function validateMealBreakApproval(input: { action: 'approve' | 'reject'; remark?: string }): 'remarkRequired' | null {
  return input.action === 'reject' && !input.remark?.trim() ? 'remarkRequired' : null
}

export interface AttendanceMealPaySummary {
  deductionMinutes: number
  pendingAddBackMinutes: number
  approvedAddBackMinutes: number
  paidMinutes?: number
}

/**
 * 记录表「用餐与计薪」列：排班有用餐、或有加回申请时才返回；
 * 旧后端没有这些字段、或排班不用餐且无申请时返回 undefined，单元格留空。
 */
export function buildAttendanceMealPaySummary(schedule: AttendanceScheduleDto): AttendanceMealPaySummary | undefined {
  const deductionMinutes = schedule.mealDeductionMinutes ?? 0
  const pendingAddBackMinutes = schedule.pendingMealAddBackMinutes ?? 0
  const approvedAddBackMinutes = schedule.approvedMealAddBackMinutes ?? 0
  if (deductionMinutes <= 0 && pendingAddBackMinutes <= 0 && approvedAddBackMinutes <= 0) return undefined
  return { deductionMinutes, pendingAddBackMinutes, approvedAddBackMinutes, paidMinutes: schedule.paidMinutes }
}

function localPunchMinuteKey(value?: string): string | undefined {
  if (!value) return undefined
  const match = value.match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/)
  return match ? `${match[1]}T${match[2]}` : undefined
}

export function getProposedAdjustmentPunchStatus(
  preview: AttendancePunchAdjustmentPreviewDto,
  punchType: AttendancePunchType,
  requestedPunchTimeLocal: string,
): string | undefined {
  const requestedKey = localPunchMinuteKey(requestedPunchTimeLocal)
  if (!requestedKey) return undefined

  for (const segment of preview.proposedSession?.segments ?? []) {
    const punch = punchType === 'ClockIn' ? segment.clockIn : segment.clockOut
    if (typeof punch !== 'object' || punch.punchType !== punchType) continue
    if (localPunchMinuteKey(punch.punchTimeLocal ?? punch.punchTimeUtc) === requestedKey) {
      return punch.status
    }
  }
  return undefined
}

export function buildLocalPunchAdjustmentPreview(
  schedule: AttendanceScheduleDto,
  punchType: AttendancePunchType,
  requestedPunchTimeLocal: string,
  adjustmentMode: PunchAdjustmentMode,
  originalPunchGuid?: string,
): LocalPunchAdjustmentPreview {
  const summary = buildAttendanceRecordSummary(schedule)
  const segments = orderedSegments(schedule.segments)
  const selectedPunch = adjustmentMode === 'replace' && originalPunchGuid
    ? segments
      .map((segment) => ({
        segment,
        punch: punchType === 'ClockIn' ? segment.clockIn : segment.clockOut,
      }))
      .find(({ punch }) => typeof punch === 'object' && punch?.punchGuid === originalPunchGuid)
    : undefined
  const fallbackBoundaryTime = punchType === 'ClockIn' ? summary.firstClockIn : summary.finalClockOut
  const originalPunchTimeLocal = adjustmentMode === 'create'
    ? undefined
    : originalPunchGuid
      ? resolveSegmentPunchTime(selectedPunch?.punch)
      : fallbackBoundaryTime
  const originalMinute = minuteOfDay(originalPunchTimeLocal)
  const requestedMinute = minuteOfDay(requestedPunchTimeLocal)
  const delta = adjustmentMode === 'create' || originalMinute === undefined || requestedMinute === undefined
    ? 0
    : punchType === 'ClockIn'
      ? originalMinute - requestedMinute
      : requestedMinute - originalMinute
  const afterWorkedMinutes = Math.max(0, summary.workedMinutes + delta)
  const selectedSegmentIndex = selectedPunch?.segment.segmentIndex
  const firstClockInSegmentIndex = segments.find((segment) => segment.clockIn)?.segmentIndex
  const finalClockOutSegmentIndex = [...segments].reverse().find((segment) => segment.clockOut)?.segmentIndex
  const isScheduleBoundary = adjustmentMode === 'replace'
    ? selectedPunch
      ? selectedSegmentIndex === (punchType === 'ClockIn' ? firstClockInSegmentIndex : finalClockOutSegmentIndex)
      : !originalPunchGuid
    : false
  const scheduledBoundary = minuteOfDay(punchType === 'ClockIn' ? schedule.startTime : schedule.endTime)
  const exceptions: string[] = []
  // 迟到/早退需应用服务端 grace 设置，本地只保留不依赖设置的提示。
  if (schedule.hasMissingClockOut && punchType === 'ClockOut' && adjustmentMode === 'create') {
    exceptions.push('MissingClockOutResolved')
  }
  const boundaryOvertime = scheduledBoundary === undefined || requestedMinute === undefined
    ? 0
    : punchType === 'ClockIn'
      ? Math.max(0, scheduledBoundary - requestedMinute)
      : Math.max(0, requestedMinute - scheduledBoundary)
  const unchangedBoundaryOvertime = punchType === 'ClockIn'
    ? summary.lateOvertimeMinutes
    : summary.earlyOvertimeMinutes
  const afterCandidateOvertimeMinutes = isScheduleBoundary
    ? boundaryOvertime + unchangedBoundaryOvertime
    : summary.candidateOvertimeMinutes

  return {
    punchType,
    originalPunchTimeLocal,
    requestedPunchTimeLocal,
    beforeWorkedMinutes: summary.workedMinutes,
    afterWorkedMinutes,
    workedMinutesDelta: afterWorkedMinutes - summary.workedMinutes,
    beforeCandidateOvertimeMinutes: summary.candidateOvertimeMinutes,
    afterCandidateOvertimeMinutes,
    overtimeMinutesDelta: afterCandidateOvertimeMinutes - summary.candidateOvertimeMinutes,
    exceptions,
  }
}
