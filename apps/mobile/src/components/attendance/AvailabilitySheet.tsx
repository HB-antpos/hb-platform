import { useEffect, useRef, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Chip, SegmentedButtons, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { AvailabilityBatchSaveError } from "@/modules/attendance/availability-batch";
import {
  buildAvailabilityBatchPayload,
  getAvailabilityDraftError,
  isAllDayAvailability,
  type AvailabilityDraft,
} from "@/modules/attendance/availability-entry";
import { buildWeekDates } from "@/modules/attendance/attendance-my-week";
import type {
  AttendanceAvailability,
  AttendanceAvailabilityBatchPayload,
  AttendanceAvailabilityPayload,
} from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ATTENDANCE_STATUS_TONES } from "./AdjustmentFormControls";

function shiftTime(value: string, deltaMinutes: number) {
  const match = /^(\d{1,2}):(\d{2})/.exec(value);
  const minutes = match ? Number(match[1]) * 60 + Number(match[2]) : 0;
  const next = Math.min(Math.max(minutes + deltaMinutes, 0), 23 * 60 + 30);
  return `${String(Math.floor(next / 60)).padStart(2, "0")}:${String(next % 60).padStart(2, "0")}`;
}

/** 开始/结束时间各一组 ±30 分钟步进，替代二级时间选择弹层。 */
function TimeStepperBox({
  label,
  value,
  disabled,
  onChange,
}: {
  label: string;
  value: string;
  disabled: boolean;
  onChange: (value: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <View style={styles.timeBox}>
      <Text variant="labelSmall" style={styles.muted}>{label}</Text>
      <View style={styles.timeRow}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("myAttendance.availabilitySheet.earlier", { label })}
          disabled={disabled}
          onPress={() => onChange(shiftTime(value, -30))}
          style={({ pressed }) => [styles.timeStep, pressed && styles.pressed]}
        >
          <Text variant="titleMedium">−</Text>
        </Pressable>
        <Text variant="titleLarge" style={styles.timeValue}>{value}</Text>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("myAttendance.availabilitySheet.later", { label })}
          disabled={disabled}
          onPress={() => onChange(shiftTime(value, 30))}
          style={({ pressed }) => [styles.timeStep, pressed && styles.pressed]}
        >
          <Text variant="titleMedium">+</Text>
        </Pressable>
      </View>
    </View>
  );
}

/**
 * 员工填写可上班时间的底部弹层：一次勾选本周多天 + 全天/时段 + 备注。
 * 保存与「结果未确认时只读核对」的流程沿用原 AvailabilityForm，失败时保留草稿。
 */
