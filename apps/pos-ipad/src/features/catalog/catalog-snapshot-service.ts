import type {
  CatalogRefreshProgressEvent,
  CatalogRefreshProgressObserver,
} from "@hb/pos-domain/features/catalog/catalog-refresh-contract";
import type {
  CatalogDeletedLookup,
  CatalogLookupItem,
  VerifiedCatalogSyncPage,
} from "./hbpos-catalog-remote";

import { HbposApiError } from "@/core/api/hbpos-api";

/**
 * 已验证的 Hbpos lookup 行落库前必须显式转换为整数分。
 * 字段保持与 SQLCipher 仓储的输入结构一致，但 feature 不依赖具体 SQLite 实现。
 */
export type CatalogStagedItem = Readonly<{
  storeCode: string;
  productCode: string;
  referenceCode: string | null;
  itemNumber: string | null;
  displayName: string;
  barcode: string | null;
  lookupCode: string;
  lookupCodeNormalized: string;
  retailPriceCents: number;
  priceSource: 0 | 1 | 2 | 3 | 4;
  priceSourceLabel: string;
  quantityFactor: number;
  taxRateBasisPoints: number | null;
  updatedAtIso: string | null;
  rowVersion: string | null;
  productImage: string | null;
  discountRate: number | null;
  isSpecialProduct: boolean;
}>;

export type CatalogPromotion = Readonly<{
  promotionId: string;
  definitionJson: string;
  validFromIso: string | null;
  validUntilIso: string | null;
  priority: number;
}>;

/** 已激活目录的最小身份；门店不匹配时绝不可复用为增量基线。 */
export type ActiveCatalogSnapshotMetadata = Readonly<{
  snapshotId: string;
  /** 同一物理 snapshot 增量激活后用新 generation 隔离 lookup overlay。 */
  generationId?: string | null;
  /** 旧本地快照未必可反推出门店；此时必须全量。 */
  storeCode?: string | null;
  catalogVersion: string;
  itemCount: number;
  activatedAt: string;
}>;

export type CatalogSyncPlan = Readonly<{
  mode: "noChange" | "delta" | "full";
  baseCatalogVersion: string | null;
  targetCatalogVersion: string;
  targetTotal: number;
  downloadLeaseId?: string | null;
  deltaOperationCount?: number | null;
}>;

/** 增量页不伪造全量快照生成时间，但 checksum 必须同时覆盖 upsert 与 delete。 */
export type CatalogDeltaPage = Omit<VerifiedCatalogSyncPage, "generatedAt">;

/** 远端调用端必须只返回一个服务器固定快照；绝不由离线本地搜索伪造在线结果。 */
export interface CatalogSyncRemotePort {
  getPage(input: Readonly<{
    storeCode: string;
    cursor: string | null;
    pageSize: number;
    catalogVersion?: string;
    downloadLeaseId?: string;
    signal?: AbortSignal;
  }>): Promise<VerifiedCatalogSyncPage>;
  getSyncPlan?(input: Readonly<{
    storeCode: string;
    baseCatalogVersion: string | null;
    signal?: AbortSignal;
  }>): Promise<CatalogSyncPlan>;
  getDeltaPage?(input: Readonly<{
    storeCode: string;
    baseCatalogVersion: string;
    targetCatalogVersion: string;
    cursor: string | null;
    pageSize: number;
    downloadLeaseId?: string;
    signal?: AbortSignal;
  }>): Promise<CatalogDeltaPage>;
  /** 当前 Hbpos lookup API 未返回促销定义；后端提供该合同时再由 adapter 实现。 */
  getPromotions?(input: Readonly<{
    storeCode: string;
    signal?: AbortSignal;
  }>): Promise<readonly CatalogPromotion[]>;
}

/** 由 SQLCipher 仓储实现；业务服务不接触裸 SQLite 或 SQL。 */
export interface CatalogSnapshotStoragePort {
  beginStaging(snapshot: Readonly<{ snapshotId: string; catalogVersion: string; checksum: string; downloadedAtIso: string }>): Promise<void>;
  appendPage(snapshotId: string, items: readonly CatalogStagedItem[]): Promise<void>;
  replacePromotions(snapshotId: string, promotions: readonly CatalogPromotion[]): Promise<void>;
  /** 实现必须验证完整数量后，在同一独占事务中 retire 旧 active 并激活 snapshot。 */
  activate(snapshotId: string, expectedItemCount: number, activatedAtIso: string): Promise<void>;
  discardStaging(snapshotId: string): Promise<void>;
  /** 已知 staging 的失败/取消清理可分批实现，避免大目录级联删除长期占用 SQLite。 */
  discardStagingBatch?(snapshotId: string, batchSize?: number): Promise<number>;
  /** 旧版实现可省略增量能力，服务将安全回退全量。 */
  getActiveMetadata?(): Promise<ActiveCatalogSnapshotMetadata | null>;
  beginDeltaStaging?(input: Readonly<{
    sourceSnapshotId: string;
    baseCatalogVersion: string;
    snapshotId: string;
    catalogVersion: string;
    checksum: string;
    downloadedAtIso: string;
  }>): Promise<void>;
  appendDeltaBatch?(snapshotId: string, batch: Readonly<{
    items: readonly CatalogStagedItem[];
    deletedLookups: readonly CatalogDeletedLookup[];
  }>): Promise<void>;
  activateDelta?(input: Readonly<{
    sourceSnapshotId: string;
    baseCatalogVersion: string;
    stagingSnapshotId: string;
    expectedItemCount: number;
    activatedAtIso: string;
  }>): Promise<ActiveCatalogSnapshotMetadata>;
  /** 每次只清理一个 staging 子树中的有限行，绝不触及 active/retired。 */
  cleanupStagingBatch?(batchSize?: number): Promise<number>;
  cleanupRetiredBatch?(batchSize?: number): Promise<number>;
}

