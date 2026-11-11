import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
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
import { requestProgressStep, waitedSince } from "@/modules/attendance/attendance-my-week-view";
import { resolveAttendancePunchDisplayTime, toAttendanceDeviceLocalTime } from "@/modules/attendance/attendance-device-time";
import { getMyLeaveOptions, LEAVE_OPTIONS_QUERY_KEY } from "@/modules/attendance/leave-api";
import { formatHours, localDateString } from "@/modules/attendance/leave-rules";
import { isPaidLeaveType } from "@/modules/attendance/leave-types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ATTENDANCE_STATUS_TONES, type AttendanceStatusTone, StatusPill } from "./AdjustmentFormControls";
import { useApprovalDateLabels } from "./approvals/use-approval-labels";
import { LeaveTypeCard, PaidLeaveCards } from "./leave/LeaveHomeScreen";
import { OtherLeaveSheet } from "./leave/OtherLeaveSheet";
import { LEAVE_TONES, type LeaveTone } from "./leave/leave-ui";
import { paidLeaveHref } from "./leave/leave-route-guard";

type IconName = string;

const STATUS_TONES: Record<string, AttendanceStatusTone> = {
  pending: "warning",
  applied: "success",
  approved: "success",
  rejected: "danger",
  cancelled: "neutral",
};

const LEAVE_BADGES: Record<string, { tone: LeaveTone; icon: IconName }> = {
  PaidSick: { tone: "sick", icon: "medical-bag" },
  PaidAnnual: { tone: "annual", icon: "beach" },
  AnnualCashOut: { tone: "cashOut", icon: "cash-multiple" },
};

type Filter = "all" | "pending" | "done";

function statusKey(status: string) {
  const normalized = status.toLowerCase();
  return normalized === "applied" ? "approved" : normalized;
}

function hhmm(value?: string) {
  if (!value) return "--:--";
  const timePart = value.includes("T") ? value.split("T").pop() : value;
  return timePart?.slice(0, 5) || "--:--";
}

/** UTC 时间 → 手机本地「11-10 18:40」。 */
function localStamp(value?: string) {
  const local = toAttendanceDeviceLocalTime(value);
  return local.length >= 16 ? `${local.slice(5, 10)} ${local.slice(11, 16)}` : "";
}

/** 每条申请的展示字段：标题、日期行、图标与色系，列表卡和详情共用。 */
function useRequestDisplay() {
  const { t } = useAppTranslation(["attendance", "common", "attendanceLeave"]);
  const { weekdayDate } = useApprovalDateLabels();

  return (item: MyRequestItem) => {
    if (item.adjustment) {
      const adjustment = item.adjustment;
      const time = adjustment.requestedPunchTimeUtc
        ? resolveAttendancePunchDisplayTime({ punchTimeUtc: adjustment.requestedPunchTimeUtc })
        : adjustment.requestedPunchTimeLocal;
      return {
        title: t("myAttendance.requests.adjustmentTitle", {
          punchType: t(`punchTypes.${adjustment.punchType}`, adjustment.punchType),
          time: hhmm(time),
        }),
        when: weekdayDate(adjustment.requestedPunchTimeLocal?.slice(0, 10)),
        reason: adjustment.reason,
        amount: "",
        icon: "clock-edit-outline",
        palette: { background: ATTENDANCE_STATUS_TONES.accent.background, foreground: ATTENDANCE_STATUS_TONES.accent.text },
        attachmentCount: 0,
      };
    }
    const leave = item.leave!;
    const badge = LEAVE_BADGES[leave.leaveType] ?? { tone: "other" as LeaveTone, icon: "calendar-remove-outline" };
    const leaveType = isPaidLeaveType(leave.leaveType)
      ? t(`attendanceLeave:leaveTypes.${leave.leaveType}`)
      : t(`leaveTypes.${leave.leaveType}`, leave.leaveType);
    const days = leaveDayCount(leave.startDate, leave.endDate);
    const range = leave.startDate === leave.endDate
      ? weekdayDate(leave.startDate)
      : `${leave.startDate.slice(5, 10)} ~ ${leave.endDate.slice(5, 10)}`;
    // 带薪假写申请工时；年假已批准且财务核定过时写核定工时。
    const hours = statusKey(leave.status) === "approved" && leave.approvedPaidHours !== undefined
      ? leave.approvedPaidHours
      : leave.paidHours;
    return {
      title: t("myAttendance.requests.leaveTitle", { leaveType, range }),
      when: range,
      reason: leave.reason,
      amount: [
        t("myAttendance.requests.days", { count: days }),
        hours !== undefined ? t("myAttendance.requests.hoursAmount", { hours: formatHours(hours) }) : undefined,
      ].filter(Boolean).join(" · "),
      icon: badge.icon,
      palette: LEAVE_TONES[badge.tone],
      attachmentCount: leave.attachmentUrls?.length ?? (leave.attachmentUrl ? 1 : 0),
    };
  };
}

