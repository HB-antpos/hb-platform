// 生产用的接口实例：把 apiClient 注入 createNotificationsApi（apiClient 带登录态、基础地址与 401 刷新）。
// 逻辑都在 api-core.ts，那里不依赖 React Native，可以在 Node 下用假客户端测试。
import { apiClient } from "@/shared/api/client";
import { createNotificationsApi, type NotificationsHttp } from "./api-core";

export const notificationsApi = createNotificationsApi(apiClient as unknown as NotificationsHttp);

/** 某一类通知的未读数（GET /notifications/unread-count?type=）；type 缺省 = 全部。 */
export const getUnreadCount = (type?: string, signal?: AbortSignal): Promise<number> => notificationsApi.unreadCount(type, signal);
