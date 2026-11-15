// 店长排班「按天分组」看板（与 Web 周排班看板同一套规则）的纯逻辑：
// 一人一色、同一天的排序、每天人数 / 工时、员工图例、可上班未排名单、相对周数、员工 × 日期批量排班计划。
import { computeScheduleHourStats } from "./attendance-my-week";
import { availabilityKey, classifyScheduleGridCell, normalizeClockTime } from "./schedule-grid";
import { getPlacementVerdict } from "./schedule-placement";
import type { AttendanceAvailability, AttendanceSchedule } from "./types";

// ---------- 一人一色（与 Web staffColorLogic 完全相同：同一员工两端同色） ----------

export interface StaffColor {
  /** 卡片底色（浅）。 */
  bg: string;
  /** 左侧色条、图例圆点、边框（饱和）。 */
  bar: string;
  /** 文字色（深）。 */
  ink: string;
}

/** 红、橙不进色板：留给「冲突 / 不能上班 / 未成年提醒」这些状态色，避免和员工色混淆。 */
export const STAFF_PALETTE: readonly StaffColor[] = [
  { bg: "#e8f0ff", bar: "#3b82f6", ink: "#1d4ed8" },
  { bg: "#e3f6e9", bar: "#22a55a", ink: "#15803d" },
  { bg: "#fbe8fb", bar: "#c026d3", ink: "#a21caf" },
  { bg: "#eceff4", bar: "#64748b", ink: "#334155" },
  { bg: "#fdf6d3", bar: "#c9a000", ink: "#7a6200" },
  { bg: "#dcf5f2", bar: "#14b8a6", ink: "#0f766e" },
  { bg: "#eef8d9", bar: "#7cb518", ink: "#4d7c0f" },
  { bg: "#e9e9ff", bar: "#6366f1", ink: "#4338ca" },
  { bg: "#f5ece2", bar: "#b7791f", ink: "#92400e" },
  { bg: "#ffe7f0", bar: "#ec4899", ink: "#be185d" },
  { bg: "#def3fd", bar: "#0ea5e9", ink: "#0369a1" },
  { bg: "#f3f1dc", bar: "#9c8f2b", ink: "#6b6114" },
  { bg: "#f1eaff", bar: "#8b5cf6", ink: "#6d28d9" },
  { bg: "#e9f1fb", bar: "#3567b5", ink: "#1e3a8a" },
  { bg: "#eef6e8", bar: "#4f8a10", ink: "#2f5d0a" },
  { bg: "#f7e9f5", bar: "#9d3b8f", ink: "#6b1f61" },
];

/** 找不到员工（已离职但仍有历史班次）时用的中性灰。 */
export const FALLBACK_STAFF_COLOR: StaffColor = { bg: "#f2f3f5", bar: "#8c8c8c", ink: "#434343" };

/** FNV-1a 32 位哈希：同一个字符串永远得到同一个数，跨设备、跨周一致。 */
export function hashString(value: string): number {
  let hash = 0x811c9dc5;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash >>> 0;
}

export interface ColorSubject {
  userGuid: string;
  name?: string;
}

const collator = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });

/**
 * 给一批员工分配颜色：入参顺序不影响结果（按姓名、ID 排序后再分配）；
 * 哈希决定起点，撞色时顺延到下一种空闲颜色；人数超过色板时多出来的人复用颜色。
 */
export function assignStaffColors(subjects: readonly ColorSubject[]): Map<string, StaffColor> {
  const unique = new Map<string, ColorSubject>();
  subjects.forEach((item) => {
    if (item.userGuid && !unique.has(item.userGuid)) unique.set(item.userGuid, item);
  });
  const ordered = Array.from(unique.values()).sort(
    (left, right) => collator.compare(left.name ?? "", right.name ?? "") || left.userGuid.localeCompare(right.userGuid),
  );
  const size = STAFF_PALETTE.length;
  const used = new Set<number>();
  const result = new Map<string, StaffColor>();
  ordered.forEach((subject) => {
    let index = hashString(subject.userGuid) % size;
    if (used.size < size) {
      while (used.has(index)) index = (index + 1) % size;
    }
    used.add(index);
    result.set(subject.userGuid, STAFF_PALETTE[index]);
  });
  return result;
}

// ---------- 按天分组 ----------

