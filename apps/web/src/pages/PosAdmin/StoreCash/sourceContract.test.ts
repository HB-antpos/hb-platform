import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 现金管理页接线契约：测试不能 import 页面组件 / CSS，所以读源码静态核对关键约定。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/PosAdmin/StoreCash'
const read = (path: string) => readFileSync(resolve(path), 'utf8')
const page = read(`${directory}/index.tsx`)
const overview = read(`${directory}/OverviewTab.tsx`)
const daily = read(`${directory}/DailyTab.tsx`)
const deposits = read(`${directory}/DepositsTab.tsx`)
const expenses = read(`${directory}/ExpensesTab.tsx`)
const depositDrawer = read(`${directory}/DepositDrawer.tsx`)
const expenseDrawer = read(`${directory}/ExpenseDrawer.tsx`)
const logic = read(`${directory}/logic.ts`)
const csv = read(`${directory}/csv.ts`)
const css = read(`${directory}/storeCash.css`)
const service = read('src/services/storeCashService.ts')
const routes = read('src/router/routes.tsx')
const access = read('src/utils/access.ts')
const portal = read('src/utils/webPortalAccess.ts')
const preview = read('src/utils/webMenuPreview.ts')
const expoPreview = read('src/utils/expoRoleMenuPreview.ts')
const permissions = read('src/types/permissions.ts')
const authTypes = read('src/types/auth.ts')
const navigation = read('../../services/backend/BlazorApp.Api/Services/NavigationService.cs')
const allPageSources = [page, overview, daily, deposits, expenses, depositDrawer, expenseDrawer, logic, csv]

// 数据：只经服务层调用约定的接口
assert(service.includes("'/api/react/v1/cash'"), '服务必须请求 api/react/v1/cash')
for (const source of allPageSources) {
  assert(!source.includes('/api/react/v1/cash'), '页面必须经 storeCashService 调用接口')
}
assert(page.includes('useKeepAliveContext') && page.includes('active && query.tab ==='), '保活页面与页签激活时才取数')
assert(page.includes('parseCashSearch(') && page.includes('serializeCashQuery(') && page.includes('{ replace: true }'), '页签与筛选写进地址栏（replace）')
assert(page.includes('location.pathname !== STORE_CASH_PATH'), '保活隐藏时不能跟随别的页面地址改状态')

