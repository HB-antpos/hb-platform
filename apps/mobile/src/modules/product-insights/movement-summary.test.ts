import assert from "node:assert/strict";
import { test } from "node:test";
import {
  buildDailyFlow,
  buildMovementSummary,
  buildMovementTrend,
  buildPresetRange,
  buildPurchaseBatches,
  countRangeDays,
  resolvePeriodRange,
} from "./movement-summary";
import type { StoreProductInsight } from "./types";

function sale(date: string, quantity: number) {
  return { date: `${date}T00:00:00`, quantity, amount: quantity * 5.9 };
}

function purchase(id: string, date: string, quantity: number) {
  return {
    id,
    date: `${date}T00:00:00`,
    documentNo: `INV${id}`,
    quantity,
    supplierName: "Ausway",
  };
}

// 设计稿样例：近 90 天 4 批进货，批次间销售 5 / 39 / 39 / 33 / 15 件。
const sales = [
  sale("2026-08-03", 5),
  sale("2026-08-20", 39),
  sale("2026-09-10", 39),
  sale("2026-10-05", 33),
  sale("2026-10-21", 1),
  sale("2026-10-22", 2),
  sale("2026-10-23", 1),
  sale("2026-10-24", 2),
  sale("2026-10-25", 2),
  sale("2026-10-26", 0),
  sale("2026-10-27", 3),
  sale("2026-10-29", 1),
  sale("2026-10-30", 3),
];

const local: StoreProductInsight = {
  range: { startDate: "2026-08-02", endDate: "2026-10-30" },
  generatedAt: "2026-10-30T03:26:00Z",
  salesStatisticLastUpdatedAt: null,
  store: { storeCode: "1020", storeName: "Wallsend" },
  product: {
    productCode: "P1",
    productName: "Container",
    itemNumber: "HB204-118",
    barcode: null,
    productImage: null,
    localSupplierCode: null,
    localSupplierName: null,
  },
  sourceType: "local",
  sales: {
    quantity: 131,
    amount: sales.reduce((sum, item) => sum + item.amount, 0),
    records: sales,
  },
  purchases: {
    quantity: 156,
    documentCount: 4,
    records: [
      purchase("4", "2026-10-21", 24),
      purchase("3", "2026-09-29", 48),
      purchase("2", "2026-09-02", 36),
      purchase("1", "2026-08-06", 48),
    ],
    lastRecord: null,
  },
  warehouse: {
    orderedQuantity: 0,
    deliveredQuantity: 0,
    orders: [],
    deliveries: [],
    lastDelivery: null,
  },
};

test("汇总：售罄率、进销差、日均、平均售价、距上次进货", () => {
  const summary = buildMovementSummary(local);
  assert.equal(summary.rangeDays, 90);
  assert.equal(summary.inQuantity, 156);
  assert.equal(summary.inDocumentCount, 4);
  assert.equal(summary.lastInDate, "2026-10-21");
  assert.equal(summary.daysSinceLastIn, 9);
  assert.equal(summary.netQuantity, 25);
  assert.equal(summary.sellThroughRate?.toFixed(3), "0.840");
  assert.equal(summary.dailyAverage.toFixed(2), "1.46");
  assert.equal(summary.averagePrice?.toFixed(2), "5.90");
  // 销量为 0 的日记录不算动销天数。
  assert.equal(summary.activeSalesDays, 12);
});

test("汇总：区间无进货时回退到历史记录，售罄率为空", () => {
  const summary = buildMovementSummary({
    ...local,
    purchases: {
      quantity: 0,
      documentCount: 0,
      records: [],
      lastRecord: purchase("0", "2026-05-01", 12),
    },
  });
  assert.equal(summary.sellThroughRate, null);
  assert.equal(summary.lastInDate, "2026-05-01");
  assert.equal(summary.lastInIsHistorical, true);
  assert.equal(summary.netQuantity, -131);
});

test("批次：按到货日切分销售，各组合计等于区间销售", () => {
  const batches = buildPurchaseBatches(local);
  assert.deepEqual(
    batches.map((item) => [item.date, item.salesQuantity, item.days]),
    [
      ["2026-10-21", 15, 10],
      ["2026-09-29", 33, 22],
      ["2026-09-02", 39, 27],
      ["2026-08-06", 39, 27],
      [null, 5, 4],
    ],
  );
  assert.equal(
    batches.reduce((sum, item) => sum + item.salesQuantity, 0),
    local.sales.quantity,
  );
  assert.equal(batches[0].latest, true);
  assert.equal(batches[0].salesDays, 8);
  // 销量为 0 的日记录（26/10）不进批次的每日销售。
  assert.equal(batches[0].sales.length, 8);
  assert.equal(batches[0].sellThroughRate, 15 / 24);
  assert.equal(batches[2].sellThroughRate, 39 / 36);
  assert.equal(batches[4].sellThroughRate, null);
  // 批次内每日销售新日期在前。
  assert.equal(batches[0].sales[0].date.slice(0, 10), "2026-10-30");
});

