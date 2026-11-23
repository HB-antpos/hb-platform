import { useMemo, useState } from "react";
import { Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Icon, IconButton, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { MOVEMENT_COLORS } from "@/components/product-insights/MovementTrendChart";
import { BleScannerStatusButton } from "@/modules/scanner/ble/BleScannerStatusButton";
import {
  buildDailyTrend,
  buildWeeklyLedger,
  buildWeeklyTrend,
  formatPercent,
  formatQuantity,
  inboundMarkers,
  rangesDiffer,
  sellThroughRate,
  shortDate,
  summarizeDaily,
  unionRange,
} from "@/modules/seasonal-product-insights/logic";
import type {
  SeasonalLookupResult,
  SeasonalProductInsight,
  SeasonalRanges,
  SeasonalTrendMode,
} from "@/modules/seasonal-product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ProductImageBox } from "./ProductImageBox";
import { DailyTrendChart, WeeklyComboChart } from "./SalesTrendChart";
import { SeasonalCandidateList } from "./SeasonalCandidateList";
import { Legend, RangeButton, Segmented } from "./SeasonalInsightParts";
import { SeasonalOverviewCard } from "./SeasonalOverviewCard";
import { SeasonalWeeklyLedger } from "./SeasonalWeeklyLedger";

type Translate = (key: string, values?: Record<string, unknown>) => string;

export interface SeasonalProductInsightsViewProps {
  storeName: string;
  onStorePress?: () => void;
  query: string;
  matchMode: "barcode" | "itemNumber" | null;
  onQueryChange: (value: string) => void;
  onQueryFocus?: () => void;
  onQueryBlur?: () => void;
  onSearch: () => void;
  onScan: () => void;
  data: SeasonalProductInsight | null;
  /** 搜到多个商品时的候选结果：直接在页面里列成进销卡片。 */
  lookup: SeasonalLookupResult | null;
  lookupKeyword: string;
  onSelectCandidate: (productCode: string) => void;
  today: string | null;
  loading: boolean;
  error: string | null;
  emptyState: "initial" | "not-found";
  onRetry?: () => void;
  onBack: () => void;
  onOpenRanges: () => void;
  onAlignRanges: () => void;
}

export function SeasonalProductInsightsView(props: SeasonalProductInsightsViewProps) {
  const { t } = useAppTranslation("seasonalProductInsights");
  const insets = useSafeAreaInsets();
  const { data, lookup, loading, error } = props;

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={t("actions.back")} onPress={props.onBack} />
        <Text variant="titleMedium" style={styles.title} numberOfLines={1}>
          {t("title")}
        </Text>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("actions.selectStore")}
          disabled={!props.onStorePress}
          onPress={props.onStorePress}
          style={styles.storeChip}
        >
          <Icon source="storefront-outline" size={14} color={HB_COLORS.textSecondary} />
          <Text numberOfLines={1} style={styles.storeText}>
            {props.storeName}
          </Text>
          {props.onStorePress ? <Icon source="chevron-down" size={14} color="#667085" /> : null}
        </Pressable>
      </View>
      <View style={styles.searchRow}>
        <View style={styles.searchBox}>
          <Icon source="magnify" size={18} color="#667085" />
          <TextInput
            value={props.query}
            onChangeText={props.onQueryChange}
            onFocus={props.onQueryFocus}
            onBlur={props.onQueryBlur}
            onSubmitEditing={props.onSearch}
            placeholder={t("search.placeholder")}
            placeholderTextColor="#98A2B3"
            returnKeyType="search"
            autoCapitalize="characters"
            autoCorrect={false}
            accessibilityLabel={t("search.placeholder")}
            style={styles.searchInput}
          />
          {props.matchMode ? (
            <Text style={styles.modeBadge}>{t(`search.mode.${props.matchMode}`)}</Text>
          ) : null}
        </View>
        <IconButton
          mode="contained"
          icon="barcode-scan"
          size={20}
          onPress={props.onScan}
          disabled={loading}
          accessibilityLabel={t("actions.scan")}
          containerColor={HB_COLORS.brand}
          iconColor={HB_COLORS.white}
          style={styles.scanButton}
        />
        {/* 蓝牙枪状态：仅配对过 BLE 扫码枪时显示，休眠时点一下即重连。 */}
        <BleScannerStatusButton variant="icon" />
      </View>
      <ScrollView
        contentContainerStyle={[styles.content, { paddingBottom: HB_SPACING.lg + insets.bottom }]}
        keyboardShouldPersistTaps="handled"
      >
        {loading ? (
          <View style={styles.state}>
            <ActivityIndicator color={HB_COLORS.brand} />
            <Text style={styles.stateText}>{t("states.loading")}</Text>
          </View>
        ) : error ? (
          <Outcome icon="alert-circle-outline" title={t("states.loadFailed")} description={error} action={props.onRetry ? t("actions.retry") : undefined} onAction={props.onRetry} />
        ) : data ? (
          <InsightBody data={data} today={props.today} t={t} onOpenRanges={props.onOpenRanges} onAlignRanges={props.onAlignRanges} />
        ) : lookup ? (
          <>
            <RangeRow ranges={lookup.ranges} t={t} onOpenRanges={props.onOpenRanges} />
            <SeasonalCandidateList keyword={props.lookupKeyword} result={lookup} today={props.today} onSelect={props.onSelectCandidate} />
          </>
        ) : (
          <Outcome
            icon={props.emptyState === "not-found" ? "package-variant-closed" : "barcode-scan"}
            title={t(props.emptyState === "not-found" ? "states.notFoundTitle" : "states.initialTitle")}
            description={t(props.emptyState === "not-found" ? "states.notFoundDescription" : "states.initialDescription")}
          />
        )}
      </ScrollView>
    </SafeAreaView>
  );
}

