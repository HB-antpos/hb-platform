import assert from "node:assert/strict";
import {
  FALLBACK_STAFF_COLOR,
  STAFF_PALETTE,
  assignStaffColors,
  batchCellKey,
  batchMatrixTargets,
  buildBatchMatrix,
  buildStaffLegend,
  compareDayShifts,
  findAvailableUnscheduled,
  getRelativeWeekOffset,
  groupScheduleBoardDays,
  hashString,
  summarizeBatchMatrix,
} from "./schedule-board";
import { groupAvailabilityByUserDate } from "./schedule-grid";
import type { AttendanceAvailability, AttendanceSchedule } from "./types";

const shift = (
  userGuid: string,
  workDate: string,
  startTime: string,
  endTime: string,
  extra: Partial<AttendanceSchedule> = {},
) => ({
  scheduleGuid: `${userGuid}-${workDate}-${startTime}`,
  storeCode: "1002",
  userGuid,
  workDate,
  startTime,
  endTime,
  status: "Active",
  isMine: false,
  ...extra,
}) as AttendanceSchedule;

const avail = (userGuid: string, workDate: string, startTime = "00:00:00", endTime = "23:59:00", isUnavailable = false) => ({
  availabilityGuid: `a-${userGuid}-${workDate}-${startTime}`,
  userGuid,
  workDate,
  startTime,
  endTime,
  status: "Submitted",
  isUnavailable,
}) as AttendanceAvailability;

const names: Record<string, string> = { u1: "Liam", u2: "Cody", u3: "Hunter", u4: "Ava" };
const nameOf = (guid: string) => names[guid] ?? guid;
const week = ["2026-11-16", "2026-11-17", "2026-11-18", "2026-11-19", "2026-11-20", "2026-11-21", "2026-11-22"];

// ---------- 配色：与 Web 同算法（这里用 Web 测试里的同一组断言） ----------
assert.equal(STAFF_PALETTE.length, 16);
assert.equal(hashString(""), 0x811c9dc5);
assert.equal(hashString("abc"), hashString("abc"));
const colorsA = assignStaffColors([{ userGuid: "u1", name: "Liam" }, { userGuid: "u2", name: "Cody" }, { userGuid: "u3", name: "Hunter" }]);
const colorsB = assignStaffColors([{ userGuid: "u3", name: "Hunter" }, { userGuid: "u1", name: "Liam" }, { userGuid: "u2", name: "Cody" }]);
assert.deepEqual([...colorsA.entries()].sort(), [...colorsB.entries()].sort()); // 入参顺序不影响
assert.equal(new Set(colorsA.values()).size, 3); // 不撞色
const many = assignStaffColors(Array.from({ length: 16 }, (_, index) => ({ userGuid: `g${index}`, name: `N${index}` })));
assert.equal(new Set(many.values()).size, 16); // 16 人正好用满色板
assert.ok(!STAFF_PALETTE.includes(FALLBACK_STAFF_COLOR));
assert.equal(assignStaffColors([{ userGuid: "", name: "x" }]).size, 0);

// ---------- 同一天排序：开始时间 → 员工名 → 结束时间；请假排最后 ----------
const s1 = shift("u1", "2026-11-16", "09:00:00", "13:00:00");
const s2 = shift("u2", "2026-11-16", "09:00", "17:00");
const s3 = shift("u3", "2026-11-16", "08:30", "12:00");
const leave = shift("u4", "2026-11-16", "00:00", "23:59", { leaveType: "Annual" });
const cancelled = shift("u4", "2026-11-17", "09:00", "12:00", { status: "Cancelled" });
const ordered = [s1, leave, s2, s3].sort((a, b) => compareDayShifts(a, b, nameOf)).map((item) => item.userGuid);
assert.deepEqual(ordered, ["u3", "u2", "u1", "u4"]);

const days = groupScheduleBoardDays(week, [s1, s2, s3, leave, cancelled, shift("u1", "2026-12-01", "09:00", "10:00")], nameOf);
assert.equal(days.length, 7);
assert.deepEqual(days[0].shifts.map((item) => item.userGuid), ["u3", "u2", "u1", "u4"]);
assert.equal(days[0].people, 3); // 请假不算上班人数
assert.equal(days[1].shifts.length, 0); // 已取消不显示
assert.equal(days[0].paidMinutes > 0, true);
assert.deepEqual(days.map((day) => day.date), week);

// 同一人一天两个班只算 1 人
const twoShifts = groupScheduleBoardDays(week, [shift("u1", "2026-11-18", "09:00", "11:00"), shift("u1", "2026-11-18", "14:00", "16:00")], nameOf);
assert.equal(twoShifts[2].people, 1);
assert.equal(twoShifts[2].shifts.length, 2);

// ---------- 图例：本周有班的人，按名字排，含班数与工时 ----------
const legend = buildStaffLegend([s1, s2, s3, leave, cancelled, shift("u1", "2026-11-18", "09:00", "13:00")], nameOf);
assert.deepEqual(legend.map((item) => item.userGuid), ["u2", "u3", "u1"]);
assert.equal(legend.find((item) => item.userGuid === "u1")?.shifts, 2);
assert.equal(legend.find((item) => item.userGuid === "u1")?.paidMinutes, 8 * 60);

