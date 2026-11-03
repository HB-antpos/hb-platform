import type { LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'

/** 自动定价支持的最低进货价，与后端 AutoPricingRules.MinPurchasePrice 一致。 */
export const AUTO_PRICING_MIN_PURCHASE_PRICE = 0.1

/**
 * 自动定价开着、进货价大于 0 但低于下限：后端检测不会生成倍率和新自动零售价，执行「新建商品」也会被拦下。
 * 行内要把原因直接说出来，而不是只显示一个没有倍率的「自动」让人猜。
 * 进货价为 0 或没填走「无进货价」提示，不算这里。
 */
export function isAutoPricingBelowMinimum(
  detail: Pick<LocalSupplierInvoiceItemDto, 'autoPricing' | 'purchasePrice'>,
): boolean {
  if (detail.autoPricing !== true) return false
  const price = detail.purchasePrice
  return typeof price === 'number' && Number.isFinite(price) && price > 0 && price < AUTO_PRICING_MIN_PURCHASE_PRICE
}
