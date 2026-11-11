import { memo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Card, Icon, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { EMPLOYEE_PROFILE_REVIEW_SEGMENTS, type EmployeeProfileReviewSegment } from "./list-filters";
import {
  changesBirthday,
  formatReviewDate,
  getReviewAvatarInitial,
  getReviewHandledAt,
  getReviewPersonLabel,
  getReviewResultTone,
  getReviewWait,
  groupChangedFields,
  summarizeStores,
  type ReviewFieldGroup,
  type ReviewResultTone,
  type ReviewWaitTone,
} from "./review-list-presentation";
import type { EmployeeProfileReviewStatus, EmployeeProfileReviewSummary } from "./types";

// 审核列表的展示部件：分段控件、请求卡片、分店清单弹层。
// 这里只渲染列表摘要里的非敏感字段，不读取任何完整账号、证件号或照片。

type Translate = (key: string, options?: Record<string, unknown>) => string;

/** 状态胶囊 / 等待胶囊的配色：浅底 + 深字，对比度均 ≥ 4.5:1。 */
const TONES: Record<ReviewWaitTone | ReviewResultTone, { bg: string; fg: string; border: string }> = {
  neutral: { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary, border: HB_COLORS.surfaceMuted },
  muted: { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary, border: HB_COLORS.surfaceMuted },
  warning: { bg: "#FFFAEB", fg: HB_COLORS.warning, border: "#FEDF89" },
  danger: { bg: "#FEF3F2", fg: HB_COLORS.danger, border: "#FECDCA" },
  success: { bg: "#ECFDF3", fg: HB_COLORS.success, border: "#ECFDF3" },
};

const STATUS_ICONS: Record<EmployeeProfileReviewStatus, string> = {
  Pending: "clock-outline",
  Approved: "check-circle-outline",
  Rejected: "close-circle-outline",
  Superseded: "minus-circle-outline",
  Withdrawn: "undo-variant",
};

const GROUP_ICONS: Record<ReviewFieldGroup, string> = {
  birthday: "cake-variant-outline",
  bank: "bank-outline",
  superannuation: "piggy-bank-outline",
  identity: "card-account-details-outline",
};

// ---------------------------------------------------------------- 分段控件

interface ReviewSegmentControlProps {
  value: EmployeeProfileReviewSegment;
  onChange: (value: EmployeeProfileReviewSegment) => void;
  t: Translate;
  /** 待审核数量；null / undefined 时不显示（例如正在搜索，数量只是命中数）。 */
  pendingCount?: number | null;
}

export function ReviewSegmentControl({ value, onChange, t, pendingCount }: ReviewSegmentControlProps) {
  return (
    <View style={styles.segment} accessibilityRole="tablist">
      {EMPLOYEE_PROFILE_REVIEW_SEGMENTS.map((segment) => {
        const selected = segment === value;
        const showCount = segment === "pending" && selected && typeof pendingCount === "number";
        return (
          <Pressable
            key={segment}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(segment)}
            style={[styles.segmentItem, selected && styles.segmentItemOn]}
          >
            <Text style={[styles.segmentLabel, selected && styles.segmentLabelOn]}>{t(`segments.${segment}`)}</Text>
            {showCount ? (
              <View style={styles.countPill}>
                <Text style={styles.countText}>{pendingCount}</Text>
              </View>
            ) : null}
          </Pressable>
        );
      })}
    </View>
  );
}

// ---------------------------------------------------------------- 请求卡片

interface ReviewRequestCardProps {
  item: EmployeeProfileReviewSummary;
  language: string;
  t: Translate;
  onOpen: (item: EmployeeProfileReviewSummary) => void;
  onOpenStores: (item: EmployeeProfileReviewSummary) => void;
}

function ToneChip({ tone, icon, label, accessibilityLabel }: {
  tone: ReviewWaitTone | ReviewResultTone;
  icon: string;
  label: string;
  accessibilityLabel?: string;
}) {
  const colors = TONES[tone];
  return (
    <View
      accessible
      accessibilityLabel={accessibilityLabel ?? label}
      style={[styles.toneChip, { backgroundColor: colors.bg, borderColor: colors.border }]}
    >
      <Icon source={icon} size={14} color={colors.fg} />
      <Text style={[styles.toneChipText, { color: colors.fg }]} numberOfLines={1}>{label}</Text>
    </View>
  );
}

