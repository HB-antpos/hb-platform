import type { CurrentUser } from '../types/auth'

// routes 初始化 i18n 时会读取浏览器语言；测试只提供最小只读存根，不写入持久化存储。
Object.defineProperty(globalThis, 'localStorage', {
  configurable: true,
  value: { getItem: () => null },
})

const { buildAccess } = await import('../utils/access')
const { buildMenus, getCurrentRoute, NAV_GROUPS, resolveRoute } = await import('./routes')
const { getVisibleNavGroupTabs, shouldMountNavGroupTab } = await import('./navGroupLogic')

function createUser(roleNames: string[]): CurrentUser {
  return {
    userGUID: 'remote-maintenance-test',
    username: 'tester',
    email: 'tester@example.com',
    permissions: [],
    roleNames,
    storeNames: [],
  }
}

function assert(condition: unknown, message: string) {
  if (!condition) throw new Error(message)
}

function includesMenuPath(items: unknown[] | undefined, path: string): boolean {
  return Boolean(items?.some((item) => {
    if (!item || typeof item !== 'object') return false
    const node = item as { key?: unknown; children?: unknown[] }
    return node.key === path || includesMenuPath(node.children, path)
  }))
}

const route = resolveRoute('/system/remote-maintenance')
assert(route?.meta.accessKey === 'isAdmin', 'remote maintenance route must be admin-only')
// 远程维护并入「设备与远程」合并页：外壳按组缓存，凭据页标签本身不保活，切走或离开合并页即卸载。
assert(route?.meta.navGroup === '/system/devices', 'remote maintenance must live in the devices & remote group')
const devicesGroup = NAV_GROUPS.find((group) => group.path === '/system/devices')
const remoteTab = devicesGroup?.tabs.find((tab) => tab.path === '/system/remote-maintenance')
assert(remoteTab?.keepAlive === false, 'credentials tab must not be kept alive')
assert(remoteTab && shouldMountNavGroupTab(remoteTab, {
  activePath: '/system/device-registration',
  groupInForeground: true,
  visited: new Set(['/system/remote-maintenance']),
}) === false, 'credentials page must unmount when switching to another tab')
assert(remoteTab && shouldMountNavGroupTab(remoteTab, {
  activePath: '/system/remote-maintenance',
  groupInForeground: false,
  visited: new Set(['/system/remote-maintenance']),
}) === false, 'credentials page must unmount when navigation leaves the group')

const adminAccess = buildAccess(createUser(['Admin']))
const regularAccess = buildAccess(createUser(['StoreManager']))
assert(getCurrentRoute('/system/remote-maintenance', adminAccess)?.element === route?.element, 'admin should resolve the remote maintenance page')
assert(getCurrentRoute('/system/remote-maintenance', regularAccess)?.element !== route?.element, 'non-admin should resolve the forbidden page')
assert(includesMenuPath(buildMenus(adminAccess), '/system/devices'), 'admin menu should include the devices & remote group')
assert(!includesMenuPath(buildMenus(adminAccess), '/system/remote-maintenance'), 'remote maintenance is a tab, not a separate menu item')
assert(devicesGroup && getVisibleNavGroupTabs(devicesGroup, adminAccess).some((tab) => tab.path === '/system/remote-maintenance'), 'admin should see the remote maintenance tab')
assert(devicesGroup && !getVisibleNavGroupTabs(devicesGroup, regularAccess).some((tab) => tab.path === '/system/remote-maintenance'), 'non-admin should not see the remote maintenance tab')

console.log('remoteMaintenanceRoute.test: ok')