/**
 * 目录刷新成功后的一码多商品候选刷新。实现应自行把失败收敛为“保留旧候选”；
 * 服务仍会兜底吞掉异常，候选失败绝不影响已激活目录或刷新结果。
 */
export interface CatalogCodeConflictRefreshPort {
  refresh(input: Readonly<{
    storeCode: string;
    signal?: AbortSignal;
  }>): Promise<unknown>;
}

export type CatalogSnapshotServiceOptions = Readonly<{
  createSnapshotId: () => string;
  nowIso?: () => string;
  nowMilliseconds?: () => number;
  pageSize?: number;
  yieldControl?: () => Promise<void>;
  codeConflicts?: CatalogCodeConflictRefreshPort;
}>;

export type CatalogRefreshRequest = Readonly<{
  storeCode: string;
  onProgress?: CatalogRefreshProgressObserver | undefined;
  signal?: AbortSignal | undefined;
  /** 在写入 active 前由组合根重新核验可信会话，避免下载期间权限漂移后仍切换目录。 */
  beforeActivate?: (() => void | Promise<void>) | undefined;
  /**
   * active 已提交后，在本服务的串行临界区内重载同一快照的运行时依赖。
   * 调用方必须自行把可恢复告警收敛为结果；这里不会把它当作“切换前失败”。
   */
  afterActivate?: ((result: CatalogActivationResult) => void | Promise<void>) | undefined;
}>;

export type CatalogActivationResult = Readonly<{
  snapshotId: string;
  catalogVersion: string;
  itemCount: number;
  activatedAt: string;
}>;

export type CatalogSnapshotFailureContext = Readonly<{
  code: string;
  pageNumber: number;
  completedItemCount: number;
  totalItemCount?: number;
  httpStatus?: number;
}>;

/** 只携带可写入设备日志的分页坐标，不保留远端正文或商品身份。 */
export class CatalogSnapshotFailure extends HbposApiError {
  public constructor(public readonly context: CatalogSnapshotFailureContext) {
    super(context.code, {
      kind: "envelope",
      code: context.code,
      ...(context.httpStatus === undefined
        ? {}
        : { status: context.httpStatus }),
    });
    this.name = "CatalogSnapshotFailure";
  }
}

export class CatalogSnapshotService {
  private readonly nowIso: () => string;
  private readonly nowMilliseconds: () => number;
  private readonly pageSize: number;
  private readonly yieldControl: () => Promise<void>;
  private serial = Promise.resolve();
  private retiredCleanup = Promise.resolve();
  private stagingCleanup = Promise.resolve();

  public constructor(
    private readonly storage: CatalogSnapshotStoragePort,
    private readonly remote: CatalogSyncRemotePort,
    private readonly options: CatalogSnapshotServiceOptions,
  ) {
    this.nowIso = options.nowIso ?? (() => new Date().toISOString());
    this.nowMilliseconds = options.nowMilliseconds ?? (() => Date.now());
    this.pageSize = options.pageSize ?? 5_000;
    this.yieldControl = options.yieldControl ?? yieldToEventLoop;
  }

  public downloadAndActivate(input: CatalogRefreshRequest): Promise<CatalogActivationResult> {
    const operation = this.serial.then(
      () => this.runDownloadAndActivate(input),
      () => this.runDownloadAndActivate(input),
    );
    // 中文注释：失败只影响本次请求；后续刷新仍须排队而不能并发切换 active。
    this.serial = operation.then(() => undefined, () => undefined);
    return operation;
  }

  /** 启动时低优先级续跑残留 staging 与 retired；失败不改变 active，下次下载会重新等待。 */
  public resumeRetiredCleanup(): void {
    void this.queueRetiredCleanup().catch(() => undefined);
    void this.queueStagingCleanup().catch(() => undefined);
  }

  private queueRetiredCleanup(): Promise<void> {
    const cleanupRetiredBatch = this.storage.cleanupRetiredBatch;
    if (!cleanupRetiredBatch) return Promise.resolve();
    const operation = this.retiredCleanup.then(async () => {
      while (true) {
        const deleted = await cleanupRetiredBatch.call(this.storage, 500);
        if (deleted <= 0) return;
        await this.yieldControl();
      }
    });
    this.retiredCleanup = operation.catch(() => undefined);
    return operation;
  }

  private queueStagingCleanup(): Promise<void> {
    const cleanupStagingBatch = this.storage.cleanupStagingBatch;
    if (!cleanupStagingBatch) return Promise.resolve();
    const operation = this.stagingCleanup.then(async () => {
      while (true) {
        const deleted = await cleanupStagingBatch.call(this.storage, 500);
        if (deleted <= 0) return;
        // 中文注释：断电恢复可能有大量暂存行，每批后让出 SQLite 队列避免阻塞收银。
        await this.yieldControl();
      }
    });
    this.stagingCleanup = operation.catch(() => undefined);
    return operation;
  }

  private runDownloadAndActivate(input: CatalogRefreshRequest): Promise<CatalogActivationResult> {
    if (!this.storage.getActiveMetadata || !this.remote.getSyncPlan) {
      return this.runFullDownloadAndActivate(input);
    }
    return this.runWithSyncPlan(input);
  }

