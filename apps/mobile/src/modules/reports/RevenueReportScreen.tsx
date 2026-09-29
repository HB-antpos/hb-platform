import { useCallback, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useFocusEffect } from "@react-navigation/native";
import {
  FlatList,
  InteractionManager,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  View,
  type StyleProp,
  type ViewToken,
  type ViewStyle,
} from "react-native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Button,
  Icon,
  IconButton,
  Modal,
  Portal,
  SegmentedButtons,
  Text,
  TextInput,
} from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { MonthDatePickerField } from "@/components/attendance/MonthDatePicker";
import { fetchProductReportStoreOptions } from "@/modules/product-report/api";
import {
  BranchRevenueRow,
  DailyRevenueRow,
  HourlyRevenueRow,
  type BranchHourlyRevenueRow,
  type ExecutiveBranchPerformanceSnapshot,
  type RevenueDetailSnapshot,
  fetchBranchDailyPerformance,
  fetchExecutiveBranchPerformance,
  fetchExecutiveHourlyTraffic,
  fetchExecutiveHourlyTrafficByBranch,
} from "@/modules/reports/api";
import { CumulativeRevenueCard } from "@/modules/reports/CumulativeRevenueCard";
import { CumulativeRevenueChart } from "@/modules/reports/CumulativeRevenueChart";
import {
  FULL_DAY_CUTOFF_HOUR,
  alignBranchRowsToCutoff,
  buildCumulativeChartModel,
  buildHourlyDetailRows,
  buildHourlySeries,
  formatHourLabel,
  formatLocalClockTime,
  getCumulativeTotals,
  getDisplayCutoffHour,
  groupHourlySeriesByBranch,
  resolveDefaultCutoff,
  resolveEffectiveCutoff,
  type HourlyDetailView,
} from "@/modules/reports/hourly-cumulative";
import { useStatisticsFreshnessQuery } from "@/modules/reports/statistics-freshness";
import {
  RevenuePeriod,
  RevenuePeriodMode,
  getCompareRevenuePeriod,
  getDefaultRevenuePeriod,
  getLastMonthRevenuePeriod,
  getLastWeekRevenuePeriod,
  getNextRevenuePeriod,
  parseDateKey,
  getPreviousRevenuePeriod,
  getYesterdayRevenuePeriod,
  getRevenueDateBounds,
  getRevenuePeriodForDate,
  isRevenuePeriodAvailable,
  refreshRevenueDateSelection,
} from "@/modules/reports/periods";
import { formatMoney, formatWholeMoney } from "@/modules/reports/format";
import { GROWTH_COLORS, formatGrowthRate, getGrowthTone } from "@/modules/reports/growth-rate";
import { REPORT_QUERY_OPTIONS } from "@/modules/reports/report-config";
import {
  ReportLoadPerformanceTimer,
  ReportLoadVisibilityGate,
  discardReportNavigationStart,
  hasUsableSuccessfulReportCache,
  recordReportLoadPerformance,
  type ReportLoadCacheState,
  type ReportLoadPerformanceMeasurement,
} from "@/modules/reports/report-load-performance";
import {
  createReportSnapshotKey,
  formatReportSnapshotTime,
  getCompleteReportSnapshot,
  getReportSnapshotDisplay,
  isReportScopeValid,
  saveCompleteReportSnapshot,
  type CompleteReportSnapshot,
} from "@/modules/reports/report-snapshot";
import {
  getCashierEnabledStoreCodes,
  getCashierScopedBranchCodes,
} from "@/modules/reports/cashier-enabled-store-scope";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";

type Drilldown =
  | { type: "hourly"; branch: BranchRevenueRow }
  | { type: "daily"; branch: BranchRevenueRow };

type DetailRow = HourlyRevenueRow | DailyRevenueRow;

type HourlyDetailMode = "cumulative" | "perHour";

// 空明细用稳定引用，避免下游 useMemo 每次渲染都重算。
const EMPTY_DETAIL_ROWS: DetailRow[] = [];

function isHourlyDetailView(row: DetailRow): row is HourlyDetailView {
  return "status" in row;
}

interface RevenueReportScreenProps {
  embedded?: boolean;
  onRefreshFreshness?: () => Promise<unknown>;
  onRefreshReport?: () => Promise<unknown>;
  freshnessLabel?: string;
  reportNavigationActionId?: number | null;
}

interface RevenueSummary {
  revenue: number;
  compareRevenue: number;
  transactions: number;
  compareTransactions: number;
  averageTransaction: number;
  compareAverageTransaction: number;
}

type SummaryListItem =
  | { type: "table-header" }
  | { type: "branch"; row: BranchRevenueRow; rank: number };

function buildQuery(period: RevenuePeriod, branchCodes: readonly string[]) {
  const compareMode =
    period.mode === "day"
      ? "lastYearSameWeekday"
      : period.mode === "week"
        ? "lastYearIsoWeek"
        : "lastYearSameMonth";
  const comparePeriod = getCompareRevenuePeriod(period, compareMode);
  return {
    startDate: period.startDate,
    endDate: period.endDate,
    compareStartDate: comparePeriod.startDate,
    compareEndDate: comparePeriod.endDate,
    compareMode: period.mode === "month" ? "ByDate" as const : "ByWeek" as const,
    branchCodes: [...branchCodes],
  };
}

function getPeriodLabel(period: RevenuePeriod) {
  return period.startDate === period.endDate
    ? period.startDate
    : `${period.startDate} - ${period.endDate}`;
}

function isDailyRow(row: DetailRow): row is DailyRevenueRow {
  return "date" in row;
}

function getDetailDateParts(value: string) {
  try {
    const date = parseDateKey(value);
    return { dateLabel: value, weekday: date.getDay() };
  } catch {
    return { dateLabel: value, weekday: null };
  }
}

function formatCount(value: number) {
  return Math.round(value).toLocaleString("en-AU");
}

function formatOptionalWholeMoney(value: number | null | undefined) {
  return value == null ? "—" : formatWholeMoney(value);
}

function buildRevenueSummary(rows: BranchRevenueRow[]): RevenueSummary | null {
  if (rows.length === 0) {
    return null;
  }

  const totals = rows.reduce(
    (sum, row) => ({
      revenue: sum.revenue + row.revenue,
      compareRevenue: sum.compareRevenue + row.compareRevenue,
      transactions: sum.transactions + row.transactions,
      compareTransactions: sum.compareTransactions + row.compareTransactions,
    }),
    { revenue: 0, compareRevenue: 0, transactions: 0, compareTransactions: 0 }
  );

  return {
    ...totals,
    // 汇总客单价必须用汇总营业额/客单数计算，不能平均各分店客单价。
    averageTransaction: totals.transactions > 0 ? totals.revenue / totals.transactions : 0,
    compareAverageTransaction:
      totals.compareTransactions > 0 ? totals.compareRevenue / totals.compareTransactions : 0,
  };
}

function formatOrdinal(index: number) {
  return String(index + 1).padStart(2, "0");
}

function TableText({
  children,
  style,
  numeric,
  noTruncate = false,
  lines = 1,
}: {
  children: string;
  style?: object;
  numeric?: boolean;
  noTruncate?: boolean;
  lines?: number;
}) {
  return (
    <Text
      variant="bodySmall"
      numberOfLines={noTruncate || numeric ? undefined : lines}
      selectable
      style={[styles.tableCellText, numeric ? styles.numericText : null, style]}
    >
      {children}
    </Text>
  );
}

function DetailPeriodText({
  item,
  getWeekdayLabel,
}: {
  item: DetailRow;
  getWeekdayLabel: (weekday: number) => string;
}) {
  if (!isDailyRow(item)) {
    return <TableText style={styles.strongText}>{item.label}</TableText>;
  }

  const label = getDetailDateParts(item.date);
  return (
    <Text
      variant="bodySmall"
      numberOfLines={2}
      selectable
      style={[styles.tableCellText, styles.strongText]}
    >
      {label.dateLabel}
      {label.weekday !== null ? (
        <Text style={styles.weekdayText}>{`\n${getWeekdayLabel(label.weekday)}`}</Text>
      ) : null}
    </Text>
  );
}

function RevenueSummaryCard({
  title,
  caption,
  summary,
}: {
  title: string;
  caption?: string;
  summary: RevenueSummary | null;
}) {
  const { t } = useAppTranslation("common");
  const metrics = [
    {
      key: "revenue",
      label: t("reports.metrics.revenue"),
      current: summary?.revenue,
      compare: summary?.compareRevenue,
      format: formatOptionalWholeMoney,
    },
    {
      key: "transactions",
      label: t("reports.metrics.transactions"),
      current: summary?.transactions,
      compare: summary?.compareTransactions,
      format: (value: number | null | undefined) =>
        value == null ? "—" : formatCount(value),
    },
    {
      key: "averageTransaction",
      label: t("reports.metrics.averageTransaction"),
      current: summary?.averageTransaction,
      compare: summary?.compareAverageTransaction,
      format: (value: number | null | undefined) =>
        value == null ? "—" : formatMoney(value),
    },
  ] as const;
  const growthNewLabel = t("reports.metrics.newGrowth");

  return (
    <View style={styles.summaryCard}>
      <View style={styles.cardTitleRow}>
        <Text variant="titleMedium" style={styles.sectionTitle}>
          {title}
        </Text>
        {caption ? (
          <Text variant="bodySmall" style={styles.muted}>
            {caption}
          </Text>
        ) : null}
      </View>
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator
        contentContainerStyle={styles.summaryGridContent}
      >
        <View style={styles.summaryGrid}>
        <View style={styles.summaryRow}>
          <View style={styles.summaryLabelColumn} />
          {metrics.map((metric) => (
            <View key={metric.key} style={styles.summaryMetricColumn}>
              <TableText numeric style={styles.headerText}>{metric.label}</TableText>
            </View>
          ))}
        </View>
        <View style={styles.summaryRow}>
          <View style={styles.summaryLabelColumn}>
            <TableText style={styles.strongText}>{t("reports.metrics.current")}</TableText>
          </View>
          {metrics.map((metric) => (
            <View key={metric.key} style={styles.summaryMetricColumn}>
              <TableText numeric style={styles.summaryValue}>{metric.format(metric.current)}</TableText>
            </View>
          ))}
        </View>
        <View style={styles.summaryRow}>
          <View style={styles.summaryLabelColumn}>
            <TableText style={styles.muted}>{t("reports.metrics.compare")}</TableText>
          </View>
          {metrics.map((metric) => (
            <View key={metric.key} style={styles.summaryMetricColumn}>
              <TableText numeric style={styles.muted}>{metric.format(metric.compare)}</TableText>
            </View>
          ))}
        </View>
        <View style={[styles.summaryRow, styles.summaryLastRow]}>
          <View style={styles.summaryLabelColumn}>
            <TableText style={styles.positiveText}>{t("reports.metrics.growthRate")}</TableText>
          </View>
          {metrics.map((metric) => {
            if (metric.current == null || metric.compare == null) {
              return (
                <View key={metric.key} style={styles.summaryMetricColumn}>
                  <TableText numeric style={styles.muted}>—</TableText>
                </View>
              );
            }
            const tone = getGrowthTone(metric.current, metric.compare);
            return (
              <View key={metric.key} style={styles.summaryMetricColumn}>
                <TableText numeric style={[styles.strongText, { color: GROWTH_COLORS[tone] }]}>
                  {formatGrowthRate(metric.current, metric.compare, growthNewLabel)}
                </TableText>
              </View>
            );
          })}
        </View>
        </View>
      </ScrollView>
    </View>
  );
}

