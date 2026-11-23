import assert from "node:assert/strict";
import { test } from "node:test";
import {
  buildDailyTrend,
  buildWeekEntries,
  buildWeeklyLedger,
  buildWeeklyTrend,
  candidateFlag,
  canViewSeasonalProductInsights,
  daysSinceLastInbound,
  formatPercent,
  seasonPerDay,
  sellThroughRate,
  sortCandidates,
  summarizePace,
  unionRange,
  weeklyBarCeiling,
  formatQuantity,
  inboundMarkers,
  rangesDiffer,
  seasonStart,
  summarizeDaily,
  summarizeWeekly,
  validateSeasonalRange,
} from "./logic";

const salesRange = { startDate: "2026-08-01", endDate: "2026-09-23" };

test("季节起点：默认从最近一个 8 月 1 日开始", () => {
  assert.equal(seasonStart("2026-09-23"), "2026-08-01");
  assert.equal(seasonStart("2026-08-01"), "2026-08-01");
  assert.equal(seasonStart("2026-07-31"), "2025-08-01");
  assert.equal(seasonStart("2027-03-01"), "2026-08-01");
});

test("区间校验：格式、先后与一年上限和后端一致", () => {
  assert.equal(validateSeasonalRange(salesRange), null);
  assert.equal(validateSeasonalRange({ startDate: "2025-09-23", endDate: "2026-09-23" }), null);
  assert.equal(validateSeasonalRange({ startDate: "2025-09-22", endDate: "2026-09-23" }), "tooLong");
  assert.equal(validateSeasonalRange({ startDate: "2026-09-02", endDate: "2026-09-01" }), "order");
  assert.equal(validateSeasonalRange({ startDate: "2026-02-30", endDate: "2026-03-01" }), "format");
  assert.equal(validateSeasonalRange({ startDate: "2026/08/01", endDate: "2026-09-01" }), "format");
});

test("两个区间只要起止有一端不同就视为不一致", () => {
  assert.equal(rangesDiffer({ inbound: salesRange, sales: { ...salesRange } }), false);
  assert.equal(rangesDiffer({ inbound: { ...salesRange, startDate: "2026-08-15" }, sales: salesRange }), true);
  assert.equal(rangesDiffer({ inbound: salesRange, sales: { ...salesRange, endDate: "2026-09-22" } }), true);
});

test("日视图：按真实日期定位，无记录的日期不补 0，区间外的记录丢弃", () => {
  const trend = buildDailyTrend(
    [
      { date: "2026-09-23T00:00:00", quantity: 4, amount: 8 },
      { date: "2026-08-01T00:00:00", quantity: 7, amount: 14 },
      { date: "2026-08-05", quantity: 12, amount: 24 },
      { date: "2026-07-31", quantity: 99, amount: 1 },
    ],
    salesRange,
  );
  assert.equal(trend.dayCount, 54);
  assert.deepEqual(
    trend.points.map((point) => [point.index, point.date, point.quantity]),
    [
      [0, "2026-08-01", 7],
      [4, "2026-08-05", 12],
      [53, "2026-09-23", 4],
    ],
  );
  const summary = summarizeDaily(trend.points, trend.dayCount);
  assert.equal(summary.recordedDays, 3);
  assert.equal(summary.averagePerRecordedDay, 23 / 3);
  assert.equal(summary.peak?.date, "2026-08-05");
});

test("周视图：周一为起点，首尾不足 7 天标为不完整周，并汇总当周进货", () => {
  const weeks = buildWeeklyTrend(
    [
      { date: "2026-08-01", quantity: 5, amount: 0 },
      { date: "2026-08-03", quantity: 10, amount: 0 },
      { date: "2026-08-09", quantity: 2, amount: 0 },
      { date: "2026-09-23", quantity: 6, amount: 0 },
    ],
    [
      { id: "a", date: "2026-08-04T00:00:00", documentNo: "A", quantity: 100 },
      { id: "b", date: "2026-08-05", documentNo: "B", quantity: 20 },
    ],
    salesRange,
  );
  // 08/01 是周六：第一周只有 08/01–08/02；09/23 是周三：最后一周只有 09/21–09/23。
  assert.equal(weeks.length, 9);
  assert.deepEqual(
    [weeks[0].startDate, weeks[0].endDate, weeks[0].quantity, weeks[0].partial],
    ["2026-08-01", "2026-08-02", 5, true],
  );
  assert.deepEqual(
    [weeks[1].startDate, weeks[1].endDate, weeks[1].quantity, weeks[1].inboundQuantity, weeks[1].partial],
    ["2026-08-03", "2026-08-09", 12, 120, false],
  );
  assert.deepEqual([weeks[8].startDate, weeks[8].endDate, weeks[8].quantity, weeks[8].partial], ["2026-09-21", "2026-09-23", 6, true]);
  // 区间内没有销售的完整周也要给出（0 销量），不能跳过。
  assert.equal(weeks[2].quantity, 0);
});

