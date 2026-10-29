import { getAllStores } from "@/modules/shop/api";
import type { Store } from "@/modules/shop/types";
import { apiClient } from "@/shared/api/client";
import type { AxiosRequestConfig } from "axios";
import { buildAssignableRoleGuids } from "./access-management";
import type {
  AccessPermission,
  AccessPermissionCategory,
  AccessStoreOption,
  AssignUserAccessRolesInput,
  AssignUserAccessStoresInput,
  AssignUserDirectPermissionsInput,
  CopyPermissionMode,
  CopyUserDirectPermissionsInput,
  UserDirectPermissionCopyResult,
  UserDirectPermissionCopyTarget,
  UserAccessPermissionInheritedSource,
  UserAccessPermissionAccess,
  UserAccessPermissionState,
  UserAccessRole,
  UserAccessStore,
} from "./access-management-types";

const INVALID_RESPONSE_MESSAGE = "Invalid access management response";

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readValue(source: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    if (Object.prototype.hasOwnProperty.call(source, key)) return source[key];
  }
  return undefined;
}

function unwrapAccessEnvelope(value: unknown): unknown {
  if (!isRecord(value)) return value;
  const success = readValue(
    value,
    "success",
    "isSuccess",
    "Success",
    "IsSuccess",
  );
  if (success === false) {
    const message = readValue(value, "message", "Message");
    // apiClient 正常会先抛出失败 envelope；此处保留严格兜底，禁止吞成空数据。
    throw new Error(typeof message === "string" ? message : "Request failed");
  }

  if (success === true) {
    const data = readValue(value, "data", "Data");
    if (data === undefined) throw new Error(INVALID_RESPONSE_MESSAGE);
    return data;
  }

  return value;
}

function readRequiredString(
  source: Record<string, unknown>,
  ...keys: string[]
) {
  const value = readValue(source, ...keys);
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(INVALID_RESPONSE_MESSAGE);
  }
  return value.trim();
}

