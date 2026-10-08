import assert from 'node:assert/strict'

import type {
  SeasonalCardBatch,
  SeasonalCardStatsPriceQuantity,
  SeasonalCardStatsStoreRow,
} from '../../../types/seasonalCardStats'

import {
  MINUS_SIGN,
  averagePerFilledStore,
  buildExportFileName,
  buildFooterTotals,
  buildPriceBars,
  buildSummaryQuery,
  buildSupplierMatrix,
  buildUnfilledCopyText,
  buildYearComparison,
  buildYearOptions,
  classifyStatsError,
  countByStatus,
  createAbortableRequestGuard,
  createDefaultFilters,
  fillPercent,
  filterRowsByStatus,
  formatBatchLines,
  formatCount,
  formatLocalTime,
  formatMoney,
  formatSignedCount,
  formatSupplierList,
  hasQuantityFilter,
  isAbortError,
  isCardType,
  isPriceOption,
  isStatusFilter,
  mergeStoreUniverse,
  parseStoreParam,
  parseUtcTimestamp,
  sumMoney,
  withStoreParam,
} from './logic'

const prices = (q1: number, q2: number, q3: number, q4: number, amount4 = 0): SeasonalCardStatsPriceQuantity[] => [
  { priceOption: 1, priceLabel: '$1', quantity: q1, amount: q1 },
  { priceOption: 2, priceLabel: '$2', quantity: q2, amount: q2 * 2 },
  { priceOption: 3, priceLabel: '$3', quantity: q3, amount: q3 * 3 },
  { priceOption: 4, priceLabel: '其他', quantity: q4, amount: amount4 },
]

function row(storeCode: string, filled: boolean, q: [number, number, number, number] = [0, 0, 0, 0], amount4 = 0): SeasonalCardStatsStoreRow {
  const list = prices(...q, amount4)
  return {
    storeCode,
    storeName: `Store ${storeCode}`,
    isFilled: filled,
    prices: list,
    totalQuantity: list.reduce((sum, item) => sum + item.quantity, 0),
    totalAmount: list.reduce((sum, item) => sum + item.amount, 0),
    suppliers: filled ? [{ localSupplierCode: 'SUP-A', supplierName: 'Alpha' }] : [],
    lastSubmittedAt: filled ? '2026-10-08T03:32:00' : null,
    lastSubmittedByName: filled ? 'Wang' : null,
  }
}

// ───────── 筛选与查询 ─────────

assert.deepEqual(createDefaultFilters(2026), {
  seasonYear: 2026,
  cardType: 1,
  localSupplierCode: undefined,
  priceOption: undefined,
  storeCodes: [],
})
assert.deepEqual(buildYearOptions(2026), [2027, 2026, 2025, 2024, 2023, 2022], '明年 + 今年 + 往前 4 年')
assert.equal(isCardType(5), true)
assert.equal(isCardType(6), false)
assert.equal(isPriceOption(4), true)
assert.equal(isPriceOption(0), false)

assert.deepEqual(
  buildSummaryQuery({ seasonYear: 2026, cardType: 2, localSupplierCode: '  ', priceOption: undefined, storeCodes: [] }),
  { seasonYear: 2026, cardType: 2, localSupplierCode: undefined, priceOption: undefined, storeCodes: undefined },
  '空供应商、空分店都不传',
)
assert.deepEqual(
  buildSummaryQuery({ seasonYear: 2026, cardType: 1, localSupplierCode: ' SUP-A ', priceOption: 4, storeCodes: ['1013', ' 1013', '1014', ''] }),
  { seasonYear: 2026, cardType: 1, localSupplierCode: 'SUP-A', priceOption: 4, storeCodes: ['1013', '1014'] },
  '分店去重去空，供应商去空格',
)
assert.equal(hasQuantityFilter(createDefaultFilters(2026)), false)
assert.equal(hasQuantityFilter({ ...createDefaultFilters(2026), priceOption: 1 }), true)
assert.equal(hasQuantityFilter({ ...createDefaultFilters(2026), localSupplierCode: 'SUP-A' }), true)
assert.equal(hasQuantityFilter({ ...createDefaultFilters(2026), storeCodes: ['1013'] }), false, '分店筛选不算数量口径筛选')

