/**
 * 总部「收银端设置下发」的 iPad / 手持共用同步服务（纯 TS，无原生依赖）。
 *
 * 语义与 WPF `PosSettingsService` 对齐（同一份契约：GET pos-settings/sync + POST pos-settings/ack）：
 * 1. 生效值 = 设备 → 门店 → 全局（服务端合并，客户端只收「已管理」的项）；没出现的键 = 沿用本机值。
 * 2. 「写穿」：总部值写回各开关**原来的本机存储**（由 binding 完成），消费方一行不用改。
 * 3. 锁定 = 总部管理了这一项 && 联网探测在线；离线时可临时修改，恢复联网后被总部值覆盖。
 *    联网状态未知按在线处理（宁可先锁着）。服务端还没部署接口（404）时快照为空，不锁任何项。
 * 4. 离线临时修改：登记覆盖、累计次数（只增不减，服务端取最大值）、写审计 POS_SETTING_OFFLINE_OVERRIDE。
 * 5. 回执：已应用指纹 + 各开关实际值 + 累计离线修改次数。
 *
 * 与 WPF 的差别：iPad / 手持每个开关只有「单点写入」（设置页保存回调），所有用户改动都经过 `trySet`，
 * 所以不需要 WPF 那套「重连时靠旧快照识别绕过修改」的检测。
 *
 * 触发点（设备就绪 / 回前台 / 网络恢复 / 60 秒定时）由壳层沿用既有生命周期钩子调用 `requestSync`；
 * 这里负责单飞、404 退避 10 分钟、同一份回执被 400 拒绝后本进程不再重试、失败日志只在状态变化时记一次。
 * 日志只出现键名、布尔值、指纹前缀、状态码，不出现任何收银员或顾客信息。
 */

/** 可下发键（与 BlazorApp.Shared.Constants.PosSettingKeys 同一份字符串，键一经发布不能改名）。 */
export const POS_SETTING_KEYS = {
  cashMovementEnabled: "cashMovement.enabled",
  manualCard: "payment.manualCard",
  voucher: "payment.voucher",
} as const;

export const POS_SETTINGS_SYNC_INTERVAL_MS = 60_000;
/** 服务端还没部署 sync 接口（404）时退避 10 分钟再试，避免每分钟刷 404 日志。 */
export const POS_SETTINGS_UNSUPPORTED_BACKOFF_MS = 10 * 60_000;
/** 单轮同步（含回执）的时间预算；超时当作失败，下一轮重试。 */
export const POS_SETTINGS_ROUND_BUDGET_MS = 20_000;
/** 审计事件类型（后端 POS API 白名单已登记）。 */
export const POS_SETTING_OFFLINE_OVERRIDE_AUDIT = "POS_SETTING_OFFLINE_OVERRIDE";

export type PosSettingsSyncTrigger = "startup" | "foreground" | "network" | "timer" | "manual";

export type PosSettingsSyncResponse = Readonly<{
  changed: boolean;
  hash: string;
  /** changed=true 时是全部已管理项；changed=false 时为 null。 */
  settings: Readonly<Record<string, Readonly<{ value: boolean; source: string }>>> | null;
}>;

export type PosSettingsAckPayload = Readonly<{
  hash: string;
  applied: Readonly<Record<string, boolean>>;
  appVersion: string | null;
  offlineOverrideCount: number;
}>;

export interface PosSettingsApiPort {
  sync(knownHash: string, signal: AbortSignal): Promise<PosSettingsSyncResponse>;
  ack(payload: PosSettingsAckPayload, signal: AbortSignal): Promise<void>;
}

/** 一个开关的本机存储适配：read 读真实值，write 必须同时刷新消费方使用的内存快照（写穿）。 */
export interface PosSettingBinding {
  readonly key: string;
  read(): Promise<boolean>;
  write(value: boolean): Promise<void>;
}

export type PosSettingStoredValue = Readonly<{ value: boolean; source: string }>;

