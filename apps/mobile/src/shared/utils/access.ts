import type { AccessControl, CurrentUser } from "@/modules/auth/types";

export const PERMISSIONS = {
  DeviceRegistration: {
    View: "DeviceRegistration.View",
    Manage: "DeviceRegistration.Manage",
    ActivationCodesManage: "DeviceRegistration.ActivationCodes.Manage",
    MobileActivationCodesManage: "DeviceRegistration.MobileActivationCodes.Manage",
  },
  System: {
    ManageSettings: "System.ManageSettings",
  },
  PosTerminal: {
    // 后端常量本身带 Permissions. 前缀，与 PosTerminal.* 其余收银权限同一命名空间。
    AuditView: "Permissions.PosTerminal.Audit.View",
  },
  LegacyEmployeeLogs: {
    View: "LegacyEmployeeLogs.View",
    Review: "LegacyEmployeeLogs.Review",
  },
  EmployeeProfiles: {
    View: "EmployeeProfiles.View",
    Edit: "EmployeeProfiles.Edit",
    EditPositionType: "EmployeeProfiles.EditPositionType",
  },
  Users: {
    View: "Users.View",
    Create: "Users.Create",
    Edit: "Users.Edit",
    ResetPassword: "Users.ResetPassword",
    // 移动端「员工列表」本店店员管理专用权限；店长只授予这三项，不获得 Web 全局用户管理。
    CreateStoreStaff: "Users.CreateStoreStaff",
    EditStoreStaff: "Users.EditStoreStaff",
    ResetStoreStaffPassword: "Users.ResetStoreStaffPassword",
    ManageRoles: "Users.ManageRoles",
    ManageStores: "Users.ManageStores",
    ManagePos: "Users.ManagePosTerminalPermissions",
    ManagePosTerminalPermissions: "Users.ManagePosTerminalPermissions",
  },
  CashRegisterUsers: {
    // 移动端「收银用户条码」独立权限：管理（查看/创建/更新/启停）与打印（查看/打印计数）分开授予。
    MobileManage: "CashRegisterUsers.MobileManage",
    MobilePrint: "CashRegisterUsers.MobilePrint",
  },
  SeasonalProductInsights: {
    // 移动端「季节商品查询」独立权限：会展示其他分店库存，不借用商品查询的 StoreProducts.View。
    View: "SeasonalProductInsights.View",
  },
  LocalPurchase: {
    View: "LocalPurchase.View",
    Edit: "LocalPurchase.Edit",
    PushToHq: "LocalPurchase.PushToHq",
  },
  InstallmentOrders: {
    View: "InstallmentOrders.View",
  },
  Advertisements: {
    View: "Advertisements.View",
    Edit: "Advertisements.Edit",
  },
  StoreVouchers: {
    View: "StoreVouchers.View",
  },
  // 现金管理五个码由管理员显式授予，界面按接口返回的 capabilities 控制按钮。
  Cash: {
    OverviewView: "Cash.Overview.View",
    DepositCreate: "Cash.Deposit.Create",
    ExpenseCreate: "Cash.Expense.Create",
    Void: "Cash.Void",
    AllStoresView: "Cash.AllStores.View",
  },
  StoreProducts: {
    View: "StoreProducts.View",
    Create: "StoreProducts.Create",
    Edit: "StoreProducts.Edit",
    SyncToOtherStores: "StoreProducts.SyncToOtherStores",
    PriceUpdates: "StoreProducts.PriceUpdates",
  },
  Container: {
    View: "Container.View",
    Create: "Container.Create",
    Edit: "Container.Edit",
    Delete: "Container.Delete",
  },
  SeasonalCards: {
    Remaining: {
      ViewManagedStore: "SeasonalCards.Remaining.ViewManagedStore",
      SubmitManagedStore: "SeasonalCards.Remaining.SubmitManagedStore",
    },
  },
  Attendance: {
    ScheduleViewSelf: "Attendance.Schedule.ViewSelf",
    ScheduleViewStore: "Attendance.Schedule.ViewStore",
    ScheduleEditManagedStore: "Attendance.Schedule.EditManagedStore",
    AvailabilitySubmitSelf: "Attendance.Availability.SubmitSelf",
    AvailabilityViewManagedStore: "Attendance.Availability.ViewManagedStore",
    PunchSelf: "Attendance.Punch.Self",
    PunchViewManagedStore: "Attendance.Punch.ViewManagedStore",
    PunchAdjustManagedStore: "Attendance.Punch.AdjustManagedStore",
    ApprovalViewManagedStore: "Attendance.Approval.ViewManagedStore",
    ApprovalReviewManagedStore: "Attendance.Approval.ReviewManagedStore",
    HolidayViewStore: "Attendance.Holiday.ViewStore",
    HolidayEditManagedStore: "Attendance.Holiday.EditManagedStore",
    LeaveApplySelf: "Attendance.Leave.ApplySelf",
    LeaveViewManagedStore: "Attendance.Leave.ViewManagedStore",
    LeaveReviewManagedStore: "Attendance.Leave.ReviewManagedStore",
    SettingsEdit: "Attendance.Settings.Edit",
    AdminView: "Attendance.Admin.View",
  },
} as const;

