import assert from "node:assert/strict";
import test from "node:test";

import { HbposInstallmentsApi } from "./hbpos-installments-api";
import type { InstallmentRepaymentCapabilities } from "./installment-models";

import type {
  HbposTransport,
  HbposTransportRequest,
  HbposTransportResponse,
} from "@hb/pos-api-client/transport";

class QueueTransport implements HbposTransport {
  public readonly requests: HbposTransportRequest[] = [];

  public constructor(private readonly responses: unknown[]) {}

  public async request<T>(
    request: HbposTransportRequest,
  ): Promise<HbposTransportResponse<T>> {
    this.requests.push(request);
    if (this.responses.length === 0) throw new Error("Missing fake response.");
    return { status: 200, data: this.responses.shift() as T };
  }
}

const installmentGuid = "10000000-0000-4000-8000-000000000001";
const paymentGuid = "20000000-0000-4000-8000-000000000001";
const lineGuid = "30000000-0000-4000-8000-000000000001";
const operationGuid = "40000000-0000-4000-8000-000000000001";
const originalPaymentGuid = "50000000-0000-4000-8000-000000000001";

test("repayment claim 使用稳定 v1 路由、整数分映射和同一 operationGuid", async () => {
  const claimPayload = {
    installmentGuid,
    operationGuid,
    paymentGuid,
    amount: 80,
    method: 2,
    idempotencyKey: operationGuid,
    status: 2,
    provider: "square",
    providerAttemptId: "attempt-1",
    createdAtUtc: "2026-08-04T01:00:00Z",
    updatedAtUtc: "2026-08-04T01:01:00Z",
    expiresAtUtc: null,
    commit: null,
    alreadyExists: false,
  };
  const committedPayload = {
    ...claimPayload,
    status: 3,
    commit: {
      details: detailsPayload({ status: 2, balanceAmount: 0 }),
      alreadyRecorded: false,
    },
  };
  const transport = new QueueTransport([
    {
      success: true,
      data: {
        repaymentClaimsSupported: true,
        repaymentClaimsRequired: true,
        cardRepaymentSupported: false,
        repaymentClaimPrepareProviderV1: true,
        crossDeviceRepaymentEnabled: true,
        crossDeviceCancelRefundEnabled: true,
        crossDeviceVoidEnabled: false,
        crossDevicePickupEnabled: true,
        preparedClaimTtlSeconds: 300,
        cancelClaimsSupported: true,
        cancelClaimsRequired: false,
        cancelPreparedClaimTtlSeconds: 120,
      },
    },
    { success: true, data: { ...claimPayload, status: 1, provider: null, providerAttemptId: null } },
    { success: true, data: claimPayload },
    { success: true, data: claimPayload },
    { success: true, data: { ...claimPayload, status: 6 } },
    { success: true, data: committedPayload },
  ]);
  const api = new HbposInstallmentsApi(transport, "S1");

  assert.deepEqual(await api.getCapabilities(), {
    repaymentClaimsSupported: true,
    repaymentClaimsRequired: true,
    cardRepaymentSupported: false,
    repaymentClaimPrepareProviderV1: true,
    crossDeviceRepaymentEnabled: true,
    crossDeviceCancelRefundEnabled: true,
    crossDeviceVoidEnabled: false,
    crossDevicePickupEnabled: true,
    preparedClaimTtlSeconds: 300,
    cancelClaimsSupported: true,
    cancelClaimsRequired: false,
    cancelPreparedClaimTtlSeconds: 120,
  });
  await api.createRepaymentClaim({
    installmentGuid,
    operationGuid,
    paymentGuid,
    amountCents: 8_000,
    method: "card",
    idempotencyKey: operationGuid,
  });
  await api.beginRepaymentClaimProvider({
    installmentGuid,
    operationGuid,
    provider: "square",
    providerAttemptId: "attempt-1",
  });
  await api.getRepaymentClaim({ installmentGuid, operationGuid });
  await api.resolveRepaymentClaim({
    installmentGuid,
    operationGuid,
    outcome: "Unknown",
  });
  const committed = await api.commitRepaymentClaim({
    installmentGuid,
    operationGuid,
    reference: "TXN-1",
    reservationToken: null,
    cardTransactions: [],
  });

  assert.deepEqual(
    transport.requests.map(({ method, url, data }) => ({ method, url, data })),
    [
      { method: "GET", url: "/api/v1/installments/capabilities", data: undefined },
      {
        method: "POST",
        url: `/api/v1/installments/${installmentGuid}/repayment-claims`,
        data: { operationGuid, paymentGuid, amount: 80, method: 2, idempotencyKey: operationGuid },
      },
      {
        method: "POST",
        url: `/api/v1/installments/${installmentGuid}/repayment-claims/${operationGuid}/begin-provider`,
        data: { provider: "square", providerAttemptId: "attempt-1" },
      },
      {
        method: "GET",
        url: `/api/v1/installments/${installmentGuid}/repayment-claims/${operationGuid}`,
        data: undefined,
      },
      {
        method: "POST",
        url: `/api/v1/installments/${installmentGuid}/repayment-claims/${operationGuid}/resolve`,
        data: { outcome: 3 },
      },
      {
        method: "POST",
        url: `/api/v1/installments/${installmentGuid}/repayment-claims/${operationGuid}/commit`,
        data: { reference: "TXN-1", reservationToken: null, cardTransactions: [] },
      },
    ],
  );
  assert.equal(committed.status, "Committed");
  assert.equal(committed.commit?.details.status, "PaidOff");
});

