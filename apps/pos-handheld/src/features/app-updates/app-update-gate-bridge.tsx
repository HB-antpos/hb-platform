import { type Href, usePathname, useRouter } from "expo-router";
import {
  useCallback,
  useEffect,
  useState,
  useSyncExternalStore,
} from "react";
import { useTranslation } from "react-i18next";
import {
  StyleSheet,
  Text,
  View,
} from "react-native";

import {
  isAndroidInstallPermissionRequiredError,
  type AndroidInstallPermissionStatus,
} from "./android-native-update-adapter";
import { ApkDownloadProgressBar } from "./apk-download-progress-bar";
import {
  resolveAppUpdateCopy,
  type AppUpdateCopyKey,
} from "./app-update-copy";
import type {
  AppUpdateDownloadProgress,
  AppUpdateOrchestrator,
  AppUpdatePresentation,
} from "./app-update-orchestrator";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { HandheldStateSurface } from "@/ui/handheld/handheld-design-states";
import { posColors } from "@/ui/theme";

const HIDDEN_PRESENTATION: AppUpdatePresentation = Object.freeze({
  key: "runtime-unavailable",
  kind: "none",
  requirement: null,
  phase: "hidden",
  blocking: false,
  releaseMessage: null,
  platform: null,
  appStoreUrl: null,
  downloadUrl: null,
});

const RECOVERY_ACCESS_PATHS = new Set([
  "/registration",
  "/update-recovery",
]);

