// 语言悬浮按钮的位置与手势判定（纯函数，便于单测）。
// 坐标系：悬浮层容器左上角为原点，容器 = 壳布局里装 Stack 的那块区域（在底部导航栏上方）。

/** 按钮直径（dp）。 */
export const FLOATING_BUTTON_SIZE = 46;
/** 吸边时伸出屏幕外的宽度：只露出约 36dp，少挡内容。 */
export const FLOATING_EDGE_TUCK = 9;
/** 拖动时离左右边缘至少留的距离，避开 iOS 左缘返回手势与安卓手势导航边缘。 */
export const FLOATING_DRAG_EDGE_MARGIN = 16;
/** 纵向离顶部（状态栏之下）与底部（底部导航栏之上）至少留的距离。 */
export const FLOATING_TOP_MARGIN = 8;
export const FLOATING_BOTTOM_MARGIN = 12;
/** 手指移动超过这个距离才算拖动（也会取消长按计时）。 */
export const FLOATING_MOVE_THRESHOLD = 8;
/** 按住多久打开快速跳转：默认 2 秒，用户可在设置页用滑轨调整（范围 1~3 秒）。 */
export const FLOATING_LONG_PRESS_MS = 2000;
/**
 * 滑轨范围与步长（毫秒）。下限 1 秒：「点一下」的判定上限是 0.5 秒，
 * 按住时长不能压到它附近，否则点一下和长按会混在一起；上限 3 秒由用户指定。
 */
export const FLOATING_LONG_PRESS_MIN_MS = 1000;
export const FLOATING_LONG_PRESS_MAX_MS = 3000;
export const FLOATING_LONG_PRESS_STEP_MS = 100;

/** 读回存的按住时长：只收有限数字，夹在 1~3 秒内并取整到 100ms；其余（没存过、存坏了）回落到默认 2 秒。 */
export function normalizeLongPressMs(raw: unknown): number {
  if (typeof raw !== "number" || !Number.isFinite(raw)) return FLOATING_LONG_PRESS_MS;
  const clamped = Math.min(Math.max(raw, FLOATING_LONG_PRESS_MIN_MS), FLOATING_LONG_PRESS_MAX_MS);
  return Math.round(clamped / FLOATING_LONG_PRESS_STEP_MS) * FLOATING_LONG_PRESS_STEP_MS;
}

/** 滑轨位置（0~1）换算成按住时长（毫秒），按步长取整。 */
export function longPressFromRatio(ratio: number): number {
  const safe = Number.isFinite(ratio) ? Math.min(Math.max(ratio, 0), 1) : 0;
  return normalizeLongPressMs(
    FLOATING_LONG_PRESS_MIN_MS + safe * (FLOATING_LONG_PRESS_MAX_MS - FLOATING_LONG_PRESS_MIN_MS)
  );
}

/** 按住时长（毫秒）换算成滑轨位置（0~1）。 */
export function ratioFromLongPress(ms: number): number {
  return (normalizeLongPressMs(ms) - FLOATING_LONG_PRESS_MIN_MS) / (FLOATING_LONG_PRESS_MAX_MS - FLOATING_LONG_PRESS_MIN_MS);
}

/** 毫秒换成界面显示的秒数文案：2000 → "2"，1500 → "1.5"。 */
export function formatLongPressSeconds(ms: number): string {
  return String(normalizeLongPressMs(ms) / 1000);
}
/** 按下到松手不超过这个时长、且没移动，才算「点一下」切换语言；按了一会儿又松手什么都不做，避免误切。 */
export const FLOATING_TAP_MAX_MS = 500;
/** 拖动时出现的底部「隐藏」区：圆心离容器底部的距离、半径与命中半径。 */
export const FLOATING_CLOSE_ZONE_CENTER_FROM_BOTTOM = 64;
export const FLOATING_CLOSE_ZONE_RADIUS = 32;
export const FLOATING_CLOSE_ZONE_HIT_RADIUS = 60;

export type FloatingSide = "left" | "right";

export interface FloatingPosition {
  side: FloatingSide;
  /** 纵向位置占可用高度的比例（0 = 最上，1 = 最下），容器尺寸变化（字号、横竖屏）时按比例还原。 */
  yRatio: number;
}

