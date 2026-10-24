import assert from 'node:assert/strict'

import type { SupplierPreorderPreviewItem, SupplierPreorderRefCandidate } from '../../../types/supplierPreorder'

import {
  HISTORY_POLL_INTERVAL_MS,
  buildRefOverrides,
  canRecomputeHistory,
  collectMatchedProductCodes,
  formatRefScore,
  getHistoryVersionKey,
  getHistoryWindow,
  getRefCellState,
  getRefDisplayCode,
  getRefPickerActions,
  getRefPriceDiffRate,
  importChoiceToItemRef,
  resolveImportRefContext,
  shouldPollHistory,
  toImportRefChoice,
  type ImportRefChoice,
} from './refPicker.logic'

// ---------- 去年历史状态 ----------

assert.equal(HISTORY_POLL_INTERVAL_MS, 15_000)
assert.equal(shouldPollHistory('Queued'), true)
assert.equal(shouldPollHistory('Running'), true)
for (const status of ['None', 'Ready', 'Failed', 'NoWindow'] as const) {
  assert.equal(shouldPollHistory(status), false, status)
  assert.equal(canRecomputeHistory(status), true, status)
}
assert.equal(shouldPollHistory(undefined), false)
assert.equal(canRecomputeHistory('Running'), false)

// 窗口优先取快照记下的，没有时退回订单表的历史对比季；缺一端不显示
assert.deepEqual(getHistoryWindow({ windowFrom: '2025-09-01T00:00:00', windowTo: '2026-01-18T00:00:00' }, '2024-09-01', '2025-01-18'), {
  from: '2025-09-01',
  to: '2026-01-18',
})
assert.deepEqual(getHistoryWindow({ windowFrom: null, windowTo: null }, '2025-08-01T00:00:00', '2025-11-07'), { from: '2025-08-01', to: '2025-11-07' })
assert.equal(getHistoryWindow({ windowFrom: '2025-08-01', windowTo: null }, null, null), null)
assert.equal(getHistoryWindow(null), null)

// 版本键：状态或完成时间变了才变
assert.equal(getHistoryVersionKey({ status: 'Ready', computedAtUtc: '2026-10-23T01:00:00Z' }), 'Ready|2026-10-23T01:00:00Z')
assert.notEqual(getHistoryVersionKey({ status: 'Queued', computedAtUtc: null }), getHistoryVersionKey({ status: 'Ready', computedAtUtc: null }))
assert.equal(getHistoryVersionKey(undefined), 'none')

// ---------- 参考列显示状态 ----------

assert.equal(getRefCellState({ ownSalesQty: 0, ref: { source: 'Auto', productCode: 'P1', score: 0.72 } }), 'auto')
assert.equal(getRefCellState({ ownSalesQty: null, ref: { source: 'Manual', productCode: 'P1' } }), 'manual')
// 人工给有历史的商品也指定了参考：显示参考
assert.equal(getRefCellState({ ownSalesQty: 30, ref: { source: 'Manual', productCode: 'P1' } }), 'manual')
assert.equal(getRefCellState({ ownSalesQty: 0, ref: { source: 'Cleared', productCode: null } }), 'cleared')
// 来源是 Auto 但没有商品编码（数据不完整）：按没有参考处理
assert.equal(getRefCellState({ ownSalesQty: 0, ref: { source: 'Auto', productCode: null } }), 'none')
assert.equal(getRefCellState({ ownSalesQty: 12, ref: null }), 'own')
assert.equal(getRefCellState({ ownSalesQty: 0, ref: null }), 'none')
assert.equal(getRefCellState({ ownSalesQty: null }), 'pending')
assert.equal(getRefCellState({}), 'pending')

assert.equal(getRefDisplayCode({ itemNumber: 'XD0001342', productCode: 'abc' }), 'XD0001342')
assert.equal(getRefDisplayCode({ itemNumber: '  ', productCode: 'abc' }), 'abc')
assert.equal(getRefDisplayCode(null), '')

// ---------- 格式化 ----------

assert.equal(formatRefScore(0.723), '0.72')
assert.equal(formatRefScore(0.6), '0.60')
assert.equal(formatRefScore(1), '1.00')
assert.equal(formatRefScore(null), '--')
assert.equal(formatRefScore(Number.NaN), '--')

// 参考 $1.00 → 本品 $1.10：+10%
assert.ok(Math.abs((getRefPriceDiffRate(1, 1.1) ?? 0) - 0.1) < 1e-9)
assert.ok(Math.abs((getRefPriceDiffRate(1.2, 0.9) ?? 0) + 0.25) < 1e-9)
assert.equal(getRefPriceDiffRate(0, 1), null)
assert.equal(getRefPriceDiffRate(null, 1), null)
assert.equal(getRefPriceDiffRate(1, undefined), null)

// ---------- 弹窗操作 ----------

