import assert from "node:assert/strict";
import {
  buildAttendanceTodayDisplay,
  normalizeAttendanceToday,
  resolveAttendancePunchExceptionMinutes,
  splitExceptionDuration,
} from "./attendance-today-normalization";

// 异常时长：不足 60 分钟只有分钟；满 60 分钟起拆成小时 + 分钟（整小时分钟为 0，界面不再补「0 分钟」）
assert.deepEqual(splitExceptionDuration(0), { hours: 0, minutes: 0 });
assert.deepEqual(splitExceptionDuration(45), { hours: 0, minutes: 45 });
assert.deepEqual(splitExceptionDuration(59), { hours: 0, minutes: 59 });
assert.deepEqual(splitExceptionDuration(60), { hours: 1, minutes: 0 }, "正好 60 分钟 = 1 小时");
assert.deepEqual(splitExceptionDuration(65), { hours: 1, minutes: 5 });
assert.deepEqual(splitExceptionDuration(120), { hours: 2, minutes: 0 });
assert.deepEqual(splitExceptionDuration(135), { hours: 2, minutes: 15 });
assert.deepEqual(splitExceptionDuration(59.6), { hours: 1, minutes: 0 }, "小数分钟先四舍五入再拆分");
assert.deepEqual(splitExceptionDuration(-5), { hours: 0, minutes: 0 }, "负数按 0 处理");

assert.equal(
  resolveAttendancePunchExceptionMinutes({
    status: "Late",
    lateMinutes: 10,
    earlyArrivalMinutes: 0,
  }),
  10,
  "迟到必须读取 lateMinutes，不能被不适用的 earlyArrivalMinutes=0 覆盖",
);
assert.equal(
  resolveAttendancePunchExceptionMinutes({
    status: "Early",
    lateMinutes: 0,
    earlyArrivalMinutes: 17,
  }),
  17,
  "早到必须读取 earlyArrivalMinutes",
);
assert.equal(
  resolveAttendancePunchExceptionMinutes({
    status: "EarlyLeave",
    earlyLeaveMinutes: 30,
    lateDepartureMinutes: 0,
  }),
  30,
  "早退必须读取 earlyLeaveMinutes，不能被不适用的 lateDepartureMinutes=0 覆盖",
);
assert.equal(
  resolveAttendancePunchExceptionMinutes({
    status: "LateLeave",
    earlyLeaveMinutes: 0,
    lateDepartureMinutes: 22,
  }),
  22,
  "晚退必须读取 lateDepartureMinutes",
);
assert.equal(
  resolveAttendancePunchExceptionMinutes({
    status: "Normal",
    lateMinutes: 10,
  }),
  undefined,
  "Normal 没有异常分钟语义",
);
assert.equal(
  resolveAttendancePunchExceptionMinutes({
    status: "CustomException",
    earlyArrivalMinutes: 0,
    lateMinutes: 9,
  }),
  9,
  "未知状态回退时不得让无意义的 0 掩盖有效分钟",
);

const multiSegmentToday = normalizeAttendanceToday({
  WorkDate: "2026-07-21",
  StorePunchStates: [
    {
      StoreCode: "A",
      StoreName: "A 店",
      RelatedReminder: "A 店尚未下班，但已在 B 店上班",
      ScheduleSessions: [
        {
          ScheduleGuid: "schedule-a",
          StartTime: "09:00",
          EndTime: "18:00",
          WorkedMinutes: 420,
          OvertimeRawMinutes: 37,
          OvertimeCandidateMinutes: 30,
          ApprovedMinutes: 15,
          AdjustmentStatus: "Pending",
          Segments: [
            {
              SegmentNumber: 1,
              ClockIn: {
                EffectivePunchTime: "2026-07-21T08:43:00+10:00",
                Status: "Normal",
                EarlyArrivalMinutes: 17,
              },
              ClockOut: {
                EffectivePunchTime: "2026-07-21T12:00:00+10:00",
                Status: "Normal",
              },
              WorkedMinutes: 197,
            },
            {
              SegmentNumber: 2,
              ClockIn: {
                EffectivePunchTime: "2026-07-21T12:45:00+10:00",
                Status: "Late",
                LateMinutes: 45,
              },
              ClockOut: {
                EffectivePunchTime: "2026-07-21T16:28:00+10:00",
                Status: "EarlyLeave",
                EarlyLeaveMinutes: 92,
                LateDepartureMinutes: 28,
              },
              WorkedMinutes: 223,
            },
          ],
        },
      ],
    },
    {
      storeCode: "B",
      storeName: "B Store",
      relatedReminder: "A Store has an unfinished shift",
      scheduleSessions: [
        {
          scheduleGuid: "schedule-b",
          startTime: "18:30",
          endTime: "22:00",
          segments: [
            {
              segmentNumber: 1,
              clockIn: {
                effectivePunchTime: "2026-07-21T18:31:00+10:00",
                status: "Normal",
              },
            },
          ],
        },
      ],
    },
  ],
  NextPunchType: "ClockOut",
  CanClockIn: false,
  CanClockOut: true,
});

