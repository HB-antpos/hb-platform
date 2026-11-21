import assert from "node:assert/strict";
import test from "node:test";

import { receiptCode128 } from "@hb/pos-receipt-core/features/receipts/receipt-code128";
import type {
  FrozenReceiptReprintSettings,
  ReceiptReprintSettingsSource,
} from "@hb/pos-receipt-core/features/receipts/receipt-reprint-service";
import {
  isRemoteHistoryReceiptReprintEligible,
  RemoteHistoryReceiptReprintPreparationService,
} from "@hb/pos-receipt-core/features/receipts/remote-history-receipt-reprint-service";

import type {
  RemoteOrderHistoryDetails,
  RemoteOrderHistoryPort,
} from "@hb/pos-domain/core/contracts/remote-history";

const decoder = new TextDecoder();
const orderGuid = "12345678-1234-1234-1234-abcdef47c164";

const settings: FrozenReceiptReprintSettings = {
  printerId: "printer-001",
  paper: "58mm",
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

function details(
  overrides: Partial<RemoteOrderHistoryDetails> = {},
): RemoteOrderHistoryDetails {
  return {
    orderGuid,
    storeCode: "BNE",
    deviceCode: "POS_1042_1327",
    cashierName: "Alice",
    soldAtIso: "2026-08-02T08:06:07.000Z",
    totalCents: 500,
    discountCents: 100,
    actualAmountCents: 400,
    lines: [{
      orderLineGuid: "12345678-1234-1234-1234-000000000001",
      productCode: "P-1",
      referenceCode: "REF-1",
      displayName: "Spring water",
      lookupCode: "930000000001",
      itemNumber: "SKU-1",
      quantity: "1",
      unitPriceCents: 500,
      discountCents: 100,
      actualAmountCents: 400,
      kind: "sale",
    }],
    payments: [{
      paymentGuid: "12345678-1234-1234-1234-000000000002",
      method: "card",
      amountCents: 400,
      displayReference: "****4321",
      cardType: "SECRET-CARD-TYPE",
      maskedCardNumber: "****9999",
    }],
    ...overrides,
  };
}

function createService(input: Readonly<{
  response?: RemoteOrderHistoryDetails | null;
  trustedStoreCode?: string;
  settingValue?: FrozenReceiptReprintSettings | null;
  getDetails?: RemoteOrderHistoryPort["getDetails"];
}>) {
  const detailCalls: string[] = [];
  let settingsCalls = 0;
  const history: Pick<RemoteOrderHistoryPort, "getDetails"> = {
    getDetails: async (requestedOrderGuid) => {
      detailCalls.push(requestedOrderGuid);
      return input.getDetails
        ? input.getDetails(requestedOrderGuid)
        : (input.response ?? null);
    },
  };
  const settingSource: ReceiptReprintSettingsSource = {
    getFrozenReceiptSettings: async () => {
      settingsCalls += 1;
      return input.settingValue === undefined ? settings : input.settingValue;
    },
  };
  return {
    detailCalls,
    get settingsCalls() {
      return settingsCalls;
    },
    service: new RemoteHistoryReceiptReprintPreparationService({
      history,
      settings: settingSource,
      trustedStoreCode: input.trustedStoreCode ?? "bne",
    }),
  };
}

test("远程历史重打资格只接受正额、非现金且明细与付款精确对账的订单", () => {
  assert.equal(isRemoteHistoryReceiptReprintEligible(details()), true);
  assert.equal(isRemoteHistoryReceiptReprintEligible(details({
    payments: [
      { ...details().payments[0]!, amountCents: 200 },
      {
        ...details().payments[0]!,
        paymentGuid: "12345678-1234-1234-1234-000000000003",
        method: "voucher",
        amountCents: 200,
      },
    ],
  })), true);

  const ineligible: RemoteOrderHistoryDetails[] = [
    details({ actualAmountCents: 0, lines: [], payments: [] }),
    details({ lines: [] }),
    details({ payments: [] }),
    details({ discountCents: 99 }),
    details({ lines: [{ ...details().lines[0]!, kind: "return" }] }),
    details({ lines: [{ ...details().lines[0]!, quantity: "0" }] }),
    details({
      lines: [
        { ...details().lines[0]!, actualAmountCents: -100 },
        {
          ...details().lines[0]!,
          orderLineGuid: "12345678-1234-1234-1234-000000000004",
          actualAmountCents: 500,
        },
      ],
    }),
    details({
      payments: [
        { ...details().payments[0]!, amountCents: -100 },
        {
          ...details().payments[0]!,
          paymentGuid: "12345678-1234-1234-1234-000000000005",
          amountCents: 500,
        },
      ],
    }),
    details({ lines: [{ ...details().lines[0]!, actualAmountCents: 399 }] }),
    details({ payments: [{ ...details().payments[0]!, amountCents: 399 }] }),
    details({ payments: [{ ...details().payments[0]!, method: "cash" }] }),
  ];
  for (const value of ineligible) {
    assert.equal(isRemoteHistoryReceiptReprintEligible(value), false);
  }
});

test("prepare 重新读取严格订单，冻结一次设置并生成带订单短码条码的重打票据", async () => {
  const harness = createService({ response: details({ storeCode: "BnE" }) });

  const prepared = await harness.service.prepare(orderGuid);
  const receipt = decoder.decode(prepared?.receiptBytes);

  assert.deepEqual(harness.detailCalls, [orderGuid]);
  assert.equal(harness.settingsCalls, 1);
  assert.equal(prepared?.orderGuid, orderGuid);
  assert.equal(prepared?.printerId, "printer-001");
  assert.match(receipt, /\*\*\* REPRINT \*\*\*/u);
  // 对齐 WPF：顶部是 GUID 末 8 位短码（EF47C164）的 Code128，下方印可读短码；不再印完整 GUID 文本。
  const payload = receiptCode128("EF47C164").payload;
  assert.ok(receipt.includes(payload));
  assert.ok(receipt.indexOf(payload) < receipt.indexOf("Date:"));
  assert.ok(receipt.indexOf("EF47C164", receipt.indexOf(payload) + payload.length) > receipt.indexOf(payload));
  assert.doesNotMatch(receipt, /12345678-1234-1234-1234|abcdef47/iu);
  assert.doesNotMatch(receipt, /Order:|#EF47C164/u);
  assert.match(receipt, /Ref: \*\*\*\*4321/u);
  assert.doesNotMatch(receipt, /SECRET-CARD-TYPE|\*\*\*\*9999/u);
});

test("prepare 对返回订单或可信门店不一致严格 fail closed，且不读取打印设置", async () => {
  for (const response of [
    details({ orderGuid: `${orderGuid}-other` }),
    details({ storeCode: "SYD" }),
  ]) {
    const harness = createService({ response });
    assert.equal(await harness.service.prepare(orderGuid), null);
    assert.deepEqual(harness.detailCalls, [orderGuid]);
    assert.equal(harness.settingsCalls, 0);
  }
});

test("prepare 对现金、零额、对账差异、缺失设置和读取异常保守拒绝", async () => {
  const rejected = [
    details({ actualAmountCents: 0, lines: [], payments: [] }),
    details({ payments: [{ ...details().payments[0]!, method: "cash" }] }),
    details({ discountCents: 99 }),
    details({ lines: [{ ...details().lines[0]!, kind: "return" }] }),
    details({ lines: [{ ...details().lines[0]!, actualAmountCents: 399 }] }),
    details({ payments: [{ ...details().payments[0]!, amountCents: 399 }] }),
  ];
  for (const response of rejected) {
    assert.equal(await createService({ response }).service.prepare(orderGuid), null);
  }

  assert.equal(await createService({
    response: details(),
    settingValue: null,
  }).service.prepare(orderGuid), null);
  assert.equal(await createService({
    response: details(),
    settingValue: { ...settings, printerId: " " },
  }).service.prepare(orderGuid), null);
  assert.equal(await createService({
    getDetails: async () => {
      throw new Error("network unavailable");
    },
  }).service.prepare(orderGuid), null);
});
