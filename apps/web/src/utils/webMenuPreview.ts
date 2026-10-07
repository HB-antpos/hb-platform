import type { AccessControl } from '../types/auth'
import { P } from '../types/permissions'

export type WebMenuPreviewTranslate = (key: string, fallback?: string) => string

interface WebMenuPreviewRoute {
  path: string
  title: string
  accessKey?: keyof AccessControl
  children?: WebMenuPreviewRoute[]
}

export interface WebMenuPreviewNode {
  key: string
  title: string
  path: string
  accessKey?: keyof AccessControl
  permissionCodes: string[]
  visible: boolean
  edit: {
    canAdd: boolean
    canRemove: boolean
    isReadOnly: boolean
    isFixed: boolean
    addPermissionCodes: string[]
    removePermissionCodes: string[]
    reason?: 'group' | 'read-only' | 'fixed'
  }
  children?: WebMenuPreviewNode[]
}

export type WebMenuVisibilityFilter = 'all' | 'visible' | 'hidden'

interface BuildWebRoleMenuPreviewOptions {
  includeHidden?: boolean
  explicitPermissionCodes?: string[]
  readOnly?: boolean
}

const accessKeyPermissionMap: Partial<Record<keyof AccessControl, string[]>> = {
  isAdmin: [],
  canAccessDashboard: [P.Dashboard.View],
  canAccessOrderFront: [P.OrderFront.View],
  canReadStore: [P.Stores.View],
  canViewEmployeeProfiles: [P.EmployeeProfiles.View],
  canViewSystemLogs: [P.System.ViewLogs],
  canViewPerformanceBaseline: [P.System.ViewPerformanceBaseline],
  canManageScheduledTasks: [P.System.ManageScheduledTasks],
  canManageSystemSettings: [P.System.ManageSettings],
  canViewAppDownloads: [P.System.ViewAppDownloads, P.System.ManageAppDownloads],
  canReadUserConsole: [P.Users.ViewWebConsole],
  canReadRole: [P.Roles.View],
  canViewDeviceRegistration: [
    P.DeviceRegistration.View,
    P.DeviceRegistration.Manage,
    P.DeviceRegistration.ActivationCodesManage,
    P.DeviceRegistration.MobileActivationCodesManage,
  ],
  canManageDomesticSuppliers: [P.DomesticPurchase.ManageSuppliers],
  canReadProduct: [P.Products.View],
  canManageDomesticPrefixCodes: [P.DomesticPurchase.ManagePrefixCodes],
  canManageDomesticProducts: [P.DomesticPurchase.ManageProducts],
  canManageWarehouseOrders: [P.Warehouse.ManageOrders, P.Warehouse.Manage],
  canManageStoreOrderImportPriceVariance: [P.Warehouse.ManageOrders, P.Warehouse.Manage],
  canViewContainers: [P.Container.View],
  canManageWarehouseProducts: [P.Warehouse.ManageProducts, P.Warehouse.Manage],
  canManageWarehouseCategories: [P.Warehouse.ManageCategories, P.Warehouse.Manage],
  canManageWarehouseLocations: [P.Warehouse.ManageLocations, P.Warehouse.Manage],
  canViewReports: [P.Reports.View],
  canViewSalesIntelligence: [],
  canViewSalesData: [P.SalesDashboard.SalesDataView],
  canViewSalesDetail: [P.SalesDashboard.SalesDetailView],
  canViewCompactSalesBoard: [P.SalesDashboard.CompactBoardView],
  canViewMonthlyDailySalesDownload: [P.SalesDashboard.MonthlyDailySalesDownloadView],
  canViewProductMovementReport: [P.SalesDashboard.ProductMovementView],
  canViewBatchProductSalesAnalysis: [P.SalesDashboard.BatchProductSalesView],
  canViewWarehouseProductFlowAnalysis: [P.SalesDashboard.WarehouseFlowView],
  canViewLocalProductSalesAnalysis: [P.SalesDashboard.LocalProductAnalysisView],
  canViewPurchaseAmountDashboard: [P.SalesDashboard.PurchaseAmountView],
  canViewLocalSupplierPurchaseSalesAnalysis: [P.SalesDashboard.LocalSupplierPurchaseSalesView],
  // 合并页任一权限可见；从菜单授予时只补第一个（批量货号销量），移除时两个标签权限一并撤销。
  canViewPurchaseSalesAnalysis: [P.SalesDashboard.BatchProductSalesView, P.SalesDashboard.LocalSupplierPurchaseSalesView],
  canViewProductSalesAnalysis: [P.Reports.ProductMovementView],
  canViewAustralianSuppliers: [P.AustralianSuppliers.View],
  canViewPosProducts: [P.PosProducts.View, P.PosProducts.Manage],
  canManageStoreProducts: [P.StoreProducts.View],
  canManagePricing: [P.PricingStrategy.View],
  canManagePromotions: [P.Promotions.View],
  canManageAdvertisements: [P.Advertisements.View],
  canViewAttendanceSchedule: [P.Attendance.AdminView, P.Attendance.ScheduleViewStore],
  canManageStoreOps: [P.Store.ManageOperations],
  canViewOperationAudits: [P.PosTerminal.AuditView],
  canViewLegacyEmployeeLogs: [P.LegacyEmployeeLogs.View],
  // 合并页任一权限可见；从菜单授予时只补第一个（老收银查看），移除时两个来源权限一并撤销。
  canViewEmployeeOperationLogs: [P.LegacyEmployeeLogs.View, P.PosTerminal.AuditView],
  canViewDailyCloseRecords: [P.DailyCloseRecords.View],
  canReadOrder: [P.Orders.View],
  canManageLocalPurchase: [P.LocalPurchase.View, 'LocalInvocie.View'],
  canEditLocalPurchase: [P.LocalPurchase.Edit, 'LocalInvocie.Edit'],
}

