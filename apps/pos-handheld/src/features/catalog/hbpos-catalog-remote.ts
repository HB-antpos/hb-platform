import type {
  CatalogDeltaPage,
  CatalogPromotion,
  CatalogSyncPlan,
} from "./catalog-snapshot-service";

import {
  HbposApiError,
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "@/core/api/hbpos-api";
import type { components } from "@hb/pos-api-client/openapi";

const CHECKSUM_SPECS = {
  1: {
    marker: "HBPOS-CATALOG-PAGE-CHECKSUM-V1",
    prefix: "sha256-catalog-page-v1:",
  },
  2: {
    marker: "HBPOS-CATALOG-PAGE-CHECKSUM-V2",
    prefix: "sha256-catalog-page-v2:",
  },
} as const;

export type CatalogPageChecksumVersion = keyof typeof CHECKSUM_SPECS;

type GeneratedDownloadLeaseEcho = Readonly<{
  downloadLeaseId?: string | null;
}>;
type GeneratedCatalogPage = components["schemas"]["CatalogSyncPageResponse"]
& GeneratedDownloadLeaseEcho & {
  catalogVersion?: string | null;
  pageChecksum?: string | null;
};
type GeneratedCatalogItem = components["schemas"]["CatalogLookupItemDto"];
type GeneratedDeletedLookup = components["schemas"]["DeletedLookupDto"];
type GeneratedCatalogPromotions = components["schemas"]["CatalogPromotionsResponse"];
type GeneratedCatalogPromotion = components["schemas"]["CatalogPromotionRuleDto"];
type GeneratedCatalogPromotionProduct = components["schemas"]["CatalogPromotionProductDto"];
type GeneratedCatalogSyncPlan = components["schemas"]["CatalogSyncPlanResponse"];
type GeneratedCatalogDeltaPage = components["schemas"]["CatalogDeltaPageResponse"]
& GeneratedDownloadLeaseEcho;
type GeneratedCatalogSyncPlanWithLease = GeneratedCatalogSyncPlan & Readonly<{
  downloadLeaseId?: string | null;
  deltaOperationCount?: number | null;
}>;
type GeneratedCatalogCodeConflicts = components["schemas"]["CatalogCodeConflictsResponse"];

const DELTA_CHECKSUM_MARKER = "HBPOS-CATALOG-DELTA-PAGE-CHECKSUM-V1";
const DELTA_CHECKSUM_PREFIX = "sha256-catalog-delta-page-v1:";

export type CatalogLookupItem = Readonly<{
  storeCode: string;
  productCode: string;
  referenceCode: string | null;
  displayName: string;
  lookupCode: string;
  lookupCodeNormalized: string;
  itemNumber: string | null;
  barcode: string | null;
  retailPrice: number;
  priceSource: components["schemas"]["PriceSourceKind"];
  priceSourceLabel: string;
  quantityFactor: number;
  updatedAt: string | null;
  rowVersion: string | null;
  productImage: string | null;
  discountRate: number | null;
  isSpecialProduct: boolean;
}>;

export type CatalogDeletedLookup = Readonly<{
  storeCode: string;
  lookupCode: string;
  lookupCodeNormalized: string;
  deletedAt: string | null;
}>;

export type VerifiedCatalogSyncPage = Readonly<{
  storeCode: string;
  generatedAt: string;
  cursor: string | null;
  items: readonly CatalogLookupItem[];
  deletedLookups: readonly CatalogDeletedLookup[];
  nextCursor: string | null;
  hasMore: boolean;
  totalCount: number;
  catalogVersion: string;
  pageChecksum: string;
}>;

/**
 * 一码多商品候选：每个冲突查询码下每个不同商品一行（首行即目录胜出项）。
 * available=false 表示服务端尚未算出候选（如由旧快照恢复），调用方必须保留本地旧数据。
 */
export type VerifiedCatalogCodeConflicts = Readonly<{
  storeCode: string;
  generatedAt: string;
  available: boolean;
  items: readonly CatalogLookupItem[];
}>;

export type CatalogPageDigest = (canonicalPayload: string) => Promise<string>;

export class HbposCatalogPageApi {
  public constructor(
    private readonly transport: HbposTransport,
    private readonly digest: CatalogPageDigest = expoSha256,
  ) {}

  public async getPage(input: Readonly<{
    storeCode: string;
    cursor: string | null;
    pageSize: number;
    catalogVersion?: string;
    downloadLeaseId?: string;
    signal?: AbortSignal;
  }>): Promise<VerifiedCatalogSyncPage> {
    const response = await this.transport.request<HbposEnvelope<GeneratedCatalogPage>>({
      method: "GET",
      url: "/api/v1/catalog/sellable-items/page",
      params: {
        storeCode: input.storeCode,
        cursor: input.cursor ?? undefined,
        pageSize: input.pageSize,
        catalogVersion: input.catalogVersion ?? undefined,
        downloadLeaseId: input.downloadLeaseId ?? undefined,
        checksumVersion: 2,
      },
      // WPF 目录下载同样不设隐藏固定超时；页面生命周期信号负责主动取消。
      timeoutMs: 0,
      ...(input.signal ? { signal: input.signal } : {}),
    });
    const rawPage = unwrapHbposEnvelope(response.data);
    verifyDownloadLeaseEcho(input.downloadLeaseId, rawPage.downloadLeaseId);
    const page = normalizePage(rawPage);
    const calculatedChecksum = await calculateCatalogPageChecksum(
      page.items,
      this.digest,
      2,
    );
    if (page.pageChecksum.toLowerCase() !== calculatedChecksum) {
      throw new HbposApiError("Catalog page checksum verification failed.", {
        kind: "envelope",
        code: "CATALOG_PAGE_CHECKSUM_MISMATCH",
      });
    }
    return page;
  }

  public async getSyncPlan(input: Readonly<{
    storeCode: string;
    baseCatalogVersion: string | null;
    signal?: AbortSignal;
  }>): Promise<CatalogSyncPlan> {
    const response = await this.transport.request<HbposEnvelope<GeneratedCatalogSyncPlan>>({
      method: "GET",
      url: "/api/v1/catalog/sync-plan",
      params: {
        storeCode: input.storeCode,
        baseCatalogVersion: input.baseCatalogVersion ?? undefined,
      },
      timeoutMs: 0,
      ...(input.signal ? { signal: input.signal } : {}),
    });
    return normalizeSyncPlan(unwrapHbposEnvelope(response.data), input);
  }

  public async getDeltaPage(input: Readonly<{
    storeCode: string;
    baseCatalogVersion: string;
    targetCatalogVersion: string;
    cursor: string | null;
    pageSize: number;
    downloadLeaseId?: string;
    signal?: AbortSignal;
  }>): Promise<CatalogDeltaPage> {
    const response = await this.transport.request<HbposEnvelope<GeneratedCatalogDeltaPage>>({
      method: "GET",
      url: "/api/v1/catalog/delta/page",
      params: {
        storeCode: input.storeCode,
        baseCatalogVersion: input.baseCatalogVersion,
        targetCatalogVersion: input.targetCatalogVersion,
        cursor: input.cursor ?? undefined,
        pageSize: input.pageSize,
        downloadLeaseId: input.downloadLeaseId ?? undefined,
        checksumVersion: 1,
      },
      timeoutMs: 0,
      ...(input.signal ? { signal: input.signal } : {}),
    });
    const rawPage = unwrapHbposEnvelope(response.data);
    verifyDownloadLeaseEcho(input.downloadLeaseId, rawPage.downloadLeaseId);
    if (
      requiredText(rawPage.baseCatalogVersion, "delta.baseCatalogVersion") !== input.baseCatalogVersion
      || requiredText(rawPage.targetCatalogVersion, "delta.targetCatalogVersion") !== input.targetCatalogVersion
    ) {
      throw invalidPage("delta.version");
    }
    const page = normalizeDeltaPage(rawPage);
    const calculatedChecksum = await calculateCatalogDeltaPageChecksum({
      baseCatalogVersion: input.baseCatalogVersion,
      targetCatalogVersion: input.targetCatalogVersion,
      items: page.items,
      deletedLookups: page.deletedLookups,
    }, this.digest);
    if (page.pageChecksum.toLowerCase() !== calculatedChecksum) {
      throw new HbposApiError("Catalog delta page checksum verification failed.", {
        kind: "envelope",
        code: "CATALOG_DELTA_PAGE_CHECKSUM_MISMATCH",
      });
    }
    return page;
  }

  /**
   * 促销定义会直接影响离线金额，故只接受生成合同中的白名单字段。新字段必须经
   * 人工审查后才能进入 definitionJson，避免后端扩展被客户端静默解释为价格规则。
   */
  public async getPromotions(input: Readonly<{
    storeCode: string;
    signal?: AbortSignal;
  }>): Promise<readonly CatalogPromotion[]> {
    const requestedStoreCode = requiredPromotionText(input.storeCode, "requested storeCode");
    const response = await this.transport.request<HbposEnvelope<GeneratedCatalogPromotions>>({
      method: "GET",
      url: "/api/v1/catalog/promotions",
      params: { storeCode: requestedStoreCode },
      timeoutMs: 0,
      ...(input.signal ? { signal: input.signal } : {}),
    });
    return normalizePromotions(
      unwrapHbposEnvelope(response.data),
      requestedStoreCode,
    );
  }

  /**
   * 一码多商品候选。该接口没有分页 checksum，因此逐字段按目录行口径校验，
   * 门店、查询码规范化或 available 与候选不一致时整体拒绝，由调用方保留本地旧数据。
   */
  public async getCodeConflicts(input: Readonly<{
    storeCode: string;
    signal?: AbortSignal;
  }>): Promise<VerifiedCatalogCodeConflicts> {
    const response = await this.transport.request<HbposEnvelope<GeneratedCatalogCodeConflicts>>({
      method: "GET",
      url: "/api/v1/catalog/sellable-items/code-conflicts",
      params: { storeCode: input.storeCode },
      // 中文注释：候选刷新发生在目录激活之后，设上限避免旁路请求长期占住目录刷新串行门；
      // 服务端冷构建索引仍有充足时间。
      timeoutMs: CODE_CONFLICTS_TIMEOUT_MS,
      ...(input.signal ? { signal: input.signal } : {}),
    });
    return normalizeCodeConflicts(
      unwrapHbposEnvelope(response.data),
      input.storeCode,
    );
  }
}

const CODE_CONFLICTS_TIMEOUT_MS = 60_000;

function normalizeCodeConflicts(
  source: GeneratedCatalogCodeConflicts,
  requestedStoreCode: string,
): VerifiedCatalogCodeConflicts {
  try {
    const storeCode = requiredText(source.storeCode, "codeConflicts.storeCode");
    if (storeCode !== requestedStoreCode) {
      throw invalidPage("codeConflicts.storeCode");
    }
    const generatedAt = requiredTimestamp(source.generatedAt, "codeConflicts.generatedAt");
    const available = requiredBoolean(source.available, "codeConflicts.available");
    const items = requiredArray(
      source.items as readonly GeneratedCatalogItem[] | null | undefined,
      "codeConflicts.items",
    ).map(normalizeItem);
    // 中文注释：服务端未算出候选时只返回空列表；带候选的 available=false 属于自相矛盾的响应。
    if (!available && items.length > 0) {
      throw invalidPage("codeConflicts.available");
    }
    for (const item of items) {
      if (item.storeCode !== requestedStoreCode) {
        throw invalidPage("codeConflicts.item.storeCode");
      }
      if (item.lookupCode.trim().toUpperCase() !== item.lookupCodeNormalized) {
        throw invalidPage("codeConflicts.item.lookupCodeNormalized");
      }
    }
    return { storeCode, generatedAt, available, items };
  } catch (error) {
    if (error instanceof HbposApiError && error.code === "CATALOG_PAGE_INVALID") {
      throw new HbposApiError(
        error.message.replace("Catalog page field", "Catalog code conflicts field"),
        {
          kind: "envelope",
          code: "CATALOG_CODE_CONFLICTS_INVALID",
        },
      );
    }
    throw error;
  }
}

export async function calculateCatalogPageChecksum(
  items: readonly CatalogLookupItem[],
  digest: CatalogPageDigest = expoSha256,
  version: CatalogPageChecksumVersion = 1,
): Promise<string> {
  const spec = CHECKSUM_SPECS[version];
  const canonical = buildCanonicalPage(items, version);
  let digestResult: string;
  try {
    digestResult = await digest(canonical);
  } catch {
    throw new HbposApiError("Catalog page digest is unavailable.", {
      kind: "envelope",
      code: "CATALOG_PAGE_DIGEST_UNAVAILABLE",
    });
  }
  const hex = digestResult.trim().toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(hex)) {
    throw new HbposApiError("Catalog page digest returned an invalid SHA256 value.", {
      kind: "envelope",
      code: "CATALOG_PAGE_DIGEST_INVALID",
    });
  }
  return `${spec.prefix}${hex}`;
}

