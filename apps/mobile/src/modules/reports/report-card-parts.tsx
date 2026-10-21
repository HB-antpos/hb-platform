import { StyleSheet, View, type StyleProp, type ViewStyle } from "react-native";
import { Text } from "react-native-paper";
import { formatGrowthRate, getGrowthTone, type GrowthTone } from "@/modules/reports/growth-rate";

/**
 * 报表中心（营业额 / 商品两个页签）卡片化版式的共用部件。
 * 颜色沿用累计营业额卡里已有的胶囊配色，保证两个页签的涨跌表现一致。
 */
export const REPORT_UI = {
  background: "#F7F8FA",
  card: "#FFFFFF",
  border: "#E5E7EB",
  divider: "#EEF0F3",
  tile: "#F9FAFB",
  text: "#111827",
  subText: "#374151",
  muted: "#6B7280",
  accent: "#073B83",
  accentSoft: "#EAF2FF",
  accentBar: "#84ADFF",
} as const;

type PillTone = GrowthTone | "new";

const PILL_COLORS: Record<PillTone, { background: string; text: string }> = {
  up: { background: "#DCFCE7", text: "#166534" },
  down: { background: "#FEE4E2", text: "#B42318" },
  flat: { background: "#F2F4F7", text: "#344054" },
  new: { background: "#EAF2FF", text: "#073B83" },
};

/** 把「本期 vs 同期」折成胶囊的文字与色调；同期缺失（补算中）统一显示「—」。 */
export function getGrowthPillModel(
  current: number | null | undefined,
  compare: number | null | undefined,
  newLabel: string,
  unavailable = false,
): { label: string; tone: PillTone } {
  if (unavailable || current == null || compare == null) {
    return { label: "—", tone: "flat" };
  }
  // 同期为 0 且本期有销售：沿用「新增」口径，不显示无穷百分比。
  if (compare === 0 && current > 0) {
    return { label: newLabel, tone: "new" };
  }
  const label = formatGrowthRate(current, compare, newLabel);
  return { label, tone: label === "--" ? "flat" : getGrowthTone(current, compare) };
}

export function GrowthPill({
  current,
  compare,
  newLabel,
  unavailable = false,
  size = "md",
  fitText = false,
  style,
}: {
  current: number | null | undefined;
  compare: number | null | undefined;
  newLabel: string;
  unavailable?: boolean;
  size?: "sm" | "md" | "lg";
  /** 放在定宽窄列里（如供应商表）时，百分比太长就按比例缩小，而不是被省略号截断。 */
  fitText?: boolean;
  style?: StyleProp<ViewStyle>;
}) {
  const { label, tone } = getGrowthPillModel(current, compare, newLabel, unavailable);
  const colors = PILL_COLORS[tone];
  return (
    <View style={[styles.pill, styles[`pill_${size}`], fitText ? styles.pillFit : null, { backgroundColor: colors.background }, style]}>
      <Text
        style={[styles.pillText, styles[`pillText_${size}`], { color: colors.text }]}
        numberOfLines={1}
        adjustsFontSizeToFit={fitText}
        minimumFontScale={fitText ? 0.65 : undefined}
      >
        {label}
      </Text>
    </View>
  );
}

/** 名次徽标：前三名实心深蓝，其余浅灰；编号文字由调用方给（已补零）。 */
export function RankBadge({ value, highlight = false }: { value: string; highlight?: boolean }) {
  return (
    <View style={[styles.rank, highlight ? styles.rankHighlight : null]}>
      <Text style={[styles.rankText, highlight ? styles.rankTextHighlight : null]}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  pill: {
    alignSelf: "flex-start",
    borderRadius: 10,
  },
  pillFit: { maxWidth: "100%" },
  pill_sm: { paddingHorizontal: 6, paddingVertical: 1 },
  pill_md: { paddingHorizontal: 8, paddingVertical: 2 },
  pill_lg: { paddingHorizontal: 10, paddingVertical: 4 },
  pillText: {
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  pillText_sm: { fontSize: 11, lineHeight: 15 },
  pillText_md: { fontSize: 12, lineHeight: 16 },
  pillText_lg: { fontSize: 14, lineHeight: 18 },
  rank: {
    alignItems: "center",
    backgroundColor: "#F2F4F7",
    borderRadius: 7,
    height: 24,
    justifyContent: "center",
    width: 24,
  },
  rankHighlight: {
    backgroundColor: REPORT_UI.accent,
  },
  rankText: {
    color: "#475467",
    fontSize: 11.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  rankTextHighlight: {
    color: "#FFFFFF",
  },
});
