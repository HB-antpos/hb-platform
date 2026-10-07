import assert from 'node:assert/strict'
import type { StoreOrderImportPriceVarianceSupplierSummary } from '../../../types/storeOrder'
import {
  MINUS_SIGN,
  SUPPLIER_PREVIEW_COUNT,
  formatAmount,
  formatQuantity,
  formatSignedAmount,
  getFilterSignature,
  getMaxAbsVariance,
  getSupplierRankingKey,
  getSupplierRowKey,
  getVarianceBarWidths,
  getVarianceTone,
  getVisibleSupplierRows,
  sortSupplierSummaries,
  summarizeSupplierTotals,
} from './priceVariance.logic'

function supplier(overrides: Partial<StoreOrderImportPriceVarianceSupplierSummary>): StoreOrderImportPriceVarianceSupplierSummary {
  return {
    supplierCode: 'S',
    supplierName: '供应商',
    productCount: 1,
    detailCount: 1,
    originalImportAmountTotal: 0,
    baselineImportAmountTotal: 0,
    increaseVarianceAmountTotal: 0,
    decreaseVarianceAmountTotal: 0,
    varianceAmountTotal: 0,
    ...overrides,
  }
}

// 金额格式：千分位 + 两位小数；负数用真正的减号；带符号差额区分多收 / 少收。
assert.equal(formatAmount(1284302.15), '$1,284,302.15')
assert.equal(formatAmount(-8674.55), `${MINUS_SIGN}$8,674.55`)
assert.equal(formatAmount(8.6, '¥'), '¥8.60')
assert.equal(formatAmount(undefined), '--')
assert.equal(formatSignedAmount(41206.3), '+$41,206.30')
assert.equal(formatSignedAmount(-8674.55), `${MINUS_SIGN}$8,674.55`)
assert.equal(formatSignedAmount(0.001), '$0.00', '浮点误差不能显示成带符号的 0')
assert.equal(formatSignedAmount(undefined), '$0.00')
assert.equal(formatQuantity(3240), '3,240')
assert.equal(formatQuantity(12.5), '12.50')

// 差额方向：正数多收、负数少收、零无方向。
assert.equal(getVarianceTone(12), 'over')
assert.equal(getVarianceTone(-0.5), 'under')
assert.equal(getVarianceTone(0.004), 'none')
assert.equal(getVarianceTone(undefined), 'none')

// 双向条：按最大绝对值归一，正数画右半边、负数画左半边。
assert.equal(getMaxAbsVariance([12, -40, undefined, 5]), 40)
assert.deepEqual(getVarianceBarWidths(20, 40), { over: 50, under: 0 })
assert.deepEqual(getVarianceBarWidths(-40, 40), { over: 0, under: 100 })
assert.deepEqual(getVarianceBarWidths(5, 0), { over: 0, under: 0 })
assert.deepEqual(getVarianceBarWidths(0, 40), { over: 0, under: 0 })

// 供应商合计：和总汇总一致时才用来推出明细行 / 多收 / 少收；对不上说明不是全量，不显示。
{
  const rows = [
    supplier({ supplierCode: 'A', detailCount: 10, originalImportAmountTotal: 100.1, increaseVarianceAmountTotal: 30.2, decreaseVarianceAmountTotal: 5.1, varianceAmountTotal: 25.1 }),
    supplier({ supplierCode: 'B', detailCount: 4, originalImportAmountTotal: 50.2, increaseVarianceAmountTotal: 0, decreaseVarianceAmountTotal: 12.3, varianceAmountTotal: -12.3 }),
  ]
  const totals = summarizeSupplierTotals({ originalImportAmountTotal: 150.3, varianceAmountTotal: 12.8 }, rows)
  assert(totals)
  assert.equal(totals.detailRows, 14)
  assert.equal(Math.round(totals.increaseTotal * 100) / 100, 30.2)
  assert.equal(Math.round(totals.decreaseTotal * 100) / 100, 17.4)
  assert.equal(summarizeSupplierTotals({ originalImportAmountTotal: 999, varianceAmountTotal: 12.8 }, rows), null)
  assert.equal(summarizeSupplierTotals({ originalImportAmountTotal: 150.3, varianceAmountTotal: 99 }, rows), null)
  assert.deepEqual(
    summarizeSupplierTotals({ originalImportAmountTotal: 0, varianceAmountTotal: 0 }, []),
    { detailRows: 0, increaseTotal: 0, decreaseTotal: 0 },
    '无数据时合计为 0',
  )
}

// 供应商排序：默认按净差额绝对值倒序；点列头按该列排序，同值按编码稳定排序。
{
  const rows = [
    supplier({ supplierCode: 'B', supplierName: '乙', productCount: 3, varianceAmountTotal: -50 }),
    supplier({ supplierCode: 'A', supplierName: '甲', productCount: 3, varianceAmountTotal: 80 }),
    supplier({ supplierCode: 'C', supplierName: '丙', productCount: 9, varianceAmountTotal: 50 }),
  ]
  assert.deepEqual(sortSupplierSummaries(rows, null).map(getSupplierRowKey), ['A', 'B', 'C'])
  assert.deepEqual(sortSupplierSummaries(rows, { key: 'net', order: 'ascend' }).map(getSupplierRowKey), ['B', 'C', 'A'])
  assert.deepEqual(sortSupplierSummaries(rows, { key: 'productCount', order: 'descend' }).map(getSupplierRowKey), ['C', 'A', 'B'])
  assert.deepEqual(rows.map(getSupplierRowKey), ['B', 'A', 'C'], '排序不能改动原数组')
  assert.equal(getSupplierRowKey({ supplierCode: undefined, supplierName: undefined }), 'unknown-supplier')
}

// 收起时只显示前 N 个；选中的供应商在 N 名以外时补在末尾。
{
  const rows = Array.from({ length: SUPPLIER_PREVIEW_COUNT + 3 }, (_, index) => supplier({ supplierCode: `S${index}` }))
  assert.equal(getVisibleSupplierRows(rows, false).length, SUPPLIER_PREVIEW_COUNT)
  assert.equal(getVisibleSupplierRows(rows, true).length, rows.length)
  const withSelected = getVisibleSupplierRows(rows, false, `S${SUPPLIER_PREVIEW_COUNT + 1}`)
  assert.equal(withSelected.length, SUPPLIER_PREVIEW_COUNT + 1)
  assert.equal(withSelected[withSelected.length - 1]?.supplierCode, `S${SUPPLIER_PREVIEW_COUNT + 1}`)
  assert.equal(getVisibleSupplierRows(rows, false, 'S0').length, SUPPLIER_PREVIEW_COUNT)
}

// 筛选签名：忽略空值与键顺序；供应商排行键不含国内供应商。
assert.equal(
  getFilterSignature({ keyword: 'abc', storeCode: undefined, varianceDirection: 'all' }),
  getFilterSignature({ varianceDirection: 'all', keyword: 'abc', orderNo: '' }),
)
assert.notEqual(getFilterSignature({ keyword: 'abc' }), getFilterSignature({ keyword: 'abd' }))
assert.equal(
  getSupplierRankingKey({ keyword: 'abc', supplierCode: 'OY0412', varianceDirection: 'all' }),
  getSupplierRankingKey({ keyword: 'abc', varianceDirection: 'all' }),
)
assert.notEqual(
  getSupplierRankingKey({ keyword: 'abc', startDate: '2026-07-01' }),
  getSupplierRankingKey({ keyword: 'abc' }),
)

console.log('priceVariance.logic.test: ok')
