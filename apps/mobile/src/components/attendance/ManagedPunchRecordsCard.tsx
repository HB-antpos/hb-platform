// 店长「打卡记录」：周日期条（红点 = 当天有异常）→ 分组筛选 → 按「需处理 / 工作中 / 未开始 / 已下班 / 请假」
// 分组的名单，每人一条共用时间轴（排班 / 实际在岗 / 迟到缺卡 / 现在）。点人看当天明细，
// 需处理的行直接给补录快捷操作；修改与补录沿用 ManagedPunchEditSheet（预览指纹 + 留痕）。
import { useEffect, useMemo, useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Icon, Text } from "react-native-paper";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createManagedAttendancePunchAdjustment,
  getManagedAttendanceRecords,
  getManagedAttendanceRecordsRange,
  previewManagedAttendancePunchAdjustment,
} from "@/modules/attendance/api";
import { formatClockMinutes, timelinePercent } from "@/modules/attendance/approval-board";
import {
  buildManagedDayAxis,
  classifyManagedRecord,
  datesWithExceptions,
  groupManagedRecords,
  isWithinManagedAdjustmentWindow,
  type ManagedRecordGroupKey,
  type ManagedRecordIssue,
  mondayOfDate,
  shiftDateString,
  weekDatesFrom,
} from "@/modules/attendance/attendance-managed-records";
import type { AttendanceApproval, AttendanceScheduleSession } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ATTENDANCE_STATUS_TONES } from "./AdjustmentFormControls";
import { PersonAvatar } from "./approvals/approval-ui";
import { ManagedPunchDayDetail } from "./management/ManagedPunchDayDetail";
import { RecordTimeline } from "./management/RecordTimeline";
import { localDateString, localMinutesNow } from "./management/ManagementHeader";
import {
  ISSUE_TONES,
  issueLabel,
  rowBars,
  rowHours,
  rowMeta,
  sessionAxisPoints,
} from "./management/managed-record-display";
import { type ManagedPunchEditInitialAction, ManagedPunchEditSheet } from "./ManagedPunchEditSheet";

type GroupFilter = "all" | ManagedRecordGroupKey;


/** 这个人这天还有几条待审（补卡、打卡异常、加班等），按申请人 + 工作日匹配。 */
function pendingFor(approvals: AttendanceApproval[], session: AttendanceScheduleSession) {
  const date = session.workDate.slice(0, 10);
  const user = session.userGuid?.toLowerCase();
  return approvals.filter((item) =>
    item.workDate?.slice(0, 10) === date && Boolean(user) && item.applicantUserGuid?.toLowerCase() === user);
}

