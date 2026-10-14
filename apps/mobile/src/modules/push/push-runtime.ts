// 系统推送的运行时接线：把核心逻辑（push-*-core.ts）与真实依赖（登录状态、apiClient、react-query、路由、原生适配层）接在一起。
//
// 进程内的全部状态都在内存里，不落盘。这个文件被 app/(shell)/_layout.tsx 间接引入，因此只在进入主界面后才会加载；
// 加载时把「注销推送设备」注册到退出登录钩子（见 push-logout-hook.ts）。
//
// 重要：expo-notifications 只在 push-native.ts 里以动态 import 出现；本文件和调用方都不直接引用它，
// 所以旧安装包（没有推送原生模块）上这里的一切都只会得到 null 并静默跳过。
import * as Application from "expo-application";
import Constants from "expo-constants";
import { router } from "expo-router";
import { Platform } from "react-native";
import { faceHitUnreadQueryKey } from "@/modules/face-match/hit-unread";
import { buildFaceHubRoute } from "@/modules/navigation/face-hub";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import type { NotificationInfiniteData } from "@/modules/notifications/logic";
import { notificationKeys } from "@/modules/notifications/use-notifications";
import { getCurrentAppUpdateInfo } from "@/modules/updates/app-update-runtime";
import { queryClient } from "@/shared/api/query-client";
import { i18n } from "@/shared/i18n/i18n";
import { useAuthStore } from "@/store/auth-store";
import { pushDevicesApi } from "./push-api";
import { createPushEventController, type PushSessionPhase } from "./push-events-core";
import { registerPushLogoutHook } from "./push-logout-hook";
import { loadPushNative } from "./push-native";
import {
  PUSH_UPLOAD_TIMEOUT_MS,
  createPushRegistrationState,
  registerPushToken,
  type PushRegistrationDeps,
  type PushRegistrationResult,
} from "./push-registration-core";
import type { PushTarget } from "./push-routing";
import { PUSH_UNREGISTER_TIMEOUT_MS, unregisterPushToken, type PushUnregisterDeps } from "./push-unregister-core";
import type { PushNative, PushPlatform, PushSessionSnapshot } from "./push-types";

const registrationState = createPushRegistrationState();

function currentPlatform(): PushPlatform | null {
  return Platform.OS === "ios" ? "ios" : Platform.OS === "android" ? "android" : null;
}

function loadNative(): Promise<PushNative | null> {
  return loadPushNative({
    platformOS: Platform.OS,
    // Android 系统设置里「通知类别」显示的名称；必须在加载时取，避免 i18n 尚未初始化时被缓存成键名。
    getChannelName: () => i18n.t("push:channelName"),
  });
}

/** 只有个人账号会话（sessionKind === "account"）才注册推送：设备会话 / 设备账号会话是门店共用设备，不绑定个人。 */
function getSession(): PushSessionSnapshot {
  const auth = useAuthStore.getState();
  const iosReview = isIosReviewSessionActive() || auth.iosReviewOfflineGuardActive || auth.sessionKind === "iosReview";
  const accountGuid = auth.isAuthenticated && auth.sessionKind === "account" ? auth.user?.userGUID || null : null;
  return { iosReview, accountGuid };
}

function readProjectId(): string | null {
  const fromExpoConfig = Constants.expoConfig?.extra?.eas?.projectId;
  const fromEasConfig = Constants.easConfig?.projectId;
  const projectId = typeof fromExpoConfig === "string" && fromExpoConfig ? fromExpoConfig : fromEasConfig;
  return typeof projectId === "string" && projectId ? projectId : null;
}

const registrationDeps: PushRegistrationDeps = {
  state: registrationState,
  get platform() {
    return currentPlatform();
  },
  get projectId() {
    return readProjectId();
  },
  now: () => Date.now(),
  getSession,
  loadNative,
  uploadToken: (payload, accountGuid) => pushDevicesApi.register(payload, accountGuid, { timeoutMs: PUSH_UPLOAD_TIMEOUT_MS }),
  getAppInfo: () => {
    const info = getCurrentAppUpdateInfo();
    return {
      appVersion: Application.nativeApplicationVersion || info.appVersion,
      runtimeVersion: info.runtimeVersion,
    };
  },
};

