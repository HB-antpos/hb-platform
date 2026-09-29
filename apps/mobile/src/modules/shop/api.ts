import { getUserStoresApi } from "@/modules/auth/api";
import type {
  Store,
  StoreOrderCategoryNode,
  StoreOrderDynamicData,
  StoreOrderDynamicDataRequest,
  StoreOrderProductGradeOption,
  StoreOrderProductItem,
  StoreOrderProductQuery,
  StoreOrderProductListResult,
  StoreOrderProductPageResult,
  StoreOrderScanLookupResult,
  StoreOrderScanLookupAddResult,
  AddToCartPayload,
  UpdateCartQuantityPayload,
  StoreOrderCart,
  StoreOrderCartMutationResult,
} from "@/modules/shop/types";
import type { StoreOrderScanMatchType } from "@/modules/orders/types";
import { resolveCartSkuCount } from "@/modules/shop/cart-summary-density";
import { normalizeEmbeddedDynamicData } from "@/modules/shop/embedded-dynamic-data";
import { buildScanLookupPayload } from "@/modules/shop/scan-lookup-payload";
import { normalizeShopStores, normalizeShopStoresApiResponse } from "@/modules/shop/store-normalization";
import { apiClient } from "@/shared/api/client";

type ApiItem = Record<string, unknown>;

function buildScanTraceHeaders(scanTraceId?: string) {
  // 扫码链路的 trace 只在相关请求上透传，普通商品/购物车请求不带额外 header。
  return scanTraceId
    ? {
        headers: {
          "X-Scan-Trace-Id": scanTraceId,
        },
      }
    : undefined;
}

function getStringValue(...values: unknown[]) {
  for (const value of values) {
    if (typeof value === "string") {
      const trimmed = value.trim();
      if (trimmed) {
        return trimmed;
      }
      continue;
    }

    if (value != null) {
      const normalized = String(value).trim();
      if (normalized) {
        return normalized;
      }
    }
  }

  return undefined;
}

function getFiniteNumber(...values: unknown[]) {
  for (const value of values) {
    if (value == null || value === "") {
      continue;
    }

    const parsed = Number(value);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }

  return undefined;
}

function normalizeProductGradeOption(raw: unknown): StoreOrderProductGradeOption | null {
  if (typeof raw !== "object" || raw === null) {
    const gradeValue = getStringValue(raw);

    return gradeValue
      ? {
          grade: gradeValue,
          label: gradeValue,
          value: gradeValue,
        }
      : null;
  }

  const item = raw as ApiItem;
  const grade = getStringValue(item.grade, item.Grade, item.label, item.Label, item.value, item.Value);

  if (!grade) {
    return null;
  }

  return {
    grade,
    label: grade,
    value: grade,
  };
}

