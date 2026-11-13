import assert from "node:assert/strict";
import { normalizeCartonDetail, normalizeLocate, normalizePalletDetail, readPackingError } from "./api-normalization";
import {
  buildCartonLabelPayload,
  buildPalletLabelPayload,
  cartonDisplayState,
  cartonLabelPreview,
  clampCopies,
  destinationFormError,
  destinationFormOf,
  emptyDestinationForm,
  findCartonItemByCode,
  formatLabelTime,
  groupLocateRows,
  looksLikePackCode,
  orderCartonItems,
  orderProgress,
  packTopUp,
  palletBlockReason,
  palletLabelPreview,
  parseQuantityInput,
  parseWeightInput,
  resolveScanQuantity,
  sharedOrderNo,
  splitPackNumber,
  sumCartonWeight,
  toDestinationRequest,
} from "./packing-logic";
import { shouldHidePrimaryTabBar } from "../navigation/primary-navigation";
import type { PackCartonItem, PackLocateRow } from "./types";

const cartonPayload = {
  cartonGuid: "c-1",
  cartonNo: "BX2611130042",
  status: 1,
  originName: "HB WAREHOUSE",
  destination: { destinationType: 1, storeCode: "S01", storeName: "Sunnybank" },
  skuCount: 2,
  pieceCount: 36,
  orderGuid: "o-1",
  orderNo: "2026-0412",
  createdByName: "Lily",
  createdAtUtc: "2026-11-13T04:30:00",
  updatedAtUtc: "2026-11-13T04:32:00",
  items: [
    {
      productCode: "P1",
      itemNumber: "HB23817",
      productName: "Mug",
      barcode: "9345678123457",
      quantity: 24,
      middlePackageQuantity: 12,
      packingQuantity: 48,
      orderQuantity: 36,
      packedInOtherCartons: 6,
      updatedAtUtc: "2026-11-13T04:32:00",
    },
    { productCode: "P2", itemNumber: "HB25031", quantity: 12, updatedAtUtc: "2026-11-13T04:31:00" },
  ],
  changedProductCode: "P1",
};

// ---- 归一化：后端省略空字段（WhenWritingNull），统一补成 null / 0 / [] ----
const carton = normalizeCartonDetail({ success: true, data: cartonPayload });
assert.equal(carton.cartonNo, "BX2611130042");
assert.equal(carton.palletGuid, null, "省略的可空字段补成 null");
assert.equal(carton.destination.carrierGuid, null);
assert.equal(carton.items[1].orderQuantity, null, "订货单外商品没有配货数");
assert.equal(carton.items[1].packedInOtherCartons, 0);
assert.equal(carton.items[1].middlePackageQuantity, null);
assert.throws(() => normalizeCartonDetail({ cartonNo: "BX1" }), /Invalid warehouse packing response/, "缺关键字段时拒绝");

const pallet = normalizePalletDetail({
  palletGuid: "p-1",
  palletNo: "PL261113007",
  status: 1,
  originName: "HB WAREHOUSE",
  destination: { destinationType: 2, storeCode: "S09", storeName: "Cairns", carrierGuid: "k-1", carrierName: "Border Express" },
  consignmentNo: "BEX 4471 0932 18",
  cartonCount: 1,
  skuCount: 2,
  pieceCount: 36,
  createdByName: "Lily",
  createdAtUtc: "2026-11-13T05:00:00",
  updatedAtUtc: "2026-11-13T05:06:00",
  cartons: [{ ...cartonPayload, status: 2, items: undefined, weightKg: 4.2 }],
});
assert.equal(pallet.cartons.length, 1);
assert.equal(pallet.cartons[0].weightKg, 4.2);
assert.equal(pallet.remark, null);

