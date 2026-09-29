import type { StoreOrderProductQuery } from "./types";

/** 首页/分类浏览的客户端新鲜期，与后端订货商品首页、分类缓存（PR #379 起为 2 分钟）对齐。 */
export const SHOP_BROWSE_PRODUCTS_STALE_TIME_MS = 2 * 60 * 1000;

export function hasShopProductsKeyword(query: StoreOrderProductQuery) {
  return Boolean(query.itemNumber?.trim() || query.productName?.trim());
}

/**
 * 关键字检索（货号/条码/商品名）不设客户端新鲜期：上架前搜过同词得到的空结果若仍在新鲜期内，
 * 上架后再搜会被直接复用，门店会误以为“上架了还搜不到”（2026-09-29 ME542-6）。
 * 后端自 PR #379 起关键字检索也不缓存，两端口径一致；浏览类查询保留与后端相同的 2 分钟。
 */
export function resolveShopProductsStaleTime(query: StoreOrderProductQuery) {
  return hasShopProductsKeyword(query) ? 0 : SHOP_BROWSE_PRODUCTS_STALE_TIME_MS;
}

export function buildShopProductsQueryKey(
  query: StoreOrderProductQuery,
  locationLookupEnabled: boolean,
) {
  return ["shopProducts", query, locationLookupEnabled] as const;
}

interface PreviousShopProductsQuery {
  queryKey: readonly unknown[];
}

export function resolveShopProductsPlaceholderData<T>(
  previousData: T | undefined,
  previousQuery: PreviousShopProductsQuery | undefined,
  locationLookupEnabled: boolean,
  currentQuery: StoreOrderProductQuery,
): T | undefined {
  // 权限范围变化时禁止沿用旧数据，避免短暂显示无权查看的货位结果。
  if (previousQuery?.queryKey[2] !== locationLookupEnabled) {
    return undefined;
  }

  const previousProductQuery = previousQuery.queryKey[1];
  const previousItemNumber =
    typeof previousProductQuery === "object" && previousProductQuery !== null
      ? (previousProductQuery as StoreOrderProductQuery).itemNumber?.trim()
      : undefined;
  if (previousItemNumber && !currentQuery.itemNumber?.trim()) {
    // 清空关键词时不得把旧搜索结果伪装成原商品页；无缓存时交给现有加载态承接。
    return undefined;
  }

  return previousData;
}
