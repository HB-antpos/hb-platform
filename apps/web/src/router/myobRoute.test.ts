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
const MYOB_BIND_CODE = 'Myob.Employee.Bind'
const PAYROLL_EDIT_CODE = 'Attendance.Payroll.Edit'

// 路由登记：作为「工时与工资」合并页的标签；懒加载、不保活（授权回跳带 ?myob= 参数，必须按新进入加载）、全局菜单键与独立访问键
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${MYOB_PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记 MYOB 对接路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart) + 40)
assertEqual(routeSource.includes("const PosAdminMyobPage = lazy(() => import('../pages/PosAdmin/Myob'))"), true, '页面必须懒加载，不进首屏包')
assertEqual(routeBlock.includes("title: 'menu.myob'"), true, '路由标题使用全局菜单键')
assertEqual(routeBlock.includes('keepAlive: false'), true, '路由不能 keepAlive')
assertEqual(routeBlock.includes("accessKey: 'canAccessMyob'"), true, '路由使用「三者任一」访问键')
assertEqual(routeBlock.includes('element: <PosAdminMyobPage />'), true, '路由必须渲染 MYOB 对接页')
assertEqual(
  routeSource.indexOf(MYOB_PATH) > routeSource.indexOf('const ATTENDANCE_PAYROLL_NAV_GROUP'),
  true,
  '登记为工时与工资合并页的标签',
)

