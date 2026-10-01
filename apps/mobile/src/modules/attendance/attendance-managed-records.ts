import type {
  AttendancePunch,
  AttendancePunchType,
  AttendanceScheduleSession,
} from "./types";

/** 店长打卡记录列表里的异常分类；越靠前越需要先处理。 */
export type ManagedRecordIssue =
  | "missingClockOut"
  | "noPunch"
  | "late"
  | "earlyLeave"
  | "inProgress"
  | "notStarted"
  | "normal";

const ISSUE_PRIORITY: Record<ManagedRecordIssue, number> = {
  missingClockOut: 0,
  noPunch: 1,
  late: 2,
  earlyLeave: 3,
  inProgress: 4,
  notStarted: 5,
  normal: 6,
};

const EXCEPTION_ISSUES = new Set<ManagedRecordIssue>([
  "missingClockOut",
  "noPunch",
  "late",
  "earlyLeave",
]);

function parseDay(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (!match) return undefined;
  return Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])) / 86_400_000;
}

export function classifyManagedRecord(
  session: AttendanceScheduleSession,
  today: string,
): ManagedRecordIssue {
  if (session.hasMissingClockOut) return "missingClockOut";
  const punches = session.segments.flatMap((segment) => [segment.clockIn, segment.clockOut]);
  const hasAnyPunch = punches.some(Boolean);
  const workDay = parseDay(session.workDate);
  const todayDay = parseDay(today);
  const isPastDay = workDay !== undefined && todayDay !== undefined && workDay < todayDay;
  if (!hasAnyPunch) return isPastDay ? "noPunch" : "notStarted";
  if (punches.some((punch) => punch?.status === "Late")) return "late";
  if (punches.some((punch) => punch?.status === "EarlyLeave")) return "earlyLeave";
  if (session.hasOpenSegment) return isPastDay ? "missingClockOut" : "inProgress";
  return "normal";
}

export function isManagedRecordException(issue: ManagedRecordIssue) {
  return EXCEPTION_ISSUES.has(issue);
}

/** 异常优先，其次按班次开始时间，再按员工名，保证列表顺序稳定。 */
export function sortManagedRecords<T extends { session: AttendanceScheduleSession; issue: ManagedRecordIssue }>(
  rows: T[],
) {
  return [...rows].sort((left, right) =>
    ISSUE_PRIORITY[left.issue] - ISSUE_PRIORITY[right.issue]
    || left.session.startTime.localeCompare(right.session.startTime)
    || (left.session.employeeName ?? "").localeCompare(right.session.employeeName ?? "")
    || left.session.scheduleGuid.localeCompare(right.session.scheduleGuid));
}

/**
 * 与后端 IsWithinManagedAdjustmentWindow 同一规则：单周（周一起）工资周期，
 * 可改本周至今天；周一、周二是上周结算核对期，仍可改上周。仅用于界面提示，以服务端为准。
 */
export function isWithinManagedAdjustmentWindow(workDate: string, today: string) {
  const work = parseDay(workDate);
  const current = parseDay(today);
  if (work === undefined || current === undefined) return false;
  // 1970-01-01 是周四；换算成周一=0 的序号。
  const weekdayFromMonday = (((current + 3) % 7) + 7) % 7;
  const currentWeekStart = current - weekdayFromMonday;
  const earliest = weekdayFromMonday <= 1 ? currentWeekStart - 7 : currentWeekStart;
  return work >= earliest && work <= current;
}

/** 列出班次内可被修改的有效打卡（按时间顺序）。 */
export function listSessionPunches(session: AttendanceScheduleSession) {
  return session.segments.flatMap((segment) =>
    [segment.clockIn, segment.clockOut].filter((punch): punch is AttendancePunch => Boolean(punch)));
}

/** 补录时默认的打卡类型：缺下班就补下班，否则补上班。 */
export function resolveDefaultMissingPunchType(session: AttendanceScheduleSession): AttendancePunchType {
  const last = session.segments[session.segments.length - 1];
  return last?.clockIn && !last.clockOut ? "ClockOut" : "ClockIn";
}

/** 补录时默认时间：按缺失的类型取班次开始或结束时间（门店本地时间）。 */
export function resolveDefaultMissingPunchTime(
  session: AttendanceScheduleSession,
  punchType: AttendancePunchType,
) {
  const time = (punchType === "ClockOut" ? session.endTime : session.startTime).slice(0, 5) || "09:00";
  return `${session.workDate.slice(0, 10)}T${time}`;
}

/**
 * 按分钟平移本地补卡时间（YYYY-MM-DDTHH:mm），允许跨到次日以覆盖跨午夜班次，
 * 但限制在工作日 00:00 至次日 23:59 之间，避免误拨到无关日期。
 */
export function shiftLocalPunchTime(value: string, deltaMinutes: number, workDate: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value);
  const workDay = parseDay(workDate);
  if (!match || workDay === undefined) return value;
  const day = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])) / 86_400_000;
  const minutes = day * 1440 + Number(match[4]) * 60 + Number(match[5]) + deltaMinutes;
  const clamped = Math.min(Math.max(minutes, workDay * 1440), (workDay + 2) * 1440 - 1);
  const date = new Date(clamped * 60_000);
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`
    + `T${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}`;
}

/** 补卡时间相对工作日的偏移天数（跨午夜时为 1）。 */
export function resolveLocalPunchDayOffset(value: string, workDate: string) {
  const day = parseDay(value);
  const workDay = parseDay(workDate);
  return day === undefined || workDay === undefined ? 0 : day - workDay;
}

/** 快捷原因 + 补充说明拼成后端 reason；选「其他」时必须有说明。 */
export function buildAdjustmentReason(preset: string | undefined, note: string) {
  const trimmedNote = note.trim();
  const trimmedPreset = preset?.trim() ?? "";
  if (trimmedPreset && trimmedNote) return `${trimmedPreset}：${trimmedNote}`;
  return trimmedPreset || trimmedNote;
}
