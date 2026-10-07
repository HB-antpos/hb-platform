import {
  AUD_CASH_DENOMINATIONS_CENTS,
  type DailyCloseArchive,
  type DailyCloseTenderBreakdown,
  type DailyCloseTenderMethod,
} from "@hb/pos-domain/core/contracts/daily-close";

import type { components } from "@hb/pos-api-client/openapi";

export type DailyCloseSyncRequest = components["schemas"]["DailyCloseSyncRequest"];
export type DailyCloseSyncResponse = components["schemas"]["DailyCloseSyncResponse"];

/** 服务端 DailyCloseContractConstants：手持与 iPad 各自固定的 clientKind。 */
export type DailyCloseSyncClientKind = "Handheld" | "Ipad";

/** 当前唯一受支持的 schemaVersion。 */
export const DAILY_CLOSE_SYNC_SCHEMA_VERSION = 1;
/** 与服务端 AppVersion 的长度上限一致；超长会被服务端 400 拒绝，所以这里截断。 */
export const DAILY_CLOSE_SYNC_MAXIMUM_APP_VERSION_LENGTH = 64;

/** 金额换算的安全上限（分）。远大于服务端 1e8 元的校验上限，仅用于保证 double 能精确表示 2 位小数。 */
const MAXIMUM_SAFE_CENTS = 100_000_000_000_000;

const TENDER_WIRE_METHODS: Readonly<Record<DailyCloseTenderMethod, "Cash" | "Card" | "Voucher">> = {
  cash: "Cash",
  card: "Card",
  voucher: "Voucher",
};
const TENDER_ORDER: readonly DailyCloseTenderMethod[] = ["cash", "card", "voucher"];

/**
 * 本地存档无法映射成上传请求（数据本身有问题）。这类错误重试不会改变结果：
 * 上传服务把对应日结永久标记为 rejected，且不发任何请求。
 */
export class DailyCloseSyncMappingError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "DailyCloseSyncMappingError";
  }
}

export type DailyCloseSyncMappingOptions = Readonly<{
  clientKind: DailyCloseSyncClientKind;
  appVersion?: string | null | undefined;
}>;

/**
 * 本地日结存档 → 上传请求。不修正任何数据：存档里的汇总值不满足服务端校验时，
 * 由服务端 400 永久拒绝（服务端会精确校验 实点 = Σ(面额×数量)、纸币/硬币小计、差额与支付方式净额）。
 * 金额一律由“分”做整数换算成元，不引入浮点累加误差；不发送各支付方式笔数（存档本来没有）。
 */
export function mapDailyCloseArchiveToSyncRequest(
  archive: DailyCloseArchive,
  options: DailyCloseSyncMappingOptions,
): DailyCloseSyncRequest {
  const tenders = TENDER_ORDER.map((method) =>
    mapTender(requireTender(archive.tenders, method)),
  );
  // 存档没有单独保存“退款总额”：它就是各支付方式退款额（本地为非正分币）的绝对值之和。
  const refundCents = TENDER_ORDER.reduce(
    (total, method) =>
      total + Math.abs(requireTender(archive.tenders, method).refundCents),
    0,
  );
  return {
    schemaVersion: DAILY_CLOSE_SYNC_SCHEMA_VERSION,
    dailyCloseGuid: archive.closeId,
    storeCode: archive.storeCode,
    deviceCode: archive.deviceCode,
    clientKind: options.clientKind,
    businessDate: wireBusinessDate(archive.businessDate),
    periodFrom: wireTimestamp(archive.periodFromIso, "period from"),
    periodTo: wireTimestamp(archive.periodToIso, "period to"),
    savedAt: wireTimestamp(archive.savedAtIso, "saved at"),
    cashierId: archive.savedCashierId,
    cashierName: archive.savedCashierName,
    appVersion: normalizeDailyCloseAppVersion(options.appVersion),
    orderCount: archive.orderCount,
    returnQuantity: returnQuantityToNumber(archive.returnQuantity),
    refundAmount: centsToAmount(refundCents),
    tenders,
    cashCounts: AUD_CASH_DENOMINATIONS_CENTS.map((denominationCents) => {
      const entry = archive.denominations.find(
        (candidate) => candidate.denominationCents === denominationCents,
      );
      const quantity = entry?.quantity ?? 0;
      if (!Number.isSafeInteger(quantity) || quantity < 0) {
        throw new DailyCloseSyncMappingError(
          "DAILY_CLOSE_CASH_COUNT_INVALID",
          "Daily close cash count quantity is invalid.",
        );
      }
      // 缺档补 0，保证恰好 11 档；面额保持整数分，服务端按分校验。
      return { denominationCents, quantity };
    }),
    noteSubtotal: centsToAmount(archive.notesSubtotalCents),
    coinSubtotal: centsToAmount(archive.coinsSubtotalCents),
    countedCashAmount: centsToAmount(archive.countedCashCents),
    cashDifference: centsToAmount(archive.varianceCents),
  };
}

