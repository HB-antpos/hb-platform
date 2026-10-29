import type { CurrentUser } from '../types/auth'
import type { TabItem } from '../types/router'

// routes 初始化 i18n 时会读取浏览器语言；测试只提供最小只读存根，不写入持久化存储。
Object.defineProperty(globalThis, 'localStorage', {
  configurable: true,
  value: { getItem: () => null },
})

const { buildAccess } = await import('../utils/access')
const {
  buildMenus,
  getBreadcrumbItems,
  getSelectedMenuKeys,
  NAV_GROUPS,
  normalizePersistedTabs,
  resolveRoute,
  toTabItem,
} = await import('./routes')
const { getVisibleNavGroupTabs, matchNavGroupTab, shouldMountNavGroupTab } = await import('./navGroupLogic')
const { P } = await import('../types/permissions')

function assert(condition: unknown, message: string) {
  if (!condition) throw new Error(message)
}

function createUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'nav-group-test',
    username: 'tester',
    email: 'tester@example.invalid',
    permissions,
    exactPermissions: permissions,
    roleNames,
    storeNames: [],
  }
}

function collectMenuKeys(items: unknown[] | undefined): string[] {
  return (items ?? []).flatMap((item) => {
    if (!item || typeof item !== 'object') return []
    const node = item as { key?: unknown; children?: unknown[] }
    return [String(node.key), ...collectMenuKeys(node.children)]
  })
}

const groupPaths = NAV_GROUPS.map((group) => group.path)
assert(groupPaths.length === 9, '九个合并页')
assert(new Set(groupPaths).size === groupPaths.length, '合并页地址不重复')

// 每个标签的地址仍能解析，且挂在所属合并页上（菜单高亮、共用页签都靠 navGroup）
for (const group of NAV_GROUPS) {
  const groupRoute = resolveRoute(group.path)
  assert(groupRoute?.meta.navGroup === group.path, `${group.path} 合并页路由必须登记`)
  assert(!groupRoute?.meta.hidden, `${group.path} 合并页是可见菜单项`)
  for (const tab of group.tabs) {
    const route = resolveRoute(tab.path)
    assert(route, `${tab.path} 原地址必须保留`)
    assert(route?.meta.hidden === true, `${tab.path} 不再单独出现在侧栏`)
    assert(route?.meta.navGroup === group.path, `${tab.path} 必须挂在 ${group.path}`)
    assert(route?.meta.activeMenu === group.path, `${tab.path} 侧栏高亮合并页`)
    if (tab.anyAccessKeys?.length) {
      assert(route?.meta.accessKey === undefined, `${tab.path} 声明了任一访问键就不再要求单个 accessKey`)
      assert(route?.meta.anyAccessKeys?.join(',') === tab.anyAccessKeys.join(','), `${tab.path} 沿用任一访问键`)
    } else {
      assert(route?.meta.accessKey === tab.accessKey, `${tab.path} 沿用原访问键`)
    }
  }
}

// 敏感页面不保活；MYOB 对接授权回跳带 ?myob= 参数，必须按新进入重新加载，同样不保活
const sensitivePaths = ['/system/remote-maintenance', '/system/face-cameras', '/system/face-match', '/pos-admin/myob']
for (const group of NAV_GROUPS) {
  for (const tab of group.tabs) {
    assert(tab.keepAlive === !sensitivePaths.includes(tab.path), `${tab.path} 保活设置与原路由一致`)
  }
}

// 菜单：只有现金管理权限 → 只显示「结算与现金」，不显示单页，也不显示别的合并页
const cashOnly = buildAccess(createUser([P.Cash.OverviewView]))
const cashMenuKeys = collectMenuKeys(buildMenus(cashOnly))
assert(cashMenuKeys.includes('/pos-admin/settlement-cash'), '任一标签有权限即显示合并页')
assert(!cashMenuKeys.includes('/pos-admin/store-cash'), '标签页不再单独出现在菜单里')
assert(!cashMenuKeys.includes('/pos-admin/products-pricing'), '无权限的合并页不显示')
const settlement = NAV_GROUPS.find((group) => group.path === '/pos-admin/settlement-cash')!
assert(
  getVisibleNavGroupTabs(settlement, cashOnly).map((tab) => tab.path).join(',') === '/pos-admin/store-cash',
  '只显示有权限的标签（Linkly 仅管理员、日结需独立权限）',
)
// 收银用户条码不再出现在侧栏
const admin = buildAccess(createUser([], ['Admin']))
const adminMenuKeys = collectMenuKeys(buildMenus(admin))
assert(!adminMenuKeys.includes('/pos-admin/cash-register-users'), '收银用户条码不在侧栏')
assert(resolveRoute('/pos-admin/cash-register-users'), '收银用户条码地址保留')
for (const path of groupPaths) {
  assert(adminMenuKeys.includes(path), `管理员菜单包含 ${path}`)
}

