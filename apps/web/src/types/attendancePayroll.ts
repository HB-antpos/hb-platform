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

/** 工时统计一个打卡班段：上班 / 下班时间与来源。 */
export interface AttendanceHoursSegmentDto {
  start: string
  end?: string | null
  isAutoClockOut?: boolean
  /** 上班打卡来源：PosQr 收银机扫码 / ManualAdjustment 店长修改 / ManualEntry 补录 / App …；旧版接口没有。 */
  inSource?: string | null
  /** 下班打卡来源。 */
  outSource?: string | null
  /** 上班打卡的收银机设备码（如 POS_1003_1308）；非收银机打卡为空。 */
  deviceCode?: string | null
}

/** 一个排班在格子上的明细：分钟数与工资计算用的是同一份结果。 */
export interface AttendanceHoursShiftDto {
  /** 排班窗口 HH:mm；跨午夜的班结束时刻小于开始时刻。 */
  scheduleStart: string
  scheduleEnd: string
  workedMinutes: number
  inScheduleMinutes: number
  mealDeductionMinutes: number
  mealAddBackMinutes: number
  overtimeMinutes: number
  paidMinutes: number
  /** 打卡班段；未打卡的班为空数组。 */
  segments: AttendanceHoursSegmentDto[]
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
    /** 店长补录建的假：留痕（原因 / 补录人 / 时间），「补」角标与撤销用；员工自己申请的没有。 */
    manualEntry?: AttendanceManualEntryTrace | null
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
  segments?: AttendanceHoursSegmentDto[]
  /** 当天每个排班的明细（排班窗口 / 打卡段与来源 / 用餐扣除 / 加班），气泡展示用；旧版接口没有这个字段。 */
  shifts?: AttendanceHoursShiftDto[]
  /** 有排班、排班窗口已过去、却一次打卡都没有（目前按 0 工时计）：可补录。 */
  missingPunch?: boolean
  /** 店长已确认缺勤（NoShow 待审已批准）：该班 0 工时，格子显示「缺勤」，此时 missingPunch 为 false。 */
  noShowConfirmed?: boolean
  /** 未打卡班的排班窗口（HH:mm），补录表单预填用；只在 missingPunch 时有值。 */
  scheduleStart?: string | null
  scheduleEnd?: string | null
  /** 这一天有手动补录的打卡：补录人 / 时间 / 原因（「补」角标与撤销用）。 */
  manualEntry?: AttendanceManualEntryTrace | null
}

