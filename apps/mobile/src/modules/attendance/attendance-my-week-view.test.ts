import assert from "node:assert/strict";
import { buildMyWeekRows } from "./attendance-my-week";
import {
  availabilityFillProgress,
  myShiftOutcome,
  requestProgressStep,
  summarizeMyWeek,
  waitedSince,
} from "./attendance-my-week-view";
import type { AttendanceAvailability, AttendanceSchedule, AttendanceWeek } from "./types";

const s = (workDate: string, startTime: string, endTime: string, extra: Partial<AttendanceSchedule> = {}) =>
  ({ scheduleGuid: `${workDate}${startTime}`, storeCode: "ROB", userGuid: "u", workDate, startTime, endTime, status: "Active", isMine: true, ...extra }) as AttendanceSchedule;

const week: AttendanceWeek = {
  weekStart: "2026-11-09",
  weekEnd: "2026-11-15",
  days: [
    { workDate: "2026-11-09", dayOfWeek: 1, schedules: [s("2026-11-09", "10:00", "18:00", { scheduleState: "Completed", paidMinutes: 450 })] },
    { workDate: "2026-11-10", dayOfWeek: 2, schedules: [s("2026-11-10", "10:00", "18:00", { scheduleState: "MissingClockOut" })] },
    { workDate: "2026-11-11", dayOfWeek: 3, schedules: [s("2026-11-11", "10:00", "18:00", { scheduleState: "Working", workedMinutes: 243 })] },
    { workDate: "2026-11-13", dayOfWeek: 5, schedules: [s("2026-11-13", "09:00", "17:00", { leaveType: "PaidSick" })] },
    { workDate: "2026-11-14", dayOfWeek: 6, schedules: [s("2026-11-14", "10:00", "16:00", { scheduleState: "NotStarted" })] },
  ],
};
const rows = buildMyWeekRows("2026-11-09", "2026-11-11", week, []);

// 本周汇总：请假不算班；已完成按后端计薪分钟；每天小时数按排班计薪（扣餐）。
const summary = summarizeMyWeek(rows);
assert.equal(summary.shiftCount, 4);
assert.equal(summary.doneCount, 1);
assert.equal(summary.doneMinutes, 450);
assert.deepEqual(summary.dailyMinutes, [450, 450, 450, 0, 0, 330, 0]);

// 每个班的结果。
assert.deepEqual(myShiftOutcome(rows[0].schedules[0], true), { kind: "completed", paidMinutes: 450 });
assert.deepEqual(myShiftOutcome(rows[1].schedules[0], true), { kind: "missingClockOut" });
assert.deepEqual(myShiftOutcome(rows[2].schedules[0], false), { kind: "working", workedMinutes: 243 });
assert.deepEqual(myShiftOutcome(rows[4].schedules[0], false), { kind: "leave", leaveType: "PaidSick" });
assert.deepEqual(myShiftOutcome(rows[5].schedules[0], false), { kind: "upcoming" });
assert.deepEqual(myShiftOutcome(s("2026-11-08", "10:00", "16:00", { scheduleState: "NotStarted" }), true), { kind: "absent" });
assert.deepEqual(myShiftOutcome(s("2026-11-08", "10:00", "16:00"), true), { kind: "pastUnknown" });
// 已完成但旧后端没给计薪分钟：退回排班计薪（8 小时扣 1 次餐 = 450）。
assert.deepEqual(myShiftOutcome(s("2026-11-08", "10:00", "18:00", { scheduleState: "Completed" }), true), { kind: "completed", paidMinutes: 450 });

// 下周填写进度：只数今天及以后、没排班的日子。
const availability = (workDate: string, isUnavailable = false) =>
  ({ availabilityGuid: workDate, workDate, startTime: "00:00", endTime: "23:59", status: "Active", isUnavailable }) as AttendanceAvailability;
const nextWeekRows = buildMyWeekRows("2026-11-16", "2026-11-11", { weekStart: "2026-11-16", weekEnd: "2026-11-22", days: [] }, [
  availability("2026-11-16"),
  availability("2026-11-18", true),
]);
assert.deepEqual(availabilityFillProgress(nextWeekRows), { total: 7, filled: 2, unpublished: true });
assert.deepEqual(availabilityFillProgress(rows), { total: 2, filled: 0, unpublished: false });

assert.equal(requestProgressStep("Pending"), 2);
assert.equal(requestProgressStep("Approved"), 3);
assert.equal(requestProgressStep("Rejected"), 3);

// 已等多久：后端 UTC 不带 Z 也按 UTC。
const now = Date.parse("2026-11-11T08:00:00Z");
assert.deepEqual(waitedSince("2026-11-11T07:35:00", now), { unit: "minutes", count: 25 });
assert.deepEqual(waitedSince("2026-11-10T12:00:00Z", now), { unit: "hours", count: 20 });
assert.deepEqual(waitedSince("2026-11-07T01:00:00Z", now), { unit: "days", count: 4 });
assert.equal(waitedSince(undefined, now), undefined);

console.log("attendance-my-week-view tests passed");
