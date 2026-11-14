import assert from "node:assert/strict";
import test from "node:test";

import type { DailyCloseArchive } from "@hb/pos-domain/core/contracts/daily-close";

import {
  buildDailyCloseReceipt,
  type DailyCloseReceiptInput,
} from "./daily-close-receipt";

/** 日结回单的 Cash In / Out 段：有存入 / 取出时才打印，没有时整段不打，旧格式不变。 */

function archive(overrides: Partial<DailyCloseArchive> = {}): DailyCloseArchive {
  const values = [10_000, 5_000, 2_000, 1_000, 500, 200, 100, 50, 20, 10, 5] as const;
  return {
    businessDate: "2026-11-14",
    periodFromIso: "2026-11-13T14:00:00.000Z",
    periodToIso: "2026-11-14T14:00:00.000Z",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    orderCount: 4,
    returnQuantity: "0",
    tenders: [
      { method: "cash", salesCents: 1_200, refundCents: 0, netCents: 1_200 },
      { method: "card", salesCents: 2_000, refundCents: 0, netCents: 2_000 },
      { method: "voucher", salesCents: 0, refundCents: 0, netCents: 0 },
    ],
    installmentPayments: [
      { method: "cash", salesCents: 0, refundCents: 0, netCents: 0 },
      { method: "card", salesCents: 0, refundCents: 0, netCents: 0 },
      { method: "voucher", salesCents: 0, refundCents: 0, netCents: 0 },
    ],
    installmentPaymentCount: 0,
    cashRoundingCents: 0,
    cashInCents: 0,
    cashOutCents: 0,
    expectedCashCents: 1_200,
    closeId: "close-1",
    savedCashierId: "C1",
    savedCashierName: "Alice",
    savedAtIso: new Date(2026, 10, 14, 12, 0, 0).toISOString(),
    denominations: values.map((denominationCents) => ({
      denominationCents,
      quantity: 0,
      subtotalCents: 0,
    })),
    notesSubtotalCents: 0,
    coinsSubtotalCents: 0,
    countedCashCents: 0,
    varianceCents: -1_200,
    ...overrides,
  };
}

function input(overrides: Partial<DailyCloseReceiptInput> = {}): DailyCloseReceiptInput {
  return {
    archive: archive(),
    locale: "en",
    paper: "80mm",
    reprint: false,
    brandName: "Hot Bargain",
    storeName: "Sunnybank Plaza",
    ...overrides,
  };
}

const withMovements = archive({
  cashInCents: 15_000,
  cashOutCents: 31_250,
  expectedCashCents: 1_200 + 15_000 - 31_250,
  varianceCents: -(1_200 + 15_000 - 31_250),
});
const byReason = { ChangeTopUp: 10_000, OtherIn: 5_000, SafeDrop: 30_000, PaidOut: 1_250 } as const;

test("没有现金进出时整段不打：与没有这两个字段之前逐行一致", () => {
  const text = buildDailyCloseReceipt(input()).lines.join("\n");
  assert.doesNotMatch(text, /Cash In|Cash Out|Change top-up|Safe drop|Paid out/);
  // 仍是旧的 Rounding → Expected 紧邻。
  const lines = buildDailyCloseReceipt(input()).lines;
  const rounding = lines.findIndex((line) => line.startsWith("Cash Rounding"));
  assert.ok(lines[rounding + 1]?.startsWith("Expected Cash"));
});

test("有现金进出：Cash In（Change top-up / Other cash in 明细）与 Cash Out（Safe drop / Paid out 明细），位于 Rounding 之后、Expected 之前", () => {
  for (const paper of ["80mm", "58mm"] as const) {
    const document = buildDailyCloseReceipt(
      input({ archive: withMovements, cashMovementByReason: byReason, paper }),
    );
    const lines = document.lines;
    const at = (prefix: string) => lines.findIndex((line) => line.trimStart().startsWith(prefix));
    const matcher = (prefix: string, amount: RegExp) => {
      const line = lines[at(prefix)];
      assert.ok(line, prefix);
      assert.match(line, amount);
    };
    matcher("Cash In", /\$150\.00$/);
    matcher("Change top-up", /\$100\.00$/);
    matcher("Other cash in", /\$50\.00$/);
    matcher("Cash Out", /\$312\.50$/);
    matcher("Safe drop", /\$300\.00$/);
    matcher("Paid out", /\$12\.50$/);
    matcher("Expected Cash", /-\$150\.50$/);
    assert.ok(at("Cash Rounding") < at("Cash In"));
    assert.ok(at("Cash In") < at("Change top-up"));
    assert.ok(at("Change top-up") < at("Other cash in"));
    assert.ok(at("Other cash in") < at("Cash Out"));
    assert.ok(at("Cash Out") < at("Safe drop"));
    assert.ok(at("Safe drop") < at("Paid out"));
    assert.ok(at("Paid out") < at("Expected Cash"));
    assert.ok(at("Expected Cash") < at("Counted Cash"));
  }
});

test("只有存入或只有取出时也打印两行合计；明细缺省或与存档合计对不上时只打合计，不打误导的明细", () => {
  const onlyOut = archive({ cashOutCents: 500, expectedCashCents: 700, varianceCents: -700 });
  const totalsOnly = buildDailyCloseReceipt(input({ archive: onlyOut })).lines.join("\n");
  assert.match(totalsOnly, /Cash In\s+\$0\.00/);
  assert.match(totalsOnly, /Cash Out\s+\$5\.00/);
  assert.doesNotMatch(totalsOnly, /Safe drop|Paid out|Change top-up/);

  const mismatched = buildDailyCloseReceipt(
    input({ archive: withMovements, cashMovementByReason: { ...byReason, SafeDrop: 1 } }),
  ).lines.join("\n");
  assert.match(mismatched, /Cash Out\s+\$312\.50/);
  assert.doesNotMatch(mismatched, /Safe drop|Paid out/);
});

test("中文版有对应的双语标签", () => {
  const text = buildDailyCloseReceipt(
    input({ archive: withMovements, cashMovementByReason: byReason, locale: "zh-CN" }),
  ).lines.join("\n");
  assert.match(text, /现金存入\/Cash In/);
  assert.match(text, /补零钱\/Change top-up/);
  assert.match(text, /现金取出\/Cash Out/);
});
