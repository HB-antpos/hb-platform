import assert from "node:assert/strict";
import { countNewProductKinds } from "./summary";

assert.equal(countNewProductKinds([]), 0);
// 同一商品出现在两个货柜（大小写不同也视为同一商品）只算一个品种
assert.equal(countNewProductKinds([{ productCode: "p-1" }, { productCode: "P-1" }, { productCode: "p-2" }]), 2);

console.log("container-new-products summary tests passed");