test("显式确认尚未收现时 resolve 携带原 provider attempt，普通 resolve 保持兼容", async () => {
  const releasedPayload = {
    installmentGuid,
    operationGuid,
    paymentGuid,
    amount: 80,
    method: 1,
    idempotencyKey: operationGuid,
    status: 4,
    provider: "cash",
    providerAttemptId: "cash-attempt-1",
    createdAtUtc: "2026-08-04T01:00:00Z",
    updatedAtUtc: "2026-08-04T01:01:00Z",
    expiresAtUtc: null,
    commit: null,
    alreadyExists: false,
  };
  const transport = new QueueTransport([
    { success: true, data: releasedPayload },
    { success: true, data: { ...releasedPayload, status: 6 } },
  ]);
  const api = new HbposInstallmentsApi(transport, "S1");

  await api.resolveRepaymentClaim({
    installmentGuid,
    operationGuid,
    outcome: "Released",
    cashNotCollectedConfirmed: true,
    providerAttemptId: "cash-attempt-1",
  });
  await api.resolveRepaymentClaim({
    installmentGuid,
    operationGuid,
    outcome: "Unknown",
  });

  assert.deepEqual(transport.requests, [
    {
      method: "POST",
      url: `/api/v1/installments/${installmentGuid}/repayment-claims/${operationGuid}/resolve`,
      data: {
        outcome: 1,
        cashNotCollectedConfirmed: true,
        providerAttemptId: "cash-attempt-1",
      },
    },
    {
      method: "POST",
      url: `/api/v1/installments/${installmentGuid}/repayment-claims/${operationGuid}/resolve`,
      data: { outcome: 3 },
    },
  ]);
});

test("repayment claim 字段长度在客户端精确对齐服务端 100/32/128", async () => {
  const createPayload = {
    installmentGuid,
    operationGuid,
    paymentGuid,
    amount: 10,
    method: 1,
    idempotencyKey: "i".repeat(100),
    status: 1,
    provider: null,
    providerAttemptId: null,
    createdAtUtc: "2026-08-04T01:00:00Z",
    updatedAtUtc: "2026-08-04T01:00:00Z",
    expiresAtUtc: null,
    commit: null,
    alreadyExists: false,
  };
  const begunPayload = {
    ...createPayload,
    status: 2,
    provider: "p".repeat(32),
    providerAttemptId: "a".repeat(128),
  };
  const accepted = new HbposInstallmentsApi(
    new QueueTransport([
      { success: true, data: createPayload },
      { success: true, data: begunPayload },
    ]),
    "S1",
  );
  await accepted.createRepaymentClaim({
    installmentGuid,
    operationGuid,
    paymentGuid,
    amountCents: 1_000,
    method: "cash",
    idempotencyKey: "i".repeat(100),
  });
  await accepted.beginRepaymentClaimProvider({
    installmentGuid,
    operationGuid,
    provider: "p".repeat(32),
    providerAttemptId: "a".repeat(128),
  });

  const rejected = new HbposInstallmentsApi(new QueueTransport([]), "S1");
  await assert.rejects(
    rejected.createRepaymentClaim({
      installmentGuid,
      operationGuid,
      paymentGuid,
      amountCents: 1_000,
      method: "cash",
      idempotencyKey: "i".repeat(101),
    }),
    /idempotencyKey/i,
  );
  await assert.rejects(
    rejected.beginRepaymentClaimProvider({
      installmentGuid,
      operationGuid,
      provider: "p".repeat(33),
      providerAttemptId: "attempt",
    }),
    /provider/i,
  );
  await assert.rejects(
    rejected.beginRepaymentClaimProvider({
      installmentGuid,
      operationGuid,
      provider: "cash",
      providerAttemptId: "a".repeat(129),
    }),
    /providerAttemptId/i,
  );
});

