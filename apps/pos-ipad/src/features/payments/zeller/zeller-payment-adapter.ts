import type {
  CardSyncEvidenceV1,
  OnlinePaymentPort,
  PaymentAttempt,
  PaymentProviderReferences,
  PaymentProviderResult,
} from "@hb/pos-domain/core/contracts/payment";
import { deriveZellerAttemptReference } from "@hb/pos-payments-core/features/payments/zeller/zeller-payment-reference";
import {
  classifyZellerCall,
  classifyZellerVerification,
  type ZellerAttemptExpectation,
  type ZellerFailureKind,
  type ZellerPendingDecision,
} from "@hb/pos-payments-core/features/payments/zeller/zeller-result-classifier";
import {
  ZELLER_NO_CALL_EVIDENCE,
  accumulateZellerCallEvidence,
  mapZellerSdkCallException,
  mapZellerSdkCallResponse,
  mapZellerSdkPairingResponse,
  mapZellerSdkQueryResponse,
  type ZellerCallEvidence,
} from "@hb/pos-payments-core/features/payments/zeller/zeller-sdk-mapper";
import type {
  ZellerCallResult,
  ZellerQueryResult,
  ZellerTransaction,
} from "@hb/pos-payments-core/features/payments/zeller/zeller-terminal-models";

import type { ZellerTerminalSource } from "./zeller-terminal-registry";

import type { ZellerProbeOperation } from "@/features/zeller-probe/zeller-probe-runner";

/**
 * 刷卡机读到卡之后被判成未付款的，要再等这么久、重新查到 0 笔才收尾。
 * 依据：WPF 端按未付款收尾约 60 秒后复核，查到过迟到的批准（钱其实扣了）。
 */
export const ZELLER_NOT_PAID_SETTLE_MS = 60_000;

/** 一笔还在本进程内存里的 Zeller 扣款调用。进程重启后不存在，只能按 reference 查询。 */
type InFlightPurchase = {
  readonly operation: ZellerProbeOperation;
  evidence: ZellerCallEvidence;
  /** SDK 调用结束后的转换结果；还没结束时为 null。 */
  settled: ZellerCallResult | null;
  settledAtMs: number | null;
};

export type ZellerPaymentAdapterOptions = Readonly<{
  terminals: ZellerTerminalSource;
  /** 测试可注入。 */
  nowMs?: () => number;
}>;

/**
 * Zeller 刷卡机直连的收款适配器（只做扣款；原路退款还没有接入）。
 *
 * 中文注释：与 Square / Linkly 的做法保持同一个节奏，所以收银页不需要为 Zeller 另写流程：
 * 1. submit 把请求交给 SDK 后立刻返回 Pending，不等顾客刷完卡。SDK 的扣款调用会一直阻塞到刷卡结束，
 *    如果在 submit 里等它，等待期间页面处于忙碌状态，取消按钮点不了。
 * 2. 结果由 recover 收：调用结束前返回 Pending；结束后用分类器下结论；需要核实的再按 reference 查询。
 * 3. 进程重启后内存里的调用不存在了，recover 只按 reference 查询，绝不重新发起扣款
 *    （Zeller 不对 reference 去重，同一个 reference 再扣一次就是再收一次钱）。
 *
 * 判定原则（与 WPF 的 ZellerResultClassifier 共用同一套规则和测试向量）：只有状态为批准且金额、reference
 * 都对得上才算批准；只有明确的拒绝，或明确的取消 / 超时并且按 reference 查到 0 笔，才算未付款；其余一律结果未知。
 *
 * 同一时间只允许一个 SDK 调用：SDK 并发时会返回 Operation Busy，并且真机上出现过把一个调用的结果交给另一个调用。
 */
export class ZellerPaymentAdapter implements OnlinePaymentPort {
  public readonly provider = "zeller" as const;

