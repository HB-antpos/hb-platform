// 人脸比对页面共用的数据钩子（react-query）。
// 缓存里只有列表 / 汇总 / 档案 / 摄像头的结构化数据；图片字节（含列表缩略图）绝不经过 react-query（见 image-session.ts、thumbnail-loader.ts）。
// 查询键都带账号编号：切换账号后不会读到上一个账号的缓存（登出时全局缓存也会清空）。
import { useCallback, useMemo } from "react";
import { useInfiniteQuery, useQuery, useQueryClient, type InfiniteData, type QueryKey } from "@tanstack/react-query";
import { getAllStores } from "@/modules/shop/api";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { faceMatchApi } from "./api";
import { markCaptureResultHitRead, markHitResultRead, mapPagesItems } from "./capture-logic";
import { CAMERA_CAPTURE_PAGE_SIZE, nextCapturePage } from "./cameras-logic";
import { PAGE_SIZE } from "./constants";
import { mergePages, nextPageParam } from "./entries-logic";
import { extractHttpStatus } from "./errors";
import { writeFaceHitUnreadCount } from "./hit-unread";
import type {
  FaceCameraCapturePage,
  FaceCaptureResult,
  FaceCaptureResultQuery,
  FaceHitReadAllResult,
  FaceHitResult,
  FaceHitResultQuery,
  FaceHitSummaryQuery,
  FaceMatchPage,
  FaceUnwelcomeEntry,
  FaceUnwelcomeTask,
} from "./types";

/** 抓拍结果列表的筛选条件（不含分页）。 */
export type CaptureResultFilters = Omit<FaceCaptureResultQuery, "page" | "pageSize">;

/** 命中流（黑名单）列表的筛选条件（不含分页）。 */
export type HitResultFilters = Omit<FaceHitResultQuery, "page" | "pageSize">;

type HitPages = InfiniteData<FaceMatchPage<FaceHitResult>, number>;
type CapturePages = InfiniteData<FaceMatchPage<FaceCaptureResult>, number>;

/** 命中流汇总的筛选条件：只有门店与时间范围。 */
export type HitSummaryFilters = FaceHitSummaryQuery;

export const faceMatchKeys = {
  all: ["face-match"] as const,
  config: (userGuid: string | null) => ["face-match", userGuid, "config"] as const,
  entriesRoot: (userGuid: string | null) => ["face-match", userGuid, "entries"] as const,
  entries: (userGuid: string | null, status: number | undefined, storeCode: string | null) =>
    ["face-match", userGuid, "entries", status ?? "all", storeCode ?? "all"] as const,
  tasksRoot: (userGuid: string | null) => ["face-match", userGuid, "tasks"] as const,
  tasks: (userGuid: string | null, status: number | undefined, storeCode: string | null) =>
    ["face-match", userGuid, "tasks", status ?? "all", storeCode ?? "all"] as const,
  captureResults: (userGuid: string | null, filters: CaptureResultFilters) =>
    ["face-match", userGuid, "capture-results", filters.storeCode ?? "all", filters.decision ?? "all", filters.hitOnly ? "hit" : "any", filters.fromUtc ?? "", filters.toUtc ?? ""] as const,
  captureResultsRoot: (userGuid: string | null) => ["face-match", userGuid, "capture-results"] as const,
  hitResultsRoot: (userGuid: string | null) => ["face-match", userGuid, "hit-results"] as const,
  hitResults: (userGuid: string | null, filters: HitResultFilters) =>
    ["face-match", userGuid, "hit-results", filters.storeCode ?? "all", filters.hitStatus ?? "all", filters.fromUtc ?? "", filters.toUtc ?? ""] as const,
  hitSummaryRoot: (userGuid: string | null) => ["face-match", userGuid, "hit-summary"] as const,
  hitSummary: (userGuid: string | null, filters: HitSummaryFilters) =>
    ["face-match", userGuid, "hit-summary", filters.storeCode ?? "all", filters.fromUtc ?? "", filters.toUtc ?? ""] as const,
  profile: (userGuid: string | null, storeCode: string | null) => ["face-match", userGuid, "profile", storeCode] as const,
  cameras: (userGuid: string | null) => ["face-match", userGuid, "cameras"] as const,
  cameraCaptures: (userGuid: string | null, cameraId: string) => ["face-match", userGuid, "camera-captures", cameraId] as const,
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

/** 抓拍与比对结果：缓存里只有结构化数据与图片编号，图片字节不经过 react-query（详情里按需读取）。 */
export function useCaptureResults(filters: CaptureResultFilters, enabled: boolean) {
  const userGuid = useUserGuid();
  const queryKey = faceMatchKeys.captureResults(userGuid, filters);
  const query = useInfiniteQuery({
    queryKey,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) => faceMatchApi.listCaptureResults({ ...filters, page: pageParam, pageSize: PAGE_SIZE }, signal),
    getNextPageParam: nextPageParam,
    enabled,
    staleTime: 15_000,
    retry: retryTransientOnly,
  });
  const pages = query.data?.pages;
  const items = useMemo<FaceCaptureResult[]>(
    () => (pages ? mergePages(pages, (result) => result.captureId) : []),
    [pages],
  );
  const refresh = useRefreshFirstPage<FaceCaptureResult>(queryKey, query.refetch);
  return { query, items, refresh };
}

