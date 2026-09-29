import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";

import type { CatalogPromotion } from "./catalog-snapshot-service";
import {
  HbposCatalogPageApi,
  calculateCatalogDeltaPageChecksum,
  calculateCatalogPageChecksum,
  type CatalogPageDigest,
  type CatalogLookupItem,
} from "./hbpos-catalog-remote";

import {
  HbposApiError,
  type HbposTransport,
  type HbposTransportRequest,
} from "@/core/api";

const digest: CatalogPageDigest = async (payload) =>
  createHash("sha256").update(payload, "utf8").digest("hex");

test("目录摘要依赖进入主 bundle，刷新时不请求 Metro 懒加载分包", () => {
  const source = readFileSync(
    resolve(__dirname, "hbpos-catalog-remote.ts"),
    "utf8",
  );

  assert.match(source, /require\(["']expo-crypto["']\)/);
  assert.doesNotMatch(source, /await\s+import\(["']expo-crypto["']\)/);
});

const canonicalItem: CatalogLookupItem = {
  storeCode: "S01",
  productCode: "P-001",
  referenceCode: null,
  displayName: "牛奶🥛",
  lookupCode: "930000000001",
  lookupCodeNormalized: "930000000001",
  itemNumber: "I-001",
  barcode: "source-barcode-is-not-the-offline-lookup",
  retailPrice: 12.34,
  priceSource: 0,
  priceSourceLabel: "product",
  quantityFactor: 1,
  updatedAt: "2026-07-28T01:02:03.456Z",
  rowVersion: "ROW",
  productImage: null,
  discountRate: null,
  isSpecialProduct: true,
};

test("TypeScript 与服务端共享规范化 SHA256 v1 测试向量", async () => {
  assert.equal(
    await calculateCatalogPageChecksum([canonicalItem], digest),
    "sha256-catalog-page-v1:4eb87e036003575ca8b8e9961ab6c21dbe63ed6d18482886f1da92e8f4165530",
  );
});

test("checksum 以 JavaScript 可观测数值为协议表示，避免 decimal 精度误拒绝", async () => {
  assert.equal(
    await calculateCatalogPageChecksum([{
      ...canonicalItem,
      productCode: "P-HIGH",
      referenceCode: null,
      displayName: "精度",
      lookupCode: "HIGH",
      lookupCodeNormalized: "HIGH",
      itemNumber: null,
      barcode: null,
      retailPrice: 0.1000000000000000000000000001,
      quantityFactor: 12345678901234567890.123456789,
      updatedAt: null,
      productImage: null,
      discountRate: 0.3333333333333333333333333333,
      isSpecialProduct: false,
    }], digest),
    "sha256-catalog-page-v1:86178b9aa03175a4dc97d8c61fa8db018507d0c29d60b43a5d65b47106681e44",
  );
});

test("TypeScript 与服务端共享 IEEE-754 SHA256 v2 测试向量", async () => {
  assert.equal(
    await calculateCatalogPageChecksum([canonicalItem], digest, 2),
    "sha256-catalog-page-v2:22181273b9791ad9664ad4f30ca2cddd3916ad9a012851490db28f7e1b229c27",
  );
  assert.equal(
    await calculateCatalogPageChecksum([{
      ...canonicalItem,
      productCode: "P-HIGH",
      referenceCode: null,
      displayName: "精度",
      lookupCode: "HIGH",
      lookupCodeNormalized: "HIGH",
      itemNumber: null,
      barcode: null,
      retailPrice: 0.1000000000000000000000000001,
      quantityFactor: 12345678901234567890.123456789,
      updatedAt: null,
      productImage: null,
      discountRate: 0.3333333333333333333333333333,
      isSpecialProduct: false,
    }], digest, 2),
    "sha256-catalog-page-v2:d93e540bfb88bafc7735c24ec747537e9f1de0cf4f0d38a6c5d763e552f7486b",
  );
});

test("checksum 将等价的 DateTimeOffset JSON 规范化为服务端 UTC 毫秒格式", async () => {
  const offsetItem = {
    ...canonicalItem,
    updatedAt: "2026-07-28T11:02:03.4560000+10:00",
  };
  assert.equal(
    await calculateCatalogPageChecksum([offsetItem], digest, 2),
    await calculateCatalogPageChecksum([canonicalItem], digest, 2),
  );

  const canonicalDeleted = {
    storeCode: "S01",
    lookupCode: "A",
    lookupCodeNormalized: "A",
    deletedAt: "2026-07-28T01:02:03.456Z",
  };
  const offsetDeleted = {
    ...canonicalDeleted,
    deletedAt: "2026-07-28T11:02:03.4560000+10:00",
  };
  assert.equal(
    await calculateCatalogDeltaPageChecksum({
      baseCatalogVersion: "v1",
      targetCatalogVersion: "v2",
      items: [offsetItem],
      deletedLookups: [offsetDeleted],
    }, digest),
    await calculateCatalogDeltaPageChecksum({
      baseCatalogVersion: "v1",
      targetCatalogVersion: "v2",
      items: [canonicalItem],
      deletedLookups: [canonicalDeleted],
    }, digest),
  );
});

test("delta checksum 覆盖 base/target、upsert 与 delete，且按规范化售卖码稳定排序", async () => {
  const checksum = await calculateCatalogDeltaPageChecksum({
    baseCatalogVersion: "v1",
    targetCatalogVersion: "v2",
    items: [{ ...canonicalItem, lookupCode: "B", lookupCodeNormalized: "B" }],
    deletedLookups: [{ storeCode: "S01", lookupCode: "A", lookupCodeNormalized: "A", deletedAt: null }],
  }, digest);
  assert.match(checksum, /^sha256-catalog-delta-page-v1:[0-9a-f]{64}$/);
  assert.notEqual(checksum, await calculateCatalogDeltaPageChecksum({
    baseCatalogVersion: "v0",
    targetCatalogVersion: "v2",
    items: [{ ...canonicalItem, lookupCode: "B", lookupCodeNormalized: "B" }],
    deletedLookups: [{ storeCode: "S01", lookupCode: "A", lookupCodeNormalized: "A", deletedAt: null }],
  }, digest));
});

test("Hbpos catalog adapter 使用 sync-plan 与 delta 合同，并校验 delta 与租约回显", async () => {
  const deltaItem = { ...canonicalItem, lookupCode: "B", lookupCodeNormalized: "B" };
  const deleted = { storeCode: "S01", lookupCode: "A", lookupCodeNormalized: "A", deletedAt: null };
  const checksum = await calculateCatalogDeltaPageChecksum({
    baseCatalogVersion: "v1", targetCatalogVersion: "v2", items: [deltaItem], deletedLookups: [deleted],
  }, digest);
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      calls.push(request);
      return {
        status: 200,
        data: {
          success: true,
          data: request.url.endsWith("sync-plan")
            ? {
              storeCode: "S01",
              mode: "delta",
              baseCatalogVersion: "v1",
              targetCatalogVersion: "v2",
              targetTotal: 1,
              downloadLeaseId: "lease-delta",
              deltaOperationCount: 2,
            }
            : {
              storeCode: "S01", baseCatalogVersion: "v1", targetCatalogVersion: "v2",
              cursor: null, items: [deltaItem], deletedLookups: [deleted], nextCursor: null,
              hasMore: false, targetTotal: 1, pageChecksum: checksum,
              downloadLeaseId: "lease-delta",
            },
        } as T,
      };
    },
  };
  const api = new HbposCatalogPageApi(transport, digest);

  const plan = await api.getSyncPlan({ storeCode: "S01", baseCatalogVersion: "v1" });
  const page = await api.getDeltaPage({
    storeCode: "S01", baseCatalogVersion: "v1", targetCatalogVersion: "v2", cursor: null, pageSize: 500,
    downloadLeaseId: "lease-delta",
  });

  assert.deepEqual(plan, {
    mode: "delta",
    baseCatalogVersion: "v1",
    targetCatalogVersion: "v2",
    targetTotal: 1,
    downloadLeaseId: "lease-delta",
    deltaOperationCount: 2,
  });
  assert.equal(page.catalogVersion, "v2");
  assert.equal(calls[0]?.url, "/api/v1/catalog/sync-plan");
  assert.equal(calls[1]?.url, "/api/v1/catalog/delta/page");
  assert.equal(calls[1]?.params?.checksumVersion, 1);
  assert.equal(calls[1]?.params?.downloadLeaseId, "lease-delta");
});

test("Hbpos catalog adapter 用 null 基线取得 full 租约，并校验固定版本全量页回显", async () => {
  const calls: HbposTransportRequest[] = [];
  const checksum = await calculateCatalogPageChecksum([canonicalItem], digest, 2);
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      calls.push(request);
      return {
        status: 200,
        data: {
          success: true,
          data: request.url.endsWith("sync-plan")
            ? {
              storeCode: "S01",
              mode: "full",
              baseCatalogVersion: null,
              targetCatalogVersion: "v2",
              targetTotal: 1,
              downloadLeaseId: "lease-full",
              deltaOperationCount: null,
            }
            : {
              storeCode: "S01",
              generatedAt: "2026-07-28T01:02:03.456Z",
              cursor: null,
              items: [canonicalItem],
              deletedLookups: [],
              nextCursor: null,
              hasMore: false,
              totalCount: 1,
              catalogVersion: "v2",
              pageChecksum: checksum,
              downloadLeaseId: "lease-full",
            },
        } as T,
      };
    },
  };
  const api = new HbposCatalogPageApi(transport, digest);

  const plan = await api.getSyncPlan({ storeCode: "S01", baseCatalogVersion: null });
  await api.getPage({
    storeCode: "S01",
    cursor: null,
    pageSize: 500,
    catalogVersion: plan.targetCatalogVersion,
    ...(plan.downloadLeaseId ? { downloadLeaseId: plan.downloadLeaseId } : {}),
  });

  assert.equal(calls[0]?.params?.baseCatalogVersion, undefined);
  assert.equal(calls[1]?.params?.catalogVersion, "v2");
  assert.equal(calls[1]?.params?.downloadLeaseId, "lease-full");
});

test("Hbpos catalog adapter 对带租约的 full/delta 页面缺失或错误回显均 fail-closed", async () => {
  const fullChecksum = await calculateCatalogPageChecksum([canonicalItem], digest, 2);
  const deltaItem = { ...canonicalItem, lookupCode: "B", lookupCodeNormalized: "B" };
  const deleted = {
    storeCode: "S01",
    lookupCode: "A",
    lookupCodeNormalized: "A",
    deletedAt: null,
  };
  const deltaChecksum = await calculateCatalogDeltaPageChecksum({
    baseCatalogVersion: "v1",
    targetCatalogVersion: "v2",
    items: [deltaItem],
    deletedLookups: [deleted],
  }, digest);
  const cases = [
    { label: "缺失回显", responseLeaseId: undefined },
    { label: "错误回显", responseLeaseId: "lease-other" },
  ] as const;

  for (const entry of cases) {
    const fullTransport: HbposTransport = {
      async request<T>() {
        return {
          status: 200,
          data: {
            success: true,
            data: {
              storeCode: "S01",
              generatedAt: "2026-07-28T01:02:03.456Z",
              cursor: null,
              items: [canonicalItem],
              deletedLookups: [],
              nextCursor: null,
              hasMore: false,
              totalCount: 1,
              catalogVersion: "v2",
              pageChecksum: fullChecksum,
              ...(entry.responseLeaseId === undefined
                ? {}
                : { downloadLeaseId: entry.responseLeaseId }),
            },
          } as T,
        };
      },
    };
    await assert.rejects(
      () => new HbposCatalogPageApi(fullTransport, digest).getPage({
        storeCode: "S01",
        cursor: null,
        pageSize: 500,
        catalogVersion: "v2",
        downloadLeaseId: "lease-full",
      }),
      (error: unknown) =>
        error instanceof HbposApiError
        && error.kind === "envelope"
        && error.code === "CATALOG_DOWNLOAD_LEASE_MISMATCH",
      `full ${entry.label}`,
    );

    const deltaTransport: HbposTransport = {
      async request<T>() {
        return {
          status: 200,
          data: {
            success: true,
            data: {
              storeCode: "S01",
              baseCatalogVersion: "v1",
              targetCatalogVersion: "v2",
              cursor: null,
              items: [deltaItem],
              deletedLookups: [deleted],
              nextCursor: null,
              hasMore: false,
              targetTotal: 1,
              pageChecksum: deltaChecksum,
              ...(entry.responseLeaseId === undefined
                ? {}
                : { downloadLeaseId: entry.responseLeaseId }),
            },
          } as T,
        };
      },
    };
    await assert.rejects(
      () => new HbposCatalogPageApi(deltaTransport, digest).getDeltaPage({
        storeCode: "S01",
        baseCatalogVersion: "v1",
        targetCatalogVersion: "v2",
        cursor: null,
        pageSize: 500,
        downloadLeaseId: "lease-delta",
      }),
      (error: unknown) =>
        error instanceof HbposApiError
        && error.kind === "envelope"
        && error.code === "CATALOG_DOWNLOAD_LEASE_MISMATCH",
      `delta ${entry.label}`,
    );
  }
});

test("Hbpos catalog adapter 的无租约请求兼容旧后端未回显租约字段", async () => {
  const fullChecksum = await calculateCatalogPageChecksum([canonicalItem], digest, 2);
  const deltaChecksum = await calculateCatalogDeltaPageChecksum({
    baseCatalogVersion: "v1",
    targetCatalogVersion: "v2",
    items: [],
    deletedLookups: [],
  }, digest);
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      return {
        status: 200,
        data: {
          success: true,
          data: request.url.endsWith("delta/page")
            ? {
              storeCode: "S01",
              baseCatalogVersion: "v1",
              targetCatalogVersion: "v2",
              cursor: null,
              items: [],
              deletedLookups: [],
              nextCursor: null,
              hasMore: false,
              targetTotal: 0,
              pageChecksum: deltaChecksum,
            }
            : {
              storeCode: "S01",
              generatedAt: "2026-07-28T01:02:03.456Z",
              cursor: null,
              items: [canonicalItem],
              deletedLookups: [],
              nextCursor: null,
              hasMore: false,
              totalCount: 1,
              catalogVersion: "v2",
              pageChecksum: fullChecksum,
            },
        } as T,
      };
    },
  };
  const api = new HbposCatalogPageApi(transport, digest);

  await api.getPage({
    storeCode: "S01",
    cursor: null,
    pageSize: 500,
    catalogVersion: "v2",
  });
  await api.getDeltaPage({
    storeCode: "S01",
    baseCatalogVersion: "v1",
    targetCatalogVersion: "v2",
    cursor: null,
    pageSize: 500,
  });
});

