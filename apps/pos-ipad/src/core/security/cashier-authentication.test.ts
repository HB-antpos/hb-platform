import assert from "node:assert/strict";
import test from "node:test";

import { HbposApiError } from "../api/hbpos-api";

import { CashierAuthenticationService, type CashierAuthenticationApi } from "./cashier-authentication";
import {
  CashierAuthorizationStore,
  CashierSessionCache,
  DeviceLockStore,
  InMemorySecureStore,
  type CashierSessionKeyHasher,
} from "./secure-storage";

const session = {
  cashierId: "CASHIER-1",
  userGuid: "USER-1",
  cashierName: "Alice",
  storeCode: "1003",
  deviceCode: "POS_1003_1011",
  authorizationToken: "cashier-ticket",
  authorizationExpiresAtUtc: "2099-07-29T00:00:00.000Z",
};

const cacheKeyHasher: CashierSessionKeyHasher = {
  async sha256Hex() {
    return "a".repeat(64);
  },
};
const cashierCacheNamespace = {
  async getAuthorizationFingerprint() {
    return "device-auth-fingerprint";
  },
};

test("已知离线只读取同门店同设备同条码缓存，不请求 API", async () => {
  const cache = new CashierSessionCache(new InMemorySecureStore(), cacheKeyHasher, cashierCacheNamespace);
  await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", session);
  let calls = 0;
  const api: CashierAuthenticationApi = {
    async barcodeLogin() {
      calls++;
      throw new Error("not used");
    }
  };
  const service = new CashierAuthenticationService(api, cache, { isOnline: async () => false });

  const result = await service.login({
    storeCode: "1003",
    deviceCode: "POS_1003_1011",
    userBarcode: "CASHIER-BARCODE"
  });

  assert.equal(result.source, "offline-cache");
  assert.equal(result.session.cashierId, "CASHIER-1");
  assert.equal(calls, 0);
});

test("在线成功刷新加密缓存，传输失败才可回退缓存", async () => {
  const cache = new CashierSessionCache(new InMemorySecureStore(), cacheKeyHasher, cashierCacheNamespace);
  await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", { ...session, cashierName: "Cached Alice" });
  const api: CashierAuthenticationApi = {
    async barcodeLogin() {
      throw new HbposApiError("network unavailable", { kind: "transport" });
    }
  };
  const service = new CashierAuthenticationService(api, cache, { isOnline: async () => true });

  const result = await service.login({
    storeCode: "1003",
    deviceCode: "POS_1003_1011",
    userBarcode: "CASHIER-BARCODE"
  });

  assert.equal(result.source, "offline-cache");
  assert.equal(result.session.cashierName, "Cached Alice");
});

test("过期在线票据不使普通离线缓存失效，但不会成为后续 API bearer", async () => {
  const secureStore = new InMemorySecureStore();
  const cache = new CashierSessionCache(
    secureStore,
    cacheKeyHasher,
    cashierCacheNamespace,
  );
  await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", {
    ...session,
    authorizationExpiresAtUtc: "2026-07-27T00:00:00.000Z",
  });
  const authorization = new CashierAuthorizationStore(
    secureStore,
    {
      getSystemUptimeMilliseconds: () => 10_000,
      nowEpochMs: () =>
        Date.parse("2026-07-28T00:00:00.000Z"),
    },
  );
  const service = new CashierAuthenticationService(
    {
      async barcodeLogin() {
        throw new Error("offline path must not call API");
      },
    },
    cache,
    { isOnline: async () => false },
    authorization,
  );

  const result = await service.login({
    storeCode: "1003",
    deviceCode: "POS_1003_1011",
    userBarcode: "CASHIER-BARCODE",
  });

  assert.equal(result.source, "offline-cache");
  assert.equal(result.session.cashierId, "CASHIER-1");
  assert.equal(
    await authorization.get({ storeCode: "1003", deviceCode: "POS_1003_1011" }),
    null,
  );
});

