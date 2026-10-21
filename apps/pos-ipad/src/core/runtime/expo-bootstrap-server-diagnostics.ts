import { createFailoverFetch } from "@hb/pos-api-client/transport-failover";
import Constants from "expo-constants";

import { ExpoSecureStoreAdapter } from "../security/expo-secure-store";
import {
  normalizeTrustedApiOrigins,
  PosPublicRuntimeConfigurationStore,
} from "../security/pos-public-runtime-configuration";
import { PendingDeviceActivationCodeStore } from "../security/secure-storage";

import {
  createBootstrapServerDiagnostics,
  type BootstrapServerDiagnostics as BaseBootstrapServerDiagnostics,
} from "./bootstrap-server-diagnostics";
import { createSettingsApiHealthProbe } from "./expo-settings-configuration";
import { resolveHbposApiUrl } from "./runtime-config";

export type BootstrapServerDiagnostics = BaseBootstrapServerDiagnostics &
  Readonly<{
    abandonPendingDeviceActivation(): Promise<void>;
    canAbandonPendingDeviceActivation: boolean;
  }>;

type BootstrapExtra = Readonly<{
  hbpos?: Readonly<{
    apiBaseUrl?: string;
    trustedApiOrigins?: readonly string[];
  }>;
}>;

/**
 * 完整 runtime 尚未可用时只开放受信地址的只读探测；永久切换仍由已打开
 * 本地账本的注册运行时完成，避免在无法检查待补传数据时绕过安全门禁。
 */
export async function loadExpoBootstrapServerDiagnostics(): Promise<BootstrapServerDiagnostics> {
  const extra = Constants.expoConfig?.extra as BootstrapExtra | undefined;
  const secureStore = new ExpoSecureStoreAdapter();
  const trustedApiOrigins = normalizeTrustedApiOrigins([
    ...(extra?.hbpos?.trustedApiOrigins ?? []),
    ...(extra?.hbpos?.apiBaseUrl
      ? [extra.hbpos.apiBaseUrl]
      : [resolveHbposApiUrl(undefined)]),
  ]);
  const store = new PosPublicRuntimeConfigurationStore(
    secureStore,
    trustedApiOrigins,
  );
  const pendingActivation = new PendingDeviceActivationCodeStore(secureStore);
  const [persisted, pendingActivationCode] = await Promise.all([
    store.load(),
    // 只允许 Development Build 暴露主动放弃；正式包继续失败关闭并等待服务端确定结果。
    __DEV__ ? pendingActivation.load() : Promise.resolve(null),
  ]);
  const currentApiBaseUrl = resolveHbposApiUrl(
    persisted.apiBaseUrl ?? extra?.hbpos?.apiBaseUrl,
  );
  // 只读探测也走入口感知的 fetch：边缘入口不通时改探直连。
  const probe = createSettingsApiHealthProbe(
    createFailoverFetch((url, init) => fetch(url, init)),
  );

  const serverDiagnostics = createBootstrapServerDiagnostics({
    currentApiBaseUrl,
    trustedApiOrigins,
    probe,
  });

  return Object.freeze({
    ...serverDiagnostics,
    canAbandonPendingDeviceActivation:
      __DEV__ && pendingActivationCode !== null,
    abandonPendingDeviceActivation: async () => {
      if (!__DEV__) {
        throw new Error("BOOTSTRAP_PENDING_ACTIVATION_ABANDON_DISABLED");
      }
      // 精确删除一次性码 staging；不得触碰设备凭据、安装 ID 或离线账本密钥。
      await pendingActivation.clear();
    },
  });
}
