import assert from 'node:assert/strict'

import type { SupplierPreorderMatrixTotals, SupplierPreorderPreviewItem } from '../../../types/supplierPreorder'

import {
  EMPTY_MAPPING,
  buildDefaultFormTitle,
  buildEnabledStoreGuids,
  canDeleteForm,
  filterPreviewItems,
  findDuplicateMappedColumns,
  formatCbm,
  formatMoney,
  formatPriceChangeRate,
  formatSydneyDateTime,
  getColumnLetter,
  getDeadlineInfo,
  getDefaultHistoryRange,
  getImportFileKind,
  getMatrixCellValue,
  getMatrixStoreTotal,
  getMissingRequiredMappings,
  getNotStartedStoreCount,
  isAbortError,
  normalizeDateOnly,
  normalizeMapping,
  resolveContainerPreset,
  splitPastedSheet,
  storeHasOrderData,
  sydneyDateEndToUtcIso,
  utcIsoToSydneyDate,
} from './supplierPreorderAdmin.logic'

// ---------- 粘贴拆分 ----------

// Excel 复制多行单元格时会加引号；引号里的换行和制表符属于同一个单元格，"" 是转义的引号
const pasted = 'Seq\tStock Code\tDescription\tPrice\r\n1\tXD0001815\t"Flocked Rib\nGlitter ""Bauble"""\t1.10\r\n2\tXD0001817\tLantern\r\n\r\n'
assert.deepEqual(splitPastedSheet(pasted), [
  ['Seq', 'Stock Code', 'Description', 'Price'],
  ['1', 'XD0001815', 'Flocked Rib\nGlitter "Bauble"', '1.10'],
  // 短行补齐到同一列数，保证列下标一致
  ['2', 'XD0001817', 'Lantern', ''],
])
assert.deepEqual(splitPastedSheet('   \n  '), [])
// 单元格首尾空格去掉，内部保留
assert.deepEqual(splitPastedSheet(' A \t B C '), [['A', 'B C']])

assert.equal(getImportFileKind('QT0013074.xlsx'), 'excel')
assert.equal(getImportFileKind('Dats 2026.XLSM'), 'excel')
assert.equal(getImportFileKind('old.xls'), 'xls')
assert.equal(getImportFileKind('list.csv'), 'unsupported')

// ---------- 列映射 ----------

assert.equal(getColumnLetter(0), 'A')
assert.equal(getColumnLetter(25), 'Z')
assert.equal(getColumnLetter(26), 'AA')
assert.equal(getColumnLetter(27), 'AB')
assert.equal(getColumnLetter(701), 'ZZ')
assert.equal(getColumnLetter(702), 'AAA')
assert.equal(getColumnLetter(-1), '')

assert.deepEqual(getMissingRequiredMappings(EMPTY_MAPPING), ['itemCode', 'name', 'price', 'cartonQty'])
assert.deepEqual(getMissingRequiredMappings({ ...EMPTY_MAPPING, itemCode: 2, name: 3, price: 0, cartonQty: 5 }), [])

assert.deepEqual(findDuplicateMappedColumns({ ...EMPTY_MAPPING, itemCode: 2, prevYearItemCode: 2, name: 3, barcode: 3, price: 4 }), [
  { column: 2, keys: ['itemCode', 'prevYearItemCode'] },
  { column: 3, keys: ['name', 'barcode'] },
])

// 后端少返回键或返回非法值时补成 null
const normalized = normalizeMapping({ itemCode: 2, name: -1 } as never)
assert.equal(normalized.itemCode, 2)
assert.equal(normalized.name, null)
assert.equal(normalized.orderQty, null)

// ---------- 校对预览 ----------

