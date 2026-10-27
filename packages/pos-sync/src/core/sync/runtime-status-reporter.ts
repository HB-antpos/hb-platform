/**
 * 设备运行状态心跳（对齐 WPF PosRuntimeStatusApiClient，#613）：
 * 后台设备管理页据此显示设备在线与应用版本。服务端对缺失字段保留库里旧值，所以只上报能确定的字段。
 */
export type RuntimeStatusPayload = Readonly<{
  isOnline: boolean;
  appVersion: string | null;
  currentCashierId?: string | null;
  currentCashierName?: string | null;
}>;

export interface RuntimeStatusApiPort {
  reportRuntimeStatus(payload: RuntimeStatusPayload): Promise<void>;
}

export type RuntimeStatusReporterOptions = Readonly<{
  api: RuntimeStatusApiPort;
  /** 应用版本号（含可选的 OTA 标记）；服务端上限 50 字符，超长视为未上报。 */
  appVersion: string;
  /** 当前收银员（可选）；不可用时不带该字段。 */
  getCurrentCashier?: () => Readonly<{ id: string; name: string }> | null;
  /** 时间源（毫秒），便于测试；默认 Date.now。 */
  now?: () => number;
}>;

/** 与服务端 MaxAppVersionLength 对齐：超长直接不上报，避免被丢弃后以为已更新。 */
export const RUNTIME_STATUS_MAX_APP_VERSION_LENGTH = 50;

/** 一般失败（超时、5xx 等）后至少隔这么久再上报，避免对异常的服务端连续施压。 */
export const RUNTIME_STATUS_FAILURE_BACKOFF_MS = 60_000;
/**
 * 服务端明确拒绝（401/403，例如当前收银员无设备登记权限）后的静默期：这种拒绝短时间内不会自愈，
 * 持续上报只会刷服务端日志；收银员换人或下次回前台时由调用方重建/重置。
 */
export const RUNTIME_STATUS_REJECTION_BACKOFF_MS = 10 * 60_000;

export class RuntimeStatusReporter {
  private inFlight: Promise<void> | null = null;
  private suppressedUntilMs = 0;

  public constructor(private readonly options: RuntimeStatusReporterOptions) {}

  /**
   * 上报一次“在线”心跳。同一时刻只保留一个在途请求（定时器、回前台、网络恢复可能同时触发）；
   * 失败只抛给调用方，由调用方吞掉——心跳绝不能影响收银。
   */
  public report(): Promise<void> {
    if (this.inFlight) return this.inFlight;
    if ((this.options.now ?? Date.now)() < this.suppressedUntilMs) {
      return Promise.resolve();
    }
    const running = this.send()
      .catch((error: unknown) => {
        const status = (error as { status?: unknown } | null)?.status;
        const rejected = status === 401 || status === 403;
        this.suppressedUntilMs =
          (this.options.now ?? Date.now)() +
          (rejected
            ? RUNTIME_STATUS_REJECTION_BACKOFF_MS
            : RUNTIME_STATUS_FAILURE_BACKOFF_MS);
        throw error;
      })
      .finally(() => {
        if (this.inFlight === running) this.inFlight = null;
      });
    this.inFlight = running;
    return running;
  }

  private async send(): Promise<void> {
    const version = this.options.appVersion.trim();
    let cashier: Readonly<{ id: string; name: string }> | null = null;
    try {
      cashier = this.options.getCurrentCashier?.() ?? null;
    } catch {
      // 读取收银员失败不影响版本与在线状态上报。
    }
    await this.options.api.reportRuntimeStatus({
      isOnline: true,
      appVersion:
        version && version.length <= RUNTIME_STATUS_MAX_APP_VERSION_LENGTH
          ? version
          : null,
      ...(cashier
        ? { currentCashierId: cashier.id, currentCashierName: cashier.name }
        : {}),
    });
  }
}

/** 版本号标签：原生版本 +（OTA 更新时）更新 ID 前 8 位，便于后台区分同一原生包上的不同 OTA。 */
export function formatRuntimeAppVersion(
  appVersion: string,
  update: Readonly<{ updateId: string | null; isEmbeddedLaunch: boolean }>,
): string {
  const base = appVersion.trim();
  if (!update.isEmbeddedLaunch && update.updateId) {
    return `${base}+ota.${update.updateId.replace(/-/gu, "").slice(0, 8)}`;
  }
  return base;
}