export async function calculateCatalogDeltaPageChecksum(input: Readonly<{
  baseCatalogVersion: string;
  targetCatalogVersion: string;
  items: readonly CatalogLookupItem[];
  deletedLookups: readonly CatalogDeletedLookup[];
}>, digest: CatalogPageDigest = expoSha256): Promise<string> {
  const canonical = buildCanonicalDeltaPage(input);
  let digestResult: string;
  try {
    digestResult = await digest(canonical);
  } catch {
    throw new HbposApiError("Catalog delta page digest is unavailable.", {
      kind: "envelope",
      code: "CATALOG_DELTA_PAGE_DIGEST_UNAVAILABLE",
    });
  }
  const hex = digestResult.trim().toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(hex)) {
    throw new HbposApiError("Catalog delta digest returned an invalid SHA256 value.", {
      kind: "envelope",
      code: "CATALOG_DELTA_PAGE_DIGEST_INVALID",
    });
  }
  return `${DELTA_CHECKSUM_PREFIX}${hex}`;
}

function normalizePage(page: GeneratedCatalogPage): VerifiedCatalogSyncPage {
  const items = requiredArray(page.items, "items").map(normalizeItem);
  return {
    storeCode: requiredText(page.storeCode, "storeCode"),
    generatedAt: requiredTimestamp(page.generatedAt, "generatedAt"),
    cursor: optionalText(page.cursor, "cursor"),
    items,
    deletedLookups: requiredArray(page.deletedLookups, "deletedLookups").map(normalizeDeletedLookup),
    nextCursor: optionalText(page.nextCursor, "nextCursor"),
    hasMore: requiredBoolean(page.hasMore, "hasMore"),
    totalCount: requiredNonNegativeInteger(page.totalCount, "totalCount"),
    catalogVersion: requiredText(page.catalogVersion, "catalogVersion"),
    pageChecksum: requiredText(page.pageChecksum, "pageChecksum"),
  };
}

