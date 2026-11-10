import {
  ZELLER_ERROR_TYPE_CODES,
  type ZellerCallResult,
  type ZellerErrorType,
  type ZellerKnownErrorType,
  type ZellerQueryResult,
  type ZellerTransaction,
  type ZellerTransactionStatus,
  type ZellerTransactionType,
} from "./zeller-terminal-models";

/**
 * 把 Zeller React Native SDK（@zeller-public/payments-sdk-react-native，按 0.2.5 的类型声明核对）
 * 的返回值转换成我们自己的类型。对应 WPF 的 Sdk/ZellerSdkMapper.cs。
 * 只做转换，不做判断：被拒算不算失败、错误怎么处理，都交给 zeller-result-classifier。
 *
 * 中文注释：
 * 1. 这里不引用 SDK 包，入参一律当作 unknown 逐项检查。SDK 升级后多出字段或换了取值，
 *    不会让这里编译失败，而是落到「结果未知」一侧。
 * 2. SDK 返回了本程序不认识的东西时，一律转成「结果未知」一侧：扣款、退款转成 Faulted，
 *    查询转成失败，交易状态转成 Unknown。上层会按 reference 核实，不会把它当成没扣款。
 * 3. 与 .NET SDK 的差别：RN SDK 的错误不是数值枚举，而是带空格的字符串（如 "Operation Busy"），
 *    放在返回的 Error 对象的 type 上；并且没有 FeatureNotAvailable / PermissionsMissing 这两种。
 */

/** 调用期间收到的事件证据，由适配层在 onEvent 回调里累积。 */
export type ZellerCallEvidence = Readonly<{
  reachedTerminal: boolean;
  processingStarted: boolean;
}>;

export const ZELLER_NO_CALL_EVIDENCE: ZellerCallEvidence = {
  reachedTerminal: false,
  processingStarted: false,
};

const KNOWN_ERROR_TYPES: ReadonlySet<string> = new Set(Object.keys(ZELLER_ERROR_TYPE_CODES));

/** 把 RN SDK 的错误类型字符串（"Operation Busy"）转成我们的错误类型；不认识的一律 Unrecognised。 */
export function zellerErrorTypeFromSdkName(type: unknown): ZellerErrorType {
  if (typeof type !== "string") return "Unrecognised";
  const compact = type.replace(/\s+/gu, "");
  return KNOWN_ERROR_TYPES.has(compact) ? (compact as ZellerKnownErrorType) : "Unrecognised";
}

/**
 * 把 SDK 推来的事件并入已有证据。
 * AWAITING_PAYMENT_METHOD：刷卡机已提示刷卡，请求已经到达刷卡机。
 * PROCESSING：刷卡机已读到卡，从这一刻起钱可能已经扣了（此时请求必然已到刷卡机）。
 */
export function accumulateZellerCallEvidence(
  evidence: ZellerCallEvidence,
  event: unknown,
): ZellerCallEvidence {
  const name = isRecord(event) ? event["event"] : undefined;
  if (name === "PROCESSING") return { reachedTerminal: true, processingStarted: true };
  if (name === "AWAITING_PAYMENT_METHOD") return { ...evidence, reachedTerminal: true };
  return evidence;
}

/** 转换 SDK 的一笔交易；连金额都读不出来时返回 null，由调用方按「结果未知」处理。 */
export function mapZellerSdkTransaction(raw: unknown): ZellerTransaction | null {
  if (!isRecord(raw)) return null;
  const amount = raw["amount"];
  if (typeof amount !== "number" || !Number.isSafeInteger(amount)) return null;
  return {
    status: toStatus(raw["status"]),
    type: toType(raw["type"]),
    amountCents: amount,
    transactionUuid: textOrNull(raw["transactionUuid"]),
    externalReference: textOrNull(raw["externalReference"]),
    responseCode: textOrNull(raw["responseCode"]),
    responseText: textOrNull(raw["responseText"]),
    approvalCode: textOrNull(raw["approvalCode"]),
    rrn: textOrNull(raw["rrn"]),
    panMasked: textOrNull(raw["panMasked"]),
    scheme: textOrNull(raw["scheme"]),
    bin: textOrNull(raw["bin"]),
    // 中文注释：caid / catid 是收单的商户号（Card Acceptor ID）和终端号（Card Acceptor Terminal ID）。
    merchantId: textOrNull(raw["caid"]),
    terminalId: textOrNull(raw["catid"]),
    receiptText: textOrNull(raw["receiptText"]),
    receiptLink: textOrNull(raw["receiptLink"]),
    timestampIso: normalizeZellerTimestamp(raw["timestampLocal"]),
  };
}

