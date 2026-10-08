import assert from "node:assert/strict";
import { clampPage, getPageCount, getPageGridNumbers, getPageRange, normalizePageSize, parsePageInput } from "./pagination-logic";

// 总页数：向上取整，空数据也是 1 页，非法每页条数不抛错
assert.equal(getPageCount(0, 50), 1);
assert.equal(getPageCount(1, 50), 1);
assert.equal(getPageCount(50, 50), 1);
assert.equal(getPageCount(51, 50), 2);
assert.equal(getPageCount(1234, 500), 3);
assert.equal(getPageCount(100, 0), 1);
assert.equal(getPageCount(Number.NaN, 50), 1);
assert.equal(getPageCount(-5, 50), 1);

// 页码夹取
assert.equal(clampPage(3, 5), 3);
assert.equal(clampPage(9, 5), 5);
assert.equal(clampPage(0, 5), 1);
assert.equal(clampPage(-2, 5), 1);
assert.equal(clampPage(Number.NaN, 5), 1);
assert.equal(clampPage(2.9, 5), 2);
assert.equal(clampPage(3, 0), 1);

// 显示区间：首页 / 中间页 / 末页按实际剩余收尾 / 越界页夹回 / 空数据
assert.deepEqual(getPageRange(1, 50, 120), { from: 1, to: 50 });
assert.deepEqual(getPageRange(2, 50, 120), { from: 51, to: 100 });
assert.deepEqual(getPageRange(3, 50, 120), { from: 101, to: 120 });
assert.deepEqual(getPageRange(9, 50, 120), { from: 101, to: 120 });
assert.deepEqual(getPageRange(1, 500, 7), { from: 1, to: 7 });
assert.deepEqual(getPageRange(1, 50, 0), { from: 0, to: 0 });
assert.deepEqual(getPageRange(1, 0, 10), { from: 0, to: 0 });

// 每页条数只认可选项，其余（旧版本的 20、脏值、null）回到 fallback
const containerOptions = [50, 100, 200, 500] as const;
assert.equal(normalizePageSize(100, containerOptions, 50), 100);
assert.equal(normalizePageSize("500", containerOptions, 50), 500);
assert.equal(normalizePageSize(20, containerOptions, 50), 50);
assert.equal(normalizePageSize(null, containerOptions, 50), 50);
assert.equal(normalizePageSize(undefined, containerOptions, 100), 100);
assert.equal(normalizePageSize("abc", containerOptions, 50), 50);
assert.equal(normalizePageSize("", containerOptions, 50), 50);
// 同一个值在不同页面的可选项里结果不同：可选项由页面决定，不写死 50/100/200
assert.equal(normalizePageSize(500, [50, 100, 200] as const, 50), 50);

// 跳页输入：只接受 1..pageCount 的整数
assert.equal(parsePageInput(" 7 ", 9), 7);
assert.equal(parsePageInput("9", 9), 9);
assert.equal(parsePageInput("10", 9), null);
assert.equal(parsePageInput("0", 9), null);
assert.equal(parsePageInput("2.5", 9), null);
assert.equal(parsePageInput("-1", 9), null);
assert.equal(parsePageInput("", 9), null);

// 页码格子：只在 2..40 页时出现
assert.deepEqual(getPageGridNumbers(1), []);
assert.deepEqual(getPageGridNumbers(3), [1, 2, 3]);
assert.equal(getPageGridNumbers(40).length, 40);
assert.deepEqual(getPageGridNumbers(41), []);

console.log("pagination logic tests passed");
