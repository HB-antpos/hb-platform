import type {
  PagedResult,
  SeasonalCardBatch,
  SeasonalCardBatchLine,
  SeasonalCardBatchPayload,
  SeasonalCardCatalogItem,
  SeasonalCardOverview,
  SeasonalCardOverviewHoliday,
  SeasonalCardOverviewQuery,
  SeasonalCardPriceOption,
  SeasonalCardSubmissionPayload,
  SeasonalCardSubmissionQuery,
  SeasonalCardSubmissionRecord,
  SeasonalCardType,
} from "@/modules/seasonal-cards/types";

const BASE_PATH = "/react/v1/seasonal-card-remaining";
const LIST_PAGE_SIZES = [20, 50, 100] as const;

async function getApiClient() {
  const { apiClient } = await import("@/shared/api/client");
  return apiClient;
}

function pick(raw: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    if (raw[key] !== undefined && raw[key] !== null) {
      return raw[key];
    }
  }
  return undefined;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function asString(value: unknown, fallback = ""): string {
  if (typeof value === "string") {
    return value;
  }
  if (typeof value === "number" && Number.isFinite(value)) {
    return String(value);
  }
  return fallback;
}

function asNullableNumber(value: unknown): number | null {
  if (value === null || value === undefined) {
    return null;
  }
  if (typeof value === "string" && !value.trim()) {
    return null;
  }
  const parsed = typeof value === "string" ? Number(value) : value;
  return typeof parsed === "number" && Number.isFinite(parsed) ? parsed : null;
}

function asNumber(value: unknown, fallback = 0): number {
  const parsed = asNullableNumber(value);
  return parsed == null ? fallback : parsed;
}

function asNullableInt(value: unknown): number | null {
  const parsed = asNullableNumber(value);
  return parsed == null ? null : Math.trunc(parsed);
}

function asBoolean(value: unknown) {
  if (typeof value === "boolean") {
    return value;
  }
  if (typeof value === "number") {
    return value !== 0;
  }
  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (!normalized) {
      return false;
    }
    return normalized === "true" || normalized === "1" || normalized === "yes";
  }
  return false;
}

function trimText(value: unknown) {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed ? trimmed : undefined;
}

/**
 * 服务端时间统一按 UTC 解读：从数据库读出的 DateTime 序列化时没有时区后缀（Kind=Unspecified），
 * 直接 new Date() 会被当成手机本地时间，这里补上 Z，显示时再按本地时区格式化。
 */
export function normalizeServerUtcTimestamp(value: unknown): string {
  const text = asString(value).trim();
  if (!text) {
    return "";
  }
  const isIsoWithoutZone =
    /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(text);
  return isIsoWithoutZone ? `${text}Z` : text;
}

function normalizePage(value?: number) {
  return value && Number.isFinite(value) && value > 0 ? Math.trunc(value) : 1;
}

function normalizePageSize(value?: number) {
  const normalizedValue =
    value && Number.isFinite(value) ? Math.trunc(value) : undefined;
  return LIST_PAGE_SIZES.includes(
    normalizedValue as (typeof LIST_PAGE_SIZES)[number]
  )
    ? normalizedValue!
    : 20;
}

function asSeasonalCardType(value: unknown): SeasonalCardType | null {
  const parsed = asNullableInt(value);
  return parsed != null && parsed >= 1 && parsed <= 5
    ? (parsed as SeasonalCardType)
    : null;
}

function asSeasonalCardPriceOption(value: unknown): SeasonalCardPriceOption | null {
  const parsed = asNullableInt(value);
  return parsed != null && parsed >= 1 && parsed <= 4
    ? (parsed as SeasonalCardPriceOption)
    : null;
}

function unwrapPayload(payload: unknown) {
  if (Array.isArray(payload)) {
    return payload;
  }

  const root = asRecord(payload) ?? {};
  return pick(root, "data", "Data") ?? root;
}

function getArray(payload: unknown, ...keys: string[]) {
  if (Array.isArray(payload)) {
    return payload;
  }
  const root = asRecord(payload) ?? {};
  const value = pick(root, ...keys);
  return Array.isArray(value) ? value : [];
}

export function buildSeasonalCardSubmissionPayload(
  payload: SeasonalCardSubmissionPayload
) {
  const customUnitPrice = asNullableNumber(payload.customUnitPrice);
  const remark = trimText(payload.remark);

  return {
    storeCode: trimText(payload.storeCode) ?? "",
    catalogGuid: trimText(payload.catalogGuid) ?? "",
    seasonYear: asNullableInt(payload.seasonYear) ?? 0,
    remainingQuantity: asNullableInt(payload.remainingQuantity) ?? 0,
    ...(customUnitPrice == null ? {} : { customUnitPrice }),
    ...(remark ? { remark } : {}),
  };
}

