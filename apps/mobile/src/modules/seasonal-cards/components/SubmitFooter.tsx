import { StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import type { SeasonalCardSubmitState } from "@/modules/seasonal-cards/submit-draft";
import { SEASONAL_CARD_COLORS } from "./palette";

/** 底部固定栏：组合 + 合计张数与金额；按钮三态 提交填报 / 覆盖提交 / 未修改。 */
export function SubmitFooter({
  summaryLine,
  totalLine,
  state,
  buttonLabel,
  busy,
  onPress,
}: {
  summaryLine: string;
  totalLine: string;
  state: SeasonalCardSubmitState;
  buttonLabel: string;
  busy: boolean;
  onPress: () => void;
}) {
  const insets = useSafeAreaInsets();
  const disabled = busy || state === "disabled" || state === "unchanged";

  return (
    <View
      style={[
        BUSINESS_UI.footer,
        styles.footer,
        { paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) },
      ]}
    >
      <View style={styles.summary}>
        <Text style={styles.summaryLine}>{summaryLine}</Text>
        <Text style={styles.totalLine}>{totalLine}</Text>
      </View>
      <Button
        mode="contained"
        loading={busy}
        disabled={disabled}
        onPress={onPress}
        style={styles.button}
        contentStyle={styles.buttonContent}
        labelStyle={styles.buttonLabel}
      >
        {buttonLabel}
      </Button>
    </View>
  );
}

const styles = StyleSheet.create({
  footer: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  summary: { flex: 1, gap: 2 },
  summaryLine: { fontSize: 12, lineHeight: 17, color: SEASONAL_CARD_COLORS.mutedText },
  totalLine: {
    fontSize: 18,
    lineHeight: 24,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  button: { borderRadius: 10, flexShrink: 0, maxWidth: "50%" },
  buttonContent: { minHeight: 48, paddingHorizontal: 6 },
  buttonLabel: { fontSize: 16, lineHeight: 22, fontWeight: "600" },
});
