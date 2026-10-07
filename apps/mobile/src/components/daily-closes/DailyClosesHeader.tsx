import { Pressable, StyleSheet, View } from "react-native";
import { Icon, IconButton, Text } from "react-native-paper";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 列表页头：返回、标题、筛选图标（带已生效筛选数徽标），下方两个筛选 chips（分店 / 营业日）。
 * 两个 chips 都打开筛选面板，一眼看到「当前看的是哪些分店、哪几天」。
 */
export function DailyClosesHeader({
  title,
  backLabel,
  filterLabel,
  filterCount,
  storeLabel,
  rangeLabel,
  onBack,
  onOpenFilters,
}: {
  title: string;
  backLabel: string;
  filterLabel: string;
  filterCount: number;
  storeLabel: string;
  rangeLabel: string;
  onBack: () => void;
  onOpenFilters: () => void;
}) {
  return (
    <View>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={backLabel} onPress={onBack} />
        <Text variant="titleMedium" style={styles.title} numberOfLines={1}>
          {title}
        </Text>
        <View>
          <IconButton icon="tune-variant" accessibilityLabel={filterLabel} onPress={onOpenFilters} />
          {filterCount > 0 ? (
            <View style={styles.badge} pointerEvents="none">
              <Text style={styles.badgeText}>{filterCount}</Text>
            </View>
          ) : null}
        </View>
      </View>
      <View style={styles.chips}>
        <Chip icon="storefront-outline" label={storeLabel} accessibilityLabel={`${filterLabel}: ${storeLabel}`} onPress={onOpenFilters} />
        <Chip icon="calendar" label={rangeLabel} accessibilityLabel={`${filterLabel}: ${rangeLabel}`} onPress={onOpenFilters} />
      </View>
    </View>
  );
}

function Chip({ icon, label, accessibilityLabel, onPress }: { icon: string; label: string; accessibilityLabel: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel} onPress={onPress} style={styles.chip}>
      <Icon source={icon} size={15} color={HB_COLORS.textSecondary} />
      <Text numberOfLines={1} style={styles.chipText}>
        {label}
      </Text>
      <Icon source="chevron-down" size={15} color={HB_COLORS.textSecondary} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  header: { flexDirection: "row", alignItems: "center", paddingRight: HB_SPACING.xxs },
  title: { flex: 1, fontWeight: "700", color: HB_COLORS.textPrimary },
  badge: {
    position: "absolute",
    top: 6,
    right: 6,
    minWidth: 16,
    height: 16,
    paddingHorizontal: 4,
    borderRadius: 8,
    backgroundColor: HB_COLORS.action,
    alignItems: "center",
    justifyContent: "center",
  },
  badgeText: { fontSize: 10, lineHeight: 12, color: HB_COLORS.white, fontWeight: "700" },
  chips: { flexDirection: "row", gap: HB_SPACING.xs, marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs },
  chip: {
    flexShrink: 1,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    minHeight: 34,
    paddingHorizontal: 12,
    borderRadius: 17,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  chipText: { flexShrink: 1, fontSize: 13, color: HB_COLORS.textPrimary },
});
