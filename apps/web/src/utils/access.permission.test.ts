import type { CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { buildAccess } from './access'
import { buildExpoRoleMenuPreview, filterExpoRoutesByVisibility } from './expoRoleMenuPreview'
import { applyRolePermissionMutation, buildRolePreviewAccess } from './roleMenuPreview'
import { buildWebRoleMenuPreview, filterWebMenuNodesByVisibility, getAccessKeyPermissionCodes, type WebMenuPreviewNode } from './webMenuPreview'
import { getDefaultWebPath, resolveAuthorizedWebTarget, WEB_NO_ACCESS_PATH } from './webPortalAccess'

function createCurrentUser(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userGUID: 'test-user-guid',
    username: 'tester',
    email: 'tester@example.com',
    permissions: [],
    roleNames: [],
    storeNames: [],
    ...overrides,
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const translate = (key: string, fallback?: string) => fallback ?? key

function findWebMenuNode(nodes: WebMenuPreviewNode[], path: string): WebMenuPreviewNode | undefined {
  for (const node of nodes) {
    if (node.path === path) {
      return node
    }
    const child = node.children ? findWebMenuNode(node.children, path) : undefined
    if (child) {
      return child
    }
  }
  return undefined
}

const warehouseManagerAccess = buildAccess(
  createCurrentUser({
    roleNames: ['WarehouseManager'],
    permissions: [],
  }),
)

assertEqual(
  warehouseManagerAccess.hasPermission(P.Warehouse.ManageOrders),
  false,
  'WarehouseManager without explicit permissions should not gain Warehouse.ManageOrders at runtime',
)

const warehouseOrderManagerAccess = buildAccess(
  createCurrentUser({
    permissions: [P.Warehouse.ManageOrders],
  }),
)

assertEqual(
  warehouseOrderManagerAccess.canManageWarehouseOrders,
  true,
  'Warehouse.ManageOrders should unlock warehouse store order management',
)

assertEqual(
  warehouseOrderManagerAccess.canManageWarehouse,
  false,
  'Warehouse.ManageOrders should not be confused with the legacy whole-warehouse permission',
)

assertEqual(
  warehouseOrderManagerAccess.canAccessAdminShell,
  true,
  'Warehouse.ManageOrders should unlock the admin shell without granting Dashboard.View',
)

assertEqual(
  getDefaultWebPath(warehouseOrderManagerAccess),
  '/warehouse/store-orders',
  'Warehouse.ManageOrders should default to the store order page',
)

assertEqual(
  resolveAuthorizedWebTarget('/warehouse/store-orders', warehouseOrderManagerAccess),
  '/warehouse/store-orders',
  'Warehouse.ManageOrders should preserve an authorized warehouse redirect target',
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/executive-sales-intelligence/purchase-amount-dashboard',
    warehouseOrderManagerAccess,
  ),
  undefined,
  'Warehouse.ManageOrders should not authorize a local purchase dashboard redirect target',
)

const storeManagerAccess = buildAccess(
  createCurrentUser({
    roleNames: ['StoreManager'],
    permissions: [],
    stores: [
      {
        storeGUID: 'managed-store-guid',
        storeName: 'Managed Store',
        storeCode: 'M1',
        isManageable: true,
        assignedAt: '2026-01-01T00:00:00Z',
      },
      {
        storeGUID: 'linked-store-guid',
        storeName: 'Linked Store',
        storeCode: 'L1',
        isManageable: false,
        assignedAt: '2026-01-01T00:00:00Z',
      },
    ],
  }),
)

assertEqual(
  storeManagerAccess.hasPermission(P.StoreProducts.View),
  false,
  'StoreManager without explicit permissions should not gain StoreProducts.View at runtime',
)

assertEqual(
  storeManagerAccess.canManageStoreProducts,
  false,
  'StoreManager without explicit permissions should not unlock store product management',
)

assertEqual(
  storeManagerAccess.managedStoreCodes()?.join(','),
  'M1',
  'StoreManager managed store scope should include only manageable stores',
)

assertEqual(
  storeManagerAccess.visibleStoreCodes()?.join(','),
  'M1,L1',
  'StoreManager visible store scope should include all linked stores',
)

const productMovementReportOnlyAccess = buildAccess(
  createCurrentUser({
    permissions: [P.SalesDashboard.ProductMovementView],
  }),
)

const localPurchaseDashboardOnlyAccess = buildAccess(
  createCurrentUser({
    permissions: [P.LocalPurchase.View],
  }),
)

assertEqual(
  localPurchaseDashboardOnlyAccess.canManageLocalPurchase,
  true,
  'LocalPurchase.View should unlock local purchase pages',
)

assertEqual(
  localPurchaseDashboardOnlyAccess.canViewSalesIntelligence,
  false,
  '本地进货权限不再连带显示销售看板',
)

assertEqual(
  localPurchaseDashboardOnlyAccess.canAccessAdminShell,
  true,
  'LocalPurchase.View should unlock the admin shell',
)

assertEqual(
  localPurchaseDashboardOnlyAccess.canAccessDashboard,
  false,
  'LocalPurchase.View should not grant Dashboard.View',
)

assertEqual(
  localPurchaseDashboardOnlyAccess.canViewReports,
  false,
  'LocalPurchase.View should not grant other sales report permissions',
)

assertEqual(
  localPurchaseDashboardOnlyAccess.canViewProductMovementReport,
  false,
  'LocalPurchase.View should not grant product movement report access',
)

assertEqual(
  getDefaultWebPath(localPurchaseDashboardOnlyAccess),
  '/pos-admin/local-supplier-invoices',
  'LocalPurchase.View should default to the only authorized admin page',
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/executive-sales-intelligence/purchase-amount-dashboard',
    localPurchaseDashboardOnlyAccess,
  ),
  undefined,
  '本地进货权限不允许跳转到未授权的进货金额看板',
)

assertEqual(
  resolveAuthorizedWebTarget('/dashboard', localPurchaseDashboardOnlyAccess),
  undefined,
  'LocalPurchase.View should not authorize a Dashboard.View redirect target',
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/pos-admin/local-supplier-invoices/invoice-guid-1',
    localPurchaseDashboardOnlyAccess,
  ),
  undefined,
  'LocalPurchase.View should not preserve an invoice edit redirect without LocalPurchase.Edit',
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/pos-admin/local-supplier-invoices/invoice-guid-1/',
    localPurchaseDashboardOnlyAccess,
  ),
  undefined,
  'A trailing slash must not bypass the LocalPurchase.Edit redirect check',
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/pos-admin/local-supplier-invoices/invoice-guid-1/sales-analysis',
    localPurchaseDashboardOnlyAccess,
  ),
  '/pos-admin/local-supplier-invoices/invoice-guid-1/sales-analysis',
  'LocalPurchase.View should preserve an invoice sales analysis redirect',
)

assertEqual(
  resolveAuthorizedWebTarget('/pos-admin/invoice-detail/invoice-guid-1', localPurchaseDashboardOnlyAccess),
  '/pos-admin/invoice-detail/invoice-guid-1',
  'LocalPurchase.View should preserve a read-only invoice detail redirect',
)

const localPurchaseEditorAccess = buildAccess(
  createCurrentUser({
    permissions: [P.LocalPurchase.View, P.LocalPurchase.Edit],
  }),
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/pos-admin/local-supplier-invoices/invoice-guid-1',
    localPurchaseEditorAccess,
  ),
  '/pos-admin/local-supplier-invoices/invoice-guid-1',
  'LocalPurchase.Edit should preserve an invoice edit redirect',
)

const dashboardOnlyAccess = buildAccess(
  createCurrentUser({
    permissions: [P.Dashboard.View],
  }),
)

assertEqual(
  dashboardOnlyAccess.canAccessAdminShell,
  true,
  'Dashboard.View should continue to unlock the admin shell',
)

