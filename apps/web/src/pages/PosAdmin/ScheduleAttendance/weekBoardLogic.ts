// 按天分列的周排班视图所需的纯逻辑：每天的班次排序、人数 / 工时汇总、员工图例、「可上班未排」名单。
import type { DayAvailability } from './availabilityLogic'
import { normalizeHm } from './scheduleCellEditorLogic'
import { summarizeSchedules } from './weekSummaryLogic'
import type { SummarySchedule } from './weekSummaryLogic'

export interface BoardSchedule extends SummarySchedule {
  scheduleGuid: string
  userGuid: string
  /** 接口里可能是 2026-11-16 或 2026-11-16T00:00:00，只取前 10 位。 */
  workDate: string
}

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true })

/**
 * 同一天内的排序：先按开始时间，开始时间相同按员工名，同名再按结束时间，最后按班次 ID 兜底（保证排序稳定、不抖动）。
 */
export function compareDayShifts(
  left: BoardSchedule,
  right: BoardSchedule,
  nameOf: (userGuid: string) => string,
): number {
  const byStart = normalizeHm(left.startTime).localeCompare(normalizeHm(right.startTime))
  if (byStart) return byStart
  const byName = collator.compare(nameOf(left.userGuid), nameOf(right.userGuid))
  if (byName) return byName
  const byEnd = normalizeHm(left.endTime).localeCompare(normalizeHm(right.endTime))
  if (byEnd) return byEnd
  return left.scheduleGuid.localeCompare(right.scheduleGuid)
}

export function sortDayShifts<T extends BoardSchedule>(list: readonly T[], nameOf: (userGuid: string) => string): T[] {
  return [...list].sort((left, right) => compareDayShifts(left, right, nameOf))
}

/** 把整周班次按日期分组并排好序；不在 dateKeys 里的日期（别的周的脏数据）丢弃。 */
export function groupSchedulesByDay<T extends BoardSchedule>(
  schedules: readonly T[],
  dateKeys: readonly string[],
  nameOf: (userGuid: string) => string,
): Map<string, T[]> {
  const result = new Map<string, T[]>(dateKeys.map((key) => [key, []]))
  schedules.forEach((schedule) => {
    result.get(schedule.workDate.slice(0, 10))?.push(schedule)
  })
  result.forEach((list, key) => result.set(key, sortDayShifts(list, nameOf)))
  return result
}

/** 某一天：有（未取消）班次的人数与工时合计；同一人多个班次只算 1 人。 */
export function summarizeDayList(list: readonly BoardSchedule[]): { people: number; hours: number; shifts: number } {
  const live = list.filter((item) => item.status !== 'Cancelled')
  return {
    people: new Set(live.map((item) => item.userGuid)).size,
    hours: summarizeSchedules(live).hours,
    shifts: live.length,
  }
}

export interface StaffLegendEntry {
  userGuid: string
  name: string
  /** 本周班次时长合计（不含已取消）。 */
  hours: number
  /** 扣除用餐后的计薪时长合计。 */
  paidHours: number
  /** 本周班次数（不含已取消）。 */
  shifts: number
}

/** 员工图例：本周出现过班次（含只有已取消班次）的每个人一项，按姓名排序。 */
export function buildStaffLegend(
  schedules: readonly BoardSchedule[],
  nameOf: (userGuid: string) => string,
): StaffLegendEntry[] {
  const byUser = new Map<string, BoardSchedule[]>()
  schedules.forEach((schedule) => {
    byUser.set(schedule.userGuid, [...(byUser.get(schedule.userGuid) ?? []), schedule])
  })
  return Array.from(byUser.entries())
    .map(([userGuid, list]) => {
      const total = summarizeSchedules(list)
      return { userGuid, name: nameOf(userGuid), hours: total.hours, paidHours: total.paidHours, shifts: total.shifts }
    })
    .sort((left, right) => collator.compare(left.name, right.name) || left.userGuid.localeCompare(right.userGuid))
}

/** 当天上报了「可上班」、但当天没有（未取消）班次的员工 ID。 */
export function findAvailableUnscheduled(
  dateKey: string,
  userGuids: readonly string[],
  dayShifts: readonly BoardSchedule[],
  availabilityOf: (userGuid: string, dateKey: string) => DayAvailability | undefined,
): string[] {
  const scheduled = new Set(dayShifts.filter((item) => item.status !== 'Cancelled').map((item) => item.userGuid))
  return userGuids.filter((userGuid) => !scheduled.has(userGuid) && (availabilityOf(userGuid, dateKey)?.available.length ?? 0) > 0)
}

/** 搜索框：姓名包含关键字（忽略大小写）的员工 ID；关键字为空返回空集合（表示不做高亮）。 */
export function matchStaffKeyword(
  users: readonly { userGuid: string; name?: string }[],
  keyword?: string,
): Set<string> {
  const needle = keyword?.trim().toLowerCase()
  if (!needle) return new Set()
  return new Set(users.filter((user) => (user.name ?? '').toLowerCase().includes(needle)).map((user) => user.userGuid))
}
