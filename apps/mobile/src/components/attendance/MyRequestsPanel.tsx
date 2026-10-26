import { useMemo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, Card, Icon, Text } from "react-native-paper";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  cancelLeaveRequest,
  getMyAttendancePunchAdjustments,
  getMyLeaveRequests,
} from "@/modules/attendance/api";
import {
  buildMyRequestItems,
  isPendingRequestStatus,
  leaveDayCount,
  type MyRequestItem,
} from "@/modules/attendance/attendance-my-week";
import { resolveAttendancePunchDisplayTime } from "@/modules/attendance/attendance-device-time";
import { isPaidLeaveType } from "@/modules/attendance/leave-types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { type AttendanceStatusTone, StatusPill } from "./AdjustmentFormControls";
import { leaveHomeHref } from "./leave/leave-route-guard";

const STATUS_TONES: Record<string, AttendanceStatusTone> = {
  pending: "warning",
  applied: "success",
  approved: "success",
  rejected: "danger",
  cancelled: "neutral",
};

function statusKey(status: string) {
  const normalized = status.toLowerCase();
  return normalized === "applied" ? "approved" : normalized;
}

function monthDay(value?: string) {
  return value ? value.slice(5, 10).replace("-", "/") : "";
}

function hhmm(value?: string) {
  if (!value) return "--:--";
  const timePart = value.includes("T") ? value.split("T").pop() : value;
  return timePart?.slice(0, 5) || "--:--";
}

/**
 * 员工「申请」页：发起补卡/请假，并集中查看本人申请进度（含审核人与驳回原因）。
 * 「请假」进入「申请假期」页（带薪病假 / 年假 / 其他请假），原请假表单已移到 leave/OtherLeaveSheet。
 */
