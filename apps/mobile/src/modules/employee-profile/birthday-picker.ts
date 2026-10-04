// 纯 JS 生日选择器的日期运算：年/月/日三列联动，保证永远拼不出非法或未来日期。
// 格式校验与年龄计算仍由 birthday.ts 负责，这里只处理选择器的候选项与联动。
import { MIN_BIRTH_YEAR, normalizeBirthday } from "./birthday";

export interface BirthdayParts {
  year: number;
  month: number;
  day: number;
}

/** 首次打开且没有生日时的默认定位：今年往前 25 年的 1 月 1 日，减少滚动距离。 */
const DEFAULT_AGE_OFFSET = 25;

function todayParts(today: Date): BirthdayParts {
  return { year: today.getFullYear(), month: today.getMonth() + 1, day: today.getDate() };
}

export function getDaysInMonth(year: number, month: number) {
  // Date.UTC 的第 0 天是上个月最后一天，天然处理闰年。
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

/** 年份候选：今年到最早允许年份，倒序排列（近的年份在前）。 */
export function getBirthdayYearOptions(today: Date = new Date()) {
  const current = today.getFullYear();
  return Array.from({ length: current - MIN_BIRTH_YEAR + 1 }, (_, index) => current - index);
}

/** 某年可选的最大月份：今年只能选到本月，避免未来日期。 */
export function getMaxBirthdayMonth(year: number, today: Date = new Date()) {
  const now = todayParts(today);
  return year >= now.year ? now.month : 12;
}

/** 某年某月可选的最大日期：本年本月只能选到今天。 */
export function getMaxBirthdayDay(year: number, month: number, today: Date = new Date()) {
  const now = todayParts(today);
  const days = getDaysInMonth(year, month);
  return year >= now.year && month >= now.month ? Math.min(days, now.day) : days;
}

/** 把任意年月日夹到合法区间：年份在 [1900, 今年]，月份/日期不越过当月天数和今天。 */
export function clampBirthdayParts(parts: BirthdayParts, today: Date = new Date()): BirthdayParts {
  const now = todayParts(today);
  const year = Math.min(Math.max(Math.trunc(parts.year) || now.year, MIN_BIRTH_YEAR), now.year);
  const month = Math.min(Math.max(Math.trunc(parts.month) || 1, 1), getMaxBirthdayMonth(year, today));
  const day = Math.min(Math.max(Math.trunc(parts.day) || 1, 1), getMaxBirthdayDay(year, month, today));
  return { year, month, day };
}

/** 切换某一列后联动修正其余列，例如 3 月 31 日切到 2 月自动变为 2 月 28/29 日。 */
export function updateBirthdayParts(
  parts: BirthdayParts,
  change: Partial<BirthdayParts>,
  today: Date = new Date()
) {
  return clampBirthdayParts({ ...parts, ...change }, today);
}

/** 以当前值定位选择器；空值或非法值回落到默认年份。 */
export function parseBirthdayParts(value: string | null | undefined, today: Date = new Date()): BirthdayParts {
  const normalized = normalizeBirthday(value);
  if (!normalized) {
    return clampBirthdayParts({ year: today.getFullYear() - DEFAULT_AGE_OFFSET, month: 1, day: 1 }, today);
  }
  const [year, month, day] = normalized.split("-").map(Number);
  return clampBirthdayParts({ year, month, day }, today);
}

export function formatBirthdayParts(parts: BirthdayParts) {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${String(parts.year).padStart(4, "0")}-${pad(parts.month)}-${pad(parts.day)}`;
}
