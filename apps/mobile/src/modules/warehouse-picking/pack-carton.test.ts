import assert from "node:assert/strict";
import { test } from "node:test";
import { normalizePickMutation, normalizePickSubmit } from "./api-normalization";
import { normalizePackProgress, normalizeSetCartonItem, readCartonSync, readPackSubmitBlock } from "./pack-api-normalization";
import {
  applyCartonDetail,
  applyCartonSync,
  cartonFromDetail,
  cartonFromProgress,
  cartonPiecesOf,
  classifyCartonWriteError,
  encodePackedParam,
  isPackApiMissing,
  parsePackedParam,
  previewCartonOf,
  resumableCarton,
  sameGuid,
  summarizeCartons,
  withCartonGuid,
} from "./pack-carton-logic";
import { PACK_CARTON_STATUS, type CartonSync, type PackProgressCarton } from "./pack-types";
import type { PackCartonDetail } from "../packing/types";

const lineBody = { detailGuid: "d-1", pickedTotal: 12, pickedBy: [], assigneeUserGuid: null };

function detail(overrides: Partial<PackCartonDetail> = {}): PackCartonDetail {
  return {
    cartonGuid: "c-1",
    cartonNo: "BX2611140003",
    status: 1,
    ownerCode: "WAREHOUSE",
    ownerName: "Warehouse",
    originCode: "WAREHOUSE",
    originName: "HB Warehouse",
    destination: { destinationType: 2, code: "SUNNY", name: "Sunnybank", carrierGuid: null, carrierName: null },
    skuCount: 0,
    pieceCount: 0,
    palletGuid: null,
    palletNo: null,
    locationCode: null,
    weightKg: null,
    orderGuid: "o-1",
    orderNo: "2026-0412",
    createdByName: "Lily",
    createdAtUtc: "2026-11-14T00:00:00Z",
    sealedByName: null,
    sealedAtUtc: null,
    dispatchedByName: null,
    dispatchedAtUtc: null,
    receivedByName: null,
    receivedAtUtc: null,
    updatedAtUtc: "2026-11-14T00:00:00Z",
    remark: null,
    items: [],
    changedProductCode: null,
    ...overrides,
  };
}

function progressCarton(overrides: Partial<PackProgressCarton> & Pick<PackProgressCarton, "cartonGuid">): PackProgressCarton {
  return {
    cartonNo: overrides.cartonGuid.toUpperCase(),
    status: PACK_CARTON_STATUS.packing,
    skuCount: 0,
    pieceCount: 0,
    palletNo: null,
    createdByName: "Lily",
    createdAtUtc: null,
    isMine: false,
    items: null,
    ...overrides,
  };
}

test("不带箱的拣货写入：请求体原样返回，不出现 cartonGuid 字段（行为与旧版一致）", () => {
  const body = { detailGuid: "d-1", source: 2, clientRequestId: "r-1" };
  assert.equal(withCartonGuid(body, null), body, "没有当前箱时返回同一个对象");
  assert.equal(withCartonGuid(body, undefined), body);
  assert.equal(withCartonGuid(body, ""), body);
  assert.equal("cartonGuid" in withCartonGuid(body, null), false);
  assert.equal(JSON.stringify(withCartonGuid(body, null)), JSON.stringify(body), "序列化后逐字节一致");
  assert.deepEqual(withCartonGuid(body, "c-1"), { ...body, cartonGuid: "c-1" });
  assert.equal("cartonGuid" in body, false, "不改原对象");
});

