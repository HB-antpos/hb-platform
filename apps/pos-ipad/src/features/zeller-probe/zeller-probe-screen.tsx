import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, Share, StyleSheet, Text, View } from "react-native";

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

import { PosButton } from "@/ui/controls/pos-button";
import { PosSegmented } from "@/ui/controls/pos-segmented";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
  type PosPalette,
} from "@/ui/theme";

/** 可选的验证金额（分）。Zeller 没有测试环境，只给小金额，不提供自由输入。 */
export const ZELLER_PROBE_AMOUNTS_CENTS = [100, 200, 500] as const;

// 只存色名：色值在渲染时按当前主题读取。
const TONE_COLORS: Record<ZellerProbeTone, keyof PosPalette> = {
  good: "green",
  bad: "red",
  warn: "yellow",
  neutral: "blue",
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
  usePosThemeVersion();
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
    <PosScreenFrame
      header={
        <PosPageHeader
          actions={
            <>
              <PosButton
                disabled={entries.length === 0 || busy}
                icon="trash-can-outline"
                label={t("action.clear")}
                onPress={() => setEntries([])}
                size="medium"
                testID="zeller-probe-clear"
                tone="quiet"
              />
              <PosButton
                disabled={entries.length === 0}
                icon="export-variant"
                label={t("action.share")}
                onPress={() => void onShare()}
                size="medium"
                testID="zeller-probe-share"
                tone="quiet"
              />
            </>
          }
          backLabel={t("action.back")}
          backTestID="zeller-probe-back"
          onBack={onBack}
          subtitle={t("subtitle")}
          title={t("title")}
        />
      }
      rail="none"
      testID="zeller-probe-screen"
    >
      <View style={styles.workspace}>
        <ScrollView contentContainerStyle={styles.columnContent} style={styles.sideColumn}>
          <View style={styles.warning} testID="zeller-probe-warning">
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.red}
              name="alert"
              size={24}
            />
            <Text style={styles.warningText}>{t("warning.realMoney")}</Text>
          </View>

          <View style={styles.statusCard}>
            <View
              style={[
                styles.statusBar,
                { backgroundColor: posColors[TONE_COLORS[sdkTone]] },
              ]}
            />
            <View style={styles.statusBody}>
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
          </View>

          <View style={styles.panel}>
            <Text style={styles.sectionTitle}>{t("section.pairing")}</Text>
            <Text style={styles.hint}>{t("hint.pairing")}</Text>
            <View style={styles.buttonRow}>
              <PosButton
                disabled={actionsDisabled}
                fill
                icon="link-variant"
                label={t("action.initialise")}
                onPress={onInitialise}
                size="medium"
                testID="zeller-probe-initialise"
              />
              <PosButton
                disabled={actionsDisabled}
                fill
                icon="account-key-outline"
                label={t("action.setup")}
                onPress={onSetup}
                size="medium"
                testID="zeller-probe-setup"
              />
            </View>
          </View>

          <View style={styles.panel}>
            <Text style={styles.sectionTitle}>{t("section.purchase")}</Text>
            <Text style={styles.hint}>{t("amount.label")}</Text>
            {/* 金额三选一：只给固定的小金额，进行中不能换。 */}
            <PosSegmented
              accessibilityLabel={t("amount.label")}
              disabled={busy}
              onChange={(value) => setAmountCents(Number(value))}
              options={ZELLER_PROBE_AMOUNTS_CENTS.map((cents) => ({
                label: formatDollars(cents),
                testID: `zeller-probe-amount-${cents}`,
                value: String(cents),
              }))}
              value={String(amountCents)}
            />
            <View style={styles.buttonRow}>
              {/* 真实扣款是这一页唯一的主操作；「取消」只在刷卡机等待时可用。 */}
              <PosButton
                disabled={actionsDisabled}
                fill
                icon="credit-card-outline"
                label={t("action.purchase", { amount: formatDollars(amountCents) })}
                onPress={onPurchase}
                size="large"
                testID="zeller-probe-purchase"
                tone="primary"
              />
              <PosButton
                disabled={!busy || !cancellable}
                icon="close-circle-outline"
                label={t("action.cancel")}
                onPress={onCancel}
                size="large"
                style={styles.cancelButton}
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
              <PosButton
                disabled={actionsDisabled || !lastPurchase}
                fill
                icon="magnify"
                label={t("action.query")}
                onPress={onQuery}
                size="medium"
                testID="zeller-probe-query"
              />
              <PosButton
                disabled={actionsDisabled || !refundableUuid}
                fill
                icon="cash-refund"
                label={t("action.refund")}
                onPress={onRefund}
                size="medium"
                testID="zeller-probe-refund"
              />
            </View>
          </View>
        </ScrollView>

        <ScrollView contentContainerStyle={styles.logContent} style={styles.mainColumn}>
          <View style={[styles.panel, styles.logPanel]}>
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
                  style={[styles.entry, { borderLeftColor: posColors[TONE_COLORS[entry.tone]] }]}
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
    </PosScreenFrame>
  );
}

function formatDollars(cents: number): string {
  return `$${(cents / 100).toFixed(2)}`;
}

const styles = createPosStyles((colors) => ({
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  sideColumn: { flexGrow: 0, flexShrink: 0, width: 440 },
  mainColumn: { flex: 1, minWidth: 0 },
  columnContent: { gap: 12, paddingBottom: 12 },
  logContent: { flexGrow: 1 },
  warning: {
    alignItems: "center",
    backgroundColor: colors.redSoft,
    borderColor: colors.red,
    borderRadius: posRadius.large,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    padding: 14,
  },
  warningText: {
    color: colors.red,
    flex: 1,
    fontSize: 16,
    fontWeight: "900",
    lineHeight: 22,
  },
  statusCard: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flexDirection: "row",
    overflow: "hidden",
  },
  statusBar: { width: 8 },
  statusBody: { flex: 1, gap: 6, minWidth: 0, padding: 16 },
  statusText: { color: colors.ink, fontSize: 17, fontWeight: "800" },
  busyRow: { alignItems: "center", flexDirection: "row", gap: 8, minHeight: 22 },
  panel: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 10,
    padding: 16,
  },
  logPanel: { flexGrow: 1 },
  sectionTitle: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
  },
  hint: { color: colors.mutedInk, fontSize: 14, fontWeight: "600", lineHeight: 20 },
  buttonRow: { flexDirection: "row", gap: 10 },
  cancelButton: { minWidth: 132 },
  lastPurchase: { color: colors.ink, fontSize: 15, fontWeight: "800" },
  shareFailed: { color: colors.red, fontSize: 14, fontWeight: "700" },
  entry: {
    borderLeftWidth: 4,
    borderTopColor: colors.border,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: 6,
    paddingLeft: 12,
    paddingVertical: 10,
  },
  entryTop: { alignItems: "baseline", flexDirection: "row", gap: 8 },
  entryHeadline: { color: colors.ink, flex: 1, fontSize: 17, fontWeight: "900" },
  entryMs: {
    color: colors.mutedInk,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  entryLines: { color: colors.ink, fontFamily: "Menlo", fontSize: 13, lineHeight: 20 },
}));
