import { useMemo } from "react";
import { StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { MOVEMENT_COLORS } from "@/components/product-insights/MovementTrendChart";
import {
  buildWeeklyTrend,
  daysSinceLastInbound,
  formatPercent,
  formatQuantity,
  seasonPerDay,
  sellThroughRate,
  summarizePace,
} from "@/modules/seasonal-product-insights/logic";
import type { SeasonalProductInsight } from "@/modules/seasonal-product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const oneDecimal = (value: number) => String(Math.round(value * 10) / 10);

function money(value: number) {
  return `$${value.toLocaleString("en-AU", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

/**
 * 进销概览：把原来三个独立的 KPI 合成一张卡 ——
 * 进货 / 销量 / 理论存货并排，下面一条「已售 vs 剩余」比例条直接读出售罄率，
 * 再给出按近几周销速估算的卖完天数。
 */
export function SeasonalOverviewCard({ data, today }: { data: SeasonalProductInsight; today: string | null }) {
  const { t } = useAppTranslation("seasonalProductInsights");
  const inbound = data.inbound.quantity;
  const sales = data.sales.quantity;
  const stock = data.theoreticalStock;
  const rate = sellThroughRate(inbound, sales);
  const over = sales - inbound;
  const pace = useMemo(
    () => summarizePace(buildWeeklyTrend(data.sales.daily, data.inbound.records, data.ranges.sales), stock),
    [data.sales.daily, data.inbound.records, data.ranges.sales, stock],
  );
  const lastInboundDays = today ? daysSinceLastInbound(data.inbound.records, today) : null;
  const soldShare = inbound > 0 ? Math.min(1, sales / inbound) : 0;

  return (
    <View style={styles.card}>
      <View style={styles.head}>
        <Text style={styles.title}>{t("overview.title")}</Text>
        <Text style={styles.sub}>{t("overview.subtitle")}</Text>
      </View>

      <View style={styles.cols}>
        <View style={styles.col}>
          <View style={styles.labelRow}>
            <Icon source="package-down" size={14} color={MOVEMENT_COLORS.in} />
            <Text style={[styles.label, { color: MOVEMENT_COLORS.in }]}>{t("overview.inbound")}</Text>
          </View>
          <Text style={styles.big} numberOfLines={1} adjustsFontSizeToFit>
            {formatQuantity(inbound)}
          </Text>
          <Text style={styles.hint}>{t("overview.docs", { count: data.inbound.documentCount })}</Text>
        </View>
        <View style={[styles.col, styles.colDivider]}>
          <View style={styles.labelRow}>
            <Icon source="shopping-outline" size={14} color={MOVEMENT_COLORS.sales} />
            <Text style={[styles.label, { color: MOVEMENT_COLORS.sales }]}>{t("overview.sales")}</Text>
          </View>
          <Text style={styles.big} numberOfLines={1} adjustsFontSizeToFit>
            {formatQuantity(sales)}
          </Text>
          <Text style={styles.hint}>{money(data.sales.amount)}</Text>
        </View>
        <View style={[styles.col, styles.colDivider]}>
          <Text style={[styles.label, { color: HB_COLORS.textSecondary }]}>{t("overview.stock")}</Text>
          <Text style={[styles.big, stock < 0 ? styles.negative : null]} numberOfLines={1} adjustsFontSizeToFit>
            {formatQuantity(stock)}
          </Text>
          <Text style={styles.hint}>{t("overview.unit")}</Text>
        </View>
      </View>

      {inbound > 0 ? (
        <View style={styles.stack}>
          <View style={styles.bar} accessible accessibilityLabel={t("overview.stackA11y", { percent: formatPercent(rate ?? 0) })}>
            <View style={[styles.barSold, { width: `${soldShare * 100}%` }]} />
          </View>
          <View style={styles.legend}>
            <Text style={[styles.legendText, { color: MOVEMENT_COLORS.sales }]}>
              {t("overview.stackSold", { sold: formatQuantity(sales), percent: formatPercent(rate ?? 0) })}
            </Text>
            {over > 0 ? null : (
              <Text style={[styles.legendText, { color: MOVEMENT_COLORS.in }]}>
                {t("overview.stackLeft", {
                  left: formatQuantity(stock),
                  percent: formatPercent(Math.max(0, 1 - (rate ?? 0))),
                })}
              </Text>
            )}
          </View>
          {over > 0 ? <Text style={styles.warn}>{t("overview.stackOver", { over: formatQuantity(over) })}</Text> : null}
        </View>
      ) : (
        <Text style={[styles.warn, styles.stackNone]}>{t("overview.stackNone")}</Text>
      )}

      {pace ? (
        <View style={styles.pace}>
          <Icon source="flash-outline" size={16} color={HB_COLORS.warning} />
          <Text style={styles.paceText}>
            {pace.daysLeft != null
              ? t("overview.pace", {
                  weeks: pace.weeks,
                  recent: oneDecimal(pace.recentPerDay),
                  season: oneDecimal(seasonPerDay(sales, data.ranges.sales)),
                  days: pace.daysLeft,
                })
              : t("overview.paceNoStock", {
                  weeks: pace.weeks,
                  recent: oneDecimal(pace.recentPerDay),
                  season: oneDecimal(seasonPerDay(sales, data.ranges.sales)),
                })}
            {lastInboundDays == null
              ? ""
              : lastInboundDays === 0
                ? t("overview.lastInboundToday")
                : t("overview.lastInbound", { days: lastInboundDays })}
          </Text>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.white, overflow: "hidden" },
  head: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs, padding: HB_SPACING.sm, paddingBottom: 6 },
  title: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  sub: { fontSize: 11, color: "#667085", flexShrink: 1 },
  cols: { flexDirection: "row", paddingHorizontal: HB_SPACING.sm },
  col: { flex: 1, minWidth: 0, gap: 2 },
  colDivider: { marginLeft: HB_SPACING.sm, paddingLeft: HB_SPACING.sm, borderLeftWidth: StyleSheet.hairlineWidth, borderLeftColor: "#EEF2F7" },
  labelRow: { flexDirection: "row", alignItems: "center", gap: 4 },
  label: { fontSize: 12.5, fontWeight: "700" },
  big: { fontSize: 24, fontWeight: "800", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  hint: { fontSize: 11, color: "#667085" },
  negative: { color: HB_COLORS.danger },
  stack: { marginHorizontal: HB_SPACING.sm, marginTop: 14, gap: 6 },
  bar: { height: 14, borderRadius: 7, backgroundColor: MOVEMENT_COLORS.inSoft, overflow: "hidden" },
  barSold: { height: 14, borderRadius: 7, backgroundColor: MOVEMENT_COLORS.salesBar },
  legend: { flexDirection: "row", justifyContent: "space-between" },
  legendText: { fontSize: 11.5, fontWeight: "700", fontVariant: ["tabular-nums"] },
  warn: { fontSize: 11.5, color: HB_COLORS.danger },
  stackNone: { marginHorizontal: HB_SPACING.sm, marginTop: 12 },
  pace: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    margin: HB_SPACING.sm,
    padding: 10,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#F9FAFB",
  },
  paceText: { flex: 1, fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
});
