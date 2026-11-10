/**
 * Zeller 直连刷卡的引用格式，与 apps/pos-wpf/src/Hbpos.Contracts/Orders/ZellerPaymentReference.cs 同一语义。
 *
 * 中文注释：两类引用分开。
 * 1. 发给 Zeller 的 reference（HBZ-… / HBZR-…）由本地 attempt 的 GUID 派生，付款前先落库，是崩溃后找回交易的唯一线索。
 *    Zeller 不对 reference 去重：同一个 reference 再次扣款会再扣一次，所以每次尝试必须用新的 attempt。
 * 2. 订单付款记录里的引用（ZELLER:… / ZELLERRF:…）同时带上 reference 和 Zeller 的 transactionUuid。
 *    一个 reference 可能对应多笔交易，退款必须用 transactionUuid 才能唯一指向原交易。
 *
 * 服务端（Hbpos.Api）按 C# 版解析这些引用，两边必须得到相同结果；
 * 跨语言向量见 test-fixtures/zeller/payment-reference-vectors.json（C# 与 TS 共用）。
 *
 * 与 C# 的已知差别：GUID 只接受 D（带连字符）、N（32 位十六进制）以及外包花括号或圆括号的写法，
 * 不接受 .NET 的 X 写法（{0x…}）。Zeller 和我们自己都不会产生 X 写法。
 * 另外，退款引用包装（CARD_REFUND|…）的拆解留到接入 Zeller 退款时再移植。
 */
export const ZELLER_PROCESSOR = "Zeller";
export const ZELLER_PAYMENT_PREFIX = "ZELLER:";
export const ZELLER_REFUND_PREFIX = "ZELLERRF:";
export const ZELLER_PURCHASE_REFERENCE_PREFIX = "HBZ-";
export const ZELLER_REFUND_REFERENCE_PREFIX = "HBZR-";

/** 订单里的 Zeller 引用拆出的两部分：发给 Zeller 的 reference，和 Zeller 返回的 transactionUuid。 */
export type ZellerPaymentReferenceParts = Readonly<{
  reference: string;
  transactionUuid: string;
}>;

const GUID_D = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u;
const GUID_N = /^[0-9a-f]{32}$/u;
const NIL_GUID_N = "0".repeat(32);

/** 扣款时发给 Zeller 的 reference：HBZ- 加 32 位小写十六进制，共 36 个字符，不含个人信息。 */
export function createZellerPurchaseReference(attemptGuid: string): string {
  return `${ZELLER_PURCHASE_REFERENCE_PREFIX}${requireAttempt(attemptGuid)}`;
}

/** 退款时发给 Zeller 的 reference：HBZR- 加 32 位小写十六进制，共 37 个字符。 */
export function createZellerRefundReference(attemptGuid: string): string {
  return `${ZELLER_REFUND_REFERENCE_PREFIX}${requireAttempt(attemptGuid)}`;
}

/**
 * 由本地 attempt 身份派生发给 Zeller 的 reference：扣款 HBZ-…，退款 HBZR-…。
 * attemptId 不是标准 GUID 时返回 null：没有可找回的 reference 就不能向 Zeller 发起交易，由调用方失败关闭。
 */
export function deriveZellerAttemptReference(
  attempt: Readonly<{ attemptId: string; operation: "purchase" | "refund" }>,
): string | null {
  const canonical = canonicalGuid(attempt.attemptId);
  if (canonical === null) return null;
  const hex = canonical.replaceAll("-", "");
  return attempt.operation === "refund"
    ? `${ZELLER_REFUND_REFERENCE_PREFIX}${hex}`
    : `${ZELLER_PURCHASE_REFERENCE_PREFIX}${hex}`;
}

/** 订单里的扣款引用：ZELLER:{reference}:{transactionUuid}。 */
export function formatZellerPayment(reference: string, transactionUuid: string): string {
  const purchase = requireReference(reference, ZELLER_PURCHASE_REFERENCE_PREFIX);
  return `${ZELLER_PAYMENT_PREFIX}${purchase}:${requireTransactionUuid(transactionUuid)}`;
}

/** 订单里的退款引用：ZELLERRF:{refundReference}:{refundTransactionUuid}。 */
export function formatZellerRefund(refundReference: string, refundTransactionUuid: string): string {
  const refund = requireReference(refundReference, ZELLER_REFUND_REFERENCE_PREFIX);
  return `${ZELLER_REFUND_PREFIX}${refund}:${requireTransactionUuid(refundTransactionUuid)}`;
}

/**
 * 主管确认已付款、但查不到 Zeller 交易时的订单引用：ZELLER:{reference}，没有 transactionUuid。
 * 这种付款不能原路退回卡上（解析不出原交易），退款要改用其他方式。
 */
export function formatZellerUnverifiedPayment(reference: string): string {
  return `${ZELLER_PAYMENT_PREFIX}${requireReference(reference, ZELLER_PURCHASE_REFERENCE_PREFIX)}`;
}

/** 主管确认已退款、但查不到 Zeller 退款交易时的订单引用：ZELLERRF:{refundReference}。 */
export function formatZellerUnverifiedRefund(refundReference: string): string {
  return `${ZELLER_REFUND_PREFIX}${requireReference(refundReference, ZELLER_REFUND_REFERENCE_PREFIX)}`;
}

