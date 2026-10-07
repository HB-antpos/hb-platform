import { useEffect, useMemo, useState } from "react";
import { useInfiniteQuery, useQuery, useQueryClient, type InfiniteData } from "@tanstack/react-query";
import { addDays } from "@/modules/store-cash/dates";
import { useAuthStore } from "@/store/auth-store";
import { fetchDailyCloseDetail, fetchDailyCloses } from "./api";
import {
  buildDailyCloseListParams,
  classifyDailyCloseError,
  collectDeviceCodes,
  DAILY_CLOSE_DEVICE_LOOKBACK_DAYS,
  DAILY_CLOSE_PAGE_SIZE,
  mergeDailyClosePages,
  nextDailyClosePage,
  todayInSydney,
} from "./logic";
import type { DailyCloseFilters, DailyCloseListPage, DailyCloseListParams } from "./types";

// react-query 键都挂在 ["daily-closes"] 下并带账号编号：切换账号后不会读到上一个账号的缓存（登出时全局缓存也会清空）。
export const dailyCloseKeys = {
  all: ["daily-closes"] as const,
  list: (userGuid: string | null, params: DailyCloseListParams | null) => ["daily-closes", "list", userGuid, params] as const,
  preview: (userGuid: string | null, params: DailyCloseListParams | null) => ["daily-closes", "preview", userGuid, params] as const,
  devices: (userGuid: string | null, storeCode: string | null) => ["daily-closes", "devices", userGuid, storeCode] as const,
  detail: (userGuid: string | null, guid: string) => ["daily-closes", "detail", userGuid, guid] as const,
};

/** 4xx 是确定的业务结论（无权限、不存在、参数不合法），重试没有意义；网络与 5xx 才重试一次。 */
function retryTransientOnly(failureCount: number, error: unknown) {
  return classifyDailyCloseError(error) === "other" && failureCount < 1;
}

/** 列表：按页签/筛选分页拉取，页码从 1 起；区间不合法（自定义超过 93 天等）时不发请求。 */
export function useDailyCloseList(filters: DailyCloseFilters, enabled: boolean) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const today = todayInSydney();
  const baseParams = useMemo(() => buildDailyCloseListParams(filters, 1, DAILY_CLOSE_PAGE_SIZE, today), [filters, today]);
  const queryKey = dailyCloseKeys.list(userGuid, baseParams);
  const queryClient = useQueryClient();
  const query = useInfiniteQuery({
    queryKey,
    enabled: enabled && baseParams !== null,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) => {
      const params = buildDailyCloseListParams(filters, pageParam, DAILY_CLOSE_PAGE_SIZE, today);
      if (!params) throw new Error("Invalid daily close range");
      return fetchDailyCloses(params, signal);
    },
    getNextPageParam: (lastPage, allPages) => nextDailyClosePage(lastPage, mergeDailyClosePages(allPages).length),
    staleTime: 30_000,
    retry: retryTransientOnly,
  });

  const pages = query.data?.pages;
  const items = useMemo(() => (pages ? mergeDailyClosePages(pages) : []), [pages]);
  const firstPage: DailyCloseListPage | undefined = pages?.[0];
  /** 下拉刷新：先把缓存截到第一页再刷新，只重取一页，不会把滚过的所有分页逐页重发。 */
  const refresh = async () => {
    queryClient.setQueryData<InfiniteData<DailyCloseListPage, number>>(queryKey, (data) =>
      data ? { pages: data.pages.slice(0, 1), pageParams: data.pageParams.slice(0, 1) } : data,
    );
    await query.refetch();
  };

  return { query, items, firstPage, refresh, rangeValid: baseParams !== null };
}

/**
 * 筛选面板「查看 N 条」的实时命中数：对草稿筛选取 1 条，只用返回的总数。
 * 防抖 400ms，避免连续点选时每点一下都发请求；草稿区间不合法时不查。
 */
export function useDailyClosePreviewCount(draft: DailyCloseFilters, enabled: boolean) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const today = todayInSydney();
  const params = useMemo(() => buildDailyCloseListParams(draft, 1, 1, today), [draft, today]);
  const [debounced, setDebounced] = useState(params);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(params), 400);
    return () => clearTimeout(timer);
  }, [params]);
  const query = useQuery({
    queryKey: dailyCloseKeys.preview(userGuid, debounced),
    queryFn: ({ signal }) => fetchDailyCloses(debounced!, signal),
    enabled: enabled && debounced !== null,
    staleTime: 30_000,
    retry: false,
  });
  // 防抖窗口内（草稿已变、查询还没发）数字是旧的，标记为 pending 让按钮不显示过期数字
  const pending = JSON.stringify(debounced) !== JSON.stringify(params);
  return { total: pending ? undefined : query.data?.total, loading: pending || query.isFetching, failed: query.isError };
}

/** 单个分店的终端选项：取该店近 93 天的日结（最多 100 条）里出现过的终端，缓存 5 分钟。 */
export function useStoreDeviceOptions(storeCode: string | null, selected: string | null) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const today = todayInSydney();
  const query = useQuery({
    queryKey: dailyCloseKeys.devices(userGuid, storeCode),
    queryFn: ({ signal }) =>
      fetchDailyCloses(
        {
          businessDateFrom: addDays(today, -(DAILY_CLOSE_DEVICE_LOOKBACK_DAYS - 1)),
          businessDateTo: today,
          storeCodes: storeCode!,
          page: 1,
          pageSize: 100,
        },
        signal,
      ),
    enabled: Boolean(storeCode),
    staleTime: 5 * 60_000,
    retry: false,
  });
  const devices = useMemo(() => (storeCode ? collectDeviceCodes(query.data?.items ?? [], storeCode, selected) : []), [query.data, selected, storeCode]);
  return { devices, loading: query.isFetching };
}

export function useDailyCloseDetail(guid: string) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: dailyCloseKeys.detail(userGuid, guid),
    queryFn: ({ signal }) => fetchDailyCloseDetail(guid, signal),
    enabled: Boolean(guid),
    staleTime: 60_000,
    retry: retryTransientOnly,
  });
}
