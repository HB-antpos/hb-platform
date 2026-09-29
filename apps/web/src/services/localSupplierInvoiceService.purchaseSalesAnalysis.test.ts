import dayjs from 'dayjs'
import {
  __localSupplierInvoiceServiceTestOnly,
  getLocalSupplierPurchaseSalesAnalysis,
  getLocalSupplierPurchaseSalesAnalysisCategoryTree,
  getLocalSupplierPurchaseSalesAnalysisSupplierOptions,
  getShopLocalSupplierPurchaseSalesAnalysis,
  getShopLocalSupplierPurchaseSalesAnalysisSupplierOptions,
} from './localSupplierInvoiceService'
import {
  buildPurchaseSalesAnalysisImageSourceChain,
  DEFAULT_PRODUCT_IMAGE_BASE_URL,
  DEFAULT_PURCHASE_SALES_ANALYSIS_PAGE_SIZE,
  getDefaultPurchaseSalesAnalysisDateRange,
  normalizePurchaseSalesAnalysisPageSize,
  PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_BY,
  PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_ORDER,
  PURCHASE_SALES_ANALYSIS_MIN_TABLE_BODY_HEIGHT,
  resolvePurchaseSalesAnalysisTableBodyHeight,
  TRANSPARENT_IMAGE_FALLBACK,
  toPurchaseSalesAnalysisSort,
} from '../pages/PosAdmin/LocalSupplierPurchaseSalesAnalysis/helpers'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const originalFetch = globalThis.fetch
let requestUrl = ''
let requestMethod = ''
let supplierOptionsRequestUrl = ''
let categoryTreeRequestUrl = ''
let shopRequestUrl = ''
let shopSupplierOptionsRequestUrl = ''
let refreshRequestCount = 0

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input)

  if (url.startsWith('https://api.ipify.org')) {
    return new Response(JSON.stringify({ ip: '8.8.8.88' }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  if (url.endsWith('/api/Auth/session/refresh')) {
    refreshRequestCount += 1
    return new Response(JSON.stringify({ success: true, data: {} }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  if (url.includes('/api/react/v1/local-supplier-invoices/shop/purchase-sales-analysis/supplier-options')) {
    shopSupplierOptionsRequestUrl = url
    return new Response(JSON.stringify({ success: true, data: [{ label: 'Brazco', value: 'BRZ' }] }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  if (url.includes('/api/react/v1/local-supplier-invoices/shop/purchase-sales-analysis')) {
    shopRequestUrl = url
    return new Response(JSON.stringify({ success: true, data: { Items: [], Total: 0, Page: 1, PageSize: 100 } }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  if (url.includes('/api/react/v1/local-supplier-invoices/purchase-sales-analysis/supplier-options')) {
    supplierOptionsRequestUrl = url
    return new Response(JSON.stringify({
      success: true,
      data: [
        { Label: 'Malmar', Value: '200' },
        { Label: '', Value: 'BROKEN' },
      ],
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  if (url.includes('/api/react/v1/local-supplier-invoices/purchase-sales-analysis/category-tree')) {
    categoryTreeRequestUrl = url
    return new Response(JSON.stringify({ success: true, data: [
      { categoryGuid: 'CAT-A', name: 'Cards', isActive: true, children: [] },
    ] }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  if (url.includes('/api/react/v1/local-supplier-invoices/purchase-sales-analysis')) {
    requestUrl = url
    requestMethod = String(init?.method || 'GET')
    return new Response(JSON.stringify({
      success: true,
      data: {
        Items: [
          {
            StoreCode: 'S001',
            StoreName: 'Sydney',
            ProductCode: 'P001',
            ItemNumber: 'HB001',
            Barcode: '9350001',
            ProductName: '苹果',
            ProductImage: 'https://example.com/a.jpg',
            SupplierCode: 'SUP01',
            SupplierName: '供应商 A',
            LatestPurchaseDate: '2026-06-01',
            LatestPurchaseQty: 12.5,
            PreviousPurchaseDate: '2026-05-12',
            PreviousPurchaseQty: 9,
            PurchaseIntervalDays: 20,
            SalesBetweenPurchases: 18,
            SalesQty30: 20,
            SalesQty60: 30,
            SalesQty90: 40,
            TotalSalesSinceLatestPurchase: 137,
            SalesStatisticLastUpdate: '2026-06-25T08:00:00',
            DailySales: [
              { Date: '2026-06-01T00:00:00', Quantity: 3 },
              { date: '2026-06-02', quantity: -1 },
              { Quantity: 9 },
              'broken',
            ],
            Purchases: [
              { Date: '2026-05-12T00:00:00', Quantity: 9 },
              { Date: '2026-06-01T00:00:00', Quantity: 13 },
            ],
          },
          {
            ProductCode: 'BROKEN',
          },
        ],
        Total: 1,
        Page: 2,
        PageSize: 999,
        SalesStatisticLastUpdate: '2026-06-25T09:00:00',
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  return new Response(JSON.stringify({ success: true, data: null }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  const defaultRange = getDefaultPurchaseSalesAnalysisDateRange(dayjs('2026-06-25'))
  assertEqual(defaultRange[0].format('YYYY-MM-DD'), '2025-12-27', '默认开始日期应为 180 天前')
  assertEqual(defaultRange[1].format('YYYY-MM-DD'), '2026-06-25', '默认结束日期应为当天')

  assertEqual(
    normalizePurchaseSalesAnalysisPageSize(undefined),
    DEFAULT_PURCHASE_SALES_ANALYSIS_PAGE_SIZE,
    '分页默认值应为 100',
  )
  assertEqual(normalizePurchaseSalesAnalysisPageSize(50), 50, '允许的分页值应保留')
  assertEqual(
    normalizePurchaseSalesAnalysisPageSize(80),
    DEFAULT_PURCHASE_SALES_ANALYSIS_PAGE_SIZE,
    '不允许的分页值应回退到 100',
  )
  assertEqual(
    PURCHASE_SALES_ANALYSIS_MIN_TABLE_BODY_HEIGHT,
    520,
    '明细表格应保留足够高度展示多行趋势数据',
  )
  assertEqual(
    resolvePurchaseSalesAnalysisTableBodyHeight(180),
    520,
    '较矮视口也不能把明细表体压缩回只能显示少量数据行',
  )
  assertEqual(
    resolvePurchaseSalesAnalysisTableBodyHeight(680.8),
    680,
    '较高视口应继续使用全部可用高度',
  )

  assertDeepEqual(
    toPurchaseSalesAnalysisSort('salesBetweenPurchases', 'descend'),
    { sortBy: 'salesBetweenPurchases', sortOrder: 'desc' },
    '排序器应转换为后端需要的 sortBy 和 sortOrder',
  )
  assertDeepEqual(
    toPurchaseSalesAnalysisSort('totalSalesSinceLatestPurchase', 'descend'),
    { sortBy: 'totalSalesSinceLatestPurchase', sortOrder: 'desc' },
    '总销量应在排序白名单内',
  )
  assertDeepEqual(
    toPurchaseSalesAnalysisSort('purchaseIntervalDays', 'descend'),
    { sortBy: PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_BY, sortOrder: PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_ORDER },
    '已下线的间隔天数列不应再作为排序字段',
  )
  assertDeepEqual(
    toPurchaseSalesAnalysisSort('supplierName', 'ascend'),
    { sortBy: PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_BY, sortOrder: PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_ORDER },
    '不在后端白名单内的排序字段应回退默认排序（当前默认是总销量降序）',
  )

  const normalized = __localSupplierInvoiceServiceTestOnly.normalizePurchaseSalesAnalysisResponse({
    Items: [
      {
        StoreCode: 'S001',
        ProductCode: 'P001',
        SupplierCode: 'SUP01',
        SalesQty30: 1,
        SalesQty60: 2,
        SalesQty90: 3,
      },
      {
        StoreCode: 'S002',
      },
    ],
    Total: 9,
    Page: 3,
    PageSize: 70,
  })
  assertEqual(normalized.items.length, 1, 'normalizer 应过滤掉缺少关键字段的行')
  assertEqual(normalized.total, 9, 'normalizer 应保留总数')
  assertEqual(normalized.pageSize, 100, 'normalizer 应把非法 pageSize 归一化为 100')

  const normalizedSupplierOptions =
    __localSupplierInvoiceServiceTestOnly.normalizePurchaseSalesAnalysisSupplierOptions({
      data: [
        { Label: 'Malmar', Value: '200' },
        { Label: '', Value: 'BROKEN' },
      ],
    })
  assertDeepEqual(
    normalizedSupplierOptions,
    [{ label: 'Malmar', value: '200' }],
    '供应商选项 normalizer 应过滤缺少 label 或 value 的项',
  )

  const supplierOptions = await getLocalSupplierPurchaseSalesAnalysisSupplierOptions('S001')
  const parsedSupplierOptionsUrl = new URL(supplierOptionsRequestUrl, 'https://example.test')
  assertEqual(
    parsedSupplierOptionsUrl.searchParams.get('storeCode'),
    'S001',
    '供应商选项接口应按已选分店传参',
  )
  assertDeepEqual(supplierOptions, [{ label: 'Malmar', value: '200' }], '供应商选项接口应归一化响应')

  const categoryTree = await getLocalSupplierPurchaseSalesAnalysisCategoryTree('SUP01', 'S001')
  const parsedCategoryTreeUrl = new URL(categoryTreeRequestUrl, 'https://example.test')
  assertEqual(parsedCategoryTreeUrl.searchParams.get('supplierCode'), 'SUP01', '分类树应限定当前供应商')
  assertEqual(parsedCategoryTreeUrl.searchParams.get('storeCode'), 'S001', '分类树应携带当前分店供后端校验可见范围')
  assertEqual(categoryTree[0]?.categoryGuid, 'CAT-A', '分类树响应应返回可选分类')

  const result = await getLocalSupplierPurchaseSalesAnalysis({
    storeCode: 'S001',
    supplierCode: 'SUP01',
    supplierCategoryGuids: ['CAT-A', 'CAT-B'],
    orderDateStart: '2026-01-01',
    orderDateEnd: '2026-06-25',
    keyword: '苹果',
    sortBy: 'totalSalesSinceLatestPurchase',
    sortOrder: 'desc',
    page: 2,
    pageSize: 200,
  })

  const parsedUrl = new URL(requestUrl, 'https://example.test')
  assertEqual(requestMethod, 'GET', '查询接口应使用 GET')
  assertEqual(parsedUrl.searchParams.get('sortBy'), 'totalSalesSinceLatestPurchase', '总销量排序字段应透传到后端')
  assertEqual(parsedUrl.searchParams.get('sortOrder'), 'desc', '排序方向应透传到后端')
  assertEqual(parsedUrl.searchParams.get('pageSize'), '200', '合法 pageSize 应透传到后端')
  assertDeepEqual(parsedUrl.searchParams.getAll('supplierCategoryGuids'), ['CAT-A', 'CAT-B'], '分类多选应重复序列化为查询参数')
  assertEqual(result.items.length, 1, '接口 normalizer 应过滤无效行')
  assertEqual(result.pageSize, 100, '接口响应中的非法 pageSize 应回退到 100')
  assertEqual(result.items[0].totalSalesSinceLatestPurchase, 137, '总销量应从后端响应归一化')
  assertDeepEqual(
    result.items[0].dailySales,
    [{ date: '2026-06-01', quantity: 3 }, { date: '2026-06-02', quantity: -1 }],
    '逐日销量应裁剪为 yyyy-MM-dd、保留退货负数并丢弃缺日期或非法项',
  )
  assertDeepEqual(
    result.items[0].purchases,
    [{ date: '2026-05-12', quantity: 9 }, { date: '2026-06-01', quantity: 13 }],
    '进货事件应归一化为日期与数量',
  )

  const shopSupplierOptions = await getShopLocalSupplierPurchaseSalesAnalysisSupplierOptions('S001')
  assertDeepEqual(shopSupplierOptions, [{ label: 'Brazco', value: 'BRZ' }], '前台供应商选项应走前台只读接口并归一化')
  assertEqual(
    new URL(shopSupplierOptionsRequestUrl, 'https://example.test').searchParams.get('storeCode'),
    'S001',
    '前台供应商选项必须携带当前分店',
  )
  const shopResult = await getShopLocalSupplierPurchaseSalesAnalysis({ storeCode: 'S001', supplierCode: 'BRZ', page: 1, pageSize: 100 })
  assertEqual(shopRequestUrl.includes('/local-supplier-invoices/shop/purchase-sales-analysis'), true, '前台分析必须请求 shop 前缀接口')
  assertDeepEqual(shopResult.items, [], '前台分析空结果应归一化为空数组')

  const imageChainWithFallback = buildPurchaseSalesAnalysisImageSourceChain(
    'https://img.example.com/a.jpg',
    'HB001',
    'P001',
  )
  assertDeepEqual(
    imageChainWithFallback,
    [
      'https://img.example.com/a.jpg',
      `${DEFAULT_PRODUCT_IMAGE_BASE_URL}/HB001.jpg`,
      TRANSPARENT_IMAGE_FALLBACK,
    ],
    '图片兜底链路应包含原图、默认 COS 图和透明图',
  )

  const imageChainWithoutPrimary = buildPurchaseSalesAnalysisImageSourceChain(undefined, '', '')
  assertDeepEqual(
    imageChainWithoutPrimary,
    [TRANSPARENT_IMAGE_FALLBACK],
    '缺少货号和主图时应直接退回透明图',
  )

  assertEqual(refreshRequestCount, 0, '正常测试不应触发刷新令牌请求')
} finally {
  globalThis.fetch = originalFetch
}
