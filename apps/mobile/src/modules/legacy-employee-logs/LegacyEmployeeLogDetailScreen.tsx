import { useCallback, useEffect, useRef, useState } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { ActivityIndicator, Button, Icon, IconButton, Snackbar, Text, TextInput } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { productTitle } from "@/components/legacy-employee-logs/LegacyLogCard";
import { ProductImageBox } from "@/components/seasonal-product-insights/ProductImageBox";
import { DangerBadge, OperationTag, ReviewBadge, RuleBadge } from "@/components/legacy-employee-logs/LegacyLogTags";
import { LEGACY_UI, RISK } from "@/components/legacy-employee-logs/ui";
import { createProductInsightRequestGate } from "@/modules/product-insights/request-gate";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { fetchLegacyLogContext, fetchPosLogContext, reviewLegacyLog, reviewPosLog } from "./api";
import { LegacyScreenMessage, useLegacyLogsGuard, usePosOperationLabel } from "./LegacyEmployeeLogsScreen";
import { activeReview, clockOf, describeFlagEvidence, formatAmountImpact, productThumbnailUri, storeDisplayName } from "./logic";
import { notifyLegacyLogReviewed } from "./review-events";
import type { LegacyLogContext, LegacyLogItem, LogSource } from "./types";

const NOTE_MAX_LENGTH = 500;
const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value)?.trim() || "";

export function LegacyEmployeeLogDetailScreen() {
  const { t } = useAppTranslation("legacyEmployeeLogs");
  const router = useRouter();
  const blocked = useLegacyLogsGuard();
  const params = useLocalSearchParams<{ id?: string | string[]; source?: string | string[] }>();
  const id = first(params.id);
  // 来源由列表带入；缺省按老收银（旧版本的深链只有 id）。
  const source: LogSource = first(params.source) === "pos" ? "pos" : "legacy";
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/legacy-employee-logs"));
  if (blocked) return <LegacyScreenMessage message={blocked} onBack={goBack} />;
  if (!id) return <LegacyScreenMessage message={t("states.detailNotFound")} onBack={goBack} />;
  return <DetailContent key={`${source}:${id}`} id={id} source={source} onBack={goBack} />;
}

