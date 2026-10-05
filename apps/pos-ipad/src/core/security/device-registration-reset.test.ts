import assert from "node:assert/strict";
import test from "node:test";

import { HbposApiError, type DeviceVerifyResponse } from "../api/hbpos-api";

import {
  CashierAuthenticationService,
  type CashierAuthenticationApi,
} from "./cashier-authentication";
import {
  DeviceRegistrationResetCoordinator,
  type DeviceRegistrationResetApi,
} from "./device-registration-reset";
import {
  CashierAuthorizationStore,
  CashierSessionCache,
  DeviceCredentialStore,
  DeviceLockStore,
  DevicePresentationStore,
  DeviceRegistrationResetMarkerStore,
  InMemorySecureStore,
  InstallationIdentityStore,
  PendingDeviceRegistrationStore,
} from "./secure-storage";

const operationId = "10000000-0000-4000-8000-000000000001";
const hardwareId = "20000000-0000-4000-8000-000000000002";
const credentials = {
  deviceCode: "IPAD-1042-01",
  storeCode: "1042",
  hardwareId,
  authorizationCode: "device-secret",
};
const resetResponse = {
  operationId,
  deviceCode: credentials.deviceCode,
  storeCode: credentials.storeCode,
  disabledAtUtc: "2026-08-18T02:00:01.000Z",
};

function createHarness(api: DeviceRegistrationResetApi) {
  const secureStore = new InMemorySecureStore();
  const credentialStore = new DeviceCredentialStore(secureStore);
  const presentationStore = new DevicePresentationStore(secureStore);
  const pendingStore = new PendingDeviceRegistrationStore(secureStore);
  const lockStore = new DeviceLockStore(secureStore);
  const markerStore = new DeviceRegistrationResetMarkerStore(secureStore);
  const cashierAuthorization = new CashierAuthorizationStore(secureStore);
  const installation = new InstallationIdentityStore(secureStore, () => hardwareId);
  let invalidations = 0;
  const subject = new DeviceRegistrationResetCoordinator({
    api,
    authenticateOnline: async () => ({
      source: "online" as const,
      session: {
        cashierId: "CASHIER-1",
        userGuid: "USER-1",
        cashierName: "Reviewer",
        storeCode: credentials.storeCode,
        deviceCode: credentials.deviceCode,
        permissionCodes: [
          "Permissions.PosTerminal.Settings.DeviceRegistration",
        ],
        authorizationToken: "fresh-online-ticket",
      },
    }),
    credentials: credentialStore,
    presentation: presentationStore,
    pendingRegistration: pendingStore,
    lock: lockStore,
    marker: markerStore,
    cashierAuthorization,
    installation,
    createOperationId: () => operationId,
    nowIso: () => "2026-08-18T02:00:00.000Z",
    invalidateCurrentCashier: () => {
      invalidations += 1;
    },
  });
  return {
    secureStore,
    credentialStore,
    presentationStore,
    pendingStore,
    lockStore,
    markerStore,
    cashierAuthorization,
    installation,
    subject,
    invalidations: () => invalidations,
  };
}

async function seedRegistered(harness: ReturnType<typeof createHarness>) {
  await harness.installation.getOrCreate();
  await harness.credentialStore.save(credentials);
  await harness.presentationStore.save({
    deviceCode: credentials.deviceCode,
    storeCode: credentials.storeCode,
    storeName: "testStore",
  });
  await harness.pendingStore.save({
    deviceCode: credentials.deviceCode,
    storeCode: credentials.storeCode,
  });
  await harness.cashierAuthorization.set({
    authorizationToken: "current-ticket",
    expiresAtEpochMs: Date.now() + 60_000,
    source: "online",
    scope: {
      deviceCode: credentials.deviceCode,
      storeCode: credentials.storeCode,
    },
  });
}

