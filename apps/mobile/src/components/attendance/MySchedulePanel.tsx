import { useMemo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import {
  buildMyWeekRows,
  computeScheduleHourStats,
  defaultAvailabilityDates,
  formatScheduleHours,
  schedulePaidMinutes,
  shiftWeekStart,
  type MyWeekRow,
} from "@/modules/attendance/attendance-my-week";
import {
  availabilityFillProgress,
  myShiftOutcome,
  summarizeMyWeek,
} from "@/modules/attendance/attendance-my-week-view";
import { isAllDayAvailability } from "@/modules/attendance/availability-entry";
import type { AttendanceAvailability, AttendanceSchedule, AttendanceWeek } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ATTENDANCE_STATUS_TONES, StatusPill } from "./AdjustmentFormControls";

const SUMMARY_BLUE = "#0958D9";
const BAR_MAX_HEIGHT = 40;

function hhmm(value: string) {
  return value.slice(0, 5);
}

/** 已工作分钟 → 「4:03」。 */
function clock(minutes: number) {
  return `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, "0")}`;
}

/** 周一的日期 → 相对本周的标签：本周 / 下周 / 上周；更远的周不加标签。 */
function weekTagKey(weekStartDate: string, today: string) {
  const thisWeek = buildWeekStartOf(today);
  if (weekStartDate === thisWeek) return "current";
  if (weekStartDate === shiftWeekStart(thisWeek, 1)) return "next";
  if (weekStartDate === shiftWeekStart(thisWeek, -1)) return "previous";
  return undefined;
}

function buildWeekStartOf(date: string) {
  const day = new Date(`${date.slice(0, 10)}T00:00:00Z`);
  const offset = (day.getUTCDay() + 6) % 7;
  day.setUTCDate(day.getUTCDate() - offset);
  return day.toISOString().slice(0, 10);
}