test("在线成功用同门店同设备同条码覆盖缓存并激活收银员票据", async () => {
  const secureStore = new InMemorySecureStore();
  const cache = new CashierSessionCache(secureStore, cacheKeyHasher, cashierCacheNamespace);
  const api: CashierAuthenticationApi = {
    async barcodeLogin() {
      return session;
    }
  };
  const authorization = new CashierAuthorizationStore(
    secureStore,
    {
      getSystemUptimeMilliseconds: () => 10_000,
      nowEpochMs: () =>
        Date.parse("2026-07-28T10:00:00.000Z"),
    },
  );
  const service = new CashierAuthenticationService(api, cache, { isOnline: async () => true }, authorization);

  const result = await service.login({
    storeCode: "1003",
    deviceCode: "POS_1003_1011",
    userBarcode: "CASHIER-BARCODE"
  });

  assert.equal(result.source, "online");
  assert.deepEqual(
    await cache.load("1003", "POS_1003_1011", "CASHIER-BARCODE"),
    session
  );
  assert.equal(
    await authorization.get({ storeCode: "1003", deviceCode: "POS_1003_1011" }),
    "cashier-ticket",
  );
});

test("在线明确拒绝绝不回退缓存", async () => {
  const cache = new CashierSessionCache(new InMemorySecureStore(), cacheKeyHasher, cashierCacheNamespace);
  await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", session);
  const api: CashierAuthenticationApi = {
    async barcodeLogin() {
      throw new HbposApiError("denied", { kind: "http", status: 401, code: "CASHIER_LOGIN_FAILED" });
    }
  };
  const service = new CashierAuthenticationService(api, cache, { isOnline: async () => true });

  await assert.rejects(
    () => service.login({
      storeCode: "1003",
      deviceCode: "POS_1003_1011",
      userBarcode: "CASHIER-BARCODE"
    }),
    (error: unknown) => error instanceof HbposApiError && error.kind === "http"
  );
});

test("设备注册重置验票必须在线且不允许回退离线缓存", async () => {
  const secureStore = new InMemorySecureStore();
  const cache = new CashierSessionCache(secureStore, cacheKeyHasher, cashierCacheNamespace);
  await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", session);
  let apiCalls = 0;
  const service = new CashierAuthenticationService(
    {
      async barcodeLogin() {
        apiCalls += 1;
        throw new HbposApiError("network unavailable", { kind: "transport" });
      },
    },
    cache,
    { isOnline: async () => true },
  );

  await assert.rejects(
    () =>
      service.loginOnlineOnly({
        storeCode: "1003",
        deviceCode: "POS_1003_1011",
        userBarcode: "CASHIER-BARCODE",
      }),
    (error: unknown) =>
      error instanceof HbposApiError && error.kind === "transport",
  );
  assert.equal(apiCalls, 1);
});

test("设备注册重置验票拒绝断网、紧急二维码且不激活当前收银员授权", async () => {
  const secureStore = new InMemorySecureStore();
  const cache = new CashierSessionCache(secureStore, cacheKeyHasher, cashierCacheNamespace);
  const authorization = new CashierAuthorizationStore(secureStore);
  let online = false;
  let apiCalls = 0;
  const service = new CashierAuthenticationService(
    {
      async barcodeLogin() {
        apiCalls += 1;
        return session;
      },
    },
    cache,
    { isOnline: async () => online },
    authorization,
  );

  await assert.rejects(
    () =>
      service.loginOnlineOnly({
        storeCode: "1003",
        deviceCode: "POS_1003_1011",
        userBarcode: "CASHIER-BARCODE",
      }),
    (error: unknown) =>
      error instanceof HbposApiError && error.code === "ONLINE_LOGIN_REQUIRED",
  );
  online = true;
  await assert.rejects(
    () =>
      service.loginOnlineOnly({
        storeCode: "1003",
        deviceCode: "POS_1003_1011",
        userBarcode: "HBPOSE2-signed-token",
      }),
    (error: unknown) =>
      error instanceof HbposApiError && error.code === "REAL_EMPLOYEE_BARCODE_REQUIRED",
  );

  const result = await service.loginOnlineOnly({
    storeCode: "1003",
    deviceCode: "POS_1003_1011",
    userBarcode: "CASHIER-BARCODE",
  });

  assert.equal(result.source, "online");
  assert.equal(apiCalls, 1);
  assert.equal(
    await cache.load("1003", "POS_1003_1011", "CASHIER-BARCODE"),
    null,
  );
  assert.equal(
    await authorization.get({ storeCode: "1003", deviceCode: "POS_1003_1011" }),
    null,
  );
});

