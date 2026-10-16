import type {
  GridFilterModel,
  GridRequest,
  GridResult,
  InvoiceDetailPageSize,
  InvoiceDetailsGridQuery,
  InvoiceGridQuery,
  InvoiceListPageSize,
  LocalSupplierOption,
  LocalSupplierInvoice,
  LocalSupplierInvoiceItem,
} from "@/modules/local-supplier-invoices/types";

const BASE_PATH = "/react/v1/local-supplier-invoices";
const ACTIVE_LOCAL_SUPPLIERS_PATH = "/react/v1/local-suppliers/active";
const LIST_PAGE_SIZES: InvoiceListPageSize[] = [20, 50, 100];
const DETAIL_PAGE_SIZES: InvoiceDetailPageSize[] = [50, 100, 200];

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
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
  }
  return fallback;
}

function asNullableInt(value: unknown): number | null {
  const parsed = asNullableNumber(value);
  return parsed == null ? null : Math.trunc(parsed);
}

function normalizePage(page?: number) {
  return page && Number.isFinite(page) && page > 0 ? Math.trunc(page) : 1;
}

function normalizePageSize<T extends number>(
  requested: number | undefined,
  allowed: readonly T[],
  fallback: T
): T {
  return allowed.includes(requested as T) ? (requested as T) : fallback;
}

function getPayloadItems(payload: unknown): unknown[] {
  const root = asRecord(payload) ?? {};
  const items = pick(root, "items", "Items");
  return Array.isArray(items) ? items : [];
}

function getEnvelopeData(payload: unknown): unknown {
  const root = asRecord(payload) ?? {};
  return pick(root, "data", "Data");
}

function getPayloadTotal(payload: unknown): number {
  const root = asRecord(payload) ?? {};
  return asNumber(pick(root, "total", "Total", "totalCount", "TotalCount"), 0);
}

function textFilter(value?: string): GridFilterModel | null {
  const trimmed = value?.trim();
  return trimmed
    ? { filterType: "text", type: "contains", filter: trimmed }
    : null;
}

function dateRangeFilter(from?: string, to?: string): GridFilterModel | null {
  const start = from?.trim();
  const end = to?.trim();
  if (start && end) {
    return { filterType: "date", type: "inRange", filter: start, filterTo: end };
  }
  if (start) {
    return { filterType: "date", type: "greaterThanOrEqual", filter: start };
  }
  if (end) {
    return { filterType: "date", type: "lessThanOrEqual", filter: end };
  }
  return null;
}

export function buildInvoiceGridRequest(query: InvoiceGridQuery): GridRequest {
  const pageSize = normalizePageSize(query.pageSize, LIST_PAGE_SIZES, 20);
  const page = normalizePage(query.page);
  const startRow = (page - 1) * pageSize;
  const filterModel: Record<string, GridFilterModel> = {};
  const storeCode = query.filters?.storeCode?.trim();
  const storeFilter = storeCode
    ? { filterType: "text" as const, type: "equals", filter: storeCode }
    : null;
  const supplierFilter = textFilter(query.filters?.supplierCode);
  const invoiceFilter = textFilter(query.filters?.invoiceNo);
  const productFilter = textFilter(query.filters?.productKeyword);
  const inboundStatus = query.filters?.inboundStatus;
  const orderDateFilter = dateRangeFilter(
    query.filters?.orderDateFrom,
    query.filters?.orderDateTo
  );

  if (storeFilter) filterModel.storeCode = storeFilter;
  if (supplierFilter) filterModel.supplierCode = supplierFilter;
  if (invoiceFilter) filterModel.invoiceNo = invoiceFilter;
  // 后端 Grid 把 ProductKeyword 当作「按商品匹配进货单」的特殊列：用相关 EXISTS 在明细里匹配，不是单据头字段。
  if (productFilter) filterModel.ProductKeyword = productFilter;
  if (inboundStatus === 0 || inboundStatus === 1 || inboundStatus === 2) {
    filterModel.inboundStatus = {
      filterType: "number",
      type: "equals",
      // 后端 Grid Filter 契约使用 string，避免 JSON 模型绑定在控制器入口返回 400。
      filter: String(inboundStatus),
    };
  }
  if (orderDateFilter) filterModel.OrderDate = orderDateFilter;

  return {
    startRow,
    endRow: startRow + pageSize,
    pageSize,
    filterModel: Object.keys(filterModel).length ? filterModel : undefined,
    sortModel: [
      {
        colId: query.sort?.colId ?? "OrderDate",
        sort: query.sort?.direction ?? "desc",
      },
    ],
  };
}

export function buildInvoiceDetailsGridRequest(query: InvoiceDetailsGridQuery): GridRequest {
  const pageSize = normalizePageSize(query.pageSize, DETAIL_PAGE_SIZES, 50);
  const page = normalizePage(query.page);
  const startRow = (page - 1) * pageSize;
  const filterModel: Record<string, GridFilterModel> = {};
  const keyword = textFilter(query.keyword);

  if (keyword) {
    filterModel.productKeyword = keyword;
  }

  if (query.priceChange === "up" || query.priceChange === "down") {
    filterModel.priceChange = {
      filterType: "text",
      type: "equals",
      filter: query.priceChange,
    };
  }

  return {
    startRow,
    endRow: startRow + pageSize,
    pageSize,
    filterModel: Object.keys(filterModel).length ? filterModel : undefined,
  };
}