export interface FloatingBounds {
  width: number;
  height: number;
  /** 顶部安全区（状态栏 / 刘海）。 */
  topInset: number;
}

export const DEFAULT_FLOATING_POSITION: FloatingPosition = { side: "right", yRatio: 0.62 };

function clamp(value: number, min: number, max: number) {
  if (max < min) return min;
  return Math.min(Math.max(value, min), max);
}

function clampRatio(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? clamp(value, 0, 1) : DEFAULT_FLOATING_POSITION.yRatio;
}

/** 读回本机存的位置：只接受已知取值，其余回落到默认（右侧、偏下）。 */
export function normalizeFloatingPosition(raw: unknown): FloatingPosition {
  const value = (raw ?? {}) as { side?: unknown; yRatio?: unknown };
  return {
    side: value.side === "left" ? "left" : "right",
    yRatio: clampRatio(value.yRatio),
  };
}

function verticalRange(bounds: FloatingBounds) {
  const minY = bounds.topInset + FLOATING_TOP_MARGIN;
  const maxY = bounds.height - FLOATING_BUTTON_SIZE - FLOATING_BOTTOM_MARGIN;
  return { minY, maxY: Math.max(minY, maxY) };
}

/** 吸边后的左上角坐标。 */
export function resolveDockedPoint(position: FloatingPosition, bounds: FloatingBounds) {
  const { minY, maxY } = verticalRange(bounds);
  const x = position.side === "left"
    ? -FLOATING_EDGE_TUCK
    : bounds.width - FLOATING_BUTTON_SIZE + FLOATING_EDGE_TUCK;
  return { x, y: minY + (maxY - minY) * clampRatio(position.yRatio) };
}

/** 拖动中的坐标：夹在安全范围内，按钮不会被拖出屏幕或拖进状态栏 / 底部导航栏。 */
export function clampDragPoint(point: { x: number; y: number }, bounds: FloatingBounds) {
  const { minY, maxY } = verticalRange(bounds);
  return {
    x: clamp(point.x, FLOATING_DRAG_EDGE_MARGIN, bounds.width - FLOATING_BUTTON_SIZE - FLOATING_DRAG_EDGE_MARGIN),
    y: clamp(point.y, minY, maxY),
  };
}

/** 松手后吸到离按钮中心更近的一侧，并换算成纵向比例保存。 */
export function resolveDropPosition(point: { x: number; y: number }, bounds: FloatingBounds): FloatingPosition {
  const { minY, maxY } = verticalRange(bounds);
  const centerX = point.x + FLOATING_BUTTON_SIZE / 2;
  const side: FloatingSide = centerX < bounds.width / 2 ? "left" : "right";
  const span = maxY - minY;
  const yRatio = span > 0 ? clamp((clamp(point.y, minY, maxY) - minY) / span, 0, 1) : DEFAULT_FLOATING_POSITION.yRatio;
  return { side, yRatio };
}

/** 隐藏区圆心坐标。 */
export function resolveCloseZoneCenter(bounds: Pick<FloatingBounds, "width" | "height">) {
  return { x: bounds.width / 2, y: bounds.height - FLOATING_CLOSE_ZONE_CENTER_FROM_BOTTOM };
}

/** 按钮（左上角坐标）是否已拖进底部隐藏区。 */
export function isInCloseZone(point: { x: number; y: number }, bounds: Pick<FloatingBounds, "width" | "height">) {
  const zone = resolveCloseZoneCenter(bounds);
  const dx = point.x + FLOATING_BUTTON_SIZE / 2 - zone.x;
  const dy = point.y + FLOATING_BUTTON_SIZE / 2 - zone.y;
  return Math.hypot(dx, dy) <= FLOATING_CLOSE_ZONE_HIT_RADIUS;
}

export function hasMovedBeyondThreshold(dx: number, dy: number) {
  return Math.hypot(dx, dy) > FLOATING_MOVE_THRESHOLD;
}

/** 松手时这一下算不算「点一下」：没拖动、没触发长按，且按住时间够短。 */
export function isTapGesture(input: { moved: boolean; longPressFired: boolean; pressedMs: number }) {
  return !input.moved && !input.longPressFired && input.pressedMs <= FLOATING_TAP_MAX_MS;
}
