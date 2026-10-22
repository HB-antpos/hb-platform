import { paymentProviderAmountCents } from "@hb/pos-payments-core/features/payments/payment-amount";

import type { PaymentStatusQueryResult } from "../runtime/payment-status-query-result";

import {
  HbposApiError,
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
  type HbposTransportRequest,
} from "@/core/api";
import {
  normalizeCardSyncEvidence,
  type CardSyncEvidenceV1,
  type OnlinePaymentPort,
  type PaymentAttempt,
  type PaymentProviderReferences,
  type PaymentProviderResult,
} from "@/core/contracts";
import type { components } from "@hb/pos-api-client/openapi";

type SquareCreateCheckoutRequest =
  components["schemas"]["SquareCreateCheckoutRequest"];
type SquareCheckoutActionRequest =
  components["schemas"]["SquareCheckoutActionRequest"];
type SquareCheckoutStatusResponse =
  components["schemas"]["SquareCheckoutStatusResponse"];
type SquarePaymentStatusDto =
  components["schemas"]["SquarePaymentStatusDto"];
type SquareRefundRequest = components["schemas"]["SquareRefundRequest"];
type SquareRefundResponse = components["schemas"]["SquareRefundResponse"];

export type SquareTerminalConfiguration = Readonly<{
  environment: string;
  deviceId: string;
  locationId: string;
}>;

type SquareEnvironmentConfiguration = Pick<SquareTerminalConfiguration, "environment">;

const SQUARE_SANDBOX_CREDIT_CARD_SUCCESS_DEVICE_ID =
  "9fa747a2-25ff-48ee-b078-04381f7c828f";
const SQUARE_SANDBOX_SUCCESS_MAX_AMOUNT_CENTS = 2_500;
const SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED =
  "SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED";

export type SquareTerminalConfigurationProvider =
  () => Promise<SquareTerminalConfiguration>;

type SquareRecoveryControl = Readonly<{
  signal: AbortSignal;
  deadlineAtMs: number;
}>;

/** 可注入的时钟与等待，仅供测试推进虚拟时间；生产使用系统时钟。 */
export type SquarePaymentAdapterOptions = Readonly<{
  nowMs?: () => number;
  sleep?: (delayMs: number, signal?: AbortSignal) => Promise<void>;
}>;

/** 结账已 COMPLETED 但 payment 仍是 APPROVED（已授权未入账）时，最多再等这么久（对齐 WPF #700）。 */
export const SQUARE_APPROVED_PAYMENT_WAIT_MS = 30_000;
const SQUARE_APPROVED_PAYMENT_POLL_INTERVAL_MS = 2_000;
/** 等待顾客刷卡期间幂等查询遇瞬时错误时的响应码：保持 Pending，由付款页自动轮询在其窗口内继续重查。 */
export const SQUARE_RECOVERY_TRANSIENT_ERROR = "SQUARE_RECOVERY_TRANSIENT_ERROR";
/** Square 已受理退款（PENDING）：退货当场完成，结算由后台跟踪（对齐 WPF #674）。 */
export const SQUARE_REFUND_PENDING_SETTLEMENT = "SQUARE_REFUND_PENDING";

/**
 * Square access token 永远只存在 Hbpos.Api；iPad 仅携带设备/收银员认证并调用后端代理路由。
 */
export class SquarePaymentAdapter implements OnlinePaymentPort {
  public readonly provider = "square" as const;
  public readonly providerEnvironment: string;
  private readonly nowMs: () => number;
  private readonly sleep: (delayMs: number, signal?: AbortSignal) => Promise<void>;

  public constructor(
    private readonly transport: HbposTransport,
    private readonly getConfiguration: SquareTerminalConfigurationProvider,
    providerEnvironment: string,
    options: SquarePaymentAdapterOptions = {},
  ) {
    this.providerEnvironment = requiredSquareEnvironment(providerEnvironment);
    this.nowMs = options.nowMs ?? Date.now;
    this.sleep = options.sleep ?? abortableDelay;
  }

  public submit(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.safely(attempt, async () => {
      assertPurchaseAttempt(attempt);
      const environment = environmentForAttempt(attempt);
      return attempt.references.checkoutId
        ? this.getStatusWithConfiguration(attempt, environment)
        : this.createOrReplayCheckout(
            attempt,
            configurationForAttempt(
              await this.getConfiguration(),
              environment,
            ),
          );
    });
  }

