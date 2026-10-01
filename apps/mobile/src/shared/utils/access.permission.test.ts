import { buildAccess, PERMISSIONS } from "./access";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function createUser(
  permissions: string[],
  roleNames = ["User"],
  stores: NonNullable<Parameters<typeof buildAccess>[0]>["stores"] = []
): NonNullable<Parameters<typeof buildAccess>[0]> {
  return {
    userGuid: "user-1",
    userGUID: "user-1",
    username: "tester",
    email: "tester@example.com",
    permissions,
    roleNames,
    storeNames: [],
    stores,
  };
}

const legacyLogViewer = buildAccess(createUser([PERMISSIONS.LegacyEmployeeLogs.View]));
assertEqual(
  legacyLogViewer.canViewLegacyEmployeeLogs,
  true,
  "LegacyEmployeeLogs.View enables the employee operation logs entrance"
);
assertEqual(
  legacyLogViewer.canReviewLegacyEmployeeLogs,
  false,
  "viewing legacy logs does not imply reviewing anomalies"
);
assertEqual(
  buildAccess(createUser([PERMISSIONS.LegacyEmployeeLogs.Review])).canReviewLegacyEmployeeLogs,
  true,
  "LegacyEmployeeLogs.Review enables anomaly review"
);
assertEqual(
  buildAccess(createUser([PERMISSIONS.PosTerminal.AuditView])).canViewLegacyEmployeeLogs,
  false,
  "new POS audit permission alone no longer opens the mobile employee operation logs"
);
assertEqual(
  buildAccess(createUser(["PosTerminal.Audit.View"])).hasPermission(PERMISSIONS.PosTerminal.AuditView),
  true,
  "legacy PosTerminal.Audit.View without prefix is treated as the same permission"
);

const purchaseLegacyAccess = buildAccess(
  createUser(["LocalInvocie.View", "LocalInvocie.Edit"])
);

assertEqual(
  buildAccess(createUser(["EmployeeProfiles.Edit"])).hasPermission(PERMISSIONS.EmployeeProfiles.EditPositionType),
  false,
  "普通资料编辑权限不隐式授予职位类型修改权限"
);
assertEqual(
  buildAccess(createUser([PERMISSIONS.EmployeeProfiles.EditPositionType])).hasPermission(PERMISSIONS.EmployeeProfiles.EditPositionType),
  true,
  "显式职位类型权限允许修改该字段"
);

assertEqual(
  purchaseLegacyAccess.hasPermission(PERMISSIONS.LocalPurchase.View),
  true,
  "legacy LocalInvocie.View aliases to LocalPurchase.View"
);

assertEqual(
  purchaseLegacyAccess.hasPermission(PERMISSIONS.LocalPurchase.Edit),
  true,
  "legacy LocalInvocie.Edit aliases to LocalPurchase.Edit"
);

assertEqual(
  purchaseLegacyAccess.canViewLocalPurchase,
  true,
  "legacy LocalInvocie.View enables local purchase view capability"
);

assertEqual(
  purchaseLegacyAccess.canEditLocalPurchase,
  true,
  "legacy LocalInvocie.Edit enables local purchase edit capability"
);

const deviceViewerAccess = buildAccess(
  createUser([PERMISSIONS.DeviceRegistration.View])
);

assertEqual(
  deviceViewerAccess.canViewDeviceRegistration,
  true,
  "DeviceRegistration.View enables device management entrance capability"
);

assertEqual(
  deviceViewerAccess.canManageDeviceRegistration,
  false,
  "DeviceRegistration.View does not enable device management action capability"
);

const deviceManagerAccess = buildAccess(
  createUser([PERMISSIONS.DeviceRegistration.Manage])
);

assertEqual(
  deviceManagerAccess.canViewDeviceRegistration,
  true,
  "DeviceRegistration.Manage implies device management entrance capability"
);

assertEqual(
  deviceManagerAccess.canManageDeviceRegistration,
  true,
  "DeviceRegistration.Manage enables device management action capability"
);

const posActivationAccess = buildAccess(
  createUser([PERMISSIONS.DeviceRegistration.ActivationCodesManage])
);

assertEqual(
  posActivationAccess.canManageDeviceActivationCodes,
  true,
  "DeviceRegistration.ActivationCodes.Manage enables POS activation codes only"
);

assertEqual(
  posActivationAccess.canViewDeviceRegistration,
  true,
  "POS activation-code permission enables the device management entrance"
);

assertEqual(
  posActivationAccess.canManageMobileDeviceActivationCodes,
  false,
  "POS activation-code permission does not enable mobile activation codes"
);

const mobileActivationAccess = buildAccess(
  createUser([PERMISSIONS.DeviceRegistration.MobileActivationCodesManage])
);

assertEqual(
  mobileActivationAccess.canManageMobileDeviceActivationCodes,
  true,
  "DeviceRegistration.MobileActivationCodes.Manage enables mobile activation codes only"
);

