import { useEffect, useRef, useState } from "react";
import Constants from "expo-constants";
import * as Application from "expo-application";
import { Alert, AppState, Linking, type AppStateStatus } from "react-native";
import { i18n } from "@/shared/i18n/i18n";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  checkIosNativeAppUpdate,
  createIosNativeOptionalReminderSession,
  markIosNativeOptionalReminder,
  readCachedIosNativeRequiredDecision,
  shouldActivateIosNativeOptionalPrompt,
  shouldCheckIosNativeUpdateOnAppStateChange,
  type IosNativeUpdateContext,
  type IosNativeUpdateDecision,
  type IosNativeUpdateCheckReceipt,
} from "./ios-native-app-update";
import { fetchIosNativeUpdateDecision } from "./ios-native-app-update-api";
import { resolveIosNativeUpdateCenterBaseUrl } from "./ios-native-update-center";
import { appUpdateMutualExclusion } from "./app-update-mutual-exclusion";
import { useForegroundUpdateCheckInterval } from "./foreground-update-interval";

type IosNativeUpdateSnapshot = {
  initialized: boolean;
  decision: IosNativeUpdateDecision | null;
  optionalPromptActive: boolean;
};

const EMPTY_SNAPSHOT: IosNativeUpdateSnapshot = {
  initialized: false,
  decision: null,
  optionalPromptActive: false,
};

async function resolveUpdateContext(): Promise<IosNativeUpdateContext> {
  const apiBaseUrl = resolveIosNativeUpdateCenterBaseUrl({
    buildProfile: Constants.expoConfig?.extra?.nativeAppBuildProfile,
    override: Constants.expoConfig?.extra?.iosNativeUpdateCenterUrl,
  });
  const installedVersion =
    Application.nativeApplicationVersion
    ?? Constants.expoConfig?.version
    ?? "";
  const installedBuild =
    Application.nativeBuildVersion
    ?? Constants.expoConfig?.ios?.buildNumber
    ?? "";

  if (!installedVersion.trim() || !installedBuild.trim()) {
    throw new Error("当前 iOS 安装包缺少版本号或构建号");
  }

  return {
    apiBaseUrl,
    installedVersion,
    installedBuild,
  };
}

async function openAppStore(url: string) {
  try {
    await Linking.openURL(url);
  } catch (error) {
    console.warn("[updates] open iOS App Store failed", error);
    Alert.alert(
      i18n.t("settings:dialogs.iosNativeUpdateOpenFailedTitle"),
      i18n.t("settings:dialogs.iosNativeUpdateOpenFailedMessage"),
    );
  }
}