test("checksum digest 与 canonical 数值异常返回稳定目录校验码", async () => {
  await assert.rejects(
    () => calculateCatalogPageChecksum([canonicalItem], async () => "invalid"),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.kind === "envelope"
      && error.code === "CATALOG_PAGE_DIGEST_INVALID",
  );
  await assert.rejects(
    () => calculateCatalogPageChecksum([{
      ...canonicalItem,
      retailPrice: Number.POSITIVE_INFINITY,
    }], digest),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.kind === "envelope"
      && error.code === "CATALOG_PAGE_VALUE_INVALID",
  );
});

test("checksum digest 抛错时仅返回安全稳定错误码", async () => {
  const throwingDigest: CatalogPageDigest = async (payload) => {
    throw new Error(
      `digest failed url=https://secret.example/catalog body=${payload}`,
    );
  };

  await assert.rejects(
    () => calculateCatalogPageChecksum([canonicalItem], throwingDigest),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.kind === "envelope"
      && error.code === "CATALOG_PAGE_DIGEST_UNAVAILABLE"
      && error.message === "Catalog page digest is unavailable."
      && !error.message.includes("secret.example")
      && !error.message.includes(canonicalItem.displayName)
      && !error.message.includes(canonicalItem.barcode ?? ""),
  );
  await assert.rejects(
    () => calculateCatalogDeltaPageChecksum({
      baseCatalogVersion: "catalog-v1:base",
      targetCatalogVersion: "catalog-v1:target",
      items: [canonicalItem],
      deletedLookups: [],
    }, throwingDigest),
    (error: unknown) =>
      error instanceof HbposApiError
      && error.kind === "envelope"
      && error.code === "CATALOG_DELTA_PAGE_DIGEST_UNAVAILABLE"
      && error.message === "Catalog delta page digest is unavailable."
      && !error.message.includes("secret.example")
      && !error.message.includes(canonicalItem.displayName)
      && !error.message.includes(canonicalItem.barcode ?? ""),
  );
});

