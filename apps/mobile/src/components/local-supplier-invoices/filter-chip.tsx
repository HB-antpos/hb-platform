import { Pressable, StyleSheet } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS } from "@/shared/theme/tokens";

/** 详情页的筛选胶囊：选中态浅蓝底；warn 用于「主档不存在 / 条码异常」这类需要处理的计数。 */
export function FilterChip({
  label,
  count,
  selected,
  warn,
  onPress,
}: {
  label: string;
  count?: number;
  selected: boolean;
  warn?: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.chip, selected ? styles.selected : null]}
    >
      <Text style={[styles.text, selected ? styles.textSelected : null]}>
        {label}
        {count != null ? " " : ""}
        {count != null ? (
          <Text style={[styles.count, warn && count > 0 && !selected ? styles.warnCount : null]}>{count}</Text>
        ) : null}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  chip: {
    minHeight: 32,
    justifyContent: "center",
    paddingHorizontal: 10,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  selected: { backgroundColor: "#EAF2FF", borderColor: HB_COLORS.brand },
  text: { fontSize: 12, color: HB_COLORS.textSecondary },
  textSelected: { color: "#073B83", fontWeight: "700" },
  count: { fontWeight: "700" },
  warnCount: { color: HB_COLORS.danger },
});