/** 是不是主管确认、没有 transactionUuid 的 Zeller 引用（扣款或退款）。 */
export function isUnverifiedZellerReference(reference: unknown): boolean {
  const trimmed = trimmedOrNull(reference);
  if (trimmed === null) return false;
  if (startsWithIgnoreCase(trimmed, ZELLER_PAYMENT_PREFIX)) {
    return isAttemptReference(
      trimmed.slice(ZELLER_PAYMENT_PREFIX.length),
      ZELLER_PURCHASE_REFERENCE_PREFIX,
    );
  }
  return (
    startsWithIgnoreCase(trimmed, ZELLER_REFUND_PREFIX) &&
    isAttemptReference(trimmed.slice(ZELLER_REFUND_PREFIX.length), ZELLER_REFUND_REFERENCE_PREFIX)
  );
}

/** 中文注释：防护判断包括损坏的 Zeller 引用，不能因为格式错误而回退到别的刷卡通道。 */
export function isZellerReference(reference: unknown): boolean {
  const trimmed = trimmedOrNull(reference);
  return (
    trimmed !== null &&
    (startsWithIgnoreCase(trimmed, ZELLER_PAYMENT_PREFIX) ||
      startsWithIgnoreCase(trimmed, ZELLER_REFUND_PREFIX))
  );
}

/** 解析订单里的扣款引用；格式不对或不是 Zeller 扣款引用时返回 null。 */
export function parseZellerPayment(reference: unknown): ZellerPaymentReferenceParts | null {
  return parse(reference, ZELLER_PAYMENT_PREFIX, ZELLER_PURCHASE_REFERENCE_PREFIX);
}

/** 解析订单里的退款引用；格式不对或不是 Zeller 退款引用时返回 null。 */
export function parseZellerRefund(reference: unknown): ZellerPaymentReferenceParts | null {
  return parse(reference, ZELLER_REFUND_PREFIX, ZELLER_REFUND_REFERENCE_PREFIX);
}

function parse(
  reference: unknown,
  prefix: string,
  referencePrefix: string,
): ZellerPaymentReferenceParts | null {
  const trimmed = trimmedOrNull(reference);
  if (trimmed === null || !startsWithIgnoreCase(trimmed, prefix)) return null;

  const segments = trimmed.slice(prefix.length).split(":");
  const [attemptReference, rawTransactionUuid] = segments;
  if (
    segments.length !== 2 ||
    attemptReference === undefined ||
    rawTransactionUuid === undefined ||
    !isAttemptReference(attemptReference, referencePrefix)
  ) {
    return null;
  }
  const transactionUuid = canonicalGuid(rawTransactionUuid);
  if (transactionUuid === null) return null;

  // 与 C# 一致：reference 原样带出，transactionUuid 规范成小写 D 格式。
  return { reference: attemptReference, transactionUuid };
}

/** reference 前缀区分大小写；后面必须是非全零的 32 位十六进制（大小写不限）。 */
function isAttemptReference(value: string, referencePrefix: string): boolean {
  if (!value.startsWith(referencePrefix)) return false;
  // .NET 的 Guid.TryParseExact 会先去掉首尾空白，这里保持一致。
  const hex = value.slice(referencePrefix.length).trim().toLowerCase();
  return GUID_N.test(hex) && hex !== NIL_GUID_N;
}

/** 返回小写 D 格式；不是 GUID 或是全零 GUID 时返回 null。 */
function canonicalGuid(value: unknown): string | null {
  if (typeof value !== "string") return null;
  let text = value.trim().toLowerCase();
  if (
    (text.startsWith("{") && text.endsWith("}")) ||
    (text.startsWith("(") && text.endsWith(")"))
  ) {
    text = text.slice(1, -1);
    if (!GUID_D.test(text)) return null;
  }
  let hex: string;
  if (GUID_D.test(text)) {
    hex = text.replaceAll("-", "");
  } else if (GUID_N.test(text)) {
    hex = text;
  } else {
    return null;
  }
  if (hex === NIL_GUID_N) return null;
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function requireAttempt(attemptGuid: string): string {
  const canonical = canonicalGuid(attemptGuid);
  if (canonical === null) throw new Error("ZELLER_ATTEMPT_GUID_INVALID");
  return canonical.replaceAll("-", "");
}

function requireReference(reference: string, referencePrefix: string): string {
  const trimmed = typeof reference === "string" ? reference.trim() : "";
  if (!isAttemptReference(trimmed, referencePrefix)) throw new Error("ZELLER_REFERENCE_INVALID");
  return trimmed;
}

function requireTransactionUuid(transactionUuid: string): string {
  const canonical = canonicalGuid(transactionUuid);
  if (canonical === null) throw new Error("ZELLER_TRANSACTION_UUID_INVALID");
  return canonical;
}

function trimmedOrNull(reference: unknown): string | null {
  return typeof reference === "string" ? reference.trim() : null;
}

function startsWithIgnoreCase(value: string, prefix: string): boolean {
  return value.slice(0, prefix.length).toUpperCase() === prefix;
}
