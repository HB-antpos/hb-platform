// 站内通知收件箱类型。契约来自后端 GET/POST /api/react/v1/notifications（NotificationContracts.cs），字段 camelCase。
// 标题与正文是后端固定的英文文案，原样展示，不翻译；Data 只放跳转用的标识（taskId / storeCode），不含敏感内容。

export interface NotificationItem {
  id: string;
  /** 业务类型，如 face.unwelcome-hit；客户端不认识的类型按未知类型兜底展示。 */
  type: string;
  /** 0 普通、1 高优先级。 */
  priority: number;
  title: string;
  body: string;
  data: Record<string, string>;
  storeCode: string | null;
  createdAtUtc: string;
  /** null = 未读。 */
  readAtUtc: string | null;
}

export interface NotificationPage {
  items: NotificationItem[];
  /** 下一页游标；null 表示没有更多。 */
  nextCursor: string | null;
  /** 服务端口径的未读总数（不是本页未读数）。 */
  unreadCount: number;
}

export interface NotificationMarkReadResult {
  updated: number;
  unreadCount: number;
}

export interface NotificationListQuery {
  cursor?: string | null;
  take?: number;
  unreadOnly?: boolean;
}
