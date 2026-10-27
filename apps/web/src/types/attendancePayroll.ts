// 工时统计、工资表、个人时薪表、审核工作台扩展的接口类型。
// 口径见 docs/plans/2026-10-21-attendance-payroll-leave.md 第 0、2、3 节；
// 字段对齐后端 BlazorApp.Shared/DTOs/AttendancePayrollDtos.cs 与 AttendanceDtos.cs（日期在服务层统一成 YYYY-MM-DD）。

/** 员工类型：全职 / 兼职 / 临时工（Casual 不能申请带薪假）。 */
export type AttendanceEmploymentType = 'FullTime' | 'PartTime' | 'Casual'

/** 发放方式：全转账 / 全现金 / 转账 + 现金。 */
export type AttendancePayMode = 'Transfer' | 'Cash' | 'Mixed'

/** 带薪假类型：病假、年假休息、年假发放（照常上班）。 */
export type AttendancePaidLeaveType = 'PaidSick' | 'PaidAnnual' | 'AnnualCashOut'

/** 工资里需要财务核定工时的年假类型。 */
export type AttendanceAnnualLeaveType = Extract<AttendancePaidLeaveType, 'PaidAnnual' | 'AnnualCashOut'>

export type AttendancePayPeriodStatus = 'Open' | 'Submitted'

export type AttendancePublicHolidayPayState = 'Pending' | 'Confirmed' | 'NotApplicable'

/** 时薪四档（转账）；工时也按这四档拆分。 */
export interface AttendanceTierHours {
  weekday: number
  saturday: number
  sunday: number
  publicHoliday: number
}

export interface AttendancePeriodQuery {
  storeCode: string
  /** 期内任一天（YYYY-MM-DD），后端对齐到期起点；缺省 = 今天所在期。 */
  periodStart?: string
  /** 缺省 = 考勤设置里的默认周数。 */
  weeks?: 1 | 2
  includePending?: boolean
  mergeStores?: boolean
}

// ===== 工时统计（GET payroll/hours）=====

export interface AttendanceHoursDayHeaderDto {
  date: string
  /** ISO 星期：1 = 周一 … 7 = 周日。 */
  isoWeekday: number
  isPublicHoliday: boolean
  holidayName?: string
}

export interface AttendanceHoursDayDto {
  date: string
  /** 当天计薪工时（十进制小时，2 位小数）；没上班为 null。 */
  paidHours: number | null
  /** 计薪工时按档拆分（同一天只会落在一个档）。 */
  byTier: AttendanceTierHours
  hasSchedule: boolean
  /** 员工标记了「不能上班」（含年假休息期间自动标记）。 */
  isUnavailable?: boolean
  leave?: {
    /** 旧的无薪请假（AnnualLeave / SickLeave）也会出现，工时为 0。 */
    type: AttendancePaidLeaveType | 'AnnualLeave' | 'SickLeave'
    /** 申请工时；年假最终发放由财务在工资表核定。 */
    hours: number
    hasCertificate?: boolean
    leaveGuid?: string
  }
  publicHolidayPay?: {
    state: AttendancePublicHolidayPayState
    hours?: number | null
    /** 过去 N 周同一星期几的排班次数。 */
    count: number
    approvalGuid?: string
  }
  /** 当天待处理事项的审批类型码（PunchAdjustment / Overtime / PublicHolidayPay …）。 */
  pending: string[]
  /** 当天已批准事项（显示 ✓）。 */
  approvedMarks: string[]
  otherStores: { storeCode: string; storeName?: string; hours: number }[]
  isBirthday?: boolean
  /** 当天班段（HH:mm；系统午夜自动下班 end = 24:00）。 */
  segments?: { start: string; end?: string | null; isAutoClockOut?: boolean }[]
}

export interface AttendanceHoursTotalsDto extends AttendanceTierHours {
  /** 带薪假申请工时（病假 + 年假 + 已确认公假 + 年假发放）。 */
  leave: number
  total: number
  sick: number
  annual: number
  cashOut: number
  publicHolidayPay: number
}

export interface AttendanceHoursEmployeeDto {
  userGuid: string
  userName: string
  /** 员工资料没填时为 null。 */
  employmentType?: AttendanceEmploymentType | null
  isManager?: boolean
  /** 未成年才返回周岁。 */
  ageYears?: number | null
  birthday?: string | null
  segmentLimit?: number
  hasOtherStores?: boolean
  pendingCount?: number
  days: AttendanceHoursDayDto[]
  /** 后端按分钟合计后再舍到 2 位；缺省时前端用各天相加兜底。 */
  totals?: AttendanceHoursTotalsDto
}

