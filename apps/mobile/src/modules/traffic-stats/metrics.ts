// 客流统计页的指标与图表数据推导（纯函数，Node 下可测）。
//
// 口径（与方案文档 §3 一致，改动前先看文档）：
// - 主指标是「进店人次」(visitCount)，不依赖去重，最稳定；独立访客只在门店开启去重时展示，且是估算值。
// - 同比只比「已完整结束」的时间：今天比昨天只取到上一个完整小时；本周 / 本月不含今天，
//   避免把一半的当天数据拿去和整天比，得出虚假的下降。
// - 日均 / 最忙一天 / 周末日均 / 按星期平均 同样只用已结束且有抓拍的日子；有抓拍才算营业日，
//   没数据的日子（歇业、摄像机离线）不拉低平均。
import { isFutureDate, isWeekend, parseAsOf, weekdayIndex } from "./period";
import type { TrafficCounts, TrafficDay, TrafficOverview } from "./types";

export const HOURS_PER_DAY = 24;
/** 默认营业时段（图表横轴至少覆盖它，数据超出时向两侧扩）。 */
export const DEFAULT_HOUR_WINDOW = { start: 8, end: 20 } as const;

export function formatCount(value: number): string {
  return Math.round(value).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ",");
}

/** 平均值显示：小于 10 保留 1 位小数（小店每小时只有几个人，取整会抹平曲线），否则取整。 */
export function formatAverage(value: number): string {
  return value < 10 ? (Math.round(value * 10) / 10).toString() : formatCount(value);
}

