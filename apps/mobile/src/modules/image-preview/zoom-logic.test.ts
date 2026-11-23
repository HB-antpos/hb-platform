import assert from "node:assert/strict";
import { test } from "node:test";
import {
  DOUBLE_TAP_SCALE,
  MAX_SCALE,
  clampScale,
  clampTranslation,
  dragProgress,
  doubleTapTarget,
  fitContain,
  isDoubleTap,
  isPointInsideImage,
  maxTranslation,
  pinchScale,
  pinchTransform,
  shouldDismiss,
  shouldShowPreviewHint,
  touchDistance,
  touchMidpoint,
  zoomPercentLabel,
} from "./zoom-logic";

const container = { width: 390, height: 844 };

test("缩放倍数限制在 1–4 倍，异常值回到 1 倍", () => {
  assert.equal(clampScale(0.4), 1);
  assert.equal(clampScale(2.2), 2.2);
  assert.equal(clampScale(9), MAX_SCALE);
  assert.equal(clampScale(Number.NaN), 1);
});

test("双指距离与中点", () => {
  assert.equal(touchDistance({ x: 0, y: 0 }, { x: 3, y: 4 }), 5);
  assert.deepEqual(touchMidpoint({ x: 10, y: 20 }, { x: 30, y: 60 }), { x: 20, y: 40 });
});

test("双指缩放：按距离比例放大并限制上下限，起始距离为 0 时不变", () => {
  assert.equal(pinchScale(1, 100, 200), 2);
  assert.equal(pinchScale(2, 100, 400), MAX_SCALE);
  assert.equal(pinchScale(1, 100, 50), 1);
  assert.equal(pinchScale(1.5, 0, 80), 1.5);
});

test("完整显示：宽图受限于宽度、竖图受限于高度，未知尺寸用正方形兜底", () => {
  assert.deepEqual(fitContain({ width: 1000, height: 500 }, container), { width: 390, height: 195 });
  assert.deepEqual(fitContain({ width: 500, height: 2000 }, container), { width: 211, height: 844 });
  assert.deepEqual(fitContain(null, container), { width: 390, height: 390 });
});

test("平移范围：内容没超出容器就不能平移，放大后只能移到边缘", () => {
  const displayed = { width: 390, height: 390 };
  assert.deepEqual(maxTranslation(1, displayed, container), { x: 0, y: 0 });
  // 2× 后宽 780、高 780：横向可移 195，纵向 780 < 844 仍不能移。
  assert.deepEqual(maxTranslation(2, displayed, container), { x: 195, y: 0 });
  assert.deepEqual(clampTranslation({ x: 999, y: -50 }, 2, displayed, container), { x: 195, y: 0 });
});

test("双指缩放时，起始中点下的图片点跟着当前中点走", () => {
  // 在 (100, 50) 处以 1× 开始，放大到 2×，两指没有移动：该点应仍在 (100, 50)。
  const stay = pinchTransform(1, { x: 0, y: 0 }, { x: 100, y: 50 }, { x: 100, y: 50 }, 2);
  assert.deepEqual(stay, { x: -100, y: -50 });
  // 验证：图片点 q = 起始中点 / 起始倍数 = (100,50)；缩放后位置 = 2*q + t = (100,50)。
  assert.deepEqual({ x: 2 * 100 + stay.x, y: 2 * 50 + stay.y }, { x: 100, y: 50 });
  // 两指一起往右移 30：画面整体跟着右移 30。
  const moved = pinchTransform(1, { x: 0, y: 0 }, { x: 100, y: 50 }, { x: 130, y: 50 }, 2);
  assert.deepEqual({ x: 2 * 100 + moved.x, y: 2 * 50 + moved.y }, { x: 130, y: 50 });
});

test("双击判定：时间和位置都要足够近", () => {
  const first = { time: 1000, x: 100, y: 100 };
  assert.equal(isDoubleTap(null, first), false);
  assert.equal(isDoubleTap(first, { time: 1200, x: 110, y: 105 }), true);
  assert.equal(isDoubleTap(first, { time: 1400, x: 100, y: 100 }), false);
  assert.equal(isDoubleTap(first, { time: 1200, x: 200, y: 100 }), false);
});

test("双击：1× 放大到 2.5× 并保持被点位置，已放大则还原", () => {
  const displayed = { width: 390, height: 844 };
  const zoomed = doubleTapTarget(1, { x: 60, y: -100 }, displayed, container);
  assert.equal(zoomed.scale, DOUBLE_TAP_SCALE);
  // 2.5× 后被点的位置 = 2.5 * 点 + 平移 = 原位置（未触边时）。
  assert.deepEqual(
    { x: zoomed.scale * 60 + zoomed.translate.x, y: zoomed.scale * -100 + zoomed.translate.y },
    { x: 60, y: -100 },
  );
  // 点在边角时平移会被限制在图片边缘内，不露出黑边。
  const corner = doubleTapTarget(1, { x: 190, y: 400 }, displayed, container);
  const max = maxTranslation(DOUBLE_TAP_SCALE, displayed, container);
  assert.ok(Math.abs(corner.translate.x) <= max.x && Math.abs(corner.translate.y) <= max.y);
  assert.deepEqual(doubleTapTarget(2.5, { x: 0, y: 0 }, displayed, container), {
    scale: 1,
    translate: { x: 0, y: 0 },
  });
});

test("点击是否落在图片内：缩放平移后依然按实际范围判断", () => {
  const displayed = { width: 390, height: 300 };
  assert.equal(isPointInsideImage({ x: 0, y: 100 }, 1, { x: 0, y: 0 }, displayed), true);
  assert.equal(isPointInsideImage({ x: 0, y: 200 }, 1, { x: 0, y: 0 }, displayed), false);
  assert.equal(isPointInsideImage({ x: 0, y: 200 }, 2, { x: 0, y: 0 }, displayed), true);
  assert.equal(isPointInsideImage({ x: 300, y: 0 }, 2, { x: 200, y: 0 }, displayed), true);
});

test("下滑关闭：只在 1× 生效，距离或甩动速度够才关闭", () => {
  assert.equal(shouldDismiss(130, 0, 1), true);
  assert.equal(shouldDismiss(60, 0.5, 1), false);
  assert.equal(shouldDismiss(60, 1.5, 1), true);
  assert.equal(shouldDismiss(20, 3, 1), false);
  assert.equal(shouldDismiss(400, 3, 2.5), false);
});

test("下滑进度与手势提示次数", () => {
  assert.equal(dragProgress(0, 800), 0);
  assert.equal(dragProgress(200, 800), 0.5);
  assert.equal(dragProgress(900, 800), 1);
  assert.equal(dragProgress(-50, 800), 0);
  assert.equal(dragProgress(100, 0), 0);
  assert.equal(shouldShowPreviewHint(0), true);
  assert.equal(shouldShowPreviewHint(2), true);
  assert.equal(shouldShowPreviewHint(3), false);
  assert.equal(zoomPercentLabel(2.5), "250%");
});
