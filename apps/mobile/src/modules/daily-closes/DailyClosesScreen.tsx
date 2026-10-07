import { useEffect, useMemo, useState } from "react";
import { Pressable, RefreshControl, SectionList, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { DailyCloseCard } from "@/components/daily-closes/DailyCloseCard";
import { DailyCloseFilterSheet } from "@/components/daily-closes/DailyCloseFilterSheet";
import { DailyCloseStatusTabs } from "@/components/daily-closes/DailyCloseStatusTabs";
import { DailyCloseSummaryCard } from "@/components/daily-closes/DailyCloseSummaryCard";
import { DailyClosesHeader } from "@/components/daily-closes/DailyClosesHeader";
import { AMOUNT_COLORS, DAILY_UI } from "@/components/daily-closes/ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import {
  classifyDifference,
  countActiveDailyCloseFilters,
  createDefaultDailyCloseFilters,
  formatDailyCloseDifference,
  formatDailyCloseRangeDates,
  groupDailyClosesByBusinessDate,
  resolveDailyCloseRange,
  statusTabCounts,
  summarizeDailyCloseStores,
  summarizeDailyCloseTotals,
  todayInSydney,
  type DailyCloseSection,
} from "./logic";
import { DailyCloseScreenMessage, useBusinessDateLabel, useDailyCloseErrorMessage, useDailyClosesGuard } from "./daily-close-shared";
import type { DailyCloseCounts, DailyCloseFilters, DailyCloseListItem, DailyCloseStatusTab } from "./types";
import { useDailyCloseList } from "./use-daily-closes";

export function DailyClosesScreen() {
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useDailyClosesGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <DailyCloseScreenMessage message={blocked} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号的筛选与结果留在屏幕上。
  return <DailyClosesContent key={userGuid} onBack={goBack} />;
}

function DailyClosesContent({ onBack }: { onBack: () => void }) {
  const { t } = useAppTranslation("dailyCloses");
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const { stores } = useStores();
  const [filters, setFilters] = useState<DailyCloseFilters>(createDefaultDailyCloseFilters);
  const [filterVisible, setFilterVisible] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const { query, items, firstPage, refresh, rangeValid } = useDailyCloseList(filters, true);
  const formatError = useDailyCloseErrorMessage("errors.loadFailed");
  const dateLabel = useBusinessDateLabel();
  const storeNames = useMemo(() => new Map(stores.map((store) => [store.storeCode, store.storeName || store.storeCode])), [stores]);

  // 页签计数不受状态页签影响：切换页签时新请求还没回来，先沿用同一组其余筛选下的上一份计数，避免数字闪成「–」。
  const scopeKey = JSON.stringify({ ...filters, status: "all" });
  const [known, setKnown] = useState<{ key: string; counts: DailyCloseCounts } | null>(null);
  useEffect(() => {
    if (firstPage) setKnown({ key: scopeKey, counts: firstPage.counts });
  }, [firstPage, scopeKey]);
  const tabCounts = firstPage?.counts ?? (known?.key === scopeKey ? known.counts : null);

  const hasNextPage = query.hasNextPage;
  const complete = !hasNextPage;
  const sections = useMemo(() => groupDailyClosesByBusinessDate(items, complete), [items, complete]);
  const summary = useMemo(() => (firstPage ? summarizeDailyCloseTotals(firstPage.counts, firstPage.totals, filters.status) : null), [filters.status, firstPage]);
  const total = firstPage?.total;

  const storeSummary = summarizeDailyCloseStores(filters.storeCodes, storeNames);
  const today = todayInSydney();
  const shortRange = formatDailyCloseRangeDates(resolveDailyCloseRange(filters, today), today);
  const rangeLabel = filters.preset === "custom" ? t("scope.customRange", { from: shortRange.from, to: shortRange.to }) : t(`presets.${filters.preset}`);
  const filterCount = countActiveDailyCloseFilters(filters);

  const apply = (next: DailyCloseFilters) => {
    setFilters(next);
    setFilterVisible(false);
  };
  const changeStatus = (status: DailyCloseStatusTab) => setFilters((current) => ({ ...current, status }));
  const resetFilters = () => setFilters(createDefaultDailyCloseFilters());

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await refresh();
    } finally {
      setRefreshing(false);
    }
  };
  const loadMore = () => {
    if (hasNextPage && !query.isFetchingNextPage && !query.isFetchNextPageError) void query.fetchNextPage();
  };

  const footer = (
    <View style={[styles.footer, { paddingBottom: HB_SPACING.lg + insets.bottom }]}>
      {query.isFetchingNextPage ? (
        <ActivityIndicator size="small" color={HB_COLORS.brand} />
      ) : query.isFetchNextPageError ? (
        <Pressable accessibilityRole="button" onPress={() => void query.fetchNextPage()}>
          <Text style={styles.footerError}>{t("states.loadMoreFailed")}</Text>
        </Pressable>
      ) : items.length > 0 && total !== undefined ? (
        <Text style={styles.footerText}>{items.length < total ? t("states.loadedOf", { shown: items.length, total }) : t("states.end", { total })}</Text>
      ) : null}
    </View>
  );

  let body;
  if (!rangeValid) {
    body = (
      <View style={styles.state}>
        <EmptyState title={t("states.rangeInvalid")} actionLabel={t("actions.resetFilters")} onAction={resetFilters} />
      </View>
    );
  } else if (query.isPending) {
    body = (
      <View style={styles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={styles.stateText}>{t("states.loading")}</Text>
      </View>
    );
  } else if (query.isError && !query.data) {
    body = (
      <View style={styles.state}>
        <EmptyState title={t("states.loadFailed")} description={formatError(query.error)} actionLabel={t("actions.retry")} onAction={() => void query.refetch()} />
      </View>
    );
  } else {
    body = (
      <SectionList
        sections={sections}
        keyExtractor={(item) => item.dailyCloseGuid}
        renderItem={({ item }) => <DailyCloseCard item={item} onPress={(target: DailyCloseListItem) => router.push({ pathname: "/(shell)/daily-closes/detail", params: { id: target.dailyCloseGuid } })} />}
        renderSectionHeader={({ section }) => <SectionHeader section={section} dateLabel={dateLabel} />}
        ListHeaderComponent={
          <>
            {query.isRefetchError && !query.isFetchNextPageError ? (
              <Pressable accessibilityRole="button" onPress={() => void query.refetch()} style={styles.refreshError}>
                <Text style={styles.footerError}>{t("states.refreshFailed")}</Text>
              </Pressable>
            ) : null}
            {summary && items.length > 0 ? <DailyCloseSummaryCard summary={summary} loading={query.isFetching && !query.isFetchingNextPage && !refreshing} /> : null}
          </>
        }
        stickySectionHeadersEnabled={false}
        onEndReachedThreshold={0.4}
        onEndReached={loadMore}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void onRefresh()} tintColor={HB_COLORS.brand} />}
        ListEmptyComponent={
          <View style={styles.state}>
            <EmptyState
              title={t("states.emptyTitle")}
              description={t("states.emptyDescription")}
              actionLabel={filterCount > 0 || filters.status !== "all" ? t("actions.resetFilters") : undefined}
              onAction={resetFilters}
            />
          </View>
        }
        ListFooterComponent={footer}
        style={styles.list}
      />
    );
  }

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <DailyClosesHeader
        title={t("title")}
        backLabel={t("actions.back")}
        filterLabel={t("actions.openFilters")}
        filterCount={filterCount}
        storeLabel={t(storeSummary.key, "params" in storeSummary ? storeSummary.params : undefined)}
        rangeLabel={rangeLabel}
        onBack={onBack}
        onOpenFilters={() => setFilterVisible(true)}
      />
      <DailyCloseStatusTabs value={filters.status} counts={tabCounts ? statusTabCounts(tabCounts) : null} onChange={changeStatus} />
      <View style={styles.gap} />
      {body}
      <DailyCloseFilterSheet visible={filterVisible} filters={filters} stores={stores} appliedTotal={total} onClose={() => setFilterVisible(false)} onApply={apply} />
    </SafeAreaView>
  );
}

