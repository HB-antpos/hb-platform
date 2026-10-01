import type { ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, IconButton, Text } from "react-native-paper";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

/** 列表与员工汇总共用的页头：返回、标题、筛选（带角标）、来源切换（两个来源都有权限时）、页签切换、时间与分店范围按钮。 */
export function LegacyLogsHeader({
  title,
  activeTab,
  recordsLabel,
  employeesLabel,
  scopeLabel,
  filterCount,
  backLabel,
  filterLabel,
  onBack,
  onOpenFilters,
  onSwitchTab,
  sourceSwitch,
}: {
  title: string;
  activeTab: "records" | "employees";
  recordsLabel: string;
  employeesLabel: string;
  scopeLabel: string;
  filterCount: number;
  backLabel: string;
  filterLabel: string;
  onBack: () => void;
  onOpenFilters: () => void;
  onSwitchTab: (tab: "records" | "employees") => void;
  sourceSwitch?: ReactNode;
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
      {sourceSwitch}
      <View style={styles.tabs} accessibilityRole="tablist">
        {(["records", "employees"] as const).map((tab) => {
          const active = tab === activeTab;
          return (
            <Pressable
              key={tab}
              accessibilityRole="tab"
              accessibilityState={{ selected: active }}
              onPress={() => (active ? undefined : onSwitchTab(tab))}
              style={[styles.tab, active ? styles.tabOn : null]}
            >
              <Text style={[styles.tabText, active ? styles.tabTextOn : null]}>{tab === "records" ? recordsLabel : employeesLabel}</Text>
            </Pressable>
          );
        })}
      </View>
      <Pressable accessibilityRole="button" accessibilityLabel={`${filterLabel}: ${scopeLabel}`} onPress={onOpenFilters} style={styles.scope}>
        <Icon source="calendar" size={15} color={HB_COLORS.textSecondary} />
        <Text numberOfLines={1} style={styles.scopeText}>
          {scopeLabel}
        </Text>
        <Icon source="chevron-down" size={15} color={HB_COLORS.textSecondary} />
      </Pressable>
    </View>
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
  tabs: {
    flexDirection: "row",
    marginHorizontal: HB_SPACING.md,
    marginBottom: HB_SPACING.xs,
    padding: 2,
    borderRadius: 10,
    backgroundColor: "#E4E7EB",
  },
  tab: { flex: 1, minHeight: 40, alignItems: "center", justifyContent: "center", borderRadius: 8 },
  tabOn: { backgroundColor: HB_COLORS.white },
  tabText: { fontSize: 14, color: "#3D4550" },
  tabTextOn: { fontWeight: "700", color: HB_COLORS.textPrimary },
  scope: {
    flexDirection: "row",
    alignItems: "center",
    alignSelf: "flex-start",
    gap: 6,
    maxWidth: "92%",
    minHeight: 34,
    marginHorizontal: HB_SPACING.md,
    marginBottom: HB_SPACING.xs,
    paddingHorizontal: 12,
    borderRadius: 17,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  scopeText: { flexShrink: 1, fontSize: 13, color: HB_COLORS.textPrimary },
});

