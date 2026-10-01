import assert from "node:assert/strict";
import type { BranchHourlyRevenueRow } from "./api";
import { groupHourlySeriesByBranch, resolveDefaultCutoff, sumBeforeHour } from "./hourly-cumulative";
import {
  FALLBACK_UTC_OFFSET_MINUTES,
  buildStoreTimeZoneMap,
  formatClockTimeAtOffset,
  getBranchOffsetMinutes,
  getBranchShiftHours,
  getCutoffClockAnchorUtc,
  getTimeZoneOffsetMinutes,
  resolveCutoffClock,
  shiftBranchHourlyRows,
  toBranchClockHour,
} from "./store-cutoff-clock";

function row(branchCode: string, hour: number, revenue: number, compareRevenue = 0): BranchHourlyRevenueRow {
  return {
    id: `${branchCode}:${hour}`,
    branchCode,
    branchName: branchCode,
    hour,
    revenue,
    compareRevenue,
    transactions: 0,
    compareTransactions: 0,
  };
}

// —— 时区偏移：悉尼 2026-10-04 进入夏令时，布里斯班全年 UTC+10 ——
const beforeDst = getCutoffClockAnchorUtc("2026-09-29");
const afterDst = getCutoffClockAnchorUtc("2026-10-06");
assert.equal(getTimeZoneOffsetMinutes("Australia/Sydney", beforeDst), 600);
assert.equal(getTimeZoneOffsetMinutes("Australia/Sydney", afterDst), 660, "夏令时期间悉尼是 UTC+11");
assert.equal(getTimeZoneOffsetMinutes("Australia/Brisbane", afterDst), 600, "布里斯班没有夏令时");
assert.equal(getTimeZoneOffsetMinutes("Australia/Adelaide", afterDst), 630);
assert.equal(getTimeZoneOffsetMinutes("Not/AZone", afterDst), null, "无效时区不能抛错");
assert.equal(getTimeZoneOffsetMinutes("Australia/Sydney", Number.NaN), null);
assert.equal(
  getTimeZoneOffsetMinutes("Australia/Sydney", getCutoffClockAnchorUtc("2026-10-04")),
  660,
  "切换当天按营业时段（中午）取偏移",
);

// —— 门店时区表 ——
const timeZones = buildStoreTimeZoneMap([
  { value: " 1013 ", timeZoneId: "Australia/Brisbane" },
  { value: "1012", timeZoneId: "Australia/Sydney" },
  { value: "1028", timeZoneId: "   " },
  { value: "1014" },
]);
assert.deepEqual([...timeZones.entries()], [
  ["1013", "Australia/Brisbane"],
  ["1012", "Australia/Sydney"],
]);

// —— 夏令时期间的全部分店：参考悉尼，布里斯班店与无时区门店平移一小时 ——
const allStores = ["1012", "1013", "1014"];
const dstClock = resolveCutoffClock({
  branchCodes: allStores,
  referenceBranchCodes: allStores,
  timeZoneByBranch: timeZones,
  atUtc: afterDst,
});
assert.equal(dstClock.referenceOffsetMinutes, 660);
assert.equal(getBranchShiftHours(dstClock, "1012"), 0);
assert.equal(getBranchShiftHours(dstClock, "1013"), 1);
assert.equal(getBranchShiftHours(dstClock, "1014"), 1, "时区未知按 UTC+10 回退，只会少算不会多算");
assert.equal(getBranchShiftHours(dstClock, " 1013 "), 1, "分店代码大小写与空格不影响查找");
assert.equal(getBranchOffsetMinutes(dstClock, "UNKNOWN"), FALLBACK_UTC_OFFSET_MINUTES);

// 悉尼 10:30（UTC 前一天 23:30）完成统计：悉尼店 9–10 点已完整，布里斯班店本地才 09:30。
const completedAt = "2026-10-05T23:30:42Z";
const dstCutoff = resolveDefaultCutoff({
  selectedDate: "2026-10-06",
  todayKey: "2026-10-06",
  statisticsCompletedAtUtc: completedAt,
  utcOffsetMinutes: dstClock.referenceOffsetMinutes,
});
assert.deepEqual(dstCutoff, { cutoffHour: 10, live: true, liveHourFraction: 0.5 }, "夏令时按悉尼时间截止到 10:00");
assert.equal(
  resolveDefaultCutoff({
    selectedDate: "2026-10-06",
    todayKey: "2026-10-06",
    statisticsCompletedAtUtc: completedAt,
  })?.cutoffHour,
  9,
  "旧的固定 UTC+10 会让悉尼店多滞后一小时",
);
assert.equal(formatClockTimeAtOffset(completedAt, dstClock.referenceOffsetMinutes), "10:30");
assert.equal(formatClockTimeAtOffset(completedAt, 600), "09:30");
assert.equal(formatClockTimeAtOffset(null, 600), null);