test("当前箱：新建的空箱各商品件数已知，接着用的旧箱只知道汇总", () => {
  const fresh = cartonFromDetail(detail());
  assert.equal(fresh.itemsComplete, true);
  assert.equal(cartonPiecesOf(fresh, "P1"), 0, "新箱里没碰过的商品是 0 件");

  const loaded = cartonFromDetail(
    detail({
      skuCount: 1,
      pieceCount: 12,
      items: [{ productCode: "P1", itemNumber: null, productName: null, barcode: null, productImage: null, quantity: 12, middlePackageQuantity: 6, packingQuantity: null, orderQuantity: 24, packedInOtherCartons: 0, updatedAtUtc: "x" }],
    }),
  );
  assert.equal(cartonPiecesOf(loaded, "p1"), 12, "商品编码不分大小写");

  const resumedEmpty = cartonFromProgress(progressCarton({ cartonGuid: "c-2", pieceCount: 0 }));
  assert.equal(resumedEmpty.itemsComplete, true, "接着用的是空箱：各商品都是 0，已知");
  const resumedFilled = cartonFromProgress(progressCarton({ cartonGuid: "c-3", skuCount: 3, pieceCount: 28 }));
  assert.equal(resumedFilled.itemsComplete, false);
  assert.equal(cartonPiecesOf(resumedFilled, "P1"), null, "没碰过的商品件数未知，不能当 0");
  assert.equal(cartonPiecesOf(null, "P1"), null);
});

test("进入页面自动接着用的箱：自己建的、装箱中的，多只取最后一只", () => {
  const cartons = [
    progressCarton({ cartonGuid: "c-sealed", isMine: true, status: PACK_CARTON_STATUS.sealed }),
    progressCarton({ cartonGuid: "c-other", isMine: false }),
    progressCarton({ cartonGuid: "c-old", isMine: true, pieceCount: 5 }),
    progressCarton({ cartonGuid: "c-new", isMine: true }),
    progressCarton({ cartonGuid: "c-transit", isMine: true, status: PACK_CARTON_STATUS.inTransit }),
  ];
  assert.equal(resumableCarton({ cartons })?.cartonGuid, "c-new");
  assert.equal(resumableCarton({ cartons: cartons.filter((carton) => !carton.isMine) }), null, "同事的箱不接着用");
  assert.equal(resumableCarton({ cartons: [] }), null);
});

test("接着用旧箱：后端带 items 时各商品件数已知（含空箱），旧后端没有 items 退回只有汇总", () => {
  const withItems = cartonFromProgress(
    progressCarton({
      cartonGuid: "c-5",
      skuCount: 2,
      pieceCount: 28,
      items: [
        { productCode: "HB-1", quantity: 12 },
        { productCode: "HB-2", quantity: 16 },
      ],
    }),
  );
  assert.equal(withItems.itemsComplete, true, "有 items：不用再读箱详情");
  assert.equal(cartonPiecesOf(withItems, "hb-1"), 12, "商品编码不分大小写");
  assert.equal(cartonPiecesOf(withItems, "HB-9"), 0, "箱里没有的商品是 0 件，不是未知");

  const emptyWithItems = cartonFromProgress(progressCarton({ cartonGuid: "c-6", items: [] }));
  assert.equal(emptyWithItems.itemsComplete, true);
  assert.equal(cartonPiecesOf(emptyWithItems, "HB-1"), 0);

  // 旧后端：没有 items 字段，有商品的箱件数未知。
  const legacy = cartonFromProgress(progressCarton({ cartonGuid: "c-7", skuCount: 1, pieceCount: 9, items: null }));
  assert.equal(legacy.itemsComplete, false);
  assert.equal(cartonPiecesOf(legacy, "HB-1"), null);
});

