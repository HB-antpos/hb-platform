import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from './access'
import {
  addExpoMenuPermission,
  buildExpoRoleMenuPreview,
  buildExpoUserMenuPreview,
  removeExpoMenuDirectPermissions,
} from './expoRoleMenuPreview'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertArrayEqual<T>(actual: T[], expected: T[], message: string) {
  assertEqual(JSON.stringify(actual), JSON.stringify(expected), message)
}

function buildPreview(permissionCodes: string[]) {
  const user: CurrentUser = {
    userGUID: 'expo-menu-test-user',
    username: 'expo-menu-test',
    email: '',
    permissions: permissionCodes,
    roleNames: [],
    storeNames: [],
  }

  return buildExpoRoleMenuPreview(buildAccess(user))
}

function readWorkspaceSource(relativePath: string) {
  return readFileSync(resolve(process.cwd(), '../..', relativePath), 'utf8')
}

assertEqual(
  buildPreview([P.Attendance.AvailabilitySubmitSelf]).visibleRoutes.some(
    (route) => route.routeName === 'attendance-personal',
  ),
  false,
  '个人考勤入口应与后端 FullAppMenu 一致，只由 Schedule.ViewSelf 授权',
)

assertEqual(
  buildPreview([P.Container.View]).visibleRoutes.some((route) => route.routeName === 'warehouse'),
  true,
  'Container.View 应通过仓库入口的任选权限显示仓库菜单',
)

const completePreview = buildPreview([
  P.Orders.Create,
  P.Orders.View,
  P.SalesOrders.View,
  P.Warehouse.ManageProducts,
  P.DomesticPurchase.ManageProducts,
  P.LocalPurchase.MobileView,
  P.Advertisements.View,
  P.Promotions.View,
  P.StoreProducts.View,
  P.StoreProducts.Edit,
  P.StoreProducts.PriceUpdates,
  P.SeasonalProductInsights.View,
  P.InstallmentOrders.View,
  P.StoreVouchers.View,
  P.Attendance.ScheduleViewSelf,
  P.Attendance.ScheduleViewStore,
  'SeasonalCards.Remaining.ViewManagedStore',
  P.Users.View,
  P.PosTerminal.AuditView,
  P.Roles.View,
  P.EmployeeProfiles.View,
  'EmployeeProfiles.ReviewSensitiveManagedStore',
  P.DeviceRegistration.View,
  P.Reports.ProductMovementView,
])

assertArrayEqual(
  completePreview.allRoutes.map((route) => route.routeName),
  [
    'home',
    'orders',
    'sales-orders',
    'cart',
    'warehouse',
    'container-new-products',
    'warehouse-picking',
    'domestic-purchase',
    'local-supplier-invoices',
    'advertisements',
    'promotions',
    'product-query',
    'price-updates',
    'product-insights',
    'warehouse-product-insights',
    'seasonal-product-insights',
    'installment-orders',
    'store-vouchers',
    'attendance-personal',
    'attendance-management',
    'seasonal-cards',
    'users',
    'pos-operation-logs',
    'user-admin',
    'cash-register-users',
    'roles',
    'permissions',
    'employee-profile',
    'employee-profile-review',
    'device-management',
    'reports',
    'app-downloads',
    'wpf-versions',
    'settings',
  ],
  'HbwebExpo 预览路由应与后端 FullAppMenu 及移动端默认路由保持完整一致',
)

const previewRouteNames = completePreview.allRoutes.map((route) => route.routeName)
const navigationServiceSource = readWorkspaceSource(
  'services/backend/BlazorApp.Api/Services/NavigationService.cs',
)
const fullAppMenuSource = navigationServiceSource.slice(
  navigationServiceSource.indexOf('private static readonly List<AppNavigationDefinition> FullAppMenu'),
  navigationServiceSource.indexOf('private static readonly HashSet<string> DeviceBaseRouteNames'),
)
const backendRouteNames = Array.from(
  fullAppMenuSource.matchAll(/RouteName\s*=\s*"([^"]+)"/g),
  (match) => match[1],
)
assertArrayEqual(
  backendRouteNames,
  previewRouteNames,
  'Web 预览路由顺序应直接匹配 NavigationService.FullAppMenu',
)

