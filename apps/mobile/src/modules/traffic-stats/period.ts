// 客流统计页的日期与周期逻辑。纯函数、只处理 "yyyy-MM-dd" 字符串，不读手机时区：
// 「今天」「现在几点」一律来自服务端按门店时区算好的 today / asOfLocal。
import type { AppLanguage } from "@/shared/i18n/types";
import type { TrafficOverview } from "./types";

const MS_PER_DAY = 86_400_000;
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

const WEEKDAYS: Record<AppLanguage, readonly string[]> = {
  zh: ["周一", "周二", "周三", "周四", "周五", "周六", "周日"],
  en: ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"],
};
const MONTHS_EN = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"] as const;

function toUtcMs(date: string): number | null {
  const match = DATE_PATTERN.exec(date);
  if (!match) return null;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const ms = Date.UTC(year, month - 1, day);
  const check = new Date(ms);
  // 2026-02-31 这类会被 Date 自动进位的日期一律判无效。
  return check.getUTCFullYear() === year && check.getUTCMonth() === month - 1 && check.getUTCDate() === day ? ms : null;
}

function fromUtcMs(ms: number): string {
  const d = new Date(ms);
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, "0")}-${String(d.getUTCDate()).padStart(2, "0")}`;
}

export function isValidDate(date: string): boolean {
  return toUtcMs(date) !== null;
}

export function addDays(date: string, days: number): string {
  const ms = toUtcMs(date);
  return ms === null ? date : fromUtcMs(ms + days * MS_PER_DAY);
}

/** 周一为 0 … 周日为 6；日期无效返回 -1。 */
export function weekdayIndex(date: string): number {
  const ms = toUtcMs(date);
  if (ms === null) return -1;
  return (new Date(ms).getUTCDay() + 6) % 7;
}

/** 日期比较（字符串的字典序即时间序，前提是格式合法）。 */
export function compareDates(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

export function dayOfMonth(date: string): number {
  return Number(date.slice(8, 10));
}

export function isWeekend(date: string): boolean {
  const index = weekdayIndex(date);
  return index === 5 || index === 6;
}

/** asOfLocal（yyyy-MM-ddTHH:mm）里的小时与分钟；取不到返回 null。 */
export function parseAsOf(asOfLocal: string): { date: string; hour: number; minute: number } | null {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2})/.exec(asOfLocal);
  if (!match) return null;
  const hour = Number(match[2]);
  const minute = Number(match[3]);
  return hour <= 23 && minute <= 59 ? { date: match[1], hour, minute } : null;
}

export function formatClock(hour: number, minute = 0): string {
  return `${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}`;
}

/** 「10月24日」/「24 Oct」。 */
export function formatMonthDay(date: string, language: AppLanguage): string {
  const month = Number(date.slice(5, 7));
  const day = dayOfMonth(date);
  return language === "zh" ? `${month}月${day}日` : `${day} ${MONTHS_EN[month - 1] ?? ""}`;
}

/** 「10/24」，明细表里的短日期。 */
export function formatShortSlash(date: string): string {
  return `${Number(date.slice(5, 7))}/${dayOfMonth(date)}`;
}

export function weekdayLabel(date: string, language: AppLanguage): string {
  const index = weekdayIndex(date);
  return index < 0 ? "" : WEEKDAYS[language][index];
}

export function weekdayLabels(language: AppLanguage): readonly string[] {
  return WEEKDAYS[language];
}

/** 「2026年10月」/「Oct 2026」。 */
export function formatYearMonth(date: string, language: AppLanguage): string {
  const year = date.slice(0, 4);
  const month = Number(date.slice(5, 7));
  return language === "zh" ? `${year}年${month}月` : `${MONTHS_EN[month - 1] ?? ""} ${year}`;
}

export type PeriodHeadline =
  | { kind: "today" | "yesterday" | "thisWeek" | "lastWeek" | "thisMonth" | "lastMonth" }
  | { kind: "text"; text: string };

/** 日期步进条的标题：当前 / 上一个周期用语义词，更早的用具体日期。 */
export function resolvePeriodHeadline(overview: TrafficOverview, language: AppLanguage): PeriodHeadline {
  const { period, from, to, today } = overview;
  if (period === "day") {
    if (from === today) return { kind: "today" };
    if (from === addDays(today, -1)) return { kind: "yesterday" };
    return { kind: "text", text: formatMonthDay(from, language) };
  }
  if (period === "week") {
    if (overview.isCurrent) return { kind: "thisWeek" };
    if (to === addDays(today, -(weekdayIndex(today) + 1))) return { kind: "lastWeek" };
    return { kind: "text", text: `${formatMonthDay(from, language)} – ${formatMonthDay(to, language)}` };
  }
  if (overview.isCurrent) return { kind: "thisMonth" };
  if (from.slice(0, 7) === addDays(`${today.slice(0, 7)}-01`, -1).slice(0, 7)) return { kind: "lastMonth" };
  return { kind: "text", text: formatYearMonth(from, language) };
}

/** 步进到上一个周期：返回新的 anchor（周期内任一日期）。 */
export function previousAnchor(overview: Pick<TrafficOverview, "from">): string {
  return addDays(overview.from, -1);
}

/** 步进到下一个周期；当前周期已含今天时返回 null（不能越过今天）。 */
export function nextAnchor(overview: Pick<TrafficOverview, "to" | "isCurrent">): string | null {
  return overview.isCurrent ? null : addDays(overview.to, 1);
}

/**
 * 切换周期类型时保留所看的位置：正在看当前周期 → 回到 null（服务端今天）；
 * 正在看历史周期 → 用该周期起始日作 anchor，新周期就是包含这一天的那个周。
 */
export function anchorForPeriodSwitch(overview: Pick<TrafficOverview, "from" | "isCurrent"> | null | undefined): string | null {
  if (!overview || overview.isCurrent) return null;
  return overview.from;
}

/** 当期日期是否还没到（相对门店今天）。 */
export function isFutureDate(date: string, today: string): boolean {
  return compareDates(date, today) > 0;
}
