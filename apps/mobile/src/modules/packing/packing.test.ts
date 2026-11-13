import assert from "node:assert/strict";
import {
  normalizeCartonDetail,
  normalizeCartonList,
  normalizeIncoming,
  normalizeLocate,
  normalizeLocations,
  normalizePalletDetail,
  normalizePalletList,
  normalizeReceiveLookup,
  normalizeResolve,
  normalizeScope,
  readPackingError,
} from "./api-normalization";
import {
  allCartonsChecked,
  buildCartonLabelPayload,
  buildPalletLabelPayload,
  cartonDisplayState,
  cartonLabelPreview,
  cartonViewMode,
  clampCopies,
  destinationFormError,
  destinationFormOf,
  destinationRequestOf,
  emptyDestinationForm,
  findCartonItemByCode,
  findPalletCartonByCode,
  formatLabelTime,
  groupLocateRows,
  groupLocateSections,
  inTransitCartons,
  isShippableDestination,
  looksLikePackCode,
  orderCartonItems,
  orderProgress,
  packTopUp,
  palletBlockReason,
  palletDisplayState,
  palletLabelPreview,
  palletViewMode,
  parseQuantityInput,
  parseWeightInput,
  receiveBlockKind,
  receiveOwnerCode,
  resolveActiveOwner,
  resolveScanQuantity,
  savedOwnerCode,
  sharedOrderNo,
  splitPackNumber,
  sumCartonWeight,
  toDestinationRequest,
  validCheckedGuids,
} from "./packing-logic";
import { shouldHidePrimaryTabBar } from "../navigation/primary-navigation";
import type { PackCartonItem, PackLocateRow } from "./types";

const base = {
  originCode: "S01",
  originName: "Sunnybank",
  ownerCode: "S01",
  ownerName: "Sunnybank",
  createdByName: "Lily",
  createdAtUtc: "2026-11-13T04:30:00",
  updatedAtUtc: "2026-11-13T04:32:00",
};

