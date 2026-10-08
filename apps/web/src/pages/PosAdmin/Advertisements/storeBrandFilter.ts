// 广告编辑弹窗「分店范围」的品牌筛选纯逻辑：品牌分组、按品牌收窄分店、在筛选范围内全选 / 取消。
import type { BrandedStoreOption } from '../../../services/storeService'

/** 未设置品牌的分店单独归为一组，键值不会与真实品牌名冲突。 */
export const UNBRANDED_STORE_KEY = '__unbranded__'

export interface StoreBrandGroup {
  key: string
  /** 品牌显示名，取首次出现的写法；未设置品牌的组为 undefined，由页面显示文案。 */
  brandName?: string
  count: number
}

/** 品牌按不区分大小写、去首尾空格归并，避免 "Hot Bargain" 与 "hot bargain " 被拆成两组。 */
export function getStoreBrandKey(store: Pick<BrandedStoreOption, 'brandName'>) {
  const name = store.brandName?.trim()
  return name ? name.toLocaleLowerCase() : UNBRANDED_STORE_KEY
}

/** 品牌分组：分店多的品牌排前面，同数量按名称排；未设置品牌的组固定放最后。 */
export function buildStoreBrandGroups(stores: readonly BrandedStoreOption[]): StoreBrandGroup[] {
  const groups = new Map<string, StoreBrandGroup>()
  for (const store of stores) {
    const key = getStoreBrandKey(store)
    const existing = groups.get(key)
    if (existing) {
      existing.count += 1
    } else {
      groups.set(key, {
        key,
        brandName: key === UNBRANDED_STORE_KEY ? undefined : store.brandName?.trim(),
        count: 1,
      })
    }
  }

  return [...groups.values()].sort((left, right) => {
    if (left.key === UNBRANDED_STORE_KEY) return 1
    if (right.key === UNBRANDED_STORE_KEY) return -1
    return right.count - left.count || (left.brandName ?? '').localeCompare(right.brandName ?? '')
  })
}

/** brandKey 为空表示「全部品牌」，原样返回。 */
export function filterStoresByBrand<T extends BrandedStoreOption>(stores: readonly T[], brandKey: string | null) {
  return brandKey ? stores.filter((store) => getStoreBrandKey(store) === brandKey) : [...stores]
}

/** 当前范围内的勾选状态：全部选中 / 部分选中（半选）/ 未选。 */
export function getScopeSelectionState(selected: readonly string[], scopeValues: readonly string[]) {
  const selectedSet = new Set(selected)
  const selectedInScope = scopeValues.filter((value) => selectedSet.has(value)).length
  return {
    checked: scopeValues.length > 0 && selectedInScope === scopeValues.length,
    indeterminate: selectedInScope > 0 && selectedInScope < scopeValues.length,
  }
}

/**
 * 在当前范围内全选或取消：只增删范围内的分店，范围外已选的分店保持不变，
 * 这样可以先筛 A 品牌全选、再筛 B 品牌全选，叠加出跨品牌的投放范围。
 */
export function applyScopeSelection(selected: readonly string[], scopeValues: readonly string[], checked: boolean) {
  if (checked) {
    const selectedSet = new Set(selected)
    return [...selected, ...scopeValues.filter((value) => !selectedSet.has(value))]
  }
  const scopeSet = new Set(scopeValues)
  return selected.filter((value) => !scopeSet.has(value))
}
