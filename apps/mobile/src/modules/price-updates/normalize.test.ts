import assert from "node:assert/strict";
import {
  normalizePriceNotificationPreview,
  normalizePriceUpdateBatchResult,
  normalizePriceUpdatePendingCount,
  normalizePriceUpdateSummary,
  normalizePriceUpdateTask,
  normalizePriceUpdateTaskPage,
  normalizeSuggestedDiscountLookup,
  normalizeSyncTargets,
} from "./normalize";

const camel = normalizePriceUpdateTask({
  id: 7,
  storeCode: "1001",
  productCode: "P001",
  productName: " Mug ",
  status: "Pending",
  kind: "PriceUpdate",
  changedFields: ["retailPrice", "discountRate", "retailPrice", "unknown"],
  storeRetailPrice: 10,
  storeDiscountRate: 0.1,
  targetRetailPrice: "12.50",
  targetDiscountRate: null,
  initiatorName: "alice",
  initiatorSource: "StoreSync",
  initiatorReference: "1002",
  initiatedAtUtc: "2026-09-19T00:00:00Z",
  changeCount: 3,
  labelPrintCount: 0,
  hqSyncOperationId: "op-1",
  hqSyncStatus: "Blocked",
});
assert.equal(camel.id, 7);
assert.equal(camel.productName, "Mug", "文本字段应去除首尾空白");
assert.deepEqual(camel.changedFields, ["retailPrice", "discountRate"], "变更字段去重并丢弃未知值");
assert.equal(camel.targetRetailPrice, 12.5, "字符串金额应转为数字");
assert.equal(camel.targetDiscountRate, null, "目标折扣 null 表示不比较，必须保留为 null 而不是 0");
assert.equal(camel.hqSyncStatus, "blocked", "总部同步状态大小写不敏感");
assert.equal(camel.completionMode, null);
assert.equal(camel.changeCount, 3);

const pascal = normalizePriceUpdateTask({
  Id: 8,
  StoreCode: "1001",
  ProductCode: "P002",
  Status: "Completed",
  Kind: "LabelOnly",
  ChangedFields: ["DiscountRate"],
  ShelfRetailPrice: 9,
  StoreRetailPrice: 10,
  CompletionMode: "Printed",
  CompletedBy: "bob",
  LabelPrintCount: 2,
  InitiatorName: "System",
  InitiatorSource: "BatchUpdate",
  InitiatedAtUtc: "2026-09-18T00:00:00Z",
});
assert.equal(pascal.id, 8, "兼容 PascalCase 响应");
assert.equal(pascal.status, "Completed");
assert.equal(pascal.kind, "LabelOnly");
assert.deepEqual(pascal.changedFields, ["discountRate"]);
assert.equal(pascal.completionMode, "Printed");
assert.equal(pascal.labelPrintCount, 2);
assert.equal(pascal.changeCount, 1, "缺省变更次数按 1 处理");

const unknown = normalizePriceUpdateTask({ id: 9, kind: "Mystery", status: "Weird", completionMode: "Nope", hqSyncStatus: "??" });
assert.equal(unknown.kind, "LabelOnly", "未知类型按待换标签收紧，不会误触发改价");
assert.equal(unknown.status, "Pending");
assert.equal(unknown.completionMode, null);
assert.equal(unknown.hqSyncStatus, null);

const page = normalizePriceUpdateTaskPage({
  items: [{ id: 1, kind: "PriceUpdate" }, { id: 0 }, null],
  total: 41,
  page: 2,
  pageSize: 30,
  pendingCount: 41,
  pendingPriceUpdateCount: 30,
  pendingLabelOnlyCount: 11,
  completedCount: 5,
  hqSyncEnabled: true,
});
assert.equal(page.items.length, 1, "没有有效 id 的条目必须丢弃，避免列表 key 冲突");
assert.deepEqual(
  [page.total, page.page, page.pageSize, page.pendingCount, page.pendingPriceUpdateCount, page.pendingLabelOnlyCount, page.completedCount],
  [41, 2, 30, 41, 30, 11, 5]
);
assert.equal(page.hqSyncEnabled, true);

const emptyPage = normalizePriceUpdateTaskPage(null);
assert.deepEqual(emptyPage.items, []);
assert.equal(emptyPage.page, 1);
assert.equal(emptyPage.hqSyncEnabled, false);

assert.equal(normalizePriceUpdatePendingCount({ pendingCount: 12 }), 12);
assert.equal(normalizePriceUpdatePendingCount({ PendingCount: "3" }), 3);
assert.equal(normalizePriceUpdatePendingCount({ pendingCount: -4 }), 0);
assert.equal(normalizePriceUpdatePendingCount(undefined), 0);

const batch = normalizePriceUpdateBatchResult({
  items: [
    { taskId: 1, success: true, code: "OK", task: { id: 1, kind: "LabelOnly", status: "Pending", storeRetailPrice: 12 } },
    { taskId: 2, success: false, code: "target_changed", message: "changed" },
    { taskId: 3, success: false },
  ],
  successCount: 1,
  failedCount: 2,
  hqSyncEnabled: true,
  hqSyncSubmittedCount: 1,
});
assert.equal(batch.items[0].code, "ok");
assert.equal(batch.items[0].task?.kind, "LabelOnly");
assert.equal(batch.items[1].code, "target_changed");
assert.equal(batch.items[1].message, "changed");
assert.equal(batch.items[2].code, "failed", "缺失 code 按失败处理");
assert.equal(batch.items[2].task, null);
assert.deepEqual([batch.successCount, batch.failedCount, batch.hqSyncSubmittedCount], [1, 2, 1]);

