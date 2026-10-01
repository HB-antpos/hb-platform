import { useEffect, useMemo, useRef, useState } from "react";
import { StyleSheet, View } from "react-native";
import {
  Button,
  Card,
  Chip,
  Icon,
  SegmentedButtons,
  Text,
} from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import {
  buildAdjustmentReason,
  resolveDefaultMissingPunchTime,
  resolveDefaultMissingPunchType,
} from "@/modules/attendance/attendance-managed-records";
import {
  canRequestAttendanceAdjustmentForDate,
  buildAttendancePunchAdjustmentResetKey,
  buildAttendancePunchAdjustmentFingerprint,
  buildAttendancePunchAdjustmentPayload,
  createAttendanceAdjustmentRequestGate,
  runLatestAttendanceAdjustmentRequest,
  validateAttendancePunchAdjustment,
} from "@/modules/attendance/attendance-punch-adjustment";
import type {
  AttendanceAdjustmentPreview,
  AttendancePunchAdjustmentPayload,
  AttendancePunchType,
  AttendanceToday,
} from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  toAttendanceDeviceLocalTime,
  toAttendancePunchTimeUtc,
} from "@/modules/attendance/attendance-device-time";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { AdjustmentReasonPicker, PunchTimeStepper } from "./AdjustmentFormControls";

const SELF_REASON_KEYS = ["forgot", "scanFailed", "phoneDead", "other"] as const;

