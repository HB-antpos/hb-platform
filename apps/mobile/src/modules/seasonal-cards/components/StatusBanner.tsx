import { StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { HB_SPACING } from "@/shared/theme/tokens";
import { SEASONAL_CARD_COLORS } from "./palette";

export type StatusBannerTone = "info" | "warning" | "danger";

const TONES: Record<StatusBannerTone, { background: string; border: string; text: string }> = {
  info: {
    background: SEASONAL_CARD_COLORS.infoBackground,
    border: SEASONAL_CARD_COLORS.infoBorder,
    text: SEASONAL_CARD_COLORS.infoText,
  },
  warning: {
    background: SEASONAL_CARD_COLORS.warningBackground,
    border: SEASONAL_CARD_COLORS.warningBorder,
    text: SEASONAL_CARD_COLORS.warningText,
  },
  danger: {
    background: SEASONAL_CARD_COLORS.dangerBackground,
    border: SEASONAL_CARD_COLORS.dangerBorder,
    text: SEASONAL_CARD_COLORS.dangerText,
  },
};

/** 填报状态提示条：没填过蓝色、已填过黄色、被他人抢先更新或加载失败红色。 */
export function StatusBanner({
  tone,
  title,
  body,
  actionLabel,
  onAction,
}: {
  tone: StatusBannerTone;
  title: string;
  body?: string;
  actionLabel?: string;
  onAction?: () => void;
}) {
  const palette = TONES[tone];
  return (
    <View
      accessibilityRole="summary"
      style={[styles.banner, { backgroundColor: palette.background, borderColor: palette.border }]}
    >
      <View style={styles.icon}>
        <Icon source="information-outline" size={18} color={palette.text} />
      </View>
      <View style={styles.texts}>
        <Text style={[styles.title, { color: palette.text }]}>{title}</Text>
        {body ? <Text style={[styles.body, { color: palette.text }]}>{body}</Text> : null}
        {actionLabel && onAction ? (
          <Button
            compact
            mode="text"
            onPress={onAction}
            textColor={palette.text}
            style={styles.action}
            contentStyle={styles.actionContent}
          >
            {actionLabel}
          </Button>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: HB_SPACING.sm,
    borderRadius: 10,
    borderWidth: 1,
  },
  icon: { paddingTop: 1 },
  texts: { flex: 1, gap: 2 },
  title: { fontSize: 13, lineHeight: 20, fontWeight: "600" },
  body: { fontSize: 13, lineHeight: 20 },
  action: { alignSelf: "flex-start", marginLeft: -8 },
  actionContent: { minHeight: 44 },
});