const webMenuPreviewRoutes: WebMenuPreviewRoute[] = [
  { path: '/dashboard', title: 'menu.dashboard', accessKey: 'canAccessDashboard' },
  {
    path: '/system',
    title: 'menu.system',
    children: [
      { path: '/system/stores', title: 'menu.systemStores', accessKey: 'canReadStore' },
      { path: '/system/employee-profiles', title: 'menu.systemEmployeeProfiles', accessKey: 'canViewEmployeeProfiles' },
      { path: '/system/center-logs', title: 'menu.systemCenterLogs', accessKey: 'canViewSystemLogs' },
      { path: '/system/performance-baseline', title: 'menu.performanceBaseline', accessKey: 'canViewPerformanceBaseline' },
      { path: '/system/scheduled-statistics', title: 'menu.scheduledStatistics', accessKey: 'canManageScheduledTasks' },
      { path: '/system/invoice-email-settings', title: 'menu.invoiceEmailSettings', accessKey: 'canManageSystemSettings' },
      { path: '/system/payment-terminal-settings', title: 'menu.paymentTerminalSettings', accessKey: 'canManageSystemSettings' },
      { path: '/system/emergency-login-keys', title: 'menu.emergencyLoginKeys', accessKey: 'canManageSystemSettings' },
      { path: '/system/users', title: 'menu.systemUsers', accessKey: 'canReadUserConsole' },
      { path: '/system/roles', title: 'menu.systemRoles', accessKey: 'canReadRole' },
      { path: '/system/permissions', title: 'menu.systemPermissions', accessKey: 'canReadRole' },
      { path: '/system/device-registration', title: 'menu.deviceRegistration', accessKey: 'canViewDeviceRegistration' },
      { path: '/system/app-downloads', title: 'menu.appDownloads', accessKey: 'canViewAppDownloads' },
    ],
  },
  {
    path: '/domestic-purchase',
    title: 'menu.domesticPurchase',
    children: [
      { path: '/domestic-purchase/china-suppliers', title: 'menu.chinaSuppliers', accessKey: 'canManageDomesticSuppliers' },
      { path: '/domestic-purchase/domestic-products', title: 'menu.domesticProducts', accessKey: 'canReadProduct' },
      { path: '/domestic-purchase/prefix-code-management', title: 'menu.prefixCodeManagement', accessKey: 'canManageDomesticPrefixCodes' },
      { path: '/domestic-purchase/product-creation', title: 'menu.productCreation', accessKey: 'canManageDomesticProducts' },
      { path: '/domestic-purchase/product-import', title: 'menu.productImport', accessKey: 'canManageDomesticProducts' },
    ],
  },
  {
    path: '/warehouse',
    title: 'menu.warehouse',
    children: [
      { path: '/warehouse/store-orders', title: 'menu.storeOrders', accessKey: 'canManageWarehouseOrders' },
      { path: '/warehouse/store-order-import-price-variance', title: 'menu.storeOrderImportPriceVariance', accessKey: 'canManageStoreOrderImportPriceVariance' },
      { path: '/warehouse/containers', title: 'menu.containers', accessKey: 'canViewContainers' },
      { path: '/warehouse/products', title: 'menu.warehouseProducts', accessKey: 'canManageWarehouseProducts' },
      { path: '/warehouse/categories', title: 'menu.categories', accessKey: 'canManageWarehouseCategories' },
      { path: '/warehouse/locations', title: 'menu.warehouseLocations', accessKey: 'canManageWarehouseLocations' },
      { path: '/warehouse/product-grade-management', title: 'menu.productGradeManagement', accessKey: 'canManageWarehouseProducts' },
    ],
  },
  {
    path: '/executive-sales-intelligence',
    title: 'menu.executiveSalesIntelligence',
    accessKey: 'canViewSalesIntelligence',
    children: [
      { path: '/executive-sales-intelligence/overview', title: 'menu.salesData', accessKey: 'canViewSalesData' },
      { path: '/executive-sales-intelligence/sales-detail-v2', title: 'menu.salesDetail', accessKey: 'canViewSalesDetail' },
      { path: '/executive-sales-intelligence/compact-sales-board', title: 'menu.compactSalesBoard', accessKey: 'canViewCompactSalesBoard' },
      { path: '/executive-sales-intelligence/monthly-daily-sales-download', title: 'menu.monthlyDailySalesDownload', accessKey: 'canViewMonthlyDailySalesDownload' },
      { path: '/executive-sales-intelligence/product-movement-report', title: 'menu.productMovementReport', accessKey: 'canViewProductMovementReport' },
      { path: '/executive-sales-intelligence/warehouse-product-flow-analysis', title: 'menu.warehouseProductFlowAnalysis', accessKey: 'canViewWarehouseProductFlowAnalysis' },
      { path: '/executive-sales-intelligence/purchase-sales-analysis', title: 'menu.purchaseSalesAnalysis', accessKey: 'canViewPurchaseSalesAnalysis' },
      { path: '/executive-sales-intelligence/local-product-sales-analysis', title: 'menu.localProductSalesAnalysis', accessKey: 'canViewLocalProductSalesAnalysis' },
      { path: '/executive-sales-intelligence/purchase-amount-dashboard', title: 'menu.purchaseAmountDashboard', accessKey: 'canViewPurchaseAmountDashboard' },
    ],
  },
  {
    path: '/pos-admin',
    title: 'menu.posAdmin',
    children: [
      { path: '/pos-admin/suppliers', title: 'menu.suppliers', accessKey: 'canViewAustralianSuppliers' },
      { path: '/pos-admin/products', title: 'menu.productManagement', accessKey: 'canViewPosProducts' },
      { path: '/pos-admin/store-product-price', title: 'menu.storeProductPrice', accessKey: 'canManageStoreProducts' },
      { path: '/pos-admin/pricing-strategies', title: 'menu.pricingStrategies', accessKey: 'canManagePricing' },
      { path: '/pos-admin/promotions', title: 'menu.promotions', accessKey: 'canManagePromotions' },
      { path: '/pos-admin/advertisements', title: 'menu.advertisements', accessKey: 'canManageAdvertisements' },
      { path: '/pos-admin/schedule-attendance', title: 'menu.scheduleAttendance', accessKey: 'canViewAttendanceSchedule' },
      { path: '/pos-admin/cash-register-users', title: 'menu.cashRegisterUsers', accessKey: 'canManageStoreOps' },
      { path: '/pos-admin/operation-logs', title: 'menu.operationLogs', accessKey: 'canViewEmployeeOperationLogs' },
      { path: '/pos-admin/linkly-settlements', title: 'menu.linklySettlements', accessKey: 'isAdmin' },
      { path: '/pos-admin/daily-closes', title: 'menu.dailyCloses', accessKey: 'canViewDailyCloseRecords' },
      { path: '/pos-admin/sales-orders', title: 'menu.salesOrders', accessKey: 'canReadOrder' },
      { path: '/pos-admin/local-supplier-invoices', title: 'menu.localSupplierInvoices', accessKey: 'canManageLocalPurchase' },
    ],
  },
]

