import assert from 'node:assert/strict'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { DraftProductItem } from './batchCreateRules'
import {
  MAX_CREATED_ITEMS,
  addSubItem,
  applyBatchRename,
  buildCreatedBatchInfo,
  buildPreviewRows,
  buildSubmitRequest,
  countExpectedItems,
  createInitialProducts,
  hasProductContent,
  isBatchDraftDirty,
  removeProduct,
  removeSubItem,
  summarizeDraft,
  updateProductField,
  updateSubItemField,
  validateDraft,
} from './batchWorkspaceLogic'

const normal = (key: string, extra: Partial<DraftProductItem> = {}): DraftProductItem => ({
  key,
  productName: '',
  productType: ProductCreationType.NORMAL,
  ...extra,
})

const setOf = (key: string, subItems: DraftProductItem['subItems'], extra: Partial<DraftProductItem> = {}): DraftProductItem => ({
  key,
  productName: '',
  productType: ProductCreationType.SET,
  createCount: 1,
  setQuantity: subItems?.length ?? 0,
  subItems,
  ...extra,
})

// ---- 预计货号数：与后端 CreateBatchAsync 的展开口径一致 ----
assert.equal(countExpectedItems([normal('a'), normal('b')]), 2)
assert.equal(
  countExpectedItems([
    // 创建套数 2 × (1 + 3 个有效子项) = 8
    setOf('s1', [
      { key: 'x1', productName: 'A' },
      { key: 'x2', privateLabelPrice: 3 },
      { key: 'x3', productName: 'C', privateLabelPrice: 1 },
      { key: 'blank', productName: ' ', privateLabelPrice: null },
    ], { createCount: 2 }),
    normal('n'),
  ]),
  9,
  '空白子项占位行不计入展开量',
)
assert.equal(countExpectedItems([setOf('s0', [], { createCount: 3.9 })]), 3, '无有效子项时每套只占套装本身，套数向下取整')
assert.equal(countExpectedItems([setOf('s0', [{ key: 'k', productName: 'A' }], { createCount: 0 })]), 2, '套数最小按 1 计')

const summary = summarizeDraft([
  normal('n1'),
  normal('n2'),
  setOf('s1', [{ key: 'k1', productName: 'A' }]),
  setOf('s2', [{ key: 'k2', productName: '' }]),
])
assert.deepEqual(summary, { normalCount: 2, setCount: 2, pendingSetCount: 1, expectedItems: 2 + 2 + 1, overLimit: false })

// ---- 校验：只有三类真正的错误 ----
assert.deepEqual(validateDraft({ supplierCode: 'S001', products: [normal('a')] }), [], '名称与零售价都为空的普通行不是错误')
assert.deepEqual(
  validateDraft({
    supplierCode: 'S001',
    products: [normal('a', { productName: '', privateLabelPrice: undefined }), normal('b', { privateLabelPrice: null })],
  }),
  [],
  '商品名称、零售价允许留空',
)
assert.deepEqual(validateDraft({ supplierCode: '', products: [normal('a')] }), [{ kind: 'missing_supplier' }])
assert.deepEqual(validateDraft({ supplierCode: undefined, products: [normal('a')] }), [{ kind: 'missing_supplier' }])
assert.deepEqual(validateDraft({ supplierCode: '  ', products: [normal('a')] }), [{ kind: 'missing_supplier' }])
assert.deepEqual(
  validateDraft({
    supplierCode: 'S001',
    products: [
      normal('a'),
      setOf('s-empty', [{ key: 'k', productName: ' ', privateLabelPrice: null }]),
      setOf('s-price-only', [{ key: 'k2', privateLabelPrice: 0 }]),
      setOf('s-none', undefined),
    ],
  }),
  [
    { kind: 'set_without_sub_item', rowKey: 's-empty', rowIndex: 2 },
    { kind: 'set_without_sub_item', rowKey: 's-none', rowIndex: 4 },
  ],
  '子项只要名称或价格有一个就算有效（价格 0 也算填了）',
)

// 超过 10,000：创建套数 5000、带 1 个子项的套装正好 10,000，再多 1 个普通商品就超限
const overLimitProducts: DraftProductItem[] = [
  setOf('big', [{ key: 'k', productName: 'A' }], { createCount: 5000 }), // 5000 × 2 = 10000
  normal('extra'),
]
assert.equal(countExpectedItems(overLimitProducts), MAX_CREATED_ITEMS + 1)
assert.equal(summarizeDraft(overLimitProducts).overLimit, true)
assert.deepEqual(validateDraft({ supplierCode: 'S001', products: overLimitProducts }), [
  { kind: 'over_limit', expected: MAX_CREATED_ITEMS + 1 },
])
assert.equal(
  validateDraft({ supplierCode: 'S001', products: [setOf('big', [{ key: 'k', productName: 'A' }], { createCount: 5000 })] }).length,
  0,
  '恰好 10,000 不超限',
)

