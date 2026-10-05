import type { ReactNode } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Surface, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  getAndroidNativeUpdateBoundaryMode,
  type AndroidNativeUpdateDecision,
} from "./android-native-required-update";
import type { NativeAppUpdatePhase } from "./native-app-update";

type AndroidNativeUpdateBoundaryProps = {
  enabled: boolean;
  decision: AndroidNativeUpdateDecision | null;
  phase: NativeAppUpdatePhase | "failed" | null;
  readyToInstall: boolean;
  onInstall: () => void;
  onRetry: () => void;
  children: ReactNode;
};

/**
 * 安卓原生强制更新拦截页：低于最低支持构建号时整页替换（含登录页），
 * 只提供「立即安装 / 重试」，没有「稍后」。安装包下载与校验沿用原生更新通道。
 */
export function AndroidNativeUpdateBoundary({
  enabled,
  decision,
  phase,
  readyToInstall,
  onInstall,
  onRetry,
  children,
}: AndroidNativeUpdateBoundaryProps) {
  const { t } = useAppTranslation("settings");
  const mode = getAndroidNativeUpdateBoundaryMode({ enabled, decision });

  if (mode === "content") {
    return children;
  }

  const busy = phase === "checking" || phase === "downloading" || phase === "verifying";
  const statusText = phase === "failed"
    ? t("dialogs.androidNativeUpdateFailed")
    : busy
      ? t(phase === "checking"
        ? "dialogs.nativeUpdateChecking"
        : phase === "downloading"
          ? "dialogs.nativeUpdateDownloading"
          : "dialogs.nativeUpdateVerifying")
      : readyToInstall
        ? t("dialogs.androidNativeUpdateReady")
        : t("dialogs.androidNativeUpdatePreparing");
  const versionText = decision?.latestVersion
    ? decision.latestBuildNumber
      ? `${decision.latestVersion} (${decision.latestBuildNumber})`
      : decision.latestVersion
    : null;

  return (
    <SafeAreaView style={styles.screen} accessibilityViewIsModal>
      <ScrollView
        contentContainerStyle={styles.scrollContent}
        bounces={false}
        keyboardShouldPersistTaps="handled"
      >
        <Surface style={styles.panel} elevation={1}>
          <Text variant="labelLarge" style={styles.eyebrow}>
            {t("dialogs.androidNativeUpdateRequiredEyebrow")}
          </Text>
          <Text variant="headlineMedium" style={styles.title} accessibilityRole="header">
            {t("dialogs.androidNativeUpdateRequiredTitle")}
          </Text>
          {versionText ? (
            <View style={styles.versionBadge}>
              <Text variant="labelLarge" style={styles.versionText}>
                {t("dialogs.androidNativeUpdateVersion", { version: versionText })}
              </Text>
            </View>
          ) : null}
          <Text variant="bodyLarge" style={styles.message}>
            {decision?.releaseMessage || t("dialogs.androidNativeUpdateRequiredMessage")}
          </Text>
          <View style={styles.status} accessibilityLiveRegion="polite">
            {busy ? <ActivityIndicator size="small" /> : null}
            <Text
              variant="bodyMedium"
              style={phase === "failed" ? styles.statusFailed : styles.statusText}
            >
              {statusText}
            </Text>
          </View>
          <Button
            mode="contained"
            icon="download"
            onPress={onInstall}
            disabled={!readyToInstall || busy}
            contentStyle={styles.primaryActionContent}
            accessibilityLabel={t("dialogs.nativeUpdateInstallAction")}
          >
            {t("dialogs.nativeUpdateInstallAction")}
          </Button>
          <Button
            mode="outlined"
            icon="refresh"
            loading={busy}
            disabled={busy}
            onPress={onRetry}
            contentStyle={styles.secondaryActionContent}
            accessibilityLabel={t("dialogs.nativeUpdateRetryAction")}
          >
            {t("dialogs.nativeUpdateRetryAction")}
          </Button>
          <Text variant="bodySmall" style={styles.helper}>
            {t("dialogs.androidNativeUpdateRequiredHelper")}
          </Text>
        </Surface>
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
    backgroundColor: "#F8FBFF",
  },
  scrollContent: {
    flexGrow: 1,
    justifyContent: "center",
    paddingHorizontal: 20,
    paddingVertical: 28,
  },
  panel: {
    width: "100%",
    maxWidth: 520,
    alignSelf: "center",
    gap: 16,
    borderRadius: 16,
    backgroundColor: "#FFFFFF",
    paddingHorizontal: 24,
    paddingVertical: 28,
  },
  eyebrow: {
    color: "#1677FF",
    fontWeight: "700",
    letterSpacing: 0.4,
  },
  title: {
    color: "#0F172A",
    fontWeight: "700",
  },
  versionBadge: {
    alignSelf: "flex-start",
    borderRadius: 8,
    backgroundColor: "#E6F4FF",
    paddingHorizontal: 12,
    paddingVertical: 7,
  },
  versionText: {
    color: "#0958D9",
  },
  message: {
    color: "#334155",
    lineHeight: 26,
  },
  status: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  statusText: {
    flex: 1,
    color: "#475569",
  },
  statusFailed: {
    flex: 1,
    color: "#B42318",
  },
  primaryActionContent: {
    minHeight: 48,
  },
  secondaryActionContent: {
    minHeight: 44,
  },
  helper: {
    color: "#64748B",
    lineHeight: 19,
  },
});