/** 本机持久快照（整份原子替换）。 */
export type PosSettingsLocalState = Readonly<{
  storeCode: string;
  /** 已应用的指纹；空串 = 从未同步过。 */
  hash: string;
  values: Readonly<Record<string, PosSettingStoredValue>>;
  syncedAtIso: string | null;
  /** 当前快照下被离线改过的键；同步成功后清空（总部值已覆盖）。 */
  overrides: Readonly<Record<string, true>>;
  /** 累计离线修改次数，只增不减。 */
  offlineOverrideCount: number;
  ackedHash: string;
  ackedOverrideCount: number;
}>;

export const EMPTY_POS_SETTINGS_STATE: PosSettingsLocalState = Object.freeze({
  storeCode: "",
  hash: "",
  values: Object.freeze({}),
  syncedAtIso: null,
  overrides: Object.freeze({}),
  offlineOverrideCount: 0,
  ackedHash: "",
  ackedOverrideCount: 0,
});

export interface PosSettingsLocalStorePort {
  /** 损坏或不存在时返回 null（等同未同步过）。 */
  load(): Promise<PosSettingsLocalState | null>;
  save(state: PosSettingsLocalState): Promise<void>;
}

export type PosSettingOfflineOverrideAudit = Readonly<{
  key: string;
  previous: boolean;
  current: boolean;
  managedValue: boolean;
  managedSource: string;
}>;

export interface PosSettingsAuditPort {
  recordOfflineOverride(entry: PosSettingOfflineOverrideAudit): Promise<void>;
}

export type PosSettingState = Readonly<{
  key: string;
  value: boolean;
  /** 总部来源（global / store / device / default）；null = 总部没管这一项。 */
  source: string | null;
  isManaged: boolean;
  isLocked: boolean;
  /** 离线期间本机值与总部值不同；恢复联网后会被覆盖。 */
  isOfflineOverride: boolean;
}>;

export type PosSettingsView = Readonly<{
  items: readonly PosSettingState[];
  isOnline: boolean;
  lastSyncedAtIso: string | null;
  /** 离线且有总部管理项时，设置页显示「离线：可临时修改」黄条。 */
  showsOfflineBanner: boolean;
  hasLockedItems: boolean;
}>;

export type PosSettingChange = Readonly<{ key: string; value: boolean }>;

export type PosSettingsTrySetResult =
  | Readonly<{ outcome: "applied" }>
  | Readonly<{ outcome: "locked"; key: string }>
  | Readonly<{ outcome: "unknown-key"; key: string }>
  | Readonly<{ outcome: "failed" }>;

export type PosSettingsSyncResult =
  | Readonly<{ status: "updated"; hash: string; ackPending: boolean }>
  | Readonly<{ status: "up-to-date"; hash: string; ackPending: boolean }>
  | Readonly<{ status: "skipped"; reason: "not-ready" | "backoff" | "disposed" }>
  | Readonly<{
      status: "failed";
      reason:
        | "offline"
        | "unauthorized"
        | "unsupported"
        | "server-error"
        | "invalid-response"
        | "store-mismatch"
        | "storage"
        | "timeout"
        | "unknown";
    }>;

export type PosSettingsLogEvent = Readonly<{
  level: "Information" | "Warning";
  message: string;
  properties: Readonly<Record<string, string | number | boolean>>;
}>;

export type PosSettingsServiceOptions = Readonly<{
  api: PosSettingsApiPort;
  store: PosSettingsLocalStorePort;
  /** 本端支持的开关；键集合就是回执里上报的键集合，顺序即界面顺序。 */
  bindings: readonly PosSettingBinding[];
  /** 设备认证后的可信绑定门店代码；未就绪返回 null。 */
  boundStoreCode(): string | null;
  appVersion(): string | null;
  now(): number;
  audit?: PosSettingsAuditPort;
  log?: (event: PosSettingsLogEvent) => void;
}>;

export class PosSettingsService {
  private readonly bindings: readonly PosSettingBinding[];
  private readonly bindingByKey: ReadonlyMap<string, PosSettingBinding>;
  private readonly listeners = new Set<() => void>();
  private readonly lifetime = new AbortController();

  private state: PosSettingsLocalState = EMPTY_POS_SETTINGS_STATE;
  private loaded = false;
  private actual: Readonly<Record<string, boolean>> = {};
  private viewReady = false;
  private view: PosSettingsView = EMPTY_VIEW;

