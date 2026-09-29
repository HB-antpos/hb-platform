import assert from "node:assert/strict";
import type { BranchHourlyRevenueRow, BranchRevenueRow, HourlyRevenueRow } from "./api";
import {
  FULL_DAY_CUTOFF_HOUR,
  alignBranchRowsToCutoff,
  buildCumulativeChartModel,
  buildHourlyDetailRows,
  buildHourlySeries,
  formatHourLabel,
  formatLocalClockTime,
  getCumulativeTotals,
  getCutoffOptions,
  getDisplayCutoffHour,
  getLiveHourSummary,
  groupHourlySeriesByBranch,
  isLowBase,
  parseUtcTimestamp,
  resolveDefaultCutoff,
  resolveEffectiveCutoff,
  sumBeforeHour,
} from "./hourly-cumulative";

function row(hour: number, revenue: number, compareRevenue: number, transactions = 0, compareTransactions = 0): HourlyRevenueRow {
  return {
    id: String(hour),
    hour,
    label: formatHourLabel(hour),
    revenue,
    compareRevenue,
    revenueDelta: revenue - compareRevenue,
    revenueDeltaRatio: compareRevenue ? (revenue - compareRevenue) / compareRevenue : null,
    transactions,
    compareTransactions,
    averageTransaction: transactions ? revenue / transactions : 0,
    compareAverageTransaction: compareTransactions ? compareRevenue / compareTransactions : 0,
  };
}

// 2026-09-21 15:30 这一轮统计的全部分店小时数据（今天到 15:30，去年同 ISO 周同星期全天）。
const today: Record<number, number> = { 8: 224, 9: 5596, 10: 8902, 11: 9030, 12: 9283, 13: 7955, 14: 6836, 15: 3583 };
const lastYear: Record<number, number> = {
  8: 405, 9: 6824, 10: 8736, 11: 9143, 12: 9026, 13: 8417, 14: 7242, 15: 6990, 16: 6576, 17: 2721, 18: 92,
};
const allStoreRows = Array.from({ length: 24 }, (_, hour) => hour)
  .filter((hour) => today[hour] !== undefined || lastYear[hour] !== undefined)
  .map((hour) => row(hour, today[hour] ?? 0, lastYear[hour] ?? 0, today[hour] ? 10 : 0, lastYear[hour] ? 8 : 0));

// 逐小时对齐与累计
const series = buildHourlySeries(allStoreRows);
assert.equal(series.firstHour, 8);
assert.equal(series.endHour, 19);
assert.equal(sumBeforeHour(series.revenue, 15), 47_826);
assert.equal(sumBeforeHour(series.compareRevenue, 15), 49_793);
assert.equal(sumBeforeHour(series.revenue, 99), 51_409, "越界截止按整天处理");
assert.equal(sumBeforeHour(series.revenue, -3), 0);
assert.deepEqual(getCumulativeTotals(series, 10), {
  revenue: 5_820,
  compareRevenue: 7_229,
  transactions: 20,
  compareTransactions: 16,
});

const duplicated = buildHourlySeries([row(9, 10, 5), row(9, 2, 1), row(30, 99, 99), row(Number.NaN, 1, 1)]);
assert.equal(duplicated.revenue[9], 12, "同一小时多行必须累加");
assert.equal(duplicated.compareRevenue[9], 6);
assert.equal(duplicated.firstHour, 9);
assert.equal(duplicated.endHour, 10);
assert.equal(buildHourlySeries([]).firstHour, null);

// 小基数：09:00 去年同期只有 $405，占去年全天 0.6%，百分比会失真
const lastYearFullDay = sumBeforeHour(series.compareRevenue, FULL_DAY_CUTOFF_HOUR);
assert.equal(isLowBase(405, lastYearFullDay), true);
assert.equal(isLowBase(7_229, lastYearFullDay), false);
assert.equal(isLowBase(0, 0), false, "去年全天为 0 时交给「新增」口径处理，不算小基数");

