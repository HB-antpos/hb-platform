import { useCallback, useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import type { Store } from "@/modules/shop/types";
import { getPriceUpdateSummary } from "./api";
import { buildPriceTaskOverview, type PriceTaskOverview } from "./overview";
import { priceUpdateSummaryQueryKey } from "./query-keys";

/** 汇总 30 秒内视为新鲜；前台每 5 分钟自动刷新一次，回到前台 / 改价后由各处主动失效。 */
const SUMMARY_STALE_MS = 30_000;
const SUMMARY_REFETCH_INTERVAL_MS = 5 * 60_000;

export interface UsePriceTaskOverviewOptions {
  enabled: boolean;
  /** 账号 / 设备身份：放进查询键，换账号时不会读到上一个人的汇总缓存。 */
  scopeKey: string;
  stores: readonly Store[];
  isDeviceMode: boolean;
  deviceBoundStore: Store | null;
  isAdmin: boolean;
  /** 仓库管理员：管理分店与店长同口径（只算 isPrimary），且不锁报表，见 buildPriceTaskOverview。 */
  isWarehouseManager: boolean;
}

/**
 * 价格和标签任务的概览（待办合计、管理分店超期数、报表是否锁定）。
 * 只在 Shell 布局里调用一次，结果经导航上下文分发，避免多个页面各查各的、数字对不上。
 * 接口失败且没有旧数据时概览为「未就绪」，此时不提醒也不锁定（故障不能把人挡在报表外）。
 */
export function usePriceTaskOverview(options: UsePriceTaskOverviewOptions): {
  overview: PriceTaskOverview;
  refresh: () => Promise<PriceTaskOverview | null>;
} {
  const { enabled, scopeKey, stores, isDeviceMode, deviceBoundStore, isAdmin, isWarehouseManager } = options;
  const query = useQuery({
    queryKey: [...priceUpdateSummaryQueryKey(), scopeKey],
    enabled,
    queryFn: getPriceUpdateSummary,
    staleTime: SUMMARY_STALE_MS,
    refetchInterval: SUMMARY_REFETCH_INTERVAL_MS,
  });

  const summary = query.data;
  const overview = useMemo(
    () => buildPriceTaskOverview({ summary, stores, isDeviceMode, deviceBoundStore, isAdmin, isWarehouseManager }),
    [deviceBoundStore, isAdmin, isDeviceMode, isWarehouseManager, stores, summary]
  );

  const { refetch } = query;
  const refresh = useCallback(async () => {
    // refetch() 会无视 enabled:false：没有价格和标签入口 / 权限的账号不能去请求汇总接口（白白吃一个 403）。
    if (!enabled) return null;
    const result = await refetch();
    return result.data
      ? buildPriceTaskOverview({
          summary: result.data,
          stores,
          isDeviceMode,
          deviceBoundStore,
          isAdmin,
          isWarehouseManager,
        })
      : null;
  }, [deviceBoundStore, enabled, isAdmin, isDeviceMode, isWarehouseManager, refetch, stores]);

  return { overview, refresh };
}
