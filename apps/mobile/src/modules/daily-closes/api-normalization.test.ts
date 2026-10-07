import assert from "node:assert/strict";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { normalizeDailyCloseDetail, normalizeDailyCloseItem, normalizeDailyCloseListPage } from "./api-normalization";

const GUID = "6f1c2a52-0a4d-4b52-9a63-3a1f8f6d0001";

/** 后端 DailyCloseListItemDto（camelCase）的典型返回。 */
function rawItem(overrides: Record<string, unknown> = {}) {
  return {
    dailyCloseGuid: GUID,
    storeCode: "1008",
    storeName: "Bankstown",
    storeTimeZoneId: "Australia/Sydney",
    deviceCode: "POS01",
    clientKind: "Wpf",
    detailLevel: "Full",
    dataSource: "ClientUpload",
    businessDate: "2026-10-06",
    businessDateInferred: false,
    cashierId: "E1",
    cashierName: "Mei Lin",
    savedAtUtc: "2026-10-06T11:48:00Z",
    orderCount: 142,
    expectedCashAmount: 1842.3,
    countedCashAmount: 1838.8,
    cashDifference: -3.5,
    cardNetAmount: 5206.15,
    differenceKind: "short",
    saveSequence: 2,
    saveCountInDay: 3,
    ...overrides,
  };
}

// —— 列表项：典型 Full 记录 ——
assert.deepEqual(normalizeDailyCloseItem(rawItem()), {
  dailyCloseGuid: GUID,
  storeCode: "1008",
  storeName: "Bankstown",
  storeTimeZoneId: "Australia/Sydney",
  deviceCode: "POS01",
  clientKind: "Wpf",
  detailLevel: "Full",
  dataSource: "ClientUpload",
  businessDate: "2026-10-06",
  businessDateInferred: false,
  cashierId: "E1",
  cashierName: "Mei Lin",
  savedAtUtc: "2026-10-06T11:48:00Z",
  orderCount: 142,
  expectedCashAmount: 1842.3,
  countedCashAmount: 1838.8,
  cashDifference: -3.5,
  cardNetAmount: 5206.15,
  differenceKind: "short",
  saveSequence: 2,
  saveCountInDay: 3,
});

// 没有编号的记录无法进入详情也无法去重：丢弃
assert.equal(normalizeDailyCloseItem(rawItem({ dailyCloseGuid: "" })), null);
assert.equal(normalizeDailyCloseItem(rawItem({ dailyCloseGuid: undefined })), null);
assert.equal(normalizeDailyCloseItem(null), null);
assert.equal(normalizeDailyCloseItem("garbage"), null);
assert.equal(normalizeDailyCloseItem([]), null);

// null 金额是「无金额」，不是 0（0 是已平）
const trace = normalizeDailyCloseItem(
  rawItem({ detailLevel: "TraceOnly", dataSource: "AuditBackfill", expectedCashAmount: null, countedCashAmount: null, cashDifference: null, cardNetAmount: null, orderCount: null, differenceKind: "none" }),
)!;
assert.deepEqual([trace.expectedCashAmount, trace.countedCashAmount, trace.cashDifference, trace.cardNetAmount, trace.orderCount], [null, null, null, null, null]);
assert.equal(trace.differenceKind, "none");
assert.equal(trace.detailLevel, "TraceOnly");
assert.equal(trace.dataSource, "AuditBackfill");
const even = normalizeDailyCloseItem(rawItem({ cashDifference: 0, expectedCashAmount: 1120, countedCashAmount: 1120, differenceKind: "even" }))!;
assert.equal(even.cashDifference, 0, "0 保持 0");
assert.equal(even.differenceKind, "even");
const missingAmounts = normalizeDailyCloseItem(rawItem({ expectedCashAmount: undefined, countedCashAmount: "12.5", cashDifference: undefined }))!;
assert.deepEqual([missingAmounts.expectedCashAmount, missingAmounts.countedCashAmount, missingAmounts.cashDifference], [null, null, null], "缺失或字符串都不是金额");

