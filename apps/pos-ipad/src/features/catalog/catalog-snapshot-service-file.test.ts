import assert from "node:assert/strict";
import test from "node:test";

import type {
  CatalogDownloadFile,
  CatalogFileSyncPlan,
  CatalogFileSyncPort,
} from "./catalog-file-sync";
import {
  CatalogSnapshotService,
  type ActiveCatalogSnapshotMetadata,
  type CatalogDeltaPage,
  type CatalogSnapshotStoragePort,
  type CatalogStagedItem,
  type CatalogSyncRemotePort,
} from "./catalog-snapshot-service";
import type { CatalogDeletedLookup, CatalogLookupItem } from "./hbpos-catalog-remote";

import { HbposApiError } from "@/core/api/hbpos-api";

const item = (lookupCode: string, overrides: Partial<CatalogLookupItem> = {}): CatalogLookupItem => ({
  storeCode: "S1",
  productCode: `P-${lookupCode}`,
  referenceCode: null,
  displayName: `商品 ${lookupCode}`,
  lookupCode,
  lookupCodeNormalized: lookupCode.toUpperCase(),
  itemNumber: null,
  barcode: lookupCode,
  retailPrice: 2.5,
  priceSource: 1,
  priceSourceLabel: "store",
  quantityFactor: 1,
  updatedAt: "2026-10-01T09:00:00.000Z",
  rowVersion: `ROW-${lookupCode}`,
  productImage: null,
  discountRate: null,
  isSpecialProduct: false,
  ...overrides,
});

const fileDescriptor = (kind: "full" | "delta"): CatalogDownloadFile => ({
  kind,
  format: kind === "full" ? "hbpos-catalog-full-v1" : "hbpos-catalog-delta-v1",
  path: `api/v1/catalog/files/${kind}`,
  bytes: 10,
  sha256: "a".repeat(64),
});

const filePlan = (overrides: Partial<CatalogFileSyncPlan>): CatalogFileSyncPlan => ({
  storeCode: "S1",
  mode: "full",
  baseCatalogVersion: null,
  targetCatalogVersion: "v2",
  targetTotal: 0,
  deltaOperationCount: null,
  file: fileDescriptor("full"),
  ...overrides,
});

class MemoryCatalogStorage implements CatalogSnapshotStoragePort {
  public readonly active = new Map<string, readonly CatalogStagedItem[]>();
  public readonly staged = new Map<string, CatalogStagedItem[]>();
  public readonly appendBatchSizes: number[] = [];
  public readonly discarded: string[] = [];
  public activeMetadata: ActiveCatalogSnapshotMetadata | null = null;
  public activated: string | null = null;
  public deltaActivated: string | null = null;
  public activateDeltaError: Error | null = null;
  public readonly deltaDeleted = new Map<string, Set<string>>();

  public async getActiveMetadata(): Promise<ActiveCatalogSnapshotMetadata | null> {
    return this.activeMetadata;
  }

  public async beginStaging(snapshot: { snapshotId: string }): Promise<void> {
    this.staged.set(snapshot.snapshotId, []);
  }

  public async appendPage(snapshotId: string, items: readonly CatalogStagedItem[]): Promise<void> {
    this.appendBatchSizes.push(items.length);
    this.staged.get(snapshotId)?.push(...items);
  }

  public async replacePromotions(): Promise<void> {}

  public async activate(snapshotId: string, expectedItemCount: number): Promise<void> {
    const staged = this.staged.get(snapshotId) ?? [];
    assert.equal(staged.length, expectedItemCount);
    this.active.clear();
    this.active.set(snapshotId, staged);
    this.activated = snapshotId;
  }

  public async discardStaging(snapshotId: string): Promise<void> {
    this.discarded.push(snapshotId);
    this.staged.delete(snapshotId);
  }

  public async beginDeltaStaging(input: Readonly<{ snapshotId: string }>): Promise<void> {
    this.staged.set(input.snapshotId, []);
    this.deltaDeleted.set(input.snapshotId, new Set());
  }

  public async appendDeltaBatch(snapshotId: string, batch: Readonly<{
    items: readonly CatalogStagedItem[];
    deletedLookups: CatalogDeltaPage["deletedLookups"];
  }>): Promise<void> {
    this.staged.get(snapshotId)?.push(...batch.items);
    for (const deleted of batch.deletedLookups) this.deltaDeleted.get(snapshotId)?.add(deleted.lookupCodeNormalized);
  }

