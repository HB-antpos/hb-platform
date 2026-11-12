// 「请哪种假」弹层内容（全职 / 兼职）：每一项直接写「现在能请哪天」，规则缩成灰色小标签。
// 日期全部由 my/leave-options 的 today / rules / sickLeaveCandidates 换算（见 leave-picker.ts），不读设备时间。
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Text } from "react-native-paper";
import { buildLeavePickerSummary, splitShownDates } from "@/modules/attendance/leave-picker";
import { formatHours } from "@/modules/attendance/leave-rules";
import type { AttendanceMyLeaveOptions, PaidLeaveType } from "@/modules/attendance/leave-types";
import { clauseSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { LEAVE_TONES, LeaveNotice, useLeaveDateFormat, type LeaveTone } from "./leave-ui";

/** 弹层副标题：员工类型 · 门店 · 门店今天（例：Part-time · TestStore · today Thu 12 Nov）。 */
export function useLeavePickerSubtitle(options?: AttendanceMyLeaveOptions) {
  const { t } = useAppTranslation("attendanceLeave");
  const { dayLabel } = useLeaveDateFormat();
  if (!options) return undefined;
  const employment = options.employmentType
    ? t(`picker.employment.${options.employmentType}`, { defaultValue: options.employmentType })
    : "";
  return [employment, options.storeName ?? "", t("picker.today", { date: dayLabel(options.today) })].filter(Boolean).join(" · ");
}

export function LeavePickerOptions({
  options,
  isLoading,
  isError,
  onRetry,
  onOpenPaid,
  onOpenOther,
}: {
  options?: AttendanceMyLeaveOptions;
  isLoading: boolean;
  isError: boolean;
  onRetry: () => void;
  onOpenPaid: (type: PaidLeaveType) => void;
  onOpenOther: () => void;
}) {
  const { t } = useAppTranslation("attendanceLeave");
  const { dayLabel } = useLeaveDateFormat();
  const days = (count: number) => t("units.days", { count });
  const weeks = (count: number) => t("units.weeks", { count });

  const paid = options?.paidLeaveEligible ? options : undefined;
  const summary = paid ? buildLeavePickerSummary(paid) : undefined;
  const sick = summary ? splitShownDates(summary.sickDates, 2) : undefined;
  const sickDatesText = sick
    ? [sick.shown.map(dayLabel).join(t("picker.or")), sick.more ? t("picker.more", { count: sick.more }) : ""].filter(Boolean).join(" ")
    : "";

  return (
    <View style={styles.content}>
      {isLoading ? <ActivityIndicator /> : null}
      {isError ? <LeaveNotice tone="warning" text={t("home.optionsFailed")} actionLabel={t("messages.retry")} onAction={onRetry} /> : null}

      {paid && summary ? (
        <>
          <GroupLabel label={t("picker.paid")} />
          <OptionRow
            tone="sick"
            icon="medical-bag"
            title={t("picker.sick.title")}
            when={summary.sickDates.length ? t("picker.sick.claim", { dates: sickDatesText }) : t("picker.sick.none", { days: days(paid.rules.sickLeaveSubmitDays) })}
            whenMuted={summary.sickDates.length === 0}
            tags={[t("picker.sick.tagRostered"), t("picker.sick.tagWithin", { days: days(paid.rules.sickLeaveSubmitDays) }), t("picker.sick.tagCertificate")]}
            onPress={() => onOpenPaid("PaidSick")}
          />
          <OptionRow
            tone="annual"
            icon="beach"
            title={t("picker.annual.title")}
            when={t("picker.annual.when", { date: dayLabel(summary.annualEarliest), max: weeks(paid.rules.annualLeaveMaxWeeks) })}
            tags={[t("picker.annual.tagHours", { hours: formatHours(paid.rules.annualLeaveHoursPerWorkday) }), t("picker.annual.tagExcluded")]}
            onPress={() => onOpenPaid("PaidAnnual")}
          />
          <OptionRow
            tone="cashOut"
            icon="cash-multiple"
            title={t("picker.cashOut.title")}
            when={t("picker.cashOut.when", { date: dayLabel(summary.cashOutEarliest) })}
            tags={[t("picker.cashOut.tagMax", { max: weeks(paid.rules.annualCashOutMaxWeeks) })]}
            onPress={() => onOpenPaid("AnnualCashOut")}
          />
        </>
      ) : null}

      <GroupLabel label={t("picker.unpaid")} />
      <OptionRow
        tone="other"
        icon="calendar-remove-outline"
        title={t("picker.other.title")}
        when={t("picker.other.when")}
        tags={[t("picker.other.tag")]}
        onPress={onOpenOther}
      />
      {paid ? <Text variant="bodySmall" style={styles.footnote}>{t("picker.footer")}</Text> : null}
    </View>
  );
}

function GroupLabel({ label }: { label: string }) {
  return (
    <View style={styles.groupRow}>
      <Text style={styles.groupLabel}>{label}</Text>
      <View style={styles.groupLine} />
    </View>
  );
}

function OptionRow({
  tone,
  icon,
  title,
  when,
  whenMuted,
  tags,
  onPress,
}: {
  tone: LeaveTone;
  icon: keyof typeof MaterialCommunityIcons.glyphMap;
  title: string;
  when: string;
  whenMuted?: boolean;
  tags: string[];
  onPress: () => void;
}) {
  const palette = LEAVE_TONES[tone];
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={[title, when, ...tags].join(clauseSeparator())}
      onPress={onPress}
      style={({ pressed }) => [styles.row, pressed ? styles.pressed : null]}
    >
      <View style={[styles.icon, { backgroundColor: palette.background }]}>
        <MaterialCommunityIcons name={icon} size={22} color={palette.foreground} />
      </View>
      <View style={styles.body}>
        <Text style={styles.title}>{title}</Text>
        <Text style={[styles.when, whenMuted ? styles.whenMuted : null]}>{when}</Text>
        <View style={styles.tags}>
          {tags.map((tag) => (
            <Text key={tag} style={styles.tag}>{tag}</Text>
          ))}
        </View>
      </View>
      <MaterialCommunityIcons name="chevron-right" size={22} color={HB_COLORS.textSecondary} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  content: { gap: HB_SPACING.xs },
  groupRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, marginTop: HB_SPACING.xxs },
  groupLabel: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700", letterSpacing: 0.4, textTransform: "uppercase" },
  groupLine: { backgroundColor: HB_COLORS.outlineMuted, flex: 1, height: StyleSheet.hairlineWidth },
  row: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.sm,
  },
  pressed: { opacity: 0.7 },
  icon: { alignItems: "center", borderRadius: 11, height: 42, justifyContent: "center", width: 42 },
  body: { flex: 1, gap: 2, minWidth: 0 },
  title: { color: HB_COLORS.textPrimary, fontSize: 15.5, fontWeight: "700" },
  when: { color: HB_COLORS.textPrimary, fontSize: 13, fontVariant: ["tabular-nums"] },
  whenMuted: { color: HB_COLORS.textSecondary },
  tags: { flexDirection: "row", flexWrap: "wrap", gap: 6, marginTop: 4 },
  tag: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: 6,
    color: HB_COLORS.textSecondary,
    fontSize: 11,
    fontWeight: "600",
    overflow: "hidden",
    paddingHorizontal: 7,
    paddingVertical: 2,
  },
  footnote: { color: HB_COLORS.textSecondary, marginTop: HB_SPACING.xxs, textAlign: "center" },
});
