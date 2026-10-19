import assert from "node:assert/strict";
import { buildNetworkReport, describeClientIp, formatSydneyDateTime, type NetworkTestTranslate } from "./report";
import {
  buildApiUrl,
  createInitialLinks,
  IMAGE_PROBE_URL,
  INTERNET_PROBE_URL,
  runNetworkTest,
  type NetworkTestRunnerPorts,
} from "./runner";
import { computeLinkStats, computeVerdict, formatDuration, formatSpeed, rateLink } from "./stats";
import type { NetworkLinkResult, NetworkSample, NetworkTestRunSummary } from "./types";
import zh from "../../locales/zh/screens/networkTest.json";

/** 用真实中文文案做插值，报告断言贴近用户实际看到的文字。 */
const t: NetworkTestTranslate = (key, params = {}) => {
  const template = key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown>)?.[part], zh);
  assert.equal(typeof template, "string", `缺少文案：${key}`);
  return (template as string).replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(params[name] ?? ""));
};

const ok = (ms: number): NetworkSample => ({ ok: true, ms, status: 200 });
const fail = (ms: number, error: NetworkSample["error"] = "timeout"): NetworkSample => ({ ok: false, ms, error });
const link = (id: NetworkLinkResult["id"], samples: NetworkSample[], extra: Partial<NetworkLinkResult> = {}): NetworkLinkResult => ({
  id,
  state: "done",
  plannedCount: samples.length,
  samples,
  ...extra,
});

// ── 统计 ──
{
  const stats = computeLinkStats([ok(100), ok(200), fail(8000), ok(300)]);
  assert.deepEqual(stats, { total: 4, success: 3, minMs: 100, avgMs: 200, maxMs: 300, p95Ms: 300, jitterMs: 100, lossRate: 0.25 });
  assert.deepEqual(computeLinkStats([fail(8000), fail(8000)]).lossRate, 1);
  assert.equal(computeLinkStats([]).lossRate, 0);
  // 10 次取 P95 = 第 10 小（最慢）的那次。
  assert.equal(computeLinkStats([1, 2, 3, 4, 5, 6, 7, 8, 9, 50].map(ok)).p95Ms, 50);
}

// ── 评级 ──
{
  assert.equal(rateLink(link("server", [ok(80), ok(120)])), "good");
  assert.equal(rateLink(link("server", [ok(80), ok(1200)])), "fair");
  assert.equal(rateLink(link("server", Array.from({ length: 9 }, () => ok(100)).concat(fail(8000)))), "fair");
  assert.equal(rateLink(link("server", [ok(100), fail(8000)])), "bad");
  assert.equal(rateLink(link("server", [fail(8000)])), "bad");
  // 商品图片在上海，阈值放宽：1.2 秒仍算良好。
  assert.equal(rateLink(link("image", [ok(1200), ok(1300), ok(1250)])), "good");
  assert.equal(rateLink(link("database", [ok(30)], { serverLatencyMs: 12 })), "good");
  assert.equal(rateLink(link("database", [ok(30)], { serverLatencyMs: 800 })), "bad");
  assert.equal(rateLink(link("download", [ok(1000)], { downloadKBps: 480 })), "good");
  assert.equal(rateLink(link("download", [ok(1000)], { downloadKBps: 30 })), "bad");
  assert.equal(rateLink({ ...link("auth", []), state: "skipped" }), null);
  assert.equal(rateLink({ ...link("database", []), state: "unavailable" }), null);
  // 诊断请求本身失败（连不上）时测不到数据库，不评级。
  assert.equal(rateLink(link("database", [fail(8000, "network")])), null);
}

// ── 结论 ──
{
  const base = (links: NetworkLinkResult[]) => ({ links, cancelled: false });
  assert.equal(computeVerdict(base([link("internet", [fail(5000)]), link("server", [fail(8000)])])).key, "noInternet");
  assert.equal(computeVerdict(base([link("internet", [ok(80)]), link("server", [fail(8000)])])).key, "serverUnreachable");
  assert.equal(
    computeVerdict(base([link("internet", [ok(80)]), link("server", [ok(210), ok(900), fail(8000), ok(260)])])).key,
    "serverUnstable"
  );
  assert.equal(computeVerdict(base([link("internet", [ok(80)]), link("server", [ok(700), ok(720), ok(710)])])).key, "serverSlow");
  assert.equal(
    computeVerdict(base([link("server", [ok(90)]), link("database", [ok(120)], { serverLatencyMs: 900 })])).key,
    "databaseIssue"
  );
  // 诊断请求本身失败（例如限流）不能误报成数据库问题。
  assert.equal(
    computeVerdict(base([link("server", [ok(90)]), link("database", [{ ok: false, ms: 50, status: 429, error: "http" }])])).key,
    "allGood"
  );
  assert.equal(computeVerdict(base([link("server", [ok(90)]), link("auth", [fail(40, "unauthorized")])])).key, "authExpired");
  assert.equal(computeVerdict(base([link("internet", [fail(5000)]), link("server", [ok(90)])])).key, "internetProbeBlocked");
  assert.equal(computeVerdict(base([link("internet", [ok(60)]), link("server", [ok(90)])])).key, "allGood");
  assert.equal(computeVerdict({ links: [], cancelled: true }).key, "cancelled");
}

