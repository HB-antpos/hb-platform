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
const { P } = await import('../types/permissions')

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function findNode(nodes: WebMenuPreviewNode[], path: string): WebMenuPreviewNode | undefined {
  for (const node of nodes) {
    if (node.path === path) {
      return node
    }
    const child = node.children ? findNode(node.children, path) : undefined
    if (child) {
      return child
    }
  }
  return undefined
}

const access = buildRolePreviewAccess({
  roleGuid: 'operation-audit-role',
  roleName: 'StoreManager',
  isSuperAdmin: false,
  implicitAllPermissions: false,
  explicitPermissionCodes: [P.PosTerminal.AuditView],
  effectivePermissionCodes: [P.PosTerminal.AuditView],
})

const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')

assertEqual(
  routeSource.includes("const PosAdminEmployeeLogsPage = lazy(() => import('../pages/PosAdmin/EmployeeLogs'))") &&
    routeSource.includes("path: '/pos-admin/operation-logs'") &&
    routeSource.includes("title: 'menu.operationLogs'") &&
    routeSource.includes("accessKey: 'canViewEmployeeOperationLogs'") &&
    routeSource.includes('element: <PosAdminEmployeeLogsPage />'),
  true,
  '员工操作日志路由应注册合并页和合并权限',
)

assertEqual(
  getAccessKeyPermissionCodes('canViewEmployeeOperationLogs').join(','),
  [P.LegacyEmployeeLogs.View, P.PosTerminal.AuditView].join(','),
  '合并页菜单应映射到老收银、新收银两个查看权限（任一可见）',
)

const preview = buildWebRoleMenuPreview(access, (key) => key, { includeHidden: true })
assertEqual(
  Boolean(findNode(preview, '/pos-admin/operation-logs')),
  true,
  '只有新收银权限的角色菜单预览应包含员工操作日志入口',
)
assertEqual(
  Boolean(findNode(preview, '/pos-admin/legacy-employee-logs')),
  false,
  '菜单不再单列老系统操作日志',
)

// 后端 Web 菜单同一入口，任一权限可见。
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/pos-admin\/operation-logs".*AnyPermissions = new List<string> \{ Permissions\.LegacyEmployeeLogs\.View, Permissions\.PosTerminal\.Audit\.View \}/.test(navigationSource),
  true,
  '后端 FullMenu 应把员工操作日志登记为任一权限可见',
)
assertEqual(navigationSource.includes('Path = "/pos-admin/legacy-employee-logs"'), false, '后端菜单不再单列老系统操作日志')

console.log('operationLogsRoute.test: ok')