export type AttendanceHoursAlertKind = 'MultiStore' | 'PublicHolidayPay' | 'Birthday'

/** 提醒条：结构化字段，前端按 i18n 组句。 */
export interface AttendanceHoursAlertDto {
  type: AttendanceHoursAlertKind
  /** 多店：每条「谁、哪天、在哪家店、几小时」。 */
  otherStoreItems?: { userGuid: string; userName: string; date: string; storeCode: string; storeName?: string; hours: number }[]
  userGuid?: string
  userName?: string
  employmentType?: AttendanceEmploymentType | null
  date?: string
  /** 带薪公假：公假名、过去 N 周同一星期几的排班次数、规则阈值、建议工时、对应审批。 */
  holidayName?: string
  ordinaryCount?: number
  lookbackWeeks?: number
  minCount?: number
  hours?: number | null
  approvalGuid?: string
  /** 生日：满几岁、是否未成年（未成年提示核对时薪）。 */
  ageYears?: number | null
  isMinor?: boolean | null
}

export interface AttendanceHoursResultDto {
  storeCode: string
  periodStart: string
  periodEnd?: string
  weeks: 1 | 2
  weekNumber?: number
  periodStatus: AttendancePayPeriodStatus
  /** 本期待处理数（提交锁定前必须清零）。 */
  pendingCount: number
  days: AttendanceHoursDayHeaderDto[]
  employees: AttendanceHoursEmployeeDto[]
  alerts: AttendanceHoursAlertDto[]
}

// ===== 工资表（GET payroll/pay）=====

/** 转账装填与现金都按这五档记工时；leave = 带薪假（用平日转账时薪）。 */
export interface AttendancePayBuckets {
  sunday: number
  saturday: number
  leave: number
  publicHoliday: number
  weekday: number
}

export interface AttendanceLeaveApprovalDto {
  leaveType: AttendanceAnnualLeaveType
  requestedHours: number
  /** 财务核定工时；没有核定记录时等于申请工时。 */
  approvedHours: number
  isFinanceApproved?: boolean
}

/** 工资行告警码：没有时薪记录、缺转账 / 现金时薪、低于自填最低时薪。 */
export type AttendancePayWarningCode = 'NO_RATE' | 'MISSING_TRANSFER_RATE' | 'MISSING_CASH_RATE' | 'BELOW_MINIMUM_RATE'

export interface AttendancePayRowDto {
  userGuid: string
  userName: string
  employmentType?: AttendanceEmploymentType | null
  ageYears?: number | null
  /** 还没有时薪记录时为 null。 */
  payMode?: AttendancePayMode | null
  fixedTransferHoursPerWeek?: number
  /** 应付工时：四档实际工时 + 带薪假（年假按核定）。 */
  hours: AttendanceTierHours & { leave: number; total: number }
  /** annual / cashOut：后端总会返回，服务层在申请与核定都为 0 时去掉。 */
  leaveDetail: {
    sick: number
    publicHolidayPay: number
    annual?: AttendanceLeaveApprovalDto
    cashOut?: AttendanceLeaveApprovalDto
  }
  transfer: { buckets: AttendancePayBuckets; hours: number; amount: number }
  /** cash.amount 不含补贴。 */
  cash: { buckets: AttendancePayBuckets; hours: number; rate?: number | null; amount: number }
  allowance: number
  grossPay: number
  note?: string | null
  warnings?: (AttendancePayWarningCode | string)[]
}

export interface AttendancePayResultDto {
  storeCode: string
  periodStart: string
  periodEnd?: string
  weeks: 1 | 2
  periodStatus: AttendancePayPeriodStatus
  pendingCount: number
  submittedAt?: string | null
  submittedBy?: string | null
  rows: AttendancePayRowDto[]
  totalGrossPay?: number
}

// ===== 个人时薪表（GET / POST payroll/rates）=====

