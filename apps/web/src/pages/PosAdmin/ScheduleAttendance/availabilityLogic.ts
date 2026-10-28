import type { AttendanceAvailabilityDto } from '../../../types/scheduleAttendance'
import { normalizeHm } from './scheduleCellEditorLogic'

/** 周排班格内「可上班 / 不能上班」状态行的纯逻辑：按员工 + 日期归并、同类时段合并、班次冲突判定。 */

export interface AvailabilitySegment {
  /** 开始/结束（分钟，0~1440），仅用于比较；展示用 startLabel / endLabel。 */
  startMin: number
  endMin: number
  startLabel: string
  endLabel: string
  /** 00:00 ~ 23:59(:59)，员工端「全天」与带薪年假回写都用这个口径。 */
  allDay: boolean
  remark?: string
}

export interface DayAvailability {
  available: AvailabilitySegment[]
  unavailable: AvailabilitySegment[]
}

/** userGuid → 日期(YYYY-MM-DD) → 当天的可用 / 不可用时段。 */
export type AvailabilityIndex = Map<string, Map<string, DayAvailability>>

export type ScheduleAvailabilityConflict = 'unavailable' | 'outsideAvailable'

/** 接口时间是 HH:mm 或 HH:mm:ss，换算成分钟；秒数不足一分钟按小数保留，让 23:59:59 仍然 ≥ 1439。 */
export function timeToMinutes(value?: string): number | undefined {
  const matched = /^(\d{1,2}):(\d{2})(?::(\d{2}))?/.exec((value ?? '').trim())
  if (!matched) return undefined
  const hours = Number(matched[1])
  const minutes = Number(matched[2])
  const seconds = matched[3] ? Number(matched[3]) : 0
  return hours * 60 + minutes + seconds / 60
}

const ALL_DAY_END_MIN = 23 * 60 + 59

function toSegment(item: AttendanceAvailabilityDto): AvailabilitySegment | undefined {
  const startMin = timeToMinutes(item.startTime)
  const endMin = timeToMinutes(item.endTime)
  if (startMin === undefined || endMin === undefined || endMin <= startMin) return undefined
  return {
    startMin,
    endMin,
    startLabel: normalizeHm(item.startTime),
    endLabel: normalizeHm(item.endTime),
    allDay: startMin === 0 && endMin >= ALL_DAY_END_MIN,
    remark: item.remark?.trim() || undefined,
  }
}

/** 重叠或首尾相接的同类时段合并成一段（员工多次提交时避免格内堆叠）；保留最先出现的备注。 */
export function mergeSegments(segments: AvailabilitySegment[]): AvailabilitySegment[] {
  const sorted = [...segments].sort((left, right) => left.startMin - right.startMin || left.endMin - right.endMin)
  const merged: AvailabilitySegment[] = []
  sorted.forEach((segment) => {
    const last = merged[merged.length - 1]
    if (last && segment.startMin <= last.endMin) {
      if (segment.endMin > last.endMin) {
        last.endMin = segment.endMin
        last.endLabel = segment.endLabel
      }
      last.allDay = last.startMin === 0 && last.endMin >= ALL_DAY_END_MIN
      last.remark = last.remark ?? segment.remark
      return
    }
    merged.push({ ...segment })
  })
  return merged
}

/**
 * 把可用时间记录整理成「员工 → 日期」索引。已取消的记录不算；
 * 日期取 availableDate 的前 10 位（接口可能带 T00:00:00），避免时区换算把日期挪一天。
 */
export function buildAvailabilityIndex(items: AttendanceAvailabilityDto[]): AvailabilityIndex {
  const raw = new Map<string, Map<string, DayAvailability>>()
  items.forEach((item) => {
    if (item.status === 'Cancelled' || !item.userGuid) return
    const dateKey = (item.availableDate ?? '').slice(0, 10)
    const segment = toSegment(item)
    if (!dateKey || !segment) return
    const byDate = raw.get(item.userGuid) ?? new Map<string, DayAvailability>()
    const day = byDate.get(dateKey) ?? { available: [], unavailable: [] }
    // 同一张表区分两类：isUnavailable=true 才是不能上班，缺省按可上班。
    ;(item.isUnavailable ? day.unavailable : day.available).push(segment)
    byDate.set(dateKey, day)
    raw.set(item.userGuid, byDate)
  })
  raw.forEach((byDate) => {
    byDate.forEach((day) => {
      day.available = mergeSegments(day.available)
      day.unavailable = mergeSegments(day.unavailable)
    })
  })
  return raw
}

export function getDayAvailability(index: AvailabilityIndex, userGuid: string, dateKey: string): DayAvailability | undefined {
  const day = index.get(userGuid)?.get(dateKey)
  return day && (day.available.length || day.unavailable.length) ? day : undefined
}

/**
 * 班次与员工上报的冲突：
 * - 与任一「不能上班」时段有重叠 → unavailable（优先）；
 * - 员工当天上报过「可上班」时段，但班次没有被某一段完整覆盖 → outsideAvailable；
 * - 没上报或只上报了不能上班：不做判断（没上报不等于不能上班）。
 * 已取消的班次、跨午夜（结束 ≤ 开始）的班次不判断。
 */
export function getScheduleAvailabilityConflict(
  schedule: { startTime: string; endTime: string; status?: string },
  day?: DayAvailability,
): ScheduleAvailabilityConflict | undefined {
  if (!day || schedule.status === 'Cancelled') return undefined
  const start = timeToMinutes(schedule.startTime)
  const end = timeToMinutes(schedule.endTime)
  if (start === undefined || end === undefined || end <= start) return undefined

  if (day.unavailable.some((segment) => start < segment.endMin && end > segment.startMin)) return 'unavailable'
  if (day.available.length && !day.available.some((segment) => start >= segment.startMin && end <= segment.endMin)) {
    return 'outsideAvailable'
  }
  return undefined
}
