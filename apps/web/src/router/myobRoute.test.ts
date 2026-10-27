import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from '../utils/access'
import { buildWebRoleMenuPreview, getAccessKeyPermissionCodes, type WebMenuPreviewNode } from '../utils/webMenuPreview'
import { getDefaultWebPath, hasBackendNavigationAccess, resolveAuthorizedWebTarget } from '../utils/webPortalAccess'

// MYOB 对接页：路由登记、独立访问键、菜单预览、新权限默认不放行、与后端菜单一致。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createCurrentUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'myob-user',
    username: 'myob-user',
    email: 'myob@example.invalid',
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

const MYOB_PATH = '/pos-admin/myob'
const MYOB_CODE = 'Myob.Connection.Manage'

// 路由登记：作为「工时与工资」合并页的标签；懒加载、不保活（授权回跳带 ?myob= 参数，必须按新进入加载）、全局菜单键与独立访问键
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${MYOB_PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记 MYOB 对接路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart) + 40)
assertEqual(routeSource.includes("const PosAdminMyobPage = lazy(() => import('../pages/PosAdmin/Myob'))"), true, '页面必须懒加载，不进首屏包')
assertEqual(routeBlock.includes("title: 'menu.myob'"), true, '路由标题使用全局菜单键')
assertEqual(routeBlock.includes('keepAlive: false'), true, '路由不能 keepAlive')
assertEqual(routeBlock.includes("accessKey: 'canManageMyobConnections'"), true, '路由使用独立访问键')
assertEqual(routeBlock.includes('element: <PosAdminMyobPage />'), true, '路由必须渲染 MYOB 对接页')
assertEqual(
  routeSource.indexOf(MYOB_PATH) > routeSource.indexOf('const ATTENDANCE_PAYROLL_NAV_GROUP'),
  true,
  '登记为工时与工资合并页的标签',
)

// 菜单文案在全局语言包（菜单在页面加载前就要显示）
for (const [language, expected] of [['zh', 'MYOB 对接'], ['en', 'MYOB Integration']] as const) {
  const locale = JSON.parse(readFileSync(join(process.cwd(), `src/i18n/locales/${language}.json`), 'utf8')) as {
    menu: Record<string, string>
  }
  assertEqual(locale.menu.myob, expected, `${language} menu.myob`)
}

// 权限码与后端一致
assertEqual(P.Myob.ConnectionManage, MYOB_CODE, '权限码与后端一致')
assertEqual(getAccessKeyPermissionCodes('canManageMyobConnections').join(','), MYOB_CODE, '菜单应只映射到 Myob.Connection.Manage')

// 后端 Web 菜单：MYOB 对接并入「工时与工资」合并页入口，独立权限码任一可见；后端缺叶子时前端整棵会退回本地菜单
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/pos-admin\/payroll".*TitleKey = "menu\.attendancePayroll".*AnyPermissions = new List<string> \{[^}]*Permissions\.Myob\.Connection\.Manage[^}]*\}/.test(navigationSource),
  true,
  '后端 FullMenu 的工时与工资合并页应包含 Myob.Connection.Manage',
)
assertEqual(navigationSource.includes('Path = "/pos-admin/myob"'), false, '后端 FullMenu 不再单独登记 MYOB 对接')

// 只有该权限：能进后台、登录落到该页、授权回跳的深链（带 ?myob=）被放行、菜单预览可见
const only = buildAccess(createCurrentUser([MYOB_CODE]))
assertEqual(only.canManageMyobConnections, true, '单独授予即可管理 MYOB 连接')
assertEqual(only.canAccessDashboard, false, '不连带工作台')
assertEqual(hasBackendNavigationAccess(only), true, '只有该权限的账号也要能进入后台')
assertEqual(only.canAccessAdminShell, true, '后台壳可进入')
assertEqual(getDefaultWebPath(only), MYOB_PATH, '只有该权限时登录落到该页')
for (const target of [
  MYOB_PATH,
  `${MYOB_PATH}?myob=connected&connection=abc`,
  `${MYOB_PATH}?myob=error&code=MYOB_AUTHORIZATION_DENIED`,
  // 合并页菜单地址：直接打开时会落到第一个有权限的标签
  '/pos-admin/payroll',
]) {
  assertEqual(resolveAuthorizedWebTarget(target, only), target, `${target} 被放行`)
}
assertEqual(resolveAuthorizedWebTarget('/pos-admin/daily-closes', only), undefined, '不能顺带放行其他后台页面')
const onlyPreview = buildWebRoleMenuPreview(only, (key) => key, { includeHidden: true })
assertEqual(findNode(onlyPreview, MYOB_PATH)?.visible, true, '角色菜单预览应显示该入口')
assertEqual(findNode(onlyPreview, MYOB_PATH)?.permissionCodes.join(','), MYOB_CODE, '预览节点只要求本权限')

// 新权限默认谁都没有：考勤、系统、设备类权限都不能打开它
for (const code of [
  P.Attendance.AdminView,
  P.Attendance.PayrollView,
  P.Attendance.PayrollEdit,
  P.System.ManageSettings,
  P.DeviceRegistration.Manage,
  P.PosTerminal.AuditView,
]) {
  const other = buildAccess(createCurrentUser([code]))
  assertEqual(other.canManageMyobConnections, false, `${code} 不应放行 MYOB 对接`)
  assertEqual(
    findNode(buildWebRoleMenuPreview(other, (key) => key, { includeHidden: true }), MYOB_PATH)?.visible,
    false,
    `${code} 的菜单预览不应显示 MYOB 对接`,
  )
  assertEqual(resolveAuthorizedWebTarget(MYOB_PATH, other), undefined, `${code} 不能访问该页面地址`)
}

const nobody = buildAccess(createCurrentUser([]))
assertEqual(nobody.canManageMyobConnections, false, '无权限不可管理')
assertEqual(resolveAuthorizedWebTarget(MYOB_PATH, nobody), undefined, '无权限不可访问')

// 管理员可见
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canManageMyobConnections, true, '管理员可管理')
assertEqual(findNode(buildWebRoleMenuPreview(admin, (key) => key, { includeHidden: true }), MYOB_PATH)?.visible, true, '管理员菜单预览应显示该入口')
assertEqual(resolveAuthorizedWebTarget(MYOB_PATH, admin), MYOB_PATH, '管理员可访问')

// 组合权限用户沿用原默认入口，不被本页抢走落点
const withDailyCloses = buildAccess(createCurrentUser([P.DailyCloseRecords.View, MYOB_CODE]))
assertEqual(getDefaultWebPath(withDailyCloses), '/pos-admin/daily-closes', '组合权限用户沿用原默认入口')

console.log('myobRoute.test: ok')
