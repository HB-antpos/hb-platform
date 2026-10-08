import type { PermissionCategoryDto } from './role'

export interface UserQueryDto {
  page?: number
  pageNumber?: number
  pageSize?: number
  search?: string
  searchKeyword?: string
  roleGuid?: string
  storeGuid?: string
  isActive?: boolean
  sortBy?: string
  sortDirection?: string
}

export interface UserLoginRecordQueryDto {
  page?: number
  pageSize?: number
}

export type UserLoginRecordStatus = 'active' | 'revoked' | 'expired'

export interface UserLoginRecordDto {
  sessionId: string
  loginAt: string
  ipAddress?: string
  userAgent?: string
  expiresAt: string
  isRevoked: boolean
  isExpired: boolean
  status: UserLoginRecordStatus
}

export interface UserStoreDto {
  storeGUID: string
  storeName: string
  storeCode: string
  isActive?: boolean
  isManageable: boolean
  assignedAt: string
}

export interface UserDto {
  userGUID: string
  username: string
  email: string
  fullName?: string
  phone?: string
  lastLoginAt?: string
  lastLoginIp?: string
  isActive: boolean
  createdAt: string
  updatedAt: string
  currentStore?: string
  roleNames: string[]
  storeNames: string[]
  stores?: UserStoreDto[]
  permissions?: string[]
}

export interface UserDetailDto extends UserDto {}

export interface EmployeeCashierBarcodeDto {
  exists: boolean
  barcode: string | null
  format: string
  printCount: number
  createdAt?: string | null
  updatedAt?: string | null
}

export interface UpdateUserDto {
  username: string
  email: string
  fullName?: string
  isActive?: boolean
}

export interface UserRoleAssignmentDto {
  roleGuids: string[]
}

export interface UserPermissionInheritedSourceDto {
  roleName: string
  permissionCodes: string[]
}

export interface UserPermissionStateDto {
  userGuid: string
  isSuperAdmin: boolean
  implicitAllPermissions: boolean
  inheritedPermissionCodes: string[]
  directPermissionCodes: string[]
  effectivePermissionCodes: string[]
  inheritedSources: UserPermissionInheritedSourceDto[]
}

export interface UserAccessPermissionDto {
  state: UserPermissionStateDto
  categories: PermissionCategoryDto[]
}

export interface UserPermissionAssignmentDto {
  permissions: string[]
}

export interface PosTerminalPermissionOptionDto {
  code: string
  name: string
  group: string
  description: string
}

export interface UserStorePosTerminalPermissionsResponse {
  mode: string
  assignablePermissions: PosTerminalPermissionOptionDto[]
  inheritedPermissionCodes: string[]
  overriddenPermissionCodes: string[]
  grantedPermissionCodes: string[]
  effectivePermissionCodes: string[]
}

export interface UpdateUserStorePosTerminalPermissionsRequest {
  grantedPermissionCodes: string[]
}

export interface UserStoreAssignmentDto {
  storeGUID: string
  accessLevel?: string
  isManageable?: boolean
}

export type BatchUserStoreOperation = 'add' | 'remove'

export interface BatchUserStoreOperationRequest {
  userGuids: string[]
  storeGuids: string[]
  operation: BatchUserStoreOperation
  /** 仅添加：同时设为可管理分店（只有管理员可以授予） */
  asManageable?: boolean
  /** 只计算影响、不写入 */
  dryRun?: boolean
}

/** 批量分店操作的影响统计；预演与正式执行返回同一结构。 */
export interface BatchUserStoreOperationResult {
  dryRun: boolean
  addedCount: number
  upgradedCount: number
  removedCount: number
  removedManageableCount: number
  unchangedCount: number
  protectedManageableCount: number
  affectedUserCount: number
  usersLosingStoreManagerRole: string[]
  usersGainingStoreManagerRole: string[]
}

export interface CreateUserDto {
  username: string
  email: string
  password: string
  passwordFormat?: 'raw' | 'clientSha256'
  fullName?: string
  isActive?: boolean
  roleGuids?: string[]
  storeGuids?: string[]
}

export interface UpdateUserPasswordDto {
  newPassword: string
  passwordFormat?: 'raw' | 'clientSha256'
  forcePasswordChange?: boolean
}
