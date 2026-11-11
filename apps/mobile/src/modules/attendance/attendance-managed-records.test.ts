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
  buildManagedDayAxis,
  datesWithExceptions,
  groupManagedRecords,
  mondayOfDate,
  shiftDateString,
  weekDatesFrom,
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


// 今天班次已结束却没打卡：传了当前时间就判「未打卡」，还没到结束时间仍是「未开始」。
assert.equal(classifyManagedRecord(session({ segments: [] }), "2026-05-18", 17 * 60 + 5), "noPunch");
assert.equal(classifyManagedRecord(session({ segments: [] }), "2026-05-18", 12 * 60), "notStarted");
// 跨午夜班次（22:00–06:00）当天 23:00 还没结束。
assert.equal(classifyManagedRecord(session({ startTime: "22:00:00", endTime: "06:00:00", segments: [] }), "2026-05-18", 23 * 60), "notStarted");

// 分组：需处理 → 工作中 → 未开始 → 已下班 → 请假，空组不返回。
{
  const rows = [
    { session: session({ scheduleGuid: "n" }), issue: "normal" as const },
    { session: session({ scheduleGuid: "l" }), issue: "late" as const },
    { session: session({ scheduleGuid: "v" }), issue: "onLeave" as const },
    { session: session({ scheduleGuid: "p" }), issue: "inProgress" as const },
    { session: session({ scheduleGuid: "m" }), issue: "missingClockOut" as const },
  ];
  const groups = groupManagedRecords(rows);
  assert.deepEqual(groups.map((group) => group.key), ["attention", "inProgress", "done", "onLeave"]);
  assert.deepEqual(groups[0].rows.map((row) => row.session.scheduleGuid), ["m", "l"]);
}

// 周日期：周三所在周的周一，及连续 7 天。
assert.equal(mondayOfDate("2026-11-11"), "2026-11-09");
assert.equal(mondayOfDate("2026-11-15"), "2026-11-09");
assert.equal(mondayOfDate("2026-11-09"), "2026-11-09");
assert.deepEqual(weekDatesFrom("2026-11-09"), ["2026-11-09", "2026-11-10", "2026-11-11", "2026-11-12", "2026-11-13", "2026-11-14", "2026-11-15"]);
assert.equal(shiftDateString("2026-11-09", -7), "2026-11-02");

// 红点：只标有异常的日子。
assert.deepEqual(
  [...datesWithExceptions([
    session({ workDate: "2026-05-18", segments: [{ clockIn: clockIn("Late"), clockOut: clockOut() }] }),
    session({ workDate: "2026-05-19", segments: [{ clockIn: clockIn(), clockOut: clockOut() }] }),
  ], "2026-05-20")],
  ["2026-05-18"],
);

// 时间轴：整点对齐，至少 8 小时，3 小时一个刻度。
assert.deepEqual(buildManagedDayAxis([8 * 60 + 52, 20 * 60]), { start: 480, end: 1200, ticks: [480, 660, 840, 1020, 1200] });
assert.deepEqual(buildManagedDayAxis([10 * 60, 14 * 60]).end - buildManagedDayAxis([10 * 60, 14 * 60]).start, 480);

console.log("attendance-managed-records.test.ts: ok");
