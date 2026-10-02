import assert from "node:assert/strict";
import {
  buildMyRequestItems,
  buildMyWeekRows,
  buildWeekDates,
  computeScheduleHourStats,
  defaultMealBreakCount,
  formatScheduleHours,
  defaultAvailabilityDates,
  leaveDayCount,
  scheduleDurationMinutes,
  schedulePaidMinutes,
  shiftDate,
  shiftWeekStart,
  sumScheduledMinutes,
} from "./attendance-my-week";
import type {
  AttendanceAvailability,
  AttendanceLeaveRequest,
  AttendancePunchAdjustment,
  AttendanceSchedule,
  AttendanceWeek,
} from "./types";

const schedule = (workDate: string, startTime: string, endTime: string, status = "Active") => ({
  scheduleGuid: `${workDate}-${startTime}`,
  storeCode: "BRI",
  userGuid: "u1",
  workDate,
  startTime,
  endTime,
  status,
  isMine: true,
}) as AttendanceSchedule;

const availability = (workDate: string, status = "Active") => ({
  availabilityGuid: `a-${workDate}`,
  workDate,
  startTime: "00:00:00",
  endTime: "23:59:00",
  status,
}) as AttendanceAvailability;

assert.deepEqual(buildWeekDates("2026-09-28"), [
  "2026-09-28", "2026-09-29", "2026-09-30", "2026-10-01", "2026-10-02", "2026-10-03", "2026-10-04",
]);
assert.equal(shiftWeekStart("2026-09-28", 1), "2026-10-05");
assert.equal(shiftWeekStart("2026-09-28", -1), "2026-09-21");
assert.equal(scheduleDurationMinutes({ startTime: "09:00:00", endTime: "17:30:00" }), 510);
assert.equal(scheduleDurationMinutes({ startTime: "22:00", endTime: "02:00" }), 240, "跨午夜按次日结束");
// 计薪工时：默认超过 4.5 小时 1 次、超过 9 小时 2 次用餐，每次 30 分钟；正好到门槛按低一档。
assert.equal(schedulePaidMinutes({ startTime: "09:00:00", endTime: "17:30:00" }), 480, "8.5 小时班默认扣 1 次用餐，记 8 小时");
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "13:30" }), 270, "正好 4.5 小时不扣用餐");
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "13:31" }), 241, "超过 4.5 小时即扣 1 次用餐");
assert.equal(schedulePaidMinutes({ startTime: "22:00", endTime: "04:00" }), 330, "跨午夜班次按总时长判断");
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "18:00" }), 510, "正好 9 小时仍只扣 1 次");
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "18:01" }), 481, "超过 9 小时扣 2 次");
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "19:00" }), 540, "10 小时班扣 2 次，记 9 小时");
// 店长指定的次数优先于默认：0 次＝取消用餐扣除，也可以多扣；结果不会为负。
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "17:30", mealBreakCount: 0 }), 510, "指定 0 次即不扣用餐");
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "17:30", mealBreakCount: 2 }), 450, "指定 2 次扣 60 分钟");
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "10:00", mealBreakCount: 3 }), 0, "扣除不超过班次时长");
assert.equal(schedulePaidMinutes({ startTime: "09:00", endTime: "17:30", mealBreakCount: null }), 480, "null 按默认");
assert.equal(defaultMealBreakCount(270), 0);
assert.equal(defaultMealBreakCount(271), 1);
assert.equal(defaultMealBreakCount(541), 2);

