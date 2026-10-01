import type { OnlinePaymentPort, PaymentAttempt, PaymentProviderResult } from "@/core/contracts";

/** 只重放收银员已耐久保存的人工确认；没有任何终端或网络依赖。 */
export class ManualCardPaymentAdapter implements OnlinePaymentPort {
  public readonly provider = "manual-card" as const;

  public async submit(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return confirmedResult(attempt);
  }

  public async recover(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return confirmedResult(attempt);
  }

  public async cancel(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    // 已确认外部收款不能通过本地取消抹掉，继续要求完成原订单。
    return { ...confirmedResult(attempt), state: "Unknown", protectedSyncEvidence: null, responseCode: "MANUAL_CARD_CANNOT_CANCEL" };
  }

  public async refund(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return { ...confirmedResult(attempt), state: "Unknown", protectedSyncEvidence: null, responseCode: "MANUAL_CARD_REFUND_UNSUPPORTED" };
  }
}

function confirmedResult(attempt: PaymentAttempt): PaymentProviderResult {
  const references = attempt.references;
  const valid = attempt.provider === "manual-card" &&
    attempt.operation === "purchase" &&
    attempt.amount.currency === "AUD" &&
    Number.isSafeInteger(attempt.amount.cents) && attempt.amount.cents > 0 &&
    references.txnRef === `MANUAL:${attempt.attemptId}` &&
    references.checkoutId === null && references.paymentId === null &&
    references.sessionId === null && references.rfn === null &&
    references.voucherReservationToken === null &&
    ["Created", "Submitted", "Pending", "Unknown", "Approved"].includes(attempt.state);
  if (!valid) return { state: "Unknown", references, receiptText: null, responseCode: "MANUAL_CARD_CONFIRMATION_REQUIRED" };
  return {
    state: "Approved", references, receiptText: null, responseCode: "MANUAL_CONFIRMED",
    protectedSyncEvidence: {
      version: 1, provider: "manual-card", operation: "purchase", processor: "Manual",
      txnRef: references.txnRef, amountCents: attempt.amount.cents,
      authCode: null, cardType: null, cardBin: null, maskedCardNumber: null,
      merchantId: null, responseCode: "MANUAL_CONFIRMED", responseText: null,
      stan: null, bankDateTimeIso: null, refundReference: null,
    },
  };
}
