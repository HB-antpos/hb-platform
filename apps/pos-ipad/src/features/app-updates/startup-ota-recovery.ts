import type {
  ExpoOtaBeforeReloadDecision,
  ExpoOtaUpdateApplyResult,
} from "./expo-ota-update-port";
import type { OtaUpdateRefreshResult } from "./ota-update-coordinator";

import type {
  PosIpadOtaUpdatePolicy,
  PosIpadOtaUpdatePolicyStorePort,
} from "@/core/contracts/ota-app-updates";

type AvailableOtaPolicy = Extract<
  PosIpadOtaUpdatePolicy,
  { state: "optional" | "required" }
>;

export type StartupOtaRecoveryFailureReason =
  | "check-failed"
  | "selection-changed"
  | "restart-unsafe"
  | Exclude<ExpoOtaUpdateApplyResult, { state: "reloaded" }>["reason"];

export type StartupOtaRecoveryState =
  | Readonly<{ phase: "idle" }>
  | Readonly<{ phase: "checking" }>
  | Readonly<{
      phase: "unavailable";
      reason: "updates-disabled" | "device-unavailable";
    }>
  | Readonly<{ phase: "up-to-date" }>
  | Readonly<{
      phase: "available";
      requirement: "optional" | "required";
      releaseMessage: string | null;
    }>
  | Readonly<{ phase: "applying" }>
  | Readonly<{
      phase: "failed";
      reason: StartupOtaRecoveryFailureReason;
    }>;

export type StartupOtaRecoveryCoordinatorPort = Readonly<{
  refresh(reason: "startup" | "foreground"): Promise<OtaUpdateRefreshResult>;
  apply(
    policy: PosIpadOtaUpdatePolicy,
    beforeReload: () =>
      | ExpoOtaBeforeReloadDecision
      | Promise<ExpoOtaBeforeReloadDecision>,
  ): Promise<ExpoOtaUpdateApplyResult>;
}>;

export type StartupOtaRecoveryOptions = Readonly<{
  /** 与完整 runtime 相同的构建开关；开发/测试包或未配置自动 OTA 时不得出网。 */
  enabled: boolean;
  /** 只读判断 Keychain 中是否有可出站的设备凭据；没有凭据时不发请求。 */
  hasDeviceCredentials(): Promise<boolean>;
  /** 复用既有 OtaUpdateCoordinator：策略合同、runtimeVersion 与 manifest 核验均由它与 Expo 执行端口完成。 */
  coordinator: StartupOtaRecoveryCoordinatorPort;
  /** reload 前最后一次确认 runtime 仍处于失败态（未被"重试"拉起、SQLite 已关闭）。 */
  canReload(): boolean;
}>;

export type StartupOtaRecoveryListener = (
  state: StartupOtaRecoveryState,
) => void;

/**
 * runtime 初始化失败时的最小 OTA 恢复通道。
 *
 * 完整 runtime 的更新编排器依赖 SQLite 策略缓存和收银/支付安全快照，
 * 初始化失败时都不存在；这里只保留"后台受控策略 + Expo 原生核验"两道闸门：
 * - 只接受本次远端实时返回的策略，绝不使用缓存或内存旧策略；
 * - 安装前重新拉取策略，目标被撤回或替换即放弃；
 * - 下载、manifest 校验和 channel 覆盖清理全部沿用 ExpoOtaUpdatePort；
 * - reload 前确认 runtime 仍处于失败态，SQLite 已在失败收尾中关闭。
 * 整个流程不读写本地账本，不清除任何本地数据。
 */
export class StartupOtaRecovery {
  private state: StartupOtaRecoveryState = Object.freeze({ phase: "idle" });
  private policy: AvailableOtaPolicy | null = null;
  private operation: Promise<StartupOtaRecoveryState> | null = null;
  private readonly listeners = new Set<StartupOtaRecoveryListener>();

  public constructor(private readonly options: StartupOtaRecoveryOptions) {}

  public getState(): StartupOtaRecoveryState {
    return this.state;
  }

