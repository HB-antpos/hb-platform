import assert from "node:assert/strict";
import test from "node:test";

import { AxiosError, create, type AxiosRequestConfig } from "axios";

import { createAxiosHbposTransport } from "./transport";
import { HbposApiError, isHbposNonApiResponse, unwrapHbposEnvelope } from "./transport-contracts";

const credentials = {
  async getCredentials() {
    return { cashierAuthorization: "current-cashier-ticket" };
  },
};

test("iPad 策略保留显式 cashier header 且不暴露响应头", async () => {
  let request: AxiosRequestConfig | undefined;
  const instance = create({
    adapter: async (config) => {
      request = config;
      return {
        config,
        status: 200,
        statusText: "OK",
        headers: { "X-HBPOS-Allow-Transactions": "false" },
        data: { success: true },
      };
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example",
    credentials,
    instance,
    undefined,
    { cashierHeader: "preserve-explicit", responseHeaders: "omit" },
  );

  const response = await transport.request({
    method: "POST",
    url: "/api/v1/devices/reset-registration",
    headers: { "X-HBPOS-Cashier-Authorization": "fresh-online-ticket" },
  });

  assert.equal(
    request?.headers?.["X-HBPOS-Cashier-Authorization"],
    "fresh-online-ticket",
  );
  assert.equal(Object.hasOwn(response, "headers"), false);
});

test("手持策略覆盖 cashier header 并冻结规范化响应头", async () => {
  let request: AxiosRequestConfig | undefined;
  const instance = create({
    adapter: async (config) => {
      request = config;
      return {
        config,
        status: 200,
        statusText: "OK",
        headers: {
          " X-HBPOS-Allow-Transactions ": " false ",
          "X-Multi": ["one", "two"],
        },
        data: { success: true },
      };
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example",
    credentials,
    instance,
    undefined,
    { cashierHeader: "override-explicit", responseHeaders: "normalize" },
  );

  const response = await transport.request({
    method: "GET",
    url: "/api/v1/app-updates/pos-handheld",
    headers: { "X-HBPOS-Cashier-Authorization": "stale-ticket" },
  });

  assert.equal(
    request?.headers?.["X-HBPOS-Cashier-Authorization"],
    "current-cashier-ticket",
  );
  assert.deepEqual(response.headers, {
    "x-hbpos-allow-transactions": "false",
    "x-multi": "one, two",
  });
  assert.equal(Object.isFrozen(response.headers), true);
});

test("二进制下载按 arraybuffer 请求并原样转交 Range 请求头，默认请求不改变响应类型", async () => {
  const requests: AxiosRequestConfig[] = [];
  const instance = create({
    adapter: async (config) => {
      requests.push(config);
      return { config, status: 206, statusText: "Partial Content", headers: {}, data: new ArrayBuffer(4) };
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hbpos.example",
    credentials,
    instance,
    undefined,
    { cashierHeader: "override-explicit", responseHeaders: "omit" },
  );

  const response = await transport.request<ArrayBuffer>({
    method: "GET",
    url: "/api/v1/catalog/files/full",
    headers: { Range: "bytes=0-3", "If-Range": "\"abc\"" },
    responseType: "arraybuffer",
  });
  await transport.request({ method: "GET", url: "/api/v1/catalog/files/sync-plan" });

  assert.equal(requests[0]?.responseType, "arraybuffer");
  assert.equal(requests[0]?.headers?.Range, "bytes=0-3");
  assert.equal(requests[0]?.headers?.["If-Range"], "\"abc\"");
  assert.equal(response.status, 206);
  assert.equal(response.data.byteLength, 4);
  assert.equal(requests[1]?.responseType, undefined);
});

function failingInstance(status: number, data: unknown, headers: Record<string, string> = {}) {
  return create({
    adapter: async (config) => {
      throw new AxiosError("request failed", "ERR_BAD_RESPONSE", config, undefined, {
        config,
        data,
        headers,
        status,
        statusText: String(status),
      });
    },
  });
}

test("HTTP 错误记录正文是否为 ApiResult 信封：网关 HTML 403/404/405 标记为非 API 响应，信封错误码照常保留", async () => {
  const unauthorized: string[] = [];
  const cases: ReadonlyArray<Readonly<{
    status: number;
    data: unknown;
    apiEnvelope: boolean;
    code: string | undefined;
  }>> = [
    { status: 403, data: "<html><body>EdgeOne 403 Forbidden</body></html>", apiEnvelope: false, code: undefined },
    { status: 404, data: "<html>404 Not Found</html>", apiEnvelope: false, code: undefined },
    { status: 405, data: "", apiEnvelope: false, code: undefined },
    { status: 403, data: { code: "DEVICE_SCOPE_FORBIDDEN", message: "scope" }, apiEnvelope: false, code: undefined },
    {
      status: 403,
      data: { success: false, errorCode: "DEVICE_SCOPE_FORBIDDEN", message: "scope" },
      apiEnvelope: true,
      code: "DEVICE_SCOPE_FORBIDDEN",
    },
  ];

  for (const item of cases) {
    const transport = createAxiosHbposTransport(
      "https://hbpos.example",
      { async getCredentials() { return {}; } },
      failingInstance(item.status, item.data),
      {
        async onUnauthorized() { unauthorized.push(String(item.status)); },
        async onForbidden() { unauthorized.push(`forbidden-${item.status}`); },
      },
      { cashierHeader: "override-explicit", responseHeaders: "omit" },
    );
    await assert.rejects(
      () => transport.request({ method: "POST", url: "/api/v1/devices/verify" }),
      (error: unknown) => {
        assert.ok(error instanceof HbposApiError);
        assert.equal(error.kind, "http");
        assert.equal(error.status, item.status);
        assert.equal(error.apiEnvelope, item.apiEnvelope);
        assert.equal(error.code, item.code);
        assert.equal(isHbposNonApiResponse(error), !item.apiEnvelope);
        return true;
      },
    );
  }
  // 非撤销码 403 与 404/405 都不得触发锁机或清票据。
  assert.deepEqual(unauthorized, []);
});

test("429 限流记录 Retry-After 整数秒；缺失、HTTP-date 或非法值为 undefined（设备激活页据此提示等待分钟数）", async () => {
  const cases: ReadonlyArray<readonly [Record<string, string>, number | undefined]> = [
    [{ "retry-after": "90" }, 90],
    [{}, undefined],
    [{ "retry-after": "Wed, 21 Oct 2026 07:28:00 GMT" }, undefined],
    [{ "retry-after": "0" }, undefined],
    [{ "retry-after": "-5" }, undefined],
  ];
  for (const [headers, expected] of cases) {
    const transport = createAxiosHbposTransport(
      "https://hbpos.example",
      { async getCredentials() { return {}; } },
      failingInstance(429, "Too Many Requests", headers),
      {
        async onUnauthorized() {},
        async onForbidden() {},
      },
      { cashierHeader: "override-explicit", responseHeaders: "omit" },
    );
    await assert.rejects(
      () => transport.request({ method: "POST", url: "/api/v1/devices/activation-code/redeem" }),
      (error: unknown) => {
        assert.ok(error instanceof HbposApiError);
        assert.equal(error.status, 429);
        assert.equal(error.retryAfterSeconds, expected);
        return true;
      },
    );
  }
});

test("unwrap 遇到 2xx 非信封正文（HTML、null、空对象、数组）抛 envelope 错误并标记非 API 响应，不抛 TypeError", () => {
  for (const body of ["<html>captive portal</html>", null, {}, [], { data: { ok: true } }]) {
    assert.throws(
      () => unwrapHbposEnvelope(body as never),
      (error: unknown) =>
        error instanceof HbposApiError &&
        error.kind === "envelope" &&
        error.code === undefined &&
        error.apiEnvelope === false &&
        isHbposNonApiResponse(error),
    );
  }

  assert.throws(
    () => unwrapHbposEnvelope({ success: false, errorCode: "CASHIER_LOGIN_FAILED", message: "bad" }),
    (error: unknown) =>
      error instanceof HbposApiError &&
      error.kind === "envelope" &&
      error.code === "CASHIER_LOGIN_FAILED" &&
      error.apiEnvelope === true &&
      !isHbposNonApiResponse(error),
  );
  assert.deepEqual(unwrapHbposEnvelope({ success: true, data: { ok: true } }), { ok: true });
});
