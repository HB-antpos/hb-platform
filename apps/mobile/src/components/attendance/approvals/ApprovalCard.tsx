import { useState } from "react";
import { Alert, Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text, TextInput } from "react-native-paper";
import {
  parsePublicHolidayPayHours,
  validateAttendanceMealBreakApproval,
  type OvertimeApprovalAction,
} from "@/modules/attendance/attendance-approval";
import { toAttendanceDeviceLocalTime } from "@/modules/attendance/attendance-device-time";
import { isPaidLeaveType } from "@/modules/attendance/leave-types";
import type { AttendanceApproval, AttendanceApprovalPayload } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type { AttendanceStatusTone } from "../AdjustmentFormControls";
import { PersonAvatar, SELF_REVIEW_TONE, TypeTag } from "./approval-ui";
import { punchClock, punchMetaLine, punchReason } from "./punch-labels";

export type ApprovalDetailKind = "overtime" | "missingClockOut" | "sickLeave";

const TYPE_TONES: Record<string, AttendanceStatusTone> = {
  MissingClockOut: "danger",
  PunchAdjustment: "accent",
  Overtime: "warning",
  MealBreak: "success",
  Leave: "accent",
  Punch: "neutral",
  PublicHolidayPay: "warning",
};

const LEAVE_RANGE_PATTERN = /^\s*(\d{4}-\d{2}-\d{2})\s*-\s*(\d{4}-\d{2}-\d{2})(?:\s*·\s*(.+))?$/;

/** 卡片需要进详情处理的类型：加班（选分钟）、漏下班（填时间）、带薪病假（看医嘱）。 */
export function resolveApprovalDetailKind(item: AttendanceApproval): ApprovalDetailKind | undefined {
  if (item.sourceType === "Overtime") return "overtime";
  if (item.sourceType === "MissingClockOut") return "missingClockOut";
  if (item.sourceType === "Leave" && (item.leave?.leaveType ?? item.title) === "PaidSick") return "sickLeave";
  return undefined;
}

function clockOf(value?: string) {
  const match = value ? /[T ](\d{2}:\d{2})/.exec(value) : null;
  return match?.[1];
}

function formatHours(hours?: number) {
  return hours === undefined ? undefined : `${Number(hours.toFixed(2))} h`;
}

/**
 * 审核列表的一张卡：头像 + 姓名 + 类型标签 + 提交时间；副行写身份与当天排班；
 * 正文一句话写清「从什么改成什么」。能一键处理的给「拒绝 / 批准」，
 * 需要填时间或看图的只给一个主按钮进详情；本人申请用虚线卡、默认不给按钮。
 */
