import { isAxiosError } from "axios";
import { apiClient } from "@/shared/api/client";
import { accountBoundRequestConfig } from "@/modules/auth/account-bound-request";
import {
  assignUserAccessRoles as assignExistingUserAccessRoles,
  assignUserAccessStores as assignExistingUserAccessStores,
  assignUserDirectPermissions as assignExistingUserDirectPermissions,
  fetchAccessRoleCatalog as fetchExistingAccessRoleCatalog,
  fetchBelowActorRoleCatalog as fetchExistingBelowActorRoleCatalog,
  fetchAccessStoreCatalog as fetchExistingAccessStoreCatalog,
  fetchUserAccessPermissionAccess as fetchExistingUserAccessPermissionAccess,
  fetchUserAccessRoles as fetchExistingUserAccessRoles,
  fetchUserAccessStores as fetchExistingUserAccessStores,
} from "@/modules/users/access-management-api";
import type { AssignUserAccessRolesInput, AssignUserAccessStoresInput, AssignUserDirectPermissionsInput } from "@/modules/users/access-management-types";
import { normalizeShopStoresApiResponse } from "@/modules/shop/store-normalization";
import type {
  IdentityAdminErrorMeta,
  IdentityCreateRoleInput,
  IdentityCreateSysPermissionInput,
  IdentityCreateUserInput,
  IdentityPagedResult,
  IdentityPermission,
  IdentityPermissionAlias,
  IdentityPermissionCatalog,
  IdentityPermissionCategory,
  IdentityPermissionRoleCounts,
  IdentityPermissionUserAssignmentInput,
  IdentityRole,
  IdentityRoleDetail,
  IdentityRolePermissionState,
  IdentityRolePermissionTemplate,
  IdentityRoleQuery,
  IdentityRoleUser,
  IdentitySysPermission,
  IdentityUpdateRoleInput,
  IdentityUpdateUserInput,
  IdentityUpdateUserPasswordInput,
  IdentityUser,
  IdentityUserDetail,
  IdentityUserLoginRecord,
  IdentityUserLoginRecordQuery,
  IdentityUserQuery,
  IdentityUserStore,
} from "./types";

const INVALID_RESPONSE_MESSAGE = "IDENTITY_ADMIN_RESPONSE_INVALID";

type ApiRecord = Record<string, unknown>;

function isRecord(value: unknown): value is ApiRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function pick(source: ApiRecord, ...keys: string[]) {
  for (const key of keys) {
    if (Object.prototype.hasOwnProperty.call(source, key)) return source[key];
  }
  return undefined;
}

function unwrap(value: unknown): unknown {
  if (!isRecord(value)) return value;
  const success = pick(value, "success", "Success", "isSuccess", "IsSuccess");
  if (success === false) {
    const message = pick(value, "message", "Message");
    const code = pick(value, "errorCode", "ErrorCode", "code", "Code");
    const error = new Error(typeof message === "string" && message.trim() ? message : "Request failed");
    // 收到完整业务响应说明服务端已明确拒绝请求；保留此标记，避免把用户名冲突等确定失败误判成传输不确定。
    Object.assign(error, { apiBusinessError: true });
    if (typeof code === "string" && code.trim()) Object.assign(error, { code: code.trim() });
    throw error;
  }
  if (success === true) {
    const data = pick(value, "data", "Data");
    if (data === undefined) throw new Error(INVALID_RESPONSE_MESSAGE);
    return data;
  }
  return value;
}

function requiredRecord(value: unknown): ApiRecord {
  const normalized = unwrap(value);
  if (!isRecord(normalized)) throw new Error(INVALID_RESPONSE_MESSAGE);
  return normalized;
}

function stringValue(source: ApiRecord, ...keys: string[]) {
  const value = pick(source, ...keys);
  return typeof value === "string" ? value.trim() : "";
}

function requiredString(source: ApiRecord, ...keys: string[]) {
  const value = stringValue(source, ...keys);
  if (!value) throw new Error(INVALID_RESPONSE_MESSAGE);
  return value;
}

function optionalString(source: ApiRecord, ...keys: string[]) {
  return stringValue(source, ...keys) || undefined;
}

function booleanValue(source: ApiRecord, fallback: boolean, ...keys: string[]) {
  const value = pick(source, ...keys);
  return typeof value === "boolean" ? value : fallback;
}

