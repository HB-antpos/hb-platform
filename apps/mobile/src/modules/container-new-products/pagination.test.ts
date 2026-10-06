import assert from "node:assert/strict";
import { CONTAINER_NEW_PRODUCTS_PAGE_SIZE, normalizePageSize, paginate, parsePageInput } from "./pagination";

const items = Array.from({ length: 120 }, (_, index) => index + 1);

assert.equal(CONTAINER_NEW_PRODUCTS_PAGE_SIZE, 50);
assert.deepEqual(paginate(items, 1).items, items.slice(0, 50));
assert.deepEqual(paginate(items, 3), { page: 3, pageCount: 3, items: items.slice(100, 120) });
assert.deepEqual(paginate(items, 1, 100).items, items.slice(0, 100));
assert.equal(paginate(items, 1, 200).pageCount, 1);
// 刷新后条数变少，原页码超出范围时回到最后一页，而不是显示空页
assert.deepEqual(paginate(items.slice(0, 60), 3), { page: 2, pageCount: 2, items: items.slice(50, 60) });
assert.equal(paginate(items, 0).page, 1);
assert.equal(paginate(items, Number.NaN).page, 1);
assert.deepEqual(paginate([], 2), { page: 1, pageCount: 1, items: [] });

// 每页条数只认 50 / 100 / 200，旧版本记住的 20 或脏值回到默认 50
assert.equal(normalizePageSize(100), 100);
assert.equal(normalizePageSize("200"), 200);
assert.equal(normalizePageSize(20), 50);
assert.equal(normalizePageSize(null), 50);
assert.equal(normalizePageSize("abc"), 50);

assert.equal(parsePageInput(" 7 ", 9), 7);
assert.equal(parsePageInput("10", 9), null);
assert.equal(parsePageInput("0", 9), null);
assert.equal(parsePageInput("2.5", 9), null);
assert.equal(parsePageInput("", 9), null);

console.log("container-new-products pagination tests passed");
