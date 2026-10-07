import { paymentProviderAmountCents } from "@hb/pos-payments-core/features/payments/payment-amount";

import {
  HbposApiError,
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
  type HbposTransportRequest,
} from "@/core/api";
import type {
  OnlinePaymentPort,
  PaymentAttempt,
  PaymentOperation,
  PaymentProviderReferences,
  PaymentProviderResult,
} from "@/core/contracts";
import type { components } from "@hb/pos-api-client/openapi";

type VoucherQueryResponse =
  components["schemas"]["StoreVoucherQueryResponse"];
type VoucherLockRequest =
  components["schemas"]["StoreVoucherLockRequest"];
type VoucherLockResponse =
  components["schemas"]["StoreVoucherLockResponse"];
type VoucherReleaseRequest =
  components["schemas"]["StoreVoucherReleaseRequest"];
type VoucherReleaseResponse =
  components["schemas"]["StoreVoucherReleaseResponse"];
type VoucherRefundRequest =
  components["schemas"]["StoreVoucherIssueRefundRequest"];
type VoucherRefundResponse =
  components["schemas"]["StoreVoucherIssueRefundResponse"];
type VoucherIssueRequest =
  components["schemas"]["StoreVoucherIssueRequest"];
type VoucherIssueResponse =
  components["schemas"]["StoreVoucherIssueResponse"];

export type VoucherPaymentContext = Readonly<{
  storeCode: string;
  cashierId: string;
  voucherCode: string | null;
  refundReason: string | null;
}>;

export type VoucherPaymentContextProvider =
  (attempt: PaymentAttempt) => Promise<VoucherPaymentContext>;

export type VoucherProtectedPhase =
  | "purchase-prepared"
  | "lock-submitted"
  | "approved"
  | "release-submitted"
  | "released"
  | "refund-submitted";

export type VoucherLatestBalanceConfirmation =
  | Readonly<{
      status: "confirmed";
      remainingCents: number;
      confirmedAtIso: string;
    }>
  | Readonly<{
      status: "unavailable";
      remainingCents: null;
      confirmedAtIso: string;
    }>;

/**
 * 此结构包含敏感券码与 reservation token。
 * 实现方必须使用 Keychain/SQLCipher 等受保护存储，不得放入 AsyncStorage 或日志。
 */
export type VoucherProtectedAttemptState = Readonly<{
  protectedReference: string;
  attemptId: string;
  idempotencyKey: string;
  orderGuid: string;
  operation: PaymentOperation;
  phase: VoucherProtectedPhase;
  storeCode: string;
  cashierId: string;
  voucherCode: string | null;
  reservationToken: string | null;
  amountCents: number;
  expiresAtIso: string | null;
  reason?: string | null;
  /** 仅在服务端订单核销成功后单调补齐；锁券时余额不得写入此字段。 */
  latestBalanceConfirmation?: VoucherLatestBalanceConfirmation;
}>;

export type VoucherProtectedAttemptStateDraft = Omit<
  VoucherProtectedAttemptState,
  "protectedReference"
>;

export interface VoucherProtectedTokenPort {
  /**
   * 同一 attempt 必须返回稳定的 `vpr_` 句柄；句柄本身不得编码券码或 token。
   * 状态写入必须先于对应的不可逆 Hbpos.Api 请求完成。
   */
  save(state: VoucherProtectedAttemptStateDraft): Promise<string>;
  getByAttempt(attemptId: string): Promise<VoucherProtectedAttemptState | null>;
  resolve(protectedReference: string): Promise<VoucherProtectedAttemptState | null>;
}

/** iPad 只访问 Hbpos.Api；券数据库和 reservation 表从不由客户端直连。 */
export class VoucherHbposApi {
  public constructor(private readonly transport: HbposTransport) {}

