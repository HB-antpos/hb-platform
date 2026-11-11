import assert from "node:assert/strict";
import {
  buildDayStaffing,
  countDraftShifts,
  countScheduledPeopleByDay,
  shiftMinutesRange,
  todayScrollOffset,
  availabilityKey,
  classifyScheduleGridCell,
  countUncoveredDays,
  formatShiftShort,
  groupAvailabilityByUserDate,
  isAllDayRange,
  normalizeClockTime,
  shiftEditorMinutes,
  filterScheduleUsers,
  isOtherManager,
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
// 不能上班：只有不能上班记录时格子标为 unavailable；同天还有可上班时段则仍按可上班；有班次时以班次为准。
const unavailableItem = { ...availability("u1", "2026-09-28"), isUnavailable: true };
assert.equal(classifyScheduleGridCell([], [unavailableItem]).kind, "unavailable");
assert.equal(classifyScheduleGridCell([], [unavailableItem, availability("u1", "2026-09-28", "13:00", "17:00")]).kind, "available");
assert.equal(isBatchSchedulableCell({ kind: "unavailable" }), false, "批量排班跳过不能上班的格子");
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

// 排班名单过滤：店长默认只显示自己，其他店长（无论是否管理本店）收进筛选；本周已有班次的始终显示。
const scheduleUsers = [
  { userGUID: "staff" },
  { userGUID: "Me-Manager", isStoreManager: true, managesStore: true },
  { userGUID: "other-manager", isStoreManager: true, managesStore: true },
  { userGUID: "related-manager", isStoreManager: true, managesStore: false },
  { userGUID: "other-scheduled", isStoreManager: true, managesStore: true },
];
assert.equal(isOtherManager(scheduleUsers[0], "me-manager"), false, "店员不算其他店长");
assert.equal(isOtherManager(scheduleUsers[1], "me-manager"), false, "当前登录的店长自己不算（GUID 忽略大小写）");
assert.equal(isOtherManager(scheduleUsers[2], "me-manager"), true);
const hiddenByDefault = filterScheduleUsers(scheduleUsers, new Set(["other-scheduled"]), false, "me-manager");
assert.deepEqual(
  hiddenByDefault.visible.map((user) => user.userGUID),
  ["staff", "Me-Manager", "other-scheduled"],
  "其他店长默认隐藏，但本周已有班次的仍显示",
);
assert.equal(hiddenByDefault.otherManagerCount, 3);
assert.deepEqual(
  filterScheduleUsers(scheduleUsers, new Set(), true, "me-manager").visible.map((user) => user.userGUID),
  ["staff", "Me-Manager", "other-manager", "related-manager", "other-scheduled"],
  "打开筛选后显示全部店长",
);
assert.equal(
  filterScheduleUsers(scheduleUsers, new Set(), false, undefined).visible.length,
  1,
  "取不到当前用户时所有店长都按其他店长处理",
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

// ───────── 排班矩阵改版 ─────────
{
  const s = (userGuid: string, workDate: string, startTime: string, endTime: string, extra: Record<string, unknown> = {}) =>
    ({ scheduleGuid: `${userGuid}-${workDate}-${startTime}`, storeCode: "ROB", userGuid, workDate, startTime, endTime, status: "Active", ...extra }) as never;
  const week = ["2026-11-09", "2026-11-10", "2026-11-11"];
  // 每天人数：同一人两班只算一次；请假、已取消不算。
  assert.deepEqual(countScheduledPeopleByDay(week, [
    s("a", "2026-11-09", "09:00", "12:00"), s("a", "2026-11-09", "13:00", "17:00"), s("b", "2026-11-09", "10:00", "18:00"),
    s("c", "2026-11-10", "09:00", "17:00", { leaveType: "PaidAnnual" }), s("d", "2026-11-10", "09:00", "17:00", { status: "Cancelled" }),
  ]), [2, 0, 0]);
  assert.equal(countDraftShifts([s("a", "2026-11-09", "09:00", "17:00", { status: "Draft" }), s("b", "2026-11-09", "09:00", "17:00")]), 1);
  // 滚到今天：左边留前一天。
  assert.equal(todayScrollOffset(week, "2026-11-11", 76), 76);
  assert.equal(todayScrollOffset(week, "2026-11-09", 76), 0);
  assert.equal(todayScrollOffset(week, "2026-12-01", 76), 0);
  // 跨午夜。
  assert.deepEqual(shiftMinutesRange({ startTime: "22:00", endTime: "06:00" }), { start: 1320, end: 1800 });
  // 日视图人手：09–17、10–18、12–20 → 20 点后没人在 range 外；19 点只有 1 人。
  const staffing = buildDayStaffing([s("a", "2026-11-11", "09:00", "17:00"), s("b", "2026-11-11", "10:00", "18:00"), s("c", "2026-11-11", "12:00", "20:00")]);
  assert.deepEqual(staffing.range, { start: 540, end: 1200 });
  assert.deepEqual(staffing.hours.map((item) => item.count), [1, 2, 2, 3, 3, 3, 3, 3, 2, 1, 1]);
  assert.deepEqual(staffing.gaps, [{ start: 540, end: 600, count: 1 }, { start: 1080, end: 1200, count: 1 }]);
  assert.deepEqual(buildDayStaffing([]).range, { start: 480, end: 1200 });
}
