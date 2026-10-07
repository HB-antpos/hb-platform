import {
  isStoreOrderDetailColumnOrderCustomized,
  isStoreOrderListColumnOrderCustomized,
  mergeStoreOrderDetailColumnOrder,
  mergeStoreOrderListColumnOrder,
  moveStoreOrderDetailColumnOrder,
  moveStoreOrderListColumnOrder,
  type StoreOrderDetailTableColumnKey,
  type StoreOrderListTableColumnKey,
} from './columnOrder'

function assertDeepEqual<T>(actual: T, expected: T, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}\nExpected: ${expectedJson}\nActual: ${actualJson}`)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}\nExpected: ${expected}\nActual: ${actual}`)
  }
}

const defaultColumnOrder: StoreOrderListTableColumnKey[] = [
  'index',
  'orderNo',
  'storeCode',
  'orderDate',
  'flowStatus',
]

assertDeepEqual(
  mergeStoreOrderListColumnOrder(['storeCode', 'unknown', 'storeCode', 'orderNo'], defaultColumnOrder),
  ['storeCode', 'orderNo', 'index', 'orderDate', 'flowStatus'],
  '分店订货列表列顺序应过滤未知列、去重并补齐新增列',
)

assertDeepEqual(
  mergeStoreOrderListColumnOrder({ storeCode: true } as unknown as readonly unknown[], defaultColumnOrder),
  defaultColumnOrder,
  '分店订货列表列顺序遇到非数组持久化值时应回退默认顺序',
)

assertDeepEqual(
  moveStoreOrderListColumnOrder(defaultColumnOrder, 'flowStatus', 'orderNo'),
  ['index', 'flowStatus', 'orderNo', 'storeCode', 'orderDate'],
  '分店订货列表列拖拽应把 active 列移动到 over 列位置',
)

assertDeepEqual(
  moveStoreOrderListColumnOrder(defaultColumnOrder, 'missing', 'orderNo'),
  defaultColumnOrder,
  '分店订货列表列拖拽遇到未知 active 列时应保持原顺序',
)

assertDeepEqual(
  moveStoreOrderListColumnOrder(defaultColumnOrder, 'orderNo', 'orderNo'),
  defaultColumnOrder,
  '分店订货列表列拖拽 active 与 over 相同时应保持原顺序',
)

assertEqual(
  isStoreOrderListColumnOrderCustomized(defaultColumnOrder, defaultColumnOrder),
  false,
  '分店订货列表默认列顺序不应判定为已自定义',
)

assertEqual(
  isStoreOrderListColumnOrderCustomized(
    moveStoreOrderListColumnOrder(defaultColumnOrder, 'flowStatus', 'orderNo'),
    defaultColumnOrder,
  ),
  true,
  '分店订货列表拖拽列顺序后应判定为已自定义',
)

assertEqual(
  isStoreOrderListColumnOrderCustomized([], defaultColumnOrder),
  false,
  '分店订货列表列顺序初始化为空时不应误判为已自定义',
)

// 订货明细重设计后：商品列（图片 + 货号 + 名称）固定在左侧，操作列固定在右侧，其余为可拖拽的普通列。
const defaultDetailColumnOrder: StoreOrderDetailTableColumnKey[] = [
  'product',
  'locationCode',
  'quantity',
  'allocQuantity',
  'allocatedImportAmount',
  'actions',
]

assertDeepEqual(
  mergeStoreOrderDetailColumnOrder(['quantity', 'unknown', 'product', 'quantity', 'itemNumber'], defaultDetailColumnOrder),
  ['product', 'quantity', 'locationCode', 'allocQuantity', 'allocatedImportAmount', 'actions'],
  '订货明细列顺序应过滤未知列（含改版前的旧列键）、去重、补齐新增列，并把固定列留在两端',
)

assertDeepEqual(
  moveStoreOrderDetailColumnOrder(defaultDetailColumnOrder, 'locationCode', 'product'),
  defaultDetailColumnOrder,
  '订货明细列拖拽不应允许普通列跨入固定左列分区',
)

assertDeepEqual(
  moveStoreOrderDetailColumnOrder(defaultDetailColumnOrder, 'actions', 'quantity'),
  defaultDetailColumnOrder,
  '订货明细列拖拽不应允许固定右列跨入普通列分区',
)

assertDeepEqual(
  moveStoreOrderDetailColumnOrder(defaultDetailColumnOrder, 'quantity', 'locationCode'),
  ['product', 'quantity', 'locationCode', 'allocQuantity', 'allocatedImportAmount', 'actions'],
  '订货明细普通列仍应能在中间分区内调整顺序',
)

assertEqual(
  isStoreOrderDetailColumnOrderCustomized(defaultDetailColumnOrder, defaultDetailColumnOrder),
  false,
  '订货明细默认列顺序不应判定为已自定义',
)

assertEqual(
  isStoreOrderDetailColumnOrderCustomized(
    moveStoreOrderDetailColumnOrder(defaultDetailColumnOrder, 'quantity', 'locationCode'),
    defaultDetailColumnOrder,
  ),
  true,
  '订货明细拖拽列顺序后应判定为已自定义',
)

console.log('storeOrderColumnOrder.test: ok')