function numberValue(source: ApiRecord, fallback: number, ...keys: string[]) {
  const value = pick(source, ...keys);
  const numeric = typeof value === "string" && value.trim() ? Number(value) : value;
  return typeof numeric === "number" && Number.isFinite(numeric) ? numeric : fallback;
}

function stringArray(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return Array.from(new Set(value.filter((item): item is string => typeof item === "string").map((item) => item.trim()).filter(Boolean)));
}

function optionalStringArray(value: unknown): string[] {
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value) || value.some((item) => typeof item !== "string")) {
    throw new Error(INVALID_RESPONSE_MESSAGE);
  }
  return stringArray(value);
}

function requiredRecordArray(value: unknown): ApiRecord[] {
  if (!Array.isArray(value) || value.some((item) => !isRecord(item))) {
    throw new Error(INVALID_RESPONSE_MESSAGE);
  }
  return value;
}

function optionalRecordArray(value: unknown): ApiRecord[] {
  if (value === undefined || value === null) return [];
  return requiredRecordArray(value);
}

function normalizeUserStore(value: unknown): IdentityUserStore {
  const source = requiredRecord(value);
  return {
    storeGUID: requiredString(source, "storeGUID", "StoreGUID", "storeGuid", "StoreGuid"),
    storeName: requiredString(source, "storeName", "StoreName"),
    storeCode: requiredString(source, "storeCode", "StoreCode"),
    ...(typeof pick(source, "isActive", "IsActive") === "boolean"
      ? { isActive: booleanValue(source, false, "isActive", "IsActive") }
      : {}),
    isPrimary: booleanValue(source, false, "isPrimary", "IsPrimary", "isManageable", "IsManageable"),
    ...(optionalString(source, "assignedAt", "AssignedAt")
      ? { assignedAt: optionalString(source, "assignedAt", "AssignedAt") }
      : {}),
  };
}

function normalizeRole(value: unknown): IdentityRole {
  const source = requiredRecord(value);
  return {
    roleGUID: requiredString(source, "roleGUID", "RoleGUID", "roleGuid", "RoleGuid"),
    roleName: requiredString(source, "roleName", "RoleName"),
    ...(optionalString(source, "description", "Description")
      ? { description: optionalString(source, "description", "Description") }
      : {}),
    isActive: booleanValue(source, false, "isActive", "IsActive"),
    createdAt: stringValue(source, "createdAt", "CreatedAt"),
    updatedAt: stringValue(source, "updatedAt", "UpdatedAt"),
    userCount: numberValue(source, 0, "userCount", "UserCount"),
  };
}

function normalizeRoleUser(value: unknown): IdentityRoleUser {
  const source = requiredRecord(value);
  return {
    userGUID: requiredString(source, "userGUID", "UserGUID", "userGuid", "UserGuid"),
    username: requiredString(source, "username", "Username"),
    email: requiredString(source, "email", "Email"),
    ...(optionalString(source, "fullName", "FullName")
      ? { fullName: optionalString(source, "fullName", "FullName") }
      : {}),
    ...(optionalString(source, "givenName", "GivenName") ? { givenName: optionalString(source, "givenName", "GivenName") } : {}),
    ...(optionalString(source, "familyName", "FamilyName") ? { familyName: optionalString(source, "familyName", "FamilyName") } : {}),
    ...(optionalString(source, "nickname", "Nickname") ? { nickname: optionalString(source, "nickname", "Nickname") } : {}),
    isActive: booleanValue(source, false, "isActive", "IsActive"),
    assignedAt: stringValue(source, "assignedAt", "AssignedAt"),
  };
}

