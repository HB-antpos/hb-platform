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

const PACKING_PATH = '/packing'

// 路由登记：懒加载、保活、全局菜单键与本页访问键；放在「收银管理」组（/pos-admin）里，不在仓库管理组
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${PACKING_PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记扫码打包路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart))
assertEqual(
  routeSource.includes("const PackingPage = lazy(() => import('../pages/Packing'))"),
  true,
  '页面必须懒加载，不进首屏包',
)
assertEqual(routeBlock.includes("title: 'menu.packing'"), true, '路由标题使用全局菜单键')
assertEqual(routeBlock.includes('keepAlive: true'), true, '路由必须 keepAlive')
assertEqual(routeBlock.includes("accessKey: 'canUsePacking'"), true, '路由使用中性的扫码打包访问键')
assertEqual(routeSource.includes('element: <PackingPage />'), true, '路由必须渲染扫码打包页')
assertEqual(routeSource.includes('/warehouse/packing'), false, '旧的仓库组地址不再登记')
assertEqual(routeSource.includes('canUseWarehousePacking'), false, '旧访问键不再使用')
const posAdminStart = routeSource.indexOf("path: '/pos-admin',")
assertEqual(posAdminStart >= 0 && posAdminStart < routeStart, true, '扫码打包路由在「收银管理」组之后登记')
const warehouseStart = routeSource.indexOf("path: '/warehouse',")
assertEqual(warehouseStart >= 0 && warehouseStart < posAdminStart, true, '仓库管理组在收银管理组之前')

// 全局语言包只放一个菜单键；页面文案在页面目录懒注册
for (const language of ['zh', 'en']) {
  const messages = JSON.parse(readFileSync(join(process.cwd(), `src/i18n/locales/${language}.json`), 'utf8')) as {
    menu: Record<string, string>
  }
  assertEqual(typeof messages.menu.packing, 'string', `${language} 语言包必须有 menu.packing`)
  assertEqual('warehousePacking' in messages.menu, false, `${language} 语言包不再有旧的 menu.warehousePacking`)
}

// 权限码与别名：StorePacking.Use（分店）、Warehouse.Packing（总仓）及其别名（管理仓库、管理仓库订货）
assertEqual(P.StorePacking.Use, 'StorePacking.Use', '分店打包权限码与后端一致')
assertEqual(P.Warehouse.Packing, 'Warehouse.Packing', '总仓打包权限码与后端一致')
assertEqual(
  getAccessKeyPermissionCodes('canUsePacking').join(','),
  [P.StorePacking.Use, P.Warehouse.Packing, P.Warehouse.Manage, P.Warehouse.ManageOrders].join(','),
  '菜单映射到分店 / 总仓打包权限及其别名',
)

// 后端 Web 菜单同一入口：路径、标题键、图标、权限；只有打包权限的账号也要拿得到后台菜单
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/packing",\s+TitleKey = "menu\.packing",\s+Icon = "InboxOutlined",\s+AnyPermissions = PackingMenuPermissions/.test(
    navigationSource,
  ),
  true,
  '后端 FullMenu 应登记扫码打包',
)
assertEqual(navigationSource.includes('"/warehouse/packing"'), false, '后端不再有旧的仓库组入口')
const backendAccessStart = navigationSource.indexOf('private static bool HasBackendNavigationAccess')
const backendAccessBlock = navigationSource.slice(
  backendAccessStart,
  navigationSource.indexOf('private static bool CanAccess', backendAccessStart),
)
assertEqual(backendAccessBlock.includes('Permissions.StorePacking.Use'), true, '后端后台入口判定应包含分店打包权限')
assertEqual(backendAccessBlock.includes('Permissions.Warehouse.Packing'), true, '后端后台入口判定应包含总仓打包权限')

