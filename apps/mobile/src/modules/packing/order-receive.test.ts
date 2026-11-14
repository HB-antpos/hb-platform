import assert from "node:assert/strict";
import { test } from "node:test";
import { normalizeIncoming, normalizeOrderReceive, readPackingError } from "./api-normalization";
import {
  canReceiveOrder,
  clampCartonCount,
  countMismatchCounts,
  incomingOrderEntries,
  orderEntriesOf,
  orderReceiveKey,
  parseOrderBarcode,
  orderProductsByVariance,
  productShipDiff,
  productShipStatus,
  productShipTarget,
  receiveOrderRoute,
  splitOrderCartons,
} from "./order-receive-logic";
import { CARTON_STATUS, type PackCartonDetail, type PackCartonSummary, type PackOrderReceiveCarton } from "./types";

const received: PackOrderReceiveCarton = { cartonGuid: "c-1", cartonNo: "BX1", status: CARTON_STATUS.sealed, palletNo: null, skuCount: 1, pieceCount: 5 };
const transit: PackOrderReceiveCarton = { cartonGuid: "c-2", cartonNo: "BX2", status: CARTON_STATUS.inTransit, palletNo: "PL1", skuCount: 2, pieceCount: 20 };

function summary(overrides: Partial<PackCartonSummary> & Pick<PackCartonSummary, "cartonGuid">): PackCartonSummary {
  return {
    cartonNo: overrides.cartonGuid.toUpperCase(),
    status: CARTON_STATUS.inTransit,
    ownerCode: "WAREHOUSE",
    ownerName: "Warehouse",
    originCode: "WAREHOUSE",
    originName: "Warehouse",
    destination: { destinationType: 2, code: "SUNNY", name: "Sunnybank", carrierGuid: null, carrierName: null },
    skuCount: 1,
    pieceCount: 5,
    palletGuid: null,
    palletNo: null,
    locationCode: null,
    weightKg: null,
    orderGuid: null,
    orderNo: null,
    createdByName: "Lily",
    createdAtUtc: "x",
    sealedByName: null,
    sealedAtUtc: null,
    dispatchedByName: null,
    dispatchedAtUtc: null,
    receivedByName: null,
    receivedAtUtc: null,
    updatedAtUtc: "x",
    ...overrides,
  };
}

test("按订单签收：箱分成在途与已签收，数量以服务端为准", () => {
  const groups = splitOrderCartons([received, transit]);
  assert.deepEqual(groups.inTransit.map((carton) => carton.cartonGuid), ["c-2"]);
  assert.deepEqual(groups.received.map((carton) => carton.cartonGuid), ["c-1"]);
  assert.equal(canReceiveOrder({ inTransitCount: 3 }), true);
  assert.equal(canReceiveOrder({ inTransitCount: 0 }), false, "没有在途的箱不显示签收");
});

test("箱数核对：实到箱数限制在 0~999，对不上时提示实到与系统记录两个数", () => {
  assert.equal(clampCartonCount(5.9), 5);
  assert.equal(clampCartonCount(-2), 0);
  assert.equal(clampCartonCount(5000), 999);
  assert.equal(clampCartonCount(Number.NaN), 0);
  assert.deepEqual(countMismatchCounts(3, 5, 4), { entered: 3, actual: 5 }, "优先用服务端带回的在途箱数");
  assert.deepEqual(countMismatchCounts(3, null, 4), { entered: 3, actual: 4 }, "服务端没带回用页面上的在途数");
  const info = readPackingError({
    response: { status: 409, data: { success: false, errorCode: "COUNT_MISMATCH", data: { orderGuid: "o-1", actualInTransitCartonCount: 5 } } },
  });
  assert.equal(info.code, "COUNT_MISMATCH");
  assert.equal(info.actualInTransitCartonCount, 5, "409 的 data 里带回实际在途箱数");
});

