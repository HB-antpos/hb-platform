import {
  NETWORK_TEST_LINK_IDS,
  type NetworkLinkResult,
  type NetworkSample,
  type NetworkTestContext,
  type NetworkTestLinkId,
  type NetworkTestRunSummary,
} from "./types";

/** 外网基线：Cloudflare 的轻量诊断地址，全球节点、响应只有几百字节，与我们的服务器无关。 */
export const INTERNET_PROBE_URL = "https://cloudflare.com/cdn-cgi/trace";
/** 商品图片：与 ProductImageUrlHelper 同一个上海 COS 目录（HEAD 请求，不下载图片本身）。 */
export const IMAGE_PROBE_URL = "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/";
/** 下载测速文件大小（KB），与后端 HealthController.DefaultDownloadKilobytes 一致。 */
export const DOWNLOAD_PROBE_KB = 512;

/** 每条链路的请求次数：主服务器次数最多，用来看稳定性（抖动 / 丢包）。 */
export const LINK_PLANNED_COUNTS: Record<NetworkTestLinkId, number> = {
  internet: 5,
  server: 10,
  auth: 3,
  database: 1,
  image: 3,
  download: 1,
};

const REQUEST_TIMEOUT_MS = 8000;
const DOWNLOAD_TIMEOUT_MS = 20000;
/** 同一链路相邻两次请求的间隔：太密会测成连接复用的最好情况，看不出抖动。 */
const SAMPLE_INTERVAL_MS = 250;

export interface NetworkTestRunnerPorts {
  fetchImpl: typeof fetch;
  /** 单调递增的毫秒时钟（performance.now 优先）。 */
  now: () => number;
  nowIso: () => string;
  sleep: (ms: number) => Promise<void>;
}

export function createDefaultRunnerPorts(): NetworkTestRunnerPorts {
  const perf = (globalThis as { performance?: { now?: () => number } }).performance;
  return {
    fetchImpl: (input, init) => fetch(input, init),
    now: typeof perf?.now === "function" ? () => perf.now!() : () => Date.now(),
    nowIso: () => new Date().toISOString(),
    sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
  };
}

/**
 * 由 API 基础地址拼出后端接口地址。
 * 基础地址可能是 https://hotbargain.vip/api，也可能是局域网 http://x:5002（不带 /api），两种都要拼对。
 */
export function buildApiUrl(apiBaseUrl: string, path: string): string {
  const normalized = apiBaseUrl.replace(/\/+$/, "");
  const prefix = normalized.endsWith("/api") ? normalized : `${normalized}/api`;
  return `${prefix}${path.startsWith("/") ? path : `/${path}`}`;
}

/** 给 GET 地址加时间戳参数，避免任何一层缓存让测出的耗时偏小。 */
function withCacheBuster(url: string, nonce: string) {
  return `${url}${url.includes("?") ? "&" : "?"}_t=${encodeURIComponent(nonce)}`;
}

export function createInitialLinks(context: Pick<NetworkTestContext, "accessToken">): NetworkLinkResult[] {
  return NETWORK_TEST_LINK_IDS.map((id) => ({
    id,
    // 未登录时登录态接口一开始就标成跳过，界面上不会出现「等待」。
    state: id === "auth" && !context.accessToken ? "skipped" : "pending",
    plannedCount: LINK_PLANNED_COUNTS[id],
    samples: [],
  }));
}

type ResponseReader = "none" | "json" | "arrayBuffer";

interface TimedResponse {
  sample: NetworkSample;
  json?: unknown;
  byteLength?: number;
}

/**
 * 发一次请求并计时：任何异常都转成失败样本，不向上抛。
 * 外部取消（用户点「停止测试」）会把正在进行的请求一起中止。
 */
