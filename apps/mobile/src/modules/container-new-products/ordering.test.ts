import assert from "node:assert/strict";
import { compareByArrivalThenProductNo } from "./ordering";
import type { ContainerNewProductItem } from "./types";

// date 是到仓库日期；传 null 模拟旧版后端（没有到仓库日期，只有预计到店起始日 storeDate）
const item = (productCode: string, hbProductNo: string | null, date: string | null = "2026-10-01", storeDate = "2026-10-20"): ContainerNewProductItem => ({
  productCode, hbProductNo, estimatedStoreArrivalDate: storeDate, estimatedStoreArrivalDateEnd: null, quantity: null,
  imageUrl: null, barcode: null, retailPrice: null, containerNumber: null, containerCode: "C", warehouseArrivalDate: date, basis: "estimated", isNewProduct: true,
});

const sorted = [
  item("p-a", null),
  item("p-b", "HB150-574"),
  item("p-late", "HB001-001", "2026-10-02"),
  item("p-c", "hb150-568"),
  item("p-d", "HB038-XM-017"),
  item("p-e", null),
].sort(compareByArrivalThenProductNo);

// 同一天内按货号（忽略大小写），无货号的排末尾并按 ProductCode 定序；到仓库日期仍是第一排序键
assert.deepEqual(sorted.map((x) => x.productCode), ["p-d", "p-c", "p-b", "p-a", "p-e", "p-late"]);

// 主排序键是到仓库日期而不是到店日期：两者故意顺序相反，且货号大小也相反
const byWarehouse = [
  item("p-store-first", "HB001", "2026-10-05", "2026-10-06"),
  item("p-warehouse-first", "HB999", "2026-10-02", "2026-10-09"),
].sort(compareByArrivalThenProductNo);
assert.deepEqual(byWarehouse.map((x) => x.productCode), ["p-warehouse-first", "p-store-first"]);

// 旧版后端没有到仓库日期：退回预计到店起始日排序，与原来的顺序一致
const legacy = [
  item("p-l-late", "HB001", null, "2026-10-09"),
  item("p-l-early-b", "HB200", null, "2026-10-03"),
  item("p-l-early-a", "HB100", null, "2026-10-03"),
].sort(compareByArrivalThenProductNo);
assert.deepEqual(legacy.map((x) => x.productCode), ["p-l-early-a", "p-l-early-b", "p-l-late"]);

console.log("container-new-products ordering tests passed");
