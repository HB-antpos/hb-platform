import assert from "node:assert/strict";
import { CONTAINER_NEW_PRODUCTS_PAGE_SIZE, CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS, paginate } from "./pagination";

const items = Array.from({ length: 120 }, (_, index) => index + 1);

assert.equal(CONTAINER_NEW_PRODUCTS_PAGE_SIZE, 50);
assert.deepEqual([...CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS], [50, 100, 200]);
assert.deepEqual(paginate(items, 1).items, items.slice(0, 50));
assert.deepEqual(paginate(items, 3), { page: 3, pageCount: 3, items: items.slice(100, 120) });
assert.deepEqual(paginate(items, 1, 100).items, items.slice(0, 100));
assert.equal(paginate(items, 1, 200).pageCount, 1);
// 刷新后条数变少，原页码超出范围时回到最后一页，而不是显示空页
assert.deepEqual(paginate(items.slice(0, 60), 3), { page: 2, pageCount: 2, items: items.slice(50, 60) });
assert.equal(paginate(items, 0).page, 1);
assert.equal(paginate(items, Number.NaN).page, 1);
assert.deepEqual(paginate([], 2), { page: 1, pageCount: 1, items: [] });

// 每页条数归一化、跳页输入解析已迁到公共分页模块，对应测试见 components/ui/pagination/pagination-logic.test.ts

console.log("container-new-products pagination tests passed");
