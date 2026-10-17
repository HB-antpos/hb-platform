import { Platform, StyleSheet, View } from "react-native";
import { Icon } from "react-native-paper";
import { HB_COLORS } from "@/shared/theme/tokens";

/** 权限码 / 角色码等需要等宽显示的文字（Android 没有 Menlo，用系统等宽字体）。 */
export const ADMIN_MONO_FONT = Platform.select({ ios: "Menlo", default: "monospace" });

/**
 * 设计稿 A6 的方形勾选框：整行由外层 Pressable 负责点击与无障碍（accessibilityRole="checkbox"），
 * 这里只负责画出「未选 / 已选 / 锁定 / 不可改」四种外观，所以对读屏隐藏。
 */
export function AdminCheckBox({
  checked,
  locked = false,
  disabled = false,
}: {
  checked: boolean;
  /** 锁定：始终视为已选且不可改（如管理员角色默认拥有全部权限），显示灰色锁。 */
  locked?: boolean;
  /** 当前账号没有修改权限 / 正在保存：保持原勾选状态，整体变淡。 */
  disabled?: boolean;
}) {
  return (
    <View
      accessible={false}
      importantForAccessibility="no-hide-descendants"
      style={[
        styles.box,
        checked && styles.boxChecked,
        locked && styles.boxLocked,
        disabled && !locked && styles.boxDisabled,
      ]}
    >
      {locked ? (
        <Icon source="lock" size={13} color="#98A2B3" />
      ) : checked ? (
        <Icon source="check" size={16} color={HB_COLORS.white} />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  box: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: 6,
    borderWidth: 1.5,
    height: 22,
    justifyContent: "center",
    width: 22,
  },
  boxChecked: { backgroundColor: HB_COLORS.brand, borderColor: HB_COLORS.brand },
  boxLocked: { backgroundColor: "#EAECF0", borderColor: "#D0D5DD" },
  boxDisabled: { opacity: 0.55 },
});
