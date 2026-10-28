import type { AttendanceScheduleStatus, SaveAttendanceSchedulePayload } from '../../../types/scheduleAttendance'

/** 周排班单元格就地编辑浮层的纯逻辑：固定常用班次、用餐扣除、可同时应用的日期、保存载荷、姓名过滤。 */

export interface ShiftPreset {
  start: string
  end: string
}

/** 固定的常用班次（顺序即界面顺序）。不再按本周出现次数推导，所有门店一致，拖拽托盘也用这一组。 */
export const SHIFT_PRESETS: ShiftPreset[] = [
  { start: '09:00', end: '13:00' },
  { start: '16:00', end: '17:30' },
  { start: '09:00', end: '17:00' },
  { start: '09:00', end: '21:00' },
  { start: '09:00', end: '19:00' },
]

/** 新建班次时浮层默认填入的时段。 */
export const DEFAULT_SHIFT: ShiftPreset = { start: '09:00', end: '17:30' }

/** 接口时间可能是 HH:mm:ss，统一收敛成 HH:mm。 */
export function normalizeHm(value?: string): string {
  return (value ?? '').slice(0, 5)
}

/** 用餐扣除的选择：auto＝按班次时长自动（不存值），数字＝店长手动指定次数（0＝不扣）。 */
export type MealChoice = 'auto' | 0 | 1 | 2 | 3

/** 每次用餐扣除的分钟数，与后端 AttendanceMealBreakRules.MealBreakMinutes 一致。 */
export const MEAL_BREAK_MINUTES = 30

const toMinutes = (value: string) => {
  const [hour, minute] = value.split(':').map(Number)
  return Number.isFinite(hour) && Number.isFinite(minute) ? hour * 60 + minute : undefined
}

/** 按班次时长推算的默认用餐次数；门槛均为「超过」，正好 4.5 / 9 小时按低一档（与后端、手机端同一规则）。 */
export function defaultMealBreakCount(durationMinutes: number): number {
  if (durationMinutes > 9 * 60) return 2
  if (durationMinutes > 270) return 1
  return 0
}

/** 排班记录里的 mealBreakCount（null / undefined＝自动）转成界面选择。 */
export function toMealChoice(mealBreakCount?: number | null): MealChoice {
  return mealBreakCount === 0 || mealBreakCount === 1 || mealBreakCount === 2 || mealBreakCount === 3 ? mealBreakCount : 'auto'
}

export interface MealSummary {
  /** 班次时长（小时，保留 2 位小数）。 */
  hours: number
  /** 实际生效的用餐次数（自动时按时长推算）。 */
  count: number
  isAuto: boolean
  deductMinutes: number
  /** 扣除用餐后的计薪时长（小时）。 */
  paidHours: number
}

/** 浮层里「X 小时 − 用餐 N 分钟 = 计薪 Y 小时」那一行的数据；起止不合法（结束不晚于开始）返回 undefined。 */
export function getMealSummary(start: string, end: string, choice: MealChoice): MealSummary | undefined {
  const from = toMinutes(start)
  const to = toMinutes(end)
  if (from === undefined || to === undefined || to <= from) return undefined
  const minutes = to - from
  const isAuto = choice === 'auto'
  const count = isAuto ? defaultMealBreakCount(minutes) : choice
  const deductMinutes = count * MEAL_BREAK_MINUTES
  const round = (value: number) => Math.round(value * 100) / 100
  return { hours: round(minutes / 60), count, isAuto, deductMinutes, paidHours: round((minutes - deductMinutes) / 60) }
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
  /** 用餐选择；不传＝不带用餐字段（新建时后端按自动算、更新时后端保持原值）。 */
  mealChoice?: MealChoice
  /** 更新已有班次时：选「自动」要显式带上恢复自动标记，后端才会把原来手动指定的次数清掉。 */
  resetAutoMeal?: boolean
}

/** 同一时段 × 多个日期 → 多条保存载荷（日期去重并按时间排序，备注去空白）。 */
export function buildScheduleSavePayloads(input: BuildPayloadsInput): SaveAttendanceSchedulePayload[] {
  const remark = input.remark?.trim() || undefined
  const choice = input.mealChoice
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
      ...(typeof choice === 'number' ? { mealBreakCount: choice } : {}),
      ...(choice === 'auto' && input.resetAutoMeal ? { resetMealBreakCount: true } : {}),
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
  const from = toMinutes(start)
  const to = toMinutes(end)
  if (from === undefined || to === undefined || to <= from) return undefined
  return Math.round(((to - from) / 60) * 10) / 10
}
