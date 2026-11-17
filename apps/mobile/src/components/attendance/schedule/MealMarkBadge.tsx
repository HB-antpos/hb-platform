// 看板卡片名字行右侧的「有无午饭」小标识：
// - 自动含午饭：与卡片文字同色系的浅灰叉勺，扣 2 次时旁边写 ×2；
// - 手动指定含午饭：同样的图标外加虚线框（与「手动＝虚线」的语言一致）；
// - 长班次无午饭：橙色斜杠叉勺 + 橙色虚线框，是要一眼看出的例外；
// - hidden（已取消 / 短班次 / 时间不合法）不渲染。
// 只负责展示：不可点、不改变卡片点击区，读屏信息由卡片的 accessibilityLabel 带上。
import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import Svg, { Path } from "react-native-svg";
import { MEAL_ICON_PATH, MEAL_NONE_TONE, MEAL_SLASH_PATH, type MealMark } from "@/modules/attendance/meal-mark";

interface MealMarkBadgeProps {
  mark: MealMark;
  /** 卡片文字色（员工色的 ink），自动 / 手动的图标与边框跟随它。 */
  tint: string;
}

export function MealMarkBadge({ mark, tint }: MealMarkBadgeProps) {
  if (mark.kind === "hidden") return null;
  const none = mark.kind === "none";
  const iconColor = none ? MEAL_NONE_TONE.text : tint;
  return (
    // 装饰性标识：读屏由卡片整体标签带出「含午饭 N 次 / 无午饭」，这里隐藏避免重复
    <View
      accessibilityElementsHidden
      importantForAccessibility="no-hide-descendants"
      style={[
        styles.badge,
        none ? styles.badgeNone : null,
        !none && mark.manual ? { borderColor: tint, borderStyle: "dashed", borderWidth: 1 } : null,
      ]}
    >
      <Svg width={11} height={11} viewBox="0 0 24 24">
        <Path
          d={MEAL_ICON_PATH}
          fill="none"
          stroke={iconColor}
          strokeWidth={2.4}
          strokeLinecap="round"
          strokeLinejoin="round"
          opacity={none ? 1 : 0.75}
        />
        {none ? <Path d={MEAL_SLASH_PATH} fill="none" stroke={MEAL_NONE_TONE.text} strokeWidth={2.6} strokeLinecap="round" /> : null}
      </Svg>
      {mark.count > 1 ? <Text style={[styles.count, { color: iconColor }]}>{`×${mark.count}`}</Text> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  badge: {
    alignItems: "center",
    backgroundColor: "rgba(255,255,255,0.72)",
    borderRadius: 3,
    flexDirection: "row",
    flexShrink: 0,
    gap: 1,
    height: 14,
    marginLeft: "auto",
    paddingHorizontal: 3,
  },
  badgeNone: {
    backgroundColor: MEAL_NONE_TONE.background,
    borderColor: MEAL_NONE_TONE.border,
    borderStyle: "dashed",
    borderWidth: 1,
  },
  count: { fontSize: 9.5, fontWeight: "700" },
});
