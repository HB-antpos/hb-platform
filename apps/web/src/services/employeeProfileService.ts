import type { ApiResponse, PagedResult } from '../types/api'
import type {
  EmployeeProfileDetailDto,
  EmployeeProfileFilterOptions,
  EmployeeProfileManagedDetailDto,
  EmployeeProfileQueryDto,
  EmployeeProfileSensitiveChangeDetailDto,
  EmployeeProfileSensitiveChangeQueryDto,
  EmployeeProfileSensitiveChangeStatus,
  EmployeeProfileSensitiveChangeSummaryDto,
  EmployeeProfileSensitiveField,
  EmployeeProfileSensitiveRejectPayload,
  EmployeeProfileSensitiveReviewPayload,
  EmployeeProfileSummaryDto,
  SaveEmployeeProfilePayload,
  SaveManagedEmployeeProfilePayload,
} from '../types/employeeProfile'
import request, { unwrapApiData, unwrapPagedResult } from '../utils/request'

const ADMIN_BASE_PATH = '/api/EmployeeProfiles/admin'
const ME_BASE_PATH = '/api/EmployeeProfiles/me'
const MANAGED_BASE_PATH = '/api/EmployeeProfiles/managed'

type BackendEmployeeProfile = Record<string, unknown>

function asString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

function mapEmployeeProfile<T extends EmployeeProfileSummaryDto>(raw: BackendEmployeeProfile): T {
  return {
    id: asString(raw.id) ?? asString(raw.employeeInfoId) ?? asString(raw.EmployeeInfoId),
    userGUID: asString(raw.userGUID) ?? asString(raw.UserGUID) ?? asString(raw.userGuid),
    userId: asString(raw.userId) ?? asString(raw.UserId) ?? asString(raw.userGUID) ?? asString(raw.UserGUID),
    // 三态：缺失保持 undefined，避免旧响应被误判为「未建档」。
    hasProfile: typeof (raw.hasProfile ?? raw.HasProfile) === 'boolean'
      ? (raw.hasProfile ?? raw.HasProfile) as boolean
      : undefined,
    username: asString(raw.username) ?? asString(raw.Username),
    displayName: asString(raw.displayName) ?? asString(raw.DisplayName) ?? asString(raw.fullName) ?? asString(raw.FullName),
    bankBsb: asString(raw.bankBsb) ?? asString(raw.bankBSB) ?? asString(raw.BankBSB),
    bankAccountNumber: asString(raw.bankAccountNumber) ?? asString(raw.bankACC) ?? asString(raw.BankACC),
    superannuationCompanyName: asString(raw.superannuationCompanyName) ?? asString(raw.SuperannuationCompanyName),
    superannuationCompanyCode: asString(raw.superannuationCompanyCode) ?? asString(raw.SuperannuationCompanyCode),
    superannuationAccountNumber:
      asString(raw.superannuationAccountNumber) ?? asString(raw.superannuationAccount) ?? asString(raw.SuperannuationAccount),
    birthday: asString(raw.birthday) ?? asString(raw.Birthday),
    gender: (asString(raw.gender) ?? asString(raw.Gender)) as T['gender'],
    employmentType: (asString(raw.employmentType) ?? asString(raw.employeeType) ?? asString(raw.EmployeeType)) as T['employmentType'],
    avatarUrl: asString(raw.avatarUrl) ?? asString(raw.AvatarUrl),
    identityId: asString(raw.identityId) ?? asString(raw.IdentityId),
    identityType: asString(raw.identityType) ?? asString(raw.IdentityType),
    identityPhotoUrl: asString(raw.identityPhotoUrl) ?? asString(raw.IdentityPhotoUrl),
    identityPhotoUrlExpiresAt: asString(raw.identityPhotoUrlExpiresAt) ?? asString(raw.IdentityPhotoUrlExpiresAt),
    address: asString(raw.address) ?? asString(raw.Address),
    createdAt: asString(raw.createdAt) ?? asString(raw.CreatedAt),
    updatedAt: asString(raw.updatedAt) ?? asString(raw.UpdatedAt),
    sensitiveRevision: asNumber(raw.sensitiveRevision ?? raw.SensitiveRevision),
  } as unknown as T
}

