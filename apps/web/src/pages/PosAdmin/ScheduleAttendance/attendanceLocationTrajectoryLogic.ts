import type {
  AttendanceLocationSampleDto,
  AttendancePunchDto,
} from '../../../types/scheduleAttendance'

export type AttendanceTrajectoryPointKind = 'ClockIn' | 'Sample' | 'ClockOut'

export type AttendanceTrajectoryFailureReason =
  | 'INVALID_SELECTED_TIME'
  | 'SUPERSEDE_CYCLE'
  | 'SUPERSEDE_TARGET_MISSING'
  | 'EFFECTIVE_PUNCH_NOT_FOUND'
  | 'SEGMENT_NOT_FOUND'
  | 'INVALID_DATE_RANGE'

export interface AttendanceTrajectoryPoint {
  key: string
  kind: AttendanceTrajectoryPointKind
  capturedAtUtc: string
  displayTimeSource?: string
  latitude?: number
  longitude?: number
  accuracy?: number
  deviceSystem?: string
  /** 仅 Sample 有值：样本事件类型（ShiftSample / ShiftHeartbeat / ServerRequest 等），界面据此给「服务端请求」点位打标签。 */
  eventType?: string
}

export interface AttendanceTrajectoryResult {
  ok: boolean
  reason?: AttendanceTrajectoryFailureReason
  segmentIndex?: number
  isOpen: boolean
  clockIn?: AttendancePunchDto
  clockOut?: AttendancePunchDto
  fromDate: string
  toDate: string
  points: AttendanceTrajectoryPoint[]
  mapPoints: AttendanceTrajectoryPoint[]
  sampleCount: number
}

export interface BuildAttendanceLocationTrajectoryInput {
  selectedPunch: AttendancePunchDto
  punches: AttendancePunchDto[]
  samples: AttendanceLocationSampleDto[]
  nowUtc: string
  currentStoreLocalDate?: string
}

export interface AttendanceSampleDateRange {
  fromDate: string
  toDate: string
}

interface EffectiveSegment {
  segmentIndex: number
  clockIn: AttendancePunchDto
  clockOut?: AttendancePunchDto
}

function fail(reason: AttendanceTrajectoryFailureReason): AttendanceTrajectoryResult {
  return {
    ok: false,
    reason,
    isOpen: false,
    fromDate: '',
    toDate: '',
    points: [],
    mapPoints: [],
    sampleCount: 0,
  }
}

function normalizedKey(value: string): string {
  return value.trim().toLocaleLowerCase('en-US')
}

function sameOptionalGuid(left?: string | null, right?: string | null): boolean {
  const leftKey = left?.trim()
  const rightKey = right?.trim()
  if (!leftKey || !rightKey) return !leftKey && !rightKey
  return normalizedKey(leftKey) === normalizedKey(rightKey)
}

const attendanceUtcPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(Z|[+-]\d{2}:\d{2})?$/i

export function normalizeAttendanceUtcText(value?: string): string | undefined {
  const trimmed = value?.trim()
  if (!trimmed) return undefined

  const match = attendanceUtcPattern.exec(trimmed)
  if (!match) return undefined

  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  const hour = Number(match[4])
  const minute = Number(match[5])
  const second = Number(match[6])
  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate()
  if (
    year < 1
    || month < 1
    || month > 12
    || day < 1
    || day > daysInMonth
    || hour > 23
    || minute > 59
    || second > 59
  ) {
    return undefined
  }

  // SQL DateTime 不保存 Kind；仅 UTC 语义字段允许把严格的无后缀 .NET ISO 时间补成 UTC。
  const normalized = match[7] ? trimmed : `${trimmed}Z`
  return Number.isFinite(Date.parse(normalized)) ? normalized : undefined
}

function utcTimestamp(value?: string): number | undefined {
  const normalized = normalizeAttendanceUtcText(value)
  if (!normalized) return undefined
  const timestamp = Date.parse(normalized)
  return Number.isFinite(timestamp) ? timestamp : undefined
}

function localDatePart(value?: string): string | undefined {
  const match = value?.match(/^(\d{4})-(\d{2})-(\d{2})(?:T|$)/)
  if (!match) return undefined

  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  const parsed = new Date(Date.UTC(year, month - 1, day))
  if (
    parsed.getUTCFullYear() !== year
    || parsed.getUTCMonth() !== month - 1
    || parsed.getUTCDate() !== day
  ) {
    return undefined
  }
  return `${match[1]}-${match[2]}-${match[3]}`
}