function ReviewRequestCardBase({ item, language, t, onOpen, onOpenStores }: ReviewRequestCardProps) {
  const person = getReviewPersonLabel(item);
  const stores = summarizeStores(item.storeNames);
  const groups = groupChangedFields(item.changedFields);
  const birthdayChanged = changesBirthday(item.changedFields);
  const isPending = item.status === "Pending";
  const wait = isPending ? getReviewWait(item.submittedAt) : null;
  // 等了 8 天以上的待审核申请加左侧红条，一眼能找到最该先处理的。
  const urgent = wait?.tone === "danger";

  let rightChip;
  let rightText: string;
  if (wait) {
    const label = wait.kind === "days"
      ? t("wait.days", { count: wait.days })
      : t(`wait.${wait.kind}`);
    rightChip = (
      <ToneChip
        tone={wait.tone}
        icon="clock-outline"
        label={label}
        accessibilityLabel={wait.kind === "days" ? t("wait.accessibility", { count: wait.days }) : label}
      />
    );
    rightText = formatReviewDate(item.submittedAt, language);
  } else {
    rightChip = (
      <ToneChip
        tone={getReviewResultTone(item.status)}
        icon={STATUS_ICONS[item.status]}
        label={t(`statuses.${item.status}`)}
      />
    );
    rightText = t(`handled.${item.status}`, { date: formatReviewDate(getReviewHandledAt(item), language, false) });
  }

  return (
    <Card
      mode="outlined"
      style={[styles.card, urgent && styles.cardUrgent]}
      onPress={() => onOpen(item)}
      accessibilityLabel={t("list.openRequest", { name: person.primary })}
    >
      <Card.Content style={styles.cardContent}>
        <View style={styles.topRow}>
          <View style={styles.avatar}>
            <Text style={styles.avatarText}>{getReviewAvatarInitial(person.primary)}</Text>
          </View>
          <View style={styles.who}>
            <Text style={styles.name} numberOfLines={1} selectable>
              {person.primary}
              {person.secondary ? <Text style={styles.username}>{`  ${person.secondary}`}</Text> : null}
            </Text>
            <View style={styles.storeRow}>
              <Text style={styles.storeText} numberOfLines={1} selectable>
                {stores.label || t("list.storeUnavailable")}
              </Text>
              {stores.hasMore ? (
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={t("list.moreStoresAccessibility", { count: stores.total })}
                  hitSlop={{ top: 10, bottom: 10, left: 8, right: 8 }}
                  onPress={() => onOpenStores(item)}
                >
                  <Text style={styles.moreStores}>
                    {t("list.moreStores", { count: stores.total, hidden: stores.total - 1 })} ›
                  </Text>
                </Pressable>
              ) : null}
            </View>
          </View>
          <View style={styles.right}>
            {rightChip}
            <Text style={styles.rightText} numberOfLines={1}>{rightText}</Text>
          </View>
        </View>
        <View style={styles.bottomRow}>
          <View style={styles.groups}>
            {groups.map(({ group, count }) => {
              const hot = group === "birthday" && birthdayChanged && isPending;
              const label = hot ? t("groups.birthdayAffectsAge") : t(`groups.${group}`);
              return (
                <View key={group} style={[styles.groupChip, hot && styles.groupChipHot]}>
                  <Icon source={GROUP_ICONS[group]} size={15} color={hot ? HB_COLORS.warning : HB_COLORS.textSecondary} />
                  <Text style={[styles.groupText, hot && styles.groupTextHot]}>{label}</Text>
                  {group !== "birthday" ? <Text style={styles.groupCount}>{count}</Text> : null}
                </View>
              );
            })}
          </View>
          <Button
            compact
            mode="text"
            contentStyle={styles.touchTarget}
            labelStyle={styles.goLabel}
            onPress={() => onOpen(item)}
          >
            {`${t(isPending ? "actions.review" : "actions.view")} ›`}
          </Button>
        </View>
      </Card.Content>
    </Card>
  );
}

export const ReviewRequestCard = memo(ReviewRequestCardBase);

// ---------------------------------------------------------------- 分店清单弹层

