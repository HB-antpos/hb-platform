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
    userGUID: 'face-cameras-user',
    username: 'face-cameras-user',
    email: 'face-cameras@example.invalid',
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

const FACE_CAMERAS_PATH = '/system/face-cameras'

// 路由登记：懒加载、不保活（页面内存里会短暂持有一次性摄像机地址）、全局菜单键与本页访问键
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const routeStart = routeSource.indexOf(`path: '${FACE_CAMERAS_PATH}'`)
assertEqual(routeStart >= 0, true, '必须登记人脸抓拍摄像机路由')
const routeBlock = routeSource.slice(routeStart, routeSource.indexOf('element:', routeStart))
assertEqual(
  routeSource.includes("const FaceCamerasPage = lazy(() => import('../pages/System/FaceCameras'))"),
  true,
  '页面必须懒加载，不进首屏包',
)
assertEqual(routeBlock.includes("title: 'menu.faceCameras'"), true, '路由标题使用全局菜单键')
const navGroupStart = routeSource.indexOf('const FACE_RECOGNITION_NAV_GROUP: NavGroupDefinition')
const navGroupBlock = routeSource.slice(navGroupStart, routeSource.indexOf('\nconst ', navGroupStart + 1))
// 已并入「人脸识别」合并页：菜单图标在合并页上，本页是其中一个标签
assertEqual(navGroupStart >= 0 && navGroupBlock.includes(`path: '${FACE_CAMERAS_PATH}'`), true, '人脸抓拍摄像机是「人脸识别」合并页的标签')
assertEqual(navGroupBlock.includes("icon: 'VideoCameraOutlined'"), true, '合并页菜单图标 VideoCameraOutlined')
assertEqual(routeBlock.includes('keepAlive: false'), true, '路由不能 keepAlive')
assertEqual(routeBlock.includes("accessKey: 'canManageFaceCameras'"), true, '路由使用独立访问键')
assertEqual(routeSource.includes('element: <FaceCamerasPage />'), true, '路由必须渲染人脸抓拍摄像机页')
assertEqual(/VideoCameraOutlined:\s*<VideoCameraOutlined\s*\/>/.test(routeSource), true, '图标表里必须有 VideoCameraOutlined')
// 路由登记在「系统」分组下（与紧急登录密钥等系统页并列）
assertEqual(
  routeSource.indexOf('...navGroupRoutes(FACE_RECOGNITION_NAV_GROUP)') > routeSource.indexOf("path: '/system'"),
  true,
  '合并页登记在系统分组下',
)

// 独立顶层权限码：与后端 Permissions.FaceCapture.Cameras.Manage 一致
assertEqual(P.FaceCapture.CamerasManage, 'FaceCapture.Cameras.Manage', '权限码与后端一致')
assertEqual(
  getAccessKeyPermissionCodes('canManageFaceCameras').join(','),
  P.FaceCapture.CamerasManage,
  '菜单应只映射到 FaceCapture.Cameras.Manage',
)

// 只有该权限：能进后台、登录落到该页、深链放行、菜单预览可见
const only = buildAccess(createCurrentUser([P.FaceCapture.CamerasManage]))
assertEqual(only.canManageFaceCameras, true, '单独授予即可管理人脸抓拍摄像机')
assertEqual(only.canManageSystemSettings, false, '不连带系统设置权限')
assertEqual(hasBackendNavigationAccess(only), true, '只有该权限的账号也要能进入后台')
assertEqual(only.canAccessAdminShell, true, '后台壳可进入')
assertEqual(getDefaultWebPath(only), FACE_CAMERAS_PATH, '只有该权限时登录落到该页')
assertEqual(resolveAuthorizedWebTarget(FACE_CAMERAS_PATH, only), FACE_CAMERAS_PATH, '页面地址被放行')
assertEqual(resolveAuthorizedWebTarget('/system/emergency-login-keys', only), undefined, '不能顺带放行系统设置类页面')
assertEqual(resolveAuthorizedWebTarget('/system/users', only), undefined, '不能顺带放行用户管理')
const onlyPreview = buildWebRoleMenuPreview(only, (key) => key, { includeHidden: true })
assertEqual(findNode(onlyPreview, FACE_CAMERAS_PATH)?.visible, true, '角色菜单预览应显示该入口')
assertEqual(findNode(onlyPreview, FACE_CAMERAS_PATH)?.permissionCodes.join(','), P.FaceCapture.CamerasManage, '预览节点只要求本权限')
assertEqual(
  findNode(onlyPreview, '/system/face-recognition')?.children?.some((child) => child.path === FACE_CAMERAS_PATH),
  true,
  '菜单挂在「系统 / 人脸识别」合并页下',
)

// 其它系统类权限都不能打开它
for (const code of [P.System.ManageSettings, P.System.ViewLogs, P.System.ViewAppDownloads, P.DeviceRegistration.Manage, P.PosTerminal.AuditView]) {
  const other = buildAccess(createCurrentUser([code]))
  assertEqual(other.canManageFaceCameras, false, `${code} 不应放行人脸抓拍摄像机`)
  assertEqual(
    findNode(buildWebRoleMenuPreview(other, (key) => key, { includeHidden: true }), FACE_CAMERAS_PATH)?.visible,
    false,
    `${code} 的菜单预览不应显示人脸抓拍摄像机`,
  )
  assertEqual(resolveAuthorizedWebTarget(FACE_CAMERAS_PATH, other), undefined, `${code} 不能访问该页面地址`)
}

// 没有任何权限的普通用户不可见
const nobody = buildAccess(createCurrentUser([]))
assertEqual(nobody.canManageFaceCameras, false, '无权限不可管理')
assertEqual(resolveAuthorizedWebTarget(FACE_CAMERAS_PATH, nobody), undefined, '无权限不可访问')

// 管理员可见
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canManageFaceCameras, true, '管理员可管理')
assertEqual(
  findNode(buildWebRoleMenuPreview(admin, (key) => key, { includeHidden: true }), FACE_CAMERAS_PATH)?.visible,
  true,
  '管理员菜单预览应显示该入口',
)
assertEqual(resolveAuthorizedWebTarget(FACE_CAMERAS_PATH, admin), FACE_CAMERAS_PATH, '管理员可访问')

// 组合权限用户沿用原默认入口，不被该页抢走落点
const withSettings = buildAccess(createCurrentUser([P.System.ManageSettings, P.FaceCapture.CamerasManage]))
assertEqual(getDefaultWebPath(withSettings), '/system/invoice-email-settings', '组合权限用户沿用原默认入口')

console.log('faceCamerasRoute.test: ok')