/** 转换 purchase() / refund() 的返回值。SDK 用「返回 Error 对象」表示失败，不抛异常。 */
export function mapZellerSdkCallResponse(
  response: unknown,
  evidence: ZellerCallEvidence = ZELLER_NO_CALL_EVIDENCE,
): ZellerCallResult {
  if (response instanceof Error) {
    return {
      outcome: "Error",
      errorType: zellerErrorTypeFromSdkName((response as { type?: unknown }).type),
      message: textOrNull(response.message),
      ...evidence,
    };
  }
  const transaction = mapZellerSdkTransaction(response);
  if (transaction === null) {
    return {
      outcome: "Faulted",
      message: describeUnexpected(response),
      ...evidence,
    };
  }
  // 返回了交易：请求必然到过刷卡机并且处理过，与 WPF 的 FromTransaction 默认值一致。
  return { outcome: "Transaction", transaction, reachedTerminal: true, processingStarted: true };
}

/** SDK 调用本身抛了异常（SDK 约定不抛，抛了说明内部状态不可信）。 */
export function mapZellerSdkCallException(
  error: unknown,
  evidence: ZellerCallEvidence = ZELLER_NO_CALL_EVIDENCE,
): ZellerCallResult {
  return {
    outcome: "Faulted",
    message: error instanceof Error ? textOrNull(error.message) : describeUnexpected(error),
    ...evidence,
  };
}

/** 转换 getTransactions() 的返回值。有任何一笔读不懂，整次查询都算失败：不能在缺证据的情况下下结论。 */
export function mapZellerSdkQueryResponse(response: unknown): ZellerQueryResult {
  if (response instanceof Error) {
    return {
      succeeded: false,
      transactions: [],
      errorType: zellerErrorTypeFromSdkName((response as { type?: unknown }).type),
      message: textOrNull(response.message),
    };
  }
  if (!Array.isArray(response)) {
    return failedQuery(describeUnexpected(response));
  }
  const transactions: ZellerTransaction[] = [];
  for (const item of response as readonly unknown[]) {
    const transaction = mapZellerSdkTransaction(item);
    if (transaction === null) {
      return failedQuery("the Zeller SDK returned a transaction that could not be read");
    }
    transactions.push(transaction);
  }
  return { succeeded: true, transactions };
}

/** 转换 initialise() / setup() 的返回值：只有严格的 true 才算已配对。 */
export function mapZellerSdkPairingResponse(
  response: unknown,
): Readonly<{ isPaired: boolean; errorType: ZellerErrorType | null; message: string | null }> {
  if (response === true) return { isPaired: true, errorType: null, message: null };
  if (response instanceof Error) {
    return {
      isPaired: false,
      errorType: zellerErrorTypeFromSdkName((response as { type?: unknown }).type),
      message: textOrNull(response.message),
    };
  }
  return { isPaired: false, errorType: "Unrecognised", message: describeUnexpected(response) };
}

/**
 * 刷卡机给的时间。实测有两种写法：UTC 带 Z 和带时区偏移；没有时区的按 UTC 处理（与 WPF 一致，
 * 不能交给 Date.parse 按本机时区猜）。只用于显示和排序，解析不了就留空。
 */
export function normalizeZellerTimestamp(value: unknown): string | null {
  if (typeof value !== "string" || value.trim() === "") return null;
  const trimmed = value.trim();
  const withZone = /(?:z|[+-]\d{2}:?\d{2})$/iu.test(trimmed) ? trimmed : `${trimmed}Z`;
  return Number.isNaN(Date.parse(withZone)) ? null : withZone;
}

function toStatus(status: unknown): ZellerTransactionStatus {
  switch (status) {
    case "APPROVED":
      return "Approved";
    case "DECLINED":
      return "Declined";
    case "FAILED":
      return "Failed";
    default:
      return "Unknown";
  }
}

function toType(type: unknown): ZellerTransactionType {
  switch (type) {
    case "PURCHASE":
      return "Purchase";
    case "REFUND":
      return "Refund";
    default:
      return "Unknown";
  }
}

function failedQuery(message: string): ZellerQueryResult {
  return { succeeded: false, transactions: [], errorType: "Unrecognised", message };
}

function describeUnexpected(response: unknown): string {
  if (response === null || response === undefined) return "the Zeller SDK returned no response";
  return `the Zeller SDK returned an unexpected response of type ${Array.isArray(response) ? "array" : typeof response}`;
}

function textOrNull(value: unknown): string | null {
  return typeof value === "string" && value.trim() !== "" ? value : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
