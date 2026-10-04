import type {
  EmployeeProfile,
  EmployeeProfileSensitiveChangeRequest,
  SensitiveEmployeeProfilePayload,
  UpdateEmployeeProfilePayload,
} from "./types";
import { normalizeBirthday } from "./birthday";

type ApiRecord = Record<string, unknown>;
type EmployeeProfileHttpClient = {
  get: (path: string) => Promise<{ data: unknown }>;
  put: (path: string, payload: unknown) => Promise<{ data: unknown }>;
};

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

export function normalizeSensitiveChangeRequest(
  payload: unknown
): EmployeeProfileSensitiveChangeRequest | null {
  if (payload == null) {
    return null;
  }
  const data = asRecord(payload);
  const rawStatus = asString(data.status ?? data.Status);
  // 后端没有申请时返回 Ok(null)，ASP.NET Core 会转成 204 空响应体，axios 的 data 是空串而非 null；
  // 没有状态就不是一条有效申请，按「没有申请」处理，否则界面会拼出 status. 这种原样翻译键。
  if (!rawStatus) {
    return null;
  }
  const status = (
    rawStatus.charAt(0).toUpperCase() + rawStatus.slice(1).toLowerCase()
  ) as EmployeeProfileSensitiveChangeRequest["status"];
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
  };
}
