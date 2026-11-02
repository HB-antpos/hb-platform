import assert from "node:assert/strict";
import {
  getStoreSelectionLabel,
  isSameStoreSelection,
  isStoreSelected,
  normalizeStoreSelection,
  toggleStoreSelection,
} from "./store-selection";

// 空选择集代表全部分店，不算选中任何一家。
assert.equal(isStoreSelected([], "1001"), false);
assert.equal(isStoreSelected(["1001"], " 1001 "), true, "比较忽略首尾空白");
assert.equal(isStoreSelected(["ab01"], "AB01"), true, "比较忽略大小写");
assert.equal(isStoreSelected(["1001"], ""), false);

// 点选：加入、再点移除；移除最后一家回到全部分店。
assert.deepEqual(toggleStoreSelection([], "1001"), ["1001"]);
assert.deepEqual(toggleStoreSelection(["1001"], "1002"), ["1001", "1002"]);
assert.deepEqual(toggleStoreSelection(["1001", "1002"], "1001"), ["1002"]);
assert.deepEqual(toggleStoreSelection(["1001"], "1001"), []);
assert.deepEqual(toggleStoreSelection(["AB01"], "ab01"), [], "大小写不同视为同一家");

// 收敛：只保留白名单内的分店，用白名单写法和顺序，去重。
const enabled = ["1001", "1002", "AB03", "1004"];
assert.deepEqual(normalizeStoreSelection([], enabled), []);
assert.deepEqual(normalizeStoreSelection(["1004", "1001"], enabled), ["1001", "1004"], "按白名单顺序");
assert.deepEqual(normalizeStoreSelection(["ab03", "AB03"], enabled), ["AB03"], "去重并用白名单写法");
assert.deepEqual(normalizeStoreSelection(["9999", "1002"], enabled), ["1002"], "已停用的分店被剔除");
assert.deepEqual(normalizeStoreSelection(["9999"], enabled), [], "全部失效时为空数组，由调用方阻断旧范围请求");
assert.deepEqual(
  normalizeStoreSelection(["1004", "AB03", "1002", "1001"], enabled),
  [],
  "选满全部启用分店等同于全部分店",
);
assert.deepEqual(normalizeStoreSelection(["1001"], []), [], "白名单为空时没有可选分店");

// 同一组分店：不计顺序与大小写。
assert.equal(isSameStoreSelection([], []), true);
assert.equal(isSameStoreSelection(["1001", "AB03"], ["ab03", "1001"]), true);
assert.equal(isSameStoreSelection(["1001"], ["1001", "1002"]), false);
assert.equal(isSameStoreSelection(["1001"], []), false);

// 显示文案。
const names: Record<string, string> = { "1001": "Bankstown", "1002": "" };
const labels = { all: "全部分店", multiple: (count: number) => `已选 ${count} 家分店` };
assert.equal(getStoreSelectionLabel([], (code) => names[code], labels), "全部分店");
assert.equal(getStoreSelectionLabel(["1001"], (code) => names[code], labels), "Bankstown");
assert.equal(getStoreSelectionLabel(["1002"], (code) => names[code], labels), "1002", "店名为空时退回代码");
assert.equal(getStoreSelectionLabel(["1001", "1002"], (code) => names[code], labels), "已选 2 家分店");
