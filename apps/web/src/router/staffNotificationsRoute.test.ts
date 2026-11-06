import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'

// routes 初始化 i18n 时会读取浏览器语言；测试只提供最小只读存根，不写入持久化存储。
Object.defineProperty(globalThis, 'localStorage', {
  configurable: true,
  value: { getItem: () => null },
})

const { buildAccess } = await import('../utils/access')
const { NAV_GROUPS, buildMenus, resolveRoute } = await import('./routes')
const { getVisibleNavGroupTabs } = await import('./navGroupLogic')
const { P } = await import('../types/permissions')
const { buildWebRoleMenuPreview, getAccessKeyPermissionCodes } = await import('../utils/webMenuPreview')
type WebMenuPreviewNode = import('../utils/webMenuPreview').WebMenuPreviewNode

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'staff-notifications-user',
    username: 'tester',
    email: 'tester@example.invalid',
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

function collectMenuKeys(items: unknown[] | undefined): string[] {
  return (items ?? []).flatMap((item) => {
    if (!item || typeof item !== 'object') return []
    const node = item as { key?: unknown; children?: unknown[] }
    return [String(node.key), ...collectMenuKeys(node.children)]
  })
}

const PATH = '/system/staff-notifications'

// 路由登记：懒加载（不进首屏包）、挂在「账号与权限」合并页、与重置密码同源的访问键。
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
assertEqual(
  routeSource.includes("const StaffNotificationsPage = lazy(() => import('../pages/System/StaffNotifications'))"),
  true,
  '员工通知页必须懒加载',
)
const accounts = NAV_GROUPS.find((group) => group.path === '/system/accounts')
const tab = accounts?.tabs.find((candidate) => candidate.path === PATH)
assertEqual(Boolean(tab), true, '员工通知是「账号与权限」合并页的标签')
assertEqual(tab?.title, 'menu.staffNotifications', '标签标题使用全局菜单键')
assertEqual(tab?.accessKey, 'canResetUserPassword', '访问键与「重置密码」同源')
assertEqual(tab?.keepAlive, true, '保活：切走再切回保留筛选与分页')
assertEqual(
  accounts?.tabs.map((candidate) => candidate.path).join(','),
  '/system/users,/system/employee-profiles,/system/roles,/system/permissions,/system/staff-notifications',
  '员工通知排在账号与权限各标签之后',
)
assertEqual(resolveRoute(PATH)?.meta.navGroup, '/system/accounts', '地址能解析且挂在合并页上')
assertEqual(resolveRoute(PATH)?.meta.hidden, true, '不单独出现在侧栏')

// 访问键：Users.ResetPassword 或管理员；其它用户类权限不连带。
assertEqual(P.Users.ResetPassword, 'Users.ResetPassword', '权限码与后端一致')
assertEqual(getAccessKeyPermissionCodes('canResetUserPassword').join(','), P.Users.ResetPassword, '菜单预览只映射到 Users.ResetPassword')
const resetOnly = buildAccess(createUser([P.Users.ResetPassword]))
assertEqual(resetOnly.canResetUserPassword, true, '有重置密码权限即可发送员工通知')
assertEqual(resetOnly.canReadUserConsole, false, '不连带用户管理页权限')
assertEqual(buildAccess(createUser([P.Users.Edit, P.Users.ViewWebConsole, P.Users.View])).canResetUserPassword, false, '改用户资料、看用户列表都不放行发通知')
assertEqual(buildAccess(null).canResetUserPassword, false, '未登录没有权限')
assertEqual(buildAccess(createUser([], ['Admin'])).canResetUserPassword, true, '管理员默认放行')

// 有权限的账号能看到这个标签，没有的看不到。
assertEqual(
  getVisibleNavGroupTabs(accounts!, resetOnly).map((candidate) => candidate.path).join(','),
  PATH,
  '只有重置密码权限时合并页里只有员工通知一个标签',
)
const usersOnly = buildAccess(createUser([P.Users.ViewWebConsole]))
assertEqual(getVisibleNavGroupTabs(accounts!, usersOnly).some((candidate) => candidate.path === PATH), false, '没有重置密码权限看不到员工通知')

// 侧栏：有权限的账号能看到「账号与权限」合并页入口。
const resetMenuKeys = collectMenuKeys(buildMenus(resetOnly) as unknown[] | undefined)
assertEqual(resetMenuKeys.includes('/system/accounts'), true, '有权限时合并页菜单项可见')
const noneMenuKeys = collectMenuKeys(buildMenus(buildAccess(createUser([]))) as unknown[] | undefined)
assertEqual(noneMenuKeys.includes('/system/accounts'), false, '没有任何账号类权限时合并页菜单项不可见')

// 角色菜单预览：员工通知挂在「系统 / 账号与权限」下，随 Users.ResetPassword 显示。
const preview = buildWebRoleMenuPreview(resetOnly, (key) => key, { includeHidden: true })
assertEqual(findNode(preview, PATH)?.visible, true, '角色菜单预览应显示员工通知')
assertEqual(findNode(preview, PATH)?.permissionCodes.join(','), P.Users.ResetPassword, '预览节点只要求 Users.ResetPassword')
assertEqual(findNode(preview, '/system/accounts')?.children?.some((child) => child.path === PATH), true, '菜单挂在账号与权限合并页下')
assertEqual(findNode(preview, '/system/users')?.visible, false, '不连带用户管理')
assertEqual(findNode(buildWebRoleMenuPreview(usersOnly, (key) => key, { includeHidden: true }), PATH)?.visible, false, '没有重置密码权限的预览不显示员工通知')

console.log('staffNotificationsRoute tests passed')
