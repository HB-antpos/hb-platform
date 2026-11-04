import { readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from '../utils/access'
import { buildWebRoleMenuPreview, getAccessKeyPermissionCodes, type WebMenuPreviewNode } from '../utils/webMenuPreview'
import { getDefaultWebPath, hasBackendNavigationAccess, resolveAuthorizedWebTarget } from '../utils/webPortalAccess'

// 商品陈列图：路由登记（「商品与价格」合并页的第 5 个页签）、独立访问键、菜单预览、新权限默认不放行、与后端菜单一致。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function createCurrentUser(permissions: string[], roleNames: string[] = ['User']): CurrentUser {
  return {
    userGUID: 'planogram-user',
    username: 'planogram-user',
    email: 'planogram@example.invalid',
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

const PLANOGRAM_PATH = '/pos-admin/planograms'
const DESIGNER_PATH = '/pos-admin/planograms/:guid/design'
const GROUP_PATH = '/pos-admin/products-pricing'
const VIEW_CODE = 'Planogram.View'
const EDIT_CODE = 'Planogram.Edit'
const MANAGE_CODE = 'Planogram.Manage'

// 路由登记：作为「商品与价格」合并页的页签；懒加载、保活、全局菜单键与独立访问键
const routeSource = readFileSync(join(process.cwd(), 'src/router/routes.tsx'), 'utf8')
assertEqual(
  routeSource.includes("const PosAdminPlanogramPage = lazy(() => import('../pages/PosAdmin/Planogram'))"),
  true,
  '页面必须懒加载，不进首屏包',
)
const groupStart = routeSource.indexOf('const PRODUCTS_PRICING_NAV_GROUP')
const groupEnd = routeSource.indexOf('const SETTLEMENT_CASH_NAV_GROUP')
assertEqual(groupStart >= 0 && groupEnd > groupStart, true, '找得到「商品与价格」合并页定义')
const groupBlock = routeSource.slice(groupStart, groupEnd)
const tabStart = groupBlock.indexOf(`path: '${PLANOGRAM_PATH}'`)
assertEqual(tabStart >= 0, true, '商品陈列图必须登记为「商品与价格」合并页的页签')
const tabBlock = groupBlock.slice(tabStart, groupBlock.indexOf('/>', tabStart) + 2)
assertEqual(tabBlock.includes("title: 'menu.planogram'"), true, '页签标题使用全局菜单键')
assertEqual(tabBlock.includes("accessKey: 'canViewPlanogram'"), true, '页签使用独立访问键')
assertEqual(tabBlock.includes('keepAlive: true'), true, '页签保活：切走再回来保留页签、筛选与分页')
assertEqual(tabBlock.includes('element: <PosAdminPlanogramPage />'), true, '页签必须渲染商品陈列图页面')
assertEqual(
  groupBlock.indexOf(`path: '/pos-admin/promotions'`) < tabStart,
  true,
  '商品陈列图排在促销之后（设计图里的顺序）',
)
assertEqual(routeSource.split(`path: '${PLANOGRAM_PATH}'`).length - 1, 1, '只登记一次（不要既当页签又当独立菜单）')

// 设计器子页面（批 2）：独立懒加载块、隐藏路由、保活、查看权限即可进入（页内再按编辑权限控制）、侧栏高亮「商品与价格」合并页
assertEqual(
  routeSource.includes("const PosAdminPlanogramDesignerPage = lazy(() => import('../pages/PosAdmin/Planogram/Designer'))"),
  true,
  '设计器必须是独立懒加载块，不进首屏包',
)
const designerStart = routeSource.indexOf(`path: '${DESIGNER_PATH}'`)
assertEqual(designerStart >= 0, true, '必须登记设计器路由 /pos-admin/planograms/:guid/design')
const designerBlock = routeSource.slice(designerStart, routeSource.indexOf('element: <PosAdminPlanogramDesignerPage />', designerStart) + 60)
assertEqual(designerBlock.includes("title: 'menu.planogramDesigner'"), true, '设计器页签标题使用全局菜单键')
assertEqual(designerBlock.includes('hidden: true'), true, '设计器不进菜单')
assertEqual(designerBlock.includes('keepAlive: true'), true, '设计器保活：切到别的页签再回来保留画布与撤销栈')
assertEqual(designerBlock.includes("accessKey: 'canViewPlanogram'"), true, '设计器按查看权限进入（编辑能力页内控制）')
assertEqual(designerBlock.includes('activeMenu: PRODUCTS_PRICING_NAV_GROUP.path'), true, '设计器高亮「商品与价格」合并页菜单')
assertEqual(designerBlock.includes('element: <PosAdminPlanogramDesignerPage />'), true, '设计器路由渲染设计器页面')
assertEqual(routeSource.split(`path: '${DESIGNER_PATH}'`).length - 1, 1, '设计器路由只登记一次')

// 菜单文案在全局语言包（菜单在页面加载前就要显示）
for (const [language, expected, designer] of [['zh', '商品陈列图', '陈列方案设计'], ['en', 'Planogram', 'Planogram designer']] as const) {
  const locale = JSON.parse(readFileSync(join(process.cwd(), `src/i18n/locales/${language}.json`), 'utf8')) as {
    menu: Record<string, string>
  }
  assertEqual(locale.menu.planogram, expected, `${language} menu.planogram`)
  assertEqual(locale.menu.planogramDesigner, designer, `${language} menu.planogramDesigner`)
}

// 权限码与后端一致
assertEqual(P.Planogram.View, VIEW_CODE, '权限码与后端一致（View）')
assertEqual(P.Planogram.Edit, EDIT_CODE, '权限码与后端一致（Edit）')
assertEqual(P.Planogram.Manage, MANAGE_CODE, '权限码与后端一致（Manage）')
assertEqual(getAccessKeyPermissionCodes('canViewPlanogram').join(','), VIEW_CODE, '菜单入口只映射到 Planogram.View')

// 后端 Web 菜单：商品陈列图并入「商品与价格」合并页入口（Planogram.View 任一可见，Edit / Manage 经别名满足 View），
// 不单独登记菜单项，也不新增图标（沿用合并页的 AppstoreOutlined）；只有该权限的账号也要能拿到后台菜单。
// 后端缺这条时前端整棵菜单会退回本地菜单，只有该权限的账号会看不到入口。
const navigationSource = readFileSync(
  join(process.cwd(), '../../services/backend/BlazorApp.Api/Services/NavigationService.cs'),
  'utf8',
)
assertEqual(
  /Path = "\/pos-admin\/products-pricing".*Icon = "AppstoreOutlined".*AnyPermissions = new List<string> \{[^}]*Permissions\.Planogram\.View[^}]*\}/.test(navigationSource),
  true,
  '后端 FullMenu 的「商品与价格」合并页应包含 Planogram.View',
)
assertEqual(navigationSource.includes('Path = "/pos-admin/planograms"'), false, '后端 FullMenu 不单独登记商品陈列图菜单项')
const accessStart = navigationSource.indexOf('private static bool HasBackendNavigationAccess')
assertEqual(accessStart >= 0, true, '找得到 HasBackendNavigationAccess')
const accessBody = navigationSource.slice(accessStart, navigationSource.indexOf('private static', accessStart + 10))
assertEqual(accessBody.includes('Permissions.Planogram.View'), true, '后端 HasBackendNavigationAccess 应包含 Planogram.View（前端 ADMIN_ENTRY_RULES 同一入口语义）')