test("新 prepare-provider 只发送六个绑定字段，并返回 ProviderPending claim", async () => {
  const payload = {
    installmentGuid,
    operationGuid,
    paymentGuid,
    amount: 80,
    method: 1,
    idempotencyKey: operationGuid,
    status: 2,
    provider: "cash",
    providerAttemptId: "cash-attempt-1",
    createdAtUtc: "2026-08-04T01:00:00Z",
    updatedAtUtc: "2026-08-04T01:01:00Z",
    expiresAtUtc: null,
    commit: null,
    alreadyExists: false,
  };
  const transport = new QueueTransport([{ success: true, data: payload }]);
  const api = new HbposInstallmentsApi(transport, "S1");

  const claim = await api.prepareRepaymentClaimProvider({
    installmentGuid,
    operationGuid,
    paymentGuid,
    amountCents: 8_000,
    method: "cash",
    idempotencyKey: operationGuid,
    provider: "cash",
    providerAttemptId: "cash-attempt-1",
  });

  assert.equal(claim.status, "ProviderPending");
  assert.deepEqual(transport.requests, [
    {
      method: "POST",
      url: `/api/v1/installments/${installmentGuid}/repayment-claims/${operationGuid}/prepare-provider`,
      data: {
        paymentGuid,
        amount: 80,
        method: 1,
        idempotencyKey: operationGuid,
        provider: "cash",
        providerAttemptId: "cash-attempt-1",
      },
    },
  ]);
});

test("旧 capabilities payload 缺少取消 claim 与跨机动作字段时保持兼容并 fail-closed", async () => {
  const api = new HbposInstallmentsApi(
    new QueueTransport([
      {
        success: true,
        data: {
          repaymentClaimsSupported: true,
        repaymentClaimsRequired: false,
          repaymentClaimPrepareProviderV1: undefined,
          crossDeviceRepaymentEnabled: false,
          preparedClaimTtlSeconds: 120,
        },
      },
    ]),
    "S1",
  );
  const capabilities: InstallmentRepaymentCapabilities =
    await api.getCapabilities();
  assert.deepEqual(capabilities, {
    repaymentClaimsSupported: true,
    repaymentClaimsRequired: false,
    repaymentClaimPrepareProviderV1: false,
    cardRepaymentSupported: false,
    crossDeviceRepaymentEnabled: false,
    crossDeviceCancelRefundEnabled: false,
    crossDeviceVoidEnabled: false,
    crossDevicePickupEnabled: false,
    preparedClaimTtlSeconds: 120,
  });
});

