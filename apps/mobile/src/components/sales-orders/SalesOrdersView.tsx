import { useCallback } from "react";
import { FlatList, Pressable, StyleSheet, View } from "react-native";
import {
  ActivityIndicator,
  Button,
  Icon,
  IconButton,
  Searchbar,
  Text,
} from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import {
  SALES_ORDER_REFUND_TYPE,
  formatSalesOrderGuidTail,
  resolveSalesOrderRefundTone,
  resolveSalesOrderStatusKey,
} from "@/modules/sales-orders/logic";
import type {
  SalesOrderFilters,
  SalesOrderListItem,
  SalesOrderScope,
} from "@/modules/sales-orders/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { REFUND_TONES, SalesOrderRefundTag, SalesOrderStatusTag } from "./SalesOrderStatusTag";
import { formatSalesOrderMoney, formatSalesOrderTime, formatShortDate } from "./format";

export interface SalesOrdersViewProps {
  query: string;
  onQueryChange: (value: string) => void;
  onQueryFocus?: () => void;
  onQueryBlur?: () => void;
  onSearch: () => void;
  onScan: () => void;
  filters: SalesOrderFilters;
  scope: SalesOrderScope | null;
  branchCount: number | null;
  activeFilterCount: number;
  items: SalesOrderListItem[];
  total: number | null;
  loading: boolean;
  loadingMore: boolean;
  hasMore: boolean;
  error: string | null;
  searched: boolean;
  onRetry: () => void;
  onLoadMore: () => void;
  onRefresh: () => void;
  onBack: () => void;
  onOpenFilters: () => void;
  onToggleSort: () => void;
  onOpenOrder: (item: SalesOrderListItem) => void;
}

export function SalesOrdersView({
  query,
  onQueryChange,
  onQueryFocus,
  onQueryBlur,
  onSearch,
  onScan,
  filters,
  scope,
  branchCount,
  activeFilterCount,
  items,
  total,
  loading,
  loadingMore,
  hasMore,
  error,
  searched,
  onRetry,
  onLoadMore,
  onRefresh,
  onBack,
  onOpenFilters,
  onToggleSort,
  onOpenOrder,
}: SalesOrdersViewProps) {
  const { t } = useAppTranslation("salesOrders");
  const insets = useSafeAreaInsets();
  const rangeLabel =
    filters.range.startDate === filters.range.endDate
      ? formatShortDate(filters.range.startDate)
      : `${formatShortDate(filters.range.startDate)}–${formatShortDate(filters.range.endDate)}`;

  const renderItem = useCallback(
    ({ item }: { item: SalesOrderListItem }) => (
      <OrderCard item={item} onPress={() => onOpenOrder(item)} />
    ),
    [onOpenOrder],
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
          <Icon source="store-outline" size={17} color={HB_COLORS.action} />
          <Text numberOfLines={1} style={styles.scopeText}>
            {scope == null
              ? t("scope.loading")
              : t(scope === "all-stores" ? "scope.all" : "scope.authorized", {
                  count: branchCount ?? 0,
                  selected: filters.branchCodes.length || (branchCount ?? 0),
                })}
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
            returnKeyType="search"
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
        <View style={styles.chips}>
          <FilterChip
            icon="calendar"
            label={rangeLabel}
            active
            onPress={onOpenFilters}
          />
          <FilterChip
            icon="store-outline"
            label={
              filters.branchCodes.length
                ? t("chips.branches", { count: filters.branchCodes.length })
                : t("chips.allBranches")
            }
            active={filters.branchCodes.length > 0}
            onPress={onOpenFilters}
          />
          <FilterChip
            icon="filter-variant"
            label={
              filters.orderType === -1
                ? t("status.all")
                : filters.orderType === SALES_ORDER_REFUND_TYPE && filters.refundTier !== "all"
                  ? `${t("status.refunded")} · ${t(`refund.${filters.refundTier}`)}`
                  : t(`status.${resolveSalesOrderStatusKey(filters.orderType)}`)
            }
            active={filters.orderType !== -1}
            onPress={onOpenFilters}
          />
        </View>
        <View style={styles.summary}>
          <Text style={styles.summaryText}>
            {total == null
              ? activeFilterCount > 0
                ? t("summary.filters", { count: activeFilterCount })
                : " "
              : t("summary.total", { count: total })}
          </Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("actions.toggleSort")}
            onPress={onToggleSort}
            style={styles.sortButton}
          >
            <Icon
              source={filters.sortDirection === "desc" ? "sort-clock-descending" : "sort-clock-ascending"}
              size={16}
              color={HB_COLORS.action}
            />
            <Text style={styles.sortText}>
              {t(filters.sortDirection === "desc" ? "sort.newestFirst" : "sort.oldestFirst")}
            </Text>
          </Pressable>
        </View>
        <FlatList
          data={items}
          keyExtractor={(item) => item.orderGuid}
          renderItem={renderItem}
          contentContainerStyle={[
            styles.content,
            { paddingBottom: 88 + insets.bottom },
            items.length === 0 ? styles.contentEmpty : null,
          ]}
          keyboardShouldPersistTaps="handled"
          onEndReachedThreshold={0.4}
          onEndReached={() => {
            if (hasMore && !loading && !loadingMore && !error) onLoadMore();
          }}
          refreshing={false}
          onRefresh={loading ? undefined : onRefresh}
          ListEmptyComponent={
            loading ? (
              <View style={styles.state}>
                <ActivityIndicator color={HB_COLORS.brand} />
                <Text style={styles.stateText}>{t("states.loading")}</Text>
              </View>
            ) : error ? (
              <Outcome
                icon="alert-circle-outline"
                title={t("states.loadFailed")}
                description={error}
                action={t("actions.retry")}
                onAction={onRetry}
              />
            ) : (
              <Outcome
                icon={searched ? "receipt-text-remove-outline" : "receipt-text-outline"}
                title={t(searched ? "states.emptyTitle" : "states.initialTitle")}
                description={t(searched ? "states.emptyDescription" : "states.initialDescription")}
              />
            )
          }
          ListFooterComponent={
            items.length > 0 ? (
              <View style={styles.footer}>
                {loadingMore ? (
                  <ActivityIndicator size="small" color={HB_COLORS.brand} />
                ) : error ? (
                  <Button compact mode="text" onPress={onRetry}>
                    {t("actions.retryLoadMore")}
                  </Button>
                ) : (
                  <Text style={styles.footerText}>
                    {hasMore
                      ? t("states.loadMore", { shown: items.length, total: total ?? items.length })
                      : t("states.allLoaded", { total: total ?? items.length })}
                  </Text>
                )}
              </View>
            ) : null
          }
        />
      </View>
    </SafeAreaView>
  );
}

