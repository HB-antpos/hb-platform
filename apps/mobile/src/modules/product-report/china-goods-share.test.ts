import assert from "node:assert/strict";
import type { ProductReportProductRow } from "./api";
import {
  CHINA_BRANCH_COLLAPSED_ROW_COUNT,
  DEFAULT_CHINA_BRANCH_SHARE_SORT,
  buildChinaBranchShareRows,
  getChinaBranchShareScaleMax,
  mergeSupplierBranchSales,
  sortChinaBranchShareRows,
  summarizeChinaGoods,
  summarizeProductPage,
  toggleChinaBranchShareSort,
  type ChinaGoodsBranchTotals,
} from "./china-goods-share";

assert.deepEqual(DEFAULT_CHINA_BRANCH_SHARE_SORT, { field: "amount", order: "desc" }, "分店区块默认按中国货金额降序");
assert.equal(CHINA_BRANCH_COLLAPSED_ROW_COUNT, 8);

const rows = buildChinaBranchShareRows(
  [
    { branchCode: "1001", branchName: "Alpha", revenue: 1000, compareRevenue: 800 },
    { branchCode: "1002", branchName: "Beta", revenue: 500, compareRevenue: 500 },
    // 没有任何中国货销售的分店仍要以 0% 出现。
    { branchCode: "1003", branchName: "Gamma", revenue: 400, compareRevenue: 0 },
  ],
  [
    { branchCode: "1001", branchName: "Alpha", revenue: 300, compareRevenue: 200 },
    { branchCode: "1002", branchName: "Beta", revenue: 250, compareRevenue: 100 },
    // 营业额缺失的分店只能给出 null 占比，不能除以 0。
    { branchCode: "1009", branchName: "Orphan", revenue: 50, compareRevenue: 0 },
  ],
);
assert.equal(rows.length, 4, "以分店营业额为主表左连接，并保留只出现在中国货结果里的分店");
const alpha = rows.find((row) => row.branchCode === "1001")!;
assert.equal(alpha.share, 0.3);
assert.equal(alpha.compareShare, 0.25);
assert.ok(Math.abs(alpha.shareDeltaPoints! - 5) < 1e-9, "增减按百分点计算");
const gamma = rows.find((row) => row.branchCode === "1003")!;
assert.equal(gamma.chinaRevenue, 0);
assert.equal(gamma.share, 0, "有营业额但无中国货的分店占比为 0%");
assert.equal(gamma.compareShare, null, "同期营业额为 0 时同期占比不可计算");
assert.equal(gamma.shareDeltaPoints, null, "任一期占比缺失时不显示增减");
const orphan = rows.find((row) => row.branchCode === "1009")!;
assert.equal(orphan.share, null);
assert.equal(orphan.branchName, "Orphan");

const byAmount = sortChinaBranchShareRows(rows, DEFAULT_CHINA_BRANCH_SHARE_SORT).map((row) => row.branchCode);
assert.deepEqual(byAmount, ["1001", "1002", "1009", "1003"], "默认按中国货金额降序");
const byShare = sortChinaBranchShareRows(rows, { field: "share", order: "desc" }).map((row) => row.branchCode);
assert.deepEqual(byShare, ["1002", "1001", "1003", "1009"], "按占比降序时不可计算的占比排最后");
const byShareAsc = sortChinaBranchShareRows(rows, { field: "share", order: "asc" }).map((row) => row.branchCode);
assert.deepEqual(byShareAsc, ["1003", "1001", "1002", "1009"], "升序时空占比仍排最后");
assert.deepEqual(toggleChinaBranchShareSort(DEFAULT_CHINA_BRANCH_SHARE_SORT, "share"), { field: "share", order: "desc" });
assert.deepEqual(toggleChinaBranchShareSort({ field: "share", order: "desc" }, "share"), { field: "share", order: "asc" });

assert.equal(getChinaBranchShareScaleMax(rows), 0.5, "最大占比 50% 时刻度上限正好 50%");
assert.equal(getChinaBranchShareScaleMax([alpha]), 0.3);
assert.equal(getChinaBranchShareScaleMax([]), 0.1, "没有数据时刻度至少 10%");
assert.equal(
  getChinaBranchShareScaleMax(buildChinaBranchShareRows(
    [{ branchCode: "1", branchName: "A", revenue: 100, compareRevenue: 100 }],
    [{ branchCode: "1", branchName: "A", revenue: 31, compareRevenue: 12 }],
  )),
  0.4,
  "31% 向上取整到 40%",
);
const singleSupplierRows = buildChinaBranchShareRows(
  [
    { branchCode: "1", branchName: "A", revenue: 1000, compareRevenue: 1000 },
    { branchCode: "2", branchName: "B", revenue: 1000, compareRevenue: 1000 },
  ],
  [
    { branchCode: "1", branchName: "A", revenue: 23, compareRevenue: 12 },
    { branchCode: "2", branchName: "B", revenue: 0, compareRevenue: 31 },
  ],
);
assert.equal(getChinaBranchShareScaleMax(singleSupplierRows, 100), 0.04, "选中供应商后按 1% 取整：3.1% 向上取整到 4%");
assert.equal(getChinaBranchShareScaleMax([], 100), 0.01, "按 1% 取整时刻度至少 1%");
assert.equal(getChinaBranchShareScaleMax(singleSupplierRows), 0.1, "默认仍按 10% 取整");