test("cancel claim 不携带门店或收银员身份，并复用同一 durable operation", async () => {
  const payload = {
    installmentGuid,
    operationGuid,
    idempotencyKey: operationGuid,
    refundPlanFingerprint: `sha256:${"a".repeat(64)}`,
    status: 2,
    createdAtUtc: "2026-08-04T01:00:00Z",
    updatedAtUtc: "2026-08-04T01:00:00Z",
    expiresAtUtc: null,
    commit: null,
    alreadyExists: false,
  };
  const committed = {
    ...payload,
    status: 3,
    commit: { details: detailsPayload({ status: 4, balanceAmount: 0 }), alreadyCancelled: false },
  };
  const transport = new QueueTransport([
    { success: true, data: { ...payload, status: 1 } },
    { success: true, data: payload },
    { success: true, data: payload },
    { success: true, data: { ...payload, status: 6 } },
    { success: true, data: committed },
  ]);
  const api = new HbposInstallmentsApi(transport, "S1");
  const created = await api.createCancelClaim({ installmentGuid, operationGuid, idempotencyKey: operationGuid, reason: "customer", refundPlanFingerprint: payload.refundPlanFingerprint, refundMode: "original-route" });
  // 旧服务端回包没有 refundMode：按原路退映射，交由 runtime 与本地选择严格比对。
  assert.equal(created.refundMode, "original-route");
  await api.beginCancelClaimRefund({ installmentGuid, operationGuid });
  await api.getCancelClaim({ installmentGuid, operationGuid });
  await api.resolveCancelClaim({ installmentGuid, operationGuid, outcome: "Unknown" });
  await api.commitCancelClaim({ installmentGuid, operationGuid, refunds: [{ paymentGuid, originalPaymentGuid, method: "cash", amountCents: 2_000, reference: null, cardTransactions: [], idempotencyKey: `${operationGuid}:refund:${originalPaymentGuid}` }] });
  assert.deepEqual(transport.requests.map(({ method, url, data }) => ({ method, url, data })), [
    { method: "POST", url: `/api/v1/installments/${installmentGuid}/cancel-claims`, data: { operationGuid, idempotencyKey: operationGuid, reason: "customer", refundPlanFingerprint: payload.refundPlanFingerprint, refundMode: 1 } },
    { method: "POST", url: `/api/v1/installments/${installmentGuid}/cancel-claims/${operationGuid}/begin-refund`, data: undefined },
    { method: "GET", url: `/api/v1/installments/${installmentGuid}/cancel-claims/${operationGuid}`, data: undefined },
    { method: "POST", url: `/api/v1/installments/${installmentGuid}/cancel-claims/${operationGuid}/resolve`, data: { outcome: 3 } },
    { method: "POST", url: `/api/v1/installments/${installmentGuid}/cancel-claims/${operationGuid}/commit`, data: { refunds: [{ paymentGuid, method: 1, amount: 20, reference: null, cardTransactions: [], idempotencyKey: `${operationGuid}:refund:${originalPaymentGuid}`, originalPaymentGuid }] } },
  ]);
});

test("cancel claim 退代金券模式下发 refundMode=2 并严格映射回包退款方式", async () => {
  const payload = {
    installmentGuid,
    operationGuid,
    idempotencyKey: operationGuid,
    refundPlanFingerprint: `sha256:${"b".repeat(64)}`,
    status: 1,
    createdAtUtc: "2026-08-04T01:00:00Z",
    updatedAtUtc: "2026-08-04T01:00:00Z",
    expiresAtUtc: null,
    commit: null,
    alreadyExists: false,
  };
  const transport = new QueueTransport([
    { success: true, data: { ...payload, refundMode: 2 } },
    { success: true, data: { ...payload, refundMode: 1 } },
    { success: true, data: { ...payload, refundMode: 9 } },
  ]);
  const api = new HbposInstallmentsApi(transport, "S1");
  const command = {
    installmentGuid,
    operationGuid,
    idempotencyKey: operationGuid,
    reason: null,
    refundPlanFingerprint: payload.refundPlanFingerprint,
    refundMode: "voucher" as const,
  };

  assert.equal((await api.createCancelClaim(command)).refundMode, "voucher");
  assert.equal(
    (await api.getCancelClaim({ installmentGuid, operationGuid })).refundMode,
    "original-route",
  );
  await assert.rejects(
    api.getCancelClaim({ installmentGuid, operationGuid }),
    /cancelClaim\.refundMode/u,
  );
  assert.deepEqual(transport.requests[0]?.data, {
    operationGuid,
    idempotencyKey: operationGuid,
    reason: null,
    refundPlanFingerprint: payload.refundPlanFingerprint,
    refundMode: 2,
  });
  await assert.rejects(
    api.createCancelClaim({
      ...command,
      refundMode: "card" as unknown as "voucher",
    }),
    /refundMode/u,
  );
  assert.equal(transport.requests.length, 3);
});

