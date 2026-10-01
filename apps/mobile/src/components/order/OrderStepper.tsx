import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS } from "@/shared/theme/tokens";
import { ORDER_COLORS } from "./order-ui";

interface OrderStepperProps {
  quantity: number;
  step: number;
  disabled?: boolean;
  busy?: boolean;
  compact?: boolean;
  /** 只禁止加量（如暂停供货）：服务端仍允许减量与移除。 */
  increaseDisabled?: boolean;
  /** 中间数量按钮的可访问标签；点数量打开数量编辑。 */
  accessibilityLabel: string;
  decreaseLabel: string;
  increaseLabel: string;
  removeLabel: string;
  onDecrease: () => void;
  onIncrease: () => void;
  onEdit: () => void;
}

export function OrderStepper({
  quantity,
  step,
  disabled = false,
  busy = false,
  compact = false,
  increaseDisabled = false,
  accessibilityLabel,
  decreaseLabel,
  increaseLabel,
  removeLabel,
  onDecrease,
  onIncrease,
  onEdit,
}: OrderStepperProps) {
  const locked = disabled || busy;
  // 再减一个起订量就归零时，「−」换成删除图标：归零即移出购物车，提前把后果画出来。
  const decreaseRemoves = quantity - step <= 0;
  const increaseLocked = locked || increaseDisabled;

  return (
    <View style={[styles.stepper, compact ? styles.stepperCompact : null, locked ? styles.stepperLocked : null]}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={decreaseRemoves ? removeLabel : decreaseLabel}
        disabled={locked}
        hitSlop={4}
        onPress={onDecrease}
        style={({ pressed }) => [
          styles.side,
          compact ? styles.sideCompact : null,
          decreaseRemoves && !locked ? styles.sideDanger : null,
          pressed ? styles.sidePressed : null,
        ]}
      >
        <MaterialCommunityIcons
          name={decreaseRemoves ? "trash-can-outline" : "minus"}
          size={20}
          color={locked ? HB_COLORS.outline : decreaseRemoves ? ORDER_COLORS.danger : HB_COLORS.action}
        />
      </Pressable>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={accessibilityLabel}
        disabled={locked}
        onPress={onEdit}
        style={({ pressed }) => [styles.value, busy ? styles.valueBusy : null, pressed ? styles.valuePressed : null]}
      >
        {busy ? (
          <ActivityIndicator size="small" color={HB_COLORS.white} />
        ) : (
          <Text style={styles.valueText}>{quantity}</Text>
        )}
      </Pressable>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={increaseLabel}
        disabled={increaseLocked}
        hitSlop={4}
        onPress={onIncrease}
        style={({ pressed }) => [styles.side, compact ? styles.sideCompact : null, pressed ? styles.sidePressed : null]}
      >
        <MaterialCommunityIcons name="plus" size={20} color={increaseLocked ? HB_COLORS.outline : HB_COLORS.action} />
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  stepper: {
    width: 132,
    height: 44,
    flexDirection: "row",
    alignItems: "stretch",
    borderWidth: 1,
    borderColor: ORDER_COLORS.stepperBorder,
    borderRadius: 8,
    backgroundColor: HB_COLORS.white,
    overflow: "hidden",
  },
  stepperCompact: {
    width: 124,
  },
  stepperLocked: {
    borderColor: HB_COLORS.outline,
  },
  side: {
    width: 42,
    alignItems: "center",
    justifyContent: "center",
  },
  sideCompact: {
    width: 40,
  },
  sideDanger: {
    backgroundColor: "#FEF3F2",
  },
  sidePressed: {
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  value: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: HB_COLORS.action,
  },
  valueBusy: {
    backgroundColor: "#528BFF",
  },
  valuePressed: {
    backgroundColor: ORDER_COLORS.tonalText,
  },
  valueText: {
    color: HB_COLORS.white,
    fontSize: 16,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
});
