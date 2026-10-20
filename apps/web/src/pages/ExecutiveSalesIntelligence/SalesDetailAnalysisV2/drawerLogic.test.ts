import assert from 'node:assert/strict'

import {
  PURCHASE_LOOKBACK_DAYS, addDays, batchOffset, buildDailyPoints, buildMonthlyPoints, daysBetween, drawerWindow,
  enumerateDays, niceScale, previousPurchase, purchaseOnlyStores, summarizeStorePurchases, weekdayOf,
} from './drawerLogic'
import { normalizeProductPurchases, normalizeProductTrend, type ProductPurchaseBatch } from './reportService'

// 窗口：销售开始日前 60 天 ~ 销售结束日，与后端常量一致。
assert.equal(PURCHASE_LOOKBACK_DAYS, 60)
assert.deepEqual(drawerWindow('2025-12-29', '2026-01-01'), { start: '2025-10-30', end: '2026-01-01' }, '窗口为销售开始日前 60 天起')
assert.equal(addDays('2026-03-01', -1), '2026-02-28')
assert.equal(daysBetween('2025-12-22', '2025-12-29'), 7)
assert.equal(weekdayOf('2025-12-30'), 2, '2025-12-30 是周二')
const window = drawerWindow('2025-12-29', '2026-01-01')
assert.equal(enumerateDays(window.start, window.end).length, 64, '10-30 ~ 01-01 共 64 天')
assert.equal(enumerateDays('2026-01-02', '2026-01-01').length, 0, '起止颠倒不产生日期')
assert.equal(enumerateDays('2020-01-01', '2026-01-01').length, 731 + 60, '超长窗口只取最后上限天数')

// 日点：销量缺口补 0，进货按来源分开；分店过滤按去空格、不分大小写的编码。
const batches: ProductPurchaseBatch[] = [
  { branchCode: 'B1', date: '2025-11-12', source: 'warehouse', documentNo: '2025-1655', quantity: 120 },
  { branchCode: 'b1 ', date: '2025-12-30', source: 'warehouse', documentNo: '2025-1731', quantity: 60 },
  { branchCode: 'B2', date: '2025-12-30', source: 'supplier', documentNo: 'INV-1', quantity: 24 },
  { branchCode: 'B1', date: '2025-12-22', source: 'supplier', documentNo: 'INV-2', quantity: 60 },
]
const points = buildDailyPoints(window, [{ date: '2025-12-30', quantity: 126 }, { date: '2026-01-01', quantity: 54 }], batches)
assert.equal(points.length, 64)
assert.deepEqual(points.find(point => point.date === '2025-12-30'), { date: '2025-12-30', sales: 126, warehouse: 60, supplier: 24 }, '同一天两个来源分别累加')
assert.deepEqual(points.find(point => point.date === '2025-12-31'), { date: '2025-12-31', sales: 0, warehouse: 0, supplier: 0 }, '缺口补 0')
const onlyB1 = buildDailyPoints(window, [], batches, 'B1')
assert.equal(onlyB1.find(point => point.date === '2025-12-30')?.supplier, 0, '只看某分店时不含其他分店的进货')
assert.equal(onlyB1.find(point => point.date === '2025-12-30')?.warehouse, 60)

// 月点：自然月汇总，头尾不满整月标 partial，含销售期的月份标 hasSalesPeriod。
const months = buildMonthlyPoints(points, '2025-12-29', '2026-01-01')
assert.deepEqual(months.map(month => [month.key, month.days, month.partial, month.hasSalesPeriod]), [
  ['2025-10', 2, true, false], ['2025-11', 30, false, false], ['2025-12', 31, false, true], ['2026-01', 1, true, true],
], '10 月只有 30–31 日、1 月只有 1 日')
assert.equal(months.reduce((sum, month) => sum + month.sales, 0), 180, '月汇总不丢销量')
assert.equal(months[2].warehouse + months[2].supplier, 60 + 24 + 60, '12 月进货合计')

