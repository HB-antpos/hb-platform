// 店长审核列表（移动端）的纯逻辑：按工作日分组、本人申请判定、提交时间相对文案、
// 加班批准分钟选项、当天打卡时间轴几何。界面只消费这里的结果，便于用 tsx 单测覆盖。
import { toAttendanceDeviceLocalTime, toAttendanceUtcMillis } from "./attendance-device-time";
import type {
  AttendanceApproval,
  AttendancePunch,
  AttendanceScheduleSession,
} from "./types";

export type WorkDateTier = "weekday" | "saturday" | "sunday" | "publicHoliday";

export interface ApprovalDayGroup {
  /** YYYY-MM-DD；没有工作日的条目归到空字符串组，排在最后。 */
  workDate: string;
  tier: WorkDateTier;
  holidayName?: string;
  items: AttendanceApproval[];
}

const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})/;

function parseDateOnly(value?: string) {
  const match = value ? DATE_PATTERN.exec(value) : null;
  if (!match) return undefined;
  return new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
}

/** 0 = 周一 … 6 = 周日（与设计图「周一 10-19」一致）。 */
export function weekdayIndex(workDate: string) {
  const date = parseDateOnly(workDate);
  return date ? (date.getUTCDay() + 6) % 7 : -1;
}

/** 分组头的时薪档：公共假期优先，其次周六 / 周日。 */
export function resolveWorkDateTier(workDate: string, holidayName?: string): WorkDateTier {
  if (holidayName) return "publicHoliday";
  const index = weekdayIndex(workDate);
  if (index === 5) return "saturday";
  if (index === 6) return "sunday";
  return "weekday";
}

/**
 * 按工作日分组，最近的在上；组内按提交时间倒序（新提交的在上）。
 * 同组任一条带公假名就整组按公假时薪显示。
 */
export function groupApprovalsByWorkDate(items: AttendanceApproval[]): ApprovalDayGroup[] {
  const groups = new Map<string, AttendanceApproval[]>();
  for (const item of items) {
    const workDate = item.workDate?.slice(0, 10) ?? "";
    const key = DATE_PATTERN.test(workDate) ? workDate : "";
    groups.set(key, [...(groups.get(key) ?? []), item]);
  }
  return Array.from(groups.entries())
    .sort(([left], [right]) => {
      if (!left) return 1;
      if (!right) return -1;
      return right.localeCompare(left);
    })
    .map(([workDate, groupItems]) => {
      const holidayName = groupItems.find((item) => item.holidayName)?.holidayName;
      return {
        workDate,
        tier: workDate ? resolveWorkDateTier(workDate, holidayName) : "weekday",
        holidayName,
        items: [...groupItems].sort((left, right) =>
          (toAttendanceUtcMillis(right.submittedAt) ?? 0) - (toAttendanceUtcMillis(left.submittedAt) ?? 0)),
      };
    });
}

/** 本人申请：后端明确给了 isSelf 就以它为准，否则比对申请人与当前用户（不区分大小写）。 */
export function isSelfApproval(item: AttendanceApproval, currentUserGuid?: string) {
  if (item.isSelf !== undefined) return item.isSelf;
  const applicant = item.applicantUserGuid?.trim().toLowerCase();
  const current = currentUserGuid?.trim().toLowerCase();
  return Boolean(applicant && current && applicant === current);
}

export type RelativeSubmitted =
  | { kind: "justNow" }
  | { kind: "minutes"; count: number }
  | { kind: "hours"; count: number }
  | { kind: "yesterday"; time: string }
  | { kind: "date"; date: string; time: string };

/**
 * 卡片右上角的提交时间：1 分钟内「刚刚」、1 小时内「N 分钟前」、12 小时内「N 小时前」，
 * 昨天给「昨天 HH:mm」，更早给「MM-DD HH:mm」。后端 CreatedAt 是 UTC（可能不带 Z）。
 */