  public query(
    storeCode: string,
    voucherCode: string,
  ): Promise<VoucherQueryResponse> {
    return this.requestData({
      method: "GET",
      url: `/api/v1/vouchers/${encodeURIComponent(voucherCode)}`,
      params: { storeCode },
    });
  }

  public lock(input: VoucherLockRequest): Promise<VoucherLockResponse> {
    return this.requestData({
      method: "POST",
      url: "/api/v1/vouchers/lock",
      data: input,
    });
  }

  public release(
    input: VoucherReleaseRequest,
  ): Promise<VoucherReleaseResponse> {
    return this.requestData({
      method: "POST",
      url: "/api/v1/vouchers/release",
      data: input,
    });
  }

  public refund(input: VoucherRefundRequest): Promise<VoucherRefundResponse> {
    return this.requestData({
      method: "POST",
      url: "/api/v1/vouchers/refund",
      data: input,
    });
  }

  /**
   * 当前 Hbpos.Api 会以 410/VOUCHER_ISSUE_DISABLED 拒绝 direct issue。
   * 保留强类型调用面仅用于合同对齐和管理功能显式展示该限制。
   */
  public issue(input: VoucherIssueRequest): Promise<VoucherIssueResponse> {
    return this.requestData({
      method: "POST",
      url: "/api/v1/vouchers/issue",
      data: input,
    });
  }

  private async requestData<T>(request: HbposTransportRequest): Promise<T> {
    const response = await this.transport.request<HbposEnvelope<T>>(request);
    if (response.status < 200 || response.status >= 300) {
      throw new HbposApiError("Voucher API request failed.", {
        kind: "http",
        status: response.status,
      });
    }
    return unwrapHbposEnvelope(response.data);
  }
}

/**
 * 券支付的耐久适配器。
 *
 * lock 没有服务端幂等键和状态查询端点，因此 POST 前先保存 lock-submitted；
 * 一旦响应丢失，只能保持 Unknown，绝不自动重锁或释放。退款签发则始终以
 * attempt.idempotencyKey 重放，服务端会返回同一退款券。
 */
export class VoucherPaymentAdapter implements OnlinePaymentPort {
  public readonly provider = "voucher" as const;

  public constructor(
    private readonly api: VoucherHbposApi,
    private readonly protectedTokens: VoucherProtectedTokenPort,
    private readonly getContext: VoucherPaymentContextProvider,
  ) {}

  public submit(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return attempt.operation === "refund"
      ? this.refund(attempt)
      : this.safely(attempt, () => this.submitPurchase(attempt));
  }

  public recover(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.safely(attempt, async () => {
      assertVoucherAttempt(attempt);
      const state = await this.readBoundState(attempt);
      if (!state) {
        if (attempt.state === "Unknown") {
          return unknown(
            attempt.references,
            "VOUCHER_PROTECTED_STATE_MISSING",
          );
        }
        return attempt.operation === "refund"
          ? this.submitRefund(attempt)
          : this.submitPurchase(attempt);
      }

      if (state.phase === "approved") {
        return approvedFromState(attempt, state);
      }
      if (state.phase === "released") {
        return providerResult(
          "Cancelled",
          referencesWithProtectedToken(
            attempt.references,
            state.protectedReference,
          ),
          "VOUCHER_RELEASED",
        );
      }
      if (state.phase === "release-submitted") {
        return unknown(
          referencesWithProtectedToken(
            attempt.references,
            state.protectedReference,
          ),
          "VOUCHER_RELEASE_RESULT_UNRESOLVED",
        );
      }
      if (state.phase === "lock-submitted") {
        return unknown(
          attempt.references,
          "VOUCHER_LOCK_RESULT_UNRESOLVED",
        );
      }
      if (state.phase === "refund-submitted") {
        return attempt.operation === "refund"
          ? this.issueRefund(attempt, state)
          : unknown(attempt.references, "VOUCHER_PROTECTED_REFERENCE_CONFLICT");
      }
      if (state.phase === "purchase-prepared") {
        return attempt.operation === "purchase"
          ? this.lockPreparedPurchase(attempt, state)
          : unknown(attempt.references, "VOUCHER_PROTECTED_REFERENCE_CONFLICT");
      }
      return unknown(attempt.references, "VOUCHER_STATE_UNRESOLVED");
    });
  }

