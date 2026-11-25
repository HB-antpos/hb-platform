import ReactDOM from 'react-dom/client'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import enUS from 'antd/locale/en_US'
import 'antd/dist/reset.css'
import '../../src/styles/global.css'
import i18n from '../../src/i18n'
import ShopLayout from '../../src/layout/ShopLayout'
import { ShopPreorderLeaveProvider } from '../../src/pages/ShopPreorder/preorderLeaveContext'
import ShopPurchaseSalesAnalysisPage from '../../src/pages/ShopPurchaseSalesAnalysis'
import { useAuthStore } from '../../src/store/auth'
import { buildAccess } from '../../src/utils/access'
import type { BatchSalesMetrics, BatchSalesProduct, BatchSalesScope } from '../../src/types/batchProductSalesAnalysis'

// 订货前台本地验收入口：真实 ShopLayout + 正式「进货销量分析 · 粘贴数据查看」标签，仅在本入口内模拟货号销量接口；未登记生产路由。
const stores = [
  { code: 'B1', name: 'Springfield' }, { code: 'B2', name: 'Sunnybank' },
  { code: 'B3', name: 'Browns Plains' }, { code: 'B4', name: 'Logan Central' },
]
const definitions: Array<[string, string, number, number]> = [
  ['001236', '透明收纳盒 1.5L', 268, 72], ['HB24018', '厨房清洁海绵 6片装', 186, 44],
  ['006821', '不锈钢保温杯 500ml', 142, 24], ['HB23056', '防滑衣架 10只装', 98, 26],
  ['000875', '抽屉分隔收纳盒', 64, 14], ['HB25012', '微纤维清洁布 4片装', 52, 14],
  ['009102', '食品密封夹 12只装', 31, 7], ['HB22034', '陶瓷马克杯', 12, 0],
]
const products: BatchSalesProduct[] = definitions.map(([itemNumber, productName], index) => ({
  productCode: `P${index + 1}`, itemNumber, productName, englishName: `Preview product ${index + 1}`,
}))
const allocate = (total: number, weights: number[]) => {
  const sum = weights.reduce((a, b) => a + b, 0)
  let cumulative = 0
  return weights.map(weight => {
    const before = Math.round(total * cumulative / sum)
    cumulative += weight
    return Math.round(total * cumulative / sum) - before
  })
}
const zero = (): BatchSalesMetrics => ({
  quantity: 0, regularQuantity: 0, discountQuantity: 0, unknownQuantity: 0, returnQuantity: 0,
  salesAmount: 0, discountStatus: 'complete', originalPriceMin: null, originalPriceMax: null,
  discountPriceMin: null, discountPriceMax: null,
})
const add = (rows: BatchSalesMetrics[]) => rows.reduce((sum, row) => ({
  ...sum, quantity: sum.quantity + row.quantity, regularQuantity: sum.regularQuantity + row.regularQuantity,
  discountQuantity: sum.discountQuantity + row.discountQuantity, unknownQuantity: sum.unknownQuantity + row.unknownQuantity,
  returnQuantity: sum.returnQuantity + row.returnQuantity, salesAmount: sum.salesAmount + row.salesAmount,
  originalPriceMin: row.originalPriceMin ?? sum.originalPriceMin, originalPriceMax: row.originalPriceMax ?? sum.originalPriceMax,
  discountPriceMin: row.discountPriceMin ?? sum.discountPriceMin, discountPriceMax: row.discountPriceMax ?? sum.discountPriceMax,
}), zero())
// 演示事实覆盖 2026-08-01 至 2026-09-18，匹配页面默认的最近 30 天窗口；两段折扣促销期用于展示分类颜色。
const DEMO_DATES = (() => { const out: string[] = []; for (let t = Date.parse('2026-08-01T00:00:00Z'); t <= Date.parse('2026-09-18T00:00:00Z'); t += 86_400_000) out.push(new Date(t).toISOString().slice(0, 10)); return out })()
const regularWeights = DEMO_DATES.map((date, index) => 8 + ((index * 7) % 11) + (new Date(date).getUTCDay() >= 5 ? 6 : 0))
const discountWeights = DEMO_DATES.map(date => (date >= '2026-08-15' && date <= '2026-08-18') || (date >= '2026-09-05' && date <= '2026-09-08') ? 10 : 0)
const facts = products.flatMap((product, productIndex) => {
  const quantity = definitions[productIndex][2], discount = definitions[productIndex][3]
  const regularByBranch = allocate(quantity - discount, productIndex === 0 ? [60, 54, 39, 43] : [4, 3, 2, 1])
  const discountByBranch = allocate(discount, productIndex === 0 ? [26, 18, 22, 6] : [4, 3, 2, 1])
  return stores.flatMap((store, index) => {
    const regular = allocate(regularByBranch[index] * 3, regularWeights)
    const discounted = allocate(discountByBranch[index] * 3, discountWeights)
    return regular.map((value, day) => ({
      productCode: product.productCode, branchCode: store.code, date: DEMO_DATES[day],
      metrics: { ...zero(), quantity: value + discounted[day], regularQuantity: value,
        discountQuantity: discounted[day], salesAmount: value * 6 + discounted[day] * 4.5,
        originalPriceMin: value + discounted[day] > 0 ? 6 : null, originalPriceMax: value + discounted[day] > 0 ? 6 : null,
        discountPriceMin: discounted[day] > 0 ? 4.5 : null, discountPriceMax: discounted[day] > 0 ? 4.5 : null },
    }))
  })
})
const effectiveScope = (input: BatchSalesScope): BatchSalesScope => ({
  startDate: input.startDate, endDate: input.endDate,
  storeCodes: input.storeCodes?.length ? input.storeCodes : stores.map(store => store.code),
})
const dates = (scope: BatchSalesScope) => {
  const result: string[] = []
  for (let current = Date.parse(`${scope.startDate}T00:00:00Z`); current <= Date.parse(`${scope.endDate}T00:00:00Z`); current += 86_400_000)
    result.push(new Date(current).toISOString().slice(0, 10))
  return result
}
const inScope = (scope: BatchSalesScope, codes: string[]) => facts.filter(row =>
  codes.includes(row.productCode) && row.date >= scope.startDate && row.date <= scope.endDate && scope.storeCodes.includes(row.branchCode))
