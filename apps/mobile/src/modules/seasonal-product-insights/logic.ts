import type {
  DailyTrendPoint,
  LedgerDayEntry,
  SeasonalCandidateFlag,
  SeasonalCandidateSort,
  SeasonalDailySales,
  SeasonalInboundRecord,
  SeasonalRange,
  SeasonalRangeError,
  SeasonalRanges,
  WeeklyLedgerRow,
  WeeklyTrendPoint,
} from "./types";

/** 与后端 SeasonalProductInsightRules 保持一致。 */
export const SEASON_START_MONTH = 8;
export const SEASONAL_MAX_RANGE_DAYS = 366;
export const SEASONAL_PERMISSION = "SeasonalProductInsights.View";

const DAY_MS = 86_400_000;

// 日期一律按 YYYY-MM-DD 的 UTC 零点换算，避免设备时区让日期偏移一天。
function toUtc(date: string) {
  return Date.parse(`${date.slice(0, 10)}T00:00:00Z`);
}

function fromUtc(ms: number) {
  return new Date(ms).toISOString().slice(0, 10);
}

export function isValidIsoDate(value: string) {
  return (
    /^\d{4}-\d{2}-\d{2}$/.test(value) &&
    Number.isFinite(toUtc(value)) &&
    fromUtc(toUtc(value)) === value
  );
}

export function addDays(date: string, days: number) {
  return fromUtc(toUtc(date) + days * DAY_MS);
}

export function daysBetweenInclusive(range: SeasonalRange) {
  return Math.round((toUtc(range.endDate) - toUtc(range.startDate)) / DAY_MS) + 1;
}

/** 季节默认从 8 月 1 日开始；1–7 月仍属于上一年 8 月起的季节。 */
export function seasonStart(today: string) {
  const year = Number(today.slice(0, 4));
  const month = Number(today.slice(5, 7));
  const seasonYear = month >= SEASON_START_MONTH ? year : year - 1;
  return `${seasonYear}-${String(SEASON_START_MONTH).padStart(2, "0")}-01`;
}

export function validateSeasonalRange(range: SeasonalRange): SeasonalRangeError | null {
  if (!isValidIsoDate(range.startDate) || !isValidIsoDate(range.endDate)) return "format";
  if (range.startDate > range.endDate) return "order";
  if (daysBetweenInclusive(range) > SEASONAL_MAX_RANGE_DAYS) return "tooLong";
  return null;
}

export function rangesDiffer(ranges: SeasonalRanges) {
  return (
    ranges.inbound.startDate !== ranges.sales.startDate ||
    ranges.inbound.endDate !== ranges.sales.endDate
  );
}

export function canViewSeasonalProductInsights(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
  isReview: boolean,
) {
  return isAuthenticated && !isReview && hasPermission(SEASONAL_PERMISSION);
}

/** 日视图：真实日期轴，只放有销售记录的日期，无记录的日期留空而不是画成 0。 */
export function buildDailyTrend(daily: SeasonalDailySales[], range: SeasonalRange) {
  const dayCount = daysBetweenInclusive(range);
  const start = toUtc(range.startDate);
  const points: DailyTrendPoint[] = daily
    .map((item) => ({
      index: Math.round((toUtc(item.date) - start) / DAY_MS),
      date: item.date.slice(0, 10),
      quantity: item.quantity,
    }))
    .filter((point) => point.index >= 0 && point.index < dayCount)
    .sort((a, b) => a.index - b.index);
  return { dayCount, points };
}

function mondayOf(date: string) {
  const ms = toUtc(date);
  const weekday = new Date(ms).getUTCDay();
  const offset = (weekday + 6) % 7;
  return fromUtc(ms - offset * DAY_MS);
}

/** 周视图：周一为一周起点，区间内每一周都给出（含 0 销量周），首尾不足 7 天的标记为不完整周。 */
export function buildWeeklyTrend(
  daily: SeasonalDailySales[],
  inbound: SeasonalInboundRecord[],
  range: SeasonalRange,
): WeeklyTrendPoint[] {
  const weeks: WeeklyTrendPoint[] = [];
  for (let monday = mondayOf(range.startDate); monday <= range.endDate; monday = addDays(monday, 7)) {
    const sunday = addDays(monday, 6);
    const startDate = monday < range.startDate ? range.startDate : monday;
    const endDate = sunday > range.endDate ? range.endDate : sunday;
    weeks.push({
      startDate,
      endDate,
      quantity: 0,
      inboundQuantity: 0,
      partial: startDate !== monday || endDate !== sunday,
    });
  }
  const find = (date: string) =>
    weeks.find((week) => date >= week.startDate && date <= week.endDate);
  for (const item of daily) {
    const week = find(item.date.slice(0, 10));
    if (week) week.quantity += item.quantity;
  }
  for (const record of inbound) {
    const week = find(record.date.slice(0, 10));
    if (week) week.inboundQuantity += record.quantity;
  }
  return weeks;
}

