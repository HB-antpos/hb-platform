import {
  buildBatchExecuteRequest,
  constrainSelectionToVisible,
  countNewProductsWithAdditionalBarcodes,
  excludeFromBatchExecuteRequest,
  findLargePriceChangeRows,
  findSkippableFailedRows,
  groupExecuteTargets,
  pickChosenGuids,
  pickExecuteTargets,
} from "./batch-execute";
import { assertDeepEqual, assertEqual, makeItem } from "./test-helpers";

// 口径搬自 Web batchExecuteConfirm.test.ts，并加上手机端「只执行新建商品 / 更新进货价」的限制。
const create = makeItem({ detailGuid: "c1", activityType: 1, existingProductCount: 0, additionalBarcodes: ["111"] });
const createPlain = makeItem({ detailGuid: "c2", activityType: 1, existingProductCount: 0 });
const up = makeItem({ detailGuid: "u1", activityType: 2, lastPurchasePrice: 1, purchasePrice: 1.2 });
const bigUp = makeItem({ detailGuid: "u2", activityType: 2, lastPurchasePrice: 1, purchasePrice: 1.5, itemNumber: "A2", productName: "Big" });
const down = makeItem({ detailGuid: "d1", activityType: 2, lastPurchasePrice: 2, purchasePrice: 1.6 });
const bigDown = makeItem({ detailGuid: "d2", activityType: 2, lastPurchasePrice: 1, purchasePrice: 0.59 });
const flat = makeItem({ detailGuid: "f1", activityType: 2, lastPurchasePrice: 1, purchasePrice: 1 });
const noHistory = makeItem({ detailGuid: "f2", activityType: 2, lastPurchasePrice: null, purchasePrice: 3 });
const itemNo = makeItem({ detailGuid: "i1", activityType: 4 });
const multi = makeItem({ detailGuid: "m1", activityType: 5 });
const waiting = makeItem({ detailGuid: "w1", activityType: 3 });
const executed = makeItem({ detailGuid: "x1", activityType: 99 });
const unchecked = makeItem({ detailGuid: "n1", activityType: 2, existingProductCount: null });
const all = [create, createPlain, up, bigUp, down, bigDown, flat, noHistory, itemNo, multi, waiting, executed, unchecked];
const allGuids = all.map((row) => row.detailGuid);

// ---- 目标行 ----
const pendingAll = pickExecuteTargets(all, { selectedGuids: [], visibleGuids: [] });
assertDeepEqual(pendingAll.guids, ["c1", "c2", "u1", "u2", "d1", "d2", "f1", "f2"], "无勾选：整单待执行里只取新建 / 更新进货价（未检测的行不算待执行）");
assertEqual(pendingAll.webOnlyCount, 2, "无勾选：更新货号 / 添加多码计入「去 Web 执行」");
assertEqual(pendingAll.noActionCount, 0, "无勾选：等待操作 / 已执行不在待执行里，不计数");

const picked = pickExecuteTargets(all, { selectedGuids: ["u1", "i1", "w1", "x1", "n1", "d1"], visibleGuids: ["u1", "i1", "w1", "x1", "n1"] });
assertDeepEqual(picked.guids, ["u1", "n1"], "有勾选：只取勾选且可见的行；勾选的未检测行照样提交（与 Web 一致，后端校验）");
assertEqual(picked.webOnlyCount, 1, "勾选的更新货号计入去 Web");
assertEqual(picked.noActionCount, 2, "勾选的等待操作 / 已执行计入无可执行操作");

// ---- 分组与勾选 ----
const groups = groupExecuteTargets(pendingAll.guids, all);
assertDeepEqual(groups.createGuids, ["c1", "c2"], "新建商品组");
assertDeepEqual(groups.upGuids, ["u1", "u2"], "涨价组");
assertDeepEqual(groups.downGuids, ["d1", "d2"], "降价组");
assertDeepEqual(groups.flatGuids, ["f1", "f2"], "持平与无历史价单列");

