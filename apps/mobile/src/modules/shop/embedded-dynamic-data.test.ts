import assert from "node:assert/strict";
import { normalizeEmbeddedDynamicData } from "./embedded-dynamic-data";
import { buildShopDynamicDataQueryKey, resolvePageProductCodes } from "./product-query-key";
import type { StoreOrderProductItem } from "./types";

assert.equal(normalizeEmbeddedDynamicData(undefined), undefined, "空响应不视为已返回动态数据");
assert.equal(
  normalizeEmbeddedDynamicData({ items: [], total: 0 }),
  undefined,
  "旧后端不返回 dynamicData 时必须是 undefined，调用方才会回落到单独请求",
);
assert.deepEqual(normalizeEmbeddedDynamicData({ dynamicData: [] }), [], "新后端返回空数组时视为已返回");
assert.deepEqual(
  normalizeEmbeddedDynamicData({
    DynamicData: [
      { ProductCode: "P1", CartQuantity: 12, LastOrderDate: "2026-09-20", LastQuantity: 6, LastAllocQuantity: 6 },
      { productCode: "P2", cartQuantity: 0 },
      { cartQuantity: 3 },
      null,
    ],
  }),
  [
    { productCode: "P1", cartQuantity: 12, lastOrderDate: "2026-09-20", lastQuantity: 6, lastAllocQuantity: 6 },
    { productCode: "P2", cartQuantity: 0, lastOrderDate: undefined, lastQuantity: undefined, lastAllocQuantity: undefined },
  ],
  "兼容 PascalCase，丢弃缺商品编码或非对象的行",
);

const items = [
  { productCode: "P1" },
  { productCode: "" },
  { productCode: "P2" },
] as StoreOrderProductItem[];
assert.deepEqual(resolvePageProductCodes(items), ["P1", "P2"], "本页商品编码保持列表顺序并去掉空值");
assert.deepEqual(
  buildShopDynamicDataQueryKey("S001", resolvePageProductCodes(items)),
  ["shopDynamicData", "S001", ["P1", "P2"]],
  "写入与读取动态数据缓存必须使用同一个键",
);
assert.deepEqual(buildShopDynamicDataQueryKey(undefined, []), ["shopDynamicData", null, []], "未选门店时键里是 null");

console.log("embedded-dynamic-data.test.ts: ok");
