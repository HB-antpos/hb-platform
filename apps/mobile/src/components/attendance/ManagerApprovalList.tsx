import { useMemo, useState } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { Button, Card, Chip, Icon, Text, TextInput } from "react-native-paper";
import {
  getSupplementalAttendanceApprovalDetail,
  isKnownAttendanceApprovalSourceType,
  validateAttendanceMealBreakApproval,
  validateAttendanceOvertimeApproval,
  type KnownAttendanceApprovalSourceType,
  type OvertimeApprovalAction,
} from "@/modules/attendance/attendance-approval";
import type {
  AttendanceApproval,
  AttendanceApprovalPayload,
} from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { toAttendanceDeviceLocalTime } from "@/modules/attendance/attendance-device-time";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  ATTENDANCE_STATUS_TONES,
  StatusPill,
} from "@/components/attendance/AdjustmentFormControls";

type ApprovalFilter = "all" | KnownAttendanceApprovalSourceType;

/** 筛选 Chip 的固定顺序：最常处理的补卡/加班在前，打卡异常放最后。 */
const FILTER_TYPES: KnownAttendanceApprovalSourceType[] = [
  "PunchAdjustment",
  "Overtime",
  "MealBreak",
  "MissingClockOut",
  "Leave",
  "Punch",
];

const CJK_PATTERN = /[㐀-鿿豈-﫿]/;
const DATE_PATTERN = /(\d{4})-(\d{2})-(\d{2})/;
const CLOCK_PATTERN = /\d{4}-\d{2}-\d{2}[T ](\d{2}:\d{2})/;
/** 后端请假 detail 形如「2026-10-12 - 2026-10-14 · 原因」，解析出起止日期与原因。 */
const LEAVE_RANGE_PATTERN = /^\s*(\d{4}-\d{2}-\d{2})\s*-\s*(\d{4}-\d{2}-\d{2})(?:\s*·\s*(.+))?$/;

/** 头像首字母：中文取前两个字，英文取首尾单词首字母。 */
function getAvatarInitials(name?: string) {
  const trimmed = name?.trim();
  if (!trimmed) return "?";
  if (CJK_PATTERN.test(trimmed)) {
    return Array.from(trimmed.replace(/\s+/g, "")).slice(0, 2).join("");
  }
  const words = trimmed.split(/\s+/).filter(Boolean);
  const first = Array.from(words[0] ?? "")[0] ?? "";
  const last = words.length > 1 ? Array.from(words[words.length - 1])[0] ?? "" : "";
  return `${first}${last}`.toUpperCase() || "?";
}

function getDateParts(value?: string) {
  const match = value ? DATE_PATTERN.exec(value) : null;
  return match ? { date: `${match[1]}-${match[2]}-${match[3]}`, month: match[2], day: match[3] } : null;
}

/** 允许分钟快捷值：0/15/30/候选值，超过候选值的不提供（仍由原校验兜底）。 */
function getQuickOvertimeMinutes(candidateMinutes: number) {
  return Array.from(new Set([0, 15, 30, candidateMinutes]))
    .filter((minutes) => minutes >= 0 && minutes <= candidateMinutes)
    .sort((left, right) => left - right);
}