/**
 * 员工「我的排班」：
 * - 顶部蓝色汇总卡（几个班、共几小时、已上几班、每天的小柱子）；
 * - 有班的日子每天一张卡，过去的写实际结果，今天的带「去打卡」；没班的日子收成虚线细行；
 * - 一个班都还没发布的未来周：橙色提示 + 已填进度，每天一行可上班 / 不能上班 / 还没填。
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
  onGoToToday,
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
  /** 今天那张卡的「去打卡」：切回「今天」页签。 */
  onGoToToday?: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const rows = useMemo(
    () => buildMyWeekRows(weekStartDate, today, week, availability),
    [availability, today, week, weekStartDate],
  );
  // 周工时 / 工作日 / 周末：请假班次不计入。
  const hourStats = computeScheduleHourStats(rows.flatMap((row) => row.schedules));
  const summary = summarizeMyWeek(rows);
  const progress = availabilityFillProgress(rows);
  const fillDates = defaultAvailabilityDates(rows);
  const isFutureWeek = (rows[0]?.workDate ?? "") > today;
  // 未来周且一个班都没发布：进入「先报可上班时间」模式。
  const fillMode = isFutureWeek && progress.unpublished;
  const tagKey = weekTagKey(weekStartDate, today);
  const range = `${rows[0]?.workDate.slice(5) ?? ""} ~ ${rows[6]?.workDate.slice(5) ?? ""}`;
  const maxDaily = Math.max(...summary.dailyMinutes, 1);

  const editRow = (row: MyWeekRow) => {
    if (row.isPast) return;
    if (row.availability[0]) onEditAvailability(row.availability[0]);
    else onFillAvailability([row.workDate]);
  };

  const availabilityText = (row: MyWeekRow) => {
    // 可上班取第一段可上班时段；只填了不能上班的取第一段不能上班时段。
    const unavailable = row.state === "unavailable";
    const item = row.availability.find((entry) => Boolean(entry.isUnavailable) === unavailable) ?? row.availability[0];
    if (!item) return "";
    const time = isAllDayAvailability(item.startTime, item.endTime)
      ? t("availability.allDay")
      : `${hhmm(item.startTime)}–${hhmm(item.endTime)}`;
    return t(unavailable ? "myAttendance.unavailableAt" : "myAttendance.availableAt", { time });
  };

  const renderShiftPill = (row: MyWeekRow, schedule: AttendanceSchedule) => {
    const outcome = myShiftOutcome(schedule, row.isPast);
    switch (outcome.kind) {
      case "leave":
        return <StatusPill label={t(`leaveTypes.${outcome.leaveType}`, outcome.leaveType)} tone="warning" />;
      case "completed":
        return (
          <StatusPill
            label={t("myAttendance.outcome.completed", { hours: formatScheduleHours(outcome.paidMinutes) })}
            tone="success"
          />
        );
      case "pastUnknown":
        return <StatusPill label={t("myAttendance.done")} tone="success" />;
      case "missingClockOut":
        return <StatusPill label={t("myAttendance.outcome.missingClockOut")} tone="warning" />;
      case "absent":
        return <StatusPill label={t("myAttendance.outcome.absent")} tone="danger" />;
      default:
        return row.isToday ? <StatusPill label={t("myAttendance.today")} tone="accent" /> : null;
    }
  };

  const shiftSubline = (row: MyWeekRow, schedule: AttendanceSchedule) => {
    const outcome = myShiftOutcome(schedule, row.isPast);
    if (outcome.kind === "working") return t("myAttendance.outcome.working", { time: clock(outcome.workedMinutes) });
    if (outcome.kind === "onBreak") return t("myAttendance.outcome.onBreak", { time: clock(outcome.workedMinutes) });
    return [
      schedule.storeName,
      schedule.leaveType ? undefined : t("myAttendance.paidHours", { hours: formatScheduleHours(schedulePaidMinutes(schedule)) }),
    ].filter(Boolean).join(" · ");
  };

  const renderDateBlock = (row: MyWeekRow, compact = false) => (
    <View style={[styles.dateBlock, compact && styles.dateBlockCompact, row.isToday && styles.dateBlockToday]}>
      <Text style={[styles.dateWeekday, row.isToday && styles.dateTodayText]}>{t(`weekdays.${row.weekdayIndex}`)}</Text>
      <Text style={[styles.dateDay, row.isToday && styles.dateTodayText]}>{Number(row.workDate.slice(8, 10))}</Text>
    </View>
  );

  const renderShiftCard = (row: MyWeekRow) => (
    <View key={row.workDate} style={[styles.dayCard, row.isToday && styles.dayCardToday]}>
      {renderDateBlock(row)}
      <View style={styles.dayBody}>
        {row.schedules.map((schedule) => (
          <View key={schedule.scheduleGuid} style={styles.shiftLine}>
            <View style={styles.shiftText}>
              <Text style={styles.shiftTime}>{`${hhmm(schedule.startTime)}–${hhmm(schedule.endTime)}`}</Text>
              <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>{shiftSubline(row, schedule)}</Text>
            </View>
            {renderShiftPill(row, schedule)}
          </View>
        ))}
        {row.holidayName ? <Text variant="bodySmall" style={styles.holiday}>{row.holidayName}</Text> : null}
        {row.isToday && onGoToToday ? (
          <Pressable accessibilityRole="button" onPress={onGoToToday} hitSlop={8} style={styles.goPunch}>
            <Text style={styles.goPunchText}>{t("myAttendance.goPunch")}</Text>
            <Icon source="chevron-right" size={16} color={HB_COLORS.action} />
          </Pressable>
        ) : null}
      </View>
    </View>
  );

  const renderRestRow = (row: MyWeekRow) => {
    const pill = row.state === "available"
      ? <StatusPill label={availabilityText(row)} tone="success" />
      : row.state === "unavailable"
        ? <StatusPill label={availabilityText(row)} tone="danger" />
        : row.state === "unfilled"
          ? <StatusPill label={t("myAttendance.unfilled")} tone="warning" />
          : null;
    return (
      <Pressable
        key={row.workDate}
        accessibilityRole="button"
        disabled={row.isPast}
        onPress={() => editRow(row)}
        style={({ pressed }) => [styles.restRow, pressed && styles.pressed]}
      >
        <Text style={styles.restDate}>
          {`${t(`weekdays.${row.weekdayIndex}`)} ${Number(row.workDate.slice(8, 10))}`}
        </Text>
        <Text variant="bodySmall" style={[styles.muted, styles.flex]} numberOfLines={1}>
          {row.holidayName ?? t("myAttendance.rest")}
        </Text>
        {pill}
      </Pressable>
    );
  };

  const renderFillRow = (row: MyWeekRow) => {
    const tone = row.state === "available" ? "available" : row.state === "unavailable" ? "unavailable" : "unfilled";
    const note = row.availability[0]?.note;
    return (
      <Pressable
        key={row.workDate}
        accessibilityRole="button"
        disabled={row.isPast}
        onPress={() => editRow(row)}
        style={({ pressed }) => [styles.fillRow, styles[`fill_${tone}`], pressed && styles.pressed]}
      >
        {renderDateBlock(row, true)}
        <View style={styles.flex}>
          {tone === "unfilled" ? (
            <>
              <Text style={[styles.fillTitle, styles.faint]}>{t("myAttendance.fill.unfilled")}</Text>
              <Text variant="bodySmall" style={styles.faint}>{t("myAttendance.fill.tapToFill")}</Text>
            </>
          ) : (
            <>
              <Text style={styles.fillTitle}>{availabilityText(row)}</Text>
              {note ? <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>{note}</Text> : null}
            </>
          )}
        </View>
        {!row.isPast ? (
          <Text style={styles.editLink}>{tone === "unfilled" ? "+" : t("myAttendance.fill.edit")}</Text>
        ) : null}
      </Pressable>
    );
  };

  return (
    <View style={styles.root}>
      <View style={styles.weekNav}>
        <Pressable accessibilityRole="button" accessibilityLabel={t("availability.previousWeek")} onPress={onPreviousWeek} hitSlop={8} style={styles.weekArrow}>
          <Icon source="chevron-left" size={22} color={HB_COLORS.textSecondary} />
        </Pressable>
        <Text style={styles.weekLabel}>
          {tagKey ? `${range} · ${t(`myAttendance.weekTag.${tagKey}`)}` : range}
        </Text>
        <Pressable accessibilityRole="button" accessibilityLabel={t("availability.nextWeek")} onPress={onNextWeek} hitSlop={8} style={styles.weekArrow}>
          <Icon source="chevron-right" size={22} color={HB_COLORS.textSecondary} />
        </Pressable>
      </View>
      {isLoading ? <ActivityIndicator /> : null}

      {fillMode ? (
        <View style={styles.banner}>
          <Text style={styles.bannerTitle}>
            {t("myAttendance.fill.title", { week: tagKey ? t(`myAttendance.weekTag.${tagKey}`) : range })}
          </Text>
          <Text variant="bodySmall" style={styles.bannerText}>{t("myAttendance.fill.hint")}</Text>
          {progress.total > 0 ? (
            <View style={styles.progressRow}>
              <Text style={styles.progressText}>
                {t("myAttendance.fill.progress", { filled: progress.filled, total: progress.total })}
              </Text>
              <View style={styles.progressTrack}>
                <View style={[styles.progressFill, { flex: progress.filled }]} />
                <View style={{ flex: progress.total - progress.filled }} />
              </View>
              <Text style={styles.progressText}>{t("myAttendance.fill.due")}</Text>
            </View>
          ) : null}
        </View>
      ) : (
        <View style={styles.summary}>
          <View style={styles.summaryTop}>
            <View>
              <Text style={styles.summaryLabel}>{t("myAttendance.summary.shifts", { count: summary.shiftCount })}</Text>
              <Text style={styles.summaryHours}>
                {formatScheduleHours(hourStats.totalMinutes)}
                <Text style={styles.summaryUnit}>{` ${t("myAttendance.summary.hoursUnit")}`}</Text>
              </Text>
            </View>
            <View style={styles.summaryRight}>
              {summary.doneCount > 0 ? (
                <Text style={styles.summaryLabel}>
                  {t("myAttendance.summary.done", { count: summary.doneCount, hours: formatScheduleHours(summary.doneMinutes) })}
                </Text>
              ) : null}
              <Text style={styles.summaryLabel}>
                {t("myAttendance.weekHoursSplit", {
                  weekday: formatScheduleHours(hourStats.weekdayMinutes),
                  weekend: formatScheduleHours(hourStats.weekendMinutes),
                })}
              </Text>
            </View>
          </View>
          {/* 每天一根小柱子：高度按当天排班计薪小时，今天的更亮、字加粗。 */}
          <View style={styles.bars}>
            {rows.map((row, index) => {
              const minutes = summary.dailyMinutes[index] ?? 0;
              return (
                <View key={row.workDate} style={styles.barColumn}>
                  <View
                    style={[
                      styles.bar,
                      minutes ? { height: Math.max(6, (minutes / maxDaily) * BAR_MAX_HEIGHT) } : styles.barEmpty,
                      row.isToday && minutes ? styles.barToday : null,
                    ]}
                  />
                  <Text style={[styles.barLabel, row.isToday && styles.barLabelToday]}>
                    {t(`myAttendance.weekdayShort.${row.weekdayIndex}`)}
                  </Text>
                </View>
              );
            })}
          </View>
        </View>
      )}

      {rows.map((row) => (row.schedules.length
        ? renderShiftCard(row)
        : fillMode ? renderFillRow(row) : renderRestRow(row)))}

      {fillDates.length ? (
        <Button
          mode="contained"
          icon="calendar-plus"
          onPress={() => onFillAvailability(fillDates)}
          style={styles.fillButton}
          contentStyle={styles.fillButtonContent}
        >
          {progress.filled > 0
            ? t("myAttendance.fill.remaining", { count: fillDates.length })
            : t("myAttendance.fillAvailability")}
        </Button>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { gap: HB_SPACING.xs },
  flex: { flex: 1 },
  muted: { color: HB_COLORS.textSecondary },
  faint: { color: "#98A2B3" },
  pressed: { opacity: 0.7 },
  holiday: { color: HB_COLORS.warning },
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
  summary: { backgroundColor: SUMMARY_BLUE, borderRadius: 16, gap: HB_SPACING.sm, padding: 14 },
  summaryTop: { alignItems: "flex-end", flexDirection: "row", justifyContent: "space-between" },
  summaryLabel: { color: "#D1E9FF", fontSize: 12.5 },
  summaryHours: { color: HB_COLORS.white, fontSize: 30, fontVariant: ["tabular-nums"], fontWeight: "800" },
  summaryUnit: { color: "#D1E9FF", fontSize: 15, fontWeight: "600" },
  summaryRight: { alignItems: "flex-end", flexShrink: 1, gap: 2 },
  bars: { alignItems: "flex-end", flexDirection: "row", gap: 5, height: BAR_MAX_HEIGHT + 16 },
  barColumn: { alignItems: "stretch", flex: 1, gap: 3, justifyContent: "flex-end" },
  bar: { backgroundColor: "rgba(255,255,255,0.45)", borderRadius: 4 },
  barEmpty: { backgroundColor: "rgba(255,255,255,0.18)", height: 3 },
  barToday: { backgroundColor: HB_COLORS.white },
  barLabel: { color: "#D1E9FF", fontSize: 10, textAlign: "center" },
  barLabelToday: { color: HB_COLORS.white, fontWeight: "800" },
  dayCard: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 14,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    padding: 10,
  },
  dayCardToday: { borderColor: "#84CAFF", borderWidth: 1.5 },
  dateBlock: {
    alignItems: "center",
    alignSelf: "stretch",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: 10,
    justifyContent: "center",
    minHeight: 52,
    width: 44,
  },
  dateBlockCompact: { minHeight: 42, width: 40 },
  dateBlockToday: { backgroundColor: SUMMARY_BLUE },
  dateWeekday: { color: HB_COLORS.textSecondary, fontSize: 11 },
  dateDay: { color: HB_COLORS.textPrimary, fontSize: 18, fontVariant: ["tabular-nums"], fontWeight: "700" },
  dateTodayText: { color: HB_COLORS.white },
  dayBody: { flex: 1, gap: 6, justifyContent: "center" },
  shiftLine: { alignItems: "flex-start", flexDirection: "row", gap: HB_SPACING.xs },
  shiftText: { flex: 1, gap: 2 },
  shiftTime: { color: HB_COLORS.textPrimary, fontSize: 17, fontVariant: ["tabular-nums"], fontWeight: "800" },
  goPunch: { alignItems: "center", alignSelf: "flex-end", flexDirection: "row" },
  goPunchText: { color: HB_COLORS.action, fontSize: 13, fontWeight: "700" },
  restRow: {
    alignItems: "center",
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.surface,
    borderStyle: "dashed",
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: 40,
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  restDate: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "700", width: 54 },
  banner: {
    backgroundColor: ATTENDANCE_STATUS_TONES.warning.background,
    borderColor: "#FEDF89",
    borderRadius: 14,
    borderWidth: 1,
    gap: 4,
    padding: HB_SPACING.sm,
  },
  bannerTitle: { color: HB_COLORS.warning, fontSize: 15, fontWeight: "700" },
  bannerText: { color: "#7A2E0E" },
  progressRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, marginTop: 4 },
  progressText: { color: HB_COLORS.warning, fontSize: 12, fontWeight: "700" },
  progressTrack: { backgroundColor: "#FEF0C7", borderRadius: 3, flex: 1, flexDirection: "row", height: 6, overflow: "hidden" },
  progressFill: { backgroundColor: "#F79009", borderRadius: 3 },
  fillRow: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 14,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    padding: 9,
  },
  fill_available: { borderLeftColor: "#12B76A", borderLeftWidth: 4 },
  fill_unavailable: { borderLeftColor: "#F04438", borderLeftWidth: 4 },
  fill_unfilled: { backgroundColor: "#FCFCFD", borderColor: HB_COLORS.outline, borderStyle: "dashed", borderWidth: 1.5 },
  fillTitle: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "700" },
  editLink: { color: HB_COLORS.action, fontSize: 14, fontWeight: "700", paddingHorizontal: 6 },
  fillButton: { borderRadius: 24, marginTop: 4 },
  fillButtonContent: { height: 46 },
});
