import assert from "node:assert/strict";
import test from "node:test";

import { networkCheckChineseCopy, networkCheckEnglishCopy } from "./network-check-copy";
import { buildNetworkCheckReport } from "./network-check-report";
import {
  buildEntryRow,
  evaluateNetworkCheck,
  type NetworkVerdictKey,
} from "./network-check-rules";
import { emptyMeasurements } from "./network-check-runner";
import type {
  NetworkCheckMeasurements,
  NetworkEntryMeasurement,
  NetworkSeriesResult,
} from "./network-check-types";

function series(
  succeeded: number,
  attempts: number,
  avgMs: number | null = 300,
  maxMs: number | null = 400,
): NetworkSeriesResult {
  return {
    attempts,
    succeeded,
    avgMs: succeeded === 0 ? null : avgMs,
    maxMs: succeeded === 0 ? null : maxMs,
    jitterMs: succeeded > 1 ? 20 : null,
    failures: Array.from({ length: attempts - succeeded }, () => "timeout" as const),
  };
}

function entry(
  kind: "edge" | "direct",
  overrides: Partial<NetworkEntryMeasurement> = {},
): NetworkEntryMeasurement {
  return {
    kind,
    host: kind === "edge" ? "pos.hotbargain.vip" : "hotbargain.vip",
    baseUrl: `https://${kind === "edge" ? "pos.hotbargain.vip" : "hotbargain.vip"}/pos-api`,
    dns: { ms: 40, addresses: ["43.159.98.78"], failed: false },
    first: { ok: true, ms: 900, status: 200 },
    sequential: series(6, 6),
    burst: series(6, 6, 700, 900),
    ...overrides,
  };
}

const down: Partial<NetworkEntryMeasurement> = {
  first: { ok: false, ms: 10_000, error: "timeout" },
  sequential: series(0, 3),
  burst: null,
};

function measurements(
  overrides: Partial<NetworkCheckMeasurements> = {},
): NetworkCheckMeasurements {
  return {
    ...emptyMeasurements(),
    local: { known: true, connected: true, type: "WIFI", ipAddress: "192.168.1.20" },
    reference: {
      url: "https://1.1.1.1/cdn-cgi/trace",
      first: { ok: true, ms: 80, status: 200 },
      sequential: series(5, 5, 40, 60),
    },
    entries: [entry("edge"), entry("direct")],
    publicIp: { ip: "124.187.52.242", requestId: "req-1", failed: false },
    clockSkewMs: 1_200,
    activeEntry: "edge",
    ...overrides,
  };
}

function verdictKey(input: NetworkCheckMeasurements): NetworkVerdictKey {
  return evaluateNetworkCheck(input).key;
}

test("全部正常时结论为 allGood", () => {
  assert.equal(verdictKey(measurements()), "allGood");
});

test("系统报告未联网时先判本机没有网络", () => {
  assert.equal(
    verdictKey(
      measurements({
        local: { known: true, connected: false, type: "NONE", ipAddress: null },
        entries: [entry("edge", down), entry("direct", down)],
      }),
    ),
    "noLocalNetwork",
  );
});

test("读不到系统网络状态时不据此判定没网", () => {
  assert.equal(
    verdictKey(
      measurements({ local: { known: false, connected: null, type: null, ipAddress: null } }),
    ),
    "allGood",
  );
});

test("两条入口和参照地址都不通：上不了外网", () => {
  assert.equal(
    verdictKey(
      measurements({
        reference: {
          url: "https://1.1.1.1/cdn-cgi/trace",
          first: { ok: false, ms: 10_000, error: "network" },
          sequential: series(0, 5),
        },
        entries: [entry("edge", down), entry("direct", down)],
      }),
    ),
    "internetUnreachable",
  );
});

test("参照地址正常而两条入口都不通：连不上服务器；有 HTTP 错误则判服务器报错", () => {
  assert.equal(
    verdictKey(measurements({ entries: [entry("edge", down), entry("direct", down)] })),
    "serverUnreachable",
  );
  assert.equal(
    verdictKey(
      measurements({
        entries: [
          entry("edge", { ...down, first: { ok: false, ms: 300, status: 502, error: "http" } }),
          entry("direct", down),
        ],
      }),
    ),
    "serverError",
  );
  assert.equal(
    verdictKey(
      measurements({
        entries: [
          entry("edge", { ...down, first: { ok: false, ms: 300, status: 200, error: "notPosApi" } }),
          entry("direct", down),
        ],
      }),
    ),
    "notPosApi",
  );
});

test("只有一条入口不通时指明是哪条", () => {
  assert.deepEqual(
    evaluateNetworkCheck(measurements({ entries: [entry("edge", down), entry("direct")] })),
    { key: "edgeDown", tone: "warn", entry: "edge" },
  );
  assert.deepEqual(
    evaluateNetworkCheck(measurements({ entries: [entry("edge"), entry("direct", down)] })),
    { key: "directDown", tone: "warn", entry: "direct" },
  );
});

test("时钟偏差超过 5 分钟", () => {
  assert.equal(verdictKey(measurements({ clockSkewMs: -6 * 60_000 })), "clockSkew");
});

