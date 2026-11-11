// 打卡异常详情：点审核卡片打开。整页展示当天打卡（时间轴 + 逐条打卡，当前这条高亮）、
// 这次打卡的依据（时间、方式、设备、定位）和对工时的影响；未处理时底部可拒绝 / 批准。
import { useMemo, useState } from "react";
import { StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Text, TextInput } from "react-native-paper";
import { useQuery } from "@tanstack/react-query";
import { getManagedAttendanceRecords } from "@/modules/attendance/api";
import { buildApprovalTimeline, punchLocalDateTime } from "@/modules/attendance/approval-board";
import type { AttendanceApproval, AttendanceApprovalPayload, AttendancePunch } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ApprovalTimelineBar } from "./ApprovalTimelineBar";
import { ApprovalDetailFrame, DetailCard, PersonAvatar, TypeTag } from "./approval-ui";
import { punchClock, punchLocation, punchMethod, punchReason } from "./punch-labels";
import { useApprovalDateLabels } from "./use-approval-labels";

const ANOMALY_STATUSES = new Set(["Late", "EarlyLeave", "NoSchedule", "Duplicate"]);

export function PunchReviewDetail({
  approval,
  coveringAdjustment,
  storeCode,
  storeName,
  canReview,
  processed,
  isBusy,
  onClose,
  onApprove,
  onReject,
}: {
  approval: AttendanceApproval;
  /** 同一天改的正是这次打卡的补卡（待审）：在时间线里提示「员工申请改为 10:00」。 */
  coveringAdjustment?: AttendanceApproval;
  storeCode?: string;
  storeName?: string;
  canReview: boolean;
  /** 已处理页签打开：只读。 */
  processed: boolean;
  isBusy: boolean;
  onClose: () => void;
  onApprove: (payload: AttendanceApprovalPayload) => Promise<void>;
  onReject: (payload: AttendanceApprovalPayload) => Promise<void>;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const { weekdayDate } = useApprovalDateLabels();
  const [remark, setRemark] = useState("");
  const punch = approval.punch!;
  const workDate = approval.workDate?.slice(0, 10) ?? punch.punchTimeLocal?.slice(0, 10) ?? "";
  const effectiveStore = storeCode ?? approval.storeCode;
  const reason = punchReason(punch, t);

  // 当天打卡：沿用店长打卡记录接口（与加班 / 漏下班详情同一个缓存键），按打卡号找到这一班。
  const recordsQuery = useQuery({
    queryKey: ["attendance", "managed", "records", effectiveStore ?? "", workDate],
    queryFn: () => getManagedAttendanceRecords({ storeCode: effectiveStore!, workDate }),
    enabled: Boolean(effectiveStore && workDate),
  });
  const target = punch.punchGuid.toLowerCase();
  const session = recordsQuery.data?.find((item) =>
    item.segments.some((segment) =>
      segment.clockIn?.punchGuid?.toLowerCase() === target || segment.clockOut?.punchGuid?.toLowerCase() === target));
  const timeline = useMemo(() => (session ? buildApprovalTimeline(session) : undefined), [session]);
  const dayPunches = useMemo(
    () => (session?.segments ?? []).flatMap((segment) => [segment.clockIn, segment.clockOut].filter((item): item is AttendancePunch => Boolean(item))),
    [session],
  );
  const requested = coveringAdjustment?.adjustment;

  const submit = async (action: "approve" | "reject") => {
    const payload = { approvalGuid: approval.approvalGuid, remark };
    try {
      await (action === "approve" ? onApprove(payload) : onReject(payload));
      onClose();
    } catch {
      // 失败提示由列表页统一弹出；详情保持打开，方便重试。
    }
  };

  const renderPunchRow = (item: AttendancePunch) => {
    const isCurrent = item.punchGuid?.toLowerCase() === target;
    const time = punchLocalDateTime(item)?.slice(11, 16) ?? "--:--";
    // 当前这条用审批里的原因（带分钟）；其余打卡只在异常时标原因。
    const rowReason = isCurrent
      ? reason
      : ANOMALY_STATUSES.has(String(item.status))
        ? punchReason({ punchGuid: item.punchGuid, punchType: String(item.punchType), status: String(item.status), lateMinutes: item.lateMinutes, earlyLeaveMinutes: item.earlyLeaveMinutes, hasLocation: false }, t)
        : undefined;
    return (
      <View key={item.punchGuid} style={[styles.punchRow, isCurrent ? styles.punchRowCurrent : null]}>
        <View style={[styles.dot, rowReason ? styles.dotAnomaly : null]} />
        <View style={styles.punchRowBody}>
          <View style={styles.inline}>
            <Text variant="titleSmall" style={styles.strong}>
              {t("approvals.board.punchInfo.line", { punch: t(`punchTypes.${item.punchType}`, String(item.punchType)), time })}
            </Text>
            {rowReason ? <TypeTag label={rowReason.label} tone={rowReason.tone} /> : null}
            {isCurrent ? <Text variant="labelSmall" style={styles.currentText}>{t("approvals.board.punchInfo.current")}</Text> : null}
          </View>
          {isCurrent && requested ? (
            <Text variant="bodySmall" style={styles.requestText}>
              {`↳ ${t(`approvals.board.punchCard.${requested.punchType}`, requested.punchType)} → ${punchClock(requested.requestedPunchTimeLocal) ?? "--:--"}`}
              {requested.reason ? ` · ${t("approvals.board.reasonLine", { reason: requested.reason })}` : ""}
            </Text>
          ) : null}
        </View>
      </View>
    );
  };

  return (
    <ApprovalDetailFrame
      visible
      title={t("approvals.board.punchInfo.detailTitle")}
      statusLabel={processed ? undefined : t("approvals.board.statusPending")}
      onClose={onClose}
      footer={canReview && !processed ? (
        <>
          <Button mode="outlined" textColor={HB_COLORS.danger} style={[styles.footerButton, styles.rejectButton]} disabled={isBusy} onPress={() => void submit("reject")}>
            {t("actions.reject")}
          </Button>
          <Button mode="contained" style={styles.footerButton} disabled={isBusy} loading={isBusy} onPress={() => void submit("approve")}>
            {t("approvals.board.approve")}
          </Button>
        </>
      ) : undefined}
    >
      <DetailCard>
        <View style={styles.personRow}>
          <PersonAvatar name={approval.employeeName} size={44} />
          <View style={styles.personBody}>
            <Text variant="titleMedium" style={styles.strong} numberOfLines={1}>{approval.employeeName}</Text>
            <Text variant="bodySmall" style={styles.muted} numberOfLines={2}>
              {[weekdayDate(workDate), storeName ?? approval.storeName].filter(Boolean).join(" · ")}
            </Text>
          </View>
          <TypeTag label={reason.label} tone={reason.tone} />
        </View>
      </DetailCard>

      <DetailCard
        title={t("approvals.board.dayPunches")}
        aside={approval.scheduleStartTime && approval.scheduleEndTime
          ? t("approvals.board.scheduleWindow", { start: approval.scheduleStartTime.slice(0, 5), end: approval.scheduleEndTime.slice(0, 5) })
          : t("approvals.board.punchInfo.noSchedule")}
      >
        {recordsQuery.isLoading ? <ActivityIndicator /> : null}
        {timeline ? (
          <ApprovalTimelineBar
            timeline={timeline}
            mode="plain"
            scheduleLabel={t("approvals.board.timelineSchedule")}
            actualLabel={t("approvals.board.timelineActual")}
          />
        ) : null}
        {dayPunches.length ? dayPunches.map(renderPunchRow) : null}
        {!recordsQuery.isLoading && !dayPunches.length ? (
          <>
            <Text variant="bodySmall" style={styles.muted}>{t("approvals.board.punchInfo.noDayRecords")}</Text>
            {renderPunchRow({ punchGuid: punch.punchGuid, workDate, punchType: punch.punchType, punchTimeLocal: punch.punchTimeLocal, status: punch.status })}
          </>
        ) : null}
      </DetailCard>

      <DetailCard title={t("approvals.board.punchInfo.thisPunch")}>
        <View style={styles.kv}>
          <Text variant="bodyMedium" style={styles.kvKey}>{t("approvals.board.punchInfo.timeLabel")}</Text>
          <Text variant="bodyMedium" style={styles.kvValue}>{punch.punchTimeLocal?.slice(11, 19) ?? "--"}</Text>
        </View>
        <View style={styles.kv}>
          <Text variant="bodyMedium" style={styles.kvKey}>{t("approvals.board.punchInfo.method")}</Text>
          <Text variant="bodyMedium" style={styles.kvValue}>{punchMethod(punch, t)}</Text>
        </View>
        {punch.posDeviceCode ? (
          <View style={styles.kv}>
            <Text variant="bodyMedium" style={styles.kvKey}>{t("approvals.board.punchInfo.deviceLabel")}</Text>
            <Text variant="bodyMedium" style={styles.kvValue}>{punch.posDeviceCode}</Text>
          </View>
        ) : null}
        <View style={styles.kv}>
          <Text variant="bodyMedium" style={styles.kvKey}>{t("approvals.board.punchInfo.locationLabel")}</Text>
          <Text variant="bodyMedium" style={styles.kvValue}>
            {/* 左边已写「定位」，这里只写精度。 */}
            {punch.hasLocation && punch.locationAccuracy !== undefined ? `±${Math.round(punch.locationAccuracy)}m` : punchLocation(punch, t)}
          </Text>
        </View>
      </DetailCard>

      <DetailCard title={t("approvals.board.punchInfo.impactTitle")}>
        <View style={styles.impact}>
          <Text variant="bodyMedium" style={styles.impactText}>
            {processed ? t("approvals.board.punchInfo.impactProcessed") : t("approvals.board.punchInfo.impactPending")}
          </Text>
        </View>
        {canReview && !processed ? (
          <TextInput
            mode="outlined"
            dense
            value={remark}
            onChangeText={setRemark}
            placeholder={t("approvals.board.punchInfo.remarkOptional")}
          />
        ) : null}
      </DetailCard>
    </ApprovalDetailFrame>
  );
}

const styles = StyleSheet.create({
  currentText: { color: HB_COLORS.danger, fontWeight: "700" },
  dot: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: 6,
    borderWidth: 2,
    height: 12,
    marginTop: 4,
    width: 12,
  },
  dotAnomaly: { backgroundColor: "#FEF3F2", borderColor: HB_COLORS.danger },
  footerButton: { flex: 1 },
  impact: {
    backgroundColor: "#FFFAEB",
    borderColor: "#FEDF89",
    borderRadius: 10,
    borderWidth: 1,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  impactText: { color: "#7A2E0E" },
  inline: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 6 },
  kv: { flexDirection: "row", gap: HB_SPACING.sm },
  kvKey: { color: HB_COLORS.textSecondary, width: 76 },
  kvValue: { color: HB_COLORS.textPrimary, flex: 1 },
  muted: { color: HB_COLORS.textSecondary },
  personBody: { flex: 1, gap: 2, minWidth: 0 },
  personRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm },
  punchRow: { flexDirection: "row", gap: HB_SPACING.sm, paddingVertical: 4 },
  punchRowBody: { flex: 1, gap: 2, minWidth: 0 },
  punchRowCurrent: {
    backgroundColor: "#FEF3F2",
    borderRadius: HB_RADIUS.control,
    marginHorizontal: -HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.xs,
  },
  rejectButton: { borderColor: "#FDA29B" },
  requestText: { color: HB_COLORS.action },
  strong: { color: HB_COLORS.textPrimary, fontWeight: "600" },
});