// 只有分店打包权限：能进后台、登录落到打包页，不连带其他页面
const storeOnly = buildAccess(createCurrentUser([P.StorePacking.Use], ['StoreStaff']))
assertEqual(storeOnly.canUsePacking, true, '单独授予分店打包权限即可使用')
assertEqual(storeOnly.canManageWarehouseOrders, false, '分店打包权限不获得订货管理')
assertEqual(storeOnly.canManageWarehouseLocations, false, '分店打包权限不获得货位管理')
assertEqual(hasBackendNavigationAccess(storeOnly), true, '只有该权限的店员也要能进入后台')
assertEqual(getDefaultWebPath(storeOnly), PACKING_PATH, '只有该权限时登录落到扫码打包页')
assertEqual(resolveAuthorizedWebTarget(PACKING_PATH, storeOnly), PACKING_PATH, '打包页地址应被放行')
assertEqual(resolveAuthorizedWebTarget('/warehouse/store-orders', storeOnly), undefined, '不能顺带放行分店订货')
const storePreview = buildWebRoleMenuPreview(storeOnly, (key) => key, { includeHidden: true })
assertEqual(findNode(storePreview, PACKING_PATH)?.visible, true, '角色菜单预览应显示扫码打包入口')
assertEqual(findNode(storePreview, PACKING_PATH)?.permissionCodes[0], P.StorePacking.Use, '预览节点主权限是分店打包')
assertEqual(findNode(storePreview, '/warehouse/store-orders')?.visible, false, '预览不应显示分店订货')

// 只有总仓打包权限
const warehouseOnly = buildAccess(createCurrentUser([P.Warehouse.Packing]))
assertEqual(warehouseOnly.canUsePacking, true, '单独授予总仓打包权限即可使用')
assertEqual(getDefaultWebPath(warehouseOnly), PACKING_PATH, '只有总仓打包权限时落到打包页')

// 别名：管理仓库 / 管理仓库订货自动具备；只管商品的账号没有
for (const code of [P.Warehouse.Manage, P.Warehouse.ManageOrders]) {
  const alias = buildAccess(createCurrentUser([code]))
  assertEqual(alias.canUsePacking, true, `${code} 应自动具备扫码打包`)
  assertEqual(resolveAuthorizedWebTarget(PACKING_PATH, alias), PACKING_PATH, `${code} 可访问打包页`)
  assertEqual(getDefaultWebPath(alias), '/warehouse/store-orders', `${code} 沿用原默认入口`)
}
const productsOnly = buildAccess(createCurrentUser([P.Dashboard.View, P.Warehouse.ManageProducts]))
assertEqual(productsOnly.canUsePacking, false, '只管商品的账号不能打包')
assertEqual(resolveAuthorizedWebTarget(PACKING_PATH, productsOnly), undefined, '只管商品的账号不能访问打包页')
assertEqual(
  findNode(buildWebRoleMenuPreview(productsOnly, (key) => key, { includeHidden: true }), PACKING_PATH)?.visible,
  false,
  '只管商品的账号菜单预览不显示扫码打包',
)

// 管理员可见
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canUsePacking, true, '管理员可使用扫码打包')

// 仓库员工（受限菜单）：除分店订货外，持有打包权限的要能看到「收银管理 → 扫码打包」，组里其他页面不开放
const warehouseStaff = buildAccess(createCurrentUser([P.Warehouse.Manage, P.Warehouse.Packing, P.Orders.View], ['WarehouseStaff']))
const staffPreview = buildWebRoleMenuPreview(warehouseStaff, (key) => key, { includeHidden: false })
assertEqual(staffPreview.map((node) => node.path).join(','), '/warehouse,/pos-admin', '仓库员工菜单：仓库组 + 收银管理组')
assertEqual(
  findNode(staffPreview, '/pos-admin')?.children?.map((node) => node.path).join(','),
  PACKING_PATH,
  '仓库员工在收银管理组里只看到扫码打包',
)
assertEqual(findNode(staffPreview, '/warehouse')?.children?.map((node) => node.path).join(','), '/warehouse/store-orders', '仓库组仍只有分店订货')

// 页面入口不拆 lazy 子组件（包体门禁要求页面级入口是独立动态分块）
const pageSource = readFileSync(join(process.cwd(), 'src/pages/Packing/index.tsx'), 'utf8')
assertEqual(/\blazy\(/.test(pageSource), false, '页面入口里不要再拆 lazy 子组件')
assertEqual(pageSource.includes('registerPageMessages('), true, '页面文案必须页面级懒注册')
assertEqual(pageSource.includes('listPackDestinations('), true, '去向候选走打包自己的 destinations 接口')
assertEqual(pageSource.includes('storeService'), false, '页面入口不再依赖通用分店列表接口（店员没有查看分店权限）')

console.log('packingRoute.test: ok')