function verifyDownloadLeaseEcho(requestedLeaseId: string | undefined, echoedLeaseId: unknown): void {
  // 只有新客户端主动携带租约时才强制回显；无租约请求继续兼容尚未返回该字段的旧后端。
  if (requestedLeaseId === undefined || echoedLeaseId === requestedLeaseId) {
    return;
  }
  throw new HbposApiError("Catalog download lease echo verification failed.", {
    kind: "envelope",
    code: "CATALOG_DOWNLOAD_LEASE_MISMATCH",
  });
}

function normalizeSyncPlan(
  source: GeneratedCatalogSyncPlanWithLease,
  requested: Readonly<{ storeCode: string; baseCatalogVersion: string | null }>,
): CatalogSyncPlan {
  const mode = source.mode;
  if (mode !== "noChange" && mode !== "delta" && mode !== "full") throw invalidPage("syncPlan.mode");
  const storeCode = requiredText(source.storeCode, "syncPlan.storeCode");
  if (storeCode !== requested.storeCode) throw invalidPage("syncPlan.storeCode");
  const baseCatalogVersion = optionalText(source.baseCatalogVersion, "syncPlan.baseCatalogVersion");
  if (baseCatalogVersion !== requested.baseCatalogVersion) throw invalidPage("syncPlan.baseCatalogVersion");
  const targetCatalogVersion = requiredText(source.targetCatalogVersion, "syncPlan.targetCatalogVersion");
  const targetTotal = requiredNonNegativeInteger(source.targetTotal, "syncPlan.targetTotal");
  const downloadLeaseId = source.downloadLeaseId === undefined
    ? undefined
    : optionalText(source.downloadLeaseId, "syncPlan.downloadLeaseId");
  const deltaOperationCount = source.deltaOperationCount === undefined
    ? undefined
    : optionalNonNegativeInteger(source.deltaOperationCount, "syncPlan.deltaOperationCount");
  return {
    mode,
    baseCatalogVersion,
    targetCatalogVersion,
    targetTotal,
    ...(downloadLeaseId === undefined ? {} : { downloadLeaseId }),
    ...(deltaOperationCount === undefined ? {} : { deltaOperationCount }),
  };
}