const merged = mergeSupplierBranchSales([
  { branchCode: "1", branchName: "A", revenue: 10, compareRevenue: 4 },
  { branchCode: "2", branchName: "", revenue: 0, compareRevenue: 7 },
  { branchCode: "1", branchName: "A", revenue: 5, compareRevenue: 1 },
  { branchCode: "2", branchName: "B", revenue: 3, compareRevenue: 0 },
]);
assert.deepEqual(
  merged,
  [
    { branchCode: "1", branchName: "A", revenue: 15, compareRevenue: 5 },
    { branchCode: "2", branchName: "B", revenue: 3, compareRevenue: 7 },
  ],
  "分店分解按「分店×供应商」返回时按分店合并金额，名称取第一个非空值",
);
const onlyCompare = buildChinaBranchShareRows(
  [{ branchCode: "2", branchName: "B", revenue: 1000, compareRevenue: 700 }],
  mergeSupplierBranchSales([{ branchCode: "2", branchName: "B", revenue: 0, compareRevenue: 7 }]),
)[0];
assert.equal(onlyCompare.share, 0, "只有同期有销售的分店本期占比为 0%");
assert.equal(onlyCompare.compareShare, 0.01);
assert.ok(Math.abs(onlyCompare.shareDeltaPoints! + 1) < 1e-9, "只有同期有销售时增减为负，不能被当成 0");

const branchTotal = (partial: Partial<ChinaGoodsBranchTotals>): ChinaGoodsBranchTotals => ({
  revenue: 0,
  compareRevenue: 0,
  grossProfit: null,
  compareGrossProfit: null,
  costStatus: "Complete",
  compareCostStatus: "Complete",
  ...partial,
});

const summary = summarizeChinaGoods(
  [
    branchTotal({ revenue: 600, compareRevenue: 500, grossProfit: 240, compareGrossProfit: 180 }),
    branchTotal({ revenue: 400, compareRevenue: 300, grossProfit: 160, compareGrossProfit: null, compareCostStatus: "Missing" }),
  ],
  4000,
  3200,
);
assert.equal(summary.revenue, 1000);
assert.equal(summary.share, 0.25, "中国货占总营业额与供应商表「占总营业」同分母");
assert.equal(summary.compareShare, 0.25);
assert.equal(summary.grossMarginRate, 0.4);
assert.equal(summary.compareCostStatus, "Missing");
assert.equal(summary.compareGrossMarginRate, null, "任一分店缺成本时同期毛利率不可信，交给界面显示成本待补全");
assert.equal(summarizeChinaGoods([], 0, 0).share, null, "总营业额为 0 时占比不可计算");

function product(partial: Partial<ProductReportProductRow>): ProductReportProductRow {
  return {
    id: "p",
    productCode: "p",
    itemNumber: "p",
    productImage: null,
    productName: "p",
    quantity: 0,
    compareQuantity: 0,
    salesAmount: 0,
    compareSalesAmount: 0,
    grossProfit: null,
    compareGrossProfit: null,
    grossMarginRate: null,
    compareGrossMarginRate: null,
    averageUnitPrice: 0,
    compareAverageUnitPrice: 0,
    orderCount: 0,
    compareOrderCount: 0,
    costStatus: "Complete",
    compareCostStatus: "Complete",
    ...partial,
  };
}

const pageTotals = summarizeProductPage([
  product({ quantity: 10, compareQuantity: 8, salesAmount: 50, compareSalesAmount: 40, grossProfit: 20, compareGrossProfit: 16 }),
  product({ quantity: 30, compareQuantity: 0, salesAmount: 30, compareSalesAmount: 0, grossProfit: 12, compareGrossProfit: 0, compareCostStatus: "NoActivity" }),
]);
assert.equal(pageTotals.quantity, 40);
assert.equal(pageTotals.salesAmount, 80);
assert.equal(pageTotals.averageUnitPrice, 2, "本页均价 = 本页金额 ÷ 本页数量，不对单品均价取平均");
assert.equal(pageTotals.grossMarginRate, 0.4);
assert.equal(pageTotals.compareAverageUnitPrice, 5);
assert.equal(pageTotals.compareCostStatus, "Complete");
assert.equal(summarizeProductPage([]).averageUnitPrice, null, "空页没有均价");
assert.equal(
  summarizeProductPage([product({ salesAmount: 10, quantity: 1, costStatus: "Missing" })]).grossProfit,
  null,
  "本页有缺成本商品时毛利额不可信",
);

console.log("china-goods-share.test.ts: ok");
