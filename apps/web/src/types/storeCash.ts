/**
 * 分店现金管理（存银行 / 现金支出 / 现金池）接口类型，与后端
 * services/backend/BlazorApp.Shared/DTOs/StoreCashDtos.cs 一一对应（JSON camelCase）。
 *
 * 约定：
 * - 日期（DateOnly）是门店本地日期字符串 yyyy-MM-dd；时间（DateTime）是带 Z 的 UTC ISO 字符串。
 * - 金额是澳元小数（number）。
 * - 可空字段（如日结未接入时的现金池余额）是 null，不能当成 0 显示或累加。
 * - 支出类别 T2 在代码、接口、界面、导出里都只叫 T2。
 */

export const CASH_EXPENSE_CATEGORIES = ['Salary', 'Purchase', 'T2', 'Other'] as const
export type CashExpenseCategory = (typeof CASH_EXPENSE_CATEGORIES)[number]

export const CASH_REVIEW_STATUSES = ['None', 'Reviewed', 'Flagged'] as const
export type CashReviewStatus = (typeof CASH_REVIEW_STATUSES)[number]

export type CashRecordStatus = 'Active' | 'Voided'
export type CashSelectionMode = 'Default' | 'Manual'
export type CashBalanceEntryType = 'Opening' | 'Count'

/** 接口错误码（StoreCashConstants.ErrorCodes）；失败响应的 message 是可直接展示的中文。 */
export const CASH_ERROR_CODES = [
  'CASH_INVALID_REQUEST',
  'CASH_STORE_NOT_FOUND',
  'CASH_STORE_FORBIDDEN',
  'CASH_RECORD_NOT_FOUND',
  'CASH_DATE_OUT_OF_RANGE',
  'CASH_ATTACHMENT_INVALID',
  'CASH_ATTACHMENT_REQUIRED',
  'CASH_OVERRIDE_REASON_REQUIRED',
  'CASH_OPENING_EXISTS',
  'CASH_VOID_NOT_ALLOWED',
  'CASH_CLOSE_SOURCE_UNAVAILABLE',
  'CASH_CLOSE_NOT_FOUND',
  'CASH_CONFLICT',
  'CASH_INTERNAL_ERROR',
] as const
export type CashErrorCode = (typeof CASH_ERROR_CODES)[number]

export interface CashStoreOption {
  storeCode: string
  storeName: string
  timeZoneId: string
  /** 该店当前的本地日期，日期选择以它为准，不用浏览器时区。 */
  storeToday: string
  /** 该店是否启用收银系统；旧后端没有这个字段时按启用处理（服务层兜底）。 */
  cashRegisterEnabled: boolean
}

export interface CashCapabilities {
  canCreateDeposit: boolean
  canCreateExpense: boolean
  /** 有全部分店权限：看全部分店、全部历史 T2。 */
  canViewAllStores: boolean
  canVoid: boolean
}

/** GET cash/context */
export interface CashContext {
  stores: CashStoreOption[]
  capabilities: CashCapabilities
  /** 后端日结数据是否已接入；未接入时现金池余额、日结流入、日结差异为 null。 */
  dailyCloseConnected: boolean
  t2VisibleDays: number
  maxBackfillDays: number
  selfVoidHours: number
  depositOverdueDays: number
  depositDifferenceReasonThreshold: number
  maxSlipsPerDeposit: number
  maxImagesPerSlip: number
  maxImagesPerExpense: number
}

export interface CashPaged<T> {
  items: T[]
  total: number
}

export interface CashBalanceEntry {
  entryGuid: string
  storeCode: string
  entryType: CashBalanceEntryType | string
  entryDate: string
  amount: number
  expectedAmount: number | null
  difference: number | null
  note: string | null
  status: CashRecordStatus | string
  createdByName: string | null
  createdAtUtc: string
  canVoid: boolean
}

export interface CashExpenseCategoryTotal {
  category: CashExpenseCategory | string
  amount: number
}

// ───────────────────────── 多店总览 ─────────────────────────

export interface CashOverviewRow {
  storeCode: string
  storeName: string
  storeToday: string
  // 当前（截止门店今天）
  openingMissing: boolean
  poolBalance: number | null
  uncoveredDayCount: number
  oldestUncoveredDate: string | null
  depositOverdue: boolean
  lastDepositDate: string | null
  // 区间
  inflowCash: number | null
  closeVariance: number | null
  closeDayCount: number
  missingCloseDayCount: number
  depositTotal: number
  depositCount: number
  expenseTotal: number
  expenseByCategory: CashExpenseCategoryTotal[]
  flaggedExpenseCount: number
}

