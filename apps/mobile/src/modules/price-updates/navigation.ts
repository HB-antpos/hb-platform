export const PRICE_UPDATES_PATH = "/(shell)/price-updates";

export interface PriceUpdatesRouteTarget {
  /** 要选中的分店；不传则沿用当前分店。 */
  storeCode?: string | null;
  /** 打开后直接选中「超期」筛选。 */
  overdueOnly?: boolean;
}

/**
 * 打开「价格和标签」的路由。工作台红条、被锁的报表入口、每小时提醒都走这里，
 * 页面只在一处（parsePriceUpdatesParams）解释这些参数，避免各入口各写一套。
 */
export function buildPriceUpdatesRoute(target: PriceUpdatesRouteTarget = {}) {
  const params: Record<string, string> = {};
  if (target.storeCode) params.storeCode = target.storeCode;
  if (target.overdueOnly) params.filter = "overdue";
  return { pathname: PRICE_UPDATES_PATH, params };
}

function firstParam(value: string | string[] | undefined): string | null {
  const raw = Array.isArray(value) ? value[0] : value;
  const trimmed = raw?.trim();
  return trimmed ? trimmed : null;
}

export function parsePriceUpdatesParams(
  params: Record<string, string | string[] | undefined>
): { storeCode: string | null; overdueOnly: boolean } {
  return {
    storeCode: firstParam(params.storeCode),
    overdueOnly: firstParam(params.filter) === "overdue",
  };
}

/** 当前是否就停在「价格和标签」页（用于不在该页弹提醒）。 */
export function isPriceUpdatesPath(pathname: string | null | undefined): boolean {
  if (!pathname) return false;
  return pathname.replace(/\/+$/, "").endsWith("/price-updates");
}
