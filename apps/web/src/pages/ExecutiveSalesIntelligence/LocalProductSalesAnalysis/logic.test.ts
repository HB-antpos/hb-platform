import {
  applyCandidateSelection,
  applyLocalProductSalesAnalysisBootstrapResult,
  applyLocalProductSalesAnalysisSectionResult,
  buildBranchPriceTiers,
  buildTrendChartModel,
  countInclusiveDays,
  getSellThroughLevel,
  safeDivide,
  buildBrisbaneDefaultRange,
  buildLocalProductSalesAnalysisBootstrapRequest,
  canSetCurrentProduct,
  collectSupplierCategoryLeafGuids,
  clearLocalProductSalesAnalysisDetailSections,
  clearLocalProductSalesAnalysisSectionError,
  createEmptyLocalProductSalesAnalysisState,
  createIncludedSelection,
  createLatestRequestGuard,
  createPageRequestTimeout,
  getDateRangeError,
  getCurrentProductAfterCancellation,
  isSelected,
  mergeVisibleSupplierCategorySelection,
  PAGE_BOOTSTRAP_TIMEOUT_SECONDS,
  PAGE_SECTION_TIMEOUT_SECONDS,
  setLocalProductSalesAnalysisSectionError,
} from './logic'

function equal<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) throw new Error(`${message}: expected ${String(expected)}, received ${String(actual)}`)
}

function deepEqual(actual: unknown, expected: unknown, message: string) {
  equal(JSON.stringify(actual), JSON.stringify(expected), message)
}

const range = buildBrisbaneDefaultRange(30, new Date('2026-08-19T04:00:00.000Z'))
deepEqual(range, { startDate: '2026-07-20', endDate: '2026-08-18' }, '默认范围必须是 Brisbane 昨天向前 30 天')
equal(getDateRangeError('2025-08-18', '2026-08-18', '2026-08-18'), undefined, '366 天的含首尾范围必须允许')
equal(getDateRangeError('2025-08-17', '2026-08-18', '2026-08-18'), '参数错误：日期范围不能超过 366 天', '超过 366 天必须给局部参数错误')
equal(getDateRangeError('2026-08-18', '2026-08-19', '2026-08-18'), '参数错误：日期范围截至 Brisbane 昨天', '今天和未来必须禁止')

let selection = createIncludedSelection(['P1'])
selection = applyCandidateSelection(selection, 'P2', true)
equal(isSelected(selection, 'P1'), true, '跨页已选商品必须保留')
equal(isSelected(selection, 'P2'), true, '当前页选择必须加入')
selection = applyCandidateSelection(selection, 'P1', false)
equal(isSelected(selection, 'P1'), false, '取消当前商品必须移出选择')
equal(canSetCurrentProduct(selection, 'P1'), false, '未勾选候选行不能设为当前商品')
equal(canSetCurrentProduct(selection, 'P2'), true, '已勾选候选行可以设为当前商品')

deepEqual(
  mergeVisibleSupplierCategorySelection(['hidden-child', 'visible-old'], ['visible-new'], new Set(['visible-old', 'visible-new'])),
  ['hidden-child', 'visible-new'],
  '树搜索后勾选可见分类必须保留隐藏分支中的已选分类',
)
deepEqual(
  collectSupplierCategoryLeafGuids([{ guid: 'root', children: [{ guid: 'child' }] }]),
  ['child'],
  '重开供应商分类树时必须保留已选子分类 GUID',
)

const current = { productCode: 'P2', productName: '跨页商品' }
equal(getCurrentProductAfterCancellation(current, [{ productCode: 'P3', productName: '首项商品' }], false)?.productCode, 'P2', '未取消时必须保留跨页快照')
equal(getCurrentProductAfterCancellation(current, [{ productCode: 'P3', productName: '首项商品' }], true)?.productCode, 'P3', '仅取消当前商品时迁移到 summary 首项')

