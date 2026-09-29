import { useCallback, useEffect, useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Dialog, Portal, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useQueryClient } from "@tanstack/react-query";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { fetchPickSheet, submitPickOrder } from "../api";
import { readPickingError } from "../api-normalization";
import { lineStatus, sortLinesByLocation, summarizeLines, summarizePickers } from "../pick-math";
import { activeTeammates, relativeMinutes, shortPickerName } from "../pick-view-model";
import { usePickerStore } from "../picker-store";
import { PICKER_RECONFIRM_CODES, pickingErrorMessage } from "../picking-errors";
import { PICK_SESSION_STATUS } from "../types";
import type { PickSheet } from "../types";
import { Avatar, PickHeader } from "../components/PickHeader";
import { ProductThumb } from "../components/ProductThumb";
import { MONO_FONT, PICK_COLORS } from "../components/pick-theme";
import { PICKING_HOME } from "./PickOrderListView";

/** 5 分钟内还有操作的同事视为“还在拣”，提交前提醒。 */
const STILL_PICKING_MS = 5 * 60 * 1000;

/**
 * 完成拣货：核对差异后提交，各行已拣合计写入订单配货数；订单保持配货中，由主管按现有流程出库。
 * 提交会锁定本单拣货，其他同事的后续写入会被拒绝，所以有人还在拣时先确认。
 */