export function ManagedPunchRecordsCard({
  initialWorkDate,
  storeCode,
  canAdjust,
  onMessage,
  pendingApprovals = [],
  onOpenApprovals,
}: {
  /** 从审核「补录下班」跳转时定位到的日期，默认今天。 */
  initialWorkDate?: string;
  storeCode?: string;
  canAdjust: boolean;
  onMessage: (message: string) => void;
  /** 待审列表（审核页签同一份）：用于「这天有 N 项待审」与行内「N 项待审 ›」。 */
  pendingApprovals?: AttendanceApproval[];
  onOpenApprovals?: () => void;
}) {
  const { t, language } = useAppTranslation(["attendance", "common"]);
  const queryClient = useQueryClient();
  const today = useMemo(() => localDateString(new Date()), []);
  const [workDate, setWorkDate] = useState(initialWorkDate ?? today);
  const [filter, setFilter] = useState<GroupFilter>("all");
  const [detailSession, setDetailSession] = useState<AttendanceScheduleSession>();
  const [editing, setEditing] = useState<{ session: AttendanceScheduleSession; action?: ManagedPunchEditInitialAction }>();
  // 「现在」每分钟走一格：今天的在岗条、未打卡判定都依赖它。
  const [nowMinutes, setNowMinutes] = useState(() => localMinutesNow());
  useEffect(() => {
    const timer = setInterval(() => setNowMinutes(localMinutesNow()), 60_000);
    return () => clearInterval(timer);
  }, []);
  const isToday = workDate === today;
  const weekStart = mondayOfDate(workDate);
  const weekDates = useMemo(() => weekDatesFrom(weekStart), [weekStart]);

  const recordsQuery = useQuery({
    queryKey: ["attendance", "managed", "records", storeCode ?? "", workDate],
    queryFn: () => getManagedAttendanceRecords({ storeCode: storeCode!, workDate }),
    enabled: Boolean(storeCode),
  });
  // 周日期条的红点：一次取整周（第四段是 "week"，与单日缓存键区分，又能被 records 前缀一起刷新）。
  const weekQuery = useQuery({
    queryKey: ["attendance", "managed", "records", "week", storeCode ?? "", weekStart],
    queryFn: () => getManagedAttendanceRecordsRange({ storeCode: storeCode!, fromDate: weekStart, toDate: weekDates[6] }),
    enabled: Boolean(storeCode),
  });
  const exceptionDates = useMemo(
    () => datesWithExceptions(weekQuery.data ?? [], today, nowMinutes),
    [nowMinutes, today, weekQuery.data],
  );

  const previewMutation = useMutation({ mutationFn: previewManagedAttendancePunchAdjustment });
  const createMutation = useMutation({
    mutationFn: createManagedAttendancePunchAdjustment,
    onSuccess: async () => {
      onMessage(t("managedRecords.saved"));
      // 修改会改变该员工的工时、加班候选与待审事项，几处一起刷新。
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["attendance", "managed", "records"] }),
        queryClient.invalidateQueries({ queryKey: ["attendance", "approvals"] }),
        queryClient.invalidateQueries({ queryKey: ["attendance", "my", "today"] }),
        queryClient.invalidateQueries({ queryKey: ["attendance", "payroll", "hours"] }),
      ]);
    },
  });

  const rows = useMemo(
    () => (recordsQuery.data ?? []).map((session) => ({
      session,
      issue: classifyManagedRecord(session, today, isToday ? nowMinutes : undefined) as ManagedRecordIssue,
    })),
    [isToday, nowMinutes, recordsQuery.data, today],
  );
  const groups = useMemo(() => groupManagedRecords(rows), [rows]);
  const activeFilter: GroupFilter = filter !== "all" && groups.some((group) => group.key === filter) ? filter : "all";
  const visibleGroups = activeFilter === "all" ? groups : groups.filter((group) => group.key === activeFilter);
  const axis = useMemo(
    () => buildManagedDayAxis((recordsQuery.data ?? []).flatMap(sessionAxisPoints).concat(isToday ? [nowMinutes] : [])),
    [isToday, nowMinutes, recordsQuery.data],
  );
  const dayPending = pendingApprovals.filter((item) => item.workDate?.slice(0, 10) === workDate);
  const editableDay = canAdjust && isWithinManagedAdjustmentWindow(workDate, today);
  const options = { isToday, nowMinutes };

  // 详情里的班次随列表刷新（修改后）换成最新数据。
  const liveDetail = detailSession
    ? recordsQuery.data?.find((session) => session.scheduleGuid === detailSession.scheduleGuid) ?? detailSession
    : undefined;

  if (!storeCode) {
    return (
      <View style={styles.card}>
        <Text variant="bodySmall" style={styles.muted}>{t("managedRecords.noStore")}</Text>
      </View>
    );
  }

  const weekdayLabel = (date: string) => {
    const index = weekDates.indexOf(date);
    return t(`weekdays.${index}`);
  };

  return (
    <View style={styles.root}>
      {/* 周日期条：‹ 一 二 … 日 ›，未来日期不可选 */}
      <View style={styles.weekRow}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("managedRecords.previousWeek")}
          hitSlop={6}
          onPress={() => setWorkDate(shiftDateString(workDate, -7))}
          style={styles.weekArrow}
        >
          <Icon source="chevron-left" size={20} color={HB_COLORS.textSecondary} />
        </Pressable>
        {weekDates.map((date) => {
          const selected = date === workDate;
          const future = date > today;
          const hasException = exceptionDates.has(date);
          return (
            <Pressable
              key={date}
              accessibilityRole="button"
              accessibilityState={{ selected, disabled: future }}
              accessibilityLabel={`${date}${hasException ? ` ${t("managedRecords.dayHasException")}` : ""}`}
              disabled={future}
              onPress={() => setWorkDate(date)}
              style={[styles.day, selected ? styles.daySelected : null, future ? styles.dayFuture : null]}
            >
              {/* 今天：星期字变蓝加粗（底部位置留给异常红点）。 */}
              <Text style={[styles.dayWeekday, date === today && !selected ? styles.dayToday : null, selected ? styles.daySelectedSub : null]}>{weekdayLabel(date)}</Text>
              <Text style={[styles.dayNumber, selected ? styles.daySelectedText : null]}>{Number(date.slice(8, 10))}</Text>
              {hasException ? <View style={[styles.dot, selected ? styles.dotSelected : null]} /> : null}
            </Pressable>
          );
        })}
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("managedRecords.nextWeek")}
          hitSlop={6}
          disabled={weekDates[6] >= today}
          onPress={() => {
            const next = shiftDateString(workDate, 7);
            setWorkDate(next > today ? today : next);
          }}
          style={styles.weekArrow}
        >
          <Icon source="chevron-right" size={20} color={weekDates[6] >= today ? HB_COLORS.outline : HB_COLORS.textSecondary} />
        </Pressable>
      </View>

      {dayPending.length > 0 && onOpenApprovals ? (
        <Pressable accessibilityRole="button" onPress={onOpenApprovals} style={styles.banner}>
          <Icon source="alert-outline" size={16} color={HB_COLORS.warning} />
          <Text variant="bodySmall" style={styles.bannerText}>{t("managedRecords.pendingBanner", { count: dayPending.length })}</Text>
          <Text variant="labelMedium" style={styles.bannerAction}>{t("managedRecords.openApprovals")}</Text>
        </Pressable>
      ) : null}

      {rows.length > 0 ? (
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips}>
          {(["all", ...groups.map((group) => group.key)] as GroupFilter[]).map((key) => {
            const selected = key === activeFilter;
            const count = key === "all" ? rows.length : groups.find((group) => group.key === key)?.rows.length ?? 0;
            const label = key === "all" ? t("managedRecords.filterAll", { count }) : t("managedRecords.filterGroup", { label: t(`managedRecords.groups.${key}`), count });
            return (
              <Pressable
                key={key}
                accessibilityRole="button"
                accessibilityState={{ selected }}
                onPress={() => setFilter(key)}
                style={[styles.chip, key === "attention" && !selected ? styles.chipAttention : null, selected ? styles.chipSelected : null]}
              >
                <Text style={[styles.chipText, key === "attention" && !selected ? styles.chipAttentionText : null, selected ? styles.chipSelectedText : null]}>
                  {label}
                </Text>
              </Pressable>
            );
          })}
        </ScrollView>
      ) : null}

      {!isToday && canAdjust ? (
        <Text variant="bodySmall" style={styles.note}>
          {editableDay ? t("managedRecords.windowNote") : t("managedRecords.outsideWindow")}
        </Text>
      ) : null}

      <View style={styles.card}>
        {recordsQuery.isLoading ? <ActivityIndicator style={styles.loading} /> : null}
        {recordsQuery.error ? (
          <Text variant="bodySmall" style={[styles.dangerText, styles.cardPad]} accessibilityRole="alert">
            {t("managedRecords.loadFailed")}
          </Text>
        ) : null}
        {!recordsQuery.isLoading && !recordsQuery.error && !rows.length ? (
          <Text variant="bodySmall" style={[styles.muted, styles.cardPad]}>{t("managedRecords.empty")}</Text>
        ) : null}

        {rows.length ? (
          <View style={styles.axis}>
            {axis.ticks.map((tick) => (
              <Text key={tick} style={[styles.tick, { left: `${timelinePercent(tick, axis)}%` }]}>{formatClockMinutes(tick).slice(0, 2)}</Text>
            ))}
            {isToday && nowMinutes >= axis.start && nowMinutes <= axis.end ? (
              <View style={[styles.nowTag, { left: `${timelinePercent(nowMinutes, axis)}%` }]}>
                <Text style={styles.nowTagText} numberOfLines={1}>{formatClockMinutes(nowMinutes)}</Text>
              </View>
            ) : null}
          </View>
        ) : null}

        {visibleGroups.map((group) => (
          <View key={group.key}>
            <View style={styles.section}>
              <Text style={[styles.sectionTitle, group.key === "attention" ? styles.sectionAttention : null]}>
                {`${t(`managedRecords.groups.${group.key}`)} ${group.rows.length}`}
              </Text>
              {group.key === "attention" ? <Text style={styles.sectionHint}>{t("managedRecords.tapHint")}</Text> : null}
            </View>
            {group.rows.map(({ session, issue }) => {
              const hours = rowHours(session, issue, t);
              const tone = ATTENDANCE_STATUS_TONES[ISSUE_TONES[issue]];
              const personPending = pendingFor(pendingApprovals, session);
              const showActions = group.key === "attention" && (editableDay || personPending.length > 0);
              return (
                <Pressable
                  key={session.scheduleGuid}
                  accessibilityRole="button"
                  onPress={() => setDetailSession(session)}
                  style={({ pressed }) => [styles.row, pressed ? styles.rowPressed : null]}
                >
                  <View style={styles.rowHead}>
                    <PersonAvatar name={session.employeeName} size={30} />
                    <View style={styles.rowBody}>
                      <View style={styles.nameLine}>
                        <Text style={styles.name} numberOfLines={1}>{session.employeeName || session.userGuid}</Text>
                        <View style={[styles.pill, { backgroundColor: tone.background }]}>
                          <Text style={[styles.pillText, { color: tone.text }]} numberOfLines={1}>{issueLabel(session, issue, t)}</Text>
                        </View>
                      </View>
                      <Text style={styles.meta} numberOfLines={1}>{rowMeta(session, issue, t, options)}</Text>
                    </View>
                    {hours ? (
                      <View style={styles.hours}>
                        <Text style={styles.hoursValue}>{hours.value}</Text>
                        {hours.sub ? <Text style={styles.hoursSub} numberOfLines={1}>{hours.sub}</Text> : null}
                      </View>
                    ) : null}
                  </View>
                  <RecordTimeline bars={rowBars(session, issue, options)} axis={axis} now={isToday ? nowMinutes : undefined} style={styles.timelineIndent} />
                  {showActions ? (
                    <View style={styles.actions}>
                      {editableDay && issue === "missingClockOut" ? (
                        <ActionButton primary label={t("managedRecords.actions.fillClockOut")} onPress={() => setEditing({ session, action: { kind: "add", punchType: "ClockOut" } })} />
                      ) : null}
                      {editableDay && issue === "noPunch" ? (
                        <ActionButton label={t("managedRecords.actions.fillPunches")} onPress={() => setEditing({ session, action: { kind: "add", punchType: "ClockIn" } })} />
                      ) : null}
                      {personPending.length > 0 && onOpenApprovals ? (
                        <ActionButton warning label={t("managedRecords.actions.pending", { count: personPending.length })} onPress={onOpenApprovals} />
                      ) : null}
                    </View>
                  ) : null}
                </Pressable>
              );
            })}
          </View>
        ))}
      </View>

      {liveDetail ? (
        <ManagedPunchDayDetail
          session={liveDetail}
          issue={classifyManagedRecord(liveDetail, today, isToday ? nowMinutes : undefined)}
          today={today}
          nowMinutes={nowMinutes}
          language={language}
          canEdit={editableDay}
          pendingApprovals={pendingFor(pendingApprovals, liveDetail)}
          onOpenApprovals={onOpenApprovals ? () => { setDetailSession(undefined); onOpenApprovals(); } : undefined}
          onEdit={(action) => {
            setEditing({ session: liveDetail, action });
            setDetailSession(undefined);
          }}
          onDismiss={() => setDetailSession(undefined)}
        />
      ) : null}

      <ManagedPunchEditSheet
        visible={Boolean(editing)}
        session={editing?.session}
        initialAction={editing?.action}
        today={today}
        canAdjust={canAdjust}
        isBusy={previewMutation.isPending || createMutation.isPending}
        onPreview={(payload) => previewMutation.mutateAsync(payload)}
        onSubmit={async (payload) => {
          await createMutation.mutateAsync(payload);
        }}
        onDismiss={() => setEditing(undefined)}
      />
    </View>
  );
}

