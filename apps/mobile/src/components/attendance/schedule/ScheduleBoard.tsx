// 排班「按天分组」看板（与 Web 周排班看板同一套规则）：
// - list：竖屏，每天一组、组内两列卡片，竖向滚动；
// - columns：横屏全屏，7 天一屏，每天一列。
// 卡片写员工名、一人一色；同一天按「开始时间 → 员工名」排；顶部员工图例点一下高亮该员工。
// 只负责展示：点卡片 / 点「可上班未排」/ 放置模式下点某天，都交回上层用现有逻辑处理。
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { formatScheduleHours, schedulePaidMinutes } from "@/modules/attendance/attendance-my-week";
import { getMealMark, HIDDEN_MEAL_MARK, type MealMark } from "@/modules/attendance/meal-mark";
import { FALLBACK_STAFF_COLOR, type BoardDay, type StaffColor, type StaffLegendItem } from "@/modules/attendance/schedule-board";
import { normalizeClockTime } from "@/modules/attendance/schedule-grid";
import type { PlacementVerdict } from "@/modules/attendance/schedule-placement";
import type { StaffNameMode } from "@/modules/attendance/staff-display-name";
import type { AttendanceSchedule } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PatternFill } from "../approvals/approval-ui";
import { MealMarkBadge } from "./MealMarkBadge";
import { StaffNameModeSwitch } from "./StaffNameModeSwitch";

export interface ScheduleBoardCardFlags {
  /** 与员工上报的可上班 / 不能上班冲突，或未成年用工提醒：卡片时间后加橙色「!」。 */
  warn: boolean;
  /** 有打卡 / 今天以前：不能移动，卡片显示小锁。 */
  locked: boolean;
}

interface ScheduleBoardProps {
  layout: "list" | "columns";
  days: readonly BoardDay[];
  today: string;
  colors: ReadonlyMap<string, StaffColor>;
  /** 看板上显示的名字（昵称 / 全名，由上层按开关算好）。 */
  nameOf: (userGuid: string) => string;
  /** 全名：只给读屏用（卡片 / 图例的无障碍文案），避免读屏只读昵称认不出人；不传就退回 nameOf。 */
  fullNameOf?: (userGuid: string) => string;
  /** 员工名显示方式及切换回调；不传 onChangeNameMode 就不显示开关。 */
  nameMode?: StaffNameMode;
  onChangeNameMode?: (mode: StaffNameMode) => void;
  ageOf: (userGuid: string) => number | undefined;
  flagsOf: (schedule: AttendanceSchedule) => ScheduleBoardCardFlags;
  legend: readonly StaffLegendItem[];
  highlighted?: string;
  onToggleHighlight: (userGuid: string) => void;
  /** 每天「可上班未排」的员工；点提示打开批量排班并预选这天和这些人。 */
  freeByDay: readonly (readonly string[])[];
  onPressFree?: (dayIndex: number) => void;
  onPressCard: (schedule: AttendanceSchedule, dayIndex: number) => void;
  /** 空白日「＋ 排班」。 */
  onPressAdd?: (dayIndex: number) => void;
  /** 放置模式（编辑面板里点了「移到别天 / 复制到」）：每天头部变成目标按钮。 */
  placement?: {
    verdictOf: (dayIndex: number) => PlacementVerdict | undefined;
    onDrop: (dayIndex: number) => void;
  };
  disabled?: boolean;
}

