import assert from "node:assert/strict";
import test from "node:test";

import type { LocalOrder } from "../contracts";

import {
  OrderRepositoryPaymentCompletionProjection,
  OrderRepositoryPaymentReceiptRenderer,
  PersistedPaymentCompletionSettings,
} from "./payment-completion-runtime";

test("完成投影只累加持久 tender，并保留 reversal 符号", async () => {
  const projection = new OrderRepositoryPaymentCompletionProjection({
    async getByGuid() {
      return order({
        tenders: [
          tender("cash-1", "cash", 400),
          tender("cash-reversal", "cash", -100),
        ],
      });
    },
  });

  assert.deepEqual(await projection.read("order-1"), {
    orderGuid: "order-1",
    total: { currency: "AUD", cents: 1_000 },
    paid: { currency: "AUD", cents: 300 },
  });
});

test("完成设置不为混合现金自动打印，并从可信 session 决定钱箱权限", async () => {
  const settings = new PersistedPaymentCompletionSettings(
    {
      async getReceiptPrinterSettings() {
        return receiptSettings();
      },
    },
    { canOpenCashDrawer: () => true },
  );

  assert.deepEqual(await settings.load(), {
    printerId: "printer-1",
    automaticPrint: {
      cash: false,
      card: true,
      voucher: true,
    },
    cashDrawerEnabled: true,
    cashDrawerPermissionAllowed: true,
  });
});

test("支付小票在批准 tender 落库前以持久订单加计划 tender 渲染且不含 provider reference", async () => {
  const renderer = new OrderRepositoryPaymentReceiptRenderer(
    {
      async getByGuid() {
        return order({
          tenders: [tender("cash-1", "cash", 400)],
        });
      },
    },
    {
      async getReceiptPrinterSettings() {
        return receiptSettings();
      },
    },
  );

  const text = new TextDecoder().decode(
    await renderer.render({
      orderGuid: "order-1",
      method: "card",
      amount: { currency: "AUD", cents: 600 },
      attemptId: "attempt-sensitive-reference",
    }),
  );

  assert.match(text, /Cash\s+\$4\.00/);
  assert.match(text, /Card\s+\$6\.00/);
  assert.match(text, /order-1\n.*Date:/s);
  assert.doesNotMatch(text, /Order: #1/);
  assert.doesNotMatch(text, /attempt-sensitive-reference/);
});

test("支付小票拒绝与订单总额不一致的计划 tender", async () => {
  const renderer = new OrderRepositoryPaymentReceiptRenderer(
    {
      async getByGuid() {
        return order({
          tenders: [tender("cash-1", "cash", 400)],
        });
      },
    },
    {
      async getReceiptPrinterSettings() {
        return receiptSettings();
      },
    },
  );

  await assert.rejects(
    () =>
      renderer.render({
        orderGuid: "order-1",
        method: "card",
        amount: { currency: "AUD", cents: 500 },
        attemptId: "attempt-1",
      }),
    /TENDER_TOTAL_MISMATCH/,
  );
});

test("支付完成票据拒绝账本中的 ESC/POS 控制字符", async () => {
  const unsafe = order({
    tenders: [tender("cash-1", "cash", 400)],
    lines: [{ ...order().lines[0]!, lookupCode: "9300\u001bpulse" }],
  });
  const renderer = new OrderRepositoryPaymentReceiptRenderer(
    { async getByGuid() { return unsafe; } },
    { async getReceiptPrinterSettings() { return receiptSettings(); } },
  );

  await assert.rejects(
    () => renderer.render({
      orderGuid: "order-1",
      method: "card",
      amount: { currency: "AUD", cents: 600 },
      attemptId: "attempt-unsafe",
    }),
    /control characters/i,
  );
});

function order(
  overrides: Partial<LocalOrder> = {},
): LocalOrder {
  return {
    orderGuid: "order-1",
    localSequence: 1,
    storeCode: "S001",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Cashier",
    soldAtIso: "2026-07-28T00:00:00.000Z",
    state: "Completing",
    total: { currency: "AUD", cents: 1_000 },
    discount: { currency: "AUD", cents: 0 },
    actualAmount: { currency: "AUD", cents: 1_000 },
    lines: [
      {
        lineId: "line-1",
        productCode: "P-1",
        itemNumber: "I-1",
        lookupCode: "930000000001",
        displayName: "Milk",
        quantity: "1",
        unitPrice: { currency: "AUD", cents: 1_000 },
        discount: { currency: "AUD", cents: 0 },
        actualAmount: { currency: "AUD", cents: 1_000 },
        priceSource: "catalog",
        kind: "sale",
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
      },
    ],
    tenders: [],
    originalOrderGuid: null,
    ...overrides,
  };
}

function tender(
  tenderGuid: string,
  method: "cash" | "card" | "voucher",
  cents: number,
) {
  return {
    tenderGuid,
    method,
    amount: { currency: "AUD" as const, cents },
    reference: null,
    reservationToken: null,
  };
}

function receiptSettings() {
  return {
    printEnabled: true,
    drawerEnabled: true,
    peripheralId: "printer-1",
    paper: "80mm" as const,
    locale: "en" as const,
    brandName: "Hot Bargain",
    storeName: "Brisbane",
    address: "1 Queen St",
    phone: "0712345678",
    abn: "12 345 678 901",
    returnPolicy: "",
    voucherTerms: "",
    installmentTerms: "",
    profileStoreCode: "S001",
    profileVersion: 0,
    profileAckedVersion: 0,
  };
}
