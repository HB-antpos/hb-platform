import assert from "node:assert/strict";
import { test } from "node:test";
import { normalizeOrderReceive, readPackingError } from "./api-normalization";
import {
  canReceiveOrder,
  clampCartonCount,
  countMismatchCounts,
  orderEntriesOf,
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
