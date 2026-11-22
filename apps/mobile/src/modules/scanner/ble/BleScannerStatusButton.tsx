import { useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import type { RowTone } from "@/modules/settings/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { describeBleScannerIndicator } from "./ble-scanner-presentation";
import { isBleScannerFeatureAvailable, reconnectBleScanner } from "./ble-scanner-service";
import { BleScannerSettingsSheet } from "./BleScannerSettingsSheet";
import { useBleScannerStore } from "./ble-scanner-state";

/** 与设置页状态点同色系：图标 / 文字用深色，底色用很浅的同色。 */
const TONE_COLORS: Record<RowTone, Readonly<{ fg: string; bg: string; border: string }>> = {
  success: { fg: HB_COLORS.success, bg: "#ECFDF3", border: "#ABEFC6" },
  warning: { fg: HB_COLORS.warning, bg: "#FFFAEB", border: "#FEDF89" },
  danger: { fg: HB_COLORS.danger, bg: "#FEF3F2", border: "#FECDCA" },
  neutral: { fg: HB_COLORS.textSecondary, bg: HB_COLORS.surfaceMuted, border: HB_COLORS.outlineMuted },
};

interface BleScannerStatusButtonProps {
  /**
   * pill：图标 + 状态文字（页头有空间时用）；
   * icon：40×40 纯图标（搜索行等放不下文字的地方），可重连时右下角带「↻」角标。
   */
  variant?: "pill" | "icon";
}

/**
 * 扫码页页头的「蓝牙枪」状态按钮。只有本机配对过 BLE 扫码枪、且原生模块可用（非旧原生包、非审核态）才显示。
 * 休眠 / 重连中 / 已断开：点一下直接重连，不用回设置页；其余状态点按打开扫码枪设置。
 */
export function BleScannerStatusButton({ variant = "pill" }: BleScannerStatusButtonProps) {
  const { t } = useAppTranslation("settings");
  const status = useBleScannerStore((state) => state.status);
  const [settingsVisible, setSettingsVisible] = useState(false);
  const indicator = isBleScannerFeatureAvailable() ? describeBleScannerIndicator(status) : null;
  if (!indicator) return null;

  const colors = TONE_COLORS[indicator.tone];
  const stateLabel = t(indicator.labelKey);
  const label =
    indicator.batteryLevel === null ? stateLabel : `${stateLabel} ${indicator.batteryLevel}%`;
  const canReconnect = indicator.action === "reconnect";
  const accessibilityLabel = canReconnect
    ? t("bleScanner.indicator.reconnectA11y", { state: label })
    : t("bleScanner.indicator.statusA11y", { state: label });

  const handlePress = () => {
    if (canReconnect) {
      // 连接中状态由控制器立即推送，按钮会马上变成黄色「连接中」作为反馈。
      void reconnectBleScanner().catch(() => undefined);
      return;
    }
    setSettingsVisible(true);
  };

  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={accessibilityLabel}
        accessibilityHint={canReconnect ? t("bleScanner.indicator.reconnectHint") : undefined}
        onPress={handlePress}
        // 长按任何状态都打开扫码枪设置（换枪、测试扫码等）。
        onLongPress={() => setSettingsVisible(true)}
        hitSlop={variant === "pill" ? 6 : 2}
        style={({ pressed }) => [
          variant === "pill" ? styles.pill : styles.iconButton,
          { backgroundColor: colors.bg, borderColor: colors.border },
          pressed ? styles.pressed : null,
        ]}
        testID="ble-scanner-status-button"
      >
        <Icon source={indicator.icon} size={variant === "pill" ? 16 : 20} color={colors.fg} />
        {variant === "pill" ? (
          <>
            <Text numberOfLines={1} style={[styles.pillText, { color: colors.fg }]}>
              {label}
            </Text>
            {canReconnect ? <Icon source="refresh" size={14} color={colors.fg} /> : null}
          </>
        ) : canReconnect ? (
          <View style={[styles.badge, { backgroundColor: colors.fg }]}>
            <Icon source="refresh" size={10} color={HB_COLORS.white} />
          </View>
        ) : null}
      </Pressable>
      {settingsVisible ? (
        <BleScannerSettingsSheet visible={settingsVisible} onDismiss={() => setSettingsVisible(false)} />
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  pill: {
    height: 32,
    maxWidth: 140,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingHorizontal: 10,
    borderRadius: 16,
    borderWidth: StyleSheet.hairlineWidth,
  },
  pillText: { fontSize: 12, lineHeight: 16, fontWeight: "600", flexShrink: 1 },
  iconButton: {
    width: 40,
    height: 40,
    borderRadius: 20,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: StyleSheet.hairlineWidth,
  },
  badge: {
    position: "absolute",
    right: 3,
    bottom: 3,
    width: 14,
    height: 14,
    borderRadius: 7,
    alignItems: "center",
    justifyContent: "center",
  },
  pressed: { opacity: 0.6 },
});