/** 黑名单命中流：缓存里只有结构化数据（含通知编号 / 未读标志），图片字节不经过 react-query（详情里按需读取）。 */
export function useHitResults(filters: HitResultFilters, enabled: boolean) {
  const userGuid = useUserGuid();
  const queryKey = faceMatchKeys.hitResults(userGuid, filters);
  const query = useInfiniteQuery({
    queryKey,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) => faceMatchApi.listHits({ ...filters, page: pageParam, pageSize: PAGE_SIZE }, signal),
    getNextPageParam: nextPageParam,
    enabled,
    staleTime: 15_000,
    retry: retryTransientOnly,
  });
  const pages = query.data?.pages;
  const items = useMemo<FaceHitResult[]>(() => (pages ? mergePages(pages, (hit) => hit.taskId) : []), [pages]);
  const refresh = useRefreshFirstPage<FaceHitResult>(queryKey, query.refetch);
  return { query, items, refresh };
}

/** 命中流各状态条数（「黑名单」chip 的红色数字、二级筛选的数字、列表条）；与列表同样的门店与时间条件。 */
export function useHitSummary(filters: HitSummaryFilters, enabled: boolean) {
  const userGuid = useUserGuid();
  return useQuery({
    queryKey: faceMatchKeys.hitSummary(userGuid, filters),
    queryFn: ({ signal }) => faceMatchApi.getHitSummary(filters, signal),
    enabled,
    staleTime: 15_000,
    retry: retryTransientOnly,
  });
}

/**
 * 命中通知「已读」动作：
 * - markRead(taskId)：打开详情时调用。成功后用接口返回的剩余未读数写回红色数字缓存（导航徽标立刻变化），
 *   就地把缓存里（命中流与抓拍流）那一行的 unread 改成 false（行仍留在当前列表里，下拉刷新才按筛选重排），并让汇总重新取；
 * - markAllRead()：「全部标为已读」。成功后写回剩余未读数，并刷新汇总与两种列表。
 * 失败原样抛给调用方（打开详情时静默，全部已读时给轻提示）。
 */
export function useHitReadActions() {
  const queryClient = useQueryClient();
  const userGuid = useUserGuid();
  const markRead = useCallback(
    async (taskId: string): Promise<number> => {
      const unreadCount = await faceMatchApi.markHitRead(taskId);
      writeFaceHitUnreadCount(queryClient, userGuid, unreadCount);
      queryClient.setQueriesData<HitPages>({ queryKey: faceMatchKeys.hitResultsRoot(userGuid) }, (data) =>
        mapPagesItems<FaceHitResult, HitPages>(data, (item) => markHitResultRead(item, taskId)),
      );
      queryClient.setQueriesData<CapturePages>({ queryKey: faceMatchKeys.captureResultsRoot(userGuid) }, (data) =>
        mapPagesItems<FaceCaptureResult, CapturePages>(data, (item) => markCaptureResultHitRead(item, taskId)),
      );
      void queryClient.invalidateQueries({ queryKey: faceMatchKeys.hitSummaryRoot(userGuid) });
      return unreadCount;
    },
    [queryClient, userGuid],
  );
  const markAllRead = useCallback(async (): Promise<FaceHitReadAllResult> => {
    const result = await faceMatchApi.markAllHitsRead();
    writeFaceHitUnreadCount(queryClient, userGuid, result.unreadCount);
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: faceMatchKeys.hitSummaryRoot(userGuid) }),
      queryClient.invalidateQueries({ queryKey: faceMatchKeys.hitResultsRoot(userGuid) }),
      queryClient.invalidateQueries({ queryKey: faceMatchKeys.captureResultsRoot(userGuid) }),
    ]);
    return result;
  }, [queryClient, userGuid]);
  return { markRead, markAllRead };
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

/**
 * 某台摄像头的最近抓拍（只有元数据，图片字节不经过这里）。按页累加，到页码上限后不再有「加载更多」。
 * 打开抓拍弹框才启用；关闭（enabled=false）后缓存按默认时间回收。
 */
export function useFaceCameraCaptures(cameraId: string | null, enabled: boolean) {
  const userGuid = useUserGuid();
  const queryKey = faceMatchKeys.cameraCaptures(userGuid, cameraId ?? "");
  const query = useInfiniteQuery({
    queryKey,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) => faceMatchApi.listCameraCaptures(cameraId!, pageParam, CAMERA_CAPTURE_PAGE_SIZE, signal),
    getNextPageParam: nextCapturePage,
    enabled: enabled && Boolean(cameraId),
    staleTime: 0,
    // 抓拍记录是生物识别相关的元数据：弹框关闭（没有观察者）就立刻丢缓存，重新打开从第 1 页开始，不会把缓存里的所有分页逐页重发。
    gcTime: 0,
    retry: retryTransientOnly,
  });
  // 刷新只重取第一页：先把缓存截到第一页，避免 refetch 把已滚过的（最多 100 页）逐页重发。
  const queryClient = useQueryClient();
  const { refetch } = query;
  const refresh = useCallback(async () => {
    queryClient.setQueryData<InfiniteData<FaceCameraCapturePage, number>>(queryKey, (data) =>
      data ? { pages: data.pages.slice(0, 1), pageParams: data.pageParams.slice(0, 1) } : data,
    );
    await refetch();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- queryKey 由 userGuid + cameraId 决定，下面两项已覆盖
  }, [queryClient, refetch, userGuid, cameraId]);
  return { query, refresh };
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
