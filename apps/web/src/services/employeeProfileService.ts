import type { ApiResponse, PagedResult } from '../types/api'
import type {
  EmployeeProfileDetailDto,
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
} from '../types/employeeProfile'
import request, { unwrapApiData, unwrapPagedResult } from '../utils/request'

const ADMIN_BASE_PATH = '/api/EmployeeProfiles/admin'
const ME_BASE_PATH = '/api/EmployeeProfiles/me'

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
