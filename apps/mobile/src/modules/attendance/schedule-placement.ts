import { effectiveMealBreakCount, scheduleDurationMinutes } from "./attendance-my-week";
import { normalizeClockTime } from "./schedule-grid";
import type {
  AttendanceAvailability,
  AttendanceSchedule,
  AttendanceSchedulePayload,
  AttendanceScheduleUpdatePayload,
} from "./types";

/**
 * 店长排班「点选目标格」的纯逻辑（与 Web 周排班的拖拽同一套规则，手机上用点选代替拖动）：
 * 固定常用班次、落点判定（可放 / 提醒 / 不能放）、锁定（已有打卡不可移动）、各操作的保存载荷。
 */

export interface ShiftPreset {
  startTime: string;
  endTime: string;
}

/** 固定的常用班次（顺序即界面顺序，与 Web 一致）。 */
export const SHIFT_PRESETS: ShiftPreset[] = [
  { startTime: "09:00", endTime: "13:00" },
  { startTime: "16:00", endTime: "17:30" },
  { startTime: "09:00", endTime: "17:00" },
  { startTime: "09:00", endTime: "21:00" },
  { startTime: "09:00", endTime: "19:00" },
];

/** 新建班次的默认时段。 */
export const DEFAULT_SHIFT: ShiftPreset = { startTime: "09:00", endTime: "17:30" };

export type PlacementKind = "move" | "copy" | "preset";

/** 正在「拿起」的内容：已有班次（移动 / 复制）或常用班次（新建）。 */
export interface PlacementSource {
  kind: PlacementKind;
  /** 移动 / 复制时的原班次；常用班次添加时为空。 */
  schedule?: AttendanceSchedule;
  startTime: string;
  endTime: string;
}

export type AvailabilityConflict = "unavailable" | "outsideAvailable";

export type PlacementVerdict =
  | { state: "noop" }
  | { state: "ok" }
  /** 能放但要提醒：员工当天上报了不能上班 / 超出可上班时段。 */
  | { state: "warn"; reason: AvailabilityConflict }
  /** 不能放：与目标格已有班次时间重叠（后端同样会拒绝）。 */
  | { state: "bad"; reason: "overlap" };

const toMinutes = (value: string): number | undefined => {
  const normalized = normalizeClockTime(value);
  if (!normalized) return undefined;
  const [hour, minute] = normalized.split(":").map(Number);
  return hour * 60 + minute;
};

/** 两个时段是否重叠（首尾相接不算）；无法解析或结束不晚于开始的脏数据不判重叠。 */
export function rangesOverlap(aStart: string, aEnd: string, bStart: string, bEnd: string): boolean {
  const a0 = toMinutes(aStart);
  const a1 = toMinutes(aEnd);
  const b0 = toMinutes(bStart);
  const b1 = toMinutes(bEnd);
  if (a0 === undefined || a1 === undefined || b0 === undefined || b1 === undefined) return false;
  if (a1 <= a0 || b1 <= b0) return false;
  return a0 < b1 && b0 < a1;
}

/**
 * 班次与员工上报的冲突：
 * - 与任一「不能上班」时段重叠 → unavailable（优先）；
 * - 员工当天上报过「可上班」时段，但班次没有被某一段（相连的合并后）完整覆盖 → outsideAvailable；
 * - 没上报或只上报了不能上班：不判断（没上报不等于不能上班）。
 * 跨午夜（结束不晚于开始）的班次不判断。
 */
export function getAvailabilityConflict(
  shift: { startTime: string; endTime: string },
  availability: AttendanceAvailability[] | undefined,
): AvailabilityConflict | undefined {
  if (!availability?.length) return undefined;
  const start = toMinutes(shift.startTime);
  const end = toMinutes(shift.endTime);
  if (start === undefined || end === undefined || end <= start) return undefined;

  const segments = availability
    .filter((item) => (item.status ?? "").toLowerCase() !== "cancelled")
    .map((item) => ({
      from: toMinutes(item.startTime),
      to: toMinutes(item.endTime),
      unavailable: Boolean(item.isUnavailable),
    }))
    .filter((item): item is { from: number; to: number; unavailable: boolean } => (
      item.from !== undefined && item.to !== undefined && item.to > item.from
    ));

  if (segments.some((item) => item.unavailable && start < item.to && end > item.from)) return "unavailable";

  // 可上班时段先合并（重叠或首尾相接），再判断班次是否整段落在某一段里
  const available = segments
    .filter((item) => !item.unavailable)
    .sort((left, right) => left.from - right.from);
  if (!available.length) return undefined;
  const merged: { from: number; to: number }[] = [];
  available.forEach((item) => {
    const last = merged[merged.length - 1];
    if (last && item.from <= last.to) last.to = Math.max(last.to, item.to);
    else merged.push({ from: item.from, to: item.to });
  });
  return merged.some((item) => start >= item.from && end <= item.to) ? undefined : "outsideAvailable";
}

interface PlacementTarget {
  userGuid: string;
  /** YYYY-MM-DD */
  workDate: string;
  /** 目标格里已有的班次（已取消的会被忽略）。 */
  schedules: Pick<AttendanceSchedule, "scheduleGuid" | "startTime" | "endTime" | "status">[];
  availability?: AttendanceAvailability[];
}

