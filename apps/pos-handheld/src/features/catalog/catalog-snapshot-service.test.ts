import assert from "node:assert/strict";
import test from "node:test";

import {
  CatalogSnapshotFailure,
  CatalogSnapshotService,
  mapCatalogLookupToStagedItem,
  type ActiveCatalogSnapshotMetadata,
  type CatalogCodeConflictRefreshPort,
  type CatalogDeltaPage,
  type CatalogSyncPlan,
  type CatalogSnapshotStoragePort,
  type CatalogStagedItem,
  type CatalogSyncRemotePort,
} from "./catalog-snapshot-service";
import type {
  CatalogLookupItem,
  VerifiedCatalogSyncPage,
} from "./hbpos-catalog-remote";

import { HbposApiError } from "@/core/api/hbpos-api";

const item = (
  lookupCode: string,
  overrides: Partial<CatalogLookupItem> = {},
): CatalogLookupItem => ({
  storeCode: "S1",
  productCode: `P-${lookupCode}`,
  referenceCode: null,
  displayName: `商品 ${lookupCode}`,
  lookupCode,
  lookupCodeNormalized: lookupCode.toUpperCase(),
  itemNumber: `I-${lookupCode}`,
  barcode: `SOURCE-${lookupCode}`,
  retailPrice: 1,
  priceSource: 0,
  priceSourceLabel: "product",
  quantityFactor: 1,
  updatedAt: "2026-07-28T00:00:00.000Z",
  rowVersion: "ROW",
  productImage: null,
  discountRate: null,
  isSpecialProduct: false,
  ...overrides,
});

const page = (input: Readonly<{
  cursor: string | null;
  nextCursor: string | null;
  items: readonly CatalogLookupItem[];
  totalCount: number;
  catalogVersion?: string;
  storeCode?: string;
  hasMore?: boolean;
}>): VerifiedCatalogSyncPage => ({
  storeCode: input.storeCode ?? "S1",
  generatedAt: "2026-07-28T00:00:00.000Z",
  cursor: input.cursor,
  items: input.items,
  deletedLookups: [],
  nextCursor: input.nextCursor,
  hasMore: input.hasMore ?? input.nextCursor !== null,
  totalCount: input.totalCount,
  catalogVersion: input.catalogVersion ?? "catalog-v2",
  pageChecksum: `verified-${input.cursor ?? "first"}`,
});

class MemoryCatalogStorage implements CatalogSnapshotStoragePort {
  public readonly active = new Map<string, readonly CatalogStagedItem[]>();
  public readonly staged = new Map<string, readonly CatalogStagedItem[]>();
  public readonly appendBatchSizes: number[] = [];
  public activated: string | null = null;
  public activeMetadata: ActiveCatalogSnapshotMetadata | null = null;
  public promotionReplacements: string[] = [];
  public deltaBegin: Readonly<{
    sourceSnapshotId: string;
    baseCatalogVersion: string;
    snapshotId: string;
    catalogVersion: string;
  }> | null = null;
  public readonly deltaBatchSizes: number[] = [];
  public readonly deltaDeleted = new Map<string, Set<string>>();
  public deltaActivated: string | null = null;

  public async getActiveMetadata(): Promise<ActiveCatalogSnapshotMetadata | null> {
    return this.activeMetadata;
  }

  public async beginDeltaStaging(input: Readonly<{
    sourceSnapshotId: string;
    baseCatalogVersion: string;
    snapshotId: string;
    catalogVersion: string;
  }>): Promise<void> {
    this.deltaBegin = input;
    this.staged.set(input.snapshotId, []);
    this.deltaDeleted.set(input.snapshotId, new Set());
  }

  public async appendDeltaBatch(snapshotId: string, batch: Readonly<{
    items: readonly CatalogStagedItem[];
    deletedLookups: CatalogDeltaPage["deletedLookups"];
  }>): Promise<void> {
    this.deltaBatchSizes.push(batch.items.length + batch.deletedLookups.length);
    this.staged.set(snapshotId, [...(this.staged.get(snapshotId) ?? []), ...batch.items]);
    const deleted = this.deltaDeleted.get(snapshotId) ?? new Set<string>();
    for (const lookup of batch.deletedLookups) deleted.add(lookup.lookupCodeNormalized);
    this.deltaDeleted.set(snapshotId, deleted);
  }

  public async activateDelta(input: Readonly<{
    sourceSnapshotId: string;
    baseCatalogVersion: string;
    stagingSnapshotId: string;
    expectedItemCount: number;
    activatedAtIso: string;
  }>): Promise<ActiveCatalogSnapshotMetadata> {
    const begin = this.deltaBegin;
    assert.ok(begin);
    const entries = new Map(
      (this.active.get(input.sourceSnapshotId) ?? []).map((entry) => [entry.lookupCodeNormalized, entry]),
    );
    for (const lookup of this.deltaDeleted.get(input.stagingSnapshotId) ?? []) entries.delete(lookup);
    for (const changed of this.staged.get(input.stagingSnapshotId) ?? []) {
      entries.set(changed.lookupCodeNormalized, changed);
    }
    assert.equal(entries.size, input.expectedItemCount);
    const metadata: ActiveCatalogSnapshotMetadata = {
      snapshotId: input.sourceSnapshotId,
      generationId: input.stagingSnapshotId,
      storeCode: "S1",
      catalogVersion: begin.catalogVersion,
      itemCount: entries.size,
      activatedAt: input.activatedAtIso,
    };
    this.active.set(input.sourceSnapshotId, [...entries.values()]);
    this.activeMetadata = metadata;
    this.staged.delete(input.stagingSnapshotId);
    this.deltaDeleted.delete(input.stagingSnapshotId);
    this.deltaActivated = input.stagingSnapshotId;
    this.activated = input.sourceSnapshotId;
    return metadata;
  }

  public async beginStaging(snapshot: { snapshotId: string }): Promise<void> {
    this.staged.set(snapshot.snapshotId, []);
  }

  public async appendPage(snapshotId: string, items: readonly CatalogStagedItem[]): Promise<void> {
    this.appendBatchSizes.push(items.length);
    this.staged.set(snapshotId, [...(this.staged.get(snapshotId) ?? []), ...items]);
  }

  public async replacePromotions(snapshotId: string): Promise<void> {
    this.promotionReplacements.push(snapshotId);
  }

  public async activate(snapshotId: string, expectedItemCount: number): Promise<void> {
    const staged = this.staged.get(snapshotId) ?? [];
    if (staged.length !== expectedItemCount) throw new Error("staging count mismatch");
    this.active.clear();
    this.active.set(snapshotId, staged);
    this.activated = snapshotId;
    this.activeMetadata = {
      snapshotId,
      storeCode: "S1",
      catalogVersion: "catalog-v2",
      itemCount: expectedItemCount,
      activatedAt: "2026-07-28T00:00:00.000Z",
    };
  }

  public async discardStaging(snapshotId: string): Promise<void> {
    this.staged.delete(snapshotId);
  }

}

function syncPlan(input: CatalogSyncPlan): CatalogSyncPlan {
  return input;
}

class AbortAfterBeginStorage extends MemoryCatalogStorage {
  public constructor(private readonly controller: AbortController) {
    super();
  }

  public override async beginStaging(snapshot: { snapshotId: string }): Promise<void> {
    await super.beginStaging(snapshot);
    // 中文注释：模拟暂存事务已成功、调用方尚未来得及标记 cleanup 所有权时取消。
    this.controller.abort();
  }
}

class RetiredCleanupStorage extends MemoryCatalogStorage {
  public retiredCleanupResults: number[] = [];
  public readonly retiredCleanupBatchSizes: number[] = [];
  public stagingCleanupResults: number[] = [];
  public readonly stagingCleanupBatchSizes: number[] = [];

  public async cleanupRetiredBatch(batchSize = 500): Promise<number> {
    this.retiredCleanupBatchSizes.push(batchSize);
    return this.retiredCleanupResults.shift() ?? 0;
  }

  public async cleanupStagingBatch(batchSize = 500): Promise<number> {
    this.stagingCleanupBatchSizes.push(batchSize);
    return this.stagingCleanupResults.shift() ?? 0;
  }
}

class BoundedDiscardStorage extends MemoryCatalogStorage {
  public readonly discardBatchSizes: number[] = [];

