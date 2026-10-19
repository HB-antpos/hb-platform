import { computeLinkStats, formatDuration, formatSpeed, rateLink, type NetworkVerdict } from "./stats";
import type { NetworkLinkResult, NetworkRating, NetworkSample, NetworkTestRunSummary } from "./types";

/** 报告与界面共用的翻译函数形状（i18next 的 t 兼容这个签名）。 */
export type NetworkTestTranslate = (key: string, params?: Record<string, unknown>) => string;

export const RATING_ICONS: Record<NetworkRating | "skip", string> = {
  good: "🟢",
  fair: "🟡",
  bad: "🔴",
  skip: "⚪",
};

const REPORT_DIVIDER = "────────────────────";

/** 报告与页面上的时间统一用悉尼时间（门店与服务器日志都按悉尼时间看）。 */
export function formatSydneyDateTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  try {
    const parts: Record<string, string> = {};
    const formatter = new Intl.DateTimeFormat("en-CA", {
      timeZone: "Australia/Sydney",
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hour12: false,
    });
    for (const part of formatter.formatToParts(date)) parts[part.type] = part.value;
    const hour = parts.hour === "24" ? "00" : parts.hour;
    return `${parts.year}-${parts.month}-${parts.day} ${hour}:${parts.minute}:${parts.second}`;
  } catch {
    return iso.replace("T", " ").slice(0, 19);
  }
}

/** 失败样本的原因文字，例如「超时」「HTTP 502」。 */
export function describeSampleError(sample: NetworkSample, t: NetworkTestTranslate): string {
  switch (sample.error) {
    case "timeout":
      return t("sampleError.timeout");
    case "unauthorized":
      return t("sampleError.unauthorized");
    case "http":
      return t("sampleError.http", { status: sample.status ?? "?" });
    default:
      return t("sampleError.network");
  }
}

export interface NetworkReportInput {
  summary: NetworkTestRunSummary;
  verdict: NetworkVerdict;
  serverHost: string;
  serverSourceLabel: string;
  deviceLabel: string;
  appLabel: string;
  accountLabel: string;
}

function linkName(link: NetworkLinkResult, t: NetworkTestTranslate) {
  return t(`links.${link.id}.name`);
}

function buildLinkLines(link: NetworkLinkResult, index: number, t: NetworkTestTranslate): string[] {
  const number = index + 1;
  const name = linkName(link, t);

  if (link.state === "skipped") return [t("report.statusLine", { index: number, name, value: t("state.skippedNotLoggedIn") })];
  if (link.state === "unavailable") return [t("report.statusLine", { index: number, name, value: t("state.unavailable") })];
  if (link.state !== "done") return [t("report.statusLine", { index: number, name, value: t("state.notRun") })];

  const rating = rateLink(link);
  const icon = rating ? RATING_ICONS[rating] : RATING_ICONS.skip;

  if (link.id === "database") {
    const sample = link.samples[0];
    const value = sample?.ok
      ? link.serverLatencyMs == null ? t("state.databaseUnavailable") : formatDuration(link.serverLatencyMs)
      : sample ? describeSampleError(sample, t) : t("state.notRun");
    return [t("report.valueLine", { index: number, name, value, icon })];
  }

  if (link.id === "download") {
    const sample = link.samples[0];
    const value = sample?.ok ? formatSpeed(link.downloadKBps) : sample ? describeSampleError(sample, t) : t("state.notRun");
    return [t("report.valueLine", { index: number, name, value, icon })];
  }

  const stats = computeLinkStats(link.samples);
  const lines = [
    t("report.latencyLine", {
      index: number,
      name,
      success: stats.success,
      total: stats.total,
      avg: formatDuration(stats.avgMs),
      icon,
    }),
  ];
  // 多次请求的链路补一行稳定性数据；有失败的列出第几次、什么原因，管理员可以对照服务器日志。
  if (stats.total > 1 && stats.success > 0) {
    lines.push(
      t("report.statsLine", {
        min: formatDuration(stats.minMs),
        p95: formatDuration(stats.p95Ms),
        jitter: formatDuration(stats.jitterMs),
      })
    );
  }
  const failures = link.samples
    .map((sample, sampleIndex) => ({ sample, sampleIndex }))
    .filter(({ sample }) => !sample.ok)
    .map(({ sample, sampleIndex }) =>
      t("report.failureItem", { index: sampleIndex + 1, reason: describeSampleError(sample, t) })
    );
  if (failures.length > 0) lines.push(t("report.failuresLine", { items: failures.join(t("report.listSeparator")) }));
  return lines;
}

/** 生成可直接粘贴到微信的纯文本报告；不含令牌、账号权限等敏感信息。 */
/** 公网 IP 显示：旧版后端没有诊断接口时说明「服务器未提供」，而不是「未识别」。 */
export function describeClientIp(summary: Pick<NetworkTestRunSummary, "clientIp" | "links">, t: NetworkTestTranslate): string {
  if (summary.clientIp) return summary.clientIp;
  const diagnostics = summary.links.find((link) => link.id === "database");
  if (diagnostics?.state === "unavailable") return t("context.publicIpUnavailable");
  // 诊断请求本身没成功（连不上 / 超时 / 限流）：没拿到，不是服务器识别不出。
  if (diagnostics?.state === "done" && !diagnostics.samples[0]?.ok) return t("context.publicIpNotFetched");
  return t("context.publicIpUnknown");
}

export function buildNetworkReport(input: NetworkReportInput, t: NetworkTestTranslate): string {
  const { summary } = input;
  const lines = [
    t("report.header", { time: formatSydneyDateTime(summary.finishedAtIso ?? summary.startedAtIso) }),
    t("report.device", { value: input.deviceLabel }),
    t("report.app", { value: input.appLabel }),
    t("report.account", { value: input.accountLabel }),
    t("report.server", { host: input.serverHost, source: input.serverSourceLabel }),
    t("report.publicIp", { value: describeClientIp(summary, t) }),
    t("report.verdict", { value: t(`verdict.${input.verdict.key}.title`) }),
    REPORT_DIVIDER,
    ...summary.links.flatMap((link, index) => buildLinkLines(link, index, t)),
  ];
  if (summary.requestId) lines.push(t("report.requestId", { value: summary.requestId }));
  if (summary.cancelled) lines.push(t("report.stoppedNote"));
  return lines.join("\n");
}