  public subscribe(listener: StartupOtaRecoveryListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  public check(): Promise<StartupOtaRecoveryState> {
    return this.exclusive(() => this.checkOnce());
  }

  public apply(): Promise<StartupOtaRecoveryState> {
    return this.exclusive(() => this.applyOnce());
  }

  private exclusive(
    run: () => Promise<StartupOtaRecoveryState>,
  ): Promise<StartupOtaRecoveryState> {
    // 检查与安装共用单飞，避免两次点击并发设置原生 channel 覆盖。
    if (this.operation) return this.operation;
    const operation = run().finally(() => {
      if (this.operation === operation) this.operation = null;
    });
    this.operation = operation;
    return operation;
  }

  private async checkOnce(): Promise<StartupOtaRecoveryState> {
    this.policy = null;
    if (!this.options.enabled) {
      return this.setState({ phase: "unavailable", reason: "updates-disabled" });
    }
    this.setState({ phase: "checking" });
    const policy = await this.readRemotePolicy("startup");
    if (policy === "device-unavailable") {
      return this.setState({
        phase: "unavailable",
        reason: "device-unavailable",
      });
    }
    if (policy === null) {
      return this.setState({ phase: "failed", reason: "check-failed" });
    }
    if (policy.state === "none") {
      return this.setState({ phase: "up-to-date" });
    }
    this.policy = policy;
    return this.setState({
      phase: "available",
      requirement: policy.state,
      releaseMessage: policy.releaseMessage,
    });
  }

  private async applyOnce(): Promise<StartupOtaRecoveryState> {
    const selected = this.policy;
    if (!selected || this.state.phase !== "available") {
      return this.state;
    }
    this.setState({ phase: "applying" });
    // 与正常编排器一致：最终安装前重新核对后台策略，撤回或替换的目标不能继续安装。
    const latest = await this.readRemotePolicy("foreground");
    if (latest === "device-unavailable" || latest === null) {
      return this.setState({ phase: "failed", reason: "check-failed" });
    }
    if (latest.state === "none" || !sameTarget(selected, latest)) {
      this.policy = null;
      return this.setState({ phase: "failed", reason: "selection-changed" });
    }
    let result: ExpoOtaUpdateApplyResult;
    try {
      result = await this.options.coordinator.apply(selected, () =>
        this.options.canReload() ? true : "restart-unsafe",
      );
    } catch {
      return this.setState({ phase: "failed", reason: "update-check-failed" });
    }
    if (result.state === "reloaded") {
      // reloadAsync 成功后 JS 即将被替换，保持 applying 避免重复点击。
      return this.state;
    }
    return this.setState({ phase: "failed", reason: result.reason });
  }

  private async readRemotePolicy(
    reason: "startup" | "foreground",
  ): Promise<PosIpadOtaUpdatePolicy | "device-unavailable" | null> {
    try {
      if (!(await this.options.hasDeviceCredentials())) {
        return "device-unavailable";
      }
      const refreshed = await this.options.coordinator.refresh(reason);
      // memory/cache/unchecked 都不是本次实时决策；恢复通道只认远端结果。
      return refreshed.source === "remote" ? refreshed.policy : null;
    } catch {
      return null;
    }
  }

  private setState(
    next: StartupOtaRecoveryState,
  ): StartupOtaRecoveryState {
    const frozen = Object.freeze({ ...next }) as StartupOtaRecoveryState;
    this.state = frozen;
    for (const listener of this.listeners) {
      try {
        listener(frozen);
      } catch {
        // UI 订阅故障不能改变恢复状态机。
      }
    }
    return frozen;
  }
}

/** 恢复通道不落 SQLite：每次都从远端实时取策略，缓存读不到任何旧值。 */
export function createStartupOtaRecoveryPolicyStore(): PosIpadOtaUpdatePolicyStorePort {
  return Object.freeze({
    get: () => Promise.resolve(null),
    save: (policy: PosIpadOtaUpdatePolicy) => Promise.resolve(policy),
  });
}

function sameTarget(
  left: AvailableOtaPolicy,
  right: AvailableOtaPolicy,
): boolean {
  return (
    left.policyVersion === right.policyVersion &&
    left.channel === right.channel &&
    left.runtimeVersion === right.runtimeVersion &&
    left.iosUpdateId === right.iosUpdateId &&
    left.updateGroupId === right.updateGroupId
  );
}