  /** null = 联网探测还没出结果，按在线处理。 */
  private online: boolean | null = null;
  /** 启动后、离线过、换店后的第一次成功同步要把总部值核对写一遍。 */
  private needsReapply = true;
  private ackDirty = false;
  private unsupportedUntil = 0;
  private rejectedAckSignature: string | null = null;
  private lastFailureKey: string | null = null;

  private inFlight: Promise<PosSettingsSyncResult> | null = null;
  /** 状态读改写串行化（JS 单线程，但 await 之间会交错）。 */
  private gate: Promise<unknown> = Promise.resolve();

  public constructor(private readonly options: PosSettingsServiceOptions) {
    this.bindings = options.bindings;
    this.bindingByKey = new Map(options.bindings.map((binding) => [binding.key, binding]));
  }

  // ───────────────────────── 读 ─────────────────────────

  /** 同步读取当前视图（界面渲染用）；`load` 之前是空视图。 */
  public getView(): PosSettingsView {
    return this.view;
  }

  public subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /** 读入本机快照与各开关真实值并刷新视图。启动与进入设置页时调用。 */
  public async load(): Promise<PosSettingsView> {
    await this.exclusive(async () => {
      await this.ensureLoaded(this.options.boundStoreCode());
      await this.readActual();
    });
    this.rebuildView();
    return this.view;
  }

  /** 该键此刻是否被总部锁定（保存回调的防御性检查用，与界面灰显同口径）。 */
  public isLocked(key: string): boolean {
    return this.bindingByKey.has(key) && key in this.state.values && (this.online ?? true);
  }

  // ───────────────────────── 本机修改 ─────────────────────────

  /**
   * 用户在设置页改开关。全有或全无：任何一项被锁定就整批拒绝，不写入任何内容。
   * 总部管理的项在离线时放行，并登记覆盖 / 累计次数 / 审计。
   */
  public async trySet(changes: readonly PosSettingChange[]): Promise<PosSettingsTrySetResult> {
    for (const change of changes) {
      if (!this.bindingByKey.has(change.key)) return { outcome: "unknown-key", key: change.key };
    }
    let result: PosSettingsTrySetResult = { outcome: "applied" };
    await this.exclusive(async () => {
      // 必须先把本机状态读进来再判断是否被锁：启动早期状态还没加载时，不能把总部锁定的项当成「没下发」放行。
      await this.ensureLoaded(this.options.boundStoreCode());
      const online = this.online ?? true;
      for (const change of changes) {
        if (change.key in this.state.values && online) {
          result = { outcome: "locked", key: change.key };
          return;
        }
      }
      try {
        for (const change of changes) {
          await this.applyUserChange(change);
        }
      } catch (error) {
        this.log("Warning", "POS setting change failed.", {
          reason: errorName(error),
        });
        result = { outcome: "failed" };
      }
    });
    this.rebuildView();
    return result;
  }

  private async applyUserChange(change: PosSettingChange): Promise<void> {
    const binding = this.bindingByKey.get(change.key)!;
    const previous = await binding.read();
    if (previous === change.value) return;
    await binding.write(change.value);
    this.actual = { ...this.actual, [change.key]: change.value };

    const managed = this.state.values[change.key];
    if (!managed) return;
    // 离线临时修改：登记覆盖、累计次数、写审计。恢复联网后同步成功会被总部值覆盖。
    const next: PosSettingsLocalState = {
      ...this.state,
      overrides: { ...this.state.overrides, [change.key]: true },
      offlineOverrideCount: this.state.offlineOverrideCount + 1,
    };
    await this.options.store.save(next);
    this.state = next;
    this.ackDirty = true;
    await this.recordAudit({
      key: change.key,
      previous,
      current: change.value,
      managedValue: managed.value,
      managedSource: managed.source,
    });
  }

  // ───────────────────────── 联网状态 ─────────────────────────

  public reportConnectivity(isOnline: boolean): void {
    const changed = this.online !== isOnline;
    this.online = isOnline;
    // 离线期间本机可能被改过：恢复联网后的第一次成功同步要把总部值重新核对写一遍。
    if (!isOnline) this.needsReapply = true;
    if (changed) this.rebuildView();
  }

