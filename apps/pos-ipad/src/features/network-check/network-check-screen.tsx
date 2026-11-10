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

import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import { PosPill } from "@/ui/controls/pos-pill";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
  type PosPalette,
} from "@/ui/theme";

const IDLE_RUN: NetworkCheckRun = {
  phase: "idle",
  measurements: emptyMeasurements(),
  startedAtIso: null,
  finishedAtIso: null,
  totalMs: null,
};

// 只存色名：色值必须在渲染时按当前主题读取，不能在模块加载时定死。
const STATUS_COLORS: Record<
  NetworkRowStatus,
  Readonly<{ ink: keyof PosPalette; soft: keyof PosPalette }>
> = {
  ok: { ink: "green", soft: "greenSoft" },
  // 黄色浅底上的文字用 yellowInk，四套主题下都看得清。
  slow: { ink: "yellowInk", soft: "yellowSoft" },
  failed: { ink: "red", soft: "redSoft" },
  skipped: { ink: "mutedInk", soft: "surfaceSunken" },
  pending: { ink: "mutedInk", soft: "surfaceSunken" },
};

type VerdictTone = "good" | "warn" | "bad" | "neutral";

// 结论卡：左边色条、结论文字与图标随结果变色。同样只存色名。
const VERDICT_TONES: Record<
  VerdictTone,
  Readonly<{ bar: keyof PosPalette; ink: keyof PosPalette; icon: PosIconName }>
