// 分店现金管理接口：基础路径 /react/v1/cash（apiClient 的 baseURL 已含 /api）。
// apiClient 的响应拦截器已经把 ApiResponse<T> 解包成 T，success=false 会直接抛错（错误码在 error.code）。
// 这里再做一层防御式规范化：缺字段、null、数字字符串都能得到类型稳定的结果，页面不用到处判空。
import type {
  CashAttachment,
  CashAttachmentUploadRequest,
  CashAttachmentUploadSignature,
  CashBalanceEntry,
  CashCapabilities,
  CashCloseArchive,
  CashCloseSelectionRequest,
  CashContext,
  CashDaily,
  CashDailyDevice,
  CashDailyRow,
  CashDepositDetail,
  CashDepositListItem,
  CashDepositSlip,
  CashExpenseCategoryTotal,
  CashExpenseDetail,
  CashExpenseListItem,
  CashExpenseListQuery,
  CashListQuery,
  CashPaged,
  CashRecordStatus,
  CashReviewStatus,
  CashSelectionMode,
  CashStoreOption,
  CashStoreSummary,
  CashVoidRequest,
  CreateCashCountRequest,
  CreateCashDepositRequest,
  CreateCashExpenseRequest,
  SetCashOpeningRequest,
} from "./types";

const BASE_PATH = "/react/v1/cash";

type ApiRecord = Record<string, unknown>;

async function getApiClient() {
  // 延迟加载，让规范化与请求构造的纯逻辑测试不必初始化 React Native 运行时。
  const { apiClient } = await import("@/shared/api/client");
  return apiClient;
}

// ───────────────────────── 基础取值 ─────────────────────────

function asRecord(value: unknown): ApiRecord {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as ApiRecord) : {};
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function asString(value: unknown, fallback = ""): string {
  if (typeof value === "string") return value;
  if (typeof value === "number" && Number.isFinite(value)) return String(value);
  return fallback;
}

function asNullableString(value: unknown): string | null {
  const text = asString(value).trim();
  return text ? text : null;
}

/**
 * 时间字段约定是 UTC ISO 字符串。DateTime 的 Kind 为 Unspecified 时 .NET 序列化会漏掉末尾的 Z，
 * 客户端按本地时间解析就会整体偏移数小时，所以没有时区标记的 ISO 时间一律补 Z。
 */
function asUtcString(value: unknown): string {
  const text = asString(value).trim();
  return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(text) ? `${text}Z` : text;
}

function asNullableUtcString(value: unknown): string | null {
  const text = asUtcString(value);
  return text ? text : null;
}

/** DateOnly 约定是 yyyy-MM-dd；万一带了时间部分，只取日期。 */
function asDateString(value: unknown): string {
  const text = asString(value).trim();
  return text.includes("T") ? text.slice(0, 10) : text;
}

function asNullableDateString(value: unknown): string | null {
  const text = asDateString(value);
  return text ? text : null;
}

function asNullableNumber(value: unknown): number | null {
  if (value === null || value === undefined) return null;
  if (typeof value === "string" && !value.trim()) return null;
  const parsed = typeof value === "string" ? Number(value) : value;
  return typeof parsed === "number" && Number.isFinite(parsed) ? parsed : null;
}

function asNumber(value: unknown, fallback = 0): number {
  return asNullableNumber(value) ?? fallback;
}

function asInt(value: unknown, fallback = 0): number {
  return Math.trunc(asNumber(value, fallback));
}

function asBoolean(value: unknown, fallback = false): boolean {
  if (typeof value === "boolean") return value;
  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (normalized === "true") return true;
    if (normalized === "false") return false;
  }
  return fallback;
}

function asRecordStatus(value: unknown): CashRecordStatus {
  return asString(value) === "Voided" ? "Voided" : "Active";
}

function asReviewStatus(value: unknown): CashReviewStatus {
  const text = asString(value);
  return text === "Reviewed" || text === "Flagged" ? text : "None";
}

function asSelectionMode(value: unknown): CashSelectionMode {
  return asString(value) === "Manual" ? "Manual" : "Default";
}

// ───────────────────────── 规范化 ─────────────────────────

