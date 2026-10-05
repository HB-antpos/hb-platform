export type EmployeeProfileGender = 'male' | 'female' | 'other' | 'unknown'

export type EmployeeEmploymentType = 'fullTime' | 'partTime' | 'casual'

export interface EmployeeProfileQueryDto {
  keyword?: string
  page?: number
  pageSize?: number
}

export interface EmployeeProfileSummaryDto {
  id?: string
  userId?: string
  userGUID?: string
  /** 列表接口返回：该用户是否已有员工资料记录（未建档的用户所有资料字段都为空）。 */
  hasProfile?: boolean
  username?: string
  displayName?: string
  bankBsb?: string
  bankAccountNumber?: string
  superannuationCompanyName?: string
  superannuationCompanyCode?: string
  superannuationAccountNumber?: string
  birthday?: string
  gender?: EmployeeProfileGender
  employmentType?: EmployeeEmploymentType
  avatarUrl?: string
  identityId?: string
  identityType?: string
  identityPhotoUrl?: string
  identityPhotoUrlExpiresAt?: string
  address?: string
  createdAt?: string
  updatedAt?: string
}

export interface EmployeeProfileDetailDto extends EmployeeProfileSummaryDto {
  sensitiveRevision: number
}

export interface SaveEmployeeProfilePayload {
  id?: string
  userId?: string
  userGUID?: string
  username?: string
  displayName?: string
  bankBsb?: string
  bankAccountNumber?: string
  superannuationCompanyName?: string
  superannuationCompanyCode?: string
  superannuationAccountNumber?: string
  birthday?: string
  gender?: EmployeeProfileGender
  employmentType?: EmployeeEmploymentType
  avatarUrl?: string
  identityId?: string
  identityType?: string
  identityPhotoUrl?: string
  address?: string
  confirmSupersedePendingSensitiveChangeRequest?: boolean
  expectedSensitiveRevision?: number
}

export type EmployeeProfileSensitiveChangeStatus = 'Pending' | 'Approved' | 'Rejected' | 'Superseded' | 'Withdrawn'
export type EmployeeProfileSensitiveField =
  | 'birthday'
  | 'bankBsb'
  | 'bankAccountNumber'
  | 'superannuationCompanyName'
  | 'superannuationCompanyCode'
  | 'superannuationAccountNumber'
  | 'identityType'
  | 'identityId'
  | 'identityPhotoUrl'

export interface EmployeeProfileSensitiveChangeQueryDto {
  keyword?: string
  page?: number
  pageSize?: number
  status?: EmployeeProfileSensitiveChangeStatus
}

export interface EmployeeProfileSensitiveChangeSummaryDto {
  requestId: number
  userGuid: string
  username?: string
  status: EmployeeProfileSensitiveChangeStatus
  baseSensitiveRevision: number
  submittedAt: string
  reviewedAt?: string
  reviewReason?: string
  changedFields: EmployeeProfileSensitiveField[]
}

export interface EmployeeProfileSensitiveChangeDetailDto extends EmployeeProfileSensitiveChangeSummaryDto {
  birthday?: string
  bankBsb?: string
  bankAccountNumber?: string
  superannuationCompanyName?: string
  superannuationCompanyCode?: string
  superannuationAccountNumber?: string
  identityType?: string
  identityId?: string
  hasIdentityPhoto: boolean
  identityPhotoUrl?: string
  identityPhotoUrlExpiresAt?: string
  submittedBy?: string
  reviewedBy?: string
}

export interface EmployeeProfileSensitiveReviewPayload {
  reason?: string
}

export interface EmployeeProfileSensitiveRejectPayload {
  reason: string
}
