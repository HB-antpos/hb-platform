// expo-notifications / expo-device 的适配层，也是整个 App 里唯一允许出现这两个模块名的文件，并且只能以动态 import() 出现。
//
// 为什么必须动态加载：推送要求新的原生模块，但同一份 JS 会通过 OTA 下发给还没升级原生包的旧安装包。
// 静态 import 在旧包上会因为原生模块不存在而在启动阶段直接崩溃；动态 import 包在 try/catch 里，
// 旧包（以及 Node 测试环境、部分受限环境）只会得到 null，推送整体静默不工作，App 其余功能不受影响。
//
// 本文件不写日志、不落盘；原始通知对象不会越过这里（只向外交付归一化后的 PushResponse）。
import {
  PUSH_ANDROID_CHANNEL_ID,
  PUSH_FOREGROUND_BEHAVIOR,
  type PushNative,
  type PushPermissionStatus,
  type PushResponse,
} from "./push-types";

type NotificationsModule = typeof import("expo-notifications");
type DeviceModule = Pick<typeof import("expo-device"), "isDevice">;

export interface PushNativeOptions {
  /** react-native 的 Platform.OS。 */
  platformOS: string;
  /** Android 通知渠道在系统设置里显示的名称（走 i18n）。 */
  getChannelName: () => string;
}

function mapPermission(response: { status?: string; granted?: boolean }): PushPermissionStatus {
  if (response.granted === true || response.status === "granted") return "granted";
  if (response.status === "undetermined") return "undetermined";
  return "denied";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** 把原生交付的点击对象归一化；原始对象（含标题、正文）到此为止。 */
function toPushResponse(raw: import("expo-notifications").NotificationResponse, defaultAction: string): PushResponse {
  const request = raw.notification?.request;
  const rawData = request?.content?.data;
  const data = isRecord(rawData) ? rawData : {};
  return {
    id: typeof request?.identifier === "string" && request.identifier ? request.identifier : null,
    isDefaultAction: raw.actionIdentifier === defaultAction,
    type: typeof data.type === "string" ? data.type : null,
    data,
  };
}

export function createPushNative(notifications: NotificationsModule, device: DeviceModule, options: PushNativeOptions): PushNative {
  const defaultAction = notifications.DEFAULT_ACTION_IDENTIFIER;
  return {
    isPhysicalDevice: device.isDevice === true,

    async ensureAndroidChannel() {
      if (options.platformOS !== "android") return;
      // 服务端发的推送带 channelId: "default"，渠道不存在时 Android 8+ 会静默丢弃展示。
      await notifications.setNotificationChannelAsync(PUSH_ANDROID_CHANNEL_ID, {
        name: options.getChannelName(),
        importance: notifications.AndroidImportance.HIGH,
      });
    },

    async getPermissionStatus() {
      return mapPermission(await notifications.getPermissionsAsync());
    },

    async requestPermission() {
      return mapPermission(await notifications.requestPermissionsAsync());
    },

    async getExpoPushToken(projectId) {
      const token = await notifications.getExpoPushTokenAsync({ projectId });
      return token.data;
    },

    installForegroundHandler(onForeground) {
      notifications.setNotificationHandler({
        handleNotification: async () => {
          try {
            onForeground();
          } catch {
            // 刷新失败不影响展示策略
          }
          return { ...PUSH_FOREGROUND_BEHAVIOR };
        },
      });
    },

    addResponseListener(listener) {
      const subscription = notifications.addNotificationResponseReceivedListener((raw) => listener(toPushResponse(raw, defaultAction)));
      return { remove: () => subscription.remove() };
    },

    getLastResponse() {
      try {
        // getLastNotificationResponseAsync 在当前版本已标记废弃，同步版本语义相同。
        const raw = notifications.getLastNotificationResponse();
        return raw ? toPushResponse(raw, defaultAction) : null;
      } catch {
        return null;
      }
    },

    clearLastResponse() {
      try {
        notifications.clearLastNotificationResponse();
      } catch {
        // 原生模块不支持时忽略
      }
    },
  };
}

let loading: Promise<PushNative | null> | null = null;

/**
 * 加载原生推送能力；当前安装包没有对应原生模块时返回 null（不抛错）。
 * 结果在进程内缓存：原生模块有没有不会在运行中改变，失败也不重复尝试。
 */
export function loadPushNative(options: PushNativeOptions): Promise<PushNative | null> {
  loading ??= (async () => {
    try {
      const [notifications, device] = await Promise.all([import("expo-notifications"), import("expo-device")]);
      return createPushNative(notifications, device, options);
    } catch {
      return null;
    }
  })();
  return loading;
}

/** 仅供测试：清掉进程内缓存。 */
export function __resetPushNativeForTests(): void {
  loading = null;
}
