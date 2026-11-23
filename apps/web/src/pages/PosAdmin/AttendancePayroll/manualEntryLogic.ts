import type {
  AttendanceHoursDayDto,
  AttendanceHoursEmployeeDto,
  AttendanceManualEntrySegmentPayload,
  AttendanceManualLeavePayload,
  AttendanceManualLeaveType,
} from '../../../types/attendancePayroll'

// 手动补录工时的纯逻辑（无界面依赖，便于单测）：表单行校验、请求拼装、哪些格子 / 日期可以补录、未打卡班汇总。
// 规则以后端为准（权限、周期锁定、可改窗口、重叠、班段数上限都在后端校验）；这里只做「点得开 / 填得完整」的前置判断。

/** 一段上下班时间（门店本地，HH:mm）。 */
export interface ManualEntryRow {
  start: string
  end: string
}

/** 补录原因最长 400（后端：补录排班备注要加前缀，备注列上限 500）。 */
export const MANUAL_ENTRY_REASON_MAX = 400

const CLOCK = /^([01]\d|2[0-3]):([0-5]\d)$/

/** HH:mm → 当天第几分钟；格式不对返回 null。 */
export function parseClock(value: string): number | null {
  const match = CLOCK.exec(value.trim())
  return match ? Number(match[1]) * 60 + Number(match[2]) : null
}

export type ManualRowsError = 'incomplete' | 'range' | 'overlap'

/**
 * 校验一天的班段：每段都填全且格式正确 → 下班晚于上班 → 按上班时间排序后互不重叠（下一段上班必须晚于上一段下班）。
 * 返回 null 表示可以去预览。
 */
export function validateManualRows(rows: readonly ManualEntryRow[]): ManualRowsError | null {
  if (rows.length === 0) return 'incomplete'
  const spans: { start: number; end: number }[] = []
  for (const row of rows) {
    const start = parseClock(row.start)
    const end = parseClock(row.end)
    if (start === null || end === null) return 'incomplete'
    if (end <= start) return 'range'
    spans.push({ start, end })
  }
  spans.sort((a, b) => a.start - b.start)
  for (let index = 1; index < spans.length; index += 1) {
    if (spans[index].start <= spans[index - 1].end) return 'overlap'
  }
  return null
}

/** 拼成后端要的班段：workDate 当天的本地时间 YYYY-MM-DDTHH:mm:00，按上班时间排序。 */
export function buildManualSegments(workDate: string, rows: readonly ManualEntryRow[]): AttendanceManualEntrySegmentPayload[] {
  return [...rows]
    .sort((a, b) => (parseClock(a.start) ?? 0) - (parseClock(b.start) ?? 0))
    .map((row) => ({ startLocal: `${workDate}T${row.start.trim()}:00`, endLocal: `${workDate}T${row.end.trim()}:00` }))
}

/** 后端错误文案是「CODE: 提示」，界面只显示提示。 */
export function manualEntryErrorText(error: unknown, fallback: string): string {
  const raw = error instanceof Error ? error.message : ''
  const text = raw.replace(/^[A-Z][A-Z0-9_]+:\s*/, '').trim()
  return text || fallback
}

/** 未打卡班（提示条与「逐个补录」用）。 */
export interface MissingPunchItem {
  userGuid: string
  userName: string
  date: string
  start?: string | null
  end?: string | null
}

/** 汇总所有「有排班、窗口已过、一次打卡都没有」的班，按日期、姓名排序。 */
export function collectMissingPunches(employees: readonly AttendanceHoursEmployeeDto[]): MissingPunchItem[] {
  const items: MissingPunchItem[] = []
  for (const employee of employees) {
    for (const day of employee.days) {
      if (!day.missingPunch) continue
      items.push({ userGuid: employee.userGuid, userName: employee.userName, date: day.date, start: day.scheduleStart, end: day.scheduleEnd })
    }
  }
  return items.sort((a, b) => a.date.localeCompare(b.date) || a.userName.localeCompare(b.userName))
}

/**
 * 补录抽屉用的工时数据必须就是「所看周期」的：只有周期起点和周数都对得上才返回，否则返回 null。
 * 在「工资表」页签翻期只会重载工资数据，工时数据（hours）可能还停在之前那一期；
 * 直接喂给抽屉会让日期可选范围、每天的状态、员工的排班全是旧一期的（比如切到上周后只能选本周的今天）。
 */
export function pickHoursInView<T extends { periodStart: string; weeks: number }>(
  hours: T | null | undefined,
  periodStart: string | undefined,
  weeks: number,
): T | null {
  if (!hours || !periodStart) return null
  return hours.periodStart === periodStart && hours.weeks === weeks ? hours : null
}

export type ManualEntryCellKind = 'missing' | 'addable' | 'none'

/**
 * 工时统计格子能不能补录：
 * - 页面没开放（没权限 / 本期已锁定）→ none；未来日期 → none；
 * - 当天已有打卡 / 已补录 / 有带薪假标记 / 已有工时 → none（已有打卡改走「修改打卡」，补录不覆盖）；
 * - 未打卡班 → missing（琥珀色「未打卡 ＋」，预填排班时间）；没排班也没打卡 → addable（悬停出现「＋ 补录」）。
 */