// 按钮显隐以接口返回为准，不按角色推断
for (const source of allPageSources) {
  assert(!/hasRole\(|isAdmin|isStoreManager/.test(source), '页面不能按角色推断按钮显隐')
}
assert(depositDrawer.includes('detail.canVoid'), '存款作废按钮以 canVoid 为准')
assert(expenseDrawer.includes('detail?.canReview') && expenseDrawer.includes('detail?.canVoid'), '支出核对 / 作废以 canReview / canVoid 为准')

// 日结未接入：不可算的金额是 null，显示「—」
assert(page.includes('dailyCloseConnected') && page.includes("tr('dailyCloseMissing.title')"), '日结未接入时页顶说明')
assert(logic.includes("EMPTY_CELL = '—'"), '空值显示破折号')

// 表格：只用 MeasuredTable，metricId 唯一
const metricIds = [overview, daily, deposits, expenses]
  .flatMap((source) => [...source.matchAll(/metricId="([^"]+)"/g)].map((match) => match[1]))
assert(metricIds.length >= 5, '每张表都要有 metricId')
assert(new Set(metricIds).size === metricIds.length, `metricId 必须唯一：${metricIds.join(',')}`)
assert(metricIds.every((id) => id.startsWith('pos-admin.store-cash.')), 'metricId 统一前缀 pos-admin.store-cash.')
for (const source of [overview, daily, deposits, expenses]) {
  assert(source.includes('<MeasuredTable'), '表格必须用 MeasuredTable')
  assert(!/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(source), '不能直接从 antd 引入 Table')
}

// 图片：缩略图懒加载，PreviewGroup 支持放大与左右翻页；每次打开详情重新取（签名地址几分钟过期）
for (const drawer of [depositDrawer, expenseDrawer]) {
  assert(drawer.includes('<Image.PreviewGroup>') && drawer.includes('loading="lazy"'), '存单 / 收据图片要懒加载并可放大翻页')
}
assert(deposits.includes('drawerSeqRef.current += 1') && expenses.includes('drawerSeqRef.current += 1'), '每次打开详情都重新取数据')

// 导出：纯前端 CSV，带 BOM，分页拉全量有上限
assert(csv.includes("CSV_BOM = '\\uFEFF'") && csv.includes('text/csv;charset=utf-8'), 'CSV 必须 UTF-8 带 BOM')
assert(deposits.includes('fetchAllPages') && expenses.includes('fetchAllPages'), '存款、支出导出按分页循环取完')
assert(deposits.includes('EXPORT_MAX_ROWS') && expenses.includes('EXPORT_MAX_ROWS'), '导出有 5000 行上限')
// 全局契约（noAutomaticReload）会扫描 pages 下所有文件里的该字面量，所以这里拆开拼接。
for (const source of allPageSources) {
  assert(!source.includes(['window', 'location', 'reload'].join('.')), '页面不得自动 hard reload')
}

// 样式：窄屏换行、宽表格在容器内滚动、减少动画偏好
assert(css.includes('@media (max-width: 767px)') && css.includes('prefers-reduced-motion'), '必须有窄屏与减少动画处理')
assert(css.includes('.store-cash-negative') && css.includes('#cf1322'), '负数用醒目色')
for (const source of [overview, daily, deposits, expenses]) {
  assert(/scroll=\{\{ x: \d+ \}\}/.test(source), '宽表格横向滚动')
}

// 路由与权限登记
const routeStart = routes.indexOf("path: '/pos-admin/store-cash'")
assert(routeStart >= 0, '必须登记路由')
const routeBlock = routes.slice(routeStart, routes.indexOf('element:', routeStart))
assert(routeBlock.includes("title: 'menu.storeCash'"), '路由标题使用全局菜单键')
assert(routeBlock.includes('keepAlive: true'), '路由必须 keepAlive')
assert(routeBlock.includes("accessKey: 'canViewStoreCash'"), '路由使用本页访问键')
assert(routeBlock.includes("icon: 'AccountBookOutlined'"), '路由图标与后端菜单一致')
assert(routes.includes("lazy(() => import('../pages/PosAdmin/StoreCash'))"), '页面必须懒加载')
assert(routes.includes('AccountBookOutlined: <AccountBookOutlined />'), '后端菜单图标必须在 iconMap 里，否则侧边栏不显示图标')
assert(permissions.includes("OverviewView: 'Cash.Overview.View'") && permissions.includes("AllStoresView: 'Cash.AllStores.View'"), '权限码常量')
assert(authTypes.includes('canViewStoreCash: boolean'), 'AccessControl 新字段')
assert(access.includes('const canViewStoreCash = hasPermission(P.Cash.OverviewView)'), 'access 必须读取本页权限')
assert(portal.includes("defaultPath: '/pos-admin/store-cash'") && portal.includes('P.Cash.OverviewView'), '门户落地规则必须包含本页')
assert(preview.includes("{ path: '/pos-admin/store-cash', title: 'menu.storeCash', accessKey: 'canViewStoreCash' }"), '菜单预览必须包含本页')
assert(preview.includes('canViewStoreCash: [P.Cash.OverviewView]'), '菜单预览访问键映射')
assert(expoPreview.includes('permissionCodes: [P.Cash.OverviewView]') && !expoPreview.includes("'Cash.Overview.View'"), 'App 菜单预览引用权限常量，不写死权限码')

// 与后端 Web 菜单一致：同一路径、同一权限、同一图标
assert(
  /Path = "\/pos-admin\/store-cash".*TitleKey = "menu\.storeCash".*Icon = "AccountBookOutlined".*Permission = Permissions\.Cash\.OverviewView/.test(navigation),
  '后端 FullMenu 应登记 /pos-admin/store-cash（menu.storeCash / AccountBookOutlined / Cash.Overview.View）',
)

console.log('storeCash source contract: ok')