test("入口偶发失败：参照稳定判到服务器的路丢包，参照也不稳判本机链路不稳", () => {
  const lossyEdge = entry("edge", { sequential: series(4, 6) });
  assert.deepEqual(
    evaluateNetworkCheck(measurements({ entries: [lossyEdge, entry("direct")] })),
    { key: "entryLossy", tone: "warn", entry: "edge" },
  );
  assert.equal(
    verdictKey(
      measurements({
        entries: [lossyEdge, entry("direct")],
        reference: {
          url: "https://1.1.1.1/cdn-cgi/trace",
          first: { ok: true, ms: 80, status: 200 },
          sequential: series(3, 5, 40, 60),
        },
      }),
    ),
    "linkUnstable",
  );
});

test("第一次请求失败、后面又成功也算丢包", () => {
  assert.equal(
    verdictKey(
      measurements({
        entries: [entry("edge"), entry("direct", { first: { ok: false, ms: 10_000, error: "timeout" } })],
      }),
    ),
    "entryLossy",
  );
});

test("逐个正常而同时请求失败：并发受限", () => {
  assert.deepEqual(
    evaluateNetworkCheck(
      measurements({ entries: [entry("edge"), entry("direct", { burst: series(3, 6) })] }),
    ),
    { key: "burstLimited", tone: "warn", entry: "direct" },
  );
});

test("偏慢：参照也慢判外网整体慢，否则指明慢的入口（首选优先）", () => {
  const slow = { sequential: series(6, 6, 1_800, 2_100) };
  assert.deepEqual(
    evaluateNetworkCheck(measurements({ entries: [entry("edge", slow), entry("direct", slow)] })),
    { key: "entrySlow", tone: "warn", entry: "edge" },
  );
  assert.equal(
    verdictKey(
      measurements({
        entries: [entry("edge", slow), entry("direct")],
        reference: {
          url: "https://1.1.1.1/cdn-cgi/trace",
          first: { ok: true, ms: 1_900, status: 200 },
          sequential: series(5, 5, 1_600, 1_400),
        },
      }),
    ),
    "internetSlow",
  );
});

test("服务器正常、参照地址被屏蔽不算问题", () => {
  assert.deepEqual(
    evaluateNetworkCheck(
      measurements({
        reference: {
          url: "https://1.1.1.1/cdn-cgi/trace",
          first: { ok: false, ms: 10_000, error: "network" },
          sequential: series(0, 5),
        },
      }),
    ),
    { key: "referenceBlocked", tone: "good" },
  );
});

test("行状态：EdgeOne 52x 单独归类，逐个全失败的入口并发项标为跳过", () => {
  const edgeError = entry("edge", { first: { ok: false, ms: 500, status: 522, error: "edge" } });
  assert.deepEqual(buildEntryRow(edgeError, "first", "done").detail, {
    key: "error.edge",
    params: { status: 522 },
  });
  const row = buildEntryRow(entry("direct", down), "burst", "done");
  assert.equal(row.status, "skipped");
  assert.equal(buildEntryRow(entry("edge", { sequential: series(6, 6, 300, 1_600) }), "sequential", "done").status, "slow");
  assert.equal(buildEntryRow(entry("edge", { dns: null }), "dns", "basics").status, "pending");
});

test("报告跟随界面语言，分别列出两条入口与 EdgeOne 日志编号，不含令牌", () => {
  const run = {
    phase: "done" as const,
    measurements: measurements({
      entries: [
        entry("edge", { first: { ok: true, ms: 800, status: 200, eoLogUuid: "15814732990385504304" } }),
        entry("direct", down),
      ],
    }),
    startedAtIso: "2026-10-23T08:00:00.000Z",
    finishedAtIso: "2026-10-23T08:00:12.000Z",
    totalMs: 12_000,
  };
  const context = { deviceLabel: "1042 收银台 (iPad) · iPadOS 26.6", appLabel: "0.2.0 (11)", serverLabel: "https://hotbargain.vip/pos-api" };
  const zh = buildNetworkCheckReport("zh", run, context);
  assert.match(zh, /HB POS 网络检测报告/u);
  assert.match(zh, /时间：2026-10-23 19:00:12/u);
  assert.match(zh, /【EdgeOne · pos\.hotbargain\.vip】/u);
  assert.match(zh, /【直连 · hotbargain\.vip】/u);
  assert.match(zh, /结论：直连入口不通，EdgeOne 正常/u);
  assert.match(zh, /加速入口 EdgeOne 日志编号：15814732990385504304/u);
  assert.match(zh, /公网地址：正常 · 124\.187\.52\.242/u);
  const en = buildNetworkCheckReport("en", run, context);
  assert.match(en, /HB POS network report/u);
  assert.match(en, /Finding: Direct entry is down; EdgeOne works/u);
  assert.doesNotMatch(en, /Bearer|token/iu);
});

test("中英文案键一一对应", () => {
  assert.deepEqual(
    Object.keys(networkCheckChineseCopy).sort(),
    Object.keys(networkCheckEnglishCopy).sort(),
  );
});