export interface CashOverviewTotals {
  poolBalance: number | null
  inflowCash: number | null
  closeVariance: number | null
  depositTotal: number
  depositCount: number
  expenseTotal: number
  expenseByCategory: CashExpenseCategoryTotal[]
  uncoveredDayCount: number
  overdueStoreCount: number
  flaggedExpenseCount: number
}

/** GET cash/overview?from=&to=&storeCodes= */
export interface CashOverview {
  from: string
  to: string
  dailyCloseConnected: boolean
  /** 当前账号受 T2 窗口限制：区间里的 T2 只含最近 N 天。 */
  t2Restricted: boolean
  rows: CashOverviewRow[]
  totals: CashOverviewTotals
}

// ───────────────────────── 按日明细 ─────────────────────────

export interface CashCloseArchive {
  closeId: string
  savedAtUtc: string
  periodFromUtc: string
  periodToUtc: string
  countedCash: number
  expectedCash: number
  variance: number
  /** 当前是否纳入现金池。 */
  included: boolean
}

export interface CashDailyDevice {
  deviceCode: string
  selectionMode: CashSelectionMode | string
  /** 手选之后又出现了更新的存档，需要人确认。 */
  selectionStale: boolean
  selectionOverlapWarning: boolean
  selectionReason: string | null
  selectedByName: string | null
  selectedAtUtc: string | null
  includedCash: number
  archives: CashCloseArchive[]
}

export interface CashDailyRow {
  businessDate: string
  inflowCash: number
  hasClose: boolean
  covered: boolean
  coveredByDepositGuid: string | null
  expenseTotal: number
  devices: CashDailyDevice[]
}

/** GET cash/daily?storeCode=&from=&to= */
export interface CashDaily {
  storeCode: string
  dailyCloseConnected: boolean
  rows: CashDailyRow[]
}

// ───────────────────────── 附件 ─────────────────────────

export interface CashAttachment {
  attachmentGuid: string
  /** 带过期时间的私有下载地址（几分钟有效），每次打开详情重新签发。 */
  url: string
  urlExpiresAtUtc: string
  contentType: string
  sortOrder: number
}

// ───────────────────────── 存款 ─────────────────────────

export interface CashDepositSlip {
  slipGuid: string
  amount: number
  slipNo: string | null
  attachments: CashAttachment[]
}

/** 存单摘要：银行对账按存单粒度匹配入账流水。 */
export interface CashDepositSlipSummary {
  slipGuid: string
  amount: number
  slipNo: string | null
  imageCount: number
}

export interface CashDepositListItem {
  depositGuid: string
  storeCode: string
  depositDate: string
  coveredFromDate: string | null
  coveredToDate: string | null
  totalAmount: number
  slipCount: number
  imageCount: number
  /** 按录入顺序的存单摘要；旧版后端没有该字段时按空处理。 */
  slipSummaries?: CashDepositSlipSummary[]
  status: CashRecordStatus | string
  note: string | null
  createdByName: string | null
  createdAtUtc: string
  canVoid: boolean
}

export interface CashDepositDetail extends CashDepositListItem {
  overrideReason: string | null
  voidReason: string | null
  voidedByName: string | null
  voidedAtUtc: string | null
  slips: CashDepositSlip[]
}

// ───────────────────────── 现金支出 ─────────────────────────

export interface CashExpenseListItem {
  expenseGuid: string
  storeCode: string
  expenseDate: string
  category: CashExpenseCategory | string
  amount: number
  payeeName: string | null
  note: string | null
  /** 财务事后核对标记，不影响支出生效。 */
  reviewStatus: CashReviewStatus | string
  reviewNote: string | null
  reviewedByName: string | null
  reviewedAtUtc: string | null
  /** 当前账号能否打核对标记（持有 Cash.Void，且记录有效）。 */
  canReview: boolean
  status: CashRecordStatus | string
  imageCount: number
  createdByName: string | null
  createdAtUtc: string
  canVoid: boolean
}

export interface CashExpenseDetail extends CashExpenseListItem {
  payeeUserGuid: string | null
  voidReason: string | null
  voidedByName: string | null
  voidedAtUtc: string | null
  attachments: CashAttachment[]
}

// ───────────────────────── 查询参数 ─────────────────────────

export interface CashOverviewQuery {
  from: string
  to: string
  /** 不传或空数组表示全部可见分店。 */
  storeCodes?: string[]
}

export interface CashDailyQuery {
  storeCode: string
  from: string
  to: string
}

export interface CashDepositListQuery {
  storeCode: string
  from?: string
  to?: string
  includeVoided?: boolean
  /** 服务端最大 200。 */
  limit?: number
  offset?: number
}

export interface CashExpenseListQuery extends CashDepositListQuery {
  category?: CashExpenseCategory
  reviewStatus?: CashReviewStatus
}

export interface CashExpenseReviewRequest {
  reviewStatus: CashReviewStatus
  note?: string
}