test("Hbpos catalog adapter 映射分页合同并校验服务端 checksum", async () => {
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      calls.push(request);
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S01",
            generatedAt: "2026-07-28T01:02:03.456Z",
            cursor: null,
            items: [canonicalItem],
            deletedLookups: [],
            nextCursor: "930000000001",
            hasMore: true,
            totalCount: 2,
            catalogVersion: "catalog-v1:server",
            pageChecksum: "sha256-catalog-page-v2:22181273b9791ad9664ad4f30ca2cddd3916ad9a012851490db28f7e1b229c27",
          },
        } as T,
      };
    },
  };

  const controller = new AbortController();
  const page = await new HbposCatalogPageApi(transport, digest).getPage({
    storeCode: "S01",
    cursor: null,
    pageSize: 500,
    signal: controller.signal,
  });

  assert.deepEqual(page, {
    storeCode: "S01",
    generatedAt: "2026-07-28T01:02:03.456Z",
    cursor: null,
    items: [canonicalItem],
    deletedLookups: [],
    nextCursor: "930000000001",
    hasMore: true,
    totalCount: 2,
    catalogVersion: "catalog-v1:server",
    pageChecksum: "sha256-catalog-page-v2:22181273b9791ad9664ad4f30ca2cddd3916ad9a012851490db28f7e1b229c27",
  });
  assert.deepEqual(calls, [{
    method: "GET",
    url: "/api/v1/catalog/sellable-items/page",
    params: {
      storeCode: "S01",
      cursor: undefined,
      pageSize: 500,
      catalogVersion: undefined,
      downloadLeaseId: undefined,
      checksumVersion: 2,
    },
    signal: controller.signal,
    timeoutMs: 0,
  }]);
});

