import assert from "node:assert/strict";
import test from "node:test";

import {
  buildDailyCloseCashSlip,
  buildDailyCloseReceipt,
  dailyCloseReceiptDisplayWidth,
  dailyCloseReceiptToEscPosBytes,
  type DailyCloseReceiptInput,
} from "./daily-close-receipt";

import type { DailyCloseArchive } from "@hb/pos-domain/core/contracts/daily-close";

/** 保存时间用本地时间构造，避免测试依赖运行机时区。 */
const localIso = (year: number, month: number, day: number, hour = 12) =>
  new Date(year, month - 1, day, hour, 0, 0).toISOString();

function archive(overrides: Partial<DailyCloseArchive> = {}): DailyCloseArchive {
  const values = [10_000, 5_000, 2_000, 1_000, 500, 200, 100, 50, 20, 10, 5] as const;
  const quantities = new Map<number, number>([[10_000, 1], [500, 1], [200, 1]]);
  return {
    businessDate: "2026-10-26",
    periodFromIso: "2026-10-25T13:00:00.000Z",
    periodToIso: "2026-10-26T13:00:00.000Z",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    orderCount: 4,
    returnQuantity: "0",
    tenders: [
      { method: "cash", salesCents: 1_200, refundCents: 0, netCents: 1_200 },
      { method: "card", salesCents: 2_000, refundCents: 0, netCents: 2_000 },
      { method: "voucher", salesCents: 0, refundCents: 0, netCents: 0 },
    ],
    cashRoundingCents: 0,
    expectedCashCents: 1_200,
    closeId: "close-1",
    savedCashierId: "C1",
    savedCashierName: "Alice",
    savedAtIso: localIso(2026, 10, 26),
    denominations: values.map((denominationCents) => {
      const quantity = quantities.get(denominationCents) ?? 0;
      return { denominationCents, quantity, subtotalCents: denominationCents * quantity };
    }),
    notesSubtotalCents: 10_500,
    coinsSubtotalCents: 200,
    countedCashCents: 10_700,
    varianceCents: 9_500,
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

test("营业日期单独成块并加粗；保存日与营业日期一致时没有核对提示", () => {
  const document = buildDailyCloseReceipt(input());
  const text = document.lines.join("\n");

  assert.match(text, /BUSINESS DATE/);
  const dateIndex = document.lines.findIndex((line) => line.includes("2026-10-26 Mon"));
  assert.ok(dateIndex >= 0);
  assert.ok(document.emphasizedLines?.includes(dateIndex));
  assert.doesNotMatch(text, /CHECK DATE/);
  // 营业日期块排在门店/终端信息之前。
  assert.ok(dateIndex < document.lines.findIndex((line) => line.startsWith("Store")));
});

test("保存日与营业日期不一致时追加加粗的核对提示（如隔夜未重启按旧日期日结）", () => {
  const document = buildDailyCloseReceipt(
    input({ archive: archive({ savedAtIso: localIso(2026, 10, 27) }) }),
  );
  const checkIndex = document.lines.findIndex((line) => line.includes("** CHECK DATE **"));

  assert.ok(checkIndex >= 0);
  assert.ok(document.emphasizedLines?.includes(checkIndex));
  assert.match(document.lines.join("\n"), /Saved on 2026-10-27, not the business date/);

  const zh = buildDailyCloseReceipt(
    input({ locale: "zh-CN", archive: archive({ savedAtIso: localIso(2026, 10, 27) }) }),
  );
  assert.match(zh.lines.join("\n"), /请核对日期/);
});

test("现金单只含面额明细、纸币/硬币合计与实盘金额，带随现金保存提示和签名栏，不含销售汇总与长短款", () => {
  for (const paper of ["58mm", "80mm"] as const) {
    const slip = buildDailyCloseCashSlip(input({ paper }));
    const text = slip.lines.join("\n");

    assert.match(text, /CASH SLIP/);
    assert.match(text, /\$100\s+x1\s+\$100\.00/);
    assert.match(text, /Notes\s+\$105\.00/);
    assert.match(text, /Coins\s+\$2\.00/);
    assert.match(text, /Cash Counted\s+\$107\.00/);
    assert.match(text, /KEEP THIS SLIP/);
    assert.match(text, /WITH THE CASH/);
    assert.match(text, /Counted by:/);
    assert.match(text, /Checked by:/);
    assert.match(text, /Archive: close-1/);
    assert.doesNotMatch(text, /Variance|Expected|Orders|SALES/);
    assert.ok(slip.lines.every((line) => dailyCloseReceiptDisplayWidth(line) <= slip.width));
    const countedIndex = slip.lines.findIndex((line) => line.startsWith("Cash Counted"));
    assert.ok(slip.emphasizedLines?.includes(countedIndex));
  }
});

test("ESC/POS 输出：加粗行前发 ESC E 1，其余行显式 ESC E 0，结尾关闭加粗后切纸", () => {
  const document = buildDailyCloseReceipt(input());
  const bytes = dailyCloseReceiptToEscPosBytes(document);
  const boldOn = [0x1b, 0x45, 0x01];
  const boldOff = [0x1b, 0x45, 0x00];
  const count = (pattern: readonly number[]) =>
    Array.from(bytes).filter((_, start) =>
      pattern.every((value, offset) => bytes[start + offset] === value)).length;

  assert.equal(count(boldOn), document.emphasizedLines?.length);
  assert.ok(count(boldOff) >= document.lines.length - (document.emphasizedLines?.length ?? 0));
  // 末尾：ESC E 0, ESC d 3（走纸）, GS V 0（切纸）。
  assert.deepEqual(Array.from(bytes.slice(-8)), [0x1b, 0x45, 0x00, 0x1b, 0x64, 0x03, 0x1d, 0x56, 0x00].slice(-8));
});
