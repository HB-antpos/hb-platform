import type { CurrentUser } from '../../types/auth'

// routes 初始化 i18n 时会读取浏览器语言；测试只提供最小只读存根，不写入持久化存储。
Object.defineProperty(globalThis, 'localStorage', {
  configurable: true,
  value: { getItem: () => null },
})

const { buildAccess } = await import('../../utils/access')
const { buildMenus } = await import('../../router/routes')
const { P } = await import('../../types/permissions')
const { DEFAULT_QUICK_LINK_KEYS, defaultPinnedKeys, flattenMenuLinks, resolvePinnedKeys } = await import('./quickLinks')

function assert(condition: unknown, message: string) {
  if (!condition) throw new Error(message)
}

function createUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'quick-links-test',
    username: 'tester',
    email: 'tester@example.invalid',
    permissions,
    exactPermissions: permissions,
    roleNames,
    storeNames: [],
  }
}

// 用真实侧栏菜单校验：默认常用导航写的都是真实存在的菜单路径。
// 菜单将来改了路径，这里会第一时间失败，而不是让工作台默认项悄悄失效。
const admin = buildAccess(createUser([], ['Admin']))
const adminEntries = flattenMenuLinks(buildMenus(admin))
const adminKeys = new Set(adminEntries.map((entry) => entry.key))

assert(adminEntries.length > 0, '管理员菜单应能拍平出可跳转页面')
assert(!adminKeys.has('/dashboard'), '工作台自己不在候选里')
for (const key of DEFAULT_QUICK_LINK_KEYS) {
  assert(adminKeys.has(key), `默认常用导航 ${key} 不在管理员的真实菜单里（菜单路径是否改了？）`)
}
assert(
  defaultPinnedKeys(adminEntries).join() === DEFAULT_QUICK_LINK_KEYS.join(),
  '管理员的默认常用导航应完全按默认顺序',
)
assert(adminEntries.every((entry) => entry.title.length > 0 && entry.module.length > 0), '每个页面都应有标题和所属模块')

// 权限很少的账号：默认只取有权限的页面，且不会凭空出现无权限页面
const cashOnly = buildAccess(createUser([P.Cash.OverviewView]))
const cashEntries = flattenMenuLinks(buildMenus(cashOnly))
const cashKeys = new Set(cashEntries.map((entry) => entry.key))
const cashDefaults = defaultPinnedKeys(cashEntries)
assert(cashDefaults.length > 0, '有可访问页面时默认常用导航不应为空')
assert(cashDefaults.every((key) => cashKeys.has(key)), '默认常用导航只含有权限的页面')
assert(cashDefaults.includes('/pos-admin/settlement-cash'), '有「结算与现金」权限时默认包含它')
assert(!cashDefaults.includes('/pos-admin/payroll'), '没有工时权限就不应出现「工时与工资」')

// 用户曾经固定、之后失去权限的页面：显示时被过滤，不报错
const stale = resolvePinnedKeys(cashEntries, ['/pos-admin/payroll', '/pos-admin/settlement-cash'])
assert(stale.join() === '/pos-admin/settlement-cash', '失去权限的已固定页面不显示')

console.log('quickLinksMenu tests passed')
