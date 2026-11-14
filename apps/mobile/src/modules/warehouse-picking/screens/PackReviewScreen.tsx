import { useCallback, useEffect, useRef, useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQueryClient } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { clauseSeparator } from "@/shared/i18n/punctuation";
import { UnitRow, Tag, packStyles } from "../../packing/components/pack-ui";
import { fetchPickSheet, newClientRequestId, putPickLineTotal, submitPickOrder } from "../api";
import { readPickingError } from "../api-normalization";
import { encodePackedParam } from "../pack-carton-logic";
import { readPackSubmitBlock } from "../pack-api-normalization";
import { packTotals, planPickTotalChanges, reviewState } from "../pack-review-logic";
import { packLaterRoute } from "../pack-routes";
import { PACK_CARTON_STATUS, type PackMismatch, type PackProgressCarton } from "../pack-types";
import { usePickerStore } from "../picker-store";
import { PICKER_RECONFIRM_CODES, pickingErrorMessage } from "../picking-errors";
import type { PickSheet } from "../types";
import { useOrderCarton } from "../use-order-carton";
import { PickHeader } from "../components/PickHeader";
import { PickSealSheet } from "../components/PickSealSheet";
import { MONO_FONT, PICK_COLORS } from "../components/pick-theme";
import { PICKING_HOME } from "./PickOrderListView";

/**
 * 提交拣货前核对：提交时服务端发现还有没封的箱、或拣货数与装箱数对不上，就来到这里。
 * 三格汇总（已拣 / 已装箱 / 箱数）、没封的箱（去封箱）、差异商品（去补装 / 按装箱数改拣货数），处理完自动重新提交。
 * 数据都是现拉的：封箱、补装回来后自动刷新。
 */