const bootstrapFilter = { startDate: '2026-07-20', endDate: '2026-08-18', keyword: '玩具' }
const bootstrapPages = { candidatePageNumber: 1, candidatePageSize: 20, summaryPageNumber: 1, summaryPageSize: 50 }

// 挂载/查询/重置：自动选首项、不强制刷新、不带旧选择与旧当前商品
const firstScreenRequest = buildLocalProductSalesAnalysisBootstrapRequest({
  filter: bootstrapFilter, autoSelectFirst: true, forceRefresh: false, ...bootstrapPages,
})
deepEqual(firstScreenRequest, { filter: bootstrapFilter, autoSelectFirst: true, forceRefresh: false, ...bootstrapPages }, '首屏/查询 bootstrap 必须省略旧选择并自动选中首项')
equal(JSON.stringify(firstScreenRequest).includes('selection'), false, '首屏/查询不得携带旧选择')
equal(JSON.stringify(firstScreenRequest).includes('currentProductCode'), false, '首屏/查询不得携带旧当前商品')

// 刷新：保留原选择/当前商品、不自动选首项、强制刷新
const carriedSelection = { mode: 'included' as const, includedProductCodes: ['LP-1'], excludedProductCodes: [] }
const refreshRequest = buildLocalProductSalesAnalysisBootstrapRequest({
  filter: bootstrapFilter, selection: carriedSelection, currentProductCode: 'LP-1', autoSelectFirst: false, forceRefresh: true, ...bootstrapPages,
})
deepEqual(refreshRequest, { filter: bootstrapFilter, selection: carriedSelection, currentProductCode: 'LP-1', autoSelectFirst: false, forceRefresh: true, ...bootstrapPages }, '刷新必须携带原选择与当前商品且不自动选首项')

// 尊重用户主动清空：刷新携带空选择且不自动选首项
const clearedSelection = { mode: 'included' as const, includedProductCodes: [], excludedProductCodes: [] }
const clearedRequest = buildLocalProductSalesAnalysisBootstrapRequest({
  filter: bootstrapFilter, selection: clearedSelection, autoSelectFirst: false, forceRefresh: true, ...bootstrapPages,
})
deepEqual(clearedRequest, { filter: bootstrapFilter, selection: clearedSelection, autoSelectFirst: false, forceRefresh: true, ...bootstrapPages }, '刷新必须尊重用户主动清空选择')

// bootstrap 允许已证实的 9 秒内成功响应完成，分段交互仍保持 8 秒上限
equal(PAGE_BOOTSTRAP_TIMEOUT_SECONDS, 15, 'bootstrap 安全超时必须是 15 秒')
equal(PAGE_SECTION_TIMEOUT_SECONDS, 8, '分段请求安全超时必须保持 8 秒')
{
  const timeout = createPageRequestTimeout(0.01)
  await new Promise((resolve) => setTimeout(resolve, 30))
  equal(timeout.signal.aborted, true, '超过安全超时必须中止请求')
  equal(timeout.signal.reason?.name, 'AbortError', '超时中止必须是 AbortError')
  timeout.clear()
}
{
  const timeout = createPageRequestTimeout(0.01)
  timeout.clear()
  await new Promise((resolve) => setTimeout(resolve, 30))
  equal(timeout.signal.aborted, false, '成功后清理超时不得再中止请求')
}
{
  const timeout = createPageRequestTimeout(PAGE_BOOTSTRAP_TIMEOUT_SECONDS)
  timeout.abort()
  equal(timeout.signal.aborted, true, '用户取消必须立即中止仍在 bootstrap 等待期内的请求')
  timeout.clear()
}

// 竞态旧 bootstrap 响应不得提交
const bootstrapGuard = createLatestRequestGuard()
const staleBootstrap = bootstrapGuard.next()
const latestBootstrap = bootstrapGuard.next()
equal(bootstrapGuard.isCurrent(staleBootstrap), false, '竞态旧 bootstrap 响应不得提交')
equal(bootstrapGuard.isCurrent(latestBootstrap), true, '最新 bootstrap 响应必须可提交')

