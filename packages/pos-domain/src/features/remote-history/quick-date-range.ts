/**
 * 交易历史快捷日期范围（对齐 WPF #608）：今天 / 昨天 / 近 7 天（含今天，即今天往前 6 天）。
 * 结果是 yyyy-MM-dd 日历日文本，直接填入起止日期选择器；是否到“今天”按给定时区的日历日判断
 * （本机历史按分店营业时区，远程历史按设备本地时间）。
 */
export type HistoryQuickDateRange = "today" | "yesterday" | "last7days";

export const HISTORY_QUICK_DATE_RANGES: readonly HistoryQuickDateRange[] =
  Object.freeze(["today", "yesterday", "last7days"]);

export type HistoryDateRangeText = Readonly<{ from: string; to: string }>;

export function quickHistoryDateRange(
  kind: HistoryQuickDateRange,
  now: Date,
  timeZone?: string,
): HistoryDateRangeText {
  const today = calendarDayOf(now, timeZone);
  switch (kind) {
    case "today":
      return { from: today, to: today };
    case "yesterday": {
      const yesterday = addCalendarDays(today, -1);
      return { from: yesterday, to: yesterday };
    }
    case "last7days":
      return { from: addCalendarDays(today, -6), to: today };
  }
}

/** 给定时刻在目标时区（缺省设备本地）的日历日 yyyy-MM-dd。 */
function calendarDayOf(now: Date, timeZone: string | undefined): string {
  if (timeZone === undefined) {
    return formatParts(now.getFullYear(), now.getMonth() + 1, now.getDate());
  }
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(now);
  const pick = (type: string) =>
    Number(parts.find((part) => part.type === type)?.value);
  const year = pick("year");
  const month = pick("month");
  const day = pick("day");
  if (![year, month, day].every(Number.isInteger)) {
    throw new RangeError("Cannot resolve calendar day for time zone.");
  }
  return formatParts(year, month, day);
}

function addCalendarDays(day: string, delta: number): string {
  const [year = 0, month = 1, date = 1] = day.split("-").map(Number);
  // 用 UTC 做纯日历加减，不受夏令时影响。
  const shifted = new Date(Date.UTC(year, month - 1, date + delta));
  return formatParts(
    shifted.getUTCFullYear(),
    shifted.getUTCMonth() + 1,
    shifted.getUTCDate(),
  );
}

function formatParts(year: number, month: number, day: number): string {
  return `${String(year).padStart(4, "0")}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
}
