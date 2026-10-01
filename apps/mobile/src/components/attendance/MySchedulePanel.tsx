import { useMemo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Card, IconButton, Text } from "react-native-paper";
import {
  buildMyWeekRows,
  defaultAvailabilityDates,
  type MyWeekRow,
  sumScheduledMinutes,
} from "@/modules/attendance/attendance-my-week";
import { isAllDayAvailability } from "@/modules/attendance/availability-entry";
import type { AttendanceAvailability, AttendanceWeek } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ATTENDANCE_STATUS_TONES, StatusPill } from "./AdjustmentFormControls";

function hhmm(value: string) {
  return value.slice(0, 5);
}

function formatHours(minutes: number) {
  const hours = minutes / 60;
  return Number.isInteger(hours) ? String(hours) : hours.toFixed(1);
}

/**
 * 员工「排班」页：一周 7 行，每行合并当天班次与可上班时间。
 * 点未来日期的行可直接填写/修改可上班时间，减少在两个列表之间来回对照。
 */
export function MySchedulePanel({
  weekStartDate,
  today,
  week,
  availability,
  isLoading,
  onPreviousWeek,
  onNextWeek,
  onFillAvailability,
  onEditAvailability,
}: {
  weekStartDate: string;
  today: string;
  week?: AttendanceWeek;
  availability: AttendanceAvailability[];
  isLoading: boolean;
  onPreviousWeek: () => void;
  onNextWeek: () => void;
  onFillAvailability: (dates: string[]) => void;
  onEditAvailability: (item: AttendanceAvailability) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const rows = useMemo(
    () => buildMyWeekRows(weekStartDate, today, week, availability),
    [availability, today, week, weekStartDate],
  );
  const totalMinutes = sumScheduledMinutes(rows);
  const fillDates = defaultAvailabilityDates(rows);
  const isFutureWeek = (rows[6]?.workDate ?? "") >= today;

  const handleRowPress = (row: MyWeekRow) => {
    if (row.isPast) return;
    if (row.availability[0]) {
      onEditAvailability(row.availability[0]);
    } else if (!row.schedules.length) {
      onFillAvailability([row.workDate]);
    }
  };

  const renderTrailing = (row: MyWeekRow) => {
    if (row.state === "scheduled") {
      if (row.isToday) return <StatusPill label={t("myAttendance.today")} tone="accent" />;
      return row.isPast ? <StatusPill label={t("myAttendance.done")} tone="success" /> : null;
    }
    if (row.state === "available") {
      const item = row.availability[0];
      const time = isAllDayAvailability(item.startTime, item.endTime)
        ? t("availability.allDay")
        : `${hhmm(item.startTime)}–${hhmm(item.endTime)}`;
      return <StatusPill label={t("myAttendance.availableAt", { time })} tone="neutral" />;
    }
    if (row.state === "unfilled") return <StatusPill label={t("myAttendance.unfilled")} tone="warning" />;
    return null;
  };

  return (
    <Card mode="outlined" style={styles.card}>
      <Card.Content style={styles.content}>
        <View style={styles.header}>
          <IconButton icon="chevron-left" accessibilityLabel={t("availability.previousWeek")} onPress={onPreviousWeek} />
          <View style={styles.headerCenter}>
            <Text variant="titleSmall" style={styles.tabular}>
              {`${rows[0]?.workDate.slice(5) ?? ""} – ${rows[6]?.workDate.slice(5) ?? ""}`}
            </Text>
            <Text variant="labelSmall" style={styles.muted}>
              {t("myAttendance.weekHours", { hours: formatHours(totalMinutes) })}
            </Text>
          </View>
          <IconButton icon="chevron-right" accessibilityLabel={t("availability.nextWeek")} onPress={onNextWeek} />
        </View>
        {isLoading ? <ActivityIndicator /> : null}

        <View>
          {rows.map((row, index) => (
            <Pressable
              key={row.workDate}
              accessibilityRole="button"
              disabled={row.isPast || row.state === "scheduled"}
              onPress={() => handleRowPress(row)}
              style={({ pressed }) => [
                styles.row,
                index === rows.length - 1 ? styles.rowLast : null,
                pressed ? styles.rowPressed : null,
              ]}
            >
              <View style={[styles.dateBlock, row.isToday ? styles.dateBlockToday : null]}>
                <Text variant="labelSmall" style={row.isToday ? styles.todayText : styles.muted}>
                  {t(`weekdays.${row.weekdayIndex}`)}
                </Text>
                <Text variant="titleMedium" style={row.isToday ? styles.todayText : undefined}>
                  {Number(row.workDate.slice(8, 10))}
                </Text>
              </View>
              <View style={styles.rowBody}>
                {row.schedules.length ? row.schedules.map((schedule) => (
                  <View key={schedule.scheduleGuid}>
                    <Text variant="bodyLarge" style={[styles.tabular, row.isToday ? styles.bold : null]}>
                      {`${hhmm(schedule.startTime)}–${hhmm(schedule.endTime)}`}
                    </Text>
                    {schedule.storeName ? (
                      <Text variant="bodySmall" style={styles.muted}>{schedule.storeName}</Text>
                    ) : null}
                  </View>
                )) : (
                  <Text variant="bodyMedium" style={styles.muted}>
                    {row.state === "rest" ? t("myAttendance.rest") : t("week.noSchedule")}
                  </Text>
                )}
                {row.holidayName ? (
                  <Text variant="bodySmall" style={styles.holiday}>{row.holidayName}</Text>
                ) : null}
              </View>
              {renderTrailing(row)}
            </Pressable>
          ))}
        </View>

        {isFutureWeek ? (
          <Button
            mode="outlined"
            icon="calendar-plus"
            onPress={() => onFillAvailability(fillDates)}
            style={styles.fillButton}
          >
            {t("myAttendance.fillAvailability")}
          </Button>
        ) : null}
      </Card.Content>
    </Card>
  );
}

const styles = StyleSheet.create({
  bold: { fontWeight: "600" },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  content: { gap: HB_SPACING.xs },
  dateBlock: { alignItems: "center", borderRadius: HB_RADIUS.control, paddingVertical: 4, width: 44 },
  dateBlockToday: { backgroundColor: ATTENDANCE_STATUS_TONES.accent.background },
  fillButton: { borderRadius: HB_RADIUS.control, marginTop: HB_SPACING.xs },
  header: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  headerCenter: { alignItems: "center" },
  holiday: { color: HB_COLORS.warning },
  muted: { color: HB_COLORS.textSecondary },
  row: {
    alignItems: "center",
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    minHeight: 56,
    paddingVertical: 6,
  },
  rowBody: { flex: 1, gap: 2 },
  rowLast: { borderBottomWidth: 0 },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  tabular: { fontVariant: ["tabular-nums"] },
  todayText: { color: ATTENDANCE_STATUS_TONES.accent.text, fontWeight: "600" },
});
