import { useCallback } from "react";
import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuthStore } from "@/store/auth-store";
import {
  fetchStaffNoticeBatch,
  fetchStaffNoticeBatches,
  fetchStaffNoticePreflight,
  fetchStaffNoticePreview,
  fetchStaffNoticeTemplates,
  retryStaffNoticeBatch,
  sendStaffNotice,
} from "./api";
import { STAFF_NOTICE_POLL_MS, isBatchFinished } from "./logic";
import type { StaffNoticePreflightRequest, StaffNoticeSendRequest } from "./types";

const RECORDS_PAGE_SIZE = 20;

/** 缓存按账号隔离：切换账号后不能看到上一个账号的发送记录。 */
function useActorKey() {
  const user = useAuthStore((state) => state.user);
  return user?.userGUID || user?.userGuid || "";
}

export function useStaffNoticeTemplates(enabled = true) {
  const actorKey = useActorKey();
  return useQuery({
    queryKey: ["staffNotices", actorKey, "templates"],
    enabled,
    queryFn: fetchStaffNoticeTemplates,
  });
}

/** 预检结果只在当下有效（谁刚登录、谁已在进行中），不缓存，每次打开和切换开关都重新问服务端。 */
export function useStaffNoticePreflight(request: StaffNoticePreflightRequest | null) {
  const actorKey = useActorKey();
  return useQuery({
    queryKey: ["staffNotices", actorKey, "preflight", request],
    enabled: Boolean(request?.templateKey && request.userGuids.length),
    queryFn: () => fetchStaffNoticePreflight(request!),
    gcTime: 0,
    staleTime: 0,
    retry: false,
    // 切开关时列表别闪空；旧结果只用来显示，发送按钮要等 isFetching 结束才放开。
    placeholderData: keepPreviousData,
  });
}

export function useStaffNoticePreview(templateKey: string | undefined, userGuid: string | undefined) {
  const actorKey = useActorKey();
  return useQuery({
    queryKey: ["staffNotices", actorKey, "preview", templateKey ?? "", userGuid ?? ""],
    enabled: Boolean(templateKey && userGuid),
    queryFn: () => fetchStaffNoticePreview(templateKey!, userGuid!),
    gcTime: 0,
    retry: false,
  });
}

/** 批次详情；`poll` 打开时每 1.5 秒轮询，批次结束自动停。组件卸载（关掉弹层）即停止轮询，后台发送不受影响。 */
export function useStaffNoticeBatch(batchId: string | null, { poll }: { poll: boolean }) {
  const actorKey = useActorKey();
  return useQuery({
    queryKey: ["staffNotices", actorKey, "batch", batchId ?? ""],
    enabled: Boolean(batchId),
    queryFn: () => fetchStaffNoticeBatch(batchId!),
    refetchInterval: (query) => (poll && !isBatchFinished(query.state.data) ? STAFF_NOTICE_POLL_MS : false),
    // 断网时轮询失败不要把已拿到的进度清掉，页面显示「连接中断」并继续重试。
    retry: false,
  });
}

export function useStaffNoticeBatches() {
  const actorKey = useActorKey();
  return useInfiniteQuery({
    queryKey: ["staffNotices", actorKey, "batches"],
    initialPageParam: 1,
    queryFn: ({ pageParam }) => fetchStaffNoticeBatches(pageParam, RECORDS_PAGE_SIZE),
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
  });
}

export function useStaffNoticeSend() {
  const queryClient = useQueryClient();
  const actorKey = useActorKey();
  return useMutation({
    mutationFn: (request: StaffNoticeSendRequest) => sendStaffNotice(request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["staffNotices", actorKey, "batches"] }),
  });
}

export function useStaffNoticeRetry() {
  const queryClient = useQueryClient();
  const actorKey = useActorKey();
  return useMutation({
    mutationFn: (batchId: string) => retryStaffNoticeBatch(batchId),
    onSuccess: (detail) => {
      queryClient.setQueryData(["staffNotices", actorKey, "batch", detail.id], detail);
      return queryClient.invalidateQueries({ queryKey: ["staffNotices", actorKey, "batches"] });
    },
  });
}

/** 批次结束后刷新人员列表与详情：员工变成「待首次登录」，标签要跟着变。 */
export function useRefreshPeopleAfterNotice() {
  const queryClient = useQueryClient();
  const actorKey = useActorKey();
  return useCallback(() => Promise.all([
    queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
    queryClient.invalidateQueries({ queryKey: ["storeUserDetail"] }),
    queryClient.invalidateQueries({ queryKey: ["storeUserProfile"] }),
    queryClient.invalidateQueries({ queryKey: ["staffNotices", actorKey, "batches"] }),
  ]), [actorKey, queryClient]);
}