export function RevenueReportScreen({
  embedded = false,
  onRefreshFreshness,
  onRefreshReport,
  freshnessLabel,
  reportNavigationActionId = null,
}: RevenueReportScreenProps) {
  const { t } = useAppTranslation("common");
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const accountIdentity = useAuthStore((state) => state.isAuthenticated
    ? state.user?.userGuid || state.user?.userGUID || ""
    : "");
  const revenueLoadTimer = useRef(new ReportLoadPerformanceTimer()).current;
  const revenueLoadQueryKeyRef = useRef<readonly unknown[] | null>(null);
  const revenueRequestGenerationRef = useRef(0);
  const revenueBranchVisibleRef = useRef(false);
  const detailLoadGate = useRef(new ReportLoadVisibilityGate()).current;
  const detailLoadQueryKeyRef = useRef<readonly unknown[] | null>(null);
  const detailRequestGenerationRef = useRef(0);
  const detailLoadTypeRef = useRef<Drilldown["type"] | null>(null);
  const detailPhysicalRowVisibleRef = useRef(false);
  const detailPhysicalPresentationReadyRef = useRef(false);
  const completeSummarySnapshotsRef = useRef(
    new Map<string, CompleteReportSnapshot<BranchRevenueRow[]>>(),
  ).current;
  const completeDetailSnapshotsRef = useRef(
    new Map<string, CompleteReportSnapshot<DetailRow[]>>(),
  ).current;
  // 日报累计对比的逐店小时数据：刷新期间沿用同条件下最后一份完整数据，避免排行闪回旧口径。
  const completeCumulativeSnapshotsRef = useRef(
    new Map<string, BranchHourlyRevenueRow[]>(),
  ).current;
  const previousRevenueScopeCodesRef = useRef("");
  const summaryViewabilityConfig = useRef({
    itemVisiblePercentThreshold: 50,
    minimumViewTime: 16,
  }).current;
  const detailViewabilityConfig = useRef({
    itemVisiblePercentThreshold: 50,
    minimumViewTime: 16,
  }).current;
  const [mode, setMode] = useState<RevenuePeriodMode>("day");
  const [period, setPeriod] = useState(() => getDefaultRevenuePeriod("day"));
  const [selectedDate, setSelectedDate] = useState(() => getDefaultRevenuePeriod("day").startDate);
  const [selectedBranchCode, setSelectedBranchCode] = useState<string | null>(null);
  const [branchPickerVisible, setBranchPickerVisible] = useState(false);
  const [branchPickerSearch, setBranchPickerSearch] = useState("");
  const [branchSearchVisible, setBranchSearchVisible] = useState(false);
  const [branchSearch, setBranchSearch] = useState("");
  const [drilldown, setDrilldown] = useState<Drilldown | null>(null);
  // 用户点选的截止整点；null 表示跟随默认（当天最近完整整点 / 历史日期整天）。
  const [selectedCutoffHour, setSelectedCutoffHour] = useState<number | null>(null);
  const [hourlyDetailMode, setHourlyDetailMode] = useState<HourlyDetailMode>("cumulative");
  const [dateBounds, setDateBounds] = useState(() => getRevenueDateBounds());
  useFocusEffect(
    useCallback(() => {
      const refreshed = refreshRevenueDateSelection(selectedDate);
      setDateBounds(refreshed.bounds);
      if (refreshed.selectedDate !== selectedDate) {
        setSelectedDate(refreshed.selectedDate);
        setPeriod(getRevenuePeriodForDate(mode, refreshed.selectedDate));
        setDrilldown(null);
        setSelectedCutoffHour(null);
      }
    }, [mode, selectedDate]),
  );
  const statisticsFreshnessQuery = useStatisticsFreshnessQuery();
  const statisticsCompletedAtUtc = statisticsFreshnessQuery.data?.lastSuccessfulAtUtc ?? null;

  const cashierStoreOptionsQuery = useQuery({
    queryKey: ["reports", "cashier-enabled-stores", accountIdentity],
    queryFn: ({ signal }) => fetchProductReportStoreOptions({ signal }),
    ...REPORT_QUERY_OPTIONS,
    enabled: Boolean(accountIdentity),
  });
  const cashierEnabledStoreCodes = useMemo(
    () => getCashierEnabledStoreCodes(cashierStoreOptionsQuery.data ?? []),
    [cashierStoreOptionsQuery.data],
  );
  const cashierStoreScopeVersion = cashierStoreOptionsQuery.dataUpdatedAt;
  const reportScopeValid = isReportScopeValid(accountIdentity, cashierStoreOptionsQuery, cashierEnabledStoreCodes);
  const revenuePeriodAvailable = isRevenuePeriodAvailable(period, dateBounds);
  const cashierStoreScopeEmpty =
    cashierStoreOptionsQuery.isSuccess && cashierEnabledStoreCodes.length === 0;
  const summaryQueryEnabled =
    reportScopeValid
    && revenuePeriodAvailable
    && cashierStoreOptionsQuery.isSuccess
    && !cashierStoreOptionsQuery.isFetching
    && cashierEnabledStoreCodes.length > 0;
  const summaryQueryTerminallyBlocked =
    !revenuePeriodAvailable
    || cashierStoreOptionsQuery.isError
    || cashierStoreScopeEmpty;
  useLayoutEffect(() => {
    if (reportNavigationActionId === null || !summaryQueryTerminallyBlocked) return;
    discardReportNavigationStart("revenue", reportNavigationActionId);
  }, [reportNavigationActionId, summaryQueryTerminallyBlocked]);

  const startRevenueLoad = useCallback((cacheState: ReportLoadCacheState) => {
    revenueLoadTimer.start(cacheState, "revenue");
  }, [revenueLoadTimer]);
  const queryParams = useMemo(
    () => buildQuery(period, cashierEnabledStoreCodes),
    [cashierEnabledStoreCodes, period],
  );
  const summaryQueryKey = useMemo(
    () => ["reports", "revenue-summary", accountIdentity, cashierStoreScopeVersion, queryParams] as const,
    [accountIdentity, cashierStoreScopeVersion, queryParams],
  );
  const summarySnapshotKey = useMemo(
    () => createReportSnapshotKey({
      accountIdentity,
      tab: "revenue",
      period: period.mode,
      startDate: queryParams.startDate,
      endDate: queryParams.endDate,
      compareStartDate: queryParams.compareStartDate,
      compareEndDate: queryParams.compareEndDate,
      compareMode: queryParams.compareMode,
      branchCodes: queryParams.branchCodes ?? [],
    }),
    [accountIdentity, period.mode, queryParams],
  );
  const summaryQuery = useQuery({
    queryKey: summaryQueryKey,
    queryFn: async ({ signal }) => {
      const requestGeneration = revenueRequestGenerationRef.current + 1;
      revenueRequestGenerationRef.current = requestGeneration;
      const isSameQuery = revenueLoadQueryKeyRef.current === summaryQueryKey;
      if (!isSameQuery) {
        revenueBranchVisibleRef.current = false;
      }
      revenueLoadQueryKeyRef.current = summaryQueryKey;
      // 每个真实查询只启动一次会话；API 内部补算轮询不会重入这里。
      const cachedSummary = queryClient.getQueryData<ExecutiveBranchPerformanceSnapshot>(summaryQueryKey);
      startRevenueLoad(
        hasUsableSuccessfulReportCache(
          queryClient.getQueryState(summaryQueryKey)?.status,
          cachedSummary,
          (cachedSummary) => cachedSummary.isComplete && cachedSummary.rows.length > 0,
        ) ? "warm" : "cold",
      );
      try {
        const result = await fetchExecutiveBranchPerformance(queryParams, { signal });
        if (
          signal.aborted
          || revenueRequestGenerationRef.current !== requestGeneration
          || revenueLoadQueryKeyRef.current !== summaryQueryKey
        ) {
          const abortError = new Error("Stale revenue report request");
          abortError.name = "AbortError";
          throw abortError;
        }
        return result;
      } catch (error) {
        if (
          revenueRequestGenerationRef.current === requestGeneration
          && revenueLoadQueryKeyRef.current === summaryQueryKey
        ) {
          revenueLoadTimer.fail();
        }
        throw error;
      }
    },
    enabled: summaryQueryEnabled,
    ...REPORT_QUERY_OPTIONS,
  });
  useLayoutEffect(() => {
    const scopeFingerprint = JSON.stringify([accountIdentity, cashierEnabledStoreCodes]);
    if (previousRevenueScopeCodesRef.current !== ""
      && previousRevenueScopeCodesRef.current !== scopeFingerprint) {
      // 收银授权范围变化或身份切换后，旧分店数据不可继续下钻或显示。
      completeSummarySnapshotsRef.clear();
      completeDetailSnapshotsRef.clear();
      completeCumulativeSnapshotsRef.clear();
      setDrilldown(null);
    }
    previousRevenueScopeCodesRef.current = scopeFingerprint;
    if (!reportScopeValid) {
      completeSummarySnapshotsRef.clear();
      completeDetailSnapshotsRef.clear();
      completeCumulativeSnapshotsRef.clear();
      setDrilldown(null);
    }
  }, [
    accountIdentity,
    reportScopeValid,
    cashierStoreOptionsQuery.isError,
    cashierStoreOptionsQuery.isSuccess,
    cashierEnabledStoreCodes,
    cashierStoreScopeVersion,
    completeCumulativeSnapshotsRef,
    completeDetailSnapshotsRef,
    completeSummarySnapshotsRef,
  ]);
  useLayoutEffect(() => {
    if (revenueLoadQueryKeyRef.current !== summaryQueryKey) {
      revenueLoadQueryKeyRef.current = summaryQueryKey;
      revenueBranchVisibleRef.current = false;
    }
    return () => {
      if (revenueLoadQueryKeyRef.current !== summaryQueryKey) return;
      revenueRequestGenerationRef.current += 1;
      revenueLoadTimer.cancel();
      revenueLoadQueryKeyRef.current = null;
      revenueBranchVisibleRef.current = false;
    };
  }, [revenueLoadTimer, summaryQueryKey]);

  useLayoutEffect(() => {
    if (cashierStoreOptionsQuery.isFetching) {
      // 白名单重验期间隐藏旧范围交互；保留下钻意图，成功后仅在分店仍有效时恢复。
      setBranchPickerVisible(false);
      return;
    }
    if (cashierStoreOptionsQuery.isError) {
      // 白名单不可用时关闭下钻，避免继续展示或重试上一次范围的缓存明细。
      setDrilldown(null);
      return;
    }
    if (!cashierStoreOptionsQuery.isSuccess) return;

    if (
      selectedBranchCode
      && getCashierScopedBranchCodes(cashierEnabledStoreCodes, selectedBranchCode).length === 0
    ) {
      setSelectedBranchCode(null);
    }
    if (
      drilldown
      && getCashierScopedBranchCodes(
        cashierEnabledStoreCodes,
        drilldown.branch.branchCode,
      ).length === 0
    ) {
      setDrilldown(null);
    }
    if (cashierEnabledStoreCodes.length === 0) {
      setBranchPickerVisible(false);
    }
  }, [
    cashierEnabledStoreCodes,
    cashierStoreOptionsQuery.isError,
    cashierStoreOptionsQuery.isFetching,
    cashierStoreOptionsQuery.isSuccess,
    drilldown,
    selectedBranchCode,
  ]);

  const completeRevenueLoad = useCallback(() => {
    if (!revenueBranchVisibleRef.current) return;
    const measurement = revenueLoadTimer.markFirstRowVisible();
    if (!measurement) return;
    recordReportLoadPerformance("revenue", measurement);
  }, [revenueLoadTimer]);

  const onSummaryViewableItemsChanged = useRef(
    ({ viewableItems }: { viewableItems: ViewToken<SummaryListItem>[] }) => {
      const hasVisibleBranch = viewableItems.some(
        (token) => token.isViewable && token.item.type === "branch",
      );
      revenueBranchVisibleRef.current = hasVisibleBranch;
      if (!hasVisibleBranch) return;
      completeRevenueLoad();
    },
  ).current;

  useLayoutEffect(() => {
    if (!reportScopeValid || summaryQuery.isError || summaryQuery.data === undefined) return;
    // React Query 在重新请求时会暂留上一份 data；它不能提前结束本次新请求的计时。
    if (summaryQuery.isFetching) return;
    if (!summaryQuery.data.isComplete) {
      // 有界追数仍未完成时明确结束本次样本，避免性能会话悬挂或把部分快照记为成功。
      revenueLoadTimer.fail();
      return;
    }
    // 三组营业额视图共用同一份完整快照；刷新中的半包络不会覆盖它。
    if (!summaryQuery.isFetching) {
      saveCompleteReportSnapshot(
        completeSummarySnapshotsRef,
        summarySnapshotKey,
        summaryQuery.data.rows,
        {
          statisticUpdatedAt: summaryQuery.data.statisticUpdatedAt,
          cacheVersion: summaryQuery.data.cacheVersion,
        },
      );
    }
    revenueLoadTimer.markDataNormalized();
    if (summaryQuery.data.rows.length === 0) {
      // 最终完整空快照没有“首条业务数据”，结束会话但不伪造成功事件。
      revenueLoadTimer.cancel();
      return;
    }
    completeRevenueLoad();
  }, [
    reportScopeValid,
    completeRevenueLoad,
    completeSummarySnapshotsRef,
    revenueLoadTimer,
    summaryQuery.data,
    summaryQuery.dataUpdatedAt,
    summaryQuery.isFetching,
    summaryQuery.isError,
    summarySnapshotKey,
    summaryQueryKey,
  ]);

  const detailBranchCodes = useMemo(
    () => getCashierScopedBranchCodes(
      cashierEnabledStoreCodes,
      drilldown?.branch.branchCode,
    ),
    [cashierEnabledStoreCodes, drilldown?.branch.branchCode],
  );
  const detailParams = useMemo(
    () => buildQuery(period, detailBranchCodes),
    [detailBranchCodes, period]
  );
  const detailQueryEnabled =
    reportScopeValid
    && Boolean(drilldown)
    && cashierStoreOptionsQuery.isSuccess
    && !cashierStoreOptionsQuery.isFetching
    && detailBranchCodes.length > 0;
  const detailQueryKey = useMemo(
    () => ["reports", drilldown?.type, accountIdentity, cashierStoreScopeVersion, detailParams] as const,
    [accountIdentity, cashierStoreScopeVersion, detailParams, drilldown?.type],
  );
  const detailSnapshotKey = useMemo(
    () => createReportSnapshotKey({
      accountIdentity,
      tab: "revenue",
      detail: drilldown?.type ?? "",
      period: period.mode,
      startDate: detailParams.startDate,
      endDate: detailParams.endDate,
      compareStartDate: detailParams.compareStartDate,
      compareEndDate: detailParams.compareEndDate,
      compareMode: detailParams.compareMode,
      branchCodes: detailParams.branchCodes ?? [],
    }),
    [accountIdentity, detailParams, drilldown?.type, period.mode],
  );
  const detailQuery = useQuery<RevenueDetailSnapshot<DetailRow>>({
    queryKey: detailQueryKey,
    queryFn: async ({ signal }) => {
      const detailType = drilldown?.type;
      if (!detailType) {
        throw new Error("Revenue detail query requires an active drilldown");
      }
      const requestGeneration = detailRequestGenerationRef.current + 1;
      detailRequestGenerationRef.current = requestGeneration;
      const isSameDetailQuery = detailLoadQueryKeyRef.current === detailQueryKey;
      const cachedDetailSnapshot = queryClient.getQueryData<RevenueDetailSnapshot<DetailRow>>(detailQueryKey);
      if (!isSameDetailQuery) {
        detailPhysicalRowVisibleRef.current = false;
        detailPhysicalPresentationReadyRef.current = false;
      }
      detailLoadQueryKeyRef.current = detailQueryKey;
      detailLoadTypeRef.current = detailType;
      detailLoadGate.start(hasUsableSuccessfulReportCache(
        queryClient.getQueryState(detailQueryKey)?.status,
        cachedDetailSnapshot,
        (cachedSnapshot) => cachedSnapshot.isComplete && cachedSnapshot.rows.length > 0,
      ) ? "warm" : "cold", {
        restorePhysicalState: isSameDetailQuery
          ? {
              firstRowVisible: detailPhysicalRowVisibleRef.current,
              presentationReady: detailPhysicalPresentationReadyRef.current,
            }
          : undefined,
      });
      try {
        const result = detailType === "hourly"
          ? await fetchExecutiveHourlyTraffic(detailParams, { signal })
          : await fetchBranchDailyPerformance(detailParams, { signal });
        if (
          signal.aborted
          || detailRequestGenerationRef.current !== requestGeneration
          || detailLoadQueryKeyRef.current !== detailQueryKey
        ) {
          const abortError = new Error("Stale revenue detail request");
          abortError.name = "AbortError";
          throw abortError;
        }
        return result;
      } catch (error) {
        if (
          detailRequestGenerationRef.current === requestGeneration
          && detailLoadQueryKeyRef.current === detailQueryKey
        ) {
          detailLoadGate.fail();
        }
        throw error;
      }
    },
    enabled: detailQueryEnabled,
    ...REPORT_QUERY_OPTIONS,
  });
  useLayoutEffect(() => {
    if (!drilldown) {
      detailLoadGate.cancel();
      detailLoadQueryKeyRef.current = null;
      detailLoadTypeRef.current = null;
      detailPhysicalRowVisibleRef.current = false;
      detailPhysicalPresentationReadyRef.current = false;
      return;
    }
    return () => {
      if (detailLoadQueryKeyRef.current !== detailQueryKey) return;
      detailRequestGenerationRef.current += 1;
      detailLoadGate.cancel();
      detailLoadQueryKeyRef.current = null;
      detailLoadTypeRef.current = null;
      detailPhysicalRowVisibleRef.current = false;
      detailPhysicalPresentationReadyRef.current = false;
    };
  }, [detailLoadGate, detailQueryKey, drilldown]);

  const recordDetailMeasurement = useCallback((measurement: ReportLoadPerformanceMeasurement | null) => {
    if (!measurement || !detailLoadTypeRef.current) return;
    recordReportLoadPerformance(
      detailLoadTypeRef.current === "hourly" ? "revenue-hourly" : "revenue-daily",
      measurement,
    );
  }, []);

  const onDetailViewableItemsChanged = useRef(
    ({ viewableItems }: { viewableItems: ViewToken<DetailRow>[] }) => {
      const hasVisibleDetailRow = viewableItems.some((token) => token.isViewable);
      detailPhysicalRowVisibleRef.current = hasVisibleDetailRow;
      recordDetailMeasurement(detailLoadGate.setFirstRowVisible(hasVisibleDetailRow));
    },
  ).current;

  useLayoutEffect(() => {
    if (!drilldown) return;
    let cancelled = false;
    const interactionTask = InteractionManager.runAfterInteractions(() => {
      if (cancelled || detailLoadQueryKeyRef.current !== detailQueryKey) return;
      detailPhysicalPresentationReadyRef.current = true;
      recordDetailMeasurement(detailLoadGate.setPresentationReady(true));
    });
    return () => {
      cancelled = true;
      interactionTask.cancel();
    };
  }, [detailLoadGate, detailQueryKey, drilldown, recordDetailMeasurement]);

  // —— 日报按小时累计的同期对比 ——
  // 一次取范围内每家店的逐小时本期与同期；截止整点在客户端计算，点选整点不需要重新请求。
  const cumulativeQueryEnabled = summaryQueryEnabled && mode === "day";
  const cumulativeQueryKey = useMemo(
    () => ["reports", "revenue-cumulative", accountIdentity, cashierStoreScopeVersion, queryParams] as const,
    [accountIdentity, cashierStoreScopeVersion, queryParams],
  );
  const cumulativeQuery = useQuery({
    queryKey: cumulativeQueryKey,
    queryFn: ({ signal }) => fetchExecutiveHourlyTrafficByBranch(queryParams, { signal }),
    enabled: cumulativeQueryEnabled,
    ...REPORT_QUERY_OPTIONS,
  });
  useLayoutEffect(() => {
    if (!reportScopeValid || !cumulativeQuery.data?.isComplete || cumulativeQuery.isFetching) return;
    completeCumulativeSnapshotsRef.set(summarySnapshotKey, cumulativeQuery.data.rows);
  }, [
    completeCumulativeSnapshotsRef,
    cumulativeQuery.data,
    cumulativeQuery.isFetching,
    reportScopeValid,
    summarySnapshotKey,
  ]);
  const cumulativeRows = !reportScopeValid || mode !== "day"
    ? null
    : cumulativeQuery.data?.isComplete
      ? cumulativeQuery.data.rows
      : completeCumulativeSnapshotsRef.get(summarySnapshotKey) ?? null;
  const cumulativeSeriesByBranch = useMemo(
    () => (cumulativeRows ? groupHourlySeriesByBranch(cumulativeRows) : null),
    [cumulativeRows],
  );
  const cumulativeScopeSeries = useMemo(
    () => cumulativeRows
      ? buildHourlySeries(selectedBranchCode
          ? cumulativeRows.filter((row) => row.branchCode === selectedBranchCode)
          : cumulativeRows)
      : null,
    [cumulativeRows, selectedBranchCode],
  );
  const cumulativeCutoff = useMemo(
    () => mode === "day"
      ? resolveDefaultCutoff({
          selectedDate: period.startDate,
          todayKey: dateBounds.maxDate,
          statisticsCompletedAtUtc,
        })
      : null,
    [dateBounds.maxDate, mode, period.startDate, statisticsCompletedAtUtc],
  );
  const effectiveCutoffHour = cumulativeCutoff
    ? resolveEffectiveCutoff(selectedCutoffHour, cumulativeCutoff.cutoffHour)
    : null;
  const liveTimeLabel = formatLocalClockTime(statisticsCompletedAtUtc);
  // 当天比到最近完整整点；历史日期默认比整天（沿用日统计原值），只有点选了整点才截断。
  const rankingAlignmentNeeded =
    mode === "day" && effectiveCutoffHour !== null && effectiveCutoffHour < FULL_DAY_CUTOFF_HOUR;
  const cumulativeSettledWithoutData =
    !cumulativeQueryEnabled
    || cumulativeQuery.isError
    || Boolean(cumulativeQuery.data && !cumulativeQuery.data.isComplete && cumulativeQuery.data.pollingExhausted);
  const cutoffPendingForToday =
    mode === "day"
    && period.startDate === dateBounds.maxDate
    && cumulativeCutoff === null
    && statisticsFreshnessQuery.isLoading;
  // 当天排行必须等逐店小时数据就绪后按同一截止整点显示，不能先闪出「今天部分值 vs 去年全天」。
  // 小时数据失败或补算耗尽时退回日统计原值，排行本身不受影响。
  const rankingAwaitsAlignment =
    summaryQueryEnabled
    && (cutoffPendingForToday
      || (rankingAlignmentNeeded && !cumulativeSeriesByBranch && !cumulativeSettledWithoutData));
  const rankingCutoffHour = rankingAlignmentNeeded && cumulativeSeriesByBranch ? effectiveCutoffHour : null;
  const rankingCutoffLabel = rankingCutoffHour === null
    ? null
    : formatHourLabel(cumulativeScopeSeries
      ? getDisplayCutoffHour(cumulativeScopeSeries, rankingCutoffHour)
      : rankingCutoffHour);
  const selectCutoffHour = (hour: number) => {
    if (!cumulativeCutoff) return;
    const defaultDisplay = cumulativeScopeSeries
      ? getDisplayCutoffHour(cumulativeScopeSeries, cumulativeCutoff.cutoffHour)
      : cumulativeCutoff.cutoffHour;
    // 选回默认截止时恢复跟随最新统计，下一轮统计完成后截止整点会自动前移。
    setSelectedCutoffHour(hour >= defaultDisplay ? null : hour);
  };

  const summaryLoading =
    cashierStoreOptionsQuery.isFetching
    || (summaryQueryEnabled && summaryQuery.isLoading)
    || rankingAwaitsAlignment;
  const summaryError = cashierStoreOptionsQuery.isError || summaryQuery.isError;
  const summaryRefreshing =
    cashierStoreOptionsQuery.isRefetching || summaryQuery.isRefetching;
  const retrySummary = () => {
    if (cashierStoreOptionsQuery.isFetching) return;
    // 先重验权威白名单，新 revision 会自动重试对应范围的业务查询。
    void cashierStoreOptionsQuery.refetch();
  };
  const retryDetail = () => {
    if (detailQueryEnabled) {
      void cashierStoreOptionsQuery.refetch();
    }
  };

  const refresh = () => {
    if (onRefreshReport) {
      // 嵌入报告中心时统一走控制器，避免下拉与页头刷新并发。
      void onRefreshReport();
      return;
    }
    void Promise.all([
      cashierStoreOptionsQuery.refetch(),
      onRefreshFreshness?.(),
    ]);
  };

  const setPeriodMode = (nextMode: RevenuePeriodMode) => {
    const nextPeriod = getDefaultRevenuePeriod(nextMode);
    setMode(nextMode);
    setPeriod(nextPeriod);
    setSelectedDate(dateBounds.maxDate);
    setDrilldown(null);
    setSelectedCutoffHour(null);
  };

  const setActivePeriod = (nextPeriod: RevenuePeriod, anchorDate?: string) => {
    setMode(nextPeriod.mode);
    setPeriod(nextPeriod);
    setSelectedDate(anchorDate ?? (nextPeriod.startDate < dateBounds.minDate ? dateBounds.minDate : nextPeriod.startDate));
    setDrilldown(null);
    setSelectedCutoffHour(null);
  };
  const previousPeriod = getPreviousRevenuePeriod(period);
  const nextPeriod = getNextRevenuePeriod(period);
  const currentPeriod = getDefaultRevenuePeriod(mode);
  const isCurrentPeriod = period.startDate === currentPeriod.startDate && period.endDate === currentPeriod.endDate;

  const currentShortcut =
    mode === "day"
      ? t("reports.shortcuts.today")
      : mode === "week"
        ? t("reports.shortcuts.thisWeek")
        : t("reports.shortcuts.thisMonth");
  const previousShortcut =
    mode === "day"
      ? {
          label: t("reports.shortcuts.yesterday"),
          period: getYesterdayRevenuePeriod,
        }
      : mode === "week"
        ? {
            label: t("reports.shortcuts.lastWeek"),
            period: getLastWeekRevenuePeriod,
          }
        : {
            label: t("reports.shortcuts.lastMonth"),
            period: getLastMonthRevenuePeriod,
          };
  const previousShortcutPeriod = previousShortcut.period();
  const isPreviousShortcutPeriod =
    period.startDate === previousShortcutPeriod.startDate && period.endDate === previousShortcutPeriod.endDate;

  const statisticRows = useMemo(() => {
    return getReportSnapshotDisplay(
      summaryQuery,
      getCompleteReportSnapshot(completeSummarySnapshotsRef, summarySnapshotKey),
      (snapshot) => snapshot.isComplete ? snapshot.rows : undefined,
      reportScopeValid,
    ) ?? [];
  }, [completeSummarySnapshotsRef, reportScopeValid, summaryQuery, summarySnapshotKey]);
  // 排行对齐到截止整点后按对齐营业额重排；搜索、分店数、汇总与下钻都使用同一份对齐结果。
  const rows = useMemo(() => {
    if (rankingAwaitsAlignment) return [];
    if (rankingCutoffHour === null || !cumulativeSeriesByBranch) return statisticRows;
    return alignBranchRowsToCutoff(statisticRows, cumulativeSeriesByBranch, rankingCutoffHour);
  }, [cumulativeSeriesByBranch, rankingAwaitsAlignment, rankingCutoffHour, statisticRows]);
  // 日视图的首屏业务数据是顶部累计卡片，排行常被推到首屏之外：卡片与排行都就绪即视为首条数据可见，
  // 否则日视图的首屏计时要等用户滚动才完成。放在数据归一化的 layout effect 之后，同一次提交内先归一化再完成。
  const cumulativeCardReady = mode === "day" && cumulativeScopeSeries !== null && rows.length > 0;
  useLayoutEffect(() => {
    if (!cumulativeCardReady) return;
    const measurement = revenueLoadTimer.markFirstRowVisible();
    if (measurement) recordReportLoadPerformance("revenue", measurement);
  }, [cumulativeCardReady, revenueLoadTimer, summaryQuery.dataUpdatedAt]);
  const summaryPending = summaryQuery.data !== undefined && !summaryQuery.data.isComplete;
  const summaryPollingExhausted = summaryPending && Boolean(summaryQuery.data?.pollingExhausted);
  const selectedBranch = useMemo(
    () => rows.find((row) => row.branchCode === selectedBranchCode) ?? null,
    [rows, selectedBranchCode],
  );
  const branchPickerRows = useMemo(() => {
    const search = branchPickerSearch.trim().toLocaleLowerCase();
    // 选择器按店名排列，复制数组以保留报表自身的营业额排名。
    return rows.filter((row) => !search ||
      `${row.branchName} ${row.branchCode}`.toLocaleLowerCase().includes(search),
    ).sort((a, b) =>
      (a.branchName || a.branchCode).localeCompare(b.branchName || b.branchCode, "en", {
        sensitivity: "base", numeric: true,
      }) || a.branchCode.localeCompare(b.branchCode, "en", { numeric: true }),
    );
  }, [branchPickerSearch, rows]);
  const scopedRows = useMemo(
    () => selectedBranchCode
      ? rows.filter((row) => row.branchCode === selectedBranchCode)
      : rows,
    [rows, selectedBranchCode],
  );
  const visibleRows = useMemo(() => {
    const search = branchSearch.trim().toLocaleLowerCase();
    if (!search) return scopedRows;
    return scopedRows.filter((row) =>
      `${row.branchName} ${row.branchCode}`.toLocaleLowerCase().includes(search),
    );
  }, [branchSearch, scopedRows]);
  // 半成品或失败刷新只替换状态，不覆盖同条件下最后一份完整明细。
  const detailSnapshot = reportScopeValid ? getCompleteReportSnapshot(completeDetailSnapshotsRef, detailSnapshotKey) : undefined;
  const completeDetailRows = detailQuery.data?.isComplete ? detailQuery.data.rows : [];
  const detailRows = getReportSnapshotDisplay(
    detailQuery,
    detailSnapshot,
    (snapshot) => snapshot.isComplete ? completeDetailRows : undefined,
    reportScopeValid,
  ) ?? EMPTY_DETAIL_ROWS;
  const detailShowingSnapshot = detailSnapshot !== undefined
    && (detailQuery.isFetching || !detailQuery.data?.isComplete || detailQuery.isError);
  const detailPending = detailQuery.data !== undefined && !detailQuery.data.isComplete;
  const detailPollingExhausted = detailPending && Boolean(detailQuery.data?.pollingExhausted);
  // 分时下钻：累计 / 逐小时两种口径；状态按最近完整整点判断，高亮跟随用户点选的整点。
  const hourlyDetailSourceRows = useMemo(
    () => drilldown?.type === "hourly"
      ? detailRows.filter((row): row is HourlyRevenueRow => !isDailyRow(row))
      : [],
    [detailRows, drilldown?.type],
  );
  const hourlyDetailRows = useMemo(
    () => drilldown?.type === "hourly"
      ? buildHourlyDetailRows(hourlyDetailSourceRows, {
          cutoffHour: cumulativeCutoff?.cutoffHour ?? FULL_DAY_CUTOFF_HOUR,
          live: cumulativeCutoff?.live ?? false,
          cumulative: hourlyDetailMode === "cumulative",
          highlightCutoffHour: effectiveCutoffHour ?? FULL_DAY_CUTOFF_HOUR,
        })
      : null,
    [cumulativeCutoff, drilldown?.type, effectiveCutoffHour, hourlyDetailMode, hourlyDetailSourceRows],
  );
  const detailListRows: DetailRow[] = hourlyDetailRows ?? detailRows;
  const detailSeries = useMemo(
    () => (hourlyDetailSourceRows.length > 0 ? buildHourlySeries(hourlyDetailSourceRows) : null),
    [hourlyDetailSourceRows],
  );
  const detailChartModel = useMemo(
    () => (detailSeries && cumulativeCutoff ? buildCumulativeChartModel(detailSeries, cumulativeCutoff) : null),
    [cumulativeCutoff, detailSeries],
  );
  const detailMarkerHour = detailSeries
    ? getDisplayCutoffHour(detailSeries, effectiveCutoffHour ?? FULL_DAY_CUTOFF_HOUR)
    : FULL_DAY_CUTOFF_HOUR;
  useLayoutEffect(() => {
    if (
      !reportScopeValid
      || !drilldown
      || detailQuery.data === undefined
      || detailQuery.isFetching
      || detailLoadQueryKeyRef.current !== detailQueryKey
    ) return;
    if (detailQuery.isError) {
      // 有旧完整快照时保留物理行可见状态；没有快照才进入空错误态。
      if (detailRows.length > 0) return;
      detailPhysicalRowVisibleRef.current = false;
      detailLoadGate.setFirstRowVisible(false);
      return;
    }
    if (!detailQuery.data.isComplete) {
      // 有界轮询耗尽或契约不完整时不能显示部分行，更不能计入首条可见耗时。
      detailPhysicalRowVisibleRef.current = false;
      detailLoadGate.setFirstRowVisible(false);
      detailLoadGate.fail();
      return;
    }
    if (detailRows.length === 0) {
      // 空结果没有首条业务行，不能把空态误记成 2 秒达标。
      detailPhysicalRowVisibleRef.current = false;
      detailLoadGate.cancel();
      return;
    }
    if (!detailQuery.isFetching) {
      saveCompleteReportSnapshot(
        completeDetailSnapshotsRef,
        detailSnapshotKey,
        detailQuery.data.rows,
        {
          statisticUpdatedAt: detailQuery.data.statisticUpdatedAt,
          cacheVersion: detailQuery.data.cacheVersion,
        },
      );
      recordDetailMeasurement(detailLoadGate.markDataNormalized());
    }
  }, [
    reportScopeValid,
    detailLoadGate,
    detailQuery.data,
    detailQuery.dataUpdatedAt,
    detailQuery.isError,
    detailQuery.isFetching,
    detailQueryKey,
    detailRows.length,
    completeDetailSnapshotsRef,
    detailSnapshotKey,
    drilldown,
    recordDetailMeasurement,
  ]);
  const summary = useMemo(() => buildRevenueSummary(scopedRows), [scopedRows]);
  const selectedBranchSummary = useMemo(
    () => (drilldown ? buildRevenueSummary([drilldown.branch]) : null),
    [drilldown]
  );
  const summaryListItems = useMemo<SummaryListItem[]>(
    () => [
      { type: "table-header" },
      ...visibleRows.map((row) => ({
        type: "branch" as const,
        row,
        rank: rows.findIndex((candidate) => candidate.id === row.id),
      })),
    ],
    [rows, visibleRows]
  );
  const growthNewLabel = t("reports.metrics.newGrowth");
  const getWeekdayLabel = (weekday: number) => t(`reports.weekdaysShort.${weekday}`);

  const renderGrowthCell = (current: number, compare: number, columnStyle?: StyleProp<ViewStyle>) => {
    const tone = getGrowthTone(current, compare);
    return (
      <View style={[styles.growthColumn, columnStyle]}>
        <TableText numeric style={[styles.strongText, { color: GROWTH_COLORS[tone] }]}>
          {formatGrowthRate(current, compare, growthNewLabel)}
        </TableText>
      </View>
    );
  };

  const renderSummaryRow = (item: BranchRevenueRow, rowIndex: number, isLast: boolean) => (
    <Pressable
      style={[
        styles.tableRow,
        styles.summaryTableRow,
        isLast ? styles.lastSummaryTableRow : null,
      ]}
      accessibilityRole="button"
      accessibilityLabel={t(
        mode === "day"
          ? "reports.accessibility.openHourlyDetail"
          : "reports.accessibility.openDailyDetail",
        { branch: `${formatOrdinal(rowIndex)} ${item.branchName || item.branchCode}` }
      )}
      onPress={() => setDrilldown({ type: mode === "day" ? "hourly" : "daily", branch: item })}
    >
      <View style={styles.rankColumn}>
        <TableText style={styles.rankText}>{formatOrdinal(rowIndex)}</TableText>
      </View>
      <View style={styles.branchColumn}>
        {/* 最长的分店名（如 Discount General Charlestown）允许折两行，与右侧三行数字同高。 */}
        <TableText style={styles.strongText} lines={2}>{item.branchName || item.branchCode}</TableText>
        <TableText style={styles.muted}>{item.branchCode}</TableText>
      </View>
      <View style={styles.amountColumn}>
        <TableText numeric style={styles.strongText}>{formatWholeMoney(item.revenue)}</TableText>
        <TableText numeric style={styles.muted}>{formatWholeMoney(item.compareRevenue)}</TableText>
        <TableText
          numeric
          style={[styles.compactGrowthText, { color: GROWTH_COLORS[getGrowthTone(item.revenue, item.compareRevenue)] }]}
        >
          {formatGrowthRate(item.revenue, item.compareRevenue, growthNewLabel)}
        </TableText>
      </View>
      <View style={styles.countColumn}>
        <TableText numeric style={styles.strongText}>{formatCount(item.transactions)}</TableText>
        <TableText numeric style={styles.muted}>{formatCount(item.compareTransactions)}</TableText>
      </View>
      <View style={styles.averageColumn}>
        <TableText numeric style={styles.strongText}>{formatMoney(item.averageTransaction)}</TableText>
        <TableText numeric style={styles.muted}>{formatMoney(item.compareAverageTransaction)}</TableText>
      </View>
      <Text style={styles.chevronText} accessibilityElementsHidden>›</Text>
    </Pressable>
  );

  const renderSummaryTableHeader = () => (
    <View style={[styles.tableRow, styles.summaryTableRow, styles.tableHeaderRow, styles.summaryTableHeaderRow]}>
      <View style={styles.rankColumn}>
        <TableText style={styles.headerText}>#</TableText>
      </View>
      <View style={styles.branchColumn}>
        <TableText style={styles.headerText}>{t("productReport.filters.store")}</TableText>
      </View>
      <View style={styles.amountColumn}>
        <TableText numeric style={styles.headerText}>{t("reports.metrics.revenue")}</TableText>
      </View>
      <View style={styles.countColumn}>
        <TableText numeric style={styles.headerText}>{t("reports.metrics.transactions")}</TableText>
      </View>
      <View style={styles.averageColumn}>
        <TableText numeric style={styles.headerText}>{t("reports.metrics.averageTransaction")}</TableText>
      </View>
      <View style={styles.chevronColumn} />
    </View>
  );

  const renderDetailRow = (item: DetailRow, index: number) => {
    const hourlyView = isHourlyDetailView(item) ? item : null;
    const upcoming = hourlyView?.status === "upcoming";
    const inProgress = hourlyView?.status === "live";
    // 进行中的小时只有半截数据，数值弱化显示且不算增长率；未到的小时只保留去年值。
    const currentStyle = inProgress ? styles.detailLiveValue : styles.strongText;
    return (
      <View
        style={[
          styles.tableRow,
          styles.summaryTableRow,
          styles.detailTableRow,
          // 高亮「截至所选整点」的累计行；逐小时口径下这一行是 11–12 点，易被误读为 12 点，故不高亮。
          hourlyView?.isCutoffRow && hourlyDetailMode === "cumulative" ? styles.detailCutoffRow : null,
          upcoming ? styles.detailUpcomingRow : null,
          index === detailListRows.length - 1 ? styles.lastSummaryTableRow : null,
        ]}
      >
        <View style={styles.detailRankColumn}>
          <TableText style={styles.rankText}>{formatOrdinal(index)}</TableText>
        </View>
        <View style={styles.detailPeriodColumn}>
          {hourlyView && hourlyDetailMode === "cumulative" ? (
            <TableText style={styles.strongText}>{formatHourLabel(hourlyView.boundaryHour)}</TableText>
          ) : (
            <DetailPeriodText item={item} getWeekdayLabel={getWeekdayLabel} />
          )}
          {inProgress ? (
            <Text style={styles.detailLiveTag} numberOfLines={1}>
              {t("reports.cumulative.live", { time: liveTimeLabel ?? "" })}
            </Text>
          ) : upcoming ? (
            <TableText style={styles.muted}>{t("reports.cumulative.upcoming")}</TableText>
          ) : null}
        </View>
        <View style={styles.detailAmountColumn}>
          <TableText numeric style={upcoming ? styles.muted : currentStyle}>
            {upcoming ? "—" : formatWholeMoney(item.revenue)}
          </TableText>
          <TableText numeric style={styles.muted}>{formatWholeMoney(item.compareRevenue)}</TableText>
        </View>
        {inProgress || upcoming ? (
          <View style={[styles.growthColumn, styles.detailGrowthColumn]}>
            <TableText numeric style={[styles.compactGrowthText, styles.muted]}>
              {inProgress && hourlyView
                ? t("reports.cumulative.comparedAt", { time: formatHourLabel(hourlyView.boundaryHour) })
                : "—"}
            </TableText>
          </View>
        ) : renderGrowthCell(item.revenue, item.compareRevenue, styles.detailGrowthColumn)}
        <View style={styles.detailCountColumn}>
          <TableText numeric style={upcoming ? styles.muted : currentStyle}>
            {upcoming ? "—" : formatCount(item.transactions)}
          </TableText>
          <TableText numeric style={styles.muted}>{formatCount(item.compareTransactions)}</TableText>
        </View>
        <View style={styles.detailAmountColumn}>
          <TableText numeric style={upcoming ? styles.muted : currentStyle}>
            {upcoming ? "—" : formatMoney(item.averageTransaction)}
          </TableText>
          <TableText numeric style={styles.muted}>{formatMoney(item.compareAverageTransaction)}</TableText>
        </View>
      </View>
    );
  };

  return (
    <View style={styles.container}>
      {!embedded ? (
        <View style={styles.standaloneHeader}>
          <Text variant="headlineSmall" style={styles.title}>
            {t("reports.title")}
          </Text>
        </View>
      ) : null}

      {/* 过滤、汇总和 28 家分店共用一个虚拟列表；表头吸顶且不对分店分页。 */}
      <FlatList
        data={summaryListItems}
        keyExtractor={(item) => item.type === "table-header" ? item.type : item.row.id}
        renderItem={({ item, index }) =>
          item.type === "table-header"
            ? renderSummaryTableHeader()
            : renderSummaryRow(item.row, item.rank, index === summaryListItems.length - 1)
        }
        initialNumToRender={12}
        maxToRenderPerBatch={12}
        windowSize={7}
        viewabilityConfig={summaryViewabilityConfig}
        onViewableItemsChanged={onSummaryViewableItemsChanged}
        style={styles.tableList}
        contentContainerStyle={[styles.listContent, { paddingBottom: insets.bottom + 96 }]}
        contentInsetAdjustmentBehavior="automatic"
        ListHeaderComponent={
          <View style={styles.listHeader}>
            <View style={styles.filtersCard}>
              <SegmentedButtons
                value={mode}
                onValueChange={(value) => setPeriodMode(value as RevenuePeriodMode)}
                buttons={[
                  { value: "day", label: t("reports.periods.day") },
                  { value: "week", label: t("reports.periods.week") },
                  { value: "month", label: t("reports.periods.month") },
                ]}
              />

              <View style={styles.dateNavigationRow}>
                <Pressable
                  style={styles.branchFilterButton}
                  accessibilityRole="button"
                  accessibilityLabel={t("reports.actions.selectBranch")}
                  disabled={
                    !cashierStoreOptionsQuery.isSuccess
                    || cashierStoreOptionsQuery.isFetching
                    || cashierStoreScopeEmpty
                  }
                  onPress={() => setBranchPickerVisible(true)}
                >
                  <View style={styles.branchFilterTextBlock}>
                    <Text variant="labelSmall" style={styles.muted} numberOfLines={1}>
                      {t("productReport.filters.store")}
                    </Text>
                    <Text variant="bodySmall" style={styles.strongText} numberOfLines={1}>
                      {selectedBranch?.branchName || selectedBranchCode || t("productReport.filters.allStores")}
                    </Text>
                  </View>
                  <Text style={styles.branchFilterChevron}>⌄</Text>
                </Pressable>
                <MonthDatePickerField
                  compact
                  value={selectedDate}
                  minDate={dateBounds.minDate}
                  maxDate={dateBounds.maxDate}
                  label={t("reports.periods.date")}
                  onChange={(date) => setActivePeriod(getRevenuePeriodForDate(mode, date), date)}
                  style={styles.datePicker}
                />
              </View>

              <View style={styles.toolbar}>
                <IconButton
                  icon="chevron-left"
                  mode="outlined"
                  size={20}
                  disabled={!isRevenuePeriodAvailable(previousPeriod, dateBounds)}
                  accessibilityLabel={t("reports.actions.previous")}
                  onPress={() => setActivePeriod(previousPeriod)}
                  style={styles.dateNavigationButton}
                />
                <Button
                  compact
                  mode={isCurrentPeriod ? "contained" : "outlined"}
                  onPress={() => setActivePeriod(currentPeriod, dateBounds.maxDate)}
                  style={styles.shortcutButton}
                >
                  {currentShortcut}
                </Button>
                <Button
                  compact
                  mode={isPreviousShortcutPeriod ? "contained" : "outlined"}
                  onPress={() => setActivePeriod(previousShortcutPeriod)}
                  style={styles.shortcutButton}
                >
                  {previousShortcut.label}
                </Button>
                <IconButton
                  icon="chevron-right"
                  mode="outlined"
                  size={20}
                  disabled={!isRevenuePeriodAvailable(nextPeriod, dateBounds)}
                  accessibilityLabel={t("reports.actions.next")}
                  onPress={() => setActivePeriod(nextPeriod)}
                  style={styles.dateNavigationButton}
                />
              </View>
            </View>

            {mode === "day" && summaryQueryEnabled ? (
              <CumulativeRevenueCard
                scopeLabel={selectedBranch?.branchName || selectedBranchCode || t("productReport.filters.allStores")}
                compareDate={queryParams.compareStartDate}
                series={cumulativeScopeSeries}
                cutoff={cumulativeCutoff}
                effectiveCutoffHour={effectiveCutoffHour}
                liveTimeLabel={liveTimeLabel}
                loading={(cumulativeQuery.isLoading && !cumulativeRows) || cutoffPendingForToday}
                error={cumulativeQuery.isError && !cumulativeRows}
                statisticsIncomplete={Boolean(cumulativeQuery.data?.pollingExhausted) && !cumulativeRows}
                onRetry={retrySummary}
                onSelectCutoff={selectCutoffHour}
              />
            ) : null}

            <View style={styles.rankingTitleRow}>
              <Text variant="titleMedium" style={styles.sectionTitle}>
                {t("reports.sections.branchRanking")}
              </Text>
              <View style={styles.rankingActions}>
                <Text variant="bodySmall" style={[styles.muted, styles.rankingStatusText]} numberOfLines={1}>
                  {summaryRefreshing
                    ? t("reports.states.refreshingStatistics")
                    : summaryLoading
                    ? t("loading")
                    : summaryPending
                    ? t(summaryPollingExhausted
                        ? "reports.states.statisticsIncomplete"
                        : "reports.states.refreshingStatistics")
                    : `${t("reports.branchCount", { count: visibleRows.length })} · ${t("reports.metrics.revenue")} ↓`}
                </Text>
                {completeSummarySnapshotsRef.has(summarySnapshotKey) && (summaryRefreshing || summaryError || summaryPending) ? (
                  <Text variant="labelSmall" style={styles.snapshotNotice}>
                    {t("reports.states.showingSnapshot", {
                      time: formatReportSnapshotTime(
                        completeSummarySnapshotsRef.get(summarySnapshotKey)?.statisticUpdatedAt ?? null,
                      ) ?? t("reports.freshness.noSuccess"),
                    })}
                  </Text>
                ) : null}
                {summaryPollingExhausted && summaryQueryEnabled && !summaryError ? (
                  <IconButton
                    icon="refresh"
                    size={18}
                    accessibilityLabel={t("actions.retry")}
                    onPress={retrySummary}
                    style={styles.rankingSearchButton}
                  />
                ) : null}
                <IconButton
                  icon={branchSearchVisible ? "close" : "magnify"}
                  size={18}
                  accessibilityLabel={t(branchSearchVisible ? "reports.actions.closeSearch" : "reports.actions.searchBranch")}
                  onPress={() => {
                    setBranchSearchVisible((visible) => !visible);
                    if (branchSearchVisible) setBranchSearch("");
                  }}
                  style={styles.rankingSearchButton}
                />
              </View>
            </View>
            {rankingCutoffLabel ? (
              <View style={styles.rankingCutoffRow}>
                <View style={styles.rankingCutoffPill}>
                  <Icon source="clock-outline" size={14} color="#073B83" />
                  <Text variant="labelMedium" style={styles.rankingCutoffText} numberOfLines={1}>
                    {t("reports.cumulative.rankingAsOf", { time: rankingCutoffLabel })}
                  </Text>
                </View>
              </View>
            ) : null}
            {branchSearchVisible ? (
              <TextInput
                dense
                mode="outlined"
                value={branchSearch}
                onChangeText={setBranchSearch}
                placeholder={t("reports.actions.searchBranchPlaceholder")}
                accessibilityLabel={t("reports.actions.searchBranch")}
                left={<TextInput.Icon icon="magnify" />}
                right={branchSearch ? (
                  <TextInput.Icon
                    icon="close"
                    accessibilityLabel={t("reports.actions.clearSearch")}
                    onPress={() => setBranchSearch("")}
                  />
                ) : undefined}
                style={styles.rankingSearchInput}
              />
            ) : null}
          </View>
        }
        stickyHeaderIndices={[1]}
        refreshControl={<RefreshControl refreshing={summaryRefreshing} onRefresh={refresh} />}
        ListFooterComponent={
          <>
            {summaryLoading && rows.length === 0 ? (
              <View style={styles.summaryTableState}>
                <StateBox label={t("loading")} loading />
              </View>
            ) : summaryError && rows.length === 0 ? (
              <View style={styles.summaryTableState}>
                <StateBox label={t("reports.states.errorTitle")} actionLabel={t("actions.retry")} onAction={retrySummary} />
              </View>
            ) : cashierStoreScopeEmpty ? (
              <View style={styles.summaryTableState}>
                <StateBox label={t("reports.states.noCashierEnabledStores")} />
              </View>
            ) : summaryPending && rows.length === 0 ? (
              <View style={styles.summaryTableState}>
                <StateBox
                  label={t(summaryPollingExhausted
                    ? "reports.states.statisticsIncomplete"
                    : "reports.states.refreshingStatistics")}
                  actionLabel={summaryPollingExhausted ? t("actions.retry") : undefined}
                  onAction={summaryPollingExhausted ? retrySummary : undefined}
                />
              </View>
            ) : visibleRows.length === 0 ? (
              <View style={styles.summaryTableState}>
                <StateBox label={t("reports.states.empty")} />
              </View>
            ) : null}
            {scopedRows.length > 0 && !summaryPending ? (
              <View style={styles.summaryFooter}>
                <RevenueSummaryCard
                  title={t("reports.sections.summary")}
                  caption={`${t("reports.branchCount", { count: scopedRows.length })} · ${getPeriodLabel(period)}${
                    rankingCutoffLabel ? ` · ${t("reports.cumulative.asOfShort", { time: rankingCutoffLabel })}` : ""
                  }`}
                  summary={summary}
                />
              </View>
            ) : null}
          </>
        }
      />

      <Portal>
        <Modal
          visible={Boolean(drilldown) && detailQueryEnabled}
          onDismiss={() => setDrilldown(null)}
          contentContainerStyle={styles.bottomSheet}
        >
          <View style={styles.sheetHandle} />
          <View style={styles.modalHeader}>
            <View style={styles.modalTitleBlock}>
              <Text variant="titleLarge" style={styles.title} numberOfLines={1}>
                {t(mode === "day" ? "reports.sections.hourlyDetail" : "reports.sections.dailyDetail")}
              </Text>
              <Text variant="bodyMedium" style={styles.muted} numberOfLines={1}>
                {drilldown?.branch.branchName || drilldown?.branch.branchCode} · {drilldown?.branch.branchCode}
              </Text>
            </View>
            <IconButton icon="close" accessibilityLabel={t("actions.close")} onPress={() => setDrilldown(null)} />
          </View>

          <View style={styles.detailMetaRow}>
            <Text variant="bodySmall" style={styles.detailMetaText}>{getPeriodLabel(period)}</Text>
            <Text variant="bodySmall" style={styles.muted}>
              {t("reports.metrics.compare")} {queryParams.compareStartDate === queryParams.compareEndDate
                ? queryParams.compareStartDate
                : `${queryParams.compareStartDate} - ${queryParams.compareEndDate}`}
            </Text>
            {freshnessLabel ? <Text variant="bodySmall" style={styles.positiveText}>{freshnessLabel}</Text> : null}
          </View>

          {/* 汇总、累计曲线、口径切换与表头放进列表头部，整张抽屉一起滚动；业务行仍是列表条目以保留首行可见计时。 */}
          <ScrollView
            horizontal
            showsHorizontalScrollIndicator
            style={styles.detailHorizontalScroll}
            contentContainerStyle={styles.detailHorizontalContent}
          >
          <FlatList
            data={detailListRows}
            keyExtractor={(item) => item.id}
            renderItem={({ item, index }) => renderDetailRow(item, index)}
            initialNumToRender={12}
            maxToRenderPerBatch={16}
            windowSize={5}
            viewabilityConfig={detailViewabilityConfig}
            onViewableItemsChanged={onDetailViewableItemsChanged}
            bounces={false}
            style={styles.modalList}
            ListHeaderComponent={
              <View style={styles.detailListHeader}>
                <RevenueSummaryCard
                  title={t("reports.sections.branchSummary")}
                  caption={rankingCutoffLabel
                    ? t("reports.cumulative.asOfShort", { time: rankingCutoffLabel })
                    : undefined}
                  summary={selectedBranchSummary}
                />

                {detailChartModel && detailSeries ? (
                  <View style={styles.detailChartCard}>
                    <View style={styles.detailChartTitleRow}>
                      <Text variant="titleSmall" style={styles.sectionTitle}>
                        {t("reports.cumulative.title")}
                      </Text>
                      <Text variant="bodySmall" style={[styles.muted, styles.detailChartCaption]}>
                        {`${cumulativeCutoff?.live
                          ? t("reports.cumulative.liveAt", { time: liveTimeLabel ?? "" })
                          : t("reports.cumulative.dayTotal")} ${formatWholeMoney(getCumulativeTotals(detailSeries, FULL_DAY_CUTOFF_HOUR).revenue)}`}
                      </Text>
                    </View>
                    <CumulativeRevenueChart
                      model={detailChartModel}
                      markerHour={detailMarkerHour}
                      compareFullDayLabel={`${t("reports.cumulative.lyFullDay")} ${formatWholeMoney(
                        getCumulativeTotals(detailSeries, FULL_DAY_CUTOFF_HOUR).compareRevenue,
                      )}`}
                      accessibilityLabel={t("reports.cumulative.chartLabel", {
                        scope: drilldown?.branch.branchName || drilldown?.branch.branchCode || "",
                        time: formatHourLabel(detailMarkerHour),
                        current: formatWholeMoney(getCumulativeTotals(detailSeries, detailMarkerHour).revenue),
                        compare: formatWholeMoney(getCumulativeTotals(detailSeries, detailMarkerHour).compareRevenue),
                      })}
                    />
                  </View>
                ) : null}

                <View style={styles.detailSectionHeader}>
                  <Text variant="titleMedium" style={styles.sectionTitle}>
                    {t(mode === "day" ? "reports.sections.hourlyDetail" : "reports.sections.dailyDetail")}
                  </Text>
                  <Text variant="bodySmall" style={styles.muted}>
                    {t("reports.detailCount", { count: detailRows.length })}
                  </Text>
                </View>
                {drilldown?.type === "hourly" ? (
                  <SegmentedButtons
                    value={hourlyDetailMode}
                    onValueChange={(value) => setHourlyDetailMode(value as HourlyDetailMode)}
                    buttons={[
                      { value: "cumulative", label: t("reports.cumulative.cumulative") },
                      { value: "perHour", label: t("reports.cumulative.perHour") },
                    ]}
                  />
                ) : null}

                {detailShowingSnapshot && detailRows.length > 0 ? (
                  <Text variant="labelSmall" style={styles.snapshotNotice}>
                    {t("reports.states.showingSnapshot", {
                      time: formatReportSnapshotTime(detailSnapshot?.statisticUpdatedAt ?? null)
                        ?? t("reports.freshness.noSuccess"),
                    })}
                  </Text>
                ) : null}

                <View
                  style={[
                    styles.tableRow,
                    styles.summaryTableRow,
                    styles.tableHeaderRow,
                    styles.summaryTableHeaderRow,
                    styles.detailTableRow,
                  ]}
                >
                  <View style={styles.detailRankColumn}><TableText style={styles.headerText}>#</TableText></View>
                  <View style={styles.detailPeriodColumn}>
                    <TableText style={styles.headerText}>
                      {drilldown?.type === "hourly" && hourlyDetailMode === "cumulative"
                        ? t("reports.cumulative.upTo")
                        : mode === "day" ? t("reports.periods.time") : t("reports.periods.date")}
                    </TableText>
                  </View>
                  <View style={styles.detailAmountColumn}>
                    <TableText numeric style={styles.headerText}>{t("reports.metrics.revenue")}</TableText>
                  </View>
                  <View style={styles.detailGrowthColumn}>
                    <TableText numeric style={styles.headerText}>{t("reports.metrics.growthRate")}</TableText>
                  </View>
                  <View style={styles.detailCountColumn}>
                    <TableText numeric style={styles.headerText}>{t("reports.metrics.transactions")}</TableText>
                  </View>
                  <View style={styles.detailAmountColumn}>
                    <TableText numeric style={styles.headerText}>{t("reports.metrics.averageTransaction")}</TableText>
                  </View>
                </View>
              </View>
            }
            ListEmptyComponent={
              <View style={styles.summaryTableState}>
                {detailQuery.isLoading && detailRows.length === 0 ? (
                  <StateBox label={t("loading")} loading />
                ) : detailQuery.isError && detailRows.length === 0 ? (
                  <StateBox label={t("reports.states.errorTitle")} actionLabel={t("actions.retry")} onAction={retryDetail} />
                ) : detailPending && detailRows.length === 0 ? (
                  <StateBox
                    label={t(detailPollingExhausted
                      ? "reports.states.statisticsIncomplete"
                      : "reports.states.refreshingStatistics")}
                    actionLabel={detailPollingExhausted ? t("actions.retry") : undefined}
                    onAction={detailPollingExhausted ? retryDetail : undefined}
                  />
                ) : (
                  <StateBox label={t("reports.states.empty")} />
                )}
              </View>
            }
            ListFooterComponent={drilldown?.type === "hourly" && detailListRows.length > 0 ? (
              <Text variant="bodySmall" style={styles.detailFootnote}>
                {t("reports.cumulative.footnote")}
              </Text>
            ) : null}
          />
          </ScrollView>
        </Modal>

        <Modal
          visible={
            branchPickerVisible
            && cashierStoreOptionsQuery.isSuccess
            && !cashierStoreOptionsQuery.isFetching
          }
          onDismiss={() => setBranchPickerVisible(false)}
          contentContainerStyle={styles.branchPickerSheet}
        >
          <View style={styles.sheetHandle} />
          <View style={styles.modalHeader}>
            <View style={styles.modalTitleBlock}>
              <Text variant="titleLarge" style={styles.title}>
                {t("reports.actions.selectBranch")}
              </Text>
              <Text variant="bodySmall" style={styles.muted}>
                {t("reports.branchCount", { count: rows.length })}
              </Text>
            </View>
            <IconButton
              icon="close"
              accessibilityLabel={t("actions.close")}
              onPress={() => setBranchPickerVisible(false)}
            />
          </View>

          <TextInput
            dense
            mode="outlined"
            value={branchPickerSearch}
            onChangeText={setBranchPickerSearch}
            placeholder={t("reports.actions.searchBranchPlaceholder")}
            accessibilityLabel={t("reports.actions.searchBranch")}
            left={<TextInput.Icon icon="magnify" />}
            right={branchPickerSearch ? (
              <TextInput.Icon
                icon="close"
                accessibilityLabel={t("reports.actions.clearSearch")}
                onPress={() => setBranchPickerSearch("")}
              />
            ) : undefined}
            style={styles.branchPickerSearchInput}
          />

          <Pressable
            style={[styles.branchPickerRow, selectedBranchCode === null ? styles.branchPickerRowSelected : null]}
            accessibilityRole="button"
            accessibilityState={{ selected: selectedBranchCode === null }}
            onPress={() => {
              setSelectedBranchCode(null);
              setBranchPickerSearch("");
              setBranchPickerVisible(false);
            }}
          >
            <View style={styles.detailRankColumn}>
              <TableText style={styles.rankText}>—</TableText>
            </View>
            <Text variant="bodyMedium" style={[styles.branchPickerName, styles.strongText]} numberOfLines={1}>
              {t("productReport.filters.allStores")}
            </Text>
            <Text style={styles.branchPickerCheck}>{selectedBranchCode === null ? "✓" : ""}</Text>
          </Pressable>

          <FlatList
            data={branchPickerRows}
            keyExtractor={(row) => row.id}
            renderItem={({ item, index }) => {
              const selected = item.branchCode === selectedBranchCode;
              return (
                <Pressable
                  style={[styles.branchPickerRow, selected ? styles.branchPickerRowSelected : null]}
                  accessibilityRole="button"
                  accessibilityState={{ selected }}
                  accessibilityLabel={t("reports.actions.selectBranchNamed", {
                    branch: item.branchName || item.branchCode,
                  })}
                  onPress={() => {
                    setSelectedBranchCode(item.branchCode);
                    setBranchPickerSearch("");
                    setBranchPickerVisible(false);
                  }}
                >
                  <View style={styles.detailRankColumn}>
                    <TableText style={styles.rankText}>{formatOrdinal(index)}</TableText>
                  </View>
                  <View style={styles.branchPickerName}>
                    <TableText style={styles.strongText}>{item.branchName || item.branchCode}</TableText>
                    <TableText style={styles.muted}>{item.branchCode}</TableText>
                  </View>
                  <TableText numeric style={styles.muted}>{formatWholeMoney(item.revenue)}</TableText>
                  <Text style={styles.branchPickerCheck}>{selected ? "✓" : ""}</Text>
                </Pressable>
              );
            }}
            initialNumToRender={14}
            maxToRenderPerBatch={14}
            windowSize={5}
            bounces={false}
            style={styles.branchPickerList}
          />
        </Modal>
      </Portal>
    </View>
  );
}