  public async discardStagingBatch(
    snapshotId: string,
    batchSize = 500,
  ): Promise<number> {
    this.discardBatchSizes.push(batchSize);
    const staged = this.staged.get(snapshotId);
    if (!staged) return 0;
    if (staged.length > 0) {
      const deleted = Math.min(batchSize, staged.length);
      this.staged.set(snapshotId, staged.slice(deleted));
      return deleted;
    }
    // 中文注释：模拟子行清空后才删除 staging 父行，计入同一有界清理批次。
    this.staged.delete(snapshotId);
    return 1;
  }
}

function remote(pages: readonly VerifiedCatalogSyncPage[]): CatalogSyncRemotePort {
  return {
    async getPage(input) {
      const result = pages.find((candidate) => candidate.cursor === input.cursor);
      if (!result) throw new Error("unexpected cursor");
      return result;
    },
  };
}

test("首次下载或换店先用 null 基线取得 full 租约，再从固定 target 下载第一页", async () => {
  const storage = new MemoryCatalogStorage();
  storage.activeMetadata = {
    snapshotId: "old-s2",
    storeCode: "S2",
    catalogVersion: "v1",
    itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  let plans = 0;
  const pageRequests: Parameters<CatalogSyncRemotePort["getPage"]>[0][] = [];
  const service = new CatalogSnapshotService(storage, {
    async getPage(input) {
      pageRequests.push(input);
      return page({
        cursor: null,
        nextCursor: null,
        items: [item("A")],
        totalCount: 1,
        catalogVersion: "v2",
      });
    },
    async getSyncPlan(input) {
      plans += 1;
      assert.equal(input.baseCatalogVersion, null);
      return syncPlan({
        mode: "full",
        baseCatalogVersion: null,
        targetCatalogVersion: "v2",
        targetTotal: 1,
        downloadLeaseId: "lease-full",
        deltaOperationCount: null,
      });
    },
  }, { createSnapshotId: () => "full-s1", nowIso: () => "2026-07-28T00:00:00.000Z" });

  await service.downloadAndActivate({ storeCode: "S1" });

  assert.equal(plans, 1);
  assert.equal(pageRequests[0]?.catalogVersion, "v2");
  assert.equal(
    (pageRequests[0] as Readonly<{ downloadLeaseId?: string }> | undefined)?.downloadLeaseId,
    "lease-full",
  );
  assert.equal(storage.activated, "full-s1");
});

test("启动及下一次 full 前按 500 行批次继续 retired janitor，并在批间让出队列", async () => {
  const storage = new RetiredCleanupStorage();
  storage.retiredCleanupResults = [500, 2, 0];
  storage.stagingCleanupResults = [500, 1, 0];
  let yields = 0;
  const service = new CatalogSnapshotService(storage, remote([
    page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 }),
  ]), {
    createSnapshotId: () => "full-after-cleanup",
    yieldControl: async () => { yields += 1; },
  });

  await service.downloadAndActivate({ storeCode: "S1" });

  assert.deepEqual(storage.retiredCleanupBatchSizes.slice(0, 3), [500, 500, 500]);
  assert.deepEqual(storage.stagingCleanupBatchSizes.slice(0, 3), [500, 500, 500]);
  assert.equal(yields >= 4, true);
  assert.equal(storage.activated, "full-after-cleanup");
});

test("同店相同版本不重下目录，仍更新促销并重载 active", async () => {
  const storage = new MemoryCatalogStorage();
  storage.activeMetadata = {
    snapshotId: "active-s1",
    storeCode: "S1",
    catalogVersion: "v1",
    itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  let afterActivated = 0;
  const service = new CatalogSnapshotService(storage, {
    ...remote([]),
    async getSyncPlan() {
      return syncPlan({ mode: "noChange", baseCatalogVersion: "v1", targetCatalogVersion: "v1", targetTotal: 1 });
    },
    async getPromotions() { return []; },
  }, { createSnapshotId: () => "unused", nowIso: () => "2026-07-28T00:00:00.000Z" });

  const result = await service.downloadAndActivate({
    storeCode: "S1",
    afterActivate: () => { afterActivated += 1; },
  });

  assert.equal(storage.activated, null);
  assert.deepEqual(storage.promotionReplacements, ["active-s1"]);
  assert.equal(afterActivated, 1);
  assert.equal(result.snapshotId, "active-s1");
});

test("noChange 的 targetTotal 与 active 不一致时改取 null-base full，而不复用目录", async () => {
  const storage = new MemoryCatalogStorage();
  storage.activeMetadata = {
    snapshotId: "active-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const requestedBases: (string | null)[] = [];
  const service = new CatalogSnapshotService(storage, {
    async getSyncPlan(input) {
      requestedBases.push(input.baseCatalogVersion);
      return input.baseCatalogVersion === null
        ? syncPlan({ mode: "full", baseCatalogVersion: null, targetCatalogVersion: "v2", targetTotal: 2 })
        : syncPlan({ mode: "noChange", baseCatalogVersion: "v1", targetCatalogVersion: "v1", targetTotal: 2 });
    },
    async getPage() {
      return page({ cursor: null, nextCursor: null, items: [item("A"), item("B")], totalCount: 2, catalogVersion: "v2" });
    },
  }, { createSnapshotId: () => "full-after-bad-nochange" });

  await service.downloadAndActivate({ storeCode: "S1" });

  assert.deepEqual(requestedBases, ["v1", null]);
  assert.equal(storage.activated, "full-after-bad-nochange");
});

test("sync-plan 旧端点 404 时在尚未建 staging 前退回首包固定版本 full", async () => {
  const storage = new MemoryCatalogStorage();
  const pageCalls: Parameters<CatalogSyncRemotePort["getPage"]>[0][] = [];
  const service = new CatalogSnapshotService(storage, {
    async getSyncPlan() {
      throw new HbposApiError("old backend", { kind: "http", status: 404 });
    },
    async getPage(input) {
      pageCalls.push(input);
      return page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1, catalogVersion: "legacy-v1" });
    },
  }, { createSnapshotId: () => "legacy-full" });

  await service.downloadAndActivate({ storeCode: "S1" });

  assert.equal("catalogVersion" in (pageCalls[0] ?? {}), false);
  assert.equal(storage.activated, "legacy-full");
});

test("sync-plan 501 或明确 unsupported 同样只在未建 staging 时回退 legacy full", async () => {
  const unsupportedErrors = [
    new HbposApiError("not implemented", { kind: "http", status: 501 }),
    Object.assign(new Error("unsupported"), { code: "CATALOG_SYNC_PLAN_UNSUPPORTED" }),
  ];
  for (const [index, unsupported] of unsupportedErrors.entries()) {
    const storage = new MemoryCatalogStorage();
    const service = new CatalogSnapshotService(storage, {
      async getSyncPlan() { throw unsupported; },
      async getPage() {
        return page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1, catalogVersion: "legacy-v1" });
      },
    }, { createSnapshotId: () => `legacy-full-${index}` });

    await service.downloadAndActivate({ storeCode: "S1" });

    assert.equal(storage.activated, `legacy-full-${index}`);
  }
});

test("sync-plan 的冲突、服务不可用和校验错误绝不降级为 legacy full", async () => {
  const errors = [
    new HbposApiError("conflict", { kind: "http", status: 409, code: "CATALOG_DOWNLOAD_LEASE_CONFLICT" }),
    new HbposApiError("unavailable", { kind: "http", status: 503 }),
    new HbposApiError("invalid", { kind: "envelope", code: "CATALOG_SYNC_PLAN_INVALID" }),
  ];
  for (const failure of errors) {
    const storage = new MemoryCatalogStorage();
    const service = new CatalogSnapshotService(storage, {
      async getSyncPlan() { throw failure; },
      async getPage() { throw new Error("不得请求 legacy full"); },
    }, { createSnapshotId: () => "must-not-fallback" });

    await assert.rejects(() => service.downloadAndActivate({ storeCode: "S1" }), (error: unknown) => error === failure);
    assert.equal(storage.staged.size, 0);
  }
});

