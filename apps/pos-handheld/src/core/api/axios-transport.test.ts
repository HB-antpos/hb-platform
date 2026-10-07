import assert from "node:assert/strict";
import test from "node:test";

import { AxiosError, create, type AxiosRequestConfig } from "axios";

import { SecurityApiCredentialProvider } from "../security/api-credential-provider";

import {
  createAxiosHbposTransport,
  createFreshCashierAxiosHbposTransport,
  type HbposRequestCredentials,
} from "./axios-transport";
import { HbposApiError, HbposCashierApi, HbposStoreApi } from "./hbpos-api";

function createDeferred<T>(): {
  promise: Promise<T>;
  resolve(value: T): void;
  reject(reason: unknown): void;
} {
  let resolvePromise: ((value: T) => void) | undefined;
  let rejectPromise: ((reason: unknown) => void) | undefined;
  const promise = new Promise<T>((resolve, reject) => {
    resolvePromise = resolve;
    rejectPromise = reject;
  });
  return {
    promise,
    resolve(value) {
      resolvePromise?.(value);
    },
    reject(reason) {
      rejectPromise?.(reason);
    },
  };
}

function settlesWithin<T>(request: Promise<T>, timeoutMs: number): Promise<T> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      reject(new Error(`request did not settle within ${timeoutMs}ms`));
    }, timeoutMs);
    request.then(
      (response) => {
        clearTimeout(timer);
        resolve(response);
      },
      (error: unknown) => {
        clearTimeout(timer);
        reject(error);
      },
    );
  });
}

test("Axios middleware 仅从安全凭据提供者附加设备和收银员认证头", async () => {
  let request: AxiosRequestConfig | undefined;
  let credentialReads = 0;
  const instance = create({
    adapter: async (config) => {
      request = config;
      return { config, status: 200, statusText: "OK", headers: {}, data: { success: true } };
    }
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      credentialReads += 1;
      return {
        device: {
          authorizationCode: "device-secret",
          deviceCode: "POS-001",
          storeCode: "1003",
          hardwareId: "INSTALL-001"
        },
        cashierAuthorization: "cashier-secret"
      };
    }
  }, instance);

  await transport.request({ method: "GET", url: "/api/v1/cashiers/session" });

  assert.equal(credentialReads, 1);
  assert.equal(request?.headers?.Authorization, "Bearer device-secret");
  assert.equal(request?.headers?.["X-HBPOS-Device-Code"], "POS-001");
  assert.equal(request?.headers?.["X-HBPOS-Store-Code"], "1003");
  assert.equal(request?.headers?.["X-HBPOS-Hardware-Id"], "INSTALL-001");
  assert.equal(request?.headers?.["X-HBPOS-Cashier-Authorization"], "cashier-secret");
});

