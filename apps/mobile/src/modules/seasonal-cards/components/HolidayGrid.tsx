import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import type { SeasonalCardType } from "@/modules/seasonal-cards/types";
import { SEASONAL_CARD_COLORS } from "./palette";

export type HolidayFillStatus = "filled" | "pending" | "unknown";

export interface HolidayGridItem {
  cardType: SeasonalCardType;
  label: string;
  status: HolidayFillStatus;
}

const COLUMNS = 3;

/** 5 个节日按 3 列排；每格显示当前 年份 + 供应商 下「已填报 / 待填报」。 */
export function HolidayGrid({
  title,
  items,
  selected,
  statusLabels,
  disabled = false,
  onSelect,
}: {
  title: string;
  items: HolidayGridItem[];
  selected: SeasonalCardType;
  statusLabels: Record<HolidayFillStatus, string>;
  disabled?: boolean;
  onSelect: (cardType: SeasonalCardType) => void;
}) {
  const rows: (HolidayGridItem | null)[][] = [];
  for (let index = 0; index < items.length; index += COLUMNS) {
    const row: (HolidayGridItem | null)[] = items.slice(index, index + COLUMNS);
    // 最后一行补空位，保证每格宽度一致。
    while (row.length < COLUMNS) {
      row.push(null);
    }
    rows.push(row);
  }

  return (
    <View style={styles.block}>
      <Text style={styles.title}>{title}</Text>
      {rows.map((row, rowIndex) => (
        <View key={rowIndex} style={styles.row}>
          {row.map((item, columnIndex) => {
            if (!item) {
              return (
                <View key={`empty-${columnIndex}`} style={styles.placeholder} />
              );
            }
            const isSelected = item.cardType === selected;
            // 外层等宽格子不带边框和内边距，选中加粗边框也不会让各列宽度不一致。
            return (
              <View key={item.cardType} style={styles.slot}>
                <Pressable
                  accessibilityRole="radio"
                  accessibilityState={{ selected: isSelected, disabled }}
                  accessibilityLabel={`${item.label} ${statusLabels[item.status]}`}
                  disabled={disabled}
                  onPress={() => onSelect(item.cardType)}
                  style={({ pressed }) => [
                    styles.cell,
                    isSelected ? styles.cellSelected : null,
                    pressed ? styles.cellPressed : null,
                  ]}
                >
                  <Text
                    style={[
                      styles.label,
                      isSelected ? styles.labelSelected : null,
                    ]}
                  >
                    {item.label}
                  </Text>
                  <Text
                    style={[
                      styles.badge,
                      item.status === "filled"
                        ? styles.badgeFilled
                        : item.status === "pending"
                          ? styles.badgePending
                          : styles.badgeUnknown,
                    ]}
                  >
                    {statusLabels[item.status]}
                  </Text>
                </Pressable>
              </View>
            );
          })}
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  block: { gap: HB_SPACING.xs },
  title: {
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
  },
  row: { flexDirection: "row", gap: HB_SPACING.xs },
  placeholder: { flex: 1 },
  slot: { flex: 1 },
  cell: {
    flexGrow: 1,
    minHeight: 60,
    paddingHorizontal: 4,
    paddingVertical: 8,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    alignItems: "center",
    justifyContent: "center",
    gap: 2,
  },
  cellSelected: {
    borderWidth: 1.5,
    borderColor: SEASONAL_CARD_COLORS.selectedBorder,
    backgroundColor: SEASONAL_CARD_COLORS.selectedBackground,
  },
  cellPressed: { opacity: 0.8 },
  label: {
    fontSize: 15,
    lineHeight: 20,
    color: "#344054",
    textAlign: "center",
  },
  labelSelected: {
    color: SEASONAL_CARD_COLORS.selectedText,
    fontWeight: "600",
  },
  badge: { fontSize: 11, lineHeight: 15, textAlign: "center" },
  badgeFilled: { color: HB_COLORS.success },
  badgePending: { color: HB_COLORS.warning },
  badgeUnknown: { color: SEASONAL_CARD_COLORS.disabledText },
});
