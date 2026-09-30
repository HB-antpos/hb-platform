import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from '../utils/access'
import { buildWebRoleMenuPreview, getAccessKeyPermissionCodes, type WebMenuPreviewNode } from '../utils/webMenuPreview'
import { getDefaultWebPath, resolveAuthorizedWebTarget } from '../utils/webPortalAccess'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createCurrentUser(permissions: string[]): CurrentUser {
  return {
    userGUID: 'legacy-employee-logs-user',
    username: 'legacy-employee-logs-user',
    email: 'legacy-employee-logs@example.invalid',
    permissions,
    exactPermissions: permissions,
    roleNames: ['User'],
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

const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
assertEqual(
  routeSource.includes("const PosAdminLegacyEmployeeLogsPage = lazy(() => import('../pages/PosAdmin/LegacyEmployeeLogs'))") &&
    routeSource.includes("path: '/pos-admin/legacy-employee-logs'") &&
    routeSource.includes("title: 'menu.legacyEmployeeLogs'") &&
    routeSource.includes("accessKey: 'canViewLegacyEmployeeLogs'") &&
    routeSource.includes('element: <PosAdminLegacyEmployeeLogsPage />'),
  true,
  '老系统操作日志路由应注册页面和独立权限',
)

// 后端菜单与 Web 路由用同一路径和权限码。
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/pos-admin\/legacy-employee-logs".*Permission = Permissions\.LegacyEmployeeLogs\.View/.test(navigationSource),
  true,
  '后端 FullMenu 应登记同一入口与权限',
)
assertEqual(P.LegacyEmployeeLogs.View.startsWith('Permissions.PosTerminal.'), false, '不得使用收银端权限前缀')
assertEqual(
  getAccessKeyPermissionCodes('canViewLegacyEmployeeLogs').join(','),
  P.LegacyEmployeeLogs.View,
  '菜单预览应映射到老系统操作日志权限',
)

const legacyOnly = buildAccess(createCurrentUser([P.LegacyEmployeeLogs.View]))
assertEqual(legacyOnly.canViewLegacyEmployeeLogs, true, '单独授予即可访问页面')
assertEqual(legacyOnly.canViewOperationAudits, false, '不连带新系统员工操作日志')
assertEqual(getDefaultWebPath(legacyOnly), '/pos-admin/legacy-employee-logs', '只有该权限时登录落到该页')
assertEqual(
  resolveAuthorizedWebTarget('/pos-admin/legacy-employee-logs', legacyOnly),
  '/pos-admin/legacy-employee-logs',
  '可直接回到该页',
)
assertEqual(
  Boolean(findNode(buildWebRoleMenuPreview(legacyOnly, (key) => key, { includeHidden: true }), '/pos-admin/legacy-employee-logs')),
  true,
  '角色菜单预览应包含该入口',
)

// 既有入口的默认落点不因新增规则改变。
const auditOnly = buildAccess(createCurrentUser([P.PosTerminal.AuditView]))
assertEqual(auditOnly.canViewLegacyEmployeeLogs, false, '员工操作日志权限不连带老系统日志')
assertEqual(getDefaultWebPath(auditOnly), '/pos-admin/operation-logs', '员工操作日志用户落点不变')
const both = buildAccess(createCurrentUser([P.PosTerminal.AuditView, P.LegacyEmployeeLogs.View]))
assertEqual(getDefaultWebPath(both), '/pos-admin/operation-logs', '两个权限都有时仍落到既有入口')

console.log('legacyEmployeeLogsRoute.test: ok')