const previewBase: SupplierPreorderPreviewItem = {
  rowNo: 1,
  seq: 1,
  supplierItemCode: 'X',
  name: 'n',
  cartonQty: 24,
  price: 1,
  matchMethod: 'None',
  status: 'New',
  issues: [],
}
const previewItems: SupplierPreorderPreviewItem[] = [
  { ...previewBase, rowNo: 1, status: 'Matched', matchMethod: 'Barcode', priceChangeRate: 0.059 },
  { ...previewBase, rowNo: 2, status: 'New' },
  { ...previewBase, rowNo: 3, status: 'Warning', priceChangeRate: 0.0001 },
  { ...previewBase, rowNo: 4, status: 'Invalid' },
]
assert.deepEqual(filterPreviewItems(previewItems, 'all').map((item) => item.rowNo), [1, 2, 3, 4])
assert.deepEqual(filterPreviewItems(previewItems, 'matched').map((item) => item.rowNo), [1])
assert.deepEqual(filterPreviewItems(previewItems, 'new').map((item) => item.rowNo), [2])
assert.deepEqual(filterPreviewItems(previewItems, 'warning').map((item) => item.rowNo), [3])
assert.deepEqual(filterPreviewItems(previewItems, 'invalid').map((item) => item.rowNo), [4])
// 0.01% 的浮点误差不算进价变动
assert.deepEqual(filterPreviewItems(previewItems, 'priceChanged').map((item) => item.rowNo), [1])
assert.equal(formatPriceChangeRate(0.059), '+5.9%')
assert.equal(formatPriceChangeRate(-0.049), '-4.9%')

// ---------- 悉尼日期 ----------

// 夏令时（AEDT +11）：悉尼 3/6 23:59:59 = UTC 3/6 12:59:59
assert.equal(sydneyDateEndToUtcIso('2026-03-06'), '2026-03-06T12:59:59.000Z')
// 标准时（AEST +10）
assert.equal(sydneyDateEndToUtcIso('2026-06-30'), '2026-06-30T13:59:59.000Z')
// 夏令时结束当天（2026-04-05 凌晨 3 点回拨），当晚已是 +10
assert.equal(sydneyDateEndToUtcIso('2026-04-05'), '2026-04-05T13:59:59.000Z')
// 夏令时开始当天（2026-10-04 凌晨 2 点拨快），当晚已是 +11
assert.equal(sydneyDateEndToUtcIso('2026-10-04'), '2026-10-04T12:59:59.000Z')
assert.equal(sydneyDateEndToUtcIso('bad'), null)

assert.equal(utcIsoToSydneyDate('2026-03-06T12:59:59.000Z'), '2026-03-06')
// UTC 当天下午已是悉尼次日
assert.equal(utcIsoToSydneyDate('2026-03-06T14:00:00Z'), '2026-03-07')
assert.equal(utcIsoToSydneyDate(null), null)
assert.equal(formatSydneyDateTime('2026-02-20T05:40:00Z'), '2026-02-20 16:40')
assert.equal(formatSydneyDateTime(null), '--')

assert.equal(normalizeDateOnly('2026-02-05T00:00:00'), '2026-02-05')
assert.equal(normalizeDateOnly('2026-02-05'), '2026-02-05')
assert.equal(normalizeDateOnly(''), null)
assert.equal(normalizeDateOnly('Feb 5'), null)

// 截止剩余天数按悉尼日历日
const deadline = '2026-03-06T12:59:59.000Z' // 悉尼 3/6 23:59:59
assert.deepEqual(getDeadlineInfo(null, Date.parse('2026-03-01T00:00:00Z')), { state: 'none', daysLeft: null, urgent: false })
// 悉尼 2/22 早上 → 剩 12 天
assert.deepEqual(getDeadlineInfo(deadline, Date.parse('2026-02-21T22:00:00Z')), { state: 'days', daysLeft: 12, urgent: false })
// 悉尼 3/3 → 剩 3 天，琥珀色
assert.deepEqual(getDeadlineInfo(deadline, Date.parse('2026-03-03T01:00:00Z')), { state: 'days', daysLeft: 3, urgent: true })
// 悉尼 3/6 上午 → 今天截止
assert.deepEqual(getDeadlineInfo(deadline, Date.parse('2026-03-05T22:00:00Z')), { state: 'today', daysLeft: 0, urgent: true })
// 已过
assert.deepEqual(getDeadlineInfo(deadline, Date.parse('2026-03-06T13:00:00Z')), { state: 'passed', daysLeft: null, urgent: false })

// ---------- 订单表信息默认值 ----------

assert.equal(buildDefaultFormTitle('Uncle Bills (KFC)', 'Christmas', 2026), 'Uncle Bills (KFC) · Christmas 2026')
assert.equal(buildDefaultFormTitle('', 'Halloween', 2026), 'Halloween 2026')
assert.equal(buildDefaultFormTitle('Dats', 'Custom', 2026), 'Dats · 2026')