export function normalizeCashStoreOption(raw: unknown): CashStoreOption {
  const item = asRecord(raw);
  return {
    storeCode: asString(item.storeCode),
    storeName: asString(item.storeName),
    timeZoneId: asString(item.timeZoneId),
    storeToday: asDateString(item.storeToday),
  };
}

export function normalizeCashCapabilities(raw: unknown): CashCapabilities {
  const item = asRecord(raw);
  return {
    canCreateDeposit: asBoolean(item.canCreateDeposit),
    canCreateExpense: asBoolean(item.canCreateExpense),
    canViewAllStores: asBoolean(item.canViewAllStores),
    canVoid: asBoolean(item.canVoid),
  };
}

export function normalizeCashContext(raw: unknown): CashContext {
  const item = asRecord(raw);
  return {
    stores: asArray(item.stores).map(normalizeCashStoreOption).filter((store) => store.storeCode),
    capabilities: normalizeCashCapabilities(item.capabilities),
    dailyCloseConnected: asBoolean(item.dailyCloseConnected),
    t2VisibleDays: asInt(item.t2VisibleDays, 14),
    maxBackfillDays: asInt(item.maxBackfillDays, 7),
    selfVoidHours: asInt(item.selfVoidHours, 24),
    depositOverdueDays: asInt(item.depositOverdueDays, 3),
    depositDifferenceReasonThreshold: asNumber(item.depositDifferenceReasonThreshold, 20),
    maxSlipsPerDeposit: asInt(item.maxSlipsPerDeposit, 10),
    maxImagesPerSlip: asInt(item.maxImagesPerSlip, 3),
    maxImagesPerExpense: asInt(item.maxImagesPerExpense, 5),
  };
}

export function normalizeCashBalanceEntry(raw: unknown): CashBalanceEntry {
  const item = asRecord(raw);
  return {
    entryGuid: asString(item.entryGuid),
    storeCode: asString(item.storeCode),
    entryType: asString(item.entryType) === "Opening" ? "Opening" : "Count",
    entryDate: asDateString(item.entryDate),
    amount: asNumber(item.amount),
    expectedAmount: asNullableNumber(item.expectedAmount),
    difference: asNullableNumber(item.difference),
    note: asNullableString(item.note),
    status: asRecordStatus(item.status),
    createdByName: asNullableString(item.createdByName),
    createdAtUtc: asUtcString(item.createdAtUtc),
    canVoid: asBoolean(item.canVoid),
  };
}

function normalizeNullableEntry(raw: unknown): CashBalanceEntry | null {
  return raw && typeof raw === "object" ? normalizeCashBalanceEntry(raw) : null;
}

function normalizeCategoryTotal(raw: unknown): CashExpenseCategoryTotal {
  const item = asRecord(raw);
  return { category: asString(item.category), amount: asNumber(item.amount) };
}

export function normalizeCashSummary(raw: unknown): CashStoreSummary {
  const item = asRecord(raw);
  return {
    storeCode: asString(item.storeCode),
    storeName: asString(item.storeName),
    asOfDate: asDateString(item.asOfDate),
    dailyCloseConnected: asBoolean(item.dailyCloseConnected),
    latestCloseDate: asNullableDateString(item.latestCloseDate),
    openingMissing: asBoolean(item.openingMissing),
    opening: normalizeNullableEntry(item.opening),
    // 日结未接入时这些字段是 null：保持 null，不能当 0 处理
    poolBalance: asNullableNumber(item.poolBalance),
    inflowTotal: asNullableNumber(item.inflowTotal),
    depositTotal: asNumber(item.depositTotal),
    expenseTotal: asNumber(item.expenseTotal),
    expenseByCategory: asArray(item.expenseByCategory).map(normalizeCategoryTotal),
    t2Restricted: asBoolean(item.t2Restricted),
    uncoveredDayCount: asInt(item.uncoveredDayCount),
    oldestUncoveredDate: asNullableDateString(item.oldestUncoveredDate),
    uncoveredCash: asNullableNumber(item.uncoveredCash),
    depositOverdue: asBoolean(item.depositOverdue),
    suggestedDepositAmount: asNullableNumber(item.suggestedDepositAmount),
    lastDepositDate: asNullableDateString(item.lastDepositDate),
    lastCount: normalizeNullableEntry(item.lastCount),
    missingCloseDates: asArray(item.missingCloseDates).map(asDateString).filter(Boolean),
  };
}

