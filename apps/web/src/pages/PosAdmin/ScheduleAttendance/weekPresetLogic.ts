// 「上周 / 本周 / 下周」快捷周（考勤记录、考勤审核共用）：周一到周日，与周排班的周一致。
// 纯日期字符串计算（UTC），不依赖 dayjs / 本地时区，便于单元测试。

export type WeekPreset = 'previous' | 'current' | 'next'

export const WEEK_PRESETS: readonly WeekPreset[] = ['previous', 'current', 'next']

/** 文案键（字面量，供页面文案契约测试扫描）。 */
export const WEEK_PRESET_LABEL_KEYS: Readonly<Record<WeekPreset, string>> = {
  previous: 'posAdmin.scheduleAttendance.weekNav.lastWeek',
  current: 'posAdmin.scheduleAttendance.weekNav.thisWeek',
  next: 'posAdmin.scheduleAttendance.weekNav.nextWeek',
}

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/

function toUtc(value: string): Date | null {
  const match = ISO_DATE.exec(value)
  if (!match) return null
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])))
  return date.getUTCDate() === Number(match[3]) ? date : null
}

function format(date: Date): string {
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}-${String(date.getUTCDate()).padStart(2, '0')}`
}

/** 某天所在周的周一（yyyy-MM-dd）；非法日期返回 null。 */
export function mondayOf(day: string): string | null {
  const date = toUtc(day)
  if (!date) return null
  // getUTCDay：周日 0 … 周六 6；周日要退回 6 天
  const back = (date.getUTCDay() + 6) % 7
  date.setUTCDate(date.getUTCDate() - back)
  return format(date)
}

/** 快捷周的起止日（含首尾）；today 非法返回 null。 */
export function weekPresetRange(preset: WeekPreset, today: string): [string, string] | null {
  const monday = mondayOf(today)
  const start = monday ? toUtc(monday) : null
  if (!start) return null
  const offset = preset === 'previous' ? -7 : preset === 'next' ? 7 : 0
  start.setUTCDate(start.getUTCDate() + offset)
  const end = new Date(start)
  end.setUTCDate(end.getUTCDate() + 6)
  return [format(start), format(end)]
}

/** 当前范围恰好是某个快捷周时返回它（高亮用）；自定义范围返回 undefined。 */
export function activeWeekPreset(from: string | undefined, to: string | undefined, today: string): WeekPreset | undefined {
  if (!from || !to) return undefined
  return WEEK_PRESETS.find((preset) => {
    const range = weekPresetRange(preset, today)
    return range !== null && range[0] === from && range[1] === to
  })
}

/** 日期是否在范围内（含首尾）；字符串比较对 yyyy-MM-dd 成立。 */
export function isDateInRange(day: string | undefined, from: string, to: string): boolean {
  if (!day) return false
  const value = day.slice(0, 10)
  return value >= from && value <= to
}
