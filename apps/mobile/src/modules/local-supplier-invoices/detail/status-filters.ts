import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";
import {
  ACTION_TYPE_FILTERS,
  getActionTypeFilter,
  getBarcodeStatusFilter,
  getProductStatusFilter,
  isBarcodeStatusAbnormal,
  type ActionTypeFilter,
  type BarcodeStatusFilter,
  type ProductStatusFilter,
  type RowActions,
} from "./detail-status";
import { getDetailProgressBucket, type DetailProgressBucketFilter } from "./progress-buckets";
import { getPriceChangeStats, isPurchasePriceDown, isPurchasePriceUp } from "./price-change";

// 分类函数在 detail-status 定义（避免与 progress-buckets 循环引用），这里原样再导出，调用方从任一处导入均可。
export {
  ACTION_TYPE_FILTERS,
  getActionTypeFilter,
  getBarcodeStatusFilter,
  getProductStatusFilter,
  isBarcodeStatusAbnormal,
  type ActionTypeFilter,
  type BarcodeStatusFilter,
  type ProductStatusFilter,
};

/**
 * 明细筛选模型（口径与 Web InvoiceEdit/statusFilters.ts、index.tsx filteredDetails 对齐）：
 * 搜索关键词、快捷筛选、「更多筛选」、处理进度分段之间按 AND 叠加；
 * 「更多筛选」每一组内允许多选，组内按 OR，空组 = 不限。
 */
export type ProductTypeFilter = 0 | 1 | 2 | "unknown";
export type SpecialProductFilter = "yes" | "no";
/** 快捷筛选：涨价 / 降价（同属「价格」组，同时选中按 OR）、主档不存在、条码异常（各自独立，按 AND）。 */
export type DetailQuickFilter = "priceUp" | "priceDown" | "productNotExists" | "barcodeAbnormal";

export interface DetailMoreFilters {
  productTypes: ProductTypeFilter[];
  productStatuses: ProductStatusFilter[];
  barcodeStatuses: BarcodeStatusFilter[];
  actionTypes: ActionTypeFilter[];
  special: SpecialProductFilter[];
}

export interface DetailFilters {
  searchText: string;
  quickFilters: DetailQuickFilter[];
  more: DetailMoreFilters;
  progressBucket: DetailProgressBucketFilter;
  /** 行内改过但未回读的操作类型覆盖，影响「操作类型」组与处理进度分段。 */
  rowActions?: RowActions;
}

export const PRODUCT_STATUS_FILTERS: readonly ProductStatusFilter[] = [
  "notDetected",
  "exists",
  "notExists",
  "createdHere",
];
export const BARCODE_STATUS_FILTERS: readonly BarcodeStatusFilter[] = [
  "notDetected",
  "normal",
  "noMatch",
  "multiMatch",
];
export const PRODUCT_TYPE_FILTERS: readonly ProductTypeFilter[] = [0, 1, 2, "unknown"];
export const SPECIAL_PRODUCT_FILTERS: readonly SpecialProductFilter[] = ["yes", "no"];

export function createEmptyDetailMoreFilters(): DetailMoreFilters {
  return { productTypes: [], productStatuses: [], barcodeStatuses: [], actionTypes: [], special: [] };
}

export function createEmptyDetailFilters(): DetailFilters {
  return {
    searchText: "",
    quickFilters: [],
    more: createEmptyDetailMoreFilters(),
    progressBucket: "all",
  };
}

/** 「更多筛选」里有多少组启用了条件（用于角标）。 */
export function countActiveMoreFilterGroups(more: DetailMoreFilters): number {
  return [
    more.productTypes,
    more.productStatuses,
    more.barcodeStatuses,
    more.actionTypes,
    more.special,
  ].filter((group) => group.length > 0).length;
}

/** 是否存在任何生效中的筛选条件。 */
export function hasActiveDetailFilters(filters: DetailFilters): boolean {
  return (
    filters.searchText.trim() !== "" ||
    filters.quickFilters.length > 0 ||
    countActiveMoreFilterGroups(filters.more) > 0 ||
    filters.progressBucket !== "all"
  );
}

/** 多选组的开关：有则移除、无则追加，返回新数组。 */
export function toggleFilterValue<T>(values: readonly T[], value: T): T[] {
  return values.includes(value) ? values.filter((item) => item !== value) : [...values, value];
}

function isKnownProductType(productType: number | null | undefined): boolean {
  return productType === 0 || productType === 1 || productType === 2;
}

/** 商品类型分类：0 单品 / 1 套装 / 2 多码，其余（含未匹配主档）归 unknown。 */
export function getProductTypeFilter(detail: Pick<LocalSupplierInvoiceItem, "productType">): ProductTypeFilter {
  return isKnownProductType(detail.productType) ? (detail.productType as 0 | 1 | 2) : "unknown";
}

export function getSpecialProductFilter(
  detail: Pick<LocalSupplierInvoiceItem, "isSpecialProduct">
): SpecialProductFilter {
  return detail.isSpecialProduct ? "yes" : "no";
}

type SearchableDetail = Pick<
  LocalSupplierInvoiceItem,
  "productCode" | "itemNumber" | "barcode" | "productName" | "storeProductCode"
