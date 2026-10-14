// 系统推送令牌的取得与上报（依赖全部由外部注入：生产见 push-runtime.ts，测试用假实现）。不依赖 React Native。
//
// 设计要点：
// 1. 这个 APK 会同时装在员工手机（有 Google Play 服务）和 PDA（没有）上，旧安装包 / 模拟器 / iOS 审核态也都会走到这里。
//    所有「做不到」的情况一律静默跳过：不抛错、不弹提示、不写日志，只返回固定短码；
// 2. 取令牌失败（无 GMS、离线等）统一记为 unavailable，只在内存里记次数与时间：
//    同一次进程最多再试 1 次（首次 + 1 次重试），且重试须距上次失败 ≥ 1 小时，PDA 不会被反复折腾；
// 3. 先取令牌、再弹系统授权框：没有 GMS 的 PDA 在取令牌这一步就失败，永远不会看到授权框；
// 4. 同一账号同一令牌在一个进程内只上报一次（内存去重，不落盘）；上报失败（含后端尚未上线的 404）静默，下次回到前台再试；
// 5. 令牌、推送内容不写日志、不落盘；失败原因只用 PushSkipReason 里的固定短码。
import type { PushDevicePayload } from "./push-api-core";
import { withTimeout } from "./push-async";
import {
  isExpoPushToken,
  type PushNative,
  type PushPermissionStatus,
  type PushPlatform,
  type PushSessionSnapshot,
} from "./push-types";

/** 取令牌失败后，下一次尝试至少相隔多久。 */
export const PUSH_UNAVAILABLE_RETRY_AFTER_MS = 60 * 60 * 1000;
/** 取令牌失败的次数上限（首次 + 最多再试 1 次），达到后本进程内不再尝试。 */
export const PUSH_MAX_UNAVAILABLE_ATTEMPTS = 2;
/** 取令牌（含 FCM / APNs 注册与访问 Expo 服务）的超时；超时按 unavailable 处理，避免原生调用悬挂卡住后续尝试。 */
export const PUSH_TOKEN_TIMEOUT_MS = 20_000;
/** 上报令牌的整体超时。 */
export const PUSH_UPLOAD_TIMEOUT_MS = 15_000;

/** 跳过原因（固定短码，只用于内存里的状态与测试断言）。 */
export type PushSkipReason =
  | "ios-review" // iOS 审核态：零网络请求、不加载原生模块
  | "not-signed-in" // 未登录，或不是个人账号会话（设备会话 / 设备账号会话）
  | "unsupported-platform" // 非 iOS / Android
  | "already-registered" // 同账号同令牌本进程已上报
  | "in-flight" // 已有一次注册在进行
  | "throttled" // 取令牌失败后 1 小时内不重试
  | "unavailable" // 取令牌失败（无 GMS 等）；达到次数上限后一直保持
  | "native-module-missing" // 当前安装包没有推送原生模块
  | "not-device" // 模拟器 / 模拟机
  | "no-project-id" // 配置里没有 EAS 项目 ID
  | "permission-denied" // 系统通知权限未授予
  | "session-changed" // 异步期间会话变了（登出、切换账号、进入审核态）
  | "upload-failed"; // 上报失败（含 404、超时）

export type PushRegistrationResult = { status: "registered" } | { status: "skipped"; reason: PushSkipReason };

/** 本进程内的推送注册状态：只在内存里，进程结束即丢弃，从不落盘。 */
export interface PushRegistrationState {
  unavailableCount: number;
  lastUnavailableAtMs: number | null;
  /** 已成功上报的账号 + 令牌（内存去重）。 */
  uploaded: { accountGuid: string; token: string } | null;
  /** 已取到并确认授权的令牌，供上报重试与退出登录时注销使用（不必再访问 Expo 服务）。 */
  held: { accountGuid: string; token: string } | null;
  inFlight: Promise<PushRegistrationResult> | null;
  /** 注销时 +1：注册过程中如果发生了注销，完成后不得再写入 uploaded / held。 */
  epoch: number;
  /** 最近一次结果的短码（仅供排查，不含令牌与账号信息）。 */
  lastResult: string | null;
}

export function createPushRegistrationState(): PushRegistrationState {
  return { unavailableCount: 0, lastUnavailableAtMs: null, uploaded: null, held: null, inFlight: null, epoch: 0, lastResult: null };
}

/** 取令牌失败后的闸门：达到次数上限 → unavailable；未满 1 小时 → throttled；否则放行。 */
export function checkUnavailableGate(state: PushRegistrationState, nowMs: number): "unavailable" | "throttled" | null {
  if (state.unavailableCount >= PUSH_MAX_UNAVAILABLE_ATTEMPTS) return "unavailable";
  if (state.lastUnavailableAtMs !== null && nowMs - state.lastUnavailableAtMs < PUSH_UNAVAILABLE_RETRY_AFTER_MS) return "throttled";
  return null;
}