  private async runWithSyncPlan(input: CatalogRefreshRequest): Promise<CatalogActivationResult> {
    const active = await this.storage.getActiveMetadata!();
    throwIfAborted(input.signal);
    const getSyncPlan = this.remote.getSyncPlan;
    if (!getSyncPlan) return this.runFullDownloadAndActivate(input);
    const matchingActive = active !== null && active.storeCode === input.storeCode
      ? active
      : null;
    let plan: CatalogSyncPlan;
    try {
      plan = await getSyncPlan.call(this.remote, {
        storeCode: input.storeCode,
        baseCatalogVersion: matchingActive?.catalogVersion ?? null,
        ...(input.signal ? { signal: input.signal } : {}),
      });
    } catch (error) {
      // 中文注释：仅旧端点明确不支持时才回落首包固定版本 full，不能吞掉冲突、租约或校验错误。
      if (isSyncPlanUnsupported(error)) return this.runFullDownloadAndActivate(input);
      throw error;
    }
    throwIfAborted(input.signal);
    assertSyncPlan(plan, matchingActive?.catalogVersion ?? null);
    if (matchingActive === null) {
      if (plan.mode !== "full") {
        throw catalogVerificationError(
          "Catalog sync plan without a local base must be full.",
          "CATALOG_SYNC_PLAN_INVALID",
        );
      }
      return this.runFullDownloadAndActivate(input, plan);
    }
    if (plan.mode === "noChange") {
      if (plan.targetTotal !== matchingActive.itemCount) {
        return this.runFullWithFreshPlan(input);
      }
      return this.refreshPromotionsOnly(input, matchingActive, plan);
    }
    if (plan.mode === "delta") {
      if ((plan.deltaOperationCount ?? 0) > 5_000) {
        return this.runFullWithFreshPlan(input);
      }
      return this.runDeltaDownloadAndActivate(input, matchingActive, plan);
    }
    // 中文注释：服务端保留窗口外或无法证明连续性时，宁可全量也不能猜测增量。
    return this.runFullDownloadAndActivate(input, plan);
  }

  private async runFullWithFreshPlan(input: CatalogRefreshRequest): Promise<CatalogActivationResult> {
    const getSyncPlan = this.remote.getSyncPlan;
    if (!getSyncPlan) return this.runFullDownloadAndActivate(input);
    throwIfAborted(input.signal);
    let plan: CatalogSyncPlan;
    try {
      plan = await getSyncPlan.call(this.remote, {
        storeCode: input.storeCode,
        baseCatalogVersion: null,
        ...(input.signal ? { signal: input.signal } : {}),
      });
    } catch (error) {
      // 中文注释：此处尚未创建 staging；仅旧端点不支持时可安全退回首包固定版本 full。
      if (isSyncPlanUnsupported(error)) return this.runFullDownloadAndActivate(input);
      throw error;
    }
    throwIfAborted(input.signal);
    assertSyncPlan(plan, null);
    if (plan.mode !== "full") {
      throw catalogVerificationError(
        "Catalog fallback plan without a base must be full.",
        "CATALOG_SYNC_PLAN_INVALID",
      );
    }
    return this.runFullDownloadAndActivate(input, plan);
  }

