import assert from 'node:assert/strict'

import { formatStoreMarkup, getRetailPriceDisplay, STORE_RETAIL_MIN_MARKUP } from './retailPriceDisplay'
import { compareNullableNumbers, matchesNumberColumnFilter, serializeNumberColumnFilter } from './tableColumnFilters'

assert.equal(STORE_RETAIL_MIN_MARKUP, 1.5, '下限与自动定价 1.5～5 成率一致')

// 真实案例（11-03 Waratah 0002080736-D01）：没填指定零售价，本店现价 2.50，本次进货价 1.57。
const storeOnly = getRetailPriceDisplay({ productCode: 'P1', retailPrice: undefined, storeRetailPrice: 2.5, purchasePrice: 1.57 })
assert.equal(storeOnly.source, 'store', '没有指定价时显示本店现价')
assert.equal(storeOnly.value, 2.5)
assert.equal(formatStoreMarkup(storeOnly.storeMarkup!), '1.59', '倍率 = 本店现价 ÷ 本次进货价')
assert.equal(storeOnly.storeMarkupLow, false, '1.59 倍不算低')

const designated = getRetailPriceDisplay({ productCode: 'P1', retailPrice: 3.99, storeRetailPrice: 2.5, purchasePrice: 1.57 })
assert.equal(designated.source, 'designated', '填了指定价时主行显示指定价')
assert.equal(designated.value, 3.99)
assert.equal(designated.storeRetailPrice, 2.5, '本店现价仍作为对照返回')

const zeroDesignated = getRetailPriceDisplay({ productCode: 'P1', retailPrice: 0, storeRetailPrice: 2.5, purchasePrice: 1.57 })
assert.equal(zeroDesignated.source, 'store', '指定价为 0 时执行同样当作没填，显示本店现价')

const lowMarkup = getRetailPriceDisplay({ productCode: 'P1', retailPrice: undefined, storeRetailPrice: 2.0, purchasePrice: 1.57 })
assert.equal(lowMarkup.storeMarkupLow, true, '本店现价不到进货价 1.5 倍要标红')

const noCost = getRetailPriceDisplay({ productCode: 'P1', retailPrice: undefined, storeRetailPrice: 2.5, purchasePrice: 0 })
assert.equal(noCost.storeMarkup, undefined, '没有进货价不算倍率')
assert.equal(noCost.storeMarkupLow, false)

const noProduct = getRetailPriceDisplay({ productCode: undefined, retailPrice: undefined, storeRetailPrice: 2.5, purchasePrice: 1.57 })
assert.equal(noProduct.source, 'none', '没匹配商品时不挂本店现价')
assert.equal(noProduct.value, undefined)

const noStorePrice = getRetailPriceDisplay({ productCode: 'P1', retailPrice: undefined, storeRetailPrice: null, purchasePrice: 1.57 })
assert.equal(noStorePrice.source, 'none', '本店还没建价格时照旧为空')

// 零售价列的排序与筛选按单元格显示的值：本店现价也参与。
const rowStore = { detailGUID: 'a', productCode: 'P1', storeRetailPrice: 2.5, purchasePrice: 1.57 }
const rowDesignated = { detailGUID: 'b', productCode: 'P2', retailPrice: 3.99, storeRetailPrice: 2.5, purchasePrice: 1.57 }
const rowEmpty = { detailGUID: 'c', productCode: undefined, purchasePrice: 1.57 }
assert.equal(matchesNumberColumnFilter(rowStore as never, 'retailPrice', serializeNumberColumnFilter({ mode: 'equals', value: 2.5 })), true, '筛选能命中只有本店现价的行')
assert.equal(matchesNumberColumnFilter(rowDesignated as never, 'retailPrice', serializeNumberColumnFilter({ mode: 'equals', value: 3.99 })), true, '有指定价时按指定价筛选')
assert.equal(matchesNumberColumnFilter(rowEmpty as never, 'retailPrice', serializeNumberColumnFilter({ mode: 'empty' })), true, '两者都没有才算空')
assert.equal(matchesNumberColumnFilter(rowStore as never, 'retailPrice', serializeNumberColumnFilter({ mode: 'empty' })), false, '有本店现价的行不算空')
assert.ok(compareNullableNumbers(getRetailPriceDisplay(rowStore as never).value, getRetailPriceDisplay(rowDesignated as never).value) < 0, '排序按显示值')

console.log('retailPriceDisplay tests passed')
