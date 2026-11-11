// 分店级「特殊商品分类」的前端类型。字段与后端契约（api/react/v1/store-special-product-categories）一一对应，全部 camelCase。

/** 分类（itemCount 只数仍是特殊商品的）。 */
export interface SpecialCategory {
  id: string;
  name: string;
  /** #RRGGBB；没有颜色时为 null。 */
  color: string | null;
  sortOrder: number;
  itemCount: number;
}

/** 分类列表页的总览：统计 + 全部分类（已按 sortOrder 排好）。 */
export interface CategoryOverview {
  storeCode: string;
  /** 本店当前特殊商品总数。 */
  totalCount: number;
  uncategorizedCount: number;
  categories: SpecialCategory[];
}

/** 分类里的商品（分类内按服务器顺序，「未分类」按名称）。 */
export interface CategoryProduct {
  productCode: string;
  displayName: string;
  itemNumber: string;
  barcode: string;
  retailPrice: number | null;
  productImage: string | null;
  categoryId: string | null;
  categoryName: string | null;
  sortOrder: number | null;
}

export interface CategoryProductsPage {
  items: CategoryProduct[];
  page: number;
  pageSize: number;
  total: number;
}

/** 「添加商品」搜索结果：本店有有效零售价行的商品，可能还不是特殊商品。 */
export interface CandidateProduct {
  productCode: string;
  displayName: string;
  itemNumber: string;
  barcode: string;
  retailPrice: number | null;
  productImage: string | null;
  isSpecialProduct: boolean;
  categoryId: string | null;
  categoryName: string | null;
}

export interface SkippedProductBrief {
  productCode: string;
  reason: string;
}

export interface AssignResult {
  assignedCount: number;
  markedCount: number;
  skipped: SkippedProductBrief[];
}

/** 「未分类」在 products 接口里的 categoryId 字面量。 */
export const UNCATEGORIZED_ID = "uncategorized";

// ---------------- 同步到其他分店 ----------------

export type SyncScope = "categories" | "categories-and-products";
export type SyncMode = "merge" | "replace";

export interface SyncSource {
  storeCode: string;
  categoryCount: number;
  productCount: number;
}

export interface SyncTarget {
  storeCode: string;
  storeName: string;
  categoryCount: number;
  specialProductCount: number;
}

export interface SyncTargetsResponse {
  source: SyncSource;
  targets: SyncTarget[];
}

export interface SyncSkippedProduct {
  productCode: string;
  displayName: string;
  /** 稳定英文码（如 NO_STORE_PRICE），中文由客户端映射。 */
  reason: string;
}

/** 预览与执行结果共用的各项计数。 */
export interface SyncCounts {
  createdCategories: number;
  mergedCategories: number;
  removedCategories: number;
  newlyMarkedProducts: number;
  reassignedProducts: number;
  unchangedProducts: number;
  unmarkedProducts: number;
  skipped: SyncSkippedProduct[];
}

export interface SyncPreviewTarget extends SyncCounts {
  storeCode: string;
  storeName: string;
  /** 预览阶段就能判定该店无法同步（如同步后分类数会超过 50）：执行时这家店会 ok=false 且不被改动。 */
  error: string | null;
}

export interface SyncResultTarget extends SyncPreviewTarget {
  ok: boolean;
}

export interface SyncRequest {
  sourceStoreCode: string;
  targetStoreCodes: string[];
  scope: SyncScope;
  mode: SyncMode;
}
