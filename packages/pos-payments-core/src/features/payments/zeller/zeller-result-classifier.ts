import type {
  ZellerCallResult,
  ZellerErrorType,
  ZellerQueryResult,
  ZellerTransaction,
  ZellerTransactionType,
} from "./zeller-terminal-models";

/**
 * 把 SDK 的返回和按 reference 查询的结果变成本地结论。纯函数，不访问网络和数据库。
 * 与 apps/pos-wpf/src/Hbpos.Client.Wpf/Services/Zeller/ZellerResultClassifier.cs 同一套规则，
 * 两边共用 test-fixtures/zeller/result-classifier-vectors.json 的黄金向量。
 *
 * 中文注释：规则依据是 2026-11-05 的两轮真机验证。原则：
 * 只有 status=Approved 且金额、reference 都对得上才算批准；只有明确的拒绝，
 * 或者明确的取消、超时并且按 reference 查到 0 笔，才算未付款；其余一律结果未知。
 * 判定只看 status 和错误类型，不看 responseText（同一笔交易在返回和查询里文案不同），也不看时间戳。
 */

/**
 * 一次扣款或退款调用结束后，下一步该怎么办。
 * - Approved：批准，金额、reference 都对得上，可以建单（退款：退款已完成）。
 * - Declined：银行明确拒绝，未付款。
 * - NotSubmitted：请求确定没有发出，未付款，不需要查询。
 * - VerifyThenNotPaid：取消、超时、刷卡机没连上。立即按 reference 查一次，查到 0 笔才算未付款。
 * - VerifyThenDeclined：卡片类错误。立即按 reference 查一次确认，再按拒付处理。
 * - Unknown：结果未知。自动重查，查不清就进恢复中心。绝不重新发起扣款。
 */
export type ZellerCallDecision =
  | "Approved"
  | "Declined"
  | "NotSubmitted"
  | "VerifyThenNotPaid"
  | "VerifyThenDeclined"
  | "Unknown";

/** 给付款页选提示文案用的失败种类。 */
export type ZellerFailureKind =
  | "None"
  | "BankDeclined"
  | "CardProblem"
  | "Cancelled"
  | "Timeout"
  | "TerminalUnavailable"
  | "SetupRequired"
  | "InvalidRequest"
  | "Busy"
  /** Zeller SDK 本身起不来，请求没有发出。 */
  | "SdkUnavailable"
  | "OriginalTransactionNotFound"
  | "ResultUnknown";

/** 结果未知的原因，落库后供恢复中心和排查使用。 */
export type ZellerUnknownReason =
  | "None"
  | "SdkError"
  | "WatchdogTimeout"
  | "SdkException"
  | "ReferenceMismatch"
  | "AmountMismatch"
  | "MissingTransactionUuid"
  | "UnrecognisedStatus"
  | "TerminalConnectionLost"
  | "MultipleApproved"
  | "LateApproval"
  | "VerificationFailed"
  | "ProcessExited"
  | "RefundAlreadyInProgress";

/** 本地 attempt 对这次调用的预期：类型、金额（分）和发给 Zeller 的 reference。 */
export type ZellerAttemptExpectation = Readonly<{
  type: ZellerTransactionType;
  amountCents: number;
  reference: string;
}>;

export type ZellerCallClassification = Readonly<{
  decision: ZellerCallDecision;
  failureKind: ZellerFailureKind;
  unknownReason: ZellerUnknownReason;
  /** SDK 内部状态不可信：先重建 Terminal 再查询。 */
  rebuildTerminalFirst: boolean;
  /** 我们的串行门有漏洞，当时进行中的那一笔结果已经丢失，调用方要按故障处理。 */
  serialGateBreached: boolean;
  transaction: ZellerTransaction | null;
}>;

/**
 * 按 reference 查询之后的结论。
 * KeepUnknown：仍然不能下结论。unknownReason 为 None 表示只是还没查到，可以继续重查。
 */
export type ZellerVerificationDecision = "Approved" | "Declined" | "NotPaid" | "KeepUnknown";

export type ZellerVerificationClassification = Readonly<{
  decision: ZellerVerificationDecision;
  unknownReason: ZellerUnknownReason;
  transaction: ZellerTransaction | null;
}>;

/** 需要按 reference 查询才能定论的三种决定。 */
export type ZellerPendingDecision = Extract<
  ZellerCallDecision,
  "VerifyThenNotPaid" | "VerifyThenDeclined" | "Unknown"
>;

