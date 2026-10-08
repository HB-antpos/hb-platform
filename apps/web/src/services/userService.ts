import type { ApiResponse, PagedResult } from '../types/api'
import type { RoleOptionDto } from '../types/role'
import type {
  CreateUserDto,
  UpdateUserDto,
  UserPermissionAssignmentDto,
  UserAccessPermissionDto,
  UserPermissionStateDto,
  UserLoginRecordDto,
  UserLoginRecordQueryDto,
  UpdateUserPasswordDto,
  UserDetailDto,
  UserDto,
  UserQueryDto,
  UserRoleAssignmentDto,
  UserStorePosTerminalPermissionsResponse,
  EmployeeCashierBarcodeDto,
  UserStoreAssignmentDto,
  UserStoreDto,
  UpdateUserStorePosTerminalPermissionsRequest,
} from '../types/user'
import request, { unwrapApiData, unwrapPagedResult } from '../utils/request'

type UserStoreApiDto = Omit<UserStoreDto, 'isManageable'> & {
  IsActive?: boolean
  isManageable?: boolean
  isPrimary?: boolean
}

const mapUserStore = (store: UserStoreApiDto): UserStoreDto => {
  const { IsActive, isPrimary, isManageable, ...rest } = store
  return {
    ...rest,
    // 用户关联分店可能已停用，前端仍保留状态给详情展示和后续判断。
    isActive: rest.isActive ?? IsActive,
    isManageable: isManageable ?? isPrimary ?? false,
  }
}

export async function getUsers(params: UserQueryDto): Promise<PagedResult<UserDto>> {
  const response = await request.get<ApiResponse<PagedResult<UserDto>>>('/api/Users/optimized', {
    params: params as Record<string, unknown>,
  })
  return unwrapPagedResult(response)
}

export async function createUser(payload: CreateUserDto): Promise<UserDto> {
  const response = await request.post<ApiResponse<UserDto>>('/api/Users', {
    Username: payload.username,
    Email: payload.email,
    Password: payload.password,
    PasswordFormat: payload.passwordFormat,
    FullName: payload.fullName ?? null,
    IsActive: payload.isActive ?? true,
    RoleGuids: payload.roleGuids ?? [],
    StoreGuids: payload.storeGuids ?? [],
  })
  return unwrapApiData(response)
}

export async function getUserByGuid(guid: string): Promise<UserDetailDto> {
  const response = await request.get<ApiResponse<UserDetailDto>>(`/api/Users/guid/${guid}`)
  return unwrapApiData(response)
}

export async function getUserCashierBarcode(guid: string): Promise<EmployeeCashierBarcodeDto> {
  const response = await request.get<ApiResponse<EmployeeCashierBarcodeDto>>(
    `/api/Users/guid/${encodeURIComponent(guid)}/cashier-barcode`,
    { cache: 'no-store' },
  )
  return unwrapApiData(response)
}

export async function refreshUserCashierBarcode(
  guid: string,
  expectedBarcode: string | null,
): Promise<EmployeeCashierBarcodeDto> {
  const response = await request.post<ApiResponse<EmployeeCashierBarcodeDto>>(
    `/api/Users/guid/${encodeURIComponent(guid)}/cashier-barcode/refresh`,
    { expectedBarcode },
  )
  return unwrapApiData(response)
}

export async function getUserLoginRecords(
  guid: string,
  params: UserLoginRecordQueryDto,
): Promise<PagedResult<UserLoginRecordDto>> {
  const response = await request.get<ApiResponse<PagedResult<UserLoginRecordDto>>>(
    `/api/Users/guid/${guid}/login-records`,
    { params: params as Record<string, unknown> },
  )
  return unwrapPagedResult(response)
}

export async function getUserStores(guid: string): Promise<UserStoreDto[]> {
  const response = await request.get<ApiResponse<UserStoreApiDto[]>>(`/api/Users/guid/${guid}/stores`)
  return (unwrapApiData(response) ?? []).map(mapUserStore)
}

export async function updateUser(guid: string, payload: UpdateUserDto): Promise<UserDetailDto> {
  const response = await request.put<ApiResponse<UserDetailDto>>(`/api/Users/guid/${guid}`, payload)
  return unwrapApiData(response)
}

/**
 * 批量启用 / 停用：后端在一个事务里逐个校验目标（不存在、无权编辑、当前账号都会让整批回滚），
 * 所以要么全部生效、要么全部不变。
 */
