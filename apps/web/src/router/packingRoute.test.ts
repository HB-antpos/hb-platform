import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from '../utils/access'
import { buildWebRoleMenuPreview, getAccessKeyPermissionCodes, type WebMenuPreviewNode } from '../utils/webMenuPreview'
import { getDefaultWebPath, hasBackendNavigationAccess, resolveAuthorizedWebTarget } from '../utils/webPortalAccess'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createCurrentUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'packing-user',
    username: 'packing-user',
    email: 'packing@example.invalid',
    permissions,
    exactPermissions: permissions,
    roleNames,
    storeNames: [],
  }
}

function findNode(nodes: WebMenuPreviewNode[], path: string): WebMenuPreviewNode | undefined {
  for (const node of nodes) {
    if (node.path === path) return node
    const child = node.children ? findNode(node.children, path) : undefined
    if (child) return child
  }
  return undefined
}

const STORE_PACKING_PATH = '/packing'
const WAREHOUSE_PACKING_PATH = '/warehouse/packing'

// 路由登记：两个入口共用一个懒加载页面组件、各自保活；仓库打包在仓库管理组，分店打包在收银管理组。
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
function routeBlockOf(path: string) {
  const start = routeSource.indexOf(`path: '${path}'`)
  assertEqual(start >= 0, true, `必须登记 ${path} 路由`)
  return { start, block: routeSource.slice(start, routeSource.indexOf('/>', start) + 2) }
}
assertEqual(
  routeSource.includes("const PackingPage = lazy(() => import('../pages/Packing'))"),
  true,
  '页面必须懒加载，不进首屏包',
)
const warehouseRoute = routeBlockOf(WAREHOUSE_PACKING_PATH)
assertEqual(warehouseRoute.block.includes("title: 'menu.warehousePacking'"), true, '仓库打包路由标题使用全局菜单键')
assertEqual(warehouseRoute.block.includes('keepAlive: true'), true, '仓库打包路由必须 keepAlive')
assertEqual(warehouseRoute.block.includes("accessKey: 'canUseWarehousePacking'"), true, '仓库打包路由使用仓库打包访问键')
assertEqual(warehouseRoute.block.includes('element: <PackingPage side="warehouse" />'), true, '仓库打包路由只看总仓')
const storeRoute = routeBlockOf(STORE_PACKING_PATH)
assertEqual(storeRoute.block.includes("title: 'menu.storePacking'"), true, '分店打包路由标题使用全局菜单键')
assertEqual(storeRoute.block.includes('keepAlive: true'), true, '分店打包路由必须 keepAlive')
assertEqual(storeRoute.block.includes("accessKey: 'canUseStorePacking'"), true, '分店打包路由使用分店打包访问键')
assertEqual(storeRoute.block.includes('element: <PackingPage side="store" />'), true, '分店打包路由只看分店')
assertEqual(routeSource.includes('canUsePacking'), false, '不再有两边共用的访问键')
const warehouseStart = routeSource.indexOf("path: '/warehouse',")
const posAdminStart = routeSource.indexOf("path: '/pos-admin',")
assertEqual(warehouseStart >= 0 && warehouseStart < warehouseRoute.start && warehouseRoute.start < posAdminStart, true, '仓库打包登记在仓库管理组里')
assertEqual(posAdminStart < storeRoute.start, true, '分店打包登记在收银管理组之后')

// 全局语言包两个菜单键；页面文案在页面目录懒注册
for (const language of ['zh', 'en']) {
  const messages = JSON.parse(readFileSync(join(process.cwd(), `src/i18n/locales/${language}.json`), 'utf8')) as {
    menu: Record<string, string>
  }
  assertEqual(typeof messages.menu.warehousePacking, 'string', `${language} 语言包必须有 menu.warehousePacking`)
  assertEqual(typeof messages.menu.storePacking, 'string', `${language} 语言包必须有 menu.storePacking`)
  assertEqual('packing' in messages.menu, false, `${language} 语言包不再有两边共用的 menu.packing`)
}

