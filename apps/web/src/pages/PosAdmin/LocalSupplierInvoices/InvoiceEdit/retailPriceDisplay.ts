import type { LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'

/** 本店现价 ÷ 本次进货价低于它就标红：与自动定价 1.5～5 成率的下限一致，涨价后毛利不够时一眼能看出来。 */
export const STORE_RETAIL_MIN_MARKUP = 1.5

export type RetailPriceSource = 'designated' | 'store' | 'none'

export interface RetailPriceDisplay {
  /** 零售价列主行的值：有指定零售价用指定价，否则用本店现价；两者都没有为 undefined。 */
  value?: number
  source: RetailPriceSource
  /** 本店现价；没匹配商品或本店还没建价格时为 undefined。 */
  storeRetailPrice?: number
  /** 本店现价 ÷ 本次进货价；两者都有且进货价大于 0 才计算。 */
  storeMarkup?: number
  storeMarkupLow: boolean
}

function isFiniteNumber(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

/**
 * 零售价列显示什么：明细的 retailPrice 是「指定零售价」（「更新到分店」优先写入它），
 * storeRetailPrice 是分店价格表里的本店现价（只读）。指定价为空或 0 时（执行时同样当作没填）显示本店现价作参考。
 */
export function getRetailPriceDisplay(
  detail: Pick<LocalSupplierInvoiceItemDto, 'retailPrice' | 'storeRetailPrice' | 'purchasePrice' | 'productCode'>,
): RetailPriceDisplay {
  // 没匹配到商品谈不上本店价格；前端改选商品后到重新加载前，旧的本店现价也不该挂在新商品上。
  const storeRetailPrice = detail.productCode && isFiniteNumber(detail.storeRetailPrice)
    ? detail.storeRetailPrice
    : undefined
  const designated = isFiniteNumber(detail.retailPrice) && detail.retailPrice > 0 ? detail.retailPrice : undefined
  const purchasePrice = detail.purchasePrice
  const storeMarkup = storeRetailPrice !== undefined && isFiniteNumber(purchasePrice) && purchasePrice > 0
    ? storeRetailPrice / purchasePrice
    : undefined

  return {
    value: designated ?? storeRetailPrice,
    source: designated !== undefined ? 'designated' : storeRetailPrice !== undefined ? 'store' : 'none',
    storeRetailPrice,
    storeMarkup,
    storeMarkupLow: storeMarkup !== undefined && storeMarkup < STORE_RETAIL_MIN_MARKUP,
  }
}

/** 倍率保留两位，与定价列「自动 ×2.50」口径一致。 */
export function formatStoreMarkup(markup: number) {
  return markup.toFixed(2)
}
