import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { buildContainerNewProductsParams, CONTAINER_NEW_PRODUCTS_WINDOW_BASIS } from "./request-params";

assert.equal(CONTAINER_NEW_PRODUCTS_WINDOW_BASIS, "warehouse");

// 工作台角标（只要新商品）：不带 includeExisting，但始终带 windowBasis=warehouse，与页面同一个窗口
assert.deepEqual(buildContainerNewProductsParams("S-1", false), { storeCode: "S-1", windowBasis: "warehouse" });
assert.equal("includeExisting" in buildContainerNewProductsParams("S-1", false), false, "角标请求不应带 includeExisting");
// 页面（连同已有商品一起取）：按 includeExisting=true 取，同样带 windowBasis
assert.deepEqual(buildContainerNewProductsParams("S-1", true), { storeCode: "S-1", includeExisting: true, windowBasis: "warehouse" });

// api.ts 的请求必须经过这个函数，页面与角标共用 getContainerNewProducts，不能各自拼参数
const api = readFileSync(join(__dirname, "api.ts"), "utf8");
assert.match(api, /params: buildContainerNewProductsParams\(storeCode, includeExisting\)/);
assert.doesNotMatch(api, /params: includeExisting \?/, "不应再有绕过 windowBasis 的旧参数写法");

console.log("container-new-products request params tests passed");