  public async activateDelta(input: Readonly<{
    sourceSnapshotId: string;
    stagingSnapshotId: string;
    activatedAtIso: string;
  }>): Promise<ActiveCatalogSnapshotMetadata> {
    if (this.activateDeltaError) throw this.activateDeltaError;
    this.deltaActivated = input.stagingSnapshotId;
    return {
      snapshotId: input.sourceSnapshotId,
      generationId: input.stagingSnapshotId,
      storeCode: "S1",
      catalogVersion: "v2",
      itemCount: 2,
      activatedAt: input.activatedAtIso,
    };
  }
}

/** 文件端口替身：计划、下载与读取都可单独注入失败。 */
class FakeFileSync implements CatalogFileSyncPort {
  public readonly planRequests: (string | null)[] = [];
  public readonly released: string[] = [];
  public downloads = 0;
  public planError: Error | null = null;
  public downloadError: Error | null = null;
  public readError: Error | null = null;
  public items: readonly CatalogLookupItem[] = [];
  public delta: Readonly<{ items: readonly CatalogLookupItem[]; deletedLookups: readonly CatalogDeletedLookup[] }> = {
    items: [],
    deletedLookups: [],
  };

  public constructor(public plan: CatalogFileSyncPlan) {}

  public async getPlan(input: Readonly<{ baseCatalogVersion: string | null }>): Promise<CatalogFileSyncPlan> {
    this.planRequests.push(input.baseCatalogVersion);
    if (this.planError) throw this.planError;
    return this.plan;
  }

  public async download(input: Readonly<{ onBytes?: (downloaded: number, total: number) => void }>): Promise<string> {
    this.downloads += 1;
    if (this.downloadError) throw this.downloadError;
    input.onBytes?.(5, 10);
    input.onBytes?.(10, 10);
    return "verified-file";
  }

  public async *readFull(_name: string, _expected: unknown, batchSize: number): AsyncIterable<readonly CatalogLookupItem[]> {
    for (let start = 0; start < this.items.length; start += batchSize) {
      if (this.readError && start > 0) throw this.readError;
      yield this.items.slice(start, start + batchSize);
    }
    if (this.readError) throw this.readError;
  }

  public async readDelta(): Promise<Readonly<{ items: readonly CatalogLookupItem[]; deletedLookups: readonly CatalogDeletedLookup[] }>> {
    if (this.readError) throw this.readError;
    return this.delta;
  }

  public async release(name: string): Promise<void> {
    this.released.push(name);
  }
}

/** 分页远端：记录是否被调用，并能完成一次 1 条的 full 下载作为回退结果。 */
function pagingRemote(calls: string[]): CatalogSyncRemotePort {
  return {
    async getSyncPlan(input) {
      calls.push(`plan:${input.baseCatalogVersion ?? "null"}`);
      return {
        mode: "full",
        baseCatalogVersion: input.baseCatalogVersion,
        targetCatalogVersion: "v-paged",
        targetTotal: 1,
        downloadLeaseId: "lease",
        deltaOperationCount: null,
      };
    },
    async getPage() {
      calls.push("page");
      return {
        storeCode: "S1",
        generatedAt: "2026-10-01T09:00:00.000Z",
        cursor: null,
        items: [item("PAGED")],
        deletedLookups: [],
        nextCursor: null,
        hasMore: false,
        totalCount: 1,
        catalogVersion: "v-paged",
        pageChecksum: "verified",
      };
    },
  };
}

function createService(
  storage: MemoryCatalogStorage,
  fileSync: CatalogFileSyncPort,
  calls: string[] = [],
  codeConflictRefreshes: string[] = [],
): CatalogSnapshotService {
  let sequence = 0;
  return new CatalogSnapshotService(storage, pagingRemote(calls), {
    createSnapshotId: () => `snapshot-${++sequence}`,
    nowIso: () => "2026-10-01T09:00:00.000Z",
    yieldControl: async () => undefined,
    fileSync,
    codeConflicts: {
      async refresh(input) {
        codeConflictRefreshes.push(input.storeCode);
      },
    },
  });
}

