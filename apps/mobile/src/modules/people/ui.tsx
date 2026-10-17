import type { ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, View, type StyleProp, type ViewStyle } from "react-native";
import { Icon, Text } from "react-native-paper";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 「人员管理」统一 UI 原语（设计稿 A1–A6 的共用部件）。
 * 颜色只用 tokens + 少量设计稿里的浅底色，别在页面里再手写十六进制。
 */

export type PeopleTone = "gray" | "green" | "blue" | "amber" | "red" | "violet";

/** 标签 / 提示条的前景色与浅底色。 */
export const PEOPLE_TONES: Record<PeopleTone, { fg: string; bg: string }> = {
  gray: { fg: HB_COLORS.textSecondary, bg: HB_COLORS.surfaceMuted },
  green: { fg: HB_COLORS.success, bg: "#ECFDF3" },
  blue: { fg: HB_COLORS.action, bg: "#EFF6FF" },
  amber: { fg: HB_COLORS.warning, bg: "#FFFAEB" },
  red: { fg: HB_COLORS.danger, bg: "#FEF3F2" },
  violet: { fg: "#6941C6", bg: "#F4F3FF" },
};

const AVATAR_TONES = [
  { fg: HB_COLORS.action, bg: "#DBEAFE" },
  { fg: "#C11574", bg: "#FCE7F6" },
  { fg: HB_COLORS.success, bg: "#DCFAE6" },
  { fg: HB_COLORS.warning, bg: "#FEF0C7" },
  { fg: "#5925DC", bg: "#EBE9FE" },
] as const;

/** 同一个人始终得到同一种头像底色（按 key 取模），列表里不会因排序变色。 */
export function pickAvatarTone(key: string) {
  let hash = 0;
  for (const char of key) {
    hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  }
  return AVATAR_TONES[hash % AVATAR_TONES.length];
}

/** 小标签：启用中 / 待首次登录 / 由管理分店派生 等。 */
export function PeopleTag({ label, tone = "gray", icon }: { label: string; tone?: PeopleTone; icon?: string }) {
  const color = PEOPLE_TONES[tone];
  return (
    <View style={[styles.tag, { backgroundColor: color.bg }]}>
      {icon ? <Icon source={icon} size={13} color={color.fg} /> : null}
      <Text style={[styles.tagText, { color: color.fg }]} numberOfLines={1}>{label}</Text>
    </View>
  );
}

/** 圆形头像；停用账号置灰，`presence` 控制右下角状态点。 */
export function PersonAvatar({
  initials,
  seed,
  size = 40,
  disabled = false,
  presence,
}: {
  initials: string;
  seed: string;
  size?: number;
  disabled?: boolean;
  /** "on" 绿点（启用）/ "off" 灰点（停用）/ 不传不显示。 */
  presence?: "on" | "off";
}) {
  const tone = disabled ? { fg: "#98A2B3", bg: "#EAECF0" } : pickAvatarTone(seed);
  return (
    <View style={[styles.avatar, { width: size, height: size, borderRadius: size / 2, backgroundColor: tone.bg }]}>
      <Text style={[styles.avatarText, { color: tone.fg, fontSize: Math.round(size * 0.38) }]}>{initials}</Text>
      {presence ? (
        <View style={[styles.presence, { backgroundColor: presence === "on" ? "#17B26A" : "#98A2B3" }]} />
      ) : null}
    </View>
  );
}