assertEqual(
  getDefaultWebPath(dashboardOnlyAccess),
  '/dashboard',
  'Dashboard.View should keep the existing default page',
)

const noWebPortalAccess = buildAccess(createCurrentUser())

assertEqual(
  noWebPortalAccess.canAccessAdminShell,
  false,
  'Users without an admin feature permission should not enter the admin shell',
)

assertEqual(
  getDefaultWebPath(noWebPortalAccess),
  WEB_NO_ACCESS_PATH,
  'Users without Web portal permissions should keep the access denied landing page',
)

const pureOrderAccess = buildAccess(
  createCurrentUser({
    roleNames: ['Order'],
    permissions: [P.OrderFront.View, P.Dashboard.View],
  }),
)

assertEqual(pureOrderAccess.onlyOrder, true, 'Only the Order role should be treated as a pure order role')
assertEqual(
  pureOrderAccess.canAccessOrderFront,
  true,
  'A pure Order role should retain OrderFront access',
)
assertEqual(
  pureOrderAccess.canAccessAdminShell,
  false,
  'A pure Order role must not enter the admin shell even with a backend permission',
)
assertEqual(
  getDefaultWebPath(pureOrderAccess),
  '/shop',
  'A pure Order role should always default to the shop',
)
assertEqual(
  resolveAuthorizedWebTarget('/dashboard', pureOrderAccess),
  '/shop',
  'A pure Order role should send a historical admin redirect to the shop',
)
assertEqual(
  resolveAuthorizedWebTarget('/warehouse/store-orders', pureOrderAccess),
  '/shop',
  'A pure Order role must not bypass the shop boundary with a direct admin address',
)
assertEqual(
  resolveAuthorizedWebTarget('/shop/orders', pureOrderAccess),
  '/shop/orders',
  'A pure Order role should retain authorized shop subpaths',
)

const pureChineseOrderAccess = buildAccess(createCurrentUser({ roleNames: ['订货员'] }))
assertEqual(pureChineseOrderAccess.onlyOrder, true, 'Only the 订货员 alias should be treated as a pure order role')

const pureOrderAliasAccess = buildAccess(createCurrentUser({ roleNames: ['Order', '订货员'] }))
assertEqual(
  pureOrderAliasAccess.onlyOrder,
  true,
  'Both Order aliases together should still be treated as a pure order role',
)

const multiRoleOrderAccess = buildAccess(
  createCurrentUser({
    roleNames: ['Order', 'WarehouseStaff'],
    permissions: [P.Dashboard.View],
  }),
)
assertEqual(
  multiRoleOrderAccess.onlyOrder,
  false,
  'Order plus any non-order role must not be treated as a pure order role',
)
assertEqual(
  multiRoleOrderAccess.canAccessAdminShell,
  true,
  'A multi-role Order user should retain the normal backend permission behavior',
)

const backendNavigationEntryCases: Array<[string, string]> = [
  [P.Dashboard.View, '/dashboard'],
  [P.Warehouse.ManageOrders, '/warehouse/store-orders'],
  [P.Warehouse.Manage, '/warehouse/store-orders'],
  [P.Container.View, '/warehouse/containers'],
  [P.SalesDashboard.ProductMovementView, '/executive-sales-intelligence/product-movement-report'],
  [P.LocalPurchase.View, '/pos-admin/local-supplier-invoices'],
  [P.System.ManageSettings, '/system/invoice-email-settings'],
  [P.System.ViewAppDownloads, '/system/app-downloads'],
  [P.System.ManageAppDownloads, '/system/app-downloads'],
  [P.PosTerminal.AuditView, '/pos-admin/operation-logs'],
  [P.Cash.OverviewView, '/pos-admin/store-cash'],
  [P.SeasonalCards.Remaining.ViewAllStores, '/pos-admin/seasonal-card-stats'],
]

for (const [permission, expectedPath] of backendNavigationEntryCases) {
  const access = buildAccess(createCurrentUser({ permissions: [permission] }))
  assertEqual(access.canAccessAdminShell, true, `${permission} should unlock the admin shell`)
  assertEqual(
    getDefaultWebPath(access),
    expectedPath,
    `${permission} should default to its first authorized backend page`,
  )
}

assertEqual(
  productMovementReportOnlyAccess.canViewReports,
  false,
  '商品经营分析单页权限不应授予旧报表总权限',
)

assertEqual(
  productMovementReportOnlyAccess.canViewProductMovementReport,
  true,
  '商品经营分析单页权限应放开对应页面',
)

assertEqual(
  productMovementReportOnlyAccess.canViewSalesIntelligence,
  true,
  'Product movement report permission should keep sales dashboard parent visible',
)

const adminAccess = buildAccess(
  createCurrentUser({
    roleNames: ['Admin'],
    permissions: [],
  }),
)

assertEqual(
  adminAccess.hasPermission(P.Container.Delete),
  true,
  'Admin should continue to satisfy all permission checks',
)

const systemLogAccess = buildAccess(
  createCurrentUser({
    permissions: [P.System.ViewLogs],
  }),
)

assertEqual(
  systemLogAccess.canViewSystemLogs,
  true,
  'System.ViewLogs should unlock center log page visibility',
)

for (const roleName of ['SuperAdmin', '超级管理员']) {
  const superAdminAccess = buildAccess(
    createCurrentUser({
      roleNames: [roleName],
      permissions: [],
    }),
  )
  assertEqual(superAdminAccess.isAdmin, true, `${roleName} should use the global admin access path`)
  assertEqual(
    superAdminAccess.canViewOperationAudits,
    true,
    `${roleName} should see employee operation audits without an explicit permission row`,
  )
}

const operationAuditAccess = buildAccess(
  createCurrentUser({
    permissions: ['Permissions.PosTerminal.Audit.View'],
  }),
)

assertEqual(
  (operationAuditAccess as unknown as Record<string, unknown>).canViewOperationAudits,
  true,
  'PosTerminal.Audit.View should unlock employee operation audit visibility',
)

const usersViewOnlyAccess = buildAccess(createCurrentUser({ permissions: [P.Users.View] }))

assertEqual(
  usersViewOnlyAccess.canReadUser,
  true,
  'Users.View should keep unlocking user read access for mobile pages and APIs',
)

assertEqual(
  usersViewOnlyAccess.canReadUserConsole,
  false,
  'Users.View alone should not show the Web system users menu or page',
)

const usersConsoleAccess = buildAccess(createCurrentUser({ permissions: [P.Users.ViewWebConsole] }))

assertEqual(
  usersConsoleAccess.canReadUserConsole,
  true,
  'Users.ViewWebConsole should unlock the Web system users menu and page',
)

assertEqual(
  adminAccess.canReadUserConsole,
  true,
  'Admin should continue to see the Web system users menu',
)

const appDownloadAccess = buildAccess(
  createCurrentUser({
    permissions: [P.System.ViewAppDownloads],
  }),
)

assertEqual(
  appDownloadAccess.canViewAppDownloads,
  true,
  'System.ViewAppDownloads should unlock App download page visibility',
)

assertEqual(
  appDownloadAccess.canManageAppDownloads,
  false,
  'System.ViewAppDownloads should not unlock OTA management actions',
)

const appDownloadManagerAccess = buildAccess(
  createCurrentUser({
    permissions: [P.System.ManageAppDownloads],
  }),
)

assertEqual(
  appDownloadManagerAccess.canManageAppDownloads,
  true,
  'System.ManageAppDownloads should unlock OTA management actions',
)

assertEqual(
  appDownloadManagerAccess.canViewAppDownloads,
  true,
  'System.ManageAppDownloads should implied System.ViewAppDownloads for page visibility',
)

const appDownloadDeniedAccess = buildAccess(createCurrentUser())

assertEqual(
  appDownloadDeniedAccess.canViewAppDownloads,
  false,
  'Missing System.ViewAppDownloads should not unlock App download page visibility',
)

