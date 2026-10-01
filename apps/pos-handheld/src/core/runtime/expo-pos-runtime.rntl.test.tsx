import { describe, expect, it, jest } from "@jest/globals";

import {
  ApplicationLogActorBinding,
  ApplicationLogger,
  type ApplicationLogDraft,
  type ApplicationLogEntry,
} from "../logging/application-log";

import {
  bindCashierSessionToApplicationLog,
  createExpoAppUpdateCacheScopes,
  readSettingsDevicePresentation,
  recordRuntimeInitializationFailure,
  shouldEnableAppUpdates,
  shutdownExpoPosRuntimeServices,
  shutdownCompositionBeforeDatabaseClose,
} from "./expo-pos-runtime";

jest.mock("expo-application", () => ({}));
jest.mock("expo-router", () => ({}));
jest.mock("react-native-safe-area-context", () => ({}));
jest.mock("react-native-paper", () => ({ MD3LightTheme: {} }));
jest.mock("../../features/app-updates", () => ({}));
jest.mock("expo-constants", () => ({
  __esModule: true,
  default: { expoConfig: null },
}));
jest.mock("expo-crypto", () => ({}));
jest.mock("expo-network", () => ({}));
jest.mock("expo-updates", () => ({}));
jest.mock("../db/expo-sqlite-driver", () => ({
  ExpoSqliteDriver: class ExpoSqliteDriver {},
}));
jest.mock("../security/expo-secure-store", () => ({
  ExpoSecureStoreAdapter: class ExpoSecureStoreAdapter {},
}));
jest.mock("../security/sensitive-payload-encryptor", () => ({
  SensitivePayloadEncryptor: class SensitivePayloadEncryptor {},
}));
jest.mock("../peripherals/attendance-security/native", () => ({
  createExpoAttendanceSecurityAdapter: jest.fn(),
}));
jest.mock("./expo-printer-adapter", () => ({
  createLazyExpoPrinterAdapter: jest.fn(),
}));

it("开发/测试包不接收升级，正式包和 preview 保持更新能力", () => {
  for (const profile of [undefined, "production", "preview", "android-internal"]) {
    expect(shouldEnableAppUpdates(true, profile)).toBe(false);
    expect(shouldEnableAppUpdates(false, profile)).toBe(true);
  }
  for (const profile of ["development", "test", "testing", " DEVELOPMENT "]) {
    expect(shouldEnableAppUpdates(false, profile)).toBe(false);
  }
});

describe("Expo app update cache identity", () => {
  it("从同一运行时 metadata 生成互不混用的 native/OTA scope", () => {
    expect(
      createExpoAppUpdateCacheScopes({
        apiOrigin: "https://hotbargain.vip/pos-api",
        storeCode: "BNE-01",
        platform: "Android",
        installedVersion: "1.2.3",
        installedBuild: "43",
        projectId: "123E4567-E89B-42D3-A456-426614174000",
        projectName: "hb-pos-handheld",
        configuredChannel: "pos-handheld-production",
        runtimeVersion: "1.2.3",
        currentUpdateId: "323E4567-E89B-42D3-A456-426614174000",
        currentUpdateGroupId:
          "223E4567-E89B-42D3-A456-426614174000",
      }),
    ).toEqual({
      native: {
        kind: "native",
        apiOrigin: "https://hotbargain.vip",
        storeCode: "BNE-01",
        appKey: "pos-handheld",
        platform: "Android",
        installedVersion: "1.2.3",
        installedBuild: "43",
      },
      ota: {
        kind: "ota",
        apiOrigin: "https://hotbargain.vip",
        storeCode: "BNE-01",
        appKey: "pos-handheld",
        projectId: "123e4567-e89b-42d3-a456-426614174000",
        projectName: "hb-pos-handheld",
        platform: "Android",
        configuredChannel: "pos-handheld-production",
        runtimeVersion: "1.2.3",
        currentUpdateId: "323e4567-e89b-42d3-a456-426614174000",
        currentUpdateGroupId:
          "223e4567-e89b-42d3-a456-426614174000",
      },
    });
  });
});