  public cancel(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    if (attempt.state === "Unknown") {
      return Promise.resolve(
        unknown(
          attempt.references,
          "VOUCHER_UNKNOWN_REQUIRES_RECOVERY",
        ),
      );
    }
    return this.releaseReservation(attempt);
  }

  public refund(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.safely(attempt, () => this.submitRefund(attempt));
  }

  public queryVoucher(
    storeCode: string,
    voucherCode: string,
  ): Promise<VoucherQueryResponse> {
    return this.api.query(
      requiredText(storeCode, "VOUCHER_STORE_CODE_REQUIRED"),
      requiredText(voucherCode, "VOUCHER_CODE_REQUIRED"),
    );
  }

  /** 只能由明确的移除 tender/取消动作调用；Unknown 恢复路径不会进入这里。 */
  public releaseReservation(
    attempt: PaymentAttempt,
  ): Promise<PaymentProviderResult> {
    if (attempt.state === "Unknown") {
      return Promise.resolve(
        unknown(
          attempt.references,
          "VOUCHER_UNKNOWN_REQUIRES_RECOVERY",
        ),
      );
    }
    return this.safelyRelease(attempt, async () => {
      assertPurchaseAttempt(attempt);
      const state = await this.readBoundState(attempt);
      if (
        !state ||
        state.operation !== "purchase" ||
        (state.phase !== "approved" &&
          state.phase !== "release-submitted" &&
          state.phase !== "released")
      ) {
        return unknown(
          attempt.references,
          "VOUCHER_RESERVATION_REQUIRED",
        );
      }

      releaseRequestFromState(state);
      if (state.phase === "released") {
        return providerResult(
          "Cancelled",
          referencesWithProtectedToken(
            attempt.references,
            state.protectedReference,
          ),
          "VOUCHER_RELEASED",
        );
      }

      // approved 必须先耐久化为 release-submitted；后续专用调用只重放同一受保护请求。
      const submitted =
        state.phase === "approved"
          ? await this.persistState(
              {
                ...stateWithoutReference(state),
                phase: "release-submitted",
              },
              state.protectedReference,
            )
          : state;
      const released = await this.api.release(
        releaseRequestFromState(submitted),
      );
      if (
        optionalText(released.voucherCode) !== submitted.voucherCode ||
        optionalText(released.reservationToken) !==
          submitted.reservationToken ||
        released.released !== true
      ) {
        return unknown(
          referencesWithProtectedToken(
            attempt.references,
            submitted.protectedReference,
          ),
          "VOUCHER_RELEASE_RESULT_UNRESOLVED",
        );
      }

      const completed = await this.persistState(
        {
          ...stateWithoutReference(submitted),
          phase: "released",
        },
        submitted.protectedReference,
      );
      return providerResult(
        "Cancelled",
        referencesWithProtectedToken(
          attempt.references,
          completed.protectedReference,
        ),
        "VOUCHER_RELEASED",
      );
    });
  }

