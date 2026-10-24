import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import type { WebMenuPreviewNode } from '../utils/webMenuPreview'

const storage = new Map<string, string>()
Object.defineProperty(globalThis, 'localStorage', {
  value: {
    getItem: (key: string) => storage.get(key) ?? null,
    setItem: (key: string, value: string) => storage.set(key, value),
    removeItem: (key: string) => storage.delete(key),
  },
  configurable: true,
})

const { buildRolePreviewAccess } = await import('../utils/roleMenuPreview')
const { buildWebRoleMenuPreview, getAccessKeyPermissionCodes } = await import('../utils/webMenuPreview')
const { getValidLinklySettlementRouteId } = await import('../pages/PosAdmin/LinklySettlements/logic')

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`)
}

function findNode(nodes: WebMenuPreviewNode[], path: string): WebMenuPreviewNode | undefined {
  for (const node of nodes) {
    if (node.path === path) return node
    const child = node.children ? findNode(node.children, path) : undefined
    if (child) return child
  }
  return undefined
}

const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const detailRouteStart = routeSource.indexOf("path: '/pos-admin/linkly-settlements/:id'")
const detailRouteEnd = routeSource.indexOf('\n      },', detailRouteStart)
const detailRoute = routeSource.slice(detailRouteStart, detailRouteEnd)

assert(routeSource.includes("const LinklySettlementsPage = lazy(() => import('../pages/PosAdmin/LinklySettlements'))"), '列表页必须注册独立懒加载路由 import')
assert(routeSource.includes("const LinklySettlementDetailPage = lazy(() => import('../pages/PosAdmin/LinklySettlementDetail'))"), '详情页必须注册独立懒加载路由 import')
assert(routeSource.includes("path: '/pos-admin/linkly-settlements'"), '必须注册列表路由')
assert(routeSource.includes("title: 'menu.linklySettlements'"), '列表路由必须使用 i18n 标题')
assert(detailRouteStart >= 0 && detailRouteEnd > detailRouteStart, '必须注册隐藏详情路由')
assert(detailRoute.includes('hidden: true'), '详情路由必须隐藏')
assert(detailRoute.includes("accessKey: 'isAdmin'"), '详情路由必须 admin-only')
// 列表已并入「结算与现金」合并页，详情页高亮合并页菜单
assert(detailRoute.includes('activeMenu: SETTLEMENT_CASH_NAV_GROUP.path'), '详情路由必须高亮合并页菜单')

const detailPageSource = readFileSync(join(process.cwd(), 'src/pages/PosAdmin/LinklySettlementDetail/index.tsx'), 'utf8')
const largeSettlementId = '9007199254740993'
assertEqual(
  getValidLinklySettlementRouteId(largeSettlementId),
  largeSettlementId,
  '详情路由 BIGINT ID 必须精确往返',
)
assert(!detailPageSource.includes('Number(params.id)'), '详情路由禁止将 ID 转为 Number')
assert(
  detailPageSource.includes('getLinklySettlementRouteIdFromPathname(location.pathname)')
    && detailPageSource.includes('getLinklySettlementDetail(id, currentRequest.signal)'),
  '详情请求必须从 AdminLayout 当前 pathname 读取并校验原始字符串路由 ID',
)

const listRouteStart = routeSource.indexOf("path: '/pos-admin/linkly-settlements'")
const listRouteEnd = routeSource.indexOf('\n      },', listRouteStart)
const listRoute = routeSource.slice(listRouteStart, listRouteEnd)
assert(listRoute.includes("accessKey: 'isAdmin'"), '列表路由必须 admin-only')
assertEqual(getAccessKeyPermissionCodes('isAdmin').length, 0, 'admin-only 菜单不得映射为可授予权限')

const adminAccess = buildRolePreviewAccess({
  roleGuid: 'linkly-admin',
  roleName: 'Admin',
  isSuperAdmin: true,
  implicitAllPermissions: true,
  explicitPermissionCodes: [],
  effectivePermissionCodes: [],
})
const staffAccess = buildRolePreviewAccess({
  roleGuid: 'linkly-staff',
  roleName: 'Cashier',
  isSuperAdmin: false,
  implicitAllPermissions: false,
  explicitPermissionCodes: [],
  effectivePermissionCodes: [],
})

const adminNode = findNode(buildWebRoleMenuPreview(adminAccess, (key) => key, { includeHidden: true }), '/pos-admin/linkly-settlements')
const staffNode = findNode(buildWebRoleMenuPreview(staffAccess, (key) => key, { includeHidden: true }), '/pos-admin/linkly-settlements')
assertEqual(adminNode?.visible, true, '管理员菜单预览应显示 Linkly 结算')
assertEqual(staffNode?.visible, false, '非管理员菜单预览不得显示 Linkly 结算')
assertEqual(adminNode?.edit.canRemove, false, 'admin-only 菜单不得通过角色编辑移除')
assertEqual(staffNode?.edit.canAdd, false, 'admin-only 菜单不得通过角色编辑授予')

console.log('linklySettlementsRoute.test: ok')