> = {
  good: { bar: "green", ink: "green", icon: "check-circle" },
  warn: { bar: "yellow", ink: "yellowInk", icon: "alert-circle" },
  bad: { bar: "red", ink: "red", icon: "close-circle" },
  neutral: { bar: "blue", ink: "ink", icon: "access-point-network" },
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
 * 网络检测页：页头放「返回 / 分享报告 / 开始或停止检测」；左栏依次是结论、概要、本机与互联网，
 * 右栏每个服务器入口一列并排对比，下方是可选中拷贝的完整报告。
 * 只读：只发 GET，不带令牌，不改任何设置，所以未登录、启动失败、待审批时都能打开——
 * 因此这一页不显示左侧功能栏（rail="none"），只有顶栏和「返回」。
 */
export function NetworkCheckScreen({
  apiBaseUrl,
  context,
  locale: localeOverride,
  onBack,
  ports,
  shareReport = shareWithSystemSheet,
}: NetworkCheckScreenProps) {
  usePosThemeVersion();
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
  const tone: VerdictTone =
    verdict?.tone ?? (run.phase === "cancelled" ? "warn" : "neutral");
  const toneTokens = VERDICT_TONES[tone];
  const preferredEntry = run.measurements.preferredEntry;
  // 「是否回退中」：用首选入口时显示绿色，回退中显示提醒色；不适用时保持普通文字。
  const fallbackInk: keyof PosPalette =
    preferredEntry === null
      ? "ink"
      : run.measurements.usingFallback
        ? "yellowInk"
        : "green";
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
    <PosScreenFrame
      header={
        <PosPageHeader
          actions={
            <>
              <PosButton
                disabled={run.phase === "idle" || running}
                icon="export-variant"
                label={t("action.share")}
                onPress={() => void onShare()}
                size="medium"
                testID="network-check-share"
                tone="quiet"
              />
              {running ? (
                // 检测中把「重新检测」换成红色「停止」，同一位置只出现一个。
                <PosButton
                  icon="stop-circle-outline"
                  label={t("action.stop")}
                  onPress={stop}
                  size="medium"
                  testID="network-check-stop"
                  tone="danger"
                />
              ) : (
                <PosButton
                  disabled={!apiBaseUrl}
                  icon="refresh"
                  label={t(run.phase === "idle" ? "action.start" : "action.restart")}
                  onPress={start}
                  size="medium"
                  testID="network-check-start"
                />
              )}
            </>
          }
          backLabel={t("action.back")}
          backTestID="network-check-back"
          onBack={onBack}
          subtitle={t("subtitle")}
          title={t("title")}
        />
      }
      rail="none"
      testID="network-check-screen"
    >
      <View style={styles.workspace}>
        <ScrollView
          contentContainerStyle={styles.sideContent}
          style={styles.sideColumn}
        >
          <View
            accessibilityLiveRegion="polite"
            style={styles.verdictCard}
            testID="network-check-verdict"
          >
            <View
              style={[
                styles.verdictBar,
                { backgroundColor: posColors[toneTokens.bar] },
              ]}
            />
            <View style={styles.verdictBody}>
              <View style={styles.phaseRow}>
                {running ? (
                  <ActivityIndicator color={posColors.blue} size="small" />
                ) : (
                  <MaterialCommunityIcons
                    accessible={false}
                    color={posColors[toneTokens.bar]}
                    name={toneTokens.icon}
                    size={20}
                  />
                )}
                <Text style={styles.phaseLabel} testID="network-check-phase">
                  {phaseLabel}
                </Text>
              </View>
              <Text
                adjustsFontSizeToFit
                minimumFontScale={0.6}
                numberOfLines={3}
                style={[styles.verdictTitle, { color: posColors[toneTokens.ink] }]}
                testID="network-check-verdict-title"
              >
                {verdictTitle(locale, run, verdict)}
              </Text>
              <Text style={styles.verdictAdvice}>
                {verdictAdvice(locale, run, verdict)}
              </Text>
              {shareFailed ? (
                <Text style={styles.shareFailed}>{t("share.failed")}</Text>
              ) : null}
            </View>
          </View>

          <View style={styles.card}>
            <Text style={styles.sectionLabel}>{t("section.summary")}</Text>
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
              ink={fallbackInk}
              label={t("summary.fallbackState")}
              testID="network-check-summary-fallback"
              value={fallbackStateLabel(locale, run) ?? "-"}
            />
          </View>

          <View style={[styles.card, styles.cardFill]}>
            <Text style={styles.sectionLabel}>{t("section.device")}</Text>
            {buildDeviceRows(run).map(({ key, row }, index) => (
              <CheckRow
                first={index === 0}
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
          contentContainerStyle={styles.mainContent}
          style={styles.mainColumn}
        >
          <Text accessibilityRole="header" style={styles.sectionTitle}>
            {t("section.entries")}
          </Text>
          {run.measurements.entries.length === 0 ? (
            <View style={styles.card}>
              <Text style={styles.emptyText}>{t("verdict.idle.advice")}</Text>
            </View>
          ) : (
            <View style={styles.entryGrid}>
              {run.measurements.entries.map((entry) => {
                // 首选入口用主操作色描边，一眼看出后台设的是哪条。
                const preferred = entry.kind === preferredEntry;
                return (
                  <View
                    key={entry.kind}
                    style={[styles.entryCard, preferred && styles.entryCardPreferred]}
                    testID={`network-check-entry-${entry.kind}`}
                  >
                    <View style={styles.entryHeader}>
                      <Text numberOfLines={1} style={styles.entryName}>
                        {entryName(locale, entry.kind)}
                      </Text>
                      <PosPill
                        label={entryRoleLabel(locale, run, entry.kind)}
                        style={styles.entryRole}
                        tone={preferred ? "accent" : "neutral"}
                      />
                    </View>
                    <Text numberOfLines={1} selectable style={styles.entryHost}>
                      {entry.host}
                    </Text>
                    {NETWORK_ENTRY_ROW_IDS.map((id) => (
                      <CheckRow
                        key={id}
                        label={t(`row.${id}` as NetworkCheckCopyKey)}
                        locale={locale}
                        row={buildEntryRow(entry, id, run.phase)}
                        showEmptyMs
                        testID={`network-check-${entry.kind}-${id}`}
                      />
                    ))}
                  </View>
                );
              })}
            </View>
          )}

          {run.phase === "done" || run.phase === "cancelled" ? (
            <View style={styles.card}>
              <Text style={styles.sectionLabel}>{t("section.report")}</Text>
              <Text selectable style={styles.reportText} testID="network-check-report">
                {report}
              </Text>
            </View>
          ) : null}
        </ScrollView>
      </View>
    </PosScreenFrame>
  );
}

/** 概要卡的一行：左边说明、右边取值（可选中拷贝）。 */
function SummaryLine({
  ink = "ink",
  label,
  testID,
  value,
}: Readonly<{
  ink?: keyof PosPalette;
  label: string;
  testID?: string;
  value: string;
}>) {
  return (
    <View style={styles.summaryLine}>
      <Text style={styles.summaryLabel}>{label}</Text>
      <Text
        selectable
        style={[styles.summaryValue, { color: posColors[ink] }]}
        testID={testID}
      >
        {value}
      </Text>
    </View>
  );
}

/** 一条检测项：名称、耗时、状态标签，下面一行是具体说明（地址、失败原因等）。 */
function CheckRow({
  first = false,
  label,
  locale,
  row,
  showEmptyMs = false,
  testID,
}: Readonly<{
  /** 卡片里的第一行不画上分隔线。 */
  first?: boolean;
  label: string;
  locale: NetworkCheckLocale;
  row: NetworkRow;
  /** 没有耗时时显示「—」占位，让各入口列的耗时对齐。 */
  showEmptyMs?: boolean;
  testID: string;
}>) {
  const statusTokens = STATUS_COLORS[row.status];
  const colors = {
    ink: posColors[statusTokens.ink],
    soft: posColors[statusTokens.soft],
  };
  return (
    <View style={[styles.checkRow, first && styles.checkRowFirst]} testID={testID}>
      <View style={styles.checkRowTop}>
        <Text numberOfLines={1} style={styles.checkLabel}>
          {label}
        </Text>
        {row.ms !== null ? (
          <Text style={styles.checkMs}>{formatMs(row.ms)}</Text>
        ) : showEmptyMs ? (
          <Text accessible={false} style={styles.checkMs}>
            —
          </Text>
        ) : null}
        <View style={[styles.statusPill, { backgroundColor: colors.soft }]}>
          <Text
            numberOfLines={1}
            style={[styles.statusText, { color: colors.ink }]}
            testID={`${testID}-status`}
          >
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

const styles = createPosStyles((colors) => ({
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  sideColumn: { flexGrow: 0, flexShrink: 0, width: 400 },
  sideContent: { flexGrow: 1, gap: 12 },
  mainColumn: { flex: 1, minWidth: 0 },
  mainContent: { gap: 10, paddingBottom: 12 },
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    padding: 16,
  },
  cardFill: { flexGrow: 1 },
  verdictCard: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flexDirection: "row",
    overflow: "hidden",
  },
  verdictBar: { width: 10 },
  verdictBody: { flex: 1, gap: 4, minWidth: 0, padding: 16 },
  phaseRow: { alignItems: "center", flexDirection: "row", gap: 8 },
  phaseLabel: { color: colors.mutedInk, fontSize: 13, fontWeight: "700" },
  verdictTitle: { fontSize: 34, fontWeight: "900", lineHeight: 40 },
  verdictAdvice: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 20,
    marginTop: 2,
  },
  shareFailed: {
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 20,
    marginTop: 4,
  },
  sectionLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
    marginBottom: 4,
  },
  sectionTitle: { color: colors.ink, fontSize: 18, fontWeight: "900" },
  summaryLine: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    paddingVertical: 5,
  },
  summaryLabel: {
    color: colors.mutedInk,
    flexShrink: 1,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 20,
  },
  summaryValue: {
    flexShrink: 1,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 20,
    textAlign: "right",
  },
  entryGrid: { alignItems: "flex-start", flexDirection: "row", gap: 10 },
  entryCard: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flex: 1,
    minWidth: 0,
    // 普通卡片多留 1.5 的内边距，与首选入口的粗描边对齐，三列内容不会错位。
    padding: 15.5,
  },
  entryCardPreferred: {
    borderColor: colors.accent,
    borderWidth: 2.5,
    padding: 14,
  },
  entryHeader: { alignItems: "center", flexDirection: "row", gap: 8 },
  entryName: { color: colors.ink, flexShrink: 1, fontSize: 19, fontWeight: "900" },
  entryRole: { alignSelf: "center", flexShrink: 0 },
  entryHost: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    marginBottom: 8,
    marginTop: 4,
  },
  emptyText: { color: colors.mutedInk, fontSize: 15, fontWeight: "600" },
  checkRow: {
    borderTopColor: colors.border,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: 3,
    justifyContent: "center",
    minHeight: 56,
    paddingVertical: 9,
  },
  checkRowFirst: { borderTopWidth: 0 },
  checkRowTop: { alignItems: "center", flexDirection: "row", gap: 8 },
  checkLabel: {
    color: colors.ink,
    flex: 1,
    fontSize: 15,
    fontWeight: "800",
    minWidth: 0,
  },
  checkMs: {
    color: colors.mutedInk,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  statusPill: {
    alignItems: "center",
    borderRadius: 13,
    justifyContent: "center",
    minHeight: 26,
    minWidth: 46,
    paddingHorizontal: 10,
  },
  statusText: { fontSize: 12.5, fontWeight: "800", textAlign: "center" },
  checkDetail: { color: colors.mutedInk, fontSize: 12.5, lineHeight: 18 },
  reportText: {
    color: colors.ink,
    fontFamily: "Menlo",
    fontSize: 12.5,
    lineHeight: 19,
  },
}));
