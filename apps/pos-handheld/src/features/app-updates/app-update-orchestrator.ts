import type {
  AndroidInstallPermissionStatus,
  AndroidNativeUpdatePort,
} from "./android-native-update-adapter";
import { isAndroidInstallPermissionRequiredError } from "./android-native-update-adapter";
import {
  decideAppUpdateRestart,
  type AppUpdateRefreshReason,
  type AppUpdateRestartDecision,
  type AppUpdateRestartSafetySnapshot,
} from "./app-update-coordinator";
import type {
  ExpoOtaBeforeReloadDecision,
  ExpoOtaUpdateApplyResult,
  ExpoOtaUpdatePrepareResult,
} from "./expo-ota-update-port";

import type {
  NewTransactionGate,
  PosHandheldUpdatePolicy,
} from "@/core/contracts/app-updates";
import type { PosHandheldOtaUpdatePolicy } from "@/core/contracts/ota-app-updates";
import type { DeviceSystem } from "@/core/contracts/security";

export type AppUpdatePresentation = Readonly<{
  key: string;
  kind: "none" | "native" | "ota";
  requirement: "optional" | "required" | null;
  phase:
    | "unchecked"
    | "hidden"
    | "prompt"
    | "waiting-for-safe"
    | "blocking";
  blocking: boolean;
  releaseMessage: string | null;
  platform: DeviceSystem | null;
  appStoreUrl: string | null;
  downloadUrl: string | null;
  downloadState?: "idle" | "downloading" | "ready" | "failed" | undefined;
  downloadTargetKey?: string | undefined;
}>;

export type AppUpdateActionResult =
  | Readonly<{ action: "none"; reason: "unchecked" | "no-update" }>
  | Readonly<{
      action: "blocked";
      reason:
        | Exclude<
            AppUpdateRestartDecision,
            { canRestart: true }
          >["reason"]
        | "selection-changed";
    }>
  | Readonly<{ action: "open-app-store"; url: string }>
  | Readonly<{ action: "install-android-apk" }>
  | Readonly<{ action: "ota"; result: ExpoOtaUpdateApplyResult }>;

type NativeUpdateCoordinatorPort = Readonly<{
  getPolicy(): PosHandheldUpdatePolicy | null;
  getGate(): NewTransactionGate;
  subscribe(listener: (gate: NewTransactionGate) => void): () => void;
  refreshOnStartup(): Promise<unknown>;
  refreshOnForeground(): Promise<unknown>;
  refreshOnNetworkAvailable(): Promise<unknown>;
}>;

type OtaUpdateCoordinatorPort = Readonly<{
  getPolicy(): PosHandheldOtaUpdatePolicy | null;
  subscribe(
    listener: (policy: PosHandheldOtaUpdatePolicy | null) => void,
  ): () => void;
  refreshOnStartup(): Promise<unknown>;
  refreshOnForeground(): Promise<unknown>;
  refreshOnNetworkAvailable(): Promise<unknown>;
  prepare(policy: PosHandheldOtaUpdatePolicy): Promise<ExpoOtaUpdatePrepareResult>;
  apply(
    policy: PosHandheldOtaUpdatePolicy,
    beforeReload: () =>
      | ExpoOtaBeforeReloadDecision
      | Promise<ExpoOtaBeforeReloadDecision>,
  ): Promise<ExpoOtaUpdateApplyResult>;
}>;

export type AppUpdateOrchestratorOptions = Readonly<{
  installedVersion: string;
  native: NativeUpdateCoordinatorPort;
  ota: OtaUpdateCoordinatorPort;
  safety: Readonly<{
    getSafetySnapshot():
      | AppUpdateRestartSafetySnapshot
      | Promise<AppUpdateRestartSafetySnapshot>;
  }>;
  transition: Readonly<{
    isTransitionActive(): boolean;
    subscribe(listener: () => void): () => void;
    runTransition<T>(operation: () => Promise<T>): Promise<T>;
  }>;
  appStore: Readonly<{
    open(url: string): Promise<void>;
  }>;
  androidNative?: AndroidNativeUpdatePort;
}>;

type GateListener = (gate: NewTransactionGate) => void;
type PresentationListener = (presentation: AppUpdatePresentation) => void;

/**
 * 原生与 OTA 各自刷新、缓存和失败回退；这里只合并交易准入与用户展示优先级。
 * required 在已有交易未安全前仅阻止下一单，安全后才升级为全局阻断门。
 */
