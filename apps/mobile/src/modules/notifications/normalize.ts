// 通知接口响应的防御式规范化：字段缺失 / 类型漂移时界面不崩，缺失的编号与类型按空处理。
import type { NotificationItem, NotificationMarkReadResult, NotificationPage } from "./types";

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : {};
}

function asString(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function asNullableString(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value : null;
}

function asNumber(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

/** Data 只保留字符串值（后端是 string→string 字典），其余类型丢弃。 */
function asStringMap(value: unknown): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, entry] of Object.entries(asRecord(value))) {
    if (typeof entry === "string") result[key] = entry;
  }
  return result;
}

export function normalizeNotification(value: unknown): NotificationItem {
  const raw = asRecord(value);
  return {
    id: asString(raw.id),
    type: asString(raw.type),
    priority: asNumber(raw.priority),
    title: asString(raw.title),
    body: asString(raw.body),
    data: asStringMap(raw.data),
    storeCode: asNullableString(raw.storeCode),
    createdAtUtc: asString(raw.createdAtUtc),
    readAtUtc: asNullableString(raw.readAtUtc),
  };
}

export function normalizeNotificationPage(value: unknown): NotificationPage {
  const raw = asRecord(value);
  return {
    // 没有编号的条目既无法标记已读也无法作列表 key，直接丢弃。
    items: (Array.isArray(raw.items) ? raw.items : []).map(normalizeNotification).filter((item) => item.id !== ""),
    nextCursor: asNullableString(raw.nextCursor),
    unreadCount: Math.max(0, asNumber(raw.unreadCount)),
  };
}

export function normalizeMarkReadResult(value: unknown): NotificationMarkReadResult {
  const raw = asRecord(value);
  return { updated: Math.max(0, asNumber(raw.updated)), unreadCount: Math.max(0, asNumber(raw.unreadCount)) };
}

/** GET /unread-count 的 data 是一个整数。 */
export function normalizeUnreadCount(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? Math.max(0, Math.trunc(value)) : 0;
}
