import type { ApiResponse, PagedResult } from '../types/api'
import type { RoleOptionDto } from '../types/role'
import type {
  BatchUserStoreOperationRequest,
  BatchUserStoreOperationResult,
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
    // 姓名拆分字段：创建时未填写即为 null（后端按「名 / 中间名 / 姓」是否有值决定是否重算 FullName）。
    GivenName: payload.givenName ?? null,
    MiddleName: payload.middleName ?? null,
    FamilyName: payload.familyName ?? null,
    Nickname: payload.nickname ?? null,
    IsActive: payload.isActive ?? true,
    // 新建员工：电话、生日（YYYY-MM-DD）必填；监护人只在未满 18 岁时带，成年不传该字段。
    Phone: payload.phone ?? null,
    Birthday: payload.birthday ?? null,
    ...(payload.guardian
      ? {
          Guardian: {
            Name: payload.guardian.name,
            Relationship: payload.guardian.relationship,
            Phone: payload.guardian.phone,
            Email: payload.guardian.email,
          },
        }
      : {}),
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

/**
 * 更新用户资料。payload 原样作为请求体：
 * givenName / middleName / familyName / nickname 的语义是 undefined 或 null = 不修改、空串 = 清空、非空 = 设置，
 * 调用方应只传有变化的字段（见 usersPageLogic 的 diffUserNameParts），不要把未变化的旧值回传。
 */
export async function updateUser(guid: string, payload: UpdateUserDto): Promise<UserDetailDto> {
  const response = await request.put<ApiResponse<UserDetailDto>>(`/api/Users/guid/${guid}`, payload)
  return unwrapApiData(response)
}

/**
 * 开关某账号「允许登录 Web / App」（管理员用，需要 Users.Edit）。
 * 与启用 / 停用账号是两回事：账号保留，但被限制后任何人都登不进来，已登录的会话也会失效。
 * 失败时抛 RequestError，payload.errorCode 可能是 SELF_PROFILE_FIELDS_DENIED / USER_NOT_FOUND /
 * USER_SCOPE_DENIED / HIGH_PRIVILEGE_TARGET_DENIED（页面按错误码给中文提示，见 usersPageLogic）。
 */
export async function updateUserLoginEnabled(guid: string, loginEnabled: boolean): Promise<boolean> {
  const response = await request.put<ApiResponse<boolean>>(
    `/api/Users/guid/${encodeURIComponent(guid)}/login-enabled`,
    // 与同文件 updateUser / batchSetUsersActive 一致用 camelCase；后端必填，缺字段会 400 而不是按 false 处理。
    { loginEnabled },
  )
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

/**
 * 批量添加 / 移除分店关联：后端单事务执行，任一目标不合法整批不写入；
 * dryRun=true 时只返回影响统计，用于确认前预览。
 */
export async function batchManageUserStores(payload: BatchUserStoreOperationRequest): Promise<BatchUserStoreOperationResult> {
  const response = await request.post<ApiResponse<BatchUserStoreOperationResult>>('/api/Users/batch-stores', {
    userGuids: payload.userGuids,
    storeGuids: payload.storeGuids,
    operation: payload.operation,
    asManageable: payload.asManageable ?? false,
    dryRun: payload.dryRun ?? false,
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