const dayRows = (scope: BatchSalesScope, rows: typeof facts) => dates(scope).map(date => ({ date, metrics: add(rows.filter(row => row.date === date).map(row => row.metrics)) }))
const branchRows = (scope: BatchSalesScope, rows: typeof facts, withCount: boolean) => stores.filter(store => scope.storeCodes.includes(store.code)).map(store => {
  const branchFacts = rows.filter(row => row.branchCode === store.code)
  return { branchCode: store.code, branchName: store.name, metrics: add(branchFacts.map(row => row.metrics)), daily: dayRows(scope, branchFacts),
    ...(withCount ? { contributingProductCount: new Set(branchFacts.filter(row => row.metrics.quantity !== 0).map(row => row.productCode)).size, selectedProductCount: new Set(rows.map(row => row.productCode)).size } : {}) }
})
const coverage = (scope: BatchSalesScope) => ({ status: 'complete', readyDates: dates(scope), pendingDates: [], version: 'preview-v1' })
const productRows = (rows: typeof facts, codes: string[]) => products.filter(product => codes.includes(product.productCode)).map(product => ({ ...product, metrics: add(rows.filter(row => row.productCode === product.productCode).map(row => row.metrics)) }))

// 进货演示数据：06-20 首批铺货（销售 09-01 起时落在默认 60 天窗口外）、08-15 仓库发货、08-28 仓库单（第二家店未出库）、09-10 供应商进货。
const purchaseFacts = products.slice(0, 6).flatMap((product, productIndex) => stores.flatMap((store, storeIndex) => [
  { source: 'Warehouse', documentNo: `2026-12${productIndex}${storeIndex}`, purchaseDate: '2026-06-20', isShipped: true, productCode: product.productCode, branchCode: store.code, quantity: 144 },
  { source: 'Warehouse', documentNo: `2026-15${productIndex}${storeIndex}`, purchaseDate: '2026-08-15', isShipped: true, productCode: product.productCode, branchCode: store.code, quantity: 72 },
  { source: 'Warehouse', documentNo: `2026-18${productIndex}${storeIndex}`, purchaseDate: '2026-08-28', isShipped: storeIndex !== 1, productCode: product.productCode, branchCode: store.code, quantity: 24 + storeIndex * 12 },
  ...(storeIndex % 2 === 0 ? [{ source: 'Supplier', documentNo: `INV-${3180 + productIndex * 10 + storeIndex}`, purchaseDate: '2026-09-10', isShipped: true, productCode: product.productCode, branchCode: store.code, quantity: 6 }] : []),
]))
const originalFetch = window.fetch.bind(window)
window.fetch = async (input, init) => {
  const url = String(input)
  if (!url.includes('/api/')) return originalFetch(input, init)
  const json = (data: unknown) => new Response(JSON.stringify({ success: true, data }), { headers: { 'content-type': 'application/json' } })
  // ShopLayout 外壳依赖：当前用户门店、分类树、购物车摘要、预订门禁。
  if (/\/api\/Users\/guid\/[^/]+\/stores/.test(url)) return json([{ storeGUID: 'store-b1', storeName: 'Springfield', storeCode: 'B1', isActive: true, isManageable: false, assignedAt: '2026-01-01T00:00:00Z' }])
  if (url.includes('/warehouse-categories/tree')) return json([])
  if (url.includes('/store-order/cart/')) return json({ items: [], totalQuantity: 0, totalAmount: 0 })
  if (url.includes('/preorders/active')) return json({ normalOrderBlocked: false, activations: [] })
  if (!url.includes('/batch-product-sales-analysis/')) return json({})
  const endpoint = url.split('/batch-product-sales-analysis/')[1], body = init?.body ? JSON.parse(String(init.body)) : {}
  await new Promise(resolve => setTimeout(resolve, 120))
  if (endpoint === 'options') return json({ stores, maxItemNumbers: 500, maxDays: 366 })
  const scope = effectiveScope(body)
  if (endpoint === 'query') {
    const matches = (body.itemNumbers as string[]).map(value => {
      const found = products.filter(product => product.itemNumber.toLowerCase() === value.toLowerCase())
      return { itemNumber: value, status: found.length ? 'matched' : 'notFound', productCodes: found.map(product => product.productCode) }
    })
    const codes = [...new Set(matches.flatMap(row => row.productCodes))]
    const rows = inScope(scope, codes)
    return json({ ...scope, matches, products: products.filter(product => codes.includes(product.productCode)).map(product => {
      const metrics = add(rows.filter(row => row.productCode === product.productCode).map(row => row.metrics))
      return { ...product, quantity: metrics.quantity, salesAmount: metrics.salesAmount }
    }), warnings: [], statisticStatus: 'Fresh', discountStatisticStatus: 'Fresh', coverage: coverage(scope),
      overview: { metrics: add(rows.map(row => row.metrics)), daily: dayRows(scope, rows), branches: branchRows(scope, rows, true) } })
  }
  if (endpoint === 'detail') {
    const product = products.find(product => product.productCode === body.productCode)!
    const rows = inScope(scope, [product.productCode])
    return json({ ...scope, productCodes: [product.productCode], product, metrics: add(rows.map(row => row.metrics)), daily: dayRows(scope, rows),
      branches: branchRows(scope, rows, false), warnings: [], statisticStatus: 'Fresh', discountStatisticStatus: 'Fresh', coverage: coverage(scope) })
  }
  const codes: string[] = body.productCodes ?? products.map(product => product.productCode)
  if (endpoint === 'purchases' || endpoint === 'purchase-details') {
    const matched = purchaseFacts.filter(row => codes.includes(row.productCode) && scope.storeCodes.includes(row.branchCode)
      && (!body.branchCode || row.branchCode === body.branchCode) && row.purchaseDate >= scope.startDate && row.purchaseDate <= scope.endDate)
    if (endpoint === 'purchase-details') {
      return json({ ...scope, productCodes: codes, branchCode: body.branchCode, limit: 500, truncated: false,
        rows: [...matched].sort((left, right) => right.purchaseDate.localeCompare(left.purchaseDate) || left.source.localeCompare(right.source) || left.documentNo.localeCompare(right.documentNo)) })
    }
    const totals = new Map<string, { productCode: string; branchCode: string; warehouseQuantity: number; supplierQuantity: number }>()
    matched.forEach(row => {
      const key = `${row.productCode}|${row.branchCode}`
      const total = totals.get(key) ?? { productCode: row.productCode, branchCode: row.branchCode, warehouseQuantity: 0, supplierQuantity: 0 }
      if (row.source === 'Warehouse') total.warehouseQuantity += row.quantity
      else total.supplierQuantity += row.quantity
      totals.set(key, total)
    })
    return json({ ...scope, productCodes: codes, rows: [...totals.values()] })
  }
  const rows = inScope(scope, codes)
  if (endpoint === 'overview/branch') {
    const branchFacts = rows.filter(row => row.branchCode === body.branchCode)
    return json({ ...scope, productCodes: codes, coverage: coverage(scope), branch: branchRows(scope, branchFacts, false).find(branch => branch.branchCode === body.branchCode), products: productRows(branchFacts, codes) })
  }
  if (endpoint === 'overview/discounts') {
    const branchFacts = body.branchCode ? rows.filter(row => row.branchCode === body.branchCode) : rows
    return json({ ...scope, productCodes: codes, coverage: coverage(scope), overview: { metrics: add(rows.map(row => row.metrics)), daily: dayRows(scope, rows), branches: branchRows(scope, rows, true) },
      branch: body.branchCode ? branchRows(scope, branchFacts, false)[0] : null, products: productRows(branchFacts, codes), discountStatisticStatus: 'Fresh', warnings: [] })
  }
  return json({})
}

