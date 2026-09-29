import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { getProductDynamicData, getProducts } from "@/modules/shop/api";
import {
  buildShopProductsQueryKey,
  resolveShopProductsPlaceholderData,
  resolveShopProductsStaleTime,
} from "@/modules/shop/product-query-key";
import type { ProductDynamicDataMap, StoreOrderProductQuery } from "@/modules/shop/types";

export { buildShopProductsQueryKey } from "@/modules/shop/product-query-key";

export function useProducts(query: StoreOrderProductQuery, locationLookupEnabled = false) {
  const productsQuery = useQuery({
    queryKey: buildShopProductsQueryKey(query, locationLookupEnabled),
    enabled: Boolean(query.storeCode),
    // 关键字检索每次都重新请求，浏览类查询缓存 2 分钟，规则见 resolveShopProductsStaleTime。
    staleTime: resolveShopProductsStaleTime(query),
    placeholderData: (previousData, previousQuery) =>
      resolveShopProductsPlaceholderData(
        previousData,
        previousQuery,
        locationLookupEnabled,
        query,
      ),
    retry: false,
    queryFn: () => getProducts(query),
  });

  const productCodes = useMemo(
    () => (productsQuery.data?.items ?? []).map((item) => item.productCode).filter(Boolean),
    [productsQuery.data?.items]
  );

  const dynamicDataQuery = useQuery({
    queryKey: ["shopDynamicData", query.storeCode ?? null, productCodes],
    enabled: Boolean(query.storeCode) && productCodes.length > 0,
    queryFn: () =>
      getProductDynamicData({
        storeCode: query.storeCode!,
        productCodes,
      }),
  });

  const dynamicDataMap = useMemo<ProductDynamicDataMap>(() => {
    const data = dynamicDataQuery.data ?? [];

    return data.reduce<ProductDynamicDataMap>((accumulator, item) => {
      accumulator[item.productCode] = item;
      return accumulator;
    }, {});
  }, [dynamicDataQuery.data]);

  return {
    ...productsQuery,
    dynamicData: dynamicDataQuery.data ?? [],
    dynamicDataMap,
    dynamicDataQuery,
  };
}
