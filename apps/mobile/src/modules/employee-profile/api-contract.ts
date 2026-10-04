import type {
  EmployeeProfile,
  EmployeeProfileSensitiveChangeHistoryItem,
  EmployeeProfileSensitiveChangeRequest,
  SensitiveEmployeeProfilePayload,
  UpdateEmployeeProfilePayload,
} from "./types";
import { normalizeBirthday } from "./birthday";

type ApiRecord = Record<string, unknown>;
type EmployeeProfileHttpClient = {
  get: (path: string) => Promise<{ data: unknown }>;
  put: (path: string, payload: unknown) => Promise<{ data: unknown }>;
  post: (path: string, payload?: unknown) => Promise<{ data: unknown }>;
};

const SENSITIVE_CHANGE_STATUSES = new Set<EmployeeProfileSensitiveChangeRequest["status"]>([
  "Pending",
  "Approved",
  "Rejected",
  "Superseded",
  "Withdrawn",
]);

function asRecord(payload: unknown): ApiRecord {
  return payload && typeof payload === "object" ? payload as ApiRecord : {};
}

function asString(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}

function asStringArray(value: unknown) {
  return Array.isArray(value) ? value.map(asString).filter(Boolean) : [];
}

export function normalizeEmployeeProfile(payload: unknown): EmployeeProfile {
  const data = asRecord(payload);
  return {
    username: asString(data.username ?? data.userName ?? data.UserName),
    displayName: asString(data.displayName ?? data.DisplayName ?? data.fullName ?? data.FullName) || undefined,
    phone: asString(data.phone ?? data.Phone),
    email: asString(data.email ?? data.Email),
    bankBsb: asString(data.bankBsb ?? data.BankBsb),
    bankAccountNumber: asString(data.bankAccountNumber ?? data.BankAccountNumber),
    superannuationCompanyName: asString(data.superannuationCompanyName ?? data.SuperannuationCompanyName),
    superannuationCompanyCode: asString(data.superannuationCompanyCode ?? data.SuperannuationCompanyCode),
    superannuationAccountNumber: asString(data.superannuationAccountNumber ?? data.SuperannuationAccountNumber),
    birthday: asString(data.birthday ?? data.Birthday),
    gender: asString(data.gender ?? data.Gender),
    employmentType: asString(data.employmentType ?? data.EmploymentType),
    avatarUrl: asString(data.avatarUrl ?? data.AvatarUrl),
    identityType: asString(data.identityType ?? data.IdentityType),
    identityId: asString(data.identityId ?? data.IdentityId),
    identityPhotoUrl: asString(data.identityPhotoUrl ?? data.IdentityPhotoUrl),
    identityPhotoUrlExpiresAt: asString(data.identityPhotoUrlExpiresAt ?? data.IdentityPhotoUrlExpiresAt) || undefined,
    address: asString(data.address ?? data.Address),
    createdAt: asString(data.createdAt ?? data.CreatedAt) || undefined,
    updatedAt: asString(data.updatedAt ?? data.UpdatedAt) || undefined,
    sensitiveRevision: Number(data.sensitiveRevision ?? data.SensitiveRevision) || 0,
  };
}

function normalizeSensitiveStatus(value: unknown) {
  const rawStatus = asString(value);
  if (!rawStatus) return null;
  const status = (
    rawStatus.charAt(0).toUpperCase() + rawStatus.slice(1).toLowerCase()
  ) as EmployeeProfileSensitiveChangeRequest["status"];
  // 未知状态按「已失效」展示：不可撤回、不可当作待审恢复草稿。
  return SENSITIVE_CHANGE_STATUSES.has(status) ? status : "Superseded";
}

/** 历史条目逐字段白名单构造，即使服务端意外多返回字段也不会把敏感值带进缓存。 */
export function normalizeSensitiveChangeHistory(
  payload: unknown
): EmployeeProfileSensitiveChangeHistoryItem[] {
  if (!Array.isArray(payload)) return [];
  return payload.flatMap((item) => {
    const data = asRecord(item);
    const requestId = Number(data.requestId ?? data.RequestId) || 0;
    const status = normalizeSensitiveStatus(data.status ?? data.Status);
    const submittedAt = asString(data.submittedAt ?? data.SubmittedAt);
    if (!requestId || !status || !submittedAt) return [];
    return [{
      requestId,
      status,
      changedFields: asStringArray(data.changedFields ?? data.ChangedFields),
      submittedAt,
      reviewedAt: asString(data.reviewedAt ?? data.ReviewedAt) || undefined,
      reviewReason: asString(data.reviewReason ?? data.ReviewReason) || undefined,
    }];
  });
}

