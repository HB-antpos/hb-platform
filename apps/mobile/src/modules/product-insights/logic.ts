import type { ProductInsightRange } from "./types";

export function isValidProductInsightRange(
  range: ProductInsightRange,
): boolean {
  const validDate = (value: string) =>
    /^\d{4}-\d{2}-\d{2}$/.test(value) &&
    Number.isFinite(Date.parse(`${value}T00:00:00Z`)) &&
    new Date(`${value}T00:00:00Z`).toISOString().slice(0, 10) === value;
  return (
    validDate(range.startDate) &&
    validDate(range.endDate) &&
    range.startDate <= range.endDate
  );
}

export function canViewProductInsightBranches(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
  isReview: boolean,
) {
  return (
    isAuthenticated &&
    !isReview &&
    hasPermission("Reports.ProductMovement.View")
  );
}

export function findProductInsightStore<T extends { storeCode: string }>(
  stores: T[],
  requestedCode: string | null | undefined,
) {
  return (
    stores.find(
      (store) =>
        store.storeCode.toLowerCase() === requestedCode?.trim().toLowerCase(),
    ) ?? null
  );
}

/** 售罄率低于该值且进货不少于 OVERSTOCK_MIN_PURCHASE 件时，分店行挂「积压」小标。 */
export const OVERSTOCK_MAX_SELL_THROUGH = 0.3;
export const OVERSTOCK_MIN_PURCHASE = 24;

export type BranchPurchaseState =
  | "unknown" // 服务器未返回进货数据
  | "noPurchase" // 本区间无进货
  | "noSales" // 有进货但无销售
  | "oversold" // 销量大于进货，在卖区间开始前的存货
  | "normal";

export interface BranchPurchaseInsight {
  state: BranchPurchaseState;
  /** 售罄率 = 销量 / 进货；无进货或未知时为 null。 */
  sellThroughRate: number | null;
  overstock: boolean;
}

export function describeBranchPurchase(row: {
  quantity: number;
  purchaseQuantity: number | null;
}): BranchPurchaseInsight {
  const purchase = row.purchaseQuantity;
  if (purchase === null) {
    return { state: "unknown", sellThroughRate: null, overstock: false };
  }
  if (purchase <= 0) {
    return { state: "noPurchase", sellThroughRate: null, overstock: false };
  }
  const rate = row.quantity / purchase;
  const overstock =
    rate < OVERSTOCK_MAX_SELL_THROUGH && purchase >= OVERSTOCK_MIN_PURCHASE;
  if (row.quantity <= 0) {
    return { state: "noSales", sellThroughRate: 0, overstock };
  }
  return {
    state: row.quantity > purchase ? "oversold" : "normal",
    sellThroughRate: rate,
    overstock,
  };
}

export type BranchSortKey = "quantity" | "purchase";

/** 分店表排序：按所选列降序，并列时依次按销量、销售额、分店名、编码，保证结果稳定。 */
export function sortBranchRows<
  T extends {
    storeCode: string;
    storeName: string;
    quantity: number;
    amount: number;
    purchaseQuantity: number | null;
  },
>(rows: readonly T[], key: BranchSortKey): T[] {
  const primary = (row: T) =>
    key === "purchase" ? (row.purchaseQuantity ?? -1) : row.quantity;
  return [...rows].sort(
    (a, b) =>
      primary(b) - primary(a) ||
      b.quantity - a.quantity ||
      b.amount - a.amount ||
      a.storeName.localeCompare(b.storeName) ||
      a.storeCode.localeCompare(b.storeCode),
  );
}

export interface BranchPurchaseTotals {
  sellThroughRate: number | null;
  purchaseStoreCount: number;
  salesStoreCount: number;
}

export function summarizeBranchPurchases(data: {
  quantity: number;
  purchaseQuantity: number | null;
  rows: readonly { quantity: number; purchaseQuantity: number | null }[];
}): BranchPurchaseTotals | null {
  if (data.purchaseQuantity === null) return null;
  return {
    sellThroughRate:
      data.purchaseQuantity > 0 ? data.quantity / data.purchaseQuantity : null,
    purchaseStoreCount: data.rows.filter(
      (row) => (row.purchaseQuantity ?? 0) > 0,
    ).length,
    salesStoreCount: data.rows.filter((row) => row.quantity > 0).length,
  };
}
