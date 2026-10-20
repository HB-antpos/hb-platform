import assert from 'node:assert/strict'

import { fetchProductPurchases, fetchProductTrend, type SalesDetailQuery } from './reportService'

const original = globalThis.fetch
const controller = new AbortController()
const calls: URL[] = []
let trendStatus = 'Fresh'
const query: SalesDetailQuery = { kind: 'australia', startDate: '2025-12-29', endDate: '2026-01-01', compareMode: 'ByDate',
  compareStartDate: '2024-12-30', compareEndDate: '2025-01-02', branchCodes: ['S1', 'S2'], selectedBranchCode: 'S9',
  selectedSupplierCode: 'HB215', search: 'sparkler', pageIndex: 3, pageSize: 20 }

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), 'http://localhost')
  calls.push(url)
  assert.equal(init?.signal, controller.signal, '请求必须透传 AbortSignal，关闭抽屉时取消在途请求')
  const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } })
  if (url.pathname === '/api/react/v1/dashboard/sales-detail-product-trend') {
    return json({ success: true, statisticStatus: trendStatus, cacheVersion: 'v1',
      data: { ProductCode: 'P1', BranchCode: url.searchParams.get('selectedBranchCode'), WindowStart: '2025-10-30T00:00:00', WindowEnd: '2026-01-01T00:00:00',
        Daily: [{ Date: '2025-12-30T00:00:00', Quantity: 126 }] } })
  }
  assert.equal(url.pathname, '/api/react/v1/dashboard/sales-detail-product-purchases')
  return json({ success: true, data: { productCode: 'P1', windowStart: '2025-10-30', windowEnd: '2026-01-01',
    stores: [{ code: 'S1', name: 'Store One' }], batches: [{ branchCode: 'S1', date: '2025-12-30T00:00:00', source: 'warehouse', documentNo: '2025-1731', quantity: 60 }] } })
}) as typeof fetch

try {
  const trend = await fetchProductTrend(query, 'P1', controller.signal, 'S1')
  const trendUrl = calls[0]!
  // 只传曲线需要的参数：销售区间、商品、分店范围、所选单店；不带报表的分页、搜索、供应商等条件。
  assert.equal(trendUrl.searchParams.get('startDate'), '2025-12-29')
  assert.equal(trendUrl.searchParams.get('endDate'), '2026-01-01')
  assert.equal(trendUrl.searchParams.get('productCode'), 'P1')
  assert.deepEqual(trendUrl.searchParams.getAll('branchCodes'), ['S1', 'S2'])
  assert.equal(trendUrl.searchParams.get('selectedBranchCode'), 'S1', '只看某家店时由参数传入，不取页面上的分店选择')
  for (const key of ['search', 'pageIndex', 'pageSize', 'selectedSupplierCode', 'kind', 'compareStartDate']) assert.equal(trendUrl.searchParams.has(key), false, `曲线请求不应带 ${key}`)
  assert.deepEqual(trend.data, { productCode: 'P1', branchCode: 'S1', windowStart: '2025-10-30', windowEnd: '2026-01-01', daily: [{ date: '2025-12-30', quantity: 126 }] })
  assert.equal(trend.statisticStatus, 'Fresh')

  await fetchProductTrend(query, 'P1', controller.signal)
  assert.equal(calls[1]!.searchParams.has('selectedBranchCode'), false, '不指定单店时不传 selectedBranchCode')

  trendStatus = 'Pending'
  const waiting = await fetchProductTrend(query, 'P1', controller.signal)
  assert.equal(waiting.statisticStatus, 'Pending', '统计未就绪的状态必须带回，交给查询 hook 重试与提示')

  const purchases = await fetchProductPurchases(query, 'P1', controller.signal)
  const purchaseUrl = calls[3]!
  assert.deepEqual([...purchaseUrl.searchParams.keys()].sort(), ['branchCodes', 'branchCodes', 'endDate', 'productCode', 'startDate'], '进货请求只带区间、商品与分店范围')
  assert.equal(purchases.statisticStatus, undefined, '进货不依赖销量统计快照，没有统计状态')
  assert.deepEqual(purchases.data.batches, [{ branchCode: 'S1', date: '2025-12-30', source: 'warehouse', documentNo: '2025-1731', quantity: 60 }])
  console.log('商品抽屉曲线与进货请求：通过')
} finally { globalThis.fetch = original }