/** 进货区间、销售区间两个按钮；两个区间不一致时进货区间高亮成黄色。 */
function RangeRow({ ranges, t, onOpenRanges }: { ranges: SeasonalRanges; t: Translate; onOpenRanges: () => void }) {
  return (
    <View style={styles.rangeRow}>
      <RangeButton label={t("range.inbound")} range={ranges.inbound} highlighted={rangesDiffer(ranges)} onPress={onOpenRanges} />
      <RangeButton label={t("range.sales")} range={ranges.sales} highlighted={false} onPress={onOpenRanges} />
    </View>
  );
}

function InsightBody({
  data,
  today,
  t,
  onOpenRanges,
  onAlignRanges,
}: {
  data: SeasonalProductInsight;
  today: string | null;
  t: Translate;
  onOpenRanges: () => void;
  onAlignRanges: () => void;
}) {
  // 默认看周视图：进货与销量、周末存货在同一张图里，一眼看出进销节奏。
  const [trendMode, setTrendMode] = useState<SeasonalTrendMode>("week");
  const mismatch = rangesDiffer(data.ranges);
  const product = data.product;
  const caption = [`${t("labels.itemNumber")} ${product.itemNumber ?? "—"}`, product.barcode].filter(Boolean).join(" · ");

  return (
    <>
      <View style={styles.card}>
        <View style={styles.productRow}>
          <ProductImageBox uri={product.productImage} size={72} label={product.productName} caption={caption} introHint />
          <View style={styles.productMeta}>
            <Text style={styles.productName} numberOfLines={2}>
              {product.productName}
            </Text>
            <Text style={styles.meta} numberOfLines={2}>
              {t("labels.itemNumber")} <Text style={styles.metaStrong}>{product.itemNumber ?? "—"}</Text>
              {"   "}
              {t("labels.barcode")} <Text style={styles.metaStrong}>{product.barcode ?? "—"}</Text>
            </Text>
          </View>
        </View>
      </View>

      <RangeRow ranges={data.ranges} t={t} onOpenRanges={onOpenRanges} />
      {mismatch ? (
        <View style={styles.banner}>
          <Icon source="alert-outline" size={16} color={HB_COLORS.warning} />
          <Text style={styles.bannerText}>
            {t("range.mismatchBanner", {
              inbound: shortDate(data.ranges.inbound.startDate),
              sales: shortDate(data.ranges.sales.startDate),
            })}
          </Text>
          <Button compact onPress={onAlignRanges} textColor={HB_COLORS.warning} labelStyle={styles.bannerAction}>
            {t("range.align")}
          </Button>
        </View>
      ) : null}

      <SeasonalOverviewCard data={data} today={today} />
      <TrendCard data={data} today={today} mode={trendMode} onModeChange={setTrendMode} t={t} />
      <SeasonalWeeklyLedger data={data} today={today} />
      <BranchTable data={data} t={t} />
    </>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.stat}>
      <Text style={styles.statLabel} numberOfLines={1}>
        {label}
      </Text>
      <Text style={styles.statValue} numberOfLines={1}>
        {value}
      </Text>
    </View>
  );
}