export interface PushRegistrationDeps {
  state: PushRegistrationState;
  platform: PushPlatform | null;
  projectId: string | null;
  now(): number;
  getSession(): PushSessionSnapshot;
  /** 加载原生推送能力；当前安装包没有原生模块时返回 null（不抛错）。 */
  loadNative(): Promise<PushNative | null>;
  /** 上报令牌；失败抛出即可（由本函数静默处理）。 */
  uploadToken(payload: PushDevicePayload, accountGuid: string): Promise<void>;
  getAppInfo(): { appVersion?: string | null; runtimeVersion?: string | null };
  tokenTimeoutMs?: number;
  uploadTimeoutMs?: number;
}

const skipped = (reason: PushSkipReason): PushRegistrationResult => ({ status: "skipped", reason });

/**
 * 尝试取得推送令牌并上报后端。永远不抛错；并发调用时后到的直接返回 in-flight。
 * 调用时机：登录后进入主界面、应用回到前台。
 */
export async function registerPushToken({ deps }: { deps: PushRegistrationDeps }): Promise<PushRegistrationResult> {
  const { state } = deps;
  if (state.inFlight) return skipped("in-flight");
  const run = attempt(deps)
    .catch((): PushRegistrationResult => skipped("unavailable"))
    .then((result) => {
      state.lastResult = result.status === "registered" ? "registered" : result.reason;
      return result;
    });
  state.inFlight = run;
  try {
    return await run;
  } finally {
    if (state.inFlight === run) state.inFlight = null;
  }
}

async function attempt(deps: PushRegistrationDeps): Promise<PushRegistrationResult> {
  const { state } = deps;

  // 审核态与未登录必须最先判断：之后的任何一步都可能触网或加载原生模块。
  const session = deps.getSession();
  if (session.iosReview) return skipped("ios-review");
  const accountGuid = session.accountGuid;
  if (!accountGuid) return skipped("not-signed-in");
  const platform = deps.platform;
  if (!platform) return skipped("unsupported-platform");

  if (state.uploaded?.accountGuid === accountGuid) return skipped("already-registered");
  const gate = checkUnavailableGate(state, deps.now());
  if (gate) return skipped(gate);

  const stillSameSession = () => {
    const current = deps.getSession();
    return !current.iosReview && current.accountGuid === accountGuid;
  };
  const epoch = state.epoch;

  const native = await deps.loadNative();
  if (!native) return skipped("native-module-missing");
  if (!native.isPhysicalDevice) return skipped("not-device");
  if (!deps.projectId) return skipped("no-project-id");
  if (!stillSameSession()) return skipped("session-changed");

  let permission: PushPermissionStatus;
  try {
    permission = await native.getPermissionStatus();
  } catch {
    return markUnavailable(deps);
  }
  // 已被拒绝就不再反复弹（交给系统设置）；也不浪费一次取令牌。
  if (permission === "denied") return skipped("permission-denied");

  let token = state.held?.accountGuid === accountGuid ? state.held.token : null;
  if (!token) {
    // Android 13+ 要求先有通知渠道才会弹授权框；渠道创建失败不影响取令牌（系统会退回默认渠道）。
    try {
      await native.ensureAndroidChannel();
    } catch {
      // 忽略：渠道只影响展示方式，不影响能否收到推送
    }
    try {
      const fetched = await withTimeout(native.getExpoPushToken(deps.projectId), deps.tokenTimeoutMs ?? PUSH_TOKEN_TIMEOUT_MS);
      if (!isExpoPushToken(fetched)) return markUnavailable(deps);
      token = fetched;
    } catch {
      // 无 Google Play 服务（PDA）、离线、Expo 服务不可达等：一律归为 unavailable，静默
      return markUnavailable(deps);
    }
    if (!stillSameSession()) return skipped("session-changed");
  }

  if (permission === "undetermined") {
    // 只有「未决定」才弹系统授权框；不显示自定义弹窗。
    try {
      permission = await native.requestPermission();
    } catch {
      return skipped("permission-denied");
    }
  }
  if (permission !== "granted") return skipped("permission-denied");
  if (!stillSameSession()) return skipped("session-changed");

  // 取令牌成功：之前的失败记录作废。
  state.unavailableCount = 0;
  state.lastUnavailableAtMs = null;
  if (state.epoch === epoch) state.held = { accountGuid, token };

  const info = deps.getAppInfo();
  const payload: PushDevicePayload = { expoPushToken: token, platform };
  if (info.appVersion) payload.appVersion = info.appVersion;
  if (info.runtimeVersion) payload.runtimeVersion = info.runtimeVersion;
  try {
    await withTimeout(deps.uploadToken(payload, accountGuid), deps.uploadTimeoutMs ?? PUSH_UPLOAD_TIMEOUT_MS);
  } catch {
    // 后端未上线（404）、网络失败、超时、期间切换了账号：静默，下次回到前台再试
    return skipped("upload-failed");
  }
  if (state.epoch === epoch) state.uploaded = { accountGuid, token };
  return { status: "registered" };
}

function markUnavailable(deps: PushRegistrationDeps): PushRegistrationResult {
  deps.state.unavailableCount += 1;
  deps.state.lastUnavailableAtMs = deps.now();
  return skipped("unavailable");
}
