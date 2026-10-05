import {
  beforeEach,
  describe,
  expect,
  it,
  jest,
} from "@jest/globals";

import type {
  HbposAuthenticationFailureHandler,
  HbposRequestCredentialProvider,
} from "../api/axios-transport";
import {
  DeviceCredentialStore,
  DeviceLockStore,
  InstallationIdentityStore,
  type SecureStorePort,
} from "../security/secure-storage";

import { loadExpoStartupOtaRecovery } from "./expo-startup-ota-recovery";

const mockSecureValues = new Map<string, string>();
const mockExtra: { hbpos: { automaticOtaChecks?: boolean } } = {
  hbpos: { automaticOtaChecks: true },
};
const mockRequests: { url: string; params: unknown; credentials: unknown }[] = [];
const mockTransportFactory = jest.fn<
  (
    baseUrl: string,
    provider: HbposRequestCredentialProvider,
    instance?: unknown,
    failureHandler?: HbposAuthenticationFailureHandler,
  ) => unknown
>();
const mockUpdates = {
  isEnabled: true,
  runtimeVersion: "1.0.7",
  channel: "pos-ipad-production",
  updateId: null as string | null,
  manifest: null as unknown,
  setUpdateRequestHeadersOverride: jest.fn(),
  checkForUpdateAsync: jest.fn(async () => ({
    isAvailable: true,
    manifest: { id: "0a11e5d1-0000-4000-8000-000000000011", runtimeVersion: "1.0.7" },
  })),
  fetchUpdateAsync: jest.fn(async () => ({
    isNew: true,
    manifest: { id: "0a11e5d1-0000-4000-8000-000000000011", runtimeVersion: "1.0.7" },
  })),
  reloadAsync: jest.fn(async () => undefined),
};

// 失败页的恢复通道不得加载本地账本：任何 SQLite 依赖被 require 都会让套件失败。
jest.mock("expo-sqlite", () => {
  throw new Error("Startup OTA recovery must not load SQLite.");
});
jest.mock("../db/pos-database", () => {
  throw new Error("Startup OTA recovery must not load PosDatabase.");
});
jest.mock("expo-updates", () => mockUpdates);
jest.mock("expo-constants", () => ({
  __esModule: true,
  default: {
    nativeAppVersion: "1.0.7",
    expoConfig: {
      version: "1.0.7",
      get extra() {
        return mockExtra;
      },
    },
  },
}));
jest.mock("./expo-bootstrap-server-diagnostics", () => ({
  loadExpoBootstrapServerDiagnostics: async () => ({
    currentApiBaseUrl: "https://hotbargain.vip/pos-api",
    test: async () => true,
  }),
}));
jest.mock("../security/expo-secure-store", () => ({
  ExpoSecureStoreAdapter: class {
    public async get(key: string) {
      return mockSecureValues.get(key) ?? null;
    }
    public async set(key: string, value: string) {
      mockSecureValues.set(key, value);
    }
    public async remove(key: string) {
      mockSecureValues.delete(key);
    }
  },
}));
jest.mock("../api/axios-transport", () => ({
  createAxiosHbposTransport: (
    baseUrl: string,
    provider: HbposRequestCredentialProvider,
    instance?: unknown,
    failureHandler?: HbposAuthenticationFailureHandler,
  ) => {
    mockTransportFactory(baseUrl, provider, instance, failureHandler);
    return {
      async request(config: { url: string; params?: unknown }) {
        mockRequests.push({
          url: config.url,
          params: config.params,
          credentials: await provider.getCredentials(),
        });
        return {
          status: 200,
          data: {
            success: true,
            data: {
              state: "required",
              policyVersion: "11",
              channel: "pos-ipad-production-store-1013",
              runtimeVersion: "1.0.7",
              iosUpdateId: "0a11e5d1-0000-4000-8000-000000000011",
              updateGroupId: "86a1abec-0000-4000-8000-000000000001",
              releaseMessage: null,
            },
          },
        };
      },
    };
  },
}));