// 顶部页签：同组标签共用一个页签，path 跟随当前标签
const usersTab = toTabItem('/system/users', admin)
const rolesTab = toTabItem('/system/roles', admin)
assert(usersTab?.key === '/system/accounts' && rolesTab?.key === '/system/accounts', '同组共用页签 key')
assert(rolesTab?.path === '/system/roles', '页签记住当前标签地址')
assert(usersTab?.keepAlive === true, '合并页外壳按组缓存')
assert(getSelectedMenuKeys('/system/roles', admin).join(',') === '/system/accounts', '侧栏高亮合并页')
const crumbs = getBreadcrumbItems('/system/roles', admin, '账号与权限').map((item) => item.title)
assert(crumbs.length === 3, '面包屑：父菜单 / 合并页 / 当前标签')

// 考勤两个合并页：考勤审核成为排班考勤页内「考勤记录」后的子标签（外层只剩一个标签），MYOB 对接并入工时与工资
const scheduleGroup = NAV_GROUPS.find((group) => group.path === '/pos-admin/attendance')!
const payrollGroup = NAV_GROUPS.find((group) => group.path === '/pos-admin/payroll')!
assert(
  scheduleGroup.tabs.map((tab) => tab.path).join(',') === '/pos-admin/schedule-attendance',
  '排班考勤合并页只剩一个标签，考勤审核已并入页内子标签',
)
assert(
  scheduleGroup.tabs[0].anyAccessKeys?.join(',') === 'canViewAttendanceSchedule,canReviewAttendance',
  '排班考勤标签：排班查看或审核任一即可进入',
)
assert(
  payrollGroup.tabs.map((tab) => tab.path).join(',') === '/pos-admin/attendance-payroll,/pos-admin/myob',
  '工时与工资合并页含工时与工资、MYOB 对接',
)
assert(payrollGroup.tabs.find((tab) => tab.path === '/pos-admin/myob')?.keepAlive === false, 'MYOB 授权回跳要按新进入加载，不保活')
for (const path of ['/pos-admin/attendance-review', '/pos-admin/myob']) {
  assert(!adminMenuKeys.includes(path), `${path} 不再单独出现在侧栏`)
}
// 只有审核权限：仍显示「排班考勤」合并页并能进入唯一的标签（页内只会显示「考勤审核」子标签），不能因合并丢入口
const reviewerOnly = buildAccess(createUser([P.Attendance.ApprovalReviewManagedStore]))
assert(collectMenuKeys(buildMenus(reviewerOnly)).includes('/pos-admin/attendance'), '审核权限显示排班考勤合并页')
assert(
  getVisibleNavGroupTabs(scheduleGroup, reviewerOnly).map((tab) => tab.path).join(',') === '/pos-admin/schedule-attendance',
  '审核权限能进入排班考勤标签',
)
assert(reviewerOnly.canViewAttendanceSchedule === false && reviewerOnly.canReviewAttendance === true, '审核权限不连带排班查看')
assert(Boolean(resolveRoute('/pos-admin/schedule-attendance')), '排班考勤路由登记')
// 只有排班查看权限：能进入，页内看不到审核（页面按 canReviewAttendance 控制）
const scheduleOnly = buildAccess(createUser([P.Attendance.ScheduleViewStore]))
assert(
  getVisibleNavGroupTabs(scheduleGroup, scheduleOnly).map((tab) => tab.path).join(',') === '/pos-admin/schedule-attendance',
  '排班查看权限能进入排班考勤标签',
)
assert(scheduleOnly.canReviewAttendance === false, '排班查看权限不连带审核')
// 两种权限都没有：看不到该合并页
const noAttendance = buildAccess(createUser([P.Cash.OverviewView]))
assert(getVisibleNavGroupTabs(scheduleGroup, noAttendance).length === 0, '无考勤相关权限不显示排班考勤标签')
assert(!collectMenuKeys(buildMenus(noAttendance)).includes('/pos-admin/attendance'), '无考勤相关权限不显示排班考勤菜单')
// 只有 MYOB 权限：只显示「工时与工资」合并页，且只剩 MYOB 对接一个标签
const myobOnly = buildAccess(createUser([P.Myob.ConnectionManage]))
assert(collectMenuKeys(buildMenus(myobOnly)).includes('/pos-admin/payroll'), 'MYOB 权限显示工时与工资合并页')
assert(
  getVisibleNavGroupTabs(payrollGroup, myobOnly).map((tab) => tab.path).join(',') === '/pos-admin/myob',
  'MYOB 权限只看到 MYOB 对接标签',
)
// 面包屑：首个标签与合并页同名时只保留一级；其余标签仍是「父菜单 / 合并页 / 当前标签」
assert(getBreadcrumbItems('/pos-admin/schedule-attendance', admin).length === 2, '同名标签不重复面包屑')
assert(getBreadcrumbItems('/pos-admin/myob', admin).length === 3, 'MYOB 对接面包屑含合并页与标签')
// 旧「考勤审核」地址只做跳转（转到页内 ?tab=review），不生成页签，需要审核权限才放行，侧栏仍高亮排班考勤
const reviewRedirect = resolveRoute('/pos-admin/attendance-review')
assert(reviewRedirect?.meta.hidden === true && reviewRedirect.meta.accessKey === 'canReviewAttendance', '旧考勤审核地址保留为隐藏跳转路由')
assert(reviewRedirect?.meta.activeMenu === '/pos-admin/attendance', '旧考勤审核地址侧栏高亮排班考勤')
assert(toTabItem('/pos-admin/attendance-review', admin) === null, '旧考勤审核地址只跳转，不生成页签')
assert(getSelectedMenuKeys('/pos-admin/myob', admin).join(',') === '/pos-admin/payroll', 'MYOB 对接侧栏高亮工时与工资')

