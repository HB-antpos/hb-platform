export type WarehouseProductTableColumnKey = string

export function mergeWarehouseProductColumnOrder(
  savedOrder: readonly unknown[] | null | undefined,
  availableOrder: readonly WarehouseProductTableColumnKey[],
): WarehouseProductTableColumnKey[] {
  const availableSet = new Set(availableOrder)
  const seen = new Set<WarehouseProductTableColumnKey>()
  const merged: WarehouseProductTableColumnKey[] = []

  for (const value of savedOrder ?? []) {
    if (typeof value !== 'string' || !availableSet.has(value)) {
      continue
    }
    if (seen.has(value)) {
      continue
    }
    seen.add(value)
    merged.push(value)
  }

  for (const key of availableOrder) {
    if (!seen.has(key)) {
      merged.push(key)
    }
  }

  return merged
}

export function moveWarehouseProductColumnOrder(
  currentOrder: readonly WarehouseProductTableColumnKey[],
  activeKey: unknown,
  overKey: unknown,
): WarehouseProductTableColumnKey[] {
  if (typeof activeKey !== 'string' || typeof overKey !== 'string' || activeKey === overKey) {
    return [...currentOrder]
  }

  const fromIndex = currentOrder.indexOf(activeKey)
  const toIndex = currentOrder.indexOf(overKey)
  if (fromIndex < 0 || toIndex < 0) {
    return [...currentOrder]
  }

  const nextOrder = [...currentOrder]
  const [moved] = nextOrder.splice(fromIndex, 1)
  nextOrder.splice(toIndex, 0, moved)
  return nextOrder
}

export function isWarehouseProductColumnOrderCustomized(
  currentOrder: readonly WarehouseProductTableColumnKey[],
  defaultOrder: readonly WarehouseProductTableColumnKey[],
): boolean {
  const normalizedOrder = mergeWarehouseProductColumnOrder(currentOrder, defaultOrder)

  return normalizedOrder.length !== defaultOrder.length ||
    normalizedOrder.some((key, index) => key !== defaultOrder[index])
}

/**
 * 列设置：低频列默认隐藏，用户勾选显示的可选列存到 localStorage。
 * 读取时过滤未知列与重复列；没有保存过（或内容不可用）时回到默认显示的可选列。
 */
export function normalizeWarehouseProductVisibleOptionalColumns(
  saved: unknown,
  optionalKeys: readonly WarehouseProductTableColumnKey[],
  defaultVisibleKeys: readonly WarehouseProductTableColumnKey[] = [],
): WarehouseProductTableColumnKey[] {
  const optionalSet = new Set(optionalKeys)
  const source = Array.isArray(saved) ? saved : defaultVisibleKeys
  const visible = new Set<WarehouseProductTableColumnKey>()
  for (const value of source) {
    if (typeof value === 'string' && optionalSet.has(value)) {
      visible.add(value)
    }
  }
  // 按可选列声明顺序输出，保证同样的勾选结果得到同样的数组，便于比较是否改过默认。
  return optionalKeys.filter((key) => visible.has(key))
}

export function isWarehouseProductColumnVisibilityCustomized(
  visibleOptionalKeys: readonly WarehouseProductTableColumnKey[],
  defaultVisibleKeys: readonly WarehouseProductTableColumnKey[],
): boolean {
  const current = new Set(visibleOptionalKeys)
  const defaults = new Set(defaultVisibleKeys)
  return current.size !== defaults.size || [...current].some((key) => !defaults.has(key))
}

/** 从完整列顺序里去掉未勾选的可选列；常驻列（商品、价格、状态、操作等）始终保留。 */
export function filterWarehouseProductVisibleColumnOrder(
  order: readonly WarehouseProductTableColumnKey[],
  optionalKeys: readonly WarehouseProductTableColumnKey[],
  visibleOptionalKeys: readonly WarehouseProductTableColumnKey[],
): WarehouseProductTableColumnKey[] {
  const optionalSet = new Set(optionalKeys)
  const visibleSet = new Set(visibleOptionalKeys)
  return order.filter((key) => !optionalSet.has(key) || visibleSet.has(key))
}
