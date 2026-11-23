import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { MOVEMENT_COLORS } from "@/components/product-insights/MovementTrendChart";
import {
  candidateFlag,
  formatPercent,
  formatQuantity,
  HOT_SELL_THROUGH,
  sellThroughRate,
  sortCandidates,
} from "@/modules/seasonal-product-insights/logic";
import type {
  SeasonalCandidate,
  SeasonalCandidateFlag,
  SeasonalCandidateSort,
  SeasonalLookupResult,
} from "@/modules/seasonal-product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ProductImageBox } from "./ProductImageBox";
import { Segmented } from "./SeasonalInsightParts";

export interface SeasonalCandidateListProps {
  keyword: string;
  result: SeasonalLookupResult;
  /** 门店当地今天，用来判断「到货满 28 天仍没卖动」；缺失时用销售区间终点代替。 */
  today: string | null;
  onSelect: (productCode: string) => void;
}

/** 把货号里与关键词相同的片段拆出来高亮（不区分大小写）。 */
function splitMatch(value: string, keyword: string) {
  const index = value.toUpperCase().indexOf(keyword.trim().toUpperCase());
  if (!keyword.trim() || index < 0) return [value, "", ""] as const;
  const end = index + keyword.trim().length;
  return [value.slice(0, index), value.slice(index, end), value.slice(end)] as const;
}

const FLAG_STYLE: Record<SeasonalCandidateFlag, { bg: string; color: string }> = {
  hot: { bg: "#FFFAEB", color: HB_COLORS.warning },
  slow: { bg: HB_COLORS.surfaceMuted, color: HB_COLORS.textSecondary },
  negative: { bg: "#FEF3F2", color: HB_COLORS.danger },
};

/** 搜到多个商品时在页面里直接列出进销卡片，替代原来的底部候选弹层。 */
export function SeasonalCandidateList({ keyword, result, today, onSelect }: SeasonalCandidateListProps) {
  const { t } = useAppTranslation("seasonalProductInsights");
  const [sort, setSort] = useState<SeasonalCandidateSort>("sellThrough");
  const byBarcode = result.matchMode === "barcode";
  const reference = today ?? result.ranges.sales.endDate;
  const items = useMemo(() => sortCandidates(result.items, sort), [result.items, sort]);

  return (
    <View style={styles.wrap}>
      <View style={styles.head}>
        <View style={styles.headText}>
          <Text style={styles.title}>{t("candidates.title", { count: result.items.length })}</Text>
          <Text style={styles.sub} numberOfLines={2}>
            {result.truncated
              ? t("candidates.truncated", { count: result.items.length })
              : t(byBarcode ? "candidates.byBarcode" : "candidates.byItemNumber", { keyword })}
          </Text>
        </View>
        <Segmented
          label={t("candidates.sortLabel")}
          items={[
            { key: "sellThrough", label: t("candidates.sort.sellThrough") },
            { key: "stock", label: t("candidates.sort.stock") },
            { key: "itemNumber", label: t("candidates.sort.itemNumber") },
          ]}
          value={sort}
          onChange={(value) => setSort(value as SeasonalCandidateSort)}
        />
      </View>
      {items.map((item) => (
        <CandidateCard key={item.productCode} item={item} keyword={keyword} byBarcode={byBarcode} today={reference} onSelect={onSelect} />
      ))}
    </View>
  );
}

