// 打卡记录点人打开的「当天打卡明细」：已工作 / 班段 / 状态，排班与实际时间轴，按班段列出上下班卡
// （时间、原因、扫码设备、定位），每张卡旁边就是「修改 / 补录」；有待审补卡时给出提示并可跳审核。
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { formatClockMinutes, timelinePercent } from "@/modules/attendance/approval-board";
import { resolveAttendancePunchDisplayTime } from "@/modules/attendance/attendance-device-time";
import { buildManagedDayAxis, type ManagedRecordIssue } from "@/modules/attendance/attendance-managed-records";
import type { AttendanceApproval, AttendancePunch, AttendanceScheduleSession } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ATTENDANCE_STATUS_TONES } from "../AdjustmentFormControls";
import { useApprovalDateLabels } from "../approvals/use-approval-labels";
import type { ManagedPunchEditInitialAction } from "../ManagedPunchEditSheet";
import { clockText, ISSUE_TONES, issueLabel, rowBars, rowHours, sessionAxisPoints } from "./managed-record-display";
import { RecordTimeline } from "./RecordTimeline";

type Translate = (key: string, options?: Record<string, unknown>) => string;

function punchMeta(punch: AttendancePunch, t: Translate) {
  const parts: string[] = [];
  if (punch.posDeviceCode) {
    parts.push(t("managedRecords.detail.viaQr"), t("managedRecords.detail.device", { code: punch.posDeviceCode }));
  }
  if (punch.locationAccuracy !== undefined && punch.locationLatitude !== undefined) {
    parts.push(t("managedRecords.detail.location", { meters: Math.round(punch.locationAccuracy) }));
  }
  return parts.join(" · ");
}

function punchStatusLabel(punch: AttendancePunch, t: Translate) {
  if (punch.status === "Late") {
    return punch.lateMinutes ? t("managedRecords.lateBy", { minutes: punch.lateMinutes }) : t("managedRecords.issues.late");
  }
  if (punch.status === "EarlyLeave") {
    return punch.earlyLeaveMinutes ? t("managedRecords.earlyBy", { minutes: punch.earlyLeaveMinutes }) : t("managedRecords.issues.earlyLeave");
  }
  return undefined;
}

