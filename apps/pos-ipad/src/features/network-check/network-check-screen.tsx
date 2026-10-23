import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  ScrollView,
  Share,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  networkCheckText,
  resolveNetworkCheckLocale,
  type NetworkCheckCopyKey,
  type NetworkCheckLocale,
} from "./network-check-copy";
import {
  activeEntryLabel,
  buildDeviceRows,
  buildNetworkCheckReport,
  describeRowDetail,
  entryName,
  entryRoleLabel,
  fallbackStateLabel,
  preferredEntryLabel,
  resolveVerdict,
  verdictAdvice,
  verdictTitle,
  type NetworkCheckReportContext,
} from "./network-check-report";
import {
  buildEntryRow,
  formatMs,
  NETWORK_ENTRY_ROW_IDS,
  type NetworkRow,
  type NetworkRowStatus,
} from "./network-check-rules";
import {
  emptyMeasurements,
  runNetworkCheck,
  type NetworkCheckPorts,
} from "./network-check-runner";
import type { NetworkCheckRun } from "./network-check-types";

import { PosPressable } from "@/ui/controls/pos-pressable";
import { posColors } from "@/ui/theme";

const IDLE_RUN: NetworkCheckRun = {
  phase: "idle",
  measurements: emptyMeasurements(),
  startedAtIso: null,
  finishedAtIso: null,
  totalMs: null,
};

const STATUS_COLORS: Record<NetworkRowStatus, Readonly<{ ink: string; soft: string }>> = {
  ok: { ink: posColors.green, soft: posColors.greenSoft },
  slow: { ink: posColors.yellow, soft: posColors.yellowSoft },
  failed: { ink: posColors.red, soft: posColors.redSoft },
  skipped: { ink: posColors.mutedInk, soft: "#EFEDE7" },
  pending: { ink: posColors.mutedInk, soft: "#EFEDE7" },
};

export type NetworkCheckScreenProps = Readonly<{
  /** null 表示还在读取服务器地址；读到后自动开始检测。 */
  apiBaseUrl: string | null;
  context: NetworkCheckReportContext;
  locale?: NetworkCheckLocale;
  onBack(): void;
  ports: NetworkCheckPorts;
  /** 测试可注入；默认用系统分享面板（可拷贝、AirDrop、发微信）。 */
  shareReport?: (report: string, title: string) => Promise<void>;
}>;

async function shareWithSystemSheet(report: string, title: string) {
  await Share.share({ message: report, title });
}

/**
 * 网络检测页：左侧结论、操作、首选 / 实际入口与本机 / 互联网检测，右侧三条服务器入口逐项并排对比。
 * 只读：只发 GET，不带令牌，不改任何设置，所以未登录、启动失败、待审批时都能打开。
 */
