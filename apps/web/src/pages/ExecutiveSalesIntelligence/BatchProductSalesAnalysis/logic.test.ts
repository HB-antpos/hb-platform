import assert from 'node:assert/strict'
import type { BatchSalesBranch, BatchSalesDaily, BatchSalesDetail, BatchSalesDiscountOverview, BatchSalesMetrics } from '../../../types/batchProductSalesAnalysis'
import { appendBatchProductSalesPurchaseOnlyBranches, buildBatchProductSalesAnalysis, buildBatchProductSalesDetailExportScope, buildBatchProductSalesPurchaseIndex, getBatchProductSalesPurchase, escapeCsvCell, formatCsvRow, getBatchProductSalesDiscountStateKey, getBatchProductSalesClassifiedQuantity, getBatchProductSalesDateRangeError, hasBatchProductSalesDiscountStatisticsNotice, hasBatchProductSalesDailyActivity, mergeBatchProductSalesDetailClassifications, runBatchProductSalesPool, shouldRefreshBatchProductSalesDiscountStatistics, sortBatchProductSalesBranchesByQuantity } from './logic'

function metrics(overrides: Partial<BatchSalesMetrics> = {}): BatchSalesMetrics {
  return {
    quantity: 0,
    regularQuantity: 0,
    discountQuantity: 0,
    unknownQuantity: 0,
    returnQuantity: 0,
    salesAmount: 0,
    discountStatus: 'complete',
    originalPriceMin: null,
    originalPriceMax: null,
    discountPriceMin: null,
    discountPriceMax: null,
    ...overrides,
  }
}

function daily(overrides: Partial<BatchSalesMetrics> = {}): BatchSalesDaily {
  return { date: '2026-09-14', metrics: metrics(overrides) }
}

function branch(code: string, quantity: number): BatchSalesBranch {
  return {
    branchCode: code,
    branchName: code,
    metrics: metrics({ quantity }),
    daily: [],
  }
}

function detail(productCode: string, branches: BatchSalesBranch[]): BatchSalesDetail {
  const metricRows = branches.map((item) => item.metrics)
  const quantity = metricRows.reduce((sum, item) => sum + item.quantity, 0)
  const regularQuantity = metricRows.reduce((sum, item) => sum + item.regularQuantity, 0)
  const discountQuantity = metricRows.reduce((sum, item) => sum + item.discountQuantity, 0)
  const salesAmount = metricRows.reduce((sum, item) => sum + item.salesAmount, 0)
  return {
    startDate: '2026-09-01',
    endDate: '2026-09-02',
    storeCodes: [],
    productCodes: [productCode],
    warnings: [],
    product: { productCode, itemNumber: productCode, productName: productCode },
    metrics: metrics({
      quantity,
      regularQuantity,
      discountQuantity,
      salesAmount,
    }),
    daily: branches.flatMap((item) => item.daily),
    branches,
    coverage: { status: 'complete', readyDates: ['2026-09-01', '2026-09-02'], pendingDates: [], version: 'test' },
  }
}

assert.equal(getBatchProductSalesDateRangeError('2025-08-18', '2026-08-18', '2026-08-18'), undefined, '含首尾共 366 天的范围必须允许')
assert.equal(getBatchProductSalesDateRangeError('2025-08-17', '2026-08-18', '2026-08-18'), '日期范围不能超过 366 天', '超过 366 天必须拒绝')
assert.equal(getBatchProductSalesDateRangeError('2026-08-19', '2026-08-18', '2026-08-18'), '开始日期不能晚于结束日期', '开始日期晚于结束日期必须拒绝')
assert.equal(getBatchProductSalesDateRangeError('2026-02-31', '2026-08-18', '2026-08-18'), '日期格式无效', '不存在的日期必须拒绝')
assert.equal(getBatchProductSalesDateRangeError('2026-08-18', '2026-08-19', '2026-08-18'), '结束日期不能晚于今天', '结束日期晚于传入业务日期必须拒绝')

