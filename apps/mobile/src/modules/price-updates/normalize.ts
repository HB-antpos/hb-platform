import type {
  PriceNotificationPreview,
  PriceUpdateBatchResult,
  PriceUpdateBatchResultItem,
  PriceUpdateChangedField,
  PriceUpdateCompletionMode,
  PriceUpdateHqSyncStatus,
  PriceUpdateTaskKind,
  PriceUpdateTaskStatus,
  StorePriceUpdateStoreSummary,
  StorePriceUpdateSummary,
  StorePriceUpdateTask,
  StorePriceUpdateTaskPage,
  SyncTargetStore,
  SyncTargetsResult,
} from "./types";

type RawRecord = Record<string, unknown>;

const TASK_STATUSES = new Set<PriceUpdateTaskStatus>(["Pending", "Completed", "Cancelled"]);
const COMPLETION_MODES = new Set<PriceUpdateCompletionMode>([
  "Printed",
  "MarkedReplaced",
  "KeptStorePrice",
  "PriceAligned",
]);
const HQ_SYNC_STATUSES = new Set<PriceUpdateHqSyncStatus>([
  "pending",
  "processing",
  "retrying",
  "succeeded",
  "blocked",
  "superseded",
]);

function asRecord(payload: unknown): RawRecord {
  return (payload && typeof payload === "object" ? payload : {}) as RawRecord;
}

/** 后端约定 camelCase，但沿用商品维护模块的写法同时兼容 PascalCase。 */
function pick(data: RawRecord, key: string): unknown {
  if (data[key] !== undefined) {
    return data[key];
  }
  return data[key.charAt(0).toUpperCase() + key.slice(1)];
}

