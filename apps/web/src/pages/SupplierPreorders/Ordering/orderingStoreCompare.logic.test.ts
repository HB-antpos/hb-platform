import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import type { SupplierPreorderStoreHistory } from '../../../types/supplierPreorder'

import { sortStoresBySalesDesc } from './orderingStoreCompare.logic'

// 展开行「各分店去年对比」表：按销量降序（需在 apps/web 目录下运行，与其他 web 单测一致）。

const storeRow = (storeCode: string, salesQty: number | null, extra: Partial<SupplierPreorderStoreHistory> = {}): SupplierPreorderStoreHistory => ({
  storeCode,
  storeName: `Store ${storeCode}`,
  isCurrent: false,
  hasData: salesQty !== null,
  purchaseBatches: 0,
  purchaseQty: 0,
  warehouseQty: 0,
  supplierQty: 0,
  salesQty: salesQty ?? 0,
  discountStatus: 'none',
  hasDiscount: false,
  discountRanges: [],
  daily: [],
  batches: [],
  ...extra,
})

// 后端按店号排：1001 当前店（没有去年记录）、1002 销量 0、1003 销量 27、1004 没记录、1005 销量 39、1006 / 1007 / 1008 同为 24
const input = [
  storeRow('1001', null, { isCurrent: true }),
  storeRow('1002', 0),
  storeRow('1003', 27),
  storeRow('1004', null),
  storeRow('1005', 39),
  storeRow('1006', 24),
  storeRow('1007', 24),
  storeRow('1008', 24),
]
const sorted = sortStoresBySalesDesc(input)
assert.deepEqual(
  sorted.map((store) => store.storeCode),
  ['1005', '1003', '1006', '1007', '1008', '1002', '1001', '1004'],
  '销量从高到低；同销量保持原顺序；有记录但销量为 0 的店排在没有记录的店前；当前店没记录时也不置顶',
)
assert.deepEqual(input.map((store) => store.storeCode), ['1001', '1002', '1003', '1004', '1005', '1006', '1007', '1008'], '不改入参')
assert.deepEqual(sortStoresBySalesDesc([]), [])
assert.equal(sorted.find((store) => store.isCurrent)?.storeCode, '1001', '当前店标记随行保留（界面靠行高亮标出）')

// 页面源码契约：对比表必须用排序后的行，销量列头带 ↓
const expandSource = readFileSync(resolve('src/pages/SupplierPreorders/Ordering/OrderingHistoryExpand.tsx'), 'utf8')
assert.ok(expandSource.includes('sortStoresBySalesDesc(stores)') && expandSource.includes('sortedStores.map('), '各分店去年对比表必须按销量降序渲染')
assert.ok(!/\{stores\.map\(/.test(expandSource), '对比表不得再直接遍历未排序的 stores')
assert.ok(expandSource.includes('colSales`)} ↓'), '销量列头必须带 ↓ 标出排序方向')