  private async runFullDownloadAndActivate(
    input: CatalogRefreshRequest,
    plan?: CatalogSyncPlan,
  ): Promise<CatalogActivationResult> {
    const startedAtMilliseconds = this.nowMilliseconds();
    const progress = (
      event: Omit<CatalogRefreshProgressEvent, "elapsedMilliseconds">,
    ): void => {
      reportProgress(input.onProgress, {
        ...event,
        elapsedMilliseconds: Math.max(
          0,
          this.nowMilliseconds() - startedAtMilliseconds,
        ),
      });
    };
    const snapshotId = this.options.createSnapshotId();
    let stagingStarted = false;
    let activated = false;
    let pageNumber = 1;
    let completedItemCount = 0;
    let totalItemCount: number | undefined;
    try {
      // 上次异常遗留的 staging 和 retired 都须先有界回收，文件 freelist 才能稳定复用于本次 staging。
      if (this.storage.cleanupStagingBatch) {
        await this.queueStagingCleanup();
      }
      if (this.storage.cleanupRetiredBatch) {
        await this.queueRetiredCleanup();
      }
      throwIfAborted(input.signal);
      progress({ step: "prepare", percent: 0 });
      throwIfAborted(input.signal);
      const first = await this.remote.getPage({
        storeCode: input.storeCode,
        cursor: null,
        pageSize: this.pageSize,
        ...(plan ? { catalogVersion: plan.targetCatalogVersion } : {}),
        ...(plan?.downloadLeaseId
          ? { downloadLeaseId: plan.downloadLeaseId }
          : {}),
        ...(input.signal ? { signal: input.signal } : {}),
      });
      throwIfAborted(input.signal);
      assertPageContract(first, {
        requestedStoreCode: input.storeCode,
        requestedCursor: null,
      });
      if (
        plan
        && (
          first.catalogVersion !== plan.targetCatalogVersion
          || first.totalCount !== plan.targetTotal
        )
      ) {
        throw catalogVerificationError(
          "Catalog full page does not match the pinned sync plan.",
          "CATALOG_SYNC_PLAN_TARGET_CHANGED",
        );
      }
      totalItemCount = first.totalCount;
      const firstItems = first.items.map(mapCatalogLookupToStagedItem);
      throwIfAborted(input.signal);
      await this.storage.beginStaging({
        snapshotId,
        catalogVersion: first.catalogVersion,
        checksum: first.pageChecksum,
        downloadedAtIso: this.nowIso(),
      });
      stagingStarted = true;
      // 中文注释：begin 成功后必须先拥有清理权，即使此刻取消也只能丢弃 staging。
      throwIfAborted(input.signal);
      const totalPageCount = Math.max(
        1,
        Math.ceil(first.totalCount / this.pageSize),
      );
      progress({ step: "prepare", percent: 100 });
      progress({
        step: "products",
        percent: 0,
        completedItemCount: 0,
        totalItemCount: first.totalCount,
        completedPageCount: 0,
        totalPageCount,
      });

      const seenLookupKeys = new Set<string>();
      const seenCursors = new Set<string>();
      let count = 0;
      let completedPageCount = 0;
      let page: VerifiedCatalogSyncPage | null = first;
      let stagedItems = firstItems;
      let requestedCursor: string | null = null;
      // 中文注释：预取管道——落库当前页期间提前请求下一页，让网络 RTT 与 SQLite 写入重叠；
      // 请求参数、cursor 顺序与全部校验语义与串行版完全一致，仅把"拉页"提前到"落库"之前。
      let pendingCursor: string | null = null;
      let inflightPage: Promise<VerifiedCatalogSyncPage> | null = null;
      const prefetchNext = (currentPage: VerifiedCatalogSyncPage): void => {
        const nextCursor = currentPage.nextCursor;
        if (nextCursor === null || inflightPage !== null) return;
        if (seenCursors.has(nextCursor)) {
          throw catalogVerificationError(
            "Catalog pagination cursor repeated.",
            "CATALOG_CURSOR_REPEATED",
          );
        }
        seenCursors.add(nextCursor);
        pendingCursor = nextCursor;
        // 中文注释：当前正在处理第 completedPageCount+1 页，预取的是其下一页（+2）。
        pageNumber = completedPageCount + 2;
        inflightPage = this.remote.getPage({
          storeCode: input.storeCode,
          cursor: nextCursor,
          pageSize: this.pageSize,
          catalogVersion: first.catalogVersion,
          ...(plan?.downloadLeaseId
            ? { downloadLeaseId: plan.downloadLeaseId }
            : {}),
          ...(input.signal ? { signal: input.signal } : {}),
        });
        // 中文注释：abort 时预取可能永不消费；挂安全网避免 unhandled rejection。
        void inflightPage.catch(() => undefined);
      };
      while (page) {
        throwIfAborted(input.signal);
        assertPageContract(page, {
          requestedStoreCode: input.storeCode,
          requestedCursor,
        });
        if (page.catalogVersion !== first.catalogVersion) {
          throw catalogVerificationError(
            "Catalog snapshot version changed during paged download.",
            "CATALOG_SNAPSHOT_VERSION_CHANGED",
          );
        }
        if (page.totalCount !== first.totalCount) {
          throw catalogVerificationError(
            "Catalog total changed during paged download.",
            "CATALOG_SNAPSHOT_TOTAL_CHANGED",
          );
        }
        // 中文注释：先发起下一页预取再落库当前页，网络请求与本地写入并行执行。
        prefetchNext(page);
        assertUniqueLookupKeys(stagedItems, seenLookupKeys);
        const finalPage = page.nextCursor === null;
        if (!finalPage && first.totalCount === 0) {
          throw catalogVerificationError(
            "Catalog pagination cannot continue after an empty total.",
            "CATALOG_PAGINATION_INVALID",
          );
        }

        const localBatches = chunkItems(stagedItems, 500);
        for (const [batchIndex, batch] of localBatches.entries()) {
          throwIfAborted(input.signal);
          await this.storage.appendPage(snapshotId, batch);
          throwIfAborted(input.signal);
          count += batch.length;
          completedItemCount = count;
          if (count > first.totalCount) {
            throw catalogVerificationError(
              "Catalog page count exceeds the server total.",
              "CATALOG_ITEM_COUNT_MISMATCH",
            );
          }
          const serverPageCompleted = batchIndex === localBatches.length - 1;
          if (finalPage && serverPageCompleted && count !== first.totalCount) {
            throw catalogVerificationError(
              "Catalog page count does not match the server total.",
              "CATALOG_ITEM_COUNT_MISMATCH",
            );
          }
          progress({
            step: "products",
            // 中文注释：只有最终服务端页已完整分批落库后才能报告 100%。
            percent: finalPage && serverPageCompleted
              ? 100
              : Math.min(99, Math.floor((count / first.totalCount) * 100)),
            completedItemCount: count,
            totalItemCount: first.totalCount,
            completedPageCount:
              completedPageCount + (serverPageCompleted ? 1 : 0),
            totalPageCount,
          });
        }
        completedPageCount += 1;
        if (localBatches.length === 0) {
          progress({
            step: "products",
            percent: finalPage ? 100 : 0,
            completedItemCount: count,
            totalItemCount: first.totalCount,
            completedPageCount,
            totalPageCount,
          });
        }
        if (finalPage && count !== first.totalCount) {
          throw catalogVerificationError(
            "Catalog page count does not match the server total.",
            "CATALOG_ITEM_COUNT_MISMATCH",
          );
        }
        if (finalPage) {
          page = null;
        } else {
          // 中文注释：预取已在落库前发起；此处按序消费并恢复请求 cursor 上下文。
          if (inflightPage === null) {
            throw catalogVerificationError(
              "Catalog pagination prefetch is missing.",
              "CATALOG_PAGINATION_INVALID",
            );
          }
          const nextPage = await inflightPage;
          inflightPage = null;
          throwIfAborted(input.signal);
          requestedCursor = pendingCursor;
          page = nextPage;
          stagedItems = page.items.map(mapCatalogLookupToStagedItem);
        }
      }
      if (count !== first.totalCount) {
        throw catalogVerificationError(
          "Catalog page count does not match the server total.",
          "CATALOG_ITEM_COUNT_MISMATCH",
        );
      }

      progress({ step: "promotions", percent: 0 });
      throwIfAborted(input.signal);
      const promotions = await this.remote.getPromotions?.({
        storeCode: input.storeCode,
        ...(input.signal ? { signal: input.signal } : {}),
      }) ?? [];
      throwIfAborted(input.signal);
      await this.storage.replacePromotions(snapshotId, promotions);
      throwIfAborted(input.signal);
      progress({ step: "promotions", percent: 100 });
      progress({ step: "activate", percent: 0 });
      throwIfAborted(input.signal);
      await input.beforeActivate?.();
      throwIfAborted(input.signal);
      const activatedAt = this.nowIso();
      await this.storage.activate(snapshotId, count, activatedAt);
      activated = true;
      this.resumeRetiredCleanup();
      const result: CatalogActivationResult = {
        snapshotId,
        catalogVersion: first.catalogVersion,
        itemCount: count,
        activatedAt,
      };
      await this.refreshCodeConflicts(input);
      await input.afterActivate?.(result);
      // 中文注释：100% 表示 active 与同快照运行时依赖均已完成收口。
      progress({ step: "activate", percent: 100 });
      return result;
    } catch (error) {
      // 中文注释：active 已提交后，后置重载异常绝不能回到“切换前失败”的清理路径。
      if (stagingStarted && !activated) await this.discardStaging(snapshotId);
      throw contextualizeCatalogFailure(error, {
        pageNumber,
        completedItemCount,
        ...(totalItemCount === undefined ? {} : { totalItemCount }),
      });
    }
  }