assert.equal(multiSegmentToday.storePunchStates.length, 2);
assert.equal(multiSegmentToday.scheduleSessions.length, 2);
assert.equal(multiSegmentToday.scheduleSessions[0]?.segments.length, 2);
assert.equal(
  multiSegmentToday.scheduleSessions[0]?.segments[0]?.clockIn?.effectivePunchTime,
  "2026-07-21T08:43:00+10:00",
);
assert.equal(multiSegmentToday.scheduleSessions[0]?.overtimeCandidateMinutes, 30);
assert.equal(multiSegmentToday.scheduleSessions[0]?.approvedOvertimeMinutes, 15);
assert.equal(multiSegmentToday.scheduleSessions[0]?.adjustmentStatus, "Pending");
assert.equal(multiSegmentToday.relatedStoreReminders.length, 2);

const display = buildAttendanceTodayDisplay(multiSegmentToday);
const firstSession = display.stores[0]?.sessions[0];
assert.equal(firstSession?.workedMinutes, 420, "工时优先采用后端结果");
assert.equal(firstSession?.segments[0]?.isBreakAfter, true, "中间下班后应标记休息中");
assert.equal(firstSession?.segments[0]?.showClockInException, true);
assert.equal(firstSession?.segments[0]?.showClockOutException, false);
assert.equal(firstSession?.segments[1]?.showClockInException, false);
assert.equal(firstSession?.segments[1]?.showClockOutException, true);
assert.equal(
  firstSession?.segments[1]?.clockIn?.lateMinutes,
  45,
  "保留后端异常数据，但由展示标志限制只在首上班/最终下班显示",
);
assert.equal(firstSession?.overtime.rawMinutes, 37);
assert.equal(firstSession?.overtime.candidateMinutes, 30);
assert.equal(firstSession?.overtime.approvedMinutes, 15);

const fallback = normalizeAttendanceToday({
  workDate: "2026-07-21",
  schedules: [
    {
      scheduleGuid: "legacy-schedule",
      storeCode: "LEGACY",
      storeName: "Legacy Store",
      userGuid: "user-1",
      workDate: "2026-07-21",
      startTime: "09:00",
      endTime: "17:00",
      status: "Active",
      isMine: true,
    },
  ],
  punches: [
    {
      punchGuid: "in-1",
      scheduleGuid: "legacy-schedule",
      storeCode: "LEGACY",
      workDate: "2026-07-21",
      punchType: "ClockIn",
      punchTimeLocal: "2026-07-21T09:00:00+10:00",
      status: "Normal",
    },
    {
      punchGuid: "out-1",
      scheduleGuid: "legacy-schedule",
      storeCode: "LEGACY",
      workDate: "2026-07-21",
      punchType: "ClockOut",
      punchTimeLocal: "2026-07-21T12:00:00+10:00",
      status: "Normal",
    },
    {
      punchGuid: "in-2",
      scheduleGuid: "legacy-schedule",
      storeCode: "LEGACY",
      workDate: "2026-07-21",
      punchType: "ClockIn",
      punchTimeLocal: "2026-07-21T13:00:00+10:00",
      status: "Normal",
    },
    {
      punchGuid: "out-2",
      scheduleGuid: "legacy-schedule",
      storeCode: "LEGACY",
      workDate: "2026-07-21",
      punchType: "ClockOut",
      punchTimeLocal: "2026-07-21T17:00:00+10:00",
      status: "Normal",
    },
  ],
});

assert.equal(fallback.storePunchStates.length, 1);
assert.equal(fallback.scheduleSessions.length, 1);
assert.equal(fallback.scheduleSessions[0]?.segments.length, 2);
assert.equal(
  buildAttendanceTodayDisplay(fallback).stores[0]?.sessions[0]?.workedMinutes,
  420,
  "旧响应回退应累加班段工时且排除午休间隔",
);

const singleSegmentFallback = normalizeAttendanceToday({
  WorkDate: "2026-07-21",
  Schedules: [{ ScheduleGuid: "single", StoreCode: "S", StartTime: "09:00", EndTime: "17:00", Status: "Active" }],
  Punches: [{ PunchGuid: "single-in", ScheduleGuid: "single", StoreCode: "S", PunchType: "ClockIn", PunchTimeLocal: "09:01", Status: "Normal" }],
});
assert.equal(singleSegmentFallback.scheduleSessions[0]?.segments.length, 1);
assert.equal(singleSegmentFallback.scheduleSessions[0]?.segments[0]?.segmentNumber, 1);