function normalizeDeltaPage(source: GeneratedCatalogDeltaPage): CatalogDeltaPage {
  const targetCatalogVersion = requiredText(source.targetCatalogVersion, "delta.targetCatalogVersion");
  return {
    storeCode: requiredText(source.storeCode, "delta.storeCode"),
    cursor: optionalText(source.cursor, "delta.cursor"),
    items: requiredArray(source.items as readonly GeneratedCatalogItem[] | null | undefined, "delta.items").map(normalizeItem),
    deletedLookups: requiredArray(source.deletedLookups as readonly GeneratedDeletedLookup[] | null | undefined, "delta.deletedLookups").map(normalizeDeletedLookup),
    nextCursor: optionalText(source.nextCursor, "delta.nextCursor"),
    hasMore: requiredBoolean(source.hasMore, "delta.hasMore"),
    totalCount: requiredNonNegativeInteger(source.targetTotal, "delta.targetTotal"),
    catalogVersion: targetCatalogVersion,
    pageChecksum: requiredText(source.pageChecksum, "delta.pageChecksum"),
  };
}

function normalizePromotions(
  response: GeneratedCatalogPromotions,
  requestedStoreCode: string,
): readonly CatalogPromotion[] {
  if (requiredPromotionText(response.storeCode, "response storeCode") !== requestedStoreCode) {
    throw invalidPromotion("response storeCode");
  }
  // generatedAt 不入库，但必须可解析，防止接受半截或错版本的服务端响应。
  requiredPromotionTimestamp(response.generatedAt, "generatedAt");

  const identifiers = new Set<string>();
  return requiredPromotionArray(response.promotions, "promotions").map((promotion) => {
    const normalized = normalizePromotion(promotion);
    if (identifiers.has(normalized.promotionId)) {
      throw invalidPromotion("duplicate promotionId");
    }
    identifiers.add(normalized.promotionId);
    return normalized;
  });
}

