import { StyleSheet, Text, View } from "react-native";

import { PosPressable } from "./pos-pressable";

import {
  HISTORY_QUICK_DATE_RANGES,
  type HistoryQuickDateRange,
} from "@hb/pos-domain/features/remote-history/quick-date-range";
import { posColors } from "@/ui/theme";

const MIN_TOUCH_TARGET = 44;

/**
 * 交易历史“今天 / 昨天 / 近 7 天”快捷日期范围（对齐 WPF #608）：
 * 点了由调用方填入起止日期并立即查询。
 */
export function HistoryQuickRangeRow({
  disabled = false,
  labels,
  onSelect,
  testIDPrefix,
}: Readonly<{
  disabled?: boolean;
  labels: Readonly<Record<HistoryQuickDateRange, string>>;
  onSelect(kind: HistoryQuickDateRange): void;
  testIDPrefix: string;
}>) {
  return (
    <View style={styles.row} testID={`${testIDPrefix}-quick-ranges`}>
      {HISTORY_QUICK_DATE_RANGES.map((kind) => (
        <PosPressable
          accessibilityRole="button"
          accessibilityState={{ disabled }}
          disabled={disabled}
          key={kind}
          onPress={() => onSelect(kind)}
          sound="tap"
          style={[styles.chip, disabled && styles.disabled]}
          testID={`${testIDPrefix}-quick-${kind}`}
        >
          <Text style={styles.chipText}>{labels[kind]}</Text>
        </PosPressable>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  row: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    alignSelf: "stretch",
    width: "100%",
  },
  chip: {
    minHeight: MIN_TOUCH_TARGET,
    paddingHorizontal: 16,
    borderRadius: 6,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: posColors.surface,
    alignItems: "center",
    justifyContent: "center",
  },
  chipText: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  disabled: {
    opacity: 0.45,
  },
});
