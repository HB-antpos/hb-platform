export interface LoginRequest {
  username: string;
  password: string;
  passwordFormat?: "raw" | "clientSha256";
  hardwareId?: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  storeCode?: string;
  locationLatitude?: number;
  locationLongitude?: number;
  locationAccuracy?: number;
  locationPermissionStatus?: string;
  locationCapturedAtUtc?: string;
}

export interface TokenResponse {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiry: string;
  refreshTokenExpiry: string;
  success: boolean;
  message: string;
  isDeviceSwitched?: boolean;
  isCommonDevice?: boolean;
}

export interface RefreshTokenRequest {
  accessToken: string;
  refreshToken: string;
}

export interface UserStoreDto {
  storeGUID?: string;
  storeCode: string;
  storeName: string;
  /** 后端门店的 POS 启用开关，不代表设备在线状态。 */
  isActive?: boolean;
  postcode?: string;
  stateCode?: string;
  isPrimary?: boolean;
  assignedAt?: string;
}

export interface CurrentUser {
  userGuid: string;
  userGUID: string;
  username: string;
  email: string;
  fullName?: string;
  /** 姓名拆分字段（/auth/current 在迁移 20261018 之后返回；旧后端或旧缓存缺省）。 */
  givenName?: string;
  middleName?: string;
  familyName?: string;
  nickname?: string;
  phone?: string;
  permissions: string[];
  roleNames: string[];
  storeNames: string[];
  stores: UserStoreDto[];
  /** 店长新建或重置密码后须先改成自己的密码；旧后端不返回时视为 false。 */
  mustChangePassword?: boolean;
  /**
   * 门店管理类功能是否不限分店（后端 /auth/current 给出；旧后端缺省时按角色回退：管理员或仓库经理）。
   * 后端开关 Access:WarehouseManagerStoreScoped 打开后仓库经理为 false，按分配的分店收口。
   */
  hasAllStoreScope?: boolean;
  /** 仓库本职业务（订货 / 发货等）是否不限分店：管理员与仓库经理为 true（旧后端缺省时按角色回退）。 */
  hasWarehouseWideScope?: boolean;
}

export interface AccessControl {
  isAdmin: boolean;
  isManager: boolean;
  isUser: boolean;
  isWarehouseStaff: boolean;
  isWarehouseStaffOnly: boolean;
  isWarehouseManager: boolean;
  isStoreStaff: boolean;
  isStoreManager: boolean;
  isStoreLevelManager: boolean;
  /** 门店管理类功能是否不限分店（见 CurrentUser.hasAllStoreScope）。 */
  hasAllStoreScope: boolean;
  /** 仓库本职业务是否不限分店（见 CurrentUser.hasWarehouseWideScope）。 */
  hasWarehouseWideScope: boolean;
  /**
   * 账号授权里的「仓库委派者」（不限分店、可授管理关系、可改店长）：仓库经理且门店口径不限分店。
   * 后端口径收口后为 false，仓库经理按店长规则（只管自己「管理」分店的员工）。
   */
  isWarehouseAccessDelegate: boolean;
  onlyOrder: boolean;
  canReadOrder: boolean;
  canCreateOrder: boolean;
  canEditOrder: boolean;
  canWriteOrder: boolean;
  canDeleteOrder: boolean;
  canReadProduct: boolean;
  canWriteProduct: boolean;
  canDeleteProduct: boolean;
  canReadUser: boolean;
  canWriteUser: boolean;
  canDeleteUser: boolean;
  canReadRole: boolean;
  canWriteRole: boolean;
  canDeleteRole: boolean;
  canReadStore: boolean;
  canWriteStore: boolean;
  canDeleteStore: boolean;
  canManageWarehouse: boolean;
  canViewContainers: boolean;
  canCreateContainer: boolean;
  canEditContainer: boolean;
  canDeleteContainer: boolean;
  /** 可点按钮查看完整货柜编号（默认只显示后 4 位） */
  canViewFullContainerNumber: boolean;
  /** 可查看国内供应商名称（没有时后端只返回供应商代码，如 HB196） */
  canViewDomesticSupplierName: boolean;
  canManageStore: boolean;
  canViewReports: boolean;
  canViewDeviceRegistration: boolean;
  canViewLegacyEmployeeLogs: boolean;
  /** 新收银操作审计（员工操作日志页的「新收银」来源）。 */
  canViewPosOperationAudits: boolean;
  /** 日结记录（独立权限码 DailyCloseRecords.View）。 */
  canViewDailyCloseRecords: boolean;
  canReviewLegacyEmployeeLogs: boolean;
  canManageDeviceRegistration: boolean;
  canManageDeviceActivationCodes: boolean;
  canManageMobileDeviceActivationCodes: boolean;
  canManageEmergencyLoginGrants: boolean;
  canViewEmployeeProfiles: boolean;
  canViewAttendancePersonal: boolean;
  canViewAttendanceManagement: boolean;
  canReviewAttendance: boolean;
  /** Attendance.Approval.ReviewSelf：可审核本人申请（需二次确认）。 */
  canReviewOwnAttendance: boolean;
  canEditAttendanceHoliday: boolean;
  canEditAttendanceSettings: boolean;
  canViewAttendancePunchRecords: boolean;
  canAdjustAttendancePunch: boolean;
  canViewLocalPurchase: boolean;
  canEditLocalPurchase: boolean;
  canPushLocalPurchaseToHq: boolean;
  canViewInstallmentOrders: boolean;
  canViewAdvertisements: boolean;
  canManageAdvertisements: boolean;
  canViewStoreVouchers: boolean;
  canViewSeasonalCardRemaining: boolean;
  canSubmitSeasonalCardRemaining: boolean;
  canCreateStoreProducts: boolean;
  canManageAttendance: boolean;
  hasPermission: (permission: string) => boolean;
  hasRole: (role: string) => boolean;
  onlyRole: (role: string) => boolean;
  hasAnyRole: (roles: string[]) => boolean;
  hasAllRoles: (roles: string[]) => boolean;
  assignedStoreCodes: () => string[] | null;
  managedStoreCodes: () => string[] | null;
}