// 只有该权限：能进后台、登录落到该页签、合并页菜单地址与带参数的深链被放行、菜单预览可见
const only = buildAccess(createCurrentUser([VIEW_CODE]))
assertEqual(only.canViewPlanogram, true, '单独授予即可查看')
assertEqual(only.canEditPlanogram, false, 'View 不连带编辑')
assertEqual(only.canManagePlanogram, false, 'View 不连带型号维护')
assertEqual(only.canAccessDashboard, false, '不连带工作台')
assertEqual(only.canViewPosProducts, false, '不连带商品维护')
assertEqual(hasBackendNavigationAccess(only), true, '只有该权限的账号也要能进入后台')
assertEqual(only.canAccessAdminShell, true, '后台壳可进入')
assertEqual(getDefaultWebPath(only), PLANOGRAM_PATH, '只有该权限时登录落到该页签')
for (const target of [
  PLANOGRAM_PATH,
  `${PLANOGRAM_PATH}?tab=models`,
  `${PLANOGRAM_PATH}?tab=models&view=hook`,
  `${PLANOGRAM_PATH}?tab=specs`,
  // 设计器深链（从列表打开 / 刷新页面 / 书签）：查看权限即可进入
  `${PLANOGRAM_PATH}/0190f3c2-aaaa-7000-8000-0123456789ab/design`,
  // 合并页菜单地址：直接打开时会落到第一个有权限的标签
  GROUP_PATH,
]) {
  assertEqual(resolveAuthorizedWebTarget(target, only), target, `${target} 被放行`)
}
assertEqual(resolveAuthorizedWebTarget('/pos-admin/promotions', only), undefined, '不能顺带放行同一合并页里的其他标签')
assertEqual(resolveAuthorizedWebTarget('/pos-admin/planograms-evil/x/design', only), undefined, '路径前缀要按段匹配，不能被相似前缀蹭过')
assertEqual(resolveAuthorizedWebTarget('/pos-admin/daily-closes', only), undefined, '不能顺带放行其他后台页面')
const onlyPreview = buildWebRoleMenuPreview(only, (key) => key, { includeHidden: true })
assertEqual(findNode(onlyPreview, PLANOGRAM_PATH)?.visible, true, '角色菜单预览应显示该页签')
assertEqual(findNode(onlyPreview, PLANOGRAM_PATH)?.permissionCodes.join(','), VIEW_CODE, '预览节点只要求 Planogram.View')
assertEqual(findNode(onlyPreview, GROUP_PATH)?.visible, true, '预览里「商品与价格」合并页被点亮')

