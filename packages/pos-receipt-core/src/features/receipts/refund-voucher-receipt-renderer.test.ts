import assert from "node:assert/strict";
import test from "node:test";

import { encodeEscPosText } from "./esc-pos-text-encoding";
import {
  encodeRefundVoucherDocuments,
  ProtectedRefundVoucherReceiptRenderer,
  type ProtectedRefundVoucherPrintMaterial,
} from "./refund-voucher-receipt-renderer";
import { REFUND_VOUCHER_TERMS } from "./refund-voucher-terms";
import type { FrozenReturnReceiptSettings } from "./return-receipt-renderer";

import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";

const encoder = new TextDecoder();

test("单一券退款只在打印瞬间解析受保护券码，并生成独立 CODE128/QR 券面", async () => {
  const materialReads: {
    actionId: string;
    returnOrderGuid: string;
  }[] = [];
  const renderer = new ProtectedRefundVoucherReceiptRenderer(
    {
      async getByGuid() {
        return pureVoucherReturn();
      },
    },
    {
      async resolveApprovedRefundVouchers(actionId, returnOrderGuid) {
        materialReads.push({ actionId, returnOrderGuid });
        return [protectedMaterial()];
      },
    },
    {
      async getFrozenReturnReceiptSettings() {
        const current = settings();
        return {
          ...current,
          store: {
            ...current.store,
            returnPolicy: "Refunds within 14 days with proof of purchase.",
          },
        };
      },
    },
    () => new Date(2026, 6, 10, 9, 30, 0),
  );

  const rendered = await renderer.render(
    "return-action-1",
    "return-order-1",
  );
  const text = encoder.decode(rendered.receiptBytes);
  const bytes = [...rendered.receiptBytes];

  assert.equal(rendered.printerId, "printer-1");
  assert.deepEqual(materialReads, [{
    actionId: "return-action-1",
    returnOrderGuid: "return-order-1",
  }]);
  assert.match(text, /Hot Bargain/u);
  assert.match(text, /REFUND VOUCHER/);
  assert.match(text, /Voucher: RF123/);
  assert.match(text, /Amount: \$8\.00/);
  assert.match(text, /Order: return-order-1/);
  assert.match(text, /Print Time: 2026-07-10 09:30:00/);
  assert.match(text, /Refunds and returns/);
  assert.match(text, /Refunds within 14 days with proof of purchase\./);
  assert.doesNotMatch(text, /TAX INVOICE|Secret product|Payment:/);
  assert.doesNotMatch(text, /return-action-1/);
  assert.equal(
    JSON.stringify(rendered).includes("return-action-1"),
    false,
    "actionId 不得进入打印结果公开快照",
  );
  assert.equal(
    containsSequence(bytes, [0x1d, 0x6b, 0x49]),
    true,
    "必须包含 CODE128 指令",
  );
  assert.equal(
    containsSequence(bytes, [0x1d, 0x28, 0x6b]),
    true,
    "必须包含 QR 指令",
  );
  assert.deepEqual(bytes.slice(-3), [0x1d, 0x56, 0x00]);
});

test("退款券抬头依次回退 Brand、Store、Store Code", async () => {
  const render = async (store: FrozenReturnReceiptSettings["store"]) => {
    const renderer = new ProtectedRefundVoucherReceiptRenderer(
      { async getByGuid() { return pureVoucherReturn(); } },
      { async resolveApprovedRefundVouchers() { return [protectedMaterial()]; } },
      {
        async getFrozenReturnReceiptSettings() {
          return { ...settings(), store };
        },
      },
      () => new Date(2026, 6, 10, 9, 30, 0),
    );
    return encoder.decode((await renderer.render(
      "return-action-1",
      "return-order-1",
    )).receiptBytes);
  };
  const base = settings().store;

  assert.match(await render(base), /Hot Bargain/u);
  assert.match(
    await render({ ...base, brandName: "", storeName: "Brisbane" }),
    /Brisbane/u,
  );
  assert.match(
    await render({ ...base, brandName: "", storeName: "" }),
    /S001/u,
  );
});

test("zh-CN 退款券启用中文模式并使用 GB18030 文本字节", async () => {
  const renderer = new ProtectedRefundVoucherReceiptRenderer(
    {
      async getByGuid() {
        return pureVoucherReturn();
      },
    },
    {
      async resolveApprovedRefundVouchers() {
        return [protectedMaterial()];
      },
    },
    {
      async getFrozenReturnReceiptSettings() {
        return { ...settings(), locale: "zh-CN" };
      },
    },
    () => new Date(2026, 6, 10, 9, 30, 0),
  );

  const rendered = await renderer.render(
    "return-action-1",
    "return-order-1",
  );
  const bytes = [...rendered.receiptBytes];

  assert.equal(
    containsSequence(bytes, [0x1b, 0x40, 0x1c, 0x26]),
    true,
    "ESC @ 后必须立即进入中文字符模式",
  );
  assert.equal(
    containsSequence(bytes, [0xcd, 0xcb, 0xbf, 0xee, 0xc8, 0xaf]),
    true,
    "退款券必须以 GB18030 字节输出",
  );
  assert.equal(
    containsSequence(bytes, [
      0xe9, 0x80, 0x80,
      0xe6, 0xac, 0xbe,
      0xe5, 0x88, 0xb8,
    ]),
    false,
    "不得再输出 UTF-8 中文字节",
  );
});

