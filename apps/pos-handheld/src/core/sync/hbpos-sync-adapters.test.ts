import assert from "node:assert/strict";
import test from "node:test";

import { HbposApiError, type HbposTransport, type HbposTransportRequest, type HbposTransportResponse } from "../api/hbpos-api";
import type { AuditEventDraft, LocalOrder } from "@hb/pos-domain/core/contracts/order";
import type { CardSyncEvidenceV1 } from "@hb/pos-domain/core/contracts/payment";
import type { OrderRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";
import { OrderSyncMaterialError } from "../db/sqlite-order-sync-material";

import {
  HbposAuditBatchAdapter,
  HbposOrderSyncAdapter,
  type OrderSyncMaterialResolverPort,
} from "@hb/pos-sync/core/sync/hbpos-sync-adapters";

const orderGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d01";
const lineGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d02";
const tenderGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d03";
const eventGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d04";
const persistedAuditScope = {
  storeCode: "1003",
  deviceCode: "IPAD_1",
} as const;

/** 单元测试模拟的是已由审计仓储读回的事实，必须显式带入库时 scope。 */
function persistedAudits(
  events: readonly AuditEventDraft[],
): readonly AuditEventDraft[] {
  return events.map((event) => ({
    ...event,
    auditScope: event.auditScope ?? persistedAuditScope,
  }));
}

function order(method: "cash" | "card" | "voucher" = "cash"): LocalOrder {
  return {
    orderGuid, localSequence: 7, storeCode: "1003", deviceCode: "IPAD_1", cashierId: "cashier-1", cashierName: "Alice",
    soldAtIso: "2026-07-28T00:00:00.000Z", state: "PendingSync",
    total: { cents: 1234, currency: "AUD" }, discount: { cents: 34, currency: "AUD" }, actualAmount: { cents: 1200, currency: "AUD" },
    lines: [{ lineId: lineGuid, productCode: "P1", itemNumber: "I1", lookupCode: "931234", displayName: "商品", quantity: "1.250", unitPrice: { cents: 1000, currency: "AUD" }, discount: { cents: 34, currency: "AUD" }, actualAmount: { cents: 966, currency: "AUD" }, priceSource: "catalog", syncProvenance: { referenceCode: "MULTI-123", priceSource: 3 }, kind: "sale", returnSourceKey: null, originalOrderGuid: null, originalOrderDetailGuid: null }],
    tenders: [{ tenderGuid, method, amount: { cents: 1200, currency: "AUD" }, reference: method === "cash" ? null : null, reservationToken: null }], originalOrderGuid: null,
  };
}

function cardSyncEvidence(
  overrides: Partial<CardSyncEvidenceV1> = {},
): CardSyncEvidenceV1 {
  return {
    version: 1,
    provider: "square",
    operation: "purchase",
    processor: "Square",
    txnRef: "square-payment-1",
    authCode: "AUTH01",
    cardType: "VISA",
    cardBin: 411111,
    maskedCardNumber: "411111******1111",
    merchantId: "merchant-1",
    responseCode: "00",
    responseText: "APPROVED",
    stan: "123456",
    bankDateTimeIso: "2026-07-28T00:00:00.000Z",
    amountCents: 1_200,
    refundReference: null,
    ...overrides,
  };
}

class FakeOrders implements OrderRepositoryPort {
  public constructor(private readonly value: LocalOrder | null) {}
  public async nextLocalSequence(): Promise<number> { return 1; }
  public async saveDraft(): Promise<void> {}
  public async getByGuid(guid: string): Promise<LocalOrder | null> { return guid === orderGuid ? this.value : null; }
  public async listLocal(): Promise<readonly LocalOrder[]> { return []; }
  public async transition(): Promise<boolean> { return true; }
}

class FakeTransport implements HbposTransport {
  public readonly calls: HbposTransportRequest[] = [];
  public constructor(private readonly reply: unknown | Error) {}
  public async request<T>(request: HbposTransportRequest): Promise<HbposTransportResponse<T>> {
    this.calls.push(request);
    if (this.reply instanceof Error) throw this.reply;
    return this.reply as HbposTransportResponse<T>;
  }
}

function orderAdapter(reply: unknown | Error, local = order()) {
  const transport = new FakeTransport(reply);
  return { transport, adapter: new HbposOrderSyncAdapter(transport, new FakeOrders(local)) };
}

function trustedOrderAdapter(
  resolver: OrderSyncMaterialResolverPort,
  local: LocalOrder = order("card"),
  linklyEnvironment: string | null = "Sandbox",
) {
  const transport = new FakeTransport({
    status: 200,
    data: {
      success: true,
      data: { orderGuid, accepted: true, alreadySynced: false },
    },
  });
  return {
    transport,
    adapter: new HbposOrderSyncAdapter(
      transport,
      new FakeOrders(local),
      { resolver, linklyEnvironment },
    ),
  };
}

test("现金订单从本地账本构造分币金额和十进制数量，AlreadySynced 视为成功", async () => {
  const { transport, adapter } = orderAdapter({ status: 200, data: { success: true, data: { orderGuid, accepted: true, alreadySynced: true } } });
  const result = await adapter.sync(orderGuid, JSON.stringify({ orderGuid }));
  assert.deepEqual(result, { kind: "synced", alreadySynced: true });
  assert.deepEqual(transport.calls[0]?.data, {
    orderGuid, storeCode: "1003", deviceCode: "IPAD_1", cashierId: "cashier-1", cashierName: "Alice", soldAt: "2026-07-28T00:00:00.000Z",
    totalAmount: 12.34, discountAmount: 0.34, actualAmount: 12, lines: [{ orderLineGuid: lineGuid, productCode: "P1", referenceCode: "MULTI-123", displayName: "商品", lookupCode: "931234", quantity: 1.25, unitPrice: 10, discountAmount: 0.34, actualAmount: 9.66, priceSource: 3, itemNumber: "I1", kind: 1, returnSourceKey: null, originalOrderGuid: null, originalOrderDetailGuid: null }],
    payments: [{ paymentGuid: tenderGuid, method: 1, amount: 12, reference: null, reservationToken: null, cardTransactions: null }],
  });
});

test("缺少冻结交易行来源的旧订单不得按当前目录或 ProductBase 猜测上传", async () => {
  const legacy = order();
  const withoutProvenance: LocalOrder = {
    ...legacy,
    lines: legacy.lines.map(({ syncProvenance: _ignored, ...line }) => line),
  };
  const { transport, adapter } = orderAdapter(
    {
      status: 200,
      data: {
        success: true,
        data: { orderGuid, accepted: true, alreadySynced: false },
      },
    },
    withoutProvenance,
  );

  assert.deepEqual(
    await adapter.sync(orderGuid, JSON.stringify({ orderGuid })),
    {
      kind: "rejected",
      failure: "business-rejection",
      code: "ORDER_SYNC_LINE_PROVENANCE_MISSING",
    },
  );
  assert.equal(transport.calls.length, 0);
});

test("outbox 指针、响应订单换绑和非现金缺少安全引用全部失败关闭", async () => {
  const success = { status: 200, data: { success: true, data: { orderGuid, accepted: true } } };
  const pointer = orderAdapter(success).adapter;
  assert.equal((await pointer.sync(orderGuid, JSON.stringify({ orderGuid: lineGuid }))).kind, "rejected");
  const rebound = orderAdapter({ status: 200, data: { success: true, data: { orderGuid: lineGuid, accepted: true } } }).adapter;
  assert.deepEqual(await rebound.sync(orderGuid, JSON.stringify({ orderGuid })), { kind: "rejected", failure: "business-rejection", code: "ORDER_SYNC_RESPONSE_INVALID" });
  const card = orderAdapter(success, order("card")).adapter;
  assert.deepEqual(await card.sync(orderGuid, JSON.stringify({ orderGuid })), { kind: "rejected", failure: "business-rejection", code: "CARD_PAYMENT_REFERENCE_REQUIRED" });
});

test("HTTP 仅明确设备撤销码锁机，普通 401 与权限/门禁/scope 403 保留待重试", async () => {
  const cases: readonly [Error, string][] = [
    [new HbposApiError("auth", { kind: "http", status: 401 }), "retry"],
    [new HbposApiError("disabled", { kind: "http", status: 401, code: "DEVICE_DISABLED" }), "blocked"],
    [new HbposApiError("forbidden", { kind: "http", status: 403, code: "DEVICE_DISABLED" }), "blocked"],
    [new HbposApiError("permission", { kind: "http", status: 403 }), "retry"],
    [new HbposApiError("scope", { kind: "http", status: 403, code: "DEVICE_SCOPE_FORBIDDEN" }), "retry"],
    [new HbposApiError("gate", { kind: "http", status: 403, code: "POS_IPAD_NEW_TRANSACTIONS_DISABLED" }), "retry"],
    [new HbposApiError("server", { kind: "http", status: 503 }), "retry"],
    [new HbposApiError("rejected", { kind: "envelope", code: "ORDER_REJECTED" }), "rejected"],
  ];
  for (const [error, kind] of cases) {
    const result = await orderAdapter(error).adapter.sync(orderGuid, JSON.stringify({ orderGuid }));
    assert.equal(result.kind, kind);
  }
});

test("已收款历史订单小数数量 400 保留待同步，其余 400 仍是拒绝", async () => {
  const unsupported = new HbposApiError("quantity", { kind: "http", status: 400, code: "ORDER_SYNC_QUANTITY_UNSUPPORTED" });
  assert.deepEqual(await orderAdapter(unsupported).adapter.sync(orderGuid, JSON.stringify({ orderGuid })), {
    kind: "retry", failure: "server", code: "ORDER_SYNC_QUANTITY_UNSUPPORTED",
  });
  const invalid = new HbposApiError("invalid", { kind: "http", status: 400, code: "ORDER_SYNC_INVALID" });
  assert.deepEqual(await orderAdapter(invalid).adapter.sync(orderGuid, JSON.stringify({ orderGuid })), {
    kind: "rejected", failure: "business-rejection", code: "ORDER_SYNC_INVALID",
  });
});

test("受信任材料只进入本次 HTTP 载荷，仓储中的脱敏订单保持不变", async () => {
  const local = order("card");
  const resolverInputs: LocalOrder[] = [];
  let resolverEnvironment: string | null = null;
  const { transport, adapter } = trustedOrderAdapter({
    async resolveForSync(input, environment) {
      resolverInputs.push(input);
      resolverEnvironment = environment;
      return {
        order: {
          ...input,
          tenders: input.tenders.map((tender) => ({
            ...tender,
            reference: "SQ:provider-payment-id",
          })),
        },
        cardSyncEvidenceByTenderGuid: new Map([
          [tenderGuid, cardSyncEvidence()],
        ]),
      };
    },
  }, local);

  assert.deepEqual(
    await adapter.sync(orderGuid, JSON.stringify({ orderGuid })),
    { kind: "synced", alreadySynced: false },
  );
  assert.equal(resolverEnvironment, "Sandbox");
  assert.notEqual(resolverInputs[0], local);
  assert.notEqual(resolverInputs[0]?.tenders, local.tenders);
  assert.equal(local.tenders[0]?.reference, null);
  const data = transport.calls[0]?.data as {
    payments: { reference: string | null }[];
  };
  assert.equal(data.payments[0]?.reference, "SQ:provider-payment-id");
  assert.equal(local.tenders[0]?.reference, null);
});

test("卡交易只从已绑定的受保护证据构造后端 DTO，缺失或换绑证据时不发送请求", async () => {
  const approved = trustedOrderAdapter({
    async resolveForSync(input) {
      return {
        order: {
          ...input,
          tenders: input.tenders.map((tender) => ({
            ...tender,
            reference: "SQ:provider-payment-id",
          })),
        },
        cardSyncEvidenceByTenderGuid: new Map([
          [tenderGuid, cardSyncEvidence()],
        ]),
      };
    },
  });

  assert.deepEqual(
    await approved.adapter.sync(orderGuid, JSON.stringify({ orderGuid })),
    { kind: "synced", alreadySynced: false },
  );
  const approvedData = approved.transport.calls[0]?.data as {
    payments: { cardTransactions: unknown }[];
  };
  assert.deepEqual(approvedData.payments[0]?.cardTransactions, [
    {
      processor: "Square",
      txnRef: "square-payment-1",
      authCode: "AUTH01",
      cardType: "VISA",
      cardBin: 411111,
      maskedCardNumber: "411111******1111",
      merchantId: "merchant-1",
      responseCode: "00",
      responseText: "APPROVED",
      stan: "123456",
      bankDateTime: "2026-07-28T00:00:00.000Z",
      amount: 12,
      receiptText: null,
      refundReference: null,
    },
  ]);

  for (const [name, evidence] of [
    ["missing", new Map<string, CardSyncEvidenceV1>()],
    [
      "amount-mismatch",
      new Map([[tenderGuid, cardSyncEvidence({ amountCents: 1_201 })]]),
    ],
    [
      "operation-mismatch",
      new Map([
        [tenderGuid, cardSyncEvidence({ operation: "refund" })],
      ]),
    ],
  ] as const) {
    const rejected = trustedOrderAdapter({
      async resolveForSync(input) {
        return {
          order: {
            ...input,
            tenders: input.tenders.map((tender) => ({
              ...tender,
              reference: "SQ:provider-payment-id",
            })),
          },
          cardSyncEvidenceByTenderGuid: evidence,
        };
      },
    });
    assert.deepEqual(
      await rejected.adapter.sync(orderGuid, JSON.stringify({ orderGuid })),
      {
        kind: "rejected",
        failure: "business-rejection",
        code:
          name === "missing"
            ? "CARD_SYNC_EVIDENCE_REQUIRED"
            : "CARD_SYNC_EVIDENCE_MISMATCH",
      },
    );
    assert.equal(rejected.transport.calls.length, 0);
  }
});

test("未配置 Linkly 环境仍原样交给受信任解析器，由实际 tender 决定是否拒绝", async () => {
  let resolverEnvironment: string | null | undefined;
  const { adapter } = trustedOrderAdapter(
    {
      async resolveForSync(input, environment) {
        resolverEnvironment = environment;
        return {
          order: {
            ...input,
            tenders: input.tenders.map((tender) => ({
              ...tender,
              reference: "SQ:provider-payment-id",
            })),
          },
          cardSyncEvidenceByTenderGuid: new Map([
            [tenderGuid, cardSyncEvidence()],
          ]),
        };
      },
    },
    order("card"),
    null,
  );

  assert.deepEqual(
    await adapter.sync(orderGuid, JSON.stringify({ orderGuid })),
    { kind: "synced", alreadySynced: false },
  );
  assert.equal(resolverEnvironment, null);
});

test("稳定材料错配返回明确业务拒绝且不发送请求", async () => {
  const { transport, adapter } = trustedOrderAdapter({
    async resolveForSync() {
      throw new OrderSyncMaterialError("ORDER_SYNC_TENDER_MISMATCH");
    },
  });

  assert.deepEqual(
    await adapter.sync(orderGuid, JSON.stringify({ orderGuid })),
    {
      kind: "rejected",
      failure: "business-rejection",
      code: "ORDER_SYNC_TENDER_MISMATCH",
    },
  );
  assert.equal(transport.calls.length, 0);
});

test("意外数据库或 IO 错误向 outbox 抛出且不发送请求", async () => {
  const ioError = new Error("temporary database read failure");
  const { transport, adapter } = trustedOrderAdapter({
    async resolveForSync() {
      throw ioError;
    },
  });

  await assert.rejects(
    adapter.sync(orderGuid, JSON.stringify({ orderGuid })),
    (error: unknown) => error === ioError,
  );
  assert.equal(transport.calls.length, 0);
});

test("券退款允许已签发券码且不得携带购买 reservation token", async () => {
  const local: LocalOrder = {
    ...order("voucher"),
    total: { cents: -1200, currency: "AUD" },
    discount: { cents: 0, currency: "AUD" },
    actualAmount: { cents: -1200, currency: "AUD" },
    tenders: [{
      tenderGuid,
      method: "voucher",
      amount: { cents: -1200, currency: "AUD" },
      reference: null,
      reservationToken: null,
    }],
    originalOrderGuid: "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d09",
  };
  const { transport, adapter } = trustedOrderAdapter({
    async resolveForSync(input) {
      return {
        order: {
          ...input,
          tenders: input.tenders.map((tender) => ({
            ...tender,
            reference: "issued-voucher-code",
            reservationToken: null,
          })),
        },
        cardSyncEvidenceByTenderGuid: new Map(),
      };
    },
  }, local);

  assert.equal(
    (await adapter.sync(orderGuid, JSON.stringify({ orderGuid }))).kind,
    "synced",
  );
  const data = transport.calls[0]?.data as {
    payments: { reference: string; reservationToken: string | null }[];
  };
  assert.deepEqual(data.payments[0], {
    paymentGuid: tenderGuid,
    method: 3,
    amount: -12,
    reference: "issued-voucher-code",
    reservationToken: null,
    cardTransactions: null,
  });
});

test("审计从原订单加载身份；裸 DTO duplicate 成功、rejected 失败，并从载荷剔除 token/PAN", async () => {
  // OperationAuditsController 直接返回 OperationAuditBatchResultDto，不包 HbposEnvelope。
  const transport = new FakeTransport({ status: 200, data: { results: [{ eventId: eventGuid, status: "duplicate" }] } });
  const adapter = new HbposAuditBatchAdapter(transport, new FakeOrders(order()), { storeCode: "bad", deviceCode: "bad", appVersion: "1.0.0", instanceId: "ipad-install" });
  const event = { eventId: eventGuid, eventType: "SALE_COMPLETE", occurredAtIso: "2026-07-28T00:00:00.000Z", orderGuid, correlationId: orderGuid, payload: { source: "cash", authorizationToken: "secret", pan: "4111111111111111" } } as const;
  assert.deepEqual(await adapter.upload(persistedAudits([event])), { kind: "uploaded" });
  const body = transport.calls[0]?.data as { events: { storeCode: string; deviceCode: string; properties: Record<string, string> | null }[] };
  assert.deepEqual(body.events[0]?.properties, { source: "cash" });
  assert.equal(body.events[0]?.storeCode, "1003");
  assert.equal(body.events[0]?.deviceCode, "IPAD_1");
  assert.doesNotMatch(JSON.stringify(body), /secret|4111111111111111/);

  const rejected = new HbposAuditBatchAdapter(new FakeTransport({ status: 200, data: { results: [{ eventId: eventGuid, status: "rejected", errorCode: "INVALID_EVENT" }] } }), new FakeOrders(order()), { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" });
  assert.deepEqual(await rejected.upload(persistedAudits([event])), {
    kind: "acknowledged",
    uploadedEventIds: [],
    rejected: [{ eventId: eventGuid, code: "INVALID_EVENT" }],
  });
});

test("订单审计完整 actor 快照优先；残缺旧载荷只能整套回退订单身份", async () => {
  const secondEventGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d10";
  const thirdEventGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d11";
  const transport = new FakeTransport({
    status: 200,
    data: {
      results: [
        { eventId: eventGuid, status: "accepted" },
        { eventId: secondEventGuid, status: "accepted" },
        { eventId: thirdEventGuid, status: "accepted" },
      ],
    },
  });
  const adapter = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(order()),
    { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" },
  );

  assert.deepEqual(await adapter.upload(persistedAudits([
    {
      eventId: eventGuid,
      eventType: "SALE_COMPLETE",
      occurredAtIso: "2026-07-28T00:00:00.000Z",
      orderGuid,
      correlationId: eventGuid,
      payload: {
        requestingCashierId: "ACTOR-1",
        requestingCashierName: "Actor One",
        requestingUserGuid: "actor-user-guid",
      },
    },
    {
      eventId: secondEventGuid,
      eventType: "SALE_COMPLETE",
      occurredAtIso: "2026-07-28T00:00:00.000Z",
      orderGuid,
      correlationId: secondEventGuid,
      payload: {
        requestingCashierId: "PARTIAL-ACTOR",
      },
    },
    {
      eventId: thirdEventGuid,
      eventType: "SALE_COMPLETE",
      occurredAtIso: "2026-07-28T00:00:00.000Z",
      orderGuid,
      correlationId: thirdEventGuid,
      payload: {
        requestingCashierId: "ACTOR-NULLS",
        requestingCashierName: null,
        requestingUserGuid: null,
      },
    },
  ])), { kind: "uploaded" });

  const events = (transport.calls[0]?.data as {
    events: Readonly<{
      cashierId: string | null;
      cashierName: string | null;
      userGuid: string | null;
    }>[];
  }).events;
  assert.deepEqual(
    events.map(({ cashierId, cashierName, userGuid }) => ({
      cashierId,
      cashierName,
      userGuid,
    })),
    [
      {
        cashierId: "ACTOR-1",
        cashierName: "Actor One",
        userGuid: "actor-user-guid",
      },
      {
        cashierId: "cashier-1",
        cashierName: "Alice",
        userGuid: null,
      },
      {
        cashierId: "ACTOR-NULLS",
        cashierName: null,
        userGuid: null,
      },
    ],
  );
});

test("非订单登录审计只使用发生时冻结的 requester userGuid", async () => {
  const transport = new FakeTransport({
    status: 200,
    data: { results: [{ eventId: eventGuid, status: "accepted" }] },
  });
  const adapter = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(null),
    { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" },
  );
  assert.deepEqual(await adapter.upload(persistedAudits([{
    eventId: eventGuid,
    eventType: "CASHIER_LOGIN",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    orderGuid: null,
    correlationId: eventGuid,
    payload: {
      requestingCashierId: "cashier-1",
      requestingCashierName: "Alice",
      requestingUserGuid: "user-guid-1",
    },
  }])), { kind: "uploaded" });
  const body = transport.calls[0]?.data as {
    events: {
      cashierId: string | null;
      userGuid: string | null;
      items: unknown;
    }[];
  };
  assert.equal(body.events[0]?.cashierId, "cashier-1");
  assert.equal(body.events[0]?.userGuid, "user-guid-1");
  // 服务端 Items 为非可空 List，null 会触发整批 400 并被本地永久标为 rejected。
  assert.deepEqual(body.events[0]?.items, []);
});

test("设备重新注册后，订单和非订单审计仍使用入库时冻结的门店与设备范围", async () => {
  const secondEventGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d23";
  const transport = new FakeTransport({
    status: 200,
    data: {
      results: [
        { eventId: eventGuid, status: "accepted" },
        { eventId: secondEventGuid, status: "accepted" },
      ],
    },
  });
  const adapter = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(order()),
    // 模拟设备重注册后的当前身份；它绝不能覆盖旧事实。
    { storeCode: "STORE-NEW", deviceCode: "IPAD-NEW", appVersion: "1", instanceId: "i" },
  );
  const oldScope = { storeCode: "STORE-OLD", deviceCode: "IPAD-OLD" } as const;

  assert.deepEqual(await adapter.upload(persistedAudits([
    {
      eventId: eventGuid,
      eventType: "CASHIER_LOGIN",
      occurredAtIso: "2026-07-28T00:00:00.000Z",
      orderGuid: null,
      correlationId: eventGuid,
      auditScope: oldScope,
      payload: {},
    },
    {
      eventId: secondEventGuid,
      eventType: "SALE_COMPLETE",
      occurredAtIso: "2026-07-28T00:00:00.000Z",
      orderGuid,
      correlationId: secondEventGuid,
      auditScope: oldScope,
      payload: {},
    },
  ])), { kind: "uploaded" });

  const events = (transport.calls[0]?.data as {
    events: Readonly<{ storeCode: string; deviceCode: string }>[];
  }).events;
  assert.deepEqual(
    events.map(({ storeCode, deviceCode }) => ({ storeCode, deviceCode })),
    [oldScope, oldScope],
  );
});

test("审计逐项回执会确认 accepted/duplicate 并隔离 rejected，不以单条拒绝堵住队头", async () => {
  const rejectedEventId = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d06";
  const transport = new FakeTransport({
    status: 200,
    data: {
      results: [
        { eventId: eventGuid, status: "accepted" },
        { eventId: rejectedEventId, status: "rejected", errorCode: "INVALID_EVENT" },
      ],
    },
  });
  const adapter = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(null),
    { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" },
  );
  const base = {
    eventType: "CART_CLEAR",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    orderGuid: null,
    payload: {},
  } as const;

  assert.deepEqual(await adapter.upload(persistedAudits([
    { ...base, eventId: eventGuid, correlationId: eventGuid },
    { ...base, eventId: rejectedEventId, correlationId: rejectedEventId },
  ])), {
    kind: "acknowledged",
    uploadedEventIds: [eventGuid],
    rejected: [{ eventId: rejectedEventId, code: "INVALID_EVENT" }],
  });
});

test("员工审计混合回执先确认已知终态，仅重试缺失回执事件", async () => {
  const missingEventId = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d19";
  const transport = new FakeTransport({
    status: 200,
    data: { results: [{ eventId: eventGuid, status: "duplicate" }] },
  });
  const adapter = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(null),
    { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" },
  );
  const base = {
    eventType: "CART_CLEAR",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    orderGuid: null,
    payload: {},
  } as const;
  assert.deepEqual(await adapter.upload(persistedAudits([
    { ...base, eventId: eventGuid, correlationId: eventGuid },
    { ...base, eventId: missingEventId, correlationId: missingEventId },
  ])), {
    kind: "acknowledged",
    uploadedEventIds: [eventGuid],
    rejected: [],
    retryEventIds: [missingEventId],
  });
});

test("员工审计用 canonical UUID 匹配回执，写回仍保留本地原始 event_id", async () => {
  const acceptedEventId =
    "018F1B9B-47C5-7C1B-9F8E-39C5CB3B9D31";
  const missingEventId =
    "018F1B9B-47C5-7C1B-9F8E-39C5CB3B9D32";
  const duplicateEventId =
    "018F1B9B-47C5-7C1B-9F8E-39C5CB3B9D33";
  const rejectedEventId =
    "018F1B9B-47C5-7C1B-9F8E-39C5CB3B9D34";
  const unknownEventId =
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d35";
  const transport = new FakeTransport({
    status: 200,
    data: {
      results: [
        { eventId: acceptedEventId.toLowerCase(), status: "accepted" },
        { eventId: duplicateEventId.toLowerCase(), status: "duplicate" },
        {
          eventId: rejectedEventId.toLowerCase(),
          status: "rejected",
          errorCode: "INVALID_EVENT",
        },
        { eventId: unknownEventId, status: "accepted" },
      ],
    },
  });
  const adapter = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(null),
    {
      storeCode: "1003",
      deviceCode: "IPAD_1",
      appVersion: "1",
      instanceId: "i",
    },
  );
  const base = {
    eventType: "CART_CLEAR",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    orderGuid: null,
    payload: {},
  } as const;
  const localEventIds = [
    acceptedEventId,
    missingEventId,
    duplicateEventId,
    rejectedEventId,
  ];

  assert.deepEqual(
    await adapter.upload(
      persistedAudits(
        localEventIds.map((eventId) => ({
          ...base,
          eventId,
          correlationId: eventId,
        })),
      ),
    ),
    {
      kind: "acknowledged",
      uploadedEventIds: [acceptedEventId, duplicateEventId],
      rejected: [{ eventId: rejectedEventId, code: "INVALID_EVENT" }],
      retryEventIds: [missingEventId],
    },
  );
  const request = transport.calls[0]?.data as {
    events: Readonly<{ eventId: string }>[];
  };
  assert.deepEqual(
    request.events.map((event) => event.eventId),
    localEventIds.map((eventId) => eventId.toLowerCase()),
  );
});

test("历史本地审计事件确定性映射到后端枚举，诊断事件隔离且单批最多 8 条", async () => {
  const ids = [
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d11",
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d12",
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d13",
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d14",
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d15",
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d16",
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d17",
    "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d18",
  ] as const;
  const transport = new FakeTransport({
    status: 200,
    data: { results: ids.slice(0, 6).map((eventId) => ({ eventId, status: "accepted" })) },
  });
  const returned = order();
  const returnedOrder: LocalOrder = {
    ...returned,
    total: { cents: -1_200, currency: "AUD" },
    actualAmount: { cents: -1_200, currency: "AUD" },
    lines: returned.lines.map((line) => ({
      ...line,
      kind: "return" as const,
      actualAmount: { cents: -1_200, currency: "AUD" },
    })),
  };
  const orders = new FakeOrders(returnedOrder);
  const adapter = new HbposAuditBatchAdapter(transport, orders, {
    storeCode: "1003",
    deviceCode: "IPAD_1",
    appVersion: "1",
    instanceId: "i",
  });
  const base = {
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    payload: {},
  } as const;
  const events = [
    { ...base, eventId: ids[0], eventType: "PAYMENT_MIXED_CASH_COMPLETE", orderGuid, correlationId: ids[0] },
    { ...base, eventId: ids[1], eventType: "PAYMENT_APPROVED_COMPLETE", orderGuid, correlationId: ids[1] },
    { ...base, eventId: ids[2], eventType: "RETURN_ORDER_COMPLETED", orderGuid, correlationId: ids[2] },
    { ...base, eventId: ids[3], eventType: "MIXED_CASH_TENDER_APPENDED", orderGuid: null, correlationId: ids[3] },
    { ...base, eventId: ids[4], eventType: "MIXED_CASH_TENDER_REVERSED", orderGuid: null, correlationId: ids[4] },
    { ...base, eventId: ids[5], eventType: "PAYMENT_DRAFT_CANCELLED_CLOSED", orderGuid: null, correlationId: ids[5] },
    { ...base, eventId: ids[6], eventType: "PAYMENT_DRAFT_ABANDONED", orderGuid: null, correlationId: ids[6] },
    { ...base, eventId: ids[7], eventType: "DAILY_CLOSE_MIGRATED", orderGuid: null, correlationId: ids[7] },
  ];

  assert.deepEqual(await adapter.upload(persistedAudits(events)), {
    kind: "acknowledged",
    uploadedEventIds: ids.slice(0, 6),
    rejected: [
      { eventId: ids[6], code: "AUDIT_LOCAL_DIAGNOSTIC" },
      { eventId: ids[7], code: "AUDIT_LOCAL_DIAGNOSTIC" },
    ],
  });
  const body = transport.calls[0]?.data as { events: { operationType: string }[] };
  assert.deepEqual(body.events.map((event) => event.operationType), [
    "RETURN_REFUND_COMPLETE",
    "RETURN_REFUND_COMPLETE",
    "RETURN_REFUND_COMPLETE",
    "PAYMENT_TENDER_ADD",
    "PAYMENT_TENDER_REMOVE",
    "PAYMENT_CANCEL",
  ]);

  assert.deepEqual(await adapter.upload(persistedAudits(Array.from({ length: 9 }, (_, index) => ({
    ...base,
    eventId: `018f1b9b-47c5-7c1b-9f8e-39c5cb3b9e${index}`,
    eventType: "CART_CLEAR",
    orderGuid: null,
    correlationId: `018f1b9b-47c5-7c1b-9f8e-39c5cb3b9e${index}`,
  })))), {
    kind: "rejected",
    code: "AUDIT_BATCH_SIZE_INVALID",
  });
});

test("正数混合销售退货的旧 PAYMENT_APPROVED_COMPLETE 仍映射为销售完成", async () => {
  const mixedPositive = order();
  const returnLine = mixedPositive.lines[0]!;
  const mixedOrder: LocalOrder = {
    ...mixedPositive,
    lines: [
      ...mixedPositive.lines,
      {
        ...returnLine,
        lineId: "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d19",
        kind: "return",
        actualAmount: { cents: -100, currency: "AUD" },
        originalOrderGuid: orderGuid,
        originalOrderDetailGuid: lineGuid,
      },
    ],
  };
  const transport = new FakeTransport({
    status: 200,
    data: { results: [{ eventId: eventGuid, status: "accepted" }] },
  });
  const adapter = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(mixedOrder),
    { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" },
  );

  assert.deepEqual(await adapter.upload(persistedAudits([{
    eventId: eventGuid,
    eventType: "PAYMENT_APPROVED_COMPLETE",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    orderGuid,
    correlationId: eventGuid,
    payload: {},
  }])), { kind: "uploaded" });
  const body = transport.calls[0]?.data as { events: { operationType: string }[] };
  assert.equal(body.events[0]?.operationType, "SALE_COMPLETE");
});

test("员工审计 HTTP 429 保留为可重试；超过 4 MiB 的单条载荷被隔离而不发送", async () => {
  const event = {
    eventId: eventGuid,
    eventType: "CART_CLEAR",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    orderGuid: null,
    correlationId: eventGuid,
    payload: {},
  } as const;
  const rateLimited = new HbposAuditBatchAdapter(
    new FakeTransport(new HbposApiError("rate limited", { kind: "http", status: 429 })),
    new FakeOrders(null),
    { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" },
  );
  assert.deepEqual(await rateLimited.upload(persistedAudits([event])), {
    kind: "retry",
    failure: "server",
  });

  const transport = new FakeTransport({ status: 200, data: { results: [] } });
  const oversized = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(null),
    { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" },
  );
  const hugeItem = {
    productCode: "P1",
    itemNumber: "I1",
    referenceCode: "R1",
    lookupCode: "L1",
    displayName: "x".repeat(4 * 1024 * 1024),
    lineKind: "sale",
    beforeQuantity: 1,
    afterQuantity: 1,
    quantityDelta: 0,
    beforeUnitPriceCents: 100,
    afterUnitPriceCents: 100,
    unitPriceDeltaCents: 0,
    beforeDiscountCents: 0,
    afterDiscountCents: 0,
    discountDeltaCents: 0,
    beforeGrossCents: 100,
    afterGrossCents: 100,
    grossDeltaCents: 0,
    beforeActualCents: 100,
    afterActualCents: 100,
    actualDeltaCents: 0,
  };
  assert.deepEqual(await oversized.upload(persistedAudits([{ ...event, payload: { items: [hugeItem] } }])), {
    kind: "acknowledged",
    uploadedEventIds: [],
    rejected: [{ eventId: eventGuid, code: "AUDIT_REQUEST_TOO_LARGE" }],
  });
  assert.equal(transport.calls.length, 0);

  const laterEventId = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d07";
  const prefixTransport = new FakeTransport({
    status: 200,
    data: { results: [{ eventId: eventGuid, status: "accepted" }] },
  });
  const prefixAdapter = new HbposAuditBatchAdapter(
    prefixTransport,
    new FakeOrders(null),
    { storeCode: "1003", deviceCode: "IPAD_1", appVersion: "1", instanceId: "i" },
  );
  assert.deepEqual(await prefixAdapter.upload(persistedAudits([
    event,
    {
      ...event,
      eventId: laterEventId,
      correlationId: laterEventId,
      payload: { items: [hugeItem] },
    },
  ])), {
    kind: "acknowledged",
    uploadedEventIds: [eventGuid],
    rejected: [],
  });
  assert.equal((prefixTransport.calls[0]?.data as { events: unknown[] }).events.length, 1);
});

test("购物车审计将快照明细、分币金额和授权上下文映射为后端合同", async () => {
  const transport = new FakeTransport({
    status: 200,
    data: { results: [{ eventId: eventGuid, status: "accepted" }] },
  });
  const adapter = new HbposAuditBatchAdapter(transport, new FakeOrders(null), {
    storeCode: "1003",
    deviceCode: "IPAD_1",
    appVersion: "1.0.0",
    instanceId: "ipad-install",
  });
  const event = {
    eventId: eventGuid,
    eventType: "CART_ITEM_PRICE_CHANGE",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    orderGuid: null,
    correlationId: orderGuid,
    payload: {
      outcome: "Succeeded",
      action: "cart-item-price-change",
      screen: "pos-terminal",
      permissionCode: "POS.CART.PRICE_CHANGE",
      authorizationMode: "supervisor",
      requestingCashierId: "cashier-1",
      requestingCashierName: "Alice",
      requestingUserGuid: "cashier-user-guid",
      authorizingCashierId: "manager-2",
      reason: "supervisor-approved",
      itemCount: 1,
      beforeSubtotalCents: 1234,
      afterSubtotalCents: 1334,
      beforeDiscountCents: 34,
      afterDiscountCents: 54,
      beforeActualCents: 1200,
      afterActualCents: 1280,
      amountDeltaCents: 80,
      items: [{
        productCode: "P1",
        itemNumber: "I1",
        referenceCode: "MULTI-123",
        lookupCode: "931234",
        displayName: "商品",
        lineKind: "sale",
        beforeQuantity: 1,
        afterQuantity: 1,
        quantityDelta: 0,
        beforeUnitPriceCents: 1000,
        afterUnitPriceCents: 1100,
        unitPriceDeltaCents: 100,
        beforeDiscountCents: 34,
        afterDiscountCents: 54,
        discountDeltaCents: 20,
        beforeGrossCents: 1000,
        afterGrossCents: 1100,
        grossDeltaCents: 100,
        beforeActualCents: 966,
        afterActualCents: 1046,
        actualDeltaCents: 80,
      }],
      authorizationToken: "secret",
    },
  } as const;

  assert.deepEqual(await adapter.upload(persistedAudits([event])), { kind: "uploaded" });
  const body = transport.calls[0]?.data as { events: Record<string, unknown>[] };
  assert.deepEqual(body.events[0], {
    eventId: eventGuid,
    schemaVersion: 1,
    occurredAtUtc: "2026-07-28T00:00:00.000Z",
    operationType: "CART_ITEM_PRICE_CHANGE",
    outcome: "Succeeded",
    cashierId: "cashier-1",
    userGuid: "cashier-user-guid",
    cashierName: "Alice",
    isOfflineCached: false,
    isEmergencyOverride: false,
    storeCode: "1003",
    deviceCode: "IPAD_1",
    appVersion: "1.0.0",
    instanceId: "ipad-install",
    orderGuid: null,
    correlationId: orderGuid,
    currencyCode: "AUD",
    beforeGross: 12.34,
    afterGross: 13.34,
    beforeDiscount: 0.34,
    afterDiscount: 0.54,
    beforeActual: 12,
    afterActual: 12.8,
    amountDelta: 0.8,
    properties: {
      action: "cart-item-price-change",
      screen: "pos-terminal",
      reason: "supervisor-approved",
      itemCount: "1",
      requestingCashierId: "cashier-1",
      requestingCashierName: "Alice",
      requestingUserGuid: "cashier-user-guid",
      authorizingCashierId: "manager-2",
      permissionCode: "POS.CART.PRICE_CHANGE",
      authorizationMode: "supervisor",
    },
    items: [{
      productCode: "P1",
      itemNumber: "I1",
      referenceCode: "MULTI-123",
      lookupCode: "931234",
      displayName: "商品",
      lineKind: "sale",
      beforeQuantity: 1,
      afterQuantity: 1,
      quantityDelta: 0,
      beforeUnitPrice: 10,
      afterUnitPrice: 11,
      unitPriceDelta: 1,
      beforeDiscountAmount: 0.34,
      afterDiscountAmount: 0.54,
      discountAmountDelta: 0.2,
      beforeGrossAmount: 10,
      afterGrossAmount: 11,
      grossAmountDelta: 1,
      beforeActualAmount: 9.66,
      afterActualAmount: 10.46,
      actualAmountDelta: 0.8,
    }],
  });
  assert.doesNotMatch(JSON.stringify(body), /secret/);

  assert.deepEqual(
    await adapter.upload(persistedAudits([{
      ...event,
      payload: { ...event.payload, afterActualCents: Number.MAX_SAFE_INTEGER + 1 },
    }])),
    {
      kind: "acknowledged",
      uploadedEventIds: [],
      rejected: [{ eventId: eventGuid, code: "AUDIT_AMOUNT_INVALID" }],
    },
  );
  assert.equal(transport.calls.length, 1);
});

test("硬件失败审计保留 Failed outcome，非法 outcome 在请求前失败关闭", async () => {
  const transport = new FakeTransport({ status: 200, data: { results: [{ eventId: eventGuid, status: "accepted" }] } });
  const adapter = new HbposAuditBatchAdapter(transport, new FakeOrders(order()), {
    storeCode: "1003",
    deviceCode: "IPAD_1",
    appVersion: "1.0.0",
    instanceId: "ipad-install",
  });
  const event = {
    eventId: eventGuid,
    eventType: "CASH_DRAWER_OPEN",
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    orderGuid,
    correlationId: orderGuid,
    payload: { outcome: "Failed", reason: "cash-sale", status: "Failed" },
  } as const;

  assert.deepEqual(await adapter.upload(persistedAudits([event])), { kind: "uploaded" });
  const body = transport.calls[0]?.data as {
    events: { outcome: string; properties: Record<string, string> | null }[];
  };
  assert.equal(body.events[0]?.outcome, "Failed");
  assert.deepEqual(body.events[0]?.properties, {
    status: "Failed",
    reason: "cash-sale",
  });

  assert.deepEqual(
    await adapter.upload(persistedAudits([{ ...event, payload: { outcome: "maybe" } }])),
    {
      kind: "acknowledged",
      uploadedEventIds: [],
      rejected: [{ eventId: eventGuid, code: "AUDIT_OUTCOME_INVALID" }],
    },
  );
  assert.equal(transport.calls.length, 1);
});

test("M16 礼券撤销的本地终态事实映射为后端审计枚举，避免堵塞上传队列", async () => {
  const blockedEventGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d05";
  const transport = new FakeTransport({
    status: 200,
    data: {
      results: [
        { eventId: eventGuid, status: "accepted" },
        { eventId: blockedEventGuid, status: "accepted" },
      ],
    },
  });
  const adapter = new HbposAuditBatchAdapter(
    transport,
    new FakeOrders(order("voucher")),
    {
      storeCode: "1003",
      deviceCode: "IPAD_1",
      appVersion: "1.0.0",
      instanceId: "ipad-install",
    },
  );
  const terminalFacts = [
    {
      eventId: eventGuid,
      eventType: "PAYMENT_TENDER_REMOVE",
      occurredAtIso: "2026-07-28T00:00:00.000Z",
      orderGuid,
      correlationId: eventGuid,
      payload: {
        action: "payment-tender-remove",
        outcome: "success",
        reason: "SALE",
      },
    },
    {
      eventId: blockedEventGuid,
      eventType: "PAYMENT_TENDER_REMOVE",
      occurredAtIso: "2026-07-28T00:00:01.000Z",
      orderGuid,
      correlationId: blockedEventGuid,
      payload: {
        action: "payment-tender-remove",
        outcome: "blocked",
        reason: "SALE",
      },
    },
  ] as const;

  assert.deepEqual(await adapter.upload(persistedAudits(terminalFacts)), { kind: "uploaded" });
  const body = transport.calls[0]?.data as {
    events: { outcome: string; properties: Record<string, string> | null }[];
  };
  assert.deepEqual(
    body.events.map((event) => event.outcome),
    ["Succeeded", "Denied"],
  );
  assert.deepEqual(body.events[0]?.properties, {
    action: "payment-tender-remove",
    reason: "SALE",
  });
});
