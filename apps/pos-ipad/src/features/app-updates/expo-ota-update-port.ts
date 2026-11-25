import {
  normalizePosIpadOtaUpdatePolicy,
  type PosIpadOtaUpdatePolicy,
} from "@/core/contracts/ota-app-updates";

export type ExpoOtaUpdateManifest = Readonly<{
  id?: unknown;
  runtimeVersion?: unknown;
  metadata?: unknown;
  updateGroupId?: unknown;
}>;

export interface ExpoOtaUpdatesRuntimePort {
  setUpdateRequestHeadersOverride(
    headers: Record<string, string> | null,
  ): void;
  checkForUpdateAsync(): Promise<
    Readonly<{
      isAvailable: boolean;
      manifest?: ExpoOtaUpdateManifest | undefined;
    }>
  >;
  fetchUpdateAsync(): Promise<
    Readonly<{
      isNew: boolean;
      manifest?: ExpoOtaUpdateManifest | undefined;
    }>
  >;
  // 刻意不暴露 reloadAsync：见 ExpoOtaUpdatePort 类注释。
}

export type ExpoOtaUpdateApplyResult =
  // 已下载并通过 manifest 核验，等下一次冷启动由 expo-updates 自动加载。
  | Readonly<{ state: "downloaded"; reason: null }>
  | Readonly<{
      state: "unavailable";
      reason:
        | "no-update"
        | "updates-disabled"
        | "not-available"
        | "not-new";
    }>
  | Readonly<{
      state: "rejected";
      reason:
        | "runtime-mismatch"
        | "update-id-mismatch"
        | "selection-changed"
        | "restart-unsafe"
        | "manifest-invalid"
        | "channel-override-failed"
        | "channel-clear-failed"
        | "update-check-failed";
    }>;

export type ExpoOtaBeforeDownloadDecision =
  | true
  | "selection-changed"
  | "restart-unsafe";

export type ExpoOtaUpdatePortOptions = Readonly<{
  enabled: boolean;
  runtimeVersion: string | null;
  updates: ExpoOtaUpdatesRuntimePort;
}>;

/**
 * channel override 只在后台策略命中后短暂生效；check 与 fetch 返回的
 * runtimeVersion/update id 都必须匹配策略，任一不一致都不能落地。
 *
 * 关键：这里只下载、不在进程内 reload。expo-modules-core 3.0.30（SDK 54）在
 * Updates.reloadAsync() 拆掉旧 Hermes 运行时后，SharedObjectRegistry 才在后台队列里
 * 异步析构挂在旧运行时上的 jsi::WeakObject，必然访问已释放内存而闪退
 * （2026-11-25 build 18 真机两次 EXC_BAD_ACCESS）。下载完成的更新由 expo-updates
 * 在下一次冷启动自动加载，冷启动没有旧运行时可拆，不会触发这个竞态。
 * 下载一旦完成就会在下次冷启动生效，所以安全与选择确认放在 fetch 之前。
 */
export class ExpoOtaUpdatePort {
  public constructor(private readonly options: ExpoOtaUpdatePortOptions) {
    if (!options.enabled) return;
    try {
      // request header override 会被 expo-updates 持久化；冷启动先清掉上次进程遗留的分店 channel。
      options.updates.setUpdateRequestHeadersOverride(null);
    } catch {
      // 原生模块暂不可用时不能让应用启动失败；NEVER 仍阻止后台自行检查遗留 channel。
    }
  }