export function ManagedPunchDayDetail({
  session,
  issue,
  today,
  nowMinutes,
  canEdit,
  pendingApprovals,
  onOpenApprovals,
  onEdit,
  onDismiss,
}: {
  session: AttendanceScheduleSession;
  issue: ManagedRecordIssue;
  today: string;
  nowMinutes: number;
  language: string;
  /** 有补卡修改权限且日期在可修改窗口内。 */
  canEdit: boolean;
  pendingApprovals: AttendanceApproval[];
  onOpenApprovals?: () => void;
  /** 打开修改弹层；不传 action = 按缺卡默认。 */
  onEdit: (action?: ManagedPunchEditInitialAction) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const { weekdayDate } = useApprovalDateLabels();
  const workDate = session.workDate.slice(0, 10);
  const isToday = workDate === today;
  const options = { isToday, nowMinutes };
  const axis = buildManagedDayAxis(sessionAxisPoints(session).concat(isToday ? [nowMinutes] : []));
  const hours = rowHours(session, issue, t);
  const tone = ATTENDANCE_STATUS_TONES[ISSUE_TONES[issue]];
  const subtitle = [
    weekdayDate(workDate),
    t("managedRecords.meta.schedule", { start: clockText(session.startTime) ?? "--:--", end: clockText(session.endTime) ?? "--:--" }),
  ].join(" · ");

  const renderPunchRow = (kind: "ClockIn" | "ClockOut", punch: AttendancePunch | undefined, segmentOpen: boolean) => {
    const real = punch && !punch.isSystemAutoClockOut ? punch : undefined;
    const status = real ? punchStatusLabel(real, t) : undefined;
    const meta = real ? punchMeta(real, t) : undefined;
    let value: string;
    let sub: string | undefined;
    if (real) {
      value = clockText(resolveAttendancePunchDisplayTime(real)) ?? "--:--";
      sub = meta || undefined;
    } else if (kind === "ClockOut" && segmentOpen && isToday && issue !== "missingClockOut") {
      value = `— ${t("managedRecords.working")}`;
      sub = t("managedRecords.detail.notClockedOut");
    } else {
      value = t("managedRecords.detail.missing");
      sub = punch?.isSystemAutoClockOut ? t("managedRecords.meta.autoClockOut") : undefined;
    }
    const action: ManagedPunchEditInitialAction | undefined = real?.punchGuid
      ? { kind: "edit", punchGuid: real.punchGuid }
      : { kind: "add", punchType: kind };
    return (
      <View key={kind} style={styles.punchRow}>
        <Text style={styles.punchKind}>{kind === "ClockIn" ? t("managedRecords.detail.clockIn") : t("managedRecords.detail.clockOut")}</Text>
        <View style={styles.punchBody}>
          <View style={styles.inline}>
            <Text style={[styles.punchTime, !real ? styles.punchTimeMissing : null]}>{value}</Text>
            {status ? (
              <View style={[styles.pill, { backgroundColor: ATTENDANCE_STATUS_TONES[real?.status === "Late" ? "danger" : "warning"].background }]}>
                <Text style={[styles.pillText, { color: ATTENDANCE_STATUS_TONES[real?.status === "Late" ? "danger" : "warning"].text }]}>{status}</Text>
              </View>
            ) : null}
          </View>
          {sub ? <Text style={styles.punchSub}>{sub}</Text> : null}
        </View>
        {canEdit ? (
          <Pressable accessibilityRole="button" onPress={() => onEdit(action)} style={styles.editButton} hitSlop={4}>
            <Text style={styles.editText}>{real ? t("managedRecords.detail.edit") : t("managedRecords.detail.fill")}</Text>
          </Pressable>
        ) : null}
      </View>
    );
  };

  return (
    <BusinessSheet
      visible
      title={session.employeeName || session.userGuid || ""}
      subtitle={subtitle}
      onDismiss={onDismiss}
      footer={
        <View style={styles.footer}>
          <Text variant="bodySmall" style={styles.footerNote}>
            {canEdit ? t("managedRecords.auditNotice") : t("managedRecords.readOnly")}
          </Text>
          {canEdit ? (
            <View style={styles.footerButtons}>
              <Button mode="outlined" style={styles.footerButton} onPress={() => onEdit({ kind: "add", punchType: "ClockIn" })}>
                {t("managedRecords.detail.addSegment")}
              </Button>
              <Button mode="contained" style={styles.footerButton} onPress={() => onEdit()}>
                {t("managedRecords.detail.editPunches")}
              </Button>
            </View>
          ) : null}
        </View>
      }
    >
      <View style={styles.stats}>
        <View style={styles.stat}>
          <Text variant="bodySmall" style={styles.muted}>{t("managedRecords.detail.worked")}</Text>
          <Text style={styles.statValue}>{hours?.value ?? "0m"}</Text>
        </View>
        <View style={styles.stat}>
          <Text variant="bodySmall" style={styles.muted}>{t("managedRecords.detail.segments")}</Text>
          <Text style={styles.statValue}>
            {session.segmentLimit ? `${session.segments.length} / ${session.segmentLimit}` : String(session.segments.length)}
          </Text>
        </View>
        <View style={styles.stat}>
          <Text variant="bodySmall" style={styles.muted}>{t("managedRecords.detail.status")}</Text>
          <Text style={[styles.statValue, { color: tone.text }]} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.7}>
            {issueLabel(session, issue, t)}
          </Text>
        </View>
      </View>

      <View style={styles.timeline}>
        <RecordTimeline bars={rowBars(session, issue, options)} axis={axis} now={isToday ? nowMinutes : undefined} height={14} />
        <View style={styles.ticks}>
          {axis.ticks.map((tick) => (
            <Text key={tick} style={[styles.tick, { left: `${timelinePercent(tick, axis)}%` }]}>{formatClockMinutes(tick).slice(0, 2)}</Text>
          ))}
        </View>
      </View>

      {session.segments.length === 0 ? (
        <View style={styles.segment}>
          <Text variant="bodySmall" style={[styles.muted, styles.segmentEmpty]}>{t("managedRecords.detail.noSegments")}</Text>
          {renderPunchRow("ClockIn", undefined, false)}
        </View>
      ) : (
        session.segments.map((segment, index) => {
          const open = Boolean(segment.clockIn && !segment.clockOut);
          const worked = segment.workedMinutes ?? segment.durationMinutes;
          return (
            <View key={segment.segmentNumber || index} style={styles.segment}>
              <View style={styles.segmentHead}>
                <Text style={styles.segmentTitle}>{t("managedRecords.detail.segmentTitle", { number: segment.segmentNumber || index + 1 })}</Text>
                <Text style={styles.segmentTitle}>
                  {[worked ? `${Math.floor(worked / 60)}h${String(worked % 60).padStart(2, "0")}m` : "", open && isToday ? t("managedRecords.detail.ongoing") : ""].filter(Boolean).join(" · ")}
                </Text>
              </View>
              {renderPunchRow("ClockIn", segment.clockIn, open)}
              <View style={styles.divider} />
              {renderPunchRow("ClockOut", segment.clockOut, open)}
            </View>
          );
        })
      )}

      {pendingApprovals.map((item) => {
        const text = item.adjustment
          ? `${t(`punchTypes.${item.adjustment.punchType}`, item.adjustment.punchType)} → ${clockText(item.adjustment.requestedPunchTimeLocal) ?? "--:--"}${item.adjustment.reason ? ` · ${item.adjustment.reason}` : ""}`
          : t(`sourceTypes.${item.sourceType}`, item.sourceType);
        return (
          <Pressable
            key={item.approvalGuid}
            accessibilityRole="button"
            disabled={!onOpenApprovals}
            onPress={onOpenApprovals}
            style={styles.pending}
          >
            <Text variant="bodySmall" style={styles.pendingText} numberOfLines={2}>{t("managedRecords.detail.pendingFix", { text })}</Text>
            {onOpenApprovals ? <Text variant="labelMedium" style={styles.pendingAction}>{t("managedRecords.detail.openApprovals")}</Text> : null}
          </Pressable>
        );
      })}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  muted: { color: HB_COLORS.textSecondary },
  stats: { flexDirection: "row", gap: HB_SPACING.xs },
  stat: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: 10, flex: 1, paddingHorizontal: 10, paddingVertical: 8 },
  statValue: { color: HB_COLORS.textPrimary, fontSize: 17, fontVariant: ["tabular-nums"], fontWeight: "700" },
  timeline: { marginTop: HB_SPACING.sm },
  ticks: { height: 18, marginTop: 4 },
  tick: { color: "#98A2B3", fontSize: 10.5, fontVariant: ["tabular-nums"], marginLeft: -8, position: "absolute", textAlign: "center", width: 16 },
  segment: {
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    marginTop: HB_SPACING.sm,
    overflow: "hidden",
  },
  segmentHead: {
    backgroundColor: "#FCFCFD",
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    justifyContent: "space-between",
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 7,
  },
  segmentTitle: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700" },
  segmentEmpty: { paddingHorizontal: HB_SPACING.sm, paddingTop: HB_SPACING.sm },
  divider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, marginHorizontal: HB_SPACING.sm },
  punchRow: { alignItems: "center", flexDirection: "row", gap: 10, paddingHorizontal: HB_SPACING.sm, paddingVertical: 10 },
  punchKind: { color: HB_COLORS.textSecondary, fontSize: 12, width: 32 },
  punchBody: { flex: 1, gap: 2, minWidth: 0 },
  inline: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 6 },
  punchTime: { color: HB_COLORS.textPrimary, fontSize: 16, fontVariant: ["tabular-nums"], fontWeight: "700" },
  punchTimeMissing: { color: "#98A2B3" },
  punchSub: { color: HB_COLORS.textSecondary, fontSize: 12 },
  pill: { borderRadius: 5, paddingHorizontal: 6, paddingVertical: 1 },
  pillText: { fontSize: 11, fontWeight: "700" },
  editButton: { borderColor: "#B2DDFF", borderRadius: 8, borderWidth: 1, paddingHorizontal: 10, paddingVertical: 4 },
  editText: { color: HB_COLORS.action, fontSize: 12.5, fontWeight: "600" },
  pending: {
    alignItems: "center",
    backgroundColor: "#EFF8FF",
    borderColor: "#B2DDFF",
    borderRadius: 10,
    borderWidth: 1,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    marginTop: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 9,
  },
  pendingText: { color: "#175CD3", flex: 1 },
  pendingAction: { color: "#175CD3", fontWeight: "700" },
  footer: { gap: HB_SPACING.xs },
  footerNote: { color: HB_COLORS.textSecondary, textAlign: "center" },
  footerButtons: { flexDirection: "row", gap: HB_SPACING.sm },
  footerButton: { flex: 1 },
});
