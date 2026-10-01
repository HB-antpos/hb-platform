import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Icon, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { LegacyLogFilterSheet } from "@/components/legacy-employee-logs/LegacyLogFilterSheet";
import { LegacyLogsHeader } from "@/components/legacy-employee-logs/LegacyLogsHeader";
import { LEGACY_UI, RISK } from "@/components/legacy-employee-logs/ui";
import { createProductInsightRequestGate } from "@/modules/product-insights/request-gate";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { fetchLegacyEmployeeSummary } from "./api";
import { LegacyScreenMessage, useLegacyLogsGuard, useLegacyRouteParams } from "./LegacyEmployeeLogsScreen";
import {
  buildLegacyEmployeeSummaryQuery,
  countActiveLegacyFilters,
  createDefaultLegacyLogFilters,
  dangerRate,
  filtersFromRouteParams,
  filtersToRouteParams,
  orderLegacyEmployees,
  storeDisplayName,
  summarizeStores,
  type LegacyEmployeeSort,
  type LegacyRouteParams,
} from "./logic";
import type { LegacyEmployeeSummary, LegacyEmployeeSummaryResult, LegacyLogFilters } from "./types";

const SORTS: LegacyEmployeeSort[] = ["abnormal", "rate", "amount"];

export function LegacyEmployeeSummaryScreen() {
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useLegacyLogsGuard();
  const routeParams = useLegacyRouteParams();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <LegacyScreenMessage message={blocked} onBack={goBack} />;
  return <SummaryContent key={`${userGuid}:${JSON.stringify(routeParams)}`} routeParams={routeParams} onBack={goBack} />;
}

function SummaryContent({ routeParams, onBack }: { routeParams: LegacyRouteParams; onBack: () => void }) {
  const { t, language } = useAppTranslation("legacyEmployeeLogs");
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const { stores, selectedStoreCode } = useStores();
  const [filters, setFilters] = useState<LegacyLogFilters>(() => filtersFromRouteParams(routeParams));
  const [data, setData] = useState<LegacyEmployeeSummaryResult | null>(null);
  const [sort, setSort] = useState<LegacyEmployeeSort>("abnormal");
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [filterVisible, setFilterVisible] = useState(false);
  const gate = useRef(createProductInsightRequestGate()).current;
  const storeNames = useMemo(() => new Map(stores.map((store) => [store.storeCode, store.storeName || store.storeCode])), [stores]);

  const load = useCallback(
    async (next: LegacyLogFilters, mode: "initial" | "refresh") => {
      const params = buildLegacyEmployeeSummaryQuery(next);
      if (!params) return;
      const lease = gate.begin();
      if (mode === "refresh") setRefreshing(true);
      else setLoading(true);
      setError(null);
      try {
        const result = await fetchLegacyEmployeeSummary(params, lease.signal);
        if (lease.isCurrent()) setData(result);
      } catch (cause) {
        if (!lease.isCurrent()) return;
        setError(resolveLocalizedErrorMessage(cause, { t, language, fallbackKey: "employees.loadFailed", allowRawMessageInChinese: false }));
      } finally {
        if (lease.isCurrent()) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [gate, language, t],
  );

  const apply = useCallback(
    (next: LegacyLogFilters) => {
      setFilters(next);
      setFilterVisible(false);
      void load(next, "initial");
    },
    [load],
  );

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
  useEffect(() => () => gate.cancel(), [gate]);

  const average = data ? dangerRate(data.dangerTotal, data.total) : 0;
  const employees = useMemo(() => orderLegacyEmployees(data?.employees ?? [], sort), [data, sort]);
  const maxDanger = Math.max(1, ...employees.map((row) => row.dangerCount));
  const storeSummary = summarizeStores(filters.storeCodes, storeNames);
  const scopeLabel = `${t(`presets.${filters.preset}`)} · ${t(storeSummary.key, storeSummary.params)}`;

  // 点员工：回到记录页并带上该员工；有异常看异常入口，否则看危险入口。
  const openEmployee = (row: LegacyEmployeeSummary) => {
    if (!row.employeeId) return;
    router.replace({
      pathname: "/(shell)/legacy-employee-logs",
      params: {
        ...filtersToRouteParams(filters, {
          employeeId: row.employeeId,
          employeeName: row.employeeName ?? row.employeeId,
          lens: row.abnormalCount > 0 ? "abnormal" : "danger",
        }),
      },
    });
  };

  const renderItem = ({ item }: { item: LegacyEmployeeSummary }) => {
    const rate = dangerRate(item.dangerCount, item.total);
    const topRules = [...item.abnormalByRule]
      .sort((a, b) => b.count - a.count)
      .slice(0, 2)
      .map((rule) => `${t(`rules.${rule.ruleCode}.label`)} ${rule.count}`)
      .join(" · ");
    return (
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={item.employeeName ?? item.employeeId ?? "-"}
        disabled={!item.employeeId}
        onPress={() => openEmployee(item)}
        style={({ pressed }) => [LEGACY_UI.card, styles.card, pressed ? styles.pressed : null]}
      >
        <View style={styles.row}>
          <Text style={styles.name}>{item.employeeName || item.employeeId || "-"}</Text>
          <Text numberOfLines={1} style={styles.where}>
            {[item.storeCodes.map((code) => storeDisplayName(code, storeNames)).join("、"), item.deviceCodes.join("、")].filter(Boolean).join(" · ")}
          </Text>
          {item.amountImpact > 0 ? <Text style={[styles.amount, LEGACY_UI.mono]}>−{item.amountImpact.toFixed(2)}</Text> : null}
        </View>
        <View style={styles.stats}>
          <Stat label={t("employees.total")} value={item.total.toLocaleString("en-US")} />
          <Stat label={t("employees.danger")} value={String(item.dangerCount)} />
          <Stat label={t("employees.rate")} value={`${(rate * 100).toFixed(1)}%`} hot={rate > average} />
        </View>
        <View style={styles.bar} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
          <View style={[styles.barFill, { width: `${(item.dangerCount / maxDanger) * 100}%` }]} />
        </View>
        {item.abnormalCount > 0 ? (
          <View style={styles.abnormal}>
            <Icon source="pulse" size={13} color={RISK.abnormalIcon} />
            <Text style={styles.abnormalStrong}>{t("employees.abnormalLine", { count: item.abnormalCount, pending: item.pendingReview })}</Text>
            <Text numberOfLines={1} style={styles.abnormalText}>{topRules}</Text>
          </View>
        ) : (
          <Text style={styles.none}>{t("employees.noAbnormal")}</Text>
        )}
      </Pressable>
    );
  };

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <LegacyLogsHeader
        title={t("title")}
        activeTab="employees"
        recordsLabel={t("tabs.records")}
        employeesLabel={t("tabs.employees")}
        scopeLabel={scopeLabel}
        filterCount={countActiveLegacyFilters(filters)}
        backLabel={t("actions.back")}
        filterLabel={t("actions.openFilters")}
        onBack={onBack}
        onOpenFilters={() => setFilterVisible(true)}
        onSwitchTab={() => router.replace({ pathname: "/(shell)/legacy-employee-logs", params: { ...filtersToRouteParams(filters) } })}
      />
      <View style={styles.sortRow}>
        <Text style={styles.sortLabel}>{t("employees.sortLabel")}</Text>
        {SORTS.map((key) => {
          const active = key === sort;
          return (
            <Pressable key={key} accessibilityRole="button" accessibilityState={{ selected: active }} onPress={() => setSort(key)} style={[LEGACY_UI.chip, active ? LEGACY_UI.chipOn : null]}>
              <Text style={[LEGACY_UI.chipText, active ? LEGACY_UI.chipTextOn : null]}>{t(`employees.sort.${key}`)}</Text>
            </Pressable>
          );
        })}
      </View>
      {data ? <Text style={styles.caption}>{t("employees.average", { percent: (average * 100).toFixed(1) })}</Text> : null}

      {loading ? (
        <View style={styles.state}>
          <ActivityIndicator color={HB_COLORS.brand} />
        </View>
      ) : error ? (
        <View style={styles.state}>
          <EmptyState title={t("employees.loadFailed")} description={error} actionLabel={t("actions.retry")} onAction={() => void load(filters, "initial")} />
        </View>
      ) : (
        <FlatList
          data={employees}
          keyExtractor={(item) => item.employeeId ?? `name:${item.employeeName ?? ""}`}
          renderItem={renderItem}
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(filters, "refresh")} tintColor={HB_COLORS.brand} />}
          ListEmptyComponent={
            <View style={styles.state}>
              <EmptyState title={t("employees.empty")} />
            </View>
          }
          ListFooterComponent={
            employees.length > 0 ? <Text style={[styles.note, { paddingBottom: HB_SPACING.lg + insets.bottom }]}>{t("employees.amountNote")}</Text> : null
          }
          style={styles.list}
        />
      )}

      <LegacyLogFilterSheet
        visible={filterVisible}
        filters={filters}
        stores={stores}
        employees={[]}
        devices={[]}
        onClose={() => setFilterVisible(false)}
        onApply={apply}
        onReset={() => apply(createDefaultLegacyLogFilters(filters.storeCodes))}
      />
    </SafeAreaView>
  );
}

