import assert from "node:assert/strict";
import type { AttendanceApproval, AttendanceScheduleSession } from "./types";

// 相对时间依赖设备时区：固定为不走夏令时的布里斯班（UTC+10），结果与机器所在地无关。
process.env.TZ = "Australia/Brisbane";

function approval(overrides: Partial<AttendanceApproval>): AttendanceApproval {
  return {
    approvalGuid: overrides.approvalGuid ?? "a",
    sourceGuid: "s",
    sourceType: "PunchAdjustment",
    title: "PunchAdjustment",
    status: "Pending",
    ...overrides,
  };
}

function punch(time: string, overrides: Record<string, unknown> = {}) {
  return { punchGuid: `p-${time}`, workDate: "2026-10-17", punchType: "ClockIn", status: "Normal", punchTimeLocal: time, ...overrides };
}

async function run() {
  const board = await import("./approval-board");

  // 分组：最近的工作日在上，没有日期的排最后；组内新提交的在上；周末 / 公假给出时薪档。
  const groups = board.groupApprovalsByWorkDate([
    approval({ approvalGuid: "old", workDate: "2026-10-17T00:00:00", submittedAt: "2026-10-18T01:00:00" }),
    approval({ approvalGuid: "mon", workDate: "2026-10-19" }),
    approval({ approvalGuid: "new", workDate: "2026-10-17", submittedAt: "2026-10-19T01:00:00" }),
    approval({ approvalGuid: "none" }),
    approval({ approvalGuid: "sun", workDate: "2026-10-18" }),
    approval({ approvalGuid: "ph", workDate: "2026-10-05", holidayName: "King's Birthday" }),
  ]);
  assert.deepEqual(groups.map((group) => group.workDate), ["2026-10-19", "2026-10-18", "2026-10-17", "2026-10-05", ""]);
  assert.deepEqual(groups.map((group) => group.tier), ["weekday", "sunday", "saturday", "publicHoliday", "weekday"]);
  assert.deepEqual(groups[2].items.map((item) => item.approvalGuid), ["new", "old"]);
  assert.equal(board.weekdayIndex("2026-10-19"), 0, "2026-10-19 是周一");

  // 本人申请：后端 isSelf 优先；否则比对申请人（忽略大小写）。
  assert.equal(board.isSelfApproval(approval({ applicantUserGuid: "ABC" }), "abc"), true);
  assert.equal(board.isSelfApproval(approval({ applicantUserGuid: "abc", isSelf: false }), "abc"), false);
  assert.equal(board.isSelfApproval(approval({}), "abc"), false);

  // 提交时间：刚刚 / 分钟 / 小时 / 昨天 / 更早。now = 布里斯班 2026-10-20 08:00。
  const now = Date.parse("2026-10-19T22:00:00Z");
  assert.deepEqual(board.describeSubmittedAt("2026-10-19T21:59:40", now), { kind: "justNow" });
  assert.deepEqual(board.describeSubmittedAt("2026-10-19T21:15:00Z", now), { kind: "minutes", count: 45 });
  assert.deepEqual(board.describeSubmittedAt("2026-10-19T12:00:00", now), { kind: "hours", count: 10 });
  assert.deepEqual(board.describeSubmittedAt("2026-10-19T08:05:00", now), { kind: "yesterday", time: "18:05" });
  assert.deepEqual(board.describeSubmittedAt("2026-10-16T23:30:00", now), { kind: "date", date: "10-17", time: "09:30" });
  assert.equal(board.describeSubmittedAt(undefined, now), undefined);

  // 加班批准分钟：15 的倍数且不超过候选值。
  assert.deepEqual(board.listOvertimeMinuteOptions(45), [0, 15, 30, 45]);
  assert.deepEqual(board.listOvertimeMinuteOptions(49), [0, 15, 30, 45]);
  assert.deepEqual(board.listOvertimeMinuteOptions(10), [0]);
  assert.equal(board.defaultOvertimeMinutes(49), 45);
  assert.equal(board.formatDurationMinutes(244), "4h 04m");
  assert.equal(board.formatDurationMinutes(45), "45m");

  // 时间轴：两段 + 31 分钟空档算 1 次用餐；轴按 2 小时对齐。
  const session = {
    workDate: "2026-10-17",
    startTime: "09:00",
    endTime: "17:00",
    segments: [
      { segmentIndex: 0, segmentNumber: 1, clockIn: punch("2026-10-17T08:58"), clockOut: punch("2026-10-17T13:02") },
      { segmentIndex: 1, segmentNumber: 2, clockIn: punch("2026-10-17T13:33"), clockOut: punch("2026-10-17T17:47") },
    ],
  } as unknown as AttendanceScheduleSession;
  const timeline = board.buildApprovalTimeline(session);
  assert.deepEqual(timeline.schedule, { start: 540, end: 1020 });
  assert.deepEqual(timeline.segments.map((segment) => segment.durationMinutes), [244, 254]);
  assert.deepEqual(timeline.gaps, [{ afterSegment: 1, minutes: 31, countsAsMeal: true }]);
  assert.deepEqual(timeline.range, { start: 480, end: 1080 });
  assert.equal(timeline.ticks[0], 480);
  assert.equal(board.timelinePercent(780, timeline.range), 50);

  // 漏下班：系统 00:00 自动下班；店长填 21:31 后最后一段按填写时间计。
  const missing = {
    workDate: "2026-10-19",
    startTime: "16:00",
    endTime: "21:30",
    segments: [{
      segmentIndex: 0,
      segmentNumber: 1,
      clockIn: punch("2026-10-19T16:02"),
      clockOut: punch("2026-10-20T00:00", { punchGuid: "auto", punchType: "ClockOut", isSystemAutoClockOut: true }),
    }],
  } as unknown as AttendanceScheduleSession;
  assert.deepEqual(board.resolveMissingClockOutTimes(missing), {
    clockIn: 962,
    autoClockOutPunchGuid: "auto",
    autoClockOut: 1440,
  });
  const fixed = board.buildApprovalTimeline(missing, { overrideLastClockOut: 1291 });
  assert.equal(fixed.segments[0].durationMinutes, 329);
  assert.equal(fixed.range.end, 1320, "只按填写时间算，轴到 22 点");
  assert.equal(
    board.buildApprovalTimeline(missing, { overrideLastClockOut: 1291, extendRangeTo: 1440 }).range.end,
    1440,
    "漏下班详情要把轴延伸到自动下班的 00:00",
  );
  assert.equal(fixed.segments[0].isAutoClockOut, true);
  assert.equal(board.workDayMinutesToLocal("2026-10-19", 1291), "2026-10-19T21:31");
  assert.equal(board.workDayMinutesToLocal("2026-10-19", 1445), "2026-10-20T00:05");
  assert.equal(board.clockToMinutes("21:30:00"), 1290);
}

void run();
