import type { ReactNode } from "react";
import { ActivityIndicator, Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type {
  ReleaseCenterTarget,
  ReleaseLaneStatus,
} from "../release-center-nav";
import {
  Panel,
  ScreenFrame,
  SecondaryButton,
  SectionHeader,
  StatusDot,
  type Tone,
} from "../ui";
import {
  OVERVIEW_TERMINALS,
  describeLaneChange,
  fill,
  formatReleaseTime,
  type OverviewTerminal,
  type ReleaseAttentionItem,
  type ReleaseLaneMode,
  type ReleaseLaneSummary,
  type ReleaseOverviewCopy,
} from "./logic";
import { useReleaseOverview, useReleaseOverviewCopy } from "./use-release-overview";

// 设计稿里的标签配色：HB token 里没有对应值，只在本视图使用。
const TAG_COLORS: Record<ReleaseLaneMode | "scope", { bg: string; fg: string }> = {
  optional: { bg: "#EAF2FF", fg: "#0958D9" },
  minimum: { bg: "#FEF0C7", fg: "#93370D" },
  force: { bg: "#FFEAD5", fg: "#B93815" },
  scope: { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary },
};
const ATTENTION_SOFT = "#FFFAEB";
const ATTENTION_BORDER = "#FEDF89";
const ICON_MUTED = "#667085";

const STATUS_TONE: Record<ReleaseLaneStatus, Tone> = {
  active: "success",
  pending: "warning",
  inactive: "neutral",
  error: "danger",
};

const TERMINAL_ICONS: Record<OverviewTerminal, string> = {
  mobile: "cellphone",
  ipad: "tablet",
  handheld: "barcode-scan",
  wpf: "monitor",
};

/**
 * 版本发布中心「投放总览」：待处理、按终端分组的线路状态、最近策略变更。
 * 标题与终端切换由外壳通过 header 传入；数据由本视图自行读取（单条线路失败只影响自己）。
 */
export function ReleaseOverviewView({
  header,
  refreshing,
  onRefresh,
  onOpenLane,
}: {
  header: ReactNode;
  refreshing: boolean;
  onRefresh: () => void;
  onOpenLane: (target: ReleaseCenterTarget) => void;
}) {
  const copy = useReleaseOverviewCopy();
  const overview = useReleaseOverview();

  let body: ReactNode;
  if (overview.isLoading) {
    body = (
      <Panel style={styles.stateBox}>
        <ActivityIndicator color={HB_COLORS.action} />
        <Text style={styles.caption}>{copy.laneLoading}</Text>
      </Panel>
    );
  } else if (overview.isAllFailed) {
    body = (
      <Panel style={styles.stateBox}>
        <Icon source="alert-circle-outline" size={28} color={HB_COLORS.danger} />
        <Text style={styles.stateTitle}>{copy.errors.allFailed}</Text>
        <Text style={styles.caption}>{copy.errors.allFailedHint}</Text>
        <SecondaryButton
          label={copy.errors.retry}
          icon="refresh"
          onPress={() => void overview.refetch()}
        />
      </Panel>
    );
  } else {
    body = (
      <>
        {overview.attention.length ? (
          <View style={styles.section}>
            <SectionHeader
              title={fill(copy.attention.title, {
                count: overview.attention.length,
              })}
            />
            {overview.attention.map((item) => (
              <AttentionCard
                key={item.id}
                item={item}
                copy={copy}
                onPress={() => onOpenLane(item.navTarget)}
              />
            ))}
          </View>
        ) : null}

        <View style={styles.section}>
          <SectionHeader title={copy.statusTitle} />
          {overview.hasFailure ? (
            <Text style={styles.warningText}>{copy.errors.partialFailed}</Text>
          ) : null}
          {OVERVIEW_TERMINALS.map((terminal) => (
            <TerminalGroup
              key={terminal}
              terminal={terminal}
              status={overview.terminalStatus[terminal]}
              lanes={overview.lanes.filter((lane) => lane.terminal === terminal)}
              copy={copy}
              onOpenLane={onOpenLane}
            />
          ))}
        </View>

        <View style={styles.section}>
          <SectionHeader title={copy.recent.title} />
          <Panel>
            {overview.recent.length ? (
              overview.recent.map((lane, index) => (
                <RecentRow
                  key={lane.key}
                  lane={lane}
                  copy={copy}
                  divider={index > 0}
                  onPress={() => onOpenLane(lane.navTarget)}
                />
              ))
            ) : (
              <Text style={[styles.caption, styles.emptyText]}>
                {copy.recent.empty}
              </Text>
            )}
          </Panel>
        </View>
      </>
    );
  }

  return (
    <ScreenFrame header={header} refreshing={refreshing} onRefresh={onRefresh}>
      <Legend copy={copy} />
      {body}
    </ScreenFrame>
  );
}

function Legend({ copy }: { copy: ReleaseOverviewCopy }) {
  const items: { tone: Tone; label: string }[] = [
    { tone: "success", label: copy.legend.active },
    { tone: "warning", label: copy.legend.pending },
    { tone: "neutral", label: copy.legend.inactive },
  ];
  return (
    <View style={styles.legend}>
      {items.map((item) => (
        <View key={item.label} style={styles.legendItem}>
          <StatusDot tone={item.tone} />
          <Text style={styles.caption}>{item.label}</Text>
        </View>
      ))}
    </View>
  );
}

function Tag({ label, kind }: { label: string; kind: ReleaseLaneMode | "scope" }) {
  const palette = TAG_COLORS[kind];
  return (
    <View style={[styles.tag, { backgroundColor: palette.bg }]}>
      <Text numberOfLines={1} style={[styles.tagLabel, { color: palette.fg }]}>
        {label}
      </Text>
    </View>
  );
}

function AttentionCard({
  item,
  copy,
  onPress,
}: {
  item: ReleaseAttentionItem;
  copy: ReleaseOverviewCopy;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={fill(copy.a11y.openAttention, {
        lane: item.laneLabel,
        title: item.title,
      })}
      onPress={onPress}
      style={({ pressed }) => [styles.attentionCard, pressed && styles.attentionPressed]}
    >
      <View style={styles.flexText}>
        <View style={styles.inlineRow}>
          <StatusDot tone="warning" />
          <Text numberOfLines={1} style={[styles.caption, styles.flexText]}>
            {item.laneLabel}
          </Text>
        </View>
        <Text style={styles.attentionTitle}>{item.title}</Text>
        <Text style={styles.caption}>{item.description}</Text>
      </View>
      {/* 整卡已是按钮，这里只做视觉提示。 */}
      <View style={styles.inlineRow}>
        <Text style={styles.goLabel}>{copy.attention.go}</Text>
        <Icon source="chevron-right" size={18} color={HB_COLORS.action} />
      </View>
    </Pressable>
  );
}

function TerminalGroup({
  terminal,
  status,
  lanes,
  copy,
  onOpenLane,
}: {
  terminal: OverviewTerminal;
  status: ReleaseLaneStatus | null;
  lanes: ReleaseLaneSummary[];
  copy: ReleaseOverviewCopy;
  onOpenLane: (target: ReleaseCenterTarget) => void;
}) {
  return (
    <Panel>
      <View style={styles.groupHeader}>
        <View style={styles.groupIcon}>
          <Icon source={TERMINAL_ICONS[terminal]} size={20} color={HB_COLORS.action} />
        </View>
        <View style={styles.flexText}>
          <Text accessibilityRole="header" style={styles.groupTitle}>
            {copy.terminals[terminal]}
          </Text>
          <Text numberOfLines={1} style={styles.caption}>
            {copy.terminalSubtitles[terminal]}
          </Text>
        </View>
        {status ? <StatusDot tone={STATUS_TONE[status]} /> : null}
      </View>
      {lanes.map((lane) => (
        <LaneRow
          key={lane.key}
          lane={lane}
          copy={copy}
          onPress={() => onOpenLane(lane.navTarget)}
        />
      ))}
    </Panel>
  );
}

function laneHeadline(lane: ReleaseLaneSummary, copy: ReleaseOverviewCopy) {
  if (lane.loading) return copy.laneLoading;
  if (lane.status === "error") return copy.legend.error;
  if (lane.status === "inactive") return copy.notEnabled;
  return lane.target ?? copy.noTarget;
}

function LaneRow({
  lane,
  copy,
  onPress,
}: {
  lane: ReleaseLaneSummary;
  copy: ReleaseOverviewCopy;
  onPress: () => void;
}) {
  const headline = laneHeadline(lane, copy);
  const live = !lane.loading && (lane.status === "active" || lane.status === "pending");
  const statusLabel = lane.loading ? copy.laneLoading : copy.legend[lane.status];
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={fill(copy.a11y.openLane, {
        lane: lane.fullLabel,
        status: statusLabel,
        target: headline,
      })}
      onPress={onPress}
      style={({ pressed }) => [styles.laneRow, pressed && styles.pressed]}
    >
      <Text style={styles.laneLabel}>{lane.label}</Text>
      <View style={styles.laneBody}>
        <View style={styles.inlineRow}>
          {lane.loading ? (
            <ActivityIndicator size="small" color={ICON_MUTED} />
          ) : (
            <StatusDot tone={STATUS_TONE[lane.status]} />
          )}
          <Text
            numberOfLines={1}
            style={[
              styles.flexText,
              live ? styles.laneTarget : styles.laneMuted,
              lane.status === "error" && !lane.loading && styles.laneError,
            ]}
          >
            {headline}
          </Text>
        </View>
        {live && lane.detail ? (
          <Text numberOfLines={1} style={styles.caption}>
            {lane.detail}
          </Text>
        ) : null}
        {lane.status === "error" && !lane.loading ? (
          <Text numberOfLines={2} style={styles.caption}>
            {copy.laneFailed}
          </Text>
        ) : null}
        {live && (lane.mode || lane.scope) ? (
          <View style={styles.tags}>
            {lane.mode ? <Tag kind={lane.mode} label={copy.modes[lane.mode]} /> : null}
            {lane.scope ? <Tag kind="scope" label={lane.scope} /> : null}
          </View>
        ) : null}
      </View>
      <Icon source="chevron-right" size={20} color={ICON_MUTED} />
    </Pressable>
  );
}