test("商品对照：有配货数按配货数，没有按订货数；少发在先", () => {
  const exact = { orderedPieces: 24, allocPieces: 20, shippedPieces: 20 };
  const short = { orderedPieces: 24, allocPieces: 20, shippedPieces: 12 };
  const over = { orderedPieces: 24, allocPieces: 20, shippedPieces: 26 };
  const noAlloc = { orderedPieces: 24, allocPieces: null, shippedPieces: 24 };
  assert.equal(productShipTarget(exact), 20);
  assert.equal(productShipTarget(noAlloc), 24);
  assert.equal(productShipStatus(exact), "match");
  assert.equal(productShipStatus(short), "short");
  assert.equal(productShipStatus(over), "over");
  assert.equal(productShipStatus(noAlloc), "match");
  assert.equal(productShipDiff(short), -8);
  assert.equal(productShipDiff(over), 6);
  assert.deepEqual(orderProductsByVariance([exact, over, short, noAlloc]), [short, over, exact, noAlloc]);
});

test("按订单签收入口：只在能签收时给，托盘取在途的箱，同一订单只列一次，没有订单的箱不给", () => {
  const withOrder = (guid: string, orderGuid: string | null, orderNo: string | null, status: number = CARTON_STATUS.inTransit) =>
    summary({ cartonGuid: guid, orderGuid, orderNo, status });

  const palletLookup = {
    canReceive: true,
    carton: null,
    pallet: {
      cartons: [
        withOrder("c-1", "o-1", "2026-0412"),
        withOrder("c-2", "O-1", "2026-0412"),
        withOrder("c-3", "o-2", "2026-0415"),
        withOrder("c-4", null, null),
        withOrder("c-5", "o-3", "2026-0416", CARTON_STATUS.sealed),
      ],
    },
  } as unknown as Parameters<typeof orderEntriesOf>[0];
  assert.deepEqual(orderEntriesOf(palletLookup), [
    { orderGuid: "o-1", orderNo: "2026-0412" },
    { orderGuid: "o-2", orderNo: "2026-0415" },
  ]);

  const cartonLookup = {
    canReceive: true,
    pallet: null,
    carton: { ...summary({ cartonGuid: "c-9", orderGuid: "o-9", orderNo: "2026-0500" }), remark: null, items: [], changedProductCode: null } satisfies PackCartonDetail,
  };
  assert.deepEqual(orderEntriesOf(cartonLookup), [{ orderGuid: "o-9", orderNo: "2026-0500" }]);
  assert.deepEqual(orderEntriesOf({ ...cartonLookup, canReceive: false }), [], "不是发给本店 / 不在途：不给入口");
  assert.deepEqual(orderEntriesOf({ canReceive: true, pallet: null, carton: null }), []);
});

test("按订单签收页路由在 /packing 前缀下，订单 GUID 要编码", () => {
  assert.equal(receiveOrderRoute("abc"), "/packing/receive-order?orderGuid=abc");
  assert.equal(receiveOrderRoute("a b/c"), "/packing/receive-order?orderGuid=a%20b%2Fc");
});

test("按订单签收响应：null 字段省略时补默认值，缺必填按接口失败", () => {
  const detail = normalizeOrderReceive({
    success: true,
    data: {
      orderGuid: "o-1",
      storeCode: "SUNNY",
      cartons: [{ cartonGuid: "c-1", cartonNo: "BX1", status: 3, skuCount: 2, pieceCount: 20 }],
      inTransitCount: 1,
      receivedCount: 0,
      products: [{ productCode: "P1", orderedPieces: 24, shippedPieces: 20 }],
    },
  });
  assert.equal(detail.orderNo, null);
  assert.equal(detail.cartons[0].palletNo, null);
  assert.equal(detail.products[0].allocPieces, null);
  assert.equal(detail.products[0].productName, null);
  assert.equal(detail.actualInTransitCartonCount, null);
  assert.throws(() => normalizeOrderReceive({ success: true, data: { storeCode: "SUNNY" } }), /Invalid warehouse packing response/);
});

