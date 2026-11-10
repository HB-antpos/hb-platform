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

/** Zeller 单笔扣款最低 100 分（1.00 澳元），与 WPF 的 ZellerMinimumPurchaseCents 一致。 */
export const ZELLER_MINIMUM_PURCHASE_CENTS = 100;

/**
 * 「SDK 是否正忙」的闸门，按终端来源共享。
 *
 * 中文注释：销售和分期各有一套通道注册表，也就各有一个适配器实例，但刷卡机和 SDK 只有一份。
 * 闸门如果每个实例各记各的，一边在扣款时另一边还会去查询，SDK 并发时会串结果。
 */
type ZellerSdkGate = { calls: number };
const sdkGates = new WeakMap<ZellerTerminalSource, ZellerSdkGate>();

function sdkGateOf(terminals: ZellerTerminalSource): ZellerSdkGate {
  let gate = sdkGates.get(terminals);
  if (gate === undefined) {
    gate = { calls: 0 };
    sdkGates.set(terminals, gate);
  }
  return gate;
}

/** 一笔还在本进程内存里的 Zeller 扣款或退款调用。进程重启后不存在，只能按 reference 查询。 */
type InFlightCall = {
  readonly operation: ZellerProbeOperation;
  evidence: ZellerCallEvidence;
  /** SDK 调用结束后的转换结果；还没结束时为 null。 */
  settled: ZellerCallResult | null;
  settledAtMs: number | null;
  /** SDK 调用结束（无论成败）后完成，永不 reject。退款要等它，扣款不等。 */
  done: Promise<void>;
};

export type ZellerPaymentAdapterOptions = Readonly<{
  terminals: ZellerTerminalSource;
  /** 测试可注入。 */
  nowMs?: () => number;
}>;

/**
 * Zeller 刷卡机直连的收款适配器：扣款，以及按原交易号把钱退回原卡（原路退款）。
 *
 * 中文注释：扣款与 Square / Linkly 的做法保持同一个节奏，所以收银页不需要为 Zeller 另写流程：
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
 *
 * 退款与扣款有两点不同：
 * 1. refund 要等刷卡机给出结果再返回。退货流程不会轮询等待中的退款，立刻返回 Pending 会让每一笔退款
 *    都落成「结果未知」、要收银员再点一次恢复。
 * 2. 退款是往外打钱，结果未知时更不能重发：恢复只按本次退款的 reference（HBZR-…）查询。
 *    本进程没有这笔调用的记录、又查不到交易时保持结果未知，交给主管核实，不能判成没退。
 * 原交易号（transactionUuid）来自退货额度保险库，在 attempt 落库前写进 references.paymentId，页面碰不到；
 * 这里不支持不带原交易号的退款（WPF 的「无关联退款」默认也是关闭的）。
 */
export class ZellerPaymentAdapter implements OnlinePaymentPort {
  public readonly provider = "zeller" as const;

  private readonly terminals: ZellerTerminalSource;
  private readonly nowMs: () => number;
  private readonly inFlight = new Map<string, InFlightCall>();
  /** 正在占用 SDK 的调用数（扣款、退款或查询）。大于 0 时不再向 SDK 发任何请求；同一终端来源的实例共用。 */
  private readonly gate: ZellerSdkGate;
  /** 最近一次 recover 是否真的从 Zeller 拿到了可信的答复（供 queryExistingPayment 报告）。 */
  private lastRecoverVerified = false;

  public constructor(options: ZellerPaymentAdapterOptions) {
    this.terminals = options.terminals;
    this.gate = sdkGateOf(options.terminals);
    this.nowMs = options.nowMs ?? (() => Date.now());
  }

  public async submit(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    const reference = this.attemptReference(attempt);
    // 以下几种情况请求都没有发给 Zeller，可以放心按未付款收尾。
    if (
      reference === null ||
      attempt.operation !== "purchase" ||
      attempt.amount.cents <= 0 ||
      attempt.amount.currency !== "AUD"
    ) {
      return this.notSubmitted(attempt, "ZELLER_NOT_SUBMITTED_INVALID_ATTEMPT");
    }
    // Zeller 不收低于 1.00 澳元的款。页面应该先拦住，这里是最后一道：不向刷卡机发请求。
    if (attempt.amount.cents < ZELLER_MINIMUM_PURCHASE_CENTS) {
      return this.notSubmitted(attempt, "ZELLER_NOT_SUBMITTED_BELOW_MINIMUM");
    }
    const amountCents = attempt.amount.cents;
    // 中文注释：设置里打开「收款时不弹出 Zeller 窗口」才带 unattended；没打开时请求与以前完全一致。
    // 隐藏窗口后收银员看付款页自己的「等待支付终端」和「取消支付」；退款没有这个选项，配对必须弹窗。
    const hideSdkUi = this.terminals.hideSdkUi?.() === true;
    const started = await this.start(attempt, (terminal) =>
      terminal.purchase({
        amount: amountCents,
        reference,
        ...(hideSdkUi ? { unattended: true } : {}),
      }),
    );
    // 已经发起过的只收结果；没发出去的按「没有发出」收尾。
    if (started.kind === "existing") return this.recover(attempt);
    if (started.kind === "blocked") return this.notSubmitted(attempt, started.code);
    return this.pending(attempt, "ZELLER_AWAITING_TERMINAL");
  }