test("首次 prepared marker 写入失败时零服务端写入并立即锁机", async () => {
  let resetCalls = 0;
  const harness = createHarness({
    resetRegistration: async () => {
      resetCalls += 1;
      return resetResponse;
    },
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);
  harness.markerStore.save = async () => {
    throw new Error("marker save failed");
  };

  await assert.rejects(
    () => harness.subject.reset("EMPLOYEE-BARCODE"),
    /marker save failed/,
  );

  assert.equal(resetCalls, 0);
  assert.deepEqual(await harness.credentialStore.load(), credentials);
  assert.equal(await harness.lockStore.isLocked(), true);
  assert.equal(harness.invalidations(), 1);
});

test("显式服务端拒绝撤销 prepared 标记并保留现有注册", async () => {
  const rejection = new HbposApiError("denied", {
    kind: "envelope",
    code: "DEVICE_REGISTRATION_RESET_DENIED",
  });
  const harness = createHarness({
    resetRegistration: async () => {
      throw rejection;
    },
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);

  await assert.rejects(
    () => harness.subject.reset("EMPLOYEE-BARCODE"),
    (error: unknown) => error === rejection,
  );

  assert.deepEqual(await harness.credentialStore.load(), credentials);
  assert.equal(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), false);
});

test("显式服务端拒绝后 marker 删除失败时立即锁定当前进程", async () => {
  const rejection = new HbposApiError("denied", {
    kind: "envelope",
    code: "DEVICE_REGISTRATION_RESET_DENIED",
  });
  const harness = createHarness({
    resetRegistration: async () => {
      throw rejection;
    },
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);
  harness.markerStore.clear = async () => {
    throw new Error("marker clear failed");
  };

  await assert.rejects(
    () => harness.subject.reset("EMPLOYEE-BARCODE"),
    (error: unknown) => error === rejection,
  );

  assert.deepEqual(await harness.credentialStore.load(), credentials);
  assert.equal(await harness.lockStore.isLocked(), true);
  assert.equal(harness.invalidations(), 1);
});

test("marker 与持久锁同时失败后进程锁仍拒绝重新登录", async () => {
  const rejection = new HbposApiError("denied", {
    kind: "envelope",
    code: "DEVICE_REGISTRATION_RESET_DENIED",
  });
  const harness = createHarness({
    resetRegistration: async () => {
      throw rejection;
    },
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);
  harness.markerStore.clear = async () => {
    throw new Error("marker clear failed");
  };
  harness.lockStore.lock = async () => {
    throw new Error("device lock write failed");
  };

  await assert.rejects(
    () => harness.subject.reset("EMPLOYEE-BARCODE"),
    (error: unknown) => error === rejection,
  );
  await harness.lockStore.unlock();

  let loginCalls = 0;
  const cashierApi: CashierAuthenticationApi = {
    barcodeLogin: async () => {
      loginCalls += 1;
      throw new Error("locked login must not reach the API");
    },
  };
  const cashierCache = new CashierSessionCache(
    harness.secureStore,
    { sha256Hex: async (material) => material },
    { getAuthorizationFingerprint: async () => "device-fingerprint" },
  );
  const authentication = new CashierAuthenticationService(
    cashierApi,
    cashierCache,
    { isOnline: async () => true },
    harness.cashierAuthorization,
    harness.lockStore,
  );

  await assert.rejects(
    () => authentication.login({
      storeCode: credentials.storeCode,
      deviceCode: credentials.deviceCode,
      userBarcode: "ANOTHER-EMPLOYEE",
    }),
    (error: unknown) =>
      error instanceof HbposApiError && error.code === "DEVICE_LOCKED",
  );
  assert.equal(loginCalls, 0);
  assert.equal(harness.invalidations(), 1);
});

test("响应丢失保留 prepared 标记并锁定运行时等待匿名 verify", async () => {
  const harness = createHarness({
    resetRegistration: async () => {
      throw new HbposApiError("network lost", { kind: "transport" });
    },
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);

  await assert.rejects(() => harness.subject.reset("EMPLOYEE-BARCODE"));

  assert.equal((await harness.markerStore.load())?.phase, "prepared");
  assert.deepEqual(await harness.credentialStore.load(), credentials);
  assert.equal(await harness.lockStore.isLocked(), true);
  assert.equal(harness.invalidations(), 1);
});

test("服务端返回不匹配身份时按不确定结果恢复，不能撤销 marker 或继续营业", async () => {
  const harness = createHarness({
    resetRegistration: async () => ({
      ...resetResponse,
      deviceCode: "OTHER-DEVICE",
    }),
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);

  await assert.rejects(
    () => harness.subject.reset("EMPLOYEE-BARCODE"),
    (error: unknown) =>
      error instanceof HbposApiError &&
      error.code === "DEVICE_REGISTRATION_RESET_RESPONSE_INVALID",
  );

  assert.notEqual(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), true);
});

test("服务端成功后清除固定注册凭据但保留 installation ID 和其他安全数据", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);
  await harness.secureStore.set(
    "hbpos.ipad.sqlcipher-key.v1",
    "database-secret",
    { requireThisDeviceOnly: true },
  );

  const result = await harness.subject.reset("EMPLOYEE-BARCODE");

  assert.deepEqual(result, resetResponse);
  assert.equal(await harness.credentialStore.load(), null);
  assert.equal(await harness.presentationStore.load(), null);
  assert.equal(await harness.pendingStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), false);
  assert.equal(await harness.markerStore.load(), null);
  assert.equal(await harness.installation.getOrCreate(), hardwareId);
  assert.equal(
    await harness.secureStore.get("hbpos.ipad.sqlcipher-key.v1"),
    "database-secret",
  );
  assert.equal(harness.invalidations(), 1);
});

test("启动恢复仅在匿名 verify 确认精确设备已停用后继续本机清理", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: async (input) => ({
      deviceCode: input.deviceCode,
      storeCode: input.storeCode,
      deviceStatus: 0,
      isAllowed: false,
      message: "disabled",
      exactIdentityMatched: true,
    }) as DeviceVerifyResponse & { exactIdentityMatched: true },
  });
  await seedRegistered(harness);
  await harness.markerStore.save({
    version: 1,
    operationId,
    phase: "prepared",
    deviceCode: credentials.deviceCode,
    storeCode: credentials.storeCode,
    hardwareId,
    createdAtUtc: "2026-08-18T02:00:00.000Z",
  });
  await harness.lockStore.lockForRecovery("recovery-required");

  assert.equal(await harness.subject.recover(), "completed");
  assert.equal(await harness.credentialStore.load(), null);
  assert.equal(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), false);
});

