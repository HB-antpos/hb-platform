import assert from "node:assert/strict";
import test from "node:test";

import {
  CatalogCodeConflictSynchronizer,
  findExactCatalogCandidates,
  mergeCatalogCodeConflictCandidates,
  type CatalogCodeConflictRemotePort,
  type CatalogCodeConflictSyncEvent,
} from "./catalog-code-conflicts";
import type { CatalogStagedItem } from "./catalog-snapshot-service";
import type {
  CatalogLookupItem,
  VerifiedCatalogCodeConflicts,
} from "./hbpos-catalog-remote";

import { HbposApiError } from "@/core/api/hbpos-api";
import type { LocalCatalogMatch } from "@/core/db/catalog-repository";

const CONFLICT_CODE = "6405090401470";

const lookupItem = (
  overrides: Partial<CatalogLookupItem> = {},
): CatalogLookupItem => ({
  storeCode: "1042",
  productCode: "P-FLY",
  referenceCode: null,
  displayName: " EXTENSION Fly Swatter ",
  lookupCode: CONFLICT_CODE,
  lookupCodeNormalized: CONFLICT_CODE,
  itemNumber: "EXT-FLY",
  barcode: "9300000000017",
  retailPrice: 8.99,
  priceSource: 2,
  priceSourceLabel: "set",
  quantityFactor: 1,
  updatedAt: "2026-09-26T00:00:00.000Z",
  rowVersion: "row-fly",
  productImage: null,
  discountRate: null,
  isSpecialProduct: false,
  ...overrides,
});

const match = (overrides: Partial<LocalCatalogMatch> = {}): LocalCatalogMatch => ({
  storeCode: "1042",
  productCode: "P-FLY",
  referenceCode: null,
  itemNumber: "EXT-FLY",
  displayName: "EXTENSION Fly Swatter",
  barcode: "9300000000017",
  lookupCode: CONFLICT_CODE,
  lookupCodeNormalized: CONFLICT_CODE,
  retailPriceCents: 899,
  priceSource: 2,
  priceSourceLabel: "set",
  quantityFactor: 1,
  taxRateBasisPoints: null,
  updatedAtIso: null,
  rowVersion: "row-fly",
  productImage: null,
  discountRate: null,
  isSpecialProduct: false,
  ...overrides,
});

class MemoryConflictStorage {
  public readonly replacements: Readonly<{
    storeCode: string;
    items: readonly CatalogStagedItem[];
  }>[] = [];
  public failure: unknown = null;

  public async replaceStoreConflicts(
    storeCode: string,
    items: readonly CatalogStagedItem[],
  ): Promise<void> {
    if (this.failure !== null) throw this.failure;
    this.replacements.push({ storeCode, items });
  }
}

function remote(
  result: VerifiedCatalogCodeConflicts | (() => Promise<VerifiedCatalogCodeConflicts>),
): CatalogCodeConflictRemotePort & { calls: number } {
  const port = {
    calls: 0,
    async getCodeConflicts() {
      port.calls += 1;
      return typeof result === "function" ? result() : result;
    },
  };
  return port;
}

function synchronizer(
  remotePort: CatalogCodeConflictRemotePort,
  storage: MemoryConflictStorage,
  events: CatalogCodeConflictSyncEvent[] = [],
) {
  return new CatalogCodeConflictSynchronizer({
    remote: remotePort,
    storage,
    onDiagnostic: (event) => events.push(event),
  });
}

test("可用候选按目录分币规则整体替换本地门店候选", async () => {
  const storage = new MemoryConflictStorage();
  const events: CatalogCodeConflictSyncEvent[] = [];
  const result = await synchronizer(
    remote({
      storeCode: "1042",
      generatedAt: "2026-09-26T00:00:00.000Z",
      available: true,
      items: [
        lookupItem(),
        lookupItem({
          productCode: "P-FLOWER",
          displayName: "flower",
          retailPrice: 2.995,
          priceSource: 0,
          priceSourceLabel: "product",
        }),
      ],
    }),
    storage,
    events,
  ).refresh({ storeCode: "1042" });

  assert.deepEqual(result, {
    storeCode: "1042",
    outcome: "replaced",
    code: "CATALOG_CODE_CONFLICTS_REPLACED",
    itemCount: 2,
  });
  assert.deepEqual(events, [result]);
  assert.equal(storage.replacements.length, 1);
  assert.equal(storage.replacements[0]?.storeCode, "1042");
  assert.deepEqual(
    storage.replacements[0]?.items.map((item) => [
      item.productCode,
      item.displayName,
      item.retailPriceCents,
      item.taxRateBasisPoints,
    ]),
    [
      ["P-FLY", "EXTENSION Fly Swatter", 899, null],
      // 中文注释：与目录分页相同按十进制半分进位。
      ["P-FLOWER", "flower", 300, null],
    ],
  );
});

