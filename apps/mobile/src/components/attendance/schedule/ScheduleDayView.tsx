// 排班「日视图」：选一天，上方每小时在岗人数柱状图（只有 0～1 人的时段标红并写出缺口），
// 下方每人一条班次条（草稿虚线、员工可上班时段铺浅绿、不能上班 / 请假整行灰掉）。点一行打开现有编辑弹层。
import { useMemo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { formatScheduleHours, computeScheduleHourStats } from "@/modules/attendance/attendance-my-week";
import {
  buildDayStaffing,
  isAllDayRange,
  normalizeClockTime,
  shiftMinutesRange,
  type ScheduleGridCell,
} from "@/modules/attendance/schedule-grid";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PatternFill } from "../approvals/approval-ui";

interface DayRow {
  userGuid: string;
  employeeName?: string;
}

const clock = (minutes: number) => {
  const normalized = ((minutes % 1440) + 1440) % 1440;
  return `${String(Math.floor(normalized / 60)).padStart(2, "0")}:${String(normalized % 60).padStart(2, "0")}`;
};

export function ScheduleDayView<R extends DayRow>({
  days,
  today,
  dayIndex,
  onSelectDay,
  rows,
  peopleByDay,
  cellOf,
  disabled,
  onOpenCell,
}: {
  days: string[];
  today: string;
  dayIndex: number;
  onSelectDay: (index: number) => void;
  rows: R[];
  peopleByDay: number[];
  cellOf: (row: R, day: string) => ScheduleGridCell;
  disabled: boolean;
  onOpenCell: (row: R, day: string, index: number, cell: ScheduleGridCell) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const day = days[dayIndex] ?? days[0];
  const cells = useMemo(() => rows.map((row) => ({ row, cell: cellOf(row, day) })), [cellOf, day, rows]);
  const staffing = useMemo(() => buildDayStaffing(cells.flatMap(({ cell }) => cell.schedules)), [cells]);

  // 班次条的轴：覆盖人手范围与员工可上班时段，至少 8 小时。
  const axis = useMemo(() => {
    let start = staffing.range.start;
    let end = staffing.range.end;
    cells.forEach(({ cell }) => cell.availability.forEach((item) => {
      if (item.isUnavailable || isAllDayRange(item.startTime, item.endTime)) return;
      const range = shiftMinutesRange(item);
      if (!range) return;
      start = Math.min(start, Math.floor(range.start / 60) * 60);
      end = Math.max(end, Math.ceil(range.end / 60) * 60);
    }));
    if (end - start < 480) end = start + 480;
    return { start, end };
  }, [cells, staffing.range]);
  const pct = (minutes: number): `${number}%` => `${Math.min(100, Math.max(0, ((minutes - axis.start) / (axis.end - axis.start)) * 100))}%`;
  const width = (from: number, to: number): `${number}%` => `${Math.max(1, ((Math.min(to, axis.end) - Math.max(from, axis.start)) / (axis.end - axis.start)) * 100)}%`;
  const ticks = Array.from({ length: Math.floor((axis.end - axis.start) / 180) + 1 }, (_, index) => axis.start + index * 180);
  const maxCount = Math.max(1, ...staffing.hours.map((item) => item.count));
  const gapText = staffing.gaps
    .map((gap) => t(gap.count === 0 ? "scheduleManagement.dayView.gapNone" : "scheduleManagement.dayView.gapOne", { start: clock(gap.start), end: clock(gap.end) }))
    .join("；");
  const hasShifts = staffing.hours.some((item) => item.count > 0);

  return (
    <View style={styles.root}>
      <View style={styles.days}>
        {days.map((date, index) => {
          const selected = index === dayIndex;
          return (
            <Pressable
              key={date}
              accessibilityRole="tab"
              accessibilityState={{ selected }}
              onPress={() => onSelectDay(index)}
              style={[styles.day, selected ? styles.daySelected : null]}
            >
              <Text style={[styles.dayWeekday, date === today && !selected ? styles.dayToday : null, selected ? styles.daySelectedSub : null]}>
                {t(`weekdays.${index}`)}
              </Text>
              <Text style={[styles.dayNumber, selected ? styles.daySelectedText : null]}>{Number(date.slice(8, 10))}</Text>
              <Text style={[styles.dayPeople, peopleByDay[index] === 0 ? styles.dayPeopleZero : null, selected ? styles.daySelectedSub : null]}>
                {t("scheduleManagement.dayPeople", { count: peopleByDay[index] ?? 0 })}
              </Text>
            </Pressable>
          );
        })}
      </View>

      <View style={styles.card}>
        <View style={styles.cardHead}>
          <Text style={styles.cardTitle}>{t("scheduleManagement.dayView.staffingTitle", { date: `${t(`weekdays.${dayIndex}`)} ${day.slice(5)}` })}</Text>
          {hasShifts ? <Text style={styles.muted}>{`${clock(staffing.range.start)}–${clock(staffing.range.end)}`}</Text> : null}
        </View>
        <View style={styles.histogram} accessibilityLabel={gapText || t("scheduleManagement.dayView.noGap")}>
          {staffing.hours.map((item) => (
            <View key={item.hour} style={styles.histogramSlot}>
              <View
                style={[
                  styles.histogramBar,
                  { height: `${Math.max(6, (item.count / maxCount) * 100)}%` },
                  item.count <= 1 ? styles.histogramLow : null,
                ]}
              />
            </View>
          ))}
        </View>
        <View style={styles.histogramAxis}>
          <Text style={styles.axisText}>{clock(staffing.range.start).slice(0, 2)}</Text>
          <Text style={styles.axisText}>{clock(staffing.range.end).slice(0, 2)}</Text>
        </View>
        {hasShifts ? (
          <Text style={gapText ? styles.gapText : styles.okText}>
            {gapText ? `⚠ ${gapText}` : t("scheduleManagement.dayView.noGap")}
          </Text>
        ) : (
          <Text style={styles.muted}>{t("scheduleManagement.dayView.empty")}</Text>
        )}
      </View>

      <View style={styles.card}>
        {cells.map(({ row, cell }) => {
          const shiftMinutes = computeScheduleHourStats(cell.schedules).totalMinutes;
          const firstAvailable = cell.availability.find((item) => !item.isUnavailable);
          const sub = cell.kind === "leave"
            ? t("scheduleManagement.cell.leave")
            : cell.kind === "unavailable"
              ? t("scheduleManagement.cell.unavailable")
              : cell.kind === "shift"
                ? t("scheduleManagement.hoursShort", { hours: formatScheduleHours(shiftMinutes) })
                : firstAvailable
                  ? t("scheduleManagement.dayView.available", {
                      range: isAllDayRange(firstAvailable.startTime, firstAvailable.endTime)
                        ? t("availability.allDay")
                        : `${normalizeClockTime(firstAvailable.startTime)}–${normalizeClockTime(firstAvailable.endTime)}`,
                    })
                  : t("scheduleManagement.dayView.rest");
          return (
            <Pressable
              key={row.userGuid}
              accessibilityRole="button"
              accessibilityLabel={`${row.employeeName || row.userGuid} ${sub}`}
              disabled={disabled}
              onPress={() => onOpenCell(row, day, dayIndex, cell)}
              style={({ pressed }) => [styles.row, pressed ? styles.rowPressed : null]}
            >
              <View style={styles.rowName}>
                <Text style={styles.name} numberOfLines={1}>{row.employeeName || row.userGuid}</Text>
                <Text style={styles.sub} numberOfLines={1}>{sub}</Text>
              </View>
              <View style={styles.track}>
                {cell.availability.filter((item) => !item.isUnavailable).map((item) => {
                  const range = isAllDayRange(item.startTime, item.endTime) ? { start: axis.start, end: axis.end } : shiftMinutesRange(item);
                  return range ? <View key={item.availabilityGuid ?? `${item.startTime}`} style={[styles.available, { left: pct(range.start), width: width(range.start, range.end) }]} /> : null;
                })}
                {cell.kind === "unavailable" || cell.kind === "leave" ? (
                  <View style={[styles.blocked, cell.kind === "leave" ? styles.leave : null]}>
                    <PatternFill color={cell.kind === "leave" ? "#D9D6FE" : "#D0D5DD"} variant="diagonal" />
                  </View>
                ) : null}
                {cell.kind === "shift"
                  ? cell.schedules.filter((item) => !item.leaveType).map((item) => {
                      const range = shiftMinutesRange(item);
                      if (!range) return null;
                      const draft = item.status.toLowerCase() === "draft";
                      return (
                        <View
                          key={item.scheduleGuid}
                          style={[styles.shift, draft ? styles.shiftDraft : null, { left: pct(range.start), width: width(range.start, range.end) }]}
                        >
                          <Text style={[styles.shiftText, draft ? styles.shiftDraftText : null]} numberOfLines={1}>
                            {`${normalizeClockTime(item.startTime)}–${normalizeClockTime(item.endTime)}`}
                          </Text>
                        </View>
                      );
                    })
                  : null}
              </View>
            </Pressable>
          );
        })}
        <View style={[styles.row, styles.axisRow]}>
          <View style={styles.rowName} />
          <View style={styles.tickTrack}>
            {ticks.map((tick) => (
              <Text key={tick} style={[styles.tick, { left: pct(tick) }]}>{clock(tick).slice(0, 2)}</Text>
            ))}
          </View>
        </View>
      </View>
      <Text style={[styles.muted, styles.hint]}>{t("scheduleManagement.dayView.tapHint")}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { gap: HB_SPACING.xs },
  muted: { color: HB_COLORS.textSecondary, fontSize: 12 },
  hint: { textAlign: "center" },
  days: { flexDirection: "row", gap: 3 },
  day: { alignItems: "center", borderRadius: 11, flex: 1, paddingVertical: 5 },
  daySelected: { backgroundColor: HB_COLORS.textPrimary },
  dayWeekday: { color: HB_COLORS.textSecondary, fontSize: 11 },
  dayToday: { color: HB_COLORS.action, fontWeight: "700" },
  dayNumber: { color: HB_COLORS.textPrimary, fontSize: 15, fontVariant: ["tabular-nums"], fontWeight: "700" },
  dayPeople: { color: "#98A2B3", fontSize: 10, fontWeight: "700" },
  dayPeopleZero: { color: HB_COLORS.danger },
  daySelectedText: { color: HB_COLORS.white },
  daySelectedSub: { color: "#CFD4DC" },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface + 2,
    borderWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  cardHead: { alignItems: "center", flexDirection: "row", justifyContent: "space-between", marginBottom: 6 },
  cardTitle: { color: HB_COLORS.textPrimary, fontSize: 13.5, fontWeight: "700" },
  histogram: { alignItems: "flex-end", flexDirection: "row", gap: 2, height: 44 },
  histogramSlot: { flex: 1, height: "100%", justifyContent: "flex-end" },
  histogramBar: { backgroundColor: "#B2DDFF", borderTopLeftRadius: 3, borderTopRightRadius: 3 },
  histogramLow: { backgroundColor: "#FDA29B" },
  histogramAxis: { flexDirection: "row", justifyContent: "space-between", marginTop: 3 },
  axisText: { color: "#98A2B3", fontSize: 10, fontVariant: ["tabular-nums"] },
  gapText: { color: HB_COLORS.danger, fontSize: 12, fontWeight: "600", marginTop: 6 },
  okText: { color: HB_COLORS.success, fontSize: 12, fontWeight: "600", marginTop: 6 },
  row: {
    alignItems: "center",
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    paddingVertical: 7,
  },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  axisRow: { paddingVertical: 2 },
  rowName: { width: 82 },
  name: { color: HB_COLORS.textPrimary, fontSize: 12.5, fontWeight: "600" },
  sub: { color: HB_COLORS.textSecondary, fontSize: 10.5 },
  track: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: 6, flex: 1, height: 26, overflow: "hidden", position: "relative" },
  available: { backgroundColor: "#D1FADF", bottom: 0, position: "absolute", top: 0 },
  blocked: { ...StyleSheet.absoluteFillObject, backgroundColor: "#F9FAFB", overflow: "hidden" },
  leave: { backgroundColor: "#F4F3FF" },
  shift: {
    backgroundColor: "#53B1FD",
    borderRadius: 5,
    bottom: 2,
    justifyContent: "center",
    overflow: "hidden",
    paddingHorizontal: 4,
    position: "absolute",
    top: 2,
  },
  shiftDraft: { backgroundColor: HB_COLORS.white, borderColor: "#53B1FD", borderStyle: "dashed", borderWidth: 1.5 },
  shiftText: { color: HB_COLORS.white, fontSize: 10.5, fontVariant: ["tabular-nums"], fontWeight: "700" },
  shiftDraftText: { color: "#175CD3" },
  tickTrack: { flex: 1, height: 14, position: "relative" },
  tick: { color: "#98A2B3", fontSize: 10, marginLeft: -8, position: "absolute", textAlign: "center", width: 16 },
});
