import assert from "node:assert/strict";
import { DEFAULT_ATTENDANCE_LEAVE_RULES, type AttendanceSickLeaveCandidate } from "./leave-types";
import {
  addDays,
  annualEarliestDate,
  applyCalendarTap,
  buildMonthGrid,
  countLeaveDays,
  diffDays,
  formatHours,
  listSickLeaveCandidates,
  roundHours,
  shiftMonth,
  sickCandidateState,
  sickLeaveDateRange,
  sickLeaveEarliestDate,
  sumSickPaidHours,
  validateAnnualRange,
  weekdayIndex,
} from "./leave-rules";

const rules = DEFAULT_ATTENDANCE_LEAVE_RULES;
const today = "2026-10-20"; // 周二，对齐设计图

// ---- 日期基础
assert.equal(addDays("2026-10-31", 1), "2026-11-01");
assert.equal(addDays("2026-03-01", -1), "2026-02-28");
assert.equal(addDays("bad", 3), "bad", "无效日期原样返回");
assert.equal(diffDays("2026-10-20", "2026-11-17"), 28);
assert.equal(weekdayIndex("2026-10-19"), 0, "2026-10-19 是周一");
assert.equal(weekdayIndex("2026-10-25"), 6, "2026-10-25 是周日");
// 跨澳洲夏令时切换（10-04）也不能少算一天。
assert.equal(diffDays("2026-10-03", "2026-10-05"), 2);

// ---- 小时格式
assert.equal(roundHours(7.983333), 7.98);
assert.equal(formatHours(5.5), "5.5");
assert.equal(formatHours(72), "72");
assert.equal(formatHours(0.1 + 0.2), "0.3");

// ---- 带薪病假
const candidate = (workDate: string, hasPunch: boolean, paidHours = 4, startTime = "09:00"): AttendanceSickLeaveCandidate => ({
  scheduleGuid: `sample-${workDate}-${startTime}`,
  workDate,
  startTime,
  endTime: "15:00",
  paidHours,
  hasPunch,
});
assert.equal(sickLeaveEarliestDate(today, rules), "2026-10-16", "4 天期限：今天 10-20 → 最早 10-16");
assert.equal(sickCandidateState(candidate("2026-10-15", false), today, rules), "expired");
assert.equal(sickCandidateState(candidate("2026-10-16", true), today, rules), "worked");
assert.equal(sickCandidateState(candidate("2026-10-19", false), today, rules), "selectable");
assert.equal(sickCandidateState(candidate("2026-10-20", false), today, rules), "selectable", "今天也能选");
assert.equal(sickCandidateState(candidate("2026-10-21", false), today, rules), "future");

const candidates = [
  candidate("2026-10-19", false, 5.5),
  candidate("2026-10-15", false),
  candidate("2026-10-17", true, 7.5),
  candidate("2026-10-16", true),
  candidate("2026-10-22", false),
];
const listed = listSickLeaveCandidates(candidates, today, rules);
assert.deepEqual(listed.map((item) => item.workDate), ["2026-10-16", "2026-10-17", "2026-10-19"], "只列期限内，按日期升序");
assert.deepEqual(listed.map((item) => item.state), ["worked", "worked", "selectable"]);
assert.equal(sumSickPaidHours(listed, ["sample-2026-10-19-09:00"]), 5.5);
assert.equal(sumSickPaidHours(listed, ["sample-2026-10-19-09:00", "sample-2026-10-17-09:00"]), 5.5, "已上班的不计");
assert.deepEqual(
  sickLeaveDateRange(candidates, ["sample-2026-10-19-09:00", "sample-2026-10-16-09:00"]),
  { startDate: "2026-10-16", endDate: "2026-10-19" },
);
assert.equal(sickLeaveDateRange(candidates, []), null);

// ---- 年假：工作日 / 周末 / 公假
const noHolidays = new Set<string>();
assert.deepEqual(countLeaveDays("2026-11-17", "2026-11-28", noHolidays), {
  totalDays: 12,
  workdays: 9,
  weekendDays: 3,
  holidayDays: 0,
}, "设计图：11-17 ~ 11-28 工作日 9 天、周末 3 天");
// 公假落在工作日单独计；落在周末按周末算，不重复。
assert.deepEqual(countLeaveDays("2026-12-21", "2026-12-27", new Set(["2026-12-25", "2026-12-26"])), {
  totalDays: 7,
  workdays: 4,
  weekendDays: 2,
  holidayDays: 1,
});
assert.equal(countLeaveDays("2026-11-28", "2026-11-17", noHolidays).totalDays, 0, "倒置区间为 0");