test("已知设备禁用后，离线收银员缓存不得绕过锁定", async () => {
  const secureStore = new InMemorySecureStore();
  const cache = new CashierSessionCache(secureStore, cacheKeyHasher, cashierCacheNamespace);
  await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", session);
  const deviceLock = new DeviceLockStore(secureStore);
  await deviceLock.lock("Device is disabled.");
  const api: CashierAuthenticationApi = {
    async barcodeLogin() {
      throw new Error("offline path must not call API");
    }
  };
  const service = new CashierAuthenticationService(api, cache, { isOnline: async () => false }, undefined, deviceLock);

  await assert.rejects(
    () => service.login({
      storeCode: "1003",
      deviceCode: "POS_1003_1011",
      userBarcode: "CASHIER-BARCODE"
    }),
    (error: unknown) => error instanceof HbposApiError && error.code === "DEVICE_LOCKED"
  );
});

test("在线 408、429 和 5xx 视为可恢复故障，普通与主管登录都可回退同一加密缓存", async () => {
  for (const status of [408, 429, 500, 503]) {
    const cache = new CashierSessionCache(new InMemorySecureStore(), cacheKeyHasher, cashierCacheNamespace);
    await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", session);
    const api: CashierAuthenticationApi = {
      async barcodeLogin() {
        throw new HbposApiError("temporary failure", { kind: "http", status });
      },
    };
    const service = new CashierAuthenticationService(api, cache, { isOnline: async () => true });

    const result = await service.login({
      storeCode: "1003",
      deviceCode: "POS_1003_1011",
      userBarcode: "CASHIER-BARCODE",
    });
    assert.equal(result.source, "offline-cache", `HTTP ${status} must use the offline cache`);
  }
});

test("在线遇网关 404/405 或 2xx 非 ApiResult 信封按服务不可用回退离线缓存（WPF #606/#607）", async () => {
  const failures = [
    new HbposApiError("Request failed with status code 404", { kind: "http", status: 404, apiEnvelope: false }),
    new HbposApiError("Request failed with status code 405", { kind: "http", status: 405, apiEnvelope: false }),
    new HbposApiError("Hbpos API response is not an ApiResult envelope.", { kind: "envelope", apiEnvelope: false }),
  ];
  for (const failure of failures) {
    const cache = new CashierSessionCache(new InMemorySecureStore(), cacheKeyHasher, cashierCacheNamespace);
    await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", session);
    const api: CashierAuthenticationApi = {
      async barcodeLogin() {
        throw failure;
      },
    };
    const service = new CashierAuthenticationService(api, cache, { isOnline: async () => true });

    const result = await service.login({
      storeCode: "1003",
      deviceCode: "POS_1003_1011",
      userBarcode: "CASHIER-BARCODE",
    });
    assert.equal(result.source, "offline-cache", `${failure.kind} ${failure.status ?? "2xx"} must use the offline cache`);
  }
});

test("在线其他 4xx 和 envelope 业务拒绝绝不回退缓存", async () => {
  const rejections = [
    new HbposApiError("bad request", { kind: "http", status: 400 }),
    new HbposApiError("denied", { kind: "http", status: 403 }),
    // 与 WPF #606 同口径：登录遇 403 无论正文是否为信封（含网关 HTML 403）都按在线拒绝，不回退缓存。
    new HbposApiError("Request failed with status code 403", { kind: "http", status: 403, apiEnvelope: false }),
    new HbposApiError("business denied", { kind: "envelope", code: "CASHIER_LOGIN_FAILED" }),
    new HbposApiError("business denied", { kind: "envelope", code: "CASHIER_LOGIN_FAILED", apiEnvelope: true }),
  ];
  for (const rejection of rejections) {
    const cache = new CashierSessionCache(new InMemorySecureStore(), cacheKeyHasher, cashierCacheNamespace);
    await cache.save("1003", "POS_1003_1011", "CASHIER-BARCODE", session);
    const api: CashierAuthenticationApi = {
      async barcodeLogin() {
        throw rejection;
      },
    };
    const service = new CashierAuthenticationService(api, cache, { isOnline: async () => true });

    await assert.rejects(
      () => service.login({
        storeCode: "1003",
        deviceCode: "POS_1003_1011",
        userBarcode: "CASHIER-BARCODE",
      }),
      (error: unknown) => error === rejection,
    );
  }
});

