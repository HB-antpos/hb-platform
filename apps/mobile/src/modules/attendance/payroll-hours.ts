// 店长「工时统计」的纯函数：周期日期计算、接口归一化、按周分组、某天的显示状态与小时格式化。
// 工时由后端算好（规格第 3 节），这里只做展示所需的合计兜底与排版，不重算计薪口径。
import {
  asBoolean,
  asDateOnly,
  asNumber,
  asOptionalNumber,
  asOptionalString,
  asRecordArray,
  asString,
  asStringArray,
  isRecord,
  pick,
  type RawRecord,
} from "./raw-fields";
import type {
  PayrollDayPending,
  PayrollDaySegment,
  PayrollHoursAlert,
  PayrollHoursDay,
  PayrollHoursEmployee,
  PayrollHoursQuery,
  PayrollHoursReport,
  PayrollHoursTotals,
  PayrollTierHours,
} from "./payroll-hours-types";

const DAY_MS = 24 * 60 * 60 * 1000;

// ───────────────────────── 日期（一律按 YYYY-MM-DD 的 UTC 午夜计算，避开设备时区与夏令时） ─────────────────────────

function toUtcMs(date: string): number {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(date);
  if (!match) return Number.NaN;
  return Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
}

function fromUtcMs(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10);
}

export function isValidDate(date: string | undefined | null): date is string {
  return typeof date === "string" && !Number.isNaN(toUtcMs(date));
}

export function addDays(date: string, days: number): string {
  const ms = toUtcMs(date);
  return Number.isNaN(ms) ? date : fromUtcMs(ms + days * DAY_MS);
}

/** 设备本地「今天」，只用来决定「下一期」按钮是否可点。 */
export function localToday(now: Date = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, "0");
  const day = String(now.getDate()).padStart(2, "0");
  return `${now.getFullYear()}-${month}-${day}`;
}

/** ISO 星期：周一 = 1 … 周日 = 7。 */
export function isoWeekday(date: string): number {
  const ms = toUtcMs(date);
  if (Number.isNaN(ms)) return 0;
  const day = new Date(ms).getUTCDay();
  return day === 0 ? 7 : day;
}

export function isWeekendDate(date: string): boolean {
  return isoWeekday(date) >= 6;
}

/** 所在周的周一（统计周起点是周一）。 */
export function mondayOf(date: string): string {
  const weekday = isoWeekday(date);
  return weekday ? addDays(date, 1 - weekday) : date;
}

/** ISO 8601 周序号：所在周的周四落在哪一年，就是那一年的第几周。 */
export function isoWeekNumber(date: string): number {
  const ms = toUtcMs(date);
  if (Number.isNaN(ms)) return 0;
  const thursday = toUtcMs(addDays(mondayOf(date), 3));
  const yearStart = Date.UTC(new Date(thursday).getUTCFullYear(), 0, 1);
  return Math.floor((thursday - yearStart) / DAY_MS / 7) + 1;
}

export function periodEndOf(periodStart: string, weeks: number): string {
  return addDays(periodStart, weeks * 7 - 1);
}

/** 翻页：以后端返回的 periodStart 为准，按「周数 × 7 天」前后步进（2 周时自然保持双周对齐）。 */
export function shiftPeriodStart(periodStart: string, weeks: number, direction: -1 | 1): string {
  return addDays(periodStart, direction * weeks * 7);
}

/**
 * 1 周 / 2 周切换时下一次请求的 periodStart：
 * - 1 → 2：传当前周起点，后端把它对齐到包含这一周的双周（与「双周起始日」一致）；
 * - 2 → 1：优先看双周里的第二周；第二周还没开始（在今天之后）时退回第一周。
 */
export function periodStartForWeeksSwitch(input: {
  periodStart: string;
  fromWeeks: 1 | 2;
  toWeeks: 1 | 2;
  today: string;
}): string {
  const { periodStart, fromWeeks, toWeeks, today } = input;
  if (fromWeeks === toWeeks || toWeeks === 2) return periodStart;
  const secondWeek = addDays(periodStart, 7);
  return secondWeek <= today ? secondWeek : periodStart;
}

