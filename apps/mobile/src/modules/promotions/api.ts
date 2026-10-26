import type {
  PagedResult,
  PromotionCopyRequest,
  PromotionDetail,
  PromotionFormValues,
  PromotionGridQuery,
  PromotionListItem,
  PromotionProductItem,
  PromotionScopeType,
  PromotionStatusCounts,
  PromotionStatusFilter,
  PromotionStoreItem,
} from "@/modules/promotions/types";

const BASE_PATH = "/react/v1/promotions";
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

function asString(value: unknown, fallback = "") {
  if (typeof value === "string") {
    return value;
  }
  if (typeof value === "number" && Number.isFinite(value)) {
    return String(value);
  }
  return fallback;
}

function asNumber(value: unknown, fallback = 0) {
  if (value === null || value === undefined) {
    return fallback;
  }
  if (typeof value === "string" && !value.trim()) {
    return fallback;
  }
  const parsed = typeof value === "string" ? Number(value) : value;
  return typeof parsed === "number" && Number.isFinite(parsed) ? parsed : fallback;
}

function asInt(value: unknown, fallback = 0) {
  return Math.trunc(asNumber(value, fallback));
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

function getArray(raw: unknown, ...keys: string[]) {
  if (Array.isArray(raw)) {
    return raw;
  }
  const record = asRecord(raw) ?? {};
  const value = pick(record, ...keys);
  return Array.isArray(value) ? value : [];
}

function unwrapPayload(payload: unknown) {
  if (Array.isArray(payload)) {
    return payload;
  }
  const root = asRecord(payload) ?? {};
  return pick(root, "data", "Data") ?? root;
}

function normalizeScopeType(value: unknown): PromotionScopeType | null {
  const text = asString(value).trim();
  if (text === "StoreOnly" || text === "MultiStore" || text === "Headquarters") {
    return text;
  }
  return null;
}

function normalizeProduct(raw: unknown): PromotionProductItem {
  const item = asRecord(raw) ?? {};
  return {
    id: trimText(pick(item, "id", "Id")),
    productCode: asString(pick(item, "productCode", "ProductCode")).trim(),
    unitWeight: Math.max(1, asInt(pick(item, "unitWeight", "UnitWeight", "weight", "Weight"), 1)),
  };
}

function normalizeStore(raw: unknown): PromotionStoreItem {
  const item = asRecord(raw) ?? {};
  return {
    id: trimText(pick(item, "id", "Id")),
    storeCode: asString(pick(item, "storeCode", "StoreCode")).trim(),
  };
}

export function buildPromotionGridPayload(query: PromotionGridQuery) {
  const pageNumber = normalizePage(query.page);
  const pageSize = normalizePageSize(query.pageSize);
  return {
    storeCode: trimText(query.storeCode) ?? "",
    globalSearch: trimText(query.keyword),
    startRow: (pageNumber - 1) * pageSize,
    pageSize,
    sortModel: query.sortModel?.length ? query.sortModel : undefined,
    status: normalizeStatusFilter(query.status),
  };
}

const STATUS_FILTERS: readonly PromotionStatusFilter[] = ["active", "upcoming", "disabled", "ended", "all"];

function normalizeStatusFilter(value?: PromotionStatusFilter) {
  // 「全部」不传参数，旧后端也能正常返回。
  return value && value !== "all" && STATUS_FILTERS.includes(value) ? value : undefined;
}

export function normalizeStatusCounts(payload: unknown): PromotionStatusCounts {
  const record = asRecord(unwrapPayload(payload)) ?? {};
  return {
    active: Math.max(0, asInt(pick(record, "active", "Active"), 0)),
    upcoming: Math.max(0, asInt(pick(record, "upcoming", "Upcoming"), 0)),
    disabled: Math.max(0, asInt(pick(record, "disabled", "Disabled"), 0)),
    ended: Math.max(0, asInt(pick(record, "ended", "Ended"), 0)),
    total: Math.max(0, asInt(pick(record, "total", "Total"), 0)),
  };
}

export function buildPromotionPayload(payload: PromotionFormValues) {
  const storeCode = trimText(payload.storeCode) ?? "";
  const maxApplications = asInt(payload.maxApplicationsPerOrder, 0);

  return {
    name: trimText(payload.name) ?? "",
    description: trimText(payload.description),
    effectiveStart: trimText(payload.effectiveStart) ?? "",
    effectiveEnd: trimText(payload.effectiveEnd) ?? "",
    isEnabled: payload.isEnabled ?? true,
    isExclusive: payload.isExclusive ?? true,
    priority: asInt(payload.priority, 0),
    applyQuantity: asInt(payload.applyQuantity, 0),
    fixedPrice: asNumber(payload.fixedPrice, 0),
    maxApplicationsPerOrder: maxApplications > 0 ? maxApplications : undefined,
    products: (payload.products ?? [])
      .map((item) => ({
        productCode: trimText(item.productCode) ?? "",
        unitWeight: Math.max(1, asInt(item.unitWeight, 1)),
      }))
      .filter((item) => item.productCode),
    stores: storeCode ? [{ storeCode }] : [],
  };
}

export function buildPromotionCopyPayload(payload: PromotionCopyRequest) {
  return {
    sourcePromotionId: trimText(payload.sourcePromotionId) ?? "",
    storeCode: trimText(payload.storeCode) ?? "",
    name: trimText(payload.name),
  };
}

export function normalizePromotion(raw: unknown): PromotionListItem {
  const item = asRecord(raw) ?? {};
  const products = getArray(item, "products", "Products", "details", "Details").map(normalizeProduct);
  const stores = getArray(item, "stores", "Stores").map(normalizeStore);

  return {
    id: asString(pick(item, "id", "Id", "promotionId", "PromotionId")),
    name: asString(pick(item, "name", "Name", "promotionName", "PromotionName")),
    description: trimText(pick(item, "description", "Description")),
    effectiveStart: asString(pick(item, "effectiveStart", "EffectiveStart")),
    effectiveEnd: asString(pick(item, "effectiveEnd", "EffectiveEnd")),
    isEnabled: asBoolean(pick(item, "isEnabled", "IsEnabled")),
    isExclusive: asBoolean(pick(item, "isExclusive", "IsExclusive")),
    priority: asInt(pick(item, "priority", "Priority"), 0),
    applyQuantity: asInt(pick(item, "applyQuantity", "ApplyQuantity"), 0),
    fixedPrice: asNumber(pick(item, "fixedPrice", "FixedPrice"), 0),
    maxApplicationsPerOrder: asInt(pick(item, "maxApplicationsPerOrder", "MaxApplicationsPerOrder"), 0) || undefined,
    productsCount: asInt(pick(item, "productsCount", "ProductsCount"), products.length),
    storesCount: asInt(pick(item, "storesCount", "StoresCount"), stores.length),
    products,
    stores,
    scopeType: normalizeScopeType(pick(item, "scopeType", "ScopeType")),
    canEditInStoreScope: asBoolean(pick(item, "canEditInStoreScope", "CanEditInStoreScope")),
    canCopyToStore: asBoolean(pick(item, "canCopyToStore", "CanCopyToStore")),
  };
}

export function normalizePromotionsResponse(payload: unknown): PagedResult<PromotionListItem> {
  const data = unwrapPayload(payload);
  const record = asRecord(data) ?? {};
  const pageSize = asInt(pick(record, "pageSize", "PageSize", "limit", "Limit"), 20);
  return {
    items: getArray(data, "items", "Items", "rows", "Rows").map(normalizePromotion),
    total: asInt(pick(record, "total", "Total", "totalCount", "TotalCount"), 0),
    pageNumber: asInt(pick(record, "pageNumber", "PageNumber", "page", "Page"), 1),
    pageSize,
  };
}

export function normalizeValidPromotionsResponse(payload: unknown): PromotionListItem[] {
  return getArray(unwrapPayload(payload), "items", "Items").map(normalizePromotion);
}

export function normalizePromotionDetail(payload: unknown): PromotionDetail | null {
  const data = unwrapPayload(payload);
  const record = asRecord(
    pick(asRecord(data) ?? {}, "item", "Item", "promotion", "Promotion")
  ) ?? asRecord(data);
  return record ? normalizePromotion(record) : null;
}

export async function fetchPromotions(query: PromotionGridQuery) {
  const client = await getApiClient();
  const response = await client.post(`${BASE_PATH}/store/grid`, buildPromotionGridPayload(query));
  return normalizePromotionsResponse(response.data);
}

export async function fetchPromotionStatusCounts(storeCode: string, keyword?: string) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/store/status-counts`, {
    params: { storeCode: storeCode.trim(), keyword: trimText(keyword) },
  });
  return normalizeStatusCounts(response.data);
}

/** 商品查询页「本商品促销」：当前分店相关、未结束（含未开始、已停用）的促销。 */
export async function fetchStorePromotionsByProduct(productCode: string, storeCode: string) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/store/by-product`, {
    params: {
      productCode: productCode.trim(),
      storeCode: storeCode.trim(),
    },
  });
  return normalizeValidPromotionsResponse(response.data);
}

