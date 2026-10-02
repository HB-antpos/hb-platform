export const STORE_STAFF_ROLE = "StoreStaff";

export interface StoreUserListItem {
  userGUID: string;
  username: string;
  fullName?: string;
  email?: string;
  phone?: string;
  status: number;
  storeCode?: string;
  storeName?: string;
  roleNames: string[];
  birthday?: string;
  gender?: string;
  employmentType?: string;
  /** 仅考勤员工接口返回：未满 18 岁时的周岁。 */
  age?: number;
  /** 仅考勤员工接口返回：是否店长角色。 */
  isStoreManager?: boolean;
  /** 仅考勤员工接口返回：店长且本店是其主分店（管理本店）。 */
  managesStore?: boolean;
  lastLoginTime?: string;
  lastLoginIp?: string;
  createdAt?: string;
  updatedAt?: string;
}

export interface StoreUserDetail extends StoreUserListItem {
  remarks?: string;
}

export interface StoreUserProfile extends StoreUserDetail {
  identityId?: string;
  avatarUrl?: string;
  address?: string;
  bankBsb?: string;
  bankAccountNumber?: string;
  superannuationCompanyName?: string;
  superannuationCompanyCode?: string;
  superannuationAccountNumber?: string;
}

export interface StoreUserGridParams {
  storeCode?: string | null;
  keyword?: string;
}

export interface StoreUserMutationInput {
  username: string;
  fullName?: string;
  email?: string;
  phone?: string;
  password?: string;
  passwordFormat?: "raw" | "clientSha256";
  status: number;
}

export interface StoreUserUpdatePayload extends StoreUserMutationInput {
  userGuid: string;
  storeCode: string;
  roleNames?: string[];
}

export interface StoreUserCreatePayload extends StoreUserMutationInput {
  password: string;
  storeCode: string;
  roleNames?: string[];
  employmentType?: "casual";
}

export interface StoreUserStatusPayload {
  userGuid: string;
  storeCode: string;
  status: number;
}

export interface StoreUserPasswordPayload {
  userGuid: string;
  storeCode: string;
  newPassword: string;
  passwordFormat?: "raw" | "clientSha256";
}

export interface StoreUserFormValues {
  username: string;
  fullName: string;
  email: string;
  phone: string;
  status: boolean;
}

export interface StaffCashierBarcodeResponse {
  exists: boolean;
  barcode: string;
  format: string;
  printCount: number;
  createdAt?: string;
  updatedAt?: string;
}

export interface PosTerminalPermissionOption {
  code: string;
  name: string;
  group: string;
  description: string;
}

export interface StoreUserPosTerminalPermissions {
  mode: string;
  assignablePermissions: PosTerminalPermissionOption[];
  inheritedPermissionCodes: string[];
  overriddenPermissionCodes: string[];
  grantedPermissionCodes: string[];
  effectivePermissionCodes: string[];
}

export interface StoreUserPosTerminalPermissionTarget {
  userGuid: string;
  storeGuid: string;
}

export interface UpdateStoreUserPosTerminalPermissionsPayload
  extends StoreUserPosTerminalPermissionTarget {
  grantedPermissionCodes: string[];
}