const PERMISSION_ALIAS_GROUPS = [
  {
    canonicalCode: PERMISSIONS.LocalPurchase.View,
    aliasCodes: ["LocalInvocie.View"],
  },
  {
    canonicalCode: PERMISSIONS.LocalPurchase.Edit,
    aliasCodes: ["LocalInvocie.Edit"],
  },
  {
    // 历史授权数据可能只存了无前缀写法，两种写法都视为同一权限。
    canonicalCode: PERMISSIONS.PosTerminal.AuditView,
    aliasCodes: ["PosTerminal.Audit.View"],
  },
  {
    canonicalCode: PERMISSIONS.StoreProducts.Create,
    aliasCodes: [
      "StoreProduct.Create",
      "StoreProductManagement.Create",
      "StoreProductMaintenance.Create",
      "StoreProductsManagement.Create",
      "StoreProductsMaintenance.Create",
      "分店商品管理.创建分店商品",
      "创建分店商品",
    ],
  },
] as const;

const ATTENDANCE_PERSONAL_PERMISSIONS = [
  PERMISSIONS.Attendance.ScheduleViewSelf,
  PERMISSIONS.Attendance.AvailabilitySubmitSelf,
  PERMISSIONS.Attendance.PunchSelf,
  PERMISSIONS.Attendance.LeaveApplySelf,
];

const ATTENDANCE_MANAGEMENT_PERMISSIONS = [
  PERMISSIONS.Attendance.ScheduleViewStore,
  PERMISSIONS.Attendance.ScheduleEditManagedStore,
  PERMISSIONS.Attendance.AvailabilityViewManagedStore,
  PERMISSIONS.Attendance.PunchViewManagedStore,
  PERMISSIONS.Attendance.ApprovalViewManagedStore,
  PERMISSIONS.Attendance.ApprovalReviewManagedStore,
  PERMISSIONS.Attendance.HolidayViewStore,
  PERMISSIONS.Attendance.HolidayEditManagedStore,
  PERMISSIONS.Attendance.LeaveViewManagedStore,
  PERMISSIONS.Attendance.LeaveReviewManagedStore,
  PERMISSIONS.Attendance.SettingsEdit,
  PERMISSIONS.Attendance.AdminView,
];

const ATTENDANCE_REVIEW_PERMISSIONS = [
  PERMISSIONS.Attendance.ApprovalReviewManagedStore,
  PERMISSIONS.Attendance.LeaveReviewManagedStore,
  PERMISSIONS.Attendance.AdminView,
];

function resolvePermissionAliases(permission: string) {
  const normalizedPermission = permission.toLowerCase();
  const group = PERMISSION_ALIAS_GROUPS.find(({ canonicalCode, aliasCodes }) =>
    [canonicalCode, ...aliasCodes].some(
      (item) => item.toLowerCase() === normalizedPermission
    )
  );

  return group ? [group.canonicalCode, ...group.aliasCodes] : [permission];
}