// 权限码与别名：仓库打包 = Warehouse.Packing 及其别名（管理仓库、管理仓库订货）；分店打包 = StorePacking.Use
assertEqual(P.StorePacking.Use, 'StorePacking.Use', '分店打包权限码与后端一致')
assertEqual(P.Warehouse.Packing, 'Warehouse.Packing', '总仓打包权限码与后端一致')
assertEqual(
  getAccessKeyPermissionCodes('canUseWarehousePacking').join(','),
  [P.Warehouse.Packing, P.Warehouse.Manage, P.Warehouse.ManageOrders].join(','),
  '仓库打包菜单映射到仓库打包权限及其别名',
)
assertEqual(getAccessKeyPermissionCodes('canUseStorePacking').join(','), P.StorePacking.Use, '分店打包菜单只映射分店打包权限')

// 后端 Web 菜单同口径：两个入口的路径、标题键、权限；只有打包权限的账号也要拿得到后台菜单
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  navigationSource.includes(
    'new() { Path = "/warehouse/packing", TitleKey = "menu.warehousePacking", Icon = "InboxOutlined", AnyPermissions = WarehousePackingMenuPermissions },',
  ),
  true,
  '后端 FullMenu 应在仓库组登记仓库打包',
)
assertEqual(
  navigationSource.includes(
    'new() { Path = "/packing", TitleKey = "menu.storePacking", Icon = "InboxOutlined", Permission = Permissions.StorePacking.Use },',
  ),
  true,
  '后端 FullMenu 应在收银管理组登记分店打包',
)
const backendAccessStart = navigationSource.indexOf('private static bool HasBackendNavigationAccess')
const backendAccessBlock = navigationSource.slice(
  backendAccessStart,
  navigationSource.indexOf('private static bool CanAccess', backendAccessStart),
)
assertEqual(backendAccessBlock.includes('Permissions.StorePacking.Use'), true, '后端后台入口判定应包含分店打包权限')
assertEqual(backendAccessBlock.includes('Permissions.Warehouse.Packing'), true, '后端后台入口判定应包含总仓打包权限')

// 只有分店打包权限：能进后台、登录落到分店打包，看不到仓库打包
const storeOnly = buildAccess(createCurrentUser([P.StorePacking.Use], ['StoreStaff']))
assertEqual(storeOnly.canUseStorePacking, true, '单独授予分店打包权限即可使用分店打包')
assertEqual(storeOnly.canUseWarehousePacking, false, '分店打包权限不连带仓库打包')
assertEqual(storeOnly.canManageWarehouseOrders, false, '分店打包权限不获得订货管理')
assertEqual(storeOnly.canManageWarehouseLocations, false, '分店打包权限不获得货位管理')
assertEqual(hasBackendNavigationAccess(storeOnly), true, '只有该权限的店员也要能进入后台')
assertEqual(getDefaultWebPath(storeOnly), STORE_PACKING_PATH, '只有该权限时登录落到分店打包页')
assertEqual(resolveAuthorizedWebTarget(STORE_PACKING_PATH, storeOnly), STORE_PACKING_PATH, '分店打包页地址应被放行')
assertEqual(resolveAuthorizedWebTarget(WAREHOUSE_PACKING_PATH, storeOnly), undefined, '不能顺带放行仓库打包')
assertEqual(resolveAuthorizedWebTarget('/warehouse/store-orders', storeOnly), undefined, '不能顺带放行分店订货')
const storePreview = buildWebRoleMenuPreview(storeOnly, (key) => key, { includeHidden: true })
assertEqual(findNode(storePreview, STORE_PACKING_PATH)?.visible, true, '角色菜单预览应显示分店打包入口')
assertEqual(findNode(storePreview, STORE_PACKING_PATH)?.permissionCodes[0], P.StorePacking.Use, '预览节点主权限是分店打包')
assertEqual(findNode(storePreview, WAREHOUSE_PACKING_PATH)?.visible, false, '预览不应显示仓库打包')
assertEqual(findNode(storePreview, '/warehouse/store-orders')?.visible, false, '预览不应显示分店订货')

