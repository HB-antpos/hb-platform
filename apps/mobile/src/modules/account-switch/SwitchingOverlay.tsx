import { useEffect } from "react";
import { ActivityIndicator, BackHandler, StyleSheet, Text, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { resolveAvatarInitial } from "@/modules/settings/settings-summary";
import { SWITCH_STAGE_COUNT, useAccountSwitchStore } from "./account-switch-store";

const STEP_KEYS = ["overlay.stepVerify", "overlay.stepClear", "overlay.stepLoad", "overlay.stepFinish"] as const;

/**
 * 切换账号时的全屏遮罩：四步进度，期间挡住所有触摸与安卓返回键。
 * 挂在外壳布局的最上层；用自绘视图而不是原生 Modal，避免盖不住 Paper 弹窗或被系统忽略。
 */
export function SwitchingOverlay() {
  const { t } = useAppTranslation("accountSwitch");
  const switching = useAccountSwitchStore((state) => state.switching);
  const label = useAccountSwitchStore((state) => state.targetLabel);
  const stage = useAccountSwitchStore((state) => state.stage);

  useEffect(() => {
    if (!switching) return undefined;
    const subscription = BackHandler.addEventListener("hardwareBackPress", () => true);
    return () => subscription.remove();
  }, [switching]);

  if (!switching) return null;

  return (
    <View
      style={styles.overlay}
      testID="account-switching-overlay"
      accessibilityViewIsModal
      accessibilityLiveRegion="polite"
      // 吃掉所有触摸，切换期间不允许操作下面的页面。
      onStartShouldSetResponder={() => true}
    >
      <View style={styles.avatar}>
        {label ? (
          <Text style={styles.avatarText}>{resolveAvatarInitial(label)}</Text>
        ) : (
          <MaterialCommunityIcons name="account-switch-outline" size={30} color={HB_COLORS.action} />
        )}
      </View>
      <Text style={styles.title} maxFontSizeMultiplier={1.4}>
        {label ? t("overlay.title", { name: label }) : t("overlay.titleGeneric")}
      </Text>
      <View style={styles.steps}>
        {STEP_KEYS.slice(0, SWITCH_STAGE_COUNT).map((key, index) => {
          const done = index < stage;
          const active = index === stage;
          return (
            <View key={key} style={styles.step}>
              <View style={styles.stepMark}>
                {done ? (
                  <MaterialCommunityIcons name="check-circle" size={20} color={HB_COLORS.success} />
                ) : active ? (
                  <ActivityIndicator size="small" color={HB_COLORS.action} />
                ) : (
                  <MaterialCommunityIcons name="circle-outline" size={18} color={HB_COLORS.outline} />
                )}
              </View>
              <Text
                style={[styles.stepText, done && styles.stepDone, active && styles.stepActive]}
                maxFontSizeMultiplier={1.4}
              >
                {t(key)}
              </Text>
            </View>
          );
        })}
      </View>
      <Text style={styles.hint} maxFontSizeMultiplier={1.4}>
        {t("overlay.hint")}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  overlay: {
    ...StyleSheet.absoluteFillObject,
    zIndex: 1000,
    elevation: 1000,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 32,
    gap: 14,
    backgroundColor: "rgba(255,255,255,0.97)",
  },
  avatar: {
    width: 56,
    height: 56,
    borderRadius: 28,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#EEF4FF",
  },
  avatarText: { fontSize: 22, fontWeight: "800", color: HB_COLORS.action },
  title: { fontSize: 19, fontWeight: "800", color: HB_COLORS.textPrimary, textAlign: "center" },
  steps: { alignSelf: "stretch", gap: 10, marginTop: 4 },
  step: { flexDirection: "row", alignItems: "flex-start", gap: 10 },
  stepMark: { width: 22, alignItems: "center", paddingTop: 1 },
  stepText: { flex: 1, fontSize: 14, lineHeight: 20, color: HB_COLORS.textSecondary },
  stepDone: { color: HB_COLORS.textPrimary },
  stepActive: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  hint: { fontSize: 12.5, color: HB_COLORS.textSecondary, textAlign: "center", marginTop: 6 },
});