export function AvailabilitySheet({
  visible,
  weekStartDate,
  today,
  initialDates,
  editingItem,
  isBusy,
  onCreate,
  onVerify,
  onUpdate,
  onCancelItem,
  onDismiss,
}: {
  visible: boolean;
  weekStartDate: string;
  today: string;
  initialDates: string[];
  editingItem?: AttendanceAvailability;
  isBusy: boolean;
  onCreate: (payload: AttendanceAvailabilityBatchPayload) => Promise<unknown>;
  onVerify: (payload: AttendanceAvailabilityBatchPayload) => Promise<string[]>;
  onUpdate: (availabilityGuid: string, payload: AttendanceAvailabilityPayload) => Promise<unknown>;
  onCancelItem: (availabilityGuid: string) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const [form, setForm] = useState<AvailabilityDraft>({
    workDates: initialDates,
    allDay: true,
    startTime: "09:00",
    endTime: "17:30",
    note: "",
    unavailable: false,
  });
  const [submitting, setSubmitting] = useState(false);
  const submittingRef = useRef(false);
  const [saveFailed, setSaveFailed] = useState(false);
  const [verification, setVerification] = useState<{
    payload: AttendanceAvailabilityBatchPayload;
    uncertainDates: string[];
  } | null>(null);
  const weekDates = buildWeekDates(weekStartDate);
  const busy = isBusy || submitting;
  const locked = busy || verification !== null;
  const validationError = getAvailabilityDraftError(form);

  // 每次打开按调用方给的初始日期或编辑项重建草稿。
  useEffect(() => {
    if (!visible) return;
    if (editingItem) {
      const allDay = isAllDayAvailability(editingItem.startTime, editingItem.endTime);
      setForm({
        workDates: [editingItem.workDate.slice(0, 10)],
        allDay,
        startTime: allDay ? "09:00" : editingItem.startTime.slice(0, 5),
        endTime: allDay ? "17:30" : editingItem.endTime.slice(0, 5),
        note: editingItem.note ?? "",
        unavailable: Boolean(editingItem.isUnavailable),
      });
    } else {
      setForm({ workDates: initialDates, allDay: true, startTime: "09:00", endTime: "17:30", note: "", unavailable: false });
    }
    setSaveFailed(false);
    setVerification(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible, editingItem?.availabilityGuid]);

  const toggleDate = (date: string) => {
    if (locked || editingItem) return;
    setForm((current) => ({
      ...current,
      workDates: current.workDates.includes(date)
        ? current.workDates.filter((item) => item !== date)
        : [...current.workDates, date].sort(),
    }));
  };
  const selectDates = (indexes: number[]) => {
    if (locked || editingItem) return;
    setForm((current) => ({
      ...current,
      // 快捷选择只勾选今天及以后的日期，过去日期不需要填。
      workDates: indexes.map((index) => weekDates[index]).filter((date) => date >= today),
    }));
  };

  const submit = async () => {
    if (locked || submittingRef.current || validationError) return;
    submittingRef.current = true;
    setSubmitting(true);
    setSaveFailed(false);
    try {
      const { workDates, ...timeFields } = buildAvailabilityBatchPayload(form);
      if (editingItem) {
        await onUpdate(editingItem.availabilityGuid, { ...timeFields, workDate: workDates[0] });
      } else {
        await onCreate({ ...timeFields, workDates });
      }
      onDismiss();
    } catch (error) {
      if (error instanceof AvailabilityBatchSaveError) {
        setForm((current) => ({ ...current, workDates: error.remainingDates }));
        if (error.uncertainDates.length) {
          setVerification({
            payload: { ...buildAvailabilityBatchPayload(form), workDates: error.remainingDates },
            uncertainDates: error.uncertainDates,
          });
        }
      }
      setSaveFailed(true);
    } finally {
      submittingRef.current = false;
      setSubmitting(false);
    }
  };

  const verifySave = async () => {
    if (!verification || busy || submittingRef.current) return;
    submittingRef.current = true;
    setSubmitting(true);
    try {
      // 只读核对未知结果；未确认前绝不再次发送新增请求，避免重复上报。
      const confirmed = new Set(await onVerify(verification.payload));
      const remainingDates = form.workDates.filter((date) => !confirmed.has(date));
      const uncertainDates = verification.uncertainDates.filter((date) => !confirmed.has(date));
      setForm((current) => ({ ...current, workDates: remainingDates }));
      setVerification(uncertainDates.length ? {
        payload: { ...verification.payload, workDates: remainingDates },
        uncertainDates,
      } : null);
      setSaveFailed(uncertainDates.length > 0);
      if (!remainingDates.length) onDismiss();
    } catch {
      setSaveFailed(true);
    } finally {
      submittingRef.current = false;
      setSubmitting(false);
    }
  };

  const summaryTime = form.allDay ? t("availability.allDay") : `${form.startTime}–${form.endTime}`;

  return (
    <BusinessSheet
      visible={visible}
      title={editingItem
        ? t(form.unavailable ? "availability.unavailableEditTitle" : "availability.editTitle")
        : t("sections.availability")}
      subtitle={`${weekDates[0] ?? ""} – ${weekDates[6] ?? ""}`}
      onDismiss={onDismiss}
      footer={(
        <View style={styles.footer}>
          {editingItem ? (
            <Button
              mode="outlined"
              textColor={HB_COLORS.danger}
              style={styles.footerButton}
              disabled={busy}
              onPress={() => {
                onCancelItem(editingItem.availabilityGuid);
                onDismiss();
              }}
            >
              {t("myAttendance.availabilitySheet.remove")}
            </Button>
          ) : null}
          {verification ? (
            <Button mode="outlined" style={styles.footerButton} disabled={busy} onPress={() => void verifySave()}>
              {t("availability.verifySave")}
            </Button>
          ) : null}
          <Button
            mode="contained"
            style={styles.footerButtonWide}
            disabled={Boolean(validationError) || locked}
            loading={busy}
            onPress={() => void submit()}
          >
            {editingItem
              ? t("common:actions.save")
              : t(form.unavailable
                ? "myAttendance.availabilitySheet.submitUnavailable"
                : "myAttendance.availabilitySheet.submit", { count: form.workDates.length })}
          </Button>
        </View>
      )}
    >
      <View style={styles.content}>
        {/* 先选类型：可上班，或员工无法到岗的不能上班时间（店长排班时据此避开）。 */}
        <SegmentedButtons
          value={form.unavailable ? "unavailable" : "available"}
          onValueChange={(value) => setForm((current) => ({ ...current, unavailable: value === "unavailable" }))}
          buttons={[
            { value: "available", label: t("availability.typeAvailable"), icon: "check-circle-outline", disabled: locked },
            { value: "unavailable", label: t("availability.typeUnavailable"), icon: "close-circle-outline", disabled: locked },
          ]}
          density="small"
        />
        <View style={styles.dayRow}>
          {weekDates.map((date, index) => {
            const selected = form.workDates.includes(date);
            const disabled = locked || Boolean(editingItem) || date < today;
            return (
              <Pressable
                key={date}
                accessibilityRole="checkbox"
                accessibilityState={{ checked: selected, disabled }}
                disabled={disabled}
                onPress={() => toggleDate(date)}
                style={[styles.day, selected && styles.daySelected, disabled && !selected && styles.dayDisabled]}
              >
                <Text variant="labelSmall" style={selected ? styles.daySelectedText : styles.muted}>
                  {t(`weekdays.${index}`)}
                </Text>
                <Text variant="titleSmall" style={selected ? styles.daySelectedText : undefined}>
                  {Number(date.slice(8, 10))}
                </Text>
              </Pressable>
            );
          })}
        </View>
        {!editingItem ? (
          <View style={styles.quickRow}>
            <Chip compact onPress={() => selectDates([0, 1, 2, 3, 4, 5, 6])}>{t("availability.selectWholeWeek")}</Chip>
            <Chip compact onPress={() => selectDates([0, 1, 2, 3, 4])}>{t("availability.selectWorkdays")}</Chip>
            <Chip compact onPress={() => selectDates([5, 6])}>{t("availability.selectWeekend")}</Chip>
            <Text variant="labelSmall" style={styles.muted}>
              {t("availability.selectedCount", { count: form.workDates.length })}
            </Text>
          </View>
        ) : null}

        <SegmentedButtons
          value={form.allDay ? "allDay" : "specific"}
          onValueChange={(value) => setForm((current) => ({ ...current, allDay: value === "allDay" }))}
          buttons={[
            { value: "allDay", label: t("availability.allDay"), disabled: locked },
            { value: "specific", label: t("availability.specificTime"), disabled: locked },
          ]}
          density="small"
        />
        {form.allDay ? (
          <Text variant="bodySmall" style={styles.muted}>
            {t(form.unavailable ? "availability.unavailableAllDayHint" : "availability.allDayHint")}
          </Text>
        ) : (
          <View style={styles.timeBoxes}>
            <TimeStepperBox
              label={t("fields.startTime")}
              value={form.startTime}
              disabled={locked}
              onChange={(startTime) => setForm((current) => ({ ...current, startTime }))}
            />
            <TimeStepperBox
              label={t("fields.endTime")}
              value={form.endTime}
              disabled={locked}
              onChange={(endTime) => setForm((current) => ({ ...current, endTime }))}
            />
          </View>
        )}
        {validationError && validationError !== "datesRequired" ? (
          <Text accessibilityRole="alert" style={styles.error}>{t(`availability.${validationError}`)}</Text>
        ) : null}

        <TextInput
          mode="outlined"
          dense
          label={t("availability.optionalNote")}
          placeholder={t("availability.notePlaceholder")}
          value={form.note}
          onChangeText={(note) => setForm((current) => ({ ...current, note }))}
          disabled={locked}
          maxLength={500}
        />

        <View style={styles.summary}>
          <Text variant="labelSmall" style={styles.muted}>{t("myAttendance.availabilitySheet.willSubmit")}</Text>
          <Text variant="bodyMedium">
            {t("availability.summary", { count: form.workDates.length, time: summaryTime })}
          </Text>
        </View>
        {saveFailed ? (
          <Text accessibilityRole="alert" style={styles.error}>
            {verification
              ? t("availability.unconfirmedHint", { dates: verification.uncertainDates.join("、") })
              : t("availability.saveFailedHint")}
          </Text>
        ) : null}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  content: { gap: HB_SPACING.sm },
  day: {
    alignItems: "center",
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    flex: 1,
    paddingVertical: 6,
  },
  dayDisabled: { opacity: 0.4 },
  dayRow: { flexDirection: "row", gap: 4 },
  daySelected: {
    backgroundColor: ATTENDANCE_STATUS_TONES.accent.background,
    borderColor: ATTENDANCE_STATUS_TONES.accent.text,
  },
  daySelectedText: { color: ATTENDANCE_STATUS_TONES.accent.text },
  error: { color: HB_COLORS.danger, fontSize: 13 },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  footerButtonWide: { flex: 1.6 },
  muted: { color: HB_COLORS.textSecondary },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  quickRow: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 6 },
  summary: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.control, gap: 2, padding: HB_SPACING.sm },
  timeBox: {
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    flex: 1,
    gap: 4,
    padding: HB_SPACING.xs,
  },
  timeBoxes: { flexDirection: "row", gap: HB_SPACING.xs },
  timeRow: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  timeStep: {
    alignItems: "center",
    borderRadius: HB_RADIUS.control,
    height: 36,
    justifyContent: "center",
    width: 36,
  },
  timeValue: { fontVariant: ["tabular-nums"], fontWeight: "600" },
});
