import { useCallback, useEffect, useMemo, useRef, useState, type ReactElement } from "react";
import { Pressable, RefreshControl, ScrollView, SectionList, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { LegacyLogCard } from "@/components/legacy-employee-logs/LegacyLogCard";
import { LegacyLogFilterSheet } from "@/components/legacy-employee-logs/LegacyLogFilterSheet";
import { LegacyLogsHeader } from "@/components/legacy-employee-logs/LegacyLogsHeader";
import { LEGACY_UI, RISK } from "@/components/legacy-employee-logs/ui";
import { createProductInsightRequestGate } from "@/modules/product-insights/request-gate";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { fetchLegacyLogs } from "./api";
import {
  LEGACY_CATEGORY_GROUPS,
  LEGACY_DANGER_GROUPS,
  LEGACY_RULE_CODES,
  buildLegacyLogQuery,
  countActiveLegacyFilters,
  createDefaultLegacyLogFilters,
  filtersFromRouteParams,
  filtersToRouteParams,
  groupLegacyLogsByHour,
  sameOperations,
  sumOperationCounts,
  summarizeStores,
  switchLens,
  type LegacyRouteParams,
} from "./logic";
import { subscribeLegacyLogReviewed } from "./review-events";
import type { LegacyLogFilters, LegacyLogItem, LegacyLogPage, LegacyRiskLens } from "./types";

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value) ?? undefined;

/** 入口守卫：审核模式与无权限直接给出说明，不发请求。 */
export function useLegacyLogsGuard() {
  const { t } = useAppTranslation("legacyEmployeeLogs");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const canView = useAuthStore((state) => state.access.canViewLegacyEmployeeLogs);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  if (review || sessionKind === "iosReview") return t("messages.reviewUnavailable");
  if (!isAuthenticated || !canView) return t("messages.notAllowed");
  return null;
}

export function LegacyScreenMessage({ message, onBack }: { message: string; onBack: () => void }) {
  const { t } = useAppTranslation("legacyEmployeeLogs");
  return (
    <SafeAreaView style={styles.message}>
      <Text accessibilityLiveRegion="polite">{message}</Text>
      <Button onPress={onBack}>{t("actions.back")}</Button>
    </SafeAreaView>
  );
}

export function useLegacyRouteParams(): LegacyRouteParams {
  const params = useLocalSearchParams<Record<keyof LegacyRouteParams, string | string[]>>();
  return {
    stores: first(params.stores),
    preset: first(params.preset),
    device: first(params.device),
    employeeId: first(params.employeeId),
    employeeName: first(params.employeeName),
    lens: first(params.lens),
  };
}

export function LegacyEmployeeLogsScreen() {
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useLegacyLogsGuard();
  const routeParams = useLegacyRouteParams();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <LegacyScreenMessage message={blocked} onBack={goBack} />;
  // 账号或深链参数变化时重建页面状态，避免把上一身份 / 上一条件的日志留在屏幕上。
  return <LegacyLogsContent key={`${userGuid}:${JSON.stringify(routeParams)}`} routeParams={routeParams} onBack={goBack} />;
}

