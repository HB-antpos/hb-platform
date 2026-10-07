import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { DAILY_CLOSE_STATUS_TABS } from "@/modules/daily-closes/logic";
import type { DailyCloseStatusTab } from "@/modules/daily-closes/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { DAILY_UI } from "./ui";

/** 状态分段控件：全部 / 短款 / 长款 / 已平，带计数；「无金额」只在「全部」里出现，不单独成页签。 */
export function DailyCloseStatusTabs({
  value,
  counts,
  onChange,
}: {
  value: DailyCloseStatusTab;
  counts: Record<DailyCloseStatusTab, number> | null;
  onChange: (next: DailyCloseStatusTab) => void;
}) {
  const { t } = useAppTranslation("dailyCloses");
  return (
    <View style={styles.track} accessibilityRole="tablist">
      {DAILY_CLOSE_STATUS_TABS.map((tab) => {
        const active = tab === value;
        return (
          <Pressable
            key={tab}
            accessibilityRole="tab"
            accessibilityState={{ selected: active }}
            onPress={() => (active ? undefined : onChange(tab))}
            style={[styles.tab, active ? styles.tabOn : null]}
          >
            <Text style={[styles.label, active ? styles.labelOn : null]} numberOfLines={1}>
              {t(`status.${tab}`)}
            </Text>
            <Text style={[styles.count, DAILY_UI.mono, active ? styles.countOn : null]} numberOfLines={1}>
              {counts ? counts[tab].toLocaleString("en-AU") : "–"}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  track: {
    flexDirection: "row",
    marginHorizontal: HB_SPACING.md,
    padding: 2,
    borderRadius: 10,
    backgroundColor: "#E4E7EB",
  },
  tab: { flex: 1, minHeight: 44, alignItems: "center", justifyContent: "center", borderRadius: 8, paddingHorizontal: 2 },
  tabOn: { backgroundColor: HB_COLORS.white },
  label: { fontSize: 13, color: "#3D4550" },
  labelOn: { fontWeight: "700", color: HB_COLORS.action },
  count: { fontSize: 12, color: HB_COLORS.textSecondary },
  countOn: { color: HB_COLORS.action, fontWeight: "600" },
});
