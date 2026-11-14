import assert from "node:assert/strict";
import { test } from "node:test";
import {
  distinctProducts,
  nextCartonQuantity,
  overPackBy,
  packLaterTotals,
  packScanStep,
  splitPackLines,
  MAX_CARTON_PIECES,
} from "./pack-later-logic";
import { emptyOpenCartons, openFilledCartons, packTotals, planPickTotalChanges, reviewMismatches, reviewState } from "./pack-review-logic";
import { packLaterRoute, packReviewRoute } from "./pack-routes";
import { PACK_CARTON_STATUS, type PackProgressCarton, type PackProgressLine } from "./pack-types";

function progressLine(productCode: string, picked: number, packed: number, extra: Partial<PackProgressLine> = {}): PackProgressLine {
  return { productCode, productName: productCode, itemNumber: null, orderedPieces: picked, pickedPieces: picked, packedPieces: packed, ...extra };
}

function carton(cartonGuid: string, pieceCount: number, status: number = PACK_CARTON_STATUS.packing): PackProgressCarton {
  return { cartonGuid, cartonNo: cartonGuid.toUpperCase(), status, skuCount: pieceCount > 0 ? 1 : 0, pieceCount, palletNo: null, createdByName: "Lily", isMine: true };
}

test("提交前核对：已拣 / 已装箱 / 箱数三格汇总", () => {
  const totals = packTotals({
    lines: [progressLine("A", 48, 44), progressLine("B", 24, 24)],
    cartons: [carton("c-1", 28, PACK_CARTON_STATUS.sealed), carton("c-2", 40)],
  });
  assert.deepEqual(totals, { picked: 72, packed: 68, cartonCount: 2 });
});

test("未封箱：非空的装箱中箱会拦住提交，空箱提交时由服务端作废不算", () => {
  const cartons = [carton("c-sealed", 10, PACK_CARTON_STATUS.sealed), carton("c-open", 20), carton("c-empty", 0), carton("c-transit", 5, PACK_CARTON_STATUS.inTransit)];
  assert.deepEqual(openFilledCartons({ cartons }).map((item) => item.cartonGuid), ["c-open"]);
  assert.deepEqual(emptyOpenCartons({ cartons }).map((item) => item.cartonGuid), ["c-empty"]);
});

test("差异商品：拣货数 ≠ 装箱数；订单没有任何箱时不对账（与服务端一致）", () => {
  const sheetLines = [
    { detailGuid: "d-a", productCode: "A", pickedTotal: 48 },
    { detailGuid: "d-b", productCode: "B", pickedTotal: 24 },
  ];
  const lines = [progressLine("A", 48, 44), progressLine("B", 24, 24), progressLine("C", 0, 3)];
  const mismatches = reviewMismatches({ lines, cartons: [carton("c-1", 28)] }, sheetLines);
  assert.deepEqual(
    mismatches.map((item) => ({ code: item.productCode, picked: item.picked, packed: item.packed, diff: item.diff, details: item.detailGuids })),
    [
      { code: "A", picked: 48, packed: 44, diff: 4, details: ["d-a"] },
      { code: "C", picked: 0, packed: 3, diff: -3, details: [] },
    ],
  );
  assert.deepEqual(reviewMismatches({ lines, cartons: [] }, sheetLines), [], "没有箱：不对账");
});

test("核对状态：没有未封箱也没有差异才能提交，商品编码按不分大小写对回订单行", () => {
  const sheetLines = [{ detailGuid: "d-a", productCode: "a-1", pickedTotal: 10 }];
  const ready = reviewState({ lines: [progressLine("A-1", 10, 10)], cartons: [carton("c-1", 10, PACK_CARTON_STATUS.sealed), carton("c-empty", 0)] }, sheetLines);
  assert.equal(ready.canSubmit, true);
  const blocked = reviewState({ lines: [progressLine("A-1", 10, 8)], cartons: [carton("c-1", 8)] }, sheetLines);
  assert.equal(blocked.canSubmit, false);
  assert.equal(blocked.openCartons.length, 1);
  assert.deepEqual(blocked.mismatches[0].detailGuids, ["d-a"], "大小写不同的商品编码也对得上订单行");
});

test("按装箱数改拣货数：一个订单行时 total = 装箱数，期望合计取现有拣货合计", () => {
  const totals = new Map([["d-a", 48]]);
  assert.deepEqual(planPickTotalChanges({ detailGuids: ["d-a"], packed: 44 }, totals), [{ detailGuid: "d-a", total: 44, expectedTotal: 48 }], "拣多了：改小");
  assert.deepEqual(planPickTotalChanges({ detailGuids: ["d-a"], packed: 52 }, totals), [{ detailGuid: "d-a", total: 52, expectedTotal: 48 }], "箱里装多了：改大");
  assert.deepEqual(planPickTotalChanges({ detailGuids: ["d-a"], packed: 48 }, totals), [], "已经一致：不发请求");
  assert.deepEqual(planPickTotalChanges({ detailGuids: ["d-a"], packed: 0 }, totals), [{ detailGuid: "d-a", total: 0, expectedTotal: 48 }]);
});