assert.equal(escapeCsvCell('plain'), 'plain', '普通单元格不应额外转义')
assert.equal(escapeCsvCell('a,"b"'), '"a,""b"""', 'CSV 引号和逗号必须按 RFC 4180 转义')
assert.equal(escapeCsvCell('line1\nline2'), '"line1\nline2"', 'CSV 换行必须被包裹')
assert.equal(escapeCsvCell('=SUM(A1:A2)'), "'=SUM(A1:A2)", '等号开头必须防止公式执行')
assert.equal(escapeCsvCell('  +1'), "'  +1", '带前导空白的公式前缀也必须防护')
assert.equal(escapeCsvCell('-12'), "'-12", '负号开头必须防止 CSV 被解释为公式')
assert.equal(escapeCsvCell('@cmd'), "'@cmd", '@ 开头必须防止 CSV 被解释为公式')
assert.equal(escapeCsvCell(-3), '-3', '数值型退货数量必须保持为可计算数字')
assert.equal(escapeCsvCell('-3'), "'-3", '字符串型负数仍必须防止 CSV 被解释为公式')
assert.equal(escapeCsvCell(Number.NaN), '', '非有限数值不得导出为错误数值')
assert.equal(formatCsvRow(['货号', '=A1', 'a,b']), '货号,\'=A1,"a,b"', '整行导出必须复用安全单元格转义')

assert.equal(hasBatchProductSalesDailyActivity(daily()), false, '全零补齐日必须隐藏')
assert.equal(hasBatchProductSalesDailyActivity(daily({ regularQuantity: 3, quantity: 3 })), true, '正价销售必须保留')
assert.equal(hasBatchProductSalesDailyActivity(daily({ discountQuantity: 2, quantity: 2 })), true, '折扣销售必须保留')
assert.equal(hasBatchProductSalesDailyActivity(daily({ unknownQuantity: 1, quantity: 1 })), true, '未知分类销售必须保留')
assert.equal(hasBatchProductSalesDailyActivity(daily({ regularQuantity: -2, quantity: -2 })), true, '纯退货日必须保留')
assert.equal(hasBatchProductSalesDailyActivity(daily({ regularQuantity: 4, discountQuantity: -4, quantity: 0 })), true, '正负分类相互抵消日必须保留')
assert.equal(hasBatchProductSalesDailyActivity(daily({ returnQuantity: 4 })), true, '同类销售与退货抵消后，非零退货量仍证明当天有真实活动')
assert.equal(hasBatchProductSalesDailyActivity(daily({ salesAmount: 12.5 })), true, '非零销售额也应避免把真实业务日误认为补齐日')
assert.equal(hasBatchProductSalesDailyActivity(daily({ quantity: 6, discountStatus: 'pending' })), true, '待统计且分类不可用时，非零总销量必须保留')
assert.equal(hasBatchProductSalesDailyActivity(daily({ quantity: -1, discountStatus: 'pending' })), true, '待统计纯退货的非零总销量必须保留')
assert.equal(hasBatchProductSalesDailyActivity(daily({ quantity: 0, discountStatus: 'pending' })), false, '待统计的全零补齐日仍必须隐藏')

const branches = [branch('B', 4), branch('A', 9), branch('C', 4), branch('D', -2)]
const sortedBranches = sortBatchProductSalesBranchesByQuantity(branches)
assert.deepEqual(
  sortedBranches.map((item) => item.branchCode),
  ['A', 'B', 'C', 'D'],
  '分店必须按销量稳定降序排列',
)
assert.deepEqual(
  branches.map((item) => item.branchCode),
  ['B', 'A', 'C', 'D'],
  '排序不得修改服务端原始数组',
)
assert.notEqual(sortedBranches, branches, '排序结果必须是新的数组')

assert.equal(getBatchProductSalesClassifiedQuantity(metrics({ regularQuantity: 4 }), 'regularQuantity'), 4, '分类完成时应显示真实数量')
assert.equal(getBatchProductSalesClassifiedQuantity(metrics({ discountStatus: 'pending', regularQuantity: 0 }), 'regularQuantity'), null, '待统计不得把正价缺失显示成零')
assert.equal(getBatchProductSalesClassifiedQuantity(metrics({ discountStatus: 'unknown', regularQuantity: 0 }), 'regularQuantity', true), null, '终态未知不得把正价缺失显示成零')
assert.equal(getBatchProductSalesClassifiedQuantity(metrics({ discountStatus: 'unknown', unknownQuantity: 6 }), 'unknownQuantity', true), 6, '终态未知仍应显示可靠的未知数量')
assert.equal(getBatchProductSalesClassifiedQuantity(metrics({ discountStatus: 'unknown', regularQuantity: 0 }), 'regularQuantity'), null, '未知分类不得把正价缺失显示成零')
assert.equal(getBatchProductSalesClassifiedQuantity(metrics({ discountStatus: 'partial', regularQuantity: 4 }), 'regularQuantity'), 4, '部分分类仍应显示已有证据')