  /**
   * 扣款并等刷卡机给出结果再返回，给不会轮询等待中付款的流程用（分期首付、续付）。
   *
   * 中文注释：分期页提交后不会自动来收结果。立刻返回 Pending 的话，页面直接进入「需要恢复」，
   * 中央的续付占用也会被标成结果未知，而顾客可能还在刷卡。等到有结论再返回，真的不明才是未知。
   * 等待期间进程被杀也没关系：reference 在发起前已经落库，重启后按 reference 查询。
   */
  public async submitAndSettle(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    const submitted = await this.submit(attempt);
    const record = this.inFlight.get(attempt.attemptId);
    if (submitted.state !== "Pending" || record === undefined) return submitted;
    await record.done;
    return this.recover(attempt);
  }

  /**
   * 把钱退回原交易的那张卡。原交易号取自 references.paymentId（由退货额度保险库在落库前写入）。
   * 与扣款不同，这里等刷卡机给出结果再返回；返回的结论由 recover 统一给出。
   */
  public async refund(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    const reference = this.attemptReference(attempt);
    const originalTransactionUuid =
      attempt.operation === "refund" ? transactionUuidOf(attempt.references.paymentId) : null;
    // PaymentAttempt 是账本模型，退款金额是负数；发给 Zeller 的是正的退款额。
    const amountCents = -attempt.amount.cents;
    if (
      reference === null ||
      originalTransactionUuid === null ||
      !Number.isSafeInteger(amountCents) ||
      amountCents <= 0 ||
      attempt.amount.currency !== "AUD"
    ) {
      return this.notSubmitted(attempt, "ZELLER_NOT_SUBMITTED_INVALID_ATTEMPT");
    }
    const started = await this.start(attempt, (terminal) =>
      terminal.refund({ amount: amountCents, purchase: originalTransactionUuid, reference }),
    );
    if (started.kind === "blocked") return this.notSubmitted(attempt, started.code);
    // 这笔已经发起过的（重复调用）绝不再发第二次，只等它结束。
    await started.record.done;
    return this.recover(attempt);
  }

