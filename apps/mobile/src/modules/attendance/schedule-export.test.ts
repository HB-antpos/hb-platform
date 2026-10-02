import assert from "node:assert/strict";
import {
  buildScheduleExportTable,
  estimateTextWidth,
  fitTextToWidth,
  layoutScheduleExport,
  SCHEDULE_EXPORT_LAYOUT,
  scheduleExportFileName,
  scheduleExportRowHeight,
} from "./schedule-export";
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

const days = [
  "2026-09-28",
  "2026-09-29",
  "2026-09-30",
  "2026-10-01",
  "2026-10-02",
  "2026-10-03",
  "2026-10-04",
];

const table = buildScheduleExportTable(days, [
  {
    userGuid: "u1",
    employeeName: "Alice",
    schedules: [
      // 两班倒按开始时间排序，时间统一成 HH:mm
      schedule("u1", "2026-09-28T00:00:00", "14:00:00", "18:00:00"),
      schedule("u1", "2026-09-28", "08:00:00", "12:00:00"),
      // 已取消不显示
      schedule("u1", "2026-09-29", "09:00:00", "17:30:00", { status: "Cancelled" }),
      // 请假优先于班次
      schedule("u1", "2026-09-30", "09:00:00", "17:30:00"),
      schedule("u1", "2026-09-30", "00:00:00", "00:00:00", { leaveType: "AnnualLeave" }),
      // 周六 10 小时：默认扣 2 次用餐 → 9 小时
      schedule("u1", "2026-10-03", "08:00:00", "18:00:00"),
    ],
  },
  // 整周只有已取消班次：不进图片
  {
    userGuid: "u2",
    employeeName: "Bob",
    schedules: [schedule("u2", "2026-09-28", "09:00:00", "17:00:00", { status: "Cancelled" })],
  },
  // 没有任何班次的员工也不进图片
  { userGuid: "u3", employeeName: "Carol", schedules: [] },
  // 缺姓名时回退到 GUID
  { userGuid: "u4", schedules: [schedule("u4", "2026-09-28", "10:00", "16:00")] },
]);

assert.deepEqual(table.rows.map((row) => row.employeeName), ["Alice", "u4"]);
const [alice, fallback] = table.rows;
assert.deepEqual(alice.cells[0], {
  kind: "shift",
  shifts: [
    { startTime: "08:00", endTime: "12:00" },
    { startTime: "14:00", endTime: "18:00" },
  ],
});
assert.equal(alice.cells[1].kind, "rest");
assert.deepEqual(alice.cells[2], { kind: "leave", shifts: [], leaveType: "AnnualLeave" });
assert.equal(alice.cells[5].kind, "shift");
assert.deepEqual(fallback.cells[0], { kind: "shift", shifts: [{ startTime: "10:00", endTime: "16:00" }] });
// 在岗人数只数上班班次，请假与休息不算
assert.deepEqual(table.dayHeadcounts, [2, 0, 0, 0, 0, 1, 0]);

// 行高：当天最多两班 → 两段叠放
const L = SCHEDULE_EXPORT_LAYOUT;
assert.equal(scheduleExportRowHeight(alice), L.cellPaddingY * 2 + L.shiftHeight * 2 + L.shiftGap);
assert.equal(scheduleExportRowHeight(fallback), L.cellPaddingY * 2 + L.shiftHeight);

// 排版：行依次向下排，图片高度包住汇总行与页脚
const layout = layoutScheduleExport(table);
assert.equal(layout.tableWidth, L.nameWidth + L.dayWidth * 7);
assert.equal(layout.width, layout.tableWidth + L.padding * 2);
assert.equal(layout.rowTops[0], layout.tableTop + L.headerHeight);
assert.equal(layout.rowTops[1], layout.rowTops[0] + layout.rowHeights[0]);
assert.equal(layout.summaryTop, layout.rowTops[1] + layout.rowHeights[1]);
assert.equal(layout.height, layout.summaryTop + L.summaryHeight + L.footerHeight + L.padding);

// 空表也能排版（只有表头与汇总行）
const emptyLayout = layoutScheduleExport(buildScheduleExportTable(days, []));
assert.equal(emptyLayout.summaryTop, emptyLayout.tableTop + L.headerHeight);

// 文字宽度与截断：中文按 1em
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