export function manualEntryCellKind(day: AttendanceHoursDayDto, options: { enabled: boolean; today: string }): ManualEntryCellKind {
  if (!options.enabled) return 'none'
  if (day.date > options.today) return 'none'
  if ((day.segments?.length ?? 0) > 0 || day.manualEntry) return 'none'
  if (day.leave && day.leave.type !== 'AnnualCashOut') return 'none'
  if (day.missingPunch) return 'missing'
  if ((day.paidHours ?? 0) > 0) return 'none'
  return 'addable'
}

export type ManualEntryDayState = 'ok' | 'future' | 'hasPunches' | 'leave'

/** 抽屉里日期按钮的状态：未来不可选；已有打卡 / 带薪假的日子不在这里改。 */
export function manualEntryDayState(day: AttendanceHoursDayDto | undefined, date: string, today: string): ManualEntryDayState {
  if (date > today) return 'future'
  if (!day) return 'ok'
  if ((day.segments?.length ?? 0) > 0 || day.manualEntry) return 'hasPunches'
  if (day.leave && day.leave.type !== 'AnnualCashOut') return 'leave'
  return 'ok'
}

/** 新开一天的默认班段：未打卡班预填排班时间，其它留空让人填（不替人猜时间）。 */
export function defaultManualRows(day?: AttendanceHoursDayDto): ManualEntryRow[] {
  return [{ start: day?.missingPunch ? day.scheduleStart ?? '' : '', end: day?.missingPunch ? day.scheduleEnd ?? '' : '' }]
}

/** 分钟 → 「5.5 h」文本。 */
export function manualPreviewHoursText(hours: number): string {
  return `${Math.round(hours * 100) / 100} h`
}

// ---------- 补录带薪假（病假 / 年假；年假发放已取消）----------

/** 补录抽屉的模式：补工时，或补两种带薪假之一。 */
export type ManualEntryMode = 'hours' | AttendanceManualLeaveType

export const MANUAL_ENTRY_MODES: readonly ManualEntryMode[] = ['hours', 'PaidSick', 'PaidAnnual']

/** 病假能补录的一天：员工「有排班、没打卡」的班（带排班时间，界面上显示「排 09:00–17:00」）。 */
export interface SickEligibleDay {
  date: string
  start?: string | null
  end?: string | null
}

/**
 * 病假只能补在「有排班、窗口已过、一次打卡都没有」的班上（与员工自己申请的规则一致：按排班算工时）。
 * 已被病假 / 年假占用的班后端会清掉未打卡标记，这里再防一层；未来的日子不能补。
 */
export function collectSickEligibleDays(employee: AttendanceHoursEmployeeDto | undefined, today: string): SickEligibleDay[] {
  if (!employee) return []
  return employee.days
    .filter((day) => day.missingPunch && !day.leave && day.date <= today)
    .map((day) => ({ date: day.date, start: day.scheduleStart, end: day.scheduleEnd }))
    .sort((a, b) => a.date.localeCompare(b.date))
}

/** 补录假的表单：病假选若干天，年假选一个起止区间，另填合计小时数、员工当天是否休息。 */
export interface ManualLeaveForm {
  sickDates: string[]
  range: [string, string] | null
  /** 年假的合计小时数（整段区间的总数）；null = 没手填，用后端预览返回的默认值（工作日 × 8 h）。 */
  totalHours: number | null
  /** 年假当天员工是否休息；默认 true，关掉表示照常上班、年假另外加算。 */
  rests: boolean
}

export const EMPTY_MANUAL_LEAVE_FORM: ManualLeaveForm = { sickDates: [], range: null, totalHours: null, rests: true }

/** 表单是否填到了可以去预览的程度（原因另算：预览用占位原因，保存才要真实原因）。 */
export function isManualLeaveFormReady(type: AttendanceManualLeaveType, form: ManualLeaveForm): boolean {
  return type === 'PaidSick' ? form.sickDates.length > 0 : form.range !== null
}

/** 年假日期选择：只能选本期内、且不晚于今天的日子（补录是事后记录；其余窗口规则以后端为准）。 */
export function isManualLeaveDateDisabled(date: string, periodStart: string | undefined, today: string): boolean {
  return date > today || (periodStart !== undefined && date < periodStart)
}

/** 拼成后端要的请求：病假带 dates，年假带起止日期（病假的起止日期取最早 / 最晚一天，后端以 dates 为准）。 */
export function buildManualLeavePayload(input: {
  storeCode: string
  userGuid: string
  type: AttendanceManualLeaveType
  form: ManualLeaveForm
  reason: string
}): AttendanceManualLeavePayload {
  const { storeCode, userGuid, type, form, reason } = input
  if (type === 'PaidSick') {
    const dates = [...form.sickDates].sort()
    return { storeCode, userGuid, leaveType: type, startDate: dates[0] ?? '', endDate: dates[dates.length - 1] ?? '', dates, reason }
  }
  // 年假：没手填合计就不传（后端按工作日 × 8 h）；不休息才显式传 false，休息沿用后端默认。
  return {
    storeCode,
    userGuid,
    leaveType: type,
    startDate: form.range?.[0] ?? '',
    endDate: form.range?.[1] ?? '',
    reason,
    ...(form.totalHours !== null ? { totalHours: form.totalHours } : {}),
    ...(form.rests ? {} : { restsOnLeaveDays: false }),
  }
}
