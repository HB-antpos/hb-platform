// 列表默认列序：随货单号打头作为主识别列；入库/已收/备注/最后修改默认隐藏（见下方默认隐藏列）。
// 生产数据显示入库状态从未离开「未入库」、入库日期几乎不填，所以只放在列设置里按需打开。
export const DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER = [
  'invoiceNo',
  'storeCode',
  'supplierCode',
  'orderDate',
  'detailCount',
  'priceChange',
  'totalAmount',
  'isProductChecked',
  'flowStatus',
  'createdAt',
  'inboundDate',
  'inboundStatus',
  'receivedTotalAmount',
  'remarks',
  'updatedAt',
] as const

export const MAX_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_LENGTH = 4096

export type LocalSupplierInvoiceColumnKey =
  (typeof DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER)[number]

interface LocalSupplierInvoiceDndEvent {
  active: { id: unknown }
  over?: { id: unknown } | null
}

interface LocalSupplierInvoiceDndAccessibilityMessages {
  instructions: string
  unknownColumn: string
  dragStart: (column: string) => string
  dragOver: (column: string, overColumn: string) => string
  dragOverNone: (column: string) => string
  dragEnd: (column: string, overColumn: string) => string
  dragCancel: (column: string) => string
}

export function createLocalSupplierInvoiceDndAccessibility(
  columnLabels: Partial<Record<LocalSupplierInvoiceColumnKey, string>>,
  messages: LocalSupplierInvoiceDndAccessibilityMessages,
) {
  const getColumnLabel = (id: unknown) => {
    if (
      typeof id === 'string'
      && DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER.includes(
        id as LocalSupplierInvoiceColumnKey,
      )
    ) {
      return columnLabels[id as LocalSupplierInvoiceColumnKey] || messages.unknownColumn
    }
    return messages.unknownColumn
  }

  return {
    screenReaderInstructions: { draggable: messages.instructions },
    announcements: {
      onDragStart: ({ active }: LocalSupplierInvoiceDndEvent) =>
        messages.dragStart(getColumnLabel(active.id)),
      onDragOver: ({ active, over }: LocalSupplierInvoiceDndEvent) =>
        over
          ? messages.dragOver(getColumnLabel(active.id), getColumnLabel(over.id))
          : messages.dragOverNone(getColumnLabel(active.id)),
      onDragEnd: ({ active, over }: LocalSupplierInvoiceDndEvent) =>
        over
          ? messages.dragEnd(getColumnLabel(active.id), getColumnLabel(over.id))
          : messages.dragCancel(getColumnLabel(active.id)),
      onDragCancel: ({ active }: LocalSupplierInvoiceDndEvent) =>
        messages.dragCancel(getColumnLabel(active.id)),
    },
  }
}

export function dispatchLocalSupplierInvoiceDragHandleKeyDown<
  TEvent extends { stopPropagation: () => void },
>(event: TEvent, dndListener?: (event: TEvent) => void) {
  // 拖拽按键只交给手柄处理，避免空格或回车同时触发表头排序。
  event.stopPropagation()
  dndListener?.(event)
}

export function dispatchLocalSupplierInvoiceDragHandlePointerDown<
  TEvent extends { stopPropagation: () => void },
>(event: TEvent, dndListener?: (event: TEvent) => void) {
  // 指针拖拽只交给手柄处理，避免排序表头截获激活事件。
  event.stopPropagation()
  dndListener?.(event)
}

export function dispatchLocalSupplierInvoiceSortableHeaderKeyDown<
  TEvent extends { code: string; stopPropagation: () => void },
>(
  event: TEvent,
  dndListener?: (event: TEvent) => void,
  sortListener?: (event: TEvent) => void,
) {
  if (event.code === 'Space') {
    // 排序列头用 Space 拖拽、Enter 排序，避免两个键盘入口互相抢占。
    event.stopPropagation()
    dndListener?.(event)
    return
  }
  sortListener?.(event)
}

export function mergeLocalSupplierInvoiceColumnOrder(
  savedOrder: unknown,
  availableOrder: readonly LocalSupplierInvoiceColumnKey[] =
    DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
): LocalSupplierInvoiceColumnKey[] {
  const availableSet = new Set<LocalSupplierInvoiceColumnKey>(availableOrder)
  const seen = new Set<LocalSupplierInvoiceColumnKey>()
  const merged: LocalSupplierInvoiceColumnKey[] = []
  const savedValues = Array.isArray(savedOrder) ? savedOrder : []

  if (savedValues.length > availableOrder.length * 4) {
    return [...availableOrder]
  }

  // 本地列序可能来自旧版本或损坏数据，统一过滤、去重并补齐新增列。
  for (const value of savedValues) {
    if (typeof value !== 'string' || !availableSet.has(value as LocalSupplierInvoiceColumnKey)) {
      continue
    }
    const key = value as LocalSupplierInvoiceColumnKey
    if (seen.has(key)) continue
    seen.add(key)
    merged.push(key)
  }

  for (const key of availableOrder) {
    if (!seen.has(key)) merged.push(key)
  }

  return merged
}

