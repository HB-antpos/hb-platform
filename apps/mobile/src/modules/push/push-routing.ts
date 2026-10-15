// 点击推送后的去向：把推送 data.type 映射成路由目标。纯函数，不依赖 React Native。
//
// 规则：
// - face.unwelcome-hit（疑似命中，需人工复核）→ 「人脸比对」合并入口的「名单」页签 · 命中复核；
// - face.unwelcome-expiring（名单条目将到期）→ 「名单」页签 · 名单条目；
// - 其它 face.* → 合并入口（不指定页签，由合并页按各页签权限退回第一个可见页签）；
// - 其它类型（approval.* / store.* / container.* / system.* / 将来新增的）与缺字段 → 通知收件箱。
// 本函数看不到权限：目标页签没有权限时，合并页自己的 resolveFaceHubActiveTab 会兜底到第一个可见页签，
// 没有任何页签权限则显示既有的「无权限」提示，不会在这里做第二套权限判断。
import type { FaceHubParams } from "../navigation/face-hub";

/** 后端 AppNotificationTypes.FaceUnwelcomeHit / FaceUnwelcomeExpiring。 */
export const PUSH_TYPE_FACE_UNWELCOME_HIT = "face.unwelcome-hit";
export const PUSH_TYPE_FACE_UNWELCOME_EXPIRING = "face.unwelcome-expiring";

export type PushTarget =
  | { kind: "face-hub"; params: FaceHubParams }
  | { kind: "notifications" };

const INBOX: PushTarget = { kind: "notifications" };

/**
 * @param type 推送 data.type（敏感类型的推送 data 只有 notificationId 与 type 两个字段）
 * @param _data 预留给「其它类型多带的跳转标识」；当前所有类型都只靠 type 决定去向，不读取 data 内容
 */
export function resolvePushTarget(type: unknown, _data?: unknown): PushTarget {
  if (typeof type !== "string") return INBOX;
  const normalized = type.trim();
  if (!normalized) return INBOX;
  if (normalized === PUSH_TYPE_FACE_UNWELCOME_HIT) return { kind: "face-hub", params: { tab: "list", sub: "review" } };
  if (normalized === PUSH_TYPE_FACE_UNWELCOME_EXPIRING) return { kind: "face-hub", params: { tab: "list", sub: "entries" } };
  if (normalized.startsWith("face.")) return { kind: "face-hub", params: {} };
  return INBOX;
}
