import assert from "node:assert/strict";
import {
  buildAttendanceMealDeclaration,
  findActiveMealSession,
  formatMealElapsed,
  isMealClaimPendingReview,
  mealAddBackMinutes,
  normalizeAttendanceMealClaim,
  normalizeAttendanceMealState,
  resolveAttendanceMealPanelState,
  shouldConfirmMealBeforeClockOut,
} from "./attendance-meal-break";
import { normalizeAttendanceToday } from "./attendance-today-normalization";
import type { AttendanceMealState, AttendanceScheduleSession } from "./types";

// 后端从数据库读出的 UTC 时间不带 Z，必须仍按 UTC 解析。
const meal = normalizeAttendanceMealState({
  ScheduleGuid: "sch-1",
  StoreCode: "BRI",
  EffectiveMealBreakCount: 1,
  HandledCount: 0,
  RequiredCount: 1,
  ClockOutWouldBeFinal: true,
  MissingCountIfClockOutNow: 1,
  HasOpenBreak: false,
  NextReminderAtUtc: "2026-05-18T02:42:00",
  ServerTimeUtc: "2026-05-18T07:22:00Z",
  Breaks: [{ BreakGuid: "b1", StartUtc: "2026-05-18T03:00:00", EndUtc: null }],
});
assert.ok(meal);
assert.equal(meal.scheduleGuid, "sch-1");
assert.equal(meal.missingCountIfClockOutNow, 1);
assert.equal(meal.clockOutWouldBeFinal, true);
assert.equal(meal.breaks.length, 1);
assert.equal(meal.breaks[0]?.endUtc, undefined);
assert.equal(normalizeAttendanceMealState(undefined), undefined, "旧后端没有 meal 字段时不出现用餐区");

const claim = normalizeAttendanceMealClaim({
  claimGuid: "c1",
  workDate: "2026-05-18T00:00:00",
  expectedCount: 1,
  recordedCount: 0,
  missingCount: 1,
  notTakenCount: 1,
  claimedMinutes: 30,
  status: "Pending",
  reason: "太忙",
});
assert.equal(claim?.workDate, "2026-05-18");
assert.equal(claim?.approvedMinutes, undefined);
assert.equal(isMealClaimPendingReview(claim), true);
assert.equal(isMealClaimPendingReview({ ...claim!, status: "None", claimedMinutes: 0 }), false);
assert.equal(normalizeAttendanceMealClaim({ status: "Pending" }), undefined, "没有 claimGuid 的声明视为不存在");

// 用餐区状态：到期、未到期、休息中、已休息够。
const baseMeal: AttendanceMealState = {
  scheduleGuid: "sch-1",
  storeCode: "BRI",
  effectiveMealBreakCount: 1,
  handledCount: 0,
  requiredCount: 0,
  clockOutWouldBeFinal: false,
  missingCountIfClockOutNow: 0,
  hasOpenBreak: false,
  nextReminderAtUtc: "2026-05-18T02:42:00",
  breaks: [],
};
const reminderMs = Date.UTC(2026, 4, 18, 2, 42);
assert.deepEqual(
  resolveAttendanceMealPanelState(baseMeal, reminderMs - 60_000),
  { kind: "upcoming", dueAtMs: reminderMs },
);
assert.deepEqual(
  resolveAttendanceMealPanelState(baseMeal, reminderMs),
  { kind: "due", dueAtMs: reminderMs },
  "连续工作满 4 小时的那一刻开始提醒",
);
assert.deepEqual(
  resolveAttendanceMealPanelState({ ...baseMeal, nextReminderAtUtc: undefined }, reminderMs),
  { kind: "upcoming", dueAtMs: undefined },
);
assert.deepEqual(
  resolveAttendanceMealPanelState(
    { ...baseMeal, hasOpenBreak: true, openBreakStartedAtUtc: "2026-05-18T03:00:00" },
    Date.UTC(2026, 4, 18, 3, 12, 5),
  ),
  { kind: "onBreak", startedAtMs: Date.UTC(2026, 4, 18, 3, 0), elapsedSeconds: 725 },
);
assert.deepEqual(
  resolveAttendanceMealPanelState({ ...baseMeal, handledCount: 1 }, reminderMs),
  { kind: "done" },
  "用餐够了就不再提醒",
);

