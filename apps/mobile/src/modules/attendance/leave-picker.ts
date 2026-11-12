// 「请哪种假」弹层与无薪请假表单的纯逻辑：把后端规则换算成「现在能请哪天」、两周日历的日期窗口、受影响的排班日。
// 只用 my/leave-options 已有字段（today / rules / sickLeaveCandidates / publishedScheduleDates），不读设备时间。
import { addDays, annualEarliestDate, applyCalendarTap, listSickLeaveCandidates, toDayNumber, weekdayIndex } from "./leave-rules";
import type { AttendanceMyLeaveOptions } from "./leave-types";
import type { AttendanceLeaveType } from "./types";

/** 两周日历一页 14 天，前后翻页也按 14 天。 */
export const LEAVE_CALENDAR_DAYS = 14;

/** 临时工（后端判定不能请带薪假）点「请假」直接打开无薪表单，不再先弹「请哪种假」。规则还没读到时不跳过。 */
export function shouldOpenTimeOffDirectly(options?: Pick<AttendanceMyLeaveOptions, "paidLeaveEligible">): boolean {
  return options?.paidLeaveEligible === false;
}

/** 某天所在周的周一（日历按周一开头）；无效日期原样返回。 */
export function mondayOf(date: string): string {
  const index = weekdayIndex(date);
  return index < 0 ? date : addDays(date, -index);
}

/** 从某个周一起连续 14 天。 */
export function twoWeekDates(monday: string): string[] {
  if (toDayNumber(monday) === undefined) return [];
  return Array.from({ length: LEAVE_CALENDAR_DAYS }, (_, offset) => addDays(monday, offset));
}

/**
 * 有排班的日子：今天起已发布的排班 + 近几天的病假候选（候选只含过去有排班的日子）。
 * 两者合起来，日历上过去和将来的排班都能打点。
 */
export function rosteredDateSet(
  options?: Pick<AttendanceMyLeaveOptions, "publishedScheduleDates" | "sickLeaveCandidates">,
): Set<string> {
  const dates = new Set<string>();
  for (const date of options?.publishedScheduleDates ?? []) dates.add(date.slice(0, 10));
  for (const candidate of options?.sickLeaveCandidates ?? []) dates.add(candidate.workDate.slice(0, 10));
  return dates;
}

/** 区间内（含首尾）有排班的日子，升序。 */
export function rosteredDatesInRange(rostered: ReadonlySet<string>, startDate: string, endDate: string): string[] {
  return [...rostered].filter((date) => date >= startDate && date <= endDate).sort();
}

export interface LeaveRangeSelection {
  startDate: string;
  /** 为空表示只点了起点、等第二下；按单日处理。 */
  endDate?: string;
}

/** 提交与显示用的区间：没点终点就是单日。 */
export function effectiveLeaveRange(selection: LeaveRangeSelection): { startDate: string; endDate: string } {
  const endDate = selection.endDate && selection.endDate >= selection.startDate ? selection.endDate : selection.startDate;
  return { startDate: selection.startDate, endDate };
}

/**
 * 日历点选：第一下选起点（单日），第二下选终点；已经选好一段时再点就重新开始。
 * 点在起点之前则换成新的起点。沿用年假页的 applyCalendarTap 规则。
 */
export function tapLeaveRange(selection: LeaveRangeSelection, date: string): LeaveRangeSelection {
  const next = applyCalendarTap(selection, date);
  return { startDate: next.startDate ?? date, endDate: next.endDate };
}

/** 无薪请假的两种原因，对应后端已有的两种类型（不改接口）：生病 → SickLeave，有事 → AnnualLeave。 */
export type UnpaidLeaveReason = "sick" | "personal";

export function unpaidLeaveTypeOf(reason: UnpaidLeaveReason): AttendanceLeaveType {
  return reason === "sick" ? "SickLeave" : "AnnualLeave";
}

export interface LeavePickerSummary {
  /** 现在还能申请带薪病假的排班日（期限内、有排班、没打卡），升序去重。 */
  sickDates: string[];
  /** 带薪年假（休息）最早可选的开始日。 */
  annualEarliest: string;
  /** 年假发放（照常上班）最早可选的开始日。 */
  cashOutEarliest: string;
}

/** 「请哪种假」每一项要写的具体日期，全部从后端规则和门店今天换算。 */
export function buildLeavePickerSummary(
  options: Pick<AttendanceMyLeaveOptions, "today" | "rules" | "sickLeaveCandidates">,
): LeavePickerSummary {
  const sickDates = [
    ...new Set(
      listSickLeaveCandidates(options.sickLeaveCandidates, options.today, options.rules)
        .filter((candidate) => candidate.state === "selectable")
        .map((candidate) => candidate.workDate.slice(0, 10)),
    ),
  ].sort();
  return {
    sickDates,
    annualEarliest: annualEarliestDate("PaidAnnual", options.today, options.rules),
    cashOutEarliest: annualEarliestDate("AnnualCashOut", options.today, options.rules),
  };
}

/** 日期列表只显示前几个，其余折成「+N」。 */
export function splitShownDates(dates: readonly string[], limit = 3): { shown: string[]; more: number } {
  return { shown: dates.slice(0, limit), more: Math.max(0, dates.length - limit) };
}