  public recover(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.recoverOnce(attempt);
  }

  /** Square 恢复专用的结构化截止能力；不向通用 OnlinePaymentPort 泄露这个扩展。 */
  public recoverWithControl(
    attempt: PaymentAttempt,
    control: SquareRecoveryControl,
  ): Promise<PaymentProviderResult> {
    return this.recoverOnce(attempt, control);
  }

  private recoverOnce(
    attempt: PaymentAttempt,
    control?: SquareRecoveryControl,
  ): Promise<PaymentProviderResult> {
    return this.safely(attempt, async () => {
      const environment = environmentForAttempt(attempt);
      if (attempt.operation === "refund") {
        assertRefundAttempt(attempt);
        return this.refundWithConfiguration(attempt, environment, control);
      }
      assertPurchaseAttempt(attempt);
      return attempt.references.checkoutId
        ? this.getStatusWithConfiguration(attempt, environment, control)
        : this.createOrReplayCheckout(
            attempt,
            configurationForAttempt(
              await this.getConfiguration(),
              environment,
            ),
            control,
          );
    });
  }

  public cancel(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.safely(attempt, async () => {
      assertPurchaseAttempt(attempt);
      const checkoutId = requiredReference(
        attempt.references.checkoutId,
        "SQUARE_CHECKOUT_ID_REQUIRED",
      );
      const configuration = environmentForAttempt(attempt);
      const checkout = await this.requestData<SquareCheckoutStatusResponse>({
        method: "POST",
        url: `/api/v1/square/checkouts/${encodeURIComponent(checkoutId)}/cancel`,
        data: actionRequest(configuration),
      });
      return this.mapCheckout(attempt, configuration, checkout);
    });
  }

  /** dismiss 只能由明确的操作员动作调用；Unknown 恢复路径绝不会自动触发。 */
  public dismiss(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.safely(attempt, async () => {
      assertPurchaseAttempt(attempt);
      const checkoutId = requiredReference(
        attempt.references.checkoutId,
        "SQUARE_CHECKOUT_ID_REQUIRED",
      );
      const configuration = environmentForAttempt(attempt);
      const checkout = await this.requestData<SquareCheckoutStatusResponse>({
        method: "POST",
        url: `/api/v1/square/checkouts/${encodeURIComponent(checkoutId)}/dismiss`,
        data: actionRequest(configuration),
      });
      return this.mapCheckout(attempt, configuration, checkout);
    });
  }

  public refund(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.safely(attempt, async () => {
      assertRefundAttempt(attempt);
      const configuration = environmentForAttempt(attempt);
      return this.refundWithConfiguration(attempt, configuration);
    });
  }

  public getStatus(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.safely(attempt, async () => {
      assertPurchaseAttempt(attempt);
      requiredReference(attempt.references.checkoutId, "SQUARE_CHECKOUT_ID_REQUIRED");
      const configuration = environmentForAttempt(attempt);
      return this.getStatusWithConfiguration(attempt, configuration);
    });
  }

  /**
   * 退款结算跟踪专用：只 GET 同一笔退款的当前状态（大写），绝不重新发起退款。
   * 回包的退款号或环境与请求不符时抛错，由跟踪器记为检查失败。
   */
  public async getRefundStatus(environment: string, refundId: string): Promise<string> {
    const expectedEnvironment = requiredSquareEnvironment(environment);
    const id = requiredReference(refundId, "SQUARE_REFUND_ID_REQUIRED");
    const refund = await this.requestData<SquareRefundResponse>({
      method: "GET",
      url: `/api/v1/square/refunds/${encodeURIComponent(id)}`,
      params: { environment: expectedEnvironment },
    });
    if (
      optionalText(refund.refundId) !== id ||
      optionalText(refund.environment)?.toLowerCase() !== expectedEnvironment.toLowerCase()
    ) {
      throw new SquareAdapterError("SQUARE_REFUND_REFERENCE_CONFLICT");
    }
    return normalizedStatus(refund.status);
  }