function useWaitedLabel() {
  const { t } = useAppTranslation("attendance");
  return (submittedAt: string | undefined, nowMs: number) => {
    const waited = waitedSince(submittedAt, nowMs);
    return waited ? t(`myAttendance.requests.waited.${waited.unit}`, { count: waited.count }) : "";
  };
}

/** 三步进度：已提交 → 店长审核 → 生效（已处理的写处理结果）。 */
function RequestSteps({ status }: { status: string }) {
  const { t } = useAppTranslation("attendance");
  const step = requestProgressStep(status);
  const key = statusKey(status);
  const finalLabel = step === 3 ? t(`myAttendance.requests.status.${key}`, status) : t("myAttendance.requests.steps.effective");
  return (
    <View style={styles.steps}>
      <View style={styles.stepTrack}>
        <View style={[styles.stepDot, styles.stepDone]} />
        <View style={[styles.stepLine, styles.stepDone]} />
        <View style={[styles.stepDot, step === 2 ? styles.stepCurrent : styles.stepDone]} />
        <View style={[styles.stepLine, step === 3 ? styles.stepDone : null]} />
        <View style={[styles.stepDot, step === 3 ? styles.stepDone : null]} />
      </View>
      <View style={styles.stepLabels}>
        <Text style={styles.stepLabel}>{t("myAttendance.requests.steps.submitted")}</Text>
        <Text style={[styles.stepLabel, step === 2 && styles.stepLabelCurrent]}>{t("myAttendance.requests.steps.review")}</Text>
        <Text style={styles.stepLabel}>{finalLabel}</Text>
      </View>
    </View>
  );
}

/**
 * 员工「我的申请」：补卡 / 请假两个大入口 + 全部 / 进行中 / 已处理筛选。
 * 进行中的卡片带三步进度和「已等多久」；已处理的收成紧凑卡并直接显示审核人与店长备注；点卡片看详情。
 * 「请假」先弹「请哪种假」，带薪三类进入原有填写页，其他请假打开原有表单弹层。
 */