function normalizePromotion(source: GeneratedCatalogPromotion): CatalogPromotion {
  const promotionId = requiredPromotionText(source.promotionId, "promotionId");
  const effectiveStart = requiredPromotionTimestamp(source.effectiveStart, "effectiveStart");
  const effectiveEnd = requiredPromotionTimestamp(source.effectiveEnd, "effectiveEnd");
  if (Date.parse(effectiveStart) > Date.parse(effectiveEnd)) {
    throw invalidPromotion("effective range");
  }

  const products = requiredPromotionArray(source.products, "products").map(
    normalizePromotionProduct,
  );
  if (products.length === 0) {
    throw invalidPromotion("products");
  }
  const productCodes = new Set<string>();
  for (const product of products) {
    if (productCodes.has(product.productCode)) {
      throw invalidPromotion("duplicate productCode");
    }
    productCodes.add(product.productCode);
  }

  const definition = {
    promotionId,
    name: requiredPromotionText(source.name, "name"),
    isExclusive: requiredPromotionBoolean(source.isExclusive, "isExclusive"),
    priority: requiredPromotionInteger(source.priority, "priority"),
    applyQuantity: requiredPromotionPositiveInteger(source.applyQuantity, "applyQuantity"),
    fixedPrice: requiredPromotionAmount(source.fixedPrice, "fixedPrice"),
    maxApplicationsPerOrder: optionalPromotionNonNegativeInteger(
      source.maxApplicationsPerOrder,
      "maxApplicationsPerOrder",
    ),
    effectiveStart,
    effectiveEnd,
    updatedAt: optionalPromotionTimestamp(source.updatedAt, "updatedAt"),
    products,
  };
  return {
    promotionId,
    // 属性插入顺序固定；这样同一服务端定义在所有手持终端上产生完全相同的 JSON。
    definitionJson: JSON.stringify(definition),
    validFromIso: effectiveStart,
    validUntilIso: effectiveEnd,
    priority: definition.priority,
  };
}

function normalizePromotionProduct(
  source: GeneratedCatalogPromotionProduct,
): Readonly<{ productCode: string; unitWeight: number }> {
  return {
    productCode: requiredPromotionText(source.productCode, "product.productCode"),
    unitWeight: requiredPromotionPositiveInteger(source.unitWeight, "product.unitWeight"),
  };
}

function normalizeItem(item: GeneratedCatalogItem): CatalogLookupItem {
  return {
    storeCode: requiredText(item.storeCode, "item.storeCode"),
    productCode: requiredText(item.productCode, "item.productCode"),
    referenceCode: optionalText(item.referenceCode, "item.referenceCode"),
    displayName: requiredRepairableText(item.displayName, "item.displayName"),
    lookupCode: requiredText(item.lookupCode, "item.lookupCode"),
    lookupCodeNormalized: requiredText(item.lookupCodeNormalized, "item.lookupCodeNormalized"),
    itemNumber: optionalText(item.itemNumber, "item.itemNumber"),
    barcode: optionalText(item.barcode, "item.barcode"),
    retailPrice: requiredFiniteNumber(item.retailPrice, "item.retailPrice"),
    priceSource: requiredPriceSource(item.priceSource),
    priceSourceLabel: requiredRepairableText(item.priceSourceLabel, "item.priceSourceLabel"),
    quantityFactor: requiredFiniteNumber(item.quantityFactor, "item.quantityFactor"),
    updatedAt: optionalText(item.updatedAt, "item.updatedAt"),
    rowVersion: optionalText(item.rowVersion, "item.rowVersion"),
    productImage: optionalText(item.productImage, "item.productImage"),
    discountRate: optionalFiniteNumber(item.discountRate, "item.discountRate"),
    isSpecialProduct: requiredBoolean(item.isSpecialProduct, "item.isSpecialProduct"),
  };
}

