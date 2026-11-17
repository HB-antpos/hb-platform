import { getMealMark, type MealMark } from "./meal-mark";
import { FALLBACK_STAFF_COLOR, type StaffColor } from "./schedule-board";
import { normalizeClockTime } from "./schedule-grid";
import type { AttendanceSchedule } from "./types";

/**
 * 排班导出图片（方案 A：按天分列的看板，与 Web 导出同一版式）：
 * 每天一列，列里是当天已发布的上班班次卡片（名字、时间、午饭标识），供分享到微信等聊天工具。
 * 这里只放纯数据与排版计算，绘制在 ScheduleExportSheet 里完成。
 *
 * 隐私：导出图面向全店员工，**模型里根本没有年龄**（也没有工时、锁、冲突、备注、打卡）——
 * 不是样式隐藏，而是类型里不存在这些字段，谁也翻不出来；新增字段前先想想要不要发到群里。
 */

export interface ScheduleExportCard {
  scheduleGuid: string;
  userGuid: string;
  /** 显示名：沿用看板当前的昵称 / 全名开关（由上层传入的 nameOf 决定）。 */
  name: string;
  /** HH:mm。 */
  startTime: string;
  endTime: string;
  /** 员工色：与屏幕看板同一套（按全名分配，切换昵称 / 全名不换色）。 */
  color: StaffColor;
  /** 有无午饭标识（与看板卡片同一判定）。 */
  meal: MealMark;
}

export interface ScheduleExportDay {
  /** YYYY-MM-DD。 */
  date: string;
  cards: ScheduleExportCard[];
  /** 当天在岗人数：有班次的员工数（同一人多个班次只算 1 人）。 */
  headcount: number;
}

export interface ScheduleExportBoard {
  days: ScheduleExportDay[];
  /** 整周有班次的员工数（页脚「共 N 人」）。 */
  peopleCount: number;
  /** 本周出现过「含午饭 / 无午饭」标识才在页脚放对应图例，免得图例说明一个图里没有的东西。 */
  hasMeal: boolean;
  hasNoMeal: boolean;
}

export interface ScheduleExportSource {
  /** 一周 7 天（YYYY-MM-DD，周一起）。 */
  days: readonly string[];
  schedules: readonly AttendanceSchedule[];
  nameOf: (userGuid: string) => string;
  colorOf: (userGuid: string) => StaffColor | undefined;
}

/** 能进图的班次：已发布（Active）的上班班次；请假、草稿、已取消都不进图。 */
export function isExportableShift(schedule: AttendanceSchedule) {
  return !schedule.leaveType && schedule.status.toLowerCase() === "active";
}

const nameCollator = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });

/**
 * 整理导出看板：按天分组，同一天按「开始时间 → 显示名」排序（与屏幕看板一致）。
 * 整周没有班次的员工自然不出现。
 */
export function buildScheduleExportBoard(source: ScheduleExportSource): ScheduleExportBoard {
  const people = new Set<string>();
  let hasMeal = false;
  let hasNoMeal = false;

  const days = source.days.map<ScheduleExportDay>((date) => {
    const cards = source.schedules
      .filter((schedule) => isExportableShift(schedule) && schedule.workDate.slice(0, 10) === date)
      .map<ScheduleExportCard>((schedule) => ({
        scheduleGuid: schedule.scheduleGuid,
        userGuid: schedule.userGuid,
        name: source.nameOf(schedule.userGuid),
        startTime: normalizeClockTime(schedule.startTime),
        endTime: normalizeClockTime(schedule.endTime),
        color: source.colorOf(schedule.userGuid) ?? FALLBACK_STAFF_COLOR,
        meal: getMealMark({
          startTime: schedule.startTime,
          endTime: schedule.endTime,
          mealBreakCount: schedule.mealBreakCount,
          status: schedule.status,
        }),
      }))
      // 时间解析不了的脏数据不进图（画出来是一张「–」的空卡片，没有意义）
      .filter((card) => card.startTime && card.endTime)
      .sort(
        (left, right) =>
          left.startTime.localeCompare(right.startTime)
          || nameCollator.compare(left.name, right.name)
          || left.userGuid.localeCompare(right.userGuid),
      );
    cards.forEach((card) => {
      people.add(card.userGuid);
      if (card.meal.kind === "meal") hasMeal = true;
      if (card.meal.kind === "none") hasNoMeal = true;
    });
    return { date, cards, headcount: new Set(cards.map((card) => card.userGuid)).size };
  });

  return { days, peopleCount: people.size, hasMeal, hasNoMeal };
}

/**
 * 图片排版常量（viewBox 单位）。7 列等宽：卡片宽 = 列宽 − 2 × 列内边距，
 * 时间一行「09:00–17:30」放得下，午饭标识（含 ×2 时最宽约 30）在时间行右侧且两者间留 ≥ 6 的空隙；分享出去的图按 2 倍以上分辨率截取，对方可放大看。
 */
export const SCHEDULE_EXPORT_LAYOUT = {
  padding: 20,
  titleHeight: 64,
  columnWidth: 132,
  headerHeight: 44,
  columnPaddingX: 6,
  columnPaddingY: 6,
  cardGap: 5,
  cardHeight: 40,
  /** 当天没人时列也保持的最小高度，整张图不会塌成一条。 */
  minBodyHeight: 56,
  footerHeight: 32,
} as const;

export interface ScheduleExportLayout {
  width: number;
  height: number;
  boardLeft: number;
  boardTop: number;
  boardWidth: number;
  /** 表头下沿 = 列内容区顶部。 */
  bodyTop: number;
  /** 所有列等高：取最多卡片的那一天。 */
  bodyHeight: number;
  boardBottom: number;
  cardWidth: number;
  /** 每列左边 x，与 board.days 一一对应。 */
  columnLefts: number[];
  /** 每天每张卡片的顶部 y。 */
  cardTops: number[][];
}

export function layoutScheduleExport(board: ScheduleExportBoard): ScheduleExportLayout {
  const L = SCHEDULE_EXPORT_LAYOUT;
  const boardWidth = L.columnWidth * board.days.length;
  const boardLeft = L.padding;
  const boardTop = L.padding + L.titleHeight;
  const bodyTop = boardTop + L.headerHeight;
  const maxCards = Math.max(0, ...board.days.map((day) => day.cards.length));
  const cardsHeight = maxCards ? maxCards * L.cardHeight + (maxCards - 1) * L.cardGap : 0;
  const bodyHeight = Math.max(L.minBodyHeight, L.columnPaddingY * 2 + cardsHeight);
  const boardBottom = bodyTop + bodyHeight;
  return {
    width: boardWidth + L.padding * 2,
    height: boardBottom + L.footerHeight + L.padding,
    boardLeft,
    boardTop,
    boardWidth,
    bodyTop,
    bodyHeight,
    boardBottom,
    cardWidth: L.columnWidth - L.columnPaddingX * 2,
    columnLefts: board.days.map((_, index) => boardLeft + index * L.columnWidth),
    cardTops: board.days.map((day) =>
      day.cards.map((_, index) => bodyTop + L.columnPaddingY + index * (L.cardHeight + L.cardGap)),
    ),
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
