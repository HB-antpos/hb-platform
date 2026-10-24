// 客流统计响应的防御式规范化：字段缺失 / 类型漂移时界面不崩；
// 计数一律取非负整数，hours 补齐到 24 项，缺少的开关一律当 false（不放大展示范围）。
import type {
  TrafficCounts,
  TrafficOverview,
  TrafficPeriod,
  TrafficPeriodData,
} from "./types";

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : {};
}

function asString(value: unknown, fallback = ""): string {
  return typeof value === "string" ? value : fallback;
}

function asCount(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? Math.round(value) : 0;
}

export const EMPTY_COUNTS: TrafficCounts = Object.freeze({
  visitCount: 0,
  captureCount: 0,
  staffMatchCount: 0,
  uniqueVisitorCount: 0,
  repeatCount: 0,
  lowQualityCount: 0,
  apiFailureCount: 0,
});

export function normalizeTrafficCounts(value: unknown): TrafficCounts {
  const raw = asRecord(value);
  return {
    visitCount: asCount(raw.visitCount),
    captureCount: asCount(raw.captureCount),
    staffMatchCount: asCount(raw.staffMatchCount),
    uniqueVisitorCount: asCount(raw.uniqueVisitorCount),
    repeatCount: asCount(raw.repeatCount),
    lowQualityCount: asCount(raw.lowQualityCount),
    apiFailureCount: asCount(raw.apiFailureCount),
  };
}

function normalizePeriodData(value: unknown): TrafficPeriodData {
  const raw = asRecord(value);
  const days = (Array.isArray(raw.days) ? raw.days : [])
    .map((item) => {
      const day = asRecord(item);
      return { date: asString(day.date), counts: normalizeTrafficCounts(day.counts) };
    })
    .filter((day) => /^\d{4}-\d{2}-\d{2}$/.test(day.date));

  // 小时按 hour 落位补齐到 24 项：服务端漏项 / 乱序都不影响图表横轴。
  const byHour = new Map<number, TrafficCounts>();
  for (const item of Array.isArray(raw.hours) ? raw.hours : []) {
    const hour = asRecord(item);
    const index = typeof hour.hour === "number" ? Math.trunc(hour.hour) : -1;
    if (index >= 0 && index <= 23) byHour.set(index, normalizeTrafficCounts(hour.counts));
  }
  const hours = Array.from({ length: 24 }, (_, hour) => ({ hour, counts: byHour.get(hour) ?? { ...EMPTY_COUNTS } }));

  return { days, hours, total: normalizeTrafficCounts(raw.total), hourlyDays: asCount(raw.hourlyDays) };
}

function normalizePeriod(value: unknown, fallback: TrafficPeriod): TrafficPeriod {
  return value === "day" || value === "week" || value === "month" ? value : fallback;
}

export function normalizeTrafficOverview(value: unknown, requestedPeriod: TrafficPeriod): TrafficOverview {
  const raw = asRecord(value);
  const storeName = asString(raw.storeName).trim();
  return {
    storeCode: asString(raw.storeCode),
    storeName: storeName || null,
    timeZoneId: asString(raw.timeZoneId),
    period: normalizePeriod(raw.period, requestedPeriod),
    from: asString(raw.from),
    to: asString(raw.to),
    prevFrom: asString(raw.prevFrom),
    prevTo: asString(raw.prevTo),
    today: asString(raw.today),
    asOfLocal: asString(raw.asOfLocal),
    isCurrent: raw.isCurrent === true,
    countingEnabled: raw.countingEnabled === true,
    trafficDedupEnabled: raw.trafficDedupEnabled === true,
    current: normalizePeriodData(raw.current),
    previous: normalizePeriodData(raw.previous),
  };
}

/** 响应是否带齐了画图所需的日期（缺了就当作服务端异常，页面走错误态而不是画空图）。 */
export function isUsableTrafficOverview(overview: TrafficOverview): boolean {
  return Boolean(overview.from && overview.to && overview.today && overview.current.days.length > 0);
}