function normalizeDeletedLookup(item: GeneratedDeletedLookup): CatalogDeletedLookup {
  return {
    storeCode: requiredText(item.storeCode, "deletedLookup.storeCode"),
    lookupCode: requiredText(item.lookupCode, "deletedLookup.lookupCode"),
    lookupCodeNormalized: requiredText(
      item.lookupCodeNormalized,
      "deletedLookup.lookupCodeNormalized",
    ),
    deletedAt: optionalTimestamp(item.deletedAt, "deletedLookup.deletedAt"),
  };
}

function buildCanonicalPage(
  items: readonly CatalogLookupItem[],
  version: CatalogPageChecksumVersion,
): string {
  // 中文注释：v1 为 WPF/旧客户端共用格式，逐字节保持原实现；v2 走缓冲复用构建。
  if (version === 2) {
    return buildCanonicalPageV2(items);
  }
  const values = [
    CHECKSUM_SPECS[version].marker,
    formatCanonicalNumberV1(items.length),
  ];
  for (const item of items) {
    values.push(
      item.storeCode,
      item.productCode,
      item.referenceCode ?? "",
      item.displayName,
      item.lookupCode,
      item.lookupCodeNormalized,
      item.itemNumber ?? "",
      item.barcode ?? "",
      formatCanonicalNumberV1(item.retailPrice),
      formatCanonicalNumberV1(item.priceSource),
      item.priceSourceLabel,
      formatCanonicalNumberV1(item.quantityFactor),
      // 服务端按 UTC 毫秒格式计算摘要；JSON 中等价的 DateTimeOffset 文本必须先对齐。
      optionalTimestamp(item.updatedAt, "item.updatedAt") ?? "",
      item.productImage ?? "",
      item.discountRate === null ? "" : formatCanonicalNumberV1(item.discountRate),
      item.isSpecialProduct ? "1" : "0",
    );
  }
  return values.map((value) => `${value.length}:${value}|`).join("");
}

// 中文注释：v2 canonical 构建优化——复用 8 字节 IEEE754 缓冲与十六进制表，并以
// 分片数组直接输出长度帧，消除每个字段和商品产生的临时字符串/数组。
// 输出格式与后端 v2 完全一致：每字段 UTF-16 长度前缀 + ":" + 内容 + "|"。
const BINARY64_BUFFER = new ArrayBuffer(8);
const BINARY64_VIEW = new DataView(BINARY64_BUFFER);
const HEX_CHARS = "0123456789abcdef";

/** 中文注释：IEEE754 binary64 大端十六进制（16 字符小写）；同步调用，缓冲无并发竞争。 */
function formatCanonicalNumberV2Fast(value: number): string {
  if (!Number.isFinite(value)) {
    throw new HbposApiError("Catalog checksum cannot encode a non-finite number.", {
      kind: "envelope",
      code: "CATALOG_PAGE_VALUE_INVALID",
    });
  }
  BINARY64_VIEW.setFloat64(0, Object.is(value, -0) ? 0 : value, false);
  const bytes = new Uint8Array(BINARY64_BUFFER);
  let hex = "";
  for (const byte of bytes) {
    // 中文注释：byte 范围为 0-255，十六进制表索引恒在界内；空值兜底仅为满足严格索引检查。
    hex += HEX_CHARS[byte >> 4] ?? "";
    hex += HEX_CHARS[byte & 15] ?? "";
  }
  return hex;
}

function buildCanonicalPageV2(items: readonly CatalogLookupItem[]): string {
  const parts: string[] = [];
  appendCanonicalField(parts, CHECKSUM_SPECS[2].marker);
  appendCanonicalField(parts, formatCanonicalNumberV2Fast(items.length));
  for (const item of items) {
    appendCanonicalField(parts, item.storeCode);
    appendCanonicalField(parts, item.productCode);
    appendCanonicalField(parts, item.referenceCode ?? "");
    appendCanonicalField(parts, item.displayName);
    appendCanonicalField(parts, item.lookupCode);
    appendCanonicalField(parts, item.lookupCodeNormalized);
    appendCanonicalField(parts, item.itemNumber ?? "");
    appendCanonicalField(parts, item.barcode ?? "");
    appendCanonicalField(parts, formatCanonicalNumberV2Fast(item.retailPrice));
    appendCanonicalField(parts, formatCanonicalNumberV2Fast(item.priceSource));
    appendCanonicalField(parts, item.priceSourceLabel);
    appendCanonicalField(parts, formatCanonicalNumberV2Fast(item.quantityFactor));
    // 中文注释：服务端按 UTC 毫秒格式计算摘要；JSON 中等价的 DateTimeOffset 文本必须先对齐。
    appendCanonicalField(parts, optionalTimestamp(item.updatedAt, "item.updatedAt") ?? "");
    appendCanonicalField(parts, item.productImage ?? "");
    appendCanonicalField(
      parts,
      item.discountRate === null ? "" : formatCanonicalNumberV2Fast(item.discountRate),
    );
    appendCanonicalField(parts, item.isSpecialProduct ? "1" : "0");
  }
  return parts.join("");
}