  /** 人工核实后的状态查询只读取已存在 checkout，禁止缺少 ID 时重新创建。 */
  public async queryExistingPayment(attempt: PaymentAttempt, control: SquareRecoveryControl = {
    signal: new AbortController().signal,
    deadlineAtMs: Date.now() + 10_000,
  }): Promise<PaymentStatusQueryResult> {
    const result = await this.safely(attempt, async () => {
      assertPurchaseAttempt(attempt);
      requiredReference(attempt.references.checkoutId, "SQUARE_CHECKOUT_ID_REQUIRED");
      const configuration = environmentForAttempt(attempt);
      return this.getStatusWithConfiguration(attempt, configuration, control);
    });
    // Unknown 包含网络、本地校验和交易证据错误，不能证明已取得有效原交易状态；
    // 瞬时错误保持的 Pending 同样没有取得原交易状态。
    return {
      ...result,
      queryVerified:
        result.state !== "Unknown" &&
        result.responseCode !== SQUARE_RECOVERY_TRANSIENT_ERROR,
    };
  }

  private async createOrReplayCheckout(
    attempt: PaymentAttempt,
    configuration: SquareTerminalConfiguration,
    control?: SquareRecoveryControl,
  ): Promise<PaymentProviderResult> {
    const amountCents = squareProviderAmountCents(attempt);
    if (
      configuration.environment.toUpperCase() === "SANDBOX" &&
      configuration.deviceId.toLowerCase() ===
        SQUARE_SANDBOX_CREDIT_CARD_SUCCESS_DEVICE_ID &&
      amountCents > SQUARE_SANDBOX_SUCCESS_MAX_AMOUNT_CENTS
    ) {
      // Square 官方成功测试终端会让超额 checkout 长时间停在 PENDING；请求前确定性拒绝。
      return providerResult(
        "Declined",
        attempt.references,
        SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED,
      );
    }
    // 中文注释：响应丢失后的恢复仍进入本方法，但请求体只复用耐久 attempt 的同一幂等键。
    const request: SquareCreateCheckoutRequest = {
      environment: configuration.environment,
      idempotencyKey: attempt.idempotencyKey,
      deviceId: configuration.deviceId,
      locationId: configuration.locationId,
      amountMoney: {
        amount: amountCents,
        currency: "AUD",
      },
      referenceId: limit(attempt.orderGuid, 40),
      note: limit(`HB POS iPad ${attempt.orderGuid}`, 500),
    };
    const checkout = await this.requestData<SquareCheckoutStatusResponse>(
      {
        method: "POST",
        url: "/api/v1/square/checkouts",
        data: request,
      },
      control,
    );
    return this.mapCheckout(attempt, configuration, checkout, control);
  }

  private async getStatusWithConfiguration(
    attempt: PaymentAttempt,
    configuration: SquareEnvironmentConfiguration,
    control?: SquareRecoveryControl,
  ): Promise<PaymentProviderResult> {
    const checkoutId = requiredReference(
      attempt.references.checkoutId,
      "SQUARE_CHECKOUT_ID_REQUIRED",
    );
    let checkout: SquareCheckoutStatusResponse;
    try {
      checkout = await this.requestData<SquareCheckoutStatusResponse>(
        {
          method: "GET",
          url: `/api/v1/square/checkouts/${encodeURIComponent(checkoutId)}`,
          params: { environment: configuration.environment },
        },
        control,
      );
    } catch (error) {
      // 顾客可能正在终端上刷卡：幂等 GET 的瞬时失败不升级为 Unknown（否则自动轮询停止），交给下一轮重查。
      const transient = transientWhileAwaitingTerminal(attempt, attempt.references, error);
      if (transient) return transient;
      throw error;
    }
    return this.mapCheckout(attempt, configuration, checkout, control);
  }

