import assert from 'node:assert/strict'
import { fetchSalesDetailReport, type SalesDetailQuery } from './reportService'

const original = globalThis.fetch
const controller = new AbortController()
const calls: URL[] = []
let pending = false
const query: SalesDetailQuery = { kind: 'china', startDate: '2026-09-01', endDate: '2026-09-06', compareMode: 'ByWeek',
  branchCodes: ['S1', 'S2'], selectedBranchCode: 'S1', selectedSupplierCode: 'HB215', selectedSupplierCodes: ['HB215', 'HB216'],
  warehouseCategoryGuids: ['WC1', 'WC2'], selectedProductCode: 'P1', search: '玛索 pen', pageIndex: 3, pageSize: 20 }
const section = (code: string) => ({ total: 1, rows: [{ Code: code, Revenue: 10, GrossProfit: null,
  GrossMarginRate: null, CompareGrossProfit: 0, CompareGrossMarginRate: 0 }] })

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), 'http://localhost')
  calls.push(url)
  assert.equal(url.pathname, '/api/react/v1/dashboard/sales-detail-report')
  assert.equal(url.searchParams.get('selectedProductCode'), 'P1')
  assert.equal(url.searchParams.get('selectedBranchCode'), 'S1')
  assert.deepEqual(url.searchParams.getAll('branchCodes'), ['S1', 'S2'])
  assert.deepEqual(url.searchParams.getAll('selectedSupplierCodes'), ['HB215', 'HB216'])
  assert.deepEqual(url.searchParams.getAll('warehouseCategoryGuids'), ['WC1', 'WC2'])
  assert.equal(url.searchParams.get('search'), '玛索 pen')
  assert.equal(url.searchParams.get('pageIndex'), '3')
  assert.equal(init?.signal, controller.signal)
  assert.equal(init?.credentials, 'include')
  const productsOnly = url.searchParams.getAll('sections').length > 0
  return new Response(JSON.stringify({ success: true, statisticStatus: pending ? 'Pending' : 'Fresh', cacheVersion: 'version-a',
    data: pending ? { summary: null, suppliers: null, branches: null, products: null } : productsOnly
      ? { summary: null, suppliers: null, branches: null, products: section('P1') }
      : { summary: section('TOTAL'), suppliers: section('HB215'), branches: section('S1'), products: section('P1') } }),
    { headers: { 'content-type': 'application/json' } })
}) as typeof fetch

try {
  const bundle = await fetchSalesDetailReport(query, controller.signal)
  assert.equal(calls[0]!.searchParams.has('sections'), false, '完整加载省略 sections，一次返回四栏')
  assert.equal(bundle.data.summary?.rows[0]?.code, 'TOTAL')
  assert.equal(bundle.data.suppliers?.rows[0]?.grossProfit, null)
  assert.equal(bundle.data.products?.rows[0]?.compareGrossProfit, 0)
  assert.equal(bundle.cacheVersion, 'version-a')

  const products = await fetchSalesDetailReport(query, controller.signal, ['products'])
  assert.deepEqual(calls[1]!.searchParams.getAll('sections'), ['products'], '翻页只请求商品栏')
  assert.equal(products.data.products?.total, 1)
  assert.equal(products.data.summary, undefined, 'products-only 不把缺省栏伪造为空数据')

  pending = true
  const waiting = await fetchSalesDetailReport(query, controller.signal)
  assert.equal(waiting.statisticStatus, 'Pending')
  assert.deepEqual(waiting.data, {}, 'Pending 空包必须留给查询 hook 轮询，不能伪造为零或抛错')
  console.log('销售明细聚合请求、商品分页、取消与快照包络：通过')
} finally { globalThis.fetch = original }
