import { useEffect, useRef, useState } from "react";
import { Alert, AppState, Platform, type AppStateStatus } from "react-native";
import * as Updates from "expo-updates";
import { i18n } from "@/shared/i18n/i18n";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  appUpdateMutualExclusion,
  createUpdateLaneRetryGate,
} from "./app-update-mutual-exclusion";
import { backgroundDownloadGate } from "./background-download-gate";
import { useForegroundUpdateCheckInterval } from "./foreground-update-interval";
import {
  checkMobileOtaUpdate,
  readCachedMobileOtaRequiredDecision,
  tryClaimMobileOtaOptionalPrompt,
  type MobileOtaManualCheckResult,
  type MobileOtaUpdateContext,
  type MobileOtaUpdateDecision,
} from "./mobile-ota-update";
import { fetchMobileOtaUpdateDecision } from "./mobile-ota-update-api";
import { MobileOtaUpdatePort } from "./mobile-ota-update-port";
import { resolveMobileOtaRuntimeContext } from "./mobile-ota-update-runtime";

type MobileOtaBarrierReceipt = Readonly<{ allowed: boolean; epoch: number }>;

export type UseMobileOtaUpdateOptions = Readonly<{
  enabled: boolean;
  beforeCheck?: () => Promise<MobileOtaBarrierReceipt>;
  getEpoch?: () => number;
}>;

type MobileOtaSnapshot = Readonly<{
  enabled: boolean;
  initialized: boolean;
  checking: boolean;
  downloading: boolean;
  applying: boolean;
  downloaded: boolean;
  decision: MobileOtaUpdateDecision | null;
  lastError: string | null;
}>;

const EMPTY_SNAPSHOT: MobileOtaSnapshot = Object.freeze({
  enabled: false,
  initialized: false,
  checking: false,
  downloading: false,
  applying: false,
  downloaded: false,
  decision: null,
  lastError: null,
});

function decisionTargetIdentity(decision: MobileOtaUpdateDecision) {
  return JSON.stringify([
    decision.policyVersion,
    decision.releaseChannel,
    decision.runtimeVersion,
    decision.updateId,
    decision.updateGroupId,
  ]);
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : String(error);
}

