import assert from "node:assert/strict";
import test from "node:test";

import { create, type AxiosRequestConfig } from "axios";

import { createAxiosHbposTransport } from "./transport";

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
