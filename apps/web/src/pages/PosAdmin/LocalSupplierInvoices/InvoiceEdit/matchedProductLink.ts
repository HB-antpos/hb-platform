import type { LocalSupplierInvoiceItemDto, ProductCheckResult } from '../../../../types/localSupplierInvoice'

import { getProductStatusFilter } from './statusFilters'

/**
 * 「选用已有商品」只给检测后主档不存在的行：货号对不上主档、但条码能匹配到已有商品时，
 * 由人工指定这行就是该商品，回填商品编码。主档已存在的行按货号命中，不需要也不允许改关联。
 */
export function canLinkMatchedProduct(detail: LocalSupplierInvoiceItemDto) {
  return getProductStatusFilter(detail) === 'notExists'
}

/** 把一条商品检测结果合并进明细行；检测结果由后端落库，前端只同步展示。 */
export function mergeProductCheckResult(
  detail: LocalSupplierInvoiceItemDto,
  checkResult: ProductCheckResult,
): LocalSupplierInvoiceItemDto {
  return {
    ...detail,
    productCode: checkResult.productInfo?.productCode ?? undefined,
    storeProductCode: checkResult.productInfo?.storeProductCode ?? checkResult.storeProductCode ?? undefined,
    existingProductCount: checkResult.existingProductCount,
    barcodeStatus: checkResult.barcodeStatus,
    barcodeMatchCount: checkResult.barcodeMatchCount,
    autoPricing: checkResult.autoPricing ?? undefined,
    isSpecialProduct: checkResult.isSpecialProduct ?? undefined,
    discountRate: checkResult.discountRate ?? undefined,
    pricingFloatRate: checkResult.pricingFloatRate ?? undefined,
    newAutoRetailPrice: checkResult.newAutoRetailPrice ?? undefined,
    // 商品检测只补空的上次进货价；已有快照由手动按钮强制刷新，避免自动检测覆盖比较基准。
    lastPurchasePrice: detail.lastPurchasePrice ?? checkResult.lastPurchasePrice ?? undefined,
    activityType: checkResult.defaultAction ?? detail.activityType,
  }
}

/** 选用后重新检测，确认后端确实沿用了所选商品（条码不属于该商品时检测会清掉关联）。 */
export function isProductLinkConfirmed(checkResult: ProductCheckResult | undefined, productCode: string) {
  const linkedCode = checkResult?.productInfo?.productCode?.trim()
  return !!linkedCode && linkedCode.toLowerCase() === productCode.trim().toLowerCase()
}
