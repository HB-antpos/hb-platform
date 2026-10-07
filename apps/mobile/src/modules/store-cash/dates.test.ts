import assert from "node:assert/strict";
import {
  addDays,
  buildRecentRange,
  compareDates,
  diffDays,
  formatUtcInZone,
  isDateWithinRange,
  isValidDateString,
  rangeLength,
  resolveEntryDateRange,
  validateDailyRange,
  weekdayOf,
} from "./dates";

// ───────── 日期有效性 ─────────
assert.equal(isValidDateString("2026-10-07"), true);
assert.equal(isValidDateString("2028-02-29"), true, "闰年 2 月 29 日有效");
assert.equal(isValidDateString("2026-02-29"), false, "平年没有 2 月 29 日");
assert.equal(isValidDateString("2026-02-30"), false, "不能被 Date 自动进位");
assert.equal(isValidDateString("2026-13-01"), false);
assert.equal(isValidDateString("2026-2-3"), false, "必须是定长 yyyy-MM-dd");
assert.equal(isValidDateString("2026-10-07T00:00:00Z"), false);
assert.equal(isValidDateString(""), false);
assert.equal(isValidDateString(null), false);
assert.equal(isValidDateString(undefined), false);

// ───────── 日期加减 ─────────
assert.equal(addDays("2026-10-07", -7), "2026-09-30");
assert.equal(addDays("2026-10-07", 0), "2026-10-07");
assert.equal(addDays("2026-03-01", -1), "2026-02-28");
assert.equal(addDays("2028-03-01", -1), "2028-02-29");
assert.equal(addDays("2026-12-31", 1), "2027-01-01");
assert.equal(addDays("not-a-date", 3), "not-a-date", "无效输入原样返回");
assert.equal(diffDays("2026-10-07", "2026-09-30"), 7);
assert.equal(diffDays("2026-09-30", "2026-10-07"), -7);
assert.equal(diffDays("2026-10-07", "bad"), null);
assert.equal(compareDates("2026-10-07", "2026-10-08"), -1);
assert.equal(compareDates("2026-10-08", "2026-10-07"), 1);
assert.equal(compareDates("2026-10-07", "2026-10-07"), 0);
assert.equal(weekdayOf("2026-10-07"), 3, "2026-10-07 是周三");
assert.equal(weekdayOf("1970-01-01"), 4, "1970-01-01 是周四");
assert.equal(weekdayOf("2026-10-04"), 0, "2026-10-04 是周日");

// ───────── 补录日期范围：店长只能选 [今天−N, 今天]，全部分店权限不限回溯 ─────────
const managerRange = resolveEntryDateRange({ storeToday: "2026-10-07", maxBackfillDays: 7, canViewAllStores: false });
assert.deepEqual(managerRange, { min: "2026-09-30", max: "2026-10-07" });
assert.equal(isDateWithinRange("2026-09-30", managerRange), true, "回溯边界当天允许");
assert.equal(isDateWithinRange("2026-09-29", managerRange), false, "超出回溯天数不允许");
assert.equal(isDateWithinRange("2026-10-07", managerRange), true, "门店今天允许");
assert.equal(isDateWithinRange("2026-10-08", managerRange), false, "不允许选未来");
assert.equal(isDateWithinRange("2026-02-30", managerRange), false, "无效日期不允许");

const allStoresRange = resolveEntryDateRange({ storeToday: "2026-10-07", maxBackfillDays: 7, canViewAllStores: true });
assert.deepEqual(allStoresRange, { min: null, max: "2026-10-07" });
assert.equal(isDateWithinRange("2020-01-01", allStoresRange), true, "有全部分店权限时不限回溯");
assert.equal(isDateWithinRange("2026-10-08", allStoresRange), false, "但仍不能选未来");

// 门店今天与手机日期无关：同一个 storeToday 永远得到同一个范围
assert.deepEqual(
  resolveEntryDateRange({ storeToday: "2026-01-01", maxBackfillDays: 3, canViewAllStores: false }),
  { min: "2025-12-29", max: "2026-01-01" },
  "跨年回溯",
);

// ───────── 按日明细范围 ─────────
assert.deepEqual(buildRecentRange("2026-10-07", 14), { from: "2026-09-24", to: "2026-10-07" });
assert.deepEqual(buildRecentRange("2026-10-07", 1), { from: "2026-10-07", to: "2026-10-07" });
assert.deepEqual(buildRecentRange("2026-10-07", 500), { from: "2026-07-07", to: "2026-10-07" }, "最多 93 天");
assert.equal(rangeLength({ from: "2026-09-24", to: "2026-10-07" }), 14);

assert.equal(validateDailyRange({ from: "2026-09-24", to: "2026-10-07" }, "2026-10-07"), null);
assert.equal(validateDailyRange({ from: "2026-10-07", to: "2026-10-07" }, "2026-10-07"), null);
assert.equal(validateDailyRange({ from: "2026-10-08", to: "2026-10-07" }, "2026-10-07"), "reversed");
assert.equal(validateDailyRange({ from: "2026-09-24", to: "2026-10-08" }, "2026-10-07"), "future");
assert.equal(validateDailyRange({ from: "bad", to: "2026-10-07" }, "2026-10-07"), "invalid");
assert.equal(validateDailyRange({ from: "2026-07-07", to: "2026-10-07" }, "2026-10-07"), null, "93 天含两端恰好允许");
assert.equal(validateDailyRange({ from: "2026-07-06", to: "2026-10-07" }, "2026-10-07"), "tooLong", "94 天超限");

// ───────── 时间展示（按门店时区） ─────────
assert.equal(formatUtcInZone("2026-10-07T04:30:00Z", "Australia/Sydney"), "2026-10-07 15:30", "悉尼 10 月 4 日起夏令时 UTC+11");
assert.equal(formatUtcInZone("2026-10-07T04:30:00Z", "Australia/Perth"), "2026-10-07 12:30", "珀斯 UTC+8 无夏令时");
assert.equal(formatUtcInZone("2026-10-07T20:00:00Z", "Australia/Sydney"), "2026-10-08 07:00", "跨日");
assert.match(formatUtcInZone("2026-10-07T04:30:00Z", "Not/AZone"), /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/, "时区无效退回设备时区");
assert.equal(formatUtcInZone("", "Australia/Sydney"), "");
assert.equal(formatUtcInZone(null, "Australia/Sydney"), "");
assert.equal(formatUtcInZone("garbage", "Australia/Sydney"), "garbage", "无法解析时原样显示");

console.log("dates.test.ts: ok");
