// 工时统计、工资表、个人时薪表、审核工作台扩展的接口类型。
// 口径与接口约定见 docs/plans/2026-10-21-attendance-payroll-leave.md 第 0、2、3 节；
// 后端（卡 A / 卡 B）合入前按这里的字段名 mock 开发，联调时以后端为准再对齐。

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
  /** 周期起点（周一，YYYY-MM-DD）。 */
  periodStart: string
  weeks: 1 | 2
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
    type: AttendancePaidLeaveType
    /** 申请工时；年假最终发放由财务在工资表核定。 */
    hours: number
    hasCertificate?: boolean
  }
  publicHolidayPay?: {
    state: AttendancePublicHolidayPayState
    hours?: number
    /** 过去 N 周同一星期几的排班次数。 */
    count: number
    approvalGuid?: string
  }
  /** 当天待处理事项（补卡、加班、迟到……），后端给类型码。 */
  pending: string[]
  /** 当天已批准事项（显示 ✓）。 */
  approvedMarks: string[]
  otherStores: { storeCode: string; storeName?: string; hours: number }[]
  isBirthday?: boolean
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
  employmentType: AttendanceEmploymentType
  isManager?: boolean
  /** 未成年才返回周岁。 */
  ageYears?: number
  birthday?: string
  segmentLimit?: number
  hasOtherStores?: boolean
  days: AttendanceHoursDayDto[]
  /** 后端按分钟合计后再舍到 2 位；旧数据缺省时前端用各天相加兜底。 */
  totals?: AttendanceHoursTotalsDto
}

export type AttendanceHoursAlertKind = 'MultiStore' | 'PublicHolidayPay' | 'Birthday'

export interface AttendanceHoursAlertDto {
  kind: AttendanceHoursAlertKind
  /** 多店：每条「谁、哪天、在哪家店、几小时」，前端拼成一句提示。 */
  otherStoreItems?: { userGuid: string; userName: string; date: string; storeCode: string; storeName?: string; hours: number }[]
  userGuid?: string
  userName?: string
  employmentType?: AttendanceEmploymentType
  date?: string
  /** 带薪公假：公假名、过去 N 周同一星期几的排班次数、规则阈值、建议工时、对应审批。 */
  holidayName?: string
  ordinaryCount?: number
  lookbackWeeks?: number
  minCount?: number
  hours?: number
  approvalGuid?: string
  /** 生日：满几岁、是否未成年（未成年提示核对时薪）。 */
  ageYears?: number
  isMinor?: boolean
}

export interface AttendanceHoursResultDto {
  storeCode: string
  periodStart: string
  weeks: 1 | 2
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
  /** 财务核定工时；没有记录时等于申请工时。 */
  approvedHours: number
  approvedBy?: string
  approvedAt?: string
}

export interface AttendancePayRowDto {
  userGuid: string
  userName: string
  employmentType: AttendanceEmploymentType
  ageYears?: number
  payMode: AttendancePayMode
  fixedTransferHoursPerWeek?: number
  /** 应付工时：四档实际工时 + 带薪假（年假按核定）。 */
  hours: AttendanceTierHours & { leave: number; total: number }
  leaveDetail: {
    sick: number
    publicHolidayPay: number
    annual?: AttendanceLeaveApprovalDto
    cashOut?: AttendanceLeaveApprovalDto
  }
  transfer: { buckets: AttendancePayBuckets; hours: number; amount: number }
  cash: { buckets: AttendancePayBuckets; hours: number; rate?: number; amount: number }
  allowance: number
  grossPay: number
  note?: string
  /** 某档时薪缺失等无法算钱的情况，后端返回原因码。 */
  warnings?: string[]
}

export interface AttendancePayResultDto {
  storeCode: string
  periodStart: string
  weeks: 1 | 2
  periodStatus: AttendancePayPeriodStatus
  pendingCount: number
  submittedAt?: string
  submittedBy?: string
  rows: AttendancePayRowDto[]
}

// ===== 个人时薪表（GET / POST payroll/rates）=====

export interface AttendancePayRateDto {
  rateGuid?: string
  userGuid: string
  userName?: string
  employmentType?: AttendanceEmploymentType
  isManager?: boolean
  ageYears?: number
  birthday?: string
  payMode: AttendancePayMode
  fixedTransferHoursPerWeek?: number
  transferWeekdayRate?: number
  transferSaturdayRate?: number
  transferSundayRate?: number
  transferPublicHolidayRate?: number
  /** 现金只有一个价，不分周末和公假。 */
  cashRate?: number
  cashAllowancePerPeriod: number
  minimumRate?: number
  effectiveFrom?: string
  remark?: string
  createdAt?: string
  createdBy?: string
  /** 未成年过了生日、时薪还是旧年龄设的：提示核对。 */
  birthdayReviewHint?: boolean
}

export type SaveAttendancePayRatePayload = Omit<
  AttendancePayRateDto,
  'rateGuid' | 'userName' | 'employmentType' | 'isManager' | 'ageYears' | 'birthday' | 'createdAt' | 'createdBy' | 'birthdayReviewHint'
> & { effectiveFrom: string; storeCode?: string }

// ===== 期操作（PUT / POST payroll/periods/...）=====

export interface AttendancePeriodKey {
  storeCode: string
  periodStart: string
  weeks: 1 | 2
}

export interface SaveAttendancePayNotePayload extends AttendancePeriodKey {
  userGuid: string
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
  /** 删除时已有打卡的排班（软删除），恢复后重新对账。 */
  punchCount?: number
  deletedAt?: string
  deletedBy?: string
}

// ===== 审核工作台扩展（卡 A：本人审核、卡 B：带薪假与公假）=====

/** 审核详情的「批准后」预览：当天时间线与工时。 */
export interface AttendanceApprovalDayPreviewDto {
  scheduleStart?: string
  scheduleEnd?: string
  /** 现有已闭合班段（HH:mm）。 */
  segments: { start: string; end: string; isSystemAutoClockOut?: boolean }[]
  /** 批准后会新增 / 替换成的班段。 */
  proposedSegments?: { start: string; end: string }[]
  segmentLimit?: number
  workedHoursAfter?: number
  paidHoursAfter?: number
  /** 批准后工时所在时薪档。 */
  tier?: keyof AttendanceTierHours
  /** 后端解释（为什么不算加班、扣了几次餐）。 */
  explanation?: string
  /** 批准会连带结案的事项（如同日漏下班）。 */
  sideEffects?: { label: string; before: string; after: string }[]
}

export interface AttendanceApprovalLeaveDetailDto {
  leaveType: AttendancePaidLeaveType | 'AnnualLeave' | 'SickLeave'
  startDate: string
  endDate: string
  dayCount?: number
  paidHours?: number
  attachmentUrls: string[]
  /** 与已发布排班冲突的天（YYYY-MM-DD HH:mm-HH:mm）。 */
  scheduleConflicts?: string[]
}

export interface AttendanceApprovalPublicHolidayDto {
  holidayDate: string
  holidayName?: string
  ordinaryCount: number
  lookbackWeeks: number
  suggestedHours?: number
}