test("自动接着用的箱：自己建的、装箱中的里建箱时间最晚的；时间缺失时取列表靠后的", () => {
  const cartons = [
    progressCarton({ cartonGuid: "c-newest", isMine: true, createdAtUtc: "2026-11-14T05:00:00Z" }),
    progressCarton({ cartonGuid: "c-older", isMine: true, createdAtUtc: "2026-11-14T03:00:00Z" }),
    progressCarton({ cartonGuid: "c-later-other", isMine: false, createdAtUtc: "2026-11-14T09:00:00Z" }),
    progressCarton({ cartonGuid: "c-sealed", isMine: true, status: PACK_CARTON_STATUS.sealed, createdAtUtc: "2026-11-14T08:00:00Z" }),
  ];
  assert.equal(resumableCarton({ cartons })?.cartonGuid, "c-newest", "按建箱时间，不按列表位置");
  // 不带 Z 的时间也按 UTC 解析，和带 Z 的可以比较。
  assert.equal(
    resumableCarton({
      cartons: [
        progressCarton({ cartonGuid: "c-a", isMine: true, createdAtUtc: "2026-11-14T05:00:00" }),
        progressCarton({ cartonGuid: "c-b", isMine: true, createdAtUtc: "2026-11-14T04:00:00Z" }),
      ],
    })?.cartonGuid,
    "c-a",
  );
  // 同一时间：后面的（箱号更大）胜出；没有时间的旧后端：取最后一只。
  assert.equal(
    resumableCarton({
      cartons: [
        progressCarton({ cartonGuid: "c-1", isMine: true, createdAtUtc: "2026-11-14T05:00:00Z" }),
        progressCarton({ cartonGuid: "c-2", isMine: true, createdAtUtc: "2026-11-14T05:00:00Z" }),
      ],
    })?.cartonGuid,
    "c-2",
  );
  assert.equal(
    resumableCarton({ cartons: [progressCarton({ cartonGuid: "c-x", isMine: true }), progressCarton({ cartonGuid: "c-y", isMine: true })] })?.cartonGuid,
    "c-y",
  );
  // 有时间的比没时间的新。
  assert.equal(
    resumableCarton({
      cartons: [
        progressCarton({ cartonGuid: "c-timed", isMine: true, createdAtUtc: "2026-11-14T01:00:00Z" }),
        progressCarton({ cartonGuid: "c-untimed", isMine: true }),
      ],
    })?.cartonGuid,
    "c-timed",
  );
});

test("写入成功后按响应更新当前箱：汇总与该商品件数，别的商品不动；对应的不是当前箱则忽略", () => {
  const current = { ...cartonFromDetail(detail()), items: { p2: 6 }, skuCount: 1, pieceCount: 6 };
  const sync: CartonSync = { cartonGuid: "C-1", cartonNo: "BX2611140003", skuCount: 2, pieceCount: 18, itemPieces: 12 };
  const next = applyCartonSync(current, sync, "P1");
  assert.equal(next?.skuCount, 2);
  assert.equal(next?.pieceCount, 18);
  assert.deepEqual(next?.items, { p2: 6, p1: 12 });
  assert.equal(current.pieceCount, 6, "不改原状态");

  assert.equal(applyCartonSync(current, { ...sync, cartonGuid: "other" }, "P1"), current, "响应对应别的箱：原样返回");
  assert.equal(applyCartonSync(null, sync, "P1"), null);
  assert.deepEqual(applyCartonSync(current, sync, null)?.items, { p2: 6 }, "不知道商品时只更新汇总");

  // 先拣后装：完整箱详情覆盖，各商品件数以服务端为准。
  const replaced = applyCartonDetail(
    current,
    detail({
      skuCount: 1,
      pieceCount: 4,
      items: [{ productCode: "P3", itemNumber: null, productName: null, barcode: null, productImage: null, quantity: 4, middlePackageQuantity: null, packingQuantity: null, orderQuantity: null, packedInOtherCartons: 0, updatedAtUtc: "x" }],
    }),
  );
  assert.deepEqual(replaced?.items, { p3: 4 });
  assert.equal(replaced?.itemsComplete, true);
  assert.equal(applyCartonDetail(current, detail({ cartonGuid: "other" })), current);
});

