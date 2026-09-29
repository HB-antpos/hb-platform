import assert from "node:assert/strict";
import {
  FRESH_LINKED_SECTIONS,
  filterRowsByBranch,
  findRowPage,
  getCollapsedRowsWithSelection,
  getLinkedTransitionStaleness,
  getScopedRevenue,
  isSameLinkedCode,
  type ProductReportDisplayContext,
} from "./linked-selection";

const base: ProductReportDisplayContext = {
  scopeKey: JSON.stringify(["user-1", ["1001", "1002"]]),
  kind: "china",
  startDate: "2026-09-01",
  endDate: "2026-09-29",
  compareStartDate: "2025-09-01",
  compareEndDate: "2025-09-29",
  compareMode: "ByDate",
  branchCode: null,
  supplierCode: null,
  productSearch: "",
  productPage: 1,
  productSort: "quantity:desc",
};
const next = (patch: Partial<ProductReportDisplayContext>) => ({ ...base, ...patch });

assert.deepEqual(
  getLinkedTransitionStaleness(base, next({ branchCode: "1001" })),
  { branchTable: false, supplierTable: true, productTable: true },
  "选分店：分店表不变（只高亮），供应商表与明细重取",
);
assert.deepEqual(
  getLinkedTransitionStaleness(base, next({ supplierCode: "CN01" })),
  { branchTable: true, supplierTable: false, productTable: true },
  "选供应商：分店表与明细重取，供应商表不变（只高亮）",
);
assert.deepEqual(
  getLinkedTransitionStaleness(next({ kind: "australia" }), next({ kind: "australia", supplierCode: "S01" })),
  { branchTable: false, supplierTable: false, productTable: true },
  "澳洲页签没有分店表，选供应商只影响明细",
);
assert.deepEqual(
  getLinkedTransitionStaleness(next({ branchCode: "1001", supplierCode: "CN01" }), next({ branchCode: "1002", supplierCode: "CN01" })),
  { branchTable: false, supplierTable: true, productTable: true },
  "已选供应商时换分店：分店表数据不变",
);
assert.deepEqual(
  getLinkedTransitionStaleness(base, next({ productPage: 2 })),
  { branchTable: false, supplierTable: false, productTable: true },
  "明细翻页只影响明细，不反向影响分店表与供应商表",
);
assert.deepEqual(
  getLinkedTransitionStaleness(base, next({ productSearch: "HB1", productSort: "amount:desc" })),
  { branchTable: false, supplierTable: false, productTable: true },
);
assert.deepEqual(
  getLinkedTransitionStaleness(next({ branchCode: "1001" }), next({ branchCode: " 1001 " })),
  FRESH_LINKED_SECTIONS,
  "代码只差大小写或空白时视为同一选择",
);
assert.equal(getLinkedTransitionStaleness(base, next({ kind: "australia" })), null, "切页签不是同一份报告，走整页加载");
assert.equal(getLinkedTransitionStaleness(base, next({ endDate: "2026-09-28" })), null, "改日期不能沿用旧结果");
assert.equal(getLinkedTransitionStaleness(base, next({ compareMode: "ByWeek" })), null);
assert.equal(
  getLinkedTransitionStaleness(base, next({ scopeKey: JSON.stringify(["user-1", ["1001"]]) })),
  null,
  "授权分店变化代表权限边界变化，不能沿用旧结果",
);

assert.equal(isSameLinkedCode("abc", " ABC "), true);
assert.equal(isSameLinkedCode(null, undefined), true);
assert.equal(isSameLinkedCode("1001", null), false);

const branches = [
  { branchCode: "1001", branchName: "A", revenue: 100, compareRevenue: 80 },
  { branchCode: "1002", branchName: "B", revenue: 50, compareRevenue: 70 },
];
assert.deepEqual(filterRowsByBranch(branches, null), branches, "未选分店时返回全部");
assert.deepEqual(filterRowsByBranch(branches, "1002").map((row) => row.branchName), ["B"]);
assert.deepEqual(filterRowsByBranch(branches, "9999"), [], "白名单外的分店切不出任何行");

const totalRevenue = { revenue: 160, compareRevenue: 150, branches };
assert.deepEqual(getScopedRevenue(totalRevenue, null), { revenue: 160, compareRevenue: 150 }, "未选分店沿用接口总额");
assert.deepEqual(getScopedRevenue(totalRevenue, "1001"), { revenue: 100, compareRevenue: 80 }, "选分店时从分店明细切片");
assert.deepEqual(getScopedRevenue(totalRevenue, "9999"), { revenue: 0, compareRevenue: 0 });
assert.deepEqual(getScopedRevenue(undefined, "1001"), { revenue: 0, compareRevenue: 0 });

const letters = ["a", "b", "c", "d", "e"];
assert.deepEqual(
  getCollapsedRowsWithSelection(letters, 3, (row) => row === "b"),
  { rows: ["a", "b", "c"], appendedSelected: false },
  "选中行在前几名内时不重复附加",
);
assert.deepEqual(
  getCollapsedRowsWithSelection(letters, 3, (row) => row === "e"),
  { rows: ["a", "b", "c", "e"], appendedSelected: true },
  "选中行不在前几名时附在折叠列表末尾",
);
assert.deepEqual(
  getCollapsedRowsWithSelection(letters, 3, () => false),
  { rows: ["a", "b", "c"], appendedSelected: false },
);

const rows = Array.from({ length: 45 }, (_, index) => index);
assert.equal(findRowPage(rows, 20, (row) => row === 0), 1);
assert.equal(findRowPage(rows, 20, (row) => row === 19), 1);
assert.equal(findRowPage(rows, 20, (row) => row === 20), 2);
assert.equal(findRowPage(rows, 20, (row) => row === 44), 3);
assert.equal(findRowPage(rows, 20, (row) => row === 99), null, "找不到时不翻页");

console.log("linked-selection.test.ts: ok");