/** 分组标题：营业日 + 当天份数与差额合计（差额带正负号与颜色）。最后一组还有下一页时只显示已加载份数，不显示不完整的合计。 */
function SectionHeader({ section, dateLabel }: { section: DailyCloseSection; dateLabel: (date: string) => string }) {
  const { t } = useAppTranslation("dailyCloses");
  const showDifference = !section.partial && section.difference !== null;
  const kind = classifyDifference(section.difference);
  return (
    <View style={styles.sectionHeader}>
      <Text style={styles.sectionDate}>{dateLabel(section.businessDate)}</Text>
      <Text style={[styles.sectionMeta, DAILY_UI.mono]}>
        {t(section.partial ? "section.countPartial" : "section.count", { count: section.count })}
        {showDifference ? (
          <>
            {` · ${t("section.difference")} `}
            <Text style={[styles.sectionDifference, { color: AMOUNT_COLORS[kind] }]}>{formatDailyCloseDifference(section.difference)}</Text>
          </>
        ) : null}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  gap: { height: HB_SPACING.xs },
  list: { flex: 1 },
  state: { paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center", gap: HB_SPACING.sm },
  stateText: { fontSize: 13, color: HB_COLORS.textSecondary },
  sectionHeader: {
    flexDirection: "row",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    paddingBottom: 6,
  },
  sectionDate: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  sectionMeta: { flexShrink: 1, fontSize: 12, color: HB_COLORS.textSecondary },
  sectionDifference: { fontWeight: "700" },
  footer: { paddingTop: HB_SPACING.sm, alignItems: "center" },
  footerText: { fontSize: 12, color: HB_COLORS.textSecondary },
  footerError: { fontSize: 13, color: HB_COLORS.action, paddingVertical: HB_SPACING.xs },
  refreshError: { alignItems: "center" },
});
