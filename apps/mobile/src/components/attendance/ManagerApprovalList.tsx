import { useEffect, useMemo, useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Icon, Text } from "react-native-paper";
import {
  isKnownAttendanceApprovalSourceType,
  type KnownAttendanceApprovalSourceType,
} from "@/modules/attendance/attendance-approval";
import {
  findCoveringAdjustment,
  groupApprovalsByPerson,
  groupApprovalsByWorkDate,
  isSelfApproval,
} from "@/modules/attendance/approval-board";
import { toAttendanceUtcMillis } from "@/modules/attendance/attendance-device-time";
import { DEFAULT_ATTENDANCE_LEAVE_RULES } from "@/modules/attendance/leave-types";
import type {
  AttendanceApproval,
  AttendanceApprovalPayload,
  AttendanceApprovalReconcileSummary,
} from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { listSeparator } from "@/shared/i18n/punctuation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ApprovalCard, type ApprovalDetailKind } from "./approvals/ApprovalCard";
import { ApprovalPersonCard } from "./approvals/ApprovalPersonCard";
import { PunchReviewDetail } from "./approvals/PunchReviewDetail";
import { MissingClockOutDetail } from "./approvals/MissingClockOutDetail";
import { OvertimeReviewDetail } from "./approvals/OvertimeReviewDetail";
import { SickLeaveReviewDetail } from "./approvals/SickLeaveReviewDetail";
import { useApprovalDateLabels } from "./approvals/use-approval-labels";

type ApprovalFilter = "all" | KnownAttendanceApprovalSourceType;
type BoardTab = "pending" | "processed";

/** 筛选 Chip 的固定顺序：最常处理的补卡/加班在前，打卡异常放最后。 */
const FILTER_TYPES: KnownAttendanceApprovalSourceType[] = [
  "PunchAdjustment",
  "Overtime",
  "MissingClockOut",
  "MealBreak",
  "Leave",
  "PublicHolidayPay",
  "Punch",
];

/**
 * 店长审核看板（移动端）：待审核 / 已处理两个页签。
 * 待审核顶部显示打开时对账新增了几条，列表按工作日分组（最近的在上），
 * 加班、漏下班、带薪病假进整页详情处理，其余在卡片上一键处理。
 */
