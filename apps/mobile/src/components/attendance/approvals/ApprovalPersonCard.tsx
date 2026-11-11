// 同一人同一天有 2 条以上审核时合并成一张卡：卡头写人和排班，下面一条小时间条标出出问题的打卡，
// 再逐条列出（编号 1、2、3），每条仍各自拒绝 / 批准。单条审核仍用普通 ApprovalCard。
import type { ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import {
  buildPunchDayStrip,
  formatClockMinutes,
  timelinePercent,
  type PunchDayStrip,
} from "@/modules/attendance/approval-board";
import type { AttendanceApproval } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PersonAvatar, TIMELINE_COLORS } from "./approval-ui";

/** 时间条刻度文字之间至少隔开这么多百分比，避免挤在一起。 */
const LABEL_GAP_PERCENT = 11;

export function ApprovalPersonCard({
  items,
  isSelf,
  processed,
  submittedLabel,
  children,
}: {
  items: AttendanceApproval[];
  isSelf: boolean;
  processed: boolean;
  /** 组内最新一条的提交时间文案。 */
  submittedLabel: string;
  /** 逐条渲染好的 ApprovalCard（合并模式）。 */
  children: ReactNode[];
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const first = items[0];
  const roleLabel = first.isStoreManager ? t("approvals.board.roleManager") : t("approvals.board.roleStaff");
  const withWindow = items.find((item) => item.scheduleStartTime && item.scheduleEndTime);
  const scheduleText = withWindow
    ? t("approvals.board.scheduleWindow", { start: withWindow.scheduleStartTime!.slice(0, 5), end: withWindow.scheduleEndTime!.slice(0, 5) })
    : undefined;
  const strip = buildPunchDayStrip(items);

  return (
    <View style={[styles.card, isSelf && !processed ? styles.selfCard : null]}>
      <View style={styles.header}>
        <PersonAvatar name={first.employeeName} />
        <View style={styles.headerBody}>
          <Text variant="titleMedium" style={styles.name} numberOfLines={1}>
            {isSelf ? t("approvals.board.selfName", { name: first.employeeName ?? "" }) : first.employeeName}
          </Text>
          <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>
            {[roleLabel, scheduleText].filter(Boolean).join(" · ")}
          </Text>
        </View>
        <View style={styles.headerAside}>
          <Text variant="labelMedium" style={processed ? styles.muted : styles.pendingCount}>
            {processed
              ? t("approvals.board.punchInfo.groupProcessed", { count: items.length })
              : t("approvals.board.punchInfo.groupPending", { count: items.length })}
          </Text>
          <Text variant="bodySmall" style={styles.submitted}>{submittedLabel}</Text>
        </View>
      </View>
      {strip ? <PunchDayStripView strip={strip} scheduleLabel={t("approvals.board.timelineSchedule")} /> : null}
      {children.map((child, index) => (
        <View key={items[index]?.approvalGuid ?? index} style={styles.row}>
          {child}
        </View>
      ))}
    </View>
  );
}

/** 小时间条：浅蓝底 = 排班窗口，红竖线 = 异常打卡，蓝竖线 = 补卡申请的时间。 */
function PunchDayStripView({ strip, scheduleLabel }: { strip: PunchDayStrip; scheduleLabel: string }) {
  const at = (minutes: number) => timelinePercent(minutes, strip.range);
  const scheduleLeft = at(strip.schedule.start);
  const scheduleRight = at(strip.schedule.end);
  // 刻度文字：异常打卡优先，其次补卡时间，最后排班起止；和已放下的文字离得太近就省略。
  type Label = { key: string; minutes: number; tone: "schedule" | "anomaly" | "request" };
  const candidates: Label[] = [
    ...strip.markers.filter((marker) => marker.tone === "anomaly"),
    ...strip.markers.filter((marker) => marker.tone === "request"),
    { key: "start", minutes: strip.schedule.start, tone: "schedule" as const },
    { key: "end", minutes: strip.schedule.end, tone: "schedule" as const },
  ].map((item): Label => ({ key: item.key, minutes: item.minutes, tone: item.tone }));
  const labels = candidates.reduce<Label[]>((kept, label) => {
    if (kept.every((other) => Math.abs(at(label.minutes) - at(other.minutes)) >= LABEL_GAP_PERCENT)) kept.push(label);
    return kept;
  }, []);

  return (
    <View
      style={styles.strip}
      accessible
      accessibilityLabel={`${scheduleLabel} ${formatClockMinutes(strip.schedule.start)}–${formatClockMinutes(strip.schedule.end)}`}
    >
      <View style={styles.track}>
        <View style={[styles.schedule, { left: `${scheduleLeft}%`, width: `${Math.max(1, scheduleRight - scheduleLeft)}%` }]} />
        {strip.markers.map((marker) => (
          <View
            key={marker.key}
            style={[styles.marker, { left: `${at(marker.minutes)}%` }, marker.tone === "anomaly" ? styles.markerAnomaly : styles.markerRequest]}
          />
        ))}
      </View>
      <View style={styles.labels}>
        {labels.map((label) => (
          <Text
            key={label.key}
            variant="labelSmall"
            numberOfLines={1}
            style={[
              styles.label,
              { left: `${at(label.minutes)}%` },
              label.tone === "anomaly" ? styles.labelAnomaly : label.tone === "request" ? styles.labelRequest : null,
            ]}
          >
            {formatClockMinutes(label.minutes)}
          </Text>
        ))}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.md,
    paddingBottom: HB_SPACING.xxs,
  },
  selfCard: { backgroundColor: "#FCFCFD", borderColor: HB_COLORS.outline, borderStyle: "dashed", borderWidth: 1 },
  header: { alignItems: "flex-start", flexDirection: "row", gap: HB_SPACING.sm },
  headerBody: { flex: 1, gap: 2, minWidth: 0 },
  headerAside: { alignItems: "flex-end", gap: 2 },
  name: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  muted: { color: HB_COLORS.textSecondary },
  pendingCount: { color: HB_COLORS.warning, fontWeight: "700" },
  submitted: { color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  row: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  strip: { marginBottom: HB_SPACING.xs, marginTop: HB_SPACING.sm },
  track: { backgroundColor: TIMELINE_COLORS.track, borderRadius: 5, height: 10 },
  schedule: { backgroundColor: "#D1E9FF", borderRadius: 5, bottom: 0, position: "absolute", top: 0 },
  marker: { borderRadius: 1, height: 20, marginLeft: -1, position: "absolute", top: -5, width: 2 },
  markerAnomaly: { backgroundColor: HB_COLORS.danger },
  markerRequest: { backgroundColor: HB_COLORS.action },
  labels: { height: 16, marginTop: 6 },
  // 文字以刻度为中心：先放到 left%，再左移自身一半宽度（RN 不支持百分比 translate，用固定宽度）。
  label: { color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"], marginLeft: -22, position: "absolute", textAlign: "center", width: 44 },
  labelAnomaly: { color: HB_COLORS.danger, fontWeight: "700" },
  labelRequest: { color: HB_COLORS.action, fontWeight: "700" },
});
