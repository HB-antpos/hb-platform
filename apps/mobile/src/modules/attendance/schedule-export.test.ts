import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import {
  buildScheduleExportBoard,
  estimateTextWidth,
  fitTextToWidth,
  isExportableShift,
  layoutScheduleExport,
  SCHEDULE_EXPORT_LAYOUT,
  scheduleExportFileName,
} from "./schedule-export";
import { FALLBACK_STAFF_COLOR, type StaffColor } from "./schedule-board";
import type { AttendanceSchedule } from "./types";

const schedule = (
  userGuid: string,
  workDate: string,
  startTime: string,
  endTime: string,
  extra: Partial<AttendanceSchedule> = {},
) => ({
  scheduleGuid: `${userGuid}-${workDate}-${startTime}`,
  storeCode: "BRI",
  userGuid,
  workDate,
  startTime,
  endTime,
  status: "Active",
  isMine: false,
  ...extra,
}) as AttendanceSchedule;

const days = ["2026-11-16", "2026-11-17", "2026-11-18", "2026-11-19", "2026-11-20", "2026-11-21", "2026-11-22"];
const names: Record<string, string> = { cody: "Cody", hunter: "Hunter", rylie: "Rylie", terry: "Terry", charlie: "Charlie" };
const cody: StaffColor = { bg: "#e8f0ff", bar: "#3b82f6", ink: "#1d4ed8" };
const colors: Record<string, StaffColor> = { cody };

const board = buildScheduleExportBoard({
  days,
  nameOf: (guid) => names[guid] ?? guid,
  colorOf: (guid) => colors[guid],
  schedules: [
    // 周一：同一天按「开始时间 → 显示名」排；Terry 和 Hunter 同为 09:00，按名字 Hunter 在前
    schedule("terry", "2026-11-16T00:00:00", "09:00:00", "17:30:00"),
    schedule("hunter", "2026-11-16", "09:00:00", "17:30:00"),
    schedule("cody", "2026-11-16", "08:00:00", "12:00:00"),
    // 同一人同一天两个班：算 1 人在岗，两张卡片
    schedule("cody", "2026-11-16", "14:00:00", "18:00:00"),
    // 周二：手动不扣的长班次 → 无午饭标识；短班次不标识；10 小时自动 2 次
    schedule("rylie", "2026-11-17", "09:00:00", "17:30:00", { mealBreakCount: 0 }),
    schedule("charlie", "2026-11-17", "15:00:00", "17:30:00"),
    schedule("terry", "2026-11-17", "08:00:00", "18:30:00"),
    // 不进图：草稿 / 已取消 / 请假 / 时间解析不了
    schedule("cody", "2026-11-18", "09:00:00", "17:30:00", { status: "Draft" }),
    schedule("hunter", "2026-11-18", "09:00:00", "17:30:00", { status: "Cancelled" }),
    schedule("terry", "2026-11-18", "00:00:00", "00:00:00", { leaveType: "AnnualLeave" }),
    schedule("rylie", "2026-11-18", "", ""),
    // 状态大小写不敏感
    schedule("hunter", "2026-11-19", "10:00", "16:00", { status: "active" }),
  ],
});

assert.deepEqual(board.days.map((day) => day.date), days, "7 天一一对应");
const [monday, tuesday, wednesday, thursday] = board.days;
assert.deepEqual(
  monday.cards.map((card) => `${card.startTime} ${card.name}`),
  ["08:00 Cody", "09:00 Hunter", "09:00 Terry", "14:00 Cody"],
  "按开始时间 → 显示名排序，时间统一成 HH:mm",
);
assert.equal(monday.headcount, 3, "同一人两个班只算 1 人在岗");
assert.equal(monday.cards[0].color, cody, "颜色取调用方给的员工色");
assert.equal(monday.cards[1].color, FALLBACK_STAFF_COLOR, "没有员工色时用中性灰");

// 午饭标识
assert.equal(monday.cards[1].meal.kind, "meal", "09:00–17:30 自动含午饭");
assert.equal(monday.cards[0].meal.kind, "hidden", "4 小时的短班次不标识");
const rylie = tuesday.cards.find((card) => card.name === "Rylie");
assert.equal(rylie?.meal.kind, "none", "长班次手动不扣 → 无午饭");
assert.equal(rylie?.meal.manual, true);
assert.equal(tuesday.cards.find((card) => card.name === "Charlie")?.meal.kind, "hidden", "2.5 小时不标识");
assert.equal(tuesday.cards.find((card) => card.startTime === "08:00")?.meal.count, 2, "超过 9 小时自动 2 次");
assert.equal(board.hasMeal, true);
assert.equal(board.hasNoMeal, true);

// 草稿 / 已取消 / 请假 / 解析不了的时间都不进图
assert.equal(wednesday.cards.length, 0, "周三的草稿、已取消、请假、脏数据都不导出");
assert.equal(wednesday.headcount, 0);
assert.deepEqual(thursday.cards.map((card) => card.name), ["Hunter"], "status 大小写不敏感");
assert.equal(board.peopleCount, 5, "整周有班次的人数：cody / hunter / terry / rylie / charlie");