// ---- 错误读取：409 带回候选商品或被拦下的箱 ----
const ambiguous = readPackingError({
  response: {
    status: 409,
    data: {
      success: false,
      errorCode: "CODE_AMBIGUOUS",
      message: "这个码对应多个商品，请选择",
      data: { cartonGuid: "c-1", candidates: [{ productCode: "P1", productName: "Mug" }, { productCode: "P9" }] },
    },
  },
});
assert.equal(ambiguous.code, "CODE_AMBIGUOUS");
assert.equal(ambiguous.status, 409);
assert.deepEqual(ambiguous.candidates.map((item) => item.productCode), ["P1", "P9"]);
assert.equal(ambiguous.blockedCarton, null);

const blocked = readPackingError({
  response: {
    status: 409,
    data: {
      errorCode: "DESTINATION_MISMATCH",
      data: { palletGuid: "p-1", palletNo: "PL261113007", blockedCarton: { ...cartonPayload, status: 2, items: undefined } },
    },
  },
});
assert.equal(blocked.blockedCarton?.cartonNo, "BX2611130042", "被拦下的箱用来说明原因");
assert.equal(blocked.blockedCarton?.destination.storeName, "Sunnybank");
assert.deepEqual(blocked.candidates, []);
assert.equal(readPackingError({ response: { status: 403, data: { errorCode: "PACKING_NOT_ALLOWED" } } }).status, 403);
assert.equal(readPackingError(new Error("Network Error")).code, null);
assert.equal(palletBlockReason("CARTON_NOT_SEALED"), "CARTON_NOT_SEALED");
assert.equal(palletBlockReason("CARTON_ON_OTHER_PALLET"), "CARTON_ON_OTHER_PALLET");
assert.equal(palletBlockReason("DESTINATION_MISMATCH"), "DESTINATION_MISMATCH");
assert.equal(palletBlockReason("PALLET_NOT_BUILDING"), null, "其余错误不是拦截横幅");

// ---- 编号 ----
assert.equal(looksLikePackCode(" bx2611130042 "), "carton");
assert.equal(looksLikePackCode("PL261113007"), "pallet");
assert.equal(looksLikePackCode("9345678123457"), null);
assert.equal(looksLikePackCode("BXA-01"), null, "货位码不会被当成箱号");
assert.deepEqual(splitPackNumber("BX2611130042"), { prefix: "BX261113", tail: "0042" });
assert.deepEqual(splitPackNumber("PL261113007"), { prefix: "PL261113", tail: "007" });
assert.deepEqual(splitPackNumber("BX1"), { prefix: "", tail: "BX1" });

// ---- 三种计数方式 ----
const mug = carton.items[0];
const paper = carton.items[1];
assert.equal(findCartonItemByCode(carton.items, "9345678123457"), mug, "按条码找到箱内商品");
assert.equal(findCartonItemByCode(carton.items, "hb25031"), paper, "货号不区分大小写");
assert.equal(findCartonItemByCode(carton.items, "0000"), null);
const sameBarcode: PackCartonItem[] = [mug, { ...paper, barcode: mug.barcode }];
assert.equal(findCartonItemByCode(sameBarcode, "9345678123457"), null, "一码多品不在客户端猜");
assert.equal(resolveScanQuantity("each", mug), 1, "每扫 +1");
assert.equal(resolveScanQuantity("pack", mug), 12, "按中包：箱里已有且有中包数时加一个中包");
assert.equal(resolveScanQuantity("pack", paper), 1, "没有中包数时加 1");
assert.equal(resolveScanQuantity("pack", null), 1, "商品第一次进箱先加 1");
assert.equal(resolveScanQuantity("ask", mug, 30), 30, "扫后输数量用输入值");
assert.equal(resolveScanQuantity("ask", null, 0), 1);
assert.equal(resolveScanQuantity("ask", null, 999999), 100000, "不超过后端单品上限");
assert.equal(packTopUp({ ...mug, quantity: 1 }, 1), 12, "第一次进箱补到整中包");
assert.equal(packTopUp({ ...mug, quantity: 25 }, 1), 36, "已有数量上再补 11 件");
assert.equal(packTopUp(mug, 12), null, "已经按中包加过就不再补");
assert.equal(packTopUp(paper, 1), null, "没有中包数不补");
assert.equal(packTopUp(null, 1), null);