test("历史查询固定可信门店并严格映射状态、时间和整数分", async () => {
  const transport = new QueueTransport([
    {
      success: true,
      data: {
        orders: [
          {
            installmentGuid,
            installmentNumber: "IP-0001",
            storeCode: "S1",
            deviceCode: "IPAD-1",
            cashierName: "Alice",
            customerName: "Bob",
            customerPhone: "0400000000",
            createdAt: "2026-07-27T01:02:03Z",
            totalAmount: 120.5,
            downPaymentAmount: 20,
            paidAmount: 45.25,
            balanceAmount: 75.25,
            status: 1,
            updatedAt: "2026-07-27T02:03:04Z",
          },
        ],
      },
    },
  ]);
  const api = new HbposInstallmentsApi(transport, " S1 ");

  const orders = await api.list({
    createdFromIso: "2026-07-20T14:00:00.000Z",
    createdToIso: "2026-07-27T13:59:59.999Z",
    deviceCode: " IPAD-1 ",
    keyword: " Bob ",
    skip: 50,
    status: "Active",
    take: 51,
  });

  assert.deepEqual(transport.requests, [
    {
      method: "GET",
      url: "/api/v1/installments/history",
      params: {
        storeCode: "S1",
        deviceCode: "IPAD-1",
        createdFrom: "2026-07-20T14:00:00.000Z",
        createdTo: "2026-07-27T13:59:59.999Z",
        keyword: "Bob",
        skip: 50,
        status: 1,
        take: 51,
      },
    },
  ]);
  assert.deepEqual(orders, [
    {
      installmentGuid,
      installmentNumber: "IP-0001",
      storeCode: "S1",
      deviceCode: "IPAD-1",
      cashierName: "Alice",
      customerName: "Bob",
      customerPhone: "0400000000",
      createdAtIso: "2026-07-27T01:02:03.000Z",
      totalCents: 12_050,
      downPaymentCents: 2_000,
      paidCents: 4_525,
      balanceCents: 7_525,
      status: "Active",
      updatedAtIso: "2026-07-27T02:03:04.000Z",
    },
  ]);
});

test("详情只投影 UI 白名单，不暴露支付引用、授权码或原始回单", async () => {
  const transport = new QueueTransport([
    {
      success: true,
      data: {
        installmentGuid,
        installmentNumber: "IP-0001",
        storeCode: "S1",
        deviceCode: "IPAD-1",
        cashierId: "C1",
        cashierName: "Alice",
        customerName: "Bob",
        customerPhone: "0400000000",
        createdAt: "2026-07-27T01:02:03Z",
        totalAmount: 120.5,
        minimumDownPayment: 20,
        downPaymentAmount: 20,
        paidAmount: 120.5,
        balanceAmount: 0,
        status: 2,
        lines: [
          {
            installmentLineGuid: lineGuid,
            productCode: "P1",
            referenceCode: "R1",
            displayName: "Tea",
            lookupCode: "930001",
            quantity: 1.25,
            unitPrice: 100,
            discountAmount: 5,
            actualAmount: 120.5,
            itemNumber: "I1",
          },
        ],
        payments: [
          {
            paymentGuid,
            method: 2,
            amount: 120.5,
            reference: "provider-payment-secret",
            status: 1,
            recordedAt: "2026-07-27T02:03:04Z",
            cashierId: "C1",
            deviceCode: "IPAD-1",
            idempotencyKey: "secret-idempotency-key",
            cardTransactions: [
              {
                authCode: "AUTH-SECRET",
                cardType: "VISA",
                maskedCardNumber: "**** **** **** 1234",
                receiptText: "RAW-RECEIPT-SECRET",
                txnRef: "TXN-SECRET",
              },
            ],
          },
        ],
        pickupInfo: null,
        cancellationInfo: null,
        note: "Collect Friday",
      },
    },
  ]);

  const details = await new HbposInstallmentsApi(
    transport,
    "S1",
  ).getDetails(installmentGuid);

  assert.equal(details?.status, "PaidOff");
  assert.equal(details?.minimumDownPaymentCents, 2_000);
  assert.deepEqual(details?.lines[0], {
    installmentLineGuid: lineGuid,
    productCode: "P1",
    referenceCode: "R1",
    displayName: "Tea",
    lookupCode: "930001",
    quantity: "1.25",
    unitPriceCents: 10_000,
    discountCents: 500,
    actualAmountCents: 12_050,
    itemNumber: "I1",
  });
  assert.deepEqual(details?.payments[0], {
    paymentGuid,
    method: "card",
    amountCents: 12_050,
    status: "Recorded",
    recordedAtIso: "2026-07-27T02:03:04.000Z",
    cashierId: "C1",
    deviceCode: "IPAD-1",
    cardType: "VISA",
    maskedCardNumber: "**** **** **** 1234",
  });
  const serialized = JSON.stringify(details);
  for (const secret of [
    "provider-payment-secret",
    "secret-idempotency-key",
    "AUTH-SECRET",
    "RAW-RECEIPT-SECRET",
    "TXN-SECRET",
  ]) {
    assert.equal(serialized.includes(secret), false);
  }
});