assertEqual(
  adminAccess.canViewAppDownloads,
  true,
  'Admin should continue to satisfy App download page visibility',
)

assertEqual(
  adminAccess.canManageAppDownloads,
  true,
  'Admin should continue to satisfy App download management actions',
)

const scheduledTaskManagerAccess = buildAccess(
  createCurrentUser({
    permissions: [P.System.ManageScheduledTasks],
  }),
)

assertEqual(
  scheduledTaskManagerAccess.canManageScheduledTasks,
  true,
  'System.ManageScheduledTasks should unlock scheduled task runtime control writes',
)

const legacyAliasAccess = buildAccess(
  createCurrentUser({
    permissions: ['LocalInvocie.View', 'LocalInvocie.Edit'],
  }),
)

assertEqual(
  legacyAliasAccess.hasPermission(P.LocalPurchase.View),
  true,
  'Legacy LocalInvocie.View should continue to satisfy LocalPurchase.View',
)

assertEqual(
  legacyAliasAccess.hasPermission(P.LocalPurchase.Edit),
  true,
  'Legacy LocalInvocie.Edit should continue to satisfy LocalPurchase.Edit',
)

const containerAccess = buildAccess(
  createCurrentUser({
    permissions: [P.Container.View],
  }),
)

assertEqual(
  containerAccess.canViewContainers,
  true,
  'Container.View should unlock container list/detail visibility',
)

assertEqual(
  resolveAuthorizedWebTarget('/warehouse/container/detail/container-guid-1', containerAccess),
  '/warehouse/container/detail/container-guid-1',
  'Container.View should preserve a container detail redirect',
)

assertEqual(
  resolveAuthorizedWebTarget('/warehouse/container/allocation-sales/container-guid-1', containerAccess),
  '/warehouse/container/allocation-sales/container-guid-1',
  'Container.View should preserve a container allocation sales redirect',
)

const warehouseStaffOrderAccess = buildAccess(
  createCurrentUser({
    roleNames: ['WarehouseStaff'],
    permissions: [P.Warehouse.ManageOrders],
  }),
)

const warehouseStaffOrderFrontAccess = buildAccess(
  createCurrentUser({
    roleNames: ['WarehouseStaff'],
    permissions: [P.Orders.Create],
  }),
)
assertEqual(
  warehouseStaffOrderFrontAccess.canAccessOrderFront,
  true,
  'WarehouseStaff + Orders.Create 必须可以进入正式订货前台',
)
assertEqual(getDefaultWebPath(warehouseStaffOrderFrontAccess), '/shop', '该精确组合登录后默认进入订货前台')
assertEqual(
  resolveAuthorizedWebTarget('/shop', warehouseStaffOrderFrontAccess),
  '/shop',
  '该精确组合的 /shop 重定向必须保留',
)
const warehouseStaffWithoutCreateAccess = buildAccess(
  createCurrentUser({ roleNames: ['WarehouseStaff'], permissions: [] }),
)
assertEqual(
  warehouseStaffWithoutCreateAccess.canAccessOrderFront,
  false,
  'WarehouseStaff 没有 Orders.Create 时不能扩大订货前台权限',
)

assertEqual(
  warehouseStaffOrderAccess.canManageWarehouseOrders,
  true,
  'Warehouse.ManageOrders should keep warehouse order pages available to warehouse staff',
)

assertEqual(
  warehouseStaffOrderAccess.canManageStoreOrderImportPriceVariance,
  false,
  'WarehouseStaff should not gain the import price variance page from Warehouse.ManageOrders alone',
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/warehouse/store-order-import-price-variance',
    warehouseStaffOrderAccess,
  ),
  undefined,
  'Warehouse order access should not preserve the price variance redirect without its leaf permission flag',
)

const warehouseStaffLegacyManageAccess = buildAccess(
  createCurrentUser({
    roleNames: ['WarehouseStaff'],
    permissions: [P.Warehouse.Manage],
  }),
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/warehouse/store-order-import-price-variance',
    warehouseStaffLegacyManageAccess,
  ),
  undefined,
  'Warehouse.Manage must not bypass the price variance leaf permission for warehouse staff',
)

assertEqual(
  resolveAuthorizedWebTarget('/warehouse/containers', warehouseStaffLegacyManageAccess),
  undefined,
  'Warehouse.Manage must not bypass Container.View during login redirect',
)

assertEqual(
  resolveAuthorizedWebTarget('/warehouse/products', warehouseStaffLegacyManageAccess),
  '/warehouse/products',
  'Warehouse.Manage should continue to preserve its authorized warehouse product redirect',
)

assertEqual(
  warehouseOrderManagerAccess.canManageStoreOrderImportPriceVariance,
  true,
  'A non-warehouse-staff order manager should retain the import price variance page',
)

assertEqual(
  resolveAuthorizedWebTarget(
    '/warehouse/store-order-import-price-variance',
    warehouseOrderManagerAccess,
  ),
  '/warehouse/store-order-import-price-variance',
  'The dedicated price variance flag should preserve its redirect target',
)

const noPermissionPreviewAccess = buildRolePreviewAccess({
  roleGuid: 'role-without-permissions',
  roleName: 'NoPermissionRole',
  isSuperAdmin: false,
  implicitAllPermissions: false,
  explicitPermissionCodes: [],
  effectivePermissionCodes: [],
})
const noPermissionExpoPreview = buildExpoRoleMenuPreview(noPermissionPreviewAccess, translate)

assertEqual(
  noPermissionExpoPreview.visibleRoutes.map((route) => route.routeName).join(','),
  'settings',
  'Role preview without Expo app permissions should only show settings',
)

assertEqual(
  noPermissionExpoPreview.storeChildren.length,
  0,
  'Role preview without Expo app permissions should not show store menu children',
)

assertEqual(
  filterExpoRoutesByVisibility(noPermissionExpoPreview.allRoutes, 'visible').map((route) => route.routeName).join(','),
  'settings',
  'Visible HbwebExpo filter should only show settings for roles without app permissions',
)

assertEqual(
  filterExpoRoutesByVisibility(noPermissionExpoPreview.allRoutes, 'hidden').some((route) => route.routeName === 'home'),
  true,
  'Hidden HbwebExpo filter should include protected routes for roles without app permissions',
)

const orderCreatorExpoPreview = buildExpoRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'order-creator-role',
    roleName: 'OrderCreatorRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [
      P.Orders.Create,
      P.Orders.View,
      P.StoreProducts.View,
      P.LocalPurchase.View,
      P.InstallmentOrders.View,
      P.StoreVouchers.View,
    ],
    effectivePermissionCodes: [
      P.Orders.Create,
      P.Orders.View,
      P.StoreProducts.View,
      P.LocalPurchase.View,
      P.InstallmentOrders.View,
      P.StoreVouchers.View,
    ],
  }),
  translate,
)

assertEqual(
  orderCreatorExpoPreview.visibleRoutes.some((route) => route.routeName === 'home'),
  true,
  'Orders.Create should show the HbwebExpo home tab',
)

assertEqual(
  orderCreatorExpoPreview.visibleRoutes.some((route) => route.routeName === 'cart'),
  true,
  'Orders.Create should show the HbwebExpo cart tab',
)

assertEqual(
  orderCreatorExpoPreview.visibleRoutes.some((route) => route.routeName === 'product-query'),
  true,
  'StoreProducts.View should show the HbwebExpo product-query tab',
)

assertEqual(
  orderCreatorExpoPreview.displayTabs.some((item) => item.type === 'store'),
  true,
  'Overflowing HbwebExpo store routes should collapse into the store menu',
)

assertEqual(
  orderCreatorExpoPreview.storeChildren.some((route) => route.routeName === 'installment-orders'),
  true,
  'Collapsed HbwebExpo store menu should include installment orders when permitted',
)