function normalizeUser(value: unknown): IdentityUser {
  const source = requiredRecord(value);
  return {
    userGUID: requiredString(source, "userGUID", "UserGUID", "userGuid", "UserGuid"),
    username: requiredString(source, "username", "Username"),
    email: requiredString(source, "email", "Email"),
    ...(optionalString(source, "fullName", "FullName") ? { fullName: optionalString(source, "fullName", "FullName") } : {}),
    ...(optionalString(source, "givenName", "GivenName") ? { givenName: optionalString(source, "givenName", "GivenName") } : {}),
    ...(optionalString(source, "middleName", "MiddleName") ? { middleName: optionalString(source, "middleName", "MiddleName") } : {}),
    ...(optionalString(source, "familyName", "FamilyName") ? { familyName: optionalString(source, "familyName", "FamilyName") } : {}),
    ...(optionalString(source, "nickname", "Nickname") ? { nickname: optionalString(source, "nickname", "Nickname") } : {}),
    ...(optionalString(source, "phone", "Phone") ? { phone: optionalString(source, "phone", "Phone") } : {}),
    // 只认布尔值：服务端省略（null）或类型异常都保持 undefined，绝不把「未知」当成「允许」。
    ...(typeof pick(source, "loginEnabled", "LoginEnabled") === "boolean"
      ? { loginEnabled: booleanValue(source, true, "loginEnabled", "LoginEnabled") }
      : {}),
    ...(optionalString(source, "lastLoginAt", "LastLoginAt") ? { lastLoginAt: optionalString(source, "lastLoginAt", "LastLoginAt") } : {}),
    ...(optionalString(source, "lastLoginIp", "LastLoginIp") ? { lastLoginIp: optionalString(source, "lastLoginIp", "LastLoginIp") } : {}),
    isActive: booleanValue(source, false, "isActive", "IsActive"),
    createdAt: stringValue(source, "createdAt", "CreatedAt"),
    updatedAt: stringValue(source, "updatedAt", "UpdatedAt"),
    ...(optionalString(source, "currentStore", "CurrentStore") ? { currentStore: optionalString(source, "currentStore", "CurrentStore") } : {}),
    roleNames: optionalStringArray(pick(source, "roleNames", "RoleNames")),
    storeNames: optionalStringArray(pick(source, "storeNames", "StoreNames")),
    roles: optionalRecordArray(pick(source, "roles", "Roles")).map(normalizeRole),
    stores: optionalRecordArray(pick(source, "stores", "Stores")).map(normalizeUserStore),
    permissions: optionalStringArray(pick(source, "permissions", "Permissions")),
    exactPermissions: optionalStringArray(pick(source, "exactPermissions", "ExactPermissions")),
  };
}

function normalizePaged<T>(value: unknown, normalizeItem: (item: unknown) => T): IdentityPagedResult<T> {
  const source = requiredRecord(value);
  // 分页 items 缺失或类型错误属于契约异常，不能伪装成业务空页。
  const items = requiredRecordArray(pick(source, "items", "Items")).map(normalizeItem);
  const page = Math.max(1, numberValue(source, 1, "page", "Page", "pageIndex", "PageIndex"));
  const pageSize = Math.max(1, numberValue(source, Math.max(items.length, 1), "pageSize", "PageSize"));
  const total = Math.max(0, numberValue(source, items.length, "total", "Total", "totalCount", "TotalCount"));
  return {
    items,
    total,
    page,
    pageSize,
    totalPages: Math.max(0, numberValue(source, Math.ceil(total / pageSize), "totalPages", "TotalPages")),
  };
}

function normalizePermission(value: unknown): IdentityPermission {
  const source = requiredRecord(value);
  return {
    name: requiredString(source, "name", "Name"),
    displayName: requiredString(source, "displayName", "DisplayName"),
    ...(optionalString(source, "description", "Description") ? { description: optionalString(source, "description", "Description") } : {}),
    category: requiredString(source, "category", "Category"),
    isSystemPermission: booleanValue(source, false, "isSystemPermission", "IsSystemPermission"),
    ...(optionalString(source, "createdAt", "CreatedAt") ? { createdAt: optionalString(source, "createdAt", "CreatedAt") } : {}),
    ...(optionalString(source, "createdBy", "CreatedBy") ? { createdBy: optionalString(source, "createdBy", "CreatedBy") } : {}),
    ...(optionalString(source, "updatedAt", "UpdatedAt") ? { updatedAt: optionalString(source, "updatedAt", "UpdatedAt") } : {}),
    ...(optionalString(source, "updatedBy", "UpdatedBy") ? { updatedBy: optionalString(source, "updatedBy", "UpdatedBy") } : {}),
  };
}