export function buildSeasonalCardSubmissionQuery(
  query: SeasonalCardSubmissionQuery
) {
  const localSupplierCode = trimText(query.localSupplierCode ?? undefined);
  return {
    storeCode: trimText(query.storeCode),
    cardType: asSeasonalCardType(query.cardType),
    seasonYear: asNullableInt(query.seasonYear),
    ...(localSupplierCode ? { localSupplierCode } : {}),
    pageNumber: normalizePage(query.pageNumber),
    pageSize: normalizePageSize(query.pageSize),
  };
}

export function normalizeSeasonalCardCatalogItem(raw: unknown): SeasonalCardCatalogItem {
  const item = asRecord(raw) ?? {};
  return {
    catalogGuid: asString(pick(item, "catalogGuid", "CatalogGuid", "CatalogGUID")),
    cardType: asSeasonalCardType(pick(item, "cardType", "CardType")),
    cardTypeName: asString(pick(item, "cardTypeName", "CardTypeName")),
    priceOption: asSeasonalCardPriceOption(pick(item, "priceOption", "PriceOption")),
    priceOptionName: asString(pick(item, "priceOptionName", "PriceOptionName")),
    priceLabel: asString(pick(item, "priceLabel", "PriceLabel")),
    fixedUnitPrice: asNullableNumber(
      pick(item, "fixedUnitPrice", "FixedUnitPrice", "fixedPrice", "FixedPrice")
    ),
    allowsCustomUnitPrice: asBoolean(
      pick(item, "allowsCustomUnitPrice", "AllowsCustomUnitPrice")
    ),
    isEnabled: asBoolean(pick(item, "isEnabled", "IsEnabled", "isActive", "IsActive")),
    sortOrder: asNullableInt(pick(item, "sortOrder", "SortOrder")),
  };
}

export function normalizeSeasonalCardCatalogResponse(
  payload: unknown
): SeasonalCardCatalogItem[] {
  const data = unwrapPayload(payload);
  return getArray(data, "items", "Items", "catalog", "Catalog").map(
    normalizeSeasonalCardCatalogItem
  );
}

export function normalizeSeasonalCardSubmission(
  raw: unknown
): SeasonalCardSubmissionRecord {
  const item = asRecord(raw) ?? {};
  return {
    submissionGuid: asString(
      pick(item, "submissionGuid", "SubmissionGuid", "SubmissionGUID")
    ),
    storeCode: asString(pick(item, "storeCode", "StoreCode")),
    catalogGuid: asString(pick(item, "catalogGuid", "CatalogGuid", "CatalogGUID")),
    cardType: asSeasonalCardType(pick(item, "cardType", "CardType")),
    cardTypeName: asString(pick(item, "cardTypeName", "CardTypeName")),
    seasonYear: asNullableInt(pick(item, "seasonYear", "SeasonYear")),
    unitPrice: asNullableNumber(pick(item, "unitPrice", "UnitPrice")),
    priceLabel: asString(pick(item, "priceLabel", "PriceLabel")),
    remainingQuantity: asNullableInt(
      pick(item, "remainingQuantity", "RemainingQuantity")
    ),
    remark: asString(pick(item, "remark", "Remark", "remarks", "Remarks")),
    submittedByName: asString(
      pick(item, "submittedByName", "SubmittedByName", "createUser", "CreateUser")
    ),
    submittedAt: normalizeServerUtcTimestamp(
      pick(item, "submittedAt", "SubmittedAt", "createTime", "CreateTime")
    ),
    priceOption: asSeasonalCardPriceOption(pick(item, "priceOption", "PriceOption")),
    localSupplierCode: asString(pick(item, "localSupplierCode", "LocalSupplierCode")).trim(),
    supplierName: asString(pick(item, "supplierName", "SupplierName")).trim(),
    batchGuid: asString(pick(item, "batchGuid", "BatchGuid", "BatchGUID")).trim(),
  };
}

export function normalizeSeasonalCardSubmissionsResponse(
  payload: unknown
): PagedResult<SeasonalCardSubmissionRecord> {
  const data = unwrapPayload(payload);
  return {
    items: getArray(data, "items", "Items", "submissions", "Submissions").map(
      normalizeSeasonalCardSubmission
    ),
    total: asNumber(pick(data as Record<string, unknown>, "total", "Total", "totalCount", "TotalCount"), 0),
    pageNumber: asNumber(pick(data as Record<string, unknown>, "pageNumber", "PageNumber", "page", "Page"), 1),
    pageSize: asNumber(pick(data as Record<string, unknown>, "pageSize", "PageSize", "limit", "Limit"), 20),
  };
}