export function ApprovalCard({
  item,
  submittedLabel,
  isSelf,
  canReview,
  canReviewSelf,
  isBusy,
  processed,
  onOpenDetail,
  onOpenPunchDetail,
  grouped,
  coveredByIndex,
  onApprove,
  onReject,
}: {
  item: AttendanceApproval;
  submittedLabel: string;
  isSelf: boolean;
  canReview: boolean;
  canReviewSelf: boolean;
  isBusy: boolean;
  /** 已处理页签：只读，显示结果与审核人。 */
  processed?: boolean;
  onOpenDetail: (kind: ApprovalDetailKind) => void;
  /** 打卡异常：点卡片看当天打卡详情（仍保留卡片上的拒绝 / 批准）。 */
  onOpenPunchDetail?: () => void;
  /** 同一人同一天合并成一张卡时的第几条：不再画头像姓名，改成编号 + 类型。 */
  grouped?: { index: number };
  /** 同组里改的正是这次异常打卡的补卡编号：批准那条补卡后，这条会被后端自动取消。 */
  coveredByIndex?: number;
  onApprove: (payload: AttendanceApprovalPayload) => Promise<void>;
  onReject: (payload: AttendanceApprovalPayload) => Promise<void>;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const [remark, setRemark] = useState("");
  const [showRemark, setShowRemark] = useState(false);
  const [error, setError] = useState("");
  // 带薪公假确认工时：默认填后端建议值，店长可改（0 < h ≤ 24）。
  const suggestedHolidayHours = item.publicHolidayPay?.suggestedHours;
  const [holidayHoursText, setHolidayHoursText] = useState(
    suggestedHolidayHours !== undefined ? String(Number(suggestedHolidayHours.toFixed(2))) : "",
  );
  const detailKind = resolveApprovalDetailKind(item);
  const leaveType = item.leave?.leaveType ?? (item.sourceType === "Leave" ? item.title : undefined);
  const selfReview = isSelf && canReviewSelf;
  const canAct = !processed && canReview && (!isSelf || canReviewSelf);

  const typeLabel = (() => {
    if (item.sourceType === "Leave" && leaveType && isPaidLeaveType(leaveType)) return t(`leaveTypes.${leaveType}`);
    return t(`sourceTypes.${item.sourceType}`, item.sourceType);
  })();
  const typeTone = item.sourceType === "Leave" && leaveType === "PaidSick" ? "danger" : TYPE_TONES[item.sourceType] ?? "neutral";
  const roleLabel = item.isStoreManager ? t("approvals.board.roleManager") : t("approvals.board.roleStaff");
  const scheduleText = item.scheduleStartTime && item.scheduleEndTime
    ? t("approvals.board.scheduleWindow", { start: item.scheduleStartTime.slice(0, 5), end: item.scheduleEndTime.slice(0, 5) })
    : undefined;

  const renderBody = () => {
    if (item.sourceType === "PunchAdjustment" && item.adjustment) {
      const adjustment = item.adjustment;
      const requested = clockOf(adjustment.requestedPunchTimeLocal)
        ?? clockOf(toAttendanceDeviceLocalTime(adjustment.requestedPunchTimeUtc));
      const original = clockOf(adjustment.originalPunchTimeLocal);
      const punchLabel = t(`approvals.board.punchCard.${adjustment.punchType}`, adjustment.punchType);
      return (
        <>
          <Text variant="bodyLarge" style={styles.bodyLine}>
            {original ? `${punchLabel} ` : t("approvals.board.addPunchCard", { punch: punchLabel })}
            {original ? (
              <Text variant="bodyLarge" style={styles.struck}>
                {adjustment.originalIsSystemAutoClockOut ? t("approvals.board.autoTime", { time: original }) : original}
              </Text>
            ) : null}
            {` → `}
            <Text variant="bodyLarge" style={styles.strong}>{requested ?? "--:--"}</Text>
          </Text>
          {adjustment.reason ? (
            <View style={styles.quote}>
              <Text variant="labelSmall" style={styles.quoteLabel}>{t("approvals.board.punchInfo.employeeReason")}</Text>
              <Text variant="bodyMedium" style={styles.bodyLine}>{adjustment.reason}</Text>
            </View>
          ) : null}
        </>
      );
    }
    if (item.sourceType === "MissingClockOut") {
      return (
        <>
          <Text variant="bodyLarge" style={styles.bodyLine}>
            {item.clockInTime
              ? t("approvals.board.missingClockOut.cardLine", { time: item.clockInTime.slice(0, 5) })
              : t("approvals.board.missingClockOut.cardLineNoTime")}
          </Text>
          {!processed ? <Text variant="bodyMedium" style={styles.bodySecondary}>{t("approvals.board.missingClockOut.cardHint")}</Text> : null}
        </>
      );
    }
    if (item.sourceType === "Overtime") {
      const minutes = processed ? item.approvedOvertimeMinutes ?? 0 : item.candidateOvertimeMinutes ?? 0;
      return (
        <Text variant="bodyLarge" style={styles.bodyLine}>
          {processed
            ? t("approvals.board.overtimeApprovedLine", { candidate: item.candidateOvertimeMinutes ?? 0, approved: minutes })
            : t("approvals.board.overtimeCardLine", { minutes })}
        </Text>
      );
    }
    if (item.sourceType === "MealBreak" && item.mealClaim) {
      return (
        <>
          <Text variant="bodyLarge" style={styles.bodyLine}>
            {t("approvals.summary.MealBreak", { count: item.mealClaim.notTakenCount, minutes: item.mealClaim.claimedMinutes })}
          </Text>
          <Text variant="bodyMedium" style={styles.bodySecondary}>
            {[
              t("approvals.mealBreakDetail", { expected: item.mealClaim.expectedCount, recorded: item.mealClaim.recordedCount }),
              item.mealClaim.reason ? t("approvals.board.reasonLine", { reason: item.mealClaim.reason }) : "",
            ].filter(Boolean).join(" · ")}
          </Text>
        </>
      );
    }
    if (item.sourceType === "PublicHolidayPay" && item.publicHolidayPay) {
      const pay = item.publicHolidayPay;
      return (
        <>
          <Text variant="bodyLarge" style={styles.bodyLine}>
            {t("approvals.board.holidayPay.cardLine", {
              holiday: pay.holidayName ?? t("approvals.board.tierRate.publicHoliday"),
              month: pay.holidayDate.slice(5, 7),
              day: pay.holidayDate.slice(8, 10),
            })}
          </Text>
          <Text variant="bodyMedium" style={styles.bodySecondary}>
            {processed && pay.hours !== undefined
              ? t("approvals.board.holidayPay.confirmedLine", { hours: formatHours(pay.hours) })
              : t("approvals.board.holidayPay.historyLine", { weeks: pay.lookbackWeeks, count: pay.ordinaryCount })}
          </Text>
          {!processed && canAct ? (
            <TextInput
              mode="outlined"
              dense
              style={styles.hoursInput}
              label={t("approvals.board.holidayPay.hoursLabel")}
              value={holidayHoursText}
              onChangeText={(value) => { setHolidayHoursText(value); setError(""); }}
              keyboardType="decimal-pad"
              error={Boolean(error)}
            />
          ) : null}
        </>
      );
    }
    if (item.sourceType === "Leave") {
      const parsed = item.detail ? LEAVE_RANGE_PATTERN.exec(item.detail) : null;
      const start = item.leave?.startDate ?? parsed?.[1];
      const end = item.leave?.endDate ?? parsed?.[2];
      const reason = item.leave?.reason ?? parsed?.[3]?.trim();
      const range = start && end && start !== end
        ? `${start.slice(5)} ~ ${end.slice(5)}`
        : start?.slice(5) ?? "";
      const hours = formatHours(item.leave?.paidHours);
      const photos = item.leave?.attachmentUrls.length ?? 0;
      return (
        <>
          <Text variant="bodyLarge" style={styles.bodyLine}>
            {[
              // 带薪假的类型已写在标签上，正文只写日期与工时；无薪请假标签是「请假」，正文补上年假 / 病假。
              leaveType && !isPaidLeaveType(leaveType) ? t(`leaveTypes.${leaveType}`, leaveType) : "",
              range,
              hours,
            ].filter(Boolean).join(" · ")}
          </Text>
          {leaveType === "PaidSick" ? (
            <Text variant="bodyMedium" style={photos ? styles.bodySecondary : styles.dangerText}>
              {photos ? t("approvals.board.sick.photoCount", { count: photos }) : t("approvals.board.sick.noCertificate")}
            </Text>
          ) : reason ? (
            <Text variant="bodyMedium" style={styles.bodySecondary}>{t("approvals.board.reasonLine", { reason })}</Text>
          ) : null}
        </>
      );
    }
    if (item.sourceType === "Punch" && item.punch) {
      const punch = item.punch;
      const reason = punchReason(punch, t);
      return (
        <>
          <View style={styles.punchLine}>
            <Text variant="bodyLarge" style={styles.strong}>
              {t("approvals.board.punchInfo.line", {
                punch: t(`punchTypes.${punch.punchType}`, punch.punchType),
                time: punchClock(punch.punchTimeLocal) ?? "--:--",
              })}
            </Text>
            <TypeTag label={reason.label} tone={reason.tone} />
          </View>
          <Text variant="bodySmall" style={styles.bodySecondary}>{punchMetaLine(punch, t)}</Text>
          {coveredByIndex !== undefined && !processed ? (
            <Text variant="bodySmall" style={styles.linkedText}>
              {`↳ ${t("approvals.board.punchInfo.coveredBy", { index: coveredByIndex })}`}
            </Text>
          ) : null}
        </>
      );
    }
    return (
      <Text variant="bodyLarge" style={styles.bodyLine}>
        {item.sourceType === "Punch" && item.title
          ? t("approvals.summary.Punch", { punchType: t(`punchTypes.${item.title}`, item.title) })
          : item.detail || typeLabel}
      </Text>
    );
  };

  const quickSubmit = async (action: OvertimeApprovalAction) => {
    if (item.sourceType === "PublicHolidayPay" && action === "approve") {
      const hours = parsePublicHolidayPayHours(holidayHoursText);
      if (hours === null) {
        setError(t("approvals.board.holidayPay.invalidHours"));
        return;
      }
      try {
        await onApprove({ approvalGuid: item.approvalGuid, remark, approvedPaidHours: hours });
      } catch {
        // 列表页统一提示失败。
      }
      return;
    }
    // 用餐加回：拒绝必须写原因，没写就先展开备注框。
    if (item.sourceType === "MealBreak" && validateAttendanceMealBreakApproval({ action, remark })) {
      setShowRemark(true);
      setError(t("approvals.mealBreakValidation.remarkRequired"));
      return;
    }
    const payload = { approvalGuid: item.approvalGuid, remark };
    try {
      await (action === "approve" ? onApprove(payload) : onReject(payload));
    } catch {
      // 列表页统一提示失败。
    }
  };

  const confirmSelfApprove = () => {
    // 本人审核要二次确认：提示会记成「本人审批」，Web 可筛选。
    Alert.alert(
      t("approvals.board.selfReview.confirmTitle"),
      t("approvals.board.selfReview.confirmMessage"),
      [
        { text: t("common:actions.cancel"), style: "cancel" },
        { text: t("approvals.board.selfReview.confirmAction"), onPress: () => void quickSubmit("approve") },
      ],
    );
  };

  const renderActions = () => {
    if (processed) return null;
    if (isSelf && !canReviewSelf) {
      return (
        <View style={[styles.lockRow, grouped ? styles.groupedIndent : null]}>
          <Icon source="lock-outline" size={16} color={HB_COLORS.textSecondary} />
          <Text variant="bodySmall" style={styles.bodySecondary}>{t("approvals.board.selfReview.locked")}</Text>
        </View>
      );
    }
    if (!canReview) return null;
    if (detailKind) {
      const icon = detailKind === "missingClockOut" ? "clock-edit-outline" : detailKind === "sickLeave" ? "file-image-outline" : "chart-timeline-variant";
      return (
        <Button mode="contained" icon={icon} disabled={isBusy} style={grouped ? styles.groupedIndent : undefined} onPress={() => onOpenDetail(detailKind)}>
          {t(`approvals.board.openDetail.${detailKind}`)}
        </Button>
      );
    }
    return (
      <View style={[styles.actionRow, grouped ? styles.groupedIndent : null]}>
        <Button
          mode="outlined"
          textColor={HB_COLORS.danger}
          style={[styles.actionButton, styles.rejectButton]}
          disabled={isBusy || !canAct}
          onPress={() => void quickSubmit("reject")}
        >
          {item.sourceType === "PublicHolidayPay" ? t("approvals.board.holidayPay.notApplicable") : t("actions.reject")}
        </Button>
        <Button
          mode="contained"
          buttonColor={selfReview ? SELF_REVIEW_TONE.text : undefined}
          style={styles.actionButton}
          disabled={isBusy || !canAct}
          onPress={() => (selfReview ? confirmSelfApprove() : void quickSubmit("approve"))}
        >
          {selfReview
            ? t("approvals.board.selfReview.approve")
            : item.sourceType === "MealBreak"
              ? t("actions.approveMealBreak")
              : item.sourceType === "PublicHolidayPay"
                ? t("approvals.board.holidayPay.confirm", { hours: parsePublicHolidayPayHours(holidayHoursText) ?? "--" })
                : t("approvals.board.approve")}
        </Button>
      </View>
    );
  };

  const statusKey = item.status.toLowerCase() === "approved" ? "approved" : item.status.toLowerCase() === "rejected" ? "rejected" : "other";
  const reviewedLocal = toAttendanceDeviceLocalTime(item.reviewedAt);

  const opensDetail = Boolean(detailKind) && !processed && !(isSelf && !canReviewSelf);
  const opensPunch = !detailKind && item.sourceType === "Punch" && Boolean(item.punch) && Boolean(onOpenPunchDetail);
  const submittedOrResult = processed ? (
    <TypeTag
      label={t(`approvals.board.result.${statusKey}`, item.status)}
      tone={statusKey === "approved" ? "success" : statusKey === "rejected" ? "danger" : "neutral"}
    />
  ) : (
    <Text variant="bodySmall" style={styles.submitted}>{submittedLabel}</Text>
  );

  return (
    <Pressable
      accessibilityRole={opensDetail || opensPunch ? "button" : undefined}
      accessibilityLabel={opensPunch ? t("approvals.board.punchInfo.openDetailA11y", { name: item.employeeName ?? "" }) : undefined}
      disabled={!opensDetail && !opensPunch}
      onPress={() => {
        if (detailKind) onOpenDetail(detailKind);
        else onOpenPunchDetail?.();
      }}
      style={grouped
        ? styles.groupedItem
        : [styles.card, isSelf && !processed ? styles.selfCard : null]}
    >
      {grouped ? (
        <View style={styles.groupedHeader}>
          <View style={styles.indexBadge}>
            <Text variant="labelSmall" style={styles.indexText}>{grouped.index}</Text>
          </View>
          <View style={styles.groupedTags}>
            <TypeTag label={typeLabel} tone={typeTone} />
            {selfReview && !processed ? <TypeTag label={t("approvals.board.selfReview.tag")} custom={SELF_REVIEW_TONE} /> : null}
            {processed && item.isSelfReview ? <TypeTag label={t("approvals.board.selfReview.recorded")} custom={SELF_REVIEW_TONE} /> : null}
          </View>
          {/* 提交时间已写在合并卡头，这里只在已处理时显示结果。 */}
          {processed ? submittedOrResult : null}
        </View>
      ) : (
      <View style={styles.header}>
        <PersonAvatar name={item.employeeName} />
        <View style={styles.headerBody}>
          <View style={styles.nameRow}>
            <Text variant="titleMedium" style={styles.name} numberOfLines={1}>
              {isSelf ? t("approvals.board.selfName", { name: item.employeeName ?? "" }) : item.employeeName}
            </Text>
            <TypeTag label={typeLabel} tone={typeTone} />
            {selfReview && !processed ? <TypeTag label={t("approvals.board.selfReview.tag")} custom={SELF_REVIEW_TONE} /> : null}
            {processed && item.isSelfReview ? <TypeTag label={t("approvals.board.selfReview.recorded")} custom={SELF_REVIEW_TONE} /> : null}
          </View>
          <Text variant="bodySmall" style={styles.subLine} numberOfLines={1}>
            {[roleLabel, scheduleText].filter(Boolean).join(" · ")}
          </Text>
        </View>
        {submittedOrResult}
      </View>
      )}

      <View style={[styles.body, grouped ? styles.groupedBody : null]}>{renderBody()}</View>

      {processed ? (
        <Text variant="bodySmall" style={styles.bodySecondary}>
          {[
            item.reviewedByName ? t("approvals.board.reviewedBy", { name: item.reviewedByName }) : "",
            reviewedLocal ? reviewedLocal.slice(5, 16).replace("T", " ") : "",
            item.reviewRemark ? t("approvals.board.reasonLine", { reason: item.reviewRemark }) : "",
          ].filter(Boolean).join(" · ")}
        </Text>
      ) : null}

      {showRemark ? (
        <TextInput
          mode="outlined"
          dense
          value={remark}
          onChangeText={(value) => { setRemark(value); setError(""); }}
          placeholder={t("fields.remark")}
          error={Boolean(error)}
        />
      ) : null}
      {error ? <Text variant="bodySmall" style={styles.dangerText}>{error}</Text> : null}
      {renderActions()}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  actionButton: { flex: 1 },
  actionRow: { flexDirection: "row", gap: HB_SPACING.sm },
  body: { gap: 2, paddingLeft: 52 },
  bodyLine: { color: HB_COLORS.textPrimary },
  bodySecondary: { color: HB_COLORS.textSecondary },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.sm,
    padding: HB_SPACING.md,
  },
  dangerText: { color: HB_COLORS.danger },
  header: { alignItems: "flex-start", flexDirection: "row", gap: HB_SPACING.sm },
  hoursInput: { marginTop: HB_SPACING.xxs, width: 160 },
  // 合并卡里的一条：外层卡片负责边框与头像，这里只留编号行、正文与按钮。
  groupedItem: { gap: HB_SPACING.xs, paddingVertical: HB_SPACING.sm },
  groupedHeader: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  groupedTags: { flex: 1, flexDirection: "row", flexWrap: "wrap", gap: 6 },
  groupedBody: { paddingLeft: 28 },
  groupedIndent: { marginLeft: 28, paddingLeft: 0 },
  indexBadge: {
    alignItems: "center",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: 10,
    height: 20,
    justifyContent: "center",
    width: 20,
  },
  indexText: { color: HB_COLORS.textSecondary, fontWeight: "700" },
  linkedText: { color: HB_COLORS.success, marginTop: 2 },
  punchLine: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 6 },
  quote: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderBottomRightRadius: 8,
    borderLeftColor: HB_COLORS.brand,
    borderLeftWidth: 3,
    borderTopRightRadius: 8,
    gap: 1,
    marginTop: 4,
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  quoteLabel: { color: HB_COLORS.textSecondary },
  headerBody: { flex: 1, gap: 2, minWidth: 0 },
  lockRow: { alignItems: "center", flexDirection: "row", gap: 6, paddingLeft: 52 },
  name: { color: HB_COLORS.textPrimary, flexShrink: 1, fontWeight: "600" },
  nameRow: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 6 },
  rejectButton: { borderColor: "#FDA29B" },
  selfCard: { backgroundColor: "#FCFCFD", borderColor: HB_COLORS.outline, borderStyle: "dashed", borderWidth: 1 },
  strong: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  struck: { color: HB_COLORS.textSecondary, textDecorationLine: "line-through" },
  subLine: { color: HB_COLORS.textSecondary },
  submitted: { color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
});