function normalizeCloseArchive(raw: unknown): CashCloseArchive {
  const item = asRecord(raw);
  return {
    closeId: asString(item.closeId),
    savedAtUtc: asUtcString(item.savedAtUtc),
    periodFromUtc: asUtcString(item.periodFromUtc),
    periodToUtc: asUtcString(item.periodToUtc),
    countedCash: asNumber(item.countedCash),
    expectedCash: asNumber(item.expectedCash),
    variance: asNumber(item.variance),
    included: asBoolean(item.included),
  };
}

export function normalizeCashDailyDevice(raw: unknown): CashDailyDevice {
  const item = asRecord(raw);
  return {
    deviceCode: asString(item.deviceCode),
    selectionMode: asSelectionMode(item.selectionMode),
    selectionStale: asBoolean(item.selectionStale),
    selectionOverlapWarning: asBoolean(item.selectionOverlapWarning),
    selectionReason: asNullableString(item.selectionReason),
    selectedByName: asNullableString(item.selectedByName),
    selectedAtUtc: asNullableUtcString(item.selectedAtUtc),
    includedCash: asNumber(item.includedCash),
    // 存档按保存时间从新到旧展示
    archives: asArray(item.archives)
      .map(normalizeCloseArchive)
      .sort((a, b) => Date.parse(b.savedAtUtc) - Date.parse(a.savedAtUtc) || 0),
  };
}

function normalizeDailyRow(raw: unknown): CashDailyRow {
  const item = asRecord(raw);
  return {
    businessDate: asDateString(item.businessDate),
    inflowCash: asNumber(item.inflowCash),
    hasClose: asBoolean(item.hasClose),
    covered: asBoolean(item.covered),
    coveredByDepositGuid: asNullableString(item.coveredByDepositGuid),
    expenseTotal: asNumber(item.expenseTotal),
    devices: asArray(item.devices).map(normalizeCashDailyDevice),
  };
}

export function normalizeCashDaily(raw: unknown): CashDaily {
  const item = asRecord(raw);
  return {
    storeCode: asString(item.storeCode),
    dailyCloseConnected: asBoolean(item.dailyCloseConnected),
    // 最近的营业日排在前面
    rows: asArray(item.rows)
      .map(normalizeDailyRow)
      .sort((a, b) => (a.businessDate < b.businessDate ? 1 : a.businessDate > b.businessDate ? -1 : 0)),
  };
}

export function normalizeCashUploadSignature(raw: unknown): CashAttachmentUploadSignature {
  const item = asRecord(raw);
  const headers: Record<string, string> = {};
  for (const [key, value] of Object.entries(asRecord(item.headers))) {
    const text = asString(value);
    if (key && text !== "") headers[key] = text;
  }
  return {
    attachmentGuid: asString(item.attachmentGuid),
    url: asString(item.url),
    headers,
    expiresAtUtc: asUtcString(item.expiresAtUtc),
  };
}

function normalizeAttachment(raw: unknown): CashAttachment {
  const item = asRecord(raw);
  return {
    attachmentGuid: asString(item.attachmentGuid),
    url: asString(item.url),
    urlExpiresAtUtc: asUtcString(item.urlExpiresAtUtc),
    contentType: asString(item.contentType),
    sortOrder: asInt(item.sortOrder),
  };
}

function normalizeAttachments(raw: unknown): CashAttachment[] {
  return asArray(raw)
    .map(normalizeAttachment)
    .filter((attachment) => attachment.url)
    .sort((a, b) => a.sortOrder - b.sortOrder);
}