export function summarizeDaily(points: DailyTrendPoint[], dayCount: number) {
  const total = points.reduce((sum, point) => sum + point.quantity, 0);
  const peak = points.reduce<DailyTrendPoint | null>(
    (best, point) => (!best || point.quantity > best.quantity ? point : best),
    null,
  );
  return {
    recordedDays: points.length,
    dayCount,
    averagePerRecordedDay: points.length ? total / points.length : 0,
    peak,
  };
}

export function summarizeWeekly(weeks: WeeklyTrendPoint[]) {
  const full = weeks.filter((week) => !week.partial);
  const best = full.reduce<WeeklyTrendPoint | null>(
    (top, week) => (!top || week.quantity > top.quantity ? week : top),
    null,
  );
  const last = full.at(-1);
  const previous = full.at(-2);
  return {
    averagePerFullWeek: full.length
      ? full.reduce((sum, week) => sum + week.quantity, 0) / full.length
      : null,
    best,
    // 上一个完整周为 0 时环比无意义，返回 null 而不是无穷大。
    weekOverWeek:
      last && previous && previous.quantity > 0
        ? (last.quantity - previous.quantity) / previous.quantity
        : null,
  };
}

/** 进货记录日期落在销售区间内的，才能在趋势图上标出到货。 */
export function inboundMarkers(inbound: SeasonalInboundRecord[], range: SeasonalRange) {
  const byDate = new Map<string, number>();
  for (const record of inbound) {
    const date = record.date.slice(0, 10);
    if (date < range.startDate || date > range.endDate) continue;
    byDate.set(date, (byDate.get(date) ?? 0) + record.quantity);
  }
  const start = toUtc(range.startDate);
  return [...byDate.entries()]
    .map(([date, quantity]) => ({
      date,
      quantity,
      index: Math.round((toUtc(date) - start) / DAY_MS),
    }))
    .sort((a, b) => a.index - b.index);
}

export function formatQuantity(value: number) {
  const rounded = Math.round(value * 100) / 100;
  const sign = rounded < 0 ? "−" : "";
  return `${sign}${Math.abs(rounded).toLocaleString("en-AU", { maximumFractionDigits: 2 })}`;
}

export function shortDate(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return match ? `${match[2]}/${match[3]}` : "—";
}

export function weekdayIndex(value: string) {
  return new Date(toUtc(value)).getUTCDay();
}

// ───────── 候选卡片：售罄率、状态标签、排序 ─────────

/** 售罄率达到该值标「快售罄」。 */
export const HOT_SELL_THROUGH = 0.85;
/** 到货满该天数仍低于 SLOW_SELL_THROUGH 标「动销慢」。 */
export const SLOW_MIN_DAYS = 28;
export const SLOW_SELL_THROUGH = 0.25;

/** 售罄率 = 销量 ÷ 进货；进货为 0 或数据缺失时没有意义，返回 null 而不是 0 或无穷大。 */
export function sellThroughRate(inbound: number | null | undefined, sales: number | null | undefined) {
  if (inbound == null || sales == null || !(inbound > 0)) return null;
  return sales / inbound;
}

export function formatPercent(rate: number) {
  return `${Math.round(rate * 100)}%`;
}

/** 两个 YYYY-MM-DD 相差的天数（后者减前者）。 */
export function daysBetween(from: string, to: string) {
  return Math.round((toUtc(to) - toUtc(from)) / DAY_MS);
}

export interface CandidateStats {
  theoreticalStock: number;
  inboundQuantity: number | null;
  salesQuantity: number | null;
  firstInboundDate: string | null;
}

/** 负库存优先，其次快售罄；动销慢要求最早进货日距今满 28 天，避免把刚到货的新品误标成滞销。 */
export function candidateFlag(item: CandidateStats, today: string): SeasonalCandidateFlag | null {
  if (item.theoreticalStock < 0) return "negative";
  const rate = sellThroughRate(item.inboundQuantity, item.salesQuantity);
  if (rate == null) return null;
  if (rate >= HOT_SELL_THROUGH) return "hot";
  if (
    rate < SLOW_SELL_THROUGH &&
    item.firstInboundDate &&
    daysBetween(item.firstInboundDate.slice(0, 10), today) >= SLOW_MIN_DAYS
  ) {
    return "slow";
  }
  return null;
}

/** 排序不改原数组；售罄率没有值的（无进货）排最后，同值按货号保持稳定。 */
export function sortCandidates<T extends CandidateStats & { itemNumber: string | null }>(
  items: T[],
  sort: SeasonalCandidateSort,
) {
  const byItemNumber = (a: T, b: T) => (a.itemNumber ?? "").localeCompare(b.itemNumber ?? "");
  const copy = [...items];
  if (sort === "itemNumber") return copy.sort(byItemNumber);
  if (sort === "stock") return copy.sort((a, b) => b.theoreticalStock - a.theoreticalStock || byItemNumber(a, b));
  return copy.sort((a, b) => {
    const ra = sellThroughRate(a.inboundQuantity, a.salesQuantity);
    const rb = sellThroughRate(b.inboundQuantity, b.salesQuantity);
    if (ra == null && rb == null) return byItemNumber(a, b);
    if (ra == null) return 1;
    if (rb == null) return -1;
    return rb - ra || byItemNumber(a, b);
  });
}

