import type { ProductReportDateRange } from "./date-ranges";
import { getDashboardCompareMode, getProductReportCompareRange } from "./date-ranges";
import { PRODUCT_PAGE_SIZE } from "./pagination";
import { DEFAULT_REPORT_SORT, isDefaultReportSort, type ReportSort } from "./sorting";
import { REPORT_QUERY_TIMEOUT_MS } from "../reports/report-config";
import {
  normalizeExecutiveBranchPerformance,
  type ExecutiveBranchPerformanceSnapshot,
} from "../reports/api";

export type SupplierReportKind = "australia" | "china";

export interface ProductReportRequestOptions {
  signal?: AbortSignal;
}

export type ProductReportStatisticStatus = "Fresh" | "Pending" | "Stale" | "Failed";
export type ProductReportCostStatus = "Complete" | "Missing" | "NoActivity";

export interface ProductReportSnapshot<T> {
  data: T;
  statisticStatus: ProductReportStatisticStatus;
  statisticMessage: string | null;
  statisticUpdatedAt: string | null;
  cacheVersion: string | null;
  isComplete: boolean;
  pollingExhausted: boolean;
  pollingAttemptCount: number;
}

export type ProductReportCacheVersionState = "pending" | "aligned" | "mismatch";
export type ProductReportCacheVersionSyncDecision = "wait" | "ready" | "refetch" | "exhausted";

interface ProductReportVersionedSnapshot {
  isComplete: boolean;
  cacheVersion: string | null;
}

/** 三块主报表只有同时 Fresh 且 cacheVersion 相同，才能作为同一批次展示。 */
export function getProductReportCacheVersionState(
  snapshots: readonly (ProductReportVersionedSnapshot | undefined)[],
): ProductReportCacheVersionState {
  if (
    snapshots.length === 0
    || snapshots.some((snapshot) => !snapshot?.isComplete || !snapshot.cacheVersion?.trim())
  ) return "pending";

  const versions = snapshots.map((snapshot) => snapshot!.cacheVersion!.trim());
  return versions.every((version) => version === versions[0]) ? "aligned" : "mismatch";
}

export function getProductReportCacheVersionSyncDecision(
  state: ProductReportCacheVersionState,
  attemptCount: number,
  isFetching: boolean,
  maxAttempts: number,
): ProductReportCacheVersionSyncDecision {
  // 在途请求可能暂时与旧缓存对齐；整组完成前不能清零协调重试次数。
  if (isFetching) return "wait";
  if (state === "aligned") return "ready";
  if (state !== "mismatch") return "wait";
  return attemptCount < Math.max(0, maxAttempts) ? "refetch" : "exhausted";
}

export interface ProductReportPollingOptions extends ProductReportRequestOptions {
  delaysMs?: readonly number[];
  deadlineMs?: number;
  now?: () => number;
  wait?: (delayMs: number, signal?: AbortSignal) => Promise<void>;
}

const PRODUCT_STATISTICS_POLL_DEADLINE_MS = 8_000;
const PRODUCT_STATISTICS_POLL_DELAYS_MS = [200, 400, 800, 1_600, 3_200, 6_400] as const;

export function getProductReportRequestConfig(signal?: AbortSignal) {
  return signal
    ? { timeout: REPORT_QUERY_TIMEOUT_MS, signal } as const
    : { timeout: REPORT_QUERY_TIMEOUT_MS } as const;
}

export interface ProductReportDateQuery {
  startDate: string;
  endDate: string;
  compareStartDate: string;
  compareEndDate: string;
  compareMode: "ByDate" | "ByWeek";
  branchCodes?: string[];
}

export interface ProductReportStoreOption {
  label: string;
  value: string;
  /** 门店 IANA 时区（如 Australia/Sydney）；旧后端不返回，营业额截止整点按固定 UTC+10 回退。 */
  timeZoneId?: string;
}

/** 分店营业额，作为中国供应商页签「分店中国货占比」的分母。 */
export interface ProductReportBranchRevenue {
  branchCode: string;
  branchName: string;
  revenue: number;
  compareRevenue: number;
}

export interface ProductReportTotalRevenue {
  revenue: number;
  compareRevenue: number;
  branches: ProductReportBranchRevenue[];
  isComplete: boolean;
  statisticsPending: boolean;
  statisticsExpectedBranchCount: number | null;
  statisticsSnapshotBranchCount: number;
  pollingExhausted: boolean;
  pollingAttemptCount: number;
  statisticStatus: ProductReportStatisticStatus;
  statisticMessage: string | null;
  statisticUpdatedAt: string | null;
  cacheVersion: string | null;
}

export interface SupplierReportRow {
  id: string;
  supplierCode: string;
  supplierName: string;
  revenue: number;
  compareRevenue: number;
  grossProfit: number | null;
  compareGrossProfit: number | null;
  grossMarginRate: number | null;
  compareGrossMarginRate: number | null;
  totalQuantity: number;
  compareTotalQuantity: number | null;
  averagePrice: number | null;
  compareAveragePrice: number | null;
  storeCount: number;
  orderCount: number;
  compareOrderCount: number;
  averageTransaction: number;
  compareAverageTransaction: number;
  costStatus: ProductReportCostStatus;
  compareCostStatus: ProductReportCostStatus;
}