/** 工时统计格子上「补」角标的留痕。 */
export interface AttendanceManualEntryTrace {
  reason?: string | null
  /** 补录人账号名。 */
  createdBy?: string | null
  createdAt?: string | null
  /** 这次补录同时新建了当天排班（撤销时一并取消）。 */
  createdSchedule?: boolean
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
  /** 员工昵称；页面显示为「全名（昵称）」。 */
  nickname?: string | null
  /** 员工资料没填时为 null。 */
  employmentType?: AttendanceEmploymentType | null
  isManager?: boolean
  /** 21 岁以下才返回周岁（青少年时薪按年龄计）。 */
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
  /** 生日：满几岁、是否 21 岁以下（合规文案用）。 */
  ageYears?: number | null
  isMinor?: boolean | null
  /** 生日当天年龄 ≤ 21（含刚满 21 岁转成人档）→ 要核对按年龄计的时薪；旧后端没有这个字段时退回 isMinor。 */
  rateReview?: boolean | null
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

/** 工资行告警码：没有时薪记录、缺转账 / 现金时薪、低于自填最低时薪、手填现金金额没生效。 */
export type AttendancePayWarningCode =
  | 'NO_RATE'
  | 'MISSING_TRANSFER_RATE'
  | 'MISSING_CASH_RATE'
  | 'BELOW_MINIMUM_RATE'
  | 'CASH_OVERRIDE_IGNORED'

/** 可以手填现金金额的两档，值与后端 PayrollBucket 枚举名一致。 */
export type AttendanceCashOverrideBucket = 'PublicHoliday' | 'Leave'

/** 公假 / 带薪假某一档的现金金额：默认是系统算的（现金工时 × 现金时薪），财务可以手填覆盖。 */
export interface AttendancePayCashBucketDto {
  /** 这一档落到现金的工时（转账装不下的部分）。 */
  hours: number
  /** 最终现金金额：已手填则为手填值，否则等于 systemAmount。 */
  amount: number
  /** 系统算的现金金额，不含手填。 */
  systemAmount: number
  /** 手填值是否已生效。 */
  isManual: boolean
  /** 库里存着的手填值（即使因为这一档已没有现金工时而不生效也会带出）；没有手填为 null。 */
  manualAmount?: number | null
  /** 这一档有现金工时才允许手填（工时全在转账里的档位没有现金部分）。 */
  editable: boolean
  updatedBy?: string | null
  updatedAt?: string | null
}

export interface AttendancePayRowDto {
  userGuid: string
  userName: string
  /** 员工昵称；页面显示为「全名（昵称）」。 */
  nickname?: string | null
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
  /** cash.amount 不含补贴，但含公假 / 带薪假两档的手填金额（手填取代该档的系统值）。 */
  cash: {
    buckets: AttendancePayBuckets
    hours: number
    rate?: number | null
    amount: number
    publicHoliday: AttendancePayCashBucketDto
    leave: AttendancePayCashBucketDto
  }
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

/** 分店侧栏：该店在所看工资周期是否已提交锁定。 */
export interface AttendancePayPeriodStoreStatusDto {
  storeCode: string
  locked: boolean
}

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
  /** 列表接口：所看周期（或今天）之后最近一条已录入、还没生效的时薪记录的生效日。 */
  nextEffectiveFrom?: string | null
  remark?: string | null
  createdAt?: string | null
  createdBy?: string | null
  /** 生日已过 60 天内（生日当天年龄 ≤ 21，含刚满 21 岁）、时薪还是生日前设的：提示核对。 */
  birthdayReviewHint?: boolean
  /** 需要核对时薪的那次生日（YYYY-MM-DD）与当天满的岁数；birthdayReviewHint 为 true 时才有值。 */
  birthdayReviewDate?: string | null
  birthdayReviewAge?: number | null
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

export interface SaveAttendanceCashOverridePayload extends AttendancePeriodKey {
  userGuid: string
  bucket: AttendanceCashOverrideBucket
  /** 手填的现金金额（≥ 0，最多两位小数）；null = 清除手填、恢复系统值。 */
  amount: number | null
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

// ===== 手动补录工时（POST /managed/manual-entries/preview | / | /revoke）=====

export interface AttendanceManualEntrySegmentPayload {
  /** 门店本地时间 YYYY-MM-DDTHH:mm:ss，必须在所选工作日当天。 */
  startLocal: string
  endLocal: string
}

export interface AttendanceManualEntryPayload {
  storeCode: string
  userGuid: string
  workDate: string
  segments: AttendanceManualEntrySegmentPayload[]
  /** 用餐次数覆盖值（0–3）；null = 按时长自动。只在需要新建排班时生效。 */
  mealBreakCount?: number | null
  reason: string
  /** 保存必须带回预览返回的版本号。 */
  previewRevision?: string
}

export interface AttendanceManualEntryPreview {
  previewRevision: string
  /** true = 当天没有排班，保存时会同时新建一条。 */
  willCreateSchedule: boolean
  scheduleGuid?: string | null
  scheduleStart: string
  scheduleEnd: string
  effectiveMealBreakCount: number
  workedMinutes: number
  paidMinutes: number
  paidHours: number
  /** 超出排班窗口的加班候选分钟数；保存后进「待审批」，批准前不计薪。 */
  candidateOvertimeMinutes: number
}

export interface AttendanceManualEntryResult {
  scheduleGuid: string
  createdSchedule: boolean
  punchCount: number
  paidHours: number
  candidateOvertimeMinutes: number
}

export interface RevokeAttendanceManualEntryPayload {
  storeCode: string
  userGuid: string
  workDate: string
}

// ===== 补录带薪假（POST /managed/manual-leaves/preview | / | /revoke）=====

/** 补录支持的假：带薪病假 / 带薪年假。年假发放已取消（历史记录仍可能出现在工时表里，类型见 AttendancePaidLeaveType）。 */
export type AttendanceManualLeaveType = 'PaidSick' | 'PaidAnnual'

export interface AttendanceManualLeavePayload {
  storeCode: string
  userGuid: string
  leaveType: AttendanceManualLeaveType
  /** 年假的区间（YYYY-MM-DD）；病假不用，但字段仍要传。 */
  startDate: string
  endDate: string
  /** 带薪病假要补录的日期（每天必须有本店已发布、没打卡的排班）。 */
  dates?: string[]
  /** 带薪年假的合计小时数（整段区间的总数，不是每天）；不传 = 区间内工作日 × 8 h。 */
  totalHours?: number
  /** 带薪年假当天员工是否休息；不传按休息。false = 照常上班、年假另外加算。 */
  restsOnLeaveDays?: boolean
  reason: string
  /** 保存必须带回预览返回的版本号。 */
  previewRevision?: string
}

export interface AttendanceManualLeavePreview {
  previewRevision: string
  leaveType: AttendanceManualLeaveType
  startDate: string
  endDate: string
  paidHours: number
  /** 带薪年假的默认合计（区间内工作日 × 8 h），用来预填合计小时数；病假为 0。 */
  defaultPaidHours: number
  /** 带薪年假当天是否休息（预览回显）。 */
  restsOnLeaveDays: boolean
  /** 每天的带薪工时（年假为合计摊出来的每天小时数；周末 / 公共假期为 0）。 */
  days: { date: string; hours: number }[]
  /** 病假没有医嘱：工时表该格会标「无医嘱」。 */
  noCertificate: boolean
}

export interface AttendanceManualLeaveResult {
  leaveGuid: string
  paidHours: number
}

export interface RevokeAttendanceManualLeavePayload {
  storeCode: string
  leaveGuid: string
}