function ActionButton({ label, onPress, primary, warning }: { label: string; onPress: () => void; primary?: boolean; warning?: boolean }) {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      hitSlop={4}
      style={[styles.action, primary ? styles.actionPrimary : null, warning ? styles.actionWarning : null]}
    >
      <Text style={[styles.actionText, primary ? styles.actionPrimaryText : null, warning ? styles.actionWarningText : null]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { gap: HB_SPACING.xs },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface + 2,
    borderWidth: StyleSheet.hairlineWidth,
    overflow: "hidden",
  },
  cardPad: { padding: HB_SPACING.md },
  loading: { padding: HB_SPACING.md },
  muted: { color: HB_COLORS.textSecondary },
  dangerText: { color: HB_COLORS.danger },
  weekRow: { alignItems: "center", flexDirection: "row", gap: 2 },
  weekArrow: { alignItems: "center", height: 44, justifyContent: "center", width: 22 },
  day: { alignItems: "center", borderRadius: 12, flex: 1, paddingBottom: 8, paddingTop: 5 },
  daySelected: { backgroundColor: HB_COLORS.textPrimary },
  dayFuture: { opacity: 0.35 },
  dayWeekday: { color: HB_COLORS.textSecondary, fontSize: 11 },
  dayNumber: { color: HB_COLORS.textPrimary, fontSize: 16, fontVariant: ["tabular-nums"], fontWeight: "700" },
  daySelectedText: { color: HB_COLORS.white },
  daySelectedSub: { color: "#CFD4DC" },
  dayToday: { color: HB_COLORS.action, fontWeight: "700" },
  dot: { backgroundColor: HB_COLORS.danger, borderRadius: 3, bottom: 2, height: 5, position: "absolute", width: 5 },
  dotSelected: { backgroundColor: "#FDA29B" },
  banner: {
    alignItems: "center",
    backgroundColor: "#FFFAEB",
    borderColor: "#FEDF89",
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    flexDirection: "row",
    gap: 6,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 9,
  },
  bannerText: { color: HB_COLORS.warning, flex: 1, fontWeight: "500" },
  bannerAction: { color: HB_COLORS.warning, fontWeight: "700" },
  chips: { gap: 6 },
  chip: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: 16,
    borderWidth: 1,
    paddingHorizontal: 11,
    paddingVertical: 5,
  },
  chipText: { color: HB_COLORS.textPrimary, fontSize: 13 },
  chipSelected: { backgroundColor: HB_COLORS.textPrimary, borderColor: HB_COLORS.textPrimary },
  chipSelectedText: { color: HB_COLORS.white, fontWeight: "600" },
  chipAttention: { backgroundColor: "#FEF3F2", borderColor: "#FECDCA" },
  chipAttentionText: { color: HB_COLORS.danger },
  note: { color: HB_COLORS.textSecondary, paddingHorizontal: 2 },
  axis: {
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    height: 26,
    marginLeft: 14 + 39,
    marginRight: 14,
  },
  tick: { color: "#98A2B3", fontSize: 10.5, fontVariant: ["tabular-nums"], marginLeft: -8, position: "absolute", textAlign: "center", top: 8, width: 16 },
  nowTag: { backgroundColor: "#F04438", borderRadius: 4, marginLeft: -22, position: "absolute", top: 5, width: 44 },
  nowTagText: { color: HB_COLORS.white, fontSize: 10, fontVariant: ["tabular-nums"], fontWeight: "700", textAlign: "center" },
  section: {
    backgroundColor: "#FCFCFD",
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    justifyContent: "space-between",
    paddingBottom: 3,
    paddingHorizontal: 14,
    paddingTop: 9,
  },
  sectionTitle: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700" },
  sectionAttention: { color: HB_COLORS.danger },
  sectionHint: { color: HB_COLORS.danger, fontSize: 11.5 },
  row: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: 8,
    paddingBottom: 10,
    paddingHorizontal: 14,
    paddingTop: 9,
  },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  rowHead: { alignItems: "center", flexDirection: "row", gap: 9 },
  rowBody: { flex: 1, minWidth: 0 },
  nameLine: { alignItems: "center", flexDirection: "row", gap: 5 },
  name: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 15, fontWeight: "600" },
  pill: { borderRadius: 5, paddingHorizontal: 6, paddingVertical: 1 },
  pillText: { fontSize: 11, fontWeight: "700" },
  meta: { color: HB_COLORS.textSecondary, fontSize: 12, fontVariant: ["tabular-nums"], marginTop: 1 },
  hours: { alignItems: "flex-end" },
  hoursValue: { color: HB_COLORS.textPrimary, fontSize: 14, fontVariant: ["tabular-nums"], fontWeight: "700" },
  hoursSub: { color: "#98A2B3", fontSize: 11 },
  timelineIndent: { marginLeft: 39 },
  actions: { flexDirection: "row", flexWrap: "wrap", gap: 6, marginLeft: 39 },
  action: { borderColor: HB_COLORS.outline, borderRadius: 8, borderWidth: 1, paddingHorizontal: 10, paddingVertical: 5 },
  actionText: { color: HB_COLORS.textPrimary, fontSize: 12.5, fontWeight: "600" },
  actionPrimary: { backgroundColor: HB_COLORS.action, borderColor: HB_COLORS.action },
  actionPrimaryText: { color: HB_COLORS.white },
  actionWarning: { borderColor: "#FEDF89" },
  actionWarningText: { color: HB_COLORS.warning },
});
