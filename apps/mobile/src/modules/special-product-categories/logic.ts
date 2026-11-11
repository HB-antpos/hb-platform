// 特殊商品分类的纯逻辑：响应归一化、排序后重排、名称校验、添加商品三种情况、同步预览汇总、reason 码映射。
// 不依赖 React Native / i18n，可直接在 Node 下做单元测试；界面文案由调用方按返回的码映射。
import { unwrapApiEnvelope } from "../../shared/api/api-envelope";
import type {
  AssignResult,
  CandidateProduct,
  CategoryOverview,
  CategoryProduct,
  CategoryProductsPage,
  SpecialCategory,
  SyncCounts,
  SyncPreviewTarget,
  SyncRequest,
  SyncResultTarget,
  SyncSkippedProduct,
  SyncTarget,
  SyncTargetsResponse,
} from "./types";

// ---------------- 归一化（容忍缺字段 / 数字字符串 / PascalCase） ----------------

type RecordValue = Record<string, unknown>;

const record = (value: unknown): RecordValue =>
  value && typeof value === "object" && !Array.isArray(value) ? (value as RecordValue) : {};
const pick = (source: RecordValue, ...keys: string[]) => {
  for (const key of keys) {
    if (source[key] !== undefined && source[key] !== null) return source[key];
  }
  return undefined;
};
const text = (value: unknown) => (typeof value === "string" || typeof value === "number" ? String(value).trim() : "");
const nullableText = (value: unknown) => text(value) || null;
const count = (value: unknown) => {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? Math.trunc(parsed) : 0;
};
const nullableNumber = (value: unknown) => {
  if (value === null || value === undefined || value === "") return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
};
const bool = (value: unknown) => value === true || value === "true" || value === 1;
const list = (value: unknown): unknown[] => (Array.isArray(value) ? value : []);

export function normalizeCategory(value: unknown): SpecialCategory | null {
  const raw = record(value);
  const id = text(pick(raw, "id", "Id", "categoryId"));
  if (!id) return null;
  return {
    id,
    name: text(pick(raw, "name", "Name")),
    color: normalizeColor(pick(raw, "color", "Color")),
    sortOrder: Number.isFinite(Number(raw.sortOrder)) ? Number(raw.sortOrder) : 0,
    itemCount: count(pick(raw, "itemCount", "ItemCount")),
  };
}

/** 分类列表按 sortOrder 升序（同值按名称），保证界面顺序与收银机一致。 */
export function sortCategories(categories: readonly SpecialCategory[]): SpecialCategory[] {
  return [...categories].sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
}

export function normalizeCategories(value: unknown): SpecialCategory[] {
  return sortCategories(list(value).map(normalizeCategory).filter((item): item is SpecialCategory => item !== null));
}

export function normalizeOverview(payload: unknown, fallbackStoreCode: string): CategoryOverview {
  const raw = record(unwrapApiEnvelope(payload));
  return {
    storeCode: text(raw.storeCode) || fallbackStoreCode,
    totalCount: count(raw.totalCount),
    uncategorizedCount: count(raw.uncategorizedCount),
    categories: normalizeCategories(raw.categories),
  };
}

function normalizeProductBase(raw: RecordValue) {
  return {
    productCode: text(pick(raw, "productCode", "ProductCode")),
    displayName: text(pick(raw, "displayName", "DisplayName", "name")),
    itemNumber: text(pick(raw, "itemNumber", "ItemNumber")),
    barcode: text(pick(raw, "barcode", "Barcode")),
    retailPrice: nullableNumber(pick(raw, "retailPrice", "RetailPrice")),
    productImage: nullableText(pick(raw, "productImage", "ProductImage")),
    categoryId: nullableText(pick(raw, "categoryId", "CategoryId")),
    categoryName: nullableText(pick(raw, "categoryName", "CategoryName")),
  };
}

export function normalizeProductsPage(payload: unknown): CategoryProductsPage {
  const raw = record(unwrapApiEnvelope(payload));
  const items: CategoryProduct[] = list(raw.items)
    .map((item) => {
      const row = record(item);
      return { ...normalizeProductBase(row), sortOrder: nullableNumber(row.sortOrder) };
    })
    .filter((item) => item.productCode);
  return {
    items,
    page: Math.max(1, count(raw.page) || 1),
    pageSize: count(raw.pageSize) || items.length,
    total: count(raw.total) || items.length,
  };
}

