// 现金模块的日期纯函数：全部基于 "yyyy-MM-dd" 字符串与 UTC 日序号运算，
// 不读取手机当前时间或时区——门店「今天」只能来自 context.stores[].storeToday。
import { CASH_DAILY_DEFAULT_DAYS, CASH_DAILY_MAX_DAYS } from "./constants";

const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;
const MS_PER_DAY = 86_400_000;

export function isValidDateString(value: string | null | undefined): value is string {
  if (typeof value !== "string") return false;
  const match = DATE_PATTERN.exec(value);
  if (!match) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const time = Date.UTC(year, month - 1, day);
  const date = new Date(time);
  // 往返比对，挡掉 2026-02-30 这类会被 Date 自动进位的日期
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

/** 日期字符串转 UTC 日序号；无效日期返回 null。 */
export function toDayNumber(value: string): number | null {
  if (!isValidDateString(value)) return null;
  const match = DATE_PATTERN.exec(value)!;
  return Math.round(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])) / MS_PER_DAY);
}

export function fromDayNumber(dayNumber: number): string {
  const date = new Date(dayNumber * MS_PER_DAY);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${String(date.getUTCFullYear()).padStart(4, "0")}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`;
}

/** 日期加减天数；输入无效时原样返回，由调用方先用 isValidDateString 校验。 */
export function addDays(value: string, delta: number): string {
  const day = toDayNumber(value);
  return day == null ? value : fromDayNumber(day + delta);
}

/** later 比 earlier 晚几天（可为负）；任一无效返回 null。 */
export function diffDays(later: string, earlier: string): number | null {
  const a = toDayNumber(later);
  const b = toDayNumber(earlier);
  return a == null || b == null ? null : a - b;
}

/** 字符串字典序即日期序（yyyy-MM-dd 定长）。 */
export function compareDates(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** 0 = 周日 … 6 = 周六；无效日期返回 null。 */
export function weekdayOf(value: string): number | null {
  const day = toDayNumber(value);
  // 1970-01-01 是周四
  return day == null ? null : (((day + 4) % 7) + 7) % 7;
}

// ───────────────────────── 补录日期范围 ─────────────────────────

export interface EntryDateRange {
  /** 最早可选日期；null 表示不限回溯（有全部分店权限者）。 */
  min: string | null;
  /** 最晚可选日期 = 门店今天，不允许选未来。 */
  max: string;
}

export function resolveEntryDateRange(options: {
  storeToday: string;
  maxBackfillDays: number;
  canViewAllStores: boolean;
}): EntryDateRange {
  const { storeToday, maxBackfillDays, canViewAllStores } = options;
  if (canViewAllStores) {
    return { min: null, max: storeToday };
  }
  return { min: addDays(storeToday, -Math.max(0, Math.trunc(maxBackfillDays))), max: storeToday };
}

export function isDateWithinRange(value: string, range: EntryDateRange): boolean {
  if (!isValidDateString(value)) return false;
  if (value > range.max) return false;
  return range.min == null || value >= range.min;
}

// ───────────────────────── 按日明细 / 记录列表范围 ─────────────────────────

export interface DateRange {
  from: string;
  to: string;
}

/** 默认展示最近 days 天（含门店今天）。 */
export function buildRecentRange(storeToday: string, days: number = CASH_DAILY_DEFAULT_DAYS): DateRange {
  const span = Math.min(Math.max(1, Math.trunc(days)), CASH_DAILY_MAX_DAYS);
  return { from: addDays(storeToday, -(span - 1)), to: storeToday };
}

export type DailyRangeIssue = "invalid" | "reversed" | "tooLong" | "future";

/** 校验按日明细的查询范围：两端含，最多 93 天，不超过门店今天。 */
export function validateDailyRange(range: DateRange, storeToday: string): DailyRangeIssue | null {
  if (!isValidDateString(range.from) || !isValidDateString(range.to)) return "invalid";
  if (range.from > range.to) return "reversed";
  if (range.to > storeToday) return "future";
  const span = diffDays(range.to, range.from);
  return span != null && span + 1 > CASH_DAILY_MAX_DAYS ? "tooLong" : null;
}

/** 范围覆盖的天数（含两端）。 */
export function rangeLength(range: DateRange): number | null {
  const span = diffDays(range.to, range.from);
  return span == null ? null : span + 1;
}

// ───────────────────────── 时间展示 ─────────────────────────

/**
 * 把 UTC ISO 时间按门店时区格式化成 "yyyy-MM-dd HH:mm"。
 * 时区无效或运行时不支持 Intl 时区时退回设备时区，保证至少能显示。
 */
export function formatUtcInZone(utcIso: string | null | undefined, timeZoneId?: string | null): string {
  if (!utcIso) return "";
  const date = new Date(utcIso);
  if (Number.isNaN(date.getTime())) return utcIso;
  const parts = (zone?: string) => {
    const formatter = new Intl.DateTimeFormat("en-CA", {
      timeZone: zone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      hourCycle: "h23",
    });
    const map: Record<string, string> = {};
    for (const part of formatter.formatToParts(date)) map[part.type] = part.value;
    return `${map.year}-${map.month}-${map.day} ${map.hour}:${map.minute}`;
  };
  try {
    return parts(timeZoneId || undefined);
  } catch {
    try {
      return parts(undefined);
    } catch {
      return date.toISOString().slice(0, 16).replace("T", " ");
    }
  }
}
