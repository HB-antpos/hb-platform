import {
  isNetworkCheckCopyKey,
  networkCheckText,
  type NetworkCheckCopyKey,
  type NetworkCheckLocale,
} from "./network-check-copy";
import {
  buildClockRow,
  buildEntryRow,
  buildLocalRow,
  buildPublicIpRow,
  buildReferenceRow,
  evaluateNetworkCheck,
  formatMs,
  NETWORK_ENTRY_ROW_IDS,
  type NetworkRow,
  type NetworkRowDetail,
  type NetworkVerdict,
} from "./network-check-rules";
import type {
  NetworkCheckEntryKind,
  NetworkCheckRun,
} from "./network-check-types";

export type NetworkCheckReportContext = Readonly<{
  deviceLabel: string;
  appLabel: string;
  serverLabel: string;
}>;

const ENTRY_NAME_KEYS: Record<NetworkCheckEntryKind, NetworkCheckCopyKey> = {
  edge: "entry.edge",
  direct: "entry.direct",
  configured: "entry.configured",
};

export function entryName(
  locale: NetworkCheckLocale,
  kind: NetworkCheckEntryKind,
): string {
  return networkCheckText(locale, ENTRY_NAME_KEYS[kind]);
}

export function describeRowDetail(
  locale: NetworkCheckLocale,
  detail: NetworkRowDetail,
): string {
  return isNetworkCheckCopyKey(detail.key)
    ? networkCheckText(locale, detail.key, detail.params)
    : detail.key;
}

/** 检测进行中 / 未开始 / 已停止时不下结论，只有完成后才评估。 */
export function resolveVerdict(run: NetworkCheckRun): NetworkVerdict | null {
  return run.phase === "done" ? evaluateNetworkCheck(run.measurements) : null;
}

export function verdictTitle(
  locale: NetworkCheckLocale,
  run: NetworkCheckRun,
  verdict: NetworkVerdict | null,
): string {
  if (verdict) {
    return networkCheckText(
      locale,
      `verdict.${verdict.key}.title` as NetworkCheckCopyKey,
      { entry: verdict.entry ? entryName(locale, verdict.entry) : "" },
    );
  }
  return networkCheckText(locale, `verdict.${pendingVerdictKey(run)}.title` as NetworkCheckCopyKey);
}

export function verdictAdvice(
  locale: NetworkCheckLocale,
  run: NetworkCheckRun,
  verdict: NetworkVerdict | null,
): string {
  if (verdict) {
    return networkCheckText(
      locale,
      `verdict.${verdict.key}.advice` as NetworkCheckCopyKey,
    );
  }
  return networkCheckText(locale, `verdict.${pendingVerdictKey(run)}.advice` as NetworkCheckCopyKey);
}

function pendingVerdictKey(run: NetworkCheckRun): "idle" | "running" | "cancelled" {
  if (run.phase === "idle") return "idle";
  return run.phase === "cancelled" ? "cancelled" : "running";
}

/** 本机与互联网分区的行（顺序即显示顺序）。 */
export function buildDeviceRows(
  run: NetworkCheckRun,
): readonly Readonly<{ key: NetworkCheckCopyKey; row: NetworkRow }>[] {
  const { measurements, phase } = run;
  return [
    { key: "row.local", row: buildLocalRow(measurements, phase) },
    { key: "row.reference", row: buildReferenceRow(measurements, phase) },
    { key: "row.publicIp", row: buildPublicIpRow(measurements, phase) },
    { key: "row.clock", row: buildClockRow(measurements, phase) },
  ];
}

export function activeEntryLabel(
  locale: NetworkCheckLocale,
  run: NetworkCheckRun,
): string {
  const active = run.measurements.activeEntry;
  return networkCheckText(
    locale,
    active === "direct"
      ? "activeEntry.direct"
      : active === "edge"
        ? "activeEntry.edge"
        : "activeEntry.configured",
  );
}

/** 悉尼时间（门店与服务器日志都按悉尼时间看），格式 yyyy-MM-dd HH:mm:ss。 */
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

/**
 * 纯文本报告：给「分享报告」用，可直接发给管理员。
 * 只含网络信息与耗时，不含收银员、条码、令牌或设备凭据。
 */
export function buildNetworkCheckReport(
  locale: NetworkCheckLocale,
  run: NetworkCheckRun,
  context: NetworkCheckReportContext,
): string {
  const t = (key: NetworkCheckCopyKey, values?: Readonly<Record<string, string | number>>) =>
    networkCheckText(locale, key, values);
  const verdict = resolveVerdict(run);
  const row = (name: string, value: NetworkRow) =>
    t("report.row", {
      name,
      status: t(`status.${value.status}` as NetworkCheckCopyKey),
      ms: value.ms === null ? "" : ` ${formatMs(value.ms)}`,
      detail: describeRowDetail(locale, value.detail),
    });

  const lines = [
    t("report.header"),
    t("report.time", { value: formatSydneyDateTime(run.finishedAtIso ?? run.startedAtIso ?? "") }),
    t("report.duration", { value: formatMs(run.totalMs) }),
    t("report.device", { value: context.deviceLabel }),
    t("report.app", { value: context.appLabel }),
    t("report.server", { value: context.serverLabel }),
    t("report.activeEntry", { value: activeEntryLabel(locale, run) }),
    t("report.verdict", { value: verdictTitle(locale, run, verdict) }),
    "",
    t("report.section", { name: t("section.device") }),
    ...buildDeviceRows(run).map(({ key, row: value }) => row(t(key), value)),
  ];
  for (const entry of run.measurements.entries) {
    lines.push(
      "",
      t("report.section", { name: `${entryName(locale, entry.kind)} · ${entry.host}` }),
      ...NETWORK_ENTRY_ROW_IDS.map((id) =>
        row(t(`row.${id}` as NetworkCheckCopyKey), buildEntryRow(entry, id, run.phase)),
      ),
    );
    if (entry.first?.eoLogUuid) {
      lines.push(t("report.eoLogUuid", { value: entry.first.eoLogUuid }));
    }
  }
  if (run.measurements.publicIp?.requestId) {
    lines.push("", t("report.requestId", { value: run.measurements.publicIp.requestId }));
  }
  if (run.phase === "cancelled") lines.push("", t("report.stopped"));
  return lines.join("\n");
}
