import { Pressable, StyleSheet, View } from "react-native";
import { Chip, Text, TextInput } from "react-native-paper";
import {
  resolveLocalPunchDayOffset,
  shiftLocalPunchTime,
} from "@/modules/attendance/attendance-managed-records";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export const ATTENDANCE_STATUS_TONES = {
  success: { background: "#ECFDF3", text: HB_COLORS.success },
  warning: { background: "#FFFAEB", text: HB_COLORS.warning },
  danger: { background: "#FEF3F2", text: HB_COLORS.danger },
  accent: { background: "#EFF8FF", text: "#175CD3" },
  neutral: { background: HB_COLORS.surfaceMuted, text: HB_COLORS.textSecondary },
} as const;

export type AttendanceStatusTone = keyof typeof ATTENDANCE_STATUS_TONES;

/** 小号状态标签：底色与文字取同一色系，保证弱背景下的可读性。 */
export function StatusPill({ label, tone }: { label: string; tone: AttendanceStatusTone }) {
  const colors = ATTENDANCE_STATUS_TONES[tone];
  return (
    <View style={[styles.pill, { backgroundColor: colors.background }]}>
      <Text variant="labelSmall" style={[styles.pillText, { color: colors.text }]}>{label}</Text>
    </View>
  );
}

const STEPS = [
  { delta: -60, label: "-1h", a11y: "adjustment.stepper.minusHour" },
  { delta: -5, label: "-5", a11y: "adjustment.stepper.minus5" },
  { delta: 5, label: "+5", a11y: "adjustment.stepper.plus5" },
  { delta: 60, label: "+1h", a11y: "adjustment.stepper.plusHour" },
] as const;

/**
 * 补卡时间步进器：替代手输 YYYY-MM-DDTHH:mm，单手即可微调到分钟；
 * 不嵌套第二层原生 Modal，避免与外层 BusinessSheet 叠加时互相遮挡。
 */
export function PunchTimeStepper({
  value,
  workDate,
  onChange,
}: {
  value: string;
  workDate: string;
  onChange: (value: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const dayOffset = resolveLocalPunchDayOffset(value, workDate);
  const renderStep = (step: (typeof STEPS)[number]) => (
    <Pressable
      key={step.label}
      accessibilityRole="button"
      accessibilityLabel={t(step.a11y)}
      onPress={() => onChange(shiftLocalPunchTime(value, step.delta, workDate))}
      style={({ pressed }) => [styles.step, pressed ? styles.stepPressed : null]}
    >
      <Text variant="labelLarge" style={styles.stepText}>{step.label}</Text>
    </Pressable>
  );

  return (
    <View style={styles.stepper}>
      {STEPS.slice(0, 2).map(renderStep)}
      <View style={styles.timeValue} accessibilityLiveRegion="polite">
        <Text variant="headlineSmall" style={styles.timeText}>{value.slice(11, 16) || "--:--"}</Text>
        {dayOffset > 0 ? (
          <Text variant="labelSmall" style={styles.nextDay}>{t("adjustment.nextDay")}</Text>
        ) : null}
      </View>
      {STEPS.slice(2).map(renderStep)}
    </View>
  );
}

export interface AdjustmentReasonPreset {
  key: string;
  label: string;
}

/** 快捷原因 + 补充说明；选「其他」时说明必填，由调用方决定是否显示错误。 */
export function AdjustmentReasonPicker({
  presets,
  selectedKey,
  note,
  showNoteRequired,
  onSelect,
  onNoteChange,
}: {
  presets: AdjustmentReasonPreset[];
  selectedKey?: string;
  note: string;
  showNoteRequired: boolean;
  onSelect: (key: string) => void;
  onNoteChange: (note: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <View style={styles.reasonBlock}>
      <View style={styles.chipRow}>
        {presets.map((preset) => (
          <Chip
            key={preset.key}
            compact
            selected={selectedKey === preset.key}
            showSelectedCheck={false}
            style={selectedKey === preset.key ? styles.chipSelected : styles.chip}
            textStyle={selectedKey === preset.key ? styles.chipSelectedText : undefined}
            onPress={() => onSelect(preset.key)}
          >
            {preset.label}
          </Chip>
        ))}
      </View>
      <TextInput
        mode="outlined"
        dense
        label={t("adjustment.note")}
        value={note}
        onChangeText={onNoteChange}
        maxLength={200}
        error={showNoteRequired}
      />
      {showNoteRequired ? (
        <Text variant="bodySmall" style={styles.errorText}>{t("adjustment.noteRequired")}</Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  chip: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outline, borderWidth: StyleSheet.hairlineWidth },
  chipRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  chipSelected: { backgroundColor: ATTENDANCE_STATUS_TONES.accent.background },
  chipSelectedText: { color: ATTENDANCE_STATUS_TONES.accent.text },
  errorText: { color: HB_COLORS.danger },
  nextDay: { color: HB_COLORS.warning },
  pill: { alignSelf: "flex-start", borderRadius: 999, paddingHorizontal: HB_SPACING.xs, paddingVertical: 2 },
  pillText: { fontWeight: "600" },
  reasonBlock: { gap: HB_SPACING.xs },
  step: {
    alignItems: "center",
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    justifyContent: "center",
    minHeight: 44,
    minWidth: 48,
    paddingHorizontal: HB_SPACING.xs,
  },
  stepPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  stepText: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  stepper: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  timeText: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], fontWeight: "600" },
  timeValue: { alignItems: "center", flex: 1 },
});