  private readonly terminals: ZellerTerminalSource;
  private readonly nowMs: () => number;
  private readonly inFlight = new Map<string, InFlightPurchase>();
  /** 正在占用 SDK 的调用数（扣款或查询）。大于 0 时不再向 SDK 发任何请求。 */
  private sdkCalls = 0;
  /** 最近一次 recover 是否真的从 Zeller 拿到了可信的答复（供 queryExistingPayment 报告）。 */
  private lastRecoverVerified = false;

  public constructor(options: ZellerPaymentAdapterOptions) {
    this.terminals = options.terminals;
    this.nowMs = options.nowMs ?? (() => Date.now());
  }

  public async submit(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    const reference = this.purchaseReference(attempt);
    // 以下几种情况请求都没有发给 Zeller，可以放心按未付款收尾。
    if (reference === null || attempt.amount.cents <= 0 || attempt.amount.currency !== "AUD") {
      return this.notSubmitted(attempt, "ZELLER_NOT_SUBMITTED_INVALID_ATTEMPT");
    }
    // 这笔已经发起过（内存里还有它的调用）：绝不再发第二次，只收结果。
    if (this.inFlight.has(attempt.attemptId)) return this.recover(attempt);
    const terminal = this.terminals.current();
    if (terminal === null) {
      // 让界面层去加载 SDK，下一次发起时就有终端了；这一次请求没有发出。
      this.terminals.request?.();
      return this.notSubmitted(attempt, "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE");
    }
    if (this.sdkCalls > 0) {
      return this.notSubmitted(attempt, "ZELLER_NOT_SUBMITTED_TERMINAL_BUSY");
    }

    // 中文注释：发扣款之前先确认这台 iPad 还配着对（不弹窗、不涉及金额）。
    // 不做这一步的话，没配对时扣款会返回「需要设置」，随后按 reference 的核实查询同样因为没配对而失败，
    // 这笔付款只能落成「结果未知」交给主管——而实际上请求根本没有发出。在这里拦下就能干净地按「没有发出」收尾。
    const pairingBlocker = await this.pairingBlocker(terminal);
    if (pairingBlocker !== null) return this.notSubmitted(attempt, pairingBlocker);
    // 检查配对期间可能有别的调用进来（比如另一单的核实查询），再确认一次没有人占着 SDK，也没有重复发起。
    if (this.inFlight.has(attempt.attemptId)) return this.recover(attempt);
    if (this.sdkCalls > 0) {
      return this.notSubmitted(attempt, "ZELLER_NOT_SUBMITTED_TERMINAL_BUSY");
    }

    let operation: ZellerProbeOperation;
    try {
      operation = terminal.purchase({ amount: attempt.amount.cents, reference });
    } catch {
      // 调用还没进入 SDK 就抛了：请求确定没有发出。
      return this.notSubmitted(attempt, "ZELLER_NOT_SUBMITTED_SDK_UNAVAILABLE");
    }
    const record: InFlightPurchase = {
      operation,
      evidence: ZELLER_NO_CALL_EVIDENCE,
      settled: null,
      settledAtMs: null,
    };
    this.inFlight.set(attempt.attemptId, record);
    this.sdkCalls += 1;
    try {
      operation.onEvent?.((event) => {
        record.evidence = accumulateZellerCallEvidence(record.evidence, event);
      });
    } catch {
      // 订阅事件失败不影响扣款本身；只是少了「请求是否到过刷卡机」的证据，分类会更保守。
    }
    const settle = (result: ZellerCallResult) => {
      record.settled = result;
      record.settledAtMs = this.nowMs();
      this.sdkCalls -= 1;
    };
    void Promise.resolve(operation).then(
      (raw) => settle(mapZellerSdkCallResponse(raw, record.evidence)),
      (error: unknown) => settle(mapZellerSdkCallException(error, record.evidence)),
    );
    return this.pending(attempt, "ZELLER_AWAITING_TERMINAL");
  }

