import { STORE_STAFF_ROLE } from "@/modules/users/types";
import type { PasswordSetupEmailResult, StoreUserDetail, StoreUserListItem, StoreUserProfile } from "@/modules/users/types";

type ApiRecord = Record<string, unknown>;

function asString(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }

  const normalized = value.trim();
  return normalized ? normalized : undefined;
}

function asNumber(value: unknown, fallback = 0): number {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
  }

  return fallback;
}

function asStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((item) => (typeof item === "string" ? item.trim() : ""))
    .filter(Boolean);
}

export function normalizeStoreUser(raw: ApiRecord): StoreUserListItem {
  return {
    userGUID: String(raw.userGUID ?? raw.userGuid ?? raw.UserGUID ?? raw.UserGuid ?? ""),
    username: String(raw.username ?? raw.userName ?? raw.UserName ?? raw.Username ?? ""),
    fullName: asString(raw.fullName ?? raw.FullName ?? raw.name ?? raw.Name),
    email: asString(raw.email ?? raw.Email),
    phone: asString(raw.phone ?? raw.Phone ?? raw.mobile ?? raw.Mobile),
    status: asNumber(raw.status ?? raw.Status, 1),
    storeCode: asString(raw.storeCode ?? raw.StoreCode),
    storeName: asString(raw.storeName ?? raw.StoreName),
    roleNames: asStringArray(raw.roleNames ?? raw.RoleNames).length
      ? asStringArray(raw.roleNames ?? raw.RoleNames)
      : [STORE_STAFF_ROLE],
    birthday: asString(raw.birthday ?? raw.Birthday),
    gender: asString(raw.gender ?? raw.Gender),
    employmentType: asString(raw.employmentType ?? raw.EmploymentType),
    lastLoginTime: asString(raw.lastLoginTime ?? raw.LastLoginTime),
    lastLoginIp: asString(raw.lastLoginIp ?? raw.LastLoginIp),
    createdAt: asString(raw.createdAt ?? raw.CreatedAt),
    updatedAt: asString(raw.updatedAt ?? raw.UpdatedAt),
    mustChangePassword: raw.mustChangePassword === true || raw.MustChangePassword === true,
  };
}

export function normalizePasswordSetupEmailResult(payload: unknown): PasswordSetupEmailResult | undefined {
  if (!payload || typeof payload !== "object") return undefined;
  const raw = payload as ApiRecord;
  const maskedEmail = asString(raw.maskedEmail ?? raw.MaskedEmail);
  if (!maskedEmail) return undefined;
  return { maskedEmail, expiresAtUtc: asString(raw.expiresAtUtc ?? raw.ExpiresAtUtc) };
}

export function normalizeStoreUserDetail(payload: unknown): StoreUserDetail {
  if (!payload || typeof payload !== "object") {
    throw new Error("Invalid user detail payload");
  }

  const raw = payload as ApiRecord;
  return {
    ...normalizeStoreUser(raw),
    passwordSetupEmail: normalizePasswordSetupEmailResult(raw.passwordSetupEmail ?? raw.PasswordSetupEmail),
    passwordSetupEmailError: asString(raw.passwordSetupEmailError ?? raw.PasswordSetupEmailError),
  };
}

export function normalizeStoreUserProfile(payload: unknown): StoreUserProfile {
  if (!payload || typeof payload !== "object") {
    throw new Error("Invalid user profile payload");
  }

  const data = payload as ApiRecord;
  const normalized = normalizeStoreUser(data);

  return {
    ...normalized,
    identityId: asString(data.identityId ?? data.IdentityId ?? data.employeeId ?? data.EmployeeId),
    avatarUrl: asString(data.avatarUrl ?? data.AvatarUrl),
    address: asString(data.address ?? data.Address),
    bankBsb: asString(data.bankBsb ?? data.BankBsb),
    bankAccountNumber: asString(data.bankAccountNumber ?? data.BankAccountNumber),
    superannuationCompanyName: asString(data.superannuationCompanyName ?? data.SuperannuationCompanyName),
    superannuationCompanyCode: asString(data.superannuationCompanyCode ?? data.SuperannuationCompanyCode),
    superannuationAccountNumber: asString(
      data.superannuationAccountNumber ?? data.SuperannuationAccountNumber
    ),
    remarks: asString(data.remarks ?? data.Remarks),
  };
}

export function normalizeStoreUserList(payload: unknown): StoreUserListItem[] {
  if (Array.isArray(payload)) {
    return payload
      .filter((item): item is ApiRecord => Boolean(item) && typeof item === "object")
      .map(normalizeStoreUser);
  }

  if (payload && typeof payload === "object") {
    const record = payload as ApiRecord;
    const items = record.items ?? record.Items ?? record.rows ?? record.Rows ?? record.list ?? record.List;
    if (Array.isArray(items)) {
      return items
        .filter((item): item is ApiRecord => Boolean(item) && typeof item === "object")
        .map(normalizeStoreUser);
    }
  }

  return [];
}
