import assert from "node:assert/strict";
import {
  buildLeavePickerSummary,
  effectiveLeaveRange,
  mondayOf,
  rosteredDateSet,
  rosteredDatesInRange,
  shouldOpenTimeOffDirectly,
  splitShownDates,
  tapLeaveRange,
  twoWeekDates,
  unpaidLeaveTypeOf,
} from "./leave-picker";
import { DEFAULT_ATTENDANCE_LEAVE_RULES, type AttendanceSickLeaveCandidate } from "./leave-types";

// —— 临时工直达无薪表单：只在后端明确判定 false 时跳过「请哪种假」——
assert.equal(shouldOpenTimeOffDirectly({ paidLeaveEligible: false }), true, "临时工直接打开表单");
assert.equal(shouldOpenTimeOffDirectly({ paidLeaveEligible: true }), false, "全职 / 兼职先选哪种假");
assert.equal(shouldOpenTimeOffDirectly(undefined), false, "规则还没读到时不跳过");

// —— 两周日历窗口：按周一开头，14 天 ——
assert.equal(mondayOf("2026-11-12"), "2026-11-09", "周四所在周的周一");
assert.equal(mondayOf("2026-11-09"), "2026-11-09", "周一本身");
assert.equal(mondayOf("2026-11-15"), "2026-11-09", "周日归到前面的周一");
assert.equal(mondayOf("bad"), "bad", "无效日期原样返回");
const window = twoWeekDates("2026-11-09");
assert.equal(window.length, 14);
assert.deepEqual([window[0], window[13]], ["2026-11-09", "2026-11-22"]);
assert.deepEqual(twoWeekDates("bad"), []);

// —— 排班日期：已发布排班 + 病假候选（过去的排班）合并去重 ——
const candidates: AttendanceSickLeaveCandidate[] = [
  { scheduleGuid: "a", workDate: "2026-11-10", startTime: "09:00", endTime: "17:00", paidHours: 7.5, hasPunch: false },
  { scheduleGuid: "b", workDate: "2026-11-11", startTime: "09:00", endTime: "17:00", paidHours: 7.5, hasPunch: true },
  { scheduleGuid: "c", workDate: "2026-11-12", startTime: "09:00", endTime: "13:00", paidHours: 4, hasPunch: false },
  { scheduleGuid: "d", workDate: "2026-11-12", startTime: "14:00", endTime: "18:00", paidHours: 4, hasPunch: false },
  { scheduleGuid: "e", workDate: "2026-11-05", startTime: "09:00", endTime: "17:00", paidHours: 7.5, hasPunch: false },
];
const rostered = rosteredDateSet({
  publishedScheduleDates: ["2026-11-12", "2026-11-13T00:00:00", "2026-11-17"],
  sickLeaveCandidates: candidates,
});
assert.deepEqual([...rostered].sort(), ["2026-11-05", "2026-11-10", "2026-11-11", "2026-11-12", "2026-11-13", "2026-11-17"]);
assert.deepEqual(rosteredDateSet(undefined).size, 0, "没读到规则时没有排班点");
assert.deepEqual(rosteredDatesInRange(rostered, "2026-11-12", "2026-11-15"), ["2026-11-12", "2026-11-13"]);
assert.deepEqual(rosteredDatesInRange(rostered, "2026-11-14", "2026-11-16"), []);

// —— 区间点选：第一下单日，第二下终点，再点重新开始 ——
let selection = { startDate: "2026-11-12", endDate: "2026-11-12" } as { startDate: string; endDate?: string };
selection = tapLeaveRange(selection, "2026-11-15");
assert.deepEqual(selection, { startDate: "2026-11-15", endDate: undefined }, "已选好一段时再点 = 重新开始");
assert.deepEqual(effectiveLeaveRange(selection), { startDate: "2026-11-15", endDate: "2026-11-15" }, "只点了起点按单日");
selection = tapLeaveRange(selection, "2026-11-17");
assert.deepEqual(selection, { startDate: "2026-11-15", endDate: "2026-11-17" }, "第二下是终点");
selection = tapLeaveRange({ startDate: "2026-11-15" }, "2026-11-13");
assert.deepEqual(selection, { startDate: "2026-11-13", endDate: undefined }, "点在起点之前换起点");
assert.deepEqual(effectiveLeaveRange({ startDate: "2026-11-15", endDate: "2026-11-10" }), { startDate: "2026-11-15", endDate: "2026-11-15" }, "倒置按单日");

// —— 无薪原因 → 后端已有类型 ——
assert.equal(unpaidLeaveTypeOf("sick"), "SickLeave");
assert.equal(unpaidLeaveTypeOf("personal"), "AnnualLeave");

// —— 「请哪种假」的具体日期：设计图口径（今天 11-12、病假 4 天、年假提前 4 周、发放提前 2 天）——
const summary = buildLeavePickerSummary({ today: "2026-11-12", rules: DEFAULT_ATTENDANCE_LEAVE_RULES, sickLeaveCandidates: candidates });
assert.deepEqual(summary.sickDates, ["2026-11-10", "2026-11-12"], "只列期限内、有排班、没打卡的日子，同日两班只算一天");
assert.equal(summary.annualEarliest, "2026-12-10", "年假休息最早 4 周后");
assert.equal(summary.cashOutEarliest, "2026-11-14", "年假发放最早 2 天后");
assert.deepEqual(
  buildLeavePickerSummary({ today: "2026-11-12", rules: DEFAULT_ATTENDANCE_LEAVE_RULES, sickLeaveCandidates: [] }).sickDates,
  [],
  "没有可申请的病假日",
);

// —— 日期列表折叠 ——
assert.deepEqual(splitShownDates(["a", "b"]), { shown: ["a", "b"], more: 0 });
assert.deepEqual(splitShownDates(["a", "b", "c", "d", "e"]), { shown: ["a", "b", "c"], more: 2 });
assert.deepEqual(splitShownDates(["a", "b", "c"], 2), { shown: ["a", "b"], more: 1 });

console.log("leave-picker.test.ts: ok");
