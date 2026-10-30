import { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, IconButton, Modal, Portal, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import {
  formatDateTime,
  formatMoney,
  formatOrderDate,
  formatQuantity,
} from "@/components/local-supplier-invoices/invoice-detail-format";
import { EmptyState } from "@/components/ui/EmptyState";
import { fetchInvoiceSalesAnalysis } from "@/modules/local-supplier-invoices/api";
import {
  SALES_ANALYSIS_SORTS,
  filterSalesAnalysisRows,
  getSalesAnalysisTotals,
  getSellThroughRatio,
  sortSalesAnalysisRows,
  type SalesAnalysisSort,
} from "@/modules/local-supplier-invoices/sales-analysis";
import type {
  InvoiceSalesAnalysis,
  InvoiceSalesAnalysisRow,
  LocalSupplierInvoice,
} from "@/modules/local-supplier-invoices/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

type Translate = (key: string, options?: Record<string, unknown>) => string;

export interface InvoiceSalesAnalysisViewProps {
  invoice: Pick<
    LocalSupplierInvoice,
    "invoiceGuid" | "invoiceNo" | "storeCode" | "storeName" | "supplierCode" | "supplierName" | "orderDate" | "inboundDate"
  >;
  onClose: () => void;
}

/** 销量占进货数量的百分比文案；进货数量缺失/为 0 时不显示。超过 100% 说明旧库存也在卖，如实显示。 */
function formatSellThrough(sales: number, purchased: number | null) {
  const ratio = getSellThroughRatio(sales, purchased);
  return ratio == null ? null : `${Math.round(ratio * 100)}%`;
}

const SalesRow = memo(function SalesRow({ row, t }: { row: InvoiceSalesAnalysisRow; t: Translate }) {
  const sellThrough = formatSellThrough(row.salesQty90, row.quantity);
  const noSales = row.salesQty90 <= 0;
  return (
    <View style={styles.rowCard}>
      <Text style={styles.rowName} numberOfLines={2}>{row.productName || row.itemNumber || row.productCode || "--"}</Text>
      <Text style={styles.rowMeta} numberOfLines={1}>
        {[row.itemNumber, row.barcode].filter(Boolean).join(" · ") || "--"}
      </Text>
      <Text style={styles.rowMeta} numberOfLines={1}>
        {t("salesAnalysis.purchased", { qty: formatQuantity(row.quantity) })}
        {" · "}
        {t("salesAnalysis.price", { purchase: formatMoney(row.purchasePrice), retail: formatMoney(row.retailPrice) })}
      </Text>

      <View style={styles.tiles}>
        <View style={styles.tile}>
          <Text style={styles.tileLabel}>{t("salesAnalysis.days30")}</Text>
          <Text style={styles.tileValue}>{formatQuantity(row.salesQty30)}</Text>
        </View>
        <View style={styles.tile}>
          <Text style={styles.tileLabel}>{t("salesAnalysis.days60")}</Text>
          <Text style={styles.tileValue}>{formatQuantity(row.salesQty60)}</Text>
        </View>
        <View style={[styles.tile, noSales ? styles.tileWarn : null]}>
          <Text style={[styles.tileLabel, noSales ? styles.tileLabelWarn : null]}>{t("salesAnalysis.days90")}</Text>
          <Text style={[styles.tileValue, noSales ? styles.tileValueWarn : null]}>
            {formatQuantity(row.salesQty90)}
            {sellThrough ? <Text style={styles.tileHint}>{`  ${sellThrough}`}</Text> : null}
          </Text>
        </View>
      </View>

      <Text style={styles.rowMeta} numberOfLines={2}>
        {row.previousPurchaseDate
          ? [
              t("salesAnalysis.previous", { date: formatOrderDate(row.previousPurchaseDate) }),
              row.previousToCurrentDays != null ? t("salesAnalysis.interval", { days: row.previousToCurrentDays }) : null,
              row.salesSincePreviousPurchase != null
                ? t("salesAnalysis.intervalSales", { qty: formatQuantity(row.salesSincePreviousPurchase) })
                : null,
            ].filter(Boolean).join(" · ")
          : t("salesAnalysis.noPrevious")}
      </Text>
    </View>
  );
});

/**
 * 单张进货单销量分析（只读）：每个商品本次进货后 30/60/90 天卖了多少，以及与上次进货的间隔和区间销量。
 * 数据与 Web「销量分析」页同一个接口；店长 / 仓库管理员只看，不涉及任何写入。
 */
export function InvoiceSalesAnalysisView({ invoice, onClose }: InvoiceSalesAnalysisViewProps) {
  const { t, language } = useAppTranslation(["localSupplierInvoices", "common"]);
  const guid = invoice.invoiceGuid;
  const [analysis, setAnalysis] = useState<InvoiceSalesAnalysis | null>(null);
  const [loading, setLoading] = useState(true);
  const [errorText, setErrorText] = useState("");
  const [searchInput, setSearchInput] = useState("");
  const [keyword, setKeyword] = useState("");
  const [sort, setSort] = useState<SalesAnalysisSort>("default");
  const requestIdRef = useRef(0);

  const load = useCallback(async () => {
    const requestId = ++requestIdRef.current;
    setLoading(true);
    setErrorText("");
    try {
      const result = await fetchInvoiceSalesAnalysis(guid);
      if (requestId !== requestIdRef.current) return;
      setAnalysis(result);
    } catch (error) {
      if (requestId !== requestIdRef.current) return;
      setErrorText(resolveLocalizedErrorMessage(error, { language, t, fallbackKey: "salesAnalysis.loadFailed" }));
    } finally {
      if (requestId === requestIdRef.current) setLoading(false);
    }
  }, [guid, language, t]);

  useEffect(() => {
    void load();
    // 关闭页面后丢弃在途请求的结果，避免回写已卸载的组件。
    return () => { requestIdRef.current += 1; };
  }, [load]);

  // 搜索防抖：与明细页一致 250ms。
  useEffect(() => {
    const next = searchInput.trim();
    if (next === keyword) return;
    const timeout = setTimeout(() => setKeyword(next), 250);
    return () => clearTimeout(timeout);
  }, [keyword, searchInput]);

  const allRows = useMemo(() => analysis?.items ?? [], [analysis]);
  const visibleRows = useMemo(
    () => sortSalesAnalysisRows(filterSalesAnalysisRows(allRows, keyword), sort),
    [allRows, keyword, sort]
  );
  const totals = useMemo(() => getSalesAnalysisTotals(visibleRows), [visibleRows]);
  const filtered = keyword.length > 0;

  const renderItem = useCallback(
    ({ item }: { item: InvoiceSalesAnalysisRow }) => (
      <View style={styles.rowWrap}>
        <SalesRow row={item} t={t as Translate} />
      </View>
    ),
    [t]
  );

  const header = (
    <View>
      <View style={styles.summaryCard}>
        <Text style={styles.supplier} numberOfLines={1}>
          {analysis?.supplierName || invoice.supplierName || invoice.supplierCode || "--"}
        </Text>
        <Text style={styles.context} numberOfLines={1}>
          {analysis?.invoiceNo || invoice.invoiceNo || "--"} · {analysis?.storeName || invoice.storeName || invoice.storeCode || "--"}
        </Text>
        <Text style={styles.context} numberOfLines={1}>
          {t("labels.orderDate")} {formatOrderDate(analysis?.orderDate ?? invoice.orderDate)} · {t("labels.inboundDate")} {formatOrderDate(analysis?.inboundDate ?? invoice.inboundDate)}
        </Text>
        {analysis?.salesStatisticLastUpdate ? (
          <Text style={styles.context} numberOfLines={1}>
            {t("salesAnalysis.updatedAt", { time: formatDateTime(analysis.salesStatisticLastUpdate) })}
          </Text>
        ) : null}

        {analysis ? (
          <View style={styles.totalsGrid}>
            <View style={styles.totalCell}>
              <Text style={styles.metricLabel}>{t("salesAnalysis.products")}</Text>
              <Text style={styles.totalValue}>{formatQuantity(totals.rowCount)}</Text>
            </View>
            <View style={styles.totalCell}>
              <Text style={styles.metricLabel}>{t("salesAnalysis.sales30")}</Text>
              <Text style={styles.totalValue}>{formatQuantity(totals.salesQty30)}</Text>
            </View>
            <View style={styles.totalCell}>
              <Text style={styles.metricLabel}>{t("salesAnalysis.sales90")}</Text>
              <Text style={styles.totalValue}>{formatQuantity(totals.salesQty90)}</Text>
            </View>
            <View style={[styles.totalCell, totals.zeroSales90Count > 0 ? styles.totalCellWarn : null]}>
              <Text style={[styles.metricLabel, totals.zeroSales90Count > 0 ? styles.tileLabelWarn : null]}>
                {t("salesAnalysis.zeroSales90")}
              </Text>
              <Text style={[styles.totalValue, totals.zeroSales90Count > 0 ? styles.tileValueWarn : null]}>
                {formatQuantity(totals.zeroSales90Count)}
              </Text>
            </View>
          </View>
        ) : null}
      </View>

      {analysis ? <Text style={styles.note}>{t("salesAnalysis.note")}</Text> : null}

      {analysis && allRows.length ? (
        <>
          <TextInput
            dense
            mode="outlined"
            placeholder={t("salesAnalysis.searchPlaceholder")}
            value={searchInput}
            onChangeText={setSearchInput}
            left={<TextInput.Icon icon="magnify" />}
            style={styles.search}
          />
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.sortRow} keyboardShouldPersistTaps="handled">
            {SALES_ANALYSIS_SORTS.map((option) => {
              const selected = sort === option;
              return (
                <Pressable
                  key={option}
                  accessibilityRole="button"
                  accessibilityState={{ selected }}
                  onPress={() => setSort(option)}
                  style={[styles.sortChip, selected ? styles.sortChipSelected : null]}
                >
                  <Text style={[styles.sortChipText, selected ? styles.sortChipTextSelected : null]}>
                    {t(`salesAnalysis.sort.${option}`)}
                  </Text>
                </Pressable>
              );
            })}
          </ScrollView>
        </>
      ) : null}
    </View>
  );

  return (
    <Portal>
      <Modal visible onDismiss={onClose} style={styles.overlay} contentContainerStyle={styles.modal}>
        <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.screen}>
          <View style={styles.navBar}>
            <IconButton accessibilityLabel={t("actions.returnToInvoices")} icon="chevron-left" onPress={onClose} style={styles.navButton} />
            <Text style={styles.navTitle} numberOfLines={1}>{t("salesAnalysis.title")}</Text>
          </View>

          <FlatList
            data={loading || errorText ? [] : visibleRows}
            keyExtractor={(row) => row.detailGuid}
            renderItem={renderItem}
            ListHeaderComponent={header}
            ListEmptyComponent={loading ? (
              <View style={styles.centerBox}>
                <ActivityIndicator />
                <Text>{t("salesAnalysis.loading")}</Text>
              </View>
            ) : errorText ? (
              <View style={styles.centerBox}>
                <Text style={styles.errorText}>{errorText}</Text>
                <Button icon="refresh" mode="outlined" onPress={() => void load()}>
                  {t("common:actions.retry")}
                </Button>
              </View>
            ) : (
              <EmptyState title={filtered ? t("salesAnalysis.emptyFiltered") : t("salesAnalysis.empty")} />
            )}
            style={styles.list}
            contentContainerStyle={styles.listContent}
            initialNumToRender={6}
            maxToRenderPerBatch={6}
            windowSize={7}
            keyboardShouldPersistTaps="handled"
          />

          {analysis && !errorText ? (
            <View style={styles.summaryBar}>
              <Text style={styles.summaryText}>
                {t("salesAnalysis.visibleOfTotal", { visible: visibleRows.length, total: allRows.length })}
              </Text>
            </View>
          ) : null}
        </SafeAreaView>
      </Modal>
    </Portal>
  );
}

