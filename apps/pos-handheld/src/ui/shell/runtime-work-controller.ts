export type RuntimeBackgroundWorkPort = Readonly<{
  sync: Readonly<{
    onApplicationStarted(): Promise<unknown>;
    onForeground(): Promise<unknown>;
    onNetworkChanged(isOnline: boolean): Promise<unknown>;
  }>;
  fulfilment: Readonly<{
    drainAutomaticQueue(): Promise<unknown>;
  }>;
  appUpdates?: Readonly<{
    refreshOnStartup(): Promise<unknown>;
    refreshOnForeground(): Promise<unknown>;
    refreshOnNetworkAvailable(): Promise<unknown>;
  }>;
  /**
   * 总部下发小票资料的后台同步。缺省（设备未就绪、旧组合）时整条链路静默跳过；
   * 实现保证永不 reject，且自身负责单飞、404 退避与失败日志。
   */
  receiptProfileSync?:
    | Readonly<{
        requestSync(
          trigger: "startup" | "foreground" | "network" | "timer",
        ): Promise<unknown>;
      }>
    | undefined;
}>;

/**
 * 把系统生命周期转换为耐久队列触发器。
 *
 * sync 自身按 OrderGuid 单飞；这里另外让外设 drain 单飞，避免快速前后台切换把
 * 同一个 Queued/Required 动作排入多个串行扫描。错误继续交给调用方记录或忽略，
 * 状态机和 SQLCipher 队列仍保留真实恢复事实。
 */
export class RuntimeWorkController {
  private hardwareDrain: Promise<void> | null = null;

  public constructor(private readonly services: RuntimeBackgroundWorkPort) {}

  public onApplicationStarted(): Promise<void> {
    this.requestReceiptProfileSync("startup");
    return this.runWithHardware(
      () => this.services.sync.onApplicationStarted(),
      () => this.services.appUpdates?.refreshOnStartup(),
    );
  }

  public onForeground(): Promise<void> {
    this.requestReceiptProfileSync("foreground");
    return this.runWithHardware(
      () => this.services.sync.onForeground(),
      () => this.services.appUpdates?.refreshOnForeground(),
    );
  }

  public async onNetworkChanged(isOnline: boolean): Promise<void> {
    // 联网（含离线恢复）才去检查总部下发资料；断网不发请求，继续用本机资料。
    if (isOnline) this.requestReceiptProfileSync("network");
    await this.services.sync.onNetworkChanged(isOnline);
    if (isOnline) {
      await this.services.appUpdates?.refreshOnNetworkAvailable();
    }
  }

  /** 前台常驻时每 60 秒由壳层定时器调用一次，让总部下发在一分钟内生效。 */
  public onReceiptProfileTimer(): void {
    this.requestReceiptProfileSync("timer");
  }

  /**
   * 下发资料同步与订单同步、外设队列互不依赖：不等待它，也不让它的异常影响其他触发器。
   * 单飞、退避与日志都在 receiptProfileSync 内部处理。
   */
  private requestReceiptProfileSync(
    trigger: "startup" | "foreground" | "network" | "timer",
  ): void {
    const port = this.services.receiptProfileSync;
    if (!port) return;
    try {
      void Promise.resolve(port.requestSync(trigger)).catch(() => {
        // 同步失败只记日志（控制器内部处理），这里不再重复上抛。
      });
    } catch {
      // 同步入口抛出同步异常时同样不能影响启动、前台与联网流程。
    }
  }

  private async runWithHardware(
    sync: () => Promise<unknown>,
    refreshAppUpdate: () => Promise<unknown> | undefined,
  ): Promise<void> {
    // 先让同步/审计与外设队列到达稳定点，更新门禁才能读取可信安全快照。
    await Promise.all([
      sync(),
      this.drainHardware(),
    ]);
    await refreshAppUpdate();
  }

  private drainHardware(): Promise<void> {
    if (this.hardwareDrain) return this.hardwareDrain;

    const drain = this.services.fulfilment
      .drainAutomaticQueue()
      .then(() => undefined)
      .finally(() => {
        if (this.hardwareDrain === drain) {
          this.hardwareDrain = null;
        }
      });
    this.hardwareDrain = drain;
    return drain;
  }
}