test("Hbpos catalog adapter 接受服务端可选字段的空字符串并规范化为 null", async () => {
  const normalizedItem: CatalogLookupItem = {
    ...canonicalItem,
    referenceCode: null,
    itemNumber: null,
    barcode: null,
    productImage: null,
  };
  const pageChecksum = await calculateCatalogPageChecksum([normalizedItem], digest, 2);
  const transport: HbposTransport = {
    async request<T>() {
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S01",
            generatedAt: "2026-07-28T01:02:03.456Z",
            cursor: null,
            items: [{
              ...normalizedItem,
              referenceCode: "",
              itemNumber: "",
              barcode: "",
              productImage: "",
            }],
            deletedLookups: [],
            nextCursor: null,
            hasMore: false,
            totalCount: 1,
            catalogVersion: "catalog-v1:server",
            pageChecksum,
          },
        } as T,
      };
    },
  };

  const page = await new HbposCatalogPageApi(transport, digest).getPage({
    storeCode: "S01",
    cursor: null,
    pageSize: 500,
  });

  assert.equal(page.items[0]?.referenceCode, null);
  assert.equal(page.items[0]?.itemNumber, null);
  assert.equal(page.items[0]?.barcode, null);
  assert.equal(page.items[0]?.productImage, null);
});

test("Hbpos catalog adapter 校验原始摘要后保留可修正商品内容给 staging 归一化", async () => {
  const repairableItem: CatalogLookupItem = {
    ...canonicalItem,
    displayName: "",
    priceSourceLabel: "",
    updatedAt: "2026-02-30T00:00:00.000Z",
  };
  const pageChecksum = await calculateCatalogPageChecksum([repairableItem], digest, 2);
  const transport: HbposTransport = {
    async request<T>() {
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S01",
            generatedAt: "2026-07-28T01:02:03.456Z",
            cursor: null,
            items: [repairableItem],
            deletedLookups: [],
            nextCursor: null,
            hasMore: false,
            totalCount: 1,
            catalogVersion: "catalog-v1:server",
            pageChecksum,
          },
        } as T,
      };
    },
  };

  const page = await new HbposCatalogPageApi(transport, digest).getPage({
    storeCode: "S01",
    cursor: null,
    pageSize: 500,
  });

  assert.equal(page.items[0]?.displayName, "");
  assert.equal(page.items[0]?.priceSourceLabel, "");
  assert.equal(page.items[0]?.updatedAt, "2026-02-30T00:00:00.000Z");
});

