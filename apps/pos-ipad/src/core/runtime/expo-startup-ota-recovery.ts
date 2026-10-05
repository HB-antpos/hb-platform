import Constants from "expo-constants";

import { ExpoOtaUpdatePort } from "../../features/app-updates/expo-ota-update-port";
import { HbposPosIpadOtaUpdateApi } from "../../features/app-updates/hbpos-pos-ipad-ota-update-api";
import { OtaUpdateCoordinator } from "../../features/app-updates/ota-update-coordinator";
import {
  createStartupOtaRecoveryPolicyStore,
  StartupOtaRecovery,
} from "../../features/app-updates/startup-ota-recovery";
import { createAxiosHbposTransport } from "../api/axios-transport";
import { ExpoSecureStoreAdapter } from "../security/expo-secure-store";
import {
  DeviceCredentialStore,
  DeviceLockStore,
  InstallationIdentityStore,
  type StoredDeviceCredentials,
} from "../security/secure-storage";

import { loadExpoBootstrapServerDiagnostics } from "./expo-bootstrap-server-diagnostics";
import { readCurrentUpdateGroupId } from "./expo-update-identity";
import { resolveExpoUpdateRuntimeVersion } from "./expo-update-runtime-version";

type StartupRecoveryExtra = Readonly<{
  hbpos?: Readonly<{
    automaticOtaChecks?: boolean;
  }>;
}>;

type ExpoUpdatesModule = typeof import("expo-updates");

function expoUpdates(): ExpoUpdatesModule {
  // 同步 require 让 Metro 将 expo-updates 放入主 bundle，同时避免启动页 Jest 套件在未调用时解析原生入口。
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  return require("expo-updates") as ExpoUpdatesModule;
}

/**
 * 启动失败页的最小 OTA 依赖组合：只读 Keychain 中的公开配置与设备凭据，
 * 不打开 SQLite、不构造收银会话或支付运行时，也不挂认证失败回调
 * （401/403 只让本次检查失败，锁机与清票据仍由完整 runtime 负责）。
 */
export async function loadExpoStartupOtaRecovery(
  input: Readonly<{ canReload(): boolean }>,
): Promise<StartupOtaRecovery> {
  const Updates = expoUpdates();
  const extra = Constants.expoConfig?.extra as StartupRecoveryExtra | undefined;
  const { currentApiBaseUrl } = await loadExpoBootstrapServerDiagnostics();
  const secureStore = new ExpoSecureStoreAdapter();
  const deviceCredentials = new DeviceCredentialStore(secureStore);
  const deviceLock = new DeviceLockStore(secureStore);
  const installation = new InstallationIdentityStore(secureStore, () => {
    // 恢复通道只读：缺少安装 ID 说明设备从未完成注册，不在这里生成新身份。
    throw new Error("STARTUP_OTA_RECOVERY_INSTALLATION_ID_MISSING");
  });
  // 与 DeviceSessionCoordinator.getTransportCredentials 同一判定：锁机或硬件不符一律不出站。
  const readDeviceCredentials =
    async (): Promise<StoredDeviceCredentials | null> => {
      if (await deviceLock.isLocked()) return null;
      const credentials = await deviceCredentials.load();
      if (!credentials) return null;
      let installationId: string;
      try {
        installationId = await installation.getOrCreate();
      } catch {
        return null;
      }
      return credentials.hardwareId === installationId ? credentials : null;
    };
  const transport = createAxiosHbposTransport(currentApiBaseUrl, {
    getCredentials: async () => {
      const device = await readDeviceCredentials();
      return device ? Object.freeze({ device }) : Object.freeze({});
    },
  });

  const appVersion =
    Constants.nativeAppVersion ?? Constants.expoConfig?.version ?? "0.0.0";
  const runtimeVersion = resolveExpoUpdateRuntimeVersion(
    Updates.runtimeVersion,
    appVersion,
  );
  // 与完整 runtime 的 automaticChecksConfigured 完全一致。
  const enabled = extra?.hbpos?.automaticOtaChecks === true;
  const coordinator = new OtaUpdateCoordinator({
    automaticChecksEnabled: enabled,
    metadata: {
      runtimeVersion,
      currentUpdateId: Updates.updateId,
      currentUpdateGroupId: readCurrentUpdateGroupId(Updates.manifest),
    },
    policyStore: createStartupOtaRecoveryPolicyStore(),
    remote: new HbposPosIpadOtaUpdateApi(transport),
    installer: new ExpoOtaUpdatePort({
      enabled: Updates.isEnabled,
      runtimeVersion: Updates.runtimeVersion,
      updates: {
        setUpdateRequestHeadersOverride: (headers) =>
          Updates.setUpdateRequestHeadersOverride(headers),
        checkForUpdateAsync: () => Updates.checkForUpdateAsync(),
        fetchUpdateAsync: () => Updates.fetchUpdateAsync(),
        reloadAsync: () => Updates.reloadAsync(),
      },
    }),
  });

  return new StartupOtaRecovery({
    enabled,
    hasDeviceCredentials: async () => (await readDeviceCredentials()) !== null,
    coordinator,
    canReload: () => input.canReload(),
  });
}
