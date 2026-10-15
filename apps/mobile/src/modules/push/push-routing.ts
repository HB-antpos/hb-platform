// 点击推送后的去向：把推送 data.type 映射成路由目标。纯函数，不依赖 React Native。
//
// 规则：
// - face.unwelcome-hit（疑似命中）→ 「人脸比对」合并入口的「抓拍」页签 · 黑名单 · 未读命中，与通知收件箱点同一条通知的去向一致
//   （没有抓拍页签权限时，合并页退回「名单」页签，sub=review 让它落在「命中复核」）。
//   不能直接去「命中复核 · 待复核」：高相似度命中会在提示发出后被系统自动确认（FaceMatchMaintenance.AutoConfirmHitsAsync），
//   用户点开推送时待复核往往已是空的，刚推送的那条在「已确认」里；「未读命中」按通知是否已读列出，不论它此刻是什么复核状态都能看到；
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
 * 疑似命中推送的落点参数。本函数看不到权限，所以把两种落点一起带上，交给合并页按权限选：
 * 有抓拍页签 → tab/filter/hitStatus 生效（黑名单 · 未读）；没有 → 退回名单页签，sub=review 生效（命中复核）。
 * 与 notifications/logic.ts 的 resolveNotificationTarget 在各权限组合下的最终落点一致（有测试守着）。
 */
export const FACE_HIT_PUSH_PARAMS: FaceHubParams = Object.freeze({
  tab: "capture",
  filter: "blacklist",
  hitStatus: "unread",
  sub: "review",
});

/**
 * @param type 推送 data.type（敏感类型的推送 data 只有 notificationId 与 type 两个字段）
 * @param _data 预留给「其它类型多带的跳转标识」；当前所有类型都只靠 type 决定去向，不读取 data 内容
 */
export function resolvePushTarget(type: unknown, _data?: unknown): PushTarget {
  if (typeof type !== "string") return INBOX;
  const normalized = type.trim();
  if (!normalized) return INBOX;
  if (normalized === PUSH_TYPE_FACE_UNWELCOME_HIT) return { kind: "face-hub", params: FACE_HIT_PUSH_PARAMS };
  if (normalized === PUSH_TYPE_FACE_UNWELCOME_EXPIRING) return { kind: "face-hub", params: { tab: "list", sub: "entries" } };
  if (normalized.startsWith("face.")) return { kind: "face-hub", params: {} };
  return INBOX;
}