/** 周期内每一天（升序）。 */
export function periodDates(periodStart: string, weeks: number): string[] {
  if (!isValidDate(periodStart)) return [];
  return Array.from({ length: weeks * 7 }, (_, index) => addDays(periodStart, index));
}

/** 周期条标题用：第一周与最后一周的周序号。 */
export function periodWeekNumbers(periodStart: string, weeks: number): { first: number; last: number } {
  const first = isoWeekNumber(periodStart);
  return { first, last: weeks > 1 ? isoWeekNumber(addDays(periodStart, (weeks - 1) * 7)) : first };
}

/** 短日期：中文「10-05」，英文按澳洲习惯日在前「5/10」。 */
export function formatShortDate(date: string, language: string): string {
  if (!isValidDate(date)) return date;
  const month = date.slice(5, 7);
  const day = date.slice(8, 10);
  return language.toLowerCase().startsWith("zh") ? `${month}-${day}` : `${Number(day)}/${Number(month)}`;
}

// ───────────────────────── 小时 ─────────────────────────

export function roundHours(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.round((value + Number.EPSILON * Math.sign(value)) * 100) / 100;
}

/** 十进制小时：最多 2 位小数并去掉尾零（7.60 → 7.6，8.00 → 8）。 */
export function formatHours(value: number): string {
  const rounded = roundHours(value);
  return String(Object.is(rounded, -0) ? 0 : rounded);
}

export const EMPTY_TIER_HOURS: PayrollTierHours = { weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0 };

export function addTierHours(a: PayrollTierHours, b: PayrollTierHours): PayrollTierHours {
  return {
    weekday: roundHours(a.weekday + b.weekday),
    saturday: roundHours(a.saturday + b.saturday),
    sunday: roundHours(a.sunday + b.sunday),
    publicHoliday: roundHours(a.publicHoliday + b.publicHoliday),
  };
}

export function weekendHoursOf(byTier: PayrollTierHours): number {
  return roundHours(byTier.saturday + byTier.sunday);
}

// ───────────────────────── 带薪假与待审 ─────────────────────────

/** 计入「带薪假」合计的假别；原有 SickLeave / AnnualLeave 是无薪的「其他请假」，不计。 */
const PAID_LEAVE_TYPES = new Set(["PaidSick", "PaidAnnual", "AnnualCashOut"]);

export function isPaidLeaveHoursType(type: string | undefined): boolean {
  return Boolean(type && PAID_LEAVE_TYPES.has(type));
}

export function isSickLeaveType(type: string | undefined): boolean {
  return type === "PaidSick" || type === "SickLeave";
}

/** 休息型年假（当天不上班）；「年假发放」照常上班，只打角标。 */
export function isAnnualRestLeaveType(type: string | undefined): boolean {
  return type === "PaidAnnual" || type === "AnnualLeave";
}

export function isCashOutDay(day: PayrollHoursDay): boolean {
  return day.leave?.type === "AnnualCashOut" || day.approvedMarks.includes("AnnualCashOut");
}

/** 当天计入带薪假的小时：带薪假别的工时 + 已确认的带薪公假工时。 */
export function dayPaidLeaveHours(day: PayrollHoursDay): number {
  const leave = day.leave && isPaidLeaveHoursType(day.leave.type) ? day.leave.hours : 0;
  const holiday = day.publicHolidayPay?.state === "Confirmed" ? (day.publicHolidayPay.hours ?? 0) : 0;
  return roundHours(leave + holiday);
}

/** 待审事项类型 → 文案 key（attendanceHours:pending.*）。 */
export function pendingLabelKey(type: string): string {
  switch (type) {
    case "Late":
    case "LateArrival":
      return "late";
    case "Punch":
      return "punch";
    case "PunchAdjustment":
      return "punchAdjustment";
    case "Overtime":
      return "overtime";
    case "MissingClockOut":
      return "missingClockOut";
    case "MealBreak":
      return "mealBreak";
    case "Leave":
      return "leave";
    case "PublicHolidayPay":
      return "publicHolidayPay";
    default:
      return "other";
  }
}

// ───────────────────────── 某天的显示状态 ─────────────────────────

