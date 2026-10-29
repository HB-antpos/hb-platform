// 带薪假申请的纯规则函数（规格 docs/plans/2026-10-21-attendance-payroll-leave.md 第 0.2、3 节）。
// 只做界面提示与预校验：日期一律按 YYYY-MM-DD 字符串、以 UTC 零点换算天数，避免设备时区与夏令时把一天算成 23/25 小时。
// 「今天」统一取后端给的门店今天（options.today），不读设备时间；最终以后端校验为准。
import type { AttendanceLeaveRules, AttendanceSickLeaveCandidate, PaidLeaveType } from "./leave-types";

const DAY_MS = 24 * 60 * 60 * 1000;
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

/** YYYY-MM-DD → 自 1970-01-01 起的天序号；格式不对返回 undefined。 */
export function toDayNumber(value: string): number | undefined {
  const match = DATE_PATTERN.exec(value.slice(0, 10));
  if (!match) return undefined;
  const time = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return Number.isFinite(time) ? Math.round(time / DAY_MS) : undefined;
}

export function fromDayNumber(day: number): string {
  return new Date(day * DAY_MS).toISOString().slice(0, 10);
}

/** 日期加减天数；无效日期原样返回。 */
export function addDays(value: string, days: number): string {
  const day = toDayNumber(value);
  return day === undefined ? value : fromDayNumber(day + days);
}

/** to − from 的天数差（同一天为 0）；任一无效返回 NaN。 */
export function diffDays(from: string, to: string): number {
  const start = toDayNumber(from);
  const end = toDayNumber(to);
  return start === undefined || end === undefined ? Number.NaN : end - start;
}

/** 星期序号：0 = 周一 … 6 = 周日（日历按周一开头）。 */
export function weekdayIndex(value: string): number {
  const day = toDayNumber(value);
  if (day === undefined) return -1;
  // 1970-01-01 是周四（周一开头序号 3）。
  return (((day + 3) % 7) + 7) % 7;
}

export function isWeekend(value: string): boolean {
  const index = weekdayIndex(value);
  return index === 5 || index === 6;
}