const warehouseStaffVisibleMenuPaths = new Set([
  '/warehouse',
  '/warehouse/store-orders',
])

function isWarehouseStaffNavigationLimited(access: AccessControl) {
  return (
    access.isWarehouseStaff &&
    !access.isAdmin &&
    !access.isWarehouseManager &&
    (access.hasRole('WarehouseStaff') || access.hasRole('仓库员工'))
  )
}

function canAccessRoute(route: WebMenuPreviewRoute, access: AccessControl) {
  const accessKey = route.accessKey
  if (!accessKey) {
    return true
  }
  return access[accessKey] === true
}

function buildAddPermissionCodes(
  route: WebMenuPreviewRoute,
  permissionCodes: string[],
  explicitPermissionCodeSet: Set<string>,
): string[] {
  if (!route.accessKey || !permissionCodes.length) {
    return []
  }

  // 中文注释：菜单添加只补最小可见权限，避免只读入口把角色提升成管理权限。
  const primaryPermissionCode = permissionCodes[0]
  const nextCodes = [primaryPermissionCode]
  // 销售看板的单页权限已包含后台入口能力，不连带授予工作台。
  if (route.path !== '/dashboard' && !route.path.startsWith('/executive-sales-intelligence/') &&
    !explicitPermissionCodeSet.has(P.Dashboard.View)) {
    nextCodes.push(P.Dashboard.View)
  }

  return nextCodes
}

