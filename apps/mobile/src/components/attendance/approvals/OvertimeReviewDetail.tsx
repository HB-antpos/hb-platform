import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, Text, TextInput } from "react-native-paper";
import { useQuery } from "@tanstack/react-query";
import { getManagedAttendanceRecords } from "@/modules/attendance/api";
import { validateAttendanceOvertimeApproval } from "@/modules/attendance/attendance-approval";
import {
  buildApprovalTimeline,
  defaultOvertimeMinutes,
  formatClockMinutes,
  formatDurationMinutes,
  listOvertimeMinuteOptions,
  resolveWorkDateTier,
} from "@/modules/attendance/approval-board";
import type { AttendanceApproval, AttendanceApprovalPayload } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ApprovalTimelineBar } from "./ApprovalTimelineBar";
import { ApprovalDetailFrame, DetailCard, OVERTIME_TONE, PersonAvatar } from "./approval-ui";
import { useApprovalDateLabels } from "./use-approval-labels";

/**
 * 加班审核详情：排班与实际打卡叠在一条时间轴上，批准分钟只能选 15 的倍数；
 * 少批或拒绝必须写备注（沿用 validateAttendanceOvertimeApproval 的现有规则）。
 */
export function OvertimeReviewDetail({
  approval,
  storeCode,
  storeName,
  canReview,
  isBusy,
  onClose,
  onApprove,
  onReject,
}: {
  approval: AttendanceApproval;
  storeCode?: string;
  storeName?: string;
  canReview: boolean;
  isBusy: boolean;
  onClose: () => void;
  onApprove: (payload: AttendanceApprovalPayload) => Promise<void>;
  onReject: (payload: AttendanceApprovalPayload) => Promise<void>;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const { weekdayDate, tierLabel } = useApprovalDateLabels();
  const workDate = approval.workDate?.slice(0, 10) ?? "";
  const candidate = approval.candidateOvertimeMinutes ?? 0;
  const [minutes, setMinutes] = useState(defaultOvertimeMinutes(candidate));
  const [remark, setRemark] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    setMinutes(defaultOvertimeMinutes(candidate));
    setRemark("");
    setError("");
  }, [approval.approvalGuid, candidate]);

  // 加班审批的 sourceGuid 就是排班 GUID；取当天打卡记录找到这一班。
  const recordsQuery = useQuery({
    queryKey: ["attendance", "managed", "records", storeCode ?? approval.storeCode ?? "", workDate],
    queryFn: () => getManagedAttendanceRecords({ storeCode: (storeCode ?? approval.storeCode)!, workDate }),
    enabled: Boolean((storeCode ?? approval.storeCode) && workDate),
  });
  const session = recordsQuery.data?.find((item) => item.scheduleGuid === approval.sourceGuid);
  const timeline = useMemo(() => (session ? buildApprovalTimeline(session) : undefined), [session]);
  const tier = workDate ? resolveWorkDateTier(workDate, approval.holidayName ?? session?.holidayName) : "weekday";
  const options = listOvertimeMinuteOptions(candidate);
  const scheduledMinutes = timeline?.schedule ? timeline.schedule.end - timeline.schedule.start : undefined;
  const workedMinutes = session?.workedMinutes
    ?? timeline?.segments.reduce((sum, segment) => sum + (segment.durationMinutes ?? 0), 0);

  const submit = async (action: "approve" | "reject") => {
    const approvedMinutes = action === "reject" ? 0 : minutes;
    const validation = validateAttendanceOvertimeApproval({ candidateMinutes: candidate, approvedMinutes, action, remark });
    if (validation) {
      setError(t(`approvals.overtimeValidation.${validation}`));
      return;
    }
    const payload = { approvalGuid: approval.approvalGuid, remark, approvedOvertimeMinutes: approvedMinutes };
    try {
      await (action === "approve" ? onApprove(payload) : onReject(payload));
      onClose();
    } catch {
      // 失败提示由列表页统一弹出；详情保持打开，方便改完重试。
    }
  };

  return (
    <ApprovalDetailFrame
      visible
      title={t("approvals.board.overtimeTitle")}
      statusLabel={t("approvals.board.statusPending")}
      onClose={onClose}
      footer={canReview ? (
        <>
          <Button mode="outlined" textColor={HB_COLORS.danger} style={[styles.footerButton, styles.rejectButton]} disabled={isBusy} onPress={() => void submit("reject")}>
            {t("actions.reject")}
          </Button>
          <Button mode="contained" style={styles.primaryButton} disabled={isBusy} loading={isBusy} onPress={() => void submit("approve")}>
            {t("approvals.board.approveMinutes", { minutes })}
          </Button>
        </>
      ) : undefined}
    >
      <DetailCard>
        <View style={styles.personRow}>
          <PersonAvatar name={approval.employeeName} size={44} />
          <View style={styles.personBody}>
            <Text variant="titleMedium" style={styles.strong} numberOfLines={1}>{approval.employeeName}</Text>
            <Text variant="bodySmall" style={styles.muted} numberOfLines={2}>
              {[weekdayDate(workDate), storeName ?? approval.storeName].filter(Boolean).join(" · ")}
              {tier !== "weekday" ? <Text variant="bodySmall" style={styles.tierText}>{` · ${tierLabel(tier)}`}</Text> : null}
            </Text>
          </View>
          <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.systemGenerated")}</Text>
        </View>
      </DetailCard>

      <DetailCard
        title={t("approvals.board.dayPunches")}
        aside={session?.segmentLimit
          ? t("approvals.board.segmentLimit", { count: session.segments.length, limit: session.segmentLimit })
          : undefined}
      >
        {recordsQuery.isLoading ? <ActivityIndicator /> : null}
        {timeline ? (
          <>
            <ApprovalTimelineBar
              timeline={timeline}
              mode="overtime"
              scheduleLabel={t("approvals.board.timelineSchedule")}
              actualLabel={t("approvals.board.timelineActual")}
            />
            {timeline.segments.map((segment, index) => {
              const gap = timeline.gaps.find((item) => item.afterSegment === segment.number);
              return (
                <View key={segment.number}>
                  <View style={styles.segmentRow}>
                    <Text variant="bodyMedium" style={styles.segmentIndex}>{`#${segment.number}`}</Text>
                    <Text variant="bodyLarge" style={styles.segmentTime}>
                      {`${formatClockMinutes(segment.start)} → ${segment.end !== undefined ? formatClockMinutes(segment.end) : "--:--"}`}
                    </Text>
                    <Text variant="bodyLarge" style={styles.tabular}>
                      {segment.durationMinutes !== undefined ? formatDurationMinutes(segment.durationMinutes) : ""}
                    </Text>
                  </View>
                  {gap && index < timeline.segments.length - 1 && gap.countsAsMeal ? (
                    <View style={styles.gapRow}>
                      <Icon source="silverware-fork-knife" size={14} color={HB_COLORS.success} />
                      <Text variant="bodySmall" style={styles.gapText}>
                        {t("approvals.board.gapCountsAsMeal", { minutes: gap.minutes })}
                      </Text>
                    </View>
                  ) : null}
                </View>
              );
            })}
          </>
        ) : !recordsQuery.isLoading ? (
          <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.timelineUnavailable")}</Text>
        ) : null}
      </DetailCard>

      <View style={styles.statRow}>
        <View style={styles.stat}>
          <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.statSchedule")}</Text>
          <Text variant="titleLarge" style={styles.tabular}>{scheduledMinutes !== undefined ? formatDurationMinutes(scheduledMinutes) : "--"}</Text>
        </View>
        <View style={styles.stat}>
          <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.statWorked")}</Text>
          <Text variant="titleLarge" style={styles.tabular}>{workedMinutes !== undefined ? formatDurationMinutes(workedMinutes) : "--"}</Text>
        </View>
        <View style={[styles.stat, styles.statHighlight]}>
          <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.statCandidate")}</Text>
          <Text variant="titleLarge" style={[styles.tabular, { color: OVERTIME_TONE.text }]}>
            {t("approvals.board.minutesValue", { minutes: candidate })}
          </Text>
        </View>
      </View>

      <DetailCard
        title={t("approvals.board.approveMinutesTitle")}
        aside={session?.earlyOvertimeMinutes !== undefined || session?.lateOvertimeMinutes !== undefined
          ? t("approvals.board.overtimeSplit", { early: session?.earlyOvertimeMinutes ?? 0, late: session?.lateOvertimeMinutes ?? 0 })
          : t("approvals.board.roundTo15")}
      >
        <View style={styles.optionGrid}>
          {options.map((option) => {
            const selected = option === minutes;
            return (
              <Pressable
                key={option}
                accessibilityRole="radio"
                accessibilityState={{ selected, disabled: !canReview }}
                disabled={!canReview}
                onPress={() => { setMinutes(option); setError(""); }}
                style={[styles.option, selected ? styles.optionSelected : null]}
              >
                <Text variant="titleMedium" style={[styles.tabular, selected ? styles.optionSelectedText : null]}>{option}</Text>
              </Pressable>
            );
          })}
        </View>
        <TextInput
          mode="outlined"
          dense
          value={remark}
          onChangeText={(value) => { setRemark(value); setError(""); }}
          placeholder={t("approvals.board.remarkWhenReduced", { minutes: candidate })}
          error={Boolean(error)}
          disabled={!canReview}
        />
        {error ? <Text variant="bodySmall" style={styles.error}>{error}</Text> : null}
      </DetailCard>
    </ApprovalDetailFrame>
  );
}