for (const status of ['Queued', 'Running', 'Pending', 'Backfilling', 'Refreshing', 'Partial', 'OutOfSync', 'Unavailable', 'queued', 'RUNNING']) {
  assert.equal(shouldRefreshBatchProductSalesDiscountStatistics(status), true, `${status} 应继续轮询`)
  assert.equal(hasBatchProductSalesDiscountStatisticsNotice(status), true, `${status} 应显示状态提示`)
}
for (const status of ['Fresh', 'Failed', 'Superseded', undefined]) {
  assert.equal(shouldRefreshBatchProductSalesDiscountStatistics(status), false, `${status ?? 'undefined'} 不应继续轮询`)
}
assert.equal(hasBatchProductSalesDiscountStatisticsNotice('Fresh'), false, 'Fresh 不应显示折扣统计提示')
assert.equal(getBatchProductSalesDiscountStateKey('Fresh'), 'Fresh', 'Fresh 必须保留为可用折扣分类状态')
assert.equal(hasBatchProductSalesDiscountStatisticsNotice(undefined), false, '缺少状态时不应误报折扣统计异常')
assert.equal(getBatchProductSalesDiscountStateKey('backFILLing'), 'Backfilling', '折扣状态翻译 key 必须大小写稳健')
assert.equal(getBatchProductSalesDiscountStateKey('OUT_OF_SYNC'), 'OutOfSync', '折扣状态翻译 key 必须兼容服务端分隔符')
assert.equal(shouldRefreshBatchProductSalesDiscountStatistics('OUT_OF_SYNC'), true, '分隔符状态也必须继续轮询')
assert.equal(getBatchProductSalesDiscountStateKey('new-state'), 'Unavailable', '未知状态必须降级到已有翻译 key')
for (const status of ['Partial', 'Failed', 'OutOfSync', 'Superseded', 'Unavailable']) {
  assert.equal(hasBatchProductSalesDiscountStatisticsNotice(status), true, `${status} 应显示终态警告`)
}

let activeWorkers = 0
let maxActiveWorkers = 0
const processedItems: number[] = []
await runBatchProductSalesPool([1, 2, 3, 4, 5, 6, 7], 3, async (item) => {
  activeWorkers += 1
  maxActiveWorkers = Math.max(maxActiveWorkers, activeWorkers)
  await new Promise((resolve) => setTimeout(resolve, 1))
  processedItems.push(item)
  activeWorkers -= 1
})
assert.equal(maxActiveWorkers, 3, '明细请求池必须遵守并发上限')
assert.deepEqual(
  [...processedItems].sort((left, right) => left - right),
  [1, 2, 3, 4, 5, 6, 7],
  '请求池必须且只处理每个商品一次',
)