const mobileDefaultRouteSource = readWorkspaceSource('apps/mobile/src/modules/navigation/default-route.ts')
const mobileTabPathsSource = mobileDefaultRouteSource.slice(
  mobileDefaultRouteSource.indexOf('export const TAB_PATHS'),
  mobileDefaultRouteSource.indexOf('export const SUPPORTED_TAB_ROUTE_NAMES'),
)
const mobileRouteNames = Array.from(
  mobileTabPathsSource.matchAll(/^\s*(?:"([^"]+)"|'([^']+)'|([a-z][\w-]*)):\s*["']/gm),
  (match) => match[1] ?? match[2] ?? match[3],
).filter((routeName) => routeName !== 'workbench')
assertArrayEqual(
  [...mobileRouteNames].sort(),
  [...previewRouteNames].sort(),
  'Web 预览业务路由集合应匹配移动端 TAB_PATHS，固定工作台不进入权限菜单',
)

assertArrayEqual(
  completePreview.storeChildren.map((route) => route.routeName),
  [
    'home',
    'orders',
    'cart',
    'local-supplier-invoices',
    'product-query',
    'price-updates',
    'product-insights',
    'seasonal-product-insights',
    'installment-orders',
    'store-vouchers',
    'seasonal-cards',
  ],
  'Web 权限预览的门店业务分组应包含商品进销和节日贺卡',
)

// 季节商品查询使用独立权限：只有「查看分店商品」不应看到入口。
assertEqual(
  buildPreview([P.StoreProducts.View]).allRoutes.find((route) => route.routeName === 'seasonal-product-insights')?.visible,
  false,
  '只有查看分店商品权限时不应显示季节商品查询',
)

// 价格更新使用专用权限：只有「编辑分店商品」不应看到入口。
const editOnlyPreview = buildPreview([P.StoreProducts.View, P.StoreProducts.Edit])
assertEqual(
  editOnlyPreview.allRoutes.find((route) => route.routeName === 'price-updates')?.visible,
  false,
  '仅有 StoreProducts.Edit 时不应显示价格更新入口',
)
assertEqual(
  buildPreview([P.StoreProducts.PriceUpdates]).allRoutes.find((route) => route.routeName === 'price-updates')?.visible,
  true,
  '拥有 StoreProducts.PriceUpdates 时应显示价格更新入口',
)

const warehouseRoute = completePreview.allRoutes.find((route) => route.routeName === 'warehouse')
assertArrayEqual(
  warehouseRoute?.permissionCodes ?? [],
  [P.Warehouse.ManageProducts, P.Container.View],
  '仓库入口应声明 Warehouse.ManageProducts / Container.View 任选权限',
)

// 订单拣货入口与后端 FullAppMenu 的 AnyPermissions 同口径：拣货专用权限或两个仓库管理权限任一即可见。
const warehousePickingRoute = completePreview.allRoutes.find((route) => route.routeName === 'warehouse-picking')
assertArrayEqual(
  warehousePickingRoute?.permissionCodes ?? [],
  [P.Warehouse.Picking, P.Warehouse.Manage, P.Warehouse.ManageOrders],
  '订单拣货入口应声明 Warehouse.Picking / Warehouse.Manage / Warehouse.ManageOrders 任选权限',
)
assertEqual(warehousePickingRoute?.path, '/(shell)/warehouse-picking', '订单拣货入口应指向移动端 warehouse-picking 路由')
for (const permissionCode of [P.Warehouse.Picking, P.Warehouse.Manage, P.Warehouse.ManageOrders]) {
  assertEqual(
    buildPreview([permissionCode]).allRoutes.find((route) => route.routeName === 'warehouse-picking')?.visible,
    true,
    `仅有 ${permissionCode} 时应显示订单拣货入口`,
  )
}
assertEqual(
  buildPreview([P.Orders.View]).allRoutes.find((route) => route.routeName === 'warehouse-picking')?.visible,
  false,
  '只有订单查看权限时不应显示订单拣货入口',
)

