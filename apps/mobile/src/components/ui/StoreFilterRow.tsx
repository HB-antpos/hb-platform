import { Pressable, StyleSheet } from "react-native";
import { Icon, Text } from "react-native-paper";
import { HB_COLORS } from "@/shared/theme/tokens";

/**
 * 筛选弹层里的「分店」一行选择器：分店有几十家，不再平铺成 chip 墙，而是折成一行，点开进「选择分店」二级页。
 * 有选择时整行变蓝，左侧 ✕ 一键清回「全部分店」（不传 onClear 则不显示 ✕）。
 */
export function StoreFilterRow({
  label,
  caption,
  active,
  accessibilityLabel,
  clearLabel,
  onPress,
  onClear,
}: {
  /** 主文案：全部分店 / 分店名 / 已选 N 家。 */
  label: string;
  /** 右侧淡色补充：分店编号，或「共 N 家」。 */
  caption?: string;
  active: boolean;
  accessibilityLabel: string;
  clearLabel?: string;
  onPress: () => void;
  onClear?: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      onPress={onPress}
      style={[styles.row, active && styles.rowActive]}
    >
      {active && onClear ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={clearLabel}
          onPress={onClear}
          hitSlop={8}
          style={styles.clear}
        >
          <Icon source="close" size={14} color={HB_COLORS.white} />
        </Pressable>
      ) : null}
      <Text style={[styles.label, active && styles.labelActive]} numberOfLines={1}>{label}</Text>
      {caption ? <Text style={styles.caption} numberOfLines={1}>{caption}</Text> : null}
      <Icon source="chevron-right" size={20} color={HB_COLORS.textSecondary} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  row: { minHeight: 50, flexDirection: "row", alignItems: "center", gap: 8, paddingHorizontal: 14, borderRadius: 12, borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white },
  rowActive: { borderColor: HB_COLORS.brand, backgroundColor: "#EFF6FF" },
  clear: { width: 22, height: 22, borderRadius: 11, alignItems: "center", justifyContent: "center", backgroundColor: "#98B4E8" },
  label: { flex: 1, color: HB_COLORS.textPrimary, fontSize: 15 },
  labelActive: { color: HB_COLORS.action, fontWeight: "700" },
  caption: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
});
