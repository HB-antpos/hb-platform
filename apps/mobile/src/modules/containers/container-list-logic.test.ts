import assert from "node:assert/strict";
import {
  buildArrivalDatePatch,
  countSheetFilters,
  describeDateRange,
  diffDays,
  findInvalidDateRange,
  formatShortDate,
  getArrivalInsight,
  toDateOnly,
} from "./container-list-logic";

const TODAY = "2026-10-01";

// 接口日期只取日期部分，不经 Date 解析。
assert.equal(toDateOnly("2026-10-08T00:00:00"), "2026-10-08");
assert.equal(toDateOnly(undefined), "");
assert.equal(formatShortDate("2026-10-08T00:00:00"), "10-08");
assert.equal(diffDays("2026-09-28", "2026-10-01"), 3);
assert.equal(diffDays("2026-10-01", "2026-09-28"), -3);

// 未登记实际到库：逾期 / 今天 / 几天后。
assert.deepEqual(getArrivalInsight({ 预计到岸日期: "2026-09-28T00:00:00", 状态: 1 }, TODAY), {
  text: "已逾期 3 天",
  tone: "warning",
  target: "estimated",
});
assert.deepEqual(getArrivalInsight({ 预计到岸日期: "2026-10-01", 状态: 0 }, TODAY), {
  text: "今天到库",
  tone: "accent",
  target: "estimated",
});
assert.deepEqual(getArrivalInsight({ 预计到岸日期: "2026-10-08", 状态: 1 }, TODAY), {
  text: "7 天后",
  tone: "muted",
  target: "estimated",
});
// 已完成、已取消、没有预计日期都不提示逾期。
assert.equal(getArrivalInsight({ 预计到岸日期: "2026-09-01", 状态: 2 }, TODAY), null);
assert.equal(getArrivalInsight({ 预计到岸日期: "2026-09-01", 状态: 7 }, TODAY), null);
assert.equal(getArrivalInsight({ 状态: 1 }, TODAY), null);

// 已登记实际到库：与预计比较晚到/早到，准时不提示。
assert.deepEqual(
  getArrivalInsight({ 预计到岸日期: "2026-09-22", 实际到货日期: "2026-09-24T00:00:00", 状态: 2 }, TODAY),
  { text: "晚 2 天", tone: "warning", target: "actual" },
);
assert.deepEqual(
  getArrivalInsight({ 预计到岸日期: "2026-09-22", 实际到货日期: "2026-09-21", 状态: 1 }, TODAY),
  { text: "早 1 天", tone: "muted", target: "actual" },
);
assert.equal(getArrivalInsight({ 预计到岸日期: "2026-09-22", 实际到货日期: "2026-09-22" }, TODAY), null);
assert.equal(getArrivalInsight({ 实际到货日期: "2026-09-22" }, TODAY), null);

// 日期补丁只带变化字段；清空发送显式标记，不发送空字符串。
assert.equal(
  buildArrivalDatePatch(
    { estimated: "2026-10-08T00:00:00", actual: "" },
    { estimated: "2026-10-08", actual: "" },
  ),
  null,
);
assert.deepEqual(
  buildArrivalDatePatch({ estimated: "2026-10-08T00:00:00", actual: "" }, { estimated: "2026-10-08", actual: "2026-10-01" }),
  { 实际到货日期: "2026-10-01" },
);
assert.deepEqual(
  buildArrivalDatePatch({ estimated: "2026-10-08", actual: "2026-10-09" }, { estimated: "", actual: "" }),
  { ClearEstimatedArrivalDate: true, ClearActualArrivalDate: true },
);
assert.deepEqual(
  buildArrivalDatePatch({ estimated: "", actual: "" }, { estimated: "2026-10-12", actual: "" }),
  { 预计到岸日期: "2026-10-12" },
);
// 补丁里绝不会同时出现同一日期的赋值与清空（后端会 400）。
const patch = buildArrivalDatePatch({ estimated: "2026-10-08", actual: "2026-10-09" }, { estimated: "2026-10-10", actual: "" });
assert.deepEqual(patch, { 预计到岸日期: "2026-10-10", ClearActualArrivalDate: true });

// 区间校验：单边允许，开始晚于结束报对应区间名。
assert.equal(findInvalidDateRange({ loadingDateStart: "2026-09-01" }), null);
assert.equal(findInvalidDateRange({ loadingDateStart: "2026-09-10", loadingDateEnd: "2026-09-01" }), "装柜日期");
assert.equal(
  findInvalidDateRange({ actualArrivalDateStart: "2026-10-02", actualArrivalDateEnd: "2026-10-01" }),
  "实际到库",
);

// 角标计数：货号、每个日期区间、非默认排序各算一项。
assert.equal(countSheetFilters({}), 0);
assert.equal(countSheetFilters({ dateType: "预计到岸日期", containerNumberFilter: "HB", statuses: [1] }), 0);
assert.equal(
  countSheetFilters({
    itemNumberFilter: " A1 ",
    loadingDateStart: "2026-09-01",
    loadingDateEnd: "2026-09-30",
    actualArrivalDateEnd: "2026-10-01",
    dateType: "装柜日期",
  }),
  4,
);

assert.equal(describeDateRange("2026-09-01", "2026-09-30"), "2026-09-01 至 2026-09-30");
assert.equal(describeDateRange("2026-09-01", undefined), "2026-09-01 起");
assert.equal(describeDateRange(undefined, "2026-09-30"), "截至 2026-09-30");
assert.equal(describeDateRange(), "");

console.log("container-list-logic.test.ts: ok");
