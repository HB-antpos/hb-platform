// 排班「有无午饭」标识的判定（纯函数，与 Web 周排班看板同一套规则；卡片与导出图共用）。
// 规则沿用现有用餐扣除：满 4.5 小时（含）默认 1 次，超过 9 小时 2 次，每次 30 分钟；
// 店长手动指定（0–3 次，含 0＝不扣）优先于默认。
// 标识只在「有意义」时出现：
// - meal：扣了午饭（count > 0），手动指定过的带 manual 标志；
// - none：长班次（满 4.5 小时）却没有午饭（手动设成 0），这是要一眼看出的例外；
// - hidden：已取消、时间不合法、或不到 4.5 小时本来就没有午饭的短班次——不加标识，避免满屏噪音。
import { defaultMealBreakCount, MEAL_BREAK_MINUTES } from "./attendance-my-week";
import { normalizeClockTime } from "./schedule-grid";

export type MealMarkKind = "hidden" | "meal" | "none";

export interface MealMark {
  kind: MealMarkKind;
  /** 实际生效的用餐次数（自动时按时长推算）。 */
  count: number;
  /** 是否店长手动指定过次数（含 0）。 */
  manual: boolean;
  deductMinutes: number;
  durationMinutes: number;
}

export interface MealMarkInput {
  startTime: string;
  endTime: string;
  /** null / undefined＝按时长自动；0–3＝手动指定。 */
  mealBreakCount?: number | null;
  status?: string | null;
}

/** 满 4.5 小时（含）才有午饭可言；手动设成「不扣」的班次时长达到它才算「长班次无午饭」。 */
export const MEAL_LONG_SHIFT_MINUTES = 270;

/** 不标识：已取消 / 时间不合法 / 短班次 / 请假。 */
export const HIDDEN_MEAL_MARK: MealMark = { kind: "hidden", count: 0, manual: false, deductMinutes: 0, durationMinutes: 0 };

/** "HH:mm[:ss]" 或带 T 的时间 → 当天分钟数；解析不了返回 undefined。 */
function toMinutes(value: string): number | undefined {
  const normalized = normalizeClockTime(value);
  if (!normalized) return undefined;
  const [hour, minute] = normalized.split(":").map(Number);
  return Number.isFinite(hour) && Number.isFinite(minute) ? hour * 60 + minute : undefined;
}

export function getMealMark(input: MealMarkInput): MealMark {
  if ((input.status ?? "").toLowerCase() === "cancelled") return HIDDEN_MEAL_MARK;
  const start = toMinutes(input.startTime);
  const end = toMinutes(input.endTime);
  // 结束不晚于开始（跨午夜等）与 Web 一样不下结论
  if (start === undefined || end === undefined || end <= start) return HIDDEN_MEAL_MARK;

  const durationMinutes = end - start;
  const specified = input.mealBreakCount;
  // 只认 0–3 的整数为手动指定；其它值（脏数据）按自动处理，与 Web 的 toMealChoice 一致
  const manual = specified === 0 || specified === 1 || specified === 2 || specified === 3;
  const count = manual ? (specified as number) : defaultMealBreakCount(durationMinutes);
  const deductMinutes = count * MEAL_BREAK_MINUTES;

  if (count > 0) return { kind: "meal", count, manual, deductMinutes, durationMinutes };
  if (durationMinutes >= MEAL_LONG_SHIFT_MINUTES) return { kind: "none", count: 0, manual, deductMinutes: 0, durationMinutes };
  return { ...HIDDEN_MEAL_MARK, manual, durationMinutes };
}

// ---------- 图标（卡片与导出图共用同一份图形，保证长得一样） ----------

/** 叉勺图标（viewBox 0 0 24 24，描边图形，圆头圆角，线宽 2.4）。 */
export const MEAL_ICON_PATH = "M6 3v7a2 2 0 0 0 4 0V3M8 10v11M17 3c-2.2 1.6-3 4-3 6.5 0 1.8 1 3 3 3.2V21";
/** 「无午饭」叠在叉勺上的斜杠（线宽 2.6）。 */
export const MEAL_SLASH_PATH = "M3 3l18 18";

/** 「长班次无午饭」的橙色：文字 / 图标、底、虚线框。 */
export const MEAL_NONE_TONE = { text: "#AD4E00", background: "#FFF7E6", border: "#FFA940" } as const;
