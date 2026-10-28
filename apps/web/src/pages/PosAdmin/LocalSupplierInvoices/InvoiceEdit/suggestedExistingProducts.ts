import type {
  LocalSupplierInvoiceItemDto,
  ProductCheckResult,
  ProductCheckSuggestedProduct,
} from '../../../../types/localSupplierInvoice'

import { getProductStatusFilter } from './statusFilters'

/** 明细行 → 建议选用的已有商品；建议只存在于商品检测响应里（后端不落库），所以页面自己按行保存。 */
export type SuggestedExistingByDetail = Record<string, ProductCheckSuggestedProduct[]>

/**
 * 把一批商品检测结果合并进「行 → 建议」表。
 * 检测结果里出现的行一律以最新结果为准：没有建议（例如已选用商品、条码改了）就清掉旧提示，
 * 没出现在本次结果里的行保持不动。
 */
export function mergeSuggestedExistingProducts(
  previous: SuggestedExistingByDetail,
  results: readonly ProductCheckResult[],
): SuggestedExistingByDetail {
  const next: SuggestedExistingByDetail = { ...previous }
  for (const result of results) {
    const suggestions = (result.suggestedExistingProducts ?? []).filter(
      (item) => !!item.productCode?.trim(),
    )
    if (suggestions.length > 0) {
      next[result.detailGuid] = suggestions
    } else {
      delete next[result.detailGuid]
    }
  }
  return next
}

/**
 * 行内要展示的建议：只给「检测后主档不存在」的行。
 * 已选用商品或货号已命中主档的行，即使表里还有旧建议也不再提示。
 */
export function getRowSuggestedProducts(
  detail: LocalSupplierInvoiceItemDto,
  suggestions: SuggestedExistingByDetail,
): ProductCheckSuggestedProduct[] {
  if (getProductStatusFilter(detail) !== 'notExists') return []
  return suggestions[detail.detailGUID] ?? []
}

/** 提示里点名的商品：后端已把同货号的排在最前，取第一个；名称缺失时退回货号/商品编码。 */
export function describeSuggestedProduct(product: ProductCheckSuggestedProduct) {
  return {
    name: product.productName?.trim() || product.itemNumber?.trim() || product.productCode?.trim() || '--',
    supplier: product.localSupplierCode?.trim() || '--',
  }
}
