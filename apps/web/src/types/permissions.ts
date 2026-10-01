/**
 * Frontend permission constants — mirrors backend BlazorApp.Shared.Constants.Permissions
 * Used by <HasPermission code={P.XXX}> and buildAccess() for permission-driven access control.
 *
 * KEEP IN SYNC with backend Permissions.cs
 */

export const P = {
  Users: {
    View: 'Users.View',
    // Web 后台 /system/users 菜单与页面的独立授权，与移动端复用的 Users.View 分开。
    ViewWebConsole: 'Users.ViewWebConsole',
    Create: 'Users.Create',
    Edit: 'Users.Edit',
    Delete: 'Users.Delete',
    ManageRoles: 'Users.ManageRoles',
    ManageStores: 'Users.ManageStores',
    ManagePosTerminalPermissions: 'Users.ManagePosTerminalPermissions',
    ResetPassword: 'Users.ResetPassword',
  },
  Roles: {
    View: 'Roles.View',
    Create: 'Roles.Create',
    Edit: 'Roles.Edit',
    Delete: 'Roles.Delete',
    ManagePermissions: 'Roles.ManagePermissions',
    ManageUsers: 'Roles.ManageUsers',
  },
  Stores: {
    View: 'Stores.View',
    Create: 'Stores.Create',
    Edit: 'Stores.Edit',
    Delete: 'Stores.Delete',
    Sync: 'Stores.Sync',
  },
  Products: {
    View: 'Products.View',
    Create: 'Products.Create',
    Edit: 'Products.Edit',
    Delete: 'Products.Delete',
  },
  Orders: {
    View: 'Orders.View',
    Create: 'Orders.Create',
    Edit: 'Orders.Edit',
    Delete: 'Orders.Delete',
  },
  Warehouse: {
    Manage: 'Warehouse.Manage',
    ManageProducts: 'Warehouse.ManageProducts',
    ManageCategories: 'Warehouse.ManageCategories',
    ManageLocations: 'Warehouse.ManageLocations',
    ManageOrders: 'Warehouse.ManageOrders',
    // 移动端「订单拣货」专用权限；后端按别名让 Warehouse.Manage / Warehouse.ManageOrders 持有者自动具备，反向不成立。
    Picking: 'Warehouse.Picking',
  },
  Container: {
    View: 'Container.View',
    Create: 'Container.Create',
    Edit: 'Container.Edit',
    Delete: 'Container.Delete',
    // 移动端「新品到店」页面专用权限，与后端 Permissions.Container.MobileNewProductsView 一致。
    MobileNewProductsView: 'Container.MobileNewProductsView',
  },
  SalesOrders: {
    View: 'SalesOrders.View',
  },
  InstallmentOrders: {
    View: 'InstallmentOrders.View',
  },
  StoreVouchers: {
    View: 'StoreVouchers.View',
  },
  DomesticPurchase: {
    ManageSuppliers: 'DomesticPurchase.ManageSuppliers',
    ManageProducts: 'DomesticPurchase.ManageProducts',
    ManagePrefixCodes: 'DomesticPurchase.ManagePrefixCodes',
  },
  Reports: {
    View: 'Reports.View',
    ProductMovementView: 'Reports.ProductMovement.View',
  },
  SalesDashboard: {
    SalesDataView: 'SalesDashboard.SalesData.View',
    SalesDetailView: 'SalesDashboard.SalesDetail.View',
    CompactBoardView: 'SalesDashboard.CompactBoard.View',
    ProductMovementView: 'SalesDashboard.ProductMovement.View',
    BatchProductSalesView: 'SalesDashboard.BatchProductSales.View',
    WarehouseFlowView: 'SalesDashboard.WarehouseFlow.View',
    LocalProductAnalysisView: 'SalesDashboard.LocalProductAnalysis.View',
    PurchaseAmountView: 'SalesDashboard.PurchaseAmount.View',
    // 分店进货销量分析已挪到销售看板，按该菜单的逐页授权模式使用独立权限码。
    LocalSupplierPurchaseSalesView: 'SalesDashboard.LocalSupplierPurchaseSales.View',
  },
  System: {
    ViewLogs: 'System.ViewLogs',
    ViewPerformanceBaseline: 'System.ViewPerformanceBaseline',
    ManagePerformanceBaseline: 'System.ManagePerformanceBaseline',
    ManageScheduledTasks: 'System.ManageScheduledTasks',
    ManageSettings: 'System.ManageSettings',
    ViewAppDownloads: 'System.ViewAppDownloads',
    ManageAppDownloads: 'System.ManageAppDownloads',
    ViewMobileAppInstallLinks: 'System.ViewMobileAppInstallLinks',
  },
  DeviceRegistration: {
    View: 'DeviceRegistration.View',
    Manage: 'DeviceRegistration.Manage',
    ActivationCodesManage: 'DeviceRegistration.ActivationCodes.Manage',
    MobileActivationCodesManage: 'DeviceRegistration.MobileActivationCodes.Manage',
  },
  EmployeeProfiles: {
    View: 'EmployeeProfiles.View',
    Edit: 'EmployeeProfiles.Edit',
    EditPositionType: 'EmployeeProfiles.EditPositionType',
  },
  StoreProducts: {
    View: 'StoreProducts.View',
    Create: 'StoreProducts.Create',
    Edit: 'StoreProducts.Edit',
    // 移动端「商品维护」把本店零售价/折扣同步到其它可管理分店；名称与说明由后端权限种子提供。
    SyncToOtherStores: 'StoreProducts.SyncToOtherStores',
    PriceUpdates: 'StoreProducts.PriceUpdates',
  },
  Promotions: {
    View: 'Promotions.View',
    Edit: 'Promotions.Edit',
  },
  Advertisements: {
    View: 'Advertisements.View',
    Edit: 'Advertisements.Edit',
  },
  PricingStrategy: {
    View: 'PricingStrategy.View',
    Edit: 'PricingStrategy.Edit',
  },
  LocalPurchase: {
    View: 'LocalPurchase.View',
    MobileView: 'LocalPurchase.MobileView',
    Edit: 'LocalPurchase.Edit',
    PushToHq: 'LocalPurchase.PushToHq',
  },
  AustralianSuppliers: {
    View: 'AustralianSuppliers.View',
    Edit: 'AustralianSuppliers.Edit',
  },
  Store: {
    ManageOperations: 'Store.ManageOperations',
  },
  // 移动端「收银用户条码」独立权限；Web 页面仍只认 Store.ManageOperations。
  CashRegisterUsers: {
    MobileManage: 'CashRegisterUsers.MobileManage',
    MobilePrint: 'CashRegisterUsers.MobilePrint',
  },
  // 移动端「季节商品查询」独立权限，Web 端暂无对应页面。
  SeasonalProductInsights: {
    View: 'SeasonalProductInsights.View',
  },
  PosProducts: {
    View: 'PosProducts.View',
    Manage: 'PosProducts.Manage',
  },
  PosTerminal: {
    AuditView: 'Permissions.PosTerminal.Audit.View',
  },
  // 老系统（旧版收银 POSM.EmployeeLogs）操作日志只读页，刻意不用 Permissions.PosTerminal.* 前缀。
  LegacyEmployeeLogs: {
    View: 'LegacyEmployeeLogs.View',
    Review: 'LegacyEmployeeLogs.Review',
  },
  Dashboard: {
    View: 'Dashboard',
  },
  OrderFront: {
    View: 'OrderFront',
    // 订货前台「货号销量」页面，独立于后台销售看板权限。
    BatchProductSalesView: 'OrderFront.BatchProductSales.View',
  },
  Attendance: {
    ScheduleViewSelf: 'Attendance.Schedule.ViewSelf',
    ScheduleViewStore: 'Attendance.Schedule.ViewStore',
    ScheduleEditManagedStore: 'Attendance.Schedule.EditManagedStore',
    AvailabilitySubmitSelf: 'Attendance.Availability.SubmitSelf',
    AvailabilityViewManagedStore: 'Attendance.Availability.ViewManagedStore',
    PunchSelf: 'Attendance.Punch.Self',
    PunchViewManagedStore: 'Attendance.Punch.ViewManagedStore',
    PunchAdjustManagedStore: 'Attendance.Punch.AdjustManagedStore',
    ApprovalViewManagedStore: 'Attendance.Approval.ViewManagedStore',
    ApprovalReviewManagedStore: 'Attendance.Approval.ReviewManagedStore',
    HolidayViewStore: 'Attendance.Holiday.ViewStore',
    HolidayEditManagedStore: 'Attendance.Holiday.EditManagedStore',
    LeaveApplySelf: 'Attendance.Leave.ApplySelf',
    LeaveViewManagedStore: 'Attendance.Leave.ViewManagedStore',
    LeaveReviewManagedStore: 'Attendance.Leave.ReviewManagedStore',
    SettingsEdit: 'Attendance.Settings.Edit',
    AdminView: 'Attendance.Admin.View',
  },
} as const

/** All permission code values as a flat array */
export const ALL_PERMISSIONS: string[] = Object.values(P).flatMap((group) =>
  Object.values(group),
)
