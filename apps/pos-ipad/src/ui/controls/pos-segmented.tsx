import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text, View, type StyleProp, type ViewStyle } from "react-native";

import type { PosIconName } from "./pos-button";
import { PosPressable } from "./pos-pressable";

import { createPosStyles, posColors, posRadius, posTouch } from "@/ui/theme";

export type PosSegmentedOption<T extends string> = Readonly<{
  value: T;
  label: string;
  icon?: PosIconName;
  /** 选项右侧的数量角标，0 或缺省时不显示。 */
  badge?: number;
  disabled?: boolean;
  testID?: string;
  accessibilityLabel?: string;
}>;

export type PosSegmentedProps<T extends string> = Readonly<{
  options: readonly PosSegmentedOption<T>[];
  value: T;
  onChange(value: T): void;
  /**
   * 再次点到已选中的那一段时触发。不传则重复点击无效果。
   * 用于「再点一次当前项 = 重置 / 重新聚焦」这类既有行为。
   */
  onReselect?: (value: T) => void;
  /** medium = 56（默认），small = 44（行内次要筛选）。 */
  size?: "medium" | "small";
  disabled?: boolean;
  /** 选项按内容宽度排，而不是平分。 */
  hugContent?: boolean;
  accessibilityLabel?: string;
  style?: StyleProp<ViewStyle>;
  testID?: string;
}>;

/** 多选一的分段按钮：当前选中段深色填充，整段都是点击区。 */
export function PosSegmented<T extends string>({
  accessibilityLabel,
  disabled = false,
  hugContent = false,
  onChange,
  onReselect,
  options,
  size = "medium",
  style,
  testID,
  value,
}: PosSegmentedProps<T>) {
  const small = size === "small";
  return (
    <View
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="radiogroup"
      style={[styles.group, small && styles.groupSmall, style]}
      testID={testID}
    >
      {options.map((option, index) => {
        const selected = option.value === value;
        const inactive = disabled || option.disabled === true;
        const foreground = selected
          ? posColors.onInverse
          : inactive
            ? posColors.disabledInk
            : posColors.mutedInk;
        return (
          <PosPressable
            accessibilityLabel={option.accessibilityLabel ?? option.label}
            accessibilityRole="radio"
            accessibilityState={{ checked: selected, disabled: inactive }}
            disabled={inactive}
            key={option.value}
            onPress={() => {
              if (selected) onReselect?.(option.value);
              else onChange(option.value);
            }}
            style={({ pressed }) => [
              styles.segment,
              !hugContent && styles.segmentEqual,
              small && styles.segmentSmall,
              index > 0 && styles.segmentDivider,
              selected && styles.segmentSelected,
              pressed && !inactive && !selected && styles.segmentPressed,
            ]}
            testID={option.testID}
          >
            {option.icon ? (
              <MaterialCommunityIcons
                accessible={false}
                color={foreground}
                name={option.icon}
                size={small ? 18 : 20}
              />
            ) : null}
            <Text
              adjustsFontSizeToFit
              minimumFontScale={0.8}
              // 有空格或连字符才允许折两行；单个长词折行只会被从中间劈开。
              numberOfLines={/[\s-]/.test(option.label.trim()) ? 2 : 1}
              style={[
                styles.label,
                small && styles.labelSmall,
                { color: foreground },
              ]}
            >
              {option.label}
            </Text>
            {option.badge ? (
              <View style={styles.badge}>
                <Text style={styles.badgeText}>{option.badge}</Text>
              </View>
            ) : null}
          </PosPressable>
        );
      })}
    </View>
  );
}

const styles = createPosStyles((colors) => ({
  badge: {
    alignItems: "center",
    backgroundColor: colors.accent,
    borderRadius: 11,
    height: 22,
    justifyContent: "center",
    minWidth: 22,
    paddingHorizontal: 6,
  },
  badgeText: {
    color: colors.onAccent,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  group: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    minHeight: posTouch.medium,
    overflow: "hidden",
  },
  groupSmall: {
    minHeight: posTouch.min,
  },
  label: {
    flexShrink: 1,
    fontSize: 15.5,
    fontWeight: "800",
    // 折行时居中，两行标签不会一行靠左一行靠右。
    textAlign: "center",
  },
  labelSmall: {
    fontSize: 14,
  },
  segment: {
    alignItems: "center",
    flexDirection: "row",
    gap: 6,
    justifyContent: "center",
    minHeight: posTouch.medium,
    paddingHorizontal: 16,
  },
  segmentDivider: {
    borderLeftColor: colors.border,
    borderLeftWidth: 1,
  },
  segmentEqual: {
    flex: 1,
  },
  segmentPressed: {
    backgroundColor: colors.surfaceAlt,
  },
  segmentSelected: {
    backgroundColor: colors.inverseSurface,
  },
  segmentSmall: {
    minHeight: posTouch.min,
    paddingHorizontal: 12,
  },
}));