test("重置目录总是以 null-base full 计划重新下载，不接受 noChange 或 delta", async () => {
  const storage = new MemoryCatalogStorage();
  storage.activeMetadata = {
    snapshotId: "active-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const requestedBases: (string | null)[] = [];
  const service = new CatalogSnapshotService(storage, {
    async getSyncPlan(input) {
      requestedBases.push(input.baseCatalogVersion);
      assert.equal(input.baseCatalogVersion, null);
      return syncPlan({ mode: "full", baseCatalogVersion: null, targetCatalogVersion: "v2", targetTotal: 1 });
    },
    async getPage(input) {
      assert.equal(input.catalogVersion, "v2");
      return page({ cursor: null, nextCursor: null, items: [item("RESET")], totalCount: 1, catalogVersion: "v2" });
    },
  }, { createSnapshotId: () => "reset-full" });

  await service.resetAndRedownload({ storeCode: "S1" });

  assert.deepEqual(requestedBases, [null]);
  assert.equal(storage.deltaBegin, null);
  assert.equal(storage.activated, "reset-full");
});

test("重置目录的 null-base sync-plan 在旧后端 404 时回退 legacy full", async () => {
  const storage = new MemoryCatalogStorage();
  const service = new CatalogSnapshotService(storage, {
    async getSyncPlan(input) {
      assert.equal(input.baseCatalogVersion, null);
      throw new HbposApiError("legacy sync plan", { kind: "http", status: 404 });
    },
    async getPage(input) {
      assert.equal("catalogVersion" in input, false);
      return page({ cursor: null, nextCursor: null, items: [item("RESET")], totalCount: 1, catalogVersion: "legacy-v1" });
    },
  }, { createSnapshotId: () => "reset-legacy-full" });

  await service.resetAndRedownload({ storeCode: "S1" });

  assert.equal(storage.activated, "reset-legacy-full");
});

test("同店 delta 只暂存变更并在原物理 snapshot 上原子激活新 generation", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("active-s1", [mapCatalogLookupToStagedItem(item("OLD")), mapCatalogLookupToStagedItem(item("KEEP"))]);
  storage.activeMetadata = {
    snapshotId: "active-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 2,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const service = new CatalogSnapshotService(storage, {
    ...remote([]),
    async getSyncPlan() {
      return syncPlan({
        mode: "delta",
        baseCatalogVersion: "v1",
        targetCatalogVersion: "v2",
        targetTotal: 2,
        downloadLeaseId: "lease-delta",
        deltaOperationCount: 2,
      });
    },
    async getDeltaPage(input) {
      assert.equal(input.baseCatalogVersion, "v1");
      assert.equal(
        (input as Readonly<{ downloadLeaseId?: string }>).downloadLeaseId,
        "lease-delta",
      );
      return {
        ...page({ cursor: null, nextCursor: null, items: [item("NEW")], totalCount: 2, catalogVersion: "v2" }),
        deletedLookups: [{ storeCode: "S1", lookupCode: "OLD", lookupCodeNormalized: "OLD", deletedAt: null }],
      };
    },
    async getPromotions() { return []; },
  }, { createSnapshotId: () => "delta-s1", nowIso: () => "2026-07-28T00:00:00.000Z" });

  const result = await service.downloadAndActivate({ storeCode: "S1" });

  assert.equal(storage.deltaBegin?.sourceSnapshotId, "active-s1");
  assert.equal(storage.deltaBegin?.baseCatalogVersion, "v1");
  assert.deepEqual(storage.deltaBatchSizes, [2]);
  assert.equal(storage.deltaActivated, "delta-s1");
  assert.equal(storage.activated, "active-s1");
  assert.deepEqual(storage.active.get("active-s1")?.map((entry) => entry.lookupCodeNormalized).sort(), ["KEEP", "NEW"]);
  assert.equal(result.snapshotId, "active-s1");
  assert.equal(result.catalogVersion, "v2");
});

test("delta 网络页按最多 500 个操作拆成短事务批次", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("active-s1", []);
  storage.activeMetadata = {
    snapshotId: "active-s1",
    storeCode: "S1",
    catalogVersion: "v1",
    itemCount: 0,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  let yields = 0;
  const changed = Array.from({ length: 501 }, (_, index) => item(`DELTA-${index}`));
  const service = new CatalogSnapshotService(storage, {
    ...remote([]),
    async getSyncPlan() {
      return syncPlan({
        mode: "delta",
        baseCatalogVersion: "v1",
        targetCatalogVersion: "v2",
        targetTotal: 501,
        deltaOperationCount: 501,
      });
    },
    async getDeltaPage() {
      return {
        ...page({
          cursor: null,
          nextCursor: null,
          items: changed,
          totalCount: 501,
          catalogVersion: "v2",
        }),
        deletedLookups: [],
      };
    },
  }, {
    createSnapshotId: () => "delta-501",
    yieldControl: async () => { yields += 1; },
  });

  await service.downloadAndActivate({ storeCode: "S1" });

  assert.deepEqual(storage.deltaBatchSizes, [500, 1]);
  assert.equal(yields, 1);
  assert.equal(storage.active.get("active-s1")?.length, 501);
});

test("服务端误报超过 5000 条 delta 时不下载增量，重新取得 full 租约", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("active-s1", [mapCatalogLookupToStagedItem(item("OLD"))]);
  storage.activeMetadata = {
    snapshotId: "active-s1",
    storeCode: "S1",
    catalogVersion: "v1",
    itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  let deltaRequests = 0;
  const service = new CatalogSnapshotService(storage, {
    async getSyncPlan(input) {
      return syncPlan(input.baseCatalogVersion === null
        ? {
          mode: "full",
          baseCatalogVersion: null,
          targetCatalogVersion: "v3",
          targetTotal: 1,
          downloadLeaseId: "lease-threshold-full",
        }
        : {
          mode: "delta",
          baseCatalogVersion: "v1",
          targetCatalogVersion: "v2",
          targetTotal: 1,
          deltaOperationCount: 5_001,
        });
    },
    async getDeltaPage() {
      deltaRequests += 1;
      throw new Error("不应请求超阈值增量");
    },
    async getPage(input) {
      assert.equal(input.catalogVersion, "v3");
      assert.equal(input.downloadLeaseId, "lease-threshold-full");
      return page({
        cursor: null,
        nextCursor: null,
        items: [item("FULL")],
        totalCount: 1,
        catalogVersion: "v3",
      });
    },
  }, { createSnapshotId: () => "full-threshold" });

  await service.downloadAndActivate({ storeCode: "S1" });

  assert.equal(deltaRequests, 0);
  assert.equal(storage.activated, "full-threshold");
});

test("delta 实际操作数与计划不符时丢弃 staging 并保留旧 active", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("active-s1", [mapCatalogLookupToStagedItem(item("OLD"))]);
  storage.activeMetadata = {
    snapshotId: "active-s1",
    storeCode: "S1",
    catalogVersion: "v1",
    itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const service = new CatalogSnapshotService(storage, {
    ...remote([]),
    async getSyncPlan() {
      return syncPlan({
        mode: "delta",
        baseCatalogVersion: "v1",
        targetCatalogVersion: "v2",
        targetTotal: 1,
        deltaOperationCount: 2,
      });
    },
    async getDeltaPage() {
      return {
        ...page({
          cursor: null,
          nextCursor: null,
          items: [item("NEW")],
          totalCount: 1,
          catalogVersion: "v2",
        }),
        deletedLookups: [],
      };
    },
  }, { createSnapshotId: () => "bad-count" });

  await assert.rejects(
    () => service.downloadAndActivate({ storeCode: "S1" }),
    catalogError("CATALOG_DELTA_OPERATION_COUNT_MISMATCH"),
  );
  assert.equal(storage.staged.has("bad-count"), false);
  assert.deepEqual(
    storage.active.get("active-s1")?.map((entry) => entry.lookupCodeNormalized),
    ["OLD"],
  );
});