  private async mapCheckout(
    attempt: PaymentAttempt,
    configuration: SquareEnvironmentConfiguration,
    checkout: SquareCheckoutStatusResponse,
    control?: SquareRecoveryControl,
  ): Promise<PaymentProviderResult> {
    const checkoutId = optionalText(checkout.checkoutId);
    if (optionalText(checkout.environment)?.toLowerCase() !== configuration.environment.toLowerCase()) {
      return unknown(attempt.references, "SQUARE_ENVIRONMENT_CONFLICT");
    }
    if (!checkoutId) {
      return unknown(attempt.references, "SQUARE_MISSING_CHECKOUT_ID");
    }
    const checkoutReferences = mergeReference(
      attempt.references,
      "checkoutId",
      checkoutId,
    );
    if (!checkoutReferences) {
      return unknown(attempt.references, "SQUARE_REFERENCE_CONFLICT");
    }

    const status = normalizedStatus(checkout.status);
    if (isPendingCheckoutStatus(status)) {
      return providerResult("Pending", checkoutReferences, `SQUARE_CHECKOUT_${status}`);
    }
    if (status === "CANCELED") {
      return providerResult(
        "Cancelled",
        checkoutReferences,
        optionalText(checkout.cancelReason) ?? "SQUARE_CHECKOUT_CANCELED",
      );
    }
    if (status !== "COMPLETED") {
      return unknown(
        checkoutReferences,
        status ? `SQUARE_CHECKOUT_${status}` : "SQUARE_CHECKOUT_STATUS_MISSING",
      );
    }

    const paymentId = selectPaymentId(attempt.references.paymentId, checkout);
    if (paymentId.kind === "conflict") {
      return unknown(checkoutReferences, "SQUARE_REFERENCE_CONFLICT");
    }
    if (paymentId.kind === "missing") {
      return unknown(checkoutReferences, "SQUARE_MISSING_PAYMENT_ID");
    }
    const paymentReferences = mergeReference(
      checkoutReferences,
      "paymentId",
      paymentId.value,
    );
    if (!paymentReferences) {
      return unknown(checkoutReferences, "SQUARE_REFERENCE_CONFLICT");
    }

    try {
      const settled = await this.waitForCapturedPayment(
        paymentId.value,
        configuration,
        control,
      );
      if (settled.cutShort && isAwaitingTerminal(attempt)) {
        // 恢复截止/页面卸载打断了 30 秒等待：仍是已授权未入账，保持 Pending 交给后续查询，不判未知。
        return providerResult("Pending", paymentReferences, "SQUARE_PAYMENT_APPROVED");
      }
      return verifyPayment(attempt, paymentReferences, paymentId.value, settled.payment);
    } catch (error) {
      const transient = transientWhileAwaitingTerminal(attempt, paymentReferences, error);
      if (transient) return transient;
      // checkout 已验证的标识必须随 Unknown 保存，后续恢复才能继续查询原交易。
      return unknown(paymentReferences, errorCode(error));
    }
  }

  /**
   * 结账 COMPLETED 后查询付款详情；仍是 APPROVED（已授权未入账）时每 2 秒重查，最多 30 秒，
   * 避免已刷成功的卡被送进恢复中心。只有 COMPLETED 才算成功；等满仍 APPROVED 由调用方照旧判未知。
   * 受控恢复的截止时间早于 30 秒时提前停止并标记 cutShort。
   */
  private async waitForCapturedPayment(
    paymentId: string,
    configuration: SquareEnvironmentConfiguration,
    control: SquareRecoveryControl | undefined,
  ): Promise<Readonly<{ payment: SquarePaymentStatusDto; cutShort: boolean }>> {
    const query = () => this.requestData<SquarePaymentStatusDto>(
      {
        method: "GET",
        url: `/api/v1/square/payments/${encodeURIComponent(paymentId)}`,
        params: { environment: configuration.environment },
      },
      control,
    );
    let payment = await query();
    const waitUntilMs = this.nowMs() + SQUARE_APPROVED_PAYMENT_WAIT_MS;
    while (normalizedStatus(payment.status) === "APPROVED") {
      const nowMs = this.nowMs();
      const remainingMs = waitUntilMs - nowMs;
      if (remainingMs <= 0) return { payment, cutShort: false };
      const controlRemainingMs = control ? control.deadlineAtMs - nowMs : Number.POSITIVE_INFINITY;
      const delayMs = Math.min(SQUARE_APPROVED_PAYMENT_POLL_INTERVAL_MS, remainingMs);
      if (controlRemainingMs <= delayMs) return { payment, cutShort: true };
      await this.sleep(delayMs, control?.signal);
      if (control?.signal.aborted) {
        throw new SquareAdapterError("SQUARE_RECOVERY_ABORTED");
      }
      payment = await query();
    }
    return { payment, cutShort: false };
  }

