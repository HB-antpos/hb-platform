import type {
  NetworkLinkResult,
  NetworkLinkStats,
  NetworkRating,
  NetworkSample,
  NetworkTestLinkId,
  NetworkTestRunSummary,
} from "./types";

/**
 * 延迟类链路的评级阈值（毫秒 / 失败比例）。
 * - internet：Cloudflare 全球就近节点，澳洲境内几十毫秒，阈值最严；
 * - server：主服务器（hotbargain.vip，腾讯云新加坡）与登录态接口。**悉尼到它光 TCP 握手就要 260~300 毫秒**
 *   （2026-10-19 实测 curl time_connect 0.26~0.30s），一次请求至少一个往返，健康时平均 300~450 毫秒；
 *   按 300 毫秒判良好会让每个澳洲门店永远显示「偏慢」，所以 good 放到 600、fair 放到 1500；
 * - image：商品图片走上海 COS，从澳洲访问本来就要 1~1.5 秒，单独放宽。
 */
const LATENCY_THRESHOLDS: Record<"internet" | "server" | "image", { goodMs: number; fairMs: number; fairLoss: number }> = {
  internet: { goodMs: 300, fairMs: 1000, fairLoss: 0.1 },
  server: { goodMs: 600, fairMs: 1500, fairLoss: 0.1 },
  image: { goodMs: 1500, fairMs: 3000, fairLoss: 0.34 },
};

/** 服务器 → 数据库耗时阈值（毫秒）。 */
const DATABASE_GOOD_MS = 50;
const DATABASE_FAIR_MS = 300;

/** 下载速度阈值（KB/s）：200 KB/s 以上打开商品图、列表都流畅，50 以下明显卡顿。 */
const DOWNLOAD_GOOD_KBPS = 200;
const DOWNLOAD_FAIR_KBPS = 50;

export function computeLinkStats(samples: readonly NetworkSample[]): NetworkLinkStats {
  const successTimes = samples.filter((sample) => sample.ok).map((sample) => sample.ms);
  const total = samples.length;
  const success = successTimes.length;
  if (success === 0) {
    return { total, success, minMs: null, avgMs: null, maxMs: null, p95Ms: null, jitterMs: null, lossRate: total === 0 ? 0 : 1 };
  }

  const sorted = [...successTimes].sort((left, right) => left - right);
  // P95 用最近秩法：10 次里取第 10 小的值，样本少时就是最慢的一次。
  const p95Index = Math.min(sorted.length - 1, Math.ceil(sorted.length * 0.95) - 1);
  let jitterTotal = 0;
  for (let index = 1; index < successTimes.length; index += 1) {
    jitterTotal += Math.abs(successTimes[index] - successTimes[index - 1]);
  }

  return {
    total,
    success,
    minMs: sorted[0],
    avgMs: Math.round(successTimes.reduce((sum, value) => sum + value, 0) / success),
    maxMs: sorted[sorted.length - 1],
    p95Ms: sorted[p95Index],
    jitterMs: successTimes.length > 1 ? Math.round(jitterTotal / (successTimes.length - 1)) : 0,
    lossRate: (total - success) / total,
  };
}

/** 单条链路的评级；跳过 / 服务器未提供 / 未完成时返回 null。 */
export function rateLink(link: NetworkLinkResult): NetworkRating | null {
  if (link.state !== "done") return null;

  if (link.id === "database") {
    // 诊断请求本身失败时测不到数据库，不评级（不能把「连不上服务器」算成数据库差）。
    if (!link.samples[0]?.ok) return null;
    if (link.serverLatencyMs == null) return "bad";
    if (link.serverLatencyMs < DATABASE_GOOD_MS) return "good";
    return link.serverLatencyMs < DATABASE_FAIR_MS ? "fair" : "bad";
  }

  if (link.id === "download") {
    if (link.downloadKBps == null) return "bad";
    if (link.downloadKBps >= DOWNLOAD_GOOD_KBPS) return "good";
    return link.downloadKBps >= DOWNLOAD_FAIR_KBPS ? "fair" : "bad";
  }

  const stats = computeLinkStats(link.samples);
  if (stats.success === 0 || stats.avgMs == null) return "bad";
  const threshold = LATENCY_THRESHOLDS[link.id === "internet" ? "internet" : link.id === "image" ? "image" : "server"];
  if (stats.avgMs < threshold.goodMs && stats.lossRate === 0) return "good";
  if (stats.avgMs < threshold.fairMs && stats.lossRate <= threshold.fairLoss) return "fair";
  return "bad";
}