function TrendCard({
  data,
  today,
  mode,
  onModeChange,
  t,
}: {
  data: SeasonalProductInsight;
  today: string | null;
  mode: SeasonalTrendMode;
  onModeChange: (mode: SeasonalTrendMode) => void;
  t: Translate;
}) {
  const salesRange = data.ranges.sales;
  const differ = rangesDiffer(data.ranges);
  // 周图要同时放下进货与销量两边的记录，按两个区间的并集排周；日图仍只看销售区间。
  const weekRange = useMemo(() => unionRange(data.ranges), [data.ranges]);
  const daily = useMemo(() => buildDailyTrend(data.sales.daily, salesRange), [data.sales.daily, salesRange]);
  const weeks = useMemo(
    () => buildWeeklyTrend(data.sales.daily, data.inbound.records, weekRange),
    [data.inbound.records, data.sales.daily, weekRange],
  );
  // 周末存货折线只在两个区间一致时才成立。
  const stock = useMemo(
    () => (differ ? null : buildWeeklyLedger(data.sales.daily, data.inbound.records, weekRange, true).map((row) => row.endStock ?? 0)),
    [data.inbound.records, data.sales.daily, differ, weekRange],
  );
  const markers = useMemo(() => inboundMarkers(data.inbound.records, salesRange), [data.inbound.records, salesRange]);
  const dailyStats = summarizeDaily(daily.points, daily.dayCount);
  const endIsToday = today != null && salesRange.endDate === today;

  return (
    <View style={styles.card}>
      <View style={styles.cardHead}>
        <Text style={styles.cardTitle}>{t("trend.title")}</Text>
        <Segmented
          label={t("trend.granularity")}
          items={[
            { key: "day", label: t("trend.day") },
            { key: "week", label: t("trend.week") },
          ]}
          value={mode}
          onChange={(value) => onModeChange(value as SeasonalTrendMode)}
        />
      </View>
      <View style={styles.legend}>
        {mode === "day" ? (
          <>
            <Legend color={MOVEMENT_COLORS.salesBar} label={t("trend.legendDaily")} />
            <Legend color={MOVEMENT_COLORS.inBar} label={t("trend.legendInbound")} line="dashed" />
            {endIsToday ? <Legend color="#A6D9BC" label={t("trend.legendToday")} /> : null}
          </>
        ) : (
          <>
            <Legend color={MOVEMENT_COLORS.inBar} label={t("trend.legendWeeklyInboundBar")} />
            <Legend color={MOVEMENT_COLORS.salesBar} label={t("trend.legendWeeklySalesBar")} />
            {stock ? <Legend color="#475467" label={t("trend.legendStockLine")} line="solid" /> : null}
            <Legend color="#A6D9BC" label={t("trend.legendPartialWeek")} />
          </>
        )}
      </View>
      {mode === "day" ? (
        daily.points.length ? (
          <DailyTrendChart
            dayCount={daily.dayCount}
            points={daily.points}
            markers={markers}
            startDate={salesRange.startDate}
            endDate={salesRange.endDate}
            today={today}
            peakDate={dailyStats.peak?.date ?? null}
            endLabel={endIsToday ? t("trend.today") : shortDate(salesRange.endDate)}
            accessibilityLabel={t("trend.dailyA11y", { days: dailyStats.recordedDays })}
          />
        ) : (
          <Text style={styles.emptyChart}>{t("trend.empty")}</Text>
        )
      ) : (
        <WeeklyComboChart
          weeks={weeks}
          stock={stock}
          accessibilityLabel={t("trend.weeklyA11y", { weeks: weeks.length })}
          clipNote={(max) => t("trend.clipNote", { max })}
        />
      )}
      {mode === "day" ? (
        <View style={styles.stats}>
          <Stat label={t("trend.recordedDays")} value={t("trend.recordedDaysValue", { recorded: dailyStats.recordedDays, total: dailyStats.dayCount })} />
          <Stat label={t("trend.dailyAverage")} value={formatQuantity(Math.round(dailyStats.averagePerRecordedDay * 10) / 10)} />
          <Stat
            label={t("trend.dailyPeak")}
            value={dailyStats.peak ? `${shortDate(dailyStats.peak.date)} · ${formatQuantity(dailyStats.peak.quantity)}` : "—"}
          />
        </View>
      ) : null}
    </View>
  );
}