/** 扣款或退款调用刚结束时的分类。 */
export function classifyZellerCall(
  result: ZellerCallResult,
  expected: ZellerAttemptExpectation,
): ZellerCallClassification {
  switch (result.outcome) {
    case "Transaction":
      return classifyReturnedTransaction(result.transaction ?? null, expected);
    case "Error":
      return classifyError(
        result.errorType ?? "Unrecognised",
        result.reachedTerminal === true,
        expected.type,
      );
    // 看门狗放弃的调用和抛异常的调用，SDK 内部状态都不可信：先重建 Terminal 再查询。
    case "Abandoned":
      return unknown("WatchdogTimeout", true);
    case "Faulted":
      return unknown("SdkException", true);
    // 请求没有进入 SDK：要么排队时被取消，要么创建 Terminal 或发起调用时就失败了。
    case "NotStarted":
      return call("NotSubmitted", result.errorType === "Cancelled" ? "Cancelled" : "SdkUnavailable");
    default:
      return unknown("SdkError");
  }
}

/** 按 reference 查询之后的分类。pending 必须是需要查询才能定论的三种决定之一。 */
export function classifyZellerVerification(
  pending: ZellerCallDecision,
  query: ZellerQueryResult,
  expected: ZellerAttemptExpectation,
): ZellerVerificationClassification {
  if (pending !== "VerifyThenNotPaid" && pending !== "VerifyThenDeclined" && pending !== "Unknown") {
    throw new Error("ZELLER_VERIFICATION_DECISION_NOT_PENDING");
  }

  // 查询本身失败时，「没有交易」不是结论。
  if (!query.succeeded) return keepUnknown("VerificationFailed");

  const sameReference = query.transactions.filter((transaction) =>
    referenceMatches(transaction.externalReference, expected.reference),
  );
  if (
    sameReference.some(
      (transaction) => transaction.type === "Unknown" || transaction.status === "Unknown",
    )
  ) {
    return keepUnknown("UnrecognisedStatus");
  }

  const relevant = sameReference.filter((transaction) => transaction.type === expected.type);
  const approved = relevant.filter((transaction) => transaction.status === "Approved");

  // 同一个 reference 下出现多笔批准（Zeller 不去重），只能由主管处理。被拒的笔数不算。
  // 中文注释：把最早的那笔带出来作为证据落库：顾客确实被扣过款，主管不能再「确认未付款」。
  if (approved.length > 1) {
    return keepUnknown("MultipleApproved", earliest(approved));
  }

  const onlyApproved = approved[0];
  if (onlyApproved !== undefined) {
    if (onlyApproved.amountCents !== expected.amountCents) {
      return keepUnknown("AmountMismatch", onlyApproved);
    }
    return isBlank(onlyApproved.transactionUuid)
      ? keepUnknown("MissingTransactionUuid", onlyApproved)
      : { decision: "Approved", unknownReason: "None", transaction: onlyApproved };
  }

  // 取最后一笔被拒的（不用 findLast：共享包的编译目标是 ES2022）。
  const declined = [...relevant]
    .reverse()
    .find((transaction) => transaction.status === "Declined" || transaction.status === "Failed");
  if (declined !== undefined) {
    return { decision: "Declined", unknownReason: "None", transaction: declined };
  }

  // 查到 0 笔：只有 SDK 已经明确说未付款的那两类才能收尾，结果未知的继续保持未知。
  switch (pending) {
    case "VerifyThenNotPaid":
      return { decision: "NotPaid", unknownReason: "None", transaction: null };
    case "VerifyThenDeclined":
      return { decision: "Declined", unknownReason: "None", transaction: null };
    default:
      return keepUnknown("None");
  }
}

/**
 * 按未付款收尾约 60 秒后的复核。查到批准说明顾客其实已被扣款：返回 KeepUnknown，
 * 调用方要把记录交给恢复中心。查询失败时维持未付款，unknownReason 标成 VerificationFailed 供调用方决定是否再查。
 */
export function classifyZellerLateRecheck(
  query: ZellerQueryResult,
  expected: ZellerAttemptExpectation,
): ZellerVerificationClassification {
  const verification = classifyZellerVerification("VerifyThenNotPaid", query, expected);
  if (verification.decision === "Approved") {
    return keepUnknown("LateApproval", verification.transaction);
  }
  if (
    verification.decision === "KeepUnknown" &&
    verification.unknownReason === "VerificationFailed"
  ) {
    return { decision: "NotPaid", unknownReason: "VerificationFailed", transaction: null };
  }
  return verification;
}

function classifyReturnedTransaction(
  transaction: ZellerTransaction | null,
  expected: ZellerAttemptExpectation,
): ZellerCallClassification {
  if (transaction === null) return unknown("SdkError");

  // 返回的交易不是这次尝试的：SDK 可能把别的调用的结果交给了我们（并发串线时出现过），重建后再查。
  if (
    !referenceMatches(transaction.externalReference, expected.reference) ||
    transaction.type !== expected.type
  ) {
    return unknown("ReferenceMismatch", true, transaction);
  }

  switch (transaction.status) {
    case "Approved":
      if (transaction.amountCents !== expected.amountCents) {
        return unknown("AmountMismatch", false, transaction);
      }
      return isBlank(transaction.transactionUuid)
        ? unknown("MissingTransactionUuid", false, transaction)
        : call("Approved", "None", { transaction });
    // 被拒的交易也是「成交类响应」，必须看 status。
    case "Declined":
    case "Failed":
      return call("Declined", "BankDeclined", { transaction });
    default:
      return unknown("UnrecognisedStatus", false, transaction);
  }
}

