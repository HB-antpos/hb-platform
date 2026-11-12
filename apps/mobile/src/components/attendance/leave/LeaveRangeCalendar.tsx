// 无薪请假表单里的两周日历：周一开头、一页 14 天，蓝点 = 那天有排班；点一下选起点，再点选终点。
import { useMemo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { IconButton, Text } from "react-native-paper";
import { LEAVE_CALENDAR_DAYS, twoWeekDates } from "@/modules/attendance/leave-picker";
import { addDays } from "@/modules/attendance/leave-rules";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useLeaveDateFormat } from "./leave-ui";

const WEEKDAY_KEYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;
const RANGE_FILL = "#E8F1FF";

export interface LeaveRangeCalendarProps {
  /** 当前页第一天（周一）。 */
  monday: string;
  onShiftPage: (monday: string) => void;
  today: string;
  startDate: string;
  endDate: string;
  rostered: ReadonlySet<string>;
  onPressDate: (date: string) => void;
}

export function LeaveRangeCalendar({ monday, onShiftPage, today, startDate, endDate, rostered, onPressDate }: LeaveRangeCalendarProps) {
  const { t } = useAppTranslation("attendanceLeave");
  const { dayLabel, range } = useLeaveDateFormat();
  const dates = useMemo(() => twoWeekDates(monday), [monday]);
  const rows = [dates.slice(0, 7), dates.slice(7, 14)];
  const lastDate = dates[dates.length - 1] ?? monday;

  return (
    <View style={styles.calendar}>
      <View style={styles.header}>
        <IconButton
          icon="chevron-left"
          size={20}
          style={styles.arrow}
          accessibilityLabel={t("timeOff.previousWeeks")}
          onPress={() => onShiftPage(addDays(monday, -LEAVE_CALENDAR_DAYS))}
        />
        <Text variant="titleSmall" style={styles.title}>{`${range(monday, lastDate)} ${lastDate.slice(0, 4)}`}</Text>
        <IconButton
          icon="chevron-right"
          size={20}
          style={styles.arrow}
          accessibilityLabel={t("timeOff.nextWeeks")}
          onPress={() => onShiftPage(addDays(monday, LEAVE_CALENDAR_DAYS))}
        />
      </View>
      <View style={styles.weekRow}>
        {WEEKDAY_KEYS.map((key, index) => (
          <Text key={`${key}-${index}`} variant="labelSmall" style={styles.weekHeader}>{t(`calendar.header.${key}`)}</Text>
        ))}
      </View>
      {rows.map((row, rowIndex) => (
        <View key={rowIndex} style={styles.weekRow}>
          {row.map((date) => {
            const inRange = date >= startDate && date <= endDate;
            const isEdge = date === startDate || date === endDate;
            const isSingle = startDate === endDate;
            const isPast = date < today;
            const hasRoster = rostered.has(date);
            // 区间底色：起点只铺右半、终点只铺左半，中间整格铺满，连成一条。
            const fillStyle = !inRange || isSingle
              ? null
              : date === startDate
                ? styles.fillRight
                : date === endDate
                  ? styles.fillLeft
                  : styles.fillFull;
            const a11y = [dayLabel(date), hasRoster ? t("timeOff.rostered") : ""].filter(Boolean).join(" · ");
            return (
              <Pressable
                key={date}
                accessibilityRole="button"
                accessibilityLabel={a11y}
                accessibilityState={{ selected: inRange }}
                onPress={() => onPressDate(date)}
                style={styles.cell}
              >
                {fillStyle ? <View style={[styles.fill, fillStyle]} /> : null}
                <View
                  style={[
                    styles.dayCircle,
                    date === today && !isEdge ? styles.todayCircle : null,
                    isEdge ? styles.edgeCircle : null,
                  ]}
                >
                  <Text
                    style={[
                      styles.dayText,
                      isPast && !inRange ? styles.pastText : null,
                      isEdge ? styles.edgeText : null,
                    ]}
                  >
                    {Number(date.slice(8, 10))}
                  </Text>
                </View>
                <View style={[styles.dot, hasRoster ? (isPast && !inRange ? styles.dotPast : styles.dotOn) : null]} />
              </Pressable>
            );
          })}
        </View>
      ))}
      <View style={styles.legend}>
        <View style={[styles.legendDot, styles.dotOn]} />
        <Text variant="bodySmall" style={styles.legendText}>{t("timeOff.rostered")}</Text>
        <Text variant="bodySmall" style={[styles.legendText, styles.legendHint]} numberOfLines={1}>{t("timeOff.tapHint")}</Text>
      </View>
    </View>
  );
}

const CELL_HEIGHT = 46;
const CELL_TOP = 2;
const CIRCLE = 34;

const styles = StyleSheet.create({
  calendar: {
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    paddingBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.xxs,
  },
  header: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  arrow: { margin: 0 },
  title: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], fontWeight: "700" },
  weekRow: { flexDirection: "row" },
  weekHeader: { color: HB_COLORS.textSecondary, flex: 1, paddingBottom: 2, textAlign: "center" },
  // 圆圈固定贴顶 2pt，区间底色用同样的 top / height，保证两者对齐。
  cell: { alignItems: "center", flex: 1, height: CELL_HEIGHT, paddingTop: CELL_TOP },
  fill: { backgroundColor: RANGE_FILL, height: CIRCLE, position: "absolute", top: CELL_TOP },
  fillFull: { left: 0, right: 0 },
  fillLeft: { left: 0, right: "50%" },
  fillRight: { left: "50%", right: 0 },
  dayCircle: { alignItems: "center", borderRadius: CIRCLE / 2, height: CIRCLE, justifyContent: "center", width: CIRCLE },
  todayCircle: { borderColor: HB_COLORS.textPrimary, borderWidth: 1.5 },
  edgeCircle: { backgroundColor: HB_COLORS.action },
  dayText: { color: HB_COLORS.textPrimary, fontSize: 15, fontVariant: ["tabular-nums"] },
  pastText: { color: HB_COLORS.textSecondary, opacity: 0.6 },
  edgeText: { color: HB_COLORS.white, fontWeight: "700" },
  dot: { borderRadius: 3, height: 5, marginTop: 3, width: 5 },
  dotOn: { backgroundColor: HB_COLORS.action },
  dotPast: { backgroundColor: HB_COLORS.textSecondary, opacity: 0.5 },
  legend: { alignItems: "center", flexDirection: "row", gap: 6, paddingHorizontal: HB_SPACING.xs, paddingTop: 2 },
  legendDot: { borderRadius: 3, height: 6, width: 6 },
  legendText: { color: HB_COLORS.textSecondary },
  legendHint: { flexShrink: 1, marginLeft: HB_SPACING.xs },
});
