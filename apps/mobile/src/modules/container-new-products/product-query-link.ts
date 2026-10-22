import type { ContainerNewProductItem } from "./types";

export const PRODUCT_QUERY_PATH = "/(shell)/product-query";

/**
 * 点击新品卡片的去向：
 * - 已建档，或旧版后端没给 isCreated（未知）→ 打开商品查询页，带上商品编码和当前门店；
 *   本店没有分店价时，商品查询页会自动补建分店价，所以这里不拦。
 * - 明确未建档（isCreated === false）→ 不跳转，提示等仓库完成创建。
 */
export type ProductTapAction =
  | { type: "open"; pathname: typeof PRODUCT_QUERY_PATH; params: { productCode: string; storeCode: string } }
  | { type: "pending" };

export function isAwaitingWarehouseCreation(item: Pick<ContainerNewProductItem, "isCreated">): boolean {
  return item.isCreated === false;
}

export function resolveProductTap(
  item: Pick<ContainerNewProductItem, "productCode" | "isCreated">,
  storeCode: string,
): ProductTapAction {
  if (isAwaitingWarehouseCreation(item)) return { type: "pending" };
  return { type: "open", pathname: PRODUCT_QUERY_PATH, params: { productCode: item.productCode, storeCode } };
}

/** 「重新检查」刷新列表后找回同一行：同一货柜同一商品（不区分大小写，与后端分组口径一致）。 */
export function findSameItem(
  items: readonly ContainerNewProductItem[],
  target: Pick<ContainerNewProductItem, "containerCode" | "productCode">,
): ContainerNewProductItem | undefined {
  const container = target.containerCode.toUpperCase();
  const product = target.productCode.toUpperCase();
  return items.find((item) => item.containerCode.toUpperCase() === container && item.productCode.toUpperCase() === product);
}

export type RecheckOutcome = "open" | "stillPending" | "failed";

/**
 * 「重新检查」的结论：刷新失败 → failed（留在弹窗可再试）；
 * 刷新后仍明确未建档 → stillPending；已建档、字段未知、或该行已不在列表里（货柜移出时间窗）→ open，交给商品查询页。
 */
export function resolveRecheck(
  refreshed: readonly ContainerNewProductItem[] | null,
  target: Pick<ContainerNewProductItem, "containerCode" | "productCode">,
): RecheckOutcome {
  if (!refreshed) return "failed";
  const fresh = findSameItem(refreshed, target);
  return fresh && isAwaitingWarehouseCreation(fresh) ? "stillPending" : "open";
}
