export type RevenuePeriodMode = "day" | "week" | "month";
export type RevenueCompareMode =
  | "previousPeriod"
  | "lastYearSameWeekday"
  | "lastYearIsoWeek"
  | "lastYearSameMonth";

export interface RevenuePeriod {
  mode: RevenuePeriodMode;
  startDate: string;
  endDate: string;
}

export interface RevenueDateBounds {
  minDate: string;
  maxDate: string;
}

const DAY_MS = 24 * 60 * 60 * 1000;

function pad(value: number) {
  return String(value).padStart(2, "0");
}

export function formatDateKey(date: Date) {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export function parseDateKey(value: string) {
  const [year, month, day] = value.split("-").map(Number);
  if (!year || !month || !day) {
    throw new Error(`Invalid date key: ${value}`);
  }
  return new Date(year, month - 1, day);
}

function addDays(date: Date, days: number) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate() + days);
}

function addMonths(date: Date, months: number) {
  return new Date(date.getFullYear(), date.getMonth() + months, date.getDate());
}

function startOfWeek(date: Date) {
  const day = date.getDay();
  const offset = day === 0 ? -6 : 1 - day;
  return addDays(date, offset);
}

function getIsoWeekInfo(date: Date) {
  const normalized = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const day = (normalized.getDay() + 6) % 7;
  normalized.setDate(normalized.getDate() + 3 - day);
  const weekYear = normalized.getFullYear();
  const weekOne = new Date(weekYear, 0, 4);
  const week =
    1 +
    Math.round(
      ((normalized.getTime() - weekOne.getTime()) / DAY_MS - 3 + ((weekOne.getDay() + 6) % 7)) / 7
    );
  return { weekYear, week, weekday: day + 1 };
}

function dateFromIsoWeek(weekYear: number, week: number, weekday: number) {
  const weekOne = new Date(weekYear, 0, 4);
  const weekOneStart = startOfWeek(weekOne);
  return addDays(weekOneStart, (week - 1) * 7 + weekday - 1);
}

function getIsoWeeksInYear(weekYear: number) {
  return getIsoWeekInfo(new Date(weekYear, 11, 28)).week;
}

function endOfMonth(date: Date) {
  return new Date(date.getFullYear(), date.getMonth() + 1, 0);
}

function getNaturalRevenuePeriod(mode: RevenuePeriodMode, anchor: Date): RevenuePeriod {
  if (mode === "day") {
    return { mode, startDate: formatDateKey(anchor), endDate: formatDateKey(anchor) };
  }

  if (mode === "week") {
    const start = startOfWeek(anchor);
    return { mode, startDate: formatDateKey(start), endDate: formatDateKey(addDays(start, 6)) };
  }

  const start = new Date(anchor.getFullYear(), anchor.getMonth(), 1);
  return { mode, startDate: formatDateKey(start), endDate: formatDateKey(endOfMonth(start)) };
}

function clipCurrentRevenuePeriod(period: RevenuePeriod, anchor: Date): RevenuePeriod {
  const anchorDate = formatDateKey(anchor);
  if (anchorDate < period.startDate || anchorDate > period.endDate) {
    return period;
  }

  return { ...period, endDate: anchorDate };
}

function daysBetween(startDate: string, endDate: string) {
  return Math.round((parseDateKey(endDate).getTime() - parseDateKey(startDate).getTime()) / DAY_MS) + 1;
}

function shiftPeriod(period: RevenuePeriod, days: number): RevenuePeriod {
  return {
    mode: period.mode,
    startDate: formatDateKey(addDays(parseDateKey(period.startDate), days)),
    endDate: formatDateKey(addDays(parseDateKey(period.endDate), days)),
  };
}

export function getDefaultRevenuePeriod(mode: RevenuePeriodMode, anchor = new Date()): RevenuePeriod {
  // 当前周/月只查询已经发生的日期，避免把未来零值混入累计指标。
  return clipCurrentRevenuePeriod(getNaturalRevenuePeriod(mode, anchor), anchor);
}

export function getRevenuePeriodForDate(mode: RevenuePeriodMode, date: string, anchor = new Date()) {
  const period = getNaturalRevenuePeriod(mode, parseDateKey(date));
  // 只有所选日期落在当前自然周期时才截到今天；历史周/月始终保持完整。
  return clipCurrentRevenuePeriod(period, anchor);
}

// 营业额最早可回看 18 个月；单个周期最长一个自然月，不需要另设跨度上限。
export const REVENUE_HISTORY_MONTHS = 18;

export function getRevenueDateBounds(anchor = new Date()): RevenueDateBounds {
  const minimumMonthStart = new Date(anchor.getFullYear(), anchor.getMonth() - REVENUE_HISTORY_MONTHS, 1);
  // 目标月没有同一天（如 8-31 回看到 2 月）时取该月月末。
  const minimumDay = Math.min(anchor.getDate(), endOfMonth(minimumMonthStart).getDate());
  return {
    minDate: formatDateKey(
      new Date(minimumMonthStart.getFullYear(), minimumMonthStart.getMonth(), minimumDay),
    ),
    maxDate: formatDateKey(anchor),
  };
}

