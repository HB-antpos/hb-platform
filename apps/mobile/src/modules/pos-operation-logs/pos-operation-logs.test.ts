import assert from "node:assert/strict";
import { test } from "node:test";
import {
  normalizePosOperationLogDetail,
  normalizePosOperationLogPage,
  normalizePosOperationLogSummary,
} from "./api-normalization";
import {
  POS_OPERATION_LOG_MAX_RANGE_DAYS,
  POS_OPERATION_TYPE_GROUPS,
  POS_OPERATION_TYPES,
  applyDeepLinkParams,
  applyQuickFilter,
  buildMoneyRows,
  buildPosOperationLogQueryParams,
  canViewPosOperationLogs,
  countActivePosOperationFilters,
  createDefaultPosOperationLogFilters,
  describeRowSummary,
  formatMoney,
  formatSignedMoney,
  groupPosOperationLogsByDay,
  operationTypeI18nKey,
  reportingDelaySeconds,
  resolvePosOperationRange,
  resolveQuickFilter,
  resolveRowAmount,
  shortenIdentifier,
  validateCustomRange,
} from "./logic";
import type { PosOperationLogItem } from "./types";

const now = new Date(2026, 8, 17, 14, 32, 5); // 本地 2026-09-17 14:32:05

function item(overrides: Partial<PosOperationLogItem> = {}): PosOperationLogItem {
  return {
    eventId: "e1",
    occurredAtUtc: new Date(2026, 8, 17, 10, 0, 0).toISOString(),
    receivedAtUtc: new Date(2026, 8, 17, 10, 0, 2).toISOString(),
    operationType: "SALE_COMPLETE",
    outcome: "Succeeded",
    cashierId: "C0231",
    userGuid: null,
    cashierName: "王小明",
    isOfflineCached: false,
    isEmergencyOverride: false,
    storeCode: "CHAT",
    deviceCode: "POS-CHAT-02",
    deviceSystem: "Windows",
    appVersion: "3.14.2",
    orderGuid: "8f3a1c2d-0000-4000-8000-00000000c2d1",
    receiptNumber: "R2609170412",
    correlationId: null,
    traceId: null,
    paymentMethod: "Card",
    reasonCode: null,
    safeMessage: null,
    currencyCode: "AUD",
    paymentAmount: null,
    beforeGross: 520,
    afterGross: 400,
    beforeDiscount: 0,
    afterDiscount: 0,
    beforeActual: 520,
    afterActual: 400,
    amountDelta: -120,
    productCount: 4,
    primaryProduct: "Dyson V12",
    ...overrides,
  };
}

test("权限：只认统一的审计查看权限码，别名归一交给 access 层", () => {
  assert.equal(canViewPosOperationLogs(true, (p) => p === "Permissions.PosTerminal.Audit.View"), true);
  assert.equal(canViewPosOperationLogs(true, (p) => p === "PosTerminal.Audit.View"), false);
  assert.equal(canViewPosOperationLogs(true, () => false), false);
  assert.equal(canViewPosOperationLogs(false, () => true), false);
});

test("操作类型文案键映射与清单完整性", () => {
  assert.equal(operationTypeI18nKey("CART_ITEM_ADD"), "cartItemAdd");
  assert.equal(operationTypeI18nKey("LINKLY_SETTLEMENT_REPRINT"), "linklySettlementReprint");
  assert.equal(operationTypeI18nKey("CARD_PAYMENT_SUPERVISOR_RESOLUTION"), "cardPaymentSupervisorResolution");
  // 与 Web 端 OPERATION_TYPE_KEYS 的 34 种保持一致。
  assert.equal(POS_OPERATION_TYPES.length, 34);
  assert.equal(new Set(POS_OPERATION_TYPES).size, 34);
  assert.equal(operationTypeI18nKey("API_SERVER_CHANGE"), "apiServerChange");
  // 高风险组在筛选面板默认展开，主管授权必须落在其中。
  assert.ok(POS_OPERATION_TYPE_GROUPS[0].types.includes("PERMISSION_OVERRIDE"));
});

test("预设区间按设备本地日推导", () => {
  const today = resolvePosOperationRange({ preset: "today", startDate: "", endDate: "" }, now)!;
  assert.equal(today.fromUtc, new Date(2026, 8, 17, 0, 0, 0).toISOString());
  assert.equal(today.toUtc, now.toISOString());
  assert.equal(today.startDate, "2026-09-17");

  const yesterday = resolvePosOperationRange({ preset: "yesterday", startDate: "", endDate: "" }, now)!;
  assert.equal(yesterday.fromUtc, new Date(2026, 8, 16, 0, 0, 0).toISOString());
  assert.equal(yesterday.toUtc, new Date(new Date(2026, 8, 17, 0, 0, 0).getTime() - 1).toISOString());

  const week = resolvePosOperationRange({ preset: "last7Days", startDate: "", endDate: "" }, now)!;
  assert.equal(week.startDate, "2026-09-11");
  assert.equal(week.endDate, "2026-09-17");

  const custom = resolvePosOperationRange(
    { preset: "custom", startDate: "2026-09-01", endDate: "2026-09-03" },
    now,
  )!;
  assert.equal(custom.fromUtc, new Date(2026, 8, 1, 0, 0, 0).toISOString());
  assert.equal(custom.toUtc, new Date(new Date(2026, 8, 4, 0, 0, 0).getTime() - 1).toISOString());
  assert.equal(
    resolvePosOperationRange({ preset: "custom", startDate: "2026-02-30", endDate: "2026-03-01" }, now),
    null,
  );
});