const unregisterDeps: PushUnregisterDeps = {
  state: registrationState,
  get projectId() {
    return readProjectId();
  },
  now: () => Date.now(),
  getSession,
  loadNative,
  deleteToken: (token, accountGuid, signal) =>
    pushDevicesApi.unregister(token, accountGuid, { signal, timeoutMs: PUSH_UNREGISTER_TIMEOUT_MS }),
};

/** 登录后进入主界面、应用回到前台时调用；带去重与节流，永不抛错。 */
export function registerPushForCurrentSession(): Promise<PushRegistrationResult> {
  return registerPushToken({ deps: registrationDeps });
}

// ───────────── 收到 / 点击推送 ─────────────

let phase: PushSessionPhase = "signed-out";

/** 前台收到推送后刷新未读数：只标记 / 重取，与推送内容无关。收件箱先截到第一页再重取，避免把滚过的所有分页逐页重发。 */
function refreshUnreadCounts(): void {
  const userGuid = useAuthStore.getState().user?.userGUID ?? null;
  if (!userGuid) return;
  void queryClient.invalidateQueries({ queryKey: faceHitUnreadQueryKey(userGuid) });
  const inboxKey = notificationKeys.list(userGuid);
  queryClient.setQueryData<NotificationInfiniteData>(inboxKey, (data) =>
    data ? { pages: data.pages.slice(0, 1), pageParams: data.pageParams.slice(0, 1) } : data,
  );
  void queryClient.invalidateQueries({ queryKey: inboxKey });
}

function navigateToPushTarget(target: PushTarget): void {
  if (target.kind === "face-hub") router.push(buildFaceHubRoute(target.params));
  else router.push("/(shell)/notifications");
}

const eventController = createPushEventController({
  getPhase: () => phase,
  refreshUnreadCounts,
  navigate: navigateToPushTarget,
  clearLastResponse: () => {
    void loadNative().then((native) => native?.clearLastResponse());
  },
  now: () => Date.now(),
});

/** 主界面的会话阶段：signed-out（无个人账号会话）/ busy（已登录但主界面未就绪）/ ready。 */
export function setPushSessionPhase(next: PushSessionPhase): void {
  phase = next;
  if (next === "ready") eventController.flushPending();
  if (next === "signed-out") eventController.reset();
}

let startPromise: Promise<void> | null = null;

/** 距离进程启动的毫秒数（RN 的 performance.now 自 JS 运行时创建起算）；取不到时按 0，即视为「刚启动」。 */
function msSinceProcessStart(): number {
  return typeof performance !== "undefined" && typeof performance.now === "function" ? performance.now() : 0;
}

/**
 * 安装前台策略与点击监听（整个进程只装一次），并读取冷启动时的「最近一次点击」。
 * 监听器一直保留到进程结束，由事件控制器按会话阶段决定处理还是丢弃（登出后点到旧推送会被丢弃）。
 */
export function startPushEvents(): Promise<void> {
  // 条件不满足时不占用「只装一次」的名额：之后满足时仍可安装。
  const session = getSession();
  if (session.iosReview || !session.accountGuid) return Promise.resolve();
  startPromise ??= (async () => {
    const native = await loadNative();
    if (!native || !native.isPhysicalDevice) return;
    native.installForegroundHandler(() => eventController.onForegroundNotification());
    native.addResponseListener((response) => eventController.handleResponse(response));
    const last = native.getLastResponse();
    if (last) eventController.handleColdStartResponse(last, msSinceProcessStart());
  })().catch(() => undefined);
  return startPromise;
}

// ───────────── 退出登录 ─────────────

/** 退出登录时尽力注销本机推送令牌（最长 3 秒，失败吞掉）；由 auth-store 的 logout 通过钩子调用。 */
async function unregisterOnLogout(): Promise<void> {
  await unregisterPushToken({ deps: unregisterDeps });
}

registerPushLogoutHook(unregisterOnLogout);