export function normalizeCandidates(payload: unknown): CandidateProduct[] {
  const body = unwrapApiEnvelope(payload);
  return list(Array.isArray(body) ? body : record(body).items)
    .map((item) => {
      const row = record(item);
      return { ...normalizeProductBase(row), isSpecialProduct: bool(pick(row, "isSpecialProduct", "IsSpecialProduct")) };
    })
    .filter((item) => item.productCode);
}

export function normalizeAssignResult(payload: unknown): AssignResult {
  const raw = record(unwrapApiEnvelope(payload));
  return {
    assignedCount: count(raw.assignedCount),
    markedCount: count(raw.markedCount),
    skipped: list(raw.skipped)
      .map((item) => ({ productCode: text(record(item).productCode), reason: text(record(item).reason) }))
      .filter((item) => item.productCode),
  };
}

function normalizeSkipped(value: unknown): SyncSkippedProduct[] {
  return list(value)
    .map((item) => {
      const row = record(item);
      const productCode = text(row.productCode);
      return { productCode, displayName: text(row.displayName) || productCode, reason: text(row.reason) };
    })
    .filter((item) => item.productCode);
}

function normalizeCounts(raw: RecordValue): SyncCounts {
  return {
    createdCategories: count(raw.createdCategories),
    mergedCategories: count(raw.mergedCategories),
    removedCategories: count(raw.removedCategories),
    newlyMarkedProducts: count(raw.newlyMarkedProducts),
    reassignedProducts: count(raw.reassignedProducts),
    unchangedProducts: count(raw.unchangedProducts),
    unmarkedProducts: count(raw.unmarkedProducts),
    skipped: normalizeSkipped(raw.skipped),
  };
}

export function normalizeSyncTargets(payload: unknown, sourceStoreCode: string): SyncTargetsResponse {
  const raw = record(unwrapApiEnvelope(payload));
  const source = record(raw.source);
  const targets: SyncTarget[] = list(raw.targets)
    .map((item) => {
      const row = record(item);
      const storeCode = text(row.storeCode);
      return {
        storeCode,
        storeName: text(row.storeName) || storeCode,
        categoryCount: count(row.categoryCount),
        specialProductCount: count(row.specialProductCount),
      };
    })
    .filter((item) => item.storeCode);
  return {
    source: {
      storeCode: text(source.storeCode) || sourceStoreCode,
      categoryCount: count(source.categoryCount),
      productCount: count(source.productCount),
    },
    targets,
  };
}

export function normalizeSyncPreview(payload: unknown): SyncPreviewTarget[] {
  const raw = record(unwrapApiEnvelope(payload));
  return list(raw.targets)
    .map((item) => {
      const row = record(item);
      const storeCode = text(row.storeCode);
      return { storeCode, storeName: text(row.storeName) || storeCode, error: nullableText(row.error), ...normalizeCounts(row) };
    })
    .filter((item) => item.storeCode);
}

export function normalizeSyncResults(payload: unknown): SyncResultTarget[] {
  const raw = record(unwrapApiEnvelope(payload));
  return list(raw.results)
    .map((item) => {
      const row = record(item);
      const storeCode = text(row.storeCode);
      return {
        storeCode,
        storeName: text(row.storeName) || storeCode,
        ok: row.ok === true,
        error: nullableText(row.error),
        ...normalizeCounts(row),
      };
    })
    .filter((item) => item.storeCode);
}

// ---------------- 颜色与名称校验 ----------------

/** 分类可选颜色（设计图里的几种 + 补充），都是 #RRGGBB。 */
export const CATEGORY_COLORS = [
  "#F76707",
  "#1677FF",
  "#C2255C",
  "#0C8599",
  "#2F9E44",
  "#F59F00",
  "#E03131",
  "#7048E8",
  "#495057",
  "#5C940D",
] as const;

const HEX_COLOR = /^#[0-9a-fA-F]{6}$/;

/** 只接受 #RRGGBB（大写化）；其它一律当没有颜色，与后端「color 为 #RRGGBB 或 null」一致。 */
export function normalizeColor(value: unknown): string | null {
  const textValue = typeof value === "string" ? value.trim() : "";
  return HEX_COLOR.test(textValue) ? textValue.toUpperCase() : null;
}

export const CATEGORY_NAME_MAX_LENGTH = 30;

export type CategoryNameIssue = "empty" | "tooLong" | "duplicate";

