import type {
  BarcodeMatchedProduct,
  CheckProductsJobSnapshot,
  CheckProductsSummary,
  GridFilterModel,
  GridRequest,
  GridResult,
  InvoiceDetailPageSize,
  InvoiceDetailsGridQuery,
  InvoiceGridQuery,
  InvoiceListPageSize,
  InvoiceSalesAnalysis,
  InvoiceSalesAnalysisRow,
  LocalSupplierOption,
  LocalSupplierInvoice,
  LocalSupplierInvoiceItem,
  ProductCheckRowResult,
  UpdateInvoiceHeaderRequest,
} from "@/modules/local-supplier-invoices/types";
import { DEFAULT_INVOICE_DATE_FIELD } from "@/modules/local-supplier-invoices/types";
import { toCreatedAtInstantRange } from "@/modules/local-supplier-invoices/date-range";
import type { InvoiceDetailUpsertItem } from "@/modules/local-supplier-invoices/detail/inline-edit";
import type { BatchEditFields } from "@/modules/local-supplier-invoices/detail/batch-edit";
import type { BatchExecuteRequest } from "@/modules/local-supplier-invoices/detail/batch-execute";

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

function asBool(value: unknown, fallback = false): boolean {
  if (typeof value === "boolean") return value;
  if (value === 1 || value === "1" || value === "true" || value === "True") return true;
  if (value === 0 || value === "0" || value === "false" || value === "False") return false;
  return fallback;
}

function asNullableBool(value: unknown): boolean | null {
  if (value === null || value === undefined || value === "") return null;
  return asBool(value, false);
}

