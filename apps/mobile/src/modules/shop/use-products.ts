import { useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { getProductDynamicData, getProducts } from "@/modules/shop/api";
import {
  buildShopDynamicDataQueryKey,
  buildShopProductsQueryKey,
  resolvePageProductCodes,
  resolveShopProductsPlaceholderData,
} from "@/modules/shop/product-query-key";
import type { ProductDynamicDataMap, StoreOrderProductQuery } from "@/modules/shop/types";

export { buildShopProductsQueryKey } from "@/modules/shop/product-query-key";

// 商品接口顺带写入的动态数据在这段时间内视为新鲜，避免挂载后立刻重复请求；
// 加购、改数量仍通过 setQueriesData / invalidate 实时更新这份缓存。
const EMBEDDED_DYNAMIC_DATA_STALE_MS = 15_000;

export function useProducts(query: StoreOrderProductQuery, locationLookupEnabled = false) {
  const queryClient = useQueryClient();
  const productsQuery = useQuery({
    queryKey: buildShopProductsQueryKey(query, locationLookupEnabled),
    enabled: Boolean(query.storeCode),
    staleTime: 5 * 60 * 1000,
    placeholderData: (previousData, previousQuery) =>
      resolveShopProductsPlaceholderData(
        previousData,
        previousQuery,
        locationLookupEnabled,
        query,
      ),
    retry: false,
    queryFn: async () => {
      const result = await getProducts(query);
      if (query.storeCode && result.dynamicData) {
        // 后端已随商品页返回动态数据：直接写入与下方查询同一个键，不再发第二次串行请求。
        queryClient.setQueryData(
          buildShopDynamicDataQueryKey(query.storeCode, resolvePageProductCodes(result.items)),
          result.dynamicData,
        );
      }
      return result;
    },
  });

  const productCodes = useMemo(
    () => resolvePageProductCodes(productsQuery.data?.items),
    [productsQuery.data?.items]
  );

  const dynamicDataQuery = useQuery({
    queryKey: buildShopDynamicDataQueryKey(query.storeCode, productCodes),
    enabled: Boolean(query.storeCode) && productCodes.length > 0,
    staleTime: EMBEDDED_DYNAMIC_DATA_STALE_MS,
    queryFn: () =>
      getProductDynamicData({
        storeCode: query.storeCode!,
        productCodes,
        includeSales: false,
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
