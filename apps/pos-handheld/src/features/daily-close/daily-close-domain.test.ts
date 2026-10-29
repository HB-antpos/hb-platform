import assert from "node:assert/strict";
import test from "node:test";

import {
  DAILY_CLOSE_REPRINT_PERMISSION,
  DAILY_CLOSE_SAVE_PERMISSION,
  DAILY_CLOSE_VIEW_PERMISSION,
  resolveDailyCloseAccess,
} from "@hb/pos-domain/features/daily-close/daily-close-authorization";
import {
  buildDailyCloseArchiveCommit,
  businessDateInTimeZone,
  dailyCloseBusinessDayScope,
} from "./daily-close-domain";

import {
  AUD_CASH_DENOMINATIONS_CENTS,
  type DailyCloseSummary,
} from "@/core/contracts";

test("View、Save、Reprint 权限逐项精确授权，不接受父级或近似名称", () => {
  assert.deepEqual(
    resolveDailyCloseAccess([
      DAILY_CLOSE_VIEW_PERMISSION,
      DAILY_CLOSE_REPRINT_PERMISSION,
      "Permissions.PosTerminal.DailyClose",
      `${DAILY_CLOSE_SAVE_PERMISSION}.Extra`,
    ]),
    {
      canView: true,
      canSave: false,
      canReprint: true,
    },
  );
});

test("门店本地业务日转换成 [from,to) UTC，正确覆盖普通日和 DST 日", () => {
  assert.deepEqual(
    dailyCloseBusinessDayScope({
      businessDate: "2026-07-28",
      businessTimeZone: "Australia/Brisbane",
      deviceCode: "IPAD-1",
      storeCode: "S1",
    }),
    {
      businessDate: "2026-07-28",
      periodFromIso: "2026-07-27T14:00:00.000Z",
      periodToIso: "2026-07-28T14:00:00.000Z",
      deviceCode: "IPAD-1",
      storeCode: "S1",
    },
  );
  const dst = dailyCloseBusinessDayScope({
    businessDate: "2026-10-04",
    businessTimeZone: "Australia/Melbourne",
    deviceCode: "IPAD-1",
    storeCode: "S1",
  });
  assert.equal(
    Date.parse(dst.periodToIso) - Date.parse(dst.periodFromIso),
    23 * 60 * 60 * 1_000,
  );
  assert.equal(
    businessDateInTimeZone(
      new Date("2026-07-28T13:59:59.999Z"),
      "Australia/Brisbane",
    ),
    "2026-07-28",
  );
});

test("归档构造固定补齐 11 种 AUD 面额并以整数分币冻结审计事实", () => {
  const summary = createSummary();
  const commit = buildDailyCloseArchiveCommit({
    auditEventId: "audit-1",
    closeId: "close-1",
    counts: [
      { denominationCents: 10_000, quantity: 1 },
      { denominationCents: 500, quantity: 2 },
      { denominationCents: 50, quantity: 3 },
    ],
    savedAtIso: "2026-07-28T08:00:00.000Z",
    savedCashierId: "C1",
    savedCashierName: "Alice",
    savedUserGuid: "U1",
    summary,
  });

  assert.deepEqual(
    commit.archive.denominations.map((entry) => entry.denominationCents),
    AUD_CASH_DENOMINATIONS_CENTS,
  );
  assert.equal(commit.archive.denominations.length, 11);
  assert.equal(commit.archive.notesSubtotalCents, 11_000);
  assert.equal(commit.archive.coinsSubtotalCents, 150);
  assert.equal(commit.archive.countedCashCents, 11_150);
  assert.equal(commit.archive.varianceCents, 10_350);
  assert.deepEqual(commit.audit, {
    eventId: "audit-1",
    eventType: "DAILY_CLOSE_SAVE",
    occurredAtIso: "2026-07-28T08:00:00.000Z",
    orderGuid: null,
    correlationId: "close-1",
    payload: {
      action: "daily-close-save",
      businessDate: "2026-07-28",
      closeId: "close-1",
      countedCashCents: 11_150,
      deviceCode: "IPAD-1",
      storeCode: "S1",
      varianceCents: 10_350,
      requestingCashierId: "C1",
      requestingCashierName: "Alice",
      requestingUserGuid: "U1",
    },
  });
});

function createSummary(): DailyCloseSummary {
  return {
    businessDate: "2026-07-28",
    periodFromIso: "2026-07-27T14:00:00.000Z",
    periodToIso: "2026-07-28T14:00:00.000Z",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    orderCount: 3,
    returnQuantity: "1.5",
    tenders: [
      {
        method: "cash",
        salesCents: 1_000,
        refundCents: -200,
        netCents: 800,
      },
      {
        method: "card",
        salesCents: 2_000,
        refundCents: 0,
        netCents: 2_000,
      },
      {
        method: "voucher",
        salesCents: 300,
        refundCents: -100,
        netCents: 200,
      },
    ],
    cashRoundingCents: 0,
    expectedCashCents: 800,
  };
}