assert.deepEqual(getDefaultHistoryRange('Christmas', 2026), { from: '2025-09-01', to: '2026-01-18' })
assert.deepEqual(getDefaultHistoryRange('Halloween', 2026), { from: '2025-08-01', to: '2025-11-07' })
assert.equal(getDefaultHistoryRange('Easter', 2026), null)
assert.equal(getDefaultHistoryRange('Christmas', null), null)

assert.equal(resolveContainerPreset('40GP'), '40GP')
assert.equal(resolveContainerPreset('1 x 40 GP'), '40GP')
assert.equal(resolveContainerPreset('20gp'), '20GP')
assert.equal(resolveContainerPreset('40HC'), 'custom')
assert.equal(resolveContainerPreset(''), 'none')
assert.equal(resolveContainerPreset(null), 'none')

// ---------- 分店开启 ----------

const stores = [
  { storeGuid: 'a', enabled: true },
  { storeGuid: 'b', enabled: false },
  { storeGuid: 'c', enabled: true },
]
assert.deepEqual(buildEnabledStoreGuids(stores, 'b', true), ['a', 'b', 'c'])
assert.deepEqual(buildEnabledStoreGuids(stores, 'a', false), ['c'])
assert.deepEqual(buildEnabledStoreGuids(stores, 'z', true), ['a', 'c', 'z'])

assert.equal(storeHasOrderData({ orderStatus: 'None', cartons: 0, itemCount: 0 }), false)
assert.equal(storeHasOrderData({ orderStatus: 'Draft', cartons: 0, itemCount: 0 }), true)
assert.equal(storeHasOrderData({ orderStatus: 'None', cartons: 3, itemCount: 1 }), true)

assert.equal(getNotStartedStoreCount({ enabledStoreCount: 6, submittedStoreCount: 3, draftStoreCount: 2 }), 1)
assert.equal(getNotStartedStoreCount({ enabledStoreCount: 0, submittedStoreCount: 1, draftStoreCount: 0 }), 0)
assert.equal(canDeleteForm({ submittedStoreCount: 0, draftStoreCount: 0, totalCartons: 0 }), true)
assert.equal(canDeleteForm({ submittedStoreCount: 0, draftStoreCount: 1, totalCartons: 0 }), false)

// ---------- 矩阵 ----------

const row = { storeCartons: { orion: 4, kawana: 3 }, cartonQty: 24, price: 1.1 }
assert.equal(getMatrixCellValue(row, 'orion', 'cartons'), 4)
assert.equal(getMatrixCellValue(row, 'orion', 'pieces'), 96)
// 4 × 24 × 1.10 = 105.60（浮点要四舍五入到分）
assert.equal(getMatrixCellValue(row, 'orion', 'amount'), 105.6)
assert.equal(getMatrixCellValue(row, 'missing', 'amount'), 0)
assert.equal(getMatrixCellValue({ storeCartons: { s: 3 }, cartonQty: 12, price: 7.21 }, 's', 'amount'), 259.56)

const totals: SupplierPreorderMatrixTotals = {
  storeCartons: { orion: 112 },
  storePieces: { orion: 2688 },
  storeAmount: { orion: 4912.36 },
  storeCbm: { orion: 9.4 },
  orderedItemCount: 412,
  totalCartons: 420,
  totalPieces: 8964,
  totalAmount: 18481.25,
  totalCbm: 41.8,
}
assert.equal(getMatrixStoreTotal(totals, 'orion', 'cartons'), 112)
assert.equal(getMatrixStoreTotal(totals, 'orion', 'pieces'), 2688)
assert.equal(getMatrixStoreTotal(totals, 'orion', 'amount'), 4912.36)
assert.equal(getMatrixStoreTotal(totals, 'none', 'amount'), 0)

// ---------- 格式化 ----------

assert.equal(formatMoney(18481.25), '$18,481.25')
assert.equal(formatMoney(0), '$0.00')
assert.equal(formatMoney(-3.5), '-$3.50')
assert.equal(formatMoney(null), '--')
assert.equal(formatCbm(0.052), '0.052')
assert.equal(formatCbm(41.8), '41.8')
assert.equal(formatCbm(9.4000001), '9.4')

assert.equal(isAbortError({ name: 'AbortError' }), true)
assert.equal(isAbortError(new Error('x')), false)