export type PayrollDayKind =
  | "rest" // 没排班、没工时：虚线浅格 / 明细里压成「休息」细行
  | "unavailable" // 不能上班：斜纹
  | "sickLeave" // 病假：红色
  | "annualLeave" // 年假休息：紫色
  | "publicHoliday" // 公假当天没上班，有带薪公假（已确认或待确认）
  | "pending" // 没有计薪工时，但有待审事项（如待审补卡）
  | "scheduled" // 有排班但没有计薪工时
  | "worked"; // 有计薪工时

export interface PayrollDayDisplay {
  kind: PayrollDayKind;
  /** 格子 / 行右侧显示的小时（rest、unavailable 等为 0）。 */
  hours: number;
  isPending: boolean;
  isWeekend: boolean;
  isPublicHoliday: boolean;
  isCashOut: boolean;
  isBirthday: boolean;
}

/**
 * 判定某天怎么显示。优先级：有计薪工时 → worked；否则病假 / 年假休息 → 对应假别；
 * 公假带薪（含待确认）→ publicHoliday；不能上班 → unavailable；有待审 → pending；有排班 → scheduled；其余 rest。
 */
export function resolveDayDisplay(day: PayrollHoursDay): PayrollDayDisplay {
  const base = {
    isPending: day.pending.length > 0 || day.publicHolidayPay?.state === "Pending",
    isWeekend: isWeekendDate(day.date),
    isPublicHoliday: Boolean(day.isPublicHoliday || day.holidayName || day.publicHolidayPay),
    isCashOut: isCashOutDay(day),
    isBirthday: day.isBirthday,
  };
  if (day.paidHours > 0) return { ...base, kind: "worked", hours: day.paidHours };
  if (day.leave && isSickLeaveType(day.leave.type)) return { ...base, kind: "sickLeave", hours: day.leave.hours };
  if (day.leave && isAnnualRestLeaveType(day.leave.type)) return { ...base, kind: "annualLeave", hours: day.leave.hours };
  if (day.publicHolidayPay && day.publicHolidayPay.state !== "NotApplicable") {
    return { ...base, kind: "publicHoliday", hours: day.publicHolidayPay.state === "Confirmed" ? (day.publicHolidayPay.hours ?? 0) : 0 };
  }
  if (day.isUnavailable) return { ...base, kind: "unavailable", hours: 0 };
  if (day.pending.length > 0) return { ...base, kind: "pending", hours: 0 };
  if (day.isScheduled) return { ...base, kind: "scheduled", hours: 0 };
  return { ...base, kind: "rest", hours: 0 };
}

/** 分档小计：平 · 六 · 日 · 公 · 假，只列非零项。 */
export type TierPartKey = "weekday" | "saturday" | "sunday" | "publicHoliday" | "leave";

export function tierSummaryParts(byTier: PayrollTierHours, leaveHours: number): { key: TierPartKey; hours: number }[] {
  const parts: { key: TierPartKey; hours: number }[] = [
    { key: "weekday", hours: byTier.weekday },
    { key: "saturday", hours: byTier.saturday },
    { key: "sunday", hours: byTier.sunday },
    { key: "publicHoliday", hours: byTier.publicHoliday },
    { key: "leave", hours: leaveHours },
  ];
  return parts.filter((part) => roundHours(part.hours) !== 0).map((part) => ({ ...part, hours: roundHours(part.hours) }));
}

// ───────────────────────── 按周分组 / 补齐日期 ─────────────────────────

export function emptyPayrollDay(date: string): PayrollHoursDay {
  return {
    date,
    paidHours: 0,
    byTier: { ...EMPTY_TIER_HOURS },
    pending: [],
    approvedMarks: [],
    otherStores: [],
    isBirthday: false,
  };
}

/** 按周期补齐每一天（后端漏给的日子当作休息），丢掉周期外的日子，保证格子数 = 周数 × 7。 */
export function fillPeriodDays(days: PayrollHoursDay[], periodStart: string, weeks: number): PayrollHoursDay[] {
  const byDate = new Map(days.map((day) => [day.date, day]));
  return periodDates(periodStart, weeks).map((date) => byDate.get(date) ?? emptyPayrollDay(date));
}

export interface PayrollWeekGroup {
  weekNumber: number;
  start: string;
  end: string;
  days: PayrollHoursDay[];
  /** 本周计薪工时合计（不含带薪假）。 */
  totalHours: number;
}

