import type { WebMenuPreviewNode } from '../utils/webMenuPreview'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

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

const translate = (key: string, fallback?: string) => fallback ?? key

const viewAccess = buildRolePreviewAccess({
  roleGuid: 'release-view-role',
  roleName: 'ReleaseViewRole',
  isSuperAdmin: false,
  implicitAllPermissions: false,
  explicitPermissionCodes: [P.System.ViewAppDownloads],
  effectivePermissionCodes: [P.System.ViewAppDownloads],
})

const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')

// WPF 版本已并入版本发布中心：旧地址只做重定向，不再单独懒加载页面。
assertEqual(
  routeSource.includes("import('../pages/System/WpfVersions')"),
  false,
  'Routes should no longer lazy import the standalone WPF versions page',
)
assertEqual(
  routeSource.includes("path: '/system/wpf-versions'") &&
    routeSource.includes('<Navigate replace to="/system/app-downloads?view=wpf" />') &&
    routeSource.includes("activeMenu: '/system/app-downloads'"),
  true,
  'Legacy WPF versions URL should redirect to the WPF terminal of the release center',
)
assertEqual(
  routeSource.includes("const SystemAppDownloadsPage = lazy(() => import('../pages/System/AppDownloads'))") &&
    routeSource.includes("path: '/system/app-downloads'") &&
    routeSource.includes("accessKey: 'canViewAppDownloads'") &&
    routeSource.includes('element: <SystemAppDownloadsPage />'),
  true,
  'Release center route should keep the App Downloads path and view permission',
)

assertEqual(
  getAccessKeyPermissionCodes('canViewAppDownloads').join(','),
  `${P.System.ViewAppDownloads},${P.System.ManageAppDownloads}`,
  'Release center menu should document view and manage permissions accepted by the route',
)

const preview = buildWebRoleMenuPreview(viewAccess, translate, { includeHidden: true })
assertEqual(
  Boolean(findNode(preview, '/system/wpf-versions')),
  false,
  'Web role preview should no longer list a separate WPF versions menu',
)
const releaseCenterMenu = findNode(preview, '/system/app-downloads')
assertEqual(Boolean(releaseCenterMenu), true, 'Web role preview should include the release center menu')
assertEqual(
  releaseCenterMenu?.permissionCodes.join(','),
  `${P.System.ViewAppDownloads},${P.System.ManageAppDownloads}`,
  'Release center menu preview should display both accepted permissions',
)
assertEqual(
  releaseCenterMenu?.edit.removePermissionCodes.join(','),
  `${P.System.ViewAppDownloads},${P.System.ManageAppDownloads}`,
  'Removing the release center menu should revoke both permissions, otherwise manage alone keeps it visible',
)

console.log('wpfVersionsRoute.test: ok')