// ───────── 详情：销售速度与预计卖完天数 ─────────

const PACE_WEEKS = 4;

/**
 * 最近 4 个「完整周」的日均销量，和按此速度还能卖多少天。
 * 完整周不含区间首尾被截断的周，因此不会把今天未结束的数据算进去；没有完整周或这几周一件没卖时返回 null。
 */
export function summarizePace(weeks: WeeklyTrendPoint[], stock: number) {
  const recent = weeks.filter((week) => !week.partial).slice(-PACE_WEEKS);
  if (!recent.length) return null;
  const perDay = recent.reduce((sum, week) => sum + week.quantity, 0) / (recent.length * 7);
  if (!(perDay > 0)) return null;
  return {
    recentPerDay: perDay,
    weeks: recent.length,
    // 已经没有存货（≤ 0）时不估算卖完天数。
    daysLeft: stock > 0 ? Math.ceil(stock / perDay) : null,
  };
}

/** 整个销售区间的日均销量（含没有销售的天）。 */
export function seasonPerDay(salesQuantity: number, range: SeasonalRange) {
  return salesQuantity / Math.max(daysBetweenInclusive(range), 1);
}

/** 距离最近一次进货过了几天；区间内没有进货返回 null。 */
export function daysSinceLastInbound(records: SeasonalInboundRecord[], today: string) {
  const dates = records.map((record) => record.date.slice(0, 10)).sort();
  const last = dates.at(-1);
  return last ? Math.max(0, daysBetween(last, today)) : null;
}

// ───────── 详情：按周进销 ─────────

/**
 * 每周一行：进货、销量，以及周末理论存货（累计进货 − 累计销量）。
 * 周末存货只在进货区间与销售区间一致时才有意义，不一致时 withStock 传 false，整列为 null。
 * 返回按时间正序；界面再倒序展示（最近的周在最上）。
 */
export function buildWeeklyLedger(
  daily: SeasonalDailySales[],
  inbound: SeasonalInboundRecord[],
  range: SeasonalRange,
  withStock: boolean,
): WeeklyLedgerRow[] {
  const weeks = buildWeeklyTrend(daily, inbound, range);
  let stock = 0;
  return weeks.map((week) => {
    stock += week.inboundQuantity - week.quantity;
    return {
      startDate: week.startDate,
      endDate: week.endDate,
      partial: week.partial,
      inboundQuantity: week.inboundQuantity,
      salesQuantity: week.quantity,
      endStock: withStock ? stock : null,
      documentNos: [
        ...new Set(
          inbound
            .filter((record) => {
              const date = record.date.slice(0, 10);
              return date >= week.startDate && date <= week.endDate;
            })
            .map((record) => record.documentNo),
        ),
      ],
    };
  });
}

/** 展开一周：把区间内属于这一周的进货单和有销量的日期按日期倒序混排（同一天进货排在销量前）。 */
export function buildWeekEntries(
  week: { startDate: string; endDate: string },
  daily: SeasonalDailySales[],
  inbound: SeasonalInboundRecord[],
): LedgerDayEntry[] {
  const inWeek = (date: string) => date >= week.startDate && date <= week.endDate;
  const entries: LedgerDayEntry[] = [
    ...inbound
      .filter((record) => inWeek(record.date.slice(0, 10)))
      .map((record): LedgerDayEntry => ({
        kind: "inbound",
        date: record.date.slice(0, 10),
        documentNo: record.documentNo,
        quantity: record.quantity,
      })),
    ...daily
      .filter((item) => inWeek(item.date.slice(0, 10)))
      .map((item): LedgerDayEntry => ({ kind: "sales", date: item.date.slice(0, 10), quantity: item.quantity })),
  ];
  return entries.sort((a, b) => (a.date === b.date ? (a.kind === b.kind ? 0 : a.kind === "inbound" ? -1 : 1) : a.date < b.date ? 1 : -1));
}

/** 周图里进货柱超过纵轴上限会被截断：返回纵轴上限，由销量决定；只有进货没有销量时按进货定。 */
export function weeklyBarCeiling(weeks: WeeklyTrendPoint[]) {
  const salesMax = Math.max(0, ...weeks.map((week) => week.quantity));
  if (salesMax > 0) return salesMax;
  return Math.max(0, ...weeks.map((week) => week.inboundQuantity));
}

/**
 * 进货区间与销售区间的并集：按周进销与周图要同时放得下两边的数据。
 * 两个区间各自只返回区间内的记录，所以并集里每条记录仍只属于它自己的那个区间。
 */
export function unionRange(ranges: SeasonalRanges): SeasonalRange {
  return {
    startDate: ranges.inbound.startDate < ranges.sales.startDate ? ranges.inbound.startDate : ranges.sales.startDate,
    endDate: ranges.inbound.endDate > ranges.sales.endDate ? ranges.inbound.endDate : ranges.sales.endDate,
  };
}