export function groupDaysByWeek(days: PayrollHoursDay[], periodStart: string, weeks: number): PayrollWeekGroup[] {
  const filled = fillPeriodDays(days, periodStart, weeks);
  return Array.from({ length: weeks }, (_, index) => {
    const start = addDays(periodStart, index * 7);
    const weekDays = filled.slice(index * 7, index * 7 + 7);
    return {
      weekNumber: isoWeekNumber(start),
      start,
      end: addDays(start, 6),
      days: weekDays,
      totalHours: roundHours(weekDays.reduce((sum, day) => sum + day.paidHours, 0)),
    };
  });
}

// ───────────────────────── 合计兜底 ─────────────────────────

export function summarizeEmployeeDays(days: PayrollHoursDay[]): Pick<PayrollHoursEmployee, "totalHours" | "byTier" | "leaveHours" | "pendingCount"> {
  let byTier: PayrollTierHours = { ...EMPTY_TIER_HOURS };
  let totalHours = 0;
  let leaveHours = 0;
  let pendingCount = 0;
  for (const day of days) {
    byTier = addTierHours(byTier, day.byTier);
    totalHours += day.paidHours;
    leaveHours += dayPaidLeaveHours(day);
    pendingCount += day.pending.length + (day.publicHolidayPay?.state === "Pending" ? 1 : 0);
  }
  return { totalHours: roundHours(totalHours), byTier, leaveHours: roundHours(leaveHours), pendingCount };
}

export function sumReportTotals(employees: PayrollHoursEmployee[]): PayrollHoursTotals {
  let paidHours = 0;
  let weekendHours = 0;
  let publicHolidayHours = 0;
  let leaveHours = 0;
  let pendingCount = 0;
  for (const employee of employees) {
    paidHours += employee.totalHours;
    weekendHours += weekendHoursOf(employee.byTier);
    publicHolidayHours += employee.byTier.publicHoliday;
    leaveHours += employee.leaveHours;
    pendingCount += employee.pendingCount;
  }
  return {
    paidHours: roundHours(paidHours),
    weekendHours: roundHours(weekendHours),
    publicHolidayHours: roundHours(publicHolidayHours),
    leaveHours: roundHours(leaveHours),
    pendingCount,
  };
}

// ───────────────────────── 头像 ─────────────────────────

/** 头像首字母：去掉「[临时]」之类的方括号前缀，取前两个词的首字母；中文名取第一个字。 */
export function employeeInitials(name: string): string {
  const cleaned = name.replace(/^\s*\[[^\]]*\]\s*/, "").trim();
  if (!cleaned) return "?";
  const words = cleaned.split(/\s+/).filter(Boolean);
  if (words.length >= 2 && /^[A-Za-z]/.test(words[0])) {
    return `${words[0][0]}${words[words.length - 1][0]}`.toUpperCase();
  }
  return Array.from(cleaned)[0]?.toUpperCase() ?? "?";
}

const AVATAR_COLORS = ["#6941C6", "#1570EF", "#0E9384", "#C11574", "#E04F16", "#3E4784", "#4E5BA6", "#099250"];

/** 按 userGuid 稳定取色，同一个人在列表与明细里颜色一致。 */
export function avatarColor(seed: string): string {
  let hash = 0;
  for (const char of seed) hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  return AVATAR_COLORS[hash % AVATAR_COLORS.length];
}

// ───────────────────────── 接口归一化 ─────────────────────────

function normalizeTier(raw: unknown): PayrollTierHours | undefined {
  if (!isRecord(raw)) return undefined;
  return {
    weekday: roundHours(asNumber(pick(raw, "weekday", "Weekday"))),
    saturday: roundHours(asNumber(pick(raw, "saturday", "Saturday"))),
    sunday: roundHours(asNumber(pick(raw, "sunday", "Sunday"))),
    publicHoliday: roundHours(asNumber(pick(raw, "publicHoliday", "PublicHoliday"))),
  };
}