export interface ProductReportProductRow {
  id: string;
  productCode: string;
  itemNumber: string;
  productImage: string | null;
  productName: string;
  quantity: number;
  compareQuantity: number;
  salesAmount: number;
  compareSalesAmount: number;
  grossProfit: number | null;
  compareGrossProfit: number | null;
  grossMarginRate: number | null;
  compareGrossMarginRate: number | null;
  averageUnitPrice: number;
  compareAverageUnitPrice: number;
  orderCount: number;
  compareOrderCount: number;
  costStatus: ProductReportCostStatus;
  compareCostStatus: ProductReportCostStatus;
}

export interface ProductReportProductPage {
  rows: ProductReportProductRow[];
  total: number;
  pageIndex: number;
  pageSize: number;
}

export interface SupplierBranchBreakdownRow {
  id: string;
  branchCode: string;
  branchName: string;
  supplierCode: string;
  supplierName: string;
  revenue: number;
  compareRevenue: number;
  grossProfit: number | null;
  compareGrossProfit: number | null;
  grossMarginRate: number | null;
  compareGrossMarginRate: number | null;
  totalQuantity: number;
  compareTotalQuantity: number | null;
  averagePrice: number | null;
  compareAveragePrice: number | null;
  orderCount: number;
  compareOrderCount: number;
  averageTransaction: number;
  compareAverageTransaction: number;
  costStatus: ProductReportCostStatus;
  compareCostStatus: ProductReportCostStatus;
}

/** 单个分店的中国货（全部中国供应商）合计；同期覆盖同期期间的全部中国供应商。 */
export interface ChinaSupplierBranchTotalRow {
  id: string;
  branchCode: string;
  branchName: string;
  revenue: number;
  compareRevenue: number;
  totalQuantity: number;
  compareTotalQuantity: number | null;
  supplierCount: number;
  grossProfit: number | null;
  compareGrossProfit: number | null;
  costStatus: ProductReportCostStatus;
  compareCostStatus: ProductReportCostStatus;
}

export interface ProductBranchBreakdownRow {
  id: string;
  branchCode: string;
  branchName: string;
  quantity: number;
  compareQuantity: number;
  discountedQuantity: number;
  salesAmount: number;
  compareSalesAmount: number;
  grossProfit: number | null;
  compareGrossProfit: number | null;
  grossMarginRate: number | null;
  compareGrossMarginRate: number | null;
  averageUnitPrice: number;
  compareAverageUnitPrice: number;
  costStatus: ProductReportCostStatus;
  compareCostStatus: ProductReportCostStatus;
}

async function getApiClient() {
  const { apiClient } = await import("@/shared/api/client");
  return apiClient;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function pick(raw: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    if (raw[key] !== undefined && raw[key] !== null) {
      return raw[key];
    }
  }
  return undefined;
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
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
  }
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

/**
 * 毛利依赖成本快照，后端缺少成本时必须保留 null，不能把它混同为实际毛利 0。
 */
