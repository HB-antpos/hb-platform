// 排班看板「有无午饭」标识：纯函数，便于单测。规则直接复用班次的用餐扣除（getMealSummary），不改后端。
// 卡片（Web 看板）、图例和导出图片共用这一份判定，手机端有同一套规则的镜像（modules/attendance/meal-mark.ts）。
//
// 显示规则（与设计图一致）：
//   - 自动含午饭（满 4.5 小时扣 1 次、超过 9 小时扣 2 次）→ meal
//   - 手动指定了次数且 > 0 → meal，并带 manual 标志（虚线框）
//   - 满 4.5 小时的长班次却没有午饭（手动指定「不扣」）→ none：要一眼看出的例外
//   - 不到 4.5 小时的短班次本来就没有午饭 → hidden，避免满屏噪音
//   - 已取消、起止不合法 → hidden

import { getMealSummary, normalizeHm, toMealChoice } from './scheduleCellEditorLogic'

export type MealMarkKind = 'hidden' | 'meal' | 'none'

export interface MealMark {
  kind: MealMarkKind
  /** 实际生效的用餐次数（自动时按时长推算，手动时取手动值）。 */
  count: number
  /** 店长手动指定过次数（含「不扣」）。 */
  manual: boolean
  deductMinutes: number
  /** 班次总时长（分钟，不扣用餐）。 */
  durationMinutes: number
}

export interface MealMarkSource {
  startTime: string
  endTime: string
  /** null / undefined＝按时长自动；0–3＝手动指定次数。 */
  mealBreakCount?: number | null
  status?: string
}

/** 满 4.5 小时（含）才有午饭；也是「长班次无午饭」例外的判定线（与后端 AttendanceMealBreakRules 一致）。 */
export const LONG_SHIFT_MINUTES = 270

/** 叉勺图标的 SVG 路径（viewBox 0 0 24 24，描边 2.4、圆头圆角）；卡片、图例与导出图片共用。 */
export const MEAL_ICON_PATH = 'M6 3v7a2 2 0 0 0 4 0V3M8 10v11M17 3c-2.2 1.6-3 4-3 6.5 0 1.8 1 3 3 3.2V21'
/** 「无午饭」在叉勺上叠的斜杠。 */
export const MEAL_SLASH_PATH = 'M3 3l18 18'

const HIDDEN: MealMark = { kind: 'hidden', count: 0, manual: false, deductMinutes: 0, durationMinutes: 0 }

const toMinutes = (value: string): number | undefined => {
  const [hour, minute] = value.split(':').map(Number)
  return Number.isFinite(hour) && Number.isFinite(minute) ? hour * 60 + minute : undefined
}

export function getMealMark(source: MealMarkSource): MealMark {
  if (source.status === 'Cancelled') return HIDDEN
  const start = normalizeHm(source.startTime)
  const end = normalizeHm(source.endTime)
  const summary = getMealSummary(start, end, toMealChoice(source.mealBreakCount))
  const from = toMinutes(start)
  const to = toMinutes(end)
  if (!summary || from === undefined || to === undefined) return HIDDEN

  const durationMinutes = to - from
  const manual = !summary.isAuto
  const base = { count: summary.count, manual, deductMinutes: summary.deductMinutes, durationMinutes }
  if (summary.count > 0) return { kind: 'meal', ...base }
  // 没有午饭：只有长班次才值得提示（短班次本来就没有）
  return durationMinutes >= LONG_SHIFT_MINUTES ? { kind: 'none', ...base } : { ...HIDDEN, durationMinutes, manual }
}