  private async refundWithConfiguration(
    attempt: PaymentAttempt,
    configuration: SquareEnvironmentConfiguration,
    control?: SquareRecoveryControl,
  ): Promise<PaymentProviderResult> {
    const paymentId = requiredReference(
      attempt.references.paymentId,
      "SQUARE_ORIGINAL_PAYMENT_ID_REQUIRED",
    );
    const request: SquareRefundRequest = {
      environment: configuration.environment,
      idempotencyKey: attempt.idempotencyKey,
      paymentId,
      amountMoney: {
        amount: squareProviderAmountCents(attempt),
        currency: "AUD",
      },
    };
    // Square refund 同样按 idempotencyKey 幂等；Pending/Unknown 恢复只能重放这一个请求。
    const refund = await this.requestData<SquareRefundResponse>(
      {
        method: "POST",
        url: "/api/v1/square/refunds",
        data: request,
      },
      control,
    );
    return verifyRefund(attempt, configuration, paymentId, refund);
  }

  private async requestData<T>(
    request: HbposTransportRequest,
    control?: SquareRecoveryControl,
  ): Promise<T> {
    const response = await this.transport.request<HbposEnvelope<T>>(
      squareRecoveryRequest(request, control),
    );
    if (response.status < 200 || response.status >= 300) {
      throw new SquareAdapterError(`SQUARE_HTTP_${response.status}`);
    }
    return unwrapHbposEnvelope(response.data);
  }

  private async safely(
    attempt: PaymentAttempt,
    operation: () => Promise<PaymentProviderResult>,
  ): Promise<PaymentProviderResult> {
    try {
      return await operation();
    } catch (error) {
      return unknown(attempt.references, errorCode(error));
    }
  }
}

class SquareAdapterError extends Error {
  public constructor(public readonly code: string) {
    super(code);
    this.name = "SquareAdapterError";
  }
}

function squareRecoveryRequest(
  request: HbposTransportRequest,
  control: SquareRecoveryControl | undefined,
): HbposTransportRequest {
  // 普通 submit/recover/refund 保持原请求形状；只有自动恢复使用请求级控制。
  if (!control) return request;
  if (control.signal.aborted) {
    throw new SquareAdapterError("SQUARE_RECOVERY_ABORTED");
  }
  const remainingMs = control.deadlineAtMs - Date.now();
  if (!Number.isFinite(remainingMs) || remainingMs <= 0) {
    throw new SquareAdapterError("SQUARE_RECOVERY_DEADLINE_EXCEEDED");
  }
  return {
    ...request,
    signal: control.signal,
    timeoutMs: Math.min(15_000, Math.ceil(remainingMs)),
  };
}

function verifyPayment(
  attempt: PaymentAttempt,
  references: PaymentProviderReferences,
  expectedPaymentId: string,
  payment: SquarePaymentStatusDto,
): PaymentProviderResult {
  const returnedPaymentId = optionalText(payment.paymentId);
  if (!returnedPaymentId || returnedPaymentId !== expectedPaymentId) {
    return unknown(references, "SQUARE_REFERENCE_CONFLICT");
  }

  const status = normalizedStatus(payment.status);
  if (status === "FAILED" || status === "REJECTED") {
    return providerResult("Declined", references, `SQUARE_PAYMENT_${status}`);
  }
  if (status === "CANCELED") {
    return providerResult("Cancelled", references, "SQUARE_PAYMENT_CANCELED");
  }
  if (status !== "COMPLETED") {
    return unknown(
      references,
      status ? `SQUARE_PAYMENT_${status}` : "SQUARE_PAYMENT_STATUS_MISSING",
    );
  }

  const money = payment.approvedMoney ?? payment.totalMoney;
  const expectedAmountCents = squareProviderAmountCents(attempt);
  if (
    !money ||
    money.amount !== expectedAmountCents ||
    normalizedStatus(money.currency) !== "AUD"
  ) {
    return unknown(references, "SQUARE_PAYMENT_VERIFICATION_FAILED");
  }
  return approvedWithEvidence(
    references,
    normalizeSquarePaymentEvidence(attempt, returnedPaymentId, status, payment),
  );
}