export function splitAttendanceSampleDateRange(
  fromDate: string,
  toDate: string,
  maxDays = 7,
): AttendanceSampleDateRange[] {
  const normalizedFrom = localDatePart(fromDate)
  const normalizedTo = localDatePart(toDate)
  if (!normalizedFrom || !normalizedTo || normalizedTo < normalizedFrom || !Number.isInteger(maxDays) || maxDays < 1) {
    return []
  }

  const toUtcDate = (value: string) => {
    const [year, month, day] = value.split('-').map(Number)
    return new Date(Date.UTC(year, month - 1, day))
  }
  const toDateText = (value: Date) => value.toISOString().slice(0, 10)
  const ranges: AttendanceSampleDateRange[] = []
  const finalDate = toUtcDate(normalizedTo)
  let cursor = toUtcDate(normalizedFrom)

  while (cursor <= finalDate) {
    const rangeEnd = new Date(cursor)
    rangeEnd.setUTCDate(rangeEnd.getUTCDate() + maxDays - 1)
    if (rangeEnd > finalDate) rangeEnd.setTime(finalDate.getTime())
    ranges.push({
      fromDate: toDateText(cursor),
      toDate: toDateText(rangeEnd),
    })
    cursor = new Date(rangeEnd)
    cursor.setUTCDate(cursor.getUTCDate() + 1)
  }
  return ranges
}

function dateInTimeZone(valueUtc: string, timeZone?: string): string | undefined {
  const timestamp = utcTimestamp(valueUtc)
  if (timestamp === undefined || !timeZone?.trim()) return undefined

  try {
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    }).formatToParts(new Date(timestamp))
    const year = parts.find((part) => part.type === 'year')?.value
    const month = parts.find((part) => part.type === 'month')?.value
    const day = parts.find((part) => part.type === 'day')?.value
    return year && month && day ? `${year}-${month}-${day}` : undefined
  } catch {
    return undefined
  }
}

function punchLocalDate(punch: AttendancePunchDto): string | undefined {
  return localDatePart(punch.punchTimeLocal)
    ?? (punch.punchTimeUtc ? dateInTimeZone(punch.punchTimeUtc, punch.storeTimeZone) : undefined)
}

function compareStableKey(left: string, right: string): number {
  return left.localeCompare(right, 'en', { sensitivity: 'base' })
    || left.localeCompare(right, 'en')
}

function hasValidCoordinates(latitude?: number, longitude?: number): boolean {
  return typeof latitude === 'number'
    && Number.isFinite(latitude)
    && latitude >= -90
    && latitude <= 90
    && typeof longitude === 'number'
    && Number.isFinite(longitude)
    && longitude >= -180
    && longitude <= 180
}

function validAccuracy(value?: number): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0
    ? value
    : undefined
}

function punchPoint(
  punch: AttendancePunchDto,
  kind: 'ClockIn' | 'ClockOut',
): AttendanceTrajectoryPoint {
  const point: AttendanceTrajectoryPoint = {
    key: `punch:${punch.punchGuid}`,
    kind,
    capturedAtUtc: punch.punchTimeUtc!,
    displayTimeSource: punch.punchTimeLocal,
    accuracy: validAccuracy(punch.locationAccuracy),
  }
  if (hasValidCoordinates(punch.locationLatitude, punch.locationLongitude)) {
    point.latitude = punch.locationLatitude
    point.longitude = punch.locationLongitude
  }
  return point
}

function samplePoint(sample: AttendanceLocationSampleDto): AttendanceTrajectoryPoint {
  const point: AttendanceTrajectoryPoint = {
    key: `sample:${sample.sampleGuid}`,
    kind: 'Sample',
    capturedAtUtc: sample.locationCapturedAtUtc,
    displayTimeSource: sample.locationCapturedAtUtc,
    accuracy: validAccuracy(sample.locationAccuracy),
    deviceSystem: sample.deviceSystem,
    eventType: sample.eventType,
  }
  if (hasValidCoordinates(sample.locationLatitude, sample.locationLongitude)) {
    point.latitude = sample.locationLatitude
    point.longitude = sample.locationLongitude
  }
  return point
}