function DetailContent({ id, source, onBack }: { id: string; source: LogSource; onBack: () => void }) {
  const { t, language } = useAppTranslation("legacyEmployeeLogs");
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const { stores } = useStores();
  const canReview = useAuthStore((state) => state.access.canReviewLegacyEmployeeLogs);
  const reviewerName = useAuthStore((state) => state.user?.fullName || state.user?.username || "");
  const [context, setContext] = useState<LegacyLogContext | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState("");
  const [submitting, setSubmitting] = useState<"normal" | "followUp" | "revoked" | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const gate = useRef(createProductInsightRequestGate()).current;
  const storeNames = new Map(stores.map((store) => [store.storeCode, store.storeName || store.storeCode]));
  const posOperationLabel = usePosOperationLabel();

  const load = useCallback(async () => {
    const lease = gate.begin();
    setLoading(true);
    setError(null);
    try {
      const result = source === "pos" ? await fetchPosLogContext(id, posOperationLabel, lease.signal) : await fetchLegacyLogContext(id, lease.signal);
      if (lease.isCurrent()) setContext(result);
    } catch (cause) {
      if (!lease.isCurrent()) return;
      const status = (cause as { response?: { status?: number } })?.response?.status;
      setError(
        status === 404
          ? t("states.detailNotFound")
          : status === 403
            ? t("states.detailForbidden")
            : resolveLocalizedErrorMessage(cause, { t, language, fallbackKey: "messages.detailFailed", allowRawMessageInChinese: false }),
      );
    } finally {
      if (lease.isCurrent()) setLoading(false);
    }
  }, [gate, id, language, posOperationLabel, source, t]);

  useEffect(() => {
    void load();
    return () => gate.cancel();
  }, [gate, load]);

  const target = context?.target ?? null;

  const submit = async (result: "normal" | "followUp" | "revoked") => {
    if (!target) return;
    setSubmitting(result);
    try {
      const body = {
        result,
        note: result === "revoked" ? undefined : note.trim() || undefined,
        // 撤销后再核查也要带上次的版本号；从未核查过时为 null。
        expectedVersion: target.review?.version ?? null,
      };
      const review = source === "pos" ? await reviewPosLog({ eventId: target.id, ...body }) : await reviewLegacyLog({ logId: target.id, ...body });
      setContext((current) => (current ? { ...current, target: { ...current.target, review } } : current));
      setNote("");
      setToast(t(result === "revoked" ? "review.revoked" : "review.saved"));
      notifyLegacyLogReviewed();
    } catch (cause) {
      const status = (cause as { response?: { status?: number } })?.response?.status;
      if (status === 409) {
        setToast(t("review.conflict"));
        void load();
        notifyLegacyLogReviewed();
      } else {
        setToast(resolveLocalizedErrorMessage(cause, { t, language, fallbackKey: "review.failed", allowRawMessageInChinese: false }));
      }
    } finally {
      setSubmitting(null);
    }
  };

  const review = activeReview(target?.review);
  const pendingReview = Boolean(target && target.flags.length > 0 && !review && canReview);

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={t("actions.back")} onPress={onBack} />
        <Text variant="titleMedium" style={styles.title}>{t("detail.title")}</Text>
        <View style={styles.headerSpacer} />
      </View>

      {loading ? (
        <View style={styles.state}><ActivityIndicator color={HB_COLORS.brand} /></View>
      ) : error || !target ? (
        <View style={styles.state}>
          <EmptyState title={t("states.loadFailed")} description={error ?? undefined} actionLabel={t("actions.retry")} onAction={() => void load()} />
        </View>
      ) : (
        <>
          <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
            <View style={styles.tags}>
              <OperationTag operation={target.operation} tone={target.tone} />
              {target.isDanger ? <DangerBadge label={t("badges.danger")} /> : null}
              {target.flags.map((flag) => <RuleBadge key={flag.ruleCode} label={t(`rules.${flag.ruleCode}.label`)} />)}
              <ReviewBadge item={target} reviewedLabel={t("badges.reviewed")} followUpLabel={t("badges.followUp")} />
            </View>
            <View style={styles.titleRow}>
              {target.hasProduct ? (
                <ProductImageBox uri={productThumbnailUri(target.productImage, 112)} size={56} label={target.title ?? ""} />
              ) : null}
              <View style={styles.headlineBox}>
                <Text style={styles.headline}>{target.title ?? productTitle(target.operationDetail) ?? target.operation ?? "-"}</Text>
                {target.itemNumber ? <Text style={[styles.headlineItemNumber, LEGACY_UI.mono]}>{target.itemNumber}</Text> : null}
              </View>
              {formatAmountImpact(target.amountImpact) ? (
                <Text style={[styles.amount, LEGACY_UI.mono]}>{formatAmountImpact(target.amountImpact)}</Text>
              ) : null}
            </View>
            <Text style={styles.meta}>
              {[target.employeeName || "-", `${storeDisplayName(target.storeCode, storeNames)}（${target.storeCode ?? "-"}）`, target.deviceCode || "-", clockOf(target.operationTime)].join(" · ")}
            </Text>

            {target.flags.map((flag) => (
              <View key={flag.ruleCode} style={styles.callout}>
                <View style={styles.calloutHead}>
                  <Icon source="pulse" size={16} color={RISK.abnormalIcon} />
                  <Text style={styles.calloutTitle}>{t(`rules.${flag.ruleCode}.label`)}</Text>
                </View>
                <Text style={styles.calloutBody}>
                  {describeFlagEvidence(flag).map((part) => t(`evidence.${part.key}`, part.params)).join("；")}
                </Text>
                <Text style={styles.calloutRule}>{t(source === "pos" ? `rulesPos.${flag.ruleCode}` : `rules.${flag.ruleCode}.desc`)}</Text>
              </View>
            ))}
            {target.flags.length === 0 && target.isDanger ? <Text style={styles.dangerOnly}>{t(source === "pos" ? "detail.pos.dangerOnly" : "detail.dangerOnly")}</Text> : null}

            {target.flags.length > 0 ? (
              <View style={[LEGACY_UI.card, styles.panel]}>
                <View style={styles.panelHead}>
                  <Text style={styles.panelTitle}>{t("review.title")}</Text>
                  {review ? (
                    canReview ? (
                      <Button compact loading={submitting === "revoked"} disabled={submitting !== null} onPress={() => void submit("revoked")}>
                        {t("review.revoke")}
                      </Button>
                    ) : null
                  ) : (
                    <Text style={styles.pending}>{t("review.pending")}</Text>
                  )}
                </View>
                {review ? (
                  <>
                    <Text style={styles.muted}>{t("review.by", { name: review.reviewedByName, at: review.reviewedAtUtc.replace("T", " ").slice(0, 16) })}</Text>
                    {review.note ? <Text style={styles.body}>{review.note}</Text> : null}
                  </>
                ) : canReview ? (
                  <>
                    <Text style={styles.muted}>{t("review.reviewer", { name: reviewerName })}</Text>
                    <TextInput
                      mode="outlined"
                      label={t("review.noteLabel")}
                      placeholder={t("review.notePlaceholder")}
                      value={note}
                      onChangeText={setNote}
                      maxLength={NOTE_MAX_LENGTH}
                      multiline
                      numberOfLines={2}
                    />
                  </>
                ) : (
                  <Text style={styles.muted}>{t("review.readOnly")}</Text>
                )}
              </View>
            ) : null}

            {target.pos ? (
              <View style={[LEGACY_UI.card, styles.panel]}>
                <Info label={t("detail.operationTime")} value={`${target.operationTime.replace("T", " ")}（${t("detail.pos.localTimeNote")}）`} />
                <Info label={t("detail.pos.receivedTime")} value={target.lastUploadTime.replace("T", " ").slice(11, 19)} />
                <Info label={t("detail.store")} value={`${storeDisplayName(target.storeCode, storeNames)} ${target.storeCode ?? ""}`} />
                <Info label={t("detail.device")} value={[target.deviceCode ?? "-", target.pos.deviceSystem].filter(Boolean).join(" · ")} />
                <Info label={t("detail.employee")} value={[target.employeeName ?? "-", target.employeeId].filter(Boolean).join(" · ")} />
                <Info label={t("detail.pos.outcome")} value={t(`outcomes.${target.pos.outcome}`, { defaultValue: target.pos.outcome || "-" })} />
                {target.pos.reasonCode ? <Info label={t("detail.pos.reason")} value={target.pos.reasonCode} /> : null}
                {target.pos.paymentMethod || target.pos.paymentAmount !== null ? (
                  <Info label={t("detail.pos.payment")} value={[target.pos.paymentMethod, target.pos.paymentAmount?.toFixed(2)].filter(Boolean).join(" · ")} />
                ) : null}
                {target.pos.beforeActual !== null ? (
                  <Info label={t("detail.pos.actual")} value={`${target.pos.beforeActual.toFixed(2)} → ${target.pos.afterActual?.toFixed(2) ?? "-"}`} />
                ) : null}
                {target.pos.orderGuid ? <Info label={t("detail.pos.order")} value={target.pos.orderGuid} /> : null}
                {target.pos.isEmergencyOverride || target.pos.isOfflineCached ? (
                  <Info
                    label={t("detail.pos.flags")}
                    value={[target.pos.isEmergencyOverride ? t("detail.pos.emergency") : null, target.pos.isOfflineCached ? t("detail.pos.offline") : null].filter(Boolean).join(" · ")}
                  />
                ) : null}
                {target.pos.safeMessage ? <Info label={t("detail.pos.message")} value={target.pos.safeMessage} /> : null}
              </View>
            ) : (
              <View style={[LEGACY_UI.card, styles.panel]}>
                <Info label={t("detail.operationTime")} value={target.operationTime.replace("T", " ")} />
                <Info label={t("detail.uploadTime")} value={`${target.lastUploadTime.replace("T", " ").slice(11, 19)} · ${t("detail.lag", { minutes: lagMinutes(target) })}`} />
                <Info label={t("detail.store")} value={`${storeDisplayName(target.storeCode, storeNames)} ${target.storeCode ?? ""}`} />
                <Info label={t("detail.device")} value={target.deviceCode ?? "-"} />
                <Info label={t("detail.employee")} value={target.employeeName ?? "-"} />
                <Info label={t("detail.raw")} value={target.operationDetail ?? "-"} />
              </View>
            )}

            <View style={[LEGACY_UI.card, styles.panel]}>
              <View style={styles.panelHead}>
                <Text style={styles.panelTitle}>{t("detail.context")}</Text>
                <Text style={styles.muted}>{t("detail.contextHint", { minutes: context?.windowMinutes ?? 15 })}</Text>
              </View>
              {(context?.neighbors.length ?? 0) <= 1 ? (
                <Text style={styles.muted}>{t("detail.contextEmpty")}</Text>
              ) : (
                context!.neighbors.map((item) => <TimelineRow key={item.id} item={item} current={item.id === target.id} currentLabel={t("detail.current")} />)
              )}
              {target.employeeId ? (
                <Button
                  compact
                  style={styles.employeeDay}
                  onPress={() =>
                    router.replace({
                      pathname: "/(shell)/legacy-employee-logs",
                      params: {
                        source,
                        stores: target.storeCode ?? "",
                        preset: "today",
                        employeeId: target.employeeId!,
                        employeeName: target.employeeName ?? target.employeeId!,
                      },
                    })
                  }
                >
                  {t("detail.onlyEmployeeDay", { name: target.employeeName ?? target.employeeId })}
                </Button>
              ) : null}
            </View>
          </ScrollView>

          {pendingReview ? (
            <View style={[styles.bottomBar, { paddingBottom: HB_SPACING.sm + insets.bottom }]}>
              <Button
                mode="outlined"
                icon="flag-outline"
                textColor={RISK.followUp}
                style={[styles.bottomButton, { borderColor: RISK.followUp }]}
                loading={submitting === "followUp"}
                disabled={submitting !== null}
                onPress={() => void submit("followUp")}
              >
                {t("review.followUp")}
              </Button>
              <Button mode="contained" icon="check" style={styles.bottomButton} loading={submitting === "normal"} disabled={submitting !== null} onPress={() => void submit("normal")}>
                {t("review.normal")}
              </Button>
            </View>
          ) : null}
        </>
      )}
      <Snackbar visible={toast !== null} onDismiss={() => setToast(null)} duration={2500}>
        {toast ?? ""}
      </Snackbar>
    </SafeAreaView>
  );
}

