import { useState } from "react";
import { StyleSheet, View } from "react-native";
import { Button, Icon, Text, TextInput } from "react-native-paper";
import type { AttendanceApproval, AttendanceApprovalPayload } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { ApprovalDetailFrame, DetailCard } from "./approval-ui";
import { useApprovalDateLabels } from "./use-approval-labels";

/**
 * 缺勤确认（有排班但整班一次卡都没打）：店长只能「确认缺勤」＝调批准接口，该班按 0 工时计。
 * 没有驳回：员工其实来了要去补录打卡、请假了要去补录带薪假，补录后后端会自动取消这条待审。
 * 批准时如果该班已有打卡 / 请假 / 排班已删，后端返回 NO_SHOW_RESOLVED，由列表页统一提示并刷新。
 */
export function NoShowDetail({
  approval,
  canReview,
  isBusy,
  onClose,
  onApprove,
}: {
  approval: AttendanceApproval;
  canReview: boolean;
  isBusy: boolean;
  onClose: () => void;
  onApprove: (payload: AttendanceApprovalPayload) => Promise<void>;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const { weekdayDate } = useApprovalDateLabels();
  const [remark, setRemark] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const workDate = approval.workDate?.slice(0, 10) ?? "";
  const start = approval.scheduleStartTime?.slice(0, 5);
  const end = approval.scheduleEndTime?.slice(0, 5);
  // 排班时段：信息行只写「10:00–18:00」，提示正文用「排班 10:00–18:00」。
  const scheduleRange = start && end ? `${start}–${end}` : "--";
  const schedule = start && end ? t("approvals.board.scheduleWindow", { start, end }) : t("approvals.board.noShow.scheduleUnknown");
  const busy = isBusy || submitting;

  const handleConfirm = async () => {
    setSubmitting(true);
    try {
      await onApprove({ approvalGuid: approval.approvalGuid, remark });
      onClose();
    } catch {
      // 列表页统一提示失败；NO_SHOW_RESOLVED 时列表刷新后这条消失，详情随之关闭。
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <ApprovalDetailFrame
      visible
      title={t("approvals.board.noShow.title")}
      statusLabel={t("approvals.board.statusNeedsAction")}
      statusTone="danger"
      onClose={onClose}
      footer={canReview ? (
        <Button
          mode="contained"
          icon="account-off-outline"
          style={styles.primaryButton}
          disabled={busy}
          loading={submitting}
          onPress={() => void handleConfirm()}
        >
          {t("approvals.board.noShow.confirm")}
        </Button>
      ) : undefined}
    >
      <DetailCard tone="danger">
        <View style={styles.alertRow}>
          <Icon source="account-off-outline" size={22} color={HB_COLORS.danger} />
          <View style={styles.alertBody}>
            <Text variant="titleSmall" style={styles.alertTitle}>{t("approvals.board.noShow.alertTitle")}</Text>
            <Text variant="bodyMedium" style={styles.alertText}>
              {t("approvals.board.noShow.alertBody", {
                name: approval.employeeName ?? "",
                date: workDate ? weekdayDate(workDate) : "",
                schedule,
              })}
            </Text>
          </View>
        </View>
      </DetailCard>

      <DetailCard title={t("approvals.board.noShow.shiftTitle")} aside={approval.storeName ?? approval.storeCode}>
        <View style={styles.infoRow}>
          <Text variant="bodyMedium" style={styles.muted}>{t("approvals.board.noShow.dateLabel")}</Text>
          <Text variant="bodyMedium" style={styles.value}>{workDate ? weekdayDate(workDate) : "--"}</Text>
        </View>
        <View style={styles.infoRow}>
          <Text variant="bodyMedium" style={styles.muted}>{t("approvals.board.noShow.scheduleLabel")}</Text>
          <Text variant="bodyMedium" style={styles.value}>{scheduleRange}</Text>
        </View>
        <View style={styles.infoRow}>
          <Text variant="bodyMedium" style={styles.muted}>{t("approvals.board.noShow.punchLabel")}</Text>
          <Text variant="bodyMedium" style={styles.dangerValue}>{t("approvals.board.noShow.punchNone")}</Text>
        </View>
      </DetailCard>

      <DetailCard title={t("approvals.board.noShow.explainTitle")}>
        <Text variant="bodyMedium" style={styles.bodyText}>{t("approvals.board.noShow.explainZeroHours")}</Text>
        <Text variant="bodyMedium" style={styles.bodyText}>{t("approvals.board.noShow.explainCameIn")}</Text>
        <Text variant="bodyMedium" style={styles.bodyText}>{t("approvals.board.noShow.explainLeave")}</Text>
        {canReview ? (
          <TextInput
            mode="outlined"
            dense
            value={remark}
            onChangeText={setRemark}
            placeholder={t("approvals.board.noShow.remarkPlaceholder")}
            maxLength={200}
          />
        ) : null}
      </DetailCard>
    </ApprovalDetailFrame>
  );
}

const styles = StyleSheet.create({
  alertBody: { flex: 1, gap: 4 },
  alertRow: { flexDirection: "row", gap: HB_SPACING.sm },
  alertText: { color: HB_COLORS.danger },
  alertTitle: { color: HB_COLORS.danger, fontWeight: "700" },
  bodyText: { color: HB_COLORS.textPrimary },
  dangerValue: { color: HB_COLORS.danger, fontWeight: "600" },
  infoRow: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  muted: { color: HB_COLORS.textSecondary },
  primaryButton: { flex: 1 },
  value: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], fontWeight: "600" },
});