const cartonPayload = {
  ...base,
  cartonGuid: "c-1",
  cartonNo: "BX2611130042",
  status: 1,
  destination: { destinationType: 2, code: "S02", name: "Inala", carrierGuid: "k-1", carrierName: "Border Express" },
  skuCount: 2,
  pieceCount: 36,
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
assert.equal(carton.destination.code, "S02");
assert.equal(carton.destination.carrierName, "Border Express");
assert.equal(carton.dispatchedAtUtc, null);
assert.equal(carton.items[1].orderQuantity, null, "订货单外商品没有配货数");
assert.equal(carton.items[1].packedInOtherCartons, 0);
assert.equal(carton.items[1].middlePackageQuantity, null);
assert.throws(() => normalizeCartonDetail({ cartonNo: "BX1" }), /Invalid warehouse packing response/, "缺关键字段时拒绝");
// 去向整段省略时按「未指定」处理
const noDestination = normalizeCartonDetail({ ...cartonPayload, destination: undefined });
assert.equal(noDestination.destination.destinationType, 0);
assert.equal(noDestination.destination.code, null);

const cartonList = normalizeCartonList({
  items: [cartonPayload],
  packingCount: 2,
  storedCount: 18,
  outgoingCount: 3,
  incomingCount: 3,
});
assert.deepEqual(
  [cartonList.packingCount, cartonList.storedCount, cartonList.outgoingCount, cartonList.incomingCount],
  [2, 18, 3, 3],
);

const palletPayload = {
  ...base,
  ownerCode: "WAREHOUSE",
  ownerName: "HB Warehouse",
  originCode: "WAREHOUSE",
  originName: "HB Warehouse",
  palletGuid: "p-1",
  palletNo: "PL261113007",
  status: 3,
  destination: { destinationType: 2, code: "S01", name: "Sunnybank", carrierGuid: "k-1", carrierName: "Border Express" },
  consignmentNo: "BEX 4471 0932 18",
  cartonCount: 3,
  inTransitCartonCount: 2,
  skuCount: 2,
  pieceCount: 108,
  dispatchedByName: "Tom",
  dispatchedAtUtc: "2026-11-13T05:00:00",
  cartons: [
    { ...cartonPayload, cartonGuid: "c-a", cartonNo: "BX2611130039", status: 3, items: undefined, weightKg: 4.2 },
    { ...cartonPayload, cartonGuid: "c-b", cartonNo: "BX2611130037", status: 3, items: undefined },
    { ...cartonPayload, cartonGuid: "c-c", cartonNo: "BX2611130036", status: 2, items: undefined, orderNo: "2026-0412" },
  ],
};
const pallet = normalizePalletDetail(palletPayload);
assert.equal(pallet.cartons.length, 3);
assert.equal(pallet.cartons[0].weightKg, 4.2);
assert.equal(pallet.inTransitCartonCount, 2);
assert.equal(pallet.actualInTransitCartonCount, null);
assert.equal(normalizePalletList({ items: [palletPayload], buildingCount: 1, storedCount: 4 }).storedCount, 4);

assert.deepEqual(normalizeResolve({ kind: "carton", guid: "c-1", no: "BX1", palletGuid: "p-1" }).palletGuid, "p-1");
assert.equal(normalizeResolve({ kind: "pallet", guid: "p-1", no: "PL1" }).palletGuid, null);

const scope = normalizeScope({
  owners: [
    { code: "S01", name: "Sunnybank" },
    { code: "WAREHOUSE", name: "HB Warehouse", isWarehouse: true },
  ],
  isManager: true,
});
assert.equal(scope.owners[0].isWarehouse, false, "省略的 isWarehouse 按 false");
assert.equal(scope.isManager, true);
assert.equal(scope.isAdmin, false);

assert.deepEqual(
  normalizeLocations([{ locationGuid: "l-1", ownerCode: "S01", name: "后仓 A 架", barcode: "LC000001", isActive: true }]).map(
    (location) => location.barcode,
  ),
  ["LC000001"],
);

const incoming = normalizeIncoming({
  pending: [
    { kind: "pallet", guid: "p-1", no: "PL261113007", originCode: "WAREHOUSE", originName: "HB Warehouse", cartonCount: 5, receivedCartonCount: 2, pieceCount: 366 },
    { kind: "carton", guid: "c-9", no: "BX2611130018", originCode: "S02", originName: "Inala", cartonCount: 1, pieceCount: 48 },
  ],
});
assert.equal(incoming.pending.length, 2);
assert.equal(incoming.pending[0].receivedCartonCount, 2);
assert.equal(incoming.pending[1].receivedCartonCount, 0, "散箱没有已签收箱数");
assert.deepEqual(incoming.recent, []);

// ---- 签收查找：托盘标签丢了，扫箱标签找到托盘 ----
const lookupVia = normalizeReceiveLookup({
  kind: "pallet",
  pallet: palletPayload,
  scannedCartonGuid: "c-b",
  scannedCartonNo: "BX2611130037",
  viaCartonLabel: true,
  inTransitCartonCount: 2,
  canReceive: true,
});
assert.equal(lookupVia.viaCartonLabel, true);
assert.equal(lookupVia.pallet?.palletNo, "PL261113007");
assert.equal(lookupVia.carton, null);
assert.equal(lookupVia.blockReason, null);
assert.equal(receiveBlockKind(lookupVia), null);
const lookupBlocked = normalizeReceiveLookup({ kind: "pallet", pallet: palletPayload, inTransitCartonCount: 2, canReceive: false, blockReason: "NOT_FOR_YOU" });
assert.equal(receiveBlockKind(lookupBlocked), "notForYou");
assert.equal(receiveBlockKind({ canReceive: false, blockReason: "NOT_IN_TRANSIT" }), "notInTransit");
assert.equal(receiveBlockKind({ canReceive: false, blockReason: null }), "empty");
assert.equal(receiveOwnerCode(lookupVia), "S01", "签收后放位置用收货地点的位置表");
const lookupCarton = normalizeReceiveLookup({ kind: "carton", carton: { ...cartonPayload, status: 3 }, inTransitCartonCount: 1, canReceive: true });
assert.equal(lookupCarton.kind, "carton");
assert.equal(receiveOwnerCode(lookupCarton), "S02");

// ---- 错误读取 ----
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
assert.equal(ambiguous.actualInTransitCartonCount, null);

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
assert.equal(blocked.blockedCarton?.destination.name, "Inala");
assert.deepEqual(blocked.candidates, []);

const mismatch = readPackingError({
  response: {
    status: 409,
    data: { errorCode: "COUNT_MISMATCH", message: "实到 4 箱，系统记录是 5 箱", data: { ...palletPayload, actualInTransitCartonCount: 5 } },
  },
});
assert.equal(mismatch.code, "COUNT_MISMATCH");
assert.equal(mismatch.actualInTransitCartonCount, 5, "整托签收数量对不上时读出系统记录的箱数");
assert.equal(readPackingError({ response: { status: 403, data: { errorCode: "NOT_FOR_YOU" } } }).code, "NOT_FOR_YOU");
assert.equal(readPackingError({ response: { status: 403, data: { errorCode: "PACKING_NOT_ALLOWED" } } }).status, 403);
assert.equal(readPackingError(new Error("Network Error")).code, null);
assert.equal(palletBlockReason("CARTON_NOT_SEALED"), "CARTON_NOT_SEALED");
assert.equal(palletBlockReason("CARTON_ON_OTHER_PALLET"), "CARTON_ON_OTHER_PALLET");
assert.equal(palletBlockReason("DESTINATION_MISMATCH"), "DESTINATION_MISMATCH");
assert.equal(palletBlockReason("PALLET_NOT_BUILDING"), null, "其余错误不是拦截横幅");

// ---- 所属地点 ----
const owners = scope.owners;
assert.equal(resolveActiveOwner(owners, "WAREHOUSE")?.code, "WAREHOUSE", "本机记住的地点仍在范围内就用它");
assert.equal(resolveActiveOwner(owners, "s01")?.code, "S01", "编码不区分大小写");
assert.equal(resolveActiveOwner(owners, "S99")?.code, "S01", "记住的地点已不在账号范围内，退回第一个");
assert.equal(resolveActiveOwner(owners, null)?.code, "S01");
assert.equal(resolveActiveOwner([], "S01"), null, "没有任何地点返回 null");
assert.equal(savedOwnerCode({ "user-a": "S02" }, "USER-A"), "S02", "按账号分开记");
assert.equal(savedOwnerCode({ "user-a": "S02" }, "user-b"), null, "不同账号的选择不串");
assert.equal(savedOwnerCode({}, null), null);

// ---- 编号 ----
assert.equal(looksLikePackCode(" bx2611130042 "), "carton");
assert.equal(looksLikePackCode("PL261113007"), "pallet");
assert.equal(looksLikePackCode("9345678123457"), null);
assert.equal(looksLikePackCode("LC000001"), null, "位置条码不会被当成箱号");
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

// ---- 订货单进度（仓库按订货单装箱才有；分店没有订货单时不显示）----
assert.deepEqual(orderProgress(mug), { ordered: 36, packed: 30, remaining: 6, over: 0 }, "已装含其他箱");
assert.deepEqual(orderProgress({ ...mug, quantity: 40 }), { ordered: 36, packed: 46, remaining: 0, over: 10 });
assert.equal(orderProgress(paper), null, "订货单外商品没有进度");
const ordered = orderCartonItems(carton.items, "P2");
assert.equal(ordered.highlighted, paper, "刚扫入的商品置顶");
assert.deepEqual(ordered.rest, [mug]);
assert.equal(orderCartonItems(carton.items, null).rest.length, 2);

// ---- 去向：类型 0 未指定 / 1 本地存放 / 2 其他分店 / 3 总仓；运输公司是「怎么送」----
assert.deepEqual(toDestinationRequest({ destinationType: 2, code: "S02", carrierGuid: "k-1" }), {
  destinationType: 2,
  destinationCode: "S02",
  carrierGuid: "k-1",
});
assert.deepEqual(toDestinationRequest({ destinationType: 2, code: "S02", carrierGuid: null }), {
  destinationType: 2,
  destinationCode: "S02",
  carrierGuid: null,
}, "自己送 / 对方来取不带运输公司");
assert.deepEqual(toDestinationRequest({ destinationType: 3, code: "WAREHOUSE", carrierGuid: "k-1" }), {
  destinationType: 3,
  destinationCode: null,
  carrierGuid: "k-1",
}, "总仓编码固定，不用传");
assert.deepEqual(toDestinationRequest({ destinationType: 1, code: "S02", carrierGuid: "k-1" }), {
  destinationType: 1,
  destinationCode: null,
  carrierGuid: null,
}, "本地存放不带分店和运输公司");
assert.deepEqual(toDestinationRequest({ destinationType: 0, code: "S02", carrierGuid: "k-1" }), {
  destinationType: 0,
  destinationCode: null,
  carrierGuid: null,
});
assert.deepEqual(destinationRequestOf(carton.destination), { destinationType: 2, destinationCode: "S02", carrierGuid: "k-1" }, "继续装下一箱沿用去向");
assert.equal(isShippableDestination(carton.destination), true);
assert.equal(isShippableDestination({ ...carton.destination, destinationType: 1 }), false);
assert.equal(destinationFormError(emptyDestinationForm(), false), null, "暂不指定可以直接提交");
assert.equal(destinationFormError({ ...emptyDestinationForm(), destinationType: 1 }, false), null, "本地存放可以直接提交");
assert.equal(destinationFormError({ ...emptyDestinationForm(), destinationType: 2 }, false), "storeRequired", "其他分店必须选店");
assert.equal(destinationFormError({ ...emptyDestinationForm(), destinationType: 3 }, false), null, "总仓不需要选店");
assert.equal(destinationFormError({ ...emptyDestinationForm(), destinationType: 3 }, true), "carrierRequired", "选了交运输公司必须选一家");
assert.equal(destinationFormError(destinationFormOf(carton.destination), true), null);
assert.equal(destinationFormOf(carton.destination, { remark: "易碎" }).remark, "易碎");

// ---- 箱 / 托盘的状态与可用方式 ----
const ownerCodes = ["S01"];
const stored = { ...carton, status: 2, palletGuid: null, destination: { destinationType: 1, code: null, name: null, carrierGuid: null, carrierName: null } };
assert.equal(cartonDisplayState({ ...carton, status: 1 }, "S01"), "packing");
assert.equal(cartonDisplayState(stored, "S01"), "stored");
assert.equal(cartonDisplayState({ ...carton, status: 2 }, "S01"), "toDispatch", "封好、去向是其他分店 = 待发出");
assert.equal(cartonDisplayState({ ...stored, palletGuid: "p-1" }, "S01"), "onPallet");
assert.equal(cartonDisplayState({ ...carton, status: 3 }, "S01"), "outgoing", "在路上 = 已发出");
assert.equal(cartonDisplayState({ ...stored, ownerCode: "S02" }, "S01"), "received", "所属地点换成对方、发出方是本地点 = 对方已签收");
assert.equal(cartonDisplayState({ ...carton, status: 9 }, "S01"), "voided");
assert.equal(palletDisplayState({ ...pallet, ownerCode: "WAREHOUSE", status: 3 }, "WAREHOUSE"), "outgoing");
assert.equal(palletDisplayState({ ...pallet, ownerCode: "S01", status: 2, destination: carton.destination }, "S01"), "toDispatch");
assert.equal(palletDisplayState({ ...pallet, ownerCode: "S01", originCode: "WAREHOUSE", status: 2, destination: stored.destination }, "S01"), "stored");

assert.equal(cartonViewMode({ ...carton, status: 1 }, ownerCodes), "editable");
assert.equal(cartonViewMode({ ...carton, status: 2 }, ownerCodes), "editable");
assert.equal(cartonViewMode({ ...carton, status: 3 }, ownerCodes), "outgoing", "发出后发出方只读");
assert.equal(cartonViewMode({ ...carton, status: 3, ownerCode: "S02", originCode: "S02", destination: { ...carton.destination, code: "S01" } }, ownerCodes), "incoming", "发给你的在路上");
assert.equal(cartonViewMode({ ...carton, status: 2, ownerCode: "S02" }, ownerCodes), "received", "你发出、对方已签收");
assert.equal(cartonViewMode({ ...carton, status: 2, ownerCode: "S02", originCode: "S03" }, ownerCodes), "readonly");
assert.equal(palletViewMode({ ...pallet, ownerCode: "WAREHOUSE", status: 3 }, ["WAREHOUSE"]), "outgoing");
assert.equal(palletViewMode({ ...pallet, ownerCode: "WAREHOUSE", status: 3 }, ownerCodes), "incoming", "发给本店的在路上托盘");
assert.equal(palletViewMode({ ...pallet, ownerCode: "S01", status: 1 }, ownerCodes), "editable");

// ---- 签收核对 ----
const transit = inTransitCartons(pallet);
assert.deepEqual(transit.map((item) => item.cartonNo), ["BX2611130039", "BX2611130037"], "已签收的箱不再算待核对");
assert.equal(findPalletCartonByCode(pallet, "bx2611130037")?.cartonGuid, "c-b", "扫箱标签在托盘上就打勾");
assert.equal(findPalletCartonByCode(pallet, "BX0000"), null);
assert.deepEqual(validCheckedGuids(pallet, new Set(["c-a", "c-c", "zzz"])), ["c-a"], "只算还在路上的箱，已签收和不存在的剔除");
assert.equal(allCartonsChecked(pallet, new Set(["c-a"])), false);
assert.equal(allCartonsChecked(pallet, new Set(["c-a", "c-b"])), true, "全部核对完整托签收可以直接用总箱数");
assert.equal(allCartonsChecked({ cartons: [] }, new Set()), false, "没有待签收的箱不算全部核对");

// ---- 位置查询分组 ----
const locate = normalizeLocate({
  product: { productCode: "P1", itemNumber: "HB23817" },
  packedQuantity: 120,
  cartonCount: 5,
  palletCount: 1,
  rows: [
    { cartonGuid: "c-3", cartonNo: "BX2611130041", cartonStatus: 2, quantity: 24, updatedAtUtc: "2026-11-13T04:00:00", group: "stored" },
    { cartonGuid: "c-1", cartonNo: "BX2611130042", cartonStatus: 2, palletGuid: "p-8", palletNo: "PL261113008", locationCode: "A-03", quantity: 24, updatedAtUtc: "2026-11-13T04:00:00", group: "stored" },
    { cartonGuid: "c-2", cartonNo: "BX2611130040", cartonStatus: 2, palletGuid: "p-8", palletNo: "PL261113008", locationCode: "A-03", quantity: 48, updatedAtUtc: "2026-11-13T04:00:00", group: "stored" },
    { cartonGuid: "c-4", cartonNo: "BX2611130037", cartonStatus: 3, quantity: 48, updatedAtUtc: "2026-11-13T04:00:00", group: "transit" },
    { cartonGuid: "c-5", cartonNo: "BX2611120018", cartonStatus: 2, ownerCode: "S02", ownerName: "Inala", quantity: 24, updatedAtUtc: "2026-11-13T04:00:00", group: "elsewhere" },
    { cartonGuid: "c-6", cartonNo: "BX2611120001", cartonStatus: 2, ownerCode: "S05", ownerName: "Cairns", quantity: 12, updatedAtUtc: "2026-11-13T04:00:00", group: "something-new" },
  ],
});
assert.equal(locate.rows[5].group, "other", "未知分组按其他分店处理");
assert.equal(locate.stockQuantity, null, "分店没有仓库库存");
const sections = groupLocateSections(locate.rows as PackLocateRow[]);
assert.deepEqual(
  sections.map((section) => [section.group, section.quantity, section.rows.length]),
  [
    ["stored", 96, 3],
    ["transit", 48, 1],
    ["elsewhere", 24, 1],
    ["other", 12, 1],
  ],
  "按 本店存放 / 在路上 / 对方已签收 / 其他分店 分段，空的分段不出现",
);
assert.deepEqual(groupLocateSections([]), []);
const storedGroups = groupLocateRows(sections[0].rows);
assert.deepEqual(
  storedGroups.map((group) => [group.locationCode, group.palletNo, group.quantity, group.rows.length]),
  [
    ["A-03", "PL261113008", 72, 2],
    [null, null, 24, 1],
  ],
  "有位置的在前，同一位置同一托盘合计",
);

// ---- 标签内容 ----
const labelTime = new Date(2026, 10, 13, 14, 32);
assert.equal(formatLabelTime(labelTime), "13 NOV 14:32");
assert.equal(formatLabelTime(new Date(2026, 0, 5, 7, 4)), "05 JAN 07:04");
const cartonLabel = buildCartonLabelPayload({ ...carton, sealedByName: "Tom" }, labelTime);
assert.deepEqual(cartonLabel, {
  cartonNo: "BX2611130042",
  originName: "Sunnybank",
  destinationType: 2,
  destinationName: "Inala",
  carrierName: "Border Express",
  skuCount: 2,
  pieceCount: 36,
  orderNo: null,
  printedAt: "13 NOV 14:32",
  operatorName: "Tom",
});
assert.equal(buildCartonLabelPayload(carton, labelTime).operatorName, "Lily", "没有封箱人时用建箱人");
const cartonPreview = cartonLabelPreview(cartonLabel);
assert.equal(cartonPreview.statLine, "2 SKU - 36 PCS");
assert.equal(cartonPreview.fromLine, "FROM SUNNYBANK - VIA BORDER EXPRESS", "预览与打印同源");
assert.equal(cartonPreview.destinationName, "INALA");
assert.equal(cartonLabelPreview({ ...cartonLabel, destinationType: 0, destinationName: null, carrierName: null }).destinationName, "UNASSIGNED");
assert.equal(cartonLabelPreview({ ...cartonLabel, destinationType: 1, destinationName: null, carrierName: null }).destinationName, "STORAGE");

const palletLabel = buildPalletLabelPayload(pallet, labelTime);
assert.equal(palletLabel.carrierName, "Border Express");
assert.equal(palletLabel.destinationType, 2);
assert.equal(palletLabel.destinationName, "Sunnybank");
assert.equal(palletLabel.originName, "HB Warehouse", "FROM 取后端返回的发出地");
assert.equal(palletLabel.consignmentNo, "BEX 4471 0932 18");
assert.equal(palletLabel.orderNo, null, "托盘上的箱订货单不一致时不打单号");
const palletPreview = palletLabelPreview(palletLabel, 2);
assert.equal(palletPreview.fromLine, "FROM HB WAREHOUSE - VIA BORDER EXPRESS");
assert.equal(palletPreview.referenceLine, "CN BEX 4471 0932 18");
assert.equal(palletPreview.sheetText, "1/2");
const directPallet = buildPalletLabelPayload(
  { ...pallet, destination: { ...pallet.destination, carrierGuid: null, carrierName: null }, consignmentNo: null },
  labelTime,
);
assert.equal(palletLabelPreview(directPallet, 1).fromLine, "FROM HB WAREHOUSE - DIRECT", "要发出且没有运输公司打 DIRECT");
assert.equal(sharedOrderNo([{ orderNo: "A" }, { orderNo: "B" }]), null, "多张订货单混装不打单号");
assert.equal(sharedOrderNo([{ orderNo: "A" }, { orderNo: null }]), null);
assert.equal(sharedOrderNo([{ orderNo: "A" }, { orderNo: "A" }]), "A");
assert.equal(sharedOrderNo([]), null);
assert.equal(sumCartonWeight([{ weightKg: 4.2 }, { weightKg: 11 }]), 15.2);
assert.equal(sumCartonWeight([{ weightKg: 4.2 }, { weightKg: null }]), null, "有箱没称重时不显示偏小的合计");

// ---- 路由：打包是 PDA 全屏作业，不显示全局底栏 ----
assert.equal(shouldHidePrimaryTabBar("/packing"), true);
assert.equal(shouldHidePrimaryTabBar("/packing/carton/abc"), true);
assert.equal(shouldHidePrimaryTabBar("/packing/receive"), true);
assert.equal(shouldHidePrimaryTabBar("/packingx"), false);

console.log("packing.test.ts passed");