  // ───────────────────────── 同步 ─────────────────────────

  public requestSync(
    trigger: Exclude<PosSettingsSyncTrigger, "manual">,
  ): Promise<PosSettingsSyncResult> {
    if (this.inFlight) return this.inFlight;
    return this.start(trigger);
  }

  /** 设置页「立即同步」：等在途同步结束后再跑新的一轮，且无视 404 退避。永不 reject。 */
  public async syncNow(): Promise<PosSettingsSyncResult> {
    while (this.inFlight) {
      await this.inFlight;
    }
    return this.start("manual");
  }

  public dispose(): void {
    this.lifetime.abort();
    this.listeners.clear();
  }

  private start(trigger: PosSettingsSyncTrigger): Promise<PosSettingsSyncResult> {
    const run = this.runWithBudget(trigger)
      .catch((): PosSettingsSyncResult => ({ status: "failed", reason: "unknown" }))
      .finally(() => {
        if (this.inFlight === run) this.inFlight = null;
      });
    this.inFlight = run;
    return run;
  }

  private async runWithBudget(trigger: PosSettingsSyncTrigger): Promise<PosSettingsSyncResult> {
    if (this.lifetime.signal.aborted) return { status: "skipped", reason: "disposed" };
    const storeCode = this.options.boundStoreCode()?.trim() ?? "";
    if (storeCode === "") return { status: "skipped", reason: "not-ready" };
    const manual = trigger === "manual";
    if (!manual && this.options.now() < this.unsupportedUntil) {
      return { status: "skipped", reason: "backoff" };
    }

    // 本轮预算：到点中止请求，当作失败，下一轮重试。
    const budget = new AbortController();
    const abortOnDispose = (): void => budget.abort();
    this.lifetime.signal.addEventListener("abort", abortOnDispose, { once: true });
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      budget.abort();
    }, POS_SETTINGS_ROUND_BUDGET_MS);
    try {
      return await this.runRound(storeCode, trigger, budget.signal);
    } catch (error) {
      if (this.lifetime.signal.aborted) return { status: "skipped", reason: "disposed" };
      if (timedOut) return this.fail("timeout", trigger);
      throw error;
    } finally {
      clearTimeout(timer);
      this.lifetime.signal.removeEventListener("abort", abortOnDispose);
    }
  }

  private async runRound(
    storeCode: string,
    trigger: PosSettingsSyncTrigger,
    signal: AbortSignal,
  ): Promise<PosSettingsSyncResult> {
    let knownHash = "";
    try {
      await this.exclusive(async () => {
        await this.ensureLoaded(storeCode);
        knownHash = this.state.hash;
      });
    } catch {
      return this.fail("storage", trigger);
    }

    let response: PosSettingsSyncResponse;
    try {
      response = await this.options.api.sync(knownHash, signal);
    } catch (error) {
      if (signal.aborted) throw error;
      return this.failFromRequestError(error, trigger);
    }
    // 接口可用了：解除 404 退避，恢复 60 秒节奏。
    this.unsupportedUntil = 0;

    // 没有指纹的响应无法比较新旧、也无法回执：丢弃，绝不改动本机任何设置。
    if (typeof response.hash !== "string" || response.hash.trim() === "") {
      return this.fail("invalid-response", trigger);
    }
    // 请求期间设备换了店：这是旧店的设置，丢弃。
    if ((this.options.boundStoreCode()?.trim() ?? "") !== storeCode) {
      return this.fail("store-mismatch", trigger);
    }
    const incoming = response.changed ? this.parseSettings(response.settings) : null;
    if (response.changed && incoming === null) return this.fail("invalid-response", trigger);

    let applied: PosSettingsLocalState | null = null;
    let actualAfter: Readonly<Record<string, boolean>> = {};
    let failure: PosSettingsSyncResult | null = null;
    await this.exclusive(async () => {
      const outcome = await this.applyRound(storeCode, response, incoming, trigger);
      if ("status" in outcome) failure = outcome;
      else {
        applied = outcome.state;
        actualAfter = outcome.actual;
      }
    });
    this.rebuildView();
    if (failure) return failure;
    if (!applied) return this.fail("unknown", trigger);
    const state: PosSettingsLocalState = applied;

    this.lastFailureKey = null;
    if (response.changed) {
      this.log("Information", "POS settings applied.", {
        trigger,
        managedKeys: Object.keys(state.values).length,
        hash: state.hash.slice(0, 8),
      });
    }

    let ackPending = false;
    const ackNeeded =
      this.ackDirty ||
      state.hash !== state.ackedHash ||
      state.offlineOverrideCount !== state.ackedOverrideCount;
    if (ackNeeded) ackPending = !(await this.tryAck(state, actualAfter, signal));

    return {
      status: response.changed ? "updated" : "up-to-date",
      hash: state.hash,
      ackPending,
    };
  }

  private async applyRound(
    storeCode: string,
    response: PosSettingsSyncResponse,
    incoming: Readonly<Record<string, PosSettingStoredValue>> | null,
    trigger: PosSettingsSyncTrigger,
  ): Promise<
    | { state: PosSettingsLocalState; actual: Readonly<Record<string, boolean>> }
    | PosSettingsSyncResult
  > {
    const state = this.state;
    // 协议约定指纹不同必然 changed=true；两者矛盾说明响应不可信，不动本机设置。
    if (!response.changed && response.hash !== state.hash) {
      return this.fail("invalid-response", trigger);
    }

    const needReapply =
      this.needsReapply || response.changed || Object.keys(state.overrides).length > 0;
    const target = response.changed ? incoming! : state.values;

    // 以各开关本机存储里真实的值为准。
    await this.readActual();
    const actual: Record<string, boolean> = { ...this.actual };

    if (needReapply) {
      for (const binding of this.bindings) {
        const wanted = target[binding.key];
        // 总部没设置这一项（或已清除）：本机沿用自己的值，不动它。
        if (!wanted) continue;
        if (actual[binding.key] === wanted.value) continue;
        try {
          await binding.write(wanted.value);
          actual[binding.key] = wanted.value;
        } catch (error) {
          // 任何一项写失败都不落盘快照：下一轮仍会重新核对并重试，
          // 不会留下「快照说已应用、本机其实没写」的状态。
          this.actual = actual;
          this.log("Warning", "POS setting write-through failed.", {
            key: binding.key,
            reason: errorName(error),
          });
          return this.fail("storage", trigger);
        }
      }
    }

    const next: PosSettingsLocalState = {
      ...state,
      storeCode,
      hash: response.changed ? response.hash : state.hash,
      values: target,
      syncedAtIso: new Date(this.options.now()).toISOString(),
      overrides: {},
    };
    // 稳态（指纹没变、没有要核对的）只更新内存里的最近同步时间，不每分钟写一次 SQLite。
    if (needReapply || response.changed) {
      try {
        await this.options.store.save(next);
      } catch {
        this.actual = actual;
        return this.fail("storage", trigger);
      }
    }
    this.state = next;
    this.actual = actual;
    if (needReapply) {
      this.needsReapply = false;
      this.ackDirty = true;
    }
    return { state: next, actual };
  }

  /** 回执。返回 true = 服务端已接受。400 = 服务端不认这份回执，本进程内不再重试同一份。 */
  private async tryAck(
    state: PosSettingsLocalState,
    actual: Readonly<Record<string, boolean>>,
    signal: AbortSignal,
  ): Promise<boolean> {
    if (state.hash.trim() === "") return true;
    const signature = `${state.hash}|${state.offlineOverrideCount}`;
    if (this.rejectedAckSignature === signature) return false;

    // 回执只报本端支持的项。
    const applied: Record<string, boolean> = {};
    for (const binding of this.bindings) {
      if (binding.key in actual) applied[binding.key] = actual[binding.key]!;
    }

    try {
      await this.options.api.ack(
        {
          hash: state.hash,
          applied,
          appVersion: this.options.appVersion(),
          offlineOverrideCount: state.offlineOverrideCount,
        },
        signal,
      );
    } catch (error) {
      if (signal.aborted) throw error;
      if (httpStatusOf(error) === 400) {
        this.rejectedAckSignature = signature;
        this.log("Warning", "POS settings acknowledgement was rejected.", {
          hash: state.hash.slice(0, 8),
        });
        return false;
      }
      this.logFailureOnce(`ack:${String(httpStatusOf(error) ?? "")}`, "POS settings acknowledgement failed.");
      return false;
    }

    try {
      await this.exclusive(async () => {
        // 回执期间快照已被替换（又同步到新指纹）时，不能把旧回执记在新指纹头上。
        if (this.state.hash !== state.hash) return;
        const acked: PosSettingsLocalState = {
          ...this.state,
          ackedHash: state.hash,
          ackedOverrideCount: state.offlineOverrideCount,
        };
        await this.options.store.save(acked);
        this.state = acked;
      });
    } catch {
      // 回执已送达但本机没记上：下一轮会再发一次，服务端按单调不降处理，幂等。
      this.logFailureOnce("ack-persist", "POS settings acknowledgement could not be saved locally.");
      return false;
    }
    this.ackDirty = false;
    return true;
  }

  // ───────────────────────── 内部工具 ─────────────────────────

  /** 只保留本机认识的键（比本版本新的服务端可能加了新键，直接忽略）；来源缺失按 global。 */
  private parseSettings(
    settings: PosSettingsSyncResponse["settings"],
  ): Record<string, PosSettingStoredValue> | null {
    if (!settings || typeof settings !== "object") return null;
    const result: Record<string, PosSettingStoredValue> = {};
    for (const [key, dto] of Object.entries(settings)) {
      if (!this.bindingByKey.has(key) || !dto || typeof dto.value !== "boolean") continue;
      const source = typeof dto.source === "string" && dto.source.trim() !== "" ? dto.source.trim() : "global";
      result[key] = { value: dto.value, source };
    }
    return result;
  }

  private async ensureLoaded(storeCode: string | null): Promise<void> {
    if (!this.loaded) {
      this.state = (await this.options.store.load()) ?? EMPTY_POS_SETTINGS_STATE;
      this.loaded = true;
    }
    const code = storeCode?.trim() ?? "";
    if (code !== "" && this.state.storeCode !== code) {
      // 换店（或首次绑定）：旧店的快照、覆盖记录、累计次数都不属于这家店，一并作废；本机各开关保持当前值。
      this.state = { ...EMPTY_POS_SETTINGS_STATE, storeCode: code };
      this.needsReapply = true;
    }
  }

  /** 把各开关真实的本机值读一遍；单项读失败时沿用上一次的值。 */
  private async readActual(): Promise<void> {
    const next: Record<string, boolean> = {};
    for (const binding of this.bindings) {
      try {
        next[binding.key] = await binding.read();
      } catch (error) {
        this.log("Warning", "POS setting read failed.", { key: binding.key, reason: errorName(error) });
        const old = this.actual[binding.key];
        if (old !== undefined) next[binding.key] = old;
      }
    }
    this.actual = next;
    this.viewReady = true;
  }

  private async recordAudit(entry: PosSettingOfflineOverrideAudit): Promise<void> {
    try {
      await this.options.audit?.recordOfflineOverride(entry);
    } catch (error) {
      // 审计写入失败不能影响收银员改开关，也不能让同步失败。
      this.log("Warning", "POS setting audit failed.", { key: entry.key, reason: errorName(error) });
    }
  }

  private rebuildView(): void {
    if (!this.viewReady) return;
    const online = this.online ?? true;
    const items: PosSettingState[] = this.bindings.map((binding) => {
      const value = this.actual[binding.key] === true;
      const stored = this.state.values[binding.key];
      const managed = stored !== undefined;
      return Object.freeze({
        key: binding.key,
        value,
        source: managed ? stored.source : null,
        isManaged: managed,
        isLocked: managed && online,
        isOfflineOverride: managed && value !== stored.value,
      });
    });
    const next: PosSettingsView = Object.freeze({
      items: Object.freeze(items),
      isOnline: online,
      lastSyncedAtIso: this.state.syncedAtIso,
      showsOfflineBanner: !online && items.some((item) => item.isManaged),
      hasLockedItems: items.some((item) => item.isLocked),
    });
    if (viewsEqual(this.view, next)) return;
    this.view = next;
    for (const listener of [...this.listeners]) {
      try {
        listener();
      } catch {
        // 订阅方（界面刷新）出错不能影响同步与修改的结果。
      }
    }
  }

  private failFromRequestError(error: unknown, trigger: PosSettingsSyncTrigger): PosSettingsSyncResult {
    const status = httpStatusOf(error);
    if (status === 404) {
      // 服务端还没部署该接口：视为「暂不支持」；本机各开关沿用自己的值，不锁定任何项。
      this.unsupportedUntil = this.options.now() + POS_SETTINGS_UNSUPPORTED_BACKOFF_MS;
      return this.fail("unsupported", trigger, status);
    }
    if (status === 401 || status === 403) return this.fail("unauthorized", trigger, status);
    if (status !== undefined && (status >= 500 || status === 408 || status === 429)) {
      return this.fail("server-error", trigger, status);
    }
    if (status === undefined && kindOf(error) === "transport") return this.fail("offline", trigger);
    return this.fail("unknown", trigger, status);
  }

  private fail(
    reason: Extract<PosSettingsSyncResult, { status: "failed" }>["reason"],
    trigger: PosSettingsSyncTrigger,
    status?: number,
  ): PosSettingsSyncResult {
    // 离线与服务器未升级是常态，记 Information；只在状态变化时记一次，不弹窗。
    const expected = reason === "offline" || reason === "unauthorized" || reason === "unsupported";
    const key = `${reason}:${String(status ?? "")}`;
    if (this.lastFailureKey !== key) {
      this.lastFailureKey = key;
      this.log(expected ? "Information" : "Warning", "POS settings sync failed.", {
        reason,
        trigger,
        ...(status === undefined ? {} : { status }),
      });
    }
    return { status: "failed", reason };
  }

  private logFailureOnce(key: string, message: string): void {
    if (this.lastFailureKey === key) return;
    this.lastFailureKey = key;
    this.log("Information", message, {});
  }

  private log(
    level: PosSettingsLogEvent["level"],
    message: string,
    properties: Readonly<Record<string, string | number | boolean>>,
  ): void {
    try {
      this.options.log?.({ level, message, properties });
    } catch {
      // 日志旁路故障不能影响同步。
    }
  }

  /** 把一段读改写排进串行队列；前一段失败不阻塞后一段。 */
  private exclusive<T>(work: () => Promise<T>): Promise<T> {
    const run = this.gate.then(work, work);
    this.gate = run.catch(() => undefined);
    return run;
  }
}

