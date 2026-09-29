import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { ORDER_COLORS } from "./order-ui";
import { buildQuantityPresets } from "./quantity-presets";

interface QuantityPresetRowProps {
  step: number;
  value: string;
  disabled?: boolean;
  onSelect: (quantity: number) => void;
}

/** 数量编辑的快捷数量：只改输入框草稿，仍由「确认」统一提交。 */
export function QuantityPresetRow({ step, value, disabled = false, onSelect }: QuantityPresetRowProps) {
  const { t } = useAppTranslation("common");
  const presets = buildQuantityPresets(step);
  const current = value.trim();

  return (
    <View style={styles.wrap}>
      <Text style={styles.caption}>{t("quantityPresets.caption", { step: presets[1] })}</Text>
      <View style={styles.row}>
        {presets.map((preset) => {
          const selected = current === String(preset);
          const isClear = preset === 0;
          return (
            <Pressable
              key={preset}
              accessibilityRole="button"
              accessibilityState={{ selected, disabled }}
              accessibilityLabel={isClear ? t("quantityPresets.clear") : t("quantityPresets.set", { quantity: preset })}
              disabled={disabled}
              onPress={() => onSelect(preset)}
              style={({ pressed }) => [
                styles.preset,
                selected ? styles.presetSelected : null,
                pressed ? styles.presetPressed : null,
              ]}
            >
              <Text
                style={[
                  styles.presetText,
                  isClear ? styles.presetClearText : null,
                  selected ? styles.presetSelectedText : null,
                ]}
              >
                {isClear ? t("quantityPresets.clear") : preset}
              </Text>
            </Pressable>
          );
        })}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    gap: 6,
  },
  caption: {
    color: ORDER_COLORS.subtleText,
    fontSize: 12,
    lineHeight: 16,
  },
  row: {
    flexDirection: "row",
    gap: 6,
  },
  preset: {
    flex: 1,
    minHeight: 44,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 8,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  presetSelected: {
    borderColor: HB_COLORS.action,
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  presetPressed: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  presetText: {
    color: HB_COLORS.textPrimary,
    fontSize: 15,
    fontWeight: "600",
    fontVariant: ["tabular-nums"],
  },
  presetClearText: {
    color: ORDER_COLORS.danger,
    fontSize: 14,
  },
  presetSelectedText: {
    color: ORDER_COLORS.tonalText,
    fontWeight: "700",
  },
});
