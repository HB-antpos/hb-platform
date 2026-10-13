import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from '../utils/access'
import { buildWebRoleMenuPreview, getAccessKeyPermissionCodes, type WebMenuPreviewNode } from '../utils/webMenuPreview'
import { getDefaultWebPath, hasBackendNavigationAccess, resolveAuthorizedWebTarget } from '../utils/webPortalAccess'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createCurrentUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'face-match-user',
    username: 'face-match-user',
    email: 'face-match@example.invalid',
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

const FACE_MATCH_PATH = '/system/face-match'
const FACE_MATCH_CODES = [
  P.FaceMatch.Manage,
  P.FaceMatch.Traffic.View,
  P.FaceMatch.Unwelcome.Manage,
  P.FaceMatch.Unwelcome.Approve,
  P.FaceMatch.Unwelcome.Review,
]

// 路由登记：懒加载、不保活（页面内存里会短暂持有复核图片 / 登记照预览的 objectURL）、全局菜单键与本页访问键
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${FACE_MATCH_PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记人脸比对路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart))
assertEqual(
  routeSource.includes("const FaceMatchPage = lazy(() => import('../pages/System/FaceMatch'))"),
  true,
  '页面必须懒加载，不进首屏包',
)
assertEqual(routeBlock.includes("title: 'menu.faceMatch'"), true, '路由标题使用全局菜单键')
assertEqual(routeBlock.includes("icon: 'SafetyCertificateOutlined'"), true, '菜单图标与后端导航一致：SafetyCertificateOutlined')
assertEqual(routeBlock.includes('keepAlive: false'), true, '路由不能 keepAlive')
assertEqual(routeBlock.includes("accessKey: 'canAccessFaceMatch'"), true, '路由使用独立访问键')
assertEqual(routeSource.includes('element: <FaceMatchPage />'), true, '路由必须渲染人脸比对页')
assertEqual(/SafetyCertificateOutlined:\s*<SafetyCertificateOutlined\s*\/>/.test(routeSource), true, '图标表里必须有 SafetyCertificateOutlined')
assertEqual(routeStart > routeSource.indexOf("path: '/system'"), true, '路由登记在系统分组之后')

// 权限码：与后端 Permissions.FaceMatch.* 完全一致
assertEqual(P.FaceMatch.Manage, 'FaceMatch.Manage', '权限码与后端一致')
assertEqual(P.FaceMatch.Traffic.View, 'FaceMatch.Traffic.View', '权限码与后端一致')
assertEqual(P.FaceMatch.Unwelcome.Manage, 'FaceMatch.Unwelcome.Manage', '权限码与后端一致')
assertEqual(P.FaceMatch.Unwelcome.Approve, 'FaceMatch.Unwelcome.Approve', '权限码与后端一致')
assertEqual(P.FaceMatch.Unwelcome.Review, 'FaceMatch.Unwelcome.Review', '权限码与后端一致')
assertEqual(
  getAccessKeyPermissionCodes('canAccessFaceMatch').join(','),
  FACE_MATCH_CODES.join(','),
  '菜单应映射到五个 FaceMatch 权限码',
)

// 五个权限码里任意一个：能进后台、登录落到该页、深链放行、菜单预览可见
for (const code of FACE_MATCH_CODES) {
  const only = buildAccess(createCurrentUser([code]))
  assertEqual(only.canAccessFaceMatch, true, `${code} 即可进入人脸比对`)
  assertEqual(only.canManageSystemSettings, false, `${code} 不连带系统设置权限`)
  assertEqual(only.canManageFaceCameras, false, `${code} 不连带摄像机管理权限`)
  assertEqual(hasBackendNavigationAccess(only), true, `${code} 的账号也要能进入后台`)
  assertEqual(only.canAccessAdminShell, true, `${code} 可进入后台壳`)
  assertEqual(getDefaultWebPath(only), FACE_MATCH_PATH, `${code} 登录落到该页`)
  assertEqual(resolveAuthorizedWebTarget(FACE_MATCH_PATH, only), FACE_MATCH_PATH, `${code} 页面地址被放行`)
  assertEqual(resolveAuthorizedWebTarget('/system/emergency-login-keys', only), undefined, `${code} 不能顺带放行系统设置类页面`)
  assertEqual(resolveAuthorizedWebTarget('/system/face-cameras', only), undefined, `${code} 不能顺带放行摄像机管理`)
  const preview = buildWebRoleMenuPreview(only, (key) => key, { includeHidden: true })
  assertEqual(findNode(preview, FACE_MATCH_PATH)?.visible, true, `${code} 的菜单预览应显示该入口`)
  assertEqual(
    findNode(preview, '/system')?.children?.some((child) => child.path === FACE_MATCH_PATH),
    true,
    '菜单挂在「系统」分组下',
  )
}

// 其它系统类权限都不能打开它；摄像机管理权限也不行
for (const code of [P.System.ManageSettings, P.System.ViewLogs, P.System.ViewAppDownloads, P.DeviceRegistration.Manage, P.PosTerminal.AuditView, P.FaceCapture.CamerasManage, P.FaceCapture.ImagesView]) {
  const other = buildAccess(createCurrentUser([code]))
  assertEqual(other.canAccessFaceMatch, false, `${code} 不应放行人脸比对`)
  assertEqual(
    findNode(buildWebRoleMenuPreview(other, (key) => key, { includeHidden: true }), FACE_MATCH_PATH)?.visible,
    false,
    `${code} 的菜单预览不应显示人脸比对`,
  )
  assertEqual(resolveAuthorizedWebTarget(FACE_MATCH_PATH, other), undefined, `${code} 不能访问该页面地址`)
}

// 没有任何权限的普通用户不可见
const nobody = buildAccess(createCurrentUser([]))
assertEqual(nobody.canAccessFaceMatch, false, '无权限不可访问')
assertEqual(resolveAuthorizedWebTarget(FACE_MATCH_PATH, nobody), undefined, '无权限不可访问')

// 管理员可见
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canAccessFaceMatch, true, '管理员可访问')
assertEqual(
  findNode(buildWebRoleMenuPreview(admin, (key) => key, { includeHidden: true }), FACE_MATCH_PATH)?.visible,
  true,
  '管理员菜单预览应显示该入口',
)
assertEqual(resolveAuthorizedWebTarget(FACE_MATCH_PATH, admin), FACE_MATCH_PATH, '管理员可访问')

// 组合权限用户沿用原默认入口，不被该页抢走落点
const withSettings = buildAccess(createCurrentUser([P.System.ManageSettings, P.FaceMatch.Manage]))
assertEqual(getDefaultWebPath(withSettings), '/system/invoice-email-settings', '组合权限用户沿用原默认入口')
const withCameras = buildAccess(createCurrentUser([P.FaceCapture.CamerasManage, P.FaceMatch.Manage]))
assertEqual(getDefaultWebPath(withCameras), '/system/face-cameras', '同时有摄像机管理权限时仍落在摄像机管理页（先登记者优先）')

console.log('faceMatchRoute.test: ok')
