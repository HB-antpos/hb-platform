// 打卡记录一行要显示的文字与时间条几何：状态标签（带迟到 / 早退分钟）、副行说明、右侧工时，
// 以及这一班在共用时间轴上的排班带、实际在岗段、迟到 / 缺卡标记。列表与明细共用。
import {
  buildApprovalTimeline,
  clockToMinutes,
  formatClockMinutes,
  formatDurationMinutes,
} from "@/modules/attendance/approval-board";
import { resolveAttendancePunchDisplayTime } from "@/modules/attendance/attendance-device-time";
import type { ManagedRecordIssue } from "@/modules/attendance/attendance-managed-records";
import type { AttendanceScheduleSession } from "@/modules/attendance/types";
import type { AttendanceStatusTone } from "../AdjustmentFormControls";

type Translate = (key: string, options?: Record<string, unknown>) => string;

export const ISSUE_TONES: Record<ManagedRecordIssue, AttendanceStatusTone> = {
  onLeave: "neutral",
  missingClockOut: "danger",
  noPunch: "danger",
  late: "danger",
  earlyLeave: "warning",
  inProgress: "accent",
  notStarted: "neutral",
  normal: "success",
};

export function clockText(value?: string) {
  if (!value) return undefined;
  const timePart = value.includes("T") ? value.split("T").pop() : value;
  return timePart?.slice(0, 5) || undefined;
}

function scheduleWindow(session: AttendanceScheduleSession) {
  const start = clockToMinutes(session.startTime);
  let end = clockToMinutes(session.endTime);
  if (start === undefined || end === undefined) return undefined;
  if (end <= start) end += 1440;
  return { start, end };
}

/** 状态标签：迟到 / 早退带分钟数（后端给了分钟才带）。 */
export function issueLabel(session: AttendanceScheduleSession, issue: ManagedRecordIssue, t: Translate) {
  const punches = session.segments.flatMap((segment) => [segment.clockIn, segment.clockOut]);
  if (issue === "late") {
    const minutes = punches.find((punch) => punch?.status === "Late")?.lateMinutes;
    if (minutes) return t("managedRecords.lateBy", { minutes });
  }
  if (issue === "earlyLeave") {
    const minutes = punches.find((punch) => punch?.status === "EarlyLeave")?.earlyLeaveMinutes;
    if (minutes) return t("managedRecords.earlyBy", { minutes });
  }
  return t(`managedRecords.issues.${issue}`);
}

/** 名字下面那行：几点上班 / 下班，没打卡时写排班与已过多久。 */
export function rowMeta(
  session: AttendanceScheduleSession,
  issue: ManagedRecordIssue,
  t: Translate,
  options: { isToday: boolean; nowMinutes: number },
) {
  const window = scheduleWindow(session);
  const schedule = t("managedRecords.meta.schedule", {
    start: clockText(session.startTime) ?? "--:--",
    end: clockText(session.endTime) ?? "--:--",
  });
  const first = session.segments[0];
  const last = session.segments[session.segments.length - 1];
  const clockIn = clockText(resolveAttendancePunchDisplayTime(first?.clockIn));
  const clockOut = last?.clockOut && !last.clockOut.isSystemAutoClockOut
    ? clockText(resolveAttendancePunchDisplayTime(last.clockOut))
    : undefined;
  if (issue === "noPunch") {
    if (options.isToday && window) {
      return `${schedule} · ${t("managedRecords.meta.shiftPassed", { duration: formatDurationMinutes(options.nowMinutes - window.end) })}`;
    }
    return `${schedule} · ${t("managedRecords.meta.noPunchAllDay")}`;
  }
  if (issue === "notStarted" || issue === "onLeave" || !clockIn) return schedule;
  const parts = [t("managedRecords.meta.clockIn", { time: clockIn })];
  if (issue === "missingClockOut") {
    parts.push(last?.clockOut?.isSystemAutoClockOut ? t("managedRecords.meta.autoClockOut") : t("managedRecords.meta.noClockOut"));
  } else if (clockOut) {
    parts.push(t("managedRecords.meta.clockOut", { time: clockOut }));
  }
  if (session.segments.length > 1) parts.push(t("managedRecords.meta.segments", { count: session.segments.length }));
  return parts.join(" · ");
}