export function normalizeInvoice(raw: unknown): LocalSupplierInvoice {
  const item = asRecord(raw) ?? {};
  return {
    invoiceGuid: asString(pick(item, "invoiceGuid", "InvoiceGUID", "invoiceGUID")),
    storeCode: asString(pick(item, "storeCode", "StoreCode")),
    storeName: asString(pick(item, "storeName", "StoreName")),
    supplierCode: asString(pick(item, "supplierCode", "SupplierCode")),
    supplierName: asString(pick(item, "supplierName", "SupplierName")),
    invoiceNo: asString(pick(item, "invoiceNo", "InvoiceNo")),
    orderDate: asString(pick(item, "orderDate", "OrderDate")),
    inboundDate: asString(pick(item, "inboundDate", "InboundDate")),
    totalAmount: asNullableNumber(pick(item, "totalAmount", "TotalAmount")),
    receivedTotalAmount: asNullableNumber(pick(item, "receivedTotalAmount", "ReceivedTotalAmount")),
    priceIncreaseItemCount: asNumber(
      pick(item, "priceIncreaseItemCount", "PriceIncreaseItemCount"),
      0
    ),
    priceDecreaseItemCount: asNumber(
      pick(item, "priceDecreaseItemCount", "PriceDecreaseItemCount"),
      0
    ),
    flowStatus: asNullableInt(pick(item, "flowStatus", "FlowStatus")),
    inboundStatus: asNullableInt(pick(item, "inboundStatus", "InboundStatus")),
    remarks: asString(pick(item, "remarks", "Remarks")),
    createdAt: asString(pick(item, "createdAt", "CreatedAt")),
    updatedAt: asString(pick(item, "updatedAt", "UpdatedAt")),
  };
}

export function normalizeInvoiceItem(raw: unknown): LocalSupplierInvoiceItem {
  const item = asRecord(raw) ?? {};
  return {
    detailGuid: asString(pick(item, "detailGuid", "DetailGUID", "detailGUID")),
    invoiceGuid: asString(pick(item, "invoiceGuid", "InvoiceGUID", "invoiceGUID")),
    storeCode: asString(pick(item, "storeCode", "StoreCode")),
    supplierCode: asString(pick(item, "supplierCode", "SupplierCode")),
    productCode: asString(pick(item, "productCode", "ProductCode")),
    storeProductCode: asString(pick(item, "storeProductCode", "StoreProductCode")),
    itemNumber: asString(pick(item, "itemNumber", "ItemNumber")),
    barcode: asString(pick(item, "barcode", "Barcode")),
    productName: asString(pick(item, "productName", "ProductName")),
    specification: asString(pick(item, "specification", "Specification")),
    unit: asString(pick(item, "unit", "Unit")),
    quantity: asNullableNumber(pick(item, "quantity", "Quantity")),
    lastPurchasePrice: asNullableNumber(pick(item, "lastPurchasePrice", "LastPurchasePrice")),
    purchasePrice: asNullableNumber(pick(item, "purchasePrice", "PurchasePrice")),
    retailPrice: asNullableNumber(pick(item, "retailPrice", "RetailPrice")),
    amount: asNullableNumber(pick(item, "amount", "Amount")),
    productImage: asString(pick(item, "productImage", "ProductImage")),
  };
}

export function normalizeLocalSupplierOption(raw: unknown): LocalSupplierOption {
  const item = asRecord(raw) ?? {};
  return {
    supplierCode: asString(
      pick(item, "supplierCode", "SupplierCode", "localSupplierCode", "LocalSupplierCode")
    ),
    supplierName: asString(
      pick(item, "supplierName", "SupplierName", "localSupplierName", "LocalSupplierName", "name", "Name")
    ),
  };
}

export function normalizeInvoiceGridResponse(payload: unknown): GridResult<LocalSupplierInvoice> {
  return {
    items: getPayloadItems(payload).map(normalizeInvoice),
    total: getPayloadTotal(payload),
  };
}

export function normalizeInvoiceDetailsGridResponse(
  payload: unknown
): GridResult<LocalSupplierInvoiceItem> {
  return {
    items: getPayloadItems(payload).map(normalizeInvoiceItem),
    total: getPayloadTotal(payload),
  };
}

export function normalizeActiveLocalSuppliersResponse(payload: unknown): LocalSupplierOption[] {
  if (Array.isArray(payload)) {
    return payload.map(normalizeLocalSupplierOption).filter((item) => item.supplierCode);
  }

  const data = getEnvelopeData(payload);
  return Array.isArray(data)
    ? data.map(normalizeLocalSupplierOption).filter((item) => item.supplierCode)
    : [];
}

export async function fetchInvoices(query: InvoiceGridQuery) {
  const client = await getApiClient();
  const response = await client.post(`${BASE_PATH}/grid`, buildInvoiceGridRequest(query));
  return normalizeInvoiceGridResponse(response.data);
}

export async function fetchInvoice(invoiceGuid: string) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}`);
  return normalizeInvoice(response.data);
}

export async function fetchInvoiceDetailsGrid(
  invoiceGuid: string,
  query: InvoiceDetailsGridQuery
) {
  const client = await getApiClient();
  const response = await client.post(
    `${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/details/grid`,
    buildInvoiceDetailsGridRequest(query)
  );
  return normalizeInvoiceDetailsGridResponse(response.data);
}

export async function fetchActiveLocalSuppliers() {
  const client = await getApiClient();
  const response = await client.get(ACTIVE_LOCAL_SUPPLIERS_PATH);
  return normalizeActiveLocalSuppliersResponse(response.data);
}