/** 设备本地今天（仅在后端没给 today 时兜底用）。 */
export function localDateString(date = new Date()): string {
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${date.getFullYear()}-${month}-${day}`;
}

/** 小时统一舍到 2 位小数（规格 0.2：十进制小时，最多 2 位）。 */
export function roundHours(hours: number): number {
  return Number.isFinite(hours) ? Math.round(hours * 100) / 100 : 0;
}

/** 小时显示：5.5、7.98、72，不补尾零。 */
export function formatHours(hours: number): string {
  return String(roundHours(hours));
}

// ---------------------------------------------------------------- 带薪病假

/**
 * 病假最早可选日：今天往前 sickLeaveSubmitDays 天（生病当天起 N 天内提交）。
 * 设计图：今天 10-20、4 天 → 最早 10-16，10-15 及以前已超过 4 天。
 */
export function sickLeaveEarliestDate(today: string, rules: Pick<AttendanceLeaveRules, "sickLeaveSubmitDays">): string {
  return addDays(today, -Math.max(0, Math.floor(rules.sickLeaveSubmitDays)));
}

export type SickCandidateState = "selectable" | "worked" | "expired" | "future";

/** 一条排班能不能申请病假：已打卡 → 已上班；超出提交期限 → 过期；今天之后 → 还没到。 */
export function sickCandidateState(
  candidate: Pick<AttendanceSickLeaveCandidate, "workDate" | "hasPunch">,
  today: string,
  rules: Pick<AttendanceLeaveRules, "sickLeaveSubmitDays">,
): SickCandidateState {
  if (candidate.workDate > today) return "future";
  if (candidate.workDate < sickLeaveEarliestDate(today, rules)) return "expired";
  if (candidate.hasPunch) return "worked";
  return "selectable";
}

/**
 * 病假页要列出的排班：期限内（含已上班，置灰说明原因），按日期、开始时间升序。
 * 过期和未来的不列——页面顶部已提示「只列 MM-DD 以后有排班的日子」。
 */
export function listSickLeaveCandidates(
  candidates: AttendanceSickLeaveCandidate[],
  today: string,
  rules: Pick<AttendanceLeaveRules, "sickLeaveSubmitDays">,
): (AttendanceSickLeaveCandidate & { state: SickCandidateState })[] {
  return candidates
    .map((candidate) => ({ ...candidate, state: sickCandidateState(candidate, today, rules) }))
    .filter((candidate) => candidate.state === "selectable" || candidate.state === "worked")
    .sort((left, right) => left.workDate.localeCompare(right.workDate) || left.startTime.localeCompare(right.startTime));
}

/** 所选排班的带薪工时合计（各条已是扣餐后的小时）；只算可选的，防止状态变化后残留选中。 */
export function sumSickPaidHours(
  candidates: (AttendanceSickLeaveCandidate & { state?: SickCandidateState })[],
  selectedGuids: readonly string[],
): number {
  const selected = new Set(selectedGuids);
  const total = candidates
    .filter((candidate) => selected.has(candidate.scheduleGuid) && (candidate.state ?? "selectable") === "selectable")
    .reduce((sum, candidate) => sum + candidate.paidHours, 0);
  return roundHours(total);
}

/** 提交用：所选排班的最早 / 最晚日期。 */
export function sickLeaveDateRange(
  candidates: AttendanceSickLeaveCandidate[],
  selectedGuids: readonly string[],
): { startDate: string; endDate: string } | null {
  const selected = new Set(selectedGuids);
  const dates = candidates.filter((candidate) => selected.has(candidate.scheduleGuid)).map((candidate) => candidate.workDate).sort();
  return dates.length ? { startDate: dates[0], endDate: dates[dates.length - 1] } : null;
}

// ---------------------------------------------------------------- 带薪年假（休息 / 发放）

export type AnnualLeaveKind = Extract<PaidLeaveType, "PaidAnnual" | "AnnualCashOut">;

/** 至少提前几天：休息按周（4 周 = 28 天），发放按天。 */
export function annualLeadDays(kind: AnnualLeaveKind, rules: AttendanceLeaveRules): number {
  return kind === "PaidAnnual" ? rules.annualLeaveLeadWeeks * 7 : rules.annualCashOutLeadDays;
}

/** 单次最长几天（含首尾）。 */
export function annualMaxDays(kind: AnnualLeaveKind, rules: AttendanceLeaveRules): number {
  return (kind === "PaidAnnual" ? rules.annualLeaveMaxWeeks : rules.annualCashOutMaxWeeks) * 7;
}

/** 最早可选日 = 今天 + 提前量。 */
export function annualEarliestDate(kind: AnnualLeaveKind, today: string, rules: AttendanceLeaveRules): string {
  return addDays(today, annualLeadDays(kind, rules));
}

export interface LeaveDayBreakdown {
  /** 区间总天数（含首尾）。 */
  totalDays: number;
  /** 周一至周五且不是公共假期。 */
  workdays: number;
  /** 周六、周日（落在周末的公假算周末，不重复计）。 */
  weekendDays: number;
  /** 落在周一至周五的公共假期。 */
  holidayDays: number;
}

export function isPublicHoliday(date: string, holidayDates: ReadonlySet<string>): boolean {
  return holidayDates.has(date);
}

/** 区间内工作日 / 周末 / 公假天数；起止无效或倒置时全为 0。 */
export function countLeaveDays(startDate: string, endDate: string, holidayDates: ReadonlySet<string>): LeaveDayBreakdown {
  const start = toDayNumber(startDate);
  const end = toDayNumber(endDate);
  const result: LeaveDayBreakdown = { totalDays: 0, workdays: 0, weekendDays: 0, holidayDays: 0 };
  if (start === undefined || end === undefined || end < start) return result;
  for (let day = start; day <= end; day += 1) {
    const date = fromDayNumber(day);
    result.totalDays += 1;
    if (isWeekend(date)) result.weekendDays += 1;
    else if (holidayDates.has(date)) result.holidayDays += 1;
    else result.workdays += 1;
  }
  return result;
}

/** 年假申请工时 = 工作日 × 每工作日小时（规格第 3 节）。 */
export function annualRequestedHours(breakdown: Pick<LeaveDayBreakdown, "workdays">, rules: Pick<AttendanceLeaveRules, "annualLeaveHoursPerWorkday">): number {
  return roundHours(breakdown.workdays * rules.annualLeaveHoursPerWorkday);
}

export type AnnualCheckKey = "lead" | "maxLength" | "noPublishedSchedule";

export interface AnnualCheck {
  key: AnnualCheckKey;
  ok: boolean;
}

export interface AnnualRangeValidation {
  /** 起止都选了且不倒置。 */
  hasRange: boolean;
  /** 距今几天（起始日 − 今天）。 */
  daysFromToday: number;
  /** 共几天（含首尾）。 */
  totalDays: number;
  leadDays: number;
  maxDays: number;
  /** 区间内已发布排班的日期（只休息类型检查）。 */
  conflictDates: string[];
  checks: AnnualCheck[];
  breakdown: LeaveDayBreakdown;
  requestedHours: number;
  /** 全部检查通过且有可计工时，才允许提交。 */
  ok: boolean;
}

/**
 * 年假区间逐条检查：
 * - 提前量：起始日距今 ≥ 提前天数；
 * - 最长：共几天 ≤ 最长周数 × 7；
 * - 仅「休息」：区间内还没有已发布的排班（发放类型照常排班，不查）。
 */
export function validateAnnualRange(params: {
  kind: AnnualLeaveKind;
  startDate?: string;
  endDate?: string;
  today: string;
  rules: AttendanceLeaveRules;
  publicHolidays: readonly string[];
  publishedScheduleDates: readonly string[];
}): AnnualRangeValidation {
  const { kind, startDate, endDate, today, rules } = params;
  const leadDays = annualLeadDays(kind, rules);
  const maxDays = annualMaxDays(kind, rules);
  const start = startDate ?? "";
  const end = endDate ?? startDate ?? "";
  const hasRange = Boolean(startDate) && toDayNumber(start) !== undefined && toDayNumber(end) !== undefined && end >= start;
  const breakdown = hasRange
    ? countLeaveDays(start, end, new Set(params.publicHolidays))
    : { totalDays: 0, workdays: 0, weekendDays: 0, holidayDays: 0 };
  const daysFromToday = hasRange ? diffDays(today, start) : 0;
  const conflictDates = hasRange && kind === "PaidAnnual"
    ? [...new Set(params.publishedScheduleDates)].filter((date) => date >= start && date <= end).sort()
    : [];
  const checks: AnnualCheck[] = [
    { key: "lead", ok: hasRange && daysFromToday >= leadDays },
    { key: "maxLength", ok: hasRange && breakdown.totalDays <= maxDays },
  ];
  if (kind === "PaidAnnual") checks.push({ key: "noPublishedSchedule", ok: hasRange && conflictDates.length === 0 });
  const requestedHours = annualRequestedHours(breakdown, rules);
  return {
    hasRange,
    daysFromToday,
    totalDays: breakdown.totalDays,
    leadDays,
    maxDays,
    conflictDates,
    checks,
    breakdown,
    requestedHours,
    ok: hasRange && checks.every((check) => check.ok) && requestedHours > 0,
  };
}

/**
 * 日历点选：没有起点或已选完一段 → 重新从这天开始；点在起点之前 → 换起点；否则作为终点。
 * 返回新的 [start, end]，end 为空表示等第二下。
 */
export function applyCalendarTap(
  current: { startDate?: string; endDate?: string },
  date: string,
  target: "start" | "end" | "auto" = "auto",
): { startDate?: string; endDate?: string } {
  const { startDate, endDate } = current;
  if (target === "start") {
    return { startDate: date, endDate: endDate && endDate >= date ? endDate : undefined };
  }
  if (target === "end" && startDate) {
    return date >= startDate ? { startDate, endDate: date } : { startDate: date, endDate: undefined };
  }
  if (!startDate || endDate) return { startDate: date, endDate: undefined };
  if (date < startDate) return { startDate: date, endDate: undefined };
  return { startDate, endDate: date };
}

/** 月历格子：按周一开头补齐前后空位（null），每行 7 格。 */
export function buildMonthGrid(year: number, month: number): (string | null)[][] {
  const first = `${year}-${String(month).padStart(2, "0")}-01`;
  const firstDay = toDayNumber(first);
  if (firstDay === undefined) return [];
  const nextMonth = month === 12 ? `${year + 1}-01-01` : `${year}-${String(month + 1).padStart(2, "0")}-01`;
  const daysInMonth = (toDayNumber(nextMonth) ?? firstDay) - firstDay;
  const cells: (string | null)[] = Array.from({ length: weekdayIndex(first) }, () => null);
  for (let offset = 0; offset < daysInMonth; offset += 1) cells.push(fromDayNumber(firstDay + offset));
  while (cells.length % 7 !== 0) cells.push(null);
  const rows: (string | null)[][] = [];
  for (let index = 0; index < cells.length; index += 7) rows.push(cells.slice(index, index + 7));
  return rows;
}

/** 月份前后翻：返回 { year, month }。 */
export function shiftMonth(year: number, month: number, delta: number): { year: number; month: number } {
  const zeroBased = year * 12 + (month - 1) + delta;
  return { year: Math.floor(zeroBased / 12), month: (zeroBased % 12) + 1 };
}