export async function batchSetUsersActive(userGuids: string[], isActive: boolean): Promise<boolean> {
  const response = await request.post<ApiResponse<boolean>>('/api/Users/batch', {
    userGuids,
    operation: isActive ? 'activate' : 'deactivate',
  })
  return unwrapApiData(response)
}

export async function getUserRoles(guid: string): Promise<RoleOptionDto[]> {
  const response = await request.get<ApiResponse<RoleOptionDto[]>>(`/api/Users/guid/${guid}/roles`)
  return unwrapApiData(response) ?? []
}

export async function assignRolesToUser(guid: string, payload: UserRoleAssignmentDto): Promise<boolean> {
  const response = await request.post<ApiResponse<boolean>>(`/api/Users/guid/${guid}/roles`, {
    RoleGuids: payload.roleGuids,
  })
  return unwrapApiData(response)
}

export async function getUserPermissionState(guid: string): Promise<UserPermissionStateDto> {
  const response = await request.get<ApiResponse<UserPermissionStateDto>>(
    `/api/Users/guid/${guid}/permissions/state`,
  )
  return unwrapApiData(response)
}

export async function getUserAccessPermissions(guid: string): Promise<UserAccessPermissionDto> {
  const response = await request.get<ApiResponse<UserAccessPermissionDto>>(
    `/api/Users/guid/${guid}/access-permissions`,
  )
  return unwrapApiData(response)
}

export async function assignPermissionsToUser(
  guid: string,
  payload: UserPermissionAssignmentDto,
): Promise<boolean> {
  const response = await request.post<ApiResponse<boolean>>(
    `/api/Users/guid/${guid}/permissions`,
    { permissions: payload.permissions },
  )
  return unwrapApiData(response)
}

function getUserStorePosTerminalPermissionsPath(userGuid: string, storeGuid: string) {
  return `/api/Users/guid/${userGuid}/stores/${storeGuid}/pos-terminal-permissions`
}

export async function getUserStorePosTerminalPermissions(
  userGuid: string,
  storeGuid: string,
): Promise<UserStorePosTerminalPermissionsResponse> {
  const response = await request.get<ApiResponse<UserStorePosTerminalPermissionsResponse>>(
    getUserStorePosTerminalPermissionsPath(userGuid, storeGuid),
  )
  return unwrapApiData(response)
}

export async function updateUserStorePosTerminalPermissions(
  userGuid: string,
  storeGuid: string,
  payload: UpdateUserStorePosTerminalPermissionsRequest,
): Promise<UserStorePosTerminalPermissionsResponse> {
  const response = await request.put<ApiResponse<UserStorePosTerminalPermissionsResponse>>(
    getUserStorePosTerminalPermissionsPath(userGuid, storeGuid),
    // 分店覆盖接口只接受授权码，禁止透传响应中的继承或有效权限字段。
    { grantedPermissionCodes: payload.grantedPermissionCodes },
  )
  return unwrapApiData(response)
}

export async function deleteUserStorePosTerminalPermissions(
  userGuid: string,
  storeGuid: string,
): Promise<UserStorePosTerminalPermissionsResponse> {
  const response = await request.delete<ApiResponse<UserStorePosTerminalPermissionsResponse>>(
    getUserStorePosTerminalPermissionsPath(userGuid, storeGuid),
  )
  return unwrapApiData(response)
}

export async function assignStoresToUser(guid: string, payload: UserStoreAssignmentDto[]): Promise<boolean> {
  const response = await request.post<ApiResponse<boolean>>(
    `/api/Users/guid/${guid}/stores`,
    payload.map((item) => ({
      StoreGUID: item.storeGUID,
      AccessLevel: item.accessLevel ?? 'ReadWrite',
      IsPrimary: item.isManageable ?? false,
    })),
  )
  return unwrapApiData(response)
}

export async function updateUserPassword(guid: string, dto: UpdateUserPasswordDto): Promise<boolean> {
  const response = await request.put<ApiResponse<boolean>>(
    `/api/Users/guid/${guid}/password`,
    {
      NewPassword: dto.newPassword,
      PasswordFormat: dto.passwordFormat,
      ForcePasswordChange: dto.forcePasswordChange,
    },
  )
  return unwrapApiData(response)
}