const secureStore: SecureStorePort = {
  get: async (key) => mockSecureValues.get(key) ?? null,
  set: async (key, value) => {
    mockSecureValues.set(key, value);
  },
  remove: async (key) => {
    mockSecureValues.delete(key);
  },
};

async function seedRegisteredDevice(): Promise<void> {
  const installationId = await new InstallationIdentityStore(
    secureStore,
    () => "install-ipad-0001",
  ).getOrCreate();
  await new DeviceCredentialStore(secureStore).save({
    deviceCode: "IPAD_1013_0001",
    storeCode: "1013",
    hardwareId: installationId,
    authorizationCode: "device-secret",
  });
}

describe("loadExpoStartupOtaRecovery", () => {
  beforeEach(() => {
    mockExtra.hbpos = { automaticOtaChecks: true };
    mockSecureValues.clear();
    mockRequests.length = 0;
    mockTransportFactory.mockClear();
    mockUpdates.setUpdateRequestHeadersOverride.mockClear();
    mockUpdates.reloadAsync.mockClear();
  });

  it("只用 Keychain 设备凭据请求受控 OTA 策略，套用后 reload 且不清除本地数据", async () => {
    await seedRegisteredDevice();
    const before = new Map(mockSecureValues);
    const recovery = await loadExpoStartupOtaRecovery({
      canReload: () => true,
    });

    await expect(recovery.check()).resolves.toEqual({
      phase: "available",
      requirement: "required",
      releaseMessage: null,
    });
    await recovery.apply();

    const [baseUrl, , , failureHandler] =
      mockTransportFactory.mock.calls[0] ?? [];
    expect(baseUrl).toBe("https://hotbargain.vip/pos-api");
    // 不挂认证失败回调：401/403 不会在恢复通道里锁机或清票据。
    expect(failureHandler).toBeUndefined();
    expect(mockRequests).toHaveLength(2);
    expect(mockRequests[0]).toEqual({
      url: "/api/v1/app-updates/pos-ipad/ota",
      params: {
        runtimeVersion: "1.0.7",
        currentUpdateId: undefined,
        currentUpdateGroupId: undefined,
      },
      credentials: {
        device: {
          deviceCode: "IPAD_1013_0001",
          storeCode: "1013",
          hardwareId: "install-ipad-0001",
          authorizationCode: "device-secret",
        },
      },
    });
    // iPad 执行端口在核验期间定向分店 channel，reload 前已清除覆盖。
    expect(mockUpdates.setUpdateRequestHeadersOverride).toHaveBeenCalledWith({
      "expo-channel-name": "pos-ipad-production-store-1013",
    });
    expect(mockUpdates.setUpdateRequestHeadersOverride).toHaveBeenLastCalledWith(
      null,
    );
    expect(mockUpdates.reloadAsync).toHaveBeenCalledTimes(1);
    expect(mockSecureValues).toEqual(before);
  });

  it("设备被锁定时不出站，也不生成新的安装 ID", async () => {
    await seedRegisteredDevice();
    await new DeviceLockStore(secureStore).lock("revoked");
    const before = new Map(mockSecureValues);
    const recovery = await loadExpoStartupOtaRecovery({
      canReload: () => true,
    });

    await expect(recovery.check()).resolves.toEqual({
      phase: "unavailable",
      reason: "device-unavailable",
    });
    expect(mockRequests).toHaveLength(0);
    expect(mockSecureValues).toEqual(before);
  });

  it("从未注册的设备不在恢复通道里创建安装身份", async () => {
    const recovery = await loadExpoStartupOtaRecovery({
      canReload: () => true,
    });

    await expect(recovery.check()).resolves.toEqual({
      phase: "unavailable",
      reason: "device-unavailable",
    });
    expect(mockSecureValues.size).toBe(0);
  });

  it("未配置自动 OTA 检查的构建不出网", async () => {
    mockExtra.hbpos = {};
    await seedRegisteredDevice();
    const recovery = await loadExpoStartupOtaRecovery({
      canReload: () => true,
    });

    await expect(recovery.check()).resolves.toEqual({
      phase: "unavailable",
      reason: "updates-disabled",
    });
    expect(mockRequests).toHaveLength(0);
  });
});
