import type {
  AttendanceAvailability,
  AttendanceLeaveRequest,
  AttendancePunchAdjustment,
  AttendanceSchedule,
  AttendanceWeek,
} from "./types";

function parseDay(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (!match) return undefined;
  return Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])) / 86_400_000;
}

function formatDay(day: number) {
  const date = new Date(day * 86_400_000);
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}-${String(date.getUTCDate()).padStart(2, "0")}`;
}

function minutesOf(time: string) {
  const match = /^(\d{1,2}):(\d{2})/.exec(time);
  return match ? Number(match[1]) * 60 + Number(match[2]) : undefined;
}

/** 周一起算的 7 个日期（YYYY-MM-DD）。 */
export function buildWeekDates(weekStartDate: string) {
  const start = parseDay(weekStartDate);
  if (start === undefined) return [];
  return Array.from({ length: 7 }, (_, index) => formatDay(start + index));
}

export function shiftWeekStart(weekStartDate: string, weeks: number) {
  const start = parseDay(weekStartDate);
  return start === undefined ? weekStartDate : formatDay(start + weeks * 7);
}

/** 班次时长（分钟），跨午夜按次日结束计算。 */
export function scheduleDurationMinutes(schedule: Pick<AttendanceSchedule, "startTime" | "endTime">) {
  const start = minutesOf(schedule.startTime);
  const end = minutesOf(schedule.endTime);
  if (start === undefined || end === undefined) return 0;
  return end > start ? end - start : end + 1440 - start;
}

export type MyWeekDayState = "scheduled" | "available" | "unfilled" | "rest";

export interface MyWeekRow {
  workDate: string;
  /** 0 = 周一 … 6 = 周日 */
  weekdayIndex: number;
  isToday: boolean;
  isPast: boolean;
  holidayName?: string;
  schedules: AttendanceSchedule[];
  availability: AttendanceAvailability[];
  state: MyWeekDayState;
}

/**
 * 员工「排班」页的一周行：排班与可上班时间按日期合并。
 * 有班显示班次；没班但已填可上班显示可上班；未来日期两者皆无则提示「未填」；过去日期为休息。
 */
export function buildMyWeekRows(
  weekStartDate: string,
  today: string,
  week?: AttendanceWeek,
  availability: AttendanceAvailability[] = [],
): MyWeekRow[] {
  const todayDay = parseDay(today);
  const activeAvailability = availability.filter((item) => item.status.toLowerCase() !== "cancelled");
  return buildWeekDates(weekStartDate).map((workDate, weekdayIndex) => {
    const day = week?.days.find((item) => item.workDate.slice(0, 10) === workDate);
    const schedules = (day?.schedules ?? [])
      .filter((item) => item.status.toLowerCase() !== "cancelled")
      .sort((left, right) => left.startTime.localeCompare(right.startTime));
    const dayAvailability = activeAvailability
      .filter((item) => item.workDate.slice(0, 10) === workDate)
      .sort((left, right) => left.startTime.localeCompare(right.startTime));
    const dayNumber = parseDay(workDate);
    const isPast = dayNumber !== undefined && todayDay !== undefined && dayNumber < todayDay;
    const state: MyWeekDayState = schedules.length
      ? "scheduled"
      : dayAvailability.length
        ? "available"
        : isPast ? "rest" : "unfilled";
    return {
      workDate,
      weekdayIndex,
      isToday: workDate === today,
      isPast,
      holidayName: day?.holidayName,
      schedules,
      availability: dayAvailability,
      state,
    };
  });
}

export function sumScheduledMinutes(rows: MyWeekRow[]) {
  return rows.reduce(
    (sum, row) => sum + row.schedules.reduce((inner, schedule) => inner + scheduleDurationMinutes(schedule), 0),
    0,
  );
}

/** 填写可上班时间时默认勾选：本周今天及以后、还没有排班也没填过的日期。 */
export function defaultAvailabilityDates(rows: MyWeekRow[]) {
  return rows.filter((row) => !row.isPast && row.state === "unfilled").map((row) => row.workDate);
}

export type MyRequestKind = "adjustment" | "leave";

export interface MyRequestItem {
  key: string;
  kind: MyRequestKind;
  status: string;
  /** 排序与「进行中」判定用：待审核在前，其余按提交/审核时间倒序。 */
  sortTime: string;
  adjustment?: AttendancePunchAdjustment;
  leave?: AttendanceLeaveRequest;
}

export function isPendingRequestStatus(status: string) {
  return status.toLowerCase() === "pending";
}

/** 合并本人补卡与请假申请：待审核在前，其余按时间倒序。 */
export function buildMyRequestItems(
  adjustments: AttendancePunchAdjustment[] = [],
  leaves: AttendanceLeaveRequest[] = [],
): MyRequestItem[] {
  const items: MyRequestItem[] = [
    ...adjustments.map((adjustment) => ({
      key: `adjustment:${adjustment.adjustmentGuid}`,
      kind: "adjustment" as const,
      status: adjustment.status,
      sortTime: adjustment.reviewedAt ?? adjustment.submittedAt ?? adjustment.requestedPunchTimeLocal,
      adjustment,
    })),
    ...leaves.map((leave) => ({
      key: `leave:${leave.leaveGuid}`,
      kind: "leave" as const,
      status: leave.status,
      sortTime: leave.reviewedAt ?? leave.submittedAt ?? leave.startDate,
      leave,
    })),
  ];
  return items.sort((left, right) =>
    Number(isPendingRequestStatus(right.status)) - Number(isPendingRequestStatus(left.status))
    || right.sortTime.localeCompare(left.sortTime)
    || left.key.localeCompare(right.key));
}

/** 请假天数（含首尾），日期无效时为 0。 */
export function leaveDayCount(startDate: string, endDate: string) {
  const start = parseDay(startDate);
  const end = parseDay(endDate);
  if (start === undefined || end === undefined || end < start) return 0;
  return end - start + 1;
}

export function shiftDate(value: string, days: number) {
  const day = parseDay(value);
  return day === undefined ? value : formatDay(day + days);
}
