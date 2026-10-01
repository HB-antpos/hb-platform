import { useEffect, useMemo, useRef, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Chip, Icon, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import {
  buildAdjustmentReason,
  isWithinManagedAdjustmentWindow,
  listSessionPunches,
  resolveDefaultMissingPunchTime,
  resolveDefaultMissingPunchType,
} from "@/modules/attendance/attendance-managed-records";
import {
  buildAttendancePunchAdjustmentFingerprint,
  buildAttendancePunchAdjustmentPayload,
  createAttendanceAdjustmentRequestGate,
  runLatestAttendanceAdjustmentRequest,
  validateAttendancePunchAdjustment,
} from "@/modules/attendance/attendance-punch-adjustment";
import {
  resolveAttendancePunchDisplayTime,
  toAttendanceDeviceLocalTime,
} from "@/modules/attendance/attendance-device-time";
import type {
  AttendanceAdjustmentPreview,
  AttendanceManagedPunchAdjustmentPayload,
  AttendancePunch,
  AttendancePunchType,
  AttendanceScheduleSession,
} from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  AdjustmentReasonPicker,
  ATTENDANCE_STATUS_TONES,
  PunchTimeStepper,
  StatusPill,
} from "./AdjustmentFormControls";

const MANAGED_REASON_KEYS = ["missed", "device", "shiftChanged", "other"] as const;

function punchLocalTime(punch: AttendancePunch) {
  const value = punch.punchTimeUtc
    ? toAttendanceDeviceLocalTime(punch.punchTimeUtc)
    : punch.punchTimeLocal ?? punch.effectivePunchTime ?? "";
  return value.slice(0, 16);
}

function formatTime(value?: string) {
  if (!value) return "--:--";
  const timePart = value.includes("T") ? value.split("T").pop() : value;
  return timePart?.slice(0, 5) || "--:--";
}

/**
 * 店长修改单个员工某班次的打卡：点选已有打卡改时间，或补录缺失的上/下班卡。
 * 与员工补卡共用预览指纹与请求隔离器，保存前必须拿到与当前表单一致的服务端预览版本。
 */
