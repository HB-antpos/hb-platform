import type { StoreProductPriceListDto } from '../../../types/storeProductPrice'

/** 表格行：多分店查询时同一商品会在每个分店各出现一行，所以行键必须带上分店 */
export type StoreProductPriceRow = StoreProductPriceListDto & { key: string; storeCode: string }

/** 价格类列的排序依赖单个分店的价格，多分店时后端不支持 */
export const SINGLE_STORE_SORT_FIELDS: ReadonlySet<string> = new Set(['storePurchasePrice', 'storeRetailPrice', 'discountRate'])

export function buildStoreProductPriceRows(
  items: StoreProductPriceListDto[],
  queriedStoreCodes: readonly string[],
): StoreProductPriceRow[] {
  // 单店查询时，该店没有价格记录的商品 storeCode 为空，用所查的分店补上；多分店时后端每行都带分店
  const fallbackStoreCode = queriedStoreCodes.length === 1 ? queriedStoreCodes[0] : ''
  return items.map((item, index) => {
    const storeCode = item.storeCode || fallbackStoreCode
    return { ...item, storeCode, key: `${storeCode}\u001f${item.productCode ?? `#${index}`}` }
  })
}

/** 选中行按分店分组（保持首次出现顺序、商品编码去重），批量更新逐个分店提交 */
export function groupProductCodesByStore(rows: readonly StoreProductPriceRow[]) {
  const groups = new Map<string, string[]>()
  for (const row of rows) {
    if (!row.storeCode || !row.productCode) continue
    const codes = groups.get(row.storeCode) ?? []
    if (!codes.includes(row.productCode)) codes.push(row.productCode)
    groups.set(row.storeCode, codes)
  }
  return [...groups].map(([storeCode, productCodes]) => ({ storeCode, productCodes }))
}

/** 选中行全部来自同一个分店时返回该分店，否则（含未选）返回 undefined */
export function getSingleStoreCode(rows: readonly StoreProductPriceRow[]) {
  const storeCodes = new Set(rows.map((row) => row.storeCode))
  return storeCodes.size === 1 ? [...storeCodes][0] || undefined : undefined
}

/** 与顺序无关地比较两次分店选择是否相同，相同则不重新查询 */
export function isSameStoreSelection(a: readonly string[], b: readonly string[]) {
  if (a.length !== b.length) return false
  const set = new Set(a)
  return b.every((code) => set.has(code))
}

/** 下拉里「全部分店」勾选框的状态 */
export function resolveSelectAllState(selected: readonly string[], all: readonly string[]) {
  const allSet = new Set(all)
  const selectedCount = selected.filter((code) => allSet.has(code)).length
  return {
    checked: all.length > 0 && selectedCount === all.length,
    indeterminate: selectedCount > 0 && selectedCount < all.length,
  }
}
