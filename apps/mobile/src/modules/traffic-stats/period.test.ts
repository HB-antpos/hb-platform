import assert from "node:assert/strict";
import { dayOverview, monthOverview, weekOverview } from "./fixtures";
import {
  addDays,
  anchorForPeriodSwitch,
  formatMonthDay,
  formatYearMonth,
  isValidDate,
  nextAnchor,
  parseAsOf,
  previousAnchor,
  resolvePeriodHeadline,
  weekdayIndex,
  weekdayLabel,
} from "./period";

// 日期运算：跨月、跨年、闰年
assert.equal(addDays("2026-10-31", 1), "2026-11-01");
assert.equal(addDays("2026-01-01", -1), "2025-12-31");
assert.equal(addDays("2028-02-28", 1), "2028-02-29");
assert.equal(addDays("2027-02-28", 1), "2027-03-01");
assert.equal(isValidDate("2026-02-31"), false);
assert.equal(isValidDate("2026-10-24"), true);
assert.equal(isValidDate("26-10-24"), false);

// 星期：周一为 0
assert.equal(weekdayIndex("2026-10-19"), 0);
assert.equal(weekdayIndex("2026-10-24"), 5);
assert.equal(weekdayIndex("2026-10-25"), 6);
assert.equal(weekdayLabel("2026-10-24", "zh"), "周六");
assert.equal(weekdayLabel("2026-10-24", "en"), "Sat");

// 文案
assert.equal(formatMonthDay("2026-10-24", "zh"), "10月24日");
assert.equal(formatMonthDay("2026-10-24", "en"), "24 Oct");
assert.equal(formatYearMonth("2026-10-24", "zh"), "2026年10月");
assert.equal(formatYearMonth("2026-10-24", "en"), "Oct 2026");

// asOfLocal
assert.deepEqual(parseAsOf("2026-10-24T14:32"), { date: "2026-10-24", hour: 14, minute: 32 });
assert.equal(parseAsOf("garbage"), null);
assert.equal(parseAsOf("2026-10-24T25:00"), null);

// 标题：当前周期与相邻周期用语义词，更早的用日期
assert.deepEqual(resolvePeriodHeadline(dayOverview(), "zh"), { kind: "today" });
assert.deepEqual(
  resolvePeriodHeadline(dayOverview({ from: "2026-10-23", to: "2026-10-23", isCurrent: false }), "zh"),
  { kind: "yesterday" },
);
assert.deepEqual(
  resolvePeriodHeadline(dayOverview({ from: "2026-10-10", to: "2026-10-10", isCurrent: false }), "zh"),
  { kind: "text", text: "10月10日" },
);
assert.deepEqual(resolvePeriodHeadline(weekOverview(), "zh"), { kind: "thisWeek" });
assert.deepEqual(
  resolvePeriodHeadline(weekOverview({ from: "2026-10-12", to: "2026-10-18", isCurrent: false }), "zh"),
  { kind: "lastWeek" },
);
assert.deepEqual(
  resolvePeriodHeadline(weekOverview({ from: "2026-10-05", to: "2026-10-11", isCurrent: false }), "en"),
  { kind: "text", text: "5 Oct – 11 Oct" },
);
assert.deepEqual(resolvePeriodHeadline(monthOverview(), "zh"), { kind: "thisMonth" });
assert.deepEqual(
  resolvePeriodHeadline(monthOverview({ from: "2026-09-01", to: "2026-09-30", isCurrent: false }), "zh"),
  { kind: "lastMonth" },
);
assert.deepEqual(
  resolvePeriodHeadline(monthOverview({ from: "2026-07-01", to: "2026-07-31", isCurrent: false }), "zh"),
  { kind: "text", text: "2026年7月" },
);
// 跨年：今天是 1 月时「上月」是去年 12 月
assert.deepEqual(
  resolvePeriodHeadline(
    monthOverview({ from: "2025-12-01", to: "2025-12-31", today: "2026-01-05", isCurrent: false }),
    "zh",
  ),
  { kind: "lastMonth" },
);

// 步进：往前一个周期用起始日 -1；当前周期不能往后
assert.equal(previousAnchor({ from: "2026-10-19" }), "2026-10-18");
assert.equal(previousAnchor({ from: "2026-10-01" }), "2026-09-30");
assert.equal(nextAnchor({ to: "2026-10-25", isCurrent: true }), null);
assert.equal(nextAnchor({ to: "2026-10-18", isCurrent: false }), "2026-10-19");

// 切换周期类型：当前周期回到「今天」，历史周期保留起始日
assert.equal(anchorForPeriodSwitch(weekOverview()), null);
assert.equal(anchorForPeriodSwitch(weekOverview({ from: "2026-10-12", isCurrent: false })), "2026-10-12");
assert.equal(anchorForPeriodSwitch(null), null);

console.log("traffic-stats period tests passed");
