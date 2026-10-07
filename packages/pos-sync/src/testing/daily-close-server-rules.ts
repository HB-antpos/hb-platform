/**
 * 仅供测试使用：复刻 Hbpos.Api `DailyCloseSyncService.ValidateAndNormalize` 与 `DailyClosesController`
 * 对上传请求的全部校验规则，用来证明“真实保存路径产出的日结存档，经映射后能通过服务端校验”。
 *
 * 复刻方式：请求先序列化成 JSON 文本再解析（与线上一致），数字按 JSON 文本里的十进制精确读入
 * （System.Text.Json 读 decimal 也是按文本精确解析，不经过 double），金额比较全部用整数缩放，不用浮点。
 * 服务端规则变化时必须同步修改这里；生产代码不得导入本文件。
 */

export type ServerRuleResult =
  | Readonly<{ ok: true }>
  | Readonly<{ ok: false; code: string }>;

const DENOMINATION_CENTS = [10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10, 5];
const NOTE_MINIMUM_DENOMINATION_CENTS = 500;
const TENDER_METHODS = ["Cash", "Card", "Voucher"];
const CLIENT_KINDS = ["Wpf", "Handheld", "Ipad"];
const MAXIMUM_ABSOLUTE_VALUE = 100_000_000n;
const MAXIMUM_CASH_COUNT_QUANTITY = 100_000;
const INT32_MAXIMUM = 2_147_483_647;
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;
/** DateTimeOffset 必须带 Z 或 ±hh:mm 偏移：缺偏移时服务端会按其本地时区解释，视为不合格。 */
const OFFSET_TIMESTAMP_PATTERN =
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/iu;

type ParsedDecimal = Readonly<{ unscaled: bigint; scale: number }>;

class RuleViolation extends Error {
  public constructor(public readonly code: string) {
    super(code);
  }
}

/**
 * @param request 映射得到的上传请求对象
 * @param authenticatedScope 设备认证 claims 里的门店/设备（控制器要求与请求体 ordinal 相等）
 */
export function validateAgainstServerRules(
  request: unknown,
  authenticatedScope: Readonly<{ storeCode: string; deviceCode: string }>,
): ServerRuleResult {
  try {
    // 与线上一致：先过一遍 JSON 文本，再按服务端契约类型读取。
    const wire = JSON.parse(JSON.stringify(request)) as Readonly<
      Record<string, unknown>
    >;
    check(wire, authenticatedScope);
    return { ok: true };
  } catch (error) {
    if (error instanceof RuleViolation) return { ok: false, code: error.code };
    throw error;
  }
}

