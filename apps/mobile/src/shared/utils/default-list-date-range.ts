/** 列表页（HB订单、供应商发票）默认查询的月数：打开页面时只看最近 6 个月，避免全历史扫表。 */
export const DEFAULT_LIST_DATE_RANGE_MONTHS = 6;

export interface DefaultListDateRange {
  /** 起始日期，YYYY-MM-DD（含）。 */
  from: string;
  /** 结束日期，YYYY-MM-DD（含，今天）。 */
  to: string;
}

function pad2(value: number) {
  return value.toString().padStart(2, "0");
}

function formatLocalDate(date: Date) {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
}

/**
 * 返回「N 个月前的今天 ~ 今天」，按设备本地日历计算。
 * 关键逻辑：目标月份没有对应日（如 8 月 31 日往前推 6 个月是 2 月 31 日）时取该月最后一天，
 * 不能直接用 Date.setMonth，否则会溢出到下个月（2 月 31 日变成 3 月 3 日），默认区间会比预期短几天。
 */
export function getDefaultListDateRange(
  now: Date = new Date(),
  months: number = DEFAULT_LIST_DATE_RANGE_MONTHS
): DefaultListDateRange {
  const targetMonthIndex = now.getMonth() - months;
  const lastDayOfTargetMonth = new Date(now.getFullYear(), targetMonthIndex + 1, 0).getDate();
  const from = new Date(
    now.getFullYear(),
    targetMonthIndex,
    Math.min(now.getDate(), lastDayOfTargetMonth)
  );

  return { from: formatLocalDate(from), to: formatLocalDate(now) };
}