function transformProductItem(raw: ApiItem): StoreOrderProductItem {
  const stockQuantity =
    raw.stockQuantity != null
      ? Number(raw.stockQuantity)
      : raw.StockQuantity != null
        ? Number(raw.StockQuantity)
        : 0;

  return {
    productCode: String(raw.productCode ?? raw.ProductCode ?? ""),
    itemNumber: raw.itemNumber != null ? String(raw.itemNumber) : raw.ItemNumber != null ? String(raw.ItemNumber) : undefined,
    barcode: raw.barcode != null ? String(raw.barcode) : raw.Barcode != null ? String(raw.Barcode) : undefined,
    grade:
      raw.grade != null
        ? String(raw.grade)
        : raw.Grade != null
          ? String(raw.Grade)
          : raw.productGrade != null
            ? String(raw.productGrade)
            : raw.ProductGrade != null
              ? String(raw.ProductGrade)
              : undefined,
    productName: raw.productName != null ? String(raw.productName) : raw.ProductName != null ? String(raw.ProductName) : undefined,
    productImage: raw.productImage != null ? String(raw.productImage) : raw.ProductImage != null ? String(raw.ProductImage) : undefined,
    categoryName: raw.categoryName != null ? String(raw.categoryName) : raw.CategoryName != null ? String(raw.CategoryName) : undefined,
    warehouseCategoryGUID:
      raw.warehouseCategoryGUID != null
        ? String(raw.warehouseCategoryGUID)
        : raw.WarehouseCategoryGUID != null
          ? String(raw.WarehouseCategoryGUID)
          : undefined,
    oemPrice: raw.oemPrice != null ? Number(raw.oemPrice) : raw.OEMPrice != null ? Number(raw.OEMPrice) : undefined,
    minOrderQuantity:
      raw.minOrderQuantity != null
        ? Number(raw.minOrderQuantity)
        : raw.MinOrderQuantity != null
          ? Number(raw.MinOrderQuantity)
          : 1,
    stockQuantity,
    isInStock:
      raw.isInStock != null
        ? Boolean(raw.isInStock)
        : raw.IsInStock != null
          ? Boolean(raw.IsInStock)
          : stockQuantity > 0,
    packQty: raw.packQty != null ? Number(raw.packQty) : raw.PackQty != null ? Number(raw.PackQty) : undefined,
    importPrice:
      raw.importPrice != null ? Number(raw.importPrice) : raw.ImportPrice != null ? Number(raw.ImportPrice) : undefined,
  };
}

function normalizeProductPagedList(payload: Partial<StoreOrderProductListResult> | null | undefined): StoreOrderProductListResult {
  const normalizedPayload = payload as
    | ((Partial<StoreOrderProductListResult> & {
        pageNumber?: number;
        PageNumber?: number;
        Total?: number;
        PageSize?: number;
      }) & {
        items?: ApiItem[];
        Items?: ApiItem[];
      })
    | null
    | undefined;

  const items = normalizedPayload?.items ?? normalizedPayload?.Items;

  return {
    items: Array.isArray(items) ? items.map(transformProductItem) : [],
    total: normalizedPayload?.total ?? normalizedPayload?.Total ?? 0,
    page: normalizedPayload?.page ?? normalizedPayload?.pageNumber ?? normalizedPayload?.PageNumber ?? 1,
    pageSize: normalizedPayload?.pageSize ?? normalizedPayload?.PageSize ?? 24,
  };
}

function transformCartItem(raw: ApiItem) {
  return {
    detailGUID: String(raw.detailGUID ?? raw.DetailGUID ?? ""),
    productCode: String(raw.productCode ?? raw.ProductCode ?? ""),
    itemNumber: raw.itemNumber != null ? String(raw.itemNumber) : raw.ItemNumber != null ? String(raw.ItemNumber) : undefined,
    barcode: raw.barcode != null ? String(raw.barcode) : raw.Barcode != null ? String(raw.Barcode) : undefined,
    grade:
      raw.grade != null
        ? String(raw.grade)
        : raw.Grade != null
          ? String(raw.Grade)
          : raw.productGrade != null
            ? String(raw.productGrade)
            : raw.ProductGrade != null
              ? String(raw.ProductGrade)
              : undefined,
    productName: raw.productName != null ? String(raw.productName) : raw.ProductName != null ? String(raw.ProductName) : undefined,
    productImage: raw.productImage != null ? String(raw.productImage) : raw.ProductImage != null ? String(raw.ProductImage) : undefined,
    price: Number(raw.price ?? raw.Price ?? 0),
    quantity: Number(raw.quantity ?? raw.Quantity ?? 0),
    allocQuantity:
      raw.allocQuantity != null ? Number(raw.allocQuantity) : raw.AllocQuantity != null ? Number(raw.AllocQuantity) : undefined,
    amount: Number(raw.amount ?? raw.Amount ?? 0),
    importPrice: Number(raw.importPrice ?? raw.ImportPrice ?? 0),
    importAmount: Number(raw.importAmount ?? raw.ImportAmount ?? 0),
    volume: raw.volume != null ? Number(raw.volume) : raw.Volume != null ? Number(raw.Volume) : undefined,
    totalVolume:
      raw.totalVolume != null ? Number(raw.totalVolume) : raw.TotalVolume != null ? Number(raw.TotalVolume) : undefined,
    minOrderQuantity:
      raw.minOrderQuantity != null
        ? Number(raw.minOrderQuantity)
        : raw.MinOrderQuantity != null
          ? Number(raw.MinOrderQuantity)
          : 1,
    isActive: raw.isActive != null ? Boolean(raw.isActive) : raw.IsActive != null ? Boolean(raw.IsActive) : true,
    locationCode:
      raw.locationCode != null ? String(raw.locationCode) : raw.LocationCode != null ? String(raw.LocationCode) : undefined,
    rrp: raw.rrp != null ? Number(raw.rrp) : raw.RRP != null ? Number(raw.RRP) : undefined,
    updatedAt: raw.updatedAt != null ? String(raw.updatedAt) : raw.UpdatedAt != null ? String(raw.UpdatedAt) : undefined,
  };
}

