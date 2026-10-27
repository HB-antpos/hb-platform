import {
  API_ENTRY_HOSTS,
  parseEntryUrl,
} from "@hb/pos-api-client/transport-failover";

import type {
  NetworkCheckEntryKind,
  NetworkCheckEntryState,
  NetworkCheckMeasurements,
  NetworkCheckRun,
  NetworkDnsResult,
  NetworkEntryMeasurement,
  NetworkLocalInfo,
  NetworkProbeError,
  NetworkProbeSample,
  NetworkPublicIpResult,
  NetworkSeriesResult,
} from "./network-check-types";

/** 参照地址：Cloudflare 的公共服务，按 IP 访问不依赖 DNS，对应 WPF 的 1.1.1.1:443 参照连接。 */
export const NETWORK_CHECK_REFERENCE_URL = "https://1.1.1.1/cdn-cgi/trace";
/** 公共 DNS（DNS over HTTPS），先 Cloudflare 后 Google；对应 WPF 的 DnsWireClient 直查 1.1.1.1 / 8.8.8.8。 */
export const NETWORK_CHECK_DOH_URLS = [
  "https://1.1.1.1/dns-query",
  "https://dns.google/resolve",
] as const;
/**
 * 主站诊断接口：返回服务器看到的公网 IP 与请求编号。只走直连——经过 EdgeOne 时
 * 服务器看到的是边缘节点地址，不是门店的公网 IP。
 */
export const NETWORK_CHECK_PUBLIC_IP_URL =
  "https://hotbargain.vip/api/health/diagnostics";

/** 第一次请求含 DNS、建连与 TLS，给得宽一些（WPF 的健康检查给 12 秒）。 */
const FIRST_TIMEOUT_MS = 10_000;
const SAMPLE_TIMEOUT_MS = 4_000;
const BURST_TIMEOUT_MS = 6_000;
const DNS_TIMEOUT_MS = 5_000;
export const NETWORK_CHECK_SEQUENTIAL_ATTEMPTS = 6;
export const NETWORK_CHECK_REFERENCE_ATTEMPTS = 5;
export const NETWORK_CHECK_BURST_ATTEMPTS = 6;
/** 逐个请求的总预算：超过就停，避免线路很差时整个检测拖到一分钟以上。 */
const SEQUENTIAL_BUDGET_MS = 10_000;
/** 相邻两次请求的间隔：太密测不出抖动。 */
const SAMPLE_INTERVAL_MS = 200;

type FetchLikeResponse = Readonly<{
  status: number;
  headers: Readonly<{ get(name: string): string | null }>;
  text(): Promise<string>;
}>;

export type NetworkCheckFetch = (
  url: string,
  init: Readonly<{
    method: "GET";
    headers: Record<string, string>;
    signal: AbortSignal;
    cache: "no-store";
  }>,
) => Promise<FetchLikeResponse>;

export interface NetworkCheckPorts {
  /** 直接发请求：不得经过入口故障转移，也不改动入口选择器状态。 */
  fetch: NetworkCheckFetch;
  /** 单调递增毫秒时钟（计耗时）。 */
  now(): number;
  /** 墙钟毫秒（算时钟偏差）。 */
  wallNow(): number;
  sleep(ms: number): Promise<void>;
  readLocalNetwork(): Promise<NetworkLocalInfo>;
  /** App 业务请求的入口状态：后台设置的首选、实际在用的入口、是否回退中。 */
  readEntryState(): NetworkCheckEntryState;
}

export type NetworkCheckTarget = Readonly<{
  kind: NetworkCheckEntryKind;
  host: string;
  baseUrl: string;
  /** IP 字面量或 localhost：不查公共 DNS。 */
  literal: boolean;
}>;

