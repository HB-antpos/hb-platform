import type { PromotionSnapshotLoadResult } from "./promotion-snapshot-loader";

/**
 * 触发源：登录后立即一次；回前台、联网恢复、前台常驻定时器由壳层调用。
 * 与小票资料同步的触发模式一致（对齐 WPF 收银端的定时促销刷新）。
 */
export type PromotionRefreshTrigger =
  | "login"
  | "foreground"
  | "network"
  | "timer";

/**
 * 总部下发的促销。结构上等同目录模块的 CatalogPromotion；这里只声明需要的字段，
 * 避免销售运行时层反向依赖目录模块。
 */
export type RefreshablePromotion = Readonly<{
  promotionId: string;
  definitionJson: string;
  validFromIso: string | null;
  validUntilIso: string | null;
  priority: number;
}>;

/** 本机 active 目录快照里当前门店的促销（只读投影）。 */
export type StoredPromotionRules = Readonly<{
  snapshotId: string;
  promotions: readonly Readonly<{
    promotionId: string;
    definitionJson: string;
  }>[];
}>;

export type PromotionRefreshResult =
  /** 远端规则与本机不同（或上次装入失败）：已落库并装入购物车。 */
  | Readonly<{ status: "applied"; ruleCount: number }>
  /** 远端规则与本机一致，什么都没动（购物车也不会被重算）。 */
  | Readonly<{ status: "unchanged" }>
  /** 已落库，但购物车正忙（付款中等）没能装入；下一轮会重试。 */
  | Readonly<{ status: "deferred" }>
  | Readonly<{
      status: "skipped";
      reason: "no-session" | "no-active-snapshot" | "catalog-busy" | "in-flight";
    }>
  | Readonly<{ status: "failed" }>;

export type PromotionRefreshLogEvent = Readonly<{
  kind: "applied" | "deferred" | "failed";
  trigger: PromotionRefreshTrigger;
  ruleCount?: number;
  error?: unknown;
}>;

export type PromotionBackgroundRefreshPorts = Readonly<{
  /** 当前已登录收银员所属门店；未登录、锁屏或会话与本机不符时返回 null。 */
  currentStoreCode(): string | null;
  /** 目录下载 / 重置进行中：让路，由它自己刷新促销。 */
  isCatalogRefreshRunning(): boolean;
  /** 拉取本店当前有效的促销规则（网络请求，应支持 AbortSignal）。 */
  fetchRemote(
    input: Readonly<{ storeCode: string; signal: AbortSignal }>,
  ): Promise<readonly RefreshablePromotion[]>;
  loadStored(storeCode: string): Promise<StoredPromotionRules | null>;
  replaceStored(
    snapshotId: string,
    promotions: readonly RefreshablePromotion[],
  ): Promise<void>;
  /** 把本机 active 快照里的促销装入活动购物车（与登录、目录下载后装入是同一个入口）。 */
  applyToCart(
    input: Readonly<{ storeCode: string; asOfIso: string }>,
  ): Promise<PromotionSnapshotLoadResult>;
  /**
   * 与目录刷新协调器互斥地执行「落库 + 装入」。目录刷新在途时不执行并返回
   * { ran: false }，绝不排队等待。
   */
  tryRunExclusive<T>(
    operation: () => Promise<T>,
  ): Promise<Readonly<{ ran: true; value: T }> | Readonly<{ ran: false }>>;
  nowIso(): string;
  log?(event: PromotionRefreshLogEvent): void;
}>;

/** 单次拉取预算（对齐 WPF 的 20 秒），超时视为失败，下一轮再试。 */
const DEFAULT_FETCH_BUDGET_MS = 20_000;

/**
 * 后台只刷新促销规则，不碰商品目录。
 *
 * 为什么需要：iPad 过去只在「手动下载商品目录」时才拉促销规则，总部新建 / 修改 / 停用促销后
 * 收银端要有人点下载才会生效；WPF 则有定时刷新。这里补齐同样的能力。
 *
 * 安全边界：
 * - 单飞：同一时刻只跑一轮，其余触发直接跳过；永不 reject。
 * - 与目录刷新互斥：目录在刷新时让路；落库 + 装入放在协调器的独占区里，避免写到正被替换的快照。
 * - 远端与本机一致时什么都不做，不会反复重算购物车；
 * - 落库后装入购物车失败（付款中 / 购物车忙）会标记待装入，下一轮即使远端没变也重试。
 */
export class PromotionBackgroundRefresher {
  private inFlight: Promise<PromotionRefreshResult> | null = null;
  /** 已落库但还没成功装进购物车：下一轮要重试装入。 */
  private pendingApply = false;
  /**
   * 上一次已记日志的异常状态。服务端故障或长时间付款时每分钟都会失败 / 延后，
   * 只在「进入」该状态时记一条，恢复正常后重新武装，避免日志被刷满。
   */
  private loggedAbnormal: "failed" | "deferred" | null = null;

  public constructor(
    private readonly ports: PromotionBackgroundRefreshPorts,
    private readonly options: Readonly<{ fetchBudgetMs?: number }> = {},
  ) {}