/**
 * 某个格子对当前拿起内容的落点判定：
 * 移动且目标就是原格 → noop；与目标格别的未取消班次时间重叠 → bad（移动时忽略它自己）；
 * 与员工上报冲突 → warn（只提醒，不阻止）；否则 ok。
 */
export function getPlacementVerdict(source: PlacementSource, target: PlacementTarget): PlacementVerdict {
  const origin = source.schedule;
  if (
    source.kind === "move"
    && origin
    && origin.userGuid === target.userGuid
    && origin.workDate.slice(0, 10) === target.workDate.slice(0, 10)
  ) {
    return { state: "noop" };
  }

  const ignoreGuid = source.kind === "move" ? origin?.scheduleGuid : undefined;
  const overlapped = target.schedules.some((item) => (
    item.status.toLowerCase() !== "cancelled"
    && item.scheduleGuid !== ignoreGuid
    && rangesOverlap(source.startTime, source.endTime, item.startTime, item.endTime)
  ));
  if (overlapped) return { state: "bad", reason: "overlap" };

  const conflict = getAvailabilityConflict(source, target.availability);
  if (conflict) return { state: "warn", reason: conflict };
  return { state: "ok" };
}

/** 把本周打卡列表整理成「有打卡的排班 GUID 集合」。 */
export function collectPunchScheduleGuids(punches: { scheduleGuid?: string | null }[]): Set<string> {
  const guids = new Set<string>();
  punches.forEach((punch) => {
    if (punch.scheduleGuid) guids.add(punch.scheduleGuid);
  });
  return guids;
}

/**
 * 班次是否被锁定（不能移动或复制；「删除」仍可用，与 Web 的拖拽锁定一致）：
 * - 本周打卡里能查到这条班次的打卡记录 → 锁；
 * - 看不到打卡（无权限 / 拉取失败，无法确认）时，今天及以前的班次保守地锁住。
 * 已取消的班次不参与移动，由调用方另行判断。
 */
export function isScheduleLocked(input: {
  scheduleGuid: string;
  workDate: string;
  today: string;
  punchScheduleGuids: ReadonlySet<string>;
  punchesKnown: boolean;
}): boolean {
  if (input.punchScheduleGuids.has(input.scheduleGuid)) return true;
  return !input.punchesKnown && input.workDate.slice(0, 10) <= input.today;
}

const hm = (value: string) => normalizeClockTime(value) || value;

/** 同员工换日期：走「更新排班」，保持原状态、时间、备注；用餐字段不带（后端保持原值）。 */
export function buildMoveUpdatePayload(schedule: AttendanceSchedule, workDate: string): AttendanceScheduleUpdatePayload {
  return {
    workDate,
    startTime: hm(schedule.startTime),
    endTime: hm(schedule.endTime),
    status: schedule.status,
    remark: schedule.remark,
  };
}

/** 换员工：新建一条（备注和手动指定的用餐次数一并带过去），之后再删除原班次。 */
export function buildTransferCreatePayload(
  schedule: AttendanceSchedule,
  userGuid: string,
  workDate: string,
): AttendanceSchedulePayload {
  return {
    storeCode: schedule.storeCode,
    userGuid,
    workDate,
    startTime: hm(schedule.startTime),
    endTime: hm(schedule.endTime),
    status: schedule.status,
    remark: schedule.remark,
    mealBreakCount: typeof schedule.mealBreakCount === "number" ? schedule.mealBreakCount : undefined,
  };
}

/** 复制：新建草稿，备注和手动指定的用餐次数一并带过去，原班次不动。 */
export function buildCopyPayload(
  schedule: AttendanceSchedule,
  userGuid: string,
  workDate: string,
): AttendanceSchedulePayload {
  return { ...buildTransferCreatePayload(schedule, userGuid, workDate), status: "Draft" };
}

/** 常用班次添加：新建草稿，用餐默认自动。 */
export function buildPresetPayload(
  storeCode: string,
  userGuid: string,
  workDate: string,
  preset: ShiftPreset,
): AttendanceSchedulePayload {
  return { storeCode, userGuid, workDate, startTime: preset.startTime, endTime: preset.endTime, status: "Draft" };
}

/**
 * 排班格里的用餐小标记：一格可能有多个班次，按每个班次的「有效用餐次数」合计。
 * 有效次数 = 店长手动指定的（含 0）优先，否则按时长自动算（满 4.5 小时 1 次、超过 9 小时 2 次，每次 30 分钟）。
 * manual 表示其中有班次是手动指定的（界面上更醒目）；纯自动且合计为 0 的不显示，避免短班次满格噪音。
 * 已取消的班次和请假占位不参与。
 */
export function getCellMealBadge(
  schedules: Pick<AttendanceSchedule, "startTime" | "endTime" | "mealBreakCount" | "status" | "leaveType">[],
): { count: number; manual: boolean } | undefined {
  const worked = schedules.filter((item) => item.status.toLowerCase() !== "cancelled" && !item.leaveType);
  if (!worked.length) return undefined;
  const count = worked.reduce(
    (sum, item) => sum + effectiveMealBreakCount(item.mealBreakCount, scheduleDurationMinutes(item)),
    0,
  );
  const manual = worked.some((item) => typeof item.mealBreakCount === "number");
  return count > 0 || manual ? { count, manual } : undefined;
}