test("服务端要求 full 或增量校验失败时，保留旧 active；full 则安全回退全量", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("active-s1", [mapCatalogLookupToStagedItem(item("OLD"))]);
  storage.activeMetadata = {
    snapshotId: "active-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  let mode: "full" | "delta" = "full";
  const service = new CatalogSnapshotService(storage, {
    ...remote([page({ cursor: null, nextCursor: null, items: [item("FULL")], totalCount: 1, catalogVersion: "v3" })]),
    async getSyncPlan() {
      return syncPlan(mode === "full"
        ? { mode: "full", baseCatalogVersion: "v1", targetCatalogVersion: "v3", targetTotal: 1 }
        : { mode: "delta", baseCatalogVersion: "v1", targetCatalogVersion: "v2", targetTotal: 1 });
    },
    async getDeltaPage() { throw new HbposApiError("checksum", { kind: "envelope", code: "CATALOG_DELTA_PAGE_CHECKSUM_MISMATCH" }); },
  }, { createSnapshotId: () => mode === "full" ? "full-s1" : "failed-delta", nowIso: () => "2026-07-28T00:00:00.000Z" });

  await service.downloadAndActivate({ storeCode: "S1" });
  assert.equal(storage.activated, "full-s1");
  storage.activeMetadata = {
    snapshotId: "full-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  mode = "delta";
  await assert.rejects(() => service.downloadAndActivate({ storeCode: "S1" }), /checksum/i);
  assert.equal(storage.activated, "full-s1");
  assert.equal(storage.staged.has("failed-delta"), false);
});

test("delta 基线在激活前改变时，同一次刷新清理 staging 并回退 null-base 全量", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("active-s1", [mapCatalogLookupToStagedItem(item("OLD"))]);
  storage.activeMetadata = {
    snapshotId: "active-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const snapshotIds = ["expired-delta", "full-fallback"];
  const service = new CatalogSnapshotService(storage, {
    ...remote([page({
      cursor: null,
      nextCursor: null,
      items: [item("FULL")],
      totalCount: 1,
      catalogVersion: "v3",
    })]),
    async getSyncPlan(input) {
      return syncPlan(input.baseCatalogVersion === null
        ? {
          mode: "full",
          baseCatalogVersion: null,
          targetCatalogVersion: "v3",
          targetTotal: 1,
          downloadLeaseId: "lease-full-fallback",
        }
        : {
          mode: "delta",
          baseCatalogVersion: "v1",
          targetCatalogVersion: "v2",
          targetTotal: 1,
          downloadLeaseId: "lease-expired",
          deltaOperationCount: 1,
        });
    },
    async getDeltaPage() {
      throw new HbposApiError("base changed", {
        kind: "envelope",
        code: "CATALOG_DELTA_BASE_CHANGED",
        status: 409,
      });
    },
  }, {
    createSnapshotId: () => {
      const snapshotId = snapshotIds.shift();
      assert.ok(snapshotId);
      return snapshotId;
    },
    nowIso: () => "2026-07-28T00:00:00.000Z",
  });

  const result = await service.downloadAndActivate({ storeCode: "S1" });

  assert.equal(storage.staged.has("expired-delta"), false);
  assert.equal(storage.activated, "full-fallback");
  assert.equal(result.catalogVersion, "v3");
  assert.deepEqual(
    storage.active.get("full-fallback")?.map((entry) => entry.lookupCodeNormalized),
    ["FULL"],
  );
});

test("delta 回退的 null-base sync-plan 在旧后端 501 时继续走 legacy full", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("active-s1", [mapCatalogLookupToStagedItem(item("OLD"))]);
  storage.activeMetadata = {
    snapshotId: "active-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const bases: (string | null)[] = [];
  const snapshotIds = ["delta-staging", "legacy-full"];
  const service = new CatalogSnapshotService(storage, {
    async getSyncPlan(input) {
      bases.push(input.baseCatalogVersion);
      if (input.baseCatalogVersion === null) {
        throw new HbposApiError("legacy sync plan", { kind: "http", status: 501 });
      }
      return syncPlan({
        mode: "delta", baseCatalogVersion: "v1", targetCatalogVersion: "v2",
        targetTotal: 1, deltaOperationCount: 1,
      });
    },
    async getDeltaPage() {
      throw new HbposApiError("base changed", {
        kind: "envelope", code: "CATALOG_DELTA_BASE_CHANGED", status: 409,
      });
    },
    async getPage(input) {
      assert.equal("catalogVersion" in input, false);
      return page({ cursor: null, nextCursor: null, items: [item("FULL")], totalCount: 1, catalogVersion: "legacy-v3" });
    },
  }, {
    createSnapshotId: () => {
      const snapshotId = snapshotIds.shift();
      assert.ok(snapshotId);
      return snapshotId;
    },
  });

  await service.downloadAndActivate({ storeCode: "S1" });

  assert.deepEqual(bases, ["v1", null]);
  assert.equal(storage.staged.has("delta-staging"), false);
  assert.equal(storage.activated, "legacy-full");
});

test("固定 lookup 快照分页下载，显式转整数分后才原子切换 active", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("old", [mapCatalogLookupToStagedItem(item("OLD"))]);
  const service = new CatalogSnapshotService(storage, remote([
    page({
      cursor: null,
      nextCursor: "c2",
      items: [item("A", { retailPrice: 12.34 })],
      totalCount: 2,
    }),
    page({
      cursor: "c2",
      nextCursor: null,
      items: [item("B", { productCode: "P-A", retailPrice: 0.05 })],
      totalCount: 2,
    }),
  ]), {
    createSnapshotId: () => "snapshot-v2",
    nowIso: () => "2026-07-28T00:00:00.000Z",
  });

  const events: { step: string; percent: number }[] = [];
  const result = await service.downloadAndActivate({
    storeCode: "S1",
    onProgress(event) {
      events.push({ step: event.step, percent: event.percent });
      throw new Error("观察器故障不得阻断目录刷新");
    },
  });

  assert.equal(storage.activated, "snapshot-v2");
  assert.deepEqual(result, {
    snapshotId: "snapshot-v2",
    catalogVersion: "catalog-v2",
    itemCount: 2,
    activatedAt: "2026-07-28T00:00:00.000Z",
  });
  assert.deepEqual(events, [
    { step: "prepare", percent: 0 },
    { step: "prepare", percent: 100 },
    { step: "products", percent: 0 },
    { step: "products", percent: 50 },
    { step: "products", percent: 100 },
    { step: "promotions", percent: 0 },
    { step: "promotions", percent: 100 },
    { step: "activate", percent: 0 },
    { step: "activate", percent: 100 },
  ]);
  assert.deepEqual(
    storage.active.get("snapshot-v2")?.map((entry) => ({
      lookup: entry.lookupCodeNormalized,
      cents: entry.retailPriceCents,
    })),
    [
      { lookup: "A", cents: 1234 },
      { lookup: "B", cents: 5 },
    ],
  );
});

test("第一页省略版本，后续分页固定使用首包 catalogVersion", async () => {
  const calls: Readonly<{
    storeCode: string;
    cursor: string | null;
    pageSize: number;
    catalogVersion?: string;
  }>[] = [];
  const pages = [
    page({
      cursor: null,
      nextCursor: "c2",
      items: [item("A")],
      totalCount: 2,
      catalogVersion: "catalog-v1:pinned",
    }),
    page({
      cursor: "c2",
      nextCursor: null,
      items: [item("B")],
      totalCount: 2,
      catalogVersion: "catalog-v1:pinned",
    }),
  ] as const;
  const service = new CatalogSnapshotService(
    new MemoryCatalogStorage(),
    {
      async getPage(input) {
        calls.push(input);
        const result = pages.find((candidate) => candidate.cursor === input.cursor);
        if (!result) throw new Error("unexpected cursor");
        return result;
      },
    },
    { createSnapshotId: () => "pinned-version" },
  );

  await service.downloadAndActivate({ storeCode: "S1" });

  assert.equal("catalogVersion" in (calls[0] ?? {}), false);
  assert.equal(calls[1]?.catalogVersion, "catalog-v1:pinned");
});

test("按 WPF 的 5000 条网络页下载，整页验证后最多每 500 条写入并报告真实页数与耗时", async () => {
  const storage = new MemoryCatalogStorage();
  const requestedPages: Parameters<CatalogSyncRemotePort["getPage"]>[0][] = [];
  const controller = new AbortController();
  let nowMilliseconds = 1_000;
  const items = Array.from({ length: 1_201 }, (_, index) => item(`ITEM-${index}`));
  const events: {
    step: string;
    percent: number;
    elapsedMilliseconds?: number;
    completedItemCount?: number;
    completedPageCount?: number;
    totalPageCount?: number;
  }[] = [];
  const service = new CatalogSnapshotService(
    storage,
    {
      async getPage(input) {
        requestedPages.push(input);
        nowMilliseconds += 250;
        return page({
          cursor: null,
          nextCursor: null,
          items,
          totalCount: items.length,
        });
      },
    },
    {
      createSnapshotId: () => "wpf-page-size",
      nowMilliseconds: () => nowMilliseconds,
    },
  );

  await service.downloadAndActivate({
    storeCode: "S1",
    signal: controller.signal,
    onProgress: (event) => events.push(event),
  });

  assert.equal(requestedPages.length, 1);
  assert.equal(requestedPages[0]?.pageSize, 5_000);
  assert.equal(requestedPages[0]?.signal, controller.signal);
  assert.deepEqual(storage.appendBatchSizes, [500, 500, 201]);
  const productEvents = events.filter((event) => event.step === "products");
  assert.deepEqual(
    productEvents.map((event) => ({
      percent: event.percent,
      completedItemCount: event.completedItemCount,
      completedPageCount: event.completedPageCount,
      totalPageCount: event.totalPageCount,
    })),
    [
      { percent: 0, completedItemCount: 0, completedPageCount: 0, totalPageCount: 1 },
      { percent: 41, completedItemCount: 500, completedPageCount: 0, totalPageCount: 1 },
      { percent: 83, completedItemCount: 1_000, completedPageCount: 0, totalPageCount: 1 },
      { percent: 100, completedItemCount: 1_201, completedPageCount: 1, totalPageCount: 1 },
    ],
  );
  assert.equal(events[0]?.step, "prepare");
  assert.equal(events[0]?.percent, 0);
  assert.equal(events[0]?.elapsedMilliseconds, 0);
  assert.equal(
    events.every((event, index) =>
      index === 0
      || (event.elapsedMilliseconds ?? 0) >= (events[index - 1]?.elapsedMilliseconds ?? 0)),
    true,
  );
});

