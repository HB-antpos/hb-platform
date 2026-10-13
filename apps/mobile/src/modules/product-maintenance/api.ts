import type { AxiosRequestConfig } from "axios";
import { apiClient } from "@/shared/api/client";
import { useDeviceStore } from "@/store/device-store";
import type {
  CreateProductWithPricesRequest,
  CreateProductWithPricesResult,
  CreateSetCodeRequest,
  DeleteSetCodeResult,
  LocalSupplierOption,
  MultiCodeEditableItem,
  ProductDetail,
  ProductCodePage,
  ProductLookupItem,
  ScanLabelResult,
  ScanLabelPrintTarget,
  ProductHqSyncOperation,
  ProductSetCodeItem,
  StoreClearancePriceItem,
  StorePriceEditable,
  StoreProductLookupRequest,
  EvaluateAutoPricingRequest,
  EvaluateAutoPricingResult,
  UpdateSetCodeRequest,
  ProductNameHistoryItem,
  UpdateProductNameRequest,
  UpdateProductNameResult,
  UpdateProductTypeRequest,
  UpdateProductTypeResult,
  UpdateMultiCodeRequest,
  UpdateStorePriceRequest,
  UpsertClearancePriceRequest,
  WarehousePriceSyncRequest,
  WarehousePriceSyncResult,
} from "@/modules/product-maintenance/types";
import {
  buildCreateProductWithPricesPayload,
  normalizeActiveLocalSuppliersResponse,
  normalizeCreateProductWithPricesResult,
} from "@/modules/product-maintenance/api-normalization";
import { normalizeWarehousePriceSyncResponse } from "@/modules/product-maintenance/warehouse-price-sync";
import { normalizeHqSyncOperation } from "@/modules/product-maintenance/hq-sync";

const BASE_PATH = "/react/v1/store-product-maintenance";
const PRODUCTS_PATH = "/react/v1/products";
const ACTIVE_LOCAL_SUPPLIERS_PATH = "/react/v1/local-suppliers/active";
/**
 * 查询类请求的专用超时：默认 30s 在「Wi-Fi 已连接但无外网」时会让每次扫码等 30s 才能降级离线；
 * 这三个接口后端本就期望毫秒级响应，10s 足够覆盖慢网络。
 */
export const PRODUCT_QUERY_REQUEST_TIMEOUT_MS = 10_000;

function buildRequestConfig(): AxiosRequestConfig {
  const session = useDeviceStore.getState().session;
  if (!session?.hardwareId || !session.authCode) {
    return {};
  }

  return {
    headers: {
      "X-Device-Id": session.hardwareId,
      "X-Auth-Code": session.authCode,
    },
  };
}