test("匿名 verify 缺少 exactIdentityMatched 时保持 pending 并锁定", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: async (input) => ({
      deviceCode: input.deviceCode,
      storeCode: input.storeCode,
      deviceStatus: 0,
      isAllowed: false,
      message: "disabled",
    }),
  });
  await seedRegistered(harness);
  await harness.markerStore.save({
    version: 1,
    operationId,
    phase: "server-disabled",
    deviceCode: credentials.deviceCode,
    storeCode: credentials.storeCode,
    hardwareId,
    createdAtUtc: "2026-08-18T02:00:00.000Z",
  });

  assert.equal(await harness.subject.recover(), "pending");
  assert.deepEqual(await harness.credentialStore.load(), credentials);
  assert.notEqual(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), true);
});

test("匿名 verify 明确 exactIdentityMatched=false 时保持 pending 并锁定", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: async (input) =>
      ({
        deviceCode: input.deviceCode,
        storeCode: input.storeCode,
        deviceStatus: 0,
        isAllowed: false,
        message: "disabled",
        exactIdentityMatched: false,
      }) as DeviceVerifyResponse & { exactIdentityMatched: false },
  });
  await seedRegistered(harness);
  await harness.markerStore.save({
    version: 1,
    operationId,
    phase: "server-disabled",
    deviceCode: credentials.deviceCode,
    storeCode: credentials.storeCode,
    hardwareId,
    createdAtUtc: "2026-08-18T02:00:00.000Z",
  });

  assert.equal(await harness.subject.recover(), "pending");
  assert.deepEqual(await harness.credentialStore.load(), credentials);
  assert.notEqual(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), true);
});

test("marker 读取失败时立即锁定当前进程并保持 pending", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);
  harness.markerStore.load = async () => {
    throw new Error("marker load failed");
  };

  assert.equal(await harness.subject.recover(), "pending");
  assert.deepEqual(await harness.credentialStore.load(), credentials);
  assert.equal(await harness.lockStore.isLocked(), true);
  assert.equal(harness.invalidations(), 1);
});

