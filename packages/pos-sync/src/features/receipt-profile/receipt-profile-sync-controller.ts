/**
 * 总部「下发」小票资料的收银端同步控制器（手持 / iPad 共用，纯 TS，无原生依赖）。
 *
 * 契约（门店小票资料下发 §4）：
 * 1. 用本机已应用的 profileVersion 调 GET receipt-profile/sync；
 * 2. changed=true：校验返回的资料后一次性原子写入本机，再回执 ack；
 * 3. changed=false 且本机版本高于已回执版本：补发 ack；
 * 4. 返回资料的门店与本机绑定门店不一致：丢弃，防串店；
 * 5. 失败只记日志、不弹窗，离线继续使用本机最后一次应用的资料。
 *
 * 触发点（设备就绪、回前台、网络恢复、每 60 秒）由壳层沿用既有生命周期钩子调用
 * `requestSync`；这里只负责「同一时刻只有一个同步在飞」「404 退避 10 分钟」
 * 「同一版本 ack 被拒后本进程不再重试」「失败日志只在状态变化时记一次」。
 *
 * 日志里只出现版本号、触发源、失败原因与 HTTP 状态码，绝不写入地址、电话、ABN 等资料内容。
 *
 * 认证说明：sync / ack 只要求设备认证（设备头由通用 transport 附加），不要求收银员票据，
 * 也不要求「设置小票打印机」权限——没人登录、或当前收银员没有该权限时，后台轮询同样应成功。
 * 因此 401/403 表示设备认证本身有问题（例如设备被撤销或尚未授权），而不是账号权限问题：
 * 这里归为 unauthorized，只在状态变化时记一次日志，不弹窗、不影响本机资料，下一轮继续重试。
 */

export const RECEIPT_PROFILE_SYNC_INTERVAL_MS = 60_000;
/** 服务端还没部署 sync 接口（404）时，退避到每 10 分钟再试一次，避免每分钟刷 404 日志。 */
export const RECEIPT_PROFILE_UNSUPPORTED_BACKOFF_MS = 10 * 60_000;

export type ReceiptProfileSyncTrigger =
  | "startup"
  | "foreground"
  | "network"
  | "timer"
  | "manual";

/** 服务端下发的一版资料；version 由 API 适配器容错归一（缺失按 0），由本控制器拒绝无效版本。 */
export type ReceiptProfileSnapshot = Readonly<{
  version: number;
  storeCode: string;
  storeName: string;
  brandName: string;
  address: string;
  phone: string;
  abn: string;
  returnPolicy: string;
}>;

export type ReceiptProfileSyncResponse = Readonly<{
  changed: boolean;
  version: number;
  profile: ReceiptProfileSnapshot | null;
}>;

export type ReceiptProfileLocalState = Readonly<{
  /** 已应用的下发版本；0 = 从未应用过下发。 */
  profileVersion: number;
  /** 已成功回执的版本。 */
  profileAckedVersion: number;
}>;

/**
 * applied：已原子写入；rejected：资料没通过本机与「现有保存」同口径的校验，未写入任何内容。
 * 存储层的 I/O 故障以抛错表示（下一轮重试），与 rejected（同一版本不再重试）区分开。
 */
export type ReceiptProfileApplyOutcome = "applied" | "rejected";

export interface ReceiptProfileSyncApiPort {
  sync(knownVersion: number, signal: AbortSignal): Promise<ReceiptProfileSyncResponse>;
  ack(version: number, signal: AbortSignal): Promise<void>;
}

export interface ReceiptProfileSyncStorePort {
  read(): Promise<ReceiptProfileLocalState>;
  /** 必须一次性原子写入 6 个字段 + 版本 + 绑定门店代码。 */
  apply(profile: ReceiptProfileSnapshot): Promise<ReceiptProfileApplyOutcome>;
  /** 仅当本机当前版本仍等于 version 时才写入已回执版本（比较并设置）。 */
  markAcked(version: number): Promise<void>;
}

export type ReceiptProfileSyncLogEvent = Readonly<{
  level: "Information" | "Warning";
  message: string;
  /** 仅限版本号、触发源、失败原因、HTTP 状态码等非敏感标量。 */
  properties: Readonly<Record<string, string | number | boolean>>;
}>;

export type ReceiptProfileSyncFailureReason =
  | "offline"
  | "unauthorized"
  | "unsupported"
  | "server-error"
  | "invalid-response"
  | "invalid-profile"
  | "store-mismatch"
  | "storage"
  | "unknown";

export type ReceiptProfileSyncResult =
  | Readonly<{ status: "updated"; version: number }>
  | Readonly<{ status: "up-to-date"; version: number }>
  | Readonly<{ status: "not-published" }>
  | Readonly<{
      status: "skipped";
      reason: "not-ready" | "backoff" | "disposed";
    }>
  | Readonly<{ status: "failed"; reason: ReceiptProfileSyncFailureReason }>;