/** 右侧：已工作时长 + 小字（工作中 / 已扣用餐）。没打卡的不显示。 */
export function rowHours(session: AttendanceScheduleSession, issue: ManagedRecordIssue, t: Translate) {
  const worked = session.workedMinutes
    ?? session.segments.reduce((sum, segment) => sum + (segment.workedMinutes ?? segment.durationMinutes ?? 0), 0);
  if (!worked || issue === "noPunch" || issue === "notStarted" || issue === "onLeave") return undefined;
  const sub = issue === "inProgress" || (session.hasOpenSegment && issue !== "missingClockOut")
    ? t("managedRecords.working")
    : session.mealDeductionMinutes
      ? t("managedRecords.mealDeducted", { minutes: session.mealDeductionMinutes })
      : undefined;
  return { value: formatDurationMinutes(worked), sub };
}

export interface RowBar {
  key: string;
  start: number;
  end: number;
  kind: "schedule" | "actual" | "open" | "late" | "missing" | "leave";
}

/**
 * 一班在共用时间轴上的色块：排班（浅蓝）、实际在岗（深蓝）、今天还在岗的段画到「现在」（条纹）、
 * 迟到（排班开始到首次上班，红）、缺卡 / 漏下班（红色虚线）、请假（灰色斜纹）。
 */
export function rowBars(
  session: AttendanceScheduleSession,
  issue: ManagedRecordIssue,
  options: { isToday: boolean; nowMinutes: number },
): RowBar[] {
  const window = scheduleWindow(session);
  const bars: RowBar[] = [];
  if (issue === "onLeave") {
    return window ? [{ key: "leave", start: window.start, end: window.end, kind: "leave" }] : [];
  }
  if (window) bars.push({ key: "schedule", start: window.start, end: window.end, kind: "schedule" });
  if (issue === "noPunch" && window) {
    const end = options.isToday ? Math.min(window.end, options.nowMinutes) : window.end;
    bars.push({ key: "missing", start: window.start, end, kind: "missing" });
    return bars;
  }
  const timeline = buildApprovalTimeline(session);
  timeline.segments.forEach((segment, index) => {
    if (segment.end !== undefined && !segment.isAutoClockOut) {
      bars.push({ key: `seg-${index}`, start: segment.start, end: segment.end, kind: "actual" });
    } else if (options.isToday && issue !== "missingClockOut") {
      bars.push({ key: `seg-${index}`, start: segment.start, end: Math.max(segment.start, options.nowMinutes), kind: "open" });
    } else {
      // 漏下班：上班到排班结束算实际，之后画红色虚线到当天结束（系统自动下班，不计薪）。
      const shiftEnd = window ? Math.max(segment.start, window.end) : segment.start + 60;
      bars.push({ key: `seg-${index}`, start: segment.start, end: shiftEnd, kind: "actual" });
      bars.push({ key: `miss-${index}`, start: shiftEnd, end: Math.max(shiftEnd, (segment.end ?? 1440)), kind: "missing" });
    }
  });
  if (issue === "late" && window && timeline.segments[0] && timeline.segments[0].start > window.start) {
    bars.push({ key: "late", start: window.start, end: timeline.segments[0].start, kind: "late" });
  }
  return bars;
}

/** 列表时间轴要覆盖的分钟点：所有排班起止与打卡段。 */
export function sessionAxisPoints(session: AttendanceScheduleSession) {
  const window = scheduleWindow(session);
  const timeline = buildApprovalTimeline(session);
  return [
    ...(window ? [window.start, window.end] : []),
    ...timeline.segments.flatMap((segment) => [segment.start, ...(segment.end !== undefined && !segment.isAutoClockOut ? [segment.end] : [])]),
  ];
}

export { formatClockMinutes };
