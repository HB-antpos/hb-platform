// 通知收件箱接口调用（HTTP 客户端由外部注入：生产用 apiClient，测试用假客户端抓取请求）。
// 本期只做拉取（列表、未读数、标记已读、全部已读），不做 SignalR 与系统推送。
// 通知只含固定英文文案与标识，不含照片等敏感内容，所以不需要 no-store；本文件不写日志。
import type { AxiosRequestConfig } from "axios";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { normalizeMarkReadResult, normalizeNotificationPage, normalizeUnreadCount } from "./normalize";
import type { NotificationListQuery, NotificationMarkReadResult, NotificationPage } from "./types";

/** apiClient 的 baseURL 已带 /api，业务路径与其他 mobile API 一致，不再加 /api 前缀。 */
export const NOTIFICATIONS_API_BASE = "/react/v1/notifications";
/** 后端一次最多标记 200 条（NotificationInboxService.MaxMarkReadIds），超出会 400；客户端分批发送。 */
export const MAX_MARK_READ_IDS = 200;

export interface NotificationsHttp {
  get(url: string, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
  post(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
}

export function createNotificationsApi(http: NotificationsHttp) {
  const data = (response: { data: unknown }) => unwrapApiEnvelope<unknown>(response.data);

  return {
    /** 分页参数是游标（cursor）+ take（≤ 50），不是页码；cursor 为空取第一页。 */
    async list(query: NotificationListQuery = {}, signal?: AbortSignal): Promise<NotificationPage> {
      const params = {
        cursor: query.cursor || undefined,
        take: query.take,
        unreadOnly: query.unreadOnly ? true : undefined,
      };
      return normalizeNotificationPage(data(await http.get(NOTIFICATIONS_API_BASE, { params, signal })));
    },

    async unreadCount(signal?: AbortSignal): Promise<number> {
      return normalizeUnreadCount(data(await http.get(`${NOTIFICATIONS_API_BASE}/unread-count`, { signal })));
    },

    /** 只会改到本人的未读记录；返回值里的 unreadCount 是最新的服务端未读总数。 */
    async markRead(ids: readonly string[]): Promise<NotificationMarkReadResult> {
      const unique = Array.from(new Set(ids.map((id) => id.trim()).filter(Boolean)));
      let updated = 0;
      let unreadCount = 0;
      for (let start = 0; start < unique.length; start += MAX_MARK_READ_IDS) {
        const batch = unique.slice(start, start + MAX_MARK_READ_IDS);
        const result = normalizeMarkReadResult(data(await http.post(`${NOTIFICATIONS_API_BASE}/read`, { ids: batch })));
        updated += result.updated;
        unreadCount = result.unreadCount;
      }
      return { updated, unreadCount };
    },

    async markAllRead(): Promise<NotificationMarkReadResult> {
      return normalizeMarkReadResult(data(await http.post(`${NOTIFICATIONS_API_BASE}/read-all`)));
    },
  };
}

export type NotificationsApi = ReturnType<typeof createNotificationsApi>;
