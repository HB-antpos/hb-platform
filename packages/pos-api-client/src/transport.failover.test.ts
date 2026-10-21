/**
 * createAxiosHbposTransport 的双入口故障转移集成测试：
 * 用 axios 自定义 adapter 模拟「某个入口连不上 / 返回 524 / 慢」，验证入口选择、重试与凭据头。
 */
import assert from "node:assert/strict";
import test from "node:test";

import { AxiosError, create, type InternalAxiosRequestConfig } from "axios";

import { HbposApiError } from "./transport-contracts";
import { createAxiosHbposTransport } from "./transport";
import { ApiTransportSelector } from "./transport-failover";

const IDENTITY_BASE_URL = "https://hotbargain.vip/pos-api";
const EDGE = "pos.hotbargain.vip";
const DIRECT = "hotbargain.vip";
const POLICY = { cashierHeader: "override-explicit", responseHeaders: "omit" } as const;

const credentials = {
  async getCredentials() {
    return {
      device: {
        authorizationCode: "device-auth",
        deviceCode: "D-001",
        storeCode: "S-001",
        hardwareId: "HW-001",
      },
      cashierAuthorization: "cashier-ticket",
    };
  },
};

type Seen = { host: string; method: string; authorization: string | undefined };
type Behavior = (config: InternalAxiosRequestConfig, host: string) => unknown;

function setup(behavior: Behavior, options: { now?: () => number } = {}) {
  const seen: Seen[] = [];
  const selector = new ApiTransportSelector(options);
  const instance = create({
    baseURL: IDENTITY_BASE_URL,
    adapter: async (config) => {
      const host = new URL(config.baseURL ?? "").hostname;
      seen.push({
        host,
        method: String(config.method),
        authorization: config.headers.get("Authorization")?.toString(),
      });
      const outcome = behavior(config, host);
      if (outcome instanceof Error) {
        throw outcome;
      }
      return { config, status: 200, statusText: "OK", headers: {}, data: outcome ?? { ok: true } };
    },
  });
  const transport = createAxiosHbposTransport(
    IDENTITY_BASE_URL,
    credentials,
    instance,
    undefined,
    POLICY,
    { selector },
  );
  return { seen, selector, transport };
}

function networkError(config: InternalAxiosRequestConfig) {
  return new AxiosError("Network Error", "ERR_NETWORK", config);
}

function statusError(config: InternalAxiosRequestConfig, status: number) {
  return new AxiosError(
    `Request failed with status code ${status}`,
    "ERR_BAD_RESPONSE",
    config,
    null,
    { status, statusText: "", headers: {}, config, data: {} },
  );
}

test("默认走边缘入口，凭据头照常注入", async () => {
  const { seen, selector, transport } = setup(() => ({ ok: true }));
  await transport.request({ method: "GET", url: "/api/v1/health" });
  assert.deepEqual(seen.map((item) => item.host), [EDGE]);
  assert.equal(seen[0]?.authorization, "Bearer device-auth");
  assert.equal(selector.isUsingFallback(), false);
});

test("GET 在边缘入口连接失败：当场改走直连重试一次，凭据头不重复", async () => {
  const { seen, selector, transport } = setup((config, host) =>
    host === EDGE ? networkError(config) : { ok: true, from: host },
  );
  const response = await transport.request<{ from: string }>({ method: "GET", url: "/api/v1/catalog" });
  assert.equal(response.data.from, DIRECT);
  assert.deepEqual(seen.map((item) => item.host), [EDGE, DIRECT]);
  // 两次派发各注入一次，没有出现 "Bearer x, Bearer x" 这种叠加。
  assert.deepEqual(seen.map((item) => item.authorization), ["Bearer device-auth", "Bearer device-auth"]);
  assert.equal(selector.isUsingFallback(), true);

  // 冷却期内的后续请求直接走直连，不再先碰边缘入口。
  await transport.request({ method: "GET", url: "/api/v1/catalog" });
  assert.deepEqual(seen.map((item) => item.host), [EDGE, DIRECT, DIRECT]);
});