// ---- 草稿是否「脏」 ----
const initial = createInitialProducts()
assert.equal(initial.length, 1)
assert.equal(isBatchDraftDirty({ products: initial }), false, '刚打开的工作台返回不需要确认')
assert.equal(isBatchDraftDirty({ supplierCode: 'S001', products: initial }), true)
assert.equal(isBatchDraftDirty({ prefixCode: 'BX', products: initial }), true)
assert.equal(isBatchDraftDirty({ products: [...initial, normal('b')] }), true)
assert.equal(isBatchDraftDirty({ products: [{ ...initial[0], productName: '杯子' }] }), true)
assert.equal(isBatchDraftDirty({ products: [{ ...initial[0], privateLabelPrice: 0 }] }), true, '价格 0 也算填写')
assert.equal(isBatchDraftDirty({ products: [setOf('s', [{ key: 'k' }])] }), true, '只有一个套装行也算有内容')
assert.equal(hasProductContent(normal('a')), false)
assert.equal(hasProductContent(normal('a', { setPrice: 9 })), true)

// ---- 预览货号：只是示意 ----
const previewProducts: DraftProductItem[] = [
  normal('n1'),
  setOf('s1', [{ key: 'k1', productName: 'A' }, { key: 'k2', productName: 'B' }], { createCount: 2 }),
]
const withPrefix = buildPreviewRows(previewProducts, ' BX ')
assert.deepEqual(withPrefix.map((row) => row.itemNumber), ['BX0001', 'BX0002', 'BX0003', 'BX0004', 'BX0005', 'BX0006', 'BX0007'])
assert.equal(withPrefix.length, countExpectedItems(previewProducts), '预览行数等于预计生成货号数')
const noPrefix = buildPreviewRows(previewProducts, '')
assert.equal(noPrefix.length, withPrefix.length)
assert.ok(noPrefix.every((row) => row.itemNumber === ''), '没选前缀时不推演货号（旧实现会显示 0001）')

// ---- 提交请求体 ----
const request = buildSubmitRequest({
  supplierCode: 'S001',
  prefixCode: ' BX ',
  products: [normal('a', { productName: ' 杯子 ', privateLabelPrice: 6.99 }), normal('b')],
})
assert.equal(request.supplierCode, 'S001')
assert.equal(request.prefixCode, 'BX')
assert.equal(request.prefixName, 'BX')
assert.equal(request.items.length, 2)
assert.equal(request.items[0].productName, '杯子')
assert.equal(request.items[1].productName, undefined, '空名称不提交')
assert.equal(request.items[1].privateLabelPrice, undefined, '空零售价不提交')
assert.equal(buildSubmitRequest({ supplierCode: 'S001', prefixCode: '', products: [normal('a')] }).prefixCode, undefined)

const createdAt = new Date('2026-10-06T01:02:03.000Z')
assert.deepEqual(
  buildCreatedBatchInfo({
    response: { batchNumber: 'B1', totalCreated: 8, normalProductCount: 1, setProductCount: 1 },
    supplierCode: 'S001',
    supplierName: '嘉悦',
    prefixCode: 'BX',
    now: createdAt,
  }),
  {
    batchNumber: 'B1',
    supplierCode: 'S001',
    supplierName: '嘉悦',
    prefixCode: 'BX',
    normalCount: 1,
    setCount: 1,
    totalCount: 8,
    createdAt: '2026-10-06T01:02:03.000Z',
  },
)

// ---- 批量命名 ----
const named = [normal('a', { productName: '杯子' }), normal('b')]
assert.deepEqual(applyBatchRename(named, 'replace', '新').map((item) => item.productName), ['新', '新'])
assert.deepEqual(applyBatchRename(named, 'prefix', '前-').map((item) => item.productName), ['前-杯子', '前-'])
assert.deepEqual(applyBatchRename(named, 'suffix', '-后').map((item) => item.productName), ['杯子-后', '-后'])
assert.equal(named[0].productName, '杯子', '不修改原数组')

// ---- 行与子项更新 ----
const rows = [normal('a'), setOf('s', [{ key: 'k1', productName: 'A' }])]
assert.equal(updateProductField(rows, 'a', 'productName', '杯子')[0].productName, '杯子')
assert.equal(rows[0].productName, '', '不修改原数组')
assert.equal(removeProduct(rows, 'a').length, 1)
assert.equal(removeProduct([normal('only')], 'only').length, 1, '最后一行不可删')

const added = addSubItem(rows, 's', { key: 'k2', productName: '' })
assert.equal(added[1].subItems?.length, 2)
assert.equal(added[1].setQuantity, 2, '增删子项后套装数量同步')
const removed = removeSubItem(added, 's', 'k1')
assert.deepEqual(removed[1].subItems?.map((item) => item.key), ['k2'])
assert.equal(removed[1].setQuantity, 1)
const updated = updateSubItemField(removed, 's', 'k2', 'privateLabelPrice', 3.5)
assert.equal(updated[1].subItems?.[0].privateLabelPrice, 3.5)
assert.equal(updated[0], removed[0], '未命中的行保持原引用')

console.log('batchWorkspaceLogic.test: ok')