export function normalizeCashDepositListItem(raw: unknown): CashDepositListItem {
  const item = asRecord(raw);
  return {
    depositGuid: asString(item.depositGuid),
    storeCode: asString(item.storeCode),
    depositDate: asDateString(item.depositDate),
    coveredFromDate: asNullableDateString(item.coveredFromDate),
    coveredToDate: asNullableDateString(item.coveredToDate),
    totalAmount: asNumber(item.totalAmount),
    slipCount: asInt(item.slipCount),
    imageCount: asInt(item.imageCount),
    status: asRecordStatus(item.status),
    note: asNullableString(item.note),
    createdByName: asNullableString(item.createdByName),
    createdAtUtc: asUtcString(item.createdAtUtc),
    canVoid: asBoolean(item.canVoid),
  };
}

function normalizeDepositSlip(raw: unknown): CashDepositSlip {
  const item = asRecord(raw);
  return {
    slipGuid: asString(item.slipGuid),
    amount: asNumber(item.amount),
    slipNo: asNullableString(item.slipNo),
    attachments: normalizeAttachments(item.attachments),
  };
}

export function normalizeCashDepositDetail(raw: unknown): CashDepositDetail {
  const item = asRecord(raw);
  return {
    ...normalizeCashDepositListItem(raw),
    overrideReason: asNullableString(item.overrideReason),
    voidReason: asNullableString(item.voidReason),
    voidedByName: asNullableString(item.voidedByName),
    voidedAtUtc: asNullableUtcString(item.voidedAtUtc),
    slips: asArray(item.slips).map(normalizeDepositSlip),
  };
}

export function normalizeCashExpenseListItem(raw: unknown): CashExpenseListItem {
  const item = asRecord(raw);
  return {
    expenseGuid: asString(item.expenseGuid),
    storeCode: asString(item.storeCode),
    expenseDate: asDateString(item.expenseDate),
    category: asString(item.category),
    amount: asNumber(item.amount),
    payeeName: asNullableString(item.payeeName),
    note: asNullableString(item.note),
    reviewStatus: asReviewStatus(item.reviewStatus),
    status: asRecordStatus(item.status),
    imageCount: asInt(item.imageCount),
    createdByName: asNullableString(item.createdByName),
    createdAtUtc: asUtcString(item.createdAtUtc),
    canVoid: asBoolean(item.canVoid),
  };
}

export function normalizeCashExpenseDetail(raw: unknown): CashExpenseDetail {
  const item = asRecord(raw);
  return {
    ...normalizeCashExpenseListItem(raw),
    payeeUserGuid: asNullableString(item.payeeUserGuid),
    voidReason: asNullableString(item.voidReason),
    voidedByName: asNullableString(item.voidedByName),
    voidedAtUtc: asNullableUtcString(item.voidedAtUtc),
    attachments: normalizeAttachments(item.attachments),
  };
}

function normalizePaged<T>(raw: unknown, normalizeItem: (item: unknown) => T): CashPaged<T> {
  const item = asRecord(raw);
  const items = asArray(item.items).map(normalizeItem);
  return { items, total: Math.max(asInt(item.total, items.length), items.length) };
}

export const normalizeCashDepositPage = (raw: unknown) => normalizePaged(raw, normalizeCashDepositListItem);
export const normalizeCashExpensePage = (raw: unknown) => normalizePaged(raw, normalizeCashExpenseListItem);

// ───────────────────────── 请求参数构造 ─────────────────────────

function listParams(query: CashListQuery) {
  return {
    storeCode: query.storeCode,
    from: query.from || undefined,
    to: query.to || undefined,
    includeVoided: query.includeVoided ? true : undefined,
    limit: query.limit,
    offset: query.offset,
  };
}

export function buildCashDepositListParams(query: CashListQuery) {
  return listParams(query);
}

export function buildCashExpenseListParams(query: CashExpenseListQuery) {
  return { ...listParams(query), category: query.category || undefined };
}

const path = (...segments: string[]) => `${BASE_PATH}/${segments.map(encodeURIComponent).join("/")}`;

// ───────────────────────── 接口调用 ─────────────────────────

export async function fetchCashContext(): Promise<CashContext> {
  const client = await getApiClient();
  const response = await client.get(path("context"));
  return normalizeCashContext(response.data);
}

