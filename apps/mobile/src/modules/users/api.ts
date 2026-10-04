import { isAxiosError } from "axios";
import { apiClient } from "@/shared/api/client";
import type {
  PasswordSetupEmailResult,
  StoreUserCreatePayload,
  StoreUserDetail,
  StoreUserGridParams,
  StoreUserListItem,
  StoreUserPasswordPayload,
  StoreUserProfile,
  StoreUserStatusPayload,
  StoreUserUpdatePayload,
  StaffCashierBarcodeResponse,
} from "@/modules/users/types";
import {
  normalizePasswordSetupEmailResult,
  normalizeStoreUserDetail,
  normalizeStoreUserList,
  normalizeStoreUserProfile,
} from "@/modules/users/profile-normalization";
import { STORE_STAFF_ROLE } from "@/modules/users/types";

function sanitizeMutationPayload<T extends { storeCode: string; roleNames?: string[] }>(payload: T): T {
  return {
    ...payload,
    roleNames: [STORE_STAFF_ROLE],
    storeCode: payload.storeCode.trim(),
  };
}

export interface SafeStoreUserErrorLogMetadata {
  name: string;
  code?: string;
  status?: number;
}

export function toSafeStoreUserErrorLog(error: unknown): SafeStoreUserErrorLogMetadata {
  if (!isAxiosError(error)) {
    return { name: error instanceof Error ? error.name : "UnknownError" };
  }

  return {
    name: "AxiosError",
    ...(typeof error.code === "string" ? { code: error.code } : {}),
    ...(typeof error.response?.status === "number" ? { status: error.response.status } : {}),
  };
}

export async function fetchStoreUsers(params: StoreUserGridParams): Promise<StoreUserListItem[]> {
  const response = await apiClient.post("/react/v1/store-users/grid", {
    storeCode: params.storeCode?.trim() || undefined,
    keyword: params.keyword?.trim() || undefined,
  });

  return normalizeStoreUserList(response.data);
}

export async function fetchStoreUserDetail(userGuid: string, storeCode: string): Promise<StoreUserDetail> {
  const response = await apiClient.get("/react/v1/store-users/" + encodeURIComponent(userGuid), {
    params: { storeCode },
  });

  return normalizeStoreUserDetail(response.data);
}

export async function fetchStoreUserProfile(
  userGuid: string,
  storeCode: string
): Promise<StoreUserProfile> {
  const response = await apiClient.get(
    "/react/v1/store-users/" + encodeURIComponent(userGuid) + "/profile",
    {
      params: { storeCode },
    }
  );

  return normalizeStoreUserProfile(response.data);
}

export async function updateStoreUser(payload: StoreUserUpdatePayload): Promise<StoreUserDetail> {
  const response = await apiClient.put(
    "/react/v1/store-users/" + encodeURIComponent(payload.userGuid),
    sanitizeMutationPayload(payload)
  );

  return normalizeStoreUserDetail(response.data);
}

export async function createStoreUser(payload: StoreUserCreatePayload): Promise<StoreUserDetail> {
  // 创建入口只允许生成固定的店员账号，避免调用方意外提升角色或改变员工类型。
  const response = await apiClient.post("/react/v1/store-users", {
    ...sanitizeMutationPayload(payload),
    employmentType: "casual",
    passwordFormat: "raw",
  });

  return normalizeStoreUserDetail(response.data);
}

/** 给店员邮箱发设置密码验证码（新建后重发，或代替手动重置密码）。 */
export async function sendStoreUserPasswordSetupEmail(payload: {
  userGuid: string;
  storeCode: string;
}): Promise<PasswordSetupEmailResult> {
  const response = await apiClient.post(
    "/react/v1/store-users/" + encodeURIComponent(payload.userGuid) + "/password-setup-email",
    { storeCode: payload.storeCode }
  );
  const result = normalizePasswordSetupEmailResult(response.data);
  if (!result) {
    throw new Error("Password setup email response is empty");
  }
  return result;
}

export async function updateStoreUserStatus(payload: StoreUserStatusPayload): Promise<void> {
  await apiClient.put("/react/v1/store-users/" + encodeURIComponent(payload.userGuid) + "/status", {
    storeCode: payload.storeCode,
    status: payload.status,
  });
}

export async function resetStoreUserPassword(payload: StoreUserPasswordPayload): Promise<void> {
  await apiClient.put(
    "/react/v1/store-users/" + encodeURIComponent(payload.userGuid) + "/password",
    {
      storeCode: payload.storeCode,
      newPassword: payload.newPassword,
      passwordFormat: payload.passwordFormat,
    }
  );
}

type ApiRecord = Record<string, unknown>;

function normalizeStaffCashierBarcode(payload: unknown): StaffCashierBarcodeResponse {
  const envelope = payload && typeof payload === "object" ? payload as ApiRecord : {};
  const value = envelope.data && typeof envelope.data === "object" ? envelope.data as ApiRecord : envelope;
  const text = (candidate: unknown) => typeof candidate === "string" ? candidate.trim() : "";
  return {
    exists: Boolean(value.exists ?? value.Exists),
    barcode: text(value.barcode ?? value.Barcode),
    format: text(value.format ?? value.Format) || "EAN13",
    printCount: Number(value.printCount ?? value.PrintCount) || 0,
    createdAt: text(value.createdAt ?? value.CreatedAt) || undefined,
    updatedAt: text(value.updatedAt ?? value.UpdatedAt) || undefined,
  };
}

function staffCashierBarcodePath(userGuid: string, suffix = "") {
  return `/react/v1/store-users/${encodeURIComponent(userGuid)}/cashier-barcode${suffix}`;
}

export async function getStaffCashierBarcodeApi(userGuid: string, storeCode: string) {
  const response = await apiClient.get(staffCashierBarcodePath(userGuid), { params: { storeCode } });
  return normalizeStaffCashierBarcode(response.data);
}

export async function ensureStaffCashierBarcodeApi(userGuid: string, storeCode: string) {
  const response = await apiClient.post(staffCashierBarcodePath(userGuid, "/ensure"), { storeCode });
  return normalizeStaffCashierBarcode(response.data);
}

export async function confirmStaffCashierBarcodePrintApi(
  userGuid: string,
  storeCode: string,
  barcode: string,
  printAttemptId: string
) {
  const response = await apiClient.post(staffCashierBarcodePath(userGuid, "/print-confirmation"), {
    storeCode,
    barcode: barcode.trim(),
    printAttemptId: printAttemptId.trim(),
  });
  return normalizeStaffCashierBarcode(response.data);
}
