// 分店现金管理接口类型，与 BlazorApp.Shared/DTOs/StoreCashDtos.cs 一一对应（JSON 为 camelCase）。
// 日期 DateOnly 一律是 "yyyy-MM-dd" 的门店本地日期字符串；时间是 UTC ISO 字符串；金额是澳元小数。

export type CashExpenseCategory = "Salary" | "Purchase" | "T2" | "Other";
export type CashRecordStatus = "Active" | "Voided";
export type CashBalanceEntryType = "Opening" | "Count";
export type CashSelectionMode = "Default" | "Manual";
export type CashReviewStatus = "None" | "Reviewed" | "Flagged";

export interface CashStoreOption {
  storeCode: string;
  storeName: string;
  timeZoneId: string;
  /** 该店当前的本地日期；日期选择与补录范围一律以它为准，不用手机时区。 */
  storeToday: string;
}

export interface CashCapabilities {
  canCreateDeposit: boolean;
  canCreateExpense: boolean;
  canViewAllStores: boolean;
  canVoid: boolean;
}

export interface CashContext {
  stores: CashStoreOption[];
  capabilities: CashCapabilities;
  /** 后端日结是否已接入；false 时现金池余额等字段为 null，页面要提示而不是显示 0。 */
  dailyCloseConnected: boolean;
  t2VisibleDays: number;
  maxBackfillDays: number;
  selfVoidHours: number;
  depositOverdueDays: number;
  depositDifferenceReasonThreshold: number;
  maxSlipsPerDeposit: number;
  maxImagesPerSlip: number;
  maxImagesPerExpense: number;
}

export interface CashPaged<T> {
  items: T[];
  total: number;
}

export interface CashBalanceEntry {
  entryGuid: string;
  storeCode: string;
  entryType: CashBalanceEntryType;
  entryDate: string;
  amount: number;
  expectedAmount: number | null;
  difference: number | null;
  note: string | null;
  status: CashRecordStatus;
  createdByName: string | null;
  createdAtUtc: string;
  canVoid: boolean;
}

export interface CashExpenseCategoryTotal {
  category: string;
  amount: number;
}

export interface CashStoreSummary {
  storeCode: string;
  storeName: string;
  /** 统计截止日 = 门店今天，不是最近有日结的那天。 */
  asOfDate: string;
  dailyCloseConnected: boolean;
  /** 最近一个有日结存档的营业日；日结未接入或没有存档时为 null。 */
  latestCloseDate: string | null;
  openingMissing: boolean;
  opening: CashBalanceEntry | null;
  poolBalance: number | null;
  inflowTotal: number | null;
  depositTotal: number;
  expenseTotal: number;
  expenseByCategory: CashExpenseCategoryTotal[];
  t2Restricted: boolean;
  uncoveredDayCount: number;
  oldestUncoveredDate: string | null;
  uncoveredCash: number | null;
  depositOverdue: boolean;
  suggestedDepositAmount: number | null;
  lastDepositDate: string | null;
  lastCount: CashBalanceEntry | null;
  missingCloseDates: string[];
}

export interface CashCloseArchive {
  closeId: string;
  savedAtUtc: string;
  periodFromUtc: string;
  periodToUtc: string;
  countedCash: number;
  expectedCash: number;
  variance: number;
  included: boolean;
}

export interface CashDailyDevice {
  deviceCode: string;
  selectionMode: CashSelectionMode;
  selectionStale: boolean;
  selectionOverlapWarning: boolean;
  selectionReason: string | null;
  selectedByName: string | null;
  selectedAtUtc: string | null;
  includedCash: number;
  archives: CashCloseArchive[];
}

export interface CashDailyRow {
  businessDate: string;
  inflowCash: number;
  hasClose: boolean;
  covered: boolean;
  coveredByDepositGuid: string | null;
  expenseTotal: number;
  devices: CashDailyDevice[];
}

export interface CashDaily {
  storeCode: string;
  dailyCloseConnected: boolean;
  rows: CashDailyRow[];
}

export interface CashCloseSelectionRequest {
  storeCode: string;
  businessDate: string;
  deviceCode: string;
  mode: CashSelectionMode;
  closeIds: string[];
  reason?: string;
}

export interface CashAttachmentUploadRequest {
  storeCode: string;
  contentType: string;
  fileSize: number;
}

export interface CashAttachmentUploadSignature {
  attachmentGuid: string;
  url: string;
  headers: Record<string, string>;
  expiresAtUtc: string;
}

export interface CashAttachment {
  attachmentGuid: string;
  /** 几分钟有效的私有下载地址，每次进详情重新取，不缓存。 */
  url: string;
  urlExpiresAtUtc: string;
  contentType: string;
  sortOrder: number;
}

export interface CashDepositSlipInput {
  amount: number;
  slipNo?: string;
  attachmentGuids: string[];
}

export interface CreateCashDepositRequest {
  clientRequestId: string;
  storeCode: string;
  depositDate: string;
  coveredFromDate?: string;
  coveredToDate?: string;
  note?: string;
  overrideReason?: string;
  slips: CashDepositSlipInput[];
}

export interface CashDepositSlip {
  slipGuid: string;
  amount: number;
  slipNo: string | null;
  attachments: CashAttachment[];
}

export interface CashDepositListItem {
  depositGuid: string;
  storeCode: string;
  depositDate: string;
  coveredFromDate: string | null;
  coveredToDate: string | null;
  totalAmount: number;
  slipCount: number;
  imageCount: number;
  status: CashRecordStatus;
  note: string | null;
  createdByName: string | null;
  createdAtUtc: string;
  canVoid: boolean;
}

export interface CashDepositDetail extends CashDepositListItem {
  overrideReason: string | null;
  voidReason: string | null;
  voidedByName: string | null;
  voidedAtUtc: string | null;
  slips: CashDepositSlip[];
}

export interface CashVoidRequest {
  reason: string;
}

export interface CreateCashExpenseRequest {
  clientRequestId: string;
  storeCode: string;
  expenseDate: string;
  category: CashExpenseCategory;
  amount: number;
  payeeUserGuid?: string;
  payeeName?: string;
  note?: string;
  attachmentGuids: string[];
}

export interface CashExpenseListItem {
  expenseGuid: string;
  storeCode: string;
  expenseDate: string;
  category: string;
  amount: number;
  payeeName: string | null;
  note: string | null;
  reviewStatus: CashReviewStatus;
  status: CashRecordStatus;
  imageCount: number;
  createdByName: string | null;
  createdAtUtc: string;
  canVoid: boolean;
}

export interface CashExpenseDetail extends CashExpenseListItem {
  payeeUserGuid: string | null;
  voidReason: string | null;
  voidedByName: string | null;
  voidedAtUtc: string | null;
  attachments: CashAttachment[];
}

export interface SetCashOpeningRequest {
  clientRequestId: string;
  storeCode: string;
  entryDate: string;
  amount: number;
  note?: string;
}

export interface CreateCashCountRequest {
  clientRequestId: string;
  storeCode: string;
  entryDate: string;
  amount: number;
  note?: string;
}

export interface CashListQuery {
  storeCode: string;
  from?: string;
  to?: string;
  includeVoided?: boolean;
  limit?: number;
  offset?: number;
}

export interface CashExpenseListQuery extends CashListQuery {
  category?: CashExpenseCategory;
}
