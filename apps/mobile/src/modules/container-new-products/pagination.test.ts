import assert from "node:assert/strict";
import { CONTAINER_NEW_PRODUCTS_PAGE_SIZE, paginate } from "./pagination";

const items = Array.from({ length: 45 }, (_, index) => index + 1);

assert.equal(CONTAINER_NEW_PRODUCTS_PAGE_SIZE, 20);
assert.deepEqual(paginate(items, 1).items, items.slice(0, 20));
assert.deepEqual(paginate(items, 3), { page: 3, pageCount: 3, items: [41, 42, 43, 44, 45] });
// 刷新后条数变少，原页码超出范围时回到最后一页，而不是显示空页
assert.deepEqual(paginate(items.slice(0, 25), 3), { page: 2, pageCount: 2, items: items.slice(20, 25) });
assert.equal(paginate(items, 0).page, 1);
assert.equal(paginate(items, Number.NaN).page, 1);
assert.deepEqual(paginate([], 2), { page: 1, pageCount: 1, items: [] });

console.log("container-new-products pagination tests passed");
