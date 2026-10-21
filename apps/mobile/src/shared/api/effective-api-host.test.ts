/**
 * 生效 API 地址解析（effective-api-host.ts）单元测试。
 *
 * 回归重点：设备账号会话必须解析出「绑定 host」而不是设置页的偏好 host。
 * 两者分裂过一次真实缺陷 —— 可达性探测按偏好 host 探得通、业务请求按绑定 host
 * 必然失败，离线态因此被拖进「判定恢复→重跑失败→再判定恢复」的紧密循环。
 *
 * 所有凭据端口都注入，避免加载依赖 expo-secure-store 的真实实现。
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { resolveEffectiveApiBaseUrl, resolveEffectiveApiHost } from "./effective-api-host";
import type { EffectiveApiHostPorts } from "./effective-api-host";

function ports(overrides: Partial<EffectiveApiHostPorts> = {}): EffectiveApiHostPorts {
  return {
    getStoredApiHost: async () => "hotbargain.vip",
    getToken: async () => "access-token",
    getRefreshToken: async () => null,
    getAuthSessionMarker: async () => "deviceAccount",
    loadBinding: async () => ({ apiHost: "127.0.0.1" }),
    ...overrides,
  };
}

test("设备账号会话解析出绑定 host，而不是设置页偏好 host", async () => {
  // 探测与业务请求必须看到同一个后端，否则离线态会在两者之间反复横跳。
  assert.equal(await resolveEffectiveApiHost(ports()), "127.0.0.1");
});

test("设备账号会话但没有绑定记录时退回偏好 host", async () => {
  assert.equal(
    await resolveEffectiveApiHost(ports({ loadBinding: async () => null })),
    "hotbargain.vip",
  );
});

test("普通账号会话即使存在异 host 绑定也用偏好 host", async () => {
  assert.equal(
    await resolveEffectiveApiHost(
      ports({ getAuthSessionMarker: async () => "account", getRefreshToken: async () => "refresh" }),
    ),
    "hotbargain.vip",
  );
});

test("没有会话标记但持有绑定与 access token 时按设备账号处理", async () => {
  // 对应 access token 先于 marker 落盘的崩溃窗口，仍必须绑定到原 apiHost。
  assert.equal(
    await resolveEffectiveApiHost(ports({ getAuthSessionMarker: async () => null })),
    "127.0.0.1",
  );
});

test("凭据读取抛错时不向上抛，退回偏好 host", async () => {
  const host = await resolveEffectiveApiHost(
    ports({
      getAuthSessionMarker: async () => null,
      loadBinding: async () => {
        throw new Error("secure store locked");
      },
      getToken: async () => {
        throw new Error("secure store locked");
      },
    }),
  );
  assert.equal(host, "hotbargain.vip");
});

test("基础地址按 host 类型补全协议与端口", async () => {
  // 生产域名走 Nginx HTTPS 代理；其余直连 5002 明文端口。
  assert.equal(await resolveEffectiveApiBaseUrl(ports()), "http://127.0.0.1:5002/api");
  assert.equal(
    await resolveEffectiveApiBaseUrl(ports({ loadBinding: async () => null })),
    "https://hotbargain.vip/api",
  );
});

test("健康探测默认必须走统一 host 解析（源码契约）", () => {
  // 默认实现若退回偏好 host，探测与业务请求就会再次分裂成两个后端。
  const currentDir = dirname(fileURLToPath(import.meta.url));
  const source = readFileSync(resolve(currentDir, "../network/health-check.ts"), "utf8");
  // 默认路径必须以 resolveEffectiveApiHost()（与 resolveEffectiveApiBaseUrl 同一套解析）得到身份 host，
  // 再经传输入口选择探测（当前入口不通就试备用入口），这样探测与业务请求始终走同一个入口。
  assert.match(
    source,
    /probeWithTransportFallback\(\s*await resolveEffectiveApiHost\(\)/,
    "健康探测的默认地址来源必须是 resolveEffectiveApiHost，并经传输入口选择",
  );
  assert.doesNotMatch(
    source,
    /getStoredApiHost/,
    "健康探测不得再直接读取设置页偏好 host",
  );
});