assertEqual(
  mobileActivationAccess.canViewDeviceRegistration,
  true,
  "mobile activation-code permission enables the device management entrance"
);

assertEqual(
  buildAccess(createUser([PERMISSIONS.DeviceRegistration.Manage])).canManageEmergencyLoginGrants,
  false,
  "device management alone cannot create an emergency login grant"
);

assertEqual(
  buildAccess(createUser([PERMISSIONS.System.ManageSettings])).canManageEmergencyLoginGrants,
  false,
  "system settings alone cannot create an emergency login grant"
);

assertEqual(
  buildAccess(createUser([
    PERMISSIONS.DeviceRegistration.Manage,
    PERMISSIONS.System.ManageSettings,
  ])).canManageEmergencyLoginGrants,
  true,
  "emergency login grants require both device management and system settings"
);

const attendancePersonalAccess = buildAccess(
  createUser([PERMISSIONS.Attendance.ScheduleViewSelf])
);

assertEqual(
  attendancePersonalAccess.canViewAttendancePersonal,
  true,
  "Attendance.Schedule.ViewSelf enables personal attendance capability"
);

assertEqual(
  attendancePersonalAccess.canViewAttendanceManagement,
  false,
  "personal attendance permission does not enable management attendance capability"
);

const attendanceManagerAccess = buildAccess(
  createUser([PERMISSIONS.Attendance.ApprovalReviewManagedStore])
);

assertEqual(
  attendanceManagerAccess.canViewAttendanceManagement,
  true,
  "managed attendance permissions enable attendance management capability"
);

assertEqual(
  attendanceManagerAccess.canReviewAttendance,
  true,
  "review managed attendance permission enables review capability"
);

const attendanceHolidayEditorAccess = buildAccess(
  createUser([PERMISSIONS.Attendance.HolidayEditManagedStore])
);

assertEqual(
  attendanceHolidayEditorAccess.canEditAttendanceHoliday,
  true,
  "holiday edit permission enables attendance holiday edit capability"
);

const attendanceSettingsEditorAccess = buildAccess(
  createUser([PERMISSIONS.Attendance.SettingsEdit])
);

assertEqual(
  attendanceSettingsEditorAccess.canEditAttendanceSettings,
  true,
  "attendance settings edit permission enables attendance settings capability"
);

const storeFinanceAccess = buildAccess(
  createUser([
    PERMISSIONS.LocalPurchase.PushToHq,
    PERMISSIONS.StoreProducts.Create,
    PERMISSIONS.InstallmentOrders.View,
    PERMISSIONS.Advertisements.View,
    PERMISSIONS.Advertisements.Edit,
    PERMISSIONS.StoreVouchers.View,
    PERMISSIONS.SeasonalCards.Remaining.ViewManagedStore,
    PERMISSIONS.SeasonalCards.Remaining.SubmitManagedStore,
  ])
);

assertEqual(
  storeFinanceAccess.canPushLocalPurchaseToHq,
  true,
  "LocalPurchase.PushToHq enables local purchase push capability"
);

assertEqual(
  storeFinanceAccess.canCreateStoreProducts,
  true,
  "StoreProducts.Create enables store product creation capability"
);

assertEqual(
  buildAccess(createUser(["StoreProductManagement.Create"], ["StoreManager"])).canCreateStoreProducts,
  true,
  "StoreProductManagement.Create direct grant enables store product creation capability"
);

assertEqual(
  buildAccess(createUser(["StoreProducts.Edit"], ["StoreManager"])).canCreateStoreProducts,
  false,
  "StoreProducts.Edit alone does not enable store product creation capability"
);

assertEqual(
  buildAccess(createUser([])).canCreateStoreProducts,
  false,
  "missing StoreProducts.Create keeps store product creation capability disabled"
);

assertEqual(
  storeFinanceAccess.canViewInstallmentOrders,
  true,
  "InstallmentOrders.View enables installment orders capability"
);

assertEqual(
  storeFinanceAccess.canViewAdvertisements,
  true,
  "Advertisements.View enables advertisements capability"
);

assertEqual(
  storeFinanceAccess.canManageAdvertisements,
  true,
  "Advertisements.Edit enables advertisements management capability"
);

assertEqual(
  buildAccess(createUser([PERMISSIONS.Advertisements.Edit])).canViewAdvertisements,
  false,
  "Advertisements.Edit alone does not enable advertisements list visibility"
);

assertEqual(
  storeFinanceAccess.canViewStoreVouchers,
  true,
  "StoreVouchers.View enables store vouchers capability"
);

assertEqual(
  storeFinanceAccess.canViewSeasonalCardRemaining,
  true,
  "SeasonalCards.Remaining.ViewManagedStore enables seasonal card remaining history capability"
);

assertEqual(
  storeFinanceAccess.canSubmitSeasonalCardRemaining,
  true,
  "SeasonalCards.Remaining.SubmitManagedStore enables seasonal card remaining submission capability"
);