export function PackReviewScreen({ orderGuid }: { orderGuid: string }) {
  const { t, language } = useAppTranslation("warehousePicking");
  const router = useRouter();
  const focused = useIsFocused();
  const queryClient = useQueryClient();
  const clearPicker = usePickerStore((state) => state.clearPicker);
  const [sheet, setSheet] = useState<PickSheet | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [working, setWorking] = useState<"submit" | "adjust" | null>(null);
  const [snackbar, setSnackbar] = useState("");
  const workingRef = useRef(false);

  const handleAuthError = useCallback(
    (error: unknown) => {
      const { code } = readPickingError(error);
      if (code && PICKER_RECONFIRM_CODES.has(code)) {
        clearPicker();
        router.dismissTo(PICKING_HOME);
        return true;
      }
      return false;
    },
    [clearPicker, router],
  );

  const pack = useOrderCarton({
    orderGuid,
    // 回到本页（封箱 / 补装回来）时重新拉一次进度。
    enabled: focused,
    productOf: () => null,
    onAuthError: handleAuthError,
    notify: setSnackbar,
  });
  const { refresh: refreshPack, progress } = pack;

  const loadSheet = useCallback(async () => {
    try {
      const result = await fetchPickSheet(orderGuid);
      setSheet(result);
      setLoadError(null);
      return result;
    } catch (error) {
      if (!handleAuthError(error)) setLoadError(pickingErrorMessage(error, t, language));
      return null;
    }
  }, [handleAuthError, language, orderGuid, t]);

  useEffect(() => {
    if (focused) void loadSheet();
  }, [focused, loadSheet]);

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PICKING_HOME));

  const submit = useCallback(async () => {
    if (workingRef.current) return;
    workingRef.current = true;
    setWorking("submit");
    try {
      const result = await submitPickOrder(orderGuid);
      await queryClient.invalidateQueries({ queryKey: ["warehousePicking", "orders"] });
      router.dismissTo(
        (result.cartonCount != null
          ? { pathname: PICKING_HOME, params: { packed: encodePackedParam(result.cartonCount, result.packedPieces ?? 0) } }
          : PICKING_HOME) as Parameters<typeof router.dismissTo>[0],
      );
    } catch (error) {
      if (handleAuthError(error)) return;
      const { code, data } = readPickingError(error);
      if (readPackSubmitBlock(code, data)) {
        // 状态又变了（别人刚装 / 开了箱）：重新拉最新情况再看。
        await Promise.all([refreshPack(), loadSheet()]);
        setSnackbar(t("review.stillBlocked"));
        return;
      }
      setSnackbar(pickingErrorMessage(error, t, language));
    } finally {
      workingRef.current = false;
      setWorking(null);
    }
  }, [handleAuthError, language, loadSheet, orderGuid, queryClient, refreshPack, router, t]);

  /** 处理完重新拉最新数据，没有别的问题了就自动重新提交。 */
  const resubmitIfReady = useCallback(async () => {
    const [nextProgress, nextSheet] = await Promise.all([refreshPack(), loadSheet()]);
    if (nextProgress && nextSheet && reviewState(nextProgress, nextSheet.lines).canSubmit) await submit();
  }, [loadSheet, refreshPack, submit]);

  /** 按装箱数改拣货数：逐商品逐行 PUT lines/{d}/total（不带 cartonGuid），合计以现有拣货为准。 */
  const adjustToPacked = useCallback(
    async (targets: PackMismatch[]) => {
      if (workingRef.current) return;
      workingRef.current = true;
      setWorking("adjust");
      let ok = false;
      try {
        const fresh = await fetchPickSheet(orderGuid);
        const totals = new Map(fresh.lines.map((line) => [line.detailGuid, line.pickedTotal] as const));
        const unresolved: PackMismatch[] = [];
        for (const target of targets) {
          const plan = planPickTotalChanges(target, totals);
          if (plan === null) {
            unresolved.push(target);
            continue;
          }
          for (const change of plan) {
            const result = await putPickLineTotal(orderGuid, change.detailGuid, {
              total: change.total,
              expectedTotal: change.expectedTotal,
              clientRequestId: newClientRequestId(),
            });
            totals.set(change.detailGuid, result.line.pickedTotal);
          }
        }
        if (unresolved.length > 0) {
          setSnackbar(t("review.unresolved", { names: unresolved.map((item) => item.productName || item.productCode).join(clauseSeparator(language)) }));
        } else {
          ok = true;
        }
      } catch (error) {
        if (!handleAuthError(error)) setSnackbar(pickingErrorMessage(error, t, language));
      } finally {
        workingRef.current = false;
        setWorking(null);
      }
      if (ok) await resubmitIfReady();
      else await Promise.all([refreshPack(), loadSheet()]);
    },
    [handleAuthError, language, loadSheet, orderGuid, refreshPack, resubmitIfReady, t],
  );

  const sealCarton = (carton: PackProgressCarton) => {
    pack.selectCarton(carton);
    pack.setSealVisible(true);
  };

  if (!progress || !sheet) {
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.center}>
        {loadError || (pack.loaded && pack.available && !progress) ? (
          <>
            <Text accessibilityLiveRegion="polite" style={styles.centerText}>
              {loadError ?? t("messages.loadFailed")}
            </Text>
            <Button
              mode="contained"
              onPress={() => {
                void loadSheet();
                void refreshPack();
              }}
            >
              {t("actions.retry")}
            </Button>
            <Button onPress={goBack}>{t("actions.back")}</Button>
          </>
        ) : !pack.available ? (
          <>
            <Text style={styles.centerText}>{t("review.unavailable")}</Text>
            <Button onPress={goBack}>{t("actions.back")}</Button>
          </>
        ) : (
          <ActivityIndicator />
        )}
      </SafeAreaView>
    );
  }

  const totals = packTotals(progress);
  const state = reviewState(progress, sheet.lines);
  const issueCount = state.openCartons.length + state.mismatches.length;
  const adjustable = state.mismatches.filter((item) => item.detailGuids.length > 0);
  const busy = working !== null || pack.sealing;

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.screen}>
      <PickHeader
        title={t("review.title")}
        subtitle={t("review.subtitle", { orderNo: sheet.orderNo ?? "—", store: sheet.storeName || sheet.storeCode || "—" })}
        onBack={goBack}
        backLabel={t("actions.back")}
      />
      <ScrollView contentContainerStyle={styles.content}>
        <View style={styles.metrics}>
          <Metric label={t("review.picked")} value={String(totals.picked)} unit={t("review.unitPieces")} />
          <Metric
            label={t("review.packed")}
            value={String(totals.packed)}
            unit={t("review.unitPieces")}
            tone={totals.packed === totals.picked ? undefined : "warning"}
          />
          <Metric label={t("review.cartons")} value={String(totals.cartonCount)} unit={t("review.unitCartons")} />
        </View>

        {state.openCartons.length > 0 ? (
          <View style={[styles.alert, styles.alertWarning]} accessibilityLiveRegion="polite">
            <View style={styles.alertHead}>
              <MaterialCommunityIcons name="alert-outline" size={18} color={PICK_COLORS.warning} />
              <Text style={styles.alertTitle}>{t("review.openCartonsTitle", { count: state.openCartons.length })}</Text>
            </View>
            {state.openCartons.map((carton) => (
              <View key={carton.cartonGuid} style={styles.openRow}>
                <View style={styles.openText}>
                  <Text style={styles.mono}>{carton.cartonNo}</Text>
                  <Text style={styles.muted}>
                    {t("review.cartonStat", { sku: carton.skuCount, pieces: carton.pieceCount })}
                    {carton.isMine || !carton.createdByName ? "" : ` · ${carton.createdByName}`}
                  </Text>
                </View>
                <Pressable
                  accessibilityRole="button"
                  disabled={busy}
                  onPress={() => sealCarton(carton)}
                  style={[styles.outlineButton, busy ? styles.disabled : null]}
                >
                  <Text style={styles.outlineText}>{t("review.sealCarton")}</Text>
                </Pressable>
              </View>
            ))}
            <Text style={styles.muted}>{t("review.openCartonsHint")}</Text>
          </View>
        ) : null}

        {state.mismatches.length > 0 ? (
          <View style={[styles.alert, styles.alertWarning]} accessibilityLiveRegion="polite">
            <View style={styles.alertHead}>
              <MaterialCommunityIcons name="alert-outline" size={18} color={PICK_COLORS.warning} />
              <Text style={styles.alertTitle}>{t("review.mismatchTitle", { count: state.mismatches.length })}</Text>
            </View>
            {state.mismatches.map((item) => (
              <View key={item.productCode} style={styles.mismatchCard}>
                <View style={styles.mismatchTop}>
                  <View style={styles.openText}>
                    <Text numberOfLines={2} style={styles.productName}>
                      {item.productName || item.itemNumber || item.productCode}
                    </Text>
                    <Text style={styles.muted}>{item.itemNumber || item.productCode}</Text>
                  </View>
                  <View style={styles.mismatchNumbers}>
                    <Text style={styles.pickedText}>{t("review.pickedOf", { count: item.picked })}</Text>
                    <Text style={styles.packedText}>
                      {t("review.packedOf", { count: item.packed })}
                      {" · "}
                      <Text style={styles.diffText}>{item.diff > 0 ? t("review.diffShort", { count: item.diff }) : t("review.diffOver", { count: -item.diff })}</Text>
                    </Text>
                  </View>
                </View>
                <View style={styles.actions}>
                  {item.diff > 0 ? (
                    <Pressable
                      accessibilityRole="button"
                      disabled={busy}
                      onPress={() => router.push(packLaterRoute(orderGuid, item.productCode))}
                      style={[styles.outlineButton, styles.actionButton, busy ? styles.disabled : null]}
                    >
                      <Text style={styles.outlineText}>{t("review.goPack", { count: item.diff })}</Text>
                    </Pressable>
                  ) : null}
                  {item.detailGuids.length > 0 ? (
                    <Pressable
                      accessibilityRole="button"
                      disabled={busy}
                      onPress={() => void adjustToPacked([item])}
                      style={[styles.outlineButton, styles.actionButton, busy ? styles.disabled : null]}
                    >
                      <Text style={styles.outlineText}>{t("review.adjustToPacked")}</Text>
                    </Pressable>
                  ) : (
                    <Pressable
                      accessibilityRole="button"
                      disabled={busy}
                      onPress={() => router.push(packLaterRoute(orderGuid, item.productCode))}
                      style={[styles.outlineButton, styles.actionButton, busy ? styles.disabled : null]}
                    >
                      <Text style={styles.outlineText}>{t("review.goHandle")}</Text>
                    </Pressable>
                  )}
                </View>
                {item.detailGuids.length === 0 ? <Text style={styles.muted}>{t("review.notOnOrder")}</Text> : null}
              </View>
            ))}
            {adjustable.length > 1 ? (
              <Pressable
                accessibilityRole="button"
                disabled={busy}
                onPress={() => void adjustToPacked(adjustable)}
                style={[styles.outlineButton, busy ? styles.disabled : null]}
              >
                <Text style={styles.outlineText}>{t("review.adjustAll", { count: adjustable.length })}</Text>
              </Pressable>
            ) : null}
          </View>
        ) : null}

        {issueCount === 0 ? (
          <View style={[styles.alert, styles.alertOk]}>
            <MaterialCommunityIcons name="check-circle-outline" size={18} color={PICK_COLORS.success} />
            <Text style={styles.okText}>{t("review.allClear")}</Text>
          </View>
        ) : null}

        <Text style={styles.sectionTitle}>{t("review.cartonsTitle")}</Text>
        {progress.cartons.length === 0 ? (
          <Text style={styles.muted}>{t("review.noCartons")}</Text>
        ) : (
          <View style={packStyles.cardList}>
            {progress.cartons.map((carton, index) => {
              const inTransit = carton.status === PACK_CARTON_STATUS.inTransit;
              const sealed = carton.status === PACK_CARTON_STATUS.sealed;
              return (
                <UnitRow
                  key={carton.cartonGuid}
                  divider={index > 0}
                  icon="package-variant-closed"
                  tone={sealed ? "green" : inTransit ? "orange" : "blue"}
                  title={carton.cartonNo}
                  monoTitle
                  subtitle={[
                    t("review.cartonStat", { sku: carton.skuCount, pieces: carton.pieceCount }),
                    carton.palletNo ? t("review.onPallet", { palletNo: carton.palletNo }) : t("review.noPallet"),
                  ].join(" · ")}
                  right={
                    <Tag
                      tone={sealed ? "green" : inTransit ? "orange" : "blue"}
                      label={sealed ? t("review.statusSealed") : inTransit ? t("review.statusInTransit") : t("review.statusPacking")}
                    />
                  }
                />
              );
            })}
          </View>
        )}
        <Text style={styles.muted}>{t("review.note")}</Text>
      </ScrollView>

      <View style={styles.footer}>
        <Pressable
          accessibilityRole="button"
          disabled={!state.canSubmit || busy}
          onPress={() => void submit()}
          style={[styles.primaryButton, !state.canSubmit || busy ? styles.disabled : null]}
        >
          {working === "submit" ? (
            <ActivityIndicator color={PICK_COLORS.white} />
          ) : (
            <Text style={styles.primaryText}>{state.canSubmit ? t("review.submit") : t("review.submitBlocked", { count: issueCount })}</Text>
          )}
        </Pressable>
      </View>

      <PickSealSheet pack={pack} defaultNext="none" onSealed={() => void resubmitIfReady()} />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={4000} style={styles.snackbar}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

