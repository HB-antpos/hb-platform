import { useEffect, useState } from "react";
import { Platform } from "react-native";
import Constants from "expo-constants";
import { getCurrentAppUpdateInfo } from "@/modules/updates/app-update-runtime";
import {
  formatAppPackageVersion,
  resolveAppUpdateChannelKind,
  type AppUpdateInfo,
} from "@/modules/updates/app-update-info";
import { getStoredApiHost } from "@/shared/api/config";
import { resolveEffectiveApiBaseUrl } from "@/shared/api/effective-api-host";
import { SecureStorage } from "@/shared/storage/secure";
import { useAuthStore } from "@/store/auth-store";
import type { NetworkTestContext } from "./types";

/** 从 API 基础地址里取主机名（不含端口），用于显示与判断地址来源。 */
export function extractHostname(apiBaseUrl: string): string {
  const match = /^[a-z]+:\/\/([^/:?#]+)/i.exec(apiBaseUrl.trim());
  return match ? match[1] : apiBaseUrl;
}

/**
 * 设备描述：只用 React Native 内置的 Platform 和早已在用的 expo-constants。
 * 不用 expo-device——它 10-15 才随推送加入，更早的原生包（安卓正式包、iOS 1.0.7/1.0.8）没有这个原生模块，
 * 网络测试要能通过 OTA 发给所有在用的包，引用它会在旧包上直接崩溃。
 */
export function describeDevice(): string {
  const constants = (Platform.constants ?? {}) as Record<string, unknown>;
  const deviceName = typeof Constants.deviceName === "string" ? Constants.deviceName.trim() : "";
  if (Platform.OS === "android") {
    const model = [constants.Brand ?? constants.Manufacturer, constants.Model]
      .filter((part): part is string => typeof part === "string" && part.length > 0)
      .join(" ");
    const release = typeof constants.Release === "string" ? constants.Release : String(Platform.Version);
    return `${model || deviceName || "Android"} · Android ${release}`;
  }
  if (Platform.OS === "ios") {
    const kind = Platform.isPad ? "iPad" : "iPhone";
    // 设备名是用户在手机里设的名字（如「1020 收银台 iPhone」），门店多台设备时便于认出是哪台。
    const name = deviceName && deviceName !== kind ? `${deviceName} (${kind})` : kind;
    return `${name} · iOS ${String(Platform.Version)}`;
  }
  return Platform.OS;
}

function readUpdateInfo(): AppUpdateInfo | null {
  try {
    return getCurrentAppUpdateInfo();
  } catch {
    return null;
  }
}

export interface NetworkTestEnvironment {
  context: NetworkTestContext | null;
  deviceLabel: string;
  appVersionLabel: string;
  /** 渠道键（production / preview …），界面用登录页的渠道文案翻译。 */
  appChannelKind: ReturnType<typeof resolveAppUpdateChannelKind>;
  /** OTA 标识：内置包为 null，OTA 为更新 ID 前 8 位。 */
  otaId: string | null;
  accountLabel: string | null;
}

/**
 * 读取本次测试的环境：服务器地址（与业务请求同一套解析，设备绑定时以绑定地址为准）、
 * 登录令牌（未登录为 null）、设备与 App 版本。未登录时也能用，不依赖任何登录后才有的数据。
 */
export function useNetworkTestEnvironment(): NetworkTestEnvironment {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const user = useAuthStore((state) => state.user);
  const [context, setContext] = useState<NetworkTestContext | null>(null);
  const [updateInfo] = useState(readUpdateInfo);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const [apiBaseUrl, storedHost] = await Promise.all([resolveEffectiveApiBaseUrl(), getStoredApiHost()]);
      // 审核会话不发真实请求；其余会话只有登录着才带令牌测登录态接口。
      const accessToken =
        isAuthenticated && sessionKind !== "iosReview" ? await SecureStorage.getToken().catch(() => null) : null;
      const serverHost = extractHostname(apiBaseUrl);
      if (cancelled) return;
      setContext({
        apiBaseUrl,
        serverHost,
        serverSource: serverHost === extractHostname(`http://${storedHost}`) ? "settings" : "deviceBinding",
        accessToken: accessToken || null,
      });
    })();
    return () => {
      cancelled = true;
    };
  }, [isAuthenticated, sessionKind]);

  const [deviceLabel] = useState(describeDevice);
  return {
    context,
    deviceLabel,
    appVersionLabel: updateInfo ? formatAppPackageVersion(updateInfo, "--") : "--",
    appChannelKind: resolveAppUpdateChannelKind(updateInfo?.channel),
    otaId: updateInfo && !updateInfo.isEmbeddedLaunch && updateInfo.updateId ? updateInfo.updateId.slice(0, 8) : null,
    accountLabel: isAuthenticated ? user?.fullName?.trim() || user?.username || null : null,
  };
}
