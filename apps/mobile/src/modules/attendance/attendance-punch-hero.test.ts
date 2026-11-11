import assert from "node:assert/strict";
import {
  lastClockIn,
  liveWorkedMinutes,
  localMillis,
  minutesToShiftStart,
  nextShiftAfter,
  shiftProgress,
  shiftWindow,
  weekDayShiftLabel,
} from "./attendance-punch-hero";
import type { AttendancePunch, AttendanceSchedule, AttendanceWeek } from "./types";

const punch = (time: string, extra: Partial<AttendancePunch> = {}) =>
  ({ punchGuid: time, workDate: "2026-11-11", punchType: "ClockIn", status: "Normal", punchTimeLocal: `2026-11-11T${time}:00`, ...extra }) as AttendancePunch;
const at = (time: string) => localMillis(`2026-11-11T${time}:00`)!;

// 已工作：闭合段 + 还开着的段算到现在；系统自动下班的段不算。
assert.equal(liveWorkedMinutes([{ clockIn: punch("10:02"), clockOut: punch("12:30") }, { clockIn: punch("13:00") }], at("14:05")), 148 + 65);
assert.equal(liveWorkedMinutes([{ clockIn: punch("16:00"), clockOut: punch("23:59", { isSystemAutoClockOut: true }) }], at("23:59")), 0);
assert.equal(liveWorkedMinutes([], at("10:00")), 0);

// 排班窗口与进度；跨午夜。
const window = shiftWindow("2026-11-11", "10:00:00", "18:00:00")!;
assert.equal(window.end - window.start, 8 * 3_600_000);
assert.equal(shiftWindow("2026-11-11", "22:00", "06:00")!.end - shiftWindow("2026-11-11", "22:00", "06:00")!.start, 8 * 3_600_000);
assert.deepEqual(shiftProgress(window, at("14:00")), { ratio: 0.5, remainingMinutes: 240 });
assert.deepEqual(shiftProgress(window, at("19:00")), { ratio: 1, remainingMinutes: 0 });
assert.equal(minutesToShiftStart(window, at("09:52")), 8);
assert.equal(minutesToShiftStart(window, at("10:26")), -26);

assert.equal(lastClockIn([{ clockIn: punch("10:02"), clockOut: punch("12:30") }, { clockIn: punch("13:00") }])?.punchGuid, "13:00");

// 下一个班：只看之后的日子，跳过请假与已取消。
const s = (workDate: string, startTime: string, endTime: string, extra: Partial<AttendanceSchedule> = {}) =>
  ({ scheduleGuid: `${workDate}${startTime}`, storeCode: "ROB", userGuid: "u", workDate, startTime, endTime, status: "Active", ...extra }) as AttendanceSchedule;
const week: AttendanceWeek = {
  weekStart: "2026-11-09",
  weekEnd: "2026-11-15",
  days: [
    { workDate: "2026-11-11", dayOfWeek: 3, schedules: [s("2026-11-11", "10:00", "18:00")] },
    { workDate: "2026-11-12", dayOfWeek: 4, schedules: [s("2026-11-12", "09:00", "17:00", { leaveType: "PaidAnnual" })] },
    { workDate: "2026-11-13", dayOfWeek: 5, schedules: [s("2026-11-13", "09:00", "17:00", { status: "Cancelled" })] },
    { workDate: "2026-11-14", dayOfWeek: 6, schedules: [s("2026-11-14", "10:00", "16:00", { status: "Draft" })] },
  ],
};
assert.equal(nextShiftAfter(week, "2026-11-11")?.workDate, "2026-11-14");
assert.equal(nextShiftAfter(week, "2026-11-14"), undefined);

// 日期条小字。
assert.deepEqual(weekDayShiftLabel([s("2026-11-11", "10:00:00", "18:30:00")]), { kind: "shift", text: "10–18:30", more: 0 });
assert.deepEqual(weekDayShiftLabel([]), { kind: "rest" });
assert.deepEqual(weekDayShiftLabel([s("2026-11-12", "09:00", "17:00", { leaveType: "PaidAnnual" })]), { kind: "leave" });

console.log("attendance-punch-hero tests passed");
