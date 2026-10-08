import { Pressable, StyleSheet } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface OptionChipProps {
  label: string;
  selected: boolean;
  onPress: () => void;
  /** 筛选面板里的多选项用 checkbox 语义，标签条用 button 语义 */
  role?: "button" | "checkbox";
  testID?: string;
}

/**
 * 货柜明细的轻量选项标签（标签条与筛选面板共用）。
 * 视觉高度 36，上下各扩 4px 触控区，整体触控高度 44。
 */
export function OptionChip({ label, selected, onPress, role = "button", testID }: OptionChipProps) {
  return (
    <Pressable
      testID={testID}
      accessibilityRole={role}
      accessibilityState={role === "checkbox" ? { checked: selected } : { selected }}
      hitSlop={{ top: 4, bottom: 4 }}
      onPress={onPress}
      style={[styles.chip, selected && styles.chipSelected]}
    >
      <Text style={[styles.text, selected && styles.textSelected]} numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  chip: {
    minHeight: 36,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    alignItems: "center",
    justifyContent: "center",
  },
  chipSelected: { backgroundColor: HB_COLORS.action, borderColor: HB_COLORS.action },
  text: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "600", fontVariant: ["tabular-nums"] },
  textSelected: { color: HB_COLORS.white },
});
