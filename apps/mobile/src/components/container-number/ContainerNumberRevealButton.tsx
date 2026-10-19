import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Pressable, StyleSheet } from "react-native";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { useContainerNumberDisplay } from "./use-container-number-display";

interface ContainerNumberRevealButtonProps {
  size?: number;
  color?: string;
}

/**
 * 货柜编号旁的眼睛按钮：只有「查看完整货柜编号」权限（仓库管理、管理员）才渲染。
 * 点击切换全 App 货柜编号的完整 / 遮罩显示。
 */
export function ContainerNumberRevealButton({ size = 20, color = HB_COLORS.action }: ContainerNumberRevealButtonProps) {
  const { t } = useAppTranslation("common");
  const { canReveal, showFull, toggle } = useContainerNumberDisplay();
  if (!canReveal) return null;

  const label = showFull ? t("containerNumber.hideFull") : t("containerNumber.showFull");
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ selected: showFull }}
      hitSlop={8}
      onPress={toggle}
      style={styles.button}
    >
      <MaterialCommunityIcons name={showFull ? "eye-off-outline" : "eye-outline"} size={size} color={color} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: {
    paddingHorizontal: 4,
    justifyContent: "center",
    alignItems: "center",
  },
});