export function ManagerApprovalList({
  approvals,
  reconcile,
  processedApprovals,
  isProcessedLoading,
  onShowProcessed,
  isBusy,
  canReview = true,
  canReviewSelf = false,
  canAdjustPunch = false,
  currentUserGuid,
  storeCode,
  storeName,
  sickLeaveSubmitDays = DEFAULT_ATTENDANCE_LEAVE_RULES.sickLeaveSubmitDays,
  emptyMessage,
  onApprove,
  onReject,
  onMissingClockOutResolved,
}: {
  approvals: AttendanceApproval[];
  reconcile?: AttendanceApprovalReconcileSummary;
  processedApprovals?: AttendanceApproval[];
  isProcessedLoading?: boolean;
  /** 切到「已处理」时通知父组件开始拉取（避免每次进审核都多一个请求）。 */
  onShowProcessed?: () => void;
  isBusy: boolean;
  canReview?: boolean;
  /** Attendance.Approval.ReviewSelf：可审本人申请（二次确认）。 */
  canReviewSelf?: boolean;
  /** Attendance.Punch.AdjustManagedStore：漏下班补下班卡。 */
  canAdjustPunch?: boolean;
  currentUserGuid?: string;
  storeCode?: string;
  storeName?: string;
  sickLeaveSubmitDays?: number;
  emptyMessage?: string;
  onApprove: (payload: AttendanceApprovalPayload) => Promise<void>;
  onReject: (payload: AttendanceApprovalPayload) => Promise<void>;
  onMissingClockOutResolved: (message: string) => Promise<void> | void;
}) {
  const { t, language } = useAppTranslation(["attendance", "common"]);
  const { weekdayDate, tierRate, submittedLabel } = useApprovalDateLabels();
  const [tab, setTab] = useState<BoardTab>("pending");
  const [filter, setFilter] = useState<ApprovalFilter>("all");
  const [detail, setDetail] = useState<{ kind: ApprovalDetailKind; approvalGuid: string }>();
  // 打卡异常详情：待审与已处理都能打开（已处理只读）。
  const [punchDetailGuid, setPunchDetailGuid] = useState<string>();
  // 相对时间每分钟刷新一次即可（「刚刚」→「1 分钟前」）。
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 60_000);
    return () => clearInterval(timer);
  }, []);

  const source = useMemo(
    () => (tab === "pending" ? approvals : processedApprovals ?? []),
    [approvals, processedApprovals, tab],
  );
  const typeCounts = useMemo(() => {
    const counts: Partial<Record<KnownAttendanceApprovalSourceType, number>> = {};
    for (const item of source) {
      if (isKnownAttendanceApprovalSourceType(item.sourceType)) {
        counts[item.sourceType] = (counts[item.sourceType] ?? 0) + 1;
      }
    }
    return counts;
  }, [source]);
  // 选中类型被处理完（数量归零）后自动回到「全部」，避免停留在空列表。
  const activeFilter: ApprovalFilter = filter !== "all" && (typeCounts[filter] ?? 0) > 0 ? filter : "all";
  const visible = activeFilter === "all" ? source : source.filter((item) => item.sourceType === activeFilter);
  const groups = useMemo(() => groupApprovalsByWorkDate(visible), [visible]);
  const detailApproval = detail ? approvals.find((item) => item.approvalGuid === detail.approvalGuid) : undefined;
  const punchDetailApproval = punchDetailGuid ? source.find((item) => item.approvalGuid === punchDetailGuid && item.punch) : undefined;

  const switchTab = (next: BoardTab) => {
    setTab(next);
    setFilter("all");
    setPunchDetailGuid(undefined);
    if (next === "processed") onShowProcessed?.();
  };

  const reconcileText = (() => {
    if (!reconcile) return undefined;
    if (reconcile.failed) return t("approvals.board.reconcile.failed");
    const parts = [
      reconcile.createdMissingClockOutCount
        ? t("approvals.board.reconcile.missingClockOut", { count: reconcile.createdMissingClockOutCount })
        : "",
      reconcile.createdOvertimeCount
        ? t("approvals.board.reconcile.overtime", { count: reconcile.createdOvertimeCount })
        : "",
    ].filter(Boolean);
    const when = reconcile.reconciledAt ? submittedLabel(reconcile.reconciledAt, now) : "";
    const body = parts.length
      ? t("approvals.board.reconcile.created", { days: reconcile.lookbackDays, items: parts.join(listSeparator(language)) })
      : t("approvals.board.reconcile.none", { days: reconcile.lookbackDays });
    return when ? `${body} · ${when}` : body;
  })();

  const renderChip = (key: ApprovalFilter, label: string, count: number) => {
    const selected = activeFilter === key;
    return (
      <Pressable
        key={key}
        accessibilityRole="button"
        accessibilityState={{ selected }}
        onPress={() => setFilter(key)}
        style={[styles.chip, selected ? styles.chipSelected : null]}
      >
        <Text variant="labelLarge" style={[styles.chipText, selected ? styles.chipTextSelected : null]}>
          {`${label} ${count}`}
        </Text>
      </Pressable>
    );
  };

  return (
    <View style={styles.root}>
      <View style={styles.tabs} accessibilityRole="tablist">
        {(["pending", "processed"] as const).map((key) => {
          const selected = tab === key;
          return (
            <Pressable
              key={key}
              accessibilityRole="tab"
              accessibilityState={{ selected }}
              onPress={() => switchTab(key)}
              style={[styles.tab, selected ? styles.tabSelected : null]}
            >
              <Text variant="titleSmall" style={selected ? styles.tabTextSelected : styles.tabText}>
                {key === "pending" ? t("approvals.board.tabPending") : t("approvals.board.tabProcessed")}
                {key === "pending" && approvals.length ? (
                  <Text variant="titleSmall" style={styles.tabCount}>{` ${approvals.length}`}</Text>
                ) : null}
              </Text>
            </Pressable>
          );
        })}
      </View>

      {tab === "pending" && reconcileText ? (
        <View style={[styles.banner, reconcile?.failed ? styles.bannerWarning : null]} accessibilityLiveRegion="polite">
          <Icon
            source={reconcile?.failed ? "alert-outline" : "sync"}
            size={18}
            color={reconcile?.failed ? HB_COLORS.warning : "#175CD3"}
          />
          <Text variant="bodyMedium" style={[styles.bannerText, reconcile?.failed ? styles.bannerTextWarning : null]}>
            {reconcileText}
          </Text>
        </View>
      ) : null}

      {source.length ? (
        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          accessibilityLabel={t("approvals.filterA11y")}
          contentContainerStyle={styles.chips}
        >
          {renderChip("all", t("approvals.filterAll"), source.length)}
          {FILTER_TYPES.filter((type) => (typeCounts[type] ?? 0) > 0).map((type) =>
            renderChip(
              type,
              type === "Punch" ? t("approvals.presentation.Punch.title") : t(`approvals.board.filters.${type}`),
              typeCounts[type] ?? 0,
            ))}
        </ScrollView>
      ) : null}

      {tab === "processed" && isProcessedLoading ? <ActivityIndicator style={styles.loading} /> : null}

      {groups.map((group) => (
        <View key={group.workDate || "none"} style={styles.group}>
          <View style={styles.groupHeader}>
            <Text variant="labelLarge" style={styles.groupTitle}>
              {group.workDate ? weekdayDate(group.workDate) : t("approvals.board.noDate")}
            </Text>
            <Text variant="bodySmall" style={styles.groupMeta}>
              {[
                t("approvals.board.itemCount", { count: group.items.length }),
                group.tier !== "weekday" ? (group.holidayName ? `${group.holidayName}` : tierRate(group.tier)) : "",
              ].filter(Boolean).join(" · ")}
            </Text>
          </View>
          {groupApprovalsByPerson(group.items).map((person) => {
            const renderCard = (item: AttendanceApproval, grouped?: { index: number }) => {
              const covering = grouped ? findCoveringAdjustment(person.items, item) : undefined;
              return (
                <ApprovalCard
                  key={item.approvalGuid}
                  item={item}
                  submittedLabel={submittedLabel(item.submittedAt, now)}
                  isSelf={isSelfApproval(item, currentUserGuid)}
                  canReview={canReview}
                  canReviewSelf={canReviewSelf}
                  isBusy={isBusy}
                  processed={tab === "processed"}
                  grouped={grouped}
                  coveredByIndex={covering ? person.items.indexOf(covering) + 1 : undefined}
                  onOpenDetail={(kind) => setDetail({ kind, approvalGuid: item.approvalGuid })}
                  onOpenPunchDetail={() => setPunchDetailGuid(item.approvalGuid)}
                  onApprove={onApprove}
                  onReject={onReject}
                />
              );
            };
            // 同一人同一天只有 1 条：照旧一张卡；2 条以上合并成一张，逐条编号。
            if (person.items.length === 1) return renderCard(person.items[0]);
            const latest = person.items.reduce((current, item) =>
              (toAttendanceUtcMillis(item.submittedAt) ?? 0) > (toAttendanceUtcMillis(current.submittedAt) ?? 0) ? item : current);
            return (
              <ApprovalPersonCard
                key={person.key}
                items={person.items}
                isSelf={isSelfApproval(person.items[0], currentUserGuid)}
                processed={tab === "processed"}
                submittedLabel={submittedLabel(latest.submittedAt, now)}
              >
                {person.items.map((item, index) => renderCard(item, { index: index + 1 }))}
              </ApprovalPersonCard>
            );
          })}
        </View>
      ))}

      {!source.length && !(tab === "processed" && isProcessedLoading) ? (
        <View style={styles.empty}>
          <Icon source="inbox-outline" size={28} color={HB_COLORS.outline} />
          <Text variant="bodyMedium" style={styles.muted}>
            {tab === "pending" ? emptyMessage ?? t("approvals.empty") : t("approvals.board.processedEmpty")}
          </Text>
        </View>
      ) : null}

      {detail && detailApproval && detail.kind === "overtime" ? (
        <OvertimeReviewDetail
          approval={detailApproval}
          storeCode={storeCode}
          storeName={storeName}
          canReview={canReview && (!isSelfApproval(detailApproval, currentUserGuid) || canReviewSelf)}
          isBusy={isBusy}
          onClose={() => setDetail(undefined)}
          onApprove={onApprove}
          onReject={onReject}
        />
      ) : null}
      {detail && detailApproval && detail.kind === "missingClockOut" ? (
        <MissingClockOutDetail
          approval={detailApproval}
          storeCode={storeCode}
          canReview={canReview && (!isSelfApproval(detailApproval, currentUserGuid) || canReviewSelf)}
          canAdjust={canAdjustPunch}
          isBusy={isBusy}
          onClose={() => setDetail(undefined)}
          onReject={onReject}
          onResolved={onMissingClockOutResolved}
        />
      ) : null}
      {punchDetailApproval ? (
        <PunchReviewDetail
          approval={punchDetailApproval}
          coveringAdjustment={findCoveringAdjustment(source, punchDetailApproval)}
          storeCode={storeCode}
          storeName={storeName}
          canReview={canReview && (!isSelfApproval(punchDetailApproval, currentUserGuid) || canReviewSelf)}
          processed={tab === "processed"}
          isBusy={isBusy}
          onClose={() => setPunchDetailGuid(undefined)}
          onApprove={onApprove}
          onReject={onReject}
        />
      ) : null}
      {detail && detailApproval && detail.kind === "sickLeave" ? (
        <SickLeaveReviewDetail
          approval={detailApproval}
          canReview={canReview && (!isSelfApproval(detailApproval, currentUserGuid) || canReviewSelf)}
          isBusy={isBusy}
          submitLimitDays={sickLeaveSubmitDays}
          onClose={() => setDetail(undefined)}
          onApprove={onApprove}
          onReject={onReject}
        />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    alignItems: "flex-start",
    backgroundColor: "#EFF8FF",
    borderRadius: HB_RADIUS.surface,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
  },
  bannerText: { color: "#175CD3", flex: 1 },
  bannerTextWarning: { color: HB_COLORS.warning },
  bannerWarning: { backgroundColor: "#FFFAEB" },
  chip: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: 999,
    borderWidth: 1,
    paddingHorizontal: 14,
    paddingVertical: 6,
  },
  chipSelected: { backgroundColor: HB_COLORS.textPrimary, borderColor: HB_COLORS.textPrimary },
  chipText: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  chipTextSelected: { color: HB_COLORS.white },
  chips: { gap: HB_SPACING.xs, paddingVertical: 2 },
  empty: { alignItems: "center", gap: HB_SPACING.xs, paddingVertical: HB_SPACING.lg },
  group: { gap: HB_SPACING.xs },
  groupHeader: { alignItems: "baseline", flexDirection: "row", justifyContent: "space-between", paddingHorizontal: 2, paddingTop: HB_SPACING.xs },
  groupMeta: { color: HB_COLORS.textSecondary },
  groupTitle: { color: HB_COLORS.textSecondary },
  loading: { paddingVertical: HB_SPACING.md },
  muted: { color: HB_COLORS.textSecondary },
  root: { gap: HB_SPACING.sm },
  tab: { alignItems: "center", borderRadius: HB_RADIUS.control, flex: 1, paddingVertical: 10 },
  tabCount: { color: HB_COLORS.danger, fontWeight: "700" },
  tabSelected: { backgroundColor: HB_COLORS.white, elevation: 1, shadowColor: "#101828", shadowOffset: { height: 1, width: 0 }, shadowOpacity: 0.06, shadowRadius: 2 },
  tabText: { color: HB_COLORS.textSecondary },
  tabTextSelected: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  tabs: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.surface, flexDirection: "row", padding: 4 },
});
