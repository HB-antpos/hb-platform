import assert from "node:assert/strict";
import {
  getPageSelectionState,
  getSelectedCreatableDetails,
  getSelectedDetails,
  locateDetailRow,
  resolveRowPressAction,
  toSelectedSet,
  toggleRowSelection,
} from "./container-detail-selection";
import type { ContainerDetail } from "./types";

const rows: ContainerDetail[] = [
  { hguid: "a", 是否新商品: true },
  { hguid: "b", 是否新商品: false },
  { hguid: "C", 是否新商品: true },
  {}, // 没有 GUID 的行不参与勾选
];

// ---- 选择集合 ----
assert.deepEqual([...toSelectedSet([" a ", "", "b", "a"])].sort(), ["a", "b"]);

// ---- 本页勾选状态（表头复选框三态） ----
assert.deepEqual(getPageSelectionState(rows, new Set()), { pageCount: 3, pageSelectedCount: 0, allSelected: false, partiallySelected: false });
assert.deepEqual(getPageSelectionState(rows, new Set(["a"])), { pageCount: 3, pageSelectedCount: 1, allSelected: false, partiallySelected: true });
assert.deepEqual(getPageSelectionState(rows, new Set(["a", "b", "C", "other-page"])), { pageCount: 3, pageSelectedCount: 3, allSelected: true, partiallySelected: false });
assert.equal(getPageSelectionState([], new Set(["a"])).allSelected, false, "空页不算全选");

// ---- 单行勾选：返回新数组，不改入参 ----
const original = ["a"];
assert.deepEqual(toggleRowSelection(original, "b"), ["a", "b"]);
assert.deepEqual(toggleRowSelection(original, "a"), []);
assert.deepEqual(toggleRowSelection(original, " a "), [], "忽略首尾空白");
assert.deepEqual(toggleRowSelection(original, "  "), ["a"], "空 GUID 不改变选择");
assert.deepEqual(original, ["a"]);

// ---- 创建新商品候选：已勾选 且 未建档 ----
assert.deepEqual(getSelectedCreatableDetails(rows, new Set(["a", "b"])).map((row) => row.hguid), ["a"], "已建档的 b 不能再次创建");
assert.deepEqual(getSelectedCreatableDetails(rows, new Set(["a", "C"])).map((row) => row.hguid), ["a", "C"]);
assert.deepEqual(getSelectedCreatableDetails(rows, new Set()), []);
assert.deepEqual(getSelectedDetails(rows, new Set(["b", "C"])).map((row) => row.hguid), ["b", "C"]);

// ---- 定位到行 ----
assert.deepEqual(locateDetailRow(rows, "C"), { kind: "found", index: 2 });
assert.deepEqual(locateDetailRow(rows, " c "), { kind: "found", index: 2 }, "忽略大小写与空白");
assert.deepEqual(locateDetailRow(rows, "zzz"), { kind: "not-on-page" });
assert.deepEqual(locateDetailRow(rows, ""), { kind: "not-on-page" });
assert.deepEqual(locateDetailRow([], "a"), { kind: "not-on-page" });

// ---- 行点击：与旧版「编辑明细」按钮同一权限条件 ----
assert.equal(resolveRowPressAction(true), "edit");
assert.equal(resolveRowPressAction(false), "view", "无编辑权限只能只读查看");

console.log("container-detail-selection.test.ts: ok");
