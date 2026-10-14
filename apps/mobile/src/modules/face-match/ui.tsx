// 人脸比对页面共用的小组件：页面框架、提示条、状态标签、筛选 chip、分区标题、门店单选弹层。
// 视觉沿用现有业务页（HB_COLORS / HB_SPACING / HB_RADIUS + react-native-paper），不引入新的设计风格。
import type { ReactNode } from "react";
import { Pressable, StyleSheet, View, type StyleProp, type TextStyle, type ViewStyle } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, IconButton, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type { StatusTone } from "./entries-logic";

export const FACE_SOFT = {
  accent: "#EAF2FF",
  success: "#ECFDF3",
  warning: "#FFFAEB",
  danger: "#FEF3F2",
} as const;

/** 黑名单命中的强调色（设计稿 badgeSolid / hitBorder）：实心红标签与角标、未读红条、命中卡片描边。 */
export const FACE_HIT = {
  solid: "#D92D20",
  border: "#FDA29B",
} as const;

export type FaceTone = StatusTone | "info";

const TONES: Record<FaceTone, { background: string; foreground: string }> = {
  neutral: { background: HB_COLORS.surfaceMuted, foreground: HB_COLORS.textSecondary },
  info: { background: FACE_SOFT.accent, foreground: HB_COLORS.action },
  success: { background: FACE_SOFT.success, foreground: HB_COLORS.success },
  warning: { background: FACE_SOFT.warning, foreground: HB_COLORS.warning },
  danger: { background: FACE_SOFT.danger, foreground: HB_COLORS.danger },
};

export const MONO: TextStyle = { fontVariant: ["tabular-nums"] };

type IconName = keyof typeof MaterialCommunityIcons.glyphMap;

/** 页面框架：顶部返回 + 标题，内容区自行滚动。 */
export function FaceScreenFrame({
  title,
  onBack,
  children,
  right,
}: {
  title: string;
  onBack: () => void;
  children: ReactNode;
  right?: ReactNode;
}) {
  const { t } = useAppTranslation("common");
  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={t("actions.back")} onPress={onBack} />
        <Text variant="titleMedium" style={styles.headerTitle} numberOfLines={1}>
          {title}
        </Text>
        {right ?? <View style={styles.headerSpacer} />}
      </View>
      {children}
    </SafeAreaView>
  );
}

/** 只显示一句说明（无权限 / 审核模式）加返回按钮的整页提示。 */
export function FaceScreenMessage({ message, backLabel, onBack }: { message: string; backLabel: string; onBack: () => void }) {
  return (
    <SafeAreaView style={styles.message}>
      <Text accessibilityLiveRegion="polite">{message}</Text>
      <Button onPress={onBack}>{backLabel}</Button>
    </SafeAreaView>
  );
}

/** 提示条：图标 + 文案，颜色之外还有图标，不只靠颜色传达状态。 */
export function Notice({
  tone = "info",
  text,
  icon,
  action,
  trailing,
  style,
}: {
  tone?: FaceTone;
  text: string;
  icon?: IconName;
  action?: { label: string; onPress: () => void };
  /** 提示条最右侧的附加控件（例如抓拍页的「眼睛」按钮）。 */
  trailing?: ReactNode;
  style?: StyleProp<ViewStyle>;
}) {
  const palette = TONES[tone];
  const glyph: IconName = icon ?? (tone === "danger" ? "alert-circle-outline" : tone === "warning" ? "alert-outline" : tone === "success" ? "check-circle-outline" : "information-outline");
  return (
    <View style={[styles.notice, { backgroundColor: palette.background }, style]} accessibilityRole="alert">
      <MaterialCommunityIcons name={glyph} size={18} color={palette.foreground} style={styles.noticeIcon} />
      <Text style={[styles.noticeText, { color: palette.foreground }]}>{text}</Text>
      {action ? (
        <Pressable accessibilityRole="button" onPress={action.onPress} hitSlop={8}>
          <Text style={[styles.noticeAction, { color: palette.foreground }]}>{action.label}</Text>
        </Pressable>
      ) : null}
      {trailing}
    </View>
  );
}

/** 状态标签。solid：实心强调（仅 danger 有实心样式：红底白字，用于「黑名单命中」）。 */
export function Tag({ label, tone = "neutral", icon, solid, style }: { label: string; tone?: FaceTone; icon?: IconName; solid?: boolean; style?: StyleProp<ViewStyle> }) {
  const palette = solid && tone === "danger" ? { background: FACE_HIT.solid, foreground: HB_COLORS.white } : TONES[tone];
  return (
    <View style={[styles.tag, { backgroundColor: palette.background }, style]}>
      {icon ? <MaterialCommunityIcons name={icon} size={12} color={palette.foreground} /> : null}
      <Text style={[styles.tagText, { color: palette.foreground }]} numberOfLines={1}>
        {label}
      </Text>
    </View>
  );
}

/**
 * 筛选 chip：选中态用品牌色，触控高度不小于 36（compact 的二级筛选 32，外加 hitSlop 补足触控区）。
 * tone="danger"：红色描边（黑名单）；选中为淡红底、红边、粗体。badge：右侧红色实心数字（null / 空不显示）。
 */
