// 工资单两屏 + 邮件弹层共用的小组件与配色。视觉沿用业务页令牌（HB_COLORS / HB_SPACING / HB_RADIUS），
// 数字统一 tabular-nums 便于竖向对齐；所有可点区域 ≥ 44pt。
import type { ReactNode } from "react";
import { Pressable, StyleSheet, View, type TextStyle } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Button, IconButton, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export const MONO: TextStyle = { fontVariant: ["tabular-nums"] };

/** 工资单页专用配色：纸面黑线 / 灰表头 / 实发黑底，与设计稿一致。 */
export const PAYSLIP_COLORS = {
  paper: HB_COLORS.white,
  paperLine: "#101828",
  tableHeadBg: "#D9D9D9",
  netBg: "#101828",
  tagBorder: "#98A2B3",
  chipOnBg: "#EFF8FF",
  chipOnBorder: "#84CAFF",
  infoBg: "#EFF8FF",
  infoBorder: "#B2DDFF",
  infoText: "#175CD3",
  warnBg: "#FFFAEB",
  warnBorder: "#FEDF89",
  dangerBg: "#FEF3F2",
  dangerBorder: "#FECDCA",
  successBg: "#ECFDF3",
} as const;

type IconName = keyof typeof MaterialCommunityIcons.glyphMap;

/** 页面框架：返回 + 标题 + 右侧控件；内容区自行滚动。 */
export function PayslipFrame({ onBack, title, right, children }: { onBack: () => void; title: string; right?: ReactNode; children: ReactNode }) {
  const { t } = useAppTranslation("common");
  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" size={28} accessibilityLabel={t("actions.back")} onPress={onBack} style={styles.iconButton} />
        <Text accessibilityRole="header" style={styles.title} numberOfLines={1}>
          {title}
        </Text>
        {right ?? <View style={styles.iconButton} />}
      </View>
      {children}
    </SafeAreaView>
  );
}

/** 整页提示（无权限 / 审核模式 / 缺参数）加返回按钮。 */
export function PayslipMessage({ message, backLabel, onBack }: { message: string; backLabel: string; onBack: () => void }) {
  return (
    <SafeAreaView style={styles.message}>
      <Text accessibilityLiveRegion="polite" style={styles.messageText}>
        {message}
      </Text>
      <Button onPress={onBack}>{backLabel}</Button>
    </SafeAreaView>
  );
}

/** 筛选胶囊：选中态浅蓝底 + 蓝边；带下拉箭头的用于弹层选择。 */
export function FilterChip({
  label,
  selected = false,
  dropdown = false,
  onPress,
  accessibilityLabel,
}: {
  label: string;
  selected?: boolean;
  dropdown?: boolean;
  onPress: () => void;
  accessibilityLabel?: string;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? label}
      accessibilityState={{ selected }}
      onPress={onPress}
      style={({ pressed }) => [styles.chip, selected ? styles.chipOn : null, pressed ? styles.pressed : null]}
    >
      <Text style={[styles.chipText, selected ? styles.chipTextOn : null]} numberOfLines={1}>
        {label}
      </Text>
      {dropdown ? <MaterialCommunityIcons name="chevron-down" size={18} color={selected ? HB_COLORS.action : HB_COLORS.textSecondary} /> : null}
    </Pressable>
  );
}

export type TagTone = "neutral" | "success" | "info" | "warning";

const TAG_TONES: Record<TagTone, { bg: string; fg: string }> = {
  neutral: { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary },
  success: { bg: PAYSLIP_COLORS.successBg, fg: HB_COLORS.success },
  info: { bg: PAYSLIP_COLORS.infoBg, fg: PAYSLIP_COLORS.infoText },
  warning: { bg: "#FEF0C7", fg: HB_COLORS.warning },
};

/** 小标签（已发薪 / Weekly / 分店 / 未绑定）。 */
export function Tag({ label, tone = "neutral" }: { label: string; tone?: TagTone }) {
  const palette = TAG_TONES[tone];
  return (
    <View style={[styles.tag, { backgroundColor: palette.bg }]}>
      <Text style={[styles.tagText, { color: palette.fg }]} numberOfLines={1}>
        {label}
      </Text>
    </View>
  );
}

