import type { LocalSupplierInvoiceItem, ProductCheckRowResult } from "@/modules/local-supplier-invoices/types";
import { getProductStatusFilter } from "./detail-status";

/**
 * 「选用已有商品」前置条件与复核（与 Web InvoiceEdit/matchedProductLink.ts 对齐，并加上手机端的权限 / 未保存限制）。
 */

/**
 * 是否允许对该行「选用匹配商品」：
 * - 仅管理员（Web 的 canEditDetailRows = isAdmin）；
 * - 仅检测后主档不存在的行（货号对不上主档、条码却能匹配到已有商品，由人工指定这行就是该商品）；
 *   主档已存在、未检测、本单新品的行都不允许；
 * - 该行没有未保存修改（选用会写入并重新检测该行，先保存才不会丢手改）。
 */
export function canLinkMatchedProduct(
  item: Parameters<typeof getProductStatusFilter>[0],
  options: { isAdmin: boolean; hasUnsavedEdit: boolean }
): boolean {
  if (!options.isAdmin || options.hasUnsavedEdit) return false;
  return getProductStatusFilter(item) === "notExists";
}

/**
 * 选用后重新检测，确认后端确实沿用了所选商品（条码不属于该商品时检测会清掉关联）。
 * 商品编码比较忽略大小写与首尾空白；缺少检测结果或编码为空都判定为未关联。
 */
export function isProductLinkConfirmed(
  checkResult: Pick<ProductCheckRowResult, "productCode"> | null | undefined,
  productCode: string
): boolean {
  const linkedCode = checkResult?.productCode?.trim();
  return !!linkedCode && linkedCode.toLowerCase() === productCode.trim().toLowerCase();
}

export type MatchedProductLinkItem = Pick<LocalSupplierInvoiceItem, "detailGuid" | "productCode">;