export function normalizeSeasonalCardSubmissionDetail(
  payload: unknown
): SeasonalCardSubmissionRecord | null {
  const data = unwrapPayload(payload);
  const detail = asRecord(
    pick(asRecord(data) ?? {}, "item", "Item", "submission", "Submission")
  );
  const record = detail ?? asRecord(data);
  if (!record) {
    return null;
  }
  return normalizeSeasonalCardSubmission(record);
}

export function normalizeSeasonalCardBatchLine(raw: unknown): SeasonalCardBatchLine {
  const item = asRecord(raw) ?? {};
  return {
    submissionGuid: asString(
      pick(item, "submissionGuid", "SubmissionGuid", "SubmissionGUID")
    ),
    catalogGuid: asString(pick(item, "catalogGuid", "CatalogGuid", "CatalogGUID")),
    priceOption: asSeasonalCardPriceOption(pick(item, "priceOption", "PriceOption")),
    priceLabel: asString(pick(item, "priceLabel", "PriceLabel")),
    unitPrice: asNumber(pick(item, "unitPrice", "UnitPrice"), 0),
    remainingQuantity: Math.max(
      0,
      Math.trunc(asNumber(pick(item, "remainingQuantity", "RemainingQuantity"), 0))
    ),
  };
}

/** 批次 DTO；不是对象（含 null）时返回 null，表示该组合还没填过。 */
export function normalizeSeasonalCardBatch(raw: unknown): SeasonalCardBatch | null {
  const item = asRecord(raw);
  if (!item) {
    return null;
  }
  return {
    batchGuid: asString(pick(item, "batchGuid", "BatchGuid", "BatchGUID")).trim(),
    storeCode: asString(pick(item, "storeCode", "StoreCode")),
    storeName: asString(pick(item, "storeName", "StoreName")),
    seasonYear: asNullableInt(pick(item, "seasonYear", "SeasonYear")),
    cardType: asSeasonalCardType(pick(item, "cardType", "CardType")),
    cardTypeName: asString(pick(item, "cardTypeName", "CardTypeName")),
    localSupplierCode: asString(pick(item, "localSupplierCode", "LocalSupplierCode")).trim(),
    supplierName: asString(pick(item, "supplierName", "SupplierName")).trim(),
    remark: asString(pick(item, "remark", "Remark")),
    submittedByName: asString(pick(item, "submittedByName", "SubmittedByName")),
    submittedAt: normalizeServerUtcTimestamp(pick(item, "submittedAt", "SubmittedAt")),
    totalQuantity: asNumber(pick(item, "totalQuantity", "TotalQuantity"), 0),
    totalAmount: asNumber(pick(item, "totalAmount", "TotalAmount"), 0),
    isCurrent: asBoolean(pick(item, "isCurrent", "IsCurrent")),
    lines: getArray(item, "lines", "Lines").map(normalizeSeasonalCardBatchLine),
  };
}

/** 节日固定 1-5 五项；服务端缺项时补成「没填过」，保证节日网格始终完整。 */
/** 日期字段只保留 yyyy-MM-dd；带时间的 ISO 字符串截掉时间部分，格式不对返回空字符串。 */
function asIsoDay(value: unknown) {
  const match = /^(\d{4}-\d{2}-\d{2})/.exec(asString(value).trim());
  return match ? match[1] : "";
}

