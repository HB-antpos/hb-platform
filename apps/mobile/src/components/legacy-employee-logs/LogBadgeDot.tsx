import { StyleSheet, View, type StyleProp, type ViewStyle } from "react-native";
import { LOG_BADGE_COLORS } from "@/modules/legacy-employee-logs/badge";
import type { LogBadgeLevel } from "@/modules/legacy-employee-logs/types";

interface LogBadgeDotProps {
  level: LogBadgeLevel;
  /** 描边色：与所在背景同色，圆点压在图标角上时才不糊在一起（底栏 / 工作台白底都用白色）。 */
  borderColor: string;
  /** 定位由调用方给（图标右上角、分组头行内等）。 */
  style?: StyleProp<ViewStyle>;
}

/**
 * 员工操作日志角标圆点（红 / 橙，约 10px，无数字）。
 * 自身不带无障碍标签：颜色不能作为唯一信息，由父级 Pressable 的 accessibilityLabel 读出
 * 「有危险操作待核查 / 有异常待核查」，这里对读屏隐藏，避免重复朗读。
 */
export function LogBadgeDot({ level, borderColor, style }: LogBadgeDotProps) {
  return (
    <View
      pointerEvents="none"
      accessibilityElementsHidden
      importantForAccessibility="no-hide-descendants"
      style={[styles.dot, { backgroundColor: LOG_BADGE_COLORS[level], borderColor }, style]}
    />
  );
}

const styles = StyleSheet.create({
  // 内径 7 + 1.5 描边 ×2 = 10px。
  dot: {
    width: 10,
    height: 10,
    borderRadius: 5,
    borderWidth: 1.5,
  },
});