function Metric({ label, value, unit, tone }: { label: string; value: string; unit: string; tone?: "warning" }) {
  const palette = tone === "warning" ? { value: PICK_COLORS.warning, bg: PICK_COLORS.warningBg, border: PICK_COLORS.warningBorder } : { value: PICK_COLORS.ink, bg: PICK_COLORS.white, border: PICK_COLORS.outlineMuted };
  return (
    <View style={[styles.metric, { backgroundColor: palette.bg, borderColor: palette.border }]}>
      <Text style={styles.metricLabel}>{label}</Text>
      <Text numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.6} style={[styles.metricValue, { color: palette.value }]}>
        {value}
        <Text style={styles.metricUnit}>{` ${unit}`}</Text>
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: PICK_COLORS.background },
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PICK_COLORS.background },
  centerText: { textAlign: "center", color: PICK_COLORS.ink },
  content: { padding: 12, gap: 10 },
  metrics: { flexDirection: "row", gap: 8 },
  metric: { flex: 1, minWidth: 0, borderWidth: 1, borderRadius: 10, paddingHorizontal: 10, paddingVertical: 8 },
  metricLabel: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  metricValue: { fontSize: 24, lineHeight: 30, fontWeight: "700", fontVariant: ["tabular-nums"] },
  metricUnit: { fontSize: 12, fontWeight: "400", color: PICK_COLORS.textSecondary },
  alert: { borderWidth: 1, borderRadius: 12, padding: 12, gap: 8 },
  alertWarning: { backgroundColor: PICK_COLORS.warningBg, borderColor: PICK_COLORS.warningBorder },
  alertOk: { flexDirection: "row", alignItems: "center", gap: 8, backgroundColor: PICK_COLORS.successBg, borderColor: PICK_COLORS.successBorder },
  alertHead: { flexDirection: "row", alignItems: "center", gap: 8 },
  alertTitle: { flex: 1, fontSize: 14, lineHeight: 20, fontWeight: "700", color: PICK_COLORS.warningText },
  okText: { flex: 1, fontSize: 14, lineHeight: 20, fontWeight: "600", color: PICK_COLORS.successText },
  openRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  openText: { flex: 1, minWidth: 0, gap: 2 },
  mono: { fontFamily: MONO_FONT, fontSize: 14, lineHeight: 20, fontWeight: "700", color: PICK_COLORS.ink },
  muted: { fontSize: 12, lineHeight: 17, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  outlineButton: { minHeight: 44, paddingHorizontal: 12, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.outline, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center" },
  outlineText: { fontSize: 14, fontWeight: "600", color: PICK_COLORS.ink, textAlign: "center" },
  mismatchCard: { backgroundColor: PICK_COLORS.white, borderWidth: 1, borderColor: PICK_COLORS.warningBorder, borderRadius: 10, padding: 10, gap: 8 },
  mismatchTop: { flexDirection: "row", gap: 8 },
  productName: { fontSize: 14, lineHeight: 20, fontWeight: "700", color: PICK_COLORS.ink },
  mismatchNumbers: { alignItems: "flex-end", gap: 2 },
  pickedText: { fontSize: 13, lineHeight: 18, color: PICK_COLORS.ink, fontVariant: ["tabular-nums"] },
  packedText: { fontSize: 13, lineHeight: 18, color: PICK_COLORS.ink, fontVariant: ["tabular-nums"] },
  diffText: { fontWeight: "700", color: PICK_COLORS.warning },
  actions: { flexDirection: "row", gap: 8 },
  actionButton: { flex: 1 },
  sectionTitle: { marginTop: 4, fontSize: 13, lineHeight: 18, fontWeight: "700", color: PICK_COLORS.textSecondary },
  disabled: { opacity: 0.45 },
  // 底栏必须留在文档流里（见拣货页底栏的说明）。
  footer: { paddingHorizontal: 12, paddingVertical: 8, backgroundColor: PICK_COLORS.white, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PICK_COLORS.outlineMuted },
  primaryButton: { height: 48, borderRadius: 8, backgroundColor: PICK_COLORS.ink, alignItems: "center", justifyContent: "center", paddingHorizontal: 12 },
  primaryText: { fontSize: 15, fontWeight: "700", color: PICK_COLORS.white },
  snackbar: { marginBottom: 72 },
});