function FilterChip({
  icon,
  label,
  active,
  onPress,
}: {
  icon: string;
  label: string;
  active: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={[styles.chip, active ? styles.chipActive : null]}
    >
      <Icon source={icon} size={14} color={active ? HB_COLORS.action : HB_COLORS.textSecondary} />
      <Text numberOfLines={1} style={[styles.chipText, active ? styles.chipTextActive : null]}>
        {label}
      </Text>
      <Icon source="chevron-down" size={14} color={active ? HB_COLORS.action : HB_COLORS.textSecondary} />
    </Pressable>
  );
}

function OrderCard({ item, onPress }: { item: SalesOrderListItem; onPress: () => void }) {
  const { t } = useAppTranslation("salesOrders");
  const matched = item.matchedProducts[0];
  const extra = item.matchedProducts.length - 1;
  // 含退款商品：整单金额不为负是部分退款（橙），为负是整单退款（红）；金额与状态标签同色。
  const refundTone = resolveSalesOrderRefundTone(item);
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.card}>
      <View style={styles.cardRow}>
        <Text style={styles.orderGuid}>{formatSalesOrderGuidTail(item.orderGuid)}</Text>
        <View style={styles.cardTags}>
          {refundTone && item.status !== SALES_ORDER_REFUND_TYPE ? <SalesOrderRefundTag tone={refundTone} /> : null}
          <SalesOrderStatusTag status={item.status} refundTone={refundTone} />
        </View>
      </View>
      <View style={styles.cardRow}>
        <Text numberOfLines={1} style={[styles.cardMeta, styles.cardMetaGrow]}>
          {formatSalesOrderTime(item.orderTime)} · {item.branchName ?? item.branchCode ?? "—"}
        </Text>
        {item.deviceCode ? <Text style={styles.cardMeta}>{item.deviceCode}</Text> : null}
      </View>
      <View style={[styles.cardRow, styles.cardAmounts]}>
        <Text style={styles.cardMeta}>
          {t("card.counts", {
            sku: item.skuCount ?? 0,
            items: item.quantityTotal ?? item.itemCount ?? 0,
          })}
        </Text>
        <Text style={[styles.cardAmount, refundTone ? { color: REFUND_TONES[refundTone].color } : null]}>
          {formatSalesOrderMoney(item.actualAmount)}
        </Text>
      </View>
      {matched ? (
        <View style={styles.matched}>
          <Icon source="target" size={14} color={HB_COLORS.warning} />
          <Text numberOfLines={1} style={styles.matchedText}>
            {t("card.matched", {
              code: matched.itemNumber ?? matched.productCode,
              name: matched.productName ?? "",
              quantity: matched.quantity,
            })}
            {extra > 0 ? t("card.matchedMore", { count: extra }) : ""}
          </Text>
        </View>
      ) : null}
    </Pressable>
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
  description?: string;
  action?: string;
  onAction?: () => void;
}) {
  return (
    <View style={styles.state}>
      <Icon source={icon} size={40} color={HB_COLORS.outline} />
      <Text style={styles.stateTitle}>{title}</Text>
      {description ? <Text style={styles.stateText}>{description}</Text> : null}
      {action && onAction ? (
        <Button mode="outlined" compact onPress={onAction}>
          {action}
        </Button>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  screen: { flex: 1 },
  header: { flexDirection: "row", alignItems: "center", paddingRight: HB_SPACING.md },
  title: { flex: 1, fontWeight: "800", color: HB_COLORS.textPrimary },
  scopeBar: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: HB_SPACING.md,
    paddingBottom: HB_SPACING.xs,
  },
  scopeText: { flex: 1, fontSize: 12, color: HB_COLORS.textSecondary },
  searchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xxs,
    paddingHorizontal: HB_SPACING.sm,
  },
  search: { flex: 1, backgroundColor: HB_COLORS.surface },
  searchInput: { fontSize: 14, minHeight: 0 },
  searchAction: { margin: 0 },
  chips: {
    flexDirection: "row",
    gap: HB_SPACING.xxs,
    paddingHorizontal: HB_SPACING.sm,
    paddingTop: HB_SPACING.xs,
  },
  chip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingVertical: 5,
    paddingHorizontal: 10,
    borderRadius: 16,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.surface,
    maxWidth: "48%",
  },
  chipActive: { backgroundColor: "#E6F1FB", borderColor: HB_COLORS.brand },
  chipText: { fontSize: 12, color: HB_COLORS.textSecondary, flexShrink: 1 },
  chipTextActive: { color: HB_COLORS.action, fontWeight: "600" },
  summary: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    marginHorizontal: HB_SPACING.sm,
    marginTop: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  summaryText: { fontSize: 12, color: HB_COLORS.textSecondary },
  sortButton: { flexDirection: "row", alignItems: "center", gap: 4 },
  sortText: { fontSize: 12, color: HB_COLORS.action, fontWeight: "600" },
  content: { padding: HB_SPACING.sm, gap: HB_SPACING.xs },
  contentEmpty: { flexGrow: 1 },
  card: {
    backgroundColor: HB_COLORS.surface,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.sm,
    gap: 4,
  },
  cardRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 8 },
  cardTags: { flexDirection: "row", alignItems: "center", gap: 6 },
  cardAmounts: { marginTop: 2 },
  orderGuid: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  cardMeta: { fontSize: 12, color: HB_COLORS.textSecondary },
  cardMetaGrow: { flex: 1 },
  cardAmount: { fontSize: 15, fontWeight: "800", color: HB_COLORS.textPrimary },
  matched: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    marginTop: 4,
    paddingHorizontal: 8,
    paddingVertical: 4,
    borderRadius: 6,
    backgroundColor: "#FFF7E6",
  },
  matchedText: { flex: 1, fontSize: 11, color: HB_COLORS.warning },
  state: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.lg,
  },
  stateTitle: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary, textAlign: "center" },
  stateText: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center" },
  footer: { alignItems: "center", paddingVertical: HB_SPACING.sm },
  footerText: { fontSize: 12, color: HB_COLORS.textSecondary },
});
