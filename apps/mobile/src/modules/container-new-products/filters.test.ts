import assert from "node:assert/strict";
import {
  countActiveFilters,
  DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS,
  matchesContainerSearch,
  matchesFilters,
  pruneSelectedContainers,
  summarizeContainers,
} from "./filters";
import type { ContainerNewProductItem } from "./types";

function item(overrides: Partial<ContainerNewProductItem>): ContainerNewProductItem {
  return {
    productCode: "P-1", hbProductNo: null, quantity: null, imageUrl: null, barcode: null, retailPrice: null,
    containerNumber: "CSNU 7012345", containerCode: "C-1", estimatedStoreArrivalDate: "2026-10-01",
    estimatedStoreArrivalDateEnd: "2026-10-06", warehouseArrivalDate: "2026-09-28", basis: "actual", isNewProduct: true, isCreated: true, ...overrides,
  };
}

const freshArrived = item({ productCode: "P-NEW", containerCode: "C-1", basis: "actual", isNewProduct: true });
const restockArrived = item({ productCode: "P-OLD", containerCode: "C-1", basis: "actual", isNewProduct: false });
const freshTransit = item({ productCode: "P-NEW2", containerCode: "C-2", containerNumber: null, basis: "estimated", isNewProduct: true, estimatedStoreArrivalDate: "2026-09-29", warehouseArrivalDate: "2026-09-24" });

// 默认只显示新商品
assert.equal(matchesFilters(freshArrived, DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS), true);
assert.equal(matchesFilters(restockArrived, DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS), false);
assert.equal(matchesFilters(restockArrived, { ...DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS, productType: "existing" }), true);
assert.equal(matchesFilters(restockArrived, { ...DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS, productType: "all" }), true);

// 到仓状态 = basis
assert.equal(matchesFilters(freshTransit, { ...DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS, warehouse: "arrived" }), false);
assert.equal(matchesFilters(freshTransit, { ...DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS, warehouse: "transit" }), true);

// 货柜多选：只显示选中的货柜，空数组为全部；大小写不敏感
assert.equal(matchesFilters(freshTransit, { ...DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS, containerCodes: ["c-1"] }), false);
assert.equal(matchesFilters(freshArrived, { ...DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS, containerCodes: ["c-1", "C-3"] }), true);

// 分面计数：忽略自身这一类筛选
assert.equal(matchesFilters(restockArrived, DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS, "productType"), true);

assert.equal(countActiveFilters(DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS), 0);
assert.equal(countActiveFilters({ productType: "all", warehouse: "transit", containerCodes: ["C-1"] }), 3);

// 货柜汇总：按到仓库日期排序，柜号缺失时用 ContainerCode，新/已有按商品去重
const options = summarizeContainers([freshArrived, restockArrived, item({ productCode: "p-old", containerCode: "c-1", isNewProduct: false }), freshTransit]);
assert.deepEqual(options.map((option) => [option.label, option.newKinds, option.existingKinds, option.basis]), [
  ["C-2", 1, 0, "estimated"],
  ["CSNU 7012345", 1, 1, "actual"],
]);

// 仓库日期随商品行带到货柜选项（已到仓 = 实际日期，在途 = 预计日期，由 basis 区分）
assert.deepEqual(options.map((option) => [option.label, option.warehouseArrivalDate]), [
  ["C-2", "2026-09-24"],
  ["CSNU 7012345", "2026-09-28"],
]);
// 排序主键是到仓库日期：故意让它与到店日期顺序相反（YYYU 到仓更早、到店更晚），仍应按到仓库日期排在前面
const reversed = summarizeContainers([
  item({ productCode: "P-X", containerCode: "C-X", containerNumber: "XXXU 1", estimatedStoreArrivalDate: "2026-10-02", warehouseArrivalDate: "2026-09-30" }),
  item({ productCode: "P-Y", containerCode: "C-Y", containerNumber: "YYYU 2", estimatedStoreArrivalDate: "2026-10-05", warehouseArrivalDate: "2026-09-01" }),
]);
assert.deepEqual(reversed.map((option) => option.label), ["YYYU 2", "XXXU 1"]);
// 到仓库日期相同再按柜号（忽略大小写）
const sameDay = summarizeContainers([
  item({ productCode: "P-B", containerCode: "C-B", containerNumber: "bbbu 2", warehouseArrivalDate: "2026-09-30" }),
  item({ productCode: "P-A", containerCode: "C-A", containerNumber: "AAAU 1", warehouseArrivalDate: "2026-09-30" }),
]);
assert.deepEqual(sameDay.map((option) => option.label), ["AAAU 1", "bbbu 2"]);
// 旧版后端不返回仓库日期：选项里为 null，退回按预计到店起始日排序，其余汇总不受影响
const legacy = summarizeContainers([
  item({ productCode: "P-L2", containerCode: "C-L2", containerNumber: "LATE 2", estimatedStoreArrivalDate: "2026-10-09", warehouseArrivalDate: null }),
  item({ productCode: "P-L1", containerCode: "C-L1", containerNumber: "EARLY 1", estimatedStoreArrivalDate: "2026-10-03", warehouseArrivalDate: null }),
]);
assert.deepEqual(legacy.map((option) => [option.label, option.warehouseArrivalDate, option.newKinds]), [["EARLY 1", null, 1], ["LATE 2", null, 1]]);

assert.deepEqual(pruneSelectedContainers(["C-1", "C-GONE"], options), ["C-1"]);
assert.equal(matchesContainerSearch(options[1], "csnu70"), true);
assert.equal(matchesContainerSearch(options[1], "7012 345"), true);
assert.equal(matchesContainerSearch(options[1], "TGHU"), false);

console.log("container-new-products filters tests passed");