/**
 * 分类名校验：trim 后 1–30 个字符（按字符而非 UTF-16 单元数，emoji 算一个），
 * 同店不区分大小写不重复（编辑自己时排除自己）。返回 trim 后的名称或问题码，文案由界面映射。
 */
export function validateCategoryName(
  rawName: string,
  existing: readonly Pick<SpecialCategory, "id" | "name">[],
  selfId?: string | null,
): { ok: true; name: string } | { ok: false; issue: CategoryNameIssue } {
  const name = rawName.trim();
  if (!name) return { ok: false, issue: "empty" };
  if (Array.from(name).length > CATEGORY_NAME_MAX_LENGTH) return { ok: false, issue: "tooLong" };
  const lowered = name.toLowerCase();
  if (existing.some((item) => item.id !== selfId && item.name.trim().toLowerCase() === lowered)) {
    return { ok: false, issue: "duplicate" };
  }
  return { ok: true, name };
}

// ---------------- 拖动排序 / 上移下移 ----------------

/** 把 ids 中 from 位置的元素移到 to 位置，返回新数组；越界 / 不动返回原数组引用，便于调用方判断「没有变化」。 */
export function moveItem<T>(items: readonly T[], from: number, to: number): T[] {
  if (from < 0 || from >= items.length) return items as T[];
  const target = Math.min(Math.max(to, 0), items.length - 1);
  if (target === from) return items as T[];
  const next = [...items];
  const [moved] = next.splice(from, 1);
  next.splice(target, 0, moved);
  return next;
}

/**
 * 拖动时手指落点对应的目标下标：行高固定，被拖行顶边 = 起始下标 × 行高 + 纵向位移，
 * 取最近的槽位并夹在 [0, count - 1]。
 */
export function computeDropIndex(startIndex: number, translationY: number, rowHeight: number, count: number): number {
  if (count <= 0 || rowHeight <= 0) return 0;
  const raw = Math.round(startIndex + translationY / rowHeight);
  return Math.min(Math.max(raw, 0), count - 1);
}

/** 拖动过程中各行应处的槽位：被拖行落到 targetIndex，其余按原相对顺序补位。返回 id → 槽位下标。 */
export function buildSlotMap(ids: readonly string[], dragId: string | null, targetIndex: number): Map<string, number> {
  const from = dragId ? ids.indexOf(dragId) : -1;
  const ordered = from < 0 ? [...ids] : moveItem(ids, from, targetIndex);
  return new Map(ordered.map((id, index) => [id, index]));
}

/**
 * 用本地乐观顺序（id 列表）重排服务器返回的分类：列表里没有的 id 忽略，服务器有而本地顺序没有的（别处新建）排到末尾。
 * localOrder 为 null 表示完全跟随服务器。
 */
export function applyLocalOrder(categories: readonly SpecialCategory[], localOrder: readonly string[] | null): SpecialCategory[] {
  if (!localOrder) return [...categories];
  const byId = new Map(categories.map((item) => [item.id, item]));
  const ordered: SpecialCategory[] = [];
  for (const id of localOrder) {
    const found = byId.get(id);
    if (found) {
      ordered.push(found);
      byId.delete(id);
    }
  }
  return [...ordered, ...byId.values()];
}

export function sameOrder(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((id, index) => id === b[index]);
}

/** 后端业务限制（见 StoreSpecialProductCategoryService）：每店最多 50 个分类；assign / unmark 单次最多 500 个商品编码。 */
export const MAX_CATEGORIES_PER_STORE = 50;
export const MAX_BATCH_PRODUCT_CODES = 500;

/** 数组按固定大小切块（批量接口单次上限）。 */
export function chunk<T>(items: readonly T[], size: number): T[][] {
  const result: T[][] = [];
  for (let index = 0; index < items.length; index += size) result.push(items.slice(index, index + size));
  return result;
}

/** 从错误里取稳定的业务错误码：unwrapApiEnvelope 抛出的 error.code，或 HTTP 错误体里的 errorCode / code。 */
export function getErrorCode(error: unknown): string | null {
  const record = error as { code?: unknown; response?: { data?: Record<string, unknown> } } | null;
  const data = record?.response?.data;
  const candidate = [record?.code, data?.errorCode, data?.ErrorCode, data?.code].find((item) => typeof item === "string" && item.trim());
  return typeof candidate === "string" ? candidate.trim().toUpperCase() : null;
}

