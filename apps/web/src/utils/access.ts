import type { AccessControl, CurrentUser } from '../types/auth'
import { P } from '../types/permissions'
import { hasBackendNavigationAccess } from './webPortalAccess'

const PERMISSION_ALIAS_GROUPS = [
  {
    canonicalCode: P.LocalPurchase.View,
    aliasCodes: ['LocalInvocie.View'],
  },
  {
    canonicalCode: P.LocalPurchase.Edit,
    aliasCodes: ['LocalInvocie.Edit'],
  },
] as const

const permissionAliasMap = new Map<string, string[]>()

PERMISSION_ALIAS_GROUPS.forEach(({ canonicalCode, aliasCodes }) => {
  const codes = [canonicalCode, ...aliasCodes]
  const uniqueCodes = Array.from(new Set(codes.map((code) => code.toLowerCase())))

  codes.forEach((code) => {
    permissionAliasMap.set(code.toLowerCase(), uniqueCodes)
  })
})

function getEquivalentPermissionCodes(permission: string): string[] {
  const normalizedPermission = permission.toLowerCase()
  return permissionAliasMap.get(normalizedPermission) ?? [normalizedPermission]
}

function createEmptyAccess(): AccessControl {
  const alwaysFalse = () => false

  return {
    isAdmin: false,
    isManager: false,
    isUser: false,
    isWarehouseStaff: false,
    isWarehouseStaffOnly: false,
    isWarehouseManager: false,
    isStoreStaff: false,
    isStoreManager: false,
    isStoreLevelManager: false,
    onlyOrder: false,
    canReadOrder: false,
    canWriteOrder: false,
    canDeleteOrder: false,
    canReadProduct: false,
    canWriteProduct: false,
    canDeleteProduct: false,
    canReadUser: false,
    canReadUserConsole: false,
    canWriteUser: false,
    canDeleteUser: false,
    canReadRole: false,
    canWriteRole: false,
    canDeleteRole: false,
    canReadStore: false,
    canWriteStore: false,
    canDeleteStore: false,
    canManageWarehouse: false,
    canManageStore: false,
    canViewReports: false,
    canViewSalesIntelligence: false,
    canViewSalesData: false,
    canViewSalesDetail: false,
    canViewCompactSalesBoard: false,
    canViewProductMovementReport: false,
    canViewBatchProductSalesAnalysis: false,
    canViewShopBatchProductSales: false,
    canViewWarehouseProductFlowAnalysis: false,
    canViewLocalProductSalesAnalysis: false,
    canViewPurchaseAmountDashboard: false,
    canViewLocalSupplierPurchaseSalesAnalysis: false,
    canViewPurchaseSalesAnalysis: false,
    canViewProductSalesAnalysis: false,
    // 新细粒度权限
    canManageWarehouseProducts: false,
    canManageWarehouseOrders: false,
    canManageStoreOrderImportPriceVariance: false,
    canManageWarehouseCategories: false,
    canManageWarehouseLocations: false,
    canViewContainers: false,
    canCreateContainer: false,
    canEditContainer: false,
    canDeleteContainer: false,
    canManageStoreProducts: false,
    canEditStoreProducts: false,
    canCreateStoreProducts: false,
    canManageStoreOps: false,
    canManageLocalPurchase: false,
    canEditLocalPurchase: false,
    canPushLocalPurchaseToHq: false,
    canManagePricing: false,
    canEditPricing: false,
    canManagePromotions: false,
    canEditPromotions: false,
    canManageAdvertisements: false,
    canEditAdvertisements: false,
    canViewAustralianSuppliers: false,
    canEditAustralianSuppliers: false,
    canManageDomesticSuppliers: false,
    canManageDomesticProducts: false,
    canManageDomesticPrefixCodes: false,
    canViewAttendanceSchedule: false,
    canEditAttendanceSchedule: false,
    canViewAttendanceAvailability: false,
    canViewAttendancePunches: false,
    canReviewAttendance: false,
    canEditAttendanceHoliday: false,
    canEditAttendanceSettings: false,
    canViewEmployeeProfiles: false,
    canViewSystemLogs: false,
    canViewPerformanceBaseline: false,
    canViewOperationAudits: false,
    canViewLegacyEmployeeLogs: false,
    canReviewLegacyEmployeeLogs: false,
    canManageSystemSettings: false,
    canManageScheduledTasks: false,
    canViewAppDownloads: false,
    canManageAppDownloads: false,
    canViewDeviceRegistration: false,
    canManageDeviceRegistration: false,
    canManageMobileDeviceActivationCodes: false,
    canViewPosProducts: false,
    canManagePosProducts: false,
    canAccessAdminShell: false,
    canAccessDashboard: false,
    canAccessOrderFront: false,
    hasPermission: alwaysFalse,
    hasRole: alwaysFalse,
    onlyRole: alwaysFalse,
    hasAnyRole: alwaysFalse,
    hasAllRoles: alwaysFalse,
    managedStoreCodes: () => null,
    visibleStoreCodes: () => null,
  }
}