/** 中文注释：输出 UTF-16 长度帧：<十进制长度>:<内容>|，与后端跨端协议一致。 */
function appendCanonicalField(parts: string[], value: string): void {
  parts.push(String(value.length), ":", value, "|");
}

function buildCanonicalDeltaPage(input: Readonly<{
  baseCatalogVersion: string;
  targetCatalogVersion: string;
  items: readonly CatalogLookupItem[];
  deletedLookups: readonly CatalogDeletedLookup[];
}>): string {
  const operations = [
    ...input.items.map((item) => ({ kind: "U" as const, key: item.lookupCodeNormalized, item })),
    ...input.deletedLookups.map((deleted) => ({ kind: "D" as const, key: deleted.lookupCodeNormalized, deleted })),
  ].sort((left, right) => left.key < right.key ? -1 : left.key > right.key ? 1 : 0);
  const values = [
    DELTA_CHECKSUM_MARKER,
    input.baseCatalogVersion,
    input.targetCatalogVersion,
    formatCanonicalNumberV1(operations.length),
  ];
  for (const operation of operations) {
    if (operation.kind === "U") {
      const item = operation.item;
      values.push(
        "U", item.storeCode, item.productCode, item.referenceCode ?? "", item.displayName,
        item.lookupCode, item.lookupCodeNormalized, item.itemNumber ?? "", item.barcode ?? "",
        formatCanonicalNumberV1(item.retailPrice), formatCanonicalNumberV1(item.priceSource),
        item.priceSourceLabel, formatCanonicalNumberV1(item.quantityFactor),
        optionalTimestamp(item.updatedAt, "item.updatedAt") ?? "",
        item.productImage ?? "", item.discountRate === null ? "" : formatCanonicalNumberV1(item.discountRate),
        item.isSpecialProduct ? "1" : "0",
      );
    } else {
      const deleted = operation.deleted;
      values.push(
        "D", deleted.storeCode, deleted.lookupCode, deleted.lookupCodeNormalized,
        optionalTimestamp(deleted.deletedAt, "deletedLookup.deletedAt") ?? "",
      );
    }
  }
  return values.map((value) => `${value.length}:${value}|`).join("");
}

async function expoSha256(payload: string): Promise<string> {
  // 同步 require 让 Metro 将原生摘要桥接放入主 bundle，同时避免 Node 合同测试在未调用时解析原生入口。
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const Crypto = require("expo-crypto") as typeof import("expo-crypto");
  return Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, payload);
}

function formatCanonicalNumberV1(value: number): string {
  if (!Number.isFinite(value)) {
    throw new HbposApiError("Catalog checksum cannot encode a non-finite number.", {
      kind: "envelope",
      code: "CATALOG_PAGE_VALUE_INVALID",
    });
  }
  if (Object.is(value, -0)) {
    return "0";
  }

  const text = String(value).toLowerCase();
  if (!text.includes("e")) {
    return text;
  }

  const [mantissa = "", exponentText = "0"] = text.split("e");
  const exponent = Number(exponentText);
  const negative = mantissa.startsWith("-");
  const unsignedMantissa = negative ? mantissa.slice(1) : mantissa;
  const [whole = "", fraction = ""] = unsignedMantissa.split(".");
  const digits = `${whole}${fraction}`;
  const decimalIndex = whole.length + exponent;
  const expanded = decimalIndex <= 0
    ? `0.${"0".repeat(-decimalIndex)}${digits}`
    : decimalIndex >= digits.length
      ? `${digits}${"0".repeat(decimalIndex - digits.length)}`
      : `${digits.slice(0, decimalIndex)}.${digits.slice(decimalIndex)}`;
  return negative ? `-${expanded}` : expanded;
}

function requiredText(value: unknown, field: string): string {
  if (typeof value !== "string" || value.length === 0) {
    throw invalidPage(field);
  }
  return value;
}

function requiredRepairableText(value: unknown, field: string): string {
  // 商品展示内容先按原文参与摘要；空白内容由 staging 安全回退，不影响身份校验。
  if (typeof value !== "string") {
    throw invalidPage(field);
  }
  return value;
}