function verifyRefund(
  attempt: PaymentAttempt,
  configuration: SquareEnvironmentConfiguration,
  expectedPaymentId: string,
  refund: SquareRefundResponse,
): PaymentProviderResult {
  if (
    optionalText(refund.environment)?.toLowerCase() !==
    configuration.environment.toLowerCase()
  ) {
    return unknown(attempt.references, "SQUARE_ENVIRONMENT_CONFLICT");
  }
  const refundId = optionalText(refund.refundId);
  const returnedPaymentId = optionalText(refund.paymentId);
  if (
    !refundId ||
    (returnedPaymentId !== null && returnedPaymentId !== expectedPaymentId)
  ) {
    return unknown(attempt.references, "SQUARE_REFUND_REFERENCE_CONFLICT");
  }

  const amountCents = squareProviderAmountCents(attempt);
  if (
    refund.amountMoney &&
    (refund.amountMoney.amount !== amountCents ||
      normalizedStatus(refund.amountMoney.currency) !== "AUD")
  ) {
    return unknown(attempt.references, "SQUARE_REFUND_VERIFICATION_FAILED");
  }

  const status = normalizedStatus(refund.status);
  if (
    status === "PENDING" &&
    (returnedPaymentId !== expectedPaymentId || !refund.amountMoney)
  ) {
    // 引用或金额无法逐项核对时不能当作已受理，保留原 Pending 语义由恢复处理。
    return providerResult(
      "Pending",
      attempt.references,
      SQUARE_REFUND_PENDING_SETTLEMENT,
    );
  }
  if (status === "PENDING" || status === "COMPLETED") {
    // Square 官方：PENDING = 已受理、正在退回原卡（多数几小时、最长 14 天），只有商家余额与绑定银行
    // 账户都不足时才会 REJECTED/FAILED。与 WPF #674 一致视为已发起：退货当场完成，结算由后台只读跟踪。
    if (returnedPaymentId !== expectedPaymentId) {
      return unknown(
        attempt.references,
        "SQUARE_REFUND_REFERENCE_CONFLICT",
      );
    }
    if (!refund.amountMoney) {
      return unknown(
        attempt.references,
        "SQUARE_REFUND_VERIFICATION_FAILED",
      );
    }
    const approved = approvedWithEvidence(
      attempt.references,
      normalizeSquareRefundEvidence(
        attempt,
        returnedPaymentId,
        refundId,
        status,
        refund,
      ),
    );
    // 已受理未结算的退款用专用响应码落库，后台结算跟踪据此登记（见 M53）。
    return status === "PENDING" && approved.state === "Approved"
      ? { ...approved, responseCode: SQUARE_REFUND_PENDING_SETTLEMENT }
      : approved;
  }
  if (status === "FAILED" || status === "REJECTED") {
    return providerResult("Declined", attempt.references, `SQUARE_REFUND_${status}`);
  }
  if (status === "CANCELED") {
    return providerResult("Cancelled", attempt.references, "SQUARE_REFUND_CANCELED");
  }
  return unknown(
    attempt.references,
    status ? `SQUARE_REFUND_${status}` : "SQUARE_REFUND_STATUS_MISSING",
  );
}

function selectPaymentId(
  existingPaymentId: string | null,
  checkout: SquareCheckoutStatusResponse,
):
  | Readonly<{ kind: "value"; value: string }>
  | Readonly<{ kind: "missing" }>
  | Readonly<{ kind: "conflict" }> {
  const candidates = [
    optionalText(checkout.payment?.paymentId),
    ...(checkout.paymentIds ?? []).map(optionalText),
  ].filter((value): value is string => value !== null);
  const unique = [...new Set(candidates)];

  if (existingPaymentId) {
    return unique.length > 0 && !unique.includes(existingPaymentId)
      ? { kind: "conflict" }
      : { kind: "value", value: existingPaymentId };
  }
  if (unique.length > 1) {
    return { kind: "conflict" };
  }
  return unique[0] ? { kind: "value", value: unique[0] } : { kind: "missing" };
}

function mergeReference<K extends "checkoutId" | "paymentId">(
  references: PaymentProviderReferences,
  key: K,
  value: string,
): PaymentProviderReferences | null {
  const existing = references[key];
  if (existing !== null && existing !== value) return null;
  return { ...references, [key]: value };
}

