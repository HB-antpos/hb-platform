import { toAttendanceUtcMillis } from "./attendance-device-time";
import { MEAL_BREAK_MINUTES } from "./attendance-my-week";
import type {
  AttendanceMealBreakRecord,
  AttendanceMealClaim,
  AttendanceMealDeclaration,
  AttendanceMealState,
  AttendanceScheduleSession,
} from "./types";

type ApiRecord = Record<string, unknown>;

function isRecord(value: unknown): value is ApiRecord {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function pick(raw: ApiRecord, ...keys: string[]) {
  for (const key of keys) {
    if (raw[key] !== undefined && raw[key] !== null) return raw[key];
  }
  return undefined;
}

function asOptionalString(value: unknown) {
  if (typeof value !== "string") return undefined;
  const trimmed = value.trim();
  return trimmed || undefined;
}

function asCount(value: unknown) {
  const parsed = typeof value === "number" ? value : typeof value === "string" ? Number(value) : NaN;
  return Number.isFinite(parsed) ? Math.max(0, Math.trunc(parsed)) : 0;
}

function asOptionalCount(value: unknown) {
  if (value === undefined || value === null || value === "") return undefined;
  return asCount(value);
}

function asBoolean(value: unknown) {
  return value === true || (typeof value === "string" && value.toLowerCase() === "true");
}

/** 「我的今日」排班项里的 meal 字段；旧后端不返回时为 undefined，界面整体不出现用餐提示。 */
export function normalizeAttendanceMealState(raw: unknown): AttendanceMealState | undefined {
  if (!isRecord(raw)) return undefined;
  const breaks = pick(raw, "breaks", "Breaks");
  return {
    scheduleGuid: asOptionalString(pick(raw, "scheduleGuid", "ScheduleGuid")) ?? "",
    storeCode: asOptionalString(pick(raw, "storeCode", "StoreCode")) ?? "",
    effectiveMealBreakCount: asCount(pick(raw, "effectiveMealBreakCount", "EffectiveMealBreakCount")),
    handledCount: asCount(pick(raw, "handledCount", "HandledCount")),
    requiredCount: asCount(pick(raw, "requiredCount", "RequiredCount")),
    clockOutWouldBeFinal: asBoolean(pick(raw, "clockOutWouldBeFinal", "ClockOutWouldBeFinal")),
    missingCountIfClockOutNow: asCount(pick(raw, "missingCountIfClockOutNow", "MissingCountIfClockOutNow")),
    hasOpenBreak: asBoolean(pick(raw, "hasOpenBreak", "HasOpenBreak")),
    openBreakStartedAtUtc: asOptionalString(pick(raw, "openBreakStartedAtUtc", "OpenBreakStartedAtUtc")),
    nextReminderAtUtc: asOptionalString(pick(raw, "nextReminderAtUtc", "NextReminderAtUtc")),
    serverTimeUtc: asOptionalString(pick(raw, "serverTimeUtc", "ServerTimeUtc")),
    breaks: Array.isArray(breaks)
      ? breaks.filter(isRecord).map((item): AttendanceMealBreakRecord => ({
          breakGuid: asOptionalString(pick(item, "breakGuid", "BreakGuid")) ?? "",
          startUtc: asOptionalString(pick(item, "startUtc", "StartUtc")) ?? "",
          endUtc: asOptionalString(pick(item, "endUtc", "EndUtc")),
        }))
      : [],
  };
}

/** 下班打卡响应或审批项里的用餐声明。 */
export function normalizeAttendanceMealClaim(raw: unknown): AttendanceMealClaim | undefined {
  if (!isRecord(raw)) return undefined;
  const claimGuid = asOptionalString(pick(raw, "claimGuid", "ClaimGuid"));
  if (!claimGuid) return undefined;
  const workDate = asOptionalString(pick(raw, "workDate", "WorkDate"));
  return {
    claimGuid,
    scheduleGuid: asOptionalString(pick(raw, "scheduleGuid", "ScheduleGuid")),
    workDate: workDate?.includes("T") ? workDate.slice(0, 10) : workDate,
    expectedCount: asCount(pick(raw, "expectedCount", "ExpectedCount")),
    recordedCount: asCount(pick(raw, "recordedCount", "RecordedCount")),
    missingCount: asCount(pick(raw, "missingCount", "MissingCount")),
    notTakenCount: asCount(pick(raw, "notTakenCount", "NotTakenCount")),
    claimedMinutes: asCount(pick(raw, "claimedMinutes", "ClaimedMinutes")),
    approvedMinutes: asOptionalCount(pick(raw, "approvedMinutes", "ApprovedMinutes")),
    status: asOptionalString(pick(raw, "status", "Status")) ?? "None",
    reason: asOptionalString(pick(raw, "reason", "Reason")),
  };
}

/**
 * 今日数据里正在上班（或休息中）、且排班有用餐要求的那条排班。
 * 有进行中班段的优先；都没有时返回 undefined，界面不出现用餐区。
 */
export function findActiveMealSession(
  sessions?: AttendanceScheduleSession[],
): (AttendanceScheduleSession & { meal: AttendanceMealState }) | undefined {
  const candidates = (sessions ?? []).filter(
    (session): session is AttendanceScheduleSession & { meal: AttendanceMealState } =>
      Boolean(session.meal && session.meal.effectiveMealBreakCount > 0
        && (session.hasOpenSegment || session.meal.hasOpenBreak)),
  );
  return candidates.find((session) => session.hasOpenSegment) ?? candidates[0];
}

export type AttendanceMealPanelState =
  | { kind: "onBreak"; startedAtMs?: number; elapsedSeconds: number }
  | { kind: "due"; dueAtMs: number }
  | { kind: "upcoming"; dueAtMs?: number }
  | { kind: "done" };

/**
 * 用餐区当前该显示什么：休息中显示计时；用餐够了只给一行确认；
 * 到了提醒时间（连续工作满 4 小时）显示提醒横幅；没到时只给一行提示和「开始休息」。
 * nowMs 用手机时间：提醒时间由后端算好，几分钟的时钟误差不影响提醒意义。
 */
export function resolveAttendanceMealPanelState(
  meal: AttendanceMealState,
  nowMs: number,
): AttendanceMealPanelState {
  if (meal.hasOpenBreak) {
    const startedAtMs = toAttendanceUtcMillis(meal.openBreakStartedAtUtc);
    return {
      kind: "onBreak",
      startedAtMs,
      elapsedSeconds: startedAtMs === undefined ? 0 : Math.max(0, Math.floor((nowMs - startedAtMs) / 1000)),
    };
  }
  if (meal.handledCount >= meal.effectiveMealBreakCount) {
    return { kind: "done" };
  }
  const dueAtMs = toAttendanceUtcMillis(meal.nextReminderAtUtc);
  if (dueAtMs !== undefined && nowMs >= dueAtMs) {
    return { kind: "due", dueAtMs };
  }
  return { kind: "upcoming", dueAtMs };
}

/** 扫码下班前是否要先问「今天吃饭休息了吗」：只在最后一次下班、且后端算出缺休息时问。 */
export function shouldConfirmMealBeforeClockOut(meal?: AttendanceMealState) {
  return Boolean(meal && meal.clockOutWouldBeFinal && meal.missingCountIfClockOutNow > 0);
}

export type AttendanceMealCheckAnswer = "taken" | "notTaken";

/** 员工的回答转成打卡请求里的声明；缺几次由后端在下班时重算，这里只表达「都休息了 / 都没休息」。 */
export function buildAttendanceMealDeclaration(
  answer: AttendanceMealCheckAnswer,
  missingCount: number,
): AttendanceMealDeclaration {
  return { notTakenCount: answer === "notTaken" ? Math.max(1, Math.trunc(missingCount)) : 0 };
}

/** 声明没休息、等待店长审核时，下班成功提示要告诉员工已提交审核。 */
export function isMealClaimPendingReview(claim?: AttendanceMealClaim) {
  return Boolean(claim && claim.status === "Pending" && claim.claimedMinutes > 0);
}

/** 缺 count 次用餐对应的加回分钟数。 */
export function mealAddBackMinutes(count: number) {
  return Math.max(0, Math.trunc(count)) * MEAL_BREAK_MINUTES;
}

/** 休息计时显示：不足 1 小时为 m:ss，超过为 h:mm:ss。 */
export function formatMealElapsed(totalSeconds: number) {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const rest = String(seconds % 60).padStart(2, "0");
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${rest}`
    : `${minutes}:${rest}`;
}