// 谁能进图
assert.equal(isExportableShift(schedule("a", "2026-11-16", "09:00", "17:00")), true);
assert.equal(isExportableShift(schedule("a", "2026-11-16", "09:00", "17:00", { status: "Draft" })), false);
assert.equal(isExportableShift(schedule("a", "2026-11-16", "09:00", "17:00", { leaveType: "Sick" })), false);

// 没有任何午饭标识：图例不该出现
const plain = buildScheduleExportBoard({
  days, nameOf: (guid) => guid, colorOf: () => undefined,
  schedules: [schedule("a", "2026-11-16", "10:00", "13:00")],
});
assert.deepEqual([plain.hasMeal, plain.hasNoMeal], [false, false]);

// 昵称 / 全名：显示名完全由调用方的 nameOf 决定（跟随看板开关）
const full = buildScheduleExportBoard({
  days, nameOf: () => "Zhan Chen", colorOf: () => undefined,
  schedules: [schedule("terry", "2026-11-16", "09:00", "17:30")],
});
assert.equal(full.days[0].cards[0].name, "Zhan Chen");

// ---------- 隐私：模型里没有年龄（结构性保证，不是样式隐藏） ----------
const keysOf = (value: unknown, out = new Set<string>()): Set<string> => {
  if (Array.isArray(value)) value.forEach((item) => keysOf(item, out));
  else if (value && typeof value === "object") {
    Object.entries(value).forEach(([key, item]) => { out.add(key); keysOf(item, out); });
  }
  return out;
};
const modelKeys = [...keysOf(board)];
assert.ok(!modelKeys.some((key) => /age/i.test(key)), `导出模型不应有年龄字段，实际键：${modelKeys.join(",")}`);
// 源码层面也不引用年龄：导出模型与绘制面板里都不出现 age 标识符
for (const file of ["schedule-export.ts", "../../components/attendance/ScheduleExportSheet.tsx"]) {
  const source = readFileSync(resolve(__dirname, file), "utf8");
  assert.ok(!/\bage(Of)?\b/i.test(source.replace(/\/\*[\s\S]*?\*\/|\/\/.*$/gm, "")), `${file} 的代码里不应引用年龄`);
}

// ---------- 排版 ----------
const L = SCHEDULE_EXPORT_LAYOUT;
const layout = layoutScheduleExport(board);
assert.equal(layout.boardWidth, L.columnWidth * 7);
assert.equal(layout.width, layout.boardWidth + L.padding * 2);
assert.deepEqual(layout.columnLefts, days.map((_, index) => L.padding + index * L.columnWidth));
assert.equal(layout.cardWidth, L.columnWidth - L.columnPaddingX * 2);
assert.equal(layout.bodyTop, layout.boardTop + L.headerHeight);
// 所有列等高：取最多卡片的那天（周一 4 张）
const tallest = L.columnPaddingY * 2 + 4 * L.cardHeight + 3 * L.cardGap;
assert.equal(layout.bodyHeight, tallest);
assert.equal(layout.boardBottom, layout.bodyTop + tallest);
assert.equal(layout.height, layout.boardBottom + L.footerHeight + L.padding);
// 卡片依次向下排，间距固定
assert.equal(layout.cardTops[0][0], layout.bodyTop + L.columnPaddingY);
assert.equal(layout.cardTops[0][1], layout.cardTops[0][0] + L.cardHeight + L.cardGap);
assert.equal(layout.cardTops[2].length, 0, "没人的那天没有卡片");
// 空周：列也有最小高度，整张图不塌
const emptyLayout = layoutScheduleExport(buildScheduleExportBoard({ days, schedules: [], nameOf: (guid) => guid, colorOf: () => undefined }));
assert.equal(emptyLayout.bodyHeight, L.minBodyHeight);
// 卡片宽度要放得下「09:00–17:30」一行 + 右侧午饭标识（含 ×2 最宽 30 单位，右边距 5），两者之间留 ≥ 6 单位空隙。
// estimateTextWidth 是截断用的保守估算（每字符 0.6em，对等宽数字偏大），这里按真实字宽算：
// 8 个数字约 0.56em、2 个冒号约 0.28em、1 个破折号约 0.5em，字号 12。
const realTimeWidth = (8 * 0.56 + 2 * 0.28 + 0.5) * 12;
assert.ok(realTimeWidth + 12 + 30 + 5 + 6 < layout.cardWidth, "时间一行 + 右侧午饭标识（含 ×2）放得下且留有空隙");

// ---------- 文字宽度与截断：中文按 1em ----------
assert.equal(estimateTextWidth("张三", 14), 28);
assert.equal(estimateTextWidth("Ab", 10), 12);
assert.equal(fitTextToWidth("张三", 96, 14), "张三");
const truncated = fitTextToWidth("Christopher Alexander", 96, 14);
assert.ok(truncated.endsWith("…"));
assert.ok(estimateTextWidth(truncated, 14) <= 96);

// 文件名只保留 ASCII 安全字符
assert.equal(scheduleExportFileName("BRI", "2026-09-28"), "roster-BRI-2026-09-28.png");
assert.equal(scheduleExportFileName("店/01", ""), "roster-01-week.png");
assert.equal(scheduleExportFileName(undefined, "2026-09-28"), "roster-store-2026-09-28.png");

console.log("schedule-export tests passed");
