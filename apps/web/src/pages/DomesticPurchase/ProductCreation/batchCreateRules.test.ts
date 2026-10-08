import assert from 'node:assert/strict'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import {
  applyBatchAddProducts,
  buildPreviewItems,
  buildCreateBatchItems,
  createDraftProduct,
  findInvalidSetProduct,
  normalizeCreateCount,
  resolveProductPrice,
  sumSetSubItemPrices,
} from './batchCreateRules'
import type { DraftProductItem } from './batchCreateRules'
import {
  applyParentColumnPaste,
  applySubItemColumnPaste,
  getNextBatchCreateEditableCell,
  parseBatchCreateClipboardColumn,
} from './batchCreateGridRules'

let keyIndex = 0
const nextKey = (prefix: string) => `${prefix}-${keyIndex++}`

const products: DraftProductItem[] = [
  {
    key: 'normal-1',
    productName: ' 普通商品 ',
    productType: ProductCreationType.NORMAL,
    privateLabelPrice: 12.5,
  },
  {
    key: 'set-1',
    productName: ' 套装商品 ',
    productType: ProductCreationType.SET,
    createCount: 2.9,
    setQuantity: 1,
    setPrice: 25,
    subItems: [
      { key: 'empty-sub', productName: ' ', privateLabelPrice: null },
      { key: 'sub-1', productName: ' 子项商品 ', privateLabelPrice: 8 },
    ],
  },
]

assert.equal(normalizeCreateCount(undefined), 1)
assert.equal(normalizeCreateCount(0), 1)
assert.equal(normalizeCreateCount(2.9), 2)

assert.equal(findInvalidSetProduct(products), undefined)
assert.deepEqual(findInvalidSetProduct([
  { key: 'set-empty', productName: '', productType: ProductCreationType.SET, subItems: [] },
]), { key: 'set-empty', index: 1 })

const requestItems = buildCreateBatchItems(products)
assert.equal(requestItems[0].productName, '普通商品')
assert.equal(requestItems[0].createCount, undefined)
assert.equal(requestItems[1].productName, '套装商品')
assert.equal(requestItems[1].createCount, 2)
assert.equal(requestItems[1].subItems?.length, 1)
assert.equal(requestItems[1].subItems?.[0].productName, '子项商品')

keyIndex = 0
const appended = applyBatchAddProducts({
  products: [createDraftProduct(ProductCreationType.NORMAL, 0, null, nextKey)],
  selectedRowKeys: [],
  expandedRowKeys: [],
  type: ProductCreationType.SET,
  count: 2,
  price: 9.5,
  mode: 'append',
  createProduct: (type, index, price) => createDraftProduct(type, index, price, nextKey),
})

assert.equal(appended.products.length, 3)
assert.equal(appended.products[0].productType, ProductCreationType.NORMAL)
assert.equal(appended.products[1].productType, ProductCreationType.SET)
assert.equal(appended.products[2].productType, ProductCreationType.SET)
assert.deepEqual(appended.expandedRowKeys, ['temp-1', 'temp-3'])
assert.equal(appended.products[1].subItems?.length, 1)

keyIndex = 0
const overwritten = applyBatchAddProducts({
  products,
  selectedRowKeys: ['normal-1'],
  expandedRowKeys: ['set-1'],
  type: ProductCreationType.SET,
  count: 2,
  price: 10,
  mode: 'overwrite',
  createProduct: (type, index, price) => createDraftProduct(type, index, price, nextKey),
})

assert.equal(overwritten.products.length, 2)
assert.ok(overwritten.products.every((product) => product.productType === ProductCreationType.SET))
assert.deepEqual(overwritten.selectedRowKeys, [])
assert.deepEqual(overwritten.expandedRowKeys, ['temp-0', 'temp-2'])

const previewItems = buildPreviewItems([
  {
    key: 'set-preview',
    productName: '套装',
    productType: ProductCreationType.SET,
    createCount: 2,
    subItems: [
      { key: 'sub-preview', productName: ' 子项 ', privateLabelPrice: 3 },
      { key: 'price-only', productName: '   ', privateLabelPrice: 4 },
    ],
  },
], 'HB')

assert.deepEqual(previewItems.map((item) => item.itemNumber), ['HB0001', 'HB0002', 'HB0003', 'HB0004', 'HB0005', 'HB0006'])
assert.equal(previewItems[1].productName, '子项')
assert.equal(previewItems[2].productName, '')

const parsedColumn = parseBatchCreateClipboardColumn('第一行\r\n\r\n第三行\r\n')
assert.deepEqual(parsedColumn, {
  ok: true,
  values: ['第一行', '', '第三行'],
})
assert.deepEqual(parseBatchCreateClipboardColumn('A\tB\nC\tD'), {
  ok: false,
  reason: 'multiple_columns',
})