export function FilterChip({
  label,
  selected,
  onPress,
  icon,
  disabled,
  tone = "default",
  badge,
  compact,
  accessibilityLabel,
}: {
  label: string;
  selected?: boolean;
  onPress: () => void;
  icon?: IconName;
  disabled?: boolean;
  tone?: "default" | "danger";
  badge?: string | null;
  compact?: boolean;
  accessibilityLabel?: string;
}) {
  const danger = tone === "danger";
  const iconColor = danger ? HB_COLORS.danger : selected ? HB_COLORS.action : HB_COLORS.textSecondary;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      accessibilityState={{ selected: Boolean(selected), disabled: Boolean(disabled) }}
      disabled={disabled}
      hitSlop={compact ? { top: 2, bottom: 2 } : undefined}
      onPress={onPress}
      style={[
        styles.chip,
        compact ? styles.chipCompact : null,
        danger ? styles.chipDanger : null,
        selected ? (danger ? styles.chipDangerOn : styles.chipOn) : null,
        disabled ? styles.disabled : null,
      ]}
    >
      {icon ? <MaterialCommunityIcons name={icon} size={compact ? 14 : 16} color={iconColor} /> : null}
      <Text
        style={[
          styles.chipText,
          compact ? styles.chipTextCompact : null,
          danger ? styles.chipTextDanger : null,
          selected ? (danger ? styles.chipTextDangerOn : styles.chipTextOn) : null,
        ]}
        numberOfLines={1}
      >
        {label}
      </Text>
      {badge ? (
        <View style={styles.chipBadge}>
          <Text style={styles.chipBadgeText}>{badge}</Text>
        </View>
      ) : null}
    </Pressable>
  );
}

/** 白底卡片。 */
export function FaceCard({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  return <View style={[styles.card, style]}>{children}</View>;
}

/** flush：标题所在容器已经有水平内边距（表单页）时去掉自带的内边距，避免和下面的内容错位。 */
export function SectionTitle({ title, hint, flush }: { title: string; hint?: string; flush?: boolean }) {
  return (
    <View style={[styles.sectionTitle, flush ? styles.sectionTitleFlush : null]}>
      <Text style={styles.sectionTitleText}>{title}</Text>
      {hint ? <Text style={styles.sectionHint}>{hint}</Text> : null}
    </View>
  );
}

/** 分段页签（名单条目 / 命中复核）。 */
export function SegmentTabs<T extends string>({
  value,
  options,
  onChange,
}: {
  value: T;
  options: readonly { key: T; label: string }[];
  onChange: (key: T) => void;
}) {
  return (
    <View style={styles.segmentTrack} accessibilityRole="tablist">
      {options.map((option) => {
        const active = option.key === value;
        return (
          <Pressable
            key={option.key}
            accessibilityRole="tab"
            accessibilityState={{ selected: active }}
            onPress={() => (active ? undefined : onChange(option.key))}
            style={[styles.segment, active ? styles.segmentOn : null]}
          >
            <Text style={[styles.segmentLabel, active ? styles.segmentLabelOn : null]} numberOfLines={1}>
              {option.label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

export const faceStyles = StyleSheet.create({
  rowWrap: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  rowBetween: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  muted: { fontSize: 12, color: HB_COLORS.textSecondary },
  strong: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  state: { paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center", gap: HB_SPACING.sm },
});

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center", paddingRight: HB_SPACING.xxs },
  headerTitle: { flex: 1, fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 48 },
  message: { flex: 1, gap: HB_SPACING.md, padding: HB_SPACING.lg, justifyContent: "center", backgroundColor: HB_COLORS.background },
  notice: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.xs, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.control },
  noticeIcon: { marginTop: 1 },
  noticeText: { flex: 1, fontSize: 13, lineHeight: 19 },
  noticeAction: { fontSize: 13, fontWeight: "700", paddingVertical: 2 },
  tag: { flexDirection: "row", alignItems: "center", gap: 4, paddingHorizontal: 8, paddingVertical: 3, borderRadius: 6, maxWidth: "100%" },
  tagText: { fontSize: 12, fontWeight: "600" },
  chip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    minHeight: 36,
    paddingHorizontal: 12,
    borderRadius: 18,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  chipOn: { borderColor: HB_COLORS.action, backgroundColor: FACE_SOFT.accent },
  chipCompact: { minHeight: 32, paddingHorizontal: 11 },
  chipDanger: { borderColor: FACE_HIT.border },
  chipDangerOn: { borderColor: HB_COLORS.danger, backgroundColor: FACE_SOFT.danger },
  chipText: { fontSize: 13, color: HB_COLORS.textPrimary, flexShrink: 1 },
  chipTextCompact: { fontSize: 13 },
  chipTextOn: { color: HB_COLORS.action, fontWeight: "700" },
  chipTextDanger: { color: HB_COLORS.danger },
  chipTextDangerOn: { color: HB_COLORS.danger, fontWeight: "700" },
  chipBadge: {
    minWidth: 18,
    height: 18,
    paddingHorizontal: 5,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: FACE_HIT.solid,
  },
  chipBadgeText: { fontSize: 11, fontWeight: "800", color: HB_COLORS.white, fontVariant: ["tabular-nums"] },
  disabled: { opacity: 0.5 },
  card: {
    marginHorizontal: HB_SPACING.md,
    marginBottom: HB_SPACING.sm,
    padding: HB_SPACING.md,
    gap: HB_SPACING.xs,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.white,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  sectionTitle: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm, paddingBottom: 6, gap: 2 },
  sectionTitleFlush: { paddingHorizontal: 0 },
  sectionTitleText: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  sectionHint: { fontSize: 12, color: HB_COLORS.textSecondary },
  segmentTrack: { flexDirection: "row", marginHorizontal: HB_SPACING.md, padding: 2, borderRadius: 10, backgroundColor: "#E4E7EB" },
  segment: { flex: 1, minHeight: 40, alignItems: "center", justifyContent: "center", borderRadius: 8, paddingHorizontal: 4 },
  segmentOn: { backgroundColor: HB_COLORS.white },
  segmentLabel: { fontSize: 14, color: "#3D4550" },
  segmentLabelOn: { fontWeight: "700", color: HB_COLORS.action },
});