assertEqual(
  filterExpoRoutesByVisibility(orderCreatorExpoPreview.allRoutes, 'visible').some((route) => route.routeName === 'local-supplier-invoices'),
  true,
  'Visible HbwebExpo filter should include authorized local supplier invoice route',
)

assertEqual(
  filterExpoRoutesByVisibility(orderCreatorExpoPreview.allRoutes, 'hidden').some((route) => route.routeName === 'local-supplier-invoices'),
  false,
  'Hidden HbwebExpo filter should exclude authorized local supplier invoice route',
)

assertEqual(
  filterExpoRoutesByVisibility(orderCreatorExpoPreview.allRoutes, 'all').length,
  orderCreatorExpoPreview.allRoutes.length,
  'All HbwebExpo filter should keep the complete route permission list',
)

for (const permissionCode of [
  P.OrderFront.View,
  P.Orders.View,
  P.Warehouse.ManageOrders,
  P.Warehouse.Manage,
]) {
  const preview = buildExpoRoleMenuPreview(
    buildRolePreviewAccess({
      roleGuid: `expo-orders-${permissionCode}`,
      roleName: 'PdaOrdersRole',
      isSuperAdmin: false,
      implicitAllPermissions: false,
      explicitPermissionCodes: [permissionCode],
      effectivePermissionCodes: [permissionCode],
    }),
    translate,
    { explicitPermissionCodes: [permissionCode] },
  )
  assertEqual(
    preview.visibleRoutes.some((route) => route.routeName === 'orders'),
    true,
    `${permissionCode} should show the PDA orders tab`,
  )
}

const mobileLocalPurchaseAccess = buildAccess(
  createCurrentUser({ permissions: [P.LocalPurchase.MobileView] }),
)
assertEqual(
  mobileLocalPurchaseAccess.canManageLocalPurchase,
  false,
  'LocalPurchase.MobileView must not unlock desktop local purchase pages',
)
assertEqual(
  mobileLocalPurchaseAccess.canViewSalesIntelligence,
  false,
  'LocalPurchase.MobileView must not unlock the desktop purchase dashboard',
)
assertEqual(
  mobileLocalPurchaseAccess.canReadOrder,
  false,
  'LocalPurchase.MobileView must not unlock desktop cashier records',
)

const mobileLocalPurchaseExpoPreview = buildExpoRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'mobile-local-purchase-role',
    roleName: 'MobileLocalPurchaseRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.LocalPurchase.MobileView],
    effectivePermissionCodes: [P.LocalPurchase.MobileView],
  }),
  translate,
  { explicitPermissionCodes: [P.LocalPurchase.MobileView] },
)
assertEqual(
  mobileLocalPurchaseExpoPreview.visibleRoutes.some((route) => route.routeName === 'local-supplier-invoices'),
  true,
  'LocalPurchase.MobileView should show 澳洲进货 in the PDA menu',
)

const missingLocalPurchaseExpoPreview = buildExpoRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'missing-local-purchase-role',
    roleName: 'MissingLocalPurchaseRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [],
    effectivePermissionCodes: [],
  }),
  translate,
)
const missingLocalPurchaseExpoRoute = missingLocalPurchaseExpoPreview.allRoutes.find(
  (route) => route.routeName === 'local-supplier-invoices',
)
assertEqual(
  missingLocalPurchaseExpoRoute?.addPermissionCodes.join(','),
  P.LocalPurchase.MobileView,
  'Adding 澳洲进货 should grant the dedicated PDA permission first',
)

const mobileLocalPurchaseWebPreview = buildWebRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'mobile-local-purchase-web-role',
    roleName: 'MobileLocalPurchaseWebRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.LocalPurchase.MobileView],
    effectivePermissionCodes: [P.LocalPurchase.MobileView],
  }),
  translate,
  { includeHidden: true },
)
assertEqual(
  filterWebMenuNodesByVisibility(mobileLocalPurchaseWebPreview, 'visible').length,
  0,
  'LocalPurchase.MobileView should leave the desktop menu empty',
)
assertEqual(
  findWebMenuNode(mobileLocalPurchaseWebPreview, '/pos-admin/local-supplier-invoices')?.visible,
  false,
  'LocalPurchase.MobileView must not show the desktop 澳洲进货 page',
)
assertEqual(
  findWebMenuNode(mobileLocalPurchaseWebPreview, '/executive-sales-intelligence/purchase-amount-dashboard')?.visible,
  false,
  'LocalPurchase.MobileView must not show the desktop purchase dashboard',
)
assertEqual(
  findWebMenuNode(mobileLocalPurchaseWebPreview, '/pos-admin/sales-orders')?.visible,
  false,
  'LocalPurchase.MobileView must not show desktop cashier records',
)

const attendanceExpoPreview = buildExpoRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'attendance-role',
    roleName: 'AttendanceRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.Attendance.ScheduleViewSelf],
    effectivePermissionCodes: [P.Attendance.ScheduleViewSelf],
  }),
  translate,
)

assertEqual(
  attendanceExpoPreview.visibleRoutes.some((route) => route.routeName === 'attendance-personal'),
  true,
  'HbwebExpo legacy attendance app menu should expand to attendance-personal',
)

assertEqual(
  attendanceExpoPreview.visibleRoutes.some((route) => route.routeName === 'attendance'),
  false,
  'HbwebExpo preview should not expose the legacy attendance route directly',
)

const attendanceSelfByAvailabilityPreview = buildExpoRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'attendance-self-availability-role',
    roleName: 'AttendanceSelfAvailabilityRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.Attendance.AvailabilitySubmitSelf],
    effectivePermissionCodes: [P.Attendance.AvailabilitySubmitSelf],
  }),
  translate,
)

assertEqual(
  attendanceSelfByAvailabilityPreview.visibleRoutes.some((route) => route.routeName === 'attendance-personal'),
  false,
  'HbwebExpo attendance personal menu should match FullAppMenu and require ScheduleViewSelf',
)

const attendanceManagementPreview = buildExpoRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'attendance-management-role',
    roleName: 'AttendanceManagementRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.Attendance.ScheduleViewStore],
    effectivePermissionCodes: [P.Attendance.ScheduleViewStore],
  }),
  translate,
)

assertEqual(
  attendanceManagementPreview.visibleRoutes.some((route) => route.routeName === 'attendance-management'),
  true,
  'HbwebExpo attendance management menu should follow AnyPermissions rules for store attendance permissions',
)

const warehousePreviewAccess = buildRolePreviewAccess({
  roleGuid: 'warehouse-role',
  roleName: 'WarehouseRole',
  isSuperAdmin: false,
  implicitAllPermissions: false,
  explicitPermissionCodes: [P.Warehouse.ManageProducts],
  effectivePermissionCodes: [P.Warehouse.ManageProducts],
})

assertEqual(
  buildExpoRoleMenuPreview(warehousePreviewAccess, translate).visibleRoutes.some((route) => route.routeName === 'warehouse'),
  true,
  'Warehouse.ManageProducts should show the HbwebExpo warehouse tab',
)

const superAdminPreviewAccess = buildRolePreviewAccess({
  roleGuid: 'super-admin-role',
  roleName: 'CustomSuperAdmin',
  isSuperAdmin: true,
  implicitAllPermissions: true,
  explicitPermissionCodes: [],
  effectivePermissionCodes: [],
})
const superAdminExpoPreview = buildExpoRoleMenuPreview(superAdminPreviewAccess, translate)

assertEqual(
  superAdminExpoPreview.visibleRoutes.some((route) => route.routeName === 'device-management'),
  true,
  'Super admin role preview should show protected HbwebExpo app tabs',
)

assertEqual(
  superAdminExpoPreview.visibleRoutes.length > orderCreatorExpoPreview.visibleRoutes.length,
  true,
  'Super admin role preview should show more HbwebExpo app tabs than a limited order role',
)