  public async apply(
    input: PosIpadOtaUpdatePolicy,
    beforeDownload: () =>
      | ExpoOtaBeforeDownloadDecision
      | Promise<ExpoOtaBeforeDownloadDecision> = () => true,
  ): Promise<ExpoOtaUpdateApplyResult> {
    const policy = normalizePosIpadOtaUpdatePolicy(input);
    if (policy.state === "none") {
      return Object.freeze({ state: "unavailable", reason: "no-update" });
    }
    if (!this.options.enabled) {
      return Object.freeze({
        state: "unavailable",
        reason: "updates-disabled",
      });
    }
    if (
      this.options.runtimeVersion === null ||
      this.options.runtimeVersion !== policy.runtimeVersion
    ) {
      return Object.freeze({
        state: "rejected",
        reason: "runtime-mismatch",
      });
    }

    let downloaded = false;
    let stopResult: Exclude<
      ExpoOtaUpdateApplyResult,
      Readonly<{ state: "downloaded"; reason: null }>
    > | null = null;
    let channelClearFailed = false;
    try {
      try {
        this.options.updates.setUpdateRequestHeadersOverride({
          "expo-channel-name": policy.channel,
        });
      } catch {
        // 原生 setter 可能先持久化再抛错，因此仍须进入 finally 尝试清理。
        stopResult = Object.freeze({
          state: "rejected",
          reason: "channel-override-failed",
        });
      }

      if (stopResult === null) {
        const checked = await this.options.updates.checkForUpdateAsync();
        if (!checked.isAvailable) {
          stopResult = Object.freeze({
            state: "unavailable",
            reason: "not-available",
          });
        } else {
          const checkedManifest = verifyManifest(
            checked.manifest,
            policy.runtimeVersion,
            policy.iosUpdateId,
          );
          if (checkedManifest) {
            stopResult = checkedManifest;
          } else {
            // fetch 完成即等于「下次冷启动生效」，所以最后一次安全 / 选择确认必须在 fetch 之前。
            const approval = beforeDownload();
            const selectionStillCurrent = isPromiseLike(approval)
              ? await approval
              : approval;
            if (selectionStillCurrent !== true) {
              stopResult = Object.freeze({
                state: "rejected",
                reason: selectionStillCurrent,
              });
            }
          }
          if (stopResult === null) {
            const fetched = await this.options.updates.fetchUpdateAsync();
            if (!fetched.isNew) {
              stopResult = Object.freeze({
                state: "unavailable",
                reason: "not-new",
              });
            } else {
              const fetchedManifest = verifyManifest(
                fetched.manifest,
                policy.runtimeVersion,
                policy.iosUpdateId,
              );
              if (fetchedManifest) {
                stopResult = fetchedManifest;
              } else {
                downloaded = true;
              }
            }
          }
        }
      }
    } catch {
      // 原生 check/fetch 异常也转为稳定失败，确保 finally 的 channel 清理结果可以优先裁决。
      stopResult = Object.freeze({
        state: "rejected",
        reason: "update-check-failed",
      });
    } finally {
      // 避免一次分店定向检查把 channel 泄漏到后续策略或其他登录门店。
      try {
        this.options.updates.setUpdateRequestHeadersOverride(null);
      } catch {
        channelClearFailed = true;
      }
    }

    if (channelClearFailed) {
      // 未确认持久化 override 已清除前不能报成功，避免跨分店复用 channel。
      return Object.freeze({
        state: "rejected",
        reason: "channel-clear-failed",
      });
    }
    if (stopResult !== null) {
      return stopResult;
    }

    if (downloaded) {
      return Object.freeze({ state: "downloaded", reason: null });
    }
    return Object.freeze({
      state: "rejected",
      reason: "manifest-invalid",
    });
  }
}

function isPromiseLike<T>(
  value: T | Promise<T>,
): value is Promise<T> {
  return (
    typeof value === "object" &&
    value !== null &&
    "then" in value &&
    typeof value.then === "function"
  );
}

function verifyManifest(
  manifest: ExpoOtaUpdateManifest | undefined,
  expectedRuntimeVersion: string,
  expectedUpdateId: string,
): Extract<ExpoOtaUpdateApplyResult, { state: "rejected" }> | null {
  if (!manifest || typeof manifest !== "object") {
    return Object.freeze({
      state: "rejected",
      reason: "manifest-invalid",
    });
  }
  if (manifest.runtimeVersion !== expectedRuntimeVersion) {
    return Object.freeze({
      state: "rejected",
      reason: "runtime-mismatch",
    });
  }
  if (
    typeof manifest.id !== "string" ||
    manifest.id.toLowerCase() !== expectedUpdateId
  ) {
    return Object.freeze({
      state: "rejected",
      reason: "update-id-mismatch",
    });
  }
  return null;
}
