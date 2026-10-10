import assert from "node:assert/strict";
import { PRODUCT_PAGE_SIZE, PRODUCT_PAGE_SIZE_OPTIONS, SUPPLIER_PAGE_SIZE, SUPPLIER_PAGE_SIZE_OPTIONS, getPageRows } from "./pagination";

assert.deepEqual([...SUPPLIER_PAGE_SIZE_OPTIONS], [50, 100]);
assert.equal(SUPPLIER_PAGE_SIZE, 50, "供应商默认每页 50 条");
assert.deepEqual([...PRODUCT_PAGE_SIZE_OPTIONS], [100, 200, 500]);
assert.equal(PRODUCT_PAGE_SIZE, 100, "商品明细默认每页 100 条");
assert.ok(SUPPLIER_PAGE_SIZE_OPTIONS.includes(SUPPLIER_PAGE_SIZE as 50), "默认值必须在可选项里");
assert.ok(PRODUCT_PAGE_SIZE_OPTIONS.includes(PRODUCT_PAGE_SIZE as 100), "默认值必须在可选项里");
assert.deepEqual(getPageRows([1, 2, 3], 2, 2), [3]);
