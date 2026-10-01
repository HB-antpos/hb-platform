import type { AppUpdateRefreshReason } from "./app-update-coordinator";
import type {
  ExpoOtaBeforeReloadDecision,
  ExpoOtaUpdateApplyResult,
  ExpoOtaUpdatePrepareResult,
} from "./expo-ota-update-port";
import type {
  PosHandheldOtaUpdateClientMetadata,
  PosHandheldOtaUpdatePolicyRemotePort,
} from "./hbpos-pos-handheld-ota-update-api";

import {
  createPosHandheldOtaNonePolicy,
  normalizePosHandheldOtaUpdatePolicy,
  type PosHandheldOtaUpdatePolicy,
  type PosHandheldOtaUpdatePolicyStorePort,
} from "@/core/contracts/ota-app-updates";
import type { DeviceSystem } from "@/core/contracts/security";

export type OtaUpdateRefreshResult = Readonly<{
  reason: AppUpdateRefreshReason;
  source: "remote" | "memory" | "cache" | "unchecked" | "disabled";
  policy: PosHandheldOtaUpdatePolicy | null;
}>;

export type OtaUpdateCoordinatorOptions = Readonly<{
  platform: DeviceSystem;
  automaticChecksEnabled: boolean;
  metadata: PosHandheldOtaUpdateClientMetadata;
  policyStore: PosHandheldOtaUpdatePolicyStorePort;
  remote: PosHandheldOtaUpdatePolicyRemotePort;
  installer?: Readonly<{
    prepare?(policy: PosHandheldOtaUpdatePolicy): Promise<ExpoOtaUpdatePrepareResult>;
    apply(
      policy: PosHandheldOtaUpdatePolicy,
      beforeReload?: () =>
        | ExpoOtaBeforeReloadDecision
        | Promise<ExpoOtaBeforeReloadDecision>,
    ): Promise<ExpoOtaUpdateApplyResult>;
  }>;
}>;

export type OtaUpdatePolicyListener = (
  policy: PosHandheldOtaUpdatePolicy | null,
) => void;

export function shouldCheckOtaPolicy(
  input: Readonly<{
    automaticChecksConfigured: boolean;
    updatesEnabled: boolean;
  }>,
): boolean {
  // 执行器损坏不等于后台没有 required 策略；生产仍必须检查并失败关闭。
  return input.automaticChecksConfigured;
}

/** OTA 策略、缓存与安装状态不复用原生 App Store 状态机。 */
export class OtaUpdateCoordinator {
  private policy: PosHandheldOtaUpdatePolicy | null;
  private inFlight: Promise<OtaUpdateRefreshResult> | null = null;
  private installInFlight: Promise<ExpoOtaUpdateApplyResult> | null = null;
  private readonly listeners = new Set<OtaUpdatePolicyListener>();

  public constructor(private readonly options: OtaUpdateCoordinatorOptions) {
    this.policy = this.isEnabled()
      ? null
      : createPosHandheldOtaNonePolicy(options.platform);
  }

  public getPolicy(): PosHandheldOtaUpdatePolicy | null {
    return this.policy;
  }