function toDateString(date: Date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function defaultLocalTime(workDate: string) {
  const now = new Date();
  return `${workDate}T${String(now.getHours()).padStart(2, "0")}:${String(now.getMinutes()).padStart(2, "0")}`;
}

function punchTime(punch: AttendanceToday["punches"][number]) {
  return punch.punchTimeUtc
    ? toAttendanceDeviceLocalTime(punch.punchTimeUtc)
    : punch.punchTimeLocal ?? punch.effectivePunchTime ?? "";
}

export function PunchAdjustmentCard({
  today,
  selectedDate,
  storeCode,
  storeName,
  isManagerStore,
  isBusy,
  onPreview,
  onSubmit,
}: {
  today?: AttendanceToday;
  selectedDate: string;
  storeCode?: string;
  storeName?: string;
  /** 管理该分店且持有「补卡与修改管理分店打卡」权限：本人补卡直接生效。 */
  isManagerStore: boolean;
  isBusy: boolean;
  onPreview: (payload: AttendancePunchAdjustmentPayload) => Promise<AttendanceAdjustmentPreview>;
  onSubmit: (payload: AttendancePunchAdjustmentPayload) => Promise<void>;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const [punchType, setPunchType] = useState<AttendancePunchType>("ClockIn");
  const [requestedPunchTimeLocal, setRequestedPunchTimeLocal] = useState(() => defaultLocalTime(selectedDate));
  const [sheetVisible, setSheetVisible] = useState(false);
  const [reasonKey, setReasonKey] = useState<string>();
  const [reasonNote, setReasonNote] = useState("");
  const [scheduleGuid, setScheduleGuid] = useState<string | undefined>();
  const [originalPunchGuid, setOriginalPunchGuid] = useState<string | undefined>();
  const [preview, setPreview] = useState<AttendanceAdjustmentPreview>();
  const [previewFingerprint, setPreviewFingerprint] = useState<string>();
  const [localError, setLocalError] = useState("");
  const latestPayloadFingerprintRef = useRef("");
  const previewRequestGateRef = useRef(createAttendanceAdjustmentRequestGate());
  const submitRequestGateRef = useRef(createAttendanceAdjustmentRequestGate());
  const selectableSchedules = useMemo(
    () => (today?.scheduleSessions ?? []).filter((session) => session.scheduleState !== "NoSchedule"),
    [today?.scheduleSessions],
  );
  const resetKey = buildAttendancePunchAdjustmentResetKey(
    selectedDate,
    storeCode,
    selectableSchedules,
  );

  useEffect(() => {
    setRequestedPunchTimeLocal(defaultLocalTime(selectedDate));
    setScheduleGuid(selectableSchedules[0]?.scheduleGuid);
    setOriginalPunchGuid(undefined);
    setPreview(undefined);
    setPreviewFingerprint(undefined);
    setLocalError("");
    previewRequestGateRef.current.invalidate();
    submitRequestGateRef.current.invalidate();
    // 默认补缺的那一张卡，并把时间放到班次起止，减少步进次数。
    const firstSchedule = selectableSchedules[0];
    if (firstSchedule) {
      const defaultType = resolveDefaultMissingPunchType(firstSchedule);
      setPunchType(defaultType);
      setRequestedPunchTimeLocal(resolveDefaultMissingPunchTime(firstSchedule, defaultType));
    }
  }, [resetKey]);

  const reasonPresets = SELF_REASON_KEYS.map((key) => ({ key, label: t(`adjustment.reasons.${key}`) }));
  const reasonPresetLabel = reasonPresets.find((preset) => preset.key === reasonKey)?.label;
  const reason = buildAdjustmentReason(reasonPresetLabel, reasonNote);
  const isNoteRequired = reasonKey === "other" && !reasonNote.trim();
  const isWithinSelfWindow = canRequestAttendanceAdjustmentForDate(
    selectedDate,
    toDateString(new Date()),
  );
  const canOpenForm = today?.canRequestAdjustment ?? isWithinSelfWindow;
  const payload = useMemo<AttendancePunchAdjustmentPayload>(
    () => ({
      ...buildAttendancePunchAdjustmentPayload({
        storeCode,
        today,
        scheduleGuid,
        originalPunchGuid,
        punchType,
        requestedPunchTimeLocal,
        reason,
      }),
      requestedPunchTimeUtc: toAttendancePunchTimeUtc(requestedPunchTimeLocal),
    }),
    [originalPunchGuid, punchType, reason, requestedPunchTimeLocal, scheduleGuid, storeCode, today],
  );
  const payloadFingerprint = buildAttendancePunchAdjustmentFingerprint(payload);
  latestPayloadFingerprintRef.current = payloadFingerprint;
  const missingFields = validateAttendancePunchAdjustment(payload);
  const isManagerDirect = preview?.wouldAutoApprove === true;
  const isPreviewRevisionMissing = preview?.isValid && !preview.previewRevision;
  const canSubmit = Boolean(
    preview?.isValid
    && preview.previewRevision
    && previewFingerprint === buildAttendancePunchAdjustmentFingerprint(payload),
  );

  const updateDraft = (update: () => void) => {
    update();
    setPreview(undefined);
    setPreviewFingerprint(undefined);
    setLocalError("");
    previewRequestGateRef.current.invalidate();
    submitRequestGateRef.current.invalidate();
  };

  const selectExistingPunch = (punch: AttendanceToday["punches"][number]) => {
    updateDraft(() => {
      setOriginalPunchGuid(punch.punchGuid || undefined);
      setScheduleGuid(punch.scheduleGuid ?? selectableSchedules[0]?.scheduleGuid);
      setPunchType(punch.punchType === "ClockOut" ? "ClockOut" : "ClockIn");
      setRequestedPunchTimeLocal(punchTime(punch) || defaultLocalTime(selectedDate));
    });
  };

  const selectPunchType = (value: AttendancePunchType) => {
    updateDraft(() => {
      setPunchType(value);
      // 补录时切换类型，同步把时间放到对应的班次起止；改已有记录时保留原时间。
      const session = selectableSchedules.find((item) => item.scheduleGuid === scheduleGuid);
      if (!originalPunchGuid && session) {
        setRequestedPunchTimeLocal(resolveDefaultMissingPunchTime(session, value));
      }
    });
  };

  const handlePreview = async () => {
    if (isNoteRequired) {
      setLocalError(t("adjustment.noteRequired"));
      return;
    }
    if (missingFields.length) {
      setLocalError(t("adjustment.validation.required"));
      return;
    }
    const requestedFingerprint = buildAttendancePunchAdjustmentFingerprint(payload);
    const request = previewRequestGateRef.current.begin(requestedFingerprint);
    await runLatestAttendanceAdjustmentRequest({
      gate: previewRequestGateRef.current,
      request,
      getCurrentFingerprint: () => latestPayloadFingerprintRef.current,
      operation: () => onPreview({ ...payload, reason: payload.reason.trim() }),
      onSuccess: (result) => {
        setPreview(result);
        setPreviewFingerprint(requestedFingerprint);
      },
      onError: (error) => {
        setLocalError(error instanceof Error ? error.message : t("adjustment.messages.previewFailed"));
      },
    });
  };

  const handleSubmit = async () => {
    if (
      !preview?.isValid
      || previewFingerprint !== buildAttendancePunchAdjustmentFingerprint(payload)
    ) return;
    if (!preview.previewRevision) {
      setLocalError(t("adjustment.messages.previewRevisionMissing"));
      return;
    }
    const requestedFingerprint = buildAttendancePunchAdjustmentFingerprint(payload);
    const request = submitRequestGateRef.current.begin(requestedFingerprint);
    await runLatestAttendanceAdjustmentRequest({
      gate: submitRequestGateRef.current,
      request,
      getCurrentFingerprint: () => latestPayloadFingerprintRef.current,
      operation: () => onSubmit({
        ...payload,
        reason: payload.reason.trim(),
        previewRevision: preview.previewRevision,
      }),
      onSuccess: () => {
        setReasonKey(undefined);
        setReasonNote("");
        setSheetVisible(false);
        setOriginalPunchGuid(undefined);
        setPreview(undefined);
        setPreviewFingerprint(undefined);
        setLocalError("");
      },
      onError: (error) => {
        setLocalError(error instanceof Error ? error.message : t("adjustment.messages.submitFailed"));
      },
    });
  };

  if (!canOpenForm) {
    return (
      <Card mode="outlined" style={styles.card}>
        <Card.Content style={styles.entry}>
          <Icon source="clock-edit-outline" size={20} color={HB_COLORS.textSecondary} />
          <Text variant="bodySmall" style={[styles.muted, styles.flexText]}>
            {t("adjustment.outsideWindow")}
          </Text>
        </Card.Content>
      </Card>
    );
  }

  return (
    <>
      {/* 整行可点，避免标题与按钮重复同一文案 */}
      <Card mode="outlined" style={styles.card} onPress={() => setSheetVisible(true)} accessibilityRole="button">
        <Card.Content style={styles.entry}>
          <Icon source="clock-edit-outline" size={20} color={HB_COLORS.action} />
          <View style={styles.flexText}>
            <Text variant="titleSmall">{t("adjustment.open")}</Text>
            <Text variant="bodySmall" style={styles.muted}>
              {isManagerStore ? t("adjustment.entryHintManager") : t("adjustment.entryHint")}
            </Text>
          </View>
          <Icon source="chevron-right" size={20} color={HB_COLORS.textSecondary} />
        </Card.Content>
      </Card>

      <BusinessSheet
        visible={sheetVisible}
        title={t("adjustment.title")}
        subtitle={t("adjustment.sheetSubtitle", {
          date: selectedDate,
          store: storeName || storeCode || t("common:na"),
        })}
        onDismiss={() => setSheetVisible(false)}
        footer={(
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
              {isManagerDirect ? t("adjustment.saveDirect") : t("adjustment.submitRequest")}
            </Button>
          </View>
        )}
      >
        <View style={styles.content}>
          {selectableSchedules.length > 1 ? (
            <View style={styles.section}>
              <Text variant="labelMedium" style={styles.muted}>{t("adjustment.selectSchedule")}</Text>
              <View style={styles.chipRow}>
                {selectableSchedules.map((session, index) => (
                  <Chip
                    key={session.scheduleGuid}
                    compact
                    selected={scheduleGuid === session.scheduleGuid}
                    onPress={() => updateDraft(() => {
                      setScheduleGuid(session.scheduleGuid);
                      setOriginalPunchGuid(undefined);
                    })}
                  >
                    {t("adjustment.scheduleOption", {
                      number: index + 1,
                      start: session.startTime.slice(0, 5) || "--:--",
                      end: session.endTime.slice(0, 5) || "--:--",
                    })}
                  </Chip>
                ))}
              </View>
            </View>
          ) : null}

          {today?.punches.length ? (
            <View style={styles.section}>
              <Text variant="labelMedium" style={styles.muted}>{t("adjustment.selectExisting")}</Text>
              <View style={styles.chipRow}>
                <Chip
                  compact
                  selected={!originalPunchGuid}
                  onPress={() => updateDraft(() => setOriginalPunchGuid(undefined))}
                >
                  {t("adjustment.addMissing")}
                </Chip>
                {today.punches.map((punch) => (
                  <Chip
                    key={punch.punchGuid || `${punch.punchType}-${punchTime(punch)}`}
                    compact
                    selected={originalPunchGuid === punch.punchGuid}
                    onPress={() => selectExistingPunch(punch)}
                  >
                    {t(`punchTypes.${punch.punchType}`, punch.punchType)} {punchTime(punch).slice(11, 16) || punchTime(punch).slice(0, 5)}
                  </Chip>
                ))}
              </View>
            </View>
          ) : null}

          <View style={styles.section}>
            <Text variant="labelMedium" style={styles.muted}>{t("adjustment.punchTypeTitle")}</Text>
            <SegmentedButtons
              value={punchType}
              onValueChange={(value) => selectPunchType(value as AttendancePunchType)}
              buttons={[
                { value: "ClockIn", label: t("actions.clockIn") },
                { value: "ClockOut", label: t("actions.clockOut") },
              ]}
            />
          </View>

          <View style={styles.section}>
            <Text variant="labelMedium" style={styles.muted}>{t("adjustment.time")}</Text>
            <PunchTimeStepper
              value={requestedPunchTimeLocal}
              workDate={selectedDate}
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
              <Text variant="labelLarge">{t("adjustment.previewTitle")}</Text>
              {preview.proposedSession ? (
                <>
                  <Text variant="bodySmall">
                    {t("adjustment.previewWorked", {
                      before: preview.existingSession?.workedMinutes ?? 0,
                      after: preview.proposedSession.workedMinutes ?? 0,
                      delta: preview.workedMinutesDelta,
                    })}
                  </Text>
                  <Text variant="bodySmall">
                    {t("adjustment.previewOvertime", {
                      before: preview.existingSession?.candidateOvertimeMinutes ?? 0,
                      after: preview.proposedSession.candidateOvertimeMinutes ?? 0,
                      delta: preview.candidateOvertimeMinutesDelta,
                    })}
                  </Text>
                  {preview.proposedSession.hasMissingClockOut ? (
                    <Text variant="bodySmall" style={styles.exceptionText}>
                      {t("today.timeline.missingClockOut")}
                    </Text>
                  ) : null}
                </>
              ) : null}
              {!preview.isValid ? (
                <Text variant="bodySmall" style={styles.exceptionText}>
                  {preview.validationMessage || preview.validationErrorCode || t("adjustment.messages.invalid")}
                </Text>
              ) : null}
              {isPreviewRevisionMissing ? (
                <Text variant="bodySmall" style={styles.exceptionText}>
                  {t("adjustment.messages.previewRevisionMissing")}
                </Text>
              ) : null}
              {preview.wouldAutoApprove ? (
                <Text variant="bodySmall">{t("adjustment.directApplyNotice")}</Text>
              ) : (
                <Text variant="bodySmall" style={styles.muted}>{t("adjustment.pendingNotice")}</Text>
              )}
            </View>
          ) : null}

          {localError ? <Text variant="bodySmall" style={styles.exceptionText}>{localError}</Text> : null}
        </View>
      </BusinessSheet>
    </>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  chipRow: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  content: { gap: HB_SPACING.md },
  entry: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm },
  exceptionText: { color: HB_COLORS.danger },
  flexText: { flex: 1 },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  muted: { color: HB_COLORS.textSecondary },
  previewBox: { backgroundColor: "#ECFDF3", borderRadius: HB_RADIUS.control, gap: 4, padding: HB_SPACING.sm },
  previewInvalid: { backgroundColor: "#FEF3F2" },
  section: { gap: 6 },
});