test("自定义区间校验：格式、顺序与上限", () => {
  assert.deepEqual(validateCustomRange("2026-9-1", "2026-09-02"), { ok: false, dayCount: 0, reason: "format" });
  assert.deepEqual(validateCustomRange("2026-09-03", "2026-09-02"), { ok: false, dayCount: 0, reason: "order" });
  assert.equal(validateCustomRange("2026-09-01", "2026-09-01").dayCount, 1);
  const tooLong = validateCustomRange("2026-01-01", "2026-12-31");
  assert.equal(tooLong.ok, false);
  assert.equal(tooLong.reason, "tooLong");
  assert.equal(tooLong.dayCount > POS_OPERATION_LOG_MAX_RANGE_DAYS, true);
});

test("查询参数只包含非空条件，布尔过滤仅在开启时发送", () => {
  const base = createDefaultPosOperationLogFilters(now);
  const params = buildPosOperationLogQueryParams(base, now)!;
  assert.deepEqual(Object.keys(params).sort(), ["fromUtc", "sortBy", "sortOrder", "toUtc"]);

  const rich = buildPosOperationLogQueryParams(
    {
      ...base,
      storeCode: "CHAT",
      cashierKeyword: "  王 ",
      deviceCode: "POS-1",
      deviceSystem: "iPadOS",
      operationType: "SALE_VOID",
      outcome: "Denied",
      emergencyOverrideOnly: true,
      offlineCachedOnly: false,
      productKeyword: "dyson",
      orderGuid: "abc",
      keyword: "R26",
    },
    now,
  )!;
  assert.equal(rich.cashierKeyword, "王");
  assert.equal(rich.isEmergencyOverride, true);
  assert.equal("isOfflineCached" in rich, false);
  assert.equal(rich.outcome, "Denied");
  assert.equal(rich.deviceSystem, "iPadOS");
  assert.equal(buildPosOperationLogQueryParams({ ...base, preset: "custom", startDate: "x", endDate: "y" }, now), null);
});

test("筛选角标计数与快捷过滤互斥", () => {
  const base = createDefaultPosOperationLogFilters(now);
  assert.equal(countActivePosOperationFilters(base), 0);
  assert.equal(countActivePosOperationFilters({ ...base, preset: "last7Days", storeCode: "A", keyword: "x" }), 3);

  assert.equal(resolveQuickFilter(base), "all");
  const denied = applyQuickFilter({ ...base, emergencyOverrideOnly: true }, "Denied");
  assert.equal(denied.outcome, "Denied");
  assert.equal(denied.emergencyOverrideOnly, false);
  const emergency = applyQuickFilter(denied, "emergencyOverride");
  assert.equal(emergency.outcome, null);
  assert.equal(emergency.emergencyOverrideOnly, true);
  assert.equal(resolveQuickFilter(emergency), "emergencyOverride");
  assert.equal(resolveQuickFilter(applyQuickFilter(emergency, "all")), "all");
});

test("按本地日分组并标注今天/昨天", () => {
  const sections = groupPosOperationLogsByDay(
    [
      item({ eventId: "a", occurredAtUtc: new Date(2026, 8, 17, 14, 0).toISOString() }),
      item({ eventId: "b", occurredAtUtc: new Date(2026, 8, 17, 9, 0).toISOString() }),
      item({ eventId: "c", occurredAtUtc: new Date(2026, 8, 16, 21, 0).toISOString() }),
      item({ eventId: "d", occurredAtUtc: new Date(2026, 8, 10, 21, 0).toISOString() }),
    ],
    now,
  );
  assert.deepEqual(
    sections.map((section) => [section.dayKey, section.relative, section.dateLabel, section.items.length]),
    [
      ["2026-09-17", "today", "17/09/2026", 2],
      ["2026-09-16", "yesterday", "16/09/2026", 1],
      ["2026-09-10", null, "10/09/2026", 1],
    ],
  );
});

test("金额格式：负号、符号与空值", () => {
  assert.equal(formatMoney(1248, "AUD"), "A$1,248.00");
  assert.equal(formatMoney(-120, "AUD"), "−A$120.00");
  assert.equal(formatMoney(null), "—");
  assert.equal(formatSignedMoney(86.5), "+A$86.50");
  assert.equal(formatSignedMoney(0), "A$0.00");
  assert.equal(formatSignedMoney(-1), "−A$1.00");
  assert.equal(formatMoney(5, "NZD"), "NZD 5.00");
});