// ---------- 可上班未排 ----------
const availabilityMap = groupAvailabilityByUserDate([
  avail("u1", "2026-11-16"),
  avail("u2", "2026-11-16"),
  avail("u4", "2026-11-16", "00:00", "23:59", true),
  avail("u3", "2026-11-17"),
]);
// u1 当天有班、u4 报了不能上班、u2 可上班无班
assert.deepEqual(findAvailableUnscheduled("2026-11-16", ["u1", "u2", "u3", "u4"], [s1], availabilityMap), ["u2"]);
assert.deepEqual(findAvailableUnscheduled("2026-11-17", ["u1", "u2", "u3"], [], availabilityMap), ["u3"]);
// 不在名单里的人（隐藏的其他店长）不算
assert.deepEqual(findAvailableUnscheduled("2026-11-17", ["u1"], [], availabilityMap), []);

// ---------- 相对周数 ----------
assert.equal(getRelativeWeekOffset("2026-11-16", "2026-11-16"), 0);
assert.equal(getRelativeWeekOffset("2026-11-23", "2026-11-16"), 1);
assert.equal(getRelativeWeekOffset("2026-11-02", "2026-11-16"), -2);
assert.equal(getRelativeWeekOffset("2027-01-04", "2026-12-28"), 1);
assert.equal(getRelativeWeekOffset("2026-10-05", "2026-09-28"), 1); // 悉尼夏令时开始那周
assert.equal(getRelativeWeekOffset("bad", "2026-11-16"), 0);

// ---------- 员工 × 日期批量排班 ----------
const matrixAvailability = groupAvailabilityByUserDate([
  avail("u2", "2026-11-18", "10:00", "14:00"), // 只能 10–14，排 09–13 → 提醒
  avail("u3", "2026-11-16", "00:00", "23:59", true), // 不能上班 → 跳过
]);
const matrix = buildBatchMatrix({
  userGuids: ["u1", "u2", "u3"],
  dates: ["2026-11-16", "2026-11-18"],
  startTime: "09:00",
  endTime: "13:00",
  schedules: [s1, leave, shift("u2", "2026-11-16", "15:00", "17:00")],
  availabilityMap: matrixAvailability,
});
const stateOf = (userGuid: string, date: string) => matrix.find((cell) => cell.key === batchCellKey(userGuid, date));
assert.equal(matrix.length, 6);
assert.deepEqual([stateOf("u1", "2026-11-16")?.state, stateOf("u1", "2026-11-16")?.reason], ["skip", "existing"]);
assert.deepEqual([stateOf("u2", "2026-11-16")?.state, stateOf("u2", "2026-11-16")?.reason], ["skip", "existing"]); // 不重叠也跳过：已有班次
assert.deepEqual([stateOf("u3", "2026-11-16")?.state, stateOf("u3", "2026-11-16")?.reason], ["skip", "unavailable"]);
assert.equal(stateOf("u1", "2026-11-18")?.state, "new");
assert.equal(stateOf("u2", "2026-11-18")?.state, "warn");
assert.equal(stateOf("u3", "2026-11-18")?.state, "new");
assert.deepEqual(summarizeBatchMatrix(matrix), { total: 6, create: 3, warn: 1, skip: 3, excluded: 0 });
assert.deepEqual(batchMatrixTargets(matrix), [
  { userGuid: "u1", date: "2026-11-18" },
  { userGuid: "u2", date: "2026-11-18" },
  { userGuid: "u3", date: "2026-11-18" },
]);

// 请假那天跳过（原因 leave）
const leaveMatrix = buildBatchMatrix({ userGuids: ["u4"], dates: ["2026-11-16"], startTime: "09:00", endTime: "13:00", schedules: [leave], availabilityMap: new Map() });
assert.deepEqual([leaveMatrix[0].state, leaveMatrix[0].reason], ["skip", "leave"]);

// 点格子排除：只对 new / warn 生效，跳过的格子不受影响
const excludedMatrix = buildBatchMatrix({
  userGuids: ["u1", "u2"],
  dates: ["2026-11-16", "2026-11-18"],
  startTime: "09:00",
  endTime: "13:00",
  schedules: [s1],
  availabilityMap: new Map(),
  excludedKeys: new Set([batchCellKey("u2", "2026-11-18"), batchCellKey("u1", "2026-11-16")]),
});
assert.equal(excludedMatrix.find((cell) => cell.key === batchCellKey("u1", "2026-11-16"))?.excluded, false);
assert.deepEqual(summarizeBatchMatrix(excludedMatrix), { total: 4, create: 2, warn: 0, skip: 1, excluded: 1 });
assert.deepEqual(batchMatrixTargets(excludedMatrix).map((item) => `${item.userGuid}|${item.date}`), ["u1|2026-11-18", "u2|2026-11-16"]);

// 空选择
assert.deepEqual(summarizeBatchMatrix(buildBatchMatrix({ userGuids: [], dates: week, startTime: "09:00", endTime: "13:00", schedules: [], availabilityMap: new Map() })), { total: 0, create: 0, warn: 0, skip: 0, excluded: 0 });

console.log("schedule-board tests passed");
