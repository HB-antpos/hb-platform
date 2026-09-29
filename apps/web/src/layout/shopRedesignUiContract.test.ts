import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { existsSync, readFileSync, statSync } from 'node:fs'
import path from 'node:path'

const root = process.cwd()
const read = (relativePath: string) => readFileSync(path.join(root, relativePath), 'utf8')

const indexHtml = read('index.html')
const layout = read('src/layout/ShopLayout.tsx')
const shopHome = read('src/pages/ShopHome/index.tsx')
const purchaseSalesPage = read('src/pages/ShopPurchaseSalesAnalysis/index.tsx')
const scanBar = read('src/components/ShopScanBar.tsx')
const productCard = read('src/pages/ShopHome/components/ProductCard.tsx')
const bestSellers = read('src/pages/ShopHome/components/BestSellersSection.tsx')
const globalCss = read('src/styles/global.css')
const preorderCss = read('src/pages/ShopPreorder/styles.css')
const en = JSON.parse(read('src/i18n/locales/en.json')) as { shop?: Record<string, unknown> }
const zh = JSON.parse(read('src/i18n/locales/zh.json')) as { shop?: Record<string, unknown> }

const brandAssetRelativePath = 'src/assets/shop-brand-cart.png'
const brandAssetPath = path.join(root, brandAssetRelativePath)
assert.ok(existsSync(brandAssetPath), 'Shop 品牌图标资产必须存在')
const brandAsset = readFileSync(brandAssetPath)
assert.equal(brandAsset.toString('ascii', 1, 4), 'PNG', 'Shop 品牌图标必须使用 PNG 资产')
assert.equal(brandAsset.readUInt32BE(16), 384, 'Shop 品牌图标宽度必须优化为 384px')
assert.equal(brandAsset.readUInt32BE(20), 384, 'Shop 品牌图标高度必须优化为 384px')
assert.ok(statSync(brandAssetPath).size < 350_000, 'Shop 品牌图标必须小于 350KB')
assert.equal(
  createHash('sha256').update(brandAsset).digest('hex'),
  'd125bd9321b6eb107e96e0969a427c110b5b9d696b44bca3dacfe8730adec29c',
  'Shop 品牌图标必须保持已批准的页面色板版本，禁止无声漂色',
)
assert.ok(
  indexHtml.includes('<link rel="icon" type="image/png" href="/pwa/icon-192.png" />'),
  '浏览器标签页必须默认使用原蓝色 HB 图标',
)
assert.ok(!indexHtml.includes('href="/src/assets/shop-brand-cart.png"'), 'Shop 购物车图标不得污染登录页及其他非 Shop 页面')