function normalizePermissionCategory(value: unknown): IdentityPermissionCategory {
  const source = requiredRecord(value);
  return {
    category: requiredString(source, "category", "Category"),
    displayName: requiredString(source, "displayName", "DisplayName"),
    ...(optionalString(source, "description", "Description") ? { description: optionalString(source, "description", "Description") } : {}),
    permissions: requiredRecordArray(pick(source, "permissions", "Permissions")).map(normalizePermission),
  };
}

export function normalizeIdentityUsers(value: unknown) {
  return normalizePaged(value, normalizeUser);
}

export function normalizeIdentityUser(value: unknown): IdentityUserDetail {
  return normalizeUser(value);
}

export function normalizeIdentityRoles(value: unknown) {
  return normalizePaged(value, normalizeRole);
}

export function normalizeIdentityRole(value: unknown): IdentityRoleDetail {
  const source = requiredRecord(value);
  return {
    ...normalizeRole(source),
    users: optionalRecordArray(pick(source, "users", "Users")).map(normalizeRoleUser),
    permissions: optionalStringArray(pick(source, "permissions", "Permissions")),
  };
}

export function normalizeIdentityRoleUsers(value: unknown) {
  return normalizePaged(value, normalizeRoleUser);
}

export function normalizeIdentityUserLoginRecords(value: unknown) {
  return normalizePaged(value, (item): IdentityUserLoginRecord => {
    const source = requiredRecord(item);
    return {
      sessionId: requiredString(source, "sessionId", "SessionId", "sessionID", "SessionID"),
      loginAt: stringValue(source, "loginAt", "LoginAt"),
      ...(optionalString(source, "ipAddress", "IpAddress") ? { ipAddress: optionalString(source, "ipAddress", "IpAddress") } : {}),
      ...(optionalString(source, "userAgent", "UserAgent") ? { userAgent: optionalString(source, "userAgent", "UserAgent") } : {}),
      expiresAt: stringValue(source, "expiresAt", "ExpiresAt"),
      isRevoked: booleanValue(source, false, "isRevoked", "IsRevoked"),
      isExpired: booleanValue(source, false, "isExpired", "IsExpired"),
      status: stringValue(source, "status", "Status"),
    };
  });
}

export function normalizeIdentityPermissionCatalog(value: unknown): IdentityPermissionCatalog {
  const source = requiredRecord(value);
  const normalizeAlias = (item: unknown): IdentityPermissionAlias => {
    const record = requiredRecord(item);
    return {
      canonicalCode: requiredString(record, "canonicalCode", "CanonicalCode"),
      aliasCodes: stringArray(pick(record, "aliasCodes", "AliasCodes")),
    };
  };
  const normalizeTemplate = (item: unknown): IdentityRolePermissionTemplate => {
    const record = requiredRecord(item);
    return {
      roleName: requiredString(record, "roleName", "RoleName"),
      permissionCodes: stringArray(pick(record, "permissionCodes", "PermissionCodes")),
    };
  };
  return {
    categories: requiredRecordArray(pick(source, "categories", "Categories")).map(normalizePermissionCategory),
    permissionAliases: requiredRecordArray(pick(source, "permissionAliases", "PermissionAliases")).map(normalizeAlias),
    roleTemplates: requiredRecordArray(pick(source, "roleTemplates", "RoleTemplates")).map(normalizeTemplate),
    superAdminRoleNames: stringArray(pick(source, "superAdminRoleNames", "SuperAdminRoleNames")),
  };
}

function normalizeSysPermission(value: unknown): IdentitySysPermission {
  const source = requiredRecord(value);
  const code = requiredString(source, "code", "Code");
  return {
    id: stringValue(source, "id", "Id") || code,
    code,
    name: stringValue(source, "name", "Name") || code,
    category: stringValue(source, "category", "Category"),
    ...(optionalString(source, "description", "Description") ? { description: optionalString(source, "description", "Description") } : {}),
  };
}

export function normalizeIdentitySysPermissions(value: unknown): IdentitySysPermission[] {
  const normalized = unwrap(value);
  // 后端 data 为 null 时视为空表，与 Web 端 `?? []` 语义一致。
  if (normalized === null || normalized === undefined) return [];
  if (!Array.isArray(normalized)) throw new Error(INVALID_RESPONSE_MESSAGE);
  return normalized.map(normalizeSysPermission);
}