  public request(
    trigger: PromotionRefreshTrigger,
  ): Promise<PromotionRefreshResult> {
    if (this.inFlight !== null) {
      return Promise.resolve({ status: "skipped", reason: "in-flight" });
    }
    const run: Promise<PromotionRefreshResult> = this.run(trigger)
      .catch((error: unknown): PromotionRefreshResult => {
        this.log({ kind: "failed", trigger, error });
        return { status: "failed" };
      })
      .finally(() => {
        if (this.inFlight === run) this.inFlight = null;
      });
    this.inFlight = run;
    return run;
  }

  private async run(
    trigger: PromotionRefreshTrigger,
  ): Promise<PromotionRefreshResult> {
    const storeCode = this.ports.currentStoreCode();
    if (storeCode === null) return { status: "skipped", reason: "no-session" };
    if (this.ports.isCatalogRefreshRunning()) {
      return { status: "skipped", reason: "catalog-busy" };
    }

    // 网络请求放在独占区之外，独占区只做本机落库与装入，持锁时间很短。
    const remote = await this.fetchWithBudget(storeCode);
    const stored = await this.ports.loadStored(storeCode);
    if (stored === null) {
      return { status: "skipped", reason: "no-active-snapshot" };
    }
    if (
      signature(remote) === signature(stored.promotions) &&
      !this.pendingApply
    ) {
      return { status: "unchanged" };
    }

    const exclusive = await this.ports.tryRunExclusive(() =>
      this.persistAndApply(storeCode, remote, trigger),
    );
    return exclusive.ran
      ? exclusive.value
      : { status: "skipped", reason: "catalog-busy" };
  }

  private async persistAndApply(
    storeCode: string,
    remote: readonly RefreshablePromotion[],
    trigger: PromotionRefreshTrigger,
  ): Promise<PromotionRefreshResult> {
    // 进入独占区后再读一次：目录下载可能刚激活了新快照，写入必须落在当前 active 快照上。
    const current = await this.ports.loadStored(storeCode);
    if (current === null) {
      return { status: "skipped", reason: "no-active-snapshot" };
    }
    if (signature(remote) !== signature(current.promotions)) {
      await this.ports.replaceStored(current.snapshotId, remote);
    }

    const loaded = await this.ports.applyToCart({
      storeCode,
      asOfIso: this.ports.nowIso(),
    });
    if (loaded.status === "loaded") {
      this.pendingApply = false;
      this.log({ kind: "applied", trigger, ruleCount: loaded.ruleCount });
      return { status: "applied", ruleCount: loaded.ruleCount };
    }
    // 购物车忙（付款中）或快照暂不可读：规则已落库，留待下一轮装入，绝不清空旧折扣。
    this.pendingApply = true;
    this.log({ kind: "deferred", trigger });
    return { status: "deferred" };
  }

  private async fetchWithBudget(
    storeCode: string,
  ): Promise<readonly RefreshablePromotion[]> {
    const controller = new AbortController();
    const timer = setTimeout(
      () => controller.abort(),
      this.options.fetchBudgetMs ?? DEFAULT_FETCH_BUDGET_MS,
    );
    try {
      return await this.ports.fetchRemote({
        storeCode,
        signal: controller.signal,
      });
    } finally {
      clearTimeout(timer);
    }
  }

  private log(event: PromotionRefreshLogEvent): void {
    if (event.kind === "applied") {
      // 规则真正变化并装入才会走到这里，本身就很少，每次都记；同时重新武装异常日志。
      this.loggedAbnormal = null;
    } else {
      if (this.loggedAbnormal === event.kind) return;
      this.loggedAbnormal = event.kind;
    }
    try {
      this.ports.log?.(event);
    } catch {
      // 日志失败绝不能影响收银。
    }
  }
}

/**
 * 规则集合签名：按 id + 规整后的定义 JSON 排序拼接。两侧的 definitionJson 都由同一套归一化生成
 * （远端归一化后原样落库），所以逐字节比较即可判断规则是否变化。
 *
 * 唯一要额外规整的是商品明细的顺序：服务端读取明细没有显式排序，同一条规则的商品在两次请求里
 * 顺序可能不同；顺序不影响定价，却会被误判成「规则变了」，让购物车每轮都被重算一次。
 */
function signature(
  promotions: readonly Readonly<{
    promotionId: string;
    definitionJson: string;
  }>[],
): string {
  return promotions
    .map(
      (promotion) =>
        `${promotion.promotionId}\u0000${canonicalDefinition(promotion.definitionJson)}`,
    )
    .sort()
    .join("\u0001");
}

function canonicalDefinition(definitionJson: string): string {
  try {
    const definition = JSON.parse(definitionJson) as {
      products?: readonly { productCode?: unknown }[];
    } & Record<string, unknown>;
    if (Array.isArray(definition.products)) {
      const products = [...definition.products].sort((left, right) =>
        String(left?.productCode).localeCompare(String(right?.productCode)),
      );
      // 展开时 products 键保持原位置，其余字段顺序不变。
      return JSON.stringify({ ...definition, products });
    }
  } catch {
    // 不是合法 JSON：退回原文比较，宁可多刷新一次也不能漏掉变化。
  }
  return definitionJson;
}
