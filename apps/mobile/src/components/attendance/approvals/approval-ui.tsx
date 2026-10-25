import type { ReactNode } from "react";
import { Modal, ScrollView, StyleSheet, View } from "react-native";
import { IconButton, Text } from "react-native-paper";
import { SafeAreaProvider, SafeAreaView } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ATTENDANCE_STATUS_TONES, StatusPill, type AttendanceStatusTone } from "../AdjustmentFormControls";

/** 本人审核用的紫色（设计图「本人审核」标签与按钮）。 */
export const SELF_REVIEW_TONE = { background: "#F4F3FF", text: "#5925DC", border: "#BDB4FE" } as const;
/** 加班 / 周末时薪的橙色。 */
export const OVERTIME_TONE = { background: "#FFFAEB", text: "#B54708", fill: "#F79009" } as const;
/** 时间轴：排班灰、实际蓝、自动下班红。 */
export const TIMELINE_COLORS = {
  track: HB_COLORS.surfaceMuted,
  schedule: "#98A2B3",
  actual: "#1570EF",
  auto: "#F04438",
} as const;

const AVATAR_PALETTE = ["#E8590C", "#0E9384", "#6938EF", "#1570EF", "#DD2590", "#CA8504", "#475467"];

const CJK_PATTERN = /[㐀-鿿豈-﫿]/;

/** 头像首字母：去掉「[样例]」之类的方括号前缀；中文取前两个字，英文取首尾单词首字母。 */
export function getAvatarInitials(name?: string) {
  const trimmed = name?.replace(/^\s*\[[^\]]*\]\s*/, "").trim();
  if (!trimmed) return "?";
  if (CJK_PATTERN.test(trimmed)) {
    return Array.from(trimmed.replace(/\s+/g, "")).slice(0, 2).join("");
  }
  const words = trimmed.split(/\s+/).filter(Boolean);
  const first = Array.from(words[0] ?? "")[0] ?? "";
  const last = words.length > 1 ? Array.from(words[words.length - 1])[0] ?? "" : "";
  return `${first}${last}`.toUpperCase() || "?";
}

/** 同一个人在各处头像颜色固定（按姓名散列）。 */
export function avatarColor(name?: string) {
  let hash = 0;
  for (const char of name ?? "") hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  return AVATAR_PALETTE[hash % AVATAR_PALETTE.length];
}

export function PersonAvatar({ name, size = 40 }: { name?: string; size?: number }) {
  return (
    <View style={[styles.avatar, { backgroundColor: avatarColor(name), borderRadius: size / 2, height: size, width: size }]}>
      <Text style={[styles.avatarText, { fontSize: size * 0.36 }]} numberOfLines={1}>{getAvatarInitials(name)}</Text>
    </View>
  );
}

/** 小号类型标签：浅底 + 同色字，比 StatusPill 更紧凑，放在姓名后面。 */
export function TypeTag({ label, tone, custom }: {
  label: string;
  tone?: AttendanceStatusTone;
  custom?: { background: string; text: string };
}) {
  const colors = custom ?? ATTENDANCE_STATUS_TONES[tone ?? "neutral"];
  return (
    <View style={[styles.tag, { backgroundColor: colors.background }]}>
      <Text variant="labelSmall" style={[styles.tagText, { color: colors.text }]}>{label}</Text>
    </View>
  );
}

/**
 * 斜纹 / 竖虚线填充：RN 没有 CSS 渐变斜纹，用一排旋转的细条在裁剪容器里拼出来。
 * 斜纹用于「超出排班的加班」，竖虚线用于「自动下班补出的、不计薪的时段」。
 */
export function PatternFill({ color, variant }: { color: string; variant: "diagonal" | "dashed" }) {
  const count = 60;
  return (
    <View style={styles.patternClip} pointerEvents="none">
      <View style={styles.patternRow}>
        {Array.from({ length: count }, (_, index) => (
          <View
            key={index}
            style={variant === "diagonal"
              ? [styles.diagonalStripe, { backgroundColor: color }]
              : [styles.dashStripe, { backgroundColor: color }]}
          />
        ))}
      </View>
    </View>
  );
}

