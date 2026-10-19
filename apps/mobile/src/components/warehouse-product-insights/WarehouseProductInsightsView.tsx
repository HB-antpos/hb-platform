import { useMemo, useState } from "react";
import { Image, Pressable, ScrollView, StyleSheet, View } from "react-native";
import {
  ActivityIndicator,
  Button,
  Icon,
  IconButton,
  Searchbar,
  Text,
} from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { useContainerNumberDisplay } from "@/components/container-number";
import { buildInsightFlowSegments } from "@/modules/warehouse-product-insights/logic";
import type {
  WarehouseInsightTab,
  WarehouseProductInsight,
} from "@/modules/warehouse-product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export interface WarehouseProductInsightsViewProps {
  query: string;
  onQueryChange: (value: string) => void;
  onQueryFocus?: () => void;
  onQueryBlur?: () => void;
  onSearch: () => void;
  onScan: () => void;
  data: WarehouseProductInsight | null;
  loading?: boolean;
  error?: string | null;
  emptyState?: "initial" | "not-found";
  onRetry?: () => void;
  onBack: () => void;
  onOpenRange: () => void;
  onOpenBranches: (sort: "sales" | "shipped" | "ordered") => void;
  onOpenContainers: () => void;
}

const money = (value: number) =>
  `A$${value.toLocaleString("en-AU", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

function dateLabel(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return match ? `${match[3]}/${match[2]}/${match[1]}` : "—";
}

function shortDate(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return match ? `${match[3]}/${match[2]}` : "—";
}

function timestampLabel(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2})/.exec(value);
  return match ? `${match[3]}/${match[2]} ${match[4]}:${match[5]}` : dateLabel(value);
}

export function WarehouseProductInsightsView({
  query,
  onQueryChange,
  onQueryFocus,
  onQueryBlur,
  onSearch,
  onScan,
  data,
  loading = false,
  error,
  emptyState = "initial",
  onRetry,
  onBack,
  onOpenRange,
  onOpenBranches,
  onOpenContainers,
}: WarehouseProductInsightsViewProps) {
  const { t } = useAppTranslation("warehouseProductInsights");
  const insets = useSafeAreaInsets();
  const [tab, setTab] = useState<WarehouseInsightTab>("containers");
  const flow = useMemo(
    () =>
      data
        ? buildInsightFlowSegments(data.totals)
        : { sold: 0, shippedUnsold: 0, remaining: 0 },
    [data],
  );

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
        <View style={styles.scopeBar}>
          <Icon source="warehouse" size={17} color={HB_COLORS.action} />
          <Text numberOfLines={1} style={styles.scopeText}>
            {data
              ? t(
                  data.scope === "authorized-stores"
                    ? "scope.authorized"
                    : "scope.all",
                  { count: data.branches.length },
                )
              : t("scope.warehouse")}
          </Text>
        </View>
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
        <ScrollView
          contentContainerStyle={[
            styles.content,
            { paddingBottom: 88 + insets.bottom },
          ]}
          keyboardShouldPersistTaps="handled"
        >
          {loading ? (
            <View style={styles.state}>
              <ActivityIndicator color={HB_COLORS.brand} />
              <Text style={styles.stateText}>{t("states.loading")}</Text>
            </View>
          ) : error ? (
            <Outcome
              icon="alert-circle-outline"
              title={t("states.loadFailed")}
              description={error}
              action={onRetry ? t("actions.retry") : undefined}
              onAction={onRetry}
            />
          ) : !data ? (
            <Outcome
              icon={
                emptyState === "not-found" ? "package-variant-closed" : "barcode-search"
              }
              title={t(
                emptyState === "not-found"
                  ? "states.notFoundTitle"
                  : "states.initialTitle",
              )}
              description={t(
                emptyState === "not-found"
                  ? "states.notFoundDescription"
                  : "states.initialDescription",
              )}
            />
          ) : (
            <>
              <ProductHeader data={data} t={t} />
              <Pressable
                accessibilityRole="button"
                onPress={onOpenRange}
                style={styles.rangeCard}
              >
                <View style={styles.rangeRow}>
                  <Icon source="calendar" size={16} color={HB_COLORS.action} />
                  <Text style={styles.rangeText}>
                    {t("range.label", {
                      startDate: dateLabel(data.range.startDate),
                      endDate: dateLabel(data.range.endDate),
                    })}
                  </Text>
                  <Text style={styles.rangeDays}>
                    {t("range.days", { days: data.range.dayCount })}
                  </Text>
                  <Icon source="chevron-down" size={18} color={HB_COLORS.action} />
                </View>
                <Text style={styles.rangeMeta}>
                  {data.salesStatisticLastUpdatedAt
                    ? t("range.salesUpdatedAt", {
                        value: timestampLabel(data.salesStatisticLastUpdatedAt),
                      })
                    : t("range.salesUpdatedUnavailable")}
                </Text>
              </Pressable>

              <View style={styles.flowCard}>
                <View style={styles.flowHeader}>
                  <Text style={styles.flowLabel}>
                    {t("flow.inbound", {
                      quantity: data.totals.inboundQuantity.toLocaleString(),
                    })}
                  </Text>
                  <Text style={styles.flowLabel}>
                    {t("flow.shipped", {
                      quantity: data.totals.shippedQuantity.toLocaleString(),
                      percent: Math.round(
                        (flow.sold + flow.shippedUnsold) * 100,
                      ),
                    })}
                  </Text>
                  <Text style={styles.flowLabel}>
                    {t("flow.sold", {
                      quantity: data.totals.salesQuantity.toLocaleString(),
                      percent: Math.round(flow.sold * 100),
                    })}
                  </Text>
                </View>
                <View style={styles.track}>
                  <View
                    style={[
                      styles.trackSegment,
                      { flex: flow.sold, backgroundColor: HB_COLORS.success },
                    ]}
                  />
                  <View
                    style={[
                      styles.trackSegment,
                      { flex: flow.shippedUnsold, backgroundColor: HB_COLORS.brand },
                    ]}
                  />
                  <View
                    style={[
                      styles.trackSegment,
                      { flex: flow.remaining, backgroundColor: HB_COLORS.outline },
                    ]}
                  />
                </View>
                <View style={styles.legend}>
                  <Legend color={HB_COLORS.success} label={t("flow.legendSold")} />
                  <Legend color={HB_COLORS.brand} label={t("flow.legendShipped")} />
                  <Legend color={HB_COLORS.outline} label={t("flow.legendRemaining")} />
                </View>
              </View>

              <View style={styles.cards}>
                <MetricCard
                  label={t("cards.inbound")}
                  value={`${data.totals.inboundQuantity.toLocaleString()} ${t("units.items")}`}
                  hint={t("cards.inboundHint", { count: data.totals.containerCount })}
                  onPress={onOpenContainers}
                />
                <MetricCard
                  label={t("cards.ordered")}
                  value={`${data.totals.orderedQuantity.toLocaleString()} ${t("units.items")}`}
                  hint={t("cards.orderedHint", { count: data.totals.orderedStoreCount })}
                  onPress={() => onOpenBranches("ordered")}
                />
                <MetricCard
                  label={t("cards.shipped")}
                  value={`${data.totals.shippedQuantity.toLocaleString()} ${t("units.items")}`}
                  hint={t("cards.shippedHint", { count: data.totals.shippedStoreCount })}
                  onPress={() => onOpenBranches("shipped")}
                />
                <MetricCard
                  label={t("cards.sales")}
                  value={`${data.totals.salesQuantity.toLocaleString()} ${t("units.items")}`}
                  hint={money(data.totals.salesAmount)}
                  highlighted
                  onPress={() => onOpenBranches("sales")}
                />
              </View>

              {data.totals.pendingQuantity > 0 ? (
                <Pressable
                  accessibilityRole="button"
                  onPress={() => onOpenBranches("shipped")}
                  style={styles.banner}
                >
                  <Icon source="truck-delivery-outline" size={17} color={HB_COLORS.warning} />
                  <Text style={styles.bannerText}>
                    {t("pending.banner", {
                      quantity: data.totals.pendingQuantity.toLocaleString(),
                      count: data.totals.pendingStoreCount,
                    })}
                  </Text>
                  <Icon source="chevron-right" size={18} color={HB_COLORS.warning} />
                </Pressable>
              ) : null}

              {data.totals.inTransitQuantity > 0 ? (
                <Text style={styles.inTransit}>
                  {t("pending.inTransit", {
                    quantity: data.totals.inTransitQuantity.toLocaleString(),
                    count: data.totals.inTransitContainerCount,
                  })}
                </Text>
              ) : null}

              <View style={styles.tabs}>
                {(
                  ["containers", "orders", "shipments", "sales"] as WarehouseInsightTab[]
                ).map((value) => (
                  <Text
                    key={value}
                    accessibilityRole="button"
                    onPress={() => setTab(value)}
                    style={[styles.tab, tab === value ? styles.tabActive : null]}
                  >
                    {t(`tabs.${value}`)}
                  </Text>
                ))}
              </View>
              <RecordList data={data} tab={tab} t={t} />
            </>
          )}
        </ScrollView>
        {data ? (
          <View
            style={[
              styles.footer,
              { paddingBottom: Math.max(HB_SPACING.sm, insets.bottom) },
            ]}
          >
            <Button
              mode="contained"
              icon="chart-bar"
              onPress={() => onOpenBranches("sales")}
              contentStyle={styles.footerButton}
            >
              {t("actions.openBranches")}
            </Button>
          </View>
        ) : null}
      </View>
    </SafeAreaView>
  );
}

type Translate = (key: string, values?: Record<string, unknown>) => string;

function ProductHeader({
  data,
  t,
}: {
  data: WarehouseProductInsight;
  t: Translate;
}) {
  const product = data.product;
  return (
    <View style={styles.product}>
      <View style={styles.imageWrap}>
        {product.productImage ? (
          <Image source={{ uri: product.productImage }} style={styles.image} />
        ) : (
          <Icon source="package-variant" size={30} color="#98A2B3" />
        )}
      </View>
      <View style={styles.productMeta}>
        <Text variant="titleSmall" numberOfLines={2} style={styles.productName}>
          {product.productName}
        </Text>
        <Text numberOfLines={1} style={styles.meta}>
          {t("labels.itemNumber")}: {product.itemNumber ?? "—"} ·{" "}
          {t("labels.barcode")}: {product.barcode ?? "—"}
        </Text>
        <Text numberOfLines={1} style={styles.meta}>
          {t("labels.location")}: {product.locationCode ?? "—"} ·{" "}
          {t("labels.stock")}:{" "}
          {product.stockQuantity == null
            ? "—"
            : `${product.stockQuantity.toLocaleString()} ${t("units.items")}`}
        </Text>
      </View>
    </View>
  );
}

function Legend({ color, label }: { color: string; label: string }) {
  return (
    <View style={styles.legendItem}>
      <View style={[styles.legendDot, { backgroundColor: color }]} />
      <Text style={styles.legendText}>{label}</Text>
    </View>
  );
}

function MetricCard({
  label,
  value,
  hint,
  highlighted = false,
  onPress,
}: {
  label: string;
  value: string;
  hint: string;
  highlighted?: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={[styles.card, highlighted ? styles.cardHighlighted : null]}
    >
      <View style={styles.cardHeader}>
        <Text style={[styles.cardLabel, highlighted ? styles.cardLabelHighlighted : null]}>
          {label}
        </Text>
        <Icon
          source="chevron-right"
          size={14}
          color={highlighted ? HB_COLORS.action : "#98A2B3"}
        />
      </View>
      <Text style={[styles.cardValue, highlighted ? styles.cardValueHighlighted : null]}>
        {value}
      </Text>
      <Text style={[styles.cardHint, highlighted ? styles.cardHintHighlighted : null]}>
        {hint}
      </Text>
    </Pressable>
  );
}

function RecordList({
  data,
  tab,
  t,
}: {
  data: WarehouseProductInsight;
  tab: WarehouseInsightTab;
  t: Translate;
}) {
  // 货柜编号默认只显示后 4 位；无权限账号后端已遮罩，遮罩值可能重复，所以 key 带序号。
  const { format: formatContainerNumber } = useContainerNumberDisplay();
  const rows =
    tab === "containers"
      ? data.containers.map((row, index) => ({
          key: `${row.containerNumber}:${row.arrivalDate}:${index}`,
          title: row.containerNumber ? formatContainerNumber(row.containerNumber) : "—",
          meta: row.isEstimatedArrival
            ? t("containerSheet.estimatedArrival", { date: shortDate(row.arrivalDate) })
            : t("containerSheet.actualArrival", { date: shortDate(row.arrivalDate) }),
          value: row.quantity.toLocaleString(),
          warn: row.isEstimatedArrival,
        }))
      : tab === "sales"
        ? data.dailySales.map((row) => ({
            key: row.date,
            title: dateLabel(row.date),
            meta: money(row.amount),
            value: row.quantity.toLocaleString(),
            warn: false,
          }))
        : (tab === "orders" ? data.orders : data.shipments).map((row, index) => ({
            key: `${row.documentNo}:${row.storeCode}:${index}`,
            title: row.documentNo || "—",
            meta: `${row.storeName} · ${shortDate(row.date)}`,
            value: row.quantity.toLocaleString(),
            warn: false,
          }));
  const total =
    tab === "orders"
      ? data.totals.orderDocumentCount
      : tab === "shipments"
        ? data.totals.shipmentDocumentCount
        : rows.length;

  return (
    <View style={styles.records}>
      <View style={styles.recordHeader}>
        <Text style={styles.recordTitle}>{t(`tabs.${tab}`)}</Text>
        <Text style={styles.recordCount}>
          {rows.length < total
            ? t("records.truncated", { shown: rows.length, total })
            : t("records.count", { count: rows.length })}
        </Text>
      </View>
      {rows.length === 0 ? (
        <Text style={styles.recordEmpty}>{t("records.empty")}</Text>
      ) : (
        rows.map((row) => (
          <View key={row.key} style={styles.record}>
            <View style={styles.recordMain}>
              <Text numberOfLines={1} style={styles.recordName}>
                {row.title}
              </Text>
              <Text numberOfLines={1} style={row.warn ? styles.recordWarn : styles.recordMeta}>
                {row.meta}
              </Text>
            </View>
            <Text style={styles.recordValue}>{row.value}</Text>
          </View>
        ))
      )}
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
    <View style={styles.state}>
      <Icon source={icon} size={34} color="#98A2B3" />
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
  screen: { flex: 1 },
  header: {
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
  },
  title: { fontWeight: "700", color: HB_COLORS.textPrimary },
  scopeBar: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: 7,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  scopeText: { flex: 1, fontSize: 12, color: HB_COLORS.action },
  searchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: HB_SPACING.sm,
    paddingBottom: HB_SPACING.xs,
    backgroundColor: HB_COLORS.white,
  },
  search: { flex: 1, height: 42, backgroundColor: HB_COLORS.surfaceMuted, elevation: 0 },
  searchInput: { minHeight: 0, fontSize: 14 },
  searchAction: { margin: 0 },
  content: { padding: HB_SPACING.sm, gap: HB_SPACING.xs },
  product: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.white,
  },
  imageWrap: {
    width: 56,
    height: 56,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    alignItems: "center",
    justifyContent: "center",
    overflow: "hidden",
  },
  image: { width: "100%", height: "100%" },
  productMeta: { flex: 1, minWidth: 0, gap: 3 },
  productName: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  meta: { fontSize: 11, color: HB_COLORS.textSecondary },
  rangeCard: {
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.white,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    gap: 3,
  },
  rangeRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  rangeText: { flex: 1, fontSize: 13, color: HB_COLORS.textPrimary },
  rangeDays: { fontSize: 12, color: HB_COLORS.textSecondary },
  rangeMeta: { fontSize: 11, color: "#98A2B3" },
  flowCard: {
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.white,
    gap: 6,
  },
  flowHeader: { flexDirection: "row", justifyContent: "space-between" },
  flowLabel: { fontSize: 11, color: HB_COLORS.textSecondary },
  track: {
    flexDirection: "row",
    height: 8,
    borderRadius: 4,
    overflow: "hidden",
    backgroundColor: HB_COLORS.outlineMuted,
  },
  trackSegment: { height: "100%" },
  legend: { flexDirection: "row", gap: HB_SPACING.sm },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 4 },
  legendDot: { width: 7, height: 7, borderRadius: 2 },
  legendText: { fontSize: 11, color: HB_COLORS.textSecondary },
  cards: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  card: {
    flexGrow: 1,
    flexBasis: "47%",
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.white,
    gap: 2,
  },
  cardHighlighted: { backgroundColor: "#EAF2FF" },
  cardHeader: { flexDirection: "row", alignItems: "center", gap: 4 },
  cardLabel: { flex: 1, fontSize: 11, color: HB_COLORS.textSecondary },
  cardLabelHighlighted: { color: HB_COLORS.action },
  cardValue: { fontSize: 17, fontWeight: "800", color: HB_COLORS.textPrimary },
  cardValueHighlighted: { color: "#0C447C" },
  cardHint: { fontSize: 11, color: "#98A2B3" },
  cardHintHighlighted: { color: HB_COLORS.action },
  banner: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#FEF6EE",
  },
  bannerText: { flex: 1, fontSize: 12, color: "#854F0B" },
  inTransit: { fontSize: 11, color: HB_COLORS.warning, paddingHorizontal: 2 },
  tabs: { flexDirection: "row", gap: 6, paddingVertical: 2 },
  tab: {
    fontSize: 12,
    paddingVertical: 5,
    paddingHorizontal: 10,
    borderRadius: 14,
    overflow: "hidden",
    backgroundColor: HB_COLORS.white,
    color: HB_COLORS.textSecondary,
  },
  tabActive: { backgroundColor: HB_COLORS.brand, color: HB_COLORS.white },
  records: {
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.white,
    overflow: "hidden",
  },
  recordHeader: {
    flexDirection: "row",
    alignItems: "center",
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  recordTitle: { flex: 1, fontSize: 12, color: HB_COLORS.textSecondary },
  recordCount: { fontSize: 11, color: HB_COLORS.textSecondary },
  record: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 9,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  recordMain: { flex: 1, minWidth: 0, gap: 2 },
  recordName: { fontSize: 13, color: HB_COLORS.textPrimary },
  recordMeta: { fontSize: 11, color: "#98A2B3" },
  recordWarn: { fontSize: 11, color: HB_COLORS.warning, fontWeight: "600" },
  recordValue: { fontSize: 13, color: HB_COLORS.textPrimary, fontWeight: "600" },
  recordEmpty: {
    padding: HB_SPACING.md,
    textAlign: "center",
    color: HB_COLORS.textSecondary,
    fontSize: 12,
  },
  state: {
    paddingVertical: HB_SPACING.xl,
    alignItems: "center",
    gap: HB_SPACING.xs,
  },
  stateTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  stateText: { color: HB_COLORS.textSecondary, textAlign: "center", fontSize: 13 },
  footer: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  footerButton: { minHeight: 42 },
});
