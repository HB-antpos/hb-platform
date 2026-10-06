import { readFileSync } from 'node:fs'
import path from 'node:path'
import {
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  MAX_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_LENGTH,
  createLocalSupplierInvoiceDndAccessibility,
  dispatchLocalSupplierInvoiceDragHandleKeyDown,
  dispatchLocalSupplierInvoiceDragHandlePointerDown,
  dispatchLocalSupplierInvoiceSortableHeaderKeyDown,
  DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS,
  LOCKED_LOCAL_SUPPLIER_INVOICE_COLUMNS,
  isLocalSupplierInvoiceColumnLayoutCustomized,
  isLocalSupplierInvoiceColumnOrderCustomized,
  mergeLocalSupplierInvoiceColumnOrder,
  moveLocalSupplierInvoiceColumnOrder,
  parseLocalSupplierInvoiceColumnOrder,
  parseLocalSupplierInvoiceHiddenColumns,
  toggleLocalSupplierInvoiceHiddenColumn,
} from './columnOrder'

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const expectedDefaultOrder = [
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
]
assertDeepEqual(
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  expectedDefaultOrder,
  '默认列序应以随货单号打头，并包含明细行数、价格变动和合并后的审计列',
)
assertEqual(
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER.includes('index' as never),
  false,
  '序号列不应进入可拖动列序',
)
assertEqual(
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER.includes('action' as never),
  false,
  '操作列不应进入可拖动列序',
)

const legacyColumnOrder = DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER
  .filter((key) => key !== 'isProductChecked')
  .reverse()
assertDeepEqual(
  parseLocalSupplierInvoiceColumnOrder(JSON.stringify(legacyColumnOrder)),
  [...legacyColumnOrder, 'isProductChecked'],
  '旧版自定义列序应保持原顺序，并补入商品检测列',
)
assertDeepEqual(
  parseLocalSupplierInvoiceColumnOrder(null),
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  '重置列序后商品检测列应恢复到流程状态之前',
)

assertDeepEqual(
  mergeLocalSupplierInvoiceColumnOrder(
    ['updatedAt', 'unknown', 'updatedBy', 'updatedAt', 'storeCode'],
    DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  ),
  ['updatedAt', 'storeCode', ...expectedDefaultOrder.filter((key) => key !== 'updatedAt' && key !== 'storeCode')],
  '持久化列序应过滤未知、已下线（如 updatedBy）和重复列，并按默认顺序补齐新增列',
)
assertDeepEqual(
  mergeLocalSupplierInvoiceColumnOrder({ storeCode: true }),
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  '非数组持久化值应恢复默认列序',
)
assertDeepEqual(
  moveLocalSupplierInvoiceColumnOrder(
    DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
    'updatedAt',
    'supplierCode',
  ),
  ['invoiceNo', 'storeCode', 'updatedAt', 'supplierCode', ...expectedDefaultOrder.slice(3, -1)],
  '拖拽应将审计字段移动到目标业务列位置',
)
assertDeepEqual(
  moveLocalSupplierInvoiceColumnOrder(
    DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
    'action',
    'storeCode',
  ),
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  '静态列不得改变业务列序',
)
assertEqual(
  isLocalSupplierInvoiceColumnOrderCustomized(DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER),
  false,
  '默认列序不应显示重置列按钮',
)
assertEqual(
  isLocalSupplierInvoiceColumnOrderCustomized(
    moveLocalSupplierInvoiceColumnOrder(
      DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
      'updatedAt',
      'storeCode',
    ),
  ),
  true,
  '调整列序后应显示重置列按钮',
)

assertDeepEqual(
  parseLocalSupplierInvoiceHiddenColumns(null),
  ['flowStatus', 'inboundDate', 'inboundStatus', 'receivedTotalAmount', 'remarks', 'updatedAt'],
  '首次打开应默认隐藏流程状态、入库日期、入库状态、已收总金额、备注和最后修改',
)
assertDeepEqual(
  DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS,
  parseLocalSupplierInvoiceHiddenColumns('not-json'),
  '损坏的显隐配置应恢复默认隐藏列',
)
assertDeepEqual(
  parseLocalSupplierInvoiceHiddenColumns(JSON.stringify(['remarks', 'invoiceNo', 'unknown', 'remarks', 'storeCode'])),
  ['storeCode', 'remarks'],
  '显隐配置应过滤未知列、去重、剔除锁定的随货单号，并按默认列序输出',
)
assertDeepEqual(
  parseLocalSupplierInvoiceHiddenColumns('[]'),
  [],
  '用户把所有列都打开后应保留空的隐藏列表',
)
assertDeepEqual(
  toggleLocalSupplierInvoiceHiddenColumn(DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS, 'remarks'),
  ['flowStatus', 'inboundDate', 'inboundStatus', 'receivedTotalAmount', 'updatedAt'],
  '勾选已隐藏的备注列应把它显示出来',
)
assertDeepEqual(
  toggleLocalSupplierInvoiceHiddenColumn([], 'invoiceNo'),
  [],
  '随货单号是锁定列，不能被隐藏',
)
assertEqual(
  isLocalSupplierInvoiceColumnLayoutCustomized(
    DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
    DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS,
  ),
  false,
  '默认列序和默认隐藏列不应提示可恢复默认',
)
assertEqual(
  isLocalSupplierInvoiceColumnLayoutCustomized(DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER, ['remarks']),
  true,
  '改过列显隐后应提示可恢复默认',
)
assertEqual(
  LOCKED_LOCAL_SUPPLIER_INVOICE_COLUMNS.includes('invoiceNo'),
  true,
  '随货单号应在锁定列里',
)