test("不确定请求后设备仍启用但缺少精确身份证明，必须保留标记并锁定", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: async (input) => ({
      deviceCode: input.deviceCode,
      storeCode: input.storeCode,
      deviceStatus: 1,
      isAllowed: true,
      authorizationCode: "replacement-not-consumed",
    }),
  });
  await seedRegistered(harness);
  await harness.markerStore.save({
    version: 1,
    operationId,
    phase: "prepared",
    deviceCode: credentials.deviceCode,
    storeCode: credentials.storeCode,
    hardwareId,
    createdAtUtc: "2026-08-18T02:00:00.000Z",
  });

  assert.equal(await harness.subject.recover(), "pending");
  assert.deepEqual(await harness.credentialStore.load(), credentials);
  assert.notEqual(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), true);
});

test("启动恢复无法确认时保留标记并保持只读锁定", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: async () => {
      throw new HbposApiError("offline", { kind: "transport" });
    },
  });
  await seedRegistered(harness);
  await harness.markerStore.save({
    version: 1,
    operationId,
    phase: "server-disabled",
    deviceCode: credentials.deviceCode,
    storeCode: credentials.storeCode,
    hardwareId,
    createdAtUtc: "2026-08-18T02:00:00.000Z",
  });

  assert.equal(await harness.subject.recover(), "pending");
  assert.notEqual(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), true);
});

test("显式拒绝且 marker 确认不存在后，包装层判定时 marker.load 抛错 -> pending 且锁定/失效", async () => {
  const rejection = new HbposApiError("denied", {
    kind: "envelope",
    code: "DEVICE_REGISTRATION_RESET_DENIED",
  });
  const harness = createHarness({
    resetRegistration: async () => {
      throw rejection;
    },
    verify: async () => {
      throw new Error("not used");
    },
  });
  await seedRegistered(harness);

  // 服务端显式拒绝且 coordinator 清 marker 成功，reset 抛原错误。
  await assert.rejects(
    () => harness.subject.reset("EMPLOYEE-BARCODE"),
    (error: unknown) => error === rejection,
  );
  assert.equal(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), false);

  // 包装层判定恢复状态时 marker 读取失败：不能抛出让设备继续营业，
  // 必须 fail-closed 判定 pending 并立即锁定/失效当前收银员。
  harness.markerStore.load = async () => {
    throw new Error("marker load failed");
  };
  assert.equal(await harness.subject.isResetRecoveryPending(), true);
  assert.equal(await harness.lockStore.isLocked(), true);
  assert.equal(harness.invalidations(), 1);
});

// ---- 清除注册中断后的启动自愈：服务端精确终态决定清理还是恢复 ----

const recoveryLockReason = "DEVICE_REGISTRATION_RESET_RECOVERY_REQUIRED";
const deviceLockKey = "hbpos.ipad.device-lock.v1";

type VerifyInput = Parameters<DeviceRegistrationResetApi["verify"]>[0];

function marker(phase: "prepared" | "server-disabled" = "prepared") {
  return {
    version: 1 as const,
    operationId,
    phase,
    deviceCode: credentials.deviceCode,
    storeCode: credentials.storeCode,
    hardwareId,
    createdAtUtc: "2026-08-18T02:00:00.000Z",
  };
}

function exactVerify(overrides: Partial<DeviceVerifyResponse>) {
  return async (input: VerifyInput): Promise<DeviceVerifyResponse> => ({
    deviceCode: input.deviceCode,
    storeCode: input.storeCode,
    storeName: "testStore",
    deviceStatus: 1,
    isAllowed: true,
    exactIdentityMatched: true,
    ...overrides,
  });
}

/** 用真实 reset 流程制造“请求超时、结果未知”的中断现场。 */
async function interruptResetWithTransportError(
  harness: ReturnType<typeof createHarness>,
) {
  await assert.rejects(() => harness.subject.reset("EMPLOYEE-BARCODE"));
  assert.equal((await harness.markerStore.load())?.phase, "prepared");
  assert.equal(await harness.lockStore.isLocked(), true);
}