export function parseLocalSupplierInvoiceColumnOrder(
  raw: string | null,
): LocalSupplierInvoiceColumnKey[] {
  if (!raw || raw.length > MAX_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_LENGTH) {
    return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER]
  }

  try {
    return mergeLocalSupplierInvoiceColumnOrder(JSON.parse(raw))
  } catch {
    return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER]
  }
}

export function moveLocalSupplierInvoiceColumnOrder(
  currentOrder: readonly LocalSupplierInvoiceColumnKey[],
  activeKey: unknown,
  overKey: unknown,
): LocalSupplierInvoiceColumnKey[] {
  if (typeof activeKey !== 'string' || typeof overKey !== 'string' || activeKey === overKey) {
    return [...currentOrder]
  }

  const fromIndex = currentOrder.indexOf(activeKey as LocalSupplierInvoiceColumnKey)
  const toIndex = currentOrder.indexOf(overKey as LocalSupplierInvoiceColumnKey)
  if (fromIndex < 0 || toIndex < 0) return [...currentOrder]

  const nextOrder = [...currentOrder]
  const [moved] = nextOrder.splice(fromIndex, 1)
  nextOrder.splice(toIndex, 0, moved)
  return nextOrder
}

export function isLocalSupplierInvoiceColumnOrderCustomized(
  currentOrder: readonly LocalSupplierInvoiceColumnKey[],
  defaultOrder: readonly LocalSupplierInvoiceColumnKey[] =
    DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
): boolean {
  const normalized = mergeLocalSupplierInvoiceColumnOrder(currentOrder, defaultOrder)
  return normalized.some((key, index) => key !== defaultOrder[index])
}

export type LocalSupplierInvoiceColumnKeyList = readonly LocalSupplierInvoiceColumnKey[]

/** 默认隐藏的列：信息量低（生产几乎恒定或很少填写；近 60 天新单流程状态全是草稿），在「列设置」里可以打开。备注默认显示（常写货柜/船次号）。 */
export const DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS: LocalSupplierInvoiceColumnKeyList = [
  'flowStatus',
  'inboundDate',
  'inboundStatus',
  'receivedTotalAmount',
  'updatedAt',
]

/** 不允许隐藏的列：随货单号是行的唯一入口（链接到明细页）。 */
export const LOCKED_LOCAL_SUPPLIER_INVOICE_COLUMNS: LocalSupplierInvoiceColumnKeyList = ['invoiceNo']

function isLocalSupplierInvoiceColumnKey(value: unknown): value is LocalSupplierInvoiceColumnKey {
  return (
    typeof value === 'string'
    && DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER.includes(value as LocalSupplierInvoiceColumnKey)
  )
}

export function parseLocalSupplierInvoiceHiddenColumns(
  raw: string | null,
): LocalSupplierInvoiceColumnKey[] {
  if (!raw || raw.length > MAX_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_LENGTH) {
    return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS]
  }

  try {
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS]
    // 本地存储可能来自旧版本或被手工改坏：过滤未知列、去重，并剔除不允许隐藏的锁定列。
    const hidden = new Set<LocalSupplierInvoiceColumnKey>()
    for (const value of parsed) {
      if (isLocalSupplierInvoiceColumnKey(value) && !LOCKED_LOCAL_SUPPLIER_INVOICE_COLUMNS.includes(value)) {
        hidden.add(value)
      }
    }
    return DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER.filter((key) => hidden.has(key))
  } catch {
    return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS]
  }
}

export function toggleLocalSupplierInvoiceHiddenColumn(
  hiddenColumns: LocalSupplierInvoiceColumnKeyList,
  key: LocalSupplierInvoiceColumnKey,
): LocalSupplierInvoiceColumnKey[] {
  if (LOCKED_LOCAL_SUPPLIER_INVOICE_COLUMNS.includes(key)) return [...hiddenColumns]
  const hidden = new Set(hiddenColumns)
  if (hidden.has(key)) hidden.delete(key)
  else hidden.add(key)
  return DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER.filter((column) => hidden.has(column))
}

export function isLocalSupplierInvoiceColumnLayoutCustomized(
  currentOrder: LocalSupplierInvoiceColumnKeyList,
  hiddenColumns: LocalSupplierInvoiceColumnKeyList,
): boolean {
  if (isLocalSupplierInvoiceColumnOrderCustomized(currentOrder)) return true
  const hidden = new Set(hiddenColumns)
  return (
    hidden.size !== DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS.length
    || DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS.some((key) => !hidden.has(key))
  )
}
