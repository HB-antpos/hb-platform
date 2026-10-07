import assert from "node:assert/strict";
import {
  buildCashDepositListParams,
  buildCashExpenseListParams,
  normalizeCashContext,
  normalizeCashDaily,
  normalizeCashDepositDetail,
  normalizeCashDepositPage,
  normalizeCashExpenseDetail,
  normalizeCashSummary,
  normalizeCashUploadSignature,
} from "./api";

// ───────── context ─────────
{
  const context = normalizeCashContext({
    stores: [
      { storeCode: "S01", storeName: "Store 1", timeZoneId: "Australia/Sydney", storeToday: "2026-10-07" },
      { storeCode: "", storeName: "无编码应被丢弃", timeZoneId: "", storeToday: "" },
    ],
    capabilities: { canCreateDeposit: true, canCreateExpense: true, canViewAllStores: false, canVoid: false },
    dailyCloseConnected: false,
    t2VisibleDays: 14,
    maxBackfillDays: 7,
    selfVoidHours: 24,
    depositOverdueDays: 3,
    depositDifferenceReasonThreshold: 20,
    maxSlipsPerDeposit: 10,
    maxImagesPerSlip: 3,
    maxImagesPerExpense: 5,
  });
  assert.equal(context.stores.length, 1);
  assert.equal(context.stores[0].storeToday, "2026-10-07");
  assert.equal(context.dailyCloseConnected, false);
  assert.equal(context.capabilities.canCreateDeposit, true);
  assert.equal(context.capabilities.canVoid, false);
  assert.equal(context.maxSlipsPerDeposit, 10);
  assert.equal(context.depositDifferenceReasonThreshold, 20);

  // 缺字段时给出契约里的默认阈值，而不是 NaN / undefined
  const sparse = normalizeCashContext({});
  assert.deepEqual(sparse.stores, []);
  assert.equal(sparse.dailyCloseConnected, false);
  assert.equal(sparse.capabilities.canCreateDeposit, false, "能力缺失按无权限处理");
  assert.equal(sparse.maxBackfillDays, 7);
  assert.equal(sparse.maxImagesPerSlip, 3);
  assert.deepEqual(normalizeCashContext(null).stores, []);
}

// ───────── summary：日结未接入时保持 null，不变成 0 ─────────
{
  const summary = normalizeCashSummary({
    storeCode: "S01",
    storeName: "Store 1",
    asOfDate: "2026-10-07T00:00:00",
    dailyCloseConnected: false,
    openingMissing: true,
    opening: null,
    poolBalance: null,
    inflowTotal: null,
    depositTotal: 100,
    expenseTotal: "25.5",
    expenseByCategory: [{ category: "T2", amount: 10 }],
    t2Restricted: true,
    uncoveredDayCount: 0,
    oldestUncoveredDate: null,
    uncoveredCash: null,
    depositOverdue: false,
    suggestedDepositAmount: null,
    lastDepositDate: "2026-10-01",
    lastCount: null,
    missingCloseDates: ["2026-10-03"],
  });
  assert.equal(summary.poolBalance, null);
  assert.equal(summary.inflowTotal, null);
  assert.equal(summary.uncoveredCash, null);
  assert.equal(summary.suggestedDepositAmount, null);
  assert.equal(summary.openingMissing, true);
  assert.equal(summary.asOfDate, "2026-10-07", "带时间部分的日期只取日期");
  assert.equal(summary.expenseTotal, 25.5, "数字字符串转数字");
  assert.deepEqual(summary.expenseByCategory, [{ category: "T2", amount: 10 }]);
  assert.deepEqual(summary.missingCloseDates, ["2026-10-03"]);
  assert.equal(summary.poolBalance === 0, false);
}
{
  const withBalance = normalizeCashSummary({ poolBalance: 0, suggestedDepositAmount: 0 });
  assert.equal(withBalance.poolBalance, 0, "真实的 0 保持为 0");
  assert.equal(withBalance.suggestedDepositAmount, 0);
  assert.equal(normalizeCashSummary({ poolBalance: "" }).poolBalance, null, "空串视为 null");
}

// ───────── daily：最近营业日在前，存档从新到旧 ─────────
{
  const daily = normalizeCashDaily({
    storeCode: "S01",
    dailyCloseConnected: true,
    rows: [
      { businessDate: "2026-10-05", inflowCash: 100, hasClose: true, covered: true, coveredByDepositGuid: "d1", expenseTotal: 0, devices: [] },
      {
        businessDate: "2026-10-06",
        inflowCash: 250.5,
        hasClose: true,
        covered: false,
        coveredByDepositGuid: null,
        expenseTotal: 20,
        devices: [
          {
            deviceCode: "POS-1",
            selectionMode: "Manual",
            selectionStale: true,
            selectionOverlapWarning: true,
            selectionReason: "两班",
            selectedByName: "店长",
            selectedAtUtc: "2026-10-06T10:00:00",
            includedCash: 250.5,
            archives: [
              { closeId: "c1", savedAtUtc: "2026-10-06T08:00:00Z", periodFromUtc: "2026-10-06T00:00:00Z", periodToUtc: "2026-10-06T08:00:00Z", countedCash: 100, expectedCash: 100, variance: 0, included: true },
              { closeId: "c2", savedAtUtc: "2026-10-06T12:00:00Z", periodFromUtc: "2026-10-06T08:00:00Z", periodToUtc: "2026-10-06T12:00:00Z", countedCash: 150.5, expectedCash: 150, variance: 0.5, included: true },
            ],
          },
        ],
      },
    ],
  });
  assert.deepEqual(daily.rows.map((row) => row.businessDate), ["2026-10-06", "2026-10-05"]);
  const device = daily.rows[0].devices[0];
  assert.equal(device.selectionMode, "Manual");
  assert.equal(device.selectionStale, true);
  assert.equal(device.selectionOverlapWarning, true);
  assert.deepEqual(device.archives.map((archive) => archive.closeId), ["c2", "c1"], "新存档在前");
  assert.equal(device.selectedAtUtc, "2026-10-06T10:00:00Z", "没有时区标记的 UTC 时间补 Z，避免按本地时间解析而偏移");
  assert.equal(device.archives[0].savedAtUtc, "2026-10-06T12:00:00Z", "已带 Z 的不重复添加");
  assert.equal(daily.rows[0].coveredByDepositGuid, null);
  assert.equal(daily.rows[1].coveredByDepositGuid, "d1");
}

