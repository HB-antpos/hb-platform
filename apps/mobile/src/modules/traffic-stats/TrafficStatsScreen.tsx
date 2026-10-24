// 「客流统计」页：门店 + 今天 / 本周 / 本月分段 + 日期步进；数据按周期一次取回（当期 + 对照期）。
// 入口守卫：未登录 / iOS 审核壳 / 无 FaceMatch.Traffic.View 直接给出说明，不发请求。
// 只展示门店计数（进店人次、估算访客），不含任何生物信息、照片。
import { useCallback, useEffect, useState } from "react";
import { Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useIsFocused } from "@react-navigation/native";
import { useRouter } from "expo-router";
import { Button, IconButton, Text } from "react-native-paper";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { FaceScreenFrame, FaceScreenMessage, Notice } from "@/modules/face-match/ui";
import { formatStoreLabel, useFaceErrorMessage, useStoreNameMap } from "@/modules/face-match/shared";
import { useFaceStoreOptions } from "@/modules/face-match/use-face-match";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { canViewTrafficStats } from "./access";
import { isUsableTrafficOverview } from "./normalize";
import {
  anchorForPeriodSwitch,
  formatClock,
  formatMonthDay,
  formatYearMonth,
  nextAnchor,
  parseAsOf,
  previousAnchor,
  resolvePeriodHeadline,
  weekdayLabel,
} from "./period";
import { TrafficBody } from "./TrafficBody";
import { DateStepper, PeriodSegment, SkeletonBlock, TrafficCard } from "./traffic-parts";
import type { TrafficOverview, TrafficPeriod } from "./types";
import { useTrafficOverview } from "./use-traffic";

function useTrafficGuard(): string | null {
  const { t } = useAppTranslation("trafficStats");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive) || sessionKind === "iosReview";
  if (review) return t("messages.reviewUnavailable");
  if (!canViewTrafficStats(isAuthenticated, hasPermission, review)) return t("messages.notAllowed");
  return null;
}

export function TrafficStatsScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["trafficStats", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useTrafficGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <FaceScreenMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号看过的门店和数字留在屏幕上。
  return <TrafficStatsContent key={userGuid} onBack={goBack} />;
}