  private async submitPurchase(
    attempt: PaymentAttempt,
  ): Promise<PaymentProviderResult> {
    assertPurchaseAttempt(attempt);
    const existing = await this.readBoundState(attempt);
    if (existing) {
      if (existing.phase === "approved") {
        return approvedFromState(attempt, existing);
      }
      if (existing.phase === "released") {
        return providerResult(
          "Cancelled",
          referencesWithProtectedToken(
            attempt.references,
            existing.protectedReference,
          ),
          "VOUCHER_RELEASED",
        );
      }
      if (existing.phase === "lock-submitted") {
        return unknown(
          attempt.references,
          "VOUCHER_LOCK_RESULT_UNRESOLVED",
        );
      }
      if (existing.phase === "release-submitted") {
        return unknown(
          referencesWithProtectedToken(
            attempt.references,
            existing.protectedReference,
          ),
          "VOUCHER_RELEASE_RESULT_UNRESOLVED",
        );
      }
      if (existing.phase !== "purchase-prepared") {
        return unknown(
          attempt.references,
          "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
        );
      }
      return this.lockPreparedPurchase(attempt, existing);
    }

    const context = normalizeContext(await this.getContext(attempt), true);
    const prepared = await this.persistState({
      attemptId: attempt.attemptId,
      idempotencyKey: attempt.idempotencyKey,
      orderGuid: attempt.orderGuid,
      operation: "purchase",
      phase: "purchase-prepared",
      storeCode: context.storeCode,
      cashierId: context.cashierId,
      voucherCode: context.voucherCode,
      reservationToken: null,
      amountCents: attempt.amount.cents,
      expiresAtIso: null,
      reason: null,
    });
    return this.lockPreparedPurchase(attempt, prepared);
  }

  private async lockPreparedPurchase(
    attempt: PaymentAttempt,
    prepared: VoucherProtectedAttemptState,
  ): Promise<PaymentProviderResult> {
    if (!prepared.voucherCode) {
      throw new VoucherAdapterError("VOUCHER_CODE_REQUIRED");
    }
    const query = await this.api.query(
      prepared.storeCode,
      prepared.voucherCode,
    );
    const voucher = query.voucher;
    if (query.found !== true || !voucher) {
      return providerResult(
        "Declined",
        attempt.references,
        "VOUCHER_NOT_FOUND",
      );
    }

    const returnedVoucherCode = requiredText(
      voucher.voucherCode,
      "VOUCHER_QUERY_CODE_MISSING",
    );
    if (!sameText(returnedVoucherCode, prepared.voucherCode)) {
      throw new VoucherAdapterError("VOUCHER_REFERENCE_CONFLICT");
    }
    const returnedStoreCode = optionalText(voucher.storeCode);
    if (
      returnedStoreCode !== null &&
      !sameText(returnedStoreCode, prepared.storeCode)
    ) {
      throw new VoucherAdapterError("VOUCHER_STORE_CONFLICT");
    }
    if (optionalText(voucher.status) !== "1") {
      return providerResult(
        "Declined",
        attempt.references,
        "VOUCHER_UNAVAILABLE",
      );
    }
    const remainingCents = amountToCents(
      voucher.remainingAmount,
      "VOUCHER_REMAINING_AMOUNT_INVALID",
    );
    if (remainingCents < attempt.amount.cents) {
      return providerResult(
        "Declined",
        attempt.references,
        "VOUCHER_INSUFFICIENT_BALANCE",
      );
    }

    const submitted = await this.persistState(
      {
        ...stateWithoutReference(prepared),
        phase: "lock-submitted",
      },
      prepared.protectedReference,
    );
    const locked = await this.api.lock({
      storeCode: submitted.storeCode,
      voucherCode: submitted.voucherCode,
      requestedAmount: centsToAmount(attempt.amount.cents),
    });
    const lockedVoucherCode = requiredText(
      locked.voucherCode,
      "VOUCHER_LOCK_CODE_MISSING",
    );
    if (!sameText(lockedVoucherCode, submitted.voucherCode)) {
      throw new VoucherAdapterError("VOUCHER_REFERENCE_CONFLICT");
    }
    const lockedCents = amountToCents(
      locked.lockedAmount,
      "VOUCHER_LOCK_AMOUNT_INVALID",
    );
    if (lockedCents !== attempt.amount.cents) {
      throw new VoucherAdapterError("VOUCHER_LOCK_AMOUNT_MISMATCH");
    }
    if (
      locked.remainingAmountAfterLock !== null &&
      locked.remainingAmountAfterLock !== undefined &&
      amountToCents(
        locked.remainingAmountAfterLock,
        "VOUCHER_REMAINING_AMOUNT_INVALID",
      ) < 0
    ) {
      throw new VoucherAdapterError("VOUCHER_REMAINING_AMOUNT_INVALID");
    }
    const reservationToken = requiredText(
      locked.reservationToken,
      "VOUCHER_RESERVATION_TOKEN_MISSING",
    );
    const expiresAtIso = requiredIsoDate(
      locked.expiresAt,
      "VOUCHER_LOCK_EXPIRY_INVALID",
    );
    const approved = await this.persistState(
      {
        ...stateWithoutReference(submitted),
        phase: "approved",
        voucherCode: lockedVoucherCode,
        reservationToken,
        expiresAtIso,
      },
      submitted.protectedReference,
    );
    return approvedFromState(attempt, approved);
  }