/** 「12:00–13:00」。 */
export function formatHourRange(hour: number): string {
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(hour)}:00–${pad(hour + 1)}:00`;
}

export function toPercent(current: number, previous: number): number | null {
  return previous > 0 ? Math.round(((current - previous) / previous) * 100) : null;
}

// ───────────── 同比 ─────────────

export type CompareBasis =
  | { kind: "hours"; throughHour: number }
  | { kind: "days"; days: number }
  | { kind: "full" };

export interface CompareResult {
  current: number;
  previous: number;
  /** 对照期为 0 时为 null（页面不显示百分比，避免无穷大）。 */
  percent: number | null;
  basis: CompareBasis;
}

/** 当天已完整结束的小时数：今天按 asOfLocal 的小时（14:32 → 14，即 0–13 点完整）；历史日 24。 */
export function completedHourCount(overview: TrafficOverview): number {
  if (overview.period !== "day" || !overview.isCurrent) return HOURS_PER_DAY;
  return parseAsOf(overview.asOfLocal)?.hour ?? 0;
}

/** 当期已完整结束的日子下标（相对 current.days）：当前周期不含今天及以后；历史周期全部。 */
function completedDayIndexes(overview: TrafficOverview): number[] {
  const indexes: number[] = [];
  overview.current.days.forEach((day, index) => {
    const ended = overview.isCurrent ? !isFutureDate(day.date, overview.today) && day.date !== overview.today : true;
    if (ended) indexes.push(index);
  });
  return indexes;
}

export function resolveCompare(overview: TrafficOverview): CompareResult | null {
  if (overview.period === "day") {
    const through = completedHourCount(overview);
    if (through <= 0) return null;
    const sumHours = (hours: TrafficOverview["current"]["hours"]) =>
      hours.slice(0, through).reduce((sum, item) => sum + item.counts.visitCount, 0);
    const current = sumHours(overview.current.hours);
    const previous = sumHours(overview.previous.hours);
    return {
      current,
      previous,
      percent: toPercent(current, previous),
      basis: overview.isCurrent ? { kind: "hours", throughHour: through } : { kind: "full" },
    };
  }

  // 周 / 月：按下标对齐（周一对周一、1 号对 1 号），对照期没有这一天（31 号对 30 天的上月）就两边都不比。
  const indexes = completedDayIndexes(overview).filter((index) => index < overview.previous.days.length);
  if (indexes.length === 0) return null;
  const current = indexes.reduce((sum, index) => sum + overview.current.days[index].counts.visitCount, 0);
  const previous = indexes.reduce((sum, index) => sum + overview.previous.days[index].counts.visitCount, 0);
  return {
    current,
    previous,
    percent: toPercent(current, previous),
    basis: overview.isCurrent ? { kind: "days", days: indexes.length } : { kind: "full" },
  };
}

// ───────────── 汇总指标 ─────────────

/** 已结束且有抓拍的日子（营业日）。 */
export function activeCompletedDays(overview: TrafficOverview): TrafficDay[] {
  return completedDayIndexes(overview)
    .map((index) => overview.current.days[index])
    .filter((day) => day.counts.captureCount > 0);
}

export function dailyAverage(overview: TrafficOverview): number | null {
  const days = activeCompletedDays(overview);
  if (days.length === 0) return null;
  return days.reduce((sum, day) => sum + day.counts.visitCount, 0) / days.length;
}

export function busiestDay(overview: TrafficOverview): TrafficDay | null {
  let best: TrafficDay | null = null;
  for (const day of activeCompletedDays(overview)) {
    if (day.counts.visitCount > 0 && (!best || day.counts.visitCount > best.counts.visitCount)) best = day;
  }
  return best;
}

export function weekendAverage(overview: TrafficOverview): number | null {
  const days = activeCompletedDays(overview).filter((day) => isWeekend(day.date));
  if (days.length === 0) return null;
  return days.reduce((sum, day) => sum + day.counts.visitCount, 0) / days.length;
}

/** 日均访客（估算）：各营业日去重人数的平均。 */
export function dailyAverageUnique(overview: TrafficOverview): number | null {
  const days = activeCompletedDays(overview);
  if (days.length === 0) return null;
  return days.reduce((sum, day) => sum + day.counts.uniqueVisitorCount, 0) / days.length;
}

/** 按星期（周一 0 … 周日 6）的日均进店人次；该星期没有营业日则为 null。 */
export function weekdayAverages(overview: TrafficOverview): (number | null)[] {
  const sums = Array<number>(7).fill(0);
  const counts = Array<number>(7).fill(0);
  for (const day of activeCompletedDays(overview)) {
    const index = weekdayIndex(day.date);
    if (index < 0) continue;
    sums[index] += day.counts.visitCount;
    counts[index] += 1;
  }
  return sums.map((sum, index) => (counts[index] > 0 ? sum / counts[index] : null));
}

export interface PeakHour {
  hour: number;
  value: number;
}

/** 数组里最大的一个（并列取最早）；全为 0 / null 返回 null。 */
export function findPeak(values: readonly (number | null)[]): PeakHour | null {
  let best: PeakHour | null = null;
  values.forEach((value, hour) => {
    if (value != null && value > 0 && (!best || value > best.value)) best = { hour, value };
  });
  return best;
}

/** 分时日均（周 / 月）：各小时计数之和 ÷ 参与天数；没有参与的日子返回 null。 */
export function hourlyAverage(overview: TrafficOverview): number[] | null {
  const days = overview.current.hourlyDays;
  if (days <= 0) return null;
  return overview.current.hours.map((item) => item.counts.visitCount / days);
}

/** 今天的分时进店人次（day 视图）。 */
export function hourlyVisits(hours: TrafficOverview["current"]["hours"]): number[] {
  return hours.map((item) => item.counts.visitCount);
}

export function isAllZero(counts: TrafficCounts): boolean {
  return counts.visitCount === 0 && counts.captureCount === 0;
}

// ───────────── 图表数据 ─────────────

export interface HourWindow {
  start: number;
  /** 含。 */
  end: number;
}

/** 横轴小时窗口：至少 8–20 点；两期任一有数据的小时超出这个范围时向外扩到那个小时。 */
export function resolveHourWindow(...series: readonly (readonly (number | null)[])[]): HourWindow {
  let low = Infinity;
  let high = -Infinity;
  for (const values of series) {
    values.forEach((value, hour) => {
      if (value != null && value > 0) {
        low = Math.min(low, hour);
        high = Math.max(high, hour);
      }
    });
  }
  if (!Number.isFinite(low)) return { ...DEFAULT_HOUR_WINDOW };
  return {
    start: Math.min(DEFAULT_HOUR_WINDOW.start, low),
    end: Math.max(DEFAULT_HOUR_WINDOW.end, high),
  };
}

export interface HourlyChartModel {
  window: HourWindow;
  /** 与窗口等长；今天尚未到的小时为 null。 */
  current: (number | null)[];
  previous: number[];
  /** 进行中的小时在窗口内的下标（今天且在窗口内），没有则 null。 */
  partialIndex: number | null;
  /** 「现在」竖线位置（窗口内下标，带小数 = 分钟占比），没有则 null。 */
  nowIndex: number | null;
  nowLabel: string | null;
}

/** 「今天」分时曲线：今天（到当前小时为止）与昨天（整天）。 */
export function buildHourlyChart(overview: TrafficOverview): HourlyChartModel {
  const currentAll = hourlyVisits(overview.current.hours);
  const previousAll = hourlyVisits(overview.previous.hours);
  const asOf = overview.isCurrent ? parseAsOf(overview.asOfLocal) : null;
  const liveHour = asOf?.hour ?? null;
  const visible = currentAll.map((value, hour) => (liveHour !== null && hour > liveHour ? null : value));
  const window = resolveHourWindow(visible, previousAll);
  const slice = <T,>(values: readonly T[]) => values.slice(window.start, window.end + 1);
  const inWindow = liveHour !== null && liveHour >= window.start && liveHour <= window.end;
  return {
    window,
    current: slice(visible),
    previous: slice(previousAll),
    partialIndex: inWindow ? liveHour - window.start : null,
    nowIndex: inWindow && asOf ? liveHour - window.start + asOf.minute / 60 : null,
    nowLabel: asOf ? `${String(asOf.hour).padStart(2, "0")}:${String(asOf.minute).padStart(2, "0")}` : null,
  };
}

export interface DailyChartModel {
  dates: string[];
  /** 今天之后的日子为 null。 */
  current: (number | null)[];
  /** 与 current 同下标对齐；对照期没有这一天为 null。 */
  previous: (number | null)[];
  weekend: boolean[];
  /** 今天（进行中）的下标，没有则 null。 */
  partialIndex: number | null;
}

/** 周 / 月每日曲线。 */
export function buildDailyChart(overview: TrafficOverview): DailyChartModel {
  const dates = overview.current.days.map((day) => day.date);
  const current = overview.current.days.map((day) =>
    overview.isCurrent && isFutureDate(day.date, overview.today) ? null : day.counts.visitCount,
  );
  const previous = overview.current.days.map((_, index) => overview.previous.days[index]?.counts.visitCount ?? null);
  const partialIndex = overview.isCurrent ? dates.indexOf(overview.today) : -1;
  return {
    dates,
    current,
    previous,
    weekend: dates.map(isWeekend),
    partialIndex: partialIndex >= 0 ? partialIndex : null,
  };
}

export interface ProfileChartModel {
  window: HourWindow;
  values: number[];
  peak: PeakHour | null;
}

/** 周 / 月的「各时段平均进店」。 */
export function buildProfileChart(overview: TrafficOverview): ProfileChartModel | null {
  const average = hourlyAverage(overview);
  if (!average) return null;
  const window = resolveHourWindow(average);
  const values = average.slice(window.start, window.end + 1);
  const peak = findPeak(average);
  return { window, values, peak: peak ? { hour: peak.hour - window.start, value: peak.value } : null };
}