export function ScheduleBoard({
  layout,
  days,
  today,
  colors,
  nameOf,
  fullNameOf,
  nameMode = "nickname",
  onChangeNameMode,
  ageOf,
  flagsOf,
  legend,
  highlighted,
  onToggleHighlight,
  freeByDay,
  onPressFree,
  onPressCard,
  onPressAdd,
  placement,
  disabled,
}: ScheduleBoardProps) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const columns = layout === "columns";
  const hoursShort = (minutes: number) => t("scheduleManagement.hoursShort", { hours: formatScheduleHours(minutes) });

  // 图例行：最左边是「员工名」昵称 / 全名开关（固定不滚动，始终点得到），右边是可横向滚动的员工图例
  const renderLegend = () => (
    <View style={styles.legendRow}>
      {onChangeNameMode ? (
        <StaffNameModeSwitch mode={nameMode} onChange={onChangeNameMode} compact={columns} />
      ) : null}
      <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.legendScroll} contentContainerStyle={styles.legend}>
      {legend.map((item) => {
        const color = colors.get(item.userGuid) ?? FALLBACK_STAFF_COLOR;
        const selected = highlighted === item.userGuid;
        const dimmed = Boolean(highlighted) && !selected;
        return (
          <Pressable
            key={item.userGuid}
            accessibilityRole="button"
            accessibilityState={{ selected }}
            accessibilityLabel={t("scheduleManagement.board.legendA11y", { name: fullNameOf?.(item.userGuid) ?? item.name })}
            onPress={() => onToggleHighlight(item.userGuid)}
            style={[
              styles.legendChip,
              columns ? styles.legendChipCompact : null,
              { backgroundColor: color.bg, borderColor: selected ? color.bar : "transparent" },
              dimmed ? styles.dimmed : null,
            ]}
          >
            <View style={[styles.legendDot, { backgroundColor: color.bar }]} />
            <Text style={[styles.legendName, { color: color.ink }]} numberOfLines={1}>
              {item.name}
            </Text>
            <Text style={styles.legendMeta}>
              {t("scheduleManagement.board.legendMeta", { hours: formatScheduleHours(item.paidMinutes), count: item.shifts })}
            </Text>
          </Pressable>
        );
      })}
      </ScrollView>
    </View>
  );

  // 请假卡片没有午饭；其余班次按用餐规则判定（已取消 / 短班次 / 时间不合法为 hidden）
  const mealMarkOf = (schedule: AttendanceSchedule): MealMark => (
    schedule.leaveType
      ? HIDDEN_MEAL_MARK
      : getMealMark({
        startTime: schedule.startTime,
        endTime: schedule.endTime,
        mealBreakCount: schedule.mealBreakCount,
        status: schedule.status,
      })
  );
  const showMealCaption = days.some((day) => day.shifts.some((schedule) => mealMarkOf(schedule).kind !== "hidden"));

  const renderCard = (schedule: AttendanceSchedule, dayIndex: number) => {
    const name = nameOf(schedule.userGuid);
    const dimmed = Boolean(highlighted) && highlighted !== schedule.userGuid;
    if (schedule.leaveType) {
      return (
        <Pressable
          key={schedule.scheduleGuid}
          accessibilityRole="button"
          disabled={disabled}
          onPress={() => onPressCard(schedule, dayIndex)}
          style={[styles.card, columns ? styles.cardColumn : styles.cardList, styles.leaveCard, dimmed ? styles.dimmed : null]}
        >
          <View style={[styles.cardBar, { backgroundColor: HB_COLORS.outline }]} />
          <Text style={[styles.cardName, styles.leaveText]} numberOfLines={1}>{name}</Text>
          <Text style={[styles.cardTime, styles.leaveText]} numberOfLines={1}>
            {t(`leaveTypes.${schedule.leaveType}`, t("scheduleManagement.cell.leave"))}
          </Text>
        </Pressable>
      );
    }
    const color = colors.get(schedule.userGuid) ?? FALLBACK_STAFF_COLOR;
    const isDraft = schedule.status.toLowerCase() === "draft";
    const flags = flagsOf(schedule);
    const age = ageOf(schedule.userGuid);
    // 排班计薪时长（扣用餐）；注意不是 schedule.paidMinutes——那是按打卡算出的实际计薪
    const minutes = schedulePaidMinutes(schedule);
    // 有无午饭标识：名字行右侧；读屏标签末尾补上「含午饭 N 次 / 无午饭」
    const mark = mealMarkOf(schedule);
    const mealA11y = mark.kind === "meal"
      ? t("scheduleManagement.board.mealMark.meal", { count: mark.count })
      : mark.kind === "none"
        ? t("scheduleManagement.board.mealMark.none")
        : "";
    return (
      <Pressable
        key={schedule.scheduleGuid}
        accessibilityRole="button"
        accessibilityLabel={`${fullNameOf?.(schedule.userGuid) ?? name} ${normalizeClockTime(schedule.startTime)}–${normalizeClockTime(schedule.endTime)}${mealA11y ? `，${mealA11y}` : ""}`}
        disabled={disabled}
        onPress={() => onPressCard(schedule, dayIndex)}
        style={[
          styles.card,
          columns ? styles.cardColumn : styles.cardList,
          { backgroundColor: color.bg, borderColor: isDraft ? color.bar : `${color.bar}55` },
          isDraft ? styles.cardDraft : null,
          dimmed ? styles.dimmed : null,
        ]}
      >
        {/* 草稿：斜纹 + 虚线框，和 Web 一样一眼区分未发布 */}
        {isDraft ? <PatternFill color="rgba(255,255,255,0.55)" variant="diagonal" /> : null}
        <View style={[styles.cardBar, { backgroundColor: color.bar }]} />
        <View style={styles.cardNameRow}>
          <Text style={[styles.cardName, columns ? styles.cardNameCompact : null, { color: color.ink }]} numberOfLines={1}>
            {name}
          </Text>
          {age !== undefined && !columns ? <Text style={styles.ageBadge}>{age}</Text> : null}
          <MealMarkBadge mark={mark} tint={color.ink} />
        </View>
        <View style={styles.cardTimeRow}>
          <Text style={[styles.cardTime, columns ? styles.cardTimeCompact : null]} numberOfLines={1}>
            {`${normalizeClockTime(schedule.startTime)}–${normalizeClockTime(schedule.endTime)}`}
            {flags.warn ? <Text style={styles.warnMark}> !</Text> : null}
            {flags.locked ? <Text style={styles.lockMark}> 🔒</Text> : null}
          </Text>
          {!columns ? <Text style={styles.cardHours}>{hoursShort(minutes)}</Text> : null}
        </View>
      </Pressable>
    );
  };

  const renderDayHeader = (day: BoardDay, index: number) => {
    const isToday = day.date === today;
    const verdict = placement?.verdictOf(index);
    const stat = day.shifts.length
      ? t("scheduleManagement.board.dayStat", { people: day.people, hours: formatScheduleHours(day.paidMinutes) })
      : t("scheduleManagement.board.dayEmpty");
    const head = (
      <View style={[styles.dayHead, columns ? styles.dayHeadColumn : null, isToday ? styles.dayHeadToday : null]}>
        <View style={styles.dayTitleRow}>
          <Text style={[styles.dayTitle, isToday ? styles.todayText : null]} numberOfLines={1}>
            {columns
              ? `${day.date.slice(5).replace("-", "/")} ${t(`weekdays.${index}`)}`
              : `${t(`weekdays.${index}`)} ${day.date.slice(5).replace("-", "/")}`}
          </Text>
          {isToday && !columns ? <Text style={styles.todayTag}>{t("scheduleManagement.board.today")}</Text> : null}
        </View>
        <Text style={styles.dayStat} numberOfLines={1}>{stat}</Text>
      </View>
    );
    if (!placement || !verdict) return head;
    // 放置模式：头部下面加一条目标按钮，颜色表示能不能放（noop＝原位置）
    const tone = verdict.state === "bad" ? styles.dropBad : verdict.state === "warn" ? styles.dropWarn : verdict.state === "noop" ? styles.dropNoop : styles.dropOk;
    return (
      <View>
        {head}
        <Pressable
          accessibilityRole="button"
          disabled={verdict.state === "noop"}
          onPress={() => placement.onDrop(index)}
          style={[styles.dropTarget, tone]}
        >
          <Text style={styles.dropText} numberOfLines={1}>
            {t(`scheduleManagement.board.drop.${verdict.state}`)}
          </Text>
        </Pressable>
      </View>
    );
  };

  const renderDay = (day: BoardDay, index: number) => {
    const free = freeByDay[index] ?? [];
    return (
      <View key={day.date} style={columns ? styles.column : styles.dayGroup}>
        {renderDayHeader(day, index)}
        <View style={columns ? styles.columnCards : styles.listCards}>
          {day.shifts.map((schedule) => renderCard(schedule, index))}
        </View>
        {!day.shifts.length && onPressAdd && !placement ? (
          <Pressable accessibilityRole="button" disabled={disabled} onPress={() => onPressAdd(index)} style={styles.addRow}>
            <Text style={styles.addText}>{t("scheduleManagement.board.add")}</Text>
          </Pressable>
        ) : null}
        {free.length && !placement ? (
          <Pressable
            accessibilityRole="button"
            disabled={disabled || !onPressFree}
            onPress={() => onPressFree?.(index)}
            style={styles.freeRow}
          >
            <View style={styles.freeDot} />
            <Text style={styles.freeText} numberOfLines={1}>
              {t("scheduleManagement.board.availableUnscheduled", { count: free.length })}
            </Text>
            {onPressFree && !columns ? <Text style={styles.freeAction}>{t("scheduleManagement.board.schedule")}</Text> : null}
          </Pressable>
        ) : null}
      </View>
    );
  };

  return (
    <View style={columns ? styles.rootColumns : styles.rootList}>
      {legend.length ? renderLegend() : null}
      {showMealCaption ? (
        <Text style={styles.mealCaption} numberOfLines={1}>{t("scheduleManagement.board.mealMark.caption")}</Text>
      ) : null}
      {columns ? (
        <View style={styles.columnGrid}>{days.map(renderDay)}</View>
      ) : (
        days.map(renderDay)
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  rootList: { gap: HB_SPACING.xs },
  rootColumns: { gap: 6 },
  legendRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  legendScroll: { flex: 1 },
  legend: { gap: 6, paddingVertical: 2, paddingRight: HB_SPACING.xs },
  legendChip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    borderRadius: 14,
    borderWidth: 1.5,
    paddingHorizontal: 9,
    paddingVertical: 4,
  },
  legendChipCompact: { paddingVertical: 2, paddingHorizontal: 7 },
  legendDot: { width: 8, height: 8, borderRadius: 4 },
  legendName: { fontSize: 12.5, fontWeight: "600", maxWidth: 120 },
  legendMeta: { fontSize: 11, color: HB_COLORS.textSecondary },
  mealCaption: { fontSize: 12, color: HB_COLORS.textSecondary },
  dimmed: { opacity: 0.32 },
  dayGroup: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    overflow: "hidden",
  },
  dayHead: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    paddingHorizontal: 10,
    paddingVertical: 8,
    backgroundColor: HB_COLORS.surface,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  dayHeadColumn: { flexDirection: "column", alignItems: "flex-start", gap: 0, paddingHorizontal: 6, paddingVertical: 4 },
  dayHeadToday: { backgroundColor: "#EFF8FF" },
  dayTitleRow: { flexDirection: "row", alignItems: "center", gap: 6, flexShrink: 1 },
  dayTitle: { fontSize: 13.5, fontWeight: "700", color: HB_COLORS.textPrimary },
  todayText: { color: HB_COLORS.brand },
  todayTag: {
    fontSize: 10,
    fontWeight: "700",
    color: HB_COLORS.white,
    backgroundColor: HB_COLORS.brand,
    borderRadius: 4,
    paddingHorizontal: 4,
    overflow: "hidden",
  },
  dayStat: { fontSize: 11.5, color: HB_COLORS.textSecondary },
  listCards: { flexDirection: "row", flexWrap: "wrap", gap: 6, padding: 7 },
  columnCards: { gap: 4, padding: 4 },
  card: {
    position: "relative",
    borderRadius: 8,
    borderWidth: 1,
    paddingLeft: 11,
    paddingRight: 7,
    paddingVertical: 5,
    overflow: "hidden",
  },
  cardList: { width: "48.8%", minHeight: 46 },
  cardColumn: { paddingLeft: 8, paddingRight: 4, paddingVertical: 3 },
  cardDraft: { borderStyle: "dashed" },
  cardBar: { position: "absolute", left: 3, top: 5, bottom: 5, width: 3, borderRadius: 2 },
  cardNameRow: { flexDirection: "row", alignItems: "center", gap: 4 },
  cardName: { fontSize: 12.5, fontWeight: "700", flexShrink: 1 },
  cardNameCompact: { fontSize: 11 },
  ageBadge: {
    fontSize: 9.5,
    fontWeight: "700",
    color: HB_COLORS.warning,
    backgroundColor: "#FFFAEB",
    borderColor: "#FEDF89",
    borderWidth: 1,
    borderRadius: 3,
    paddingHorizontal: 3,
    overflow: "hidden",
  },
  cardTimeRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", marginTop: 1 },
  cardTime: { fontSize: 11.5, color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], flexShrink: 1 },
  cardTimeCompact: { fontSize: 10 },
  cardHours: { fontSize: 10.5, color: HB_COLORS.textSecondary },
  warnMark: { color: HB_COLORS.warning, fontWeight: "800" },
  lockMark: { fontSize: 9 },
  leaveCard: { backgroundColor: HB_COLORS.surfaceMuted, borderColor: HB_COLORS.outline },
  leaveText: { color: HB_COLORS.textSecondary },
  addRow: { paddingHorizontal: 10, paddingBottom: 8, paddingTop: 2 },
  addText: { fontSize: 12, color: HB_COLORS.brand, fontWeight: "600" },
  freeRow: { flexDirection: "row", alignItems: "center", gap: 6, paddingHorizontal: 10, paddingBottom: 8 },
  freeDot: { width: 5, height: 5, borderRadius: 3, backgroundColor: HB_COLORS.success },
  freeText: { flex: 1, fontSize: 11.5, color: HB_COLORS.success },
  freeAction: { fontSize: 12, color: HB_COLORS.brand, fontWeight: "700" },
  dropTarget: { marginHorizontal: 6, marginTop: 6, borderRadius: 8, borderWidth: 1.5, borderStyle: "dashed", paddingVertical: 6, alignItems: "center" },
  dropOk: { borderColor: HB_COLORS.brand, backgroundColor: "#EFF8FF" },
  dropWarn: { borderColor: HB_COLORS.warning, backgroundColor: "#FFFAEB" },
  dropBad: { borderColor: HB_COLORS.danger, backgroundColor: "#FEF3F2" },
  dropNoop: { borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.surfaceMuted },
  dropText: { fontSize: 11.5, fontWeight: "700", color: HB_COLORS.textPrimary },
  columnGrid: {
    flexDirection: "row",
    backgroundColor: HB_COLORS.white,
    borderRadius: 10,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    overflow: "hidden",
  },
  column: { flex: 1, minWidth: 0, borderRightWidth: StyleSheet.hairlineWidth, borderRightColor: HB_COLORS.outlineMuted },
});