assert.equal(parseQuantityInput("24"), 24);
assert.equal(parseQuantityInput(""), null);
assert.equal(parseQuantityInput("0"), null, "新增数量不能为 0");
assert.equal(parseQuantityInput("0", true), 0, "改总数时 0 表示移出");
assert.equal(parseQuantityInput("100001"), null);
assert.equal(parseWeightInput(""), null, "重量选填");
assert.equal(parseWeightInput("12,5"), 12.5);
assert.equal(parseWeightInput("abc"), "invalid");
assert.equal(parseWeightInput("0"), "invalid");
assert.equal(clampCopies(0), 1);
assert.equal(clampCopies(99), 5);

// ---- 订货单进度 ----
assert.deepEqual(orderProgress(mug), { ordered: 36, packed: 30, remaining: 6, over: 0 }, "已装含其他箱");
assert.deepEqual(orderProgress({ ...mug, quantity: 40 }), { ordered: 36, packed: 46, remaining: 0, over: 10 });
assert.equal(orderProgress(paper), null, "订货单外商品没有进度");

const ordered = orderCartonItems(carton.items, "P2");
assert.equal(ordered.highlighted, paper, "刚扫入的商品置顶");
assert.deepEqual(ordered.rest, [mug]);
assert.equal(orderCartonItems(carton.items, null).rest.length, 2);

assert.equal(cartonDisplayState({ status: 1, palletGuid: null }), "packing");
assert.equal(cartonDisplayState({ status: 2, palletGuid: null }), "sealed");
assert.equal(cartonDisplayState({ status: 2, palletGuid: "p-1" }), "onPallet");
assert.equal(cartonDisplayState({ status: 9, palletGuid: null }), "voided");

// ---- 去向 ----
assert.deepEqual(toDestinationRequest({ destinationType: 1, storeCode: "S01", carrierGuid: "k-1" }), {
  destinationType: 1,
  destinationStoreCode: "S01",
  carrierGuid: null,
});
assert.deepEqual(toDestinationRequest({ destinationType: 2, storeCode: null, carrierGuid: "k-1" }), {
  destinationType: 2,
  destinationStoreCode: null,
  carrierGuid: "k-1",
});
assert.deepEqual(toDestinationRequest({ destinationType: 0, storeCode: "S01", carrierGuid: "k-1" }), {
  destinationType: 0,
  destinationStoreCode: null,
  carrierGuid: null,
});
assert.equal(destinationFormError(emptyDestinationForm()), null, "暂不指定可以直接提交");
assert.equal(destinationFormError({ ...emptyDestinationForm(), destinationType: 1 }), "storeRequired");
assert.equal(destinationFormError({ ...emptyDestinationForm(), destinationType: 2 }), "carrierRequired");
assert.equal(destinationFormError(destinationFormOf(pallet.destination)), null, "运输公司的收货分店可空也可填");

// ---- 标签内容 ----
const labelTime = new Date(2026, 10, 13, 14, 32);
assert.equal(formatLabelTime(labelTime), "13 NOV 14:32");
assert.equal(formatLabelTime(new Date(2026, 0, 5, 7, 4)), "05 JAN 07:04");
const cartonLabel = buildCartonLabelPayload({ ...carton, sealedByName: "Tom" }, labelTime);
assert.deepEqual(cartonLabel, {
  cartonNo: "BX2611130042",
  originName: "HB WAREHOUSE",
  destinationName: "Sunnybank",
  skuCount: 2,
  pieceCount: 36,
  orderNo: "2026-0412",
  printedAt: "13 NOV 14:32",
  operatorName: "Tom",
});
assert.equal(buildCartonLabelPayload(carton, labelTime).operatorName, "Lily", "没有封箱人时用建箱人");
assert.equal(cartonLabelPreview(cartonLabel).statLine, "2 SKU - 36 PCS");
assert.equal(cartonLabelPreview({ ...cartonLabel, destinationName: null }).destinationName, "UNASSIGNED");

