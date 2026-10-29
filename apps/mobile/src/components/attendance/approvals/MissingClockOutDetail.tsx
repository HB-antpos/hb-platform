import { useEffect, useMemo, useRef, useState } from "react";
import { StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, Text, TextInput } from "react-native-paper";
import { useQuery } from "@tanstack/react-query";
import {
  createManagedAttendancePunchAdjustment,
  getManagedAttendanceRecords,
  previewManagedAttendancePunchAdjustment,
} from "@/modules/attendance/api";
import {
  buildApprovalTimeline,
  clockToMinutes,
  formatClockMinutes,
  formatDurationMinutes,
  minutesFromWorkDay,
  resolveMissingClockOutTimes,
  workDayMinutesToLocal,
} from "@/modules/attendance/approval-board";
import {
  buildAttendancePunchAdjustmentFingerprint,
  buildAttendancePunchAdjustmentPayload,
} from "@/modules/attendance/attendance-punch-adjustment";
import type {
  AttendanceAdjustmentPreview,
  AttendanceApproval,
  AttendanceApprovalPayload,
  AttendanceManagedPunchAdjustmentPayload,
} from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { PunchTimeStepper } from "../AdjustmentFormControls";
import { ApprovalTimelineBar } from "./ApprovalTimelineBar";
import { ApprovalDetailFrame, DetailCard } from "./approval-ui";
import { useApprovalDateLabels } from "./use-approval-labels";

const PREVIEW_DEBOUNCE_MS = 400;

/**
 * 漏下班处理（午夜自动下班）：店长用步进器填实际下班时间 → 补一张下班卡并结案；
 * 「不计这段」＝拒绝该待处理（按 0 工时结案），必须写原因。
 * 计薪预览走服务端补卡预览，与最终保存同一套口径；提交时用最新表单重新预览拿版本号再保存。
 */
