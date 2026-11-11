// 员工打卡页的本周日期条：‹ 周一 … 周日 ›，每天写自己的班（10–18 / 休 / 假），点哪天看哪天的打卡；
// 代替原来的日期选择框。今天的星期字蓝色加粗；选中的日子深色底。
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { weekDayShiftLabel } from "@/modules/attendance/attendance-punch-hero";
import type { AttendanceWeek } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

function addDays(value: string, days: number) {
  return new Date(Date.parse(`${value}T00:00:00Z`) + days * 86_400_000).toISOString().slice(0, 10);
}

export function PersonalWeekStrip({
  weekStartDate,
  selectedDate,
  today,
  week,
  onSelect,
  onPreviousWeek,
  onNextWeek,
}: {
  weekStartDate: string;
  selectedDate: string;
  today: string;
  week?: AttendanceWeek;
  onSelect: (date: string) => void;
  onPreviousWeek: () => void;
  onNextWeek: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const dates = Array.from({ length: 7 }, (_, index) => addDays(weekStartDate, index));
  return (
    <View style={styles.row}>
      <Pressable accessibilityRole="button" accessibilityLabel={t("personal.previousWeek")} hitSlop={6} onPress={onPreviousWeek} style={styles.arrow}>
        <Icon source="chevron-left" size={20} color={HB_COLORS.textSecondary} />
      </Pressable>
      {dates.map((date, index) => {
        const selected = date === selectedDate;
        const day = week?.days.find((item) => item.workDate.slice(0, 10) === date);
        const label = weekDayShiftLabel(day?.schedules ?? []);
        const text = !week
          ? " "
          : label.kind === "shift"
            ? `${label.text}${label.more > 0 ? "+" : ""}`
            : label.kind === "leave"
              ? t("personal.dayLeave")
              : t("personal.dayRest");
        return (
          <Pressable
            key={date}
            accessibilityRole="button"
            accessibilityState={{ selected }}
            accessibilityLabel={`${t(`weekdays.${index}`)} ${date.slice(5)} ${text}`}
            onPress={() => onSelect(date)}
            style={[styles.day, selected ? styles.daySelected : null]}
          >
            <Text style={[styles.weekday, date === today && !selected ? styles.weekdayToday : null, selected ? styles.selectedSub : null]}>
              {t(`weekdays.${index}`)}
            </Text>
            <Text style={[styles.date, selected ? styles.selectedText : null]}>{Number(date.slice(8, 10))}</Text>
            <Text
              style={[styles.shift, label.kind !== "shift" ? styles.shiftOff : null, selected ? styles.selectedSub : null]}
              numberOfLines={1}
              adjustsFontSizeToFit
              minimumFontScale={0.75}
            >
              {text}
            </Text>
          </Pressable>
        );
      })}
      <Pressable accessibilityRole="button" accessibilityLabel={t("personal.nextWeek")} hitSlop={6} onPress={onNextWeek} style={styles.arrow}>
        <Icon source="chevron-right" size={20} color={HB_COLORS.textSecondary} />
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  row: { alignItems: "center", flexDirection: "row", gap: 2 },
  arrow: { alignItems: "center", height: 48, justifyContent: "center", width: 20 },
  day: { alignItems: "center", borderRadius: 11, flex: 1, minWidth: 0, paddingHorizontal: 1, paddingVertical: 5 },
  daySelected: { backgroundColor: HB_COLORS.textPrimary },
  weekday: { color: HB_COLORS.textSecondary, fontSize: 11 },
  weekdayToday: { color: HB_COLORS.action, fontWeight: "700" },
  date: { color: HB_COLORS.textPrimary, fontSize: 15, fontVariant: ["tabular-nums"], fontWeight: "700" },
  shift: { color: "#98A2B3", fontSize: 9.5, fontVariant: ["tabular-nums"], fontWeight: "600" },
  shiftOff: { color: "#D0D5DD" },
  selectedText: { color: HB_COLORS.white },
  selectedSub: { color: "#CFD4DC" },
});