function normalizePending(raw: unknown): PayrollDayPending[] {
  if (!Array.isArray(raw)) return [];
  return raw
    .map((item): PayrollDayPending | undefined => {
      if (typeof item === "string") return item.trim() ? { type: item.trim() } : undefined;
      if (!isRecord(item)) return undefined;
      const type = asOptionalString(pick(item, "type", "Type", "sourceType", "SourceType"));
      return type ? { type, sourceGuid: asOptionalString(pick(item, "sourceGuid", "SourceGuid")) } : undefined;
    })
    .filter((item): item is PayrollDayPending => Boolean(item));
}

function normalizeSegments(raw: unknown): PayrollDaySegment[] | undefined {
  if (!Array.isArray(raw)) return undefined;
  return asRecordArray(raw)
    .map((item) => ({
      start: asString(pick(item, "start", "Start")).slice(0, 5),
      // 后端午夜自动下班写成 "24:00"，界面按设计图显示为 00:00。
      end: asOptionalString(pick(item, "end", "End"))?.slice(0, 5).replace(/^24:00$/, "00:00"),
      isAutoClockOut: asBoolean(pick(item, "isAutoClockOut", "IsAutoClockOut", "isSystemAutoClockOut", "IsSystemAutoClockOut")),
    }))
    .filter((segment) => Boolean(segment.start));
}

function normalizeDay(raw: RawRecord): PayrollHoursDay | undefined {
  const date = asDateOnly(pick(raw, "date", "Date", "workDate", "WorkDate"));
  if (!isValidDate(date)) return undefined;
  const leaveRaw = pick(raw, "leave", "Leave");
  const holidayPayRaw = pick(raw, "publicHolidayPay", "PublicHolidayPay");
  const isScheduledRaw = pick(raw, "isScheduled", "IsScheduled", "hasSchedule", "HasSchedule");
  const isPublicHolidayRaw = pick(raw, "isPublicHoliday", "IsPublicHoliday");
  return {
    date,
    paidHours: roundHours(asNumber(pick(raw, "paidHours", "PaidHours"))),
    byTier: normalizeTier(pick(raw, "byTier", "ByTier")) ?? { ...EMPTY_TIER_HOURS },
    leave: isRecord(leaveRaw) && asOptionalString(pick(leaveRaw, "type", "Type"))
      ? {
          type: asString(pick(leaveRaw, "type", "Type")),
          hours: roundHours(asNumber(pick(leaveRaw, "hours", "Hours"))),
          hasCertificate: asBoolean(pick(leaveRaw, "hasCertificate", "HasCertificate")),
        }
      : undefined,
    publicHolidayPay: isRecord(holidayPayRaw) && asOptionalString(pick(holidayPayRaw, "state", "State", "status", "Status"))
      ? {
          state: asString(pick(holidayPayRaw, "state", "State", "status", "Status")),
          hours: asOptionalNumber(pick(holidayPayRaw, "hours", "Hours")),
          count: asOptionalNumber(pick(holidayPayRaw, "count", "Count", "ordinaryCount", "OrdinaryCount")),
        }
      : undefined,
    pending: normalizePending(pick(raw, "pending", "Pending")),
    approvedMarks: asStringArray(pick(raw, "approvedMarks", "ApprovedMarks")),
    otherStores: asRecordArray(pick(raw, "otherStores", "OtherStores"))
      .map((item) => ({
        storeCode: asString(pick(item, "storeCode", "StoreCode")),
        storeName: asOptionalString(pick(item, "storeName", "StoreName")),
        hours: roundHours(asNumber(pick(item, "hours", "Hours"))),
      }))
      .filter((item) => Boolean(item.storeCode)),
    isBirthday: asBoolean(pick(raw, "isBirthday", "IsBirthday")),
    isPublicHoliday: isPublicHolidayRaw === undefined ? undefined : asBoolean(isPublicHolidayRaw),
    holidayName: asOptionalString(pick(raw, "holidayName", "HolidayName")),
    isUnavailable: asBoolean(pick(raw, "isUnavailable", "IsUnavailable")),
    isScheduled: isScheduledRaw === undefined ? undefined : asBoolean(isScheduledRaw),
    segments: normalizeSegments(pick(raw, "segments", "Segments")),
  };
}