/** 后端错误码 → 界面文案键（相对 errors.*）；其余错误码（VALIDATION_ERROR 等）显示后端中文原因或通用文案。 */
const ERROR_CODE_KEYS: Record<string, string> = {
  STORE_OUT_OF_SCOPE: "forbidden",
  STORE_NOT_FOUND: "storeNotFound",
  SPECIAL_CATEGORY_NOT_FOUND: "notFound",
  SPECIAL_CATEGORY_NAME_DUPLICATE: "nameDuplicate",
  SPECIAL_CATEGORY_LIMIT_REACHED: "limitReached",
  SPECIAL_CATEGORY_ORDER_CONFLICT: "orderConflict",
  SPECIAL_CATEGORY_ASSIGN_CONFLICT: "assignConflict",
  SPECIAL_CATEGORY_REPLACE_NOT_CONFIRMED: "replaceNotConfirmed",
};

export function errorCodeKey(error: unknown): string | null {
  const code = getErrorCode(error);
  return (code && ERROR_CODE_KEYS[code]) || null;
}

/** execute 结果里每店 error 的固定码：该店事务失败并已回滚（不再带异常原文）。 */
export const SYNC_STORE_FAILED_CODE = "SYNC_STORE_FAILED";

/** 结果页单店失败原因：固定码 → 本地化文案键（相对 sync.*）；其它（可读文案）原样显示。 */
export function syncResultErrorKey(error: string | null): "storeFailedRolledBack" | "storeFailed" | "unknownError" {
  if (!error) return "unknownError";
  return error.trim().toUpperCase() === SYNC_STORE_FAILED_CODE ? "storeFailedRolledBack" : "storeFailed";
}

/**
 * 是否属于「数据被别人改过」的冲突：排列与当前数据不一致（SPECIAL_CATEGORY_ORDER_CONFLICT），
 * 或没有可识别业务码的 HTTP 409。注意 409 还会用于「同名」「分类数已达上限」，那两种不是冲突，不能提示「已被别人改过」。
 * 排序提交无论冲突还是网络失败都回滚到服务器顺序；冲突时额外提示「分类已被他人修改，已刷新」。
 */
export function isConflictError(error: unknown): boolean {
  const code = getErrorCode(error);
  if (code === "SPECIAL_CATEGORY_ORDER_CONFLICT") return true;
  if (code && /CONFLICT/.test(code)) return true;
  const status = (error as { response?: { status?: number } } | null)?.response?.status;
  return status === 409 && !(code && ERROR_CODE_KEYS[code]);
}

// ---------------- 添加商品：三种情况 ----------------

export type CandidateSituation =
  /** 还不是特殊商品：标记并放进本分类。 */
  | "mark"
  /** 已在别的分类：移过来（一个商品只属于一个分类）。 */
  | "move"
  /** 已是特殊商品但还没归类：放进本分类。 */
  | "fromUncategorized"
  /** 已在本分类：置灰不可选。 */
  | "already";

export function classifyCandidate(candidate: CandidateProduct, targetCategoryId: string): CandidateSituation {
  if (!candidate.isSpecialProduct) return "mark";
  if (!candidate.categoryId) return "fromUncategorized";
  return candidate.categoryId === targetCategoryId ? "already" : "move";
}

/** 「添加商品」关键字至少 2 个字符才请求（与后端 candidates 约束一致）。 */
export const CANDIDATE_MIN_KEYWORD = 2;
export function canSearchCandidates(keyword: string): boolean {
  return Array.from(keyword.trim()).length >= CANDIDATE_MIN_KEYWORD;
}

// ---------------- 同步 ----------------

/** 跳过原因码 → i18n 键（相对 skippedReason.*）；未知码返回 null，界面显示「未知原因（码）」。 */
const SKIP_REASON_KEYS: Record<string, string> = {
  NO_STORE_PRICE: "noStorePrice",
  NO_STORE_RETAIL_PRICE: "noStorePrice",
  PRODUCT_NOT_FOUND: "productNotFound",
  NOT_FOUND: "productNotFound",
  NOT_SPECIAL: "notSpecial",
  NOT_SPECIAL_PRODUCT: "notSpecial",
  CATEGORY_NOT_FOUND: "categoryNotFound",
};

export function skippedReasonKey(reason: string): string | null {
  const normalized = reason.trim().toUpperCase().replace(/[^A-Z0-9]+/g, "_");
  return SKIP_REASON_KEYS[normalized] ?? null;
}