const palletLabel = buildPalletLabelPayload(pallet, labelTime);
assert.equal(palletLabel.carrierName, "Border Express");
assert.equal(palletLabel.direct, false);
assert.equal(palletLabel.destinationName, "Cairns");
assert.equal(palletLabel.consignmentNo, "BEX 4471 0932 18");
assert.equal(palletLabel.orderNo, "2026-0412", "托盘上的箱同属一张订货单时带出单号");
const palletPreview = palletLabelPreview(palletLabel, 2);
assert.equal(palletPreview.fromLine, "FROM HB WAREHOUSE - VIA BORDER EXPRESS");
assert.equal(palletPreview.referenceLine, "CN BEX 4471 0932 18");
assert.equal(palletPreview.sheetText, "1/2");
const directLabel = buildPalletLabelPayload(
  { ...pallet, destination: { ...pallet.destination, destinationType: 1, carrierGuid: null, carrierName: null }, consignmentNo: null },
  labelTime,
);
assert.equal(directLabel.direct, true);
assert.equal(directLabel.carrierName, null);
assert.equal(palletLabelPreview(directLabel, 1).fromLine, "FROM HB WAREHOUSE - DIRECT");
assert.equal(palletLabelPreview(directLabel, 1).referenceLine, "ORDER 2026-0412", "直送没有运单号时打订货单号");
assert.equal(sharedOrderNo([{ orderNo: "A" }, { orderNo: "B" }]), null, "多张订货单混装不打单号");
assert.equal(sharedOrderNo([{ orderNo: "A" }, { orderNo: null }]), null);
assert.equal(sharedOrderNo([]), null);
assert.equal(sumCartonWeight([{ weightKg: 4.2 }, { weightKg: 11 }]), 15.2);
assert.equal(sumCartonWeight([{ weightKg: 4.2 }, { weightKg: null }]), null, "有箱没称重时不显示偏小的合计");

// ---- 位置查询分组：货位 → 托盘 → 箱 ----
const locate = normalizeLocate({
  product: { productCode: "P1", itemNumber: "HB23817" },
  stockQuantity: 340,
  packedQuantity: 96,
  cartonCount: 3,
  palletCount: 1,
  rows: [
    { cartonGuid: "c-3", cartonNo: "BX2611130041", cartonStatus: 1, quantity: 24, updatedAtUtc: "2026-11-13T04:00:00" },
    {
      cartonGuid: "c-1",
      cartonNo: "BX2611130042",
      cartonStatus: 2,
      palletGuid: "p-8",
      palletNo: "PL261113008",
      locationCode: "A-03",
      quantity: 24,
      updatedAtUtc: "2026-11-13T04:00:00",
    },
    {
      cartonGuid: "c-2",
      cartonNo: "BX2611130040",
      cartonStatus: 2,
      palletGuid: "p-8",
      palletNo: "PL261113008",
      locationCode: "A-03",
      quantity: 48,
      updatedAtUtc: "2026-11-13T04:00:00",
    },
  ],
});
assert.equal(locate.middlePackageQuantity, null);
const groups = groupLocateRows(locate.rows as PackLocateRow[]);
assert.equal(groups.length, 2);
assert.deepEqual(
  groups.map((group) => [group.locationCode, group.palletNo, group.quantity, group.rows.length]),
  [
    ["A-03", "PL261113008", 72, 2],
    [null, null, 24, 1],
  ],
  "有货位的在前，同一货位同一托盘合计",
);

// ---- 路由：打包是 PDA 全屏作业，不显示全局底栏 ----
assert.equal(shouldHidePrimaryTabBar("/warehouse-packing"), true);
assert.equal(shouldHidePrimaryTabBar("/warehouse-packing/carton/abc"), true);
assert.equal(shouldHidePrimaryTabBar("/warehouse-packingx"), false);

console.log("warehouse-packing.test.ts passed");