function resolveEffectivePunch(
  selectedPunch: AttendancePunchDto,
  punches: AttendancePunchDto[],
): AttendancePunchDto | AttendanceTrajectoryFailureReason {
  const punchesByGuid = new Map<string, AttendancePunchDto>()
  for (const punch of punches) {
    const key = normalizedKey(punch.punchGuid)
    if (!key || punchesByGuid.has(key)) return 'EFFECTIVE_PUNCH_NOT_FOUND'
    punchesByGuid.set(key, punch)
  }

  const selectedKey = normalizedKey(selectedPunch.punchGuid)
  if (!punchesByGuid.has(selectedKey)) return 'EFFECTIVE_PUNCH_NOT_FOUND'

  const replacementKeysByTarget = new Map<string, string[]>()
  for (const punch of punches) {
    const replacementKey = normalizedKey(punch.punchGuid)
    const targetGuid = punch.supersedesPunchGuid?.trim()
    if (!targetGuid) continue

    const targetKey = normalizedKey(targetGuid)
    if (!punchesByGuid.has(targetKey)) return 'SUPERSEDE_TARGET_MISSING'
    const replacementKeys = replacementKeysByTarget.get(targetKey) ?? []
    replacementKeys.push(replacementKey)
    replacementKeysByTarget.set(targetKey, replacementKeys)
  }

  // 新卡指向旧卡；逐节点检查祖先链可同时覆盖自环和多节点循环。
  for (const punch of punches) {
    const visited = new Set<string>()
    let current: AttendancePunchDto | undefined = punch
    while (current?.supersedesPunchGuid?.trim()) {
      const currentKey = normalizedKey(current.punchGuid)
      if (visited.has(currentKey)) return 'SUPERSEDE_CYCLE'
      visited.add(currentKey)

      const targetKey = normalizedKey(current.supersedesPunchGuid)
      current = punchesByGuid.get(targetKey)
      if (!current) return 'SUPERSEDE_TARGET_MISSING'
    }
  }

  let effectiveKey = selectedKey
  const visited = new Set<string>()
  while (true) {
    if (visited.has(effectiveKey)) return 'SUPERSEDE_CYCLE'
    visited.add(effectiveKey)

    const replacementKeys = replacementKeysByTarget.get(effectiveKey) ?? []
    if (replacementKeys.length === 0) {
      return punchesByGuid.get(effectiveKey) ?? 'EFFECTIVE_PUNCH_NOT_FOUND'
    }
    if (replacementKeys.length !== 1) return 'EFFECTIVE_PUNCH_NOT_FOUND'
    effectiveKey = replacementKeys[0]
  }
}

function effectivePunches(
  punches: AttendancePunchDto[],
): AttendancePunchDto[] | AttendanceTrajectoryFailureReason {
  const supersededKeys = new Set<string>()
  for (const punch of punches) {
    if (punch.supersedesPunchGuid?.trim()) {
      supersededKeys.add(normalizedKey(punch.supersedesPunchGuid))
    }
  }

  const effective = punches.filter((punch) => !supersededKeys.has(normalizedKey(punch.punchGuid)))
  if (effective.some((punch) => utcTimestamp(punch.punchTimeUtc) === undefined)) {
    return 'SEGMENT_NOT_FOUND'
  }
  return effective.sort((left, right) => (
    utcTimestamp(left.punchTimeUtc)! - utcTimestamp(right.punchTimeUtc)!
      || compareStableKey(left.punchGuid, right.punchGuid)
  ))
}

function pairEffectivePunches(punches: AttendancePunchDto[]): EffectiveSegment[] {
  const segments: EffectiveSegment[] = []
  let openSegment: EffectiveSegment | undefined

  for (const punch of punches) {
    if (punch.punchType === 'ClockIn') {
      if (openSegment) continue
      openSegment = {
        segmentIndex: segments.length + 1,
        clockIn: punch,
      }
      segments.push(openSegment)
      continue
    }

    if (punch.punchType !== 'ClockOut' || !openSegment) continue
    openSegment.clockOut = punch
    openSegment = undefined
  }
  return segments
}

function sameText(left?: string, right?: string): boolean {
  return normalizedKey(left ?? '') === normalizedKey(right ?? '')
}