export async function fetchValidPromotionsByProduct(productCode: string, storeCode: string) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/valid/by-product`, {
    params: {
      productCode: productCode.trim(),
      storeCode: storeCode.trim(),
    },
  });
  return normalizeValidPromotionsResponse(response.data);
}

export async function fetchPromotionDetail(id: string, storeCode: string) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/store/${encodeURIComponent(id)}`, {
    params: { storeCode },
  });
  return normalizePromotionDetail(response.data);
}

export async function createPromotion(payload: PromotionFormValues) {
  const client = await getApiClient();
  const response = await client.post(`${BASE_PATH}/store`, buildPromotionPayload(payload), {
    params: { storeCode: payload.storeCode },
  });
  return normalizePromotionDetail(response.data);
}

export async function updatePromotion(id: string, payload: PromotionFormValues) {
  const client = await getApiClient();
  const response = await client.put(`${BASE_PATH}/store/${encodeURIComponent(id)}`, buildPromotionPayload(payload), {
    params: { storeCode: payload.storeCode },
  });
  return normalizePromotionDetail(response.data);
}

export async function copyPromotionToStore(payload: PromotionCopyRequest) {
  const client = await getApiClient();
  const response = await client.post(`${BASE_PATH}/store/copy`, buildPromotionCopyPayload(payload));
  return normalizePromotionDetail(response.data);
}