test("装箱写入错误分类：减一不够、箱不可用、数量越界；其他错误交回拣货原有提示", () => {
  const insufficient = classifyCartonWriteError("CARTON_QTY_INSUFFICIENT", {
    line: lineBody,
    carton: { cartonGuid: "c-1", cartonNo: "BX2611140003", skuCount: 1, pieceCount: 6, itemPieces: 6 },
  });
  assert.equal(insufficient?.kind, "qtyInsufficient");
  assert.equal(insufficient?.kind === "qtyInsufficient" ? insufficient.pieces : -1, 6, "提示里的 N = 箱内现有件数");
  assert.deepEqual(
    classifyCartonWriteError("CARTON_QTY_INSUFFICIENT", null),
    { kind: "qtyInsufficient", pieces: null, carton: null },
    "没带 carton 数据：件数未知，不乱编",
  );
  for (const code of ["CARTON_NOT_FOUND", "CARTON_NOT_PACKING", "CARTON_IN_TRANSIT"] as const) {
    assert.deepEqual(classifyCartonWriteError(code, null), { kind: "cartonUnavailable", code });
  }
  assert.deepEqual(classifyCartonWriteError("QUANTITY_OUT_OF_RANGE", null), { kind: "outOfRange" });
  assert.equal(classifyCartonWriteError("PICKED_TOTAL_CHANGED", { line: lineBody }), null);
  assert.equal(classifyCartonWriteError(null, null), null);
});

test("装箱接口不存在（后端没部署二期）只认 404 且不是业务错误码", () => {
  assert.equal(isPackApiMissing(404, null), true);
  assert.equal(isPackApiMissing(404, "ERR_BAD_REQUEST"), true, "axios 自己的 code 不算业务码");
  assert.equal(isPackApiMissing(404, "ORDER_NOT_FOUND"), false);
  assert.equal(isPackApiMissing(404, "CARTON_NOT_FOUND"), false);
  assert.equal(isPackApiMissing(500, null), false);
  assert.equal(isPackApiMissing(null, null), false, "断网不算接口不存在");
});

test("已装箱汇总：只算有商品的箱，当前箱用实时数字，刚新建还没拉取的箱也计入", () => {
  const cartons = [
    { cartonGuid: "c-1", pieceCount: 28 },
    { cartonGuid: "c-2", pieceCount: 0 },
    { cartonGuid: "c-3", pieceCount: 10 },
  ];
  assert.deepEqual(summarizeCartons(cartons, null), { cartonCount: 2, pieceCount: 38 });
  assert.deepEqual(summarizeCartons(cartons, { cartonGuid: "C-3", pieceCount: 14 }), { cartonCount: 2, pieceCount: 42 });
  assert.deepEqual(summarizeCartons(cartons, { cartonGuid: "c-2", pieceCount: 3 }), { cartonCount: 3, pieceCount: 41 }, "空箱装进东西后算一只");
  assert.deepEqual(summarizeCartons(cartons, { cartonGuid: "c-new", pieceCount: 5 }), { cartonCount: 3, pieceCount: 43 });
  assert.deepEqual(summarizeCartons(cartons, { cartonGuid: "c-new", pieceCount: 0 }), { cartonCount: 2, pieceCount: 38 });
  assert.equal(sameGuid("ABC", "abc"), true);
  assert.equal(sameGuid(null, "abc"), false);
});

test("封箱弹层标签预览：有建箱数据用它，接着用的旧箱按订单信息近似", () => {
  const withBase = { ...cartonFromDetail(detail()), skuCount: 3, pieceCount: 28 };
  const preview = previewCartonOf(withBase, null, "Lily");
  assert.equal(preview.originName, "HB Warehouse");
  assert.equal(preview.skuCount, 3);
  assert.equal(preview.pieceCount, 28);
  assert.equal(preview.destination.name, "Sunnybank");

  const resumed = cartonFromProgress(progressCarton({ cartonGuid: "c-9", cartonNo: "BX2611140009", skuCount: 2, pieceCount: 9 }));
  const approx = previewCartonOf(resumed, { orderNo: "2026-0412", storeCode: "SUNNY", storeName: "Sunnybank" }, "Lily");
  assert.equal(approx.cartonNo, "BX2611140009");
  assert.equal(approx.destination.name, "Sunnybank");
  assert.equal(approx.orderNo, "2026-0412");
  assert.equal(approx.createdByName, "Lily");
});

