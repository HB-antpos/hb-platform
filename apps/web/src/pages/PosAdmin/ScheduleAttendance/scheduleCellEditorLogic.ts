import type { AttendanceScheduleStatus, SaveAttendanceSchedulePayload } from '../../../types/scheduleAttendance'

/** 周排班单元格就地编辑浮层的纯逻辑：常用班次推导、可同时应用的日期、保存载荷、姓名过滤。 */

export type ShiftPresetNameKey = 'morning' | 'midday' | 'evening' | 'fullDay'

export interface ShiftPreset {
  start: string
  end: string
  /** 仅「恰好等于内置四档」时才有名称；本店自己的常用时段只显示时间，避免乱贴早/中/晚标签。 */
  nameKey?: ShiftPresetNameKey
}

export const DEFAULT_SHIFT_PRESETS: ShiftPreset[] = [
  { start: '07:00', end: '15:00', nameKey: 'morning' },
  { start: '11:00', end: '19:00', nameKey: 'midday' },
  { start: '15:00', end: '23:00', nameKey: 'evening' },
  { start: '09:00', end: '17:00', nameKey: 'fullDay' },
]

/** 与原「新建排班」抽屉的默认时段保持一致。 */
export const FALLBACK_SHIFT: Pick<ShiftPreset, 'start' | 'end'> = { start: '09:00', end: '17:00' }
export const MAX_SHIFT_PRESETS = 4

interface ShiftLike {
  startTime: string
  endTime: string
  status: AttendanceScheduleStatus
}

/** 接口时间可能是 HH:mm:ss，统一收敛成 HH:mm。 */
export function normalizeHm(value?: string): string {
  return (value ?? '').slice(0, 5)
}

function presetKey(start: string, end: string) {
  return `${start}-${end}`
}

function withName(start: string, end: string): ShiftPreset {
  const matched = DEFAULT_SHIFT_PRESETS.find((preset) => preset.start === start && preset.end === end)
  return matched ? { ...matched } : { start, end }
}

/** 统计当前周内出现次数最多的时段（已取消的不算），次数相同按开始时间升序，保证结果稳定。 */
function rankShiftsByFrequency(schedules: ShiftLike[]): ShiftPreset[] {
  const counter = new Map<string, { start: string; end: string; count: number }>()
  schedules.forEach((schedule) => {
    if (schedule.status === 'Cancelled') return
    const start = normalizeHm(schedule.startTime)
    const end = normalizeHm(schedule.endTime)
    if (!start || !end) return
    const key = presetKey(start, end)
    const entry = counter.get(key) ?? { start, end, count: 0 }
    entry.count += 1
    counter.set(key, entry)
  })
  return Array.from(counter.values())
    .sort((left, right) => right.count - left.count || left.start.localeCompare(right.start) || left.end.localeCompare(right.end))
    .map((entry) => withName(entry.start, entry.end))
}

/** 本店常用班次：本周最常用的时段优先，不足 4 个时用内置四档补齐。 */
export function buildShiftPresets(schedules: ShiftLike[]): ShiftPreset[] {
  const ranked = rankShiftsByFrequency(schedules).slice(0, MAX_SHIFT_PRESETS)
  const seen = new Set(ranked.map((preset) => presetKey(preset.start, preset.end)))
  const padded = DEFAULT_SHIFT_PRESETS.filter((preset) => !seen.has(presetKey(preset.start, preset.end)))
  return [...ranked, ...padded].slice(0, MAX_SHIFT_PRESETS)
}

/** 新建班次时的默认时段：本周最常用的；没有历史就用抽屉原来的 09:00-17:00。 */
export function pickDefaultShift(schedules: ShiftLike[]): Pick<ShiftPreset, 'start' | 'end'> {
  const [top] = rankShiftsByFrequency(schedules)
  return top ? { start: top.start, end: top.end } : { ...FALLBACK_SHIFT }
}

/** 该员工本周已有（未取消）班次的日期，同时应用时这些日期置灰，避免一键铺出同日重叠班次。 */
export function getOccupiedDateKeys(schedulesByDate: Record<string, { status: AttendanceScheduleStatus }[]>): Set<string> {
  const occupied = new Set<string>()
  Object.entries(schedulesByDate).forEach(([dateKey, list]) => {
    if (list.some((schedule) => schedule.status !== 'Cancelled')) occupied.add(dateKey)
  })
  return occupied
}

/** 保存后跳到「右侧下一个空白格」：跳过已有班次的日期和本次刚一并写入的日期。 */
export function getNextEmptyDateKey(
  currentDateKey: string,
  weekDateKeys: string[],
  occupied: Set<string>,
  justSaved: string[] = [],
): string | undefined {
  const currentIndex = weekDateKeys.indexOf(currentDateKey)
  if (currentIndex < 0) return undefined
  const skip = new Set(justSaved)
  return weekDateKeys.slice(currentIndex + 1).find((dateKey) => !occupied.has(dateKey) && !skip.has(dateKey))
}

interface BuildPayloadsInput {
  storeCode: string
  userGuid: string
  dateKeys: string[]
  start: string
  end: string
  remark?: string
  status: AttendanceScheduleStatus
}

/** 同一时段 × 多个日期 → 多条保存载荷（日期去重并按时间排序，备注去空白）。 */
export function buildScheduleSavePayloads(input: BuildPayloadsInput): SaveAttendanceSchedulePayload[] {
  const remark = input.remark?.trim() || undefined
  return Array.from(new Set(input.dateKeys))
    .sort()
    .map((workDate) => ({
      storeCode: input.storeCode,
      userGuid: input.userGuid.trim(),
      workDate,
      startTime: input.start,
      endTime: input.end,
      status: input.status,
      remark,
    }))
}

/** 本地姓名搜索：按姓名或 GUID 子串匹配（忽略大小写），空关键字原样返回。 */
export function filterRowsByKeyword<T extends { userName?: string; userGuid: string }>(rows: T[], keyword?: string): T[] {
  const needle = keyword?.trim().toLowerCase()
  if (!needle) return rows
  return rows.filter((row) => row.userName?.toLowerCase().includes(needle) || row.userGuid.toLowerCase().includes(needle))
}

/** 时长（小时）；结束不晚于开始（跨天等）时不下结论，返回 undefined。 */
export function getShiftHours(start: string, end: string): number | undefined {
  const toMinutes = (value: string) => {
    const [hour, minute] = value.split(':').map(Number)
    return Number.isFinite(hour) && Number.isFinite(minute) ? hour * 60 + minute : undefined
  }
  const from = toMinutes(start)
  const to = toMinutes(end)
  if (from === undefined || to === undefined || to <= from) return undefined
  return Math.round(((to - from) / 60) * 10) / 10
}
