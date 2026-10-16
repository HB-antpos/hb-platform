import assert from "node:assert/strict";
import test from "node:test";
import {
  clampDragPoint,
  DEFAULT_FLOATING_POSITION,
  FLOATING_BUTTON_SIZE,
  FLOATING_DRAG_EDGE_MARGIN,
  FLOATING_EDGE_TUCK,
  FLOATING_LONG_PRESS_MS,
  FLOATING_LONG_PRESS_MAX_MS,
  FLOATING_LONG_PRESS_MIN_MS,
  formatLongPressSeconds,
  longPressFromRatio,
  normalizeLongPressMs,
  ratioFromLongPress,
  FLOATING_TAP_MAX_MS,
  hasMovedBeyondThreshold,
  isInCloseZone,
  isTapGesture,
  normalizeFloatingPosition,
  resolveCloseZoneCenter,
  resolveDockedPoint,
  resolveDropPosition,
} from "./geometry";

// 390 宽的手机去掉底部导航栏后的容器，顶部安全区 44。
const bounds = { width: 390, height: 760, topInset: 44 };

test("读回位置：坏值回落默认，比例夹在 0~1", () => {
  assert.deepEqual(normalizeFloatingPosition(null), DEFAULT_FLOATING_POSITION);
  assert.deepEqual(normalizeFloatingPosition({ side: "up", yRatio: "x" }), DEFAULT_FLOATING_POSITION);
  assert.deepEqual(normalizeFloatingPosition({ side: "left", yRatio: 3 }), { side: "left", yRatio: 1 });
  assert.deepEqual(normalizeFloatingPosition({ side: "right", yRatio: -1 }), { side: "right", yRatio: 0 });
});

test("吸边：右侧伸出屏幕 9dp，左侧 x 为负；纵向在状态栏之下、容器底部之上", () => {
  const right = resolveDockedPoint({ side: "right", yRatio: 0 }, bounds);
  assert.equal(right.x, 390 - FLOATING_BUTTON_SIZE + FLOATING_EDGE_TUCK);
  assert.equal(right.y, 44 + 8);
  const left = resolveDockedPoint({ side: "left", yRatio: 1 }, bounds);
  assert.equal(left.x, -FLOATING_EDGE_TUCK);
  assert.equal(left.y, 760 - FLOATING_BUTTON_SIZE - 12);
});

test("拖动夹紧：不出左右安全边距、不进状态栏、不压底部导航栏", () => {
  assert.deepEqual(clampDragPoint({ x: -100, y: -100 }, bounds), { x: FLOATING_DRAG_EDGE_MARGIN, y: 52 });
  assert.deepEqual(
    clampDragPoint({ x: 1000, y: 5000 }, bounds),
    { x: 390 - FLOATING_BUTTON_SIZE - FLOATING_DRAG_EDGE_MARGIN, y: 760 - FLOATING_BUTTON_SIZE - 12 }
  );
});

test("松手吸到中心更近的一侧，纵向比例可还原", () => {
  const leftDrop = resolveDropPosition({ x: 100, y: 52 }, bounds);
  assert.equal(leftDrop.side, "left");
  assert.equal(leftDrop.yRatio, 0);
  const rightDrop = resolveDropPosition({ x: 250, y: 400 }, bounds);
  assert.equal(rightDrop.side, "right");
  assert.equal(Math.round(resolveDockedPoint(rightDrop, bounds).y), 400);
});

test("容器过矮时不出现负区间（字号放大 / 小屏）", () => {
  const tiny = { width: 320, height: 60, topInset: 20 };
  const point = resolveDockedPoint({ side: "right", yRatio: 0.5 }, tiny);
  assert.equal(point.y, 28);
  assert.equal(resolveDropPosition({ x: 0, y: 0 }, tiny).yRatio, DEFAULT_FLOATING_POSITION.yRatio);
});

test("隐藏区：按钮中心进入底部圆心 60dp 内才算", () => {
  const center = resolveCloseZoneCenter(bounds);
  const atCenter = { x: center.x - FLOATING_BUTTON_SIZE / 2, y: center.y - FLOATING_BUTTON_SIZE / 2 };
  assert.equal(isInCloseZone(atCenter, bounds), true);
  assert.equal(isInCloseZone({ x: atCenter.x + 59, y: atCenter.y }, bounds), true);
  assert.equal(isInCloseZone({ x: atCenter.x + 61, y: atCenter.y }, bounds), false);
  assert.equal(isInCloseZone({ x: atCenter.x, y: 100 }, bounds), false);
});

test("点击判定：没移动、没触发长按、按得够短才切语言", () => {
  assert.equal(hasMovedBeyondThreshold(5, 5), false);
  assert.equal(hasMovedBeyondThreshold(8, 6), true);
  assert.equal(isTapGesture({ moved: false, longPressFired: false, pressedMs: 120 }), true);
  assert.equal(isTapGesture({ moved: false, longPressFired: false, pressedMs: FLOATING_TAP_MAX_MS + 1 }), false);
  assert.equal(isTapGesture({ moved: true, longPressFired: false, pressedMs: 100 }), false);
  assert.equal(isTapGesture({ moved: false, longPressFired: true, pressedMs: 100 }), false);
});

test("按住时长：默认 2 秒；范围 1~3 秒；坏值回落默认；越界夹紧、取整到 100ms", () => {
  assert.equal(FLOATING_LONG_PRESS_MS, 2000);
  assert.equal(FLOATING_LONG_PRESS_MIN_MS, 1000);
  assert.equal(FLOATING_LONG_PRESS_MAX_MS, 3000);
  // 下限必须明显高于「点一下」的判定上限，否则点一下和长按会混在一起。
  assert.ok(FLOATING_LONG_PRESS_MIN_MS > FLOATING_TAP_MAX_MS * 1.5);
  assert.equal(normalizeLongPressMs(undefined), 2000);
  assert.equal(normalizeLongPressMs("3000"), 2000);
  assert.equal(normalizeLongPressMs(Number.NaN), 2000);
  assert.equal(normalizeLongPressMs(50), 1000);
  assert.equal(normalizeLongPressMs(99999), 3000);
  assert.equal(normalizeLongPressMs(1530), 1500);
  assert.equal(normalizeLongPressMs(2340), 2300);
});

test("滑轨换算：两端、中点与步长取整，且与时长互相还原", () => {
  assert.equal(longPressFromRatio(0), 1000);
  assert.equal(longPressFromRatio(1), 3000);
  assert.equal(longPressFromRatio(0.5), 2000);
  assert.equal(longPressFromRatio(-3), 1000);
  assert.equal(longPressFromRatio(7), 3000);
  assert.equal(longPressFromRatio(Number.NaN), 1000);
  assert.equal(longPressFromRatio(0.123), 1200);
  assert.equal(ratioFromLongPress(1000), 0);
  assert.equal(ratioFromLongPress(3000), 1);
  assert.equal(ratioFromLongPress(2000), 0.5);
  for (let ms = 1000; ms <= 3000; ms += 100) {
    assert.equal(longPressFromRatio(ratioFromLongPress(ms)), ms);
  }
});

test("按住时长的界面文案：整秒不带小数，半秒带一位", () => {
  assert.equal(formatLongPressSeconds(2000), "2");
  assert.equal(formatLongPressSeconds(1500), "1.5");
  assert.equal(formatLongPressSeconds(1000), "1");
  assert.equal(formatLongPressSeconds(2300), "2.3");
  assert.equal(formatLongPressSeconds(3000), "3");
  assert.equal(formatLongPressSeconds(undefined as unknown as number), "2");
});
