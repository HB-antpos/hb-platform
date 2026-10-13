// 人脸比对页面共用的数据钩子（react-query）。
// 缓存里只有列表 / 档案 / 摄像头的结构化数据；图片字节绝不经过 react-query（见 image-session.ts）。
// 查询键都带账号编号：切换账号后不会读到上一个账号的缓存（登出时全局缓存也会清空）。
import { useMemo } from "react";
import { useInfiniteQuery, useQuery, useQueryClient, type InfiniteData, type QueryKey } from "@tanstack/react-query";
import { getAllStores } from "@/modules/shop/api";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { faceMatchApi } from "./api";
import { PAGE_SIZE } from "./constants";
import { mergePages, nextPageParam } from "./entries-logic";
import { extractHttpStatus } from "./errors";
import type { FaceMatchPage, FaceUnwelcomeEntry, FaceUnwelcomeTask } from "./types";

export const faceMatchKeys = {
  all: ["face-match"] as const,
  config: (userGuid: string | null) => ["face-match", userGuid, "config"] as const,
  entriesRoot: (userGuid: string | null) => ["face-match", userGuid, "entries"] as const,
  entries: (userGuid: string | null, status: number | undefined, storeCode: string | null) =>
    ["face-match", userGuid, "entries", status ?? "all", storeCode ?? "all"] as const,
  tasksRoot: (userGuid: string | null) => ["face-match", userGuid, "tasks"] as const,
  tasks: (userGuid: string | null, status: number | undefined, storeCode: string | null) =>
    ["face-match", userGuid, "tasks", status ?? "all", storeCode ?? "all"] as const,
  profile: (userGuid: string | null, storeCode: string | null) => ["face-match", userGuid, "profile", storeCode] as const,
  cameras: (userGuid: string | null) => ["face-match", userGuid, "cameras"] as const,
  stores: (userGuid: string | null) => ["face-match", userGuid, "stores"] as const,
};

/** 4xx 是确定的业务结论（无权限、不存在、状态不允许），重试没有意义；网络与 5xx 才重试一次。 */
function retryTransientOnly(failureCount: number, error: unknown): boolean {
  const status = extractHttpStatus(error);
  if (status !== null && status >= 400 && status < 500) return false;
  return failureCount < 1;
}

/** 下拉刷新：先把缓存截到第一页再刷新，只重取一页，不会把滚过的所有分页逐页重发。 */
function useRefreshFirstPage<T>(queryKey: QueryKey, refetch: () => Promise<unknown>) {
  const queryClient = useQueryClient();
  return async () => {
    queryClient.setQueryData<InfiniteData<FaceMatchPage<T>, number>>(queryKey, (data) =>
      data ? { pages: data.pages.slice(0, 1), pageParams: data.pageParams.slice(0, 1) } : data,
    );
    await refetch();
  };
}

function useUserGuid(): string | null {
  return useAuthStore((state) => state.user?.userGUID ?? null);
}

/** 配置与权限标志。仅有摄像机管理权限的账号会得到 403，调用方据此隐藏需要 FaceMatch.* 的部分。 */
export function useFaceMatchConfig(enabled: boolean) {
  const userGuid = useUserGuid();
  return useQuery({
    queryKey: faceMatchKeys.config(userGuid),
    queryFn: ({ signal }) => faceMatchApi.getConfig(signal),
    enabled,
    staleTime: 60_000,
    retry: retryTransientOnly,
  });
}

export function useUnwelcomeEntries(status: number | undefined, storeCode: string | null, enabled: boolean) {
  const userGuid = useUserGuid();
  const queryKey = faceMatchKeys.entries(userGuid, status, storeCode);
  const query = useInfiniteQuery({
    queryKey,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) =>
      faceMatchApi.listEntries({ status, storeCode: storeCode ?? undefined, page: pageParam, pageSize: PAGE_SIZE }, signal),
    getNextPageParam: nextPageParam,
    enabled,
    staleTime: 15_000,
    retry: retryTransientOnly,
  });
  const pages = query.data?.pages;
  const items = useMemo<FaceUnwelcomeEntry[]>(
    () => (pages ? mergePages(pages, (entry) => entry.entryId) : []),
    [pages],
  );
  const refresh = useRefreshFirstPage<FaceUnwelcomeEntry>(queryKey, query.refetch);
  return { query, items, refresh };
}

