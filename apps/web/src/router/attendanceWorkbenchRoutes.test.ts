import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from '../utils/access'
import { buildWebRoleMenuPreview, getAccessKeyPermissionCodes, type WebMenuPreviewNode } from '../utils/webMenuPreview'

// 考勤审核、工时与工资两个独立菜单项：路由登记、访问键、角色菜单预览与新权限默认不放行。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createCurrentUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'attendance-workbench-user',
    username: 'attendance-workbench-user',
    email: 'attendance-workbench@example.invalid',
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

const REVIEW_PATH = '/pos-admin/attendance-review'
const PAYROLL_PATH = '/pos-admin/attendance-payroll'

const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
// 考勤审核已并入排班考勤页内的子标签：路由层只保留旧地址的隐藏跳转，不再有独立页面组件
{
  const start = routeSource.indexOf(`path: '${REVIEW_PATH}'`)
  assertEqual(start >= 0, true, `必须保留旧地址 ${REVIEW_PATH}`)
  const block = routeSource.slice(start, routeSource.indexOf('element:', start) + 90)
  assertEqual(block.includes("title: 'menu.attendanceReview'"), true, '旧审核地址使用全局菜单键')
  assertEqual(block.includes("accessKey: 'canReviewAttendance'"), true, '旧审核地址仍需审核权限')
  assertEqual(block.includes('hidden: true'), true, '旧审核地址不进侧栏')
  assertEqual(block.includes('<Navigate to="/pos-admin/schedule-attendance?tab=review" replace />'), true, '旧审核地址跳到页内审核子标签')
  assertEqual(routeSource.includes('PosAdminAttendanceReviewPage'), false, '路由层不再单独加载审核页')
}

// 排班考勤页：「考勤审核」子标签紧跟在「考勤记录」后面，按审核权限显示，首次切到才懒加载
{
  const pageSource = readFileSync(join(process.cwd(), 'src/pages/PosAdmin/ScheduleAttendance/index.tsx'), 'utf8')
  assertEqual(pageSource.includes("lazy(() => import('../AttendanceReview'))"), true, '审核页在子标签内懒加载')
  assertEqual(pageSource.includes('const reviewTabItem = canReviewTab ? {'), true, '审核子标签按 canReviewAttendance 显示')
  const compose = pageSource.slice(pageSource.indexOf('const tabItems = ['))
  const recordsIdx = compose.indexOf('afterRecordsKeys.has(item.key))')
  const reviewIdx = compose.indexOf('reviewTabItem ? [reviewTabItem]')
  const restIdx = compose.indexOf('!afterRecordsKeys.has(item.key))')
  assertEqual(recordsIdx >= 0 && reviewIdx > recordsIdx && restIdx > reviewIdx, true, '标签顺序：周排班、考勤记录、考勤审核、其余')
  assertEqual(pageSource.includes("set('tab', REVIEW_TAB_QUERY)"), true, '切到审核标签要写入地址 ?tab=review')
  assertEqual(
    pageSource.includes("location.pathname === SCHEDULE_ATTENDANCE_PATH && searchParams.get('tab')"),
    true,
    '保活期间只认本页地址上的 ?tab=review',
  )
}

for (const [path, title, accessKey, component] of [
  [PAYROLL_PATH, 'menu.attendancePayroll', 'canViewAttendanceHours', 'PosAdminAttendancePayrollPage'],
] as const) {
  const start = routeSource.indexOf(`path: '${path}'`)
  assertEqual(start >= 0, true, `必须登记 ${path}`)
  const block = routeSource.slice(start, routeSource.indexOf('element:', start) + 60)
  assertEqual(block.includes(`title: '${title}'`), true, `${path} 使用全局菜单键`)
  assertEqual(block.includes(`accessKey: '${accessKey}'`), true, `${path} 访问键`)
  assertEqual(block.includes('keepAlive: true'), true, `${path} 保活`)
  assertEqual(block.includes(`element: <${component} />`), true, `${path} 渲染对应页面`)
  assertEqual(routeSource.includes(`const ${component} = lazy(() => import(`), true, `${path} 页面必须懒加载`)
}