  private async refreshPromotionsOnly(
    input: CatalogRefreshRequest,
    active: ActiveCatalogSnapshotMetadata,
    plan: CatalogSyncPlan,
  ): Promise<CatalogActivationResult> {
    const startedAtMilliseconds = this.nowMilliseconds();
    const progress = (event: Omit<CatalogRefreshProgressEvent, "elapsedMilliseconds">): void => {
      reportProgress(input.onProgress, {
        ...event,
        elapsedMilliseconds: Math.max(0, this.nowMilliseconds() - startedAtMilliseconds),
      });
    };
    progress({ step: "prepare", percent: 100 });
    progress({ step: "products", percent: 100, completedItemCount: active.itemCount, totalItemCount: active.itemCount, completedPageCount: 0, totalPageCount: 0 });
    progress({ step: "promotions", percent: 0 });
    const promotions = await this.remote.getPromotions?.({
      storeCode: input.storeCode,
      ...(input.signal ? { signal: input.signal } : {}),
    }) ?? [];
    throwIfAborted(input.signal);
    await input.beforeActivate?.();
    throwIfAborted(input.signal);
    await this.storage.replacePromotions(active.snapshotId, promotions);
    progress({ step: "promotions", percent: 100 });
    const result: CatalogActivationResult = {
      snapshotId: active.snapshotId,
      catalogVersion: plan.targetCatalogVersion,
      itemCount: active.itemCount,
      activatedAt: active.activatedAt,
    };
    progress({ step: "activate", percent: 0 });
    // 中文注释：服务端判定 noChange 只比较胜出行，码冲突候选可能已变化，因此仍须刷新。
    await this.refreshCodeConflicts(input);
    await input.afterActivate?.(result);
    progress({ step: "activate", percent: 100 });
    return result;
  }