function RecentRow({
  lane,
  copy,
  divider,
  onPress,
}: {
  lane: ReleaseLaneSummary;
  copy: ReleaseOverviewCopy;
  divider: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={({ pressed }) => [
        styles.recentRow,
        divider && styles.rowDivider,
        pressed && styles.pressed,
      ]}
    >
      <View style={styles.recentHead}>
        <Text style={styles.recentTime}>{formatReleaseTime(lane.updatedAt)}</Text>
        <Text numberOfLines={1} style={[styles.caption, styles.recentOperator]}>
          {lane.updatedBy ?? copy.recent.unknownOperator}
        </Text>
      </View>
      <Text numberOfLines={1} style={styles.recentLane}>
        {lane.fullLabel}
      </Text>
      <Text numberOfLines={2} style={styles.recentSummary}>
        {describeLaneChange(lane, copy)}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  caption: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  flexText: { flex: 1, minWidth: 0 },
  inlineRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  section: { gap: HB_SPACING.sm },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  legend: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    gap: HB_SPACING.md,
    marginBottom: -HB_SPACING.sm,
  },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 6 },
  stateBox: {
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingVertical: HB_SPACING.lg,
    paddingHorizontal: HB_SPACING.md,
  },
  stateTitle: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  warningText: { fontSize: 13, lineHeight: 20, color: HB_COLORS.warning },
  emptyText: { padding: HB_SPACING.md, textAlign: "center" },
  tag: {
    height: 22,
    paddingHorizontal: 8,
    borderRadius: 6,
    justifyContent: "center",
    flexShrink: 1,
  },
  tagLabel: { fontSize: 12, lineHeight: 16, fontWeight: "500" },
  tags: { flexDirection: "row", flexWrap: "wrap", gap: 6, marginTop: 2 },
  attentionCard: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    padding: 14,
    backgroundColor: ATTENTION_SOFT,
    borderWidth: 1,
    borderColor: ATTENTION_BORDER,
    borderRadius: HB_RADIUS.surface,
  },
  attentionPressed: { opacity: 0.85 },
  attentionTitle: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    marginTop: 2,
  },
  goLabel: { fontSize: 13, fontWeight: "600", color: HB_COLORS.action },
  groupHeader: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  groupIcon: {
    width: 36,
    height: 36,
    borderRadius: 10,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#EEF4FF",
  },
  groupTitle: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
  },
  laneRow: {
    minHeight: 56,
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  laneLabel: {
    width: 96,
    fontSize: 13,
    lineHeight: 20,
    color: HB_COLORS.textSecondary,
  },
  laneBody: { flex: 1, minWidth: 0, gap: 2 },
  laneTarget: {
    fontSize: 15,
    lineHeight: 20,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  laneMuted: { fontSize: 14, lineHeight: 20, color: HB_COLORS.textSecondary },
  laneError: { color: HB_COLORS.danger },
  rowDivider: { borderTopWidth: 1, borderTopColor: HB_COLORS.outlineMuted },
  recentRow: {
    minHeight: 44,
    gap: 2,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  recentHead: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
  },
  recentTime: {
    fontSize: 12,
    lineHeight: 18,
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  recentOperator: { flexShrink: 1, textAlign: "right" },
  recentLane: {
    fontSize: 14,
    lineHeight: 20,
    fontWeight: "600",
    color: HB_COLORS.action,
  },
  recentSummary: { fontSize: 13, lineHeight: 20, color: HB_COLORS.textPrimary },
});
