import { getDetailRealtimeRetailPrice } from "./query";
import type { ContainerDetail } from "./types";

/**
 * 货柜明细改仓库零售价时由操作人决定的两个开关，语义与仓库商品页完全一致（请求不传 = 都开）：
 * - syncStoreRetailPrices（同步分店零售价）：开 = 直接把各启用分店零售价改成新价；关 = 不动分店价。
 * - generateTasks（生成价格任务）：开 = 给分店生成价格更新任务（同时开启同步是「待换标签」，否则是「改价格」）；
 *   关 = 不通知分店、不发任何任务（同步分店价时会立即同步总部 HQ，价签不会换）。
 *   是否通知分店只由 generateTasks 决定，与 syncStoreRetailPrices 无关。
 */
export interface RetailPriceSyncChoice {
  syncStoreRetailPrices: boolean;
  generateTasks: boolean;
}

export interface RetailPriceSyncRequestFields {
  syncStoreRetailPrices?: boolean;
  generateTasks?: boolean;
}

/** 每次打开编辑弹窗都回到这组默认值（都开），不持久化。 */
export const DEFAULT_RETAIL_PRICE_SYNC_CHOICE: Readonly<RetailPriceSyncChoice> = Object.freeze({
  syncStoreRetailPrices: true,
  generateTasks: true,
});

/**
 * 把开关状态转成要随请求发送的字段：本次没有改零售价返回空对象（请求不带这两个字段，
 * 其它保存保持原请求体），改了则两个字段都显式发送（关 = false，不依赖后端默认值）。
 */
export function buildRetailPriceSyncRequestFields(
  choice: RetailPriceSyncChoice,
  retailPriceChanged: boolean,
): RetailPriceSyncRequestFields {
  if (!retailPriceChanged) return {};
  return {
    syncStoreRetailPrices: choice.syncStoreRetailPrices,
    generateTasks: choice.generateTasks,
  };
}

/** 请求里的开关 → 查询串（批量保存的请求体是明细数组，开关是整批级别，所以放 query 上）。 */
export function buildRetailPriceSyncQuery(fields?: RetailPriceSyncRequestFields): string {
  const params: string[] = [];
  if (fields?.syncStoreRetailPrices !== undefined) params.push(`syncStoreRetailPrices=${fields.syncStoreRetailPrices}`);
  if (fields?.generateTasks !== undefined) params.push(`generateTasks=${fields.generateTasks}`);
  return params.length ? `?${params.join("&")}` : "";
}

function parseFiniteNumber(input: string): number | undefined {
  const trimmed = input.trim();
  if (!trimmed) return undefined;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : undefined;
}

/** 与 query.ts 其它位置一致的「新商品」判定：新商品尚未建仓库商品，零售价只存货柜明细。 */
function isNewProductDetail(detail: ContainerDetail) {
  return Boolean(detail.是否新商品 ?? detail.warehouseIsActive === false);
}

/**
 * 编辑弹窗里「已有零售价」是否会改仓库零售价：只有已有商品、且填了与当前仓库价不同的有效数字时，
 * 才会回写仓库价并联动分店价，这时才需要展示两个开关。「本次零售价」只存货柜明细，不触发开关。
 */
export function isEditRetailPriceChangeForExistingProduct(detail: ContainerDetail, warehouseOemPriceInput: string): boolean {
  if (isNewProductDetail(detail)) return false;
  const next = parseFiniteNumber(warehouseOemPriceInput);
  return next !== undefined && next !== getDetailRealtimeRetailPrice(detail);
}
