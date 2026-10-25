// 工时统计两屏共用的小组件：页面框架、头像、角标、周数分段、汇总格、斜纹底、提示条与配色。
// 视觉沿用业务页令牌（HB_COLORS / HB_SPACING / HB_RADIUS），数字统一 tabular-nums 便于竖向对齐。
import type { ReactNode } from "react";
import { Pressable, StyleSheet, View, type StyleProp, type TextStyle, type ViewStyle } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Button, IconButton, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { avatarColor, employeeInitials } from "@/modules/attendance/payroll-hours";
import { PatternFill } from "@/components/attendance/approvals/approval-ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export const MONO: TextStyle = { fontVariant: ["tabular-nums"] };

/** 工时页专用配色：周末橙、待审黄虚线、年假紫、病假红、公假浅橙、分档小计蓝。 */
export const HOURS_COLORS = {
  card: HB_COLORS.white,
  cellBg: "#F8F9FC",
  cellBorder: HB_COLORS.outlineMuted,
  weekendBg: "#FFF6ED",
  weekendBorder: "#F9DBAF",
  weekendText: "#C4320A",
  pendingBorder: "#F79009",
  pendingBg: "#FFFCF5",
  annualBg: "#F4F3FF",
  annualBorder: "#D9D6FE",
  annualText: "#5925DC",
  sickBg: "#FEF3F2",
  sickBorder: "#FECDCA",
  sickText: HB_COLORS.danger,
  holidayBg: "#FFF6ED",
  holidayBorder: "#F9DBAF",
  holidayText: "#C4320A",
  hatchBg: HB_COLORS.surfaceMuted,
  // 斜纹线要比底色深一档，否则在 surfaceMuted 底上看不出来。
  hatchLine: "#D0D5DD",
  tierText: "#175CD3",
  restBorder: "#E4E7EC",
  restText: "#98A2B3",
  cashOutBg: "#ECFDF3",
  cashOutBorder: "#ABEFC6",
  cashOutText: HB_COLORS.success,
  auto: HB_COLORS.danger,
} as const;

export type HoursTone = "pink" | "warning" | "info" | "neutral";

const ALERT_TONES: Record<HoursTone, { background: string; border: string; text: string }> = {
  pink: { background: "#FDF2FA", border: "#FCCEEE", text: "#C11574" },
  warning: { background: "#FFFAEB", border: "#FEDF89", text: HB_COLORS.warning },
  info: { background: "#EFF8FF", border: "#B2DDFF", text: "#175CD3" },
  neutral: { background: HB_COLORS.surfaceMuted, border: HB_COLORS.outlineMuted, text: HB_COLORS.textSecondary },
};

type IconName = keyof typeof MaterialCommunityIcons.glyphMap;

/** 页面框架：返回 + 中间内容（标题或头像姓名）+ 右侧控件；内容区自行滚动。 */
export function HoursFrame({ onBack, middle, right, children }: { onBack: () => void; middle: ReactNode; right?: ReactNode; children: ReactNode }) {
  const { t } = useAppTranslation("common");
  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={t("actions.back")} onPress={onBack} style={styles.back} />
        <View style={styles.headerMiddle}>{middle}</View>
        {right ? <View style={styles.headerRight}>{right}</View> : null}
      </View>
      {children}
    </SafeAreaView>
  );
}

export function HoursTitle({ title }: { title: string }) {
  return (
    <Text variant="titleMedium" style={styles.title} numberOfLines={1}>
      {title}
    </Text>
  );
}

/** 整页提示（无权限 / 审核模式 / 缺参数）加返回按钮。 */
export function HoursMessage({ message, backLabel, onBack }: { message: string; backLabel: string; onBack: () => void }) {
  return (
    <SafeAreaView style={styles.message}>
      <Text accessibilityLiveRegion="polite" style={styles.messageText}>
        {message}
      </Text>
      <Button onPress={onBack}>{backLabel}</Button>
    </SafeAreaView>
  );
}