test("GET 收到边缘 524：改走直连重试一次", async () => {
  const { seen, transport } = setup((config, host) =>
    host === EDGE ? statusError(config, 524) : { ok: true },
  );
  await transport.request({ method: "GET", url: "/api/v1/reports/slow" });
  assert.deepEqual(seen.map((item) => item.host), [EDGE, DIRECT]);
});

test("POST 绝不自动重试，但失败会让后续请求改走直连", async () => {
  const { seen, selector, transport } = setup((config, host) =>
    host === EDGE ? networkError(config) : { ok: true },
  );
  await assert.rejects(
    transport.request({ method: "POST", url: "/api/v1/orders/sync", data: { orderGuid: "o-1" } }),
    (error) =>
      error instanceof HbposApiError &&
      error.code === "NO_HTTP_RESPONSE" &&
      error.networkCode === "ERR_NETWORK",
  );
  assert.deepEqual(seen.map((item) => item.host), [EDGE], "POST 只发一次");
  assert.equal(selector.isUsingFallback(), true);

  await transport.request({ method: "POST", url: "/api/v1/orders/sync", data: { orderGuid: "o-1" } });
  assert.deepEqual(seen.map((item) => item.host), [EDGE, DIRECT], "后续请求已改走直连");
});

test("业务错误（401/500）说明线路是通的：不切换、不重试，错误原样抛出", async () => {
  const { seen, selector, transport } = setup((config) => statusError(config, 500));
  await assert.rejects(
    transport.request({ method: "GET", url: "/api/v1/catalog" }),
    (error) => error instanceof HbposApiError && error.status === 500,
  );
  assert.deepEqual(seen.map((item) => item.host), [EDGE]);
  assert.equal(selector.isUsingFallback(), false);
});

test("超时：边缘入口刚成功过则视为服务器慢，不切换不重试", async () => {
  let current = 1_000_000;
  let slow = false;
  const { seen, selector, transport } = setup(
    (config) => (slow ? new AxiosError("timeout", "ECONNABORTED", config) : { ok: true }),
    { now: () => current },
  );
  await transport.request({ method: "GET", url: "/api/v1/catalog" });
  slow = true;
  current += 10_000;
  await assert.rejects(
    transport.request({ method: "GET", url: "/api/v1/catalog" }),
    (error) => error instanceof HbposApiError && error.networkCode === "ECONNABORTED",
  );
  assert.deepEqual(seen.map((item) => item.host), [EDGE, EDGE]);
  assert.equal(selector.isUsingFallback(), false);
});

test("超时：边缘入口很久没成功过则判定线路黑洞，GET 改走直连重试", async () => {
  const { seen, selector, transport } = setup((config, host) =>
    host === EDGE ? new AxiosError("timeout", "ECONNABORTED", config) : { ok: true },
  );
  await transport.request({ method: "GET", url: "/api/v1/catalog" });
  assert.deepEqual(seen.map((item) => item.host), [EDGE, DIRECT]);
  assert.equal(selector.isUsingFallback(), true);
});

test("调用方取消的请求不算线路故障", async () => {
  const controller = new AbortController();
  const { seen, selector, transport } = setup((config) => new AxiosError("canceled", "ERR_CANCELED", config));
  await assert.rejects(
    transport.request({ method: "GET", url: "/api/v1/catalog", signal: controller.signal }),
    (error) => error instanceof HbposApiError && error.code === "REQUEST_ABORTED",
  );
  assert.deepEqual(seen.map((item) => item.host), [EDGE]);
  assert.equal(selector.isUsingFallback(), false);
});

