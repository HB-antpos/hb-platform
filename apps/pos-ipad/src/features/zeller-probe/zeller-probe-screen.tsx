import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, Share, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  resolveZellerProbeLocale,
  zellerProbeText,
  type ZellerProbeCopyKey,
  type ZellerProbeLocale,
} from "./zeller-probe-copy";
import {
  buildZellerProbeReport,
  probeInitialise,
  probePurchase,
  probeQuery,
  probeRefund,
  probeSetup,
  type ZellerProbeEntry,
  type ZellerProbeOperation,
  type ZellerProbePorts,
  type ZellerProbePurchase,
  type ZellerProbeStep,
  type ZellerProbeTone,
} from "./zeller-probe-runner";
import type { ZellerSdkHostState } from "./zeller-sdk-host";

import { PosPressable } from "@/ui/controls/pos-pressable";
import { posColors } from "@/ui/theme";

/** 可选的验证金额（分）。Zeller 没有测试环境，只给小金额，不提供自由输入。 */
export const ZELLER_PROBE_AMOUNTS_CENTS = [100, 200, 500] as const;

const TONE_COLORS: Record<ZellerProbeTone, string> = {
  good: posColors.green,
  bad: posColors.red,
  warn: posColors.yellow,
  neutral: posColors.blue,
};

export type ZellerProbeScreenProps = Readonly<{
  /** SDK 的加载状态；unavailable 表示当前构建不提供验证页（正式版）。 */
  sdk: ZellerSdkHostState | Readonly<{ status: "unavailable" }>;
  /** 写进日志开头的环境信息（App 版本、设备、SDK 版本）。 */
  context: readonly string[];
  ports: Pick<ZellerProbePorts, "newGuid" | "nowMs" | "nowIso">;
  locale?: ZellerProbeLocale;
  onBack(): void;
  /** 测试可注入；默认用系统分享面板。 */
  shareReport?: (report: string, title: string) => Promise<void>;
}>;

async function shareWithSystemSheet(report: string, title: string) {
  await Share.share({ message: report, title });
}

/**
 * Zeller 真机验证页（开发工具）：左侧按顺序操作（配对 → 扣款 → 查询 / 退款），右侧是逐步日志。
 * 每一步都同时记录 SDK 的原始返回和我们的转换、分类结论，日志可以整份分享出来。
 *
 * 中文注释：同一时间只允许一个 SDK 调用。SDK 并发时会返回 Operation Busy，
 * 并且真机上出现过把一个调用的结果交给另一个调用的情况，所以进行中时除了「取消」以外的按钮全部停用。
 */