/**
 * 提前结束本店独享促销：只把结束时间改为现在，开关与其余字段保持原样（用户定：不同时停用）。
 * 店级更新接口是整体替换，所以必须先取完整详情再回写，避免丢商品或改动排他 / 优先级。
 */
export async function endPromotionNow(id: string, storeCode: string, now: Date = new Date()) {
  const detail = await fetchPromotionDetail(id, storeCode);
  if (!detail) {
    throw new Error("PROMOTION_NOT_FOUND");
  }
  return updatePromotion(id, {
    id: detail.id,
    name: detail.name,
    description: detail.description,
    storeCode,
    effectiveStart: detail.effectiveStart,
    effectiveEnd: now.toISOString(),
    isEnabled: detail.isEnabled,
    isExclusive: detail.isExclusive,
    priority: detail.priority,
    applyQuantity: detail.applyQuantity,
    fixedPrice: detail.fixedPrice,
    maxApplicationsPerOrder: detail.maxApplicationsPerOrder ?? null,
    products: detail.products,
  });
}

export async function setPromotionEnabled(id: string, storeCode: string, enable: boolean) {
  const client = await getApiClient();
  const response = await client.post(`${BASE_PATH}/store/${encodeURIComponent(id)}/enable`, null, {
    params: { storeCode, enable },
  });
  return Boolean(response.data);
}