test("钥匙串读取超时是本地故障，不会被误判成线路故障", async () => {
  const selector = new ApiTransportSelector();
  const instance = create({
    baseURL: IDENTITY_BASE_URL,
    adapter: async (config) => ({ config, status: 200, statusText: "OK", headers: {}, data: {} }),
  });
  const transport = createAxiosHbposTransport(
    IDENTITY_BASE_URL,
    { getCredentials: () => new Promise(() => undefined) },
    instance,
    undefined,
    POLICY,
    { selector },
  );
  await assert.rejects(
    transport.request({ method: "GET", url: "/api/v1/catalog", timeoutMs: 20 }),
    (error) => error instanceof HbposApiError && error.networkCode === "ECONNABORTED",
  );
  assert.equal(selector.isUsingFallback(), false);
});

test("来源校验：两个入口都受信，其他来源仍被拒绝", async () => {
  const { seen, transport } = setup(() => ({ ok: true }));
  await transport.request({ method: "GET", url: "/api/v1/health" });
  assert.equal(seen.length, 1);

  // 带绝对地址的请求指向别的域名：必须拒绝，凭据不能发出去。
  await assert.rejects(
    transport.request({ method: "GET", url: "https://evil.example/pos-api/api/v1/health" }),
    (error) => error instanceof HbposApiError && error.code === "UNTRUSTED_API_ORIGIN",
  );
  assert.equal(seen.length, 1);
});

test("不在入口组内的地址（局域网、其他服务器）即使传了 failover 也完全不变", async () => {
  const seen: string[] = [];
  const selector = new ApiTransportSelector();
  const instance = create({
    baseURL: "http://192.168.31.247:5003",
    adapter: async (config) => {
      seen.push(String(config.baseURL));
      throw new AxiosError("Network Error", "ERR_NETWORK", config);
    },
  });
  const transport = createAxiosHbposTransport(
    "http://192.168.31.247:5003",
    credentials,
    instance,
    undefined,
    POLICY,
    { selector },
  );
  await assert.rejects(
    transport.request({ method: "GET", url: "/api/v1/health" }),
    (error) => error instanceof HbposApiError && error.code === "NO_HTTP_RESPONSE",
  );
  assert.deepEqual(seen, ["http://192.168.31.247:5003"], "只发一次，baseURL 不改写");
  assert.equal(selector.isUsingFallback(), false);
});

test("入口组地址但没传 failover：与改动前一致，始终走配置地址", async () => {
  const seen: string[] = [];
  const instance = create({
    baseURL: IDENTITY_BASE_URL,
    adapter: async (config) => {
      seen.push(String(config.baseURL));
      return { config, status: 200, statusText: "OK", headers: {}, data: {} };
    },
  });
  const transport = createAxiosHbposTransport(
    IDENTITY_BASE_URL,
    credentials,
    instance,
    undefined,
    POLICY,
  );
  await transport.request({ method: "GET", url: "/api/v1/health" });
  assert.deepEqual(seen, [IDENTITY_BASE_URL]);
});

test("冷却结束后写请求不去试探边缘入口：POST 继续走直连，由 GET 先试探", async () => {
  let current = 1_000_000;
  const { seen, transport } = setup(
    (config, host) => (host === EDGE ? networkError(config) : { ok: true }),
    { now: () => current },
  );
  // 边缘入口先失败一次，进入 2 分钟冷却。
  await transport.request({ method: "GET", url: "/api/v1/catalog" });
  seen.length = 0;

  current += 3 * 60_000; // 冷却已过，但边缘入口其实仍然不通
  await transport.request({ method: "POST", url: "/api/v1/orders/sync", data: { orderGuid: "o-2" } });
  assert.deepEqual(seen.map((item) => item.host), [DIRECT], "写请求不能拿支付 / 同步去试探一个可能坏掉的入口");

  await transport.request({ method: "GET", url: "/api/v1/catalog" });
  assert.deepEqual(
    seen.map((item) => item.host),
    [DIRECT, EDGE, DIRECT],
    "读请求负责试探边缘入口，失败后当场改走直连",
  );
});