function classifyError(
  errorType: ZellerErrorType,
  reachedTerminal: boolean,
  operation: ZellerTransactionType,
): ZellerCallClassification {
  switch (errorType) {
    // 本地参数校验，0 毫秒返回，请求没有发出。
    case "OperationRequestInvalid":
      return call("NotSubmitted", "InvalidRequest");

    // 这一笔没有发出。但它说明我们的串行门有漏洞，而且当时进行中的那一笔结果已经丢失，调用方要按故障处理。
    case "OperationBusy":
      return call("NotSubmitted", "Busy", { rebuildTerminalFirst: true, serialGateBreached: true });

    case "SetupRequired":
    case "FeatureNotAvailable":
    case "PermissionsMissing":
      return call("VerifyThenNotPaid", "SetupRequired");

    case "Cancelled":
      return call("VerifyThenNotPaid", "Cancelled");

    case "InactivityTimeout":
      return call("VerifyThenNotPaid", "Timeout");

    // 刷卡机连不上：请求没到刷卡机时可以查一次就收尾；已经到了刷卡机，就不能排除它后来完成了扣款。
    case "ConnectionFailure":
    case "PairedDeviceBusy":
    case "PairedDeviceNotAvailable":
      return reachedTerminal
        ? unknown("TerminalConnectionLost")
        : call("VerifyThenNotPaid", "TerminalUnavailable");

    case "CardDeclined":
    case "TransactionDeclined":
      return call("VerifyThenDeclined", "BankDeclined");

    case "CardBlocked":
    case "CardNotSupported":
    case "CardReadFailure":
    case "CardInsertRequired":
      return call("VerifyThenDeclined", "CardProblem");

    // 退款时原交易不存在：退款没有发生，不重试。扣款不该出现这个错误，出现了按未知处理。
    case "TransactionNotFound":
      return operation === "Refund"
        ? call("NotSubmitted", "OriginalTransactionNotFound")
        : unknown("SdkError");

    // 收到了别的调用的响应（并发串线时出现过）：SDK 内部状态不可信，先重建。
    case "OperationResponseInvalid":
      return unknown("SdkError", true);

    // NetworkFailure 不代表没扣款：实测一次已扣、一次没扣。其余几种没有观察到，按保守的一侧处理。
    default:
      return unknown("SdkError");
  }
}

/** 时间戳最早的一笔；没有时间戳或解析不了的排在最后，并列时保持原顺序（与 C# 的稳定排序一致）。 */
function earliest(transactions: readonly ZellerTransaction[]): ZellerTransaction | null {
  let best: ZellerTransaction | null = null;
  let bestTime = Number.POSITIVE_INFINITY;
  for (const transaction of transactions) {
    const time = timestampMillis(transaction.timestampIso);
    if (best === null || time < bestTime) {
      best = transaction;
      bestTime = time;
    }
  }
  return best;
}

function timestampMillis(timestampIso: string | null | undefined): number {
  if (typeof timestampIso !== "string") return Number.POSITIVE_INFINITY;
  const parsed = Date.parse(timestampIso);
  return Number.isNaN(parsed) ? Number.POSITIVE_INFINITY : parsed;
}

function referenceMatches(actual: string | null | undefined, expected: string): boolean {
  return typeof actual === "string" && actual.trim() === expected;
}

function isBlank(value: string | null | undefined): boolean {
  return typeof value !== "string" || value.trim() === "";
}

function call(
  decision: ZellerCallDecision,
  failureKind: ZellerFailureKind,
  extra: Partial<
    Pick<ZellerCallClassification, "rebuildTerminalFirst" | "serialGateBreached" | "transaction">
  > = {},
): ZellerCallClassification {
  return {
    decision,
    failureKind,
    unknownReason: "None",
    rebuildTerminalFirst: extra.rebuildTerminalFirst ?? false,
    serialGateBreached: extra.serialGateBreached ?? false,
    transaction: extra.transaction ?? null,
  };
}

function unknown(
  reason: ZellerUnknownReason,
  rebuildTerminalFirst = false,
  transaction: ZellerTransaction | null = null,
): ZellerCallClassification {
  return {
    decision: "Unknown",
    failureKind: "ResultUnknown",
    unknownReason: reason,
    rebuildTerminalFirst,
    serialGateBreached: false,
    transaction,
  };
}

function keepUnknown(
  reason: ZellerUnknownReason,
  transaction: ZellerTransaction | null = null,
): ZellerVerificationClassification {
  return { decision: "KeepUnknown", unknownReason: reason, transaction };
}