test("拣货写入响应：带 carton 时解析，没有（不装箱）或格式不对都按没有处理", () => {
  const plain = normalizePickMutation({ success: true, data: { line: lineBody, appliedDelta: 6 } });
  assert.equal(plain.carton, null, "不带箱的响应没有 carton，原有字段不变");
  assert.equal(plain.line.pickedTotal, 12);
  assert.equal(plain.appliedDelta, 6);

  const boxed = normalizePickMutation({
    success: true,
    data: { line: lineBody, appliedDelta: 6, duplicate: false, carton: { cartonGuid: "c-1", cartonNo: "BX2611140003", skuCount: 2, pieceCount: 18, itemPieces: 12 } },
  });
  assert.deepEqual(boxed.carton, { cartonGuid: "c-1", cartonNo: "BX2611140003", skuCount: 2, pieceCount: 18, itemPieces: 12 });

  const broken = normalizePickMutation({ success: true, data: { line: lineBody, appliedDelta: 0, carton: { cartonNo: 5 } } });
  assert.equal(broken.carton, null, "carton 格式不对不影响拣货结果");
  assert.equal(readCartonSync(undefined), null);
});

test("提交结果：装过箱才有 cartonCount / packedPieces", () => {
  const plain = normalizePickSubmit({
    success: true,
    data: { submittedAtUtc: "2026-11-14T00:00:00Z", submittedByName: "Lily", lineCount: 12, shortLineCount: 1, overLineCount: 0 },
  });
  assert.equal(plain.cartonCount, null);
  assert.equal(plain.packedPieces, null);
  const boxed = normalizePickSubmit({
    success: true,
    data: { submittedAtUtc: "2026-11-14T00:00:00Z", submittedByName: "Lily", lineCount: 12, shortLineCount: 1, overLineCount: 0, cartonCount: 5, packedPieces: 282 },
  });
  assert.equal(boxed.cartonCount, 5);
  assert.equal(boxed.packedPieces, 282);
});

test("提交被装箱拦下：解析未封箱与差异商品，两类之外不算装箱拦截", () => {
  const open = readPackSubmitBlock("PACKING_OPEN_CARTONS", {
    openCartons: [{ cartonGuid: "c-7", cartonNo: "BX2611140007", skuCount: 2, pieceCount: 20 }],
  });
  assert.deepEqual(open, { kind: "openCartons", openCartons: [{ cartonGuid: "c-7", cartonNo: "BX2611140007", skuCount: 2, pieceCount: 20 }] });

  const mismatch = readPackSubmitBlock("PACKING_NOT_RECONCILED", {
    mismatches: [
      { productCode: "P-20455", productName: "A5 notebook", detailGuids: ["d-1"], picked: 48, packed: 44, diff: 4 },
      { productCode: "P-OUT", detailGuids: [], picked: 0, packed: 3 },
    ],
  });
  assert.equal(mismatch?.kind, "notReconciled");
  const list = mismatch?.kind === "notReconciled" ? mismatch.mismatches : [];
  assert.equal(list[0].diff, 4);
  assert.equal(list[0].itemNumber, null);
  assert.equal(list[1].diff, -3, "diff 缺失时按 picked - packed 补");
  assert.deepEqual(list[1].detailGuids, []);

  assert.equal(readPackSubmitBlock("PACKING_OPEN_CARTONS", null)?.kind, "openCartons", "data 缺失也认得出是装箱拦截");
  assert.equal(readPackSubmitBlock("SEGMENTS_INCOMPLETE", {}), null);
  assert.equal(readPackSubmitBlock(null, null), null);
});

