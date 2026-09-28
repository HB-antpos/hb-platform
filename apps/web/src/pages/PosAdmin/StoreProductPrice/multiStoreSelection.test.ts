import assert from 'node:assert/strict'

import {
  buildStoreProductPriceRows,
  getSingleStoreCode,
  groupProductCodesByStore,
  isSameStoreSelection,
  resolveSelectAllState,
} from './multiStoreSelection'

const base = { isActive: true, isStoreAutoPricing: false, isStoreSpecialProduct: false }

// 单店查询：缺价格记录的行 storeCode 为空，用所查分店补上；行键带分店
const singleRows = buildStoreProductPriceRows(
  [
    { ...base, productCode: 'P1', storeCode: 'S01' },
    { ...base, productCode: 'P2' },
  ],
  ['S01'],
)
assert.deepEqual(singleRows.map((row) => row.storeCode), ['S01', 'S01'])
assert.equal(new Set(singleRows.map((row) => row.key)).size, 2)

// 多分店查询：同一商品在不同分店的行键不同
const multiRows = buildStoreProductPriceRows(
  [
    { ...base, productCode: 'P1', storeCode: 'S01' },
    { ...base, productCode: 'P1', storeCode: 'S02' },
    { ...base, productCode: 'P2', storeCode: 'S01' },
    { ...base, productCode: 'P2', storeCode: 'S02' },
  ],
  ['S01', 'S02'],
)
assert.equal(new Set(multiRows.map((row) => row.key)).size, 4, '同一商品不同分店的行键必须不同')

// 批量更新按分店分组，保持首次出现顺序
assert.deepEqual(groupProductCodesByStore([multiRows[1], multiRows[0], multiRows[3], multiRows[2]]), [
  { storeCode: 'S02', productCodes: ['P1', 'P2'] },
  { storeCode: 'S01', productCodes: ['P1', 'P2'] },
])

// 只有全部来自同一分店才返回该分店（同步到其他分店、打印海报需要单一分店）
assert.equal(getSingleStoreCode([multiRows[0], multiRows[2]]), 'S01')
assert.equal(getSingleStoreCode([multiRows[0], multiRows[1]]), undefined)
assert.equal(getSingleStoreCode([]), undefined)

assert.equal(isSameStoreSelection(['S01', 'S02'], ['S02', 'S01']), true)
assert.equal(isSameStoreSelection(['S01'], ['S01', 'S02']), false)
assert.equal(isSameStoreSelection(['S01', 'S03'], ['S01', 'S02']), false)

assert.deepEqual(resolveSelectAllState([], ['S01', 'S02']), { checked: false, indeterminate: false })
assert.deepEqual(resolveSelectAllState(['S01'], ['S01', 'S02']), { checked: false, indeterminate: true })
assert.deepEqual(resolveSelectAllState(['S02', 'S01'], ['S01', 'S02']), { checked: true, indeterminate: false })
assert.deepEqual(resolveSelectAllState([], []), { checked: false, indeterminate: false })

console.log('multiStoreSelection.test: ok')
