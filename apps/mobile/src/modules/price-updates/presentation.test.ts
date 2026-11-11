import assert from "node:assert/strict";
import {
  applyDiscount,
  buildPriceComparison,
  formatDiscountLabel,
  formatMoney,
  formatRelativeTime,
  getRelativeDayKind,
  groupCompletedTasksByDay,
  mergeUniqueTasks,
  resolveChangedFieldsKind,
  resolveCompletedStatusKey,
  resolveHqSyncChipKey,
  resolveInitiatorName,
  resolveInitiatorSourceLabel,
  resolveLabelPrintPrice,
  resolveTaskStoreLabel,
  summarizeSelection,
} from "./presentation";
import { createTask, echoTranslate as t } from "./test-helpers";

// 卡片分店名：有名称用名称（去掉首尾空白），没有就回退分店代码
assert.equal(resolveTaskStoreLabel({ storeName: " Rockdale ", storeCode: "1014" }), "Rockdale");
assert.equal(resolveTaskStoreLabel({ storeName: null, storeCode: "1014" }), "1014");
assert.equal(resolveTaskStoreLabel({ storeName: "  ", storeCode: "1014" }), "1014");

// 来源映射
assert.equal(resolveInitiatorSourceLabel("WarehouseProducts", null, t), "sources.warehouseProducts");
assert.equal(resolveInitiatorSourceLabel("MobileWarehouse", null, t), "sources.mobileWarehouse");
assert.equal(resolveInitiatorSourceLabel("BatchUpdate", null, t), "sources.batchUpdate");
assert.equal(resolveInitiatorSourceLabel("WarehouseAutoSync", null, t), "sources.warehouseAutoSync");
assert.equal(resolveInitiatorSourceLabel("LocalSupplierInvoice", null, t), "sources.localSupplierInvoice");
assert.equal(resolveInitiatorSourceLabel("DomesticImport", null, t), "sources.domesticImport");
assert.equal(
  resolveInitiatorSourceLabel("StoreOrderImportPriceVariance", null, t),
  "sources.storeOrderImportPriceVariance"
);
assert.equal(resolveInitiatorSourceLabel("StoreSync", " 1001 ", t), 'sources.storeSyncFrom|{"store":"1001"}');
assert.equal(resolveInitiatorSourceLabel("StoreSync", null, t), "sources.storeSync");
assert.equal(resolveInitiatorSourceLabel("DataSyncProducts", null, t), "sources.dataSync", "DataSync 前缀统一映射");
assert.equal(resolveInitiatorSourceLabel("SomethingNew", null, t), "sources.other", "未知来源归为其它入口，不展示英文代码");
assert.equal(resolveInitiatorSourceLabel("ContainerDetail", null, t), "sources.container", "货柜各变体按前缀归类");
assert.equal(resolveInitiatorSourceLabel("YiwuContainerBatch", null, t), "sources.container");
assert.equal(resolveInitiatorSourceLabel("DomesticProductBatch", null, t), "sources.domesticProduct");
assert.equal(resolveInitiatorSourceLabel("LocalSupplierInvoiceHqProductSync", null, t), "sources.localSupplierInvoice");
assert.equal(resolveInitiatorSourceLabel("StoreOrderProductStatus", null, t), "sources.storeOrder");
assert.equal(resolveInitiatorSourceLabel("ProductLegacyApi", null, t), "sources.legacyApi");
assert.equal(resolveInitiatorSourceLabel("", null, t), "");
assert.equal(resolveInitiatorName("System", t), "initiator.system");
assert.equal(resolveInitiatorName("", t), "initiator.system");
assert.equal(resolveInitiatorName(" alice ", t), "alice");

