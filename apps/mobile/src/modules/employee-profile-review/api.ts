import type {
  EmployeeProfileReviewDetail,
  EmployeeProfileReviewMutationResult,
  EmployeeProfileReviewPage,
  EmployeeProfileReviewQuery,
  EmployeeProfileReviewStatus,
  EmployeeProfileReviewSummary,
  EmployeeProfileSensitiveField,
  EmployeeProfileSensitiveSnapshot,
} from "./types";
import { EMPLOYEE_PROFILE_SENSITIVE_FIELDS } from "./types";
import { normalizeBirthday } from "@/modules/employee-profile/birthday";

type ApiRecord = Record<string, unknown>;
type ReviewHttpClient = {
  get: (path: string, config?: { params?: Record<string, unknown> }) => Promise<{ data: unknown }>;
  post: (path: string, payload?: unknown) => Promise<{ data: unknown }>;
};

const REVIEW_BASE_PATH = "/EmployeeProfiles/review/change-requests";
const REVIEW_STATUSES = new Set<EmployeeProfileReviewStatus>([
  "Pending",
  "Approved",
  "Rejected",
  "Superseded",
  "Withdrawn",
]);
const SENSITIVE_FIELD_SET = new Set<string>(EMPLOYEE_PROFILE_SENSITIVE_FIELDS);

function asRecord(value: unknown): ApiRecord {
  return value && typeof value === "object" && !Array.isArray(value)
    ? value as ApiRecord
    : {};
}

function read(source: ApiRecord, camel: string, pascal: string) {
  return source[camel] ?? source[pascal];
}

