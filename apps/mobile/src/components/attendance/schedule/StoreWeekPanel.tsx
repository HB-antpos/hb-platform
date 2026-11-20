// 员工「我的排班」页签里的「整店排班」：只读查看自己关联分店这一周所有同事的已发布班次。
// 状态：加载中 / 加载失败（保留上次内容 + 重试）/ 没有关联分店 / 店长还没发布（未来周引导去填可上班时间）/ 正常（按天或表格）。
import { useMemo, useState, type ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { buildWeekDates } from "@/modules/attendance/attendance-my-week";
import {
  buildDayAxis,
  buildStoreWeekGrid,
  groupStoreWeekDays,
  legendPeople,
  pickStoreWeek,
  staffColorMap,
  summarizeStoreWeek,
  type StoreWeek,
} from "@/modules/attendance/store-week";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { WeekNavBar, weekTagKey } from "../WeekNavBar";
import { StoreWeekDays } from "./StoreWeekDays";
import { StoreWeekGrid } from "./StoreWeekGrid";
import { StoreWeekDaySheet, StoreWeekPersonSheet } from "./StoreWeekSheets";

export type MyScheduleScope = "mine" | "store";
type StoreWeekView = "day" | "grid";

/** 通用小分段开关：和页面其它分段保持同一外观（浅灰底、选中白底）。 */
function Segmented<T extends string>({
  value,
  options,
  onChange,
  accessibilityLabel,
  compact,
}: {
  value: T;
  options: { value: T; label: string }[];
  onChange: (value: T) => void;
  accessibilityLabel: string;
  compact?: boolean;
}) {
  return (
    <View accessibilityRole="tablist" accessibilityLabel={accessibilityLabel} style={[styles.seg, compact ? styles.segCompact : null]}>
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <Pressable
            key={option.value}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(option.value)}
            style={[styles.segItem, selected ? styles.segItemOn : null]}
          >
            <Text style={[styles.segText, compact ? styles.segTextCompact : null, selected ? styles.segTextOn : null]}>{option.label}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/** 周导航下面的「我的班次 ｜ 整店排班」开关，两个视图共用。 */
export function MyScheduleScopeSwitch({ value, onChange }: { value: MyScheduleScope; onChange: (value: MyScheduleScope) => void }) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <Segmented
      value={value}
      onChange={onChange}
      accessibilityLabel={t("storeWeek.scopeLabel")}
      options={[
        { value: "mine", label: t("storeWeek.scope.mine") },
        { value: "store", label: t("storeWeek.scope.store") },
      ]}
    />
  );
}

