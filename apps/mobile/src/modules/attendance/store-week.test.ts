import assert from "node:assert/strict";
import {
  axisTicks,
  buildDayAxis,
  buildStoreWeekGrid,
  formatShiftRange,
  gridCellLabel,
  groupStoreWeekDays,
  legendPeople,
  normalizeStoreWeek,
  overlapMinutes,
  overlapRangesWithMine,
  overlapWithMine,
  pickStoreWeek,
  shortShiftRange,
  summarizeStoreWeek,
  trackPosition,
  uniqueDisplayNames,
  type StoreWeekShift,
} from "./store-week";

const WEEK = ["2026-11-16", "2026-11-17", "2026-11-18", "2026-11-19", "2026-11-20", "2026-11-21", "2026-11-22"];

const shift = (name: string, workDate: string, startTime: string, endTime: string, isMine = false, userGuid = name): StoreWeekShift => ({
  userGuid,
  name,
  isMine,
  workDate,
  startTime,
  endTime,
});

// ---------- 接口数据规整 ----------
const normalized = normalizeStoreWeek(
  {
    weekStartDate: "2026-11-16T00:00:00",
    stores: [
      {
        storeCode: "BRI",
        storeName: "Brisbane",
        published: true,
        shifts: [
          { userGuid: "u1", displayName: "Ken", fullName: "Ken Wong", workDate: "2026-11-16T00:00:00", startTime: "09:00:00", endTime: "17:00:00", isMine: false },
          { userGuid: "u2", displayName: "Mia", workDate: "2026-11-17", startTime: "10:00:00", endTime: "15:30:00", isMine: true },
          { userGuid: "u3", displayName: "Bad", workDate: "", startTime: "09:00:00", endTime: "17:00:00" },
          { userGuid: "", displayName: "NoGuid", workDate: "2026-11-17", startTime: "09:00:00", endTime: "17:00:00" },
        ],
      },
      { storeCode: "", published: true, shifts: [] },
    ],
  },
  "2026-11-09",
);
assert.equal(normalized.weekStartDate, "2026-11-16", "周一日期去掉时间部分");
assert.equal(normalized.stores.length, 1, "没有分店编码的条目丢弃");
assert.deepEqual(
  normalized.stores[0].shifts.map((item) => [item.name, item.workDate, item.startTime, item.endTime, item.isMine]),
  [
    ["Ken", "2026-11-16", "09:00", "17:00", false],
    ["Mia", "2026-11-17", "10:00", "15:30", true],
  ],
  "时间规整成 HH:mm；日期解析不出来或缺员工标识的脏数据丢弃",
);
assert.deepEqual(normalizeStoreWeek(undefined, "2026-11-16"), { weekStartDate: "2026-11-16", stores: [] }, "空响应不抛错");
assert.equal(normalizeStoreWeek({ stores: [{ storeCode: "BRI" }] }, "2026-11-16").stores[0].published, false, "缺 published 当未发布");

// ---------- 同名区分 ----------
const names = uniqueDisplayNames([
  { userGuid: "a", displayName: "Sam", fullName: "Sam Chen" },
  { userGuid: "b", displayName: "sam", fullName: "Sam Lee" },
  { userGuid: "c", displayName: "Ken" },
  { userGuid: "d", displayName: "Alex" },
  { userGuid: "e", displayName: "alex" },
]);
assert.equal(names.get("a"), "Sam Chen", "撞名时优先用全名区分");
assert.equal(names.get("b"), "Sam Lee");
assert.equal(names.get("c"), "Ken", "不撞名的保持短名");
assert.deepEqual([names.get("d"), names.get("e")], ["Alex 1", "alex 2"], "没有全名可区分时按 userGuid 顺序加序号");

// ---------- 选店 ----------
const week = {
  weekStartDate: "2026-11-16",
  stores: [
    { storeCode: "BRI", storeName: "Brisbane", published: true, shifts: [shift("Ken", "2026-11-16", "09:00", "17:00")] },
    { storeCode: "WAR", storeName: "Warners Bay", published: true, shifts: [shift("Mia", "2026-11-17", "09:00", "17:00", true)] },
  ],
};
assert.equal(pickStoreWeek(week, "bri")?.storeCode, "BRI", "指定分店忽略大小写");
assert.equal(pickStoreWeek(week, "NOPE"), undefined, "指定的分店不在列表里不乱选别家");
assert.equal(pickStoreWeek(week)?.storeCode, "WAR", "没指定时优先选我有班的店");
assert.equal(pickStoreWeek(undefined), undefined);

// ---------- 时间写法 ----------
assert.equal(formatShiftRange({ startTime: "09:00", endTime: "17:00" }), "09:00–17:00");
assert.equal(shortShiftRange({ startTime: "09:00", endTime: "17:00" }), "9–17");
assert.equal(shortShiftRange({ startTime: "10:00", endTime: "15:30" }), "10–15:30");