  public subscribe(listener: OtaUpdatePolicyListener): () => void {
    this.listeners.add(listener);
    this.notify(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  public refreshOnStartup(): Promise<OtaUpdateRefreshResult> {
    return this.refresh("startup");
  }

  public refreshOnForeground(): Promise<OtaUpdateRefreshResult> {
    return this.refresh("foreground");
  }

  public refreshOnNetworkAvailable(): Promise<OtaUpdateRefreshResult> {
    return this.refresh("network");
  }

  public refresh(
    reason: AppUpdateRefreshReason,
  ): Promise<OtaUpdateRefreshResult> {
    if (!this.isEnabled()) {
      const policy = createPosHandheldOtaNonePolicy(
        this.options.platform,
      );
      return Promise.resolve(
        Object.freeze({
          reason,
          source: "disabled",
          policy,
        }),
      );
    }
    if (this.inFlight) return this.inFlight;
    const operation = this.refreshOnce(reason).finally(() => {
      if (this.inFlight === operation) this.inFlight = null;
    });
    this.inFlight = operation;
    return operation;
  }

  public apply(
    policy: PosHandheldOtaUpdatePolicy,
    beforeReload: () =>
      | ExpoOtaBeforeReloadDecision
      | Promise<ExpoOtaBeforeReloadDecision>,
  ): Promise<ExpoOtaUpdateApplyResult> {
    if (!this.isEnabled()) {
      return Promise.resolve(
        Object.freeze({
          state: "unavailable",
          reason: "updates-disabled",
        }),
      );
    }
    if (this.installInFlight) return this.installInFlight;
    const selected = this.normalizeForPlatform(policy);
    const operation = this.applyOnce(selected, beforeReload).finally(() => {
      if (this.installInFlight === operation) this.installInFlight = null;
    });
    this.installInFlight = operation;
    return operation;
  }

  public prepare(
    policy: PosHandheldOtaUpdatePolicy,
  ): Promise<ExpoOtaUpdatePrepareResult> {
    if (!this.isEnabled()) {
      return Promise.resolve(
        Object.freeze({
          state: "unavailable",
          reason: "updates-disabled",
        }),
      );
    }
    if (!this.options.installer?.prepare) {
      return Promise.resolve(
        Object.freeze({
          state: "unavailable",
          reason: "updates-disabled",
        }),
      );
    }
    const selected = this.normalizeForPlatform(policy);
    return this.options.installer.prepare(selected);
  }

  private async applyOnce(
    policy: PosHandheldOtaUpdatePolicy,
    beforeReload: () =>
      | ExpoOtaBeforeReloadDecision
      | Promise<ExpoOtaBeforeReloadDecision>,
  ): Promise<ExpoOtaUpdateApplyResult> {
    if (!this.options.installer) {
      return Object.freeze({
        state: "unavailable",
        reason: "updates-disabled",
      });
    }
    if (policy.state === "none") {
      return Object.freeze({
        state: "unavailable",
        reason: "no-update",
      });
    }
    return this.options.installer.apply(policy, beforeReload);
  }

  private async refreshOnce(
    reason: AppUpdateRefreshReason,
  ): Promise<OtaUpdateRefreshResult> {
    try {
      const policy = this.normalizeForPlatform(
        await this.options.remote.getPolicy(this.options.metadata),
      );
      this.setPolicy(policy);
      try {
        await this.options.policyStore.save(policy);
      } catch {
        // 远端策略已通过严格校验，缓存失败不能回退或放宽当前状态。
      }
      return Object.freeze({ reason, source: "remote", policy });
    } catch {
      if (this.policy !== null) {
        return Object.freeze({
          reason,
          source: "memory",
          policy: this.policy,
        });
      }
      const cached = await this.readCachedPolicy();
      this.setPolicy(cached);
      return Object.freeze({
        reason,
        source: cached ? "cache" : "unchecked",
        policy: cached,
      });
    }
  }

  private async readCachedPolicy(): Promise<PosHandheldOtaUpdatePolicy | null> {
    try {
      const cached = await this.options.policyStore.get();
      return cached === null
        ? null
        : this.normalizeForPlatform(cached);
    } catch {
      return null;
    }
  }

  private setPolicy(policy: PosHandheldOtaUpdatePolicy | null): void {
    this.policy = policy;
    for (const listener of this.listeners) this.notify(listener);
  }

  private normalizeForPlatform(
    input: unknown,
  ): PosHandheldOtaUpdatePolicy {
    const policy = normalizePosHandheldOtaUpdatePolicy(input);
    if (policy.platform !== this.options.platform) {
      throw new TypeError(
        "Handheld OTA policy platform does not match this device.",
      );
    }
    return policy;
  }

  private notify(listener: OtaUpdatePolicyListener): void {
    try {
      listener(this.policy);
    } catch {
      // UI 订阅故障不得改变策略、缓存或其他订阅者。
    }
  }

  private isEnabled(): boolean {
    return (
      (this.options.platform === "iOS" ||
        this.options.platform === "Android") &&
      this.options.automaticChecksEnabled
    );
  }
}