// 菜单文案在全局语言包（菜单在页面加载前就要显示）
for (const language of ['zh', 'en']) {
  const locale = JSON.parse(readFileSync(join(process.cwd(), `src/i18n/locales/${language}.json`), 'utf8')) as { menu: Record<string, string> }
  assertEqual(typeof locale.menu.attendanceReview, 'string', `${language} 缺 menu.attendanceReview`)
  assertEqual(typeof locale.menu.attendancePayroll, 'string', `${language} 缺 menu.attendancePayroll`)
}

// 后端 Web 菜单树同样登记两个入口，权限与前端访问键一致（否则后端菜单缺叶子，前端整棵退回本地菜单）
const navigationSource = readFileSync(join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'), 'utf8')
// 考勤审核并入「排班考勤」合并页、工时与工资并入同一合并页的 MYOB 标签：后端只登记两个合并页入口，旧叶子不再单独出现
assertEqual(
  /Path = "\/pos-admin\/attendance",.*TitleKey = "menu\.scheduleAttendance".*Icon = "CalendarOutlined".*AnyPermissions = new List<string> \{ Permissions\.Attendance\.Schedule\.ViewStore, Permissions\.Attendance\.Approval\.ReviewManagedStore \}/.test(navigationSource),
  true,
  '后端 FullMenu 应登记排班考勤合并页，排班查看或审核任一可见（审核只认 Approval.ReviewManagedStore）',
)
assertEqual(
  /Path = "\/pos-admin\/payroll",.*TitleKey = "menu\.attendancePayroll".*Icon = "AccountBookOutlined".*AnyPermissions = new List<string> \{ Permissions\.Attendance\.Punch\.ViewManagedStore, Permissions\.Attendance\.Payroll\.View, Permissions\.Myob\.Connection\.Manage, Permissions\.Myob\.Employee\.Bind, Permissions\.Myob\.Payslip\.ViewManagedStore, Permissions\.Myob\.Payslip\.ViewAllStores \}/.test(navigationSource),
  true,
  '后端 FullMenu 应登记工时与工资合并页，打卡查看、工资查看或 MYOB 对接（连接 / 员工绑定）任一可见；工资编辑不隐含查看',
)
for (const retired of ['/pos-admin/schedule-attendance', REVIEW_PATH, PAYROLL_PATH, '/pos-admin/myob']) {
  assertEqual(navigationSource.includes(`Path = "${retired}"`), false, `后端 FullMenu 不应再单独登记 ${retired}`)
}

// 新权限码与规格一致
assertEqual(P.Attendance.ApprovalReviewSelf, 'Attendance.Approval.ReviewSelf', '本人审核权限码')
assertEqual(P.Attendance.PayrollView, 'Attendance.Payroll.View', '工资查看权限码')
assertEqual(P.Attendance.PayrollEdit, 'Attendance.Payroll.Edit', '工资编辑权限码')
assertEqual(P.Attendance.PayrollUnlock, 'Attendance.Payroll.Unlock', '工资解锁权限码')

// 审核员：能进审核页；不能看工资；不能审本人
const reviewer = buildAccess(createCurrentUser([P.Attendance.ApprovalReviewManagedStore]))
assertEqual(reviewer.canReviewAttendance, true, '审核权限可进审核工作台')
assertEqual(reviewer.canReviewOwnAttendance, false, 'ReviewSelf 默认没有')
assertEqual(reviewer.canViewAttendancePayroll, false, '审核权限不连带工资')
assertEqual(reviewer.canViewAttendanceHours, false, '审核权限不连带工时统计')

// 打卡查看：能进工时与工资页（只有工时统计），看不到工资
const punchViewer = buildAccess(createCurrentUser([P.Attendance.PunchViewManagedStore]))
assertEqual(punchViewer.canViewAttendanceHours, true, '打卡查看权限可看工时统计')
assertEqual(punchViewer.canViewAttendancePayroll, false, '打卡查看权限不能看工资表')

// Attendance.Admin.View 不自动获得工资与本人审核（新权限默认谁都没有，手动授权）
const attendanceAdmin = buildAccess(createCurrentUser([P.Attendance.AdminView]))
assertEqual(attendanceAdmin.canViewAttendancePayroll, false, 'Admin.View 不连带工资查看')
assertEqual(attendanceAdmin.canEditAttendancePayroll, false, 'Admin.View 不连带工资编辑')
assertEqual(attendanceAdmin.canReviewOwnAttendance, false, 'Admin.View 不连带本人审核')

// 工资三项各自独立
const payrollViewer = buildAccess(createCurrentUser([P.Attendance.PayrollView]))
assertEqual(payrollViewer.canViewAttendancePayroll, true, 'Payroll.View 可看工资')
assertEqual(payrollViewer.canViewAttendanceHours, true, 'Payroll.View 可进工时与工资页')
assertEqual(payrollViewer.canEditAttendancePayroll, false, 'View 不含编辑')
assertEqual(payrollViewer.canUnlockAttendancePayroll, false, 'View 不含解锁')
const payrollEditor = buildAccess(createCurrentUser([P.Attendance.PayrollView, P.Attendance.PayrollEdit]))
assertEqual(payrollEditor.canEditAttendancePayroll, true, 'Edit 可改时薪、备注、提交')
assertEqual(payrollEditor.canUnlockAttendancePayroll, false, 'Edit 不含解锁')
assertEqual(buildAccess(createCurrentUser([P.Attendance.PayrollUnlock])).canUnlockAttendancePayroll, true, 'Unlock 单独授予')

// 角色菜单预览
assertEqual(getAccessKeyPermissionCodes('canViewAttendanceHours')[0], P.Attendance.PunchViewManagedStore, '菜单授予时补打卡查看')
const preview = buildWebRoleMenuPreview(payrollViewer, (key) => key, { includeHidden: true })
assertEqual(findNode(preview, PAYROLL_PATH)?.visible, true, '工资查看者菜单预览显示工时与工资')
assertEqual(findNode(preview, REVIEW_PATH)?.visible, false, '工资查看者菜单预览不显示审核')

const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canReviewOwnAttendance && admin.canViewAttendancePayroll && admin.canUnlockAttendancePayroll, true, '管理员全部可用')

