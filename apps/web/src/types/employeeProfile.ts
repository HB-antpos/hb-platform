export type EmployeeProfileGender = 'male' | 'female' | 'other' | 'unknown'

export type EmployeeEmploymentType = 'fullTime' | 'partTime' | 'casual'

export interface EmployeeProfileQueryDto {
  keyword?: string
  page?: number
  pageSize?: number
  /** 仅 /managed 列表支持：按分店过滤。 */
  storeGuid?: string
  /** 仅 /managed 列表支持：按角色过滤。 */
  roleGuid?: string
}

/** /managed 筛选下拉：后端已按调用者范围裁剪（店长只有管理分店，且不含管理员 / 店长 / 仓库经理角色）。 */
export interface EmployeeProfileFilterOptions {
  stores: Array<{ storeGuid: string; storeCode: string; storeName: string }>
  roles: Array<{ roleGuid: string; roleName: string }>
}

/** /managed 详情：非敏感资料明文，敏感资料只有脱敏摘要，不可回写。 */
export interface EmployeeProfileManagedDetailDto {
  userGUID: string
  username?: string
  email?: string
  fullName?: string
  hasProfile: boolean
  storeNames: string[]
  roleNames: string[]
  phone?: string
  gender?: EmployeeProfileGender
  employmentType?: EmployeeEmploymentType
  address?: string
  avatarUrl?: string
  bankBsb?: string
  bankAccountNumber?: string
  superannuationCompanyName?: string
  superannuationCompanyCode?: string
  superannuationAccountNumber?: string
  identityType?: string
  identityId?: string
  hasBirthday: boolean
  /** 调用者是否持有「修改职位类型」权限；没有时员工类型置灰。 */
  canEditEmploymentType: boolean
  updatedAt?: string
  updatedBy?: string
}

/** /managed 保存：只含非敏感白名单；键「未带」表示不改，带空字符串表示清空。 */
export interface SaveManagedEmployeeProfilePayload {
  phone?: string
  gender?: EmployeeProfileGender | ''
  employmentType?: EmployeeEmploymentType | ''
  address?: string
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
  /** /managed 列表返回：所属分店 / 角色名称（非管理员只含其管理范围内的分店）。 */
  storeNames?: string[]
  roleNames?: string[]
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
