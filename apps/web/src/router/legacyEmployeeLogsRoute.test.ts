import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from '../utils/access'
import { buildWebRoleMenuPreview, type WebMenuPreviewNode } from '../utils/webMenuPreview'
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
  routeSource.includes("path: '/pos-admin/legacy-employee-logs'") &&
    routeSource.includes('element: <Navigate to="/pos-admin/operation-logs?source=legacy" replace />'),
  true,
  '旧老系统操作日志地址应重定向到合并页的老收银来源',
)
assertEqual(P.LegacyEmployeeLogs.View.startsWith('Permissions.PosTerminal.'), false, '不得使用收银端权限前缀')
assertEqual(P.LegacyEmployeeLogs.Review.startsWith('Permissions.PosTerminal.'), false, '核查权限不得使用收银端权限前缀')

// 只有老收银权限：能进合并页（页内只显示老收银），登录落到合并页，旧地址仍被放行（随后重定向）。
const legacyOnly = buildAccess(createCurrentUser([P.LegacyEmployeeLogs.View]))
assertEqual(legacyOnly.canViewLegacyEmployeeLogs, true, '单独授予即可查看老收银')
assertEqual(legacyOnly.canViewOperationAudits, false, '不连带新收银')
assertEqual(legacyOnly.canViewEmployeeOperationLogs, true, '可进入员工操作日志合并页')
assertEqual(getDefaultWebPath(legacyOnly), '/pos-admin/operation-logs', '只有该权限时登录落到合并页')
assertEqual(
  resolveAuthorizedWebTarget('/pos-admin/legacy-employee-logs', legacyOnly),
  '/pos-admin/legacy-employee-logs',
  '旧地址仍可访问（由路由重定向）',
)
assertEqual(
  Boolean(findNode(buildWebRoleMenuPreview(legacyOnly, (key) => key, { includeHidden: true }), '/pos-admin/operation-logs')),
  true,
  '角色菜单预览应包含员工操作日志入口',
)

const auditOnly = buildAccess(createCurrentUser([P.PosTerminal.AuditView]))
assertEqual(auditOnly.canViewLegacyEmployeeLogs, false, '新收银权限不连带老收银')
assertEqual(auditOnly.canViewEmployeeOperationLogs, true, '新收银权限可进入合并页')
assertEqual(getDefaultWebPath(auditOnly), '/pos-admin/operation-logs', '新收银用户落点不变')
const neither = buildAccess(createCurrentUser([]))
assertEqual(neither.canViewEmployeeOperationLogs, false, '两个权限都没有时不可进入')

console.log('legacyEmployeeLogsRoute.test: ok')