// ── 地址拼接与格式 ──
{
  assert.equal(buildApiUrl("https://hotbargain.vip/api", "/health"), "https://hotbargain.vip/api/health");
  assert.equal(buildApiUrl("https://hotbargain.vip/api/", "health/diagnostics"), "https://hotbargain.vip/api/health/diagnostics");
  assert.equal(buildApiUrl("http://192.168.31.247:5002", "/health"), "http://192.168.31.247:5002/api/health");
  assert.equal(formatDuration(85), "85 ms");
  assert.equal(formatDuration(1800), "1.8 s");
  assert.equal(formatDuration(null), "--");
  assert.equal(formatSpeed(480.4), "480 KB/s");
  assert.equal(formatSpeed(2048), "2.0 MB/s");
  // 悉尼夏令时 UTC+11。
  assert.equal(formatSydneyDateTime("2026-10-19T07:42:05.000Z"), "2026-10-19 18:42:05");
  assert.equal(createInitialLinks({ accessToken: null }).find((item) => item.id === "auth")?.state, "skipped");
  assert.equal(createInitialLinks({ accessToken: "token" }).find((item) => item.id === "auth")?.state, "pending");
}

// ── 报告 ──
{
  const summary: NetworkTestRunSummary = {
    links: [
      link("internet", [ok(80), ok(90)]),
      link("server", [ok(210), ok(640), fail(8000), ok(1800)]),
      { ...link("auth", []), state: "skipped" },
      link("database", [ok(300)], { serverLatencyMs: 12 }),
      link("image", [ok(1200)]),
      link("download", [ok(1100)], { downloadKBps: 480, downloadBytes: 524288 }),
    ],
    clientIp: "49.181.138.32",
    requestId: "TRACE-1",
    startedAtIso: "2026-10-19T07:41:40.000Z",
    finishedAtIso: "2026-10-19T07:42:05.000Z",
    cancelled: false,
  };
  const report = buildNetworkReport(
    {
      summary,
      verdict: computeVerdict(summary),
      serverHost: "hotbargain.vip",
      serverSourceLabel: "服务器设置",
      deviceLabel: "iPhone 13 · iOS 18.1",
      appLabel: "1.0.9 (60) · 正式 · OTA 1a2b3c4d",
      accountLabel: "未登录",
    },
    t
  );
  assert.match(report, /^HB 网络测试报告 {2}2026-10-19 18:42:05（悉尼）/);
  assert.match(report, /公网 IP：49\.181\.138\.32/);
  assert.match(report, /结论：服务器连接不稳定/);
  assert.match(report, /2 主服务器 {2}3\/4 {2}平均 883 ms {2}🔴/);
  assert.match(report, /最快 210 ms · P95 1\.8 s · 抖动 /);
  assert.match(report, /失败：第 3 次超时/);
  assert.match(report, /3 登录态接口 {2}未登录，已跳过/);
  assert.match(report, /4 服务器 → 数据库 {2}12 ms {2}🟢/);
  assert.match(report, /6 下载速度 {2}480 KB\/s {2}🟢/);
  assert.match(report, /请求编号：TRACE-1/);
  // 旧版后端（诊断接口 404）说「服务器未提供」；新后端识别不出公网 IP 才说「服务器未识别」。
  assert.equal(
    describeClientIp({ clientIp: null, links: [{ ...link("database", []), state: "unavailable" }] }, t),
    "服务器未提供（后端未升级）"
  );
  assert.equal(describeClientIp({ clientIp: null, links: [link("database", [ok(30)])] }, t), "服务器未识别");
  assert.equal(describeClientIp({ clientIp: null, links: [link("database", [fail(8000, "network")])] }, t), "未取到（诊断请求失败）");
}