  /**
   * 恢复中心用的只读查询：主管确认「已付款」之前，以及按主管结论恢复原单之前，先向 Zeller 核实一次原交易。
   *
   * 中文注释：recover 本身就是只读的（只收已发起调用的结果，或按 reference 查询，绝不发起扣款或取消），
   * 所以这里直接复用它。queryVerified 表示这次确实从 Zeller 拿到了可信答复；
   * 终端不在、SDK 忙或查询失败时为 false，调用方按「没查成」处理，不会把它当成「查过了、没有交易」。
   */
  public async queryExistingPayment(
    attempt: PaymentAttempt,
  ): Promise<PaymentProviderResult & Readonly<{ queryVerified: boolean }>> {
    this.lastRecoverVerified = false;
    const result = await this.recover(attempt);
    return { ...result, queryVerified: this.lastRecoverVerified };
  }

  public async recover(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    const reference = this.purchaseReference(attempt);
    if (reference === null) {
      // 没有可查询的 reference：既不能证明付了也不能证明没付，交给主管核实。
      return this.unknown(attempt, "ZELLER_UNKNOWN_REFERENCE_MISSING");
    }
    const expectation: ZellerAttemptExpectation = {
      type: "Purchase",
      amountCents: attempt.amount.cents,
      reference,
    };
    const record = this.inFlight.get(attempt.attemptId) ?? null;
    if (record !== null && record.settled === null) {
      return this.pending(attempt, "ZELLER_AWAITING_TERMINAL");
    }
    if (record === null) {
      // 进程重启过，或这笔不是本进程发起的：只能按 reference 查。查到 0 笔也不能下结论。
      return this.verify(attempt, expectation, "Unknown", null, "ResultUnknown");
    }

    const classification = classifyZellerCall(record.settled!, expectation);
    // 扣款调用自己给出了明确结论（批准 / 拒付 / 没发出），等同于核实过。
    this.lastRecoverVerified =
      classification.decision === "Approved" ||
      classification.decision === "Declined" ||
      classification.decision === "NotSubmitted";
    switch (classification.decision) {
      case "Approved":
        return this.finish(attempt, this.approved(attempt, reference, classification.transaction!));
      case "Declined":
        return this.finish(
          attempt,
          this.result(attempt, "Declined", responseCodeOf(classification.transaction, "ZELLER_DECLINED")),
        );
      case "NotSubmitted":
        return this.finish(
          attempt,
          this.result(attempt, "Declined", `ZELLER_NOT_SUBMITTED_${codeOf(classification.failureKind)}`),
        );
      default:
        return this.verify(attempt, expectation, classification.decision, record, classification.failureKind);
    }
  }

  public async cancel(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    const record = this.inFlight.get(attempt.attemptId) ?? null;
    if (record === null) {
      // 没有可以取消的调用（进程重启过）：不能当成已取消，交给 recover 按 reference 核实。
      return this.unknown(attempt, "ZELLER_CANCEL_NOT_POSSIBLE");
    }
    if (record.settled === null) {
      try {
        record.operation.cancel?.();
      } catch {
        // 取消键没发出去：保持等待，结果仍以 SDK 调用的返回为准。
      }
      // 取消只是「请求取消」：刷卡机可能已经读卡，最终结果由 recover 收。
      return this.pending(attempt, "ZELLER_CANCEL_REQUESTED");
    }
    return this.recover(attempt);
  }

  public async refund(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    // Zeller 原路退款还没有接入；上游在创建退款 attempt 时就会拦下，这里是最后一道失败关闭。
    return this.unknown(attempt, "ZELLER_REFUND_UNSUPPORTED");
  }

  /** 扣款前的配对检查：已配对返回 null，否则返回「请求没有发出」的原因码。占用 SDK 期间别的调用不会进来。 */
  private async pairingBlocker(
    terminal: NonNullable<ReturnType<ZellerTerminalSource["current"]>>,
  ): Promise<string | null> {
    this.sdkCalls += 1;
    try {
      const pairing = mapZellerSdkPairingResponse(await terminal.initialise());
      if (pairing.isPaired) return null;
      return pairing.errorType === "SetupRequired"
        ? "ZELLER_NOT_SUBMITTED_SETUP_REQUIRED"
        : "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE";
    } catch {
      return "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE";
    } finally {
      this.sdkCalls -= 1;
    }
  }

