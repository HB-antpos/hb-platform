import { HbposApiError, isHbposNonApiResponse } from "../api/hbpos-api";
import type { DeviceSessionState } from "../security/device-session";

import type {
  RuntimeBackendState,
  RuntimeDeviceState,
} from "./pos-runtime";

export type StartupDeviceGateResult = Readonly<{
  backend: RuntimeBackendState;
  device: Exclude<RuntimeDeviceState, "unknown">;
}>;

export type StartupDeviceGateOptions = Readonly<{
  internetReachable: boolean;
  registrationResetPending?: boolean;
  readPendingDeviceActivation(): Promise<string | null>;
  verifyCurrentDevice(): Promise<DeviceSessionState>;
  readLocalDevice(): Promise<Exclude<RuntimeDeviceState, "unknown">>;
  lockDevice(reason: string): Promise<void>;
}>;

/**
 * 在线启动必须重新验证设备，口径与 WPF #519 一致：
 * - 只有带服务端业务错误码的 ApiResult 403 才是明确拒绝，持久化锁机；
 * - 传输失败、5xx，以及正文不是 ApiResult 信封的响应（网关/EdgeOne/WAF HTML 403、404/405、
 *   Wi-Fi 认证页、2xx 非信封等）都证明不了服务端拒绝，按服务不可用回退本地离线状态；
 * - 其余带信封的 HTTP/业务错误、解析或编程错误保持不可交易，不能伪装成断网后使用旧 Keychain 凭据。
 * 已被旧版本误锁的设备：锁只阻止出站设备凭据，不删除凭据；下次联网启动的匿名 verify 一旦
 * 得到服务端「已启用且下发授权码」的结论，DeviceSessionCoordinator 会写回凭据并解除本地锁（自愈）。
 */
export async function resolveStartupDeviceGate(
  options: StartupDeviceGateOptions,
): Promise<StartupDeviceGateResult> {
  if (options.registrationResetPending === true) {
    return {
      backend: options.internetReachable ? "unverified" : "offline",
      device: "locked",
    };
  }
  const pendingActivation = await readPendingDeviceActivation(options);
  if (!options.internetReachable) {
    if (pendingActivation) {
      throw new DeviceActivationRecoveryRequiredError();
    }
    return {
      backend: "offline",
      device: await options.readLocalDevice(),
    };
  }

  try {
    return fromVerifiedSession(await options.verifyCurrentDevice());
  } catch (error: unknown) {
    if (isExplicitDeviceRejection(error)) {
      await options.lockDevice(error.message);
      return { backend: "rejected", device: "locked" };
    }
    if (isOfflineCompatibleFailure(error)) {
      if (await readPendingDeviceActivation(options)) {
        throw new DeviceActivationRecoveryRequiredError();
      }
      return {
        backend: "offline",
        device: await options.readLocalDevice(),
      };
    }
    throw error;
  }
}

class DeviceActivationRecoveryRequiredError extends Error {
  public constructor() {
    super("Device activation recovery is pending; reconnect to finish recovery.");
    this.name = "DeviceActivationRecoveryRequiredError";
  }
}

async function readPendingDeviceActivation(
  options: StartupDeviceGateOptions,
): Promise<string | null> {
  try {
    return await options.readPendingDeviceActivation();
  } catch {
    // Keychain/JSON 状态无法判定时不得读取旧 Enabled cache 继续交易。
    throw new DeviceActivationRecoveryRequiredError();
  }
}

function fromVerifiedSession(
  state: DeviceSessionState,
): StartupDeviceGateResult {
  switch (state.status) {
    case "authorized":
      return { backend: "reachable", device: "authorized-online" };
    case "pending-approval":
      return { backend: "reachable", device: "pending-approval" };
    case "disabled":
      return { backend: "rejected", device: "locked" };
    case "denied":
      return { backend: "rejected", device: "registration-required" };
    case "unregistered":
      // 没有设备号时 poll 不会发出 API 请求，不能声称后端已验证可达。
      return { backend: "unverified", device: "registration-required" };
    case "registering":
    case "verifying":
    case "reregistering":
      throw new Error(`Device verification returned transient state: ${state.status}`);
  }
}

function isExplicitDeviceRejection(
  error: unknown,
): error is HbposApiError & { status: number } {
  // 网关 HTML 403 没有信封也没有错误码，绝不能据此持久化锁机。
  return (
    error instanceof HbposApiError &&
    error.kind === "http" &&
    error.status === 403 &&
    error.apiEnvelope === true &&
    typeof error.code === "string" &&
    error.code.trim().length > 0
  );
}

function isOfflineCompatibleFailure(error: unknown): boolean {
  return (
    error instanceof HbposApiError &&
    (error.kind === "transport" ||
      // 非 ApiResult 响应（含 2xx 非信封抛出的 envelope 类错误）不是服务端结论，按服务不可用处理。
      isHbposNonApiResponse(error) ||
      (error.kind === "http" &&
        error.status !== undefined &&
        error.status >= 500 &&
        error.status < 600))
  );
}
