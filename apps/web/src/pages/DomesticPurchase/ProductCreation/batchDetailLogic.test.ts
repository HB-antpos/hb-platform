import assert from 'node:assert/strict'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { BatchProductItem } from '../../../types/domesticProductCreation'
import {
  buildDetailRows,
  computeChangedPrices,
  formatCopyText,
  getDetailTabCounts,
  getDisplayedPrice,
  isPriceChanged,
  isSamePrice,
  setEditedPrice,
  settleEditedPrice,
} from './batchDetailLogic'

const item = (
  itemNumber: string,
  hbProductNo: string,
  productType: ProductCreationType,
  extra: Partial<BatchProductItem> = {},
): BatchProductItem => ({
  itemNumber,
  hbProductNo,
  barcode: `69${hbProductNo.replace(/\D/g, '')}`,
  productName: `名称-${hbProductNo}`,
  productType,
  ...extra,
})

const items: BatchProductItem[] = [
  item('r-8106', 'HB012-8106', ProductCreationType.NORMAL, { privateLabelPrice: 4.99 }),
  item('r-8104', 'HB012-8104', ProductCreationType.SET_SUB_ITEM, { privateLabelPrice: 2, parentItemNumber: 'HB012-8103' }),
  item('r-8101', 'HB012-8101', ProductCreationType.NORMAL, { privateLabelPrice: 6.99 }),
  item('r-8103', 'HB012-8103', ProductCreationType.SET, { privateLabelPrice: 19.99, setQuantity: 2 }),
  item('r-8105', 'HB012-8105', ProductCreationType.SET_SUB_ITEM, { parentItemNumber: 'HB012-8103' }),
  item('r-8102', 'HB012-8102', ProductCreationType.NORMAL, { privateLabelPrice: undefined }),
  item('r-orphan', 'HB012-9000', ProductCreationType.SET_SUB_ITEM, { parentItemNumber: 'HB012-MISSING' }),
]

// ---- 页签与行序 ----
assert.deepEqual(getDetailTabCounts(items), { all: 7, normal: 3, set: 1 })
assert.deepEqual(
  buildDetailRows(items, 'all').map((row) => row.hbProductNo),
  ['HB012-8101', 'HB012-8102', 'HB012-8103', 'HB012-8104', 'HB012-8105', 'HB012-8106', 'HB012-9000'],
  '全部：按货号排序，子项紧跟套装，孤儿子项排最后',
)
assert.deepEqual(buildDetailRows(items, 'normal').map((row) => row.hbProductNo), ['HB012-8101', 'HB012-8102', 'HB012-8106'])
assert.deepEqual(
  buildDetailRows(items, 'set').map((row) => row.hbProductNo),
  ['HB012-8103', 'HB012-8104', 'HB012-8105', 'HB012-9000'],
  '套装：套装 + 它的子项，找不到父级的子项也不丢',
)
assert.equal(items[0].hbProductNo, 'HB012-8106', '不修改入参顺序')

// ---- 价格：只提交改过的 ----
assert.equal(isSamePrice(6.99, 6.990000001), true)
assert.equal(isSamePrice(undefined, undefined), true)
assert.equal(isSamePrice(undefined, 0), false, '没价格 与 价格 0 不是同一个状态')
assert.equal(isSamePrice(7, 6.99), false)

assert.deepEqual(computeChangedPrices(items, {}), [], '没动过任何价格时不提交任何行（旧实现会把全部行都提交）')
assert.deepEqual(
  computeChangedPrices(items, { 'r-8101': 6.99, 'r-8103': 20, 'r-8102': 3.5, 'r-8106': null }),
  [
    { itemNumber: 'r-8103', privateLabelPrice: 20 },
    { itemNumber: 'r-8102', privateLabelPrice: 3.5 },
  ],
  '与原价相同的行、被清空的行都不提交；原来没价格的行补上价格算改动',
)
assert.deepEqual(
  computeChangedPrices(items, { 'r-8104': 0 }),
  [{ itemNumber: 'r-8104', privateLabelPrice: 0 }],
  '用户明确输入 0 是合法改动（区别于「清空」）',
)
assert.deepEqual(computeChangedPrices(items, { 'not-in-batch': 5 }), [], '不属于当前明细的行不提交')

// 输入过程：清空后允许暂时为空，失焦时还原；改回原价也会撤销改动标记
let edited = setEditedPrice({}, 'r-8101', 7.49)
assert.equal(getDisplayedPrice(items[2], edited), 7.49)
assert.equal(isPriceChanged(items[2], edited), true)
edited = setEditedPrice(edited, 'r-8101', null)
assert.equal(getDisplayedPrice(items[2], edited), null, '输入过程中允许为空')
assert.equal(isPriceChanged(items[2], edited), false, '清空不算改动，不会存成 0')
edited = settleEditedPrice(edited, items, 'r-8101')
assert.deepEqual(edited, {}, '失焦时清空的草稿被移除')
assert.equal(getDisplayedPrice(items[2], edited), 6.99, '回到服务端原价')
edited = settleEditedPrice(setEditedPrice({}, 'r-8101', 6.99), items, 'r-8101')
assert.deepEqual(edited, {}, '改回原价等于没改')
const keep = setEditedPrice({}, 'r-8101', 8)
assert.equal(settleEditedPrice(keep, items, 'r-8101'), keep, '真正改过的草稿保持原引用')
assert.equal(settleEditedPrice(keep, items, 'r-8103'), keep, '没有草稿的行直接返回')

// ---- 复制：制表符分列 ----
const copyText = formatCopyText(buildDetailRows(items, 'set'), { itemNumber: '货号', barcode: '条码' })
const copyLines = copyText.split('\n')
assert.equal(copyLines[0], '货号\t条码')
assert.equal(copyLines[1], 'HB012-8103\t690128103')
assert.equal(copyLines.length, 5)
assert.ok(copyLines.every((line) => line.split('\t').length === 2), '每行恰好两列，用制表符分隔')
assert.equal(copyText.includes(' '), false, '不再用空格拼接')
assert.equal(formatCopyText([], { itemNumber: '货号', barcode: '条码' }), '')

console.log('batchDetailLogic.test: ok')