async function timedRequest(
  ports: NetworkTestRunnerPorts,
  url: string,
  options: {
    method?: "GET" | "HEAD";
    headers?: Record<string, string>;
    timeoutMs: number;
    signal: AbortSignal;
    reader: ResponseReader;
    /** 视为成功的状态码；默认 2xx。 */
    isOk?: (status: number) => boolean;
  }
): Promise<TimedResponse> {
  const controller = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, options.timeoutMs);
  const onOuterAbort = () => controller.abort();
  options.signal.addEventListener("abort", onOuterAbort);
  const startedAt = ports.now();
  try {
    const response = await ports.fetchImpl(url, {
      method: options.method ?? "GET",
      headers: { "Cache-Control": "no-cache", ...options.headers },
      signal: controller.signal,
    });
    let json: unknown;
    let byteLength: number | undefined;
    // 读完响应体才算一次完整请求（下载测速必须读完整个文件）。
    if (options.reader === "arrayBuffer") {
      byteLength = (await response.arrayBuffer()).byteLength;
    } else if (options.reader === "json") {
      json = await response.json().catch(() => undefined);
    }
    const ms = Math.round(ports.now() - startedAt);
    const ok = (options.isOk ?? ((status) => status >= 200 && status < 300))(response.status);
    return {
      sample: {
        ok,
        ms,
        status: response.status,
        ...(ok ? {} : { error: response.status === 401 ? "unauthorized" : "http" }),
      },
      json,
      byteLength,
    };
  } catch {
    const ms = Math.round(ports.now() - startedAt);
    return { sample: { ok: false, ms, error: timedOut ? "timeout" : "network" } };
  } finally {
    clearTimeout(timer);
    options.signal.removeEventListener("abort", onOuterAbort);
  }
}

function readDiagnostics(json: unknown) {
  const value = (json ?? {}) as {
    clientIp?: unknown;
    requestId?: unknown;
    database?: { ok?: unknown; latencyMs?: unknown };
  };
  return {
    clientIp: typeof value.clientIp === "string" && value.clientIp ? value.clientIp : null,
    requestId: typeof value.requestId === "string" && value.requestId ? value.requestId : null,
    databaseLatencyMs:
      value.database?.ok === true && typeof value.database.latencyMs === "number" ? value.database.latencyMs : null,
  };
}

export interface RunNetworkTestOptions {
  context: NetworkTestContext;
  signal: AbortSignal;
  /** 每拿到一个样本就回调一次完整快照（新对象），界面直接 setState。 */
  onUpdate: (summary: NetworkTestRunSummary) => void;
  ports?: NetworkTestRunnerPorts;
}