export function NetworkCheckScreen({
  apiBaseUrl,
  context,
  locale: localeOverride,
  onBack,
  ports,
  shareReport = shareWithSystemSheet,
}: NetworkCheckScreenProps) {
  const { i18n } = useTranslation();
  const locale =
    localeOverride ??
    resolveNetworkCheckLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = useCallback(
    (key: NetworkCheckCopyKey, values?: Readonly<Record<string, string | number>>) =>
      networkCheckText(locale, key, values),
    [locale],
  );
  const [run, setRun] = useState<NetworkCheckRun>(IDLE_RUN);
  const [shareFailed, setShareFailed] = useState(false);
  const controllerRef = useRef<AbortController | null>(null);
  const autoStartedRef = useRef(false);
  const running =
    run.phase === "basics" || run.phase === "stability" || run.phase === "burst";

  const start = useCallback(() => {
    if (!apiBaseUrl) return;
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;
    setShareFailed(false);
    void runNetworkCheck({
      apiBaseUrl,
      signal: controller.signal,
      ports,
      nowIso: () => new Date().toISOString(),
      // 新一轮开始后，上一轮（已取消）的迟到回调不得覆盖界面。
      onUpdate: (next) => {
        if (controllerRef.current === controller) setRun(next);
      },
    }).catch(() => {
      if (controllerRef.current === controller) {
        setRun((current) => ({ ...current, phase: "cancelled" }));
      }
    });
  }, [apiBaseUrl, ports]);

  const stop = useCallback(() => controllerRef.current?.abort(), []);

  // 打开页面且拿到服务器地址后自动开始一次；离开页面时中止进行中的请求。
  useEffect(() => {
    if (apiBaseUrl && !autoStartedRef.current) {
      autoStartedRef.current = true;
      start();
    }
  }, [apiBaseUrl, start]);
  useEffect(() => () => controllerRef.current?.abort(), []);

  const verdict = resolveVerdict(run);
  const report = useMemo(
    () => buildNetworkCheckReport(locale, run, context),
    [context, locale, run],
  );
  const tone = verdict?.tone ?? (run.phase === "cancelled" ? "warn" : "neutral");
  const toneColor =
    tone === "good"
      ? posColors.green
      : tone === "bad"
        ? posColors.red
        : tone === "warn"
          ? posColors.yellow
          : posColors.blue;
  const phaseLabel =
    run.phase === "done"
      ? t("phase.done", { seconds: ((run.totalMs ?? 0) / 1000).toFixed(1) })
      : t(`phase.${run.phase}` as NetworkCheckCopyKey);

  const onShare = async () => {
    try {
      await shareReport(report, t("report.header"));
      setShareFailed(false);
    } catch {
      setShareFailed(true);
    }
  };

  return (
    <SafeAreaView style={styles.safeArea} testID="network-check-screen">
      <View style={styles.header}>
        <HeaderButton
          icon="arrow-left"
          label={t("action.back")}
          onPress={onBack}
          sound="navigate"
          testID="network-check-back"
          tone="secondary"
        />
        <View style={styles.headerCopy}>
          <Text style={styles.title}>{t("title")}</Text>
          <Text numberOfLines={1} style={styles.subtitle}>
            {t("subtitle")}
          </Text>
        </View>
        <HeaderButton
          disabled={run.phase === "idle" || running}
          icon="share-variant-outline"
          label={t("action.share")}
          onPress={() => void onShare()}
          testID="network-check-share"
          tone="secondary"
        />
        {running ? (
          <HeaderButton
            icon="stop-circle-outline"
            label={t("action.stop")}
            onPress={stop}
            testID="network-check-stop"
            tone="danger"
          />
        ) : (
          <HeaderButton
            disabled={!apiBaseUrl}
            icon="refresh"
            label={t(run.phase === "idle" ? "action.start" : "action.restart")}
            onPress={start}
            testID="network-check-start"
            tone="primary"
          />
        )}
      </View>

      <View style={styles.workspace}>
        <ScrollView
          contentContainerStyle={styles.columnContent}
          style={styles.sideColumn}
        >
          <View
            accessibilityLiveRegion="polite"
            style={[styles.verdictCard, { borderLeftColor: toneColor }]}
            testID="network-check-verdict"
          >
            <View style={styles.phaseRow}>
              {running ? (
                <ActivityIndicator color={posColors.blue} size="small" />
              ) : (
                <MaterialCommunityIcons
                  color={toneColor}
                  name={
                    tone === "good"
                      ? "check-circle"
                      : tone === "bad"
                        ? "close-circle"
                        : tone === "warn"
                          ? "alert-circle"
                          : "access-point-network"
                  }
                  size={20}
                />
              )}
              <Text style={styles.phaseLabel} testID="network-check-phase">
                {phaseLabel}
              </Text>
            </View>
            <Text style={styles.verdictTitle} testID="network-check-verdict-title">
              {verdictTitle(locale, run, verdict)}
            </Text>
            <Text style={styles.verdictAdvice}>
              {verdictAdvice(locale, run, verdict)}
            </Text>
            {shareFailed ? (
              <Text style={styles.shareFailed}>{t("share.failed")}</Text>
            ) : null}
          </View>

          <View style={styles.panel}>
            <SummaryLine
              label={t("summary.server")}
              value={apiBaseUrl ?? t("summary.loadingServer")}
            />
            <SummaryLine
              label={t("summary.preferredEntry")}
              testID="network-check-summary-preferred"
              value={preferredEntryLabel(locale, run) ?? "-"}
            />
            <SummaryLine
              label={t("summary.activeEntry")}
              testID="network-check-summary-active"
              value={run.phase === "idle" ? "-" : activeEntryLabel(locale, run)}
            />
            <SummaryLine
              label={t("summary.fallbackState")}
              testID="network-check-summary-fallback"
              value={fallbackStateLabel(locale, run) ?? "-"}
            />
          </View>

          <View style={styles.panel}>
            <Text style={styles.sectionTitle}>{t("section.device")}</Text>
            {buildDeviceRows(run).map(({ key, row }) => (
              <CheckRow
                key={key}
                label={t(key)}
                locale={locale}
                row={row}
                testID={`network-check-${key.replace("row.", "")}`}
              />
            ))}
          </View>
        </ScrollView>

        <ScrollView
          contentContainerStyle={styles.columnContent}
          style={styles.mainColumn}
        >
          <View style={styles.panel}>
            <Text style={styles.sectionTitle}>{t("section.entries")}</Text>
            {run.measurements.entries.length === 0 ? (
              <Text style={styles.emptyText}>{t("verdict.idle.advice")}</Text>
            ) : (
              <View style={styles.entryGrid}>
                {run.measurements.entries.map((entry) => (
                  <View
                    key={entry.kind}
                    style={styles.entryColumn}
                    testID={`network-check-entry-${entry.kind}`}
                  >
                    <View style={styles.entryHeader}>
                      <Text style={styles.entryName}>
                        {entryName(locale, entry.kind)}
                      </Text>
                      <Text style={styles.entryRole}>
                        {entryRoleLabel(locale, run, entry.kind)}
                      </Text>
                    </View>
                    <Text numberOfLines={1} style={styles.entryHost}>
                      {entry.host}
                    </Text>
                    {NETWORK_ENTRY_ROW_IDS.map((id) => (
                      <CheckRow
                        key={id}
                        label={t(`row.${id}` as NetworkCheckCopyKey)}
                        locale={locale}
                        row={buildEntryRow(entry, id, run.phase)}
                        testID={`network-check-${entry.kind}-${id}`}
                      />
                    ))}
                  </View>
                ))}
              </View>
            )}
          </View>

          {run.phase === "done" || run.phase === "cancelled" ? (
            <View style={styles.panel}>
              <Text style={styles.sectionTitle}>{t("section.report")}</Text>
              <Text selectable style={styles.reportText} testID="network-check-report">
                {report}
              </Text>
            </View>
          ) : null}
        </ScrollView>
      </View>
    </SafeAreaView>
  );
}