// 侧栏待审角标：服务层动态加载，不进首屏
const layoutSource = readFileSync(join(process.cwd(), 'src/layout/AdminLayout.tsx'), 'utf8')
const badgeSource = readFileSync(join(process.cwd(), 'src/layout/attendancePendingBadge.tsx'), 'utf8')
assertEqual(layoutSource.includes('useAttendancePendingCount(access.canReviewAttendance'), true, '只有审核权限才拉待审数')
assertEqual(badgeSource.includes("import('../services/attendancePayrollService')"), true, '角标服务必须动态加载')
assertEqual(/^import[^\n]*services\//m.test(badgeSource), false, '角标文件不能静态引入服务层')

// 联调后服务层直连后端，样例夹具只给测试用
const serviceSource = readFileSync(join(process.cwd(), 'src/services/attendancePayrollService.ts'), 'utf8')
assertEqual(/mock|fixture/i.test(serviceSource), false, '服务层不能再引用 mock / 测试夹具')
assertEqual(serviceSource.includes('isSelfReview: true'), true, '本人审批记录按 isSelfReview=true 筛选')
assertEqual(serviceSource.includes("reviewStatus: 'Pending'"), true, '菜单角标按状态查待审，不走会触发对账的 pending 接口')

console.log('attendanceWorkbenchRoutes.test: ok')