const mixedParentProducts: DraftProductItem[] = [
  {
    key: 'normal-paste',
    productName: '旧普通',
    productType: ProductCreationType.NORMAL,
    privateLabelPrice: 1,
  },
  {
    key: 'set-paste',
    productName: '旧套装',
    productType: ProductCreationType.SET,
    privateLabelPrice: 2,
    createCount: 1,
    setQuantity: 1,
    subItems: [{ key: 'set-paste-sub', productName: '旧子项' }],
  },
]

let pastedProductIndex = 0
const parentNamePaste = applyParentColumnPaste({
  products: mixedParentProducts,
  field: 'productName',
  clipboardText: '新普通\n新套装\n\n新增普通\n',
  createProduct: (type, index) => createDraftProduct(type, index, undefined, (prefix) => `${prefix}-paste-${pastedProductIndex++}`),
})

assert.deepEqual(parentNamePaste.products.map((item) => item.productName), ['新普通', '新套装', '', '新增普通'])
assert.deepEqual(parentNamePaste.products.map((item) => item.productType), [
  ProductCreationType.NORMAL,
  ProductCreationType.SET,
  ProductCreationType.NORMAL,
  ProductCreationType.NORMAL,
])
assert.equal(parentNamePaste.appliedCount, 3)
assert.equal(parentNamePaste.clearedCount, 1)
assert.equal(parentNamePaste.addedCount, 2)
assert.equal(parentNamePaste.invalidCount, 0)

const parentPricePaste = applyParentColumnPaste({
  products: parentNamePaste.products,
  startProductKey: 'set-paste',
  field: 'privateLabelPrice',
  clipboardText: '$1,234.50\n\n0\nabc\n-2',
  createProduct: (type, index) => createDraftProduct(type, index, undefined, (prefix) => `${prefix}-price-${pastedProductIndex++}`),
})

assert.equal(parentPricePaste.products[1].privateLabelPrice, 1234.5)
assert.equal(parentPricePaste.products[2].privateLabelPrice, undefined)
assert.equal(parentPricePaste.products[3].privateLabelPrice, 0)
assert.equal(parentPricePaste.products[4].privateLabelPrice, undefined)
assert.equal(parentPricePaste.products[5].privateLabelPrice, undefined)
assert.equal(parentPricePaste.appliedCount, 2)
assert.equal(parentPricePaste.clearedCount, 1)
assert.equal(parentPricePaste.addedCount, 2)
assert.equal(parentPricePaste.invalidCount, 2)

const invalidExistingPricePaste = applyParentColumnPaste({
  products: [{
    key: 'existing-price',
    productType: ProductCreationType.NORMAL,
    privateLabelPrice: 9.9,
  }],
  startProductKey: 'existing-price',
  field: 'privateLabelPrice',
  clipboardText: 'invalid',
})
assert.equal(invalidExistingPricePaste.products[0].privateLabelPrice, 9.9)
assert.equal(invalidExistingPricePaste.invalidCount, 1)
assert.equal(applyParentColumnPaste({
  products: mixedParentProducts,
  startProductKey: 'missing-product',
  field: 'productName',
  clipboardText: '不会写入',
}).error, 'missing_target')

const isolatedSets: DraftProductItem[] = [
  {
    key: 'set-a',
    productType: ProductCreationType.SET,
    setQuantity: 2,
    subItems: [
      { key: 'set-a-1', productName: 'A1' },
      { key: 'set-a-2', productName: 'A2' },
    ],
  },
  {
    key: 'set-b',
    productType: ProductCreationType.SET,
    setQuantity: 1,
    subItems: [{ key: 'set-b-1', productName: 'B1' }],
  },
]

let pastedSubItemIndex = 0
const subItemPaste = applySubItemColumnPaste({
  products: isolatedSets,
  setKey: 'set-a',
  startSubItemKey: 'set-a-2',
  field: 'productName',
  clipboardText: '新A2\n\n新A4\n',
  createSubItem: () => ({ key: `new-sub-${pastedSubItemIndex++}`, productName: '' }),
})

assert.deepEqual(subItemPaste.products[0].subItems?.map((item) => item.productName), ['A1', '新A2', '', '新A4'])
assert.equal(subItemPaste.products[0].setQuantity, 4)
assert.deepEqual(subItemPaste.products[1], isolatedSets[1])
assert.equal(subItemPaste.appliedCount, 2)
assert.equal(subItemPaste.clearedCount, 1)
assert.equal(subItemPaste.addedCount, 2)
assert.equal(subItemPaste.invalidCount, 0)