export function ZellerProbeScreen({
  sdk,
  context,
  ports,
  locale: localeOverride,
  onBack,
  shareReport = shareWithSystemSheet,
}: ZellerProbeScreenProps) {
  const { i18n } = useTranslation();
  const locale =
    localeOverride ?? resolveZellerProbeLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = useCallback(
    (key: ZellerProbeCopyKey, values?: Readonly<Record<string, string | number>>) =>
      zellerProbeText(locale, key, values),
    [locale],
  );

  const [entries, setEntries] = useState<readonly ZellerProbeEntry[]>([]);
  const [busyStep, setBusyStep] = useState<ZellerProbeStep | null>(null);
  const [amountCents, setAmountCents] = useState<number>(ZELLER_PROBE_AMOUNTS_CENTS[0]);
  const [lastPurchase, setLastPurchase] = useState<ZellerProbePurchase | null>(null);
  const [cancellable, setCancellable] = useState(false);
  const [shareFailed, setShareFailed] = useState(false);
  const operationRef = useRef<ZellerProbeOperation | null>(null);
  // 用 ref 而不是 state 做串行门：连点两下时第二下在同一帧里就能被挡住。
  const busyRef = useRef(false);

  const terminal = sdk.status === "ready" ? sdk.terminal : null;
  const busy = busyStep !== null;

  const runnerPorts = useMemo<ZellerProbePorts>(
    () => ({
      ...ports,
      onOperation: (operation) => {
        operationRef.current = operation;
        setCancellable(typeof operation?.cancel === "function");
      },
    }),
    [ports],
  );

  const run = useCallback(
    async (step: ZellerProbeStep, action: () => Promise<ZellerProbeEntry>) => {
      if (busyRef.current) return;
      busyRef.current = true;
      setBusyStep(step);
      try {
        const entry = await action();
        setEntries((current) => [...current, entry]);
      } catch (error) {
        // 执行逻辑自己会接住 SDK 的异常；到这里说明是我们的代码出错，也要留在日志里。
        setEntries((current) => [
          ...current,
          {
            step,
            startedAtIso: ports.nowIso(),
            elapsedMs: 0,
            tone: "bad",
            headline: `${step} → probe error`,
            lines: [error instanceof Error ? error.message : String(error)],
          },
        ]);
      } finally {
        busyRef.current = false;
        setBusyStep(null);
        setCancellable(false);
      }
    },
    [ports],
  );

  const onInitialise = () => {
    if (!terminal) return;
    void run("initialise", () => probeInitialise(terminal, runnerPorts));
  };
  const onSetup = () => {
    if (!terminal) return;
    void run("setup", () => probeSetup(terminal, runnerPorts));
  };
  const onPurchase = () => {
    if (!terminal) return;
    void run("purchase", async () => {
      const { entry, purchase } = await probePurchase(terminal, amountCents, runnerPorts);
      setLastPurchase(purchase);
      return entry;
    });
  };
  const onQuery = () => {
    if (!terminal || !lastPurchase) return;
    void run("query", () => probeQuery(terminal, lastPurchase, runnerPorts));
  };
  const refundableUuid =
    lastPurchase?.decision === "Approved" ? lastPurchase.transactionUuid : null;
  const onRefund = () => {
    if (!terminal || !lastPurchase || !refundableUuid) return;
    void run("refund", () =>
      probeRefund(
        terminal,
        { transactionUuid: refundableUuid, amountCents: lastPurchase.expectation.amountCents },
        runnerPorts,
      ),
    );
  };
  const onCancel = () => operationRef.current?.cancel?.();

  const report = useMemo(() => buildZellerProbeReport(context, entries), [context, entries]);
  const onShare = async () => {
    try {
      await shareReport(report, t("title"));
      setShareFailed(false);
    } catch {
      setShareFailed(true);
    }
  };

  const sdkLabel =
    sdk.status === "ready"
      ? t("sdk.ready")
      : sdk.status === "loading"
        ? t("sdk.loading")
        : sdk.status === "failed"
          ? t("sdk.failed", { reason: sdk.reason })
          : t("sdk.unavailable");
  const sdkTone: ZellerProbeTone =
    sdk.status === "ready" ? "good" : sdk.status === "loading" ? "neutral" : "bad";
  const actionsDisabled = !terminal || busy;

  return (
    <SafeAreaView style={styles.safeArea} testID="zeller-probe-screen">
      <View style={styles.header}>
        <ActionButton
          icon="arrow-left"
          label={t("action.back")}
          onPress={onBack}
          sound="navigate"
          testID="zeller-probe-back"
          tone="secondary"
        />
        <View style={styles.headerCopy}>
          <Text style={styles.title}>{t("title")}</Text>
          <Text numberOfLines={1} style={styles.subtitle}>
            {t("subtitle")}
          </Text>
        </View>
        <ActionButton
          disabled={entries.length === 0 || busy}
          icon="trash-can-outline"
          label={t("action.clear")}
          onPress={() => setEntries([])}
          testID="zeller-probe-clear"
          tone="secondary"
        />
        <ActionButton
          disabled={entries.length === 0}
          icon="share-variant-outline"
          label={t("action.share")}
          onPress={() => void onShare()}
          testID="zeller-probe-share"
          tone="secondary"
        />
      </View>

      <View style={styles.workspace}>
        <ScrollView contentContainerStyle={styles.columnContent} style={styles.sideColumn}>
          <View style={styles.warning} testID="zeller-probe-warning">
            <MaterialCommunityIcons color={posColors.red} name="alert" size={20} />
            <Text style={styles.warningText}>{t("warning.realMoney")}</Text>
          </View>

          <View style={[styles.statusCard, { borderLeftColor: TONE_COLORS[sdkTone] }]}>
            <Text style={styles.sectionTitle}>{t("section.sdk")}</Text>
            <Text selectable style={styles.statusText} testID="zeller-probe-sdk-status">
              {sdkLabel}
            </Text>
            <View style={styles.busyRow}>
              {busy ? <ActivityIndicator color={posColors.blue} size="small" /> : null}
              <Text style={styles.hint} testID="zeller-probe-state">
                {busy ? t("state.busy") : t("state.idle")}
              </Text>
            </View>
          </View>

          <View style={styles.panel}>
            <Text style={styles.sectionTitle}>{t("section.pairing")}</Text>
            <Text style={styles.hint}>{t("hint.pairing")}</Text>
            <View style={styles.buttonRow}>
              <ActionButton
                disabled={actionsDisabled}
                icon="link-variant"
                label={t("action.initialise")}
                onPress={onInitialise}
                testID="zeller-probe-initialise"
                tone="secondary"
              />
              <ActionButton
                disabled={actionsDisabled}
                icon="account-key-outline"
                label={t("action.setup")}
                onPress={onSetup}
                testID="zeller-probe-setup"
                tone="secondary"
              />
            </View>
          </View>

          <View style={styles.panel}>
            <Text style={styles.sectionTitle}>{t("section.purchase")}</Text>
            <Text style={styles.hint}>{t("amount.label")}</Text>
            <View style={styles.buttonRow}>
              {ZELLER_PROBE_AMOUNTS_CENTS.map((cents) => (
                <PosPressable
                  accessibilityRole="button"
                  accessibilityState={{ disabled: busy, selected: cents === amountCents }}
                  disabled={busy}
                  key={cents}
                  onPress={() => setAmountCents(cents)}
                  style={[
                    styles.amountChip,
                    cents === amountCents && styles.amountChipSelected,
                    busy && styles.disabledButton,
                  ]}
                  testID={`zeller-probe-amount-${cents}`}
                >
                  <Text
                    style={[
                      styles.amountChipLabel,
                      cents === amountCents && styles.amountChipLabelSelected,
                    ]}
                  >
                    {formatDollars(cents)}
                  </Text>
                </PosPressable>
              ))}
            </View>
            <View style={styles.buttonRow}>
              <ActionButton
                disabled={actionsDisabled}
                icon="credit-card-outline"
                label={t("action.purchase", { amount: formatDollars(amountCents) })}
                onPress={onPurchase}
                testID="zeller-probe-purchase"
                tone="primary"
              />
              <ActionButton
                disabled={!busy || !cancellable}
                icon="close-circle-outline"
                label={t("action.cancel")}
                onPress={onCancel}
                testID="zeller-probe-cancel"
                tone="danger"
              />
            </View>
          </View>

          <View style={styles.panel}>
            <Text style={styles.sectionTitle}>{t("section.followUp")}</Text>
            <Text selectable style={styles.lastPurchase} testID="zeller-probe-last">
              {lastPurchase
                ? t("last.summary", {
                    decision: lastPurchase.decision,
                    reference: lastPurchase.expectation.reference,
                  })
                : t("last.none")}
            </Text>
            <Text style={styles.hint}>{t("hint.followUp")}</Text>
            <View style={styles.buttonRow}>
              <ActionButton
                disabled={actionsDisabled || !lastPurchase}
                icon="magnify"
                label={t("action.query")}
                onPress={onQuery}
                testID="zeller-probe-query"
                tone="secondary"
              />
              <ActionButton
                disabled={actionsDisabled || !refundableUuid}
                icon="cash-refund"
                label={t("action.refund")}
                onPress={onRefund}
                testID="zeller-probe-refund"
                tone="secondary"
              />
            </View>
          </View>
        </ScrollView>

        <ScrollView contentContainerStyle={styles.columnContent} style={styles.mainColumn}>
          <View style={styles.panel}>
            <Text style={styles.sectionTitle}>{t("section.log")}</Text>
            {shareFailed ? <Text style={styles.shareFailed}>{t("share.failed")}</Text> : null}
            {entries.length === 0 ? (
              <Text style={styles.hint} testID="zeller-probe-log-empty">
                {t("log.empty")}
              </Text>
            ) : (
              entries.map((entry, index) => (
                <View
                  key={`${entry.startedAtIso}-${index}`}
                  style={[styles.entry, { borderLeftColor: TONE_COLORS[entry.tone] }]}
                  testID={`zeller-probe-entry-${index}`}
                >
                  <View style={styles.entryTop}>
                    <Text selectable style={styles.entryHeadline}>
                      {entry.headline}
                    </Text>
                    <Text style={styles.entryMs}>{entry.elapsedMs} ms</Text>
                  </View>
                  <Text selectable style={styles.entryLines}>
                    {entry.lines.join("\n")}
                  </Text>
                </View>
              ))
            )}
          </View>
        </ScrollView>
      </View>
    </SafeAreaView>
  );
}