/** 规范化 API 基础地址并列出要测的入口：入口组内逐条测 EdgeOne、AWS 悉尼、直连三条，否则只测配置地址。 */
export function resolveNetworkCheckTargets(
  apiBaseUrl: string,
): NetworkCheckTarget[] {
  const normalized = apiBaseUrl.trim().replace(/\/+$/u, "");
  const entry = parseEntryUrl(normalized);
  if (entry) {
    return (["edge", "aws", "direct"] as const).map((kind) => ({
      kind,
      host: API_ENTRY_HOSTS[kind],
      literal: false,
      baseUrl: entry.withHost(API_ENTRY_HOSTS[kind]).replace(/\/+$/u, ""),
    }));
  }
  let host = normalized;
  try {
    host = new URL(normalized).hostname;
  } catch {
    // 地址无法解析时按原样显示，后续请求会失败并如实记录。
  }
  return [
    {
      kind: "configured",
      host,
      baseUrl: normalized,
      literal: isLiteralHost(host),
    },
  ];
}

function isLiteralHost(host: string): boolean {
  const normalized = host.replace(/^\[|\]$/gu, "").toLowerCase();
  return (
    normalized === "localhost" ||
    /^\d{1,3}(?:\.\d{1,3}){3}$/u.test(normalized) ||
    normalized.includes(":")
  );
}

export function emptyMeasurements(): NetworkCheckMeasurements {
  return {
    local: null,
    reference: null,
    entries: [],
    publicIp: null,
    clockSkewMs: null,
    activeEntry: null,
    preferredEntry: null,
    usingFallback: false,
  };
}