function TrafficStatsContent({ onBack }: { onBack: () => void }) {
  const { t, language } = useAppTranslation(["trafficStats", "common"]);
  const describeError = useFaceErrorMessage();
  const focused = useIsFocused();
  const { stores, isLoading: storesLoading, preferredStoreCode } = useFaceStoreOptions();
  const storeNames = useStoreNameMap(stores);

  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [period, setPeriod] = useState<TrafficPeriod>("day");
  // anchor 为空 = 门店本地今天（服务端解释）；翻看历史周期时才有值。
  const [anchor, setAnchor] = useState<string | null>(null);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [tableExpanded, setTableExpanded] = useState(false);
  const [pickerVisible, setPickerVisible] = useState(false);
  const [manualRefreshing, setManualRefreshing] = useState(false);

  // 默认门店：App 当前已选门店，其次第一家；当前选择不在可选列表里时重新落位。
  useEffect(() => {
    if (storeCode && stores.some((store) => store.storeCode === storeCode)) return;
    const preferred = stores.find((store) => store.storeCode === preferredStoreCode) ?? stores[0] ?? null;
    setStoreCode(preferred?.storeCode ?? null);
  }, [preferredStoreCode, storeCode, stores]);

  const query = useTrafficOverview({ storeCode, period, anchor, focused });
  const overview = query.data && isUsableTrafficOverview(query.data) ? query.data : undefined;
  const storeLabel = storeCode ? formatStoreLabel(storeCode, storeNames) : "";

  const resetView = useCallback(() => {
    setSelectedIndex(null);
    setTableExpanded(false);
  }, []);

  const changePeriod = (next: TrafficPeriod) => {
    if (next === period) return;
    setAnchor(anchorForPeriodSwitch(overview));
    setPeriod(next);
    resetView();
  };
  const step = (direction: "previous" | "next") => {
    if (!overview) return;
    const target = direction === "previous" ? previousAnchor(overview) : nextAnchor(overview);
    if (target === null) return;
    setAnchor(target);
    resetView();
  };
  const selectStore = (code: string) => {
    setPickerVisible(false);
    if (code === storeCode) return;
    setStoreCode(code);
    setAnchor(null);
    resetView();
  };
  const onRefresh = async () => {
    setManualRefreshing(true);
    try {
      await query.refetch();
    } finally {
      setManualRefreshing(false);
    }
  };

  const asOf = overview ? parseAsOf(overview.asOfLocal) : null;
  const freshText = query.isError && overview
    ? t("fresh.failed")
    : asOf
      ? t("fresh.updatedAt", { time: formatClock(asOf.hour, asOf.minute) })
      : "";

  return (
    <FaceScreenFrame
      title={t("title")}
      onBack={onBack}
      right={<IconButton icon="refresh" accessibilityLabel={t("fresh.refresh")} onPress={() => void query.refetch()} disabled={!storeCode} />}
    >
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={manualRefreshing} onRefresh={() => void onRefresh()} />}
        showsVerticalScrollIndicator={false}
      >
        <View style={styles.storeRow}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("store.pickerTitle")}
            disabled={stores.length === 0}
            onPress={() => setPickerVisible(true)}
            style={styles.storeChip}
          >
            <Text style={styles.storeChipText} numberOfLines={1}>
              {storeLabel || (storesLoading ? t("store.loading") : t("store.none"))}
            </Text>
            <MaterialCommunityIcons name="chevron-down" size={16} color={HB_COLORS.textSecondary} />
          </Pressable>
          {freshText ? (
            <View style={styles.fresh}>
              <View style={[styles.freshDot, query.isError ? styles.freshDotWarn : null]} />
              <Text style={styles.freshText} numberOfLines={1}>
                {freshText}
              </Text>
            </View>
          ) : null}
        </View>

        <PeriodSegment
          value={period}
          labels={{ day: t("period.day"), week: t("period.week"), month: t("period.month") }}
          onChange={changePeriod}
        />
        {overview ? (
          <DateStepper
            title={periodTitle(overview, language, t)}
            subtitle={periodSubtitle(overview, language, t)}
            canNext={!overview.isCurrent}
            previousLabel={t("period.previous")}
            nextLabel={t("period.next")}
            onPrevious={() => step("previous")}
            onNext={() => step("next")}
          />
        ) : null}

        {!storeCode ? (
          storesLoading ? <LoadingSkeleton /> : <TrafficCard><Text style={styles.emptyText}>{t("store.none")}</Text></TrafficCard>
        ) : !overview ? (
          query.isError ? (
            <ErrorCard message={describeError(query.error)} title={t("error.title")} retryLabel={t("error.retry")} onRetry={() => void query.refetch()} />
          ) : (
            <LoadingSkeleton />
          )
        ) : !overview.countingEnabled ? (
          <TrafficCard>
            <View style={styles.disabled}>
              <View style={styles.disabledIcon}>
                <MaterialCommunityIcons name="chart-timeline-variant" size={26} color={HB_COLORS.textSecondary} />
              </View>
              <Text style={styles.disabledTitle}>{t("disabled.title")}</Text>
              <Text style={styles.disabledBody}>{t("disabled.body")}</Text>
            </View>
          </TrafficCard>
        ) : (
          <View>
            {query.isError ? (
              <Notice
                tone="danger"
                text={t("error.stale")}
                action={{ label: t("error.retry"), onPress: () => void query.refetch() }}
                style={styles.staleNotice}
              />
            ) : null}
            <TrafficBody
              overview={overview}
              language={language}
              t={t}
              storeLabel={storeLabel}
              selectedIndex={selectedIndex}
              onSelect={setSelectedIndex}
              tableExpanded={tableExpanded}
              onToggleTable={() => setTableExpanded((value) => !value)}
            />
          </View>
        )}
      </ScrollView>

      <StorePickerModal
        presentation="sheet"
        visible={pickerVisible}
        stores={stores}
        selectedStoreCode={storeCode}
        title={t("store.pickerTitle")}
        cancelLabel={t("store.cancel")}
        onDismiss={() => setPickerVisible(false)}
        onSelectStore={(store) => {
          if (store) selectStore(store.storeCode);
        }}
      />
    </FaceScreenFrame>
  );
}

