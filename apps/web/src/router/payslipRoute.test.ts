import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from '../utils/access'
import { buildWebRoleMenuPreview, getAccessKeyPermissionCodes, type WebMenuPreviewNode } from '../utils/webMenuPreview'
import { getDefaultWebPath, hasBackendNavigationAccess, resolveAuthorizedWebTarget } from '../utils/webPortalAccess'

// 工资单页（「工时与工资」页面里与「MYOB 对接」并列的页签）：路由登记、独立访问键、菜单预览、新权限默认不放行、与后端一致。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createCurrentUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'payslip-user',
    username: 'payslip-user',
    email: 'payslip@example.invalid',
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

const PATH = '/pos-admin/payslips'
const MANAGED = 'Myob.Payslip.ViewManagedStore'
const ALL = 'Myob.Payslip.ViewAllStores'
const MYOB_CONNECT = 'Myob.Connection.Manage'
const MYOB_BIND = 'Myob.Employee.Bind'
const PAYROLL_EDIT = 'Attendance.Payroll.Edit'
const PAYROLL_VIEW = 'Attendance.Payroll.View'

// ───────────────────────── 路由登记 ─────────────────────────

const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记工资单路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart) + 40)
assertEqual(routeSource.includes("const PosAdminPayslipsPage = lazy(() => import('../pages/PosAdmin/Payslips'))"), true, '页面必须懒加载，不进首屏包')
assertEqual(routeBlock.includes("title: 'menu.payslips'"), true, '路由标题使用全局菜单键')
assertEqual(routeBlock.includes('keepAlive: false'), true, '含薪资的页面不能 keepAlive（离开即丢掉内存里的工资数据）')
assertEqual(routeBlock.includes("accessKey: 'canViewMyobPayslips'"), true, '路由使用工资单独立访问键')
assertEqual(routeBlock.includes('element: <PosAdminPayslipsPage />'), true, '路由必须渲染工资单页')
const groupStart = routeSource.indexOf('const ATTENDANCE_PAYROLL_NAV_GROUP')
assertEqual(routeSource.indexOf(PATH) > groupStart, true, '登记为「工时与工资」页面组里的页签')
// 页签顺序：工时与工资 → 工资单 → MYOB 对接（与设计图一致）
const hoursAt = routeSource.indexOf("path: '/pos-admin/attendance-payroll'", groupStart)
const myobAt = routeSource.indexOf("path: '/pos-admin/myob'", groupStart)
assertEqual(hoursAt < routeSource.indexOf(PATH, groupStart) && routeSource.indexOf(PATH, groupStart) < myobAt, true, '页签顺序：工时与工资、工资单、MYOB 对接')

// 菜单文案在全局语言包（菜单在页面加载前就要显示）
for (const [language, expected] of [['zh', '工资单'], ['en', 'Payslips']] as const) {
  const locale = JSON.parse(readFileSync(join(process.cwd(), `src/i18n/locales/${language}.json`), 'utf8')) as {
    menu: Record<string, string>
  }
  assertEqual(locale.menu.payslips, expected, `${language} menu.payslips`)
}

// ───────────────────────── 权限码 ─────────────────────────

const backendPermissions = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Shared/Constants/Permissions.cs'),
  'utf8',
)
assertEqual(P.Myob.PayslipViewManagedStore, MANAGED, '管辖分店权限码')
assertEqual(P.Myob.PayslipViewAllStores, ALL, '全部分店权限码')
assertEqual(backendPermissions.includes(`"${MANAGED}"`), true, '后端必须有同名管辖分店权限码')
assertEqual(backendPermissions.includes(`"${ALL}"`), true, '后端必须有同名全部分店权限码')
assertEqual(getAccessKeyPermissionCodes('canViewMyobPayslips').join(','), [MANAGED, ALL].join(','), '页签映射到两个权限码任一')
assertEqual(getAccessKeyPermissionCodes('canViewAllMyobPayslips').join(','), ALL, '全部分店口径只映射到 ViewAllStores')

// 后端 Web 菜单：工时与工资合并页入口包含两个工资单权限码，只有这个权限的账号也能拿到后台菜单
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/pos-admin\/payroll".*AnyPermissions = new List<string> \{[^}]*Permissions\.Myob\.Payslip\.ViewManagedStore[^}]*Permissions\.Myob\.Payslip\.ViewAllStores[^}]*\}/.test(navigationSource),
  true,
  '后端 FullMenu 的工时与工资合并页应包含两个工资单权限码',
)

// ───────────────────────── 只有「管辖分店」权限 ─────────────────────────

