import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { MOVEMENT_COLORS } from "@/components/product-insights/MovementTrendChart";
import {
  buildWeekEntries,
  buildWeeklyLedger,
  daysBetweenInclusive,
  formatQuantity,
  rangesDiffer,
  shortDate,
  unionRange,
  weekdayIndex,
} from "@/modules/seasonal-product-insights/logic";
import type { SeasonalProductInsight, WeeklyLedgerRow } from "@/modules/seasonal-product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** 默认只展开最近 6 周，更早的折叠起来。 */
const PREVIEW_WEEKS = 6;

/**
 * 按周进销：取代原来「进货记录 / 日销量」二选一的页签。
 * 每周一行同时显示进货、销量和周末存货；点一周展开当周每天，进货单与日销量按日期混排。
 */
export function SeasonalWeeklyLedger({ data, today }: { data: SeasonalProductInsight; today: string | null }) {
  const { t } = useAppTranslation("seasonalProductInsights");
  const [expanded, setExpanded] = useState<string | null>(null);
  const [showAll, setShowAll] = useState(false);
  const differ = rangesDiffer(data.ranges);
  // 周末存货 = 从区间起点累计的「进货 − 销量」，只有两个区间一致时才成立；
  // 区间不一致时按并集排周，保证两边的记录都放得下，存货列整列隐藏。
  const range = useMemo(() => unionRange(data.ranges), [data.ranges]);
  const rows = useMemo(
    () => buildWeeklyLedger(data.sales.daily, data.inbound.records, range, !differ).reverse(),
    [data.sales.daily, data.inbound.records, differ, range],
  );
  const visible = showAll ? rows : rows.slice(0, PREVIEW_WEEKS);
  const hidden = rows.length - visible.length;

  return (
    <View style={styles.card}>
      <View style={styles.head}>
        <Text style={styles.title}>{t("ledger.title")}</Text>
        <Text style={styles.sub}>{t("ledger.subtitle")}</Text>
      </View>
      <View style={styles.tableHead}>
        <Text style={[styles.th, styles.colWeek]}>{t("ledger.week")}</Text>
        <Text style={[styles.th, styles.colNum]}>{t("ledger.inbound")}</Text>
        <Text style={[styles.th, styles.colNum]}>{t("ledger.sales")}</Text>
        {differ ? null : <Text style={[styles.th, styles.colStock]}>{t("ledger.endStock")}</Text>}
      </View>
      {!rows.length ? <Text style={styles.empty}>{t("ledger.empty")}</Text> : null}
      {visible.map((row) => (
        <LedgerRow
          key={row.startDate}
          row={row}
          data={data}
          today={today}
          showStock={!differ}
          open={expanded === row.startDate}
          onToggle={() => setExpanded((current) => (current === row.startDate ? null : row.startDate))}
        />
      ))}
      {hidden > 0 ? (
        <Pressable accessibilityRole="button" onPress={() => setShowAll(true)} style={styles.more}>
          <Text style={styles.moreText}>{t("ledger.showEarlier", { count: hidden })}</Text>
          <Icon source="chevron-down" size={16} color={HB_COLORS.action} />
        </Pressable>
      ) : null}
      <View style={styles.total}>
        <Text style={[styles.totalLabel, styles.colWeek]}>{t("ledger.total")}</Text>
        <Text style={[styles.totalValue, styles.colNum, { color: MOVEMENT_COLORS.in }]}>+{formatQuantity(data.inbound.quantity)}</Text>
        <Text style={[styles.totalValue, styles.colNum, { color: MOVEMENT_COLORS.sales }]}>{formatQuantity(data.sales.quantity)}</Text>
        {differ ? null : (
          <Text style={[styles.totalValue, styles.colStock, data.theoreticalStock < 0 ? styles.negative : null]}>
            {formatQuantity(data.theoreticalStock)}
          </Text>
        )}
      </View>
    </View>
  );
}

