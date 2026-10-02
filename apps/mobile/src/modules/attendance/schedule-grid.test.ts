import assert from "node:assert/strict";
import {
  availabilityKey,
  classifyScheduleGridCell,
  countUncoveredDays,
  formatShiftShort,
  groupAvailabilityByUserDate,
  isAllDayRange,
  normalizeClockTime,
  shiftEditorMinutes,
  filterScheduleUsers,
  isRelatedOnlyManager,
  isBatchSchedulableCell,
  runSequentialBatch,
  stepClockTime,
  summarizeSchedulePublishState,
  employmentTypeCode,
} from "./schedule-grid";
import type { AttendanceAvailability, AttendanceSchedule } from "./types";

const schedule = (
  workDate: string,
  startTime: string,
  endTime: string,
  status = "Active",
  leaveType?: string,
) => ({
  scheduleGuid: `${workDate}-${startTime}`,
  storeCode: "BRI",
  userGuid: "u1",
  workDate,
  startTime,
  endTime,
  status,
  isMine: false,
  leaveType,
}) as AttendanceSchedule;

const availability = (
  userGuid: string | undefined,
  workDate: string,
  startTime = "00:00:00",
  endTime = "23:59:00",
  status = "Submitted",
) => ({
  availabilityGuid: `a-${userGuid}-${workDate}-${startTime}`,
  userGuid,
  workDate,
  startTime,
  endTime,
  status,
}) as AttendanceAvailability;

// 时间归一化
assert.equal(normalizeClockTime("9:05:00"), "09:05");
assert.equal(normalizeClockTime("2026-09-28T17:30:00"), "17:30");
assert.equal(normalizeClockTime("abc"), "");

// 班次简写：整点省分钟，12 小时制
assert.equal(formatShiftShort("09:00:00", "17:00:00"), "9–5");
assert.equal(formatShiftShort("09:30", "17:30"), "9:30–5:30");
assert.equal(formatShiftShort("12:00", "00:00"), "12–12");
assert.equal(formatShiftShort("", "17:00"), "--–5");

// 单元格分类：请假 > 班次 > 可上班 > 空；已取消视同不存在
assert.equal(classifyScheduleGridCell([], []).kind, "empty");
assert.equal(classifyScheduleGridCell([], [availability("u1", "2026-09-28")]).kind, "available");
assert.equal(
  classifyScheduleGridCell([], [availability("u1", "2026-09-28", "09:00", "12:00", "Cancelled")]).kind,
  "empty",
);
assert.equal(
  classifyScheduleGridCell([schedule("2026-09-28", "09:00", "17:00", "Cancelled")], []).kind,
  "empty",
);
const shiftCell = classifyScheduleGridCell([
  schedule("2026-09-28", "13:00", "17:00"),
  schedule("2026-09-28", "08:00", "12:00", "Draft"),
], [availability("u1", "2026-09-28")]);
assert.equal(shiftCell.kind, "shift");
assert.deepEqual(shiftCell.schedules.map((item) => item.startTime), ["08:00", "13:00"]);
assert.equal(
  classifyScheduleGridCell([schedule("2026-09-28", "09:00", "17:00", "Active", "AnnualLeave")], []).kind,
  "leave",
);

// 可上班分组：按员工+日期，忽略已取消与缺员工标识
const grouped = groupAvailabilityByUserDate([
  availability("u1", "2026-09-28T00:00:00", "13:00", "18:00"),
  availability("u1", "2026-09-28", "08:00", "12:00"),
  availability("u2", "2026-09-28", "08:00", "12:00", "Cancelled"),
  availability(undefined, "2026-09-28"),
]);
assert.deepEqual(
  grouped.get(availabilityKey("u1", "2026-09-28"))?.map((item) => item.startTime),
  ["08:00", "13:00"],
);
assert.equal(grouped.has(availabilityKey("u2", "2026-09-28")), false);
assert.equal(grouped.size, 1);

// 全天约定
assert.equal(isAllDayRange("00:00:00", "23:59:00"), true);
assert.equal(isAllDayRange("00:00", "23:59:59"), true);
assert.equal(isAllDayRange("09:00", "23:59"), false);

// 步进在一天内循环
assert.equal(stepClockTime("09:00", 30), "09:30");
assert.equal(stepClockTime("09:15:00", -30), "08:45");
assert.equal(stepClockTime("23:30", 30), "00:00");
assert.equal(stepClockTime("00:00", -30), "23:30");
assert.equal(stepClockTime("bad", 30), "bad");