function createEmptyAccess(): AccessControl {
  const alwaysFalse = () => false;

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
    canCreateOrder: false,
    canEditOrder: false,
    canWriteOrder: false,
    canDeleteOrder: false,
    canReadProduct: false,
    canWriteProduct: false,
    canDeleteProduct: false,
    canReadUser: false,
    canWriteUser: false,
    canDeleteUser: false,
    canReadRole: false,
    canWriteRole: false,
    canDeleteRole: false,
    canReadStore: false,
    canWriteStore: false,
    canDeleteStore: false,
    canManageWarehouse: false,
    canViewContainers: false,
    canCreateContainer: false,
    canEditContainer: false,
    canDeleteContainer: false,
    canManageStore: false,
    canViewReports: false,
    canViewDeviceRegistration: false,
    canViewLegacyEmployeeLogs: false,
    canViewPosOperationAudits: false,
    canReviewLegacyEmployeeLogs: false,
    canManageDeviceRegistration: false,
    canManageDeviceActivationCodes: false,
    canManageMobileDeviceActivationCodes: false,
    canManageEmergencyLoginGrants: false,
    canViewEmployeeProfiles: false,
    canViewAttendancePersonal: false,
    canViewAttendanceManagement: false,
    canReviewAttendance: false,
    canEditAttendanceHoliday: false,
    canEditAttendanceSettings: false,
    canViewAttendancePunchRecords: false,
    canAdjustAttendancePunch: false,
    canViewLocalPurchase: false,
    canEditLocalPurchase: false,
    canPushLocalPurchaseToHq: false,
    canViewInstallmentOrders: false,
    canViewAdvertisements: false,
    canManageAdvertisements: false,
    canViewStoreVouchers: false,
    canViewSeasonalCardRemaining: false,
    canSubmitSeasonalCardRemaining: false,
    canCreateStoreProducts: false,
    canManageAttendance: false,
    hasPermission: alwaysFalse,
    hasRole: alwaysFalse,
    onlyRole: alwaysFalse,
    hasAnyRole: alwaysFalse,
    hasAllRoles: alwaysFalse,
    assignedStoreCodes: () => null,
    managedStoreCodes: () => null,
  };
}