// UTC 时间解析：没有时区标记的字符串按 UTC 解释
assert.equal(parseUtcTimestamp("2026-09-21T05:30:03Z"), Date.UTC(2026, 8, 21, 5, 30, 3));
assert.equal(parseUtcTimestamp("2026-09-21T05:30:03"), Date.UTC(2026, 8, 21, 5, 30, 3));
assert.equal(parseUtcTimestamp("2026-09-21T15:30:03+10:00"), Date.UTC(2026, 8, 21, 5, 30, 3));
assert.equal(parseUtcTimestamp("not a date"), null);
assert.equal(parseUtcTimestamp(null), null);

// 本地「时:分」：CI 固定 TZ=Australia/Brisbane，这里只断言格式与失败回退
assert.match(formatLocalClockTime("2026-09-21T05:31:00Z") ?? "", /^\d{2}:\d{2}$/);
assert.equal(formatLocalClockTime("bad"), null);
assert.equal(formatLocalClockTime(undefined), null);

// 默认截止整点
assert.deepEqual(
  resolveDefaultCutoff({ selectedDate: "2026-09-21", todayKey: "2026-09-21", statisticsCompletedAtUtc: "2026-09-21T05:30:03Z" }),
  { cutoffHour: 15, live: true, liveHourFraction: 0.5 },
  "15:30 统计完成时 15 点之前的小时都已完整",
);
assert.deepEqual(
  resolveDefaultCutoff({ selectedDate: "2026-09-20", todayKey: "2026-09-21", statisticsCompletedAtUtc: "2026-09-21T05:30:03Z" }),
  { cutoffHour: FULL_DAY_CUTOFF_HOUR, live: false, liveHourFraction: 0 },
  "历史日期比较整天",
);
assert.deepEqual(
  resolveDefaultCutoff({ selectedDate: "2026-09-21", todayKey: "2026-09-21", statisticsCompletedAtUtc: "2026-09-20T10:00:00Z" }),
  { cutoffHour: 0, live: true, liveHourFraction: 0 },
  "今天尚未统计时没有完整小时",
);
assert.equal(
  resolveDefaultCutoff({ selectedDate: "2026-09-21", todayKey: "2026-09-21", statisticsCompletedAtUtc: null }),
  null,
  "统计时间未知时不猜整点",
);
assert.equal(
  resolveDefaultCutoff({ selectedDate: "2026-09-22", todayKey: "2026-09-21", statisticsCompletedAtUtc: "2026-09-21T05:30:03Z" }),
  null,
);
// 夏令时：悉尼 15:30（UTC+11）= 布里斯班 14:30，截止取 14 点，布里斯班店不会拿到半截的 14 点
assert.equal(
  resolveDefaultCutoff({ selectedDate: "2026-10-05", todayKey: "2026-10-05", statisticsCompletedAtUtc: "2026-10-05T04:30:00Z" })?.cutoffHour,
  14,
);
const originalTimeZone = process.env.TZ;
try {
  // 结果只取决于统计时刻本身，与运行设备的时区无关
  process.env.TZ = "America/New_York";
  assert.equal(
    resolveDefaultCutoff({ selectedDate: "2026-09-21", todayKey: "2026-09-21", statisticsCompletedAtUtc: "2026-09-21T05:30:03Z" })?.cutoffHour,
    15,
  );
} finally {
  if (originalTimeZone === undefined) delete process.env.TZ;
  else process.env.TZ = originalTimeZone;
}

// 可点选的截止整点
assert.deepEqual(getCutoffOptions(series, 15), [9, 10, 11, 12, 13, 14, 15]);
assert.deepEqual(getCutoffOptions(series, FULL_DAY_CUTOFF_HOUR), [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19]);
assert.deepEqual(getCutoffOptions(series, 8), [], "开门前没有可比较的整点");
assert.deepEqual(getCutoffOptions(buildHourlySeries([]), 15), []);
assert.equal(resolveEffectiveCutoff(null, 15), 15);
assert.equal(resolveEffectiveCutoff(12, 15), 12);
assert.equal(resolveEffectiveCutoff(18, 15), 15, "不能选到尚未完整的小时");
assert.equal(getDisplayCutoffHour(series, FULL_DAY_CUTOFF_HOUR), 19, "整天截止显示为最后营业整点");
assert.equal(getDisplayCutoffHour(series, 15), 15);