export function normalizeIdentityPermissionRoleCounts(value: unknown): IdentityPermissionRoleCounts {
  const normalized = unwrap(value);
  if (normalized === null || normalized === undefined) return {};
  if (!isRecord(normalized)) throw new Error(INVALID_RESPONSE_MESSAGE);
  const counts: IdentityPermissionRoleCounts = {};
  for (const [code, raw] of Object.entries(normalized)) {
    const count = typeof raw === "string" && raw.trim() ? Number(raw) : raw;
    if (code.trim() && typeof count === "number" && Number.isFinite(count) && count >= 0) counts[code.trim()] = Math.floor(count);
  }
  return counts;
}

export function normalizeIdentityRolePermissionState(value: unknown): IdentityRolePermissionState {
  const source = requiredRecord(value);
  return {
    roleGuid: requiredString(source, "roleGuid", "RoleGuid", "roleGUID", "RoleGUID"),
    roleName: requiredString(source, "roleName", "RoleName"),
    isSuperAdmin: booleanValue(source, false, "isSuperAdmin", "IsSuperAdmin"),
    implicitAllPermissions: booleanValue(source, false, "implicitAllPermissions", "ImplicitAllPermissions"),
    explicitPermissionCodes: stringArray(pick(source, "explicitPermissionCodes", "ExplicitPermissionCodes")),
    effectivePermissionCodes: stringArray(pick(source, "effectivePermissionCodes", "EffectivePermissionCodes")),
  };
}

function normalizeBoolean(value: unknown) {
  const normalized = unwrap(value);
  if (typeof normalized !== "boolean") throw new Error(INVALID_RESPONSE_MESSAGE);
  return normalized;
}

function path(kind: "Users" | "Roles", guid?: string, suffix?: string) {
  if (!guid) return `/${kind}`;
  const normalizedGuid = guid.trim();
  if (!normalizedGuid) throw new Error(`${kind === "Users" ? "User" : "Role"} GUID is required`);
  return `/${kind}/guid/${encodeURIComponent(normalizedGuid)}${suffix ? `/${suffix}` : ""}`;
}

function compactParams<T extends object>(params: T): Partial<T> {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== "")) as Partial<T>;
}

export async function fetchIdentityUsers(query: IdentityUserQuery = {}, actorGuid = "", signal?: AbortSignal) {
  const response = await apiClient.get(path("Users") + "/optimized", {
    ...accountBoundRequestConfig(actorGuid, signal),
    params: compactParams({
      page: query.page,
      pageSize: query.pageSize,
      search: query.search?.trim(),
      roleGuid: query.roleGuid?.trim(),
      storeGuid: query.storeGuid?.trim(),
      isActive: query.isActive,
      sortBy: query.sortBy,
      sortDirection: query.sortDirection,
    }),
  });
  return normalizeIdentityUsers(response.data);
}

export async function fetchIdentityUserDetail(userGuid: string, actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(path("Users", userGuid), accountBoundRequestConfig(actorGuid, signal));
  return normalizeIdentityUser(response.data);
}

const NAME_PART_KEYS = ["givenName", "middleName", "familyName", "nickname"] as const;

/**
 * 姓名拆分字段的请求体：未提供（undefined）的段不发送；字符串去首尾空白后原样发送，
 * 更新时空串表示「清空」、null 表示「不改」。旧后端不认识这些字段会直接忽略，
 * 所以调用方仍须同时发送合成后的 fullName。
 */
function nameParts(input: Partial<Record<(typeof NAME_PART_KEYS)[number], string | null>>) {
  const body: Partial<Record<(typeof NAME_PART_KEYS)[number], string | null>> = {};
  for (const key of NAME_PART_KEYS) {
    const value = input[key];
    if (value === undefined) continue;
    body[key] = value === null ? null : value.trim();
  }
  return body;
}

export async function createIdentityUser(input: IdentityCreateUserInput, actorGuid: string) {
  // 创建时只发送有值的姓名段（空串没有意义），其余字段与此前一致。
  const createdParts = Object.fromEntries(
    Object.entries(nameParts(input)).filter(([, value]) => typeof value === "string" && value !== ""),
  );
  const response = await apiClient.post(path("Users"), {
    username: input.username.trim(),
    email: input.email.trim(),
    password: input.password,
    passwordFormat: input.passwordFormat ?? "raw",
    fullName: input.fullName?.trim() || null,
    ...createdParts,
    isActive: input.isActive ?? true,
    roleGuids: input.roleGuids ?? [],
    storeGuids: input.storeGuids ?? [],
  }, accountBoundRequestConfig(actorGuid));
  return normalizeIdentityUser(response.data);
}

