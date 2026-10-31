import assert from "node:assert/strict";
import {
  getCompareRevenuePeriod,
  getDefaultRevenuePeriod,
  getLastMonthRevenuePeriod,
  getLastWeekRevenuePeriod,
  getLastYearIsoWeekPeriod,
  getLastYearSameMonthPeriod,
  getLastYearSameWeekdayPeriod,
  getNextRevenuePeriod,
  getPreviousRevenuePeriod,
  getYesterdayRevenuePeriod,
  getRevenueDateBounds,
  getRevenuePeriodForDate,
  isRevenuePeriodAvailable,
  refreshRevenueDateSelection,
} from "./periods";

const anchor = new Date(2026, 6, 4);

assert.deepEqual(getYesterdayRevenuePeriod(anchor), {
  mode: "day",
  startDate: "2026-07-03",
  endDate: "2026-07-03",
});
assert.deepEqual(getDefaultRevenuePeriod("day", anchor), {
  mode: "day",
  startDate: "2026-07-04",
  endDate: "2026-07-04",
});

assert.deepEqual(getLastWeekRevenuePeriod(anchor), {
  mode: "week",
  startDate: "2026-06-22",
  endDate: "2026-06-28",
});
assert.deepEqual(getDefaultRevenuePeriod("week", anchor), {
  mode: "week",
  startDate: "2026-06-29",
  endDate: "2026-07-04",
});

assert.deepEqual(getLastMonthRevenuePeriod(anchor), {
  mode: "month",
  startDate: "2026-06-01",
  endDate: "2026-06-30",
});
assert.deepEqual(getDefaultRevenuePeriod("month", anchor), {
  mode: "month",
  startDate: "2026-07-01",
  endDate: "2026-07-04",
});

const midWeekAnchor = new Date(2026, 6, 8);
assert.deepEqual(getDefaultRevenuePeriod("week", midWeekAnchor), {
  mode: "week",
  startDate: "2026-07-06",
  endDate: "2026-07-08",
});
assert.deepEqual(getDefaultRevenuePeriod("month", midWeekAnchor), {
  mode: "month",
  startDate: "2026-07-01",
  endDate: "2026-07-08",
});
assert.deepEqual(getRevenuePeriodForDate("week", "2026-07-06", midWeekAnchor), {
  mode: "week",
  startDate: "2026-07-06",
  endDate: "2026-07-08",
});
assert.deepEqual(getRevenuePeriodForDate("month", "2026-07-01", midWeekAnchor), {
  mode: "month",
  startDate: "2026-07-01",
  endDate: "2026-07-08",
});

// 历史周/月保持完整自然周期，不能因为所选日期位于周期中间而被截短。
assert.deepEqual(getRevenuePeriodForDate("week", "2026-06-17", midWeekAnchor), {
  mode: "week",
  startDate: "2026-06-15",
  endDate: "2026-06-21",
});
assert.deepEqual(getRevenuePeriodForDate("month", "2026-06-17", midWeekAnchor), {
  mode: "month",
  startDate: "2026-06-01",
  endDate: "2026-06-30",
});
assert.deepEqual(getPreviousRevenuePeriod(getDefaultRevenuePeriod("week", midWeekAnchor)), {
  mode: "week",
  startDate: "2026-06-29",
  endDate: "2026-07-05",
});
assert.deepEqual(
  getNextRevenuePeriod(
    { mode: "week", startDate: "2026-06-29", endDate: "2026-07-05" },
    midWeekAnchor,
  ),
  {
    mode: "week",
    startDate: "2026-07-06",
    endDate: "2026-07-08",
  },
);
assert.deepEqual(
  getNextRevenuePeriod(
    { mode: "month", startDate: "2026-06-01", endDate: "2026-06-30" },
    midWeekAnchor,
  ),
  {
    mode: "month",
    startDate: "2026-07-01",
    endDate: "2026-07-08",
  },
);
assert.deepEqual(
  getCompareRevenuePeriod(getDefaultRevenuePeriod("week", midWeekAnchor), "lastYearIsoWeek"),
  {
    mode: "week",
    startDate: "2025-07-07",
    endDate: "2025-07-09",
  },
);
assert.deepEqual(
  getCompareRevenuePeriod(getDefaultRevenuePeriod("month", midWeekAnchor), "lastYearSameMonth"),
  {
    mode: "month",
    startDate: "2025-07-01",
    endDate: "2025-07-08",
  },
);

const midMonthAnchor = new Date(2026, 6, 17);
assert.deepEqual(getDefaultRevenuePeriod("month", midMonthAnchor), {
  mode: "month",
  startDate: "2026-07-01",
  endDate: "2026-07-17",
});
assert.deepEqual(
  getCompareRevenuePeriod(getDefaultRevenuePeriod("month", midMonthAnchor), "lastYearSameMonth"),
  {
    mode: "month",
    startDate: "2025-07-01",
    endDate: "2025-07-17",
  },
);