// 下钻表：累计口径与状态
const cumulativeRows = buildHourlyDetailRows(allStoreRows, { cutoffHour: 15, live: true, cumulative: true });
assert.deepEqual(cumulativeRows.map((detail) => detail.status), [
  "complete", "complete", "complete", "complete", "complete", "complete", "complete", "live", "upcoming", "upcoming", "upcoming",
]);
const cutoffRow = cumulativeRows.find((detail) => detail.isCutoffRow);
assert.equal(cutoffRow?.hour, 14);
assert.equal(cutoffRow?.boundaryHour, 15);
assert.equal(cutoffRow?.revenue, 47_826);
assert.equal(cutoffRow?.compareRevenue, 49_793);
assert.equal(cutoffRow?.transactions, 70);
assert.equal(cutoffRow?.averageTransaction, 47_826 / 70, "累计客单价用累计营业额除以累计单数");
assert.equal(cumulativeRows[cumulativeRows.length - 1].compareRevenue, lastYearFullDay);

const pickedRows = buildHourlyDetailRows(allStoreRows, { cutoffHour: 15, live: true, cumulative: true, highlightCutoffHour: 12 });
assert.equal(pickedRows.find((detail) => detail.isCutoffRow)?.hour, 11, "高亮跟随用户点选的整点");
assert.equal(pickedRows.find((detail) => detail.hour === 15)?.status, "live", "状态仍按最近完整整点判断");

const perHourRows = buildHourlyDetailRows([...allStoreRows].reverse(), { cutoffHour: 15, live: true, cumulative: false });
assert.equal(perHourRows[0].hour, 8, "逐小时行按小时升序");
assert.equal(perHourRows[6].revenue, 6_836);
assert.equal(perHourRows[6].compareRevenue, 7_242);
const pastRows = buildHourlyDetailRows(allStoreRows, { cutoffHour: FULL_DAY_CUTOFF_HOUR, live: false, cumulative: true });
assert.ok(pastRows.every((detail) => detail.status === "complete"), "历史日期全部为完整小时");

// 累计曲线模型
const chart = buildCumulativeChartModel(series, { cutoffHour: 15, live: true, liveHourFraction: 0.5 });
assert.ok(chart);
assert.equal(chart.startHour, 8);
assert.equal(chart.endHour, 19);
assert.equal(chart.comparePoints.length, 12);
assert.equal(chart.currentPoints.length, 8);
assert.deepEqual(chart.currentPoints[chart.currentPoints.length - 1], { hour: 15, value: 47_826 });
assert.deepEqual(chart.liveTail, { hour: 15.5, value: 51_409 });
assert.deepEqual(chart.gapRegions.map((region) => region.tone), ["behind"]);
assert.equal(chart.maxValue, 66_172);

const crossing = buildCumulativeChartModel(
  buildHourlySeries([row(9, 100, 300), row(10, 500, 100), row(11, 0, 50)]),
  { cutoffHour: 12, live: false, liveHourFraction: 0 },
);
assert.ok(crossing);
assert.deepEqual(crossing.gapRegions.map((region) => region.tone), ["behind", "ahead"], "交叉后必须分段着色");
const crossingPoint = crossing.gapRegions[0].points[crossing.gapRegions[0].points.length - 1];
assert.equal(crossingPoint.current, crossingPoint.compare, "分段点落在两线交点上");
assert.equal(crossing.liveTail, null, "历史日期没有进行中的尾段");
assert.equal(buildCumulativeChartModel(buildHourlySeries([]), { cutoffHour: 15, live: true, liveHourFraction: 0 }), null);