export async function updateIdentityUser(userGuid: string, input: IdentityUpdateUserInput, actorGuid: string) {
  const response = await apiClient.put(path("Users", userGuid), {
    username: input.username.trim(),
    email: input.email.trim(),
    fullName: input.fullName?.trim() || null,
    ...nameParts(input),
    isActive: input.isActive ?? true,
  }, accountBoundRequestConfig(actorGuid));
  return normalizeIdentityUser(response.data);
}

export async function updateIdentityUserPassword(userGuid: string, input: IdentityUpdateUserPasswordInput, actorGuid: string) {
  const response = await apiClient.put(path("Users", userGuid, "password"), {
    newPassword: input.newPassword,
    passwordFormat: input.passwordFormat ?? "raw",
    forcePasswordChange: input.forcePasswordChange ?? false,
  }, accountBoundRequestConfig(actorGuid));
  return normalizeBoolean(response.data);
}

/**
 * PUT /Users/guid/{guid}/login-enabled（Users.Edit）：开关账号「允许登录 Web / App」，与启用 / 停用账号是两回事。
 * 服务端失败 errorCode：SELF_PROFILE_FIELDS_DENIED（不能关闭本人）/ USER_NOT_FOUND / USER_SCOPE_DENIED /
 * HIGH_PRIVILEGE_TARGET_DENIED。4xx 由 axios 抛出（getIdentityAdminErrorMeta 读 errorCode）；
 * 200 + success=false 的业务信封由 normalizeBoolean 抛出并带 code，两种路径调用方都按 errorCode 处理。
 * loginEnabled 必须是布尔值再发送，避免误传 undefined 把账号锁在系统外。
 */
export async function setIdentityUserLoginEnabled(userGuid: string, loginEnabled: boolean, actorGuid: string) {
  if (typeof loginEnabled !== "boolean") throw new Error("loginEnabled must be a boolean");
  const response = await apiClient.put(
    path("Users", userGuid, "login-enabled"),
    { loginEnabled },
    accountBoundRequestConfig(actorGuid),
  );
  return normalizeBoolean(response.data);
}

export async function fetchIdentityUserLoginRecords(userGuid: string, query: IdentityUserLoginRecordQuery = {}, actorGuid = "", signal?: AbortSignal) {
  const response = await apiClient.get(path("Users", userGuid, "login-records"), {
    ...accountBoundRequestConfig(actorGuid, signal),
    params: compactParams({ page: query.page, pageSize: query.pageSize }),
  });
  return normalizeIdentityUserLoginRecords(response.data);
}

export async function fetchIdentityRoles(query: IdentityRoleQuery = {}, actorGuid = "", signal?: AbortSignal) {
  const response = await apiClient.get(path("Roles"), { ...accountBoundRequestConfig(actorGuid, signal), params: compactParams(query) });
  return normalizeIdentityRoles(response.data);
}

export async function fetchIdentityRoleDetail(roleGuid: string, actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(path("Roles", roleGuid), accountBoundRequestConfig(actorGuid, signal));
  return normalizeIdentityRole(response.data);
}

export async function createIdentityRole(input: IdentityCreateRoleInput, actorGuid: string) {
  const response = await apiClient.post(path("Roles"), {
    roleName: input.roleName.trim(),
    description: input.description?.trim() || null,
    isActive: input.isActive ?? true,
    permissions: input.permissions ?? [],
  }, accountBoundRequestConfig(actorGuid));
  return normalizeRole(response.data);
}

export async function updateIdentityRole(roleGuid: string, input: IdentityUpdateRoleInput, actorGuid: string) {
  const response = await apiClient.put(path("Roles", roleGuid), {
    roleName: input.roleName.trim(),
    description: input.description?.trim() || null,
    isActive: input.isActive ?? true,
  }, accountBoundRequestConfig(actorGuid));
  return normalizeIdentityRole(response.data);
}

