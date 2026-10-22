import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Icon, Switch, Text } from "react-native-paper";
import Svg, { Line } from "react-native-svg";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { sparkSegments, summarizePricing, type PricingSummary } from "./logic";
import { fmtRate } from "./pricing-curve";
import { PRICING_COLORS } from "./shared";
import type { PricingStrategyLevel, PricingStrategyListItem } from "./types";

const LEVEL_STYLE: Record<PricingStrategyLevel, { color: string; bg: string; border: string }> = {
  Supplier: { color: "#175CD3", bg: PRICING_COLORS.blueBg, border: PRICING_COLORS.blueBorder },
  Store: { color: HB_COLORS.success, bg: "#ECFDF3", border: PRICING_COLORS.okBorder },
  Global: { color: PRICING_COLORS.gold, bg: PRICING_COLORS.goldBg, border: PRICING_COLORS.goldBorder },
};
const MAX_CHIPS = 3;
const SPARK_W = 88;
const SPARK_H = 30;

/**
 * 策略卡片：名称 / 优先级 / 启停开关，范围标签（级别 + 分店 + 供应商，各最多 3 个，其余折叠成 +N），
 * 定价方式摘要（固定显示倍数，其余显示缩略图与成率区间）。点卡片进入编辑页；没有编辑权限时开关只读。
 */
export function StrategyCard({
  item,
  storeNames,
  supplierNames,
  canEdit,
  toggling,
  onPress,
  onToggle,
}: {
  item: PricingStrategyListItem;
  storeNames: Map<string, string>;
  supplierNames: Map<string, string>;
  canEdit: boolean;
  toggling: boolean;
  onPress: () => void;
  onToggle: (next: boolean) => void;
}) {
  const { t } = useAppTranslation("pricingStrategies");
  const level = LEVEL_STYLE[item.level];
  const allHint =
    !item.storeCodes.length && !item.supplierCodes.length
      ? t("scope.allBoth")
      : !item.storeCodes.length
        ? t("scope.allStores")
        : !item.supplierCodes.length
          ? t("scope.allSuppliers")
          : "";
  const summary = summarizePricing(item);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("list.openStrategy", { name: item.name })}
      onPress={onPress}
      style={({ pressed }) => [styles.card, pressed ? styles.pressed : null]}
    >
      <View style={styles.head}>
        <View style={styles.headText}>
          <Text style={styles.name} numberOfLines={2}>
            {item.name}
          </Text>
          <Text style={styles.priority}>{t("priority", { value: item.priority })}</Text>
        </View>
        {toggling ? (
          <ActivityIndicator size="small" color={HB_COLORS.brand} style={styles.toggleSpinner} />
        ) : (
          <Switch value={item.isEnabled} disabled={!canEdit} onValueChange={onToggle} accessibilityLabel={item.name} />
        )}
      </View>
      <View style={styles.chips}>
        <Chip label={t(`level.${item.level}`)} color={level.color} bg={level.bg} border={level.border} />
        <CodeChips codes={item.storeCodes} names={storeNames} color={HB_COLORS.textSecondary} bg={HB_COLORS.surfaceMuted} border={HB_COLORS.outlineMuted} />
        <CodeChips codes={item.supplierCodes} names={supplierNames} color="#6941C6" bg="#F4F3FF" border="#D9D6FE" />
        {allHint ? <Text style={styles.allHint}>{allHint}</Text> : null}
      </View>
      <View style={styles.pricing}>
        <PricingBlock summary={summary} item={item} />
        <Icon source="chevron-right" size={20} color={PRICING_COLORS.muted} />
      </View>
    </Pressable>
  );
}

