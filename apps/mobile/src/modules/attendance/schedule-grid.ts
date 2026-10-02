import { schedulePaidMinutes } from "./attendance-my-week";
import type { AttendanceAvailability, AttendanceSchedule } from "./types";

/** 店长排班周网格单元格类型：有班 / 已批准请假 / 员工可上班 / 空。 */
export type ScheduleGridCellKind = "shift" | "leave" | "available" | "unavailable" | "empty";

export interface ScheduleGridCell {
  kind: ScheduleGridCellKind;
  /** 当天未取消的班次（含请假班次），按开始时间排序。 */
  schedules: AttendanceSchedule[];
  /** 当天未取消的可上班时间段，按开始时间排序。 */
  availability: AttendanceAvailability[];
}

export type SchedulePublishState = "draft" | "published" | "empty";

const isNotCancelled = (status: string) => status.toLowerCase() !== "cancelled";

/** 统一取 HH:mm：兼容 "09:00"、"09:00:00" 与带日期的 ISO 时间。 */
export function normalizeClockTime(value: string) {
  const time = value.includes("T") ? value.split("T").pop() ?? "" : value;
  const match = /^(\d{1,2}):(\d{2})/.exec(time);
  return match ? `${match[1].padStart(2, "0")}:${match[2]}` : "";
}

export function availabilityKey(userGuid: string, workDate: string) {
  return `${userGuid}|${workDate.slice(0, 10)}`;
}

/** 店长查看的全店可上班时间按「员工|日期」分组；已取消与缺员工标识的记录忽略。 */
export function groupAvailabilityByUserDate(availability: AttendanceAvailability[]) {
  const map = new Map<string, AttendanceAvailability[]>();
  availability.forEach((item) => {
    if (!item.userGuid || !isNotCancelled(item.status)) return;
    const key = availabilityKey(item.userGuid, item.workDate);
    map.set(key, [...(map.get(key) ?? []), item]);
  });
  map.forEach((items) => items.sort((left, right) => left.startTime.localeCompare(right.startTime)));
  return map;
}

/**
 * 单元格分类：请假优先（当天整天不上班、不计工时），其次是班次，
 * 都没有时员工填过可上班显示「可」，否则为空。已取消的班次视同不存在。
 */
export function classifyScheduleGridCell(
  schedules: AttendanceSchedule[],
  availability: AttendanceAvailability[] = [],
): ScheduleGridCell {
  const daySchedules = schedules
    .filter((item) => isNotCancelled(item.status))
    .sort((left, right) => left.startTime.localeCompare(right.startTime));
  const dayAvailability = availability.filter((item) => isNotCancelled(item.status));
  // 同一天有可上班时段就按可上班；只填了不能上班时才标不能上班。
  const hasAvailable = dayAvailability.some((item) => !item.isUnavailable);
  const kind: ScheduleGridCellKind = daySchedules.some((item) => item.leaveType)
    ? "leave"
    : daySchedules.length
      ? "shift"
      : hasAvailable
        ? "available"
        : dayAvailability.length ? "unavailable" : "empty";
  return { kind, schedules: daySchedules, availability: dayAvailability };
}

function shortClock(value: string) {
  const [hourText, minuteText] = normalizeClockTime(value).split(":");
  const hour = Number(hourText);
  if (!hourText || Number.isNaN(hour)) return "--";
  // 12 小时制去掉上下午与整点分钟，网格里只占很窄的宽度。
  const hour12 = hour % 12 === 0 ? 12 : hour % 12;
  return minuteText === "00" ? String(hour12) : `${hour12}:${minuteText}`;
}

/** 班次简写，如 09:00–17:30 → 9–5:30。 */
export function formatShiftShort(startTime: string, endTime: string) {
  return `${shortClock(startTime)}–${shortClock(endTime)}`;
}

/** 可上班全天约定为 00:00–23:59（秒数可有可无）。 */
export function isAllDayRange(startTime: string, endTime: string) {
  return normalizeClockTime(startTime) === "00:00" && normalizeClockTime(endTime) === "23:59";
}

/** 时间步进：按分钟增减并在一天内循环，结果为 HH:mm；无法解析时原样返回。 */
export function stepClockTime(value: string, deltaMinutes: number) {
  const normalized = normalizeClockTime(value);
  if (!normalized) return value;
  const [hour, minute] = normalized.split(":").map(Number);
  const total = (((hour * 60 + minute + deltaMinutes) % 1440) + 1440) % 1440;
  return `${String(Math.floor(total / 60)).padStart(2, "0")}:${String(total % 60).padStart(2, "0")}`;
}