const linkedStores = normalizeAttendanceToday({
  workDate: "2026-07-21",
  schedules: [
    { scheduleGuid: "a", storeCode: "A", storeName: "A 店", status: "Active", segments: [] },
    { scheduleGuid: "b", storeCode: "B", storeName: "B 店", status: "Active", segments: [] },
  ],
  storePunchStates: [
    { storeCode: "A", storeName: "A 店", state: "MissingClockOut", hasMissingClockOut: true, hasOpenSegment: false },
    { storeCode: "B", storeName: "B 店", state: "Working", hasMissingClockOut: false, hasOpenSegment: true },
  ],
});
assert.equal(linkedStores.storePunchStates[0]?.state, "MissingClockOut");
assert.equal(linkedStores.storePunchStates[1]?.hasOpenSegment, true);
assert.deepEqual(buildAttendanceTodayDisplay(linkedStores).relatedStoreAlerts, [
  {
    missingStoreCode: "A",
    missingStoreName: "A 店",
    activeStoreCode: "B",
    activeStoreName: "B 店",
  },
]);
assert.equal(
  buildAttendanceTodayDisplay(linkedStores).stores.length,
  2,
  "有排班的门店仍应展示",
);

const duplicateStoreStates = normalizeAttendanceToday({
  workDate: "2026-07-21",
  storePunchStates: [
    {
      storeCode: "A",
      storeName: "A 店",
      state: "MissingClockOut",
      hasMissingClockOut: true,
      hasOpenSegment: false,
      scheduleSessions: [{ scheduleGuid: "a-missing", storeCode: "A", status: "Active", scheduleState: "MissingClockOut" }],
    },
    {
      storeCode: "A",
      storeName: "A 店",
      state: "Completed",
      hasMissingClockOut: false,
      hasOpenSegment: false,
      scheduleSessions: [{ scheduleGuid: "a-done", storeCode: "A", status: "Active", scheduleState: "Completed" }],
    },
    {
      storeCode: "B",
      storeName: "B 店",
      state: "Working",
      hasMissingClockOut: false,
      hasOpenSegment: true,
      scheduleSessions: [{ scheduleGuid: "b-working", storeCode: "B", status: "Active", scheduleState: "Working" }],
    },
  ],
});
const duplicateStoreA = duplicateStoreStates.storePunchStates.find((store) => store.storeCode === "A");
assert.equal(duplicateStoreStates.storePunchStates.length, 2, "同店多排班状态必须聚合成一个分店");
assert.equal(duplicateStoreA?.scheduleSessions.length, 2);
assert.equal(duplicateStoreA?.hasMissingClockOut, true, "任一排班漏下班都不能被完成排班覆盖");
assert.equal(duplicateStoreA?.state, "MissingClockOut");
assert.equal(
  buildAttendanceTodayDisplay(duplicateStoreStates).relatedStoreAlerts.length,
  1,
  "A 店漏下班且 B 店已上班时必须保留跨店提醒",
);

const stateOnlyStore = normalizeAttendanceToday({
  workDate: "2026-07-21",
  storePunchStates: [
    { storeCode: "A", state: "MissingClockOut", hasMissingClockOut: true },
    { storeCode: "B", state: "Working", hasOpenSegment: true },
  ],
});
const stateOnlyDisplay = buildAttendanceTodayDisplay(stateOnlyStore);
assert.equal(stateOnlyDisplay.stores.length, 0, "只有跨店状态而无排班时不能渲染 0 shifts");
assert.equal(stateOnlyDisplay.relatedStoreAlerts.length, 1, "零排班 state 仍应用于关联提醒");

const consecutiveOut = normalizeAttendanceToday({
  workDate: "2026-07-21",
  schedules: [{ scheduleGuid: "out-sequence", storeCode: "A", status: "Active" }],
  punches: [
    { punchGuid: "in", scheduleGuid: "out-sequence", storeCode: "A", punchType: "ClockIn", punchTimeLocal: "09:00", status: "Normal" },
    { punchGuid: "out-1", scheduleGuid: "out-sequence", storeCode: "A", punchType: "ClockOut", punchTimeLocal: "12:00", status: "Normal" },
    { punchGuid: "out-2", scheduleGuid: "out-sequence", storeCode: "A", punchType: "ClockOut", punchTimeLocal: "12:05", status: "Duplicate" },
  ],
});
assert.equal(consecutiveOut.scheduleSessions[0]?.segments.length, 2);
assert.equal(consecutiveOut.scheduleSessions[0]?.segments[0]?.clockOut?.punchGuid, "out-1");
assert.equal(consecutiveOut.scheduleSessions[0]?.segments[1]?.clockIn, undefined);
assert.equal(consecutiveOut.scheduleSessions[0]?.segments[1]?.clockOut?.punchGuid, "out-2");

