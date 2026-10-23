import assert from "node:assert/strict";
import test from "node:test";

import {
  ApiTransportSelector,
  sharedApiTransportSelector,
} from "@hb/pos-api-client/transport-failover";

import {
  classifyHealthResponse,
  NETWORK_CHECK_BURST_ATTEMPTS,
  NETWORK_CHECK_PUBLIC_IP_URL,
  NETWORK_CHECK_SEQUENTIAL_ATTEMPTS,
  parseDohAddresses,
  resolveNetworkCheckTargets,
  runNetworkCheck,
  type NetworkCheckPorts,
} from "./network-check-runner";
import type { NetworkCheckRun } from "./network-check-types";

const SERVER_TIME = "2026-10-23T08:00:00.000Z";
const SERVER_MS = Date.parse(SERVER_TIME);

type Reply = Readonly<{ status: number; body: string; headers?: Record<string, string> }> | "network";

const healthOk = JSON.stringify({
  success: true,
  data: { isOnline: true, serverTime: SERVER_TIME, status: "ok" },
});

/** 按请求地址返回固定结果的假 fetch，并记录所有请求。 */
function createPorts(
  reply: (url: URL) => Reply,
  overrides: Partial<NetworkCheckPorts> = {},
): NetworkCheckPorts & { requests: string[] } {
  const requests: string[] = [];
  let clock = 0;
  return {
    requests,
    fetch: async (url, init) => {
      requests.push(url);
      if (init.signal.aborted) throw new Error("aborted");
      clock += 50;
      const result = reply(new URL(url));
      if (result === "network") throw new TypeError("Network request failed");
      return {
        status: result.status,
        headers: { get: (name: string) => result.headers?.[name] ?? null },
        text: async () => result.body,
      };
    },
    now: () => clock,
    wallNow: () => SERVER_MS + 2_000,
    sleep: async () => undefined,
    readLocalNetwork: async () => ({
      known: true,
      connected: true,
      type: "WIFI",
      ipAddress: "192.168.1.20",
    }),
    isUsingFallback: () => false,
    ...overrides,
  };
}

function defaultReply(url: URL): Reply {
  if (url.pathname === "/dns-query" || url.pathname === "/resolve") {
    return {
      status: 200,
      body: JSON.stringify({
        Status: 0,
        Answer: [
          { type: 5, data: "pos.hotbargain.vip.eo.dnse2.com." },
          { type: 1, data: url.searchParams.get("name") === "hotbargain.vip" ? "43.160.208.238" : "43.159.98.78" },
        ],
      }),
    };
  }
  if (url.pathname === "/cdn-cgi/trace") return { status: 200, body: "ip=1.2.3.4" };
  if (url.pathname === "/api/health/diagnostics") {
    return { status: 200, body: JSON.stringify({ clientIp: "124.187.52.242", requestId: "req-1" }) };
  }
  return {
    status: 200,
    body: healthOk,
    headers: url.hostname === "pos.hotbargain.vip" ? { "eo-log-uuid": "eo-123" } : {},
  };
}

async function run(
  apiBaseUrl: string,
  ports: NetworkCheckPorts,
  signal: AbortSignal = new AbortController().signal,
): Promise<{ final: NetworkCheckRun; updates: NetworkCheckRun[] }> {
  const updates: NetworkCheckRun[] = [];
  const final = await runNetworkCheck({
    apiBaseUrl,
    signal,
    ports,
    nowIso: () => "2026-10-23T08:00:00.000Z",
    onUpdate: (next) => updates.push(next),
  });
  return { final, updates };
}

test("入口组内地址拆成 EdgeOne 与直连两条，路径保持 /pos-api", () => {
  assert.deepEqual(
    resolveNetworkCheckTargets("https://hotbargain.vip/pos-api/").map((target) => target.baseUrl),
    ["https://pos.hotbargain.vip/pos-api", "https://hotbargain.vip/pos-api"],
  );
  assert.deepEqual(resolveNetworkCheckTargets("http://192.168.1.8:5002"), [
    { kind: "configured", host: "192.168.1.8", baseUrl: "http://192.168.1.8:5002", literal: true },
  ]);
});

test("健康检查返回包分类与 WPF 一致，EdgeOne 52x 单独归类", () => {
  assert.deepEqual(classifyHealthResponse(200, healthOk), { ok: true, serverTime: SERVER_TIME });
  assert.deepEqual(classifyHealthResponse(522, ""), { ok: false, error: "edge" });
  assert.deepEqual(classifyHealthResponse(502, ""), { ok: false, error: "http" });
  assert.deepEqual(classifyHealthResponse(200, "<html>portal</html>"), { ok: false, error: "notPosApi" });
  assert.deepEqual(classifyHealthResponse(200, JSON.stringify({ status: "healthy" })), { ok: false, error: "notPosApi" });
  assert.deepEqual(
    classifyHealthResponse(200, JSON.stringify({ success: true, data: { isOnline: false } })),
    { ok: false, error: "http" },
  );
});