const roleReaderWebPreview = buildWebRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'role-reader-role',
    roleName: 'RoleReaderRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.Roles.View],
    effectivePermissionCodes: [P.Roles.View],
  }),
  translate,
)
const systemMenu = roleReaderWebPreview.find((node) => node.path === '/system')
const rolesMenu = systemMenu?.children?.find((node) => node.path === '/system/roles')
const adminWebMenuPreview = buildWebRoleMenuPreview(adminAccess, translate, {
  includeHidden: true,
})
const productStatisticMenu = findWebMenuNode(adminWebMenuPreview, '/executive-sales-intelligence/product-statistics')
const compactSalesBoardMenu = findWebMenuNode(adminWebMenuPreview, '/executive-sales-intelligence/compact-sales-board')
const monthlyDailySalesDownloadMenu = findWebMenuNode(adminWebMenuPreview, '/executive-sales-intelligence/monthly-daily-sales-download')
const productMovementReportOnlyWebPreview = buildWebRoleMenuPreview(productMovementReportOnlyAccess, translate, {
  includeHidden: true,
})
const productMovementParentMenu = findWebMenuNode(productMovementReportOnlyWebPreview, '/executive-sales-intelligence')
const productMovementReportMenu = findWebMenuNode(
  productMovementReportOnlyWebPreview,
  '/executive-sales-intelligence/product-movement-report',
)
const hiddenSalesDetailForProductMovementOnly = findWebMenuNode(
  productMovementReportOnlyWebPreview,
  '/executive-sales-intelligence/sales-detail-v2',
)
const hiddenCompactSalesBoardForProductMovementOnly = findWebMenuNode(
  productMovementReportOnlyWebPreview,
  '/executive-sales-intelligence/compact-sales-board',
)
const hiddenMonthlyDailySalesDownloadForProductMovementOnly = findWebMenuNode(
  productMovementReportOnlyWebPreview,
  '/executive-sales-intelligence/monthly-daily-sales-download',
)
const localPurchaseOnlyWebPreview = buildWebRoleMenuPreview(localPurchaseDashboardOnlyAccess, translate, {
  includeHidden: true,
})
const localPurchaseSalesParentMenu = findWebMenuNode(localPurchaseOnlyWebPreview, '/executive-sales-intelligence')
const purchaseAmountDashboardMenu = findWebMenuNode(
  localPurchaseOnlyWebPreview,
  '/executive-sales-intelligence/purchase-amount-dashboard',
)
const hiddenSalesOverviewForLocalPurchaseOnly = findWebMenuNode(
  localPurchaseOnlyWebPreview,
  '/executive-sales-intelligence/overview',
)
const roleReaderCompleteWebPreview = buildWebRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'role-reader-role',
    roleName: 'RoleReaderRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.Roles.View],
    effectivePermissionCodes: [P.Roles.View],
  }),
  translate,
  {
    includeHidden: true,
  },
)
const completeSystemMenu = roleReaderCompleteWebPreview.find((node) => node.path === '/system')
const scheduledStatisticsMenu = completeSystemMenu?.children?.find((node) => node.path === '/system/scheduled-statistics')
const invoiceEmailSettingsMenu = completeSystemMenu?.children?.find((node) => node.path === '/system/invoice-email-settings')
const roleReaderVisibleWebPreview = filterWebMenuNodesByVisibility(roleReaderCompleteWebPreview, 'visible')
const roleReaderHiddenWebPreview = filterWebMenuNodesByVisibility(roleReaderCompleteWebPreview, 'hidden')

const systemSettingsAccess = buildAccess(
  createCurrentUser({
    permissions: [P.System.ManageSettings],
  }),
)

assertEqual(
  systemSettingsAccess.canManageSystemSettings,
  true,
  'System.ManageSettings should unlock invoice email settings page visibility',
)

assertEqual(
  systemMenu?.permissionCodes.length,
  0,
  'Parent Web menus without direct accessKey should keep an empty permission list',
)

assertEqual(
  rolesMenu?.permissionCodes.join(','),
  P.Roles.View,
  'Roles.View role preview should show Roles.View on the system roles Web menu',
)

assertEqual(
  rolesMenu?.accessKey,
  'canReadRole',
  'Web menu preview should expose the accessKey that controls the system roles menu',
)

assertEqual(
  getAccessKeyPermissionCodes('canManageScheduledTasks').join(','),
  P.System.ManageScheduledTasks,
  'Web menu preview should map scheduled task management access to System.ManageScheduledTasks',
)

assertEqual(
  scheduledStatisticsMenu?.accessKey,
  'canManageScheduledTasks',
  '定时统计任务 Web 菜单应由 System.ManageScheduledTasks 控制',
)

assertEqual(
  scheduledStatisticsMenu?.permissionCodes.join(','),
  P.System.ManageScheduledTasks,
  '定时统计任务 Web 菜单应展示 System.ManageScheduledTasks 权限',
)

assertEqual(
  getAccessKeyPermissionCodes('canManageSystemSettings').join(','),
  P.System.ManageSettings,
  'Web menu preview should map system settings access to System.ManageSettings',
)

assertEqual(
  getAccessKeyPermissionCodes('canReadUserConsole').join(','),
  P.Users.ViewWebConsole,
  'Web menu preview should map the system users menu to Users.ViewWebConsole only',
)

assertEqual(
  getAccessKeyPermissionCodes('canViewAppDownloads').join(','),
  `${P.System.ViewAppDownloads},${P.System.ManageAppDownloads}`,
  'Web menu preview should map App download access to both view and manage permissions',
)

assertEqual(
  invoiceEmailSettingsMenu?.accessKey,
  'canManageSystemSettings',
  '发票邮箱配置 Web 菜单应由 System.ManageSettings 控制',
)

assertEqual(
  invoiceEmailSettingsMenu?.permissionCodes.join(','),
  P.System.ManageSettings,
  '发票邮箱配置 Web 菜单应展示 System.ManageSettings 权限',
)

assertEqual(
  Boolean(productStatisticMenu),
  false,
  '销售看板 Web 菜单不应再显示旧商品统计状态入口',
)

assertEqual(
  compactSalesBoardMenu?.visible,
  true,
  '有 Reports.View 时应显示独立销售看板入口',
)

assertEqual(
  compactSalesBoardMenu?.permissionCodes.join(','),
  P.SalesDashboard.CompactBoardView,
  '独立销售看板应仅要求本页查看权限',
)

assertEqual(
  monthlyDailySalesDownloadMenu?.visible,
  true,
  '管理员应显示月度日销售下载入口',
)

assertEqual(
  monthlyDailySalesDownloadMenu?.permissionCodes.join(','),
  P.SalesDashboard.MonthlyDailySalesDownloadView,
  '月度日销售下载应仅要求本页查看权限',
)

assertEqual(
  productMovementParentMenu?.visible,
  true,
  '只有商品经营分析权限时销售看板父菜单应可见',
)

assertEqual(
  productMovementReportMenu?.visible,
  true,
  '只有商品经营分析权限时商品经营分析菜单应可见',
)

assertEqual(
  hiddenSalesDetailForProductMovementOnly?.visible,
  false,
  '只有商品经营分析权限时不应打开销售明细菜单',
)

assertEqual(
  hiddenCompactSalesBoardForProductMovementOnly?.visible,
  false,
  '只有商品经营分析权限时不应打开独立销售看板',
)

assertEqual(
  hiddenMonthlyDailySalesDownloadForProductMovementOnly?.visible,
  false,
  '只有商品经营分析权限时不应打开月度日销售下载',
)

assertEqual(
  localPurchaseSalesParentMenu?.visible,
  false,
  '只有本地进货权限时销售看板父菜单应隐藏',
)

assertEqual(
  purchaseAmountDashboardMenu?.visible,
  false,
  '本地进货权限不应显示进货金额看板',
)

assertEqual(
  purchaseAmountDashboardMenu?.permissionCodes.join(','),
  P.SalesDashboard.PurchaseAmountView,
  '进货金额看板应单独配置页面查看权限',
)

