import assert from "node:assert/strict";
import test from "node:test";

import {
  buildDailyCloseReceipt,
  dailyCloseReceiptToEscPosBytes,
  dailyCloseReceiptDisplayWidth,
} from "@hb/pos-receipt-core/features/receipts/daily-close-receipt";

import type { DailyCloseArchive } from "@/core/contracts";

test("58/80mm 按各自纸宽完整列出三类收退款净额和 11 种面额", () => {
  const narrow = buildDailyCloseReceipt({
    archive: createArchive(),
    locale: "en",
    paper: "58mm",
    reprint: false,
    brandName: "Hot Bargain",
    storeName: "Sunnybank Plaza",
  });
  const wide = buildDailyCloseReceipt({
    archive: createArchive(),
    locale: "en",
    paper: "80mm",
    reprint: false,
    brandName: "Hot Bargain",
    storeName: "Sunnybank Plaza",
  });

  assert.equal(narrow.width, 32);
  assert.equal(wide.width, 42);
  assert.equal(narrow.lines[0]?.trim(), "Hot Bargain");
  assert.equal(wide.lines[0]?.trim(), "Hot Bargain");
  assert.ok(
    narrow.lines.every(
      (line) => dailyCloseReceiptDisplayWidth(line) <= 32,
    ),
  );
  assert.ok(
    wide.lines.every(
      (line) => dailyCloseReceiptDisplayWidth(line) <= 42,
    ),
  );
  const text = narrow.lines.join("\n");
  assert.match(text, /Store.*Sunnybank Plaza \(S1\)/);
  assert.match(text, /Terminal.*IPAD-1/);
  assert.match(text, /Cashier.*Alice/);
  assert.match(text, /Cash.*\$12\.00.*-\$2\.00.*\$10\.00/);
  assert.match(text, /Card.*\$20\.00.*\$0\.00.*\$20\.00/);
  assert.match(text, /Voucher.*\$5\.00.*-\$1\.00.*\$4\.00/);
  assert.match(text, /Orders.*4/);
  assert.match(text, /Return Qty.*2\.5/);
  assert.match(text, /Expected.*\$10\.00/);
  assert.match(text, /Counted.*\$107\.00/);
  assert.match(text, /Variance.*\+\$97\.00/);
  assert.match(text, /Notes.*\$105\.00/);
  assert.match(text, /Coins.*\$2\.00/);
  for (const label of [
    "$100",
    "$50",
    "$20",
    "$10",
    "$5",
    "$2",
    "$1",
    "50c",
    "20c",
    "10c",
    "5c",
  ]) {
    assert.match(text, new RegExp(escapeRegExp(label)));
  }
});

test("中文票据和补打票据明确标记，长门店名称按 CJK 宽度安全换行", () => {
  const receipt = buildDailyCloseReceipt({
    archive: createArchive(),
    locale: "zh-CN",
    paper: "58mm",
    reprint: true,
    storeName: "阳光海岸超级长门店名称 Sunnybank Shopping Centre",
  });
  const text = receipt.lines.join("\n");

  assert.match(text, /日结/);
  assert.match(text, /补打|REPRINT/);
  assert.match(text, /门店/);
  assert.ok(
    receipt.lines.every(
      (line) => dailyCloseReceiptDisplayWidth(line) <= 42,
    ),
  );
});

test("日结打印贯穿当前门店 Return Policy 并按纸宽安全换行", () => {
  const receipt = buildDailyCloseReceipt({
    archive: createArchive(),
    locale: "en",
    paper: "58mm",
    reprint: false,
    storeName: "Sunnybank Plaza",
    returnPolicy:
      "Refunds and returns are accepted within fourteen days with proof of purchase.",
  });
  const text = receipt.lines.join("\n");

  assert.match(text, /Refunds and returns/);
  assert.match(text.replaceAll("\n", ""), /within fourteen days/);
  assert.ok(
    receipt.lines.every(
      (line) => dailyCloseReceiptDisplayWidth(line) <= 32,
    ),
  );
});

test("日结抬头依次回退 Brand、Store、Store Code", () => {
  const input = {
    archive: createArchive(),
    locale: "en" as const,
    paper: "80mm" as const,
    reprint: false,
  };

  const branded = buildDailyCloseReceipt({
    ...input,
    brandName: "Hot Bargain",
    storeName: "Sunnybank Plaza",
  });
  const storeOnly = buildDailyCloseReceipt({
    ...input,
    brandName: "",
    storeName: "Sunnybank Plaza",
  });
  const codeOnly = buildDailyCloseReceipt({
    ...input,
    brandName: "",
    storeName: "",
  });

  assert.equal(branded.lines[0]?.trim(), "Hot Bargain");
  assert.equal(storeOnly.lines[0]?.trim(), "Sunnybank Plaza");
  assert.equal(codeOnly.lines[0]?.trim(), "S1");
});