function PricingBlock({ summary, item }: { summary: PricingSummary; item: PricingStrategyListItem }) {
  const { t } = useAppTranslation("pricingStrategies");
  if (summary.kind === "fixed") {
    return (
      <View style={styles.pricingBody}>
        <Text style={styles.bigRate}>{`${fmtRate(summary.rate)}×`}</Text>
        <View style={styles.pricingText}>
          <Text style={styles.pricingTitle}>{t("pricing.fixed")}</Text>
          <Text style={styles.pricingSub}>{t("pricing.fixedFormula", { rate: fmtRate(summary.rate) })}</Text>
        </View>
      </View>
    );
  }
  if (summary.kind === "empty") {
    return <Text style={styles.pricingSub}>{t("pricing.empty")}</Text>;
  }
  const title =
    summary.kind === "mixed"
      ? t("pricing.mixed", { fixed: summary.fixedCount, curve: summary.curveCount })
      : summary.kind === "curve"
        ? t("pricing.curve", { count: summary.count })
        : t("pricing.legacy", { count: summary.count });
  const rateLine =
    summary.startRate !== null && summary.endRate !== null ? t("pricing.rateRange", { from: fmtRate(summary.startRate), to: fmtRate(summary.endRate) }) : "";
  return (
    <View style={styles.pricingBody}>
      <Sparkline item={item} legacy={summary.kind === "legacy"} />
      <View style={styles.pricingText}>
        <Text style={styles.pricingTitle}>{title}</Text>
        {rateLine ? <Text style={styles.pricingSub}>{rateLine}</Text> : null}
      </View>
    </View>
  );
}

/** 缩略图：每段一条线，固定金色、曲线蓝色、旧版灰色虚线；只表达走势，不承担读数。 */
function Sparkline({ item, legacy }: { item: PricingStrategyListItem; legacy: boolean }) {
  const segments = sparkSegments(item.intervals, SPARK_W, SPARK_H);
  return (
    <Svg width={SPARK_W} height={SPARK_H}>
      {segments.map((segment, i) => (
        <Line
          key={i}
          x1={segment.points[0].x}
          y1={segment.points[0].y}
          x2={segment.points[1].x}
          y2={segment.points[1].y}
          stroke={legacy || segment.kind === "Legacy" ? PRICING_COLORS.muted : segment.kind === "Fixed" ? PRICING_COLORS.gold : PRICING_COLORS.blue}
          strokeWidth={2.5}
          strokeLinecap="round"
          strokeDasharray={legacy || segment.kind === "Legacy" ? "4 3" : undefined}
        />
      ))}
    </Svg>
  );
}

function CodeChips({ codes, names, color, bg, border }: { codes: string[]; names: Map<string, string>; color: string; bg: string; border: string }) {
  const { t } = useAppTranslation("pricingStrategies");
  if (!codes.length) return null;
  const shown = codes.slice(0, MAX_CHIPS);
  const rest = codes.length - shown.length;
  return (
    <>
      {shown.map((code) => (
        <Chip key={code} label={names.get(code) ?? code} color={color} bg={bg} border={border} />
      ))}
      {rest > 0 ? <Chip label={t("scope.more", { count: rest })} color={HB_COLORS.textSecondary} bg={HB_COLORS.surfaceMuted} border={HB_COLORS.outlineMuted} /> : null}
    </>
  );
}

function Chip({ label, color, bg, border }: { label: string; color: string; bg: string; border: string }) {
  return (
    <View style={[styles.chip, { backgroundColor: bg, borderColor: border }]}>
      <Text style={[styles.chipText, { color }]} numberOfLines={1}>
        {label}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.surface,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.sm,
    marginHorizontal: HB_SPACING.md,
    marginBottom: HB_SPACING.xs,
  },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  head: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  headText: { flex: 1, minWidth: 0 },
  name: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  priority: { fontSize: 12, color: PRICING_COLORS.muted, marginTop: 2 },
  toggleSpinner: { width: 52 },
  chips: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: 4, marginTop: 6 },
  chip: { height: 22, paddingHorizontal: 8, borderRadius: 6, borderWidth: 1, justifyContent: "center", maxWidth: 140 },
  chipText: { fontSize: 12 },
  allHint: { fontSize: 12, color: PRICING_COLORS.muted },
  pricing: {
    flexDirection: "row",
    alignItems: "center",
    marginTop: HB_SPACING.xs,
    paddingTop: HB_SPACING.xs,
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
    borderStyle: "dashed",
  },
  pricingBody: { flex: 1, flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  pricingText: { flex: 1, minWidth: 0 },
  bigRate: { fontSize: 20, fontWeight: "700", color: PRICING_COLORS.gold, fontVariant: ["tabular-nums"] },
  pricingTitle: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary },
  pricingSub: { fontSize: 12, color: HB_COLORS.textSecondary, marginTop: 1 },
});