function asString(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

function asOptionalString(value: unknown) {
  return asString(value) || undefined;
}

function asNonNegativeInteger(value: unknown) {
  const number = Number(value);
  return Number.isInteger(number) && number >= 0 ? number : 0;
}

function asPositiveInteger(value: unknown) {
  const number = Number(value);
  return Number.isInteger(number) && number > 0 ? number : 0;
}

function asStringArray(value: unknown) {
  return Array.isArray(value) ? value.map(asString).filter(Boolean) : [];
}

function asStatus(value: unknown): EmployeeProfileReviewStatus | null {
  const normalized = asString(value) as EmployeeProfileReviewStatus;
  return REVIEW_STATUSES.has(normalized) ? normalized : null;
}

function asSensitiveFields(value: unknown): EmployeeProfileSensitiveField[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return Array.from(
    new Set(value.map(asString).filter((field) => SENSITIVE_FIELD_SET.has(field)))
  ) as EmployeeProfileSensitiveField[];
}

function normalizeSummary(payload: unknown): EmployeeProfileReviewSummary | null {
  const data = asRecord(payload);
  const requestId = asPositiveInteger(read(data, "requestId", "RequestId"));
  const userGuid = asString(read(data, "userGuid", "UserGuid"));
  const status = asStatus(read(data, "status", "Status"));
  const submittedAt = asString(read(data, "submittedAt", "SubmittedAt"));
  if (!requestId || !userGuid || !status || !submittedAt) {
    return null;
  }

  // 关键逻辑：这里逐字段白名单构造摘要，不展开服务端对象，防止意外携带完整敏感值。
  return {
    requestId,
    userGuid,
    username: asString(read(data, "username", "Username")),
    status,
    baseSensitiveRevision: asNonNegativeInteger(
      read(data, "baseSensitiveRevision", "BaseSensitiveRevision")
    ),
    submittedAt,
    reviewedAt: asOptionalString(read(data, "reviewedAt", "ReviewedAt")),
    changedFields: asSensitiveFields(read(data, "changedFields", "ChangedFields")),
    storeCodes: asStringArray(read(data, "storeCodes", "StoreCodes")),
    storeNames: asStringArray(read(data, "storeNames", "StoreNames")),
  };
}

function normalizeSnapshot(payload: unknown): EmployeeProfileSensitiveSnapshot {
  const data = asRecord(payload);
  return {
    birthday: normalizeBirthday(asString(read(data, "birthday", "Birthday"))),
    bankBsb: asString(read(data, "bankBsb", "BankBsb")),
    bankAccountNumber: asString(read(data, "bankAccountNumber", "BankAccountNumber")),
    superannuationCompanyName: asString(
      read(data, "superannuationCompanyName", "SuperannuationCompanyName")
    ),
    superannuationCompanyCode: asString(
      read(data, "superannuationCompanyCode", "SuperannuationCompanyCode")
    ),
    superannuationAccountNumber: asString(
      read(data, "superannuationAccountNumber", "SuperannuationAccountNumber")
    ),
    identityType: asString(read(data, "identityType", "IdentityType")),
    identityId: asString(read(data, "identityId", "IdentityId")),
    hasIdentityPhoto: Boolean(read(data, "hasIdentityPhoto", "HasIdentityPhoto")),
    identityPhotoUrl: asString(read(data, "identityPhotoUrl", "IdentityPhotoUrl")),
  };
}

export function normalizeEmployeeProfileReviewList(payload: unknown): EmployeeProfileReviewPage {
  const data = asRecord(payload);
  const itemsValue = read(data, "items", "Items");
  const items = Array.isArray(itemsValue)
    ? itemsValue.map(normalizeSummary).filter((item): item is EmployeeProfileReviewSummary => item !== null)
    : [];
  return {
    items,
    total: asNonNegativeInteger(read(data, "total", "Total")),
    page: asPositiveInteger(read(data, "page", "Page")) || 1,
    pageSize: asPositiveInteger(read(data, "pageSize", "PageSize")) || 20,
  };
}

export function normalizeEmployeeProfileReviewDetail(payload: unknown): EmployeeProfileReviewDetail {
  const data = asRecord(payload);
  const summary = normalizeSummary(data);
  if (!summary) {
    throw new Error("Employee profile review detail is invalid");
  }
  return {
    ...summary,
    ...normalizeSnapshot(data),
    identityPhotoUrlExpiresAt: asOptionalString(
      read(data, "identityPhotoUrlExpiresAt", "IdentityPhotoUrlExpiresAt")
    ),
    submittedBy: asOptionalString(read(data, "submittedBy", "SubmittedBy")),
    reviewedBy: asOptionalString(read(data, "reviewedBy", "ReviewedBy")),
    reviewReason: asOptionalString(read(data, "reviewReason", "ReviewReason")),
    currentSnapshot: normalizeSnapshot(read(data, "currentSnapshot", "CurrentSnapshot")),
  };
}

export function normalizeEmployeeProfileReviewMutationResult(
  payload: unknown
): EmployeeProfileReviewMutationResult {
  const data = asRecord(payload);
  const requestId = asPositiveInteger(read(data, "requestId", "RequestId"));
  const status = asStatus(read(data, "status", "Status"));
  if (!requestId || !status) {
    throw new Error("Employee profile review mutation response is invalid");
  }
  // 不复用详情 normalizer；显式白名单阻止账号、证件号和照片 URL 进入 MutationCache。
  return { requestId, status };
}

export function createEmployeeProfileReviewApi(client: ReviewHttpClient) {
  return {
    async getRequests(query: EmployeeProfileReviewQuery = {}) {
      const response = await client.get(REVIEW_BASE_PATH, {
        params: {
          page: query.page ?? 1,
          pageSize: query.pageSize ?? 20,
          status: query.status,
          search: query.search?.trim() || undefined,
          userGuid: query.userGuid?.trim() || undefined,
        },
      });
      return normalizeEmployeeProfileReviewList(response.data);
    },
    async getDetail(requestId: number) {
      const response = await client.get(`${REVIEW_BASE_PATH}/${requestId}`);
      return normalizeEmployeeProfileReviewDetail(response.data);
    },
    async approve(requestId: number, reason?: string) {
      const response = await client.post(`${REVIEW_BASE_PATH}/${requestId}/approve`, {
        reason: reason?.trim() || undefined,
      });
      return normalizeEmployeeProfileReviewMutationResult(response.data);
    },
    async reject(requestId: number, reason: string) {
      const normalizedReason = reason.trim();
      if (!normalizedReason) {
        throw new Error("Rejection reason is required");
      }
      const response = await client.post(`${REVIEW_BASE_PATH}/${requestId}/reject`, {
        reason: normalizedReason,
      });
      return normalizeEmployeeProfileReviewMutationResult(response.data);
    },
  };
}

async function getApi() {
  const { apiClient } = await import("@/shared/api/client");
  return createEmployeeProfileReviewApi(apiClient);
}

export async function getEmployeeProfileReviewRequestsApi(query: EmployeeProfileReviewQuery) {
  return (await getApi()).getRequests(query);
}

export async function getEmployeeProfileReviewDetailApi(requestId: number) {
  return (await getApi()).getDetail(requestId);
}

export async function approveEmployeeProfileReviewApi(requestId: number, reason?: string) {
  return (await getApi()).approve(requestId, reason);
}

export async function rejectEmployeeProfileReviewApi(requestId: number, reason: string) {
  return (await getApi()).reject(requestId, reason);
}
