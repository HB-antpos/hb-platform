import { useEffect, useMemo, useRef, useState } from "react";
import {
  Image,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from "react-native";
import {
  ActivityIndicator,
  Button,
  Icon,
  IconButton,
  Searchbar,
  Text,
} from "react-native-paper";
import {
  SafeAreaView,
  useSafeAreaInsets,
} from "react-native-safe-area-context";
import { DateRangePickerModal } from "@/components/ui/DateRangePickerModal";
import { isValidProductInsightRange } from "@/modules/product-insights/logic";
import {
  buildDailyFlow,
  buildMovementSummary,
  buildMovementTrend,
  buildPresetRange,
  buildPurchaseBatches,
  countRangeDays,
  DEFAULT_PRODUCT_INSIGHT_PRESET,
  PRODUCT_INSIGHT_PRESET_DAYS,
  type ProductInsightPeriod,
  type ProductMovementBatch,
  type ProductMovementFlowEntry,
  type ProductMovementFlowFilter,
  type ProductMovementFlowMonth,
  type ProductMovementSummary,
} from "@/modules/product-insights/movement-summary";
import type {
  ProductInsightMovement,
  ProductInsightOrder,
  StoreProductInsight,
} from "@/modules/product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  MOVEMENT_COLORS,
  MovementTrendChart,
  type MovementTrendMode,
} from "./MovementTrendChart";

export interface ProductInsightsViewProps {
  query: string;
  onQueryChange: (value: string) => void;
  /** 手输聚焦状态交给页面编排层处理 HID 扫码焦点。 */
  onQueryFocus?: () => void;
  onQueryBlur?: () => void;
  onSearch: () => void;
  onScan: () => void;
  store: { storeCode: string; storeName: string };
  onStorePress?: () => void;
  data: StoreProductInsight | null;
  loading?: boolean;
  /** 切换周期时保留当前内容，只在周期条上显示加载。 */
  periodLoading?: boolean;
  period: ProductInsightPeriod;
  /** 门店「今天」，用于自定义日历的「恢复默认」；未知时不提供该按钮。 */
  presetEndDate: string | null;
  onPeriodChange: (period: ProductInsightPeriod) => void;
  error?: string | null;
  onRetry?: () => void;
  onBack: () => void;
  canViewBranchSales: boolean;
  onOpenBranchSales: () => void;
  onDetailVisibilityChange?: (visible: boolean) => void;
  emptyState?: "initial" | "not-found";
}

type Translate = (key: string, values?: Record<string, unknown>) => string;
const AUTO_EXPAND_SALES_DAYS = 10;
type FlowView = "batches" | "daily";
type DetailItem =
  | { kind: "movements"; items: ProductInsightMovement[] }
  | { kind: "order"; item: ProductInsightOrder };