function asNullableNumber(value: unknown): number | null {
  if (value === null || value === undefined) {
    return null;
  }
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function normalizeCostStatus(value: unknown, hasActivity: boolean): ProductReportCostStatus {
  switch (asString(value).trim().toLowerCase()) {
    case "complete": return "Complete";
    case "missing": return "Missing";
    case "noactivity": return "NoActivity";
    default:
      // 兼容旧接口：只有存在业务信号时才显示缺成本，避免把空同期误报为成本缺失。
      return hasActivity ? "Missing" : "NoActivity";
  }
}

function getRows(payload: unknown): unknown[] {
  if (Array.isArray(payload)) {
    return payload;
  }

  const root = asRecord(payload) ?? {};
  const data = pick(root, "items", "Items", "rows", "Rows", "data", "Data");
  if (Array.isArray(data)) {
    return data;
  }

  const nested = asRecord(data);
  return nested ? getRows(nested) : [];
}

function normalizeStatisticStatus(value: unknown): ProductReportStatisticStatus {
  switch (asString(value).trim().toLowerCase()) {
    case "fresh": return "Fresh";
    case "pending": return "Pending";
    case "stale": return "Stale";
    case "failed": return "Failed";
    // 报表行数据没有完整性契约时绝不能被误当作可展示的 Fresh 结果。
    case "": return "Pending";
    default: return "Failed";
  }
}

function getStatisticMetadata(payload: unknown) {
  const root = asRecord(payload);
  const rawStatus = root ? pick(root, "statisticStatus", "StatisticStatus") : undefined;
  const declaredStatus = normalizeStatisticStatus(rawStatus);
  const message = root ? asString(pick(root, "statisticMessage", "StatisticMessage")).trim() : "";
  const updatedAt = root ? asString(
    pick(root, "statisticUpdatedAt", "StatisticUpdatedAt"),
  ).trim() : "";
  const cacheVersion = root ? asString(pick(root, "cacheVersion", "CacheVersion")).trim() : "";
  const hasCompleteMetadata = rawStatus !== undefined
    && updatedAt.length > 0
    && Number.isFinite(Date.parse(updatedAt))
    && cacheVersion.length > 0
    && cacheVersion.toLowerCase() !== "none";
  // 根级状态、统计完成时间、快照版本共同证明行数据属于同一完整统计批次；
  // 任一字段缺失均按 Pending 处理，禁止旧裸列表或半包络绕过完整性校验。
  const statisticStatus = hasCompleteMetadata ? declaredStatus : "Pending";
  return {
    hasCompleteMetadata,
    statisticStatus,
    statisticMessage: message || null,
    statisticUpdatedAt: updatedAt || null,
    cacheVersion: cacheVersion || null,
    isComplete: statisticStatus === "Fresh",
  };
}

/** 将根级统计状态与业务数据分离，非 Fresh 的空 data 绝不能被页面视为业务空结果。 */
export function normalizeProductReportSnapshot<T>(
  payload: unknown,
  normalizeData: (value: unknown) => T,
): ProductReportSnapshot<T> {
  const metadata = getStatisticMetadata(payload);
  return {
    data: normalizeData(payload),
    statisticStatus: metadata.statisticStatus,
    statisticMessage: metadata.statisticMessage,
    statisticUpdatedAt: metadata.statisticUpdatedAt,
    cacheVersion: metadata.cacheVersion,
    isComplete: metadata.isComplete,
    pollingExhausted: false,
    pollingAttemptCount: 1,
  };
}

export async function pollProductReportSnapshot<TSnapshot extends Pick<
  ProductReportSnapshot<unknown>,
  "isComplete" | "pollingExhausted" | "pollingAttemptCount"
>>(
  loadSnapshot: (signal?: AbortSignal) => Promise<TSnapshot>,
  options: ProductReportPollingOptions = {},
): Promise<TSnapshot> {
  const delaysMs = options.delaysMs ?? PRODUCT_STATISTICS_POLL_DELAYS_MS;
  const deadlineMs = Math.max(0, options.deadlineMs ?? PRODUCT_STATISTICS_POLL_DEADLINE_MS);
  const now = options.now ?? Date.now;
  const wait = options.wait ?? waitForProductStatistics;
  const deadlineAt = now() + deadlineMs;
  let latest: TSnapshot | null = null;
  let pollingAttemptCount = 0;

  for (let attempt = 0; ; attempt += 1) {
    throwIfProductReportRequestAborted(options.signal);
    latest = await loadSnapshot(options.signal);
    pollingAttemptCount += 1;
    throwIfProductReportRequestAborted(options.signal);
    if (latest.isComplete) {
      return { ...latest, pollingExhausted: false, pollingAttemptCount } as TSnapshot;
    }

    const configuredDelayMs = delaysMs[attempt];
    const remainingMs = deadlineAt - now();
    if (configuredDelayMs === undefined || remainingMs <= 0) break;
    // 最后一段退避必须截断到真实截止时间，不能因固定 delay 把补算会话无限拖长。
    await wait(Math.min(Math.max(0, configuredDelayMs), remainingMs), options.signal);
  }

  return {
    ...(latest as TSnapshot),
    pollingExhausted: true,
    pollingAttemptCount,
  } as TSnapshot;
}

function throwIfProductReportRequestAborted(signal?: AbortSignal) {
  if (!signal?.aborted) return;
  const error = new Error("Product report request aborted");
  error.name = "AbortError";
  throw error;
}

function waitForProductStatistics(delayMs: number, signal?: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    try {
      throwIfProductReportRequestAborted(signal);
    } catch (error) {
      reject(error);
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, delayMs);
    const onAbort = () => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", onAbort);
      try {
        throwIfProductReportRequestAborted(signal);
      } catch (error) {
        reject(error);
      }
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

function appendListParams(params: URLSearchParams, key: string, values?: string[]) {
  values?.filter(Boolean).forEach((value) => params.append(key, value));
}

function buildBaseParams(query: ProductReportDateQuery) {
  const params = new URLSearchParams({
    startDate: query.startDate,
    endDate: query.endDate,
    compareStartDate: query.compareStartDate,
    compareEndDate: query.compareEndDate,
    compareMode: query.compareMode,
  });
  appendListParams(params, "branchCodes", query.branchCodes);
  return params;
}

export function buildProductReportDateQuery(
  range: ProductReportDateRange,
  branchCodes?: string[]
): ProductReportDateQuery {
  const compare = getProductReportCompareRange(range);
  return {
    startDate: range.startDate,
    endDate: range.endDate,
    compareStartDate: compare.startDate,
    compareEndDate: compare.endDate,
    compareMode: getDashboardCompareMode(range),
    branchCodes,
  };
}

export function normalizeStoreOptions(payload: unknown): ProductReportStoreOption[] {
  return getRows(payload)
    .map((raw, index) => {
      const item = asRecord(raw) ?? {};
      const value = asString(pick(item, "value", "Value", "storeCode", "StoreCode"), String(index));
      const timeZoneId = asString(pick(item, "timeZoneId", "TimeZoneId")).trim();
      return {
        value,
        label: asString(pick(item, "label", "Label", "storeName", "StoreName"), value),
        ...(timeZoneId ? { timeZoneId } : {}),
      };
    })
    .filter((item) => item.value.length > 0);
}

export function normalizeSupplierRows(payload: unknown): SupplierReportRow[] {
  return getRows(payload).map((raw, index) => {
    const item = asRecord(raw) ?? {};
    const supplierCode = asString(pick(item, "supplierCode", "SupplierCode"), `supplier-${index}`);
    const revenue = asNumber(pick(item, "totalAmount", "TotalAmount", "revenue", "Revenue"));
    const totalQuantity = asNumber(pick(item, "totalQuantity", "TotalQuantity"));
    const compareTotalQuantity = asNullableNumber(pick(item, "compareTotalQuantity", "CompareTotalQuantity", "totalQuantityLY", "TotalQuantityLY"));
    const orderCount = asNumber(pick(item, "orderCount", "OrderCount", "transactions", "Transactions"));
    const compareRevenue = asNumber(pick(item, "compareTotalAmount", "CompareTotalAmount", "revenueLY", "RevenueLY"));
    const compareOrderCount = asNumber(pick(item, "compareOrderCount", "CompareOrderCount", "orderCountLY", "OrderCountLY"));
    return {
      id: supplierCode || String(index),
      supplierCode,
      supplierName: asString(pick(item, "supplierName", "SupplierName"), supplierCode),
      revenue,
      compareRevenue,
      grossProfit: asNullableNumber(pick(item, "grossProfit", "GrossProfit")),
      compareGrossProfit: asNullableNumber(
        pick(item, "compareGrossProfit", "CompareGrossProfit", "grossProfitLY", "GrossProfitLY")
      ),
      grossMarginRate: asNullableNumber(
        pick(item, "grossMarginRate", "GrossMarginRate", "grossProfitRate", "GrossProfitRate")
      ),
      compareGrossMarginRate: asNullableNumber(
        pick(
          item,
          "compareGrossMarginRate",
          "CompareGrossMarginRate",
          "grossMarginRateLY",
          "GrossMarginRateLY",
          "compareGrossProfitRate",
          "CompareGrossProfitRate",
          "grossProfitRateLY",
          "GrossProfitRateLY"
        )
      ),
      totalQuantity: asNumber(pick(item, "totalQuantity", "TotalQuantity")),
      compareTotalQuantity,
      // 商品均价使用销量口径，不能复用以订单数为分母的客单价。
      averagePrice: totalQuantity !== 0 ? revenue / totalQuantity : null,
      compareAveragePrice: compareTotalQuantity !== null && compareTotalQuantity !== 0
        ? compareRevenue / compareTotalQuantity
        : null,
      storeCount: asNumber(pick(item, "storeCount", "StoreCount")),
      orderCount,
      compareOrderCount,
      averageTransaction: asNumber(
        pick(item, "averageTransaction", "AverageTransaction", "aov", "Aov"),
        orderCount > 0 ? revenue / orderCount : 0
      ),
      compareAverageTransaction: asNumber(
        pick(item, "compareAverageTransaction", "CompareAverageTransaction", "aovLY", "AovLY"),
        compareOrderCount > 0 ? compareRevenue / compareOrderCount : 0
      ),
      costStatus: normalizeCostStatus(
        pick(item, "costStatus", "CostStatus"),
        revenue !== 0 || totalQuantity !== 0 || orderCount !== 0,
      ),
      compareCostStatus: normalizeCostStatus(
        pick(item, "compareCostStatus", "CompareCostStatus"),
        compareRevenue !== 0 || compareOrderCount !== 0 || (compareTotalQuantity ?? 0) !== 0,
      ),
    };
  });
}

export function normalizeProductPage(payload: unknown): ProductReportProductPage {
  const root = asRecord(payload) ?? {};
  // 统计状态位于 API 根包络，分页字段位于 data 内层；两层都要兼容旧直出 DTO。
  const pageRoot = asRecord(pick(root, "data", "Data")) ?? root;
  const rows = getRows(pageRoot).map((raw, index) => {
    const item = asRecord(raw) ?? {};
    const productCode = asString(pick(item, "productCode", "ProductCode"), `product-${index}`);
    const salesAmount = asNumber(pick(item, "salesAmount", "SalesAmount", "amount", "Amount"));
    const quantity = asNumber(pick(item, "quantity", "Quantity"));
    const orderCount = asNumber(pick(item, "orderCount", "OrderCount"));
    return {
      id: productCode || String(index),
      productCode,
      itemNumber: asString(pick(item, "itemNumber", "ItemNumber", "barcode", "Barcode")),
      productImage: asString(pick(item, "productImage", "ProductImage"), "") || null,
      productName: asString(pick(item, "productName", "ProductName")),
      quantity,
      compareQuantity: asNumber(pick(item, "compareQuantity", "CompareQuantity", "quantityLY", "QuantityLY")),
      salesAmount,
      compareSalesAmount: asNumber(
        pick(item, "compareSalesAmount", "CompareSalesAmount", "salesAmountLY", "SalesAmountLY")
      ),
      grossProfit: asNullableNumber(pick(item, "grossProfit", "GrossProfit")),
      compareGrossProfit: asNullableNumber(
        pick(item, "compareGrossProfit", "CompareGrossProfit", "grossProfitLY", "GrossProfitLY")
      ),
      grossMarginRate: asNullableNumber(
        pick(item, "grossMarginRate", "GrossMarginRate", "grossProfitRate", "GrossProfitRate")
      ),
      compareGrossMarginRate: asNullableNumber(
        pick(
          item,
          "compareGrossMarginRate",
          "CompareGrossMarginRate",
          "grossMarginRateLY",
          "GrossMarginRateLY",
          "compareGrossProfitRate",
          "CompareGrossProfitRate",
          "grossProfitRateLY",
          "GrossProfitRateLY"
        )
      ),
      averageUnitPrice: asNumber(pick(item, "averageUnitPrice", "AverageUnitPrice", "unitPrice", "UnitPrice")),
      compareAverageUnitPrice: asNumber(
        pick(item, "compareAverageUnitPrice", "CompareAverageUnitPrice", "averageUnitPriceLY", "AverageUnitPriceLY")
      ),
      orderCount,
      compareOrderCount: asNumber(pick(item, "compareOrderCount", "CompareOrderCount", "orderCountLY", "OrderCountLY")),
      costStatus: normalizeCostStatus(
        pick(item, "costStatus", "CostStatus"),
        salesAmount !== 0 || quantity !== 0 || orderCount !== 0,
      ),
      compareCostStatus: normalizeCostStatus(
        pick(item, "compareCostStatus", "CompareCostStatus"),
        asNumber(pick(item, "compareSalesAmount", "CompareSalesAmount", "salesAmountLY", "SalesAmountLY")) !== 0
          || asNumber(pick(item, "compareQuantity", "CompareQuantity", "quantityLY", "QuantityLY")) !== 0
          || asNumber(pick(item, "compareOrderCount", "CompareOrderCount", "orderCountLY", "OrderCountLY")) !== 0,
      ),
    };
  });

  return {
    rows,
    total: asNumber(pick(pageRoot, "total", "Total"), rows.length),
    pageIndex: asNumber(pick(pageRoot, "pageIndex", "PageIndex"), 1),
    pageSize: asNumber(pick(pageRoot, "pageSize", "PageSize"), 50),
  };
}

export function normalizeTotalRevenue(payload: unknown): ProductReportTotalRevenue {
  // 逐店明细保留下来给分店中国货占比做分母；总额仍由同一批行求和，两者天然一致。
  const branches = getRows(payload).map<ProductReportBranchRevenue>((raw, index) => {
    const item = asRecord(raw) ?? {};
    const branchCode = asString(pick(item, "branchCode", "BranchCode", "storeCode", "StoreCode"), `branch-${index}`);
    return {
      branchCode,
      branchName: asString(pick(item, "branchName", "BranchName", "storeName", "StoreName"), branchCode),
      revenue: asNumber(pick(item, "revenue", "Revenue", "totalAmount", "TotalAmount")),
      compareRevenue: asNumber(
        pick(item, "revenueLY", "RevenueLY", "compareRevenue", "CompareRevenue", "totalAmountLY", "TotalAmountLY"),
      ),
    };
  });
  const totals = branches.reduce<Pick<ProductReportTotalRevenue, "revenue" | "compareRevenue">>(
    (sum, branch) => ({
      revenue: sum.revenue + branch.revenue,
      compareRevenue: sum.compareRevenue + branch.compareRevenue,
    }),
    { revenue: 0, compareRevenue: 0 }
  );
  return {
    ...totals,
    branches,
    isComplete: true,
    statisticsPending: false,
    statisticsExpectedBranchCount: null,
    statisticsSnapshotBranchCount: getRows(payload).length,
    pollingExhausted: false,
    pollingAttemptCount: 1,
    statisticStatus: "Fresh",
    statisticMessage: null,
    statisticUpdatedAt: null,
    cacheVersion: null,
  };
}

/**
 * 商品页总额和营业额排行必须共享同一份完整快照契约，不能把补算中的分店子集求和后当成最终总额。
 */
export function normalizeProductReportTotalRevenue(payload: unknown): ProductReportTotalRevenue {
  const metadata = getStatisticMetadata(payload);
  const branchSnapshot = normalizeExecutiveBranchPerformance(payload);
  const summary = summarizeExecutiveBranchPerformance(branchSnapshot);
  return {
    ...summary,
    // 商品总额复用营业额排行数据时，必须同时满足商品统计批次与分店快照完整性。
    // 不能因为总额可由部分行求和，就把缺元数据结果展示为最终数字。
    isComplete: metadata.isComplete && branchSnapshot.isComplete,
    statisticsPending: !metadata.isComplete || !branchSnapshot.isComplete,
    statisticStatus: metadata.statisticStatus,
    statisticMessage: metadata.statisticMessage,
    statisticUpdatedAt: metadata.statisticUpdatedAt,
    cacheVersion: metadata.cacheVersion,
  };
}

function summarizeExecutiveBranchPerformance(
  snapshot: ExecutiveBranchPerformanceSnapshot,
): ProductReportTotalRevenue {
  const totals = normalizeTotalRevenue(snapshot.rows);
  return {
    ...totals,
    isComplete: snapshot.isComplete,
    statisticsPending: snapshot.statisticsPending,
    statisticsExpectedBranchCount: snapshot.statisticsExpectedBranchCount,
    statisticsSnapshotBranchCount: snapshot.statisticsSnapshotBranchCount,
    pollingExhausted: snapshot.pollingExhausted,
    pollingAttemptCount: snapshot.pollingAttemptCount,
    statisticStatus: snapshot.isComplete ? "Fresh" : "Pending",
    statisticMessage: null,
    statisticUpdatedAt: snapshot.statisticUpdatedAt,
    cacheVersion: snapshot.cacheVersion,
  };
}

export function normalizeSupplierReportSnapshot(payload: unknown) {
  return normalizeProductReportSnapshot(payload, normalizeSupplierRows);
}

export function normalizeProductReportProductPageSnapshot(payload: unknown) {
  return normalizeProductReportSnapshot(payload, normalizeProductPage);
}

export function normalizeSupplierBranchReportSnapshot(payload: unknown) {
  return normalizeProductReportSnapshot(payload, normalizeSupplierBranchRows);
}

export function normalizeProductBranchReportSnapshot(payload: unknown) {
  return normalizeProductReportSnapshot(payload, normalizeProductBranchRows);
}

export function normalizeChinaSupplierBranchTotalsSnapshot(payload: unknown) {
  return normalizeProductReportSnapshot(payload, normalizeChinaSupplierBranchTotalRows);
}

export function normalizeChinaSupplierBranchTotalRows(payload: unknown): ChinaSupplierBranchTotalRow[] {
  return getRows(payload).map((raw, index) => {
    const item = asRecord(raw) ?? {};
    const branchCode = asString(pick(item, "branchCode", "BranchCode"), `branch-${index}`);
    const revenue = asNumber(pick(item, "totalAmount", "TotalAmount", "revenue", "Revenue"));
    const compareRevenue = asNumber(pick(item, "compareTotalAmount", "CompareTotalAmount", "revenueLY", "RevenueLY"));
    const totalQuantity = asNumber(pick(item, "totalQuantity", "TotalQuantity"));
    const compareTotalQuantity = asNullableNumber(pick(item, "compareTotalQuantity", "CompareTotalQuantity"));
    return {
      id: branchCode || String(index),
      branchCode,
      branchName: asString(pick(item, "branchName", "BranchName", "storeName", "StoreName"), branchCode),
      revenue,
      compareRevenue,
      totalQuantity,
      compareTotalQuantity,
      supplierCount: asNumber(pick(item, "supplierCount", "SupplierCount")),
      grossProfit: asNullableNumber(pick(item, "grossProfit", "GrossProfit")),
      compareGrossProfit: asNullableNumber(pick(item, "compareGrossProfit", "CompareGrossProfit")),
      costStatus: normalizeCostStatus(pick(item, "costStatus", "CostStatus"), revenue !== 0 || totalQuantity !== 0),
      compareCostStatus: normalizeCostStatus(
        pick(item, "compareCostStatus", "CompareCostStatus"),
        compareRevenue !== 0 || (compareTotalQuantity ?? 0) !== 0,
      ),
    };
  });
}

export function normalizeSupplierBranchRows(payload: unknown): SupplierBranchBreakdownRow[] {
  return getRows(payload).map((raw, index) => {
    const item = asRecord(raw) ?? {};
    const branchCode = asString(pick(item, "branchCode", "BranchCode"), `branch-${index}`);
    const supplierCode = asString(pick(item, "supplierCode", "SupplierCode"));
    const revenue = asNumber(pick(item, "totalAmount", "TotalAmount", "revenue", "Revenue"));
    const orderCount = asNumber(pick(item, "orderCount", "OrderCount"));
    const compareRevenue = asNumber(pick(item, "compareTotalAmount", "CompareTotalAmount", "revenueLY", "RevenueLY"));
    const compareOrderCount = asNumber(pick(item, "compareOrderCount", "CompareOrderCount"));
    const totalQuantity = asNumber(pick(item, "totalQuantity", "TotalQuantity"));
    const compareTotalQuantity = asNullableNumber(pick(item, "compareTotalQuantity", "CompareTotalQuantity", "totalQuantityLY", "TotalQuantityLY"));
    return {
      id: `${branchCode}-${supplierCode || index}`,
      branchCode,
      branchName: asString(pick(item, "branchName", "BranchName", "storeName", "StoreName"), branchCode),
      supplierCode,
      supplierName: asString(pick(item, "supplierName", "SupplierName"), supplierCode),
      revenue,
      compareRevenue,
      grossProfit: asNullableNumber(pick(item, "grossProfit", "GrossProfit")),
      compareGrossProfit: asNullableNumber(
        pick(item, "compareGrossProfit", "CompareGrossProfit", "grossProfitLY", "GrossProfitLY")
      ),
      grossMarginRate: asNullableNumber(
        pick(item, "grossMarginRate", "GrossMarginRate", "grossProfitRate", "GrossProfitRate")
      ),
      compareGrossMarginRate: asNullableNumber(
        pick(
          item,
          "compareGrossMarginRate",
          "CompareGrossMarginRate",
          "grossMarginRateLY",
          "GrossMarginRateLY",
          "compareGrossProfitRate",
          "CompareGrossProfitRate",
          "grossProfitRateLY",
          "GrossProfitRateLY"
        )
      ),
      totalQuantity,
      compareTotalQuantity,
      // 商品均价使用销量口径，不能复用以订单数为分母的客单价。
      averagePrice: totalQuantity !== 0 ? revenue / totalQuantity : null,
      compareAveragePrice: compareTotalQuantity !== null && compareTotalQuantity !== 0
        ? compareRevenue / compareTotalQuantity
        : null,
      orderCount,
      compareOrderCount,
      averageTransaction: asNumber(
        pick(item, "averageTransaction", "AverageTransaction"),
        orderCount > 0 ? revenue / orderCount : 0
      ),
      compareAverageTransaction: asNumber(
        pick(item, "compareAverageTransaction", "CompareAverageTransaction"),
        compareOrderCount > 0 ? compareRevenue / compareOrderCount : 0
      ),
      costStatus: normalizeCostStatus(
        pick(item, "costStatus", "CostStatus"),
        revenue !== 0 || totalQuantity !== 0 || orderCount !== 0,
      ),
      compareCostStatus: normalizeCostStatus(
        pick(item, "compareCostStatus", "CompareCostStatus"),
        compareRevenue !== 0 || compareOrderCount !== 0 || (compareTotalQuantity ?? 0) !== 0,
      ),
    };
  });
}

export function normalizeProductBranchRows(payload: unknown): ProductBranchBreakdownRow[] {
  return getRows(payload).map((raw, index) => {
    const item = asRecord(raw) ?? {};
    const branchCode = asString(pick(item, "branchCode", "BranchCode"), `branch-${index}`);
    const quantity = asNumber(pick(item, "quantity", "Quantity"));
    const salesAmount = asNumber(pick(item, "salesAmount", "SalesAmount"));
    const compareQuantity = asNumber(pick(item, "compareQuantity", "CompareQuantity", "quantityLY", "QuantityLY"));
    const compareSalesAmount = asNumber(
      pick(item, "compareSalesAmount", "CompareSalesAmount", "salesAmountLY", "SalesAmountLY")
    );
    const discountedQuantity = asNumber(pick(item, "discountedQuantity", "DiscountedQuantity"));
    return {
      id: branchCode || String(index),
      branchCode,
      branchName: asString(pick(item, "branchName", "BranchName", "storeName", "StoreName"), branchCode),
      quantity,
      compareQuantity,
      discountedQuantity,
      salesAmount,
      compareSalesAmount,
      grossProfit: asNullableNumber(pick(item, "grossProfit", "GrossProfit")),
      compareGrossProfit: asNullableNumber(
        pick(item, "compareGrossProfit", "CompareGrossProfit", "grossProfitLY", "GrossProfitLY")
      ),
      grossMarginRate: asNullableNumber(
        pick(item, "grossMarginRate", "GrossMarginRate", "grossProfitRate", "GrossProfitRate")
      ),
      compareGrossMarginRate: asNullableNumber(
        pick(
          item,
          "compareGrossMarginRate",
          "CompareGrossMarginRate",
          "grossMarginRateLY",
          "GrossMarginRateLY",
          "compareGrossProfitRate",
          "CompareGrossProfitRate",
          "grossProfitRateLY",
          "GrossProfitRateLY"
        )
      ),
      averageUnitPrice: asNumber(
        pick(item, "averageUnitPrice", "AverageUnitPrice"),
        quantity > 0 ? salesAmount / quantity : 0
      ),
      compareAverageUnitPrice: asNumber(
        pick(item, "compareAverageUnitPrice", "CompareAverageUnitPrice", "averageUnitPriceLY", "AverageUnitPriceLY"),
        compareQuantity > 0 ? compareSalesAmount / compareQuantity : 0
      ),
      costStatus: normalizeCostStatus(
        pick(item, "costStatus", "CostStatus"),
        salesAmount !== 0 || quantity !== 0 || discountedQuantity !== 0,
      ),
      compareCostStatus: normalizeCostStatus(
        pick(item, "compareCostStatus", "CompareCostStatus"),
        compareSalesAmount !== 0 || compareQuantity !== 0,
      ),
    };
  });
}

export async function fetchProductReportStoreOptions(options: ProductReportRequestOptions = {}) {
  const apiClient = await getApiClient();
  const response = await apiClient.get("/react/v1/mobile-reports/store-options", {
    signal: options.signal,
  });
  return normalizeStoreOptions(response.data);
}

export async function fetchProductReportTotalRevenue(
  query: ProductReportDateQuery,
  options: ProductReportPollingOptions = {},
) {
  const apiClient = await getApiClient();
  const params = buildBaseParams(query);
  // 商品页总额还依赖商品日统计完整性；显式请求元数据，避免普通营业额页面增加统计表读取。
  params.set("includeProductStatisticMetadata", "true");
  return pollProductReportSnapshot(
    async (signal) => {
      const response = await apiClient.get("/react/v1/dashboard/executive-branch-performance", {
        params,
        ...getProductReportRequestConfig(signal),
      });
      return normalizeProductReportTotalRevenue(response.data);
    },
    options,
  );
}

export async function fetchSupplierReportRows(
  kind: SupplierReportKind,
  query: ProductReportDateQuery,
  topN = 1000,
  options: ProductReportPollingOptions = {},
) {
  const apiClient = await getApiClient();
  const params = buildBaseParams(query);
  params.set("topN", String(topN));
  const endpoint =
    kind === "china"
      ? "/react/v1/dashboard/china-supplier-sales-rank"
      : "/react/v1/dashboard/supplier-sales-rank";
  return pollProductReportSnapshot(async (signal) => {
    const response = await apiClient.get(endpoint, { params, ...getProductReportRequestConfig(signal) });
    return normalizeSupplierReportSnapshot(response.data);
  }, options);
}

/** 中国供应商页签「分店中国货占比」的分子；与供应商排行、商品明细共用同一统计批次版本。 */
export async function fetchChinaSupplierBranchTotals(
  query: ProductReportDateQuery,
  options: ProductReportPollingOptions = {},
) {
  const apiClient = await getApiClient();
  const params = buildBaseParams(query);
  return pollProductReportSnapshot(async (signal) => {
    const response = await apiClient.get("/react/v1/dashboard/china-supplier-branch-totals", {
      params,
      ...getProductReportRequestConfig(signal),
    });
    return normalizeChinaSupplierBranchTotalsSnapshot(response.data);
  }, options);
}

export async function fetchProductReportProductRows(
  kind: SupplierReportKind,
  query: ProductReportDateQuery,
  supplierCodes: string[] | undefined,
  pageIndex: number,
  pageSize = PRODUCT_PAGE_SIZE,
  productSearch?: string,
  sort: ReportSort = DEFAULT_REPORT_SORT,
  options: ProductReportPollingOptions = {},
) {
  const apiClient = await getApiClient();
  const params = buildProductReportProductParams(kind, query, supplierCodes, pageIndex, pageSize, productSearch, sort);
  return pollProductReportSnapshot(async (signal) => {
    const response = await apiClient.get("/react/v1/dashboard/enhanced-sales-product-details", {
      params,
      ...getProductReportRequestConfig(signal),
    });
    return normalizeProductReportProductPageSnapshot(response.data);
  }, options);
}

export function buildProductReportProductParams(
  kind: SupplierReportKind,
  query: ProductReportDateQuery,
  supplierCodes: string[] | undefined,
  pageIndex: number,
  pageSize = PRODUCT_PAGE_SIZE,
  productSearch?: string,
  sort: ReportSort = DEFAULT_REPORT_SORT,
) {
  const params = buildBaseParams(query);
  params.set("pageIndex", String(pageIndex));
  params.set("pageSize", String(pageSize));
  if (kind === "china") {
    // 即使未点选具体供应商，也要让后端明确限定为全部中国供应商商品，避免回退为全商品。
    params.set("supplierScope", "china");
  }
  appendListParams(params, kind === "china" ? "chinaSupplierCodes" : "localSupplierCodes", supplierCodes);
  const normalizedProductSearch = productSearch?.trim();
  if (normalizedProductSearch) {
    params.set("productSearch", normalizedProductSearch);
  }
  // 商品明细是服务端分页，排序必须交给后端在分页前完成；默认金额降序不带参数，请求与旧版一致。
  if (!isDefaultReportSort(sort)) {
    params.set("sortField", sort.field);
    params.set("sortOrder", sort.order);
  }
  return params;
}

export async function fetchSupplierBranchBreakdown(
  kind: SupplierReportKind,
  query: ProductReportDateQuery,
  supplierCode: string,
  options: ProductReportPollingOptions = {},
) {
  const apiClient = await getApiClient();
  const params = buildBaseParams(query);
  params.append("supplierCodes", supplierCode);
  const endpoint =
    kind === "china"
      ? "/react/v1/dashboard/china-supplier-store-sales"
      : "/react/v1/dashboard/supplier-store-sales";
  return pollProductReportSnapshot(async (signal) => {
    const response = await apiClient.get(endpoint, { params, ...getProductReportRequestConfig(signal) });
    return normalizeSupplierBranchReportSnapshot(response.data);
  }, options);
}

export async function fetchProductBranchBreakdown(
  query: ProductReportDateQuery,
  productCode: string,
  options: ProductReportPollingOptions = {},
) {
  const apiClient = await getApiClient();
  const params = buildBaseParams(query);
  params.set("productCode", productCode);
  return pollProductReportSnapshot(async (signal) => {
    const response = await apiClient.get("/react/v1/dashboard/product-sales-by-branches", {
      params,
      ...getProductReportRequestConfig(signal),
    });
    return normalizeProductBranchReportSnapshot(response.data);
  }, options);
}