// 页面入口的请求边界：连接 / 映射接口只认 Myob.Connection.Manage，没有该权限不能请求（避免无谓的 403 提示）；
// 员工绑定页签的公司条改调只读的 /myob/companies，不再依赖 /myob/connections。
const myobIndexSource = readFileSync(join(process.cwd(), 'src/pages/PosAdmin/Myob/index.tsx'), 'utf8')
const bindingTabSource = readFileSync(join(process.cwd(), 'src/pages/PosAdmin/Myob/EmployeeBindingTab.tsx'), 'utf8')
assertEqual(/if \(access\.canManageMyobConnections\) \{\s*void loadConnections\(\)\s*void loadMappings\(\)/.test(myobIndexSource), true, '只有连接管理权限才请求 /connections 与 /store-mappings')
assertEqual(myobIndexSource.includes('getMyobCompanies'), true, '页面入口应请求 /myob/companies')
assertEqual(myobIndexSource.includes('canConnect={access.canManageMyobConnections}'), true, '「连接其他公司文件」只对连接管理权限显示')
assertEqual(bindingTabSource.includes('getMyobConnections'), false, '员工绑定页签不再依赖 /myob/connections')
assertEqual(/\{canConnect \? \(\s*<button[^>]*myob-company-add/.test(bindingTabSource), true, '连接按钮受 canConnect 控制')

// 菜单文案在全局语言包（菜单在页面加载前就要显示）
for (const [language, expected] of [['zh', 'MYOB 对接'], ['en', 'MYOB Integration']] as const) {
  const locale = JSON.parse(readFileSync(join(process.cwd(), `src/i18n/locales/${language}.json`), 'utf8')) as {
    menu: Record<string, string>
  }
  assertEqual(locale.menu.myob, expected, `${language} menu.myob`)
}

// 权限码与后端一致
assertEqual(P.Myob.ConnectionManage, MYOB_CODE, '权限码与后端一致')
assertEqual(P.Myob.EmployeeBind, MYOB_BIND_CODE, '员工绑定权限码与后端一致')
assertEqual(getAccessKeyPermissionCodes('canManageMyobConnections').join(','), MYOB_CODE, '连接与映射页签只映射到 Myob.Connection.Manage')
assertEqual(getAccessKeyPermissionCodes('canBindMyobEmployees').join(','), MYOB_BIND_CODE, '员工绑定页签只映射到 Myob.Employee.Bind')
assertEqual(P.Myob.TimesheetPush, 'Myob.Timesheet.Push', '推送权限码与后端一致')
assertEqual(getAccessKeyPermissionCodes('canPushMyobTimesheet').join(','), 'Myob.Timesheet.Push', '推送只映射到 Myob.Timesheet.Push，不连带编辑工资')
assertEqual(
  getAccessKeyPermissionCodes('canAccessMyob').join(','),
  [MYOB_CODE, MYOB_BIND_CODE, PAYROLL_EDIT_CODE].join(','),
  '菜单入口映射到三个权限任一',
)

// 后端 Web 菜单：MYOB 对接并入「工时与工资」合并页入口，独立权限码任一可见；后端缺叶子时前端整棵会退回本地菜单
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/pos-admin\/payroll".*TitleKey = "menu\.attendancePayroll".*AnyPermissions = new List<string> \{[^}]*Permissions\.Myob\.Connection\.Manage[^}]*Permissions\.Myob\.Employee\.Bind[^}]*\}/.test(navigationSource),
  true,
  '后端 FullMenu 的工时与工资合并页应包含 MYOB 连接 / 员工绑定权限任一；工资编辑不隐含查看，不单独打开菜单',
)
assertEqual(navigationSource.includes('Path = "/pos-admin/myob"'), false, '后端 FullMenu 不再单独登记 MYOB 对接')

// 只有该权限：能进后台、登录落到该页、授权回跳的深链（带 ?myob=）被放行、菜单预览可见
const only = buildAccess(createCurrentUser([MYOB_CODE]))
assertEqual(only.canManageMyobConnections, true, '单独授予即可管理 MYOB 连接')
assertEqual(only.canAccessMyob, true, '连接权限可进入页面')
assertEqual(only.canBindMyobEmployees, false, '连接权限不连带员工绑定')
assertEqual(only.canEditAttendancePayroll, false, '连接权限不连带发薪公司')
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
assertEqual(
  findNode(onlyPreview, MYOB_PATH)?.permissionCodes.join(','),
  [MYOB_CODE, MYOB_BIND_CODE, PAYROLL_EDIT_CODE].join(','),
  '预览节点要求三个权限任一',
)

// 员工绑定 / 编辑工资：各自单独也能进入页面、能进后台、菜单可见；但不连带其他页签的权限
for (const [code, label] of [[MYOB_BIND_CODE, '员工绑定'], [PAYROLL_EDIT_CODE, '发薪公司']] as const) {
  const single = buildAccess(createCurrentUser([code]))
  assertEqual(single.canAccessMyob, true, `${label}权限可进入页面`)
  assertEqual(single.canManageMyobConnections, false, `${label}权限不连带连接管理`)
  assertEqual(hasBackendNavigationAccess(single), true, `${label}权限的账号能进入后台`)
  assertEqual(getDefaultWebPath(single) === MYOB_PATH, true, `${label}权限单独时登录落到 MYOB 页`)
  assertEqual(resolveAuthorizedWebTarget(MYOB_PATH, single), MYOB_PATH, `${label}权限可访问页面地址`)
  const preview = buildWebRoleMenuPreview(single, (key) => key, { includeHidden: true })
  assertEqual(findNode(preview, MYOB_PATH)?.visible, true, `${label}权限的菜单预览显示该入口`)
}
assertEqual(buildAccess(createCurrentUser([MYOB_BIND_CODE])).canBindMyobEmployees, true, '员工绑定页签权限')
assertEqual(buildAccess(createCurrentUser([PAYROLL_EDIT_CODE])).canBindMyobEmployees, false, '编辑工资不连带员工绑定')

// 其余考勤、系统、设备类权限仍不能打开它
for (const code of [
  P.Attendance.AdminView,
  P.Attendance.PayrollView,
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
  assertEqual(other.canAccessMyob, false, `${code} 不应放行 MYOB 页面入口`)
  assertEqual(resolveAuthorizedWebTarget(MYOB_PATH, other), undefined, `${code} 不能访问该页面地址`)
}

const nobody = buildAccess(createCurrentUser([]))
assertEqual(nobody.canManageMyobConnections, false, '无权限不可管理')
assertEqual(nobody.canAccessMyob, false, '无权限不可进入')
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