test("订单条码 HBSO:订单号：前缀不分大小写，去掉空格与扫码枪控制符，订单号保持原样", () => {
  assert.equal(parseOrderBarcode("HBSO:2026-0418"), "2026-0418");
  assert.equal(parseOrderBarcode("hbso:2026-0418"), "2026-0418", "前缀小写也认");
  assert.equal(parseOrderBarcode("HbSo:SO-abc"), "SO-abc", "订单号不转大小写");
  assert.equal(parseOrderBarcode("  HBSO:2026-0418 \r\n"), "2026-0418", "首尾空格与回车去掉");
  assert.equal(parseOrderBarcode("HBSO: 2026-0418"), "2026-0418", "冒号后的空格去掉");
  assert.equal(parseOrderBarcode("\u0002HBSO:2026-0418\t"), "2026-0418", "扫码枪的控制字符去掉");
  assert.equal(parseOrderBarcode("HBSO:"), null, "前缀后面没有订单号");
  assert.equal(parseOrderBarcode("HBSO:   "), null);
  assert.equal(parseOrderBarcode("HBSO"), null);
  assert.equal(parseOrderBarcode("BX2611140003"), null, "箱标签不是订单条码");
  assert.equal(parseOrderBarcode("PL261114001"), null);
  assert.equal(parseOrderBarcode("HBSP:2026-0418/1/A"), null, "分单条码不是订单条码");
  assert.equal(parseOrderBarcode("2026-0418"), null, "没有前缀的订单号不当订单条码");
  assert.equal(parseOrderBarcode("XHBSO:2026-0418"), null, "前缀必须在开头");
  assert.equal(parseOrderBarcode(""), null);
  assert.equal(parseOrderBarcode(null), null);
  assert.equal(parseOrderBarcode(undefined), null);
});

test("订单号要先从条码里解析出来再传：路由里用后端返回的 orderGuid，缓存键也用它", () => {
  assert.deepEqual(orderReceiveKey("o-1"), ["packing", "orderReceive", "o-1"]);
  // 请求路径里的订单号要 URL 编码（订单号可能带 / 或空格）。
  assert.equal(encodeURIComponent(parseOrderBarcode("HBSO:SO 1/2") ?? ""), "SO%201%2F2");
});

test("待签收清单的订单入口：散箱取自己的订单，托盘取 orders（去重），没有订单不给入口", () => {
  const incoming = normalizeIncoming({
    pending: [
      { kind: "carton", guid: "c-1", no: "BX1", orderGuid: "o-1", orderNo: "2026-0412", cartonCount: 1, pieceCount: 5 },
      { kind: "carton", guid: "c-2", no: "BX2", cartonCount: 1, pieceCount: 5 },
      { kind: "pallet", guid: "p-1", no: "PL1", cartonCount: 3, pieceCount: 30, orders: [{ orderGuid: "o-1", orderNo: "2026-0412" }] },
      { kind: "pallet", guid: "p-2", no: "PL2", cartonCount: 3, pieceCount: 30, orders: [{ orderGuid: "o-1", orderNo: "2026-0412" }, { orderGuid: "O-1", orderNo: "2026-0412" }, { orderGuid: "o-2" }] },
      { kind: "pallet", guid: "p-3", no: "PL3", cartonCount: 3, pieceCount: 30 },
    ],
    recent: [{ kind: "pallet", guid: "p-9", no: "PL9", cartonCount: 2, receivedCartonCount: 2, pieceCount: 8, orders: [{ orderGuid: "o-7", orderNo: "2026-0300" }] }],
  });
  const [carton, looseNoOrder, onePallet, twoPallet, noOrderPallet] = incoming.pending;
  assert.deepEqual(incomingOrderEntries(carton), [{ orderGuid: "o-1", orderNo: "2026-0412" }]);
  assert.deepEqual(incomingOrderEntries(looseNoOrder), [], "没有订货单的散箱不显示入口");
  assert.deepEqual(incomingOrderEntries(onePallet), [{ orderGuid: "o-1", orderNo: "2026-0412" }], "只有一张订单：直接进入");
  assert.deepEqual(incomingOrderEntries(twoPallet), [
    { orderGuid: "o-1", orderNo: "2026-0412" },
    { orderGuid: "o-2", orderNo: null },
  ], "多张订单：先选；重复的订单只留一个");
  assert.deepEqual(incomingOrderEntries(noOrderPallet), []);
  assert.equal(noOrderPallet.orders.length, 0, "旧后端 / 没有订单关联：orders 缺失补成空数组");
  assert.equal(carton.orderGuid, "o-1");
  assert.equal(looseNoOrder.orderGuid, null);
  assert.deepEqual(incoming.recent[0].orders, [{ orderGuid: "o-7", orderNo: "2026-0300" }]);
});
