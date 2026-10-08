import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import type { SeasonalCardType } from "@/modules/seasonal-cards/types";
import { SEASONAL_CARD_COLORS } from "./palette";

export type HolidayFillStatus = "filled" | "pending" | "unknown";

export interface HolidayGridItem {
  cardType: SeasonalCardType;
  label: string;
  /** 开放中：已填报 / 待填报（未选供应商时为 unknown）；未开放不显示填报状态。 */
  isOpen: boolean;
  status: HolidayFillStatus;
  /** 开放中为「截止 M/D」，未开放为「M/D 开放」；日期未知时为空。 */
  detail: string;
}

const COLUMNS = 3;

/**
 * 5 个节日按 3 列排。开放中的节日可选，显示填报状态和截止日；
 * 未开放的节日灰显、不可点，显示开放日期。
 */
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
  selected: SeasonalCardType | null;
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
            const isSelected = item.isOpen && item.cardType === selected;
            const itemDisabled = disabled || !item.isOpen;
            const statusText = item.isOpen ? statusLabels[item.status] : "";
            // 外层等宽格子不带边框和内边距，选中加粗边框也不会让各列宽度不一致。
            return (
              <View key={item.cardType} style={styles.slot}>
                <Pressable
                  accessibilityRole="radio"
                  accessibilityState={{ selected: isSelected, disabled: itemDisabled }}
                  accessibilityLabel={[item.label, statusText, item.detail]
                    .filter(Boolean)
                    .join(" ")}
                  disabled={itemDisabled}
                  onPress={() => onSelect(item.cardType)}
                  style={({ pressed }) => [
                    styles.cell,
                    item.isOpen ? null : styles.cellClosed,
                    isSelected ? styles.cellSelected : null,
                    pressed ? styles.cellPressed : null,
                  ]}
                >
                  <Text
                    style={[
                      styles.label,
                      item.isOpen ? null : styles.labelClosed,
                      isSelected ? styles.labelSelected : null,
                    ]}
                  >
                    {item.label}
                  </Text>
                  {statusText ? (
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
                      {statusText}
                    </Text>
                  ) : null}
                  {item.detail ? (
                    <Text style={[styles.detail, item.isOpen ? null : styles.detailClosed]}>
                      {item.detail}
                    </Text>
                  ) : null}
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
  cellClosed: {
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: SEASONAL_CARD_COLORS.disabledBackground,
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
  labelClosed: { color: SEASONAL_CARD_COLORS.disabledText },
  labelSelected: {
    color: SEASONAL_CARD_COLORS.selectedText,
    fontWeight: "600",
  },
  badge: { fontSize: 11, lineHeight: 15, textAlign: "center" },
  badgeFilled: { color: HB_COLORS.success },
  badgePending: { color: HB_COLORS.warning },
  badgeUnknown: { color: SEASONAL_CARD_COLORS.disabledText },
  detail: {
    fontSize: 11,
    lineHeight: 15,
    textAlign: "center",
    color: SEASONAL_CARD_COLORS.mutedText,
  },
  detailClosed: { color: SEASONAL_CARD_COLORS.mutedText },
});