// ───────── 上传签名 ─────────
assert.deepEqual(
  normalizeCashUploadSignature({
    attachmentGuid: "att-1",
    url: "https://storage.example/put?sig=abc",
    headers: { "Content-Type": "image/jpeg", "x-amz-acl": "private", empty: "" },
    expiresAtUtc: "2026-10-07T05:00:00Z",
  }),
  {
    attachmentGuid: "att-1",
    url: "https://storage.example/put?sig=abc",
    headers: { "Content-Type": "image/jpeg", "x-amz-acl": "private" },
    expiresAtUtc: "2026-10-07T05:00:00Z",
  },
);

// ───────── 存款列表 / 详情 ─────────
{
  const page = normalizeCashDepositPage({
    items: [
      {
        depositGuid: "d1",
        storeCode: "S01",
        depositDate: "2026-10-06",
        coveredFromDate: "2026-10-01",
        coveredToDate: null,
        totalAmount: 1200,
        slipCount: 2,
        imageCount: 3,
        status: "Voided",
        note: "",
        createdByName: "店长",
        createdAtUtc: "2026-10-06T01:00:00Z",
        canVoid: false,
      },
    ],
    total: 1,
  });
  assert.equal(page.total, 1);
  assert.equal(page.items[0].status, "Voided");
  assert.equal(page.items[0].note, null, "空备注归一为 null");
  assert.equal(page.items[0].coveredToDate, null);
  assert.equal(normalizeCashDepositPage({ items: [{}, {}], total: 1 }).total, 2, "total 不会小于已返回条数");
  assert.deepEqual(normalizeCashDepositPage(null), { items: [], total: 0 });

  const detail = normalizeCashDepositDetail({
    depositGuid: "d1",
    status: "Active",
    totalAmount: 800,
    overrideReason: "多存备用金",
    slips: [
      {
        slipGuid: "s1",
        amount: 800,
        slipNo: "A1",
        attachments: [
          { attachmentGuid: "a2", url: "https://x/2", urlExpiresAtUtc: "2026-10-07T04:10:00Z", contentType: "image/jpeg", sortOrder: 2 },
          { attachmentGuid: "a1", url: "https://x/1", urlExpiresAtUtc: "2026-10-07T04:10:00Z", contentType: "image/jpeg", sortOrder: 1 },
          { attachmentGuid: "a3", url: "", urlExpiresAtUtc: "", contentType: "image/jpeg", sortOrder: 3 },
        ],
      },
    ],
  });
  assert.equal(detail.overrideReason, "多存备用金");
  assert.deepEqual(detail.slips[0].attachments.map((item) => item.attachmentGuid), ["a1", "a2"], "按 sortOrder 排序并丢弃没有地址的附件");
}

// ───────── 支出详情 ─────────
{
  const detail = normalizeCashExpenseDetail({
    expenseGuid: "e1",
    category: "T2",
    amount: "88.80",
    reviewStatus: "Flagged",
    status: "Active",
    imageCount: 1,
    canVoid: true,
    attachments: [{ attachmentGuid: "a1", url: "https://x/1", urlExpiresAtUtc: "2026-10-07T04:10:00", contentType: "image/jpeg", sortOrder: 0 }],
  });
  assert.equal(detail.category, "T2");
  assert.equal(detail.amount, 88.8);
  assert.equal(detail.reviewStatus, "Flagged");
  assert.equal(detail.canVoid, true);
  assert.equal(detail.attachments[0].urlExpiresAtUtc, "2026-10-07T04:10:00Z");
  assert.equal(normalizeCashExpenseDetail({ reviewStatus: "weird" }).reviewStatus, "None");
}

// ───────── 列表请求参数 ─────────
assert.deepEqual(
  buildCashDepositListParams({ storeCode: "S01", from: "2026-09-01", to: "2026-10-07", includeVoided: true, limit: 20, offset: 40 }),
  { storeCode: "S01", from: "2026-09-01", to: "2026-10-07", includeVoided: true, limit: 20, offset: 40 },
);
assert.deepEqual(buildCashDepositListParams({ storeCode: "S01", from: "", includeVoided: false }), {
  storeCode: "S01",
  from: undefined,
  to: undefined,
  includeVoided: undefined,
  limit: undefined,
  offset: undefined,
});
assert.equal(buildCashExpenseListParams({ storeCode: "S01", category: "T2" }).category, "T2");
assert.equal(buildCashExpenseListParams({ storeCode: "S01" }).category, undefined);

console.log("api-normalization.test.ts: ok");