const styles = StyleSheet.create({
  error: { color: HB_COLORS.danger },
  footerButton: { flex: 1 },
  gapRow: { alignItems: "center", flexDirection: "row", gap: 4, paddingLeft: 52, paddingVertical: 2 },
  gapText: { color: HB_COLORS.success },
  muted: { color: HB_COLORS.textSecondary },
  option: {
    alignItems: "center",
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    flexBasis: "22%",
    flexGrow: 1,
    paddingVertical: HB_SPACING.sm,
  },
  optionGrid: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  optionSelected: { backgroundColor: "#EFF8FF", borderColor: "#1570EF", borderWidth: 2 },
  optionSelectedText: { color: "#175CD3", fontWeight: "700" },
  personBody: { flex: 1, minWidth: 0 },
  personRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm },
  primaryButton: { flex: 1.8 },
  rejectButton: { borderColor: "#FDA29B" },
  segmentIndex: { color: HB_COLORS.textSecondary, width: 40 },
  segmentRow: { alignItems: "center", flexDirection: "row", paddingVertical: 4 },
  segmentTime: { flex: 1, fontVariant: ["tabular-nums"] },
  stat: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    flex: 1,
    gap: 2,
    padding: HB_SPACING.sm,
  },
  statHighlight: { backgroundColor: OVERTIME_TONE.background, borderColor: "#FEDF89" },
  statRow: { flexDirection: "row", gap: HB_SPACING.xs },
  strong: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  tabular: { fontVariant: ["tabular-nums"] },
  tierText: { color: OVERTIME_TONE.text, fontWeight: "600" },
});
