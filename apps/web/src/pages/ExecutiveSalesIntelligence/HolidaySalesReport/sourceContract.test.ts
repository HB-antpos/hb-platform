import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 页面接线契约：不能让测试 import index.tsx / CSS（CI 把 .css 打成空模块），所以读源码静态核对关键约定。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/ExecutiveSalesIntelligence/HolidaySalesReport'
const read = (path: string) => readFileSync(resolve(path), 'utf8')
const page = read(`${directory}/index.tsx`)
const sections = read(`${directory}/Sections.tsx`)
const logic = read(`${directory}/logic.ts`)
const css = read(`${directory}/styles.module.css`)
const service = read('src/services/holidaySalesReportService.ts')
const routes = read('src/router/routes.tsx')
const access = read('src/utils/access.ts')
const portal = read('src/utils/webPortalAccess.ts')
const preview = read('src/utils/webMenuPreview.ts')
const permissions = read('src/types/permissions.ts')

// 数据与权限：页面经服务层取数，分店范围由服务端解析（页面不传 branchCodes 子集）
assert(service.includes("'/api/react/v1/dashboard/holiday-sales-report'"), '服务必须请求约定的接口路径')
assert(!page.includes('/api/react/v1/dashboard/holiday-sales-report'), '页面必须经 holidaySalesReportService 调用接口')
assert(page.includes('getHolidaySalesReport(') && page.includes('getHolidayCategoryMap('), '页面必须使用服务层读取数据')
assert(!/branchCodes/.test(page), '授权分店范围由服务端按用户解析，页面不能自己传 branchCodes')
assert(page.includes('useKeepAliveContext') && page.includes('active,'), '保活页面必须按 active 控制查询')
assert(page.includes('useReportQuery'), '加载沿用看板统一的 useReportQuery 模式')
assert(/forceRefreshRef\.current = false/.test(page), '强制刷新标记必须在本次刷新后复位，否则之后每次切换筛选都绕过服务端缓存')

// 日期必须按悉尼时区取，不能用浏览器本地时区
assert(page.includes('formatSydneyIsoDate'), '默认年份必须取悉尼时区的今天')
for (const [name, source] of [['index.tsx', page], ['Sections.tsx', sections], ['logic.ts', logic]] as const) {
  assert(!/new Date\(\s*\)/.test(source) && !/dayjs\(/.test(source) && !/Date\.now\(/.test(source), `${name} 不应用浏览器本地时间推导业务日期`)
}

// 数据显示不能把空值（没有进货的售罄率）当 0
assert(!/\?\?\s*0\b/.test(logic) && !/\|\|\s*0\b/.test(logic), 'logic.ts 不应把空值回退成 0')
assert(sections.includes('formatSellThrough('), '售罄率必须经 formatSellThrough 显示（null 显示「—」，超过 100% 显示「100%+」）')

// 无障碍：图标按钮要有名称，图表有文字说明，分店行可键盘操作
assert(page.includes('aria-label={tr(') && sections.includes('role="img"'), '图标按钮与图表必须有可访问名称')
assert(sections.includes('tabIndex={0}') && sections.includes("event.key === 'Enter'"), '可点击的分店行必须可用键盘打开')
assert(sections.includes('aria-live="polite"'), '图表悬停读数要能被读屏读到')
assert(!page.includes(['window', 'location', 'reload'].join('.')), '页面不得自动 hard reload')

// 商品图片失败时有占位，不显示破图
assert(sections.includes('onError={() => setFailed(true)}') && sections.includes('thumbEmpty'), '商品图片加载失败必须有占位')

// 样式：宽表格在自己的容器里滚动；窄屏与减少动画；键盘焦点可见
assert(css.includes('.tableScroll { overflow-x: auto; }'), '宽表格必须在自己的容器里滚动')
assert(css.includes('@media (max-width: 760px)') && css.includes('prefers-reduced-motion'), '必须有窄屏与减少动画处理')
assert(css.includes('.page button:focus-visible') && css.includes('outline: 2px solid'), '键盘焦点必须可见')

// 路由与权限登记
const routeStart = routes.indexOf("path: '/executive-sales-intelligence/holiday-sales-report'")
assert(routeStart >= 0, '必须登记路由')
const routeBlock = routes.slice(routeStart, routes.indexOf('element:', routeStart))
assert(routeBlock.includes("title: 'menu.holidaySalesReport'"), '路由标题使用全局菜单键')
assert(routeBlock.includes('keepAlive: true'), '路由必须 keepAlive')
assert(routeBlock.includes("accessKey: 'canViewHolidaySalesReport'"), '路由使用本页访问键')
assert(routes.includes("lazy(() => import('../pages/ExecutiveSalesIntelligence/HolidaySalesReport'))"), '页面必须懒加载')
assert(permissions.includes("HolidaySalesReportView: 'SalesDashboard.HolidaySalesReport.View'"), '权限码常量')
assert(access.includes('hasPermission(P.SalesDashboard.HolidaySalesReportView)'), 'access 必须读取本页权限')
const parentStart = access.indexOf('const canViewSalesIntelligence =')
assert(parentStart >= 0 && access.slice(parentStart, access.indexOf('// ---', parentStart)).includes('canViewHolidaySalesReport'), '销售看板父菜单必须被本页权限点亮')
assert(portal.includes("'holiday-sales-report'") && portal.includes('HolidaySalesReportView'), '门户落地规则必须包含本页')
assert(preview.includes('canViewHolidaySalesReport') && preview.includes('/executive-sales-intelligence/holiday-sales-report'), '菜单预览必须包含本页')

console.log('holidaySalesReport source contract: ok')
