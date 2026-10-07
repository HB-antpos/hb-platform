import { ScrollView, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { ActivityIndicator, Button, IconButton, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { BackfillNotice, CashCountPanel, DataPlaceholder, InfoPanel, ReconciliationCard, SectionTitle, TenderTable } from "@/components/daily-closes/DailyCloseDetailSections";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { DailyCloseScreenMessage, useBusinessDateLabel, useDailyCloseErrorMessage, useDailyClosesGuard } from "./daily-close-shared";
import {
  classifyDailyCloseError,
  clientKindLabelKey,
  formatDailyCloseCount,
  formatDailyCloseMoney,
  formatSavedLabel,
  formatZonedDateTime,
  hasCashCountData,
  hasTenderData,
  isBackfilledRecord,
  resolveBackfillNotice,
  resolveSaveLogLink,
  saveSequenceMark,
  todayInSydney,
} from "./logic";
import { useDailyCloseDetail } from "./use-daily-closes";

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value)?.trim() || "";

export function DailyCloseDetailScreen() {
  const { t } = useAppTranslation("dailyCloses");
  const router = useRouter();
  const blocked = useDailyClosesGuard();
  const params = useLocalSearchParams<{ id?: string | string[] }>();
  const id = first(params.id);
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/daily-closes"));
  if (blocked) return <DailyCloseScreenMessage message={blocked} onBack={goBack} />;
  if (!id) return <DailyCloseScreenMessage message={t("detail.notFound")} onBack={goBack} />;
  return <DetailContent key={id} id={id} onBack={goBack} />;
}

function DetailContent({ id, onBack }: { id: string; onBack: () => void }) {
  const { t } = useAppTranslation("dailyCloses");
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const query = useDailyCloseDetail(id);
  const formatError = useDailyCloseErrorMessage("errors.detailFailed");
  const dateLabel = useBusinessDateLabel();
  const canLegacyLogs = useAuthStore((state) => state.access.canViewLegacyEmployeeLogs);
  const canPosLogs = useAuthStore((state) => state.access.canViewPosOperationAudits);
  const detail = query.data;

  let body;
  if (query.isPending) {
    body = (
      <View style={styles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
      </View>
    );
  } else if (query.isError || !detail) {
    // 404（不存在或不在可见分店内）重试没有意义，只给返回
    const notFound = classifyDailyCloseError(query.error) === "notFound";
    body = (
      <View style={styles.state}>
        <EmptyState
          title={notFound ? t("detail.notFound") : t("states.loadFailed")}
          description={formatError(query.error)}
          actionLabel={notFound ? undefined : t("actions.retry")}
          onAction={notFound ? undefined : () => void query.refetch()}
        />
      </View>
    );
  } else {
    const sourceKey = clientKindLabelKey(detail.clientKind);
    const source = sourceKey ? t(`source.${sourceKey}`) : detail.clientKind || "-";
    const store = detail.storeName || detail.storeCode || "-";
    const notice = resolveBackfillNotice(detail);
    const saveLog = resolveSaveLogLink(detail, { canLegacy: canLegacyLogs, canPos: canPosLogs }, todayInSydney());
    // 折叠态摘要：只列有值的项（补录记录没有订单数与退货件数），都没有时显示「—」
    const infoSummary =
      [
        detail.orderCount !== null ? t("detail.info.summaryOrders", { value: formatDailyCloseCount(detail.orderCount) }) : null,
        detail.returnQuantity !== null ? t("detail.info.summaryReturns", { value: formatDailyCloseCount(detail.returnQuantity) }) : null,
      ]
        .filter(Boolean)
        .join(" · ") || "—";
    const infoRows = [
      { label: t("detail.info.store"), value: detail.storeName ? `${store} (${detail.storeCode})` : store },
      { label: t("detail.info.device"), value: detail.deviceCode || "-" },
      { label: t("detail.info.cashier"), value: [detail.cashierName, detail.cashierId].filter(Boolean).join(" · ") || "-" },
      { label: t("detail.info.businessDate"), value: `${detail.businessDate}${detail.businessDateInferred ? t("detail.inferredSuffix") : ""}` },
      { label: t("detail.info.orders"), value: formatDailyCloseCount(detail.orderCount) },
      { label: t("detail.info.returns"), value: formatDailyCloseCount(detail.returnQuantity) },
      { label: t("detail.info.cardNet"), value: formatDailyCloseMoney(detail.cardNetAmount) },
      { label: t("detail.info.source"), value: source },
      { label: t("detail.info.dataSource"), value: t(detail.dataSource === "AuditBackfill" ? "dataSource.auditBackfill" : "dataSource.clientUpload") },
      {
        label: t("detail.info.period"),
        value: detail.periodFromUtc || detail.periodToUtc ? `${formatZonedDateTime(detail.periodFromUtc, detail.storeTimeZoneId)} – ${formatZonedDateTime(detail.periodToUtc, detail.storeTimeZoneId)}` : "—",
      },
      { label: t("detail.info.guid"), value: detail.dailyCloseGuid, selectable: true },
      { label: t("detail.info.appVersion"), value: detail.appVersion ?? "—" },
      { label: t("detail.info.receivedAt"), value: formatZonedDateTime(detail.receivedAtUtc, detail.storeTimeZoneId) },
    ];
    body = (
      <ScrollView contentContainerStyle={[styles.content, { paddingBottom: HB_SPACING.lg + insets.bottom }]}>
        <ReconciliationCard
          detail={detail}
          identity={`${store} · ${detail.deviceCode || "-"} · ${detail.cashierName || detail.cashierId || "-"}`}
          dateLine={t("detail.dateLine", {
            date: dateLabel(detail.businessDate),
            inferred: detail.businessDateInferred ? t("detail.inferredSuffix") : "",
            time: formatSavedLabel(detail.savedAtUtc, detail.storeTimeZoneId, detail.businessDate),
            source,
          })}
          nth={saveSequenceMark(detail)}
          backfill={isBackfilledRecord(detail)}
        />
        {notice ? <BackfillNotice notice={notice} /> : null}

        <SectionTitle>{t("detail.tenders.title")}</SectionTitle>
        {hasTenderData(detail) ? <TenderTable tenders={detail.tenders} /> : <DataPlaceholder />}

        <SectionTitle>{t("detail.cashCount.title")}</SectionTitle>
        {hasCashCountData(detail) ? <CashCountPanel detail={detail} /> : <DataPlaceholder />}

        <InfoPanel
          summary={infoSummary}
          rows={infoRows}
        />

        {saveLog ? (
          <Button
            mode="outlined"
            icon="clipboard-text-clock-outline"
            onPress={() => router.push({ pathname: "/(shell)/legacy-employee-logs", params: { ...saveLog } })}
          >
            {t("detail.saveLog")}
          </Button>
        ) : null}
      </ScrollView>
    );
  }

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={t("actions.back")} onPress={onBack} />
        <Text variant="titleMedium" style={styles.title}>
          {t("detail.title")}
        </Text>
        <View style={styles.headerSpacer} />
      </View>
      {body}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center" },
  title: { flex: 1, textAlign: "center", fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 48 },
  state: { paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center" },
  content: { paddingHorizontal: HB_SPACING.md, gap: HB_SPACING.sm },
});