  /** 按 reference 查询并下结论。record 为 null 表示内存里没有这笔调用。 */
  private async verify(
    attempt: PaymentAttempt,
    expectation: ZellerAttemptExpectation,
    pending: ZellerPendingDecision,
    record: InFlightPurchase | null,
    failureKind: ZellerFailureKind,
  ): Promise<PaymentProviderResult> {
    const terminal = this.terminals.current();
    if (terminal === null) {
      // 有 Zeller 的付款要核实但 SDK 还没加载（比如重启后）：先让界面层加载，下次轮询再查。
      this.terminals.request?.();
      return this.hold(attempt, record, "ZELLER_TERMINAL_UNAVAILABLE");
    }
    if (this.sdkCalls > 0) return this.hold(attempt, record, "ZELLER_TERMINAL_BUSY");

    let query: ZellerQueryResult;
    this.sdkCalls += 1;
    try {
      query = mapZellerSdkQueryResponse(
        await terminal.getTransactions({ reference: expectation.reference }),
      );
    } catch {
      query = { succeeded: false, transactions: [], errorType: "Unrecognised" };
    } finally {
      this.sdkCalls -= 1;
    }

    this.lastRecoverVerified = query.succeeded;
    const verification = classifyZellerVerification(pending, query, expectation);
    switch (verification.decision) {
      case "Approved":
        return this.finish(
          attempt,
          this.approved(attempt, expectation.reference, verification.transaction!),
        );
      case "Declined":
        return this.finish(
          attempt,
          this.result(attempt, "Declined", responseCodeOf(verification.transaction, "ZELLER_DECLINED")),
        );
      case "NotPaid": {
        // 刷卡机已经读到卡的，钱可能迟到入账：等够时间、再查到 0 笔才收尾，期间保持等待。
        const settledAtMs = record?.settledAtMs ?? null;
        if (
          record !== null &&
          record.evidence.processingStarted &&
          settledAtMs !== null &&
          this.nowMs() - settledAtMs < ZELLER_NOT_PAID_SETTLE_MS
        ) {
          return this.pending(attempt, "ZELLER_VERIFYING_NOT_PAID");
        }
        return this.finish(
          attempt,
          this.result(attempt, "Cancelled", `ZELLER_NOT_PAID_${codeOf(failureKind)}`),
        );
      }
      default:
        return this.unknown(
          attempt,
          verification.unknownReason === "None"
            ? "ZELLER_UNKNOWN_NOT_FOUND"
            : `ZELLER_UNKNOWN_${codeOf(verification.unknownReason)}`,
        );
    }
  }

  /** 有了最终结论：内存里的调用记录不再需要。 */
  private finish(attempt: PaymentAttempt, result: PaymentProviderResult): PaymentProviderResult {
    this.inFlight.delete(attempt.attemptId);
    return result;
  }

  /** 暂时查不了（终端不在或 SDK 忙）：不改变结论，下次 recover 再查。 */
  private hold(
    attempt: PaymentAttempt,
    record: InFlightPurchase | null,
    code: string,
  ): PaymentProviderResult {
    // 本进程发起、调用已结束的，保持等待让页面继续轮询；其余保持结果未知。
    return record !== null ? this.pending(attempt, code) : this.unknown(attempt, code);
  }

  private approved(
    attempt: PaymentAttempt,
    reference: string,
    transaction: ZellerTransaction,
  ): PaymentProviderResult {
    const transactionUuid = transaction.transactionUuid!.trim().toLowerCase();
    return {
      state: "Approved",
      // txnRef 存发给 Zeller 的 reference，paymentId 存 Zeller 的 transactionUuid；订单引用由这两项组成。
      references: { ...emptyReferences(), txnRef: reference, paymentId: transactionUuid },
      receiptText: transaction.receiptText ?? null,
      responseCode: clip(transaction.responseCode, 32) ?? "ZELLER_APPROVED",
      protectedSyncEvidence: approvedEvidence(attempt, transactionUuid, transaction),
    };
  }

