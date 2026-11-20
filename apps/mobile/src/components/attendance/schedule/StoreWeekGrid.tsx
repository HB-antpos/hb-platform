// 整店整周排班 · 表格视图：人 × 周，一屏看全。
// 我的一行置顶（蓝色左边条），今天这一列表头蓝底；格子里只写起止小时（9–17），不写工时；点格子看当天在岗，点名字看他的一周。
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import {
  colorOf,
  gridCellLabel,
  type StoreWeekDay,
  type StoreWeekGridRow,
} from "@/modules/attendance/store-week";
import type { StaffColor } from "@/modules/attendance/schedule-board";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";

const ACTION_BLUE = "#0958D9";
const NAME_WIDTH = 58;

/** 较长的写法（10–15:30）拆成两行，避免在窄屏被截断。 */
function CellText({ label, color }: { label: string; color: string }) {
  const [first, second] = label.length > 5 ? label.split("–") : [label, undefined];
  return (
    <Text style={[styles.cellText, { color }]} numberOfLines={2}>
      {second === undefined ? first : `${first}–\n${second}`}
    </Text>
  );
}

export function StoreWeekGrid({
  rows,
  days,
  today,
  colors,
  onOpenDay,
  onOpenPerson,
}: {
  rows: StoreWeekGridRow[];
  days: StoreWeekDay[];
  today: string;
  colors: Map<string, StaffColor>;
  onOpenDay: (date: string) => void;
  onOpenPerson: (userGuid: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <View style={styles.grid}>
      <View style={[styles.row, styles.headRow]}>
        <View style={[styles.nameCell, styles.headCell]}>
          <Text style={styles.headText}>{t("storeWeek.colleagues")}</Text>
        </View>
        {days.map((day, index) => {
          const isToday = day.date === today;
          return (
            <Pressable
              key={day.date}
              accessibilityRole="button"
              accessibilityLabel={`${t(`weekdays.${index}`)} ${day.date.slice(5)} ${t("storeWeek.peopleOnDuty", { count: day.people })}`}
              onPress={() => onOpenDay(day.date)}
              style={[styles.dayCell, styles.headCell, isToday ? styles.todayHead : null]}
            >
              <Text style={[styles.headText, isToday ? styles.onBlueSoft : null]}>{t(`myAttendance.weekdayShort.${index}`)}</Text>
              <Text style={[styles.headDay, isToday ? styles.onBlue : null]}>{Number(day.date.slice(8, 10))}</Text>
            </Pressable>
          );
        })}
      </View>
      {rows.map((row) => {
        const color = colorOf(colors, row.userGuid);
        return (
          <View key={row.userGuid} style={[styles.row, row.isMine ? styles.mineRow : null]}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("storeWeek.personSheet.title", { name: row.name, count: row.shiftCount })}
              onPress={() => onOpenPerson(row.userGuid)}
              style={[styles.nameCell, row.isMine ? styles.mineName : null]}
            >
              <View style={styles.nameLine}>
                <Text style={styles.name} numberOfLines={1}>{row.name}</Text>
                {row.isMine ? (
                  <View style={styles.meTag}><Text style={styles.meText}>{t("storeWeek.me")}</Text></View>
                ) : null}
              </View>
              <Text style={styles.count}>{t("storeWeek.shiftCount", { count: row.shiftCount })}</Text>
            </Pressable>
            {row.cells.map((cell, index) => {
              const day = days[index];
              const isToday = day.date === today;
              return (
                <Pressable
                  key={day.date}
                  accessibilityRole="button"
                  accessibilityLabel={`${row.name} ${t(`weekdays.${index}`)} ${cell.length ? gridCellLabel(cell) : t("storeWeek.personSheet.rest")}`}
                  onPress={() => onOpenDay(day.date)}
                  style={[styles.dayCell, styles.bodyCell, isToday ? styles.todayCol : null]}
                >
                  {cell.length > 0 ? (
                    <View style={[styles.chip, { backgroundColor: color.bg, borderLeftColor: color.bar }]}>
                      <CellText label={gridCellLabel(cell)} color={color.ink} />
                    </View>
                  ) : (
                    <Text style={styles.dash}>–</Text>
                  )}
                </Pressable>
              );
            })}
          </View>
        );
      })}
      <View style={[styles.row, styles.footRow]}>
        <View style={styles.nameCell}><Text style={styles.footText}>{t("storeWeek.onDuty")}</Text></View>
        {days.map((day) => (
          <View key={day.date} style={[styles.dayCell, styles.footCell, day.date === today ? styles.todayCol : null]}>
            <Text style={styles.footText}>{day.people}</Text>
          </View>
        ))}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  grid: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.sheet - 2,
    borderWidth: 1,
    overflow: "hidden",
  },
  row: { borderBottomColor: HB_COLORS.outlineMuted, borderBottomWidth: StyleSheet.hairlineWidth, flexDirection: "row" },
  headRow: { backgroundColor: "#FAFBFC" },
  mineRow: { backgroundColor: "#F5FAFF" },
  footRow: { backgroundColor: "#FAFBFC", borderBottomWidth: 0 },
  nameCell: { justifyContent: "center", paddingHorizontal: 5, width: NAME_WIDTH },
  mineName: { borderLeftColor: ACTION_BLUE, borderLeftWidth: 3 },
  dayCell: { alignItems: "center", flex: 1, justifyContent: "center" },
  headCell: { paddingVertical: 6 },
  todayHead: { backgroundColor: ACTION_BLUE },
  todayCol: { backgroundColor: "rgba(22,119,255,0.05)" },
  headText: { color: HB_COLORS.textSecondary, fontSize: 10.5, fontWeight: "600" },
  headDay: { color: HB_COLORS.textPrimary, fontSize: 13, fontVariant: ["tabular-nums"], fontWeight: "700" },
  onBlue: { color: HB_COLORS.white },
  onBlueSoft: { color: "#D1E9FF" },
  bodyCell: { height: 46, padding: 3 },
  nameLine: { alignItems: "center", flexDirection: "row", gap: 3 },
  name: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 12, fontWeight: "700" },
  count: { color: "#98A2B3", fontSize: 10 },
  meTag: { backgroundColor: HB_COLORS.textPrimary, borderRadius: 4, paddingHorizontal: 4 },
  meText: { color: HB_COLORS.white, fontSize: 9, fontWeight: "800", lineHeight: 14 },
  chip: {
    alignItems: "center",
    alignSelf: "stretch",
    borderLeftWidth: 3,
    borderRadius: 6,
    flex: 1,
    justifyContent: "center",
  },
  cellText: { fontSize: 9.5, fontVariant: ["tabular-nums"], fontWeight: "800", letterSpacing: -0.2, lineHeight: 11, textAlign: "center" },
  dash: { color: "#D0D5DD", fontSize: 12 },
  footCell: { height: 30 },
  footText: { color: HB_COLORS.textSecondary, fontSize: 11, fontWeight: "700", paddingHorizontal: 5 },
});
