/** 网络测试的 6 条链路，按测试顺序排列。 */
export const NETWORK_TEST_LINK_IDS = ["internet", "server", "auth", "database", "image", "download"] as const;
export type NetworkTestLinkId = (typeof NETWORK_TEST_LINK_IDS)[number];

/** 单次请求失败的原因：超时、连不上（没拿到响应）、拿到了非预期的 HTTP 状态。 */
export type NetworkSampleError = "timeout" | "network" | "http" | "unauthorized";

export interface NetworkSample {
  ok: boolean;
  /** 从发出到拿到响应（或失败）的耗时，毫秒。 */
  ms: number;
  status?: number;
  error?: NetworkSampleError;
}

export type NetworkLinkState =
  | "pending"
  | "running"
  | "done"
  /** 主动跳过（未登录时的登录态接口）。 */
  | "skipped"
  /** 服务器还没上线对应接口（旧版后端返回 404），不算失败。 */
  | "unavailable";

export type NetworkRating = "good" | "fair" | "bad";

export interface NetworkLinkStats {
  total: number;
  success: number;
  minMs: number | null;
  avgMs: number | null;
  maxMs: number | null;
  p95Ms: number | null;
  /** 相邻两次成功请求耗时差的平均值，衡量稳定性。 */
  jitterMs: number | null;
  /** 失败比例 0~1。 */
  lossRate: number;
}

export interface NetworkLinkResult {
  id: NetworkTestLinkId;
  state: NetworkLinkState;
  /** 本项计划请求次数（进度条与「第 x / y 次」用）。 */
  plannedCount: number;
  samples: NetworkSample[];
  /** 数据库链路：服务器端返回的数据库往返耗时。 */
  serverLatencyMs?: number | null;
  /** 下载链路：实际下载字节数与速度。 */
  downloadBytes?: number;
  downloadKBps?: number | null;
}

export interface NetworkTestContext {
  /** 本次测试实际使用的 API 基础地址（与业务请求同一套解析）。 */
  apiBaseUrl: string;
  /** 本次测试的服务器主机名，显示用。 */
  serverHost: string;
  /** 服务器地址来源：服务器设置 / 设备绑定。 */
  serverSource: "settings" | "deviceBinding";
  /** 登录令牌；未登录为 null，登录态接口跳过。 */
  accessToken: string | null;
}

export interface NetworkTestRunSummary {
  links: NetworkLinkResult[];
  /** 服务器看到的公网 IP（诊断接口返回；旧版后端或未识别时为 null）。 */
  clientIp: string | null;
  /** 诊断接口的请求编号，便于在服务器日志里定位。 */
  requestId: string | null;
  startedAtIso: string;
  finishedAtIso: string | null;
  cancelled: boolean;
}
