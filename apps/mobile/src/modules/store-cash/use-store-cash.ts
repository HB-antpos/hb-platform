import { useCallback } from "react";
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { fetchCashContext } from "./api";
import { storeCashKeys } from "./query-keys";

/** 进入「现金」入口先取一次：可操作分店、权限能力、阈值、日结是否接入。 */
export function useCashContextQuery(enabled: boolean) {
  return useQuery({
    queryKey: storeCashKeys.context(),
    queryFn: fetchCashContext,
    enabled,
    staleTime: 60_000,
  });
}

/** 写操作成功后让总览、按日、记录、详情全部失效重取；上下文（分店、阈值）不用重取。 */
export function invalidateCashData(queryClient: QueryClient) {
  return queryClient.invalidateQueries({
    predicate: (query) => query.queryKey[0] === "store-cash" && query.queryKey[1] !== "context",
  });
}

export function useInvalidateCashData() {
  const queryClient = useQueryClient();
  return useCallback(() => invalidateCashData(queryClient), [queryClient]);
}