const active = (overrides: Partial<ActiveCatalogSnapshotMetadata> = {}): ActiveCatalogSnapshotMetadata => ({
  snapshotId: "active-1",
  storeCode: "S1",
  catalogVersion: "v1",
  itemCount: 2,
  activatedAt: "2026-10-01T08:00:00.000Z",
  ...overrides,
});

const manyItems = Array.from({ length: 1_234 }, (_, index) => item(`93${String(index).padStart(10, "0")}`));

test("全量文件计划按 500 条批次写入 staging 并激活，不再请求分页", async () => {
  const storage = new MemoryCatalogStorage();
  const fileSync = new FakeFileSync(filePlan({ targetTotal: manyItems.length }));
  fileSync.items = manyItems;
  const calls: string[] = [];
  const conflicts: string[] = [];
  const progress: Readonly<{ step: string; percent: number }>[] = [];

  const result = await createService(storage, fileSync, calls, conflicts).downloadAndActivate({
    storeCode: "S1",
    onProgress: (event) => progress.push({ step: event.step, percent: event.percent }),
  });

  assert.deepEqual(calls, []);
  assert.deepEqual(fileSync.planRequests, [null]);
  assert.deepEqual(storage.appendBatchSizes, [500, 500, 234]);
  assert.equal(result.itemCount, 1_234);
  assert.equal(result.catalogVersion, "v2");
  assert.equal(storage.activated, result.snapshotId);
  assert.deepEqual(fileSync.released, ["verified-file"]);
  assert.deepEqual(conflicts, ["S1"]);
  // 下载计入 prepare；products 只报告已写入的真实条数，结束时 activate 100%。
  assert.deepEqual(progress.slice(0, 3), [
    { step: "prepare", percent: 0 },
    { step: "prepare", percent: 50 },
    { step: "prepare", percent: 99 },
  ]);
  assert.deepEqual(progress.at(-1), { step: "activate", percent: 100 });
  const percents = progress.filter((event) => event.step === "products").map((event) => event.percent);
  assert.deepEqual(percents, [...percents].sort((left, right) => left - right));
});

test("增量文件计划在 delta staging 上应用 upsert 与删除并原子激活", async () => {
  const storage = new MemoryCatalogStorage();
  storage.activeMetadata = active();
  const fileSync = new FakeFileSync(filePlan({
    mode: "delta",
    baseCatalogVersion: "v1",
    targetTotal: 2,
    deltaOperationCount: 2,
    file: fileDescriptor("delta"),
  }));
  fileSync.delta = {
    items: [item("NEW")],
    deletedLookups: [{ storeCode: "S1", lookupCode: "old", lookupCodeNormalized: "OLD", deletedAt: null }],
  };
  const calls: string[] = [];

  const result = await createService(storage, fileSync, calls).downloadAndActivate({ storeCode: "S1" });

  assert.deepEqual(calls, []);
  assert.deepEqual(fileSync.planRequests, ["v1"]);
  assert.equal(storage.deltaActivated, "snapshot-1");
  assert.deepEqual([...(storage.deltaDeleted.get("snapshot-1") ?? [])], ["OLD"]);
  assert.equal(result.catalogVersion, "v2");
  assert.deepEqual(fileSync.released, ["verified-file"]);
});

test("无变化文件计划只刷新促销与码冲突，不下载", async () => {
  const storage = new MemoryCatalogStorage();
  storage.activeMetadata = active();
  const fileSync = new FakeFileSync(filePlan({
    mode: "noChange",
    baseCatalogVersion: "v1",
    targetCatalogVersion: "v1",
    targetTotal: 2,
    file: null,
  }));
  const calls: string[] = [];
  const conflicts: string[] = [];

  const result = await createService(storage, fileSync, calls, conflicts).downloadAndActivate({ storeCode: "S1" });

  assert.equal(fileSync.downloads, 0);
  assert.deepEqual(calls, []);
  assert.equal(result.snapshotId, "active-1");
  assert.deepEqual(conflicts, ["S1"]);
});