const scopedStoreAccess = buildAccess(
  createUser([], ["StoreManager"], [
    { storeCode: "1006", storeName: "HB WARE HOUSE", isPrimary: false },
    { storeCode: "1004", storeName: "Campbelltown", isPrimary: true },
  ])
);

assertEqual(
  scopedStoreAccess.assignedStoreCodes()?.join(","),
  "1006,1004",
  "assignedStoreCodes keeps all assigned stores including read-only stores"
);

assertEqual(
  scopedStoreAccess.managedStoreCodes()?.join(","),
  "1004",
  "managedStoreCodes keeps only isPrimary manageable stores"
);

const warehouseStaffCreateAccess = buildAccess(
  createUser(["Orders.Create"], ["WarehouseStaff"])
);

assertEqual(
  warehouseStaffCreateAccess.isWarehouseStaffOnly,
  true,
  "pure WarehouseStaff is marked separately from warehouse managers and admins"
);

assertEqual(
  warehouseStaffCreateAccess.canCreateOrder,
  true,
  "Orders.Create enables official order creation capability"
);

assertEqual(
  warehouseStaffCreateAccess.canEditOrder,
  false,
  "Orders.Create alone does not enable official order line maintenance"
);

const warehouseStaffEditAccess = buildAccess(
  createUser(["Orders.Edit"], ["仓库员工"])
);

assertEqual(
  warehouseStaffEditAccess.isWarehouseStaffOnly,
  true,
  "Chinese WarehouseStaff role is treated as pure warehouse staff"
);

assertEqual(
  warehouseStaffEditAccess.canCreateOrder,
  false,
  "Orders.Edit alone does not enable official order creation capability"
);

assertEqual(
  warehouseStaffEditAccess.canEditOrder,
  true,
  "Orders.Edit enables official order line maintenance capability"
);

const warehouseStaffLegacyManageAccess = buildAccess(
  createUser(["Warehouse.Manage"], ["WarehouseStaff"])
);

assertEqual(
  warehouseStaffLegacyManageAccess.canCreateOrder,
  false,
  "legacy Warehouse.Manage alone does not grant official order creation"
);

assertEqual(
  warehouseStaffLegacyManageAccess.canEditOrder,
  false,
  "legacy Warehouse.Manage alone does not grant official order line maintenance"
);

const warehouseManagerAccess = buildAccess(
  createUser(["Orders.Create", "Orders.Edit"], ["WarehouseManager"])
);

const warehouseManagerAliasAccess = buildAccess(
  createUser([], ["Warehouse"])
);

assertEqual(
  warehouseManagerAliasAccess.isWarehouseManager,
  true,
  "legacy Warehouse alias is treated as a warehouse manager"
);

assertEqual(
  warehouseManagerAccess.isWarehouseStaffOnly,
  false,
  "WarehouseManager is not treated as pure WarehouseStaff"
);

assertEqual(
  warehouseManagerAccess.canViewContainers,
  false,
  "WarehouseManager role alone does not enable container visibility"
);

assertEqual(
  warehouseManagerAccess.canCreateContainer,
  false,
  "WarehouseManager role alone does not enable container creation"
);

assertEqual(
  warehouseManagerAccess.canEditContainer,
  false,
  "WarehouseManager role alone does not enable container editing"
);

assertEqual(
  warehouseManagerAccess.canDeleteContainer,
  false,
  "WarehouseManager role alone does not enable container deletion"
);

const adminAccess = buildAccess(
  createUser(["Orders.Create", "Orders.Edit"], ["Admin"])
);

assertEqual(
  adminAccess.isWarehouseStaffOnly,
  false,
  "Admin is not treated as pure WarehouseStaff"
);

assertEqual(
  adminAccess.canViewContainers,
  true,
  "Admin can view container list and detail"
);

assertEqual(
  adminAccess.canCreateContainer,
  true,
  "Admin can create containers"
);

assertEqual(
  adminAccess.canEditContainer,
  true,
  "Admin can edit containers"
);

assertEqual(
  adminAccess.canDeleteContainer,
  true,
  "Admin can delete containers"
);

const containerViewAccess = buildAccess(
  createUser([PERMISSIONS.Container.View])
);

assertEqual(
  containerViewAccess.canViewContainers,
  true,
  "Container.View enables container list and detail visibility"
);

assertEqual(
  containerViewAccess.canCreateContainer,
  false,
  "Container.View alone does not enable container creation"
);

const containerMutationAccess = buildAccess(
  createUser([
    PERMISSIONS.Container.Create,
    PERMISSIONS.Container.Edit,
    PERMISSIONS.Container.Delete,
  ])
);

assertEqual(
  containerMutationAccess.canCreateContainer,
  true,
  "Container.Create enables container creation"
);

assertEqual(
  containerMutationAccess.canEditContainer,
  true,
  "Container.Edit enables container editing"
);

assertEqual(
  containerMutationAccess.canDeleteContainer,
  true,
  "Container.Delete enables container deletion"
);