export function useMobileOtaUpdate(options: UseMobileOtaUpdateOptions) {
  const effectiveEnabled = options.enabled && !__DEV__ && Updates.isEnabled;
  const [snapshot, setSnapshot] = useState<MobileOtaSnapshot>(EMPTY_SNAPSHOT);
  const snapshotRef = useRef(snapshot);
  const optionsRef = useRef(options);
  const enabledRef = useRef(effectiveEnabled);
  const contextRef = useRef<MobileOtaUpdateContext | null>(null);
  const portRef = useRef<MobileOtaUpdatePort | null>(null);
  const generationRef = useRef(0);
  const appStateRef = useRef<AppStateStatus>(AppState.currentState);
  const optionalPromptTargetRef = useRef<string | null>(null);
  const operationRetryGateRef = useRef(createUpdateLaneRetryGate());
  // 可选 OTA 因「登录后空闲」闸门未开而被挡下：闸门打开时据此自动重试。
  const deferredByIdleGateRef = useRef(false);
  // 用户在设置页手动「检查更新」时绕过闸门，立即下载。
  const manualCheckRef = useRef(false);
  const inFlightRef = useRef<{
    generation: number;
    controller: AbortController;
    promise: Promise<void>;
  } | null>(null);
  const runCheckRef = useRef<() => Promise<void>>(async () => undefined);
  const downloadRequiredRef = useRef<() => Promise<void>>(async () => undefined);
  const restartRequiredRef = useRef<() => Promise<void>>(async () => undefined);

  optionsRef.current = options;
  enabledRef.current = effectiveEnabled;
  snapshotRef.current = snapshot;

  const commitSnapshot = (next: MobileOtaSnapshot) => {
    snapshotRef.current = next;
    setSnapshot(next);
  };

  const patchSnapshot = (patch: Partial<MobileOtaSnapshot>) => {
    commitSnapshot(Object.freeze({ ...snapshotRef.current, ...patch }));
  };

  async function passNativeBarrier() {
    const barrier = optionsRef.current.beforeCheck;
    if (!barrier) return true;
    const receipt = await barrier();
    return (
      receipt.allowed
      && (
        optionsRef.current.getEpoch === undefined
        || optionsRef.current.getEpoch() === receipt.epoch
      )
    );
  }

  async function applyDownloadedDecision(decision: MobileOtaUpdateDecision) {
    const context = contextRef.current;
    const port = portRef.current;
    if (!enabledRef.current || !context || !port || !port.isReady(decision)) return;
    patchSnapshot({ applying: true, lastError: null });
    let reloadAttempted = false;
    try {
      if (!await passNativeBarrier()) {
        throw new Error("native update barrier changed");
      }
      const current = await fetchMobileOtaUpdateDecision(context);
      if (
        current.state === "none"
        || decisionTargetIdentity(current) !== decisionTargetIdentity(decision)
      ) {
        throw new Error("Mobile OTA policy selection changed");
      }
      if (!appUpdateMutualExclusion.canReloadOta()) {
        throw new Error("another app update operation is active");
      }
      const lease = appUpdateMutualExclusion.tryStartOperation("ota");
      if (!lease) throw new Error("another app update operation is active");
      try {
        reloadAttempted = true;
        await port.reload();
      } finally {
        lease.finish();
      }
    } catch (error) {
      console.warn("[updates] apply controlled Mobile OTA failed", error);
      // reload 失败后保留当前可选目标的去重身份。释放 prompt 锁会同步通知
      // 订阅者；清空 ref 会让同一个已下载目标在失败后立即再次弹窗。
      appUpdateMutualExclusion.releasePrompt("ota");
      if (reloadAttempted && decision.state === "optional") {
        Alert.alert(
          i18n.t("settings:dialogs.mobileOtaRestartFailedTitle"),
          i18n.t("settings:dialogs.mobileOtaRestartFailedMessage"),
        );
      }
      const downloadedStillReady = port.isReady(decision);
      patchSnapshot({
        applying: false,
        downloaded: downloadedStillReady,
        lastError: errorMessage(error),
      });
      // 写入失败或 409 等只重新读取权威策略，不重放旧目标。
      void runCheckRef.current();
    }
  }

  function promptOptionalRestart(decision: MobileOtaUpdateDecision) {
    const identity = decisionTargetIdentity(decision);
    if (!tryClaimMobileOtaOptionalPrompt(
      optionalPromptTargetRef,
      identity,
      () => appUpdateMutualExclusion.tryOwnPrompt("ota"),
    )) {
      return;
    }
    Alert.alert(
      i18n.t("settings:dialogs.mobileOtaAvailableTitle"),
      decision.releaseMessage
        || i18n.t("settings:dialogs.mobileOtaAvailableMessage"),
      [
        {
          text: i18n.t("settings:dialogs.mobileOtaLaterAction"),
          style: "cancel",
          onPress: () => appUpdateMutualExclusion.releasePrompt("ota"),
        },
        {
          text: i18n.t("settings:dialogs.mobileOtaRestartAction"),
          onPress: () => {
            void applyDownloadedDecision(decision);
          },
        },
      ],
      { cancelable: false },
    );
  }

  async function downloadDecision(
    decision: MobileOtaUpdateDecision,
    isCurrent: () => boolean,
  ) {
    const port = portRef.current;
    if (!port || decision.state === "none") return;
    const lease = appUpdateMutualExclusion.tryStartOperation("ota");
    if (!lease) {
      operationRetryGateRef.current.markBlocked();
      return;
    }
    operationRetryGateRef.current.clear();
    patchSnapshot({ downloading: true, downloaded: false, lastError: null });
    try {
      const result = await port.download(decision, { isCurrent });
      if (!isCurrent()) return;
      if (result.state !== "downloaded") {
        patchSnapshot({
          downloading: false,
          downloaded: false,
          lastError: result.reason,
        });
        return;
      }
      patchSnapshot({ downloading: false, downloaded: true, lastError: null });
      if (decision.state === "optional") promptOptionalRestart(decision);
    } finally {
      lease.finish();
    }
  }

  runCheckRef.current = async () => {
    const generation = generationRef.current;
    const context = contextRef.current;
    if (!enabledRef.current || !context) return;
    if (inFlightRef.current?.generation === generation) {
      return inFlightRef.current.promise;
    }
    const controller = new AbortController();
    const isCurrent = () => (
      enabledRef.current
      && generationRef.current === generation
      && !controller.signal.aborted
    );
    patchSnapshot({ checking: true, lastError: null });
    const promise = (async () => {
      try {
        if (!await passNativeBarrier() || !isCurrent()) return;
        const outcome = await checkMobileOtaUpdate({
          context,
          storage: AppAsyncStorage,
          signal: controller.signal,
          fetchDecision: (signal) =>
            fetchMobileOtaUpdateDecision(context, undefined, signal),
          memoryRequiredDecision:
            snapshotRef.current.decision?.state === "required"
              ? snapshotRef.current.decision
              : null,
        });
        if (!isCurrent()) return;
        const decision = outcome.decision;
        appUpdateMutualExclusion.setOtaRequiredGate(
          decision?.state === "required",
        );
        commitSnapshot(Object.freeze({
          ...snapshotRef.current,
          enabled: true,
          initialized: true,
          checking: false,
          decision,
          downloaded: decision?.state === "none" ? false : snapshotRef.current.downloaded,
          lastError: outcome.error ? errorMessage(outcome.error) : null,
        }));
        if (outcome.storageError) {
          console.warn("[updates] persist Mobile OTA required state failed", outcome.storageError);
        }
        // 先发布 required/optional 判定，再放行原生 lane；required 会继续阻止 APK optional。
        appUpdateMutualExclusion.setOtaInitializationPending(false);
        if (decision && decision.state !== "none" && !portRef.current?.isReady(decision)) {
          // 可选 OTA 等登录完成并空闲后再下载，不和开机、登录抢带宽；强制 OTA 与手动检查照常立即下载。
          if (
            decision.state === "optional"
            && !manualCheckRef.current
            && !backgroundDownloadGate.isOpen()
          ) {
            deferredByIdleGateRef.current = true;
            return;
          }
          deferredByIdleGateRef.current = false;
          await downloadDecision(decision, isCurrent);
        }
      } catch (error) {
        if (!isCurrent()) return;
        console.warn("[updates] controlled Mobile OTA check failed", error);
        patchSnapshot({
          initialized: true,
          checking: false,
          lastError: errorMessage(error),
        });
        appUpdateMutualExclusion.setOtaRequiredGate(
          snapshotRef.current.decision?.state === "required",
        );
        appUpdateMutualExclusion.setOtaInitializationPending(false);
      }
    })().finally(() => {
      if (inFlightRef.current?.controller === controller) {
        inFlightRef.current = null;
      }
    });
    inFlightRef.current = { generation, controller, promise };
    return promise;
  };

  downloadRequiredRef.current = async () => {
    const decision = snapshotRef.current.decision;
    const generation = generationRef.current;
    if (!decision || decision.state !== "required") return;
    await downloadDecision(decision, () => (
      enabledRef.current && generationRef.current === generation
    ));
  };

  restartRequiredRef.current = async () => {
    const decision = snapshotRef.current.decision;
    if (!decision || decision.state !== "required") return;
    await applyDownloadedDecision(decision);
  };

  useEffect(() => {
    inFlightRef.current?.controller.abort();
    inFlightRef.current = null;
    const generation = generationRef.current + 1;
    generationRef.current = generation;
    contextRef.current = null;
    portRef.current = null;
    optionalPromptTargetRef.current = null;
    operationRetryGateRef.current.clear();
    appUpdateMutualExclusion.releasePrompt("ota");
    appUpdateMutualExclusion.setOtaRequiredGate(false);

    if (!effectiveEnabled) {
      // 启动尚未就绪时继续阻止 APK lane 抢跑；仅当 JS OTA 本身不可用时放行原生检查。
      appUpdateMutualExclusion.setOtaInitializationPending(!options.enabled);
      commitSnapshot(Object.freeze({ ...EMPTY_SNAPSHOT, enabled: false }));
      return;
    }

    appUpdateMutualExclusion.setOtaInitializationPending(true);
    commitSnapshot(Object.freeze({ ...EMPTY_SNAPSHOT, enabled: true }));
    void (async () => {
      try {
        const context = resolveMobileOtaRuntimeContext({
          platform: Platform.OS,
          channel: Updates.channel,
          runtimeVersion: Updates.runtimeVersion,
          updateId: Updates.updateId,
          manifest: Updates.manifest,
        });
        if (!enabledRef.current || generationRef.current !== generation) return;
        contextRef.current = context;
        portRef.current = new MobileOtaUpdatePort({
          enabled: true,
          runtimeVersion: context.runtimeVersion,
          currentChannel: context.updateChannel,
          updates: {
            setUpdateRequestHeadersOverride: (headers) =>
              Updates.setUpdateRequestHeadersOverride(headers),
            checkForUpdateAsync: () => Updates.checkForUpdateAsync(),
            fetchUpdateAsync: () => Updates.fetchUpdateAsync(),
            reloadAsync: () => Updates.reloadAsync(),
          },
        });
        const cached = await readCachedMobileOtaRequiredDecision(
          AppAsyncStorage,
          context,
        );
        if (!enabledRef.current || generationRef.current !== generation) return;
        if (cached) {
          appUpdateMutualExclusion.setOtaRequiredGate(true);
          commitSnapshot(Object.freeze({
            ...snapshotRef.current,
            enabled: true,
            initialized: true,
            decision: cached,
          }));
        }
        await runCheckRef.current();
      } catch (error) {
        if (!enabledRef.current || generationRef.current !== generation) return;
        console.warn("[updates] initialize controlled Mobile OTA failed", error);
        appUpdateMutualExclusion.setOtaRequiredGate(false);
        appUpdateMutualExclusion.setOtaInitializationPending(false);
        patchSnapshot({ initialized: true, checking: false, lastError: errorMessage(error) });
      }
    })();

    return () => {
      if (generationRef.current === generation) generationRef.current += 1;
      inFlightRef.current?.controller.abort();
      inFlightRef.current = null;
      appUpdateMutualExclusion.releasePrompt("ota");
      operationRetryGateRef.current.clear();
      appUpdateMutualExclusion.setOtaRequiredGate(false);
      appUpdateMutualExclusion.setOtaInitializationPending(false);
    };
  }, [effectiveEnabled]);

  useEffect(() => {
    const unsubscribe = appUpdateMutualExclusion.subscribe(() => {
      if (!enabledRef.current) return;
      const decision = snapshotRef.current.decision;
      if (decision?.state === "optional" && snapshotRef.current.downloaded) {
        promptOptionalRestart(decision);
      } else if (
        !inFlightRef.current
        && operationRetryGateRef.current.consumeRetry()
      ) {
        void runCheckRef.current();
      }
    });
    const unsubscribeIdleGate = backgroundDownloadGate.subscribe(() => {
      if (
        enabledRef.current
        && deferredByIdleGateRef.current
        && backgroundDownloadGate.isOpen()
      ) {
        deferredByIdleGateRef.current = false;
        void runCheckRef.current();
      }
    });
    const subscription = AppState.addEventListener("change", (nextState) => {
      const previousState = appStateRef.current;
      appStateRef.current = nextState;
      if (previousState !== "active" && nextState === "active") {
        void runCheckRef.current();
      }
    });
    return () => {
      unsubscribe();
      unsubscribeIdleGate();
      subscription.remove();
    };
  }, []);

  // 前台定时补查新 OTA 目标；已提示过的目标由 optionalPromptTargetRef 去重，不会重复弹窗。
  useForegroundUpdateCheckInterval(() => {
    void runCheckRef.current();
  }, { enabled: effectiveEnabled });

  async function checkManually(): Promise<MobileOtaManualCheckResult> {
    if (!enabledRef.current) {
      return Object.freeze({ status: "disabled" });
    }

    // 用户主动检查时允许再次展示先前选择“稍后”的可选更新提示。
    optionalPromptTargetRef.current = null;
    manualCheckRef.current = true;
    try {
      await runCheckRef.current();
    } finally {
      manualCheckRef.current = false;
    }

    const current = snapshotRef.current;
    if (!enabledRef.current) {
      return Object.freeze({ status: "disabled" });
    }
    if (current.lastError) {
      return Object.freeze({ status: "failed" });
    }
    if (!current.decision || current.decision.state === "none") {
      return Object.freeze({ status: "not-available" });
    }
    if (current.decision.state === "required") {
      return Object.freeze({ status: "required" });
    }
    if (!current.downloaded) {
      return Object.freeze({ status: "failed" });
    }

    promptOptionalRestart(current.decision);
    return Object.freeze({ status: "update-ready" });
  }

  return {
    ...snapshot,
    // 首次 effect 前也必须保持 checking 门禁，不能让业务页面短暂可交互。
    enabled: effectiveEnabled,
    recheck: () => runCheckRef.current(),
    checkManually,
    downloadRequired: () => downloadRequiredRef.current(),
    restartRequired: () => restartRequiredRef.current(),
  };
}
