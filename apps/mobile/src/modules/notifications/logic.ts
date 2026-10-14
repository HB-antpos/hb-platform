// 通知收件箱的纯逻辑：类型 → 跳转判定、已读状态写回缓存、时间展示、分页合并。不依赖 React Native，可在 Node 下测试。
import type { InfiniteData } from "@tanstack/react-query";
import type { NotificationItem, NotificationPage } from "./types";

export const NOTIFICATION_PAGE_SIZE = 20;
/** 后端 AppNotificationTypes.FaceUnwelcomeHit：不受欢迎人员名单疑似命中（需人工复核）。 */
export const NOTIFICATION_TYPE_FACE_UNWELCOME_HIT = "face.unwelcome-hit";
/** 后端 AppNotificationPriorities.High：需要立即注意。 */
export const NOTIFICATION_PRIORITY_HIGH = 1;

export function isUnread(item: Pick<NotificationItem, "readAtUtc">): boolean {
  return item.readAtUtc === null;
}

export function isHighPriority(item: Pick<NotificationItem, "priority">): boolean {
  return item.priority >= NOTIFICATION_PRIORITY_HIGH;
}

export type NotificationTarget = { kind: "route"; routeName: "face-unwelcome" } | { kind: "none" };

/**
 * 点击后的去向：疑似命中通知进「不受欢迎人员名单」页（名单页没有按任务定位的入口，进列表即可；Data 里的 taskId 暂不使用），
 * 账号打不开该页（没有名单权限）就不跳转；其他类型（含将来新增的）一律是未知类型，只标已读、不跳转。
 */
export function resolveNotificationTarget(item: Pick<NotificationItem, "type">, canOpenUnwelcome: boolean): NotificationTarget {
  if (item.type === NOTIFICATION_TYPE_FACE_UNWELCOME_HIT && canOpenUnwelcome) {
    return { kind: "route", routeName: "face-unwelcome" };
  }
  return { kind: "none" };
}

// ───────────── 已读状态写回缓存 ─────────────

/** 收件箱列表的 react-query 缓存形状：页参数是游标（第一页为 null）。 */
export type NotificationInfiniteData = InfiniteData<NotificationPage, string | null>;

/** 把指定通知标成已读并刷新各页的未读总数（以服务端返回为准）；已读的保留原已读时间。 */
export function applyMarkRead(
  data: NotificationInfiniteData | undefined,
  ids: readonly string[],
  unreadCount: number,
  nowIso: string,
): NotificationInfiniteData | undefined {
  if (!data) return data;
  const target = new Set(ids);
  return {
    ...data,
    pages: data.pages.map((page) => ({
      ...page,
      unreadCount,
      items: page.items.map((item) => (target.has(item.id) && item.readAtUtc === null ? { ...item, readAtUtc: nowIso } : item)),
    })),
  };
}

/** 全部已读：已加载的各页全部标成已读，未读总数取服务端返回。 */
export function applyMarkAllRead(
  data: NotificationInfiniteData | undefined,
  unreadCount: number,
  nowIso: string,
): NotificationInfiniteData | undefined {
  if (!data) return data;
  return {
    ...data,
    pages: data.pages.map((page) => ({
      ...page,
      unreadCount,
      items: page.items.map((item) => (item.readAtUtc === null ? { ...item, readAtUtc: nowIso } : item)),
    })),
  };
}

/** 合并分页结果并按通知编号去重（翻页期间有新通知写入时，游标分页本身不会重复，这里只是第二道防线）。 */
export function mergeNotificationPages(pages: readonly Pick<NotificationPage, "items">[]): NotificationItem[] {
  const seen = new Set<string>();
  const merged: NotificationItem[] = [];
  for (const page of pages) {
    for (const item of page.items) {
      if (seen.has(item.id)) continue;
      seen.add(item.id);
      merged.push(item);
    }
  }
  return merged;
}

/** 当前未读数：以最新一页的服务端未读总数为准；还没加载时为 0。 */
export function currentUnreadCount(pages: readonly Pick<NotificationPage, "unreadCount">[] | undefined): number {
  return pages && pages.length > 0 ? pages[0].unreadCount : 0;
}

// ───────────── 时间展示（手机本地时区） ─────────────

export type NotificationTime = { kind: "time"; text: string } | { kind: "yesterday" } | { kind: "date"; text: string };

function pad2(value: number): string {
  return String(value).padStart(2, "0");
}

/** 当天显示 HH:mm、昨天显示「昨天」、更早显示 MM-dd（跨年带年份）；无效时间返回 null。 */
export function describeNotificationTime(utcIso: string, now: Date): NotificationTime | null {
  const date = new Date(utcIso);
  if (!utcIso || Number.isNaN(date.getTime())) return null;
  const dayStart = (value: Date) => new Date(value.getFullYear(), value.getMonth(), value.getDate()).getTime();
  const dayDiff = Math.round((dayStart(now) - dayStart(date)) / 86_400_000);
  if (dayDiff === 0) return { kind: "time", text: `${pad2(date.getHours())}:${pad2(date.getMinutes())}` };
  if (dayDiff === 1) return { kind: "yesterday" };
  const monthDay = `${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
  return { kind: "date", text: date.getFullYear() === now.getFullYear() ? monthDay : `${date.getFullYear()}-${monthDay}` };
}