const p1b1 = branch('B1', 5)
p1b1.metrics = metrics({
  quantity: 5,
  regularQuantity: 4,
  discountQuantity: 1,
  salesAmount: 42,
})
p1b1.daily = [
  {
    date: '2026-09-01',
    metrics: metrics({ quantity: 2, regularQuantity: 2, salesAmount: 18 }),
  },
  {
    date: '2026-09-02',
    metrics: metrics({
      quantity: 3,
      regularQuantity: 2,
      discountQuantity: 1,
      salesAmount: 24,
    }),
  },
]
const p1b2 = branch('B2', 2)
p1b2.metrics = metrics({ quantity: 2, regularQuantity: 2, salesAmount: 16 })
p1b2.daily = [
  {
    date: '2026-09-01',
    metrics: metrics({ quantity: 2, regularQuantity: 2, salesAmount: 16 }),
  },
]
const p2b1 = branch('B1', 3)
p2b1.metrics = metrics({
  quantity: 3,
  regularQuantity: 1,
  discountQuantity: 2,
  salesAmount: 21,
})
p2b1.daily = [
  {
    date: '2026-09-01',
    metrics: metrics({
      quantity: 3,
      regularQuantity: 1,
      discountQuantity: 2,
      salesAmount: 21,
    }),
  },
]
const p2b2 = branch('B2', 7)
p2b2.metrics = metrics({ quantity: 7, regularQuantity: 7, salesAmount: 56 })
p2b2.daily = [
  {
    date: '2026-09-02',
    metrics: metrics({ quantity: 7, regularQuantity: 7, salesAmount: 56 }),
  },
]
const aggregate = buildBatchProductSalesAnalysis([detail('P1', [p1b1, p1b2]), detail('P2', [p2b1, p2b2])])
assert.equal(aggregate.metrics.quantity, 17, '全部商品必须汇总每个已加载商品的销量')
assert.equal(aggregate.metrics.salesAmount, 135, '全部商品必须汇总销售额')
assert.deepEqual(
  aggregate.daily.map((row) => [row.date, row.metrics.quantity]),
  [
    ['2026-09-01', 7],
    ['2026-09-02', 10],
  ],
  '全部商品的每日销量必须按日期聚合',
)
assert.deepEqual(
  aggregate.branches.map((row) => [row.branchCode, row.metrics.quantity]),
  [
    ['B2', 9],
    ['B1', 8],
  ],
  '分店总量排行必须稳定按销量降序',
)
assert.deepEqual(
  buildBatchProductSalesAnalysis([detail('P1', [p1b1, p1b2]), detail('P2', [p2b1, p2b2])], { branchCode: 'B1' }).productContributions.map((row) => [row.product.productCode, row.metrics.quantity]),
  [
    ['P1', 5],
    ['P2', 3],
  ],
  '所选分店的商品贡献必须按销量降序',
)

const singleProduct = buildBatchProductSalesAnalysis([detail('P1', [p1b1, p1b2]), detail('P2', [p2b1, p2b2])], { productCode: 'P1' })
assert.equal(singleProduct.metrics.quantity, 7, '商品筛选必须只汇总已选商品')
assert.deepEqual(
  singleProduct.branches.map((row) => row.branchCode),
  ['B1', 'B2'],
  '商品筛选后的分店排行必须保留商品范围',
)

const singleBranch = buildBatchProductSalesAnalysis([detail('P1', [p1b1, p1b2]), detail('P2', [p2b1, p2b2])], { branchCode: 'B1' })
assert.equal(singleBranch.metrics.quantity, 8, '分店筛选必须只汇总已选分店')
assert.deepEqual(
  singleBranch.daily.map((row) => [row.date, row.metrics.quantity]),
  [
    ['2026-09-01', 5],
    ['2026-09-02', 3],
  ],
  '分店筛选后的每日趋势必须来自所选分店',
)

const incomplete = buildBatchProductSalesAnalysis([detail('P1', [p1b1, p1b2])])
assert.equal(incomplete.metrics.quantity, 7, '未返回的商品不得被补成零或混入已加载汇总')
assert.deepEqual(
  buildBatchProductSalesAnalysis([detail('P1', [p1b1, p1b2])], {
    branchCode: 'B1',
  }).productContributions.map((row) => row.product.productCode),
  ['P1'],
  '未返回的商品不得显示为零贡献',
)

const exportScope = { startDate: '2026-09-01', endDate: '2026-09-30', storeCodes: ['S1', 'S2'] }
const exportCoverage = { status: 'complete' as const, readyDates: ['2026-09-01', '2026-09-02'], pendingDates: [], version: 'coverage-v1' }
assert.deepEqual(
  buildBatchProductSalesDetailExportScope(exportScope, exportCoverage, ['P1', 'P2']),
  { ...exportScope, productCodes: ['P1', 'P2'], coverageVersion: 'coverage-v1', readyDates: ['2026-09-01', '2026-09-02'] },
  '全部商品视图导出必须保留摘要商品集合、实际门店范围和 coverage 锁',
)
assert.deepEqual(
  buildBatchProductSalesDetailExportScope(exportScope, exportCoverage, ['P1', 'P2'], 'P2'),
  { ...exportScope, productCodes: ['P2'], coverageVersion: 'coverage-v1', readyDates: ['2026-09-01', '2026-09-02'] },
  '选定商品导出只能提交该商品，且不能因分店钻取改变实际门店范围',
)

