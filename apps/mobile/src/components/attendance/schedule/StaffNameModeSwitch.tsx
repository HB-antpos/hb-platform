// 排班看板的「员工名」显示开关：胶囊轨道里直接写当前显示什么——「昵称」（蓝，默认）/「全名」（灰），
// 圆钮在另一侧。和 Web 工具栏里的开关同一个设计，一眼看出当前状态，不用猜「开」是什么意思。
// 只负责展示与回调；偏好的读写在 ScheduleManagementCard。
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import type { StaffNameMode } from "@/modules/attendance/staff-display-name";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

interface StaffNameModeSwitchProps {
  mode: StaffNameMode;
  onChange: (mode: StaffNameMode) => void;
  /** 横屏 7 列看板：纵向空间紧，容器矮一些（点击区靠 hitSlop 补足）。 */
  compact?: boolean;
}

export function StaffNameModeSwitch({ mode, onChange, compact }: StaffNameModeSwitchProps) {
  const { t } = useAppTranslation(["attendance"]);
  const nickname = mode === "nickname";
  const modeLabel = t(nickname ? "scheduleManagement.board.nameMode.nickname" : "scheduleManagement.board.nameMode.full");

  return (
    <View style={styles.wrap}>
      <Text style={styles.label} numberOfLines={1}>
        {t("scheduleManagement.board.nameMode.label")}
      </Text>
      {/* 外层 Pressable 就是点击区：竖屏 44pt 高；横屏紧凑版 32pt 高 + hitSlop，纵向仍有约 44pt 可点 */}
      <Pressable
        accessibilityRole="switch"
        accessibilityLabel={t("scheduleManagement.board.nameMode.a11y")}
        accessibilityState={{ checked: nickname }}
        accessibilityValue={{ text: modeLabel }}
        accessibilityHint={t("scheduleManagement.board.nameMode.hint")}
        hitSlop={compact ? { top: 6, bottom: 6, left: 4, right: 4 } : { left: 4, right: 4 }}
        onPress={() => onChange(nickname ? "full" : "nickname")}
        style={[styles.touch, compact ? styles.touchCompact : null]}
      >
        <View style={[styles.track, nickname ? styles.trackOn : styles.trackOff]}>
          {/* 昵称：文字在左、圆钮在右；全名：圆钮在左、文字在右 */}
          {nickname ? <Text style={styles.trackText}>{modeLabel}</Text> : null}
          <View style={styles.knob} />
          {!nickname ? <Text style={styles.trackText}>{modeLabel}</Text> : null}
        </View>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { flexDirection: "row", alignItems: "center", gap: 6, flexShrink: 0 },
  label: { fontSize: 12, color: HB_COLORS.textSecondary },
  touch: { minHeight: 44, justifyContent: "center" },
  touchCompact: { minHeight: 32 },
  track: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 6,
    minWidth: 64,
    height: 28,
    borderRadius: 14,
    paddingHorizontal: 3,
  },
  // 开用 action 蓝、关用深灰：白字对比度都在 6 以上，12pt 小字也读得清
  trackOn: { backgroundColor: HB_COLORS.action, paddingLeft: 9 },
  trackOff: { backgroundColor: HB_COLORS.textSecondary, paddingRight: 9 },
  trackText: { fontSize: 12, fontWeight: "700", color: HB_COLORS.white },
  knob: { width: 22, height: 22, borderRadius: 11, backgroundColor: HB_COLORS.white },
});