function normalizeEmployee(raw: RawRecord): PayrollHoursEmployee | undefined {
  const userGuid = asString(pick(raw, "userGuid", "UserGuid", "userGUID")).trim();
  if (!userGuid) return undefined;
  const days = asRecordArray(pick(raw, "days", "Days"))
    .map(normalizeDay)
    .filter((day): day is PayrollHoursDay => Boolean(day))
    .sort((a, b) => a.date.localeCompare(b.date));
  // 后端缺合计字段时由每天的数据兜底合计。
  const fallback = summarizeEmployeeDays(days);
  // 后端（卡 B）把员工合计放在 totals{weekday,saturday,sunday,publicHoliday,leave,total}，total 已含带薪假。
  const totalsRaw = pick(raw, "totals", "Totals");
  const totals = isRecord(totalsRaw) ? totalsRaw : undefined;
  const totalHours = asOptionalNumber(pick(raw, "totalHours", "TotalHours", "paidHours", "PaidHours"))
    ?? (totals ? asOptionalNumber(pick(totals, "total", "Total")) : undefined);
  const leaveHours = asOptionalNumber(pick(raw, "leaveHours", "LeaveHours"))
    ?? (totals ? asOptionalNumber(pick(totals, "leave", "Leave")) : undefined);
  const pendingCount = asOptionalNumber(pick(raw, "pendingCount", "PendingCount"));
  return {
    userGuid,
    name: asString(pick(raw, "name", "Name", "userName", "UserName", "displayName", "DisplayName")).trim() || userGuid,
    employmentType: asOptionalString(pick(raw, "employmentType", "EmploymentType", "employeeType", "EmployeeType")),
    ageYears: asOptionalNumber(pick(raw, "ageYears", "AgeYears")),
    birthday: asOptionalString(pick(raw, "birthday", "Birthday"))?.slice(0, 10),
    segmentLimit: asOptionalNumber(pick(raw, "segmentLimit", "SegmentLimit")),
    hasOtherStores: asBoolean(pick(raw, "hasOtherStores", "HasOtherStores")),
    isStoreManager: asBoolean(pick(raw, "isStoreManager", "IsStoreManager", "isManager", "IsManager")),
    totalHours: totalHours === undefined ? fallback.totalHours : roundHours(totalHours),
    byTier: normalizeTier(pick(raw, "byTier", "ByTier")) ?? normalizeTier(totals) ?? fallback.byTier,
    leaveHours: leaveHours === undefined ? fallback.leaveHours : roundHours(leaveHours),
    pendingCount: pendingCount === undefined ? fallback.pendingCount : Math.max(0, Math.round(pendingCount)),
    days,
  };
}

function normalizeAlert(raw: RawRecord): PayrollHoursAlert | undefined {
  const rawType = asOptionalString(pick(raw, "type", "Type"));
  if (!rawType) return undefined;
  // 后端只给 Birthday + isMinor；界面把未成年生日单独成句（提示核对时薪）。
  const type = rawType === "Birthday" && asBoolean(pick(raw, "isMinor", "IsMinor")) ? "MinorBirthday" : rawType;
  return {
    type,
    message: asOptionalString(pick(raw, "message", "Message")),
    userGuid: asOptionalString(pick(raw, "userGuid", "UserGuid")),
    employeeName: asOptionalString(pick(raw, "employeeName", "EmployeeName", "userName", "UserName", "name", "Name")),
    date: asOptionalString(pick(raw, "date", "Date"))?.slice(0, 10),
    ageYears: asOptionalNumber(pick(raw, "ageYears", "AgeYears")),
    count: asOptionalNumber(pick(raw, "count", "Count")),
  };
}

/**
 * 后端提醒 → 界面提醒：MultiStore 一条带 otherStoreItems，按人展开成 OtherStores；
 * PublicHolidayPay 每人每天一条，合并成一条并计数。
 */