export type ReceiptProfileSyncControllerOptions = Readonly<{
  api: ReceiptProfileSyncApiPort;
  store: ReceiptProfileSyncStorePort;
  /** 设备已认证后的可信绑定门店代码；设备尚未就绪时返回 null。 */
  boundStoreCode(): string | null;
  /** 毫秒时间戳，注入以便测试退避。 */
  now(): number;
  log?: (event: ReceiptProfileSyncLogEvent) => void;
}>;

export class ReceiptProfileSyncController {
  private inFlight: Promise<ReceiptProfileSyncResult> | null = null;
  private readonly lifetime = new AbortController();
  /** 404 退避截止时间（毫秒时间戳）；0 = 未退避。 */
  private unsupportedUntil = 0;
  /** 本进程内服务端拒绝过回执的版本（400），同一版本不再重试，避免死循环。 */
  private readonly ackRejectedVersions = new Set<number>();
  /** 本进程内本机校验不通过的版本，不再重复写入、重复记日志。 */
  private readonly rejectedVersions = new Set<number>();
  /**
   * 上一次已记录的失败签名；同一签名连续出现只记一次，对应环节成功后清空。
   * 同步请求与回执分开计，避免「同步成功但回执一直失败」每分钟刷一条日志。
   */
  private lastSyncFailureKey: string | null = null;
  private lastAckFailureKey: string | null = null;

  public constructor(private readonly options: ReceiptProfileSyncControllerOptions) {}

  /**
   * 后台触发（启动 / 回前台 / 网络恢复 / 定时）。同一时刻只允许一个同步在飞：
   * 已有同步在跑时直接复用它的结果。永不 reject。
   */
  public requestSync(
    trigger: Exclude<ReceiptProfileSyncTrigger, "manual">,
  ): Promise<ReceiptProfileSyncResult> {
    if (this.inFlight) return this.inFlight;
    return this.start(trigger);
  }

  /**
   * 设置页「立即同步」：等在途同步结束后再跑一轮新的（保证结果足够新），
   * 且无视 404 退避（用户明确要求现在就查）。永不 reject。
   */
  public async syncNow(): Promise<ReceiptProfileSyncResult> {
    while (this.inFlight) {
      await this.inFlight;
    }
    return this.start("manual");
  }

  /** runtime 关闭时中止在途请求，之后的触发一律跳过。 */
  public dispose(): void {
    this.lifetime.abort();
  }

  private start(trigger: ReceiptProfileSyncTrigger): Promise<ReceiptProfileSyncResult> {
    const run = this.run(trigger)
      .catch((): ReceiptProfileSyncResult => ({ status: "failed", reason: "unknown" }))
      .finally(() => {
        if (this.inFlight === run) this.inFlight = null;
      });
    this.inFlight = run;
    return run;
  }

  private async run(trigger: ReceiptProfileSyncTrigger): Promise<ReceiptProfileSyncResult> {
    if (this.lifetime.signal.aborted) return { status: "skipped", reason: "disposed" };
    const boundStoreCode = this.options.boundStoreCode()?.trim() ?? "";
    if (boundStoreCode === "") return { status: "skipped", reason: "not-ready" };
    const manual = trigger === "manual";
    if (!manual && this.options.now() < this.unsupportedUntil) {
      return { status: "skipped", reason: "backoff" };
    }

    let local: ReceiptProfileLocalState;
    try {
      local = await this.options.store.read();
    } catch {
      return this.fail("storage", trigger);
    }

    let response: ReceiptProfileSyncResponse;
    try {
      response = await this.options.api.sync(local.profileVersion, this.lifetime.signal);
    } catch (error) {
      if (this.lifetime.signal.aborted) return { status: "skipped", reason: "disposed" };
      return this.failFromRequestError(error, trigger);
    }
    // 服务端已经支持 sync：解除 404 退避，恢复 60 秒节奏。
    this.unsupportedUntil = 0;

    if (response.changed === true) {
      return this.applyChanged(response, boundStoreCode, trigger);
    }

    // 服务端无变化：从未下发（或快照表不存在）时不覆盖本机手工设置，也无从回执。
    if (!(response.version > 0)) {
      this.lastSyncFailureKey = null;
      return { status: "not-published" };
    }
    // 服务端声称有已下发版本却说「无变化」，而本机从未应用过：响应自相矛盾，丢弃。
    if (local.profileVersion <= 0) return this.fail("invalid-response", trigger);
    this.lastSyncFailureKey = null;
    // 上次写入后回执失败的重试。
    if (local.profileVersion > local.profileAckedVersion) {
      await this.acknowledge(local.profileVersion);
    }
    return { status: "up-to-date", version: local.profileVersion };
  }