function Stat({ label, value, hot }: { label: string; value: string; hot?: boolean }) {
  return (
    <View style={styles.stat}>
      <Text style={styles.statLabel}>{label}</Text>
      <Text style={[styles.statValue, LEGACY_UI.mono, hot ? { color: RISK.danger } : null]}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  sortRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.xs },
  sortLabel: { fontSize: 12, color: HB_COLORS.textSecondary },
  caption: { paddingHorizontal: HB_SPACING.md, paddingBottom: 6, fontSize: 12, color: HB_COLORS.textSecondary },
  list: { flex: 1 },
  card: { marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs, gap: 10 },
  pressed: { opacity: 0.85 },
  row: { flexDirection: "row", alignItems: "baseline", gap: HB_SPACING.xs },
  name: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary },
  where: { flex: 1, fontSize: 12, color: HB_COLORS.textSecondary },
  amount: { fontSize: 14, fontWeight: "700", color: RISK.danger },
  stats: { flexDirection: "row", gap: HB_SPACING.xs },
  stat: { flex: 1, gap: 2 },
  statLabel: { fontSize: 11, color: HB_COLORS.textSecondary },
  statValue: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary },
  bar: { height: 6, borderRadius: 3, backgroundColor: "#EEF0F2", overflow: "hidden" },
  barFill: { height: 6, borderRadius: 3, backgroundColor: RISK.danger },
  abnormal: { flexDirection: "row", alignItems: "center", gap: 6, paddingHorizontal: 8, paddingVertical: 6, borderRadius: 6, backgroundColor: RISK.abnormalBg },
  abnormalStrong: { fontSize: 12, fontWeight: "700", color: RISK.abnormalText },
  abnormalText: { flex: 1, fontSize: 12, color: RISK.abnormalText },
  none: { fontSize: 12, color: HB_COLORS.textSecondary },
  note: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.xs, fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  state: { paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center", gap: HB_SPACING.sm },
});
