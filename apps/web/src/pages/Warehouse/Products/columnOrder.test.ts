import {
  filterWarehouseProductVisibleColumnOrder,
  isWarehouseProductColumnOrderCustomized,
  isWarehouseProductColumnVisibilityCustomized,
  mergeWarehouseProductColumnOrder,
  moveWarehouseProductColumnOrder,
  normalizeWarehouseProductVisibleOptionalColumns,
} from './columnOrder'

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

assertDeepEqual(
  mergeWarehouseProductColumnOrder(['name', 'itemNumber'], ['itemNumber', 'name', 'barcode']),
  ['name', 'itemNumber', 'barcode'],
  '商品管理列顺序应保留已保存顺序，并自动追加新增列',
)

assertDeepEqual(
  mergeWarehouseProductColumnOrder(['removed', 'name', 'name', 'barcode'], ['itemNumber', 'name', 'barcode']),
  ['name', 'barcode', 'itemNumber'],
  '商品管理列顺序应过滤废弃列和重复列',
)

assertDeepEqual(
  mergeWarehouseProductColumnOrder(['removed', 'barcode'], ['itemNumber', 'name', 'barcode']),
  ['barcode', 'itemNumber', 'name'],
  '商品管理列顺序清理废弃列后应按默认顺序补齐缺失列',
)

assertDeepEqual(
  moveWarehouseProductColumnOrder(['itemNumber', 'name', 'barcode'], 'barcode', 'itemNumber'),
  ['barcode', 'itemNumber', 'name'],
  '商品管理列拖拽应把 active 列移动到 over 列位置',
)

assertDeepEqual(
  moveWarehouseProductColumnOrder(['itemNumber', 'name', 'barcode'], 'missing', 'name'),
  ['itemNumber', 'name', 'barcode'],
  '商品管理列拖拽遇到无效列时应保持原顺序',
)

assertDeepEqual(
  isWarehouseProductColumnOrderCustomized(['itemNumber', 'name', 'barcode'], ['itemNumber', 'name', 'barcode']),
  false,
  '商品管理列顺序与默认顺序一致时不应视为自定义',
)

assertDeepEqual(
  isWarehouseProductColumnOrderCustomized(['name', 'itemNumber', 'barcode'], ['itemNumber', 'name', 'barcode']),
  true,
  '商品管理列顺序与默认顺序不一致时应视为自定义',
)

// 列设置：可选列的显示状态
const optionalKeys = ['nameEn', 'barcode', 'updatedAt']

assertDeepEqual(
  normalizeWarehouseProductVisibleOptionalColumns(null, optionalKeys),
  [],
  '没有保存过列设置时可选列默认全部隐藏',
)

assertDeepEqual(
  normalizeWarehouseProductVisibleOptionalColumns(undefined, optionalKeys, ['barcode']),
  ['barcode'],
  '没有保存过列设置时应使用默认显示的可选列',
)

assertDeepEqual(
  normalizeWarehouseProductVisibleOptionalColumns(['updatedAt', 'product', 'nameEn', 'nameEn', 3], optionalKeys),
  ['nameEn', 'updatedAt'],
  '保存的列设置应过滤常驻列、未知值与重复值，并按可选列声明顺序输出',
)

assertDeepEqual(
  normalizeWarehouseProductVisibleOptionalColumns([], optionalKeys, ['barcode']),
  [],
  '用户主动全部取消勾选时不能回退到默认显示',
)

assertDeepEqual(
  normalizeWarehouseProductVisibleOptionalColumns('broken', optionalKeys, ['barcode']),
  ['barcode'],
  '缓存内容不是数组时回到默认显示',
)

assertDeepEqual(isWarehouseProductColumnVisibilityCustomized([], []), false, '与默认一致时不算自定义')
assertDeepEqual(isWarehouseProductColumnVisibilityCustomized(['barcode'], []), true, '多勾选一列应视为自定义')
assertDeepEqual(
  isWarehouseProductColumnVisibilityCustomized(['nameEn', 'barcode'], ['barcode', 'nameEn']),
  false,
  '同样的可选列集合与顺序无关',
)

assertDeepEqual(
  filterWarehouseProductVisibleColumnOrder(['product', 'nameEn', 'barcode', 'isActive', 'updatedAt', 'action'], optionalKeys, ['updatedAt']),
  ['product', 'isActive', 'updatedAt', 'action'],
  '只隐藏未勾选的可选列，常驻列始终保留且保持原顺序',
)

console.log('warehouseProducts.columnOrder.test: ok')