test("空目录在验证并写入暂存后将商品步骤真实推进到百分之百", async () => {
  const events: { step: string; percent: number }[] = [];
  const service = new CatalogSnapshotService(
    new MemoryCatalogStorage(),
    remote([page({ cursor: null, nextCursor: null, items: [], totalCount: 0 })]),
    { createSnapshotId: () => "empty-catalog" },
  );

  await service.downloadAndActivate({
    storeCode: "S1",
    onProgress: (event) => events.push({ step: event.step, percent: event.percent }),
  });

  assert.deepEqual(
    events.filter((event) => event.step === "products"),
    [
      { step: "products", percent: 0 },
      { step: "products", percent: 100 },
    ],
  );
});

test("远端目录版本在开始暂存前严格拒绝空白、控制字符与过长值", async () => {
  const invalidVersions = ["", " catalog-v2", "catalog-v2 ", "catalog\u0000v2", "v".repeat(513)];
  for (const [index, catalogVersion] of invalidVersions.entries()) {
    const storage = new MemoryCatalogStorage();
    const service = new CatalogSnapshotService(
      storage,
      remote([page({
        cursor: null,
        nextCursor: null,
        items: [item("A")],
        totalCount: 1,
        catalogVersion,
      })]),
      { createSnapshotId: () => `invalid-version-${index}` },
    );

    await assert.rejects(
      () => service.downloadAndActivate({ storeCode: "S1" }),
      catalogError("CATALOG_SNAPSHOT_VERSION_INVALID"),
    );
    assert.equal(storage.staged.size, 0);
  }
});

test("未闭合分页即使已达到 total 也不报告商品步骤百分之百", async () => {
  const events: { step: string; percent: number }[] = [];
  const service = new CatalogSnapshotService(
    new MemoryCatalogStorage(),
    remote([page({
      cursor: null,
      nextCursor: "missing-page",
      items: [item("A")],
      totalCount: 1,
    })]),
    { createSnapshotId: () => "unclosed-page" },
  );

  await assert.rejects(
    () => service.downloadAndActivate({
      storeCode: "S1",
      onProgress: (event) => events.push({ step: event.step, percent: event.percent }),
    }),
    /cursor/i,
  );
  assert.deepEqual(
    events.filter((event) => event.step === "products"),
    [
      { step: "products", percent: 0 },
      { step: "products", percent: 99 },
    ],
  );
});

test("最终页数量与服务端 total 不闭合时不报告商品步骤百分之百", async () => {
  const productPercents: number[] = [];
  const service = new CatalogSnapshotService(
    new MemoryCatalogStorage(),
    remote([page({
      cursor: null,
      nextCursor: null,
      items: [item("A")],
      totalCount: 2,
    })]),
    { createSnapshotId: () => "mismatched-final-count" },
  );

  await assert.rejects(
    () => service.downloadAndActivate({
      storeCode: "S1",
      onProgress: (event) => {
        if (event.step === "products") productPercents.push(event.percent);
      },
    }),
    catalogError("CATALOG_ITEM_COUNT_MISMATCH"),
  );
  assert.deepEqual(productPercents, [0]);
});

test("接近完成的 full 校验失败按 500 行分批丢弃 staging，旧 active 保持可用", async () => {
  const storage = new BoundedDiscardStorage();
  storage.active.set("old", [mapCatalogLookupToStagedItem(item("OLD"))]);
  const items = Array.from({ length: 501 }, (_, index) => item(`NEW-${index}`));
  let yields = 0;
  const service = new CatalogSnapshotService(
    storage,
    remote([page({ cursor: null, nextCursor: null, items, totalCount: 502 })]),
    {
      createSnapshotId: () => "large-invalid-full",
      yieldControl: async () => { yields += 1; },
    },
  );

  await assert.rejects(
    () => service.downloadAndActivate({ storeCode: "S1" }),
    catalogError("CATALOG_ITEM_COUNT_MISMATCH"),
  );

  assert.deepEqual(storage.discardBatchSizes, [500, 500, 500, 500]);
  assert.equal(yields, 3);
  assert.equal(storage.staged.has("large-invalid-full"), false);
  assert.equal(storage.active.has("old"), true);
});

test("取消在暂存后只清理 staging，既有 active 目录保持可用", async () => {
  const controller = new AbortController();
  const storage = new MemoryCatalogStorage();
  storage.active.set("old", [mapCatalogLookupToStagedItem(item("OLD"))]);
  const service = new CatalogSnapshotService(
    storage,
    remote([page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 })]),
    { createSnapshotId: () => "cancelled" },
  );

  await assert.rejects(
    () => service.downloadAndActivate({
      storeCode: "S1",
      signal: controller.signal,
      onProgress: (event) => {
        if (event.step === "products" && event.percent === 0) controller.abort();
      },
    }),
    /cancelled/i,
  );
  assert.equal(storage.active.has("old"), true);
  assert.equal(storage.staged.has("cancelled"), false);
});

test("同一取消信号穿透所有分页请求，在途取消后清理 staging 并释放串行队列", async () => {
  const controller = new AbortController();
  const secondPageStarted = deferred<void>();
  const capturedSignals: (AbortSignal | undefined)[] = [];
  let calls = 0;
  let nextId = 0;
  const storage = new MemoryCatalogStorage();
  storage.active.set("old", [mapCatalogLookupToStagedItem(item("OLD"))]);
  const service = new CatalogSnapshotService(
    storage,
    {
      async getPage(input) {
        calls += 1;
        capturedSignals.push(input.signal);
        if (calls === 1) {
          return page({
            cursor: null,
            nextCursor: "c2",
            items: [item("A")],
            totalCount: 2,
          });
        }
        if (calls === 2) {
          secondPageStarted.resolve();
          return new Promise<VerifiedCatalogSyncPage>((_resolve, reject) => {
            input.signal?.addEventListener(
              "abort",
              () => reject(new Error("Catalog request cancelled.")),
              { once: true },
            );
          });
        }
        return page({
          cursor: null,
          nextCursor: null,
          items: [item("RECOVERED")],
          totalCount: 1,
        });
      },
    },
    { createSnapshotId: () => `cancel-in-flight-${++nextId}` },
  );

  const cancelled = service.downloadAndActivate({
    storeCode: "S1",
    signal: controller.signal,
  });
  await secondPageStarted.promise;
  controller.abort();
  await assert.rejects(() => cancelled, /cancel/i);

  assert.deepEqual(capturedSignals.slice(0, 2), [
    controller.signal,
    controller.signal,
  ]);
  assert.equal(storage.active.has("old"), true);
  assert.equal(storage.staged.has("cancel-in-flight-1"), false);

  const recovered = await service.downloadAndActivate({ storeCode: "S1" });
  assert.equal(recovered.snapshotId, "cancel-in-flight-2");
  assert.equal(storage.activated, "cancel-in-flight-2");
});

test("beginStaging 成功瞬间取消仍会清理 staging", async () => {
  const controller = new AbortController();
  const storage = new AbortAfterBeginStorage(controller);
  storage.active.set("old", [mapCatalogLookupToStagedItem(item("OLD"))]);
  const service = new CatalogSnapshotService(
    storage,
    remote([page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 })]),
    { createSnapshotId: () => "abort-after-begin" },
  );

  await assert.rejects(
    () => service.downloadAndActivate({ storeCode: "S1", signal: controller.signal }),
    /cancelled/i,
  );
  assert.equal(storage.active.has("old"), true);
  assert.equal(storage.staged.has("abort-after-begin"), false);
});