  private async submitRefund(
    attempt: PaymentAttempt,
  ): Promise<PaymentProviderResult> {
    assertRefundAttempt(attempt);
    const existing = await this.readBoundState(attempt);
    if (existing) {
      if (existing.phase === "approved") {
        return approvedFromState(attempt, existing);
      }
      if (existing.phase !== "refund-submitted") {
        return unknown(
          attempt.references,
          "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
        );
      }
      return this.issueRefund(attempt, existing);
    }

    if (attempt.state === "Unknown") {
      return unknown(
        attempt.references,
        "VOUCHER_PROTECTED_STATE_MISSING",
      );
    }
    const context = normalizeContext(await this.getContext(attempt), false);
    const submitted = await this.persistState({
      attemptId: attempt.attemptId,
      idempotencyKey: attempt.idempotencyKey,
      orderGuid: attempt.orderGuid,
      operation: "refund",
      phase: "refund-submitted",
      storeCode: context.storeCode,
      cashierId: context.cashierId,
      voucherCode: null,
      reservationToken: null,
      amountCents: attempt.amount.cents,
      expiresAtIso: null,
      reason: context.refundReason,
    });
    return this.issueRefund(attempt, submitted);
  }

  private async issueRefund(
    attempt: PaymentAttempt,
    submitted: VoucherProtectedAttemptState,
  ): Promise<PaymentProviderResult> {
    const issued = await this.api.refund({
      storeCode: submitted.storeCode,
      amount: centsToAmount(voucherProviderAmountCents(attempt)),
      cashierId: submitted.cashierId,
      idempotencyKey: attempt.idempotencyKey,
      orderReference: attempt.orderGuid,
      reason: submitted.reason ?? null,
    });
    const voucherCode = requiredText(
      issued.voucherCode,
      "VOUCHER_REFUND_CODE_MISSING",
    );
    if (
      amountToCents(
        issued.amount,
        "VOUCHER_REFUND_AMOUNT_INVALID",
      ) !== voucherProviderAmountCents(attempt) ||
      amountToCents(
        issued.remainingAmount,
        "VOUCHER_REFUND_REMAINING_INVALID",
      ) !== voucherProviderAmountCents(attempt)
    ) {
      throw new VoucherAdapterError("VOUCHER_REFUND_AMOUNT_MISMATCH");
    }
    if (optionalText(issued.status) !== "1") {
      throw new VoucherAdapterError("VOUCHER_REFUND_STATUS_INVALID");
    }
    const expiresAtIso = requiredIsoDate(
      issued.expiredAt,
      "VOUCHER_REFUND_EXPIRY_INVALID",
    );
    const approved = await this.persistState(
      {
        ...stateWithoutReference(submitted),
        phase: "approved",
        voucherCode,
        reservationToken: null,
        expiresAtIso,
      },
      submitted.protectedReference,
    );
    return approvedFromState(attempt, approved);
  }