/**
 * 编辑中班次的计薪时长（分钟，扣除用餐时间）；mealBreakCount 为空时按时长默认次数。
 * 开始等于结束视为 0，跨午夜按次日结束。
 */
export function shiftEditorMinutes(startTime: string, endTime: string, mealBreakCount?: number | null) {
  const start = normalizeClockTime(startTime);
  const end = normalizeClockTime(endTime);
  if (!start || !end || start === end) return 0;
  return schedulePaidMinutes({ startTime: start, endTime: end, mealBreakCount });
}

/** 空缺日：一周中没有任何人上班（不含请假、已取消）的日期数。 */
export function countUncoveredDays(weekDates: string[], schedules: AttendanceSchedule[]) {
  const covered = new Set(
    schedules
      .filter((item) => isNotCancelled(item.status) && !item.leaveType)
      .map((item) => item.workDate.slice(0, 10)),
  );
  return weekDates.filter((date) => !covered.has(date)).length;
}

/** 本周发布状态：存在草稿班次即「草稿·未发布」，否则有班次即「已发布」。 */
export function summarizeSchedulePublishState(schedules: AttendanceSchedule[]): SchedulePublishState {
  const active = schedules.filter((item) => isNotCancelled(item.status) && !item.leaveType);
  if (active.some((item) => item.status.toLowerCase() === "draft")) return "draft";
  return active.length ? "published" : "empty";
}

/** 当前登录用户以外的店长（GUID 忽略大小写比较）。 */
export function isOtherManager(
  user: { userGUID: string; isStoreManager?: boolean },
  currentUserGuid: string | undefined,
) {
  return Boolean(
    user.isStoreManager
    && user.userGUID.toLowerCase() !== (currentUserGuid ?? "").toLowerCase(),
  );
}

/**
 * 排班表员工过滤：店员与当前登录的店长自己默认显示；其他店长（无论是否管理本店）默认隐藏，打开筛选后显示。
 * 本周已有班次的员工始终显示，避免班次被藏起来而与周合计对不上。
 */
export function filterScheduleUsers<T extends { userGUID: string; isStoreManager?: boolean }>(
  users: T[],
  scheduledUserGuids: ReadonlySet<string>,
  showOtherManagers: boolean,
  currentUserGuid: string | undefined,
) {
  const otherManagerCount = users.filter((user) => isOtherManager(user, currentUserGuid)).length;
  const visible = showOtherManagers
    ? users
    : users.filter(
        (user) => !isOtherManager(user, currentUserGuid) || scheduledUserGuids.has(user.userGUID),
      );
  return { visible, otherManagerCount };
}

/**
 * 批量排班只给空格与仅有「可上班」标记的格子建班；已有班次、请假或员工标了不能上班的格子跳过，
 * 与「复制上周」的跳过口径一致（不能上班的格子仍可点开单独排班）。
 */
export function isBatchSchedulableCell(cell: Pick<ScheduleGridCell, "kind">) {
  return cell.kind === "empty" || cell.kind === "available";
}

/**
 * 批量排班依次提交：上一条完成才发下一条。后端按「员工+日期」加锁并校验重叠，并发提交会互相争锁；
 * 单条失败不中断其余，最后统一汇总成功数与失败明细。
 */
export async function runSequentialBatch<T>(items: readonly T[], run: (item: T) => Promise<unknown>) {
  let succeeded = 0;
  const failures: { item: T; error: unknown }[] = [];
  for (const item of items) {
    try {
      await run(item);
      succeeded += 1;
    } catch (error) {
      failures.push({ item, error });
    }
  }
  return { succeeded, failures };
}

export type EmploymentTypeCode = "F" | "P" | "C";

/** 用工类型缩写：全职 F、兼职 P、临时工 C；未知返回 undefined。 */
export function employmentTypeCode(type?: string): EmploymentTypeCode | undefined {
  const normalized = type?.replace(/[\s_-]/g, "").toLowerCase();
  if (normalized === "fulltime") return "F";
  if (normalized === "parttime") return "P";
  if (normalized === "casual" || normalized === "temporary") return "C";
  return undefined;
}
