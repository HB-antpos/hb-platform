import assert from "node:assert/strict";
import test from "node:test";

import { cashMovementFixture } from "../../testing/cash-movement-fixtures";

import {
  CASH_MOVEMENT_SYNC_SCHEMA_VERSION,
  CashMovementSyncMappingError,
  buildCashMovementSyncRequest,
  cashMovementSyncRequestBytes,
  fitCashMovementBatch,
  mapCashMovementToSyncItem,
} from "./cash-movement-sync-request";

const options = { storeCode: "S001", deviceCode: "DEV-1", clientKind: "Ipad" } as const;

test("映射：金额整数分 → 元（无浮点误差）、时刻统一 UTC ISO、可空字段原样", () => {
  const item = mapCashMovementToSyncItem(
    cashMovementFixture({
      amountCents: 1_999_99,
      note: "float",
      occurredAtIso: "2026-11-14T12:30:00+10:00",
      authorizedById: "m1",
      authorizedByName: "Manager",
    }),
  );
  assert.deepEqual(item, {
    movementGuid: "6f9619ff-8b86-4011-b42d-00c04fc964aa",
    direction: "In",
    reasonCode: "ChangeTopUp",
    amount: 1999.99,
    note: "float",
    voidsMovementGuid: null,
    businessDate: "2026-11-14",
    occurredAt: "2026-11-14T02:30:00.000Z",
    cashierId: "cashier-1",
    cashierName: "Cashier One",
    authorizedById: "m1",
    authorizedByName: "Manager",
    displayNumber: "CM-0001",
  });
  assert.equal(mapCashMovementToSyncItem(cashMovementFixture({ amountCents: 1 })).amount, 0.01);
  assert.equal(mapCashMovementToSyncItem(cashMovementFixture({ amountCents: 100_000_000 })).amount, 1_000_000);
});

test("映射：冲正带 voidsMovementGuid 与相反方向；数据无效抛 CashMovementSyncMappingError", () => {
  const item = mapCashMovementToSyncItem(
    cashMovementFixture({
      direction: "Out",
      voidsMovementGuid: "11111111-1111-4111-8111-111111111111",
    }),
  );
  assert.equal(item.direction, "Out");
  assert.equal(item.voidsMovementGuid, "11111111-1111-4111-8111-111111111111");
  for (const bad of [
    cashMovementFixture({ occurredAtIso: "garbage" }),
    cashMovementFixture({ businessDate: "14/11/2026" }),
    cashMovementFixture({ amountCents: 1.5 }),
  ]) {
    assert.throws(() => mapCashMovementToSyncItem(bad), CashMovementSyncMappingError);
  }
});

test("请求：schemaVersion 1、店 / 设备 / clientKind、appVersion 截断到 64", () => {
  const request = buildCashMovementSyncRequest([mapCashMovementToSyncItem(cashMovementFixture())], {
    ...options,
    appVersion: ` ${"v".repeat(80)} `,
  });
  assert.equal(request.schemaVersion, CASH_MOVEMENT_SYNC_SCHEMA_VERSION);
  assert.equal(request.storeCode, "S001");
  assert.equal(request.deviceCode, "DEV-1");
  assert.equal(request.clientKind, "Ipad");
  assert.equal(request.appVersion?.length, 64);
  assert.equal(request.movements?.length, 1);
  assert.equal(buildCashMovementSyncRequest([], options).appVersion, null);
});

test("拆批：条数 <= 100；字节 <= 上限；至少 1 条；保持前缀顺序", () => {
  const items = Array.from({ length: 150 }, (_, index) =>
    mapCashMovementToSyncItem(
      cashMovementFixture({ movementGuid: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}` }),
    ),
  );
  assert.equal(fitCashMovementBatch(items, options), 100);
  assert.equal(fitCashMovementBatch(items.slice(0, 7), options), 7);
  assert.equal(fitCashMovementBatch([], options), 0);

  // 字节上限：用一个刚好容纳 3 条的上限。
  const three = cashMovementSyncRequestBytes(buildCashMovementSyncRequest(items.slice(0, 3), options));
  const four = cashMovementSyncRequestBytes(buildCashMovementSyncRequest(items.slice(0, 4), options));
  assert.ok(four > three);
  assert.equal(fitCashMovementBatch(items, options, { maximumBytes: three }), 3);
  assert.equal(fitCashMovementBatch(items, options, { maximumBytes: four - 1 }), 3);
  assert.equal(fitCashMovementBatch(items, options, { maximumBytes: four }), 4);
  // 单条就超限也至少发 1 条（交给服务端 413 永久拒绝）。
  assert.equal(fitCashMovementBatch(items, options, { maximumBytes: 10 }), 1);
  // 条数上限参数不能超过 100。
  assert.equal(fitCashMovementBatch(items, options, { maximumCount: 500 }), 100);
  assert.equal(fitCashMovementBatch(items, options, { maximumCount: 5 }), 5);
});

test("256KB 默认上限下，100 条真实大小的请求远小于上限", () => {
  const items = Array.from({ length: 100 }, (_, index) =>
    mapCashMovementToSyncItem(
      cashMovementFixture({
        movementGuid: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
        note: "n".repeat(200),
      }),
    ),
  );
  const bytes = cashMovementSyncRequestBytes(buildCashMovementSyncRequest(items, options));
  assert.ok(bytes < 256 * 1024, String(bytes));
});