export function PickFinishScreen({ orderGuid }: { orderGuid: string }) {
  const { t, language } = useAppTranslation("warehousePicking");
  const router = useRouter();
  const queryClient = useQueryClient();
  const picker = usePickerStore((state) => state.picker);
  const clearPicker = usePickerStore((state) => state.clearPicker);
  const [sheet, setSheet] = useState<PickSheet | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [confirmVisible, setConfirmVisible] = useState(false);
  const [snackbar, setSnackbar] = useState("");
  const [nowMs, setNowMs] = useState(() => Date.now());

  const load = useCallback(() => {
    setLoadError(null);
    fetchPickSheet(orderGuid)
      .then((result) => {
        setSheet(result);
        setNowMs(Date.now());
      })
      .catch((error) => setLoadError(pickingErrorMessage(error, t, language)));
  }, [language, orderGuid, t]);

  useEffect(() => {
    load();
  }, [load]);

  const backToPicking = (focus?: string) =>
    router.navigate({
      pathname: "/(shell)/warehouse-picking/[orderGuid]",
      params: focus ? { orderGuid, focus } : { orderGuid },
    } as Parameters<typeof router.navigate>[0]);

  if (!sheet) {
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.center}>
        {loadError ? (
          <>
            <Text style={styles.centerText}>{loadError}</Text>
            <Button mode="contained" onPress={load}>
              {t("actions.retry")}
            </Button>
          </>
        ) : (
          <ActivityIndicator />
        )}
      </SafeAreaView>
    );
  }

  const summary = summarizeLines(sheet.lines);
  const variances = sortLinesByLocation(sheet.lines).filter((line) => lineStatus(line) !== "complete");
  const pickers = summarizePickers(sheet.lines);
  const stillPicking = activeTeammates(sheet.participants, picker?.userGuid ?? null, nowMs, STILL_PICKING_MS);
  const submitted = sheet.session.status === PICK_SESSION_STATUS.submitted;

  const submit = async () => {
    setConfirmVisible(false);
    setSubmitting(true);
    try {
      await submitPickOrder(orderGuid);
      await queryClient.invalidateQueries({ queryKey: ["warehousePicking", "orders"] });
      router.dismissTo(PICKING_HOME);
    } catch (error) {
      const { code } = readPickingError(error);
      if (code && PICKER_RECONFIRM_CODES.has(code)) {
        clearPicker();
        router.dismissTo(PICKING_HOME);
        return;
      }
      setSnackbar(pickingErrorMessage(error, t, language));
      if (code === "SESSION_SUBMITTED") load();
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.screen}>
      <PickHeader
        title={t("finish.title")}
        subtitle={t("finish.subtitle", { orderNo: sheet.orderNo ?? "—", store: sheet.storeName || sheet.storeCode || "—" })}
        onBack={() => backToPicking()}
        backLabel={t("actions.back")}
      />
      <ScrollView contentContainerStyle={styles.content}>
        {submitted ? (
          <View style={[styles.notice, styles.noticeWarning]}>
            <Text style={styles.noticeWarningText}>{t("picking.readonlyTitle", { name: sheet.session.submittedByName ?? "" })}</Text>
          </View>
        ) : null}
        {!submitted
          ? stillPicking.map((teammate) => {
              const relative = relativeMinutes(teammate.lastActiveAtUtc, nowMs);
              return (
                <View key={teammate.pickerUserGuid} accessibilityLiveRegion="polite" style={styles.notice}>
                  <Avatar name={teammate.pickerName} color={PICK_COLORS.textSecondary} size={26} />
                  <Text style={styles.noticeText}>
                    {t("finish.stillPicking", {
                      name: teammate.pickerName,
                      time: relative.kind === "justNow" ? t("time.justNow") : t("time.minutesAgo", { count: relative.count }),
                    })}
                  </Text>
                </View>
              );
            })
          : null}

        <View style={styles.summaryCard}>
          <View style={styles.bigRow}>
            <Text style={styles.big}>{summary.completeLineCount}</Text>
            <Text style={styles.bigLabel}>{t("finish.linesComplete", { total: summary.lineCount })}</Text>
          </View>
          <View style={styles.grid}>
            <Metric label={t("finish.pieces")} value={`${summary.pickedPieces}/${summary.orderedPieces}`} />
            <Metric label={t("finish.short")} value={t("finish.lineCount", { count: summary.shortLineCount })} tone="danger" />
            <Metric label={t("finish.over")} value={t("finish.lineCount", { count: summary.overLineCount })} tone="warning" />
          </View>
          {pickers.length > 0 ? (
            <Text style={styles.pickers}>
              {t("finish.pickersSummary", {
                summary: pickers.map((entry) => `${entry.pickerName} ${entry.quantity}`).join(" · "),
              })}
            </Text>
          ) : null}
        </View>

        <View style={styles.sectionHeader}>
          <Text style={styles.sectionTitle}>{t("finish.variances")}</Text>
          <Text style={styles.sectionHint}>{t("finish.variancesHint")}</Text>
        </View>
        {variances.length === 0 ? <Text style={styles.sectionHint}>{t("finish.noVariances")}</Text> : null}
        {variances.map((line) => {
          const diff = line.pickedTotal - line.orderedQuantity;
          const over = diff > 0;
          return (
            <Pressable key={line.detailGuid} accessibilityRole="button" onPress={() => backToPicking(line.detailGuid)} style={styles.row}>
              <ProductThumb uri={line.productImage} size={44} />
              <View style={styles.rowText}>
                <View style={styles.rowTitle}>
                  <Text style={styles.rowLocation}>{line.locationCode || "—"}</Text>
                  <Text numberOfLines={1} style={styles.rowName}>
                    {line.productName || line.productCode}
                  </Text>
                </View>
                <Text style={styles.rowMeta}>{t("finish.orderedPicked", { ordered: line.orderedQuantity, picked: line.pickedTotal })}</Text>
              </View>
              <Text style={[styles.badge, over ? styles.badgeOver : styles.badgeShort]}>
                {over ? t("finish.badgeOver", { count: diff }) : t("finish.badgeShort", { count: -diff })}
              </Text>
            </Pressable>
          );
        })}
        <Text style={styles.note}>{t("finish.note")}</Text>
      </ScrollView>

      <View style={styles.footer}>
        <Pressable accessibilityRole="button" onPress={() => backToPicking()} style={styles.secondaryButton}>
          <Text style={styles.secondaryText}>{t("finish.keepPicking")}</Text>
        </Pressable>
        <Pressable
          accessibilityRole="button"
          disabled={submitted || submitting}
          onPress={() => (stillPicking.length > 0 ? setConfirmVisible(true) : void submit())}
          style={[styles.primaryButton, submitted || submitting ? styles.disabled : null]}
        >
          {submitting ? <ActivityIndicator color={PICK_COLORS.white} /> : <Text style={styles.primaryText}>{t("finish.submit")}</Text>}
        </Pressable>
      </View>

      <Portal>
        <Dialog visible={confirmVisible} onDismiss={() => setConfirmVisible(false)}>
          <Dialog.Title>{t("finish.confirmTitle")}</Dialog.Title>
          <Dialog.Content>
            <Text>
              {t("finish.confirmBody", { names: stillPicking.map((teammate) => shortPickerName(teammate.pickerName)).join("、") })}
            </Text>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setConfirmVisible(false)}>{t("actions.cancel")}</Button>
            <Button onPress={() => void submit()}>{t("finish.confirmSubmit")}</Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3500} style={styles.snackbar}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