function toBackendPayload(payload: SaveEmployeeProfilePayload) {
  return {
    userGUID: payload.userGUID ?? payload.userId,
    bankBsb: payload.bankBsb,
    bankAccountNumber: payload.bankAccountNumber,
    superannuationCompanyName: payload.superannuationCompanyName,
    superannuationCompanyCode: payload.superannuationCompanyCode,
    superannuationAccountNumber: payload.superannuationAccountNumber,
    birthday: payload.birthday,
    gender: payload.gender,
    employmentType: payload.employmentType,
    avatarUrl: payload.avatarUrl,
    identityId: payload.identityId,
    identityType: payload.identityType,
    identityPhotoUrl: payload.identityPhotoUrl,
    address: payload.address,
    confirmSupersedePendingSensitiveChangeRequest: payload.confirmSupersedePendingSensitiveChangeRequest,
    expectedSensitiveRevision: payload.expectedSensitiveRevision,
  }
}

export async function getAdminEmployeeProfiles(params: EmployeeProfileQueryDto): Promise<PagedResult<EmployeeProfileSummaryDto>> {
  const response = await request.get<ApiResponse<PagedResult<EmployeeProfileSummaryDto>>>(ADMIN_BASE_PATH, {
    params: {
      page: params.page,
      pageSize: params.pageSize,
      search: params.keyword,
    },
  })
  const result = unwrapPagedResult(response)
  return {
    ...result,
    items: result.items.map((item) => mapEmployeeProfile<EmployeeProfileSummaryDto>(item as BackendEmployeeProfile)),
  }
}

