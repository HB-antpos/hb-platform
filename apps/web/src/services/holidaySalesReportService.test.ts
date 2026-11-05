import assert from 'node:assert/strict'
import {
  getHolidayCategoryDetail,
  getHolidayCategoryMap,
  getHolidayProductStores,
  normalizeHolidayCategoryDetail,
  normalizeHolidayProductStores,
  getHolidaySalesReport,
  normalizeHolidayCategoryMap,
  normalizeHolidaySalesReport,
} from './holidaySalesReportService'

const originalFetch = globalThis.fetch
const requests: { url: URL; init?: RequestInit }[] = []
let nextBody: unknown = {}

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: new URL(String(input), 'http://localhost'), init })
  return new Response(JSON.stringify(nextBody), { status: 200, headers: { 'content-type': 'application/json' } })
}) as typeof fetch

const product = {
  productCode: 'P1',
  itemNumber: 'XH0001',
  productName: 'Hanging Ghost',
  imageUrl: 'https://img/p1.jpg',
  supplierCode: '257',
  categoryKey: 'Decoration',
  amount: 120.5,
  quantity: 10,
  purchaseQuantity: 12,
  sellThrough: 0.83,
  storeCount: 3,
  soldOutStoreCount: 2,
  daysEarly: 6,
  soldOutDate: '2026-10-25',
  estimatedLostAmount: 33,
  leftoverQuantity: 2,
  leftoverCost: 4,
}

