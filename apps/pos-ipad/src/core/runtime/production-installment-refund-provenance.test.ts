import assert from "node:assert/strict";
import test from "node:test";

import type {
  InstallmentOriginalTenderEvidence,
  InstallmentRefundProvenanceSnapshot,
} from "./production-installment-payment-adapter";
import {
  HbposInstallmentRefundProvenance,
  type InstallmentProtectedProvenanceImport,
  type InstallmentRefundProvenanceVaultPort,
} from "./production-installment-refund-provenance";

import type {
  HbposTransport,
  HbposTransportRequest,
  HbposTransportResponse,
} from "@/core/api";
import type { PaymentAttempt } from "@/core/contracts";

const INSTALLMENT_GUID = "10000000-0000-4000-8000-000000000001";
const STORE_CODE = "STORE-1";
const DEVICE_CODE = "IPAD-1";

test("完整本地 provenance 直接返回，不访问远端或重新导入", async () => {
  const local = snapshot([
    evidence("cash", 2_000, "01", null),
  ]);
  const vault = new RecordingVault(local);
  const transport = new RecordingTransport([]);
  const provenance = new HbposInstallmentRefundProvenance(transport, vault);

  assert.deepEqual(await provenance.resolveOrImport(scope()), local);
  assert.equal(transport.requests.length, 0);
  assert.equal(vault.imports.length, 0);
});

test("远端详情的现金、Square、Linkly 和券只以受保护 import 进入 vault，返回安全描述符", async () => {
  const transport = new RecordingTransport([
    ok(details({
      paidAmount: 100,
      payments: [
        payment("01", 1, 10, null, null, "cash-action"),
        payment(
          "02",
          2,
          20,
          "SQ:SQUARE-PAYMENT-ID",
          [card("Square", "SQUARE-TXN", null, 20)],
          "square-attempt",
        ),
        payment(
          "03",
          2,
          30,
          "ANZCLOUD:LINKLY-TXN:LINKLY-RFN",
          [card("ANZ Linkly Cloud", "LINKLY-TXN", "LINKLY-RFN", 30)],
          null,
        ),
        payment(
          "04",
          3,
          40,
          "VOUCHER-CODE-SECRET",
          null,
          "voucher-attempt",
        ),
      ],
    })),
  ]);
  const vault = new RecordingVault(null);
  const provenance = new HbposInstallmentRefundProvenance(transport, vault);

  const result = await provenance.resolveOrImport(scope());

  assert.equal(result.complete, true);
  assert.equal(result.paidAmountCents, 10_000);
  assert.deepEqual(
    result.tenders.map(({ method, amountCents, provider }) => ({
      method,
      amountCents,
      provider,
    })),
    [
      { method: "cash", amountCents: 1_000, provider: null },
      { method: "card", amountCents: 2_000, provider: "square" },
      { method: "card", amountCents: 3_000, provider: "linkly-cloud" },
      { method: "voucher", amountCents: 4_000, provider: "voucher" },
    ],
  );
  assert.equal(JSON.stringify(result).includes("SQUARE-PAYMENT-ID"), false);
  assert.equal(JSON.stringify(result).includes("LINKLY-RFN"), false);
  assert.equal(JSON.stringify(result).includes("VOUCHER-CODE-SECRET"), false);

  const imported = vault.imports[0];
  assert.ok(imported);
  assert.equal(imported.tenders[1]?.reference, "SQ:SQUARE-PAYMENT-ID");
  assert.equal(
    imported.tenders[2]?.cardTransactions[0]?.refundReference,
    "LINKLY-RFN",
  );
  assert.equal(imported.tenders[3]?.reference, "VOUCHER-CODE-SECRET");
  assert.equal(
    imported.tenders[2]?.sourceAttemptId,
    `hbpos:${paymentGuid("03")}`,
  );
});

test("金额不闭合、卡 provider 不明确或跨 scope 时返回 incomplete 且不污染 vault", async () => {
  const cases = [
    details({
      paidAmount: 21,
      payments: [
        payment(
          "01",
          2,
          20,
          "CARD:UNKNOWN",
          [card("Card", "UNKNOWN", null, 20)],
          null,
        ),
      ],
    }),
    details({
      paidAmount: 20,
      payments: [
        payment("01", 1, 10, null, null, null),
      ],
    }),
    details({
      storeCode: "OTHER",
      paidAmount: 10,
      payments: [payment("01", 1, 10, null, null, null)],
    }),
  ];

  for (const value of cases) {
    const vault = new RecordingVault(null);
    const provenance = new HbposInstallmentRefundProvenance(
      new RecordingTransport([ok(value)]),
      vault,
    );

    const result = await provenance.resolveOrImport(scope());

    assert.deepEqual(result, {
      complete: false,
      installmentGuid: INSTALLMENT_GUID,
      storeCode: STORE_CODE,
      requestingDeviceCode: DEVICE_CODE,
      paidAmountCents: 0,
      tenders: [],
    });
    assert.equal(vault.imports.length, 0);
  }
});