// 刻度：区间数不超过上限，好看的步长。
assert.deepEqual(niceScale(168, 4), { max: 200, ticks: [0, 50, 100, 150, 200] })
assert.deepEqual(niceScale(120, 3), { max: 120, ticks: [0, 40, 80, 120] })
assert.deepEqual(niceScale(3, 4), { max: 3, ticks: [0, 1, 2, 3] })
assert.deepEqual(niceScale(0, 2), { max: 2, ticks: [0, 1, 2] }, '全 0 时给固定小刻度')
assert.equal(niceScale(1085, 3).max, 1200, '1085 取 400 一档')

// 分店进货汇总：总量、来源拆分、批数、最近一次与是否在销售期内。
const summaries = summarizeStorePurchases(batches, '2025-12-29', '2026-01-01')
const b1 = summaries.get('B1')!
assert.deepEqual([b1.quantity, b1.warehouse, b1.supplier, b1.batchCount, b1.latestDate, b1.latestInSalesPeriod], [240, 180, 60, 3, '2025-12-30', true])
assert.deepEqual(b1.batches.map(batch => batch.date), ['2025-12-30', '2025-12-22', '2025-11-12'], '明细从近到远')
assert.equal(summaries.get('B2')!.latestInSalesPeriod, true)
assert.deepEqual(batchOffset('2025-12-22', '2025-12-29', '2026-01-01'), { inPeriod: false, daysBefore: 7 })
assert.deepEqual(batchOffset('2026-01-01', '2025-12-29', '2026-01-01'), { inPeriod: true, daysBefore: 0 }, '销售结束日当天算销售期内')

// 只进货的分店：表格里没有的分店追加在后，按进货量从多到少。
const extra = purchaseOnlyStores(['B1 '], [{ code: 'B2', name: 'Branch Two' }], summaries)
assert.deepEqual(extra, [{ code: 'B2', name: 'Branch Two' }], '已在表格里的分店不重复，名称取自进货接口')
assert.deepEqual(purchaseOnlyStores([], [], summaries).map(store => store.code), ['B1', 'B2'], '无名称时用编码，按进货量降序')

// 悬停提示：当天没有进货时给出更早的最近一次进货。
assert.deepEqual(previousPurchase(points, points.findIndex(point => point.date === '2025-12-31')), { date: '2025-12-30', quantity: 84 })
assert.equal(previousPurchase(points, 0), null)

// 响应归一化：PascalCase、日期带时间、非法来源回落为总仓。
const trend = normalizeProductTrend({ ProductCode: 'P1', BranchCode: 'B1', WindowStart: '2025-10-30T00:00:00', WindowEnd: '2026-01-01T00:00:00', Daily: [{ Date: '2025-12-30T00:00:00', Quantity: 126 }, { Date: '', Quantity: 1 }] })
assert.deepEqual(trend, { productCode: 'P1', branchCode: 'B1', windowStart: '2025-10-30', windowEnd: '2026-01-01', daily: [{ date: '2025-12-30', quantity: 126 }] })
const purchases = normalizeProductPurchases({ productCode: 'P1', stores: [{ code: 'B1', name: '' }], batches: [{ branchCode: 'B1', date: '2025-12-30T00:00:00', source: 'Supplier', documentNo: 'INV-1', quantity: 12 }, { branchCode: 'B1', date: '2025-12-29', source: 'x', quantity: 3 }, { branchCode: '', date: '2025-12-29', source: 'warehouse', quantity: 1 }] })
assert.deepEqual(purchases.batches.map(batch => [batch.date, batch.source, batch.documentNo, batch.quantity]), [['2025-12-30', 'supplier', 'INV-1', 12], ['2025-12-29', 'warehouse', undefined, 3]])
assert.equal(purchases.stores[0].name, 'B1', '分店名称为空时回落为编码')

console.log('drawerLogic.test: ok')