export function normalizeSensitiveChangeRequest(
  payload: unknown
): EmployeeProfileSensitiveChangeRequest | null {
  if (payload == null) {
    return null;
  }
  const data = asRecord(payload);
  // 后端没有申请时返回 Ok(null)，ASP.NET Core 会转成 204 空响应体，axios 的 data 是空串而非 null；
  // 没有状态就不是一条有效申请，按「没有申请」处理，否则界面会拼出 status. 这种原样翻译键。
  const status = normalizeSensitiveStatus(data.status ?? data.Status);
  if (!status) {
    return null;
  }
  return {
    requestId: Number(data.requestId ?? data.RequestId) || 0,
    status,
    birthday: normalizeBirthday(asString(data.birthday ?? data.Birthday)),
    bankBsb: asString(data.bankBsb ?? data.BankBsb),
    bankAccountNumber: asString(data.bankAccountNumber ?? data.BankAccountNumber),
    superannuationCompanyName: asString(data.superannuationCompanyName ?? data.SuperannuationCompanyName),
    superannuationCompanyCode: asString(data.superannuationCompanyCode ?? data.SuperannuationCompanyCode),
    superannuationAccountNumber: asString(data.superannuationAccountNumber ?? data.SuperannuationAccountNumber),
    identityType: asString(data.identityType ?? data.IdentityType),
    identityId: asString(data.identityId ?? data.IdentityId),
    hasIdentityPhoto: Boolean(data.hasIdentityPhoto ?? data.HasIdentityPhoto),
    identityPhotoUrl: asString(data.identityPhotoUrl ?? data.IdentityPhotoUrl),
    identityPhotoUrlExpiresAt: asString(data.identityPhotoUrlExpiresAt ?? data.IdentityPhotoUrlExpiresAt) || undefined,
    baseSensitiveRevision: Number(data.baseSensitiveRevision ?? data.BaseSensitiveRevision) || 0,
    submittedAt: asString(data.submittedAt ?? data.SubmittedAt),
    submittedBy: asString(data.submittedBy ?? data.SubmittedBy) || undefined,
    reviewedAt: asString(data.reviewedAt ?? data.ReviewedAt) || undefined,
    reviewedBy: asString(data.reviewedBy ?? data.ReviewedBy) || undefined,
    reviewReason: asString(data.reviewReason ?? data.ReviewReason) || undefined,
    changedFields: asStringArray(data.changedFields ?? data.ChangedFields),
  };
}

export function createEmployeeProfileApi(client: EmployeeProfileHttpClient) {
  return {
    async getMyEmployeeProfile() {
      const response = await client.get("/EmployeeProfiles/me");
      return normalizeEmployeeProfile(response.data);
    },
    async updateMyEmployeeProfile(payload: UpdateEmployeeProfilePayload) {
      const response = await client.put("/EmployeeProfiles/me", payload);
      return normalizeEmployeeProfile(response.data);
    },
    async getMySensitiveChangeRequest() {
      const response = await client.get("/EmployeeProfiles/me/sensitive-change-request");
      return normalizeSensitiveChangeRequest(response.data);
    },
    async upsertMySensitiveChangeRequest(payload: SensitiveEmployeeProfilePayload) {
      // 后端生日是 DateTime?，空串无法反序列化，不填时显式提交 null。
      const response = await client.put("/EmployeeProfiles/me/sensitive-change-request", {
        ...payload,
        birthday: payload.birthday.trim() || null,
      });
      const normalized = normalizeSensitiveChangeRequest(response.data);
      if (!normalized) {
        throw new Error("Sensitive change request response is empty");
      }
      return normalized;
    },
    async withdrawMySensitiveChangeRequest(requestId?: number) {
      // 带上页面看到的申请编号，后端发现已被审核或覆盖时返回 409，避免误撤另一台设备的新申请。
      const response = await client.post("/EmployeeProfiles/me/sensitive-change-request/withdraw", {
        requestId: requestId && requestId > 0 ? requestId : undefined,
      });
      const normalized = normalizeSensitiveChangeRequest(response.data);
      if (!normalized) {
        throw new Error("Sensitive change withdraw response is empty");
      }
      return normalized;
    },
    async getMySensitiveChangeHistory(take = 20) {
      const limit = Math.min(Math.max(Math.trunc(take) || 20, 1), 50);
      const response = await client.get(`/EmployeeProfiles/me/sensitive-change-requests?take=${limit}`);
      return normalizeSensitiveChangeHistory(response.data);
    },
  };
}