export type NetworkVerdictKey =
  | "cancelled"
  | "noInternet"
  | "serverUnreachable"
  | "serverUnstable"
  | "serverSlow"
  | "databaseIssue"
  | "authExpired"
  | "slowDownload"
  | "imageSlow"
  | "internetProbeBlocked"
  | "allGood";

export interface NetworkVerdict {
  key: NetworkVerdictKey;
  tone: "good" | "warn" | "bad" | "neutral";
}

function findLink(links: readonly NetworkLinkResult[], id: NetworkTestLinkId) {
  return links.find((link) => link.id === id);
}

function allFailed(link: NetworkLinkResult | undefined) {
  return Boolean(link && link.state === "done" && link.samples.length > 0 && link.samples.every((sample) => !sample.ok));
}

/**
 * 一句话结论：先判断「有没有网 / 连不连得上服务器」，再看稳定性和各分项，
 * 只给最主要的一个原因，店员和管理员一眼知道该找谁。
 */
export function computeVerdict(summary: Pick<NetworkTestRunSummary, "links" | "cancelled">): NetworkVerdict {
  if (summary.cancelled) return { key: "cancelled", tone: "neutral" };
  const { links } = summary;
  const internet = findLink(links, "internet");
  const server = findLink(links, "server");

  if (allFailed(server)) {
    return allFailed(internet) ? { key: "noInternet", tone: "bad" } : { key: "serverUnreachable", tone: "bad" };
  }

  const serverStats = server ? computeLinkStats(server.samples) : null;
  const serverRating = server ? rateLink(server) : null;
  if (serverRating === "bad" || serverRating === "fair") {
    // 有失败或抖动大 = 不稳定；全成功只是慢 = 偏慢。
    const unstable = Boolean(serverStats && (serverStats.lossRate > 0 || (serverStats.jitterMs ?? 0) >= 300));
    return {
      key: unstable ? "serverUnstable" : "serverSlow",
      tone: serverRating === "bad" ? "bad" : "warn",
    };
  }

  // 诊断请求本身失败（网络 / 限流）不算数据库问题；只有服务器回了话、数据库慢或不可用才算。
  const database = findLink(links, "database");
  if (database?.samples[0]?.ok && rateLink(database) === "bad") return { key: "databaseIssue", tone: "warn" };

  const auth = findLink(links, "auth");
  if (auth?.state === "done" && auth.samples.some((sample) => sample.error === "unauthorized")) {
    return { key: "authExpired", tone: "warn" };
  }

  const download = findLink(links, "download");
  if (download && rateLink(download) === "bad") return { key: "slowDownload", tone: "warn" };

  const image = findLink(links, "image");
  if (image && rateLink(image) === "bad") return { key: "imageSlow", tone: "warn" };

  // 服务器正常但公共测速地址不通：多半是门店网络屏蔽了外部地址，不影响使用。
  if (allFailed(internet)) return { key: "internetProbeBlocked", tone: "good" };

  return { key: "allGood", tone: "good" };
}

/** 毫秒显示：1 秒以上用秒（保留 1 位小数）。 */
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return "--";
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.round(ms)} ms`;
}

/** 下载速度显示：1024 KB/s 以上用 MB/s。 */
export function formatSpeed(kbps: number | null | undefined): string {
  if (kbps == null) return "--";
  if (kbps >= 1024) return `${(kbps / 1024).toFixed(1)} MB/s`;
  return `${Math.round(kbps)} KB/s`;
}
