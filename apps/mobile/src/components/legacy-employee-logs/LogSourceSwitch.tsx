import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import type { LogSource } from "@/modules/legacy-employee-logs/types";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

/** 数据来源切换：老收银 / 新收银。只在两个来源都有权限时由页面渲染。 */
export function LogSourceSwitch({
  value,
  labels,
  accessibilityLabel,
  onChange,
}: {
  value: LogSource;
  labels: Record<LogSource, string>;
  accessibilityLabel: string;
  onChange: (source: LogSource) => void;
}) {
  return (
    <View style={styles.row} accessibilityRole="radiogroup" accessibilityLabel={accessibilityLabel}>
      {(["legacy", "pos"] as const).map((source) => {
        const active = source === value;
        return (
          <Pressable
            key={source}
            accessibilityRole="radio"
            accessibilityState={{ selected: active }}
            onPress={() => (active ? undefined : onChange(source))}
            style={[styles.option, active ? styles.optionOn : null]}
          >
            <Text style={[styles.text, active ? styles.textOn : null]}>{labels[source]}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: "row", gap: HB_SPACING.xs, marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs },
  option: {
    minHeight: 32,
    paddingHorizontal: 14,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    alignItems: "center",
    justifyContent: "center",
  },
  optionOn: { borderColor: HB_COLORS.action, backgroundColor: "#EAF2FF" },
  text: { fontSize: 13, color: HB_COLORS.textPrimary },
  textOn: { fontWeight: "700", color: HB_COLORS.action },
});