function buildPreviewNodes(
  routes: WebMenuPreviewRoute[],
  access: AccessControl,
  t: WebMenuPreviewTranslate,
  options: BuildWebRoleMenuPreviewOptions,
): WebMenuPreviewNode[] {
  const explicitPermissionCodeSet = new Set(options.explicitPermissionCodes ?? [])
  const limitWarehouseStaffNavigation = isWarehouseStaffNavigationLimited(access)

  return routes.flatMap((route) => {
    if (limitWarehouseStaffNavigation && !warehouseStaffVisibleMenuPaths.has(route.path) &&
      route.path !== '/executive-sales-intelligence' && !route.path.startsWith('/executive-sales-intelligence/')) {
      return []
    }

    const children = route.children ? buildPreviewNodes(route.children, access, t, options) : undefined
    const hasChildren = Boolean(children?.length)
    const hasSelfAccess = canAccessRoute(route, access)
    const hasVisibleChildren = Boolean(children?.some((child) => child.visible))
    const visible = route.children ? hasVisibleChildren : hasSelfAccess

    if (!options.includeHidden && !visible) {
      return []
    }

    if (options.includeHidden && !hasSelfAccess && !hasChildren && !route.accessKey) {
      return []
    }

    const permissionCodes = route.accessKey ? getAccessKeyPermissionCodes(route.accessKey) : []
    const addPermissionCodes = buildAddPermissionCodes(route, permissionCodes, explicitPermissionCodeSet)
    const removePermissionCodes = permissionCodes
    const isEditableRoute = permissionCodes.length > 0
    const isReadOnly = Boolean(options.readOnly)

    const node: WebMenuPreviewNode = {
      key: route.path,
      title: t(route.title, route.title),
      path: route.path,
      permissionCodes,
      visible,
      edit: {
        canAdd: !visible && isEditableRoute && !isReadOnly,
        canRemove: hasSelfAccess && isEditableRoute && !isReadOnly,
        isReadOnly,
        isFixed: false,
        addPermissionCodes,
        removePermissionCodes,
        reason: isReadOnly ? 'read-only' : (!isEditableRoute ? 'group' : undefined),
      },
    }
    if (route.accessKey) {
      node.accessKey = route.accessKey
    }
    if (children?.length) {
      node.children = children
    }
    return [node]
  })
}

export function getAccessKeyPermissionCodes(accessKey: keyof AccessControl): string[] {
  return accessKeyPermissionMap[accessKey] ?? []
}

export function buildWebRoleMenuPreview(
  access: AccessControl,
  t: WebMenuPreviewTranslate,
  options: BuildWebRoleMenuPreviewOptions = {},
): WebMenuPreviewNode[] {
  return buildPreviewNodes(webMenuPreviewRoutes, access, t, options)
}

export function filterWebMenuNodesByVisibility(
  nodes: WebMenuPreviewNode[],
  filter: WebMenuVisibilityFilter,
): WebMenuPreviewNode[] {
  if (filter === 'all') {
    return nodes
  }

  const expectedVisible = filter === 'visible'

  return nodes.flatMap((node) => {
    const children = node.children ? filterWebMenuNodesByVisibility(node.children, filter) : undefined
    if (node.visible !== expectedVisible && !children?.length) {
      return []
    }

    const { children: _children, ...nodeWithoutChildren } = node
    const nextNode: WebMenuPreviewNode = { ...nodeWithoutChildren }
    if (children?.length) {
      nextNode.children = children
    }
    return [nextNode]
  })
}
