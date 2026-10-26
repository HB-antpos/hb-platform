// 店长「工时统计」接口契约：GET payroll/hours?storeCode&periodStart&weeks&includePending&mergeStores
// （规格第 2、3 节）。工时一律十进制小时、最多 2 位小数，由后端算好，前端只展示不重算。

export type PayrollTier = "weekday" | "saturday" | "sunday" | "publicHoliday";

export interface PayrollTierHours {
  weekday: number;
  saturday: number;
  sunday: number;
  publicHoliday: number;
}

/** 当天的带薪假：PaidSick / PaidAnnual / AnnualCashOut；hasCertificate 只对病假有意义。 */
export interface PayrollDayLeave {
  type: string;
  hours: number;
  hasCertificate: boolean;
}

/** 带薪公假确认（过去 10 周同一星期几排班 ≥ 7 次时生成）。 */
export interface PayrollPublicHolidayPay {
  state: "Pending" | "Confirmed" | "NotApplicable" | string;
  hours?: number;
  count?: number;
}

/** 当天未处理事项：PunchAdjustment / Overtime / MissingClockOut / MealBreak / Leave / PublicHolidayPay 等。 */
export interface PayrollDayPending {
  type: string;
  sourceGuid?: string;
}

/** 当天已闭合（或待审）的打卡段，门店本地 HH:mm。规格表未列，单人明细需要。 */
export interface PayrollDaySegment {
  start: string;
  end?: string;
  isAutoClockOut?: boolean;
}

export interface PayrollHoursDay {
  date: string;
  paidHours: number;
  byTier: PayrollTierHours;
  leave?: PayrollDayLeave;
  publicHolidayPay?: PayrollPublicHolidayPay;
  pending: PayrollDayPending[];
  /** 已批准的角标，如 AnnualCashOut（「发」）。 */
  approvedMarks: string[];
  otherStores: { storeCode: string; storeName?: string; hours: number }[];
  isBirthday: boolean;
  // ── 以下为移动端明细需要、规格表未列的补充字段（可空；缺省时按「休息」显示） ──
  isPublicHoliday?: boolean;
  holidayName?: string;
  /** 员工标了「不能上班」（含批准的年假休息）。 */
  isUnavailable?: boolean;
  /** 当天是否有有效排班。 */
  isScheduled?: boolean;
  segments?: PayrollDaySegment[];
}

export interface PayrollHoursEmployee {
  userGuid: string;
  name: string;
  /** FullTime / PartTime / Casual。 */
  employmentType?: string;
  /** 未成年才有意义：显示周岁。 */
  ageYears?: number;
  birthday?: string;
  segmentLimit?: number;
  hasOtherStores: boolean;
  isStoreManager?: boolean;
  totalHours: number;
  byTier: PayrollTierHours;
  leaveHours: number;
  pendingCount: number;
  days: PayrollHoursDay[];
}

/** 顶部提醒条：生日 / 未成年生日核对时薪 / 待审没算进去 / 带薪公假待确认 / 其他分店有打卡。 */
export interface PayrollHoursAlert {
  type: "Birthday" | "MinorBirthday" | "Pending" | "PublicHolidayPay" | "OtherStores" | string;
  message?: string;
  userGuid?: string;
  employeeName?: string;
  date?: string;
  ageYears?: number;
  count?: number;
}

export interface PayrollHoursTotals {
  paidHours: number;
  weekendHours: number;
  publicHolidayHours: number;
  leaveHours: number;
  pendingCount: number;
}

export interface PayrollHoursReport {
  storeCode: string;
  storeName?: string;
  periodStart: string;
  periodEnd: string;
  weeks: 1 | 2;
  /** ISO 周序号（第 42 周）；后端不给时前端按 periodStart 算。 */
  weekNumber?: number;
  periodStatus?: "Open" | "Submitted" | string;
  totals: PayrollHoursTotals;
  alerts: PayrollHoursAlert[];
  employees: PayrollHoursEmployee[];
}

export interface PayrollHoursQuery {
  storeCode: string;
  periodStart?: string;
  weeks: 1 | 2;
  includePending?: boolean;
  mergeStores?: boolean;
}
