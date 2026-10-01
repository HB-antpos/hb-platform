import assert from "node:assert/strict";
import {
  buildAdjustmentReason,
  classifyManagedRecord,
  isManagedRecordException,
  isWithinManagedAdjustmentWindow,
  listSessionPunches,
  resolveDefaultMissingPunchTime,
  resolveDefaultMissingPunchType,
  resolveLocalPunchDayOffset,
  shiftLocalPunchTime,
  sortManagedRecords,
} from "./attendance-managed-records";
import { normalizeAttendanceToday } from "./attendance-today-normalization";
import type { AttendanceScheduleSession } from "./types";

function session(overrides: Record<string, unknown>): AttendanceScheduleSession {
  return normalizeAttendanceToday({
    schedules: [{
      scheduleGuid: "s1",
      storeCode: "BRI",
      userGuid: "u1",
      employeeName: "Lily",
      workDate: "2026-05-18",
      startTime: "09:00:00",
      endTime: "17:00:00",
      status: "Active",
      ...overrides,
    }],
  }).scheduleSessions[0]!;
}

const clockIn = (status = "Normal") => ({ punchGuid: "in", punchType: "ClockIn", status, punchTimeLocal: "2026-05-18T08:58:00" });
const clockOut = (status = "Normal") => ({ punchGuid: "out", punchType: "ClockOut", status, punchTimeLocal: "2026-05-18T17:01:00" });

// 异常分类：漏下班优先于迟到；过去日期无打卡算缺卡，当天未打卡算未开始。
assert.equal(classifyManagedRecord(session({ hasMissingClockOut: true, segments: [{ clockIn: clockIn("Late") }] }), "2026-05-19"), "missingClockOut");
assert.equal(classifyManagedRecord(session({ segments: [] }), "2026-05-19"), "noPunch");
assert.equal(classifyManagedRecord(session({ segments: [] }), "2026-05-18"), "notStarted");
assert.equal(classifyManagedRecord(session({ segments: [{ clockIn: clockIn("Late"), clockOut: clockOut() }] }), "2026-05-18"), "late");
assert.equal(classifyManagedRecord(session({ segments: [{ clockIn: clockIn(), clockOut: clockOut("EarlyLeave") }] }), "2026-05-18"), "earlyLeave");
assert.equal(classifyManagedRecord(session({ hasOpenSegment: true, segments: [{ clockIn: clockIn() }] }), "2026-05-18"), "inProgress");
assert.equal(classifyManagedRecord(session({ hasOpenSegment: true, segments: [{ clockIn: clockIn() }] }), "2026-05-19"), "missingClockOut");
assert.equal(classifyManagedRecord(session({ segments: [{ clockIn: clockIn(), clockOut: clockOut() }] }), "2026-05-18"), "normal");
assert.equal(classifyManagedRecord(session({ leaveType: "SickLeave", segments: [] }), "2026-05-19"), "onLeave", "已批准请假且无打卡不算缺卡");
assert.equal(isManagedRecordException("onLeave"), false);
assert.equal(isManagedRecordException("late"), true);
assert.equal(isManagedRecordException("inProgress"), false);

// 排序：异常在前，同级按班次开始时间。
const sorted = sortManagedRecords([
  { session: session({ scheduleGuid: "a", startTime: "08:00:00" }), issue: "normal" as const },
  { session: session({ scheduleGuid: "b", startTime: "10:00:00" }), issue: "late" as const },
  { session: session({ scheduleGuid: "c", startTime: "09:00:00" }), issue: "missingClockOut" as const },
]);
assert.deepEqual(sorted.map((row) => row.session.scheduleGuid), ["c", "b", "a"]);

// 工资周窗口与后端用例保持一致（2026-05-18 是周一）。
assert.equal(isWithinManagedAdjustmentWindow("2026-05-11", "2026-05-18"), true);
assert.equal(isWithinManagedAdjustmentWindow("2026-05-11", "2026-05-19"), true);
assert.equal(isWithinManagedAdjustmentWindow("2026-05-10", "2026-05-18"), false);
assert.equal(isWithinManagedAdjustmentWindow("2026-05-17", "2026-05-20"), false);
assert.equal(isWithinManagedAdjustmentWindow("2026-05-18", "2026-05-20"), true);
assert.equal(isWithinManagedAdjustmentWindow("2026-05-18", "2026-05-24"), true);
assert.equal(isWithinManagedAdjustmentWindow("2026-05-21", "2026-05-20"), false);

// 补录默认值：缺下班就补下班并取班次结束时间。
const open = session({ segments: [{ clockIn: clockIn() }] });
assert.equal(resolveDefaultMissingPunchType(open), "ClockOut");
assert.equal(resolveDefaultMissingPunchTime(open, "ClockOut"), "2026-05-18T17:00");
assert.equal(resolveDefaultMissingPunchType(session({ segments: [] })), "ClockIn");
assert.deepEqual(listSessionPunches(session({ segments: [{ clockIn: clockIn(), clockOut: clockOut() }] })).map((p) => p.punchGuid), ["in", "out"]);

// 时间步进：可跨午夜，但不早于工作日 00:00、不晚于次日 23:59。
assert.equal(shiftLocalPunchTime("2026-05-18T17:00", 5, "2026-05-18"), "2026-05-18T17:05");
assert.equal(shiftLocalPunchTime("2026-05-18T23:30", 60, "2026-05-18"), "2026-05-19T00:30");
assert.equal(shiftLocalPunchTime("2026-05-18T00:10", -60, "2026-05-18"), "2026-05-18T00:00");
assert.equal(shiftLocalPunchTime("2026-05-19T23:30", 60, "2026-05-18"), "2026-05-19T23:59");
assert.equal(resolveLocalPunchDayOffset("2026-05-19T00:30", "2026-05-18"), 1);
assert.equal(shiftLocalPunchTime("bad", 5, "2026-05-18"), "bad");

// 原因拼接。
assert.equal(buildAdjustmentReason("忘记打卡", "  "), "忘记打卡");
assert.equal(buildAdjustmentReason("其他", "手机没信号"), "其他：手机没信号");
assert.equal(buildAdjustmentReason(undefined, " 说明 "), "说明");

console.log("attendance-managed-records.test.ts: ok");