export function MyRequestsPanel({
  storeCode,
  today,
  canApplyLeave,
  onRequestCorrection,
  onMessage,
}: {
  storeCode?: string;
  /** 其他请假表单的默认日期（规则读到时以后端的门店今天为准）。 */
  today: string;
  canApplyLeave: boolean;
  onRequestCorrection: () => void;
  onMessage: (message: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common", "attendanceLeave"]);
  const router = useRouter();
  const queryClient = useQueryClient();
  const display = useRequestDisplay();
  const waitedLabel = useWaitedLabel();
  const [filter, setFilter] = useState<Filter>("all");
  const [detailKey, setDetailKey] = useState<string | null>(null);
  const [pickerVisible, setPickerVisible] = useState(false);
  const [otherVisible, setOtherVisible] = useState(false);
  // 「已等多久」只在打开面板时取一次现在时间，不需要每秒刷新。
  const [nowMs] = useState(() => Date.now());

  const adjustmentsQuery = useQuery({
    queryKey: ["attendance", "my", "punch-adjustments"],
    queryFn: getMyAttendancePunchAdjustments,
  });
  const leavesQuery = useQuery({
    queryKey: ["attendance", "my", "leave-requests"],
    queryFn: getMyLeaveRequests,
    enabled: canApplyLeave,
  });
  const optionsQuery = useQuery({
    queryKey: [...LEAVE_OPTIONS_QUERY_KEY, storeCode ?? ""],
    queryFn: () => getMyLeaveOptions(storeCode),
    enabled: canApplyLeave && pickerVisible,
  });
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["attendance", "my", "leave-requests"] });
  const cancelLeaveMutation = useMutation({
    mutationFn: cancelLeaveRequest,
    onSuccess: () => {
      setDetailKey(null);
      return invalidate();
    },
    onError: (error) => onMessage(error instanceof Error ? error.message : t("messages.saveFailed")),
  });

  const items = useMemo(
    () => buildMyRequestItems(adjustmentsQuery.data ?? [], leavesQuery.data ?? []),
    [adjustmentsQuery.data, leavesQuery.data],
  );
  const pending = items.filter((item) => isPendingRequestStatus(item.status));
  const done = items.filter((item) => !isPendingRequestStatus(item.status));
  const isLoading = adjustmentsQuery.isLoading || leavesQuery.isLoading;
  const detailItem = items.find((item) => item.key === detailKey);
  const options = optionsQuery.data;

  const withdraw = (item: MyRequestItem) => {
    if (item.leave) cancelLeaveMutation.mutate(item.leave.leaveGuid);
  };

  const renderPendingCard = (item: MyRequestItem) => {
    const view = display(item);
    const submittedAt = item.adjustment?.submittedAt ?? item.leave?.submittedAt;
    const waited = waitedLabel(submittedAt, nowMs);
    const footer = [
      localStamp(submittedAt) ? t("myAttendance.requests.submittedAt", { time: localStamp(submittedAt) }) : "",
      waited,
    ].filter(Boolean).join(" · ");
    return (
      <Pressable
        key={item.key}
        accessibilityRole="button"
        onPress={() => setDetailKey(item.key)}
        style={({ pressed }) => [styles.card, styles.cardPending, pressed && styles.pressed]}
      >
        <View style={styles.cardHeader}>
          <View style={[styles.typeIcon, { backgroundColor: view.palette.background }]}>
            <Icon source={view.icon} size={20} color={view.palette.foreground} />
          </View>
          <View style={styles.flex}>
            <Text style={styles.cardTitle} numberOfLines={1}>{view.title}</Text>
            <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>
              {[item.adjustment ? view.when : view.amount, view.reason].filter(Boolean).join(" · ")}
            </Text>
          </View>
          <StatusPill label={t("myAttendance.requests.status.pending")} tone="warning" />
        </View>
        <RequestSteps status={item.status} />
        <View style={styles.cardFooter}>
          <Text variant="bodySmall" style={[styles.muted, styles.flex]} numberOfLines={1}>{footer}</Text>
          {item.leave ? (
            <Button
              compact
              mode="outlined"
              disabled={cancelLeaveMutation.isPending}
              onPress={() => withdraw(item)}
              style={styles.withdraw}
              labelStyle={styles.withdrawLabel}
            >
              {t("myAttendance.requests.withdraw")}
            </Button>
          ) : null}
        </View>
      </Pressable>
    );
  };

  const renderDoneCard = (item: MyRequestItem) => {
    const view = display(item);
    const key = statusKey(item.status);
    const reviewer = item.adjustment?.reviewedByName ?? item.leave?.reviewedByName;
    const remark = item.adjustment?.reviewRemark ?? item.leave?.reviewRemark;
    const reviewedAt = item.adjustment?.reviewedAt ?? item.leave?.reviewedAt;
    const subline = reviewer
      ? [t("myAttendance.requests.reviewedBy", { name: reviewer }), localStamp(reviewedAt).slice(0, 5)].filter(Boolean).join(" · ")
      : [view.when, view.reason].filter(Boolean).join(" · ");
    return (
      <Pressable
        key={item.key}
        accessibilityRole="button"
        onPress={() => setDetailKey(item.key)}
        style={({ pressed }) => [styles.card, styles.cardCompact, pressed && styles.pressed]}
      >
        <View style={styles.cardHeader}>
          <View style={[styles.typeIcon, styles.typeIconMuted]}>
            <Icon source={view.icon} size={18} color={HB_COLORS.textSecondary} />
          </View>
          <View style={styles.flex}>
            <Text style={styles.cardTitleCompact} numberOfLines={1}>{view.title}</Text>
            <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>{subline}</Text>
          </View>
          <StatusPill label={t(`myAttendance.requests.status.${key}`, item.status)} tone={STATUS_TONES[key] ?? "neutral"} />
        </View>
        {remark ? (
          <Text style={[styles.remark, key === "rejected" ? styles.remarkDanger : styles.remarkOk]}>
            {`「${remark}」`}
          </Text>
        ) : null}
      </Pressable>
    );
  };

  const showPending = filter !== "done" && pending.length > 0;
  const showDone = filter !== "pending" && done.length > 0;

  return (
    <>
      <View style={styles.actions}>
        <Pressable accessibilityRole="button" onPress={onRequestCorrection} style={({ pressed }) => [styles.action, pressed && styles.pressed]}>
          <View style={[styles.actionIcon, { backgroundColor: ATTENDANCE_STATUS_TONES.accent.background }]}>
            <Icon source="clock-edit-outline" size={20} color={ATTENDANCE_STATUS_TONES.accent.text} />
          </View>
          <View style={styles.flex}>
            <Text style={styles.actionTitle}>{t("adjustment.open")}</Text>
            <Text style={styles.actionHint} numberOfLines={1}>{t("myAttendance.requests.correctionHint")}</Text>
          </View>
        </Pressable>
        {canApplyLeave ? (
          <Pressable accessibilityRole="button" onPress={() => setPickerVisible(true)} style={({ pressed }) => [styles.action, pressed && styles.pressed]}>
            <View style={[styles.actionIcon, { backgroundColor: LEAVE_TONES.annual.background }]}>
              <Icon source="umbrella-outline" size={20} color={LEAVE_TONES.annual.foreground} />
            </View>
            <View style={styles.flex}>
              <Text style={styles.actionTitle}>{t("myAttendance.requests.applyLeave")}</Text>
              <Text style={styles.actionHint} numberOfLines={1}>{t("myAttendance.requests.leaveHint")}</Text>
            </View>
          </Pressable>
        ) : null}
      </View>

      <View style={styles.chips}>
        {(["all", "pending", "done"] as const).map((key) => {
          const selected = filter === key;
          return (
            <Pressable
              key={key}
              accessibilityRole="tab"
              accessibilityState={{ selected }}
              onPress={() => setFilter(key)}
              style={[styles.chip, selected && styles.chipSelected]}
            >
              <Text style={[styles.chipText, selected && styles.chipTextSelected]}>
                {t(key === "all" ? "myAttendance.requests.filterAll" : `myAttendance.requests.${key}`)}
                {key === "pending" && pending.length ? ` ${pending.length}` : ""}
              </Text>
            </Pressable>
          );
        })}
      </View>

      {isLoading ? <ActivityIndicator /> : null}
      {showPending ? (
        <View style={styles.group}>
          <Text style={styles.groupTitle}>{`${t("myAttendance.requests.pending")} · ${pending.length}`}</Text>
          {pending.map(renderPendingCard)}
        </View>
      ) : null}
      {showDone ? (
        <View style={styles.group}>
          <Text style={styles.groupTitle}>{t("myAttendance.requests.done")}</Text>
          {done.map(renderDoneCard)}
        </View>
      ) : null}
      {!isLoading && !showPending && !showDone ? (
        <Text variant="bodySmall" style={[styles.muted, styles.empty]}>{t("myAttendance.requests.empty")}</Text>
      ) : null}

      <RequestDetailSheet
        item={detailItem}
        nowMs={nowMs}
        withdrawing={cancelLeaveMutation.isPending}
        onWithdraw={withdraw}
        onDismiss={() => setDetailKey(null)}
      />

      {/* 请哪种假：带薪三类（临时工没有）+ 其他请假；规则数字来自后端参数，与「申请假期」页一致。 */}
      <BusinessSheet
        visible={pickerVisible}
        title={t("myAttendance.requests.leavePicker.title")}
        subtitle={t("myAttendance.requests.leavePicker.hint")}
        onDismiss={() => setPickerVisible(false)}
      >
        <View style={styles.pickerContent}>
          {optionsQuery.isLoading ? <ActivityIndicator /> : null}
          {options?.paidLeaveEligible ? (
            <>
              <Text style={styles.groupTitle}>{t("myAttendance.requests.leavePicker.paid")}</Text>
              <PaidLeaveCards
                rules={options.rules}
                onOpen={(type) => {
                  setPickerVisible(false);
                  router.push(paidLeaveHref(type, storeCode));
                }}
              />
            </>
          ) : options ? (
            <Text variant="bodySmall" style={styles.muted}>{t("attendanceLeave:home.casualHint")}</Text>
          ) : optionsQuery.isError ? (
            <Text variant="bodySmall" style={styles.warningText}>{t("attendanceLeave:home.optionsFailed")}</Text>
          ) : null}
          <Text style={styles.groupTitle}>{t("myAttendance.requests.leavePicker.other")}</Text>
          <LeaveTypeCard
            tone="other"
            icon="calendar-remove-outline"
            title={t("attendanceLeave:home.other.title")}
            lines={[t("attendanceLeave:home.other.line1")]}
            onPress={() => {
              setPickerVisible(false);
              setOtherVisible(true);
            }}
          />
        </View>
      </BusinessSheet>
      <OtherLeaveSheet
        visible={otherVisible}
        storeCode={storeCode ?? options?.storeCode}
        today={options?.today ?? (today || localDateString())}
        onDismiss={() => setOtherVisible(false)}
        onMessage={onMessage}
      />
    </>
  );
}