export class AppUpdateOrchestrator {
  private presentation: AppUpdatePresentation;
  private gate: NewTransactionGate;
  private safeSelectionKey: string | null = null;
  private safeForCompletion: boolean | null = null;
  private safetyInFlight: Promise<AppUpdatePresentation> | null = null;
  private refreshInFlight: Promise<AppUpdatePresentation> | null = null;
  private preparationInFlight: Promise<AppUpdatePresentation> | null = null;
  private preparingKey: string | null = null;
  private preparedKey: string | null = null;
  private failedKey: string | null = null;
  private disposed = false;
  private readonly gateListeners = new Set<GateListener>();
  private readonly presentationListeners =
    new Set<PresentationListener>();
  private readonly unsubscribeNative: () => void;
  private readonly unsubscribeOta: () => void;
  private readonly unsubscribeTransition: () => void;

  public constructor(private readonly options: AppUpdateOrchestratorOptions) {
    this.presentation = chooseAppUpdatePresentation(
      options.native.getPolicy(),
      options.ota.getPolicy(),
      options.installedVersion,
    );
    this.gate = this.calculateGate();
    this.unsubscribeNative = options.native.subscribe(() => {
      this.recompute();
    });
    this.unsubscribeOta = options.ota.subscribe(() => {
      this.recompute();
    });
    this.unsubscribeTransition = options.transition.subscribe(() => {
      this.recomputeGate();
      for (const listener of this.gateListeners) this.notifyGate(listener);
    });
  }

  public getPolicy(): PosHandheldUpdatePolicy | null {
    return this.options.native.getPolicy();
  }

  public getOtaPolicy(): PosHandheldOtaUpdatePolicy | null {
    return this.options.ota.getPolicy();
  }

  public getPresentation(): AppUpdatePresentation {
    return this.presentation;
  }

  public getGate(): NewTransactionGate {
    return this.gate;
  }

  /** 仅查询授权状态，供 UI 决定展示“安装”还是“去授权”；不会启动传输。 */
  public getAndroidInstallPermissionStatus(): Promise<
    AndroidInstallPermissionStatus | null
  > {
    if (
      this.presentation.kind !== "native" ||
      this.presentation.platform !== "Android" ||
      !this.options.androidNative
    ) {
      return Promise.resolve(null);
    }
    return this.options.androidNative.getInstallPermissionStatus();
  }

  /** 设置跳转必须由用户点击明确触发；返回前台后不会重放下载或安装。 */
  public async openAndroidInstallPermissionSettings(): Promise<void> {
    if (
      this.presentation.kind !== "native" ||
      this.presentation.platform !== "Android" ||
      !this.options.androidNative
    ) {
      throw new Error("Android install permission settings are unavailable.");
    }
    await this.options.androidNative.openInstallPermissionSettings();
  }

  private calculateGate(): NewTransactionGate {
    const nativeGate = this.options.native.getGate();
    const nativePolicy = this.options.native.getPolicy();
    if (this.options.transition.isTransitionActive()) {
      return requiredGate(
        this.presentation.kind === "ota"
          ? "ota-update"
          : "force-update",
      );
    }
    if (
      this.presentation.requirement === "required" &&
      this.presentation.kind === "native"
    ) {
      return requiredGate("force-update");
    }
    if (
      this.presentation.requirement === "required" &&
      this.presentation.kind === "ota"
    ) {
      return requiredGate("ota-update");
    }
    // 设备权限是独立门禁，不能因 OTA 未检查或平台不匹配被降级为 unchecked。
    if (nativeGate.state === "disabled") {
      return Object.freeze({
        state: nativeGate.state,
        canStartNewTransaction: nativeGate.canStartNewTransaction,
        canContinueRecovery: nativeGate.canContinueRecovery,
      });
    }
    if (
      nativeGate.state === "unchecked" ||
      (nativePolicy !== null &&
        (this.options.ota.getPolicy() === null ||
          this.options.ota.getPolicy()?.platform !== nativePolicy.platform))
    ) {
      return Object.freeze({
        state: "unchecked",
        canStartNewTransaction: true,
        canContinueRecovery: true,
      });
    }
    return Object.freeze({
      state: nativeGate.state,
      canStartNewTransaction: nativeGate.canStartNewTransaction,
      canContinueRecovery: nativeGate.canContinueRecovery,
    });
  }

  public subscribe(listener: GateListener): () => void {
    this.gateListeners.add(listener);
    this.notifyGate(listener);
    return () => {
      this.gateListeners.delete(listener);
    };
  }