function normalizeCart(payload: Partial<StoreOrderCart> | null | undefined): StoreOrderCart | null {
  if (!payload) {
    return null;
  }

  const normalizedPayload = payload as
    | (Partial<StoreOrderCart> & {
        ImportTotal?: unknown;
        ImportTotalAmount?: unknown;
        Items?: ApiItem[];
        SKUCount?: unknown;
        SkuCount?: unknown;
        TotalAmount?: unknown;
        TotalImportAmount?: unknown;
        TotalQuantity?: unknown;
        TotalSKU?: unknown;
        TotalSku?: unknown;
        TotalVolume?: unknown;
        importTotal?: unknown;
        importTotalAmount?: unknown;
        items?: ApiItem[];
        skuCount?: unknown;
        totalSKU?: unknown;
      })
    | null
    | undefined;
  const items = normalizedPayload?.items ?? normalizedPayload?.Items;
  const cartItems = Array.isArray(items) ? items.map(transformCartItem) : [];
  const fallbackImportAmount = cartItems.reduce(
    (sum, item) => sum + (Number.isFinite(item.importAmount) ? item.importAmount : item.importPrice * item.quantity),
    0
  );
  const fallbackTotalQuantity = cartItems.reduce((sum, item) => sum + item.quantity, 0);
  const reportedTotalSku = getFiniteNumber(
    normalizedPayload?.totalSku,
    normalizedPayload?.totalSKU,
    normalizedPayload?.TotalSku,
    normalizedPayload?.TotalSKU,
    normalizedPayload?.skuCount,
    normalizedPayload?.SkuCount,
    normalizedPayload?.SKUCount
  );

  return {
    orderGUID: payload.orderGUID ?? "",
    orderNo: payload.orderNo,
    storeCode: payload.storeCode,
    storeName: payload.storeName,
    totalAmount: getFiniteNumber(normalizedPayload?.totalAmount, normalizedPayload?.TotalAmount) ?? 0,
    totalQuantity:
      getFiniteNumber(normalizedPayload?.totalQuantity, normalizedPayload?.TotalQuantity) ?? fallbackTotalQuantity,
    totalImportAmount:
      getFiniteNumber(
        normalizedPayload?.totalImportAmount,
        normalizedPayload?.TotalImportAmount,
        normalizedPayload?.importTotalAmount,
        normalizedPayload?.ImportTotalAmount,
        normalizedPayload?.importTotal,
        normalizedPayload?.ImportTotal
      ) ?? fallbackImportAmount,
    totalSku: resolveCartSkuCount({
      productCodes: cartItems.map((item) => item.productCode),
      reportedSkuCount: reportedTotalSku,
    }),
    totalVolume: getFiniteNumber(normalizedPayload?.totalVolume, normalizedPayload?.TotalVolume) ?? 0,
    remarks: payload.remarks,
    shippingFee: payload.shippingFee,
    orderDate: payload.orderDate,
    storeAddress: payload.storeAddress,
    flowStatus: payload.flowStatus,
    items: cartItems,
  };
}