describe("readSettingsDevicePresentation", () => {
  it("只从公开设备展示身份映射设置页终端身份", async () => {
    const getDevicePresentation = jest.fn(async () => ({
      deviceCode: "POS-01",
      storeCode: "BNE-01",
      storeName: "Brisbane Central",
    }));

    const result = await readSettingsDevicePresentation({
      getDevicePresentation,
    });

    expect(result).toEqual({
      deviceCode: "POS-01",
      storeCode: "BNE-01",
      storeName: "Brisbane Central",
      terminalName: "",
    });
    expect(Object.isFrozen(result)).toBe(true);
    expect(getDevicePresentation).toHaveBeenCalledTimes(1);
  });

  it("公开展示身份缺少分店名称时传空字符串且不拿分店代码回退", async () => {
    await expect(
      readSettingsDevicePresentation({
        getDevicePresentation: async () => ({
          deviceCode: "POS-01",
          storeCode: "BNE-01",
          storeName: null,
        }),
      }),
    ).resolves.toEqual({
      deviceCode: "POS-01",
      storeCode: "BNE-01",
      storeName: "",
      terminalName: "",
    });
  });

  it("公开展示身份不可用时失败关闭", async () => {
    await expect(
      readSettingsDevicePresentation({
        getDevicePresentation: async () => null,
      }),
    ).rejects.toThrow("SETTINGS_DEVICE_IDENTITY_REQUIRED");
  });
});

describe("程序日志收银员身份绑定", () => {
  it("Alice、Bob 的日志在记录时冻结身份；失败登录不会沿用 Alice", async () => {
    const actor = new ApplicationLogActorBinding();
    const signin = jest.fn(async (barcode: string) => {
      if (barcode === "fail") throw new Error("CASHIER_LOGIN_FAILED");
      return barcode === "scan-alice-private"
        ? {
            cashierId: "cashier-alice",
            userGuid: "user-alice",
            cashierName: "Alice",
            storeCode: "BNE-01",
            deviceCode: "IPAD-01",
            permissions: [],
            source: "online" as const,
          }
        : {
            cashierId: "cashier-bob",
            userGuid: null,
            cashierName: "Bob",
            storeCode: "BNE-01",
            deviceCode: "IPAD-01",
            permissions: [],
            source: "offline-cache" as const,
          };
    });
    const cashierSession = bindCashierSessionToApplicationLog(
      { signIn: signin },
      actor,
    );
    const recorded: ApplicationLogEntry[] = [];
    const logger = new ApplicationLogger(
      {
        enqueue: async (entry: ApplicationLogEntry) => {
          recorded.push(entry);
        },
      } as never,
      () => ({
        storeCode: "BNE-01",
        deviceCode: "IPAD-01",
        userId: actor.read()?.userId ?? null,
        userName: actor.read()?.userName ?? null,
        appVersion: "1",
        instanceId: "instance",
      }),
      () => `event-${recorded.length}`,
      () => "2026-08-01T00:00:00.000Z",
    );

    await cashierSession.signIn("scan-alice-private");
    await logger.record({ level: "Error", message: "Alice logged error" });
    await expect(cashierSession.signIn("fail")).rejects.toThrow(
      "CASHIER_LOGIN_FAILED",
    );
    await logger.record({ level: "Error", message: "No actor error" });
    await cashierSession.signIn("scan-bob-private");
    await logger.record({ level: "Error", message: "Bob logged error" });

    expect(recorded.map((entry) => [entry.userId, entry.userName])).toEqual([
      ["user-alice", "Alice"],
      [null, null],
      ["cashier-bob", "Bob"],
    ]);
    expect(JSON.stringify(recorded)).not.toContain("scan-");
  });
});