// ───────── 金额与数量 ─────────

assert.equal(formatMoney(1234.5), '$1,234.50')
assert.equal(formatMoney(0), '$0.00')
assert.equal(formatMoney(1234.567), '$1,234.57', '四舍五入到分')
assert.equal(formatMoney(-12.3), `${MINUS_SIGN}$12.30`)
assert.equal(formatMoney(-0.001), '$0.00', '四舍五入后为 0 不带负号')
assert.equal(formatMoney(null), '—')
assert.equal(formatMoney(Number.NaN), '—')
assert.equal(formatCount(1234567), '1,234,567')
assert.equal(formatCount(0), '0')
assert.equal(formatCount(undefined), '—')
assert.equal(sumMoney([0.1, 0.2]), 0.3, '按分求和没有浮点残差')

// ───────── 状态筛选与合计 ─────────

const rows = [row('1011', true, [120, 80, 30, 0]), row('1012', false), row('1013', true, [10, 0, 0, 12], 54)]
assert.equal(isStatusFilter('filled'), true)
assert.equal(isStatusFilter('done'), false)
assert.deepEqual(countByStatus(rows), { all: 3, filled: 2, unfilled: 1 })
assert.deepEqual(filterRowsByStatus(rows, 'filled').map((item) => item.storeCode), ['1011', '1013'])
assert.deepEqual(filterRowsByStatus(rows, 'unfilled').map((item) => item.storeCode), ['1012'])
assert.equal(filterRowsByStatus(rows, 'all').length, 3)

const footer = buildFooterTotals(rows)
assert.deepEqual(footer.quantities, { 1: 130, 2: 80, 3: 30, 4: 12 })
assert.equal(footer.totalQuantity, 252)
assert.equal(footer.filledCount, 2)
assert.equal(footer.totalAmount, 120 + 160 + 90 + 10 + 54)
const unfilledFooter = buildFooterTotals(filterRowsByStatus(rows, 'unfilled'))
assert.equal(unfilledFooter.totalQuantity, 0)
assert.equal(unfilledFooter.filledCount, 0)

assert.equal(averagePerFilledStore(252, 2), 126)
assert.equal(averagePerFilledStore(10, 3), 3, '四舍五入到整数')
assert.equal(averagePerFilledStore(0, 0), null, '没有已填报分店时不显示平均')
assert.equal(fillPercent(10, 26), 38)
assert.equal(fillPercent(0, 0), 0)
assert.equal(fillPercent(30, 26), 100, '超出时封顶 100')

const bars = buildPriceBars(prices(200, 100, 0, 50))
assert.deepEqual(bars.map((bar) => bar.widthPercent), [100, 50, 0, 25])
assert.deepEqual(buildPriceBars([]).map((bar) => bar.widthPercent), [0, 0, 0, 0], '全为 0 时宽度都是 0')
assert.deepEqual(buildPriceBars([]).map((bar) => bar.priceOption), [1, 2, 3, 4], '缺项也固定 4 档')

assert.equal(
  formatSupplierList(
    [
      { localSupplierCode: 'A', supplierName: 'Alpha' },
      { localSupplierCode: null, supplierName: '未指定供应商' },
      { localSupplierCode: 'A', supplierName: 'Alpha' },
    ],
    '、',
  ),
  'Alpha、未指定供应商',
  '同名去重',
)
assert.equal(formatSupplierList([], '、'), '—')

assert.equal(
  buildUnfilledCopyText([
    { storeCode: '1014', storeName: 'Bankstown' },
    { storeCode: '1017', storeName: '' },
  ]),
  '1014 Bankstown\n1017',
  '每行一个「编码 名称」，没有名称只给编码',
)
assert.equal(buildUnfilledCopyText([]), '')