// 原子提交契约：一次调用替换全部数据分段
const options = { warehouseCategories: [{ guid: 'cat-1', name: '玩具' }], suppliers: [] }
const candidates = { items: [{ productCode: 'LP-1', productName: '本地玩具' }], total: 1, pageNumber: 1, pageSize: 20 }
const currentProduct = { productCode: 'LP-1', productName: '本地玩具' }
const summary = {
  totals: { purchaseQuantity: 8, purchaseAmount: 50, netSalesQuantity: -2, netSalesAmount: -12, sellThroughRate: null },
  items: [{ productCode: 'LP-1', suppliers: [], purchaseQuantity: 8, purchaseAmount: 0, netSalesQuantity: 0, netSalesAmount: 0, sellThroughRate: null }],
  total: 1, pageNumber: 1, pageSize: 20,
}
const invoiceDetails = { items: [{ detailGuid: 'D1', invoiceNo: 'INV-1', quantity: 3, purchasePrice: 2.5, amount: 7.5 }], total: 1, pageNumber: 1, pageSize: 20 }
const productDaily = [{ date: '2026-08-18', purchaseQuantity: 3, purchaseAmount: 7.5, netSalesQuantity: -1, netSalesAmount: -4, averageUnitPrice: null }]
const branches = [{ branchCode: 'S1', branchName: '布里斯班店', netSalesQuantity: 0, netSalesAmount: 0, averageUnitPrice: null }]

const previous = createEmptyLocalProductSalesAnalysisState()
const committed = applyLocalProductSalesAnalysisBootstrapResult({
  options, candidates, effectiveSelection: carriedSelection, currentProduct, summary, invoiceDetails, productDaily, branches, partial: true, sectionErrors: { summary: '汇总加载失败' },
}, previous)
equal(committed === previous, false, '原子提交必须返回新状态对象')
equal(committed.options.warehouseCategories[0]?.guid, 'cat-1', '原子提交必须替换 options')
equal(committed.candidates?.items[0]?.productCode, 'LP-1', '原子提交必须替换 candidates')
equal(committed.effectiveSelection.includedProductCodes[0], 'LP-1', '原子提交必须替换 effectiveSelection')
equal(committed.currentProduct?.productCode, 'LP-1', '原子提交必须替换 currentProduct')
equal(committed.summary?.totals.netSalesQuantity, -2, '原子提交必须替换 summary')
equal(committed.invoiceDetails?.items[0]?.invoiceNo, 'INV-1', '原子提交必须替换 invoiceDetails')
equal(committed.productDaily[0]?.date, '2026-08-18', '原子提交必须替换 productDaily')
equal(committed.branches[0]?.branchCode, 'S1', '原子提交必须替换 branches')
equal(committed.partial, true, '原子提交必须保留 partial 标记')
equal(committed.sectionErrors.summary, '汇总加载失败', '原子提交必须保留分段错误')
equal(previous.candidates, null, '原子提交不得改动旧状态对象')

// 切换商品只清空下游分段，保留候选与选项
const switched = clearLocalProductSalesAnalysisDetailSections(committed)
equal(switched.candidates?.items[0]?.productCode, 'LP-1', '切换商品必须保留候选')
equal(switched.options.warehouseCategories[0]?.guid, 'cat-1', '切换商品必须保留选项')
equal(switched.summary, null, '切换商品必须清空旧汇总')
equal(switched.invoiceDetails, null, '切换商品必须清空旧明细')
equal(switched.productDaily.length, 0, '切换商品必须清空旧趋势')
equal(switched.branches.length, 0, '切换商品必须清空旧分店排行')
equal(switched.sectionErrors.summary, undefined, '切换商品必须清空分段错误')