assertEqual(
  hiddenSalesOverviewForLocalPurchaseOnly?.visible,
  false,
  '只有 LocalPurchase.View 时不应打开销售数据页面',
)

assertEqual(
  filterWebMenuNodesByVisibility(roleReaderCompleteWebPreview, 'all').length,
  roleReaderCompleteWebPreview.length,
  'All Web menu filter should keep the complete desktop menu tree',
)

assertEqual(
  Boolean(findWebMenuNode(roleReaderVisibleWebPreview, '/system')),
  true,
  'Visible Web menu filter should keep parent context for authorized child menus',
)

assertEqual(
  Boolean(findWebMenuNode(roleReaderVisibleWebPreview, '/system/roles')),
  true,
  'Visible Web menu filter should include authorized desktop menu entries',
)

assertEqual(
  Boolean(findWebMenuNode(roleReaderHiddenWebPreview, '/system')),
  true,
  'Hidden Web menu filter should keep parent context for hidden child menus',
)

assertEqual(
  Boolean(findWebMenuNode(roleReaderHiddenWebPreview, '/system/stores')),
  true,
  'Hidden Web menu filter should include unauthorized desktop menu entries',
)

assertEqual(
  Boolean(findWebMenuNode(roleReaderHiddenWebPreview, '/system/roles')),
  false,
  'Hidden Web menu filter should exclude authorized desktop menu entries',
)

const warehouseManageCodes = getAccessKeyPermissionCodes('canManageWarehouseProducts')

assertEqual(
  warehouseManageCodes.join(','),
  `${P.Warehouse.ManageProducts},${P.Warehouse.Manage}`,
  'Warehouse product Web menu should document both fine-grained and legacy manager permissions',
)

const warehouseLegacyWebPreview = buildWebRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'warehouse-legacy-role',
    roleName: 'WarehouseLegacyRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.Warehouse.Manage],
    effectivePermissionCodes: [P.Warehouse.Manage],
  }),
  translate,
)
const warehouseProductsMenu = warehouseLegacyWebPreview
  .find((node) => node.path === '/warehouse')
  ?.children?.find((node) => node.path === '/warehouse/products')
const warehouseLegacyPosAdminMenu = warehouseLegacyWebPreview.find((node) => node.path === '/pos-admin')
const warehouseLegacySalesOrdersMenu = findWebMenuNode(warehouseLegacyWebPreview, '/pos-admin/sales-orders')

assertEqual(
  warehouseProductsMenu?.permissionCodes.join(','),
  `${P.Warehouse.ManageProducts},${P.Warehouse.Manage}`,
  'Warehouse.Manage role preview should show the warehouse products Web menu with both accepted permissions',
)

assertEqual(
  Boolean(warehouseLegacyPosAdminMenu),
  false,
  'Warehouse.Manage role preview should not show POS admin parent menu without visible POS children',
)

assertEqual(
  Boolean(warehouseLegacySalesOrdersMenu),
  false,
  'Warehouse.Manage role preview should not show cashier records without Orders.View',
)

const warehouseStaffWebPreview = buildWebRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'warehouse-staff-role',
    roleName: 'WarehouseStaff',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.Dashboard.View, P.Warehouse.Manage, P.Orders.View],
    effectivePermissionCodes: [P.Dashboard.View, P.Warehouse.Manage, P.Orders.View],
  }),
  translate,
)
const warehouseStaffMenu = warehouseStaffWebPreview.find((node) => node.path === '/warehouse')

assertEqual(
  warehouseStaffWebPreview.map((node) => node.path).join(','),
  '/warehouse',
  'WarehouseStaff desktop preview should only show the warehouse parent menu',
)

assertEqual(
  warehouseStaffMenu?.children?.map((node) => node.path).join(','),
  '/warehouse/store-orders',
  'WarehouseStaff desktop preview should only show the store order list under warehouse',
)

assertEqual(
  Boolean(findWebMenuNode(warehouseStaffWebPreview, '/dashboard')),
  false,
  'WarehouseStaff desktop preview should not show dashboard navigation',
)

assertEqual(
  Boolean(findWebMenuNode(warehouseStaffWebPreview, '/warehouse/products')),
  false,
  'WarehouseStaff desktop preview should not show warehouse products navigation',
)

assertEqual(
  Boolean(findWebMenuNode(warehouseStaffWebPreview, '/pos-admin/sales-orders')),
  false,
  'WarehouseStaff desktop preview should not show cashier records navigation',
)

const activationCodeOnlyAccess = buildAccess(
  createCurrentUser({
    permissions: [P.DeviceRegistration.ActivationCodesManage],
  }),
)
const activationCodeOnlyPreview = buildWebRoleMenuPreview(activationCodeOnlyAccess, translate)

const mobileActivationCodeOnlyAccess = buildAccess(
  createCurrentUser({
    permissions: [P.DeviceRegistration.MobileActivationCodesManage],
  }),
)
const mobileActivationCodeOnlyPreview = buildWebRoleMenuPreview(
  mobileActivationCodeOnlyAccess,
  translate,
)

assertEqual(
  activationCodeOnlyAccess.canViewDeviceRegistration,
  true,
  'Activation-code managers should be able to enter the device registration page',
)
assertEqual(
  activationCodeOnlyAccess.canManageDeviceRegistration,
  false,
  'Activation-code permission must not grant legacy device management',
)
assertEqual(
  activationCodeOnlyAccess.canAccessAdminShell,
  true,
  'Activation-code managers should be allowed into the backend shell',
)
assertEqual(
  Boolean(findWebMenuNode(activationCodeOnlyPreview, '/system/device-registration')),
  true,
  'Activation-code managers should see the protected device registration menu',
)
assertEqual(
  mobileActivationCodeOnlyAccess.canManageMobileDeviceActivationCodes,
  true,
  'Mobile activation permission should unlock only the Mobile activation actions',
)
assertEqual(
  mobileActivationCodeOnlyAccess.canManageDeviceRegistration,
  false,
  'Mobile activation permission must not grant legacy device management',
)
assertEqual(
  mobileActivationCodeOnlyAccess.canViewDeviceRegistration,
  true,
  'Mobile activation managers should be able to enter the device registration page',
)
assertEqual(
  mobileActivationCodeOnlyAccess.canAccessAdminShell,
  true,
  'Mobile activation managers should be allowed into the backend shell',
)
assertEqual(
  Boolean(findWebMenuNode(mobileActivationCodeOnlyPreview, '/system/device-registration')),
  true,
  'Mobile activation managers should see the protected device registration menu',
)

const superAdminWebPreview = buildWebRoleMenuPreview(superAdminPreviewAccess, translate)
const superAdminDeviceMenu = superAdminWebPreview
  .find((node) => node.path === '/system')
  ?.children?.find((node) => node.path === '/system/device-registration')

assertEqual(
  superAdminDeviceMenu?.permissionCodes.join(','),
  `${P.DeviceRegistration.View},${P.DeviceRegistration.Manage},${P.DeviceRegistration.ActivationCodesManage},${P.DeviceRegistration.MobileActivationCodesManage}`,
  'Super admin Web preview should show protected menus with their normal permission codes',
)

const advertisementViewerAccess = buildAccess(
  createCurrentUser({
    permissions: [P.Advertisements.View],
  }),
)
const advertisementEditorOnlyAccess = buildAccess(
  createCurrentUser({
    permissions: [P.Advertisements.Edit],
  }),
)

assertEqual(
  advertisementViewerAccess.canManageAdvertisements,
  true,
  'Advertisements.View should unlock advertisement management page visibility',
)

assertEqual(
  advertisementEditorOnlyAccess.canManageAdvertisements,
  false,
  'Advertisements.Edit alone should not unlock advertisement list visibility',
)

assertEqual(
  advertisementViewerAccess.canEditAdvertisements,
  false,
  'Advertisements.View alone should not unlock advertisement editing',
)