  private async readBoundState(
    attempt: PaymentAttempt,
  ): Promise<VoucherProtectedAttemptState | null> {
    const protectedReference =
      attempt.references.voucherReservationToken;
    const [byAttempt, byReference] = await Promise.all([
      this.protectedTokens.getByAttempt(attempt.attemptId),
      protectedReference
        ? this.protectedTokens.resolve(protectedReference)
        : Promise.resolve(null),
    ]);
    if (
      protectedReference &&
      (!byReference ||
        (byAttempt &&
          byAttempt.protectedReference !==
            byReference.protectedReference))
    ) {
      throw new VoucherAdapterError(
        "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
      );
    }
    const state = byReference ?? byAttempt;
    if (!state) return null;
    validateProtectedState(attempt, state);
    if (
      protectedReference &&
      state.protectedReference !== protectedReference
    ) {
      throw new VoucherAdapterError(
        "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
      );
    }
    return state;
  }

  private async persistState(
    draft: VoucherProtectedAttemptStateDraft,
    expectedReference?: string,
  ): Promise<VoucherProtectedAttemptState> {
    const protectedReference = requiredProtectedReference(
      await this.protectedTokens.save(draft),
    );
    if (
      expectedReference !== undefined &&
      protectedReference !== expectedReference
    ) {
      throw new VoucherAdapterError(
        "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
      );
    }
    return { ...draft, protectedReference };
  }

  private async safely(
    attempt: PaymentAttempt,
    operation: () => Promise<PaymentProviderResult>,
  ): Promise<PaymentProviderResult> {
    try {
      assertVoucherAttempt(attempt);
      return await operation();
    } catch (error) {
      const code = voucherErrorCode(error);
      return isStableVoucherRejection(error)
        ? providerResult("Declined", attempt.references, code)
        : unknown(attempt.references, code);
    }
  }

  private async safelyRelease(
    attempt: PaymentAttempt,
    operation: () => Promise<PaymentProviderResult>,
  ): Promise<PaymentProviderResult> {
    try {
      assertVoucherAttempt(attempt);
      return await operation();
    } catch (error) {
      return unknown(attempt.references, voucherErrorCode(error));
    }
  }
}

class VoucherAdapterError extends Error {
  public constructor(public readonly code: string) {
    super(code);
    this.name = "VoucherAdapterError";
  }
}

function approvedFromState(
  attempt: PaymentAttempt,
  state: VoucherProtectedAttemptState,
): PaymentProviderResult {
  validateProtectedState(attempt, state);
  if (
    state.phase !== "approved" ||
    !state.voucherCode ||
    (state.operation === "purchase" && !state.reservationToken)
  ) {
    return unknown(
      attempt.references,
      "VOUCHER_APPROVED_STATE_INVALID",
    );
  }
  return providerResult(
    "Approved",
    referencesWithProtectedToken(
      attempt.references,
      state.protectedReference,
    ),
    state.operation === "refund"
      ? "VOUCHER_REFUND_ISSUED"
      : "VOUCHER_LOCKED",
  );
}

function assertPurchaseAttempt(attempt: PaymentAttempt): void {
  assertVoucherAttempt(attempt);
  if (attempt.operation !== "purchase") {
    throw new VoucherAdapterError(
      "VOUCHER_PURCHASE_OPERATION_REQUIRED",
    );
  }
}

function assertRefundAttempt(attempt: PaymentAttempt): void {
  assertVoucherAttempt(attempt);
  if (attempt.operation !== "refund") {
    throw new VoucherAdapterError("VOUCHER_REFUND_OPERATION_REQUIRED");
  }
}