test("重置超时后服务端已精确停用：启动恢复完成清理并解锁", async () => {
  const verifyInputs: VerifyInput[] = [];
  const harness = createHarness({
    resetRegistration: async () => {
      throw new HbposApiError("timeout", { kind: "transport" });
    },
    verify: async (input) => {
      verifyInputs.push(input);
      return exactVerify({ deviceStatus: 0, isAllowed: false })(input);
    },
  });
  await seedRegistered(harness);
  await interruptResetWithTransportError(harness);

  assert.equal(await harness.subject.recover(), "completed");
  assert.deepEqual(verifyInputs, [
    {
      deviceCode: credentials.deviceCode,
      storeCode: credentials.storeCode,
      hardwareId,
    },
  ]);
  assert.equal(await harness.credentialStore.load(), null);
  assert.equal(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), false);
});

test("重置请求未到达服务端（仍启用且精确一致）：清除 marker、解除恢复锁并可重试清除", async () => {
  let resetCalls = 0;
  const harness = createHarness({
    resetRegistration: async () => {
      resetCalls += 1;
      if (resetCalls === 1) {
        throw new HbposApiError("network lost", { kind: "transport" });
      }
      return resetResponse;
    },
    verify: exactVerify({ authorizationCode: credentials.authorizationCode }),
  });
  await seedRegistered(harness);
  await interruptResetWithTransportError(harness);

  assert.equal(await harness.subject.recover(), "restored");
  assert.equal(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), false);
  assert.deepEqual(await harness.credentialStore.load(), credentials);

  // 设备回到正常状态后，收银员可以再次发起清除，并按正常路径完成。
  assert.deepEqual(
    await harness.subject.reset("EMPLOYEE-BARCODE"),
    resetResponse,
  );
  assert.equal(resetCalls, 2);
  assert.equal(await harness.credentialStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), false);
});

test("仍启用但另有其他原因的设备锁时只撤销本次重置的恢复锁，不误解除其他锁", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: exactVerify({}),
  });
  await seedRegistered(harness);
  await harness.markerStore.save(marker());
  await harness.lockStore.lock("Device is disabled.");

  assert.equal(await harness.subject.recover(), "restored");
  assert.equal(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), true);
  assert.equal(
    await harness.secureStore.get(deviceLockKey),
    "Device is disabled.",
  );
});

test("仍启用但 exactIdentityMatched=false 时保持 pending 并锁机", async () => {
  const harness = createHarness({
    resetRegistration: async () => resetResponse,
    verify: exactVerify({ exactIdentityMatched: false }),
  });
  await seedRegistered(harness);
  await harness.markerStore.save(marker());
  await harness.lockStore.lockForRecovery(recoveryLockReason);

  assert.equal(await harness.subject.recover(), "pending");
  assert.notEqual(await harness.markerStore.load(), null);
  assert.equal(await harness.lockStore.isLocked(), true);
});

test("身份不一致（门店不同）时无论启用还是停用都保持 pending 并锁机", async () => {
  for (const status of [
    { deviceStatus: 1, isAllowed: true },
    { deviceStatus: 0, isAllowed: false },
  ]) {
    const harness = createHarness({
      resetRegistration: async () => resetResponse,
      verify: async (input) =>
        exactVerify(status)({ ...input, storeCode: "9999" }),
    });
    await seedRegistered(harness);
    await harness.markerStore.save(marker());

    assert.equal(await harness.subject.recover(), "pending");
    assert.notEqual(await harness.markerStore.load(), null);
    assert.deepEqual(await harness.credentialStore.load(), credentials);
    assert.equal(await harness.lockStore.isLocked(), true);
  }
});

test("仍启用但本机凭据已缺失或不属于 marker 时保持 pending，不恢复残缺注册", async () => {
  for (const local of [null, { ...credentials, deviceCode: "OTHER-DEVICE" }]) {
    const harness = createHarness({
      resetRegistration: async () => resetResponse,
      verify: exactVerify({}),
    });
    await seedRegistered(harness);
    if (local) {
      await harness.credentialStore.save(local);
    } else {
      await harness.credentialStore.clear();
    }
    await harness.markerStore.save(marker("server-disabled"));
    await harness.lockStore.lockForRecovery(recoveryLockReason);

    assert.equal(await harness.subject.recover(), "pending");
    assert.notEqual(await harness.markerStore.load(), null);
    assert.equal(await harness.lockStore.isLocked(), true);
  }
});