export function ManagedPunchEditSheet({
  visible,
  session,
  today,
  canAdjust,
  isBusy,
  onPreview,
  onSubmit,
  onDismiss,
}: {
  visible: boolean;
  session?: AttendanceScheduleSession;
  today: string;
  canAdjust: boolean;
  isBusy: boolean;
  onPreview: (payload: AttendanceManagedPunchAdjustmentPayload) => Promise<AttendanceAdjustmentPreview>;
  onSubmit: (payload: AttendanceManagedPunchAdjustmentPayload) => Promise<void>;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const workDate = session?.workDate.slice(0, 10) ?? today;
  const [originalPunchGuid, setOriginalPunchGuid] = useState<string>();
  const [punchType, setPunchType] = useState<AttendancePunchType>("ClockIn");
  const [requestedPunchTimeLocal, setRequestedPunchTimeLocal] = useState("");
  const [reasonKey, setReasonKey] = useState<string>();
  const [reasonNote, setReasonNote] = useState("");
  const [preview, setPreview] = useState<AttendanceAdjustmentPreview>();
  const [previewFingerprint, setPreviewFingerprint] = useState<string>();
  const [localError, setLocalError] = useState("");
  const latestFingerprintRef = useRef("");
  const previewGateRef = useRef(createAttendanceAdjustmentRequestGate());
  const submitGateRef = useRef(createAttendanceAdjustmentRequestGate());
  const punches = useMemo(() => (session ? listSessionPunches(session) : []), [session]);
  const isWithinWindow = isWithinManagedAdjustmentWindow(workDate, today);
  const isEditable = canAdjust && isWithinWindow;

  // 换员工/班次或重新打开时回到「补录缺失卡」的默认草稿，避免带着上一个人的修改提交。
  useEffect(() => {
    if (!visible || !session) return;
    const defaultType = resolveDefaultMissingPunchType(session);
    setOriginalPunchGuid(undefined);
    setPunchType(defaultType);
    setRequestedPunchTimeLocal(resolveDefaultMissingPunchTime(session, defaultType));
    setReasonKey(undefined);
    setReasonNote("");
    setPreview(undefined);
    setPreviewFingerprint(undefined);
    setLocalError("");
    previewGateRef.current.invalidate();
    submitGateRef.current.invalidate();
    // 只按「打开 + 班次标识」重置；记录列表后台刷新换了 session 引用时不能清掉正在编辑的草稿。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible, session?.scheduleGuid]);

  const reasonPresets = MANAGED_REASON_KEYS.map((key) => ({ key, label: t(`adjustment.reasons.${key}`) }));
  const reasonPresetLabel = reasonPresets.find((preset) => preset.key === reasonKey)?.label;
  const reason = buildAdjustmentReason(reasonPresetLabel, reasonNote);
  const isNoteRequired = reasonKey === "other" && !reasonNote.trim();
  const payload = useMemo<AttendanceManagedPunchAdjustmentPayload>(() => ({
    ...buildAttendancePunchAdjustmentPayload({
      storeCode: session?.storeCode,
      scheduleGuid: session?.scheduleGuid,
      originalPunchGuid,
      punchType,
      requestedPunchTimeLocal,
      reason,
    }),
    userGuid: session?.userGuid ?? "",
  }), [originalPunchGuid, punchType, reason, requestedPunchTimeLocal, session]);
  // 指纹额外带上员工，防止同一班次结构的两个员工之间误用预览。
  const fingerprint = `${payload.userGuid}|${buildAttendancePunchAdjustmentFingerprint(payload)}`;
  latestFingerprintRef.current = fingerprint;
  const missingFields = validateAttendancePunchAdjustment(payload);
  const canSubmit = Boolean(
    isEditable
    && preview?.isValid
    && preview.previewRevision
    && previewFingerprint === fingerprint,
  );

  const updateDraft = (update: () => void) => {
    update();
    setPreview(undefined);
    setPreviewFingerprint(undefined);
    setLocalError("");
    previewGateRef.current.invalidate();
    submitGateRef.current.invalidate();
  };

  const selectPunch = (punch: AttendancePunch) => updateDraft(() => {
    setOriginalPunchGuid(punch.punchGuid || undefined);
    setPunchType(punch.punchType === "ClockOut" ? "ClockOut" : "ClockIn");
    setRequestedPunchTimeLocal(punchLocalTime(punch) || requestedPunchTimeLocal);
  });

  const startAddMissing = (type: AttendancePunchType) => updateDraft(() => {
    setOriginalPunchGuid(undefined);
    setPunchType(type);
    if (session) setRequestedPunchTimeLocal(resolveDefaultMissingPunchTime(session, type));
  });

  const handlePreview = async () => {
    if (isNoteRequired) {
      setLocalError(t("adjustment.noteRequired"));
      return;
    }
    if (missingFields.length || !payload.userGuid) {
      setLocalError(t("adjustment.validation.required"));
      return;
    }
    const request = previewGateRef.current.begin(fingerprint);
    await runLatestAttendanceAdjustmentRequest({
      gate: previewGateRef.current,
      request,
      getCurrentFingerprint: () => latestFingerprintRef.current,
      operation: () => onPreview({ ...payload, reason: payload.reason.trim() }),
      onSuccess: (result) => {
        setPreview(result);
        setPreviewFingerprint(request.fingerprint);
      },
      onError: (error) => {
        setLocalError(error instanceof Error ? error.message : t("adjustment.messages.previewFailed"));
      },
    });
  };

  const handleSubmit = async () => {
    if (!canSubmit || !preview?.previewRevision) return;
    const request = submitGateRef.current.begin(fingerprint);
    await runLatestAttendanceAdjustmentRequest({
      gate: submitGateRef.current,
      request,
      getCurrentFingerprint: () => latestFingerprintRef.current,
      operation: () => onSubmit({
        ...payload,
        reason: payload.reason.trim(),
        previewRevision: preview.previewRevision,
      }),
      onSuccess: () => onDismiss(),
      onError: (error) => {
        setLocalError(error instanceof Error ? error.message : t("adjustment.messages.submitFailed"));
      },
    });
  };

  return (
    <BusinessSheet
      visible={visible && Boolean(session)}
      title={`${session?.employeeName || session?.userGuid || ""} · ${
        isEditable ? t("managedRecords.editTitle") : t("managedRecords.viewTitle")
      }`}
      subtitle={t("managedRecords.editSubtitle", {
        date: workDate,
        start: formatTime(session?.startTime),
        end: formatTime(session?.endTime),
      })}
      onDismiss={onDismiss}
      footer={isEditable ? (
        <View style={styles.footer}>
          <Button
            mode="outlined"
            style={styles.footerButton}
            disabled={isBusy || Boolean(missingFields.length)}
            loading={isBusy && !preview}
            onPress={() => void handlePreview()}
          >
            {t("adjustment.preview")}
          </Button>
          <Button
            mode="contained"
            style={styles.footerButton}
            disabled={isBusy || !canSubmit}
            loading={isBusy && Boolean(preview)}
            onPress={() => void handleSubmit()}
          >
            {t("managedRecords.save")}
          </Button>
        </View>
      ) : undefined}
    >
      <View style={styles.content}>
        {!canAdjust ? (
          <Text variant="bodySmall" style={styles.muted}>{t("managedRecords.readOnly")}</Text>
        ) : !isWithinWindow ? (
          <Text variant="bodySmall" style={styles.warningText}>{t("managedRecords.outsideWindow")}</Text>
        ) : (
          <Text variant="bodySmall" style={styles.muted}>{t("managedRecords.selectHint")}</Text>
        )}

        <View style={styles.punchList}>
          {punches.map((punch) => {
            const selected = isEditable && originalPunchGuid === punch.punchGuid;
            return (
              <Pressable
                key={punch.punchGuid || `${punch.punchType}-${punchLocalTime(punch)}`}
                accessibilityRole="button"
                accessibilityState={{ disabled: !isEditable, selected }}
                disabled={!isEditable}
                onPress={() => selectPunch(punch)}
                style={[styles.punchRow, selected ? styles.punchRowSelected : null]}
              >
                <Text variant="bodyMedium" style={styles.punchTime}>
                  {formatTime(resolveAttendancePunchDisplayTime(punch))}
                </Text>
                <Text variant="bodyMedium" style={styles.flexText}>
                  {t(`punchTypes.${punch.punchType}`, punch.punchType)}
                </Text>
                {punch.adjustmentGuid ? <StatusPill label={t("adjustment.title")} tone="neutral" /> : null}
                <StatusPill
                  label={selected ? t("managedRecords.editing") : t(`statuses.${punch.status}`, punch.status)}
                  tone={selected ? "accent" : punch.status === "Normal" ? "success" : "warning"}
                />
                {isEditable ? <Icon source="pencil-outline" size={16} color={HB_COLORS.textSecondary} /> : null}
              </Pressable>
            );
          })}
          {!punches.length ? (
            <Text variant="bodySmall" style={styles.muted}>{t("today.noPunch")}</Text>
          ) : null}
        </View>

        {isEditable ? (
          <>
            <View style={styles.chipRow}>
              {(["ClockIn", "ClockOut"] as const).map((type) => (
                <Chip
                  key={type}
                  compact
                  icon="plus"
                  selected={!originalPunchGuid && punchType === type}
                  showSelectedCheck={false}
                  style={!originalPunchGuid && punchType === type ? styles.chipSelected : styles.chip}
                  onPress={() => startAddMissing(type)}
                >
                  {t("managedRecords.addMissing", { punchType: t(`punchTypes.${type}`) })}
                </Chip>
              ))}
            </View>

            <View style={styles.section}>
              <Text variant="labelMedium" style={styles.muted}>{t("adjustment.time")}</Text>
              <PunchTimeStepper
                value={requestedPunchTimeLocal}
                workDate={workDate}
                onChange={(value) => updateDraft(() => setRequestedPunchTimeLocal(value))}
              />
            </View>

            <View style={styles.section}>
              <Text variant="labelMedium" style={styles.muted}>{t("adjustment.reason")}</Text>
              <AdjustmentReasonPicker
                presets={reasonPresets}
                selectedKey={reasonKey}
                note={reasonNote}
                showNoteRequired={Boolean(localError) && isNoteRequired}
                onSelect={(key) => updateDraft(() => setReasonKey(key))}
                onNoteChange={(value) => updateDraft(() => setReasonNote(value))}
              />
            </View>

            {preview ? (
              <View style={[styles.previewBox, !preview.isValid ? styles.previewInvalid : null]}>
                {preview.proposedSession ? (
                  <Text variant="bodySmall">
                    {t("adjustment.previewWorked", {
                      before: preview.existingSession?.workedMinutes ?? 0,
                      after: preview.proposedSession.workedMinutes ?? 0,
                      delta: preview.workedMinutesDelta,
                    })}
                  </Text>
                ) : null}
                {preview.proposedSession ? (
                  <Text variant="bodySmall">
                    {t("adjustment.previewOvertime", {
                      before: preview.existingSession?.candidateOvertimeMinutes ?? 0,
                      after: preview.proposedSession.candidateOvertimeMinutes ?? 0,
                      delta: preview.candidateOvertimeMinutesDelta,
                    })}
                  </Text>
                ) : null}
                {!preview.isValid ? (
                  <Text variant="bodySmall" style={styles.dangerText}>
                    {preview.validationMessage || preview.validationErrorCode || t("adjustment.messages.invalid")}
                  </Text>
                ) : null}
              </View>
            ) : null}

            <View style={styles.auditNotice}>
              <Icon source="shield-check-outline" size={16} color={HB_COLORS.textSecondary} />
              <Text variant="bodySmall" style={[styles.muted, styles.flexText]}>
                {t("managedRecords.auditNotice")}
              </Text>
            </View>
            {localError ? <Text variant="bodySmall" style={styles.dangerText}>{localError}</Text> : null}
          </>
        ) : null}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  auditNotice: { alignItems: "flex-start", flexDirection: "row", gap: HB_SPACING.xs },
  chip: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outline, borderWidth: StyleSheet.hairlineWidth },
  chipRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  chipSelected: { backgroundColor: ATTENDANCE_STATUS_TONES.accent.background },
  content: { gap: HB_SPACING.md },
  dangerText: { color: HB_COLORS.danger },
  flexText: { flex: 1 },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  muted: { color: HB_COLORS.textSecondary },
  previewBox: { backgroundColor: "#ECFDF3", borderRadius: HB_RADIUS.control, gap: 4, padding: HB_SPACING.sm },
  previewInvalid: { backgroundColor: "#FEF3F2" },
  punchList: { gap: 6 },
  punchRow: {
    alignItems: "center",
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    minHeight: 44,
    paddingHorizontal: HB_SPACING.sm,
  },
  punchRowSelected: {
    backgroundColor: ATTENDANCE_STATUS_TONES.accent.background,
    borderColor: ATTENDANCE_STATUS_TONES.accent.text,
  },
  punchTime: { fontVariant: ["tabular-nums"], width: 48 },
  section: { gap: 6 },
  warningText: { color: HB_COLORS.warning },
});
