import assert from 'node:assert/strict'

import type {
  SupplierPreorderRevisionCounts,
  SupplierPreorderRevisionEntry,
  SupplierPreorderRevisionPreview,
} from '../../../types/supplierPreorder'

import {
  buildRevisionChips,
  buildRevisionCountItems,
  countPendingStores,
  filterRevisionEntries,
  getPreviousCartonQty,
  getPreviousPrice,
  getPriceDirection,
  getRemindOutcome,
  getRevisionApplyBlock,
  getRevisionChipColor,
  hasRevisionChanges,
  isOtherChangeEntry,
  truncateList,
} from './revision.logic'

// ---------- 价格涨跌 ----------

assert.equal(getPriceDirection(1.1, 1.2), 'up')
assert.equal(getPriceDirection(1.2, 1.1), 'down')
// 浮点误差不算改价
assert.equal(getPriceDirection(1.1, 1.1000001), 'same')
assert.equal(getPriceDirection(null, 1.2), null)
assert.equal(getPriceDirection(1.1, undefined), null)
assert.equal(getPriceDirection(Number.NaN, 1.2), null)

// ---------- 修订标记 ----------

assert.deepEqual(buildRevisionChips(null, { price: 1, cartonQty: 12 }), [])
assert.deepEqual(buildRevisionChips({ revision: 2, changes: [] }, { price: 1, cartonQty: 12 }), [])

// 顺序固定：新增 → 改价 → 箱规 → 改名 → 其他（其他合并成一个）
assert.deepEqual(
  buildRevisionChips(
    { revision: 2, changes: ['Image', 'Name', 'Carton', 'Price', 'Added', 'Barcode'], prevPrice: 1.1, prevCartonQty: 24 },
    { price: 1.2, cartonQty: 12 },
  ),
  [
    { kind: 'added' },
    { kind: 'price', from: 1.1, to: 1.2, direction: 'up' },
    { kind: 'carton', from: 24, to: 12 },
    { kind: 'name' },
    { kind: 'other', changes: ['Barcode', 'Image'] },
  ],
)

// 重新上架 + 降价；Removed 不出标签（由 isActive 显示）
assert.deepEqual(buildRevisionChips({ revision: 3, changes: ['Restored', 'Price'], prevPrice: 2 }, { price: 1.5, cartonQty: 6 }), [
  { kind: 'restored' },
  { kind: 'price', from: 2, to: 1.5, direction: 'down' },
])
assert.deepEqual(buildRevisionChips({ revision: 3, changes: ['Removed'] }, { price: 1.5, cartonQty: 6 }), [])

// 旧价缺失：只标「改价」不带箭头；旧值与当前值一样：不标
assert.deepEqual(buildRevisionChips({ revision: 1, changes: ['Price'], prevPrice: null }, { price: 1.5, cartonQty: 6 }), [
  { kind: 'price', from: null, to: 1.5, direction: null },
])
assert.deepEqual(buildRevisionChips({ revision: 1, changes: ['Price', 'Carton'], prevPrice: 1.5, prevCartonQty: 6 }, { price: 1.5, cartonQty: 6 }), [])
assert.deepEqual(buildRevisionChips({ revision: 1, changes: ['Carton'], prevCartonQty: null }, { price: 1.5, cartonQty: 6 }), [
  { kind: 'carton', from: null, to: 6 },
])

assert.equal(getRevisionChipColor({ kind: 'added' }), 'purple')
assert.equal(getRevisionChipColor({ kind: 'restored' }), 'purple')
assert.equal(getRevisionChipColor({ kind: 'price', from: 1, to: 2, direction: 'up' }), 'orange')
assert.equal(getRevisionChipColor({ kind: 'name' }), 'orange')
assert.equal(getRevisionChipColor({ kind: 'other', changes: ['Image'] }), 'default')

// 划掉的旧价 / 旧箱规
assert.equal(getPreviousPrice({ revision: 1, changes: ['Price'], prevPrice: 1.1 }, 1.2), 1.1)
assert.equal(getPreviousPrice({ revision: 1, changes: ['Price'], prevPrice: 1.2 }, 1.2), null)
assert.equal(getPreviousPrice({ revision: 1, changes: ['Carton'], prevPrice: 1.1 }, 1.2), null)
assert.equal(getPreviousPrice(null, 1.2), null)
assert.equal(getPreviousCartonQty({ revision: 1, changes: ['Carton'], prevCartonQty: 24 }, 12), 24)
assert.equal(getPreviousCartonQty({ revision: 1, changes: ['Carton'], prevCartonQty: 12 }, 12), null)
assert.equal(getPreviousCartonQty({ revision: 1, changes: ['Price'], prevCartonQty: 24 }, 12), null)

// ---------- 修订预览 ----------

function entry(partial: Partial<SupplierPreorderRevisionEntry>): SupplierPreorderRevisionEntry {
  return { kind: 'Changed', supplierItemCode: 'X', name: 'n', changes: [], orderedCartons: 0, orderedStores: [], ...partial }
}