test("详情优先使用服务端 updatedAt，null 时回退 createdAt", async () => {
  const current = detailsPayload({ status: 1, balanceAmount: 80 });
  const transport = new QueueTransport([
    { success: true, data: current },
    { success: true, data: { ...current, updatedAt: null } },
  ]);
  const api = new HbposInstallmentsApi(transport, "S1");

  const updated = await api.getDetails(installmentGuid);
  const legacy = await api.getDetails(installmentGuid);

  assert.equal(updated?.updatedAtIso, "2026-07-27T02:03:04.000Z");
  assert.equal(legacy?.updatedAtIso, "2026-07-27T01:02:03.000Z");
});

test("创建、补款、退款取消、作废和取货使用固定 v1 路由", async () => {
  const responseDetails = detailsPayload({ status: 1, balanceAmount: 80 });
  const transport = new QueueTransport([
    {
      success: true,
      data: { details: responseDetails },
    },
    {
      success: true,
      data: { details: detailsPayload({ status: 2, balanceAmount: 0 }) },
    },
    {
      success: true,
      data: { details: detailsPayload({ status: 4, balanceAmount: 0 }) },
    },
    {
      success: true,
      data: { details: detailsPayload({ status: 4, balanceAmount: 80 }) },
    },
    {
      success: true,
      data: { details: detailsPayload({ status: 3, balanceAmount: 0 }) },
    },
  ]);
  const api = new HbposInstallmentsApi(transport, "S1");
  const identity = {
    cashierId: "C1",
    cashierName: "Alice",
    deviceCode: "IPAD-1",
  };

  await api.create({
    ...identity,
    installmentGuid,
    createdAtIso: "2026-07-27T01:02:03Z",
    totalCents: 10_000,
    downPaymentCents: 2_000,
    customerName: "Bob",
    customerPhone: "0400000000",
    note: "Friday",
    lines: [
      {
        installmentLineGuid: lineGuid,
        productCode: "P1",
        referenceCode: "R1",
        displayName: "Tea",
        lookupCode: "930001",
        quantity: "1.25",
        unitPriceCents: 8_000,
        discountCents: 0,
        actualAmountCents: 10_000,
        itemNumber: "I1",
      },
    ],
    downPayment: {
      paymentGuid,
      method: "cash",
      amountCents: 2_000,
      reference: null,
      reservationToken: null,
      cardTransactions: [],
      idempotencyKey: "create-key",
    },
  });
  await api.appendPayment({
    ...identity,
    installmentGuid,
    payment: {
      paymentGuid,
      method: "card",
      amountCents: 8_000,
      reference: "PROTECTED-REF",
      reservationToken: null,
      cardTransactions: [],
      idempotencyKey: "repayment-key",
    },
  });
  await api.cancelWithRefund({
    ...identity,
    installmentGuid,
    cancelledAtIso: "2026-07-27T03:00:00Z",
    reason: "Customer request",
    idempotencyKey: "cancel-key",
    refunds: [
      {
        paymentGuid,
        method: "cash",
        amountCents: 2_000,
        reference: null,
        cardTransactions: [],
        idempotencyKey: "refund-key",
      },
    ],
  });
  await api.void({
    ...identity,
    installmentGuid,
    voidedAtIso: "2026-07-27T03:00:00Z",
    reason: "Incorrect order",
    operationGuid,
    idempotencyKey: "void-key",
  });
  await api.confirmPickup({
    ...identity,
    installmentGuid,
    confirmedAtIso: "2026-07-27T03:00:00Z",
    note: "ID checked",
    operationGuid,
    idempotencyKey: "pickup-key",
  });

  assert.deepEqual(
    transport.requests.map(({ method, url }) => ({ method, url })),
    [
      { method: "POST", url: "/api/v1/installments" },
      {
        method: "POST",
        url: `/api/v1/installments/${installmentGuid}/payments`,
      },
      {
        method: "POST",
        url: `/api/v1/installments/${installmentGuid}/cancel`,
      },
      {
        method: "POST",
        url: `/api/v1/installments/${installmentGuid}/void`,
      },
      {
        method: "POST",
        url: `/api/v1/installments/${installmentGuid}/pickup`,
      },
    ],
  );
  assert.deepEqual(transport.requests[0]?.data, {
    installmentGuid,
    storeCode: "S1",
    deviceCode: "IPAD-1",
    cashierId: "C1",
    cashierName: "Alice",
    createdAt: "2026-07-27T01:02:03.000Z",
    totalAmount: 100,
    downPaymentAmount: 20,
    lines: [
      {
        installmentLineGuid: lineGuid,
        productCode: "P1",
        referenceCode: "R1",
        displayName: "Tea",
        lookupCode: "930001",
        quantity: 1.25,
        unitPrice: 80,
        discountAmount: 0,
        actualAmount: 100,
        itemNumber: "I1",
      },
    ],
    downPayment: {
      paymentGuid,
      method: 1,
      amount: 20,
      reference: null,
      reservationToken: null,
      cardTransactions: [],
      idempotencyKey: "create-key",
    },
    customerName: "Bob",
    customerPhone: "0400000000",
    note: "Friday",
  });
  assert.deepEqual(transport.requests[3]?.data, {
    installmentGuid,
    storeCode: "S1",
    deviceCode: "IPAD-1",
    cashierId: "C1",
    cashierName: "Alice",
    voidedAt: "2026-07-27T03:00:00.000Z",
    reason: "Incorrect order",
    operationGuid,
    idempotencyKey: "void-key",
  });
  assert.deepEqual(transport.requests[4]?.data, {
    installmentGuid,
    storeCode: "S1",
    deviceCode: "IPAD-1",
    cashierId: "C1",
    cashierName: "Alice",
    confirmedAt: "2026-07-27T03:00:00.000Z",
    note: "ID checked",
    operationGuid,
    idempotencyKey: "pickup-key",
  });
});