const language = new URLSearchParams(location.search).get('lang') === 'en' ? 'en' : 'zh'
await i18n.changeLanguage(language)
// 订货员角色：仅一家门店、只有前台权限，凭前台货号销量权限看到「粘贴数据查看」标签并查看全部分店数据。
const user = { userGUID: 'shop-batch-sales-preview', username: 'springfield.staff', email: '',
  permissions: ['OrderFront', 'OrderFront.BatchProductSales.View'], exactPermissions: ['OrderFront', 'OrderFront.BatchProductSales.View'],
  roleNames: ['订货员'], storeNames: ['Springfield'] }
useAuthStore.setState({ currentUser: user, access: buildAccess(user), initialized: true })


ReactDOM.createRoot(document.getElementById('root')!).render(
  <ConfigProvider locale={language === 'en' ? enUS : zhCN} theme={{ token: { colorPrimary: '#1677ff', borderRadius: 6,
    fontFamily: '-apple-system,BlinkMacSystemFont,Segoe UI,PingFang SC,sans-serif' } }}>
    <div style={{ padding: '4px 18px', background: '#fff8e7', color: '#775b22', fontSize: 12 }}>本地交互验收 · 演示数据 · 订货前台 ShopLayout + 「进货销量分析 · 粘贴数据查看」</div>
    <MemoryRouter initialEntries={['/shop/purchase-sales-analysis?tab=paste']}>
      <Routes>
        <Route path="/shop" element={<ShopPreorderLeaveProvider><ShopLayout /></ShopPreorderLeaveProvider>}>
          <Route path="purchase-sales-analysis" element={<ShopPurchaseSalesAnalysisPage />} />
        </Route>
      </Routes>
    </MemoryRouter>
  </ConfigProvider>,
)

