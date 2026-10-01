import assert from "node:assert/strict";
import {
  buildMyRequestItems,
  buildMyWeekRows,
  buildWeekDates,
  defaultAvailabilityDates,
  leaveDayCount,
  scheduleDurationMinutes,
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
assert.equal(rows[3].isToday, true);
assert.equal(rows[0].isPast, true);
assert.equal(sumScheduledMinutes(rows), 16 * 60);
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

console.log("attendance-my-week.test.ts: ok");
