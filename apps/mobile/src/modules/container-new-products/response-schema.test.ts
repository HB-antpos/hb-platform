import assert from "node:assert/strict";
import { containerNewProductsResponseSchema } from "./response-schema";

function row(overrides: Record<string, unknown> = {}) {
  return {
    productCode: "P-1", containerCode: "C-1", estimatedStoreArrivalDate: "2026-10-05",
    estimatedStoreArrivalDateEnd: "2026-10-09", basis: "actual", ...overrides,
  };
}

function parse(items: unknown[]) {
  const result = containerNewProductsResponseSchema.safeParse({ storeCode: "S-1", items });
  assert.equal(result.success, true);
  return result.success ? result.data.items : [];
}

// 新版后端：返回 DateOnly 序列化后的 YYYY-MM-DD，按原样保留
assert.equal(parse([row({ warehouseArrivalDate: "2026-10-02" })])[0].warehouseArrivalDate, "2026-10-02");
// 日期若带时间部分只取前 10 位
assert.equal(parse([row({ warehouseArrivalDate: "2026-10-02T00:00:00" })])[0].warehouseArrivalDate, "2026-10-02");
// 在途货柜：basis = estimated，日期是预计到岸日期，同样解析
const estimated = parse([row({ basis: "estimated", warehouseArrivalDate: "2026-10-03" })])[0];
assert.equal(estimated.basis, "estimated");
assert.equal(estimated.warehouseArrivalDate, "2026-10-03");

// 旧版后端不返回该字段 / 返回 null / 格式不对：整份响应仍能解析，仓库日期为 null（页面不显示该行）
assert.equal(parse([row()])[0].warehouseArrivalDate, null);
assert.equal(parse([row({ warehouseArrivalDate: null })])[0].warehouseArrivalDate, null);
assert.equal(parse([row({ warehouseArrivalDate: "" })])[0].warehouseArrivalDate, null);
assert.equal(parse([row({ warehouseArrivalDate: "garbage" })])[0].warehouseArrivalDate, null);

// 新增字段不影响原有字段的解析
const legacy = parse([row()])[0];
assert.equal(legacy.estimatedStoreArrivalDate, "2026-10-05");
assert.equal(legacy.estimatedStoreArrivalDateEnd, "2026-10-09");
assert.equal(legacy.isNewProduct, true);

// 仓库建档标记：新版后端给 true / false 原样保留；旧版不返回或 null → null（未知，页面放行跳转）
assert.equal(parse([row({ isCreated: true })])[0].isCreated, true);
assert.equal(parse([row({ isCreated: false })])[0].isCreated, false);
assert.equal(legacy.isCreated, null);
assert.equal(parse([row({ isCreated: null })])[0].isCreated, null);

console.log("container-new-products response schema tests passed");