test("CODE128 转义券码中的左花括号并按转义后长度编码，QR 与明文保持原值", async () => {
  const voucherCode = "AB{C12";
  const renderer = new ProtectedRefundVoucherReceiptRenderer(
    {
      async getByGuid() {
        return pureVoucherReturn();
      },
    },
    {
      async resolveApprovedRefundVouchers() {
        return [{ ...protectedMaterial(), voucherCode }];
      },
    },
    {
      async getFrozenReturnReceiptSettings() {
        return settings();
      },
    },
    () => new Date(2026, 6, 10, 9, 30, 0),
  );

  const rendered = await renderer.render(
    "return-action-1",
    "return-order-1",
  );
  const bytes = [...rendered.receiptBytes];
  const code128Payload = [...new TextEncoder().encode("{BAB{{C12")];
  const qrPayload = [...new TextEncoder().encode(voucherCode)];

  assert.equal(code128Payload.length, 9);
  assert.equal(
    containsSequence(bytes, [
      0x1d,
      0x6b,
      0x49,
      code128Payload.length,
      ...code128Payload,
      0x0a,
    ]),
    true,
    "CODE128 长度和 payload 必须以转义后的数据为准",
  );
  assert.equal(
    containsSequence(bytes, [
      0x1d,
      0x28,
      0x6b,
      qrPayload.length + 3,
      0x00,
      0x31,
      0x50,
      0x30,
      ...qrPayload,
      0x1d,
      0x28,
      0x6b,
      0x03,
      0x00,
      0x31,
      0x51,
      0x30,
    ]),
    true,
    "QR payload 必须保留原始券码",
  );
  assert.match(encoder.decode(rendered.receiptBytes), /Voucher: AB\{C12/);
});

test("订单、金额、券码或设置不满足冻结身份时失败关闭", async (t) => {
  const baseOrder = pureVoucherReturn();
  const baseMaterial = protectedMaterial();
  const cases: readonly Readonly<{
    name: string;
    order: LocalOrder | null;
    material: ProtectedRefundVoucherPrintMaterial | null;
    settings: FrozenReturnReceiptSettings | null;
  }>[] = [
    {
      name: "missing material",
      order: baseOrder,
      material: null,
      settings: settings(),
    },
    {
      name: "amount mismatch",
      order: baseOrder,
      material: { ...baseMaterial, refundAmountCents: 799 },
      settings: settings(),
    },
    {
      name: "unsafe voucher code",
      order: baseOrder,
      material: { ...baseMaterial, voucherCode: "RF123\nOPEN DRAWER" },
      settings: settings(),
    },
    {
      // 多出一笔券而合计不等于订单实退金额：订单本身不自洽，必须拒绝。
      name: "tender total mismatch",
      order: {
        ...baseOrder,
        tenders: [
          ...baseOrder.tenders,
          {
            tenderGuid: "voucher-tender-2",
            method: "voucher",
            amount: { currency: "AUD", cents: -1 },
            reference: null,
            reservationToken: null,
          },
        ],
      },
      material: baseMaterial,
      settings: settings(),
    },
    {
      name: "missing settings",
      order: baseOrder,
      material: baseMaterial,
      settings: null,
    },
  ];

  for (const current of cases) {
    await t.test(current.name, async () => {
      const renderer = new ProtectedRefundVoucherReceiptRenderer(
        { async getByGuid() { return current.order; } },
        {
          async resolveApprovedRefundVouchers() {
            return current.material === null ? null : [current.material];
          },
        },
        {
          async getFrozenReturnReceiptSettings() {
            return current.settings;
          },
        },
        () => new Date(2026, 6, 10, 9, 30, 0),
      );
      await assert.rejects(
        () => renderer.render("return-action-1", "return-order-1"),
        /REFUND_VOUCHER_/,
      );
    });
  }
});

test("混合退款逐张打印退款券：现金+券只出券面，卡+两张券出两张券面且各自切纸", async () => {
  const base = pureVoucherReturn();
  const mixed: LocalOrder = {
    ...base,
    total: { currency: "AUD", cents: -2000 },
    actualAmount: { currency: "AUD", cents: -2000 },
    tenders: [
      {
        tenderGuid: "card-tender-1",
        method: "card",
        amount: { currency: "AUD", cents: -1000 },
        reference: null,
        reservationToken: null,
      },
      {
        tenderGuid: "voucher-tender-1",
        method: "voucher",
        amount: { currency: "AUD", cents: -600 },
        reference: null,
        reservationToken: null,
      },
      {
        tenderGuid: "voucher-tender-2",
        method: "voucher",
        amount: { currency: "AUD", cents: -400 },
        reference: null,
        reservationToken: null,
      },
    ],
  };
  const renderer = new ProtectedRefundVoucherReceiptRenderer(
    { async getByGuid() { return mixed; } },
    {
      async resolveApprovedRefundVouchers() {
        return [
          { ...protectedMaterial(), voucherCode: "RF-B", refundAmountCents: 400 },
          { ...protectedMaterial(), voucherCode: "RF-A", refundAmountCents: 600 },
        ];
      },
    },
    { async getFrozenReturnReceiptSettings() { return settings(); } },
    () => new Date(2026, 6, 10, 9, 30, 0),
  );

  const rendered = await renderer.render("return-action-1", "return-order-1");
  const text = encoder.decode(rendered.receiptBytes);

  assert.match(text, /Voucher: RF-A/);
  assert.match(text, /Amount: \$6\.00/);
  assert.match(text, /Voucher: RF-B/);
  assert.match(text, /Amount: \$4\.00/);
  assert.equal(countSequence([...rendered.receiptBytes], [0x1d, 0x56, 0x00]), 2);

  // 券材料张数与 voucher tender 不一致时失败关闭。
  const missingOne = new ProtectedRefundVoucherReceiptRenderer(
    { async getByGuid() { return mixed; } },
    {
      async resolveApprovedRefundVouchers() {
        return [{ ...protectedMaterial(), voucherCode: "RF-A", refundAmountCents: 600 }];
      },
    },
    { async getFrozenReturnReceiptSettings() { return settings(); } },
    () => new Date(2026, 6, 10, 9, 30, 0),
  );
  await assert.rejects(
    () => missingOne.render("return-action-1", "return-order-1"),
    /REFUND_VOUCHER_MATERIAL_INVALID/,
  );
});

function countSequence(bytes: readonly number[], sequence: readonly number[]): number {
  let count = 0;
  for (let index = 0; index + sequence.length <= bytes.length; index += 1) {
    if (sequence.every((value, offset) => bytes[index + offset] === value)) count += 1;
  }
  return count;
}

function pureVoucherReturn(): LocalOrder {
  return {
    orderGuid: "return-order-1",
    localSequence: 8,
    storeCode: "S001",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Cashier",
    soldAtIso: "2026-07-10T00:00:00.000Z",
    state: "PendingSync",
    total: { currency: "AUD", cents: -800 },
    discount: { currency: "AUD", cents: 0 },
    actualAmount: { currency: "AUD", cents: -800 },
    originalOrderGuid: "sale-order-1",
    lines: [
      {
        lineId: "return-line-1",
        productCode: "P-SECRET",
        itemNumber: "I-SECRET",
        lookupCode: "SECRET",
        displayName: "Secret product",
        quantity: "1",
        unitPrice: { currency: "AUD", cents: 800 },
        discount: { currency: "AUD", cents: 0 },
        actualAmount: { currency: "AUD", cents: -800 },
        priceSource: "catalog",
        kind: "return",
        returnSourceKey: "source-1",
        originalOrderGuid: "sale-order-1",
        originalOrderDetailGuid: "sale-line-1",
      },
    ],
    tenders: [
      {
        tenderGuid: "voucher-tender-1",
        method: "voucher",
        amount: { currency: "AUD", cents: -800 },
        reference: null,
        reservationToken: null,
      },
    ],
  };
}

function protectedMaterial(): ProtectedRefundVoucherPrintMaterial {
  return {
    returnOrderGuid: "return-order-1",
    voucherCode: "RF123",
    refundAmountCents: 800,
  };
}

function settings(): FrozenReturnReceiptSettings {
  return {
    printerId: "printer-1",
    paper: "80mm",
    locale: "en",
    store: {
      brandName: "Hot Bargain",
      storeName: "Brisbane",
      address: "1 Queen St",
      phone: "0712345678",
      abn: "12 345 678 901",
      returnPolicy: "",
    },
  };
}

function containsSequence(
  source: readonly number[],
  expected: readonly number[],
): boolean {
  return source.some((_, index) =>
    expected.every((value, offset) => source[index + offset] === value),
  );
}

// ---------------------------------------------------------------------------
// 到期日行与券使用说明块
// ---------------------------------------------------------------------------

/**
 * 改动前（未印到期日、未印使用说明）同一入参生成的完整券面字节（hex），用作金标准：
 * 新增内容只允许出现在「Amount 行之后的到期行」与「QR 之后、走纸切纸之前的使用说明块」，
 * 其余字节必须与改动前逐字节一致。
 */
const LEGACY_GOLDEN: Readonly<Record<string, string>> = {
  "80mm-en-nopolicy":
    "1b401c261b61011b4501486f74204261726761696e0a1b61001b45000a1b61011b45013d3d3d3d3d20524546554e4420564f5543484552203d3d3d3d3d0a1b61001b45000a1b61011b4501566f75636865723a2052463132330a1b61011b4501416d6f756e743a2024382e30300a1b61001b45002d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d0a1b61001b45004f726465723a2072657475726e2d6f726465722d310a1b61001b45005072696e742054696d653a20323032362d30372d31302030393a33303a30300a1b61011d48021d68501d77021d6b49077b4252463132330a1d286b0400314132001d286b03003143051d286b03003145311d286b080031503052463132331d286b03003151301b64031d5600",
  "58mm-en-policy":
    "1b401c261b61011b4501486f74204261726761696e0a1b61001b45000a1b61011b45013d3d3d3d3d20524546554e4420564f5543484552203d3d3d3d3d0a1b61001b45000a1b61011b4501566f75636865723a2052463132330a1b61011b4501416d6f756e743a2024382e30300a1b61001b45002d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d0a1b61001b45004f726465723a2072657475726e2d6f726465722d310a1b61001b45005072696e742054696d653a20323032362d30372d31302030393a33303a30300a1b61001b45002d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d0a1b61001b4501526566756e647320616e642072657475726e730a1b61001b4500526566756e64732077697468696e203134206461797320776974682070726f6f0a1b61001b450066206f662070757263686173652e0a1b61011d48021d68501d77021d6b49077b4252463132330a1d286b0400314132001d286b03003143051d286b03003145311d286b080031503052463132331d286b03003151301b64031d5600",
  "80mm-zh":
    "1b401c261b61011b4501486f74204261726761696e0a1b61001b45000a1b61011b45013d3d3d3d3d20cdcbbfeec8af203d3d3d3d3d0a1b61001b45000a1b61011b4501c8afc2eb3a2052463132330a1b61011b4501bdf0b6ee3a2024382e30300a1b61001b45002d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d0a1b61001b4500b6a9b5a53a2072657475726e2d6f726465722d310a1b61001b4500b4f2d3a1cab1bce43a20323032362d30372d31302030393a33303a30300a1b61011d48021d68501d77021d6b49077b4252463132330a1d286b0400314132001d286b03003143051d286b03003145311d286b080031503052463132331d286b03003151301b64031d5600",
  "58mm-en-two":
    "1b401c261b61011b4501486f74204261726761696e0a1b61001b45000a1b61011b45013d3d3d3d3d20524546554e4420564f5543484552203d3d3d3d3d0a1b61001b45000a1b61011b4501566f75636865723a2052462d410a1b61011b4501416d6f756e743a2024362e30300a1b61001b45002d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d0a1b61001b45004f726465723a2072657475726e2d6f726465722d310a1b61001b45005072696e742054696d653a20323032362d30372d31302030393a33303a30300a1b61011d48021d68501d77021d6b49067b4252462d410a1d286b0400314132001d286b03003143051d286b03003145311d286b070031503052462d411d286b03003151301b64031d56001b401c261b61011b4501486f74204261726761696e0a1b61001b45000a1b61011b45013d3d3d3d3d20524546554e4420564f5543484552203d3d3d3d3d0a1b61001b45000a1b61011b4501566f75636865723a2052462d420a1b61011b4501416d6f756e743a2024342e30300a1b61001b45002d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d0a1b61001b45004f726465723a2072657475726e2d6f726465722d310a1b61001b45005072696e742054696d653a20323032362d30372d31302030393a33303a30300a1b61011d48021d68501d77021d6b49067b4252462d420a1d286b0400314132001d286b03003143051d286b03003145311d286b070031503052462d421d286b03003151301b64031d5600",
};

type GoldenCase = Readonly<{
  paper: "58mm" | "80mm";
  locale: "en" | "zh-CN";
  returnPolicy: string;
  vouchers: readonly Readonly<{ voucherCode: string; amountCents: number }>[];
}>;

const GOLDEN_CASES: Readonly<Record<string, GoldenCase>> = {
  "80mm-en-nopolicy": {
    paper: "80mm",
    locale: "en",
    returnPolicy: "",
    vouchers: [{ voucherCode: "RF123", amountCents: 800 }],
  },
  "58mm-en-policy": {
    paper: "58mm",
    locale: "en",
    returnPolicy: "Refunds within 14 days with proof of purchase.",
    vouchers: [{ voucherCode: "RF123", amountCents: 800 }],
  },
  "80mm-zh": {
    paper: "80mm",
    locale: "zh-CN",
    returnPolicy: "",
    vouchers: [{ voucherCode: "RF123", amountCents: 800 }],
  },
  "58mm-en-two": {
    paper: "58mm",
    locale: "en",
    returnPolicy: "",
    vouchers: [
      { voucherCode: "RF-A", amountCents: 600 },
      { voucherCode: "RF-B", amountCents: 400 },
    ],
  },
};

/** 每张券面末尾固定的「走纸 3 行 + 全切」；使用说明块必须插在它之前。 */
const FEED_AND_CUT_HEX = "1b64031d5600";
const PRINTED_AT = new Date(2026, 6, 10, 9, 30, 0);
// 门店当地（悉尼夏令时 UTC+11）2027-01-05 23:59:59 = 12:59:59Z，服务端新规则的典型到期时刻。
const SYDNEY_END_OF_DAY_UTC = "2027-01-05T12:59:59.000Z";

function hexOf(bytes: Uint8Array | readonly number[]): string {
  return Buffer.from(bytes).toString("hex");
}

/** 独立于实现的期望字节：一行 ESC/POS 文本（对齐、加粗、文本、换行）。 */
function escLineHex(
  value: string,
  alignment: "left" | "center",
  bold: boolean,
): string {
  return hexOf([
    0x1b, 0x61, alignment === "center" ? 1 : 0,
    0x1b, 0x45, bold ? 1 : 0,
    ...encodeEscPosText(value),
    0x0a,
  ]);
}

/** 期望的使用说明块：空行、分隔线、居中加粗标题、左对齐按词换行的条款行。 */
function expectedTermsHex(wrappedLines: readonly string[], width: number): string {
  return [
    escLineHex("", "left", false),
    escLineHex("-".repeat(width), "left", false),
    escLineHex("VOUCHER TERMS", "center", true),
    ...wrappedLines.map((line) => escLineHex(line, "left", false)),
  ].join("");
}

// 业主审定的四条文案，按本渲染器自己的纸宽（58mm=32、80mm=48）在单词边界换行后的结果。
const WRAPPED_TERMS_58MM = [
  "Use at the issuing store only.",
  "Pay with it at checkout by",
  "scanning the barcode or QR code.",
  "Can be used across several",
  "purchases until the balance is",
  "$0.00.",
  "Not redeemable for cash.",
] as const;
const WRAPPED_TERMS_80MM = [
  "Use at the issuing store only.",
  "Pay with it at checkout by scanning the barcode",
  "or QR code.",
  "Can be used across several purchases until the",
  "balance is $0.00.",
  "Not redeemable for cash.",
] as const;

function termsHexFor(paper: "58mm" | "80mm"): string {
  return paper === "58mm"
    ? expectedTermsHex(WRAPPED_TERMS_58MM, 32)
    : expectedTermsHex(WRAPPED_TERMS_80MM, 48);
}

function goldenSettings(testCase: GoldenCase): FrozenReturnReceiptSettings {
  const base = settings();
  return {
    ...base,
    paper: testCase.paper,
    locale: testCase.locale,
    store: { ...base.store, returnPolicy: testCase.returnPolicy },
  };
}

function encodeGolden(
  testCase: GoldenCase,
  extra: Readonly<{
    expiresAtIso?: readonly (string | null | undefined)[];
    businessTimeZone?: string;
  }> = {},
): string {
  const rendered = encodeRefundVoucherDocuments({
    settings: goldenSettings(testCase),
    storeCode: "S001",
    orderLabel: "return-order-1",
    vouchers: testCase.vouchers.map((voucher, index) => ({
      ...voucher,
      ...(extra.expiresAtIso && extra.expiresAtIso[index] !== undefined
        ? { expiresAtIso: extra.expiresAtIso[index] }
        : {}),
    })),
    printedAt: PRINTED_AT,
    ...(extra.businessTimeZone !== undefined
      ? { businessTimeZone: extra.businessTimeZone }
      : {}),
  });
  return hexOf(rendered.receiptBytes);
}

function amountLineHex(testCase: GoldenCase, amountCents: number): string {
  const label = testCase.locale === "zh-CN" ? "金额" : "Amount";
  const dollars = `$${Math.floor(amountCents / 100)}.${String(amountCents % 100).padStart(2, "0")}`;
  return escLineHex(`${label}: ${dollars}`, "center", true);
}

test("没有到期日时：除券面末尾新增的使用说明块外，其余字节与改动前逐字节一致（58mm/80mm、中英文、多券）", () => {
  for (const [name, testCase] of Object.entries(GOLDEN_CASES)) {
    const legacy = LEGACY_GOLDEN[name]!;
    assert.equal(
      legacy.split(FEED_AND_CUT_HEX).length - 1,
      testCase.vouchers.length,
      `${name}: 金标准每张券面应恰有一次走纸切纸`,
    );
    const expected = legacy.replaceAll(
      FEED_AND_CUT_HEX,
      `${termsHexFor(testCase.paper)}${FEED_AND_CUT_HEX}`,
    );
    assert.equal(encodeGolden(testCase), expected, name);
    // 没有到期日时整行省略，绝不印猜测日期。
    assert.equal(
      Buffer.from(encodeGolden(testCase), "hex").toString("latin1").includes("Valid until"),
      false,
      name,
    );
  }
});

test("有到期日时：只在 Amount 行之后多一行居中加粗的英文 Valid until: yyyy-MM-dd，其余字节不变", () => {
  for (const [name, testCase] of Object.entries(GOLDEN_CASES)) {
    const legacy = LEGACY_GOLDEN[name]!;
    const expiries = testCase.vouchers.map(() => SYDNEY_END_OF_DAY_UTC);
    let expected = legacy;
    for (const voucher of testCase.vouchers) {
      const amountLine = amountLineHex(testCase, voucher.amountCents);
      assert.equal(expected.includes(amountLine), true, `${name}: 找不到 Amount 行`);
    }
    // 逐张券在各自 Amount 行之后插入到期行（金额不同的多券用金额行定位；同金额不会出现在这些用例里）。
    for (const voucher of testCase.vouchers) {
      const amountLine = amountLineHex(testCase, voucher.amountCents);
      expected = expected.replace(
        amountLine,
        `${amountLine}${escLineHex("Valid until: 2027-01-05", "center", true)}`,
      );
    }
    expected = expected.replaceAll(
      FEED_AND_CUT_HEX,
      `${termsHexFor(testCase.paper)}${FEED_AND_CUT_HEX}`,
    );
    assert.equal(
      encodeGolden(testCase, { expiresAtIso: expiries, businessTimeZone: "Australia/Brisbane" }),
      expected,
      name,
    );
  }
});

test("多张券各印各自的到期日；缺到期日的那张只省略到期行，使用说明照常", () => {
  const testCase = GOLDEN_CASES["58mm-en-two"]!;
  const hex = encodeGolden(testCase, {
    expiresAtIso: ["2027-01-05T12:59:59.000Z", null],
    businessTimeZone: "Australia/Brisbane",
  });
  const text = Buffer.from(hex, "hex").toString("latin1");

  assert.equal(text.match(/Valid until: 2027-01-05/gu)?.length, 1);
  assert.equal(text.match(/VOUCHER TERMS/gu)?.length, 2, "每张券面都带使用说明");
  assert.ok(text.indexOf("Voucher: RF-A") < text.indexOf("Valid until: 2027-01-05"));
  assert.ok(text.indexOf("Valid until: 2027-01-05") < text.indexOf("Voucher: RF-B"));
});

test("到期日按 businessTimeZone 取日历日，而不是设备时区（两个会差一天的时区场景）", () => {
  const testCase = GOLDEN_CASES["80mm-en-nopolicy"]!;
  const dateOf = (expiresAtIso: string, businessTimeZone: string | undefined): string | null => {
    const text = Buffer.from(
      encodeGolden(testCase, {
        expiresAtIso: [expiresAtIso],
        ...(businessTimeZone !== undefined ? { businessTimeZone } : {}),
      }),
      "hex",
    ).toString("latin1");
    return text.match(/Valid until: (\d{4}-\d{2}-\d{2})/u)?.[1] ?? null;
  };

  // 12:59:59Z：布里斯班(+10) 仍是 1 月 5 日 22:59，奥克兰(+13) 已是 1 月 6 日 01:59。
  assert.equal(dateOf(SYDNEY_END_OF_DAY_UTC, "Australia/Brisbane"), "2027-01-05");
  assert.equal(dateOf(SYDNEY_END_OF_DAY_UTC, "Pacific/Auckland"), "2027-01-06");
  // 03:00:00Z：布里斯班 13:00 仍是 1 月 5 日，洛杉矶(-8) 还停在 1 月 4 日 19:00。
  assert.equal(dateOf("2027-01-05T03:00:00.000Z", "Australia/Brisbane"), "2027-01-05");
  assert.equal(dateOf("2027-01-05T03:00:00.000Z", "America/Los_Angeles"), "2027-01-04");
  // 悉尼夏令时（+11）与悉尼标准时（+10）门店的当天 23:59:59，用布里斯班口径都得到同一日历日。
  assert.equal(dateOf("2027-01-05T12:59:59.000Z", "Australia/Brisbane"), "2027-01-05");
  assert.equal(dateOf("2026-07-05T13:59:59.000Z", "Australia/Brisbane"), "2026-07-05");
});

test("businessTimeZone 缺失或为空白时沿用 Australia/Brisbane；非法时区只省略到期行且不抛错", () => {
  const testCase = GOLDEN_CASES["80mm-en-nopolicy"]!;
  const render = (businessTimeZone: string | undefined): string =>
    Buffer.from(
      encodeGolden(testCase, {
        expiresAtIso: [SYDNEY_END_OF_DAY_UTC],
        ...(businessTimeZone !== undefined ? { businessTimeZone } : {}),
      }),
      "hex",
    ).toString("latin1");

  assert.match(render(undefined), /Valid until: 2027-01-05/u);
  assert.match(render("   "), /Valid until: 2027-01-05/u);
  assert.match(render("Pacific/Auckland"), /Valid until: 2027-01-06/u);

  const invalid = render("Not/A_Zone");
  assert.doesNotMatch(invalid, /Valid until/u);
  assert.match(invalid, /VOUCHER TERMS/u, "时区失败不影响使用说明");
  assert.match(invalid, /Voucher: RF123/u, "时区失败不影响出票");
});

test("到期日缺失、null、空串、非日期串、非字符串时只省略到期行，字节与不带到期日完全一致", () => {
  const testCase = GOLDEN_CASES["58mm-en-policy"]!;
  const baseline = encodeGolden(testCase);
  // 对照：同一入参给出合法到期日时确实多一行，保证下面的「一致」不是因为功能整体缺失。
  assert.notEqual(
    encodeGolden(testCase, { expiresAtIso: [SYDNEY_END_OF_DAY_UTC], businessTimeZone: "Australia/Brisbane" }),
    baseline,
  );
  for (const bad of [undefined, null, "", "   ", "not-a-date", "2027-13-45T99:00:00Z"]) {
    assert.equal(
      encodeGolden(testCase, { expiresAtIso: [bad], businessTimeZone: "Australia/Brisbane" }),
      baseline,
      `expiresAtIso=${JSON.stringify(bad)}`,
    );
  }
  const nonString = encodeRefundVoucherDocuments({
    settings: goldenSettings(testCase),
    storeCode: "S001",
    orderLabel: "return-order-1",
    vouchers: [{ voucherCode: "RF123", amountCents: 800, expiresAtIso: 20270105 as unknown as string }],
    printedAt: PRINTED_AT,
  });
  assert.equal(hexOf(nonString.receiptBytes), baseline);
});

test("服务端 +00:00 形态与规范 Z 形态的到期时刻得到同一日期", () => {
  const testCase = GOLDEN_CASES["80mm-en-nopolicy"]!;
  const canonical = encodeGolden(testCase, {
    expiresAtIso: ["2027-01-05T12:59:59.000Z"],
    businessTimeZone: "Australia/Brisbane",
  });
  assert.match(
    Buffer.from(canonical, "hex").toString("latin1"),
    /Valid until: 2027-01-05/u,
  );
  for (const raw of ["2027-01-05T12:59:59+00:00", "2027-01-05T12:59:59.000+00:00"]) {
    assert.equal(
      encodeGolden(testCase, { expiresAtIso: [raw], businessTimeZone: "Australia/Brisbane" }),
      canonical,
      raw,
    );
  }
});

test("使用说明在 QR 之后、走纸切纸之前；到期行紧随 Amount 行、早于订单行；标题与条款样式固定", () => {
  const testCase = GOLDEN_CASES["80mm-en-nopolicy"]!;
  const hex = encodeGolden(testCase, {
    expiresAtIso: [SYDNEY_END_OF_DAY_UTC],
    businessTimeZone: "Australia/Brisbane",
  });
  const qrPrintHex = "1d286b0300315130"; // GS ( k ... 打印 QR 符号
  const qrIndex = hex.lastIndexOf(qrPrintHex);
  const termsTitleIndex = hex.indexOf(escLineHex("VOUCHER TERMS", "center", true));
  const cutIndex = hex.lastIndexOf(FEED_AND_CUT_HEX);
  const amountIndex = hex.indexOf(amountLineHex(testCase, 800));
  const validUntilIndex = hex.indexOf(escLineHex("Valid until: 2027-01-05", "center", true));
  const orderIndex = hex.indexOf(escLineHex("Order: return-order-1", "left", false));

  assert.ok(qrIndex > 0 && termsTitleIndex > qrIndex, "使用说明必须在 QR 之后");
  assert.ok(termsTitleIndex < cutIndex, "使用说明必须在切纸之前");
  assert.equal(
    validUntilIndex,
    amountIndex + amountLineHex(testCase, 800).length,
    "到期行紧跟 Amount 行",
  );
  assert.ok(validUntilIndex < orderIndex);
  // 分隔线（左对齐、不加粗）紧贴在标题之前，其前是一行空行，空行紧跟 QR。
  const separatorHex = escLineHex("-".repeat(48), "left", false);
  assert.equal(
    hex.slice(qrIndex + qrPrintHex.length, termsTitleIndex),
    `${escLineHex("", "left", false)}${separatorHex}`,
  );
});

test("使用说明每一行都不超过渲染器自己的纸宽（58mm=32、80mm=48），文案逐字不变", () => {
  for (const [paper, width, expectedLines] of [
    ["58mm", 32, WRAPPED_TERMS_58MM],
    ["80mm", 48, WRAPPED_TERMS_80MM],
  ] as const) {
    const rendered = encodeRefundVoucherDocuments({
      settings: { ...settings(), paper },
      storeCode: "S001",
      orderLabel: "return-order-1",
      vouchers: [{ voucherCode: "RF123", amountCents: 800 }],
      printedAt: PRINTED_AT,
    });
    const lines = encoder.decode(rendered.receiptBytes).split("\n");
    const titleIndex = lines.findIndex((line) => line.endsWith("VOUCHER TERMS"));
    const termsLines = lines.slice(titleIndex + 1).map((line) => line.replace(/^.{6}/su, ""));
    const printed = termsLines.slice(0, expectedLines.length);

    assert.ok(titleIndex > 0, `${paper}: 找不到使用说明标题`);
    assert.deepEqual(printed, [...expectedLines], paper);
    assert.ok(printed.every((line) => line.length <= width), `${paper}: 条款行不得超过 ${width} 字符`);
    // 换行只发生在单词边界：拼回去必须等于业主审定的原句。
    assert.equal(printed.join(" "), REFUND_VOUCHER_TERMS.lines.join(" "));
  }
});

test("zh-CN 下到期行与使用说明保持英文，原有中文标签不变", () => {
  const testCase = GOLDEN_CASES["80mm-zh"]!;
  const hex = encodeGolden(testCase, {
    expiresAtIso: [SYDNEY_END_OF_DAY_UTC],
    businessTimeZone: "Australia/Brisbane",
  });

  assert.equal(hex.includes(escLineHex("Valid until: 2027-01-05", "center", true)), true);
  assert.equal(hex.includes(escLineHex("VOUCHER TERMS", "center", true)), true);
  assert.equal(hex.includes(escLineHex("Not redeemable for cash.", "left", false)), true);
  assert.equal(hex.includes(escLineHex("券码: RF123", "center", true)), true);
  assert.equal(hex.includes(escLineHex("打印时间: 2026-07-10 09:30:00", "left", false)), true);
});

test("退货首次打印（纯券）：材料里的到期日经渲染器按构造入参的 businessTimeZone 印出", async () => {
  const render = async (
    businessTimeZone: string | undefined,
    expiresAtIso: string | null | undefined,
  ): Promise<string> => {
    const renderer = new ProtectedRefundVoucherReceiptRenderer(
      { async getByGuid() { return pureVoucherReturn(); } },
      {
        async resolveApprovedRefundVouchers() {
          return [{
            ...protectedMaterial(),
            ...(expiresAtIso !== undefined ? { expiresAtIso } : {}),
          }];
        },
      },
      { async getFrozenReturnReceiptSettings() { return settings(); } },
      () => PRINTED_AT,
      businessTimeZone,
    );
    return encoder.decode(
      (await renderer.render("return-action-1", "return-order-1")).receiptBytes,
    );
  };

  assert.match(await render("Australia/Brisbane", SYDNEY_END_OF_DAY_UTC), /Valid until: 2027-01-05/u);
  assert.match(await render("Pacific/Auckland", SYDNEY_END_OF_DAY_UTC), /Valid until: 2027-01-06/u);
  assert.match(await render(undefined, SYDNEY_END_OF_DAY_UTC), /Valid until: 2027-01-05/u);
  for (const missing of [undefined, null, "garbage"]) {
    const text = await render("Australia/Brisbane", missing);
    assert.doesNotMatch(text, /Valid until/u);
    assert.match(text, /VOUCHER TERMS/u);
    assert.match(text, /Voucher: RF123/u);
  }
});

test("退货首次打印（刷卡+两张券混合）：每张券各自带自己的到期日，与材料返回顺序无关", async () => {
  const base = pureVoucherReturn();
  const mixed: LocalOrder = {
    ...base,
    total: { currency: "AUD", cents: -2000 },
    actualAmount: { currency: "AUD", cents: -2000 },
    tenders: [
      { tenderGuid: "card-tender-1", method: "card", amount: { currency: "AUD", cents: -1000 }, reference: null, reservationToken: null },
      { tenderGuid: "voucher-tender-1", method: "voucher", amount: { currency: "AUD", cents: -600 }, reference: null, reservationToken: null },
      { tenderGuid: "voucher-tender-2", method: "voucher", amount: { currency: "AUD", cents: -400 }, reference: null, reservationToken: null },
    ],
  };
  const renderer = new ProtectedRefundVoucherReceiptRenderer(
    { async getByGuid() { return mixed; } },
    {
      async resolveApprovedRefundVouchers() {
        return [
          // 故意把 RF-B 放在前面；旧券(12 个月)与新券(90 天)到期日不同。
          { ...protectedMaterial(), voucherCode: "RF-B", refundAmountCents: 400, expiresAtIso: "2027-07-09T13:59:59.000Z" },
          { ...protectedMaterial(), voucherCode: "RF-A", refundAmountCents: 600 },
        ];
      },
    },
    { async getFrozenReturnReceiptSettings() { return settings(); } },
    () => PRINTED_AT,
    "Australia/Brisbane",
  );

  const text = encoder.decode(
    (await renderer.render("return-action-1", "return-order-1")).receiptBytes,
  );
  const rfB = text.indexOf("Voucher: RF-B");
  const rfA = text.indexOf("Voucher: RF-A");
  const dateIndex = text.indexOf("Valid until: 2027-07-09");

  assert.equal(text.match(/Valid until/gu)?.length, 1, "只有带到期日的 RF-B 印到期行");
  assert.ok(rfB >= 0 && rfA > rfB && dateIndex > rfB && dateIndex < rfA, "到期行属于 RF-B 的券面");
  assert.equal(text.match(/VOUCHER TERMS/gu)?.length, 2);
});
