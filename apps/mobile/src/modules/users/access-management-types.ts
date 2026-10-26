export type UserAccessStoreState = "unassigned" | "view" | "manage";

export interface AccessStoreOption {
  storeGUID: string;
  storeCode: string;
  storeName: string;
  isActive?: boolean;
}

export interface UserAccessStore extends AccessStoreOption {
  isManageable: boolean;
  assignedAt?: string;
}

export interface UserAccessStoreAssignment {
  storeGUID: string;
  isPrimary: boolean;
}

export interface UserAccessRole {
  roleGUID: string;
  roleName: string;
  description?: string;
  isActive?: boolean;
}

export interface AccessPermission {
  name: string;
  displayName: string;
  description?: string;
  category: string;
  isSystemPermission: boolean;
}

export interface AccessPermissionCategory {
  category: string;
  displayName: string;
  description?: string;
  permissions: AccessPermission[];
}

export interface UserAccessPermissionInheritedSource {
  roleName: string;
  permissionCodes: string[];
}

export interface UserAccessPermissionState {
  userGuid: string;
  isSuperAdmin: boolean;
  implicitAllPermissions: boolean;
  inheritedPermissionCodes: string[];
  directPermissionCodes: string[];
  effectivePermissionCodes: string[];
  inheritedSources: UserAccessPermissionInheritedSource[];
}

export interface UserAccessPermissionAccess {
  state: UserAccessPermissionState;
  categories: AccessPermissionCategory[];
}

export interface DirectPermissionDraft {
  baselineCodes: string[];
  selectedCodes: string[];
  inheritedPermissionCodes: string[];
}

export interface AssignUserAccessStoresInput {
  userGuid: string;
  assignments: UserAccessStoreAssignment[];
}

export interface AssignUserAccessRolesInput {
  userGuid: string;
  roleGuids: string[];
  roleCatalog: UserAccessRole[];
}

export interface AssignUserDirectPermissionsInput {
  userGuid: string;
  permissions: string[];
}

/** 复制权限方式：merge 在对方现有直接权限上叠加；replace 用来源的直接权限整体覆盖。 */
export type CopyPermissionMode = "merge" | "replace";

/** toOthers：当前员工是来源、选中的多人是目标；fromColleague：所选同事是来源、当前员工是唯一目标。 */
export type CopyPermissionDirection = "toOthers" | "fromColleague";

export interface CopyUserDirectPermissionsInput {
  sourceUserGuid: string;
  targetUserGuids: string[];
  mode: CopyPermissionMode;
}

export interface UserDirectPermissionCopyTarget {
  userGuid: string;
  added: string[];
  removed: string[];
  unchangedCount: number;
}

export interface UserDirectPermissionCopyResult {
  sourceUserGuid: string;
  mode: CopyPermissionMode;
  preview: boolean;
  /** 来源的全部直接权限。 */
  sourceDirectPermissionCodes: string[];
  /** 来源直接权限中，当前操作者允许分配的部分。 */
  transferablePermissionCodes: string[];
  /** 操作者不能分配而被自动跳过的权限。 */
  skippedPermissionCodes: string[];
  targets: UserDirectPermissionCopyTarget[];
}

export type UserAccessSectionMode = "hidden" | "readOnly" | "editable";

export type UserAccessEligibilityReason =
  "deviceMode" | "self" | "privilegedTarget" | "noPermission";

export interface UserAccessEligibilityContext {
  isDeviceMode: boolean;
  isAdmin: boolean;
  isStoreManager: boolean;
  /** 仓库管理：不限分店的受限委派者（可授员工角色、授管理 / 查看分店关系，但不能碰管理员与仓库管理账号）。 */
  isWarehouseManager?: boolean;
  canManageStores: boolean;
  canManageRoles: boolean;
  canManagePos: boolean;
  currentUserGuid?: string | null;
  targetUserGuid: string;
  targetStatus: number;
  targetRoleNames: string[];
  hasManageableStores: boolean;
}

export interface UserAccessEligibility {
  canOpen: boolean;
  storesMode: UserAccessSectionMode;
  rolesMode: UserAccessSectionMode;
  permissionsMode: UserAccessSectionMode;
  canManagePos: boolean;
  canGrantStoreManagement: boolean;
  reason: UserAccessEligibilityReason | null;
}

export type UserAccessErrorKind =
  "unauthorized" | "forbidden" | "notFound" | "network";