// 扫码前确认：只在最后一次下班、且缺休息时问。
assert.equal(shouldConfirmMealBeforeClockOut(undefined), false);
assert.equal(shouldConfirmMealBeforeClockOut(baseMeal), false);
assert.equal(shouldConfirmMealBeforeClockOut({ ...baseMeal, missingCountIfClockOutNow: 1 }), false,
  "班段中间的下班不追问");
assert.equal(
  shouldConfirmMealBeforeClockOut({ ...baseMeal, clockOutWouldBeFinal: true, missingCountIfClockOutNow: 1 }),
  true,
);
assert.deepEqual(buildAttendanceMealDeclaration("taken", 1), { notTakenCount: 0 });
assert.deepEqual(buildAttendanceMealDeclaration("notTaken", 2), { notTakenCount: 2 });
assert.equal(mealAddBackMinutes(2), 60);

// 只取正在上班、且排班有用餐要求的那条排班。
const session = (overrides: Partial<AttendanceScheduleSession>): AttendanceScheduleSession => ({
  scheduleGuid: "sch",
  storeCode: "BRI",
  userGuid: "u",
  workDate: "2026-05-18",
  startTime: "09:00",
  endTime: "17:00",
  status: "Active",
  isMine: true,
  segments: [],
  ...overrides,
});
assert.equal(findActiveMealSession([session({ meal: baseMeal, hasOpenSegment: false })]), undefined,
  "没在上班时不出现用餐区");
assert.equal(findActiveMealSession([session({ meal: { ...baseMeal, effectiveMealBreakCount: 0 }, hasOpenSegment: true })]), undefined,
  "排班不用餐时不出现用餐区");
assert.equal(
  findActiveMealSession([
    session({ scheduleGuid: "done", meal: baseMeal, hasOpenSegment: false }),
    session({ scheduleGuid: "working", meal: baseMeal, hasOpenSegment: true }),
  ])?.scheduleGuid,
  "working",
);
assert.equal(
  findActiveMealSession([session({ meal: { ...baseMeal, hasOpenBreak: true }, hasOpenSegment: false })])?.scheduleGuid,
  "sch",
  "休息中即使班段状态没刷新，也要能结束休息",
);

assert.equal(formatMealElapsed(0), "0:00");
assert.equal(formatMealElapsed(725), "12:05");
assert.equal(formatMealElapsed(3725), "1:02:05");

// 今日接口规范化：排班项的计薪字段与 meal 状态要透传到 scheduleSessions。
const today = normalizeAttendanceToday({
  workDate: "2026-05-18",
  schedules: [{
    scheduleGuid: "sch-1",
    storeCode: "BRI",
    workDate: "2026-05-18",
    startTime: "09:00:00",
    endTime: "17:00:00",
    status: "Active",
    hasOpenSegment: true,
    effectiveMealBreakCount: 1,
    mealDeductionMinutes: 30,
    pendingMealAddBackMinutes: 30,
    approvedMealAddBackMinutes: 0,
    paidMinutes: 490,
    meal: { scheduleGuid: "sch-1", storeCode: "BRI", effectiveMealBreakCount: 1, handledCount: 0 },
  }],
  punches: [],
});
const normalizedSession = today.scheduleSessions[0];
assert.equal(normalizedSession?.effectiveMealBreakCount, 1);
assert.equal(normalizedSession?.mealDeductionMinutes, 30);
assert.equal(normalizedSession?.pendingMealAddBackMinutes, 30);
assert.equal(normalizedSession?.paidMinutes, 490);
assert.equal(normalizedSession?.meal?.effectiveMealBreakCount, 1);
assert.equal(findActiveMealSession(today.scheduleSessions)?.scheduleGuid, "sch-1");

console.log("attendance-meal-break.test.ts: ok");