test("Hbpos catalog adapter 在落库前拒绝被篡改的页面", async () => {
  const transport: HbposTransport = {
    async request<T>() {
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S01",
            generatedAt: "2026-07-28T01:02:03.456Z",
            cursor: null,
            items: [{
              storeCode: "S01",
              productCode: "P-001",
              referenceCode: null,
              displayName: "已被篡改",
              lookupCode: "930000000001",
              lookupCodeNormalized: "930000000001",
              itemNumber: "I-001",
              barcode: "source-barcode-is-not-the-offline-lookup",
              retailPrice: 12.34,
              priceSource: 0,
              priceSourceLabel: "product",
              quantityFactor: 1,
              updatedAt: "2026-07-28T01:02:03.456Z",
              rowVersion: "ROW",
              productImage: null,
              discountRate: null,
              isSpecialProduct: true,
            }],
            deletedLookups: [],
            nextCursor: null,
            hasMore: false,
            totalCount: 1,
            catalogVersion: "catalog-v1:server",
            // 使用未篡改 canonicalItem 的合法摘要，确保拒绝原因确实是响应内容被改写。
            pageChecksum: "sha256-catalog-page-v2:22181273b9791ad9664ad4f30ca2cddd3916ad9a012851490db28f7e1b229c27",
          },
        } as T,
      };
    },
  };

  await assert.rejects(
    () => new HbposCatalogPageApi(transport, digest).getPage({
      storeCode: "S01",
      cursor: null,
      pageSize: 500,
    }),
    /checksum/i,
  );
});