test("同一快照服务串行刷新，后发请求不会越过在途激活", async () => {
  const gate = deferred<void>();
  let calls = 0;
  let nextId = 0;
  const storage = new RecordingActivationStorage();
  const service = new CatalogSnapshotService(
    storage,
    {
      async getPage() {
        calls += 1;
        if (calls === 1) await gate.promise;
        return page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 });
      },
    },
    { createSnapshotId: () => `serial-${++nextId}` },
  );

  const first = service.downloadAndActivate({ storeCode: "S1" });
  const second = service.downloadAndActivate({ storeCode: "S1" });
  await Promise.resolve();
  assert.equal(calls, 1);
  gate.resolve();
  await Promise.all([first, second]);
  assert.deepEqual(storage.activationOrder, ["serial-1", "serial-2"]);
});

test("后置运行时重载未完成时，下一次刷新不会越过同一串行临界区", async () => {
  const afterActivateEntered = deferred<void>();
  const releaseAfterActivate = deferred<void>();
  const firstEvents: { step: string; percent: number }[] = [];
  let remoteCalls = 0;
  let nextId = 0;
  const storage = new RecordingActivationStorage();
  const service = new CatalogSnapshotService(
    storage,
    {
      async getPage() {
        remoteCalls += 1;
        return page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 });
      },
    },
    { createSnapshotId: () => `after-activate-${++nextId}` },
  );

  const first = service.downloadAndActivate({
    storeCode: "S1",
    onProgress: (event) => firstEvents.push(event),
    afterActivate: async () => {
      afterActivateEntered.resolve();
      await releaseAfterActivate.promise;
    },
  });
  await afterActivateEntered.promise;
  const second = service.downloadAndActivate({ storeCode: "S1" });
  await Promise.resolve();

  assert.equal(remoteCalls, 1);
  assert.deepEqual(storage.activationOrder, ["after-activate-1"]);
  assert.equal(
    firstEvents.some((event) => event.step === "activate" && event.percent === 100),
    false,
  );

  releaseAfterActivate.resolve();
  await Promise.all([first, second]);
  assert.equal(remoteCalls, 2);
  assert.deepEqual(storage.activationOrder, ["after-activate-1", "after-activate-2"]);
  assert.equal(
    firstEvents.some((event) => event.step === "activate" && event.percent === 100),
    true,
  );
});

test("后置回调失败不会把已提交 active 当作切换前失败清理", async () => {
  const events: { step: string; percent: number }[] = [];
  const storage = new MemoryCatalogStorage();
  const service = new CatalogSnapshotService(
    storage,
    remote([page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 })]),
    { createSnapshotId: () => "post-activation-failure" },
  );

  await assert.rejects(
    () => service.downloadAndActivate({
      storeCode: "S1",
      onProgress: (event) => events.push(event),
      afterActivate: () => {
        throw new Error("runtime reload failed");
      },
    }),
    /runtime reload failed/,
  );
  assert.equal(storage.activated, "post-activation-failure");
  assert.equal(storage.staged.has("post-activation-failure"), true);
  assert.equal(
    events.some((event) => event.step === "activate" && event.percent === 100),
    false,
  );
});

test("分页总数、版本、门店或 continuation 不一致时返回稳定校验码并清理 staging", async () => {
  const cases: readonly Readonly<{
    pages: readonly VerifiedCatalogSyncPage[];
    code: string;
  }>[] = [
    {
      pages: [page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 2 })],
      code: "CATALOG_ITEM_COUNT_MISMATCH",
    },
    {
      pages: [
        page({ cursor: null, nextCursor: "c2", items: [item("A")], totalCount: 2 }),
        page({ cursor: "c2", nextCursor: null, items: [item("B")], totalCount: 2, catalogVersion: "catalog-v3" }),
      ],
      code: "CATALOG_SNAPSHOT_VERSION_CHANGED",
    },
    {
      pages: [
        page({ cursor: null, nextCursor: "c2", items: [item("A")], totalCount: 2 }),
        page({ cursor: "c2", nextCursor: null, items: [item("B")], totalCount: 3 }),
      ],
      code: "CATALOG_SNAPSHOT_TOTAL_CHANGED",
    },
    {
      pages: [page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1, storeCode: "OTHER" })],
      code: "CATALOG_STORE_MISMATCH",
    },
    {
      pages: [page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1, hasMore: true })],
      code: "CATALOG_PAGINATION_INVALID",
    },
    {
      pages: [
        page({ cursor: null, nextCursor: "c2", items: [item("A")], totalCount: 3 }),
        page({ cursor: "c2", nextCursor: "c2", items: [item("B")], totalCount: 3 }),
      ],
      code: "CATALOG_CURSOR_REPEATED",
    },
  ];

  for (const [index, entry] of cases.entries()) {
    const storage = new MemoryCatalogStorage();
    storage.active.set("old", [mapCatalogLookupToStagedItem(item("OLD"))]);
    const service = new CatalogSnapshotService(storage, remote(entry.pages), {
      createSnapshotId: () => `failed-${index}`,
    });

    await assert.rejects(
      () => service.downloadAndActivate({ storeCode: "S1" }),
      catalogError(entry.code),
    );
    assert.equal(storage.active.has("old"), true);
    assert.equal(storage.staged.has(`failed-${index}`), false);
  }
});

test("服务端页 cursor 与请求不一致时返回稳定校验码且不开始 staging", async () => {
  const storage = new MemoryCatalogStorage();
  const service = new CatalogSnapshotService(
    storage,
    {
      async getPage() {
        return page({
          cursor: "unexpected",
          nextCursor: null,
          items: [item("A")],
          totalCount: 1,
        });
      },
    },
    { createSnapshotId: () => "cursor-mismatch" },
  );

  await assert.rejects(
    () => service.downloadAndActivate({ storeCode: "S1" }),
    catalogError("CATALOG_CURSOR_MISMATCH"),
  );
  assert.equal(storage.staged.size, 0);
});

test("目录校验异常携带安全分页上下文且仍清理 staging", async () => {
  const storage = new MemoryCatalogStorage();
  const service = new CatalogSnapshotService(
    storage,
    remote([
      page({
        cursor: null,
        nextCursor: "c2",
        items: [item("A")],
        totalCount: 2,
      }),
      page({
        cursor: "c2",
        nextCursor: null,
        items: [item("B")],
        totalCount: 2,
        catalogVersion: "catalog-v3",
      }),
    ]),
    { createSnapshotId: () => "diagnostic" },
  );

  await assert.rejects(
    () => service.downloadAndActivate({ storeCode: "S1" }),
    (error: unknown) => {
      assert.ok(error instanceof CatalogSnapshotFailure);
      assert.deepEqual(error.context, {
        code: "CATALOG_SNAPSHOT_VERSION_CHANGED",
        pageNumber: 2,
        completedItemCount: 1,
        totalItemCount: 2,
      });
      return true;
    },
  );
  assert.equal(storage.staged.has("diagnostic"), false);
});

test("固定快照过期保留服务端精确错误码、HTTP 状态和已完成位置", async () => {
  const storage = new MemoryCatalogStorage();
  let requestCount = 0;
  const service = new CatalogSnapshotService(
    storage,
    {
      async getPage() {
        requestCount += 1;
        if (requestCount === 1) {
          return page({
            cursor: null,
            nextCursor: "c2",
            items: [item("A")],
            totalCount: 2,
          });
        }
        throw new HbposApiError("response body must not enter diagnostics", {
          kind: "envelope",
          code: "CATALOG_SNAPSHOT_EXPIRED",
          status: 409,
        });
      },
    },
    { createSnapshotId: () => "expired-diagnostic" },
  );

  await assert.rejects(
    () => service.downloadAndActivate({ storeCode: "S1" }),
    (error: unknown) => {
      assert.ok(error instanceof CatalogSnapshotFailure);
      assert.deepEqual(error.context, {
        code: "CATALOG_SNAPSHOT_EXPIRED",
        pageNumber: 2,
        completedItemCount: 1,
        totalItemCount: 2,
        httpStatus: 409,
      });
      assert.equal(error.message.includes("response body"), false);
      return true;
    },
  );
  assert.equal(storage.staged.has("expired-diagnostic"), false);
});

class RecordingActivationStorage extends MemoryCatalogStorage {
  public readonly activationOrder: string[] = [];

  public override async activate(snapshotId: string, expectedItemCount: number): Promise<void> {
    await super.activate(snapshotId, expectedItemCount);
    this.activationOrder.push(snapshotId);
  }
}

function deferred<T>(): Readonly<{
  promise: Promise<T>;
  resolve(value: T): void;
}> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

