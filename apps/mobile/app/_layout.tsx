import { useEffect, useState } from "react";
import { Stack } from "expo-router";
import Constants from "expo-constants";
import * as SplashScreen from "expo-splash-screen";
import { Image, Platform, StyleSheet, Text, View } from "react-native";
import { PaperProvider } from "react-native-paper";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { I18nextProvider } from "react-i18next";
import { QueryClientProvider } from "@tanstack/react-query";
import { StatusBar } from "expo-status-bar";
import { usePrinterAutoConnect } from "@/modules/printer/use-printer-auto-connect";
import { waitForStartupReadiness } from "@/modules/startup/startup-readiness";
import { shouldRunAutomaticAppUpdatesForProfile } from "@/modules/updates/app-build-profile";
import { IosNativeUpdateBoundary } from "@/modules/updates/IosNativeUpdateBoundary";
import { MobileOtaUpdateBoundary } from "@/modules/updates/MobileOtaUpdateBoundary";
import {
  deriveIosNativeOtaBarrier,
  shouldEnableIosNativeUpdate,
  shouldPauseAutomaticOtaForIosNativeUpdate,
} from "@/modules/updates/ios-native-app-update";
import { useAutomaticNativeAppUpdate } from "@/modules/updates/use-automatic-native-app-update";
import { NativeAppUpdateStatus } from "@/modules/updates/NativeAppUpdateStatus";
import { useIosNativeAppUpdate } from "@/modules/updates/use-ios-native-app-update";
import { useMobileOtaUpdate } from "@/modules/updates/use-mobile-ota-update";
import { i18n, initI18n } from "@/shared/i18n/i18n";
import { queryClient } from "@/shared/api/query-client";
import { installGlobalErrorLogging, reportApplicationLog } from "@/shared/logging/log-center-runtime";
import { useDeviceStore } from "@/store/device-store";
import { useAuthStore } from "@/store/auth-store";
import { NetworkRecoveryProvider } from "@/shared/network";
import { IosReviewBanner } from "@/modules/ios-review/IosReviewBanner";
import { hbLightTheme } from "@/shared/theme/theme";
import "@/modules/attendance/location-tracking";

const MIN_SPLASH_VISIBLE_MS = 900;

SplashScreen.preventAutoHideAsync().catch(() => {
  // 启动画面可能已经被系统隐藏，忽略即可，避免启动流程被异常打断。
});

SplashScreen.setOptions({
  duration: 700,
  fade: true,
});