function optionalText(value: unknown, field: string): string | null {
  if (value === null || value === undefined || value === "") {
    return null;
  }
  return requiredText(value, field);
}

function requiredTimestamp(value: unknown, field: string): string {
  const timestamp = optionalTimestamp(value, field);
  if (timestamp === null) {
    throw invalidPage(field);
  }
  return timestamp;
}

function optionalTimestamp(value: unknown, field: string): string | null {
  const text = optionalText(value, field);
  if (text === null) {
    return null;
  }
  const parsed = new Date(text);
  if (Number.isNaN(parsed.valueOf())) {
    throw invalidPage(field);
  }
  return parsed.toISOString();
}

function requiredFiniteNumber(value: unknown, field: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw invalidPage(field);
  }
  return value;
}

function optionalFiniteNumber(value: unknown, field: string): number | null {
  if (value === null || value === undefined) {
    return null;
  }
  return requiredFiniteNumber(value, field);
}

function requiredBoolean(value: unknown, field: string): boolean {
  if (typeof value !== "boolean") {
    throw invalidPage(field);
  }
  return value;
}

function requiredNonNegativeInteger(value: unknown, field: string): number {
  if (!Number.isSafeInteger(value) || Number(value) < 0) {
    throw invalidPage(field);
  }
  return Number(value);
}

function optionalNonNegativeInteger(value: unknown, field: string): number | null {
  if (value === null || value === undefined) return null;
  return requiredNonNegativeInteger(value, field);
}

function requiredArray<T>(value: readonly T[] | null | undefined, field: string): readonly T[] {
  if (!Array.isArray(value)) {
    throw invalidPage(field);
  }
  return value;
}

function requiredPriceSource(value: unknown): components["schemas"]["PriceSourceKind"] {
  if (value !== 0 && value !== 1 && value !== 2 && value !== 3 && value !== 4) {
    throw invalidPage("item.priceSource");
  }
  return value;
}

function requiredPromotionText(value: unknown, field: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw invalidPromotion(field);
  }
  return value.trim();
}

function requiredPromotionTimestamp(value: unknown, field: string): string {
  const timestamp = optionalPromotionTimestamp(value, field);
  if (timestamp === null) {
    throw invalidPromotion(field);
  }
  return timestamp;
}

function optionalPromotionTimestamp(value: unknown, field: string): string | null {
  if (value === null || value === undefined || value === "") {
    return null;
  }
  const text = requiredPromotionText(value, field);
  const parsed = Date.parse(text);
  if (!Number.isFinite(parsed)) {
    throw invalidPromotion(field);
  }
  return new Date(parsed).toISOString();
}

function requiredPromotionBoolean(value: unknown, field: string): boolean {
  if (typeof value !== "boolean") {
    throw invalidPromotion(field);
  }
  return value;
}

function requiredPromotionInteger(value: unknown, field: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value)) {
    throw invalidPromotion(field);
  }
  return value;
}

function requiredPromotionPositiveInteger(value: unknown, field: string): number {
  const number = requiredPromotionInteger(value, field);
  if (number <= 0) {
    throw invalidPromotion(field);
  }
  return number;
}

function optionalPromotionNonNegativeInteger(value: unknown, field: string): number | null {
  if (value === null || value === undefined) {
    return null;
  }
  const number = requiredPromotionInteger(value, field);
  if (number < 0) {
    throw invalidPromotion(field);
  }
  return number;
}

function requiredPromotionAmount(value: unknown, field: string): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0) {
    throw invalidPromotion(field);
  }
  const cents = Math.round(value * 100);
  const tolerance = Number.EPSILON * Math.max(1, Math.abs(value * 100)) * 8;
  if (!Number.isSafeInteger(cents) || Math.abs(value * 100 - cents) > tolerance) {
    throw invalidPromotion(field);
  }
  return value;
}

function requiredPromotionArray<T>(
  value: readonly T[] | null | undefined,
  field: string,
): readonly T[] {
  if (!Array.isArray(value)) {
    throw invalidPromotion(field);
  }
  return value;
}

function invalidPage(field: string): HbposApiError {
  return new HbposApiError(`Catalog page field is invalid: ${field}.`, {
    kind: "envelope",
    code: "CATALOG_PAGE_INVALID",
  });
}

function invalidPromotion(field: string): HbposApiError {
  return new HbposApiError(`Catalog promotion field is invalid: ${field}.`, {
    kind: "envelope",
    code: "CATALOG_PROMOTIONS_INVALID",
  });
}