test("按装箱数改拣货数：同一商品多行时合计对上装箱数，不扣成负数", () => {
  const totals = new Map([
    ["d-1", 10],
    ["d-2", 6],
  ]);
  // 拣 16、装 13：从最后一行往前扣 3。
  assert.deepEqual(planPickTotalChanges({ detailGuids: ["d-1", "d-2"], packed: 13 }, totals), [{ detailGuid: "d-2", total: 3, expectedTotal: 6 }]);
  // 拣 16、装 4：最后一行扣光再扣前一行。
  assert.deepEqual(planPickTotalChanges({ detailGuids: ["d-1", "d-2"], packed: 4 }, totals), [
    { detailGuid: "d-2", total: 0, expectedTotal: 6 },
    { detailGuid: "d-1", total: 4, expectedTotal: 10 },
  ]);
  // 拣 16、装 20：差额补到第一行。
  assert.deepEqual(planPickTotalChanges({ detailGuids: ["d-1", "d-2"], packed: 20 }, totals), [{ detailGuid: "d-1", total: 14, expectedTotal: 10 }]);
  // detailGuids 里有不在拣货单上的：只用有订单行的。
  assert.deepEqual(planPickTotalChanges({ detailGuids: ["gone", "d-2"], packed: 2 }, totals), [{ detailGuid: "d-2", total: 2, expectedTotal: 6 }]);
});

test("按装箱数改拣货数：商品不在订单上（没有订单行）没法改，交给人处理", () => {
  assert.equal(planPickTotalChanges({ detailGuids: [], packed: 3 }, new Map([["d-1", 5]])), null);
  assert.equal(planPickTotalChanges({ detailGuids: ["unknown"], packed: 3 }, new Map([["d-1", 5]])), null);
});

test("先拣后装列表：待装排前、装完沉底，没拣没装的不列，焦点商品排最前", () => {
  const lines = [
    progressLine("DONE", 36, 36),
    progressLine("LEFT-SMALL", 24, 18),
    progressLine("LEFT-BIG", 48, 12),
    progressLine("NONE", 0, 0),
    progressLine("OVER", 10, 12),
  ];
  const groups = splitPackLines(lines);
  assert.deepEqual(groups.pending.map((line) => line.productCode), ["LEFT-SMALL", "LEFT-BIG"], "保持服务端顺序");
  assert.deepEqual(groups.pending.map((line) => line.remaining), [6, 36]);
  assert.deepEqual(groups.done.map((line) => line.productCode), ["DONE", "OVER"]);
  assert.equal(groups.done[1].remaining, -2, "装多了为负");
  assert.deepEqual(splitPackLines(lines, "left-big").pending.map((line) => line.productCode), ["LEFT-BIG", "LEFT-SMALL"], "焦点商品排最前，不分大小写");
  assert.deepEqual(splitPackLines(lines, "DONE").pending.map((line) => line.productCode), ["LEFT-SMALL", "LEFT-BIG"], "焦点不在待装里：顺序不变");
});

test("先拣后装进度：已装 / 已拣 / 还差只累加待装的商品", () => {
  const totals = packLaterTotals([progressLine("A", 48, 12), progressLine("B", 10, 12), progressLine("C", 20, 20)]);
  assert.deepEqual(totals, { picked: 78, packed: 44, remaining: 36, pendingProducts: 1 });
});

test("先拣后装：一次扫码加多少（按中包用中包数，没设置按 1 件）与绝对数计算", () => {
  assert.equal(packScanStep("pack", { minOrderQuantity: 6 }), 6);
  assert.equal(packScanStep("pack", { minOrderQuantity: null }), 1);
  assert.equal(packScanStep("pack", { minOrderQuantity: 0 }), 1);
  assert.equal(packScanStep("pack", null), 1);
  assert.equal(packScanStep("each", { minOrderQuantity: 6 }), 1);
  assert.equal(nextCartonQuantity(5, 6), 11);
  assert.equal(nextCartonQuantity(0, -1), 0, "不会减成负数");
  assert.equal(nextCartonQuantity(MAX_CARTON_PIECES, 6), MAX_CARTON_PIECES, "不超过上限");
});

test("超装判断：全订单装箱合计超过已拣才算，其他箱的件数不变", () => {
  // 已拣 48，已装 44（本箱 12 + 其他箱 32）。
  const line = { pickedPieces: 48, packedPieces: 44 };
  assert.equal(overPackBy(line, 12, 16), 0, "刚好装到 48，不超");
  assert.equal(overPackBy(line, 12, 22), 6, "本箱加到 22：合计 54，超 6");
  assert.equal(overPackBy(line, 12, 8), 0, "减少不会超");
  assert.equal(overPackBy({ pickedPieces: 0, packedPieces: 0 }, 0, 3), 3, "商品没拣过：装多少超多少");
  assert.equal(overPackBy({ pickedPieces: 10, packedPieces: 12 }, 12, 13), 3, "原本就超装：继续加按总超出算");
});

test("扫到的码对应多行：按商品去重，一个商品一项", () => {
  const lines = [
    { productCode: "P1", detailGuid: "d-1" },
    { productCode: "p1", detailGuid: "d-2" },
    { productCode: "P2", detailGuid: "d-3" },
  ];
  assert.deepEqual(distinctProducts(lines).map((line) => line.detailGuid), ["d-1", "d-3"]);
});

test("新页面路由：都在拣货前缀下，商品编码要编码", () => {
  assert.equal(packLaterRoute("abc"), "/warehouse-picking/abc/pack");
  assert.equal(packLaterRoute("abc", "HB 20/455"), "/warehouse-picking/abc/pack?focus=HB%2020%2F455");
  assert.equal(packReviewRoute("a b"), "/warehouse-picking/a%20b/pack-review");
});