function check(
  wire: Readonly<Record<string, unknown>>,
  scope: Readonly<{ storeCode: string; deviceCode: string }>,
): void {
  // 控制器：请求体范围必须与认证 claims 完全一致（ordinal）。
  if (wire.storeCode !== scope.storeCode || wire.deviceCode !== scope.deviceCode) {
    throw new RuleViolation("DEVICE_SCOPE_FORBIDDEN");
  }
  if (wire.schemaVersion !== 1) throw new RuleViolation("UNSUPPORTED_SCHEMA_VERSION");
  if (
    typeof wire.dailyCloseGuid !== "string" ||
    !GUID_PATTERN.test(wire.dailyCloseGuid) ||
    /^0{8}-0{4}-0{4}-0{4}-0{12}$/u.test(wire.dailyCloseGuid)
  ) {
    throw new RuleViolation("DAILY_CLOSE_GUID_REQUIRED");
  }
  requiredText(wire.storeCode, 32, "STORE_CODE_REQUIRED");
  requiredText(wire.deviceCode, 64, "DEVICE_CODE_REQUIRED");
  const clientKind = typeof wire.clientKind === "string" ? wire.clientKind.trim() : "";
  if (!CLIENT_KINDS.some((kind) => kind.toLowerCase() === clientKind.toLowerCase())) {
    throw new RuleViolation("INVALID_CLIENT_KIND");
  }

  if (typeof wire.businessDate !== "string" || !/^\d{4}-\d{2}-\d{2}$/u.test(wire.businessDate)) {
    throw new RuleViolation("BUSINESS_DATE_REQUIRED");
  }
  for (const field of ["periodFrom", "periodTo", "savedAt"] as const) {
    const value = wire[field];
    if (
      typeof value !== "string" ||
      !OFFSET_TIMESTAMP_PATTERN.test(value) ||
      !Number.isFinite(Date.parse(value))
    ) {
      throw new RuleViolation(
        field === "savedAt" ? "SAVED_AT_REQUIRED" : "PERIOD_REQUIRED",
      );
    }
  }
  // 营业日以门店本地时区为准，最多比保存时刻的 UTC 日期晚一天。
  const savedUtcDate = new Date(Date.parse(wire.savedAt as string))
    .toISOString()
    .slice(0, 10);
  const latestBusinessDate = new Date(
    Date.parse(`${savedUtcDate}T00:00:00.000Z`) + 86_400_000,
  )
    .toISOString()
    .slice(0, 10);
  if (wire.businessDate > latestBusinessDate) {
    throw new RuleViolation("INVALID_BUSINESS_DATE");
  }

  optionalText(wire.cashierId, 64, "CASHIER_ID_TOO_LONG");
  optionalText(wire.cashierName, 128, "CASHIER_NAME_TOO_LONG");
  optionalText(wire.appVersion, 64, "APP_VERSION_TOO_LONG");

  if (
    typeof wire.orderCount !== "number" ||
    !Number.isInteger(wire.orderCount) ||
    wire.orderCount < 0 ||
    wire.orderCount > INT32_MAXIMUM
  ) {
    throw new RuleViolation("INVALID_ORDER_COUNT");
  }
  withinLimit(decimalOf(wire.returnQuantity), "INVALID_RETURN_QUANTITY");
  withinLimit(decimalOf(wire.refundAmount), "AMOUNT_OUT_OF_RANGE");

  const tenders = validateTenders(wire.tenders);
  const cashNetCents = tenders.get("Cash")!.net;

  const { noteCents, coinCents } = validateCashCounts(wire.cashCounts);
  for (const field of ["noteSubtotal", "coinSubtotal", "countedCashAmount", "cashDifference"]) {
    withinLimit(decimalOf(wire[field]), "AMOUNT_OUT_OF_RANGE");
  }
  const noteSubtotal = roundMoneyCents(decimalOf(wire.noteSubtotal));
  const coinSubtotal = roundMoneyCents(decimalOf(wire.coinSubtotal));
  const counted = roundMoneyCents(decimalOf(wire.countedCashAmount));
  const difference = roundMoneyCents(decimalOf(wire.cashDifference));
  // 盘点金额只能由数量推出，客户端汇总值必须与之精确一致。
  if (noteSubtotal !== noteCents) throw new RuleViolation("INVALID_NOTE_SUBTOTAL");
  if (coinSubtotal !== coinCents) throw new RuleViolation("INVALID_COIN_SUBTOTAL");
  if (counted !== noteCents + coinCents) {
    throw new RuleViolation("INVALID_COUNTED_CASH_AMOUNT");
  }
  // 差额 = 实点 − 现金净额，容差 0.01（1 分）。
  if (abs(difference - (counted - cashNetCents)) > 1n) {
    throw new RuleViolation("INVALID_CASH_DIFFERENCE");
  }
}

function validateTenders(
  value: unknown,
): Map<string, Readonly<{ sales: bigint; refund: bigint; net: bigint }>> {
  if (!Array.isArray(value) || value.length !== TENDER_METHODS.length) {
    throw new RuleViolation("INVALID_TENDERS");
  }
  const result = new Map<string, { sales: bigint; refund: bigint; net: bigint }>();
  for (const tender of value as readonly Readonly<Record<string, unknown>>[]) {
    if (!tender || typeof tender !== "object") throw new RuleViolation("INVALID_TENDERS");
    const method = TENDER_METHODS.find(
      (candidate) =>
        typeof tender.method === "string" &&
        candidate.toLowerCase() === tender.method.trim().toLowerCase(),
    );
    if (!method || result.has(method)) throw new RuleViolation("INVALID_TENDERS");
    for (const field of ["salesAmount", "refundAmount", "netAmount"]) {
      withinLimit(decimalOf(tender[field]), "AMOUNT_OUT_OF_RANGE");
    }
    const sales = roundMoneyCents(decimalOf(tender.salesAmount));
    const refund = roundMoneyCents(decimalOf(tender.refundAmount));
    const net = roundMoneyCents(decimalOf(tender.netAmount));
    // 服务端契约：Net = Sales − Refund（Refund 为正的退款额），容差 1 分。
    if (abs(net - (sales - refund)) > 1n) throw new RuleViolation("TENDER_NET_MISMATCH");
    result.set(method, { sales, refund, net });
  }
  return result;
}