/** 统计一组请求；逐个请求才计算抖动。 */
export function summarizeSeries(
  samples: readonly NetworkProbeSample[],
  withJitter: boolean,
): NetworkSeriesResult {
  const successTimes = samples
    .filter((sample) => sample.ok)
    .map((sample) => sample.ms);
  let jitterTotal = 0;
  for (let index = 1; index < successTimes.length; index += 1) {
    jitterTotal += Math.abs(successTimes[index]! - successTimes[index - 1]!);
  }
  return {
    attempts: samples.length,
    succeeded: successTimes.length,
    avgMs:
      successTimes.length === 0
        ? null
        : Math.round(
            successTimes.reduce((sum, value) => sum + value, 0) /
              successTimes.length,
          ),
    maxMs: successTimes.length === 0 ? null : Math.max(...successTimes),
    jitterMs:
      withJitter && successTimes.length > 1
        ? Math.round(jitterTotal / (successTimes.length - 1))
        : null,
    failures: samples
      .filter((sample) => !sample.ok)
      .map((sample) => sample.error ?? "network"),
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * 判断健康检查的返回：与 WPF `CheckReplyAsync` 一致，必须是 POS API 的 ApiResult 返回包且 isOnline=true。
 * 520~530 是 EdgeOne 自己生成的错误（源站与 ASP.NET 不会返回），单独归类。
 */
export function classifyHealthResponse(
  status: number,
  bodyText: string,
): Readonly<{ ok: boolean; error?: NetworkProbeError; serverTime?: string }> {
  if (status >= 520 && status <= 530) return { ok: false, error: "edge" };
  if (status < 200 || status >= 300) return { ok: false, error: "http" };
  let json: unknown;
  try {
    json = JSON.parse(bodyText);
  } catch {
    return { ok: false, error: "notPosApi" };
  }
  if (!isRecord(json) || typeof json.success !== "boolean") {
    return { ok: false, error: "notPosApi" };
  }
  const data = json.data;
  if (json.success && isRecord(data) && data.isOnline === true) {
    return typeof data.serverTime === "string"
      ? { ok: true, serverTime: data.serverTime }
      : { ok: true };
  }
  return { ok: false, error: "http" };
}

/** 从 DoH JSON 返回里取 A 记录地址（CNAME 记录跳过）。 */
export function parseDohAddresses(bodyText: string): string[] {
  try {
    const json: unknown = JSON.parse(bodyText);
    if (!isRecord(json) || json.Status !== 0 || !Array.isArray(json.Answer)) {
      return [];
    }
    return json.Answer.filter(
      (answer): answer is Record<string, unknown> =>
        isRecord(answer) && answer.type === 1 && typeof answer.data === "string",
    ).map((answer) => answer.data as string);
  } catch {
    return [];
  }
}

function withCacheBuster(url: string, nonce: string): string {
  return `${url}${url.includes("?") ? "&" : "?"}_t=${encodeURIComponent(nonce)}`;
}

class NetworkCheckCancelled extends Error {
  constructor() {
    super("NETWORK_CHECK_CANCELLED");
  }
}

type TimedResult = Readonly<{
  sample: NetworkProbeSample;
  body: string | null;
  wallStartMs: number;
}>;

/**
 * 发一次 GET 并计时，任何异常都变成失败样本，不向上抛。
 * 读完响应体才算完成；外部取消会一并中止进行中的请求。
 */
async function timedGet(
  ports: NetworkCheckPorts,
  url: string,
  timeoutMs: number,
  outer: AbortSignal,
  headers: Record<string, string> = {},
): Promise<TimedResult> {
  const controller = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, timeoutMs);
  const onOuterAbort = () => controller.abort();
  outer.addEventListener("abort", onOuterAbort);
  const wallStartMs = ports.wallNow();
  const startedAt = ports.now();
  try {
    const response = await ports.fetch(url, {
      method: "GET",
      // 每次都要真正到达服务器，避免 iOS HTTP 缓存让结果虚快。
      headers: { "Cache-Control": "no-cache, no-store", Pragma: "no-cache", ...headers },
      signal: controller.signal,
      cache: "no-store",
    });
    const body = await response.text();
    const eoLogUuid = response.headers.get("eo-log-uuid")?.trim();
    return {
      sample: {
        ok: response.status >= 200 && response.status < 300,
        ms: Math.round(ports.now() - startedAt),
        status: response.status,
        ...(eoLogUuid ? { eoLogUuid } : {}),
      },
      body,
      wallStartMs,
    };
  } catch {
    return {
      sample: {
        ok: false,
        ms: Math.round(ports.now() - startedAt),
        error: timedOut ? "timeout" : "network",
      },
      body: null,
      wallStartMs,
    };
  } finally {
    clearTimeout(timer);
    outer.removeEventListener("abort", onOuterAbort);
  }
}

export interface RunNetworkCheckOptions {
  apiBaseUrl: string;
  signal: AbortSignal;
  /** 每有一项新结果就回调一次完整快照（新对象），界面直接 setState。 */
  onUpdate(run: NetworkCheckRun): void;
  ports: NetworkCheckPorts;
  nowIso(): string;
}

/**
 * 只读检测（入口组内逐条测三个入口），分三段：
 * 1. 基础（并行）：本机网络、公网 IP、各入口公共 DNS、参照地址与各入口的第一次请求；
 * 2. 稳定性：各入口与参照地址各自逐个请求若干次（不同主机之间并行）；
 * 3. 并发：各入口依次「同时发 6 个请求」，看是否被路由器 / 防火墙限制并发。
 * 与 WPF 一样把并发测试放在最后单独做，避免自己的并发污染前面的结果。
 */
export async function runNetworkCheck(
  options: RunNetworkCheckOptions,
): Promise<NetworkCheckRun> {
  const { ports, signal } = options;
  const targets = resolveNetworkCheckTargets(options.apiBaseUrl);
  const inGroup = targets.some((target) => target.kind !== "configured");
  const entryState = inGroup ? ports.readEntryState() : null;
  const startedAt = ports.now();
  let run: NetworkCheckRun = {
    phase: "basics",
    measurements: {
      ...emptyMeasurements(),
      entries: targets.map((target) => ({
        kind: target.kind,
        host: target.host,
        baseUrl: target.baseUrl,
        dns: null,
        first: null,
        sequential: null,
        burst: null,
      })),
      reference: {
        url: NETWORK_CHECK_REFERENCE_URL,
        first: null,
        sequential: null,
      },
      activeEntry: entryState?.active ?? null,
      preferredEntry: entryState?.preferred ?? null,
      usingFallback: entryState?.usingFallback ?? false,
    },
    startedAtIso: options.nowIso(),
    finishedAtIso: null,
    totalMs: null,
  };
  const publish = () => options.onUpdate(run);
  const ensureActive = () => {
    if (signal.aborted) throw new NetworkCheckCancelled();
  };
  const patch = (
    update: (measurements: NetworkCheckMeasurements) => NetworkCheckMeasurements,
  ) => {
    if (signal.aborted) return;
    run = { ...run, measurements: update(run.measurements) };
    publish();
  };
  const patchEntry = (
    index: number,
    update: (entry: NetworkEntryMeasurement) => NetworkEntryMeasurement,
  ) =>
    patch((measurements) => ({
      ...measurements,
      entries: measurements.entries.map((entry, entryIndex) =>
        entryIndex === index ? update(entry) : entry,
      ),
    }));

  const runId = `${ports.wallNow()}`;
  const healthUrl = (target: NetworkCheckTarget, nonce: string) =>
    withCacheBuster(`${target.baseUrl}/api/v1/health`, `${runId}-${nonce}`);

  const probeHealth = async (
    target: NetworkCheckTarget,
    nonce: string,
    timeoutMs: number,
  ): Promise<TimedResult> => {
    const result = await timedGet(ports, healthUrl(target, nonce), timeoutMs, signal);
    if (result.body === null || result.sample.status === undefined) return result;
    const verdict = classifyHealthResponse(result.sample.status, result.body);
    return {
      ...result,
      sample: {
        ...result.sample,
        ok: verdict.ok,
        ...(verdict.error ? { error: verdict.error } : {}),
        ...(verdict.serverTime ? { serverTime: verdict.serverTime } : {}),
      },
    };
  };

  const probeReference = async (nonce: string, timeoutMs: number) =>
    (await timedGet(
      ports,
      withCacheBuster(NETWORK_CHECK_REFERENCE_URL, `${runId}-${nonce}`),
      timeoutMs,
      signal,
    )).sample;

  const resolveDns = async (host: string): Promise<NetworkDnsResult> => {
    const startedDns = ports.now();
    for (const endpoint of NETWORK_CHECK_DOH_URLS) {
      const result = await timedGet(
        ports,
        `${endpoint}?name=${encodeURIComponent(host)}&type=A`,
        DNS_TIMEOUT_MS,
        signal,
        { Accept: "application/dns-json" },
      );
      const addresses =
        result.sample.ok && result.body ? parseDohAddresses(result.body) : [];
      if (addresses.length > 0) {
        return { ms: result.sample.ms, addresses, failed: false };
      }
      if (signal.aborted) break;
    }
    return { ms: Math.round(ports.now() - startedDns), addresses: [], failed: true };
  };

  const fetchPublicIp = async (): Promise<NetworkPublicIpResult> => {
    const result = await timedGet(
      ports,
      withCacheBuster(NETWORK_CHECK_PUBLIC_IP_URL, runId),
      SAMPLE_TIMEOUT_MS * 2,
      signal,
    );
    if (!result.sample.ok || !result.body) {
      return { ip: null, requestId: null, failed: true };
    }
    try {
      const json: unknown = JSON.parse(result.body);
      const value = isRecord(json) ? json : {};
      return {
        ip: typeof value.clientIp === "string" && value.clientIp ? value.clientIp : null,
        requestId:
          typeof value.requestId === "string" && value.requestId ? value.requestId : null,
        failed: false,
      };
    } catch {
      return { ip: null, requestId: null, failed: true };
    }
  };

  /** 逐个请求：超过总预算就停（至少测一次），与 WPF ProbeConnectStabilityAsync 的逐个模式一致。 */
  const sequential = async (
    attempts: number,
    request: (index: number) => Promise<NetworkProbeSample>,
  ): Promise<NetworkSeriesResult> => {
    const samples: NetworkProbeSample[] = [];
    const seriesStart = ports.now();
    for (let index = 0; index < attempts; index += 1) {
      ensureActive();
      if (index > 0) {
        if (ports.now() - seriesStart > SEQUENTIAL_BUDGET_MS) break;
        await ports.sleep(SAMPLE_INTERVAL_MS);
        ensureActive();
      }
      samples.push(await request(index));
    }
    return summarizeSeries(samples, true);
  };

  let clockSample: TimedResult | null = null;
  const rememberClock = (result: TimedResult) => {
    // 优先用直连入口的服务器时间（不经边缘节点转发）；没有再用其他入口。
    if (!result.sample.ok || !result.sample.serverTime) return;
    if (clockSample === null) clockSample = result;
  };

  publish();
  try {
    // 1. 基础检测：各项互不依赖，并行进行。
    const basics: Promise<void>[] = [
      ports
        .readLocalNetwork()
        .catch(
          (): NetworkLocalInfo => ({ known: false, connected: null, type: null, ipAddress: null }),
        )
        .then((local) => patch((measurements) => ({ ...measurements, local }))),
      probeReference("ref-first", FIRST_TIMEOUT_MS).then((first) =>
        patch((measurements) => ({
          ...measurements,
          reference: measurements.reference && { ...measurements.reference, first },
        })),
      ),
    ];
    if (inGroup) {
      basics.push(
        fetchPublicIp().then((publicIp) =>
          patch((measurements) => ({ ...measurements, publicIp })),
        ),
      );
    }
    const orderedTargets = targets
      .map((target, index) => ({ target, index }))
      // 直连在前：时钟偏差优先取直连入口的服务器时间。
      .sort((left, right) =>
        left.target.kind === "direct" ? -1 : right.target.kind === "direct" ? 1 : 0,
      );
    const firstResults = new Map<number, TimedResult>();
    for (const { target, index } of orderedTargets) {
      if (!target.literal) {
        basics.push(
          resolveDns(target.host).then((dns) => patchEntry(index, (entry) => ({ ...entry, dns }))),
        );
      }
      basics.push(
        probeHealth(target, "first", FIRST_TIMEOUT_MS).then((result) => {
          firstResults.set(index, result);
          patchEntry(index, (entry) => ({ ...entry, first: result.sample }));
        }),
      );
    }
    await Promise.all(basics);
    ensureActive();
    for (const { index } of orderedTargets) {
      const result = firstResults.get(index);
      if (result) rememberClock(result);
    }

    // 2. 稳定性：不同主机之间并行，同一主机逐个请求。
    run = { ...run, phase: "stability" };
    publish();
    const stability: Promise<void>[] = [
      sequential(NETWORK_CHECK_REFERENCE_ATTEMPTS, (index) =>
        probeReference(`ref-${index}`, SAMPLE_TIMEOUT_MS),
      ).then((series) =>
        patch((measurements) => ({
          ...measurements,
          reference: measurements.reference && {
            ...measurements.reference,
            sequential: series,
          },
        })),
      ),
    ];
    targets.forEach((target, index) => {
      stability.push(
        sequential(NETWORK_CHECK_SEQUENTIAL_ATTEMPTS, async (attempt) => {
          const result = await probeHealth(target, `seq-${attempt}`, SAMPLE_TIMEOUT_MS);
          rememberClock(result);
          return result.sample;
        }).then((series) => patchEntry(index, (entry) => ({ ...entry, sequential: series }))),
      );
    });
    await Promise.all(stability);
    ensureActive();

    // 3. 并发：一次只测一个入口；逐个请求一次都没成功的入口不再测（没有意义，只会拖长时间）。
    run = { ...run, phase: "burst" };
    publish();
    for (const [index, target] of targets.entries()) {
      ensureActive();
      if ((run.measurements.entries[index]?.sequential?.succeeded ?? 0) === 0) continue;
      const samples = await Promise.all(
        Array.from({ length: NETWORK_CHECK_BURST_ATTEMPTS }, async (_, attempt) =>
          (await probeHealth(target, `burst-${attempt}`, BURST_TIMEOUT_MS)).sample,
        ),
      );
      ensureActive();
      patchEntry(index, (entry) => ({ ...entry, burst: summarizeSeries(samples, false) }));
    }

    const clock = clockSample as TimedResult | null;
    const serverMs = clock?.sample.serverTime ? Date.parse(clock.sample.serverTime) : Number.NaN;
    patch((measurements) => ({
      ...measurements,
      // 用请求耗时的一半作为单程延迟的粗略校正（与 WPF 一致）。
      clockSkewMs:
        clock && Number.isFinite(serverMs)
          ? Math.round(clock.wallStartMs + clock.sample.ms / 2 - serverMs)
          : null,
    }));
    run = {
      ...run,
      phase: "done",
      finishedAtIso: options.nowIso(),
      totalMs: Math.round(ports.now() - startedAt),
    };
    publish();
    return run;
  } catch (error) {
    if (!(error instanceof NetworkCheckCancelled) && !signal.aborted) throw error;
    run = {
      ...run,
      phase: "cancelled",
      finishedAtIso: options.nowIso(),
      totalMs: Math.round(ports.now() - startedAt),
    };
    options.onUpdate(run);
    return run;
  }
}
