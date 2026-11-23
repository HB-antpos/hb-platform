// 大图预览的手势几何：全部是纯函数，方便脱离 React Native 做单测。
// 约定：变换原点是容器中心，所有坐标都相对容器中心（向右、向下为正）。

export interface Point {
  x: number;
  y: number;
}

export interface Size {
  width: number;
  height: number;
}

export const MIN_SCALE = 1;
export const MAX_SCALE = 4;
/** 双击放大的倍数。 */
export const DOUBLE_TAP_SCALE = 2.5;
export const DOUBLE_TAP_MS = 280;
export const DOUBLE_TAP_SLOP = 30;
/** 手指移动不超过这个距离且时间很短，才算「点一下」。 */
export const TAP_SLOP = 10;
export const TAP_MAX_MS = 300;
/** 下滑关闭：位移超过 120pt，或位移超过 40pt 且速度够快。 */
export const DISMISS_DISTANCE = 120;
export const DISMISS_FLICK_DISTANCE = 40;
export const DISMISS_VELOCITY = 1.2;
/** 缩放回到该倍数以内就吸附回 1×。 */
export const SNAP_BACK_SCALE = 1.05;
/** 手势提示条只在前 3 次打开时出现。 */
export const PREVIEW_HINT_MAX_SHOWS = 3;

export function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

export function clampScale(value: number) {
  return clamp(Number.isFinite(value) ? value : MIN_SCALE, MIN_SCALE, MAX_SCALE);
}

export function touchDistance(a: Point, b: Point) {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

export function touchMidpoint(a: Point, b: Point): Point {
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

/** 双指缩放：按两指距离的变化比例放大，起始距离异常时保持原倍数。 */
export function pinchScale(baseScale: number, startDistance: number, distance: number) {
  if (!(startDistance > 0) || !Number.isFinite(distance)) return clampScale(baseScale);
  return clampScale(baseScale * (distance / startDistance));
}

/**
 * 图片在容器里按「完整显示」排布后的实际尺寸（等比缩放、不裁剪）。
 * 还没拿到原图尺寸时按容器宽度的正方形兜底，之后拿到真实尺寸会重新计算。
 */
export function fitContain(natural: Size | null, container: Size): Size {
  if (!natural || !(natural.width > 0) || !(natural.height > 0)) {
    const side = Math.min(container.width, container.height);
    return { width: side, height: side };
  }
  const ratio = Math.min(container.width / natural.width, container.height / natural.height);
  return { width: natural.width * ratio, height: natural.height * ratio };
}

/** 放大后内容比容器大出的部分的一半，就是单侧允许平移的最大距离；内容没超出容器则不能平移。 */
export function maxTranslation(scale: number, displayed: Size, container: Size): Point {
  return {
    x: Math.max(0, (displayed.width * scale - container.width) / 2),
    y: Math.max(0, (displayed.height * scale - container.height) / 2),
  };
}

export function clampTranslation(translate: Point, scale: number, displayed: Size, container: Size): Point {
  const max = maxTranslation(scale, displayed, container);
  // 末尾 + 0 把 -0 规整成 0，避免动画值里出现负零。
  return { x: clamp(translate.x, -max.x, max.x) + 0, y: clamp(translate.y, -max.y, max.y) + 0 };
}

/**
 * 双指缩放时的平移：保证起始时两指中点下面的那个图片点，缩放后跟着两指中点走
 * （既实现「以两指中点为中心」，也同时支持两指一起拖动）。
 */
export function pinchTransform(
  startScale: number,
  startTranslate: Point,
  startMid: Point,
  currentMid: Point,
  newScale: number,
): Point {
  const ratio = newScale / startScale;
  return {
    x: currentMid.x - (startMid.x - startTranslate.x) * ratio,
    y: currentMid.y - (startMid.y - startTranslate.y) * ratio,
  };
}

export interface TapRecord {
  time: number;
  x: number;
  y: number;
}

export function isDoubleTap(previous: TapRecord | null, current: TapRecord) {
  if (!previous) return false;
  return (
    current.time - previous.time <= DOUBLE_TAP_MS &&
    Math.hypot(current.x - previous.x, current.y - previous.y) <= DOUBLE_TAP_SLOP
  );
}

/** 双击：已放大则还原；1× 时放大到 2.5×，并让被点的位置留在原处。 */
export function doubleTapTarget(
  currentScale: number,
  tapPoint: Point,
  displayed: Size,
  container: Size,
): { scale: number; translate: Point } {
  if (currentScale > SNAP_BACK_SCALE) return { scale: MIN_SCALE, translate: { x: 0, y: 0 } };
  const scale = DOUBLE_TAP_SCALE;
  return {
    scale,
    translate: clampTranslation(
      { x: tapPoint.x * (1 - scale), y: tapPoint.y * (1 - scale) },
      scale,
      displayed,
      container,
    ),
  };
}

/** 点击位置是否落在（缩放、平移之后的）图片范围内；范围外的点击用来关闭预览。 */
export function isPointInsideImage(
  point: Point,
  scale: number,
  translate: Point,
  displayed: Size,
): boolean {
  const halfWidth = (displayed.width * scale) / 2;
  const halfHeight = (displayed.height * scale) / 2;
  return (
    Math.abs(point.x - translate.x) <= halfWidth && Math.abs(point.y - translate.y) <= halfHeight
  );
}

/** 只有 1× 时才允许下滑关闭，放大状态下下滑只是平移，避免看细节时误关。 */
export function shouldDismiss(dragY: number, velocityY: number, scale: number) {
  if (scale > SNAP_BACK_SCALE) return false;
  return (
    dragY > DISMISS_DISTANCE || (dragY > DISMISS_FLICK_DISTANCE && velocityY > DISMISS_VELOCITY)
  );
}

/** 下滑进度 0–1：越往下背景越透明、图片越小，让关闭手势有跟手的反馈。 */
export function dragProgress(dragY: number, containerHeight: number) {
  if (!(containerHeight > 0)) return 0;
  return clamp(dragY / (containerHeight * 0.5), 0, 1);
}

export function shouldShowPreviewHint(shownCount: number) {
  return shownCount < PREVIEW_HINT_MAX_SHOWS;
}

export function zoomPercentLabel(scale: number) {
  return `${Math.round(scale * 100)}%`;
}