// 状态只由差额符号决定，不信任接口字符串（金额、符号、颜色永远一致）
assert.equal(normalizeDailyCloseItem(rawItem({ cashDifference: 2, differenceKind: "short" }))!.differenceKind, "over");
assert.equal(normalizeDailyCloseItem(rawItem({ cashDifference: -1, differenceKind: "even" }))!.differenceKind, "short");
assert.equal(normalizeDailyCloseItem(rawItem({ cashDifference: null, differenceKind: "short" }))!.differenceKind, "none");

// 端类型规整大小写；未知值原样保留
assert.equal(normalizeDailyCloseItem(rawItem({ clientKind: "WPF" }))!.clientKind, "Wpf");
assert.equal(normalizeDailyCloseItem(rawItem({ clientKind: "handheld" }))!.clientKind, "Handheld");
assert.equal(normalizeDailyCloseItem(rawItem({ clientKind: "ipad" }))!.clientKind, "Ipad");
assert.equal(normalizeDailyCloseItem(rawItem({ clientKind: "Android" }))!.clientKind, "Android");
assert.equal(normalizeDailyCloseItem(rawItem({ clientKind: null }))!.clientKind, "");

// 明细级别与来源：大小写容错；未知明细级别按「不完整」处理
assert.equal(normalizeDailyCloseItem(rawItem({ detailLevel: "full" }))!.detailLevel, "Full");
assert.equal(normalizeDailyCloseItem(rawItem({ detailLevel: "CASHONLY" }))!.detailLevel, "CashOnly");
assert.equal(normalizeDailyCloseItem(rawItem({ detailLevel: "Mystery" }))!.detailLevel, "CashOnly", "未知级别且三项金额齐全 → 现金三项");
assert.equal(normalizeDailyCloseItem(rawItem({ detailLevel: "Mystery", cashDifference: null }))!.detailLevel, "TraceOnly", "未知级别且无金额 → 仅保存记录");
assert.equal(normalizeDailyCloseItem(rawItem({ detailLevel: undefined }))!.detailLevel, "CashOnly");
assert.equal(normalizeDailyCloseItem(rawItem({ dataSource: "auditbackfill" }))!.dataSource, "AuditBackfill");
assert.equal(normalizeDailyCloseItem(rawItem({ dataSource: "whatever" }))!.dataSource, "ClientUpload");

// 营业日兼容带时间部分；推算标记只认 true
assert.equal(normalizeDailyCloseItem(rawItem({ businessDate: "2026-10-06T00:00:00" }))!.businessDate, "2026-10-06");
assert.equal(normalizeDailyCloseItem(rawItem({ businessDate: null }))!.businessDate, "");
assert.equal(normalizeDailyCloseItem(rawItem({ businessDateInferred: true }))!.businessDateInferred, true);
assert.equal(normalizeDailyCloseItem(rawItem({ businessDateInferred: "true" }))!.businessDateInferred, false);

// 第 N 次：序号至少 1，总次数不小于序号
const seq = normalizeDailyCloseItem(rawItem({ saveSequence: undefined, saveCountInDay: undefined }))!;
assert.deepEqual([seq.saveSequence, seq.saveCountInDay], [1, 1]);
const seq2 = normalizeDailyCloseItem(rawItem({ saveSequence: 3, saveCountInDay: 1 }))!;
assert.deepEqual([seq2.saveSequence, seq2.saveCountInDay], [3, 3], "总次数不小于序号");
const seq3 = normalizeDailyCloseItem(rawItem({ saveSequence: 0, saveCountInDay: 0 }))!;
assert.deepEqual([seq3.saveSequence, seq3.saveCountInDay], [1, 1]);