/** 申请详情：类型与日期、原因、医嘱张数、进度时间线、审核人与店长备注；进行中的请假可撤回。 */
function RequestDetailSheet({
  item,
  nowMs,
  withdrawing,
  onWithdraw,
  onDismiss,
}: {
  item?: MyRequestItem;
  nowMs: number;
  withdrawing: boolean;
  onWithdraw: (item: MyRequestItem) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const display = useRequestDisplay();
  const waitedLabel = useWaitedLabel();
  if (!item) return null;

  const view = display(item);
  const key = statusKey(item.status);
  const pending = isPendingRequestStatus(item.status);
  const submittedAt = item.adjustment?.submittedAt ?? item.leave?.submittedAt;
  const reviewedAt = item.adjustment?.reviewedAt ?? item.leave?.reviewedAt;
  const reviewer = item.adjustment?.reviewedByName ?? item.leave?.reviewedByName;
  const remark = item.adjustment?.reviewRemark ?? item.leave?.reviewRemark;
  const store = item.leave?.storeName ?? item.leave?.storeCode ?? item.adjustment?.storeCode;
  const waited = waitedLabel(submittedAt, nowMs);

  return (
    <BusinessSheet
      visible
      title={t("myAttendance.requests.detail.title")}
      onDismiss={onDismiss}
      footer={item.leave && pending ? (
        <Button
          mode="outlined"
          textColor={HB_COLORS.danger}
          disabled={withdrawing}
          loading={withdrawing}
          onPress={() => onWithdraw(item)}
          style={styles.detailWithdraw}
        >
          {t("myAttendance.requests.detail.withdraw")}
        </Button>
      ) : undefined}
    >
      <View style={styles.detailContent}>
        <View style={styles.detailCard}>
          <View style={styles.detailHead}>
            <View style={[styles.typeIcon, { backgroundColor: view.palette.background }]}>
              <Icon source={view.icon} size={20} color={view.palette.foreground} />
            </View>
            <Text style={[styles.cardTitle, styles.flex]}>{view.title}</Text>
            <StatusPill label={t(`myAttendance.requests.status.${key}`, item.status)} tone={STATUS_TONES[key] ?? "neutral"} />
          </View>
          <View style={styles.kv}>
            {view.when ? <DetailRow label={t("myAttendance.requests.detail.date")} value={view.when} /> : null}
            {view.amount ? <DetailRow label={t("myAttendance.requests.detail.amount")} value={view.amount} /> : null}
            {store ? <DetailRow label={t("myAttendance.requests.detail.store")} value={store} /> : null}
            {view.reason ? <DetailRow label={t("myAttendance.requests.detail.reason")} value={view.reason} /> : null}
            {view.attachmentCount ? (
              <DetailRow
                label={t("myAttendance.requests.detail.attachments")}
                value={t("myAttendance.requests.attachments", { count: view.attachmentCount })}
              />
            ) : null}
          </View>
        </View>

        <View style={styles.detailCard}>
          <Text style={styles.groupTitle}>{t("myAttendance.requests.detail.progress")}</Text>
          <TimelineItem
            state="done"
            title={t("myAttendance.requests.steps.submitted")}
            hint={localStamp(submittedAt)}
          />
          {pending ? (
            <TimelineItem
              state="current"
              title={t("myAttendance.requests.detail.waiting")}
              hint={[waited, item.leave ? t("myAttendance.requests.detail.withdrawHint") : ""].filter(Boolean).join(" · ")}
            />
          ) : (
            <TimelineItem
              state={key === "rejected" ? "danger" : "done"}
              title={t(`myAttendance.requests.status.${key}`, item.status)}
              hint={[reviewer ? t("myAttendance.requests.reviewedBy", { name: reviewer }) : "", localStamp(reviewedAt)].filter(Boolean).join(" · ")}
              remark={remark}
            />
          )}
          {pending ? (
            <TimelineItem state="waiting" title={t("myAttendance.requests.detail.afterApproval")} last />
          ) : null}
        </View>
      </View>
    </BusinessSheet>
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.kvRow}>
      <Text style={styles.kvLabel}>{label}</Text>
      <Text style={styles.kvValue}>{value}</Text>
    </View>
  );
}

