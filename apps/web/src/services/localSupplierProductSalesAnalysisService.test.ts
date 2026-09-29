import {
  getLocalSupplierProductSalesAnalysisOptions,
  getLocalSupplierProductSalesAnalysisSupplierCategoryOptions,
  queryLocalSupplierProductSalesAnalysisBranchDaily,
  queryLocalSupplierProductSalesAnalysisBranches,
  queryLocalSupplierProductSalesAnalysisCandidates,
  queryLocalSupplierProductSalesAnalysisInvoiceDetails,
  queryLocalSupplierProductSalesAnalysisProductDaily,
  queryLocalSupplierProductSalesAnalysisSummary,
  queryLocalSupplierProductSalesAnalysisBootstrap,
} from './localSupplierProductSalesAnalysisService'

function equal<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) throw new Error(`${message}: expected ${String(expected)}, received ${String(actual)}`)
}

function deepEqual(actual: unknown, expected: unknown, message: string) {
  equal(JSON.stringify(actual), JSON.stringify(expected), message)
}

const originalFetch = globalThis.fetch
const captured: Array<{ url: string; init?: RequestInit }> = []

function response(payload: unknown) {
  return new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

try {
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    captured.push({ url, init })
    if (url.includes('/supplier-category-options')) return response({ success: true, data: [{ SupplierCode: 'AU-1', Guid: 'supplier-root', Name: '玩具', IsSelectable: false, Children: [{ SupplierCode: 'AU-1', Guid: 'supplier-cat-1', ParentGuid: 'supplier-root', Name: '户外', IsSelectable: true }] }, { supplierCode: '200', guid: 'warehouse-cat-1', name: '家居' }, { SupplierCode: '', Guid: 'invalid', Name: '无效项' }] })
    if (url.includes('/options')) return response({ success: true, data: { WarehouseCategories: [{ Guid: 'cat-1', Name: '玩具' }], Suppliers: [{ Code: 'AU-1', Name: '澳洲供货商' }] } })
    if (url.endsWith('/candidates')) return response({ Success: true, Data: { Items: [{ ProductCode: 'LP-1', ItemNumber: '1001', ProductName: '本地玩具', ImageUrl: '/item.png' }], Total: 1, PageNumber: 2, PageSize: 20 } })
    if (url.endsWith('/summary')) return response({ success: true, data: { Totals: { PurchaseQuantity: 8, PurchaseAmount: 50, NetSalesQuantity: -2, NetSalesAmount: -12, SellThroughRate: null }, Items: [{ ProductCode: 'LP-1', Suppliers: [{ Code: 'AU-1', Name: '澳洲供货商' }], PurchaseQuantity: 8 }], Total: 1, PageNumber: 1, PageSize: 20 } })
    if (url.endsWith('/invoice-details')) return response({ success: true, data: { Items: [{ DetailGUID: 'D1', InvoiceNo: 'INV-1', PurchaseDate: '2026-08-18T00:00:00', Quantity: 3, PurchasePrice: 2.5, Amount: 7.5 }], Total: 1, PageNumber: 1, PageSize: 20 } })
    if (url.endsWith('/branches')) return response({ success: true, data: [{ BranchCode: 'S1', BranchName: '布里斯班店', NetSalesQuantity: 0, NetSalesAmount: 0, AverageUnitPrice: 0 }] })
    if (url.endsWith('/bootstrap')) {
      const body = JSON.parse(String(init?.body))
      if (!body.currentProductCode) {
        return response({ Success: true, Data: { Options: { WarehouseCategories: [], Suppliers: [] }, Candidates: { Items: [], Total: 0, PageNumber: 1, PageSize: 20 }, EffectiveSelection: { Mode: 'included', IncludedProductCodes: [], ExcludedProductCodes: [] }, CurrentProduct: null, Partial: false } })
      }
      return response({ Success: true, Data: {
        Options: { WarehouseCategories: [{ Guid: 'cat-1', Name: '玩具' }], Suppliers: [] },
        Candidates: { Items: [{ ProductCode: 'LP-1', ItemNumber: '1001', ProductName: '本地玩具' }], Total: 1, PageNumber: 1, PageSize: 20 },
        EffectiveSelection: { Mode: 'included', IncludedProductCodes: ['LP-1'], ExcludedProductCodes: [] },
        CurrentProduct: { ProductCode: 'LP-1', ItemNumber: '1001', ProductName: '本地玩具' },
        Summary: { Totals: { PurchaseQuantity: 8, PurchaseAmount: 50, NetSalesQuantity: -2, NetSalesAmount: -12, SellThroughRate: null }, Items: [{ ProductCode: 'LP-1', PurchaseQuantity: 8 }], Total: 1, PageNumber: 1, PageSize: 20 },
        InvoiceDetails: { Items: [{ DetailGUID: 'D1', InvoiceNo: 'INV-1', Quantity: 3 }], Total: 1, PageNumber: 1, PageSize: 20 },
        ProductDaily: [{ Date: '2026-08-18T00:00:00', PurchaseQuantity: 3, NetSalesQuantity: -1 }],
        Branches: [{ BranchCode: 'S1', NetSalesQuantity: 0, AverageUnitPrice: 0 }],
        Partial: true,
        SectionErrors: { Summary: '汇总加载失败' },
      } })
    }
    return response({ success: true, data: [{ Date: '2026-08-18T00:00:00', PurchaseQuantity: 3, PurchaseAmount: 7.5, NetSalesQuantity: -1, NetSalesAmount: -4, AverageUnitPrice: null }] })
  }) as typeof fetch

  const filter = {
    startDate: '2026-07-20',
    endDate: '2026-08-18',
    keyword: '玩具',
    categoryGuid: 'cat-1',
    supplierCode: 'AU-1',
    warehouseCategoryGuids: ['cat-1', 'cat-2'],
    supplierCodes: ['AU-1', '200'],
    supplierCategoryGuids: ['supplier-cat-1', 'warehouse-cat-1'],
    documentKeyword: 'INV',
  }
  const selection = { mode: 'included' as const, includedProductCodes: ['LP-1'], excludedProductCodes: [] }
  const options = await getLocalSupplierProductSalesAnalysisOptions()
  equal(options.data.warehouseCategories[0]?.guid, 'cat-1', '选项应归一化 PascalCase')
  equal(options.data.suppliers[0]?.code, 'AU-1', '供应商选项应归一化')

  const candidates = await queryLocalSupplierProductSalesAnalysisCandidates({ filter, selection, pageNumber: 2, pageSize: 20 })
  equal(candidates.data.items[0]?.productCode, 'LP-1', '候选商品应归一化')
  equal(candidates.data.pageNumber, 2, '候选分页应保留')
  deepEqual(JSON.parse(String(captured[1]?.init?.body)).filter, filter, '公共筛选字段必须按契约发送')

  const summary = await queryLocalSupplierProductSalesAnalysisSummary({ filter, selection, pageNumber: 1, pageSize: 20, forceRefresh: true })
  equal(summary.data.totals.netSalesQuantity, -2, '汇总净销量允许为负')
  equal(summary.data.totals.sellThroughRate, null, '可空售进比应保留 null')
  equal(JSON.parse(String(captured[2]?.init?.body)).forceRefresh, true, '刷新只在本轮请求携带 forceRefresh')

  const daily = await queryLocalSupplierProductSalesAnalysisProductDaily({ filter, selection, currentProductCode: 'LP-1' })
  equal(daily.data[0]?.date, '2026-08-18', '日期必须归一化为 YYYY-MM-DD')
  equal(daily.data[0]?.averageUnitPrice, null, '净销量为零或无均价时保留 null')

  const details = await queryLocalSupplierProductSalesAnalysisInvoiceDetails({ filter, selection, currentProductCode: 'LP-1', pageNumber: 1, pageSize: 20 })
  equal(details.data.items[0]?.purchaseDate, '2026-08-18', '明细日期必须归一化')

  const branches = await queryLocalSupplierProductSalesAnalysisBranches({ filter, selection, currentProductCode: 'LP-1' })
  equal(branches.data[0]?.averageUnitPrice, null, '净销量零的分店均价应归一化为空')
  const branchDaily = await queryLocalSupplierProductSalesAnalysisBranchDaily({ filter, selection, currentProductCode: 'LP-1', branchCode: 'S1' })
  equal(branchDaily.data[0]?.date, '2026-08-18', '分店日趋势日期必须归一化')

  const bootstrapBody = { filter, selection, currentProductCode: 'LP-1', autoSelectFirst: true, candidatePageNumber: 2, candidatePageSize: 25, summaryPageNumber: 1, summaryPageSize: 50, forceRefresh: true }
  const bootstrap = await queryLocalSupplierProductSalesAnalysisBootstrap(bootstrapBody)
  const bootstrapBodySent = JSON.parse(String(captured[7]?.init?.body))
  equal(bootstrapBodySent.autoSelectFirst, true, 'bootstrap 必须携带 autoSelectFirst')
  equal(bootstrapBodySent.candidatePageNumber, 2, 'bootstrap 必须携带候选分页页号')
  equal(bootstrapBodySent.candidatePageSize, 25, 'bootstrap 必须携带候选分页大小')
  equal(bootstrapBodySent.summaryPageNumber, 1, 'bootstrap 必须携带汇总分页页号')
  equal(bootstrapBodySent.summaryPageSize, 50, 'bootstrap 必须携带汇总分页大小')
  equal(bootstrapBodySent.forceRefresh, true, 'bootstrap 必须携带 forceRefresh')
  equal(bootstrapBodySent.currentProductCode, 'LP-1', 'bootstrap 必须携带当前商品代码')
  equal(bootstrap.data.options.warehouseCategories[0]?.guid, 'cat-1', 'bootstrap options 必须归一化 PascalCase')
  equal(bootstrap.data.candidates.items[0]?.productCode, 'LP-1', 'bootstrap candidates 必须归一化')
  equal(bootstrap.data.candidates.pageNumber, 1, 'bootstrap candidates 必须保留分页')
  equal(bootstrap.data.effectiveSelection?.includedProductCodes[0], 'LP-1', 'bootstrap effectiveSelection 必须归一化')
  equal(bootstrap.data.currentProduct?.productName, '本地玩具', 'bootstrap currentProduct 必须归一化')
  equal(bootstrap.data.summary?.totals.netSalesAmount, -12, 'bootstrap summary 必须归一化')
  equal(bootstrap.data.invoiceDetails?.items[0]?.invoiceNo, 'INV-1', 'bootstrap invoiceDetails 必须归一化')
  equal(bootstrap.data.productDaily?.[0]?.date, '2026-08-18', 'bootstrap productDaily 必须归一化日期')
  equal(bootstrap.data.branches?.[0]?.branchCode, 'S1', 'bootstrap branches 必须归一化')
  equal(bootstrap.data.partial, true, 'bootstrap 必须保留 partial 标记')
  equal(bootstrap.data.sectionErrors?.summary, '汇总加载失败', 'bootstrap sectionErrors 必须归一化为 camel 键')

  const minimal = await queryLocalSupplierProductSalesAnalysisBootstrap({ filter, autoSelectFirst: true, candidatePageNumber: 1, candidatePageSize: 20, summaryPageNumber: 1, summaryPageSize: 50 })
  equal(minimal.data.summary, null, '缺失 summary 分段必须归一化为 null')
  equal(minimal.data.invoiceDetails, null, '缺失 invoiceDetails 分段必须归一化为 null')
  equal(minimal.data.productDaily?.length, 0, '缺失 productDaily 分段必须归一化为空数组')
  equal(minimal.data.branches?.length, 0, '缺失 branches 分段必须归一化为空数组')
  equal(minimal.data.partial, false, '无分段失败时 partial 必须为 false')

  const supplierCategories = await getLocalSupplierProductSalesAnalysisSupplierCategoryOptions(['AU-1', '200'])
  deepEqual(supplierCategories.data, [
    { supplierCode: 'AU-1', guid: 'supplier-root', name: '玩具', isSelectable: false, children: [{ supplierCode: 'AU-1', guid: 'supplier-cat-1', parentGuid: 'supplier-root', name: '户外', isSelectable: true, children: [] }] },
    { supplierCode: '200', guid: 'warehouse-cat-1', name: '家居', isSelectable: true, children: [] },
  ], '供应商分类选项应归一化层级并丢弃缺少供应商编码的无效项')
  const categoryUrl = new URL(captured[captured.length - 1]!.url, 'https://test.local')
  equal(categoryUrl.searchParams.get('tree'), 'true', '供应商分类树请求必须显式启用 tree 参数')
  deepEqual(categoryUrl.searchParams.getAll('supplierCodes'), ['AU-1', '200'], '供应商分类请求必须保留多选供应商参数')

  equal(captured.map((item) => item.url).filter((url) => url.includes('/local-supplier-product-sales-analysis/')).length, 10, '必须命中全部契约端点（七个分段 + 两个 bootstrap + 供应商分类）')
} finally {
  globalThis.fetch = originalFetch
}

console.log('localSupplierProductSalesAnalysisService.test: ok')