const money = (value: number) =>
  `A$${value.toLocaleString("en-AU", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
const quantity = (value: number) =>
  value.toLocaleString("en-AU", { maximumFractionDigits: 2 });
const signed = (value: number) =>
  `${value > 0 ? "+" : value < 0 ? "−" : ""}${quantity(Math.abs(value))}`;
const percent = (value: number) => `${(value * 100).toFixed(1)}%`;

function dateLabel(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return match ? `${match[3]}/${match[2]}/${match[1]}` : "—";
}

function shortDateLabel(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return match ? `${match[3]}/${match[2]}` : "—";
}

function timestampLabel(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2})/.exec(value);
  return match
    ? `${match[3]}/${match[2]}/${match[1]} ${match[4]}:${match[5]}`
    : dateLabel(value);
}

/** 业务日按本地日历构造，再格式化星期 / 月份，不受设备时区换算影响。 */
function localDate(day: string) {
  return new Date(
    Number(day.slice(0, 4)),
    Number(day.slice(5, 7)) - 1,
    Number(day.slice(8, 10)),
  );
}

function useDateFormatters(localeTag: string) {
  return useMemo(() => {
    const weekday = new Intl.DateTimeFormat(localeTag, { weekday: "short" });
    const month = new Intl.DateTimeFormat(localeTag, {
      year: "numeric",
      month: "long",
    });
    const narrow = new Intl.DateTimeFormat(localeTag, { weekday: "narrow" });
    return {
      dayWithWeekday: (day: string) =>
        `${shortDateLabel(day)} ${weekday.format(localDate(day))}`,
      month: (key: string) => month.format(localDate(`${key}-01`)),
      // 2026-01-05 是周一：依次取周一到周日的窄星期名给日历用。
      weekdayLabels: Array.from({ length: 7 }, (_, index) =>
        narrow.format(new Date(2026, 0, 5 + index)),
      ),
    };
  }, [localeTag]);
}

export function ProductInsightsView({
  query,
  onQueryChange,
  onQueryFocus,
  onQueryBlur,
  onSearch,
  onScan,
  store,
  onStorePress,
  data,
  loading = false,
  periodLoading = false,
  period,
  presetEndDate,
  onPeriodChange,
  error,
  onRetry,
  onBack,
  canViewBranchSales,
  onOpenBranchSales,
  onDetailVisibilityChange,
  emptyState = "initial",
}: ProductInsightsViewProps) {
  const { t, language } = useAppTranslation("productInsights");
  const localeTag = resolveLocaleTag(language);
  const formatters = useDateFormatters(localeTag);
  const insets = useSafeAreaInsets();
  const [detail, setDetail] = useState<DetailItem | null>(null);
  const [calendarVisible, setCalendarVisible] = useState(false);
  const [trendMode, setTrendMode] = useState<MovementTrendMode>("buckets");
  const [flowView, setFlowView] = useState<FlowView>("batches");
  const [flowFilter, setFlowFilter] = useState<ProductMovementFlowFilter>("all");
  const [stickyVisible, setStickyVisible] = useState(false);
  const summaryBottom = useRef(Number.POSITIVE_INFINITY);

  const summary = useMemo(() => (data ? buildMovementSummary(data) : null), [data]);
  const trend = useMemo(() => (data ? buildMovementTrend(data) : null), [data]);
  const batches = useMemo(() => (data ? buildPurchaseBatches(data) : []), [data]);
  const months = useMemo(
    () => (data ? buildDailyFlow(data, flowFilter) : []),
    [data, flowFilter],
  );
  // 换商品或换周期时重置弹窗与展开状态；最新一批销售日不多时默认展开，
  // 太长则折叠，避免把其他批次挤到很下面。
  const dataKey = data
    ? `${data.product.productCode}:${data.sourceType}:${data.range.startDate}:${data.range.endDate}`
    : "";
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  useEffect(() => {
    setDetail(null);
    setExpanded(
      new Set(
        batches
          .filter((item) => item.latest && item.sales.length <= AUTO_EXPAND_SALES_DAYS)
          .map((item) => item.key),
      ),
    );
    // batches 由 data 推导，dataKey 变化即代表批次已重算。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dataKey]);
  useEffect(() => {
    if (!data) setStickyVisible(false);
  }, [data]);
  useEffect(() => {
    onDetailVisibilityChange?.(Boolean(detail) || calendarVisible);
  }, [calendarVisible, detail, onDetailVisibilityChange]);

  const onScroll = (event: NativeSyntheticEvent<NativeScrollEvent>) => {
    // 汇总卡完全滚出视口后，顶部吸附一行迷你汇总。
    const next = event.nativeEvent.contentOffset.y > summaryBottom.current;
    if (next !== stickyVisible) setStickyVisible(next);
  };
  const warehouse = data?.sourceType === "warehouse";

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.screen}>
        <View style={styles.header}>
          <IconButton
            icon="chevron-left"
            accessibilityLabel={t("actions.back")}
            onPress={onBack}
          />
          <Text variant="titleMedium" style={styles.title}>
            {t("title")}
          </Text>
        </View>
        <Pressable
          onPress={onStorePress}
          disabled={!onStorePress}
          style={styles.storeBar}
          accessibilityRole={onStorePress ? "button" : undefined}
        >
          <Icon source="store-outline" size={17} color={HB_COLORS.action} />
          <Text numberOfLines={1} style={styles.storeText}>
            {t("store", { store: store.storeName, code: store.storeCode })}
          </Text>
          {onStorePress ? (
            <Icon source="chevron-down" size={18} color={HB_COLORS.action} />
          ) : null}
        </Pressable>
        <View style={styles.searchRow}>
          <Searchbar
            placeholder={t("search.placeholder")}
            value={query}
            multiline={false}
            numberOfLines={1}
            onChangeText={onQueryChange}
            onFocus={onQueryFocus}
            onBlur={onQueryBlur}
            onSubmitEditing={onSearch}
            style={styles.search}
            inputStyle={styles.searchInput}
          />
          <IconButton
            mode="contained"
            icon="magnify"
            onPress={onSearch}
            disabled={loading}
            accessibilityLabel={t("actions.search")}
            style={styles.searchAction}
          />
          <IconButton
            mode="contained-tonal"
            icon="barcode-scan"
            onPress={onScan}
            disabled={loading}
            accessibilityLabel={t("actions.scan")}
            style={styles.searchAction}
          />
        </View>
        <View style={styles.body}>
          <ScrollView
            contentContainerStyle={[
              styles.content,
              { paddingBottom: 88 + insets.bottom },
            ]}
            keyboardShouldPersistTaps="handled"
            onScroll={onScroll}
            scrollEventThrottle={32}
          >
            {loading ? (
              <Loading />
            ) : error ? (
              <Outcome
                icon="alert-circle-outline"
                title={t("states.loadFailed")}
                description={error}
                action={onRetry ? t("actions.retry") : undefined}
                onAction={onRetry}
              />
            ) : !data || !summary || !trend ? (
              <Outcome
                icon={
                  emptyState === "not-found"
                    ? "package-variant-closed"
                    : "barcode-search"
                }
                title={
                  emptyState === "not-found"
                    ? t("states.notFoundTitle")
                    : t("states.initialTitle")
                }
                description={
                  emptyState === "not-found"
                    ? t("states.notFoundDescription")
                    : t("states.initialDescription")
                }
              />
            ) : (
              <>
                <ProductHeader data={data} t={t} />
                <PeriodBar
                  data={data}
                  period={period}
                  loading={periodLoading}
                  onPreset={(days) => onPeriodChange({ kind: "preset", days })}
                  onCustom={() => setCalendarVisible(true)}
                  t={t}
                />
                <View
                  style={periodLoading && styles.dimmed}
                  onLayout={(event) => {
                    const { y, height } = event.nativeEvent.layout;
                    summaryBottom.current = y + height;
                  }}
                >
                  <SummaryCard data={data} summary={summary} t={t} />
                </View>
                <View style={[styles.stack, periodLoading && styles.dimmed]}>
                  {!warehouse && data.purchases.records.length === 0 ? (
                    <HistoryNotice record={data.purchases.lastRecord} t={t} />
                  ) : null}
                  {warehouse && data.warehouse.deliveries.length === 0 ? (
                    <DeliveryHint record={data.warehouse.lastDelivery} t={t} />
                  ) : null}
                  {warehouse && summary.pendingQuantity > 0 ? (
                    <View style={styles.warning}>
                      <Icon source="alert-outline" size={18} color={HB_COLORS.warning} />
                      <Text style={styles.warningText}>
                        {t("summary.pending", {
                          quantity: quantity(summary.pendingQuantity),
                          count: summary.pendingOrderCount,
                          date: summary.earliestPendingDate
                            ? shortDateLabel(summary.earliestPendingDate)
                            : "—",
                        })}
                      </Text>
                    </View>
                  ) : null}
                  <TrendCard
                    data={data}
                    trend={trend}
                    mode={trendMode}
                    onModeChange={setTrendMode}
                    t={t}
                  />
                  <View style={styles.flowHead}>
                    <Text variant="titleSmall" style={styles.sectionTitle}>
                      {t("flow.title")}
                    </Text>
                    <Text variant="labelSmall" style={styles.count}>
                      {t(warehouse ? "flow.countWarehouse" : "flow.countLocal", {
                        count: batches.filter((item) => item.date).length,
                        days: summary.activeSalesDays,
                      })}
                    </Text>
                  </View>
                  <View style={styles.chips}>
                    <ChipButton
                      label={t(warehouse ? "flow.byBatchWarehouse" : "flow.byBatch")}
                      selected={flowView === "batches"}
                      onPress={() => setFlowView("batches")}
                    />
                    <ChipButton
                      label={t("flow.byDate")}
                      selected={flowView === "daily"}
                      onPress={() => setFlowView("daily")}
                    />
                  </View>
                  {flowView === "daily" ? (
                    <View style={styles.chips}>
                      {(["all", "in", "sales"] as const).map((value) => (
                        <ChipButton
                          key={value}
                          small
                          label={t(
                            value === "in" && warehouse
                              ? "flow.filter.inWarehouse"
                              : `flow.filter.${value}`,
                          )}
                          selected={flowFilter === value}
                          onPress={() => setFlowFilter(value)}
                        />
                      ))}
                    </View>
                  ) : null}
                  {flowView === "batches" ? (
                    <BatchList
                      batches={batches}
                      warehouse={warehouse}
                      expanded={expanded}
                      onToggle={(key) =>
                        setExpanded((current) => {
                          const next = new Set(current);
                          if (next.has(key)) next.delete(key);
                          else next.add(key);
                          return next;
                        })
                      }
                      onOpen={(items) => setDetail({ kind: "movements", items })}
                      formatDay={formatters.dayWithWeekday}
                      t={t}
                    />
                  ) : (
                    <DailyFlow
                      months={months}
                      warehouse={warehouse}
                      filter={flowFilter}
                      onOpen={setDetail}
                      formatDay={formatters.dayWithWeekday}
                      formatMonth={formatters.month}
                      t={t}
                    />
                  )}
                  <Text style={styles.updatedAt}>
                    {data.salesStatisticLastUpdatedAt
                      ? t("salesUpdatedAt", {
                          value: timestampLabel(data.salesStatisticLastUpdatedAt),
                        })
                      : t("salesUpdatedUnavailable")}
                  </Text>
                </View>
              </>
            )}
          </ScrollView>
          {data && summary && stickyVisible && !loading && !error ? (
            <View style={styles.sticky} pointerEvents="none">
              <Text numberOfLines={1} style={styles.stickyCode}>
                {data.product.itemNumber || data.product.productCode}
              </Text>
              <View style={styles.stickyDivider} />
              <Text style={[styles.stickyText, { color: MOVEMENT_COLORS.in }]}>
                {t("sticky.in", { value: quantity(summary.inQuantity) })}
              </Text>
              <Text style={[styles.stickyText, { color: MOVEMENT_COLORS.sales }]}>
                {t("sticky.sales", { value: quantity(summary.salesQuantity) })}
              </Text>
              {summary.sellThroughRate !== null ? (
                <Text style={styles.stickyText}>
                  {t("sticky.sellThrough", {
                    value: `${Math.round(summary.sellThroughRate * 100)}%`,
                  })}
                </Text>
              ) : null}
              <Text style={styles.stickyText}>
                {t("sticky.net", { value: signed(summary.netQuantity) })}
              </Text>
            </View>
          ) : null}
        </View>
        {data && canViewBranchSales ? (
          <View
            style={[
              styles.footer,
              { paddingBottom: Math.max(HB_SPACING.sm, insets.bottom) },
            ]}
          >
            <Button
              mode="contained"
              icon="chart-bar"
              onPress={onOpenBranchSales}
              contentStyle={styles.branchButton}
            >
              {t("actions.openBranchSales")}
            </Button>
          </View>
        ) : null}
        <DetailModal item={detail} onClose={() => setDetail(null)} t={t} />
        <DateRangePickerModal
          visible={calendarVisible}
          title={t("period.pickerTitle")}
          value={
            data
              ? { from: data.range.startDate, to: data.range.endDate }
              : {}
          }
          onApply={(next) => {
            setCalendarVisible(false);
            const range = { startDate: next.from ?? "", endDate: next.to ?? next.from ?? "" };
            if (isValidProductInsightRange(range))
              onPeriodChange({ kind: "custom", range });
          }}
          onCancel={() => setCalendarVisible(false)}
          cancelLabel={t("common:actions.cancel")}
          clearLabel={t("common:actions.clear")}
          applyLabel={t("common:actions.apply")}
          weekdayLabels={formatters.weekdayLabels}
          localeTag={localeTag}
          presetRange={
            presetEndDate
              ? {
                  label: t("period.reset"),
                  value: (() => {
                    const range = buildPresetRange(
                      presetEndDate,
                      DEFAULT_PRODUCT_INSIGHT_PRESET,
                    );
                    return { from: range.startDate, to: range.endDate };
                  })(),
                }
              : undefined
          }
        />
      </View>
    </SafeAreaView>
  );
}

function ProductHeader({ data, t }: { data: StoreProductInsight; t: Translate }) {
  const p = data.product;
  const warehouse = data.sourceType === "warehouse";
  return (
    <View style={styles.product}>
      <View style={styles.imageWrap}>
        {p.productImage ? (
          <Image source={{ uri: p.productImage }} style={styles.image} />
        ) : (
          <Icon source="package-variant" size={28} color="#98A2B3" />
        )}
      </View>
      <View style={styles.productMeta}>
        <Text variant="titleSmall" numberOfLines={2} style={styles.productName}>
          {p.productName}
        </Text>
        <View style={styles.metaRow}>
          <View style={[styles.sourceTag, warehouse && styles.sourceTagWarehouse]}>
            <Text style={[styles.sourceText, warehouse && styles.sourceTextWarehouse]}>
              {t(warehouse ? "source.warehouse" : "source.local")}
            </Text>
          </View>
          <Text numberOfLines={1} style={styles.meta}>
            {t("labels.itemNumber")} {p.itemNumber ?? "—"}
            {p.barcode ? ` · ${p.barcode}` : ""}
          </Text>
        </View>
        {!warehouse ? (
          <Text numberOfLines={1} style={styles.meta}>
            {t("labels.supplier")} {p.localSupplierName ?? p.localSupplierCode ?? "—"}
          </Text>
        ) : null}
      </View>
    </View>
  );
}

function PeriodBar({
  data,
  period,
  loading,
  onPreset,
  onCustom,
  t,
}: {
  data: StoreProductInsight;
  period: ProductInsightPeriod;
  loading: boolean;
  onPreset: (days: (typeof PRODUCT_INSIGHT_PRESET_DAYS)[number]) => void;
  onCustom: () => void;
  t: Translate;
}) {
  return (
    <View style={styles.periodBlock}>
      <View style={styles.periodRow}>
        <View style={styles.segment} accessibilityRole="tablist">
          {PRODUCT_INSIGHT_PRESET_DAYS.map((days) => {
            const selected = period.kind === "preset" && period.days === days;
            return (
              <Pressable
                key={days}
                onPress={() => onPreset(days)}
                disabled={loading || selected}
                accessibilityRole="tab"
                accessibilityState={{ selected }}
                style={[styles.segmentItem, selected && styles.segmentItemOn]}
              >
                <Text style={[styles.segmentText, selected && styles.segmentTextOn]}>
                  {t("period.days", { count: days })}
                </Text>
              </Pressable>
            );
          })}
        </View>
        <Pressable
          onPress={onCustom}
          disabled={loading}
          accessibilityRole="button"
          accessibilityLabel={t("period.custom")}
          style={[styles.calendar, period.kind === "custom" && styles.calendarOn]}
        >
          <Icon
            source="calendar-month-outline"
            size={18}
            color={period.kind === "custom" ? HB_COLORS.action : HB_COLORS.textSecondary}
          />
        </Pressable>
      </View>
      <View style={styles.rangeRow}>
        {loading ? <ActivityIndicator size={12} color={HB_COLORS.action} /> : null}
        <Text style={styles.rangeText}>
          {t("period.range", {
            startDate: dateLabel(data.range.startDate),
            endDate: dateLabel(data.range.endDate),
            count: countRangeDays(data.range),
          })}
        </Text>
        <Text style={styles.generated}>
          {t("generatedAt", { value: timestampLabel(data.generatedAt) })}
        </Text>
      </View>
    </View>
  );
}

function SummaryCard({
  data,
  summary,
  t,
}: {
  data: StoreProductInsight;
  summary: ProductMovementSummary;
  t: Translate;
}) {
  const warehouse = data.sourceType === "warehouse";
  const unit = t("units.items");
  const rate = summary.sellThroughRate;
  const lastIn =
    summary.daysSinceLastIn === null
      ? t(warehouse ? "summary.lastAllocationNone" : "summary.lastInNone")
      : summary.lastInIsHistorical
        ? t("summary.lastInHistorical", { date: dateLabel(summary.lastInDate ?? "") })
        : summary.daysSinceLastIn === 0
          ? t(warehouse ? "summary.lastAllocationToday" : "summary.lastInToday")
          : t(warehouse ? "summary.lastAllocationDays" : "summary.lastInDays", {
              count: summary.daysSinceLastIn,
            });
  return (
    <View style={styles.card}>
      <View style={styles.cardHead}>
        <Text style={styles.cardTitle}>{t("summary.title")}</Text>
        <Text numberOfLines={1} style={styles.cardScope}>
          {t(warehouse ? "summary.scopeWarehouse" : "summary.scopeLocal")}
        </Text>
      </View>
      <View style={styles.duo}>
        <View style={styles.side}>
          <View style={styles.sideLabel}>
            <Icon
              source={warehouse ? "truck-delivery-outline" : "package-down"}
              size={15}
              color={MOVEMENT_COLORS.in}
            />
            <Text style={[styles.sideLabelText, { color: MOVEMENT_COLORS.in }]}>
              {t(warehouse ? "summary.inWarehouse" : "summary.inLocal")}
            </Text>
          </View>
          <Text style={styles.big}>
            {quantity(summary.inQuantity)}
            <Text style={styles.bigUnit}> {unit}</Text>
          </Text>
          <Text numberOfLines={2} style={styles.sideSub}>
            {warehouse
              ? t("summary.inSubWarehouse", {
                  ordered: quantity(summary.orderedQuantity),
                  pending: quantity(summary.pendingQuantity),
                })
              : t("summary.inSubLocal", { count: summary.inDocumentCount })}
          </Text>
        </View>
        <View style={styles.duoDivider} />
        <View style={[styles.side, styles.sideRight]}>
          <View style={styles.sideLabel}>
            <Icon source="shopping-outline" size={15} color={MOVEMENT_COLORS.sales} />
            <Text style={[styles.sideLabelText, { color: MOVEMENT_COLORS.sales }]}>
              {t("summary.sales")}
            </Text>
          </View>
          <Text style={styles.big}>
            {quantity(summary.salesQuantity)}
            <Text style={styles.bigUnit}> {unit}</Text>
          </Text>
          <Text numberOfLines={2} style={styles.sideSub}>
            {t("summary.salesSub", {
              amount: money(summary.salesAmount),
              count: summary.activeSalesDays,
            })}
          </Text>
        </View>
      </View>
      <View style={styles.ratio}>
        <View style={styles.ratioTrack}>
          <View
            style={[
              styles.ratioFill,
              { width: `${Math.min(100, (rate ?? 0) * 100)}%` },
              rate !== null && rate > 1 && styles.ratioFillOver,
            ]}
          />
        </View>
        <View style={styles.ratioCaption}>
          <Text style={styles.ratioText}>
            {rate === null
              ? t("summary.sellThroughNone")
              : t("summary.sellThroughCaption", {
                  rate: percent(rate),
                  sales: quantity(summary.salesQuantity),
                  in: quantity(summary.inQuantity),
                })}
          </Text>
          <Text style={styles.ratioText}>
            {lastIn}
          </Text>
        </View>
      </View>
      <View style={styles.kpis}>
        {warehouse ? (
          <>
            <Kpi
              label={t("kpi.dailyAverage")}
              value={`${summary.dailyAverage.toFixed(2)} ${unit}`}
            />
            <Kpi
              label={t("kpi.fulfillment")}
              value={
                summary.fulfillmentRate === null ? "—" : percent(summary.fulfillmentRate)
              }
            />
            <Kpi
              label={t("kpi.net")}
              value={`${signed(summary.netQuantity)} ${unit}`}
              tone={summary.netQuantity < 0 ? "warn" : "in"}
            />
          </>
        ) : (
          <>
            <Kpi
              label={t("kpi.net")}
              value={`${signed(summary.netQuantity)} ${unit}`}
              tone={summary.netQuantity < 0 ? "warn" : "in"}
            />
            <Kpi
              label={t("kpi.dailyAverage")}
              value={`${summary.dailyAverage.toFixed(2)} ${unit}`}
            />
            <Kpi
              label={t("kpi.averagePrice")}
              value={summary.averagePrice === null ? "—" : money(summary.averagePrice)}
            />
          </>
        )}
      </View>
      {summary.netQuantity < 0 ? (
        <Text style={styles.negativeHint}>{t("summary.negative")}</Text>
      ) : null}
    </View>
  );
}

function Kpi({
  label,
  value,
  tone,
}: {
  label: string;
  value: string;
  tone?: "in" | "warn";
}) {
  return (
    <View style={styles.kpi}>
      <Text numberOfLines={1} style={styles.kpiLabel}>
        {label}
      </Text>
      <Text
        numberOfLines={1}
        style={[
          styles.kpiValue,
          tone === "in" && { color: MOVEMENT_COLORS.in },
          tone === "warn" && { color: HB_COLORS.warning },
        ]}
      >
        {value}
      </Text>
    </View>
  );
}

function TrendCard({
  data,
  trend,
  mode,
  onModeChange,
  t,
}: {
  data: StoreProductInsight;
  trend: ReturnType<typeof buildMovementTrend>;
  mode: MovementTrendMode;
  onModeChange: (mode: MovementTrendMode) => void;
  t: Translate;
}) {
  const warehouse = data.sourceType === "warehouse";
  const last = trend.buckets[trend.buckets.length - 1];
  return (
    <View style={styles.card}>
      <View style={styles.cardHead}>
        <Text style={styles.cardTitle}>
          {trend.bucketDays === 7
            ? t("trend.weeklyTitle")
            : t("trend.bucketTitle", { count: trend.bucketDays })}
        </Text>
        <View style={styles.miniSegment}>
          {(["buckets", "cumulative"] as const).map((value) => (
            <Pressable
              key={value}
              onPress={() => onModeChange(value)}
              accessibilityRole="tab"
              accessibilityState={{ selected: mode === value }}
              style={[styles.miniItem, mode === value && styles.miniItemOn]}
            >
              <Text style={[styles.miniText, mode === value && styles.miniTextOn]}>
                {t(`trend.${value}`)}
              </Text>
            </Pressable>
          ))}
        </View>
      </View>
      <View style={styles.legend}>
        <Legend
          color={MOVEMENT_COLORS.salesBar}
          label={t(mode === "buckets" ? "trend.sales" : "trend.cumulativeSales")}
        />
        <Legend
          color={MOVEMENT_COLORS.inBar}
          label={t(
            mode === "buckets"
              ? warehouse
                ? "trend.inWarehouse"
                : "trend.inLocal"
              : warehouse
                ? "trend.cumulativeInWarehouse"
                : "trend.cumulativeInLocal",
          )}
        />
      </View>
      <MovementTrendChart
        trend={trend}
        mode={mode}
        accessibilityLabel={t("trend.accessibility", {
          in: quantity(last?.cumulativeIn ?? 0),
          sales: quantity(last?.cumulativeSales ?? 0),
        })}
      />
    </View>
  );
}

function Legend({ color, label }: { color: string; label: string }) {
  return (
    <View style={styles.legendItem}>
      <View style={[styles.legendSwatch, { backgroundColor: color }]} />
      <Text style={styles.legendText}>{label}</Text>
    </View>
  );
}

function ChipButton({
  label,
  selected,
  onPress,
  small = false,
}: {
  label: string;
  selected: boolean;
  onPress: () => void;
  small?: boolean;
}) {
  return (
    <Pressable
      onPress={onPress}
      accessibilityRole="button"
      accessibilityState={{ selected }}
      style={[styles.chip, small && styles.chipSmall, selected && styles.chipOn]}
    >
      <Text style={[styles.chipText, selected && styles.chipTextOn]}>{label}</Text>
    </Pressable>
  );
}

function BatchList({
  batches,
  warehouse,
  expanded,
  onToggle,
  onOpen,
  formatDay,
  t,
}: {
  batches: ProductMovementBatch[];
  warehouse: boolean;
  expanded: Set<string>;
  onToggle: (key: string) => void;
  onOpen: (items: ProductInsightMovement[]) => void;
  formatDay: (day: string) => string;
  t: Translate;
}) {
  if (!batches.length) return <EmptyRecords t={t} />;
  const hasInbound = batches.some((item) => item.date);
  return (
    <View style={styles.stack}>
      {batches.map((batch) => (
        <BatchCard
          key={batch.key}
          batch={batch}
          hasInbound={hasInbound}
          warehouse={warehouse}
          open={expanded.has(batch.key)}
          onToggle={() => onToggle(batch.key)}
          onOpen={() => onOpen(batch.documents)}
          formatDay={formatDay}
          t={t}
        />
      ))}
    </View>
  );
}

function BatchCard({
  batch,
  hasInbound,
  warehouse,
  open,
  onToggle,
  onOpen,
  formatDay,
  t,
}: {
  batch: ProductMovementBatch;
  /** 区间内是否有任何进货：没有时唯一一组显示「本区间无进货」，否则是「区间起点前」。 */
  hasInbound: boolean;
  warehouse: boolean;
  open: boolean;
  onToggle: () => void;
  onOpen: () => void;
  formatDay: (day: string) => string;
  t: Translate;
}) {
  const first = batch.documents[0];
  const rate = batch.sellThroughRate;
  const span = batch.latest
    ? t("flow.ongoing", { count: batch.days })
    : t("flow.span", {
        start: shortDateLabel(batch.startDate),
        end: shortDateLabel(batch.endDate),
        count: batch.days,
      });
  const source = warehouse
    ? t("flow.warehouseOutbound")
    : (first?.supplierName ?? "—");
  return (
    <View style={styles.batch}>
      <Pressable
        onPress={first ? onOpen : undefined}
        disabled={!first}
        accessibilityRole={first ? "button" : undefined}
        style={styles.batchHead}
      >
        <Badge
          label={first ? t(warehouse ? "badge.allocation" : "badge.in") : t("badge.sales")}
          tone={first ? "in" : "sales"}
        />
        <View style={styles.batchMain}>
          <View style={styles.batchLine}>
            <View style={styles.batchTitleWrap}>
              <Text numberOfLines={1} style={styles.batchTitle}>
                {first
                  ? `${shortDateLabel(batch.startDate)} · ${first.documentNo}${
                      batch.documents.length > 1
                        ? t("flow.moreDocuments", { count: batch.documents.length })
                        : ""
                    }`
                  : t(hasInbound ? "flow.beforeRange" : warehouse ? "flow.noAllocation" : "flow.noInbound")}
              </Text>
              {batch.latest && first ? (
                <View style={styles.latestTag}>
                  <Text style={styles.latestText}>{t("flow.latest")}</Text>
                </View>
              ) : null}
            </View>
            {first ? (
              <Text style={styles.inQty}>{signed(batch.inQuantity)}</Text>
            ) : null}
          </View>
          <Text numberOfLines={2} style={styles.batchSub}>
            {first
              ? `${source} · ${span} · ${t("flow.salesDays", { count: batch.salesDays })}`
              : `${t("flow.span", {
                  start: shortDateLabel(batch.startDate),
                  end: shortDateLabel(batch.endDate),
                  count: batch.days,
                })} · ${t("flow.salesDays", { count: batch.salesDays })}`}
          </Text>
        </View>
      </Pressable>
      <View style={styles.sellThrough}>
        <View style={styles.sellLine}>
          <Text numberOfLines={2} style={styles.sellText}>
            {t("flow.batchSales", {
              quantity: quantity(batch.salesQuantity),
              amount: money(batch.salesAmount),
            })}
          </Text>
          {rate !== null ? (
            <Text style={[styles.sellRate, rate > 1 && styles.sellRateOver]}>
              {rate > 1 ? `${Math.round(rate * 100)}%` : percent(rate)}
            </Text>
          ) : null}
        </View>
        {rate !== null ? (
          <View style={styles.sellTrack}>
            <View
              style={[
                styles.sellFill,
                { width: `${Math.min(100, rate * 100)}%` },
                rate > 1 && styles.ratioFillOver,
              ]}
            />
          </View>
        ) : null}
        {rate !== null && rate > 1 ? (
          <Text style={styles.overHint}>
            {t(warehouse ? "flow.overHintWarehouse" : "flow.overHint")}
          </Text>
        ) : null}
      </View>
      {open && batch.sales.length ? (
        <View style={styles.days}>
          {batch.sales.map((item, index) => (
            <View
              key={item.date}
              style={[styles.dayRow, index > 0 && styles.dayRowDivider]}
            >
              <Text style={styles.dayDate}>{formatDay(item.date)}</Text>
              <Text style={styles.dayQty}>{signed(-item.quantity)}</Text>
              <Text style={styles.dayAmount}>{money(item.amount)}</Text>
            </View>
          ))}
        </View>
      ) : null}
      {batch.sales.length ? (
        <Pressable onPress={onToggle} accessibilityRole="button" style={styles.expand}>
          <Icon
            source={open ? "chevron-up" : "chevron-down"}
            size={16}
            color={HB_COLORS.action}
          />
          <Text style={styles.expandText}>
            {open
              ? t("flow.collapse")
              : t("flow.expand", { count: batch.sales.length })}
          </Text>
        </Pressable>
      ) : null}
    </View>
  );
}

function DailyFlow({
  months,
  warehouse,
  filter,
  onOpen,
  formatDay,
  formatMonth,
  t,
}: {
  months: ProductMovementFlowMonth[];
  warehouse: boolean;
  filter: ProductMovementFlowFilter;
  onOpen: (item: DetailItem) => void;
  formatDay: (day: string) => string;
  formatMonth: (month: string) => string;
  t: Translate;
}) {
  if (!months.length) return <EmptyRecords t={t} />;
  return (
    <View style={styles.stack}>
      {months.map((month) => {
        const parts: { text: string; color?: string }[] = [];
        if (filter !== "sales") {
          if (warehouse)
            parts.push({
              text: t("flow.monthOrder", { value: quantity(month.orderQuantity) }),
              color: MOVEMENT_COLORS.in,
            });
          parts.push({
            text: t(warehouse ? "flow.monthAllocation" : "flow.monthIn", {
              value: quantity(month.inQuantity),
            }),
            color: MOVEMENT_COLORS.in,
          });
        }
        if (filter !== "in") {
          parts.push({
            text: t("flow.monthSales", { value: quantity(month.salesQuantity) }),
            color: MOVEMENT_COLORS.sales,
          });
          parts.push({ text: money(month.salesAmount) });
        }
        return (
          <View key={month.month} style={styles.batch}>
            <View style={styles.monthHead}>
              <Text style={styles.monthTitle}>{formatMonth(month.month)}</Text>
              <Text numberOfLines={1} style={styles.monthTotals}>
                {parts.map((part, index) => (
                  <Text key={part.text}>
                    {index > 0 ? " · " : ""}
                    <Text style={part.color ? { color: part.color, fontWeight: "700" } : null}>
                      {part.text}
                    </Text>
                  </Text>
                ))}
              </Text>
            </View>
            {month.entries.map((entry, index) => (
              <FlowRow
                key={entry.key}
                entry={entry}
                first={index === 0}
                warehouse={warehouse}
                onOpen={onOpen}
                formatDay={formatDay}
                t={t}
              />
            ))}
          </View>
        );
      })}
    </View>
  );
}

function FlowRow({
  entry,
  first,
  warehouse,
  onOpen,
  formatDay,
  t,
}: {
  entry: ProductMovementFlowEntry;
  first: boolean;
  warehouse: boolean;
  onOpen: (item: DetailItem) => void;
  formatDay: (day: string) => string;
  t: Translate;
}) {
  if (entry.type === "sale") {
    return (
      <View style={[styles.flowRow, !first && styles.flowRowDivider]}>
        <Badge label={t("badge.sales")} tone="sales" small />
        <View style={styles.flowMain}>
          <Text style={styles.flowTitle}>{formatDay(entry.date)}</Text>
          <Text style={styles.flowSub}>{t("flow.posSales")}</Text>
        </View>
        <View style={styles.flowRight}>
          <Text style={styles.salesQty}>{signed(-entry.record.quantity)}</Text>
          <Text style={styles.flowSub}>{money(entry.record.amount)}</Text>
        </View>
      </View>
    );
  }
  if (entry.type === "in") {
    return (
      <Pressable
        onPress={() => onOpen({ kind: "movements", items: [entry.record] })}
        accessibilityRole="button"
        style={[styles.flowRow, !first && styles.flowRowDivider]}
      >
        <Badge label={t(warehouse ? "badge.allocation" : "badge.in")} tone="in" small />
        <View style={styles.flowMain}>
          <Text numberOfLines={1} style={styles.flowTitle}>
            {`${formatDay(entry.date)} · ${entry.record.documentNo}`}
          </Text>
          <Text numberOfLines={1} style={styles.flowSub}>
            {warehouse ? t("flow.warehouseOutbound") : (entry.record.supplierName ?? "—")}
          </Text>
        </View>
        <Text style={styles.inQty}>{signed(entry.record.quantity)}</Text>
      </Pressable>
    );
  }
  const order = entry.record;
  const pending = order.status !== "completed";
  return (
    <Pressable
      onPress={() => onOpen({ kind: "order", item: order })}
      accessibilityRole="button"
      style={[styles.flowRow, !first && styles.flowRowDivider, pending && styles.flowRowPending]}
    >
      <Badge label={t("badge.order")} tone={pending ? "warn" : "muted"} small />
      <View style={styles.flowMain}>
        <Text numberOfLines={1} style={styles.flowTitle}>
          {`${formatDay(entry.date)} · ${t("flow.orderTitle", { documentNo: order.documentNo })}`}
        </Text>
        <Text numberOfLines={1} style={[styles.flowSub, pending && { color: HB_COLORS.warning }]}>
          {pending
            ? t(`flow.orderStatus.${order.status}`, {
                defaultValue: t("flow.orderStatus.unknown"),
              })
            : t("warehouse.dispatchedOn", {
                date: order.deliveryDate ? shortDateLabel(order.deliveryDate) : "—",
              })}
        </Text>
      </View>
      <View style={styles.flowRight}>
        <Text style={[styles.orderQty, pending && { color: HB_COLORS.warning }]}>
          {quantity(order.quantity)}
        </Text>
        <Text style={styles.flowSub}>
          {t("flow.allocated", { value: quantity(order.deliveredQuantity) })}
        </Text>
      </View>
    </Pressable>
  );
}

function Badge({
  label,
  tone,
  small = false,
}: {
  label: string;
  tone: "in" | "sales" | "warn" | "muted";
  small?: boolean;
}) {
  const palette = {
    in: [MOVEMENT_COLORS.inSoft, MOVEMENT_COLORS.in],
    sales: [MOVEMENT_COLORS.salesSoft, MOVEMENT_COLORS.sales],
    warn: ["#FEF3E2", HB_COLORS.warning],
    muted: [HB_COLORS.surfaceMuted, HB_COLORS.textSecondary],
  }[tone];
  return (
    <View style={[styles.badge, small && styles.badgeSmall, { backgroundColor: palette[0] }]}>
      <Text numberOfLines={1} style={[styles.badgeText, { color: palette[1] }]}>
        {label}
      </Text>
    </View>
  );
}

function HistoryNotice({
  record,
  t,
}: {
  record: ProductInsightMovement | null;
  t: Translate;
}) {
  return (
    <View style={styles.warning}>
      <Icon source="history" size={18} color={HB_COLORS.warning} />
      <View style={styles.warningContent}>
        <Text style={styles.warningTitle}>{t("history.noPurchases")}</Text>
        <Text style={styles.warningText}>
          {record
            ? t("history.lastPurchase", {
                date: dateLabel(record.date),
                quantity: quantity(record.quantity),
                documentNo: record.documentNo,
                supplier: record.supplierName ?? "—",
              })
            : t("history.none")}
        </Text>
      </View>
    </View>
  );
}

function DeliveryHint({
  record,
  t,
}: {
  record: ProductInsightMovement | null;
  t: Translate;
}) {
  return (
    <View style={styles.delivery}>
      <Icon source="truck-delivery-outline" size={18} color={HB_COLORS.action} />
      <Text style={styles.deliveryText}>
        {record
          ? t("warehouse.lastAllocation", {
              date: dateLabel(record.date),
              quantity: quantity(record.quantity),
            })
          : t("warehouse.noAllocation")}
      </Text>
    </View>
  );
}

function EmptyRecords({ t }: { t: Translate }) {
  return (
    <View style={styles.recordsEmpty}>
      <Text style={styles.count}>{t("states.noRecords")}</Text>
    </View>
  );
}

function DetailModal({
  item,
  onClose,
  t,
}: {
  item: DetailItem | null;
  onClose: () => void;
  t: Translate;
}) {
  if (!item) return null;
  return (
    <Modal transparent animationType="fade" onRequestClose={onClose}>
      <Pressable style={styles.modalBackdrop} onPress={onClose}>
        <Pressable
          style={styles.modalCard}
          onPress={(event) => event.stopPropagation()}
        >
          <Text variant="titleMedium">{t("detail.title")}</Text>
          {item.kind === "movements" ? (
            item.items.map((movement, index) => (
              <View
                key={movement.id}
                style={[styles.detailGroup, index > 0 && styles.detailGroupDivider]}
              >
                <DetailRow label={t("labels.date")} value={dateLabel(movement.date)} />
                <DetailRow label={t("labels.documentNo")} value={movement.documentNo} />
                <DetailRow label={t("labels.quantity")} value={quantity(movement.quantity)} />
                <DetailRow
                  label={t("labels.supplier")}
                  value={movement.supplierName ?? "—"}
                />
              </View>
            ))
          ) : (
            <View style={styles.detailGroup}>
              <DetailRow label={t("labels.date")} value={dateLabel(item.item.date)} />
              <DetailRow label={t("labels.documentNo")} value={item.item.documentNo} />
              <DetailRow
                label={t("labels.orderedQuantity")}
                value={quantity(item.item.quantity)}
              />
              <DetailRow
                label={t("labels.allocatedQuantity")}
                value={quantity(item.item.deliveredQuantity)}
              />
              <DetailRow
                label={t("labels.outboundDate")}
                value={item.item.deliveryDate ? dateLabel(item.item.deliveryDate) : "—"}
              />
            </View>
          )}
          <Button mode="contained-tonal" onPress={onClose}>
            {t("actions.close")}
          </Button>
        </Pressable>
      </Pressable>
    </Modal>
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.detailRow}>
      <Text style={styles.detailLabel}>{label}</Text>
      <Text style={styles.detailValue}>{value}</Text>
    </View>
  );
}

function Loading() {
  return (
    <View style={styles.outcome}>
      <ActivityIndicator size="large" color={HB_COLORS.brand} />
    </View>
  );
}

function Outcome({
  icon,
  title,
  description,
  action,
  onAction,
}: {
  icon: string;
  title: string;
  description: string;
  action?: string;
  onAction?: () => void;
}) {
  return (
    <View style={styles.outcome}>
      <Icon source={icon} size={42} color="#98A2B3" />
      <Text variant="titleSmall" style={styles.outcomeTitle}>
        {title}
      </Text>
      <Text style={styles.outcomeText}>{description}</Text>
      {action && onAction ? (
        <Button mode="contained-tonal" onPress={onAction}>
          {action}
        </Button>
      ) : null}
    </View>
  );
}

const tabular = { fontVariant: ["tabular-nums" as const] };

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  screen: { flex: 1 },
  body: { flex: 1 },
  header: {
    height: 52,
    flexDirection: "row",
    alignItems: "center",
    paddingRight: HB_SPACING.md,
    backgroundColor: HB_COLORS.white,
  },
  title: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  storeBar: {
    minHeight: 38,
    paddingHorizontal: HB_SPACING.md,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    backgroundColor: "#EAF2FF",
  },
  storeText: {
    flex: 1,
    minWidth: 0,
    color: HB_COLORS.action,
    fontSize: 13,
    fontWeight: "700",
  },
  searchRow: {
    flexDirection: "row",
    padding: HB_SPACING.sm,
    gap: 6,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
  },
  search: {
    flex: 1,
    height: 44,
    backgroundColor: HB_COLORS.white,
    elevation: 0,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
  },
  searchInput: { minHeight: 0, fontSize: 14 },
  searchAction: { width: 42, height: 42, margin: 0 },
  content: { padding: HB_SPACING.sm, gap: HB_SPACING.sm },
  stack: { gap: HB_SPACING.sm },
  dimmed: { opacity: 0.5 },
  product: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
  },
  imageWrap: {
    width: 56,
    height: 56,
    borderRadius: HB_RADIUS.control,
    overflow: "hidden",
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  image: { width: "100%", height: "100%", resizeMode: "cover" },
  productMeta: { flex: 1, minWidth: 0, gap: 3 },
  productName: { color: HB_COLORS.textPrimary, fontWeight: "800" },
  metaRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  meta: { flexShrink: 1, color: HB_COLORS.textSecondary, fontSize: 12 },
  sourceTag: {
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 4,
    backgroundColor: MOVEMENT_COLORS.inSoft,
  },
  sourceTagWarehouse: { backgroundColor: "#F4EBFF" },
  sourceText: { fontSize: 11, fontWeight: "700", color: MOVEMENT_COLORS.in },
  sourceTextWarehouse: { color: "#6941C6" },
  periodBlock: { gap: 6 },
  periodRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  segment: {
    flex: 1,
    flexDirection: "row",
    padding: 2,
    borderRadius: 9,
    backgroundColor: HB_COLORS.outlineMuted,
  },
  segmentItem: {
    flex: 1,
    minHeight: 32,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 7,
  },
  segmentItemOn: {
    backgroundColor: HB_COLORS.white,
    shadowColor: "#101828",
    shadowOpacity: 0.12,
    shadowRadius: 2,
    shadowOffset: { width: 0, height: 1 },
    elevation: 1,
  },
  segmentText: { fontSize: 13, color: HB_COLORS.textSecondary },
  segmentTextOn: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  calendar: {
    width: 40,
    height: 36,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  calendarOn: { borderColor: "#B7CDF5", backgroundColor: "#EAF2FF" },
  rangeRow: { flexDirection: "row", alignItems: "center", gap: 5 },
  rangeText: { color: HB_COLORS.textSecondary, fontSize: 12, ...tabular },
  generated: { marginLeft: "auto", color: "#98A2B3", fontSize: 11 },
  card: {
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
    gap: 10,
  },
  cardHead: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
  },
  cardTitle: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "800" },
  cardScope: { flexShrink: 1, color: "#667085", fontSize: 11 },
  duo: { flexDirection: "row" },
  side: { flex: 1, minWidth: 0, gap: 2 },
  sideRight: { paddingLeft: HB_SPACING.sm },
  duoDivider: {
    width: StyleSheet.hairlineWidth,
    backgroundColor: HB_COLORS.outline,
    marginLeft: HB_SPACING.sm,
  },
  sideLabel: { flexDirection: "row", alignItems: "center", gap: 4 },
  sideLabelText: { fontSize: 12, fontWeight: "700" },
  big: {
    color: HB_COLORS.textPrimary,
    fontSize: 27,
    lineHeight: 34,
    fontWeight: "800",
    ...tabular,
  },
  bigUnit: { fontSize: 13, fontWeight: "500", color: HB_COLORS.textSecondary },
  sideSub: { color: HB_COLORS.textSecondary, fontSize: 12, ...tabular },
  ratio: { gap: 5 },
  ratioTrack: {
    height: 10,
    borderRadius: 5,
    overflow: "hidden",
    backgroundColor: MOVEMENT_COLORS.inSoft,
  },
  ratioFill: {
    height: "100%",
    borderRadius: 5,
    backgroundColor: MOVEMENT_COLORS.salesBar,
  },
  ratioFillOver: { backgroundColor: "#F79009" },
  ratioCaption: {
    flexDirection: "row",
    flexWrap: "wrap",
    justifyContent: "space-between",
    columnGap: HB_SPACING.xs,
    rowGap: 2,
  },
  ratioText: { flexShrink: 1, color: HB_COLORS.textSecondary, fontSize: 12, ...tabular },
  kpis: { flexDirection: "row", gap: HB_SPACING.xs },
  kpi: {
    flex: 1,
    minWidth: 0,
    paddingHorizontal: 10,
    paddingVertical: 8,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    gap: 2,
  },
  kpiLabel: { color: "#667085", fontSize: 11 },
  kpiValue: {
    color: HB_COLORS.textPrimary,
    fontSize: 15,
    fontWeight: "700",
    ...tabular,
  },
  negativeHint: { color: HB_COLORS.warning, fontSize: 12, lineHeight: 17 },
  updatedAt: { color: "#98A2B3", fontSize: 11 },
  warning: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: "#FEC84B",
    backgroundColor: "#FFFAEB",
  },
  warningContent: { flex: 1, minWidth: 0, gap: 2 },
  warningTitle: { color: HB_COLORS.warning, fontWeight: "800" },
  warningText: { flex: 1, color: "#7A2E0E", fontSize: 12, lineHeight: 18 },
  delivery: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#EAF2FF",
  },
  deliveryText: { flex: 1, color: HB_COLORS.action, fontSize: 12 },
  miniSegment: {
    flexDirection: "row",
    padding: 2,
    borderRadius: 7,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  miniItem: { paddingHorizontal: 10, paddingVertical: 4, borderRadius: 5 },
  miniItemOn: { backgroundColor: HB_COLORS.white, elevation: 1 },
  miniText: { fontSize: 12, color: HB_COLORS.textSecondary },
  miniTextOn: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  legend: { flexDirection: "row", gap: HB_SPACING.sm, marginTop: -4 },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 4 },
  legendSwatch: { width: 9, height: 9, borderRadius: 2 },
  legendText: { fontSize: 11, color: HB_COLORS.textSecondary },
  flowHead: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    marginTop: HB_SPACING.xs,
  },
  sectionTitle: { fontWeight: "800", color: HB_COLORS.textPrimary },
  count: { color: HB_COLORS.textSecondary },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 6, marginTop: -4 },
  chip: {
    minHeight: 32,
    paddingHorizontal: 12,
    justifyContent: "center",
    borderRadius: 16,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  chipSmall: { minHeight: 28, paddingHorizontal: 10 },
  chipOn: { borderColor: "#B7CDF5", backgroundColor: "#DCE7FB" },
  chipText: { fontSize: 12, color: HB_COLORS.textSecondary },
  chipTextOn: { color: HB_COLORS.action, fontWeight: "700" },
  batch: {
    overflow: "hidden",
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  batchHead: {
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: HB_SPACING.sm,
    paddingTop: HB_SPACING.sm,
    paddingBottom: 10,
  },
  batchMain: { flex: 1, minWidth: 0, gap: 2 },
  batchLine: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
  },
  batchTitleWrap: { flex: 1, minWidth: 0, flexDirection: "row", alignItems: "center", gap: 6 },
  batchTitle: {
    flexShrink: 1,
    color: HB_COLORS.textPrimary,
    fontSize: 14,
    fontWeight: "700",
  },
  latestTag: {
    paddingHorizontal: 5,
    paddingVertical: 1,
    borderRadius: 4,
    backgroundColor: MOVEMENT_COLORS.salesSoft,
  },
  latestText: { fontSize: 10, fontWeight: "700", color: MOVEMENT_COLORS.sales },
  batchSub: { color: HB_COLORS.textSecondary, fontSize: 12 },
  inQty: {
    color: MOVEMENT_COLORS.in,
    fontSize: 16,
    fontWeight: "800",
    ...tabular,
  },
  sellThrough: {
    marginLeft: 54,
    marginRight: HB_SPACING.sm,
    marginBottom: 10,
    paddingHorizontal: 10,
    paddingVertical: 8,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    gap: 6,
  },
  sellLine: { flexDirection: "row", justifyContent: "space-between", gap: 6 },
  sellText: { flexShrink: 1, color: HB_COLORS.textSecondary, fontSize: 12, ...tabular },
  sellRate: { color: HB_COLORS.textPrimary, fontSize: 12, fontWeight: "800", ...tabular },
  sellRateOver: { color: HB_COLORS.warning },
  sellTrack: {
    height: 6,
    borderRadius: 3,
    overflow: "hidden",
    backgroundColor: "#DCE5F5",
  },
  sellFill: {
    height: "100%",
    borderRadius: 3,
    backgroundColor: MOVEMENT_COLORS.salesBar,
  },
  overHint: { color: HB_COLORS.warning, fontSize: 11 },
  days: { borderTopWidth: 1, borderColor: HB_COLORS.outlineMuted },
  dayRow: {
    flexDirection: "row",
    alignItems: "center",
    paddingVertical: 8,
    paddingLeft: 54,
    paddingRight: HB_SPACING.sm,
  },
  dayRowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderColor: HB_COLORS.outlineMuted },
  dayDate: { width: 104, color: HB_COLORS.textSecondary, fontSize: 13, ...tabular },
  dayQty: {
    flex: 1,
    color: MOVEMENT_COLORS.sales,
    fontSize: 13,
    fontWeight: "700",
    ...tabular,
  },
  dayAmount: { color: HB_COLORS.textSecondary, fontSize: 13, ...tabular },
  expand: {
    minHeight: 38,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
    borderTopWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
  },
  expandText: { color: HB_COLORS.action, fontSize: 12, fontWeight: "600" },
  monthHead: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 9,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  monthTitle: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "800" },
  monthTotals: { flexShrink: 1, color: HB_COLORS.textSecondary, fontSize: 12, ...tabular },
  flowRow: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 8,
  },
  flowRowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderColor: HB_COLORS.outlineMuted },
  flowRowPending: { backgroundColor: "#FFFCF5" },
  flowMain: { flex: 1, minWidth: 0, gap: 1 },
  flowTitle: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "700", ...tabular },
  flowSub: { color: "#667085", fontSize: 11, ...tabular },
  flowRight: { alignItems: "flex-end", gap: 1 },
  salesQty: {
    color: MOVEMENT_COLORS.sales,
    fontSize: 15,
    fontWeight: "700",
    ...tabular,
  },
  orderQty: {
    color: HB_COLORS.textPrimary,
    fontSize: 15,
    fontWeight: "800",
    ...tabular,
  },
  badge: {
    width: 32,
    height: 32,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
  },
  badgeSmall: { width: 28, height: 28, borderRadius: 8 },
  badgeText: { fontSize: 12, fontWeight: "800" },
  sticky: {
    position: "absolute",
    top: 0,
    left: 0,
    right: 0,
    minHeight: 36,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: HB_SPACING.md,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
  },
  stickyCode: {
    flexShrink: 1,
    color: HB_COLORS.textPrimary,
    fontSize: 13,
    fontWeight: "800",
  },
  stickyDivider: { width: 1, height: 14, backgroundColor: HB_COLORS.outline },
  stickyText: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700", ...tabular },
  recordsEmpty: {
    padding: HB_SPACING.lg,
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
  },
  footer: {
    position: "absolute",
    left: 0,
    right: 0,
    bottom: 0,
    padding: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
  },
  branchButton: { minHeight: 44 },
  outcome: {
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: HB_SPACING.lg,
    paddingVertical: 72,
    gap: HB_SPACING.sm,
  },
  outcomeTitle: { color: HB_COLORS.textPrimary, fontWeight: "800" },
  outcomeText: {
    color: HB_COLORS.textSecondary,
    textAlign: "center",
    lineHeight: 20,
  },
  modalBackdrop: {
    flex: 1,
    padding: HB_SPACING.lg,
    justifyContent: "center",
    backgroundColor: "rgba(16,24,40,0.44)",
  },
  modalCard: {
    padding: HB_SPACING.md,
    borderRadius: HB_RADIUS.sheet,
    backgroundColor: HB_COLORS.white,
    gap: HB_SPACING.sm,
  },
  detailGroup: { gap: HB_SPACING.xs },
  detailGroupDivider: {
    paddingTop: HB_SPACING.sm,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  detailRow: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
    justifyContent: "space-between",
  },
  detailLabel: { color: HB_COLORS.textSecondary },
  detailValue: {
    flex: 1,
    color: HB_COLORS.textPrimary,
    textAlign: "right",
    fontWeight: "600",
  },
});