const parentNavigationRows = [
  { rowKey: 'normal-a', fields: ['productName', 'privateLabelPrice'] as const },
  { rowKey: 'set-a', fields: ['productName', 'privateLabelPrice', 'createCount', 'setQuantity', 'setPrice'] as const },
  { rowKey: 'normal-b', fields: ['productName', 'privateLabelPrice'] as const },
  { rowKey: 'set-b', fields: ['productName', 'privateLabelPrice', 'createCount', 'setQuantity', 'setPrice'] as const },
]

assert.deepEqual(getNextBatchCreateEditableCell({
  rows: parentNavigationRows,
  current: { rowKey: 'normal-a', field: 'productName' },
  direction: 'right',
}), { rowKey: 'normal-a', field: 'privateLabelPrice' })
assert.equal(getNextBatchCreateEditableCell({
  rows: parentNavigationRows,
  current: { rowKey: 'normal-a', field: 'privateLabelPrice' },
  direction: 'right',
}), undefined)
assert.deepEqual(getNextBatchCreateEditableCell({
  rows: parentNavigationRows,
  current: { rowKey: 'set-a', field: 'privateLabelPrice' },
  direction: 'right',
}), { rowKey: 'set-a', field: 'createCount' })
assert.deepEqual(getNextBatchCreateEditableCell({
  rows: parentNavigationRows,
  current: { rowKey: 'set-a', field: 'createCount' },
  direction: 'down',
}), { rowKey: 'set-b', field: 'createCount' })
assert.deepEqual(getNextBatchCreateEditableCell({
  rows: parentNavigationRows,
  current: { rowKey: 'set-b', field: 'setPrice' },
  direction: 'up',
}), { rowKey: 'set-a', field: 'setPrice' })
assert.deepEqual(getNextBatchCreateEditableCell({
  rows: parentNavigationRows,
  current: { rowKey: 'normal-a', field: 'privateLabelPrice' },
  direction: 'down',
}), { rowKey: 'set-a', field: 'privateLabelPrice' })

const subItemNavigationRows = [
  { rowKey: 'set-a-1', fields: ['productName', 'privateLabelPrice'] as const },
  { rowKey: 'set-a-2', fields: ['productName', 'privateLabelPrice'] as const },
]
assert.deepEqual(getNextBatchCreateEditableCell({
  rows: subItemNavigationRows,
  current: { rowKey: 'set-a-1', field: 'privateLabelPrice' },
  direction: 'down',
}), { rowKey: 'set-a-2', field: 'privateLabelPrice' })
assert.equal(getNextBatchCreateEditableCell({
  rows: subItemNavigationRows,
  current: { rowKey: 'set-a-2', field: 'privateLabelPrice' },
  direction: 'down',
}), undefined)

// ---- 套装主档零售价默认 = 子项零售价之和 ----
const setWithPricedSubs: DraftProductItem = {
  key: 'set-price',
  productName: '套装',
  productType: ProductCreationType.SET,
  subItems: [
    { key: 'p1', productName: 'A', privateLabelPrice: 0.1 },
    { key: 'p2', productName: 'B', privateLabelPrice: 0.2 },
    { key: 'p3', productName: '无价', privateLabelPrice: null },
    { key: 'p4', productName: ' ', privateLabelPrice: null },
  ],
}
// 浮点误差要被收口到两位小数：0.1 + 0.2 提交 0.3，而不是 0.30000000000000004
assert.equal(sumSetSubItemPrices(setWithPricedSubs.subItems), 0.3)
assert.equal(resolveProductPrice(setWithPricedSubs), 0.3)
assert.equal(buildCreateBatchItems([setWithPricedSubs])[0].privateLabelPrice, 0.3)
assert.equal(buildPreviewItems([setWithPricedSubs], 'HB')[0].privateLabelPrice, 0.3)

// 手填的主档价优先于默认值
assert.equal(resolveProductPrice({ ...setWithPricedSubs, privateLabelPrice: 9.9 }), 9.9)
assert.equal(buildCreateBatchItems([{ ...setWithPricedSubs, privateLabelPrice: 9.9 }])[0].privateLabelPrice, 9.9)

// 子项都没填价格时没有默认值，保持留空而不是提交 0
assert.equal(sumSetSubItemPrices([{ key: 'n', productName: '只有名称' }]), undefined)
assert.equal(resolveProductPrice({ ...setWithPricedSubs, subItems: [{ key: 'n', productName: '只有名称' }] }), undefined)

// 普通商品不受影响
assert.equal(resolveProductPrice({ key: 'n1', productType: ProductCreationType.NORMAL }), undefined)