assertDeepEqual(
  pickChosenGuids(pendingAll.guids, groups, { includeCreate: true, includeUp: false, includeDown: true, includeFlat: true }),
  ["c1", "c2", "d1", "d2", "f1", "f2"],
  "不勾涨价：涨价行留作待执行，其余保持原顺序"
);
assertDeepEqual(
  pickChosenGuids(pendingAll.guids, groups, { includeCreate: false, includeUp: false, includeDown: false, includeFlat: false }),
  [],
  "全不勾 = 没有要执行的行"
);

// ---- 40% 二次确认 ----
const large = findLargePriceChangeRows(allGuids, all);
assertDeepEqual(large.map((row) => row.detailGuid), ["u2", "d2"], "严格超过 40% 的涨价与降价都要确认；正好 40%、新建商品、无历史价不算");
assertEqual(Math.round(large[0].ratio * 100), 50, "涨跌幅 = 本次 / 上次 - 1");
assertEqual(
  findLargePriceChangeRows(["x"], [makeItem({ detailGuid: "x", activityType: 2, lastPurchasePrice: 1, purchasePrice: 1.4 })]).length,
  0,
  "正好 40% 不触发（严格大于）"
);
assertEqual(
  findLargePriceChangeRows(["z"], [makeItem({ detailGuid: "z", activityType: 2, lastPurchasePrice: 1, purchasePrice: 0 })]).length,
  0,
  "新进货价 ≤ 0 后端会跳过，不需要确认"
);

// ---- 请求体 ----
assertEqual(countNewProductsWithAdditionalBarcodes(["c1", "c2", "u1"], all), 1, "带副码的新建商品行数");
const request = buildBatchExecuteRequest(["c1", "c2", "u2", "missing"], all, { newProductType: 2, confirmedAt: "2026-10-31T00:00:00.000Z" });
assertDeepEqual(request.detailGuids, ["c1", "c2", "u2"], "找不到的行不进请求");
assertDeepEqual(
  request.expectedActions,
  [
    { detailGuid: "c1", action: 1, activityType: 1 },
    { detailGuid: "c2", action: 1, activityType: 1 },
    { detailGuid: "u2", action: 2, activityType: 2 },
  ],
  "动作快照"
);
assertEqual(request.confirmedCreateProductCount, 2, "新建商品计数与快照一致");
assertDeepEqual(request.newProductProductTypeSelections, [{ detailGuid: "c1", productType: 2 }], "只有带副码的新建行需要类型选择");
assertEqual("confirmedLargePriceChange" in request, false, "没确认过 40% 时不带标志");
assertEqual(
  buildBatchExecuteRequest(["u2"], all, { newProductType: 1, confirmedAt: "t", confirmedLargePriceChange: true }).confirmedLargePriceChange,
  true,
  "确认过 40% 才带标志"
);

const trimmed = excludeFromBatchExecuteRequest(request, ["c1"]);
assertDeepEqual(trimmed.detailGuids, ["c2", "u2"], "跳过失败行后的明细");
assertEqual(trimmed.confirmedCreateProductCount, 1, "跳过后新建计数同步收敛");
assertDeepEqual(trimmed.newProductProductTypeSelections, [], "跳过后类型选择同步收敛");

// ---- 跳过失败行 ----
assertDeepEqual(
  findSkippableFailedRows(["c1", "c2", "u2"], ["明细 c1 新建商品失败：货号不能为空"]),
  { failedGuids: ["c1"], remainingCount: 2 },
  "每条错误都能对应到行时可以跳过"
);
assertEqual(findSkippableFailedRows(["c1", "c2"], ["明细 c1 x", "整单错误"]), null, "有一条对不上行 = 整单级错误，不能跳过");
assertEqual(findSkippableFailedRows(["c1"], ["明细 c1 x"]), null, "全部失败没有可执行的剩余行");
assertEqual(findSkippableFailedRows(["c1"], []), null, "没有错误不需要跳过");

// ---- 勾选收缩 ----
const selection = ["a", "b"];
assertEqual(constrainSelectionToVisible(selection, ["a", "b", "c"]), selection, "勾选都可见时原样返回（引用不变，避免多余渲染）");
assertDeepEqual(constrainSelectionToVisible(selection, ["b"]), ["b"], "看不到的勾选去掉");

console.log("batch-execute tests passed");
