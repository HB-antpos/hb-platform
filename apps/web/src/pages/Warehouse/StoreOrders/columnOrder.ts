export type StoreOrderListTableColumnKey = string
// 订货明细重设计后合并了列：商品（图片 + 货号 + 名称，第二行条码 · 零售价）、货位 · 拣货（货位 + 负责人）；
// 订货体积改为列设置里可选显示；序号列不在这里（不可拖拽/调宽，由 Detail.tsx 固定拼在最前）。
export type StoreOrderDetailTableColumnKey =
  | 'product'
  | 'locationCode'
  | 'quantity'
  | 'allocQuantity'
  | 'importPrice'
  | 'allocatedImportAmount'
  | 'orderVolume'
  | 'allocVolume'
  | 'isActive'
  | 'actions'

const STORE_ORDER_DETAIL_FIXED_LEFT_COLUMN_KEYS = new Set<StoreOrderDetailTableColumnKey>(['product'])
const STORE_ORDER_DETAIL_FIXED_RIGHT_COLUMN_KEYS = new Set<StoreOrderDetailTableColumnKey>(['actions'])

function mergeColumnOrder<T extends string>(
  savedOrder: unknown,
  availableOrder: readonly T[],
): T[] {
  const availableSet = new Set<T>(availableOrder)
  const seen = new Set<T>()
  const merged: T[] = []
  // localStorage 可能被写入合法但非数组的 JSON，统一在合并入口兜底，避免页面初始化崩溃。
  const savedValues = Array.isArray(savedOrder) ? savedOrder : []

  for (const value of savedValues) {
    if (typeof value !== 'string' || !availableSet.has(value as T)) {
      continue
    }
    const key = value as T
    if (seen.has(key)) {
      continue
    }
    seen.add(key)
    merged.push(key)
  }

  for (const key of availableOrder) {
    if (!seen.has(key)) {
      merged.push(key)
    }
  }

  return merged
}

function moveColumnOrder<T extends string>(
  currentOrder: readonly T[],
  activeKey: unknown,
  overKey: unknown,
): T[] {
  if (typeof activeKey !== 'string' || typeof overKey !== 'string' || activeKey === overKey) {
    return [...currentOrder]
  }

  const fromIndex = currentOrder.indexOf(activeKey as T)
  const toIndex = currentOrder.indexOf(overKey as T)
  if (fromIndex < 0 || toIndex < 0) {
    return [...currentOrder]
  }

  const nextOrder = [...currentOrder]
  const [moved] = nextOrder.splice(fromIndex, 1)
  nextOrder.splice(toIndex, 0, moved)
  return nextOrder
}

function isColumnOrderCustomized<T extends string>(
  currentOrder: readonly T[],
  defaultOrder: readonly T[],
): boolean {
  if (!currentOrder.length) {
    return false
  }

  const normalizedOrder = mergeColumnOrder(currentOrder, defaultOrder)
  return normalizedOrder.length !== defaultOrder.length ||
    normalizedOrder.some((key, index) => key !== defaultOrder[index])
}

export function mergeStoreOrderListColumnOrder(
  savedOrder: unknown,
  availableOrder: readonly StoreOrderListTableColumnKey[],
): StoreOrderListTableColumnKey[] {
  return mergeColumnOrder(savedOrder, availableOrder)
}

export function moveStoreOrderListColumnOrder(
  currentOrder: readonly StoreOrderListTableColumnKey[],
  activeKey: unknown,
  overKey: unknown,
): StoreOrderListTableColumnKey[] {
  return moveColumnOrder(currentOrder, activeKey, overKey)
}

export function isStoreOrderListColumnOrderCustomized(
  currentOrder: readonly StoreOrderListTableColumnKey[],
  defaultOrder: readonly StoreOrderListTableColumnKey[],
): boolean {
  return isColumnOrderCustomized(currentOrder, defaultOrder)
}

export function mergeStoreOrderDetailColumnOrder(
  savedOrder: unknown,
  availableOrder: readonly StoreOrderDetailTableColumnKey[],
): StoreOrderDetailTableColumnKey[] {
  const mergedOrder = mergeColumnOrder(savedOrder, availableOrder)
  // 固定左列、普通列、固定右列分别保留在各自分区，避免持久化旧顺序破坏 sticky 边界。
  return [
    ...mergedOrder.filter((key) => STORE_ORDER_DETAIL_FIXED_LEFT_COLUMN_KEYS.has(key)),
    ...mergedOrder.filter(
      (key) => !STORE_ORDER_DETAIL_FIXED_LEFT_COLUMN_KEYS.has(key) && !STORE_ORDER_DETAIL_FIXED_RIGHT_COLUMN_KEYS.has(key),
    ),
    ...mergedOrder.filter((key) => STORE_ORDER_DETAIL_FIXED_RIGHT_COLUMN_KEYS.has(key)),
  ]
}

export function moveStoreOrderDetailColumnOrder(
  currentOrder: readonly StoreOrderDetailTableColumnKey[],
  activeKey: unknown,
  overKey: unknown,
): StoreOrderDetailTableColumnKey[] {
  if (typeof activeKey !== 'string' || typeof overKey !== 'string') {
    return [...currentOrder]
  }

  const activeColumnKey = activeKey as StoreOrderDetailTableColumnKey
  const overColumnKey = overKey as StoreOrderDetailTableColumnKey
  const isActiveFixedLeft = STORE_ORDER_DETAIL_FIXED_LEFT_COLUMN_KEYS.has(activeColumnKey)
  const isOverFixedLeft = STORE_ORDER_DETAIL_FIXED_LEFT_COLUMN_KEYS.has(overColumnKey)
  const isActiveFixedRight = STORE_ORDER_DETAIL_FIXED_RIGHT_COLUMN_KEYS.has(activeColumnKey)
  const isOverFixedRight = STORE_ORDER_DETAIL_FIXED_RIGHT_COLUMN_KEYS.has(overColumnKey)
  if (isActiveFixedLeft !== isOverFixedLeft || isActiveFixedRight !== isOverFixedRight) {
    return [...currentOrder]
  }

  return moveColumnOrder(currentOrder, activeKey, overKey)
}

export function isStoreOrderDetailColumnOrderCustomized(
  currentOrder: readonly StoreOrderDetailTableColumnKey[],
  defaultOrder: readonly StoreOrderDetailTableColumnKey[],
): boolean {
  return isColumnOrderCustomized(currentOrder, defaultOrder)
}
