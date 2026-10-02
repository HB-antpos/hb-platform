import { classifyScheduleGridCell, normalizeClockTime } from "./schedule-grid";
import type { AttendanceSchedule } from "./types";

/**
 * 排班导出图片：把店长排班周网格整理成「员工 × 7 天」的只读表格，
 * 再按固定的 SVG 坐标系（viewBox 单位）排版，供分享到微信等聊天工具。
 * 这里只放纯数据与排版计算，绘制在 ScheduleExportSheet 里完成。
 */

export type ScheduleExportCellKind = "shift" | "leave" | "rest";

export interface ScheduleExportShift {
  startTime: string;
  endTime: string;
}

export interface ScheduleExportCell {
  kind: ScheduleExportCellKind;
  /** 当天的上班班次（不含请假与已取消），按开始时间排序；仅 shift 有值。 */
  shifts: ScheduleExportShift[];
  /** 当天已批准请假的类型；仅 leave 有值。 */
  leaveType?: string;
}

export interface ScheduleExportRow {
  userGuid: string;
  employeeName: string;
  cells: ScheduleExportCell[];
}

export interface ScheduleExportTable {
  days: string[];
  rows: ScheduleExportRow[];
  /** 每天在岗人数：当天有上班班次的员工数（请假不算）。 */
  dayHeadcounts: number[];
}

export interface ScheduleExportSourceRow {
  userGuid: string;
  employeeName?: string;
  schedules: AttendanceSchedule[];
}

/**
 * 整理导出表格：沿用网格的单元格分类（请假优先于班次），不显示「可上班」标记；
 * 整周既无班次也无请假的员工不进图片，避免一屏「休」挤掉真正上班的人。
 */
export function buildScheduleExportTable(
  days: string[],
  sourceRows: ScheduleExportSourceRow[],
): ScheduleExportTable {
  const rows: ScheduleExportRow[] = [];
  sourceRows.forEach((source) => {
    const cells = days.map<ScheduleExportCell>((day) => {
      const cell = classifyScheduleGridCell(
        source.schedules.filter((item) => item.workDate.slice(0, 10) === day),
      );
      if (cell.kind === "leave") {
        return {
          kind: "leave",
          shifts: [],
          leaveType: cell.schedules.find((item) => item.leaveType)?.leaveType,
        };
      }
      if (cell.kind === "shift") {
        return {
          kind: "shift",
          shifts: cell.schedules.map((item) => ({
            startTime: normalizeClockTime(item.startTime),
            endTime: normalizeClockTime(item.endTime),
          })),
        };
      }
      return { kind: "rest", shifts: [] };
    });

    if (cells.every((cell) => cell.kind === "rest")) return;
    rows.push({
      userGuid: source.userGuid,
      employeeName: source.employeeName || source.userGuid,
      cells,
    });
  });

  return {
    days,
    rows,
    dayHeadcounts: days.map(
      (_, index) => rows.filter((row) => row.cells[index]?.kind === "shift").length,
    ),
  };
}

/** 图片排版常量（viewBox 单位）。列宽按手机全屏查看时两行时间仍可读来取。 */
export const SCHEDULE_EXPORT_LAYOUT = {
  padding: 20,
  titleHeight: 64,
  nameWidth: 112,
  dayWidth: 76,
  headerHeight: 44,
  cellPaddingY: 6,
  shiftHeight: 36,
  shiftGap: 4,
  summaryHeight: 36,
  footerHeight: 32,
} as const;

export interface ScheduleExportLayout {
  width: number;
  height: number;
  tableLeft: number;
  tableTop: number;
  tableWidth: number;
  /** 每行顶部 y 与行高，和 table.rows 一一对应。 */
  rowTops: number[];
  rowHeights: number[];
  summaryTop: number;
  tableBottom: number;
}

/** 行高随当天最多班次数增高：一天两班时两段时间上下叠放，不挤成一行。 */
export function scheduleExportRowHeight(row: ScheduleExportRow) {
  const { cellPaddingY, shiftHeight, shiftGap } = SCHEDULE_EXPORT_LAYOUT;
  const maxShifts = Math.max(1, ...row.cells.map((cell) => cell.shifts.length));
  return cellPaddingY * 2 + maxShifts * shiftHeight + (maxShifts - 1) * shiftGap;
}

export function layoutScheduleExport(table: ScheduleExportTable): ScheduleExportLayout {
  const L = SCHEDULE_EXPORT_LAYOUT;
  const tableWidth = L.nameWidth + L.dayWidth * table.days.length;
  const tableLeft = L.padding;
  const tableTop = L.padding + L.titleHeight;
  const rowTops: number[] = [];
  const rowHeights: number[] = [];
  let cursor = tableTop + L.headerHeight;
  table.rows.forEach((row) => {
    const height = scheduleExportRowHeight(row);
    rowTops.push(cursor);
    rowHeights.push(height);
    cursor += height;
  });
  const summaryTop = cursor;
  const tableBottom = summaryTop + L.summaryHeight;
  return {
    width: tableWidth + L.padding * 2,
    height: tableBottom + L.footerHeight + L.padding,
    tableLeft,
    tableTop,
    tableWidth,
    rowTops,
    rowHeights,
    summaryTop,
    tableBottom,
  };
}

/** 估算文字宽度：中日韩字符按 1em，其余按 0.6em，用于 SVG 文本截断（SVG 不会自动省略）。 */
export function estimateTextWidth(text: string, fontSize: number) {
  let units = 0;
  for (const char of text) {
    units += (char.codePointAt(0) ?? 0) >= 0x2e80 ? 1 : 0.6;
  }
  return units * fontSize;
}

/** 超出可用宽度时截断并补「…」。 */
export function fitTextToWidth(text: string, maxWidth: number, fontSize: number) {
  if (estimateTextWidth(text, fontSize) <= maxWidth) return text;
  const chars = Array.from(text);
  while (chars.length && estimateTextWidth(`${chars.join("")}…`, fontSize) > maxWidth) {
    chars.pop();
  }
  return `${chars.join("")}…`;
}

/** 分享文件名只用 ASCII：同一分店同一周反复导出直接覆盖缓存里的旧图。 */
export function scheduleExportFileName(storeCode: string | undefined, weekStartDate: string) {
  const safe = (value: string) => value.replace(/[^A-Za-z0-9-]+/g, "");
  return `roster-${safe(storeCode ?? "") || "store"}-${safe(weekStartDate) || "week"}.png`;
}