function HeaderButton({
  disabled = false,
  icon,
  label,
  onPress,
  sound = "tap",
  testID,
  tone,
}: Readonly<{
  disabled?: boolean;
  icon: keyof typeof MaterialCommunityIcons.glyphMap;
  label: string;
  onPress(): void;
  sound?: "tap" | "navigate";
  testID: string;
  tone: "primary" | "secondary" | "danger";
}>) {
  const ink = tone === "secondary" ? posColors.blue : "#FFFFFF";
  return (
    <PosPressable
      accessibilityLabel={label}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound={sound}
      style={({ pressed }) => [
        styles.button,
        tone === "primary" && styles.primaryButton,
        tone === "secondary" && styles.secondaryButton,
        tone === "danger" && styles.dangerButton,
        disabled && styles.disabledButton,
        pressed && !disabled && styles.pressedButton,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons color={ink} name={icon} size={20} />
      <Text style={[styles.buttonLabel, { color: ink }]}>{label}</Text>
    </PosPressable>
  );
}

function SummaryLine({
  label,
  testID,
  value,
}: Readonly<{ label: string; testID?: string; value: string }>) {
  return (
    <View style={styles.summaryLine}>
      <Text style={styles.summaryLabel}>{label}</Text>
      <Text selectable style={styles.summaryValue} testID={testID}>
        {value}
      </Text>
    </View>
  );
}

function CheckRow({
  label,
  locale,
  row,
  testID,
}: Readonly<{
  label: string;
  locale: NetworkCheckLocale;
  row: NetworkRow;
  testID: string;
}>) {
  const colors = STATUS_COLORS[row.status];
  return (
    <View style={styles.checkRow} testID={testID}>
      <View style={styles.checkRowTop}>
        <Text style={styles.checkLabel}>{label}</Text>
        {row.ms !== null ? (
          <Text style={styles.checkMs}>{formatMs(row.ms)}</Text>
        ) : null}
        <View style={[styles.statusPill, { backgroundColor: colors.soft }]}>
          <Text style={[styles.statusText, { color: colors.ink }]} testID={`${testID}-status`}>
            {networkCheckText(locale, `status.${row.status}` as NetworkCheckCopyKey)}
          </Text>
        </View>
      </View>
      <Text selectable style={styles.checkDetail}>
        {describeRowDetail(locale, row.detail)}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  safeArea: { backgroundColor: posColors.canvas, flex: 1 },
  header: {
    alignItems: "center",
    backgroundColor: posColors.surface,
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 72,
    paddingHorizontal: 20,
  },
  headerCopy: { flex: 1, minWidth: 0 },
  title: { color: posColors.ink, fontSize: 23, fontWeight: "900" },
  subtitle: { color: posColors.mutedInk, fontSize: 13, marginTop: 3 },
  button: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    justifyContent: "center",
    minHeight: 48,
    minWidth: 112,
    paddingHorizontal: 16,
  },
  primaryButton: { backgroundColor: posColors.orange },
  secondaryButton: {
    backgroundColor: posColors.surface,
    borderColor: posColors.blue,
    borderWidth: 1,
  },
  dangerButton: { backgroundColor: posColors.red },
  disabledButton: { opacity: 0.45 },
  pressedButton: { opacity: 0.62 },
  buttonLabel: { fontSize: 15, fontWeight: "800" },
  workspace: { flex: 1, flexDirection: "row", gap: 16, padding: 16 },
  sideColumn: { flex: 0.85 },
  mainColumn: { flex: 1.4 },
  columnContent: { gap: 16, paddingBottom: 24 },
  verdictCard: {
    backgroundColor: posColors.surface,
    borderLeftWidth: 5,
    gap: 8,
    padding: 20,
  },
  phaseRow: { alignItems: "center", flexDirection: "row", gap: 8 },
  phaseLabel: { color: posColors.mutedInk, fontSize: 14, fontWeight: "700" },
  verdictTitle: { color: posColors.ink, fontSize: 22, fontWeight: "900", lineHeight: 28 },
  verdictAdvice: { color: posColors.ink, fontSize: 15, lineHeight: 22 },
  shareFailed: { color: posColors.red, fontSize: 14, fontWeight: "700" },
  panel: { backgroundColor: posColors.surface, padding: 18 },
  sectionTitle: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
    letterSpacing: 0.6,
    marginBottom: 6,
  },
  summaryLine: { gap: 2, paddingVertical: 6 },
  summaryLabel: { color: posColors.mutedInk, fontSize: 13 },
  summaryValue: { color: posColors.ink, fontSize: 15, fontWeight: "700" },
  entryGrid: { flexDirection: "row", gap: 16 },
  entryColumn: { flex: 1, minWidth: 0 },
  entryHeader: { alignItems: "baseline", flexDirection: "row", gap: 8 },
  entryName: { color: posColors.ink, fontSize: 19, fontWeight: "900" },
  entryRole: { color: posColors.blue, fontSize: 13, fontWeight: "800" },
  entryHost: { color: posColors.mutedInk, fontSize: 13, marginBottom: 4, marginTop: 2 },
  emptyText: { color: posColors.mutedInk, fontSize: 15 },
  checkRow: {
    borderTopColor: posColors.border,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: 4,
    paddingVertical: 10,
  },
  checkRowTop: { alignItems: "center", flexDirection: "row", gap: 8 },
  checkLabel: { color: posColors.ink, flex: 1, fontSize: 15, fontWeight: "800" },
  checkMs: {
    color: posColors.ink,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  statusPill: { minWidth: 56, paddingHorizontal: 8, paddingVertical: 3 },
  statusText: { fontSize: 13, fontWeight: "800", textAlign: "center" },
  checkDetail: { color: posColors.mutedInk, fontSize: 13, lineHeight: 18 },
  reportText: {
    color: posColors.ink,
    fontFamily: "Menlo",
    fontSize: 12,
    lineHeight: 18,
  },
});