function normalizeCartMutationResult(payload: ApiItem | null | undefined): StoreOrderCartMutationResult | null {
  if (!payload) {
    return null;
  }

  const summary = (payload.summary ?? payload.Summary ?? {}) as ApiItem;
  const changedItemPayload = (payload.changedItem ?? payload.ChangedItem) as ApiItem | null | undefined;
  const changedItem = changedItemPayload ? transformCartItem(changedItemPayload) : null;
  const productCode =
    getStringValue(payload.productCode, payload.ProductCode, changedItem?.productCode) ?? "";

  return {
    productCode,
    removed: Boolean(payload.removed ?? payload.Removed ?? false),
    changedItem,
    summary: {
      orderGUID: getStringValue(summary.orderGUID, summary.OrderGUID) ?? "",
      storeCode: getStringValue(summary.storeCode, summary.StoreCode),
      cartRevision: getFiniteNumber(summary.cartRevision, summary.CartRevision),
      totalAmount: getFiniteNumber(summary.totalAmount, summary.TotalAmount) ?? 0,
      totalImportAmount:
        getFiniteNumber(summary.totalImportAmount, summary.TotalImportAmount) ?? 0,
      totalQuantity: getFiniteNumber(summary.totalQuantity, summary.TotalQuantity) ?? 0,
      totalSku:
        getFiniteNumber(summary.totalSku, summary.totalSKU, summary.TotalSku, summary.TotalSKU) ?? 0,
    },
  };
}

function normalizeScanLookupItems(data: ApiItem | null | undefined) {
  const items = data?.items ?? data?.Items;
  return Array.isArray(items)
    ? items.map((item) => transformProductItem(item as unknown as ApiItem))
    : [];
}

export function normalizeStoreOrderScanLookupResult(
  data: unknown,
  fallbackBarcode: string,
): StoreOrderScanLookupResult {
  const payload = (data && typeof data === "object" ? data : {}) as ApiItem;

  return {
    barcode: getStringValue(payload.barcode, payload.Barcode) ?? fallbackBarcode,
    // 兼容旧版 barcode/fallback 与新版商品/货位细分 matchType，原值不改写。
    matchType: getStringValue(payload.matchType, payload.MatchType) as StoreOrderScanMatchType | undefined,
    items: normalizeScanLookupItems(payload),
  };
}

export async function getStoresByUserGuid(userGuid: string): Promise<Store[]> {
  const stores = await getUserStoresApi(userGuid);

  return normalizeShopStores(stores);
}

export async function getAllStores(): Promise<Store[]> {
  const response = await apiClient.get("/stores/all-by-name");
  return normalizeShopStoresApiResponse(response.data);
}

export async function getCategoryTree(): Promise<StoreOrderCategoryNode[]> {
  const response = await apiClient.get("/react/v1/warehouse-categories/tree");
  return Array.isArray(response.data) ? (response.data as StoreOrderCategoryNode[]) : [];
}

export async function getProductGradeOptions(): Promise<StoreOrderProductGradeOption[]> {
  const response = await apiClient.get("/react/v1/product-grades/options", {
    params: {
      page: 1,
      pageSize: 1000,
      sortField: "grade",
      sortDirection: "asc",
    },
  });
  const payload = response.data as
    | { items?: unknown[]; Items?: unknown[] }
    | unknown[]
    | null
    | undefined;
  const items = Array.isArray(payload)
    ? payload
    : Array.isArray(payload?.items)
      ? payload.items
      : Array.isArray(payload?.Items)
        ? payload.Items
        : [];
  const seen = new Set<string>();

  return items.reduce<StoreOrderProductGradeOption[]>((accumulator, item) => {
    const option = normalizeProductGradeOption(item);

    if (!option) {
      return accumulator;
    }

    const key = option.grade.toUpperCase();
    if (seen.has(key)) {
      return accumulator;
    }

    seen.add(key);
    accumulator.push(option);
    return accumulator;
  }, []);
}

