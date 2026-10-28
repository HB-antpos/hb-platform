import type { AttendanceSchedule } from "./types";

/**
 * 店长排班「长按拖动」的纯逻辑（与 Web 周排班的拖放同一套落点规则，判定复用 schedule-placement）：
 * 手指坐标 → 命中哪个格子、靠近边缘时的自动横向滚动量、哪个格子可以被拿起。
 * 手势与渲染在 ScheduleManagementCard，这里不依赖 React Native，便于单测。
 */

/** 日期区内容（含表头）在屏幕上的位置与格子尺寸。 */
export interface DragGridGeometry {
  /** 日期区内容左上角的屏幕 X（已计入横向滚动偏移）。 */
  originX: number;
  /** 日期区内容左上角的屏幕 Y（表头顶部）。 */
  originY: number;
  cellWidth: number;
  headerHeight: number;
  cellHeight: number;
  rowCount: number;
  dayCount: number;
}

export interface DragCellIndex {
  rowIndex: number;
  dayIndex: number;
}

/** 手指所在的格子；落在表头、姓名列或表格外返回 undefined。 */
export function hitTestScheduleCell(
  point: { x: number; y: number },
  geometry: DragGridGeometry,
): DragCellIndex | undefined {
  const { originX, originY, cellWidth, headerHeight, cellHeight, rowCount, dayCount } = geometry;
  if (cellWidth <= 0 || cellHeight <= 0) return undefined;
  const dx = point.x - originX;
  const dy = point.y - originY - headerHeight;
  if (dx < 0 || dy < 0) return undefined;
  const dayIndex = Math.floor(dx / cellWidth);
  const rowIndex = Math.floor(dy / cellHeight);
  if (dayIndex >= dayCount || rowIndex >= rowCount) return undefined;
  return { rowIndex, dayIndex };
}

export function isSameDragCell(left?: DragCellIndex | null, right?: DragCellIndex | null): boolean {
  if (!left || !right) return !left && !right;
  return left.rowIndex === right.rowIndex && left.dayIndex === right.dayIndex;
}

/**
 * 手指靠近日期区可视范围左 / 右边缘时的每帧滚动量（负数向左、正数向右、0 不滚）。
 * 越靠近边缘（含越过边缘）滚得越快，最大 maxStep。
 */
export function getEdgeScrollStep(input: {
  pointX: number;
  viewportLeft: number;
  viewportRight: number;
  edge?: number;
  maxStep?: number;
}): number {
  const { pointX, viewportLeft, viewportRight, edge = 44, maxStep = 16 } = input;
  if (viewportRight - viewportLeft <= edge * 2) return 0;
  if (pointX < viewportLeft + edge) {
    const depth = Math.min(1, (viewportLeft + edge - pointX) / edge);
    return -Math.max(1, Math.round(depth * maxStep));
  }
  if (pointX > viewportRight - edge) {
    const depth = Math.min(1, (pointX - (viewportRight - edge)) / edge);
    return Math.max(1, Math.round(depth * maxStep));
  }
  return 0;
}

/** 把滚动位置限制在 [0, 内容宽 - 可视宽]。 */
export function clampScrollOffset(offset: number, contentWidth: number, viewportWidth: number): number {
  const max = Math.max(0, contentWidth - viewportWidth);
  return Math.min(max, Math.max(0, offset));
}

export type DragStartVerdict =
  | { state: "ok"; schedule: AttendanceSchedule }
  /** 已有打卡（或无法确认且是今天及以前）：不能拖，仍可点开编辑器删除。 */
  | { state: "locked" }
  /** 格子里没有可拖的班次（空格 / 请假 / 可上班标记）或有多个班次（走编辑器的「移到…」）。 */
  | { state: "none" };

/**
 * 长按某个格子时能不能拿起班次：只有「恰好一条、非请假」的班次格可以拖；
 * 一格多班次无法确定拖哪一条，仍走编辑器里的「移到… / 复制到…」。
 */
export function getDragStartVerdict(
  cell: { kind: string; schedules: AttendanceSchedule[] },
  isLocked: (schedule: AttendanceSchedule) => boolean,
): DragStartVerdict {
  if (cell.kind !== "shift" || cell.schedules.length !== 1) return { state: "none" };
  const [schedule] = cell.schedules;
  if (schedule.leaveType) return { state: "none" };
  return isLocked(schedule) ? { state: "locked" } : { state: "ok", schedule };
}
