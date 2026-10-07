import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 页面接线契约：不能让测试 import index.tsx / CSS（CI 把 .css 打成空模块），所以读源码静态核对关键约定。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/ExecutiveSalesIntelligence/MonthlyDailySalesDownload'
const read = (path: string) => readFileSync(resolve(path), 'utf8')
const page = read(`${directory}/index.tsx`)
const logic = read(`${directory}/logic.ts`)
const exporter = read(`${directory}/export.ts`)
const css = read(`${directory}/styles.module.css`)
const service = read('src/services/monthlyDailySalesService.ts')
const routes = read('src/router/routes.tsx')
const access = read('src/utils/access.ts')
const portal = read('src/utils/webPortalAccess.ts')
const preview = read('src/utils/webMenuPreview.ts')
const permissions = read('src/types/permissions.ts')

// 数据与权限
assert(service.includes("'/api/react/v1/dashboard/monthly-store-daily-sales'"), '服务必须请求约定的接口路径')
assert(!page.includes('/api/react/v1/dashboard/monthly-store-daily-sales'), '页面必须经 monthlyDailySalesService 调用接口')
assert(page.includes('getMonthlyStoreDailySales('), '页面必须使用服务层读取数据')
assert(page.includes('access.visibleStoreCodes()'), '授权分店范围取 visibleStoreCodes（管理员 / 全局范围不传）')
assert(page.includes('useKeepAliveContext') && /active[,\s}]/.test(page) && page.includes('{ active, enabled: hasStoreScope'), '保活页面必须按 active 控制查询')
assert(page.includes('useReportQuery'), '加载沿用看板统一的 useReportQuery 模式')

// 日期必须按悉尼时区取，不能用浏览器本地时区
assert(page.includes('formatSydneyIsoDate'), '默认月份必须取悉尼时区的今天')
for (const [name, source] of [['index.tsx', page], ['logic.ts', logic], ['export.ts', exporter]] as const) {
  assert(!/new Date\(\s*\)/.test(source) && !/dayjs\(/.test(source) && !/Date\.now\(/.test(source), `${name} 不应用浏览器本地时间推导业务日期`)
}

// 数据显示不能把空值当 0
assert(!/\?\?\s*0\b/.test(logic) && !/\|\|\s*0\b/.test(logic), 'logic.ts 不应把空值回退成 0')
assert(!/\?\?\s*0\b/.test(exporter) && !/\|\|\s*0\b/.test(exporter), 'export.ts 不应把空值回退成 0')

// 表格：用 div + grid，不直接引入 antd Table
assert(!/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(page), '页面不能直接从 antd 引入 Table')
assert(page.includes('role="table"') && page.includes('role="columnheader"'), '表格 div 必须带 table / columnheader 语义')
assert(page.includes('aria-label={tr(') && page.includes('type="checkbox"'), '勾选与图标按钮必须有可访问名称')
// 全局契约（noAutomaticReload）会扫描 pages 下所有文件里的该字面量，所以这里拆开拼接，避免测试文件自己被误判。
assert(!page.includes(['window', 'location', 'reload'].join('.')), '页面不得自动 hard reload')

// 导出：exceljs 只在导出时动态加载；下载副作用可注入
assert(/import type ExcelJS from 'exceljs'/.test(exporter) && !/^import ExcelJS/m.test(exporter), 'export.ts 只能 import type exceljs')
assert(exporter.includes("import('exceljs')"), 'exceljs 必须动态 import')
assert(page.includes("import('./export')"), '页面应在导出时才加载 export 模块')
assert(exporter.includes('download: (blob: Blob, fileName: string) => void') && exporter.includes('addStoreSheet:'), '组装与下载副作用必须可注入')
assert(exporter.includes('Date.UTC('), '日期单元格必须按 UTC 构造')
assert(exporter.includes("numFmt = DATE_FORMAT") && exporter.includes("'yyyy-mm-dd'"), '日期单元格格式 yyyy-mm-dd')
assert(exporter.includes("'#,##0.00'"), '金额格式 #,##0.00')
assert(exporter.includes("state: 'frozen'"), '工作表必须冻结表头')
assert(logic.includes('CSV_BOM') && logic.includes("'\\uFEFF'"), 'CSV 必须带 UTF-8 BOM')

// 样式：窄屏不整页横向滚动，宽表格在自己的容器里滚动；减少动画偏好
assert(css.includes('overflow-x: auto') && css.includes('.dailyBody { max-height: 560px; overflow: auto; }'), '宽表格必须在自己的容器里滚动')
assert(css.includes('@media (max-width: 720px)') && css.includes('prefers-reduced-motion'), '必须有窄屏与减少动画处理')
assert(css.includes('.page button:focus-visible') && css.includes('outline: 2px solid'), '键盘焦点必须可见')
assert(css.includes('#0958d9'), '主按钮使用满足对比度的 #0958d9')

// 路由与权限登记
const routeStart = routes.indexOf("path: '/executive-sales-intelligence/monthly-daily-sales-download'")
assert(routeStart >= 0, '必须登记路由')
const routeBlock = routes.slice(routeStart, routes.indexOf('element:', routeStart))
assert(routeBlock.includes("title: 'menu.monthlyDailySalesDownload'"), '路由标题使用全局菜单键')
assert(routeBlock.includes('keepAlive: true'), '路由必须 keepAlive')
assert(routeBlock.includes("accessKey: 'canViewMonthlyDailySalesDownload'"), '路由使用本页访问键')
assert(routes.includes("lazy(() => import('../pages/ExecutiveSalesIntelligence/MonthlyDailySalesDownload'))"), '页面必须懒加载')
assert(permissions.includes("MonthlyDailySalesDownloadView: 'SalesDashboard.MonthlyDailySalesDownload.View'"), '权限码常量')
assert(access.includes('hasPermission(P.SalesDashboard.MonthlyDailySalesDownloadView)'), 'access 必须读取本页权限')
const parentStart = access.indexOf('const canViewSalesIntelligence =')
assert(parentStart >= 0 && access.slice(parentStart, access.indexOf('// ---', parentStart)).includes('canViewMonthlyDailySalesDownload'), '销售看板父菜单必须被本页权限点亮')
assert(portal.includes("'monthly-daily-sales-download'") && portal.includes('MonthlyDailySalesDownloadView'), '门户落地规则必须包含本页')
assert(preview.includes('canViewMonthlyDailySalesDownload') && preview.includes('/executive-sales-intelligence/monthly-daily-sales-download'), '菜单预览必须包含本页')

console.log('monthlyDailySalesDownload source contract: ok')
