// 申请假期各页共用的外框与小部件：返回页头、白底细边卡片、底部固定主按钮、日期格式、斜纹底（表示「不算」的日子）。
import type { ReactNode } from "react";
import { useCallback } from "react";
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, View, type StyleProp, type ViewStyle } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, IconButton, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { weekdayIndex } from "@/modules/attendance/leave-rules";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export const LEAVE_TONES = {
  sick: { background: "#FEF3F2", foreground: "#B42318" },
  annual: { background: "#F4F3FF", foreground: "#5925DC" },
  cashOut: { background: "#ECFDF3", foreground: "#067647" },
  other: { background: HB_COLORS.surfaceMuted, foreground: HB_COLORS.textSecondary },
} as const;

export type LeaveTone = keyof typeof LEAVE_TONES;

const WEEKDAY_KEYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;

/** 整页外框：顶部返回页头 + 可滚动内容 + 可选底部固定按钮区（贴底并让出安全区）。 */
export function LeaveScreenFrame({
  title,
  onBack,
  children,
  footer,
  right,
}: {
  title: string;
  onBack: () => void;
  children: ReactNode;
  footer?: ReactNode;
  right?: ReactNode;
}) {
  const { t } = useAppTranslation("common");
  const insets = useSafeAreaInsets();
  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={t("actions.back")} onPress={onBack} />
        <Text variant="titleLarge" style={styles.headerTitle} numberOfLines={1} accessibilityRole="header">
          {title}
        </Text>
        {right ?? <View style={styles.headerSpacer} />}
      </View>
      {/* iOS 键盘弹起时把内容和底部按钮一起顶上去，备注框不被挡住。 */}
      <KeyboardAvoidingView style={styles.body} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView
          contentContainerStyle={[styles.content, footer ? null : { paddingBottom: HB_SPACING.lg + insets.bottom }]}
          keyboardShouldPersistTaps="handled"
          showsVerticalScrollIndicator={false}
        >
          {children}
        </ScrollView>
        {footer ? <View style={[styles.footer, { paddingBottom: HB_SPACING.sm + insets.bottom }]}>{footer}</View> : null}
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

/** 只有一句说明（无权限 / 未登录 / 不适用）加返回按钮的整页提示。 */
export function LeaveScreenMessage({ title, message, onBack }: { title: string; message: string; onBack: () => void }) {
  const { t } = useAppTranslation("common");
  return (
    <LeaveScreenFrame title={title} onBack={onBack}>
      <LeaveCard>
        <Text variant="bodyMedium" accessibilityLiveRegion="polite">{message}</Text>
        <Button mode="text" style={styles.messageBack} onPress={onBack}>{t("actions.back")}</Button>
      </LeaveCard>
    </LeaveScreenFrame>
  );
}

export function LeaveCard({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  return <View style={[styles.card, style]}>{children}</View>;
}

/** 卡片标题行：左标题、右侧灰色补充说明。 */
export function LeaveCardHeader({ title, aside }: { title: string; aside?: string }) {
  return (
    <View style={styles.cardHeader}>
      <Text variant="titleSmall" style={styles.cardTitle}>{title}</Text>
      {aside ? <Text variant="bodySmall" style={styles.cardAside} numberOfLines={2}>{aside}</Text> : null}
    </View>
  );
}

export function PrimaryBottomButton({
  label,
  disabled,
  loading,
  onPress,
}: {
  label: string;
  disabled?: boolean;
  loading?: boolean;
  onPress: () => void;
}) {
  return (
    <Button
      mode="contained"
      disabled={disabled || loading}
      loading={loading}
      onPress={onPress}
      style={styles.primary}
      contentStyle={styles.primaryContent}
      labelStyle={styles.primaryLabel}
    >
      {label}
    </Button>
  );
}

/** 错误提示条：图标 + 文案 + 可选重试。 */
export function LeaveNotice({
  text,
  tone = "danger",
  actionLabel,
  onAction,
}: {
  text: string;
  tone?: "danger" | "warning" | "info";
  actionLabel?: string;
  onAction?: () => void;
}) {
  const palette = tone === "danger"
    ? { background: "#FEF3F2", foreground: HB_COLORS.danger, icon: "alert-circle-outline" as const }
    : tone === "warning"
      ? { background: "#FFFAEB", foreground: HB_COLORS.warning, icon: "alert-outline" as const }
      : { background: "#EFF8FF", foreground: "#175CD3", icon: "information-outline" as const };
  return (
    <View style={[styles.notice, { backgroundColor: palette.background }]} accessibilityRole="alert">
      <MaterialCommunityIcons name={palette.icon} size={18} color={palette.foreground} />
      <Text style={[styles.noticeText, { color: palette.foreground }]}>{text}</Text>
      {actionLabel && onAction ? (
        <Button compact mode="text" textColor={palette.foreground} onPress={onAction}>{actionLabel}</Button>
      ) : null}
    </View>
  );
}

/**
 * 斜纹底：周末、公共假期「不算」的日子。RN 没有重复渐变，用几条旋转细线铺满并裁掉溢出。
 */
export function HatchFill({ color = HB_COLORS.outline }: { color?: string }) {
  return (
    <View style={styles.hatch} pointerEvents="none">
      {Array.from({ length: 12 }, (_, index) => (
        <View key={index} style={[styles.hatchLine, { backgroundColor: color, left: index * 8 - 24 }]} />
      ))}
    </View>
  );
}

/** 日期显示：中文「11-17」「11-17 周二」，英文「17 Nov」「Tue 17 Nov」。 */
export function useLeaveDateFormat() {
  const { t } = useAppTranslation("attendanceLeave");
  const monthDay = useCallback(
    (date: string) => {
      if (!date) return "";
      const month = Number(date.slice(5, 7));
      return t("date.monthDay", {
        mm: date.slice(5, 7),
        dd: date.slice(8, 10),
        d: String(Number(date.slice(8, 10))),
        mon: t(`calendar.monthsShort.${month}`),
      });
    },
    [t],
  );
  const weekday = useCallback(
    (date: string) => {
      const index = weekdayIndex(date);
      return index >= 0 ? t(`weekday.${WEEKDAY_KEYS[index]}`) : "";
    },
    [t],
  );
  const dayLabel = useCallback(
    (date: string) => (date ? t("date.dayWithWeekday", { md: monthDay(date), weekday: weekday(date) }) : ""),
    [monthDay, t, weekday],
  );
  const range = useCallback(
    (start: string, end: string) =>
      !end || start === end ? monthDay(start) : t("date.range", { start: monthDay(start), end: monthDay(end) }),
    [monthDay, t],
  );
  return { monthDay, weekday, dayLabel, range };
}

export const leaveStyles = StyleSheet.create({
  muted: { color: HB_COLORS.textSecondary },
  tabular: { fontVariant: ["tabular-nums"] },
});

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    paddingRight: HB_SPACING.xxs,
  },
  headerTitle: { color: HB_COLORS.textPrimary, flex: 1, fontWeight: "700" },
  headerSpacer: { width: 48 },
  body: { flex: 1 },
  content: { gap: HB_SPACING.sm, padding: HB_SPACING.md },
  footer: {
    backgroundColor: HB_COLORS.white,
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
  },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.sm,
    padding: HB_SPACING.md,
  },
  cardHeader: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, justifyContent: "space-between" },
  cardTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  cardAside: { color: HB_COLORS.textSecondary, flexShrink: 1, textAlign: "right" },
  messageBack: { alignSelf: "flex-start" },
  primary: { borderRadius: HB_RADIUS.control },
  primaryContent: { minHeight: 48 },
  primaryLabel: { fontSize: 16, fontWeight: "700" },
  notice: {
    alignItems: "center",
    borderRadius: HB_RADIUS.control,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  noticeText: { flex: 1, fontSize: 13, lineHeight: 19 },
  hatch: { ...StyleSheet.absoluteFillObject, overflow: "hidden" },
  hatchLine: {
    height: 120,
    opacity: 0.55,
    position: "absolute",
    top: -40,
    transform: [{ rotate: "45deg" }],
    width: 1,
  },
});
