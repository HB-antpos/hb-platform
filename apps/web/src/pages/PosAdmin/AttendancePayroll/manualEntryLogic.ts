import type { AttendanceHoursDayDto, AttendanceHoursEmployeeDto, AttendanceManualEntrySegmentPayload } from '../../../types/attendancePayroll'

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
