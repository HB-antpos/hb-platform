import assert from "node:assert/strict";
import test from "node:test";

import {
  LocalHistoryReceiptPreviewService,
  type ReceiptPreviewSettingsSource,
} from "./receipt-preview-service";
import type {
  ReceiptCompletionSettlementSource,
  ReceiptReprintOrderSource,
} from "@hb/pos-receipt-core/features/receipts/receipt-reprint-service";

import { createAud, type LocalOrder } from "@/core/contracts";

function order(overrides: Partial<LocalOrder> = {}): LocalOrder {
  return {
    orderGuid: "550e8400-e29b-41d4-a716-446655440000",
    localSequence: 1084,
    storeCode: "BNE",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Alice",
    soldAtIso: "2026-08-02T03:04:05.000Z",
    state: "Synced",
    total: createAud(1_100),
    discount: createAud(100),
    actualAmount: createAud(1_000),
    lines: [{
      lineId: "line-1",
      productCode: "P-1",
      itemNumber: "SKU-1",
      lookupCode: "123456",
      displayName: "中文长商品名称 Spring water",
      quantity: "1",
      unitPrice: createAud(1_100),
      discount: createAud(100),
      actualAmount: createAud(1_000),
      priceSource: "catalog",
      kind: "sale",
      returnSourceKey: null,
      originalOrderGuid: null,
      originalOrderDetailGuid: null,
    }],
    tenders: [{
      tenderGuid: "tender-1",
      method: "card",
      amount: createAud(1_000),
      reference: "4111111111111234",
      reservationToken: "must-never-preview",
    }],
    originalOrderGuid: null,
    ...overrides,
  };
}

function localReceiptTime(value: string): string {
  const date = new Date(value);
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

function service(
  value: LocalOrder | null,
  options: Readonly<{
    brandName?: string;
    settlement?: number | null;
  }> = {},
): LocalHistoryReceiptPreviewService {
  const orders: ReceiptReprintOrderSource = {
    getByOrderGuid: async (orderGuid) =>
      value?.orderGuid === orderGuid ? value : null,
    getLastByLocalSequence: async () => value,
  };
  const settings: ReceiptPreviewSettingsSource = {
    getFrozenReceiptPreviewSettings: async () => ({
      paper: "80mm",
      locale: "en",
      store: {
        brandName: options.brandName ?? "",
        storeName: "Brisbane",
        address: "1 Queen St",
        phone: "0712345678",
        abn: "12 345 678 901",
        returnPolicy: "",
      },
    }),
  };
  const settlements: ReceiptCompletionSettlementSource = {
    getCompletionSettlement: async () =>
      options.settlement === null || options.settlement === undefined
        ? null
        : { cashChangeCents: options.settlement },
  };
  return new LocalHistoryReceiptPreviewService({ orders, settings, settlements });
}

test("未配置打印机也能预览，并以可信 storeCode 回退空品牌", async () => {
  const current = order();
  const preview = await service(current).getPreview(current.orderGuid);

  assert.equal(preview?.paper, "80mm");
  assert.ok(preview?.lines.some((line) => line.kind === "text" && line.text === "BNE"));
  assert.ok(preview?.lines.some((line) => line.kind === "text" && line.text === "Brisbane"));
  assert.ok(preview?.lines.some((line) => line.kind === "text" && line.text === "1 Queen St"));
  assert.ok(preview?.lines.some(
    (line) => line.kind === "text" && line.text === current.orderGuid,
  ));
  assert.equal(preview?.lines.some(
    (line) => line.kind === "text" && /Order:|#1084/.test(line.text),
  ), false);
  assert.ok(preview?.lines.some(
    (line) => line.kind === "text" && line.text === "*** REPRINT ***",
  ));
});

test("预览只保留掩码引用，机器码固定编码完整规范化 orderGuid", async () => {
  const current = order();
  const preview = await service(current, { brandName: "Hot Bargain" }).getPreview(current.orderGuid);
  const visibleText = preview?.lines
    .filter((line) => line.kind === "text" || line.kind === "separator")
    .map((line) => line.text)
    .join("\n") ?? "";

  assert.match(visibleText, /\*\*\*\*1234/);
  assert.doesNotMatch(visibleText, /4111111111111234|must-never-preview/);
  assert.equal(preview?.lines.some((line) => line.kind === "barcode"), false);
  assert.ok(preview?.lines.some((line) => line.kind === "qr" && line.value === current.orderGuid));
});

test("现金订单缺少持久化完成审计时拒绝猜测找零", async () => {
  const cash = order({
    tenders: [{
      tenderGuid: "cash-1",
      method: "cash",
      amount: createAud(1_000),
      reference: null,
      reservationToken: null,
    }],
  });

  assert.equal(await service(cash).getPreview(cash.orderGuid), null);
  assert.ok(await service(cash, { settlement: 250 }).getPreview(cash.orderGuid));
});

test("非可打印 ASCII 的异常订单号不进入条码或预览 UI", async () => {
  const invalid = order({ orderGuid: "订单-1084" });

  assert.equal(await service(invalid).getPreview(invalid.orderGuid), null);
});

test("预览的打印时间固定使用订单成交时间", async () => {
  const current = order();
  const preview = await service(current).getPreview(current.orderGuid);

  assert.ok(preview?.lines.some(
    (line) => line.kind === "text"
      && line.text.includes(localReceiptTime(current.soldAtIso)),
  ));
  assert.ok(preview?.lines.some(
    (line) => line.kind === "text" && line.text === "*** Paid ***",
  ));
});

test("退款订单（净额为负）预览与补打状态行打印 Refund，而不是 Paid（对齐 WPF #885）", async () => {
  const refund = order({
    originalOrderGuid: "99999999-9999-4999-8999-999999999999",
    total: createAud(-1_000),
    discount: createAud(0),
    actualAmount: createAud(-1_000),
    lines: [{
      ...order().lines[0]!,
      quantity: "-1",
      unitPrice: createAud(1_000),
      discount: createAud(0),
      actualAmount: createAud(-1_000),
      kind: "return",
      returnSourceKey: "receipt:o:l",
      originalOrderGuid: "99999999-9999-4999-8999-999999999999",
      originalOrderDetailGuid: "line-orig",
    }],
    tenders: [{
      tenderGuid: "tender-refund",
      method: "card",
      amount: createAud(-1_000),
      reference: "4111111111111234",
      reservationToken: null,
    }],
  });
  const preview = await service(refund).getPreview(refund.orderGuid);

  assert.ok(preview?.lines.some(
    (line) => line.kind === "text" && line.text === "*** Refund ***",
  ));
  assert.equal(preview?.lines.some(
    (line) => line.kind === "text" && line.text === "*** Paid ***",
  ), false);
});