test("促销合同校验失败时仅丢弃 staging，既有 active 目录绝不清空", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("old", [mapCatalogLookupToStagedItem(item("OLD"))]);
  const service = new CatalogSnapshotService(storage, {
    ...remote([page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 })]),
    async getPromotions() {
      throw new Error("Catalog promotion field is invalid: response storeCode.");
    },
  }, { createSnapshotId: () => "promotion-invalid" });

  await assert.rejects(() => service.downloadAndActivate({ storeCode: "S1" }), /promotion/i);
  assert.equal(storage.active.has("old"), true);
  assert.equal(storage.staged.has("promotion-invalid"), false);
});

test("同商品可保留多条售卖码，但重复规范化 lookup 在落库前拒绝", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("old", [mapCatalogLookupToStagedItem(item("OLD"))]);
  const service = new CatalogSnapshotService(storage, remote([
    page({
      cursor: null,
      nextCursor: "c2",
      items: [item("A", { productCode: "P-SAME" })],
      totalCount: 2,
    }),
    page({
      cursor: "c2",
      nextCursor: null,
      items: [item("A", { productCode: "P-SAME", barcode: "ANOTHER-SOURCE" })],
      totalCount: 2,
    }),
  ]), { createSnapshotId: () => "duplicate" });

  await assert.rejects(
    () => service.downloadAndActivate({ storeCode: "S1" }),
    catalogError("CATALOG_DUPLICATE_LOOKUP"),
  );
  assert.equal(storage.active.has("old"), true);
});

test("商品字段级脏数据归一化后仍可激活本地目录", async () => {
  const storage = new MemoryCatalogStorage();
  const service = new CatalogSnapshotService(storage, remote([
    page({
      cursor: null,
      nextCursor: null,
      items: [
        item("NEGATIVE", {
          retailPrice: -0.01,
          quantityFactor: 0,
          discountRate: -0.25,
          updatedAt: "invalid timestamp",
        }),
        item("FRACTIONAL", {
          retailPrice: 1.001,
          quantityFactor: -3,
          discountRate: 1.25,
          displayName: "   ",
          priceSourceLabel: "   ",
          referenceCode: "   ",
        }),
        item("UNSAFE", {
          retailPrice: Number.MAX_SAFE_INTEGER,
          quantityFactor: Number.POSITIVE_INFINITY,
          discountRate: Number.NaN,
        }),
      ],
      totalCount: 3,
    }),
  ]), { createSnapshotId: () => "normalized" });

  const result = await service.downloadAndActivate({ storeCode: "S1" });
  const active = storage.active.get("normalized");

  assert.equal(result.snapshotId, "normalized");
  assert.equal(active?.length, 3);
  assert.deepEqual(
    active?.map((entry) => ({
      lookupCode: entry.lookupCode,
      retailPriceCents: entry.retailPriceCents,
      quantityFactor: entry.quantityFactor,
      discountRate: entry.discountRate,
      updatedAtIso: entry.updatedAtIso,
      displayName: entry.displayName,
      priceSourceLabel: entry.priceSourceLabel,
      referenceCode: entry.referenceCode,
    })),
    [
      {
        lookupCode: "NEGATIVE",
        retailPriceCents: 0,
        quantityFactor: 1,
        discountRate: 0,
        updatedAtIso: null,
        displayName: "商品 NEGATIVE",
        priceSourceLabel: "product",
        referenceCode: null,
      },
      {
        lookupCode: "FRACTIONAL",
        retailPriceCents: 100,
        quantityFactor: 1,
        discountRate: 1,
        updatedAtIso: "2026-07-28T00:00:00.000Z",
        displayName: "I-FRACTIONAL",
        priceSourceLabel: "catalog",
        referenceCode: null,
      },
      {
        lookupCode: "UNSAFE",
        retailPriceCents: 0,
        quantityFactor: 1,
        discountRate: null,
        updatedAtIso: "2026-07-28T00:00:00.000Z",
        displayName: "商品 UNSAFE",
        priceSourceLabel: "product",
        referenceCode: null,
      },
    ],
  );
});

test("售价按十进制半分舍入，溯源字段保真且非法日历日期归空", () => {
  const halfCent = mapCatalogLookupToStagedItem(item("HALF-CENT", {
    retailPrice: 1.005,
    referenceCode: " REF-001 ",
    itemNumber: " ITEM-001 ",
    barcode: " BARCODE-001 ",
    rowVersion: " ROW-001 ",
    productImage: " https://example.test/product.png ",
    updatedAt: "2026-02-30T00:00:00.000Z",
  }));
  const offsetTimestamp = mapCatalogLookupToStagedItem(item("OFFSET", {
    retailPrice: 10.075,
    updatedAt: "2026-07-28T11:12:13+10:00",
  }));
  const largeWholeCent = mapCatalogLookupToStagedItem(item("LARGE", {
    retailPrice: 40_000_000_000_000,
  }));
  const largerWholeCent = mapCatalogLookupToStagedItem(item("LARGER", {
    retailPrice: 90_000_000_000_000,
  }));

  assert.equal(halfCent.retailPriceCents, 101);
  assert.equal(halfCent.referenceCode, " REF-001 ");
  assert.equal(halfCent.itemNumber, " ITEM-001 ");
  assert.equal(halfCent.barcode, " BARCODE-001 ");
  assert.equal(halfCent.rowVersion, " ROW-001 ");
  assert.equal(halfCent.productImage, " https://example.test/product.png ");
  assert.equal(halfCent.updatedAtIso, null);
  assert.equal(offsetTimestamp.retailPriceCents, 1_008);
  assert.equal(offsetTimestamp.updatedAtIso, "2026-07-28T11:12:13+10:00");
  assert.equal(largeWholeCent.retailPriceCents, 4_000_000_000_000_000);
  assert.equal(largerWholeCent.retailPriceCents, 9_000_000_000_000_000);
});

function catalogError(code: string): (error: unknown) => boolean {
  return (error: unknown) =>
    error instanceof HbposApiError
    && error.kind === "envelope"
    && error.code === code;
}

async function waitForCondition(predicate: () => boolean, timeoutMs = 2_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  assert.fail("condition not met before timeout");
}

test("full 下载在落库当前页期间预取下一页（网络与写入重叠）", async () => {
  const storage = new MemoryCatalogStorage();
  const calls: string[] = [];
  let releaseAppend: (() => void) | null = null;
  let gated = false;
  const originalAppendPage = storage.appendPage.bind(storage);
  storage.appendPage = async (snapshotId, items) => {
    calls.push(`append:${items.length}`);
    // 中文注释：只挂起第一页落库，验证预取请求已在此期间发出；后续页正常放行。
    if (!gated) {
      gated = true;
      await new Promise<void>((resolve) => { releaseAppend = resolve; });
    }
    await originalAppendPage(snapshotId, items);
  };
  const service = new CatalogSnapshotService(storage, {
    async getPage(input) {
      calls.push(`fetch:${input.cursor ?? "null"}`);
      return input.cursor === null
        ? page({ cursor: null, nextCursor: "C2", items: [item("A")], totalCount: 2 })
        : page({ cursor: "C2", nextCursor: null, items: [item("B")], totalCount: 2 });
    },
  }, { createSnapshotId: () => "prefetch-s1", nowIso: () => "2026-07-28T00:00:00.000Z" });

  const operation = service.downloadAndActivate({ storeCode: "S1" });
  await waitForCondition(() => calls.some((call) => call.startsWith("append:")));
  // 中文注释：第一页落库仍被挂起时，第二页请求必须已发出。
  assert.equal(calls.some((call) => call === "fetch:C2"), true);
  (releaseAppend as (() => void) | null)?.();
  await operation;
  assert.equal(storage.activated, "prefetch-s1");
  assert.deepEqual(calls.filter((call) => call.startsWith("fetch:")), ["fetch:null", "fetch:C2"]);
});

test("full 下载最后一页不发起多余预取，请求总数等于页数", async () => {
  const storage = new MemoryCatalogStorage();
  let fetchCount = 0;
  const service = new CatalogSnapshotService(storage, {
    async getPage(input) {
      fetchCount += 1;
      return page({ cursor: input.cursor, nextCursor: null, items: [item("A")], totalCount: 1 });
    },
  }, { createSnapshotId: () => "single-page", nowIso: () => "2026-07-28T00:00:00.000Z" });

  await service.downloadAndActivate({ storeCode: "S1" });
  assert.equal(fetchCount, 1);
  assert.equal(storage.activated, "single-page");
});

