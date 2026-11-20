import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { shiftWeekStart } from "@/modules/attendance/attendance-my-week";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";

function weekStartOf(date: string) {
  const day = new Date(`${date.slice(0, 10)}T00:00:00Z`);
  const offset = (day.getUTCDay() + 6) % 7;
  day.setUTCDate(day.getUTCDate() - offset);
  return day.toISOString().slice(0, 10);
}

/** 周一的日期 → 相对本周的标签：本周 / 下周 / 上周；更远的周不加标签。 */
export function weekTagKey(weekStartDate: string, today: string) {
  const thisWeek = weekStartOf(today);
  if (weekStartDate === thisWeek) return "current";
  if (weekStartDate === shiftWeekStart(thisWeek, 1)) return "next";
  if (weekStartDate === shiftWeekStart(thisWeek, -1)) return "previous";
  return undefined;
}

/** 「我的排班」与「整店排班」共用的周切换条：‹ 11-16 ~ 11-22 · 本周 ›。 */
export function WeekNavBar({
  label,
  onPreviousWeek,
  onNextWeek,
}: {
  label: string;
  onPreviousWeek: () => void;
  onNextWeek: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <View style={styles.weekNav}>
      <Pressable accessibilityRole="button" accessibilityLabel={t("availability.previousWeek")} onPress={onPreviousWeek} hitSlop={8} style={styles.weekArrow}>
        <Icon source="chevron-left" size={22} color={HB_COLORS.textSecondary} />
      </Pressable>
      <Text style={styles.weekLabel}>{label}</Text>
      <Pressable accessibilityRole="button" accessibilityLabel={t("availability.nextWeek")} onPress={onNextWeek} hitSlop={8} style={styles.weekArrow}>
        <Icon source="chevron-right" size={22} color={HB_COLORS.textSecondary} />
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  weekNav: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    height: 42,
    paddingHorizontal: 6,
  },
  weekArrow: { alignItems: "center", height: 36, justifyContent: "center", width: 36 },
  weekLabel: { color: HB_COLORS.textPrimary, flex: 1, fontSize: 14, fontVariant: ["tabular-nums"], fontWeight: "600", textAlign: "center" },
});