export async function fetchIdentityRoleUsers(roleGuid: string, query: IdentityRoleQuery = {}, actorGuid = "", signal?: AbortSignal) {
  const response = await apiClient.get(path("Roles", roleGuid, "users"), { ...accountBoundRequestConfig(actorGuid, signal), params: compactParams(query) });
  return normalizeIdentityRoleUsers(response.data);
}

export async function addIdentityRoleUsers(roleGuid: string, userGuids: string[], actorGuid: string) {
  const response = await apiClient.post(path("Roles", roleGuid, "users"), userGuids.map((value) => value.trim()).filter(Boolean), accountBoundRequestConfig(actorGuid));
  return normalizeBoolean(response.data);
}

export async function removeIdentityRoleUser(roleGuid: string, userGuid: string, actorGuid: string) {
  const normalizedUserGuid = userGuid.trim();
  if (!normalizedUserGuid) throw new Error("User GUID is required");
  const response = await apiClient.delete(`${path("Roles", roleGuid, "users")}/${encodeURIComponent(normalizedUserGuid)}`, accountBoundRequestConfig(actorGuid));
  return normalizeBoolean(response.data);
}

export async function fetchIdentityPermissionCatalog(actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get("/Roles/permissions/catalog", accountBoundRequestConfig(actorGuid, signal));
  return normalizeIdentityPermissionCatalog(response.data);
}

export async function fetchIdentityRolePermissionState(roleGuid: string, actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(path("Roles", roleGuid, "permissions/state"), accountBoundRequestConfig(actorGuid, signal));
  return normalizeIdentityRolePermissionState(response.data);
}

export async function saveIdentityRolePermissions(roleGuid: string, permissions: string[], actorGuid: string) {
  const response = await apiClient.post(path("Roles", roleGuid, "permissions"), {
    permissions: Array.from(new Set(permissions.map((value) => value.trim()).filter(Boolean))),
  }, accountBoundRequestConfig(actorGuid));
  return normalizeBoolean(response.data);
}

function permissionPath(code: string, suffix?: string) {
  const normalized = code.trim();
  if (!normalized) throw new Error("Permission code is required");
  return `/Roles/permissions/${encodeURIComponent(normalized)}${suffix ? `/${suffix}` : ""}`;
}

export async function fetchIdentitySysPermissions(actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get("/Roles/sys-permissions", accountBoundRequestConfig(actorGuid, signal));
  return normalizeIdentitySysPermissions(response.data);
}

export async function fetchIdentityPermissionRoleCounts(actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get("/Roles/permissions/role-counts", accountBoundRequestConfig(actorGuid, signal));
  return normalizeIdentityPermissionRoleCounts(response.data);
}

export async function fetchIdentityPermissionRoles(code: string, actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(permissionPath(code, "roles"), accountBoundRequestConfig(actorGuid, signal));
  const normalized = unwrap(response.data);
  if (normalized === null || normalized === undefined) return [] as IdentityRole[];
  if (!Array.isArray(normalized)) throw new Error(INVALID_RESPONSE_MESSAGE);
  return normalized.map(normalizeRole);
}

export async function assignIdentityPermissionRoles(code: string, roleGuids: string[], actorGuid: string) {
  const response = await apiClient.post(
    permissionPath(code, "roles"),
    Array.from(new Set(roleGuids.map((value) => value.trim()).filter(Boolean))),
    accountBoundRequestConfig(actorGuid),
  );
  return normalizeBoolean(response.data);
}

/** 获取被直接授予该权限的用户（不含通过角色继承的用户） */
export async function fetchIdentityPermissionUsers(code: string, actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(permissionPath(code, "users"), accountBoundRequestConfig(actorGuid, signal));
  const normalized = unwrap(response.data);
  if (normalized === null || normalized === undefined) return [] as IdentityRoleUser[];
  if (!Array.isArray(normalized)) throw new Error(INVALID_RESPONSE_MESSAGE);
  return normalized.map(normalizeRoleUser);
}

