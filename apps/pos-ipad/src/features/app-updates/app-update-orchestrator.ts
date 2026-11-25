import {
  decideAppUpdateRestart,
  type AppUpdateRefreshReason,
  type AppUpdateRestartDecision,
  type AppUpdateRestartSafetySnapshot,
} from "./app-update-coordinator";
import type {
  ExpoOtaBeforeDownloadDecision,
  ExpoOtaUpdateApplyResult,
} from "./expo-ota-update-port";

import type {
  NewTransactionGate,
  PosIpadUpdatePolicy,
} from "@/core/contracts/app-updates";
import type { PosIpadOtaUpdatePolicy } from "@/core/contracts/ota-app-updates";

export type AppUpdatePresentation = Readonly<{
  key: string;
  kind: "none" | "native" | "ota";
  requirement: "optional" | "required" | null;
  phase:
    | "unchecked"
    | "hidden"
    | "prompt"
    | "waiting-for-safe"
    | "blocking"
    // OTA 已下载并核验，等店员关闭并重新打开 App 由冷启动加载（不在进程内 reload）。
    | "restart-pending";
  blocking: boolean;
  releaseMessage: string | null;
  appStoreUrl: string | null;
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
  | Readonly<{ action: "ota"; result: ExpoOtaUpdateApplyResult }>;

type NativeUpdateCoordinatorPort = Readonly<{
  getPolicy(): PosIpadUpdatePolicy | null;
  getGate(): NewTransactionGate;
  subscribe(listener: (gate: NewTransactionGate) => void): () => void;
  refreshOnStartup(): Promise<unknown>;
  refreshOnForeground(): Promise<unknown>;
  refreshOnNetworkAvailable(): Promise<unknown>;
}>;

type OtaUpdateCoordinatorPort = Readonly<{
  getPolicy(): PosIpadOtaUpdatePolicy | null;
  subscribe(
    listener: (policy: PosIpadOtaUpdatePolicy | null) => void,
  ): () => void;
  refreshOnStartup(): Promise<unknown>;
  refreshOnForeground(): Promise<unknown>;
  refreshOnNetworkAvailable(): Promise<unknown>;
  apply(
    policy: PosIpadOtaUpdatePolicy,
    beforeDownload: () =>
      | ExpoOtaBeforeDownloadDecision
      | Promise<ExpoOtaBeforeDownloadDecision>,
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
  /** 本进程已下载完成的 OTA 展示 key；同一目标不再重复安装，只提示重开。 */
  private downloadedOtaKey: string | null = null;
  private safetyInFlight: Promise<AppUpdatePresentation> | null = null;
  private refreshInFlight: Promise<AppUpdatePresentation> | null = null;
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

  public getPolicy(): PosIpadUpdatePolicy | null {
    return this.options.native.getPolicy();
  }

  public getOtaPolicy(): PosIpadOtaUpdatePolicy | null {
    return this.options.ota.getPolicy();
  }

  public getPresentation(): AppUpdatePresentation {
    return this.presentation;
  }

  public getGate(): NewTransactionGate {
    return this.gate;
  }

  private calculateGate(): NewTransactionGate {
    const nativeGate = this.options.native.getGate();
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
    if (nativeGate.state === "disabled") {
      return Object.freeze({
        state: nativeGate.state,
        canStartNewTransaction: nativeGate.canStartNewTransaction,
        canContinueRecovery: nativeGate.canContinueRecovery,
      });
    }
    if (
      nativeGate.state === "unchecked" ||
      this.options.ota.getPolicy() === null
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

  public async performSelectedUpdate(): Promise<AppUpdateActionResult> {
    const selected = this.presentation;
    if (selected.phase === "unchecked") {
      return Object.freeze({ action: "none", reason: "unchecked" });
    }
    if (selected.kind === "none") {
      return Object.freeze({ action: "none", reason: "no-update" });
    }
    if (selected.phase === "restart-pending") {
      // 已下载过：再点也只是等重开，不重复设置 channel 覆盖或下载。
      return Object.freeze({
        action: "ota",
        result: Object.freeze({ state: "downloaded", reason: null }),
      });
    }
    const selectedOtaPolicy =
      selected.kind === "ota" ? this.options.ota.getPolicy() : null;
    return this.options.transition.runTransition(async () => {
      const safety = await this.readSafetyDecision();
      if (!this.selectionMatches(selected, selectedOtaPolicy)) {
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
        if (!selected.appStoreUrl) {
          return Object.freeze({
            action: "blocked",
            reason: "restart-unavailable",
          });
        }
        if (!this.selectionMatches(selected, selectedOtaPolicy)) {
          return Object.freeze({
            action: "blocked",
            reason: "selection-changed",
          });
        }
        await this.options.appStore.open(selected.appStoreUrl);
        return Object.freeze({
          action: "open-app-store",
          url: selected.appStoreUrl,
        });
      }
      if (!selectedOtaPolicy || selectedOtaPolicy.state === "none") {
        return Object.freeze({
          action: "blocked",
          reason: "selection-changed",
        });
      }
      const result = await this.options.ota.apply(
        selectedOtaPolicy,
        async () => {
          if (!this.selectionMatches(selected, selectedOtaPolicy)) {
            return "selection-changed";
          }
          const finalSafety = await this.readSafetyDecision();
          if (!this.selectionMatches(selected, selectedOtaPolicy)) {
            return "selection-changed";
          }
          return finalSafety.canRestart ? true : "restart-unsafe";
        },
      );
      if (result.state === "downloaded") {
        this.downloadedOtaKey = selected.key;
        this.recompute();
      }
      return Object.freeze({ action: "ota", result });
    });
  }

  /** 保留设置组合现有窄接口；实际动作仍由独立 OTA 状态机执行。 */
  public async restartIfSafe(): Promise<AppUpdateRestartDecision> {
    const action = await this.performSelectedUpdate();
    if (
      action.action === "ota" &&
      action.result.state === "downloaded"
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
    selectedOtaPolicy: PosIpadOtaUpdatePolicy | null,
  ): boolean {
    if (!samePresentation(this.presentation, selected)) return false;
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
    this.presentation =
      next.requirement === "required" &&
      this.safeSelectionKey === next.key &&
      this.safeForCompletion === true
        ? Object.freeze({
            ...next,
            phase: "blocking",
            blocking: true,
          })
        : next;
    if (
      this.presentation.kind === "ota" &&
      this.presentation.key === this.downloadedOtaKey
    ) {
      // 已下载的目标保持「请重开」；强制更新仍全屏阻断，可选更新可收起。
      this.presentation = Object.freeze({
        ...this.presentation,
        phase: "restart-pending",
        blocking: this.presentation.requirement === "required",
      });
    }
    this.recomputeGate();
    for (const listener of this.gateListeners) this.notifyGate(listener);
    for (const listener of this.presentationListeners) {
      this.notifyPresentation(listener);
    }
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
  nativePolicy: PosIpadUpdatePolicy | null,
  otaPolicy: PosIpadOtaUpdatePolicy | null,
  installedVersion: string,
): AppUpdatePresentation {
  if (nativePolicy?.forceUpdate) {
    return nativePresentation(nativePolicy, "required");
  }
  if (otaPolicy?.state === "required") {
    return otaPresentation(otaPolicy, "required");
  }
  if (nativePolicy === null || otaPolicy === null) {
    return hiddenPresentation("unchecked");
  }
  if (isNewerVersion(nativePolicy.latestVersion, installedVersion)) {
    return nativePresentation(nativePolicy, "optional");
  }
  if (otaPolicy.state === "optional") {
    return otaPresentation(otaPolicy, "optional");
  }
  return hiddenPresentation("hidden");
}

function nativePresentation(
  policy: PosIpadUpdatePolicy,
  requirement: "optional" | "required",
): AppUpdatePresentation {
  const version = policy.latestVersion ?? policy.minimumSupportedVersion ?? "unknown";
  return Object.freeze({
    key: `native:${requirement}:${version}`,
    kind: "native",
    requirement,
    phase: requirement === "required" ? "waiting-for-safe" : "prompt",
    blocking: false,
    releaseMessage: policy.releaseMessage,
    appStoreUrl: policy.appStoreUrl,
  });
}

function otaPresentation(
  policy: Extract<PosIpadOtaUpdatePolicy, { state: "optional" | "required" }>,
  requirement: "optional" | "required",
): AppUpdatePresentation {
  return Object.freeze({
    key: `ota:${requirement}:${policy.policyVersion}:${policy.iosUpdateId}`,
    kind: "ota",
    requirement,
    phase: requirement === "required" ? "waiting-for-safe" : "prompt",
    blocking: false,
    releaseMessage: policy.releaseMessage,
    appStoreUrl: null,
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
    appStoreUrl: null,
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

function isNewerVersion(
  latestVersion: string | null,
  installedVersion: string,
): boolean {
  if (!latestVersion) return false;
  const latest = numericVersion(latestVersion);
  const installed = numericVersion(installedVersion);
  if (!latest || !installed) return latestVersion !== installedVersion;
  const length = Math.max(latest.length, installed.length);
  for (let index = 0; index < length; index += 1) {
    const delta = (latest[index] ?? 0) - (installed[index] ?? 0);
    if (delta !== 0) return delta > 0;
  }
  return false;
}

function numericVersion(value: string): readonly number[] | null {
  const normalized = value.trim().replace(/^v/iu, "");
  if (!/^\d+(?:\.\d+){0,3}$/u.test(normalized)) return null;
  return normalized.split(".").map(Number);
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
    left.appStoreUrl === right.appStoreUrl
  );
}

function sameGate(left: NewTransactionGate, right: NewTransactionGate): boolean {
  return (
    left.state === right.state &&
    left.canStartNewTransaction === right.canStartNewTransaction &&
    left.canContinueRecovery === right.canContinueRecovery
  );
}

function sameOtaPolicy(
  left: PosIpadOtaUpdatePolicy | null,
  right: PosIpadOtaUpdatePolicy | null,
): boolean {
  if (left === null || right === null) return left === right;
  return (
    left.state === right.state &&
    left.policyVersion === right.policyVersion &&
    left.channel === right.channel &&
    left.runtimeVersion === right.runtimeVersion &&
    left.iosUpdateId === right.iosUpdateId &&
    left.updateGroupId === right.updateGroupId &&
    left.releaseMessage === right.releaseMessage
  );
}