function assertVoucherAttempt(attempt: PaymentAttempt): void {
  if (attempt.provider !== "voucher") {
    throw new VoucherAdapterError("VOUCHER_PROVIDER_MISMATCH");
  }
  if (!attempt.attemptId.trim()) {
    throw new VoucherAdapterError("VOUCHER_ATTEMPT_ID_REQUIRED");
  }
  if (!attempt.idempotencyKey.trim()) {
    throw new VoucherAdapterError(
      "VOUCHER_IDEMPOTENCY_KEY_REQUIRED",
    );
  }
  if (!attempt.orderGuid.trim()) {
    throw new VoucherAdapterError("VOUCHER_ORDER_GUID_REQUIRED");
  }
  voucherProviderAmountCents(attempt);
  for (const reference of [
    attempt.references.checkoutId,
    attempt.references.paymentId,
    attempt.references.sessionId,
    attempt.references.txnRef,
    attempt.references.rfn,
  ]) {
    if (reference !== null) {
      throw new VoucherAdapterError("VOUCHER_REFERENCE_CONFLICT");
    }
  }
}

function validateProtectedState(
  attempt: PaymentAttempt,
  state: VoucherProtectedAttemptState,
): void {
  requiredProtectedReference(state.protectedReference);
  if (
    state.attemptId !== attempt.attemptId ||
    state.idempotencyKey !== attempt.idempotencyKey ||
    state.orderGuid !== attempt.orderGuid ||
    state.operation !== attempt.operation ||
    state.amountCents !== attempt.amount.cents
  ) {
    throw new VoucherAdapterError(
      "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
    );
  }
  requiredText(state.storeCode, "VOUCHER_STORE_CODE_REQUIRED");
  requiredText(state.cashierId, "VOUCHER_CASHIER_ID_REQUIRED");
}

function normalizeContext(
  value: VoucherPaymentContext,
  voucherCodeRequired: boolean,
): VoucherPaymentContext {
  const voucherCode = optionalText(value.voucherCode);
  if (voucherCodeRequired && !voucherCode) {
    throw new VoucherAdapterError("VOUCHER_CODE_REQUIRED");
  }
  return {
    storeCode: requiredText(
      value.storeCode,
      "VOUCHER_STORE_CODE_REQUIRED",
    ),
    cashierId: requiredText(
      value.cashierId,
      "VOUCHER_CASHIER_ID_REQUIRED",
    ),
    voucherCode,
    refundReason: optionalText(value.refundReason),
  };
}

function referencesWithProtectedToken(
  references: PaymentProviderReferences,
  protectedReference: string,
): PaymentProviderReferences {
  const existing = references.voucherReservationToken;
  if (existing !== null && existing !== protectedReference) {
    throw new VoucherAdapterError(
      "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
    );
  }
  return {
    ...references,
    voucherReservationToken: requiredProtectedReference(
      protectedReference,
    ),
  };
}

function providerResult(
  state: PaymentProviderResult["state"],
  references: PaymentProviderReferences,
  responseCode: string | null,
): PaymentProviderResult {
  return {
    state,
    references,
    receiptText: null,
    responseCode,
  };
}

function unknown(
  references: PaymentProviderReferences,
  responseCode: string,
): PaymentProviderResult {
  return providerResult("Unknown", references, responseCode);
}

function stateWithoutReference(
  state: VoucherProtectedAttemptState,
): VoucherProtectedAttemptStateDraft {
  const { protectedReference: _, ...draft } = state;
  return draft;
}

function releaseRequestFromState(
  state: VoucherProtectedAttemptState,
): VoucherReleaseRequest {
  return {
    storeCode: exactProtectedText(
      state.storeCode,
      "VOUCHER_STORE_CODE_REQUIRED",
    ),
    voucherCode: exactProtectedText(
      state.voucherCode,
      "VOUCHER_CODE_REQUIRED",
    ),
    reservationToken: exactProtectedText(
      state.reservationToken,
      "VOUCHER_RESERVATION_TOKEN_MISSING",
    ),
  };
}

function exactProtectedText(
  value: string | null,
  code: string,
): string {
  const normalized = requiredText(value, code);
  if (normalized !== value) {
    throw new VoucherAdapterError(
      "VOUCHER_PROTECTED_REFERENCE_CONFLICT",
    );
  }
  return normalized;
}