const advertisementManageCodes = getAccessKeyPermissionCodes('canManageAdvertisements')

assertEqual(
  advertisementManageCodes.join(','),
  P.Advertisements.View,
  'Advertisement Web menu should document the view permission required by backend read APIs',
)

const advertisementPreview = buildWebRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'advertisement-role',
    roleName: 'AdvertisementRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: [P.Advertisements.View],
    effectivePermissionCodes: [P.Advertisements.View],
  }),
  translate,
)

const advertisementMenu = advertisementPreview
  .find((node) => node.path === '/pos-admin')
  ?.children?.find((node) => node.path === '/pos-admin/advertisements')

assertEqual(
  advertisementMenu?.permissionCodes.join(','),
  P.Advertisements.View,
  'Advertisements.View role preview should show the advertisement menu with backend read permission',
)

const legacyLocalInvoiceWebPreview = buildWebRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'legacy-local-invoice-role',
    roleName: 'LegacyLocalInvoiceRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: ['LocalInvocie.View'],
    effectivePermissionCodes: ['LocalInvocie.View', P.LocalPurchase.View],
  }),
  translate,
  {
    includeHidden: true,
    explicitPermissionCodes: ['LocalInvocie.View'],
  },
)
const legacyLocalInvoiceMenu = legacyLocalInvoiceWebPreview
  .find((node) => node.path === '/pos-admin')
  ?.children?.find((node) => node.path === '/pos-admin/local-supplier-invoices')

assertEqual(
  legacyLocalInvoiceMenu?.edit.removePermissionCodes.includes('LocalInvocie.View'),
  true,
  'Web preview remove action should include legacy LocalInvocie.View so old roles can hide the menu',
)

const legacyLocalInvoiceRemovedPermissions = applyRolePermissionMutation({
  currentPermissionCodes: ['LocalInvocie.View'],
  removePermissionCodes: legacyLocalInvoiceMenu?.edit.removePermissionCodes ?? [],
})

assertEqual(
  legacyLocalInvoiceRemovedPermissions.length,
  0,
  'Removing the legacy local invoice menu should delete the effective legacy permission assignment',
)

const hiddenLocalInvoiceAfterLegacyRemoval = filterWebMenuNodesByVisibility(
  buildWebRoleMenuPreview(
    buildRolePreviewAccess({
      roleGuid: 'legacy-local-invoice-removed-role',
      roleName: 'LegacyLocalInvoiceRemovedRole',
      isSuperAdmin: false,
      implicitAllPermissions: false,
      explicitPermissionCodes: legacyLocalInvoiceRemovedPermissions,
      effectivePermissionCodes: legacyLocalInvoiceRemovedPermissions,
    }),
    translate,
    {
      includeHidden: true,
      explicitPermissionCodes: legacyLocalInvoiceRemovedPermissions,
    },
  ),
  'hidden',
)

assertEqual(
  Boolean(findWebMenuNode(hiddenLocalInvoiceAfterLegacyRemoval, '/pos-admin/local-supplier-invoices')),
  true,
  'Hidden Web menu filter should include local supplier invoices after removing the legacy LocalInvocie.View alias',
)

const legacyLocalInvoiceExpoPreview = buildExpoRoleMenuPreview(
  buildRolePreviewAccess({
    roleGuid: 'legacy-local-invoice-expo-role',
    roleName: 'LegacyLocalInvoiceExpoRole',
    isSuperAdmin: false,
    implicitAllPermissions: false,
    explicitPermissionCodes: ['LocalInvocie.View'],
    effectivePermissionCodes: ['LocalInvocie.View', P.LocalPurchase.View],
  }),
  translate,
  {
    explicitPermissionCodes: ['LocalInvocie.View'],
  },
)
const legacyLocalInvoiceExpoRoute = legacyLocalInvoiceExpoPreview.allRoutes.find(
  (route) => route.routeName === 'local-supplier-invoices',
)

assertEqual(
  legacyLocalInvoiceExpoRoute?.removePermissionCodes.includes('LocalInvocie.View'),
  true,
  'HbwebExpo preview remove action should include legacy LocalInvocie.View so old roles can hide the menu',
)

assertEqual(
  applyRolePermissionMutation({
    currentPermissionCodes: ['LocalInvocie.View'],
    removePermissionCodes: legacyLocalInvoiceExpoRoute?.removePermissionCodes ?? [],
  }).length,
  0,
  'Removing the legacy local invoice HbwebExpo menu should delete the effective legacy permission assignment',
)

const localPurchasePushAccess = buildAccess(
  createCurrentUser({
    permissions: [P.LocalPurchase.PushToHq],
  }),
)

assertEqual(
  localPurchasePushAccess.canPushLocalPurchaseToHq,
  true,
  'LocalPurchase.PushToHq should expose the HQ write permission flag',
)

assertEqual(
  localPurchasePushAccess.canEditLocalPurchase && localPurchasePushAccess.canPushLocalPurchaseToHq,
  false,
  'LocalPurchase.PushToHq alone should not be treated as full edit-page HQ write access',
)

const localPurchaseViewOnlyAccess = buildAccess(
  createCurrentUser({
    permissions: [P.LocalPurchase.View],
  }),
)

assertEqual(
  localPurchaseViewOnlyAccess.canPushLocalPurchaseToHq,
  false,
  'LocalPurchase.View should not unlock HQ write actions',
)

// --- 精确权限契约：商品销量分析只读 exactPermissions，不展开别名 ---
const exactProductMovementAnalysisAccess = buildAccess(
  createCurrentUser({
    permissions: [P.Reports.ProductMovementView],
    exactPermissions: [P.Reports.ProductMovementView],
  }),
)
assertEqual(
  exactProductMovementAnalysisAccess.canViewProductSalesAnalysis,
  true,
  'exactPermissions 含 Reports.ProductMovement.View 时应允许商品销量分析',
)

const exactReportsViewOnlyAccess = buildAccess(
  createCurrentUser({
    permissions: [P.Reports.ProductMovementView, P.Reports.View],
    exactPermissions: [P.Reports.View],
  }),
)
assertEqual(
  exactReportsViewOnlyAccess.canViewProductSalesAnalysis,
  false,
  'exactPermissions 只有 Reports.View 时应拒绝商品销量分析，即使展开 permissions 已含 ProductMovementView',
)
assertEqual(
  exactReportsViewOnlyAccess.canViewProductMovementReport,
  false,
  '旧报表权限不再放开商品经营分析页面',
)

const exactPermissionsMissingAccess = buildAccess(
  createCurrentUser({
    permissions: [P.Reports.ProductMovementView],
  }),
)
assertEqual(
  exactPermissionsMissingAccess.canViewProductSalesAnalysis,
  false,
  'exactPermissions 字段缺失时非管理员应拒绝商品销量分析',
)

const superAdminExactMissingAccess = buildAccess(
  createCurrentUser({
    roleNames: ['超级管理员'],
    permissions: [],
    exactPermissions: [],
  }),
)
assertEqual(
  superAdminExactMissingAccess.canViewProductSalesAnalysis,
  true,
  '超级管理员别名即使 exactPermissions 为空也应允许商品销量分析',
)

// --- 角色预览精确权限契约：exactPermissions 必须来自原始未展开的 explicitPermissionCodes ---
const productMovementExactPreviewAccess = buildRolePreviewAccess({
  roleGuid: 'product-movement-exact-role',
  roleName: 'ProductMovementExactRole',
  isSuperAdmin: false,
  implicitAllPermissions: false,
  explicitPermissionCodes: [P.Reports.ProductMovementView],
  effectivePermissionCodes: [P.Reports.ProductMovementView],
})
assertEqual(
  productMovementExactPreviewAccess.canViewProductSalesAnalysis,
  true,
  '角色预览 explicit 含 Reports.ProductMovement.View 时应允许商品销量分析',
)
assertEqual(
  productMovementExactPreviewAccess.canViewProductMovementReport,
  false,
  '角色预览旧商品报表权限不应放开商品经营分析页面',
)