const universe1 = mergeStoreUniverse([], [
  { storeCode: '1020', storeName: 'B' },
  { storeCode: '1003', storeName: 'A' },
])
assert.deepEqual(universe1.map((store) => store.storeCode), ['1003', '1020'], '按编码排序')
const universe2 = mergeStoreUniverse(universe1, [{ storeCode: '1003', storeName: 'A' }])
assert.equal(universe2, universe1, '没有新分店时返回原数组（避免无谓重渲染）')
const universe3 = mergeStoreUniverse(universe1, [{ storeCode: '1010', storeName: 'C' }])
assert.deepEqual(universe3.map((store) => store.storeCode), ['1003', '1010', '1020'], '只按某几店筛选时已知名单不缩小')

// ───────── 单店明细 ─────────

const batch = (overrides: Partial<SeasonalCardBatch>): SeasonalCardBatch => ({
  batchGuid: 'b1',
  storeCode: '1013',
  storeName: 'Store 1013',
  seasonYear: 2026,
  cardType: 1,
  cardTypeName: '圣诞节',
  localSupplierCode: 'SUP-A',
  supplierName: 'Alpha',
  remark: null,
  submittedByName: 'Wang',
  submittedAt: '2026-10-08T03:32:00Z',
  totalQuantity: 248,
  totalAmount: 442,
  isCurrent: true,
  lines: [
    { submissionGuid: 's1', catalogGuid: 'c1', priceOption: 1, priceLabel: '$1', unitPrice: 1, remainingQuantity: 120 },
    { submissionGuid: 's2', catalogGuid: 'c2', priceOption: 2, priceLabel: '$2', unitPrice: 2, remainingQuantity: 80 },
    { submissionGuid: 's3', catalogGuid: 'c3', priceOption: 3, priceLabel: '$3', unitPrice: 3, remainingQuantity: 36 },
    { submissionGuid: 's4', catalogGuid: 'c4', priceOption: 4, priceLabel: '其他', unitPrice: 4.5, remainingQuantity: 12 },
  ],
  ...overrides,
})

const matrix = buildSupplierMatrix(
  [batch({}), batch({ batchGuid: null, localSupplierCode: null, supplierName: null, lines: [
    { submissionGuid: 's9', catalogGuid: 'c4', priceOption: 4, priceLabel: '其他', unitPrice: 0, remainingQuantity: 0 },
  ] })],
  '未指定供应商',
)
assert.equal(matrix.length, 2)
assert.equal(matrix[0].supplierName, 'Alpha')
assert.equal(matrix[0].cells[1].quantity, 120)
assert.equal(matrix[0].cells[1].unitPrice, null, '固定价格不显示单价')
assert.equal(matrix[0].cells[4].unitPrice, 4.5, '其他价格显示实际单价')
assert.equal(matrix[1].supplierName, '未指定供应商', '历史行没有供应商')
assert.equal(matrix[1].cells[4].unitPrice, null, '数量为 0 时不显示单价')
assert.notEqual(matrix[0].key, matrix[1].key, '行 key 唯一')

const lineLabels = { other: '其他', unitPrice: (price: string) => `（${price}）` }
assert.equal(formatBatchLines(batch({}).lines, lineLabels), '$1 × 120 · $2 × 80 · $3 × 36 · 其他 × 12（$4.50）')
assert.equal(
  formatBatchLines([{ submissionGuid: 's', catalogGuid: 'c', priceOption: 4, priceLabel: '', unitPrice: 0, remainingQuantity: 0 }], lineLabels),
  '其他 × 0',
  '其他价格数量为 0 时不带单价',
)
assert.equal(formatBatchLines([], lineLabels), '—')

assert.deepEqual(buildYearComparison(248, 344), { currentWidth: 72, previousWidth: 100, delta: -96 })
assert.deepEqual(buildYearComparison(100, null), { currentWidth: 100, previousWidth: 0, delta: null })
assert.deepEqual(buildYearComparison(0, 0), { currentWidth: 0, previousWidth: 0, delta: 0 })
assert.equal(formatSignedCount(12), '+12')
assert.equal(formatSignedCount(-1200), `${MINUS_SIGN}1,200`)
assert.equal(formatSignedCount(0), '0')
assert.equal(formatSignedCount(null), '—')

// ───────── 地址栏 ?store= ─────────