test("服务端空候选同样整体替换，清掉已不再冲突的旧候选", async () => {
  const storage = new MemoryConflictStorage();
  await synchronizer(
    remote({
      storeCode: "1042",
      generatedAt: "2026-09-26T00:00:00.000Z",
      available: true,
      items: [],
    }),
    storage,
  ).refresh({ storeCode: "1042" });

  assert.deepEqual(storage.replacements, [{ storeCode: "1042", items: [] }]);
});

test("available=false、旧服务端 404/501、网络与存储失败都保留本地旧候选", async () => {
  const cases: readonly Readonly<{
    label: string;
    remote: CatalogCodeConflictRemotePort;
    storageFailure?: unknown;
    expected: Omit<CatalogCodeConflictSyncEvent, "storeCode">;
  }>[] = [
    {
      label: "服务端尚未计算",
      remote: remote({
        storeCode: "1042",
        generatedAt: "2026-09-26T00:00:00.000Z",
        available: false,
        items: [],
      }),
      expected: { outcome: "kept", code: "CATALOG_CODE_CONFLICTS_NOT_AVAILABLE" },
    },
    {
      label: "旧服务端 404",
      remote: remote(async () => {
        throw new HbposApiError("not found", { kind: "http", status: 404 });
      }),
      expected: {
        outcome: "kept",
        code: "CATALOG_CODE_CONFLICTS_UNSUPPORTED",
        httpStatus: 404,
      },
    },
    {
      label: "旧服务端 501",
      remote: remote(async () => {
        throw new HbposApiError("not implemented", { kind: "http", status: 501 });
      }),
      expected: {
        outcome: "kept",
        code: "CATALOG_CODE_CONFLICTS_UNSUPPORTED",
        httpStatus: 501,
      },
    },
    {
      label: "服务端容量繁忙",
      remote: remote(async () => {
        throw new HbposApiError("busy", {
          kind: "http",
          status: 503,
          code: "CATALOG_CAPACITY_BUSY",
        });
      }),
      expected: {
        outcome: "kept",
        code: "CATALOG_CODE_CONFLICTS_FAILED",
        errorCode: "CATALOG_CAPACITY_BUSY",
        httpStatus: 503,
      },
    },
    {
      label: "网络失败",
      remote: remote(async () => {
        throw new HbposApiError("offline", {
          kind: "transport",
          code: "NETWORK_UNAVAILABLE",
        });
      }),
      expected: {
        outcome: "kept",
        code: "CATALOG_CODE_CONFLICTS_FAILED",
        errorCode: "NETWORK_UNAVAILABLE",
      },
    },
    {
      label: "校验失败",
      remote: remote(async () => {
        throw new HbposApiError("invalid", {
          kind: "envelope",
          code: "CATALOG_CODE_CONFLICTS_INVALID",
        });
      }),
      expected: {
        outcome: "kept",
        code: "CATALOG_CODE_CONFLICTS_FAILED",
        errorCode: "CATALOG_CODE_CONFLICTS_INVALID",
      },
    },
    {
      label: "候选跨门店",
      remote: remote({
        storeCode: "1042",
        generatedAt: "2026-09-26T00:00:00.000Z",
        available: true,
        items: [lookupItem({ storeCode: "OTHER" })],
      }),
      expected: {
        outcome: "kept",
        code: "CATALOG_CODE_CONFLICTS_FAILED",
        errorCode: "CATALOG_CODE_CONFLICTS_STORE_MISMATCH",
      },
    },
    {
      label: "本地写入失败",
      remote: remote({
        storeCode: "1042",
        generatedAt: "2026-09-26T00:00:00.000Z",
        available: true,
        items: [lookupItem()],
      }),
      storageFailure: new Error("disk full"),
      expected: { outcome: "kept", code: "CATALOG_CODE_CONFLICTS_FAILED" },
    },
  ];

  for (const entry of cases) {
    const storage = new MemoryConflictStorage();
    storage.failure = entry.storageFailure ?? null;
    const result = await synchronizer(entry.remote, storage).refresh({
      storeCode: "1042",
    });
    assert.deepEqual(result, { storeCode: "1042", ...entry.expected }, entry.label);
    assert.deepEqual(storage.replacements, [], entry.label);
  }
});