function requireTender(
  tenders: readonly DailyCloseTenderBreakdown[],
  method: DailyCloseTenderMethod,
): DailyCloseTenderBreakdown {
  const tender = tenders.find((candidate) => candidate.method === method);
  if (!tender) {
    throw new DailyCloseSyncMappingError(
      "DAILY_CLOSE_TENDER_MISSING",
      `Daily close ${method} tender is missing.`,
    );
  }
  return tender;
}

function mapTender(
  tender: DailyCloseTenderBreakdown,
): NonNullable<DailyCloseSyncRequest["tenders"]>[number] {
  return {
    method: TENDER_WIRE_METHODS[tender.method],
    salesAmount: centsToAmount(tender.salesCents),
    // 本地退款为非正分币，服务端契约里 Net = Sales − Refund，所以上传退款的绝对值。
    refundAmount: centsToAmount(Math.abs(tender.refundCents)),
    netAmount: centsToAmount(tender.netCents),
  };
}

/**
 * 分 → 元，全程整数运算：先用 BigInt 整除/取余拼出两位小数的十进制字符串，再交给 Number() 解析。
 * 解析得到的是“该十进制数的最近 double”，JSON 序列化后仍是同一个两位小数（服务端按 decimal 读入时精确等于该金额），
 * 不会像 cents/100 的累加、乘 0.01 之类写法那样引入误差。
 */
export function centsToAmount(cents: number): number {
  if (!Number.isSafeInteger(cents) || Math.abs(cents) > MAXIMUM_SAFE_CENTS) {
    throw new DailyCloseSyncMappingError(
      "DAILY_CLOSE_AMOUNT_UNSAFE",
      "Daily close amount cannot be represented exactly.",
    );
  }
  const absolute = BigInt(Math.abs(cents));
  const whole = absolute / 100n;
  const fraction = (absolute % 100n).toString().padStart(2, "0");
  const value = Number(`${whole}.${fraction}`);
  // 避免 -0：整数 0 分始终序列化为 0。
  return cents < 0 ? -value : value;
}

/**
 * 本地 returnQuantity 是无符号十进制字符串。服务端是 DECIMAL(18,3)，所以先按“远离零”四舍五入到 3 位小数
 * （与服务端 decimal.Round(3, AwayFromZero) 一致），再转 number；转换后必须能无损还原，否则视为数据异常。
 */
export function returnQuantityToNumber(text: string): number {
  if (!/^(?:0|[1-9]\d*)(?:\.\d+)?$/u.test(text)) {
    throw new DailyCloseSyncMappingError(
      "DAILY_CLOSE_RETURN_QUANTITY_INVALID",
      "Daily close return quantity is invalid.",
    );
  }
  const rounded = roundDecimalString(text, 3);
  const value = Number(rounded);
  if (!Number.isFinite(value) || trimTrailingZeros(String(value)) !== rounded) {
    throw new DailyCloseSyncMappingError(
      "DAILY_CLOSE_RETURN_QUANTITY_INVALID",
      "Daily close return quantity cannot be represented exactly.",
    );
  }
  return value;
}

function roundDecimalString(text: string, scale: number): string {
  const [whole = "0", fraction = ""] = text.split(".");
  if (fraction.length <= scale) {
    return trimTrailingZeros(fraction.length === 0 ? whole : `${whole}.${fraction}`);
  }
  let digits = BigInt(`${whole}${fraction.slice(0, scale)}`);
  if (fraction.charCodeAt(scale) - 48 >= 5) digits += 1n;
  const padded = digits.toString().padStart(scale + 1, "0");
  return trimTrailingZeros(`${padded.slice(0, -scale)}.${padded.slice(-scale)}`);
}

function trimTrailingZeros(text: string): string {
  if (!text.includes(".")) return text;
  return text.replace(/0+$/u, "").replace(/\.$/u, "");
}

export function normalizeDailyCloseAppVersion(
  appVersion: string | null | undefined,
): string | null {
  const normalized = appVersion?.trim() ?? "";
  if (normalized.length === 0) return null;
  return normalized.length <= DAILY_CLOSE_SYNC_MAXIMUM_APP_VERSION_LENGTH
    ? normalized
    : normalized.slice(0, DAILY_CLOSE_SYNC_MAXIMUM_APP_VERSION_LENGTH);
}

function wireBusinessDate(value: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/u.test(value)) {
    throw new DailyCloseSyncMappingError(
      "DAILY_CLOSE_BUSINESS_DATE_INVALID",
      "Daily close business date is invalid.",
    );
  }
  return value;
}

/**
 * 服务端按 DateTimeOffset 读入，只认明确的时区偏移：本地存的是带 Z/偏移的 ISO 串时原样使用；
 * 缺少偏移的串（服务端会按其本地时区解释，容易错位）先按设备时区解析成确定时刻再输出 UTC。
 */
function wireTimestamp(value: string, label: string): string {
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) {
    throw new DailyCloseSyncMappingError(
      "DAILY_CLOSE_TIMESTAMP_INVALID",
      `Daily close ${label} is invalid.`,
    );
  }
  return /T.*(?:Z|[+-]\d{2}:\d{2})$/iu.test(value)
    ? value
    : new Date(parsed).toISOString();
}
