// 整店整周排班的两个只读弹层：
// - 当天在岗：按时间轴并排每个人的班，我的班上方用斜纹标出「和我同时在岗」的时段；
// - 某位同事本周：一行一天，休息的日子也列出来。
// 只写班次时间，不写工时、请假类型、备注（后端本来也不下发）。
import { StyleSheet, View, type DimensionValue } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { formatScheduleHours } from "@/modules/attendance/attendance-my-week";
import type { StaffColor } from "@/modules/attendance/schedule-board";
import {
  axisTicks,
  colorOf,
  overlapRangesWithMine,
  overlapWithMine,
  shortShiftRange,
  trackPosition,
  type DayAxis,
  type StoreWeekDay,
  type StoreWeekShift,
} from "@/modules/attendance/store-week";
import { clauseSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

const BRAND = "#1677FF";

function Axis({ axis, inset }: { axis: DayAxis; inset: number }) {
  return (
    <View style={[styles.axis, { marginLeft: inset }]}>
      {axisTicks(axis).map((tick) => (
        <Text key={tick.minutes} style={[styles.axisTick, { left: `${tick.percent}%` as DimensionValue }]}>{tick.label}</Text>
      ))}
    </View>
  );
}

function ShiftBar({
  shift,
  axis,
  color,
  overlaps,
}: {
  shift: StoreWeekShift;
  axis: DayAxis;
  color: StaffColor;
  overlaps?: { start: number; end: number }[];
}) {
  const position = trackPosition(shift, axis);
  const span = axis.end - axis.start;
  return (
    <View style={styles.bar}>
      {overlaps?.map((range) => (
        <View
          key={`${range.start}-${range.end}`}
          style={[
            styles.overlap,
            {
              left: `${Math.max(0, ((range.start - axis.start) / span) * 100)}%` as DimensionValue,
              width: `${Math.max(1, ((range.end - range.start) / span) * 100)}%` as DimensionValue,
            },
          ]}
        />
      ))}
      <View
        style={[
          styles.barFill,
          { backgroundColor: color.bar, left: `${position.left}%` as DimensionValue, width: `${position.width}%` as DimensionValue },
        ]}
      >
        <Text style={styles.barText} numberOfLines={1}>{shortShiftRange(shift)}</Text>
      </View>
    </View>
  );
}

/** 当天在岗弹层。 */
export function StoreWeekDaySheet({
  day,
  weekdayIndex,
  isToday,
  axis,
  colors,
  onDismiss,
  onViewMine,
}: {
  day?: StoreWeekDay;
  weekdayIndex: number;
  isToday: boolean;
  axis: DayAxis;
  colors: Map<string, StaffColor>;
  onDismiss: () => void;
  onViewMine: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const mine = day?.shifts.filter((shift) => shift.isMine) ?? [];
  const title = day
    ? t(isToday ? "storeWeek.daySheet.titleToday" : "storeWeek.daySheet.title", {
        weekday: t(`weekdays.${weekdayIndex}`),
        date: day.date.slice(5).replace("-", "/"),
        count: day.people,
      })
    : "";
  // 副标题：你几点到几点；与哪几位同事同时在岗多少小时（只有同事的班时间，没有任何工时 / 请假信息）。
  const subtitle = (() => {
    if (!day) return undefined;
    if (mine.length === 0) return t("storeWeek.daySheet.noMine");
    const parts = day.shifts
      .map((shift) => ({ shift, minutes: overlapWithMine(shift, mine) }))
      .filter((item) => item.minutes > 0)
      .map((item) => t("storeWeek.daySheet.overlap", { name: item.shift.name, hours: formatScheduleHours(item.minutes) }));
    return [t("storeWeek.daySheet.mine", { range: mine.map(shortShiftRange).join(" + ") }), ...parts].join(clauseSeparator());
  })();

  return (
    <BusinessSheet
      visible={Boolean(day)}
      title={title}
      subtitle={subtitle}
      onDismiss={onDismiss}
      footer={(
        <View style={styles.footer}>
          <Button mode="outlined" style={styles.footerButton} onPress={onDismiss}>{t("storeWeek.daySheet.close")}</Button>
          {mine.length > 0 ? (
            <Button mode="contained" style={styles.footerButton} onPress={onViewMine}>{t("storeWeek.daySheet.viewMine")}</Button>
          ) : null}
        </View>
      )}
    >
      {day && day.shifts.length > 0 ? (
        <View style={styles.list}>
          <Axis axis={axis} inset={NAME_INSET} />
          {day.shifts.map((shift, index) => (
            <View key={`${shift.userGuid}-${shift.startTime}-${index}`} style={styles.rowLine}>
              <View style={styles.who}>
                <Text style={styles.whoText} numberOfLines={1}>{shift.name}</Text>
                {shift.isMine ? <View style={styles.meTag}><Text style={styles.meText}>{t("storeWeek.me")}</Text></View> : null}
              </View>
              <ShiftBar
                shift={shift}
                axis={axis}
                color={colorOf(colors, shift.userGuid)}
                overlaps={overlapRangesWithMine(shift, mine)}
              />
            </View>
          ))}
        </View>
      ) : (
        <Text style={styles.empty}>{t("storeWeek.daySheet.empty")}</Text>
      )}
    </BusinessSheet>
  );
}

/** 某位同事本周的班。 */
export function StoreWeekPersonSheet({
  name,
  userGuid,
  days,
  today,
  axis,
  colors,
  onDismiss,
}: {
  name?: string;
  userGuid?: string;
  days: StoreWeekDay[];
  today: string;
  axis: DayAxis;
  colors: Map<string, StaffColor>;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const rows = userGuid
    ? days.map((day) => ({ day, shifts: day.shifts.filter((shift) => shift.userGuid === userGuid) }))
    : [];
  const count = rows.reduce((sum, row) => sum + row.shifts.length, 0);
  return (
    <BusinessSheet
      visible={Boolean(userGuid)}
      title={t("storeWeek.personSheet.title", { name: name ?? "", count })}
      onDismiss={onDismiss}
      footer={(
        <View style={styles.footer}>
          <Button mode="contained" style={styles.footerButton} onPress={onDismiss}>{t("storeWeek.personSheet.close")}</Button>
        </View>
      )}
    >
      <View style={styles.list}>
        <Axis axis={axis} inset={DAY_INSET} />
        {rows.map(({ day, shifts }, index) => (
          <View key={day.date} style={styles.rowLine}>
            <Text style={[styles.dayLabel, day.date === today ? styles.dayLabelToday : null]}>
              {`${t(`weekdays.${index}`)} ${Number(day.date.slice(8, 10))}`}
            </Text>
            {shifts.length === 0 ? (
              <Text style={styles.rest}>{t("storeWeek.personSheet.rest")}</Text>
            ) : (
              <View style={styles.multi}>
                {shifts.map((shift, shiftIndex) => (
                  <ShiftBar key={`${shift.startTime}-${shiftIndex}`} shift={shift} axis={axis} color={colorOf(colors, shift.userGuid)} />
                ))}
              </View>
            )}
          </View>
        ))}
      </View>
    </BusinessSheet>
  );
}

// 姓名列 / 星期列的宽度（含与轴之间的间距），刻度据此对齐班次条。
const NAME_INSET = 74;
const DAY_INSET = 82;

const styles = StyleSheet.create({
  list: { gap: 7, paddingBottom: 4 },
  axis: { height: 14, position: "relative" },
  axisTick: { color: "#98A2B3", fontSize: 10, fontVariant: ["tabular-nums"], position: "absolute", transform: [{ translateX: -8 }] },
  rowLine: { alignItems: "center", flexDirection: "row", gap: 8 },
  who: { alignItems: "center", flexDirection: "row", gap: 4, width: NAME_INSET - 8 },
  whoText: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 13, fontWeight: "700" },
  meTag: { backgroundColor: HB_COLORS.textPrimary, borderRadius: 4, paddingHorizontal: 5 },
  meText: { color: HB_COLORS.white, fontSize: 10, fontWeight: "800", lineHeight: 15 },
  dayLabel: { color: HB_COLORS.textPrimary, fontSize: 13, fontVariant: ["tabular-nums"], width: DAY_INSET - 8 },
  dayLabelToday: { color: "#0958D9", fontWeight: "800" },
  rest: { color: "#98A2B3", fontSize: 12.5 },
  multi: { flex: 1, gap: 4 },
  bar: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: 6, flex: 1, height: 24, overflow: "hidden", position: "relative" },
  barFill: { alignItems: "flex-start", borderRadius: 4, bottom: 3, justifyContent: "center", position: "absolute", top: 3 },
  barText: { color: HB_COLORS.white, fontSize: 10, fontVariant: ["tabular-nums"], fontWeight: "800", paddingLeft: 6 },
  overlap: {
    backgroundColor: "rgba(22,119,255,0.16)",
    borderColor: BRAND,
    borderLeftWidth: 1.5,
    borderRightWidth: 1.5,
    bottom: 0,
    position: "absolute",
    top: 0,
  },
  empty: { color: "#98A2B3", fontSize: 13, paddingVertical: 12, textAlign: "center" },
  footer: { flexDirection: "row", gap: 8 },
  footerButton: { flex: 1 },
});
