export const EMPLOYMENT_TYPES = ["fullTime", "partTime", "casual"] as const;

export type EmploymentType = (typeof EMPLOYMENT_TYPES)[number];

export const GENDERS = ["male", "female", "other"] as const;

export type Gender = (typeof GENDERS)[number];

export const EMPLOYEE_PROFILE_IMAGE_KINDS = ["avatar", "identityPhoto"] as const;

export type EmployeeProfileImageKind = (typeof EMPLOYEE_PROFILE_IMAGE_KINDS)[number];

export interface EmployeeProfile {
  username: string;
  displayName?: string;
  phone: string;
  email?: string;
  bankBsb: string;
  bankAccountNumber: string;
  superannuationCompanyName: string;
  superannuationCompanyCode: string;
  superannuationAccountNumber: string;
  birthday: string;
  gender: string;
  employmentType: string;
  avatarUrl: string;
  identityType: string;
  identityId: string;
  identityPhotoUrl: string;
  identityPhotoUrlExpiresAt?: string;
  address: string;
  createdAt?: string;
  updatedAt?: string;
  sensitiveRevision: number;
}

// 生日属于敏感资料，只能经 SensitiveEmployeeProfilePayload 提交审核，基本资料保存不得携带。
export interface UpdateEmployeeProfilePayload {
  phone: string;
  email?: string;
  gender: string;
  employmentType?: string;
  address: string;
}

export type EmployeeProfileSensitiveChangeStatus =
  | "Pending"
  | "Approved"
  | "Rejected"
  | "Superseded"
  | "Withdrawn";

export interface SensitiveEmployeeProfilePayload {
  /** YYYY-MM-DD；空串表示不填，提交时转为 null。 */
  birthday: string;
  bankBsb: string;
  bankAccountNumber: string;
  superannuationCompanyName: string;
  superannuationCompanyCode: string;
  superannuationAccountNumber: string;
  identityType: string;
  identityId: string;
  expectedSensitiveRevision?: number;
}

export interface EmployeeProfileSensitiveChangeRequest extends SensitiveEmployeeProfilePayload {
  requestId: number;
  status: EmployeeProfileSensitiveChangeStatus;
  hasIdentityPhoto: boolean;
  identityPhotoUrl: string;
  identityPhotoUrlExpiresAt?: string;
  baseSensitiveRevision: number;
  submittedAt: string;
  submittedBy?: string;
  reviewedAt?: string;
  reviewedBy?: string;
  reviewReason?: string;
  changedFields: string[];
}

/** 本人申请历史条目：后端只返回状态与字段标识，不含任何敏感值。 */
export interface EmployeeProfileSensitiveChangeHistoryItem {
  requestId: number;
  status: EmployeeProfileSensitiveChangeStatus;
  changedFields: string[];
  submittedAt: string;
  reviewedAt?: string;
  reviewReason?: string;
}

export interface DirectUploadRequest {
  fileName: string;
  contentType: string;
  fileSize: number;
}

export interface DirectUploadSignature {
  url: string;
  objectKey: string;
  headers: Record<string, string>;
}

export interface CashierBarcodeResponse {
  exists: boolean;
  barcode: string;
  format: string;
  printCount: number;
  createdAt?: string;
  updatedAt?: string;
}
