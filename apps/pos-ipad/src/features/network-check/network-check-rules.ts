import type {
  NetworkCheckEntryKind,
  NetworkCheckMeasurements,
  NetworkCheckPhase,
  NetworkEntryMeasurement,
  NetworkProbeError,
  NetworkProbeSample,
  NetworkSeriesResult,
} from "./network-check-types";

/**
 * 把实测数据变成「逐项状态 + 一句结论」。纯函数，阈值与判断全部在这里，方便单测。
 * 判断顺序参照 WPF `NetworkDiagnosisRules.Evaluate`，并加上双入口对比。
 */

/** 普通请求（连接已建立）超过它算慢：设备 API 只给几秒，这么慢就会表现为离线。与 WPF 一致。 */
export const NETWORK_SLOW_MS = 1500;
/** 第一次请求含 DNS、建连和 TLS 握手，到新加坡健康时也要 1 秒左右，单独放宽。 */
export const NETWORK_FIRST_SLOW_MS = 3000;
/** 同时发多个请求要各自建连握手，单独放宽。 */
export const NETWORK_BURST_SLOW_MS = 3000;
export const NETWORK_MAX_CLOCK_SKEW_MS = 5 * 60_000;

export type NetworkRowStatus = "ok" | "slow" | "failed" | "skipped" | "pending";

/** 行详情：copy 键 + 插值参数，由界面和报告各自按语言翻译。 */
export type NetworkRowDetail = Readonly<{
  key: string;
  params?: Readonly<Record<string, string | number>>;
}>;

export type NetworkRow = Readonly<{
  status: NetworkRowStatus;
  ms: number | null;
  detail: NetworkRowDetail;
}>;

export type NetworkEntryRowId = "dns" | "first" | "sequential" | "burst";
export const NETWORK_ENTRY_ROW_IDS: readonly NetworkEntryRowId[] = [
  "dns",
  "first",
  "sequential",
  "burst",
];

export type NetworkVerdictKey =
  | "noLocalNetwork"
  | "internetUnreachable"
  | "notPosApi"
  | "serverError"
  | "serverUnreachable"
  | "edgeDown"
  | "directDown"
  | "clockSkew"
  | "linkUnstable"
  | "entryLossy"
  | "burstLimited"
  | "internetSlow"
  | "entrySlow"
  | "referenceBlocked"
  | "allGood";

export type NetworkVerdict = Readonly<{
  key: NetworkVerdictKey;
  tone: "good" | "warn" | "bad";
  /** 结论涉及的入口（丢包 / 偏慢 / 并发受限时指明是哪条）。 */
  entry?: NetworkCheckEntryKind;
}>;

