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

const PACKING_PATH = '/warehouse/packing'

// 路由登记：懒加载、保活、全局菜单键与本页访问键
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${PACKING_PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记扫码打包路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart))
assertEqual(
  routeSource.includes("const WarehousePackingPage = lazy(() => import('../pages/Warehouse/Packing'))"),
  true,
  '页面必须懒加载，不进首屏包',
)
assertEqual(routeBlock.includes("title: 'menu.warehousePacking'"), true, '路由标题使用全局菜单键')
assertEqual(routeBlock.includes('keepAlive: true'), true, '路由必须 keepAlive')
assertEqual(routeBlock.includes("accessKey: 'canUseWarehousePacking'"), true, '路由使用扫码打包访问键')
assertEqual(routeSource.includes('element: <WarehousePackingPage />'), true, '路由必须渲染扫码打包页')

// 全局语言包只放一个菜单键；页面文案在页面目录懒注册
for (const language of ['zh', 'en']) {
  const messages = JSON.parse(readFileSync(join(process.cwd(), `src/i18n/locales/${language}.json`), 'utf8')) as {
    menu: Record<string, string>
  }
  assertEqual(typeof messages.menu.warehousePacking, 'string', `${language} 语言包必须有 menu.warehousePacking`)
}

// 权限码与别名：与后端 Permissions.Warehouse.Packing 及其别名（管理仓库、管理仓库订货）一致
assertEqual(P.Warehouse.Packing, 'Warehouse.Packing', '权限码与后端一致')
assertEqual(
  getAccessKeyPermissionCodes('canUseWarehousePacking').join(','),
  [P.Warehouse.Packing, P.Warehouse.Manage, P.Warehouse.ManageOrders].join(','),
  '菜单映射到打包权限及其别名',
)

// 后端 Web 菜单同一入口：路径、标题键、图标、权限；只有打包权限的账号也要拿得到后台菜单
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/warehouse\/packing",\s+TitleKey = "menu\.warehousePacking",\s+Icon = "InboxOutlined",\s+Permission = Permissions\.Warehouse\.Packing/.test(
    navigationSource,
  ),
  true,
  '后端 FullMenu 应登记扫码打包',
)
const backendAccessStart = navigationSource.indexOf('private static bool HasBackendNavigationAccess')
assertEqual(
  navigationSource.slice(backendAccessStart, navigationSource.indexOf('private static bool CanAccess', backendAccessStart))
    .includes('Permissions.Warehouse.Packing'),
  true,
  '后端后台入口判定应包含打包权限',
)

// 只有打包权限：能进后台、登录落到打包页，不连带其他仓库页面
const only = buildAccess(createCurrentUser([P.Warehouse.Packing]))
assertEqual(only.canUseWarehousePacking, true, '单独授予即可使用扫码打包')
assertEqual(only.canManageWarehouseOrders, false, '打包权限不反向获得订货管理')
assertEqual(only.canManageWarehouseLocations, false, '打包权限不反向获得货位管理')
assertEqual(hasBackendNavigationAccess(only), true, '只有该权限的账号也要能进入后台')
assertEqual(getDefaultWebPath(only), PACKING_PATH, '只有该权限时登录落到扫码打包页')
assertEqual(resolveAuthorizedWebTarget(PACKING_PATH, only), PACKING_PATH, '打包页地址应被放行')
assertEqual(resolveAuthorizedWebTarget('/warehouse/store-orders', only), undefined, '不能顺带放行分店订货')
const onlyPreview = buildWebRoleMenuPreview(only, (key) => key, { includeHidden: true })
assertEqual(findNode(onlyPreview, PACKING_PATH)?.visible, true, '角色菜单预览应显示扫码打包入口')
assertEqual(findNode(onlyPreview, '/warehouse/store-orders')?.visible, false, '预览不应显示分店订货')

// 别名：管理仓库 / 管理仓库订货自动具备；只管商品的账号没有
for (const code of [P.Warehouse.Manage, P.Warehouse.ManageOrders]) {
  const alias = buildAccess(createCurrentUser([code]))
  assertEqual(alias.canUseWarehousePacking, true, `${code} 应自动具备扫码打包`)
  assertEqual(resolveAuthorizedWebTarget(PACKING_PATH, alias), PACKING_PATH, `${code} 可访问打包页`)
  assertEqual(getDefaultWebPath(alias), '/warehouse/store-orders', `${code} 沿用原默认入口`)
}
const productsOnly = buildAccess(createCurrentUser([P.Dashboard.View, P.Warehouse.ManageProducts]))
assertEqual(productsOnly.canUseWarehousePacking, false, '只管商品的账号不能打包')
assertEqual(resolveAuthorizedWebTarget(PACKING_PATH, productsOnly), undefined, '只管商品的账号不能访问打包页')
assertEqual(
  findNode(buildWebRoleMenuPreview(productsOnly, (key) => key, { includeHidden: true }), PACKING_PATH)?.visible,
  false,
  '只管商品的账号菜单预览不显示扫码打包',
)

// 管理员可见
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canUseWarehousePacking, true, '管理员可使用扫码打包')

// 页面入口不拆 lazy 子组件（包体门禁要求页面级入口是独立动态分块）
const pageSource = readFileSync(join(process.cwd(), 'src/pages/Warehouse/Packing/index.tsx'), 'utf8')
assertEqual(/\blazy\(/.test(pageSource), false, '页面入口里不要再拆 lazy 子组件')
assertEqual(pageSource.includes('registerPageMessages('), true, '页面文案必须页面级懒注册')

console.log('warehousePackingRoute.test: ok')