>;

/** 关键词搜索：productCode / itemNumber / barcode / productName / storeProductCode 任一包含（忽略大小写、首尾空格）。 */
export function matchesDetailKeyword(detail: SearchableDetail, searchText: string): boolean {
  const keyword = searchText.trim().toLowerCase();
  if (!keyword) return true;
  return [
    detail.productCode,
    detail.itemNumber,
    detail.barcode,
    detail.productName,
    detail.storeProductCode,
  ].some((value) => typeof value === "string" && value.toLowerCase().includes(keyword));
}

type FilterableDetail = SearchableDetail &
  Pick<
    LocalSupplierInvoiceItem,
    | "detailGuid"
    | "isCreatedByThisInvoice"
    | "existingProductCount"
    | "barcodeStatus"
    | "barcodeMatchCount"
    | "activityType"
    | "productType"
    | "isSpecialProduct"
    | "lastPurchasePrice"
    | "purchasePrice"
  >;

function matchesQuickFilters(detail: FilterableDetail, quickFilters: readonly DetailQuickFilter[]): boolean {
  const wantsUp = quickFilters.includes("priceUp");
  const wantsDown = quickFilters.includes("priceDown");
  // 涨价 / 降价同属价格组：都选 = 涨或降，都不选 = 不限。
  if ((wantsUp || wantsDown) && !((wantsUp && isPurchasePriceUp(detail)) || (wantsDown && isPurchasePriceDown(detail)))) {
    return false;
  }
  if (quickFilters.includes("productNotExists") && getProductStatusFilter(detail) !== "notExists") return false;
  if (quickFilters.includes("barcodeAbnormal") && !isBarcodeStatusAbnormal(detail)) return false;
  return true;
}

function matchesMoreFilters(detail: FilterableDetail, more: DetailMoreFilters, rowActions: RowActions): boolean {
  // 每组：空 = 不限；非空 = 命中任一即可（组内 OR），组与组之间 AND。
  if (more.productTypes.length && !more.productTypes.includes(getProductTypeFilter(detail))) return false;
  if (more.productStatuses.length && !more.productStatuses.includes(getProductStatusFilter(detail))) return false;
  if (more.barcodeStatuses.length && !more.barcodeStatuses.includes(getBarcodeStatusFilter(detail))) return false;
  if (more.actionTypes.length && !more.actionTypes.includes(getActionTypeFilter(detail, rowActions))) return false;
  if (more.special.length && !more.special.includes(getSpecialProductFilter(detail))) return false;
  return true;
}

/** 统一筛选入口：处理进度 AND 搜索 AND 快捷筛选 AND 更多筛选。 */
export function applyDetailFilters<T extends FilterableDetail>(details: readonly T[], filters: DetailFilters): T[] {
  const rowActions = filters.rowActions ?? {};
  return details.filter(
    (detail) =>
      (filters.progressBucket === "all" || getDetailProgressBucket(detail, rowActions) === filters.progressBucket) &&
      matchesDetailKeyword(detail, filters.searchText) &&
      matchesQuickFilters(detail, filters.quickFilters) &&
      matchesMoreFilters(detail, filters.more, rowActions)
  );
}

/** 各筛选项的行数（始终基于传入的全部明细，不受当前筛选影响，与 Web detailStatusStats / priceStats 同口径）。 */
export interface DetailFilterCounts {
  total: number;
  product: Record<ProductStatusFilter, number>;
  barcode: Record<BarcodeStatusFilter, number>;
  /** 条码异常 = 无匹配 + 多匹配。 */
  barcodeAbnormal: number;
  action: Record<ActionTypeFilter, number>;
  productType: Record<ProductTypeFilter, number>;
  special: Record<SpecialProductFilter, number>;
  price: { up: number; down: number };
}

export function getDetailFilterCounts(
  details: readonly FilterableDetail[],
  rowActions: RowActions = {}
): DetailFilterCounts {
  const counts: DetailFilterCounts = {
    total: details.length,
    product: { notDetected: 0, exists: 0, notExists: 0, createdHere: 0 },
    barcode: { notDetected: 0, normal: 0, noMatch: 0, multiMatch: 0 },
    barcodeAbnormal: 0,
    action: { 0: 0, 1: 0, 2: 0, 3: 0, 4: 0, 5: 0 },
    productType: { 0: 0, 1: 0, 2: 0, unknown: 0 },
    special: { yes: 0, no: 0 },
    price: { up: 0, down: 0 },
  };
  const { upCount, downCount } = getPriceChangeStats(details);
  counts.price = { up: upCount, down: downCount };

  for (const detail of details) {
    counts.product[getProductStatusFilter(detail)] += 1;
    counts.barcode[getBarcodeStatusFilter(detail)] += 1;
    if (isBarcodeStatusAbnormal(detail)) counts.barcodeAbnormal += 1;
    counts.action[getActionTypeFilter(detail, rowActions)] += 1;
    counts.productType[getProductTypeFilter(detail)] += 1;
    counts.special[getSpecialProductFilter(detail)] += 1;
  }
  return counts;
}