export interface AttendancePayRateDto {
  rateGuid?: string | null
  userGuid: string
  userName?: string | null
  employmentType?: AttendanceEmploymentType | null
  isManager?: boolean
  ageYears?: number | null
  birthday?: string | null
  /** 还没有时薪记录时为 null。 */
  payMode?: AttendancePayMode | null
  fixedTransferHoursPerWeek?: number
  transferWeekdayRate?: number | null
  transferSaturdayRate?: number | null
  transferSundayRate?: number | null
  transferPublicHolidayRate?: number | null
  /** 现金只有一个价，不分周末和公假。 */
  cashRate?: number | null
  cashAllowancePerPeriod: number
  minimumRate?: number | null
  effectiveFrom?: string | null
  remark?: string | null
  createdAt?: string | null
  createdBy?: string | null
  /** 未成年过了生日、时薪还是生日前设的：提示核对。 */
  birthdayReviewHint?: boolean
  /**
   * 当前生效记录里公假转账价与平日转账价不一致（公假价按规定永远等于平日价，历史记录不改，
   * 须新建一条时薪记录纠正）；全现金恒为 false。
   */
  publicHolidayRateMismatch?: boolean
}

/** POST payroll/rates：只新增一条生效记录，不改历史（重复生效日返回 RATE_EFFECTIVE_DATE_EXISTS）。 */
export interface SaveAttendancePayRatePayload {
  storeCode: string
  userGuid: string
  payMode: AttendancePayMode
  fixedTransferHoursPerWeek: number
  transferWeekdayRate?: number
  transferSaturdayRate?: number
  transferSundayRate?: number
  transferPublicHolidayRate?: number
  cashRate?: number
  cashAllowancePerPeriod: number
  minimumRate?: number
  effectiveFrom: string
  remark?: string
}

// ===== 期操作（PUT / POST payroll/periods/...）=====

/** periodStart 必须是合法期起点（后端返回的 periodStart），否则 INVALID_PERIOD。 */
export interface AttendancePeriodKey {
  storeCode: string
  periodStart: string
  weeks: 1 | 2
}

export interface SaveAttendancePayNotePayload extends AttendancePeriodKey {
  userGuid: string
  /** 空字符串 = 清除备注。 */
  note: string
}

export interface SaveAttendanceLeaveApprovalPayload extends AttendancePeriodKey {
  userGuid: string
  leaveType: AttendanceAnnualLeaveType
  approvedHours: number
}

export interface UnlockAttendancePeriodPayload extends AttendancePeriodKey {
  reason: string
}

// ===== 已删除排班（GET schedules/deleted、POST schedules/{guid}/restore）=====

export interface AttendanceDeletedScheduleDto {
  scheduleGuid: string
  storeCode: string
  userGuid: string
  userName?: string
  workDate: string
  startTime: string
  endTime: string
  deletedAt?: string
  deletedBy?: string
}

// ===== 审核工作台（卡 A：本人审核、卡 B：带薪假与公假）=====

/** 审核详情的当天时间线与工时：前端由当天考勤记录（records）推算，不是后端字段。 */
export interface AttendanceApprovalDayPreviewDto {
  scheduleStart?: string
  scheduleEnd?: string
  /** 现有已闭合班段（HH:mm；系统午夜自动下班 end = 24:00）。 */
  segments: { start: string; end: string; isSystemAutoClockOut?: boolean }[]
  /** 批准后会新增 / 替换成的班段。 */
  proposedSegments?: { start: string; end: string }[]
  segmentLimit?: number
  /** 当前实际 / 计薪工时。 */
  workedHours?: number
  paidHours?: number
  /** 批准后实际工时（补卡按本地推算、漏下班按后端补卡预览）。 */
  workedHoursAfter?: number
  /** 工作日所在时薪档。 */
  tier?: keyof AttendanceTierHours
  /** 补卡改的那张卡：第几段、原时间（HH:mm）、是否系统午夜自动下班（批准后同日漏下班自动结案）。 */
  replaced?: { segmentNumber: number; originalTime?: string; isSystemAutoClockOut: boolean }
}

export interface AttendanceApprovalLeaveDetailDto {
  leaveType: AttendancePaidLeaveType | 'AnnualLeave' | 'SickLeave'
  startDate: string
  endDate: string
  dayCount?: number
  paidHours?: number | null
  reason?: string | null
  attachmentUrls: string[]
  /** 病假所选排班（HH:mm）。 */
  schedules?: { scheduleGuid: string; workDate: string; startTime: string; endTime: string; paidHours: number; hasPunch: boolean }[]
  /** 请假区间内已有的有效排班（YYYY-MM-DD HH:mm-HH:mm）。 */
  scheduleConflicts?: string[]
}

export interface AttendanceApprovalPublicHolidayDto {
  holidayDate: string
  holidayName?: string | null
  ordinaryCount: number
  lookbackWeeks: number
  suggestedHours?: number | null
  hours?: number | null
  status?: string
}
