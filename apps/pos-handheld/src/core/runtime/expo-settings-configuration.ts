import type {
  SettingsAppUpdateSnapshot,
  SettingsPaymentSettingsInput,
} from "../../features/settings/settings-presenter";
import type { PosHandheldUpdatePolicy } from "../contracts/app-updates";

import type { PosPaymentPublicExtra } from "./payment-runtime-config";

export type SettingsFetchPort = (
  url: string,
  init: Readonly<{ method: "GET"; signal: AbortSignal }>,
) => Promise<Readonly<{ ok: boolean }>>;

export function settingsPaymentConfiguration(
  input: PosPaymentPublicExtra | null | undefined,
): SettingsPaymentSettingsInput | null {
  if (input?.provider === "square") {
    const square = validSquare(input.square);
    return square
      ? Object.freeze({
          provider: "square" as const,
          square,
          linkly: null,
        })
      : null;
  }
  if (input?.provider === "linkly") {
    const linkly = validLinkly(input.linkly);
    return linkly
      ? Object.freeze({
          provider: "linkly" as const,
          square: null,
          linkly,
        })
      : null;
  }
  return null;
}

const RELEASE_CHANNEL_MAX_LENGTH = 160;

export function settingsAppUpdateSnapshot(input: Readonly<{
  channel: string;
  currentVersion: string;
  /** 原生构建号（Android versionCode）；同一 appVersion 下各原生包只能靠它区分。 */
  currentBuild?: string | null | undefined;
  policy: PosHandheldUpdatePolicy | null;
  restartAvailable: boolean;
}>): SettingsAppUpdateSnapshot {
  const latestVersion = input.policy?.latestVersion ?? null;
  return Object.freeze({
    // release channel 由后端生成（如 pos-handheld-production-android-release-<时间戳>-<哈希>，68 字符），
    // 上限须与后端 ReleaseChannel 校验（160）一致，否则套用 OTA 后设置页整页读取失败。
    channel: requiredText(input.channel, RELEASE_CHANNEL_MAX_LENGTH),
    currentVersion: requiredText(
      versionWithBuild(input.currentVersion, input.currentBuild),
      64,
    ),
    availableVersion:
      latestVersion === null
        ? null
        : versionWithBuild(latestVersion, input.policy?.latestBuild),
    updateRequired: input.policy?.required === true,
    restartAvailable: input.restartAvailable,
  });
}

/** 显示为「0.1.0 (9)」；构建号缺失或不是纯数字（如 "unknown" 哨兵）时只显示版本。 */
function versionWithBuild(
  version: string,
  build: string | null | undefined,
): string {
  const normalizedBuild = build?.trim() ?? "";
  return /^\d{1,9}$/u.test(normalizedBuild)
    ? `${version.trim()} (${normalizedBuild})`
    : version;
}

export function createSettingsApiHealthProbe(
  fetcher: SettingsFetchPort,
): (healthUrl: string, signal: AbortSignal) => Promise<boolean> {
  return async (healthUrl, signal) => {
    if (signal.aborted) throw abortError();
    try {
      const response = await fetcher(healthUrl, {
        method: "GET",
        signal,
      });
      if (signal.aborted) throw abortError();
      return response.ok === true;
    } catch {
      if (signal.aborted) throw abortError();
      return false;
    }
  };
}

/**
 * 设备重新绑定会先提交新凭据再返回结果；取消信号只在不可逆调用前生效。
 * rebind resolve 后不得再检查旧 signal，否则会把已提交的 authorized 误报成可重试失败。
 */
export async function reregisterSettingsDevice<TRequest>(
  request: TRequest,
  signal: AbortSignal,
  rebind: (
    request: TRequest,
    onCredentialsCommitted: () => void,
  ) => Promise<Readonly<{ status: string }>>,
  onCredentialsCommitted: () => void,
): Promise<Readonly<{ status: "committed" }>> {
  if (signal.aborted) throw abortError();
  let committed = false;
  const markCommitted = () => {
    if (committed) return;
    committed = true;
    onCredentialsCommitted();
  };
  try {
    const result = await rebind(request, markCommitted);
    if (committed) return Object.freeze({ status: "committed" });
    if (result.status !== "authorized") {
      throw new Error(
        `SETTINGS_DEVICE_REREGISTRATION_${result.status.toUpperCase()}`,
      );
    }
    throw new Error("SETTINGS_DEVICE_REREGISTRATION_COMMIT_UNCONFIRMED");
  } catch (error: unknown) {
    if (committed) return Object.freeze({ status: "committed" });
    throw error;
  }
}

/**
 * Expo 不保证 reloadAsync resolve 后旧 JS 会立即停止。成功后保持 Promise pending，
 * 让支付配置 transition 一直封门直到新 runtime 接管；reload 失败仍原样抛出。
 */
export async function reloadSettingsRuntimeTerminally(
  reload: () => Promise<void>,
): Promise<never> {
  await reload();
  return new Promise<never>(() => undefined);
}

function validSquare(
  input: PosPaymentPublicExtra["square"] | undefined,
): Readonly<{
  environment: "Sandbox" | "Production";
  deviceId: string;
  locationId: string;
}> | null {
  if (
    !input ||
    !validEnvironment(input.environment) ||
    !validIdentifier(input.deviceId) ||
    !validIdentifier(input.locationId)
  ) {
    return null;
  }
  return Object.freeze({
    environment: input.environment,
    deviceId: input.deviceId.trim(),
    locationId: input.locationId.trim(),
  });
}

function validLinkly(
  input: PosPaymentPublicExtra["linkly"] | undefined,
): Readonly<{
  environment: "Sandbox" | "Production";
}> | null {
  if (!input || !validEnvironment(input.environment)) return null;
  return Object.freeze({ environment: input.environment });
}

function validEnvironment(
  value: unknown,
): value is "Sandbox" | "Production" {
  return value === "Sandbox" || value === "Production";
}

function validIdentifier(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.trim().length > 0 &&
    value.trim().length <= 256 &&
    !/[\u0000-\u001f\u007f]/u.test(value)
  );
}

function requiredText(value: string, maximum: number): string {
  const normalized = value.trim();
  if (
    !normalized ||
    normalized.length > maximum ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new Error("Settings public metadata is invalid.");
  }
  return normalized;
}

function abortError(): Error {
  return Object.assign(new Error("Settings request aborted."), {
    name: "AbortError",
  });
}