function BranchTable({ data, t }: { data: SeasonalProductInsight; t: Translate }) {
  const rows = data.branches;
  const totals = rows.reduce(
    (sum, row) => ({
      inbound: sum.inbound + row.inboundQuantity,
      sales: sum.sales + row.salesQuantity,
      stock: sum.stock + row.theoreticalStock,
    }),
    { inbound: 0, sales: 0, stock: 0 },
  );
  const totalRate = sellThroughRate(totals.inbound, totals.sales);
  return (
    <View style={styles.card}>
      <View style={styles.cardHeadColumn}>
        <Text style={styles.cardTitle}>{t("branches.title")}</Text>
        <Text style={styles.cardSub}>{t("branches.subtitle")}</Text>
      </View>
      {!rows.length ? (
        <Text style={styles.emptyRows}>{t("branches.empty")}</Text>
      ) : (
        <>
          <View style={styles.tableHead}>
            <Text style={[styles.th, styles.colFlex]}>{t("branches.store")}</Text>
            <Text style={[styles.th, styles.colNum]}>{t("kpi.inbound")}</Text>
            <Text style={[styles.th, styles.colNum]}>{t("kpi.sales")}</Text>
            <Text style={[styles.th, styles.colRate]}>{t("branches.sellThrough")}</Text>
            <Text style={[styles.th, styles.colStock]}>{t("kpi.stock")}</Text>
          </View>
          {rows.map((row) => {
            const rate = sellThroughRate(row.inboundQuantity, row.salesQuantity);
            return (
              <View key={row.storeCode} style={styles.tr} accessible accessibilityLabel={t("branches.rowA11y", {
                store: row.storeName,
                inbound: formatQuantity(row.inboundQuantity),
                sales: formatQuantity(row.salesQuantity),
                stock: formatQuantity(row.theoreticalStock),
              })}>
                <View style={styles.colFlex}>
                  <Text style={styles.branchName} numberOfLines={1}>
                    {row.storeName}
                  </Text>
                  {row.storeName !== row.storeCode ? <Text style={styles.branchCode}>{row.storeCode}</Text> : null}
                </View>
                <Text style={[styles.tdMuted, styles.colNum]}>{formatQuantity(row.inboundQuantity)}</Text>
                <Text style={[styles.tdMuted, styles.colNum]}>{formatQuantity(row.salesQuantity)}</Text>
                <Text style={[styles.tdStrong, styles.colRate, rate != null && rate >= 0.85 ? styles.hotText : null]}>
                  {rate == null ? "—" : formatPercent(rate)}
                </Text>
                <Text style={[styles.tdStrong, styles.colStock, row.theoreticalStock < 0 ? styles.dangerText : null]}>
                  {formatQuantity(row.theoreticalStock)}
                </Text>
              </View>
            );
          })}
          <View style={styles.totalRow}>
            <Text style={[styles.totalLabel, styles.colFlex]}>{t("branches.total", { count: rows.length })}</Text>
            <Text style={[styles.totalValue, styles.colNum]}>{formatQuantity(totals.inbound)}</Text>
            <Text style={[styles.totalValue, styles.colNum]}>{formatQuantity(totals.sales)}</Text>
            <Text style={[styles.totalValue, styles.colRate]}>{totalRate == null ? "—" : formatPercent(totalRate)}</Text>
            <Text style={[styles.tdStrong, styles.colStock]}>{formatQuantity(totals.stock)}</Text>
          </View>
          {rows.some((row) => row.theoreticalStock < 0) ? (
            <Text style={styles.footnote}>{t("branches.negativeHint")}</Text>
          ) : null}
        </>
      )}
    </View>
  );
}

