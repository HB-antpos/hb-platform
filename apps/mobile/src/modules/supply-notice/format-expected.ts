import type { SupplyExpectedPrecision } from "./types";

export interface SupplyExpectedLike {
  expectedFrom: string | null;
  expectedTo: string | null;
  expectedPrecision: SupplyExpectedPrecision;
  isOverdue: boolean;
}

type Translate = (key: string, params?: Record<string, unknown>) => string;

function parseDate(value: string | null): { year: number; month: number; day: number } | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value ?? "");
  if (!match) {
    return null;
  }
  return { year: Number(match[1]), month: Number(match[2]), day: Number(match[3]) };
}

// 英文月份缩写；中文模板只用数字月份，忽略 monthName。
const MONTH_NAMES_EN = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/**
 * 把业务日期（YYYY-MM-DD 开头）格式化为「10月5日 / 5 Oct」，跨年带年份；日期格式由语言包决定。
 * 无法解析时返回 null。
 */
export function formatSupplyDay(value: string | null, t: Translate, thisYear = new Date().getFullYear()): string | null {
  const date = parseDate(value);
  return date ? formatDate(date, t, thisYear) : null;
}

function formatDate(date: { year: number; month: number; day: number }, t: Translate, thisYear: number) {
  const params = { ...date, monthName: MONTH_NAMES_EN[date.month - 1] ?? String(date.month) };
  return t(date.year === thisYear ? "dayFormat" : "dayFormatWithYear", params);
}

/**
 * 把“预计恢复订货”的时间段按录入精度还原成人话；不依赖 dayjs，日期按纯业务日期解析，不受设备时区影响。
 * 某日 → 10月5日 / 5 Oct；范围 → 10月5日 – 10月10日；某月 → 10 月 / October（跨年带年份）；待定 / 逾期 → 对应提示。
 */
export function formatSupplyExpected(value: SupplyExpectedLike, t: Translate, thisYear = new Date().getFullYear()): string {
  if (value.isOverdue) {
    return t("expectedOverdue");
  }
  const from = parseDate(value.expectedFrom);
  if (!from) {
    return t("expectedUnknown");
  }
  const formatDay = (date: { year: number; month: number; day: number }) => formatDate(date, t, thisYear);
  const monthName = MONTH_NAMES_EN[from.month - 1] ?? String(from.month);

  switch (value.expectedPrecision) {
    case "Month":
      return from.year === thisYear
        ? t("expectedMonth", { month: from.month, monthName })
        : t("expectedMonthWithYear", { year: from.year, month: from.month, monthName });
    case "Range": {
      const to = parseDate(value.expectedTo);
      if (to && (to.year !== from.year || to.month !== from.month || to.day !== from.day)) {
        return t("expectedRange", { from: formatDay(from), to: formatDay(to) });
      }
      return formatDay(from);
    }
    case "Day":
      return formatDay(from);
    default:
      return t("expectedUnknown");
  }
}
