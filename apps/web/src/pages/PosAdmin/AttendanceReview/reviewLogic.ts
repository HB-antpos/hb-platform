import dayjs from 'dayjs'
import type { AttendanceApprovalDayPreviewDto, AttendanceTierHours } from '../../../types/attendancePayroll'
import type { AttendanceApprovalDto } from '../../../types/scheduleAttendance'

/**
 * 审核分类（筛选标签与类型标签）：请假按假种细分，新增的带薪病假、年假、年假发放单独成类；
 * 带薪公假是独立来源 PublicHolidayPay。
 */
export const REVIEW_CATEGORIES = [
  'PunchAdjustment',
  'Overtime',
  'MissingClockOut',
  'MealBreak',
  'Leave',
  'Punch',
  'PaidSick',
  'PaidAnnual',
  'AnnualCashOut',
  'PublicHolidayPay',
] as const
export type ReviewCategory = (typeof REVIEW_CATEGORIES)[number]

/** 只有不需要填数的类型能批量：补卡、请假（不带薪）、打卡异常；加班、用餐、漏下班和带薪类逐条处理。 */
const BATCHABLE_CATEGORIES: ReadonlySet<ReviewCategory> = new Set(['PunchAdjustment', 'Leave', 'Punch'])

/** 系统生成的待处理，提交人显示「系统」。 */
const SYSTEM_SOURCE_TYPES: ReadonlySet<string> = new Set(['MissingClockOut', 'Overtime', 'Punch', 'PublicHolidayPay'])

export function getReviewCategory(item: Pick<AttendanceApprovalDto, 'sourceType' | 'leave'>): ReviewCategory | 'Other' {
  if (item.sourceType === 'Leave') {
    const leaveType = item.leave?.leaveType
    if (leaveType === 'PaidSick' || leaveType === 'PaidAnnual' || leaveType === 'AnnualCashOut') return leaveType
    return 'Leave'
  }
  return (REVIEW_CATEGORIES as readonly string[]).includes(item.sourceType) ? item.sourceType as ReviewCategory : 'Other'
}

export function isBatchableCategory(category: ReviewCategory | 'Other'): boolean {
  return category !== 'Other' && BATCHABLE_CATEGORIES.has(category)
}

export function isSystemGenerated(item: Pick<AttendanceApprovalDto, 'sourceType'>): boolean {
  return SYSTEM_SOURCE_TYPES.has(item.sourceType)
}

export function isSelfRequest(item: Pick<AttendanceApprovalDto, 'applicantUserGuid'>, currentUserGuid?: string): boolean {
  return Boolean(currentUserGuid) && item.applicantUserGuid.toLowerCase() === currentUserGuid!.toLowerCase()
}

/** 本人申请默认不能审；持 Attendance.Approval.ReviewSelf 才能逐条审（仍不能批量）。 */
export function canReviewItem(
  item: Pick<AttendanceApprovalDto, 'applicantUserGuid' | 'reviewStatus'>,
  currentUserGuid: string | undefined,
  canReviewOwn: boolean,
): boolean {
  if (item.reviewStatus !== 'Pending') return false
  return !isSelfRequest(item, currentUserGuid) || canReviewOwn
}

export function canSelectForBatch(item: AttendanceApprovalDto, currentUserGuid?: string): boolean {
  return item.reviewStatus === 'Pending'
    && !isSelfRequest(item, currentUserGuid)
    && isBatchableCategory(getReviewCategory(item))
}

export function countByCategory(items: AttendanceApprovalDto[]): Map<ReviewCategory | 'Other', number> {
  const counts = new Map<ReviewCategory | 'Other', number>()
  for (const item of items) {
    const category = getReviewCategory(item)
    counts.set(category, (counts.get(category) ?? 0) + 1)
  }
  return counts
}

/** 处理完一条后选中下一条：优先原位置的后一条，没有就前一条。 */
export function pickNextApprovalGuid(items: AttendanceApprovalDto[], handledGuid: string): string | undefined {
  const index = items.findIndex((item) => item.approvalGuid === handledGuid)
  const remaining = items.filter((item) => item.approvalGuid !== handledGuid)
  if (!remaining.length) return undefined
  if (index < 0) return remaining[0].approvalGuid
  return remaining[Math.min(index, remaining.length - 1)].approvalGuid
}

/** 工作日对应的时薪档（周六 / 周日按实际工时，用各自时薪；公假由后端在预览里给出）。 */
export function tierOfWorkDate(workDate?: string, previewTier?: keyof AttendanceTierHours): keyof AttendanceTierHours | undefined {
  if (previewTier) return previewTier
  if (!workDate) return undefined
  const weekday = dayjs(workDate).day()
  if (weekday === 6) return 'saturday'
  if (weekday === 0) return 'sunday'
  return 'weekday'
}

export type SubmittedTime =
  | { kind: 'justNow' }
  | { kind: 'minutesAgo'; value: number }
  | { kind: 'hoursAgo'; value: number }
  | { kind: 'yesterday'; time: string }
  | { kind: 'date'; date: string; time: string }