function lagMinutes(item: LegacyLogItem) {
  const operated = Date.parse(item.operationTime);
  const uploaded = Date.parse(item.lastUploadTime);
  if (!Number.isFinite(operated) || !Number.isFinite(uploaded) || uploaded < operated) return "-";
  return String(Math.floor((uploaded - operated) / 60000));
}

function Info({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.info}>
      <Text style={styles.infoLabel}>{label}</Text>
      <Text style={styles.infoValue} selectable>{value}</Text>
    </View>
  );
}

function TimelineRow({ item, current, currentLabel }: { item: LegacyLogItem; current: boolean; currentLabel: string }) {
  return (
    <View style={[styles.timeline, current ? styles.timelineCurrent : null]}>
      <Text style={[styles.timelineTime, LEGACY_UI.mono, current ? styles.bold : null]}>{clockOf(item.operationTime)}</Text>
      <View style={[styles.dot, current ? styles.dotCurrent : null]} />
      <View style={styles.timelineBody}>
        <View style={styles.tags}>
          <OperationTag operation={item.operation} tone={item.tone} />
          <Text style={styles.muted}>{item.employeeName ?? "-"}</Text>
        </View>
        <Text numberOfLines={2} style={[styles.body, current ? styles.bold : null]}>
          {item.operationDetail ?? "-"}
          {current ? ` · ${currentLabel}` : ""}
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center" },
  title: { flex: 1, textAlign: "center", fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 48 },
  state: { paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center" },
  content: { paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.lg, gap: HB_SPACING.sm },
  tags: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: 6 },
  titleRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  headlineBox: { flex: 1, minWidth: 0, gap: 2 },
  headline: { fontSize: 20, fontWeight: "700", color: HB_COLORS.textPrimary },
  headlineItemNumber: { fontSize: 13, color: HB_COLORS.textSecondary },
  amount: { fontSize: 18, fontWeight: "700", color: RISK.danger },
  meta: { fontSize: 13, color: HB_COLORS.textSecondary },
  callout: { gap: 6, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.surface, borderWidth: 1, borderColor: RISK.abnormalBorder, backgroundColor: "#FFFAEB" },
  calloutHead: { flexDirection: "row", alignItems: "center", gap: 8 },
  calloutTitle: { fontSize: 15, fontWeight: "700", color: RISK.abnormalText },
  calloutBody: { fontSize: 14, lineHeight: 21, color: "#3D2B00" },
  calloutRule: { fontSize: 12, color: "#6B5320" },
  dangerOnly: { fontSize: 13, lineHeight: 19, color: "#6E1D05", backgroundColor: "#FFF5F1", padding: HB_SPACING.sm, borderRadius: HB_RADIUS.control },
  panel: { gap: 10 },
  panelHead: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  panelTitle: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  pending: { fontSize: 12, fontWeight: "600", color: "#8A5800" },
  muted: { fontSize: 12, color: HB_COLORS.textSecondary },
  body: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textPrimary },
  bold: { fontWeight: "700" },
  info: { flexDirection: "row", gap: HB_SPACING.sm },
  infoLabel: { width: 76, fontSize: 13, color: HB_COLORS.textSecondary },
  infoValue: { flex: 1, fontSize: 13, color: HB_COLORS.textPrimary },
  timeline: { flexDirection: "row", gap: 8, padding: 8, borderRadius: HB_RADIUS.control },
  timelineCurrent: { backgroundColor: "#FFF6DD" },
  timelineTime: { width: 64, fontSize: 13, color: HB_COLORS.textPrimary },
  dot: { width: 9, height: 9, marginTop: 5, borderRadius: 5, backgroundColor: "#B9C0C8" },
  dotCurrent: { backgroundColor: RISK.abnormalIcon },
  timelineBody: { flex: 1, gap: 4 },
  employeeDay: { alignSelf: "flex-start" },
  bottomBar: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  bottomButton: { flex: 1 },
});