assert.deepEqual(getRefPickerActions(null), { canClear: true, canAuto: false })
assert.deepEqual(getRefPickerActions('Auto'), { canClear: true, canAuto: false })
assert.deepEqual(getRefPickerActions('Manual'), { canClear: true, canAuto: true })
assert.deepEqual(getRefPickerActions('Cleared'), { canClear: false, canAuto: true })

// ---------- 导入校对页：人工指定 ----------

const candidate: SupplierPreorderRefCandidate = {
  productCode: 'P-1342',
  itemNumber: 'XD0001342',
  barcode: '9300000000001',
  name: 'Bottle Bag Gold Foil',
  purchasePrice: 1.02,
  imageUrl: 'https://img/1.jpg',
  localSupplierCode: 'S01',
  salesQty: 820,
  purchaseQty: 960,
  score: 0.71,
  nameScore: 0.8,
  priceScore: 0.5,
  eligible: true,
  hasHistory: true,
}
const manual = toImportRefChoice(candidate)
assert.deepEqual(manual, {
  kind: 'manual',
  productCode: 'P-1342',
  itemNumber: 'XD0001342',
  name: 'Bottle Bag Gold Foil',
  purchasePrice: 1.02,
  imageUrl: 'https://img/1.jpg',
  salesQty: 820,
})
assert.equal(importChoiceToItemRef(manual)?.source, 'Manual')
assert.equal(importChoiceToItemRef(manual)?.productCode, 'P-1342')
assert.deepEqual(importChoiceToItemRef({ kind: 'cleared' }), { source: 'Cleared', productCode: null })
assert.equal(importChoiceToItemRef(undefined), null)

type PreviewRow = Pick<SupplierPreorderPreviewItem, 'rowNo' | 'status' | 'matchedProductCode'>
const previewRows: PreviewRow[] = [
  { rowNo: 5, status: 'New' },
  { rowNo: 6, status: 'Matched', matchedProductCode: 'P-6' },
  { rowNo: 7, status: 'Invalid' },
  { rowNo: 8, status: 'Warning', matchedProductCode: ' P-6 ' },
  { rowNo: 9, status: 'Matched', matchedProductCode: 'P-9' },
  { rowNo: 10, status: 'Invalid', matchedProductCode: 'P-10' },
]
const choices: Record<number, ImportRefChoice> = {
  9: { kind: 'cleared' },
  5: manual,
  // 不能导入的行、预览里已经没有的行都不提交
  7: manual,
  42: manual,
}
assert.deepEqual(buildRefOverrides(choices, previewRows), [
  { rowNo: 5, productCode: 'P-1342' },
  { rowNo: 9, productCode: null },
])
assert.deepEqual(buildRefOverrides({}, previewRows), [])

// 已匹配的我方商品去重、去空格，跳过不能导入的行
assert.deepEqual(collectMatchedProductCodes(previewRows), ['P-6', 'P-9'])

// 季节上下文：表单值优先，其次表头，缺了就提示
assert.deepEqual(
  resolveImportRefContext({
    supplierCode: ' S01 ',
    holidayType: 'Halloween',
    seasonYear: 2026,
    historyFrom: '2025-08-01',
    historyTo: '2025-11-07',
    headerInfo: { holidayType: 'Christmas', seasonYear: 2027 },
  }),
  { ok: true, context: { localSupplierCode: 'S01', holidayType: 'Halloween', seasonYear: 2026, historyFrom: '2025-08-01', historyTo: '2025-11-07' } },
)
assert.deepEqual(resolveImportRefContext({ supplierCode: 'S01', headerInfo: { holidayType: 'Christmas', seasonYear: 2026 } }), {
  ok: true,
  context: { localSupplierCode: 'S01', holidayType: 'Christmas', seasonYear: 2026, historyFrom: null, historyTo: null },
})
// 历史窗口只给了一端：两端都不传，让后端按默认算
const halfWindow = resolveImportRefContext({ supplierCode: 'S01', holidayType: 'Christmas', seasonYear: 2026, historyFrom: '2025-09-01' })
assert.ok(halfWindow.ok && halfWindow.context.historyFrom === null && halfWindow.context.historyTo === null)
assert.deepEqual(resolveImportRefContext({ supplierCode: '', holidayType: 'Christmas', seasonYear: 2026 }), { ok: false, reason: 'supplier' })
assert.deepEqual(resolveImportRefContext({ supplierCode: 'S01', seasonYear: 2026 }), { ok: false, reason: 'season' })
assert.deepEqual(resolveImportRefContext({ supplierCode: 'S01', holidayType: 'Christmas', headerInfo: {} }), { ok: false, reason: 'season' })
assert.deepEqual(resolveImportRefContext({ supplierCode: 'S01', holidayType: 'Christmas', seasonYear: 26 }), { ok: false, reason: 'season' })

console.log('refPicker.logic tests passed')