const canonicalPromotion = {
  promotionId: "PROMO-MILK-2",
  name: "牛奶两件特价",
  isExclusive: true,
  priority: 10,
  applyQuantity: 2,
  fixedPrice: 20.5,
  maxApplicationsPerOrder: 3,
  effectiveStart: "2026-07-28T00:00:00.000Z",
  effectiveEnd: "2026-08-28T00:00:00.000Z",
  updatedAt: "2026-07-27T11:12:13.000Z",
  products: [{ productCode: "P-001", unitWeight: 1 }],
};

test("Hbpos catalog adapter 将完整促销合同稳定白名单序列化给快照", async () => {
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      calls.push(request);
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S01",
            generatedAt: "2026-07-28T01:02:03.456Z",
            promotions: [{
              ...canonicalPromotion,
              // 后端将来新增字段时，不得未审查地持久化进可影响金额的规则 JSON。
              unexpectedServerField: "ignored",
            }],
          },
        } as T,
      };
    },
  };

  const controller = new AbortController();
  const promotions = await new HbposCatalogPageApi(transport, digest).getPromotions({
    storeCode: "S01",
    signal: controller.signal,
  });

  const expected: CatalogPromotion[] = [{
    promotionId: "PROMO-MILK-2",
    validFromIso: "2026-07-28T00:00:00.000Z",
    validUntilIso: "2026-08-28T00:00:00.000Z",
    priority: 10,
    definitionJson: JSON.stringify(canonicalPromotion),
  }];
  assert.deepEqual(promotions, expected);
  assert.deepEqual(calls, [{
    method: "GET",
    url: "/api/v1/catalog/promotions",
    params: { storeCode: "S01" },
    signal: controller.signal,
    timeoutMs: 0,
  }]);
});

test("Hbpos catalog adapter 拒绝跨门店、重复或非法的促销身份", async () => {
  const cases = [
    {
      label: "跨门店响应",
      body: { storeCode: "S02", promotions: [canonicalPromotion] },
    },
    {
      label: "重复促销标识",
      body: { storeCode: "S01", promotions: [canonicalPromotion, canonicalPromotion] },
    },
    {
      label: "重复商品标识",
      body: {
        storeCode: "S01",
        promotions: [{
          ...canonicalPromotion,
          products: [
            { productCode: "P-001", unitWeight: 1 },
            { productCode: "P-001", unitWeight: 2 },
          ],
        }],
      },
    },
    {
      label: "空促销标识",
      body: { storeCode: "S01", promotions: [{ ...canonicalPromotion, promotionId: " " }] },
    },
  ] as const;

  for (const entry of cases) {
    const transport: HbposTransport = {
      async request<T>() {
        return {
          status: 200,
          data: { success: true, data: entry.body } as T,
        };
      },
    };
    await assert.rejects(
      () => new HbposCatalogPageApi(transport, digest).getPromotions({ storeCode: "S01" }),
      /promotion/i,
      entry.label,
    );
  }
});

test("Hbpos catalog adapter 拒绝缺失数组和不安全的促销金额、时间、整数或商品", async () => {
  const invalidPromotions = [
    { ...canonicalPromotion, fixedPrice: 20.555 },
    { ...canonicalPromotion, priority: 0.5 },
    { ...canonicalPromotion, applyQuantity: 0 },
    { ...canonicalPromotion, effectiveEnd: "not-a-time" },
    { ...canonicalPromotion, products: [{ productCode: "P-001", unitWeight: 0 }] },
  ];

  for (const promotions of [undefined, ...invalidPromotions.map((promotion) => [promotion])]) {
    const transport: HbposTransport = {
      async request<T>() {
        return {
          status: 200,
          data: {
            success: true,
            data: {
              storeCode: "S01",
              generatedAt: "2026-07-28T01:02:03.456Z",
              promotions,
            },
          } as T,
        };
      },
    };
    await assert.rejects(
      () => new HbposCatalogPageApi(transport, digest).getPromotions({ storeCode: "S01" }),
      /promotion/i,
    );
  }
});