assert.equal(parseStoreParam('1013'), '1013')
assert.equal(parseStoreParam(' 1013 '), '1013')
assert.equal(parseStoreParam(''), null)
assert.equal(parseStoreParam(null), null)
assert.equal(parseStoreParam('10/13'), null, '非法字符当作没有')
assert.equal(parseStoreParam('x'.repeat(33)), null, '过长当作没有')
assert.equal(withStoreParam(new URLSearchParams('a=1'), '1013').toString(), 'a=1&store=1013', '保留其他参数')
assert.equal(withStoreParam(new URLSearchParams('a=1&store=1013'), null).toString(), 'a=1')

// ───────── 时间 ─────────

assert.equal(parseUtcTimestamp('2026-10-08T03:32:00')?.toISOString(), '2026-10-08T03:32:00.000Z', '不带时区后缀按 UTC 解释')
assert.equal(parseUtcTimestamp('2026-10-08T03:32:00+10:00')?.toISOString(), '2026-10-07T17:32:00.000Z')
assert.equal(parseUtcTimestamp('not a date'), null)
assert.equal(parseUtcTimestamp(null), null)
// 2026-10-08 悉尼已是夏令时（UTC+11）
assert.equal(formatLocalTime('2026-10-08T03:32:00', 'short', 'Australia/Sydney'), '10-08 14:32')
assert.equal(formatLocalTime('2026-10-08T03:32:00Z', 'full', 'Australia/Sydney'), '2026-10-08 14:32')
assert.equal(formatLocalTime('2026-10-08T03:32:00Z', 'full', 'Australia/Perth'), '2026-10-08 11:32', '按给定时区显示')
assert.equal(formatLocalTime('2026-12-31T13:30:00Z', 'full', 'Australia/Sydney'), '2027-01-01 00:30', '跨年')
assert.equal(formatLocalTime(null), '—')
// 不传时区：按运行环境的本地时区（浏览器本地），只校验格式
assert.match(formatLocalTime('2026-10-08T03:32:00Z'), /^\d{2}-\d{2} \d{2}:\d{2}$/)

// ───────── 导出文件名 ─────────

assert.equal(buildExportFileName('贺卡分店填报统计', 2026, '圣诞节'), '贺卡分店填报统计_2026_圣诞节.xlsx')
assert.equal(buildExportFileName('SeasonalCardStats', 2026, "Mother's Day"), "SeasonalCardStats_2026_Mother's-Day.xlsx", '空格换成连字符')
assert.equal(buildExportFileName('a/b', 2026, 'c:d'), 'a-b_2026_c-d.xlsx', '去掉文件系统不允许的字符')

// ───────── 请求守卫与错误分类 ─────────

const guard = createAbortableRequestGuard()
const first = guard.begin()
const second = guard.begin()
assert.equal(first.signal.aborted, true, '新请求取消旧请求')
assert.equal(guard.isLatest(first.requestId), false)
assert.equal(guard.isLatest(second.requestId), true)
guard.abort()
assert.equal(second.signal.aborted, true)
assert.equal(guard.isLatest(second.requestId), false, 'abort 之后旧请求不再算最新')

assert.equal(isAbortError({ name: 'AbortError' }), true)
assert.equal(isAbortError(new Error('x')), false)
assert.equal(classifyStatsError({ status: 403 }), 'forbidden')
assert.equal(classifyStatsError({ status: 404 }), 'notFound')
assert.equal(classifyStatsError({ status: 200, payload: { success: false, errorCode: 'STORE_NOT_FOUND' } }), 'notFound', '业务错误是 HTTP 200')
assert.equal(classifyStatsError({ status: 200, payload: { success: false, errorCode: 'INVALID_SEASON_YEAR' } }), 'invalidQuery')
assert.equal(classifyStatsError({ status: 200, payload: { success: false, code: 'INVALID_CARD_TYPE' } }), 'invalidQuery')
assert.equal(classifyStatsError({ status: 500 }), 'failed')
assert.equal(classifyStatsError(new Error('network')), 'failed')
assert.equal(classifyStatsError(null), 'failed')

console.log('SeasonalCardStats logic.test: ok')