// 新品到店入口与后端 FullAppMenu 同口径：只认移动端新品到店专用权限，货柜查看权限不会连带显示。
const containerNewProductsRoute = completePreview.allRoutes.find((route) => route.routeName === 'container-new-products')
assertArrayEqual(
  containerNewProductsRoute?.permissionCodes ?? [],
  [P.Container.MobileNewProductsView],
  '新品到店入口应只声明 Container.MobileNewProductsView',
)
assertEqual(
  buildPreview([P.Container.MobileNewProductsView]).allRoutes.find((route) => route.routeName === 'container-new-products')?.visible,
  true,
  '仅有 Container.MobileNewProductsView 时应显示新品到店入口',
)
assertEqual(
  buildPreview([P.Container.View]).allRoutes.find((route) => route.routeName === 'container-new-products')?.visible,
  false,
  '只有货柜查看权限时不应显示新品到店入口',
)

const inheritedAndDirectPreview = buildExpoUserMenuPreview({
  inheritedPermissionCodes: [P.Orders.View],
  directPermissionCodes: [P.Orders.View, P.Orders.Create, 'Unassignable.Direct'],
  assignablePermissionCodes: [P.Orders.View, P.Orders.Create],
  inheritedSources: [
    {
      roleName: 'StoreManager',
      permissionCodes: [P.Orders.View],
    },
  ],
})
const inheritedAndDirectOrders = inheritedAndDirectPreview.allRoutes.find(
  (route) => route.routeName === 'orders',
)

assertEqual(inheritedAndDirectOrders?.visible, true, '角色继承或直接草稿均应实时显示菜单')
assertEqual(inheritedAndDirectOrders?.inherited, true, '菜单应标记角色继承来源')
assertEqual(inheritedAndDirectOrders?.direct, true, '菜单应标记用户直接授权来源')
assertArrayEqual(
  inheritedAndDirectOrders?.roleSources ?? [],
  ['StoreManager'],
  '菜单应汇总满足入口权限的角色来源',
)
assertEqual(
  inheritedAndDirectOrders?.canRemove,
  true,
  '同时具有继承和直接授权时，应允许仅移除直接部分',
)

const directAfterOrdersRemoval = removeExpoMenuDirectPermissions({
  directPermissionCodes: [P.Orders.View, P.Orders.Create, 'Unassignable.Direct'],
  route: inheritedAndDirectOrders!,
  assignablePermissionCodes: [P.Orders.View, P.Orders.Create],
})
assertArrayEqual(
  directAfterOrdersRemoval,
  [P.Orders.Create, 'Unassignable.Direct'],
  '移除菜单只能删除该菜单对应且当前可分配的用户直接权限',
)

const inheritedAfterRemoval = buildExpoUserMenuPreview({
  inheritedPermissionCodes: [P.Orders.View],
  directPermissionCodes: directAfterOrdersRemoval,
  assignablePermissionCodes: [P.Orders.View, P.Orders.Create],
  inheritedSources: [{ roleName: 'StoreManager', permissionCodes: [P.Orders.View] }],
})
const inheritedOrdersAfterRemoval = inheritedAfterRemoval.allRoutes.find(
  (route) => route.routeName === 'orders',
)
assertEqual(inheritedOrdersAfterRemoval?.visible, true, '移除直接部分后，角色继承菜单仍应可见')
assertEqual(inheritedOrdersAfterRemoval?.direct, false, '移除后菜单不应再标记直接授权')
assertEqual(inheritedOrdersAfterRemoval?.locked, true, '仅由角色继承的菜单应锁定')

const restrictedPreview = buildExpoUserMenuPreview({
  inheritedPermissionCodes: [],
  directPermissionCodes: [],
  assignablePermissionCodes: [P.Container.View],
})
const restrictedWarehouse = restrictedPreview.allRoutes.find((route) => route.routeName === 'warehouse')

assertArrayEqual(
  restrictedWarehouse?.addPermissionCodes ?? [],
  [P.Container.View],
  '多权限任选入口应按菜单定义顺序选择第一个当前操作者可分配的权限',
)
assertEqual(restrictedWarehouse?.canAdd, true, '具有候选权限的隐藏菜单应允许添加')
assertEqual(
  restrictedPreview.allRoutes.some((route) => route.routeName === 'home'),
  false,
  '受限操作者不应看到其权限目录之外的隐藏菜单定义',
)

assertArrayEqual(
  addExpoMenuPermission({
    directPermissionCodes: [],
    route: restrictedWarehouse!,
    assignablePermissionCodes: [P.Container.View],
  }),
  [P.Container.View],
  '添加菜单应只把第一个可分配候选权限加入直接权限草稿',
)

