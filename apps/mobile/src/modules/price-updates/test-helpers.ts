import type { StorePriceUpdateTask } from "./types";

/** 测试用翻译替身：输出「键|参数 JSON」，断言文案键与参数而不依赖具体语言。 */
export function echoTranslate(key: string, params?: Record<string, unknown>) {
  return params ? `${key}|${JSON.stringify(params)}` : key;
}

export function createTask(patch: Partial<StorePriceUpdateTask> = {}): StorePriceUpdateTask {
  return {
    id: 1,
    storeCode: "1001",
    storeName: "Brisbane",
    productCode: "P001",
    storeRetailPriceUuid: null,
    productName: "Mug",
    itemNumber: "A-1",
    barcode: "9300000000017",
    productImage: null,
    status: "Pending",
    kind: "PriceUpdate",
    changedFields: ["retailPrice"],
    shelfRetailPrice: 10,
    shelfDiscountRate: 0,
    storeRetailPrice: 10,
    storeDiscountRate: 0,
    targetRetailPrice: 12,
    targetDiscountRate: null,
    initiatorName: "alice",
    initiatorSource: "WarehouseProducts",
    initiatorReference: null,
    initiatedAtUtc: "2026-09-19T00:00:00Z",
    changeCount: 1,
    firstInitiatedAtUtc: "2026-09-19T00:00:00Z",
    waitingDays: 0,
    isOverdue: false,
    overdueByDays: 0,
    priceAppliedBy: null,
    priceAppliedAtUtc: null,
    completionMode: null,
    completedBy: null,
    completedAtUtc: null,
    labelPrintCount: 0,
    hqSyncOperationId: null,
    hqSyncStatus: null,
    ...patch,
  };
}
