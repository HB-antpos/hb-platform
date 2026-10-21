import type { ReactNode, RefObject } from "react";
import { Image, Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import type {
  ProductBranchBreakdownRow,
  ProductReportProductRow,
  SupplierBranchBreakdownRow,
} from "@/modules/product-report/api";
import {
  formatCount,
  formatGrossMarginRate,
  formatNullableMoney,
  formatNullableWholeMoney,
} from "@/modules/product-report/product-report-format";
import { MetricGrid } from "@/modules/product-report/product-report-card-parts";
import type { ReportSortValues } from "@/modules/product-report/sorting";
import { formatMoney, formatWholeMoney } from "@/modules/reports/format";
import { GrowthPill, RankBadge, REPORT_UI } from "@/modules/reports/report-card-parts";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/**
 * 商品报告里「一行一张卡」的共用展示：商品卡、供应商分店行、商品分店行。
 * 主页面的商品明细与供应商弹窗用的是同一套，字段不会因为入口不同而漂移。
 */

const LINK_COLOR = "#0958D9";

// 供应商分店弹窗：均价在数量为 0 时是 null，排序时统一排在最后。
export const SUPPLIER_BRANCH_SORT_VALUES: ReportSortValues<SupplierBranchBreakdownRow> = {
  amount: (row) => [row.revenue, row.compareRevenue],
  quantity: (row) => [row.totalQuantity, row.compareTotalQuantity],
  unitPrice: (row) => [row.averagePrice, row.compareAveragePrice],
};

// 商品分店均价由后端给出，数量为 0 时是 0，与商品明细的服务端排序口径一致。
export const PRODUCT_BRANCH_SORT_VALUES: ReportSortValues<ProductBranchBreakdownRow> = {
  amount: (row) => [row.salesAmount, row.compareSalesAmount],
  quantity: (row) => [row.quantity, row.compareQuantity],
  unitPrice: (row) => [row.averageUnitPrice, row.compareAverageUnitPrice],
};

interface CostLabels {
  growthNewLabel: string;
  costPendingLabel: string;
  costNoActivityLabel: string;
}

// 商品卡：图片 + 货号/名称/金额/增长率在上一行；四格指标占满整卡宽度，英文「Gross profit / Gross margin」才不会被截断。
// 两个供应商页签和弹窗共用，只显示本期值（同期金额只用于算增长率）。
export function ProductReportCard({
  item,
  rowNumber,
  growthNewLabel,
  costPendingLabel,
  costNoActivityLabel,
  onPress,
  trailingHint,
}: CostLabels & {
  item: ProductReportProductRow;
  rowNumber: number;
  onPress: () => void;
  /** 金额行右侧的点击提示（如「分店 ›」）；主页面的商品卡不需要。 */
  trailingHint?: string;
}) {
  const { t } = useAppTranslation("common");
  const growthPill = <GrowthPill current={item.salesAmount} compare={item.compareSalesAmount} newLabel={growthNewLabel} />;
  return (
    <Pressable
      style={styles.productCard}
      onPress={onPress}
      accessibilityRole="button"
      accessibilityLabel={[item.itemNumber || item.productCode, item.productName || "", t("productReport.drilldown.product")].filter(Boolean).join(" ")}
    >
      <View style={styles.productCardTop}>
        <View style={styles.productImageColumn}>
          {item.productImage ? (
            <Image source={{ uri: item.productImage }} style={styles.productImage} resizeMode="cover" />
          ) : (
            <View style={styles.productImagePlaceholder}>
              <Text style={styles.placeholderText}>{t("productReport.columns.image")}</Text>
            </View>
          )}
          <View style={styles.rankBadge} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
            <Text style={styles.rankBadgeText}>{String(rowNumber)}</Text>
          </View>
        </View>
        <View style={styles.productCardBody}>
          <Text style={styles.productCode} numberOfLines={1}>{item.itemNumber || "--"}</Text>
          <Text style={styles.productName} numberOfLines={1}>{item.productName || "--"}</Text>
          <View style={styles.amountLine}>
            {trailingHint ? (
              // 弹窗里的商品卡：金额与增长率靠左，右侧给出「分店 ›」点击提示。
              <>
                <View style={styles.amountGroup}>
                  <Text style={styles.cardAmount} numberOfLines={1}>{formatWholeMoney(item.salesAmount)}</Text>
                  {growthPill}
                </View>
                <Text style={styles.trailingHint}>{trailingHint}</Text>
              </>
            ) : (
              <>
                <Text style={styles.cardAmount} numberOfLines={1}>{formatWholeMoney(item.salesAmount)}</Text>
                {growthPill}
              </>
            )}
          </View>
        </View>
      </View>
      <MetricGrid
        cells={[
          { key: "quantity", label: t("productReport.columns.quantity"), value: formatCount(item.quantity) },
          { key: "averagePrice", label: t("productReport.columns.averagePrice"), value: formatMoney(item.averageUnitPrice) },
          { key: "grossProfit", label: t("productReport.metrics.grossProfit"), value: formatNullableWholeMoney(item.grossProfit) },
          { key: "grossMargin", label: t("productReport.metrics.grossMarginRate"), value: formatGrossMarginRate(item.grossMarginRate, item.costStatus, costPendingLabel, costNoActivityLabel), pending: item.costStatus === "Missing" },
        ]}
      />
    </Pressable>
  );
}

export function DrilldownRowHead({
  rowNumber,
  branchName,
  branchCode,
  current,
  compare,
  growthNewLabel,
  trailingHint,
}: {
  rowNumber: number;
  branchName: string;
  branchCode: string;
  current: number;
  compare: number;
  growthNewLabel: string;
  trailingHint?: string;
}) {
  return (
    <View style={styles.drillRowHead}>
      <View style={styles.rowNumberColumn}>
        <RankBadge value={String(rowNumber)} />
      </View>
      <View style={styles.drillBranchName}>
        <Text style={styles.drillBranchTitle} numberOfLines={1}>{branchName || branchCode}</Text>
        <Text style={styles.caption} numberOfLines={1}>{branchCode}</Text>
      </View>
      <GrowthPill current={current} compare={compare} newLabel={growthNewLabel} />
      {trailingHint ? <Text style={styles.trailingHint}>{trailingHint}</Text> : null}
    </View>
  );
}

function PressableRow({
  onPress,
  accessibilityLabel,
  rowRef,
  children,
}: {
  onPress?: () => void;
  accessibilityLabel: string;
  rowRef?: RefObject<View | null>;
  children: ReactNode;
}) {
  if (!onPress) {
    return <View ref={rowRef} style={styles.drillRow}>{children}</View>;
  }
  return (
    <Pressable
      ref={rowRef}
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      onPress={onPress}
      style={styles.drillRow}
    >
      {children}
    </Pressable>
  );
}

export function SupplierBranchRow({
  row,
  rowNumber,
  growthNewLabel,
  costPendingLabel,
  costNoActivityLabel,
  showComparison,
  rowRef,
  onPress,
  trailingHint,
}: CostLabels & {
  row: SupplierBranchBreakdownRow;
  rowNumber: number;
  showComparison: boolean;
  rowRef?: RefObject<View | null>;
  /** 传入后整行可点（弹窗里点分店看该分店的商品明细）。 */
  onPress?: () => void;
  trailingHint?: string;
}) {
  const { t } = useAppTranslation("common");
  // 同期值一律经过 compareText：只有中国供应商下钻（showComparison）才显示同期小字。
  const compareText = (value: string) => (showComparison ? value : null);
  return (
    <PressableRow onPress={onPress} accessibilityLabel={row.branchName || row.branchCode} rowRef={rowRef}>
      <DrilldownRowHead
        rowNumber={rowNumber}
        branchName={row.branchName}
        branchCode={row.branchCode}
        current={row.revenue}
        compare={row.compareRevenue}
        growthNewLabel={growthNewLabel}
        trailingHint={trailingHint}
      />
      <MetricGrid
        variant="plain"
        style={styles.drillGrid}
        cells={[
          { key: "amount", label: t("productReport.columns.amount"), value: formatWholeMoney(row.revenue), compare: compareText(formatWholeMoney(row.compareRevenue)) },
          { key: "quantity", label: t("productReport.columns.quantity"), value: formatCount(row.totalQuantity), compare: compareText(row.compareTotalQuantity === null ? "—" : formatCount(row.compareTotalQuantity)) },
          { key: "averagePrice", label: t("productReport.columns.averagePrice"), value: formatNullableMoney(row.averagePrice), compare: compareText(formatNullableMoney(row.compareAveragePrice)) },
          { key: "grossProfit", label: t("productReport.metrics.grossProfit"), value: formatNullableWholeMoney(row.grossProfit), compare: compareText(formatNullableWholeMoney(row.compareGrossProfit)) },
          { key: "grossMargin", label: t("productReport.metrics.grossMarginRate"), value: formatGrossMarginRate(row.grossMarginRate, row.costStatus, costPendingLabel, costNoActivityLabel), pending: row.costStatus === "Missing", compare: compareText(formatGrossMarginRate(row.compareGrossMarginRate, row.compareCostStatus, costPendingLabel, costNoActivityLabel)) },
        ]}
      />
    </PressableRow>
  );
}

// 商品分店：数量、金额、均价、毛利额、毛利率五项；原表就带同期双行，这里保留为每项下方的小字，不减少数据。
export function ProductBranchRow({
  row,
  rowNumber,
  growthNewLabel,
  costPendingLabel,
  costNoActivityLabel,
  rowRef,
}: CostLabels & {
  row: ProductBranchBreakdownRow;
  rowNumber: number;
  rowRef?: RefObject<View | null>;
}) {
  const { t } = useAppTranslation("common");
  return (
    <View ref={rowRef} style={styles.drillRow}>
      <DrilldownRowHead
        rowNumber={rowNumber}
        branchName={row.branchName}
        branchCode={row.branchCode}
        current={row.salesAmount}
        compare={row.compareSalesAmount}
        growthNewLabel={growthNewLabel}
      />
      <MetricGrid
        variant="plain"
        style={styles.drillGrid}
        cells={[
          { key: "quantity", label: t("productReport.columns.quantity"), value: formatCount(row.quantity), compare: formatCount(row.compareQuantity) },
          { key: "amount", label: t("productReport.columns.amount"), value: formatWholeMoney(row.salesAmount), compare: formatWholeMoney(row.compareSalesAmount) },
          { key: "averagePrice", label: t("productReport.columns.averagePrice"), value: formatMoney(row.averageUnitPrice), compare: formatMoney(row.compareAverageUnitPrice) },
          { key: "grossProfit", label: t("productReport.metrics.grossProfit"), value: formatNullableWholeMoney(row.grossProfit), compare: formatNullableWholeMoney(row.compareGrossProfit) },
          { key: "grossMargin", label: t("productReport.metrics.grossMarginRate"), value: formatGrossMarginRate(row.grossMarginRate, row.costStatus, costPendingLabel, costNoActivityLabel), pending: row.costStatus === "Missing", compare: formatGrossMarginRate(row.compareGrossMarginRate, row.compareCostStatus, costPendingLabel, costNoActivityLabel) },
        ]}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  caption: {
    color: REPORT_UI.muted,
    fontSize: 11.5,
    lineHeight: 16,
    fontVariant: ["tabular-nums"],
  },
  rowNumberColumn: {
    width: 24,
    minWidth: 0,
    paddingTop: 1,
  },
  trailingHint: {
    color: LINK_COLOR,
    fontSize: 12,
    fontWeight: "700",
  },
  cardAmount: {
    color: REPORT_UI.text,
    fontSize: 17,
    lineHeight: 22,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  productCard: {
    gap: 8,
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 14,
    backgroundColor: REPORT_UI.card,
    paddingVertical: 10,
    paddingLeft: 10,
    paddingRight: 12,
  },
  productCardTop: {
    flexDirection: "row",
    gap: 10,
  },
  productImageColumn: {
    width: 62,
    height: 62,
    position: "relative",
    overflow: "hidden",
    borderRadius: 10,
  },
  productImage: {
    width: 62,
    height: 62,
    backgroundColor: "#F2F4F7",
  },
  productImagePlaceholder: {
    width: 62,
    height: 62,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#F2F4F7",
  },
  placeholderText: {
    color: REPORT_UI.muted,
    fontSize: 10,
  },
  rankBadge: {
    position: "absolute",
    top: 0,
    left: 0,
    minWidth: 18,
    alignItems: "center",
    borderBottomRightRadius: 8,
    backgroundColor: "rgba(16,24,40,0.78)",
    paddingHorizontal: 6,
    paddingVertical: 1,
  },
  rankBadgeText: {
    color: "#FFFFFF",
    fontSize: 10.5,
    lineHeight: 14,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  productCardBody: {
    flex: 1,
    minWidth: 0,
  },
  productCode: {
    color: REPORT_UI.text,
    fontSize: 14,
    lineHeight: 19,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  productName: {
    color: REPORT_UI.muted,
    fontSize: 12,
    lineHeight: 16,
  },
  amountLine: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    marginTop: 3,
  },
  amountGroup: {
    flexShrink: 1,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  drillRow: {
    borderBottomWidth: 1,
    borderBottomColor: REPORT_UI.divider,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  drillRowHead: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  drillBranchName: {
    flex: 1,
    minWidth: 0,
  },
  drillBranchTitle: {
    color: REPORT_UI.text,
    fontSize: 14,
    fontWeight: "700",
  },
  drillGrid: {
    marginTop: 7,
  },
});