const reportsViewExpandedPreviewAccess = buildRolePreviewAccess({
  roleGuid: 'reports-view-expanded-role',
  roleName: 'ReportsViewExpandedRole',
  isSuperAdmin: false,
  implicitAllPermissions: false,
  explicitPermissionCodes: [P.Reports.View],
  effectivePermissionCodes: [P.Reports.View, P.Reports.ProductMovementView],
})
assertEqual(
  reportsViewExpandedPreviewAccess.canViewProductSalesAnalysis,
  false,
  '角色预览 explicit 只有 Reports.View 时应拒绝商品销量分析，即使 effective 已展开含 ProductMovementView',
)
assertEqual(
  reportsViewExpandedPreviewAccess.canViewProductMovementReport,
  false,
  '角色预览旧报表权限展开不应放开商品经营分析页面',
)

const superAdminExactEmptyPreviewAccess = buildRolePreviewAccess({
  roleGuid: 'super-admin-exact-empty-role',
  roleName: 'CustomSuperAdmin',
  isSuperAdmin: true,
  implicitAllPermissions: true,
  explicitPermissionCodes: [],
  effectivePermissionCodes: [],
})
assertEqual(
  superAdminExactEmptyPreviewAccess.canViewProductSalesAnalysis,
  true,
  '超级管理员角色预览即使 explicit 为空也应允许商品销量分析',
)

// 订货前台货号销量：只认前台权限码，不放开后台入口；历史地址按页面权限核验。
const shopBatchSalesOrderAccess = buildAccess(
  createCurrentUser({
    roleNames: ['订货员'],
    permissions: [P.OrderFront.View, P.OrderFront.BatchProductSalesView],
  }),
)
assertEqual(shopBatchSalesOrderAccess.canViewShopBatchProductSales, true, '订货员获前台货号销量权限后应可查看该页')
assertEqual(shopBatchSalesOrderAccess.canViewBatchProductSalesAnalysis, false, '前台货号销量权限不能放行后台批量货号销量页')
assertEqual(shopBatchSalesOrderAccess.canAccessAdminShell, false, '前台货号销量权限不能让订货员进入后台')
assertEqual(getDefaultWebPath(shopBatchSalesOrderAccess), '/shop', '持有前台货号销量权限的订货员仍默认进入订货前台')
assertEqual(
  resolveAuthorizedWebTarget('/shop/batch-product-sales', shopBatchSalesOrderAccess),
  '/shop/batch-product-sales',
  '已授权时应保留货号销量页历史地址',
)

const shopWithoutBatchSalesAccess = buildAccess(
  createCurrentUser({ roleNames: ['订货员'], permissions: [P.OrderFront.View] }),
)
assertEqual(shopWithoutBatchSalesAccess.canViewShopBatchProductSales, false, '未授权的订货员不应看到货号销量入口')
assertEqual(
  resolveAuthorizedWebTarget('/shop/batch-product-sales?x=1', shopWithoutBatchSalesAccess),
  undefined,
  '纯订货角色未授权时不应保留货号销量历史地址',
)
assertEqual(getDefaultWebPath(shopWithoutBatchSalesAccess), '/shop', '未授权的纯订货角色默认落点仍是订货前台首页')

const backendOnlyBatchSalesAccess = buildAccess(
  createCurrentUser({ roleNames: ['User'], permissions: [P.SalesDashboard.BatchProductSalesView] }),
)
assertEqual(backendOnlyBatchSalesAccess.canViewShopBatchProductSales, false, '后台批量货号销量权限不能反向放行前台页面')
assertEqual(
  resolveAuthorizedWebTarget('/shop/batch-product-sales', backendOnlyBatchSalesAccess),
  undefined,
  '没有前台权限时不得进入前台货号销量页',
)

const orderFrontWithBackendBatchSalesAccess = buildAccess(
  createCurrentUser({ roleNames: ['User'], permissions: [P.OrderFront.View, P.SalesDashboard.BatchProductSalesView] }),
)
assertEqual(
  resolveAuthorizedWebTarget('/shop/batch-product-sales', orderFrontWithBackendBatchSalesAccess),
  undefined,
  '可进前台但缺少前台货号销量权限时，历史地址不应放行',
)

// 分店现金管理：只认 Cash.Overview.View；页面权限即可点亮 /pos-admin 父菜单并进入后台，
// 从角色菜单授予时只补本页权限，不连带授予工作台（给店长开现金管理不应开放全公司看板）。
const storeCashOnlyAccess = buildAccess(
  createCurrentUser({ roleNames: ['StoreManager'], permissions: [P.Cash.OverviewView] }),
)
assertEqual(storeCashOnlyAccess.canViewStoreCash, true, 'Cash.Overview.View 应放开现金管理页')
assertEqual(storeCashOnlyAccess.canAccessDashboard, false, '现金管理权限不应放开工作台')
assertEqual(storeCashOnlyAccess.canAccessAdminShell, true, '只有现金管理权限也应能进入后台')
assertEqual(getDefaultWebPath(storeCashOnlyAccess), '/pos-admin/store-cash', '只有现金管理权限时默认落到现金管理页')
assertEqual(
  resolveAuthorizedWebTarget('/pos-admin/store-cash?tab=expenses&store=S001', storeCashOnlyAccess),
  '/pos-admin/store-cash?tab=expenses&store=S001',
  '现金管理页带筛选参数的地址应保留',
)
assertEqual(
  resolveAuthorizedWebTarget('/pos-admin/local-supplier-invoices', storeCashOnlyAccess),
  undefined,
  '现金管理权限不应放行其他后台页面',
)
for (const permission of [P.Cash.DepositCreate, P.Cash.ExpenseCreate, P.Cash.Void, P.Cash.AllStoresView]) {
  const access = buildAccess(createCurrentUser({ permissions: [permission] }))
  assertEqual(access.canViewStoreCash, false, `${permission} 单独不应放开现金管理页`)
}
assertEqual(buildAccess(createCurrentUser({ roleNames: ['Admin'] })).canViewStoreCash, true, '管理员应能看到现金管理页')

const storeCashOnlyPreview = buildWebRoleMenuPreview(storeCashOnlyAccess, translate, {
  includeHidden: true,
  explicitPermissionCodes: [P.Cash.OverviewView],
})
const storeCashMenu = findWebMenuNode(storeCashOnlyPreview, '/pos-admin/store-cash')
assertEqual(storeCashMenu?.visible, true, '菜单预览应显示现金管理')
assertEqual(storeCashOnlyPreview.find((node) => node.path === '/pos-admin')?.visible, true, '现金管理应点亮 /pos-admin 父菜单')
assertEqual(storeCashMenu?.permissionCodes.join(','), P.Cash.OverviewView, '现金管理菜单只承载 Cash.Overview.View')
assertEqual(getAccessKeyPermissionCodes('canViewStoreCash').join(','), P.Cash.OverviewView, '访问键映射到 Cash.Overview.View')
const storeCashAddPreview = buildWebRoleMenuPreview(buildAccess(createCurrentUser({ permissions: [] })), translate, {
  includeHidden: true,
  explicitPermissionCodes: [],
})
assertEqual(
  findWebMenuNode(storeCashAddPreview, '/pos-admin/store-cash')?.edit.addPermissionCodes.join(','),
  P.Cash.OverviewView,
  '从菜单授予现金管理只补本页权限，不连带授予工作台',
)
assertEqual(
  findWebMenuNode(storeCashAddPreview, '/pos-admin/promotions')?.edit.addPermissionCodes.join(','),
  [P.Promotions.View, P.Dashboard.View].join(','),
  '其他后台页面从菜单授予时仍连带授予工作台（行为不变）',
)

console.log('access.permission.test: ok')
