import type {
  PosHandheldUpdateClientMetadata,
  PosHandheldUpdatePolicyRemotePort,
} from "./hbpos-pos-handheld-update-api";

import {
  deriveNewTransactionGate,
  normalizePosHandheldUpdatePolicy,
  type NewTransactionGate,
  type PosHandheldUpdatePolicy,
  type PosHandheldUpdatePolicyStorePort,
} from "@/core/contracts/app-updates";

export type AppUpdateRefreshReason = "startup" | "foreground" | "network";

export type AppUpdateRefreshResult = Readonly<{
  reason: AppUpdateRefreshReason;
  source: "remote" | "memory" | "cache" | "unchecked";
  gate: NewTransactionGate;
}>;

export type AppUpdateRestartSafetySnapshot = Readonly<{
  hasActiveCart: boolean;
  hasUnresolvedPayment: boolean;
  hasPendingDurableWrite: boolean;
  hasRecoveryRequired: boolean;
  hasCatalogRefreshInFlight: boolean;
  hasSyncOrAuditInFlight: boolean;
  hasFulfilmentInFlight: boolean;
}>;

export type AppUpdateRestartDecision =
  | Readonly<{ canRestart: true; reason: null }>
  | Readonly<{
      canRestart: false;
      reason:
        | "active-cart"
        | "unresolved-payment"
        | "pending-durable-write"
        | "recovery-required"
        | "catalog-refresh-in-flight"
        | "sync-audit-in-flight"
        | "fulfilment-in-flight"
        | "restart-unavailable"
        | "invalid-safety-snapshot";
    }>;

export interface AppUpdateRestartPort {
  getSafetySnapshot():
    | AppUpdateRestartSafetySnapshot
    | Promise<AppUpdateRestartSafetySnapshot>;
  restart(): Promise<void>;
}

export type AppUpdateCoordinatorOptions = Readonly<{
  updatesEnabled?: boolean;
  metadata: PosHandheldUpdateClientMetadata;
  policyStore: PosHandheldUpdatePolicyStorePort;
  remote: PosHandheldUpdatePolicyRemotePort;
  restart?: AppUpdateRestartPort;
}>;

export type AppUpdateGateListener = (gate: NewTransactionGate) => void;

/**
 * 启动、回到前台和网络恢复都复用同一个检查，防止并发请求交错把较旧策略覆盖为较新策略。
 * 刷新失败时优先保留已验证的内存策略；仅冷启动无策略且缓存不可用时进入 unchecked。
 * 所有状态下恢复流程都不受新交易门禁限制。
 */
export class AppUpdateCoordinator {
  private policy: PosHandheldUpdatePolicy | null = null;
  private gate: NewTransactionGate = deriveNewTransactionGate(null);
  private inFlight: Promise<AppUpdateRefreshResult> | null = null;
  private restartInFlight: Promise<AppUpdateRestartDecision> | null = null;
  private readonly listeners = new Set<AppUpdateGateListener>();

  public constructor(private readonly options: AppUpdateCoordinatorOptions) {}

  public getGate(): NewTransactionGate {
    return this.gate;
  }

  public getPolicy(): PosHandheldUpdatePolicy | null {
    return this.policy;
  }

