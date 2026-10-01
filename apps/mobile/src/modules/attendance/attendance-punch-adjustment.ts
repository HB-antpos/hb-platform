import type {
  AttendanceAdjustmentPreview,
  AttendancePunchAdjustment,
  AttendancePunchAdjustmentPayload,
  AttendanceScheduleSession,
  AttendanceToday,
} from "./types";
import { normalizeAttendanceToday } from "./attendance-today-normalization";
import { toAttendancePunchTimeUtc } from "./attendance-device-time";

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

function asString(value: unknown, fallback = "") {
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return fallback;
}

function asOptionalString(value: unknown) {
  const normalized = asString(value).trim();
  return normalized || undefined;
}

function asNumber(value: unknown, fallback = 0) {
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
  }
  return fallback;
}

function asBoolean(value: unknown, fallback = false) {
  if (typeof value === "boolean") return value;
  if (typeof value === "string") {
    if (value.toLowerCase() === "true") return true;
    if (value.toLowerCase() === "false") return false;
  }
  return fallback;
}

function normalizeSession(value: unknown): AttendanceScheduleSession | undefined {
  if (!isRecord(value)) return undefined;
  return normalizeAttendanceToday({ schedules: [value] }).scheduleSessions[0];
}

export function canRequestAttendanceAdjustmentForDate(
  workDate: string,
  currentDate: string,
) {
  const parseDayNumber = (value: string) => {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value.slice(0, 10));
    if (!match) return undefined;
    const year = Number(match[1]);
    const month = Number(match[2]);
    const day = Number(match[3]);
    const timestamp = Date.UTC(year, month - 1, day);
    const parsed = new Date(timestamp);
    if (
      parsed.getUTCFullYear() !== year ||
      parsed.getUTCMonth() !== month - 1 ||
      parsed.getUTCDate() !== day
    ) return undefined;
    return timestamp / 86_400_000;
  };
  const work = parseDayNumber(workDate);
  const current = parseDayNumber(currentDate);
  if (work === undefined || current === undefined) return false;
  const differenceDays = current - work;
  return differenceDays >= 0 && differenceDays <= 2;
}

export function validateAttendancePunchAdjustment(
  payload: AttendancePunchAdjustmentPayload,
) {
  const missing: Array<"storeCode" | "requestedPunchTimeLocal" | "reason"> = [];
  if (!payload.storeCode.trim()) missing.push("storeCode");
  if (
    !payload.requestedPunchTimeLocal.trim()
    || !(payload.requestedPunchTimeUtc?.trim()
      || toAttendancePunchTimeUtc(payload.requestedPunchTimeLocal))
  ) missing.push("requestedPunchTimeLocal");
  if (!payload.reason.trim()) missing.push("reason");
  return missing;
}

export function buildAttendancePunchAdjustmentFingerprint(
  payload: AttendancePunchAdjustmentPayload,
) {
  return JSON.stringify({
    storeCode: payload.storeCode.trim(),
    scheduleGuid: payload.scheduleGuid ?? "",
    originalPunchGuid: payload.originalPunchGuid ?? "",
    punchType: payload.punchType,
    requestedPunchTimeLocal: payload.requestedPunchTimeLocal.trim(),
    requestedPunchTimeUtc:
      payload.requestedPunchTimeUtc?.trim()
      || toAttendancePunchTimeUtc(payload.requestedPunchTimeLocal),
    reason: payload.reason.trim(),
  });
}

export function buildAttendancePunchAdjustmentResetKey(
  selectedDate: string,
  storeCode: string | undefined,
  schedules: Array<Pick<AttendanceScheduleSession, "scheduleGuid">>,
) {
  const scheduleKey = schedules
    .map((schedule) => schedule.scheduleGuid.trim())
    .filter(Boolean)
    .sort()
    .join("|");
  return `${selectedDate}|${storeCode?.trim().toUpperCase() ?? ""}|${scheduleKey}`;
}

export function buildAttendancePunchAdjustmentPayload(input: {
  storeCode?: string;
  today?: AttendanceToday;
  scheduleGuid?: string;
  originalPunchGuid?: string;
  punchType: AttendancePunchAdjustmentPayload["punchType"];
  requestedPunchTimeLocal: string;
  reason: string;
}): AttendancePunchAdjustmentPayload {
  return {
    storeCode: input.storeCode ?? input.today?.scheduleSessions[0]?.storeCode ?? "",
    scheduleGuid: input.scheduleGuid,
    originalPunchGuid: input.originalPunchGuid,
    punchType: input.punchType,
    requestedPunchTimeLocal: input.requestedPunchTimeLocal,
    requestedPunchTimeUtc: toAttendancePunchTimeUtc(input.requestedPunchTimeLocal),
    reason: input.reason,
  };
}

