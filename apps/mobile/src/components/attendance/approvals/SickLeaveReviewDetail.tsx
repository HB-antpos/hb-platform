import { useState } from "react";
import { Image, Modal, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Button, Icon, IconButton, Text, TextInput } from "react-native-paper";
import { SafeAreaProvider, SafeAreaView } from "react-native-safe-area-context";
import { toAttendanceDeviceLocalTime } from "@/modules/attendance/attendance-device-time";
import type { AttendanceApproval, AttendanceApprovalPayload } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ApprovalDetailFrame, DetailCard, PersonAvatar, TypeTag } from "./approval-ui";
import { useApprovalDateLabels } from "./use-approval-labels";

/** 天数差（YYYY-MM-DD）；用于「生病后第 N 天提交」。 */
function daysBetween(from: string, to: string) {
  const start = Date.parse(`${from}T00:00:00Z`);
  const end = Date.parse(`${to}T00:00:00Z`);
  return Number.isFinite(start) && Number.isFinite(end) ? Math.round((end - start) / 86_400_000) : undefined;
}

function formatHours(hours?: number) {
  if (hours === undefined) return "--";
  return `${Number(hours.toFixed(2))} h`;
}

/**
 * 带薪病假审核：排班、当天打卡、医嘱大图放在一屏，店长不用切页面核对；
 * 拒绝必须写原因（如「医嘱看不清，请重拍」）。
 */
export function SickLeaveReviewDetail({
  approval,
  canReview,
  isBusy,
  submitLimitDays,
  onClose,
  onApprove,
  onReject,
}: {
  approval: AttendanceApproval;
  canReview: boolean;
  isBusy: boolean;
  /** 病假提交时限（天），来自考勤设置；后端没给时按 4。 */
  submitLimitDays: number;
  onClose: () => void;
  onApprove: (payload: AttendanceApprovalPayload) => Promise<void>;
  onReject: (payload: AttendanceApprovalPayload) => Promise<void>;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const { weekdayDate } = useApprovalDateLabels();
  const [remark, setRemark] = useState("");
  const [error, setError] = useState("");
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);
  const leave = approval.leave;
  const schedules = leave?.schedules ?? [];
  const firstDate = leave?.startDate ?? schedules[0]?.workDate ?? approval.workDate?.slice(0, 10) ?? "";
  const submittedLocal = toAttendanceDeviceLocalTime(approval.submittedAt);
  const submittedDay = submittedLocal.slice(0, 10);
  const dayAfterSick = submittedDay && firstDate ? daysBetween(firstDate, submittedDay) : undefined;
  const photos = leave?.attachmentUrls ?? [];
  // 与员工端口径一致：今天 10-20、4 天 → 10-16 生病仍可提交（相差 4 天也算在内）。
  const withinLimit = dayAfterSick !== undefined && dayAfterSick >= 0 && dayAfterSick <= submitLimitDays;
  const scheduledNoPunch = schedules.length > 0 && schedules.every((item) => !item.hasPunch);

  const submit = async (action: "approve" | "reject") => {
    if (action === "reject" && !remark.trim()) {
      setError(t("approvals.board.sick.rejectReasonRequired"));
      return;
    }
    const payload = { approvalGuid: approval.approvalGuid, remark };
    try {
      await (action === "approve" ? onApprove(payload) : onReject(payload));
      onClose();
    } catch {
      // 列表页统一提示失败。
    }
  };

  return (
    <ApprovalDetailFrame
      visible
      title={t("approvals.board.sick.title")}
      statusLabel={t("approvals.board.statusPending")}
      onClose={onClose}
      footer={canReview ? (
        <>
          <Button mode="outlined" textColor={HB_COLORS.danger} style={[styles.secondaryButton, styles.rejectButton]} disabled={isBusy} onPress={() => void submit("reject")}>
            {t("actions.reject")}
          </Button>
          <Button mode="contained" style={styles.primaryButton} disabled={isBusy} loading={isBusy} onPress={() => void submit("approve")}>
            {t("approvals.board.sick.approve", { hours: formatHours(leave?.paidHours) })}
          </Button>
        </>
      ) : undefined}
    >
      <DetailCard>
        <View style={styles.personRow}>
          <PersonAvatar name={approval.employeeName} size={44} />
          <View style={styles.personBody}>
            <View style={styles.nameRow}>
              <Text variant="titleMedium" style={styles.strong} numberOfLines={1}>{approval.employeeName}</Text>
              {approval.employmentType ? (
                <TypeTag label={t(`approvals.board.employmentTypes.${approval.employmentType}`, approval.employmentType)} />
              ) : null}
            </View>
            <Text variant="bodySmall" style={styles.muted}>
              {[
                submittedLocal ? t("approvals.board.sick.submittedAt", {
                  month: submittedLocal.slice(5, 7),
                  day: submittedLocal.slice(8, 10),
                  time: submittedLocal.slice(11, 16),
                }) : "",
                dayAfterSick !== undefined && dayAfterSick >= 0 ? t("approvals.board.sick.dayAfter", { count: dayAfterSick }) : "",
              ].filter(Boolean).join(" · ")}
            </Text>
          </View>
        </View>
      </DetailCard>

      <DetailCard title={t("approvals.board.sick.datesTitle")}>
        {(schedules.length ? schedules : [{ workDate: firstDate, hasPunch: false }]).map((schedule, index) => (
          <View key={`${schedule.workDate}-${index}`} style={styles.infoBlock}>
            <View style={styles.infoRow}>
              <Text variant="bodyMedium">{weekdayDate(schedule.workDate)}</Text>
              <Text variant="bodyMedium" style={styles.strong}>
                {"startTime" in schedule && schedule.startTime
                  ? t("approvals.board.scheduleWindow", { start: schedule.startTime.slice(0, 5), end: schedule.endTime?.slice(0, 5) ?? "" })
                  : t("approvals.board.sick.noSchedule")}
              </Text>
            </View>
            <View style={styles.infoRow}>
              <Text variant="bodyMedium">{t("approvals.board.sick.dayPunch")}</Text>
              <Text variant="bodyMedium" style={styles.muted}>
                {schedule.hasPunch ? t("approvals.board.sick.punched") : t("approvals.board.sick.none")}
              </Text>
            </View>
          </View>
        ))}
        <View style={[styles.infoRow, styles.infoRowLast]}>
          <Text variant="bodyMedium">{t("approvals.board.sick.paidHours")}</Text>
          <Text variant="bodyMedium" style={styles.paidHours}>{formatHours(leave?.paidHours)}</Text>
        </View>
      </DetailCard>

      <DetailCard title={t("approvals.board.sick.certificateTitle")} aside={photos.length ? t("approvals.board.sick.tapToZoom") : undefined}>
        {photos.length ? (
          <View style={styles.certificateRow}>
            <View style={styles.thumbs}>
              {photos.map((url, index) => (
                <Pressable
                  key={url}
                  accessibilityRole="imagebutton"
                  accessibilityLabel={t("approvals.board.sick.openPhoto", { index: index + 1 })}
                  onPress={() => setViewerIndex(index)}
                  style={styles.thumb}
                >
                  <Image source={{ uri: url }} style={styles.thumbImage} resizeMode="cover" />
                </Pressable>
              ))}
            </View>
            <View style={styles.certificateMeta}>
              <Text variant="bodySmall" style={styles.muted}>
                {[
                  t("approvals.board.sick.photoCount", { count: photos.length }),
                  leave?.attachmentTakenAt ? t("approvals.board.sick.takenAt", {
                    month: leave.attachmentTakenAt.slice(5, 7),
                    day: leave.attachmentTakenAt.slice(8, 10),
                    time: leave.attachmentTakenAt.slice(11, 16),
                  }) : "",
                ].filter(Boolean).join(" · ")}
              </Text>
              {leave?.reason ? (
                <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.sick.note", { note: leave.reason })}</Text>
              ) : null}
            </View>
          </View>
        ) : (
          <Text variant="bodySmall" style={styles.error}>{t("approvals.board.sick.noCertificate")}</Text>
        )}
      </DetailCard>

      <DetailCard>
        <CheckLine ok={withinLimit} label={t("approvals.board.sick.checkWithin", { count: submitLimitDays })} />
        <CheckLine ok={scheduledNoPunch} label={t("approvals.board.sick.checkScheduled")} />
      </DetailCard>

      {canReview ? (
        <>
          <TextInput
            mode="outlined"
            dense
            value={remark}
            onChangeText={(value) => { setRemark(value); setError(""); }}
            placeholder={t("approvals.board.sick.remarkPlaceholder")}
            error={Boolean(error)}
          />
          {error ? <Text variant="bodySmall" style={styles.error}>{error}</Text> : null}
        </>
      ) : null}

      <Modal visible={viewerIndex !== null} animationType="fade" onRequestClose={() => setViewerIndex(null)}>
        <SafeAreaProvider>
          <SafeAreaView style={styles.viewer}>
            <IconButton
              icon="close"
              iconColor={HB_COLORS.white}
              accessibilityLabel={t("common:actions.close")}
              onPress={() => setViewerIndex(null)}
              style={styles.viewerClose}
            />
            <ScrollView maximumZoomScale={4} minimumZoomScale={1} contentContainerStyle={styles.viewerContent} centerContent>
              {viewerIndex !== null && photos[viewerIndex] ? (
                <Image source={{ uri: photos[viewerIndex] }} style={styles.viewerImage} resizeMode="contain" />
              ) : null}
            </ScrollView>
          </SafeAreaView>
        </SafeAreaProvider>
      </Modal>
    </ApprovalDetailFrame>
  );
}