test("HBPOSE 紧急二维码在普通 API 与离线缓存之前分流且不写普通收银员缓存", async () => {
  const secureStore = new InMemorySecureStore();
  const cache = new CashierSessionCache(
    secureStore,
    cacheKeyHasher,
    cashierCacheNamespace,
  );
  const authorization = new CashierAuthorizationStore(
    secureStore,
    {
      getSystemUptimeMilliseconds: () => 10_000,
      nowEpochMs: () =>
        Date.parse("2026-07-28T10:00:00.000Z"),
    },
  );
  let apiCalls = 0;
  const emergencyCalls: string[] = [];
  const service = new CashierAuthenticationService(
    {
      async barcodeLogin() {
        apiCalls += 1;
        throw new Error("emergency token must not reach barcode API");
      },
    },
    cache,
    { isOnline: async () => false },
    authorization,
    undefined,
    {
      permissionCodes: [
        "Permissions.PosTerminal.Sales.View",
        "Permissions.PosTerminal.Payment.TakeCash",
      ],
      service: {
        async verifyAndActivate(token, device) {
          emergencyCalls.push(
            `${token}:${device.storeCode}:${device.deviceCode}`,
          );
          await authorization.set({
            authorizationToken: token,
            expiresAtEpochMs: Date.parse(
              "2026-07-28T10:01:00.000Z",
            ),
            source: "emergency-override",
            scope: device,
            systemUptimeMs: 10_000,
            trustedNowEpochMs: Date.parse(
              "2026-07-28T10:00:00.000Z",
            ),
          });
          return {
            ok: true as const,
            emergencyGrantId:
              "10000000-0000-4000-8000-000000000001",
            expiresAtEpochMs: Date.parse(
              "2026-07-28T10:01:00.000Z",
            ),
            systemUptimeMs: 10_000,
            trustedNowEpochMs: Date.parse(
              "2026-07-28T10:00:00.000Z",
            ),
          };
        },
      },
    },
  );

  const result = await service.login({
    storeCode: "S1",
    deviceCode: "IPAD-1",
    userBarcode: "HBPOSE2-signed-token",
  });

  assert.equal(apiCalls, 0);
  assert.deepEqual(emergencyCalls, [
    "HBPOSE2-signed-token:S1:IPAD-1",
  ]);
  assert.equal(result.source, "emergency-override");
  assert.deepEqual(result.emergencyTiming, {
    systemUptimeMs: 10_000,
    trustedNowEpochMs: Date.parse(
      "2026-07-28T10:00:00.000Z",
    ),
  });
  assert.deepEqual(result.session, {
    cashierId: "EMERGENCY:10000000000040008000000000000001",
    userGuid: "EMERGENCY:10000000000040008000000000000001",
    cashierName: "EMERGENCY",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    roles: ["EmergencyOverride"],
    permissionCodes: [
      "Permissions.PosTerminal.Payment.TakeCash",
      "Permissions.PosTerminal.Sales.View",
    ],
    allowedStoreCodes: ["S1"],
    isSuperAdmin: false,
    isOfflineCached: false,
    isEmergencyOverride: true,
    authorizationToken: "HBPOSE2-signed-token",
    authorizationExpiresAtUtc: "2026-07-28T10:01:00.000Z",
    emergencyGrantId:
      "10000000-0000-4000-8000-000000000001",
  });
  assert.equal(
    await cache.load("S1", "IPAD-1", "HBPOSE2-signed-token"),
    null,
  );
  assert.equal(
    await authorization.get({ storeCode: "S1", deviceCode: "IPAD-1" }),
    "HBPOSE2-signed-token",
  );
});

test("紧急二维码拒绝不回退普通缓存，也不触发条码 API", async () => {
  let apiCalls = 0;
  const secureStore = new InMemorySecureStore();
  const service = new CashierAuthenticationService(
    {
      async barcodeLogin() {
        apiCalls += 1;
        return session;
      },
    },
    new CashierSessionCache(secureStore, cacheKeyHasher, cashierCacheNamespace),
    { isOnline: async () => true },
    new CashierAuthorizationStore(secureStore),
    undefined,
    {
      permissionCodes: ["Permissions.PosTerminal.Sales.View"],
      service: {
        async verifyAndActivate() {
          return {
            ok: false as const,
            errorCode: "EMERGENCY_CLOCK_ROLLBACK",
          };
        },
      },
    },
  );

  await assert.rejects(
    () =>
      service.login({
        storeCode: "S1",
        deviceCode: "IPAD-1",
        userBarcode: "HBPOSE1-rejected",
      }),
    (error: unknown) =>
      error instanceof HbposApiError &&
      error.code === "EMERGENCY_CLOCK_ROLLBACK",
  );
  assert.equal(apiCalls, 0);
});