// ---- 年假·休息：设计图样例全部通过，72 h
assert.equal(annualEarliestDate("PaidAnnual", today, rules), "2026-11-17");
assert.equal(annualEarliestDate("AnnualCashOut", today, rules), "2026-10-22");
const designCase = validateAnnualRange({
  kind: "PaidAnnual",
  startDate: "2026-11-17",
  endDate: "2026-11-28",
  today,
  rules,
  publicHolidays: [],
  publishedScheduleDates: ["2026-10-26", "2026-11-01"],
});
assert.equal(designCase.daysFromToday, 28);
assert.equal(designCase.totalDays, 12);
assert.equal(designCase.requestedHours, 72);
assert.deepEqual(designCase.checks.map((check) => [check.key, check.ok]), [
  ["lead", true],
  ["maxLength", true],
  ["noPublishedSchedule", true],
]);
assert.equal(designCase.ok, true);

// 提前量不够、超过 2 周、区间内已有发布排班 → 逐条 ✗，不能提交。
const failing = validateAnnualRange({
  kind: "PaidAnnual",
  startDate: "2026-11-10",
  endDate: "2026-11-30",
  today,
  rules,
  publicHolidays: [],
  publishedScheduleDates: ["2026-11-12", "2026-11-11", "2026-11-12"],
});
assert.deepEqual(failing.checks.map((check) => check.ok), [false, false, false]);
assert.deepEqual(failing.conflictDates, ["2026-11-11", "2026-11-12"], "冲突日期去重排序");
assert.equal(failing.ok, false);

// 只点了起点：按单日区间检查。
const singleDay = validateAnnualRange({
  kind: "PaidAnnual",
  startDate: "2026-11-18",
  today,
  rules,
  publicHolidays: [],
  publishedScheduleDates: [],
});
assert.equal(singleDay.totalDays, 1);
assert.equal(singleDay.requestedHours, 8);

// 全是周末 / 公假：检查通过但 0 h，不能提交。
const weekendOnly = validateAnnualRange({
  kind: "PaidAnnual",
  startDate: "2026-11-21",
  endDate: "2026-11-22",
  today,
  rules,
  publicHolidays: [],
  publishedScheduleDates: [],
});
assert.equal(weekendOnly.requestedHours, 0);
assert.equal(weekendOnly.ok, false);

// 未选日期：全部未通过。
const empty = validateAnnualRange({ kind: "PaidAnnual", today, rules, publicHolidays: [], publishedScheduleDates: [] });
assert.equal(empty.hasRange, false);
assert.equal(empty.ok, false);

// ---- 年假·发放：提前 2 天、最长 4 周，不查排班；公假不计工时。
const cashOut = validateAnnualRange({
  kind: "AnnualCashOut",
  startDate: "2026-10-22",
  endDate: "2026-11-18",
  today,
  rules,
  publicHolidays: ["2026-11-03"],
  publishedScheduleDates: ["2026-10-22", "2026-10-23"],
});
assert.deepEqual(cashOut.checks.map((check) => check.key), ["lead", "maxLength"]);
assert.equal(cashOut.totalDays, 28);
assert.equal(cashOut.breakdown.holidayDays, 1);
assert.equal(cashOut.requestedHours, 19 * 8);
assert.equal(cashOut.ok, true);
assert.equal(
  validateAnnualRange({ kind: "AnnualCashOut", startDate: "2026-10-21", endDate: "2026-10-21", today, rules, publicHolidays: [], publishedScheduleDates: [] }).ok,
  false,
  "发放提前 1 天不够",
);

// ---- 日历点选
assert.deepEqual(applyCalendarTap({}, "2026-11-17"), { startDate: "2026-11-17", endDate: undefined });
assert.deepEqual(applyCalendarTap({ startDate: "2026-11-17" }, "2026-11-28"), { startDate: "2026-11-17", endDate: "2026-11-28" });
assert.deepEqual(applyCalendarTap({ startDate: "2026-11-17" }, "2026-11-10"), { startDate: "2026-11-10", endDate: undefined });
assert.deepEqual(
  applyCalendarTap({ startDate: "2026-11-17", endDate: "2026-11-28" }, "2026-11-20"),
  { startDate: "2026-11-20", endDate: undefined },
  "已选完一段再点 → 重新开始",
);
assert.deepEqual(
  applyCalendarTap({ startDate: "2026-11-17", endDate: "2026-11-28" }, "2026-11-30", "end"),
  { startDate: "2026-11-17", endDate: "2026-11-30" },
  "指定改终点",
);
assert.deepEqual(
  applyCalendarTap({ startDate: "2026-11-17", endDate: "2026-11-28" }, "2026-11-30", "start"),
  { startDate: "2026-11-30", endDate: undefined },
  "改起点越过终点 → 终点清空",
);

// ---- 月历
const november = buildMonthGrid(2026, 11);
assert.equal(november[0].filter((cell) => cell === null).length, 6, "2026-11-01 是周日，前面补 6 格");
assert.equal(november.flat().filter(Boolean).length, 30);
assert.ok(november.every((row) => row.length === 7));
assert.deepEqual(shiftMonth(2026, 12, 1), { year: 2027, month: 1 });
assert.deepEqual(shiftMonth(2026, 1, -1), { year: 2025, month: 12 });

console.log("leave-rules.test.ts: ok");
