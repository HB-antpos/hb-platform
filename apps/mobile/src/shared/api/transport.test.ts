/**
 * probeWithTransportFallback / buildTransportApiBaseUrl（transport.ts）单元测试。
 * 选择器是进程内单例：每个用例开始前用 hydrate 把它恢复到初始状态（首选边缘入口、无冷却）。
 */
import { beforeEach, test } from "node:test";
import assert from "node:assert/strict";

import {
  apiTransportSelector,
  buildTransportApiBaseUrl,
  probeWithTransportFallback,
} from "./transport";

beforeEach(() => {
  apiTransportSelector.hydrate({ activeIndex: 0, stickyUntil: 0, backoffStep: 0 });
});

test("默认用边缘入口构造基础地址；入口组之外的 host 原样不变", () => {
  assert.equal(buildTransportApiBaseUrl("hotbargain.vip"), "https://pos.hotbargain.vip/api");
  assert.equal(buildTransportApiBaseUrl("hotbargain.group"), "https://hotbargain.group/api");
  assert.equal(buildTransportApiBaseUrl("192.168.31.247"), "http://192.168.31.247:5002/api");
});

test("故障转移重试时可以强制指定备用入口", () => {
  assert.equal(
    buildTransportApiBaseUrl("hotbargain.vip", "hotbargain.vip"),
    "https://hotbargain.vip/api",
  );
});

test("首个入口探测成功：只探测一次，不切换", async () => {
  const urls: string[] = [];
  const value = await probeWithTransportFallback("hotbargain.vip", async (apiBaseUrl) => {
    urls.push(apiBaseUrl);
    return "ok";
  });
  assert.equal(value, "ok");
  assert.deepEqual(urls, ["https://pos.hotbargain.vip/api"]);
  assert.equal(apiTransportSelector.isUsingFallback("hotbargain.vip"), false);
});

test("首选入口探测抛错、直连入口通：返回直连结果，并让后续请求改走直连", async () => {
  const urls: string[] = [];
  const value = await probeWithTransportFallback("hotbargain.vip", async (apiBaseUrl) => {
    urls.push(apiBaseUrl);
    if (apiBaseUrl.includes("pos.hotbargain.vip")) {
      throw new TypeError("Network request failed");
    }
    return "direct-ok";
  });
  assert.equal(value, "direct-ok");
  assert.deepEqual(urls, ["https://pos.hotbargain.vip/api", "https://hotbargain.vip/api"]);
  assert.equal(apiTransportSelector.isUsingFallback("hotbargain.vip"), true);
  // 探测与业务请求必须一致：后续构造的基础地址已经是直连。
  assert.equal(buildTransportApiBaseUrl("hotbargain.vip"), "https://hotbargain.vip/api");
});

test("首选入口返回非成功响应（例如边缘 524）、直连入口通：同样改走直连", async () => {
  const value = await probeWithTransportFallback(
    "hotbargain.vip",
    async (apiBaseUrl) => ({ ok: !apiBaseUrl.includes("pos.hotbargain.vip"), apiBaseUrl }),
    (result) => result.ok,
  );
  assert.equal(value.apiBaseUrl, "https://hotbargain.vip/api");
  assert.equal(apiTransportSelector.isUsingFallback("hotbargain.vip"), true);
});

test("两个入口都不通：抛出第一次的错误，且不改变入口选择", async () => {
  const firstError = new TypeError("first failed");
  let calls = 0;
  await assert.rejects(
    probeWithTransportFallback("hotbargain.vip", async () => {
      calls += 1;
      throw calls === 1 ? firstError : new TypeError("second failed");
    }),
    (error) => error === firstError,
  );
  assert.equal(calls, 2);
  assert.equal(apiTransportSelector.isUsingFallback("hotbargain.vip"), false);
});

test("两个入口都返回非成功响应：返回第一次的响应，不改变入口选择", async () => {
  const value = await probeWithTransportFallback(
    "hotbargain.vip",
    async (apiBaseUrl) => ({ ok: false, apiBaseUrl }),
    (result) => result.ok,
  );
  assert.equal(value.apiBaseUrl, "https://pos.hotbargain.vip/api");
  assert.equal(apiTransportSelector.isUsingFallback("hotbargain.vip"), false);
});

test("冷却期内当前是直连、直连探测失败而首选已恢复：改回首选", async () => {
  apiTransportSelector.reportFailure("hotbargain.vip", "pos.hotbargain.vip", "network");
  assert.equal(apiTransportSelector.isUsingFallback("hotbargain.vip"), true);

  const value = await probeWithTransportFallback("hotbargain.vip", async (apiBaseUrl) => {
    if (apiBaseUrl === "https://hotbargain.vip/api") {
      throw new TypeError("direct line is down");
    }
    return "edge-ok";
  });

  assert.equal(value, "edge-ok");
  assert.equal(apiTransportSelector.isUsingFallback("hotbargain.vip"), false);
});

test("入口组之外的 host 只探测一次，错误原样抛出（与改动前一致）", async () => {
  let calls = 0;
  await assert.rejects(
    probeWithTransportFallback("192.168.31.247", async () => {
      calls += 1;
      throw new TypeError("lan down");
    }),
    /lan down/,
  );
  assert.equal(calls, 1);
});
