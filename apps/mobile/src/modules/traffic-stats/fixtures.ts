// 仅供测试与预览使用的样例数据（全部虚构）。生产代码不要引用本文件。
import { addDays, weekdayIndex } from "./period";
import type { TrafficCounts, TrafficOverview, TrafficPeriod, TrafficPeriodData } from "./types";

export function counts(visit: number, extra: Partial<TrafficCounts> = {}): TrafficCounts {
  const staff = extra.staffMatchCount ?? 0;
  const low = extra.lowQualityCount ?? 0;
  const fail = extra.apiFailureCount ?? 0;
  return {
    visitCount: visit,
    captureCount: extra.captureCount ?? visit + staff + low + fail,
    staffMatchCount: staff,
    uniqueVisitorCount: extra.uniqueVisitorCount ?? 0,
    repeatCount: extra.repeatCount ?? 0,
    lowQualityCount: low,
    apiFailureCount: fail,
  };
}

export function hoursFrom(visits: readonly number[]): TrafficPeriodData["hours"] {
  return Array.from({ length: 24 }, (_, hour) => ({ hour, counts: counts(visits[hour] ?? 0) }));
}

function sum(values: readonly number[]): number {
  return values.reduce((total, value) => total + value, 0);
}

/** 昨天整天（8–20 点）。 */
export const YESTERDAY_VISITS = [0, 0, 0, 0, 0, 0, 0, 0, 8, 20, 31, 49, 66, 55, 44, 48, 57, 41, 22, 9, 3, 0, 0, 0] as const;
/** 今天到 14:32 为止（14 点进行中）。 */
export const TODAY_VISITS = [0, 0, 0, 0, 0, 0, 0, 0, 6, 21, 38, 55, 64, 50, 31, 0, 0, 0, 0, 0, 0, 0, 0, 0] as const;

function periodData(visits: readonly number[]): TrafficPeriodData {
  const total = sum(visits);
  return {
    days: [],
    hours: hoursFrom(visits),
    total: counts(total),
    hourlyDays: total > 0 ? 1 : 0,
  };
}

const BASE = {
  storeCode: "1008",
  storeName: "Bankstown",
  timeZoneId: "Australia/Sydney",
  countingEnabled: true,
  trafficDedupEnabled: true,
} as const;

/** 今天（2026-10-24 周六，门店本地 14:32）+ 昨天。 */
export function dayOverview(overrides: Partial<TrafficOverview> = {}): TrafficOverview {
  const current = periodData(TODAY_VISITS);
  current.total = counts(sum(TODAY_VISITS), { uniqueVisitorCount: 198, repeatCount: 67, staffMatchCount: 30, lowQualityCount: 9, apiFailureCount: 2 });
  current.days = [{ date: "2026-10-24", counts: current.total }];
  const previous = periodData(YESTERDAY_VISITS);
  previous.days = [{ date: "2026-10-23", counts: counts(sum(YESTERDAY_VISITS)) }];
  return {
    ...BASE,
    period: "day",
    from: "2026-10-24",
    to: "2026-10-24",
    prevFrom: "2026-10-23",
    prevTo: "2026-10-23",
    today: "2026-10-24",
    asOfLocal: "2026-10-24T14:32",
    isCurrent: true,
    current,
    previous,
    ...overrides,
  };
}

function daysBetween(from: string, to: string): string[] {
  const dates: string[] = [];
  for (let date = from; date <= to; date = addDays(date, 1)) dates.push(date);
  return dates;
}

/** 本周（10-19 周一 … 10-25 周日，今天周六 10-24 进行中）与上周。 */
export function weekOverview(overrides: Partial<TrafficOverview> = {}): TrafficOverview {
  const currentVisits = [231, 214, 246, 268, 312, 265, 0];
  const previousVisits = [218, 225, 232, 251, 298, 389, 341];
  const dates = daysBetween("2026-10-19", "2026-10-25");
  const prevDates = daysBetween("2026-10-12", "2026-10-18");
  const avg = [0, 0, 0, 0, 0, 0, 0, 0, 4, 11, 20, 30, 36, 29, 24, 27, 31, 23, 12, 5, 2, 0, 0, 0].map((value) => value * 5);
  return {
    ...BASE,
    period: "week",
    from: "2026-10-19",
    to: "2026-10-25",
    prevFrom: "2026-10-12",
    prevTo: "2026-10-18",
    today: "2026-10-24",
    asOfLocal: "2026-10-24T14:32",
    isCurrent: true,
    current: {
      days: dates.map((date, i) => ({ date, counts: counts(currentVisits[i], { uniqueVisitorCount: Math.round(currentVisits[i] * 0.72) }) })),
      hours: hoursFrom(avg),
      total: counts(sum(currentVisits)),
      hourlyDays: 5,
    },
    previous: {
      days: prevDates.map((date, i) => ({ date, counts: counts(previousVisits[i]) })),
      hours: hoursFrom(avg),
      total: counts(sum(previousVisits)),
      hourlyDays: 7,
    },
    ...overrides,
  };
}

/** 本月（2026-10，今天 24 日进行中）与上月（9 月 30 天）：每天固定值，便于断言。 */
export function monthOverview(overrides: Partial<TrafficOverview> = {}): TrafficOverview {
  const dates = daysBetween("2026-10-01", "2026-10-31");
  const prevDates = daysBetween("2026-09-01", "2026-09-30");
  const value = (date: string) => 100 + weekdayIndex(date) * 10;
  return {
    ...BASE,
    period: "month",
    from: "2026-10-01",
    to: "2026-10-31",
    prevFrom: "2026-09-01",
    prevTo: "2026-09-30",
    today: "2026-10-24",
    asOfLocal: "2026-10-24T14:32",
    isCurrent: true,
    current: {
      days: dates.map((date) => ({ date, counts: counts(date <= "2026-10-24" ? value(date) : 0) })),
      hours: hoursFrom([]),
      total: counts(sum(dates.filter((date) => date <= "2026-10-24").map(value))),
      hourlyDays: 0,
    },
    previous: {
      days: prevDates.map((date) => ({ date, counts: counts(value(date)) })),
      hours: hoursFrom([]),
      total: counts(sum(prevDates.map(value))),
      hourlyDays: 0,
    },
    ...overrides,
  };
}

export function overviewFor(period: TrafficPeriod): TrafficOverview {
  return period === "day" ? dayOverview() : period === "week" ? weekOverview() : monthOverview();
}