test("真实 cancel 与 AlreadyCancelled 回包允许 payment history 负退款，但命令和 summary 仍拒绝负数", async () => {
  const cancelledDetails = {
    ...detailsPayload({ status: 4, balanceAmount: 0 }),
    paidAmount: 0,
    payments: [
      paymentPayload({ amount: 20, paymentGuid, method: 2 }),
      paymentPayload({
        amount: -20,
        paymentGuid: "20000000-0000-4000-8000-000000000002",
        method: 2,
      }),
    ],
    cancellationInfo: {
      kind: 1,
      cancelledAt: "2026-07-27T03:00:00Z",
      cancelledBy: "Alice",
      reason: "Customer request",
    },
  };
  const transport = new QueueTransport([
    { success: true, data: { details: cancelledDetails } },
    {
      success: true,
      data: {
        alreadyCancelled: true,
        details: cancelledDetails,
        message: "AlreadyCancelled",
      },
    },
  ]);
  const api = new HbposInstallmentsApi(transport, "S1");
  const command = cancelCommand();

  const cancelled = await api.cancelWithRefund(command);
  const alreadyCancelled = await api.cancelWithRefund(command);

  assert.equal(cancelled.payments[1]?.amountCents, -2_000);
  assert.equal(alreadyCancelled.payments[1]?.amountCents, -2_000);
  assert.equal(alreadyCancelled.status, "Cancelled");

  await assert.rejects(
    api.cancelWithRefund({
      ...command,
      refunds: [{ ...command.refunds[0]!, amountCents: -1 }],
    }),
    /amount/i,
  );
  const invalidSummaryApi = new HbposInstallmentsApi(
    new QueueTransport([
      {
        success: true,
        data: {
          orders: [{ ...summaryPayload(), totalAmount: -1 }],
        },
      },
    ]),
    "S1",
  );
  await assert.rejects(
    invalidSummaryApi.list({
      keyword: null,
      skip: 0,
      status: null,
      take: 100,
    }),
    /money/i,
  );
});