export interface AttendanceAdjustmentRequestToken {
  id: number;
  fingerprint: string;
}

export function createAttendanceAdjustmentRequestGate() {
  let nextId = 0;
  let active: AttendanceAdjustmentRequestToken | undefined;
  return {
    begin(fingerprint: string) {
      active = { id: ++nextId, fingerprint };
      return active;
    },
    invalidate() {
      active = undefined;
      nextId += 1;
    },
    isCurrent(request: AttendanceAdjustmentRequestToken, currentFingerprint: string) {
      return active?.id === request.id
        && active.fingerprint === request.fingerprint
        && request.fingerprint === currentFingerprint;
    },
    finish(request: AttendanceAdjustmentRequestToken) {
      if (active?.id === request.id) active = undefined;
    },
  };
}

export async function runLatestAttendanceAdjustmentRequest<T>(options: {
  gate: ReturnType<typeof createAttendanceAdjustmentRequestGate>;
  request: AttendanceAdjustmentRequestToken;
  getCurrentFingerprint: () => string;
  operation: () => Promise<T>;
  onSuccess: (value: T) => void | Promise<void>;
  onError: (error: unknown) => void | Promise<void>;
}) {
  try {
    const result = await options.operation();
    if (options.gate.isCurrent(options.request, options.getCurrentFingerprint())) {
      await options.onSuccess(result);
    }
  } catch (error) {
    if (options.gate.isCurrent(options.request, options.getCurrentFingerprint())) {
      await options.onError(error);
    }
  } finally {
    options.gate.finish(options.request);
  }
}

export function normalizeAttendancePunchAdjustmentPreview(
  payload: unknown,
): AttendanceAdjustmentPreview {
  const raw = isRecord(payload) ? payload : {};
  return {
    isValid: asBoolean(pick(raw, "isValid", "IsValid")),
    validationErrorCode: asOptionalString(pick(raw, "validationErrorCode", "ValidationErrorCode")),
    validationMessage: asOptionalString(pick(raw, "validationMessage", "ValidationMessage")),
    existingSession: normalizeSession(pick(raw, "existingSession", "ExistingSession")),
    proposedSession: normalizeSession(pick(raw, "proposedSession", "ProposedSession")),
    workedMinutesDelta: asNumber(pick(raw, "workedMinutesDelta", "WorkedMinutesDelta")),
    candidateOvertimeMinutesDelta: asNumber(
      pick(raw, "candidateOvertimeMinutesDelta", "CandidateOvertimeMinutesDelta"),
    ),
    wouldAutoApprove: asBoolean(pick(raw, "wouldAutoApprove", "WouldAutoApprove")),
    previewRevision: asOptionalString(pick(raw, "previewRevision", "PreviewRevision")),
  };
}

export function normalizeAttendancePunchAdjustment(
  payload: unknown,
): AttendancePunchAdjustment {
  const raw = isRecord(payload) ? payload : {};
  return {
    adjustmentGuid: asString(pick(raw, "adjustmentGuid", "AdjustmentGuid", "guid", "Guid")),
    storeCode: asString(pick(raw, "storeCode", "StoreCode")),
    scheduleGuid: asOptionalString(pick(raw, "scheduleGuid", "ScheduleGuid")),
    originalPunchGuid: asOptionalString(pick(raw, "originalPunchGuid", "OriginalPunchGuid")),
    originalPunchTimeLocal: asOptionalString(pick(raw, "originalPunchTimeLocal", "OriginalPunchTimeLocal")),
    punchType: asString(pick(raw, "punchType", "PunchType"), "ClockIn") as AttendancePunchAdjustment["punchType"],
    requestedPunchTimeLocal: asString(pick(raw, "requestedPunchTimeLocal", "RequestedPunchTimeLocal")),
    requestedPunchTimeUtc: asOptionalString(pick(raw, "requestedPunchTimeUtc", "RequestedPunchTimeUtc")),
    effectivePunchTimeLocal: asOptionalString(pick(raw, "effectivePunchTimeLocal", "EffectivePunchTimeLocal")),
    reason: asString(pick(raw, "reason", "Reason")),
    status: asString(pick(raw, "status", "Status"), "Pending"),
    isDirectAdjustment: asBoolean(pick(raw, "isDirectAdjustment", "IsDirectAdjustment", "isManagerSelfDirect", "IsManagerSelfDirect")),
    submittedAt: asOptionalString(pick(raw, "submittedAt", "SubmittedAt", "createdAt", "CreatedAt")),
    reviewedAt: asOptionalString(pick(raw, "reviewedAt", "ReviewedAt", "appliedAt", "AppliedAt")),
    reviewedByName: asOptionalString(pick(raw, "reviewedByName", "ReviewedByName")),
    reviewRemark: asOptionalString(pick(raw, "reviewRemark", "ReviewRemark")),
  };
}