test("退代金券模式不要求原卡交易证据，但卡 provider 仍须可识别；原路退保持严格", async () => {
  // 刷卡原付款只有 Square 前缀引用、没有卡交易明细。
  const withoutCardTransactions = details({
    paidAmount: 30,
    payments: [
      payment("01", 1, 10, null, null, null),
      payment("02", 2, 20, "SQ:SQUARE-PAYMENT-ID", null, null),
    ],
  });

  const strictVault = new RecordingVault(null);
  const strict = await new HbposInstallmentRefundProvenance(
    new RecordingTransport([ok(withoutCardTransactions)]),
    strictVault,
  ).resolveOrImport(scope());
  assert.equal(strict.complete, false);
  assert.equal(strictVault.imports.length, 0);

  const voucherVault = new RecordingVault(null);
  const voucherMode = await new HbposInstallmentRefundProvenance(
    new RecordingTransport([ok(withoutCardTransactions)]),
    voucherVault,
  ).resolveOrImport({ ...scope(), refundMode: "voucher" });
  assert.equal(voucherMode.complete, true);
  assert.deepEqual(
    voucherMode.tenders.map(({ method, provider }) => [method, provider]),
    [["cash", null], ["card", "square"]],
  );

  // 卡 provider 无法识别时（本地证据表约束 card 必带 Square/Linkly），即使退代金券也失败关闭。
  const unknownVault = new RecordingVault(null);
  const unknownProvider = await new HbposInstallmentRefundProvenance(
    new RecordingTransport([
      ok(details({
        paidAmount: 20,
        payments: [payment("01", 2, 20, "CARD:UNKNOWN", null, null)],
      })),
    ]),
    unknownVault,
  ).resolveOrImport({ ...scope(), refundMode: "voucher" });
  assert.equal(unknownProvider.complete, false);
  assert.equal(unknownVault.imports.length, 0);
});

test("seedRefundAttempt 只委托受保护 vault，adapter 仍会复核 attempt 身份", async () => {
  const vault = new RecordingVault(snapshot([]));
  const provenance = new HbposInstallmentRefundProvenance(
    new RecordingTransport([]),
    vault,
  );
  const original = attempt();
  const seeded = await provenance.seedRefundAttempt({
    evidence: evidence("card", 2_000, "01", "square"),
    attempt: original,
  });

  assert.equal(vault.seedCalls.length, 1);
  assert.equal(seeded.references.paymentId, "PROTECTED-PAYMENT-ID");
  assert.equal(seeded.attemptId, original.attemptId);
});

class RecordingTransport implements HbposTransport {
  public readonly requests: HbposTransportRequest[] = [];

  public constructor(
    private readonly responses: HbposTransportResponse<unknown>[],
  ) {}

  public async request<T>(
    request: HbposTransportRequest,
  ): Promise<HbposTransportResponse<T>> {
    this.requests.push(request);
    const response = this.responses.shift();
    if (!response) throw new Error("Unexpected transport request.");
    return response as HbposTransportResponse<T>;
  }
}

class RecordingVault implements InstallmentRefundProvenanceVaultPort {
  public readonly imports: InstallmentProtectedProvenanceImport[] = [];
  public readonly seedCalls: unknown[] = [];

  public constructor(
    private current: InstallmentRefundProvenanceSnapshot | null,
  ) {}

  public resolve(): Promise<InstallmentRefundProvenanceSnapshot | null> {
    return Promise.resolve(this.current);
  }

  public importProtected(
    input: InstallmentProtectedProvenanceImport,
  ): Promise<InstallmentRefundProvenanceSnapshot> {
    this.imports.push(input);
    this.current = {
      complete: true,
      installmentGuid: input.installmentGuid,
      storeCode: input.storeCode,
      requestingDeviceCode: input.requestingDeviceCode,
      paidAmountCents: input.paidAmountCents,
      tenders: input.tenders.map(
        ({
          reference: _reference,
          cardTransactions: _cardTransactions,
          ...safe
        }) => safe,
      ),
    };
    return Promise.resolve(this.current);
  }

