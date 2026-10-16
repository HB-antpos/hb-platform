export interface RoleQueryDto {
  page?: number
  pageNumber?: number
  pageSize?: number
  searchKeyword?: string
  isActive?: boolean
  sortBy?: string
  sortDirection?: string
}

export interface RoleDto {
  roleGUID: string
  roleName: string
  description?: string
  isActive: boolean
  createdAt: string
  updatedAt: string
  userCount: number
}

export interface RoleUserDto {
  userGUID: string
  username: string
  email: string
  fullName?: string
  // 姓名拆分字段（后端可空），与 UserDto 一致；此处内联以避免 role.ts 反向依赖 user.ts。
  givenName?: string | null
  middleName?: string | null
  familyName?: string | null
  nickname?: string | null
  isActive: boolean
  assignedAt: string
}

/** 权限直接授权用户的增量调整；与后端 PermissionUserAssignmentDto 对应 */
export interface PermissionUserAssignmentDto {
  addUserGuids: string[]
  removeUserGuids: string[]
}

export interface RoleDetailDto extends RoleDto {
  users: RoleUserDto[]
  permissions: string[]
}

export interface UpdateRoleDto {
  roleName: string
  description?: string
  isActive?: boolean
}

export interface CreateRoleDto {
  roleName: string
  description?: string
  isActive?: boolean
}

export interface RoleOptionDto {
  roleGUID: string
  roleName: string
  description?: string
  isActive?: boolean
}

/** Single permission returned by GET /api/roles/permissions */
export interface PermissionDto {
  name: string
  displayName: string
  description?: string
  category: string
  isSystemPermission: boolean
  /** 使用端：pos / web / mobile，可同时包含多个；旧后端没有该字段。 */
  surfaces?: string[]
  createdAt: string
  createdBy?: string
  updatedAt?: string
  updatedBy?: string
}

/** Permission category with its child permissions */
export interface PermissionCategoryDto {
  category: string
  displayName: string
  description?: string
  permissions: PermissionDto[]
}

export interface PermissionAliasDto {
  canonicalCode: string
  aliasCodes: string[]
}

export interface RolePermissionTemplateDto {
  roleName: string
  permissionCodes: string[]
}

export interface PermissionCatalogDto {
  categories: PermissionCategoryDto[]
  permissionAliases: PermissionAliasDto[]
  roleTemplates: RolePermissionTemplateDto[]
  superAdminRoleNames: string[]
}

export interface RolePermissionStateDto {
  roleGuid: string
  roleName: string
  isSuperAdmin: boolean
  implicitAllPermissions: boolean
  explicitPermissionCodes: string[]
  effectivePermissionCodes: string[]
}

/** Request body for POST /api/roles/guid/{guid}/permissions */
export interface RolePermissionAssignmentDto {
  permissions: string[]
}

export interface SysPermissionDto {
  id: string
  code: string
  name: string
  category: string
  description?: string
}

export interface CreateSysPermissionDto {
  code: string
  name: string
  category: string
  description?: string
  actions?: string[]
}
