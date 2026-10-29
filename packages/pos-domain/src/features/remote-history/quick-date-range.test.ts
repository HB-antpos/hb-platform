import assert from "node:assert/strict";
import test from "node:test";

import { quickHistoryDateRange } from "./quick-date-range";

test("设备本地时间：今天 / 昨天 / 近 7 天（含今天），跨月跨年", () => {
  const now = new Date(2026, 0, 3, 15, 30); // 2026-01-03 本地
  assert.deepEqual(quickHistoryDateRange("today", now), { from: "2026-01-03", to: "2026-01-03" });
  assert.deepEqual(quickHistoryDateRange("yesterday", now), { from: "2026-01-02", to: "2026-01-02" });
  assert.deepEqual(quickHistoryDateRange("last7days", now), { from: "2025-12-28", to: "2026-01-03" });
  const leap = new Date(2028, 2, 1, 0, 5); // 闰年 3 月 1 日
  assert.deepEqual(quickHistoryDateRange("yesterday", leap), { from: "2028-02-29", to: "2028-02-29" });
});

test("按分店营业时区取日历日：同一时刻在悉尼与布里斯班可能相差一天（夏令时期间）", () => {
  // 2026-10-27T14:30:00Z：悉尼（夏令时 UTC+11）= 10-28 01:30，布里斯班（UTC+10）= 10-28 00:30，洛杉矶（UTC-7）= 10-27。
  const instant = new Date("2026-10-27T14:30:00.000Z");
  assert.equal(quickHistoryDateRange("today", instant, "Australia/Sydney").from, "2026-10-28");
  assert.equal(quickHistoryDateRange("today", instant, "Australia/Brisbane").from, "2026-10-28");
  assert.equal(quickHistoryDateRange("today", instant, "America/Los_Angeles").from, "2026-10-27");
  // 悉尼夏令时开始（2026-10-04 02:00）前后：近 7 天按日历日计算，不受 23 小时日影响。
  const dst = new Date("2026-10-06T02:00:00.000Z");
  assert.deepEqual(quickHistoryDateRange("last7days", dst, "Australia/Sydney"), {
    from: "2026-09-30",
    to: "2026-10-06",
  });
});