// 分店排行对齐：按分店拆序列，截止后重排；小时数据里没有的分店按 0 处理
function branchHourly(branchCode: string, hour: number, revenue: number, compareRevenue: number): BranchHourlyRevenueRow {
  return {
    id: `${branchCode}:${hour}`,
    branchCode,
    branchName: branchCode,
    hour,
    revenue,
    compareRevenue,
    transactions: revenue > 0 ? 1 : 0,
    compareTransactions: compareRevenue > 0 ? 1 : 0,
  };
}
function branchRow(branchCode: string, revenue: number, compareRevenue: number): BranchRevenueRow {
  return {
    id: branchCode,
    branchCode,
    branchName: branchCode,
    revenue,
    compareRevenue,
    revenueDelta: revenue - compareRevenue,
    revenueDeltaRatio: compareRevenue ? (revenue - compareRevenue) / compareRevenue : null,
    transactions: 0,
    compareTransactions: 0,
    averageTransaction: 0,
    compareAverageTransaction: 0,
  };
}
const byBranch = groupHourlySeriesByBranch([
  branchHourly("1013", 9, 1_000, 1_500),
  branchHourly("1013", 10, 2_000, 1_000),
  branchHourly("1013", 15, 500, 900),
  branchHourly("1013", 16, 0, 800),
  branchHourly("1017", 9, 500, 300),
  branchHourly("1017", 10, 900, 400),
]);
assert.equal(byBranch.size, 2);
assert.equal(byBranch.get("1013")?.endHour, 17);
// 日统计的整天值：Orion 今天 3,500（含进行中的 15 点）、去年全天 4,200；Waratah 1,400 / 700；Lutwyche 今天无销售
const alignedRanking = alignBranchRowsToCutoff(
  [branchRow("1013", 3_500, 4_200), branchRow("1001", 0, 1_822), branchRow("1017", 1_400, 700)],
  byBranch,
  15,
);
assert.deepEqual(alignedRanking.map((ranked) => ranked.branchCode), ["1013", "1017", "1001"]);
assert.equal(alignedRanking[0].revenue, 3_000, "进行中的 15 点不计入");
assert.equal(alignedRanking[0].compareRevenue, 2_500, "去年只取到 15 点，不再拿全天比");
assert.equal(alignedRanking[0].revenueDelta, 500);
assert.equal(alignedRanking[0].revenueDeltaRatio, 0.2);
assert.equal(alignedRanking[0].averageTransaction, 1_500, "对齐后客单价用累计营业额除以累计单数");
assert.equal(alignedRanking[2].revenue, 0);
assert.equal(alignedRanking[2].compareRevenue, 0, "小时数据里没有的分店两期都按 0");
assert.equal(alignedRanking[2].revenueDeltaRatio, null);
assert.equal(alignedRanking[0].branchName, "1013", "对齐只替换数值字段，保留分店信息");

// —— 进行中小时的提示：2026-09-29 Orion 10:30 统计，10 点只有 10:00–10:30 的半小时 ——
{
  const orion = buildHourlySeries([row(9, 217.73, 401.32), row(10, 239.47, 743.4)]);
  assert.deepEqual(
    getLiveHourSummary(orion, { cutoffHour: 10, live: true, liveHourFraction: 0.5 }),
    { startHour: 10, nextHour: 11, revenue: 239.47 },
    "半点统计后进行中的半小时单独提示，不进对比",
  );
  assert.equal(
    getLiveHourSummary(orion, { cutoffHour: 10, live: true, liveHourFraction: 0 }),
    null,
    "整点统计刚完成（xx:00:22）时进行中小时只有几秒入账，不提示「10:00–10:00」",
  );
  assert.equal(getLiveHourSummary(orion, { cutoffHour: 24, live: false, liveHourFraction: 0 }), null, "历史日期没有进行中的小时");
  assert.equal(
    getLiveHourSummary(orion, { cutoffHour: 11, live: true, liveHourFraction: 0.5 }),
    null,
    "进行中小时尚无入账时不提示",
  );
}

console.log("hourly-cumulative.test.ts: ok");