function toNumber(value: unknown): number | null {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function toCount(value: unknown): number {
  const numeric = toNumber(value);
  return numeric != null && numeric > 0 ? Math.trunc(numeric) : 0;
}

function toText(value: unknown): string | null {
  if (typeof value !== "string") {
    return null;
  }
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

function normalizeChangedFields(value: unknown): PriceUpdateChangedField[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const result: PriceUpdateChangedField[] = [];
  for (const item of value) {
    const normalized = typeof item === "string" ? item.trim().toLowerCase() : "";
    const field: PriceUpdateChangedField | null =
      normalized === "retailprice" ? "retailPrice" : normalized === "discountrate" ? "discountRate" : null;
    if (field && !result.includes(field)) {
      result.push(field);
    }
  }
  return result;
}

export function normalizePriceUpdateTask(payload: unknown): StorePriceUpdateTask {
  const data = asRecord(payload);
  const status = toText(pick(data, "status")) as PriceUpdateTaskStatus | null;
  const kind = toText(pick(data, "kind")) as PriceUpdateTaskKind | null;
  const completionMode = toText(pick(data, "completionMode")) as PriceUpdateCompletionMode | null;
  const hqSyncStatus = toText(pick(data, "hqSyncStatus"))?.toLowerCase() as PriceUpdateHqSyncStatus | undefined;

  return {
    id: toNumber(pick(data, "id")) ?? 0,
    storeCode: toText(pick(data, "storeCode")) ?? "",
    storeName: toText(pick(data, "storeName")),
    productCode: toText(pick(data, "productCode")) ?? "",
    storeRetailPriceUuid: toText(pick(data, "storeRetailPriceUuid")),
    productName: toText(pick(data, "productName")),
    itemNumber: toText(pick(data, "itemNumber")),
    barcode: toText(pick(data, "barcode")),
    productImage: toText(pick(data, "productImage")),
    status: status && TASK_STATUSES.has(status) ? status : "Pending",
    // 未知类型按「待换标签」收紧：不会误触发改价接口。
    kind: kind === "PriceUpdate" ? "PriceUpdate" : "LabelOnly",
    changedFields: normalizeChangedFields(pick(data, "changedFields")),
    shelfRetailPrice: toNumber(pick(data, "shelfRetailPrice")),
    shelfDiscountRate: toNumber(pick(data, "shelfDiscountRate")),
    storeRetailPrice: toNumber(pick(data, "storeRetailPrice")),
    storeDiscountRate: toNumber(pick(data, "storeDiscountRate")),
    targetRetailPrice: toNumber(pick(data, "targetRetailPrice")),
    targetDiscountRate: toNumber(pick(data, "targetDiscountRate")),
    initiatorName: toText(pick(data, "initiatorName")) ?? "",
    initiatorSource: toText(pick(data, "initiatorSource")) ?? "",
    initiatorReference: toText(pick(data, "initiatorReference")),
    initiatedAtUtc: toText(pick(data, "initiatedAtUtc")) ?? "",
    changeCount: Math.max(1, toCount(pick(data, "changeCount"))),
    // 旧后端没有这些字段：首次发起时间回退到发起时间，等待 / 超期按未超期处理（不会误锁报表）。
    firstInitiatedAtUtc:
      toText(pick(data, "firstInitiatedAtUtc")) ?? toText(pick(data, "initiatedAtUtc")) ?? "",
    waitingDays: toCount(pick(data, "waitingDays")),
    isOverdue: pick(data, "isOverdue") === true,
    overdueByDays: toCount(pick(data, "overdueByDays")),
    priceAppliedBy: toText(pick(data, "priceAppliedBy")),
    priceAppliedAtUtc: toText(pick(data, "priceAppliedAtUtc")),
    completionMode: completionMode && COMPLETION_MODES.has(completionMode) ? completionMode : null,
    completedBy: toText(pick(data, "completedBy")),
    completedAtUtc: toText(pick(data, "completedAtUtc")),
    labelPrintCount: toCount(pick(data, "labelPrintCount")),
    hqSyncOperationId: toText(pick(data, "hqSyncOperationId")),
    hqSyncStatus: hqSyncStatus && HQ_SYNC_STATUSES.has(hqSyncStatus) ? hqSyncStatus : null,
  };
}

export function normalizePriceUpdateTaskPage(payload: unknown): StorePriceUpdateTaskPage {
  const data = asRecord(payload);
  const rawItems = pick(data, "items");
  return {
    items: Array.isArray(rawItems)
      ? rawItems.map(normalizePriceUpdateTask).filter((item) => item.id > 0)
      : [],
    total: toCount(pick(data, "total")),
    page: Math.max(1, toCount(pick(data, "page"))),
    pageSize: Math.max(1, toCount(pick(data, "pageSize"))),
    pendingCount: toCount(pick(data, "pendingCount")),
    pendingPriceUpdateCount: toCount(pick(data, "pendingPriceUpdateCount")),
    pendingLabelOnlyCount: toCount(pick(data, "pendingLabelOnlyCount")),
    completedCount: toCount(pick(data, "completedCount")),
    pendingOverdueCount: toCount(pick(data, "pendingOverdueCount")),
    overdueDays: toCount(pick(data, "overdueDays")),
    hqSyncEnabled: Boolean(pick(data, "hqSyncEnabled")),
  };
}

function normalizeStoreSummary(payload: unknown): StorePriceUpdateStoreSummary {
  const data = asRecord(payload);
  const storeCode = toText(pick(data, "storeCode")) ?? "";
  const pendingPriceUpdateCount = toCount(pick(data, "pendingPriceUpdateCount"));
  const pendingLabelOnlyCount = toCount(pick(data, "pendingLabelOnlyCount"));
  return {
    storeCode,
    storeName: toText(pick(data, "storeName")) ?? storeCode,
    pendingPriceUpdateCount,
    pendingLabelOnlyCount,
    // 以两类之和为准：后端的合计字段与明细不一致时，宁可信明细，避免角标和列表对不上。
    pendingCount: pendingPriceUpdateCount + pendingLabelOnlyCount,
    overdueCount: toCount(pick(data, "overdueCount")),
  };
}

/** 按分店汇总：没有分店代码的行直接丢弃，避免后续按分店匹配时出现空键。 */
export function normalizePriceUpdateSummary(payload: unknown): StorePriceUpdateSummary {
  const data = asRecord(payload);
  const rawStores = pick(data, "stores");
  return {
    stores: Array.isArray(rawStores)
      ? rawStores.map(normalizeStoreSummary).filter((item) => item.storeCode)
      : [],
    overdueDays: toCount(pick(data, "overdueDays")),
  };
}

export function normalizePriceUpdatePendingCount(payload: unknown): number {
  return toCount(pick(asRecord(payload), "pendingCount"));
}

function normalizeBatchResultItem(payload: unknown): PriceUpdateBatchResultItem {
  const data = asRecord(payload);
  const rawTask = pick(data, "task");
  return {
    taskId: toNumber(pick(data, "taskId")) ?? 0,
    success: Boolean(pick(data, "success")),
    code: toText(pick(data, "code"))?.toLowerCase() ?? "failed",
    message: toText(pick(data, "message")),
    task: rawTask && typeof rawTask === "object" ? normalizePriceUpdateTask(rawTask) : null,
  };
}

export function normalizePriceUpdateBatchResult(payload: unknown): PriceUpdateBatchResult {
  const data = asRecord(payload);
  const rawItems = pick(data, "items");
  const items = Array.isArray(rawItems) ? rawItems.map(normalizeBatchResultItem) : [];
  return {
    items,
    successCount: toCount(pick(data, "successCount")),
    failedCount: toCount(pick(data, "failedCount")),
    hqSyncEnabled: Boolean(pick(data, "hqSyncEnabled")),
    hqSyncSubmittedCount: toCount(pick(data, "hqSyncSubmittedCount")),
  };
}

/**
 * 撤销接口（POST store-price-update-tasks/cancel）的响应：外层是 ApiResponse 信封，结果在 data 里。
 * 统一折成批量结果形状，复用分块执行器与逐条提示：撤销成功数 = successCount，被跳过的（已处理/不存在）= failedCount。
 */
export function normalizeCancelPriceUpdateTasksResult(payload: unknown): PriceUpdateBatchResult {
  const root = asRecord(payload);
  const inner = pick(root, "data");
  const data = inner && typeof inner === "object" ? asRecord(inner) : root;
  const rawItems = pick(data, "items");
  const items = Array.isArray(rawItems) ? rawItems.map(normalizeBatchResultItem) : [];
  return {
    items,
    successCount: toCount(pick(data, "cancelledCount")),
    failedCount: toCount(pick(data, "skippedCount")),
    hqSyncEnabled: false,
    hqSyncSubmittedCount: 0,
  };
}

/**
 * 按商品聚合接口（GET store-price-update-tasks/by-product）→ 指定商品当前有未完成任务的分店数。
 * 行里 stores[].state：LabelOnly / PriceUpdate = 还有未完成任务；Completed / Skipped 不算。
 * 关键字是模糊匹配，可能带回别的商品，所以必须按商品编码精确挑出那一行；找不到返回 null（不是 0）。
 */
export function normalizePendingStoreCount(payload: unknown, productCode: string): number | null {
  const root = asRecord(payload);
  const inner = pick(root, "data");
  const data = inner && typeof inner === "object" ? asRecord(inner) : root;
  const items = pick(data, "items");
  if (!Array.isArray(items)) return null;
  const wanted = productCode.trim().toLowerCase();
  const row = items
    .map(asRecord)
    .find((item) => (toText(pick(item, "productCode")) ?? "").trim().toLowerCase() === wanted);
  if (!row) return null;
  const stores = pick(row, "stores");
  if (!Array.isArray(stores)) return null;
  return stores.filter((store) => {
    const state = toText(pick(asRecord(store), "state"));
    return state === "LabelOnly" || state === "PriceUpdate";
  }).length;
}

function normalizeSyncTargetStore(payload: unknown): SyncTargetStore {
  const data = asRecord(payload);
  const storeCode = toText(pick(data, "storeCode")) ?? "";
  return {
    storeCode,
    storeName: toText(pick(data, "storeName")) ?? storeCode,
    hasRecord: Boolean(pick(data, "hasRecord")),
    retailPrice: toNumber(pick(data, "retailPrice")),
    discountRate: toNumber(pick(data, "discountRate")),
    isSpecialProduct: Boolean(pick(data, "isSpecialProduct")),
  };
}

export function normalizeSyncTargets(payload: unknown): SyncTargetsResult {
  const data = asRecord(payload);
  const rawTargets = pick(data, "targets");
  return {
    sourceStoreCode: toText(pick(data, "sourceStoreCode")) ?? "",
    productCode: toText(pick(data, "productCode")) ?? "",
    sourceRetailPrice: toNumber(pick(data, "sourceRetailPrice")),
    sourceDiscountRate: toNumber(pick(data, "sourceDiscountRate")),
    sourcePurchasePrice: toNumber(pick(data, "sourcePurchasePrice")),
    targets: Array.isArray(rawTargets)
      ? rawTargets.map(normalizeSyncTargetStore).filter((item) => item.storeCode)
      : [],
  };
}

export function normalizePriceNotificationPreview(payload: unknown): PriceNotificationPreview {
  const data = asRecord(payload);
  return {
    affectedStores: toCount(pick(data, "affectedStores")),
    skippedSpecialStores: toCount(pick(data, "skippedSpecialStores")),
  };
}

/** lookup 结果按商品码取建议折扣；未命中或 null 都表示「未设置」。 */
export function normalizeSuggestedDiscountLookup(payload: unknown): Map<string, number | null> {
  const result = new Map<string, number | null>();
  if (!Array.isArray(payload)) {
    return result;
  }
  for (const item of payload) {
    const data = asRecord(item);
    const productCode = toText(pick(data, "productCode"));
    if (productCode) {
      result.set(productCode, toNumber(pick(data, "suggestedDiscountRate")));
    }
  }
  return result;
}