const week: AttendanceWeek = {
  weekStart: "2026-09-28",
  weekEnd: "2026-10-04",
  days: [
    { workDate: "2026-09-28", dayOfWeek: 1, schedules: [schedule("2026-09-28", "09:00:00", "17:00:00")] },
    { workDate: "2026-10-01", dayOfWeek: 4, schedules: [schedule("2026-10-01", "10:00:00", "18:00:00")] },
    { workDate: "2026-10-02", dayOfWeek: 5, schedules: [schedule("2026-10-02", "09:00:00", "15:00:00", "Cancelled")] },
  ],
};
const rows = buildMyWeekRows("2026-09-28", "2026-10-01", week, [
  availability("2026-09-29"),
  availability("2026-10-03"),
  availability("2026-10-04", "Cancelled"),
]);
assert.deepEqual(rows.map((row) => row.state), [
  "scheduled", "available", "rest", "scheduled", "unfilled", "available", "unfilled",
], "已取消的排班与可上班时间不计入");
// 不能上班：只有不能上班记录的日期标为 unavailable；同一天既有可上班又有不能上班时段，按可上班处理。
const unavailableRows = buildMyWeekRows("2026-09-28", "2026-10-01", undefined, [
  { ...availability("2026-10-02"), isUnavailable: true },
  { ...availability("2026-10-03"), isUnavailable: true },
  { ...availability("2026-10-03"), availabilityGuid: "a-2026-10-03-am", startTime: "09:00:00", endTime: "12:00:00" },
]);
assert.equal(unavailableRows[4].state, "unavailable");
assert.equal(unavailableRows[5].state, "available");
assert.equal(unavailableRows[5].availability.filter((item) => !item.isUnavailable)[0].startTime, "09:00:00");
assert.equal(rows[3].isToday, true);
assert.equal(rows[0].isPast, true);
assert.equal(sumScheduledMinutes(rows), 2 * (8 * 60 - 30), "两个 8 小时班各扣 1 次用餐");
assert.deepEqual(defaultAvailabilityDates(rows), ["2026-10-02", "2026-10-04"], "只默认勾选今天及以后、未填的日期");

const adjustment = (guid: string, status: string, submittedAt: string) => ({
  adjustmentGuid: guid,
  storeCode: "BRI",
  punchType: "ClockIn",
  requestedPunchTimeLocal: submittedAt,
  reason: "忘记打卡",
  status,
  submittedAt,
}) as AttendancePunchAdjustment;
const leave = (guid: string, status: string, startDate: string) => ({
  leaveGuid: guid,
  leaveType: "AnnualLeave",
  startDate,
  endDate: startDate,
  status,
}) as AttendanceLeaveRequest;
const items = buildMyRequestItems(
  [adjustment("a1", "Applied", "2026-09-26T10:00"), adjustment("a2", "Pending", "2026-09-30T18:00")],
  [leave("l1", "Pending", "2026-10-12"), leave("l2", "Rejected", "2026-09-01")],
);
assert.deepEqual(items.map((item) => item.key), [
  "leave:l1", "adjustment:a2", "adjustment:a1", "leave:l2",
], "待审核在前，同组按时间倒序");

assert.equal(leaveDayCount("2026-10-12", "2026-10-14"), 3);
assert.equal(leaveDayCount("2026-10-14", "2026-10-12"), 0);
assert.equal(shiftDate("2026-09-30", 1), "2026-10-01");

// 工时统计：周一至五计入工作日、周六日计入周末；已取消与请假班次不计入；超过 4.5 小时的班次扣 1 次用餐，指定次数优先。
const stats = computeScheduleHourStats([
  schedule("2026-09-28", "09:00:00", "17:00:00"),
  schedule("2026-10-02", "09:00:00", "13:30:00"),
  schedule("2026-10-03", "10:00:00", "16:00:00"),
  schedule("2026-10-04", "10:00:00", "14:00:00", "Cancelled"),
  { ...schedule("2026-10-01", "09:00:00", "17:00:00"), leaveType: "SickLeave" },
  { ...schedule("2026-09-29", "09:00:00", "17:00:00"), mealBreakCount: 0 },
  schedule("2026-10-04", "15:00:00", "19:00:00"),
]);
// 等效工时：工作日 ×1、周六 ×1.25、周日 ×1.5，按扣用餐后的计薪工时加权。
assert.deepEqual(stats, {
  totalMinutes: 450 + 270 + 330 + 480 + 240,
  weekdayMinutes: 450 + 270 + 480,
  weekendMinutes: 330 + 240,
  saturdayMinutes: 330,
  sundayMinutes: 240,
  equivalentMinutes: 450 + 270 + 480 + 330 * 1.25 + 240 * 1.5,
});
assert.equal(formatScheduleHours(stats.totalMinutes), "29.5");
assert.equal(formatScheduleHours(stats.equivalentMinutes), "32.9", "1972.5 分钟 ≈ 32.9 小时");
assert.equal(formatScheduleHours(6 * 60), "6");

console.log("attendance-my-week.test.ts: ok");