const styles = StyleSheet.create({
  overlay: { justifyContent: "flex-start", marginBottom: 0, marginTop: 0, paddingBottom: 0, paddingTop: 0 },
  modal: { alignSelf: "stretch", backgroundColor: HB_COLORS.white, height: "100%", width: "100%" },
  screen: { flex: 1, backgroundColor: HB_COLORS.background },
  navBar: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    paddingHorizontal: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surface,
    borderBottomWidth: 1,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  navButton: { margin: 0 },
  navTitle: { flex: 1, fontSize: 18, fontWeight: "700", color: HB_COLORS.textPrimary },
  list: { flex: 1 },
  listContent: { paddingBottom: HB_SPACING.xs },
  rowWrap: { paddingHorizontal: HB_SPACING.sm, paddingTop: HB_SPACING.xs },
  summaryCard: {
    margin: HB_SPACING.sm,
    marginBottom: 0,
    padding: HB_SPACING.sm,
    gap: 4,
    backgroundColor: HB_COLORS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
  },
  supplier: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary },
  context: { fontSize: 12, color: HB_COLORS.textSecondary },
  totalsGrid: { flexDirection: "row", gap: 6, marginTop: 4 },
  totalCell: { flex: 1, backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.control, paddingHorizontal: 8, paddingVertical: 6, gap: 1 },
  totalCellWarn: { backgroundColor: "#FEF0C7" },
  metricLabel: { fontSize: 11, color: HB_COLORS.textSecondary },
  totalValue: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  note: { marginHorizontal: HB_SPACING.md, marginTop: HB_SPACING.xs, fontSize: 11, color: HB_COLORS.textSecondary },
  search: { marginHorizontal: HB_SPACING.sm, marginTop: HB_SPACING.xs, backgroundColor: HB_COLORS.white },
  sortRow: { flexDirection: "row", gap: 6, paddingHorizontal: HB_SPACING.sm, paddingTop: HB_SPACING.xs },
  sortChip: {
    minHeight: 32,
    justifyContent: "center",
    paddingHorizontal: 12,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  sortChipSelected: { borderColor: HB_COLORS.brand, backgroundColor: "#EAF2FF" },
  sortChipText: { fontSize: 12, color: HB_COLORS.textSecondary },
  sortChipTextSelected: { color: "#073B83", fontWeight: "700" },
  rowCard: {
    padding: HB_SPACING.sm,
    gap: 4,
    backgroundColor: HB_COLORS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
  },
  rowName: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  rowMeta: { fontSize: 12, color: HB_COLORS.textSecondary },
  tiles: { flexDirection: "row", gap: 6, marginVertical: 2 },
  tile: { flex: 1, backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.control, paddingHorizontal: 8, paddingVertical: 6, gap: 1 },
  tileWarn: { backgroundColor: "#FEF0C7" },
  tileLabel: { fontSize: 11, color: HB_COLORS.textSecondary },
  tileLabelWarn: { color: "#93370D" },
  tileValue: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  tileValueWarn: { color: "#93370D" },
  tileHint: { fontSize: 11, fontWeight: "400", color: HB_COLORS.textSecondary },
  centerBox: { alignItems: "center", gap: HB_SPACING.xs, padding: HB_SPACING.lg },
  errorText: { color: HB_COLORS.danger, textAlign: "center" },
  summaryBar: {
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surface,
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  summaryText: { fontSize: 12, color: HB_COLORS.textSecondary },
});