export function describeSubmittedAt(submittedAt: string | undefined, nowMs: number): RelativeSubmitted | undefined {
  const millis = toAttendanceUtcMillis(submittedAt);
  if (millis === undefined) return undefined;
  const elapsedMinutes = Math.max(0, Math.floor((nowMs - millis) / 60_000));
  if (elapsedMinutes < 1) return { kind: "justNow" };
  if (elapsedMinutes < 60) return { kind: "minutes", count: elapsedMinutes };
  if (elapsedMinutes < 12 * 60) return { kind: "hours", count: Math.floor(elapsedMinutes / 60) };
  const local = toAttendanceDeviceLocalTime(submittedAt) ?? "";
  const nowLocal = toAttendanceDeviceLocalTime(new Date(nowMs).toISOString()) ?? "";
  const time = local.slice(11, 16);
  const yesterday = shiftDateOnly(nowLocal.slice(0, 10), -1);
  if (local.slice(0, 10) === nowLocal.slice(0, 10)) {
    return { kind: "hours", count: Math.floor(elapsedMinutes / 60) };
  }
  if (local.slice(0, 10) === yesterday) return { kind: "yesterday", time };
  return { kind: "date", date: local.slice(5, 10), time };
}

export function shiftDateOnly(value: string, days: number) {
  const date = parseDateOnly(value);
  if (!date) return value;
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

/** 批准分钟只能选 15 的倍数，且不超过候选加班（向下取整到 15）。 */
export function listOvertimeMinuteOptions(candidateMinutes: number) {
  const max = Math.max(0, Math.floor(candidateMinutes / 15) * 15);
  return Array.from({ length: max / 15 + 1 }, (_, index) => index * 15);
}

export function defaultOvertimeMinutes(candidateMinutes: number) {
  return Math.max(0, Math.floor(candidateMinutes / 15) * 15);
}

/** 时长统一写成「4h 04m」；不足 1 小时写「45m」。 */
export function formatDurationMinutes(minutes: number) {
  const safe = Math.max(0, Math.round(minutes));
  const hours = Math.floor(safe / 60);
  const rest = safe % 60;
  return hours > 0 ? `${hours}h ${String(rest).padStart(2, "0")}m` : `${rest}m`;
}

// ───────────────────────── 时间轴 ─────────────────────────

/** 门店本地时间 YYYY-MM-DDTHH:mm 相对工作日 00:00 的分钟数（跨午夜可超过 1440）。 */
export function minutesFromWorkDay(localDateTime: string | undefined, workDate: string) {
  const match = localDateTime ? /^(\d{4}-\d{2}-\d{2})[T ](\d{2}):(\d{2})/.exec(localDateTime) : null;
  const work = parseDateOnly(workDate);
  const day = match ? parseDateOnly(match[1]) : undefined;
  if (!match || !work || !day) return undefined;
  const dayOffset = Math.round((day.getTime() - work.getTime()) / 86_400_000);
  return dayOffset * 1440 + Number(match[2]) * 60 + Number(match[3]);
}

/** HH:mm（排班时间）转分钟；结束早于开始视为跨午夜。 */
export function clockToMinutes(value?: string) {
  const match = value ? /(\d{1,2}):(\d{2})/.exec(value) : null;
  return match ? Number(match[1]) * 60 + Number(match[2]) : undefined;
}

export function formatClockMinutes(minutes: number) {
  const normalized = ((Math.round(minutes) % 1440) + 1440) % 1440;
  return `${String(Math.floor(normalized / 60)).padStart(2, "0")}:${String(normalized % 60).padStart(2, "0")}`;
}

/** 打卡的门店本地时间：优先 UTC 转换（与打卡记录页一致），没有再用本地字段。 */
export function punchLocalDateTime(punch?: AttendancePunch) {
  if (!punch) return undefined;
  const value = punch.punchTimeUtc
    ? toAttendanceDeviceLocalTime(punch.punchTimeUtc)
    : punch.punchTimeLocal ?? punch.effectivePunchTime;
  return value?.slice(0, 16);
}

export interface TimelineSegment {
  number: number;
  start: number;
  /** 没有下班卡时为空（进行中 / 漏下班）。 */
  end?: number;
  isAutoClockOut: boolean;
  durationMinutes?: number;
}

export interface TimelineGap {
  afterSegment: number;
  minutes: number;
  /** 段间空档满 30 分钟算 1 次用餐（规格第 3 节：不再重复扣）。 */
  countsAsMeal: boolean;
}

export interface ApprovalTimeline {
  schedule?: { start: number; end: number };
  segments: TimelineSegment[];
  gaps: TimelineGap[];
  /** 轴的可视范围（整点对齐）。 */
  range: { start: number; end: number };
  ticks: number[];
}

/**
 * 从班次（含打卡段）生成时间轴：排班灰条 + 实际蓝条；轴按 2 小时刻度对齐，
 * 覆盖排班与全部打卡，至少 4 小时宽。
 */
export function buildApprovalTimeline(
  session: Pick<AttendanceScheduleSession, "workDate" | "startTime" | "endTime" | "segments">,
  options: { overrideLastClockOut?: number; extendRangeTo?: number } = {},
): ApprovalTimeline {
  const workDate = session.workDate.slice(0, 10);
  const scheduleStart = clockToMinutes(session.startTime);
  let scheduleEnd = clockToMinutes(session.endTime);
  if (scheduleStart !== undefined && scheduleEnd !== undefined && scheduleEnd <= scheduleStart) {
    scheduleEnd += 1440;
  }
  const schedule = scheduleStart !== undefined && scheduleEnd !== undefined
    ? { start: scheduleStart, end: scheduleEnd }
    : undefined;

  const segments: TimelineSegment[] = session.segments
    .map((segment, index) => {
      const start = minutesFromWorkDay(punchLocalDateTime(segment.clockIn), workDate);
      const end = minutesFromWorkDay(punchLocalDateTime(segment.clockOut), workDate);
      return {
        number: segment.segmentNumber || index + 1,
        start: start ?? Number.NaN,
        end,
        isAutoClockOut: Boolean(segment.clockOut?.isSystemAutoClockOut),
        durationMinutes: start !== undefined && end !== undefined ? Math.max(0, end - start) : undefined,
      };
    })
    .filter((segment) => !Number.isNaN(segment.start));

  // 漏下班处理时，用店长填的时间替换最后一段的下班（自动下班段不计）。
  if (options.overrideLastClockOut !== undefined && segments.length) {
    const last = segments[segments.length - 1];
    last.end = Math.max(last.start, options.overrideLastClockOut);
    last.durationMinutes = last.end - last.start;
  }

  const gaps: TimelineGap[] = [];
  for (let index = 1; index < segments.length; index += 1) {
    const previousEnd = segments[index - 1].end;
    if (previousEnd === undefined) continue;
    const minutes = Math.max(0, segments[index].start - previousEnd);
    gaps.push({ afterSegment: segments[index - 1].number, minutes, countsAsMeal: minutes >= 30 });
  }

  const points = [
    ...(schedule ? [schedule.start, schedule.end] : []),
    ...segments.flatMap((segment) => [segment.start, ...(segment.end !== undefined ? [segment.end] : [])]),
    // 漏下班：轴要画到系统自动下班（00:00），红虚线那段才完整可见。
    ...(options.extendRangeTo !== undefined ? [options.extendRangeTo] : []),
  ];
  const min = points.length ? Math.min(...points) : 9 * 60;
  const max = points.length ? Math.max(...points) : 17 * 60;
  let start = Math.floor(min / 120) * 120;
  let end = Math.ceil(max / 120) * 120;
  if (end - start < 240) end = start + 240;
  if (start < 0) {
    end -= start;
    start = 0;
  }
  const ticks: number[] = [];
  for (let tick = start; tick <= end; tick += 120) ticks.push(tick);
  return { schedule, segments, gaps, range: { start, end }, ticks };
}

/** 把分钟换算成时间轴上的百分比位置（0–100）。 */
export function timelinePercent(minutes: number, range: { start: number; end: number }) {
  const span = Math.max(1, range.end - range.start);
  return Math.min(100, Math.max(0, ((minutes - range.start) / span) * 100));
}

/** 漏下班：上班时间与系统自动下班时间（最后一段）。 */
export function resolveMissingClockOutTimes(session: AttendanceScheduleSession) {
  const workDate = session.workDate.slice(0, 10);
  const last = session.segments[session.segments.length - 1];
  const clockIn = minutesFromWorkDay(punchLocalDateTime(last?.clockIn), workDate);
  const autoPunch = last?.clockOut?.isSystemAutoClockOut ? last.clockOut : undefined;
  return {
    clockIn,
    autoClockOutPunchGuid: autoPunch?.punchGuid || undefined,
    autoClockOut: autoPunch ? minutesFromWorkDay(punchLocalDateTime(autoPunch), workDate) : undefined,
  };
}

/** 年月日 + 分钟 → 本地 YYYY-MM-DDTHH:mm（补卡请求用）。 */
export function workDayMinutesToLocal(workDate: string, minutes: number) {
  const dayOffset = Math.floor(minutes / 1440);
  return `${shiftDateOnly(workDate.slice(0, 10), dayOffset)}T${formatClockMinutes(minutes)}`;
}