test("Hbpos catalog adapter 读取码冲突候选并保留服务端决胜顺序与请求上限", async () => {
  const calls: HbposTransportRequest[] = [];
  const fly = {
    ...canonicalItem,
    productCode: "P-FLY",
    displayName: "EXTENSION Fly Swatter",
    lookupCode: "6405090401470",
    lookupCodeNormalized: "6405090401470",
    retailPrice: 8.99,
    priceSource: 2 as const,
    priceSourceLabel: "set",
  };
  const flower = {
    ...fly,
    productCode: "P-FLOWER",
    displayName: "flower",
    retailPrice: 2.99,
    priceSource: 0 as const,
    priceSourceLabel: "product",
  };
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      calls.push(request);
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S01",
            generatedAt: "2026-09-26T01:02:03+10:00",
            available: true,
            items: [fly, flower],
          },
        } as T,
      };
    },
  };

  const controller = new AbortController();
  const result = await new HbposCatalogPageApi(transport, digest).getCodeConflicts({
    storeCode: "S01",
    signal: controller.signal,
  });

  assert.equal(result.available, true);
  assert.equal(result.generatedAt, "2026-09-25T15:02:03.000Z");
  assert.deepEqual(
    result.items.map((entry) => [entry.productCode, entry.retailPrice]),
    [
      ["P-FLY", 8.99],
      ["P-FLOWER", 2.99],
    ],
  );
  assert.deepEqual(calls, [{
    method: "GET",
    url: "/api/v1/catalog/sellable-items/code-conflicts",
    params: { storeCode: "S01" },
    timeoutMs: 60_000,
    signal: controller.signal,
  }]);
});

test("Hbpos catalog adapter 对跨门店、未规范化或自相矛盾的码冲突响应整体拒绝", async () => {
  const conflictItem = {
    ...canonicalItem,
    lookupCode: " abc-1 ",
    lookupCodeNormalized: "ABC-1",
  };
  const cases = [
    { label: "跨门店响应", body: { storeCode: "S02", available: true, items: [] } },
    {
      label: "跨门店候选",
      body: {
        storeCode: "S01",
        available: true,
        items: [{ ...conflictItem, storeCode: "S02" }],
      },
    },
    {
      label: "查询码未规范化",
      body: {
        storeCode: "S01",
        available: true,
        items: [{ ...conflictItem, lookupCodeNormalized: "abc-1" }],
      },
    },
    {
      label: "未计算却带候选",
      body: { storeCode: "S01", available: false, items: [conflictItem] },
    },
    { label: "缺少 available", body: { storeCode: "S01", items: [] } },
    { label: "缺少候选数组", body: { storeCode: "S01", available: true } },
    {
      label: "候选字段非法",
      body: {
        storeCode: "S01",
        available: true,
        items: [{ ...conflictItem, retailPrice: "8.99" }],
      },
    },
  ] as const;

  for (const entry of cases) {
    const transport: HbposTransport = {
      async request<T>() {
        return {
          status: 200,
          data: {
            success: true,
            data: { generatedAt: "2026-09-26T00:00:00.000Z", ...entry.body },
          } as T,
        };
      },
    };
    await assert.rejects(
      () =>
        new HbposCatalogPageApi(transport, digest).getCodeConflicts({
          storeCode: "S01",
        }),
      (error: unknown) =>
        error instanceof HbposApiError &&
        error.code === "CATALOG_CODE_CONFLICTS_INVALID",
      entry.label,
    );
  }
});

test("Hbpos catalog adapter 接受服务端尚未计算码冲突的空响应", async () => {
  const transport: HbposTransport = {
    async request<T>() {
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S01",
            generatedAt: "2026-09-26T00:00:00.000Z",
            available: false,
            items: [],
          },
        } as T,
      };
    },
  };

  assert.deepEqual(
    await new HbposCatalogPageApi(transport, digest).getCodeConflicts({
      storeCode: "S01",
    }),
    {
      storeCode: "S01",
      generatedAt: "2026-09-26T00:00:00.000Z",
      available: false,
      items: [],
    },
  );
});
