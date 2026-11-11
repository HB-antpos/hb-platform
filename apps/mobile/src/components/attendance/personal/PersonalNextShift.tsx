// 打卡页底部「下一个班」：本周里今天之后的第一个班；草稿注明「店长发布后才算数」。
import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import type { AttendanceSchedule } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export function PersonalNextShift({ schedule, weekdayIndex }: { schedule: AttendanceSchedule; weekdayIndex: number }) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const isDraft = schedule.status.toLowerCase() === "draft";
  return (
    <View style={styles.card} accessible>
      <View style={styles.date}>
        <Text style={styles.weekday}>{t(`weekdays.${weekdayIndex}`)}</Text>
        <Text style={styles.day}>{Number(schedule.workDate.slice(8, 10))}</Text>
      </View>
      <View style={styles.body}>
        <Text style={styles.title}>
          {t("personal.nextShift", { start: schedule.startTime.slice(0, 5), end: schedule.endTime.slice(0, 5) })}
        </Text>
        <Text style={styles.sub}>
          {isDraft ? t("personal.nextShiftDraft") : schedule.storeName || t("personal.nextShiftPublished")}
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface + 2,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.sm,
  },
  date: { alignItems: "center", backgroundColor: "#EFF8FF", borderRadius: 10, height: 48, justifyContent: "center", width: 46 },
  weekday: { color: HB_COLORS.action, fontSize: 11, fontWeight: "700" },
  day: { color: HB_COLORS.action, fontSize: 17, fontWeight: "800" },
  body: { flex: 1, gap: 2 },
  title: { color: HB_COLORS.textPrimary, fontSize: 14.5, fontWeight: "700" },
  sub: { color: HB_COLORS.textSecondary, fontSize: 12 },
});