const managed = buildAccess(createCurrentUser([MANAGED]))
assertEqual(managed.canViewMyobPayslips, true, '管辖分店权限可看工资单页签')
assertEqual(managed.canViewAllMyobPayslips, false, '管辖分店权限不是全部分店口径（不显示未归属入口）')
assertEqual(managed.canAccessMyob, false, '工资单权限不连带 MYOB 对接页')
assertEqual(managed.canManageMyobConnections, false, '不连带连接管理')
assertEqual(managed.canBindMyobEmployees, false, '不连带员工绑定')
assertEqual(managed.canEditAttendancePayroll, false, '不连带发薪公司 / 编辑工资')
assertEqual(managed.canAccessDashboard, false, '不连带工作台')
assertEqual(hasBackendNavigationAccess(managed), true, '只有该权限的账号也要能进入后台')
assertEqual(getDefaultWebPath(managed), PATH, '只有该权限时登录落到工资单页，而不是 MYOB 页')
for (const target of [PATH, '/pos-admin/payroll']) {
  assertEqual(resolveAuthorizedWebTarget(target, managed), target, `${target} 被放行`)
}
assertEqual(resolveAuthorizedWebTarget('/pos-admin/myob', managed), undefined, '不能顺带打开 MYOB 对接页')
assertEqual(resolveAuthorizedWebTarget('/pos-admin/daily-closes', managed), undefined, '不能顺带放行其他后台页面')
const managedPreview = buildWebRoleMenuPreview(managed, (key) => key, { includeHidden: true })
assertEqual(findNode(managedPreview, PATH)?.visible, true, '角色菜单预览应显示工资单页签')
assertEqual(findNode(managedPreview, '/pos-admin/myob')?.visible, false, '菜单预览不显示 MYOB 对接')
assertEqual(findNode(managedPreview, PATH)?.permissionCodes.join(','), [MANAGED, ALL].join(','), '预览节点要求两个权限码任一')

// ───────────────────────── 「全部分店」权限 ─────────────────────────

const all = buildAccess(createCurrentUser([ALL]))
assertEqual(all.canViewMyobPayslips, true, '全部分店权限可看工资单页签（隐含查看）')
assertEqual(all.canViewAllMyobPayslips, true, '全部分店口径')
assertEqual(getDefaultWebPath(all), PATH, '只有全部分店权限时也落到工资单页')

// ───────────────────────── 其它权限不连带工资单 ─────────────────────────

for (const code of [MYOB_CONNECT, MYOB_BIND, PAYROLL_EDIT, PAYROLL_VIEW, P.Attendance.AdminView, P.System.ManageSettings, P.PosTerminal.AuditView]) {
  const other = buildAccess(createCurrentUser([code]))
  assertEqual(other.canViewMyobPayslips, false, `${code} 不应放行工资单（含薪资的敏感信息）`)
  assertEqual(other.canViewAllMyobPayslips, false, `${code} 不应放行全部分店口径`)
  assertEqual(resolveAuthorizedWebTarget(PATH, other), undefined, `${code} 不能访问工资单地址`)
  assertEqual(
    findNode(buildWebRoleMenuPreview(other, (key) => key, { includeHidden: true }), PATH)?.visible,
    false,
    `${code} 的菜单预览不应显示工资单`,
  )
}

// 反过来：工资单权限也不放行 MYOB 的其它页签。
assertEqual(managed.canAccessMyob, false, '管辖分店工资单权限不放行 MYOB 对接页')
assertEqual(all.canAccessMyob, false, '全部分店工资单权限不放行 MYOB 对接页')

// ───────────────────────── 无权限 / 管理员 / 组合 ─────────────────────────

const nobody = buildAccess(createCurrentUser([]))
assertEqual(nobody.canViewMyobPayslips, false, '无权限不可看')
assertEqual(resolveAuthorizedWebTarget(PATH, nobody), undefined, '无权限不可访问')

const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canViewMyobPayslips, true, '管理员可看')
assertEqual(admin.canViewAllMyobPayslips, true, '管理员是全部分店口径')
assertEqual(findNode(buildWebRoleMenuPreview(admin, (key) => key, { includeHidden: true }), PATH)?.visible, true, '管理员菜单预览应显示')
assertEqual(resolveAuthorizedWebTarget(PATH, admin), PATH, '管理员可访问')

// 组合权限用户沿用原默认入口，不被本页抢走落点
assertEqual(getDefaultWebPath(buildAccess(createCurrentUser([P.DailyCloseRecords.View, MANAGED]))), '/pos-admin/daily-closes', '组合权限用户沿用原默认入口')
assertEqual(getDefaultWebPath(buildAccess(createCurrentUser([MYOB_CONNECT, MANAGED]))), '/pos-admin/myob', '同时有 MYOB 权限时默认落到 MYOB 页（原行为不变）')

// ───────────────────────── 页面入口的隐私约束 ─────────────────────────

const pageSource = readFileSync(join(process.cwd(), 'src/pages/PosAdmin/Payslips/index.tsx'), 'utf8')
const drawerSource = readFileSync(join(process.cwd(), 'src/pages/PosAdmin/Payslips/PayslipDrawer.tsx'), 'utf8')
for (const [name, source] of [['列表页', pageSource], ['抽屉', drawerSource]] as const) {
  assertEqual(/localStorage|sessionStorage|indexedDB/i.test(source), false, `${name}不得把工资数据写进浏览器存储`)
  assertEqual(/console\.(log|info|debug)/.test(source), false, `${name}不得把工资数据打到控制台`)
}
assertEqual(/exportCsv|buildCsv|download/i.test(pageSource + drawerSource), false, '本期不提供导出')
assertEqual(drawerSource.includes('getMyobPayslipHours'), true, '抽屉的我方工时走独立接口')
assertEqual(/useEffect\([^)]*getMyobPayslipHours/s.test(drawerSource), false, '我方工时必须点击后才请求，不能在 effect 里自动加载')
assertEqual(drawerSource.includes('payslips.drawer.disclaimerZh') && drawerSource.includes('payslips.drawer.disclaimerEn'), true, '抽屉页脚必须有「以 MYOB 为准」中英文声明')

console.log('payslipRoute tests passed')