test("周汇总：只用完整周算周均与环比，上周为 0 时环比为空", () => {
  const week = (startDate: string, quantity: number, partial = false) => ({
    startDate,
    endDate: startDate,
    quantity,
    inboundQuantity: 0,
    partial,
  });
  const summary = summarizeWeekly([week("2026-08-01", 500, true), week("2026-08-03", 100), week("2026-08-10", 150), week("2026-08-17", 30, true)]);
  assert.equal(summary.averagePerFullWeek, 125);
  assert.equal(summary.best?.startDate, "2026-08-10");
  assert.equal(summary.weekOverWeek, 0.5);
  assert.equal(summarizeWeekly([week("2026-08-03", 0), week("2026-08-10", 10)]).weekOverWeek, null);
  assert.equal(summarizeWeekly([week("2026-08-01", 5, true)]).averagePerFullWeek, null);
});

test("到货标注：只取销售区间内的进货，同日合并", () => {
  const markers = inboundMarkers(
    [
      { id: "a", date: "2026-08-10T00:00:00", documentNo: "A", quantity: 10 },
      { id: "b", date: "2026-08-10", documentNo: "B", quantity: 5 },
      { id: "c", date: "2026-07-20", documentNo: "C", quantity: 99 },
    ],
    salesRange,
  );
  assert.deepEqual(markers, [{ date: "2026-08-10", quantity: 15, index: 9 }]);
});

test("数量格式：千分位、最多两位小数、负数用减号", () => {
  assert.equal(formatQuantity(1397), "1,397");
  assert.equal(formatQuantity(540.004), "540");
  assert.equal(formatQuantity(-181), "−181");
  assert.equal(formatQuantity(6.5), "6.5");
});

test("权限：必须登录、非审核模式且有独立权限码", () => {
  const has = (code: string) => code === "SeasonalProductInsights.View";
  assert.equal(canViewSeasonalProductInsights(true, has, false), true);
  assert.equal(canViewSeasonalProductInsights(false, has, false), false);
  assert.equal(canViewSeasonalProductInsights(true, has, true), false);
  assert.equal(canViewSeasonalProductInsights(true, (code) => code === "StoreProducts.View", false), false);
});

// ───────── 售罄率、候选标签与排序 ─────────

test("售罄率：进货为 0 或数据缺失时为 null，不是 0 也不是无穷大", () => {
  assert.equal(sellThroughRate(480, 312), 0.65);
  assert.equal(sellThroughRate(0, 6), null);
  assert.equal(sellThroughRate(null, 6), null);
  assert.equal(sellThroughRate(100, null), null);
  assert.equal(sellThroughRate(100, 120), 1.2);
  assert.equal(formatPercent(0.655), "66%");
});

const stats = (inbound: number | null, sales: number | null, first: string | null = null) => ({
  inboundQuantity: inbound,
  salesQuantity: sales,
  theoreticalStock: (inbound ?? 0) - (sales ?? 0),
  firstInboundDate: first,
});

test("候选标签：负库存优先，其次快售罄；动销慢必须到货满 28 天", () => {
  const today = "2026-11-23";
  assert.equal(candidateFlag(stats(0, 6), today), "negative");
  assert.equal(candidateFlag(stats(100, 120), today), "negative");
  assert.equal(candidateFlag(stats(360, 341), today), "hot");
  // 刚到货 10 天、售罄率很低：新品不标动销慢。
  assert.equal(candidateFlag(stats(200, 10, "2026-11-13"), today), null);
  // 恰好 28 天且售罄率 < 25%：标动销慢。
  assert.equal(candidateFlag(stats(200, 38, "2026-10-26"), today), "slow");
  // 售罄率刚好 25%：不算慢。
  assert.equal(candidateFlag(stats(200, 50, "2026-10-26"), today), null);
  // 旧后端没有进货 / 销量字段：只可能是负库存，其余都不打标。
  assert.equal(candidateFlag(stats(null, null), today), null);
});

test("候选排序：售罄率高在前、无进货排最后、同值按货号；存货与货号排序", () => {
  const items = [
    { itemNumber: "XM2411", ...stats(200, 38) },
    { itemNumber: "XM2418", ...stats(0, 6) },
    { itemNumber: "XM2406", ...stats(360, 341) },
    { itemNumber: "XM2405", ...stats(480, 312) },
  ];
  assert.deepEqual(sortCandidates(items, "sellThrough").map((item) => item.itemNumber), ["XM2406", "XM2405", "XM2411", "XM2418"]);
  assert.deepEqual(sortCandidates(items, "stock").map((item) => item.itemNumber), ["XM2405", "XM2411", "XM2406", "XM2418"]);
  assert.deepEqual(sortCandidates(items, "itemNumber").map((item) => item.itemNumber), ["XM2405", "XM2406", "XM2411", "XM2418"]);
  // 不修改原数组。
  assert.equal(items[0].itemNumber, "XM2411");
});

// ───────── 销售速度、按周进销 ─────────