const EMPTY_VIEW: PosSettingsView = Object.freeze({
  items: Object.freeze([]),
  isOnline: true,
  lastSyncedAtIso: null,
  showsOfflineBanner: false,
  hasLockedItems: false,
});

function viewsEqual(left: PosSettingsView, right: PosSettingsView): boolean {
  if (
    left.isOnline !== right.isOnline ||
    left.lastSyncedAtIso !== right.lastSyncedAtIso ||
    left.items.length !== right.items.length
  ) {
    return false;
  }
  return left.items.every((item, index) => {
    const other = right.items[index]!;
    return (
      item.key === other.key &&
      item.value === other.value &&
      item.source === other.source &&
      item.isManaged === other.isManaged &&
      item.isLocked === other.isLocked &&
      item.isOfflineOverride === other.isOfflineOverride
    );
  });
}

function errorName(error: unknown): string {
  return error instanceof Error ? error.name : "unknown";
}

function kindOf(error: unknown): string | undefined {
  if (!error || typeof error !== "object") return undefined;
  const kind = (error as { kind?: unknown }).kind;
  return typeof kind === "string" ? kind : undefined;
}

function httpStatusOf(error: unknown): number | undefined {
  if (!error || typeof error !== "object") return undefined;
  if (kindOf(error) !== "http") return undefined;
  const status = (error as { status?: unknown }).status;
  return typeof status === "number" && Number.isInteger(status) ? status : undefined;
}
