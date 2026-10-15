// 系统推送（Expo 推送服务）的共享类型。本文件只有类型与常量，不依赖 React Native，可在 Node 下测试。
//
// 隐私约定（整个 push 模块适用）：
// - 推送令牌、推送标题 / 正文、data 内容一律不写日志、不上报中心日志、不落盘、不进 react-query 缓存；
// - 失败原因只用下面的固定短码，不携带任何设备或账号信息。

/** 上报给后端的平台标识（后端契约：ios | android）。 */
export type PushPlatform = "ios" | "android";

/** 系统通知权限的三态（iOS 的临时授权 / 短暂授权也归为 granted）。 */
export type PushPermissionStatus = "granted" | "denied" | "undetermined";

/** 被点击的推送，已归一化：只保留路由所需字段，原始通知对象不会越过适配层。 */
export interface PushResponse {
  /** 通知请求标识，用于去重（冷启动读取与监听器可能给出同一次点击）；取不到为 null。 */
  id: string | null;
  /** 是否为「点击通知本身」（而不是关闭 / 自定义动作）。 */
  isDefaultAction: boolean;
  /** 推送 data.type；缺失或非字符串为 null。 */
  type: string | null;
  data: Record<string, unknown>;
}

/** 原生推送能力的最小接口。生产实现在 push-native.ts（动态加载 expo-notifications），测试用假实现。 */
export interface PushNative {
  /** 是否真机（模拟器 / 模拟机取不到有效令牌，直接跳过）。 */
  isPhysicalDevice: boolean;
  /** Android 创建 id 为 default 的通知渠道（重要性 HIGH，服务端发 channelId: "default"）；iOS 为空操作。 */
  ensureAndroidChannel(): Promise<void>;
  getPermissionStatus(): Promise<PushPermissionStatus>;
  /** 弹系统授权框（只在状态为 undetermined 时调用）。 */
  requestPermission(): Promise<PushPermissionStatus>;
  /** 取 Expo 推送令牌；无 Google Play 服务等情况会 reject。 */
  getExpoPushToken(projectId: string): Promise<string>;
  /** 前台策略：不弹横幅、不响铃、不改角标；每收到一条前台推送回调一次 onForeground（用于刷新未读数）。 */
  installForegroundHandler(onForeground: () => void): void;
  addResponseListener(listener: (response: PushResponse) => void): { remove(): void };
  /** 本次进程内最近一次被点击的推送（冷启动点击进入 App 时可取到）。 */
  getLastResponse(): PushResponse | null;
  clearLastResponse(): void;
}

/** 会话快照：由运行时从登录状态计算，核心逻辑只看这两个字段。 */
export interface PushSessionSnapshot {
  /** iOS 审核态（离线演示会话，或审核构建登录前的 fail-closed 守卫期）：必须零网络请求、不加载原生模块。 */
  iosReview: boolean;
  /** 已登录的个人账号会话（sessionKind === "account"）的账号编号；未登录、设备会话、设备账号会话为 null。 */
  accountGuid: string | null;
}

/** 注册 / 注销推送设备时统一的请求头：失败（含 404）不进中心日志，保持静默。 */
export const PUSH_REQUEST_HEADERS = { "X-Skip-Center-Log": "1" } as const;

/** 推送设备接口（apiClient 的 baseURL 已带 /api）。令牌只放请求体，绝不拼进 URL / 查询串。 */
export const PUSH_DEVICES_PATH = "/react/v1/notifications/push-devices";

/** 与服务端约定的 Android 通知渠道 id。 */
export const PUSH_ANDROID_CHANNEL_ID = "default";

/** 令牌形态：ExponentPushToken[...] 或 ExpoPushToken[...]。 */
export function isExpoPushToken(value: unknown): value is string {
  return typeof value === "string" && /^Expo(?:nent)?PushToken\[[^\]\s]+\]$/.test(value);
}

/** App 在前台时收到推送的展示策略：不弹系统横幅、不进通知中心列表、不响铃、不改角标，只触发未读数刷新。 */
export const PUSH_FOREGROUND_BEHAVIOR = Object.freeze({
  shouldShowBanner: false,
  shouldShowList: false,
  shouldPlaySound: false,
  shouldSetBadge: false,
} as const);