export async function getProducts(query: StoreOrderProductQuery): Promise<StoreOrderProductPageResult> {
  const grade = getStringValue(query.grade);
  const response = await apiClient.post(
    "/react/v1/store-order/products",
    {
      ...query,
      grade,
      // 有门店时请后端顺带返回本页动态数据（购物车数量等），省掉一次串行往返。
      includeDynamicData: Boolean(query.storeCode),
    },
    {
      params: {
        grade,
      },
    }
  );
  return {
    ...normalizeProductPagedList(response.data as Partial<StoreOrderProductListResult>),
    dynamicData: normalizeEmbeddedDynamicData(response.data),
  };
}

export async function getProductDynamicData(
  payload: StoreOrderDynamicDataRequest
): Promise<StoreOrderDynamicData[]> {
  const response = await apiClient.post("/react/v1/store-order/dynamic-data", {
    ...payload,
    // 移动端不展示来货后销量；未显式指定时关掉，减少后端 2–3 条统计查询。
    includeSales: payload.includeSales ?? false,
  });
  return Array.isArray(response.data) ? (response.data as StoreOrderDynamicData[]) : [];
}

export async function getCart(storeCode: string): Promise<StoreOrderCart | null> {
  const response = await apiClient.get(`/react/v1/store-order/cart/${encodeURIComponent(storeCode)}`);
  return normalizeCart(response.data as Partial<StoreOrderCart> | null);
}

export async function addToCart(
  payload: AddToCartPayload,
  scanTraceId?: string
): Promise<StoreOrderCart | StoreOrderCartMutationResult | null> {
  const path = scanTraceId ? "/react/v1/store-order/cart/scan-add" : "/react/v1/store-order/cart/add";
  const response = await apiClient.post(
    path,
    payload,
    buildScanTraceHeaders(scanTraceId)
  );
  if (scanTraceId) {
    return normalizeCartMutationResult(response.data as ApiItem | null);
  }

  return normalizeCart(response.data as Partial<StoreOrderCart> | null);
}

export async function updateCartQuantity(
  payload: UpdateCartQuantityPayload,
  scanTraceId?: string
): Promise<StoreOrderCart | StoreOrderCartMutationResult | null> {
  const path = scanTraceId ? "/react/v1/store-order/cart/scan-update" : "/react/v1/store-order/cart/update";
  const response = await apiClient.post(
    path,
    payload,
    buildScanTraceHeaders(scanTraceId)
  );
  if (scanTraceId) {
    return normalizeCartMutationResult(response.data as ApiItem | null);
  }

  return normalizeCart(response.data as Partial<StoreOrderCart> | null);
}

export async function lookupProductsByBarcode(
  barcode: string,
  storeCode?: string | null,
  scanTraceId?: string
): Promise<StoreOrderScanLookupResult> {
  const response = await apiClient.post(
    "/react/v1/store-order/products/scan-lookup",
    buildScanLookupPayload(barcode, storeCode),
    buildScanTraceHeaders(scanTraceId)
  );

  return normalizeStoreOrderScanLookupResult(response.data, barcode);
}

export async function scanLookupAndAddToCart(
  barcode: string,
  storeCode: string,
  scanTraceId?: string
): Promise<StoreOrderScanLookupAddResult> {
  const response = await apiClient.post(
    "/react/v1/store-order/cart/scan-lookup-add",
    buildScanLookupPayload(barcode, storeCode),
    buildScanTraceHeaders(scanTraceId)
  );
  const data = response.data as ApiItem | null | undefined;

  return {
    barcode: getStringValue(data?.barcode, data?.Barcode) ?? barcode,
    matchType: getStringValue(data?.matchType, data?.MatchType) as StoreOrderScanMatchType | undefined,
    items: normalizeScanLookupItems(data),
    added: Boolean(data?.added ?? data?.Added ?? false),
    cart: normalizeCartMutationResult((data?.cart ?? data?.Cart) as ApiItem | null | undefined),
  };
}