  public subscribe(listener: AppUpdateGateListener): () => void {
    this.listeners.add(listener);
    this.notify(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  public refreshOnStartup(): Promise<AppUpdateRefreshResult> {
    return this.refresh("startup");
  }

  public refreshOnForeground(): Promise<AppUpdateRefreshResult> {
    return this.refresh("foreground");
  }

  public refreshOnNetworkAvailable(): Promise<AppUpdateRefreshResult> {
    return this.refresh("network");
  }

  public refresh(reason: AppUpdateRefreshReason): Promise<AppUpdateRefreshResult> {
    if (this.inFlight) return this.inFlight;
    const operation = this.refreshOnce(reason).finally(() => {
      if (this.inFlight === operation) this.inFlight = null;
    });
    this.inFlight = operation;
    return operation;
  }

  public restartIfSafe(): Promise<AppUpdateRestartDecision> {
    if (this.restartInFlight) return this.restartInFlight;
    const operation = this.restartOnce().finally(() => {
      if (this.restartInFlight === operation) this.restartInFlight = null;
    });
    this.restartInFlight = operation;
    return operation;
  }

  private async restartOnce(): Promise<AppUpdateRestartDecision> {
    const restart = this.options.restart;
    if (!restart) {
      return Object.freeze({ canRestart: false, reason: "restart-unavailable" });
    }
    let snapshot: AppUpdateRestartSafetySnapshot;
    try {
      snapshot = await restart.getSafetySnapshot();
    } catch {
      return Object.freeze({
        canRestart: false,
        reason: "invalid-safety-snapshot",
      });
    }
    const decision = decideAppUpdateRestart(snapshot);
    if (!decision.canRestart) return decision;
    await restart.restart();
    return decision;
  }

  private async refreshOnce(
    reason: AppUpdateRefreshReason,
  ): Promise<AppUpdateRefreshResult> {
    try {
      const policy = normalizePosHandheldUpdatePolicy(
        await this.options.remote.getPolicy(this.options.metadata),
      );
      this.apply(policy);
      try {
        await this.options.policyStore.save(policy);
      } catch {
        // 本次策略来自已验证的远端响应，缓存失败不应把已知门禁重新放宽。
      }
      return Object.freeze({ reason, source: "remote", gate: this.gate });
    } catch {
      if (this.policy !== null) {
        return Object.freeze({
          reason,
          source: "memory",
          gate: this.gate,
        });
      }
      const cached = await this.readCachedPolicy();
      this.apply(cached);
      return Object.freeze({
        reason,
        source: cached ? "cache" : "unchecked",
        gate: this.gate,
      });
    }
  }

  private async readCachedPolicy(): Promise<PosHandheldUpdatePolicy | null> {
    try {
      const cached = await this.options.policyStore.get();
      return cached === null ? null : normalizePosHandheldUpdatePolicy(cached);
    } catch {
      return null;
    }
  }

  private apply(policy: PosHandheldUpdatePolicy | null): void {
    // 开发包不安装正式发布包；仍读取并保留认证响应中的设备交易权限。
    if (policy && this.options.updatesEnabled === false) {
      policy = Object.freeze({
        enabled: policy.enabled,
        platform: policy.platform,
        state: "none",
        policyVersion: "none",
        required: false,
        latestVersion: null,
        latestBuild: null,
        minimumSupportedVersion: null,
        distribution: null,
        downloadUrl: null,
        fileSize: null,
        sha256: null,
        packageName: null,
        signingCertificateSha256: null,
        bundleIdentifier: null,
        appStoreId: null,
        releaseMessage: null,
      });
    }
    this.policy = policy;
    this.gate = deriveNewTransactionGate(policy);
    for (const listener of this.listeners) {
      this.notify(listener);
    }
  }

  private notify(listener: AppUpdateGateListener): void {
    try {
      listener(this.gate);
    } catch {
      // UI 订阅者故障不能影响全局交易门禁或其他订阅者。
    }
  }
}

export function decideAppUpdateRestart(
  snapshot: AppUpdateRestartSafetySnapshot,
): AppUpdateRestartDecision {
  if (!isSafetySnapshot(snapshot)) {
    return Object.freeze({
      canRestart: false,
      reason: "invalid-safety-snapshot",
    });
  }
  if (snapshot.hasActiveCart) {
    return Object.freeze({ canRestart: false, reason: "active-cart" });
  }
  if (snapshot.hasUnresolvedPayment) {
    return Object.freeze({ canRestart: false, reason: "unresolved-payment" });
  }
  if (snapshot.hasPendingDurableWrite) {
    return Object.freeze({
      canRestart: false,
      reason: "pending-durable-write",
    });
  }
  if (snapshot.hasRecoveryRequired) {
    return Object.freeze({
      canRestart: false,
      reason: "recovery-required",
    });
  }
  if (snapshot.hasCatalogRefreshInFlight) {
    return Object.freeze({
      canRestart: false,
      reason: "catalog-refresh-in-flight",
    });
  }
  if (snapshot.hasSyncOrAuditInFlight) {
    return Object.freeze({
      canRestart: false,
      reason: "sync-audit-in-flight",
    });
  }
  if (snapshot.hasFulfilmentInFlight) {
    return Object.freeze({
      canRestart: false,
      reason: "fulfilment-in-flight",
    });
  }
  return Object.freeze({ canRestart: true, reason: null });
}

function isSafetySnapshot(
  value: unknown,
): value is AppUpdateRestartSafetySnapshot {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return false;
  }
  const snapshot = value as Record<string, unknown>;
  return (
    typeof snapshot.hasActiveCart === "boolean" &&
    typeof snapshot.hasUnresolvedPayment === "boolean" &&
    typeof snapshot.hasPendingDurableWrite === "boolean" &&
    typeof snapshot.hasRecoveryRequired === "boolean" &&
    typeof snapshot.hasCatalogRefreshInFlight === "boolean" &&
    typeof snapshot.hasSyncOrAuditInFlight === "boolean" &&
    typeof snapshot.hasFulfilmentInFlight === "boolean" &&
    Object.keys(snapshot).every((key) =>
      [
        "hasActiveCart",
        "hasUnresolvedPayment",
        "hasPendingDurableWrite",
        "hasRecoveryRequired",
        "hasCatalogRefreshInFlight",
        "hasSyncOrAuditInFlight",
        "hasFulfilmentInFlight",
      ].includes(key),
    )
  );
}