function actionRequest(
  configuration: SquareEnvironmentConfiguration,
): SquareCheckoutActionRequest {
  return { environment: configuration.environment };
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

function approvedWithEvidence(
  references: PaymentProviderReferences,
  evidenceFactory: () => CardSyncEvidenceV1,
): PaymentProviderResult {
  try {
    return {
      ...providerResult("Approved", references, null),
      protectedSyncEvidence: evidenceFactory(),
    };
  } catch {
    // 中文注释：Square 已完成但安全证据无法白名单化时不得向上误报 Approved。
    return unknown(references, "SQUARE_SYNC_EVIDENCE_INVALID");
  }
}

function normalizeSquarePaymentEvidence(
  attempt: PaymentAttempt,
  paymentId: string,
  status: string,
  payment: SquarePaymentStatusDto,
): () => CardSyncEvidenceV1 {
  return () =>
    normalizeCardSyncEvidence({
      version: 1,
      provider: "square",
      operation: attempt.operation,
      processor: "Square",
      txnRef: paymentId,
      authCode: optionalText(payment.authCode),
      cardType: optionalText(payment.cardBrand),
      cardBin: null,
      maskedCardNumber: optionalText(payment.maskedCardNumber),
      merchantId: null,
      responseCode: null,
      responseText: status,
      stan: null,
      bankDateTimeIso: optionalText(payment.updatedAt),
      amountCents: squareProviderAmountCents(attempt),
      refundReference: null,
    });
}

function normalizeSquareRefundEvidence(
  attempt: PaymentAttempt,
  paymentId: string,
  refundId: string,
  status: string,
  refund: SquareRefundResponse,
): () => CardSyncEvidenceV1 {
  return () =>
    normalizeCardSyncEvidence({
      version: 1,
      provider: "square",
      operation: attempt.operation,
      processor: "Square",
      txnRef: paymentId,
      authCode: null,
      cardType: null,
      cardBin: null,
      maskedCardNumber: null,
      merchantId: null,
      responseCode: null,
      responseText: status,
      stan: null,
      bankDateTimeIso: optionalText(refund.updatedAt),
      amountCents: squareProviderAmountCents(attempt),
      refundReference: refundId,
    });
}

function unknown(
  references: PaymentProviderReferences,
  responseCode: string,
): PaymentProviderResult {
  return providerResult("Unknown", references, responseCode);
}

function assertPurchaseAttempt(attempt: PaymentAttempt): void {
  assertSquareAttempt(attempt);
  if (attempt.operation !== "purchase") {
    throw new SquareAdapterError("SQUARE_PURCHASE_OPERATION_REQUIRED");
  }
  squareProviderAmountCents(attempt);
}

function assertRefundAttempt(attempt: PaymentAttempt): void {
  assertSquareAttempt(attempt);
  if (attempt.operation !== "refund") {
    throw new SquareAdapterError("SQUARE_REFUND_OPERATION_REQUIRED");
  }
  squareProviderAmountCents(attempt);
}

function assertSquareAttempt(attempt: PaymentAttempt): void {
  if (attempt.provider !== "square") {
    throw new SquareAdapterError("SQUARE_PROVIDER_MISMATCH");
  }
  if (!attempt.idempotencyKey.trim()) {
    throw new SquareAdapterError("SQUARE_IDEMPOTENCY_KEY_REQUIRED");
  }
}

function squareProviderAmountCents(attempt: PaymentAttempt): number {
  const amount = paymentProviderAmountCents(
    attempt.operation,
    attempt.amount,
  );
  if (amount === null) {
    throw new SquareAdapterError("SQUARE_AMOUNT_INVALID");
  }
  return amount;
}

function normalizeConfiguration(
  configuration: SquareTerminalConfiguration,
): SquareTerminalConfiguration {
  const environment = requiredSquareEnvironment(configuration.environment);
  const rawDeviceId = requiredConfigurationText(
    configuration.deviceId,
    "SQUARE_DEVICE_ID_REQUIRED",
  );
  const deviceId = rawDeviceId.toLowerCase().startsWith("device:")
    ? rawDeviceId.slice("device:".length)
    : rawDeviceId;
  if (!deviceId) throw new SquareAdapterError("SQUARE_DEVICE_ID_REQUIRED");
  return {
    environment,
    deviceId,
    locationId: requiredConfigurationText(
      configuration.locationId,
      "SQUARE_LOCATION_ID_REQUIRED",
    ),
  };
}

function configurationForAttempt(
  configuration: SquareTerminalConfiguration,
  expected: SquareEnvironmentConfiguration,
): SquareTerminalConfiguration {
  const normalized = normalizeConfiguration(configuration);
  if (normalized.environment !== expected.environment) {
    throw new SquareAdapterError("SQUARE_ENVIRONMENT_CONFLICT");
  }
  return normalized;
}

function environmentForAttempt(
  attempt: PaymentAttempt,
): SquareEnvironmentConfiguration {
  return { environment: requiredSquareEnvironment(attempt.providerEnvironment) };
}

function requiredSquareEnvironment(value: unknown): string {
  const normalized = optionalText(value)?.toLowerCase();
  if (!normalized) {
    throw new SquareAdapterError("SQUARE_ENVIRONMENT_REQUIRED");
  }
  if (normalized === "sandbox") return "Sandbox";
  if (normalized === "production") return "Production";
  throw new SquareAdapterError("SQUARE_ENVIRONMENT_INVALID");
}

function requiredConfigurationText(value: string, code: string): string {
  const text = value.trim();
  if (!text) throw new SquareAdapterError(code);
  return text;
}

function requiredReference(value: string | null, code: string): string {
  const text = optionalText(value);
  if (!text) throw new SquareAdapterError(code);
  return text;
}

function optionalText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function normalizedStatus(value: unknown): string {
  return optionalText(value)?.toUpperCase() ?? "";
}

/** 还在等终端结果（已有 checkout）的 attempt：瞬时错误只能保持 Pending，不能升级为 Unknown。 */
function isAwaitingTerminal(attempt: PaymentAttempt): boolean {
  return (
    attempt.operation === "purchase" &&
    (attempt.state === "Submitted" || attempt.state === "Pending")
  );
}

/**
 * 等待刷卡期间的幂等 GET 失败分类（对齐 WPF #732，参照 Linkly 后端 controlledTransportFailure）：
 * 408/429/5xx、无 HTTP 响应的网络故障属于瞬时错误，保持 Pending 让付款页自动轮询在其窗口
 * （约 90 秒、最多 45 次）内继续重查，窗口即重试上限；abort、截止、4xx、信封/校验错误照旧判未知。
 */
function transientWhileAwaitingTerminal(
  attempt: PaymentAttempt,
  references: PaymentProviderReferences,
  error: unknown,
): PaymentProviderResult | null {
  if (!isAwaitingTerminal(attempt) || !references.checkoutId) return null;
  if (!isTransientSquareError(error)) return null;
  return providerResult("Pending", references, SQUARE_RECOVERY_TRANSIENT_ERROR);
}

function isTransientSquareError(error: unknown): boolean {
  if (error instanceof SquareAdapterError) {
    return /^SQUARE_HTTP_(408|429|5\d\d)$/.test(error.code);
  }
  if (!(error instanceof HbposApiError)) return false;
  if (error.kind === "transport") return error.code === "NO_HTTP_RESPONSE";
  return (
    error.kind === "http" &&
    error.status !== undefined &&
    (error.status === 408 || error.status === 429 || error.status >= 500)
  );
}

function abortableDelay(delayMs: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal?.aborted) {
      resolve();
      return;
    }
    const onAbort = () => {
      clearTimeout(timer);
      resolve();
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, delayMs);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

function isPendingCheckoutStatus(status: string): boolean {
  return status === "PENDING" || status === "IN_PROGRESS" || status === "CANCEL_REQUESTED";
}

function errorCode(error: unknown): string {
  if (error instanceof SquareAdapterError) return error.code;
  if (error instanceof HbposApiError) {
    const code = optionalText(error.code);
    if (code && /^[A-Za-z0-9_.:-]{1,80}$/.test(code)) return code.toUpperCase();
    if (error.status !== undefined) return `SQUARE_HTTP_${error.status}`;
    return error.kind === "envelope"
      ? "SQUARE_ENVELOPE_ERROR"
      : "SQUARE_TRANSPORT_ERROR";
  }
  return "SQUARE_TRANSPORT_ERROR";
}

function limit(value: string, maximumLength: number): string {
  return value.length <= maximumLength ? value : value.slice(0, maximumLength);
}
