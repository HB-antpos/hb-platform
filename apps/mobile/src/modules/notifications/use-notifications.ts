// 通知收件箱的数据钩子（react-query）。查询键带账号编号：切换账号后不会读到上一个账号的缓存（登出时全局缓存也会清空）。
// 本期只做拉取：下拉刷新、回前台刷新、点击即标已读；没有 SignalR 与系统推送。
import { useMemo } from "react";
import { useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useAuthStore } from "@/store/auth-store";
import { notificationsApi } from "./api";
import {
  NOTIFICATION_PAGE_SIZE,
  applyMarkAllRead,
  applyMarkRead,
  currentUnreadCount,
  mergeNotificationPages,
  type NotificationInfiniteData,
} from "./logic";
import type { NotificationItem } from "./types";

export const notificationKeys = {
  all: ["notifications"] as const,
  list: (userGuid: string | null) => ["notifications", userGuid, "list"] as const,
};

/** 收件箱列表（游标分页）；refresh 先把缓存截到第一页再刷新，只重取一页，不会把滚过的所有分页逐页重发。 */
export function useNotificationInbox(enabled: boolean) {
  const queryClient = useQueryClient();
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const queryKey = notificationKeys.list(userGuid);
  const query = useInfiniteQuery({
    queryKey,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) => notificationsApi.list({ cursor: pageParam, take: NOTIFICATION_PAGE_SIZE }, signal),
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    enabled: enabled && Boolean(userGuid),
    staleTime: 15_000,
    // 4xx 是确定的结论，不重试；网络与 5xx 重试一次。
    retry: (failureCount, error) => {
      const status = (error as { response?: { status?: number } } | null)?.response?.status;
      if (typeof status === "number" && status >= 400 && status < 500) return false;
      return failureCount < 1;
    },
  });
  const pages = query.data?.pages;
  const items = useMemo<NotificationItem[]>(() => (pages ? mergeNotificationPages(pages) : []), [pages]);
  const refresh = async () => {
    queryClient.setQueryData<NotificationInfiniteData>(queryKey, (data) =>
      data ? { pages: data.pages.slice(0, 1), pageParams: data.pageParams.slice(0, 1) } : data,
    );
    await query.refetch();
  };
  return { query, items, unreadCount: currentUnreadCount(pages), refresh };
}

/** 标记指定通知已读，成功后把已读状态与最新未读总数写回缓存（不整页重取）。 */
export function useMarkNotificationsRead() {
  const queryClient = useQueryClient();
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useMutation({
    mutationFn: (ids: readonly string[]) => notificationsApi.markRead(ids),
    onSuccess: (result, ids) => {
      queryClient.setQueryData<NotificationInfiniteData>(notificationKeys.list(userGuid), (data) =>
        applyMarkRead(data, ids, result.unreadCount, new Date().toISOString()),
      );
    },
  });
}

export function useMarkAllNotificationsRead() {
  const queryClient = useQueryClient();
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useMutation({
    mutationFn: () => notificationsApi.markAllRead(),
    onSuccess: (result) => {
      queryClient.setQueryData<NotificationInfiniteData>(notificationKeys.list(userGuid), (data) =>
        applyMarkAllRead(data, result.unreadCount, new Date().toISOString()),
      );
    },
  });
}