// 编辑时长：相等为 0，跨午夜按次日
assert.equal(shiftEditorMinutes("09:00", "17:30"), 480, "编辑弹层按计薪工时显示：8.5 小时班默认扣 1 次用餐");
assert.equal(shiftEditorMinutes("09:00", "17:30", 0), 510, "店长取消用餐后按全时长显示");
assert.equal(shiftEditorMinutes("09:00", "17:30", 2), 450, "店长指定 2 次用餐");
assert.equal(shiftEditorMinutes("09:00", "09:00:00"), 0);
assert.equal(shiftEditorMinutes("22:00", "02:00"), 240);
assert.equal(shiftEditorMinutes("", "02:00"), 0);

// 空缺日：请假与已取消不算覆盖
const weekDates = [
  "2026-09-28", "2026-09-29", "2026-09-30", "2026-10-01", "2026-10-02", "2026-10-03", "2026-10-04",
];
assert.equal(countUncoveredDays(weekDates, []), 7);
assert.equal(countUncoveredDays(weekDates, [
  schedule("2026-09-28", "09:00", "17:00"),
  schedule("2026-09-29", "09:00", "17:00", "Cancelled"),
  schedule("2026-09-30", "09:00", "17:00", "Active", "SickLeave"),
  schedule("2026-10-04T00:00:00", "09:00", "17:00", "Draft"),
]), 5);

// 发布状态
assert.equal(summarizeSchedulePublishState([]), "empty");
assert.equal(summarizeSchedulePublishState([schedule("2026-09-28", "09:00", "17:00")]), "published");
assert.equal(summarizeSchedulePublishState([
  schedule("2026-09-28", "09:00", "17:00"),
  schedule("2026-09-29", "09:00", "17:00", "Draft"),
]), "draft");
assert.equal(summarizeSchedulePublishState([
  schedule("2026-09-28", "09:00", "17:00", "Cancelled"),
  schedule("2026-09-29", "09:00", "17:00", "Draft", "AnnualLeave"),
]), "empty");

assert.equal(employmentTypeCode("fullTime"), "F");
assert.equal(employmentTypeCode("partTime"), "P");
assert.equal(employmentTypeCode("casual"), "C");
assert.equal(employmentTypeCode("Temporary"), "C");
assert.equal(employmentTypeCode(undefined), undefined);

// 排班名单过滤：管理本店的店长默认在表中，仅关联本店的店长默认不在；有班次的始终显示。
const scheduleUsers = [
  { userGUID: "staff" },
  { userGUID: "own-manager", isStoreManager: true, managesStore: true },
  { userGUID: "related-manager", isStoreManager: true, managesStore: false },
  { userGUID: "related-scheduled", isStoreManager: true, managesStore: false },
];
assert.equal(isRelatedOnlyManager(scheduleUsers[1]), false);
assert.equal(isRelatedOnlyManager(scheduleUsers[2]), true);
const hiddenByDefault = filterScheduleUsers(scheduleUsers, new Set(["related-scheduled"]), false);
assert.deepEqual(
  hiddenByDefault.visible.map((user) => user.userGUID),
  ["staff", "own-manager", "related-scheduled"],
  "仅关联本店的店长默认隐藏，但本周已有班次的仍显示",
);
assert.equal(hiddenByDefault.relatedManagerCount, 2);
assert.deepEqual(
  filterScheduleUsers(scheduleUsers, new Set(), true).visible.map((user) => user.userGUID),
  ["staff", "own-manager", "related-manager", "related-scheduled"],
  "打开筛选后显示全部相关店长",
);

// 批量排班：只给空格与仅有「可上班」标记的格子建班，已有班次或请假的跳过。
assert.equal(isBatchSchedulableCell({ kind: "empty" }), true);
assert.equal(isBatchSchedulableCell({ kind: "available" }), true);
assert.equal(isBatchSchedulableCell({ kind: "shift" }), false);
assert.equal(isBatchSchedulableCell({ kind: "leave" }), false);

async function batchTests() {
  // 依次提交：上一条完成才发下一条（后端按员工+日期加锁并校验重叠），单条失败不影响其余。
  const order: string[] = [];
  let inFlight = 0;
  let maxInFlight = 0;
  const outcome = await runSequentialBatch(["a", "b", "c"], async (item) => {
    inFlight += 1;
    maxInFlight = Math.max(maxInFlight, inFlight);
    await new Promise((resolve) => setTimeout(resolve, 1));
    inFlight -= 1;
    order.push(item);
    if (item === "b") throw new Error("overlap");
  });
  assert.deepEqual(order, ["a", "b", "c"]);
  assert.equal(maxInFlight, 1, "批量排班必须依次提交");
  assert.equal(outcome.succeeded, 2);
  assert.equal(outcome.failures.length, 1);
  assert.equal(outcome.failures[0].item, "b");
  assert.equal((outcome.failures[0].error as Error).message, "overlap");
  console.log("schedule-grid tests passed");
}

void batchTests();