/** 按顺序跑完 6 条链路；被取消时立即停止并标记 cancelled。 */
export async function runNetworkTest({
  context,
  signal,
  onUpdate,
  ports = createDefaultRunnerPorts(),
}: RunNetworkTestOptions): Promise<NetworkTestRunSummary> {
  let summary: NetworkTestRunSummary = {
    links: createInitialLinks(context),
    clientIp: null,
    requestId: null,
    startedAtIso: ports.nowIso(),
    finishedAtIso: null,
    cancelled: false,
  };
  const publish = () => onUpdate(summary);
  const updateLink = (id: NetworkTestLinkId, patch: (link: NetworkLinkResult) => NetworkLinkResult) => {
    summary = { ...summary, links: summary.links.map((link) => (link.id === id ? patch(link) : link)) };
    publish();
  };
  const addSample = (id: NetworkTestLinkId, sample: NetworkSample) =>
    updateLink(id, (link) => ({ ...link, samples: [...link.samples, sample] }));

  /** 同一链路重复请求；返回 false 表示被取消。 */
  const repeat = async (id: NetworkTestLinkId, request: (index: number) => Promise<NetworkSample>) => {
    updateLink(id, (link) => ({ ...link, state: "running" }));
    for (let index = 0; index < LINK_PLANNED_COUNTS[id]; index += 1) {
      if (signal.aborted) return false;
      if (index > 0) await ports.sleep(SAMPLE_INTERVAL_MS);
      if (signal.aborted) return false;
      addSample(id, await request(index));
    }
    updateLink(id, (link) => ({ ...link, state: "done" }));
    return true;
  };

  const runId = ports.nowIso();
  const steps: (() => Promise<boolean>)[] = [
    () =>
      repeat("internet", async (index) =>
        (await timedRequest(ports, withCacheBuster(INTERNET_PROBE_URL, `${runId}-${index}`), {
          timeoutMs: REQUEST_TIMEOUT_MS,
          signal,
          reader: "none",
        })).sample
      ),
    () =>
      repeat("server", async (index) =>
        (await timedRequest(ports, withCacheBuster(buildApiUrl(context.apiBaseUrl, "/health"), `${runId}-${index}`), {
          timeoutMs: REQUEST_TIMEOUT_MS,
          signal,
          reader: "none",
        })).sample
      ),
    async () => {
      if (!context.accessToken) return true;
      return repeat("auth", async (index) =>
        (await timedRequest(ports, withCacheBuster(buildApiUrl(context.apiBaseUrl, "/health/auth"), `${runId}-${index}`), {
          headers: { Authorization: `Bearer ${context.accessToken}` },
          timeoutMs: REQUEST_TIMEOUT_MS,
          signal,
          reader: "none",
        })).sample
      );
    },
    async () => {
      updateLink("database", (link) => ({ ...link, state: "running" }));
      const response = await timedRequest(
        ports,
        withCacheBuster(buildApiUrl(context.apiBaseUrl, "/health/diagnostics"), runId),
        { timeoutMs: REQUEST_TIMEOUT_MS, signal, reader: "json" }
      );
      if (signal.aborted) return false;
      if (response.sample.status === 404) {
        // 旧版后端没有诊断接口：标成「服务器未提供」，不算失败。
        updateLink("database", (link) => ({ ...link, state: "unavailable" }));
        return true;
      }
      const diagnostics = readDiagnostics(response.json);
      summary = { ...summary, clientIp: diagnostics.clientIp, requestId: diagnostics.requestId };
      updateLink("database", (link) => ({
        ...link,
        state: "done",
        samples: [response.sample],
        serverLatencyMs: response.sample.ok ? diagnostics.databaseLatencyMs : null,
      }));
      return true;
    },
    () =>
      repeat("image", async () =>
        (await timedRequest(ports, IMAGE_PROBE_URL, {
          method: "HEAD",
          timeoutMs: REQUEST_TIMEOUT_MS,
          signal,
          reader: "none",
          // 只测连通与耗时：COS 对目录返回 200/403/404 都说明请求到达了图片服务器。
          isOk: (status) => status > 0 && status < 500,
        })).sample
      ),
    async () => {
      updateLink("download", (link) => ({ ...link, state: "running" }));
      const response = await timedRequest(
        ports,
        withCacheBuster(buildApiUrl(context.apiBaseUrl, `/health/download?kb=${DOWNLOAD_PROBE_KB}`), runId),
        { timeoutMs: DOWNLOAD_TIMEOUT_MS, signal, reader: "arrayBuffer" }
      );
      if (signal.aborted) return false;
      if (response.sample.status === 404) {
        updateLink("download", (link) => ({ ...link, state: "unavailable" }));
        return true;
      }
      const bytes = response.sample.ok ? response.byteLength ?? 0 : 0;
      const seconds = Math.max(response.sample.ms, 1) / 1000;
      updateLink("download", (link) => ({
        ...link,
        state: "done",
        samples: [response.sample],
        downloadBytes: bytes,
        downloadKBps: response.sample.ok && bytes > 0 ? bytes / 1024 / seconds : null,
      }));
      return true;
    },
  ];

  publish();
  for (const step of steps) {
    if (signal.aborted || !(await step())) {
      // 停止时正在跑的那一项：有样本的按已测到的算，没样本的退回「等待」。
      summary = {
        ...summary,
        links: summary.links.map((link) =>
          link.state === "running" ? { ...link, state: link.samples.length > 0 ? "done" : "pending" } : link
        ),
        cancelled: true,
        finishedAtIso: ports.nowIso(),
      };
      publish();
      return summary;
    }
  }

  summary = { ...summary, finishedAtIso: ports.nowIso() };
  publish();
  return summary;
}