export function buildAccess(currentUser?: CurrentUser | null): AccessControl {
  if (!currentUser) {
    return createEmptyAccess()
  }

  const hasRole = (role: string) =>
    currentUser.roleNames?.some((item) => item.toLowerCase() === role.toLowerCase()) ?? false

  const isAdmin =
    hasRole('Admin') ||
    hasRole('管理员') ||
    hasRole('SuperAdmin') ||
    hasRole('超级管理员')
  const isWarehouseManager = hasRole('WarehouseManager') || hasRole('仓库经理')
  const currentPermissionSet = new Set((currentUser.permissions ?? []).map((item) => item.toLowerCase()))
  const currentExactPermissionSet = new Set(
    (currentUser.exactPermissions ?? []).map((item) => item.toLowerCase()),
  )

  const hasPermission = (permission: string) => {
    if (isAdmin) return true
    return getEquivalentPermissionCodes(permission).some((code) => currentPermissionSet.has(code))
  }

  const onlyRole = (role: string) => {
    if (!currentUser.roleNames?.length) {
      return false
    }
    return hasRole(role) && currentUser.roleNames.length === 1
  }

  const hasAnyRole = (roles: string[]) => roles.some((role) => hasRole(role))
  const hasAllRoles = (roles: string[]) => roles.every((role) => hasRole(role))

  // --- Role identity flags (backward compat) ---
  const isStoreManager = hasRole('StoreManager') || hasRole('店长') || hasRole('经理')
  const isManager = isStoreManager || isWarehouseManager
  const isUser = hasRole('User') || hasRole('用户')
  const isWarehouseStaff =
    isAdmin ||
    hasRole('WarehouseStaff') ||
    hasRole('仓库员工') ||
    hasRole('WarehouseManager')
  const isWarehouseStaffOnly =
    isWarehouseStaff &&
    !isAdmin &&
    !isWarehouseManager &&
    (hasRole('WarehouseStaff') || hasRole('仓库员工'))
  const isStoreStaff = hasRole('StoreStaff') || hasRole('店铺员工')
  const isStoreLevelManager = isStoreManager && !isAdmin && !isWarehouseManager
  // 仅当全部角色都是订货角色别名时才限制到订货前台，避免影响兼任其他岗位的用户。
  const orderRoleNames = currentUser.roleNames ?? []
  const onlyOrder =
    orderRoleNames.length > 0 &&
    orderRoleNames.every((roleName) => ['order', '订货员'].includes(roleName.toLowerCase()))

  const managedStoreCodes = () => {
    if (isAdmin || isWarehouseManager) {
      return null
    }
    if (currentUser.stores?.length) {
      return currentUser.stores
        .filter((item) => item.isManageable)
        .map((item) => item.storeCode)
        .filter(Boolean)
    }
    return []
  }

  const visibleStoreCodes = () => {
    if (isAdmin || isWarehouseManager) {
      return null
    }
    if (currentUser.stores?.length) {
      return currentUser.stores
        .map((item) => item.storeCode)
        .filter(Boolean)
    }
    return []
  }

  // --- 旧权限（保留兼容）---
  const canReadUser = isAdmin || hasPermission(P.Users.View)
  // Web 后台 /system/users 菜单与页面只认 Users.ViewWebConsole；Users.View 保留给移动端员工列表/用户管理与接口。
  const canReadUserConsole = isAdmin || hasPermission(P.Users.ViewWebConsole)
  const canWriteUser = isAdmin || hasPermission(P.Users.Create) || hasPermission(P.Users.Edit)
  const canDeleteUser = isAdmin || hasPermission(P.Users.Delete)
  const canReadRole = isAdmin || hasPermission(P.Roles.View)
  const canWriteRole = isAdmin || hasPermission(P.Roles.Create) || hasPermission(P.Roles.Edit)
  const canDeleteRole = isAdmin || hasPermission(P.Roles.Delete)
  const canReadStore = isAdmin || hasPermission(P.Stores.View)
  const canWriteStore = isAdmin || hasPermission(P.Stores.Create) || hasPermission(P.Stores.Edit)
  const canDeleteStore = isAdmin || hasPermission(P.Stores.Delete)
  const canManageWarehouse = isAdmin || hasPermission(P.Warehouse.Manage)
  const canManageStore = isAdmin || hasPermission(P.Stores.Edit) || hasPermission(P.Warehouse.Manage)

  const canReadOrder = isAdmin || hasPermission(P.Orders.View)
  const canWriteOrder = isAdmin || hasPermission(P.Orders.Create) || hasPermission(P.Orders.Edit)
  const canDeleteOrder = isAdmin || hasPermission(P.Orders.Delete)
  const canReadProduct = isAdmin || hasPermission(P.Products.View)
  const canWriteProduct = isAdmin || hasPermission(P.Products.Create) || hasPermission(P.Products.Edit)
  const canDeleteProduct = isAdmin || hasPermission(P.Products.Delete)

  const canViewReports = isAdmin || hasPermission(P.Reports.View)
  // 销售看板逐页授权，旧报表及进货单权限不能隐式放开其他页面；管理员由 hasPermission 统一放行。
  const canViewSalesData = hasPermission(P.SalesDashboard.SalesDataView)
  const canViewSalesDetail = hasPermission(P.SalesDashboard.SalesDetailView)
  const canViewCompactSalesBoard = hasPermission(P.SalesDashboard.CompactBoardView)
  const canViewProductMovementReport = hasPermission(P.SalesDashboard.ProductMovementView)
  const canViewBatchProductSalesAnalysis = hasPermission(P.SalesDashboard.BatchProductSalesView)
  // 订货前台货号销量页只认前台权限码，不与后台销售看板权限互相放行，避免订货员被判定为拥有后台入口。
  const canViewShopBatchProductSales = hasPermission(P.OrderFront.BatchProductSalesView)
  const canViewWarehouseProductFlowAnalysis = hasPermission(P.SalesDashboard.WarehouseFlowView)
  const canViewLocalProductSalesAnalysis = hasPermission(P.SalesDashboard.LocalProductAnalysisView)
  const canViewPurchaseAmountDashboard = hasPermission(P.SalesDashboard.PurchaseAmountView)
  // 分店进货销量分析挪到销售看板后只认本页权限；LocalPurchase.View 不再连带放开它，也不点亮销售看板父菜单。
  const canViewLocalSupplierPurchaseSalesAnalysis = hasPermission(P.SalesDashboard.LocalSupplierPurchaseSalesView)
  // 两页合并为「进货销量分析」双标签页：入口取两者之一，标签仍各自按本页权限显示。
  const canViewPurchaseSalesAnalysis = canViewBatchProductSalesAnalysis || canViewLocalSupplierPurchaseSalesAnalysis
  // 商品销量分析是精确权限契约节点：只读 exactPermissions，不做 Reports.View 别名展开，
  // 字段缺失时非管理员拒绝，超级管理员别名由 isAdmin 兼容放行。
  const canViewProductSalesAnalysis =
    isAdmin || currentExactPermissionSet.has(P.Reports.ProductMovementView.toLowerCase())
  // 父菜单由可见子页决定，不额外要求工作台或报表总权限。
  const canViewSalesIntelligence =
    canViewSalesData || canViewSalesDetail || canViewCompactSalesBoard ||
    canViewProductMovementReport || canViewWarehouseProductFlowAnalysis ||
    canViewLocalProductSalesAnalysis || canViewPurchaseAmountDashboard || canViewBatchProductSalesAnalysis ||
    canViewLocalSupplierPurchaseSalesAnalysis
  // --- 新细粒度权限 ---
  // 仓库
  const canManageWarehouseProducts =
    isAdmin || hasPermission(P.Warehouse.ManageProducts) || hasPermission(P.Warehouse.Manage)
  const canManageWarehouseOrders =
    isAdmin || hasPermission(P.Warehouse.ManageOrders) || hasPermission(P.Warehouse.Manage)
  // 首柜价差异报表暴露全局基准差异和可编辑价格入口，纯 WarehouseStaff 仍只保留分店订货文档入口。
  const canManageStoreOrderImportPriceVariance =
    canManageWarehouseOrders && !isWarehouseStaffOnly
  const canManageWarehouseCategories =
    isAdmin || hasPermission(P.Warehouse.ManageCategories) || hasPermission(P.Warehouse.Manage)
  const canManageWarehouseLocations =
    isAdmin || hasPermission(P.Warehouse.ManageLocations) || hasPermission(P.Warehouse.Manage)
  const canViewContainers = isAdmin || hasPermission(P.Container.View)
  const canCreateContainer = isAdmin || hasPermission(P.Container.Create)
  const canEditContainer = isAdmin || hasPermission(P.Container.Edit)
  const canDeleteContainer = isAdmin || hasPermission(P.Container.Delete)

  // 分店商品
  const canManageStoreProducts = isAdmin || hasPermission(P.StoreProducts.View)
  const canEditStoreProducts = isAdmin || hasPermission(P.StoreProducts.Edit)
  const canCreateStoreProducts = isAdmin || hasPermission(P.StoreProducts.Create)

  // 分店运营
  const canManageStoreOps = isAdmin || hasPermission(P.Store.ManageOperations)

  // 本地进货
  const canManageLocalPurchase = isAdmin || hasPermission(P.LocalPurchase.View)
  const canEditLocalPurchase = isAdmin || hasPermission(P.LocalPurchase.Edit)
  const canPushLocalPurchaseToHq = isAdmin || hasPermission(P.LocalPurchase.PushToHq)

  // 定价策略
  const canManagePricing = isAdmin || hasPermission(P.PricingStrategy.View)
  const canEditPricing = isAdmin || hasPermission(P.PricingStrategy.Edit)

  // 促销
  const canManagePromotions = isAdmin || hasPermission(P.Promotions.View)
  const canEditPromotions = isAdmin || hasPermission(P.Promotions.Edit)

  // 广告
  const canManageAdvertisements = isAdmin || hasPermission(P.Advertisements.View)
  const canEditAdvertisements = isAdmin || hasPermission(P.Advertisements.Edit)

  // 澳洲供应商
  const canViewAustralianSuppliers = isAdmin || hasPermission(P.AustralianSuppliers.View)
  const canEditAustralianSuppliers = isAdmin || hasPermission(P.AustralianSuppliers.Edit)

  // 国内采购
  const canManageDomesticSuppliers = isAdmin || hasPermission(P.DomesticPurchase.ManageSuppliers)
  const canManageDomesticProducts = isAdmin || hasPermission(P.DomesticPurchase.ManageProducts)
  const canManageDomesticPrefixCodes =
    isAdmin || hasPermission(P.DomesticPurchase.ManagePrefixCodes)

  // 排班考勤
  const canViewAttendanceSchedule =
    isAdmin ||
    hasPermission(P.Attendance.AdminView) ||
    hasPermission(P.Attendance.ScheduleViewStore) ||
    // 与后端权限包含关系一致：能编辑管理分店排班即可查看排班
    hasPermission(P.Attendance.ScheduleEditManagedStore)
  const canEditAttendanceSchedule =
    isAdmin ||
    hasPermission(P.Attendance.AdminView) ||
    hasPermission(P.Attendance.ScheduleEditManagedStore)
  const canViewAttendanceAvailability =
    isAdmin ||
    hasPermission(P.Attendance.AdminView) ||
    hasPermission(P.Attendance.AvailabilityViewManagedStore)
  const canViewAttendancePunches =
    isAdmin ||
    hasPermission(P.Attendance.AdminView) ||
    hasPermission(P.Attendance.PunchViewManagedStore)
  const canReviewAttendance =
    isAdmin ||
    hasPermission(P.Attendance.AdminView) ||
    hasPermission(P.Attendance.ApprovalReviewManagedStore) ||
    hasPermission(P.Attendance.LeaveReviewManagedStore)
  const canEditAttendanceHoliday =
    isAdmin ||
    hasPermission(P.Attendance.AdminView) ||
    hasPermission(P.Attendance.HolidayEditManagedStore)
  const canEditAttendanceSettings = isAdmin || hasPermission(P.Attendance.SettingsEdit)
  const canViewEmployeeProfiles = isAdmin || hasPermission(P.EmployeeProfiles.View)
  const canViewSystemLogs = isAdmin || hasPermission(P.System.ViewLogs)
  const canViewPerformanceBaseline =
    isAdmin || hasPermission(P.System.ViewPerformanceBaseline)
  const canViewOperationAudits = isAdmin || hasPermission(P.PosTerminal.AuditView)
  const canViewLegacyEmployeeLogs = isAdmin || hasPermission(P.LegacyEmployeeLogs.View)
  const canReviewLegacyEmployeeLogs = isAdmin || hasPermission(P.LegacyEmployeeLogs.Review)
  const canManageScheduledTasks = isAdmin || hasPermission(P.System.ManageScheduledTasks)
  const canManageSystemSettings = isAdmin || hasPermission(P.System.ManageSettings)
  // OTA 登记和回撤命令属于发布管理动作，和只读下载页权限分开控制。
  const canManageAppDownloads = isAdmin || hasPermission(P.System.ManageAppDownloads)
  // 管理权限隐含页面可见性，避免只有发布权限时前端路由仍被错误隐藏。
  const canViewAppDownloads =
    isAdmin || canManageAppDownloads || hasPermission(P.System.ViewAppDownloads)
  const canManageDeviceRegistration = isAdmin || hasPermission(P.DeviceRegistration.Manage)
  const canManageMobileDeviceActivationCodes =
    isAdmin || hasPermission(P.DeviceRegistration.MobileActivationCodesManage)
  const canViewDeviceRegistration =
    canManageDeviceRegistration ||
    canManageMobileDeviceActivationCodes ||
    isAdmin ||
    hasPermission(P.DeviceRegistration.View) ||
    hasPermission(P.DeviceRegistration.ActivationCodesManage)
  const canViewPosProducts =
    isAdmin || hasPermission(P.PosProducts.View) || hasPermission(P.PosProducts.Manage)
  const canManagePosProducts = isAdmin || hasPermission(P.PosProducts.Manage)

  const canAccessDashboard = isAdmin || hasPermission(P.Dashboard.View)
  const canAccessAdminShell =
    !onlyOrder &&
    hasBackendNavigationAccess({
      isAdmin,
      canAccessDashboard,
      canManageWarehouse,
      canManageWarehouseOrders,
      canManageStoreOrderImportPriceVariance,
      canViewContainers,
      canViewProductMovementReport,
      canManageLocalPurchase,
      canEditLocalPurchase,
      canManageSystemSettings,
      canViewAppDownloads,
      canViewPerformanceBaseline,
      canViewOperationAudits,
      canViewDeviceRegistration,
      hasPermission,
    })
  const canAccessOrderFront =
    isAdmin ||
    hasPermission(P.OrderFront.View) ||
    (isWarehouseStaffOnly && hasPermission(P.Orders.Create))

  return {
    isAdmin,
    isManager,
    isUser,
    isWarehouseStaff,
    isWarehouseStaffOnly,
    isWarehouseManager,
    isStoreStaff,
    isStoreManager,
    isStoreLevelManager,
    onlyOrder,
    canReadOrder,
    canWriteOrder,
    canDeleteOrder,
    canReadProduct,
    canWriteProduct,
    canDeleteProduct,
    canReadUser,
    canReadUserConsole,
    canWriteUser,
    canDeleteUser,
    canReadRole,
    canWriteRole,
    canDeleteRole,
    canReadStore,
    canWriteStore,
    canDeleteStore,
    canManageWarehouse,
    canManageStore,
    canViewReports,
    canViewSalesIntelligence,
    canViewSalesData,
    canViewSalesDetail,
    canViewCompactSalesBoard,
    canViewProductMovementReport,
    canViewBatchProductSalesAnalysis,
    canViewShopBatchProductSales,
    canViewWarehouseProductFlowAnalysis,
    canViewLocalProductSalesAnalysis,
    canViewPurchaseAmountDashboard,
    canViewLocalSupplierPurchaseSalesAnalysis,
    canViewPurchaseSalesAnalysis,
    canViewProductSalesAnalysis,
    // 新细粒度
    canManageWarehouseProducts,
    canManageWarehouseOrders,
    canManageStoreOrderImportPriceVariance,
    canManageWarehouseCategories,
    canManageWarehouseLocations,
    canViewContainers,
    canCreateContainer,
    canEditContainer,
    canDeleteContainer,
    canManageStoreProducts,
    canEditStoreProducts,
    canCreateStoreProducts,
    canManageStoreOps,
    canManageLocalPurchase,
    canEditLocalPurchase,
    canPushLocalPurchaseToHq,
    canManagePricing,
    canEditPricing,
    canManagePromotions,
    canEditPromotions,
    canManageAdvertisements,
    canEditAdvertisements,
    canViewAustralianSuppliers,
    canEditAustralianSuppliers,
    canManageDomesticSuppliers,
    canManageDomesticProducts,
    canManageDomesticPrefixCodes,
    canViewAttendanceSchedule,
    canEditAttendanceSchedule,
    canViewAttendanceAvailability,
    canViewAttendancePunches,
    canReviewAttendance,
    canEditAttendanceHoliday,
    canEditAttendanceSettings,
    canViewEmployeeProfiles,
    canViewSystemLogs,
    canViewPerformanceBaseline,
    canViewOperationAudits,
    canViewLegacyEmployeeLogs,
    canReviewLegacyEmployeeLogs,
    canManageScheduledTasks,
    canManageSystemSettings,
    canViewAppDownloads,
    canManageAppDownloads,
    canViewDeviceRegistration,
    canManageDeviceRegistration,
    canManageMobileDeviceActivationCodes,
    canViewPosProducts,
    canManagePosProducts,
    canAccessAdminShell,
    canAccessDashboard,
    canAccessOrderFront,
    hasPermission,
    hasRole,
    onlyRole,
    hasAnyRole,
    hasAllRoles,
    managedStoreCodes,
    visibleStoreCodes,
  }
}
