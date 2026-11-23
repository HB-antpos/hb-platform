import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import Svg, { Line } from "react-native-svg";
import { shortDate } from "@/modules/seasonal-product-insights/logic";
import type { SeasonalRange } from "@/modules/seasonal-product-insights/types";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";

/** 季节页多处共用的小控件：分段切换、区间按钮、图例。 */
export function Segmented({
  label,
  items,
  value,
  onChange,
}: {
  label: string;
  items: { key: string; label: string }[];
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <View style={styles.segmented} accessibilityRole="tablist" accessibilityLabel={label}>
      {items.map((item) => {
        const selected = item.key === value;
        return (
          <Pressable
            key={item.key}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(item.key)}
            style={[styles.segment, selected ? styles.segmentSelected : null]}
          >
            <Text style={[styles.segmentText, selected ? styles.segmentTextSelected : null]}>{item.label}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

export function RangeButton({
  label,
  range,
  highlighted,
  onPress,
}: {
  label: string;
  range: SeasonalRange;
  highlighted: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${label} ${range.startDate} – ${range.endDate}`}
      onPress={onPress}
      style={[styles.rangeButton, highlighted ? styles.rangeButtonChanged : null]}
    >
      <Icon source="calendar-range" size={16} color={highlighted ? HB_COLORS.warning : HB_COLORS.action} />
      <View style={styles.rangeTextBox}>
        <Text style={[styles.rangeLabel, highlighted ? styles.rangeLabelChanged : null]}>{label}</Text>
        <Text style={styles.rangeValue} numberOfLines={1}>
          {shortDate(range.startDate)} – {shortDate(range.endDate)}
        </Text>
      </View>
      <Icon source="chevron-down" size={16} color="#667085" />
    </Pressable>
  );
}

export function Legend({ color, label, line }: { color: string; label: string; line?: "dashed" | "solid" }) {
  return (
    <View style={styles.legendItem}>
      {line ? (
        // 用 SVG 画图例线：iOS 上只有上边框的虚线 View 不会渲染。
        <Svg width={14} height={4}>
          <Line x1={0} x2={14} y1={2} y2={2} stroke={color} strokeWidth={1.8} strokeDasharray={line === "dashed" ? "3 3" : undefined} />
        </Svg>
      ) : (
        <View style={[styles.legendDot, { backgroundColor: color }]} />
      )}
      <Text style={styles.legendText}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  segmented: { flexDirection: "row", padding: 2, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  segment: { minHeight: 30, minWidth: 44, paddingHorizontal: 12, borderRadius: 6, alignItems: "center", justifyContent: "center" },
  segmentSelected: { backgroundColor: HB_COLORS.white, elevation: 1 },
  segmentText: { fontSize: 12, color: "#667085" },
  segmentTextSelected: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  rangeButton: {
    flex: 1,
    minWidth: 0,
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 10,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  rangeButtonChanged: { borderColor: "#F79009", backgroundColor: "#FFFAEB" },
  rangeTextBox: { flex: 1, minWidth: 0, gap: 1 },
  rangeLabel: { fontSize: 10.5, color: "#667085" },
  rangeLabelChanged: { color: HB_COLORS.warning },
  rangeValue: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 5 },
  legendDot: { width: 8, height: 8, borderRadius: 2 },
  legendText: { fontSize: 11, color: HB_COLORS.textSecondary },
});