function centsToAmount(cents: number): number {
  if (!Number.isSafeInteger(cents) || cents <= 0) {
    throw new VoucherAdapterError("VOUCHER_AMOUNT_INVALID");
  }
  return cents / 100;
}

function voucherProviderAmountCents(attempt: PaymentAttempt): number {
  const amount = paymentProviderAmountCents(
    attempt.operation,
    attempt.amount,
  );
  if (amount === null) {
    throw new VoucherAdapterError("VOUCHER_AMOUNT_INVALID");
  }
  return amount;
}

function amountToCents(value: unknown, code: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new VoucherAdapterError(code);
  }
  const rawCents = value * 100;
  const cents = Math.round(rawCents);
  if (
    !Number.isSafeInteger(cents) ||
    Math.abs(rawCents - cents) > 1e-6
  ) {
    throw new VoucherAdapterError(code);
  }
  return cents;
}

function requiredText(value: unknown, code: string): string {
  const text = optionalText(value);
  if (!text) throw new VoucherAdapterError(code);
  return text;
}

function optionalText(value: unknown): string | null {
  return typeof value === "string" && value.trim()
    ? value.trim()
    : null;
}

/**
 * 校验服务端返回的时间并规范化为 `toISOString()` 形态（UTC、毫秒、Z）。
 *
 * 服务端（ASP.NET Core + System.Text.Json 默认序列化 DateTimeOffset）返回的是
 * "2027-01-05T12:59:59+00:00" 这类带偏移的形态，而 SqliteVoucherProtectedTokenStore 只接受
 * 规范形态：不规范化会在服务端已出券之后落库抛 TypeError，退款/锁券卡死且同一幂等键重放仍失败。
 * 锁券（expiresAt）与退款（expiredAt）共用此函数；已是规范形态时输出不变。
 */
function requiredIsoDate(value: unknown, code: string): string {
  const text = requiredText(value, code);
  const epochMilliseconds = Date.parse(text);
  if (!Number.isFinite(epochMilliseconds)) {
    throw new VoucherAdapterError(code);
  }
  return new Date(epochMilliseconds).toISOString();
}

function requiredProtectedReference(value: unknown): string {
  const reference = requiredText(
    value,
    "VOUCHER_PROTECTED_REFERENCE_INVALID",
  );
  if (!/^vpr_[A-Za-z0-9_-]{8,192}$/.test(reference)) {
    throw new VoucherAdapterError(
      "VOUCHER_PROTECTED_REFERENCE_INVALID",
    );
  }
  return reference;
}

function sameText(left: string, right: string | null): boolean {
  return (
    right !== null &&
    left.localeCompare(right, undefined, { sensitivity: "accent" }) === 0
  );
}

function voucherErrorCode(error: unknown): string {
  if (error instanceof VoucherAdapterError) return error.code;
  if (error instanceof HbposApiError) {
    const code = normalizedCode(error.code);
    if (code) return code;
    if (error.status !== undefined) return `VOUCHER_HTTP_${error.status}`;
    return error.kind === "envelope"
      ? "VOUCHER_ENVELOPE_ERROR"
      : "VOUCHER_TRANSPORT_ERROR";
  }
  return "VOUCHER_UNEXPECTED_ERROR";
}

function normalizedCode(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const code = value.trim().toUpperCase();
  return /^[A-Z0-9][A-Z0-9_.:-]{0,63}$/.test(code)
    ? code
    : null;
}

function isStableVoucherRejection(error: unknown): boolean {
  if (!(error instanceof HbposApiError)) return false;
  if (error.kind === "envelope") return true;
  return (
    error.kind === "http" &&
    error.status !== undefined &&
    error.status >= 400 &&
    error.status < 500 &&
    error.status !== 408
  );
}
