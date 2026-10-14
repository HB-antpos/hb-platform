// 「黑名单命中」未读数（红色数字）的共享约定：缓存键与写回函数。
// - 取数在 app/(shell)/_layout.tsx 的待办角标查询里（GET /notifications/unread-count?type=face.unwelcome-hit）；
// - 标记已读（点开一条命中、「全部标为已读」）后，用接口返回的 unreadCount 直接写回这个缓存，导航徽标立刻变化，不必再取一次。
// 数字只代表「当前登录人」自己还没看的命中通知：缓存键带账号编号，切换账号不会读到上一个账号的数。
import type { QueryClient } from "@tanstack/react-query";

/** 命中通知的类型（与后端 AppNotificationTypes.FaceUnwelcomeHit 一致）。 */
export const FACE_HIT_NOTIFICATION_TYPE = "face.unwelcome-hit";

export const faceHitUnreadQueryKey = (userGuid: string | null | undefined) =>
  ["faceMatch", userGuid ?? null, "hitUnreadCount"] as const;

/** 把接口返回的「该类型剩余未读数」写回缓存；非法值（负数、NaN）一律按 0。 */
export function writeFaceHitUnreadCount(queryClient: QueryClient, userGuid: string | null | undefined, count: number): void {
  const safe = Number.isFinite(count) && count > 0 ? Math.trunc(count) : 0;
  queryClient.setQueryData(faceHitUnreadQueryKey(userGuid), safe);
}
