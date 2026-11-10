import { Text, View } from "react-native";

import { PosPressable } from "./pos-pressable";

import {
  HISTORY_QUICK_DATE_RANGES,
  type HistoryQuickDateRange,
} from "@hb/pos-domain/features/remote-history/quick-date-range";
import { createPosStyles, posRadius, posTouch, usePosThemeVersion } from "@/ui/theme";

/**
 * 交易历史“今天 / 昨天 / 近 7 天”快捷日期范围（对齐 WPF #608）：
 * 点了由调用方填入起止日期并立即查询。
 *
 * 外观是一组连在一起的分段按钮（高 56）。这里不直接用 PosSegmented：
 * 分段控件点已选中的段不会再触发回调，而快捷范围需要“再点一次重新查询”。
 */
export function HistoryQuickRangeRow({
  disabled = false,
  labels,
  onSelect,
  selected = null,
  testIDPrefix,
}: Readonly<{
  disabled?: boolean;
  labels: Readonly<Record<HistoryQuickDateRange, string>>;
  onSelect(kind: HistoryQuickDateRange): void;
  /** 当前起止日期正好等于哪个快捷范围（只用于高亮，可不传）。 */
  selected?: HistoryQuickDateRange | null;
  testIDPrefix: string;
}>) {
  usePosThemeVersion();
  return (
    <View
      style={[styles.row, disabled && styles.rowDisabled]}
      testID={`${testIDPrefix}-quick-ranges`}
    >
      {HISTORY_QUICK_DATE_RANGES.map((kind, index) => {
        const active = selected === kind;
        return (
          <PosPressable
            accessibilityRole="button"
            accessibilityState={{ disabled, selected: active }}
            disabled={disabled}
            key={kind}
            onPress={() => onSelect(kind)}
            sound="tap"
            style={({ pressed }) => [
              styles.chip,
              index > 0 && styles.chipDivider,
              active && !disabled && styles.chipSelected,
              pressed && !disabled && !active && styles.chipPressed,
            ]}
            testID={`${testIDPrefix}-quick-${kind}`}
          >
            <Text
              numberOfLines={1}
              style={[
                styles.chipText,
                active && !disabled && styles.chipTextSelected,
                disabled && styles.chipTextDisabled,
              ]}
            >
              {labels[kind]}
            </Text>
          </PosPressable>
        );
      })}
    </View>
  );
}

const styles = createPosStyles((colors) => ({
  row: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    flexShrink: 0,
    minHeight: posTouch.medium,
    overflow: "hidden",
  },
  rowDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  chip: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: posTouch.medium,
    minWidth: posTouch.medium,
    paddingHorizontal: 16,
  },
  chipDivider: {
    borderLeftColor: colors.border,
    borderLeftWidth: 1,
  },
  chipPressed: {
    backgroundColor: colors.surfaceAlt,
  },
  chipSelected: {
    backgroundColor: colors.inverseSurface,
  },
  chipText: {
    color: colors.mutedInk,
    fontSize: 15.5,
    fontWeight: "800",
  },
  chipTextDisabled: {
    color: colors.disabledInk,
  },
  chipTextSelected: {
    color: colors.onInverse,
  },
}));
