import type { BranchHourlyRevenueRow, BranchRevenueRow, HourlyRevenueRow } from "./api";

/** 逐小时累计只用到四个金额/单数字段，单店与多店小时行都满足。 */
export type HourlyAmounts = Pick<
  HourlyRevenueRow,
  "hour" | "revenue" | "compareRevenue" | "transactions" | "compareTransactions"
>;

/** 历史日期默认比较整天；截止整点取 24 表示不截断。 */
export const FULL_DAY_CUTOFF_HOUR = 24;

/** 去年同期累计不足去年全天的 5% 时，百分比会被极小分母放大，改显示金额差。 */
export const LOW_BASE_SHARE = 0.05;

/**
 * 截止整点的默认参考偏移：固定 UTC+10。
 * 营业额页面会按门店时区（store-cutoff-clock.ts）换算参考时钟；拿不到门店时区时才回退到这里。
 * 门店只分布在悉尼与布里斯班，小时桶是各店本地墙钟；取最西且无夏令时的 UTC+10，
 * 夏令时期间悉尼店的同一整点也已结束，任何门店都不会把半截小时拿去和去年整小时比较。
 */
export const CUTOFF_REFERENCE_UTC_OFFSET_MINUTES = 10 * 60;

const HOURS_PER_DAY = 24;

export interface HourlySeries {
  revenue: number[];
  compareRevenue: number[];
  transactions: number[];
  compareTransactions: number[];
  /** 两期任一有数据的最早小时；没有数据时为 null。 */
  firstHour: number | null;
  /** 两期任一有数据的最晚小时 + 1，即坐标轴终点。 */
  endHour: number | null;
}

export interface CumulativeTotals {
  revenue: number;
  compareRevenue: number;
  transactions: number;
  compareTransactions: number;
}

export interface CutoffResolution {
  /** 只比较 Hour < cutoffHour 的完整小时。 */
  cutoffHour: number;
  /** 所选日期仍在营业中：截止之后还有进行中的小时。 */
  live: boolean;
  /** 最近一次统计时刻在当前小时内的进度（0–1），用于画进行中的尾段。 */
  liveHourFraction: number;
}

export type HourlyDetailStatus = "complete" | "live" | "upcoming";

export interface HourlyDetailView extends HourlyRevenueRow {
  status: HourlyDetailStatus;
  /** 累计口径下这一行的「截至」整点，即 hour + 1。 */
  boundaryHour: number;
  /** 这一行正好是当前截止整点，用于高亮。 */
  isCutoffRow: boolean;
}

export interface CumulativePoint {
  hour: number;
  value: number;
}

export interface GapPoint {
  hour: number;
  current: number;
  compare: number;
}

export interface GapRegion {
  /** 当期领先为 ahead，落后为 behind。 */
  tone: "ahead" | "behind";
  points: GapPoint[];
}

export interface CumulativeChartModel {
  startHour: number;
  endHour: number;
  currentPoints: CumulativePoint[];
  comparePoints: CumulativePoint[];
  gapRegions: GapRegion[];
  liveTail: CumulativePoint | null;
  maxValue: number;
}

function finiteOrZero(value: number) {
  return Number.isFinite(value) ? value : 0;
}

function createEmptyHours() {
  return Array.from({ length: HOURS_PER_DAY }, () => 0);
}

function clampHour(hour: number) {
  return Math.max(0, Math.min(HOURS_PER_DAY, Math.trunc(hour)));
}

function pad(value: number) {
  return String(value).padStart(2, "0");
}

export function formatHourLabel(hour: number) {
  return `${pad(clampHour(hour))}:00`;
}

/** 把接口返回的逐小时行对齐成 24 个槽位；同一小时出现多行（多家店）时累加。 */
export function buildHourlySeries(rows: readonly HourlyAmounts[]): HourlySeries {
  const series: HourlySeries = {
    revenue: createEmptyHours(),
    compareRevenue: createEmptyHours(),
    transactions: createEmptyHours(),
    compareTransactions: createEmptyHours(),
    firstHour: null,
    endHour: null,
  };
  for (const row of rows) {
    const hour = Math.trunc(row.hour);
    if (!Number.isFinite(hour) || hour < 0 || hour >= HOURS_PER_DAY) continue;
    series.revenue[hour] += finiteOrZero(row.revenue);
    series.compareRevenue[hour] += finiteOrZero(row.compareRevenue);
    series.transactions[hour] += finiteOrZero(row.transactions);
    series.compareTransactions[hour] += finiteOrZero(row.compareTransactions);
  }
  for (let hour = 0; hour < HOURS_PER_DAY; hour += 1) {
    const hasData = series.revenue[hour] !== 0
      || series.compareRevenue[hour] !== 0
      || series.transactions[hour] !== 0
      || series.compareTransactions[hour] !== 0;
    if (!hasData) continue;
    if (series.firstHour === null) series.firstHour = hour;
    series.endHour = hour + 1;
  }
  return series;
}