const targets = normalizeSyncTargets({
  sourceStoreCode: "1001",
  productCode: "P001",
  sourceRetailPrice: 12,
  sourceDiscountRate: 0.2,
  sourcePurchasePrice: 5,
  targets: [
    { storeCode: "1002", storeName: "Gold Coast", hasRecord: true, retailPrice: 10, discountRate: 0, isSpecialProduct: false },
    { storeCode: "1003", hasRecord: false },
    { storeName: "missing code" },
  ],
});
assert.equal(targets.targets.length, 2, "没有分店代码的目标必须丢弃");
assert.equal(targets.targets[1].storeName, "1003", "缺少分店名时回退到分店代码");
assert.equal(targets.targets[1].retailPrice, null);
assert.equal(targets.sourceDiscountRate, 0.2);

assert.deepEqual(normalizePriceNotificationPreview({ affectedStores: 6, skippedSpecialStores: 1 }), {
  affectedStores: 6,
  skippedSpecialStores: 1,
});
assert.deepEqual(normalizePriceNotificationPreview(null), { affectedStores: 0, skippedSpecialStores: 0 });

const lookup = normalizeSuggestedDiscountLookup([
  { productCode: "P001", suggestedDiscountRate: 0 },
  { productCode: "P002", suggestedDiscountRate: null },
  { ProductCode: "P003", SuggestedDiscountRate: 0.25 },
]);
assert.equal(lookup.get("P001"), 0, "0 = 明确无折扣，不能被当成未设置");
assert.equal(lookup.get("P002"), null);
assert.equal(lookup.get("P003"), 0.25);
assert.equal(normalizeSuggestedDiscountLookup({}).size, 0);

// ---- 超期字段：从首次发起起算，旧后端没有这些字段时按「未超期」处理（不会误锁报表） ----
const overdueTask = normalizePriceUpdateTask({
  id: 9,
  storeCode: "1001",
  productCode: "P009",
  initiatedAtUtc: "2026-10-14T00:00:00Z",
  firstInitiatedAtUtc: "2026-10-01T00:00:00Z",
  waitingDays: 14,
  isOverdue: true,
  overdueByDays: 7,
});
assert.equal(overdueTask.firstInitiatedAtUtc, "2026-10-01T00:00:00Z", "首次发起时间要与（会被刷新的）发起时间区分开");
assert.equal(overdueTask.waitingDays, 14);
assert.equal(overdueTask.isOverdue, true);
assert.equal(overdueTask.overdueByDays, 7);

const legacyTask = normalizePriceUpdateTask({
  id: 10,
  storeCode: "1001",
  productCode: "P010",
  initiatedAtUtc: "2026-10-14T00:00:00Z",
});
assert.equal(legacyTask.firstInitiatedAtUtc, "2026-10-14T00:00:00Z", "旧后端没有首次发起时间：回退到发起时间");
assert.equal(legacyTask.isOverdue, false, "旧后端没有超期字段：一律按未超期，绝不能因缺字段锁住报表");
assert.equal(legacyTask.waitingDays, 0);
assert.equal(legacyTask.overdueByDays, 0);
assert.equal(
  normalizePriceUpdateTask({ id: 11, storeCode: "1001", productCode: "P011", IsOverdue: true, OverdueByDays: 2 }).isOverdue,
  true,
  "兼容 PascalCase 响应"
);
assert.equal(
  normalizePriceUpdateTask({ id: 12, storeCode: "1001", productCode: "P012", isOverdue: "true" }).isOverdue,
  false,
  "只认布尔 true：字符串 \"true\" 不算超期，避免脏数据误锁"
);

const overduePage = normalizePriceUpdateTaskPage({
  items: [{ id: 1, storeCode: "1001", productCode: "P001" }],
  total: 1,
  pendingCount: 1,
  pendingOverdueCount: 1,
  overdueDays: 7,
});
assert.equal(overduePage.pendingOverdueCount, 1);
assert.equal(overduePage.overdueDays, 7);
assert.equal(normalizePriceUpdateTaskPage({}).pendingOverdueCount, 0);
assert.equal(normalizePriceUpdateTaskPage({}).overdueDays, 0, "没返回时限时为 0，由调用方回退到默认 7 天");

// ---- 按分店汇总 ----
const summary = normalizePriceUpdateSummary({
  overdueDays: 7,
  stores: [
    { storeCode: "1001", storeName: " Brisbane ", pendingPriceUpdateCount: 3, pendingLabelOnlyCount: 2, pendingCount: 99, overdueCount: 1 },
    { StoreCode: "1002", PendingPriceUpdateCount: 0, PendingLabelOnlyCount: 4, OverdueCount: 0 },
    { storeName: "没有分店代码的行会被丢弃" },
  ],
});
assert.equal(summary.overdueDays, 7);
assert.deepEqual(
  summary.stores.map((row) => row.storeCode),
  ["1001", "1002"],
  "没有分店代码的行必须丢弃，避免后续按分店匹配出现空键"
);
assert.equal(summary.stores[0].storeName, "Brisbane");
assert.equal(summary.stores[0].pendingCount, 5, "合计以两类之和为准，不信后端的合计字段，避免角标和列表对不上");
assert.equal(summary.stores[0].overdueCount, 1);
assert.equal(summary.stores[1].storeName, "1002", "没有分店名时回退为分店代码");
assert.equal(summary.stores[1].pendingCount, 4);
assert.deepEqual(normalizePriceUpdateSummary(null), { stores: [], overdueDays: 0 });

console.log("price-updates/normalize.test.ts: ok");
