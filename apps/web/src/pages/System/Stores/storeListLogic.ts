// 分店列表页的纯逻辑：品牌选项累积、收银状态筛选与三态值互转。

/** 常见品牌固定出现在筛选下拉里，即使当前页数据里暂时没有它们。 */
export const KNOWN_STORE_BRANDS = ['Hot Bargain', 'Discount General', 'Dollar King'] as const

export type CashRegisterFilterValue = 'all' | 'enabled' | 'disabled'

/**
 * 把新加载页里出现的品牌并入已知品牌：按不区分大小写去重，保留首次出现的写法。
 * 筛选下拉的数据不能只来自当前页，否则切到只含一个品牌的页后，其它品牌就从下拉里消失了。
 */
export function mergeBrandNames(previous: readonly string[], items: ReadonlyArray<{ brandName?: string | null }>) {
  const seen = new Map(previous.map((name) => [name.trim().toLowerCase(), name]))
  let changed = false
  for (const item of items) {
    const name = item.brandName?.trim()
    if (name && !seen.has(name.toLowerCase())) {
      seen.set(name.toLowerCase(), name)
      changed = true
    }
  }
  // 没有新品牌时返回原数组引用，避免无谓的 setState 重渲染。
  return changed ? Array.from(seen.values()) : (previous as string[])
}

export function cashRegisterFilterToValue(isActive: boolean | undefined): CashRegisterFilterValue {
  return isActive === undefined ? 'all' : isActive ? 'enabled' : 'disabled'
}

export function cashRegisterFilterFromValue(value: CashRegisterFilterValue): boolean | undefined {
  return value === 'all' ? undefined : value === 'enabled'
}

/** 详情抽屉里的时间戳：后端返回 ISO 文本，只做文本层面的整理，不经时区换算。 */
export function formatTimestampText(value?: string | null) {
  return value ? value.replace('T', ' ').slice(0, 16) : '--'
}