  private async runDeltaDownloadAndActivate(
    input: CatalogRefreshRequest,
    active: ActiveCatalogSnapshotMetadata,
    plan: CatalogSyncPlan,
  ): Promise<CatalogActivationResult> {
    const beginDeltaStaging = this.storage.beginDeltaStaging;
    const appendDeltaBatch = this.storage.appendDeltaBatch;
    const activateDelta = this.storage.activateDelta;
    const getDeltaPage = this.remote.getDeltaPage;
    if (!beginDeltaStaging || !appendDeltaBatch || !activateDelta || !getDeltaPage) {
      return this.runFullWithFreshPlan(input);
    }
    const startedAtMilliseconds = this.nowMilliseconds();
    const progress = (event: Omit<CatalogRefreshProgressEvent, "elapsedMilliseconds">): void => {
      reportProgress(input.onProgress, {
        ...event,
        elapsedMilliseconds: Math.max(0, this.nowMilliseconds() - startedAtMilliseconds),
      });
    };
    const snapshotId = this.options.createSnapshotId();
    let stagingStarted = false;
    let activated = false;
    let pageNumber = 1;
    let completedItemCount = 0;
    try {
      progress({ step: "prepare", percent: 0 });
      if (this.storage.cleanupStagingBatch) {
        await this.queueStagingCleanup();
      }
      throwIfAborted(input.signal);
      // 中文注释：delta staging 只保存 upsert/tombstone，绝不复制完整 active 子行。
      await beginDeltaStaging.call(this.storage, {
        sourceSnapshotId: active.snapshotId,
        baseCatalogVersion: active.catalogVersion,
        snapshotId,
        catalogVersion: plan.targetCatalogVersion,
        checksum: `delta:${plan.targetCatalogVersion}`,
        downloadedAtIso: this.nowIso(),
      });
      stagingStarted = true;
      progress({ step: "prepare", percent: 100 });
      progress({ step: "products", percent: 0, completedItemCount: 0, totalItemCount: plan.targetTotal, completedPageCount: 0, totalPageCount: 0 });

      const seenCursors = new Set<string>();
      const seenUpserts = new Set<string>();
      const seenDeletes = new Set<string>();
      let cursor: string | null = null;
      let completedPageCount = 0;
      // 中文注释：delta 预取管道——校验通过后立即请求下一页，与落库并行；仍按序消费。
      let pendingCursor: string | null = null;
      let inflightPage: Promise<CatalogDeltaPage> | null = null;
      const requestDeltaPage = (requestCursor: string | null): Promise<CatalogDeltaPage> =>
        getDeltaPage.call(this.remote, {
          storeCode: input.storeCode,
          baseCatalogVersion: active.catalogVersion,
          targetCatalogVersion: plan.targetCatalogVersion,
          cursor: requestCursor,
          pageSize: this.pageSize,
          ...(plan.downloadLeaseId
            ? { downloadLeaseId: plan.downloadLeaseId }
            : {}),
          ...(input.signal ? { signal: input.signal } : {}),
        });
      while (true) {
        throwIfAborted(input.signal);
        // 中文注释：首页必须同步取回（总操作数决定是否回退 full）；后续页消费预取结果。
        let page: CatalogDeltaPage;
        if (inflightPage !== null) {
          page = await inflightPage;
          inflightPage = null;
          cursor = pendingCursor;
        } else {
          page = await requestDeltaPage(cursor);
        }
        throwIfAborted(input.signal);
        assertPageContract(page, { requestedStoreCode: input.storeCode, requestedCursor: cursor });
        if (page.catalogVersion !== plan.targetCatalogVersion || page.totalCount !== plan.targetTotal) {
          throw catalogVerificationError("Catalog delta target changed during paged download.", "CATALOG_DELTA_TARGET_CHANGED");
        }
        assertUniqueLookupKeys(page.items.map(mapCatalogLookupToStagedItem), seenUpserts);
        if (page.items.some((item) => seenDeletes.has(item.lookupCodeNormalized))) {
          throw catalogVerificationError("Catalog delta contains conflicting upsert identities.", "CATALOG_DELTA_INVALID");
        }
        for (const deleted of page.deletedLookups) {
          if (deleted.storeCode !== input.storeCode || seenDeletes.has(deleted.lookupCodeNormalized) || seenUpserts.has(deleted.lookupCodeNormalized)) {
            throw catalogVerificationError("Catalog delta contains conflicting delete identities.", "CATALOG_DELTA_INVALID");
          }
          seenDeletes.add(deleted.lookupCodeNormalized);
        }

        const stagedItems = page.items.map(mapCatalogLookupToStagedItem);
        const operationCount = stagedItems.length + page.deletedLookups.length;
        if (completedItemCount + operationCount > 5_000) {
          await this.discardStaging(snapshotId);
          stagingStarted = false;
          return this.runFullWithFreshPlan(input);
        }
        // 中文注释：确认继续 delta 后才预取下一页，让网络请求与落库并行；cursor 重复检测提前到发起时。
        const nextCursor = page.nextCursor;
        if (nextCursor !== null && inflightPage === null) {
          if (seenCursors.has(nextCursor)) {
            throw catalogVerificationError(
              "Catalog delta pagination cursor repeated.",
              "CATALOG_CURSOR_REPEATED",
            );
          }
          seenCursors.add(nextCursor);
          pendingCursor = nextCursor;
          pageNumber += 1;
          inflightPage = requestDeltaPage(nextCursor);
          // 中文注释：abort 时预取可能永不消费；挂安全网避免 unhandled rejection。
          void inflightPage.catch(() => undefined);
        }
        const operations = [
          ...stagedItems.map((item) => ({
            kind: "upsert" as const,
            key: item.lookupCodeNormalized,
            item,
          })),
          ...page.deletedLookups.map((deleted) => ({
            kind: "delete" as const,
            key: deleted.lookupCodeNormalized,
            deleted,
          })),
        ].sort((left, right) => left.key.localeCompare(right.key));
        const localBatches = chunkItems(operations, 500);
        for (const [batchIndex, batch] of localBatches.entries()) {
          throwIfAborted(input.signal);
          await appendDeltaBatch.call(this.storage, snapshotId, {
            items: batch
              .filter((operation) => operation.kind === "upsert")
              .map((operation) => operation.item),
            deletedLookups: batch
              .filter((operation) => operation.kind === "delete")
              .map((operation) => operation.deleted),
          });
          throwIfAborted(input.signal);
          if (batchIndex < localBatches.length - 1 || page.nextCursor !== null) {
            await this.yieldControl();
          }
        }
        completedPageCount += 1;
        completedItemCount += operationCount;
        const finalPage = page.nextCursor === null;
        if (
          finalPage
          && plan.deltaOperationCount !== undefined
          && plan.deltaOperationCount !== null
          && completedItemCount !== plan.deltaOperationCount
        ) {
          throw catalogVerificationError(
            "Catalog delta operation count does not match the sync plan.",
            "CATALOG_DELTA_OPERATION_COUNT_MISMATCH",
          );
        }
        progress({
          step: "products",
          percent: finalPage ? 100 : Math.min(99, Math.max(1, completedPageCount)),
          completedItemCount,
          totalItemCount: plan.targetTotal,
          completedPageCount,
          totalPageCount: 0,
        });
        if (finalPage) break;
      }

      progress({ step: "promotions", percent: 0 });
      const promotions = await this.remote.getPromotions?.({
        storeCode: input.storeCode,
        ...(input.signal ? { signal: input.signal } : {}),
      }) ?? [];
      throwIfAborted(input.signal);
      await this.storage.replacePromotions(snapshotId, promotions);
      progress({ step: "promotions", percent: 100 });
      progress({ step: "activate", percent: 0 });
      await input.beforeActivate?.();
      throwIfAborted(input.signal);
      const activatedAt = this.nowIso();
      const activatedMetadata = await activateDelta.call(this.storage, {
        sourceSnapshotId: active.snapshotId,
        baseCatalogVersion: active.catalogVersion,
        stagingSnapshotId: snapshotId,
        expectedItemCount: plan.targetTotal,
        activatedAtIso: activatedAt,
      });
      activated = true;
      const result: CatalogActivationResult = {
        snapshotId: activatedMetadata.snapshotId,
        catalogVersion: plan.targetCatalogVersion,
        itemCount: plan.targetTotal,
        activatedAt: activatedMetadata.activatedAt,
      };
      await this.refreshCodeConflicts(input);
      await input.afterActivate?.(result);
      progress({ step: "activate", percent: 100 });
      return result;
    } catch (error) {
      if (stagingStarted && !activated) await this.discardStaging(snapshotId);
      if (isCatalogDeltaFallback(error)) {
        // base/target 可能在 sync-plan 与首个 delta page 之间过期；清理后同次安全回退全量。
        return this.runFullWithFreshPlan(input);
      }
      throw contextualizeCatalogFailure(error, {
        pageNumber,
        completedItemCount,
        totalItemCount: plan.targetTotal,
      });
    }
  }

  /** “重置目录”不会先清空 active：它只是强制重新下载并以同一安全切换流程替换。 */
  public resetAndRedownload(input: CatalogRefreshRequest): Promise<CatalogActivationResult> {
    const operation = this.serial.then(
      () => this.runFullWithFreshPlan(input),
      () => this.runFullWithFreshPlan(input),
    );
    // 中文注释：reset 与普通刷新共用同一串行门，不能让旧 delta 在重置前插队。
    this.serial = operation.then(() => undefined, () => undefined);
    return operation;
  }

