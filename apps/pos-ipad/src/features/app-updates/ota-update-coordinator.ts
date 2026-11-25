import type { AppUpdateRefreshReason } from "./app-update-coordinator";
import type {
  ExpoOtaBeforeDownloadDecision,
  ExpoOtaUpdateApplyResult,
} from "./expo-ota-update-port";
import type {
  PosIpadOtaUpdateClientMetadata,
  PosIpadOtaUpdatePolicyRemotePort,
} from "./hbpos-pos-ipad-ota-update-api";

import {
  POS_IPAD_OTA_NONE_POLICY,
  normalizePosIpadOtaUpdatePolicy,
  type PosIpadOtaUpdatePolicy,
  type PosIpadOtaUpdatePolicyStorePort,
} from "@/core/contracts/ota-app-updates";

export type OtaUpdateRefreshResult = Readonly<{
  reason: AppUpdateRefreshReason;
  source: "remote" | "memory" | "cache" | "unchecked" | "disabled";
  policy: PosIpadOtaUpdatePolicy | null;
}>;

export type OtaUpdateCoordinatorOptions = Readonly<{
  automaticChecksEnabled: boolean;
  metadata: PosIpadOtaUpdateClientMetadata;
  policyStore: PosIpadOtaUpdatePolicyStorePort;
  remote: PosIpadOtaUpdatePolicyRemotePort;
  installer?: Readonly<{
    apply(
      policy: PosIpadOtaUpdatePolicy,
      beforeDownload?: () =>
        | ExpoOtaBeforeDownloadDecision
        | Promise<ExpoOtaBeforeDownloadDecision>,
    ): Promise<ExpoOtaUpdateApplyResult>;
  }>;
}>;

export type OtaUpdatePolicyListener = (
  policy: PosIpadOtaUpdatePolicy | null,
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
  private policy: PosIpadOtaUpdatePolicy | null;
  private inFlight: Promise<OtaUpdateRefreshResult> | null = null;
  private installInFlight: Promise<ExpoOtaUpdateApplyResult> | null = null;
  private readonly listeners = new Set<OtaUpdatePolicyListener>();

  public constructor(private readonly options: OtaUpdateCoordinatorOptions) {
    this.policy = options.automaticChecksEnabled
      ? null
      : POS_IPAD_OTA_NONE_POLICY;
  }

  public getPolicy(): PosIpadOtaUpdatePolicy | null {
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
    if (!this.options.automaticChecksEnabled) {
      return Promise.resolve(
        Object.freeze({
          reason,
          source: "disabled",
          policy: POS_IPAD_OTA_NONE_POLICY,
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
    policy: PosIpadOtaUpdatePolicy,
    beforeDownload: () =>
      | ExpoOtaBeforeDownloadDecision
      | Promise<ExpoOtaBeforeDownloadDecision>,
  ): Promise<ExpoOtaUpdateApplyResult> {
    if (this.installInFlight) return this.installInFlight;
    const selected = normalizePosIpadOtaUpdatePolicy(policy);
    const operation = this.applyOnce(selected, beforeDownload).finally(() => {
      if (this.installInFlight === operation) this.installInFlight = null;
    });
    this.installInFlight = operation;
    return operation;
  }

  private async applyOnce(
    policy: PosIpadOtaUpdatePolicy,
    beforeDownload: () =>
      | ExpoOtaBeforeDownloadDecision
      | Promise<ExpoOtaBeforeDownloadDecision>,
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
    return this.options.installer.apply(policy, beforeDownload);
  }

  private async refreshOnce(
    reason: AppUpdateRefreshReason,
  ): Promise<OtaUpdateRefreshResult> {
    try {
      const policy = normalizePosIpadOtaUpdatePolicy(
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

  private async readCachedPolicy(): Promise<PosIpadOtaUpdatePolicy | null> {
    try {
      const cached = await this.options.policyStore.get();
      return cached === null
        ? null
        : normalizePosIpadOtaUpdatePolicy(cached);
    } catch {
      return null;
    }
  }

  private setPolicy(policy: PosIpadOtaUpdatePolicy | null): void {
    this.policy = policy;
    for (const listener of this.listeners) this.notify(listener);
  }

  private notify(listener: OtaUpdatePolicyListener): void {
    try {
      listener(this.policy);
    } catch {
      // UI 订阅故障不得改变策略、缓存或其他订阅者。
    }
  }
}