const unrestrictedWarehousePreview = buildExpoUserMenuPreview({
  inheritedPermissionCodes: [],
  directPermissionCodes: [],
  assignablePermissionCodes: [P.Container.View, P.Warehouse.ManageProducts],
})
const unrestrictedWarehouse = unrestrictedWarehousePreview.allRoutes.find(
  (route) => route.routeName === 'warehouse',
)
assertArrayEqual(
  unrestrictedWarehouse?.addPermissionCodes ?? [],
  [P.Warehouse.ManageProducts],
  '候选白名单顺序不应覆盖菜单定义中的任选权限优先级',
)

assertArrayEqual(
  removeExpoMenuDirectPermissions({
    directPermissionCodes: [P.Warehouse.ManageProducts, 'Unassignable.Direct'],
    route: warehouseRoute!,
    assignablePermissionCodes: [],
  }),
  [P.Warehouse.ManageProducts, 'Unassignable.Direct'],
  '不可分配的既有直接权限不能通过移动端菜单被删除',
)

const settingsRoute = restrictedPreview.allRoutes.find((route) => route.routeName === 'settings')
assertEqual(settingsRoute?.visible, true, '无需权限的固定入口应始终可见')
assertEqual(settingsRoute?.fixed, true, '设置入口应标记为固定入口')
assertEqual(settingsRoute?.locked, true, '固定入口应始终锁定')
assertEqual(settingsRoute?.canAdd, false, '固定入口不应提供添加操作')
assertEqual(settingsRoute?.canRemove, false, '固定入口不应提供移除操作')

const implicitAdminPreview = buildExpoUserMenuPreview({
  inheritedPermissionCodes: [],
  directPermissionCodes: [],
  assignablePermissionCodes: [],
  implicitAllPermissions: true,
})
assertEqual(
  implicitAdminPreview.visibleRoutes.length,
  completePreview.allRoutes.filter((route) => !route.requireAdmin).length,
  '隐式全权限不等同于管理员身份，不能开放版本管理入口',
)
assertEqual(
  implicitAdminPreview.allRoutes.every(
    (route) => route.readOnly && route.locked && !route.canAdd && !route.canRemove,
  ),
  true,
  '隐式全权限菜单应统一只读且不可修改直接权限草稿',
)

const superAdminPreview = buildExpoUserMenuPreview({
  inheritedPermissionCodes: [],
  directPermissionCodes: [],
  assignablePermissionCodes: [],
  isSuperAdmin: true,
})
assertEqual(
  superAdminPreview.visibleRoutes.length,
  completePreview.allRoutes.length,
  '超级管理员标识应独立触发完整移动端菜单',
)
assertEqual(
  superAdminPreview.allRoutes.every(
    (route) => route.readOnly && route.locked && !route.canAdd && !route.canRemove,
  ),
  true,
  '超级管理员菜单应统一只读且不可修改直接权限草稿',
)

const sharedPermissionPreview = buildExpoUserMenuPreview({
  inheritedPermissionCodes: [],
  directPermissionCodes: [P.Orders.Create],
  assignablePermissionCodes: [P.Orders.Create],
})
assertArrayEqual(
  sharedPermissionPreview.visibleRoutes
    .filter((route) => route.routeName === 'home' || route.routeName === 'cart')
    .map((route) => route.routeName),
  ['home', 'cart'],
  '同一直接权限影响的多个菜单应同时刷新为可见',
)

const directAfterSharedRemoval = removeExpoMenuDirectPermissions({
  directPermissionCodes: [P.Orders.Create],
  route: sharedPermissionPreview.allRoutes.find((route) => route.routeName === 'home')!,
  assignablePermissionCodes: [P.Orders.Create],
})
const hiddenAfterSharedRemoval = buildExpoUserMenuPreview({
  inheritedPermissionCodes: [],
  directPermissionCodes: directAfterSharedRemoval,
  assignablePermissionCodes: [P.Orders.Create],
})
assertEqual(
  hiddenAfterSharedRemoval.allRoutes
    .filter((route) => route.routeName === 'home' || route.routeName === 'cart')
    .every((route) => !route.visible),
  true,
  '移除共享直接权限后，所有依赖菜单应同步刷新为隐藏',
)