  private notSubmitted(attempt: PaymentAttempt, code: string): PaymentProviderResult {
    return this.result(attempt, "Declined", code);
  }

  private pending(attempt: PaymentAttempt, code: string): PaymentProviderResult {
    return this.result(attempt, "Pending", code);
  }

  private unknown(attempt: PaymentAttempt, code: string): PaymentProviderResult {
    return this.result(attempt, "Unknown", code);
  }

  private result(
    attempt: PaymentAttempt,
    state: PaymentProviderResult["state"],
    responseCode: string,
  ): PaymentProviderResult {
    return {
      state,
      // 原样带回已落库的引用，不新增也不改写：非批准的结果不产生新的引用。
      references: { ...attempt.references },
      receiptText: null,
      responseCode,
    };
  }

  /**
   * 这笔 attempt 发给 Zeller 的 reference。必须就是提交前落库的那一个，并且确实由本 attempt 派生；
   * 对不上（缺失、被改过、是别的 attempt 的）就当作没有 reference。
   */
  private purchaseReference(attempt: PaymentAttempt): string | null {
    if (attempt.provider !== "zeller" || attempt.operation !== "purchase") return null;
    const derived = deriveZellerAttemptReference(attempt);
    return derived !== null && attempt.references.txnRef === derived ? derived : null;
  }
}

/** 与 WPF 的 ZellerAuthorizationMapper 同一张对照表：服务端和退货都按这些字段认 Zeller 交易。 */
function approvedEvidence(
  attempt: PaymentAttempt,
  transactionUuid: string,
  transaction: ZellerTransaction,
): CardSyncEvidenceV1 {
  const bin = transaction.bin?.trim() ?? "";
  return {
    version: 1,
    provider: "zeller",
    operation: "purchase",
    processor: "Zeller",
    txnRef: transactionUuid,
    authCode: clip(transaction.approvalCode, 32),
    cardType: clip(transaction.scheme, 32),
    cardBin: /^\d{1,8}$/u.test(bin) ? Number(bin) : null,
    maskedCardNumber: maskedCardNumber(transaction.panMasked),
    merchantId: clip(transaction.merchantId, 64),
    responseCode: clip(transaction.responseCode, 32),
    responseText: clip(transaction.responseText, 256),
    stan: null,
    bankDateTimeIso: transaction.timestampIso ?? null,
    amountCents: Math.abs(attempt.amount.cents),
    refundReference: null,
  };
}

/**
 * 卡证据对每个字段有长度和字符限制，超出会让整笔批准落成「结果未知」。
 * 这些都是展示用的附加信息，宁可截断或留空，也不能因为它们让一笔已经扣款的交易建不了单。
 */
function clip(value: string | null | undefined, maxLength: number): string | null {
  if (typeof value !== "string") return null;
  const cleaned = value.replace(/[\u0000-\u001f\u007f]/gu, " ").trim();
  return cleaned.length === 0 ? null : cleaned.slice(0, maxLength);
}

/** Zeller 给的是卡号后四位。位数超过 4 位又没有掩码字符的不进证据，避免把完整卡号写进去。 */
function maskedCardNumber(value: string | null | undefined): string | null {
  const cleaned = clip(value, 40);
  if (cleaned === null) return null;
  const digits = cleaned.replace(/\D/gu, "");
  if (digits.length > 10 || (digits.length > 4 && !/[*xX•]/u.test(cleaned))) return null;
  return cleaned;
}

function responseCodeOf(transaction: ZellerTransaction | null, fallback: string): string {
  return clip(transaction?.responseCode, 32) ?? fallback;
}

/** 把分类器的 PascalCase 名字变成响应码里用的大写下划线：TerminalUnavailable → TERMINAL_UNAVAILABLE。 */
function codeOf(name: string): string {
  return name.replace(/([a-z0-9])([A-Z])/gu, "$1_$2").toUpperCase();
}

function emptyReferences(): PaymentProviderReferences {
  return {
    checkoutId: null,
    paymentId: null,
    sessionId: null,
    txnRef: null,
    rfn: null,
    voucherReservationToken: null,
  };
}
