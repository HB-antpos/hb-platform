export const EMPLOYEE_PROFILE_SENSITIVE_FIELDS = [
  "birthday",
  "bankBsb",
  "bankAccountNumber",
  "superannuationCompanyName",
  "superannuationCompanyCode",
  "superannuationAccountNumber",
  "identityType",
  "identityId",
  "identityPhotoUrl",
] as const;

export type EmployeeProfileSensitiveField =
  (typeof EMPLOYEE_PROFILE_SENSITIVE_FIELDS)[number];

export type EmployeeProfileReviewStatus =
  | "Pending"
  | "Approved"
  | "Rejected"
  | "Superseded";

/** 列表模型严格限制为非敏感摘要，禁止加入账号、证件号或审核原因。 */
export interface EmployeeProfileReviewSummary {
  requestId: number;
  userGuid: string;
  username: string;
  status: EmployeeProfileReviewStatus;
  baseSensitiveRevision: number;
  submittedAt: string;
  reviewedAt?: string;
  changedFields: EmployeeProfileSensitiveField[];
  storeCodes: string[];
  storeNames: string[];
}

export interface EmployeeProfileReviewPage {
  items: EmployeeProfileReviewSummary[];
  total: number;
  page: number;
  pageSize: number;
}

export interface EmployeeProfileSensitiveSnapshot {
  /** YYYY-MM-DD，空串表示未填写。 */
  birthday: string;
  bankBsb: string;
  bankAccountNumber: string;
  superannuationCompanyName: string;
  superannuationCompanyCode: string;
  superannuationAccountNumber: string;
  identityType: string;
  identityId: string;
  hasIdentityPhoto: boolean;
  identityPhotoUrl: string;
}

export interface EmployeeProfileReviewDetail extends EmployeeProfileReviewSummary,
  EmployeeProfileSensitiveSnapshot {
  identityPhotoUrlExpiresAt?: string;
  submittedBy?: string;
  reviewedBy?: string;
  reviewReason?: string;
  currentSnapshot: EmployeeProfileSensitiveSnapshot;
}

export interface EmployeeProfileReviewQuery {
  page?: number;
  pageSize?: number;
  status?: EmployeeProfileReviewStatus;
  search?: string;
}

/** Mutation 只保留审核结果标识，完整敏感详情绝不进入 MutationCache。 */
export interface EmployeeProfileReviewMutationResult {
  requestId: number;
  status: EmployeeProfileReviewStatus;
}