export function MissingClockOutDetail({
  approval,
  storeCode,
  canReview,
  canAdjust,
  isBusy,
  onClose,
  onReject,
  onResolved,
}: {
  approval: AttendanceApproval;
  storeCode?: string;
  canReview: boolean;
  /** Attendance.Punch.AdjustManagedStore：补下班卡需要。 */
  canAdjust: boolean;
  isBusy: boolean;
  onClose: () => void;
  onReject: (payload: AttendanceApprovalPayload) => Promise<void>;
  /** 补卡成功：列表页刷新待审与员工数据。 */
  onResolved: (message: string) => Promise<void> | void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const { weekdayDate } = useApprovalDateLabels();
  const workDate = approval.workDate?.slice(0, 10) ?? "";
  const effectiveStore = storeCode ?? approval.storeCode;
  const recordsQuery = useQuery({
    queryKey: ["attendance", "managed", "records", effectiveStore ?? "", workDate],
    queryFn: () => getManagedAttendanceRecords({ storeCode: effectiveStore!, workDate }),
    enabled: Boolean(effectiveStore && workDate),
  });
  // 漏下班审批的 sourceGuid 是排班 GUID。
  const session = recordsQuery.data?.find((item) => item.scheduleGuid === approval.sourceGuid);
  const times = useMemo(() => (session ? resolveMissingClockOutTimes(session) : undefined), [session]);
  const autoClockOutAt = times?.autoClockOut ?? 1440;

  const [clockOutLocal, setClockOutLocal] = useState("");
  const [reason, setReason] = useState("");
  const [error, setError] = useState("");
  const [preview, setPreview] = useState<AttendanceAdjustmentPreview>();
  const [submitting, setSubmitting] = useState(false);
  const latestFingerprintRef = useRef("");

  // 默认下班时间＝排班结束时间；拿到班次后只初始化一次。
  useEffect(() => {
    if (!session || clockOutLocal) return;
    const scheduleEnd = clockToMinutes(session.endTime ?? approval.scheduleEndTime) ?? 17 * 60;
    setClockOutLocal(workDayMinutesToLocal(workDate, scheduleEnd));
  }, [approval.scheduleEndTime, clockOutLocal, session, workDate]);

  const clockOutMinutes = minutesFromWorkDay(clockOutLocal, workDate);
  const timeline = useMemo(
    () => (session
      ? buildApprovalTimeline(session, { overrideLastClockOut: clockOutMinutes, extendRangeTo: autoClockOutAt })
      : undefined),
    [autoClockOutAt, clockOutMinutes, session],
  );
  // 补卡的原因：店长填写的原因；预览阶段还没填时用固定说明，保存前会按真实原因重新预览。
  const buildPayload = (reasonText: string): AttendanceManagedPunchAdjustmentPayload => ({
    ...buildAttendancePunchAdjustmentPayload({
      storeCode: effectiveStore,
      scheduleGuid: session?.scheduleGuid,
      // 有系统自动下班卡就改它；旧数据没有自动卡就补一张新的下班卡。
      originalPunchGuid: times?.autoClockOutPunchGuid,
      punchType: "ClockOut",
      requestedPunchTimeLocal: clockOutLocal,
      reason: reasonText,
    }),
    userGuid: session?.userGuid ?? "",
  });
  const previewReason = reason.trim() || t("approvals.board.missingClockOut.previewReason");

  useEffect(() => {
    if (!session || !clockOutLocal || !canAdjust) return undefined;
    const payload = buildPayload(previewReason);
    const fingerprint = buildAttendancePunchAdjustmentFingerprint(payload);
    latestFingerprintRef.current = fingerprint;
    const timer = setTimeout(() => {
      previewManagedAttendancePunchAdjustment(payload)
        .then((result) => {
          // 只采用与当前表单一致的预览，避免步进器连点时旧结果覆盖新结果。
          if (latestFingerprintRef.current === fingerprint) setPreview(result);
        })
        .catch(() => {
          if (latestFingerprintRef.current === fingerprint) setPreview(undefined);
        });
    }, PREVIEW_DEBOUNCE_MS);
    return () => clearTimeout(timer);
    // buildPayload 只依赖下列状态，按值触发即可。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [canAdjust, clockOutLocal, previewReason, session?.scheduleGuid, times?.autoClockOutPunchGuid]);

  const proposed = preview?.proposedSession;
  const clockOutText = clockOutMinutes !== undefined ? formatClockMinutes(clockOutMinutes) : "--:--";

  const handleResolve = async () => {
    if (!reason.trim()) {
      setError(t("approvals.board.missingClockOut.reasonRequired"));
      return;
    }
    if (!session) return;
    setSubmitting(true);
    try {
      const payload = buildPayload(reason.trim());
      const fresh = await previewManagedAttendancePunchAdjustment(payload);
      if (!fresh.isValid || !fresh.previewRevision) {
        setError(fresh.validationMessage || t("adjustment.messages.previewFailed"));
        return;
      }
      await createManagedAttendancePunchAdjustment({ ...payload, previewRevision: fresh.previewRevision });
      await onResolved(t("approvals.board.missingClockOut.resolved", { time: clockOutText }));
      onClose();
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : t("adjustment.messages.submitFailed"));
    } finally {
      setSubmitting(false);
    }
  };

  const handleExclude = async () => {
    if (!reason.trim()) {
      setError(t("approvals.board.missingClockOut.reasonRequired"));
      return;
    }
    try {
      await onReject({ approvalGuid: approval.approvalGuid, remark: reason.trim() });
      onClose();
    } catch {
      // 列表页统一提示失败，详情保持打开。
    }
  };

  const clockInText = times?.clockIn !== undefined ? formatClockMinutes(times.clockIn) : "--:--";
  const busy = isBusy || submitting;

  return (
    <ApprovalDetailFrame
      visible
      title={t("approvals.board.missingClockOut.title")}
      statusLabel={t("approvals.board.statusNeedsAction")}
      statusTone="danger"
      onClose={onClose}
      footer={canReview ? (
        <>
          <Button mode="outlined" textColor={HB_COLORS.danger} style={[styles.secondaryButton, styles.rejectButton]} disabled={busy} onPress={() => void handleExclude()}>
            {t("approvals.board.missingClockOut.exclude")}
          </Button>
          <Button
            mode="contained"
            style={styles.primaryButton}
            disabled={busy || !canAdjust || !session}
            loading={submitting}
            onPress={() => void handleResolve()}
          >
            {t("approvals.board.missingClockOut.resolve", { time: clockOutText })}
          </Button>
        </>
      ) : undefined}
    >
      <DetailCard tone="danger">
        <View style={styles.alertRow}>
          <Icon source="weather-night" size={22} color={HB_COLORS.danger} />
          <View style={styles.alertBody}>
            <Text variant="titleSmall" style={styles.alertTitle}>
              {t("approvals.board.missingClockOut.alertTitle", { time: formatClockMinutes(autoClockOutAt) })}
            </Text>
            <Text variant="bodyMedium" style={styles.alertText}>
              {t("approvals.board.missingClockOut.alertBody", {
                name: approval.employeeName ?? "",
                date: weekdayDate(workDate),
                time: clockInText,
              })}
            </Text>
          </View>
        </View>
      </DetailCard>

      <DetailCard
        title={t("approvals.board.dayPunches")}
        aside={session ? t("approvals.board.scheduleWindow", {
          start: session.startTime.slice(0, 5),
          end: session.endTime.slice(0, 5),
        }) : undefined}
      >
        {recordsQuery.isLoading ? <ActivityIndicator /> : null}
        {timeline ? (
          <>
            <ApprovalTimelineBar timeline={timeline} mode="missingClockOut" autoClockOutAt={autoClockOutAt} />
            <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.missingClockOut.legend")}</Text>
          </>
        ) : !recordsQuery.isLoading ? (
          <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.timelineUnavailable")}</Text>
        ) : null}
      </DetailCard>

      <DetailCard title={t("approvals.board.missingClockOut.actualTitle")} aside={t("approvals.board.storeTime")}>
        {clockOutLocal ? (
          <PunchTimeStepper
            value={clockOutLocal}
            workDate={workDate}
            onChange={(value) => { setClockOutLocal(value); setError(""); }}
          />
        ) : null}
        <TextInput
          mode="outlined"
          dense
          value={reason}
          onChangeText={(value) => { setReason(value); setError(""); }}
          placeholder={t("approvals.board.missingClockOut.reasonPlaceholder")}
          error={Boolean(error)}
          maxLength={200}
        />
        {error ? <Text variant="bodySmall" style={styles.error}>{error}</Text> : null}
        {!canAdjust ? (
          <Text variant="bodySmall" style={styles.warning}>{t("approvals.board.missingClockOut.needsAdjustPermission")}</Text>
        ) : null}
        <View style={styles.divider} />
        <View style={styles.previewRow}>
          <Text variant="bodyMedium" style={styles.previewText}>
            {proposed
              ? t("approvals.board.missingClockOut.previewWorked", {
                worked: formatDurationMinutes(proposed.workedMinutes ?? 0),
                meal: proposed.mealDeductionMinutes ?? 0,
              })
              : t("approvals.board.missingClockOut.previewPending")}
          </Text>
          {proposed?.paidMinutes !== undefined ? (
            <Text variant="bodyMedium" style={styles.previewPaid}>
              {t("approvals.board.missingClockOut.previewPaid", { paid: formatDurationMinutes(proposed.paidMinutes) })}
            </Text>
          ) : null}
        </View>
      </DetailCard>
    </ApprovalDetailFrame>
  );
}

const styles = StyleSheet.create({
  alertBody: { flex: 1, gap: 4 },
  alertRow: { flexDirection: "row", gap: HB_SPACING.sm },
  alertText: { color: HB_COLORS.danger },
  alertTitle: { color: HB_COLORS.danger, fontWeight: "700" },
  divider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: 1, borderStyle: "dashed" },
  error: { color: HB_COLORS.danger },
  muted: { color: HB_COLORS.textSecondary },
  previewPaid: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], fontWeight: "700" },
  previewRow: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  previewText: { color: HB_COLORS.textPrimary, flex: 1, fontVariant: ["tabular-nums"] },
  primaryButton: { flex: 1.8 },
  rejectButton: { borderColor: "#FDA29B" },
  secondaryButton: { flex: 1 },
  warning: { color: HB_COLORS.warning },
});