  public subscribePresentation(
    listener: PresentationListener,
  ): () => void {
    this.presentationListeners.add(listener);
    this.notifyPresentation(listener);
    return () => {
      this.presentationListeners.delete(listener);
    };
  }

  public refreshOnStartup(): Promise<AppUpdatePresentation> {
    return this.refresh("startup");
  }

  public refreshOnForeground(): Promise<AppUpdatePresentation> {
    return this.refresh("foreground");
  }

  public refreshOnNetworkAvailable(): Promise<AppUpdatePresentation> {
    return this.refresh("network");
  }

  public refreshSafety(): Promise<AppUpdatePresentation> {
    if (this.safetyInFlight) return this.safetyInFlight;
    const operation = this.refreshSafetyOnce().finally(() => {
      if (this.safetyInFlight === operation) this.safetyInFlight = null;
    });
    this.safetyInFlight = operation;
    return operation;
  }

  /** APK/OTA 先后台下载；此阶段不持交易切换租约，也不打开系统安装器或重启。 */
  public async prepareSelectedUpdate(): Promise<AppUpdatePresentation> {
    if (this.disposed) return this.presentation;
    const key = this.downloadKey();
    if (key === null || key === this.preparedKey) return this.presentation;
    if (this.preparationInFlight) {
      const previousKey = this.preparingKey;
      await this.preparationInFlight;
      return this.downloadKey() !== previousKey
        ? this.prepareSelectedUpdate()
        : this.presentation;
    }
    const selected = this.presentation;
    const nativePolicy = this.options.native.getPolicy();
    const otaPolicy = this.options.ota.getPolicy();
    this.preparingKey = key;
    this.preparedKey = null;
    this.failedKey = null;
    this.recompute();
    const operation = Promise.resolve().then(async () => {
      try {
        if (selected.kind === "native" && nativePolicy && this.options.androidNative) {
          await this.options.androidNative.prepare(nativePolicy);
        } else if (selected.kind === "ota" && otaPolicy) {
          const result = await this.options.ota.prepare(otaPolicy);
          if (result.state !== "ready") throw new Error("OTA download is not ready.");
        } else {
          throw new Error("Update downloader is unavailable.");
        }
        if (!this.disposed && this.downloadKey() === key) this.preparedKey = key;
      } catch {
        if (!this.disposed && this.downloadKey() === key) this.failedKey = key;
      } finally {
        this.preparingKey = null;
        this.preparationInFlight = null;
        if (!this.disposed) this.recompute();
      }
      return this.presentation;
    });
    this.preparationInFlight = operation;
    return operation;
  }

