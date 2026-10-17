import { StyleSheet, View } from "react-native";
import { Icon } from "react-native-paper";
import { POS_PERMISSION_COLORS } from "@/modules/users/pos-permission-sheet";
import type { PosPermissionSelectionState } from "@/modules/users/pos-terminal-permission-presentation";
import { HB_COLORS } from "@/shared/theme/tokens";

export type PosCheckboxTone = "default" | "danger";

/**
 * 纯展示的勾选框（全选 / 半选 / 未选），设计稿里折扣矩阵的高风险列要红色，
 * 所以不用 Paper 的 Checkbox。交互由外层 Pressable 承担，避免嵌套可点击控件。
 */
export function PosPermissionCheckbox({
  state,
  tone = "default",
  disabled = false,
  size = 22,
}: {
  state: PosPermissionSelectionState;
  tone?: PosCheckboxTone;
  disabled?: boolean;
  size?: number;
}) {
  const danger = tone === "danger";
  const accent = disabled
    ? POS_PERMISSION_COLORS.disabledText
    : danger
      ? HB_COLORS.danger
      : HB_COLORS.brand;
  const active = state !== "none";
  const idleBorder = danger && !disabled ? POS_PERMISSION_COLORS.dangerBorder : HB_COLORS.outline;

  return (
    <View
      style={[
        styles.box,
        { width: size, height: size },
        active ? { backgroundColor: accent, borderColor: accent } : { borderColor: idleBorder },
      ]}
    >
      {state === "all" ? <Icon source="check" size={size - 6} color={HB_COLORS.white} /> : null}
      {state === "partial" ? <Icon source="minus" size={size - 6} color={HB_COLORS.white} /> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  box: {
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 6,
    borderWidth: 1.5,
    backgroundColor: HB_COLORS.white,
  },
});