// ?auto=1：自动导入演示货号、查询并选中首个分店，供无头浏览器整页截图使用；每步轮询等待，适应冷启动。
if (new URLSearchParams(location.search).get('auto') === '1') {
  const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms))
  // 中英文按钮文案都识别，便于两种语言各截一份验收图。
  const button = (text: string, root: ParentNode = document) => [...root.querySelectorAll('button')].find(item => text.split('|').some(label => item.textContent?.includes(label)))
  const waitFor = async <T,>(probe: () => T | null | undefined, timeoutMs = 20_000): Promise<T> => {
    for (let elapsed = 0; elapsed < timeoutMs; elapsed += 200) {
      const value = probe()
      if (value) return value
      await sleep(200)
    }
    throw new Error('preview-auto: 等待超时')
  }
  void (async () => {
    ;(await waitFor(() => button('选择商品|Select products'))).click()
    const textarea = await waitFor(() => document.querySelector<HTMLTextAreaElement>('.ant-modal textarea'))
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, definitions.map(item => item[0]).concat(['001236', 'ZZ-404']).join('\n'))
    textarea.dispatchEvent(new Event('input', { bubbles: true }))
    const confirm = await waitFor(() => { const item = button('确认|Confirm', document.querySelector('.ant-modal') ?? document); return item && !item.hasAttribute('disabled') ? item : null })
    confirm.click()
    await waitFor(() => (document.querySelector('.ant-modal') ? null : true))
    ;(await waitFor(() => button('查询销量|Query sales'))).click()
    ;(await waitFor(() => document.querySelector<HTMLButtonElement>('[class*="branchButton"]'))).click()
    await waitFor(() => (document.querySelector('#batch-product-sales-branch-trend svg') ? true : null))
    await sleep(800)
    document.body.setAttribute('data-preview-ready', '1')
    console.info('[preview-auto] ready')
  })().catch(error => console.error('[preview-auto] failed', error))
}