test("已取消的刷新不请求候选，诊断回调异常不影响结果", async () => {
  const controller = new AbortController();
  controller.abort();
  const remotePort = remote({
    storeCode: "1042",
    generatedAt: "2026-09-26T00:00:00.000Z",
    available: true,
    items: [lookupItem()],
  });
  const storage = new MemoryConflictStorage();
  const result = await new CatalogCodeConflictSynchronizer({
    remote: remotePort,
    storage,
    onDiagnostic: () => {
      throw new Error("logger unavailable");
    },
  }).refresh({ storeCode: "1042", signal: controller.signal });

  assert.equal(result.code, "CATALOG_CODE_CONFLICTS_CANCELLED");
  assert.equal(remotePort.calls, 0);
  assert.deepEqual(storage.replacements, []);
});

test("合并规则：目录行在首位且保留自身版本，按商品编码去重，跨门店或跨码候选丢弃", () => {
  const primary = match();
  assert.deepEqual(mergeCatalogCodeConflictCandidates(null, [match()]), []);
  assert.deepEqual(mergeCatalogCodeConflictCandidates(primary, []), [primary]);

  const merged = mergeCatalogCodeConflictCandidates(primary, [
    match({ retailPriceCents: 1 }),
    match({
      productCode: " p-fly ",
      retailPriceCents: 2,
    }),
    match({
      productCode: "P-FLOWER",
      displayName: "flower",
      retailPriceCents: 299,
      priceSource: 0,
    }),
    match({ productCode: "P-OTHER-STORE", storeCode: "OTHER" }),
    match({ productCode: "P-OTHER-CODE", lookupCodeNormalized: "OTHER" }),
    match({ productCode: "p-flower", retailPriceCents: 3 }),
  ]);

  assert.deepEqual(
    merged.map((candidate) => [candidate.productCode, candidate.retailPriceCents]),
    [
      ["P-FLY", 899],
      ["P-FLOWER", 299],
    ],
  );
  assert.equal(merged[0], primary);
});

test("精确候选：码不在目录时不查候选，候选表读取失败时退回目录单行", async () => {
  let conflictReads = 0;
  assert.deepEqual(
    await findExactCatalogCandidates(
      {
        findExact: async () => null,
        findConflicts: async () => {
          conflictReads += 1;
          return [match({ productCode: "P-FLOWER" })];
        },
      },
      CONFLICT_CODE,
    ),
    [],
  );
  assert.equal(conflictReads, 0);

  const primary = match();
  assert.deepEqual(
    await findExactCatalogCandidates(
      {
        findExact: async () => primary,
        findConflicts: async () => {
          throw new Error("table unavailable");
        },
      },
      CONFLICT_CODE,
    ),
    [primary],
  );

  const requested: string[] = [];
  const candidates = await findExactCatalogCandidates(
    {
      findExact: async () => primary,
      findConflicts: async (storeCode, lookupCodeNormalized) => {
        requested.push(`${storeCode}:${lookupCodeNormalized}`);
        return [match({ productCode: "P-FLOWER", retailPriceCents: 299 })];
      },
    },
    ` ${CONFLICT_CODE} `,
  );
  assert.deepEqual(requested, [`1042:${CONFLICT_CODE}`]);
  assert.deepEqual(
    candidates.map((candidate) => candidate.productCode),
    ["P-FLY", "P-FLOWER"],
  );
});
