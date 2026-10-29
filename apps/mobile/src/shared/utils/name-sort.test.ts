import assert from "node:assert/strict";

import { compareByName, sortByName, sortStoresByName, sortSuppliersByName } from "./name-sort";

const codes = <T extends { storeCode: string }>(items: T[]) => items.map((item) => item.storeCode);

// 分店：按名字排，不是按编号；忽略大小写
assert.deepEqual(
  codes(sortStoresByName([
    { storeCode: "1004", storeName: "Campbelltown" },
    { storeCode: "1031", storeName: "Southport Park" },
    { storeCode: "1024", storeName: "bankstown" },
    { storeCode: "1014", storeName: "Kawana" },
    { storeCode: "1033", storeName: "Top Ryde" },
  ])),
  ["1024", "1004", "1014", "1031", "1033"],
);

// 名字为空退回编号；数字按数值比
assert.deepEqual(
  codes(sortStoresByName([
    { storeCode: "1010", storeName: "" },
    { storeCode: "1002", storeName: null },
    { storeCode: "Z9", storeName: "Store 10" },
    { storeCode: "Z2", storeName: "Store 2" },
  ])),
  ["1002", "1010", "Z2", "Z9"],
);

// 同名按编号；完全相同保持原顺序（稳定）；不修改入参
const input = [
  { storeCode: "B", storeName: "Same", tag: 1 },
  { storeCode: "A", storeName: "Same", tag: 2 },
  { storeCode: "A", storeName: "Same", tag: 3 },
];
const snapshot = JSON.stringify(input);
assert.deepEqual(sortStoresByName(input).map((item) => item.tag), [2, 3, 1]);
assert.equal(JSON.stringify(input), snapshot);

// 供应商：按名字，不按编号；中文界面用拼音排序
assert.deepEqual(
  sortSuppliersByName([
    { supplierCode: "250", supplierName: "Zebra Cards" },
    { supplierCode: "200", supplierName: "Hotbargain" },
    { supplierCode: "248", supplierName: "A PLUS" },
  ]).map((item) => item.supplierCode),
  ["248", "200", "250"],
);
assert.deepEqual(
  sortSuppliersByName([
    { supplierCode: "1", supplierName: "中山礼品" },
    { supplierCode: "2", supplierName: "阿里贸易" },
    { supplierCode: "3", supplierName: "宝华" },
  ], "zh").map((item) => item.supplierCode),
  ["2", "3", "1"],
);

// 通用：{code, name} 形状
assert.deepEqual(
  sortByName([{ code: "x", name: "b" }, { code: "y", name: "a" }], (item) => item).map((item) => item.code),
  ["y", "x"],
);
assert.equal(compareByName({ name: "a" }, { name: "A" }), 0);
console.log("name-sort tests passed");