export function refreshRevenueDateSelection(selectedDate: string, anchor = new Date()) {
  const bounds = getRevenueDateBounds(anchor);
  return {
    bounds,
    // 常驻页面跨日后保留仍有效的选择，只收敛真正越界的日期。
    selectedDate: selectedDate < bounds.minDate
      ? bounds.minDate
      : selectedDate > bounds.maxDate
        ? bounds.maxDate
        : selectedDate,
  };
}

export function isRevenuePeriodAvailable(period: RevenuePeriod, bounds: RevenueDateBounds) {
  // 周期只要覆盖至少一个可选日期即可，例如本周即使尚未结束仍可查询。
  return period.endDate >= bounds.minDate && period.startDate <= bounds.maxDate;
}

export function getPreviousRevenuePeriod(period: RevenuePeriod): RevenuePeriod {
  if (period.mode === "month") {
    const start = addMonths(parseDateKey(period.startDate), -1);
    return { mode: period.mode, startDate: formatDateKey(start), endDate: formatDateKey(endOfMonth(start)) };
  }

  if (period.mode === "week") {
    const start = addDays(parseDateKey(period.startDate), -7);
    return { mode: period.mode, startDate: formatDateKey(start), endDate: formatDateKey(addDays(start, 6)) };
  }

  return shiftPeriod(period, -daysBetween(period.startDate, period.endDate));
}

export function getNextRevenuePeriod(period: RevenuePeriod, anchor = new Date()): RevenuePeriod {
  if (period.mode === "month") {
    const start = addMonths(parseDateKey(period.startDate), 1);
    return clipCurrentRevenuePeriod(
      { mode: period.mode, startDate: formatDateKey(start), endDate: formatDateKey(endOfMonth(start)) },
      anchor,
    );
  }

  if (period.mode === "week") {
    const start = addDays(parseDateKey(period.startDate), 7);
    return clipCurrentRevenuePeriod(
      { mode: period.mode, startDate: formatDateKey(start), endDate: formatDateKey(addDays(start, 6)) },
      anchor,
    );
  }

  return clipCurrentRevenuePeriod(
    shiftPeriod(period, daysBetween(period.startDate, period.endDate)),
    anchor,
  );
}

export function getYesterdayRevenuePeriod(anchor = new Date()): RevenuePeriod {
  const date = addDays(anchor, -1);
  return { mode: "day", startDate: formatDateKey(date), endDate: formatDateKey(date) };
}

export function getLastWeekRevenuePeriod(anchor = new Date()): RevenuePeriod {
  const start = addDays(startOfWeek(anchor), -7);
  return { mode: "week", startDate: formatDateKey(start), endDate: formatDateKey(addDays(start, 6)) };
}

export function getLastMonthRevenuePeriod(anchor = new Date()): RevenuePeriod {
  const start = new Date(anchor.getFullYear(), anchor.getMonth() - 1, 1);
  return { mode: "month", startDate: formatDateKey(start), endDate: formatDateKey(endOfMonth(start)) };
}

export function getLastYearSameWeekdayPeriod(period: RevenuePeriod): RevenuePeriod {
  // 日报同比按去年同 ISO 周、同星期几取数。
  return getLastYearIsoWeekPeriod(period);
}

export function getLastYearIsoWeekPeriod(period: RevenuePeriod): RevenuePeriod {
  const { weekYear, week, weekday } = getIsoWeekInfo(parseDateKey(period.startDate));
  const compareWeekYear = weekYear - 1;
  const compareWeek = Math.min(week, getIsoWeeksInYear(compareWeekYear));
  const weekStart = dateFromIsoWeek(compareWeekYear, compareWeek, weekday);
  return {
    mode: period.mode,
    startDate: formatDateKey(weekStart),
    endDate: formatDateKey(addDays(weekStart, daysBetween(period.startDate, period.endDate) - 1)),
  };
}

export function getLastYearSameMonthPeriod(period: RevenuePeriod): RevenuePeriod {
  const start = parseDateKey(period.startDate);
  const currentMonthStart = new Date(start.getFullYear(), start.getMonth(), 1);
  const isCompleteNaturalMonth =
    period.startDate === formatDateKey(currentMonthStart) &&
    period.endDate === formatDateKey(endOfMonth(currentMonthStart));
  const compareStart = new Date(start.getFullYear() - 1, start.getMonth(), 1);
  const durationEnd = addDays(compareStart, daysBetween(period.startDate, period.endDate) - 1);
  const compareMonthEnd = endOfMonth(compareStart);
  return {
    mode: period.mode,
    startDate: formatDateKey(compareStart),
    // 完整历史月使用比较年份的自然月末；只有 MTD/部分月才按已发生天数对齐。
    endDate: formatDateKey(
      isCompleteNaturalMonth
        ? compareMonthEnd
        : durationEnd <= compareMonthEnd
          ? durationEnd
          : compareMonthEnd,
    ),
  };
}

export function getCompareRevenuePeriod(period: RevenuePeriod, compareMode: RevenueCompareMode) {
  if (compareMode === "lastYearSameWeekday") {
    return getLastYearSameWeekdayPeriod(period);
  }
  if (compareMode === "lastYearIsoWeek") {
    return getLastYearIsoWeekPeriod(period);
  }
  if (compareMode === "lastYearSameMonth") {
    return getLastYearSameMonthPeriod(period);
  }
  return getPreviousRevenuePeriod(period);
}
