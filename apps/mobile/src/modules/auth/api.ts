import { apiClient } from "@/shared/api/client";
import type {
  LoginRequest,
  TokenResponse,
  CurrentUser,
  UserStoreDto,
} from "@/modules/auth/types";

function normalizeUserStores(payload: unknown): UserStoreDto[] {
  if (!Array.isArray(payload)) {
    return [];
  }

  return payload
    .filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === "object")
    .map((item) => {
      const storeCode =
        (typeof item.storeCode === "string" && item.storeCode) ||
        (typeof item.StoreCode === "string" && item.StoreCode) ||
        "";

      return {
        storeGUID:
          (typeof item.storeGUID === "string" && item.storeGUID) ||
          (typeof item.storeGuid === "string" && item.storeGuid) ||
          (typeof item.StoreGUID === "string" && item.StoreGUID) ||
          (typeof item.StoreGuid === "string" && item.StoreGuid) ||
          undefined,
        storeCode,
        isActive:
          typeof item.isActive === "boolean"
            ? item.isActive
            : typeof item.IsActive === "boolean"
              ? item.IsActive
              : undefined,
        storeName:
          (typeof item.storeName === "string" && item.storeName) ||
          (typeof item.StoreName === "string" && item.StoreName) ||
          storeCode,
        postcode:
          (typeof item.postcode === "string" && item.postcode) ||
          (typeof item.postCode === "string" && item.postCode) ||
          (typeof item.Postcode === "string" && item.Postcode) ||
          (typeof item.PostCode === "string" && item.PostCode) ||
          undefined,
        stateCode:
          (typeof item.stateCode === "string" && item.stateCode) ||
          (typeof item.StateCode === "string" && item.StateCode) ||
          (typeof item.state === "string" && item.state) ||
          (typeof item.State === "string" && item.State) ||
          undefined,
        isPrimary:
          typeof item.isPrimary === "boolean"
            ? item.isPrimary
            : typeof item.IsPrimary === "boolean"
              ? item.IsPrimary
              : undefined,
        assignedAt:
          (typeof item.assignedAt === "string" && item.assignedAt) ||
          (typeof item.AssignedAt === "string" && item.AssignedAt) ||
          undefined,
      };
    })
    .filter((item) => Boolean(item.storeCode));
}

function normalizeCurrentUser(payload: unknown): CurrentUser {
  const data = (payload && typeof payload === "object" ? payload : {}) as Record<string, unknown>;
  const userGuid =
    (typeof data.userGuid === "string" && data.userGuid) ||
    (typeof data.userGUID === "string" && data.userGUID) ||
    (typeof data.UserGuid === "string" && data.UserGuid) ||
    (typeof data.UserGUID === "string" && data.UserGUID) ||
    "";

  return {
    userGuid,
    userGUID: userGuid,
    username:
      (typeof data.username === "string" && data.username) ||
      (typeof data.userName === "string" && data.userName) ||
      (typeof data.UserName === "string" && data.UserName) ||
      "",
    email:
      (typeof data.email === "string" && data.email) ||
      (typeof data.Email === "string" && data.Email) ||
      "",
    fullName:
      (typeof data.fullName === "string" && data.fullName) ||
      (typeof data.FullName === "string" && data.FullName) ||
      undefined,
    phone:
      (typeof data.phone === "string" && data.phone) ||
      (typeof data.Phone === "string" && data.Phone) ||
      undefined,
    permissions: Array.isArray(data.permissions)
      ? (data.permissions as string[])
      : Array.isArray(data.Permissions)
        ? (data.Permissions as string[])
        : [],
    roleNames: Array.isArray(data.roleNames)
      ? (data.roleNames as string[])
      : Array.isArray(data.RoleNames)
        ? (data.RoleNames as string[])
        : [],
    storeNames: Array.isArray(data.storeNames)
      ? (data.storeNames as string[])
      : Array.isArray(data.StoreNames)
        ? (data.StoreNames as string[])
        : [],
    stores: normalizeUserStores(data.stores ?? data.Stores),
    mustChangePassword: data.mustChangePassword === true || data.MustChangePassword === true,
  };
}

export async function loginApi(payload: LoginRequest): Promise<TokenResponse> {
  const res = await apiClient.post("/auth/login", payload);
  return res.data as TokenResponse;
}

export async function refreshTokenApi(
  refreshToken: string
): Promise<TokenResponse> {
  const res = await apiClient.post("/auth/refresh", { refreshToken });
  return res.data as TokenResponse;
}

export async function getCurrentUserApi(): Promise<CurrentUser> {
  const res = await apiClient.get("/auth/current");
  return normalizeCurrentUser(res.data);
}

export async function getUserStoresApi(userGuid: string): Promise<UserStoreDto[]> {
  const res = await apiClient.get(`/Users/guid/${encodeURIComponent(userGuid)}/stores`);
  return normalizeUserStores(res.data);
}

/** 改密成功后后端清除须改密标记，并吊销本账号其他会话，当前会话保留。 */
export async function changePasswordApi(payload: {
  currentPassword: string;
  newPassword: string;
  confirmPassword: string;
}): Promise<void> {
  await apiClient.post("/auth/change-password", payload);
}

export async function logoutApi(refreshToken: string): Promise<void> {
  await apiClient.post("/auth/logout", { refreshToken });
}