// 门店名称 / 时区缺失：null；字符串去空白
const noStore = normalizeDailyCloseItem(rawItem({ storeName: null, storeTimeZoneId: "  " }))!;
assert.deepEqual([noStore.storeName, noStore.storeTimeZoneId], [null, null]);
assert.equal(normalizeDailyCloseItem(rawItem({ storeName: "  Bankstown  " }))!.storeName, "Bankstown");

// —— 列表页 ——
const page = normalizeDailyCloseListPage({
  items: [rawItem(), rawItem({ dailyCloseGuid: "" }), null, rawItem({ dailyCloseGuid: "6f1c2a52-0a4d-4b52-9a63-3a1f8f6d0002" })],
  total: 58,
  page: 2,
  pageSize: 30,
  counts: { all: 58, short: 9, over: 4, even: 43, none: 2 },
  totals: { expectedCash: 71356.2, countedCash: 71314.7, difference: -41.5 },
});
assert.equal(page.items.length, 2, "无编号与空行被丢弃");
assert.deepEqual([page.total, page.page, page.pageSize], [58, 2, 30]);
assert.deepEqual(page.counts, { all: 58, short: 9, over: 4, even: 43, none: 2 });
assert.deepEqual(page.totals, { expectedCash: 71356.2, countedCash: 71314.7, difference: -41.5 });

for (const garbage of [null, undefined, "x", 12, [], {}]) {
  const empty = normalizeDailyCloseListPage(garbage);
  assert.deepEqual(empty.items, []);
  assert.deepEqual([empty.total, empty.page, empty.pageSize], [0, 1, 0], "缺字段落到安全默认值，页码至少 1");
  assert.deepEqual(empty.counts, { all: 0, short: 0, over: 0, even: 0, none: 0 });
  assert.deepEqual(empty.totals, { expectedCash: 0, countedCash: 0, difference: 0 });
}
assert.deepEqual(normalizeDailyCloseListPage({ items: "nope", counts: [], totals: "x" }).items, [], "items 不是数组");

// —— 响应信封：ApiResponse<T> 解包后归一化 ——
const envelope = {
  success: true,
  isSuccess: true,
  message: "操作成功",
  data: { items: [rawItem()], total: 1, page: 1, pageSize: 30, counts: { all: 1, short: 1, over: 0, even: 0, none: 0 }, totals: { expectedCash: 1842.3, countedCash: 1838.8, difference: -3.5 } },
  errorCode: null,
  code: null,
  details: null,
  timestamp: "2026-10-07T10:00:00Z",
};
const viaEnvelope = normalizeDailyCloseListPage(unwrapApiEnvelope(envelope));
assert.equal(viaEnvelope.total, 1);
assert.equal(viaEnvelope.items[0].dailyCloseGuid, GUID);
assert.equal(normalizeDailyCloseListPage(unwrapApiEnvelope(envelope.data)).total, 1, "兼容直接返回数据");