function Outcome({ icon, title, description, action, onAction }: { icon: string; title: string; description: string; action?: string; onAction?: () => void }) {
  return (
    <View style={styles.state}>
      <Icon source={icon} size={36} color="#98A2B3" />
      <Text variant="titleSmall" style={styles.stateTitle}>
        {title}
      </Text>
      <Text style={styles.stateText}>{description}</Text>
      {action && onAction ? <Button onPress={onAction}>{action}</Button> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center", paddingRight: HB_SPACING.sm, backgroundColor: HB_COLORS.white },
  title: { flex: 1, fontWeight: "700", color: HB_COLORS.textPrimary },
  storeChip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    maxWidth: 150,
    minHeight: 32,
    paddingHorizontal: 10,
    borderRadius: 16,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  storeText: { flexShrink: 1, fontSize: 12, color: "#344054" },
  searchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingBottom: 10,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  searchBox: {
    flex: 1,
    minWidth: 0,
    height: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingLeft: HB_SPACING.sm,
    paddingRight: 6,
    borderRadius: 10,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  searchInput: { flex: 1, minWidth: 0, fontSize: 15, color: HB_COLORS.textPrimary, paddingVertical: 0 },
  modeBadge: {
    fontSize: 11,
    color: HB_COLORS.action,
    backgroundColor: HB_COLORS.white,
    borderRadius: 12,
    overflow: "hidden",
    paddingHorizontal: 8,
    paddingVertical: 4,
  },
  scanButton: { margin: 0, width: 44, height: 44, borderRadius: 10 },
  content: { padding: HB_SPACING.sm, gap: 10 },
  card: { borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.white, overflow: "hidden" },
  productRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, padding: HB_SPACING.sm },
  productMeta: { flex: 1, minWidth: 0, gap: 5 },
  productName: { fontSize: 15, fontWeight: "700", lineHeight: 20, color: HB_COLORS.textPrimary },
  meta: { fontSize: 12, color: "#667085" },
  metaStrong: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  rangeRow: { flexDirection: "row", gap: HB_SPACING.xs },
  banner: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingLeft: HB_SPACING.sm,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: "#FEDF89",
    backgroundColor: "#FFFAEB",
  },
  bannerText: { flex: 1, fontSize: 12, lineHeight: 17, color: "#93370D", paddingVertical: HB_SPACING.xs },
  bannerAction: { fontSize: 12, fontWeight: "700" },
  dangerText: { color: HB_COLORS.danger },
  hotText: { color: HB_COLORS.warning },
  cardHead: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingTop: HB_SPACING.sm,
    paddingBottom: HB_SPACING.xs,
  },
  cardHeadColumn: { paddingHorizontal: HB_SPACING.sm, paddingTop: HB_SPACING.sm, paddingBottom: HB_SPACING.xs, gap: 2 },
  cardTitle: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  cardSub: { fontSize: 11, color: "#667085" },
  legend: { flexDirection: "row", flexWrap: "wrap", gap: 12, paddingHorizontal: HB_SPACING.sm, paddingBottom: HB_SPACING.xs },
  emptyChart: { paddingVertical: HB_SPACING.xl, textAlign: "center", fontSize: 12, color: HB_COLORS.textSecondary },
  stats: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    marginHorizontal: HB_SPACING.sm,
    paddingVertical: 10,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#EEF2F7",
  },
  stat: { flex: 1, minWidth: 0, gap: 2 },
  statLabel: { fontSize: 10.5, color: "#667085" },
  statValue: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  tableHead: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
    backgroundColor: "#F9FAFB",
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#EEF2F7",
  },
  th: { fontSize: 11, color: "#667085" },
  tr: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 44,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 8,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#EEF2F7",
  },
  tdMuted: { fontSize: 12.5, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"], textAlign: "right" },
  tdStrong: { fontSize: 13, fontWeight: "800", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], textAlign: "right" },
  colFlex: { flex: 1, minWidth: 0 },
  colNum: { width: 50, textAlign: "right" },
  colRate: { width: 46, textAlign: "right" },
  colStock: { width: 56, textAlign: "right" },
  totalRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 10,
    backgroundColor: "#F9FAFB",
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#EEF2F7",
  },
  totalLabel: { fontSize: 12, color: HB_COLORS.textSecondary },
  totalValue: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"], textAlign: "right" },
  branchName: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary },
  branchCode: { fontSize: 11, color: "#98A2B3" },
  footnote: { fontSize: 11, color: HB_COLORS.textSecondary, paddingHorizontal: HB_SPACING.sm, paddingVertical: HB_SPACING.xs },
  emptyRows: { padding: HB_SPACING.md, textAlign: "center", fontSize: 12, color: HB_COLORS.textSecondary },
  state: { paddingVertical: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center", gap: HB_SPACING.xs },
  stateTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  stateText: { color: HB_COLORS.textSecondary, textAlign: "center", fontSize: 13, lineHeight: 19 },
});
