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
    userGUID: 'seasonal-card-stats-user',
    username: 'seasonal-card-stats-user',
    email: 'seasonal-card-stats@example.invalid',
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

const STATS_PATH = '/pos-admin/seasonal-card-stats'
const VIEW_ALL = 'SeasonalCards.Remaining.ViewAllStores'

// 路由登记：懒加载、保活、使用全局菜单键与本页访问键
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${STATS_PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记分店填报统计路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart))
assertEqual(
  routeSource.includes("const SeasonalCardStatsPage = lazy(() => import('../pages/PosAdmin/SeasonalCardStats'))"),
  true,
  '页面必须懒加载，不进首屏包',
)
assertEqual(routeBlock.includes("title: 'menu.seasonalCardStats'"), true, '路由标题使用全局菜单键')
assertEqual(routeBlock.includes("icon: 'GiftOutlined'"), true, '菜单图标 GiftOutlined')
assertEqual(routeBlock.includes('keepAlive: true'), true, '路由必须 keepAlive')
assertEqual(routeBlock.includes("accessKey: 'canViewSeasonalCardStats'"), true, '路由使用分店填报统计访问键')
assertEqual(routeSource.includes('element: <SeasonalCardStatsPage />'), true, '路由必须渲染分店填报统计页')
assertEqual(/GiftOutlined:\s*<GiftOutlined\s*\/>/.test(routeSource), true, '图标表里必须有 GiftOutlined')

// 权限码与后端一致
assertEqual(P.SeasonalCards.Remaining.ViewAllStores, VIEW_ALL, '权限码与后端 Permissions.SeasonalCards.Remaining.ViewAllStores 一致')
const backendPermissions = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Shared/Constants/Permissions.cs'),
  'utf8',
)
assertEqual(backendPermissions.includes(`ViewAllStores = "${VIEW_ALL}"`), true, '后端权限常量值一致')
assertEqual(getAccessKeyPermissionCodes('canViewSeasonalCardStats').join(','), VIEW_ALL, '菜单应只映射到 ViewAllStores')

// 后端 Web 菜单同一入口：路径、标题键、图标、独立权限；且该权限能拿到后台菜单
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/pos-admin\/seasonal-card-stats".*TitleKey = "menu\.seasonalCardStats".*Icon = "GiftOutlined".*Permission = Permissions\.SeasonalCards\.Remaining\.ViewAllStores/.test(
    navigationSource,
  ),
  true,
  '后端 FullMenu 应登记分店填报统计入口，且仅凭 ViewAllStores 可见',
)
const backendAccessStart = navigationSource.indexOf('private static bool HasBackendNavigationAccess')
const backendAccessBlock = navigationSource.slice(backendAccessStart, navigationSource.indexOf(');', backendAccessStart))
assertEqual(
  backendAccessBlock.includes('Permissions.SeasonalCards.Remaining.ViewAllStores'),
  true,
  '后端后台入口集合必须包含 ViewAllStores（否则只有该权限的账号拿不到后台菜单）',
)

// 只有该权限：能进后台、登录落到统计页、深链（带 ?store=）被放行
const only = buildAccess(createCurrentUser([VIEW_ALL]))
assertEqual(only.canViewSeasonalCardStats, true, '单独授予即可查看分店填报统计')
assertEqual(only.canAccessDashboard, false, '不连带工作台')
assertEqual(hasBackendNavigationAccess(only), true, '只有该权限的账号也要能进入后台')
assertEqual(getDefaultWebPath(only), STATS_PATH, '只有该权限时登录落到分店填报统计页')
const deepLink = `${STATS_PATH}?store=1013`
assertEqual(resolveAuthorizedWebTarget(deepLink, only), deepLink, '带抽屉参数的深链应被放行')
assertEqual(resolveAuthorizedWebTarget('/pos-admin/daily-closes', only), undefined, '不能顺带放行其他后台页面')
const onlyPreview = buildWebRoleMenuPreview(only, (key) => key, { includeHidden: true })
assertEqual(findNode(onlyPreview, STATS_PATH)?.visible, true, '角色菜单预览应显示分店填报统计入口')
assertEqual(findNode(onlyPreview, STATS_PATH)?.permissionCodes.join(','), VIEW_ALL, '预览节点只要求本权限')
assertEqual(onlyPreview.find((node) => node.path === '/pos-admin')?.visible, true, '应点亮 /pos-admin 父菜单')

// 店长的移动端填报 / 本店查看权限都不能打开它
for (const code of ['SeasonalCards.Remaining.ViewManagedStore', 'SeasonalCards.Remaining.SubmitManagedStore', P.Dashboard.View]) {
  const other = buildAccess(createCurrentUser([code]))
  assertEqual(other.canViewSeasonalCardStats, false, `${code} 不应放行分店填报统计`)
  assertEqual(
    findNode(buildWebRoleMenuPreview(other, (key) => key, { includeHidden: true }), STATS_PATH)?.visible,
    false,
    `${code} 的菜单预览不应显示分店填报统计`,
  )
  assertEqual(resolveAuthorizedWebTarget(STATS_PATH, other), undefined, `${code} 不能访问分店填报统计地址`)
}

// 管理员可见
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canViewSeasonalCardStats, true, '管理员可查看分店填报统计')
assertEqual(
  findNode(buildWebRoleMenuPreview(admin, (key) => key, { includeHidden: true }), STATS_PATH)?.visible,
  true,
  '管理员菜单预览应显示分店填报统计',
)

// 组合权限用户沿用原默认入口，不被本页抢走落点
const withDailyCloses = buildAccess(createCurrentUser([P.DailyCloseRecords.View, VIEW_ALL]))
assertEqual(getDefaultWebPath(withDailyCloses), '/pos-admin/daily-closes', '组合权限用户沿用原默认入口')

// 移动端角色目录里的 Web 菜单与后端同一权限
const mobileCatalog = readFileSync(
  join(process.cwd(), '../mobile/src/modules/identity-admin/role-menu-catalog.ts'),
  'utf8',
)
assertEqual(
  mobileCatalog.includes(`["${STATS_PATH}", "Seasonal card stats", "节日贺卡填报统计", ["${VIEW_ALL}"]]`),
  true,
  '移动端角色目录 WEB_MENU 应登记分店填报统计',
)

console.log('seasonalCardStatsRoute.test: ok')
