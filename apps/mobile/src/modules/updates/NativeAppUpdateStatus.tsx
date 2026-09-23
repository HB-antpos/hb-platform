import React from "react";
import { StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import type { NativeAppUpdatePhase } from "./native-app-update";

type Props = {
  phase: NativeAppUpdatePhase | "failed" | null;
  onRetry: () => void;
  onDismiss: () => void;
};

const phaseKeys = {
  checking: "dialogs.nativeUpdateChecking",
  downloading: "dialogs.nativeUpdateDownloading",
  verifying: "dialogs.nativeUpdateVerifying",
  failed: "dialogs.nativeUpdateFailed",
} as const;

/** APK 为后台可选更新：状态条跟随根布局，路由切换时仍可见，不阻挡登录和业务操作。 */
export function NativeAppUpdateStatus({ phase, onRetry, onDismiss }: Props) {
  const { t } = useAppTranslation("settings");
  if (!phase) return null;
  const failed = phase === "failed";
  return (
    <SafeAreaView edges={["top", "left", "right"]} style={styles.container}>
      <View style={styles.content} accessibilityLiveRegion="polite">
        <View style={styles.row}>
          {!failed ? <ActivityIndicator size="small" color={HB_COLORS.action} /> : null}
          <View style={styles.copy}>
            <Text variant="titleSmall" style={failed ? styles.error : styles.title}>
              {t(phaseKeys[phase])}
            </Text>
            <Text variant="bodySmall" style={styles.helper}>
              {t(failed ? "dialogs.nativeUpdateFailedHelper" : "dialogs.nativeUpdateBackgroundHelper")}
            </Text>
          </View>
        </View>
        {failed ? (
          <View style={styles.actions}>
            <Button compact onPress={onDismiss}>{t("dialogs.nativeUpdateDismissAction")}</Button>
            <Button compact onPress={onRetry}>{t("dialogs.nativeUpdateRetryAction")}</Button>
          </View>
        ) : null}
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    backgroundColor: HB_COLORS.surface,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  content: { paddingHorizontal: 16, paddingVertical: 12, gap: 4 },
  row: { flexDirection: "row", alignItems: "center", gap: 12 },
  copy: { flex: 1, gap: 4 },
  title: { color: HB_COLORS.textPrimary },
  error: { color: HB_COLORS.danger },
  helper: { color: HB_COLORS.textSecondary },
  actions: { flexDirection: "row", justifyContent: "flex-end", flexWrap: "wrap", gap: 8 },
});
