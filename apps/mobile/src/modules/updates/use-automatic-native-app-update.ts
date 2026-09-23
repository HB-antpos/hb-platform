import { useEffect, useRef, useState } from "react";
import { Alert, AppState, Platform, type AppStateStatus } from "react-native";
import { toByteArray } from "base64-js";
import { i18n } from "@/shared/i18n/i18n";
import HBAppInstaller, {
  type HBAppInstallerNativeModule,
} from "../../../modules/hb-app-installer/src/HBAppInstallerModule";
import {
  appUpdateMutualExclusion,
  createUpdateLaneRetryGate,
} from "./app-update-mutual-exclusion";
import { useForegroundUpdateCheckInterval } from "./foreground-update-interval";
import {
  checkAndDownloadNativeAppUpdate,
  getBuildBoundNativeAppDownloadUrl,
  type NativeAppBuildInfo,
  type NativeAppUpdatePhase,
} from "./native-app-update";

const APK_MIME_TYPE = "application/vnd.android.package-archive";
const FLAG_GRANT_READ_URI_PERMISSION = 1;

function toTrustedHttpsOrigin(value: unknown) {
  if (typeof value !== "string" || !value.trim()) {
    return null;
  }
  try {
    const url = new URL(value.trim());
    return url.protocol === "https:" && !url.username && !url.password ? url.origin : null;
  } catch {
    return null;
  }
}

async function getExpoConstants() {
  return (await import("expo-constants")).default;
}

async function getNativeAppBuildProfile() {
  const Constants = await getExpoConstants();
  const value = Constants.expoConfig?.extra?.nativeAppBuildProfile;
  return typeof value === "string" && value.trim() ? value.trim() : "production";
}

async function getNativeAppInstallerEnabled() {
  const Constants = await getExpoConstants();
  const value = Constants.expoConfig?.extra?.nativeAppInstallerEnabled;
  return value !== false && value !== "false" && value !== "0";
}

async function getConfiguredNativeAppInstallerOrigins() {
  const Constants = await getExpoConstants();
  const configured = Constants.expoConfig?.extra?.nativeAppInstallerTrustedOrigins;
  return Array.isArray(configured) ? configured : [];
}

function getNativeAppInstallerTrustedOrigins(
  apiBaseUrl: string | undefined,
  configured: unknown[],
) {
  const values = [
    apiBaseUrl,
    ...configured,
  ];
  return Array.from(new Set(values.map(toTrustedHttpsOrigin).filter((value): value is string => Boolean(value))));
}