export function useUnwelcomeTasks(status: number | undefined, storeCode: string | null, enabled: boolean) {
  const userGuid = useUserGuid();
  const queryKey = faceMatchKeys.tasks(userGuid, status, storeCode);
  const query = useInfiniteQuery({
    queryKey,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) =>
      faceMatchApi.listTasks({ status, storeCode: storeCode ?? undefined, page: pageParam, pageSize: PAGE_SIZE }, signal),
    getNextPageParam: nextPageParam,
    enabled,
    staleTime: 15_000,
    retry: retryTransientOnly,
  });
  const pages = query.data?.pages;
  const items = useMemo<FaceUnwelcomeTask[]>(
    () => (pages ? mergePages(pages, (task) => task.taskId) : []),
    [pages],
  );
  const refresh = useRefreshFirstPage<FaceUnwelcomeTask>(queryKey, query.refetch);
  return { query, items, refresh };
}

export function useFaceProfile(storeCode: string | null, enabled: boolean) {
  const userGuid = useUserGuid();
  return useQuery({
    queryKey: faceMatchKeys.profile(userGuid, storeCode),
    queryFn: ({ signal }) => faceMatchApi.getProfile(storeCode!, signal),
    enabled: enabled && Boolean(storeCode),
    staleTime: 0,
    retry: retryTransientOnly,
  });
}

export function useFaceCameras(enabled: boolean) {
  const userGuid = useUserGuid();
  return useQuery({
    queryKey: faceMatchKeys.cameras(userGuid),
    queryFn: ({ signal }) => faceMatchApi.listCameras(signal),
    enabled,
    staleTime: 0,
    retry: retryTransientOnly,
  });
}

/**
 * 门店选择的数据源：沿用 App 现有的门店服务 useStores()（账号关联分店）。
 * 持有 Stores.View（管理员等）的账号额外读取全部分店（与门店管理、账号授权页同一个接口 /stores/all-by-name），
 * 因为人脸比对档案与摄像头不受「关联分店」限制；读取失败时退回关联分店，页面不会因此不可用。
 * 这里不调用 useStores().selectStore，不会改动购物车 / 订货页的全局已选门店。
 */
export function useFaceStoreOptions(): { stores: Store[]; isLoading: boolean; preferredStoreCode: string | null } {
  const userGuid = useUserGuid();
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const { stores: assignedStores, isLoading: assignedLoading, selectedStoreCode } = useStores();
  const canReadAllStores = hasPermission("Stores.View");
  const allStores = useQuery({
    queryKey: faceMatchKeys.stores(userGuid),
    queryFn: () => getAllStores(),
    enabled: canReadAllStores && Boolean(userGuid),
    staleTime: 10 * 60_000,
    retry: false,
  });
  const stores = useMemo<Store[]>(() => {
    const all = allStores.data;
    return all && all.length > 0 ? all : assignedStores;
  }, [allStores.data, assignedStores]);
  return {
    stores,
    isLoading: assignedLoading || (canReadAllStores && allStores.isLoading),
    /** App 当前已选门店（只读取，用作设置页的默认选择）。 */
    preferredStoreCode: selectedStoreCode,
  };
}

/** 写操作成功后让名单条目、复核任务列表重新取（档案与摄像头各自就地更新缓存）。 */
export function useInvalidateUnwelcome() {
  const queryClient = useQueryClient();
  const userGuid = useUserGuid();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: faceMatchKeys.entriesRoot(userGuid) }),
      queryClient.invalidateQueries({ queryKey: faceMatchKeys.tasksRoot(userGuid) }),
    ]);
}