  private async applyChanged(
    response: ReceiptProfileSyncResponse,
    boundStoreCode: string,
    trigger: ReceiptProfileSyncTrigger,
  ): Promise<ReceiptProfileSyncResult> {
    const profile = response.profile;
    if (!profile || !Number.isSafeInteger(profile.version) || profile.version <= 0) {
      // 版本缺失或 ≤ 0 视为无效响应：丢弃，不写入。
      return this.fail("invalid-response", trigger);
    }
    if (profile.storeCode.trim().toLowerCase() !== boundStoreCode.toLowerCase()) {
      // 防串店：返回资料不属于本机绑定门店。
      return this.fail("store-mismatch", trigger, { version: profile.version });
    }
    if (this.rejectedVersions.has(profile.version)) {
      // 同一版本已判定不合规，等总部下发新版本，不重复写入。
      return { status: "failed", reason: "invalid-profile" };
    }

    let outcome: ReceiptProfileApplyOutcome;
    try {
      // 统一用本机绑定的门店代码落盘，避免大小写差异让可信读取路径误判换店而清空资料。
      outcome = await this.options.store.apply({ ...profile, storeCode: boundStoreCode });
    } catch {
      return this.fail("storage", trigger, { version: profile.version });
    }
    if (outcome === "rejected") {
      this.rejectedVersions.add(profile.version);
      return this.fail("invalid-profile", trigger, { version: profile.version });
    }

    this.lastSyncFailureKey = null;
    this.log("Information", "Receipt profile applied.", {
      version: profile.version,
      trigger,
    });
    await this.acknowledge(profile.version);
    return { status: "updated", version: profile.version };
  }

  /**
   * 回执失败不影响「已更新」的结论：
   * 400 = 服务端不认这个版本，本进程内不再重试；其余（401/403/网络/5xx）下一轮按
   * `profileAckedVersion < profileVersion` 自然重试。
   */
  private async acknowledge(version: number): Promise<void> {
    if (this.ackRejectedVersions.has(version)) return;
    try {
      await this.options.api.ack(version, this.lifetime.signal);
    } catch (error) {
      if (this.lifetime.signal.aborted) return;
      const status = httpStatusOf(error);
      if (status === 400) {
        this.ackRejectedVersions.add(version);
        this.log("Warning", "Receipt profile acknowledgement was rejected.", {
          version,
          status,
        });
        return;
      }
      this.logAckFailureOnce(
        `ack-failed:${String(status ?? "")}:${version}`,
        "Receipt profile acknowledgement failed.",
        "Information",
        { version, ...(status === undefined ? {} : { status }) },
      );
      return;
    }
    try {
      await this.options.store.markAcked(version);
    } catch {
      // 回执已送达但本机没记上：下一轮会再发一次，服务端按单调不降处理，幂等。
      this.logAckFailureOnce(
        `ack-persist-failed:${version}`,
        "Receipt profile acknowledgement could not be saved locally.",
        "Warning",
        { version },
      );
      return;
    }
    this.lastAckFailureKey = null;
  }

  private failFromRequestError(
    error: unknown,
    trigger: ReceiptProfileSyncTrigger,
  ): ReceiptProfileSyncResult {
    const status = httpStatusOf(error);
    if (status === 404) {
      this.unsupportedUntil = this.options.now() + RECEIPT_PROFILE_UNSUPPORTED_BACKOFF_MS;
      return this.fail("unsupported", trigger, { status });
    }
    if (status === 401 || status === 403) return this.fail("unauthorized", trigger, { status });
    if (status === 400) return this.fail("invalid-profile", trigger, { status });
    if (status !== undefined && (status >= 500 || status === 408 || status === 429)) {
      return this.fail("server-error", trigger, { status });
    }
    if (status === undefined && kindOf(error) === "transport") {
      return this.fail("offline", trigger);
    }
    return this.fail("unknown", trigger, status === undefined ? {} : { status });
  }

  private fail(
    reason: ReceiptProfileSyncFailureReason,
    trigger: ReceiptProfileSyncTrigger,
    extra: Readonly<Record<string, string | number | boolean>> = {},
  ): ReceiptProfileSyncResult {
    // 离线与服务器未升级是常态，记 Information；unauthorized（设备认证问题）沿用同一级别，
    // 其余异常记 Warning。无论哪种都只在状态变化时记一次，不弹窗。
    const expected =
      reason === "offline" || reason === "unauthorized" || reason === "unsupported";
    const key = `${reason}:${String(extra.status ?? "")}:${String(extra.version ?? "")}`;
    if (this.lastSyncFailureKey !== key) {
      this.lastSyncFailureKey = key;
      this.log(expected ? "Information" : "Warning", "Receipt profile sync failed.", {
        reason,
        trigger,
        ...extra,
      });
    }
    return { status: "failed", reason };
  }

  private logAckFailureOnce(
    key: string,
    message: string,
    level: ReceiptProfileSyncLogEvent["level"],
    properties: Readonly<Record<string, string | number | boolean>>,
  ): void {
    if (this.lastAckFailureKey === key) return;
    this.lastAckFailureKey = key;
    this.log(level, message, properties);
  }

  private log(
    level: ReceiptProfileSyncLogEvent["level"],
    message: string,
    properties: Readonly<Record<string, string | number | boolean>>,
  ): void {
    try {
      this.options.log?.({ level, message, properties });
    } catch {
      // 日志旁路故障不能影响同步。
    }
  }
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