// 本地存储里的旧页签归到合并页，同组只留一个
const persisted: TabItem[] = [
  { key: '/dashboard', path: '/dashboard', routePath: '/dashboard', title: '工作台', affix: true, closable: false, keepAlive: true },
  { key: '/system/users', path: '/system/users', routePath: '/system/users', title: '用户管理', closable: true, keepAlive: true },
  { key: '/system/roles', path: '/system/roles', routePath: '/system/roles', title: '角色管理', closable: true, keepAlive: true },
  { key: '/pos-admin/suppliers', path: '/pos-admin/suppliers', routePath: '/pos-admin/suppliers', title: '供应商管理', closable: true, keepAlive: true },
]
const normalized = normalizePersistedTabs(persisted)
assert(normalized.map((tab) => tab.key).join(',') === '/dashboard,/system/accounts,/pos-admin/suppliers', '旧页签归并且去重')
assert(normalized[1].path === '/system/users', '归并后保留第一个旧页签的地址')

// 标签挂载规则
const devices = NAV_GROUPS.find((group) => group.path === '/system/devices')!
const deviceTab = devices.tabs.find((tab) => tab.path === '/system/device-registration')!
const remoteTab = devices.tabs.find((tab) => tab.path === '/system/remote-maintenance')!
const visited = new Set([deviceTab.path, remoteTab.path])
assert(shouldMountNavGroupTab(deviceTab, { activePath: remoteTab.path, groupInForeground: true, visited }), '保活标签切走后仍挂载')
assert(!shouldMountNavGroupTab(deviceTab, { activePath: remoteTab.path, groupInForeground: true, visited: new Set() }), '没访问过的标签不挂载')
assert(!shouldMountNavGroupTab(remoteTab, { activePath: deviceTab.path, groupInForeground: true, visited }), '敏感标签切走即卸载')
assert(!shouldMountNavGroupTab(remoteTab, { activePath: remoteTab.path, groupInForeground: false, visited }), '离开合并页时敏感标签卸载')
assert(shouldMountNavGroupTab(remoteTab, { activePath: remoteTab.path, groupInForeground: true, visited }), '当前敏感标签挂载')
assert(matchNavGroupTab(devices.tabs, '/system/remote-maintenance/')?.path === remoteTab.path, '地址末尾斜杠也能匹配')
assert(matchNavGroupTab(devices.tabs, '/system/users') === undefined, '其他地址不匹配')

console.log('navGroups.test: ok')
