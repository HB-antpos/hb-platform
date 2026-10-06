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
    estimatedStoreArrivalDateEnd: "2026-10-06", basis: "actual", isNewProduct: true, ...overrides,
  };
}

const freshArrived = item({ productCode: "P-NEW", containerCode: "C-1", basis: "actual", isNewProduct: true });
const restockArrived = item({ productCode: "P-OLD", containerCode: "C-1", basis: "actual", isNewProduct: false });
const freshTransit = item({ productCode: "P-NEW2", containerCode: "C-2", containerNumber: null, basis: "estimated", isNewProduct: true, estimatedStoreArrivalDate: "2026-09-29" });

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

// 货柜汇总：按到店起始日排序，柜号缺失时用 ContainerCode，新/已有按商品去重
const options = summarizeContainers([freshArrived, restockArrived, item({ productCode: "p-old", containerCode: "c-1", isNewProduct: false }), freshTransit]);
assert.deepEqual(options.map((option) => [option.label, option.newKinds, option.existingKinds, option.basis]), [
  ["C-2", 1, 0, "estimated"],
  ["CSNU 7012345", 1, 1, "actual"],
]);

assert.deepEqual(pruneSelectedContainers(["C-1", "C-GONE"], options), ["C-1"]);
assert.equal(matchesContainerSearch(options[1], "csnu70"), true);
assert.equal(matchesContainerSearch(options[1], "7012 345"), true);
assert.equal(matchesContainerSearch(options[1], "TGHU"), false);

console.log("container-new-products filters tests passed");