/** 多店小时行按分店拆成各自的 24 小时序列。 */
export function groupHourlySeriesByBranch(rows: readonly BranchHourlyRevenueRow[]) {
  const rowsByBranch = new Map<string, BranchHourlyRevenueRow[]>();
  for (const row of rows) {
    const branchRows = rowsByBranch.get(row.branchCode);
    if (branchRows) branchRows.push(row);
    else rowsByBranch.set(row.branchCode, [row]);
  }
  const seriesByBranch = new Map<string, HourlySeries>();
  rowsByBranch.forEach((branchRows, branchCode) => {
    seriesByBranch.set(branchCode, buildHourlySeries(branchRows));
  });
  return seriesByBranch;
}

/**
 * 分店排行对齐到截止整点：本期与同期都只累计 Hour < cutoffHour，再按对齐后的营业额重排。
 * 小时数据里没有出现的分店（两期都没有销售）按 0 处理，与日统计表的口径一致。
 */
export function alignBranchRowsToCutoff(
  rows: readonly BranchRevenueRow[],
  seriesByBranch: ReadonlyMap<string, HourlySeries>,
  cutoffHour: number,
): BranchRevenueRow[] {
  return rows
    .map((row, index) => {
      const series = seriesByBranch.get(row.branchCode);
      const revenue = series ? sumBeforeHour(series.revenue, cutoffHour) : 0;
      const compareRevenue = series ? sumBeforeHour(series.compareRevenue, cutoffHour) : 0;
      const transactions = series ? sumBeforeHour(series.transactions, cutoffHour) : 0;
      const compareTransactions = series ? sumBeforeHour(series.compareTransactions, cutoffHour) : 0;
      const aligned: BranchRevenueRow = {
        ...row,
        revenue,
        compareRevenue,
        revenueDelta: revenue - compareRevenue,
        revenueDeltaRatio: compareRevenue !== 0 ? (revenue - compareRevenue) / compareRevenue : null,
        transactions,
        compareTransactions,
        averageTransaction: transactions > 0 ? revenue / transactions : 0,
        compareAverageTransaction: compareTransactions > 0 ? compareRevenue / compareTransactions : 0,
      };
      return { aligned, index };
    })
    .sort((a, b) => b.aligned.revenue - a.aligned.revenue || a.index - b.index)
    .map(({ aligned }) => aligned);
}

/** 累计 Hour < cutoffHour 的值。 */
export function sumBeforeHour(values: readonly number[], cutoffHour: number) {
  const end = clampHour(cutoffHour);
  let total = 0;
  for (let hour = 0; hour < end; hour += 1) {
    total += values[hour] ?? 0;
  }
  return total;
}

export function getCumulativeTotals(series: HourlySeries, cutoffHour: number): CumulativeTotals {
  return {
    revenue: sumBeforeHour(series.revenue, cutoffHour),
    compareRevenue: sumBeforeHour(series.compareRevenue, cutoffHour),
    transactions: sumBeforeHour(series.transactions, cutoffHour),
    compareTransactions: sumBeforeHour(series.compareTransactions, cutoffHour),
  };
}

export function isLowBase(compareCumulative: number, compareFullDay: number) {
  return compareFullDay > 0 && compareCumulative < compareFullDay * LOW_BASE_SHARE;
}

/** 后端时间字段按 UTC 解释；缺少时区标记的字符串补 Z，避免被当成设备本地时间。 */
export function parseUtcTimestamp(value: string | null | undefined) {
  if (!value) return null;
  const trimmed = value.trim();
  const hasZone = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(trimmed);
  const timestamp = Date.parse(hasZone ? trimmed : `${trimmed}Z`);
  return Number.isFinite(timestamp) ? timestamp : null;
}