function TimelineItem({
  state,
  title,
  hint,
  remark,
  last,
}: {
  state: "done" | "current" | "waiting" | "danger";
  title: string;
  hint?: string;
  remark?: string;
  last?: boolean;
}) {
  return (
    <View style={styles.timelineItem}>
      <View style={styles.timelineRail}>
        <View style={[styles.timelineDot, styles[`dot_${state}`]]} />
        {!last ? <View style={styles.timelineLine} /> : null}
      </View>
      <View style={[styles.flex, styles.timelineBody]}>
        <Text style={[styles.timelineTitle, state === "waiting" && styles.faint]}>{title}</Text>
        {hint ? <Text variant="bodySmall" style={styles.muted}>{hint}</Text> : null}
        {remark ? (
          <Text style={[styles.remark, state === "danger" ? styles.remarkDanger : styles.remarkOk]}>{`「${remark}」`}</Text>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  muted: { color: HB_COLORS.textSecondary },
  faint: { color: "#98A2B3" },
  warningText: { color: HB_COLORS.warning },
  pressed: { opacity: 0.75 },
  empty: { paddingVertical: HB_SPACING.md, textAlign: "center" },
  actions: { flexDirection: "row", gap: HB_SPACING.xs },
  action: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 14,
    borderWidth: 1,
    flex: 1,
    flexDirection: "row",
    gap: 10,
    padding: HB_SPACING.sm,
  },
  actionIcon: { alignItems: "center", borderRadius: 11, height: 38, justifyContent: "center", width: 38 },
  actionTitle: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "700" },
  actionHint: { color: HB_COLORS.textSecondary, fontSize: 11.5 },
  chips: { flexDirection: "row", gap: 6 },
  chip: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: 16,
    borderWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: 12,
    paddingVertical: 5,
  },
  chipSelected: { backgroundColor: HB_COLORS.textPrimary, borderColor: HB_COLORS.textPrimary },
  chipText: { color: HB_COLORS.textSecondary, fontSize: 13 },
  chipTextSelected: { color: HB_COLORS.white, fontWeight: "600" },
  group: { gap: HB_SPACING.xs },
  groupTitle: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700", marginLeft: 2 },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 14,
    borderWidth: 1,
    gap: HB_SPACING.xs,
    padding: 11,
  },
  cardPending: { borderColor: "#FEDF89" },
  cardCompact: { gap: 6, paddingVertical: 9 },
  cardHeader: { alignItems: "center", flexDirection: "row", gap: 10 },
  cardTitle: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "700" },
  cardTitleCompact: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "700" },
  typeIcon: { alignItems: "center", borderRadius: 10, height: 36, justifyContent: "center", width: 36 },
  typeIconMuted: { backgroundColor: HB_COLORS.surfaceMuted },
  cardFooter: {
    alignItems: "center",
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    paddingTop: HB_SPACING.xs,
  },
  withdraw: { borderRadius: 16 },
  withdrawLabel: { fontSize: 12.5, marginVertical: 3 },
  remark: { borderRadius: 10, fontSize: 12.5, paddingHorizontal: 9, paddingVertical: 7 },
  remarkDanger: { backgroundColor: ATTENDANCE_STATUS_TONES.danger.background, color: "#912018" },
  remarkOk: { backgroundColor: ATTENDANCE_STATUS_TONES.success.background, color: "#05603A" },
  steps: { gap: 3, marginLeft: 46 },
  stepTrack: { alignItems: "center", flexDirection: "row", paddingHorizontal: 12 },
  stepDot: { backgroundColor: HB_COLORS.outline, borderRadius: 4, height: 8, width: 8 },
  stepLine: { backgroundColor: HB_COLORS.outline, flex: 1, height: 2, marginHorizontal: 4 },
  stepDone: { backgroundColor: "#12B76A" },
  stepCurrent: { backgroundColor: "#F79009", borderColor: "#FEF0C7", borderRadius: 6, borderWidth: 2, height: 12, width: 12 },
  stepLabels: { flexDirection: "row", justifyContent: "space-between" },
  stepLabel: { color: HB_COLORS.textSecondary, fontSize: 11 },
  stepLabelCurrent: { color: HB_COLORS.warning, fontWeight: "700" },
  pickerContent: { gap: HB_SPACING.xs },
  detailContent: { gap: HB_SPACING.sm },
  detailCard: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
  },
  detailHead: { alignItems: "center", flexDirection: "row", gap: 10 },
  detailWithdraw: { borderColor: "#FDA29B", borderRadius: 22 },
  kv: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, gap: 6, paddingTop: HB_SPACING.xs },
  kvRow: { flexDirection: "row", gap: HB_SPACING.sm },
  kvLabel: { color: HB_COLORS.textSecondary, fontSize: 13, width: 56 },
  kvValue: { color: HB_COLORS.textPrimary, flex: 1, fontSize: 13 },
  timelineItem: { flexDirection: "row", gap: 10 },
  timelineRail: { alignItems: "center", width: 12 },
  timelineDot: { borderRadius: 6, height: 12, marginTop: 3, width: 12 },
  timelineLine: { backgroundColor: HB_COLORS.outlineMuted, flex: 1, marginTop: 2, width: 2 },
  timelineBody: { gap: 2, paddingBottom: HB_SPACING.sm },
  timelineTitle: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "700" },
  dot_done: { backgroundColor: "#12B76A" },
  dot_current: { backgroundColor: HB_COLORS.white, borderColor: "#F79009", borderWidth: 3 },
  dot_waiting: { backgroundColor: HB_COLORS.outline },
  dot_danger: { backgroundColor: "#F04438" },
});