test("普通请求显式提供的员工票据必须被安全提供者当前票据覆盖", async () => {
  let request: AxiosRequestConfig | undefined;
  const instance = create({
    adapter: async (config) => {
      request = config;
      return { config, status: 200, statusText: "OK", headers: {}, data: { success: true } };
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example",
    {
      async getCredentials() {
        return {
          device: {
            authorizationCode: "device-secret",
            deviceCode: "ANDROID-1042-01",
            storeCode: "1042",
            hardwareId: "INSTALL-001",
          },
          cashierAuthorization: "current-cashier-ticket",
        };
      },
    },
    instance,
  );

  await transport.request({
    method: "POST",
    url: "/api/v1/devices/reset-registration",
    headers: {
      "X-HBPOS-Cashier-Authorization": "fresh-online-ticket",
    },
  });

  assert.equal(
    request?.headers?.["X-HBPOS-Cashier-Authorization"],
    "current-cashier-ticket",
  );
});

test("设备重置专用 transport 保留刚在线登录的新鲜员工票据", async () => {
  let request: AxiosRequestConfig | undefined;
  const instance = create({
    adapter: async (config) => {
      request = config;
      return { config, status: 200, statusText: "OK", headers: {}, data: { success: true } };
    },
  });
  const transport = createFreshCashierAxiosHbposTransport(
    "https://hbpos.example",
    {
      async getCredentials() {
        return { cashierAuthorization: "current-cashier-ticket" };
      },
    },
    instance,
  );

  await transport.request({
    method: "POST",
    url: "/api/v1/devices/reset-registration",
    headers: { "X-HBPOS-Cashier-Authorization": "fresh-online-ticket" },
  });

  assert.equal(request?.headers?.["X-HBPOS-Cashier-Authorization"], "fresh-online-ticket");
});

test("Axios transport 向领域适配器暴露规范化响应头", async () => {
  const instance = create({
    adapter: async (config) => ({
      config,
      status: 200,
      statusText: "OK",
      headers: { "X-HBPOS-Allow-Transactions": "false" },
      data: { success: true },
    }),
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example",
    { async getCredentials() { return {}; } },
    instance,
  );

  const response = await transport.request({
    method: "GET",
    url: "/api/v1/app-updates/pos-handheld",
  });

  assert.equal(response.headers?.["x-hbpos-allow-transactions"], "false");
});

test("请求最终 origin 偏离已选 API 时在读取凭据前失败关闭", async () => {
  let credentialReads = 0;
  let adapterCalls = 0;
  const instance = create({
    adapter: async (config) => {
      adapterCalls += 1;
      return {
        config,
        status: 200,
        statusText: "OK",
        headers: {},
        data: { success: true },
      };
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example/api",
    {
      async getCredentials() {
        credentialReads += 1;
        return {
          device: {
            authorizationCode: "device-secret",
            deviceCode: "POS-001",
            storeCode: "1003",
            hardwareId: "INSTALL-001",
          },
        };
      },
    },
    instance,
  );

  await assert.rejects(
    () =>
      transport.request({
        method: "GET",
        url: "https://attacker.example/collect",
      }),
    (error: unknown) =>
      error instanceof Error &&
      /origin|trusted/i.test(error.message),
  );
  assert.equal(credentialReads, 0);
  assert.equal(adapterCalls, 0);
});

test("扩展请求只允许显式的 PUT、条件头和非 2xx 恢复状态", async () => {
  let request: AxiosRequestConfig | undefined;
  const instance = create({
    adapter: async (config) => {
      request = config;
      return {
        config,
        status: 304,
        statusText: "Not Modified",
        headers: {},
        data: null,
      };
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example",
    {
      async getCredentials() {
        return {};
      },
    },
    instance,
  );

  const response = await transport.request({
    method: "PUT",
    url: "/api/v1/attendance/signing-key",
    headers: { "If-None-Match": "\"keys-v3\"" },
    acceptedStatuses: [304, 409],
  });

  assert.equal(response.status, 304);
  assert.equal(request?.method, "put");
  assert.equal(request?.headers?.["If-None-Match"], "\"keys-v3\"");
  assert.equal(request?.validateStatus?.(200), true);
  assert.equal(request?.validateStatus?.(304), true);
  assert.equal(request?.validateStatus?.(409), true);
  assert.equal(request?.validateStatus?.(500), false);
});

test("明确设备撤销码无论由 401 或 403 返回都必须锁定设备", async () => {
  const calls: string[] = [];
  let status = 401;
  const instance = create({
    adapter: async (config) => {
      const error = new AxiosError("forbidden", "ERR_BAD_RESPONSE", config, undefined, {
        config,
        data: { errorCode: "DEVICE_DISABLED", message: "disabled" },
        headers: {},
        status,
        statusText: status === 401 ? "Unauthorized" : "Forbidden",
      });
      throw error;
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      return {};
    },
  }, instance, {
    async onUnauthorized() {
      calls.push("401");
    },
    async onForbidden() {
      calls.push("403");
    },
  });

  await assert.rejects(
    () => transport.request({ method: "GET", url: "/api/v1/orders" }),
    (error: unknown) => error instanceof Error && error.message === "disabled",
  );
  assert.deepEqual(calls, ["403"]);

  status = 403;
  await assert.rejects(
    () => transport.request({ method: "GET", url: "/api/v1/orders" }),
    (error: unknown) => error instanceof Error && error.message === "disabled",
  );
  assert.deepEqual(calls, ["403", "403"]);
});

test("权限、门店范围、新交易门禁和无错误码 403 都不得持久锁设备", async () => {
  const calls: string[] = [];
  let errorCode: string | undefined;
  const instance = create({
    adapter: async (config) => {
      throw new AxiosError("forbidden", "ERR_BAD_RESPONSE", config, undefined, {
        config,
        data: {
          ...(errorCode ? { errorCode } : {}),
          message: "access denied",
        },
        headers: {},
        status: 403,
        statusText: "Forbidden",
      });
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      return {};
    },
  }, instance, {
    async onUnauthorized() {
      calls.push("401");
    },
    async onForbidden() {
      calls.push("403-device-revoked");
    },
  });

  for (const code of [
    undefined,
    "CASHIER_PERMISSION_FORBIDDEN",
    "DEVICE_SCOPE_FORBIDDEN",
    "POS_IPAD_NEW_TRANSACTIONS_DISABLED",
  ]) {
    errorCode = code;
    await assert.rejects(
      () => transport.request({ method: "GET", url: "/api/v1/orders" }),
      /access denied/,
    );
  }

  assert.deepEqual(calls, []);
});

test("认证失效反馈自身失败也不能掩盖原始 HTTP 拒绝", async () => {
  const instance = create({
    adapter: async (config) => {
      throw new AxiosError("unauthorized", "ERR_BAD_RESPONSE", config, undefined, {
        config,
        data: { errorCode: "AUTH_EXPIRED", message: "login again" },
        headers: {},
        status: 401,
        statusText: "Unauthorized",
      });
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      return {};
    },
  }, instance, {
    async onUnauthorized() {
      throw new Error("secure storage temporarily unavailable");
    },
    async onForbidden() {},
  });

  await assert.rejects(
    () => transport.request({ method: "GET", url: "/api/v1/orders" }),
    (error: unknown) => error instanceof Error && error.message === "login again",
  );
});

test("条码登录业务拒绝不清理认证，只有明确设备撤销 403 才锁设备", async () => {
  const calls: string[] = [];
  let status = 401;
  let errorCode: string | undefined = "CASHIER_LOGIN_FAILED";
  const instance = create({
    adapter: async (config) => {
      throw new AxiosError("login rejected", "ERR_BAD_RESPONSE", config, undefined, {
        config,
        data: { ...(errorCode ? { errorCode } : {}), message: "invalid barcode" },
        headers: {},
        status,
        statusText: status === 401 ? "Unauthorized" : "Forbidden",
      });
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      return {};
    },
  }, instance, {
    async onUnauthorized() {
      calls.push("401");
    },
    async onForbidden() {
      calls.push("403");
    },
  });
  const cashiers = new HbposCashierApi(transport);

  await assert.rejects(
    () => cashiers.barcodeLogin({ storeCode: "1003", deviceCode: "POS-1", userBarcode: "INVALID" }),
    /invalid barcode/,
  );
  assert.deepEqual(calls, []);

  status = 403;
  await assert.rejects(
    () => cashiers.barcodeLogin({ storeCode: "1003", deviceCode: "POS-1", userBarcode: "INVALID" }),
    /invalid barcode/,
  );
  assert.deepEqual(calls, []);

  errorCode = "DEVICE_DISABLED";
  await assert.rejects(
    () => cashiers.barcodeLogin({ storeCode: "1003", deviceCode: "POS-1", userBarcode: "INVALID" }),
    /invalid barcode/,
  );
  assert.deepEqual(calls, ["403"]);

  status = 401;
  errorCode = "AUTH_EXPIRED";
  await assert.rejects(
    () => cashiers.barcodeLogin({ storeCode: "1003", deviceCode: "POS-1", userBarcode: "INVALID" }),
    /invalid barcode/,
  );
  assert.deepEqual(calls, ["403", "401"]);

  errorCode = undefined;
  await assert.rejects(
    () => cashiers.barcodeLogin({ storeCode: "1003", deviceCode: "POS-1", userBarcode: "INVALID" }),
    /invalid barcode/,
  );
  assert.deepEqual(calls, ["403", "401", "401"]);
});

test("非条码登录请求的 401 仍触发默认全局认证失效处理", async () => {
  const calls: string[] = [];
  const instance = create({
    adapter: async (config) => {
      throw new AxiosError("expired", "ERR_BAD_RESPONSE", config, undefined, {
        config,
        data: { errorCode: "AUTH_EXPIRED", message: "login again" },
        headers: {},
        status: 401,
        statusText: "Unauthorized",
      });
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      return {};
    },
  }, instance, {
    async onUnauthorized() {
      calls.push("401");
    },
    async onForbidden() {},
  });

  await assert.rejects(() => transport.request({ method: "GET", url: "/api/v1/orders" }), /login again/);
  assert.deepEqual(calls, ["401"]);
});

test("目录 timeout=0 保持 0，普通请求继承默认总预算并向 adapter 传递正的剩余值", async () => {
  const requests: AxiosRequestConfig[] = [];
  const instance = create({
    timeout: 15_000,
    adapter: async (config) => {
      requests.push(config);
      return {
        config,
        status: 200,
        statusText: "OK",
        headers: {},
        data: { success: true },
      };
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example",
    { async getCredentials() { return {}; } },
    instance,
  );
  const controller = new AbortController();

  await transport.request({
    method: "GET",
    url: "/api/v1/catalog/sellable-items/page",
    timeoutMs: 0,
    signal: controller.signal,
  });
  await transport.request({
    method: "GET",
    url: "/api/v1/orders",
  });

  const catalogRequest = requests[0];
  const normalRequest = requests[1];
  const normalRequestTimeout = normalRequest?.timeout;

  assert.equal(catalogRequest?.timeout, 0);
  assert.equal(catalogRequest?.signal, controller.signal);
  assert.ok(typeof normalRequestTimeout === "number");
  assert.ok(normalRequestTimeout > 0);
  assert.ok(normalRequestTimeout <= 15_000);
  assert.equal(normalRequest?.signal, undefined);
});

test("主动取消保留可识别错误且不得触发认证失效处理", async () => {
  const authenticationFailures: string[] = [];
  const instance = create({
    adapter: async (config) => {
      throw new AxiosError(
        "canceled",
        "ERR_CANCELED",
        config,
      );
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example",
    { async getCredentials() { return {}; } },
    instance,
    {
      async onUnauthorized() {
        authenticationFailures.push("401");
      },
      async onForbidden() {
        authenticationFailures.push("403");
      },
    },
  );
  const controller = new AbortController();

  await assert.rejects(
    () => transport.request({
      method: "GET",
      url: "/api/v1/catalog/sellable-items/page",
      signal: controller.signal,
      timeoutMs: 0,
    }),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.kind === "transport"
      && error.code === "REQUEST_ABORTED"
      && /cancel/i.test(error.message),
  );
  assert.deepEqual(authenticationFailures, []);
});

test("凭据读取等待中取消会立即停止派发，并保留 REQUEST_ABORTED", async () => {
  const credentials = createDeferred<HbposRequestCredentials>();
  let credentialReads = 0;
  let adapterCalls = 0;
  const instance = create({
    adapter: async (config) => {
      adapterCalls += 1;
      return { config, status: 200, statusText: "OK", headers: {}, data: {} };
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    getCredentials() {
      credentialReads += 1;
      return credentials.promise;
    },
  }, instance);
  const controller = new AbortController();
  const request = transport.request({
    method: "GET",
    url: "/api/v1/orders",
    signal: controller.signal,
    timeoutMs: 0,
  });

  await Promise.resolve();
  assert.equal(credentialReads, 1);
  controller.abort();
  await assert.rejects(
    () => settlesWithin(request, 50),
    (error: unknown) => error instanceof HbposApiError && error.code === "REQUEST_ABORTED",
  );
  assert.equal(adapterCalls, 0);

  credentials.resolve({});
  await Promise.resolve();
  assert.equal(adapterCalls, 0);
});

test("已取消的请求不会读取凭据或派发 HTTP", async () => {
  let credentialReads = 0;
  let adapterCalls = 0;
  const instance = create({
    adapter: async (config) => {
      adapterCalls += 1;
      return { config, status: 200, statusText: "OK", headers: {}, data: {} };
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      credentialReads += 1;
      return {};
    },
  }, instance);
  const controller = new AbortController();
  controller.abort();

  await assert.rejects(
    () => transport.request({ method: "GET", url: "/api/v1/orders", signal: controller.signal }),
    (error: unknown) => error instanceof HbposApiError && error.code === "REQUEST_ABORTED",
  );
  assert.equal(credentialReads, 0);
  assert.equal(adapterCalls, 0);
});

test("凭据读取超时沿用无响应语义，晚到结果不得派发 HTTP", async () => {
  const credentials = createDeferred<HbposRequestCredentials>();
  let adapterCalls = 0;
  const instance = create({
    adapter: async (config) => {
      adapterCalls += 1;
      return { config, status: 200, statusText: "OK", headers: {}, data: {} };
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    getCredentials() {
      return credentials.promise;
    },
  }, instance);

  await assert.rejects(
    () => settlesWithin(transport.request({ method: "GET", url: "/api/v1/orders", timeoutMs: 5 }), 50),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.code === "NO_HTTP_RESPONSE"
      && error.networkCode === "ECONNABORTED",
  );
  assert.equal(adapterCalls, 0);

  credentials.resolve({});
  await Promise.resolve();
  assert.equal(adapterCalls, 0);
});

test("凭据读取超时后晚拒绝也不得派发 HTTP", async () => {
  const credentials = createDeferred<HbposRequestCredentials>();
  let adapterCalls = 0;
  const instance = create({
    adapter: async (config) => {
      adapterCalls += 1;
      return { config, status: 200, statusText: "OK", headers: {}, data: {} };
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    getCredentials() {
      return credentials.promise;
    },
  }, instance);

  await assert.rejects(
    () => settlesWithin(transport.request({ method: "GET", url: "/api/v1/orders", timeoutMs: 5 }), 50),
    (error: unknown) => error instanceof HbposApiError && error.code === "NO_HTTP_RESPONSE",
  );
  credentials.reject(new Error("late credential failure"));
  await Promise.resolve();
  assert.equal(adapterCalls, 0);
});

test("有限 timeout 在凭据读取后仅向 adapter 传递剩余预算", async () => {
  const credentials = createDeferred<HbposRequestCredentials>();
  let adapterTimeout: number | undefined;
  let now = 1_000;
  const originalDateNow = Date.now;
  Date.now = () => now;
  try {
    const instance = create({
      adapter: async (config) => {
        adapterTimeout = config.timeout;
        return { config, status: 200, statusText: "OK", headers: {}, data: {} };
      },
    });
    const transport = createAxiosHbposTransport("https://hbpos.example", {
      getCredentials() {
        return credentials.promise;
      },
    }, instance);

    const request = transport.request({ method: "GET", url: "/api/v1/orders", timeoutMs: 100 });
    await Promise.resolve();
    now = 1_040;
    credentials.resolve({});

    await request;
    assert.equal(adapterTimeout, 60);
  } finally {
    Date.now = originalDateNow;
  }
});

test("凭据阶段耗尽有限 timeout 时以 ECONNABORTED 失败且不得派发 HTTP", async () => {
  const credentials = createDeferred<HbposRequestCredentials>();
  let adapterCalls = 0;
  let now = 1_000;
  const originalDateNow = Date.now;
  Date.now = () => now;
  try {
    const instance = create({
      adapter: async (config) => {
        adapterCalls += 1;
        return { config, status: 200, statusText: "OK", headers: {}, data: {} };
      },
    });
    const transport = createAxiosHbposTransport("https://hbpos.example", {
      getCredentials() {
        return credentials.promise;
      },
    }, instance);

    const request = transport.request({ method: "GET", url: "/api/v1/orders", timeoutMs: 100 });
    await Promise.resolve();
    now = 1_100;
    credentials.resolve({});

    await assert.rejects(
      () => request,
      (error: unknown) =>
        error instanceof HbposApiError
        && error.code === "NO_HTTP_RESPONSE"
        && error.networkCode === "ECONNABORTED",
    );
    assert.equal(adapterCalls, 0);
  } finally {
    Date.now = originalDateNow;
  }
});

test("无 HTTP 响应（ERR_NETWORK）时抛出可读中文提示并携带 networkCode", async () => {
  const instance = create({
    adapter: async (config) => {
      throw new AxiosError("Network Error", "ERR_NETWORK", config);
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      return {};
    },
  }, instance);

  await assert.rejects(
    () => transport.request({ method: "GET", url: "/api/v1/advertisements/active" }),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.kind === "transport"
      && error.code === "NO_HTTP_RESPONSE"
      && error.networkCode === "ERR_NETWORK"
      && error.message.includes("网络连接失败"),
  );
});

test("无 HTTP 响应（ECONNREFUSED）时提示服务器未启动", async () => {
  const instance = create({
    adapter: async (config) => {
      throw new AxiosError(
        "connect ECONNREFUSED 192.168.31.246:5159",
        "ECONNREFUSED",
        config,
      );
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      return {};
    },
  }, instance);

  await assert.rejects(
    () => transport.request({ method: "GET", url: "/api/v1/health" }),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.networkCode === "ECONNREFUSED"
      && error.message.includes("无法连接服务器"),
  );
});

test("非 axios 异常（TRANSPORT_UNEXPECTED）携带独立错误码与中性文案", async () => {
  const instance = create({
    adapter: async () => {
      // 非 AxiosError（如 interceptor 凭证读取失败等内部故障）。
      throw new Error("keychain read failed");
    },
  });
  const transport = createAxiosHbposTransport("https://hbpos.example", {
    async getCredentials() {
      return {};
    },
  }, instance);

  await assert.rejects(
    () => transport.request({ method: "GET", url: "/api/v1/health" }),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.kind === "transport"
      && error.code === "TRANSPORT_UNEXPECTED"
      && error.message.includes("请求未能完成"),
  );
});

test("总部下发小票资料 sync/ack 经通用 transport 发出：没有收银员票据时只带设备认证头，不因缺少票据而拒绝发请求", async () => {
  const requests: AxiosRequestConfig[] = [];
  const instance = create({
    adapter: async (config) => {
      requests.push(config);
      return {
        config,
        status: 200,
        statusText: "OK",
        headers: {},
        data: {
          success: true,
          data: config.method === "get" ? { changed: false, version: 0 } : { appliedVersion: 3 },
        },
      };
    },
  });
  // 真实的 SecurityApiCredentialProvider：有设备凭据、钥匙串里没有收银员票据（没人登录）。
  const credentialProvider = new SecurityApiCredentialProvider(
    {
      async getTransportCredentials() {
        return {
          authorizationCode: "device-secret",
          deviceCode: "POS-001",
          storeCode: "1003",
          hardwareId: "INSTALL-001",
        };
      },
    } as never,
    { async get() { return null; } } as never,
  );
  const api = new HbposStoreApi(
    createAxiosHbposTransport("https://hbpos.example", credentialProvider, instance),
  );

  await api.syncReceiptProfile(2);
  await api.ackReceiptProfile(3);

  assert.equal(requests.length, 2, "没有收银员票据时 transport 照常发出请求");
  assert.equal(requests[0]?.url, "/api/v1/stores/current/receipt-profile/sync");
  assert.deepEqual(requests[0]?.params, { knownVersion: 2 });
  assert.equal(requests[1]?.url, "/api/v1/stores/current/receipt-profile/ack");
  assert.deepEqual(JSON.parse(String(requests[1]?.data)), { version: 3 });
  for (const request of requests) {
    assert.equal(request.headers?.Authorization, "Bearer device-secret");
    assert.equal(request.headers?.["X-HBPOS-Device-Code"], "POS-001");
    assert.equal(request.headers?.["X-HBPOS-Store-Code"], "1003");
    assert.equal(request.headers?.["X-HBPOS-Hardware-Id"], "INSTALL-001");
    assert.equal(request.headers?.["X-HBPOS-Cashier-Authorization"], undefined);
  }
});
