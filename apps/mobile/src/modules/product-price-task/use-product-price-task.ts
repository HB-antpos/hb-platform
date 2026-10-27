import { useQuery } from "@tanstack/react-query";
import { getPriceUpdateTasks } from "../price-updates/api";
import { priceUpdateTasksQueryKey } from "../price-updates/query-keys";
import type { StorePriceUpdateTask } from "../price-updates/types";
import { pickProductTask } from "./view-model";

// 同店同品至多一条未完成任务；后端关键字是模糊匹配，多取一些再在前端精确过滤即可。
const LOOKUP_PAGE_SIZE = 20;

/** 挂在 priceUpdates/tasks 前缀下：价格和标签页、批处理完成后的统一失效会一并刷新这里。 */
export function productPriceTaskQueryKey(storeCode: string | null, productCode: string | null) {
  return [...priceUpdateTasksQueryKey(), "product", storeCode, productCode] as const;
}

/**
 * 查「当前分店 + 当前商品」是否有未完成的价格 / 标签任务。
 * 这是辅助提示：查询失败时静默当作没有任务，不能打扰商品查询本身。
 */
export function useProductPriceTask(input: {
  storeCode: string | null;
  productCode: string | null;
  enabled: boolean;
}) {
  const { storeCode, productCode, enabled } = input;
  return useQuery<StorePriceUpdateTask | null>({
    queryKey: productPriceTaskQueryKey(storeCode, productCode),
    enabled: enabled && Boolean(storeCode) && Boolean(productCode),
    retry: false,
    // 每次回到商品页都要拿到最新任务状态，别人可能刚在别处处理过。
    staleTime: 0,
    queryFn: async () => {
      if (!storeCode || !productCode) return null;
      const page = await getPriceUpdateTasks({
        storeCode,
        status: "Pending",
        keyword: productCode,
        page: 1,
        pageSize: LOOKUP_PAGE_SIZE,
      });
      return pickProductTask(page.items, { productCode }, storeCode);
    },
  });
}