/** 分段切换（人员 / 角色 / 权限、分店 / 角色 / 直接权限）。 */
export function SegmentTabs<T extends string>({
  items,
  value,
  onChange,
}: {
  items: readonly { key: T; label: string }[];
  value: T;
  onChange: (key: T) => void;
}) {
  return (
    <View style={styles.segment} accessibilityRole="tablist">
      {items.map((item) => {
        const selected = item.key === value;
        return (
          <Pressable
            key={item.key}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(item.key)}
            style={[styles.segmentItem, selected && styles.segmentItemSelected]}
          >
            <Text style={[styles.segmentText, selected && styles.segmentTextSelected]} numberOfLines={1}>{item.label}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/** 横向筛选芯片，可带计数；放不下时横向滚动，不换行。 */
export function FilterChipRow<T extends string>({
  items,
  value,
  onChange,
}: {
  items: readonly { key: T; label: string; count?: number }[];
  value: T;
  onChange: (key: T) => void;
}) {
  return (
    <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
      {items.map((item) => {
        const selected = item.key === value;
        return (
          <Pressable
            key={item.key}
            accessibilityRole="button"
            accessibilityState={{ selected }}
            onPress={() => onChange(item.key)}
            style={[styles.chip, selected && styles.chipSelected]}
          >
            <Text style={[styles.chipText, selected && styles.chipTextSelected]}>{item.label}</Text>
            {item.count !== undefined ? (
              <Text style={[styles.chipCount, selected && styles.chipTextSelected]}>{item.count}</Text>
            ) : null}
          </Pressable>
        );
      })}
    </ScrollView>
  );
}

export function SectionTitle({
  title,
  actionLabel,
  onAction,
}: {
  title: string;
  actionLabel?: string;
  onAction?: () => void;
}) {
  return (
    <View style={styles.sectionTitleRow}>
      <Text style={styles.sectionTitle}>{title}</Text>
      {actionLabel && onAction ? (
        <Pressable accessibilityRole="button" onPress={onAction} hitSlop={8}>
          <Text style={styles.sectionAction}>{actionLabel}</Text>
        </Pressable>
      ) : null}
    </View>
  );
}

/** 白底圆角卡片，内部行之间自动画分隔线（用 `InfoCard` + `KvRow` / `ActionRow` / `PersonRow`）。 */
export function InfoCard({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  return <View style={[styles.card, style]}>{children}</View>;
}

/** 卡片里的「标签 — 值」行，传 `onPress` 时右侧出现箭头。 */
export function KvRow({
  label,
  value,
  onPress,
  trailing,
  mono = false,
  first = false,
}: {
  label: string;
  value: ReactNode;
  onPress?: () => void;
  trailing?: ReactNode;
  mono?: boolean;
  /** 卡片内第一行不画上分隔线。 */
  first?: boolean;
}) {
  const content = (
    <View style={[styles.kvRow, !first && styles.rowDivider]}>
      <Text style={styles.kvLabel}>{label}</Text>
      <View style={styles.kvValueWrap}>
        {typeof value === "string" ? (
          <Text style={[styles.kvValue, mono && styles.mono]} numberOfLines={1}>{value}</Text>
        ) : (
          value
        )}
      </View>
      {trailing}
      {onPress ? <Icon source="chevron-right" size={20} color="#98A2B3" /> : null}
    </View>
  );
  return onPress ? (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress}>{content}</Pressable>
  ) : content;
}

/** 带图标、标题、副标题的操作行（更多操作弹层、导出入口等）。 */
export function ActionRow({
  icon,
  tone = "gray",
  title,
  subtitle,
  right,
  onPress,
  disabled = false,
  first = false,
  chevron = true,
}: {
  icon: string;
  tone?: PeopleTone;
  title: string;
  subtitle?: string;
  right?: ReactNode;
  onPress?: () => void;
  disabled?: boolean;
  first?: boolean;
  chevron?: boolean;
}) {
  const color = PEOPLE_TONES[tone];
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled || !onPress}
      onPress={onPress}
      style={[styles.actionRow, !first && styles.rowDivider, disabled && styles.disabled]}
    >
      <View style={[styles.actionIcon, { backgroundColor: color.bg }]}>
        <Icon source={icon} size={19} color={color.fg} />
      </View>
      <View style={styles.actionText}>
        <Text style={styles.actionTitle} numberOfLines={1}>{title}</Text>
        {subtitle ? <Text style={styles.actionSubtitle} numberOfLines={1}>{subtitle}</Text> : null}
      </View>
      {right}
      {chevron && onPress ? <Icon source="chevron-right" size={20} color="#98A2B3" /> : null}
    </Pressable>
  );
}

/** 列表里的一个人：头像 + 名字（带昵称小字）+ 一行元信息 + 例外标签 + 右侧操作。 */
export function PersonRow({
  avatar,
  name,
  nickname,
  nameBadges,
  meta,
  tags,
  right,
  onPress,
  first = false,
  accessibilityLabel,
  testID,
}: {
  avatar: ReactNode;
  name: string;
  nickname?: string | null;
  /** 名字后面的徽标：你 / 管理员 / 店长。 */
  nameBadges?: ReactNode;
  meta: string;
  /** 只放「例外」标签（待首次登录、不能登录、无邮箱、已停用），正常员工不放。 */
  tags?: ReactNode;
  right?: ReactNode;
  onPress?: () => void;
  first?: boolean;
  accessibilityLabel?: string;
  testID?: string;
}) {
  const body = (
    <View style={[styles.personRow, !first && styles.rowDivider]} testID={testID}>
      {avatar}
      <View style={styles.personMain}>
        <View style={styles.personNameRow}>
          <Text style={styles.personName} numberOfLines={1}>{name}</Text>
          {nickname ? <Text style={styles.personNickname} numberOfLines={1}>{`「${nickname}」`}</Text> : null}
          {nameBadges}
        </View>
        <Text style={styles.personMeta} numberOfLines={1}>{meta}</Text>
        {tags ? <View style={styles.personTags}>{tags}</View> : null}
      </View>
      {right ? <View style={styles.personRight}>{right}</View> : null}
    </View>
  );
  return onPress ? (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel ?? name} onPress={onPress}>{body}</Pressable>
  ) : body;
}

/** 说明 / 警示条。 */
export function NoticeBox({ tone = "blue", icon = "information-outline", children }: { tone?: PeopleTone; icon?: string; children: ReactNode }) {
  const color = PEOPLE_TONES[tone];
  return (
    <View style={[styles.notice, { backgroundColor: color.bg }]} accessibilityRole={tone === "red" ? "alert" : undefined}>
      <Icon source={icon} size={17} color={color.fg} />
      <Text style={[styles.noticeText, { color: color.fg }]}>{children}</Text>
    </View>
  );
}

/** 页面底部吸附操作栏：左边写「改了什么」，右边放保存等按钮。 */
export function StickyActionBar({ summary, hint, children }: { summary?: string; hint?: string; children: ReactNode }) {
  return (
    <View style={styles.stickyBar}>
      {summary || hint ? (
        <View style={styles.stickySummary}>
          {summary ? <Text style={styles.stickySummaryText} numberOfLines={1}>{summary}</Text> : null}
          {hint ? <Text style={styles.stickyHint} numberOfLines={1}>{hint}</Text> : null}
        </View>
      ) : null}
      <View style={styles.stickyActions}>{children}</View>
    </View>
  );
}

/** 右下角悬浮主按钮（新增员工 / 新增账号 / 新增角色）。 */
export function FloatingAction({
  icon = "plus",
  label,
  onPress,
  disabled = false,
}: {
  icon?: string;
  label: string;
  onPress: () => void;
  disabled?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.fab, disabled && styles.fabDisabled]}
    >
      <Icon source={icon} size={22} color={HB_COLORS.white} />
      <Text style={styles.fabText}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  tag: { alignItems: "center", alignSelf: "flex-start", borderRadius: 6, flexDirection: "row", gap: 3, height: 20, paddingHorizontal: 7 },
  tagText: { fontSize: 11.5, fontWeight: "700", lineHeight: 16 },
  avatar: { alignItems: "center", justifyContent: "center" },
  avatarText: { fontWeight: "800" },
  presence: { borderColor: HB_COLORS.white, borderRadius: 6, borderWidth: 2, bottom: -1, height: 12, position: "absolute", right: -1, width: 12 },
  segment: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: 10, flexDirection: "row", gap: 3, padding: 3 },
  segmentItem: { alignItems: "center", borderRadius: HB_RADIUS.control, flex: 1, justifyContent: "center", minHeight: 36, paddingHorizontal: 4 },
  segmentItemSelected: { backgroundColor: HB_COLORS.white, elevation: 1, shadowColor: "#101828", shadowOffset: { height: 1, width: 0 }, shadowOpacity: 0.1, shadowRadius: 2 },
  segmentText: { color: HB_COLORS.textSecondary, fontSize: 13.5, fontWeight: "600" },
  segmentTextSelected: { color: HB_COLORS.textPrimary, fontWeight: "800" },
  chipRow: { alignItems: "center", gap: 6, paddingVertical: 2 },
  chip: { alignItems: "center", backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outlineMuted, borderRadius: 999, borderWidth: 1, flexDirection: "row", gap: 4, minHeight: 32, paddingHorizontal: 11 },
  chipSelected: { backgroundColor: "#EFF6FF", borderColor: "#B2CCFF" },
  chipText: { color: HB_COLORS.textPrimary, fontSize: 12.5 },
  chipTextSelected: { color: HB_COLORS.action, fontWeight: "700" },
  chipCount: { color: "#667085", fontSize: 11, fontWeight: "600" },
  sectionTitleRow: { alignItems: "center", flexDirection: "row", justifyContent: "space-between", paddingHorizontal: 4, paddingTop: 4 },
  sectionTitle: { color: "#667085", fontSize: 12.5, fontWeight: "700" },
  sectionAction: { color: HB_COLORS.action, fontSize: 12.5, fontWeight: "700" },
  card: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, borderWidth: 1, overflow: "hidden" },
  rowDivider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  kvRow: { alignItems: "center", flexDirection: "row", gap: 10, minHeight: 44, paddingHorizontal: 14 },
  kvLabel: { color: HB_COLORS.textSecondary, flexShrink: 0, fontSize: 13.5, width: 84 },
  kvValueWrap: { alignItems: "center", flex: 1, flexDirection: "row", flexWrap: "wrap", gap: 6, minWidth: 0 },
  kvValue: { color: HB_COLORS.textPrimary, fontSize: 14, flexShrink: 1 },
  mono: { fontFamily: "Menlo", fontVariant: ["tabular-nums"] },
  actionRow: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 52, paddingHorizontal: 14, paddingVertical: 8 },
  actionIcon: { alignItems: "center", borderRadius: 10, height: 34, justifyContent: "center", width: 34 },
  actionText: { flex: 1, minWidth: 0 },
  actionTitle: { color: HB_COLORS.textPrimary, fontSize: 14.5, fontWeight: "700" },
  actionSubtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 1 },
  disabled: { opacity: 0.45 },
  personRow: { alignItems: "center", flexDirection: "row", gap: 12, paddingHorizontal: 14, paddingVertical: 11 },
  personMain: { flex: 1, minWidth: 0 },
  personNameRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  personName: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 15, fontWeight: "700" },
  personNickname: { color: "#667085", fontSize: 12.5, fontWeight: "600" },
  personMeta: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 2 },
  personTags: { flexDirection: "row", flexWrap: "wrap", gap: 4, marginTop: 5 },
  personRight: { alignItems: "center", flexDirection: "row", flexShrink: 0 },
  notice: { alignItems: "flex-start", borderRadius: 10, flexDirection: "row", gap: 8, paddingHorizontal: 12, paddingVertical: 10 },
  noticeText: { flex: 1, fontSize: 12.5, lineHeight: 18 },
  stickyBar: { alignItems: "center", backgroundColor: HB_COLORS.white, borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, flexDirection: "row", gap: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md, paddingVertical: HB_SPACING.sm },
  stickySummary: { flex: 1, minWidth: 0 },
  stickySummaryText: { color: HB_COLORS.textPrimary, fontSize: 12.5, fontWeight: "800" },
  stickyHint: { color: HB_COLORS.textSecondary, fontSize: 12, marginTop: 1 },
  stickyActions: { alignItems: "center", flexDirection: "row", flexGrow: 1, flexShrink: 0, gap: HB_SPACING.xs, justifyContent: "flex-end" },
  fab: { alignItems: "center", backgroundColor: HB_COLORS.brand, borderRadius: HB_RADIUS.sheet, bottom: HB_SPACING.lg, elevation: 4, flexDirection: "row", gap: 6, height: 50, paddingHorizontal: 18, position: "absolute", right: HB_SPACING.md, shadowColor: HB_COLORS.brand, shadowOffset: { height: 8, width: 0 }, shadowOpacity: 0.35, shadowRadius: 18 },
  fabDisabled: { backgroundColor: "#98A2B3", shadowOpacity: 0 },
  fabText: { color: HB_COLORS.white, fontSize: 15, fontWeight: "800" },
});