/** 提交时间：1 小时内按分钟、当天按小时、昨天带时刻，再早显示日期。 */
export function describeSubmittedTime(createdAt: string | undefined, now: dayjs.Dayjs = dayjs()): SubmittedTime | undefined {
  if (!createdAt) return undefined
  const created = dayjs(createdAt)
  if (!created.isValid()) return undefined
  const minutes = now.diff(created, 'minute')
  if (minutes < 2) return { kind: 'justNow' }
  if (minutes < 60) return { kind: 'minutesAgo', value: minutes }
  if (created.isSame(now, 'day') || minutes < 12 * 60) return { kind: 'hoursAgo', value: Math.floor(minutes / 60) }
  if (created.isSame(now.subtract(1, 'day'), 'day')) return { kind: 'yesterday', time: created.format('HH:mm') }
  return { kind: 'date', date: created.format('MM-DD'), time: created.format('HH:mm') }
}

// ===== 当天时间线 =====

export interface TimelineBar {
  left: number
  width: number
  kind: 'schedule' | 'actual' | 'auto' | 'proposed'
}

export interface TimelineModel {
  ticks: number[]
  schedule?: TimelineBar
  actual: TimelineBar[]
}

const toMinutes = (text: string) => {
  const [hour, minute] = text.split(':').map(Number)
  return hour * 60 + (minute || 0)
}

/**
 * 把排班、现有班段、批准后班段换算成百分比位置；刻度每 2 小时一格。
 * 系统自动下班（24:00）那段画成灰色；被批准后替换的段用绿色虚线覆盖在上面。
 */
export function buildTimelineModel(preview: AttendanceApprovalDayPreviewDto): TimelineModel | undefined {
  const points: number[] = []
  if (preview.scheduleStart) points.push(toMinutes(preview.scheduleStart))
  if (preview.scheduleEnd) points.push(toMinutes(preview.scheduleEnd))
  for (const segment of [...preview.segments, ...(preview.proposedSegments ?? [])]) {
    points.push(toMinutes(segment.start))
    // 自动下班段只画到排班结束后 1 小时，避免整条轴被拉到午夜
    if (!('isSystemAutoClockOut' in segment && segment.isSystemAutoClockOut)) points.push(toMinutes(segment.end))
  }
  if (!points.length) return undefined
  const startHour = Math.max(0, Math.floor(Math.min(...points) / 60 / 2) * 2)
  const endHour = Math.min(24, Math.max(startHour + 4, Math.ceil(Math.max(...points) / 60 / 2) * 2 + (preview.segments.some((segment) => segment.isSystemAutoClockOut) ? 2 : 0)))
  const span = (endHour - startHour) * 60
  const position = (start: number, end: number, kind: TimelineBar['kind']): TimelineBar => {
    const clampedStart = Math.max(start, startHour * 60)
    const clampedEnd = Math.min(end, endHour * 60)
    return {
      left: ((clampedStart - startHour * 60) / span) * 100,
      width: Math.max(0, ((clampedEnd - clampedStart) / span) * 100),
      kind,
    }
  }
  const proposedStarts = new Set((preview.proposedSegments ?? []).map((segment) => segment.start))
  const actual: TimelineBar[] = []
  for (const segment of preview.segments) {
    // 被批准后替换的段不再画原样，避免和绿色虚线重叠
    if (proposedStarts.has(segment.start)) continue
    actual.push(position(toMinutes(segment.start), toMinutes(segment.end), segment.isSystemAutoClockOut ? 'auto' : 'actual'))
  }
  for (const segment of preview.proposedSegments ?? []) {
    actual.push(position(toMinutes(segment.start), toMinutes(segment.end), 'proposed'))
  }
  return {
    ticks: Array.from({ length: (endHour - startHour) / 2 + 1 }, (_, index) => startHour + index * 2),
    schedule: preview.scheduleStart && preview.scheduleEnd
      ? position(toMinutes(preview.scheduleStart), toMinutes(preview.scheduleEnd), 'schedule')
      : undefined,
    actual,
  }
}

/** 头像缩写：取前两个词首字母（Jack Nguyen → JN）。 */
export function initialsOf(name?: string): string {
  const words = (name ?? '').trim().split(/\s+/).filter(Boolean)
  if (!words.length) return '?'
  return words.slice(0, 2).map((word) => word.charAt(0).toUpperCase()).join('')
}

const AVATAR_COLORS = ['#6941C6', '#1570EF', '#0E9384', '#C11574', '#4E5BA6', '#DC6803', '#3538CD', '#099250']

/** 按姓名稳定取色，同一个人在审核、工时、工资三处颜色一致。 */
export function avatarColorOf(seed?: string): string {
  let hash = 0
  for (const char of seed ?? '') hash = (hash * 31 + char.charCodeAt(0)) >>> 0
  return AVATAR_COLORS[hash % AVATAR_COLORS.length]
}
