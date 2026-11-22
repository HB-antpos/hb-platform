import { useMemo, useState } from "react";
import { Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Icon, IconButton, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { BleScannerStatusButton } from "@/modules/scanner/ble/BleScannerStatusButton";
import {
  buildDailyTrend,
  buildWeeklyTrend,
  formatQuantity,
  inboundMarkers,
  rangesDiffer,
  shortDate,
  summarizeDaily,
  summarizeWeekly,
  weekdayIndex,
} from "@/modules/seasonal-product-insights/logic";
import type {
  SeasonalProductInsight,
  SeasonalRanges,
  SeasonalTrendMode,
} from "@/modules/seasonal-product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ProductImageBox } from "./ProductImageBox";
import { DailyTrendChart, WeeklyTrendChart } from "./SalesTrendChart";

type Translate = (key: string, values?: Record<string, unknown>) => string;
type RecordTab = "inbound" | "sales";

const DAILY_PREVIEW_COUNT = 7;

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
  const { data, loading, error } = props;

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
        ) : !data ? (
          <Outcome
            icon={props.emptyState === "not-found" ? "package-variant-closed" : "barcode-scan"}
            title={t(props.emptyState === "not-found" ? "states.notFoundTitle" : "states.initialTitle")}
            description={t(props.emptyState === "not-found" ? "states.notFoundDescription" : "states.initialDescription")}
          />
        ) : (
          <InsightBody data={data} today={props.today} t={t} onOpenRanges={props.onOpenRanges} onAlignRanges={props.onAlignRanges} />
        )}
      </ScrollView>
    </SafeAreaView>
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
  const [trendMode, setTrendMode] = useState<SeasonalTrendMode>("day");
  const [recordTab, setRecordTab] = useState<RecordTab>("inbound");
  const [showAllDays, setShowAllDays] = useState(false);
  const mismatch = rangesDiffer(data.ranges);
  const product = data.product;
  const stockNegative = data.theoreticalStock < 0;
  const sellThrough = data.inbound.quantity > 0 ? (data.sales.quantity / data.inbound.quantity) * 100 : null;

  return (
    <>
      <View style={styles.card}>
        <View style={styles.productRow}>
          <ProductImageBox uri={product.productImage} size={72} label={product.productName} />
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

      <View style={styles.rangeRow}>
        <RangeButton label={t("range.inbound")} range={data.ranges.inbound} highlighted={mismatch} onPress={onOpenRanges} />
        <RangeButton label={t("range.sales")} range={data.ranges.sales} highlighted={false} onPress={onOpenRanges} />
      </View>
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

      <View style={styles.kpis}>
        <Kpi label={t("kpi.inbound")} value={formatQuantity(data.inbound.quantity)} hint={t("kpi.inboundHint", { count: data.inbound.documentCount })} />
        <Kpi
          label={t("kpi.sales")}
          value={formatQuantity(data.sales.quantity)}
          hint={sellThrough == null ? t("kpi.salesHintNoInbound") : t("kpi.salesHint", { percent: sellThrough.toFixed(1) })}
        />
        <Kpi label={t("kpi.stock")} value={formatQuantity(data.theoreticalStock)} hint={t("kpi.stockHint")} tone={stockNegative ? "danger" : "brand"} />
      </View>

      <TrendCard data={data} today={today} mode={trendMode} onModeChange={setTrendMode} t={t} />

      <View style={styles.card}>
        <View style={styles.cardHead}>
          <Text style={styles.cardTitle}>{t("records.title")}</Text>
          <Segmented
            label={t("records.title")}
            items={[
              { key: "inbound", label: t("records.inboundTab", { count: data.inbound.records.length }) },
              { key: "sales", label: t("records.salesTab", { count: data.sales.daily.length }) },
            ]}
            value={recordTab}
            onChange={(value) => setRecordTab(value as RecordTab)}
          />
        </View>
        {recordTab === "inbound" ? (
          <InboundTable data={data} t={t} />
        ) : (
          <DailyList data={data} today={today} showAll={showAllDays} onShowAll={() => setShowAllDays(true)} t={t} />
        )}
      </View>

      <BranchTable data={data} t={t} />
    </>
  );
}

