import assert from "node:assert/strict";
import test from "node:test";
import { buildStoreNameLookup, formatDeviceStoreLabel } from "./store-label";

const lookup = buildStoreNameLookup([
  { storeCode: "1023", storeName: "Oran Park" },
  { storeCode: " bri ", storeName: "Brisbane" },
  { storeCode: "1023", storeName: "Duplicate" },
  { storeCode: "", storeName: "No code" },
]);

test("后端回填了名称时优先用后端名称，并附上代码", () => {
  assert.equal(formatDeviceStoreLabel({ storeCode: "1023", storeName: "Oran Park West" }, lookup, "No store"), "Oran Park West · 1023");
});

test("后端没给名称时用本地分店列表兜底，代码大小写与空格不影响匹配", () => {
  assert.equal(formatDeviceStoreLabel({ storeCode: "1023" }, lookup, "No store"), "Oran Park · 1023");
  assert.equal(formatDeviceStoreLabel({ storeCode: "BRI", storeName: "  " }, lookup, "No store"), "Brisbane · BRI");
});

test("查不到名称只显示代码，没有代码显示占位文字", () => {
  assert.equal(formatDeviceStoreLabel({ storeCode: "9999" }, lookup, "No store"), "9999");
  assert.equal(formatDeviceStoreLabel({}, lookup, "No store"), "No store");
  assert.equal(formatDeviceStoreLabel({ storeName: "Warehouse" }, lookup, "No store"), "Warehouse");
});

test("名称与代码相同时只显示一次", () => {
  assert.equal(formatDeviceStoreLabel({ storeCode: "HQ", storeName: "hq" }, lookup, "No store"), "hq");
});