assert.deepEqual(getLastYearSameWeekdayPeriod(getDefaultRevenuePeriod("day", anchor)), {
  mode: "day",
  startDate: "2025-07-05",
  endDate: "2025-07-05",
});

const week = { mode: "week" as const, startDate: "2026-06-22", endDate: "2026-06-28" };
assert.deepEqual(getPreviousRevenuePeriod(week), {
  mode: "week",
  startDate: "2026-06-15",
  endDate: "2026-06-21",
});
assert.deepEqual(getNextRevenuePeriod(week), {
  mode: "week",
  startDate: "2026-06-29",
  endDate: "2026-07-05",
});
assert.deepEqual(getLastYearSameWeekdayPeriod(week), {
  mode: "week",
  startDate: "2025-06-23",
  endDate: "2025-06-29",
});
assert.deepEqual(getLastYearIsoWeekPeriod(week), {
  mode: "week",
  startDate: "2025-06-23",
  endDate: "2025-06-29",
});

assert.deepEqual(getLastYearIsoWeekPeriod({ mode: "week", startDate: "2026-12-28", endDate: "2027-01-03" }), {
  mode: "week",
  startDate: "2025-12-22",
  endDate: "2025-12-28",
});
assert.deepEqual(getLastYearSameWeekdayPeriod({ mode: "day", startDate: "2026-12-31", endDate: "2026-12-31" }), {
  mode: "day",
  startDate: "2025-12-25",
  endDate: "2025-12-25",
});

const month = { mode: "month" as const, startDate: "2026-03-01", endDate: "2026-03-31" };
assert.deepEqual(getPreviousRevenuePeriod(month), {
  mode: "month",
  startDate: "2026-02-01",
  endDate: "2026-02-28",
});
assert.deepEqual(getLastYearSameMonthPeriod(month), {
  mode: "month",
  startDate: "2025-03-01",
  endDate: "2025-03-31",
});
assert.deepEqual(getCompareRevenuePeriod(month, "lastYearSameMonth"), getLastYearSameMonthPeriod(month));
assert.deepEqual(
  getLastYearSameMonthPeriod({ mode: "month", startDate: "2025-02-01", endDate: "2025-02-28" }),
  {
    mode: "month",
    startDate: "2024-02-01",
    endDate: "2024-02-29",
  },
);
assert.deepEqual(
  getLastYearSameMonthPeriod({ mode: "month", startDate: "2025-02-01", endDate: "2025-02-15" }),
  {
    mode: "month",
    startDate: "2024-02-01",
    endDate: "2024-02-15",
  },
);

assert.deepEqual(getRevenueDateBounds(new Date(2026, 6, 11)), {
  minDate: "2025-01-11",
  maxDate: "2026-07-11",
});
assert.deepEqual(getRevenueDateBounds(new Date(2024, 1, 29)), {
  minDate: "2022-08-29",
  maxDate: "2024-02-29",
});
// 18 个月前那个月没有同一天：取月末（平年 2 月 28 日、闰年 2 月 29 日）。
assert.deepEqual(getRevenueDateBounds(new Date(2026, 7, 31)), {
  minDate: "2025-02-28",
  maxDate: "2026-08-31",
});
assert.deepEqual(getRevenueDateBounds(new Date(2029, 7, 31)), {
  minDate: "2028-02-29",
  maxDate: "2029-08-31",
});
assert.deepEqual(getRevenuePeriodForDate("week", "2026-07-08"), {
  mode: "week",
  startDate: "2026-07-06",
  endDate: "2026-07-12",
});
assert.equal(
  isRevenuePeriodAvailable(
    { mode: "week", startDate: "2025-07-07", endDate: "2025-07-13" },
    { minDate: "2025-07-11", maxDate: "2026-07-11" },
  ),
  true,
);
assert.equal(
  isRevenuePeriodAvailable(
    { mode: "week", startDate: "2026-07-13", endDate: "2026-07-19" },
    { minDate: "2025-07-11", maxDate: "2026-07-11" },
  ),
  false,
);
assert.deepEqual(refreshRevenueDateSelection("2026-07-10", new Date(2026, 6, 12)), {
  bounds: { minDate: "2025-01-12", maxDate: "2026-07-12" },
  selectedDate: "2026-07-10",
});
// 一年前的日期现在仍在范围内，不再被收回。
assert.deepEqual(refreshRevenueDateSelection("2025-07-11", new Date(2026, 6, 12)), {
  bounds: { minDate: "2025-01-12", maxDate: "2026-07-12" },
  selectedDate: "2025-07-11",
});
assert.deepEqual(refreshRevenueDateSelection("2025-01-11", new Date(2026, 6, 12)), {
  bounds: { minDate: "2025-01-12", maxDate: "2026-07-12" },
  selectedDate: "2025-01-12",
});