export function normalizeSeasonalCardOverviewResponse(payload: unknown): SeasonalCardOverview {
  const data = asRecord(unwrapPayload(payload)) ?? {};
  const topSeasonYear = asNullableInt(pick(data, "seasonYear", "SeasonYear"));
  const holidaysByType = new Map<SeasonalCardType, SeasonalCardOverviewHoliday>();
  getArray(data, "holidays", "Holidays").forEach((raw) => {
    const item = asRecord(raw) ?? {};
    const cardType = asSeasonalCardType(pick(item, "cardType", "CardType"));
    if (cardType == null || holidaysByType.has(cardType)) {
      return;
    }
    const rawIsOpen = pick(item, "isOpen", "IsOpen");
    holidaysByType.set(cardType, {
      cardType,
      cardTypeName: asString(pick(item, "cardTypeName", "CardTypeName")),
      // 旧后端没有开放窗口字段：按开放处理，年份沿用顶层 seasonYear（旧后端回显请求年份）。
      isOpen: rawIsOpen === undefined ? true : asBoolean(rawIsOpen),
      seasonYear: asNullableInt(pick(item, "seasonYear", "SeasonYear")) ?? topSeasonYear,
      holidayDate: asIsoDay(pick(item, "holidayDate", "HolidayDate")),
      opensOn: asIsoDay(pick(item, "opensOn", "OpensOn")),
      closesOn: asIsoDay(pick(item, "closesOn", "ClosesOn")),
      currentBatch: normalizeSeasonalCardBatch(pick(item, "currentBatch", "CurrentBatch")),
    });
  });

  return {
    storeCode: asString(pick(data, "storeCode", "StoreCode")),
    seasonYear: topSeasonYear,
    today: asIsoDay(pick(data, "today", "Today")),
    localSupplierCode: asString(pick(data, "localSupplierCode", "LocalSupplierCode")).trim(),
    supplierName: asString(pick(data, "supplierName", "SupplierName")).trim(),
    holidays: ([1, 2, 3, 4, 5] as SeasonalCardType[]).map(
      (cardType) =>
        holidaysByType.get(cardType) ?? {
          cardType,
          cardTypeName: "",
          isOpen: true,
          seasonYear: topSeasonYear,
          holidayDate: "",
          opensOn: "",
          closesOn: "",
          currentBatch: null,
        }
    ),
  };
}

export function normalizeSeasonalCardBatchResponse(payload: unknown) {
  return payload == null ? null : normalizeSeasonalCardBatch(unwrapPayload(payload));
}

export function buildSeasonalCardOverviewQuery(query: SeasonalCardOverviewQuery) {
  return {
    storeCode: trimText(query.storeCode) ?? "",
    seasonYear: asNullableInt(query.seasonYear) ?? 0,
    localSupplierCode: trimText(query.localSupplierCode) ?? "",
  };
}

export function buildSeasonalCardBatchRequest(payload: SeasonalCardBatchPayload) {
  const remark = trimText(payload.remark);
  const expectedPreviousBatchGuid = trimText(payload.expectedPreviousBatchGuid ?? undefined);
  return {
    storeCode: trimText(payload.storeCode) ?? "",
    seasonYear: asNullableInt(payload.seasonYear) ?? 0,
    cardType: payload.cardType,
    localSupplierCode: trimText(payload.localSupplierCode) ?? "",
    // 没填过时必须显式传 null，服务端据此判断「预填后有没有人抢先提交」。
    expectedPreviousBatchGuid: expectedPreviousBatchGuid ?? null,
    ...(remark ? { remark } : {}),
    items: payload.items.map((item) => {
      const customUnitPrice = asNullableNumber(item.customUnitPrice);
      return {
        catalogGuid: trimText(item.catalogGuid) ?? "",
        remainingQuantity: Math.max(0, asNullableInt(item.remainingQuantity) ?? 0),
        ...(customUnitPrice == null ? {} : { customUnitPrice }),
      };
    }),
  };
}

export async function fetchSeasonalCardOverview(query: SeasonalCardOverviewQuery) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/overview`, {
    params: buildSeasonalCardOverviewQuery(query),
  });
  return normalizeSeasonalCardOverviewResponse(response.data);
}

export async function submitSeasonalCardBatch(payload: SeasonalCardBatchPayload) {
  const client = await getApiClient();
  const response = await client.post(
    `${BASE_PATH}/submissions/batch`,
    buildSeasonalCardBatchRequest(payload)
  );
  return normalizeSeasonalCardBatchResponse(response.data);
}

export async function fetchSeasonalCardCatalog() {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/catalog`);
  return normalizeSeasonalCardCatalogResponse(response.data);
}

export async function submitSeasonalCardSubmission(
  payload: SeasonalCardSubmissionPayload
) {
  const client = await getApiClient();
  const response = await client.post(
    `${BASE_PATH}/submissions`,
    buildSeasonalCardSubmissionPayload(payload)
  );
  return normalizeSeasonalCardSubmissionDetail(response.data);
}

export async function fetchSeasonalCardSubmissions(
  query: SeasonalCardSubmissionQuery
) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/submissions`, {
    params: buildSeasonalCardSubmissionQuery(query),
  });
  return normalizeSeasonalCardSubmissionsResponse(response.data);
}

export async function fetchSeasonalCardSubmissionDetail(submissionGuid: string) {
  const client = await getApiClient();
  const response = await client.get(
    `${BASE_PATH}/submissions/${encodeURIComponent(submissionGuid)}`
  );
  return normalizeSeasonalCardSubmissionDetail(response.data);
}