// ── 运行器：模拟网络，检查顺序、跳过、旧后端 404、测速计算、取消 ──
async function runWith(
  respond: (url: string, init: RequestInit | undefined) => { status: number; json?: unknown; bytes?: number } | "throw",
  options: { token?: string | null; abortAfterCalls?: number } = {}
) {
  let clock = 0;
  const calls: { url: string; init?: RequestInit }[] = [];
  const controller = new AbortController();
  const ports: NetworkTestRunnerPorts = {
    fetchImpl: (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      clock += 100;
      if (options.abortAfterCalls && calls.length >= options.abortAfterCalls) controller.abort();
      const reply = respond(url, init);
      if (reply === "throw") throw new TypeError("Network request failed");
      return {
        status: reply.status,
        json: async () => reply.json,
        arrayBuffer: async () => new ArrayBuffer(reply.bytes ?? 0),
      } as Response;
    }) as typeof fetch,
    now: () => clock,
    nowIso: () => "2026-10-19T07:42:00.000Z",
    sleep: async () => undefined,
  };
  const updates: NetworkTestRunSummary[] = [];
  const summary = await runNetworkTest({
    context: {
      apiBaseUrl: "https://hotbargain.vip/api",
      serverHost: "hotbargain.vip",
      serverSource: "settings",
      accessToken: options.token ?? null,
    },
    signal: controller.signal,
    onUpdate: (next) => updates.push(next),
    ports,
  });
  return { summary, calls, updates };
}

async function runnerTests() {
  // 未登录 + 新后端：登录态接口不发请求，诊断拿到公网 IP，下载按 100ms / 512KB 算出速度。
  {
    const { summary, calls, updates } = await runWith((url) => {
      if (url.includes("/health/diagnostics")) {
        return { status: 200, json: { clientIp: "49.181.138.32", requestId: "TRACE-9", database: { ok: true, latencyMs: 12 } } };
      }
      if (url.includes("/health/download")) return { status: 200, bytes: 512 * 1024 };
      return { status: 200 };
    });
    assert.equal(calls.filter((call) => call.url.startsWith(INTERNET_PROBE_URL)).length, 5);
    assert.equal(calls.filter((call) => call.url.includes("/api/health?")).length, 10);
    assert.equal(calls.some((call) => call.url.includes("/health/auth")), false);
    const imageCalls = calls.filter((call) => call.url === IMAGE_PROBE_URL);
    assert.equal(imageCalls.length, 3);
    assert.equal(imageCalls[0].init?.method, "HEAD");
    assert.equal(summary.clientIp, "49.181.138.32");
    assert.equal(summary.requestId, "TRACE-9");
    const database = summary.links.find((item) => item.id === "database");
    assert.equal(database?.serverLatencyMs, 12);
    const download = summary.links.find((item) => item.id === "download");
    assert.equal(Math.round(download?.downloadKBps ?? 0), 5120);
    assert.equal(summary.links.find((item) => item.id === "auth")?.state, "skipped");
    assert.equal(summary.cancelled, false);
    assert.ok(summary.finishedAtIso);
    // 每个样本都推送快照，界面能逐次刷新。
    assert.ok(updates.length > 20);
  }

  // 已登录：登录态接口带 Bearer；401 记为 unauthorized。旧后端诊断 / 下载 404 → 服务器未提供，不算失败。
  {
    const { summary, calls } = await runWith(
      (url) => {
        if (url.includes("/health/auth")) return { status: 401 };
        if (url.includes("/health/diagnostics") || url.includes("/health/download")) return { status: 404 };
        return { status: 200 };
      },
      { token: "ACCESS-TOKEN" }
    );
    const authCall = calls.find((call) => call.url.includes("/health/auth"));
    assert.equal((authCall?.init?.headers as Record<string, string>).Authorization, "Bearer ACCESS-TOKEN");
    assert.ok(summary.links.find((item) => item.id === "auth")?.samples.every((sample) => sample.error === "unauthorized"));
    assert.equal(summary.links.find((item) => item.id === "database")?.state, "unavailable");
    assert.equal(summary.links.find((item) => item.id === "download")?.state, "unavailable");
    assert.equal(computeVerdict(summary).key, "authExpired");
  }

  // 连不上：请求抛异常记为 network 失败，结论是没网。
  {
    const { summary } = await runWith(() => "throw");
    assert.ok(summary.links.find((item) => item.id === "server")?.samples.every((sample) => sample.error === "network"));
    assert.equal(computeVerdict(summary).key, "noInternet");
  }

  // 用户点停止：正在跑的项按已测到的样本收尾，后面的保持等待，标记 cancelled。
  {
    const { summary, calls } = await runWith(() => ({ status: 200 }), { abortAfterCalls: 7 });
    assert.equal(summary.cancelled, true);
    assert.equal(calls.length, 7);
    assert.equal(summary.links.find((item) => item.id === "internet")?.state, "done");
    assert.equal(summary.links.find((item) => item.id === "server")?.state, "done");
    assert.equal(summary.links.find((item) => item.id === "image")?.state, "pending");
    assert.equal(computeVerdict(summary).key, "cancelled");
  }
}

runnerTests()
  .then(() => console.log("network-test tests passed"))
  .catch((error) => {
    console.error(error);
    process.exit(1);
  });