function validateCashCounts(value: unknown): { noteCents: bigint; coinCents: bigint } {
  if (!Array.isArray(value) || value.length !== DENOMINATION_CENTS.length) {
    throw new RuleViolation("INVALID_CASH_COUNTS");
  }
  const quantities = new Map<number, number>();
  for (const count of value as readonly Readonly<Record<string, unknown>>[]) {
    if (
      !count ||
      typeof count.denominationCents !== "number" ||
      !DENOMINATION_CENTS.includes(count.denominationCents)
    ) {
      throw new RuleViolation("INVALID_CASH_COUNTS");
    }
    if (quantities.has(count.denominationCents)) throw new RuleViolation("INVALID_CASH_COUNTS");
    const quantity = count.quantity;
    // 面额与数量在契约里都是 int32：带小数点的数字会让 JSON 绑定失败。
    if (typeof quantity !== "number" || !Number.isInteger(quantity)) {
      throw new RuleViolation("INVALID_CASH_COUNTS");
    }
    if (quantity < 0 || quantity > MAXIMUM_CASH_COUNT_QUANTITY) {
      throw new RuleViolation("INVALID_CASH_COUNT_QUANTITY");
    }
    quantities.set(count.denominationCents, quantity);
  }
  let noteCents = 0n;
  let coinCents = 0n;
  for (const denomination of DENOMINATION_CENTS) {
    const cents = BigInt(denomination) * BigInt(quantities.get(denomination)!);
    if (denomination >= NOTE_MINIMUM_DENOMINATION_CENTS) noteCents += cents;
    else coinCents += cents;
  }
  return { noteCents, coinCents };
}

function requiredText(value: unknown, maxLength: number, code: string): void {
  if (typeof value !== "string" || value.trim().length === 0 || value.trim().length > maxLength) {
    throw new RuleViolation(code);
  }
}

function optionalText(value: unknown, maxLength: number, code: string): void {
  if (value === null || value === undefined) return;
  if (typeof value !== "string") throw new RuleViolation(code);
  if (value.trim().length > maxLength) throw new RuleViolation(code);
}

/** 请求里的数字按其 JSON 文本精确读入成十进制，避免任何浮点中间值。 */
function decimalOf(value: unknown): ParsedDecimal {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new RuleViolation("AMOUNT_OUT_OF_RANGE");
  }
  const text = JSON.stringify(value);
  if (/e/iu.test(text)) throw new RuleViolation("AMOUNT_OUT_OF_RANGE");
  const negative = text.startsWith("-");
  const [whole = "0", fraction = ""] = text.replace("-", "").split(".");
  const unscaled = BigInt(`${whole}${fraction}`) * (negative ? -1n : 1n);
  return { unscaled, scale: fraction.length };
}

function withinLimit(value: ParsedDecimal, code: string): void {
  // |value| <= 1e8：把上限折算到与 unscaled 相同的刻度再比较。
  if (abs(value.unscaled) > MAXIMUM_ABSOLUTE_VALUE * 10n ** BigInt(value.scale)) {
    throw new RuleViolation(code);
  }
}

/** decimal.Round(value, 2, AwayFromZero)，以“分”为单位返回。 */
function roundMoneyCents(value: ParsedDecimal): bigint {
  if (value.scale <= 2) return value.unscaled * 10n ** BigInt(2 - value.scale);
  const divisor = 10n ** BigInt(value.scale - 2);
  const magnitude = abs(value.unscaled);
  let quotient = magnitude / divisor;
  if ((magnitude % divisor) * 2n >= divisor) quotient += 1n;
  return value.unscaled < 0n ? -quotient : quotient;
}

function abs(value: bigint): bigint {
  return value < 0n ? -value : value;
}
