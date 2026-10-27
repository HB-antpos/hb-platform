// 「其他请假（无薪）」弹层：沿用原来「我的申请」里的请假表单（年假 / 病假 + 起止日期 + 原因），文案仍用 attendance 命名空间。
// 从 MyRequestsPanel 抽出，供「申请假期」首页使用；提交成功后刷新本人请假列表。
import { useState } from "react";
import { StyleSheet, View } from "react-native";
import { Button, IconButton, SegmentedButtons, Text, TextInput } from "react-native-paper";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { leaveDayCount, shiftDate } from "@/modules/attendance/attendance-my-week";
import { MY_LEAVE_REQUESTS_QUERY_KEY, submitMyLeaveRequest } from "@/modules/attendance/leave-api";
import type { AttendanceLeaveType } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** 日期前后切换：请假日期跨度短，左右点按比手输或二级日历更快。 */
function DateStepper({ label, value, onChange }: { label: string; value: string; onChange: (value: string) => void }) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <View style={styles.dateBox}>
      <Text variant="labelSmall" style={styles.muted}>{label}</Text>
      <View style={styles.dateRow}>
        <IconButton
          icon="chevron-left"
          size={18}
          accessibilityLabel={t("myAttendance.leaveSheet.previousDay", { label })}
          onPress={() => onChange(shiftDate(value, -1))}
        />
        <Text variant="titleSmall" style={styles.tabular}>{value}</Text>
        <IconButton
          icon="chevron-right"
          size={18}
          accessibilityLabel={t("myAttendance.leaveSheet.nextDay", { label })}
          onPress={() => onChange(shiftDate(value, 1))}
        />
      </View>
    </View>
  );
}

export interface OtherLeaveSheetProps {
  visible: boolean;
  storeCode?: string;
  /** 默认起止日期（门店今天）。 */
  today: string;
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

function OtherLeaveSheetBody({ storeCode, today, onDismiss, onMessage, onSubmitted }: OtherLeaveSheetProps) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const queryClient = useQueryClient();
  const [leaveType, setLeaveType] = useState<AttendanceLeaveType>("AnnualLeave");
  const [startDate, setStartDate] = useState(today);
  const [endDate, setEndDate] = useState(today);
  const [reason, setReason] = useState("");
  const dayCount = leaveDayCount(startDate, endDate);

  const createLeaveMutation = useMutation({
    mutationFn: submitMyLeaveRequest,
    onSuccess: async () => {
      onMessage(t("myAttendance.leaveSheet.submitted"));
      await queryClient.invalidateQueries({ queryKey: MY_LEAVE_REQUESTS_QUERY_KEY });
      onDismiss();
      onSubmitted?.();
    },
    onError: (error) => onMessage(error instanceof Error ? error.message : t("messages.saveFailed")),
  });

  return (
    <BusinessSheet
      visible
      title={t("myAttendance.requests.applyLeave")}
      onDismiss={onDismiss}
      footer={(
        <Button
          mode="contained"
          disabled={!storeCode || dayCount === 0 || createLeaveMutation.isPending}
          loading={createLeaveMutation.isPending}
          onPress={() => createLeaveMutation.mutate({
            storeCode,
            leaveType,
            startDate,
            endDate,
            reason: reason.trim() || undefined,
          })}
        >
          {t("myAttendance.leaveSheet.submit", { count: dayCount })}
        </Button>
      )}
    >
      <View style={styles.content}>
        <SegmentedButtons
          value={leaveType}
          onValueChange={(value) => setLeaveType(value as AttendanceLeaveType)}
          buttons={[
            { value: "AnnualLeave", label: t("leaveTypes.AnnualLeave") },
            { value: "SickLeave", label: t("leaveTypes.SickLeave") },
          ]}
        />
        <View style={styles.dateBoxes}>
          <DateStepper
            label={t("fields.startDate")}
            value={startDate}
            onChange={(value) => {
              setStartDate(value);
              // 开始日期后移超过结束日期时，结束日期跟着走，避免出现 0 天。
              if (value > endDate) setEndDate(value);
            }}
          />
          <DateStepper label={t("fields.endDate")} value={endDate} onChange={setEndDate} />
        </View>
        {dayCount === 0 ? (
          <Text variant="bodySmall" style={styles.dangerText}>{t("myAttendance.leaveSheet.invalidRange")}</Text>
        ) : null}
        <TextInput
          mode="outlined"
          dense
          label={t("fields.reason")}
          value={reason}
          onChangeText={setReason}
          maxLength={500}
        />
        {leaveType === "SickLeave" ? (
          <Text variant="bodySmall" style={styles.muted}>{t("myAttendance.leaveSheet.sickHint")}</Text>
        ) : null}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  content: { gap: HB_SPACING.xs },
  dangerText: { color: HB_COLORS.danger },
  dateBox: {
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    flex: 1,
    paddingTop: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.xs,
  },
  dateBoxes: { flexDirection: "row", gap: HB_SPACING.xs },
  dateRow: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  muted: { color: HB_COLORS.textSecondary },
  tabular: { fontVariant: ["tabular-nums"] },
});