const reliableSingle = detail('P1', [{ ...branch('B1', 5), metrics: metrics({ quantity: 5, salesAmount: 50, discountStatus: 'unknown' }), daily: [daily({ quantity: 5, salesAmount: 50, discountStatus: 'unknown' })] }])
const singleClassifications: BatchSalesDiscountOverview = {
  startDate: reliableSingle.startDate, endDate: reliableSingle.endDate, storeCodes: reliableSingle.storeCodes, productCodes: ['P1'], coverage: reliableSingle.coverage,
  overview: {
    metrics: metrics({ quantity: 999, salesAmount: 999, regularQuantity: 4, discountQuantity: 1 }),
    daily: [{ date: '2026-09-14', metrics: metrics({ quantity: 999, salesAmount: 999, regularQuantity: 4, discountQuantity: 1 }) }],
    branches: [{ branchCode: 'B1', branchName: 'B1', metrics: metrics({ quantity: 999, salesAmount: 999, regularQuantity: 4, discountQuantity: 1 }), daily: [{ date: '2026-09-14', metrics: metrics({ quantity: 999, salesAmount: 999, regularQuantity: 4, discountQuantity: 1 }) }], contributingProductCount: 1 }],
  },
  discountStatisticStatus: 'Fresh', warnings: ['classification ready'],
}
const classifiedSingle = mergeBatchProductSalesDetailClassifications(reliableSingle, singleClassifications)
assert.equal(classifiedSingle.metrics.quantity, 5, '分类迟到不得覆盖单品可靠总销量')
assert.equal(classifiedSingle.metrics.salesAmount, 50, '分类迟到不得覆盖单品可靠销售额')
assert.equal(classifiedSingle.metrics.regularQuantity, 4, '分类响应可补齐单品正价数量')
assert.equal(classifiedSingle.daily[0]?.metrics.quantity, 5, '分类迟到不得覆盖单品可靠每日销量')
assert.equal(classifiedSingle.branches[0]?.metrics.salesAmount, 50, '分类迟到不得覆盖单品可靠分店金额')
assert.equal(classifiedSingle.discountStatisticStatus, 'Fresh', '分类状态应透传到单品详情')

const purchaseIndex = buildBatchProductSalesPurchaseIndex([
  { productCode: 'P1', branchCode: 'B1', warehouseQuantity: 24, supplierQuantity: 0 },
  { productCode: 'P1', branchCode: 'B2', warehouseQuantity: 0, supplierQuantity: 12 },
  { productCode: 'P2', branchCode: 'B1', warehouseQuantity: 6, supplierQuantity: 4 },
])
assert.deepEqual(getBatchProductSalesPurchase(purchaseIndex), { total: 46, warehouse: 30, supplier: 16 }, '全部商品全部分店进货量 = 总仓配货 + 供应商进货')
assert.equal(getBatchProductSalesPurchase(purchaseIndex, { productCode: 'P1' }).total, 36, '单品进货量跨分店合计')
assert.equal(getBatchProductSalesPurchase(purchaseIndex, { branchCode: 'B1' }).total, 34, '分店进货量跨商品合计')
assert.deepEqual(getBatchProductSalesPurchase(purchaseIndex, { productCode: 'P2', branchCode: 'B1' }), { total: 10, warehouse: 6, supplier: 4 }, '商品×分店进货量保留来源拆分')
assert.equal(getBatchProductSalesPurchase(purchaseIndex, { productCode: 'P2', branchCode: 'B2' }).total, 0, '服务端未返回的组合是真实 0')
const purchaseNames = new Map([['B2', 'Browns Plains']])
const withPurchaseOnly = appendBatchProductSalesPurchaseOnlyBranches([branch('B1', 5)], purchaseIndex, 'P1', purchaseNames)
assert.deepEqual(withPurchaseOnly.map((item) => item.branchCode), ['B1', 'B2'], '单品有进货无销量的分店补在排行末尾')
assert.equal(withPurchaseOnly[1]?.branchName, 'Browns Plains', '补齐分店使用分店名称')
assert.equal(withPurchaseOnly[1]?.metrics.quantity, 0, '补齐分店销量记 0')
assert.deepEqual(appendBatchProductSalesPurchaseOnlyBranches([branch('B1', 5)], purchaseIndex, 'P2', purchaseNames).map((item) => item.branchCode), ['B1'], '已有销量行的分店不重复补齐')
assert.equal(appendBatchProductSalesPurchaseOnlyBranches([branch('B1', 5)], undefined, 'P1', purchaseNames).length, 1, '进货量未就绪时不改动排行')

console.log('BatchProductSalesAnalysis.logic.test: ok')