export async function fetchCashSummary(storeCode: string): Promise<CashStoreSummary> {
  const client = await getApiClient();
  const response = await client.get(path("summary"), { params: { storeCode } });
  return normalizeCashSummary(response.data);
}

export async function fetchCashDaily(storeCode: string, from: string, to: string): Promise<CashDaily> {
  const client = await getApiClient();
  const response = await client.get(path("daily"), { params: { storeCode, from, to } });
  return normalizeCashDaily(response.data);
}

export async function putCashCloseSelection(request: CashCloseSelectionRequest): Promise<CashDailyDevice> {
  const client = await getApiClient();
  const response = await client.put(path("close-selection"), request);
  return normalizeCashDailyDevice(response.data);
}

export async function requestCashUploadSignature(
  request: CashAttachmentUploadRequest,
): Promise<CashAttachmentUploadSignature> {
  const client = await getApiClient();
  const response = await client.post(path("attachments", "upload-signature"), request);
  return normalizeCashUploadSignature(response.data);
}

export async function fetchCashDeposits(query: CashListQuery): Promise<CashPaged<CashDepositListItem>> {
  const client = await getApiClient();
  const response = await client.get(path("deposits"), { params: buildCashDepositListParams(query) });
  return normalizeCashDepositPage(response.data);
}

export async function fetchCashDepositDetail(depositGuid: string): Promise<CashDepositDetail> {
  const client = await getApiClient();
  const response = await client.get(path("deposits", depositGuid));
  return normalizeCashDepositDetail(response.data);
}

export async function createCashDeposit(request: CreateCashDepositRequest): Promise<CashDepositDetail> {
  const client = await getApiClient();
  const response = await client.post(path("deposits"), request);
  return normalizeCashDepositDetail(response.data);
}

export async function voidCashDeposit(depositGuid: string, request: CashVoidRequest): Promise<CashDepositDetail> {
  const client = await getApiClient();
  const response = await client.post(path("deposits", depositGuid, "void"), request);
  return normalizeCashDepositDetail(response.data);
}

export async function fetchCashExpenses(query: CashExpenseListQuery): Promise<CashPaged<CashExpenseListItem>> {
  const client = await getApiClient();
  const response = await client.get(path("expenses"), { params: buildCashExpenseListParams(query) });
  return normalizeCashExpensePage(response.data);
}

export async function fetchCashExpenseDetail(expenseGuid: string): Promise<CashExpenseDetail> {
  const client = await getApiClient();
  const response = await client.get(path("expenses", expenseGuid));
  return normalizeCashExpenseDetail(response.data);
}

export async function createCashExpense(request: CreateCashExpenseRequest): Promise<CashExpenseDetail> {
  const client = await getApiClient();
  const response = await client.post(path("expenses"), request);
  return normalizeCashExpenseDetail(response.data);
}

export async function voidCashExpense(expenseGuid: string, request: CashVoidRequest): Promise<CashExpenseDetail> {
  const client = await getApiClient();
  const response = await client.post(path("expenses", expenseGuid, "void"), request);
  return normalizeCashExpenseDetail(response.data);
}

export async function fetchCashEntries(storeCode: string, includeVoided: boolean): Promise<CashBalanceEntry[]> {
  const client = await getApiClient();
  const response = await client.get(path("entries"), {
    params: { storeCode, includeVoided: includeVoided ? true : undefined },
  });
  return asArray(response.data).map(normalizeCashBalanceEntry);
}

export async function putCashOpening(request: SetCashOpeningRequest): Promise<CashBalanceEntry> {
  const client = await getApiClient();
  const response = await client.put(path("opening"), request);
  return normalizeCashBalanceEntry(response.data);
}

export async function createCashCount(request: CreateCashCountRequest): Promise<CashBalanceEntry> {
  const client = await getApiClient();
  const response = await client.post(path("counts"), request);
  return normalizeCashBalanceEntry(response.data);
}

export async function voidCashEntry(entryGuid: string, request: CashVoidRequest): Promise<CashBalanceEntry> {
  const client = await getApiClient();
  const response = await client.post(path("entries", entryGuid, "void"), request);
  return normalizeCashBalanceEntry(response.data);
}
