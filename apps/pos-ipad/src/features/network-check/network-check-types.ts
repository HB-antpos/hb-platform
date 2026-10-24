/**
 * iPad 网络检测的数据模型。参照 WPF `NetworkDiagnosticsModels`，但只保留纯 JS 能实测的部分：
 * 没有网关 ping、系统 DNS、原始 TCP 建连与 TLS / 证书检查（iOS 沙盒里的 JS 拿不到）。
 */

import type { ApiEntryMode } from "@hb/pos-api-client/transport-failover";

/**
 * 入口类型：EdgeOne、AWS 悉尼、直连三条入口之一；配置地址不在入口组时只有一个「当前地址」。
 * 哪条是首选由后台按分店设置（见 NetworkCheckEntryState），不再固定。
 */
export type NetworkCheckEntryKind = ApiEntryMode | "configured";

/** App 业务请求当前的入口状态（读共享的入口选择器）。 */
export interface NetworkCheckEntryState {
  /** 后台设置的首选入口（后台未设置时为默认 edge）。 */
  preferred: ApiEntryMode;
  /** 业务请求此刻实际会走的入口。 */
  active: ApiEntryMode;
  /** 首选入口是否因故障正处于冷却，业务请求已落到备用入口。 */
  usingFallback: boolean;
}

/**
 * 单次请求失败原因：
 * - timeout：在超时内没拿到响应；
 * - network：没拿到任何响应（域名解析、连接被拒 / 重置、TLS 失败在 JS 里都表现为这一类）；
 * - edge：EdgeOne 专用的 520~530（连不上源站、回源超时等，源站从不返回这些状态）；
 * - http：服务器回了其他非 2xx，或返回包里明确不在线；
 * - notPosApi：拿到了 2xx 但不是 POS 服务器的返回包（例如被门户页 / 拦截页替换）。
 */
export type NetworkProbeError =
  | "timeout"
  | "network"
  | "edge"
  | "http"
  | "notPosApi";

export interface NetworkProbeSample {
  ok: boolean;
  /** 从发出到读完响应（或失败）的耗时，毫秒。 */
  ms: number;
  status?: number;
  error?: NetworkProbeError;
  /** 健康检查返回的服务器时间（ISO）。 */
  serverTime?: string;
  /** EdgeOne 返回的 eo-log-uuid，可在 EdgeOne 控制台按它查这一次请求。 */
  eoLogUuid?: string;
}

/** 一组请求（逐个或同时）的统计。 */
export interface NetworkSeriesResult {
  attempts: number;
  succeeded: number;
  avgMs: number | null;
  maxMs: number | null;
  /** 相邻两次成功请求耗时差的平均值，衡量稳定性；只有逐个请求才有。 */
  jitterMs: number | null;
  /** 失败的原因（按发生顺序），报告里列出来便于对照服务器日志。 */
  failures: NetworkProbeError[];
}

export interface NetworkDnsResult {
  ms: number;
  addresses: string[];
  failed: boolean;
}

export interface NetworkEntryMeasurement {
  kind: NetworkCheckEntryKind;
  host: string;
  /** 该入口的 API 基础地址（不含末尾斜杠），健康检查为 `${baseUrl}/api/v1/health`。 */
  baseUrl: string;
  /** 公共 DNS（DoH）解析结果；IP 字面量 / localhost 不查。 */
  dns: NetworkDnsResult | null;
  /** 第一次请求：通常包含 DNS、建连与 TLS 握手。 */
  first: NetworkProbeSample | null;
  sequential: NetworkSeriesResult | null;
  burst: NetworkSeriesResult | null;
}

export interface NetworkLocalInfo {
  /** 读不到系统网络状态时为 false，结论不据此判断「没网」。 */
  known: boolean;
  connected: boolean | null;
  /** WIFI / ETHERNET / CELLULAR / NONE / UNKNOWN 等（expo-network 的类型名）。 */
  type: string | null;
  ipAddress: string | null;
}

export interface NetworkReferenceMeasurement {
  /** 参照地址（与我们服务器无关的公共服务），用来区分「整条外网差」和「只有到服务器的路差」。 */
  url: string;
  first: NetworkProbeSample | null;
  sequential: NetworkSeriesResult | null;
}

export interface NetworkPublicIpResult {
  ip: string | null;
  requestId: string | null;
  failed: boolean;
}

export interface NetworkCheckMeasurements {
  local: NetworkLocalInfo | null;
  reference: NetworkReferenceMeasurement | null;
  entries: NetworkEntryMeasurement[];
  publicIp: NetworkPublicIpResult | null;
  /** 本机时间减服务器时间（毫秒，已按请求耗时一半校正）；拿不到服务器时间为 null。 */
  clockSkewMs: number | null;
  /** App 业务请求当前实际走的入口（读共享的入口选择器）；配置地址不在入口组时为 null。 */
  activeEntry: ApiEntryMode | null;
  /** 后台设置的首选入口；配置地址不在入口组时为 null。 */
  preferredEntry: ApiEntryMode | null;
  /** 是否正在回退（首选入口故障，业务请求落到备用入口）。 */
  usingFallback: boolean;
}

export type NetworkCheckPhase =
  | "idle"
  | "basics"
  | "stability"
  | "burst"
  | "done"
  | "cancelled";

export interface NetworkCheckRun {
  phase: NetworkCheckPhase;
  measurements: NetworkCheckMeasurements;
  startedAtIso: string | null;
  finishedAtIso: string | null;
  totalMs: number | null;
}