function readOptionalString(
  source: Record<string, unknown>,
  ...keys: string[]
) {
  const value = readValue(source, ...keys);
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function readOptionalBoolean(
  source: Record<string, unknown>,
  ...keys: string[]
) {
  const value = readValue(source, ...keys);
  return typeof value === "boolean" ? value : undefined;
}

function readOptionalNumber(
  source: Record<string, unknown>,
  ...keys: string[]
) {
  const value = readValue(source, ...keys);
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function readRequiredBoolean(
  source: Record<string, unknown>,
  ...keys: string[]
) {
  const value = readOptionalBoolean(source, ...keys);
  if (value === undefined) throw new Error(INVALID_RESPONSE_MESSAGE);
  return value;
}

function normalizeStringArray(value: unknown) {
  if (!Array.isArray(value) || value.some((item) => typeof item !== "string")) {
    throw new Error(INVALID_RESPONSE_MESSAGE);
  }
  return Array.from(new Set(value.map((item) => item.trim()).filter(Boolean)));
}

function normalizeArray(value: unknown) {
  const unwrapped = unwrapAccessEnvelope(value);
  if (!Array.isArray(unwrapped)) throw new Error(INVALID_RESPONSE_MESSAGE);
  return unwrapped;
}

function normalizeUserAccessStore(value: unknown): UserAccessStore {
  if (!isRecord(value)) throw new Error(INVALID_RESPONSE_MESSAGE);
  const isActive = readOptionalBoolean(value, "isActive", "IsActive");
  const assignedAt = readOptionalString(value, "assignedAt", "AssignedAt");
  return {
    storeGUID: readRequiredString(value, "storeGUID", "StoreGUID"),
    storeCode: readRequiredString(value, "storeCode", "StoreCode"),
    storeName: readRequiredString(value, "storeName", "StoreName"),
    ...(isActive === undefined ? {} : { isActive }),
    isManageable:
      readOptionalBoolean(
        value,
        "isManageable",
        "IsManageable",
        "isPrimary",
        "IsPrimary",
      ) ?? false,
    ...(assignedAt ? { assignedAt } : {}),
  };
}

function normalizeAccessRole(value: unknown): UserAccessRole {
  if (!isRecord(value)) throw new Error(INVALID_RESPONSE_MESSAGE);
  const description = readOptionalString(value, "description", "Description");
  const isActive = readOptionalBoolean(value, "isActive", "IsActive");
  const level = readOptionalNumber(value, "level", "Level");
  return {
    roleGUID: readRequiredString(value, "roleGUID", "RoleGUID"),
    roleName: readRequiredString(value, "roleName", "RoleName"),
    ...(description ? { description } : {}),
    ...(isActive === undefined ? {} : { isActive }),
    ...(level === undefined ? {} : { level }),
  };
}

function normalizeAccessPermission(value: unknown): AccessPermission {
  if (!isRecord(value)) throw new Error(INVALID_RESPONSE_MESSAGE);
  const description = readOptionalString(value, "description", "Description");
  const isSystemPermission = readOptionalBoolean(
    value,
    "isSystemPermission",
    "IsSystemPermission",
  );
  if (isSystemPermission === undefined)
    throw new Error(INVALID_RESPONSE_MESSAGE);

  return {
    name: readRequiredString(value, "name", "Name"),
    displayName: readRequiredString(value, "displayName", "DisplayName"),
    ...(description ? { description } : {}),
    category: readRequiredString(value, "category", "Category"),
    isSystemPermission,
  };
}

function normalizePermissionCategory(value: unknown): AccessPermissionCategory {
  if (!isRecord(value)) throw new Error(INVALID_RESPONSE_MESSAGE);
  const permissions = readValue(value, "permissions", "Permissions");
  if (!Array.isArray(permissions)) throw new Error(INVALID_RESPONSE_MESSAGE);
  const description = readOptionalString(value, "description", "Description");

  return {
    category: readRequiredString(value, "category", "Category"),
    displayName: readRequiredString(value, "displayName", "DisplayName"),
    ...(description ? { description } : {}),
    permissions: permissions.map(normalizeAccessPermission),
  };
}

function normalizeInheritedSource(
  value: unknown,
): UserAccessPermissionInheritedSource {
  if (!isRecord(value)) throw new Error(INVALID_RESPONSE_MESSAGE);
  return {
    roleName: readRequiredString(value, "roleName", "RoleName"),
    permissionCodes: normalizeStringArray(
      readValue(value, "permissionCodes", "PermissionCodes"),
    ),
  };
}

export function normalizeUserAccessStoreList(value: unknown) {
  return normalizeArray(value).map(normalizeUserAccessStore);
}

export function normalizeAccessStoreCatalog(
  value: unknown,
): AccessStoreOption[] {
  return normalizeArray(value).map((item) => {
    if (!isRecord(item)) throw new Error(INVALID_RESPONSE_MESSAGE);
    const isActive = readOptionalBoolean(item, "isActive", "IsActive");
    return {
      storeGUID: readRequiredString(item, "storeGUID", "StoreGUID"),
      storeCode: readRequiredString(item, "storeCode", "StoreCode"),
      storeName: readRequiredString(item, "storeName", "StoreName"),
      ...(isActive === undefined ? {} : { isActive }),
    };
  });
}

export function normalizeAccessRoleList(value: unknown) {
  return normalizeArray(value).map(normalizeAccessRole);
}

export function normalizeAccessPermissionCatalog(value: unknown) {
  return normalizeArray(value).map(normalizePermissionCategory);
}

export function normalizeUserAccessPermissionState(
  value: unknown,
): UserAccessPermissionState {
  const unwrapped = unwrapAccessEnvelope(value);
  if (!isRecord(unwrapped)) throw new Error(INVALID_RESPONSE_MESSAGE);
  const inheritedSources = readValue(
    unwrapped,
    "inheritedSources",
    "InheritedSources",
  );
  if (!Array.isArray(inheritedSources))
    throw new Error(INVALID_RESPONSE_MESSAGE);

  return {
    userGuid: readRequiredString(unwrapped, "userGuid", "UserGuid", "UserGUID"),
    isSuperAdmin: readRequiredBoolean(
      unwrapped,
      "isSuperAdmin",
      "IsSuperAdmin",
    ),
    implicitAllPermissions: readRequiredBoolean(
      unwrapped,
      "implicitAllPermissions",
      "ImplicitAllPermissions",
    ),
    inheritedPermissionCodes: normalizeStringArray(
      readValue(
        unwrapped,
        "inheritedPermissionCodes",
        "InheritedPermissionCodes",
      ),
    ),
    directPermissionCodes: normalizeStringArray(
      readValue(unwrapped, "directPermissionCodes", "DirectPermissionCodes"),
    ),
    effectivePermissionCodes: normalizeStringArray(
      readValue(
        unwrapped,
        "effectivePermissionCodes",
        "EffectivePermissionCodes",
      ),
    ),
    inheritedSources: inheritedSources.map(normalizeInheritedSource),
  };
}

export function normalizeUserAccessPermissionAccess(
  value: unknown,
): UserAccessPermissionAccess {
  const unwrapped = unwrapAccessEnvelope(value);
  if (!isRecord(unwrapped)) throw new Error(INVALID_RESPONSE_MESSAGE);

  return {
    state: normalizeUserAccessPermissionState(
      readValue(unwrapped, "state", "State"),
    ),
    categories: normalizeAccessPermissionCatalog(
      readValue(unwrapped, "categories", "Categories"),
    ),
  };
}

function readRequiredCount(source: Record<string, unknown>, ...keys: string[]) {
  const value = readValue(source, ...keys);
  if (typeof value !== "number" || !Number.isInteger(value) || value < 0) {
    throw new Error(INVALID_RESPONSE_MESSAGE);
  }
  return value;
}

function readCopyMode(source: Record<string, unknown>): CopyPermissionMode {
  const value = readValue(source, "mode", "Mode");
  if (value !== "merge" && value !== "replace") {
    throw new Error(INVALID_RESPONSE_MESSAGE);
  }
  return value;
}

function normalizeCopyTarget(value: unknown): UserDirectPermissionCopyTarget {
  if (!isRecord(value)) throw new Error(INVALID_RESPONSE_MESSAGE);
  return {
    userGuid: readRequiredString(value, "userGuid", "UserGuid", "UserGUID"),
    added: normalizeStringArray(readValue(value, "added", "Added")),
    removed: normalizeStringArray(readValue(value, "removed", "Removed")),
    unchangedCount: readRequiredCount(value, "unchangedCount", "UnchangedCount"),
  };
}

export function normalizeUserDirectPermissionCopyResult(
  value: unknown,
): UserDirectPermissionCopyResult {
  const unwrapped = unwrapAccessEnvelope(value);
  if (!isRecord(unwrapped)) throw new Error(INVALID_RESPONSE_MESSAGE);
  const targets = readValue(unwrapped, "targets", "Targets");
  if (!Array.isArray(targets)) throw new Error(INVALID_RESPONSE_MESSAGE);

  return {
    sourceUserGuid: readRequiredString(
      unwrapped,
      "sourceUserGuid",
      "SourceUserGuid",
      "SourceUserGUID",
    ),
    mode: readCopyMode(unwrapped),
    preview: readRequiredBoolean(unwrapped, "preview", "Preview"),
    sourceDirectPermissionCodes: normalizeStringArray(
      readValue(
        unwrapped,
        "sourceDirectPermissionCodes",
        "SourceDirectPermissionCodes",
      ),
    ),
    transferablePermissionCodes: normalizeStringArray(
      readValue(
        unwrapped,
        "transferablePermissionCodes",
        "TransferablePermissionCodes",
      ),
    ),
    skippedPermissionCodes: normalizeStringArray(
      readValue(unwrapped, "skippedPermissionCodes", "SkippedPermissionCodes"),
    ),
    targets: targets.map(normalizeCopyTarget),
  };
}

function normalizeMutationResult(value: unknown) {
  const unwrapped = unwrapAccessEnvelope(value);
  if (typeof unwrapped !== "boolean") throw new Error(INVALID_RESPONSE_MESSAGE);
  return unwrapped;
}

function buildUserAccessPath(userGuid: string, suffix: string) {
  const normalizedUserGuid = userGuid.trim();
  if (!normalizedUserGuid) throw new Error("User GUID is required");
  return `/Users/guid/${encodeURIComponent(normalizedUserGuid)}/${suffix}`;
}

export async function fetchUserAccessStores(userGuid: string, config?: AxiosRequestConfig) {
  const response = await apiClient.get(buildUserAccessPath(userGuid, "stores"), config);
  return normalizeUserAccessStoreList(response.data);
}

export async function fetchUserAccessRoles(userGuid: string, config?: AxiosRequestConfig) {
  const response = await apiClient.get(buildUserAccessPath(userGuid, "roles"), config);
  return normalizeAccessRoleList(response.data);
}

export async function fetchUserAccessPermissionState(userGuid: string, config?: AxiosRequestConfig) {
  const response = await apiClient.get(
    buildUserAccessPath(userGuid, "permissions/state"),
    config,
  );
  return normalizeUserAccessPermissionState(response.data);
}

export async function fetchUserAccessPermissionAccess(userGuid: string, config?: AxiosRequestConfig) {
  const response = await apiClient.get(
    buildUserAccessPath(userGuid, "access-permissions"),
    config,
  );
  return normalizeUserAccessPermissionAccess(response.data);
}

export async function fetchAccessRoleCatalog(config?: AxiosRequestConfig) {
  const response = await apiClient.get("/Roles/active", config);
  return normalizeAccessRoleList(response.data);
}

/**
 * 人员列表角色筛选专用目录：只含等级严格低于当前登录人的角色（管理员仍是全部）。
 * 与分配角色用的 fetchAccessRoleCatalog 分开：后者对店长仍只给员工角色，不能直接放宽。
 */
export async function fetchBelowActorRoleCatalog(config?: AxiosRequestConfig) {
  const response = await apiClient.get("/Roles/active", {
    ...config,
    params: { ...config?.params, scope: "below-actor" },
  });
  return normalizeAccessRoleList(response.data);
}

export async function fetchAccessPermissionCatalog(config?: AxiosRequestConfig) {
  const response = await apiClient.get("/Roles/permissions", config);
  return normalizeAccessPermissionCatalog(response.data);
}

export async function fetchAccessStoreCatalog(
  loadStores: () => Promise<Store[]> = getAllStores,
) {
  return normalizeAccessStoreCatalog(await loadStores());
}

export async function assignUserAccessStores({
  userGuid,
  assignments,
}: AssignUserAccessStoresInput, config?: AxiosRequestConfig) {
  const body = assignments.map((assignment) => {
    const storeGUID = assignment.storeGUID.trim();
    if (!storeGUID) throw new Error("Store GUID is required");
    // 分店保存契约只发送关联主键和管理标记，禁止附带前端展示字段。
    return { StoreGUID: storeGUID, IsPrimary: assignment.isPrimary };
  });
  const response = await apiClient.post(
    buildUserAccessPath(userGuid, "stores"),
    body,
    config,
  );
  return normalizeMutationResult(response.data);
}

export async function assignUserAccessRoles({
  userGuid,
  roleGuids,
  roleCatalog,
}: AssignUserAccessRolesInput, config?: AxiosRequestConfig) {
  const response = await apiClient.post(
    buildUserAccessPath(userGuid, "roles"),
    {
      // StoreManager/店长/经理由分店管理关系派生，角色接口不得直接写入。
      RoleGuids: buildAssignableRoleGuids(
        roleCatalog,
        normalizeStringArray(roleGuids),
      ),
    },
    config,
  );
  return normalizeMutationResult(response.data);
}

export async function assignUserDirectPermissions({
  userGuid,
  permissions,
}: AssignUserDirectPermissionsInput, config?: AxiosRequestConfig) {
  const response = await apiClient.post(
    buildUserAccessPath(userGuid, "permissions"),
    { permissions: normalizeStringArray(permissions) },
    config,
  );
  return normalizeMutationResult(response.data);
}

async function requestCopyUserDirectPermissions(
  { sourceUserGuid, targetUserGuids, mode }: CopyUserDirectPermissionsInput,
  preview: boolean,
  config?: AxiosRequestConfig,
) {
  if (mode !== "merge" && mode !== "replace") {
    throw new Error("Copy mode is invalid");
  }
  const source = sourceUserGuid.trim();
  // 目标去重并剔除来源自身，避免把权限“复制给自己”。
  const targets = normalizeStringArray(targetUserGuids).filter(
    (guid) => guid.toLowerCase() !== source.toLowerCase(),
  );
  if (!targets.length) throw new Error("Target user GUID is required");
  const response = await apiClient.post(
    buildUserAccessPath(sourceUserGuid, "permissions/copy"),
    // preview=true 只算差异不写库；preview=false 由后端原子写入（全成功或全失败）。
    { targetUserGuids: targets, mode, preview },
    config,
  );
  return normalizeUserDirectPermissionCopyResult(response.data);
}

export function previewCopyUserDirectPermissions(
  input: CopyUserDirectPermissionsInput,
  config?: AxiosRequestConfig,
) {
  return requestCopyUserDirectPermissions(input, true, config);
}

export function copyUserDirectPermissions(
  input: CopyUserDirectPermissionsInput,
  config?: AxiosRequestConfig,
) {
  return requestCopyUserDirectPermissions(input, false, config);
}