  /**
   * 向刷卡机发起一笔扣款或退款，并把这次调用记在内存里。三种结果：
   * existing——这笔 attempt 已经发起过，绝不再发第二次；blocked——请求没有发出（附原因码）；
   * started——已交给 SDK，结果稍后落在 record 上。
   */
  private async start(
    attempt: PaymentAttempt,
    call: (terminal: ZellerTerminal) => ZellerProbeOperation,
  ): Promise<
    | Readonly<{ kind: "existing"; record: InFlightCall }>
    | Readonly<{ kind: "blocked"; code: string }>
    | Readonly<{ kind: "started"; record: InFlightCall }>
  > {
    const existing = this.inFlight.get(attempt.attemptId);
    if (existing !== undefined) return { kind: "existing", record: existing };
    const terminal = this.terminals.current();
    if (terminal === null) {
      // 让界面层去加载 SDK，下一次发起时就有终端了；这一次请求没有发出。
      this.terminals.request?.();
      return { kind: "blocked", code: "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE" };
    }
    if (this.gate.calls > 0) return { kind: "blocked", code: "ZELLER_NOT_SUBMITTED_TERMINAL_BUSY" };

    // 中文注释：发请求之前先确认这台 iPad 还配着对（不弹窗、不涉及金额）。
    // 不做这一步的话，没配对时调用会返回「需要设置」，随后按 reference 的核实查询同样因为没配对而失败，
    // 这笔只能落成「结果未知」交给主管——而实际上请求根本没有发出。在这里拦下就能干净地按「没有发出」收尾。
    const pairingBlocker = await this.pairingBlocker(terminal);
    if (pairingBlocker !== null) return { kind: "blocked", code: pairingBlocker };
    // 检查配对期间可能有别的调用进来（比如另一单的核实查询），再确认一次没有人占着 SDK，也没有重复发起。
    const raced = this.inFlight.get(attempt.attemptId);
    if (raced !== undefined) return { kind: "existing", record: raced };
    if (this.gate.calls > 0) return { kind: "blocked", code: "ZELLER_NOT_SUBMITTED_TERMINAL_BUSY" };

    let operation: ZellerProbeOperation;
    try {
      operation = call(terminal);
    } catch {
      // 调用还没进入 SDK 就抛了：请求确定没有发出。
      return { kind: "blocked", code: "ZELLER_NOT_SUBMITTED_SDK_UNAVAILABLE" };
    }
    const settle = (result: ZellerCallResult) => {
      record.settled = result;
      record.settledAtMs = this.nowMs();
      this.gate.calls -= 1;
    };
    const record: InFlightCall = {
      operation,
      evidence: ZELLER_NO_CALL_EVIDENCE,
      settled: null,
      settledAtMs: null,
      done: Promise.resolve(operation).then(
        (raw) => settle(mapZellerSdkCallResponse(raw, record.evidence)),
        (error: unknown) => settle(mapZellerSdkCallException(error, record.evidence)),
      ),
    };
    this.inFlight.set(attempt.attemptId, record);
    this.gate.calls += 1;
    try {
      operation.onEvent?.((event) => {
        record.evidence = accumulateZellerCallEvidence(record.evidence, event);
      });
    } catch {
      // 订阅事件失败不影响调用本身；只是少了「请求是否到过刷卡机」的证据，分类会更保守。
    }
    return { kind: "started", record };
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
    const reference = this.attemptReference(attempt);
    if (reference === null) {
      // 没有可查询的 reference：既不能证明付了也不能证明没付，交给主管核实。
      return this.unknown(attempt, "ZELLER_UNKNOWN_REFERENCE_MISSING");
    }
    // 退款 attempt 的金额是负数，Zeller 交易上的金额总是正数。
    const expectation: ZellerAttemptExpectation = {
      type: attempt.operation === "refund" ? "Refund" : "Purchase",
      amountCents: Math.abs(attempt.amount.cents),
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
    // 调用自己给出了明确结论（批准 / 拒绝 / 没发出），等同于核实过。
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

  /** 发起前的配对检查：已配对返回 null，否则返回「请求没有发出」的原因码。占用 SDK 期间别的调用不会进来。 */
  private async pairingBlocker(terminal: ZellerTerminal): Promise<string | null> {
    this.gate.calls += 1;
    try {
      const pairing = mapZellerSdkPairingResponse(await terminal.initialise());
      if (pairing.isPaired) return null;
      return pairing.errorType === "SetupRequired"
        ? "ZELLER_NOT_SUBMITTED_SETUP_REQUIRED"
        : "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE";
    } catch {
      return "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE";
    } finally {
      this.gate.calls -= 1;
    }
  }

  /** 按 reference 查询并下结论。record 为 null 表示内存里没有这笔调用。 */
  private async verify(
    attempt: PaymentAttempt,
    expectation: ZellerAttemptExpectation,
    pending: ZellerPendingDecision,
    record: InFlightCall | null,
    failureKind: ZellerFailureKind,
  ): Promise<PaymentProviderResult> {
    const terminal = this.terminals.current();
    if (terminal === null) {
      // 有 Zeller 的付款要核实但 SDK 还没加载（比如重启后）：先让界面层加载，下次轮询再查。
      this.terminals.request?.();
      return this.hold(attempt, record, "ZELLER_TERMINAL_UNAVAILABLE");
    }
    if (this.gate.calls > 0) return this.hold(attempt, record, "ZELLER_TERMINAL_BUSY");

    let query: ZellerQueryResult;
    this.gate.calls += 1;
    try {
      query = mapZellerSdkQueryResponse(
        await terminal.getTransactions({ reference: expectation.reference }),
      );
    } catch {
      query = { succeeded: false, transactions: [], errorType: "Unrecognised" };
    } finally {
      this.gate.calls -= 1;
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
    record: InFlightCall | null,
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
      references:
        attempt.operation === "refund"
          ? // 退款：引用原样不动。paymentId 是原交易号，把退款自己的交易号写进去会被判成引用冲突，
            // 钱退了单子却卡在结果未知。退款自己的交易号只放进下面的卡证据（txnRef）。
            { ...attempt.references }
          : // 扣款：txnRef 存发给 Zeller 的 reference，paymentId 存 Zeller 的 transactionUuid；订单引用由这两项组成。
            { ...emptyReferences(), txnRef: reference, paymentId: transactionUuid },
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
   * 这笔 attempt 发给 Zeller 的 reference（扣款 HBZ-…，退款 HBZR-…）。必须就是提交前落库的那一个，
   * 并且确实由本 attempt 派生；对不上（缺失、被改过、是别的 attempt 的）就当作没有 reference。
   */
  private attemptReference(attempt: PaymentAttempt): string | null {
    if (attempt.provider !== "zeller") return null;
    const derived = deriveZellerAttemptReference(attempt);
    return derived !== null && attempt.references.txnRef === derived ? derived : null;
  }
}

type ZellerTerminal = NonNullable<ReturnType<ZellerTerminalSource["current"]>>;

/** 原交易号必须是标准格式的 GUID；不是就当作没有，退款不发出。 */
function transactionUuidOf(value: string | null): string | null {
  const normalized = value?.trim().toLowerCase() ?? "";
  return /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/u.test(normalized) ? normalized : null;
}

/**
 * 与 WPF 的 ZellerAuthorizationMapper 同一张对照表：服务端和退货都按这些字段认 Zeller 交易。
 * 退款与扣款的填法相同：txnRef 是这一笔交易（退款时即退款交易）自己的 transactionUuid，金额为正数。
 */
function approvedEvidence(
  attempt: PaymentAttempt,
  transactionUuid: string,
  transaction: ZellerTransaction,
): CardSyncEvidenceV1 {
  const bin = transaction.bin?.trim() ?? "";
  return {
    version: 1,
    provider: "zeller",
    operation: attempt.operation,
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
