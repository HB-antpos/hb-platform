/**
 * 「有商品未创建」筛选的请求条件。
 *
 * 口径与列表「新品 N」标签一致：有效明细里检测为主档不存在、且至今没有关联商品编码。
 * 注意不是「本单新品 N」——那个是商品已经由本单建好了。
 * 关闭时不带该条件（后端只认 true，false 不收窄），这样请求体与旧版完全一致。
 */
export const UNCREATED_PRODUCTS_FILTER_KEY = 'hasUncreatedProducts'

export interface UncreatedProductsFilterCondition {
  filterType: 'text'
  type: 'equals'
  filter: 'true'
}

export function buildUncreatedProductsFilter(
  enabled: boolean,
): { key: typeof UNCREATED_PRODUCTS_FILTER_KEY; value: UncreatedProductsFilterCondition } | null {
  if (!enabled) return null
  return {
    key: UNCREATED_PRODUCTS_FILTER_KEY,
    value: { filterType: 'text', type: 'equals', filter: 'true' },
  }
}