function RangeButton({ label, range, highlighted, onPress }: { label: string; range: SeasonalRanges["sales"]; highlighted: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${label} ${range.startDate} – ${range.endDate}`}
      onPress={onPress}
      style={[styles.rangeButton, highlighted ? styles.rangeButtonChanged : null]}
    >
      <Icon source="calendar-range" size={16} color={highlighted ? HB_COLORS.warning : HB_COLORS.action} />
      <View style={styles.rangeTextBox}>
        <Text style={[styles.rangeLabel, highlighted ? styles.rangeLabelChanged : null]}>{label}</Text>
        <Text style={styles.rangeValue} numberOfLines={1}>
          {shortDate(range.startDate)} – {shortDate(range.endDate)}
        </Text>
      </View>
      <Icon source="chevron-down" size={16} color="#667085" />
    </Pressable>
  );
}

function Kpi({ label, value, hint, tone }: { label: string; value: string; hint: string; tone?: "brand" | "danger" }) {
  return (
    <View style={[styles.kpi, tone === "brand" ? styles.kpiBrand : tone === "danger" ? styles.kpiDanger : null]}>
      <Text style={[styles.kpiLabel, tone === "brand" ? styles.kpiBrandText : tone === "danger" ? styles.dangerText : null]}>{label}</Text>
      <Text
        style={[styles.kpiValue, tone === "brand" ? styles.kpiBrandValue : tone === "danger" ? styles.dangerText : null]}
        numberOfLines={1}
        adjustsFontSizeToFit
      >
        {value}
      </Text>
      <Text style={[styles.kpiHint, tone === "brand" ? styles.kpiBrandText : tone === "danger" ? styles.dangerText : null]} numberOfLines={1}>
        {hint}
      </Text>
    </View>
  );
}

function Segmented({
  label,
  items,
  value,
  onChange,
}: {
  label: string;
  items: { key: string; label: string }[];
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <View style={styles.segmented} accessibilityRole="tablist" accessibilityLabel={label}>
      {items.map((item) => {
        const selected = item.key === value;
        return (
          <Pressable
            key={item.key}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(item.key)}
            style={[styles.segment, selected ? styles.segmentSelected : null]}
          >
            <Text style={[styles.segmentText, selected ? styles.segmentTextSelected : null]}>{item.label}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

function Legend({ color, label, dashed }: { color: string; label: string; dashed?: boolean }) {
  return (
    <View style={styles.legendItem}>
      <View style={dashed ? [styles.legendDash, { borderTopColor: color }] : [styles.legendDot, { backgroundColor: color }]} />
      <Text style={styles.legendText}>{label}</Text>
    </View>
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
  const daily = useMemo(() => buildDailyTrend(data.sales.daily, salesRange), [data.sales.daily, salesRange]);
  const weeks = useMemo(
    () => buildWeeklyTrend(data.sales.daily, data.inbound.records, salesRange),
    [data.inbound.records, data.sales.daily, salesRange],
  );
  const markers = useMemo(() => inboundMarkers(data.inbound.records, salesRange), [data.inbound.records, salesRange]);
  const dailyStats = summarizeDaily(daily.points, daily.dayCount);
  const weeklyStats = summarizeWeekly(weeks);
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
        <Legend color="#1677FF" label={t(mode === "day" ? "trend.legendDaily" : "trend.legendWeekly")} />
        {mode === "day" ? <Legend color="#067647" label={t("trend.legendInbound")} dashed /> : <Legend color="#067647" label={t("trend.legendWeeklyInbound")} />}
        {mode === "day" && endIsToday ? <Legend color="#9CC2FF" label={t("trend.legendToday")} /> : null}
        {mode === "week" ? <Legend color="#9CC2FF" label={t("trend.legendPartialWeek")} /> : null}
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
        <WeeklyTrendChart weeks={weeks} accessibilityLabel={t("trend.weeklyA11y", { weeks: weeks.length })} />
      )}
      <View style={styles.stats}>
        {mode === "day" ? (
          <>
            <Stat label={t("trend.recordedDays")} value={t("trend.recordedDaysValue", { recorded: dailyStats.recordedDays, total: dailyStats.dayCount })} />
            <Stat label={t("trend.dailyAverage")} value={formatQuantity(Math.round(dailyStats.averagePerRecordedDay * 10) / 10)} />
            <Stat
              label={t("trend.dailyPeak")}
              value={dailyStats.peak ? `${shortDate(dailyStats.peak.date)} · ${formatQuantity(dailyStats.peak.quantity)}` : "—"}
            />
          </>
        ) : (
          <>
            <Stat
              label={t("trend.weeklyAverage")}
              value={weeklyStats.averagePerFullWeek == null ? "—" : formatQuantity(Math.round(weeklyStats.averagePerFullWeek))}
            />
            <Stat
              label={t("trend.weeklyBest")}
              value={weeklyStats.best ? `${shortDate(weeklyStats.best.startDate)} · ${formatQuantity(weeklyStats.best.quantity)}` : "—"}
            />
            <Stat
              label={t("trend.weekOverWeek")}
              value={weeklyStats.weekOverWeek == null ? "—" : `${weeklyStats.weekOverWeek > 0 ? "+" : ""}${Math.round(weeklyStats.weekOverWeek * 100)}%`}
            />
          </>
        )}
      </View>
    </View>
  );
}

function InboundTable({ data, t }: { data: SeasonalProductInsight; t: Translate }) {
  const records = data.inbound.records;
  if (!records.length) return <Text style={styles.emptyRows}>{t("records.inboundEmpty")}</Text>;
  return (
    <>
      <View style={styles.tableHead}>
        <Text style={[styles.th, styles.colDate]}>{t("records.date")}</Text>
        <Text style={[styles.th, styles.colFlex]}>{t(data.product.sourceType === "warehouse" ? "records.warehouseDocument" : "records.invoice")}</Text>
        <Text style={[styles.th, styles.colQty]}>{t("records.quantity")}</Text>
      </View>
      {records.map((record) => (
        <View key={`${record.id}:${record.date}`} style={styles.tr}>
          <Text style={[styles.td, styles.colDate]}>
            {shortDate(record.date)} <Text style={styles.weekday}>{t(`weekdays.${weekdayIndex(record.date)}`)}</Text>
          </Text>
          <Text style={[styles.tdMuted, styles.colFlex]} numberOfLines={1}>
            {record.documentNo}
          </Text>
          <Text style={[styles.tdStrong, styles.colQty, styles.inboundText]}>+{formatQuantity(record.quantity)}</Text>
        </View>
      ))}
      <View style={styles.totalRow}>
        <Text style={[styles.totalLabel, styles.colFlex]}>{t("records.inboundTotal")}</Text>
        <Text style={[styles.tdStrong, styles.colQty]}>{formatQuantity(data.inbound.quantity)}</Text>
      </View>
    </>
  );
}

function DailyList({
  data,
  today,
  showAll,
  onShowAll,
  t,
}: {
  data: SeasonalProductInsight;
  today: string | null;
  showAll: boolean;
  onShowAll: () => void;
  t: Translate;
}) {
  const rows = [...data.sales.daily].reverse();
  if (!rows.length) return <Text style={styles.emptyRows}>{t("records.salesEmpty")}</Text>;
  const peak = Math.max(...rows.map((row) => row.quantity), 1);
  const visible = showAll ? rows : rows.slice(0, DAILY_PREVIEW_COUNT);
  return (
    <>
      <Text style={styles.listHint}>{t("records.salesHint")}</Text>
      {visible.map((row) => {
        const date = row.date.slice(0, 10);
        const isToday = date === today;
        return (
          <View key={date} style={styles.tr}>
            <View style={styles.colDate}>
              <Text style={styles.td}>{shortDate(date)}</Text>
              <Text style={[styles.weekday, isToday ? styles.todayText : null]}>
                {t(`weekdays.${weekdayIndex(date)}`)}
                {isToday ? ` · ${t("records.todayOpen")}` : ""}
              </Text>
            </View>
            <View style={styles.dailyTrack}>
              <View style={[styles.dailyBar, { width: `${Math.max(2, (row.quantity / peak) * 100)}%` }, isToday ? styles.dailyBarToday : null]} />
            </View>
            <Text style={[styles.tdStrong, styles.colQty]}>{formatQuantity(row.quantity)}</Text>
          </View>
        );
      })}
      {!showAll && rows.length > DAILY_PREVIEW_COUNT ? (
        <Button onPress={onShowAll} icon="chevron-down" contentStyle={styles.moreButton}>
          {t("records.showAll", { count: rows.length })}
        </Button>
      ) : null}
    </>
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
            <Text style={[styles.th, styles.colStock]}>{t("kpi.stock")}</Text>
          </View>
          {rows.map((row) => (
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
              <Text style={[styles.tdStrong, styles.colStock, row.theoreticalStock < 0 ? styles.dangerText : null]}>
                {formatQuantity(row.theoreticalStock)}
              </Text>
            </View>
          ))}
          <View style={styles.totalRow}>
            <Text style={[styles.totalLabel, styles.colFlex]}>{t("branches.total", { count: rows.length })}</Text>
            <Text style={[styles.totalValue, styles.colNum]}>{formatQuantity(totals.inbound)}</Text>
            <Text style={[styles.totalValue, styles.colNum]}>{formatQuantity(totals.sales)}</Text>
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
  rangeButton: {
    flex: 1,
    minWidth: 0,
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: 10,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  rangeButtonChanged: { borderColor: "#F79009", backgroundColor: "#FFFAEB" },
  rangeTextBox: { flex: 1, minWidth: 0, gap: 1 },
  rangeLabel: { fontSize: 10.5, color: "#667085" },
  rangeLabelChanged: { color: HB_COLORS.warning },
  rangeValue: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
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
  kpis: { flexDirection: "row", gap: HB_SPACING.xs },
  kpi: { flex: 1, minWidth: 0, padding: 10, borderRadius: 10, backgroundColor: HB_COLORS.white, gap: 2 },
  kpiBrand: { backgroundColor: "#EAF2FF" },
  kpiDanger: { backgroundColor: "#FEF3F2" },
  kpiLabel: { fontSize: 11, color: "#667085" },
  kpiValue: { fontSize: 20, fontWeight: "800", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  kpiHint: { fontSize: 10.5, color: "#98A2B3" },
  kpiBrandText: { color: HB_COLORS.action },
  kpiBrandValue: { color: "#0C447C" },
  dangerText: { color: HB_COLORS.danger },
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
  segmented: { flexDirection: "row", padding: 2, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  segment: { minHeight: 30, minWidth: 44, paddingHorizontal: 12, borderRadius: 6, alignItems: "center", justifyContent: "center" },
  segmentSelected: { backgroundColor: HB_COLORS.white, elevation: 1 },
  segmentText: { fontSize: 12, color: "#667085" },
  segmentTextSelected: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  legend: { flexDirection: "row", flexWrap: "wrap", gap: 12, paddingHorizontal: HB_SPACING.sm, paddingBottom: HB_SPACING.xs },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 5 },
  legendDot: { width: 8, height: 8, borderRadius: 2 },
  legendDash: { width: 10, borderTopWidth: 1.5, borderStyle: "dashed" },
  legendText: { fontSize: 11, color: HB_COLORS.textSecondary },
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
  td: { fontSize: 13, color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  tdMuted: { fontSize: 12.5, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"], textAlign: "right" },
  tdStrong: { fontSize: 13, fontWeight: "800", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], textAlign: "right" },
  inboundText: { color: HB_COLORS.success },
  weekday: { fontSize: 11, color: "#98A2B3" },
  todayText: { color: HB_COLORS.action },
  colDate: { width: 76 },
  colFlex: { flex: 1, minWidth: 0 },
  colQty: { width: 68, textAlign: "right" },
  colNum: { width: 58, textAlign: "right" },
  colStock: { width: 66, textAlign: "right" },
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
  totalValue: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  listHint: { fontSize: 11, color: "#98A2B3", paddingHorizontal: HB_SPACING.sm, paddingBottom: 6 },
  dailyTrack: { flex: 1, height: 6, borderRadius: 3, backgroundColor: HB_COLORS.surfaceMuted, overflow: "hidden" },
  dailyBar: { height: 6, borderRadius: 3, backgroundColor: HB_COLORS.brand },
  dailyBarToday: { backgroundColor: "#9CC2FF" },
  moreButton: { minHeight: 44 },
  branchName: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary },
  branchCode: { fontSize: 11, color: "#98A2B3" },
  footnote: { fontSize: 11, color: HB_COLORS.textSecondary, paddingHorizontal: HB_SPACING.sm, paddingVertical: HB_SPACING.xs },
  emptyRows: { padding: HB_SPACING.md, textAlign: "center", fontSize: 12, color: HB_COLORS.textSecondary },
  state: { paddingVertical: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center", gap: HB_SPACING.xs },
  stateTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  stateText: { color: HB_COLORS.textSecondary, textAlign: "center", fontSize: 13, lineHeight: 19 },
});
