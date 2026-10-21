/**
 * 双入口故障转移的接线测试：封装层必须默认启用，健康探测必须经过入口感知的 fetch。
 * 选择器的行为本身由 @hb/pos-api-client 的单测覆盖，这里只防止接线被后续改动悄悄弄丢。
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

import {
  EDGE_API_HOST,
  sharedApiTransportSelector,
} from "@hb/pos-api-client/transport-failover";
import { AxiosError, create } from "axios";

import { createAxiosHbposTransport } from "./axios-transport";

const credentials = { getCredentials: async () => ({}) };

test("封装层默认启用故障转移：先走边缘入口，边缘不通时 GET 改走直连", async () => {
  const seen: string[] = [];
  const instance = create({
    baseURL: "https://hotbargain.vip/pos-api",
    adapter: async (config) => {
      const host = new URL(config.baseURL ?? "").hostname;
      seen.push(host);
      if (host === "pos.hotbargain.vip") {
        throw new AxiosError("Network Error", "ERR_NETWORK", config);
      }
      return { config, status: 200, statusText: "OK", headers: {}, data: {} };
    },
  });
  const transport = createAxiosHbposTransport(
    "https://hotbargain.vip/pos-api",
    credentials,
    instance,
  );
  try {
    await transport.request({ method: "GET", url: "/api/v1/health" });
    assert.deepEqual(seen, ["pos.hotbargain.vip", "hotbargain.vip"]);
  } finally {
    // 选择器是进程内单例，测试结束后恢复到首选入口。
    sharedApiTransportSelector.reportSuccess(EDGE_API_HOST);
  }
});

test("局域网 / 其他服务器地址完全不参与故障转移", async () => {
  const seen: string[] = [];
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
  );
  await assert.rejects(transport.request({ method: "GET", url: "/api/v1/health" }));
  assert.deepEqual(seen, ["http://192.168.31.247:5003"]);
});

test("三处健康探测都经过入口感知的 fetch（源码契约）", () => {
  // 任何一处退回裸 fetch，边缘入口一挂，整台设备就会被误判离线进入仅现金模式。
  for (const file of [
    "src/ui/shell/network-status-bridge.tsx",
    "src/core/runtime/expo-bootstrap-server-diagnostics.ts",
    "src/core/runtime/expo-pos-runtime.ts",
  ]) {
    const source = readFileSync(join(process.cwd(), file), "utf8");
    assert.match(
      source,
      /createSettingsApiHealthProbe\(\s*createFailoverFetch\(/u,
      `${file} 的健康探测必须经过 createFailoverFetch`,
    );
  }
});