// Edit / Manage 各自单独也能进入页面（别名包含关系），并逐级放开按钮
for (const [code, canEdit, canManage] of [[EDIT_CODE, true, false], [MANAGE_CODE, true, true]] as const) {
  const single = buildAccess(createCurrentUser([code]))
  assertEqual(single.canViewPlanogram, true, `${code} 隐含查看`)
  assertEqual(single.canEditPlanogram, canEdit, `${code} 编辑能力`)
  assertEqual(single.canManagePlanogram, canManage, `${code} 型号维护能力`)
  assertEqual(hasBackendNavigationAccess(single), true, `${code} 的账号能进入后台`)
  assertEqual(getDefaultWebPath(single), PLANOGRAM_PATH, `${code} 单独时登录落到该页签`)
  assertEqual(resolveAuthorizedWebTarget(PLANOGRAM_PATH, single), PLANOGRAM_PATH, `${code} 可访问页面地址`)
}

// 商品价格类权限与考勤、系统、设备类权限都不能打开它（新权限默认不授予任何角色）
for (const code of [
  P.PosProducts.View,
  P.PosProducts.Manage,
  P.StoreProducts.View,
  P.StoreProducts.Edit,
  P.PricingStrategy.View,
  P.Promotions.View,
  P.Attendance.AdminView,
  P.System.ManageSettings,
  P.DeviceRegistration.Manage,
]) {
  const other = buildAccess(createCurrentUser([code]))
  assertEqual(other.canViewPlanogram, false, `${code} 不应放行商品陈列图`)
  assertEqual(
    findNode(buildWebRoleMenuPreview(other, (key) => key, { includeHidden: true }), PLANOGRAM_PATH)?.visible,
    false,
    `${code} 的菜单预览不应显示商品陈列图`,
  )
  assertEqual(resolveAuthorizedWebTarget(PLANOGRAM_PATH, other), undefined, `${code} 不能访问该页面地址`)
  assertEqual(resolveAuthorizedWebTarget(`${PLANOGRAM_PATH}/abc/design`, other), undefined, `${code} 不能访问设计器地址`)
}

const nobody = buildAccess(createCurrentUser([]))
assertEqual(nobody.canViewPlanogram, false, '无权限不可查看')
assertEqual(resolveAuthorizedWebTarget(PLANOGRAM_PATH, nobody), undefined, '无权限不可访问')
assertEqual(resolveAuthorizedWebTarget(`${PLANOGRAM_PATH}/abc/design`, nobody), undefined, '无权限不可访问设计器')

// 管理员可见、可编辑、可维护型号
const admin = buildAccess(createCurrentUser([], ['Admin']))
assertEqual(admin.canViewPlanogram && admin.canEditPlanogram && admin.canManagePlanogram, true, '管理员放行')
assertEqual(findNode(buildWebRoleMenuPreview(admin, (key) => key, { includeHidden: true }), PLANOGRAM_PATH)?.visible, true, '管理员菜单预览应显示该页签')
assertEqual(resolveAuthorizedWebTarget(PLANOGRAM_PATH, admin), PLANOGRAM_PATH, '管理员可访问')

// 组合权限用户沿用原默认入口，不被本页抢走落点
const withDailyCloses = buildAccess(createCurrentUser([P.DailyCloseRecords.View, VIEW_CODE]))
assertEqual(getDefaultWebPath(withDailyCloses), '/pos-admin/daily-closes', '组合权限用户沿用原默认入口')

console.log('planogramRoute.test: ok')