// ---------- 按天分组 ----------
const shifts = [
  shift("Tom", "2026-11-17", "12:00", "20:00"),
  shift("Ken", "2026-11-17", "09:00", "17:00"),
  shift("Mia", "2026-11-17", "10:00", "16:00", true),
  shift("Lena", "2026-11-17", "09:00", "15:00"),
  shift("Ken", "2026-11-19", "09:00", "12:00"),
  shift("Ken", "2026-11-19", "13:00", "17:00"),
  shift("Gone", "2026-11-30", "09:00", "17:00"),
];
const days = groupStoreWeekDays(WEEK, shifts);
assert.equal(days.length, 7);
assert.deepEqual(days[1].shifts.map((item) => item.name), ["Ken", "Lena", "Mia", "Tom"], "同一天按开始时间、再按姓名");
assert.equal(days[1].people, 4);
assert.deepEqual(days[1].span, { start: "09:00", end: "20:00" });
assert.equal(days[3].people, 1, "同一人一天两段只算 1 人");
assert.equal(days[3].shifts.length, 2);
assert.equal(days[0].people, 0);
assert.equal(days[0].span, undefined);
assert.ok(!days.some((day) => day.shifts.some((item) => item.name === "Gone")), "不在本周的脏数据丢弃");

// ---------- 人 × 周表格 ----------
const grid = buildStoreWeekGrid(WEEK, shifts);
assert.deepEqual(grid.map((row) => row.name), ["Mia", "Ken", "Lena", "Tom"], "我的一行置顶，其余按姓名；不在本周的脏数据不成行");
assert.equal(grid[0].isMine, true);
const kenRow = grid.find((row) => row.name === "Ken")!;
assert.equal(kenRow.shiftCount, 3);
assert.equal(kenRow.cells[1].length, 1);
assert.equal(kenRow.cells[3].length, 2, "一天两段在同一格里");
assert.equal(gridCellLabel(kenRow.cells[1]), "9–17");
assert.equal(gridCellLabel(kenRow.cells[3]), "9–12+", "多段写第一段加 +");
assert.equal(gridCellLabel(kenRow.cells[0]), "", "没班为空");

// ---------- 图例 ----------
assert.deepEqual(legendPeople(shifts.slice(0, 6)).map((item) => item.name), ["Mia", "Ken", "Lena", "Tom"], "我在最前，其余按姓名");

// ---------- 时间轴 ----------
assert.deepEqual(buildDayAxis([{ startTime: "09:00", endTime: "17:00" }, { startTime: "12:30", endTime: "20:15" }]), { start: 540, end: 1260 }, "向外取整点");
assert.deepEqual(buildDayAxis([{ startTime: "10:00", endTime: "12:00" }]), { start: 600, end: 1080 }, "至少 8 小时");
assert.deepEqual(buildDayAxis([]), { start: 480, end: 1080 }, "当天没人给一个默认轴");
const axis = { start: 480, end: 1260 }; // 08:00–21:00
const position = trackPosition({ startTime: "09:00", endTime: "17:00" }, axis);
assert.ok(Math.abs(position.left - (60 / 780) * 100) < 1e-9);
assert.ok(Math.abs(position.width - (480 / 780) * 100) < 1e-9);
assert.deepEqual(axisTicks({ start: 480, end: 1260 }).map((tick) => tick.label), ["08", "11", "14", "17", "20"]);
assert.equal(axisTicks({ start: 1320, end: 1800 })[1].label, "01", "跨午夜的刻度回到 0 点后继续");

// ---------- 与我同班 ----------
assert.equal(overlapMinutes({ startTime: "10:00", endTime: "18:00" }, { startTime: "09:00", endTime: "17:00" }), 420);
assert.equal(overlapMinutes({ startTime: "09:00", endTime: "12:00" }, { startTime: "12:00", endTime: "15:00" }), 0, "首尾相接不算重叠");
assert.equal(overlapMinutes({ startTime: "22:00", endTime: "02:00" }, { startTime: "23:00", endTime: "01:00" }), 120, "跨午夜也能算");
const mine = [shift("Mia", "2026-11-20", "10:00", "18:00", true)];
assert.equal(overlapWithMine(shift("Lena", "2026-11-20", "09:00", "17:00"), mine), 420);
assert.equal(overlapWithMine(shift("Tom", "2026-11-20", "12:00", "20:00"), mine), 360);
assert.equal(overlapWithMine(mine[0], mine), 0, "我自己不和自己比");
assert.equal(overlapWithMine(shift("Lena", "2026-11-20", "09:00", "17:00"), []), 0, "我当天没班为 0");

assert.deepEqual(
  overlapRangesWithMine(shift("Lena", "2026-11-20", "09:00", "17:00"), mine),
  [{ start: 600, end: 1020 }],
  "重叠区间用于弹层里画斜纹：10:00–17:00",
);
assert.deepEqual(overlapRangesWithMine(mine[0], mine), []);

// ---------- 概况 ----------
assert.deepEqual(summarizeStoreWeek(shifts.slice(0, 6)), { shiftCount: 6, peopleCount: 4 });

console.log("store-week tests passed");