  /**
   * 目录已提交后在同一串行临界区内刷新码冲突候选；任何异常都只保留旧候选，
   * 不能回到“切换前失败”的清理路径，也不能把已激活目录报告为失败。
   */
  private async refreshCodeConflicts(input: CatalogRefreshRequest): Promise<void> {
    const codeConflicts = this.options.codeConflicts;
    if (!codeConflicts) return;
    try {
      await codeConflicts.refresh({
        storeCode: input.storeCode,
        ...(input.signal ? { signal: input.signal } : {}),
      });
    } catch {
      // 中文注释：候选是扫码选择的增强数据，失败时继续使用本地旧候选。
    }
  }

  private async discardStaging(snapshotId: string): Promise<void> {
    const discardStagingBatch = this.storage.discardStagingBatch;
    if (!discardStagingBatch) {
      await this.storage.discardStaging(snapshotId);
      return;
    }
    while (true) {
      const deleted = await discardStagingBatch.call(this.storage, snapshotId, 500);
      if (deleted <= 0) return;
      // 中文注释：失败恢复也不得让 30 万级目录的级联删除独占事件循环与 SQLite 队列。
      await this.yieldControl();
    }
  }
}

function isSyncPlanUnsupported(error: unknown): boolean {
  const candidate = error as Readonly<{ status?: unknown; code?: unknown }> | null;
  return candidate?.status === 404
    || candidate?.status === 501
    || candidate?.code === "CATALOG_SYNC_PLAN_UNSUPPORTED";
}

function isCatalogDeltaFallback(error: unknown): boolean {
  if (error instanceof CatalogSnapshotFailure) {
    return error.context.code === "CATALOG_SNAPSHOT_EXPIRED"
      || error.context.code === "CATALOG_DELTA_BASE_CHANGED";
  }

  const code = (error as Readonly<{ code?: unknown }> | null)?.code;
  return code === "CATALOG_SNAPSHOT_EXPIRED" || code === "CATALOG_DELTA_BASE_CHANGED";
}

function contextualizeCatalogFailure(
  error: unknown,
  context: Omit<CatalogSnapshotFailureContext, "code" | "httpStatus">,
): unknown {
  if (error instanceof CatalogSnapshotFailure) return error;
  const candidate = error as Readonly<{
    code?: unknown;
    status?: unknown;
  }> | null;
  if (
    typeof candidate?.code !== "string"
    || !candidate.code.startsWith("CATALOG_")
  ) {
    return error;
  }
  return new CatalogSnapshotFailure({
    code: candidate.code,
    ...context,
    ...(typeof candidate.status === "number"
      ? { httpStatus: candidate.status }
      : {}),
  });
}

function assertSyncPlan(plan: CatalogSyncPlan, activeVersion: string | null): void {
  if (
    (plan.mode !== "noChange" && plan.mode !== "delta" && plan.mode !== "full")
    || plan.baseCatalogVersion !== activeVersion
    || !isCatalogVersion(plan.targetCatalogVersion)
    || !Number.isSafeInteger(plan.targetTotal)
    || plan.targetTotal < 0
    || (
      plan.deltaOperationCount !== undefined
      && plan.deltaOperationCount !== null
      && (
        !Number.isSafeInteger(plan.deltaOperationCount)
        || plan.deltaOperationCount < 0
      )
    )
  ) {
    throw catalogVerificationError("Catalog sync plan is invalid.", "CATALOG_SYNC_PLAN_INVALID");
  }
  if (
    (plan.mode === "noChange" && plan.targetCatalogVersion !== activeVersion)
    || (plan.mode === "delta" && activeVersion === null)
  ) {
    throw catalogVerificationError("Catalog no-change plan has a different target.", "CATALOG_SYNC_PLAN_INVALID");
  }
}

