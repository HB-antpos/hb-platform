import { useEffect, useMemo, useState } from "react";
import {
  FlatList,
  KeyboardAvoidingView,
  Modal,
  Platform,
  Pressable,
  StyleSheet,
  useWindowDimensions,
  View,
} from "react-native";
import {
  ActivityIndicator,
  Button,
  Divider,
  Searchbar,
  Text,
  TextInput,
} from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import {
  describeBranchPurchase,
  isValidProductInsightRange,
  sortBranchRows,
  summarizeBranchPurchases,
  type BranchSortKey,
} from "@/modules/product-insights/logic";
import type {
  ProductBranchSales,
  ProductInsightProduct,
  ProductInsightRange,
} from "@/modules/product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export interface BranchSalesSheetProps {
  visible: boolean;
  onClose: () => void;
  product: ProductInsightProduct | null;
  currentStoreCode: string;
  data: ProductBranchSales | null;
  loading?: boolean;
  error?: string | null;
  onRetry?: () => void;
  range: ProductInsightRange;
  onRangeChange: (range: ProductInsightRange) => void;
}

const money = (value: number) =>
  `A$${value.toLocaleString("en-AU", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
// 零值 / 无进货等弱化信息用的灰色；主题 token 里没有对应的禁用文字色。
const MUTED_TEXT = "#98A2B3";
// 进货数量来自进货单明细，可能带小数；最多保留两位，整数不补零。
const count = (value: number) =>
  value.toLocaleString("en-AU", { maximumFractionDigits: 2 });
const percent = (value: number, digits: number) =>
  `${(value * 100).toFixed(digits)}%`;

export function BranchSalesSheet({
  visible,
  onClose,
  product,
  currentStoreCode,
  data,
  loading = false,
  error,
  onRetry,
  range,
  onRangeChange,
}: BranchSalesSheetProps) {
  const { t } = useAppTranslation("productInsights");
  const [storeQuery, setStoreQuery] = useState("");
  // 默认沿用后端的按销量排序；点「进货 / 配货」表头可切到按进货排序，用来找进了货却卖不动的分店。
  const [sortKey, setSortKey] = useState<BranchSortKey>("quantity");
  const [startDate, setStartDate] = useState(range.startDate);
  const [endDate, setEndDate] = useState(range.endDate);
  const { height } = useWindowDimensions();
  const insets = useSafeAreaInsets();

  useEffect(() => {
    setStartDate(range.startDate);
    setEndDate(range.endDate);
  }, [range.endDate, range.startDate, visible]);

  const rows = useMemo(() => {
    const keyword = storeQuery.trim().toLowerCase();
    return sortBranchRows(
      (data?.rows ?? []).filter(
        (row) =>
          !keyword ||
          `${row.storeCode} ${row.storeName}`.toLowerCase().includes(keyword),
      ),
      sortKey,
    );
  }, [data?.rows, sortKey, storeQuery]);
  const purchaseTotals = data ? summarizeBranchPurchases(data) : null;
  const isWarehouse = data?.purchaseSource === "warehouse";
  const purchaseColumn = t(
    isWarehouse ? "branchSheet.allocation" : "branchSheet.purchase",
  );
  const quantityColumn = t("branchSheet.quantity");
  const canSortByPurchase = Boolean(data && data.purchaseSource !== null);
  const draftRange = { startDate, endDate };
  const dateValid = isValidProductInsightRange(draftRange);
  const hasUnappliedRange = Boolean(
    data &&
    (data.range.startDate !== startDate || data.range.endDate !== endDate),
  );
  const applyRange = () => {
    if (dateValid) onRangeChange(draftRange);
  };

  if (!visible) return null;
  const header = (
    <>
      <View style={styles.handle} />
      <View style={styles.header}>
        <View style={styles.heading}>
          <Text variant="titleMedium" style={styles.title}>
            {t(
              data?.scope === "authorized-pos"
                ? "branchSheet.authorizedTitle"
                : "branchSheet.title",
            )}
          </Text>
          {product ? (
            <Text numberOfLines={1} style={styles.subtitle}>
              {product.productName} · {product.productCode}
            </Text>
          ) : null}
        </View>
        <Button compact onPress={onClose}>
          {t("actions.close")}
        </Button>
      </View>
      <View style={styles.controls}>
        <View style={styles.rangeRow}>
          <TextInput
            mode="outlined"
            dense
            label={t("branchSheet.startDate")}
            value={startDate}
            onChangeText={setStartDate}
            placeholder="YYYY-MM-DD"
            keyboardType="numbers-and-punctuation"
            style={styles.dateInput}
          />
          <TextInput
            mode="outlined"
            dense
            label={t("branchSheet.endDate")}
            value={endDate}
            onChangeText={setEndDate}
            placeholder="YYYY-MM-DD"
            keyboardType="numbers-and-punctuation"
            style={styles.dateInput}
          />
          <Button
            mode="contained-tonal"
            compact
            onPress={applyRange}
            disabled={!dateValid || loading}
            contentStyle={styles.applyButton}
          >
            {t("actions.apply")}
          </Button>
        </View>
        {!dateValid ? (
          <Text style={styles.validation}>{t("branchSheet.invalidDate")}</Text>
        ) : null}
        {data ? (
          <Text style={styles.appliedRange}>
            {t("branchSheet.appliedRange", {
              startDate: data.range.startDate,
              endDate: data.range.endDate,
            })}
          </Text>
        ) : null}
        {hasUnappliedRange ? (
          <Text style={styles.pendingRange}>
            {t("branchSheet.pendingRange")}
          </Text>
        ) : null}
        <View style={styles.totalCard}>
          <Text variant="labelMedium" style={styles.totalLabel}>
            {data
              ? t("branchSheet.total", { count: data.includedStoreCount })
              : t("branchSheet.totalUnknown")}
          </Text>
          <View style={styles.totalValues}>
            <Metric
              label={t(
                isWarehouse
                  ? "branchSheet.totalAllocation"
                  : "branchSheet.totalPurchase",
              )}
              value={
                data?.purchaseQuantity != null
                  ? `${count(data.purchaseQuantity)} ${t("units.items")}`
                  : "—"
              }
            />
            <Metric
              label={t("branchSheet.totalQuantity")}
              value={
                data
                  ? `${data.quantity.toLocaleString()} ${t("units.items")}`
                  : "—"
              }
            />
            <Metric
              label={t("branchSheet.totalAmount")}
              value={data ? money(data.amount) : "—"}
            />
          </View>
          {data ? (
            <Text style={styles.scope}>
              {!purchaseTotals
                ? t("branchSheet.purchaseUnavailable")
                : purchaseTotals.sellThroughRate === null
                  ? t(
                      isWarehouse
                        ? "branchSheet.sellThroughSummaryNoneWarehouse"
                        : "branchSheet.sellThroughSummaryNone",
                    )
                  : t(
                      isWarehouse
                        ? "branchSheet.sellThroughSummaryWarehouse"
                        : "branchSheet.sellThroughSummary",
                      {
                        rate: percent(purchaseTotals.sellThroughRate, 1),
                        sales: data.quantity.toLocaleString(),
                        in: count(data.purchaseQuantity ?? 0),
                        purchaseStores: purchaseTotals.purchaseStoreCount,
                        salesStores: purchaseTotals.salesStoreCount,
                      },
                    )}
            </Text>
          ) : null}
          {data && data.scope !== "all-pos" ? (
            <Text style={styles.scope}>
              {t("branchSheet.authorizedScope", {
                count: data.includedStoreCount,
                total: data.totalPosStoreCount,
              })}
            </Text>
          ) : null}
        </View>
        <Searchbar
          value={storeQuery}
          onChangeText={setStoreQuery}
          placeholder={t("branchSheet.searchStore")}
          style={styles.search}
          inputStyle={styles.searchInput}
        />
      </View>
    </>
  );
  return (
    <Modal
      transparent
      visible
      animationType="slide"
      presentationStyle="overFullScreen"
      statusBarTranslucent
      onRequestClose={onClose}
    >
      <KeyboardAvoidingView
        style={styles.overlay}
        behavior={Platform.OS === "ios" ? "padding" : undefined}
      >
        <Pressable style={styles.backdrop} onPress={onClose} />
        <View
          style={[
            styles.sheet,
            {
              height: Math.min(
                Math.max(height * 0.86, 560),
                height - insets.top,
              ),
              paddingBottom: insets.bottom,
            },
          ]}
        >
          {header}
          <View style={styles.tableHeader}>
            <Text style={[styles.column, styles.storeColumn]}>
              {t("branchSheet.store")}
            </Text>
            <SortHeader
              label={purchaseColumn}
              active={sortKey === "purchase"}
              disabled={!canSortByPurchase}
              accessibilityLabel={t(
                sortKey === "purchase"
                  ? "branchSheet.sorted"
                  : "branchSheet.sortBy",
                { column: purchaseColumn },
              )}
              onPress={() => setSortKey("purchase")}
            />
            <SortHeader
              label={quantityColumn}
              active={sortKey === "quantity"}
              accessibilityLabel={t(
                sortKey === "quantity"
                  ? "branchSheet.sorted"
                  : "branchSheet.sortBy",
                { column: quantityColumn },
              )}
              onPress={() => setSortKey("quantity")}
            />
            <Text style={[styles.column, styles.amountColumn]}>
              {t("branchSheet.amount")}
            </Text>
          </View>
          {loading ? (
            <State icon="loading" label={t("states.loadingBranchSales")} />
          ) : error ? (
            <State
              icon="alert-circle-outline"
              label={error}
              action={onRetry ? t("actions.retry") : undefined}
              onAction={onRetry}
            />
          ) : !data ? (
            <State icon="chart-bar" label={t("states.noBranchSales")} />
          ) : (
            <FlatList
              style={styles.list}
              data={rows}
              keyExtractor={(item) => item.storeCode}
              ItemSeparatorComponent={Divider}
              ListEmptyComponent={
                <State icon="magnify" label={t("states.noStoreMatch")} />
              }
              renderItem={({ item }) => {
                const insight = describeBranchPurchase(item);
                const hint = rowHint(insight, isWarehouse, t);
                return (
                  <View
                    style={[
                      styles.row,
                      item.storeCode === currentStoreCode
                        ? styles.currentRow
                        : null,
                    ]}
                  >
                    <View style={styles.storeColumn}>
                      <Text
                        numberOfLines={2}
                        style={[styles.column, styles.storeName]}
                      >
                        {item.storeName}
                        <Text style={styles.storeCode}>
                          {" "}
                          · {item.storeCode}
                        </Text>
                      </Text>
                      {hint ? (
                        <View style={styles.hintRow}>
                          <Text
                            numberOfLines={1}
                            style={[
                              styles.hint,
                              insight.state === "oversold"
                                ? styles.hintWarning
                                : insight.state === "noPurchase" ||
                                    insight.state === "noSales"
                                  ? styles.hintMuted
                                  : null,
                            ]}
                          >
                            {hint}
                          </Text>
                          {insight.overstock ? (
                            <Text style={styles.overstockTag}>
                              {t("branchSheet.overstock")}
                            </Text>
                          ) : null}
                        </View>
                      ) : null}
                    </View>
                    <Text
                      style={[
                        styles.column,
                        styles.numberColumn,
                        item.purchaseQuantity
                          ? styles.purchaseValue
                          : styles.mutedValue,
                      ]}
                    >
                      {item.purchaseQuantity === null
                        ? "—"
                        : count(item.purchaseQuantity)}
                    </Text>
                    <Text
                      style={[
                        styles.column,
                        styles.numberColumn,
                        insight.state === "oversold"
                          ? styles.hintWarning
                          : null,
                        item.quantity === 0 ? styles.mutedValue : null,
                      ]}
                    >
                      {item.quantity.toLocaleString()}
                    </Text>
                    <Text
                      style={[
                        styles.column,
                        styles.amountColumn,
                        item.amount === 0 ? styles.mutedValue : null,
                      ]}
                    >
                      {money(item.amount)}
                    </Text>
                  </View>
                );
              }}
            />
          )}
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

/** 分店名下方的小字：售罄率或无进货 / 超卖 / 无销售提示；进货数据未知时不显示。 */
function rowHint(
  insight: ReturnType<typeof describeBranchPurchase>,
  isWarehouse: boolean,
  t: (key: string, options?: Record<string, unknown>) => string,
) {
  switch (insight.state) {
    case "unknown":
      return null;
    case "noPurchase":
      return t(
        isWarehouse
          ? "branchSheet.rowNoAllocation"
          : "branchSheet.rowNoPurchase",
      );
    case "noSales":
      return t("branchSheet.rowNoSales");
    case "oversold":
      return t(
        isWarehouse
          ? "branchSheet.rowOversoldWarehouse"
          : "branchSheet.rowOversold",
      );
    default:
      return t("branchSheet.rowSellThrough", {
        rate: percent(insight.sellThroughRate ?? 0, 0),
      });
  }
}

function SortHeader({
  label,
  active,
  disabled = false,
  accessibilityLabel,
  onPress,
}: {
  label: string;
  active: boolean;
  disabled?: boolean;
  accessibilityLabel: string;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      accessibilityState={{ selected: active, disabled }}
      disabled={disabled}
      onPress={onPress}
      hitSlop={{ top: 8, bottom: 8 }}
      style={styles.numberColumn}
    >
      <Text
        style={[
          styles.column,
          styles.sortHeader,
          active ? styles.sortHeaderActive : null,
        ]}
      >
        {label}
        {active ? " ↓" : ""}
      </Text>
    </Pressable>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.metric}>
      <Text variant="labelSmall" style={styles.metricLabel}>
        {label}
      </Text>
      <Text variant="titleSmall" style={styles.metricValue}>
        {value}
      </Text>
    </View>
  );
}
function State({
  icon,
  label,
  action,
  onAction,
}: {
  icon: string;
  label: string;
  action?: string;
  onAction?: () => void;
}) {
  return (
    <View style={styles.state}>
      {icon === "loading" ? (
        <ActivityIndicator color={HB_COLORS.brand} />
      ) : null}
      <Text style={styles.stateText}>{label}</Text>
      {action && onAction ? (
        <Button compact onPress={onAction}>
          {action}
        </Button>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  overlay: { flex: 1, justifyContent: "flex-end" },
  backdrop: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: "rgba(16,24,40,0.44)",
  },
  sheet: {
    backgroundColor: HB_COLORS.white,
    borderTopLeftRadius: HB_RADIUS.sheet,
    borderTopRightRadius: HB_RADIUS.sheet,
    overflow: "hidden",
  },
  handle: {
    width: 44,
    height: 4,
    marginTop: 10,
    marginBottom: 4,
    borderRadius: 2,
    backgroundColor: HB_COLORS.outline,
    alignSelf: "center",
  },
  header: {
    minHeight: 52,
    paddingLeft: HB_SPACING.md,
    paddingRight: HB_SPACING.xs,
    flexDirection: "row",
    alignItems: "center",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  heading: { flex: 1, minWidth: 0 },
  title: { fontWeight: "800", color: HB_COLORS.textPrimary },
  subtitle: { color: HB_COLORS.textSecondary, fontSize: 12 },
  controls: { padding: HB_SPACING.md, gap: HB_SPACING.xs },
  rangeRow: { flexDirection: "row", gap: HB_SPACING.xs, alignItems: "center" },
  dateInput: { flex: 1, minWidth: 0, backgroundColor: HB_COLORS.white },
  applyButton: { minHeight: 40 },
  validation: { color: HB_COLORS.danger, fontSize: 12 },
  appliedRange: { color: HB_COLORS.textSecondary, fontSize: 12 },
  pendingRange: { color: HB_COLORS.warning, fontSize: 12, fontWeight: "700" },
  totalCard: {
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#EAF2FF",
    gap: HB_SPACING.xs,
  },
  totalLabel: { color: HB_COLORS.action },
  totalValues: { flexDirection: "row", gap: HB_SPACING.md },
  metric: { flex: 1, minWidth: 0 },
  metricLabel: { color: HB_COLORS.textSecondary },
  metricValue: { color: HB_COLORS.textPrimary, fontWeight: "800" },
  scope: { color: HB_COLORS.textSecondary, fontSize: 12 },
  search: { height: 42, backgroundColor: HB_COLORS.surfaceMuted, elevation: 0 },
  searchInput: { minHeight: 0, fontSize: 14 },
  tableHeader: {
    flexDirection: "row",
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderBottomWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
  },
  list: { flex: 1 },
  row: {
    flexDirection: "row",
    paddingVertical: 11,
    paddingHorizontal: HB_SPACING.md,
    alignItems: "center",
  },
  currentRow: { backgroundColor: "#EAF2FF" },
  column: { color: HB_COLORS.textSecondary, fontSize: 12 },
  storeColumn: { flex: 1, minWidth: 0 },
  // 加了进货列后两列数字各 52、金额 84，分店列在 375 宽屏上仍有约 150。
  numberColumn: { width: 52, textAlign: "right" },
  amountColumn: { width: 84, textAlign: "right" },
  sortHeader: { textAlign: "right" },
  sortHeaderActive: { color: HB_COLORS.action, fontWeight: "700" },
  purchaseValue: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  mutedValue: { color: MUTED_TEXT },
  hintRow: { flexDirection: "row", alignItems: "center", gap: 4, marginTop: 1 },
  hint: { color: HB_COLORS.textSecondary, fontSize: 11, flexShrink: 1 },
  hintWarning: { color: HB_COLORS.warning },
  hintMuted: { color: MUTED_TEXT },
  overstockTag: {
    fontSize: 10,
    lineHeight: 14,
    paddingHorizontal: 5,
    borderRadius: 7,
    overflow: "hidden",
    color: HB_COLORS.warning,
    backgroundColor: "#FEF0C7",
  },
  storeName: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  storeCode: { color: HB_COLORS.textSecondary, fontWeight: "400" },
  state: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    paddingVertical: HB_SPACING.lg,
    gap: HB_SPACING.xs,
  },
  stateText: { color: HB_COLORS.textSecondary, textAlign: "center" },
});