/** 提示条：图标 + 文案 + 右侧动作（可选）；颜色之外还有图标区分。 */
export function NoticeBar({
  tone,
  icon,
  text,
  action,
}: {
  tone: "info" | "warning" | "danger";
  icon: IconName;
  text: string;
  action?: { label: string; onPress: () => void };
}) {
  const palette =
    tone === "danger"
      ? { bg: PAYSLIP_COLORS.dangerBg, border: PAYSLIP_COLORS.dangerBorder, fg: HB_COLORS.danger }
      : tone === "warning"
        ? { bg: PAYSLIP_COLORS.warnBg, border: PAYSLIP_COLORS.warnBorder, fg: HB_COLORS.warning }
        : { bg: PAYSLIP_COLORS.infoBg, border: PAYSLIP_COLORS.infoBorder, fg: PAYSLIP_COLORS.infoText };
  return (
    <View style={[styles.notice, { backgroundColor: palette.bg, borderColor: palette.border }]} accessibilityLiveRegion="polite">
      <MaterialCommunityIcons name={icon} size={20} color={palette.fg} />
      <Text style={[styles.noticeText, { color: palette.fg }]}>{text}</Text>
      {action ? (
        <Pressable accessibilityRole="button" onPress={action.onPress} style={styles.noticeAction} hitSlop={4}>
          <Text style={[styles.noticeActionText, { color: palette.fg }]}>{action.label}</Text>
        </Pressable>
      ) : null}
    </View>
  );
}

export function PayslipLoading({ label }: { label: string }) {
  return (
    <View style={styles.loading} accessibilityLiveRegion="polite" accessibilityLabel={label}>
      <ActivityIndicator />
      <Text style={styles.loadingText}>{label}</Text>
    </View>
  );
}

export function PayslipErrorCard({ title, detail, retryLabel, onRetry }: { title: string; detail?: string; retryLabel: string; onRetry: () => void }) {
  return (
    <View style={styles.errorCard} accessibilityLiveRegion="polite">
      <Text style={styles.errorTitle}>{title}</Text>
      {detail ? <Text style={styles.errorDetail}>{detail}</Text> : null}
      <Button mode="contained" onPress={onRetry} style={styles.errorButton} contentStyle={styles.buttonContent}>
        {retryLabel}
      </Button>
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center", minHeight: 56, paddingHorizontal: HB_SPACING.xxs, backgroundColor: HB_COLORS.surface },
  iconButton: { margin: 0, width: 48, height: 48 },
  title: { flex: 1, fontSize: 20, lineHeight: 28, fontWeight: "700", color: HB_COLORS.textPrimary },
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: HB_SPACING.sm, padding: HB_SPACING.lg, backgroundColor: HB_COLORS.background },
  messageText: { textAlign: "center", color: HB_COLORS.textPrimary, fontSize: 15, lineHeight: 22 },
  chip: {
    minHeight: 44,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: 22,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
  },
  chipOn: { backgroundColor: PAYSLIP_COLORS.chipOnBg, borderColor: PAYSLIP_COLORS.chipOnBorder },
  chipText: { fontSize: 14, lineHeight: 20, color: HB_COLORS.textPrimary },
  chipTextOn: { color: HB_COLORS.action, fontWeight: "600" },
  pressed: { opacity: 0.7 },
  tag: { paddingHorizontal: 10, paddingVertical: 3, borderRadius: 12, alignSelf: "flex-start" },
  tagText: { fontSize: 13, lineHeight: 18 },
  notice: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.xs, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.control, borderWidth: 1 },
  noticeText: { flex: 1, fontSize: 14, lineHeight: 20 },
  noticeAction: { minHeight: 44, minWidth: 44, justifyContent: "center", alignItems: "flex-end", marginVertical: -12 },
  noticeActionText: { fontSize: 14, fontWeight: "700" },
  loading: { alignItems: "center", gap: HB_SPACING.xs, paddingVertical: HB_SPACING.xl },
  loadingText: { color: HB_COLORS.textSecondary, fontSize: 14 },
  errorCard: { margin: HB_SPACING.md, padding: HB_SPACING.md, gap: HB_SPACING.xs, borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted },
  errorTitle: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary },
  errorDetail: { fontSize: 14, lineHeight: 20, color: HB_COLORS.textSecondary },
  errorButton: { alignSelf: "flex-start", marginTop: HB_SPACING.xxs },
  buttonContent: { minHeight: 44 },
});