assert.ok(layout.includes("import shopBrandCart from '../assets/shop-brand-cart.png'"), 'ShopLayout 必须导入品牌图片资产')
const faviconHookStart = layout.indexOf('function useShopFavicon()')
const faviconHookEnd = layout.indexOf('function ShopBrandMark()', faviconHookStart)
assert.ok(faviconHookStart >= 0 && faviconHookEnd > faviconHookStart, 'ShopLayout 必须提供路由作用域内的 favicon 切换逻辑')
const faviconHook = layout.slice(faviconHookStart, faviconHookEnd)
assert.ok(faviconHook.includes("document.querySelector<HTMLLinkElement>('link[rel=\"icon\"]')"), 'Shop favicon 必须复用现有标签图标节点')
assert.ok(faviconHook.includes("favicon.setAttribute('href', shopBrandCart)"), '进入 /shop 路由树时必须切换为购物车图标')
assert.ok(faviconHook.includes("favicon.setAttribute('sizes', '384x384')"), 'Shop favicon 必须声明正确的资源尺寸')
assert.ok(faviconHook.includes("favicon.setAttribute('href', previousHref)"), '离开 /shop 路由树时必须恢复原 HB 图标')
assert.ok(faviconHook.includes("favicon.removeAttribute('sizes')"), '离开 /shop 后必须恢复原 favicon 的尺寸属性状态')
assert.ok(layout.includes('useShopFavicon()'), '所有 ShopLayout 路径必须启用统一的 Shop favicon')
const brandComponentStart = layout.indexOf('function ShopBrandMark()')
const brandComponentEnd = layout.indexOf('export default function ShopLayout()', brandComponentStart)
assert.ok(brandComponentStart >= 0 && brandComponentEnd > brandComponentStart, 'ShopLayout 必须提供内部 ShopBrandMark 组件')
const brandComponent = layout.slice(brandComponentStart, brandComponentEnd)
assert.ok(brandComponent.includes('src={shopBrandCart}'), 'ShopBrandMark 必须渲染指定图片资产')
assert.ok(brandComponent.includes('className="shop-brand-mark__image"'), 'ShopBrandMark 必须提供统一图片样式钩子')
assert.ok(!brandComponent.includes('ShoppingCartOutlined'), '品牌图标不得再叠加 Ant Design 购物车')
assert.equal(layout.match(/<ShopBrandMark \/>/g)?.length, 2, '桌面与移动顶栏必须复用同一 ShopBrandMark')
assert.ok(!layout.includes('shop-brand-mark__letters'), '品牌区域不得继续使用 CSS HB 文字')
assert.ok(!layout.includes('shop-brand-mark__cart'), '品牌区域不得继续使用旧购物车叠加层')
assert.ok(!globalCss.includes('.shop-brand-mark::before'), '品牌图标不得继续使用白色胶囊槽伪元素')
assert.ok(!globalCss.includes('.shop-brand-mark__letters'), '品牌图标不得保留旧 HB 文字样式')
assert.ok(!globalCss.includes('.shop-brand-mark__cart'), '品牌图标不得保留旧购物车样式')
assert.match(globalCss, /\.shop-brand-mark__image\s*\{[\s\S]*?object-fit:\s*cover/, '品牌图片必须完整填充圆角图标容器')

const desktopNavStart = layout.indexOf('<nav className="shop-primary-nav"')
const desktopNavEnd = layout.indexOf('</nav>', desktopNavStart)
assert.ok(desktopNavStart >= 0 && desktopNavEnd > desktopNavStart, '桌面 Shop 必须使用语义化一级导航')

const desktopNav = layout.slice(desktopNavStart, desktopNavEnd)
const desktopDestinations = [
  "to=\"/shop\"",
  'onClick={handleOpenPreorder}',
  "to=\"/shop/best-sellers\"",
  "to=\"/shop/coming-soon\"",
  "to=\"/shop/orders\"",
  "to=\"/shop/local-supplier-invoices\"",
  "to=\"/shop/purchase-sales-analysis\"",
]
let previousDestinationIndex = -1
for (const destination of desktopDestinations) {
  const destinationIndex = desktopNav.indexOf(destination)
  assert.ok(destinationIndex > previousDestinationIndex, `桌面导航缺少或顺序错误: ${destination}`)
  previousDestinationIndex = destinationIndex
}
assert.match(desktopNav, /aria-current=\{isShopHomePage \? 'page' : undefined\}/, 'Shop Home 必须暴露当前页语义')
assert.match(desktopNav, /aria-current=\{isOrdersPage \? 'page' : undefined\}/, 'Orders 必须暴露当前页语义')
// 货号销量并入「进货销量分析」的「粘贴数据查看」标签：布局不再有独立入口，标签由页面按前台权限显示。
assert.ok(!layout.includes('/shop/batch-product-sales'), '货号销量已并入进货销量分析，商城布局不得保留独立入口')
assert.ok(
  purchaseSalesPage.includes('state.access.canViewShopBatchProductSales'),
  '粘贴数据查看标签必须按前台货号销量权限显示',
)

assert.ok(layout.includes('className="shop-ordering-toolbar"'), '桌面必须有独立白色交易工具栏')
assert.ok(layout.includes('className="shop-ordering-search"'), '交易工具栏必须保留商品搜索')
const fetchProductsStart = shopHome.indexOf('const fetchProducts = async () => {')
const fetchProductsEnd = shopHome.indexOf('void fetchProducts()', fetchProductsStart)
assert.ok(fetchProductsStart >= 0 && fetchProductsEnd > fetchProductsStart, 'ShopHome 必须保留商品列表请求流程')
const fetchProductsSource = shopHome.slice(fetchProductsStart, fetchProductsEnd)
assert.ok(
  fetchProductsSource.includes('itemNumber: keyword || undefined,'),
  '商城关键词必须搜索货号与条码',
)
assert.ok(
  fetchProductsSource.includes('productName: keyword || undefined,'),
  '商城关键词必须同时按商品名称进行不区分大小写的包含搜索',
)
const fetchProductsDeps = shopHome.slice(fetchProductsEnd, shopHome.indexOf('])', fetchProductsEnd))
assert.ok(
  shopHome.includes('const { key: navigationKey } = useLocation()') && fetchProductsDeps.includes('navigationKey'),
  '再次搜索同一关键字时 URL 不变，商品列表必须随每次导航重新请求，不能只靠 URL 变化触发',
)
assert.ok(layout.includes('className="shop-ordering-scan"'), '交易工具栏必须提供扫码入口')
assert.ok(layout.includes('className="shop-ordering-store"'), '交易工具栏必须保留门店选择')
assert.ok(layout.includes('className="shop-ordering-cart"'), '交易工具栏必须保留购物车摘要')
assert.ok(layout.includes("t('shop.reviewOrder', 'Review Order')"), '购物车主操作必须使用 Review Order 而不是支付语义')
assert.ok(!layout.includes("t('shop.checkout'"), 'Shop 新布局不应继续展示 Checkout')
assert.equal(en.shop?.reviewOrder, 'Review Order', '英文 Review Order 文案必须存在')
assert.equal(zh.shop?.reviewOrder, '查看订单', '中文 Review Order 文案必须存在')

const mobileNavStart = layout.indexOf('<nav className="shop-mobile-bottom-nav"')
const mobileNavEnd = layout.indexOf('</nav>', mobileNavStart)
assert.ok(mobileNavStart >= 0 && mobileNavEnd > mobileNavStart, '移动端必须有固定五项底部导航')
const mobileNav = layout.slice(mobileNavStart, mobileNavEnd)
for (const className of [
  'shop-mobile-bottom-nav__shop',
  'shop-mobile-bottom-nav__categories',
  'shop-mobile-bottom-nav__scan',
  'shop-mobile-bottom-nav__orders',
  'shop-mobile-bottom-nav__more',
]) {
  assert.ok(mobileNav.includes(className), `移动底栏缺少 ${className}`)
}
assert.ok(layout.includes('open={mobileMoreVisible}'), 'More 必须使用受控抽屉')
assert.ok(layout.includes('className="shop-mobile-more-menu"'), 'More 抽屉必须有统一菜单容器')
const mobileStoreStart = layout.indexOf('className="shop-mobile-store-select"')
const mobileStoreEnd = layout.indexOf('/>', mobileStoreStart)
assert.ok(
  mobileStoreStart >= 0 && layout.slice(mobileStoreStart, mobileStoreEnd).includes('allowClear'),
  '移动门店选择器必须保留清空门店能力',
)
assert.ok(layout.includes('data-shop-scan-trigger'), '布局扫码入口必须可触发首页扫码面板')
assert.ok(scanBar.includes("window.addEventListener('shop:open-scanner'"), '扫码面板必须响应全局 Shop 扫码入口')
assert.ok(scanBar.includes("window.removeEventListener('shop:open-scanner'"), '扫码事件必须在卸载时清理')
assert.ok(scanBar.includes('scrollIntoView'), '从固定导航打开扫码时必须把扫码面板带入视口')
assert.ok(scanBar.includes("matchMedia('(prefers-reduced-motion: reduce)')"), '扫码面板滚动必须尊重减少动态效果设置')

for (const className of [
  'shop-product-card-grade',
  'shop-product-card-layout',
  'shop-product-card-media',
  'shop-product-card-content',
  'shop-product-card-purchase',
]) {
  assert.ok(productCard.includes(className), `商品卡缺少新版结构 ${className}`)
}

assert.ok(bestSellers.includes('className="shop-best-sellers-mobile-list"'), '热销页移动端必须使用可读卡片列表')
assert.ok(bestSellers.includes('className="shop-best-sellers-mobile-pagination"'), '热销页移动端必须保留分页')
assert.ok(
  globalCss.includes('grid-template-columns: 26px 28px minmax(36px, 1fr) 28px repeat(3, 28px) minmax(54px, auto);'),
  '桌面四列商品卡操作区必须使用可容纳于 308px 内容宽度的紧凑列',
)
assert.ok(globalCss.includes('--shop-orange-action: #d63a12;'), '小号白字操作必须使用满足 AA 的深橙色')
assert.match(
  globalCss,
  /\.shop-layout \.ant-btn-primary,[\s\S]*?background:\s*var\(--shop-orange-action\)/,
  'Shop primary 按钮必须使用可访问操作橙',
)
assert.ok(globalCss.includes('color: var(--shop-orange-ink);'), '白底橙色小号文字必须使用高对比色')
assert.ok(
  globalCss.includes('.shop-mobile-more-drawer .soe-entry--mobile-nav .soe-entry-trigger.ant-btn'),
  'More 抽屉必须覆盖旧移动导航订货助手的白字样式',
)
assert.match(
  globalCss,
  /\.shop-mobile-more-drawer,[\s\S]*?\.shop-mobile-category-drawer[\s\S]*?--shop-orange-action:\s*#d63a12/,
  'Portal 抽屉必须重新声明 Shop 主题变量，确保焦点态与活动态颜色有效',
)
assert.ok(
  globalCss.includes('.shop-mobile-category-drawer .ant-btn-primary'),
  'Portal 中的移动分类主按钮也必须使用 Shop 操作橙',
)
assert.ok(
  globalCss.includes('.shop-cart-drawer-footer .ant-btn-primary'),
  'Portal 中的购物车主按钮也必须使用 Shop 操作橙',
)
assert.match(
  globalCss,
  /@media \(max-width: 359px\)[\s\S]*?\.shop-layout \.shop-product-card-actions[\s\S]*?grid-template-columns:\s*repeat\(4, minmax\(0, 1fr\)\)/,
  '320px 窄屏商品操作区必须拆为四列两行',
)
assert.ok(
  globalCss.includes('width: min(560px, calc(100vw - 32px));'),
  '门店销量弹层宽度必须受手机视口约束',
)
assert.match(globalCss, /\.shop-scan-status-dot\s*\{[\s\S]*?background:\s*#94a3b8/, '暂停扫码状态点必须为灰色')
assert.match(globalCss, /\.shop-scan-status-dot\.ready\s*\{[\s\S]*?background:\s*var\(--shop-success\)/, '就绪扫码状态点必须为绿色')
assert.ok(layout.includes("' shop-nav-bar--secondary'"), '非首页导航占位必须有独立收起样式钩子')
assert.match(globalCss, /\.shop-nav-bar\.shop-nav-bar--secondary\s*\{[\s\S]*?min-height:\s*0/, '非首页不得保留空导航高度')
assert.match(
  preorderCss,
  /@media \(max-width: 768px\)[\s\S]*?\.shop-preorder-summary[\s\S]*?bottom:\s*calc\(66px \+ env\(safe-area-inset-bottom\)\)/,
  'Preorder 汇总栏只应在移动底栏出现时上移',
)

for (const token of [
  '--shop-navy:',
  '--shop-orange:',
  '--shop-page:',
  '--shop-border:',
  '--shop-radius-control:',
  '--shop-radius-panel:',
]) {
  assert.ok(globalCss.includes(token), `Shop 视觉令牌缺少 ${token}`)
}
assert.match(globalCss, /\.shop-mobile-bottom-nav[\s\S]*position:\s*fixed/, '移动底栏必须固定在视口底部')
assert.ok(globalCss.includes('env(safe-area-inset-bottom)'), '移动端必须预留底部安全区')
assert.match(globalCss, /@media \(prefers-reduced-motion: reduce\)[\s\S]*\.shop-layout/, 'Shop 新增交互必须尊重 reduced motion')

console.log('shopRedesignUiContract.test: ok')