const entries: SupplierPreorderRevisionEntry[] = [
  entry({ kind: 'Added', supplierItemCode: 'A1', changes: ['Added'] }),
  entry({ kind: 'Removed', supplierItemCode: 'R1', changes: ['Removed'], orderedCartons: 3, orderedStores: ['Bankstown'] }),
  entry({ kind: 'Restored', supplierItemCode: 'S1', changes: ['Restored', 'Price'], oldPrice: 1, newPrice: 1.2 }),
  entry({ kind: 'Changed', supplierItemCode: 'P1', changes: ['Price'], oldPrice: 1, newPrice: 0.9 }),
  entry({ kind: 'Changed', supplierItemCode: 'C1', changes: ['Carton', 'Name'] }),
  entry({ kind: 'Changed', supplierItemCode: 'O1', changes: ['Name', 'Image'] }),
]

assert.equal(isOtherChangeEntry(entries[5]), true)
assert.equal(isOtherChangeEntry(entries[4]), false)
assert.equal(isOtherChangeEntry(entries[0]), false)

const codes = (list: SupplierPreorderRevisionEntry[]) => list.map((item) => item.supplierItemCode)
assert.deepEqual(codes(filterRevisionEntries(entries, 'all')), ['A1', 'R1', 'S1', 'P1', 'C1', 'O1'])
assert.deepEqual(codes(filterRevisionEntries(entries, 'added')), ['A1'])
assert.deepEqual(codes(filterRevisionEntries(entries, 'removed')), ['R1'])
assert.deepEqual(codes(filterRevisionEntries(entries, 'restored')), ['S1'])
// 改价按 changes：重新上架同时改价也算
assert.deepEqual(codes(filterRevisionEntries(entries, 'price')), ['S1', 'P1'])
assert.deepEqual(codes(filterRevisionEntries(entries, 'carton')), ['C1'])
assert.deepEqual(codes(filterRevisionEntries(entries, 'other')), ['O1'])

const counts: SupplierPreorderRevisionCounts = {
  total: 120,
  added: 1,
  removed: 1,
  restored: 1,
  changed: 3,
  priceChanged: 2,
  cartonChanged: 1,
  unchanged: 110,
  invalid: 4,
  removedWithOrders: 1,
}
const preview: SupplierPreorderRevisionPreview = { currentRevision: 1, counts, entries, warnings: [] }

assert.deepEqual(
  buildRevisionCountItems(preview).map((item) => [item.key, item.count, item.filter]),
  [
    ['added', 1, 'added'],
    ['removed', 1, 'removed'],
    ['restored', 1, 'restored'],
    ['priceChanged', 2, 'price'],
    ['cartonChanged', 1, 'carton'],
    ['otherChanged', 1, 'other'],
    ['unchanged', 110, null],
    ['invalid', 4, null],
  ],
)

assert.equal(hasRevisionChanges(preview), true)
assert.equal(getRevisionApplyBlock(preview), null)
const zero = { added: 0, removed: 0, restored: 0, changed: 0, priceChanged: 0, cartonChanged: 0, removedWithOrders: 0 }
assert.equal(getRevisionApplyBlock({ counts: { ...counts, ...zero } }), 'noChanges')
// 一行都不能导入：即使后端算出一堆「下架」也不能应用
assert.equal(getRevisionApplyBlock({ counts: { ...counts, total: 5, invalid: 5, removed: 120 } }), 'noRows')
assert.equal(getRevisionApplyBlock({ counts: { ...counts, total: 0, invalid: 0 } }), 'noRows')

assert.deepEqual(truncateList(['a', 'b', 'c', 'd'], 3), { shown: ['a', 'b', 'c'], rest: 1 })
assert.deepEqual(truncateList(['a'], 3), { shown: ['a'], rest: 0 })
assert.deepEqual(truncateList([], 3), { shown: [], rest: 0 })

// ---------- 提醒 ----------

assert.equal(
  countPendingStores([
    { enabled: true, orderStatus: 'None' },
    { enabled: true, orderStatus: 'Draft' },
    { enabled: true, orderStatus: 'Submitted' },
    { enabled: false, orderStatus: 'None' },
  ]),
  2,
)

assert.equal(getRemindOutcome({ pendingStoreCount: 0, notifiedUserCount: 0, storesWithoutOrderers: [] }), 'nothingPending')
assert.equal(getRemindOutcome({ pendingStoreCount: 3, notifiedUserCount: 4, storesWithoutOrderers: [] }), 'allNotified')
assert.equal(getRemindOutcome({ pendingStoreCount: 3, notifiedUserCount: 2, storesWithoutOrderers: ['Bankstown'] }), 'partial')
assert.equal(getRemindOutcome({ pendingStoreCount: 1, notifiedUserCount: 0, storesWithoutOrderers: ['Bankstown'] }), 'nobodyNotified')

console.log('revision.logic tests passed')