assertDeepEqual(
  parseLocalSupplierInvoiceColumnOrder('{invalid-json'),
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  '损坏的 JSON 应恢复默认列序',
)
assertDeepEqual(
  parseLocalSupplierInvoiceColumnOrder(
    'x'.repeat(MAX_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_LENGTH + 1),
  ),
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  '超长存储内容应在解析前恢复默认列序',
)

let dragKeyDownCalled = 0
let stopPropagationCalled = 0
const keyboardEvent = {
  key: ' ',
  stopPropagation: () => {
    stopPropagationCalled += 1
  },
}
dispatchLocalSupplierInvoiceDragHandleKeyDown(keyboardEvent, (event) => {
  dragKeyDownCalled += 1
  assertEqual(event, keyboardEvent, '应把原键盘事件交给 dnd listener')
})
assertEqual(dragKeyDownCalled, 1, '键盘拖拽 listener 应调用一次')
assertEqual(stopPropagationCalled, 1, '键盘拖拽事件应停止冒泡，避免触发表头排序')

let dragPointerDownCalled = 0
let pointerStopPropagationCalled = 0
const pointerEvent = {
  pointerId: 1,
  stopPropagation: () => {
    pointerStopPropagationCalled += 1
  },
}
dispatchLocalSupplierInvoiceDragHandlePointerDown(pointerEvent, (event) => {
  dragPointerDownCalled += 1
  assertEqual(event, pointerEvent, '应把原指针事件交给 dnd listener')
})
assertEqual(dragPointerDownCalled, 1, '指针拖拽 listener 应调用一次')
assertEqual(
  pointerStopPropagationCalled,
  1,
  '指针拖拽事件应停止冒泡，避免排序表头截获激活事件',
)

let sortableHeaderDragKeyDownCalled = 0
let sortableHeaderSortKeyDownCalled = 0
let sortableHeaderStopPropagationCalled = 0
const sortableHeaderSpaceEvent = {
  code: 'Space',
  stopPropagation: () => {
    sortableHeaderStopPropagationCalled += 1
  },
}
dispatchLocalSupplierInvoiceSortableHeaderKeyDown(
  sortableHeaderSpaceEvent,
  (event) => {
    sortableHeaderDragKeyDownCalled += 1
    assertEqual(event, sortableHeaderSpaceEvent, 'Space 应把原事件交给 dnd listener')
  },
  () => {
    sortableHeaderSortKeyDownCalled += 1
  },
)
assertEqual(sortableHeaderDragKeyDownCalled, 1, '排序列头 Space 应启动键盘拖拽')
assertEqual(sortableHeaderSortKeyDownCalled, 0, '排序列头 Space 不应触发表头排序')
assertEqual(
  sortableHeaderStopPropagationCalled,
  1,
  '排序列头 Space 应停止冒泡，避免触发其他键盘交互',
)

const sortableHeaderEnterEvent = {
  code: 'Enter',
  stopPropagation: () => {
    sortableHeaderStopPropagationCalled += 1
  },
}
dispatchLocalSupplierInvoiceSortableHeaderKeyDown(
  sortableHeaderEnterEvent,
  () => {
    sortableHeaderDragKeyDownCalled += 1
  },
  (event) => {
    sortableHeaderSortKeyDownCalled += 1
    assertEqual(event, sortableHeaderEnterEvent, 'Enter 应把原事件交给排序 listener')
  },
)
assertEqual(sortableHeaderDragKeyDownCalled, 1, '排序列头 Enter 不应启动键盘拖拽')
assertEqual(sortableHeaderSortKeyDownCalled, 1, '排序列头 Enter 应触发表头排序')
assertEqual(
  sortableHeaderStopPropagationCalled,
  1,
  '排序列头 Enter 不应被拖拽逻辑停止冒泡',
)