  public async performSelectedUpdate(): Promise<AppUpdateActionResult> {
    const selected = this.presentation;
    if (selected.phase === "unchecked") {
      return Object.freeze({ action: "none", reason: "unchecked" });
    }
    if (selected.kind === "none") {
      return Object.freeze({ action: "none", reason: "no-update" });
    }
    // 手动入口也不能跳过下载完成状态，避免点击“安装”后才开始长时间传输。
    if (this.downloadKey() !== null && this.downloadKey() !== this.preparedKey) {
      return Object.freeze({ action: "blocked", reason: "restart-unavailable" });
    }
    const selectedOtaPolicy =
      selected.kind === "ota" ? this.options.ota.getPolicy() : null;
    const selectedNativePolicy =
      selected.kind === "native" ? this.options.native.getPolicy() : null;
    const selectedDownloadKey = this.downloadKey();
    return this.options.transition.runTransition(async () => {
      // 参考移动端，在最终安装/重启前重新核对策略；准备期间撤回或替换的目标不能继续安装。
      await Promise.all([
        this.options.native.refreshOnForeground(),
        this.options.ota.refreshOnForeground(),
      ]);
      this.recompute();
      const safety = await this.readSafetyDecision();
      if (
        !this.selectionMatches(
          selected,
          selectedNativePolicy,
          selectedOtaPolicy,
        )
      ) {
        return Object.freeze({
          action: "blocked",
          reason: "selection-changed",
        });
      }
      if (!safety.canRestart) {
        return Object.freeze({
          action: "blocked",
          reason: safety.reason,
        });
      }
      if (selected.kind === "native") {
        if (!selectedNativePolicy) {
          return Object.freeze({
            action: "blocked",
            reason: "selection-changed",
          });
        }
        if (
          !this.selectionMatches(
            selected,
            selectedNativePolicy,
            selectedOtaPolicy,
          )
        ) {
          return Object.freeze({
            action: "blocked",
            reason: "selection-changed",
          });
        }
        if (selected.platform === "iOS") {
          const target = selected.appStoreUrl;
          if (!target) {
            return Object.freeze({
              action: "blocked",
              reason: "restart-unavailable",
            });
          }
          await this.options.appStore.open(target);
          return Object.freeze({
            action: "open-app-store",
            url: target,
          });
        }
        if (
          selected.platform === "Android" &&
          this.options.androidNative
        ) {
          try {
            await this.options.androidNative.install(selectedNativePolicy);
          } catch (error) {
            if (!isAndroidInstallPermissionRequiredError(error)) {
              this.invalidatePreparedDownload(selectedDownloadKey);
            }
            throw error;
          }
          return Object.freeze({ action: "install-android-apk" });
        }
        return Object.freeze({
          action: "blocked",
          reason: "restart-unavailable",
        });
      }
      if (!selectedOtaPolicy || selectedOtaPolicy.state === "none") {
        return Object.freeze({
          action: "blocked",
          reason: "selection-changed",
        });
      }
      let result: ExpoOtaUpdateApplyResult;
      try {
        result = await this.options.ota.apply(
          selectedOtaPolicy,
          async () => {
            if (
              !this.selectionMatches(
                selected,
                selectedNativePolicy,
                selectedOtaPolicy,
              )
            ) {
              return "selection-changed";
            }
            const finalSafety = await this.readSafetyDecision();
            if (
              !this.selectionMatches(
                selected,
                selectedNativePolicy,
                selectedOtaPolicy,
              )
            ) {
              return "selection-changed";
            }
            return finalSafety.canRestart ? true : "restart-unsafe";
          },
        );
      } catch (error) {
        this.invalidatePreparedDownload(selectedDownloadKey);
        throw error;
      }
      if (result.state !== "reloaded" && result.reason !== "selection-changed" &&
        result.reason !== "restart-unsafe") {
        this.invalidatePreparedDownload(selectedDownloadKey);
      }
      return Object.freeze({ action: "ota", result });
    });
  }

  /** 保留设置组合现有窄接口；实际动作仍由独立 OTA 状态机执行。 */
  public async restartIfSafe(): Promise<AppUpdateRestartDecision> {
    const action = await this.performSelectedUpdate();
    if (
      action.action === "ota" &&
      action.result.state === "reloaded"
    ) {
      return Object.freeze({ canRestart: true, reason: null });
    }
    if (action.action === "blocked") {
      return Object.freeze({
        canRestart: false,
        reason:
          action.reason === "selection-changed"
            ? "restart-unavailable"
            : action.reason,
      });
    }
    return Object.freeze({
      canRestart: false,
      reason: "restart-unavailable",
    });
  }

  public dispose(): void {
    this.disposed = true;
    this.unsubscribeNative();
    this.unsubscribeOta();
    this.unsubscribeTransition();
    this.gateListeners.clear();
    this.presentationListeners.clear();
  }

  private refresh(
    reason: AppUpdateRefreshReason,
  ): Promise<AppUpdatePresentation> {
    if (this.refreshInFlight) return this.refreshInFlight;
    const operation = this.refreshOnce(reason).finally(() => {
      if (this.refreshInFlight === operation) this.refreshInFlight = null;
    });
    this.refreshInFlight = operation;
    return operation;
  }

  private async refreshOnce(
    reason: AppUpdateRefreshReason,
  ): Promise<AppUpdatePresentation> {
    const nativeRefresh =
      reason === "startup"
        ? this.options.native.refreshOnStartup()
        : reason === "foreground"
          ? this.options.native.refreshOnForeground()
          : this.options.native.refreshOnNetworkAvailable();
    const otaRefresh =
      reason === "startup"
        ? this.options.ota.refreshOnStartup()
        : reason === "foreground"
          ? this.options.ota.refreshOnForeground()
          : this.options.ota.refreshOnNetworkAvailable();
    await Promise.all([nativeRefresh, otaRefresh]);
    this.recompute();
    // 不等待大文件下载，启动、联网恢复和收银页面继续工作。
    void this.prepareSelectedUpdate();
    if (this.presentation.requirement === "required") {
      return this.refreshSafety();
    }
    return this.presentation;
  }