export function StoreWeekPanel({
  weekStartDate,
  today,
  storeCode,
  week,
  isLoading,
  isError,
  onRetry,
  onPreviousWeek,
  onNextWeek,
  onGoToMine,
  scopeSwitch,
}: {
  weekStartDate: string;
  today: string;
  /** 页面当前选中的分店；为空时取接口返回里「我有班的店」。 */
  storeCode?: string;
  week?: StoreWeek;
  isLoading: boolean;
  isError: boolean;
  onRetry: () => void;
  onPreviousWeek: () => void;
  onNextWeek: () => void;
  /** 切回「我的班次」（点弹层里的「看我的班次」、未发布时去填可上班时间）。 */
  onGoToMine: () => void;
  scopeSwitch: ReactNode;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const [view, setView] = useState<StoreWeekView>("day");
  const [dayDate, setDayDate] = useState<string>();
  const [personGuid, setPersonGuid] = useState<string>();

  const weekDates = useMemo(() => buildWeekDates(weekStartDate), [weekStartDate]);
  const store = pickStoreWeek(week, storeCode);
  const shifts = useMemo(() => store?.shifts ?? [], [store]);
  const days = useMemo(() => groupStoreWeekDays(weekDates, shifts), [shifts, weekDates]);
  const grid = useMemo(() => buildStoreWeekGrid(weekDates, shifts), [shifts, weekDates]);
  const colors = useMemo(() => staffColorMap(shifts), [shifts]);
  const legend = useMemo(() => legendPeople(shifts), [shifts]);
  // 全周共用一条时间轴：按天视图里不同天的班次条才能比长短，弹层刻度也一致。
  const axis = useMemo(() => buildDayAxis(shifts), [shifts]);
  const summary = summarizeStoreWeek(shifts);

  const tagKey = weekTagKey(weekStartDate, today);
  const range = `${weekDates[0]?.slice(5) ?? ""} ~ ${weekDates[6]?.slice(5) ?? ""}`;
  const weekWord = tagKey ? t(`myAttendance.weekTag.${tagKey}`) : range;
  const isFutureWeek = (weekDates[0] ?? "") > today;
  const dayIndex = dayDate ? weekDates.indexOf(dayDate) : -1;
  const person = personGuid ? legend.find((item) => item.userGuid === personGuid) : undefined;

  const renderBody = () => {
    if (!week) {
      if (isLoading) return <ActivityIndicator />;
      return (
        <View style={styles.card}>
          <Text style={styles.cardTitle}>{t("storeWeek.error.title")}</Text>
          <Button mode="contained" style={styles.cardAction} onPress={onRetry}>{t("storeWeek.error.retry")}</Button>
        </View>
      );
    }
    if (!store) {
      return (
        <View style={styles.card}>
          <Text style={styles.cardBody}>{t("storeWeek.noStore")}</Text>
        </View>
      );
    }
    if (!store.published) {
      return (
        <View style={styles.card}>
          <Text style={styles.cardTitle}>
            {t("storeWeek.empty.title", { store: store.storeName ?? store.storeCode, week: weekWord })}
          </Text>
          <Text style={styles.cardBody}>{t("storeWeek.empty.body")}</Text>
          {/* 本周及以前的周「没发布」没有可填的事；未来周才引导去填可上班时间（现有功能）。 */}
          {isFutureWeek ? (
            <>
              <Text style={styles.cardBody}>{t("storeWeek.empty.fillHint")}</Text>
              <Button mode="contained" style={styles.cardAction} onPress={onGoToMine}>{t("storeWeek.empty.fillAction")}</Button>
            </>
          ) : null}
        </View>
      );
    }
    return (
      <>
        {isError ? (
          <View style={styles.staleBanner} accessibilityRole="alert">
            <Text style={styles.staleText}>{t("storeWeek.error.stale")}</Text>
            <Pressable accessibilityRole="button" onPress={onRetry} hitSlop={8}>
              <Text style={styles.retryText}>{t("storeWeek.error.retry")}</Text>
            </Pressable>
          </View>
        ) : null}
        <View style={styles.modeRow}>
          <Segmented
            compact
            value={view}
            onChange={setView}
            accessibilityLabel={t("storeWeek.viewLabel")}
            options={[
              { value: "day", label: t("storeWeek.view.day") },
              { value: "grid", label: t("storeWeek.view.grid") },
            ]}
          />
          <View style={styles.meta}>
            <Text style={styles.metaStore} numberOfLines={1}>{store.storeName ?? store.storeCode}</Text>
            <Text style={styles.metaText}>
              {`${t("storeWeek.meta", { shifts: summary.shiftCount, people: summary.peopleCount })} · ${t("storeWeek.published")}`}
            </Text>
          </View>
        </View>
        {view === "day" ? (
          <>
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.legend}>
              {legend.map((item) => {
                const color = colors.get(item.userGuid);
                return (
                  <View key={item.userGuid} style={styles.legendItem}>
                    <View style={[styles.legendDot, { backgroundColor: color?.bar ?? "#8c8c8c" }]} />
                    <Text style={styles.legendText}>{item.isMine ? `${item.name} (${t("storeWeek.me")})` : item.name}</Text>
                  </View>
                );
              })}
            </ScrollView>
            <StoreWeekDays days={days} today={today} axis={axis} colors={colors} onOpenDay={setDayDate} />
          </>
        ) : (
          <>
            <StoreWeekGrid rows={grid} days={days} today={today} colors={colors} onOpenDay={setDayDate} onOpenPerson={setPersonGuid} />
            <Text style={styles.hint}>{t("storeWeek.gridHint")}</Text>
          </>
        )}
        <Text style={styles.notice}>{t("storeWeek.notice")}</Text>
      </>
    );
  };

  return (
    <View style={styles.root}>
      <WeekNavBar
        label={tagKey ? `${range} · ${t(`myAttendance.weekTag.${tagKey}`)}` : range}
        onPreviousWeek={onPreviousWeek}
        onNextWeek={onNextWeek}
      />
      {scopeSwitch}
      {renderBody()}
      <StoreWeekDaySheet
        day={dayIndex >= 0 ? days[dayIndex] : undefined}
        weekdayIndex={Math.max(dayIndex, 0)}
        isToday={dayDate === today}
        axis={axis}
        colors={colors}
        onDismiss={() => setDayDate(undefined)}
        onViewMine={() => {
          setDayDate(undefined);
          onGoToMine();
        }}
      />
      <StoreWeekPersonSheet
        name={person?.name}
        userGuid={person?.userGuid}
        days={days}
        today={today}
        axis={axis}
        colors={colors}
        onDismiss={() => setPersonGuid(undefined)}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  root: { gap: HB_SPACING.xs },
  seg: { backgroundColor: "#E9EDF3", borderRadius: 10, flexDirection: "row", padding: 3 },
  segCompact: { width: 132 },
  segItem: { alignItems: "center", borderRadius: 8, flex: 1, justifyContent: "center", paddingVertical: 7 },
  segItemOn: { backgroundColor: HB_COLORS.white, elevation: 1, shadowColor: "#101828", shadowOffset: { height: 1, width: 0 }, shadowOpacity: 0.14, shadowRadius: 2 },
  segText: { color: HB_COLORS.textSecondary, fontSize: 13.5, fontWeight: "600" },
  segTextCompact: { fontSize: 12.5 },
  segTextOn: { color: HB_COLORS.textPrimary },
  modeRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  meta: { flex: 1, gap: 1 },
  metaStore: { color: HB_COLORS.textPrimary, fontSize: 12, fontWeight: "700" },
  metaText: { color: HB_COLORS.textSecondary, fontSize: 12 },
  legend: { gap: 6, paddingVertical: 2 },
  legendItem: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 999,
    borderWidth: 1,
    flexDirection: "row",
    gap: 5,
    paddingLeft: 6,
    paddingRight: 9,
    paddingVertical: 3,
  },
  legendDot: { borderRadius: 5, height: 9, width: 9 },
  legendText: { color: HB_COLORS.textPrimary, fontSize: 11.5, fontWeight: "600" },
  card: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: 14,
    borderStyle: "dashed",
    borderWidth: 1,
    gap: 6,
    paddingHorizontal: 18,
    paddingVertical: 24,
  },
  cardTitle: { color: HB_COLORS.textPrimary, fontSize: 15.5, fontWeight: "700", textAlign: "center" },
  cardBody: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 19, textAlign: "center" },
  cardAction: { marginTop: 6 },
  staleBanner: {
    alignItems: "center",
    backgroundColor: "#FEF3F2",
    borderColor: "#FECDCA",
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    justifyContent: "space-between",
    paddingHorizontal: 12,
    paddingVertical: 9,
  },
  staleText: { color: HB_COLORS.danger, flex: 1, fontSize: 12.5 },
  retryText: { color: "#0958D9", fontSize: 13, fontWeight: "700" },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12 },
  notice: {
    backgroundColor: "#EFF8FF",
    borderColor: "#B2DDFF",
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    color: "#175CD3",
    fontSize: 12.5,
    lineHeight: 18,
    overflow: "hidden",
    paddingHorizontal: 12,
    paddingVertical: 9,
  },
});
