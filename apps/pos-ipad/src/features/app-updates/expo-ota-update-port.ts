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
        | "channel-binding-failed"
        | "update-check-failed";
    }>;

export type ExpoOtaBeforeDownloadDecision =
  | true
  | "selection-changed"
  | "restart-unsafe";

/** 「本机已下载的 OTA 属于哪个 channel、由哪个原生包下载」的记录。 */
export type ExpoOtaChannelBinding = Readonly<{
  channel: string;
  nativeBuild: string;
}>;

export interface ExpoOtaChannelBindingStore {
  read(): ExpoOtaChannelBinding | null;
  write(binding: ExpoOtaChannelBinding | null): void;
}

export type ExpoOtaUpdatePortOptions = Readonly<{
  enabled: boolean;
  runtimeVersion: string | null;
  updates: ExpoOtaUpdatesRuntimePort;
  /** 本次冷启动生效的 expo-channel-name（Updates.channel，已含持久化的 channel 覆盖）。 */
  launchChannel: string | null;
  /** 当前原生包 build 号（Constants.nativeBuildVersion）。 */
  nativeBuild: string;
  channelBinding: ExpoOtaChannelBindingStore;
}>;

/**
 * channel override 只在后台策略命中后设置；check 与 fetch 返回的
 * runtimeVersion/update id 都必须匹配策略，任一不一致都不能落地。
 *
 * 关键（2026-11-27 build 20 真机「装了还提示新版本」）：expo-updates 29 冷启动只会选
 * 「下载时的请求头 == 当前配置请求头」的更新（LauncherSelectionPolicyFilterAware）。
 * 下载记录里存的是带分店 channel 的请求头，所以：
 * 1. 下载成功后必须保留该 channel 覆盖（它会被 expo-updates 持久化），下次冷启动才能选中；
 *    以前在 finally 里一律清掉，导致下载的更新永远被跳过、一直跑原生包自带版本。
 * 2. 没下载成功时恢复成「当前已安装 OTA 的 channel」，不能清空，否则已装好的 OTA 下次也启动不了。
 * 3. 覆盖会跨 App Store / TestFlight 升级保留；若不处理，旧 OTA 会一直压住新原生包自带的 JS。
 *    所以另外记下「哪个原生包下载的」，启动时原生包变了就清掉覆盖，回到新原生包自带版本。
 *
 * 关键：这里只下载、不在进程内 reload。expo-modules-core 3.0.30（SDK 54）在
 * Updates.reloadAsync() 拆掉旧 Hermes 运行时后，SharedObjectRegistry 才在后台队列里
 * 异步析构挂在旧运行时上的 jsi::WeakObject，必然访问已释放内存而闪退
 * （2026-11-25 build 18 真机两次 EXC_BAD_ACCESS）。下载完成的更新由 expo-updates
 * 在下一次冷启动自动加载，冷启动没有旧运行时可拆，不会触发这个竞态。
 * 下载一旦完成就会在下次冷启动生效，所以安全与选择确认放在 fetch 之前。
 */
export class ExpoOtaUpdatePort {
  /** 下次冷启动应当选中的 OTA 所在 channel；null = 原生包自带版本（不设覆盖）。 */
  private installedChannel: string | null = null;

  public constructor(private readonly options: ExpoOtaUpdatePortOptions) {
    if (!options.enabled) return;
    let binding: ExpoOtaChannelBinding | null = null;
    try {
      binding = options.channelBinding.read();
    } catch {
      // 记录读不出来（例如启动失败恢复页里本地存储也异常）：情况不明时不动覆盖，
      // 按「本次启动所用 channel 就是已安装的」处理，避免误把已生效的 OTA 退回原生包版本。
      this.installedChannel = options.launchChannel;
      return;
    }
    if (
      binding !== null &&
      binding.nativeBuild === options.nativeBuild &&
      binding.channel === options.launchChannel
    ) {
      // 同一个原生包、本次就是按这个 channel 启动的：保留覆盖，已装的 OTA 继续生效。
      this.installedChannel = binding.channel;
      return;
    }
    // 没有有效记录（从未装过 OTA、记录丢失，或已换了新原生包）：清掉遗留覆盖，下次冷启动回到原生包自带版本。
    try {
      options.updates.setUpdateRequestHeadersOverride(null);
    } catch {
      // 原生模块暂不可用时不能让应用启动失败；NEVER 仍阻止后台自行检查遗留 channel。
    }
    if (binding !== null) {
      try {
        options.channelBinding.write(null);
      } catch {
        // 记录清不掉只会让下次启动再清一次覆盖，不影响当前进程。
      }
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
    let channelBindingFailed = false;
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
      if (downloaded) {
        // 下载成功：保留本次 channel 覆盖（冷启动靠它选中这个更新），并记下由哪个原生包下载。
        try {
          this.options.channelBinding.write(
            Object.freeze({
              channel: policy.channel,
              nativeBuild: this.options.nativeBuild,
            }),
          );
          this.installedChannel = policy.channel;
        } catch {
          channelBindingFailed = true;
        }
      }
      if (!downloaded || channelBindingFailed) {
        // 没装成：恢复成已安装 OTA 的 channel（没有则清空），不把本次检查的 channel 泄漏到下次启动。
        try {
          this.options.updates.setUpdateRequestHeadersOverride(
            this.installedChannel === null
              ? null
              : { "expo-channel-name": this.installedChannel },
          );
        } catch {
          channelClearFailed = true;
        }
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
    if (channelBindingFailed) {
      // 记录写不进去，下次启动会被当成「无有效记录」清掉覆盖，更新不会生效，不能报成功。
      return Object.freeze({
        state: "rejected",
        reason: "channel-binding-failed",
      });
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
