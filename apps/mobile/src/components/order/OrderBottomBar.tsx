import type { ComponentProps, ReactNode } from "react";
import { ActivityIndicator, Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text } from "react-native-paper";
import { HB_COLORS } from "@/shared/theme/tokens";
import type { OrderNotice, OrderNoticeTone } from "./order-notice";
import { ORDER_COLORS } from "./order-ui";

type IconName = ComponentProps<typeof MaterialCommunityIcons>["name"];

const NOTICE_ICONS: Record<OrderNoticeTone, { icon: IconName; background: string; color: string }> = {
  success: { icon: "check-bold", background: "#079455", color: HB_COLORS.white },
  info: { icon: "information-variant", background: HB_COLORS.action, color: HB_COLORS.white },
  warning: { icon: "exclamation-thick", background: "#DC6803", color: HB_COLORS.white },
  error: { icon: "close-thick", background: "#D92D20", color: HB_COLORS.white },
  // 暂停供货用深灰：与「未找到」的琥珀色、失败的红色一眼可分。
  paused: { icon: "package-variant-remove", background: ORDER_COLORS.barSubtext, color: ORDER_COLORS.barBackground },
};

interface OrderBottomBarProps {
  leading?: ReactNode;
  notice?: OrderNotice | null;
  title: string;
  subtitle?: string;
  /** 购物车把金额作为主数字，用更大的字号。 */
  emphasizeTitle?: boolean;
  action: ReactNode;
}

/** 订货页与购物车共用的底部操作栏：左侧汇总，闪现提示时替换汇总，右侧是主操作。 */
export function OrderBottomBar({ leading, notice, title, subtitle, emphasizeTitle = false, action }: OrderBottomBarProps) {
  const noticeIcon = notice ? NOTICE_ICONS[notice.tone] : null;

  return (
    <View style={styles.bar}>
      {leading}
      <View style={styles.center} accessibilityLiveRegion="polite">
        {notice && noticeIcon ? (
          <View style={styles.noticeRow}>
            <View style={[styles.noticeIcon, { backgroundColor: noticeIcon.background }]}>
              <MaterialCommunityIcons name={noticeIcon.icon} size={14} color={noticeIcon.color} />
            </View>
            <View style={styles.textColumn}>
              <Text numberOfLines={1} style={styles.noticeTitle}>
                {notice.title}
              </Text>
              {notice.detail ? (
                <Text numberOfLines={1} style={styles.subtitle}>
                  {notice.detail}
                </Text>
              ) : null}
            </View>
          </View>
        ) : (
          <View style={styles.textColumn}>
            <Text numberOfLines={1} style={[styles.title, emphasizeTitle ? styles.titleLarge : null]}>
              {title}
            </Text>
            {subtitle ? (
              <Text numberOfLines={1} style={styles.subtitle}>
                {subtitle}
              </Text>
            ) : null}
          </View>
        )}
      </View>
      {action}
    </View>
  );
}

interface OrderBarIconButtonProps {
  icon: IconName;
  accessibilityLabel: string;
  onPress: () => void;
  disabled?: boolean;
}

export function OrderBarIconButton({ icon, accessibilityLabel, onPress, disabled = false }: OrderBarIconButtonProps) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [styles.iconButton, pressed ? styles.iconButtonPressed : null, disabled ? styles.disabled : null]}
    >
      <MaterialCommunityIcons name={icon} size={22} color={ORDER_COLORS.barText} />
    </Pressable>
  );
}

type PrimaryTone = "action" | "warning" | "disabled";

interface OrderBarPrimaryButtonProps {
  label: string;
  onPress: () => void;
  icon?: IconName;
  tone?: PrimaryTone;
  loading?: boolean;
  disabled?: boolean;
  large?: boolean;
  accessibilityLabel?: string;
}

const PRIMARY_TONES: Record<PrimaryTone, { background: string; text: string }> = {
  action: { background: HB_COLORS.action, text: HB_COLORS.white },
  warning: { background: HB_COLORS.warning, text: HB_COLORS.white },
  disabled: { background: "#344054", text: "#98A2B3" },
};

export function OrderBarPrimaryButton({
  label,
  onPress,
  icon,
  tone = "action",
  loading = false,
  disabled = false,
  large = false,
  accessibilityLabel,
}: OrderBarPrimaryButtonProps) {
  const colors = PRIMARY_TONES[disabled ? "disabled" : tone];

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? label}
      accessibilityState={{ disabled: disabled || loading, busy: loading }}
      disabled={disabled || loading}
      onPress={onPress}
      style={({ pressed }) => [
        styles.primaryButton,
        large ? styles.primaryButtonLarge : null,
        { backgroundColor: colors.background },
        pressed ? styles.primaryPressed : null,
      ]}
    >
      {loading ? <ActivityIndicator size="small" color={colors.text} /> : null}
      <Text style={[styles.primaryText, large ? styles.primaryTextLarge : null, { color: colors.text }]}>{label}</Text>
      {icon && !loading ? <MaterialCommunityIcons name={icon} size={18} color={colors.text} /> : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  bar: {
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 12,
    paddingVertical: 8,
    backgroundColor: ORDER_COLORS.barBackground,
  },
  center: {
    flex: 1,
    minWidth: 0,
  },
  noticeRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  noticeIcon: {
    width: 24,
    height: 24,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
  },
  textColumn: {
    flex: 1,
    minWidth: 0,
  },
  title: {
    color: ORDER_COLORS.barText,
    fontSize: 15,
    lineHeight: 20,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  titleLarge: {
    fontSize: 19,
    lineHeight: 24,
  },
  noticeTitle: {
    color: ORDER_COLORS.barText,
    fontSize: 14,
    lineHeight: 18,
    fontWeight: "700",
  },
  subtitle: {
    color: ORDER_COLORS.barSubtext,
    fontSize: 12,
    lineHeight: 16,
    fontVariant: ["tabular-nums"],
  },
  iconButton: {
    width: 44,
    height: 44,
    borderRadius: 10,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: ORDER_COLORS.barButton,
  },
  iconButtonPressed: {
    backgroundColor: "#344054",
  },
  disabled: {
    opacity: 0.5,
  },
  primaryButton: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingHorizontal: 14,
    borderRadius: 10,
  },
  primaryButtonLarge: {
    minHeight: 48,
    paddingHorizontal: 16,
  },
  primaryPressed: {
    opacity: 0.85,
  },
  primaryText: {
    fontSize: 15,
    fontWeight: "700",
  },
  primaryTextLarge: {
    fontSize: 16,
  },
});