test("delta 下载在落库当前页期间预取下一页并保持 cursor 顺序", async () => {
  const storage = new MemoryCatalogStorage();
  storage.active.set("active-s1", [mapCatalogLookupToStagedItem(item("OLD"))]);
  storage.activeMetadata = {
    snapshotId: "active-s1",
    storeCode: "S1",
    catalogVersion: "v1",
    itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const calls: string[] = [];
  let releaseAppend: (() => void) | null = null;
  let gated = false;
  const originalAppendDeltaBatch = storage.appendDeltaBatch.bind(storage);
  storage.appendDeltaBatch = async (snapshotId, batch) => {
    calls.push(`append:${batch.items.length + batch.deletedLookups.length}`);
    // 中文注释：只挂起第一页 delta 落库，验证预取请求已在此期间发出；后续页正常放行。
    if (!gated) {
      gated = true;
      await new Promise<void>((resolve) => { releaseAppend = resolve; });
    }
    await originalAppendDeltaBatch(snapshotId, batch);
  };
  const service = new CatalogSnapshotService(storage, {
    ...remote([]),
    async getSyncPlan() {
      return syncPlan({
        mode: "delta",
        baseCatalogVersion: "v1",
        targetCatalogVersion: "v2",
        targetTotal: 3,
        deltaOperationCount: 2,
      });
    },
    async getDeltaPage(input) {
      calls.push(`delta:${input.cursor ?? "null"}`);
      return {
        ...page({
          cursor: input.cursor,
          nextCursor: input.cursor === null ? "C2" : null,
          items: [item(input.cursor === null ? "NEW1" : "NEW2")],
          totalCount: 3,
          catalogVersion: "v2",
        }),
        deletedLookups: [],
      };
    },
  }, { createSnapshotId: () => "delta-prefetch", nowIso: () => "2026-07-28T00:00:00.000Z" });

  const operation = service.downloadAndActivate({ storeCode: "S1" });
  await waitForCondition(() => calls.some((call) => call.startsWith("append:")));
  // 中文注释：第一页 delta 落库挂起期间，第二页请求必须已发出。
  assert.equal(calls.some((call) => call === "delta:C2"), true);
  (releaseAppend as (() => void) | null)?.();
  await operation;
  assert.equal(storage.deltaActivated, "delta-prefetch");
  assert.deepEqual(calls.filter((call) => call.startsWith("delta:")), ["delta:null", "delta:C2"]);
});

class RecordingCodeConflictRefresh implements CatalogCodeConflictRefreshPort {
  public readonly calls: Readonly<{ storeCode: string; aborted: boolean }>[] = [];

  public constructor(
    private readonly events: string[] = [],
    private readonly failure: unknown = null,
  ) {}

  public async refresh(input: Readonly<{
    storeCode: string;
    signal?: AbortSignal;
  }>): Promise<void> {
    this.calls.push({
      storeCode: input.storeCode,
      aborted: input.signal?.aborted ?? false,
    });
    this.events.push("code-conflicts");
    if (this.failure !== null) throw this.failure;
  }
}

test("全量、增量与 noChange 刷新成功后都在 afterActivate 前刷新码冲突候选", async () => {
  const fullEvents: string[] = [];
  const fullStorage = new MemoryCatalogStorage();
  const fullConflicts = new RecordingCodeConflictRefresh(fullEvents);
  const full = new CatalogSnapshotService(fullStorage, remote([
    page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 }),
  ]), {
    createSnapshotId: () => "full-conflicts",
    codeConflicts: fullConflicts,
  });
  await full.downloadAndActivate({
    storeCode: "S1",
    afterActivate: () => {
      fullEvents.push(`after:${fullStorage.activated ?? "none"}`);
    },
  });
  assert.deepEqual(fullEvents, ["code-conflicts", "after:full-conflicts"]);
  assert.deepEqual(fullConflicts.calls, [{ storeCode: "S1", aborted: false }]);

  const deltaEvents: string[] = [];
  const deltaStorage = new MemoryCatalogStorage();
  deltaStorage.active.set("active-s1", [mapCatalogLookupToStagedItem(item("KEEP"))]);
  deltaStorage.activeMetadata = {
    snapshotId: "active-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const deltaConflicts = new RecordingCodeConflictRefresh(deltaEvents);
  const delta = new CatalogSnapshotService(deltaStorage, {
    ...remote([]),
    async getSyncPlan() {
      return syncPlan({
        mode: "delta",
        baseCatalogVersion: "v1",
        targetCatalogVersion: "v2",
        targetTotal: 2,
        deltaOperationCount: 1,
      });
    },
    async getDeltaPage() {
      return {
        ...page({ cursor: null, nextCursor: null, items: [item("NEW")], totalCount: 2, catalogVersion: "v2" }),
        deletedLookups: [],
      };
    },
  }, { createSnapshotId: () => "delta-conflicts", codeConflicts: deltaConflicts });
  await delta.downloadAndActivate({
    storeCode: "S1",
    afterActivate: () => {
      deltaEvents.push(`after:${deltaStorage.deltaActivated ?? "none"}`);
    },
  });
  assert.deepEqual(deltaEvents, ["code-conflicts", "after:delta-conflicts"]);

  // 中文注释：服务端 noChange 只比较胜出行；候选可能已变化，仍须刷新。
  const noChangeEvents: string[] = [];
  const noChangeStorage = new MemoryCatalogStorage();
  noChangeStorage.activeMetadata = {
    snapshotId: "active-s1", storeCode: "S1", catalogVersion: "v1", itemCount: 1,
    activatedAt: "2026-07-28T00:00:00.000Z",
  };
  const noChangeConflicts = new RecordingCodeConflictRefresh(noChangeEvents);
  const noChange = new CatalogSnapshotService(noChangeStorage, {
    ...remote([]),
    async getSyncPlan() {
      return syncPlan({ mode: "noChange", baseCatalogVersion: "v1", targetCatalogVersion: "v1", targetTotal: 1 });
    },
    async getPromotions() {
      noChangeEvents.push("promotions");
      return [];
    },
  }, { createSnapshotId: () => "unused", codeConflicts: noChangeConflicts });
  const result = await noChange.downloadAndActivate({
    storeCode: "S1",
    afterActivate: () => {
      noChangeEvents.push("after");
    },
  });
  assert.equal(noChangeStorage.activated, null);
  assert.equal(result.snapshotId, "active-s1");
  assert.deepEqual(noChangeEvents, ["promotions", "code-conflicts", "after"]);

  // 中文注释：重置目录走 null-base 全量，同样在激活后刷新候选。
  const resetEvents: string[] = [];
  const resetConflicts = new RecordingCodeConflictRefresh(resetEvents);
  const reset = new CatalogSnapshotService(new MemoryCatalogStorage(), remote([
    page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 }),
  ]), { createSnapshotId: () => "reset-conflicts", codeConflicts: resetConflicts });
  await reset.resetAndRedownload({ storeCode: "S1" });
  assert.deepEqual(resetConflicts.calls, [{ storeCode: "S1", aborted: false }]);
});

test("码冲突刷新失败不回滚、不误报已激活目录，后置重载照常执行", async () => {
  const storage = new MemoryCatalogStorage();
  const events: string[] = [];
  const service = new CatalogSnapshotService(storage, remote([
    page({ cursor: null, nextCursor: null, items: [item("A")], totalCount: 1 }),
  ]), {
    createSnapshotId: () => "active-after-conflict-failure",
    codeConflicts: new RecordingCodeConflictRefresh(
      events,
      new HbposApiError("offline", { kind: "transport", code: "NETWORK" }),
    ),
  });

  const result = await service.downloadAndActivate({
    storeCode: "S1",
    afterActivate: () => {
      events.push("after");
    },
  });

  assert.equal(result.snapshotId, "active-after-conflict-failure");
  assert.equal(storage.activated, "active-after-conflict-failure");
  assert.deepEqual(events, ["code-conflicts", "after"]);
});

test("目录刷新在激活前失败时不刷新码冲突候选", async () => {
  const conflicts = new RecordingCodeConflictRefresh();
  const service = new CatalogSnapshotService(new MemoryCatalogStorage(), {
    async getPage() {
      throw new HbposApiError("offline", { kind: "transport", code: "NETWORK" });
    },
  }, { createSnapshotId: () => "never-active", codeConflicts: conflicts });

  await assert.rejects(() => service.downloadAndActivate({ storeCode: "S1" }));
  assert.deepEqual(conflicts.calls, []);
});