/** 按增量调整权限的直接授权用户；服务端只处理 add/remove，未提交的用户保持不变。 */
export async function assignIdentityPermissionUsers(code: string, input: IdentityPermissionUserAssignmentInput, actorGuid: string) {
  const normalize = (values: string[]) => Array.from(new Set(values.map((value) => value.trim()).filter(Boolean)));
  const response = await apiClient.post(
    permissionPath(code, "users"),
    { addUserGuids: normalize(input.addUserGuids), removeUserGuids: normalize(input.removeUserGuids) },
    accountBoundRequestConfig(actorGuid),
  );
  return normalizeBoolean(response.data);
}

export async function createIdentitySysPermission(input: IdentityCreateSysPermissionInput, actorGuid: string) {
  const actions = Array.from(new Set((input.actions ?? []).map((value) => value.trim()).filter(Boolean)));
  const response = await apiClient.post("/Roles/permissions", {
    code: input.code.trim(),
    name: input.name.trim(),
    category: input.category.trim(),
    description: input.description?.trim() || null,
    // 服务端以 actions 是否为空区分单个创建与批量生成，空数组必须省略。
    ...(actions.length ? { actions } : {}),
  }, accountBoundRequestConfig(actorGuid));
  return normalizeIdentitySysPermissions(response.data);
}

export async function deleteIdentitySysPermission(code: string, actorGuid: string) {
  const response = await apiClient.delete(permissionPath(code), accountBoundRequestConfig(actorGuid));
  return normalizeBoolean(response.data);
}

export function getIdentityAdminErrorMeta(error: unknown): IdentityAdminErrorMeta {
  if (isAxiosError(error)) {
    const payload = isRecord(error.response?.data) ? error.response.data : null;
    const code = payload ? pick(payload, "errorCode", "ErrorCode", "code", "Code") : undefined;
    return {
      message: error.message || "Request failed",
      ...(typeof error.response?.status === "number" ? { status: error.response.status } : {}),
      ...(typeof code === "string" && code.trim() ? { code: code.trim() } : typeof error.code === "string" ? { code: error.code } : {}),
    };
  }
  const record = isRecord(error) ? error : null;
  const code = record ? pick(record, "code", "Code", "errorCode", "ErrorCode") : undefined;
  return {
    message: error instanceof Error ? error.message : "Request failed",
    ...(typeof code === "string" && code.trim() ? { code: code.trim() } : {}),
  };
}

export function fetchAccessStoreCatalog(actorGuid: string, signal?: AbortSignal) {
  return fetchExistingAccessStoreCatalog(async () => {
    const response = await apiClient.get("/stores/all-by-name", accountBoundRequestConfig(actorGuid, signal));
    return normalizeShopStoresApiResponse(response.data);
  });
}

export function fetchAccessRoleCatalog(actorGuid: string, signal?: AbortSignal) {
  return fetchExistingAccessRoleCatalog(accountBoundRequestConfig(actorGuid, signal));
}

/** 人员列表角色筛选用：等级严格低于当前登录人的角色。 */
export function fetchRoleFilterCatalog(actorGuid: string, signal?: AbortSignal) {
  return fetchExistingBelowActorRoleCatalog(accountBoundRequestConfig(actorGuid, signal));
}

export function fetchUserAccessStores(userGuid: string, actorGuid: string, signal?: AbortSignal) {
  return fetchExistingUserAccessStores(userGuid, accountBoundRequestConfig(actorGuid, signal));
}

export function fetchUserAccessRoles(userGuid: string, actorGuid: string, signal?: AbortSignal) {
  return fetchExistingUserAccessRoles(userGuid, accountBoundRequestConfig(actorGuid, signal));
}

export function fetchUserAccessPermissionAccess(userGuid: string, actorGuid: string, signal?: AbortSignal) {
  return fetchExistingUserAccessPermissionAccess(userGuid, accountBoundRequestConfig(actorGuid, signal));
}

export function assignUserAccessStores(input: AssignUserAccessStoresInput, actorGuid: string) {
  return assignExistingUserAccessStores(input, accountBoundRequestConfig(actorGuid));
}

export function assignUserAccessRoles(input: AssignUserAccessRolesInput, actorGuid: string) {
  return assignExistingUserAccessRoles(input, accountBoundRequestConfig(actorGuid));
}

export function assignUserDirectPermissions(input: AssignUserDirectPermissionsInput, actorGuid: string) {
  return assignExistingUserDirectPermissions(input, accountBoundRequestConfig(actorGuid));
}