export function HoursCard({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  return <View style={[styles.card, style]}>{children}</View>;
}

export function HoursLoading() {
  return (
    <View style={styles.loading}>
      <ActivityIndicator />
    </View>
  );
}

export function HoursError({ title, retryLabel, onRetry }: { title: string; retryLabel: string; onRetry: () => void }) {
  return (
    <HoursCard style={styles.errorCard}>
      <Text style={styles.errorTitle}>{title}</Text>
      <Button mode="contained" onPress={onRetry}>
        {retryLabel}
      </Button>
    </HoursCard>
  );
}

/** 彩色圆形头像（姓名首字母），同一个人颜色固定。 */
export function HoursAvatar({ name, seed, size = 32 }: { name: string; seed: string; size?: number }) {
  return (
    <View style={[styles.avatar, { width: size, height: size, borderRadius: size / 2, backgroundColor: avatarColor(seed) }]}>
      <Text style={[styles.avatarText, { fontSize: size * 0.38 }]} allowFontScaling={false}>
        {employeeInitials(name)}
      </Text>
    </View>
  );
}

/** 姓名旁的小角标（全职 / 兼职 / 临时 / 17 岁 / 多店）。 */
export function HoursBadge({ label, tone = "neutral" }: { label: string; tone?: "neutral" | "info" | "pink" }) {
  const palette = tone === "neutral" ? { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary } : tone === "info" ? { bg: "#EFF8FF", fg: "#175CD3" } : { bg: "#FDF2FA", fg: "#C11574" };
  return (
    <View style={[styles.badge, { backgroundColor: palette.bg }]}>
      <Text style={[styles.badgeText, { color: palette.fg }]} numberOfLines={1}>
        {label}
      </Text>
    </View>
  );
}

/** 1 周 / 2 周分段。 */
export function WeeksSegment({ value, onChange, labels, accessibilityLabel }: { value: 1 | 2; onChange: (weeks: 1 | 2) => void; labels: { one: string; two: string }; accessibilityLabel: string }) {
  return (
    <View style={styles.segment} accessibilityRole="tablist" accessibilityLabel={accessibilityLabel}>
      {([1, 2] as const).map((weeks) => {
        const selected = weeks === value;
        return (
          <Pressable
            key={weeks}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(weeks)}
            style={[styles.segmentItem, selected ? styles.segmentItemOn : null]}
            hitSlop={4}
          >
            <Text style={[styles.segmentText, selected ? styles.segmentTextOn : null]} numberOfLines={1}>
              {weeks === 1 ? labels.one : labels.two}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/** 一排汇总格：标签 + 大数字。 */
export function SummaryTiles({ items }: { items: { label: string; value: string; color?: string }[] }) {
  return (
    <View style={styles.tiles}>
      {items.map((item) => (
        <View key={item.label} style={styles.tile} accessible accessibilityLabel={`${item.label} ${item.value}`}>
          <Text style={styles.tileLabel} numberOfLines={1}>
            {item.label}
          </Text>
          <Text style={[styles.tileValue, MONO, item.color ? { color: item.color } : null]} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.7}>
            {item.value}
          </Text>
        </View>
      ))}
    </View>
  );
}

/** 斜纹底（不能上班）：铺满父容器，父容器需 overflow hidden 与圆角。 */
export function Hatch({ color = HOURS_COLORS.hatchLine }: { color?: string }) {
  // 与审核时间轴共用纯 View 斜纹：react-native-svg 在宽行里视口会被截短，斜纹只铺到一半。
  return <PatternFill color={color} variant="diagonal" />;
}

/** 提示条：图标 + 文案 + 右侧动作（可选）；颜色之外还有图标区分。 */
export function HoursAlertBar({ tone, icon, text, action }: { tone: HoursTone; icon: IconName; text: string; action?: { label: string; onPress: () => void } }) {
  const palette = ALERT_TONES[tone];
  return (
    <View style={[styles.alert, { backgroundColor: palette.background, borderColor: palette.border }]}>
      <MaterialCommunityIcons name={icon} size={18} color={palette.text} />
      <Text style={[styles.alertText, { color: palette.text }]} numberOfLines={2}>
        {text}
      </Text>
      {action ? (
        <Pressable accessibilityRole="button" onPress={action.onPress} hitSlop={8} style={styles.alertAction}>
          <Text style={[styles.alertActionText, { color: palette.text }]}>{action.label}</Text>
          <MaterialCommunityIcons name="chevron-right" size={16} color={palette.text} />
        </Pressable>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: {
    flexDirection: "row",
    alignItems: "center",
    paddingRight: HB_SPACING.sm,
    minHeight: 56,
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  back: { margin: 0 },
  headerMiddle: { flex: 1, flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minWidth: 0 },
  headerRight: { flexShrink: 0, marginLeft: HB_SPACING.xs },
  title: { fontWeight: "700", color: HB_COLORS.textPrimary, flexShrink: 1 },
  message: { flex: 1, alignItems: "center", justifyContent: "center", padding: HB_SPACING.lg, gap: HB_SPACING.sm, backgroundColor: HB_COLORS.background },
  messageText: { textAlign: "center", color: HB_COLORS.textPrimary },
  card: {
    backgroundColor: HOURS_COLORS.card,
    borderColor: HB_COLORS.outlineMuted,
    borderWidth: 1,
    borderRadius: HB_RADIUS.surface,
    padding: HB_SPACING.sm,
  },
  loading: { paddingVertical: HB_SPACING.xl, alignItems: "center" },
  errorCard: { alignItems: "center", gap: HB_SPACING.sm, paddingVertical: HB_SPACING.lg },
  errorTitle: { fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  avatar: { alignItems: "center", justifyContent: "center" },
  avatarText: { color: HB_COLORS.white, fontWeight: "700" },
  badge: { borderRadius: 4, paddingHorizontal: 5, paddingVertical: 1 },
  badgeText: { fontSize: 11, fontWeight: "600" },
  segment: { flexDirection: "row", backgroundColor: HB_COLORS.surfaceMuted, borderRadius: 10, padding: 3 },
  segmentItem: { paddingHorizontal: 12, height: 34, borderRadius: 8, alignItems: "center", justifyContent: "center" },
  segmentItemOn: { backgroundColor: HB_COLORS.white, shadowColor: "#101828", shadowOpacity: 0.08, shadowRadius: 2, shadowOffset: { width: 0, height: 1 }, elevation: 1 },
  segmentText: { fontSize: 13.5, color: HB_COLORS.textSecondary, fontWeight: "500" },
  segmentTextOn: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  tiles: { flexDirection: "row", gap: 6 },
  tile: {
    flex: 1,
    minWidth: 0,
    backgroundColor: HOURS_COLORS.card,
    borderColor: HB_COLORS.outlineMuted,
    borderWidth: 1,
    borderRadius: HB_RADIUS.surface,
    paddingHorizontal: 10,
    paddingVertical: 8,
  },
  tileLabel: { fontSize: 12, color: HB_COLORS.textSecondary },
  tileValue: { fontSize: 20, fontWeight: "700", color: HB_COLORS.textPrimary, marginTop: 2 },
  alert: { flexDirection: "row", alignItems: "center", gap: 8, borderWidth: 1, borderRadius: 10, paddingHorizontal: 12, paddingVertical: 9 },
  alertText: { flex: 1, fontSize: 13, fontWeight: "500" },
  alertAction: { flexDirection: "row", alignItems: "center", flexShrink: 0 },
  alertActionText: { fontSize: 13, fontWeight: "700" },
});