function formatDollars(cents: number): string {
  return `$${(cents / 100).toFixed(2)}`;
}

function ActionButton({
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
  workspace: { flex: 1, flexDirection: "row", gap: 16, padding: 16 },
  sideColumn: { flex: 1 },
  mainColumn: { flex: 1.3 },
  columnContent: { gap: 16, paddingBottom: 24 },
  warning: {
    alignItems: "center",
    backgroundColor: posColors.redSoft,
    flexDirection: "row",
    gap: 10,
    padding: 14,
  },
  warningText: { color: posColors.red, flex: 1, fontSize: 15, fontWeight: "800" },
  statusCard: {
    backgroundColor: posColors.surface,
    borderLeftWidth: 5,
    gap: 6,
    padding: 18,
  },
  statusText: { color: posColors.ink, fontSize: 17, fontWeight: "800" },
  busyRow: { alignItems: "center", flexDirection: "row", gap: 8, minHeight: 22 },
  panel: { backgroundColor: posColors.surface, gap: 10, padding: 18 },
  sectionTitle: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
    letterSpacing: 0.6,
  },
  hint: { color: posColors.mutedInk, fontSize: 14, lineHeight: 20 },
  buttonRow: { flexDirection: "row", flexWrap: "wrap", gap: 10 },
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
  amountChip: {
    alignItems: "center",
    borderColor: posColors.border,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 48,
    minWidth: 88,
    paddingHorizontal: 14,
  },
  amountChipSelected: { backgroundColor: posColors.blueSoft, borderColor: posColors.blue },
  amountChipLabel: {
    color: posColors.ink,
    fontSize: 16,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  amountChipLabelSelected: { color: posColors.blue, fontWeight: "900" },
  lastPurchase: { color: posColors.ink, fontSize: 14, fontWeight: "700" },
  shareFailed: { color: posColors.red, fontSize: 14, fontWeight: "700" },
  entry: {
    borderLeftWidth: 4,
    borderTopColor: posColors.border,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: 6,
    paddingLeft: 12,
    paddingVertical: 10,
  },
  entryTop: { alignItems: "baseline", flexDirection: "row", gap: 8 },
  entryHeadline: { color: posColors.ink, flex: 1, fontSize: 16, fontWeight: "900" },
  entryMs: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
  },
  entryLines: { color: posColors.ink, fontFamily: "Menlo", fontSize: 12, lineHeight: 18 },
});