test("DoH 只取 A 记录", () => {
  assert.deepEqual(
    parseDohAddresses(JSON.stringify({ Status: 0, Answer: [{ type: 5, data: "x." }, { type: 1, data: "1.2.3.4" }] })),
    ["1.2.3.4"],
  );
  assert.deepEqual(parseDohAddresses(JSON.stringify({ Status: 3 })), []);
  assert.deepEqual(parseDohAddresses("not json"), []);
});

test("完整检测：两条入口分别测、记录 eo-log-uuid、公网 IP 与时钟偏差", async () => {
  const ports = createPorts(defaultReply);
  const { final, updates } = await run("https://hotbargain.vip/pos-api", ports);

  assert.equal(final.phase, "done");
  assert.ok(updates.some((update) => update.phase === "stability"));
  const [edge, direct] = final.measurements.entries;
  assert.equal(edge?.kind, "edge");
  assert.equal(edge?.first?.eoLogUuid, "eo-123");
  assert.deepEqual(edge?.dns?.addresses, ["43.159.98.78"]);
  assert.deepEqual(direct?.dns?.addresses, ["43.160.208.238"]);
  assert.equal(edge?.sequential?.attempts, NETWORK_CHECK_SEQUENTIAL_ATTEMPTS);
  assert.equal(direct?.burst?.succeeded, NETWORK_CHECK_BURST_ATTEMPTS);
  assert.equal(final.measurements.publicIp?.ip, "124.187.52.242");
  assert.equal(final.measurements.activeEntry, "edge");
  assert.equal(final.measurements.local?.type, "WIFI");
  // 本机时钟比服务器快 2 秒，再加上半个请求耗时的校正。
  assert.ok(Math.abs((final.measurements.clockSkewMs ?? 0) - 2_000) < 100);

  const healthHosts = new Set(
    ports.requests
      .map((url) => new URL(url))
      .filter((url) => url.pathname === "/pos-api/api/v1/health")
      .map((url) => url.hostname),
  );
  assert.deepEqual([...healthHosts].sort(), ["hotbargain.vip", "pos.hotbargain.vip"]);
  assert.ok(ports.requests.some((url) => url.startsWith(NETWORK_CHECK_PUBLIC_IP_URL)));
  // 每次请求都带防缓存参数。
  assert.ok(ports.requests.filter((url) => url.includes("/api/v1/health")).every((url) => url.includes("_t=")));
});

test("EdgeOne 不通时直连照常测，不通的入口不做并发测试", async () => {
  const ports = createPorts((url) =>
    url.hostname === "pos.hotbargain.vip" ? "network" : defaultReply(url),
  );
  const { final } = await run("https://pos.hotbargain.vip/pos-api", ports);
  const [edge, direct] = final.measurements.entries;
  assert.equal(edge?.first?.error, "network");
  assert.equal(edge?.sequential?.succeeded, 0);
  assert.equal(edge?.burst, null);
  assert.equal(direct?.burst?.succeeded, NETWORK_CHECK_BURST_ATTEMPTS);
});

test("诊断不经过也不改动共享的入口选择器", async () => {
  const before = sharedApiTransportSelector.resolve({ probe: false });
  const ports = createPorts((url) =>
    url.hostname === "pos.hotbargain.vip" ? "network" : defaultReply(url),
  );
  await run("https://hotbargain.vip/pos-api", ports);
  assert.equal(sharedApiTransportSelector.isUsingFallback(), false);
  assert.equal(sharedApiTransportSelector.resolve({ probe: false }), before);
});

test("App 已切到备用入口时显示直连", async () => {
  const selector = new ApiTransportSelector();
  selector.reportFailure("pos.hotbargain.vip", "network");
  const ports = createPorts(defaultReply, { isUsingFallback: () => selector.isUsingFallback() });
  const { final } = await run("https://hotbargain.vip/pos-api", ports);
  assert.equal(final.measurements.activeEntry, "direct");
});

test("配置地址不在入口组：只测这一个地址，不查公网 IP 与 DNS", async () => {
  const ports = createPorts(defaultReply);
  const { final } = await run("http://192.168.1.8:5002", ports);
  assert.equal(final.measurements.entries.length, 1);
  assert.equal(final.measurements.entries[0]?.dns, null);
  assert.equal(final.measurements.publicIp, null);
  assert.equal(final.measurements.activeEntry, null);
  assert.ok(!ports.requests.some((url) => url.startsWith(NETWORK_CHECK_PUBLIC_IP_URL)));
});

test("中途停止：标记 cancelled 并停止后续请求", async () => {
  const controller = new AbortController();
  let healthCount = 0;
  const ports = createPorts((url) => {
    if (url.pathname.endsWith("/api/v1/health")) {
      healthCount += 1;
      if (healthCount === 3) controller.abort();
    }
    return defaultReply(url);
  });
  const { final } = await run("https://hotbargain.vip/pos-api", ports, controller.signal);
  assert.equal(final.phase, "cancelled");
  const requestsAtCancel = ports.requests.length;
  assert.ok(requestsAtCancel < 30, `停止后不应继续发请求，实际 ${requestsAtCancel}`);
  assert.ok(final.measurements.entries.every((entry) => entry.burst === null));
});