export function AppUpdateGateBridge() {
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const pathname = usePathname();
  const router = useRouter();
  const updates = runtime.services?.appUpdates ?? null;
  const presentation = useUpdatePresentation(updates);
  const latestDownloadProgress = useDownloadProgress(updates);
  // 只展示当前下载目标的进度；旧原生包没有进度事件时保持原有文案。
  const downloadProgress =
    presentation.downloadState === "downloading" &&
    latestDownloadProgress !== null &&
    latestDownloadProgress.targetKey === presentation.downloadTargetKey
      ? latestDownloadProgress
      : null;
  const downloadProgressBar = downloadProgress ? (
    <ApkDownloadProgressBar progress={downloadProgress} />
  ) : null;
  const copy = resolveAppUpdateCopy(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const [dismissedKey, setDismissedKey] = useState<string | null>(null);
  const [working, setWorking] = useState(false);
  const [errorKey, setErrorKey] =
    useState<AppUpdateCopyKey | null>(null);
  const [androidInstallPermission, setAndroidInstallPermission] = useState<
    AndroidInstallPermissionStatus | null
  >(null);

  useEffect(() => {
    if (!updates || presentation.phase !== "waiting-for-safe") {
      return undefined;
    }
    const check = () => {
      void updates.refreshSafety().catch(() => {
        // 未能证明交易安全时继续保留恢复页面，下一轮再核验。
      });
    };
    const timer = setInterval(check, 1_000);
    return () => clearInterval(timer);
  }, [presentation.key, presentation.phase, updates]);

  useEffect(() => {
    setWorking(false);
    setErrorKey(null);
  }, [presentation.key]);

  useEffect(() => {
    if (presentation.downloadState === "ready") {
      setDismissedKey((current) =>
        current?.endsWith(":pending") ? null : current,
      );
    }
  }, [presentation.downloadState, presentation.downloadTargetKey, presentation.key]);

  useEffect(() => {
    let active = true;
    setAndroidInstallPermission(null);
    if (
      !updates ||
      presentation.kind !== "native" ||
      presentation.platform !== "Android"
    ) {
      return () => {
        active = false;
      };
    }
    void updates
      .getAndroidInstallPermissionStatus()
      .then((status) => {
        if (active) setAndroidInstallPermission(status);
      })
      .catch(() => {
        // 查询失败不伪造授权状态；用户仍可选择正常安装并获得原生错误契约。
        if (active) setAndroidInstallPermission(null);
      });
    return () => {
      active = false;
    };
  }, [presentation, updates]);

  const performUpdate = useCallback(async () => {
    if (!updates || working) return;
    setWorking(true);
    setErrorKey(null);
    try {
      if (androidInstallPermission === "denied") {
        await updates.openAndroidInstallPermissionSettings();
        return;
      }
      const result = await updates.performSelectedUpdate();
      if (
        result.action === "open-app-store" ||
        result.action === "install-android-apk"
      ) {
        return;
      }
      if (result.action === "blocked") {
        setErrorKey("error.notSafe");
        return;
      }
      if (
        result.action !== "ota" ||
        result.result.state !== "reloaded"
      ) {
        setErrorKey("error.unavailable");
      }
    } catch (error) {
      if (isAndroidInstallPermissionRequiredError(error)) {
        // 授权在点击后的竞态窗口被撤销时，切换为显式恢复动作，不把它误报为下载失败。
        setAndroidInstallPermission("denied");
        return;
      }
      setErrorKey("error.unavailable");
    } finally {
      setWorking(false);
    }
  }, [androidInstallPermission, updates, working]);

  const dismissalTargetKey = presentation.downloadTargetKey ?? presentation.key;
  const dismissalKey = `${dismissalTargetKey}:${presentation.downloadState === "ready" ? "ready" : presentation.downloadState ? "pending" : "store"}`;
  const downloading = presentation.downloadState === "idle" ||
    presentation.downloadState === "downloading";
  const downloadFailed = presentation.downloadState === "failed";
  if ((downloading || downloadFailed) && presentation.requirement === "optional" && !presentation.blocking && dismissedKey !== dismissalKey) {
    return (
      <View pointerEvents="box-none" style={styles.optionalLayer} testID="app-update-download-status">
        <View style={[styles.card, styles.downloadCard]}>
          <Text accessibilityLiveRegion="polite" style={styles.body}>
            {copy[downloadFailed ? "download.failed" : "download.running"]}
          </Text>
          {downloadProgressBar}
          {downloadFailed ? (
            <PosPressable accessibilityRole="button" testID="app-update-download-retry"
              style={styles.secondaryButton} onPress={() => { void updates?.prepareSelectedUpdate(); }}>
              <Text style={styles.secondaryButtonText}>{copy["action.retry"]}</Text>
            </PosPressable>
          ) : null}
          <PosPressable
            accessibilityRole="button"
            testID="app-update-dismiss"
            style={styles.secondaryButton}
            onPress={() => setDismissedKey(dismissalKey)}
          >
            <Text style={styles.secondaryButtonText}>{copy["action.later"]}</Text>
          </PosPressable>
        </View>
      </View>
    );
  }
  if (
    presentation.phase === "hidden" ||
    presentation.phase === "unchecked" ||
    presentation.phase === "waiting-for-safe"
  ) {
    return null;
  }
  if (
    presentation.requirement === "optional" &&
    dismissedKey === dismissalKey
  ) {
    return null;
  }

  const required = presentation.requirement === "required";
  const preserveRecoveryAccess =
    required && RECOVERY_ACCESS_PATHS.has(normalizePathname(pathname));
  const titleKey: AppUpdateCopyKey =
    downloadFailed ? "download.failed"
      : downloading ? "download.required"
      : presentation.downloadState === "ready" && !required
      ? presentation.kind === "native" ? "download.apkReady" : "download.otaReady"
      : presentation.kind === "native"
      ? required
        ? "required.nativeTitle"
        : "optional.nativeTitle"
      : required
        ? "required.otaTitle"
        : "optional.otaTitle";
  const actionKey: AppUpdateCopyKey =
    presentation.kind === "native"
      ? presentation.platform === "Android"
        ? androidInstallPermission === "denied"
          ? "action.openInstallSettings"
          : "action.installOta"
        : "action.openStore"
      : "action.restart";

  const gateCard = (
    <View
      accessibilityRole="alert"
      style={[
        styles.card,
        preserveRecoveryAccess
          ? styles.recoveryAccessCard
          : required
            ? styles.requiredCard
            : styles.optionalCard,
      ]}
    >
      <View style={styles.accent} />
      <Text style={styles.eyebrow}>
        {copy[required ? "eyebrow.required" : "eyebrow.optional"]}
      </Text>
      <Text style={styles.title}>{copy[titleKey]}</Text>
      <Text style={styles.body}>
        {androidInstallPermission === "denied"
          ? copy["permission.required"]
          : presentation.releaseMessage ??
          copy[required ? "required.body" : presentation.downloadState === "ready"
            ? "download.readyBody" : "optional.body"]}
      </Text>
      {downloadProgressBar}
      {errorKey ? (
        <Text accessibilityRole="alert" style={styles.error}>
          {copy[errorKey]}
        </Text>
      ) : null}
      <View style={styles.actions}>
        {required && !preserveRecoveryAccess ? (
          <>
            <PosPressable
              accessibilityRole="button"
              onPress={() =>
                router.push(
                  "/update-recovery?section=settings" as Href,
                )
              }
              sound="navigate"
              style={styles.secondaryButton}
              testID="app-update-settings-entry"
            >
              <Text style={styles.secondaryButtonText}>
                {copy["action.settings"]}
              </Text>
            </PosPressable>
            <PosPressable
              accessibilityRole="button"
              onPress={() =>
                router.push(
                  "/update-recovery?section=support" as Href,
                )
              }
              sound="navigate"
              style={styles.secondaryButton}
              testID="app-update-support-entry"
            >
              <Text style={styles.secondaryButtonText}>
                {copy["action.support"]}
              </Text>
            </PosPressable>
            <PosPressable
              accessibilityRole="button"
              onPress={() => router.push("/registration" as Href)}
              sound="navigate"
              style={styles.secondaryButton}
              testID="app-update-registration-entry"
            >
              <Text style={styles.secondaryButtonText}>
                {copy["action.registration"]}
              </Text>
            </PosPressable>
          </>
        ) : null}
        {!required ? (
          <PosPressable
            accessibilityRole="button"
            onPress={() => setDismissedKey(dismissalKey)}
            sound="navigate"
            style={styles.secondaryButton}
            testID="app-update-dismiss"
          >
            <Text style={styles.secondaryButtonText}>
              {copy["action.later"]}
            </Text>
          </PosPressable>
        ) : null}
        {downloading ? null : downloadFailed ? (
          <PosPressable accessibilityRole="button" testID="app-update-download-retry"
            style={styles.primaryButton} onPress={() => { void updates?.prepareSelectedUpdate(); }}>
            <Text style={styles.primaryButtonText}>{copy["action.retry"]}</Text>
          </PosPressable>
        ) : <PosPressable
          accessibilityRole="button"
          disabled={working}
          onPress={performUpdate}
          style={[
            styles.primaryButton,
            working ? styles.disabledButton : null,
          ]}
          testID="app-update-action"
        >
          <Text style={styles.primaryButtonText}>
            {copy[working ? "action.working" : actionKey]}
          </Text>
        </PosPressable>}
      </View>
    </View>
  );

  return (
    <View
      accessibilityViewIsModal={required && !preserveRecoveryAccess}
      pointerEvents={
        preserveRecoveryAccess
          ? "box-none"
          : required
            ? "auto"
            : "box-none"
      }
      style={
        preserveRecoveryAccess
          ? styles.recoveryAccessLayer
          : required
          ? styles.blockingLayer
          : styles.optionalLayer
      }
      testID={
        preserveRecoveryAccess
          ? "app-update-recovery-access"
          : required
          ? "app-update-blocking-gate"
          : "app-update-optional-prompt"
      }
    >
      {required ? (
        <HandheldStateSurface
          slug="required-update"
          style={styles.stateSurface}
        >
          {gateCard}
        </HandheldStateSurface>
      ) : (
        gateCard
      )}
    </View>
  );
}

function normalizePathname(pathname: string): string {
  const normalized = pathname.replace(/\/+$/u, "");
  return normalized || "/";
}

function useUpdatePresentation(
  updates: AppUpdateOrchestrator | null,
): AppUpdatePresentation {
  const subscribe = useCallback(
    (listener: () => void) =>
      updates?.subscribePresentation(listener) ?? (() => undefined),
    [updates],
  );
  const getSnapshot = useCallback(
    () => updates?.getPresentation() ?? HIDDEN_PRESENTATION,
    [updates],
  );
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

function useDownloadProgress(
  updates: AppUpdateOrchestrator | null,
): AppUpdateDownloadProgress | null {
  const subscribe = useCallback(
    (listener: () => void) =>
      updates?.subscribeDownloadProgress(listener) ?? (() => undefined),
    [updates],
  );
  const getSnapshot = useCallback(
    () => updates?.getDownloadProgress() ?? null,
    [updates],
  );
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

const styles = StyleSheet.create({
  blockingLayer: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "center",
    backgroundColor: posColors.canvas,
    elevation: 50,
    justifyContent: "center",
    padding: 16,
    zIndex: 1_000,
  },
  optionalLayer: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "flex-end",
    justifyContent: "flex-start",
    padding: 16,
    zIndex: 900,
  },
  recoveryAccessLayer: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "flex-end",
    justifyContent: "flex-start",
    padding: 16,
    zIndex: 900,
  },
  card: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 6,
    maxWidth: 560,
    overflow: "hidden",
    padding: 16,
    width: "100%",
  },
  requiredCard: {
    borderColor: posColors.orange,
    justifyContent: "center",
  },
  optionalCard: {
    maxWidth: 480,
  },
  downloadCard: {
    paddingVertical: 8,
    gap: 8,
  },
  recoveryAccessCard: {
    maxWidth: 420,
    minHeight: 0,
  },
  stateSurface: {
    alignItems: "center",
    justifyContent: "center",
    width: "100%",
  },
  accent: {
    backgroundColor: posColors.orange,
    height: 5,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  eyebrow: {
    color: posColors.orange,
    fontSize: 13,
    fontWeight: "800",
    letterSpacing: 1.3,
    marginBottom: 10,
  },
  title: {
    color: posColors.ink,
    fontSize: 24,
    fontWeight: "800",
    lineHeight: 30,
  },
  body: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 8,
  },
  error: {
    backgroundColor: posColors.redSoft,
    color: posColors.red,
    fontSize: 15,
    lineHeight: 21,
    marginTop: 16,
    paddingHorizontal: 14,
    paddingVertical: 11,
  },
  actions: {
    gap: 8,
    marginTop: 16,
  },
  primaryButton: {
    alignItems: "center",
    backgroundColor: posColors.orange,
    justifyContent: "center",
    minHeight: 48,
    borderRadius: 6,
    width: "100%",
    paddingHorizontal: 16,
  },
  secondaryButton: {
    alignItems: "center",
    borderColor: posColors.border,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 48,
    borderRadius: 6,
    width: "100%",
    paddingHorizontal: 16,
  },
  disabledButton: {
    opacity: 0.58,
  },
  primaryButtonText: {
    color: "#FFFFFF",
    fontSize: 16,
    fontWeight: "800",
  },
  secondaryButtonText: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "700",
  },
});
