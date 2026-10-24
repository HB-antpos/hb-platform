import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { fetchActiveLocalSuppliers } from "@/modules/product-maintenance/api";
import { getAllStores } from "@/modules/shop/api";
import { useAuthStore } from "@/store/auth-store";
import { fetchPricingStrategies, fetchPricingStrategy } from "./api";
import type { NamedOption, PricingListFilters } from "./types";

// react-query 键都挂在 ["pricing-strategies"] 下并带账号编号：切换账号后不会读到上一个账号的缓存。
export const pricingStrategyKeys = {
  all: ["pricing-strategies"] as const,
  list: (userGuid: string | null, filters: PricingListFilters) => ["pricing-strategies", "list", userGuid, filters] as const,
  detail: (userGuid: string | null, id: string) => ["pricing-strategies", "detail", userGuid, id] as const,
  stores: (userGuid: string | null) => ["pricing-strategies", "stores", userGuid] as const,
  suppliers: (userGuid: string | null) => ["pricing-strategies", "suppliers", userGuid] as const,
};

/** 4xx 是确定的业务结论（无权限、不存在），重试没有意义；网络与 5xx 才重试一次。 */
function retryTransientOnly(failureCount: number, error: unknown) {
  const status = (error as { response?: { status?: number } })?.response?.status;
  return !(status && status >= 400 && status < 500) && failureCount < 1;
}

export function usePricingStrategyList(filters: PricingListFilters) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: pricingStrategyKeys.list(userGuid, filters),
    queryFn: ({ signal }) => fetchPricingStrategies(filters, signal),
    staleTime: 30_000,
    retry: retryTransientOnly,
  });
}

export function usePricingStrategyDetail(id: string | null) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: pricingStrategyKeys.detail(userGuid, id ?? ""),
    queryFn: ({ signal }) => fetchPricingStrategy(id!, signal),
    enabled: Boolean(id),
    // 编辑页每次打开都取最新的策略，避免在旧数据上改完覆盖别人刚保存的版本。
    staleTime: 0,
    gcTime: 0,
    retry: retryTransientOnly,
  });
}

/** 分店与供应商的名称表（与 Web 一样用全部启用分店、全部启用的本地供应商），缓存 10 分钟；失败时界面退回显示编号。 */
export function usePricingScopeOptions() {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const storesQuery = useQuery({
    queryKey: pricingStrategyKeys.stores(userGuid),
    queryFn: async (): Promise<NamedOption[]> =>
      (await getAllStores()).map((store) => ({ code: store.storeCode, name: store.storeName || store.storeCode })),
    staleTime: 10 * 60_000,
    retry: false,
  });
  const suppliersQuery = useQuery({
    queryKey: pricingStrategyKeys.suppliers(userGuid),
    queryFn: async (): Promise<NamedOption[]> =>
      (await fetchActiveLocalSuppliers()).map((supplier) => ({ code: supplier.supplierCode, name: supplier.supplierName || supplier.supplierCode })),
    staleTime: 10 * 60_000,
    retry: false,
  });
  const stores = useMemo(() => storesQuery.data ?? [], [storesQuery.data]);
  const suppliers = useMemo(() => suppliersQuery.data ?? [], [suppliersQuery.data]);
  const storeNames = useMemo(() => new Map(stores.map((store) => [store.code, store.name])), [stores]);
  const supplierNames = useMemo(() => new Map(suppliers.map((supplier) => [supplier.code, supplier.name])), [suppliers]);
  return { stores, suppliers, storeNames, supplierNames, loading: storesQuery.isPending || suppliersQuery.isPending };
}