interface ReviewStoreSheetProps {
  target: { name: string; storeNames: string[] } | null;
  onDismiss: () => void;
  t: Translate;
}

export function ReviewStoreSheet({ target, onDismiss, t }: ReviewStoreSheetProps) {
  return (
    <BusinessSheet
      visible={Boolean(target)}
      title={t("list.storeSheetTitle", { name: target?.name ?? "" })}
      subtitle={t("list.storeSheetSubtitle", { count: target?.storeNames.length ?? 0 })}
      onDismiss={onDismiss}
    >
      {(target?.storeNames ?? []).map((name, index) => (
        <View key={`${name}-${index}`} style={styles.storeSheetRow}>
          <Text style={styles.storeSheetText} selectable>{name}</Text>
        </View>
      ))}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  segment: {
    flexDirection: "row",
    height: 44,
    padding: 3,
    borderRadius: 10,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  segmentItem: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
    borderRadius: 8,
    minHeight: 44 - 6,
  },
  segmentItemOn: {
    backgroundColor: HB_COLORS.white,
    shadowColor: "#101828",
    shadowOpacity: 0.12,
    shadowRadius: 3,
    shadowOffset: { width: 0, height: 1 },
    elevation: 1,
  },
  segmentLabel: { fontSize: 15, fontWeight: "600", color: HB_COLORS.textSecondary },
  segmentLabelOn: { color: HB_COLORS.textPrimary },
  countPill: {
    minWidth: 22,
    height: 20,
    paddingHorizontal: 6,
    borderRadius: 10,
    backgroundColor: HB_COLORS.brand,
    alignItems: "center",
    justifyContent: "center",
  },
  countText: { fontSize: 12, fontWeight: "700", color: HB_COLORS.white, fontVariant: ["tabular-nums"] },

  card: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, overflow: "hidden" },
  cardUrgent: { borderLeftWidth: 4, borderLeftColor: HB_COLORS.danger },
  cardContent: { paddingVertical: HB_SPACING.sm, paddingHorizontal: HB_SPACING.sm, gap: 2 },
  topRow: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.sm },
  avatar: {
    width: 40,
    height: 40,
    borderRadius: 20,
    backgroundColor: "#EAF2FF",
    alignItems: "center",
    justifyContent: "center",
  },
  avatarText: { fontSize: 16, fontWeight: "700", color: HB_COLORS.action },
  who: { flex: 1, minWidth: 0, gap: 2 },
  name: { fontSize: 17, lineHeight: 24, fontWeight: "700", color: HB_COLORS.textPrimary },
  username: { fontSize: 13, fontWeight: "400", color: HB_COLORS.textSecondary },
  storeRow: { flexDirection: "row", alignItems: "center", gap: 4 },
  storeText: { flexShrink: 1, fontSize: 13.5, lineHeight: 20, color: HB_COLORS.textSecondary },
  moreStores: { fontSize: 13.5, lineHeight: 20, fontWeight: "600", color: HB_COLORS.action },
  right: { alignItems: "flex-end", gap: 4, maxWidth: 140 },
  rightText: { fontSize: 12, lineHeight: 16, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  toneChip: {
    height: 26,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingHorizontal: 9,
    borderRadius: 13,
    borderWidth: 1,
  },
  toneChipText: { fontSize: 12.5, fontWeight: "700" },
  bottomRow: { flexDirection: "row", alignItems: "center", gap: 6, marginLeft: 52 },
  groups: { flex: 1, flexDirection: "row", flexWrap: "wrap", gap: 6, paddingVertical: 4 },
  groupChip: {
    height: 28,
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    paddingHorizontal: 10,
    borderRadius: 8,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  groupChipHot: { backgroundColor: "#FFFAEB" },
  groupText: { fontSize: 13.5, fontWeight: "600", color: HB_COLORS.textPrimary },
  groupTextHot: { color: HB_COLORS.warning },
  groupCount: { fontSize: 13.5, fontWeight: "600", color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  goLabel: { fontSize: 15, fontWeight: "700" },
  touchTarget: { minHeight: 44 },

  storeSheetRow: {
    minHeight: 44,
    justifyContent: "center",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  storeSheetText: { fontSize: 15, color: HB_COLORS.textPrimary },
});