/** 详情页白卡片：标题行（左标题、右灰色说明）+ 内容。 */
export function DetailCard({ title, aside, children, tone }: {
  title?: string;
  aside?: ReactNode;
  children: ReactNode;
  tone?: "danger" | "dashed";
}) {
  return (
    <View style={[
      styles.detailCard,
      tone === "danger" ? styles.detailCardDanger : null,
      tone === "dashed" ? styles.detailCardDashed : null,
    ]}>
      {title || aside ? (
        <View style={styles.detailCardHeader}>
          {title ? <Text variant="titleSmall" style={styles.detailCardTitle}>{title}</Text> : <View />}
          {typeof aside === "string" ? <Text variant="bodySmall" style={styles.muted}>{aside}</Text> : aside}
        </View>
      ) : null}
      {children}
    </View>
  );
}

/**
 * 审核详情的整页外壳：返回 + 标题 + 状态标签，正文滚动，底部固定操作栏。
 * 用全屏 Modal 而不是新路由，审批数据与提交回调都留在列表页里，返回后列表状态不丢。
 */
export function ApprovalDetailFrame({
  visible,
  title,
  statusLabel,
  statusTone = "warning",
  onClose,
  footer,
  children,
}: {
  visible: boolean;
  title: string;
  statusLabel?: string;
  statusTone?: AttendanceStatusTone;
  onClose: () => void;
  footer?: ReactNode;
  children: ReactNode;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <Modal visible={visible} animationType="slide" presentationStyle="fullScreen" onRequestClose={onClose}>
      {/* Modal 是独立的原生根视图，拿不到外层 Provider 的安全区，需要在里面再包一层。 */}
      <SafeAreaProvider>
        <SafeAreaView style={styles.frame} edges={["top", "bottom", "left", "right"]}>
          <View style={styles.frameHeader}>
            <IconButton icon="chevron-left" size={26} accessibilityLabel={t("common:actions.back")} onPress={onClose} />
            <Text variant="titleLarge" style={styles.frameTitle} numberOfLines={1}>{title}</Text>
            {statusLabel ? <StatusPill label={statusLabel} tone={statusTone} /> : null}
          </View>
          <ScrollView style={styles.frameScroll} contentContainerStyle={styles.frameContent} keyboardShouldPersistTaps="handled">
            {children}
          </ScrollView>
          {footer ? <View style={styles.frameFooter}>{footer}</View> : null}
        </SafeAreaView>
      </SafeAreaProvider>
    </Modal>
  );
}

const styles = StyleSheet.create({
  avatar: { alignItems: "center", justifyContent: "center" },
  avatarText: { color: HB_COLORS.white, fontWeight: "600" },
  dashStripe: { height: "100%", marginRight: 3, width: 3 },
  detailCard: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.sm,
    padding: HB_SPACING.md,
  },
  detailCardDanger: {
    backgroundColor: ATTENDANCE_STATUS_TONES.danger.background,
    borderColor: "#FDA29B",
  },
  detailCardDashed: { borderStyle: "dashed", borderWidth: 1 },
  detailCardHeader: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  detailCardTitle: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  diagonalStripe: { height: 110, marginRight: 5, marginTop: -25, transform: [{ rotate: "35deg" }], width: 3 },
  // 外层白底让状态栏区域与页头连成一片，正文滚动区仍是浅灰底。
  frame: { backgroundColor: HB_COLORS.white, flex: 1 },
  frameContent: { gap: HB_SPACING.sm, padding: HB_SPACING.md, paddingBottom: HB_SPACING.xl },
  frameFooter: {
    backgroundColor: HB_COLORS.white,
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  frameScroll: { backgroundColor: HB_COLORS.background, flex: 1 },
  frameHeader: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.xxs,
    paddingRight: HB_SPACING.md,
    paddingVertical: HB_SPACING.xxs,
  },
  frameTitle: { color: HB_COLORS.textPrimary, flex: 1, fontWeight: "700" },
  muted: { color: HB_COLORS.textSecondary },
  patternClip: { ...StyleSheet.absoluteFillObject, overflow: "hidden" },
  patternRow: { flexDirection: "row", height: "100%" },
  tag: { borderRadius: 6, paddingHorizontal: 6, paddingVertical: 1 },
  tagText: { fontWeight: "600" },
});