function CandidateCard({
  item,
  keyword,
  byBarcode,
  today,
  onSelect,
}: {
  item: SeasonalCandidate;
  keyword: string;
  byBarcode: boolean;
  today: string;
  onSelect: (productCode: string) => void;
}) {
  const { t } = useAppTranslation("seasonalProductInsights");
  const [before, match, after] = byBarcode
    ? ([item.itemNumber ?? "—", "", ""] as const)
    : splitMatch(item.itemNumber ?? "—", keyword);
  const hasFlow = item.inboundQuantity != null && item.salesQuantity != null;
  const rate = sellThroughRate(item.inboundQuantity, item.salesQuantity);
  const flag = candidateFlag(item, today);
  const stockNegative = item.theoreticalStock < 0;
  const caption = [`${t("labels.itemNumber")} ${item.itemNumber ?? "—"}`, item.barcode].filter(Boolean).join(" · ");

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${item.productName} ${item.itemNumber ?? ""}`}
      onPress={() => onSelect(item.productCode)}
      style={({ pressed }) => [styles.card, pressed ? styles.cardPressed : null]}
    >
      <View style={styles.top}>
        {/* 图片自己可点（放大预览），不会触发整张卡片的进入详情。 */}
        <ProductImageBox uri={item.productImage} size={56} label={item.productName} caption={caption} />
        <View style={styles.main}>
          <Text numberOfLines={2} style={styles.name}>
            {item.productName}
          </Text>
          <Text numberOfLines={1} style={styles.meta}>
            {t("labels.itemNumber")} {before}
            {match ? <Text style={styles.match}>{match}</Text> : null}
            {after}
            {item.barcode ? ` · ${item.barcode}` : ""}
          </Text>
          {flag ? (
            <View style={[styles.flag, { backgroundColor: FLAG_STYLE[flag].bg }]}>
              <Text style={[styles.flagText, { color: FLAG_STYLE[flag].color }]}>{t(`candidates.flags.${flag}`)}</Text>
            </View>
          ) : null}
        </View>
        <Icon source="chevron-right" size={18} color="#98A2B3" />
      </View>

      <View style={styles.metrics}>
        {hasFlow ? (
          <>
            <Metric first dot={MOVEMENT_COLORS.inBar} label={t("candidates.inbound")} value={formatQuantity(item.inboundQuantity ?? 0)} color={MOVEMENT_COLORS.in} />
            <Metric dot={MOVEMENT_COLORS.salesBar} label={t("candidates.sales")} value={formatQuantity(item.salesQuantity ?? 0)} color={MOVEMENT_COLORS.sales} />
            <Metric
              label={t("candidates.sellThrough")}
              value={rate == null ? "—" : formatPercent(rate)}
              color={rate != null && rate >= HOT_SELL_THROUGH ? HB_COLORS.warning : HB_COLORS.textPrimary}
            />
          </>
        ) : null}
        <Metric first={!hasFlow} label={t("kpi.stock")} value={formatQuantity(item.theoreticalStock)} color={stockNegative ? HB_COLORS.danger : HB_COLORS.textPrimary} />
      </View>

      {hasFlow ? (
        (item.inboundQuantity ?? 0) > 0 ? (
          <View style={styles.progressRow}>
            <View style={styles.track}>
              <View style={[styles.fill, { width: `${Math.min(100, ((item.salesQuantity ?? 0) / (item.inboundQuantity ?? 1)) * 100)}%` }]} />
            </View>
            <Text style={styles.progressText}>
              {t("candidates.progress", {
                sales: formatQuantity(item.salesQuantity ?? 0),
                inbound: formatQuantity(item.inboundQuantity ?? 0),
              })}
            </Text>
          </View>
        ) : (
          <Text style={styles.noInbound}>{t("candidates.noInbound")}</Text>
        )
      ) : null}
    </Pressable>
  );
}

function Metric({ label, value, color, dot, first }: { label: string; value: string; color: string; dot?: string; first?: boolean }) {
  return (
    <View style={[styles.metric, first ? styles.metricFirst : null]}>
      <View style={styles.metricLabelRow}>
        {dot ? <View style={[styles.dot, { backgroundColor: dot }]} /> : null}
        <Text style={styles.metricLabel} numberOfLines={1}>
          {label}
        </Text>
      </View>
      <Text style={[styles.metricValue, { color }]} numberOfLines={1} adjustsFontSizeToFit>
        {value}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { gap: 10 },
  head: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs, paddingHorizontal: 2 },
  headText: { flex: 1, minWidth: 0, gap: 2 },
  title: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  sub: { fontSize: 11.5, color: "#667085" },
  card: { padding: HB_SPACING.sm, borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.white, gap: 10 },
  cardPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  top: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.sm },
  main: { flex: 1, minWidth: 0, gap: 3 },
  name: { fontSize: 14, fontWeight: "700", lineHeight: 19, color: HB_COLORS.textPrimary },
  meta: { fontSize: 11.5, color: "#667085", fontVariant: ["tabular-nums"] },
  match: { color: HB_COLORS.action, fontWeight: "700" },
  flag: { alignSelf: "flex-start", marginTop: 2, borderRadius: 4, paddingHorizontal: 6, paddingVertical: 2 },
  flagText: { fontSize: 10.5, fontWeight: "700" },
  metrics: { flexDirection: "row", borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: "#EEF2F7", paddingTop: 10 },
  metric: { flex: 1, minWidth: 0, gap: 1, paddingLeft: 10, borderLeftWidth: StyleSheet.hairlineWidth, borderLeftColor: "#EEF2F7" },
  metricFirst: { paddingLeft: 0, borderLeftWidth: 0 },
  metricLabelRow: { flexDirection: "row", alignItems: "center", gap: 4 },
  dot: { width: 7, height: 7, borderRadius: 2 },
  metricLabel: { fontSize: 10.5, color: "#667085" },
  metricValue: { fontSize: 17, fontWeight: "800", fontVariant: ["tabular-nums"] },
  progressRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  track: { flex: 1, height: 6, borderRadius: 3, backgroundColor: MOVEMENT_COLORS.inSoft, overflow: "hidden" },
  fill: { height: 6, borderRadius: 3, backgroundColor: MOVEMENT_COLORS.salesBar },
  // 固定文字宽度，各卡片的进度条长度才一致。
  progressText: { minWidth: 104, textAlign: "right", fontSize: 10.5, color: "#667085", fontVariant: ["tabular-nums"] },
  noInbound: { fontSize: 11, color: HB_COLORS.danger },
});
