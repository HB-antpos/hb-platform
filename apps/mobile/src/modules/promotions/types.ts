export type PromotionScopeType = "StoreOnly" | "MultiStore" | "Headquarters";

export interface PromotionProductItem {
  id?: string;
  productCode: string;
  unitWeight: number;
}

export interface PromotionStoreItem {
  id?: string;
  storeCode: string;
}

export interface PromotionListItem {
  id: string;
  name: string;
  description?: string;
  effectiveStart: string;
  effectiveEnd: string;
  isEnabled: boolean;
  isExclusive: boolean;
  priority: number;
  applyQuantity: number;
  fixedPrice: number;
  maxApplicationsPerOrder?: number;
  productsCount: number;
  storesCount: number;
  products: PromotionProductItem[];
  stores: PromotionStoreItem[];
  scopeType: PromotionScopeType | null;
  canEditInStoreScope: boolean;
  canCopyToStore: boolean;
}

export type PromotionDetail = PromotionListItem;

/** 店级列表状态分段，取值与后端 PromotionStoreStatusFilters 一致。 */
export type PromotionStatusFilter = "active" | "upcoming" | "disabled" | "ended" | "all";

/** 单条促销在某一时刻的状态（四种互斥，与后端分段判定一致）。 */
export type PromotionLifecycleStatus = "active" | "upcoming" | "disabled" | "ended";

export interface PromotionStatusCounts {
  active: number;
  upcoming: number;
  disabled: number;
  ended: number;
  total: number;
}

export interface PromotionGridQuery {
  storeCode: string;
  keyword?: string;
  status?: PromotionStatusFilter;
  /** 仅「全部」分段有效：隐藏已结束（请求时翻译成后端的 notended 过滤）。 */
  hideEnded?: boolean;
  page?: number;
  pageSize?: number;
  sortModel?: Array<{ colId: string; sort: "asc" | "desc" }>;
}

export interface PromotionFormValues {
  id?: string;
  name: string;
  description?: string;
  storeCode: string;
  effectiveStart?: string;
  effectiveEnd?: string;
  isEnabled?: boolean;
  isExclusive?: boolean;
  priority?: number;
  applyQuantity?: number;
  fixedPrice?: number;
  maxApplicationsPerOrder?: number | null;
  products?: Array<{
    id?: string;
    productCode?: string;
    unitWeight?: number | string;
  }>;
}

export interface PromotionCopyRequest {
  sourcePromotionId: string;
  storeCode: string;
  name?: string;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  pageNumber: number;
  pageSize: number;
}
