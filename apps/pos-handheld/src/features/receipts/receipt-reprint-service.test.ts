import assert from "node:assert/strict";
import test from "node:test";

import {
  OrderRepositoryReceiptReprintSource,
  ReceiptReprintPreparationService,
  type FrozenReceiptReprintSettings,
  type ReceiptCompletionSettlementSource,
  type ReceiptReprintOrderSource,
  type ReceiptReprintSettingsSource,
} from "@hb/pos-receipt-core/features/receipts/receipt-reprint-service";

import { createAud, type LocalOrder, type OrderRepositoryPort } from "@/core/contracts";

const encoder = new TextDecoder();

function localReceiptTime(value: string): string {
  const date = new Date(value);
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

function order(overrides: Partial<LocalOrder> = {}): LocalOrder {
  return {
    orderGuid: "order-100",
    localSequence: 100,
    storeCode: "BNE",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Alice",
    soldAtIso: "2026-07-28T10:11:12.000Z",
    state: "PendingSync",
    total: createAud(782),
    discount: createAud(20),
    actualAmount: createAud(762),
    lines: [{
      lineId: "line-1",
      productCode: "P-1",
      itemNumber: "SKU-1",
      lookupCode: "123456",
      displayName: "Spring water",
      quantity: "1",
      unitPrice: createAud(782),
      discount: createAud(20),
      actualAmount: createAud(762),
      priceSource: "catalog",
      kind: "sale",
      returnSourceKey: null,
      originalOrderGuid: null,
      originalOrderDetailGuid: null,
    }],
    tenders: [{
      tenderGuid: "tender-1",
      method: "cash",
      amount: createAud(762),
      reference: null,
      reservationToken: null,
    }],
    originalOrderGuid: null,
    ...overrides,
  };
}

const settings: FrozenReceiptReprintSettings = {
  printerId: "xp-q200",
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

class MemoryOrderSource implements ReceiptReprintOrderSource {
  public currentCalls: string[] = [];
  public lastCalls = 0;

  public constructor(
    private readonly byGuid: ReadonlyMap<string, LocalOrder>,
    private readonly last: LocalOrder | null,
  ) {}

  public async getByOrderGuid(orderGuid: string): Promise<LocalOrder | null> {
    this.currentCalls.push(orderGuid);
    return this.byGuid.get(orderGuid) ?? null;
  }

  public async getLastByLocalSequence(): Promise<LocalOrder | null> {
    this.lastCalls += 1;
    return this.last;
  }
}

function service(
  orders: ReceiptReprintOrderSource,
  settingValue: FrozenReceiptReprintSettings | null = settings,
  settlementValue: number | null = 238,
  nowIso?: () => string,
) {
  const settingSource: ReceiptReprintSettingsSource = {
    getFrozenReceiptSettings: async () => settingValue,
  };
  const settlementSource: ReceiptCompletionSettlementSource = {
    getCompletionSettlement: async () => (
      settlementValue === null ? null : { cashChangeCents: settlementValue }
    ),
  };
  return new ReceiptReprintPreparationService({
    orders,
    settings: settingSource,
    settlements: settlementSource,
    ...(nowIso ? { nowIso } : {}),
  });
}

test("指定当前订单保持原样 orderGuid，绝不被更早或更新的账本订单替换", async () => {
  const earlier = order({ orderGuid: "order-early", localSequence: 2 });
  const selected = order({ orderGuid: "order-selected", localSequence: 10 });
  const later = order({ orderGuid: "order-late", localSequence: 11 });
  const source = new MemoryOrderSource(new Map([
    [earlier.orderGuid, earlier],
    [selected.orderGuid, selected],
    [later.orderGuid, later],
  ]), later);

  const prepared = await service(source).prepareCurrent(selected.orderGuid);

  assert.equal(prepared?.orderGuid, selected.orderGuid);
  assert.deepEqual(source.currentCalls, [selected.orderGuid]);
  assert.equal(source.lastCalls, 0);
});

test("最后订单只经 local_sequence 降序账本查询取得，不读取历史打印作业", async () => {
  const earlier = order({ orderGuid: "order-early", localSequence: 7 });
  const last = order({ orderGuid: "order-last", localSequence: 9 });
  const repository = {
    getByGuid: async () => null,
    listLocal: async (limit: number) => {
      assert.equal(limit, 1);
      return [last, earlier].sort((left, right) => right.localSequence - left.localSequence).slice(0, limit);
    },
  } satisfies Pick<OrderRepositoryPort, "getByGuid" | "listLocal">;
  const source = new OrderRepositoryReceiptReprintSource(repository);

  const prepared = await service(source).prepareLast();

  assert.equal(prepared?.orderGuid, last.orderGuid);
  assert.match(encoder.decode(prepared?.receiptBytes), /\*\*\* REPRINT \*\*\*/);
});

test("没有历史打印作业也能从本地订单账本准备真实重打 ESC/POS bytes", async () => {
  const current = order({ orderGuid: "order-without-print-history" });
  const prepared = await service(
    new MemoryOrderSource(new Map([[current.orderGuid, current]]), current),
    {
      ...settings,
      printerId: " xp-q200 ",
      store: {
        ...settings.store,
        returnPolicy: "Refunds within 14 days with proof of purchase.",
      },
    },
  ).prepareCurrent(current.orderGuid);

  assert.equal(prepared?.orderGuid, current.orderGuid);
  assert.equal(prepared?.printerId, settings.printerId);
  assert.deepEqual(
    [...prepared?.receiptBytes.slice(0, 4) ?? []],
    [0x1b, 0x40, 0x1c, 0x26],
  );
  assert.match(encoder.decode(prepared?.receiptBytes), /\*\*\* REPRINT \*\*\*/);
  assert.match(encoder.decode(prepared?.receiptBytes), /123456\s+1\s+\$7\.62/);
  assert.match(encoder.decode(prepared?.receiptBytes), /order-without-print-history\n.*Date:/s);
  assert.match(encoder.decode(prepared?.receiptBytes), /Refunds and returns/);
  assert.match(encoder.decode(prepared?.receiptBytes), /Refunds within 14 days/);
  assert.doesNotMatch(encoder.decode(prepared?.receiptBytes), /Order: #100/);
});

test("真实重打按 WPF 使用成交时间而不是本次任务时间", async () => {
  const current = order({ orderGuid: "order-frozen-print-time" });
  let now = "2026-08-02T04:05:06.000Z";
  const prepared = await service(
    new MemoryOrderSource(new Map([[current.orderGuid, current]]), current),
    settings,
    238,
    () => now,
  ).prepareCurrent(current.orderGuid);

  now = "2026-08-02T05:06:07.000Z";
  const bytes = encoder.decode(prepared?.receiptBytes);
  assert.match(bytes, new RegExp(`Print Time: ${localReceiptTime(current.soldAtIso)}`));
  assert.doesNotMatch(bytes, new RegExp(localReceiptTime("2026-08-02T04:05:06.000Z")));
  assert.doesNotMatch(bytes, new RegExp(localReceiptTime("2026-08-02T05:06:07.000Z")));
});

test("缺少现金完成审计、打印设置或无订单时 fail closed", async () => {
  const cash = order();
  const missingAudit = await service(new MemoryOrderSource(new Map([[cash.orderGuid, cash]]), cash), settings, null).prepareCurrent(cash.orderGuid);
  const missingSettings = await service(new MemoryOrderSource(new Map([[cash.orderGuid, cash]]), cash), null).prepareCurrent(cash.orderGuid);
  const blankPrinter = await service(new MemoryOrderSource(new Map([[cash.orderGuid, cash]]), cash), { ...settings, printerId: " " }).prepareCurrent(cash.orderGuid);
  const missingOrder = await service(new MemoryOrderSource(new Map(), null)).prepareCurrent("not-found");

  assert.equal(missingAudit, null);
  assert.equal(missingSettings, null);
  assert.equal(blankPrinter, null);
  assert.equal(missingOrder, null);
});

test("现金退单没有找零结算也能重打负数整单小票，且不读取结算记录", async () => {
  const refund = order({
    orderGuid: "return-order-cash",
    total: createAud(-250),
    discount: createAud(0),
    actualAmount: createAud(-250),
    lines: [{
      ...order().lines[0]!,
      displayName: "Pastel pink shredded paper",
      unitPrice: createAud(-250),
      discount: createAud(0),
      actualAmount: createAud(-250),
      kind: "return",
      returnSourceKey: "return:original-order:detail-1",
      originalOrderGuid: "original-order",
      originalOrderDetailGuid: "detail-1",
    }],
    tenders: [{
      tenderGuid: "tender-refund-cash",
      method: "cash",
      amount: createAud(-250),
      reference: null,
      reservationToken: null,
    }],
    originalOrderGuid: "original-order",
  });
  let settlementReads = 0;
  const prepared = await new ReceiptReprintPreparationService({
    orders: new MemoryOrderSource(new Map([[refund.orderGuid, refund]]), refund),
    settings: { getFrozenReceiptSettings: async () => settings },
    settlements: {
      getCompletionSettlement: async () => {
        settlementReads += 1;
        return null;
      },
    },
  }).prepareCurrent(refund.orderGuid);

  assert.equal(prepared?.orderGuid, refund.orderGuid);
  assert.equal(settlementReads, 0);
  const bytes = encoder.decode(prepared?.receiptBytes);
  assert.match(bytes, /\*\*\* REPRINT \*\*\*/);
  // 与 WPF 一致：退单打负数金额的整单小票，付款行为负数现金。
  assert.match(bytes, /Total\(inc GST\)\s+\$-2\.50/);
  assert.match(bytes, /Cash\s+\$-2\.50/);
});

test("零金额订单只接受审计中的零找零", async () => {
  const zero = order({
    orderGuid: "order-zero",
    total: createAud(0),
    discount: createAud(0),
    actualAmount: createAud(0),
    lines: [{ ...order().lines[0]!, unitPrice: createAud(0), discount: createAud(0), actualAmount: createAud(0) }],
    tenders: [],
  });
  const source = new MemoryOrderSource(new Map([[zero.orderGuid, zero]]), zero);

  const prepared = await service(source, settings, 0).prepareCurrent(zero.orderGuid);
  const missingAudit = await service(source, settings, null).prepareCurrent(zero.orderGuid);

  assert.doesNotMatch(encoder.decode(prepared?.receiptBytes), /Change|找零/);
  assert.equal(missingAudit, null);
});

test("卡券引用只输出掩码，PAN、授权码与 reservation token 不能进入 bytes", async () => {
  const card = order({
    orderGuid: "order-card",
    tenders: [{
      tenderGuid: "tender-card",
      method: "card",
      amount: createAud(762),
      reference: "4111111111111234",
      reservationToken: "voucher-token-must-never-print",
    }],
  });
  const prepared = await service(new MemoryOrderSource(new Map([[card.orderGuid, card]]), card)).prepareCurrent(card.orderGuid);
  const bytes = encoder.decode(prepared?.receiptBytes);

  assert.match(bytes, /\*\*\*\*1234/);
  assert.doesNotMatch(bytes, /4111111111111234|voucher-token-must-never-print/);
});

test("历史重打遇到可注入 ESC/POS 的账本文本时 fail closed", async () => {
  const unsafe = order({
    orderGuid: "order-unsafe-receipt",
    lines: [{ ...order().lines[0]!, lookupCode: "9300\u001bpulse" }],
  });
  const prepared = await service(
    new MemoryOrderSource(new Map([[unsafe.orderGuid, unsafe]]), unsafe),
  ).prepareCurrent(unsafe.orderGuid);

  assert.equal(prepared, null);
});