const isCancelled = (item: Pick<AttendanceSchedule, "status">) => item.status.toLowerCase() === "cancelled";
const isWorking = (item: Pick<AttendanceSchedule, "status" | "leaveType">) => !isCancelled(item) && !item.leaveType;

/** 同一天排序：开始时间 → 员工名 → 结束时间 → 班次 ID（保证稳定）。请假卡（全天）排在最后。 */
export function compareDayShifts(
  left: AttendanceSchedule,
  right: AttendanceSchedule,
  nameOf: (userGuid: string) => string,
): number {
  const byLeave = Number(Boolean(left.leaveType)) - Number(Boolean(right.leaveType));
  if (byLeave) return byLeave;
  const byStart = normalizeClockTime(left.startTime).localeCompare(normalizeClockTime(right.startTime));
  if (byStart) return byStart;
  const byName = collator.compare(nameOf(left.userGuid), nameOf(right.userGuid));
  if (byName) return byName;
  const byEnd = normalizeClockTime(left.endTime).localeCompare(normalizeClockTime(right.endTime));
  if (byEnd) return byEnd;
  return left.scheduleGuid.localeCompare(right.scheduleGuid);
}

export interface BoardDay {
  date: string;
  /** 当天未取消的班次（含请假），已排好序。 */
  shifts: AttendanceSchedule[];
  /** 有班（不含请假）的人数，同一人多个班次算 1 人。 */
  people: number;
  /** 计薪分钟合计（扣用餐，不含请假）。 */
  paidMinutes: number;
}

/** 整周按天分组；已取消的班次不显示；不在本周的日期（脏数据）丢弃。 */
export function groupScheduleBoardDays(
  weekDates: readonly string[],
  schedules: readonly AttendanceSchedule[],
  nameOf: (userGuid: string) => string,
): BoardDay[] {
  const byDate = new Map<string, AttendanceSchedule[]>(weekDates.map((date) => [date, []]));
  schedules.forEach((item) => {
    if (isCancelled(item)) return;
    byDate.get(item.workDate.slice(0, 10))?.push(item);
  });
  return weekDates.map((date) => {
    const shifts = [...(byDate.get(date) ?? [])].sort((left, right) => compareDayShifts(left, right, nameOf));
    const working = shifts.filter(isWorking);
    return {
      date,
      shifts,
      people: new Set(working.map((item) => item.userGuid)).size,
      paidMinutes: computeScheduleHourStats(working).totalMinutes,
    };
  });
}

export interface StaffLegendItem {
  userGuid: string;
  name: string;
  /** 本周计薪分钟（不含请假、已取消）。 */
  paidMinutes: number;
  /** 本周班次数（不含请假、已取消）。 */
  shifts: number;
}

/** 员工图例：本周有班的人，按姓名排序。 */
export function buildStaffLegend(
  schedules: readonly AttendanceSchedule[],
  nameOf: (userGuid: string) => string,
): StaffLegendItem[] {
  const byUser = new Map<string, AttendanceSchedule[]>();
  schedules.filter(isWorking).forEach((item) => {
    byUser.set(item.userGuid, [...(byUser.get(item.userGuid) ?? []), item]);
  });
  return Array.from(byUser.entries())
    .map(([userGuid, list]) => ({
      userGuid,
      name: nameOf(userGuid),
      paidMinutes: computeScheduleHourStats(list).totalMinutes,
      shifts: list.length,
    }))
    .sort((left, right) => collator.compare(left.name, right.name) || left.userGuid.localeCompare(right.userGuid));
}

/**
 * 某天「可上班未排」：员工报了可上班（不是「不能上班」）、当天又没有班次 / 请假的人。
 * 只在列表里的员工中找（隐藏的其他店长不算）。
 */
export function findAvailableUnscheduled(
  date: string,
  userGuids: readonly string[],
  schedules: readonly AttendanceSchedule[],
  availabilityMap: ReadonlyMap<string, AttendanceAvailability[]>,
): string[] {
  const busy = new Set(
    schedules.filter((item) => !isCancelled(item) && item.workDate.slice(0, 10) === date).map((item) => item.userGuid),
  );
  return userGuids.filter((userGuid) => {
    if (busy.has(userGuid)) return false;
    const items = availabilityMap.get(availabilityKey(userGuid, date)) ?? [];
    return items.some((item) => !isCancelled(item) && !item.isUnavailable);
  });
}