function StateBox({
  label,
  actionLabel,
  onAction,
  loading = false,
}: {
  label: string;
  actionLabel?: string;
  onAction?: () => void;
  loading?: boolean;
}) {
  return (
    <View style={styles.stateBox}>
      {loading ? <ActivityIndicator /> : null}
      <Text variant="bodyMedium" style={styles.stateText}>
        {label}
      </Text>
      {actionLabel && onAction ? (
        <Button mode="contained" onPress={onAction}>
          {actionLabel}
        </Button>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: "#F7F8FA",
  },
  standaloneHeader: {
    paddingHorizontal: 12,
    paddingTop: 8,
  },
  title: {
    color: "#111827",
    fontWeight: "700",
  },
  muted: {
    color: "#6B7280",
  },
  positiveText: {
    // 与 GROWTH_COLORS.up 一致；#16A34A 在白底上对比度只有 3.3:1，小字看不清。
    color: "#15803D",
    fontWeight: "700",
  },
  snapshotNotice: {
    color: "#B45309",
    marginTop: 2,
  },
  sectionTitle: {
    color: "#111827",
    fontWeight: "700",
  },
  listHeader: {
    gap: 8,
    paddingTop: 4,
    paddingBottom: 6,
  },
  filtersCard: {
    gap: 6,
    padding: 6,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    borderRadius: 10,
    backgroundColor: "#FFFFFF",
  },
  dateNavigationRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  dateNavigationButton: {
    margin: 0,
    borderColor: "#CBD5E1",
    borderRadius: 8,
  },
  branchFilterButton: {
    minHeight: 44,
    flex: 0.9,
    minWidth: 0,
    flexDirection: "row",
    alignItems: "center",
    borderWidth: 1,
    borderColor: "#CBD5E1",
    borderRadius: 8,
    paddingLeft: 10,
    paddingRight: 6,
    backgroundColor: "#FFFFFF",
  },
  branchFilterTextBlock: {
    flex: 1,
    minWidth: 0,
  },
  branchFilterChevron: {
    color: "#475569",
    fontSize: 17,
    lineHeight: 20,
  },
  datePicker: {
    flex: 1,
    minWidth: 0,
  },
  toolbar: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  shortcutButton: {
    flex: 1,
  },
  summaryCard: {
    overflow: "hidden",
    borderWidth: 1,
    borderColor: "#E2E8F0",
    borderRadius: 10,
    paddingHorizontal: 10,
    paddingTop: 8,
    backgroundColor: "#FFFFFF",
  },
  cardTitleRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    paddingBottom: 6,
    borderBottomWidth: 1,
    borderBottomColor: "#E2E8F0",
  },
  summaryGrid: {
    paddingTop: 2,
    minWidth: 346,
    flexGrow: 1,
  },
  summaryGridContent: {
    minWidth: 346,
    flexGrow: 1,
  },
  summaryRow: {
    minHeight: 21,
    flexDirection: "row",
    alignItems: "center",
    borderBottomWidth: 1,
    borderBottomColor: "#EEF2F7",
  },
  summaryLastRow: {
    borderBottomWidth: 0,
  },
  summaryLabelColumn: {
    // 58px 才能完整放下 Current / Growth（原 50px 会截成 Curr… / Grow…）。
    width: 58,
    minWidth: 0,
    paddingHorizontal: 2,
  },
  summaryMetricColumn: {
    flex: 1,
    minWidth: 96,
    flexShrink: 0,
    paddingHorizontal: 3,
  },
  summaryValue: {
    color: "#111827",
    fontSize: 14,
    fontWeight: "700",
  },
  rankingTitleRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    paddingHorizontal: 4,
    paddingTop: 2,
  },
  rankingActions: {
    minWidth: 0,
    flexShrink: 1,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "flex-end",
  },
  rankingStatusText: {
    flexShrink: 1,
  },
  rankingSearchButton: {
    width: 44,
    height: 44,
    margin: 0,
  },
  rankingSearchInput: {
    marginHorizontal: 4,
    backgroundColor: "#FFFFFF",
  },
  tableRow: {
    minHeight: 54,
    flexDirection: "row",
    alignItems: "center",
    gap: 3,
    borderBottomWidth: 1,
    borderBottomColor: "#E5E7EB",
    paddingHorizontal: 6,
    paddingVertical: 4,
  },
  tableHeaderRow: {
    minHeight: 34,
    backgroundColor: "#F3F4F6",
  },
  summaryTableRow: {
    borderLeftWidth: 1,
    borderRightWidth: 1,
    borderColor: "#E5E7EB",
    backgroundColor: "#FFFFFF",
  },
  summaryTableHeaderRow: {
    overflow: "hidden",
    borderTopWidth: 1,
    borderTopLeftRadius: 10,
    borderTopRightRadius: 10,
    backgroundColor: "#F3F4F6",
  },
  lastSummaryTableRow: {
    overflow: "hidden",
    borderBottomLeftRadius: 10,
    borderBottomRightRadius: 10,
    borderBottomWidth: 1,
  },
  summaryTableState: {
    overflow: "hidden",
    borderLeftWidth: 1,
    borderRightWidth: 1,
    borderBottomWidth: 1,
    borderColor: "#E5E7EB",
    borderBottomLeftRadius: 8,
    borderBottomRightRadius: 8,
    backgroundColor: "#FFFFFF",
  },
  summaryFooter: {
    marginTop: 8,
  },
  rankColumn: {
    width: 26,
    minWidth: 0,
  },
  rankText: {
    color: "#64748B",
    fontVariant: ["tabular-nums"],
  },
  // 分店列吃掉全部剩余宽度；数字列按内容定宽（月度单店营业额约 $250,000、客单数 5 位以内）。
  // 原先四列都用 flex 比例 + minWidth，Yoga 会把数字列 minWidth 撑出的差额全部从分店列扣掉，名称只剩两三个字母。
  branchColumn: {
    flex: 1,
    flexShrink: 1,
    minWidth: 0,
  },
  amountColumn: {
    width: 68,
    flexShrink: 0,
  },
  countColumn: {
    width: 46,
    flexShrink: 0,
  },
  averageColumn: {
    width: 56,
    flexShrink: 0,
  },
  chevronColumn: {
    width: 12,
  },
  chevronText: {
    width: 12,
    color: "#64748B",
    fontSize: 25,
    lineHeight: 28,
    textAlign: "center",
  },
  compactGrowthText: {
    fontSize: 11,
    lineHeight: 14,
    fontWeight: "700",
  },
  growthColumn: {
    width: 60,
    minWidth: 0,
  },
  detailTableRow: {
    minHeight: 54,
    gap: 2,
    paddingHorizontal: 4,
    paddingVertical: 5,
  },
  detailRankColumn: {
    width: 23,
    minWidth: 0,
  },
  detailPeriodColumn: {
    // 周/月弹窗保留完整日期和星期缩写，避免日期被压成省略号。
    flex: 1.35,
    minWidth: 0,
  },
  detailAmountColumn: {
    flex: 0.9,
    minWidth: 96,
    flexShrink: 0,
  },
  detailGrowthColumn: {
    width: 54,
    minWidth: 0,
  },
  detailCountColumn: {
    flex: 0.58,
    minWidth: 0,
  },
  tableCellText: {
    color: "#111827",
    fontVariant: ["tabular-nums"],
  },
  numericText: {
    textAlign: "right",
  },
  strongText: {
    fontWeight: "700",
  },
  headerText: {
    color: "#374151",
    fontWeight: "700",
  },
  weekdayText: {
    color: "#2563EB",
    fontWeight: "700",
  },
  listContent: {
    paddingHorizontal: 12,
  },
  tableList: {
    flex: 1,
  },
  stateBox: {
    minHeight: 96,
    alignItems: "center",
    justifyContent: "center",
    gap: 10,
    padding: 16,
  },
  stateText: {
    color: "#64748B",
  },
  bottomSheet: {
    alignSelf: "stretch",
    height: "90%",
    marginTop: "auto",
    marginHorizontal: 0,
    marginBottom: 0,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: "#E2E8F0",
    borderTopLeftRadius: 18,
    borderTopRightRadius: 18,
    paddingHorizontal: 12,
    paddingTop: 6,
    paddingBottom: 16,
    backgroundColor: "#FFFFFF",
  },
  sheetHandle: {
    alignSelf: "center",
    width: 44,
    height: 5,
    marginBottom: 4,
    borderRadius: 3,
    backgroundColor: "#CBD5E1",
  },
  modalList: {
    flex: 1,
    minHeight: 0,
    minWidth: 500,
  },
  detailHorizontalContent: {
    minWidth: 500,
    flexGrow: 1,
  },
  detailHorizontalScroll: {
    flex: 1,
    minHeight: 0,
  },
  modalHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  modalTitleBlock: {
    flex: 1,
    minWidth: 0,
  },
  detailMetaRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 8,
    paddingBottom: 8,
  },
  detailMetaText: {
    color: "#111827",
    fontVariant: ["tabular-nums"],
  },
  detailSectionHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    paddingTop: 10,
    paddingBottom: 6,
  },
  branchPickerSheet: {
    alignSelf: "stretch",
    height: "72%",
    marginTop: "auto",
    marginHorizontal: 0,
    marginBottom: 0,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: "#E2E8F0",
    borderTopLeftRadius: 18,
    borderTopRightRadius: 18,
    paddingHorizontal: 12,
    paddingTop: 6,
    paddingBottom: 16,
    backgroundColor: "#FFFFFF",
  },
  branchPickerList: {
    flex: 1,
    minHeight: 0,
  },
  branchPickerSearchInput: {
    marginBottom: 6,
    backgroundColor: "#FFFFFF",
  },
  branchPickerRow: {
    minHeight: 54,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderBottomWidth: 1,
    borderBottomColor: "#E5E7EB",
    paddingHorizontal: 8,
    backgroundColor: "#FFFFFF",
  },
  branchPickerRowSelected: {
    backgroundColor: "#EFF6FF",
  },
  branchPickerName: {
    flex: 1,
    minWidth: 0,
  },
  branchPickerCheck: {
    width: 20,
    color: "#2563EB",
    fontSize: 18,
    fontWeight: "700",
    textAlign: "right",
  },
  rankingCutoffRow: {
    flexDirection: "row",
    paddingHorizontal: 4,
    paddingBottom: 2,
  },
  rankingCutoffPill: {
    flexShrink: 1,
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    paddingHorizontal: 9,
    paddingVertical: 3,
    borderRadius: 999,
    backgroundColor: "#EAF2FF",
  },
  rankingCutoffText: {
    flexShrink: 1,
    color: "#073B83",
    fontWeight: "600",
  },
  detailListHeader: {
    gap: 8,
  },
  detailChartCard: {
    gap: 6,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    borderRadius: 10,
    paddingHorizontal: 10,
    paddingTop: 8,
    paddingBottom: 10,
    backgroundColor: "#FFFFFF",
  },
  detailChartTitleRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: 8,
  },
  detailChartCaption: {
    flexShrink: 1,
    textAlign: "right",
  },
  detailCutoffRow: {
    backgroundColor: "#EFF6FF",
  },
  detailUpcomingRow: {
    backgroundColor: "#FAFBFC",
  },
  detailLiveValue: {
    color: "#344054",
    fontWeight: "700",
  },
  detailLiveTag: {
    alignSelf: "flex-start",
    marginTop: 2,
    paddingHorizontal: 6,
    borderRadius: 999,
    overflow: "hidden",
    backgroundColor: "#E7F6EC",
    color: "#054F31",
    fontSize: 10,
    lineHeight: 16,
    fontWeight: "700",
  },
  detailFootnote: {
    color: "#6B7280",
    paddingTop: 10,
    paddingHorizontal: 2,
  },
});
