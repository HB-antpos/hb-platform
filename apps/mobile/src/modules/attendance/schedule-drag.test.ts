import assert from "node:assert/strict";
import {
  clampScrollOffset,
  getDragStartVerdict,
  getEdgeScrollStep,
  hitTestScheduleCell,
  isSameDragCell,
} from "./schedule-drag";
import type { DragGridGeometry } from "./schedule-drag";
import type { AttendanceSchedule } from "./types";

const schedule = (extra: Partial<AttendanceSchedule> = {}): AttendanceSchedule => ({
  scheduleGuid: "s-1",
  storeCode: "1013",
  userGuid: "u-sean",
  workDate: "2026-10-26T00:00:00",
  startTime: "09:00:00",
  endTime: "17:30:00",
  status: "Draft",
  isMine: false,
  ...extra,
});

// 内容左上角在屏幕 (100, 200)，表头 52、格高 52、格宽 60，3 行 7 天
const geometry: DragGridGeometry = {
  originX: 100,
  originY: 200,
  cellWidth: 60,
  headerHeight: 52,
  cellHeight: 52,
  rowCount: 3,
  dayCount: 7,
};

// 命中：第一格（周一、第一行）左上角与边界
assert.deepEqual(hitTestScheduleCell({ x: 100, y: 252 }, geometry), { rowIndex: 0, dayIndex: 0 });
assert.deepEqual(hitTestScheduleCell({ x: 159, y: 303 }, geometry), { rowIndex: 0, dayIndex: 0 });
assert.deepEqual(hitTestScheduleCell({ x: 160, y: 304 }, geometry), { rowIndex: 1, dayIndex: 1 });
// 最后一行、最后一天
assert.deepEqual(hitTestScheduleCell({ x: 100 + 60 * 7 - 1, y: 200 + 52 + 52 * 3 - 1 }, geometry), { rowIndex: 2, dayIndex: 6 });
// 表头、姓名列（内容左侧）、表格右 / 下方都不命中
assert.equal(hitTestScheduleCell({ x: 130, y: 220 }, geometry), undefined);
assert.equal(hitTestScheduleCell({ x: 99, y: 260 }, geometry), undefined);
assert.equal(hitTestScheduleCell({ x: 100 + 60 * 7, y: 260 }, geometry), undefined);
assert.equal(hitTestScheduleCell({ x: 130, y: 200 + 52 + 52 * 3 }, geometry), undefined);
// 格宽未量到时不命中，避免除零
assert.equal(hitTestScheduleCell({ x: 130, y: 260 }, { ...geometry, cellWidth: 0 }), undefined);
// 横向滚动后内容整体左移：originX 变小，同一手指位置命中更靠后的日期
assert.deepEqual(hitTestScheduleCell({ x: 130, y: 260 }, { ...geometry, originX: 100 - 120 }), { rowIndex: 0, dayIndex: 2 });

// 格子相同判断（含空值）
assert.equal(isSameDragCell({ rowIndex: 1, dayIndex: 2 }, { rowIndex: 1, dayIndex: 2 }), true);
assert.equal(isSameDragCell({ rowIndex: 1, dayIndex: 2 }, { rowIndex: 1, dayIndex: 3 }), false);
assert.equal(isSameDragCell(undefined, null), true);
assert.equal(isSameDragCell(undefined, { rowIndex: 0, dayIndex: 0 }), false);

// 边缘自动滚动：中间不滚，靠左负、靠右正，越靠边越快且不超过上限
const viewport = { viewportLeft: 100, viewportRight: 400 };
assert.equal(getEdgeScrollStep({ pointX: 250, ...viewport }), 0);
assert.ok(getEdgeScrollStep({ pointX: 110, ...viewport }) < 0);
assert.ok(getEdgeScrollStep({ pointX: 390, ...viewport }) > 0);
assert.ok(Math.abs(getEdgeScrollStep({ pointX: 101, ...viewport })) > Math.abs(getEdgeScrollStep({ pointX: 140, ...viewport })));
assert.equal(getEdgeScrollStep({ pointX: -500, ...viewport }), -16);
assert.equal(getEdgeScrollStep({ pointX: 9999, ...viewport }), 16);
// 可视区太窄（比两个边缘带还窄）不自动滚，避免左右同时触发
assert.equal(getEdgeScrollStep({ pointX: 120, viewportLeft: 100, viewportRight: 180 }), 0);

// 滚动位置限制在内容范围内
assert.equal(clampScrollOffset(-10, 420, 300), 0);
assert.equal(clampScrollOffset(50, 420, 300), 50);
assert.equal(clampScrollOffset(500, 420, 300), 120);
assert.equal(clampScrollOffset(30, 200, 300), 0);

// 能不能拿起：一格一条非请假班次才能拖
const never = () => false;
const only = schedule();
const ok = getDragStartVerdict({ kind: "shift", schedules: [only] }, never);
assert.equal(ok.state, "ok");
assert.equal(ok.state === "ok" ? ok.schedule.scheduleGuid : "", "s-1");
// 有打卡 / 保守锁定：locked
assert.equal(getDragStartVerdict({ kind: "shift", schedules: [only] }, () => true).state, "locked");
// 一格多班次、请假、空格、可上班标记：none
assert.equal(
  getDragStartVerdict({ kind: "shift", schedules: [only, schedule({ scheduleGuid: "s-2" })] }, never).state,
  "none",
);
assert.equal(getDragStartVerdict({ kind: "leave", schedules: [schedule({ leaveType: "Annual" })] }, never).state, "none");
assert.equal(getDragStartVerdict({ kind: "empty", schedules: [] }, never).state, "none");
assert.equal(getDragStartVerdict({ kind: "available", schedules: [] }, never).state, "none");
// 即使格子被分类为 shift，带请假类型的班次也不能拖
assert.equal(getDragStartVerdict({ kind: "shift", schedules: [schedule({ leaveType: "Sick" })] }, never).state, "none");

console.log("schedule-drag tests passed");
