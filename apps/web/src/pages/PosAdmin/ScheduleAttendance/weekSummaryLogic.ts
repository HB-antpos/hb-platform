// 排班工时汇总（纯函数，便于单测）：单个班次的时长与计薪时长、一组班次的合计、工时文案。已取消的班次一律不计入。
import { getMealSummary, normalizeHm, toMealChoice } from './scheduleCellEditorLogic'

/** 汇总只需要班次的这几个字段，AttendanceScheduleDto 可直接传入。 */
export interface SummarySchedule {
  status?: string
  startTime: string
  endTime: string
  /** null / undefined＝按时长自动推算用餐次数。 */
  mealBreakCount?: number | null
}

export interface HoursSummary {
  /** 未取消的班次数。 */
  shifts: number
  /** 班次时长合计（小时，不扣用餐），和格子里时间区间对得上。 */
  hours: number
  /** 扣除用餐后的计薪时长合计（小时）。 */
  paidHours: number
}

const round2 = (value: number) => Math.round(value * 100) / 100

/** 单个班次的时长；结束不晚于开始（跨天等）时不下结论，时长按 0 计，但班次数照算。 */
export function getScheduleHours(schedule: SummarySchedule): { hours: number; paidHours: number } {
  const summary = getMealSummary(
    normalizeHm(schedule.startTime),
    normalizeHm(schedule.endTime),
    toMealChoice(schedule.mealBreakCount),
  )
  return summary ? { hours: summary.hours, paidHours: summary.paidHours } : { hours: 0, paidHours: 0 }
}

/** 一组班次的合计：取消的不算。 */
export function summarizeSchedules(schedules: readonly SummarySchedule[]): HoursSummary {
  let shifts = 0
  let hours = 0
  let paidHours = 0
  schedules.forEach((schedule) => {
    if (schedule.status === 'Cancelled') return
    const item = getScheduleHours(schedule)
    shifts += 1
    hours += item.hours
    paidHours += item.paidHours
  })
  return { shifts, hours: round2(hours), paidHours: round2(paidHours) }
}

/** 工时文案：8.5 → 8.5h，8 → 8h，0.25 → 0.25h（最多两位小数，去掉多余的 0）。 */
export function formatHours(hours: number): string {
  return `${Number(round2(hours).toFixed(2))}h`
}
