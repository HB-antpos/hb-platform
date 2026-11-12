// 无薪请假弹层（临时工的「Time off」与全职 / 兼职的「其他请假」共用）：
// 原因二选一（生病 / 有事）+ 两周日历点选区间（蓝点 = 有排班）+ 受影响排班提示 + 备注。
// 两种原因分别提交后端已有的 SickLeave / AnnualLeave，接口不变；提交成功后刷新本人请假列表。
import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text, TextInput } from "react-native-paper";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { leaveDayCount } from "@/modules/attendance/attendance-my-week";
import { MY_LEAVE_REQUESTS_QUERY_KEY, submitMyLeaveRequest } from "@/modules/attendance/leave-api";
import {
  effectiveLeaveRange,
  mondayOf,
  rosteredDateSet,
  rosteredDatesInRange,
  shouldOpenTimeOffDirectly,
  splitShownDates,
  tapLeaveRange,
  unpaidLeaveTypeOf,
  type LeaveRangeSelection,
  type UnpaidLeaveReason,
} from "@/modules/attendance/leave-picker";
import type { AttendanceMyLeaveOptions } from "@/modules/attendance/leave-types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { LeaveRangeCalendar } from "./LeaveRangeCalendar";
import { LEAVE_TONES, PrimaryBottomButton, useLeaveDateFormat } from "./leave-ui";

export interface OtherLeaveSheetProps {
  visible: boolean;
  storeCode?: string;
  /** 默认日期（门店今天）。 */
  today: string;
  /** 本人请假参数：决定临时工文案、日历上的排班点；没读到时照常可提交，只是没有排班点。 */
  options?: AttendanceMyLeaveOptions;
  onDismiss: () => void;
  /** 提交结果提示（成功 / 失败文案）。 */
  onMessage: (message: string) => void;
  onSubmitted?: () => void;
}

/** 关闭时整体卸载，下次打开表单自动回到默认值。 */
export function OtherLeaveSheet(props: OtherLeaveSheetProps) {
  if (!props.visible) return null;
  return <OtherLeaveSheetBody {...props} />;
}

function OtherLeaveSheetBody({ storeCode, today, options, onDismiss, onMessage, onSubmitted }: OtherLeaveSheetProps) {
  const { t } = useAppTranslation(["attendanceLeave", "attendance"]);
  const { dayLabel, range } = useLeaveDateFormat();
  const queryClient = useQueryClient();
  const casual = shouldOpenTimeOffDirectly(options);
  const [reason, setReason] = useState<UnpaidLeaveReason>("personal");
  const [selection, setSelection] = useState<LeaveRangeSelection>({ startDate: today, endDate: today });
  const [pageMonday, setPageMonday] = useState(() => mondayOf(today));
  const [note, setNote] = useState("");

  const { startDate, endDate } = effectiveLeaveRange(selection);
  const dayCount = leaveDayCount(startDate, endDate);
  const rostered = useMemo(() => rosteredDateSet(options), [options]);
  const affected = rosteredDatesInRange(rostered, startDate, endDate);
  const { shown, more } = splitShownDates(affected);
  const affectedText = [shown.map(dayLabel).join(", "), more ? t("picker.more", { count: more }) : ""].filter(Boolean).join(" ");

  const createLeaveMutation = useMutation({
    mutationFn: submitMyLeaveRequest,
    onSuccess: async () => {
      onMessage(t("attendance:myAttendance.leaveSheet.submitted"));
      await queryClient.invalidateQueries({ queryKey: MY_LEAVE_REQUESTS_QUERY_KEY });
      onDismiss();
      onSubmitted?.();
    },
    onError: (error) => onMessage(error instanceof Error ? error.message : t("attendance:messages.saveFailed")),
  });

  return (
    <BusinessSheet
      visible
      title={t(casual ? "timeOff.titleCasual" : "timeOff.titleOther")}
      onDismiss={onDismiss}
      footer={(
        <>
          <PrimaryBottomButton
            label={t("timeOff.submit", { count: dayCount })}
            disabled={!storeCode || dayCount === 0}
            loading={createLeaveMutation.isPending}
            onPress={() => createLeaveMutation.mutate({
              storeCode,
              leaveType: unpaidLeaveTypeOf(reason),
              startDate,
              endDate,
              reason: note.trim() || undefined,
            })}
          />
          <Text variant="bodySmall" style={styles.fine}>{t(casual ? "timeOff.fineCasual" : "timeOff.fineOther")}</Text>
        </>
      )}
    >
      <View style={styles.content}>
        <View style={styles.unpaidRow}>
          <Text style={styles.unpaidPill}>{t("timeOff.unpaid")}</Text>
          <Text variant="bodySmall" style={styles.muted}>{t(casual ? "timeOff.casualSubtitle" : "timeOff.otherSubtitle")}</Text>
        </View>

        <View>
          <Text style={styles.label}>{t("timeOff.why")}</Text>
          <View style={styles.reasons} accessibilityRole="radiogroup">
            <ReasonCard
              selected={reason === "sick"}
              tone="sick"
              icon="thermometer"
              title={t("timeOff.sick")}
              hint={t("timeOff.sickHint")}
              onPress={() => setReason("sick")}
            />
            <ReasonCard
              selected={reason === "personal"}
              tone="annual"
              icon="account-outline"
              title={t("timeOff.personal")}
              hint={t(casual ? "timeOff.personalHintCasual" : "timeOff.personalHint")}
              onPress={() => setReason("personal")}
            />
          </View>
        </View>

        <View>
          <Text style={styles.label}>{t("timeOff.days")}</Text>
          <LeaveRangeCalendar
            monday={pageMonday}
            onShiftPage={setPageMonday}
            today={today}
            startDate={startDate}
            endDate={endDate}
            rostered={rostered}
            onPressDate={(date) => setSelection((current) => tapLeaveRange(current, date))}
          />
        </View>

        {/* 受影响的排班：有排班用黄色提醒店长要安排顶班；没读到排班数据时不下结论。 */}
        {options ? (
          <View style={[styles.summary, affected.length ? styles.summaryWarn : styles.summaryNeutral]} accessibilityLiveRegion="polite">
            <MaterialCommunityIcons
              name={affected.length ? "alert-outline" : "calendar-check-outline"}
              size={18}
              color={affected.length ? HB_COLORS.warning : HB_COLORS.textSecondary}
            />
            <Text style={[styles.summaryText, affected.length ? styles.summaryTextWarn : null]}>
              <Text style={styles.summaryStrong}>{t("timeOff.summary", { range: range(startDate, endDate), days: t("units.days", { count: dayCount }) })}</Text>
              {" "}
              {affected.length ? t("timeOff.affects", { count: affected.length, dates: affectedText }) : t("timeOff.noRoster")}
            </Text>
          </View>
        ) : null}

        {reason === "sick" ? (
          <View style={[styles.summary, styles.summaryNeutral]}>
            <MaterialCommunityIcons name="file-document-outline" size={18} color={HB_COLORS.textSecondary} />
            <Text style={styles.summaryText}>{t("attendance:myAttendance.leaveSheet.sickHint")}</Text>
          </View>
        ) : null}

        <TextInput
          mode="outlined"
          dense
          label={t("timeOff.note")}
          value={note}
          onChangeText={setNote}
          maxLength={500}
        />
      </View>
    </BusinessSheet>
  );
}