try {
  // 1) 请求参数：年份、单店、供应商、强制刷新；「全部供应商」不传参数，AbortSignal 透传。
  nextBody = { success: true, data: { year: 2026, totals: { amount: 100, sellThrough: 0.5 } } }
  const controller = new AbortController()
  const result = await getHolidaySalesReport({ year: 2026, branchCode: '1013', supplierCode: '257', forceRefresh: true }, controller.signal)
  const first = requests[0]
  assert.equal(first.url.pathname, '/api/react/v1/dashboard/holiday-sales-report')
  assert.equal(first.url.searchParams.get('year'), '2026')
  assert.equal(first.url.searchParams.get('branchCode'), '1013')
  assert.equal(first.url.searchParams.get('supplierCode'), '257')
  assert.equal(first.url.searchParams.get('forceRefresh'), 'true')
  assert.equal(first.init?.method, 'GET')
  assert.equal(first.init?.signal, controller.signal)
  assert.equal(result.totals.amount, 100, '信封应被解包并规范化')

  await getHolidaySalesReport({ year: 2025, supplierCode: 'all' })
  const second = requests[1]
  assert.equal(second.url.searchParams.has('supplierCode'), false, '全部供应商不应传 supplierCode')
  assert.equal(second.url.searchParams.has('branchCode'), false, '合计视图不应传 branchCode')
  assert.equal(second.url.searchParams.get('forceRefresh'), 'false')

  nextBody = { success: true, data: { year: 2026, groups: [] } }
  await getHolidayCategoryMap(2026)
  assert.equal(requests[2].url.pathname, '/api/react/v1/dashboard/holiday-sales-report/category-map')
  assert.equal(requests[2].url.searchParams.get('year'), '2026')

  // 2) 规范化：缺失 / 非数值的合计字段是 0，售罄率与卖断天数保持 null（不是 0%）。
  const report = normalizeHolidaySalesReport({
    year: 2026,
    holidayDate: '2026-10-31',
    salesFrom: '2026-08-01',
    salesThrough: '2026-11-03',
    totals: { amount: 'abc', quantity: Number.NaN, sellThrough: null, leftoverCost: 5 },
    daily: [
      { date: '2026-10-30', amount: 10 },
      { date: '2026-10-30', amount: 999 },
      { date: '2026-10-29', amount: 4 },
      { date: '2026-02-31x', amount: 1 },
      { amount: 1 },
    ],
    categories: [{ key: 'NotACategory', amount: 3 }],
    bestSellers: [product, { productName: 'no code' }, 'junk'],
    earlySoldOut: [{ ...product, daysEarly: 'x', soldOutDate: 'tomorrow', imageUrl: '' }],
    stores: [{ branchCode: '1013', amount: 9, sellThrough: undefined }, { branchName: 'no code' }],
  })
  assert.equal(report.totals.amount, 0)
  assert.equal(report.totals.quantity, 0)
  assert.equal(report.totals.sellThrough, null, '没有进货的售罄率必须是 null')
  assert.equal(report.totals.leftoverCost, 5)
  assert.deepEqual(report.daily, [{ date: '2026-10-29', amount: 4 }, { date: '2026-10-30', amount: 10 }], '重复日期保留第一条、非法日期丢弃并按日期排序')
  assert.equal(report.categories[0].key, 'Accessory', '未知大类回落到配件·道具')
  assert.equal(report.bestSellers.length, 1, '没有商品编码或不是对象的行要丢弃')
  assert.equal(report.bestSellers[0].daysEarly, 6)
  assert.equal(report.earlySoldOut[0].daysEarly, null)
  assert.equal(report.earlySoldOut[0].soldOutDate, null)
  assert.equal(report.earlySoldOut[0].imageUrl, null)
  assert.equal(report.stores.length, 1)
  assert.equal(report.stores[0].sellThrough, null)
  assert.equal(report.stores[0].branchName, '1013', '分店名缺失时用编码')
  assert.equal(report.benchmark, null)

  // 3) 单店基准：按大类的售罄率逐项读取，缺的保持 null。
  const withBenchmark = normalizeHolidaySalesReport({
    year: 2026,
    benchmark: { storeCount: 25, rank: 1, averageAmount: 11386, sellThrough: 0.68, categorySellThrough: { Costume: 0.56, Party: null } },
  })
  assert.equal(withBenchmark.benchmark?.rank, 1)
  assert.equal(withBenchmark.benchmark?.categorySellThrough.Costume, 0.56)
  assert.equal(withBenchmark.benchmark?.categorySellThrough.Party, null)
  assert.equal(withBenchmark.benchmark?.categorySellThrough.Decoration, null)

  // 4) 错误响应：success=false 抛错而不是当成空数据；响应不是对象也抛错。
  assert.throws(() => normalizeHolidaySalesReport({ success: false, message: '没有权限' }), /没有权限/)
  assert.throws(() => normalizeHolidaySalesReport('oops'), /malformed/)

  // 5) 分类对照
  const map = normalizeHolidayCategoryMap({
    data: { year: 2026, groups: [{ key: 'Costume', amount: 5, entries: [{ supplierCode: '243', supplierCategory: '', skuCount: 2, amount: 5 }] }] },
  })
  assert.equal(map.groups[0].entries[0].supplierCategory, null, '没有供应商分类名时为 null')
  assert.equal(map.groups[0].entries[0].supplierCode, '243')

  // 6) 商品分店明细（榜单钻取）：请求参数、合计行、重复分店只留第一行、缺编码抛错
  nextBody = { success: true, data: { year: 2026, productCode: 'P1', productName: 'Dagger', stores: [] } }
  await getHolidayProductStores(2026, 'G073184')
  const productCall = requests[requests.length - 1]
  assert.equal(productCall.url.pathname, '/api/react/v1/dashboard/holiday-sales-report/product')
  assert.equal(productCall.url.searchParams.get('productCode'), 'G073184')
  assert.equal(productCall.url.searchParams.get('year'), '2026')
  const stores = normalizeHolidayProductStores({
    year: 2026,
    holidayDate: '2026-10-31',
    productCode: 'P1',
    total: { amount: 5030, quantity: 835, purchaseQuantity: 1008, sellThrough: 0.8274 },
    storeCount: 20,
    soldOutStoreCount: 6,
    stores: [
      { branchCode: '1016', branchName: 'Greenhills', amount: 627, sellThrough: 1.09, soldOutDate: '2026-10-29', daysEarly: 2, firstSaleDate: '2026-09-20' },
      { branchCode: '1016', branchName: 'dup', amount: 1 },
      { branchCode: '1013', amount: 556, sellThrough: null, soldOutDate: 'soon', daysEarly: 'x' },
      { branchName: 'no code' },
    ],
  })
  assert.equal(stores.total.branchCode, '', '合计行没有分店编码')
  assert.equal(stores.total.sellThrough, 0.8274)
  assert.deepEqual(stores.stores.map((s) => s.branchCode), ['1016', '1013'], '同一家店只保留第一行、没有编码的丢弃')
  assert.equal(stores.stores[0].daysEarly, 2)
  assert.equal(stores.stores[1].branchName, '1013', '分店名缺失时用编码')
  assert.equal(stores.stores[1].sellThrough, null, '没有进货的售罄率必须是 null')
  assert.equal(stores.stores[1].soldOutDate, null)
  assert.equal(stores.stores[1].daysEarly, null)
  assert.throws(() => normalizeHolidayProductStores({ data: { stores: [] } }), /malformed/)

  // 7) 大类明细：请求参数（全部供应商 / 合计视图不传）、商品与分店规范化、缺大类抛错
  nextBody = { success: true, data: { year: 2026, category: { key: 'Costume' }, stores: [], products: [] } }
  await getHolidayCategoryDetail({ year: 2026, categoryKey: 'Costume', supplierCode: 'all' })
  const categoryCall = requests[requests.length - 1]
  assert.equal(categoryCall.url.pathname, '/api/react/v1/dashboard/holiday-sales-report/category')
  assert.equal(categoryCall.url.searchParams.get('categoryKey'), 'Costume')
  assert.equal(categoryCall.url.searchParams.has('branchCode'), false)
  assert.equal(categoryCall.url.searchParams.has('supplierCode'), false)
  await getHolidayCategoryDetail({ year: 2026, categoryKey: 'Costume', branchCode: '1013', supplierCode: '243' })
  const singleCall = requests[requests.length - 1]
  assert.equal(singleCall.url.searchParams.get('branchCode'), '1013')
  assert.equal(singleCall.url.searchParams.get('supplierCode'), '243')
  const detail = normalizeHolidayCategoryDetail({
    year: 2026,
    branchCode: '1013',
    category: { key: 'Costume', amount: 80432, sellThrough: 0.5653, skuCount: 469 },
    stores: [{ branchCode: '1013', amount: 17487 }],
    products: [{ productCode: 'X', productName: 'Cape', amount: 2487, sellThrough: null }, { productName: 'no code' }],
  })
  assert.equal(detail.category.key, 'Costume')
  assert.equal(detail.category.sellThrough, 0.5653)
  assert.equal(detail.branchCode, '1013')
  assert.equal(detail.stores.length, 1)
  assert.equal(detail.products.length, 1, '没有商品编码的行丢弃')
  assert.equal(detail.products[0].sellThrough, null)
  assert.throws(() => normalizeHolidayCategoryDetail({ data: { year: 2026 } }), /malformed/)

  console.log('holidaySalesReportService.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