export function useAutomaticNativeAppUpdate(options: { enabled: boolean }) {
  const [phase, setPhase] = useState<NativeAppUpdatePhase | "failed" | null>(null);
  const optionsRef = useRef(options);
  const appStateRef = useRef<AppStateStatus>(AppState.currentState);
  const inFlightRef = useRef(false);
  const promptedBuildIdRef = useRef<string | null>(null);
  const operationRetryGateRef = useRef(createUpdateLaneRetryGate());

  useEffect(() => {
    optionsRef.current = options;
  }, [options.enabled]);

  async function openDownloadedApk(
    build: NativeAppBuildInfo,
    fileUri: string,
    nativeInstaller: HBAppInstallerNativeModule | null,
  ) {
    try {
      if (nativeInstaller) {
        const Application = await import("expo-application");
        const packageName = Application.applicationId?.trim();
        const expectedVersionCode = Number(build.appBuildVersion);
        if (!packageName || !Number.isSafeInteger(expectedVersionCode) || expectedVersionCode <= 0) {
          throw new Error("APK 安装身份元数据无效");
        }
        await nativeInstaller.installVerifiedApk({
          fileUri,
          expectedSizeBytes: build.artifactSize,
          expectedSha256Hex: build.artifactSha256,
          expectedPackageName: packageName,
          expectedVersionCode,
          expectedVersionName: build.appVersion,
        });
      } else {
        const FileSystem = await import("expo-file-system/legacy");
        const IntentLauncher = await import("expo-intent-launcher");
        const contentUri = await FileSystem.getContentUriAsync(fileUri);

        await IntentLauncher.startActivityAsync("android.intent.action.VIEW", {
          data: contentUri,
          type: APK_MIME_TYPE,
          flags: FLAG_GRANT_READ_URI_PERMISSION,
        });
      }
    } catch (error) {
      appUpdateMutualExclusion.clearNativeInstaller();
      promptedBuildIdRef.current = null;
      console.warn("[updates] open APK installer failed", error);
      if (!appUpdateMutualExclusion.tryOwnPrompt("native")) {
        return;
      }
      Alert.alert(
        i18n.t("settings:dialogs.nativeUpdateInstallFailedTitle"),
        i18n.t("settings:dialogs.nativeUpdateInstallFailedMessage"),
        [
          {
            text: i18n.t("settings:dialogs.nativeUpdateLaterAction"),
            style: "cancel",
            onPress: () => appUpdateMutualExclusion.releasePrompt("native"),
          },
          {
            text: i18n.t("settings:dialogs.nativeUpdateOpenSettingsAction"),
            onPress: () => {
              if (appUpdateMutualExclusion.isOtaRequiredGateActive()) {
                appUpdateMutualExclusion.releasePrompt("native");
                return;
              }
              appUpdateMutualExclusion.activateNativeInstaller();
              const settingsAction = nativeInstaller
                ? nativeInstaller.openInstallPermissionSettings()
                : openUnknownSourceSettings();
              void settingsAction.catch((settingsError) => {
                appUpdateMutualExclusion.clearNativeInstaller();
                console.warn("[updates] open unknown app source settings failed", settingsError);
              });
            },
          },
        ],
        { cancelable: false },
      );
    }
  }

  async function openUnknownSourceSettings() {
    const [IntentLauncher, Application] = await Promise.all([
      import("expo-intent-launcher"),
      import("expo-application"),
    ]);
    const packageName = Application.applicationId ? `package:${Application.applicationId}` : undefined;
    await IntentLauncher.startActivityAsync(IntentLauncher.ActivityAction.MANAGE_UNKNOWN_APP_SOURCES, {
      data: packageName,
    });
  }

  function promptInstall(
    build: NativeAppBuildInfo,
    fileUri: string,
    nativeInstaller: HBAppInstallerNativeModule | null,
  ) {
    if (!appUpdateMutualExclusion.tryOwnPrompt("native")) {
      return false;
    }
    const versionText = [build.appVersion, build.appBuildVersion ? `(${build.appBuildVersion})` : null]
      .filter(Boolean)
      .join(" ");

    Alert.alert(
      i18n.t("settings:dialogs.nativeUpdateReadyTitle"),
      i18n.t("settings:dialogs.nativeUpdateReadyMessage", { version: versionText || build.easBuildId }),
      [
        {
          text: i18n.t("settings:dialogs.nativeUpdateLaterAction"),
          style: "cancel",
          onPress: () => appUpdateMutualExclusion.releasePrompt("native"),
        },
        {
          text: i18n.t("settings:dialogs.nativeUpdateInstallAction"),
          onPress: () => {
            if (appUpdateMutualExclusion.isOtaRequiredGateActive()) {
              appUpdateMutualExclusion.releasePrompt("native");
              return;
            }
            appUpdateMutualExclusion.activateNativeInstaller();
            void openDownloadedApk(build, fileUri, nativeInstaller);
          },
        },
      ],
      { cancelable: false },
    );
    return true;
  }

  async function check(options: { enabled: boolean }) {
    if (!options.enabled || inFlightRef.current) {
      return;
    }
    if (Platform.OS !== "android") return;

    const updateLease = appUpdateMutualExclusion.tryStartOperation("native");
    if (!updateLease) {
      operationRetryGateRef.current.markBlocked();
      return;
    }
    operationRetryGateRef.current.clear();

    inFlightRef.current = true;
    setPhase("checking");
    try {
      const { apiClient } = await import("@/shared/api/client");
      const buildProfile = await getNativeAppBuildProfile();
      const nativeInstallerEnabled = await getNativeAppInstallerEnabled();

      if (!nativeInstallerEnabled) {
        // 显式关闭时完全停用自动 APK 更新；人工下载只允许从后台受控入口发起。
        setPhase(null);
        return;
      }

      const [FileSystem, Application] = await Promise.all([
        import("expo-file-system/legacy"),
        import("expo-application"),
      ]);
      const nativeInstaller = HBAppInstaller;
      const configuredTrustedOrigins = await getConfiguredNativeAppInstallerOrigins();
      const downloadDirectory = nativeInstaller
        ? await nativeInstaller.getDownloadDirectory()
        : (FileSystem.cacheDirectory ?? FileSystem.documentDirectory ?? null);
      const result = await checkAndDownloadNativeAppUpdate({
        apiClient,
        platform: Platform.OS,
        getCurrentBuildVersion: () => Application.nativeBuildVersion,
        getCurrentPackageName: () => Application.applicationId,
        getBuildProfile: () => buildProfile,
        getDownloadDirectory: () => downloadDirectory,
        getDownloadUrl: (build) => getBuildBoundNativeAppDownloadUrl(apiClient.defaults.baseURL, build, buildProfile),
        getFileInfo: FileSystem.getInfoAsync,
        downloadFile: FileSystem.downloadAsync,
        deleteFile: (fileUri) => FileSystem.deleteAsync(fileUri, { idempotent: true }),
        moveFile: (from, to) => FileSystem.moveAsync({ from, to }),
        readFileChunk: async (fileUri, position, length) => {
          const value = await FileSystem.readAsStringAsync(fileUri, {
            encoding: FileSystem.EncodingType.Base64,
            position,
            length,
          });
          return toByteArray(value);
        },
        readTextFile: FileSystem.readAsStringAsync,
        writeTextFile: (fileUri, value) => FileSystem.writeAsStringAsync(fileUri, value),
        readDirectory: FileSystem.readDirectoryAsync,
        getTrustedOrigins: () => getNativeAppInstallerTrustedOrigins(
          apiClient.defaults.baseURL,
          configuredTrustedOrigins,
        ),
        nativeInstaller,
        onPhase: setPhase,
      });
      setPhase(null);

      if (result.status !== "downloaded" || promptedBuildIdRef.current === result.build.easBuildId) {
        return;
      }

      // 同一个安装包一次运行只提示一次；下次打开 App 仍会继续提醒未安装的新包。
      const promptNativeInstaller = result.verification === "native" ? nativeInstaller : null;
      if (promptInstall(result.build, result.fileUri, promptNativeInstaller)) {
        promptedBuildIdRef.current = result.build.easBuildId;
      }
    } catch (error) {
      setPhase("failed");
      console.warn("[updates] automatic APK update check failed", error);
    } finally {
      inFlightRef.current = false;
      updateLease.finish();
    }
  }

  useEffect(() => {
    if (!options.enabled) {
      return;
    }

    // 登录态恢复之前也执行检查，保证未登录设备能收到重新下载安装提示。
    void check(options);
  }, [options.enabled]);

  useEffect(() => {
    const unsubscribe = appUpdateMutualExclusion.subscribe(() => {
      if (
        optionsRef.current.enabled
        && operationRetryGateRef.current.consumeRetry()
      ) {
        void check(optionsRef.current);
      }
    });
    const subscription = AppState.addEventListener("change", (nextState) => {
      const previousState = appStateRef.current;
      appStateRef.current = nextState;
      if (nextState === "active") {
        // 从系统安装器/浏览器返回后才允许 OTA 下载或 reload。
        appUpdateMutualExclusion.clearNativeInstaller();
      }
      if (previousState === "active" || nextState !== "active") {
        return;
      }

      void check(optionsRef.current);
    });

    return () => {
      unsubscribe();
      subscription.remove();
      operationRetryGateRef.current.clear();
      appUpdateMutualExclusion.releasePrompt("native");
      appUpdateMutualExclusion.clearNativeInstaller();
    };
  }, []);

  // 常驻前台的设备也要定时发现新 APK；同一安装包每次运行仍只提示一次。
  useForegroundUpdateCheckInterval(() => {
    void check(optionsRef.current);
  }, { enabled: options.enabled });

  return {
    phase: options.enabled && Platform.OS === "android" ? phase : null,
    retry: () => { void check(optionsRef.current); },
    dismiss: () => setPhase(null),
  };
}
