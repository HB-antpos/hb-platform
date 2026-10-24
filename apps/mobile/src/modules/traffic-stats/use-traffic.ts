// 客流统计的数据钩子：一个周期一次请求（当期 + 对照期一起回来）。
import { useQuery } from "@tanstack/react-query";
import { useAuthStore } from "@/store/auth-store";
import { trafficStatsApi } from "./api";
import type { TrafficPeriod } from "./types";

/** 当期（含今天）每 5 分钟自动刷新一次；页面在后台 / 不在前台时不刷新。 */
export const LIVE_REFRESH_MS = 5 * 60_000;
/** 一分钟内重复进入同一视图不再重取（下拉刷新与定时刷新不受影响）。 */
export const TRAFFIC_STALE_MS = 60_000;

export const trafficKeys = {
  root: (userGuid: string | null) => ["traffic-stats", userGuid] as const,
  overview: (userGuid: string | null, storeCode: string | null, period: TrafficPeriod, anchor: string | null) =>
    ["traffic-stats", userGuid, "overview", storeCode, period, anchor ?? "current"] as const,
};

export function useTrafficOverview({
  storeCode,
  period,
  anchor,
  focused,
}: {
  storeCode: string | null;
  period: TrafficPeriod;
  anchor: string | null;
  focused: boolean;
}) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: trafficKeys.overview(userGuid, storeCode, period, anchor),
    queryFn: ({ signal }) => trafficStatsApi.fetchOverview({ storeCode: storeCode ?? "", period, anchor }, signal),
    enabled: Boolean(userGuid && storeCode),
    staleTime: TRAFFIC_STALE_MS,
    retry: 1,
    // 翻页 / 切周期时保留上一屏避免闪白；换门店不保留（避免把 A 店的数字挂在 B 店名下）。
    placeholderData: (previous, previousQuery) => (previousQuery?.queryKey[3] === storeCode ? previous : undefined),
    refetchInterval: (query) => (focused && query.state.data?.isCurrent ? LIVE_REFRESH_MS : false),
  });
}
