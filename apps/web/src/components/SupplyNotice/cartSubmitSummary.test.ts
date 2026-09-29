import assert from 'node:assert/strict'
import {
  getCartSubmitLabel,
  splitLabelsForDisplay,
  summarizeCartForSubmit,
  summarizeKeptLines,
} from './cartSubmitSummary'

assert.deepEqual(
  summarizeCartForSubmit([]),
  { submittableCount: 0, submittableQuantity: 0, submittableImportAmount: 0, pausedLabels: [], discontinuedLabels: [] },
  '空购物车',
)

assert.deepEqual(
  summarizeCartForSubmit([
    { productCode: 'P1', itemNumber: 'A1', isActive: true, quantity: 2, importAmount: 4 },
    { productCode: 'P2', itemNumber: 'A2', isActive: true, quantity: 3, importAmount: 6.5 },
  ]),
  { submittableCount: 2, submittableQuantity: 5, submittableImportAmount: 10.5, pausedLabels: [], discontinuedLabels: [] },
  '全部在供货：数量与金额累计',
)

assert.equal(
  summarizeCartForSubmit([{ productCode: 'P1', quantity: 1 }]).submittableCount,
  1,
  '旧后端不返回 isActive 时视为可提交',
)

const mixed = summarizeCartForSubmit([
  { productCode: 'P-OK', itemNumber: 'OK-1', isActive: true, quantity: 2, importAmount: 4 },
  { productCode: 'P-PAUSE', itemNumber: 'PAUSE-1', isActive: false, supplyPlan: null, quantity: 5, importAmount: 50 },
  { productCode: 'P-STOP', itemNumber: 'STOP-1', isActive: false, supplyPlan: 'Discontinued', quantity: 7, importAmount: 70 },
  { productCode: 'P-SEASON', itemNumber: 'SEASON-1', isActive: false, supplyPlan: 'Seasonal', quantity: 1, importAmount: 1 },
])
assert.equal(mixed.submittableCount, 1, '混合：只有在供货行进单')
assert.equal(mixed.submittableQuantity, 2, '混合：件数只累计可提交行')
assert.equal(mixed.submittableImportAmount, 4, '混合：金额只累计可提交行')
assert.deepEqual(mixed.pausedLabels, ['PAUSE-1', 'SEASON-1'], '暂停供货与季节性都归为保留，顺序按购物车')
assert.deepEqual(mixed.discontinuedLabels, ['STOP-1'], '不再供应单独分组')

assert.deepEqual(
  summarizeCartForSubmit([
    { productCode: 'P1', itemNumber: '', isActive: false },
    { productCode: 'P2', itemNumber: '   ', isActive: false },
    { productCode: 'P3', isActive: false },
  ]).pausedLabels,
  ['P1', 'P2', 'P3'],
  '货号为空或空白时退回商品编码',
)

assert.deepEqual(
  summarizeCartForSubmit([{ productCode: 'P1', itemNumber: 'A1', isActive: true, supplyPlan: 'Discontinued' }]),
  { submittableCount: 1, submittableQuantity: 0, submittableImportAmount: 0, pausedLabels: [], discontinuedLabels: [] },
  '在供货的行即使带脏 supplyPlan 也照常提交，不进任何分组',
)

for (const plan of ['WillRestock', 'Undecided', 'Seasonal', 'Whatever']) {
  const summary = summarizeCartForSubmit([{ productCode: 'P1', itemNumber: 'A1', isActive: false, supplyPlan: plan }])
  assert.deepEqual(summary.pausedLabels, ['A1'], `${plan} 归为暂停供货`)
  assert.deepEqual(summary.discontinuedLabels, [], `${plan} 不算不再供应`)
}

assert.deepEqual(
  summarizeKeptLines([]),
  { submittableCount: 0, submittableQuantity: 0, submittableImportAmount: 0, pausedLabels: [], discontinuedLabels: [] },
  '后端没有保留行',
)

const kept = summarizeKeptLines(
  [
    { productCode: 'P-STOP', itemNumber: 'STOP-1', supplyPlan: 'Discontinued', quantity: 1 },
    { productCode: 'P-PAUSE', itemNumber: 'PAUSE-1', supplyPlan: null, quantity: 2 },
    { productCode: 'P-UNKNOWN', itemNumber: 'UNKNOWN-1', supplyPlan: 'Whatever', quantity: 3 },
  ],
  4,
)
assert.equal(kept.submittableCount, 4, '后端返回的已提交行数透传')
assert.deepEqual(kept.pausedLabels, ['PAUSE-1', 'UNKNOWN-1'], '后端保留行：未知计划归为暂停供货')
assert.deepEqual(kept.discontinuedLabels, ['STOP-1'], '后端保留行：不再供应单独分组')

assert.equal(getCartSubmitLabel({ productCode: 'P1', itemNumber: ' A1 ' }), 'A1', '货号去空白')

assert.deepEqual(splitLabelsForDisplay(['a', 'b', 'c'], 10), { shown: ['a', 'b', 'c'], more: 0 }, '不超过上限全部展示')
assert.deepEqual(
  splitLabelsForDisplay(Array.from({ length: 12 }, (_, index) => `L${index + 1}`)),
  { shown: ['L1', 'L2', 'L3', 'L4', 'L5', 'L6', 'L7', 'L8', 'L9', 'L10'], more: 2 },
  '超过 10 个时折叠余下数量',
)

console.log('cartSubmitSummary tests passed')