export interface SyncTotals {
  storeCount: number;
  createdCategories: number;
  mergedCategories: number;
  removedCategories: number;
  newlyMarkedProducts: number;
  reassignedProducts: number;
  unchangedProducts: number;
  unmarkedProducts: number;
  skippedProducts: number;
}

/** 多家分店的计数求和（预览顶部与结果页汇总用）。 */
export function sumSyncCounts(targets: readonly SyncCounts[]): SyncTotals {
  return targets.reduce<SyncTotals>(
    (total, item) => ({
      storeCount: total.storeCount + 1,
      createdCategories: total.createdCategories + item.createdCategories,
      mergedCategories: total.mergedCategories + item.mergedCategories,
      removedCategories: total.removedCategories + item.removedCategories,
      newlyMarkedProducts: total.newlyMarkedProducts + item.newlyMarkedProducts,
      reassignedProducts: total.reassignedProducts + item.reassignedProducts,
      unchangedProducts: total.unchangedProducts + item.unchangedProducts,
      unmarkedProducts: total.unmarkedProducts + item.unmarkedProducts,
      skippedProducts: total.skippedProducts + item.skipped.length,
    }),
    {
      storeCount: 0,
      createdCategories: 0,
      mergedCategories: 0,
      removedCategories: 0,
      newlyMarkedProducts: 0,
      reassignedProducts: 0,
      unchangedProducts: 0,
      unmarkedProducts: 0,
      skippedProducts: 0,
    },
  );
}

/** 覆盖模式下预览里是否存在破坏性变更（删分类 / 取消标记），决定红色提示与二次确认的内容。 */
export function hasDestructiveChanges(targets: readonly SyncCounts[]): boolean {
  return targets.some((item) => item.removedCategories > 0 || item.unmarkedProducts > 0);
}

/** 「新标记特殊商品后不再跟随仓库统一改价」的提示只在会新标记商品时出现。 */
export function willMarkNewSpecialProducts(scope: string, targets: readonly SyncCounts[]): boolean {
  return scope === "categories-and-products" && targets.some((item) => item.newlyMarkedProducts > 0);
}

export function buildSyncRequest(input: SyncRequest): SyncRequest {
  // 去重并保持勾选顺序，避免重复分店在后端被处理两次。
  return { ...input, targetStoreCodes: [...new Set(input.targetStoreCodes)] };
}

/** 所有可勾选目标分店编号（目前后端已排除来源与停用店，这里只过滤空值）。 */
export function selectableTargetCodes(targets: readonly SyncTarget[]): string[] {
  return targets.map((item) => item.storeCode).filter(Boolean);
}

/** 目标分店列表只可能保留仍在后端返回里的勾选（刷新目标列表后丢弃已不存在的编号）。 */
export function pruneSelection(selected: readonly string[], available: readonly string[]): string[] {
  const allowed = new Set(available);
  return selected.filter((code) => allowed.has(code));
}

export function summarizeSyncOutcome(results: readonly SyncResultTarget[]) {
  const succeeded = results.filter((item) => item.ok);
  const failed = results.filter((item) => !item.ok);
  return {
    succeeded,
    failed,
    skippedProducts: succeeded.reduce((sum, item) => sum + item.skipped.length, 0),
  };
}

// ---------------- 分店范围 ----------------

export interface StoreLike {
  storeCode: string;
  storeName: string;
}

/**
 * 「管理的分店」：managedCodes 为 null 表示不限分店（管理员 / 仓库经理），全部可选；
 * 否则只保留主分店（managedStoreCodes() 的口径）。结果按名称排序。
 */
export function resolveManagedStores<T extends StoreLike>(stores: readonly T[], managedCodes: readonly string[] | null): T[] {
  const allowed = managedCodes ? new Set(managedCodes) : null;
  return stores
    .filter((store) => store.storeCode && (!allowed || allowed.has(store.storeCode)))
    .sort((a, b) => (a.storeName || a.storeCode).localeCompare(b.storeName || b.storeCode));
}

/** 初始分店：优先沿用全局已选分店，其次第一家；都不在可管理范围内则为空。 */
export function pickInitialStore(stores: readonly StoreLike[], preferredCode: string | null | undefined): string | null {
  if (preferredCode && stores.some((store) => store.storeCode === preferredCode)) return preferredCode;
  return stores[0]?.storeCode ?? null;
}
