import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { SEASONAL_CARD_COLORS } from "./palette";

/** 去年 / 今年 / 明年三个年份，今年带标签。 */
export function YearChips({
  years,
  currentYear,
  selectedYear,
  disabled = false,
  title,
  hint,
  thisYearLabel,
  onSelect,
}: {
  years: number[];
  currentYear: number;
  selectedYear: number;
  disabled?: boolean;
  title: string;
  hint: string;
  thisYearLabel: string;
  onSelect: (year: number) => void;
}) {
  return (
    <View style={styles.block}>
      <View style={styles.headerRow}>
        <Text style={styles.title}>{title}</Text>
        <Text style={styles.hint}>{hint}</Text>
      </View>
      <View style={styles.row}>
        {years.map((year) => {
          const selected = year === selectedYear;
          return (
            <View key={year} style={styles.slot}>
              <Pressable
                accessibilityRole="radio"
                accessibilityState={{ selected, disabled }}
                disabled={disabled}
                onPress={() => onSelect(year)}
                style={({ pressed }) => [
                  styles.chip,
                  selected ? styles.chipSelected : null,
                  pressed ? styles.chipPressed : null,
                ]}
              >
                <Text
                  style={[
                    styles.chipText,
                    selected ? styles.chipTextSelected : null,
                  ]}
                >
                  {String(year)}
                </Text>
                {year === currentYear ? (
                  <View style={styles.tag}>
                    <Text style={styles.tagText}>{thisYearLabel}</Text>
                  </View>
                ) : null}
              </Pressable>
            </View>
          );
        })}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  block: { gap: HB_SPACING.xs },
  headerRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
  },
  title: {
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
  },
  hint: { fontSize: 12, lineHeight: 16, color: SEASONAL_CARD_COLORS.mutedText },
  row: { flexDirection: "row", gap: HB_SPACING.xs },
  slot: { flex: 1 },
  chip: {
    flexGrow: 1,
    minHeight: 44,
    paddingHorizontal: 4,
    paddingVertical: 6,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    flexDirection: "row",
    flexWrap: "wrap",
    // 英文「This year」标签会换到第二行；多行内容整体垂直居中，与相邻年份对齐。
    alignContent: "center",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
  },
  chipSelected: {
    borderWidth: 1.5,
    borderColor: SEASONAL_CARD_COLORS.selectedBorder,
    backgroundColor: SEASONAL_CARD_COLORS.selectedBackground,
  },
  chipPressed: { opacity: 0.8 },
  chipText: {
    fontSize: 15,
    lineHeight: 20,
    color: "#344054",
    fontVariant: ["tabular-nums"],
  },
  chipTextSelected: {
    color: SEASONAL_CARD_COLORS.selectedText,
    fontWeight: "600",
  },
  tag: {
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 8,
    backgroundColor: SEASONAL_CARD_COLORS.tagBackground,
  },
  tagText: {
    fontSize: 11,
    lineHeight: 15,
    fontWeight: "500",
    color: SEASONAL_CARD_COLORS.selectedText,
  },
});