test("行摘要与金额优先级", () => {
  assert.equal(resolveRowAmount(item()), -120);
  assert.equal(resolveRowAmount(item({ amountDelta: null, paymentAmount: 86.5 })), 86.5);
  assert.equal(resolveRowAmount(item({ amountDelta: null, paymentAmount: null, afterActual: null })), null);

  assert.deepEqual(describeRowSummary(item({ outcome: "Denied", reasonCode: "PRICE_BELOW_COST", safeMessage: "低于成本" })), {
    kind: "reason",
    reasonCode: "PRICE_BELOW_COST",
    message: "低于成本",
  });
  // 成功事件即使带 reasonCode 也优先看商品。
  assert.deepEqual(describeRowSummary(item({ reasonCode: "X" })), { kind: "product", name: "Dyson V12", extraCount: 3 });
  assert.deepEqual(describeRowSummary(item({ primaryProduct: null, productCount: 0 })), {
    kind: "receipt",
    receiptNumber: "R2609170412",
    paymentMethod: "Card",
  });
  assert.deepEqual(describeRowSummary(item({ primaryProduct: null, receiptNumber: null, paymentMethod: null })), { kind: "none" });
});

test("详情：金额表、上报延迟与标识缩写", () => {
  const rows = buildMoneyRows(item());
  assert.deepEqual(rows.map((row) => [row.key, row.before, row.after, row.delta]), [
    ["gross", 520, 400, -120],
    ["discount", 0, 0, 0],
    ["actual", 520, 400, -120],
  ]);
  assert.deepEqual(
    buildMoneyRows(item({ beforeGross: null, afterGross: null, beforeDiscount: null, afterDiscount: null, beforeActual: null, afterActual: null, amountDelta: null })),
    [],
  );
  assert.equal(reportingDelaySeconds(item()), 2);
  assert.equal(reportingDelaySeconds(item({ receivedAtUtc: "bad" })), null);
  assert.equal(shortenIdentifier("8f3a1c2d-0000-4000-8000-00000000c2d1", 4), "8f3a…c2d1");
  assert.equal(shortenIdentifier("R2609", 4), "R2609");
  assert.equal(shortenIdentifier(null), "—");
});

test("深链：订单追溯放宽到 30 天，员工今日带门店", () => {
  const base = createDefaultPosOperationLogFilters(now);
  const order = applyDeepLinkParams(base, { orderGuid: " abc " });
  assert.equal(order.orderGuid, "abc");
  assert.equal(order.preset, "last30Days");
  const cashier = applyDeepLinkParams(base, { cashier: "C0231", storeCode: "CHAT", preset: "today" });
  assert.equal(cashier.cashierKeyword, "C0231");
  assert.equal(cashier.storeCode, "CHAT");
  assert.equal(cashier.preset, "today");
  assert.equal(applyDeepLinkParams(base, { preset: "custom" }).preset, "today");
});

test("响应归一化：信封解包、结果大小写与缺省字段", () => {
  const page = normalizePosOperationLogPage({
    success: true,
    data: {
      items: [
        {
          eventId: "E1",
          occurredAtUtc: "2026-09-17T04:00:00Z",
          receivedAtUtc: "2026-09-17T04:00:01Z",
          operationType: "CASH_DRAWER_OPEN",
          outcome: "denied",
          storeCode: "CHAT",
          deviceCode: "POS-1",
          isEmergencyOverride: true,
        },
      ],
      total: 1,
      pageNumber: 1,
      pageSize: 50,
    },
  });
  assert.equal(page.items[0].outcome, "Denied");
  assert.equal(page.items[0].currencyCode, "AUD");
  assert.equal(page.items[0].productCount, 0);
  assert.equal(page.items[0].isOfflineCached, false);
  assert.equal(page.items[0].isEmergencyOverride, true);
  assert.equal(page.items[0].cashierName, null);

  const summary = normalizePosOperationLogSummary({ success: true, data: { total: 4, denied: 1 } });
  assert.deepEqual(summary, { total: 4, succeeded: 0, denied: 1, failed: 0, emergencyOverride: 0, offlineCached: 0 });

  const detail = normalizePosOperationLogDetail({
    success: true,
    data: {
      ...page.items[0],
      outcome: "Succeeded",
      items: [
        { lineIndex: 1, displayName: "B" },
        { lineIndex: 0, displayName: "A" },
      ],
    },
  });
  assert.deepEqual(detail.items.map((line) => line.displayName), ["A", "B"]);
  assert.equal(detail.propertiesJson, null);
  assert.throws(() => normalizePosOperationLogPage({ success: false, message: "denied" }));
});