// 相对时间：用本地时间构造，保证任何时区下结果一致
const now = new Date(2026, 8, 19, 15, 0, 0);
const todayMorning = new Date(2026, 8, 19, 9, 42, 0);
const yesterdayEvening = new Date(2026, 8, 18, 17, 5, 0);
const lateLastNight = new Date(2026, 8, 18, 23, 59, 0);
const earlier = new Date(2026, 2, 8, 8, 3, 0);
const lastYear = new Date(2025, 11, 31, 8, 3, 0);
assert.equal(formatRelativeTime(todayMorning.toISOString(), now, t), 'time.today|{"time":"09:42"}');
assert.equal(formatRelativeTime(yesterdayEvening.toISOString(), now, t), 'time.yesterday|{"time":"17:05"}');
assert.equal(
  getRelativeDayKind(lateLastNight, new Date(2026, 8, 19, 0, 1, 0)),
  "yesterday",
  "相差不足 24 小时但跨了日历日，仍是昨天"
);
assert.equal(formatRelativeTime(earlier.toISOString(), now, t), "03-08 08:03");
assert.equal(formatRelativeTime(lastYear.toISOString(), now, t), "2025-12-31 08:03", "跨年带年份");
assert.equal(formatRelativeTime("not a date", now, t), "");
assert.equal(formatRelativeTime(null, now, t), "");

// 已完成按日分组
const groups = groupCompletedTasksByDay(
  [
    createTask({ id: 1, completedAtUtc: todayMorning.toISOString() }),
    createTask({ id: 2, completedAtUtc: new Date(2026, 8, 19, 8, 0, 0).toISOString() }),
    createTask({ id: 3, completedAtUtc: yesterdayEvening.toISOString() }),
    createTask({ id: 4, completedAtUtc: earlier.toISOString() }),
    createTask({ id: 5, completedAtUtc: null, initiatedAtUtc: earlier.toISOString() }),
  ],
  now
);
assert.deepEqual(
  groups.map((group) => [group.kind, group.dateLabel, group.items.map((item) => item.id)]),
  [
    ["today", "09-19", [1, 2]],
    ["yesterday", "09-18", [3]],
    ["date", "03-08", [4, 5]],
  ],
  "同一天归为一组；缺少完成时间时退回发起时间"
);
assert.deepEqual(groupCompletedTasksByDay([], now), []);

// 金额与折扣
assert.equal(formatMoney(12.5), "$12.50");
assert.equal(formatMoney(null), "--");
assert.equal(applyDiscount(10, 0.2), 8);
assert.equal(applyDiscount(19.99, 0.15), 16.99);
assert.equal(applyDiscount(10, null), 10);
assert.equal(applyDiscount(null, 0.2), null);
assert.equal(formatDiscountLabel(0.2, t), 'discount.off|{"percent":"20"}');
assert.equal(formatDiscountLabel(0.125, t), 'discount.off|{"percent":"12.5"}');
assert.equal(formatDiscountLabel(0, t), "discount.none");
assert.equal(formatDiscountLabel(null, t), "discount.none");

// 价格对比与涨跌
const up = buildPriceComparison(createTask({ storeRetailPrice: 10, targetRetailPrice: 12.5 }));
assert.deepEqual(
  [up.fromPrice, up.toPrice, up.delta, up.direction, up.priceChanged, up.discountChanged],
  [10, 12.5, 2.5, "up", true, false]
);
const down = buildPriceComparison(createTask({ storeRetailPrice: 10, targetRetailPrice: 8.01 }));
assert.deepEqual([down.delta, down.direction], [-1.99, "down"]);

const keepDiscount = buildPriceComparison(
  createTask({ storeRetailPrice: 10, storeDiscountRate: 0.1, targetRetailPrice: 12, targetDiscountRate: null })
);
assert.equal(keepDiscount.toDiscountRate, 0.1, "目标折扣 null = 不比较，沿用本店折扣");
assert.equal(keepDiscount.discountChanged, false);
assert.equal(keepDiscount.delta, 2, "折扣未变时涨跌额按零售价计算");

const discountOnly = buildPriceComparison(
  createTask({ storeRetailPrice: 10, storeDiscountRate: 0, targetRetailPrice: 10, targetDiscountRate: 0.2 })
);
assert.deepEqual(
  [discountOnly.priceChanged, discountOnly.discountChanged, discountOnly.toFinalPrice, discountOnly.delta, discountOnly.direction],
  [false, true, 8, -2, "down"],
  "仅折扣变化时涨跌额按折后价计算"
);

const clearDiscount = buildPriceComparison(
  createTask({ storeRetailPrice: 10, storeDiscountRate: 0.2, targetRetailPrice: 10, targetDiscountRate: 0 })
);
assert.deepEqual([clearDiscount.toDiscountRate, clearDiscount.discountChanged, clearDiscount.direction], [0, true, "up"], "目标折扣 0 = 明确无折扣");