export function MyRequestsPanel({
  storeCode,
  canApplyLeave,
  onRequestCorrection,
  onMessage,
}: {
  storeCode?: string;
  /** 保留以免改动调用方；申请假期页改用后端给的门店今天。 */
  today: string;
  canApplyLeave: boolean;
  onRequestCorrection: () => void;
  onMessage: (message: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common", "attendanceLeave"]);
  const router = useRouter();
  const queryClient = useQueryClient();

  const adjustmentsQuery = useQuery({
    queryKey: ["attendance", "my", "punch-adjustments"],
    queryFn: getMyAttendancePunchAdjustments,
  });
  const leavesQuery = useQuery({
    queryKey: ["attendance", "my", "leave-requests"],
    queryFn: getMyLeaveRequests,
    enabled: canApplyLeave,
  });
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["attendance", "my", "leave-requests"] });
  const cancelLeaveMutation = useMutation({
    mutationFn: cancelLeaveRequest,
    onSuccess: invalidate,
    onError: (error) => onMessage(error instanceof Error ? error.message : t("messages.saveFailed")),
  });

  const items = useMemo(
    () => buildMyRequestItems(adjustmentsQuery.data ?? [], leavesQuery.data ?? []),
    [adjustmentsQuery.data, leavesQuery.data],
  );
  const pending = items.filter((item) => isPendingRequestStatus(item.status));
  const done = items.filter((item) => !isPendingRequestStatus(item.status));
  const isLoading = adjustmentsQuery.isLoading || leavesQuery.isLoading;

  const openLeaveHome = () => router.push(leaveHomeHref(storeCode));

  const renderItem = (item: MyRequestItem, isLast: boolean) => {
    const key = statusKey(item.status);
    let title = "";
    let detail = "";
    if (item.adjustment) {
      const adjustment = item.adjustment;
      const time = adjustment.requestedPunchTimeUtc
        ? resolveAttendancePunchDisplayTime({ punchTimeUtc: adjustment.requestedPunchTimeUtc })
        : adjustment.requestedPunchTimeLocal;
      title = t("myAttendance.requests.adjustmentTitle", {
        punchType: t(`punchTypes.${adjustment.punchType}`, adjustment.punchType),
        time: hhmm(time),
      });
      detail = [monthDay(adjustment.requestedPunchTimeLocal), adjustment.reason].filter(Boolean).join(" · ");
    } else if (item.leave) {
      const leave = item.leave;
      title = t("myAttendance.requests.leaveTitle", {
        // 带薪三类的名称在 attendanceLeave 命名空间。
        leaveType: isPaidLeaveType(leave.leaveType)
          ? t(`attendanceLeave:leaveTypes.${leave.leaveType}`)
          : t(`leaveTypes.${leave.leaveType}`, leave.leaveType),
        range: leave.startDate === leave.endDate
          ? monthDay(leave.startDate)
          : `${monthDay(leave.startDate)}–${monthDay(leave.endDate)}`,
      });
      detail = [
        t("myAttendance.requests.days", { count: leaveDayCount(leave.startDate, leave.endDate) }),
        leave.reason,
      ].filter(Boolean).join(" · ");
    }
    const reviewer = item.adjustment?.reviewedByName ?? item.leave?.reviewedByName;
    const remark = item.adjustment?.reviewRemark ?? item.leave?.reviewRemark;
    return (
      <View key={item.key} style={[styles.item, isLast ? styles.itemLast : null]}>
        <View style={styles.itemHeader}>
          <Icon
            source={item.kind === "adjustment" ? "clock-edit-outline" : "beach"}
            size={18}
            color={HB_COLORS.textSecondary}
          />
          <Text variant="bodyLarge" style={styles.flexText} numberOfLines={1}>{title}</Text>
          <StatusPill label={t(`myAttendance.requests.status.${key}`, item.status)} tone={STATUS_TONES[key] ?? "neutral"} />
        </View>
        {detail ? <Text variant="bodySmall" style={styles.muted}>{detail}</Text> : null}
        {reviewer && !isPendingRequestStatus(item.status) ? (
          <Text variant="bodySmall" style={key === "rejected" ? styles.dangerText : styles.muted}>
            {remark
              ? t("myAttendance.requests.reviewedWithRemark", { name: reviewer, remark })
              : t("myAttendance.requests.reviewedBy", { name: reviewer })}
          </Text>
        ) : null}
        {item.leave && isPendingRequestStatus(item.status) ? (
          <Button
            compact
            style={styles.withdraw}
            disabled={cancelLeaveMutation.isPending}
            onPress={() => cancelLeaveMutation.mutate(item.leave!.leaveGuid)}
          >
            {t("myAttendance.requests.withdraw")}
          </Button>
        ) : null}
      </View>
    );
  };

  return (
    <>
      <View style={styles.actions}>
        <Pressable accessibilityRole="button" onPress={onRequestCorrection} style={({ pressed }) => [styles.action, pressed && styles.pressed]}>
          <Icon source="clock-edit-outline" size={20} color={HB_COLORS.action} />
          <Text variant="labelLarge">{t("adjustment.open")}</Text>
        </Pressable>
        {canApplyLeave ? (
          <Pressable accessibilityRole="button" onPress={openLeaveHome} style={({ pressed }) => [styles.action, pressed && styles.pressed]}>
            <Icon source="beach" size={20} color={HB_COLORS.action} />
            <Text variant="labelLarge">{t("myAttendance.requests.applyLeave")}</Text>
          </Pressable>
        ) : null}
      </View>

      <Card mode="outlined" style={styles.card}>
        <Card.Content style={styles.content}>
          {isLoading ? <ActivityIndicator /> : null}
          {pending.length ? (
            <>
              <Text variant="labelMedium" style={styles.muted}>{t("myAttendance.requests.pending")}</Text>
              {pending.map((item, index) => renderItem(item, index === pending.length - 1))}
            </>
          ) : null}
          {done.length ? (
            <>
              <Text variant="labelMedium" style={[styles.muted, pending.length ? styles.groupGap : null]}>
                {t("myAttendance.requests.done")}
              </Text>
              {done.map((item, index) => renderItem(item, index === done.length - 1))}
            </>
          ) : null}
          {!isLoading && !items.length ? (
            <Text variant="bodySmall" style={styles.muted}>{t("myAttendance.requests.empty")}</Text>
          ) : null}
        </Card.Content>
      </Card>

    </>
  );
}

const styles = StyleSheet.create({
  action: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    flex: 1,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    justifyContent: "center",
    minHeight: 48,
  },
  actions: { flexDirection: "row", gap: HB_SPACING.xs },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  content: { gap: HB_SPACING.xs },
  dangerText: { color: HB_COLORS.danger },
  flexText: { flex: 1 },
  groupGap: { marginTop: HB_SPACING.sm },
  item: {
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    gap: 2,
    paddingVertical: HB_SPACING.xs,
  },
  itemHeader: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  itemLast: { borderBottomWidth: 0 },
  muted: { color: HB_COLORS.textSecondary },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  withdraw: { alignSelf: "flex-end" },
});