for (const [label, configure] of [
  ["服务端开关未开", (fileSync: FakeFileSync) => {
    fileSync.planError = new HbposApiError("disabled", { kind: "http", status: 404, code: "CATALOG_FILE_DOWNLOAD_DISABLED" });
  }],
  ["服务端繁忙", (fileSync: FakeFileSync) => {
    fileSync.planError = new HbposApiError("busy", { kind: "http", status: 503, code: "CATALOG_FILE_UNAVAILABLE" });
  }],
  ["下载或校验失败", (fileSync: FakeFileSync) => {
    fileSync.downloadError = new HbposApiError("checksum", { kind: "envelope", code: "CATALOG_FILE_CHECKSUM_MISMATCH" });
  }],
  ["读取到一半发现文件无效", (fileSync: FakeFileSync) => {
    fileSync.items = manyItems;
    fileSync.plan = filePlan({ targetTotal: manyItems.length });
    fileSync.readError = new HbposApiError("invalid", { kind: "envelope", code: "CATALOG_FILE_INVALID" });
  }],
  ["文件行属于别的门店", (fileSync: FakeFileSync) => {
    fileSync.items = [item("X", { storeCode: "S2" })];
    fileSync.plan = filePlan({ targetTotal: 1 });
  }],
] as const) {
  test(`${label}时丢弃文件 staging，改走分页协议完成本次刷新`, async () => {
    const storage = new MemoryCatalogStorage();
    const fileSync = new FakeFileSync(filePlan({ targetTotal: 0 }));
    configure(fileSync);
    const calls: string[] = [];

    const result = await createService(storage, fileSync, calls).downloadAndActivate({ storeCode: "S1" });

    assert.deepEqual(calls, ["plan:null", "page"]);
    assert.equal(result.catalogVersion, "v-paged");
    for (const snapshotId of storage.discarded) assert.equal(storage.staged.has(snapshotId), false);
    assert.equal(fileSync.released.length, 0);
  });
}

test("增量激活时本地基准已变化，放弃文件增量交给分页协议", async () => {
  const storage = new MemoryCatalogStorage();
  storage.activeMetadata = active();
  storage.activateDeltaError = new HbposApiError("base changed", { kind: "envelope", code: "CATALOG_DELTA_BASE_CHANGED" });
  const fileSync = new FakeFileSync(filePlan({
    mode: "delta",
    baseCatalogVersion: "v1",
    targetTotal: 2,
    deltaOperationCount: 1,
    file: fileDescriptor("delta"),
  }));
  fileSync.delta = { items: [item("NEW")], deletedLookups: [] };
  const calls: string[] = [];

  await createService(storage, fileSync, calls).downloadAndActivate({ storeCode: "S1" });

  assert.deepEqual(storage.discarded, ["snapshot-1"]);
  assert.equal(calls[0], "plan:v1");
});

test("激活前会话核验失败照常上报，不回退分页也不吞掉错误", async () => {
  const storage = new MemoryCatalogStorage();
  const fileSync = new FakeFileSync(filePlan({ targetTotal: 1 }));
  fileSync.items = [item("A")];
  const calls: string[] = [];

  await assert.rejects(
    () => createService(storage, fileSync, calls).downloadAndActivate({
      storeCode: "S1",
      beforeActivate: () => {
        throw new Error("cashier session changed");
      },
    }),
    /cashier session changed/,
  );

  assert.deepEqual(calls, []);
  assert.equal(storage.activated, null);
  assert.deepEqual(storage.discarded, ["snapshot-1"]);
});

test("下载中取消直接抛出，不转成分页回退", async () => {
  const storage = new MemoryCatalogStorage();
  const controller = new AbortController();
  const fileSync = new FakeFileSync(filePlan({ targetTotal: 1 }));
  fileSync.download = async () => {
    controller.abort();
    throw new HbposApiError("cancelled", { kind: "transport", code: "REQUEST_ABORTED" });
  };
  const calls: string[] = [];

  await assert.rejects(() => createService(storage, fileSync, calls).downloadAndActivate({
    storeCode: "S1",
    signal: controller.signal,
  }));

  assert.deepEqual(calls, []);
});

test("重置目录用 null 基准请求全量文件，失败才走分页全量", async () => {
  const storage = new MemoryCatalogStorage();
  storage.activeMetadata = active();
  const fileSync = new FakeFileSync(filePlan({ targetTotal: 1 }));
  fileSync.items = [item("A")];
  const calls: string[] = [];

  const result = await createService(storage, fileSync, calls).resetAndRedownload({ storeCode: "S1" });

  assert.deepEqual(fileSync.planRequests, [null]);
  assert.deepEqual(calls, []);
  assert.equal(result.itemCount, 1);
});