const dndAccessibility = createLocalSupplierInvoiceDndAccessibility(
  { storeCode: '分店', updatedAt: '更新时间' },
  {
    instructions: '键盘拖拽说明',
    unknownColumn: '当前列',
    dragStart: (column) => `拾取：${column}`,
    dragOver: (column, overColumn) => `移动：${column} -> ${overColumn}`,
    dragOverNone: (column) => `移出：${column}`,
    dragEnd: (column, overColumn) => `放下：${column} -> ${overColumn}`,
    dragCancel: (column) => `取消：${column}`,
  },
)
assertEqual(
  dndAccessibility.screenReaderInstructions.draggable,
  '键盘拖拽说明',
  '读屏键盘说明应使用本地化文案',
)
assertEqual(
  dndAccessibility.announcements.onDragStart({ active: { id: 'updatedAt' } }),
  '拾取：更新时间',
  '拖拽播报应使用本地化审计字段名称',
)
assertEqual(
  dndAccessibility.announcements.onDragEnd({ active: { id: 'unknown' }, over: null }),
  '取消：当前列',
  '未知列不得向读屏暴露内部 key',
)

function interpolateLocale(template: string, values: Record<string, string>) {
  return Object.entries(values).reduce(
    (result, [key, value]) => result.split(`{{${key}}}`).join(value),
    template,
  )
}

function createLocaleAccessibility(locale: Record<string, string>) {
  return createLocalSupplierInvoiceDndAccessibility({}, {
    instructions: locale.instructions,
    unknownColumn: locale.unknownColumn,
    dragStart: (column) => interpolateLocale(locale.dragStart, { column }),
    dragOver: (column, overColumn) =>
      interpolateLocale(locale.dragOver, { column, overColumn }),
    dragOverNone: (column) => interpolateLocale(locale.dragOverNone, { column }),
    dragEnd: (column, overColumn) =>
      interpolateLocale(locale.dragEnd, { column, overColumn }),
    dragCancel: (column) => interpolateLocale(locale.dragCancel, { column }),
  })
}

const zhDndLocale = JSON.parse(
  readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8'),
).posAdmin.invoices.dnd
const enDndLocale = JSON.parse(
  readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8'),
).posAdmin.invoices.dnd
assertEqual(
  createLocaleAccessibility(zhDndLocale).announcements.onDragStart({
    active: { id: 'unknown' },
  }),
  '已拾取当前列。',
  '中文未知列播报不应重复“列”字',
)
assertEqual(
  createLocaleAccessibility(enDndLocale).announcements.onDragStart({
    active: { id: 'unknown' },
  }),
  'Picked up the current column.',
  '英文未知列播报不应重复 column',
)

const pageSource = readFileSync(
  path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/index.tsx'),
  'utf8',
)
assertEqual(
  pageSource.includes('components={{ header: { cell: DraggableHeaderCell } }}'),
  true,
  '表格应接入可拖拽表头 cell',
)
assertEqual(
  pageSource.includes('horizontalListSortingStrategy'),
  true,
  '列拖拽应使用横向排序策略',
)
assertEqual(
  pageSource.includes("'data-drag-label'"),
  true,
  '拖拽手柄应提供本地化无障碍标签',
)
assertEqual(
  pageSource.includes('ref={setActivatorNodeRef}')
    && pageSource.includes('dispatchLocalSupplierInvoiceDragHandlePointerDown'),
  true,
  '拖拽手柄应绑定独立 activator，并隔离排序表头的 pointerdown 事件',
)
assertEqual(
  pageSource.includes("activationConstraint: { distance: 8 }")
    && pageSource.includes("start: ['Space']")
    && pageSource.includes("end: ['Space']"),
  true,
  '整列头拖拽应使用 8px 指针阈值，并只用 Space 启停键盘拖拽',
)
assertEqual(
  pageSource.includes("'data-sorter-enabled': Boolean(column.sorter)")
    && pageSource.includes('dispatchLocalSupplierInvoiceSortableHeaderKeyDown')
    && pageSource.includes('sorterEnabled ? null'),
  true,
  '排序列应隐藏手柄并通过整列头处理拖拽，非排序列继续显示手柄',
)
assertEqual(
  pageSource.includes('function SortableHeaderCell')
    && pageSource.includes('if (!columnKey) return <th'),
  true,
  '静态表头应跳过 sortable 注册，避免多个静态列共享重复拖拽 ID',
)

console.log('LocalSupplierInvoices.columnOrder.test: ok')