test("日结文本编码为完整 ESC/POS 作业并在末尾切纸", () => {
  const receipt = buildDailyCloseReceipt({
    archive: createArchive(),
    locale: "en",
    paper: "80mm",
    reprint: false,
    storeName: "Sunnybank Plaza",
  });
  const bytes = dailyCloseReceiptToEscPosBytes(receipt);

  assert.deepEqual([...bytes.slice(0, 4)], [0x1b, 0x40, 0x1c, 0x26]);
  assert.deepEqual([...bytes.slice(-3)], [0x1d, 0x56, 0x00]);
  assert.match(new TextDecoder().decode(bytes), /DAILY CLOSE/);
});

test("中文日结文本使用 GB18030 且不输出 UTF-8 字节", () => {
  const receipt = buildDailyCloseReceipt({
    archive: createArchive(),
    locale: "zh-CN",
    paper: "80mm",
    reprint: false,
    storeName: "商品😀𠀀",
  });
  const bytes = dailyCloseReceiptToEscPosBytes(receipt);

  assert.ok(containsBytes(bytes, [0xc9, 0xcc, 0xc6, 0xb7, 0x3f, 0x3f]));
  assert.equal(containsBytes(bytes, [...new TextEncoder().encode("商品")]), false);
});

test("日结小票在应有现金前单独列出现金进位（中英文、两种纸宽、正负号），应有现金含进位", () => {
  const base = createArchive();
  // 现金净额 $10.00，进位 +$0.02 → 应有现金 $10.02；实点 $107.00，差额 +$96.98。
  const positive = {
    ...base,
    cashRoundingCents: 2,
    expectedCashCents: base.expectedCashCents + 2,
    varianceCents: base.countedCashCents - (base.expectedCashCents + 2),
  };
  for (const paper of ["58mm", "80mm"] as const) {
    const receipt = buildDailyCloseReceipt({
      archive: positive,
      locale: "en",
      paper,
      reprint: false,
      storeName: "Sunnybank Plaza",
    });
    const text = receipt.lines.join("\n");
    assert.match(text, /Cash Rounding.*\+\$0\.02/);
    assert.match(text, /Expected Cash.*\$10\.02/);
    assert.match(text, /Variance.*\+\$96\.98/);
    // 进位行必须排在应有现金之前。
    assert.ok(text.indexOf("Cash Rounding") < text.indexOf("Expected Cash"));
    assert.ok(
      receipt.lines.every(
        (line) => dailyCloseReceiptDisplayWidth(line) <= receipt.width,
      ),
    );
  }

  const zh = buildDailyCloseReceipt({
    archive: {
      ...positive,
      cashRoundingCents: -3,
      expectedCashCents: base.expectedCashCents - 3,
      varianceCents: base.countedCashCents - (base.expectedCashCents - 3),
    },
    locale: "zh-CN",
    paper: "58mm",
    reprint: false,
    storeName: "Sunnybank Plaza",
  });
  const zhText = zh.lines.join("\n");
  assert.match(zhText, /现金进位\/Rounding.*-\$0\.03/);
  assert.match(zhText, /应有现金\/Expected.*\$9\.97/);

  // 没有进位（旧存档）也照常打印这一行，金额为 0。
  const plain = buildDailyCloseReceipt({
    archive: base,
    locale: "en",
    paper: "58mm",
    reprint: true,
    storeName: "Sunnybank Plaza",
  });
  assert.match(plain.lines.join("\n"), /Cash Rounding.*\$0\.00/);
});

function createArchive(): DailyCloseArchive {
  const denominationValues = [
    10_000, 5_000, 2_000, 1_000, 500, 200, 100, 50, 20, 10, 5,
  ] as const;
  const quantities = new Map<number, number>([
    [10_000, 1],
    [500, 1],
    [200, 1],
  ]);
  const denominations = denominationValues.map((denominationCents) => {
    const quantity = quantities.get(denominationCents) ?? 0;
    return {
      denominationCents,
      quantity,
      subtotalCents: denominationCents * quantity,
    };
  });
  return {
    businessDate: "2026-07-28",
    periodFromIso: "2026-07-27T14:00:00.000Z",
    periodToIso: "2026-07-28T14:00:00.000Z",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    orderCount: 4,
    returnQuantity: "2.5",
    tenders: [
      {
        method: "cash",
        salesCents: 1_200,
        refundCents: -200,
        netCents: 1_000,
      },
      {
        method: "card",
        salesCents: 2_000,
        refundCents: 0,
        netCents: 2_000,
      },
      {
        method: "voucher",
        salesCents: 500,
        refundCents: -100,
        netCents: 400,
      },
    ],
    cashRoundingCents: 0,
    expectedCashCents: 1_000,
    closeId: "close-1",
    savedCashierId: "C1",
    savedCashierName: "Alice",
    savedAtIso: "2026-07-28T08:00:00.000Z",
    denominations,
    notesSubtotalCents: 10_500,
    coinsSubtotalCents: 200,
    countedCashCents: 10_700,
    varianceCents: 9_700,
  };
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function containsBytes(bytes: Uint8Array, expected: readonly number[]): boolean {
  return Array.from(
    { length: Math.max(0, bytes.length - expected.length + 1) },
    (_, start) => expected.every(
      (value, offset) => bytes[start + offset] === value,
    ),
  ).some(Boolean);
}
