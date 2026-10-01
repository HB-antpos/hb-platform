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
  phone?: string;
  permissions: string[];
  roleNames: string[];
  storeNames: string[];
  stores: UserStoreDto[];
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
  canManageStore: boolean;
  canViewReports: boolean;
  canViewDeviceRegistration: boolean;
  canViewLegacyEmployeeLogs: boolean;
  canReviewLegacyEmployeeLogs: boolean;
  canManageDeviceRegistration: boolean;
  canManageDeviceActivationCodes: boolean;
  canManageMobileDeviceActivationCodes: boolean;
  canManageEmergencyLoginGrants: boolean;
  canViewEmployeeProfiles: boolean;
  canViewAttendancePersonal: boolean;
  canViewAttendanceManagement: boolean;
  canReviewAttendance: boolean;
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