describe("初始化异常收尾", () => {
  it("组合根后台清理失败仍关闭数据库", async () => {
    const order: string[] = [];
    const shutdownError = new Error("catalog shutdown failed");

    await expect(
      shutdownCompositionBeforeDatabaseClose(
        async () => {
          order.push("shutdown-composition");
          throw shutdownError;
        },
        async () => {
          order.push("close-database");
        },
      ),
    ).rejects.toBe(shutdownError);
    expect(order).toEqual(["shutdown-composition", "close-database"]);
  });

  it("在关闭数据库前记录并完成日志收尾", async () => {
    const order: string[] = [];
    await recordRuntimeInitializationFailure(
      {
        logger: {
          record: async (draft: ApplicationLogDraft) => {
            order.push(`record:${draft.category}`);
          },
        },
        shutdown: async () => {
          order.push("shutdown-log");
        },
      } as never,
      new Error("composition failed"),
      async () => {
        order.push("close-database");
      },
    );

    expect(order).toEqual([
      "record:runtime.initialization",
      "shutdown-log",
      "close-database",
    ]);
  });

  it.each(["record", "shutdown", "close"] as const)(
    "%s 旁路失败不覆盖 caller 重抛的原初始化异常",
    async (failedStep) => {
      const order: string[] = [];
      const initializationError = new Error("original initialization failure");
      const applicationLog = {
        logger: {
          record: async () => {
            order.push("record");
            if (failedStep === "record") throw new Error("record failed");
          },
        },
        shutdown: async () => {
          order.push("shutdown");
          if (failedStep === "shutdown") throw new Error("shutdown failed");
        },
      } as never;

      const caller = async () => {
        try {
          throw initializationError;
        } catch (error) {
          await recordRuntimeInitializationFailure(
            applicationLog,
            error,
            async () => {
              order.push("close");
              if (failedStep === "close") throw new Error("close failed");
            },
          );
          throw error;
        }
      };

      await expect(caller()).rejects.toBe(initializationError);
      expect(order).toEqual(["record", "shutdown", "close"]);
    },
  );

  it.each(["sync", "record", "application-log"] as const)(
    "%s 失败仍执行后台与数据库收尾，并保留第一个错误",
    async (failedStep) => {
      const order: string[] = [];
      const firstError = new Error(`${failedStep} failed`);

      await expect(
        shutdownExpoPosRuntimeServices({
          sync: {
            shutdown: async () => {
              order.push("sync");
              if (failedStep === "sync") throw firstError;
            },
          },
          applicationLog: {
            logger: {
              record: async () => {
                order.push("record");
                if (failedStep === "record") throw firstError;
              },
            },
            shutdown: async () => {
              order.push("application-log");
              if (failedStep === "application-log") throw firstError;
            },
          } as never,
          shutdownBackgroundWork: async () => {
            order.push("background");
            throw new Error("background cleanup failed");
          },
          closeDatabase: async () => {
            order.push("database");
            throw new Error("database close failed");
          },
        }),
      ).rejects.toBe(firstError);
      expect(order).toEqual([
        "sync",
        "record",
        "application-log",
        "background",
        "database",
      ]);
    },
  );

  it("pre-step 同步异常仍按序尝试全部收尾，且后续 close 异常不覆盖首错", async () => {
    const order: string[] = [];
    const preStepError = new Error("runtime pre-step failed");

    await expect(
      shutdownExpoPosRuntimeServices({
        beforeShutdown: [
          () => {
            order.push("stop-advertisements");
            throw preStepError;
          },
          async () => { order.push("clear-display"); },
          () => { order.push("notify-lock"); },
          () => { order.push("dispose-updates"); },
        ],
        sync: {
          shutdown: async () => { order.push("sync"); },
        },
        applicationLog: {
          logger: {
            record: async () => { order.push("record"); },
          },
          shutdown: async () => { order.push("application-log"); },
        } as never,
        shutdownBackgroundWork: async () => { order.push("background"); },
        closeDatabase: async () => {
          order.push("database");
          throw new Error("database close failed");
        },
      }),
    ).rejects.toBe(preStepError);
    expect(order).toEqual([
      "stop-advertisements",
      "clear-display",
      "notify-lock",
      "dispose-updates",
      "sync",
      "record",
      "application-log",
      "background",
      "database",
    ]);
  });
});