function asStringArray(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((entry): entry is string => typeof entry === "string" && entry.trim() !== "")
    : [];
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
  const dateField = query.filters?.dateField ?? DEFAULT_INVOICE_DATE_FIELD;
  const dateRange = { from: query.filters?.dateFrom, to: query.filters?.dateTo };
  const productChecked = query.filters?.productChecked;

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
  if (dateField === "orderDate") {
    // 订单日期是业务日期，直接按天传，后端包含结束日整天。
    const orderDateFilter = dateRangeFilter(dateRange.from, dateRange.to);
    if (orderDateFilter) filterModel.OrderDate = orderDateFilter;
  } else {
    // 创建日期库里存 UTC：换算成带时区的时刻区间再传（见 toCreatedAtInstantRange）。
    const createdAtRange = toCreatedAtInstantRange(dateRange);
    if (createdAtRange) {
      filterModel.CreatedAt = { filterType: "date", type: "inRange", ...createdAtRange };
    }
  }
  // 后端按有效明细是否全部完成检测过滤；false 表示「待检测」，必须显式传 "false"，不能省略。
  if (productChecked === true || productChecked === false) {
    filterModel.IsProductChecked = {
      filterType: "text",
      type: "equals",
      filter: String(productChecked),
    };
  }
  // 后端只认 true，false 不收窄；关闭时不带该条件，请求体与旧版一致。
  if (query.filters?.hasUncreatedProducts) {
    filterModel.HasUncreatedProducts = { filterType: "text", type: "equals", filter: "true" };
  }

  return {
    startRow,
    endRow: startRow + pageSize,
    pageSize,
    filterModel: Object.keys(filterModel).length ? filterModel : undefined,
    sortModel: [
      {
        colId: query.sort?.colId ?? "CreatedAt",
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
    // 缺失 = 后端没返回汇总（旧版），保持 null，避免把「不知道」误报成待检测。
    isProductChecked: asNullableBool(pick(item, "isProductChecked", "IsProductChecked")),
    uncheckedDetailCount: asNumber(pick(item, "uncheckedDetailCount", "UncheckedDetailCount"), 0),
    newProductDetailCount: asNumber(pick(item, "newProductDetailCount", "NewProductDetailCount"), 0),
    createdHereProductDetailCount: asNumber(
      pick(item, "createdHereProductDetailCount", "CreatedHereProductDetailCount"),
      0
    ),
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
    additionalBarcodes: asStringArray(pick(item, "additionalBarcodes", "AdditionalBarcodes")),
    productType: asNullableInt(pick(item, "productType", "ProductType")),
    isCreatedByThisInvoice: asBool(
      pick(item, "isCreatedByThisInvoice", "IsCreatedByThisInvoice")
    ),
    existingProductCount: asNullableNumber(pick(item, "existingProductCount", "ExistingProductCount")),
    barcodeStatus: asNumber(pick(item, "barcodeStatus", "BarcodeStatus"), 0),
    barcodeMatchCount: asNumber(pick(item, "barcodeMatchCount", "BarcodeMatchCount"), 0),
    activityType: asNumber(pick(item, "activityType", "ActivityType"), 0),
    discountRate: asNullableNumber(pick(item, "discountRate", "DiscountRate")),
    autoPricing: asNullableBool(pick(item, "autoPricing", "AutoPricing")),
    pricingFloatRate: asNullableNumber(pick(item, "pricingFloatRate", "PricingFloatRate")),
    newAutoRetailPrice: asNullableNumber(pick(item, "newAutoRetailPrice", "NewAutoRetailPrice")),
    isSpecialProduct: asBool(pick(item, "isSpecialProduct", "IsSpecialProduct")),
    storeIsAutoPricing: asNullableBool(pick(item, "storeIsAutoPricing", "StoreIsAutoPricing")),
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

// ---------------------------------------------------------------------------
// 一期新增：全量明细读取 + 写接口（与 Web localSupplierInvoiceService 同路径、同口径）
// 写接口需要 LocalPurchase.Edit；success=false 的业务失败由 apiClient 拦截器解包时直接抛错。
// ---------------------------------------------------------------------------

/**
 * batch-upsert 请求体的一项：以 detailGUID 定位行，其余为本次要写入的字段。
 * 常规保存用 InvoiceDetailUpsertItem（口径见 detail/inline-edit）；「选用匹配商品」只写 productCode。
 */
export type InvoiceDetailUpsertPayload =
  | InvoiceDetailUpsertItem
  | { detailGUID: string; productCode: string };

/** GET {guid}/details 返回的是整张单的全部明细（Web 一次性加载、前端筛选），不是分页。 */
export function normalizeInvoiceDetailsResponse(payload: unknown): LocalSupplierInvoiceItem[] {
  const rows = Array.isArray(payload) ? payload : getEnvelopeData(payload);
  return Array.isArray(rows) ? rows.map(normalizeInvoiceItem) : [];
}

export function normalizeBarcodeMatchedProduct(raw: unknown): BarcodeMatchedProduct {
  const item = asRecord(raw) ?? {};
  return {
    productCode: asString(pick(item, "productCode", "ProductCode")),
    productName: asString(pick(item, "productName", "ProductName")),
    supplierCode: asString(pick(item, "supplierCode", "SupplierCode")),
    supplierName: asString(pick(item, "supplierName", "SupplierName")),
    itemNumber: asString(pick(item, "itemNumber", "ItemNumber")),
    barcode: asString(pick(item, "barcode", "Barcode")),
    productImage: asString(pick(item, "productImage", "ProductImage")),
    isMultiCode: asBool(pick(item, "isMultiCode", "IsMultiCode")),
    isBundle: asBool(pick(item, "isBundle", "IsBundle")),
    productType: asNullableInt(pick(item, "productType", "ProductType")),
  };
}

export function normalizeProductsByBarcodeResponse(payload: unknown): BarcodeMatchedProduct[] {
  const root = asRecord(payload) ?? {};
  const list = pick(root, "matchedProducts", "MatchedProducts");
  return Array.isArray(list) ? list.map(normalizeBarcodeMatchedProduct) : [];
}

export function normalizeCheckProductsResponse(payload: unknown): ProductCheckRowResult[] {
  const root = asRecord(payload) ?? {};
  const results = pick(root, "results", "Results");
  if (!Array.isArray(results)) return [];
  return results.map((raw) => {
    const row = asRecord(raw) ?? {};
    const info = asRecord(pick(row, "productInfo", "ProductInfo")) ?? {};
    return {
      detailGuid: asString(pick(row, "detailGuid", "DetailGUID", "detailGUID")),
      productStatus: asNumber(pick(row, "productStatus", "ProductStatus"), 0),
      barcodeStatus: asNumber(pick(row, "barcodeStatus", "BarcodeStatus"), 0),
      existingProductCount: asNumber(pick(row, "existingProductCount", "ExistingProductCount"), 0),
      barcodeMatchCount: asNumber(pick(row, "barcodeMatchCount", "BarcodeMatchCount"), 0),
      productCode: asString(pick(info, "productCode", "ProductCode")),
    };
  });
}

export async function fetchInvoiceDetailsAll(invoiceGuid: string) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/details`);
  return normalizeInvoiceDetailsResponse(response.data);
}

/** 保存表头；返回最新表头（响应里没有的名称字段由调用方保留旧值）。 */
export async function updateInvoiceHeader(invoiceGuid: string, request: UpdateInvoiceHeaderRequest) {
  const client = await getApiClient();
  const response = await client.put(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}`, request);
  return normalizeInvoice(response.data);
}

export async function batchUpsertInvoiceDetails(
  invoiceGuid: string,
  items: InvoiceDetailUpsertPayload[]
) {
  const client = await getApiClient();
  await client.post(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/details/batch-upsert`, items);
}

/** 改行操作类型：立即落库，不计入「未保存修改」（与 Web 一致）。 */
export async function updateInvoiceDetailAction(
  invoiceGuid: string,
  detailGuid: string,
  action: number
) {
  const client = await getApiClient();
  await client.put(
    `${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/details/${encodeURIComponent(detailGuid)}/action`,
    { action }
  );
}

export async function deleteInvoiceDetails(invoiceGuid: string, detailGuids: string[]) {
  const client = await getApiClient();
  await client.delete(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/details`, {
    data: detailGuids,
  });
}

export async function fetchProductsByBarcode(invoiceGuid: string, barcode: string) {
  const client = await getApiClient();
  const response = await client.get(
    `${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/products-by-barcode`,
    { params: { barcode } }
  );
  return normalizeProductsByBarcodeResponse(response.data);
}

/** 同步商品检测（只检传入的行）；后端会把结果写回明细，调用方随后刷新明细即可。 */
export async function checkInvoiceProducts(invoiceGuid: string, detailGuids: string[]) {
  const client = await getApiClient();
  const response = await client.post(`${BASE_PATH}/check-products`, { invoiceGuid, detailGuids });
  return normalizeCheckProductsResponse(response.data);
}

// ---------------------------------------------------------------------------
// 二期 A：整单商品检测（后台任务）+ 单张进货单销量分析
// ---------------------------------------------------------------------------

function buildCheckSummaryFromResults(results: unknown): CheckProductsSummary | null {
  if (!Array.isArray(results)) return null;
  const rows = normalizeCheckProductsResponse({ results });
  return {
    total: rows.length,
    productExists: rows.filter((row) => row.productStatus === 1).length,
    productNotExists: rows.filter((row) => row.productStatus === 2).length,
    barcodeNormal: rows.filter((row) => row.barcodeStatus === 1).length,
    barcodeAbnormal: rows.filter((row) => row.barcodeStatus === 2).length,
  };
}

/** 检测汇总：优先用后端给的 summary，缺失时按逐行结果自行统计；两者都没有返回 null。 */
export function normalizeCheckProductsSummary(payload: unknown): CheckProductsSummary | null {
  const root = asRecord(payload) ?? {};
  const summary = asRecord(pick(root, "summary", "Summary"));
  if (summary) {
    return {
      total: asNumber(pick(summary, "total", "Total"), 0),
      productExists: asNumber(pick(summary, "productExists", "ProductExists"), 0),
      productNotExists: asNumber(pick(summary, "productNotExists", "ProductNotExists"), 0),
      barcodeNormal: asNumber(pick(summary, "barcodeNormal", "BarcodeNormal"), 0),
      barcodeAbnormal: asNumber(pick(summary, "barcodeAbnormal", "BarcodeAbnormal"), 0),
    };
  }
  return buildCheckSummaryFromResults(pick(root, "results", "Results"));
}

/** 任务快照；接口客户端通常已解开 {success,data} 信封，这里仍兼容未解开的情况。 */
export function normalizeCheckProductsJob(payload: unknown): CheckProductsJobSnapshot {
  const root = asRecord(payload) ?? {};
  const job = asRecord(pick(root, "data", "Data")) ?? root;
  return {
    jobId: asString(pick(job, "jobId", "JobId", "jobID")),
    status: asString(pick(job, "status", "Status")),
    message: asString(pick(job, "message", "Message")),
    summary: normalizeCheckProductsSummary(pick(job, "result", "Result")),
  };
}

/** 同一张单已有检测任务在跑时后端返回 409 并带 existingJobId：直接接着等那个任务，不重复提交。 */
function getExistingCheckJobId(error: unknown): string | null {
  const response = (error as { response?: { status?: number; data?: unknown } } | null)?.response;
  if (response?.status !== 409) return null;
  const root = asRecord(response.data) ?? {};
  const data = asRecord(pick(root, "data", "Data")) ?? {};
  const jobId = asString(pick(data, "existingJobId", "ExistingJobId"));
  return jobId || null;
}

/** 后端后台任务接口分批发布时 404 表示当前环境还没有该端点，调用方可回退到同步检测。 */
export function isMissingCheckJobEndpoint(error: unknown): boolean {
  return (error as { response?: { status?: number } } | null)?.response?.status === 404;
}

/** 提交整单（或指定行）商品检测任务；detailGuids 缺省 = 整张单。需要 LocalPurchase.Edit。 */
export async function startCheckProductsJob(
  invoiceGuid: string,
  detailGuids?: string[]
): Promise<CheckProductsJobSnapshot> {
  const client = await getApiClient();
  try {
    const response = await client.post(`${BASE_PATH}/check-products/jobs`, { invoiceGuid, detailGuids });
    return normalizeCheckProductsJob(response.data);
  } catch (error) {
    const existingJobId = getExistingCheckJobId(error);
    if (existingJobId) {
      return { jobId: existingJobId, status: "Running", message: "", summary: null };
    }
    throw error;
  }
}

export async function fetchCheckProductsJob(invoiceGuid: string, jobId: string) {
  const client = await getApiClient();
  const response = await client.get(
    `${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/check-products/jobs/${encodeURIComponent(jobId)}`
  );
  return normalizeCheckProductsJob(response.data);
}

/** 旧同步检测：只用于后台任务接口 404 的回退，返回汇总。 */
export async function checkInvoiceProductsSync(invoiceGuid: string, detailGuids?: string[]) {
  const client = await getApiClient();
  const response = await client.post(`${BASE_PATH}/check-products`, { invoiceGuid, detailGuids });
  return normalizeCheckProductsSummary(response.data);
}

// ---------------------------------------------------------------------------
// 二期 B/C：批量设置操作、批量编辑、批量执行（仅管理员入口；后端还要求能访问全部启用分店）
// ---------------------------------------------------------------------------

/** 批量设置操作类型：立即落库（与单行改操作类型一样不计入未保存修改）。 */
export async function batchSetInvoiceDetailAction(invoiceGuid: string, detailGuids: string[], action: number) {
  const client = await getApiClient();
  await client.put(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/details/batch-action`, { detailGuids, action });
}

/** 批量编辑价格类字段：items 只带主键，改哪些字段由 editFields 的 update* 开关决定。 */
export async function batchUpdateInvoiceDetails(invoiceGuid: string, detailGuids: string[], editFields: BatchEditFields) {
  const client = await getApiClient();
  await client.post(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/details/batch-update`, {
    items: detailGuids.map((detailGUID) => ({ detailGUID })),
    editFields,
  });
}

export interface BatchExecuteResult {
  createdProducts: number;
  updatedPurchasePrices: number;
  updatedStorePrices: number;
  updatedItemNumbers: number;
  addedMultiCodes: number;
  skipped: number;
  failed: number;
  errors: string[];
}

export function normalizeBatchExecuteResult(payload: unknown): BatchExecuteResult {
  const root = asRecord(payload) ?? {};
  // 成功时客户端通常已解开 {success,data}；失败体的逐行结果在 details 里。
  const data = asRecord(pick(root, "data", "Data")) ?? asRecord(pick(root, "details", "Details")) ?? root;
  const errors = pick(data, "errors", "Errors");
  return {
    createdProducts: asNumber(pick(data, "createdProducts", "CreatedProducts"), 0),
    updatedPurchasePrices: asNumber(pick(data, "updatedPurchasePrices", "UpdatedPurchasePrices"), 0),
    updatedStorePrices: asNumber(pick(data, "updatedStorePrices", "UpdatedStorePrices"), 0),
    updatedItemNumbers: asNumber(pick(data, "updatedItemNumbers", "UpdatedItemNumbers"), 0),
    addedMultiCodes: asNumber(pick(data, "addedMultiCodes", "AddedMultiCodes"), 0),
    skipped: asNumber(pick(data, "skipped", "Skipped"), 0),
    failed: asNumber(pick(data, "failed", "Failed"), 0),
    errors: Array.isArray(errors) ? errors.map((item) => asString(item)).filter(Boolean) : [],
  };
}

/** 批量执行失败的分类：后端失败体 {success:false, message, code, details}。 */
export interface BatchExecuteFailure {
  code: string;
  message: string;
  errors: string[];
}

export function getBatchExecuteFailure(error: unknown): BatchExecuteFailure | null {
  const data = (error as { response?: { data?: unknown } } | null)?.response?.data;
  const root = asRecord(data);
  if (!root) return null;
  return {
    code: asString(pick(root, "code", "Code")),
    message: asString(pick(root, "message", "Message")),
    errors: normalizeBatchExecuteResult({ details: pick(root, "details", "Details") }).errors,
  };
}

/** 同步执行（没有后台任务）；成功的行后端写回操作类型 99「已执行」。 */
export async function batchExecuteInvoiceDetails(invoiceGuid: string, request: BatchExecuteRequest) {
  const client = await getApiClient();
  const response = await client.post(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/details/batch-execute`, {
    invoiceGuid,
    ...request,
  });
  return normalizeBatchExecuteResult(response.data);
}

function asNullableString(value: unknown): string | null {
  const text = asString(value).trim();
  return text ? text : null;
}

export function normalizeInvoiceSalesAnalysisRow(raw: unknown): InvoiceSalesAnalysisRow | null {
  const row = asRecord(raw);
  if (!row) return null;
  const detailGuid = asString(pick(row, "detailGUID", "DetailGUID", "detailGuid"));
  if (!detailGuid) return null;
  return {
    detailGuid,
    productCode: asString(pick(row, "productCode", "ProductCode")),
    itemNumber: asString(pick(row, "itemNumber", "ItemNumber")),
    barcode: asString(pick(row, "barcode", "Barcode")),
    productName: asString(pick(row, "productName", "ProductName")),
    quantity: asNullableNumber(pick(row, "quantity", "Quantity")),
    purchasePrice: asNullableNumber(pick(row, "purchasePrice", "PurchasePrice")),
    retailPrice: asNullableNumber(pick(row, "retailPrice", "RetailPrice")),
    amount: asNullableNumber(pick(row, "amount", "Amount")),
    salesQty30: asNumber(pick(row, "salesQty30", "SalesQty30"), 0),
    salesQty60: asNumber(pick(row, "salesQty60", "SalesQty60"), 0),
    salesQty90: asNumber(pick(row, "salesQty90", "SalesQty90"), 0),
    previousPurchaseDate: asNullableString(pick(row, "previousPurchaseDate", "PreviousPurchaseDate")),
    previousToCurrentDays: asNullableNumber(pick(row, "previousToCurrentDays", "PreviousToCurrentDays")),
    salesSincePreviousPurchase: asNullableNumber(
      pick(row, "salesSincePreviousPurchase", "SalesSincePreviousPurchase")
    ),
    salesStatisticLastUpdate: asNullableString(
      pick(row, "salesStatisticLastUpdate", "SalesStatisticLastUpdate")
    ),
  };
}

const DEFAULT_SALES_ANALYSIS_NOTE =
  "进货后30/60/90天销量从本次进货日期次日开始统计；上次到本次区间销量仍按历史区间显示。";

export function normalizeInvoiceSalesAnalysis(payload: unknown): InvoiceSalesAnalysis {
  const root = asRecord(payload) ?? {};
  const data = asRecord(pick(root, "data", "Data")) ?? root;
  const items = pick(data, "items", "Items");
  return {
    invoiceGuid: asString(pick(data, "invoiceGUID", "InvoiceGUID", "invoiceGuid")),
    invoiceNo: asString(pick(data, "invoiceNo", "InvoiceNo")),
    storeName: asString(pick(data, "storeName", "StoreName")),
    supplierName: asString(pick(data, "supplierName", "SupplierName")),
    orderDate: asNullableString(pick(data, "orderDate", "OrderDate")),
    inboundDate: asNullableString(pick(data, "inboundDate", "InboundDate")),
    analysisDate: asNullableString(pick(data, "analysisDate", "AnalysisDate")),
    salesStatisticLastUpdate: asNullableString(pick(data, "salesStatisticLastUpdate", "SalesStatisticLastUpdate")),
    calculationNote: asString(pick(data, "calculationNote", "CalculationNote")) || DEFAULT_SALES_ANALYSIS_NOTE,
    items: Array.isArray(items)
      ? items.map(normalizeInvoiceSalesAnalysisRow).filter((row): row is InvoiceSalesAnalysisRow => row !== null)
      : [],
  };
}

/** 单张进货单销量分析（只读，需要 LocalPurchase.View 且单据在可管理分店范围内）。 */
export async function fetchInvoiceSalesAnalysis(invoiceGuid: string) {
  const client = await getApiClient();
  const response = await client.get(`${BASE_PATH}/${encodeURIComponent(invoiceGuid)}/sales-analysis`);
  return normalizeInvoiceSalesAnalysis(response.data);
}
