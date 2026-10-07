import assert from 'node:assert/strict'
import { ALL_PRODUCTS_FILTER_KEY, UNCATEGORIZED_PRODUCTS_FILTER_KEY } from '../Categories/categoryProductFilters'
import {
  keepHiddenColumnFilters,
  normalizeTableFilters,
  resolveCategoryFilterValueFromTableFilters,
  toWarehouseProductFilterKey,
} from './columnFilters'

// 组合列的列 key 映射到后端 Filters 键：商品列沿用货号筛选、供应商列沿用国内供应商筛选
assert.equal(toWarehouseProductFilterKey('product'), 'itemNumber')
assert.equal(toWarehouseProductFilterKey('supplier'), 'domesticSupplierCode')
assert.equal(toWarehouseProductFilterKey('labelPrice'), 'oemPrice')
assert.equal(toWarehouseProductFilterKey('barcode'), 'barcode')
assert.deepEqual(
  normalizeTableFilters({ product: ['__filter:starts:HB'], supplier: ['OY0412'], isActive: null }),
  { itemNumber: ['__filter:starts:HB'], domesticSupplierCode: ['OY0412'] },
)

// 被「列设置」隐藏的列不在 AntD filters 里，它们的条件必须沿用原值
const tableFilters = { product: null, supplier: ['S02'], isActive: ['false'] }
const currentFilters = {
  itemNumber: ['__filter:eq:HB1'],
  domesticSupplierCode: ['S01'],
  nameEn: ['__filter:contains:Lamp'],
  localSupplierCode: ['AU1'],
  productType: ['1'],
}
assert.deepEqual(
  keepHiddenColumnFilters(normalizeTableFilters(tableFilters), tableFilters, currentFilters),
  {
    domesticSupplierCode: ['S02'],
    isActive: ['false'],
    nameEn: ['__filter:contains:Lamp'],
    localSupplierCode: ['AU1'],
    productType: ['1'],
  },
  '可见列以 AntD 回传为准（含清空），隐藏列与只在工具栏设置的条件原样保留',
)
assert.deepEqual(currentFilters.itemNumber, ['__filter:eq:HB1'], '不得改动传入的当前筛选')

// 分类列隐藏时沿用当前分类（左侧面板选中值），显示时以列头值为准
assert.equal(resolveCategoryFilterValueFromTableFilters({ isActive: null }, 'cat-1'), 'cat-1')
assert.equal(resolveCategoryFilterValueFromTableFilters({ categoryName: null }, 'cat-1'), ALL_PRODUCTS_FILTER_KEY)
assert.equal(
  resolveCategoryFilterValueFromTableFilters({ categoryName: [UNCATEGORIZED_PRODUCTS_FILTER_KEY] }, 'cat-1'),
  UNCATEGORIZED_PRODUCTS_FILTER_KEY,
)
assert.equal(resolveCategoryFilterValueFromTableFilters({}), ALL_PRODUCTS_FILTER_KEY, '不传当前值时保持旧语义')

console.log('warehouseProducts.columnFilters.test: ok')