test("批次：同日多张进货单合成一批；无进货时全部销售归一组", () => {
  const merged = buildPurchaseBatches({
    ...local,
    purchases: {
      ...local.purchases,
      records: [
        purchase("a", "2026-08-02", 10),
        purchase("b", "2026-08-02", 6),
      ],
    },
  });
  assert.equal(merged.length, 1);
  assert.equal(merged[0].inQuantity, 16);
  assert.equal(merged[0].documents.length, 2);
  assert.equal(merged[0].salesQuantity, 131);

  const none = buildPurchaseBatches({
    ...local,
    purchases: { ...local.purchases, records: [] },
  });
  assert.equal(none.length, 1);
  assert.equal(none[0].date, null);
  assert.equal(none[0].salesQuantity, 131);
});

test("趋势：90 天按 7 天分 13 桶，最后一桶不足 7 天标记 partial", () => {
  const trend = buildMovementTrend(local);
  assert.equal(trend.bucketDays, 7);
  assert.equal(trend.buckets.length, 13);
  assert.equal(trend.buckets[0].inQuantity, 48);
  assert.equal(trend.buckets[0].salesQuantity, 5);
  assert.equal(trend.buckets[0].startDate, "2026-08-02");
  assert.equal(trend.buckets[12].endDate, "2026-10-30");
  assert.equal(trend.buckets[12].partial, true);
  const last = trend.buckets[12];
  assert.equal(last.cumulativeIn, 156);
  assert.equal(last.cumulativeSales, 131);
});

test("趋势：长区间放宽桶宽，柱数不超过 26", () => {
  const trend = buildMovementTrend({
    ...local,
    range: { startDate: "2025-10-31", endDate: "2026-10-30" },
  });
  assert.equal(trend.bucketDays, 21);
  assert.equal(trend.buckets.length, 18);
});

test("预设周期以结束日倒推，含首尾", () => {
  const range = buildPresetRange("2026-10-30", 30);
  assert.deepEqual(range, { startDate: "2026-10-01", endDate: "2026-10-30" });
  assert.equal(countRangeDays(range), 30);
});

test("请求区间：默认 90 天交给后端，其他预设按锚点倒推", () => {
  assert.equal(resolvePeriodRange({ kind: "preset", days: 90 }, "2026-10-30"), undefined);
  // 还没有锚点时不猜设备日期，退回后端默认。
  assert.equal(resolvePeriodRange({ kind: "preset", days: 30 }, null), undefined);
  assert.deepEqual(resolvePeriodRange({ kind: "preset", days: 180 }, "2026-10-30"), {
    startDate: "2026-05-04",
    endDate: "2026-10-30",
  });
  const custom = { startDate: "2026-01-01", endDate: "2026-01-31" };
  assert.equal(resolvePeriodRange({ kind: "custom", range: custom }, null), custom);
});

const warehouse: StoreProductInsight = {
  ...local,
  sourceType: "warehouse",
  sales: { quantity: 3, amount: 10.5, records: [sale("2026-10-20", 3)] },
  purchases: { ...local.purchases, quantity: 0, documentCount: 0, records: [] },
  warehouse: {
    orderedQuantity: 72,
    deliveredQuantity: 48,
    orders: [
      {
        id: "O2",
        date: "2026-10-19T00:00:00",
        documentNo: "SO2",
        quantity: 24,
        deliveredQuantity: 0,
        deliveryDate: null,
        status: "submitted",
      },
      {
        id: "O1",
        date: "2026-09-24T00:00:00",
        documentNo: "SO1",
        quantity: 48,
        deliveredQuantity: 48,
        deliveryDate: "2026-09-26T00:00:00",
        status: "completed",
      },
    ],
    deliveries: [purchase("D1", "2026-09-26", 48)],
    lastDelivery: null,
  },
};

test("仓库商品：进取配货，待配数量与配货满足率来自订单", () => {
  const summary = buildMovementSummary(warehouse);
  assert.equal(summary.inQuantity, 48);
  assert.equal(summary.pendingQuantity, 24);
  assert.equal(summary.pendingOrderCount, 1);
  assert.equal(summary.earliestPendingDate, "2026-10-19");
  assert.equal(summary.fulfillmentRate, 48 / 72);
  assert.equal(summary.daysSinceLastIn, 34);
});

test("按日期：混排订货、配货、销售并按月小计，可按类型筛选", () => {
  const all = buildDailyFlow(warehouse, "all");
  assert.deepEqual(
    all.map((month) => [month.month, month.entries.map((item) => item.type)]),
    [
      ["2026-10", ["sale", "order"]],
      ["2026-09", ["in", "order"]],
    ],
  );
  assert.equal(all[0].orderQuantity, 24);
  assert.equal(all[0].salesQuantity, 3);
  assert.equal(all[1].inQuantity, 48);

  const onlySales = buildDailyFlow(local, "sales");
  assert.ok(onlySales.every((month) => month.inQuantity === 0));
  // 销量为 0 的日记录不进流水。
  assert.equal(
    onlySales.flatMap((month) => month.entries).length,
    local.sales.records.length - 1,
  );
  const onlyIn = buildDailyFlow(local, "in");
  assert.equal(onlyIn.flatMap((month) => month.entries).length, 4);
});