export function useIosNativeAppUpdate(options: {
  enabled: boolean;
  /**
   * 设置页「自动更新」开关（默认 true）。false 时仍检查并拦截强制更新，
   * 但不弹 App Store 可选更新提醒。
   */
  autoUpdate?: boolean;
}) {
  const [snapshot, setSnapshot] = useState<IosNativeUpdateSnapshot>(EMPTY_SNAPSHOT);
  const [checking, setChecking] = useState(false);
  const enabledRef = useRef(options.enabled);
  const autoUpdateRef = useRef(options.autoUpdate !== false);
  const snapshotRef = useRef(snapshot);
  const contextRef = useRef<IosNativeUpdateContext | null>(null);
  const generationRef = useRef(0);
  const updateEpochRef = useRef(0);
  const optionalReminderSessionRef = useRef(
    createIosNativeOptionalReminderSession(),
  );
  const appStateRef = useRef<AppStateStatus>(AppState.currentState);
  const pendingOptionalPromptRef = useRef<{
    generation: number;
    context: IosNativeUpdateContext;
    decision: IosNativeUpdateDecision;
  } | null>(null);
  const optionalPromptInFlightRef = useRef(false);
  const inFlightRef = useRef<{
    generation: number;
    controller: AbortController;
    promise: Promise<IosNativeUpdateCheckReceipt | null>;
  } | null>(null);
  const runServerCheckRef = useRef<
    () => Promise<IosNativeUpdateCheckReceipt | null>
  >(async () => null);
  const tryShowOptionalPromptRef = useRef<() => void>(() => undefined);

  enabledRef.current = options.enabled;
  autoUpdateRef.current = options.autoUpdate !== false;
  snapshotRef.current = snapshot;

  tryShowOptionalPromptRef.current = () => {
    const pending = pendingOptionalPromptRef.current;
    if (
      !pending
      || optionalPromptInFlightRef.current
      || !enabledRef.current
      // 自动更新关闭：可选提醒保持排队但不弹，重新打开开关后补弹；强制更新走拦截页，不经过这里。
      || !autoUpdateRef.current
      || generationRef.current !== pending.generation
    ) {
      return;
    }

    // 先置 in-flight 再获取 prompt；tryOwnPrompt 会同步通知订阅者，必须避免递归重入。
    optionalPromptInFlightRef.current = true;
    if (!appUpdateMutualExclusion.tryOwnPrompt("native")) {
      optionalPromptInFlightRef.current = false;
      return;
    }

    pendingOptionalPromptRef.current = null;
    optionalReminderSessionRef.current.markSeen(
      pending.context,
      pending.decision,
    );
    const activeSnapshot = {
      ...snapshotRef.current,
      optionalPromptActive: true,
    };
    snapshotRef.current = activeSnapshot;
    setSnapshot(activeSnapshot);

    // 提示已由会话内去重保护；持久化失败不得吞掉当前可选更新提示。
    void markIosNativeOptionalReminder(
      AppAsyncStorage,
      pending.context,
      pending.decision,
      Date.now(),
    ).catch((error) => {
      if (
        enabledRef.current
        && generationRef.current === pending.generation
      ) {
        console.warn("[updates] persist iOS App Store reminder failed", error);
      }
    });

    const resolveOptionalPrompt = () => {
      appUpdateMutualExclusion.releasePrompt("native");
      if (
        !enabledRef.current
        || generationRef.current !== pending.generation
      ) {
        return false;
      }
      const resolvedSnapshot = {
        ...snapshotRef.current,
        optionalPromptActive: false,
      };
      snapshotRef.current = resolvedSnapshot;
      setSnapshot(resolvedSnapshot);
      return true;
    };
    Alert.alert(
      i18n.t("settings:dialogs.iosNativeUpdateAvailableTitle"),
      pending.decision.releaseMessage
        || i18n.t("settings:dialogs.iosNativeUpdateAvailableMessage", {
          version: pending.decision.latestVersion,
        }),
      [
        {
          text: i18n.t("settings:dialogs.iosNativeUpdateLaterAction"),
          style: "cancel",
          onPress: resolveOptionalPrompt,
        },
        {
          text: i18n.t("settings:dialogs.iosNativeUpdateOpenStoreAction"),
          onPress: () => {
            if (
              appUpdateMutualExclusion.isOtaRequiredGateActive()
            ) {
              resolveOptionalPrompt();
              return;
            }
            if (resolveOptionalPrompt()) {
              void openAppStore(pending.decision.appStoreUrl!);
            }
          },
        },
      ],
      { cancelable: false },
    );
    optionalPromptInFlightRef.current = false;
  };

  runServerCheckRef.current = async () => {
    const context = contextRef.current;
    const generation = generationRef.current;
    if (!enabledRef.current || !context) {
      return null;
    }

    if (inFlightRef.current?.generation === generation) {
      return inFlightRef.current.promise;
    }

    const epoch = updateEpochRef.current + 1;
    updateEpochRef.current = epoch;
    const controller = new AbortController();
    const isCurrent = () => (
      enabledRef.current
      && generationRef.current === generation
      && !controller.signal.aborted
    );
    setChecking(true);
    const promise = (async () => {
      try {
        const outcome = await checkIosNativeAppUpdate({
          context,
          storage: AppAsyncStorage,
          now: Date.now,
          signal: controller.signal,
          fetchDecision: (signal) =>
            fetchIosNativeUpdateDecision(context, undefined, signal),
          optionalReminderSession: optionalReminderSessionRef.current,
          // 同一 generation 已见 required 时，缓存写失败与后续离线都不能把门禁降级为首次放行。
          memoryRequiredDecision:
            snapshotRef.current.decision?.state === "required"
              ? snapshotRef.current.decision
              : null,
        });

        if (!isCurrent()) {
          return null;
        }

        const shouldQueueOptionalPrompt = shouldActivateIosNativeOptionalPrompt({
          decision: outcome.decision,
          shouldPromptOptional: outcome.shouldPromptOptional,
        });
        pendingOptionalPromptRef.current = shouldQueueOptionalPrompt
          && outcome.decision?.state === "optional"
          && outcome.decision.appStoreUrl
          ? {
              generation,
              context,
              decision: outcome.decision,
            }
          : null;
        const nextSnapshot = {
          initialized: true,
          decision: outcome.decision,
          optionalPromptActive: snapshotRef.current.optionalPromptActive,
        };
        snapshotRef.current = nextSnapshot;
        setSnapshot(nextSnapshot);

        if (outcome.error) {
          console.warn("[updates] iOS App Store update check failed", outcome.error);
        }
        if (outcome.storageError) {
          console.warn("[updates] persist iOS App Store update state failed", outcome.storageError);
        }

        tryShowOptionalPromptRef.current();
        return { epoch, outcome };
      } catch (error) {
        if (!isCurrent()) {
          // AbortSignal 与 generation 失效统一返回 null；真实网络失败由 outcome receipt 表达。
          return null;
        }
        throw error;
      }
    })().finally(() => {
      if (
        inFlightRef.current?.generation === generation
        && inFlightRef.current.controller === controller
      ) {
        inFlightRef.current = null;
        setChecking(false);
      }
    });

    inFlightRef.current = { generation, controller, promise };
    return promise;
  };

  useEffect(() => {
    inFlightRef.current?.controller.abort();
    inFlightRef.current = null;
    const generation = generationRef.current + 1;
    generationRef.current = generation;
    // 构建资格切换也会使既有 OTA 检查凭据失效。
    updateEpochRef.current += 1;
    contextRef.current = null;
    pendingOptionalPromptRef.current = null;
    optionalPromptInFlightRef.current = false;
    appUpdateMutualExclusion.releasePrompt("native");
    setSnapshot(EMPTY_SNAPSHOT);
    setChecking(false);

    if (!options.enabled) {
      return;
    }

    void (async () => {
      try {
        const context = await resolveUpdateContext();
        if (!enabledRef.current || generationRef.current !== generation) {
          return;
        }
        contextRef.current = context;

        const cachedDecision = await readCachedIosNativeRequiredDecision(
          AppAsyncStorage,
          context,
        );
        if (!enabledRef.current || generationRef.current !== generation) {
          return;
        }

        if (cachedDecision) {
          const cachedSnapshot = {
            initialized: true,
            decision: cachedDecision,
            optionalPromptActive: false,
          };
          snapshotRef.current = cachedSnapshot;
          setSnapshot(cachedSnapshot);
        }

        await runServerCheckRef.current();
      } catch (error) {
        if (!enabledRef.current || generationRef.current !== generation) {
          return;
        }
        console.warn("[updates] initialize iOS App Store update check failed", error);
        // 没有可读取的可信强制缓存时，初始化失败按既定策略放行业务页面。
        const failedSnapshot = {
          initialized: true,
          decision: null,
          optionalPromptActive: false,
        };
        snapshotRef.current = failedSnapshot;
        setSnapshot(failedSnapshot);
      }
    })();

    return () => {
      if (generationRef.current === generation) {
        generationRef.current += 1;
        updateEpochRef.current += 1;
      }
      inFlightRef.current?.controller.abort();
      inFlightRef.current = null;
      pendingOptionalPromptRef.current = null;
      optionalPromptInFlightRef.current = false;
      appUpdateMutualExclusion.releasePrompt("native");
    };
  }, [options.enabled]);

  // 自动更新从关到开：立即重新检查一次，并补弹关闭期间排队的可选提醒。
  const previousAutoUpdateRef = useRef(options.autoUpdate !== false);
  useEffect(() => {
    const enabledNow = options.autoUpdate !== false;
    const wasEnabled = previousAutoUpdateRef.current;
    previousAutoUpdateRef.current = enabledNow;
    if (enabledNow && !wasEnabled && enabledRef.current) {
      void runServerCheckRef.current();
    }
  }, [options.autoUpdate]);

  useEffect(() => {
    const unsubscribe = appUpdateMutualExclusion.subscribe(() => {
      tryShowOptionalPromptRef.current();
    });
    const subscription = AppState.addEventListener("change", (nextState) => {
      const previousState = appStateRef.current;
      appStateRef.current = nextState;
      if (
        !enabledRef.current
        || !shouldCheckIosNativeUpdateOnAppStateChange(previousState, nextState)
      ) {
        return;
      }
      void runServerCheckRef.current();
    });

    return () => {
      unsubscribe();
      subscription.remove();
      pendingOptionalPromptRef.current = null;
      optionalPromptInFlightRef.current = false;
      appUpdateMutualExclusion.releasePrompt("native");
    };
  }, []);

  // 前台定时补查 App Store 新版本；可选提醒仍由 optionalReminderSession 按会话去重。
  useForegroundUpdateCheckInterval(() => {
    if (enabledRef.current) {
      void runServerCheckRef.current();
    }
  }, { enabled: options.enabled });

  return {
    ...snapshot,
    checking,
    openRequiredUpdate: () => {
      if (!enabledRef.current) {
        return;
      }
      const url = snapshotRef.current.decision?.appStoreUrl;
      if (url) {
        void openAppStore(url);
      }
    },
    recheck: () => runServerCheckRef.current(),
    getCheckEpoch: () => updateEpochRef.current,
  };
}