// ---------- 周切换 ----------

const DAY_MS = 24 * 60 * 60 * 1000;

/** 选中周相对本周差几周（两边都传周一 YYYY-MM-DD）：0＝本周，1＝下 1 周，-2＝上 2 周。按 UTC 日期算，避开夏令时。 */
export function getRelativeWeekOffset(weekStartDate: string, currentWeekStartDate: string): number {
  const selected = Date.parse(`${weekStartDate.slice(0, 10)}T00:00:00Z`);
  const current = Date.parse(`${currentWeekStartDate.slice(0, 10)}T00:00:00Z`);
  if (Number.isNaN(selected) || Number.isNaN(current)) return 0;
  return Math.round((selected - current) / (7 * DAY_MS));
}

// ---------- 员工 × 日期 批量排班 ----------

/**
 * 每一格的处理：
 * - new：直接新建；
 * - warn：能建但要提醒（超出员工上报的可上班时段），默认仍建；
 * - skip：不建——当天已有班次 / 请假，或员工报了不能上班（与单人批量「只排空格 / 可上班格」一致）。
 * 用户可以点格子把 new / warn 单独排除（excluded）。
 */
export type BatchCellState = "new" | "warn" | "skip";
export type BatchSkipReason = "existing" | "leave" | "unavailable";

export interface BatchMatrixCell {
  key: string;
  userGuid: string;
  date: string;
  state: BatchCellState;
  reason?: BatchSkipReason;
  excluded: boolean;
}

export const batchCellKey = (userGuid: string, date: string) => `${userGuid}|${date}`;

export function buildBatchMatrix(input: {
  userGuids: readonly string[];
  dates: readonly string[];
  startTime: string;
  endTime: string;
  schedules: readonly AttendanceSchedule[];
  availabilityMap: ReadonlyMap<string, AttendanceAvailability[]>;
  excludedKeys?: ReadonlySet<string>;
}): BatchMatrixCell[] {
  const cells: BatchMatrixCell[] = [];
  input.userGuids.forEach((userGuid) => {
    input.dates.forEach((date) => {
      const key = batchCellKey(userGuid, date);
      const daySchedules = input.schedules.filter(
        (item) => item.userGuid === userGuid && item.workDate.slice(0, 10) === date,
      );
      const dayAvailability = input.availabilityMap.get(availabilityKey(userGuid, date)) ?? [];
      const cell = classifyScheduleGridCell([...daySchedules], dayAvailability);
      let state: BatchCellState = "new";
      let reason: BatchSkipReason | undefined;
      if (cell.kind === "leave") {
        state = "skip";
        reason = "leave";
      } else if (cell.kind === "shift") {
        state = "skip";
        reason = "existing";
      } else if (cell.kind === "unavailable") {
        state = "skip";
        reason = "unavailable";
      } else {
        const verdict = getPlacementVerdict(
          { kind: "preset", startTime: input.startTime, endTime: input.endTime },
          { userGuid, workDate: date, schedules: daySchedules, availability: dayAvailability },
        );
        if (verdict.state === "warn") state = "warn";
      }
      cells.push({
        key,
        userGuid,
        date,
        state,
        reason,
        excluded: state !== "skip" && Boolean(input.excludedKeys?.has(key)),
      });
    });
  });
  return cells;
}

export interface BatchMatrixSummary {
  total: number;
  /** 要新建的格数（new + warn，不含排除）。 */
  create: number;
  warn: number;
  skip: number;
  excluded: number;
}

export function summarizeBatchMatrix(cells: readonly BatchMatrixCell[]): BatchMatrixSummary {
  const live = cells.filter((cell) => cell.state !== "skip" && !cell.excluded);
  return {
    total: cells.length,
    create: live.length,
    warn: live.filter((cell) => cell.state === "warn").length,
    skip: cells.filter((cell) => cell.state === "skip").length,
    excluded: cells.filter((cell) => cell.excluded).length,
  };
}

/** 真正要提交的格子（顺序：按员工、再按日期，与矩阵显示一致）。 */
export function batchMatrixTargets(cells: readonly BatchMatrixCell[]): { userGuid: string; date: string }[] {
  return cells
    .filter((cell) => cell.state !== "skip" && !cell.excluded)
    .map((cell) => ({ userGuid: cell.userGuid, date: cell.date }));
}