function LegacyLogsContent({ routeParams, onBack }: { routeParams: LegacyRouteParams; onBack: () => void }) {
  const { t, language } = useAppTranslation("legacyEmployeeLogs");
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const { stores, selectedStoreCode } = useStores();
  const [filters, setFilters] = useState<LegacyLogFilters>(() => filtersFromRouteParams(routeParams));
  const [page, setPage] = useState<LegacyLogPage | null>(null);
  const [items, setItems] = useState<LegacyLogItem[]>([]);
  const [pageNumber, setPageNumber] = useState(1);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [filterVisible, setFilterVisible] = useState(false);
  const gate = useRef(createProductInsightRequestGate()).current;
  const storeNames = useMemo(() => new Map(stores.map((store) => [store.storeCode, store.storeName || store.storeCode])), [stores]);

  const formatError = useCallback(
    (cause: unknown) => resolveLocalizedErrorMessage(cause, { t, language, fallbackKey: "messages.loadFailed", allowRawMessageInChinese: false }),
    [language, t],
  );

  const load = useCallback(
    async (next: LegacyLogFilters, mode: "initial" | "refresh") => {
      const params = buildLegacyLogQuery(next, 1);
      if (!params) return;
      const lease = gate.begin();
      if (mode === "refresh") setRefreshing(true);
      else setLoading(true);
      setError(null);
      try {
        const result = await fetchLegacyLogs(params, lease.signal);
        if (!lease.isCurrent()) return;
        setPage(result);
        setItems(result.items);
        setPageNumber(1);
      } catch (cause) {
        if (!lease.isCurrent()) return;
        setError(formatError(cause));
        setItems([]);
      } finally {
        if (lease.isCurrent()) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [formatError, gate],
  );

  const total = page?.total ?? 0;
  const loadMore = useCallback(async () => {
    if (loading || loadingMore || items.length >= total) return;
    const params = buildLegacyLogQuery(filters, pageNumber + 1);
    if (!params) return;
    const lease = gate.begin();
    setLoadingMore(true);
    try {
      const result = await fetchLegacyLogs(params, lease.signal);
      if (!lease.isCurrent()) return;
      // 翻页期间可能有新日志上传造成重复，按编号去重。
      setItems((current) => {
        const seen = new Set(current.map((item) => item.id));
        return [...current, ...result.items.filter((item) => !seen.has(item.id))];
      });
      setPageNumber((current) => current + 1);
    } catch (cause) {
      if (lease.isCurrent()) setError(formatError(cause));
    } finally {
      if (lease.isCurrent()) setLoadingMore(false);
    }
  }, [filters, formatError, gate, items.length, loading, loadingMore, pageNumber, total]);

  const apply = useCallback(
    (next: LegacyLogFilters) => {
      setFilters(next);
      setFilterVisible(false);
      void load(next, "initial");
    },
    [load],
  );

  // 首次进入：深链带了分店就直接查；否则等分店列表就绪，用当前所选分店（没有就取第一家）。
  const started = useRef(false);
  useEffect(() => {
    if (started.current) return;
    if (filters.storeCodes.length > 0) {
      started.current = true;
      void load(filters, "initial");
      return;
    }
    const fallback = selectedStoreCode || stores[0]?.storeCode;
    if (!fallback) return;
    started.current = true;
    apply({ ...filters, storeCodes: [fallback] });
  }, [apply, filters, load, selectedStoreCode, stores]);

  // 详情页核查后返回，列表要反映最新结论与待核查数。
  const latest = useRef({ filters, load });
  latest.current = { filters, load };
  useEffect(() => subscribeLegacyLogReviewed(() => void latest.current.load(latest.current.filters, "refresh")), []);
  useEffect(() => () => gate.cancel(), [gate]);

  const sections = useMemo(() => groupLegacyLogsByHour(items), [items]);
  const counts = page?.operationCounts ?? [];
  const summary = page?.riskSummary;
  const allTotal = counts.reduce((sum, row) => sum + row.count, 0);
  const storeSummary = summarizeStores(filters.storeCodes, storeNames);
  const scopeLabel = `${t(`presets.${filters.preset}`)} · ${t(storeSummary.key, storeSummary.params)}${filters.employeeName ? ` · ${filters.employeeName}` : ""}`;

  const lensTile = (lens: LegacyRiskLens, value: number, color: string, background: string, icon?: string, extra?: string) => {
    const active = filters.lens === lens;
    return (
      <Pressable
        key={lens}
        accessibilityRole="button"
        accessibilityState={{ selected: active }}
        onPress={() => apply(switchLens(filters, lens))}
        style={[styles.lens, active ? { borderColor: color, backgroundColor: background, borderWidth: 2 } : null]}
      >
        <View style={styles.lensHead}>
          {icon ? <Icon source={icon} size={13} color={color} /> : null}
          <Text style={[styles.lensLabel, lens !== "all" ? { color, fontWeight: "600" } : null]}>{t(`lens.${lens}`)}</Text>
        </View>
        <View style={styles.lensValueRow}>
          <Text style={[styles.lensValue, LEGACY_UI.mono, lens !== "all" ? { color } : null]}>{value.toLocaleString("en-US")}</Text>
          {extra ? <Text style={[styles.lensExtra, { color }]} numberOfLines={1}>{extra}</Text> : null}
        </View>
      </Pressable>
    );
  };

  const chip = (key: string, label: string, count: number, active: boolean, onPress: () => void) => (
    <Pressable key={key} accessibilityRole="button" accessibilityState={{ selected: active }} onPress={onPress} style={[LEGACY_UI.chip, active ? LEGACY_UI.chipOn : null]}>
      <Text style={[LEGACY_UI.chipText, active ? LEGACY_UI.chipTextOn : null]}>{label}</Text>
      <Text style={[LEGACY_UI.chipCount, active ? LEGACY_UI.chipCountOn : null]}>{count.toLocaleString("en-US")}</Text>
    </Pressable>
  );

  let chips: ReactElement[];
  if (filters.lens === "abnormal") {
    const byRule = new Map((summary?.abnormalByRule ?? []).map((row) => [row.ruleCode, row.count]));
    const pendingOn = filters.reviewStatus === "pending";
    chips = [
      chip("pending", t("lens.onlyPending"), summary?.pendingReview ?? 0, pendingOn, () => apply({ ...filters, reviewStatus: pendingOn ? "all" : "pending" })),
      ...LEGACY_RULE_CODES.map((code) => {
        const active = filters.ruleCode === code;
        return chip(code, t(`rules.${code}.label`), byRule.get(code) ?? 0, active, () => apply({ ...filters, ruleCode: active ? null : code }));
      }),
    ];
  } else {
    const groups = filters.lens === "danger" ? LEGACY_DANGER_GROUPS : LEGACY_CATEGORY_GROUPS;
    const prefix = filters.lens === "danger" ? "dangerGroups" : "categories";
    chips = groups.map((group) => {
      const active = sameOperations(group.operations, filters.subOperations);
      return chip(group.key, t(`${prefix}.${group.key}`), sumOperationCounts(counts, group.operations), active, () =>
        apply({ ...filters, subOperations: active ? [] : [...group.operations] }),
      );
    });
  }

  const footer = (
    <View style={[styles.footer, { paddingBottom: HB_SPACING.lg + insets.bottom }]}>
      {loadingMore ? (
        <ActivityIndicator size="small" color={HB_COLORS.brand} />
      ) : items.length > 0 ? (
        <Text style={styles.footerText}>{items.length < total ? t("list.loadedOf", { shown: items.length, total }) : t("list.end", { total })}</Text>
      ) : null}
    </View>
  );

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <LegacyLogsHeader
        title={t("title")}
        activeTab="records"
        recordsLabel={t("tabs.records")}
        employeesLabel={t("tabs.employees")}
        scopeLabel={scopeLabel}
        filterCount={countActiveLegacyFilters(filters)}
        backLabel={t("actions.back")}
        filterLabel={t("actions.openFilters")}
        onBack={onBack}
        onOpenFilters={() => setFilterVisible(true)}
        onSwitchTab={() => router.replace({ pathname: "/(shell)/legacy-employee-logs/employees", params: { ...filtersToRouteParams(filters) } })}
      />

      <View style={styles.lensRow}>
        {lensTile("all", allTotal, HB_COLORS.action, "#F5F9FF")}
        {lensTile("danger", summary?.dangerTotal ?? 0, RISK.danger, "#FFF5F1", "alert-octagon-outline")}
        {lensTile(
          "abnormal",
          summary?.abnormalTotal ?? 0,
          "#8A5800",
          "#FFFAEB",
          "pulse",
          summary?.pendingReview ? t("lens.pending", { count: summary.pendingReview }) : undefined,
        )}
      </View>

      <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.chipScroll} contentContainerStyle={styles.chipRow} keyboardShouldPersistTaps="handled">
        {chips}
      </ScrollView>

      <Text style={styles.caption}>{t("caption", { lens: t(`lens.${filters.lens}`), count: total.toLocaleString("en-US") })}</Text>

      {filters.storeCodes.length === 0 ? (
        <View style={styles.state}>
          <EmptyState title={t("states.selectStore")} actionLabel={t("actions.openFilters")} onAction={() => setFilterVisible(true)} />
        </View>
      ) : loading ? (
        <View style={styles.state}>
          <ActivityIndicator color={HB_COLORS.brand} />
          <Text style={styles.stateText}>{t("states.loading")}</Text>
        </View>
      ) : error ? (
        <View style={styles.state}>
          <EmptyState title={t("states.loadFailed")} description={error} actionLabel={t("actions.retry")} onAction={() => void load(filters, "initial")} />
        </View>
      ) : (
        <SectionList
          sections={sections}
          keyExtractor={(item) => item.id}
          renderItem={({ item }) => (
            <LegacyLogCard
              item={item}
              storeNames={storeNames}
              t={t}
              onPress={(target) => router.push({ pathname: "/(shell)/legacy-employee-logs/detail", params: { id: target.id } })}
            />
          )}
          renderSectionHeader={({ section }) => (
            <Text style={[styles.hour, LEGACY_UI.mono]}>
              {filters.preset === "today" ? "" : `${section.date} `}
              {t("hour", { hour: section.hour })}
            </Text>
          )}
          stickySectionHeadersEnabled={false}
          onEndReachedThreshold={0.4}
          onEndReached={() => void loadMore()}
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(filters, "refresh")} tintColor={HB_COLORS.brand} />}
          ListEmptyComponent={
            <View style={styles.state}>
              <EmptyState title={t("states.emptyTitle")} description={t("states.emptyDescription")} />
            </View>
          }
          ListFooterComponent={footer}
          style={styles.list}
        />
      )}

      <LegacyLogFilterSheet
        visible={filterVisible}
        filters={filters}
        stores={stores}
        employees={page?.employees ?? []}
        devices={page?.devices ?? []}
        onClose={() => setFilterVisible(false)}
        onApply={apply}
        onReset={() => apply(createDefaultLegacyLogFilters(filters.storeCodes))}
      />
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  message: { flex: 1, gap: HB_SPACING.md, padding: HB_SPACING.lg, justifyContent: "center", backgroundColor: HB_COLORS.background },
  lensRow: { flexDirection: "row", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md },
  lens: {
    flex: 1,
    minWidth: 0,
    minHeight: 64,
    justifyContent: "center",
    gap: 2,
    paddingHorizontal: 10,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  lensHead: { flexDirection: "row", alignItems: "center", gap: 4 },
  lensLabel: { fontSize: 12, color: HB_COLORS.textSecondary },
  lensValueRow: { flexDirection: "row", alignItems: "baseline", gap: 4 },
  lensValue: { fontSize: 20, fontWeight: "700", color: HB_COLORS.textPrimary },
  lensExtra: { flexShrink: 1, fontSize: 10, fontWeight: "600" },
  chipScroll: { flexGrow: 0, flexShrink: 0 },
  chipRow: { gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md, paddingVertical: HB_SPACING.xs, alignItems: "center" },
  caption: { paddingHorizontal: HB_SPACING.md, paddingBottom: 4, fontSize: 12, color: HB_COLORS.textSecondary },
  list: { flex: 1 },
  hour: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.xs, paddingBottom: 6, fontSize: 12, color: HB_COLORS.textSecondary },
  footer: { paddingTop: HB_SPACING.sm, alignItems: "center" },
  footerText: { fontSize: 12, color: HB_COLORS.textSecondary },
  state: { paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center", gap: HB_SPACING.sm },
  stateText: { fontSize: 13, color: HB_COLORS.textSecondary },
});
