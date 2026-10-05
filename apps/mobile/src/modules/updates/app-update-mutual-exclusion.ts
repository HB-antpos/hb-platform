export type AppUpdateOwner = "native" | "ota";

export interface AppUpdateOperationLease {
  finish(): void;
}

export interface AppUpdateMutualExclusion {
  tryStartOperation(owner: AppUpdateOwner): AppUpdateOperationLease | null;
  tryOwnPrompt(owner: AppUpdateOwner): boolean;
  releasePrompt(owner: AppUpdateOwner): void;
  activateNativeInstaller(): void;
  clearNativeInstaller(): void;
  canReloadOta(): boolean;
  subscribe(listener: () => void): () => void;
  setOtaInitializationPending(pending: boolean): void;
  setOtaRequiredGate(active: boolean): void;
  isOtaRequiredGateActive(): boolean;
  setNativeRequiredGate(active: boolean): void;
  isNativeRequiredGateActive(): boolean;
}

export interface UpdateLaneRetryGate {
  markBlocked(): void;
  consumeRetry(): boolean;
  clear(): void;
}

export function createUpdateLaneRetryGate(): UpdateLaneRetryGate {
  let blocked = false;
  return {
    markBlocked() {
      blocked = true;
    },
    consumeRetry() {
      if (!blocked) return false;
      blocked = false;
      return true;
    },
    clear() {
      blocked = false;
    },
  };
}

export function createAppUpdateMutualExclusion(options?: Readonly<{
  otaInitializationPending?: boolean;
}>): AppUpdateMutualExclusion {
  let operation: { owner: AppUpdateOwner; generation: number } | null = null;
  let promptOwner: AppUpdateOwner | null = null;
  let nativeInstallerActive = false;
  let otaInitializationPending = options?.otaInitializationPending ?? true;
  let otaRequiredGate = false;
  // 安卓原生强制更新：旧原生包本身无法再承接后续 OTA，因此 required 时原生通道优先，
  // 不再等待 OTA 初始化或 OTA required，同时禁止 OTA 抢占与 reload。
  let nativeRequiredGate = false;
  let generation = 0;
  const listeners = new Set<() => void>();
  const notify = () => {
    for (const listener of listeners) listener();
  };

  return {
    tryStartOperation(owner) {
      if (
        operation
        || nativeInstallerActive
        || (owner === "native" && !nativeRequiredGate && (otaInitializationPending || otaRequiredGate))
        || (owner === "ota" && nativeRequiredGate)
        || (promptOwner !== null && promptOwner !== owner)
      ) {
        return null;
      }
      generation += 1;
      const leaseGeneration = generation;
      operation = { owner, generation: leaseGeneration };
      notify();
      return {
        finish() {
          if (operation?.generation === leaseGeneration) {
            operation = null;
            notify();
          }
        },
      };
    },
    tryOwnPrompt(owner) {
      if (
        nativeInstallerActive
        || (owner === "native" && !nativeRequiredGate && (otaInitializationPending || otaRequiredGate))
        || (owner === "ota" && nativeRequiredGate)
        || (operation !== null && operation.owner !== owner)
        || (promptOwner !== null && promptOwner !== owner)
      ) {
        return false;
      }
      promptOwner = owner;
      notify();
      return true;
    },
    releasePrompt(owner) {
      if (promptOwner === owner) {
        promptOwner = null;
        notify();
      }
    },
    activateNativeInstaller() {
      if (promptOwner === "native") promptOwner = null;
      nativeInstallerActive = true;
      notify();
    },
    clearNativeInstaller() {
      if (nativeInstallerActive) {
        nativeInstallerActive = false;
        notify();
      }
    },
    canReloadOta() {
      return (
        !nativeInstallerActive
        && !nativeRequiredGate
        && (operation === null || operation.owner === "ota")
        && (promptOwner === null || promptOwner === "ota")
      );
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    setOtaInitializationPending(pending) {
      if (otaInitializationPending !== pending) {
        otaInitializationPending = pending;
        notify();
      }
    },
    setOtaRequiredGate(active) {
      if (otaRequiredGate !== active) {
        otaRequiredGate = active;
        notify();
      }
    },
    isOtaRequiredGateActive() {
      return otaRequiredGate;
    },
    setNativeRequiredGate(active) {
      if (nativeRequiredGate !== active) {
        nativeRequiredGate = active;
        notify();
      }
    },
    isNativeRequiredGateActive() {
      return nativeRequiredGate;
    },
  };
}

export const appUpdateMutualExclusion = createAppUpdateMutualExclusion();