test("响应门店越权、金额精度漂移或写响应缺少详情时失败关闭", async () => {
  const wrongStore = new HbposInstallmentsApi(
    new QueueTransport([
      {
        success: true,
        data: {
          orders: [
            {
              ...summaryPayload(),
              storeCode: "S2",
            },
          ],
        },
      },
    ]),
    "S1",
  );
  await assert.rejects(
    () => wrongStore.list({ keyword: null, skip: 0, status: null, take: 100 }),
    /storeCode/i,
  );

  const invalidMoney = new HbposInstallmentsApi(
    new QueueTransport([
      {
        success: true,
        data: {
          orders: [{ ...summaryPayload(), totalAmount: 1.001 }],
        },
      },
    ]),
    "S1",
  );
  await assert.rejects(
    () => invalidMoney.list({ keyword: null, skip: 0, status: null, take: 100 }),
    /money/i,
  );

  const missingDetails = new HbposInstallmentsApi(
    new QueueTransport([{ success: true, data: { status: 1 } }]),
    "S1",
  );
  await assert.rejects(
    () =>
      missingDetails.void({
        installmentGuid,
        deviceCode: "IPAD-1",
        cashierId: "C1",
        cashierName: "Alice",
        voidedAtIso: "2026-07-27T03:00:00Z",
        reason: "Incorrect",
        idempotencyKey: "void-key",
      }),
    /details/i,
  );
});

function summaryPayload() {
  return {
    installmentGuid,
    installmentNumber: "IP-0001",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    cashierName: "Alice",
    customerName: "Bob",
    customerPhone: "0400000000",
    createdAt: "2026-07-27T01:02:03Z",
    totalAmount: 100,
    downPaymentAmount: 20,
    paidAmount: 20,
    balanceAmount: 80,
    status: 1,
    updatedAt: "2026-07-27T02:03:04Z",
  };
}

function detailsPayload(
  overrides: Readonly<{ status: 1 | 2 | 3 | 4; balanceAmount: number }>,
) {
  return {
    ...summaryPayload(),
    cashierId: "C1",
    minimumDownPayment: 20,
    lines: [],
    payments: [],
    pickupInfo: null,
    cancellationInfo: null,
    note: null,
    ...overrides,
  };
}

function paymentPayload(
  overrides: Readonly<{
    amount: number;
    method: 1 | 2 | 3;
    paymentGuid: string;
  }>,
) {
  return {
    paymentGuid: overrides.paymentGuid,
    method: overrides.method,
    amount: overrides.amount,
    reference: null,
    status: 1,
    recordedAt: "2026-07-27T03:00:00Z",
    cashierId: "C1",
    deviceCode: "IPAD-1",
    idempotencyKey: "payment-key",
    cardTransactions: [],
  };
}

function cancelCommand() {
  return {
    cashierId: "C1",
    cashierName: "Alice",
    deviceCode: "IPAD-1",
    installmentGuid,
    cancelledAtIso: "2026-07-27T03:00:00Z",
    reason: "Customer request",
    idempotencyKey: "cancel-key",
    refunds: [
      {
        paymentGuid: "20000000-0000-4000-8000-000000000002",
        method: "card" as const,
        amountCents: 2_000,
        reference: "protected-ref",
        cardTransactions: [],
        idempotencyKey: "refund-key",
      },
    ],
  };
}