// —— 明细 ——
const rawDetail = {
  ...rawItem(),
  periodFromUtc: "2026-10-05T20:00:00Z",
  periodToUtc: "2026-10-06T11:45:00Z",
  appVersion: "1.0.47",
  returnQuantity: 6,
  refundAmount: 177.5,
  tenders: [
    { method: "Cash", salesAmount: 1905.8, refundAmount: 63.5, netAmount: 1842.3 },
    { method: "Card", salesAmount: 5320.15, refundAmount: 114, netAmount: 5206.15 },
    { method: "Voucher", salesAmount: 85, refundAmount: 0, netAmount: 85 },
    { method: "GiftCard", salesAmount: 10, refundAmount: 0, netAmount: 10 },
    { method: "", salesAmount: 1, refundAmount: 0, netAmount: 1 },
    null,
  ],
  cashCounts: [
    { denominationCents: 10000, quantity: 8, subtotalAmount: 800, kind: "Note" },
    { denominationCents: 5, quantity: 0, subtotalAmount: 0, kind: "Coin" },
    { denominationCents: 200, quantity: 3, subtotalAmount: 6 },
    { denominationCents: 500, quantity: 2, subtotalAmount: 10, kind: "weird" },
    { denominationCents: 0, quantity: 1, subtotalAmount: 0, kind: "Coin" },
    { denominationCents: -100, quantity: 1, subtotalAmount: 1, kind: "Coin" },
    { denominationCents: "100", quantity: 1, subtotalAmount: 1, kind: "Coin" },
    { denominationCents: 100, quantity: -4, subtotalAmount: 0, kind: "Coin" },
  ],
  noteSubtotal: 1815,
  coinSubtotal: 23.8,
  receivedAtUtc: "2026-10-06T11:49:00Z",
};
const detail = normalizeDailyCloseDetail(rawDetail)!;
assert.equal(detail.dailyCloseGuid, GUID);
assert.equal(detail.differenceKind, "short");
assert.deepEqual([detail.periodFromUtc, detail.periodToUtc, detail.appVersion, detail.returnQuantity, detail.refundAmount], ["2026-10-05T20:00:00Z", "2026-10-06T11:45:00Z", "1.0.47", 6, 177.5]);
assert.deepEqual(detail.tenders.map((tender) => tender.method), ["Cash", "Card", "Voucher", "GiftCard"], "无方式名的行与空行丢弃，未知方式保留");
assert.deepEqual(detail.tenders[0], { method: "Cash", salesAmount: 1905.8, refundAmount: 63.5, netAmount: 1842.3 });
assert.deepEqual(
  detail.cashCounts.map((row) => [row.denominationCents, row.quantity, row.kind]),
  [
    [10000, 8, "Note"],
    [5, 0, "Coin"],
    [200, 3, "Coin"],
    [500, 2, "Note"],
    [100, 0, "Coin"],
  ],
  "面额无效的行丢弃；缺类型按 5 元为界推断；数量不为负；0 张保留",
);
assert.deepEqual([detail.noteSubtotal, detail.coinSubtotal], [1815, 23.8]);
assert.equal(detail.receivedAtUtc, "2026-10-06T11:49:00Z");

// 补录明细：数组为空、可空字段为 null
const backfilled = normalizeDailyCloseDetail({
  ...rawItem({ detailLevel: "CashOnly", dataSource: "AuditBackfill", businessDateInferred: true }),
  periodFromUtc: null,
  periodToUtc: null,
  appVersion: null,
  returnQuantity: null,
  refundAmount: null,
  tenders: [],
  cashCounts: [],
  noteSubtotal: null,
  coinSubtotal: null,
  receivedAtUtc: "2026-10-07T01:00:00Z",
})!;
assert.deepEqual([backfilled.tenders, backfilled.cashCounts], [[], []]);
assert.deepEqual([backfilled.periodFromUtc, backfilled.appVersion, backfilled.returnQuantity, backfilled.refundAmount, backfilled.noteSubtotal, backfilled.coinSubtotal], [null, null, null, null, null, null]);
assert.equal(backfilled.businessDateInferred, true);

// 缺字段 / 垃圾输入不抛错
const bare = normalizeDailyCloseDetail({ dailyCloseGuid: GUID })!;
assert.deepEqual([bare.tenders, bare.cashCounts, bare.receivedAtUtc, bare.savedAtUtc], [[], [], "", ""]);
assert.equal(bare.differenceKind, "none");
assert.equal(normalizeDailyCloseDetail({}), null, "没有编号不是有效明细");
assert.equal(normalizeDailyCloseDetail(null), null);
assert.equal(normalizeDailyCloseDetail(undefined), null);
assert.equal(normalizeDailyCloseDetail({ dailyCloseGuid: GUID, tenders: "x", cashCounts: 5 })!.tenders.length, 0);

console.log("daily-closes api-normalization.test.ts: ok");