// 只有总仓打包权限：落到仓库打包，看不到分店打包
const warehouseOnly = buildAccess(createCurrentUser([P.Warehouse.Packing]))
assertEqual(warehouseOnly.canUseWarehousePacking, true, '单独授予总仓打包权限即可使用仓库打包')
assertEqual(warehouseOnly.canUseStorePacking, false, '总仓打包权限不连带分店打包')
assertEqual(hasBackendNavigationAccess(warehouseOnly), true, '只有总仓打包权限也要能进入后台')
assertEqual(getDefaultWebPath(warehouseOnly), WAREHOUSE_PACKING_PATH, '只有总仓打包权限时落到仓库打包页')
assertEqual(resolveAuthorizedWebTarget(STORE_PACKING_PATH, warehouseOnly), undefined, '不能顺带放行分店打包')
const warehousePreview = buildWebRoleMenuPreview(warehouseOnly, (key) => key, { includeHidden: true })
assertEqual(findNode(warehousePreview, WAREHOUSE_PACKING_PATH)?.visible, true, '角色菜单预览应显示仓库打包入口')
assertEqual(findNode(warehousePreview, STORE_PACKING_PATH)?.visible, false, '预览不应显示分店打包')

// 别名：管理仓库 / 管理仓库订货自动具备仓库打包（不具备分店打包）；只管商品的账号两边都没有
for (const code of [P.Warehouse.Manage, P.Warehouse.ManageOrders]) {
  const alias = buildAccess(createCurrentUser([code]))
  assertEqual(alias.canUseWarehousePacking, true, `${code} 应自动具备仓库打包`)
  assertEqual(alias.canUseStorePacking, false, `${code} 不具备分店打包`)
  assertEqual(resolveAuthorizedWebTarget(WAREHOUSE_PACKING_PATH, alias), WAREHOUSE_PACKING_PATH, `${code} 可访问仓库打包页`)
  assertEqual(getDefaultWebPath(alias), '/warehouse/store-orders', `${code} 沿用原默认入口`)
}
const productsOnly = buildAccess(createCurrentUser([P.Dashboard.View, P.Warehouse.ManageProducts]))
assertEqual(productsOnly.canUseWarehousePacking, false, '只管商品的账号不能仓库打包')
assertEqual(productsOnly.canUseStorePacking, false, '只管商品的账号不能分店打包')
assertEqual(resolveAuthorizedWebTarget(WAREHOUSE_PACKING_PATH, productsOnly), undefined, '只管商品的账号不能访问仓库打包页')
assertEqual(resolveAuthorizedWebTarget(STORE_PACKING_PATH, productsOnly), undefined, '只管商品的账号不能访问分店打包页')

// 管理员两个入口都可见
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canUseWarehousePacking, true, '管理员可使用仓库打包')
assertEqual(admin.canUseStorePacking, true, '管理员可使用分店打包')

// 仓库员工（受限菜单）：仓库组里只有分店订货和仓库打包，没有收银管理组
const warehouseStaff = buildAccess(createCurrentUser([P.Warehouse.Manage, P.Warehouse.Packing, P.Orders.View], ['WarehouseStaff']))
const staffPreview = buildWebRoleMenuPreview(warehouseStaff, (key) => key, { includeHidden: false })
assertEqual(staffPreview.map((node) => node.path).join(','), '/warehouse', '仓库员工菜单：只有仓库组')
assertEqual(
  findNode(staffPreview, '/warehouse')?.children?.map((node) => node.path).join(','),
  `/warehouse/store-orders,${WAREHOUSE_PACKING_PATH}`,
  '仓库员工的仓库组：分店订货 + 仓库打包',
)

// 页面入口不拆 lazy 子组件（包体门禁要求页面级入口是独立动态分块）
const pageSource = readFileSync(join(process.cwd(), 'src/pages/Packing/index.tsx'), 'utf8')
assertEqual(/\blazy\(/.test(pageSource), false, '页面入口里不要再拆 lazy 子组件')
assertEqual(pageSource.includes('registerPageMessages('), true, '页面文案必须页面级懒注册')
assertEqual(pageSource.includes('listPackDestinations('), true, '去向候选走打包自己的 destinations 接口')
assertEqual(pageSource.includes('getPackScope(side)'), true, '页面按入口只请求这一边的地点')
assertEqual(pageSource.includes('storeService'), false, '页面入口不再依赖通用分店列表接口（店员没有查看分店权限）')

console.log('packingRoute.test: ok')
