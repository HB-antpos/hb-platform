// 员工带薪假申请的接口契约（规格 docs/plans/2026-10-21-attendance-payroll-leave.md 第 0.2、2 节）。
// 规则参数全部来自后端考勤设置；前端只做提示与预校验，最终以后端校验为准。

/** 三种带薪假；原有 AnnualLeave / SickLeave 仍是「其他请假（无薪）」。 */
export type PaidLeaveType = "PaidSick" | "PaidAnnual" | "AnnualCashOut";

export const PAID_LEAVE_TYPES: readonly PaidLeaveType[] = ["PaidSick", "PaidAnnual", "AnnualCashOut"];

export function isPaidLeaveType(value?: string): value is PaidLeaveType {
  return value === "PaidSick" || value === "PaidAnnual" || value === "AnnualCashOut";
}

/** 后端 AttendanceSettings 里与请假相关的参数；缺省值与规格第 1 节一致。 */
export interface AttendanceLeaveRules {
  /** 生病当天起几天内可提交带薪病假（含当天），默认 4。 */
  sickLeaveSubmitDays: number;
  /** 年假休息至少提前几周，默认 4。 */
  annualLeaveLeadWeeks: number;
  /** 年假休息单次最长几周，默认 2。 */
  annualLeaveMaxWeeks: number;
  /** 年假发放（照常上班）至少提前几天，默认 2。 */
  annualCashOutLeadDays: number;
  /** 年假发放单次最长几周，默认 4。 */
  annualCashOutMaxWeeks: number;
  /** 年假每个工作日计几小时，默认 8。 */
  annualLeaveHoursPerWorkday: number;
  /** 医嘱照片最多几张，默认 3。 */
  maxCertificatePhotos: number;
}

export const DEFAULT_ATTENDANCE_LEAVE_RULES: AttendanceLeaveRules = {
  sickLeaveSubmitDays: 4,
  annualLeaveLeadWeeks: 4,
  annualLeaveMaxWeeks: 2,
  annualCashOutLeadDays: 2,
  annualCashOutMaxWeeks: 4,
  annualLeaveHoursPerWorkday: 8,
  maxCertificatePhotos: 3,
};

/** 病假可选日：近 N 天内本人有排班的日子；已打卡的也返回，前端置灰并说明原因。 */
export interface AttendanceSickLeaveCandidate {
  scheduleGuid: string;
  workDate: string;
  startTime: string;
  endTime: string;
  /** 当天排班工时扣餐后的带薪小时（十进制，最多 2 位）。 */
  paidHours: number;
  hasPunch: boolean;
}

export interface AttendancePublicHolidayDate {
  date: string;
  name?: string;
}

/**
 * GET my/leave-options?storeCode — 规格第 2 节未列，移动端需要（员工读不到 settings 接口）。
 * 已在 PR 描述与共享笔记里请卡 B 补上。
 */
export interface AttendanceMyLeaveOptions {
  /** 门店时区的今天（YYYY-MM-DD），规则里「提前几天」都从它算。 */
  today: string;
  storeCode?: string;
  storeName?: string;
  employeeName?: string;
  /** FullTime / PartTime / Casual。 */
  employmentType?: string;
  /** 后端判定能否申请带薪假（临时工 false）；前端据此隐藏三张带薪假卡片。 */
  paidLeaveEligible: boolean;
  rules: AttendanceLeaveRules;
  /** 今天起若干周内的公共假期（年假工时不算公假）。 */
  publicHolidays: AttendancePublicHolidayDate[];
  /** 今天起若干周内本人已发布排班的日期（年假休息规则「这几天还没有已发布的排班」）。 */
  publishedScheduleDates: string[];
  sickLeaveCandidates: AttendanceSickLeaveCandidate[];
}