export default function RootLayout() {
  const [appReady, setAppReady] = useState(false);
  const [startupError, setStartupError] = useState<unknown>(null);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const iosReviewOfflineGuardActive = useAuthStore(
    (state) => state.iosReviewOfflineGuardActive
  );
  const hydrateIosReviewSession = useAuthStore(
    (state) => state.hydrateIosReviewSession
  );
  const isIosReviewSession = sessionKind === "iosReview";
  const sideEffectsEnabled =
    appReady && !startupError && !iosReviewOfflineGuardActive;
  const nativeAppBuildProfile =
    Constants.expoConfig?.extra?.nativeAppBuildProfile;
  const automaticUpdatesEnabled =
    sideEffectsEnabled &&
    shouldRunAutomaticAppUpdatesForProfile(
      nativeAppBuildProfile
    );
  const iosNativeUpdateEnabled =
    sideEffectsEnabled &&
    shouldEnableIosNativeUpdate({
      platform: Platform.OS,
      buildProfile: nativeAppBuildProfile,
      isDevelopment: __DEV__,
      reviewGuardActive: iosReviewOfflineGuardActive,
    });
  const iosNativeUpdate = useIosNativeAppUpdate({
    enabled: iosNativeUpdateEnabled,
  });
  const pauseAutomaticOta = shouldPauseAutomaticOtaForIosNativeUpdate({
    enabled: iosNativeUpdateEnabled,
    initialized: iosNativeUpdate.initialized,
    state: iosNativeUpdate.decision?.state ?? null,
    optionalPromptActive: iosNativeUpdate.optionalPromptActive,
  });

  usePrinterAutoConnect({ enabled: sideEffectsEnabled });
  // Android 原生包检查先注册 effect；共享互斥会让 APK 提示优先于 OTA optional。
  const nativeAppUpdate = useAutomaticNativeAppUpdate({ enabled: automaticUpdatesEnabled });
  const mobileOtaUpdate = useMobileOtaUpdate({
    enabled: automaticUpdatesEnabled && !pauseAutomaticOta,
    beforeCheck: iosNativeUpdateEnabled
      ? async () => {
          const receipt = await iosNativeUpdate.recheck();
          // null 只表示检查被取消/失效；真实离线会携带 source=none 的 receipt 并按既定策略放行。
          return deriveIosNativeOtaBarrier(
            receipt,
            iosNativeUpdate.getCheckEpoch(),
          );
        }
      : undefined,
    getEpoch: iosNativeUpdateEnabled
      ? iosNativeUpdate.getCheckEpoch
      : undefined,
  });

  useEffect(() => {
    if (!sideEffectsEnabled) {
      return;
    }
    return installGlobalErrorLogging();
  }, [sideEffectsEnabled]);

  useEffect(() => {
    let mounted = true;

    async function prepareApp() {
      const prepareReviewAwareDeviceState = async () => {
        // 关键位置：先恢复审核 marker，避免读取并恢复普通设备绑定会话。
        await hydrateIosReviewSession();
        await useDeviceStore.getState().hydrate();
      };

      // 等待语言与设备缓存完成，避免启动页过早消失后露出空白过渡。
      const result = await waitForStartupReadiness(
        [
          initI18n(),
          prepareReviewAwareDeviceState(),
        ],
        MIN_SPLASH_VISIBLE_MS,
      );

      if (!mounted) {
        return;
      }

      if (result.ok) {
        setAppReady(true);
      } else {
        console.warn("[startup] prepare app before splash hide failed", result.error);
        const startupError = result.error instanceof Error ? result.error : new Error(String(result.error));
        reportApplicationLog({
          level: "Critical",
          message: "移动端启动准备失败",
          sourceType: "app.startup",
          exceptionType: startupError.name,
          exceptionMessage: startupError.message,
          stackTrace: startupError.stack,
        });
        setStartupError(result.error);
      }
    }

    void prepareApp();

    return () => {
      mounted = false;
    };
  }, [hydrateIosReviewSession]);

  useEffect(() => {
    if (!appReady && !startupError) {
      return;
    }

    const frame = requestAnimationFrame(() => {
      void SplashScreen.hideAsync();
    });

    return () => {
      cancelAnimationFrame(frame);
    };
  }, [appReady, startupError]);

  if (startupError) {
    return (
      <View style={styles.splashFallback}>
        <StatusBar style="dark" />
        <Image
          source={require("../assets/splash-logo.png")}
          style={styles.splashLogo}
          resizeMode="contain"
        />
        <Text style={styles.startupErrorText}>启动失败，请重新打开应用</Text>
      </View>
    );
  }

  if (!appReady) {
    return (
      <View style={styles.splashFallback}>
        <StatusBar style="dark" />
        <Image
          source={require("../assets/splash-logo.png")}
          style={styles.splashLogo}
          resizeMode="contain"
        />
      </View>
    );
  }

  return (
    <SafeAreaProvider>
      <QueryClientProvider client={queryClient}>
        <I18nextProvider i18n={i18n}>
          <PaperProvider theme={hbLightTheme}>
            {/* 浅色业务页面统一使用深色系统图标，避免白底白字看不清。 */}
            <StatusBar style="dark" />
            {/* 网络恢复补传：启动就绪且非 iOS 审核态时启用。
                离线期间的请求入队后，由 App 前台切换与指数退避自动补传。 */}
            <NetworkRecoveryProvider enabled={sideEffectsEnabled}>
              <IosNativeUpdateBoundary
                enabled={iosNativeUpdateEnabled}
                initialized={iosNativeUpdate.initialized}
                checking={iosNativeUpdate.checking}
                decision={iosNativeUpdate.decision}
                onOpenRequiredUpdate={iosNativeUpdate.openRequiredUpdate}
                onRetryRequiredUpdate={() => {
                  void iosNativeUpdate.recheck();
                }}
              >
                <MobileOtaUpdateBoundary
                  enabled={mobileOtaUpdate.enabled}
                  initialized={mobileOtaUpdate.initialized}
                  checking={mobileOtaUpdate.checking}
                  downloading={mobileOtaUpdate.downloading}
                  applying={mobileOtaUpdate.applying}
                  downloaded={mobileOtaUpdate.downloaded}
                  decision={mobileOtaUpdate.decision}
                  lastError={mobileOtaUpdate.lastError}
                  onManualCheck={mobileOtaUpdate.checkManually}
                  onDownload={() => {
                    void mobileOtaUpdate.downloadRequired();
                  }}
                  onRestart={() => {
                    void mobileOtaUpdate.restartRequired();
                  }}
                  onRetry={() => {
                    void mobileOtaUpdate.recheck();
                  }}
                >
                  <View style={styles.appContent}>
                    <NativeAppUpdateStatus
                      phase={nativeAppUpdate.phase}
                      onRetry={nativeAppUpdate.retry}
                      onDismiss={nativeAppUpdate.dismiss}
                    />
                    <Stack screenOptions={{ headerShown: false }}>
                      <Stack.Screen name="index" />
                      <Stack.Screen name="(auth)" />
                      <Stack.Screen name="(shell)" />
                    </Stack>
                    {isIosReviewSession ? <IosReviewBanner /> : null}
                  </View>
                </MobileOtaUpdateBoundary>
              </IosNativeUpdateBoundary>
            </NetworkRecoveryProvider>
          </PaperProvider>
        </I18nextProvider>
      </QueryClientProvider>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  appContent: {
    flex: 1,
  },
  splashFallback: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#F8FBFF",
  },
  splashLogo: {
    width: 210,
    height: 210,
  },
  startupErrorText: {
    marginTop: 16,
    color: "#334155",
    fontSize: 15,
    lineHeight: 22,
  },
});