const rows = [
  row("1012", 9, 100, 90),
  row("1012", 10, 40, 80),
  row("1013", 8, 50, 45),
  row("1013", 9, 30, 70),
];
const shifted = shiftBranchHourlyRows(rows, dstClock);
const series = groupHourlySeriesByBranch(shifted);
assert.equal(sumBeforeHour(series.get("1012")!.revenue, 10), 100, "悉尼店截至本地 10:00");
assert.equal(sumBeforeHour(series.get("1012")!.compareRevenue, 10), 90);
assert.equal(sumBeforeHour(series.get("1013")!.revenue, 10), 50, "布里斯班店截至本地 09:00，进行中的 9 点不算");
assert.equal(sumBeforeHour(series.get("1013")!.compareRevenue, 10), 45, "去年同期按同一本地时刻对齐");
assert.equal(rows[2]!.hour, 8, "平移不能改写原始行");

// —— 选中布里斯班单店：参考就是布里斯班本地时间，悉尼店反向平移 ——
const brisbaneClock = resolveCutoffClock({
  branchCodes: allStores,
  referenceBranchCodes: ["1013"],
  timeZoneByBranch: timeZones,
  atUtc: afterDst,
});
assert.equal(brisbaneClock.referenceOffsetMinutes, 600);
assert.equal(getBranchShiftHours(brisbaneClock, "1013"), 0);
assert.equal(getBranchShiftHours(brisbaneClock, "1012"), -1);
assert.equal(
  resolveDefaultCutoff({
    selectedDate: "2026-10-06",
    todayKey: "2026-10-06",
    statisticsCompletedAtUtc: completedAt,
    utcOffsetMinutes: brisbaneClock.referenceOffsetMinutes,
  })?.cutoffHour,
  9,
  "布里斯班单店按本地 09:30 截止到 09:00",
);
const brisbaneSeries = groupHourlySeriesByBranch(shiftBranchHourlyRows(rows, brisbaneClock));
assert.equal(sumBeforeHour(brisbaneSeries.get("1012")!.revenue, 9), 100, "悉尼店在布里斯班时钟 09:00 即本地 10:00");

// —— 夏令时之外全部门店都是 UTC+10：结果与原来的固定偏移一致，且不复制数组 ——
const standardClock = resolveCutoffClock({
  branchCodes: allStores,
  referenceBranchCodes: allStores,
  timeZoneByBranch: timeZones,
  atUtc: beforeDst,
});
assert.equal(standardClock.referenceOffsetMinutes, 600);
assert.equal(shiftBranchHourlyRows(rows, standardClock), rows, "无需平移时保持同一引用");

// 旧后端不返回时区：退回固定 UTC+10。
const noTimeZoneClock = resolveCutoffClock({
  branchCodes: allStores,
  referenceBranchCodes: allStores,
  timeZoneByBranch: new Map(),
  atUtc: afterDst,
});
assert.equal(noTimeZoneClock.referenceOffsetMinutes, FALLBACK_UTC_OFFSET_MINUTES);
assert.equal(shiftBranchHourlyRows(rows, noTimeZoneClock), rows);
assert.equal(
  resolveCutoffClock({ branchCodes: [], referenceBranchCodes: [], timeZoneByBranch: timeZones, atUtc: afterDst })
    .referenceOffsetMinutes,
  FALLBACK_UTC_OFFSET_MINUTES,
);

// 半小时时区向上取整平移：只会少算。
const adelaideClock = resolveCutoffClock({
  branchCodes: ["1012", "9001"],
  referenceBranchCodes: ["1012", "9001"],
  timeZoneByBranch: new Map([["1012", "Australia/Sydney"], ["9001", "Australia/Adelaide"]]),
  atUtc: afterDst,
});
assert.equal(getBranchShiftHours(adelaideClock, "9001"), 1);

// 超出 0–23 的小时并入首尾，整天合计不变。
const edgeRows = shiftBranchHourlyRows([row("1013", 23, 5), row("1012", 0, 7)], dstClock);
assert.deepEqual(edgeRows.map((item) => item.hour), [23, 0]);
const edgeBrisbane = shiftBranchHourlyRows([row("1012", 0, 7)], brisbaneClock);
assert.deepEqual(edgeBrisbane.map((item) => item.hour), [0]);

// —— 参考时钟整点换成门店本地整点 ——
assert.equal(toBranchClockHour(10, 1), 9);
assert.equal(toBranchClockHour(9, -1), 10);
assert.equal(toBranchClockHour(24, 1), 24, "整天口径保持整天");
assert.equal(toBranchClockHour(0, 1), 0);