export function formatMs(ms: number | null | undefined): string {
  if (ms == null) return "-";
  return ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms)} ms`;
}

function pending(phase: NetworkCheckPhase, ready: boolean): NetworkRowStatus {
  return !ready && phase !== "done" && phase !== "cancelled" ? "pending" : "skipped";
}

const notChecked: NetworkRowDetail = { key: "detail.notChecked" };

function errorDetail(sample: NetworkProbeSample): NetworkRowDetail {
  return {
    key: `error.${sample.error ?? "network"}`,
    params: { status: sample.status ?? "?" },
  };
}

function seriesClean(series: NetworkSeriesResult | null, slowMs: number): boolean {
  return Boolean(
    series &&
      series.attempts > 0 &&
      series.succeeded === series.attempts &&
      (series.maxMs ?? 0) < slowMs,
  );
}

export function buildEntryRow(
  entry: NetworkEntryMeasurement,
  id: NetworkEntryRowId,
  phase: NetworkCheckPhase,
): NetworkRow {
  switch (id) {
    case "dns": {
      const dns = entry.dns;
      if (!dns) {
        return { status: pending(phase, false), ms: null, detail: notChecked };
      }
      if (dns.failed) {
        return { status: "failed", ms: dns.ms, detail: { key: "detail.dnsFailed" } };
      }
      return {
        status: dns.ms >= NETWORK_SLOW_MS ? "slow" : "ok",
        ms: dns.ms,
        detail: { key: "detail.dnsAddresses", params: { addresses: dns.addresses.join(", ") } },
      };
    }
    case "first": {
      const first = entry.first;
      if (!first) {
        return { status: pending(phase, false), ms: null, detail: notChecked };
      }
      if (!first.ok) return { status: "failed", ms: first.ms, detail: errorDetail(first) };
      return {
        status: first.ms >= NETWORK_FIRST_SLOW_MS ? "slow" : "ok",
        ms: first.ms,
        detail: { key: "detail.online" },
      };
    }
    case "sequential":
    case "burst": {
      const series = id === "sequential" ? entry.sequential : entry.burst;
      if (!series) {
        // 逐个请求一次都没成功的入口不做并发测试。
        const skippedBurst =
          id === "burst" && entry.sequential !== null && entry.sequential.succeeded === 0;
        return {
          status: skippedBurst ? "skipped" : pending(phase, false),
          ms: null,
          detail: skippedBurst ? { key: "detail.burstSkipped" } : notChecked,
        };
      }
      const slowMs = id === "sequential" ? NETWORK_SLOW_MS : NETWORK_BURST_SLOW_MS;
      const status: NetworkRowStatus =
        series.succeeded < series.attempts
          ? "failed"
          : (series.maxMs ?? 0) >= slowMs
            ? "slow"
            : "ok";
      return {
        status,
        ms: series.avgMs,
        detail: {
          key: id === "sequential" ? "detail.sequential" : "detail.burst",
          params: {
            succeeded: series.succeeded,
            attempts: series.attempts,
            avg: formatMs(series.avgMs),
            max: formatMs(series.maxMs),
            jitter: formatMs(series.jitterMs),
          },
        },
      };
    }
  }
}

export function buildLocalRow(
  measurements: NetworkCheckMeasurements,
  phase: NetworkCheckPhase,
): NetworkRow {
  const local = measurements.local;
  if (!local) return { status: pending(phase, false), ms: null, detail: notChecked };
  if (!local.known) return { status: "skipped", ms: null, detail: { key: "detail.localUnknown" } };
  const params = {
    type: local.type ?? "UNKNOWN",
    ip: local.ipAddress ?? "-",
  };
  return local.connected === false
    ? { status: "failed", ms: null, detail: { key: "detail.localOffline", params } }
    : { status: "ok", ms: null, detail: { key: "detail.local", params } };
}

export function buildReferenceRow(
  measurements: NetworkCheckMeasurements,
  phase: NetworkCheckPhase,
): NetworkRow {
  const reference = measurements.reference;
  const first = reference?.first ?? null;
  const series = reference?.sequential ?? null;
  if (!first && !series) return { status: pending(phase, false), ms: null, detail: notChecked };
  if (!series) {
    return first?.ok
      ? { status: first.ms >= NETWORK_FIRST_SLOW_MS ? "slow" : "ok", ms: first.ms, detail: { key: "detail.online" } }
      : { status: first ? "failed" : "pending", ms: first?.ms ?? null, detail: first ? errorDetail(first) : notChecked };
  }
  const status: NetworkRowStatus =
    series.succeeded < series.attempts
      ? "failed"
      : (series.maxMs ?? 0) >= NETWORK_SLOW_MS
        ? "slow"
        : "ok";
  return {
    status,
    ms: series.avgMs,
    detail: {
      key: "detail.sequential",
      params: {
        succeeded: series.succeeded,
        attempts: series.attempts,
        avg: formatMs(series.avgMs),
        max: formatMs(series.maxMs),
        jitter: formatMs(series.jitterMs),
      },
    },
  };
}

export function buildClockRow(
  measurements: NetworkCheckMeasurements,
  phase: NetworkCheckPhase,
): NetworkRow {
  const skew = measurements.clockSkewMs;
  if (skew === null) {
    // 时钟偏差在检测最后才算出：进行中显示「未检测」，结束仍没有才说明没拿到服务器时间。
    const finished = phase === "done" || phase === "cancelled";
    return {
      status: finished ? "skipped" : "pending",
      ms: null,
      detail: finished ? { key: "detail.clockUnknown" } : notChecked,
    };
  }
  const seconds = Math.round(Math.abs(skew) / 1000);
  return Math.abs(skew) > NETWORK_MAX_CLOCK_SKEW_MS
    ? { status: "failed", ms: null, detail: { key: "detail.clockOff", params: { offset: formatSkew(seconds) } } }
    : { status: "ok", ms: null, detail: { key: "detail.clockOk", params: { seconds } } };
}

function formatSkew(seconds: number): string {
  if (seconds >= 3600) return `${(seconds / 3600).toFixed(1)} h`;
  if (seconds >= 120) return `${Math.round(seconds / 60)} min`;
  return `${seconds} s`;
}

export function buildPublicIpRow(
  measurements: NetworkCheckMeasurements,
  phase: NetworkCheckPhase,
): NetworkRow {
  const publicIp = measurements.publicIp;
  if (measurements.activeEntry === null) {
    return { status: "skipped", ms: null, detail: { key: "detail.publicIpNotApplicable" } };
  }
  if (!publicIp) return { status: pending(phase, false), ms: null, detail: notChecked };
  if (publicIp.failed) return { status: "failed", ms: null, detail: { key: "detail.publicIpFailed" } };
  return publicIp.ip
    ? { status: "ok", ms: null, detail: { key: "detail.publicIp", params: { ip: publicIp.ip } } }
    : { status: "skipped", ms: null, detail: { key: "detail.publicIpUnknown" } };
}

function successes(entry: NetworkEntryMeasurement): number {
  return (
    (entry.first?.ok ? 1 : 0) +
    (entry.sequential?.succeeded ?? 0) +
    (entry.burst?.succeeded ?? 0)
  );
}

function entryDown(entry: NetworkEntryMeasurement): boolean {
  return successes(entry) === 0;
}

function entryErrors(entry: NetworkEntryMeasurement): NetworkProbeError[] {
  return [
    ...(entry.first && !entry.first.ok ? [entry.first.error ?? "network"] : []),
    ...(entry.sequential?.failures ?? []),
  ];
}

/** 逐个请求偶发失败（但不是全部失败），或第一次失败而后面又成功：路径在丢包。 */
function entryLossy(entry: NetworkEntryMeasurement): boolean {
  const series = entry.sequential;
  if (!series || series.succeeded === 0) return false;
  return series.succeeded < series.attempts || entry.first?.ok === false;
}

function entrySlow(entry: NetworkEntryMeasurement): boolean {
  return (entry.sequential?.avgMs ?? 0) >= NETWORK_SLOW_MS;
}

/** 首选入口排在前面：同类问题先报 App 主要在用的那条。 */
function byPriority(entries: readonly NetworkEntryMeasurement[]): NetworkEntryMeasurement[] {
  const rank = (kind: NetworkCheckEntryKind) => (kind === "direct" ? 1 : 0);
  return [...entries].sort((left, right) => rank(left.kind) - rank(right.kind));
}

export function evaluateNetworkCheck(
  measurements: NetworkCheckMeasurements,
): NetworkVerdict {
  const { local, reference } = measurements;
  const entries = byPriority(measurements.entries);
  if (local?.known && local.connected === false) {
    return { key: "noLocalNetwork", tone: "bad" };
  }

  const referenceSeries = reference?.sequential ?? null;
  const referenceDown =
    reference !== null &&
    reference.first?.ok !== true &&
    (referenceSeries?.succeeded ?? 0) === 0;

  if (entries.length > 0 && entries.every(entryDown)) {
    if (referenceDown) return { key: "internetUnreachable", tone: "bad" };
    const errors = entries.flatMap(entryErrors);
    if (errors.includes("notPosApi")) return { key: "notPosApi", tone: "bad" };
    if (errors.includes("http")) return { key: "serverError", tone: "bad" };
    return { key: "serverUnreachable", tone: "bad" };
  }

  const edge = entries.find((entry) => entry.kind === "edge");
  const direct = entries.find((entry) => entry.kind === "direct");
  if (edge && direct && entryDown(edge) && !entryDown(direct)) {
    return { key: "edgeDown", tone: "warn", entry: "edge" };
  }
  if (edge && direct && entryDown(direct) && !entryDown(edge)) {
    return { key: "directDown", tone: "warn", entry: "direct" };
  }

  if (
    measurements.clockSkewMs !== null &&
    Math.abs(measurements.clockSkewMs) > NETWORK_MAX_CLOCK_SKEW_MS
  ) {
    return { key: "clockSkew", tone: "bad" };
  }

  const lossy = entries.find(entryLossy);
  if (lossy) {
    // 参照地址也不稳：问题在本机 Wi‑Fi / 门店出口，而不是到服务器的那段路。
    return referenceSeries && !seriesClean(referenceSeries, NETWORK_SLOW_MS)
      ? { key: "linkUnstable", tone: "bad" }
      : { key: "entryLossy", tone: "warn", entry: lossy.kind };
  }

  const burstLimited = entries.find(
    (entry) =>
      seriesClean(entry.sequential, NETWORK_SLOW_MS) &&
      entry.burst !== null &&
      entry.burst.succeeded < entry.burst.attempts,
  );
  if (burstLimited) {
    return { key: "burstLimited", tone: "warn", entry: burstLimited.kind };
  }

  const slow = entries.find(entrySlow);
  if (slow) {
    return (referenceSeries?.avgMs ?? 0) >= NETWORK_SLOW_MS
      ? { key: "internetSlow", tone: "warn" }
      : { key: "entrySlow", tone: "warn", entry: slow.kind };
  }

  // 服务器都正常而公共参照地址不通：多半是门店网络屏蔽了外部地址，不影响收银。
  if (referenceDown) return { key: "referenceBlocked", tone: "good" };
  return { key: "allGood", tone: "good" };
}
