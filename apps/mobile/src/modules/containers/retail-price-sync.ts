import { getDetailVisibleOemPrice } from "./query";
import type { ContainerDetail } from "./types";

/**
 * 货柜明细改仓库零售价时由操作人决定的两个开关，语义与仓库商品页完全一致（请求不传 = 都开）：
 * - syncStoreRetailPrices（同步分店零售价）：开 = 直接把各启用分店零售价改成新价并通知换标签；关 = 不动分店价，分店收到「改价格」任务。
 * - generateTasks（生成价格任务）：开 = 给分店生成价格更新任务；关 = 不通知分店（同步分店价时会立即同步总部 HQ）。
 */
export interface RetailPriceSyncChoice {
  syncStoreRetailPrices: boolean;
  generateTasks: boolean;
}

export interface RetailPriceSyncRequestFields {
  syncStoreRetailPrices?: boolean;
  generateTasks?: boolean;
}

/** 每次打开编辑 / 批量设价弹窗都回到这组默认值（都开），不持久化。 */
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
 * 编辑弹窗里零售价是否「会改仓库零售价」：只有已有商品、且填了与当前不同的有效数字时，
 * 才会回写仓库价并联动分店价，这时才需要展示两个开关。新商品的零售价只存货柜明细，不展示。
 */
export function isEditRetailPriceChangeForExistingProduct(detail: ContainerDetail, oemPriceInput: string): boolean {
  if (isNewProductDetail(detail)) return false;
  const next = parseFiniteNumber(oemPriceInput);
  return next !== undefined && next !== getDetailVisibleOemPrice(detail);
}

/** 批量设价里是否填了有效的零售价（与提交时「只传填写字段」的口径一致）。 */
export function isBulkRetailPriceFilled(oemPriceInput: string): boolean {
  return parseFiniteNumber(oemPriceInput) !== undefined;
}
