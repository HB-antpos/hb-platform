// 位置请求接口：URL、请求体字段、请求头（账号绑定 / 静默）、超时、响应解包与格式异常。
import assert from "node:assert/strict";
import { test } from "node:test";
import type { AxiosRequestConfig } from "axios";
import { EXPECTED_ACCOUNT_HEADER } from "@/shared/api/account-bound-request-header";
import { LOCATION_REQUEST_HTTP_TIMEOUT_MS, createLocationRequestApi, type LocationRequestHttp } from "./location-request-api-core";
import {
  LOCATION_REQUEST_PENDING_PATH,
  LocationRequestResponseError,
  buildRespondRequestBody,
  locationRequestRespondPath,
} from "./location-request-logic";

interface Recorded {
  method: "get" | "post";
  url: string;
  data?: unknown;
  config?: AxiosRequestConfig;
}

const ACCOUNT = "11111111-2222-3333-4444-555555555555";

function fakeHttp(respond: (call: Recorded) => unknown | Promise<unknown>) {
  const calls: Recorded[] = [];
  const http: LocationRequestHttp = {
    get: async (url, config) => {
      const call: Recorded = { method: "get", url, config };
      calls.push(call);
      return { data: await respond(call) };
    },
    post: async (url, data, config) => {
      const call: Recorded = { method: "post", url, data, config };
      calls.push(call);
      return { data: await respond(call) };
    },
  };
  return { http, calls };
}

const dto = (overrides: Record<string, unknown> = {}) => ({
  requestGuid: "req-1",
  storeCode: "1042",
  requestedByName: "Alex Manager",
  status: "Pending",
  createdAtUtc: "2026-11-19T01:05:00Z",
  expiresAtUtc: "2026-11-19T01:20:00Z",
  ...overrides,
});

test("listPending：GET 待办路径，请求绑定账号、跳过登录跳转与中心日志，带较短超时", async () => {
  const { http, calls } = fakeHttp(() => [dto()]);
  const list = await createLocationRequestApi(http).listPending(ACCOUNT);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].method, "get");
  assert.equal(calls[0].url, "/react/v1/attendance/my/location-requests/pending");
  assert.equal(calls[0].url, LOCATION_REQUEST_PENDING_PATH);
  assert.equal(calls[0].config?.timeout, LOCATION_REQUEST_HTTP_TIMEOUT_MS);
  const headers = calls[0].config?.headers as Record<string, string>;
  assert.equal(headers[EXPECTED_ACCOUNT_HEADER], ACCOUNT, "请求绑定发起时的账号，期间换号会在本地被拒");
  assert.equal(headers["X-Skip-Auth-Redirect"], "1", "自动触发的请求遇到 401 不由请求自己拉起登录页");
  assert.equal(headers["X-Skip-Center-Log"], "1", "每次回前台都发的只读请求，失败不进中心日志");
  assert.equal(list.length, 1);
  assert.equal(list[0].requestGuid, "req-1");
  assert.equal(list[0].requestedByName, "Alex Manager");
});

test("listPending：未解包的信封也能处理；响应不是列表抛格式错误（不会被当成没有待办）", async () => {
  const wrapped = fakeHttp(() => ({ success: true, data: [dto({ requestGuid: "w1" })] }));
  assert.deepEqual((await createLocationRequestApi(wrapped.http).listPending(ACCOUNT)).map((item) => item.requestGuid), ["w1"]);

  for (const payload of [null, "oops", { requestGuid: "x" }, { success: true, data: { a: 1 } }]) {
    const bad = fakeHttp(() => payload);
    await assert.rejects(() => createLocationRequestApi(bad.http).listPending(ACCOUNT), LocationRequestResponseError, JSON.stringify(payload));
  }
});

test("listPending：success:false 信封把 errorCode 带在错误上；HTTP 失败原样抛出", async () => {
  const business = fakeHttp(() => ({ success: false, errorCode: "FORBIDDEN", message: "denied" }));
  await assert.rejects(
    () => createLocationRequestApi(business.http).listPending(ACCOUNT),
    (error: unknown) => (error as { code?: string }).code === "FORBIDDEN",
  );
  const network = fakeHttp(() => {
    throw Object.assign(new Error("Network Error"), { code: "ERR_NETWORK" });
  });
  await assert.rejects(() => createLocationRequestApi(network.http).listPending(ACCOUNT), /Network Error/);
});

test("respond：POST 响应路径（guid 编码），请求体字段与构造函数一致，不带静默中心日志头", async () => {
  const body = buildRespondRequestBody(
    { hardwareId: "hw-1", systemDeviceNumber: "SYS-1", deviceSystem: "Android" },
    {
      locationLatitude: -32.9283,
      locationLongitude: 151.7817,
      locationAccuracy: 12,
      locationPermissionStatus: "granted",
      locationCapturedAtUtc: "2026-11-19T01:08:09.000Z",
    },
  );
  const { http, calls } = fakeHttp(() => dto({ status: "Responded", alreadyResponded: false }));
  const result = await createLocationRequestApi(http).respond(ACCOUNT, "req/1", body, { timeoutMs: 5000 });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].method, "post");
  assert.equal(calls[0].url, locationRequestRespondPath("req/1"));
  assert.equal(calls[0].url, "/react/v1/attendance/my/location-requests/req%2F1/respond");
  assert.deepEqual(calls[0].data, {
    hardwareId: "hw-1",
    systemDeviceNumber: "SYS-1",
    deviceSystem: "Android",
    locationLatitude: -32.9283,
    locationLongitude: 151.7817,
    locationAccuracy: 12,
    locationPermissionStatus: "granted",
    locationCapturedAtUtc: "2026-11-19T01:08:09.000Z",
  });
  assert.equal(calls[0].config?.timeout, 5000);
  const headers = calls[0].config?.headers as Record<string, string>;
  assert.equal(headers[EXPECTED_ACCOUNT_HEADER], ACCOUNT);
  assert.equal(headers["X-Skip-Auth-Redirect"], "1");
  assert.equal("X-Skip-Center-Log" in headers, false, "respond 失败有排查价值，保留中心日志（只记响应，不含请求体坐标）");
  assert.equal(result.requestGuid, "req-1");
  assert.equal(result.status, "Responded");
});

test("respond：幂等重放 alreadyResponded 原样返回；响应不是请求对象抛格式错误", async () => {
  const replay = fakeHttp(() => dto({ status: "Responded", alreadyResponded: true }));
  const body = buildRespondRequestBody({}, { locationLatitude: 1, locationLongitude: 2, locationPermissionStatus: "granted", locationCapturedAtUtc: "2026-11-19T01:08:09.000Z" });
  assert.equal((await createLocationRequestApi(replay.http).respond(ACCOUNT, "req-1", body)).alreadyResponded, true);

  for (const payload of [null, [], "ok", {}]) {
    const bad = fakeHttp(() => payload);
    await assert.rejects(() => createLocationRequestApi(bad.http).respond(ACCOUNT, "req-1", body), LocationRequestResponseError, JSON.stringify(payload));
  }
});

test("账号为空时在本地拒绝（ACCOUNT_SESSION_CHANGED），不发请求", async () => {
  const { http, calls } = fakeHttp(() => []);
  await assert.rejects(
    () => createLocationRequestApi(http).listPending("  "),
    (error: unknown) => (error as { code?: string }).code === "ACCOUNT_SESSION_CHANGED",
  );
  assert.equal(calls.length, 0);
});