function yieldToEventLoop(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function chunkItems<T>(items: readonly T[], batchSize: number): readonly (readonly T[])[] {
  const batches: T[][] = [];
  for (let start = 0; start < items.length; start += batchSize) {
    batches.push(items.slice(start, start + batchSize));
  }
  return batches;
}

function throwIfAborted(signal: AbortSignal | undefined): void {
  if (signal?.aborted) {
    throw new Error("Catalog refresh was cancelled.");
  }
}

/** 进度仅供展示；观察器自身故障绝不能中断已校验目录的安全切换。 */
function reportProgress(
  observer: CatalogRefreshProgressObserver | undefined,
  event: CatalogRefreshProgressEvent,
): void {
  try {
    observer?.(Object.freeze({ ...event }));
  } catch {
    // 中文注释：UI 销毁或订阅方异常不得影响目录下载与激活。
  }
}

export function mapCatalogLookupToStagedItem(item: CatalogLookupItem): CatalogStagedItem {
  return {
    storeCode: item.storeCode,
    productCode: item.productCode,
    referenceCode: normalizeOptionalOpaqueText(item.referenceCode),
    itemNumber: normalizeOptionalOpaqueText(item.itemNumber),
    displayName: normalizeRequiredText(item.displayName, item.itemNumber, item.productCode),
    barcode: normalizeOptionalOpaqueText(item.barcode),
    lookupCode: item.lookupCode,
    lookupCodeNormalized: item.lookupCodeNormalized,
    retailPriceCents: toIntegerCents(item.retailPrice),
    priceSource: item.priceSource,
    priceSourceLabel: normalizeRequiredText(item.priceSourceLabel, "catalog"),
    quantityFactor: normalizeQuantityFactor(item.quantityFactor),
    // 当前 Hbpos CatalogLookupItemDto 没有税率字段；定价层按既有 GST 规则计算。
    taxRateBasisPoints: null,
    updatedAtIso: normalizeOptionalTimestamp(item.updatedAt),
    rowVersion: normalizeOptionalOpaqueText(item.rowVersion),
    productImage: normalizeOptionalOpaqueText(item.productImage),
    discountRate: normalizeDiscountRate(item.discountRate),
    isSpecialProduct: item.isSpecialProduct,
  };
}

function assertPageContract(
  page: Pick<VerifiedCatalogSyncPage, "storeCode" | "cursor" | "items" | "nextCursor" | "hasMore" | "totalCount" | "catalogVersion">,
  expected: Readonly<{ requestedStoreCode: string; requestedCursor: string | null }>,
): void {
  if (!isCatalogVersion(page.catalogVersion)) {
    throw catalogVerificationError(
      "Catalog page is missing a valid snapshot version.",
      "CATALOG_SNAPSHOT_VERSION_INVALID",
    );
  }
  if (!Number.isSafeInteger(page.totalCount) || page.totalCount < 0) {
    throw catalogVerificationError(
      "Catalog page is missing a valid total.",
      "CATALOG_SNAPSHOT_TOTAL_INVALID",
    );
  }
  if (page.storeCode !== expected.requestedStoreCode) {
    throw catalogVerificationError(
      "Catalog page store does not match the requested store.",
      "CATALOG_STORE_MISMATCH",
    );
  }
  if (page.cursor !== expected.requestedCursor) {
    throw catalogVerificationError(
      "Catalog page cursor does not match the requested cursor.",
      "CATALOG_CURSOR_MISMATCH",
    );
  }
  if (page.hasMore !== (page.nextCursor !== null)) {
    throw catalogVerificationError(
      "Catalog page continuation fields are inconsistent.",
      "CATALOG_PAGINATION_INVALID",
    );
  }
  for (const item of page.items) {
    if (item.storeCode !== expected.requestedStoreCode) {
      throw catalogVerificationError(
        "Catalog item store does not match the requested store.",
        "CATALOG_ITEM_STORE_MISMATCH",
      );
    }
  }
}

function isCatalogVersion(value: unknown): value is string {
  return typeof value === "string" &&
    value.length > 0 &&
    value.trim() === value &&
    value.length <= 512 &&
    !/[\u0000-\u001f\u007f]/u.test(value);
}

function assertUniqueLookupKeys(items: readonly CatalogStagedItem[], seen: Set<string>): void {
  for (const item of items) {
    const key = `${item.storeCode}\u0000${item.lookupCodeNormalized}`;
    if (seen.has(key)) {
      throw catalogVerificationError(
        "Catalog snapshot contains a duplicate lookup code.",
        "CATALOG_DUPLICATE_LOOKUP",
      );
    }
    seen.add(key);
  }
}

function toIntegerCents(value: number): number {
  // 商品字段脏数据不应阻断整店目录；不可安全表示的价格统一按 0 写入本地目录。
  if (!Number.isFinite(value) || value <= 0) return 0;
  const match = /^(\d+)(?:\.(\d+))?(?:e([+-]?\d+))?$/u.exec(String(value).toLowerCase());
  if (match === null) return 0;

  // 按 number 的规范十进制文本做 half-up，避免二进制乘 100 在半分或大额时漂移。
  const fraction = match[2] ?? "";
  const exponent = Number(match[3] ?? "0");
  const digits = BigInt(`${match[1]}${fraction}`);
  const decimalPlaces = fraction.length - exponent;
  let cents: bigint;
  if (decimalPlaces <= 2) {
    cents = digits * (10n ** BigInt(2 - decimalPlaces));
  } else {
    const divisor = 10n ** BigInt(decimalPlaces - 2);
    const quotient = digits / divisor;
    const remainder = digits % divisor;
    cents = quotient + (remainder * 2n >= divisor ? 1n : 0n);
  }
  return cents <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(cents) : 0;
}

function normalizeQuantityFactor(value: number): number {
  return Number.isFinite(value) && value > 0 ? value : 1;
}

function normalizeDiscountRate(value: number | null): number | null {
  if (value === null || !Number.isFinite(value)) return null;
  return Math.min(1, Math.max(0, value));
}

function normalizeOptionalTimestamp(value: string | null): string | null {
  if (value === null) return null;
  const normalized = value.trim();
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$/u.exec(
    normalized,
  );
  if (match === null) return null;

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  const zone = match[7] ?? "";
  const maxDay = daysInGregorianMonth(year, month);
  if (
    year < 1
    || month < 1
    || month > 12
    || day < 1
    || day > maxDay
    || hour > 23
    || minute > 59
    || second > 59
  ) {
    return null;
  }
  if (zone !== "Z") {
    const zoneHour = Number(zone.slice(1, 3));
    const zoneMinute = Number(zone.slice(4, 6));
    if (zoneHour > 14 || zoneMinute > 59 || (zoneHour === 14 && zoneMinute !== 0)) {
      return null;
    }
  }
  return Number.isFinite(Date.parse(normalized)) ? normalized : null;
}

function daysInGregorianMonth(year: number, month: number): number {
  if (month === 2) {
    const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
    return leap ? 29 : 28;
  }
  return month === 4 || month === 6 || month === 9 || month === 11 ? 30 : 31;
}

function normalizeOptionalOpaqueText(value: string | null): string | null {
  if (value === null) return null;
  // 标识符和版本令牌只把纯空白修成 null；非空内容必须逐字保真。
  return value.trim().length > 0 ? value : null;
}

function normalizeRequiredText(
  value: string,
  ...fallbacks: readonly (string | null)[]
): string {
  const normalized = value.trim();
  if (normalized.length > 0) return normalized;
  for (const fallback of fallbacks) {
    const normalizedFallback = fallback?.trim() ?? "";
    if (normalizedFallback.length > 0) return normalizedFallback;
  }
  return "catalog";
}

function catalogVerificationError(message: string, code: string): HbposApiError {
  return new HbposApiError(message, {
    kind: "envelope",
    code,
  });
}