/** 统计完成时刻的本地「时:分」，与报告中心页头的 Latest statistics 同一时区口径。 */
export function formatLocalClockTime(value: string | null | undefined) {
  const timestamp = parseUtcTimestamp(value);
  if (timestamp === null) return null;
  const date = new Date(timestamp);
  return `${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/**
 * 默认截止整点：历史日期比整天；当天取最近一次统计完成时刻所在整点（之前的小时都已完整）。
 * 统计时间未知时返回 null，调用方回退到不截断的旧口径，而不是猜一个整点。
 */
export function resolveDefaultCutoff({
  selectedDate,
  todayKey,
  statisticsCompletedAtUtc,
  utcOffsetMinutes = CUTOFF_REFERENCE_UTC_OFFSET_MINUTES,
}: {
  selectedDate: string;
  todayKey: string;
  statisticsCompletedAtUtc: string | null | undefined;
  utcOffsetMinutes?: number;
}): CutoffResolution | null {
  if (selectedDate < todayKey) {
    return { cutoffHour: FULL_DAY_CUTOFF_HOUR, live: false, liveHourFraction: 0 };
  }
  if (selectedDate > todayKey) return null;

  const completedAt = parseUtcTimestamp(statisticsCompletedAtUtc);
  if (completedAt === null) return null;
  const reference = new Date(completedAt + utcOffsetMinutes * 60_000);
  const referenceDate = `${reference.getUTCFullYear()}-${pad(reference.getUTCMonth() + 1)}-${pad(reference.getUTCDate())}`;
  if (referenceDate < selectedDate) {
    // 今天还没有跑过统计：没有任何完整小时。
    return { cutoffHour: 0, live: true, liveHourFraction: 0 };
  }
  if (referenceDate > selectedDate) {
    return { cutoffHour: FULL_DAY_CUTOFF_HOUR, live: false, liveHourFraction: 0 };
  }
  return {
    cutoffHour: reference.getUTCHours(),
    live: true,
    liveHourFraction: reference.getUTCMinutes() / 60,
  };
}

export interface LiveHourSummary {
  /** 进行中小时的起点，即默认截止整点。 */
  startHour: number;
  /** 下一次整点统计后该小时才计入对比。 */
  nextHour: number;
  /** 进行中小时已统计到的营业额（截止整点之后的全部入账）。 */
  revenue: number;
}

/**
 * 默认截止之后进行中的小时：去年同期只有整小时数据，半小时不能拿去比较，只单独展示。
 * 整点那次统计在 xx:00 后几十秒完成，此时进行中的小时只有几秒入账，不值得提示；
 * 统计时刻至少过了该整点一分钟、且确有入账时才返回。
 */
export function getLiveHourSummary(series: HourlySeries, cutoff: CutoffResolution): LiveHourSummary | null {
  if (!cutoff.live || cutoff.cutoffHour >= HOURS_PER_DAY || cutoff.liveHourFraction <= 0) return null;
  const revenue = sumBeforeHour(series.revenue, HOURS_PER_DAY) - sumBeforeHour(series.revenue, cutoff.cutoffHour);
  if (Math.round(revenue) === 0) return null;
  return { startHour: cutoff.cutoffHour, nextHour: cutoff.cutoffHour + 1, revenue };
}

/** 可点选的截止整点：从最早营业小时之后一直到默认截止（或最后营业小时）。 */
export function getCutoffOptions(series: HourlySeries, maxCutoffHour: number) {
  if (series.firstHour === null || series.endHour === null) return [];
  const start = series.firstHour + 1;
  const end = Math.min(clampHour(maxCutoffHour), series.endHour);
  const options: number[] = [];
  for (let hour = start; hour <= end; hour += 1) options.push(hour);
  return options;
}

/** 用户点选的整点不能晚于默认截止；超出或未选时回到默认。 */
export function resolveEffectiveCutoff(selectedCutoffHour: number | null, defaultCutoffHour: number) {
  if (selectedCutoffHour === null) return defaultCutoffHour;
  return selectedCutoffHour <= defaultCutoffHour ? selectedCutoffHour : defaultCutoffHour;
}

/** 截止整点落在营业时段之后（例如历史日期取 24）时，显示为最后营业整点。 */
export function getDisplayCutoffHour(series: HourlySeries, cutoffHour: number) {
  if (series.endHour === null) return cutoffHour;
  return Math.min(cutoffHour, series.endHour);
}

/**
 * 下钻表的行：累计口径把本期与对比期都从开门累加到该小时结束；
 * 当天进行中的小时不算增长率，尚未到达的小时只保留去年值，避免出现 $0 / −100%。
 */
export function buildHourlyDetailRows(
  rows: readonly HourlyRevenueRow[],
  {
    cutoffHour,
    live,
    cumulative,
    highlightCutoffHour = cutoffHour,
  }: {
    /** 默认截止（最近完整整点）：决定进行中 / 未到的状态。 */
    cutoffHour: number;
    live: boolean;
    cumulative: boolean;
    /** 用户点选的截止整点：只决定高亮哪一行。 */
    highlightCutoffHour?: number;
  },
): HourlyDetailView[] {
  const sorted = [...rows].sort((a, b) => a.hour - b.hour);
  let revenue = 0;
  let compareRevenue = 0;
  let transactions = 0;
  let compareTransactions = 0;
  return sorted.map((row) => {
    revenue += finiteOrZero(row.revenue);
    compareRevenue += finiteOrZero(row.compareRevenue);
    transactions += finiteOrZero(row.transactions);
    compareTransactions += finiteOrZero(row.compareTransactions);
    const status: HourlyDetailStatus = !live || row.hour < cutoffHour
      ? "complete"
      : row.hour === cutoffHour
        ? "live"
        : "upcoming";
    const values = cumulative
      ? { revenue, compareRevenue, transactions, compareTransactions }
      : {
          revenue: finiteOrZero(row.revenue),
          compareRevenue: finiteOrZero(row.compareRevenue),
          transactions: finiteOrZero(row.transactions),
          compareTransactions: finiteOrZero(row.compareTransactions),
        };
    return {
      ...row,
      ...values,
      revenueDelta: values.revenue - values.compareRevenue,
      revenueDeltaRatio: values.compareRevenue !== 0
        ? (values.revenue - values.compareRevenue) / values.compareRevenue
        : null,
      // 累计客单价必须用累计营业额 / 累计单数，不能平均逐小时客单价。
      averageTransaction: values.transactions > 0 ? values.revenue / values.transactions : 0,
      compareAverageTransaction:
        values.compareTransactions > 0 ? values.compareRevenue / values.compareTransactions : 0,
      status,
      boundaryHour: row.hour + 1,
      isCutoffRow: row.hour + 1 === highlightCutoffHour,
    };
  });
}

/** 累计曲线的数据模型（与屏幕坐标无关，便于单测）。 */
export function buildCumulativeChartModel(
  series: HourlySeries,
  { cutoffHour, live, liveHourFraction }: { cutoffHour: number; live: boolean; liveHourFraction: number },
): CumulativeChartModel | null {
  if (series.firstHour === null || series.endHour === null) return null;
  const startHour = series.firstHour;
  const endHour = Math.max(series.endHour, live ? Math.min(cutoffHour + 1, HOURS_PER_DAY) : series.endHour);
  const lastCurrentHour = Math.max(startHour, Math.min(cutoffHour, endHour));

  const comparePoints: CumulativePoint[] = [];
  for (let hour = startHour; hour <= endHour; hour += 1) {
    comparePoints.push({ hour, value: sumBeforeHour(series.compareRevenue, hour) });
  }
  const currentPoints: CumulativePoint[] = [];
  for (let hour = startHour; hour <= lastCurrentHour; hour += 1) {
    currentPoints.push({ hour, value: sumBeforeHour(series.revenue, hour) });
  }

  const liveTotal = sumBeforeHour(series.revenue, HOURS_PER_DAY);
  const cutoffTotal = currentPoints[currentPoints.length - 1]?.value ?? 0;
  // 进行中小时已有入账时才画虚线尾段，位置按统计时刻在该小时内的进度。
  const liveTail = live && liveTotal > cutoffTotal
    ? { hour: lastCurrentHour + Math.max(0.1, Math.min(1, liveHourFraction)), value: liveTotal }
    : null;

  const gapRegions: GapRegion[] = [];
  let region: GapRegion | null = null;
  for (let index = 0; index < currentPoints.length; index += 1) {
    const point: GapPoint = { hour: currentPoints[index].hour, current: currentPoints[index].value, compare: comparePoints[index].value };
    const difference = point.current - point.compare;
    const tone: GapRegion["tone"] = difference >= 0 ? "ahead" : "behind";
    if (!region) {
      region = { tone, points: [point] };
      continue;
    }
    const previous: GapPoint = region.points[region.points.length - 1];
    const previousDifference = previous.current - previous.compare;
    if (previousDifference * difference < 0) {
      // 两条累计线在这一小时内交叉：按线性插值切开，前后分别着色。
      const ratio = previousDifference / (previousDifference - difference);
      const crossing: GapPoint = {
        hour: previous.hour + ratio * (point.hour - previous.hour),
        current: previous.current + ratio * (point.current - previous.current),
        compare: previous.current + ratio * (point.current - previous.current),
      };
      region.points.push(crossing);
      gapRegions.push(region);
      region = { tone, points: [crossing, point] };
      continue;
    }
    if (previousDifference === 0 && difference !== 0) region.tone = tone;
    region.points.push(point);
  }
  if (region && region.points.length > 1) gapRegions.push(region);

  const maxValue = Math.max(
    liveTotal,
    ...comparePoints.map((point) => point.value),
    ...currentPoints.map((point) => point.value),
  );
  return { startHour, endHour, currentPoints, comparePoints, gapRegions, liveTail, maxValue };
}
