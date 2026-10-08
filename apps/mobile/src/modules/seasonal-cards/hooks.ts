import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  fetchSeasonalCardCatalog,
  fetchSeasonalCardOverview,
  fetchSeasonalCardSubmissionDetail,
  fetchSeasonalCardSubmissions,
  submitSeasonalCardBatch,
} from "@/modules/seasonal-cards/api";
import { fetchActiveLocalSuppliers } from "@/modules/local-supplier-invoices/api";
import type {
  SeasonalCardBatchPayload,
  SeasonalCardOverviewQuery,
  SeasonalCardSubmissionQuery,
} from "@/modules/seasonal-cards/types";

export function seasonalCardQueryKeys() {
  return {
    all: ["seasonalCards"] as const,
    catalog: ["seasonalCards", "catalog"] as const,
    suppliers: ["seasonalCards", "suppliers"] as const,
    overviewAll: ["seasonalCards", "overview"] as const,
    overview: (query: SeasonalCardOverviewQuery) =>
      [
        "seasonalCards",
        "overview",
        query.storeCode,
        query.seasonYear,
        query.localSupplierCode,
      ] as const,
    submissionsAll: ["seasonalCards", "submissions"] as const,
    submissions: (query: SeasonalCardSubmissionQuery) =>
      ["seasonalCards", "submissions", query] as const,
    detail: (submissionGuid: string) =>
      ["seasonalCards", "detail", submissionGuid] as const,
  };
}

export function shouldEnableSeasonalCardCatalog(canSubmit: boolean) {
  return canSubmit;
}

/** 总览需要分店、年份、供应商三者齐全；缺供应商时不请求（服务端会返回 SUPPLIER_REQUIRED）。 */
export function shouldEnableSeasonalCardOverview(
  canSubmit: boolean,
  query: Partial<SeasonalCardOverviewQuery>
) {
  return Boolean(
    canSubmit &&
      query.storeCode?.trim() &&
      query.localSupplierCode?.trim() &&
      query.seasonYear &&
      query.seasonYear > 0
  );
}

export function useSeasonalCardCatalog(canSubmit = false) {
  return useQuery({
    queryKey: seasonalCardQueryKeys().catalog,
    enabled: shouldEnableSeasonalCardCatalog(canSubmit),
    queryFn: fetchSeasonalCardCatalog,
  });
}

/** 启用中的本地供应商（约 120 家），变化很慢，缓存 10 分钟。 */
export function useSeasonalCardSuppliers(enabled = true) {
  return useQuery({
    queryKey: seasonalCardQueryKeys().suppliers,
    enabled,
    staleTime: 10 * 60 * 1000,
    queryFn: fetchActiveLocalSuppliers,
  });
}

export function useSeasonalCardOverview(
  query: SeasonalCardOverviewQuery,
  canSubmit = false
) {
  return useQuery({
    queryKey: seasonalCardQueryKeys().overview(query),
    enabled: shouldEnableSeasonalCardOverview(canSubmit, query),
    queryFn: () => fetchSeasonalCardOverview(query),
  });
}

export function useSeasonalCardSubmissions(
  query: SeasonalCardSubmissionQuery,
  enabled = true
) {
  return useQuery({
    queryKey: seasonalCardQueryKeys().submissions(query),
    enabled,
    queryFn: () => fetchSeasonalCardSubmissions(query),
  });
}

export function useSeasonalCardSubmissionDetail(
  submissionGuid: string | null,
  enabled = true
) {
  return useQuery({
    queryKey: seasonalCardQueryKeys().detail(submissionGuid ?? ""),
    enabled: enabled && Boolean(submissionGuid),
    queryFn: () => fetchSeasonalCardSubmissionDetail(submissionGuid ?? ""),
  });
}

export function useSubmitSeasonalCardBatch() {
  const queryClient = useQueryClient();
  const keys = seasonalCardQueryKeys();

  return useMutation({
    mutationFn: (payload: SeasonalCardBatchPayload) => submitSeasonalCardBatch(payload),
    // 成功或被拒（有人抢先提交 / 数量无变化）后，总览与历史都可能已过期，统一失效重拉。
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: keys.overviewAll }),
        queryClient.invalidateQueries({ queryKey: keys.submissionsAll }),
      ]);
    },
  });
}
