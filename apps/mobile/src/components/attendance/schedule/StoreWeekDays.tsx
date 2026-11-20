// 整店整周排班 · 按天视图：一天一张卡，卡内一人一行（色条 + 姓名 + 起止时间 + 当天时间轴）。
// 我的班加描边与「我」标记；点整张卡弹出当天的在岗详情。样式沿用「我的排班」的日卡（日期块、今天蓝边）。
import { Pressable, StyleSheet, View, type DimensionValue } from "react-native";
import { Text } from "react-native-paper";
import {
  colorOf,
  formatShiftRange,
  trackPosition,
  type DayAxis,
  type StoreWeekDay,
} from "@/modules/attendance/store-week";
import type { StaffColor } from "@/modules/attendance/schedule-board";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

const ACTION_BLUE = "#0958D9";

export function StoreWeekDays({
  days,
  today,
  axis,
  colors,
  onOpenDay,
}: {
  days: StoreWeekDay[];
  today: string;
  /** 全周共用一条时间轴，不同天的班次条长度才能互相比较。 */
  axis: DayAxis;
  colors: Map<string, StaffColor>;
  onOpenDay: (date: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <View style={styles.root}>
      {days.map((day, index) => {
        const isToday = day.date === today;
        return (
          <Pressable
            key={day.date}
            accessibilityRole="button"
            accessibilityLabel={`${t(`weekdays.${index}`)} ${day.date.slice(5)} ${t("storeWeek.peopleOnDuty", { count: day.people })}`}
            onPress={() => onOpenDay(day.date)}
            style={({ pressed }) => [styles.card, isToday ? styles.cardToday : null, pressed ? styles.pressed : null]}
          >
            <View style={[styles.dateBlock, isToday ? styles.dateBlockToday : null]}>
              <Text style={[styles.dateWeekday, isToday ? styles.onBlue : null]}>{t(`weekdays.${index}`)}</Text>
              <Text style={[styles.dateDay, isToday ? styles.onBlue : null]}>{Number(day.date.slice(8, 10))}</Text>
            </View>
            <View style={styles.body}>
              <View style={styles.head}>
                <Text style={styles.headTitle}>
                  {isToday ? `${t("storeWeek.today")} · ` : ""}
                  {t("storeWeek.peopleOnDuty", { count: day.people })}
                </Text>
                {day.span ? <Text style={styles.span}>{`${day.span.start}–${day.span.end}`}</Text> : null}
              </View>
              {day.shifts.length === 0 ? (
                <Text style={styles.empty}>{t("storeWeek.daySheet.empty")}</Text>
              ) : (
                day.shifts.map((shift, shiftIndex) => {
                  const color = colorOf(colors, shift.userGuid);
                  const position = trackPosition(shift, axis);
                  return (
                    <View
                      key={`${shift.userGuid}-${shift.startTime}-${shiftIndex}`}
                      style={[styles.shift, { backgroundColor: color.bg }, shift.isMine ? { borderColor: color.bar, borderWidth: 1.5 } : null]}
                    >
                      <View style={[styles.bar, { backgroundColor: color.bar }]} />
                      <View style={styles.line}>
                        <View style={styles.nameRow}>
                          <Text style={[styles.name, { color: color.ink }]} numberOfLines={1}>{shift.name}</Text>
                          {shift.isMine ? (
                            <View style={styles.meTag}><Text style={styles.meText}>{t("storeWeek.me")}</Text></View>
                          ) : null}
                        </View>
                        <Text style={[styles.time, { color: color.ink }]}>{formatShiftRange(shift)}</Text>
                      </View>
                      <View style={styles.track}>
                        <View
                          style={[
                            styles.fill,
                            { backgroundColor: color.bar, left: `${position.left}%` as DimensionValue, width: `${position.width}%` as DimensionValue },
                          ]}
                        />
                      </View>
                    </View>
                  );
                })
              )}
            </View>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { gap: HB_SPACING.xs },
  pressed: { opacity: 0.85 },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 14,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    padding: 10,
  },
  cardToday: { borderColor: "#84CAFF", borderWidth: 1.5 },
  dateBlock: {
    alignItems: "center",
    alignSelf: "stretch",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: 10,
    justifyContent: "center",
    minHeight: 52,
    width: 44,
  },
  dateBlockToday: { backgroundColor: ACTION_BLUE },
  dateWeekday: { color: HB_COLORS.textSecondary, fontSize: 11 },
  dateDay: { color: HB_COLORS.textPrimary, fontSize: 18, fontVariant: ["tabular-nums"], fontWeight: "700" },
  onBlue: { color: HB_COLORS.white },
  body: { flex: 1, gap: 6, minWidth: 0 },
  head: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  headTitle: { color: HB_COLORS.textPrimary, fontSize: 12.5, fontWeight: "700" },
  span: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: 999,
    color: HB_COLORS.textSecondary,
    fontSize: 11.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
    overflow: "hidden",
    paddingHorizontal: 8,
    paddingVertical: 1,
  },
  empty: { color: "#98A2B3", fontSize: 13 },
  shift: { borderRadius: 8, gap: 3, paddingLeft: 9, paddingRight: 7, paddingVertical: 5, position: "relative" },
  bar: { borderRadius: 3, bottom: 5, left: 0, position: "absolute", top: 5, width: 4 },
  line: { alignItems: "center", flexDirection: "row", justifyContent: "space-between", gap: 8 },
  nameRow: { alignItems: "center", flexDirection: "row", flexShrink: 1, gap: 5 },
  name: { fontSize: 13, fontWeight: "700", flexShrink: 1 },
  time: { fontSize: 13, fontVariant: ["tabular-nums"], fontWeight: "700" },
  meTag: { backgroundColor: HB_COLORS.textPrimary, borderRadius: 4, paddingHorizontal: 5 },
  meText: { color: HB_COLORS.white, fontSize: 10, fontWeight: "800", lineHeight: 15 },
  track: { backgroundColor: "rgba(16,24,40,0.07)", borderRadius: 3, height: 5, overflow: "hidden", position: "relative" },
  fill: { borderRadius: 3, bottom: 0, position: "absolute", top: 0 },
});
