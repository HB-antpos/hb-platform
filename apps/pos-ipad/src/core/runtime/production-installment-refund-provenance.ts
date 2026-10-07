import type {
  InstallmentOriginalTenderEvidence,
  InstallmentRefundProvenanceRemotePort,
  InstallmentRefundProvenanceSnapshot,
} from "./production-installment-payment-adapter";

import {
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "@/core/api";
import type { PaymentAttempt, PaymentProvider } from "@/core/contracts";
import type { InstallmentCancelRefundMode } from "@/features/installments/installment-models";
import type { components } from "@hb/pos-api-client/openapi";

type GeneratedDetails = components["schemas"]["InstallmentDetailsDto"];
type GeneratedCardTransaction =
  components["schemas"]["CardTransactionDto"];

type ProvenanceScope = Readonly<{
  installmentGuid: string;
  storeCode: string;
  requestingDeviceCode: string;
}>;

export type InstallmentProtectedTenderImport =
  InstallmentOriginalTenderEvidence &
    Readonly<{
      reference: string | null;
      cardTransactions: readonly GeneratedCardTransaction[];
    }>;

/**
 * 只有 SQLCipher 实现可以接收该结构。调用返回的 snapshot 必须剥离 reference、
 * cardTransactions、provider ID 与券码，只保留支付恢复所需的安全描述符。
 */
export type InstallmentProtectedProvenanceImport = ProvenanceScope &
  Readonly<{
    paidAmountCents: number;
    tenders: readonly InstallmentProtectedTenderImport[];
  }>;

export interface InstallmentRefundProvenanceVaultPort {
  resolve(
    input: ProvenanceScope,
  ): Promise<InstallmentRefundProvenanceSnapshot | null>;
  importProtected(
    input: InstallmentProtectedProvenanceImport,
  ): Promise<InstallmentRefundProvenanceSnapshot>;
  seedRefundAttempt(input: Readonly<{
    evidence: InstallmentOriginalTenderEvidence;
    attempt: PaymentAttempt;
  }>): Promise<PaymentAttempt>;
}

/**
 * 取消分期前的原付款恢复边界。
 *
 * 远端 response 中的 Reference/CardTransactions 不进入 presenter、错误文本或日志；
 * 校验完整 scope 与金额闭合后立即交给 SQLCipher vault，公开结果只返回安全描述符。
 */
export class HbposInstallmentRefundProvenance
  implements InstallmentRefundProvenanceRemotePort
{
  public constructor(
    private readonly transport: HbposTransport,
    private readonly vault: InstallmentRefundProvenanceVaultPort,
  ) {}

  public async resolveOrImport(
    input: ProvenanceScope &
      Readonly<{ refundMode?: InstallmentCancelRefundMode }>,
  ): Promise<InstallmentRefundProvenanceSnapshot> {
    const scope = normalizeScope(input);
    const refundMode = input.refundMode ?? "original-route";
    if (refundMode !== "original-route" && refundMode !== "voucher") {
      throw new Error("Installment refund mode is invalid.");
    }
    const local = await this.vault.resolve(scope);
    if (local && validSafeSnapshot(local, scope, null)) {
      return freezeSnapshot(local);
    }

    const response = await this.transport.request<
      HbposEnvelope<GeneratedDetails | null>
    >({
      method: "GET",
      url: `/api/v1/installments/${scope.installmentGuid}`,
    });
    const details = unwrapHbposEnvelope(response.data);
    const prepared = prepareProtectedImport(details, scope, refundMode);
    if (!prepared) return incomplete(scope);

    const imported = await this.vault.importProtected(prepared);
    return validSafeSnapshot(imported, scope, prepared)
      ? freezeSnapshot(imported)
      : incomplete(scope);
  }

  public seedRefundAttempt(input: Readonly<{
    evidence: InstallmentOriginalTenderEvidence;
    attempt: PaymentAttempt;
  }>): Promise<PaymentAttempt> {
    return this.vault.seedRefundAttempt(input);
  }
}

function prepareProtectedImport(
  detailsInput: GeneratedDetails | null,
  scope: ProvenanceScope,
  refundMode: InstallmentCancelRefundMode,
): InstallmentProtectedProvenanceImport | null {
  if (!isRecord(detailsInput)) return null;
  const installmentGuid = uuid(detailsInput.installmentGuid);
  const storeCode = text(detailsInput.storeCode, 64);
  const deviceCode = text(detailsInput.deviceCode, 128);
  if (
    installmentGuid !== scope.installmentGuid ||
    storeCode !== scope.storeCode ||
    deviceCode !== scope.requestingDeviceCode
  ) {
    return null;
  }

  const paidAmountCents = cents(detailsInput.paidAmount, true);
  if (paidAmountCents === null || paidAmountCents <= 0) return null;
  if (!Array.isArray(detailsInput.payments)) return null;

  const tenders: InstallmentProtectedTenderImport[] = [];
  const paymentGuids = new Set<string>();
  const evidenceIds = new Set<string>();
  const sourceAttemptIds = new Set<string>();

  for (const paymentInput of detailsInput.payments) {
    if (!isRecord(paymentInput)) return null;
    const status = paymentInput.status;
    if (status === 2) continue;
    if (status !== 1) return null;

    const amountCents = cents(paymentInput.amount, true);
    const paymentGuid = uuid(paymentInput.paymentGuid);
    if (
      amountCents === null ||
      amountCents <= 0 ||
      !paymentGuid ||
      paymentGuids.has(paymentGuid)
    ) {
      return null;
    }

    const method = paymentMethod(paymentInput.method);
    const reference = protectedText(paymentInput.reference, 4_096);
    const cardTransactions = cardTransactionsFrom(
      paymentInput.cardTransactions,
    );
    if (!method || cardTransactions === null) return null;
    const provider = providerFor(
      method,
      reference,
      cardTransactions,
    );
    if (provider === undefined) return null;
    if (
      (method === "cash" &&
        (reference !== null || cardTransactions.length > 0)) ||
      (method === "voucher" &&
        (!reference || cardTransactions.length > 0)) ||
      // 中文注释：退代金券模式不会向卡 provider 发起退款，不再要求原卡引用与卡交易证据；
      // 但 provider 仍须能识别为 Square/Linkly（本地证据表约束 card 必带其一）。
      (method === "card" &&
        refundMode === "original-route" &&
        (!reference ||
          cardTransactions.length === 0 ||
          !cardAmountMatches(cardTransactions, amountCents)))
    ) {
      return null;
    }

    const idempotencyKey = protectedText(
      paymentInput.idempotencyKey,
      512,
    );
    const sourceAttemptId = idempotencyKey
      ? `hbpos:${idempotencyKey}:${paymentGuid}`
      : `hbpos:${paymentGuid}`;
    const evidenceId = `hbpos:${installmentGuid}:${paymentGuid}`;
    if (
      sourceAttemptIds.has(sourceAttemptId) ||
      evidenceIds.has(evidenceId)
    ) {
      return null;
    }

    paymentGuids.add(paymentGuid);
    sourceAttemptIds.add(sourceAttemptId);
    evidenceIds.add(evidenceId);
    tenders.push(
      Object.freeze({
        evidenceId,
        sourceAttemptId,
        sourcePaymentGuid: paymentGuid,
        installmentGuid,
        method,
        amountCents,
        provider,
        provenance: "hbpos-protected-details" as const,
        reference,
        cardTransactions,
      }),
    );
  }

  if (
    tenders.length === 0 ||
    tenders.reduce((sum, tender) => sum + tender.amountCents, 0) !==
      paidAmountCents
  ) {
    return null;
  }
  return Object.freeze({
    ...scope,
    paidAmountCents,
    tenders: Object.freeze(tenders),
  });
}

function validSafeSnapshot(
  snapshot: InstallmentRefundProvenanceSnapshot,
  scope: ProvenanceScope,
  expected: InstallmentProtectedProvenanceImport | null,
): boolean {
  if (
    !snapshot ||
    snapshot.complete !== true ||
    snapshot.installmentGuid !== scope.installmentGuid ||
    snapshot.storeCode !== scope.storeCode ||
    snapshot.requestingDeviceCode !== scope.requestingDeviceCode ||
    !Number.isSafeInteger(snapshot.paidAmountCents) ||
    snapshot.paidAmountCents < 0 ||
    !Array.isArray(snapshot.tenders)
  ) {
    return false;
  }
  const ids = new Set<string>();
  const attempts = new Set<string>();
  let total = 0;
  for (const tender of snapshot.tenders) {
    if (
      !safeEvidence(tender, scope.installmentGuid) ||
      ids.has(tender.evidenceId) ||
      attempts.has(tender.sourceAttemptId)
    ) {
      return false;
    }
    ids.add(tender.evidenceId);
    attempts.add(tender.sourceAttemptId);
    total += tender.amountCents;
    if (!Number.isSafeInteger(total)) return false;
  }
  if (total !== snapshot.paidAmountCents) return false;
  if (!expected) return true;
  if (
    expected.paidAmountCents !== snapshot.paidAmountCents ||
    expected.tenders.length !== snapshot.tenders.length
  ) {
    return false;
  }
  return snapshot.tenders.every((actual, index) => {
    const wanted = expected.tenders[index];
    return (
      wanted !== undefined &&
      actual.evidenceId === wanted.evidenceId &&
      actual.sourceAttemptId === wanted.sourceAttemptId &&
      actual.sourcePaymentGuid === wanted.sourcePaymentGuid &&
      actual.installmentGuid === wanted.installmentGuid &&
      actual.method === wanted.method &&
      actual.amountCents === wanted.amountCents &&
      actual.provider === wanted.provider &&
      actual.provenance === wanted.provenance
    );
  });
}

function safeEvidence(
  input: InstallmentOriginalTenderEvidence,
  installmentGuid: string,
): boolean {
  return (
    isRecord(input) &&
    text(input.evidenceId, 1_024) !== null &&
    text(input.sourceAttemptId, 1_024) !== null &&
    uuid(input.sourcePaymentGuid) !== null &&
    input.installmentGuid === installmentGuid &&
    (input.method === "cash" ||
      input.method === "card" ||
      input.method === "voucher") &&
    Number.isSafeInteger(input.amountCents) &&
    input.amountCents > 0 &&
    providerMatchesMethod(input.method, input.provider) &&
    (input.provenance === "local-approved-attempt" ||
      input.provenance === "hbpos-protected-details")
  );
}

function providerMatchesMethod(
  method: InstallmentOriginalTenderEvidence["method"],
  provider: PaymentProvider | null,
): boolean {
  if (method === "cash") return provider === null;
  if (method === "voucher") return provider === "voucher";
  return provider === "square" || provider === "linkly-cloud";
}

function providerFor(
  method: InstallmentOriginalTenderEvidence["method"],
  reference: string | null,
  transactions: readonly GeneratedCardTransaction[],
): PaymentProvider | null | undefined {
  if (method === "cash") return null;
  if (method === "voucher") return "voucher";

  const processors = transactions
    .map((transaction) => text(transaction.processor, 128)?.toLowerCase())
    .filter((value): value is string => value !== null);
  const square =
    processors.some((value) => value.includes("square")) ||
    /^(?:SQ|SQUARE)(?::|$)/iu.test(reference ?? "");
  const linkly =
    processors.some(
      (value) => value.includes("linkly") || value.includes("anz"),
    ) || /^(?:ANZ|ANZCLOUD|LINKLY)(?::|$)/iu.test(reference ?? "");
  if (square === linkly) return undefined;
  return square ? "square" : "linkly-cloud";
}

function cardTransactionsFrom(
  input: unknown,
): readonly GeneratedCardTransaction[] | null {
  if (input === null || input === undefined) return Object.freeze([]);
  if (!Array.isArray(input) || input.length > 32) return null;
  const result: GeneratedCardTransaction[] = [];
  for (const item of input) {
    if (!isRecord(item)) return null;
    const amount = cents(item.amount, false);
    if (amount === null) return null;
    result.push(
      Object.freeze({
        processor: protectedText(item.processor, 128),
        txnRef: protectedText(item.txnRef, 1_024),
        authCode: protectedText(item.authCode, 512),
        cardType: protectedText(item.cardType, 128),
        cardBin:
          item.cardBin === null || item.cardBin === undefined
            ? null
            : Number.isSafeInteger(item.cardBin)
              ? Number(item.cardBin)
              : null,
        maskedCardNumber: protectedText(
          item.maskedCardNumber,
          128,
        ),
        merchantId: protectedText(item.merchantId, 512),
        responseCode: protectedText(item.responseCode, 128),
        responseText: protectedText(item.responseText, 1_024),
        stan: protectedText(item.stan, 512),
        bankDateTime: isoOrNull(item.bankDateTime),
        amount: amount / 100,
        receiptText: protectedText(item.receiptText, 16_384),
        refundReference: protectedText(
          item.refundReference,
          2_048,
        ),
      }),
    );
  }
  return Object.freeze(result);
}

function cardAmountMatches(
  transactions: readonly GeneratedCardTransaction[],
  amountCents: number,
): boolean {
  return transactions.some(
    (transaction) => cents(transaction.amount, false) === amountCents,
  );
}

function paymentMethod(
  value: unknown,
): InstallmentOriginalTenderEvidence["method"] | null {
  if (value === 1) return "cash";
  if (value === 2) return "card";
  if (value === 3) return "voucher";
  return null;
}

function normalizeScope(input: ProvenanceScope): ProvenanceScope {
  const installmentGuid = uuid(input.installmentGuid);
  const storeCode = text(input.storeCode, 64);
  const requestingDeviceCode = text(input.requestingDeviceCode, 128);
  if (!installmentGuid || !storeCode || !requestingDeviceCode) {
    throw new Error("Installment refund provenance scope is invalid.");
  }
  return Object.freeze({
    installmentGuid,
    storeCode,
    requestingDeviceCode,
  });
}

function freezeSnapshot(
  input: InstallmentRefundProvenanceSnapshot,
): InstallmentRefundProvenanceSnapshot {
  return Object.freeze({
    complete: input.complete,
    installmentGuid: input.installmentGuid,
    storeCode: input.storeCode,
    requestingDeviceCode: input.requestingDeviceCode,
    paidAmountCents: input.paidAmountCents,
    tenders: Object.freeze(
      input.tenders.map((tender) => Object.freeze({ ...tender })),
    ),
  });
}

function incomplete(
  scope: ProvenanceScope,
): InstallmentRefundProvenanceSnapshot {
  return Object.freeze({
    complete: false,
    ...scope,
    paidAmountCents: 0,
    tenders: Object.freeze([]),
  });
}

function cents(value: unknown, allowNegative: boolean): number | null {
  if (
    typeof value !== "number" ||
    !Number.isFinite(value) ||
    (!allowNegative && value < 0)
  ) {
    return null;
  }
  const scaled = value * 100;
  const rounded = Math.round(scaled);
  return Number.isSafeInteger(rounded) &&
    Math.abs(scaled - rounded) <= 1e-7
    ? rounded
    : null;
}

function protectedText(value: unknown, maximum: number): string | null {
  if (value === null || value === undefined) return null;
  return text(value, maximum);
}

function text(value: unknown, maximum: number): string | null {
  if (typeof value !== "string") return null;
  const normalized = value.trim();
  return normalized.length > 0 &&
    normalized.length <= maximum &&
    !/[\u0000-\u001f\u007f]/u.test(normalized)
    ? normalized
    : null;
}

function uuid(value: unknown): string | null {
  const normalized = text(value, 64)?.toLowerCase() ?? null;
  return normalized &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(
      normalized,
    )
    ? normalized
    : null;
}

function isoOrNull(value: unknown): string | null {
  const candidate = protectedText(value, 128);
  if (!candidate) return null;
  const timestamp = Date.parse(candidate);
  return Number.isFinite(timestamp)
    ? new Date(timestamp).toISOString()
    : null;
}

function isRecord(
  input: unknown,
): input is Readonly<Record<string, unknown>> {
  return Boolean(input) && typeof input === "object" && !Array.isArray(input);
}