function LedgerRow({
  row,
  data,
  today,
  showStock,
  open,
  onToggle,
}: {
  row: WeeklyLedgerRow;
  data: SeasonalProductInsight;
  today: string | null;
  showStock: boolean;
  open: boolean;
  onToggle: () => void;
}) {
  const { t } = useAppTranslation("seasonalProductInsights");
  const dayCount = daysBetweenInclusive(row);
  const containsToday = today != null && row.endDate === today;
  const docs = row.documentNos;
  // 第二行说明：含今天（未结束）/ 不足一周 → 再接进货单号。
  const notes = [
    containsToday ? t("ledger.todayOpen", { count: dayCount }) : row.partial ? t("ledger.partial", { count: dayCount }) : null,
    docs.length === 1 ? t("ledger.oneDoc", { doc: docs[0] }) : docs.length > 1 ? t("ledger.docs", { count: docs.length }) : null,
  ].filter(Boolean);
  const entries = useMemo(() => (open ? buildWeekEntries(row, data.sales.daily, data.inbound.records) : []), [data.inbound.records, data.sales.daily, open, row]);
  const peak = Math.max(1, ...entries.filter((entry) => entry.kind === "sales").map((entry) => entry.quantity));
  const label = row.startDate === row.endDate ? shortDate(row.startDate) : `${shortDate(row.startDate)} – ${shortDate(row.endDate)}`;

  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ expanded: open }}
        accessibilityLabel={t("ledger.rowA11y", {
          week: label,
          inbound: formatQuantity(row.inboundQuantity),
          sales: formatQuantity(row.salesQuantity),
        })}
        onPress={onToggle}
        style={[styles.row, open ? styles.rowOpen : null]}
      >
        <View style={styles.colWeek}>
          <Text style={styles.weekText}>{label}</Text>
          {notes.length ? (
            <Text style={styles.weekNote} numberOfLines={1}>
              {notes.join(" · ")}
            </Text>
          ) : null}
        </View>
        <Text style={[styles.num, styles.colNum, row.inboundQuantity ? styles.inboundValue : styles.zero]}>
          {row.inboundQuantity ? `+${formatQuantity(row.inboundQuantity)}` : "—"}
        </Text>
        <Text style={[styles.num, styles.colNum, row.salesQuantity ? styles.salesValue : styles.zero]}>
          {row.salesQuantity ? formatQuantity(row.salesQuantity) : "—"}
        </Text>
        {showStock ? (
          <Text style={[styles.stock, styles.colStock, (row.endStock ?? 0) < 0 ? styles.negative : null]}>
            {row.endStock == null ? "—" : formatQuantity(row.endStock)}
          </Text>
        ) : null}
      </Pressable>
      {open
        ? entries.map((entry, index) => (
            <View key={`${entry.kind}:${entry.date}:${index}`} style={[styles.sub2, entry.kind === "inbound" ? styles.subInbound : null]}>
              <Text style={styles.subDate}>
                {shortDate(entry.date)}
                <Text style={styles.subWeekday}> {t(`weekdays.${weekdayIndex(entry.date)}`)}</Text>
              </Text>
              {entry.kind === "inbound" ? (
                <>
                  <View style={styles.pill}>
                    <Text style={styles.pillText}>{t("ledger.inboundTag")}</Text>
                  </View>
                  <Text style={styles.docNo} numberOfLines={1}>
                    {entry.documentNo}
                  </Text>
                  <Text style={[styles.subQty, styles.inboundValue]}>+{formatQuantity(entry.quantity)}</Text>
                </>
              ) : (
                <>
                  <View style={styles.track}>
                    <View style={[styles.fill, { width: `${Math.max(2, (entry.quantity / peak) * 100)}%` }]} />
                  </View>
                  <Text style={[styles.subQty, styles.salesValue]}>{formatQuantity(entry.quantity)}</Text>
                </>
              )}
            </View>
          ))
        : null}
    </>
  );
}

const styles = StyleSheet.create({
  card: { borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.white, overflow: "hidden" },
  head: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs, padding: HB_SPACING.sm, paddingBottom: HB_SPACING.xs },
  title: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  sub: { fontSize: 11, color: "#667085", flexShrink: 1 },
  tableHead: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    height: 30,
    paddingHorizontal: HB_SPACING.sm,
    backgroundColor: "#F9FAFB",
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#EEF2F7",
  },
  th: { fontSize: 11, color: "#667085" },
  colWeek: { flex: 1, minWidth: 0 },
  colNum: { width: 56, textAlign: "right" },
  colStock: { width: 72, textAlign: "right" },
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 48,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 8,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#EEF2F7",
  },
  rowOpen: { backgroundColor: "#F5F8FF" },
  weekText: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  weekNote: { fontSize: 11, color: "#98A2B3" },
  num: { fontSize: 13, fontWeight: "800", fontVariant: ["tabular-nums"] },
  stock: { fontSize: 13, fontWeight: "800", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  inboundValue: { color: MOVEMENT_COLORS.in },
  salesValue: { color: MOVEMENT_COLORS.sales },
  zero: { color: "#98A2B3", fontWeight: "400" },
  negative: { color: HB_COLORS.danger },
  sub2: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 36,
    paddingLeft: 26,
    paddingRight: HB_SPACING.sm,
    backgroundColor: "#FBFCFE",
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#F2F4F7",
  },
  subInbound: { backgroundColor: "#F3F7FF" },
  subDate: { width: 84, fontSize: 12, color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  subWeekday: { fontSize: 11, color: "#98A2B3" },
  subQty: { minWidth: 56, textAlign: "right", fontSize: 12.5, fontWeight: "700", fontVariant: ["tabular-nums"] },
  track: { flex: 1, height: 5, borderRadius: 3, backgroundColor: HB_COLORS.surfaceMuted, overflow: "hidden" },
  fill: { height: 5, borderRadius: 3, backgroundColor: MOVEMENT_COLORS.salesBar },
  pill: { borderRadius: 4, backgroundColor: MOVEMENT_COLORS.inSoft, paddingHorizontal: 5, paddingVertical: 1 },
  pillText: { fontSize: 10.5, fontWeight: "700", color: MOVEMENT_COLORS.in },
  docNo: { flex: 1, minWidth: 0, fontSize: 12, color: HB_COLORS.textSecondary },
  more: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#EEF2F7",
  },
  moreText: { fontSize: 13, fontWeight: "600", color: HB_COLORS.action },
  total: {
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
  totalValue: { fontSize: 13, fontWeight: "800", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  empty: { padding: HB_SPACING.md, textAlign: "center", fontSize: 12, color: HB_COLORS.textSecondary },
});
