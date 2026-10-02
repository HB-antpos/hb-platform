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

/** 每次用餐时长：30 分钟。 */
export const MEAL_BREAK_MINUTES = 30;
/** 店长可指定的用餐次数上限（与后端校验一致）。 */
export const MAX_MEAL_BREAK_COUNT = 3;

/**
 * 按班次时长推算的默认用餐次数：超过 9 小时 2 次、超过 4.5 小时 1 次，否则 0 次。
 * 门槛均为「超过」，正好 4.5 / 9 小时按低一档。
 */
export function defaultMealBreakCount(durationMinutes: number) {
  if (durationMinutes > 9 * 60) return 2;
  if (durationMinutes > 4.5 * 60) return 1;
  return 0;
}

/** 实际扣除的用餐次数：店长在排班上指定过（含 0 次＝取消）就用指定值，否则按时长默认。 */
export function effectiveMealBreakCount(mealBreakCount: number | null | undefined, durationMinutes: number) {
  return mealBreakCount ?? defaultMealBreakCount(durationMinutes);
}

/**
 * 班次计薪工时（分钟）：总时长减去用餐次数 × 30 分钟，最少为 0。
 * 所有排班工时展示（编辑弹层、每人/全店周合计、我的排班）都走这里，保证同一口径。
 */
export function schedulePaidMinutes(
  schedule: Pick<AttendanceSchedule, "startTime" | "endTime" | "mealBreakCount">,
) {
  const minutes = scheduleDurationMinutes(schedule);
  const meals = effectiveMealBreakCount(schedule.mealBreakCount, minutes);
  return Math.max(0, minutes - meals * MEAL_BREAK_MINUTES);
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

export interface ScheduleHourStats {
  totalMinutes: number;
  weekdayMinutes: number;
  weekendMinutes: number;
  saturdayMinutes: number;
  sundayMinutes: number;
  /** 等效工时：工作日 ×1、周六 ×1.25、周日 ×1.5。 */
  equivalentMinutes: number;
}

/** 等效工时倍率：周六 1.25、周日 1.5，工作日 1。 */
export const SATURDAY_HOUR_RATE = 1.25;
export const SUNDAY_HOUR_RATE = 1.5;

/**
 * 排班工时统计：周合计、工作日（周一至五）、周末（周六日），以及按周六 ×1.25、周日 ×1.5 加权的等效工时。
 * 已取消的班次与已批准请假的班次不计入；按计薪工时累计（扣除用餐时间）。
 */
export function computeScheduleHourStats(
  schedules: Pick<AttendanceSchedule, "workDate" | "startTime" | "endTime" | "status" | "leaveType" | "mealBreakCount">[],
): ScheduleHourStats {
  return schedules.reduce<ScheduleHourStats>((stats, schedule) => {
    if (schedule.status.toLowerCase() === "cancelled" || schedule.leaveType) return stats;
    const day = parseDay(schedule.workDate);
    if (day === undefined) return stats;
    const minutes = schedulePaidMinutes(schedule);
    // 1970-01-01 是周四；换算成周一=0 的序号，5 为周六、6 为周日。
    const weekdayIndex = (((day + 3) % 7) + 7) % 7;
    const isSaturday = weekdayIndex === 5;
    const isSunday = weekdayIndex === 6;
    const rate = isSunday ? SUNDAY_HOUR_RATE : isSaturday ? SATURDAY_HOUR_RATE : 1;
    return {
      totalMinutes: stats.totalMinutes + minutes,
      weekdayMinutes: stats.weekdayMinutes + (isSaturday || isSunday ? 0 : minutes),
      weekendMinutes: stats.weekendMinutes + (isSaturday || isSunday ? minutes : 0),
      saturdayMinutes: stats.saturdayMinutes + (isSaturday ? minutes : 0),
      sundayMinutes: stats.sundayMinutes + (isSunday ? minutes : 0),
      equivalentMinutes: stats.equivalentMinutes + minutes * rate,
    };
  }, {
    totalMinutes: 0,
    weekdayMinutes: 0,
    weekendMinutes: 0,
    saturdayMinutes: 0,
    sundayMinutes: 0,
    equivalentMinutes: 0,
  });
}

/** 分钟转小时文本：整数不带小数，否则保留 1 位。 */
export function formatScheduleHours(minutes: number) {
  const hours = minutes / 60;
  return Number.isInteger(hours) ? String(hours) : hours.toFixed(1);
}

export function sumScheduledMinutes(rows: MyWeekRow[]) {
  return rows.reduce(
    (sum, row) => sum + row.schedules.reduce((inner, schedule) => inner + schedulePaidMinutes(schedule), 0),
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