function CheckLine({ ok, label }: { ok: boolean; label: string }) {
  return (
    <View style={styles.checkLine}>
      <Icon source={ok ? "check-circle-outline" : "alert-circle-outline"} size={16} color={ok ? HB_COLORS.success : HB_COLORS.warning} />
      <Text variant="bodyMedium" style={{ color: ok ? HB_COLORS.success : HB_COLORS.warning }}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  certificateMeta: { flex: 1, gap: 4 },
  certificateRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm },
  checkLine: { alignItems: "center", flexDirection: "row", gap: 6 },
  error: { color: HB_COLORS.danger },
  infoBlock: { gap: HB_SPACING.xs },
  infoRow: {
    alignItems: "center",
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    justifyContent: "space-between",
    paddingBottom: HB_SPACING.xs,
  },
  infoRowLast: { borderBottomWidth: 0, paddingBottom: 0 },
  muted: { color: HB_COLORS.textSecondary },
  nameRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  paidHours: { color: HB_COLORS.danger, fontVariant: ["tabular-nums"], fontWeight: "700" },
  personBody: { flex: 1, gap: 2, minWidth: 0 },
  personRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm },
  primaryButton: { flex: 1.8 },
  rejectButton: { borderColor: "#FDA29B" },
  secondaryButton: { flex: 1 },
  strong: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  thumb: {
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    height: 136,
    overflow: "hidden",
    width: 104,
  },
  thumbImage: { height: "100%", width: "100%" },
  thumbs: { flexDirection: "row", gap: HB_SPACING.xs },
  viewer: { backgroundColor: "#000", flex: 1 },
  viewerClose: { alignSelf: "flex-end" },
  viewerContent: { alignItems: "center", flexGrow: 1, justifyContent: "center" },
  viewerImage: { aspectRatio: 0.75, width: "100%" },
});