export function normalizePayrollHoursAlerts(raw: unknown): PayrollHoursAlert[] {
  const result: PayrollHoursAlert[] = [];
  let holidayPayCount = 0;
  for (const item of asRecordArray(raw)) {
    const type = asOptionalString(pick(item, "type", "Type"));
    if (type === "MultiStore") {
      const seen = new Set<string>();
      for (const other of asRecordArray(pick(item, "otherStoreItems", "OtherStoreItems"))) {
        const userGuid = asOptionalString(pick(other, "userGuid", "UserGuid"));
        if (!userGuid || seen.has(userGuid)) continue;
        seen.add(userGuid);
        result.push({ type: "OtherStores", userGuid, employeeName: asOptionalString(pick(other, "userName", "UserName")) });
      }
      continue;
    }
    if (type === "PublicHolidayPay") {
      holidayPayCount += Math.max(1, Math.round(asNumber(pick(item, "count", "Count"), 1)));
      continue;
    }
    const alert = normalizeAlert(item);
    if (alert) result.push(alert);
  }
  if (holidayPayCount > 0) result.push({ type: "PublicHolidayPay", count: holidayPayCount });
  return result;
}

/** 接口归一化：字段缺省给安全值；totals 缺省时由员工合计；周数只认 1 / 2。 */
export function normalizePayrollHoursReport(raw: unknown, query: PayrollHoursQuery, today: string = localToday()): PayrollHoursReport {
  const record = isRecord(raw) ? raw : {};
  const weeksRaw = asNumber(pick(record, "weeks", "Weeks"), query.weeks);
  const weeks: 1 | 2 = weeksRaw === 2 ? 2 : 1;
  const employees = asRecordArray(pick(record, "employees", "Employees"))
    .map(normalizeEmployee)
    .filter((employee): employee is PayrollHoursEmployee => Boolean(employee));
  const startRaw = asDateOnly(pick(record, "periodStart", "PeriodStart"));
  const periodStart = isValidDate(startRaw)
    ? startRaw
    : isValidDate(query.periodStart)
      ? query.periodStart
      : employees[0]?.days[0]?.date ?? mondayOf(today);
  const endRaw = asDateOnly(pick(record, "periodEnd", "PeriodEnd"));
  const totalsRaw = pick(record, "totals", "Totals");
  const fallbackTotals = sumReportTotals(employees);
  // 后端合计形状：{weekday,saturday,sunday,publicHoliday,leave,total}（total 含带薪假），待审数在报表顶层 pendingCount。
  const backendWeekend = isRecord(totalsRaw) && pick(totalsRaw, "saturday", "Saturday") !== undefined
    ? asNumber(pick(totalsRaw, "saturday", "Saturday")) + asNumber(pick(totalsRaw, "sunday", "Sunday"))
    : undefined;
  const reportPending = asOptionalNumber(pick(record, "pendingCount", "PendingCount"));
  const totals: PayrollHoursTotals = isRecord(totalsRaw)
    ? {
        paidHours: roundHours(asNumber(pick(totalsRaw, "paidHours", "PaidHours", "total", "Total"), fallbackTotals.paidHours)),
        weekendHours: roundHours(asNumber(pick(totalsRaw, "weekendHours", "WeekendHours"), backendWeekend ?? fallbackTotals.weekendHours)),
        publicHolidayHours: roundHours(asNumber(pick(totalsRaw, "publicHolidayHours", "PublicHolidayHours", "publicHoliday", "PublicHoliday"), fallbackTotals.publicHolidayHours)),
        leaveHours: roundHours(asNumber(pick(totalsRaw, "leaveHours", "LeaveHours", "leave", "Leave"), fallbackTotals.leaveHours)),
        pendingCount: Math.max(0, Math.round(asNumber(pick(totalsRaw, "pendingCount", "PendingCount"), reportPending ?? fallbackTotals.pendingCount))),
      }
    : { ...fallbackTotals, pendingCount: reportPending ?? fallbackTotals.pendingCount };
  return {
    // 以请求的门店为准：页面用它判断缓存里的数据是不是当前门店的。
    storeCode: query.storeCode || asString(pick(record, "storeCode", "StoreCode")),
    storeName: asOptionalString(pick(record, "storeName", "StoreName")),
    periodStart,
    periodEnd: isValidDate(endRaw) ? endRaw : periodEndOf(periodStart, weeks),
    weeks,
    weekNumber: asOptionalNumber(pick(record, "weekNumber", "WeekNumber")) ?? isoWeekNumber(periodStart),
    periodStatus: asOptionalString(pick(record, "periodStatus", "PeriodStatus", "status", "Status")),
    totals,
    alerts: normalizePayrollHoursAlerts(pick(record, "alerts", "Alerts")),
    employees,
  };
}