  private async refreshSafetyOnce(): Promise<AppUpdatePresentation> {
    const selected = this.presentation;
    if (selected.requirement !== "required") return selected;
    const decision = await this.readSafetyDecision();
    if (
      this.presentation.key !== selected.key ||
      this.presentation.requirement !== "required"
    ) {
      return this.presentation;
    }
    this.safeSelectionKey = selected.key;
    this.safeForCompletion = decision.canRestart;
    this.recompute();
    return this.presentation;
  }

  private async readSafetyDecision(): Promise<AppUpdateRestartDecision> {
    try {
      return decideAppUpdateRestart(
        await this.options.safety.getSafetySnapshot(),
      );
    } catch {
      return Object.freeze({
        canRestart: false,
        reason: "invalid-safety-snapshot",
      });
    }
  }

  private selectionMatches(
    selected: AppUpdatePresentation,
    selectedNativePolicy: PosHandheldUpdatePolicy | null,
    selectedOtaPolicy: PosHandheldOtaUpdatePolicy | null,
  ): boolean {
    if (!samePresentation(this.presentation, selected)) return false;
    if (selected.kind === "native") {
      return sameNativePolicy(
        this.options.native.getPolicy(),
        selectedNativePolicy,
      );
    }
    if (selected.kind !== "ota") return true;
    return sameOtaPolicy(
      this.options.ota.getPolicy(),
      selectedOtaPolicy,
    );
  }

  private recompute(): void {
    const next = chooseAppUpdatePresentation(
      this.options.native.getPolicy(),
      this.options.ota.getPolicy(),
      this.options.installedVersion,
    );
    if (next.key !== this.safeSelectionKey) {
      this.safeSelectionKey = null;
      this.safeForCompletion = null;
    }
    const downloadKey = this.downloadKey(next);
    const downloadState = downloadKey === null ? undefined
      : this.preparingKey === downloadKey ? "downloading"
      : this.preparedKey === downloadKey ? "ready"
      : this.failedKey === downloadKey ? "failed" : "idle";
    this.presentation =
      next.requirement === "required" &&
      this.safeSelectionKey === next.key &&
      this.safeForCompletion === true
        ? Object.freeze({
            ...next,
            downloadState,
            downloadTargetKey: downloadKey ?? undefined,
            phase: "blocking",
            blocking: true,
          })
        : Object.freeze({
            ...next, downloadState, downloadTargetKey: downloadKey ?? undefined,
          });
    this.recomputeGate();
    for (const listener of this.gateListeners) this.notifyGate(listener);
    for (const listener of this.presentationListeners) {
      this.notifyPresentation(listener);
    }
  }

  private downloadKey(selected = this.presentation): string | null {
    if (selected.kind === "ota") {
      return JSON.stringify(["ota", this.options.ota.getPolicy()]);
    }
    if (selected.kind === "native" && selected.platform === "Android") {
      return JSON.stringify(["apk", this.options.native.getPolicy()]);
    }
    return null;
  }

  private invalidatePreparedDownload(key: string | null): void {
    if (key === null || key !== this.downloadKey()) return;
    this.preparedKey = null;
    this.failedKey = key;
    this.recompute();
  }

  private notifyGate(listener: GateListener): void {
    try {
      listener(this.getGate());
    } catch {
      // 单个 route 订阅故障不能改变交易准入。
    }
  }

  private recomputeGate(): void {
    const next = this.calculateGate();
    if (!sameGate(this.gate, next)) this.gate = next;
  }

  private notifyPresentation(listener: PresentationListener): void {
    try {
      listener(this.presentation);
    } catch {
      // 单个 UI bridge 订阅故障不能改变更新策略。
    }
  }
}

export function chooseAppUpdatePresentation(
  nativePolicy: PosHandheldUpdatePolicy | null,
  otaPolicy: PosHandheldOtaUpdatePolicy | null,
  _installedVersion: string,
): AppUpdatePresentation {
  if (nativePolicy === null) {
    return hiddenPresentation("unchecked");
  }
  if (nativePolicy.state === "required") {
    return nativePresentation(nativePolicy, "required");
  }
  const matchingOtaPolicy =
    otaPolicy?.platform === nativePolicy.platform ? otaPolicy : null;
  if (matchingOtaPolicy?.state === "required") {
    return otaPresentation(matchingOtaPolicy, "required");
  }
  if (matchingOtaPolicy === null) {
    return hiddenPresentation("unchecked");
  }
  if (nativePolicy.state === "optional") {
    return nativePresentation(nativePolicy, "optional");
  }
  if (matchingOtaPolicy.state === "optional") {
    return otaPresentation(matchingOtaPolicy, "optional");
  }
  return hiddenPresentation("hidden");
}

