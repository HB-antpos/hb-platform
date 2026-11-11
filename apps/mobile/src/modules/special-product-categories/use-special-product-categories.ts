import { useMemo } from "react";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { getAllStores } from "@/modules/shop/api";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { specialCategoriesApi } from "./api";
import { canSearchCandidates, pickInitialStore, resolveManagedStores } from "./logic";

// react-query 键统一挂在 ["special-product-categories"] 下并带账号编号：切换账号后不会读到上一个账号的缓存。
export const specialCategoryKeys = {
  all: ["special-product-categories"] as const,
  overview: (userGuid: string | null, storeCode: string) => ["special-product-categories", "overview", userGuid, storeCode] as const,
  products: (userGuid: string | null, storeCode: string, categoryId: string, keyword: string) =>
    ["special-product-categories", "products", userGuid, storeCode, categoryId, keyword] as const,
  candidates: (userGuid: string | null, storeCode: string, keyword: string) =>
    ["special-product-categories", "candidates", userGuid, storeCode, keyword] as const,
  syncTargets: (userGuid: string | null, storeCode: string) => ["special-product-categories", "sync-targets", userGuid, storeCode] as const,
  allStores: (userGuid: string | null) => ["special-product-categories", "all-stores", userGuid] as const,
};

/** 4xx 是确定的业务结论（无权限、校验失败），重试没有意义；网络与 5xx 才重试一次。 */
export function retryTransientOnly(failureCount: number, error: unknown) {
  const status = (error as { response?: { status?: number } })?.response?.status;
  return !(status && status >= 400 && status < 500) && failureCount < 1;
}

/**
 * 当前账号「管理的分店」（主分店口径，来自 access.managedStoreCodes()）：
 * 受限账号 = 已分配分店里的主分店；managedStoreCodes() 为 null（管理员 / 仓库经理，不限分店）= 全部分店。
 */
export function useManagedStores() {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const access = useAuthStore((state) => state.access);
  const { stores: assignedStores, selectedStoreCode, isLoading: assignedLoading } = useStores();
  const managedCodes = useMemo(() => access.managedStoreCodes(), [access]);
  const unrestricted = managedCodes === null;
  // 不限分店的账号：已分配分店可能只是一部分，这里取全部分店；失败时退回已分配分店。
  const allStoresQuery = useQuery({
    queryKey: specialCategoryKeys.allStores(userGuid),
    enabled: unrestricted && Boolean(userGuid),
    queryFn: async () => (await getAllStores()).map((store) => ({ storeCode: store.storeCode, storeName: store.storeName || store.storeCode })),
    staleTime: 10 * 60_000,
    retry: false,
  });
  const stores = useMemo(() => {
    const source = unrestricted && allStoresQuery.data?.length ? allStoresQuery.data : assignedStores;
    return resolveManagedStores(
      source.map((store) => ({ storeCode: store.storeCode, storeName: store.storeName || store.storeCode })),
      managedCodes,
    );
  }, [allStoresQuery.data, assignedStores, managedCodes, unrestricted]);
  const loading = assignedLoading || (unrestricted && allStoresQuery.isPending);
  return { stores, loading, preferredStoreCode: selectedStoreCode, pickInitial: () => pickInitialStore(stores, selectedStoreCode) };
}

export function useCategoryOverview(storeCode: string | null) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: specialCategoryKeys.overview(userGuid, storeCode ?? ""),
    queryFn: ({ signal }) => specialCategoriesApi.getOverview(storeCode!, signal),
    enabled: Boolean(storeCode),
    // 分类被收银端 / 其他手机同时改动的可能性小但存在：每次进入页面都重新取。
    staleTime: 0,
    retry: retryTransientOnly,
  });
}

export const PRODUCTS_PAGE_SIZE = 100;

/** 分类里的商品（分页）。categoryId 传分类 id 或 "uncategorized"。 */
export function useCategoryProducts(storeCode: string | null, categoryId: string, keyword: string) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useInfiniteQuery({
    queryKey: specialCategoryKeys.products(userGuid, storeCode ?? "", categoryId, keyword),
    queryFn: ({ pageParam, signal }) =>
      specialCategoriesApi.getProducts({ storeCode: storeCode!, categoryId, keyword, page: pageParam, pageSize: PRODUCTS_PAGE_SIZE }, signal),
    initialPageParam: 1,
    getNextPageParam: (last, pages) => {
      const loaded = pages.reduce((sum, page) => sum + page.items.length, 0);
      return loaded < last.total && last.items.length > 0 ? last.page + 1 : undefined;
    },
    enabled: Boolean(storeCode && categoryId),
    staleTime: 0,
    retry: retryTransientOnly,
  });
}

/** 「添加商品」搜索：关键字不足 2 个字符不请求。 */
export function useCandidateSearch(storeCode: string | null, keyword: string, enabled: boolean) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const trimmed = keyword.trim();
  return useQuery({
    queryKey: specialCategoryKeys.candidates(userGuid, storeCode ?? "", trimmed),
    queryFn: ({ signal }) => specialCategoriesApi.getCandidates({ storeCode: storeCode!, keyword: trimmed }, signal),
    enabled: enabled && Boolean(storeCode) && canSearchCandidates(trimmed),
    staleTime: 0,
    gcTime: 30_000,
    retry: retryTransientOnly,
  });
}

export function useSyncTargets(storeCode: string | null) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: specialCategoryKeys.syncTargets(userGuid, storeCode ?? ""),
    queryFn: ({ signal }) => specialCategoriesApi.getSyncTargets(storeCode!, signal),
    enabled: Boolean(storeCode),
    // 各店现状随时在变，每次打开都重新取，避免按旧数据误覆盖。
    staleTime: 0,
    gcTime: 0,
    retry: retryTransientOnly,
  });
}