const firstOut = normalizeAttendanceToday({
  workDate: "2026-07-21",
  schedules: [{ scheduleGuid: "first-out", storeCode: "A", status: "Active" }],
  punches: [{ punchGuid: "out-only", scheduleGuid: "first-out", storeCode: "A", punchType: "ClockOut", punchTimeLocal: "09:00", status: "Invalid" }],
});
assert.equal(firstOut.scheduleSessions[0]?.segments[0]?.clockIn, undefined);
assert.equal(firstOut.scheduleSessions[0]?.segments[0]?.clockOut?.punchGuid, "out-only");

const consecutiveIn = normalizeAttendanceToday({
  workDate: "2026-07-21",
  schedules: [{ scheduleGuid: "in-sequence", storeCode: "A", status: "Active" }],
  punches: [
    { punchGuid: "in-1", scheduleGuid: "in-sequence", storeCode: "A", punchType: "ClockIn", punchTimeLocal: "09:00", status: "Normal" },
    { punchGuid: "in-2", scheduleGuid: "in-sequence", storeCode: "A", punchType: "ClockIn", punchTimeLocal: "09:05", status: "Duplicate" },
    { punchGuid: "out", scheduleGuid: "in-sequence", storeCode: "A", punchType: "ClockOut", punchTimeLocal: "12:00", status: "Normal" },
  ],
});
assert.equal(consecutiveIn.scheduleSessions[0]?.segments.length, 2);
assert.equal(consecutiveIn.scheduleSessions[0]?.segments[0]?.clockOut, undefined);
assert.equal(consecutiveIn.scheduleSessions[0]?.segments[1]?.clockOut?.punchGuid, "out");

const indexed = normalizeAttendanceToday({
  workDate: "2026-07-21",
  schedules: [{ scheduleGuid: "indexed", storeCode: "A", status: "Active" }],
  punches: [
    { punchGuid: "in-1", scheduleGuid: "indexed", storeCode: "A", segmentIndex: 1, punchType: "ClockIn", punchTimeLocal: "09:00", status: "Normal" },
    { punchGuid: "in-2", scheduleGuid: "indexed", storeCode: "A", segmentIndex: 2, punchType: "ClockIn", punchTimeLocal: "09:05", status: "Normal" },
    { punchGuid: "out-1", scheduleGuid: "indexed", storeCode: "A", segmentIndex: 1, punchType: "ClockOut", punchTimeLocal: "12:00", status: "Normal" },
  ],
});
assert.equal(indexed.scheduleSessions[0]?.segments[0]?.clockOut?.punchGuid, "out-1");
assert.equal(indexed.scheduleSessions[0]?.segments[1]?.clockOut, undefined);

const orphanOnly = normalizeAttendanceToday({
  workDate: "2026-07-21",
  punches: [
    { punchGuid: "orphan-in", scheduleGuid: null, storeCode: "A", storeName: "A 店", punchType: "ClockIn", punchTimeLocal: "09:00", status: "NoSchedule" },
    { punchGuid: "orphan-out", scheduleGuid: null, storeCode: "A", storeName: "A 店", punchType: "ClockOut", punchTimeLocal: "10:00", status: "NoSchedule" },
  ],
});
assert.equal(orphanOnly.scheduleSessions.length, 1, "无排班打卡不得从时间线消失");
assert.equal(orphanOnly.scheduleSessions[0]?.scheduleState, "NoSchedule");
assert.equal(orphanOnly.scheduleSessions[0]?.segments[0]?.clockIn?.punchGuid, "orphan-in");
assert.equal(orphanOnly.scheduleSessions[0]?.segments[0]?.clockOut?.punchGuid, "orphan-out");
assert.equal(buildAttendanceTodayDisplay(orphanOnly).stores[0]?.sessions.length, 1);

const mixedOrphan = normalizeAttendanceToday({
  workDate: "2026-07-21",
  schedules: [{ scheduleGuid: "known", storeCode: "A", status: "Active" }],
  punches: [
    { punchGuid: "known-in", scheduleGuid: "known", storeCode: "A", punchType: "ClockIn", punchTimeLocal: "09:00", status: "Normal" },
    { punchGuid: "orphan-history", scheduleGuid: null, storeCode: "A", punchType: "ClockOut", punchTimeLocal: "08:30", status: "NoSchedule" },
  ],
});
assert.equal(mixedOrphan.scheduleSessions.length, 2, "历史 null ScheduleGuid 必须进入独立未排班组");
assert.equal(mixedOrphan.scheduleSessions[1]?.segments[0]?.clockOut?.punchGuid, "orphan-history");

console.log("attendance-today-normalization.test.ts: ok");