function periodTitle(overview: TrafficOverview, language: "zh" | "en", t: (key: string, options?: Record<string, unknown>) => string): string {
  const headline = resolvePeriodHeadline(overview, language);
  if (headline.kind === "text") return headline.text;
  return t(`period.headline.${headline.kind}`);
}

function periodSubtitle(overview: TrafficOverview, language: "zh" | "en", t: (key: string, options?: Record<string, unknown>) => string): string {
  const { period, from, to, today, isCurrent } = overview;
  if (period === "day") {
    return t("period.sub.dayWithWeekday", { date: formatMonthDay(from, language), weekday: weekdayLabel(from, language) });
  }
  if (period === "week") {
    return t("period.sub.range", { from: formatMonthDay(from, language), to: formatMonthDay(to, language) });
  }
  // 月：标题已是「本月 / 上月 / 2026年7月」，副标题给出覆盖的日期范围；当前月只到今天。
  return isCurrent
    ? t("period.sub.monthToDate", { from: formatMonthDay(from, language), to: formatMonthDay(today, language) })
    : `${formatYearMonth(from, language)} · ${t("period.sub.range", { from: formatMonthDay(from, language), to: formatMonthDay(to, language) })}`;
}

function LoadingSkeleton() {
  return (
    <TrafficCard>
      <SkeletonBlock height={14} width="40%" />
      <SkeletonBlock height={40} width="55%" />
      <SkeletonBlock height={150} />
    </TrafficCard>
  );
}

function ErrorCard({ title, message, retryLabel, onRetry }: { title: string; message: string; retryLabel: string; onRetry: () => void }) {
  return (
    <TrafficCard>
      <View style={styles.disabled}>
        <Text style={styles.disabledTitle}>{title}</Text>
        <Text style={styles.disabledBody}>{message}</Text>
        <Button mode="contained" onPress={onRetry} style={styles.retry}>
          {retryLabel}
        </Button>
      </View>
    </TrafficCard>
  );
}

const styles = StyleSheet.create({
  content: { padding: 12, paddingBottom: HB_SPACING.xl },
  storeRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", marginBottom: 10, gap: HB_SPACING.xs },
  storeChip: {
    flexShrink: 1,
    flexDirection: "row",
    alignItems: "center",
    height: 34,
    paddingHorizontal: 12,
    borderRadius: 17,
    backgroundColor: HB_COLORS.surface,
    borderColor: HB_COLORS.outline,
    borderWidth: 1,
    gap: 6,
  },
  storeChipText: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary, flexShrink: 1 },
  fresh: { flexDirection: "row", alignItems: "center", gap: 4, flexShrink: 0 },
  freshDot: { width: 6, height: 6, borderRadius: 3, backgroundColor: "#12B76A" },
  freshDotWarn: { backgroundColor: "#F79009" },
  freshText: { fontSize: 11.5, color: HB_COLORS.textSecondary },
  emptyText: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center", paddingVertical: HB_SPACING.md },
  disabled: { alignItems: "center", paddingVertical: HB_SPACING.lg, paddingHorizontal: HB_SPACING.xs },
  disabledIcon: { width: 56, height: 56, borderRadius: 28, backgroundColor: HB_COLORS.surfaceMuted, alignItems: "center", justifyContent: "center", marginBottom: 12 },
  disabledTitle: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary, marginBottom: 6, textAlign: "center" },
  disabledBody: { fontSize: 12.5, lineHeight: 20, color: HB_COLORS.textSecondary, textAlign: "center" },
  retry: { marginTop: 12 },
  staleNotice: { marginBottom: 10 },
});