export function buildAccess(currentUser?: CurrentUser | null): AccessControl {
  if (!currentUser) {
    return createEmptyAccess();
  }

  const hasRole = (role: string) =>
    currentUser.roleNames?.some(
      (item) => item.toLowerCase() === role.toLowerCase()
    ) ?? false;

  const onlyRole = (role: string) => {
    if (!currentUser.roleNames?.length) {
      return false;
    }
    return hasRole(role) && currentUser.roleNames.length === 1;
  };

  const hasAnyRole = (roles: string[]) => roles.some((role) => hasRole(role));
  const hasAllRoles = (roles: string[]) => roles.every((role) => hasRole(role));

  const isAdmin =
    hasRole("Admin") ||
    hasRole("管理员") ||
    hasRole("SuperAdmin") ||
    hasRole("超级管理员");
  const normalizedPermissions = new Set(
    currentUser.permissions?.map((item) => item.toLowerCase()) ?? []
  );
  const hasPermission = (permission: string) =>
    isAdmin ||
    resolvePermissionAliases(permission).some((item) =>
      normalizedPermissions.has(item.toLowerCase())
    );
  const isWarehouseManager =
    hasRole("WarehouseManager") ||
    hasRole("Warehouse") ||
    hasRole("仓库经理") ||
    hasRole("仓库管理员") ||
    hasRole("WarehouseAdmin");
  const isStoreManager =
    hasRole("StoreManager") || hasRole("店长") || hasRole("经理");
  const isManager = isStoreManager || isWarehouseManager;
  const isUser = hasRole("User") || hasRole("用户");
  const isWarehouseStaff =
    isAdmin || hasRole("WarehouseStaff") || hasRole("仓库员工") || hasRole("WarehouseManager");
  // 纯仓库员工用于专用购物车分流：管理员/仓库经理仍按管理角色能力处理。
  const isWarehouseStaffOnly =
    (hasRole("WarehouseStaff") || hasRole("仓库员工")) && !isAdmin && !isWarehouseManager;
  const isStoreStaff = hasRole("StoreStaff") || hasRole("店铺员工");
  const isStoreLevelManager = isStoreManager && !isAdmin && !isWarehouseManager;
  const onlyOrder = onlyRole("Order") || hasRole("订货员");

  const assignedStoreCodes = () => {
    if (isAdmin || isWarehouseManager) {
      return null;
    }
    if (currentUser.stores?.length) {
      return currentUser.stores.map((item) => item.storeCode).filter(Boolean);
    }
    return null;
  };

  const managedStoreCodes = () => {
    if (isAdmin || isWarehouseManager) {
      return null;
    }
    if (currentUser.stores?.length) {
      // isPrimary=true 表示可管理分店；false 是已分配但只读范围。
      return currentUser.stores
        .filter((item) => item.isPrimary === true)
        .map((item) => item.storeCode)
        .filter(Boolean);
    }
    return null;
  };

  const canReadUser = hasPermission("Users.View");
  const canWriteUser = hasPermission("Users.Create") || hasPermission("Users.Edit");
  const canDeleteUser = hasPermission("Users.Delete");
  const canReadRole = hasPermission("Roles.View");
  const canWriteRole = hasPermission("Roles.Create") || hasPermission("Roles.Edit");
  const canDeleteRole = hasPermission("Roles.Delete");
  const canReadStore = hasPermission("Stores.View");
  const canWriteStore = hasPermission("Stores.Create") || hasPermission("Stores.Edit");
  const canDeleteStore = hasPermission("Stores.Delete");
  const canManageWarehouse = hasPermission("Warehouse.Manage");
  const canViewContainers = hasPermission(PERMISSIONS.Container.View);
  const canCreateContainer = hasPermission(PERMISSIONS.Container.Create);
  const canEditContainer = hasPermission(PERMISSIONS.Container.Edit);
  const canDeleteContainer = hasPermission(PERMISSIONS.Container.Delete);
  const canManageStore = hasPermission("Stores.Edit") || hasPermission("Warehouse.Manage");

  const canReadOrder = hasPermission("Orders.View");
  // 普通订单建单和明细维护需要显式订单权限，旧 Warehouse.Manage 不再隐式放大。
  const canCreateOrder = hasPermission("Orders.Create");
  const canEditOrder = hasPermission("Orders.Edit");
  const canWriteOrder = canCreateOrder || canEditOrder;
  const canDeleteOrder = hasPermission("Orders.Delete");
  const canReadProduct = hasPermission("Products.View");
  const canWriteProduct = hasPermission("Products.Create") || hasPermission("Products.Edit");
  const canDeleteProduct = hasPermission("Products.Delete");

  const canViewReports = hasPermission("Reports.View");
  const canManageDeviceRegistration = hasPermission(PERMISSIONS.DeviceRegistration.Manage);
  const canManageDeviceActivationCodes = hasPermission(
    PERMISSIONS.DeviceRegistration.ActivationCodesManage
  );
  const canManageMobileDeviceActivationCodes = hasPermission(
    PERMISSIONS.DeviceRegistration.MobileActivationCodesManage
  );
  // 员工操作日志（老收银）：后端还会按可管理分店收口，这里只判断入口与核查权限。
  const canViewLegacyEmployeeLogs = hasPermission(PERMISSIONS.LegacyEmployeeLogs.View);
  const canReviewLegacyEmployeeLogs = hasPermission(PERMISSIONS.LegacyEmployeeLogs.Review);
  // 员工操作日志（新收银）：后端只放行管理员与店长类角色并按可管理分店收口；核查与老收银共用同一权限。
  const canViewPosOperationAudits = hasPermission(PERMISSIONS.PosTerminal.AuditView);
  const canViewDeviceRegistration =
    canManageDeviceRegistration ||
    hasPermission(PERMISSIONS.DeviceRegistration.View) ||
    canManageDeviceActivationCodes ||
    canManageMobileDeviceActivationCodes;
  // 紧急登录凭证同时改变设备与系统级安全边界，两个权限缺一不可。
  const canManageEmergencyLoginGrants =
    canManageDeviceRegistration && hasPermission(PERMISSIONS.System.ManageSettings);
  const canViewEmployeeProfiles = hasPermission(PERMISSIONS.EmployeeProfiles.View);
  const canViewAttendancePersonal =
    isAdmin ||
    ATTENDANCE_PERSONAL_PERMISSIONS.some((permission) =>
      hasPermission(permission)
    );
  const canViewAttendanceManagement =
    isAdmin ||
    ATTENDANCE_MANAGEMENT_PERMISSIONS.some((permission) =>
      hasPermission(permission)
    );
  const canReviewAttendance =
    isAdmin ||
    ATTENDANCE_REVIEW_PERMISSIONS.some((permission) =>
      hasPermission(permission)
    );
  const canEditAttendanceHoliday =
    isAdmin || hasPermission(PERMISSIONS.Attendance.HolidayEditManagedStore);
  const canEditAttendanceSettings =
    isAdmin || hasPermission(PERMISSIONS.Attendance.SettingsEdit);
  const canViewAttendancePunchRecords =
    isAdmin || hasPermission(PERMISSIONS.Attendance.PunchViewManagedStore);
  // 代员工补录/修改打卡并直接生效；后端还会校验管理分店与工资周窗口。
  const canAdjustAttendancePunch =
    isAdmin || hasPermission(PERMISSIONS.Attendance.PunchAdjustManagedStore);
  const canViewLocalPurchase = hasPermission(PERMISSIONS.LocalPurchase.View);
  const canEditLocalPurchase = hasPermission(PERMISSIONS.LocalPurchase.Edit);
  const canPushLocalPurchaseToHq = hasPermission(PERMISSIONS.LocalPurchase.PushToHq);
  const canViewInstallmentOrders = hasPermission(PERMISSIONS.InstallmentOrders.View);
  const canViewAdvertisements = hasPermission(PERMISSIONS.Advertisements.View);
  const canManageAdvertisements = hasPermission(PERMISSIONS.Advertisements.Edit);
  const canViewStoreVouchers = hasPermission(PERMISSIONS.StoreVouchers.View);
  const canViewSeasonalCardRemaining = hasPermission(
    PERMISSIONS.SeasonalCards.Remaining.ViewManagedStore
  );
  const canSubmitSeasonalCardRemaining = hasPermission(
    PERMISSIONS.SeasonalCards.Remaining.SubmitManagedStore
  );
  const canCreateStoreProducts =
    hasPermission(PERMISSIONS.StoreProducts.Create);
  const canManageAttendance = canViewAttendanceManagement;

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
    canCreateOrder,
    canEditOrder,
    canWriteOrder,
    canDeleteOrder,
    canReadProduct,
    canWriteProduct,
    canDeleteProduct,
    canReadUser,
    canWriteUser,
    canDeleteUser,
    canReadRole,
    canWriteRole,
    canDeleteRole,
    canReadStore,
    canWriteStore,
    canDeleteStore,
    canManageWarehouse,
    canViewContainers,
    canCreateContainer,
    canEditContainer,
    canDeleteContainer,
    canManageStore,
    canViewReports,
    canViewDeviceRegistration,
    canViewLegacyEmployeeLogs,
    canViewPosOperationAudits,
    canReviewLegacyEmployeeLogs,
    canManageDeviceRegistration,
    canManageDeviceActivationCodes,
    canManageMobileDeviceActivationCodes,
    canManageEmergencyLoginGrants,
    canViewEmployeeProfiles,
    canViewAttendancePersonal,
    canViewAttendanceManagement,
    canReviewAttendance,
    canEditAttendanceHoliday,
    canEditAttendanceSettings,
    canViewAttendancePunchRecords,
    canAdjustAttendancePunch,
    canViewLocalPurchase,
    canEditLocalPurchase,
    canPushLocalPurchaseToHq,
    canViewInstallmentOrders,
    canViewAdvertisements,
    canManageAdvertisements,
    canViewStoreVouchers,
    canViewSeasonalCardRemaining,
    canSubmitSeasonalCardRemaining,
    canCreateStoreProducts,
    canManageAttendance,
    hasPermission,
    hasRole,
    onlyRole,
    hasAnyRole,
    hasAllRoles,
    assignedStoreCodes,
    managedStoreCodes,
  };
}