const labelOnly = buildPriceComparison(
  createTask({ kind: "LabelOnly", shelfRetailPrice: 9, shelfDiscountRate: 0, storeRetailPrice: 11, targetRetailPrice: 99 })
);
assert.deepEqual([labelOnly.fromPrice, labelOnly.toPrice, labelOnly.delta], [9, 11, 2], "待换标签对比 标签旧值 → 本店现值，忽略仓库目标");

const same = buildPriceComparison(createTask({ storeRetailPrice: 10, targetRetailPrice: 10 }));
assert.deepEqual([same.delta, same.direction], [0, "same"]);
const missing = buildPriceComparison(createTask({ storeRetailPrice: null, targetRetailPrice: 10 }));
assert.deepEqual([missing.delta, missing.direction], [null, "same"]);

assert.equal(resolveChangedFieldsKind(createTask({ changedFields: ["retailPrice"] })), "retailPrice");
assert.equal(resolveChangedFieldsKind(createTask({ changedFields: ["discountRate"] })), "discountRate");
assert.equal(resolveChangedFieldsKind(createTask({ changedFields: ["retailPrice", "discountRate"] })), "both");
assert.equal(
  resolveChangedFieldsKind(
    createTask({ changedFields: [], storeRetailPrice: 10, targetRetailPrice: 10, storeDiscountRate: 0, targetDiscountRate: 0.3 })
  ),
  "discountRate",
  "后端未给 changedFields 时按实际对比推断"
);

// 打印价格 = 更新后的价格
assert.deepEqual(
  resolveLabelPrintPrice(createTask({ kind: "LabelOnly", storeRetailPrice: 12, storeDiscountRate: 0.2, targetRetailPrice: 99 })),
  { retailPrice: 12, discountRate: 0.2 }
);
assert.deepEqual(
  resolveLabelPrintPrice(createTask({ kind: "PriceUpdate", storeRetailPrice: 10, storeDiscountRate: 0.1, targetRetailPrice: 12, targetDiscountRate: null })),
  { retailPrice: 12, discountRate: 0.1 }
);

// 已完成状态
assert.equal(resolveCompletedStatusKey(createTask({ completionMode: "Printed" })), "printed");
assert.equal(resolveCompletedStatusKey(createTask({ completionMode: "MarkedReplaced" })), "markedReplaced");
assert.equal(resolveCompletedStatusKey(createTask({ completionMode: "KeptStorePrice" })), "keptStorePrice");
assert.equal(resolveCompletedStatusKey(createTask({ completionMode: "PriceAligned" })), "priceAligned");
assert.equal(resolveCompletedStatusKey(createTask({ completionMode: null })), "updated");
assert.equal(resolveHqSyncChipKey(createTask({ hqSyncStatus: "succeeded" }), true), "hqSynced");
assert.equal(resolveHqSyncChipKey(createTask({ hqSyncStatus: "blocked" }), true), "hqSyncFailed");
assert.equal(resolveHqSyncChipKey(createTask({ hqSyncStatus: "retrying" }), true), "hqSyncing");
assert.equal(resolveHqSyncChipKey(createTask({ hqSyncStatus: "superseded" }), true), null);
assert.equal(resolveHqSyncChipKey(createTask({ hqSyncStatus: "blocked" }), false), null, "未启用总部同步时隐藏所有相关状态");
assert.equal(resolveHqSyncChipKey(createTask({ hqSyncStatus: null }), true), null);

// 多选汇总与分页去重
const selectionTasks = [
  createTask({ id: 1, kind: "PriceUpdate" }),
  createTask({ id: 2, kind: "LabelOnly" }),
  createTask({ id: 3, kind: "PriceUpdate" }),
];
assert.deepEqual(summarizeSelection(selectionTasks, new Set([1, 2])), { applyCount: 1, printCount: 2, labelOnlyCount: 1 });
assert.deepEqual(summarizeSelection(selectionTasks, new Set([99])), { applyCount: 0, printCount: 0, labelOnlyCount: 0 }, "已不在列表里的已选 id 不计数");
assert.deepEqual(
  mergeUniqueTasks([{ items: [createTask({ id: 1 }), createTask({ id: 2 })] }, { items: [createTask({ id: 2 }), createTask({ id: 3 })] }]).map((item) => item.id),
  [1, 2, 3]
);

console.log("price-updates/presentation.test.ts: ok");