function Metric({ label, value, tone }: { label: string; value: string; tone?: "danger" | "warning" }) {
  const palette =
    tone === "danger"
      ? { bg: PICK_COLORS.dangerBg, border: PICK_COLORS.dangerBorder, label: PICK_COLORS.dangerText, value: PICK_COLORS.danger }
      : tone === "warning"
        ? { bg: PICK_COLORS.warningBg, border: PICK_COLORS.warningBorder, label: PICK_COLORS.warningText, value: PICK_COLORS.warning }
        : { bg: PICK_COLORS.cellBg, border: PICK_COLORS.outlineMuted, label: PICK_COLORS.textSecondary, value: PICK_COLORS.ink };
  return (
    <View style={[styles.metric, { backgroundColor: palette.bg, borderColor: palette.border }]}>
      <Text style={[styles.metricLabel, { color: palette.label }]}>{label}</Text>
      <Text style={[styles.metricValue, { color: palette.value }]}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: PICK_COLORS.background },
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PICK_COLORS.background },
  centerText: { textAlign: "center", color: PICK_COLORS.ink },
  content: { padding: 12, paddingBottom: 88, gap: 10 },
  notice: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PICK_COLORS.infoBg, borderWidth: 1, borderColor: PICK_COLORS.infoBorder, borderRadius: 10, paddingHorizontal: 12, paddingVertical: 8 },
  noticeText: { flex: 1, fontSize: 13, lineHeight: 18, color: PICK_COLORS.infoText },
  noticeWarning: { backgroundColor: PICK_COLORS.warningBg, borderColor: PICK_COLORS.warningBorder },
  noticeWarningText: { flex: 1, fontSize: 13, lineHeight: 18, fontWeight: "700", color: PICK_COLORS.warningText },
  summaryCard: { backgroundColor: PICK_COLORS.white, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, borderRadius: 12, paddingHorizontal: 14, paddingVertical: 12, gap: 10 },
  bigRow: { flexDirection: "row", alignItems: "baseline", gap: 6 },
  big: { fontSize: 32, lineHeight: 38, fontWeight: "700", color: PICK_COLORS.ink, fontVariant: ["tabular-nums"] },
  bigLabel: { fontSize: 15, lineHeight: 22, color: PICK_COLORS.textSecondary },
  grid: { flexDirection: "row", gap: 8 },
  metric: { flex: 1, borderWidth: 1, borderRadius: 8, paddingHorizontal: 10, paddingVertical: 6 },
  metricLabel: { fontSize: 12, lineHeight: 16 },
  metricValue: { fontSize: 16, lineHeight: 24, fontWeight: "700", fontVariant: ["tabular-nums"] },
  pickers: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  sectionHeader: { flexDirection: "row", justifyContent: "space-between", alignItems: "baseline" },
  sectionTitle: { fontSize: 14, lineHeight: 20, fontWeight: "700", color: PICK_COLORS.ink },
  sectionHint: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  row: { minHeight: 60, flexDirection: "row", alignItems: "center", gap: 10, paddingLeft: 8, paddingRight: 12, paddingVertical: 8, borderRadius: 10, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, backgroundColor: PICK_COLORS.white },
  rowText: { flex: 1, minWidth: 0, gap: 2 },
  rowTitle: { flexDirection: "row", alignItems: "baseline", gap: 8 },
  rowLocation: { fontFamily: MONO_FONT, fontSize: 13, fontWeight: "700", color: PICK_COLORS.ink },
  rowName: { flexShrink: 1, fontSize: 13, color: PICK_COLORS.ink },
  rowMeta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  badge: { fontSize: 13, lineHeight: 24, fontWeight: "700", paddingHorizontal: 8, borderRadius: 6, overflow: "hidden", borderWidth: 1, fontVariant: ["tabular-nums"] },
  badgeShort: { backgroundColor: PICK_COLORS.dangerBg, borderColor: PICK_COLORS.dangerBorder, color: PICK_COLORS.danger },
  badgeOver: { backgroundColor: PICK_COLORS.warningBg, borderColor: PICK_COLORS.warningBorder, color: PICK_COLORS.warning },
  note: { fontSize: 12, lineHeight: 18, color: PICK_COLORS.textSecondary },
  footer: { position: "absolute", left: 0, right: 0, bottom: 0, flexDirection: "row", gap: 8, paddingHorizontal: 12, paddingVertical: 8, backgroundColor: PICK_COLORS.white, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PICK_COLORS.outlineMuted },
  secondaryButton: { flex: 1, height: 48, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.outline, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center" },
  secondaryText: { fontSize: 15, fontWeight: "600", color: PICK_COLORS.ink },
  primaryButton: { flex: 1.6, height: 48, borderRadius: 8, backgroundColor: PICK_COLORS.ink, alignItems: "center", justifyContent: "center" },
  primaryText: { fontSize: 15, fontWeight: "700", color: PICK_COLORS.white },
  disabled: { opacity: 0.45 },
  snackbar: { marginBottom: 72 },
});
