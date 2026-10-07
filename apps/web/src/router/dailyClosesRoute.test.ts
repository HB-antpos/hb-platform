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
    userGUID: 'daily-closes-user',
    username: 'daily-closes-user',
    email: 'daily-closes@example.invalid',
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

const DAILY_CLOSES_PATH = '/pos-admin/daily-closes'

// 路由登记：懒加载、保活、使用全局菜单键与本页访问键
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${DAILY_CLOSES_PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记日结记录路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart))
assertEqual(
  routeSource.includes("const DailyClosesPage = lazy(() => import('../pages/PosAdmin/DailyCloses'))"),
  true,
  '页面必须懒加载，不进首屏包',
)
assertEqual(routeBlock.includes("title: 'menu.dailyCloses'"), true, '路由标题使用全局菜单键')
assertEqual(routeBlock.includes("icon: 'MoneyCollectOutlined'"), true, '菜单图标 MoneyCollectOutlined')
assertEqual(routeBlock.includes('keepAlive: true'), true, '路由必须 keepAlive')
assertEqual(routeBlock.includes("accessKey: 'canViewDailyCloseRecords'"), true, '路由使用日结记录访问键')
assertEqual(routeSource.includes('element: <DailyClosesPage />'), true, '路由必须渲染日结记录页')
assertEqual(/MoneyCollectOutlined:\s*<MoneyCollectOutlined\s*\/>/.test(routeSource), true, '图标表里必须有 MoneyCollectOutlined')

// 独立顶层权限码：不能落在 Permissions.PosTerminal.* 前缀（会被当作收银机权限下发）
assertEqual(P.DailyCloseRecords.View, 'DailyCloseRecords.View', '权限码与后端 Permissions.DailyCloseRecords.View 一致')
assertEqual(P.DailyCloseRecords.View.startsWith('Permissions.PosTerminal.'), false, '不得使用收银端权限前缀')
assertEqual(
  getAccessKeyPermissionCodes('canViewDailyCloseRecords').join(','),
  P.DailyCloseRecords.View,
  '菜单应只映射到 DailyCloseRecords.View',
)

// 后端 Web 菜单同一入口：路径、标题键、图标、独立权限
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/pos-admin\/daily-closes".*TitleKey = "menu\.dailyCloses".*Icon = "MoneyCollectOutlined".*Permission = Permissions\.DailyCloseRecords\.View/.test(
    navigationSource,
  ),
  true,
  '后端 FullMenu 应登记日结记录入口，且仅凭 DailyCloseRecords.View 可见',
)

// 只有日结记录权限：能进后台、登录落到日结记录页、深链（带 ?id=）被放行
const only = buildAccess(createCurrentUser([P.DailyCloseRecords.View]))
assertEqual(only.canViewDailyCloseRecords, true, '单独授予即可查看日结记录')
assertEqual(only.canViewOperationAudits, false, '不连带员工操作日志')
assertEqual(hasBackendNavigationAccess(only), true, '只有该权限的账号也要能进入后台')
assertEqual(getDefaultWebPath(only), DAILY_CLOSES_PATH, '只有该权限时登录落到日结记录页')
const deepLink = `${DAILY_CLOSES_PATH}?id=a3f29c01-7b4e-4d2a-9c3e-5f1d8e60b274`
assertEqual(resolveAuthorizedWebTarget(deepLink, only), deepLink, '带抽屉参数的深链应被放行')
assertEqual(resolveAuthorizedWebTarget('/pos-admin/operation-logs', only), undefined, '不能顺带放行员工操作日志')
const onlyPreview = buildWebRoleMenuPreview(only, (key) => key, { includeHidden: true })
assertEqual(findNode(onlyPreview, DAILY_CLOSES_PATH)?.visible, true, '角色菜单预览应显示日结记录入口')
assertEqual(findNode(onlyPreview, DAILY_CLOSES_PATH)?.permissionCodes.join(','), P.DailyCloseRecords.View, '预览节点只要求本权限')

// 收银机上的日结权限与员工操作日志权限都不能打开它（独立权限码的意义）
for (const code of [P.PosTerminal.AuditView, P.LegacyEmployeeLogs.View, 'Permissions.PosTerminal.DailyClose.View']) {
  const other = buildAccess(createCurrentUser([code]))
  assertEqual(other.canViewDailyCloseRecords, false, `${code} 不应放行日结记录`)
  assertEqual(
    findNode(buildWebRoleMenuPreview(other, (key) => key, { includeHidden: true }), DAILY_CLOSES_PATH)?.visible,
    false,
    `${code} 的菜单预览不应显示日结记录`,
  )
  assertEqual(resolveAuthorizedWebTarget(DAILY_CLOSES_PATH, other), undefined, `${code} 不能访问日结记录地址`)
}

// 管理员可见
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canViewDailyCloseRecords, true, '管理员可查看日结记录')
assertEqual(
  findNode(buildWebRoleMenuPreview(admin, (key) => key, { includeHidden: true }), DAILY_CLOSES_PATH)?.visible,
  true,
  '管理员菜单预览应显示日结记录',
)

// 组合权限用户沿用原默认入口，不被日结记录抢走落点
const withAudit = buildAccess(createCurrentUser([P.PosTerminal.AuditView, P.DailyCloseRecords.View]))
assertEqual(getDefaultWebPath(withAudit), '/pos-admin/operation-logs', '组合权限用户沿用原默认入口')

console.log('dailyClosesRoute.test: ok')