function ReasonCard({
  selected,
  tone,
  icon,
  title,
  hint,
  onPress,
}: {
  selected: boolean;
  tone: "sick" | "annual";
  icon: keyof typeof MaterialCommunityIcons.glyphMap;
  title: string;
  hint: string;
  onPress: () => void;
}) {
  const palette = LEAVE_TONES[tone];
  return (
    <Pressable
      accessibilityRole="radio"
      accessibilityState={{ selected }}
      accessibilityLabel={`${title} · ${hint}`}
      onPress={onPress}
      style={[styles.reason, selected ? { backgroundColor: palette.background, borderColor: palette.foreground } : null]}
    >
      <View style={[styles.reasonIcon, { backgroundColor: palette.background }]}>
        <MaterialCommunityIcons name={icon} size={19} color={palette.foreground} />
      </View>
      <View style={styles.flex}>
        <Text style={styles.reasonTitle}>{title}</Text>
        <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>{hint}</Text>
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  content: { gap: HB_SPACING.sm },
  flex: { flex: 1, minWidth: 0 },
  muted: { color: HB_COLORS.textSecondary },
  fine: { color: HB_COLORS.textSecondary, textAlign: "center" },
  unpaidRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  unpaidPill: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: 6,
    color: HB_COLORS.textSecondary,
    fontSize: 11,
    fontWeight: "700",
    overflow: "hidden",
    paddingHorizontal: 6,
    paddingVertical: 1,
  },
  label: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "700", marginBottom: 6 },
  reasons: { flexDirection: "row", gap: HB_SPACING.xs },
  reason: {
    alignItems: "center",
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1.5,
    flex: 1,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.xs,
  },
  reasonIcon: { alignItems: "center", borderRadius: 9, height: 34, justifyContent: "center", width: 34 },
  reasonTitle: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "700" },
  summary: { borderRadius: HB_RADIUS.surface, flexDirection: "row", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.sm, paddingVertical: HB_SPACING.xs },
  summaryWarn: { backgroundColor: "#FFFAEB" },
  summaryNeutral: { backgroundColor: HB_COLORS.surfaceMuted },
  summaryText: { color: HB_COLORS.textSecondary, flex: 1, fontSize: 13, lineHeight: 19 },
  summaryTextWarn: { color: "#7A2E0E" },
  summaryStrong: { color: HB_COLORS.textPrimary, fontWeight: "700" },
});