export async function getAdminEmployeeProfile(id: string): Promise<EmployeeProfileDetailDto> {
  const response = await request.get<ApiResponse<EmployeeProfileDetailDto>>(`${ADMIN_BASE_PATH}/${id}`)
  return mapEmployeeProfile<EmployeeProfileDetailDto>(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}

export async function saveAdminEmployeeProfile(payload: SaveEmployeeProfilePayload): Promise<EmployeeProfileDetailDto> {
  const userGuid = payload.userGUID ?? payload.userId
  if (!userGuid) {
    throw new Error('Missing user GUID')
  }
  const response = await request.put<ApiResponse<EmployeeProfileDetailDto>>(
    `${ADMIN_BASE_PATH}/${userGuid}`,
    toBackendPayload(payload),
  )
  return mapEmployeeProfile<EmployeeProfileDetailDto>(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}

function asStringList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string' && item.trim() !== '') : []
}

/**
 * 员工个人信息维护列表（Web 页面 / 手机端共用的 /managed 接口）：
 * 数据范围由后端按身份强制（管理员全部分店，店长只限管理分店），这里只负责把过滤条件带上。
 */
export async function getManagedEmployeeProfiles(params: EmployeeProfileQueryDto): Promise<PagedResult<EmployeeProfileSummaryDto>> {
  const response = await request.get<ApiResponse<PagedResult<EmployeeProfileSummaryDto>>>(MANAGED_BASE_PATH, {
    params: {
      page: params.page,
      pageSize: params.pageSize,
      search: params.keyword,
      storeGuid: params.storeGuid,
      roleGuid: params.roleGuid,
    },
  })
  const result = unwrapPagedResult(response)
  return {
    ...result,
    items: result.items.map((item) => ({
      ...mapEmployeeProfile<EmployeeProfileSummaryDto>(item as BackendEmployeeProfile),
      storeNames: asStringList((item as BackendEmployeeProfile).storeNames),
      roleNames: asStringList((item as BackendEmployeeProfile).roleNames),
    })),
  }
}

export async function getManagedEmployeeProfileFilterOptions(): Promise<EmployeeProfileFilterOptions> {
  const response = await request.get<ApiResponse<EmployeeProfileFilterOptions>>(`${MANAGED_BASE_PATH}/filter-options`)
  const data = unwrapApiData(response)
  return { stores: data?.stores ?? [], roles: data?.roles ?? [] }
}

function mapManagedDetail(raw: BackendEmployeeProfile): EmployeeProfileManagedDetailDto {
  return {
    userGUID: asString(raw.userGUID) ?? '',
    username: asString(raw.username),
    email: asString(raw.email),
    fullName: asString(raw.fullName),
    hasProfile: raw.hasProfile === true,
    storeNames: asStringList(raw.storeNames),
    roleNames: asStringList(raw.roleNames),
    phone: asString(raw.phone),
    gender: asString(raw.gender) as EmployeeProfileManagedDetailDto['gender'],
    employmentType: asString(raw.employmentType) as EmployeeProfileManagedDetailDto['employmentType'],
    address: asString(raw.address),
    avatarUrl: asString(raw.avatarUrl),
    bankBsb: asString(raw.bankBsb),
    bankAccountNumber: asString(raw.bankAccountNumber),
    superannuationCompanyName: asString(raw.superannuationCompanyName),
    superannuationCompanyCode: asString(raw.superannuationCompanyCode),
    superannuationAccountNumber: asString(raw.superannuationAccountNumber),
    identityType: asString(raw.identityType),
    identityId: asString(raw.identityId),
    hasBirthday: raw.hasBirthday === true,
    canEditEmploymentType: raw.canEditEmploymentType === true,
    updatedAt: asString(raw.updatedAt),
    updatedBy: asString(raw.updatedBy),
  }
}

export async function getManagedEmployeeProfile(userGuid: string): Promise<EmployeeProfileManagedDetailDto> {
  const response = await request.get<ApiResponse<EmployeeProfileManagedDetailDto>>(
    `${MANAGED_BASE_PATH}/${encodeURIComponent(userGuid)}`,
  )
  return mapManagedDetail(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}

/** 只提交「改动过」的非敏感字段；后端靠「键有没有带」区分未改与清空。 */
export async function saveManagedEmployeeProfile(
  userGuid: string,
  payload: SaveManagedEmployeeProfilePayload,
): Promise<EmployeeProfileManagedDetailDto> {
  const response = await request.put<ApiResponse<EmployeeProfileManagedDetailDto>>(
    `${MANAGED_BASE_PATH}/${encodeURIComponent(userGuid)}`,
    payload,
  )
  return mapManagedDetail(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}

export async function getMyEmployeeProfile(): Promise<EmployeeProfileDetailDto> {
  const response = await request.get<ApiResponse<EmployeeProfileDetailDto>>(ME_BASE_PATH)
  return mapEmployeeProfile<EmployeeProfileDetailDto>(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}

export async function updateMyEmployeeProfile(payload: SaveEmployeeProfilePayload): Promise<EmployeeProfileDetailDto> {
  const response = await request.put<ApiResponse<EmployeeProfileDetailDto>>(ME_BASE_PATH, toBackendPayload(payload))
  return mapEmployeeProfile<EmployeeProfileDetailDto>(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}

function asNumber(value: unknown, fallback = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function asBoolean(value: unknown) {
  return value === true
}

export function mapSensitiveStatus(value: unknown): EmployeeProfileSensitiveChangeStatus {
  switch (asString(value)?.toLowerCase()) {
    case 'pending':
      return 'Pending'
    case 'approved':
      return 'Approved'
    case 'rejected':
      return 'Rejected'
    case 'superseded':
      return 'Superseded'
    case 'withdrawn':
      // 员工本人撤回，与作废一样是不可操作的终态。
      return 'Withdrawn'
    default:
      // 未知状态必须保持不可操作，不能误判为待审核。
      return 'Superseded'
  }
}

const SENSITIVE_FIELD_NAMES = new Set<EmployeeProfileSensitiveField>([
  'birthday',
  'bankBsb',
  'bankAccountNumber',
  'superannuationCompanyName',
  'superannuationCompanyCode',
  'superannuationAccountNumber',
  'identityType',
  'identityId',
  'identityPhotoUrl',
])

function mapSensitiveChangedFields(value: unknown): EmployeeProfileSensitiveField[] {
  if (!Array.isArray(value)) {
    return []
  }
  return value.filter(
    (field): field is EmployeeProfileSensitiveField => typeof field === 'string'
      && SENSITIVE_FIELD_NAMES.has(field as EmployeeProfileSensitiveField),
  )
}

function mapSensitiveChangeSummary(raw: BackendEmployeeProfile): EmployeeProfileSensitiveChangeSummaryDto {
  return {
    requestId: asNumber(raw.requestId ?? raw.RequestId),
    userGuid: asString(raw.userGuid) ?? asString(raw.UserGuid) ?? asString(raw.userGUID) ?? asString(raw.UserGUID) ?? '',
    username: asString(raw.username) ?? asString(raw.Username),
    status: mapSensitiveStatus(raw.status ?? raw.Status),
    baseSensitiveRevision: asNumber(raw.baseSensitiveRevision ?? raw.BaseSensitiveRevision),
    submittedAt: asString(raw.submittedAt) ?? asString(raw.SubmittedAt) ?? '',
    reviewedAt: asString(raw.reviewedAt) ?? asString(raw.ReviewedAt),
    reviewReason: asString(raw.reviewReason) ?? asString(raw.ReviewReason),
    changedFields: mapSensitiveChangedFields(raw.changedFields ?? raw.ChangedFields),
  }
}

function mapSensitiveChangeDetail(raw: BackendEmployeeProfile): EmployeeProfileSensitiveChangeDetailDto {
  return {
    ...mapSensitiveChangeSummary(raw),
    birthday: asString(raw.birthday) ?? asString(raw.Birthday),
    bankBsb: asString(raw.bankBsb) ?? asString(raw.BankBsb),
    bankAccountNumber: asString(raw.bankAccountNumber) ?? asString(raw.BankAccountNumber),
    superannuationCompanyName: asString(raw.superannuationCompanyName) ?? asString(raw.SuperannuationCompanyName),
    superannuationCompanyCode: asString(raw.superannuationCompanyCode) ?? asString(raw.SuperannuationCompanyCode),
    superannuationAccountNumber:
      asString(raw.superannuationAccountNumber) ?? asString(raw.SuperannuationAccountNumber),
    identityType: asString(raw.identityType) ?? asString(raw.IdentityType),
    identityId: asString(raw.identityId) ?? asString(raw.IdentityId),
    hasIdentityPhoto: asBoolean(raw.hasIdentityPhoto ?? raw.HasIdentityPhoto),
    identityPhotoUrl: asString(raw.identityPhotoUrl) ?? asString(raw.IdentityPhotoUrl),
    identityPhotoUrlExpiresAt: asString(raw.identityPhotoUrlExpiresAt) ?? asString(raw.IdentityPhotoUrlExpiresAt),
    submittedBy: asString(raw.submittedBy) ?? asString(raw.SubmittedBy),
    reviewedBy: asString(raw.reviewedBy) ?? asString(raw.ReviewedBy),
  }
}

export async function getAdminSensitiveChangeRequests(
  params: EmployeeProfileSensitiveChangeQueryDto,
): Promise<PagedResult<EmployeeProfileSensitiveChangeSummaryDto>> {
  const response = await request.get<ApiResponse<PagedResult<EmployeeProfileSensitiveChangeSummaryDto>>>(
    `${ADMIN_BASE_PATH}/change-requests`,
    {
      params: {
        page: params.page,
        pageSize: params.pageSize,
        status: params.status,
        search: params.keyword,
      },
    },
  )
  const result = unwrapPagedResult(response)
  return {
    ...result,
    // 列表只保留后端摘要 DTO 白名单，即使异常响应夹带完整账号也不会进入页面状态。
    items: result.items.map((item) => mapSensitiveChangeSummary(item as unknown as BackendEmployeeProfile)),
  }
}

export async function getAdminSensitiveChangeRequest(
  requestId: number,
): Promise<EmployeeProfileSensitiveChangeDetailDto> {
  const response = await request.get<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>>(
    `${ADMIN_BASE_PATH}/change-requests/${requestId}`,
  )
  return mapSensitiveChangeDetail(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}

export async function approveAdminSensitiveChangeRequest(
  requestId: number,
  payload: EmployeeProfileSensitiveReviewPayload,
): Promise<EmployeeProfileSensitiveChangeDetailDto> {
  const response = await request.post<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>>(
    `${ADMIN_BASE_PATH}/change-requests/${requestId}/approve`,
    payload,
  )
  return mapSensitiveChangeDetail(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}

export async function rejectAdminSensitiveChangeRequest(
  requestId: number,
  payload: EmployeeProfileSensitiveRejectPayload,
): Promise<EmployeeProfileSensitiveChangeDetailDto> {
  const response = await request.post<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>>(
    `${ADMIN_BASE_PATH}/change-requests/${requestId}/reject`,
    payload,
  )
  return mapSensitiveChangeDetail(unwrapApiData(response) as unknown as BackendEmployeeProfile)
}
