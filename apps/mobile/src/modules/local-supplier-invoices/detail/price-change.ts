import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";

/**
 * 本次进货价较上次的涨跌（与 Web InvoiceEdit/index.tsx getPurchasePriceChange、
 * batchExecuteConfirm.ts getPurchasePriceChangeRatio、statusFilters.ts hasPurchasePriceChanged 对齐）。
 */
export type PriceSource = Pick<LocalSupplierInvoiceItem, "lastPurchasePrice" | "purchasePrice">;

export type PurchasePriceChangeKind = "none" | "noHistory" | "flat" | "up" | "down";
/** 涨幅分档：>20% strong、>5% normal、其余 light；降价与持平没有分档（null）。 */
export type PurchasePriceChangeLevel = "strong" | "normal" | "light";

export interface PurchasePriceChange {
  /** none = 没有本次进货价；noHistory = 没有上次进货价（空或 0）；flat = 持平；up / down = 涨 / 降。 */
  kind: PurchasePriceChangeKind;
  /** 带符号的涨跌幅（(本次 - 上次) / 上次，0.1 = +10%）；none / noHistory 时为 null，flat 时为 0。展示百分比请自行取绝对值。 */
  ratio: number | null;
  level: PurchasePriceChangeLevel | null;
  /** 上次进货价；none 时为 null。 */
  last: number | null;
}

/** 视为持平的绝对差阈值。 */
export const PURCHASE_PRICE_FLAT_EPSILON = 0.00005;
export const PURCHASE_PRICE_STRONG_RATIO = 0.2;
export const PURCHASE_PRICE_NORMAL_RATIO = 0.05;

export function getPurchasePriceChange(item: PriceSource): PurchasePriceChange {
  const current = item.purchasePrice;
  const last = item.lastPurchasePrice;
  if (current === undefined || current === null) {
    return { kind: "none", ratio: null, level: null, last: last ?? null };
  }
  if (last === undefined || last === null || last === 0) {
    return { kind: "noHistory", ratio: null, level: null, last: last ?? null };
  }
  if (Math.abs(current - last) < PURCHASE_PRICE_FLAT_EPSILON) {
    return { kind: "flat", ratio: 0, level: null, last };
  }
  // 与 Web 同一公式 (current - last) / last：分档边界（如 10.5 对 10 恰为 5%）不能换成 current / last - 1，浮点结果不同。
  const ratio = (current - last) / last;
  if (ratio < 0) return { kind: "down", ratio, level: null, last };
  const level: PurchasePriceChangeLevel =
    ratio > PURCHASE_PRICE_STRONG_RATIO ? "strong" : ratio > PURCHASE_PRICE_NORMAL_RATIO ? "normal" : "light";
  return { kind: "up", ratio, level, last };
}

/**
 * 涨跌幅（本次 / 上次 - 1），与 Web batchExecuteConfirm.getPurchasePriceChangeRatio 同口径：
 * 上次进货价为空或 ≤ 0（新商品）、本次价缺失 / 非有限数时无可比价，返回 null。
 */
export function getPurchasePriceChangeRatio(
  lastPurchasePrice: number | null | undefined,
  purchasePrice: number | null | undefined
): number | null {
  if (typeof lastPurchasePrice !== "number" || !(lastPurchasePrice > 0)) return null;
  if (typeof purchasePrice !== "number" || !Number.isFinite(purchasePrice)) return null;
  return purchasePrice / lastPurchasePrice - 1;
}

/**
 * 涨 / 降价判定（供「涨价 / 降价」筛选与计数）。与 Web statusFilters.hasPurchasePriceChanged 一致：
 * 上次价必须 > 0、本次价存在，且严格大小比较（不套持平阈值，所以展示为「持平」的微小差额仍可能被计入）。
 */
function hasPurchasePriceChanged(item: PriceSource, direction: "up" | "down"): boolean {
  const last = item.lastPurchasePrice;
  const current = item.purchasePrice;
  if (last === undefined || last === null || last <= 0 || current === undefined || current === null) {
    return false;
  }
  return direction === "up" ? current > last : current < last;
}

export function isPurchasePriceUp(item: PriceSource): boolean {
  return hasPurchasePriceChanged(item, "up");
}

export function isPurchasePriceDown(item: PriceSource): boolean {
  return hasPurchasePriceChanged(item, "down");
}

export function getPriceChangeStats(details: readonly PriceSource[]): { upCount: number; downCount: number } {
  let upCount = 0;
  let downCount = 0;
  for (const item of details) {
    if (isPurchasePriceUp(item)) upCount += 1;
    else if (isPurchasePriceDown(item)) downCount += 1;
  }
  return { upCount, downCount };
}
