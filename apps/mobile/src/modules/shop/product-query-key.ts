import type { StoreOrderProductItem, StoreOrderProductQuery } from "./types";

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

/** 本页商品编码，顺序与列表一致；动态数据缓存键依赖它。 */
export function resolvePageProductCodes(items: StoreOrderProductItem[] | undefined) {
  return (items ?? []).map((item) => item.productCode).filter(Boolean);
}

export function buildShopDynamicDataQueryKey(storeCode: string | null | undefined, productCodes: string[]) {
  return ["shopDynamicData", storeCode ?? null, productCodes] as const;
}
