import assert from "node:assert/strict";
import { compareByArrivalThenProductNo } from "./ordering";
import type { ContainerNewProductItem } from "./types";

const item = (productCode: string, hbProductNo: string | null, date = "2026-10-01"): ContainerNewProductItem => ({
  productCode, hbProductNo, estimatedStoreArrivalDate: date, estimatedStoreArrivalDateEnd: null, quantity: null,
  imageUrl: null, barcode: null, retailPrice: null, containerNumber: null, containerCode: "C", basis: "estimated", isNewProduct: true,
});

const sorted = [
  item("p-a", null),
  item("p-b", "HB150-574"),
  item("p-late", "HB001-001", "2026-10-02"),
  item("p-c", "hb150-568"),
  item("p-d", "HB038-XM-017"),
  item("p-e", null),
].sort(compareByArrivalThenProductNo);

// 同一天内按货号（忽略大小写），无货号的排末尾并按 ProductCode 定序；日期仍是第一排序键
assert.deepEqual(sorted.map((x) => x.productCode), ["p-d", "p-c", "p-b", "p-a", "p-e", "p-late"]);

console.log("container-new-products ordering tests passed");