const paceRange = { startDate: "2026-08-01", endDate: "2026-11-23" };
// 2026-11-23 是周一：区间末周只有 1 天（不完整周）。
const paceDaily = [
  { date: "2026-10-26", quantity: 28, amount: 0 }, // 10/26–11/01
  { date: "2026-11-02", quantity: 28, amount: 0 }, // 11/02–11/08
  { date: "2026-11-09", quantity: 28, amount: 0 }, // 11/09–11/15
  { date: "2026-11-16", quantity: 28, amount: 0 }, // 11/16–11/22
  { date: "2026-11-23", quantity: 99, amount: 0 }, // 今天未结束：不能算进速度
];

test("销售速度：只看最近 4 个完整周，今天未结束的不计入", () => {
  const weeks = buildWeeklyTrend(paceDaily, [], paceRange);
  const pace = summarizePace(weeks, 168);
  assert.ok(pace);
  assert.equal(pace.recentPerDay, 4); // 112 件 / 28 天
  assert.equal(pace.weeks, 4);
  assert.equal(pace.daysLeft, 42);
  assert.equal(summarizePace(weeks, 0)?.daysLeft, null);
  assert.equal(summarizePace(weeks, -3)?.daysLeft, null);
});

test("销售速度：最近几周一件没卖或没有完整周时不显示", () => {
  assert.equal(summarizePace(buildWeeklyTrend([], [], paceRange), 50), null);
  const oneDay = buildWeeklyTrend([], [], { startDate: "2026-11-23", endDate: "2026-11-23" });
  assert.equal(summarizePace(oneDay, 50), null);
});

test("整季日均与距上次进货天数", () => {
  assert.equal(seasonPerDay(312, { startDate: "2026-08-01", endDate: "2026-11-23" }), 312 / 115);
  const records = [
    { id: "a", date: "2026-10-12T00:00:00", documentNo: "INV-1", quantity: 240 },
    { id: "b", date: "2026-11-11", documentNo: "INV-2", quantity: 120 },
  ];
  assert.equal(daysSinceLastInbound(records, "2026-11-23"), 12);
  assert.equal(daysSinceLastInbound(records, "2026-11-11"), 0);
  assert.equal(daysSinceLastInbound([], "2026-11-23"), null);
});

test("按周进销：每周进货、销量与周末存货累计；区间不一致时不给存货", () => {
  const range = { startDate: "2026-11-02", endDate: "2026-11-15" };
  const daily = [
    { date: "2026-11-03", quantity: 5, amount: 0 },
    { date: "2026-11-10", quantity: 7, amount: 0 },
    { date: "2026-11-12", quantity: 3, amount: 0 },
  ];
  const inbound = [
    { id: "i1", date: "2026-11-02", documentNo: "INV-1", quantity: 20 },
    { id: "i2", date: "2026-11-11", documentNo: "INV-2", quantity: 10 },
    { id: "i3", date: "2026-11-12", documentNo: "INV-2", quantity: 4 },
  ];
  const rows = buildWeeklyLedger(daily, inbound, range, true);
  assert.deepEqual(
    rows.map((row) => [row.startDate, row.inboundQuantity, row.salesQuantity, row.endStock]),
    [
      ["2026-11-02", 20, 5, 15],
      ["2026-11-09", 14, 10, 19],
    ],
  );
  assert.deepEqual(rows[1].documentNos, ["INV-2"]); // 同一张单两行只算一张
  assert.deepEqual(buildWeeklyLedger(daily, inbound, range, false).map((row) => row.endStock), [null, null]);
});

test("展开一周：进货单与日销量按日期倒序混排，同一天进货在前", () => {
  const week = { startDate: "2026-11-09", endDate: "2026-11-15" };
  const daily = [
    { date: "2026-11-10", quantity: 7, amount: 0 },
    { date: "2026-11-11", quantity: 8, amount: 0 },
    { date: "2026-11-20", quantity: 99, amount: 0 }, // 不在本周
  ];
  const inbound = [{ id: "i", date: "2026-11-11", documentNo: "INV-2", quantity: 120 }];
  assert.deepEqual(buildWeekEntries(week, daily, inbound), [
    { kind: "inbound", date: "2026-11-11", documentNo: "INV-2", quantity: 120 },
    { kind: "sales", date: "2026-11-11", quantity: 8 },
    { kind: "sales", date: "2026-11-10", quantity: 7 },
  ]);
});

test("周图纵轴上限：由销量决定，只有进货时才按进货", () => {
  const week = (quantity: number, inboundQuantity: number) => ({ startDate: "", endDate: "", quantity, inboundQuantity, partial: false });
  assert.equal(weeklyBarCeiling([week(58, 240), week(3, 0)]), 58);
  assert.equal(weeklyBarCeiling([week(0, 240), week(0, 120)]), 240);
  assert.equal(weeklyBarCeiling([]), 0);
});

test("区间并集：取更早的起点和更晚的终点", () => {
  assert.deepEqual(
    unionRange({
      inbound: { startDate: "2026-08-15", endDate: "2026-09-23" },
      sales: { startDate: "2026-08-01", endDate: "2026-09-20" },
    }),
    { startDate: "2026-08-01", endDate: "2026-09-23" },
  );
  const same = { startDate: "2026-08-01", endDate: "2026-11-23" };
  assert.deepEqual(unionRange({ inbound: same, sales: same }), same);
});