// 分段重试只替换目标分段并清除对应错误
const sectionRetry = applyLocalProductSalesAnalysisSectionResult(switched, 'summary', summary)
equal(sectionRetry.summary?.totals.purchaseAmount, 50, '分段重试必须只替换目标分段')
equal(sectionRetry.sectionErrors.summary, undefined, '分段重试成功必须清除对应错误')
equal(sectionRetry.candidates?.items[0]?.productCode, 'LP-1', '分段重试不得影响其它分段')
const sectionError = setLocalProductSalesAnalysisSectionError(sectionRetry, 'branches', '分店加载失败')
equal(sectionError.sectionErrors.branches, '分店加载失败', '分段失败必须写入对应错误')
const clearedError = clearLocalProductSalesAnalysisSectionError(sectionError, 'branches')
equal(clearedError.sectionErrors.branches, undefined, '清除分段错误必须只清除目标键')

// 改版派生指标与图表模型
equal(safeDivide(26.64, 24)?.toFixed(2), '1.11', '进货均价必须由进货额除以进货量得到')
equal(safeDivide(10, 0), null, '除数为零时派生指标必须为空')
equal(countInclusiveDays('2026-08-20', '2026-09-18'), 30, '日期范围天数必须含首尾两天')
equal(countInclusiveDays('2026-09-18', '2026-08-20'), 0, '倒置日期范围不得产生负天数')
equal(getSellThroughLevel(null), 'none', '无进货不评级')
equal(getSellThroughLevel(316.7), 'restock', '售进比超过 150% 必须提示补货')
equal(getSellThroughLevel(150), 'healthy', '150% 属于健康上界')
equal(getSellThroughLevel(60), 'healthy', '60% 属于健康下界')
equal(getSellThroughLevel(59.9), 'slow', '30%–60% 属于偏慢')
equal(getSellThroughLevel(0), 'stale', '低于 30% 属于滞销')
deepEqual(
  buildBranchPriceTiers([{ averageUnitPrice: 2.99 }, { averageUnitPrice: 3.99 }, { averageUnitPrice: 2.990001 }, { averageUnitPrice: null }]),
  [{ price: 2.99, branchCount: 2 }, { price: 3.99, branchCount: 1 }],
  '分店价位必须按分归档并忽略无均价分店',
)
const trendDays = [
  { date: '2026-09-11', purchaseQuantity: 0, purchaseAmount: 0, netSalesQuantity: 3, netSalesAmount: 8.97, averageUnitPrice: 2.99 },
  { date: '2026-09-12', purchaseQuantity: 0, purchaseAmount: 0, netSalesQuantity: -1, netSalesAmount: -2.99, averageUnitPrice: null },
  { date: '2026-09-13', purchaseQuantity: 24, purchaseAmount: 26.64, netSalesQuantity: 5, netSalesAmount: 19.95, averageUnitPrice: 3.99 },
]
const dailyModel = buildTrendChartModel(trendDays, 'daily')
deepEqual(dailyModel.sales, [3, -1, 5], '按日模式必须保留当日净销量（含退货负数）')
deepEqual(dailyModel.ticks, [0, 10, 20, 30], '少量退货不得把刻度下界拉到 -10')
equal(dailyModel.domainMin, -1, '坐标下界必须覆盖真实最小值')
deepEqual(dailyModel.priceDomain, [2.99, 3.99], '均价面板值域取有销售日的最小与最大均价')
const cumulativeModel = buildTrendChartModel(trendDays, 'cumulative')
deepEqual(cumulativeModel.purchase, [0, 0, 24], '累计模式必须逐日累加进货量')
deepEqual(cumulativeModel.sales, [3, 2, 7], '累计模式必须逐日累加净销量')
equal(buildTrendChartModel([], 'daily').priceDomain, null, '无数据时均价值域为空')

console.log('LocalProductSalesAnalysis.logic.test: ok')
