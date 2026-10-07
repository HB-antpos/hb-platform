import assert from "node:assert/strict";
import test from "node:test";

import {
  ProtectedRefundVoucherReceiptRenderer,
  type ProtectedRefundVoucherPrintMaterial,
} from "./refund-voucher-receipt-renderer";
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