test("装箱进度：null 字段省略时补默认值，必填结构缺失按接口失败", () => {
  const progress = normalizePackProgress({
    success: true,
    data: {
      orderGuid: "o-1",
      flowStatus: 3,
      lines: [{ productCode: "P1", orderedPieces: 36, pickedPieces: 36, packedPieces: 24 }],
      cartons: [{ cartonGuid: "c-1", cartonNo: "BX2611140003", status: 1, skuCount: 3, pieceCount: 28, createdByName: "Lily", isMine: true }],
      openCartonCount: 1,
      emptyCartonCount: 0,
      sealedCartonCount: 0,
      reconciled: false,
    },
  });
  assert.equal(progress.orderNo, null);
  assert.equal(progress.sessionStatus, null);
  assert.equal(progress.lines[0].productName, null);
  assert.equal(progress.cartons[0].palletNo, null);
  assert.equal(progress.cartons[0].isMine, true);
  assert.equal(progress.cartons[0].createdAtUtc, null, "旧后端没有建箱时间");
  assert.equal(progress.cartons[0].items, null, "缺失的 items 保持 null，不能补成空箱 []");
  assert.throws(() => normalizePackProgress({ success: true, data: { flowStatus: 3 } }), /Invalid warehouse picking pack progress/);
});

test("装箱进度的箱：带 createdAtUtc 与 items（空箱是 []，已封的箱没有 items）", () => {
  const progress = normalizePackProgress({
    success: true,
    data: {
      orderGuid: "o-1",
      flowStatus: 3,
      lines: [{ productCode: "ONLY-IN-BOX", orderedPieces: 0, pickedPieces: 0, packedPieces: 4 }],
      cartons: [
        { cartonGuid: "c-1", cartonNo: "BX1", status: 1, skuCount: 1, pieceCount: 4, createdByName: "Lily", createdAtUtc: "2026-11-14T05:00:00Z", isMine: true, items: [{ productCode: "ONLY-IN-BOX", quantity: 4 }] },
        { cartonGuid: "c-2", cartonNo: "BX2", status: 1, createdAtUtc: "2026-11-14T05:10:00Z", items: [] },
        { cartonGuid: "c-3", cartonNo: "BX3", status: 2, skuCount: 1, pieceCount: 9, createdAtUtc: "2026-11-14T04:00:00Z" },
      ],
      openCartonCount: 2,
      emptyCartonCount: 1,
      sealedCartonCount: 1,
      reconciled: false,
    },
  });
  assert.equal(progress.cartons[0].createdAtUtc, "2026-11-14T05:00:00Z");
  assert.deepEqual(progress.cartons[0].items, [{ productCode: "ONLY-IN-BOX", quantity: 4 }]);
  assert.deepEqual(progress.cartons[1].items, [], "空箱的 items 是 []，与缺失不同");
  assert.equal(progress.cartons[2].items, null, "已封的箱不带 items");
  assert.equal(progress.lines[0].orderedPieces, 0, "只在箱里、订单上没有的商品也在 lines 里");
});

test("先拣后装的设置数量结果：箱详情 + 超装提示", () => {
  const result = normalizeSetCartonItem({
    success: true,
    data: {
      carton: {
        cartonGuid: "c-1",
        cartonNo: "BX2611140003",
        status: 1,
        skuCount: 1,
        pieceCount: 6,
        createdAtUtc: "2026-11-14T00:00:00Z",
        updatedAtUtc: "2026-11-14T00:00:00Z",
        items: [{ productCode: "P1", quantity: 6, updatedAtUtc: "2026-11-14T00:00:00Z" }],
      },
      overPacked: true,
      overPackedBy: 2,
    },
  });
  assert.equal(result.carton.items[0].quantity, 6);
  assert.equal(result.overPacked, true);
  assert.equal(result.overPackedBy, 2);
  assert.throws(() => normalizeSetCartonItem({ success: true, data: { overPacked: false } }));
});

test("提交成功带回订单列表的箱数提示参数：编码、解析与非法值", () => {
  assert.equal(encodePackedParam(5, 282), "5:282");
  assert.deepEqual(parsePackedParam("5:282"), { cartons: 5, pieces: 282 });
  assert.deepEqual(parsePackedParam(["3:10"]), { cartons: 3, pieces: 10 });
  assert.equal(parsePackedParam("abc"), null);
  assert.equal(parsePackedParam("5"), null);
  assert.equal(parsePackedParam(undefined), null);
  assert.equal(encodePackedParam(-1, 2.9), "0:2");
});