function nativePresentation(
  policy: PosHandheldUpdatePolicy,
  requirement: "optional" | "required",
): AppUpdatePresentation {
  const version = policy.latestVersion ?? policy.minimumSupportedVersion ?? "unknown";
  return Object.freeze({
    key: `native:${policy.platform}:${requirement}:${policy.policyVersion}:${version}`,
    kind: "native",
    requirement,
    phase: requirement === "required" ? "waiting-for-safe" : "prompt",
    blocking: false,
    releaseMessage: policy.releaseMessage,
    platform: policy.platform,
    appStoreUrl: policy.platform === "iOS" ? policy.downloadUrl : null,
    downloadUrl: policy.platform === "Android" ? policy.downloadUrl : null,
  });
}

function otaPresentation(
  policy: Extract<PosHandheldOtaUpdatePolicy, { state: "optional" | "required" }>,
  requirement: "optional" | "required",
): AppUpdatePresentation {
  return Object.freeze({
    key: `ota:${policy.platform}:${requirement}:${policy.policyVersion}:${policy.updateId}`,
    kind: "ota",
    requirement,
    phase: requirement === "required" ? "waiting-for-safe" : "prompt",
    blocking: false,
    releaseMessage: policy.releaseMessage,
    platform: policy.platform,
    appStoreUrl: null,
    downloadUrl: null,
  });
}

function hiddenPresentation(
  phase: "unchecked" | "hidden",
): AppUpdatePresentation {
  return Object.freeze({
    key: phase,
    kind: "none",
    requirement: null,
    phase,
    blocking: false,
    releaseMessage: null,
    platform: null,
    appStoreUrl: null,
    downloadUrl: null,
  });
}

function requiredGate(
  state: "force-update" | "ota-update",
): NewTransactionGate {
  return Object.freeze({
    state,
    canStartNewTransaction: false,
    canContinueRecovery: true,
  });
}

function samePresentation(
  left: AppUpdatePresentation,
  right: AppUpdatePresentation,
): boolean {
  return (
    left.key === right.key &&
    left.kind === right.kind &&
    left.requirement === right.requirement &&
    left.phase === right.phase &&
    left.blocking === right.blocking &&
    left.releaseMessage === right.releaseMessage &&
    left.platform === right.platform &&
    left.appStoreUrl === right.appStoreUrl &&
    left.downloadUrl === right.downloadUrl
  );
}

function sameGate(left: NewTransactionGate, right: NewTransactionGate): boolean {
  return (
    left.state === right.state &&
    left.canStartNewTransaction === right.canStartNewTransaction &&
    left.canContinueRecovery === right.canContinueRecovery
  );
}

function sameNativePolicy(
  left: PosHandheldUpdatePolicy | null,
  right: PosHandheldUpdatePolicy | null,
): boolean {
  if (left === null || right === null) return left === right;
  return (
    left.state === right.state &&
    left.policyVersion === right.policyVersion &&
    left.platform === right.platform &&
    left.required === right.required &&
    left.latestVersion === right.latestVersion &&
    left.latestBuild === right.latestBuild &&
    left.minimumSupportedVersion === right.minimumSupportedVersion &&
    left.distribution === right.distribution &&
    left.downloadUrl === right.downloadUrl &&
    left.fileSize === right.fileSize &&
    left.sha256 === right.sha256 &&
    left.packageName === right.packageName &&
    left.signingCertificateSha256 === right.signingCertificateSha256 &&
    left.bundleIdentifier === right.bundleIdentifier &&
    left.appStoreId === right.appStoreId &&
    left.releaseMessage === right.releaseMessage
  );
}

function sameOtaPolicy(
  left: PosHandheldOtaUpdatePolicy | null,
  right: PosHandheldOtaUpdatePolicy | null,
): boolean {
  if (left === null || right === null) return left === right;
  return (
    left.state === right.state &&
    left.policyVersion === right.policyVersion &&
    left.appKey === right.appKey &&
    left.projectName === right.projectName &&
    left.platform === right.platform &&
    left.required === right.required &&
    left.channel === right.channel &&
    left.runtimeVersion === right.runtimeVersion &&
    left.updateId === right.updateId &&
    left.updateGroupId === right.updateGroupId &&
    left.releaseMessage === right.releaseMessage
  );
}