export function buildAttendanceLocationTrajectory({
  selectedPunch,
  punches,
  samples,
  nowUtc,
  currentStoreLocalDate,
}: BuildAttendanceLocationTrajectoryInput): AttendanceTrajectoryResult {
  if (utcTimestamp(selectedPunch.punchTimeUtc) === undefined) {
    return fail('INVALID_SELECTED_TIME')
  }

  const nowTimestamp = utcTimestamp(nowUtc)
  if (nowTimestamp === undefined) return fail('INVALID_DATE_RANGE')

  const schedulePunches = punches.filter((punch) => (
    sameOptionalGuid(punch.scheduleGuid, selectedPunch.scheduleGuid)
  ))
  const effectivePunch = resolveEffectivePunch(selectedPunch, schedulePunches)
  if (typeof effectivePunch === 'string') return fail(effectivePunch)

  const orderedPunches = effectivePunches(schedulePunches)
  if (!Array.isArray(orderedPunches)) return fail(orderedPunches)

  const effectiveKey = normalizedKey(effectivePunch.punchGuid)
  const effectiveSegmentIndex = effectivePunch.segmentIndex
  let segment: EffectiveSegment | undefined
  if (typeof effectiveSegmentIndex === 'number' && Number.isInteger(effectiveSegmentIndex) && effectiveSegmentIndex > 0) {
    // 后端已按 PunchTimeUtc + 数据库 Id 计算班段；优先使用该稳定结果，避免同一 UTC 时刻仅凭 GUID 猜测顺序。
    const segmentPunches = orderedPunches.filter((punch) => punch.segmentIndex === effectiveSegmentIndex)
    const clockIns = segmentPunches.filter((punch) => punch.punchType === 'ClockIn')
    const clockOuts = segmentPunches.filter((punch) => punch.punchType === 'ClockOut')
    if (clockIns.length !== 1 || clockOuts.length > 1) return fail('SEGMENT_NOT_FOUND')
    segment = {
      segmentIndex: effectiveSegmentIndex,
      clockIn: clockIns[0],
      clockOut: clockOuts[0],
    }
  } else {
    const timestampCounts = new Map<number, number>()
    for (const punch of orderedPunches) {
      const timestamp = utcTimestamp(punch.punchTimeUtc)!
      timestampCounts.set(timestamp, (timestampCounts.get(timestamp) ?? 0) + 1)
    }
    // 无后端班段序号时，同刻记录无法复刻数据库 Id 次序，必须停止而不是混入错误班段。
    if ([...timestampCounts.values()].some((count) => count > 1)) {
      return fail('SEGMENT_NOT_FOUND')
    }
    segment = pairEffectivePunches(orderedPunches).find((candidate) => (
      normalizedKey(candidate.clockIn.punchGuid) === effectiveKey
      || normalizedKey(candidate.clockOut?.punchGuid ?? '') === effectiveKey
    ))
  }
  if (!segment) return fail('SEGMENT_NOT_FOUND')

  const startTimestamp = utcTimestamp(segment.clockIn.punchTimeUtc)
  const endTimestamp = utcTimestamp(segment.clockOut?.punchTimeUtc)
  if (startTimestamp === undefined || (segment.clockOut && endTimestamp === undefined)) {
    return fail('SEGMENT_NOT_FOUND')
  }

  const isOpen = !segment.clockOut
  const fromDate = punchLocalDate(segment.clockIn)
  const toDate = segment.clockOut
    ? punchLocalDate(segment.clockOut)
    : localDatePart(currentStoreLocalDate)
      ?? dateInTimeZone(nowUtc, selectedPunch.storeTimeZone)
  if (!fromDate || !toDate || toDate < fromDate || (isOpen && nowTimestamp < startTimestamp)) {
    return fail('INVALID_DATE_RANGE')
  }

  const segmentSamples = samples
    .filter((item) => sameText(item.userGuid, selectedPunch.userGuid))
    .filter((item) => !item.storeCode || sameText(item.storeCode, selectedPunch.storeCode))
    .map((item) => ({ item, timestamp: utcTimestamp(item.locationCapturedAtUtc) }))
    .filter((entry): entry is { item: AttendanceLocationSampleDto; timestamp: number } => (
      entry.timestamp !== undefined
      && entry.timestamp > startTimestamp
      && (isOpen ? entry.timestamp <= nowTimestamp : entry.timestamp < endTimestamp!)
    ))
    .sort((left, right) => (
      left.timestamp - right.timestamp
      || compareStableKey(left.item.sampleGuid, right.item.sampleGuid)
    ))

  const points = [
    punchPoint(segment.clockIn, 'ClockIn'),
    ...segmentSamples.map((entry) => samplePoint(entry.item)),
    ...(segment.clockOut ? [punchPoint(segment.clockOut, 'ClockOut')] : []),
  ].sort((left, right) => (
    utcTimestamp(left.capturedAtUtc)! - utcTimestamp(right.capturedAtUtc)!
      || ({ ClockIn: 0, Sample: 1, ClockOut: 2 }[left.kind]
        - { ClockIn: 0, Sample: 1, ClockOut: 2 }[right.kind])
      || compareStableKey(left.key, right.key)
  ))

  return {
    ok: true,
    segmentIndex: segment.segmentIndex,
    isOpen,
    clockIn: segment.clockIn,
    clockOut: segment.clockOut,
    fromDate,
    toDate,
    points,
    mapPoints: points.filter((point) => hasValidCoordinates(point.latitude, point.longitude)),
    sampleCount: segmentSamples.length,
  }
}