export function ManagerApprovalList({
  title,
  emptyMessage,
  approvals,
  isBusy,
  canReview = true,
  onApprove,
  onReject,
  onFixMissingClockOut,
}: {
  title?: string;
  emptyMessage?: string;
  approvals: AttendanceApproval[];
  isBusy: boolean;
  canReview?: boolean;
  onApprove: (payload: AttendanceApprovalPayload) => void;
  onReject: (payload: AttendanceApprovalPayload) => void;
  /** 可选：漏下班条目直接跳转补录下班；不传时漏下班仍按普通通过/拒绝处理。 */
  onFixMissingClockOut?: (approval: AttendanceApproval) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const [remarks, setRemarks] = useState<Record<string, string>>({});
  const [approvedMinutes, setApprovedMinutes] = useState<Record<string, string>>({});
  const [approvalErrors, setApprovalErrors] = useState<Record<string, string>>({});
  const [expandedRemarks, setExpandedRemarks] = useState<Record<string, boolean>>({});
  const [filter, setFilter] = useState<ApprovalFilter>("all");

  const typeCounts = useMemo(() => {
    const counts: Partial<Record<KnownAttendanceApprovalSourceType, number>> = {};
    for (const item of approvals) {
      if (isKnownAttendanceApprovalSourceType(item.sourceType)) {
        counts[item.sourceType] = (counts[item.sourceType] ?? 0) + 1;
      }
    }
    return counts;
  }, [approvals]);

  // 选中类型被处理完（数量归零）后自动回到「全部」，避免停留在空列表。
  const activeFilter: ApprovalFilter = filter !== "all" && (typeCounts[filter] ?? 0) > 0 ? filter : "all";
  const visibleApprovals = activeFilter === "all"
    ? approvals
    : approvals.filter((item) => item.sourceType === activeFilter);
  // 列表跨多个分店时才在细节行显示分店名，单店时省掉噪音。
  const showStoreName = useMemo(
    () => new Set(approvals.map((item) => item.storeCode || item.storeName || "")).size > 1,
    [approvals],
  );

  const setRemark = (approvalGuid: string, value: string) => {
    setRemarks((current) => ({ ...current, [approvalGuid]: value }));
    setApprovalErrors((current) => ({ ...current, [approvalGuid]: "" }));
  };

  const setOvertimeValue = (approvalGuid: string, value: string) => {
    setApprovedMinutes((current) => ({ ...current, [approvalGuid]: value }));
    setApprovalErrors((current) => ({ ...current, [approvalGuid]: "" }));
  };

  const formatShortDate = (value?: string) => {
    const parts = getDateParts(value);
    return parts ? t("approvals.shortDate", { month: parts.month, day: parts.day }) : value || "";
  };

  /** 打卡时间默认只显示 HH:mm；跨日（与工作日不同）时补上短日期，避免误读。 */
  const formatPunchTime = (value: string, workDate?: string) => {
    const clock = CLOCK_PATTERN.exec(value)?.[1];
    if (!clock) return value;
    const valueDate = getDateParts(value)?.date;
    const baseDate = getDateParts(workDate)?.date;
    return valueDate && baseDate && valueDate !== baseDate
      ? `${formatShortDate(value)} ${clock}`
      : clock;
  };

  const resolveRequestedTime = (item: AttendanceApproval) => {
    const adjustment = item.adjustment;
    if (!adjustment) return undefined;
    return adjustment.requestedPunchTimeLocal
      || (adjustment.requestedPunchTimeUtc
        ? toAttendanceDeviceLocalTime(adjustment.requestedPunchTimeUtc)
        : undefined);
  };

  const parseLeave = (item: AttendanceApproval) => {
    const match = item.detail ? LEAVE_RANGE_PATTERN.exec(item.detail) : null;
    return match ? { start: match[1], end: match[2], reason: match[3]?.trim() } : null;
  };

  const approvalTitle = (item: AttendanceApproval) => (
    isKnownAttendanceApprovalSourceType(item.sourceType)
      ? t(`approvals.presentation.${item.sourceType}.title`)
      : item.title || item.sourceType
  );

  /** 主行事项摘要，例如「补卡上班 08:55」「加班 45 分钟」「年假 10/12–10/14」。 */
  const approvalSummary = (item: AttendanceApproval) => {
    if (item.sourceType === "PunchAdjustment" && item.adjustment) {
      const requestedTime = resolveRequestedTime(item);
      return t("approvals.summary.PunchAdjustment", {
        punchType: t(`punchTypes.${item.adjustment.punchType}`, item.adjustment.punchType),
        time: requestedTime ? formatPunchTime(requestedTime, item.workDate) : t("common:na"),
      });
    }
    if (item.sourceType === "Overtime") {
      return t("approvals.summary.Overtime", { minutes: item.candidateOvertimeMinutes ?? 0 });
    }
    if (item.sourceType === "MissingClockOut") {
      return t("sourceTypes.MissingClockOut");
    }
    if (item.sourceType === "MealBreak" && item.mealClaim) {
      return t("approvals.summary.MealBreak", {
        count: item.mealClaim.notTakenCount,
        minutes: item.mealClaim.claimedMinutes,
      });
    }
    if (item.sourceType === "Leave") {
      const leave = parseLeave(item);
      const leaveType = item.title ? t(`leaveTypes.${item.title}`, item.title) : approvalTitle(item);
      return leave
        ? t("approvals.summary.Leave", {
          leaveType,
          start: formatShortDate(leave.start),
          end: formatShortDate(leave.end),
        })
        : leaveType;
    }
    if (item.sourceType === "Punch" && item.title) {
      return t("approvals.summary.Punch", {
        punchType: t(`punchTypes.${item.title}`, item.title),
      });
    }
    return approvalTitle(item);
  };

  /** 次行灰色细节：沿用原有 presentation / supplementalDetail 文案，合并成一行。 */
  const approvalDetailParts = (item: AttendanceApproval): string[] => {
    const store = showStoreName ? [item.storeName || item.storeCode || t("common:na")] : [];

    if (!isKnownAttendanceApprovalSourceType(item.sourceType)) {
      return [...store, ...(item.detail ? [item.detail] : [])];
    }

    if (item.sourceType === "Leave") {
      const leave = parseLeave(item);
      // 已在主行展示起止日期时，细节行只保留原因。
      if (leave) return [...store, ...(leave.reason ? [`${t("fields.reason")}: ${leave.reason}`] : [])];
    }

    if (item.sourceType === "Punch" || item.sourceType === "Leave") {
      const supplementalDetail = getSupplementalAttendanceApprovalDetail({
        sourceType: item.sourceType,
        detail: item.detail,
        displayedTitle: approvalTitle(item),
      });
      // 后端细节形如「ClockOut · NoSchedule」：逐段翻译成打卡类型/状态文案，翻不了的原样保留。
      const translatedDetail = supplementalDetail ? supplementalDetail
        .split(" · ")
        .map((part) => t(`punchTypes.${part}`, { defaultValue: t(`statuses.${part}`, part) }))
        .join(" · ") : undefined;
      return [
        ...store,
        // 打卡异常的日期已显示在主行右侧，细节行不再重复「某日的打卡异常」。
        ...(item.sourceType === "Leave"
          ? [t(`approvals.presentation.${item.sourceType}.detail`, {
            workDate: item.workDate?.slice(0, 10) || t("common:na"),
          })]
          : []),
        ...(translatedDetail ? [translatedDetail] : []),
      ];
    }

    if (item.sourceType === "MissingClockOut") {
      return [
        ...store,
        t("approvals.presentation.MissingClockOut.detail", {
          workDate: item.workDate?.slice(0, 10) || t("common:na"),
        }),
      ];
    }

    if (item.sourceType === "MealBreak") {
      // 店长需要看到：排班要求几次、员工记录了几次、员工给的原因。
      const claim = item.mealClaim;
      if (!claim) return [...store, ...(item.detail ? [item.detail] : [])];
      return [
        ...store,
        t("approvals.mealBreakDetail", { expected: claim.expectedCount, recorded: claim.recordedCount }),
        ...(claim.reason ? [`${t("fields.reason")}: ${claim.reason}`] : []),
      ];
    }

    if (item.sourceType === "Overtime") {
      return [
        ...store,
        t("approvals.overtimeCandidate", { minutes: item.candidateOvertimeMinutes ?? 0 }),
        ...(item.approvedOvertimeMinutes !== undefined
          ? [t("approvals.overtimeApprovedValue", { minutes: item.approvedOvertimeMinutes })]
          : []),
      ];
    }

    const adjustment = item.adjustment;
    if (!adjustment) return store;
    const requestedTime = resolveRequestedTime(item);
    return [
      ...store,
      ...(adjustment.originalPunchTimeLocal
        ? [t("approvals.adjustmentOriginal", {
          time: formatPunchTime(adjustment.originalPunchTimeLocal, item.workDate),
        })]
        : []),
      ...(requestedTime
        ? [t("approvals.adjustmentRequested", {
          punchType: t(`punchTypes.${adjustment.punchType}`, adjustment.punchType),
          time: formatPunchTime(requestedTime, item.workDate),
        })]
        : []),
      ...(adjustment.effectivePunchTimeLocal ? [t("approvals.adjustmentEffective", {
        time: formatPunchTime(adjustment.effectivePunchTimeLocal, item.workDate),
      })] : []),
      ...(adjustment.reason ? [`${t("fields.reason")}: ${adjustment.reason}`] : []),
    ];
  };

  const submit = (
    item: AttendanceApproval,
    action: OvertimeApprovalAction,
  ) => {
    const remark = remarks[item.approvalGuid];
    const isOvertime = item.sourceType === "Overtime";
    const candidateMinutes = item.candidateOvertimeMinutes ?? 0;
    const approvedOvertimeMinutes = isOvertime
      ? action === "reject"
        ? 0
        : Number(approvedMinutes[item.approvalGuid] ?? candidateMinutes)
      : undefined;
    const isMealBreak = item.sourceType === "MealBreak";
    const validationError = isOvertime
      ? validateAttendanceOvertimeApproval({
        candidateMinutes,
        approvedMinutes: approvedOvertimeMinutes ?? 0,
        action,
        remark,
      })
      : isMealBreak
        ? validateAttendanceMealBreakApproval({ action, remark })
        : null;

    if (validationError) {
      // 需要备注时自动展开备注框，方便直接补填。
      if (validationError === "remarkRequired") {
        setExpandedRemarks((current) => ({ ...current, [item.approvalGuid]: true }));
      }
      setApprovalErrors((current) => ({
        ...current,
        [item.approvalGuid]: isMealBreak
          ? t(`approvals.mealBreakValidation.${validationError}`)
          : t(`approvals.overtimeValidation.${validationError}`),
      }));
      return;
    }

    const payload: AttendanceApprovalPayload = {
      approvalGuid: item.approvalGuid,
      remark,
      approvedOvertimeMinutes,
    };
    if (action === "approve") onApprove(payload);
    else onReject(payload);
  };

  const renderFilterChip = (key: ApprovalFilter, label: string, count: number) => {
    const selected = activeFilter === key;
    return (
      <Chip
        key={key}
        compact
        selected={selected}
        showSelectedCheck={false}
        style={selected ? styles.filterChipSelected : styles.filterChip}
        textStyle={selected ? styles.filterChipSelectedText : styles.filterChipText}
        onPress={() => setFilter(key)}
      >
        {`${label} ${count}`}
      </Chip>
    );
  };

  const renderItem = (item: AttendanceApproval, index: number) => {
    const guid = item.approvalGuid;
    const isOvertime = item.sourceType === "Overtime";
    const isMissingClockOut = item.sourceType === "MissingClockOut";
    const canFixClockOut = isMissingClockOut && Boolean(onFixMissingClockOut);
    const candidateMinutes = item.candidateOvertimeMinutes ?? 0;
    const overtimeValue = approvedMinutes[guid] ?? String(candidateMinutes);
    const remark = remarks[guid] ?? "";
    const error = approvalErrors[guid];
    // 备注默认收起；已填写、手动展开或加班下调（校验要求备注）时展开。
    const showRemark = Boolean(expandedRemarks[guid])
      || remark.length > 0
      || (isOvertime && overtimeValue !== String(candidateMinutes));
    const detailText = approvalDetailParts(item).filter(Boolean).join(" · ");
    const reviewDisabled = isBusy || !canReview;

    return (
      <View key={guid} style={[styles.row, index > 0 ? styles.rowDivider : null]}>
        <View style={styles.avatar}>
          <Text variant="labelMedium" style={styles.avatarText} numberOfLines={1}>
            {getAvatarInitials(item.employeeName)}
          </Text>
        </View>
        <View style={styles.rowBody}>
          <View style={styles.rowHeader}>
            <Text variant="bodyMedium" style={styles.primaryLine} numberOfLines={1}>
              {item.employeeName ? (
                <Text variant="bodyMedium" style={styles.employeeName}>{`${item.employeeName} · `}</Text>
              ) : null}
              {approvalSummary(item)}
            </Text>
            {isMissingClockOut ? (
              <StatusPill label={t("approvals.needsFix")} tone="danger" />
            ) : item.workDate ? (
              <Text variant="labelSmall" style={styles.dateText}>{formatShortDate(item.workDate)}</Text>
            ) : null}
          </View>
          {detailText ? (
            <Text variant="bodySmall" style={styles.muted} numberOfLines={3}>{detailText}</Text>
          ) : null}

          {isOvertime ? (
            <View style={styles.overtimeRow}>
              <TextInput
                mode="outlined"
                dense
                style={styles.minutesInput}
                label={t("approvals.overtimeApproved")}
                value={overtimeValue}
                onChangeText={(value) => setOvertimeValue(guid, value)}
                keyboardType="number-pad"
                placeholder={t("approvals.overtimeIncrement")}
              />
              <View style={styles.quickChips}>
                {getQuickOvertimeMinutes(candidateMinutes).map((minutes) => {
                  const selected = overtimeValue === String(minutes);
                  return (
                    <Chip
                      key={minutes}
                      compact
                      selected={selected}
                      showSelectedCheck={false}
                      style={selected ? styles.filterChipSelected : styles.filterChip}
                      textStyle={selected ? styles.filterChipSelectedText : styles.filterChipText}
                      onPress={() => setOvertimeValue(guid, String(minutes))}
                    >
                      {t("approvals.quickMinutes", { minutes })}
                    </Chip>
                  );
                })}
              </View>
            </View>
          ) : null}

          {showRemark ? (
            <TextInput
              mode="outlined"
              dense
              label={t("fields.remark")}
              value={remark}
              onChangeText={(value) => setRemark(guid, value)}
              error={Boolean(error)}
            />
          ) : null}
          {error ? (
            <Text variant="bodySmall" style={styles.error}>{error}</Text>
          ) : null}

          <View style={styles.actions}>
            {!showRemark ? (
              <Button
                compact
                mode="text"
                icon="note-edit-outline"
                style={styles.remarkToggle}
                onPress={() => setExpandedRemarks((current) => ({ ...current, [guid]: true }))}
                disabled={reviewDisabled}
              >
                {t("approvals.addRemark")}
              </Button>
            ) : null}
            <Button
              compact
              mode={canFixClockOut ? "text" : "outlined"}
              onPress={() => submit(item, "reject")}
              disabled={reviewDisabled}
            >
              {isOvertime
                ? t("actions.rejectOvertime")
                : t("actions.reject")}
            </Button>
            <Button
              compact
              mode={canFixClockOut ? "outlined" : "contained"}
              onPress={() => submit(item, "approve")}
              disabled={reviewDisabled}
            >
              {isOvertime
                ? t("actions.approveOvertime")
                : item.sourceType === "MealBreak"
                  ? t("actions.approveMealBreak")
                  : t("actions.approve")}
            </Button>
            {canFixClockOut ? (
              <Button
                compact
                mode="contained"
                icon="clock-edit-outline"
                onPress={() => onFixMissingClockOut?.(item)}
                disabled={isBusy}
              >
                {t("approvals.fixMissingClockOut")}
              </Button>
            ) : null}
          </View>
        </View>
      </View>
    );
  };

  return (
    <Card mode="outlined" style={styles.card}>
      <Card.Title title={title ?? t("sections.approvals")} />
      <Card.Content style={styles.content}>
        {approvals.length ? (
          <>
            <ScrollView
              horizontal
              showsHorizontalScrollIndicator={false}
              accessibilityLabel={t("approvals.filterA11y")}
              contentContainerStyle={styles.filterRow}
            >
              {renderFilterChip("all", t("approvals.filterAll"), approvals.length)}
              {FILTER_TYPES.filter((type) => (typeCounts[type] ?? 0) > 0).map((type) =>
                renderFilterChip(
                  type,
                  type === "Punch" ? t("approvals.presentation.Punch.title") : t(`sourceTypes.${type}`),
                  typeCounts[type] ?? 0,
                ))}
            </ScrollView>
            <View>{visibleApprovals.map(renderItem)}</View>
          </>
        ) : (
          <View style={styles.empty}>
            <Icon source="inbox-outline" size={28} color={HB_COLORS.outline} />
            <Text variant="bodyMedium" style={styles.muted}>
              {emptyMessage ?? t("approvals.empty")}
            </Text>
          </View>
        )}
      </Card.Content>
    </Card>
  );
}

const AVATAR_SIZE = 32;

const styles = StyleSheet.create({
  actions: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
    justifyContent: "flex-end",
  },
  avatar: {
    alignItems: "center",
    backgroundColor: ATTENDANCE_STATUS_TONES.accent.background,
    borderRadius: AVATAR_SIZE / 2,
    height: AVATAR_SIZE,
    justifyContent: "center",
    width: AVATAR_SIZE,
  },
  avatarText: {
    color: ATTENDANCE_STATUS_TONES.accent.text,
    fontWeight: "600",
  },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  content: {
    gap: HB_SPACING.xs,
  },
  dateText: {
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  employeeName: {
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
  empty: {
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingVertical: HB_SPACING.lg,
  },
  error: {
    color: HB_COLORS.danger,
  },
  filterChip: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderWidth: StyleSheet.hairlineWidth,
  },
  filterChipSelected: {
    backgroundColor: ATTENDANCE_STATUS_TONES.accent.background,
    borderColor: ATTENDANCE_STATUS_TONES.accent.background,
    borderWidth: StyleSheet.hairlineWidth,
  },
  filterChipSelectedText: {
    color: ATTENDANCE_STATUS_TONES.accent.text,
    fontVariant: ["tabular-nums"],
  },
  filterChipText: {
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  filterRow: {
    gap: HB_SPACING.xs,
    paddingBottom: HB_SPACING.xxs,
  },
  minutesInput: {
    width: 148,
  },
  muted: {
    color: HB_COLORS.textSecondary,
  },
  overtimeRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  primaryLine: {
    color: HB_COLORS.textPrimary,
    flex: 1,
  },
  quickChips: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xxs,
  },
  remarkToggle: {
    marginRight: "auto",
  },
  row: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
    paddingVertical: HB_SPACING.sm,
  },
  rowBody: {
    flex: 1,
    gap: HB_SPACING.xxs,
    minWidth: 0,
  },
  rowDivider: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
  },
  rowHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.xs,
    minHeight: AVATAR_SIZE,
  },
});
