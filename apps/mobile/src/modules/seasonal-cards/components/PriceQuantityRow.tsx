import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { HelperText, Icon, Text } from "react-native-paper";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { SEASONAL_CARD_COLORS } from "./palette";

export type PriceRowHintTone = "muted" | "changed";

/**
 * 一个价格类型的剩余数量：44px 加减按钮 + 数字输入；「其他」额外展开实际单价。
 * 改过的行整行高亮并显示「上次 N → 本次 M」。模块内自写，不复用与下单耦合的 OrderStepper。
 */
export function PriceQuantityRow({
  label,
  hint,
  hintTone,
  changed,
  quantity,
  disabled = false,
  decreaseLabel,
  increaseLabel,
  quantityA11yLabel,
  onChangeQuantity,
  onStep,
  customPrice,
}: {
  label: string;
  hint: string;
  hintTone: PriceRowHintTone;
  changed: boolean;
  quantity: string;
  disabled?: boolean;
  decreaseLabel: string;
  increaseLabel: string;
  quantityA11yLabel: string;
  onChangeQuantity: (text: string) => void;
  onStep: (delta: number) => void;
  /** 只有「其他」价格传入：实际单价输入。 */
  customPrice?: {
    label: string;
    value: string;
    error?: string | null;
    onChange: (text: string) => void;
  };
}) {
  return (
    <View style={[styles.row, changed ? styles.rowChanged : null]}>
      <View style={styles.mainLine}>
        <View style={styles.labelBlock}>
          <Text style={styles.label}>{label}</Text>
          <Text style={[styles.hint, hintTone === "changed" ? styles.hintChanged : null]}>
            {hint}
          </Text>
        </View>
        <View
          style={[
            styles.stepper,
            changed ? styles.stepperChanged : null,
            disabled ? styles.stepperDisabled : null,
          ]}
        >
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={`${label} ${decreaseLabel}`}
            accessibilityState={{ disabled }}
            disabled={disabled}
            onPress={() => onStep(-1)}
            style={({ pressed }) => [styles.stepButton, pressed ? styles.stepPressed : null]}
          >
            <Icon source="minus" size={18} color={disabled ? SEASONAL_CARD_COLORS.disabledText : "#344054"} />
          </Pressable>
          <TextInput
            accessibilityLabel={quantityA11yLabel}
            editable={!disabled}
            value={quantity}
            onChangeText={onChangeQuantity}
            keyboardType="number-pad"
            inputMode="numeric"
            placeholder="0"
            placeholderTextColor={SEASONAL_CARD_COLORS.disabledText}
            selectTextOnFocus
            style={[styles.input, disabled ? styles.inputDisabled : null]}
          />
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={`${label} ${increaseLabel}`}
            accessibilityState={{ disabled }}
            disabled={disabled}
            onPress={() => onStep(1)}
            style={({ pressed }) => [styles.stepButton, pressed ? styles.stepPressed : null]}
          >
            <Icon source="plus" size={18} color={disabled ? SEASONAL_CARD_COLORS.disabledText : "#344054"} />
          </Pressable>
        </View>
      </View>

      {customPrice ? (
        <View style={styles.customBlock}>
          <View style={styles.customLine}>
            <Text style={styles.customLabel}>{customPrice.label}</Text>
            <View
              style={[
                styles.priceBox,
                customPrice.error ? styles.priceBoxError : null,
                disabled ? styles.stepperDisabled : null,
              ]}
            >
              <Text style={styles.currency}>$</Text>
              <TextInput
                accessibilityLabel={`${label} ${customPrice.label}`}
                editable={!disabled}
                value={customPrice.value}
                onChangeText={customPrice.onChange}
                keyboardType="decimal-pad"
                inputMode="decimal"
                placeholder="0.00"
                placeholderTextColor={SEASONAL_CARD_COLORS.disabledText}
                style={styles.priceInput}
              />
            </View>
          </View>
          {customPrice.error ? (
            <HelperText type="error" visible style={styles.helper}>
              {customPrice.error}
            </HelperText>
          ) : null}
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  row: {
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.surfaceMuted,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: 10,
    gap: 10,
  },
  rowChanged: { backgroundColor: SEASONAL_CARD_COLORS.changedRowBackground },
  mainLine: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  labelBlock: { flex: 1, gap: 2 },
  label: { fontSize: 17, lineHeight: 22, fontWeight: "600", color: HB_COLORS.textPrimary },
  hint: { fontSize: 12, lineHeight: 16, color: SEASONAL_CARD_COLORS.mutedText },
  hintChanged: { color: SEASONAL_CARD_COLORS.selectedText, fontWeight: "600" },
  stepper: {
    flexDirection: "row",
    alignItems: "center",
    borderRadius: 10,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    overflow: "hidden",
  },
  stepperChanged: { borderWidth: 1.5, borderColor: SEASONAL_CARD_COLORS.selectedBorder },
  stepperDisabled: { opacity: 0.5 },
  stepButton: {
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: SEASONAL_CARD_COLORS.stepperBackground,
  },
  stepPressed: { backgroundColor: HB_COLORS.outlineMuted },
  input: {
    width: 64,
    height: 44,
    borderLeftWidth: 1,
    borderRightWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
    textAlign: "center",
    fontSize: 17,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
    padding: 0,
  },
  inputDisabled: { color: SEASONAL_CARD_COLORS.disabledText },
  customBlock: { gap: 2 },
  customLine: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    backgroundColor: SEASONAL_CARD_COLORS.stepperBackground,
    borderRadius: 8,
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  customLabel: { flex: 1, fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  priceBox: {
    flexDirection: "row",
    alignItems: "center",
    minHeight: 44,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: 8,
    backgroundColor: HB_COLORS.white,
    paddingHorizontal: 10,
    gap: 4,
  },
  priceBoxError: { borderColor: HB_COLORS.danger },
  currency: { color: SEASONAL_CARD_COLORS.mutedText, fontSize: 15 },
  priceInput: {
    width: 72,
    height: 44,
    fontSize: 15,
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
    padding: 0,
  },
  helper: { paddingHorizontal: 0 },
});