  public seedRefundAttempt(input: Readonly<{
    evidence: InstallmentOriginalTenderEvidence;
    attempt: PaymentAttempt;
  }>): Promise<PaymentAttempt> {
    this.seedCalls.push(input);
    return Promise.resolve({
      ...input.attempt,
      references: {
        ...input.attempt.references,
        paymentId: "PROTECTED-PAYMENT-ID",
      },
    });
  }
}

function scope() {
  return {
    installmentGuid: INSTALLMENT_GUID,
    storeCode: STORE_CODE,
    requestingDeviceCode: DEVICE_CODE,
  };
}

function details(input: Readonly<{
  storeCode?: string;
  paidAmount: number;
  payments: readonly Record<string, unknown>[];
}>): Record<string, unknown> {
  return {
    installmentGuid: INSTALLMENT_GUID,
    installmentNumber: "IP-0001",
    storeCode: input.storeCode ?? STORE_CODE,
    deviceCode: DEVICE_CODE,
    cashierId: "cashier-1",
    cashierName: "Alice",
    customerName: "Customer",
    customerPhone: "0400000000",
    createdAt: "2026-07-28T01:00:00.000Z",
    totalAmount: 150,
    minimumDownPayment: 20,
    downPaymentAmount: 10,
    paidAmount: input.paidAmount,
    balanceAmount: 50,
    status: 1,
    lines: [],
    payments: input.payments,
    pickupInfo: null,
    cancellationInfo: null,
    note: null,
  };
}

function payment(
  suffix: string,
  method: 1 | 2 | 3,
  amount: number,
  reference: string | null,
  cardTransactions: readonly Record<string, unknown>[] | null,
  idempotencyKey: string | null,
): Record<string, unknown> {
  return {
    paymentGuid: paymentGuid(suffix),
    method,
    amount,
    reference,
    status: 1,
    recordedAt: "2026-07-28T01:01:00.000Z",
    cashierId: "cashier-1",
    deviceCode: DEVICE_CODE,
    cardTransactions,
    idempotencyKey,
  };
}

function card(
  processor: string,
  txnRef: string,
  refundReference: string | null,
  amount: number,
): Record<string, unknown> {
  return {
    processor,
    txnRef,
    authCode: "AUTH",
    cardType: "VISA",
    cardBin: 411111,
    maskedCardNumber: "****1111",
    merchantId: "MID",
    responseCode: "00",
    responseText: "APPROVED",
    stan: "STAN",
    bankDateTime: "2026-07-28T01:01:00.000Z",
    amount,
    receiptText: "RECEIPT",
    refundReference,
  };
}

function evidence(
  method: "cash" | "card" | "voucher",
  amountCents: number,
  suffix: string,
  provider: InstallmentOriginalTenderEvidence["provider"],
): InstallmentOriginalTenderEvidence {
  return {
    evidenceId: `hbpos:${INSTALLMENT_GUID}:${paymentGuid(suffix)}`,
    sourceAttemptId: `source-${suffix}`,
    sourcePaymentGuid: paymentGuid(suffix),
    installmentGuid: INSTALLMENT_GUID,
    method,
    amountCents,
    provider,
    provenance: "hbpos-protected-details",
  };
}

function snapshot(
  tenders: readonly InstallmentOriginalTenderEvidence[],
): InstallmentRefundProvenanceSnapshot {
  return {
    complete: true,
    installmentGuid: INSTALLMENT_GUID,
    storeCode: STORE_CODE,
    requestingDeviceCode: DEVICE_CODE,
    paidAmountCents: tenders.reduce(
      (total, tender) => total + tender.amountCents,
      0,
    ),
    tenders,
  };
}

function paymentGuid(suffix: string): string {
  return `20000000-0000-4000-8000-${suffix.padStart(12, "0")}`;
}

function attempt(): PaymentAttempt {
  return {
    attemptId: "30000000-0000-4000-8000-000000000001",
    idempotencyKey: "40000000-0000-4000-8000-000000000001",
    orderGuid: INSTALLMENT_GUID,
    provider: "square",
    operation: "refund",
    amount: { currency: "AUD", cents: -2_000 },
    state: "Created",
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: null,
      txnRef: null,
      rfn: null,
      voucherReservationToken: null,
    },
    createdAtIso: "2026-07-28T01:02:00.000Z",
    updatedAtIso: "2026-07-28T01:02:00.000Z",
    lastErrorCode: null,
    receiptText: null,
    responseCode: null,
  };
}

function ok(data: unknown): HbposTransportResponse<unknown> {
  return {
    status: 200,
    data: {
      success: true,
      data,
      message: null,
      errors: null,
    },
  };
}