function toNumber(value: unknown): number | null {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

function normalizeDiscountRate(value: unknown): number | null {
  const numeric = toNumber(value);
  if (numeric == null || numeric < 0) {
    return null;
  }

  if (numeric <= 1) {
    return numeric;
  }

  if (numeric <= 100) {
    return numeric / 100;
  }

  return null;
}

export function normalizeLookupItem(payload: unknown): ProductLookupItem {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  return {
    productCode: String(data.productCode ?? data.ProductCode ?? ""),
    productName: String(data.productName ?? data.ProductName ?? ""),
    itemNumber: (data.itemNumber ?? data.ItemNumber ?? null) as string | null,
    barcode: (data.barcode ?? data.Barcode ?? null) as string | null,
    productImage: (data.productImage ?? data.ProductImage ?? null) as string | null,
    matchSource: (data.matchSource ?? data.MatchSource ?? null) as string | null,
    matchValue: (data.matchValue ?? data.MatchValue ?? null) as string | null,
    productTypeLabel: (data.productTypeLabel ?? data.ProductTypeLabel ?? null) as string | null,
    grade: (data.grade ?? data.Grade ?? null) as string | null,
  };
}

function normalizeStorePrice(payload: unknown): StorePriceEditable | null {
  if (!payload || typeof payload !== "object") {
    return null;
  }

  const data = payload as Record<string, unknown>;
  const result: StorePriceEditable = {
    uuid: String(data.uuid ?? data.Uuid ?? ""),
    storeCode: (data.storeCode ?? data.StoreCode ?? null) as string | null,
    storeName: (data.storeName ?? data.StoreName ?? null) as string | null,
    productCode: (data.productCode ?? data.ProductCode ?? null) as string | null,
    storeProductCode: (data.storeProductCode ?? data.StoreProductCode ?? null) as string | null,
    supplierCode: (data.supplierCode ?? data.SupplierCode ?? null) as string | null,
    purchasePrice: toNumber(data.purchasePrice ?? data.PurchasePrice),
    retailPrice: toNumber(data.retailPrice ?? data.RetailPrice),
    discountRate: normalizeDiscountRate(data.discountRate ?? data.DiscountRate),
    isAutoPricing: Boolean(data.isAutoPricing ?? data.IsAutoPricing),
    isSpecialProduct: Boolean(data.isSpecialProduct ?? data.IsSpecialProduct),
    isActive: Boolean(data.isActive ?? data.IsActive),
    rate: toNumber(data.rate ?? data.Rate),
    strategySourceLabel: (data.strategySourceLabel ?? data.StrategySourceLabel ?? null) as string | null,
    strategyRuleLabel: (data.strategyRuleLabel ?? data.StrategyRuleLabel ?? null) as string | null,
  };
  const hqSync = normalizeHqSyncOperation(data.hqSync ?? data.HqSync);
  return hqSync ? { ...result, hqSync } : result;
}

function normalizeMultiCodeItem(payload: unknown): MultiCodeEditableItem {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const result: MultiCodeEditableItem = {
    uuid: String(data.uuid ?? data.Uuid ?? ""),
    setCodeId: String(data.setCodeId ?? data.SetCodeId ?? ""),
    storeCode: (data.storeCode ?? data.StoreCode ?? null) as string | null,
    productCode: (data.productCode ?? data.ProductCode ?? null) as string | null,
    multiCodeProductCode: (data.multiCodeProductCode ?? data.MultiCodeProductCode ?? null) as string | null,
    storeMultiCodeProductCode:
      (data.storeMultiCodeProductCode ?? data.StoreMultiCodeProductCode ?? null) as string | null,
    barcode: (data.barcode ?? data.Barcode ?? null) as string | null,
    purchasePrice: toNumber(data.purchasePrice ?? data.PurchasePrice),
    retailPrice: toNumber(data.retailPrice ?? data.RetailPrice),
    discountRate: normalizeDiscountRate(data.discountRate ?? data.DiscountRate),
    isAutoPricing: Boolean(data.isAutoPricing ?? data.IsAutoPricing),
    isSpecialProduct: Boolean(data.isSpecialProduct ?? data.IsSpecialProduct),
    isActive: Boolean(data.isActive ?? data.IsActive),
    rate: toNumber(data.rate ?? data.Rate),
    strategySourceLabel: (data.strategySourceLabel ?? data.StrategySourceLabel ?? null) as string | null,
    strategyRuleLabel: (data.strategyRuleLabel ?? data.StrategyRuleLabel ?? null) as string | null,
  };
  const hqSync = normalizeHqSyncOperation(data.hqSync ?? data.HqSync);
  return hqSync ? { ...result, hqSync } : result;
}

function normalizeSetCodeItem(payload: unknown): ProductSetCodeItem {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const result: ProductSetCodeItem = {
    setCodeId: String(data.setCodeId ?? data.SetCodeId ?? ""),
    productCode: String(data.productCode ?? data.ProductCode ?? ""),
    setProductCode: String(data.setProductCode ?? data.SetProductCode ?? ""),
    setItemNumber: String(data.setItemNumber ?? data.SetItemNumber ?? ""),
    setBarcode: (data.setBarcode ?? data.SetBarcode ?? null) as string | null,
    setPurchasePrice: toNumber(data.setPurchasePrice ?? data.SetPurchasePrice),
    setRetailPrice: toNumber(data.setRetailPrice ?? data.SetRetailPrice),
    setQuantity: Number(data.setQuantity ?? data.SetQuantity ?? 0),
    setType: Number(data.setType ?? data.SetType ?? 0),
    setTypeDescription: (data.setTypeDescription ?? data.SetTypeDescription ?? null) as string | null,
    isActive: Boolean(data.isActive ?? data.IsActive),
  };
  const hqSync = normalizeHqSyncOperation(data.hqSync ?? data.HqSync);
  return hqSync ? { ...result, hqSync } : result;
}

function normalizeClearancePrice(payload: unknown): StoreClearancePriceItem | null {
  if (!payload || typeof payload !== "object") {
    return null;
  }

  const data = payload as Record<string, unknown>;
  const result: StoreClearancePriceItem = {
    uuid: String(data.uuid ?? data.Uuid ?? ""),
    storeCode: (data.storeCode ?? data.StoreCode ?? null) as string | null,
    storeName: (data.storeName ?? data.StoreName ?? null) as string | null,
    productCode: (data.productCode ?? data.ProductCode ?? null) as string | null,
    clearanceBarcode: (data.clearanceBarcode ?? data.ClearanceBarcode ?? null) as string | null,
    clearancePrice: toNumber(data.clearancePrice ?? data.ClearancePrice),
  };
  const hqSync = normalizeHqSyncOperation(data.hqSync ?? data.HqSync);
  return hqSync ? { ...result, hqSync } : result;
}

function normalizeDetail(payload: unknown): ProductDetail {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const setCodesRaw = data.setCodes ?? data.SetCodes;
  const multiCodesRaw = data.multiCodes ?? data.MultiCodes;
  return {
    productCode: String(data.productCode ?? data.ProductCode ?? ""),
    productName: String(data.productName ?? data.ProductName ?? ""),
    itemNumber: (data.itemNumber ?? data.ItemNumber ?? null) as string | null,
    barcode: (data.barcode ?? data.Barcode ?? null) as string | null,
    productImage: (data.productImage ?? data.ProductImage ?? null) as string | null,
    productType: toNumber(data.productType ?? data.ProductType),
    productTypeLabel: (data.productTypeLabel ?? data.ProductTypeLabel ?? null) as string | null,
    grade: (data.grade ?? data.Grade ?? null) as string | null,
    localSupplierCode: (data.localSupplierCode ?? data.LocalSupplierCode ?? null) as string | null,
    localSupplierName: (data.localSupplierName ?? data.LocalSupplierName ?? null) as string | null,
    storePrice: normalizeStorePrice(data.storePrice ?? data.StorePrice),
    clearancePrice: normalizeClearancePrice(data.clearancePrice ?? data.ClearancePrice),
    setCodes: Array.isArray(setCodesRaw) ? setCodesRaw.map(normalizeSetCodeItem) : [],
    multiCodes: Array.isArray(multiCodesRaw) ? multiCodesRaw.map(normalizeMultiCodeItem) : [],
    setCodeCount: Number(data.setCodeCount ?? data.SetCodeCount ?? 0),
    multiCodeCount: Number(data.multiCodeCount ?? data.MultiCodeCount ?? 0),
    codesIncluded: Boolean(data.codesIncluded ?? data.CodesIncluded),
  };
}

function normalizeAutoPricingEvaluation(payload: unknown): EvaluateAutoPricingResult {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  return {
    productCode: String(data.productCode ?? data.ProductCode ?? ""),
    storeCode: (data.storeCode ?? data.StoreCode ?? null) as string | null,
    storePriceUuid: (data.storePriceUuid ?? data.StorePriceUuid ?? null) as string | null,
    currentRetailPrice: toNumber(data.currentRetailPrice ?? data.CurrentRetailPrice),
    recalculatedRetailPrice: toNumber(data.recalculatedRetailPrice ?? data.RecalculatedRetailPrice),
    currentRetailPriceFormatted: String(
      data.currentRetailPriceFormatted ?? data.CurrentRetailPriceFormatted ?? ""
    ),
    recalculatedRetailPriceFormatted: String(
      data.recalculatedRetailPriceFormatted ?? data.RecalculatedRetailPriceFormatted ?? ""
    ),
    discountRate: normalizeDiscountRate(data.discountRate ?? data.DiscountRate),
    isAutoPricing: Boolean(data.isAutoPricing ?? data.IsAutoPricing),
    hasValidPurchasePrice: Boolean(data.hasValidPurchasePrice ?? data.HasValidPurchasePrice),
    shouldUpdate: Boolean(data.shouldUpdate ?? data.ShouldUpdate),
  };
}

function normalizeProductTypeUpdate(payload: unknown): UpdateProductTypeResult {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const result: UpdateProductTypeResult = {
    productCode: String(data.productCode ?? data.ProductCode ?? ""),
    productType: Number(data.productType ?? data.ProductType ?? 0),
    productTypeLabel: (data.productTypeLabel ?? data.ProductTypeLabel ?? null) as string | null,
  };
  const hqSync = normalizeHqSyncOperation(data.hqSync ?? data.HqSync);
  return hqSync ? { ...result, hqSync } : result;
}

function normalizeProductNameUpdate(payload: unknown): UpdateProductNameResult {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const result: UpdateProductNameResult = {
    productCode: String(data.productCode ?? data.ProductCode ?? ""),
    productName: String(data.productName ?? data.ProductName ?? ""),
    changed: Boolean(data.changed ?? data.Changed),
  };
  const hqSync = normalizeHqSyncOperation(data.hqSync ?? data.HqSync);
  return hqSync ? { ...result, hqSync } : result;
}

function normalizeProductNameHistory(payload: unknown): ProductNameHistoryItem[] {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const items = data.items ?? data.Items;
  if (!Array.isArray(items)) {
    return [];
  }
  return items.map((item) => {
    const row = (item && typeof item === "object" ? item : {}) as Record<string, unknown>;
    const before = row.beforeName ?? row.BeforeName;
    const after = row.afterName ?? row.AfterName;
    return {
      occurredAtUtc: String(row.occurredAtUtc ?? row.OccurredAtUtc ?? ""),
      actorName: String(row.actorName ?? row.ActorName ?? ""),
      beforeName: before == null ? null : String(before),
      afterName: after == null ? null : String(after),
      source: String(row.source ?? row.Source ?? ""),
    };
  });
}

export async function lookupProducts(
  payload: StoreProductLookupRequest
): Promise<ProductLookupItem[]> {
  const response = await apiClient.post(BASE_PATH + "/lookup", payload, {
    ...buildRequestConfig(),
    timeout: PRODUCT_QUERY_REQUEST_TIMEOUT_MS,
  });
  return Array.isArray(response.data) ? response.data.map(normalizeLookupItem) : [];
}

function normalizeScanLabelPrintTarget(payload: unknown): ScanLabelPrintTarget | null {
  if (!payload || typeof payload !== "object") return null;
  const data = payload as Record<string, unknown>;
  const kind = String(data.kind ?? data.Kind ?? "").toLowerCase();
  const barcode = String(data.barcode ?? data.Barcode ?? "").trim();
  if (!(["product", "set", "multi", "clearance"] as string[]).includes(kind) || !barcode) {
    return null;
  }
  return {
    kind: kind as ScanLabelPrintTarget["kind"],
    barcode,
    retailPrice: toNumber(data.retailPrice ?? data.RetailPrice),
    discountRate: normalizeDiscountRate(data.discountRate ?? data.DiscountRate),
    codeId: (data.codeId ?? data.CodeId ?? null) as string | null,
    productCode: String(data.productCode ?? data.ProductCode ?? ""),
    storeCode: String(data.storeCode ?? data.StoreCode ?? ""),
  };
}

/** 在线扫码一次取得候选、当前门店价及精确打印目标；不写入或缓存商品价格。 */
export async function scanProductLabel(
  payload: StoreProductLookupRequest,
): Promise<ScanLabelResult> {
  const response = await apiClient.post(`${BASE_PATH}/scan-label`, payload, {
    ...buildRequestConfig(),
    timeout: PRODUCT_QUERY_REQUEST_TIMEOUT_MS,
  });
  const data = (response.data && typeof response.data === "object"
    ? response.data : {}) as Record<string, unknown>;
  const candidates = data.candidates ?? data.Candidates ?? data.items ?? data.Items;
  if (!Array.isArray(candidates)) {
    throw new Error("INVALID_SCAN_LABEL_RESPONSE");
  }
  const detail = data.detail ?? data.Detail;
  const printTarget = data.printTarget ?? data.PrintTarget;
  return {
    candidates: candidates.map(normalizeLookupItem),
    detail: detail && typeof detail === "object" ? normalizeDetail(detail) : null,
    printTarget: normalizeScanLabelPrintTarget(printTarget),
  };
}

/**
 * 离线目录同步接口的传输层：复用 apiClient 的设备头/鉴权与信封解包，
 * 不设固定超时（整页下载可能很慢），由 AbortSignal 负责取消。
 */
export function createOfflineCatalogTransport() {
  return {
    async get<T>(
      path: string,
      params: Record<string, string | number | undefined>,
      signal?: AbortSignal
    ): Promise<T> {
      const response = await apiClient.get(path, {
        ...buildRequestConfig(),
        params,
        signal,
        timeout: 0,
      });
      return response.data as T;
    },
  };
}

export async function fetchActiveLocalSuppliers(): Promise<LocalSupplierOption[]> {
  const response = await apiClient.get(ACTIVE_LOCAL_SUPPLIERS_PATH, buildRequestConfig());
  return normalizeActiveLocalSuppliersResponse(response.data);
}

export async function createProductWithPrices(
  payload: CreateProductWithPricesRequest
): Promise<CreateProductWithPricesResult> {
  const response = await apiClient.post(
    `${PRODUCTS_PATH}/create-with-prices`,
    buildCreateProductWithPricesPayload(payload),
    buildRequestConfig()
  );
  return normalizeCreateProductWithPricesResult(response.data);
}

export async function generateLocalProductBarcode(supplierCode: string): Promise<string> {
  const response = await apiClient.post(
    `${PRODUCTS_PATH}/generate-local-barcode`,
    null,
    { ...buildRequestConfig(), params: { supplierCode: supplierCode.trim() } }
  );
  const barcode = response.data?.barcode;
  if (typeof barcode !== "string" || !/^9529\d{9}$/.test(barcode)) {
    throw new Error("Invalid local product barcode response");
  }
  return barcode;
}

export async function getProductDetail(
  productCode: string,
  storeCode?: string | null,
  options?: { includeCodes?: boolean }
): Promise<ProductDetail> {
  const response = await apiClient.get(
    `${BASE_PATH}/${encodeURIComponent(productCode)}`,
    {
      ...buildRequestConfig(),
      params: {
        ...(storeCode ? { storeCode } : {}),
        ...(options?.includeCodes == null ? {} : { includeCodes: options.includeCodes }),
      },
    }
  );
  return normalizeDetail(response.data);
}

export async function getProductFastDetail(
  productCode: string,
  storeCode?: string | null
): Promise<ProductDetail> {
  const response = await apiClient.get(
    `${BASE_PATH}/${encodeURIComponent(productCode)}/fast-detail`,
    {
      ...buildRequestConfig(),
      timeout: PRODUCT_QUERY_REQUEST_TIMEOUT_MS,
      params: {
        ...(storeCode ? { storeCode } : {}),
      },
    }
  );
  return normalizeDetail(response.data);
}

export async function ensureStorePrice(
  productCode: string,
  storeCode: string,
): Promise<StorePriceEditable> {
  const response = await apiClient.post(
    `${BASE_PATH}/${encodeURIComponent(productCode)}/ensure-store-price`,
    null,
    {
      ...buildRequestConfig(),
      params: { storeCode },
    },
  );
  const storePrice = normalizeStorePrice(response.data);
  if (
    !storePrice?.uuid.trim() ||
    storePrice.productCode !== productCode ||
    storePrice.storeCode !== storeCode
  ) {
    throw new Error("INVALID_STORE_PRICE_RESPONSE");
  }
  return storePrice;
}

function normalizeCodePage<T>(
  payload: unknown,
  normalizeItem: (item: unknown) => T
): ProductCodePage<T> {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const rawItems = data.items ?? data.Items;
  const pageSize = Number(data.pageSize ?? data.PageSize ?? 50);
  const page = Number(data.page ?? data.Page ?? 1);
  const totalCount = Number(data.totalCount ?? data.TotalCount ?? 0);
  return {
    items: Array.isArray(rawItems) ? rawItems.map(normalizeItem) : [],
    totalCount,
    page,
    pageSize,
    hasMore: Boolean(data.hasMore ?? data.HasMore ?? page * pageSize < totalCount),
  };
}

export async function getProductCodes(
  productCode: string,
  storeCode: string | null | undefined,
  type: 1,
  page: number,
  pageSize: number,
  keyword?: string | null
): Promise<ProductCodePage<ProductSetCodeItem>>;
export async function getProductCodes(
  productCode: string,
  storeCode: string | null | undefined,
  type: 2,
  page: number,
  pageSize: number,
  keyword?: string | null
): Promise<ProductCodePage<MultiCodeEditableItem>>;
export async function getProductCodes(
  productCode: string,
  storeCode: string | null | undefined,
  type: 1 | 2,
  page: number,
  pageSize: number,
  keyword?: string | null
): Promise<ProductCodePage<ProductSetCodeItem> | ProductCodePage<MultiCodeEditableItem>> {
  const response = await apiClient.get(
    `${BASE_PATH}/${encodeURIComponent(productCode)}/codes`,
    {
      ...buildRequestConfig(),
      timeout: PRODUCT_QUERY_REQUEST_TIMEOUT_MS,
      params: {
        ...(storeCode ? { storeCode } : {}),
        type,
        page,
        pageSize,
        ...(keyword?.trim() ? { keyword: keyword.trim() } : {}),
      },
    }
  );

  return type === 1
    ? normalizeCodePage(response.data, normalizeSetCodeItem)
    : normalizeCodePage(response.data, normalizeMultiCodeItem);
}

export async function updateStorePrice(
  uuid: string,
  payload: UpdateStorePriceRequest
): Promise<StorePriceEditable> {
  const response = await apiClient.put(
    `${BASE_PATH}/store-prices/${encodeURIComponent(uuid)}`,
    payload,
    buildRequestConfig()
  );
  return normalizeStorePrice(response.data)!;
}

export async function syncWarehousePrice(
  uuid: string,
  payload: WarehousePriceSyncRequest
): Promise<WarehousePriceSyncResult> {
  const response = await apiClient.post(
    `${BASE_PATH}/store-prices/${encodeURIComponent(uuid)}/sync-warehouse`,
    payload,
    buildRequestConfig()
  );
  return normalizeWarehousePriceSyncResponse(response.data);
}

export async function evaluateAutoPricing(
  payload: EvaluateAutoPricingRequest
): Promise<EvaluateAutoPricingResult> {
  const response = await apiClient.post(
    `${BASE_PATH}/evaluate-auto-pricing`,
    payload,
    buildRequestConfig()
  );
  return normalizeAutoPricingEvaluation(response.data);
}

export async function updateProductType(
  productCode: string,
  payload: UpdateProductTypeRequest
): Promise<UpdateProductTypeResult> {
  const response = await apiClient.put(
    `${BASE_PATH}/products/${encodeURIComponent(productCode)}/type`,
    payload,
    buildRequestConfig()
  );
  return normalizeProductTypeUpdate(response.data);
}

/**
 * 修改商品主档中文名称（全局生效，需要 StoreProducts.EditName；后端不对匿名设备放行）。
 * 响应里的 hqSync 与改价、改类型一致，页面据此展示 HQ 同步状态。
 */
export async function updateProductName(
  productCode: string,
  payload: UpdateProductNameRequest
): Promise<UpdateProductNameResult> {
  const response = await apiClient.put(
    `${BASE_PATH}/products/${encodeURIComponent(productCode)}/name`,
    payload,
    buildRequestConfig()
  );
  return normalizeProductNameUpdate(response.data);
}

/** 商品名称的修改记录（新的在前），改名弹层里展示。 */
export async function getProductNameHistory(
  productCode: string,
  storeCode?: string | null,
  limit = 20
): Promise<ProductNameHistoryItem[]> {
  const response = await apiClient.get(
    `${BASE_PATH}/products/${encodeURIComponent(productCode)}/name-history`,
    {
      ...buildRequestConfig(),
      params: { ...(storeCode ? { storeCode } : {}), limit },
    }
  );
  return normalizeProductNameHistory(response.data);
}

export async function updateMultiCode(
  uuid: string,
  payload: UpdateMultiCodeRequest
): Promise<MultiCodeEditableItem> {
  const response = await apiClient.put(
    `${BASE_PATH}/multi-codes/${encodeURIComponent(uuid)}`,
    payload,
    buildRequestConfig()
  );
  return normalizeMultiCodeItem(response.data);
}

export async function createSetCode(payload: CreateSetCodeRequest): Promise<ProductSetCodeItem> {
  const response = await apiClient.post(
    `${BASE_PATH}/set-codes`,
    payload,
    buildRequestConfig()
  );
  return normalizeSetCodeItem(response.data);
}

export async function updateSetCode(
  setCodeId: string,
  payload: UpdateSetCodeRequest
): Promise<ProductSetCodeItem> {
  const response = await apiClient.put(
    `${BASE_PATH}/set-codes/${encodeURIComponent(setCodeId)}`,
    payload,
    buildRequestConfig()
  );
  return normalizeSetCodeItem(response.data);
}

export async function deleteSetCode(setCodeId: string): Promise<DeleteSetCodeResult> {
  const response = await apiClient.delete(
    `${BASE_PATH}/set-codes/${encodeURIComponent(setCodeId)}`,
    buildRequestConfig()
  );
  const data = response.data;
  if (!data || typeof data !== "object") {
    return { deleted: Boolean(data) };
  }

  const record = data as Record<string, unknown>;
  const hqSync = normalizeHqSyncOperation(record.hqSync ?? record.HqSync);
  return {
    deleted: Boolean(record.deleted ?? record.Deleted),
    ...(hqSync ? { hqSync } : {}),
  };
}

export async function upsertClearancePrice(
  productCode: string,
  payload: UpsertClearancePriceRequest
): Promise<StoreClearancePriceItem> {
  const response = await apiClient.put(
    `${BASE_PATH}/products/${encodeURIComponent(productCode)}/clearance-price`,
    payload,
    buildRequestConfig()
  );
  return normalizeClearancePrice(response.data)!;
}

export async function getProductHqSyncOperation(
  operationId: string
): Promise<ProductHqSyncOperation> {
  const response = await apiClient.get(
    `${BASE_PATH}/hq-sync/${encodeURIComponent(operationId)}`,
    buildRequestConfig()
  );
  const operation = normalizeHqSyncOperation(response.data);
  if (!operation) {
    throw new Error("INVALID_HQ_SYNC_OPERATION");
  }
  return operation;
}

export async function retryProductHqSyncOperation(
  operationId: string
): Promise<ProductHqSyncOperation> {
  const response = await apiClient.post(
    `${BASE_PATH}/hq-sync/${encodeURIComponent(operationId)}/retry`,
    {},
    buildRequestConfig()
  );
  const operation = normalizeHqSyncOperation(response.data);
  if (!operation) {
    throw new Error("INVALID_HQ_SYNC_OPERATION");
  }
  return operation;
}
